using Godot;
using System;
using System.Collections.Generic;
using DeadlockPlayground.Painter;

namespace DeadlockPlayground.UI
{
    public partial class UVCanvas2DUI : PanelContainer
    {
        [Signal] public delegate void VisibilityToggledEventHandler(bool isVisible);

        public enum WireframeDisplayMode
        {
            Outlines = 0,
            FullMesh = 1,
            Off = 2
        }

        // UI Controls
        private Button _btnClose;
        private Label _lblTitle;
        private Label _lblResolution;
        private Button _btnResetZoom;
        private OptionButton _optBlendMode;
        private Button _btnBaseTex;
        private Button _btnMesh;
        private Button _btnOutline;
        private Button _btnWireOpacity;
        private SubViewportContainer _viewportContainer;
        private SubViewport _subViewport;
        private Control _canvasDrawArea;

        // Subsystems
        private SkinLayerManager _layerManager;
        private MeshPainter3D _painter;
        private HeroMeshHierarchy _meshHierarchy;
        private FloatingBrushPaletteUI _brushPalette;

        // Canvas Navigation State
        private Vector2 _pan = Vector2.Zero;
        private float _zoom = 1.0f;
        private bool _isPanning = false;
        private bool _isPainting = false;
        private Vector2 _lastAtlasPx = Vector2.Zero;
        private Vector2 _hoverMousePos = new Vector2(-9999, -9999);

        // UV Wireframe Cache & Mode (Default: Outlines to preserve performance)
        private WireframeDisplayMode _wireframeMode = WireframeDisplayMode.Outlines;
        private float _wireframeOpacity = 0.5f;
        private bool _showBaseTexture = true;
        private ArrayMesh _cachedBoundaryMesh = null;
        private ArrayMesh _cachedAllWireMesh = null;
        private Rect2 _cachedSubmeshAtlasRect = default;
        private bool _hasSubmeshRect = false;

        private readonly record struct EdgeKey(int X1, int Y1, int X2, int Y2);

        [Export] public float MinPanelWidth { get; set; } = 380f;
        [Export] public float MaxPanelWidth { get; set; } = 850f;
        [Export] public float DefaultPanelWidth { get; set; } = 500f;

        private float _currentPanelWidth = 500f;
        private bool _isResizingWidth = false;
        private float _dragStartMouseX = 0f;
        private float _dragStartWidth = 0f;
        private Control _resizeGrip = null;
        private bool _isGripHovered = false;

        public bool IsResizingWidth => _isResizingWidth;

        private TextureRect _baseTextureRect = null;
        private ShaderMaterial _baseTextureMat = null;
        private TextureRect _paintOverlayRect = null;
        private ShaderMaterial _paintOverlayMat = null;
        private ColorRect _selectionOverlayRect = null;
        private ShaderMaterial _selectionOverlayMat = null;
        private Control _wireframeOverlay = null;

        private bool _hasInitialFit = false;
        private Vector2 _lastDrawAreaSize = Vector2.Zero;

        public override void _Ready()
        {
            _currentPanelWidth = Mathf.Clamp(DefaultPanelWidth, MinPanelWidth, MaxPanelWidth);
            CustomMinimumSize = new Vector2(_currentPanelWidth, 0);
            SizeFlagsHorizontal = SizeFlags.Fill;
            LinkControls();
            ConnectEvents();
            SetupResizeGrip();
            CallDeferred(nameof(EnforcePanelWidthLimits));
        }

        private void SetupResizeGrip()
        {
            _resizeGrip = new Control
            {
                Name = "UVCanvasResizeGrip",
                MouseFilter = Control.MouseFilterEnum.Stop,
                MouseDefaultCursorShape = Control.CursorShape.Hsize,
                ZIndex = 25,
                TopLevel = true,
                Visible = Visible
            };

            _resizeGrip.MouseEntered += () =>
            {
                _isGripHovered = true;
                _resizeGrip?.QueueRedraw();
            };

            _resizeGrip.MouseExited += () =>
            {
                _isGripHovered = false;
                _resizeGrip?.QueueRedraw();
            };

            _resizeGrip.GuiInput += (ev) =>
            {
                if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
                {
                    StartResizing(mb.GlobalPosition.X);
                    _resizeGrip?.QueueRedraw();
                    _resizeGrip?.AcceptEvent();
                }
            };

            _resizeGrip.Draw += () =>
            {
                if (!Visible || _resizeGrip == null) return;
                bool active = _isResizingWidth || _isGripHovered;
                if (active)
                {
                    float x = _resizeGrip.Size.X * 0.5f;
                    Color barColor = _isResizingWidth
                        ? new Color(0.9f, 0.7f, 0.25f, 0.95f)
                        : new Color(0.45f, 0.68f, 0.98f, 0.85f);
                    _resizeGrip.DrawLine(new Vector2(x, 0), new Vector2(x, _resizeGrip.Size.Y), barColor, 2f);
                }
            };

            AddChild(_resizeGrip);
        }

        public void StartResizing(float globalMouseX)
        {
            _isResizingWidth = true;
            _dragStartMouseX = globalMouseX;
            _dragStartWidth = Size.X > 0 ? Size.X : _currentPanelWidth;
        }

        public override void _Input(InputEvent @event)
        {
            if (_isResizingWidth)
            {
                if (@event is InputEventMouseMotion mm)
                {
                    float delta = mm.GlobalPosition.X - _dragStartMouseX;
                    float newWidth = Mathf.Clamp(_dragStartWidth + delta, MinPanelWidth, MaxPanelWidth);
                    SetPanelWidth(newWidth);
                    GetViewport().SetInputAsHandled();
                }
                else if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && !mb.Pressed)
                {
                    _isResizingWidth = false;
                    _resizeGrip?.QueueRedraw();
                    GetViewport().SetInputAsHandled();
                }
            }
        }

        public override void _Process(double delta)
        {
            base._Process(delta);
            if (_resizeGrip != null && Visible)
            {
                UpdateGripTransform();
            }
        }

        private void UpdateGripTransform()
        {
            if (_resizeGrip == null || !IsInsideTree()) return;
            if (!Visible)
            {
                if (_resizeGrip.Visible) _resizeGrip.Visible = false;
                return;
            }

            if (!_resizeGrip.Visible) _resizeGrip.Visible = true;

            Vector2 globalPos = GlobalPosition;
            Vector2 size = Size;
            _resizeGrip.GlobalPosition = new Vector2(globalPos.X + size.X - 5, globalPos.Y);
            _resizeGrip.Size = new Vector2(10, size.Y);
        }

        public void SetPanelWidth(float width)
        {
            _currentPanelWidth = Mathf.Clamp(width, MinPanelWidth, MaxPanelWidth);
            CustomMinimumSize = new Vector2(_currentPanelWidth, 0);

            if (GetParent() is SplitContainer splitParent)
            {
                int[] offsets = splitParent.SplitOffsets;
                if (offsets != null && offsets.Length >= 2)
                {
                    offsets[1] = (int)Mathf.Max(0, _currentPanelWidth - MinPanelWidth);
                    splitParent.SplitOffsets = offsets;
                }
            }

            UpdateGripTransform();
        }

        public void EnforcePanelWidthLimits()
        {
            _currentPanelWidth = Mathf.Clamp(_currentPanelWidth, MinPanelWidth, MaxPanelWidth);
            CustomMinimumSize = new Vector2(_currentPanelWidth, 0);
            SizeFlagsHorizontal = SizeFlags.Fill;

            if (GetParent() is SplitContainer splitParent)
            {
                int[] offsets = splitParent.SplitOffsets;
                if (offsets != null && offsets.Length >= 2)
                {
                    offsets[1] = (int)Mathf.Max(0, _currentPanelWidth - MinPanelWidth);
                    splitParent.SplitOffsets = offsets;
                }
            }

            UpdateGripTransform();
        }

        private void LinkControls()
        {
            _btnClose = GetNodeOrNull<Button>("VBoxContainer/Header/Margin/HBox/BtnClose")
                     ?? FindChild("BtnClose", true, false) as Button;

            _lblTitle = GetNodeOrNull<Label>("VBoxContainer/Header/Margin/HBox/TitleLabel")
                     ?? FindChild("TitleLabel", true, false) as Label;

            _lblResolution = GetNodeOrNull<Label>("VBoxContainer/Header/Margin/HBox/ResolutionBadge")
                          ?? FindChild("ResolutionBadge", true, false) as Label;

            _btnResetZoom = GetNodeOrNull<Button>("VBoxContainer/Header/Margin/HBox/BtnResetZoom")
                         ?? FindChild("BtnResetZoom", true, false) as Button;

            _optBlendMode = GetNodeOrNull<OptionButton>("VBoxContainer/Header/Margin/HBox/OptBlendMode")
                         ?? FindChild("OptBlendMode", true, false) as OptionButton;
            if (_optBlendMode != null)
            {
                _optBlendMode.Clear();
                _optBlendMode.AddItem("Normal", 0);
                _optBlendMode.AddItem("Multiply", 1);
                _optBlendMode.AddItem("Screen", 2);
                _optBlendMode.AddItem("Overlay", 3);
                _optBlendMode.AddItem("Darken", 4);
                _optBlendMode.AddItem("Lighten", 5);
                _optBlendMode.AddItem("Color Dodge", 6);
                _optBlendMode.AddItem("Color (HSL)", 7);
                _optBlendMode.Select(0);

                _optBlendMode.ItemSelected += (idx) =>
                {
                    int mode = _optBlendMode.GetItemId((int)idx);
                    if (_painter != null) _painter.BlendMode = mode;
                    _layerManager?.SetOverlayBlendMode(mode);
                };
            }

            _btnBaseTex = GetNodeOrNull<Button>("VBoxContainer/Header/Margin/HBox/BtnBaseTex")
                       ?? FindChild("BtnBaseTex", true, false) as Button;

            _btnMesh = GetNodeOrNull<Button>("VBoxContainer/Header/Margin/HBox/BtnMesh")
                    ?? FindChild("BtnMesh", true, false) as Button;

            _btnOutline = GetNodeOrNull<Button>("VBoxContainer/Header/Margin/HBox/BtnOutline")
                       ?? FindChild("BtnOutline", true, false) as Button;

            _btnWireOpacity = GetNodeOrNull<Button>("VBoxContainer/Header/Margin/HBox/BtnWireOpacity")
                           ?? FindChild("BtnWireOpacity", true, false) as Button;

            _viewportContainer = GetNodeOrNull<SubViewportContainer>("VBoxContainer/ViewportContainer")
                              ?? FindChild("ViewportContainer", true, false) as SubViewportContainer;
            if (_viewportContainer != null)
            {
                _viewportContainer.TextureFilter = TextureFilterEnum.Nearest;
            }

            _subViewport = GetNodeOrNull<SubViewport>("VBoxContainer/ViewportContainer/UVCanvasViewport")
                        ?? FindChild("UVCanvasViewport", true, false) as SubViewport;

            _canvasDrawArea = GetNodeOrNull<Control>("VBoxContainer/ViewportContainer/UVCanvasViewport/UVCanvasControl")
                           ?? FindChild("UVCanvasControl", true, false) as Control;
            if (_canvasDrawArea != null)
            {
                _canvasDrawArea.TextureFilter = TextureFilterEnum.Nearest;
            }

            if (_canvasDrawArea != null)
            {
                _baseTextureRect = _canvasDrawArea.GetNodeOrNull<TextureRect>("BaseTextureRect");
                if (_baseTextureRect == null)
                {
                    var oldBaseNode = _canvasDrawArea.GetNodeOrNull<Node>("BaseTextureRect");
                    if (oldBaseNode != null)
                    {
                        _canvasDrawArea.RemoveChild(oldBaseNode);
                        oldBaseNode.QueueFree();
                    }

                    _baseTextureRect = new TextureRect
                    {
                        Name = "BaseTextureRect",
                        MouseFilter = Control.MouseFilterEnum.Ignore,
                        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                        StretchMode = TextureRect.StretchModeEnum.Scale,
                        TextureFilter = TextureFilterEnum.Nearest,
                        Visible = false
                    };
                    _canvasDrawArea.AddChild(_baseTextureRect);
                    _canvasDrawArea.MoveChild(_baseTextureRect, 0);
                }
                _baseTextureRect.TextureFilter = TextureFilterEnum.Nearest;

                var baseShader = GD.Load<Shader>("res://assets/shaders/painter/uv_canvas_base.gdshader");
                if (baseShader != null)
                {
                    _baseTextureMat = new ShaderMaterial { Shader = baseShader };
                    _baseTextureRect.Material = _baseTextureMat;
                }

                _paintOverlayRect = _canvasDrawArea.GetNodeOrNull<TextureRect>("PaintOverlayRect");
                if (_paintOverlayRect == null)
                {
                    _paintOverlayRect = new TextureRect
                    {
                        Name = "PaintOverlayRect",
                        MouseFilter = Control.MouseFilterEnum.Ignore,
                        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                        StretchMode = TextureRect.StretchModeEnum.Scale,
                        TextureFilter = TextureFilterEnum.Nearest,
                        Visible = false
                    };
                    _canvasDrawArea.AddChild(_paintOverlayRect);
                }
                _paintOverlayRect.TextureFilter = TextureFilterEnum.Nearest;

                var paintShader = GD.Load<Shader>("res://assets/shaders/painter/uv_canvas_paint.gdshader");
                if (paintShader != null)
                {
                    _paintOverlayMat = new ShaderMaterial { Shader = paintShader };
                    _paintOverlayRect.Material = _paintOverlayMat;
                }

                _selectionOverlayRect = _canvasDrawArea.GetNodeOrNull<ColorRect>("SelectionOverlayRect");
                if (_selectionOverlayRect == null)
                {
                    _selectionOverlayRect = new ColorRect
                    {
                        Name = "SelectionOverlayRect",
                        MouseFilter = Control.MouseFilterEnum.Ignore,
                        Visible = false
                    };
                    _canvasDrawArea.AddChild(_selectionOverlayRect);
                }

                var shader = GD.Load<Shader>("res://assets/shaders/painter/uv_canvas_selection.gdshader");
                if (shader != null)
                {
                    _selectionOverlayMat = new ShaderMaterial { Shader = shader };
                    _selectionOverlayRect.Material = _selectionOverlayMat;
                }

                _wireframeOverlay = _canvasDrawArea.GetNodeOrNull<Control>("WireframeOverlay");
                if (_wireframeOverlay == null)
                {
                    _wireframeOverlay = new Control
                    {
                        Name = "WireframeOverlay",
                        MouseFilter = Control.MouseFilterEnum.Ignore,
                        LayoutMode = 1,
                        AnchorsPreset = (int)Control.LayoutPreset.FullRect
                    };
                    _wireframeOverlay.Draw += () => OnDrawWireframeOverlay(_wireframeOverlay);
                    _canvasDrawArea.AddChild(_wireframeOverlay);
                }
            }

            UpdateWireButtons();
            UpdateBaseTexButton();
        }

        private void ConnectEvents()
        {
            if (GetParent() is SplitContainer splitParent)
            {
                splitParent.Dragged += (offset) =>
                {
                    int[] offsets = splitParent.SplitOffsets;
                    if (offsets != null && offsets.Length >= 2)
                    {
                        float targetWidth = Mathf.Clamp(MinPanelWidth + offsets[1], MinPanelWidth, MaxPanelWidth);
                        _currentPanelWidth = targetWidth;
                        CustomMinimumSize = new Vector2(targetWidth, 0);
                        UpdateGripTransform();
                    }
                };
            }

            Resized += () =>
            {
                UpdateGripTransform();
            };

            if (_btnClose != null)
            {
                _btnClose.Pressed += () => SetVisibleState(false);
            }

            if (_btnResetZoom != null)
            {
                _btnResetZoom.Pressed += FitToView;
            }

            if (_btnBaseTex != null)
            {
                _btnBaseTex.Pressed += OnBaseTexButtonPressed;
            }

            if (_btnMesh != null)
            {
                _btnMesh.Pressed += OnMeshButtonPressed;
            }

            if (_btnOutline != null)
            {
                _btnOutline.Pressed += OnOutlineButtonPressed;
            }

            if (_btnWireOpacity != null)
            {
                _btnWireOpacity.Pressed += CycleWireOpacity;
            }

            if (_canvasDrawArea != null)
            {
                _canvasDrawArea.Draw += () => OnDrawCanvas(_canvasDrawArea);
                _canvasDrawArea.GuiInput += (ev) => OnCanvasGuiInput(ev, _canvasDrawArea);
                _canvasDrawArea.MouseExited += () =>
                {
                    _hoverMousePos = new Vector2(-9999, -9999);
                    _wireframeOverlay?.QueueRedraw();
                    _canvasDrawArea.QueueRedraw();
                };
                _canvasDrawArea.Resized += () =>
                {
                    if (!_hasInitialFit)
                    {
                        FitToView();
                    }
                    else
                    {
                        Vector2 newSize = _canvasDrawArea.Size;
                        if (_lastDrawAreaSize.X > 0 && _lastDrawAreaSize.Y > 0 && newSize.X > 0 && newSize.Y > 0)
                        {
                            Vector2 delta = (newSize - _lastDrawAreaSize) * 0.5f;
                            _pan += delta;
                        }
                        _lastDrawAreaSize = newSize;
                        _wireframeOverlay?.QueueRedraw();
                        _canvasDrawArea.QueueRedraw();
                    }
                };
            }
        }

        public void Setup(SkinLayerManager layerManager, MeshPainter3D painter, HeroMeshHierarchy hierarchy, FloatingBrushPaletteUI palette)
        {
            // Unsubscribe previous
            if (_layerManager != null)
            {
                _layerManager.StackChanged -= OnStackChanged;
            }
            if (_meshHierarchy != null)
            {
                _meshHierarchy.TargetMeshChanged -= OnTargetMeshChanged;
                _meshHierarchy.HierarchyChanged -= OnHierarchyChanged;
            }
            if (_painter != null && _painter.MagicWandTool != null)
            {
                _painter.MagicWandTool.MaskUpdated -= OnMagicWandMaskUpdated;
            }
            if (_brushPalette != null)
            {
                _brushPalette.BlendModeChanged -= OnBrushPaletteBlendModeChanged;
            }

            _layerManager = layerManager;
            _painter = painter;
            _meshHierarchy = hierarchy;
            _brushPalette = palette;

            if (_brushPalette != null)
            {
                _brushPalette.BlendModeChanged += OnBrushPaletteBlendModeChanged;
            }

            if (_painter != null && _painter.MagicWandTool != null)
            {
                _painter.MagicWandTool.MaskUpdated += OnMagicWandMaskUpdated;
            }

            if (_layerManager != null)
            {
                _layerManager.StackChanged += OnStackChanged;
                UpdateResolutionLabel();
            }

            if (_meshHierarchy != null)
            {
                _meshHierarchy.TargetMeshChanged += OnTargetMeshChanged;
                _meshHierarchy.HierarchyChanged += OnHierarchyChanged;
            }

            RebuildWireframe();
            CallDeferred(nameof(EnforcePanelWidthLimits));
            CallDeferred(nameof(FitToView));
        }

        private void OnMagicWandMaskUpdated(bool hasActiveMask)
        {
            if (_selectionOverlayRect != null)
            {
                bool hasSelection = _painter?.MagicWandTool != null && _painter.MagicWandTool.HasSelection;
                bool showPattern = _painter?.MagicWandTool != null && _painter.MagicWandTool.UseSelectionMask;
                _selectionOverlayRect.Visible = hasSelection;
                if (_selectionOverlayMat != null)
                {
                    _selectionOverlayMat.SetShaderParameter("show_stencil_pattern", showPattern);
                }
            }
            _canvasDrawArea?.QueueRedraw();
        }

        public void SetPaintActive(bool active)
        {
            SetVisibleState(active);
        }

        public void SetVisibleState(bool isVisible)
        {
            Visible = isVisible;
            if (_resizeGrip != null)
            {
                _resizeGrip.Visible = isVisible;
                if (isVisible) UpdateGripTransform();
            }
            EmitSignal(SignalName.VisibilityToggled, isVisible);
            if (isVisible)
            {
                EnforcePanelWidthLimits();
                CallDeferred(nameof(EnforcePanelWidthLimits));
                _layerManager?.RebuildBaseAtlasBuffer();
                RebuildWireframe();
                UpdateResolutionLabel();
                if (!_hasInitialFit)
                {
                    CallDeferred(nameof(FitToView));
                }
                _canvasDrawArea?.QueueRedraw();
            }
        }

        public void ToggleVisibility()
        {
            SetVisibleState(!Visible);
        }

        private void OnMeshButtonPressed()
        {
            if (_wireframeMode == WireframeDisplayMode.FullMesh)
            {
                // Clicking active mesh button toggles it off
                _wireframeMode = WireframeDisplayMode.Off;
            }
            else
            {
                _wireframeMode = WireframeDisplayMode.FullMesh;
            }
            UpdateWireButtons();
            _canvasDrawArea?.QueueRedraw();
        }

        private void OnOutlineButtonPressed()
        {
            if (_wireframeMode == WireframeDisplayMode.Outlines)
            {
                // Clicking active outline button toggles it off
                _wireframeMode = WireframeDisplayMode.Off;
            }
            else
            {
                _wireframeMode = WireframeDisplayMode.Outlines;
            }
            UpdateWireButtons();
            _canvasDrawArea?.QueueRedraw();
        }

        private void CycleWireOpacity()
        {
            if (_wireframeOpacity <= 0.35f) _wireframeOpacity = 0.6f;
            else if (_wireframeOpacity <= 0.65f) _wireframeOpacity = 1.0f;
            else _wireframeOpacity = 0.3f;

            UpdateWireButtons();
            _canvasDrawArea?.QueueRedraw();
        }

        private void OnBaseTexButtonPressed()
        {
            _showBaseTexture = _btnBaseTex?.ButtonPressed ?? true;
            UpdateBaseTexButton();
            if (_showBaseTexture)
            {
                _layerManager?.RebuildBaseAtlasBuffer();
            }
            _canvasDrawArea?.QueueRedraw();
        }

        private void UpdateBaseTexButton()
        {
            if (_btnBaseTex != null)
            {
                _btnBaseTex.ButtonPressed = _showBaseTexture;
                _btnBaseTex.Modulate = _showBaseTexture ? new Color(1.0f, 0.88f, 0.45f, 1.0f) : new Color(0.75f, 0.78f, 0.85f, 0.8f);
            }
        }

        private void UpdateWireButtons()
        {
            if (_btnMesh != null)
            {
                bool isMesh = (_wireframeMode == WireframeDisplayMode.FullMesh);
                _btnMesh.ButtonPressed = isMesh;
                _btnMesh.Modulate = isMesh ? new Color(1.0f, 0.88f, 0.45f, 1.0f) : new Color(0.75f, 0.78f, 0.85f, 0.8f);
            }

            if (_btnOutline != null)
            {
                bool isOutline = (_wireframeMode == WireframeDisplayMode.Outlines);
                _btnOutline.ButtonPressed = isOutline;
                _btnOutline.Modulate = isOutline ? new Color(1.0f, 0.88f, 0.45f, 1.0f) : new Color(0.75f, 0.78f, 0.85f, 0.8f);
            }

            if (_btnWireOpacity != null)
            {
                int pct = Mathf.RoundToInt(_wireframeOpacity * 100);
                _btnWireOpacity.Text = $"{pct}%";
                _btnWireOpacity.Visible = (_wireframeMode != WireframeDisplayMode.Off);
            }
        }

        private void OnStackChanged()
        {
            UpdateResolutionLabel();
            if (_optBlendMode != null && _layerManager?.ActiveLayer != null)
            {
                int bMode = (int)_layerManager.ActiveLayer.BlendMode;
                if (bMode >= 0 && bMode < _optBlendMode.ItemCount)
                {
                    _optBlendMode.Select(bMode);
                }
            }
            RebuildWireframe();
            _canvasDrawArea?.QueueRedraw();
        }

        private void OnBrushPaletteBlendModeChanged(int mode)
        {
            if (_optBlendMode != null && mode >= 0 && mode < _optBlendMode.ItemCount)
            {
                _optBlendMode.Select(mode);
            }
        }

        private void OnTargetMeshChanged(MeshInstance3D mesh, int surfaceIndex)
        {
            RebuildWireframe();
            _canvasDrawArea?.QueueRedraw();
        }

        private void OnHierarchyChanged()
        {
            RebuildWireframe();
            _canvasDrawArea?.QueueRedraw();
        }

        public void UpdateResolutionLabel()
        {
            if (_lblResolution != null && _layerManager != null)
            {
                _lblResolution.Text = $"{_layerManager.CanvasSize.X}x{_layerManager.CanvasSize.Y}";
            }
        }

        public void RebuildWireframe()
        {
            _cachedBoundaryMesh = null;
            _cachedAllWireMesh = null;
            _hasSubmeshRect = false;

            if (_meshHierarchy == null) return;
            var active = _meshHierarchy.ActiveTarget;
            if (active == null || active.Mesh == null || !GodotObject.IsInstanceValid(active.Mesh)) return;

            var mesh = active.Mesh.Mesh;
            if (mesh == null) return;

            int surfaceIdx = active.SurfaceIndex;
            if (surfaceIdx < 0 || surfaceIdx >= mesh.GetSurfaceCount()) surfaceIdx = 0;

            // Retrieve atlas layout transform
            Vector2 pos = Vector2.Zero;
            Vector2 size = Vector2.One;
            if (active.Mesh.MaterialOverlay is ShaderMaterial sm)
            {
                var posVar = sm.GetShaderParameter("position_in_atlas");
                var sizeVar = sm.GetShaderParameter("size_in_atlas");
                if (posVar.VariantType == Variant.Type.Vector2 && sizeVar.VariantType == Variant.Type.Vector2)
                {
                    pos = posVar.AsVector2();
                    size = sizeVar.AsVector2();
                }
            }

            int atlasSize = _layerManager?.CanvasSize.X ?? 2048;
            _cachedSubmeshAtlasRect = new Rect2(pos * atlasSize, size * atlasSize);
            _hasSubmeshRect = true;

            var arrays = mesh.SurfaceGetArrays(surfaceIdx);
            if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.TexUV) return;

            var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            if (uvs == null || uvs.Length == 0) return;

            var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();

            var edgeCounts = new Dictionary<EdgeKey, int>();
            var edgeCoords = new Dictionary<EdgeKey, (Vector2 p1, Vector2 p2)>();

            void ProcessEdge(int i1, int i2)
            {
                if (i1 < 0 || i2 < 0 || i1 >= uvs.Length || i2 >= uvs.Length) return;
                Vector2 uv1 = uvs[i1];
                Vector2 uv2 = uvs[i2];

                // 1. Filter seam-crossing wrap jumps (chords across the entire texture)
                if (Mathf.Abs(uv1.X - uv2.X) > 0.35f || Mathf.Abs(uv1.Y - uv2.Y) > 0.35f)
                {
                    return;
                }

                // 2. Quantize UV coordinates (1/8192) to match shared triangle edges
                int qx1 = Mathf.RoundToInt(uv1.X * 8192.0f);
                int qy1 = Mathf.RoundToInt(uv1.Y * 8192.0f);
                int qx2 = Mathf.RoundToInt(uv2.X * 8192.0f);
                int qy2 = Mathf.RoundToInt(uv2.Y * 8192.0f);

                if (qx1 == qx2 && qy1 == qy2) return;

                if (qx1 > qx2 || (qx1 == qx2 && qy1 > qy2))
                {
                    int tx = qx1; int ty = qy1;
                    qx1 = qx2; qy1 = qy2;
                    qx2 = tx; qy2 = ty;
                }

                var key = new EdgeKey(qx1, qy1, qx2, qy2);
                if (edgeCounts.TryGetValue(key, out int count))
                {
                    edgeCounts[key] = count + 1;
                }
                else
                {
                    edgeCounts[key] = 1;
                    Vector2 p1 = (pos + uv1 * size) * atlasSize;
                    Vector2 p2 = (pos + uv2 * size) * atlasSize;
                    edgeCoords[key] = (p1, p2);
                }
            }

            if (indices != null && indices.Length >= 3)
            {
                for (int i = 0; i < indices.Length - 2; i += 3)
                {
                    ProcessEdge(indices[i], indices[i + 1]);
                    ProcessEdge(indices[i + 1], indices[i + 2]);
                    ProcessEdge(indices[i + 2], indices[i]);
                }
            }
            else
            {
                for (int i = 0; i < uvs.Length - 2; i += 3)
                {
                    ProcessEdge(i, i + 1);
                    ProcessEdge(i + 1, i + 2);
                    ProcessEdge(i + 2, i);
                }
            }

            var allPoints = new List<Vector2>(edgeCoords.Count * 2);
            var boundaryPoints = new List<Vector2>();

            foreach (var kvp in edgeCoords)
            {
                var key = kvp.Key;
                var (p1, p2) = kvp.Value;
                allPoints.Add(p1);
                allPoints.Add(p2);

                if (edgeCounts[key] == 1)
                {
                    // Boundary edge of a UV island
                    boundaryPoints.Add(p1);
                    boundaryPoints.Add(p2);
                }
            }

            _cachedAllWireMesh = BuildLineMesh(allPoints);
            _cachedBoundaryMesh = BuildLineMesh(boundaryPoints);
        }

        private static ArrayMesh BuildLineMesh(List<Vector2> points)
        {
            if (points == null || points.Count < 2) return null;

            var meshArrays = new Godot.Collections.Array();
            meshArrays.Resize((int)Mesh.ArrayType.Max);

            Vector3[] verts = new Vector3[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                verts[i] = new Vector3(points[i].X, points[i].Y, 0.0f);
            }
            meshArrays[(int)Mesh.ArrayType.Vertex] = verts;

            var arrayMesh = new ArrayMesh();
            arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, meshArrays);
            return arrayMesh;
        }

        private void OnDrawCanvas(Control canvas)
        {
            int atlasSize = _layerManager?.CanvasSize.X ?? 2048;
            if (atlasSize <= 0) atlasSize = 2048;

            // 1. Background fill
            canvas.DrawRect(new Rect2(Vector2.Zero, canvas.Size), new Color(0.06f, 0.07f, 0.09f, 1.0f));

            // 2. Atlas bounds backdrop
            canvas.DrawRect(new Rect2(_pan, new Vector2(atlasSize * _zoom, atlasSize * _zoom)), new Color(0.12f, 0.14f, 0.18f, 1.0f));

            // 3. Update base diffuse texture with opaque RGB shader (prevents low-alpha crushing)
            var baseTex = _layerManager?.GetBaseTextureResource();
            if (_baseTextureRect != null)
            {
                bool showBase = _showBaseTexture && baseTex != null && GodotObject.IsInstanceValid(baseTex);
                _baseTextureRect.Visible = showBase;
                if (showBase)
                {
                    _baseTextureRect.Position = _pan;
                    _baseTextureRect.Scale = new Vector2(_zoom, _zoom);
                    _baseTextureRect.Size = new Vector2(atlasSize, atlasSize);
                    _baseTextureRect.TextureFilter = TextureFilterEnum.Nearest;
                    _baseTextureRect.Texture = baseTex;
                    if (_baseTextureRect.Material != _baseTextureMat && _baseTextureMat != null)
                    {
                        _baseTextureRect.Material = _baseTextureMat;
                    }
                    _baseTextureMat?.SetShaderParameter("texture_base", baseTex);
                }
            }

            // 4. Update GPU composited atlas texture (paint overlay)
            var atlasTex = _layerManager?.GetAtlasTextureResource();
            if (_paintOverlayRect != null)
            {
                bool showAtlas = atlasTex != null && GodotObject.IsInstanceValid(atlasTex);
                _paintOverlayRect.Visible = showAtlas;
                if (showAtlas)
                {
                    _paintOverlayRect.Position = _pan;
                    _paintOverlayRect.Scale = new Vector2(_zoom, _zoom);
                    _paintOverlayRect.Size = new Vector2(atlasSize, atlasSize);
                    _paintOverlayRect.TextureFilter = TextureFilterEnum.Nearest;
                    _paintOverlayRect.Texture = atlasTex;
                    if (_paintOverlayRect.Material != _paintOverlayMat && _paintOverlayMat != null)
                    {
                        _paintOverlayRect.Material = _paintOverlayMat;
                    }
                }
            }

            // 4b. Update and position Magic Wand selection overlay (marching ants + protective stencil)
            if (_selectionOverlayRect != null)
            {
                bool hasSelection = _painter?.MagicWandTool != null && _painter.MagicWandTool.HasSelection;
                bool showPattern = _painter?.MagicWandTool != null && _painter.MagicWandTool.UseSelectionMask;
                _selectionOverlayRect.Visible = hasSelection;
                if (hasSelection)
                {
                    _selectionOverlayRect.Position = _pan;
                    _selectionOverlayRect.Scale = new Vector2(_zoom, _zoom);
                    _selectionOverlayRect.Size = new Vector2(atlasSize, atlasSize);

                    if (_selectionOverlayMat != null)
                    {
                        var maskTex = _painter.MagicWandTool.MaskTextureResource;
                        _selectionOverlayMat.SetShaderParameter("selection_mask", maskTex);
                        _selectionOverlayMat.SetShaderParameter("atlas_size", new Vector2(atlasSize, atlasSize));
                        _selectionOverlayMat.SetShaderParameter("has_submesh_rect", _hasSubmeshRect);
                        _selectionOverlayMat.SetShaderParameter("show_stencil_pattern", showPattern);
                        if (_hasSubmeshRect)
                        {
                            _selectionOverlayMat.SetShaderParameter("submesh_pos", _cachedSubmeshAtlasRect.Position / atlasSize);
                            _selectionOverlayMat.SetShaderParameter("submesh_size", _cachedSubmeshAtlasRect.Size / atlasSize);
                        }
                    }
                }
            }

            // 5. Trigger wireframe and cursor overlay redraw
            if (_wireframeOverlay != null)
            {
                _wireframeOverlay.Size = canvas.Size;
                _wireframeOverlay.QueueRedraw();
            }
        }

        private void OnDrawWireframeOverlay(Control overlay)
        {
            int atlasSize = _layerManager?.CanvasSize.X ?? 2048;
            if (atlasSize <= 0) atlasSize = 2048;

            // Set 2D transform to local atlas pixel coordinates
            overlay.DrawSetTransform(_pan, 0.0f, new Vector2(_zoom, _zoom));

            // Atlas bounds border
            overlay.DrawRect(new Rect2(Vector2.Zero, new Vector2(atlasSize, atlasSize)), new Color(0.35f, 0.40f, 0.50f, 0.85f), filled: false, width: 2.0f / _zoom);

            // Active submesh highlight border
            if (_hasSubmeshRect)
            {
                overlay.DrawRect(_cachedSubmeshAtlasRect, new Color(0.96f, 0.77f, 0.26f, 0.95f), filled: false, width: 2.0f / _zoom);
            }

            // Static GPU cached UV wireframe overlay (draws in a single GPU draw call)
            if (_wireframeMode != WireframeDisplayMode.Off)
            {
                var mesh = (_wireframeMode == WireframeDisplayMode.Outlines) ? _cachedBoundaryMesh : _cachedAllWireMesh;
                if (mesh != null)
                {
                    float baseAlpha = (_wireframeMode == WireframeDisplayMode.Outlines) ? 0.85f : 0.40f;
                    Color wireColor = new Color(0.85f, 0.88f, 0.95f, baseAlpha * _wireframeOpacity);
                    overlay.DrawMesh(mesh, null, Transform2D.Identity, wireColor);
                }
            }

            // Restore identity transform for screen-space cursor preview
            overlay.DrawSetTransform(Vector2.Zero, 0.0f, Vector2.One);

            // Cursor preview circle (only for radial brush tools: Paint & Erase)
            if (_hoverMousePos.X > -1000 && _painter != null)
            {
                if (_painter.ToolMode == BrushToolMode.Paint || _painter.ToolMode == BrushToolMode.Erase)
                {
                    float brushRadius = _painter.BrushSize * 0.5f * _zoom;
                    Color cursorCol = (_painter.ToolMode == BrushToolMode.Erase)
                        ? new Color(1.0f, 0.35f, 0.35f, 0.85f)
                        : new Color(1.0f, 1.0f, 1.0f, 0.85f);
                    overlay.DrawCircle(_hoverMousePos, brushRadius, cursorCol, filled: false, width: 1.5f, antialiased: true);
                }
            }
        }

        private void OnCanvasGuiInput(InputEvent @event, Control canvas)
        {
            int atlasSize = _layerManager?.CanvasSize.X ?? 2048;

            if (@event is InputEventMouseButton mb)
            {
                if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
                {
                    ZoomAtPoint(mb.Position, 1.15f);
                    canvas.QueueRedraw();
                    canvas.AcceptEvent();
                    GetViewport()?.SetInputAsHandled();
                    return;
                }
                else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
                {
                    ZoomAtPoint(mb.Position, 1.0f / 1.15f);
                    canvas.QueueRedraw();
                    canvas.AcceptEvent();
                    GetViewport()?.SetInputAsHandled();
                    return;
                }
                else if (mb.ButtonIndex == MouseButton.Middle)
                {
                    _isPanning = mb.Pressed;
                    if (_isPanning) _hasInitialFit = true;
                    canvas.AcceptEvent();
                    GetViewport()?.SetInputAsHandled();
                    return;
                }
                else if (mb.ButtonIndex == MouseButton.Left)
                {
                    if (Input.IsKeyPressed(Key.Space))
                    {
                        _isPanning = mb.Pressed;
                        if (_isPanning) _hasInitialFit = true;
                        return;
                    }

                    if (mb.Pressed)
                    {
                        Vector2 atlasPx = ScreenToAtlasPx(mb.Position);
                        if (IsAtlasPxInBounds(atlasPx, atlasSize))
                        {
                            HandleToolClick(atlasPx);
                        }
                    }
                    else
                    {
                        if (_isPainting)
                        {
                            _isPainting = false;
                            _layerManager?.Finish2DStroke();
                            canvas.QueueRedraw();
                        }
                    }
                }
            }
            else if (@event is InputEventMouseMotion mm)
            {
                _hoverMousePos = mm.Position;
                _wireframeOverlay?.QueueRedraw();

                if (_isPanning)
                {
                    _pan += mm.Relative;
                    canvas.QueueRedraw();
                    return;
                }

                if (_isPainting)
                {
                    Vector2 currentAtlasPx = ScreenToAtlasPx(mm.Position);
                    ExecuteToolDrag(_lastAtlasPx, currentAtlasPx);
                    _lastAtlasPx = currentAtlasPx;
                    canvas.QueueRedraw();
                    return;
                }

                canvas.QueueRedraw();
            }
        }

        private void HandleToolClick(Vector2 atlasPx)
        {
            if (_painter == null || _layerManager == null) return;
            int atlasSize = _layerManager.CanvasSize.X;

            var mode = _painter.ToolMode;
            if (mode == BrushToolMode.Paint || mode == BrushToolMode.Erase)
            {
                _isPainting = true;
                _lastAtlasPx = atlasPx;
                _layerManager.RecordInitialSnapshot();
                _layerManager.PaintDab2D(atlasPx, _painter.BrushColor, _painter.BrushSize, _painter.BrushHardness, _painter.BrushFlow, mode == BrushToolMode.Erase, _painter.MagicWandTool);
                _layerManager.RecompositeGpuLayers();
                _canvasDrawArea?.QueueRedraw();
            }
            else if (mode == BrushToolMode.BucketFill)
            {
                Vector2 uv = atlasPx / atlasSize;
                var active = _meshHierarchy?.ActiveTarget;
                if (active != null && active.Mesh != null)
                {
                    Vector2 pos = Vector2.Zero;
                    Vector2 size = Vector2.One;
                    if (active.Mesh.MaterialOverlay is ShaderMaterial sm)
                    {
                        var posVar = sm.GetShaderParameter("position_in_atlas");
                        var sizeVar = sm.GetShaderParameter("size_in_atlas");
                        if (posVar.VariantType == Variant.Type.Vector2 && sizeVar.VariantType == Variant.Type.Vector2)
                        {
                            pos = posVar.AsVector2();
                            size = sizeVar.AsVector2();
                        }
                    }
                    Vector2 submeshUv = (size.X > 0 && size.Y > 0) ? (uv - pos) / size : uv;
                    _layerManager.FillSubmesh(active.Mesh, _painter.BrushColor, submeshUv, _painter.MagicWandTool, _painter.BucketFillTolerance);
                    _canvasDrawArea?.QueueRedraw();
                }
            }
            else if (mode == BrushToolMode.Eyedropper)
            {
                byte[] compBuf = _layerManager.CompositeBuffer;
                if (compBuf != null && compBuf.Length >= atlasSize * atlasSize * 8)
                {
                    int pxX = Mathf.Clamp((int)atlasPx.X, 0, atlasSize - 1);
                    int pxY = Mathf.Clamp((int)atlasPx.Y, 0, atlasSize - 1);
                    int idx = (pxY * atlasSize + pxX) * 4;

                    unsafe
                    {
                        fixed (byte* pBuf = compBuf)
                        {
                            Half* hBuf = (Half*)pBuf;
                            float r = (float)hBuf[idx];
                            float g = (float)hBuf[idx + 1];
                            float b = (float)hBuf[idx + 2];
                            Color sampled = new Color(r, g, b, 1.0f).LinearToSrgb();
                            _painter.BrushColor = sampled;
                            _brushPalette?.SetUniversalColor(sampled);
                        }
                    }
                    _canvasDrawArea?.QueueRedraw();
                }
            }
            else if (mode == BrushToolMode.MagicWand)
            {
                Vector2 uv = atlasPx / atlasSize;
                SubmeshNodeInfo targetSub = null;
                Vector2 submeshUv = uv;

                if (_meshHierarchy != null && _meshHierarchy.Submeshes != null)
                {
                    foreach (var entry in _meshHierarchy.Submeshes)
                    {
                        if (entry.Mesh?.MaterialOverlay is ShaderMaterial sm)
                        {
                            var posVar = sm.GetShaderParameter("position_in_atlas");
                            var sizeVar = sm.GetShaderParameter("size_in_atlas");
                            if (posVar.VariantType == Variant.Type.Vector2 && sizeVar.VariantType == Variant.Type.Vector2)
                            {
                                Vector2 pos = posVar.AsVector2();
                                Vector2 size = sizeVar.AsVector2();
                                Rect2 rect = new Rect2(pos, size);
                                if (rect.HasPoint(uv))
                                {
                                    targetSub = entry;
                                    submeshUv = (size.X > 0 && size.Y > 0) ? (uv - pos) / size : uv;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (targetSub == null)
                {
                    targetSub = _meshHierarchy?.ActiveTarget;
                }

                if (targetSub != null && targetSub.Mesh != null)
                {
                    _meshHierarchy.SelectTarget(targetSub, targetSub.SurfaceIndex);

                    MagicWandCombineMode combineMode = MagicWandCombineMode.Replace;
                    if (Input.IsKeyPressed(Key.Shift)) combineMode = MagicWandCombineMode.Add;
                    else if (Input.IsKeyPressed(Key.Alt)) combineMode = MagicWandCombineMode.Subtract;

                    RaycastHitResult hit = new RaycastHitResult
                    {
                        Hit = true,
                        HitUV = submeshUv,
                        HitSurfaceIndex = targetSub.SurfaceIndex
                    };

                    _painter.ExecuteMagicWandSelection(hit, combineMode);
                    _canvasDrawArea?.QueueRedraw();
                }
            }
            else if (mode == BrushToolMode.Decal)
            {
                Vector2 uv = atlasPx / atlasSize;
                var active = _meshHierarchy?.ActiveTarget;
                if (active != null && active.Mesh != null)
                {
                    Vector2 pos = Vector2.Zero;
                    Vector2 size = Vector2.One;
                    if (active.Mesh.MaterialOverlay is ShaderMaterial sm)
                    {
                        var posVar = sm.GetShaderParameter("position_in_atlas");
                        var sizeVar = sm.GetShaderParameter("size_in_atlas");
                        if (posVar.VariantType == Variant.Type.Vector2 && sizeVar.VariantType == Variant.Type.Vector2)
                        {
                            pos = posVar.AsVector2();
                            size = sizeVar.AsVector2();
                        }
                    }
                    Vector2 submeshUv = (size.X > 0 && size.Y > 0) ? (uv - pos) / size : uv;
                    var decalTex = _painter.DecalStamper?.DecalTexture;
                    if (decalTex != null)
                    {
                        _layerManager.StampDecalToAtlas(submeshUv, decalTex, 0.0f, 1.0f);
                        _canvasDrawArea?.QueueRedraw();
                    }
                }
            }
        }

        private void ExecuteToolDrag(Vector2 fromAtlasPx, Vector2 toAtlasPx)
        {
            if (_painter == null || _layerManager == null) return;
            var mode = _painter.ToolMode;
            if (mode == BrushToolMode.Paint || mode == BrushToolMode.Erase)
            {
                _layerManager.PaintStroke2D(fromAtlasPx, toAtlasPx, _painter.BrushColor, _painter.BrushSize, _painter.BrushHardness, _painter.BrushFlow, mode == BrushToolMode.Erase, _painter.MagicWandTool);
                _layerManager.RecompositeGpuLayers();
            }
        }

        private Vector2 ScreenToAtlasPx(Vector2 screenPos)
        {
            return (screenPos - _pan) / _zoom;
        }

        private bool IsAtlasPxInBounds(Vector2 atlasPx, int atlasSize)
        {
            return atlasPx.X >= 0 && atlasPx.Y >= 0 && atlasPx.X < atlasSize && atlasPx.Y < atlasSize;
        }

        private void ZoomAtPoint(Vector2 mousePos, float factor)
        {
            _hasInitialFit = true;
            Vector2 atlasPosBefore = (mousePos - _pan) / _zoom;
            _zoom = Mathf.Clamp(_zoom * factor, 0.05f, 40.0f);
            _pan = mousePos - atlasPosBefore * _zoom;
        }

        public void FitToView()
        {
            if (_canvasDrawArea == null) return;
            Vector2 viewSize = _canvasDrawArea.Size;
            if (viewSize.X <= 10 || viewSize.Y <= 10)
            {
                CallDeferred(nameof(FitToView));
                return;
            }

            int atlasSize = _layerManager?.CanvasSize.X ?? 2048;
            if (atlasSize <= 0) atlasSize = 2048;

            float margin = 20.0f;
            float availW = Mathf.Max(viewSize.X - margin * 2.0f, 10.0f);
            float availH = Mathf.Max(viewSize.Y - margin * 2.0f, 10.0f);

            _zoom = Mathf.Clamp(Mathf.Min(availW / atlasSize, availH / atlasSize), 0.05f, 40.0f);
            Vector2 scaledSize = new Vector2(atlasSize, atlasSize) * _zoom;
            _pan = (viewSize - scaledSize) * 0.5f;
            _lastDrawAreaSize = viewSize;
            _hasInitialFit = true;
            _wireframeOverlay?.QueueRedraw();
            _canvasDrawArea.QueueRedraw();
        }
    }
}
