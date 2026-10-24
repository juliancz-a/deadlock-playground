using Godot;
using System;
using DeadlockPlayground.Painter;
using DeadlockPlayground.UI;

namespace DeadlockPlayground.UI
{
    public partial class FilterModalUI : PanelContainer
    {
        [Signal] public delegate void FilterAppliedEventHandler();
        [Signal] public delegate void FilterCancelledEventHandler();

        public static bool IsOpen { get; private set; } = false;
        public static FilterParameters? LastFilterParameters { get; set; } = null;

        [ExportCategory("Header")]
        [Export] private Control _headerContainer;
        [Export] private Label _lblTitle;
        [Export] private Label _lblSubtitle;
        [Export] private Button _btnClose;

        [ExportCategory("Filter Control Panels")]
        [Export] private VBoxContainer _panelBlur;
        [Export] private VBoxContainer _panelMotionBlur;
        [Export] private VBoxContainer _panelUnsharpMask;
        [Export] private VBoxContainer _panelAddNoise;
        [Export] private VBoxContainer _panelMedian;
        [Export] private VBoxContainer _panelMosaic;
        [Export] private VBoxContainer _panelHalftone;
        [Export] private VBoxContainer _panelEmboss;
        [Export] private VBoxContainer _panelOilPaint;
        [Export] private VBoxContainer _panelChromaticAberration;
        [Export] private VBoxContainer _panelOutlineStroke;
        [Export] private VBoxContainer _panelHighPass;

        [ExportCategory("Blur Controls")]
        [Export] private HSlider _sliderBlurRadius;
        [Export] private Label _lblBlurRadiusVal;

        [ExportCategory("Motion Blur Controls")]
        [Export] private HSlider _sliderMotionAngle;
        [Export] private Label _lblMotionAngleVal;
        [Export] private HSlider _sliderMotionDistance;
        [Export] private Label _lblMotionDistanceVal;

        [ExportCategory("Unsharp Mask Controls")]
        [Export] private HSlider _sliderUnsharpAmount;
        [Export] private Label _lblUnsharpAmountVal;
        [Export] private HSlider _sliderUnsharpRadius;
        [Export] private Label _lblUnsharpRadiusVal;
        [Export] private HSlider _sliderUnsharpThreshold;
        [Export] private Label _lblUnsharpThresholdVal;

        [ExportCategory("Add Noise Controls")]
        [Export] private HSlider _sliderNoiseAmount;
        [Export] private Label _lblNoiseAmountVal;
        [Export] private CheckBox _chkNoiseMonochromatic;
        [Export] private OptionButton _optNoiseDist;

        [ExportCategory("Median Controls")]
        [Export] private HSlider _sliderMedianRadius;
        [Export] private Label _lblMedianRadiusVal;

        [ExportCategory("Mosaic Controls")]
        [Export] private HSlider _sliderMosaicCellSize;
        [Export] private Label _lblMosaicCellSizeVal;

        [ExportCategory("Halftone Controls")]
        [Export] private HSlider _sliderHalftoneDotSize;
        [Export] private Label _lblHalftoneDotSizeVal;
        [Export] private HSlider _sliderHalftoneLevels;
        [Export] private Label _lblHalftoneLevelsVal;

        [ExportCategory("Emboss Controls")]
        [Export] private HSlider _sliderEmbossAngle;
        [Export] private Label _lblEmbossAngleVal;
        [Export] private HSlider _sliderEmbossHeight;
        [Export] private Label _lblEmbossHeightVal;
        [Export] private HSlider _sliderEmbossAmount;
        [Export] private Label _lblEmbossAmountVal;

        [ExportCategory("Oil Paint Controls")]
        [Export] private HSlider _sliderOilRadius;
        [Export] private Label _lblOilRadiusVal;

        [ExportCategory("Chromatic Aberration Controls")]
        [Export] private HSlider _sliderShiftX;
        [Export] private Label _lblShiftXVal;
        [Export] private HSlider _sliderShiftY;
        [Export] private Label _lblShiftYVal;

        [ExportCategory("Outline Stroke Controls")]
        [Export] private HSlider _sliderOutlineThickness;
        [Export] private Label _lblOutlineThicknessVal;
        [Export] private ColorPickerButton _colorPickerOutline;
        [Export] private OptionButton _optOutlinePos;

        [ExportCategory("High Pass Controls")]
        [Export] private HSlider _sliderHighPassRadius;
        [Export] private Label _lblHighPassRadiusVal;

        [ExportCategory("Footer Controls")]
        [Export] private CheckBox _chkPreview;
        [Export] private Button _btnReset;
        [Export] private Button _btnCancel;
        [Export] private Button _btnApply;

        private PaintTabUI _paintTab;
        private SkinLayerManager _layerManager;
        private MeshPainter3D _painter;
        private SkinLayer _activeLayer;

        private FilterType _currentType = FilterType.GaussianBlur;
        private byte[] _backupBuffer = null;
        private bool _hasSelection = false;
        private byte[] _selectionMaskBuffer = null;
        private int _maskW = 0;
        private int _maskH = 0;

        private bool _isDraggingModal = false;
        private Vector2 _dragMouseStart;
        private Vector2 _dragPositionStart;
        private bool _isSamplingUnderlyingComposite = false;
        private bool _suppressEvents = false;

        public override void _Ready()
        {
            Visible = false;
            IsOpen = false;
            ClipContents = true;

            WireSignals();
            SetupScrollableProtection();
            InitDropdowns();
        }

        private void SetupScrollableProtection()
        {
            HSlider[] sliders = {
                _sliderBlurRadius, _sliderMotionAngle, _sliderMotionDistance,
                _sliderUnsharpAmount, _sliderUnsharpRadius, _sliderUnsharpThreshold,
                _sliderNoiseAmount, _sliderMedianRadius, _sliderMosaicCellSize,
                _sliderHalftoneDotSize, _sliderHalftoneLevels,
                _sliderEmbossAngle, _sliderEmbossHeight, _sliderEmbossAmount,
                _sliderOilRadius, _sliderShiftX, _sliderShiftY,
                _sliderOutlineThickness, _sliderHighPassRadius
            };

            foreach (var s in sliders)
            {
                if (s != null) s.Scrollable = false;
            }
        }

        private void InitDropdowns()
        {
            if (_optNoiseDist != null)
            {
                _optNoiseDist.Clear();
                _optNoiseDist.AddItem("Uniform", 0);
                _optNoiseDist.AddItem("Gaussian", 1);
                _optNoiseDist.Selected = 0;
            }

            if (_optOutlinePos != null)
            {
                _optOutlinePos.Clear();
                _optOutlinePos.AddItem("Outside", 0);
                _optOutlinePos.AddItem("Inside", 1);
                _optOutlinePos.AddItem("Center", 2);
                _optOutlinePos.Selected = 0;
            }
        }

        private void WireSignals()
        {
            if (_btnClose != null) _btnClose.Pressed += CancelAndClose;
            if (_btnCancel != null) _btnCancel.Pressed += CancelAndClose;
            if (_btnApply != null) _btnApply.Pressed += ApplyAndClose;
            if (_btnReset != null) _btnReset.Pressed += ResetCurrentParameters;

            if (_chkPreview != null) _chkPreview.Toggled += _ => OnParametersChanged();

            if (_headerContainer != null) _headerContainer.GuiInput += OnHeaderGuiInput;

            // Wire all sliders
            WireSlider(_sliderBlurRadius, _lblBlurRadiusVal, "px");
            WireSlider(_sliderMotionAngle, _lblMotionAngleVal, "°");
            WireSlider(_sliderMotionDistance, _lblMotionDistanceVal, "px");
            WireSlider(_sliderUnsharpAmount, _lblUnsharpAmountVal, "%");
            WireSlider(_sliderUnsharpRadius, _lblUnsharpRadiusVal, "px");
            WireSlider(_sliderUnsharpThreshold, _lblUnsharpThresholdVal, "");
            WireSlider(_sliderNoiseAmount, _lblNoiseAmountVal, "%");
            WireSlider(_sliderMedianRadius, _lblMedianRadiusVal, "px");
            WireSlider(_sliderMosaicCellSize, _lblMosaicCellSizeVal, "px");
            WireSlider(_sliderHalftoneDotSize, _lblHalftoneDotSizeVal, "px");
            WireSlider(_sliderHalftoneLevels, _lblHalftoneLevelsVal, "");
            WireSlider(_sliderEmbossAngle, _lblEmbossAngleVal, "°");
            WireSlider(_sliderEmbossHeight, _lblEmbossHeightVal, "px");
            WireSlider(_sliderEmbossAmount, _lblEmbossAmountVal, "%");
            WireSlider(_sliderOilRadius, _lblOilRadiusVal, "px");
            WireSlider(_sliderShiftX, _lblShiftXVal, "px");
            WireSlider(_sliderShiftY, _lblShiftYVal, "px");
            WireSlider(_sliderOutlineThickness, _lblOutlineThicknessVal, "px");
            WireSlider(_sliderHighPassRadius, _lblHighPassRadiusVal, "px");

            if (_chkNoiseMonochromatic != null) _chkNoiseMonochromatic.Toggled += _ => OnParametersChanged();
            if (_optNoiseDist != null) _optNoiseDist.ItemSelected += _ => OnParametersChanged();
            if (_colorPickerOutline != null) _colorPickerOutline.ColorChanged += _ => OnParametersChanged();
            if (_optOutlinePos != null) _optOutlinePos.ItemSelected += _ => OnParametersChanged();
        }

        private void WireSlider(HSlider slider, Label label, string unit)
        {
            if (slider == null) return;
            slider.ValueChanged += val =>
            {
                if (label != null)
                {
                    label.Text = slider.Step < 1 ? $"{val:0.0}{unit}" : $"{Mathf.RoundToInt(val)}{unit}";
                }
                OnParametersChanged();
            };
        }

        public void Open(FilterType type, PaintTabUI paintTab, SkinLayerManager layerManager, MeshPainter3D painter)
        {
            StudioUIManager.CloseAnyActiveAdjustmentOrFilterModal(this);

            _paintTab = paintTab;
            _layerManager = layerManager;
            _painter = painter;

            if (_layerManager == null)
            {
                GD.PrintErr("[FilterModalUI] LayerManager is null.");
                return;
            }

            _activeLayer = _layerManager.ActiveLayer;
            if (_activeLayer == null)
            {
                GD.PrintErr("[FilterModalUI] No active layer selected.");
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

            _backupBuffer = new byte[expectedLen];

            // GIMP-style auto-sampling visible underneath
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

            if (_chkPreview != null) _chkPreview.ButtonPressed = true;

            CenterOrPositionModal();
            StudioUIManager.Instance?.UpdateModalsState();
            OnParametersChanged();
        }

        public void ClampPositionToScreen(Vector2? candidatePos = null)
        {
            var vp = GetViewport();
            if (vp == null) return;
            Vector2 vpSize = vp.GetVisibleRect().Size;
            if (vpSize.X <= 0 || vpSize.Y <= 0) vpSize = new Vector2(1920, 1080);

            float maxAllowedH = vpSize.Y * 0.85f;
            float modalW = CustomMinimumSize.X > 0 ? CustomMinimumSize.X : Size.X;
            float modalH = CustomMinimumSize.Y > 0 ? CustomMinimumSize.Y : Size.Y;
            if (modalH > maxAllowedH)
            {
                modalH = maxAllowedH;
                CustomMinimumSize = new Vector2(modalW, modalH);
                Size = new Vector2(modalW, modalH);
            }

            Vector2 target = candidatePos ?? Position;
            float clampedX = Mathf.Clamp(target.X, 20f, Mathf.Max(20f, vpSize.X - modalW - 20f));
            float clampedY = Mathf.Clamp(target.Y, 20f, Mathf.Max(20f, vpSize.Y - modalH - 20f));
            Position = new Vector2(clampedX, clampedY);
        }

        private void CenterOrPositionModal()
        {
            var vpSize = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);
            float modalW = CustomMinimumSize.X > 0 ? CustomMinimumSize.X : 380;
            float modalH = CustomMinimumSize.Y > 0 ? CustomMinimumSize.Y : 300;

            float targetX = Mathf.Clamp(vpSize.X - modalW - 60, 20f, vpSize.X - modalW - 20f);
            float targetY = Mathf.Clamp((vpSize.Y - modalH) * 0.35f, 20f, vpSize.Y - modalH - 20f);
            ClampPositionToScreen(new Vector2(targetX, targetY));
        }

        private void SetupModalForType(FilterType type)
        {
            _suppressEvents = true;

            // Hide all subpanels
            VBoxContainer[] panels = {
                _panelBlur, _panelMotionBlur, _panelUnsharpMask, _panelAddNoise,
                _panelMedian, _panelMosaic, _panelHalftone, _panelEmboss,
                _panelOilPaint, _panelChromaticAberration, _panelOutlineStroke, _panelHighPass
            };
            foreach (var p in panels)
            {
                if (p != null) p.Visible = false;
            }

            string title = "Filter";
            string subtitle = "Apply raster filter to active target";

            var defaults = FilterParameters.Default(type);

            switch (type)
            {
                case FilterType.GaussianBlur:
                    title = "Gaussian Blur";
                    subtitle = "Separable multi-threaded Gaussian smoothing";
                    if (_panelBlur != null) _panelBlur.Visible = true;
                    if (_sliderBlurRadius != null) _sliderBlurRadius.Value = defaults.BlurRadius;
                    break;

                case FilterType.BoxBlur:
                    title = "Box Blur";
                    subtitle = "Fast separable uniform area averaging";
                    if (_panelBlur != null) _panelBlur.Visible = true;
                    if (_sliderBlurRadius != null) _sliderBlurRadius.Value = defaults.BlurRadius;
                    break;

                case FilterType.MotionBlur:
                    title = "Motion Blur";
                    subtitle = "Linear directional motion streak filter";
                    if (_panelMotionBlur != null) _panelMotionBlur.Visible = true;
                    if (_sliderMotionAngle != null) _sliderMotionAngle.Value = defaults.MotionAngle;
                    if (_sliderMotionDistance != null) _sliderMotionDistance.Value = defaults.MotionDistance;
                    break;

                case FilterType.UnsharpMask:
                    title = "Unsharp Mask";
                    subtitle = "Edge enhancement via Gaussian high-frequency difference";
                    if (_panelUnsharpMask != null) _panelUnsharpMask.Visible = true;
                    if (_sliderUnsharpAmount != null) _sliderUnsharpAmount.Value = defaults.UnsharpAmount;
                    if (_sliderUnsharpRadius != null) _sliderUnsharpRadius.Value = defaults.UnsharpRadius;
                    if (_sliderUnsharpThreshold != null) _sliderUnsharpThreshold.Value = defaults.UnsharpThreshold;
                    break;

                case FilterType.AddNoise:
                    title = "Add Noise";
                    subtitle = "Uniform or Gaussian pseudo-random grain synthesis";
                    if (_panelAddNoise != null) _panelAddNoise.Visible = true;
                    if (_sliderNoiseAmount != null) _sliderNoiseAmount.Value = defaults.NoiseAmount;
                    if (_chkNoiseMonochromatic != null) _chkNoiseMonochromatic.ButtonPressed = defaults.NoiseMonochromatic;
                    if (_optNoiseDist != null) _optNoiseDist.Selected = (int)defaults.NoiseDist;
                    break;

                case FilterType.Median:
                    title = "Median (Denoise)";
                    subtitle = "Salt-and-pepper noise removal with sharp edge preservation";
                    if (_panelMedian != null) _panelMedian.Visible = true;
                    if (_sliderMedianRadius != null) _sliderMedianRadius.Value = defaults.MedianRadius;
                    break;

                case FilterType.Mosaic:
                    title = "Mosaic / Pixelate";
                    subtitle = "Blocky cell averaging retro pixelation";
                    if (_panelMosaic != null) _panelMosaic.Visible = true;
                    if (_sliderMosaicCellSize != null) _sliderMosaicCellSize.Value = defaults.MosaicCellSize;
                    break;

                case FilterType.ColorHalftone:
                    title = "Color Halftone / Dither";
                    subtitle = "CMYK-style rosette dots and quantization";
                    if (_panelHalftone != null) _panelHalftone.Visible = true;
                    if (_sliderHalftoneDotSize != null) _sliderHalftoneDotSize.Value = defaults.HalftoneDotSize;
                    if (_sliderHalftoneLevels != null) _sliderHalftoneLevels.Value = defaults.HalftoneLevels;
                    break;

                case FilterType.Emboss:
                    title = "Emboss";
                    subtitle = "Directional relief bas-relief lighting illusion";
                    if (_panelEmboss != null) _panelEmboss.Visible = true;
                    if (_sliderEmbossAngle != null) _sliderEmbossAngle.Value = defaults.EmbossAngle;
                    if (_sliderEmbossHeight != null) _sliderEmbossHeight.Value = defaults.EmbossHeight;
                    if (_sliderEmbossAmount != null) _sliderEmbossAmount.Value = defaults.EmbossAmount;
                    break;

                case FilterType.OilPaint:
                    title = "Oil Paint (Kuwahara)";
                    subtitle = "Anisotropic quadrant variance painterly filter";
                    if (_panelOilPaint != null) _panelOilPaint.Visible = true;
                    if (_sliderOilRadius != null) _sliderOilRadius.Value = defaults.OilPaintRadius;
                    break;

                case FilterType.ChromaticAberration:
                    title = "Chromatic Aberration";
                    subtitle = "RGB channel displacement and lens fringe";
                    if (_panelChromaticAberration != null) _panelChromaticAberration.Visible = true;
                    if (_sliderShiftX != null) _sliderShiftX.Value = defaults.ShiftX;
                    if (_sliderShiftY != null) _sliderShiftY.Value = defaults.ShiftY;
                    break;

                case FilterType.OutlineStroke:
                    title = "Outline / Stroke Alpha";
                    subtitle = "Generates outer, inner, or centered contour stroke";
                    if (_panelOutlineStroke != null) _panelOutlineStroke.Visible = true;
                    if (_sliderOutlineThickness != null) _sliderOutlineThickness.Value = defaults.OutlineThickness;
                    if (_colorPickerOutline != null) _colorPickerOutline.Color = defaults.OutlineColor.LinearToSrgb();
                    if (_optOutlinePos != null) _optOutlinePos.Selected = (int)defaults.OutlinePos;
                    break;

                case FilterType.HighPass:
                    title = "High Pass";
                    subtitle = "Suppresses low-frequency gradients, leaves fine details";
                    if (_panelHighPass != null) _panelHighPass.Visible = true;
                    if (_sliderHighPassRadius != null) _sliderHighPassRadius.Value = defaults.HighPassRadius;
                    break;
            }

            if (_lblTitle != null) _lblTitle.Text = title;
            if (_lblSubtitle != null)
            {
                string layerName = _layerManager.IsBaseTextureActive ? "BASE TEXTURE" : (_activeLayer?.Name ?? "Active Layer");
                string modeHint = _isSamplingUnderlyingComposite ? " [Auto-Sampling Visible Underneath]" : "";
                _lblSubtitle.Text = _hasSelection
                    ? $"{subtitle} • Target: {layerName}{modeHint} • [Mask Active]"
                    : $"{subtitle} • Target: {layerName}{modeHint}";
            }

            _suppressEvents = false;
            UpdateValueLabels();
        }

        private void UpdateValueLabels()
        {
            if (_sliderBlurRadius != null && _lblBlurRadiusVal != null) _lblBlurRadiusVal.Text = $"{_sliderBlurRadius.Value:0.0}px";
            if (_sliderMotionAngle != null && _lblMotionAngleVal != null) _lblMotionAngleVal.Text = $"{Mathf.RoundToInt(_sliderMotionAngle.Value)}°";
            if (_sliderMotionDistance != null && _lblMotionDistanceVal != null) _lblMotionDistanceVal.Text = $"{Mathf.RoundToInt(_sliderMotionDistance.Value)}px";
            if (_sliderUnsharpAmount != null && _lblUnsharpAmountVal != null) _lblUnsharpAmountVal.Text = $"{Mathf.RoundToInt(_sliderUnsharpAmount.Value)}%";
            if (_sliderUnsharpRadius != null && _lblUnsharpRadiusVal != null) _lblUnsharpRadiusVal.Text = $"{_sliderUnsharpRadius.Value:0.0}px";
            if (_sliderUnsharpThreshold != null && _lblUnsharpThresholdVal != null) _lblUnsharpThresholdVal.Text = $"{Mathf.RoundToInt(_sliderUnsharpThreshold.Value)}";
            if (_sliderNoiseAmount != null && _lblNoiseAmountVal != null) _lblNoiseAmountVal.Text = $"{Mathf.RoundToInt(_sliderNoiseAmount.Value)}%";
            if (_sliderMedianRadius != null && _lblMedianRadiusVal != null) _lblMedianRadiusVal.Text = $"{Mathf.RoundToInt(_sliderMedianRadius.Value)}px";
            if (_sliderMosaicCellSize != null && _lblMosaicCellSizeVal != null) _lblMosaicCellSizeVal.Text = $"{Mathf.RoundToInt(_sliderMosaicCellSize.Value)}px";
            if (_sliderHalftoneDotSize != null && _lblHalftoneDotSizeVal != null) _lblHalftoneDotSizeVal.Text = $"{Mathf.RoundToInt(_sliderHalftoneDotSize.Value)}px";
            if (_sliderHalftoneLevels != null && _lblHalftoneLevelsVal != null) _lblHalftoneLevelsVal.Text = $"{Mathf.RoundToInt(_sliderHalftoneLevels.Value)}";
            if (_sliderEmbossAngle != null && _lblEmbossAngleVal != null) _lblEmbossAngleVal.Text = $"{Mathf.RoundToInt(_sliderEmbossAngle.Value)}°";
            if (_sliderEmbossHeight != null && _lblEmbossHeightVal != null) _lblEmbossHeightVal.Text = $"{_sliderEmbossHeight.Value:0.0}px";
            if (_sliderEmbossAmount != null && _lblEmbossAmountVal != null) _lblEmbossAmountVal.Text = $"{Mathf.RoundToInt(_sliderEmbossAmount.Value)}%";
            if (_sliderOilRadius != null && _lblOilRadiusVal != null) _lblOilRadiusVal.Text = $"{Mathf.RoundToInt(_sliderOilRadius.Value)}px";
            if (_sliderShiftX != null && _lblShiftXVal != null) _lblShiftXVal.Text = $"{Mathf.RoundToInt(_sliderShiftX.Value)}px";
            if (_sliderShiftY != null && _lblShiftYVal != null) _lblShiftYVal.Text = $"{Mathf.RoundToInt(_sliderShiftY.Value)}px";
            if (_sliderOutlineThickness != null && _lblOutlineThicknessVal != null) _lblOutlineThicknessVal.Text = $"{Mathf.RoundToInt(_sliderOutlineThickness.Value)}px";
            if (_sliderHighPassRadius != null && _lblHighPassRadiusVal != null) _lblHighPassRadiusVal.Text = $"{_sliderHighPassRadius.Value:0.0}px";
        }

        private FilterParameters GetCurrentParameters()
        {
            var p = FilterParameters.Default(_currentType);

            switch (_currentType)
            {
                case FilterType.GaussianBlur:
                case FilterType.BoxBlur:
                    p.BlurRadius = _sliderBlurRadius != null ? (float)_sliderBlurRadius.Value : 4f;
                    break;

                case FilterType.MotionBlur:
                    p.MotionAngle = _sliderMotionAngle != null ? (float)_sliderMotionAngle.Value : 0f;
                    p.MotionDistance = _sliderMotionDistance != null ? (float)_sliderMotionDistance.Value : 12f;
                    break;

                case FilterType.UnsharpMask:
                    p.UnsharpAmount = _sliderUnsharpAmount != null ? (float)_sliderUnsharpAmount.Value : 100f;
                    p.UnsharpRadius = _sliderUnsharpRadius != null ? (float)_sliderUnsharpRadius.Value : 2f;
                    p.UnsharpThreshold = _sliderUnsharpThreshold != null ? (float)_sliderUnsharpThreshold.Value : 0f;
                    break;

                case FilterType.AddNoise:
                    p.NoiseAmount = _sliderNoiseAmount != null ? (float)_sliderNoiseAmount.Value : 15f;
                    p.NoiseMonochromatic = _chkNoiseMonochromatic == null || _chkNoiseMonochromatic.ButtonPressed;
                    p.NoiseDist = (NoiseDistribution)(_optNoiseDist != null ? _optNoiseDist.Selected : 0);
                    break;

                case FilterType.Median:
                    p.MedianRadius = _sliderMedianRadius != null ? (int)_sliderMedianRadius.Value : 2;
                    break;

                case FilterType.Mosaic:
                    p.MosaicCellSize = _sliderMosaicCellSize != null ? (int)_sliderMosaicCellSize.Value : 8;
                    break;

                case FilterType.ColorHalftone:
                    p.HalftoneDotSize = _sliderHalftoneDotSize != null ? (int)_sliderHalftoneDotSize.Value : 6;
                    p.HalftoneLevels = _sliderHalftoneLevels != null ? (int)_sliderHalftoneLevels.Value : 4;
                    break;

                case FilterType.Emboss:
                    p.EmbossAngle = _sliderEmbossAngle != null ? (float)_sliderEmbossAngle.Value : 45f;
                    p.EmbossHeight = _sliderEmbossHeight != null ? (float)_sliderEmbossHeight.Value : 2f;
                    p.EmbossAmount = _sliderEmbossAmount != null ? (float)_sliderEmbossAmount.Value : 100f;
                    break;

                case FilterType.OilPaint:
                    p.OilPaintRadius = _sliderOilRadius != null ? (int)_sliderOilRadius.Value : 3;
                    break;

                case FilterType.ChromaticAberration:
                    p.ShiftX = _sliderShiftX != null ? (int)_sliderShiftX.Value : 4;
                    p.ShiftY = _sliderShiftY != null ? (int)_sliderShiftY.Value : 0;
                    break;

                case FilterType.OutlineStroke:
                    p.OutlineThickness = _sliderOutlineThickness != null ? (int)_sliderOutlineThickness.Value : 3;
                    p.OutlineColor = _colorPickerOutline != null ? _colorPickerOutline.Color.SrgbToLinear() : new Color(0.85f, 0.68f, 0.32f, 1f);
                    p.OutlinePos = (OutlinePosition)(_optOutlinePos != null ? _optOutlinePos.Selected : 0);
                    break;

                case FilterType.HighPass:
                    p.HighPassRadius = _sliderHighPassRadius != null ? (float)_sliderHighPassRadius.Value : 10f;
                    break;
            }

            return p;
        }

        private void ResetCurrentParameters()
        {
            SetupModalForType(_currentType);
            OnParametersChanged();
        }

        private void OnParametersChanged()
        {
            if (_suppressEvents || _backupBuffer == null || _activeLayer == null || _layerManager == null) return;
            UpdateValueLabels();

            int w = _layerManager.CanvasSize.X > 0 ? _layerManager.CanvasSize.X : 2048;
            int h = _layerManager.CanvasSize.Y > 0 ? _layerManager.CanvasSize.Y : 2048;

            bool previewEnabled = _chkPreview == null || _chkPreview.ButtonPressed;
            if (previewEnabled)
            {
                var p = GetCurrentParameters();
                FilterEngine.ApplyFilter(
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
            EmitSignal(SignalName.FilterCancelled);
        }

        public void ApplyAndClose()
        {
            if (_backupBuffer != null && _activeLayer != null && _activeLayer.GpuData != null && _layerManager != null)
            {
                int w = _layerManager.CanvasSize.X > 0 ? _layerManager.CanvasSize.X : 2048;
                int h = _layerManager.CanvasSize.Y > 0 ? _layerManager.CanvasSize.Y : 2048;

                if (_isSamplingUnderlyingComposite)
                {
                    Array.Clear(_activeLayer.GpuData, 0, _activeLayer.GpuData.Length);
                }
                else
                {
                    Buffer.BlockCopy(_backupBuffer, 0, _activeLayer.GpuData, 0, _backupBuffer.Length);
                }
                _layerManager.RecordGpuUndoSnapshot(is2DCpuStroke: true);

                var p = GetCurrentParameters();
                LastFilterParameters = p;

                FilterEngine.ApplyFilter(
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

                _layerManager.SyncActiveLayerGpuTexture();
                _layerManager.RecompositeGpuLayers();
                _layerManager.UpdateStandaloneComposite();
                _layerManager.SyncGpuStrokeToComposite();
                _layerManager.SetActiveContextHasPaintStrokes(true);

                if (!string.IsNullOrEmpty(_layerManager.ActiveMaterialKey) && _layerManager.MeshHierarchy != null)
                {
                    _layerManager.MeshHierarchy.MarkMaterialDirty(_layerManager.ActiveMaterialKey, _layerManager);
                }
                _paintTab?.RegisterActiveStrokeMetadata();
                _paintTab?.BrushPalette?.UpdateUndoRedoState(_layerManager.CanUndo, _layerManager.CanRedo);

                var uvCanvas = GetTree()?.Root?.FindChild("UVCanvasPanel", true, false) as DeadlockPlayground.UI.UVCanvas2DUI;
                uvCanvas?.QueueCanvasRedraw();

                StudioUIManager.Instance?.ShowToast($"{_lblTitle?.Text ?? "Filter"} Applied");
            }

            _backupBuffer = null;
            _isDraggingModal = false;
            Visible = false;
            IsOpen = false;

            StudioUIManager.Instance?.UpdateModalsState();
            EmitSignal(SignalName.FilterApplied);
        }

        private void OnHeaderGuiInput(InputEvent ev)
        {
            if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
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
            else if (ev is InputEventMouseMotion mm && _isDraggingModal)
            {
                ClampPositionToScreen(_dragPositionStart + (mm.GlobalPosition - _dragMouseStart));
            }
        }

        public override void _Input(InputEvent @event)
        {
            if (!Visible) return;

            if (_isDraggingModal)
            {
                if (@event is InputEventMouseMotion mm)
                {
                    ClampPositionToScreen(_dragPositionStart + (mm.GlobalPosition - _dragMouseStart));
                    GetViewport()?.SetInputAsHandled();
                }
                else if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && !mb.Pressed)
                {
                    _isDraggingModal = false;
                    GetViewport()?.SetInputAsHandled();
                }
            }
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
}
