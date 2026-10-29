using Godot;
using System;
using DeadlockPlayground.Painter;
using DeadlockPlayground.UI;

public partial class ColorAdjustmentModalUI : PanelContainer
{
    [Signal] public delegate void AdjustmentAppliedEventHandler();
    [Signal] public delegate void AdjustmentCancelledEventHandler();

    public static bool IsOpen { get; private set; } = false;

    [ExportCategory("Header")]
    [Export] private Control _headerContainer;
    [Export] private Label _lblTitle;
    [Export] private Label _lblSubtitle;
    [Export] private Button _btnClose;

    [ExportCategory("Adjustment Mode Panels")]
    [Export] private VBoxContainer _containerBrightnessContrast;
    [Export] private VBoxContainer _containerHueSaturation;
    [Export] private VBoxContainer _containerColorBalance;
    [Export] private VBoxContainer _containerExposure;
    [Export] private VBoxContainer _containerLevels;
    [Export] private VBoxContainer _containerPosterize;
    [Export] private VBoxContainer _containerThreshold;

    [ExportCategory("Brightness / Contrast Controls")]
    [Export] private HSlider _sliderBrightness;
    [Export] private Label _lblBrightnessVal;
    [Export] private HSlider _sliderContrast;
    [Export] private Label _lblContrastVal;

    [ExportCategory("Hue / Saturation Controls")]
    [Export] private HSlider _sliderHue;
    [Export] private Label _lblHueVal;
    [Export] private HSlider _sliderSaturation;
    [Export] private Label _lblSaturationVal;
    [Export] private HSlider _sliderLightness;
    [Export] private Label _lblLightnessVal;
    [Export] private CheckBox _chkColorize;

    [ExportCategory("Color Balance Controls")]
    [Export] private HSlider _sliderCyanRed;
    [Export] private Label _lblCyanRedVal;
    [Export] private HSlider _sliderMagentaGreen;
    [Export] private Label _lblMagentaGreenVal;
    [Export] private HSlider _sliderYellowBlue;
    [Export] private Label _lblYellowBlueVal;
    [Export] private CheckBox _chkPreserveLuminosity;

    [ExportCategory("Exposure Controls")]
    [Export] private HSlider _sliderExposure;
    [Export] private Label _lblExposureVal;
    [Export] private HSlider _sliderOffset;
    [Export] private Label _lblOffsetVal;
    [Export] private HSlider _sliderGamma;
    [Export] private Label _lblGammaVal;

    [ExportCategory("Levels Controls")]
    [Export] private HSlider _sliderInBlack;
    [Export] private Label _lblInBlackVal;
    [Export] private HSlider _sliderLevelsGamma;
    [Export] private Label _lblLevelsGammaVal;
    [Export] private HSlider _sliderInWhite;
    [Export] private Label _lblInWhiteVal;
    [Export] private HSlider _sliderOutBlack;
    [Export] private Label _lblOutBlackVal;
    [Export] private HSlider _sliderOutWhite;
    [Export] private Label _lblOutWhiteVal;

    [ExportCategory("Posterize Controls")]
    [Export] private HSlider _sliderPosterizeLevels;
    [Export] private Label _lblPosterizeLevelsVal;

    [ExportCategory("Threshold Controls")]
    [Export] private HSlider _sliderThreshold;
    [Export] private Label _lblThresholdVal;

    [ExportCategory("Footer Controls")]
    [Export] private CheckBox _chkPreview;
    [Export] private Button _btnReset;
    [Export] private Button _btnCancel;
    [Export] private Button _btnApply;

    private PaintTabUI _paintTab;
    private SkinLayerManager _layerManager;
    private MeshPainter3D _painter;
    private SkinLayer _activeLayer;

    private AdjustmentType _currentType = AdjustmentType.BrightnessContrast;
    private byte[] _backupBuffer = null;
    private bool _hasSelection = false;
    private byte[] _selectionMaskBuffer = null;
    private int _maskW = 0;
    private int _maskH = 0;

    private bool _isDraggingModal = false;
    private Vector2 _dragMouseStart;
    private Vector2 _dragPositionStart;
    private bool _suppressSliderEvents = false;
    private bool _isSamplingUnderlyingComposite = false;

    public override void _Ready()
    {
        Visible = false;
        IsOpen = false;

        if (_lblTitle != null)
        {
            _lblTitle.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        }
        if (_lblSubtitle != null)
        {
            _lblSubtitle.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        }

        ResolveFallbackNodes();
        WireSignals();
        SetupScrollableProtection();
    }

    private void ResolveFallbackNodes()
    {
        _containerLevels ??= FindChild("LevelsContainer", true, false) as VBoxContainer;
        _sliderInBlack ??= FindChild("InBlackSlider", true, false) as HSlider;
        _lblInBlackVal ??= FindChild("InBlackVal", true, false) as Label;
        _sliderLevelsGamma ??= FindChild("LevelsGammaSlider", true, false) as HSlider;
        _lblLevelsGammaVal ??= FindChild("LevelsGammaVal", true, false) as Label;
        _sliderInWhite ??= FindChild("InWhiteSlider", true, false) as HSlider;
        _lblInWhiteVal ??= FindChild("InWhiteVal", true, false) as Label;
        _sliderOutBlack ??= FindChild("OutBlackSlider", true, false) as HSlider;
        _lblOutBlackVal ??= FindChild("OutBlackVal", true, false) as Label;
        _sliderOutWhite ??= FindChild("OutWhiteSlider", true, false) as HSlider;
        _lblOutWhiteVal ??= FindChild("OutWhiteVal", true, false) as Label;

        _containerPosterize ??= FindChild("PosterizeContainer", true, false) as VBoxContainer;
        _sliderPosterizeLevels ??= FindChild("PosterizeLevelsSlider", true, false) as HSlider;
        _lblPosterizeLevelsVal ??= FindChild("PosterizeLevelsVal", true, false) as Label;

        _containerThreshold ??= FindChild("ThresholdContainer", true, false) as VBoxContainer;
        _sliderThreshold ??= FindChild("ThresholdSlider", true, false) as HSlider;
        _lblThresholdVal ??= FindChild("ThresholdVal", true, false) as Label;
    }

    private void SetupScrollableProtection()
    {
        // Enforce requirement: Scrollable = false on all HSliders so mouse wheel doesn't accidentally move them
        HSlider[] sliders = {
            _sliderBrightness, _sliderContrast,
            _sliderHue, _sliderSaturation, _sliderLightness,
            _sliderCyanRed, _sliderMagentaGreen, _sliderYellowBlue,
            _sliderExposure, _sliderOffset, _sliderGamma,
            _sliderInBlack, _sliderLevelsGamma, _sliderInWhite,
            _sliderOutBlack, _sliderOutWhite,
            _sliderPosterizeLevels, _sliderThreshold
        };

        foreach (var s in sliders)
        {
            if (s != null)
            {
                s.Scrollable = false;
            }
        }
    }

    private void WireSignals()
    {
        if (_btnClose != null)
            _btnClose.Pressed += CancelAndClose;
        if (_btnCancel != null)
            _btnCancel.Pressed += CancelAndClose;
        if (_btnApply != null)
            _btnApply.Pressed += ApplyAndClose;
        if (_btnReset != null)
            _btnReset.Pressed += ResetCurrentSliders;

        if (_chkPreview != null)
            _chkPreview.Toggled += (on) => OnSliderOrOptionChanged();

        if (_headerContainer != null)
        {
            _headerContainer.GuiInput += OnHeaderGuiInput;
        }

        // Sliders & Toggles
        if (_sliderBrightness != null) _sliderBrightness.ValueChanged += (v) => OnSliderOrOptionChanged();
        if (_sliderContrast != null) _sliderContrast.ValueChanged += (v) => OnSliderOrOptionChanged();

        if (_sliderHue != null) _sliderHue.ValueChanged += (v) => OnSliderOrOptionChanged();
        if (_sliderSaturation != null) _sliderSaturation.ValueChanged += (v) => OnSliderOrOptionChanged();
        if (_sliderLightness != null) _sliderLightness.ValueChanged += (v) => OnSliderOrOptionChanged();
        if (_chkColorize != null) _chkColorize.Toggled += (on) => OnSliderOrOptionChanged();

        if (_sliderCyanRed != null) _sliderCyanRed.ValueChanged += (v) => OnSliderOrOptionChanged();
        if (_sliderMagentaGreen != null) _sliderMagentaGreen.ValueChanged += (v) => OnSliderOrOptionChanged();
        if (_sliderYellowBlue != null) _sliderYellowBlue.ValueChanged += (v) => OnSliderOrOptionChanged();
        if (_chkPreserveLuminosity != null) _chkPreserveLuminosity.Toggled += (on) => OnSliderOrOptionChanged();

        if (_sliderExposure != null) _sliderExposure.ValueChanged += (v) => OnSliderOrOptionChanged();
        if (_sliderOffset != null) _sliderOffset.ValueChanged += (v) => OnSliderOrOptionChanged();
        if (_sliderGamma != null) _sliderGamma.ValueChanged += (v) => OnSliderOrOptionChanged();

        if (_sliderInBlack != null) _sliderInBlack.ValueChanged += (v) => OnSliderOrOptionChanged();
        if (_sliderLevelsGamma != null) _sliderLevelsGamma.ValueChanged += (v) => OnSliderOrOptionChanged();
        if (_sliderInWhite != null) _sliderInWhite.ValueChanged += (v) => OnSliderOrOptionChanged();
        if (_sliderOutBlack != null) _sliderOutBlack.ValueChanged += (v) => OnSliderOrOptionChanged();
        if (_sliderOutWhite != null) _sliderOutWhite.ValueChanged += (v) => OnSliderOrOptionChanged();

        if (_sliderPosterizeLevels != null) _sliderPosterizeLevels.ValueChanged += (v) => OnSliderOrOptionChanged();
        if (_sliderThreshold != null) _sliderThreshold.ValueChanged += (v) => OnSliderOrOptionChanged();
    }

    private void OnHeaderGuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    _isDraggingModal = true;
                    _dragMouseStart = mb.GlobalPosition;
                    _dragPositionStart = Position;
                }
                else
                {
                    _isDraggingModal = false;
                }
            }
        }
        else if (@event is InputEventMouseMotion mm && _isDraggingModal)
        {
            ClampPositionToScreen(_dragPositionStart + (mm.GlobalPosition - _dragMouseStart));
        }
    }

    public void Open(AdjustmentType type, PaintTabUI paintTab)
    {
        StudioUIManager.CloseAnyActiveAdjustmentOrFilterModal(this);

        _paintTab = paintTab;
        _layerManager = paintTab?.LayerManager;
        _painter = paintTab?.Painter;
        _activeLayer = _layerManager?.ActiveLayer;

        if (_layerManager == null || _activeLayer == null)
        {
            StudioUIManager.Instance?.ShowToast("No active paint layer selected.");
            return;
        }

        if (!_activeLayer.IsVisible)
        {
            StudioUIManager.Instance?.ShowToast($"Cannot adjust layer '{_activeLayer.Name}' because it is hidden.");
            return;
        }

        if (_activeLayer.IsLocked && !_layerManager.IsBaseTextureActive)
        {
            StudioUIManager.Instance?.ShowToast($"Cannot adjust layer '{_activeLayer.Name}' because it is locked.");
            return;
        }

        int w = _layerManager.CanvasSize.X > 0 ? _layerManager.CanvasSize.X : 2048;
        int h = _layerManager.CanvasSize.Y > 0 ? _layerManager.CanvasSize.Y : 2048;
        int expectedLen = w * h * 8;

        _layerManager.EnsureCpuSynced();
        if (_activeLayer.GpuData == null || _activeLayer.GpuData.Length != expectedLen)
        {
            _activeLayer.DecompressGpuData(expectedLen);
        }
        if (_activeLayer.GpuData == null || _activeLayer.GpuData.Length != expectedLen)
        {
            _activeLayer.GpuData = new byte[expectedLen];
        }

        // Check active selection mask
        _hasSelection = false;
        _selectionMaskBuffer = null;
        var sel = _painter?.SelectionMask ?? _painter?.MagicWandTool?.SelectionMask;
        if (sel != null && sel.HasSelection && sel.UseSelectionMask && sel.Buffer != null)
        {
            _hasSelection = true;
            _selectionMaskBuffer = sel.Buffer;
            _maskW = sel.Width;
            _maskH = sel.Height;
        }

        // Cache pre-adjustment backup buffer
        _backupBuffer = new byte[expectedLen];

        // GIMP / Photopea Merge-Into-Layer Behavior:
        // If target region on active paint layer is completely empty, auto-sample visible pixels underneath!
        _isSamplingUnderlyingComposite = false;
        if (!_layerManager.IsBaseTextureActive && _layerManager.IsLayerRegionEmpty(_activeLayer, _selectionMaskBuffer, _maskW, _maskH))
        {
            _isSamplingUnderlyingComposite = true;
            byte[] comp = _layerManager.SampleVisibleCompositeUnderneath(_layerManager.ActiveLayerIndex);
            Buffer.BlockCopy(comp, 0, _backupBuffer, 0, expectedLen);
        }
        else
        {
            Buffer.BlockCopy(_activeLayer.GpuData, 0, _backupBuffer, 0, expectedLen);
        }

        _currentType = type;
        SetupModalForType(type);

        Visible = true;
        IsOpen = true;

        if (_chkPreview != null)
            _chkPreview.ButtonPressed = true;

        // Position modal nicely in the viewport clamped to screen margins
        CenterOrPositionModal();
        UpdateValueLabels();
        StudioUIManager.Instance?.UpdateModalsState();
    }

    public void ClampPositionToScreen(Vector2? candidatePos = null)
    {
        var vp = GetViewport();
        if (vp == null) return;
        Vector2 vpSize = vp.GetVisibleRect().Size;
        if (vpSize.X <= 0 || vpSize.Y <= 0) vpSize = new Vector2(1920, 1080);

        float maxAllowedW = Mathf.Min(400f, vpSize.X - 40f);
        float maxAllowedH = vpSize.Y * 0.85f;

        float combinedW = GetCombinedMinimumSize().X;
        float modalW = Mathf.Clamp(Mathf.Max(Size.X, combinedW), 320f, maxAllowedW);
        float modalH = Mathf.Max(Size.Y, GetCombinedMinimumSize().Y);

        CustomMinimumSize = new Vector2(Mathf.Min(CustomMinimumSize.X > 0 ? CustomMinimumSize.X : 380f, maxAllowedW), CustomMinimumSize.Y);
        if (modalH > maxAllowedH)
        {
            modalH = maxAllowedH;
            CustomMinimumSize = new Vector2(modalW, modalH);
            Size = new Vector2(modalW, modalH);
        }

        Vector2 target = candidatePos ?? Position;
        float maxX = Mathf.Max(20f, vpSize.X - modalW - 20f);
        float maxY = Mathf.Max(20f, vpSize.Y - modalH - 20f);
        float clampedX = Mathf.Clamp(target.X, 20f, maxX);
        float clampedY = Mathf.Clamp(target.Y, 20f, maxY);
        Position = new Vector2(clampedX, clampedY);
    }

    private void CenterOrPositionModal()
    {
        var vpSize = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);
        float maxAllowedW = Mathf.Min(400f, vpSize.X - 40f);
        float modalW = Mathf.Clamp(CustomMinimumSize.X > 0 ? CustomMinimumSize.X : 380f, 320f, maxAllowedW);
        float modalH = Mathf.Clamp(CustomMinimumSize.Y > 0 ? CustomMinimumSize.Y : 320f, 240f, vpSize.Y * 0.85f);

        float maxX = Mathf.Max(20f, vpSize.X - modalW - 20f);
        float maxY = Mathf.Max(20f, vpSize.Y - modalH - 20f);
        float targetX = Mathf.Clamp(vpSize.X - modalW - 60f, 20f, maxX);
        float targetY = Mathf.Clamp((vpSize.Y - modalH) * 0.35f, 20f, maxY);
        ClampPositionToScreen(new Vector2(targetX, targetY));
    }

    private void SetupModalForType(AdjustmentType type)
    {
        _suppressSliderEvents = true;

        // Configure Containers
        if (_containerBrightnessContrast != null) _containerBrightnessContrast.Visible = (type == AdjustmentType.BrightnessContrast);
        if (_containerHueSaturation != null) _containerHueSaturation.Visible = (type == AdjustmentType.HueSaturation);
        if (_containerColorBalance != null) _containerColorBalance.Visible = (type == AdjustmentType.ColorBalance);
        if (_containerExposure != null) _containerExposure.Visible = (type == AdjustmentType.Exposure);
        if (_containerLevels != null) _containerLevels.Visible = (type == AdjustmentType.Levels);
        if (_containerPosterize != null) _containerPosterize.Visible = (type == AdjustmentType.Posterize);
        if (_containerThreshold != null) _containerThreshold.Visible = (type == AdjustmentType.Threshold);

        string titleText = "Adjustments";
        switch (type)
        {
            case AdjustmentType.BrightnessContrast:
                titleText = "Brightness / Contrast";
                if (_sliderBrightness != null) _sliderBrightness.Value = 0;
                if (_sliderContrast != null) _sliderContrast.Value = 0;
                break;

            case AdjustmentType.HueSaturation:
                titleText = "Hue / Saturation";
                if (_sliderHue != null) _sliderHue.Value = 0;
                if (_sliderSaturation != null) _sliderSaturation.Value = 0;
                if (_sliderLightness != null) _sliderLightness.Value = 0;
                if (_chkColorize != null) _chkColorize.ButtonPressed = false;
                break;

            case AdjustmentType.ColorBalance:
                titleText = "Color Balance";
                if (_sliderCyanRed != null) _sliderCyanRed.Value = 0;
                if (_sliderMagentaGreen != null) _sliderMagentaGreen.Value = 0;
                if (_sliderYellowBlue != null) _sliderYellowBlue.Value = 0;
                if (_chkPreserveLuminosity != null) _chkPreserveLuminosity.ButtonPressed = true;
                break;

            case AdjustmentType.Exposure:
                titleText = "Exposure";
                if (_sliderExposure != null) _sliderExposure.Value = 0;
                if (_sliderOffset != null) _sliderOffset.Value = 0;
                if (_sliderGamma != null) _sliderGamma.Value = 1.0;
                break;

            case AdjustmentType.Levels:
                titleText = "Levels";
                if (_sliderInBlack != null) _sliderInBlack.Value = 0;
                if (_sliderLevelsGamma != null) _sliderLevelsGamma.Value = 1.0;
                if (_sliderInWhite != null) _sliderInWhite.Value = 255;
                if (_sliderOutBlack != null) _sliderOutBlack.Value = 0;
                if (_sliderOutWhite != null) _sliderOutWhite.Value = 255;
                break;

            case AdjustmentType.Posterize:
                titleText = "Posterize";
                if (_sliderPosterizeLevels != null) _sliderPosterizeLevels.Value = 4;
                break;

            case AdjustmentType.Threshold:
                titleText = "Threshold";
                if (_sliderThreshold != null) _sliderThreshold.Value = 128;
                break;
        }

        if (_lblTitle != null)
            _lblTitle.Text = titleText;

        if (_lblSubtitle != null)
        {
            string layerName = _layerManager != null && _layerManager.IsBaseTextureActive ? "BASE TEXTURE" : (_activeLayer != null ? _activeLayer.Name : "Active Layer");
            string modeHint = _isSamplingUnderlyingComposite ? " [Auto-Sampling Visible Underneath]" : "";
            _lblSubtitle.Text = _hasSelection
                ? $"Target: {layerName}{modeHint}  •  [Selection Mask Active]"
                : $"Target: {layerName}{modeHint}";
        }

        _suppressSliderEvents = false;
    }

    private void ResetCurrentSliders()
    {
        _suppressSliderEvents = true;
        switch (_currentType)
        {
            case AdjustmentType.BrightnessContrast:
                if (_sliderBrightness != null) _sliderBrightness.Value = 0;
                if (_sliderContrast != null) _sliderContrast.Value = 0;
                break;

            case AdjustmentType.HueSaturation:
                if (_sliderHue != null) _sliderHue.Value = 0;
                if (_sliderSaturation != null) _sliderSaturation.Value = 0;
                if (_sliderLightness != null) _sliderLightness.Value = 0;
                if (_chkColorize != null) _chkColorize.ButtonPressed = false;
                break;

            case AdjustmentType.ColorBalance:
                if (_sliderCyanRed != null) _sliderCyanRed.Value = 0;
                if (_sliderMagentaGreen != null) _sliderMagentaGreen.Value = 0;
                if (_sliderYellowBlue != null) _sliderYellowBlue.Value = 0;
                if (_chkPreserveLuminosity != null) _chkPreserveLuminosity.ButtonPressed = true;
                break;

            case AdjustmentType.Exposure:
                if (_sliderExposure != null) _sliderExposure.Value = 0;
                if (_sliderOffset != null) _sliderOffset.Value = 0;
                if (_sliderGamma != null) _sliderGamma.Value = 1.0;
                break;

            case AdjustmentType.Levels:
                if (_sliderInBlack != null) _sliderInBlack.Value = 0;
                if (_sliderLevelsGamma != null) _sliderLevelsGamma.Value = 1.0;
                if (_sliderInWhite != null) _sliderInWhite.Value = 255;
                if (_sliderOutBlack != null) _sliderOutBlack.Value = 0;
                if (_sliderOutWhite != null) _sliderOutWhite.Value = 255;
                break;

            case AdjustmentType.Posterize:
                if (_sliderPosterizeLevels != null) _sliderPosterizeLevels.Value = 4;
                break;

            case AdjustmentType.Threshold:
                if (_sliderThreshold != null) _sliderThreshold.Value = 128;
                break;
        }
        _suppressSliderEvents = false;
        UpdateValueLabels();
        OnSliderOrOptionChanged();
    }

    private void UpdateValueLabels()
    {
        if (_lblBrightnessVal != null && _sliderBrightness != null)
            _lblBrightnessVal.Text = $"{_sliderBrightness.Value:+#;-#;0}";
        if (_lblContrastVal != null && _sliderContrast != null)
            _lblContrastVal.Text = $"{_sliderContrast.Value:+#;-#;0}";

        if (_lblHueVal != null && _sliderHue != null)
            _lblHueVal.Text = $"{_sliderHue.Value:+#;-#;0}°";
        if (_lblSaturationVal != null && _sliderSaturation != null)
            _lblSaturationVal.Text = $"{_sliderSaturation.Value:+#;-#;0}";
        if (_lblLightnessVal != null && _sliderLightness != null)
            _lblLightnessVal.Text = $"{_sliderLightness.Value:+#;-#;0}";

        if (_lblCyanRedVal != null && _sliderCyanRed != null)
            _lblCyanRedVal.Text = $"{_sliderCyanRed.Value:+#;-#;0}";
        if (_lblMagentaGreenVal != null && _sliderMagentaGreen != null)
            _lblMagentaGreenVal.Text = $"{_sliderMagentaGreen.Value:+#;-#;0}";
        if (_lblYellowBlueVal != null && _sliderYellowBlue != null)
            _lblYellowBlueVal.Text = $"{_sliderYellowBlue.Value:+#;-#;0}";

        if (_lblExposureVal != null && _sliderExposure != null)
            _lblExposureVal.Text = $"{_sliderExposure.Value:+0.00;-0.00;0.00}";
        if (_lblOffsetVal != null && _sliderOffset != null)
            _lblOffsetVal.Text = $"{_sliderOffset.Value:+0.000;-0.000;0.000}";
        if (_lblGammaVal != null && _sliderGamma != null)
            _lblGammaVal.Text = $"{_sliderGamma.Value:0.00}";

        if (_lblInBlackVal != null && _sliderInBlack != null)
            _lblInBlackVal.Text = $"{Mathf.RoundToInt(_sliderInBlack.Value)}";
        if (_lblLevelsGammaVal != null && _sliderLevelsGamma != null)
            _lblLevelsGammaVal.Text = $"{_sliderLevelsGamma.Value:0.00}";
        if (_lblInWhiteVal != null && _sliderInWhite != null)
            _lblInWhiteVal.Text = $"{Mathf.RoundToInt(_sliderInWhite.Value)}";
        if (_lblOutBlackVal != null && _sliderOutBlack != null)
            _lblOutBlackVal.Text = $"{Mathf.RoundToInt(_sliderOutBlack.Value)}";
        if (_lblOutWhiteVal != null && _sliderOutWhite != null)
            _lblOutWhiteVal.Text = $"{Mathf.RoundToInt(_sliderOutWhite.Value)}";

        if (_lblPosterizeLevelsVal != null && _sliderPosterizeLevels != null)
            _lblPosterizeLevelsVal.Text = $"{Mathf.RoundToInt(_sliderPosterizeLevels.Value)}";

        if (_lblThresholdVal != null && _sliderThreshold != null)
            _lblThresholdVal.Text = $"{Mathf.RoundToInt(_sliderThreshold.Value)}";
    }

    private ColorAdjustmentParams GetCurrentParams()
    {
        var p = ColorAdjustmentParams.Default(_currentType);

        switch (_currentType)
        {
            case AdjustmentType.BrightnessContrast:
                p.Brightness = _sliderBrightness != null ? (float)_sliderBrightness.Value : 0f;
                p.Contrast = _sliderContrast != null ? (float)_sliderContrast.Value : 0f;
                break;

            case AdjustmentType.HueSaturation:
                p.Hue = _sliderHue != null ? (float)_sliderHue.Value : 0f;
                p.Saturation = _sliderSaturation != null ? (float)_sliderSaturation.Value : 0f;
                p.Lightness = _sliderLightness != null ? (float)_sliderLightness.Value : 0f;
                p.Colorize = _chkColorize != null && _chkColorize.ButtonPressed;
                break;

            case AdjustmentType.ColorBalance:
                p.CyanRed = _sliderCyanRed != null ? (float)_sliderCyanRed.Value : 0f;
                p.MagentaGreen = _sliderMagentaGreen != null ? (float)_sliderMagentaGreen.Value : 0f;
                p.YellowBlue = _sliderYellowBlue != null ? (float)_sliderYellowBlue.Value : 0f;
                p.PreserveLuminosity = _chkPreserveLuminosity == null || _chkPreserveLuminosity.ButtonPressed;
                break;

            case AdjustmentType.Exposure:
                p.Exposure = _sliderExposure != null ? (float)_sliderExposure.Value : 0f;
                p.Offset = _sliderOffset != null ? (float)_sliderOffset.Value : 0f;
                p.Gamma = _sliderGamma != null ? (float)_sliderGamma.Value : 1.0f;
                break;

            case AdjustmentType.Levels:
                p.InBlack = _sliderInBlack != null ? (float)_sliderInBlack.Value : 0f;
                p.LevelsGamma = _sliderLevelsGamma != null ? (float)_sliderLevelsGamma.Value : 1.0f;
                p.InWhite = _sliderInWhite != null ? (float)_sliderInWhite.Value : 255f;
                p.OutBlack = _sliderOutBlack != null ? (float)_sliderOutBlack.Value : 0f;
                p.OutWhite = _sliderOutWhite != null ? (float)_sliderOutWhite.Value : 255f;
                break;

            case AdjustmentType.Posterize:
                p.PosterizeLevels = _sliderPosterizeLevels != null ? Mathf.RoundToInt(_sliderPosterizeLevels.Value) : 4;
                break;

            case AdjustmentType.Threshold:
                p.ThresholdValue = _sliderThreshold != null ? (float)_sliderThreshold.Value : 128f;
                break;
        }

        return p;
    }

    private void OnSliderOrOptionChanged()
    {
        if (_suppressSliderEvents || _backupBuffer == null || _activeLayer == null || _layerManager == null) return;
        UpdateValueLabels();

        int w = _layerManager.CanvasSize.X > 0 ? _layerManager.CanvasSize.X : 2048;
        int h = _layerManager.CanvasSize.Y > 0 ? _layerManager.CanvasSize.Y : 2048;

        bool previewEnabled = _chkPreview == null || _chkPreview.ButtonPressed;
        if (previewEnabled)
        {
            var p = GetCurrentParams();
            ColorAdjustmentEngine.ApplyAdjustment(
                _backupBuffer,
                _activeLayer.GpuData,
                w,
                h,
                p,
                _hasSelection ? _selectionMaskBuffer : null,
                _maskW,
                _maskH,
                _isSamplingUnderlyingComposite
            );
        }
        else
        {
            if (_isSamplingUnderlyingComposite)
            {
                Array.Clear(_activeLayer.GpuData, 0, _activeLayer.GpuData.Length);
            }
            else
            {
                // Restore pristine backup buffer when Preview is unchecked
                Buffer.BlockCopy(_backupBuffer, 0, _activeLayer.GpuData, 0, _backupBuffer.Length);
            }
        }

        _layerManager.SyncActiveLayerGpuTexture();
        _layerManager.RecompositeGpuLayers();
        _layerManager.UpdateStandaloneComposite();

        var uvCanvas = GetTree()?.Root?.FindChild("UVCanvasPanel", true, false) as DeadlockPlayground.UI.UVCanvas2DUI;
        uvCanvas?.QueueCanvasRedraw();
    }

    public void CancelAndClose()
    {
        if (_backupBuffer != null && _activeLayer != null && _activeLayer.GpuData != null && _layerManager != null)
        {
            if (_isSamplingUnderlyingComposite)
            {
                Array.Clear(_activeLayer.GpuData, 0, _activeLayer.GpuData.Length);
            }
            else
            {
                // Restore pristine backup buffer back to CPU and GPU
                Buffer.BlockCopy(_backupBuffer, 0, _activeLayer.GpuData, 0, _backupBuffer.Length);
            }
            _layerManager.SyncActiveLayerGpuTexture();
            _layerManager.RecompositeGpuLayers();
            _layerManager.UpdateStandaloneComposite();

            var uvCanvas = GetTree()?.Root?.FindChild("UVCanvasPanel", true, false) as DeadlockPlayground.UI.UVCanvas2DUI;
            uvCanvas?.QueueCanvasRedraw();
        }

        _backupBuffer = null;
        _isDraggingModal = false;
        Visible = false;
        IsOpen = false;

        StudioUIManager.Instance?.UpdateModalsState();
        EmitSignal(SignalName.AdjustmentCancelled);
    }

    public void ApplyAndClose()
    {
        if (_backupBuffer != null && _activeLayer != null && _activeLayer.GpuData != null && _layerManager != null)
        {
            int w = _layerManager.CanvasSize.X > 0 ? _layerManager.CanvasSize.X : 2048;
            int h = _layerManager.CanvasSize.Y > 0 ? _layerManager.CanvasSize.Y : 2048;

            // 1. Temporarily restore pristine backup into active layer so RecordGpuUndoSnapshot captures pristine state!
            if (_isSamplingUnderlyingComposite)
            {
                Array.Clear(_activeLayer.GpuData, 0, _activeLayer.GpuData.Length);
            }
            else
            {
                Buffer.BlockCopy(_backupBuffer, 0, _activeLayer.GpuData, 0, _backupBuffer.Length);
            }
            _layerManager.RecordGpuUndoSnapshot(is2DCpuStroke: true);

            // 2. Compute final adjusted pixels from _backupBuffer into _activeLayer.GpuData
            var p = GetCurrentParams();
            ColorAdjustmentEngine.ApplyAdjustment(
                _backupBuffer,
                _activeLayer.GpuData,
                w,
                h,
                p,
                _hasSelection ? _selectionMaskBuffer : null,
                _maskW,
                _maskH,
                _isSamplingUnderlyingComposite
            );

            // 3. Sync modified CPU buffer to GPU layer texture & composite
            _layerManager.SyncActiveLayerGpuTexture();
            _layerManager.RecompositeGpuLayers();
            _layerManager.UpdateStandaloneComposite();
            _layerManager.SyncGpuStrokeToComposite();
            _layerManager.SetActiveContextHasPaintStrokes(true);

            // 4. Mark material dirty & workspace registration
            if (!string.IsNullOrEmpty(_layerManager.ActiveMaterialKey) && _layerManager.MeshHierarchy != null)
            {
                _layerManager.MeshHierarchy.MarkMaterialDirty(_layerManager.ActiveMaterialKey, _layerManager);
            }
            _paintTab?.RegisterActiveStrokeMetadata();

            // 5. Update Undo/Redo button states in brush palette
            _paintTab?.BrushPalette?.UpdateUndoRedoState(_layerManager.CanUndo, _layerManager.CanRedo);

            // 6. Redraw 2D UV canvas
            var uvCanvas = GetTree()?.Root?.FindChild("UVCanvasPanel", true, false) as DeadlockPlayground.UI.UVCanvas2DUI;
            uvCanvas?.QueueCanvasRedraw();

            StudioUIManager.Instance?.ShowToast("Adjustment Applied");
        }

        _backupBuffer = null;
        _isDraggingModal = false;
        Visible = false;
        IsOpen = false;

        StudioUIManager.Instance?.UpdateModalsState();
        EmitSignal(SignalName.AdjustmentApplied);
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (!Visible || @event is not InputEventKey key || !key.Pressed || key.Echo) return;

        if (key.Keycode == Key.Escape)
        {
            CancelAndClose();
            GetViewport()?.SetInputAsHandled();
            return;
        }

        if (key.Keycode == Key.Enter || key.Keycode == Key.KpEnter)
        {
            ApplyAndClose();
            GetViewport()?.SetInputAsHandled();
            return;
        }
    }
}
