using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using DeadlockPlayground.Painter;
using DeadlockPlayground.UI;

namespace DeadlockPlayground.UI
{
    public partial class GradientMapModalUI : PanelContainer
    {
        [Signal] public delegate void GradientMapAppliedEventHandler();
        [Signal] public delegate void GradientMapCancelledEventHandler();

        public static bool IsOpen { get; private set; } = false;

        [ExportCategory("Header")]
        [Export] private Control _headerContainer;
        [Export] private Label _lblTitle;
        [Export] private Label _lblSubtitle;
        [Export] private Button _btnClose;

        [ExportCategory("Ramp Editor")]
        [Export] private TextureRect _gradientRampPreview;
        [Export] private Control _stopMarkersTrack;
        [Export] private ColorPickerButton _colorPickerStop;
        [Export] private SpinBox _spinStopPosition;
        [Export] private Button _btnDeleteStop;
        [Export] private CheckBox _chkReverse;
        [Export] private CheckBox _chkDither;
        [Export] private OptionButton _optBlendMode;
        [Export] private HSlider _sliderOpacity;
        [Export] private Label _lblOpacityVal;

        [ExportCategory("Presets")]
        [Export] private ItemList _listBuiltinPresets;
        [Export] private ItemList _listImportedPresets;
        [Export] private Button _btnImportGrd;
        [Export] private FileDialog _fileDialogGrd;

        [ExportCategory("Footer Controls")]
        [Export] private CheckBox _chkPreview;
        [Export] private Button _btnReset;
        [Export] private Button _btnCancel;
        [Export] private Button _btnApply;

        private PaintTabUI _paintTab;
        private SkinLayerManager _layerManager;
        private MeshPainter3D _painter;
        private SkinLayer _activeLayer;

        private byte[] _backupBuffer = null;

        private string _openedMaterialKey = "";
        private int _openedW = 0;
        private int _openedH = 0;
        private bool _hasSelection = false;
        private byte[] _selectionMaskBuffer = null;
        private int _maskW = 0;
        private int _maskH = 0;

        private bool _isDraggingModal = false;
        private Vector2 _dragMouseStart;
        private Vector2 _dragPositionStart;

        private bool _isSamplingUnderlyingComposite = false;
        private bool _suppressEvents = false;

        private readonly List<GradientStop> _currentStops = new();
        private int _selectedStopIndex = 0;
        private bool _isDraggingStop = false;

        private List<GradientDefinition> _builtInPresets = new();
        private List<GradientDefinition> _importedPresets = new();

       private bool _isAdjustingRect = false;

        public override void _Ready()
        {
            Visible = false;
            IsOpen = false;
            ClipContents = true;

            SetAnchorsPreset(LayoutPreset.TopLeft);
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            SizeFlagsVertical = SizeFlags.ShrinkBegin;

            if (_lblTitle != null)
            {
                _lblTitle.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            }
            if (_lblSubtitle != null)
            {
                _lblSubtitle.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            }

            if (_listBuiltinPresets != null)
            {
                _listBuiltinPresets.CustomMinimumSize = new Vector2(0, 135);
                _listBuiltinPresets.SizeFlagsVertical = SizeFlags.ShrinkBegin;
            }
            if (_listImportedPresets != null)
            {
                _listImportedPresets.CustomMinimumSize = new Vector2(0, 110);
                _listImportedPresets.SizeFlagsVertical = SizeFlags.ShrinkBegin;
            }

            WireSignals();
            SetupScrollableProtection();
            InitBlendModes();
            LoadPresets();
            InitDefaultGradient();
        }

        public override void _Notification(int what)
        {
            base._Notification(what);
            if ((what == NotificationResized || what == NotificationSortChildren || what == NotificationVisibilityChanged)
                && Visible && !_isAdjustingRect)
            {
                EnforceCompactHeight();
            }
        }

        private void SetupScrollableProtection()
        {
            if (_sliderOpacity != null) _sliderOpacity.Scrollable = false;
        }

        private void InitBlendModes()
        {
            if (_optBlendMode == null) return;
            _optBlendMode.Clear();
            _optBlendMode.AddItem("Normal", 0);
            _optBlendMode.AddItem("Overlay", 1);
            _optBlendMode.AddItem("Soft Light", 2);
            _optBlendMode.AddItem("Multiply", 3);
            _optBlendMode.AddItem("Color", 4);
            _optBlendMode.AddItem("Screen", 5);
            _optBlendMode.Selected = 0;
        }

        private void WireSignals()
        {
            if (_btnClose != null) _btnClose.Pressed += CancelAndClose;
            if (_btnCancel != null) _btnCancel.Pressed += CancelAndClose;
            if (_btnApply != null) _btnApply.Pressed += ApplyAndClose;
            if (_btnReset != null) _btnReset.Pressed += ResetToDefault;

            if (_chkPreview != null) _chkPreview.Toggled += _ => OnParametersChanged();
            if (_chkReverse != null) _chkReverse.Toggled += _ => { UpdateRampPreview(); OnParametersChanged(); };
            if (_chkDither != null) _chkDither.Toggled += _ => OnParametersChanged();
            if (_optBlendMode != null) _optBlendMode.ItemSelected += _ => OnParametersChanged();

            if (_sliderOpacity != null)
            {
                _sliderOpacity.ValueChanged += val =>
                {
                    if (_lblOpacityVal != null) _lblOpacityVal.Text = $"{Mathf.RoundToInt(val)}%";
                    OnParametersChanged();
                };
            }

            if (_colorPickerStop != null)
            {
                _colorPickerStop.ColorChanged += col =>
                {
                    if (_suppressEvents || _selectedStopIndex < 0 || _selectedStopIndex >= _currentStops.Count) return;
                    _currentStops[_selectedStopIndex] = new GradientStop(_currentStops[_selectedStopIndex].Position, col.SrgbToLinear());
                    UpdateRampPreview();
                    UpdateMarkersDisplay();
                    OnParametersChanged();
                };
            }

            if (_spinStopPosition != null)
            {
                _spinStopPosition.ValueChanged += val =>
                {
                    if (_suppressEvents || _selectedStopIndex < 0 || _selectedStopIndex >= _currentStops.Count) return;
                    float newPos = (float)(val / 100.0);
                    _currentStops[_selectedStopIndex] = new GradientStop(newPos, _currentStops[_selectedStopIndex].Color);
                    SortStopsAndMaintainSelection();
                    UpdateRampPreview();
                    UpdateMarkersDisplay();
                    OnParametersChanged();
                };
            }

            if (_btnDeleteStop != null)
            {
                _btnDeleteStop.Pressed += () =>
                {
                    if (_currentStops.Count <= 2 || _selectedStopIndex < 0 || _selectedStopIndex >= _currentStops.Count) return;
                    _currentStops.RemoveAt(_selectedStopIndex);
                    _selectedStopIndex = Math.Clamp(_selectedStopIndex, 0, _currentStops.Count - 1);
                    UpdateSelectedStopUI();
                    UpdateRampPreview();
                    UpdateMarkersDisplay();
                    OnParametersChanged();
                };
            }

            if (_gradientRampPreview != null)
            {
                _gradientRampPreview.GuiInput += OnRampGuiInput;
            }

            if (_stopMarkersTrack != null)
            {
                _stopMarkersTrack.GuiInput += OnTrackGuiInput;
            }

            if (_listBuiltinPresets != null)
            {
                _listBuiltinPresets.ItemSelected += idx =>
                {
                    if (idx >= 0 && idx < _builtInPresets.Count)
                    {
                        ApplyPreset(_builtInPresets[(int)idx]);
                    }
                };
            }

            if (_listImportedPresets != null)
            {
                _listImportedPresets.ItemSelected += idx =>
                {
                    if (idx >= 0 && idx < _importedPresets.Count)
                    {
                        ApplyPreset(_importedPresets[(int)idx]);
                    }
                };
            }

            if (_btnImportGrd != null)
            {
                _btnImportGrd.Pressed += OnOpenGrdDialog;
            }

            if (_fileDialogGrd != null)
            {
                _fileDialogGrd.FileSelected += OnGrdFileSelected;
            }

            if (_headerContainer != null)
            {
                _headerContainer.GuiInput += OnHeaderGuiInput;
            }
        }

        private void InitDefaultGradient()
        {
            _currentStops.Clear();
            _currentStops.Add(new GradientStop(0.0f, new Color(0, 0, 0, 1)));
            _currentStops.Add(new GradientStop(1.0f, new Color(1, 1, 1, 1)));
            _selectedStopIndex = 0;
            UpdateSelectedStopUI();
            UpdateRampPreview();
            UpdateMarkersDisplay();
        }

        public void Open(PaintTabUI paintTab, SkinLayerManager layerManager, MeshPainter3D painter)
        {
            StudioUIManager.CloseAnyActiveAdjustmentOrFilterModal(this);

            _paintTab = paintTab;
            _layerManager = layerManager;
            _painter = painter;

            if (_layerManager == null)
            {
                GD.PrintErr("[GradientMapModalUI] LayerManager is null.");
                return;
            }

            _activeLayer = _layerManager.ActiveLayer;
            if (_activeLayer == null)
            {
                GD.PrintErr("[GradientMapModalUI] No active layer selected.");
                return;
            }

            int w = _layerManager.CanvasSize.X > 0 ? _layerManager.CanvasSize.X : 2048;
            int h = _layerManager.CanvasSize.Y > 0 ? _layerManager.CanvasSize.Y : 2048;
            int expectedLen = w * h * 8;

            _openedMaterialKey = _layerManager.ActiveMaterialKey ?? "";
            _openedW = w;
            _openedH = h;

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

            // GIMP auto-sampling visible underneath
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

            if (_lblSubtitle != null)
            {
                string layerName = _layerManager.IsBaseTextureActive ? "BASE TEXTURE" : (_activeLayer.Name ?? "Active Layer");
                string modeHint = _isSamplingUnderlyingComposite ? " [Auto-Sampling Visible Underneath]" : "";
                _lblSubtitle.Text = _hasSelection
                    ? $"Target: {layerName}{modeHint}  •  [Selection Mask Active]"
                    : $"Target: {layerName}{modeHint}";
            }

            Visible = true;
            IsOpen = true;

            if (_chkPreview != null) _chkPreview.ButtonPressed = true;

            CenterOrPositionModal();
            StudioUIManager.Instance?.UpdateModalsState();
            OnParametersChanged();
        }

        private void EnforceCompactHeight()
        {
            if (!IsInsideTree() || _isAdjustingRect) return;
            _isAdjustingRect = true;

            var vpSize = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);
            if (vpSize.X <= 0 || vpSize.Y <= 0) vpSize = new Vector2(1920, 1080);

            if (GetChildCount() > 0 && GetChild(0) is Control rootChild)
            {
                rootChild.SizeFlagsVertical = SizeFlags.ShrinkBegin;
                foreach (Node grandChild in rootChild.GetChildren())
                {
                    if (grandChild is Control gc)
                        gc.SizeFlagsVertical = SizeFlags.ShrinkBegin;
                }
            }

            float modalW = Mathf.Clamp(460f, 360f, vpSize.X - 40f);
            CustomMinimumSize = new Vector2(modalW, 0f);

            float exactMinH = Mathf.Clamp(GetCombinedMinimumSize().Y, 320f, vpSize.Y * 0.85f);
            if (Mathf.Abs(Size.Y - exactMinH) > 2f || Mathf.Abs(Size.X - modalW) > 2f)
            {
                SetAnchorsPreset(LayoutPreset.TopLeft);
                Size = new Vector2(modalW, exactMinH);
            }

            // Ensure markers align to the final ramp width on first open
            Callable.From(UpdateMarkersDisplayPositionsOnly).CallDeferred();

            _isAdjustingRect = false;
        }
        public void ClampPositionToScreen(Vector2? candidatePos = null)
        {
            var vp = GetViewport();
            if (vp == null) return;
            Vector2 vpSize = vp.GetVisibleRect().Size;
            if (vpSize.X <= 0 || vpSize.Y <= 0) vpSize = new Vector2(1920, 1080);

            float modalW = Mathf.Clamp(Size.X > 0 ? Size.X : 460f, 360f, vpSize.X - 40f);
            float modalH = Mathf.Clamp(GetCombinedMinimumSize().Y, 320f, vpSize.Y * 0.85f);

            Vector2 target = candidatePos ?? Position;
            float maxX = Mathf.Max(20f, vpSize.X - modalW - 20f);
            float maxY = Mathf.Max(20f, vpSize.Y - modalH - 20f);
            float clampedX = Mathf.Clamp(target.X, 20f, maxX);
            float clampedY = Mathf.Clamp(target.Y, 20f, maxY);
            Position = new Vector2(clampedX, clampedY);
        }

       private void CenterOrPositionModal()
        {
            if (!IsInsideTree()) return;
            _isAdjustingRect = true;

            var vpSize = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);
            if (vpSize.X <= 0 || vpSize.Y <= 0) vpSize = new Vector2(1920, 1080);

            float modalW = Mathf.Clamp(460f, 360f, vpSize.X - 40f);

            SetAnchorsPreset(LayoutPreset.TopLeft);
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            SizeFlagsVertical = SizeFlags.ShrinkBegin;
            CustomMinimumSize = new Vector2(modalW, 0f);
            ResetSize();

            float modalH = Mathf.Clamp(GetCombinedMinimumSize().Y, 320f, vpSize.Y * 0.85f);
            Size = new Vector2(modalW, modalH);

            float maxX = Mathf.Max(20f, vpSize.X - modalW - 20f);
            float maxY = Mathf.Max(20f, vpSize.Y - modalH - 20f);
            float targetX = Mathf.Clamp(vpSize.X - modalW - 60f, 20f, maxX);
            float targetY = Mathf.Clamp((vpSize.Y - modalH) * 0.3f, 20f, maxY);
            Position = new Vector2(targetX, targetY);

            _isAdjustingRect = false;
            Callable.From(UpdateMarkersDisplay).CallDeferred();
        }

        private GradientDefinition CreateCurrentGradientDefinition()
        {
            var def = new GradientDefinition("Current");
            def.Stops = new List<GradientStop>(_currentStops);
            return def;
        }

        private Color EvaluateCurrentRamp(float t, bool reverse = false)
        {
            var def = CreateCurrentGradientDefinition();
            return def.Evaluate(t, reverse: reverse, dither: false);
        }

        private void LoadPresets()
        {
            _builtInPresets = PhotoshopGrdParser.GetBuiltinPresets();
            if (_listBuiltinPresets != null)
            {
                _listBuiltinPresets.Clear();
                foreach (var p in _builtInPresets)
                {
                    var img = p.GeneratePreviewImage(80, 22);
                    var tex = ImageTexture.CreateFromImage(img);
                    _listBuiltinPresets.AddItem(p.Name, tex);
                }
            }

            _importedPresets = PhotoshopGrdParser.LoadUserGradients();
            RefreshImportedPresetsList();
        }

        private void RefreshImportedPresetsList()
        {
            if (_listImportedPresets == null) return;
            _listImportedPresets.Clear();
            foreach (var p in _importedPresets)
            {
                var img = p.GeneratePreviewImage(80, 22);
                var tex = ImageTexture.CreateFromImage(img);
                _listImportedPresets.AddItem(p.Name, tex);
            }
        }

        private void ApplyPreset(GradientDefinition preset)
        {
            if (preset == null || preset.Stops.Count == 0) return;
            _currentStops.Clear();
            foreach (var s in preset.Stops)
            {
                _currentStops.Add(new GradientStop(s.Position, s.Color));
            }
            _selectedStopIndex = 0;
            UpdateSelectedStopUI();
            UpdateRampPreview();
            UpdateMarkersDisplay();
            OnParametersChanged();
        }

        private void OnOpenGrdDialog()
        {
            if (_fileDialogGrd == null)
            {
                _fileDialogGrd = new FileDialog
                {
                    FileMode = FileDialog.FileModeEnum.OpenFile,
                    Access = FileDialog.AccessEnum.Filesystem,
                    Title = "Import Photoshop .GRD Gradient Presets"
                };
                _fileDialogGrd.AddFilter("*.grd", "Photoshop Gradients (*.grd)");
                _fileDialogGrd.FileSelected += OnGrdFileSelected;
                AddChild(_fileDialogGrd);
            }
            _fileDialogGrd.PopupCentered(new Vector2I(800, 500));
        }

        private void OnGrdFileSelected(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                var parsed = PhotoshopGrdParser.LoadFromFile(path);
                if (parsed != null && parsed.Count > 0)
                {
                    _importedPresets.AddRange(parsed);
                    PhotoshopGrdParser.SaveUserGradientFile(path, bytes);
                    RefreshImportedPresetsList();
                    StudioUIManager.Instance?.ShowToast($"Imported {parsed.Count} gradient(s) from {Path.GetFileName(path)}");
                }
                else
                {
                    StudioUIManager.Instance?.ShowToast("No valid gradients found in .grd file.");
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[GradientMapModalUI] Failed to import .grd: {ex.Message}");
                StudioUIManager.Instance?.ShowToast("Failed to parse .grd file.");
            }
        }

        private void UpdateRampPreview()
        {
            if (_gradientRampPreview == null) return;
            const int W = 256;
            const int H = 28;

            var img = Image.CreateEmpty(W, H, false, Image.Format.Rgba8);
            bool reverse = _chkReverse != null && _chkReverse.ButtonPressed;
            var def = CreateCurrentGradientDefinition();

            for (int x = 0; x < W; x++)
            {
                float t = (float)x / (W - 1);
                Color cLinear = def.Evaluate(t, reverse: reverse, dither: false);
                Color cSrgb = cLinear.LinearToSrgb();
                cSrgb.A = 1.0f;

                for (int y = 0; y < H; y++)
                {
                    img.SetPixel(x, y, cSrgb);
                }
            }

            _gradientRampPreview.Texture = ImageTexture.CreateFromImage(img);
        }

        private int FindStopNearTrackPosition(float localX, float trackWidth, float tolerancePx = 16f)
        {
            int bestIdx = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < _currentStops.Count; i++)
            {
                float markerX = _currentStops[i].Position * trackWidth;
                float dist = MathF.Abs(localX - markerX);
                if (dist <= tolerancePx && dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = i;
                }
            }
            return bestIdx;
        }

        private void UpdateMarkerBorderHighlight()
        {
            if (_stopMarkersTrack == null) return;
            var children = _stopMarkersTrack.GetChildren();
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i] is PanelContainer marker)
                {
                    var style = marker.GetThemeStylebox("panel") as StyleBoxFlat;
                    if (style != null)
                    {
                        style.BorderColor = (i == _selectedStopIndex) ? new Color(1, 0.85f, 0.2f, 1f) : new Color(0.15f, 0.15f, 0.15f, 0.9f);
                    }
                }
            }
        }

        private void UpdateMarkersDisplayPositionsOnly()
        {
            if (_stopMarkersTrack == null) return;
            float trackWidth = _stopMarkersTrack.Size.X > 0 ? _stopMarkersTrack.Size.X : 256f;
            var children = _stopMarkersTrack.GetChildren();
            for (int i = 0; i < _currentStops.Count && i < children.Count; i++)
            {
                if (children[i] is Control marker)
                {
                    float markerX = _currentStops[i].Position * trackWidth;
                    marker.Position = new Vector2(markerX - 7, 0);
                }
            }
        }

        private void UpdateMarkersDisplay()
        {
            if (_stopMarkersTrack == null) return;
            foreach (Node child in _stopMarkersTrack.GetChildren())
            {
                child.QueueFree();
            }

            float trackWidth = _stopMarkersTrack.Size.X > 0 ? _stopMarkersTrack.Size.X : 256f;

            for (int i = 0; i < _currentStops.Count; i++)
            {
                int stopIdx = i;
                var stop = _currentStops[i];
                float markerX = stop.Position * trackWidth;

                var marker = new PanelContainer();
                marker.CustomMinimumSize = new Vector2(14, 18);
                marker.Position = new Vector2(markerX - 7, 0);

                var style = new StyleBoxFlat();
                Color stopSrgb = stop.Color.LinearToSrgb();
                style.BgColor = stopSrgb;
                style.BorderWidthLeft = 2;
                style.BorderWidthRight = 2;
                style.BorderWidthTop = 2;
                style.BorderWidthBottom = 2;
                style.BorderColor = (stopIdx == _selectedStopIndex) ? new Color(1, 0.85f, 0.2f, 1f) : new Color(0.15f, 0.15f, 0.15f, 0.9f);
                style.CornerRadiusBottomLeft = 3;
                style.CornerRadiusBottomRight = 3;
                style.CornerRadiusTopLeft = 3;
                style.CornerRadiusTopRight = 3;
                marker.AddThemeStyleboxOverride("panel", style);

                marker.GuiInput += (ev) => OnMarkerGuiInput(ev, stopIdx);
                _stopMarkersTrack.AddChild(marker);
            }
        }

        private void OnMarkerGuiInput(InputEvent ev, int stopIdx)
        {
            if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    _selectedStopIndex = stopIdx;
                    _isDraggingStop = true;
                    UpdateSelectedStopUI();
                    UpdateMarkerBorderHighlight();
                    GetViewport()?.SetInputAsHandled();
                }
            }
        }

        private void OnRampGuiInput(InputEvent ev)
        {
            if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                float rampW = _gradientRampPreview.Size.X > 0 ? _gradientRampPreview.Size.X : 256f;
                float localX = Mathf.Clamp(mb.Position.X, 0f, rampW);
                int hit = FindStopNearTrackPosition(localX, rampW, 16f);
                if (hit >= 0)
                {
                    _selectedStopIndex = hit;
                    _isDraggingStop = true;
                    UpdateSelectedStopUI();
                    UpdateMarkerBorderHighlight();
                }
                else
                {
                    float t = Mathf.Clamp(localX / rampW, 0f, 1f);
                    Color picked = EvaluateCurrentRamp(t);
                    _currentStops.Add(new GradientStop(t, picked));
                    SortStopsAndMaintainSelection();
                    _selectedStopIndex = _currentStops.FindIndex(s => Mathf.IsEqualApprox(s.Position, t));
                    if (_selectedStopIndex < 0) _selectedStopIndex = 0;
                    _isDraggingStop = true;
                    UpdateSelectedStopUI();
                    UpdateRampPreview();
                    UpdateMarkersDisplay();
                    OnParametersChanged();
                }
                GetViewport()?.SetInputAsHandled();
            }
        }

        private void OnTrackGuiInput(InputEvent ev)
        {
            if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
            {
                float trackWidth = _stopMarkersTrack.Size.X > 0 ? _stopMarkersTrack.Size.X : 256f;
                float localX = Mathf.Clamp(mb.Position.X, 0f, trackWidth);
                int hit = FindStopNearTrackPosition(localX, trackWidth, 16f);
                if (hit >= 0)
                {
                    _selectedStopIndex = hit;
                    _isDraggingStop = true;
                    UpdateSelectedStopUI();
                    UpdateMarkerBorderHighlight();
                }
                else
                {
                    float t = Mathf.Clamp(localX / trackWidth, 0f, 1f);
                    Color picked = EvaluateCurrentRamp(t);
                    _currentStops.Add(new GradientStop(t, picked));
                    SortStopsAndMaintainSelection();
                    _selectedStopIndex = _currentStops.FindIndex(s => Mathf.IsEqualApprox(s.Position, t));
                    if (_selectedStopIndex < 0) _selectedStopIndex = 0;
                    _isDraggingStop = true;
                    UpdateSelectedStopUI();
                    UpdateRampPreview();
                    UpdateMarkersDisplay();
                    OnParametersChanged();
                }
                GetViewport()?.SetInputAsHandled();
            }
        }

        private void SortStopsAndMaintainSelection()
        {
            if (_selectedStopIndex < 0 || _selectedStopIndex >= _currentStops.Count) return;
            var selStop = _currentStops[_selectedStopIndex];
            _currentStops.Sort((a, b) => a.Position.CompareTo(b.Position));
            _selectedStopIndex = _currentStops.IndexOf(selStop);
            if (_selectedStopIndex < 0) _selectedStopIndex = 0;
        }

        private void UpdateSelectedStopUI()
        {
            if (_selectedStopIndex < 0 || _selectedStopIndex >= _currentStops.Count) return;
            _suppressEvents = true;

            var stop = _currentStops[_selectedStopIndex];
            if (_colorPickerStop != null)
            {
                _colorPickerStop.Color = stop.Color.LinearToSrgb();
            }
            if (_spinStopPosition != null)
            {
                _spinStopPosition.Value = Mathf.RoundToInt(stop.Position * 100f);
            }
            if (_btnDeleteStop != null)
            {
                _btnDeleteStop.Disabled = (_currentStops.Count <= 2);
            }

            _suppressEvents = false;
        }

        private void OnParametersChanged()
        {

            // Si el usuario cambió de material o de capa mientras el modal estaba abierto, cancelar inmediatamente antes de corromper la GPU
            if (_layerManager.ActiveLayer != _activeLayer ||
                !string.Equals(_layerManager.ActiveMaterialKey ?? "", _openedMaterialKey, StringComparison.Ordinal) ||
                _layerManager.CanvasSize.X != _openedW ||
                _layerManager.CanvasSize.Y != _openedH)
            {
                CancelAndClose();
                return;
            }

            if (_backupBuffer == null || _activeLayer == null || _layerManager == null) return;

            int w = _layerManager.CanvasSize.X > 0 ? _layerManager.CanvasSize.X : 2048;
            int h = _layerManager.CanvasSize.Y > 0 ? _layerManager.CanvasSize.Y : 2048;

            bool previewEnabled = _chkPreview == null || _chkPreview.ButtonPressed;
            if (previewEnabled)
            {
                var gradient = CreateCurrentGradientDefinition();
                var p = new GradientMapParams
                {
                    Reverse = _chkReverse != null && _chkReverse.ButtonPressed,
                    Dither = _chkDither == null || _chkDither.ButtonPressed,
                    BlendMode = (GradientMapBlendMode)(_optBlendMode != null ? _optBlendMode.Selected : 0),
                    Opacity = _sliderOpacity != null ? (float)(_sliderOpacity.Value / 100.0) : 1.0f
                };

                GradientMapEngine.ApplyGradientMap(
                    _backupBuffer,
                    _activeLayer.GpuData,
                    w,
                    h,
                    gradient,
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
            EmitSignal(SignalName.GradientMapCancelled);
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

                var gradient = CreateCurrentGradientDefinition();
                var p = new GradientMapParams
                {
                    Reverse = _chkReverse != null && _chkReverse.ButtonPressed,
                    Dither = _chkDither == null || _chkDither.ButtonPressed,
                    BlendMode = (GradientMapBlendMode)(_optBlendMode != null ? _optBlendMode.Selected : 0),
                    Opacity = _sliderOpacity != null ? (float)(_sliderOpacity.Value / 100.0) : 1.0f
                };

                GradientMapEngine.ApplyGradientMap(
                    _backupBuffer,
                    _activeLayer.GpuData,
                    w,
                    h,
                    gradient,
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

                StudioUIManager.Instance?.ShowToast("Gradient Map Applied");
            }

            _backupBuffer = null;
            _isDraggingModal = false;
            Visible = false;
            IsOpen = false;

            StudioUIManager.Instance?.UpdateModalsState();
            EmitSignal(SignalName.GradientMapApplied);
        }

        private void ResetToDefault()
        {
            InitDefaultGradient();
            if (_chkReverse != null) _chkReverse.ButtonPressed = false;
            if (_chkDither != null) _chkDither.ButtonPressed = true;
            if (_optBlendMode != null) _optBlendMode.Selected = 0;
            if (_sliderOpacity != null) _sliderOpacity.Value = 100;
            OnParametersChanged();
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

            if (_isDraggingStop)
            {
                if (@event is InputEventMouseMotion mm)
                {
                    if (_stopMarkersTrack != null && _selectedStopIndex >= 0 && _selectedStopIndex < _currentStops.Count)
                    {
                        float trackWidth = _stopMarkersTrack.Size.X > 0 ? _stopMarkersTrack.Size.X : 256f;
                        float localX = mm.GlobalPosition.X - _stopMarkersTrack.GlobalPosition.X;
                        float newPos = Mathf.Clamp(localX / trackWidth, 0f, 1f);

                        _currentStops[_selectedStopIndex] = new GradientStop(newPos, _currentStops[_selectedStopIndex].Color);
                        UpdateMarkersDisplayPositionsOnly();

                        _suppressEvents = true;
                        if (_spinStopPosition != null)
                        {
                            _spinStopPosition.Value = Mathf.RoundToInt(newPos * 100f);
                        }
                        _suppressEvents = false;

                        UpdateRampPreview();
                        OnParametersChanged();
                    }
                    GetViewport()?.SetInputAsHandled();
                }
                else if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && !mb.Pressed)
                {
                    _isDraggingStop = false;
                    SortStopsAndMaintainSelection();
                    UpdateSelectedStopUI();
                    UpdateRampPreview();
                    UpdateMarkersDisplay();
                    OnParametersChanged();
                    GetViewport()?.SetInputAsHandled();
                }
            }
            else if (_isDraggingModal)
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
