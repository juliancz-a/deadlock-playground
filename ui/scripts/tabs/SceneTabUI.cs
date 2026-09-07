using Godot;
using System;
using System.IO;

public partial class SceneTabUI : VBoxContainer
{
    [ExportCategory("Mode Selector")]
    [Export] private OptionButton _modeOption;

    [ExportCategory("Mode Sub-Panels")]
    [Export] private Control _panelColor;
    [Export] private Control _panelGradient;
    [Export] private Control _panelPattern;
    [Export] private Control _panelImage;
    [Export] private Control _panelStage;

    [ExportCategory("Solid Color Controls")]
    [Export] private ColorPickerButton _solidColorPicker;

    [ExportCategory("Gradient Controls")]
    [Export] private ColorPickerButton _gradTopPicker;
    [Export] private ColorPickerButton _gradBottomPicker;

    [ExportCategory("Pattern Controls")]
    [Export] private OptionButton _patternStyleOption;
    [Export] private ColorPickerButton _patternColorA;
    [Export] private ColorPickerButton _patternColorB;
    [Export] private HSlider _patternScaleSlider;
    [Export] private Label _patternScaleLabel;

    [ExportCategory("Image Controls")]
    [Export] private OptionButton _presetImagesOption;
    [Export] private Button _btnBrowseImage;
    [Export] private Label _lblImagePath;
    [Export] private FileDialog _imageFileDialog;

    [ExportCategory("3D Stage Platform Controls")]
    [Export] private CheckBox _stageToggleCheck;
    [Export] private CheckBox _stageShadowCheck;
    [Export] private ColorPickerButton _stageColorPicker;
    [Export] private OptionButton _stageStyleOption;

    [ExportCategory("Scene References")]
    [Export] private ColorRect _bgColorRect;
    [Export] private TextureRect _bgTextureRect;
    [Export] private Node3D _stagePlatform;
    [Export] private MeshInstance3D _stageMesh;

    public enum BgMode
    {
        Transparent = 0,
        SolidColor = 1,
        Gradient = 2,
        Pattern = 3,
        CustomImage = 4,
        Stage3D = 5
    }

    public enum PatternType
    {
        Chess = 0,
        DiagonalStripes = 1,
        HorizontalStripes = 2,
        Dots = 3,
        Honeycomb = 4,
        StudioGrid = 5
    }

    private BgMode _currentMode = BgMode.CustomImage;
    private PatternType _currentPattern = PatternType.Chess;
    private GradientTexture2D _gradientTex;
    private StandardMaterial3D _stageMaterial;
    private ImageTexture _patternTexture;

    private static readonly (string Name, string ResPath)[] DefaultBackgrounds = new[]
    {
        ("In-Game Background 1", "res://assets/backgrounds/background_00.jpg"),
        ("In-Game Background 2", "res://assets/backgrounds/background_01.jpg"),
        ("In-Game Background 3", "res://assets/backgrounds/background_02.jpg"),
        ("In-Game Background 4", "res://assets/backgrounds/background_03.jpg"),
        ("In-Game Background 5", "res://assets/backgrounds/background_04.jpg"),
    };

    public override void _Ready()
    {
        LinkReferences();
        InitGradient();
        InitStageMaterial();
        PopulateDropdowns();
        ConnectEvents();

        // Default to Image Background on start
        SetMode(BgMode.CustomImage);
    }

    private void LinkReferences()
    {
        _bgColorRect ??= GetNodeOrNull<ColorRect>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/BGCanvas/ColorRect")
                      ?? GetNodeOrNull<ColorRect>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/BGCanvas/ColorRect")
                      ?? GetNodeOrNull<ColorRect>("/root/Main/BGCanvas/ColorRect")
                      ?? GetTree().Root.FindChild("ColorRect", true, false) as ColorRect;

        _bgTextureRect ??= GetNodeOrNull<TextureRect>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/BGCanvas/BackgroundRect")
                        ?? GetNodeOrNull<TextureRect>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/BGCanvas/BackgroundRect")
                        ?? GetNodeOrNull<TextureRect>("/root/Main/BGCanvas/BackgroundRect")
                        ?? GetTree().Root.FindChild("BackgroundRect", true, false) as TextureRect;

        _stagePlatform ??= GetNodeOrNull<Node3D>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/StagePlatform")
                        ?? GetNodeOrNull<Node3D>("/root/Main/StagePlatform")
                        ?? GetTree().Root.FindChild("StagePlatform", true, false) as Node3D;

        _stageMesh ??= GetNodeOrNull<MeshInstance3D>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/StagePlatform/FloorMesh")
                    ?? GetNodeOrNull<MeshInstance3D>("/root/Main/StagePlatform/FloorMesh")
                    ?? GetTree().Root.FindChild("FloorMesh", true, false) as MeshInstance3D;

        if (_imageFileDialog == null)
        {
            _imageFileDialog = new FileDialog
            {
                Name = "BgImageFileDialog",
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Title = "Select Background Image",
                Filters = new[] { "*.png, *.jpg, *.jpeg, *.webp ; Image Files" }
            };
            AddChild(_imageFileDialog);
        }
    }

    private void InitGradient()
    {
        _gradientTex = new GradientTexture2D
        {
            Width = 512,
            Height = 512,
            Fill = GradientTexture2D.FillEnum.Linear,
            FillFrom = new Vector2(0.5f, 0f),
            FillTo = new Vector2(0.5f, 1f)
        };

        var grad = new Gradient();
        grad.SetColor(0, new Color(0.12f, 0.14f, 0.18f));
        grad.SetColor(1, new Color(0.04f, 0.05f, 0.07f));
        _gradientTex.Gradient = grad;
    }

    private void InitStageMaterial()
    {
        _stageMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.15f, 0.16f, 0.18f),
            Roughness = 0.85f
        };

        if (_stageMesh != null)
        {
            _stageMesh.MaterialOverride = _stageMaterial;
        }
    }

    private void PopulateDropdowns()
    {
        if (_modeOption != null)
        {
            _modeOption.Clear();
            _modeOption.AddItem("Transparent (Alpha PNG)", (int)BgMode.Transparent);
            _modeOption.AddItem("Solid Color", (int)BgMode.SolidColor);
            _modeOption.AddItem("2-Color Gradient", (int)BgMode.Gradient);
            _modeOption.AddItem("Pattern (Chess, Stripes, Dots...)", (int)BgMode.Pattern);
            _modeOption.AddItem("Background Images", (int)BgMode.CustomImage);
            _modeOption.AddItem("3D Studio Stage Platform", (int)BgMode.Stage3D);
            _modeOption.Select((int)_currentMode);
        }

        if (_patternStyleOption != null)
        {
            _patternStyleOption.Clear();
            _patternStyleOption.AddItem("Checkerboard / Chess", (int)PatternType.Chess);
            _patternStyleOption.AddItem("Diagonal Stripes", (int)PatternType.DiagonalStripes);
            _patternStyleOption.AddItem("Horizontal Stripes", (int)PatternType.HorizontalStripes);
            _patternStyleOption.AddItem("Polka Dots", (int)PatternType.Dots);
            _patternStyleOption.AddItem("Honeycomb / Hex Grid", (int)PatternType.Honeycomb);
            _patternStyleOption.AddItem("Studio Blueprint Grid", (int)PatternType.StudioGrid);
            _patternStyleOption.Select(0);
        }

        if (_presetImagesOption != null)
        {
            _presetImagesOption.Clear();
            for (int i = 0; i < DefaultBackgrounds.Length; i++)
            {
                _presetImagesOption.AddItem(DefaultBackgrounds[i].Name, i);
            }
            _presetImagesOption.AddItem("Dark Studio Vignette", 5);
            _presetImagesOption.AddItem("Neutral Photography Gray", 6);
            _presetImagesOption.AddItem("Warm Studio Stage", 7);
            _presetImagesOption.AddItem("Cyberpunk Neon Studio", 8);
            _presetImagesOption.Select(0);
        }

        if (_lblImagePath != null && string.IsNullOrEmpty(_lblImagePath.Text))
        {
            _lblImagePath.Text = DefaultBackgrounds[0].Name;
        }

        if (_stageStyleOption != null)
        {
            _stageStyleOption.Clear();
            _stageStyleOption.AddItem("Circular Stage Disc", 0);
            _stageStyleOption.AddItem("Square Studio Floor", 1);
            _stageStyleOption.AddItem("Infinite Floor Plane", 2);
            _stageStyleOption.Select(1);
        }
    }

    private void ConnectEvents()
    {
        if (_modeOption != null)
        {
            _modeOption.ItemSelected += (idx) =>
            {
                SetMode((BgMode)_modeOption.GetItemId((int)idx));
            };
        }

        // Solid Color
        if (_solidColorPicker != null)
        {
            _solidColorPicker.ColorChanged += (c) =>
            {
                if (_bgColorRect != null) _bgColorRect.Color = c;
            };
        }

        // Gradient
        if (_gradTopPicker != null)
        {
            _gradTopPicker.ColorChanged += (c) =>
            {
                _gradientTex?.Gradient?.SetColor(0, c);
            };
        }

        if (_gradBottomPicker != null)
        {
            _gradBottomPicker.ColorChanged += (c) =>
            {
                _gradientTex?.Gradient?.SetColor(1, c);
            };
        }

        // Patterns
        if (_patternStyleOption != null)
        {
            _patternStyleOption.ItemSelected += (idx) =>
            {
                _currentPattern = (PatternType)_patternStyleOption.GetItemId((int)idx);
                UpdatePatternTexture();
            };
        }

        if (_patternColorA != null)
        {
            _patternColorA.ColorChanged += (_) => UpdatePatternTexture();
        }

        if (_patternColorB != null)
        {
            _patternColorB.ColorChanged += (_) => UpdatePatternTexture();
        }

        if (_patternScaleSlider != null)
        {
            _patternScaleSlider.ValueChanged += (v) =>
            {
                if (_patternScaleLabel != null) _patternScaleLabel.Text = $"{v:F0}px";
                UpdatePatternTexture();
            };
        }

        // Image Presets & Browse
        if (_presetImagesOption != null)
        {
            _presetImagesOption.ItemSelected += (idx) =>
            {
                ApplyImagePreset((int)idx);
            };
        }

        if (_btnBrowseImage != null)
        {
            _btnBrowseImage.Pressed += () => _imageFileDialog?.PopupCentered(new Vector2I(700, 500));
        }

        if (_imageFileDialog != null)
        {
            _imageFileDialog.FileSelected += LoadCustomImage;
        }

        // 3D Stage
        if (_stageToggleCheck != null)
        {
            _stageToggleCheck.Toggled += (on) =>
            {
                if (_stagePlatform != null) _stagePlatform.Visible = on;
            };
        }

        if (_stageShadowCheck != null)
        {
            _stageShadowCheck.Toggled += (on) =>
            {
                if (_stageMesh != null)
                {
                    _stageMesh.CastShadow = on ? GeometryInstance3D.ShadowCastingSetting.DoubleSided : GeometryInstance3D.ShadowCastingSetting.Off;
                }
            };
        }

        if (_stageColorPicker != null)
        {
            _stageColorPicker.ColorChanged += (c) =>
            {
                if (_stageMaterial != null) _stageMaterial.AlbedoColor = c;
            };
        }

        if (_stageStyleOption != null)
        {
            _stageStyleOption.ItemSelected += (idx) =>
            {
                SetStageMeshStyle((int)idx);
            };
        }
    }

    public void SetMode(BgMode mode)
    {
        _currentMode = mode;

        if (_panelColor != null) _panelColor.Visible = (mode == BgMode.SolidColor);
        if (_panelGradient != null) _panelGradient.Visible = (mode == BgMode.Gradient);
        if (_panelPattern != null) _panelPattern.Visible = (mode == BgMode.Pattern);
        if (_panelImage != null) _panelImage.Visible = (mode == BgMode.CustomImage);
        if (_panelStage != null) _panelStage.Visible = (mode == BgMode.Stage3D);

        switch (mode)
        {
            case BgMode.Transparent:
                if (_bgColorRect != null) _bgColorRect.Visible = false;
                if (_bgTextureRect != null) _bgTextureRect.Visible = false;
                if (_stagePlatform != null) _stagePlatform.Visible = false;
                break;

            case BgMode.SolidColor:
                if (_bgColorRect != null)
                {
                    _bgColorRect.Visible = true;
                    _bgColorRect.Color = _solidColorPicker?.Color ?? new Color(0.08f, 0.08f, 0.1f);
                }
                if (_bgTextureRect != null) _bgTextureRect.Visible = false;
                if (_stagePlatform != null) _stagePlatform.Visible = false;
                break;

            case BgMode.Gradient:
                if (_bgColorRect != null) _bgColorRect.Visible = false;
                if (_bgTextureRect != null)
                {
                    _bgTextureRect.Visible = true;
                    _bgTextureRect.Texture = _gradientTex;
                    _bgTextureRect.StretchMode = TextureRect.StretchModeEnum.Scale;
                }
                if (_stagePlatform != null) _stagePlatform.Visible = false;
                break;

            case BgMode.Pattern:
                if (_bgColorRect != null) _bgColorRect.Visible = false;
                UpdatePatternTexture();
                if (_stagePlatform != null) _stagePlatform.Visible = false;
                break;

            case BgMode.CustomImage:
                if (_bgColorRect != null) _bgColorRect.Visible = false;
                if (_bgTextureRect != null)
                {
                    _bgTextureRect.Visible = true;
                    _bgTextureRect.StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered;
                    if (_bgTextureRect.Texture == null || _bgTextureRect.Texture == _gradientTex || _bgTextureRect.Texture == _patternTexture)
                    {
                        ApplyImagePreset(_presetImagesOption?.Selected ?? 0);
                    }
                }
                if (_stagePlatform != null) _stagePlatform.Visible = false;
                break;

            case BgMode.Stage3D:
                if (_bgColorRect != null)
                {
                    _bgColorRect.Visible = true;
                    _bgColorRect.Color = new Color(0.06f, 0.07f, 0.09f);
                }
                if (_bgTextureRect != null) _bgTextureRect.Visible = false;
                if (_stagePlatform != null)
                {
                    _stagePlatform.Visible = _stageToggleCheck?.ButtonPressed ?? true;
                }
                break;
        }
    }

    private void UpdatePatternTexture()
    {
        if (_bgTextureRect == null) return;

        Color colA = _patternColorA?.Color ?? new Color(0.1f, 0.11f, 0.14f);
        Color colB = _patternColorB?.Color ?? new Color(0.18f, 0.2f, 0.25f);
        int size = (int)(_patternScaleSlider?.Value ?? 48);
        size = Mathf.Clamp(size, 16, 128);

        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);

        switch (_currentPattern)
        {
            case PatternType.Chess:
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        bool dark = ((x < size / 2) ^ (y < size / 2));
                        img.SetPixel(x, y, dark ? colA : colB);
                    }
                }
                break;

            case PatternType.DiagonalStripes:
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        bool dark = ((x + y) % size) < (size / 2);
                        img.SetPixel(x, y, dark ? colA : colB);
                    }
                }
                break;

            case PatternType.HorizontalStripes:
                for (int y = 0; y < size; y++)
                {
                    bool dark = y < (size / 2);
                    for (int x = 0; x < size; x++)
                    {
                        img.SetPixel(x, y, dark ? colA : colB);
                    }
                }
                break;

            case PatternType.Dots:
                float radius = size * 0.24f;
                Vector2 center = new Vector2(size * 0.5f, size * 0.5f);
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float d = new Vector2(x, y).DistanceTo(center);
                        img.SetPixel(x, y, d <= radius ? colB : colA);
                    }
                }
                break;

            case PatternType.StudioGrid:
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        bool isBorder = (x == 0 || y == 0);
                        img.SetPixel(x, y, isBorder ? colB : colA);
                    }
                }
                break;

            case PatternType.Honeycomb:
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int qx = x % (size / 2);
                        int qy = y % (size / 2);
                        bool edge = (qx == 0 || qy == 0 || qx == qy);
                        img.SetPixel(x, y, edge ? colB : colA);
                    }
                }
                break;
        }

        _patternTexture = ImageTexture.CreateFromImage(img);
        _bgTextureRect.Visible = true;
        _bgTextureRect.Texture = _patternTexture;
        _bgTextureRect.StretchMode = TextureRect.StretchModeEnum.Tile;
    }

    private void ApplyImagePreset(int presetIdx)
    {
        if (_bgTextureRect == null) return;
        _bgTextureRect.StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered;

        if (presetIdx >= 0 && presetIdx < DefaultBackgrounds.Length)
        {
            var (name, resPath) = DefaultBackgrounds[presetIdx];
            var tex = GD.Load<Texture2D>(resPath);
            if (tex != null)
            {
                _bgTextureRect.Texture = tex;
                if (_lblImagePath != null) _lblImagePath.Text = name;
            }
            return;
        }

        switch (presetIdx)
        {
            case 5: // Dark Studio Vignette
                _bgTextureRect.Texture = CreateRadialBackdrop(new Color(0.14f, 0.16f, 0.20f), new Color(0.04f, 0.05f, 0.07f));
                if (_lblImagePath != null) _lblImagePath.Text = "Dark Studio Vignette";
                break;

            case 6: // Neutral Photography Gray
                _bgTextureRect.Texture = CreateRadialBackdrop(new Color(0.24f, 0.25f, 0.27f), new Color(0.09f, 0.09f, 0.10f));
                if (_lblImagePath != null) _lblImagePath.Text = "Neutral Photography Gray";
                break;

            case 7: // Warm Studio Stage
                _bgTextureRect.Texture = CreateRadialBackdrop(new Color(0.25f, 0.18f, 0.16f), new Color(0.07f, 0.05f, 0.04f));
                if (_lblImagePath != null) _lblImagePath.Text = "Warm Studio Stage";
                break;

            case 8: // Cyberpunk Neon Studio
                _bgTextureRect.Texture = CreateRadialBackdrop(new Color(0.18f, 0.13f, 0.28f), new Color(0.05f, 0.04f, 0.09f));
                if (_lblImagePath != null) _lblImagePath.Text = "Cyberpunk Neon Studio";
                break;
        }
    }

    private ImageTexture CreateRadialBackdrop(Color centerColor, Color edgeColor)
    {
        int w = 512;
        int h = 512;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        Vector2 center = new Vector2(w * 0.5f, h * 0.45f);
        float maxDist = center.Length();

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float d = new Vector2(x, y).DistanceTo(center) / maxDist;
                d = Mathf.Clamp(d, 0.0f, 1.0f);
                float smoothD = d * d * (3.0f - 2.0f * d);
                img.SetPixel(x, y, centerColor.Lerp(edgeColor, smoothD));
            }
        }

        return ImageTexture.CreateFromImage(img);
    }

    private void LoadCustomImage(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        var img = Image.LoadFromFile(path);
        if (img != null)
        {
            var tex = ImageTexture.CreateFromImage(img);
            if (_bgTextureRect != null)
            {
                _bgTextureRect.Visible = true;
                _bgTextureRect.Texture = tex;
                _bgTextureRect.StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered;
            }
            if (_lblImagePath != null)
            {
                _lblImagePath.Text = Path.GetFileName(path);
            }
        }
    }

    private void SetStageMeshStyle(int styleIdx)
    {
        if (_stageMesh == null) return;

        switch (styleIdx)
        {
            case 0: // Circular Stage Disc
                _stageMesh.Mesh = new CylinderMesh
                {
                    TopRadius = 4.0f,
                    BottomRadius = 4.0f,
                    Height = 0.15f
                };
                break;

            case 1: // Square Studio Floor
                _stageMesh.Mesh = new BoxMesh
                {
                    Size = new Vector3(10.0f, 0.15f, 10.0f)
                };
                break;

            case 2: // Infinite Plane
                _stageMesh.Mesh = new PlaneMesh
                {
                    Size = new Vector2(50.0f, 50.0f)
                };
                break;
        }

        if (_stageMaterial != null)
        {
            _stageMesh.MaterialOverride = _stageMaterial;
        }
    }
}
