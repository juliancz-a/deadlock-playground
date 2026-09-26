using Godot;
using System;
using System.IO;

namespace DeadlockPlayground.Painter
{
    public partial class TextProjector : Node
    {
        [Signal] public delegate void TextPlacedEventHandler(Vector3 worldPos, Vector2 hitUv);
        [Signal] public delegate void TextBakedEventHandler();
        [Signal] public delegate void TextureChangedEventHandler(Texture2D newTexture);

        private string _text = "DEADLOCK";
        public string Text
        {
            get => _text;
            set
            {
                _text = string.IsNullOrEmpty(value) ? "" : (value.Length > 64 ? value[..64] : value);
                UpdateLabel();
            }
        }

        private int _fontSize = 64;
        public int FontSize
        {
            get => _fontSize;
            set
            {
                _fontSize = Mathf.Clamp(value, 16, 128);
                UpdateLabel();
            }
        }

        private Color _textColor = Colors.White;
        public Color TextColor
        {
            get => _textColor;
            set
            {
                _textColor = value;
                UpdateLabel();
            }
        }

        private int _outlineSize = 4;
        public int OutlineSize
        {
            get => _outlineSize;
            set
            {
                _outlineSize = Mathf.Clamp(value, 0, 16);
                UpdateLabel();
            }
        }

        private Color _outlineColor = Colors.Black;
        public Color OutlineColor
        {
            get => _outlineColor;
            set
            {
                _outlineColor = value;
                UpdateLabel();
            }
        }

        public float RotationDegrees { get; set; } = 0.0f;
        public float TextScale { get; set; } = 0.25f;

        private bool _flipH = false;
        public bool FlipH
        {
            get => _flipH;
            set
            {
                if (_flipH != value)
                {
                    _flipH = value;
                    UpdateLabel();
                }
            }
        }

        private bool _flipV = false;
        public bool FlipV
        {
            get => _flipV;
            set
            {
                if (_flipV != value)
                {
                    _flipV = value;
                    UpdateLabel();
                }
            }
        }

        private Font _customFont;
        public Font CustomFont
        {
            get => _customFont;
            set
            {
                _customFont = value;
                UpdateLabel();
            }
        }

        private SubViewport _subViewport;
        private Label _label;
        private SkinLayerManager _layerManager;
        private MeshPainter3D _painter;

        private ImageTexture _renderedTexture;
        private bool _isDirty = true;
        private int _dirtyFrames = 0;

        private Vector3 _currentWorldPos = Vector3.Zero;
        private Vector3 _currentNormal = Vector3.Up;
        private Vector2 _currentHitUv = Vector2.Zero;
        private Vector3 _decalRight = Vector3.Right;
        private Vector3 _decalDown = Vector3.Back;
        private Vector3 _worldTangent = Vector3.Zero;
        private Vector3 _worldBitangent = Vector3.Zero;
        private float _unitsU = 1.0f;
        private float _unitsV = 1.0f;
        private bool _hasPlacement = false;

        public bool HasPlacement => _hasPlacement;

        public Texture2D CurrentTexture => _renderedTexture ?? (Texture2D)_subViewport?.GetTexture();
        public Texture2D TextTexture => CurrentTexture;

        public Font GetConfiguredFont()
        {
            var baseFont = _customFont ?? PlaygroundThemeHelper.GetFontColus();
            if (baseFont == null) return null;

            // Wrap in FontVariation with contextual alternates and ligatures disabled
            // to prevent inverted/rotated 'A' and 'L' in fonts like Colus that substitute lapidary glyphs.
            var variation = new FontVariation();
            variation.BaseFont = baseFont;
            var dict = new Godot.Collections.Dictionary();
            var ts = TextServerManager.GetPrimaryInterface();
            string[] tags = { "calt", "liga", "clig", "dlig", "hlig", "salt", "titl", "swsh", "cswh", "ss01", "ss02", "ss03", "ss04", "ss05", "c2sc", "smcp" };
            foreach (var t in tags)
            {
                dict[t] = 0;
                if (ts != null)
                {
                    dict[(long)ts.NameToTag(t)] = 0;
                }
            }
            variation.OpentypeFeatures = dict;
            return variation;
        }

        public Vector2 GetRenderedTextSize(string textToTest = null)
        {
            string str = textToTest ?? _text;
            if (string.IsNullOrEmpty(str)) return Vector2.Zero;

            var font = GetConfiguredFont();
            if (font == null) return new Vector2(str.Length * _fontSize * 0.6f, _fontSize);

            return font.GetStringSize(str, HorizontalAlignment.Left, -1, _fontSize);
        }

        public bool CanAddMoreText(string proposedText)
        {
            if (string.IsNullOrEmpty(proposedText)) return true;
            int maxW = _subViewport != null ? _subViewport.Size.X - 60 : 960;
            var size = GetRenderedTextSize(proposedText);
            return size.X <= maxW;
        }

        public string ClampTextToFit(string proposedText)
        {
            if (string.IsNullOrEmpty(proposedText)) return "";
            if (CanAddMoreText(proposedText)) return proposedText;

            for (int len = proposedText.Length - 1; len >= 1; len--)
            {
                string sub = proposedText[..len];
                if (CanAddMoreText(sub)) return sub;
            }
            return proposedText[..1];
        }

        public void Setup(Node parentContainer, SkinLayerManager layerManager, MeshPainter3D painter)
        {
            _layerManager = layerManager;
            _painter = painter;

            if (_subViewport == null)
            {
                _subViewport = new SubViewport
                {
                    Name = "TextProjectorViewport",
                    Size = new Vector2I(1024, 512),
                    TransparentBg = true,
                    RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                    Disable3D = true
                };

                _label = new Label
                {
                    Name = "RenderedLabel",
                    Position = Vector2.Zero,
                    Size = new Vector2(1024, 512),
                    CustomMinimumSize = new Vector2(1024, 512),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    AutowrapMode = TextServer.AutowrapMode.Off
                };

                _subViewport.AddChild(_label);
                AddChild(_subViewport);

                if (parentContainer != null && GodotObject.IsInstanceValid(parentContainer) && GetParent() == null)
                {
                    parentContainer.AddChild(this);
                }

                RenderingServer.FramePostDraw += OnFramePostDraw;
                UpdateLabel();
            }
        }

        public override void _ExitTree()
        {
            RenderingServer.FramePostDraw -= OnFramePostDraw;
            base._ExitTree();
        }

        private void OnFramePostDraw()
        {
            if (_isDirty && _subViewport != null)
            {
                var vpTex = _subViewport.GetTexture();
                if (vpTex != null)
                {
                    var img = vpTex.GetImage();
                    if (img != null && !img.IsEmpty())
                    {
                        if (_renderedTexture == null)
                        {
                            _renderedTexture = ImageTexture.CreateFromImage(img);
                        }
                        else
                        {
                            _renderedTexture.SetImage(img);
                        }
                        _isDirty = false;
                        _dirtyFrames = 0;
                        EmitSignal(SignalName.TextureChanged, _renderedTexture);
                    }
                }
            }
        }

        public override void _Process(double delta)
        {
            if (_isDirty && _subViewport != null)
            {
                _subViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
            }
        }

        public void UpdateLabel()
        {
            if (_label == null) return;

            _label.Text = _text;

            var font = GetConfiguredFont();
            if (font != null)
            {
                _label.AddThemeFontOverride("font", font);
            }

            _label.AddThemeFontSizeOverride("font_size", _fontSize);
            _label.AddThemeColorOverride("font_color", _textColor);

            if (_outlineSize > 0)
            {
                _label.AddThemeConstantOverride("outline_size", _outlineSize);
                _label.AddThemeColorOverride("font_outline_color", _outlineColor);
            }
            else
            {
                _label.RemoveThemeConstantOverride("outline_size");
                _label.RemoveThemeColorOverride("font_outline_color");
            }

            _label.PivotOffset = _label.Size * 0.5f;
            _label.Scale = new Vector2(_flipH ? -1.0f : 1.0f, _flipV ? -1.0f : 1.0f);

            _isDirty = true;
            _dirtyFrames = 0;

            if (_subViewport != null)
            {
                _subViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
                EmitSignal(SignalName.TextureChanged, CurrentTexture);
            }
        }

        public bool LoadFontFromFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                GD.PrintErr($"[TextProjector] Font file not found: {filePath}");
                return false;
            }

            try
            {
                var font = new FontFile();
                var err = font.LoadDynamicFont(filePath);
                if (err != Error.Ok)
                {
                    GD.PrintErr($"[TextProjector] Failed to load dynamic font: {err}");
                    return false;
                }

                CustomFont = font;
                GD.Print($"[TextProjector] Loaded font: {Path.GetFileName(filePath)}");
                return true;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[TextProjector] Exception loading font: {ex.Message}");
                return false;
            }
        }

        public void PlaceAt(Vector3 worldPos, Vector3 normal, Vector2 hitUv, Vector3 decalRight = default, Vector3 decalDown = default, Vector3 tangent = default, Vector3 bitangent = default, float unitsU = 1.0f, float unitsV = 1.0f)
        {
            _currentWorldPos = worldPos;
            _currentNormal = normal;
            _currentHitUv = hitUv;
            _decalRight = decalRight;
            _decalDown = decalDown;
            _worldTangent = tangent;
            _worldBitangent = bitangent;
            _unitsU = unitsU;
            _unitsV = unitsV;
            _hasPlacement = true;

            EmitSignal(SignalName.TextPlaced, worldPos, hitUv);
        }

        public bool BakeToActiveLayer()
        {
            if (!_hasPlacement || _layerManager == null || _subViewport == null)
            {
                return false;
            }

            Texture2D tex = _renderedTexture;
            if (tex == null || _isDirty)
            {
                var vpTexture = _subViewport.GetTexture();
                if (vpTexture != null)
                {
                    var img = vpTexture.GetImage();
                    if (img != null && !img.IsEmpty())
                    {
                        if (_renderedTexture == null)
                        {
                            _renderedTexture = ImageTexture.CreateFromImage(img);
                        }
                        else
                        {
                            _renderedTexture.SetImage(img);
                        }
                        tex = _renderedTexture;
                        _isDirty = false;
                        _dirtyFrames = 0;
                    }
                }
            }

            if (tex == null) return false;

            bool success = _layerManager.StampDecalToAtlas(
                _currentHitUv, 
                tex, 
                RotationDegrees, 
                TextScale, 
                _painter?.MagicWandTool,
                _decalRight,
                _decalDown,
                _worldTangent,
                _worldBitangent,
                _unitsU,
                _unitsV);

            if (success)
            {
                GD.Print($"[TextProjector] Baked text '{_text}' to layer at UV {_currentHitUv}");
                EmitSignal(SignalName.TextBaked);
            }

            return success;
        }
    }
}
