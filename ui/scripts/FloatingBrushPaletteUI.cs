using Godot;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using DeadlockPlayground.Painter;
using DeadlockPlayground.Tools;

public partial class FloatingBrushPaletteUI : PanelContainer
{
    [Signal] public delegate void ToolChangedEventHandler(int toolMode);
    [Signal] public delegate void UndoRequestedEventHandler();
    [Signal] public delegate void RedoRequestedEventHandler();
    [Signal] public delegate void ClearRequestedEventHandler();
    [Signal] public delegate void BlendModeChangedEventHandler(int blendMode);
    [Signal] public delegate void FillRequestedEventHandler(Color color);
    [Signal] public delegate void DecalBakedEventHandler();

    // Strip buttons
    private Button _dragHandle;
    private Button _btnToolSelect;
    private Button _btnToolBrush;
    private Button _btnToolErase;
    private Button _btnToolFill;
    private Button _btnToolWand;
    private Button _btnToolDecal;
    private Button _btnToolText;
    private ColorPickerButton _btnColor;
    private Button _btnMirror;
    private Button _btnUndo;
    private Button _btnRedo;
    private Button _btnClear;
    private Button _btnToggleFlyout;
    private Button _btnPin;
    private Label _lblKeymapHint;

    // Flyout container & title
    private Control _flyoutPanel;
    private Label _lblFlyoutTitle;
    private Button _btnCloseFlyout;

    // Flyout sections
    private Control _selectOptions;
    private Label _lblSelectTarget;
    private Control _brushOptions;
    private Control _eraserOptions;
    private Control _bucketFillOptions;
    private Control _wandOptions;
    private Control _decalOptions;
    private Control _textOptions;

    // Brush controls
    private OptionButton _optBlendMode;
    private ColorPickerButton _colorPicker;
    private OptionButton _optBrushShape;
    private HSlider _sliderBrushSize;
    private Label _lblBrushSize;
    private HSlider _sliderBrushFlow;
    private Label _lblBrushFlow;
    private HSlider _sliderBrushHardness;
    private Label _lblBrushHardness;

    // Eraser controls
    private OptionButton _optEraseShape;
    private HSlider _sliderEraseSize;
    private Label _lblEraseSize;
    private HSlider _sliderEraseHardness;
    private Label _lblEraseHardness;
    private HSlider _sliderEraseFlow;
    private Label _lblEraseFlow;

    // Bucket fill controls
    private ColorPickerButton _fillColorPicker;
    private Button _btnFillActiveSubmesh;

    // Magic Wand controls
    private ColorRect _rectWandColorPreview;
    private Label _lblWandColorHex;
    private HSlider _sliderWandTolerance;
    private Label _lblWandTolerance;
    private CheckBox _chkContiguous;
    private CheckBox _chkUseMask;
    private CheckBox _chkIsolateSubmesh;
    private Button _btnClearMask;

    // Decal controls
    private Button _btnImportDecal;
    private TextureRect _decalPreview;
    private HSlider _sliderDecalScale;
    private Label _lblDecalScale;
    private HSlider _sliderDecalRot;
    private Label _lblDecalRot;
    private Button _btnBakeDecal;
    private FileDialog _decalFileDialog;

    // Text controls
    private LineEdit _editText;
    private OptionButton _optSystemFont;
    private Button _btnLoadFont;
    private HSlider _sliderFontSize;
    private Label _lblFontSize;
    private ColorPickerButton _pickerTextColor;
    private HSlider _sliderOutlineSize;
    private Label _lblOutlineSize;
    private ColorPickerButton _pickerOutlineColor;
    private HSlider _sliderTextScale;
    private Label _lblTextScale;
    private HSlider _sliderTextRot;
    private Label _lblTextRot;
    private Button _btnBakeText;
    private FileDialog _textFileDialog;

    // State
    private MeshPainter3D _painter;
    private DecalStamper _decalStamper;
    private TextProjector _textProjector;
    private BrushToolMode _currentTool = BrushToolMode.Paint;
    public bool IsPinned { get; set; } = true;

    private bool _isDragging = false;
    private Vector2 _dragOffset = Vector2.Zero;

    // Active button style with accent gold border
    private StyleBoxFlat _activeButtonStyle;
    private StyleBoxFlat _normalButtonStyle;
    private ButtonGroup _toolButtonGroup;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        var hbox = GetNodeOrNull<Control>("HBoxContainer");
        if (hbox != null) hbox.MouseFilter = MouseFilterEnum.Ignore;
        var leftCol = GetNodeOrNull<Control>("HBoxContainer/LeftColumn");
        if (leftCol != null) leftCol.MouseFilter = MouseFilterEnum.Ignore;
        var keymapHint = GetNodeOrNull<Control>("%KeymapHintContainer") ?? FindChild("KeymapHintContainer", true, false) as Control;
        if (keymapHint != null) keymapHint.MouseFilter = MouseFilterEnum.Ignore;

        CreateButtonStyles();
        LinkControls();
        if (_flyoutPanel != null) _flyoutPanel.MouseFilter = MouseFilterEnum.Stop;
        ConnectEvents();
        
        if (_btnPin != null)
        {
            _btnPin.ToggleMode = true;
            _btnPin.ButtonPressed = IsPinned;
            UpdatePinVisuals();
        }

        ApplyIdleModulates();
        KeybindsManager.OnKeybindsChanged += UpdateTooltipsAndKeymaps;
        UpdateTooltipsAndKeymaps();
        SelectTool(BrushToolMode.Paint, forceFlyoutOpen: true);
    }

    private void ApplyIdleModulates()
    {
        var idleCol = new Color(1.0f, 1.0f, 1.0f, 0.75f);
        if (_dragHandle != null) _dragHandle.Modulate = idleCol;
        if (_btnToolSelect != null) _btnToolSelect.Modulate = idleCol;
        if (_btnToolBrush != null) _btnToolBrush.Modulate = idleCol;
        if (_btnToolErase != null) _btnToolErase.Modulate = idleCol;
        if (_btnToolFill != null) _btnToolFill.Modulate = idleCol;
        if (_btnToolWand != null) _btnToolWand.Modulate = idleCol;
        if (_btnToolDecal != null) _btnToolDecal.Modulate = idleCol;
        if (_btnToolText != null) _btnToolText.Modulate = idleCol;
        if (_btnMirror != null) _btnMirror.Modulate = idleCol;
        if (_btnUndo != null) _btnUndo.Modulate = idleCol;
        if (_btnRedo != null) _btnRedo.Modulate = idleCol;
        if (_btnClear != null) _btnClear.Modulate = idleCol;
        if (_btnToggleFlyout != null) _btnToggleFlyout.Modulate = idleCol;
    }

    private void UpdatePinVisuals()
    {
        if (_btnPin == null) return;
        _btnPin.Text = IsPinned ? "📌" : "📍";
        _btnPin.Modulate = IsPinned ? new Color(1.0f, 0.85f, 0.4f, 1.0f) : new Color(0.6f, 0.6f, 0.6f, 1.0f);
        _btnPin.TooltipText = IsPinned ? "Panel Pinned (Click to Unpin)" : "Panel Unpinned (Click to Pin)";
    }

    private void CreateButtonStyles()
    {
        _activeButtonStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.2f, 0.22f, 0.28f, 1.0f),
            BorderColor = new Color(0.95f, 0.8f, 0.4f, 1.0f),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusBottomLeft = 4
        };

        _normalButtonStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.13f, 0.16f, 0.9f),
            BorderColor = new Color(0.28f, 0.3f, 0.38f, 0.6f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusBottomLeft = 4
        };
    }

    public void Setup(MeshPainter3D painter, DecalStamper decalStamper, TextProjector textProjector = null)
    {
        if (_painter != null && GodotObject.IsInstanceValid(_painter))
        {
            _painter.ColorSampled -= OnColorSampled;
            if (_painter.MeshHierarchy != null)
            {
                _painter.MeshHierarchy.TargetMeshChanged -= OnPainterTargetMeshChanged;
            }
        }

        _painter = painter;
        _decalStamper = decalStamper;
        _textProjector = textProjector;

        if (_painter != null)
        {
            _painter.ColorSampled += OnColorSampled;
            if (_painter.MeshHierarchy != null)
            {
                _painter.MeshHierarchy.TargetMeshChanged += OnPainterTargetMeshChanged;
            }
            if (_painter.MagicWandTool != null)
            {
                _painter.MagicWandTool.ColorSampled += (col) => UpdateWandUI();
                _painter.MagicWandTool.MaskUpdated += (hasMask) => UpdateWandUI();
            }
            SyncFromPainter();
        }
    }

    private T ResolveNode<T>(string uniqueName, string fallbackName) where T : class
    {
        return (GetNodeOrNull<T>($"%{uniqueName}") 
             ?? FindChild(uniqueName, true, false) as T 
             ?? FindChild(fallbackName, true, false) as T);
    }

    private void LinkControls()
    {
        // Strip buttons
        _dragHandle = ResolveNode<Button>("DragHandle", "DragHandle");
        _btnToolSelect = ResolveNode<Button>("BtnSelect", "BtnSelect");
        _btnToolBrush = ResolveNode<Button>("BtnBrush", "BtnBrush");
        _btnToolErase = ResolveNode<Button>("BtnErase", "BtnErase");
        _btnToolFill = ResolveNode<Button>("BtnFill", "BtnFill");
        _btnToolWand = ResolveNode<Button>("BtnWand", "BtnWand");
        _btnToolDecal = ResolveNode<Button>("BtnDecal", "BtnDecal");
        _btnToolText = ResolveNode<Button>("BtnText", "BtnText");
        _btnColor = ResolveNode<ColorPickerButton>("BtnColor", "BtnColor");
        _btnMirror = ResolveNode<Button>("BtnMirror", "BtnMirror");

        _btnUndo = ResolveNode<Button>("BtnUndo", "BtnUndo");
        _btnRedo = ResolveNode<Button>("BtnRedo", "BtnRedo");
        _btnClear = ResolveNode<Button>("BtnClear", "BtnClear");
        _btnToggleFlyout = ResolveNode<Button>("BtnToggleFlyout", "BtnToggleFlyout");
        _btnPin = ResolveNode<Button>("BtnPin", "BtnPin");
        _lblKeymapHint = ResolveNode<Label>("KeymapHintLabel", "KeymapHintLabel");

        // Set up Radio ButtonGroup so tools are mutually exclusive
        _toolButtonGroup = new ButtonGroup();
        var mutualButtons = new[] { _btnToolSelect, _btnToolBrush, _btnToolErase, _btnToolFill, _btnToolWand, _btnToolDecal, _btnToolText };
        foreach (var btn in mutualButtons)
        {
            if (btn != null)
            {
                btn.ToggleMode = true;
                btn.ButtonGroup = _toolButtonGroup;
            }
        }

        // Flyout container & title
        _flyoutPanel = ResolveNode<Control>("FlyoutPanel", "FlyoutPanel");
        _lblFlyoutTitle = ResolveNode<Label>("Title", "Title");
        _btnCloseFlyout = ResolveNode<Button>("BtnCloseFlyout", "BtnCloseFlyout");

        // Sections
        _selectOptions = ResolveNode<Control>("SelectOptions", "SelectOptions");
        _lblSelectTarget = ResolveNode<Label>("CurrentTargetLabel", "CurrentTargetLabel");
        _brushOptions = ResolveNode<Control>("BrushOptions", "BrushOptions");
        _eraserOptions = ResolveNode<Control>("EraserOptions", "EraserOptions");
        _bucketFillOptions = ResolveNode<Control>("BucketFillOptions", "BucketFillOptions");
        _wandOptions = ResolveNode<Control>("MagicWandOptions", "MagicWandOptions");
        _decalOptions = ResolveNode<Control>("DecalOptions", "DecalOptions");
        _textOptions = ResolveNode<Control>("TextOptions", "TextOptions");

        // Brush controls
        _optBlendMode = ResolveNode<OptionButton>("OptBlendMode", "OptBlendMode");
        _colorPicker = ResolveNode<ColorPickerButton>("ColorPickerButton", "ColorPickerButton");
        _optBrushShape = ResolveNode<OptionButton>("OptShape", "OptShape");
        _sliderBrushSize = ResolveNode<HSlider>("SliderSize", "SliderSize");
        _lblBrushSize = ResolveNode<Label>("LblSize", "LblSize");
        _sliderBrushFlow = ResolveNode<HSlider>("SliderFlow", "SliderFlow");
        _lblBrushFlow = ResolveNode<Label>("LblFlow", "LblFlow");
        _sliderBrushHardness = ResolveNode<HSlider>("SliderHardness", "SliderHardness");
        _lblBrushHardness = ResolveNode<Label>("LblHardness", "LblHardness");

        // Eraser controls
        _optEraseShape = ResolveNode<OptionButton>("OptEraseShape", "OptEraseShape");
        _sliderEraseSize = ResolveNode<HSlider>("SliderEraseSize", "SliderEraseSize");
        _lblEraseSize = ResolveNode<Label>("LblEraseSize", "LblEraseSize");
        _sliderEraseHardness = ResolveNode<HSlider>("SliderEraseHardness", "SliderEraseHardness");
        _lblEraseHardness = ResolveNode<Label>("LblEraseHardness", "LblEraseHardness");
        _sliderEraseFlow = ResolveNode<HSlider>("SliderEraseFlow", "SliderEraseFlow");
        _lblEraseFlow = ResolveNode<Label>("LblEraseFlow", "LblEraseFlow");

        // Bucket Fill controls
        _fillColorPicker = ResolveNode<ColorPickerButton>("FillColorPicker", "FillColorPicker");
        _btnFillActiveSubmesh = ResolveNode<Button>("BtnFillActiveSubmesh", "BtnFillActiveSubmesh");

        // Decal controls
        _btnImportDecal = ResolveNode<Button>("BtnImportDecal", "BtnImportDecal");
        _decalPreview = ResolveNode<TextureRect>("DecalPreview", "DecalPreview");
        _sliderDecalScale = ResolveNode<HSlider>("SliderDecalScale", "SliderDecalScale");
        _lblDecalScale = ResolveNode<Label>("LblDecalScale", "LblDecalScale");
        _sliderDecalRot = ResolveNode<HSlider>("SliderDecalRot", "SliderDecalRot");
        _lblDecalRot = ResolveNode<Label>("LblDecalRot", "LblDecalRot");
        _btnBakeDecal = ResolveNode<Button>("BtnBakeDecal", "BtnBakeDecal");
        _decalFileDialog = ResolveNode<FileDialog>("DecalFileDialog", "DecalFileDialog");

        // Text controls
        _editText = ResolveNode<LineEdit>("EditText", "EditText");
        _optSystemFont = ResolveNode<OptionButton>("OptSystemFont", "OptSystemFont");
        _btnLoadFont = ResolveNode<Button>("BtnLoadFont", "BtnLoadFont");
        _sliderFontSize = ResolveNode<HSlider>("SliderFontSize", "SliderFontSize");
        _lblFontSize = ResolveNode<Label>("LblFontSize", "LblFontSize");
        _pickerTextColor = ResolveNode<ColorPickerButton>("PickerTextColor", "PickerTextColor");
        _sliderOutlineSize = ResolveNode<HSlider>("SliderOutlineSize", "SliderOutlineSize");
        _lblOutlineSize = ResolveNode<Label>("LblOutlineSize", "LblOutlineSize");
        _pickerOutlineColor = ResolveNode<ColorPickerButton>("PickerOutlineColor", "PickerOutlineColor");
        _sliderTextScale = ResolveNode<HSlider>("SliderTextScale", "SliderTextScale");
        _lblTextScale = ResolveNode<Label>("LblTextScale", "LblTextScale");
        _sliderTextRot = ResolveNode<HSlider>("SliderTextRot", "SliderTextRot");
        _lblTextRot = ResolveNode<Label>("LblTextRot", "LblTextRot");
        _btnBakeText = ResolveNode<Button>("BtnBakeText", "BtnBakeText");
        _textFileDialog = ResolveNode<FileDialog>("TextFileDialog", "TextFileDialog");

        // Magic Wand controls
        _rectWandColorPreview = ResolveNode<ColorRect>("ColorPreview", "ColorPreview");
        _lblWandColorHex = ResolveNode<Label>("LblColorHex", "LblColorHex");
        _sliderWandTolerance = ResolveNode<HSlider>("SliderTolerance", "SliderTolerance");
        _lblWandTolerance = ResolveNode<Label>("LblTolerance", "LblTolerance");
        _chkContiguous = ResolveNode<CheckBox>("ChkContiguous", "ChkContiguous");
        _chkUseMask = ResolveNode<CheckBox>("ChkUseMask", "ChkUseMask");
        _chkIsolateSubmesh = ResolveNode<CheckBox>("ChkIsolateSubmesh", "ChkIsolateSubmesh");
        _btnClearMask = ResolveNode<Button>("BtnClearMask", "BtnClearMask");

        // Populate Blend Modes
        if (_optBlendMode != null)
        {
            _optBlendMode.Clear();
            _optBlendMode.AddItem("Normal", 0);
            _optBlendMode.AddItem("Multiply (Shadows)", 1);
            _optBlendMode.AddItem("Screen", 2);
            _optBlendMode.AddItem("Overlay", 3);
            _optBlendMode.AddItem("Darken", 4);
            _optBlendMode.AddItem("Lighten", 5);
            _optBlendMode.AddItem("Color Dodge", 6);
            _optBlendMode.Select(0);
        }

        // Populate Brush Shapes
        if (_optBrushShape != null)
        {
            _optBrushShape.Clear();
            _optBrushShape.AddItem("Soft Circle", (int)BrushShapeType.SoftCircle);
            _optBrushShape.AddItem("Hard Circle", (int)BrushShapeType.HardCircle);
            _optBrushShape.AddItem("Splatter", (int)BrushShapeType.Splatter);
            _optBrushShape.AddItem("Grunge", (int)BrushShapeType.Grunge);
            _optBrushShape.AddItem("Square", (int)BrushShapeType.Square);
            _optBrushShape.Select(0);
        }

        // Populate Eraser Shapes
        if (_optEraseShape != null)
        {
            _optEraseShape.Clear();
            _optEraseShape.AddItem("Soft Circle", (int)BrushShapeType.SoftCircle);
            _optEraseShape.AddItem("Hard Circle", (int)BrushShapeType.HardCircle);
            _optEraseShape.AddItem("Splatter", (int)BrushShapeType.Splatter);
            _optEraseShape.AddItem("Grunge", (int)BrushShapeType.Grunge);
            _optEraseShape.AddItem("Square", (int)BrushShapeType.Square);
            _optEraseShape.Select((int)BrushShapeType.HardCircle);
        }
    }

    private void ConnectEvents()
    {
        // Tool selection
        if (_btnToolSelect != null) _btnToolSelect.Pressed += () => OnToolButtonClicked(BrushToolMode.SelectSubmesh);
        if (_btnToolBrush != null) _btnToolBrush.Pressed += () => OnToolButtonClicked(BrushToolMode.Paint);
        if (_btnToolErase != null) _btnToolErase.Pressed += () => OnToolButtonClicked(BrushToolMode.Erase);
        if (_btnToolFill != null) _btnToolFill.Pressed += () => OnToolButtonClicked(BrushToolMode.BucketFill);
        if (_btnToolWand != null) _btnToolWand.Pressed += () => OnToolButtonClicked(BrushToolMode.MagicWand);
        if (_btnToolDecal != null) _btnToolDecal.Pressed += () => OnToolButtonClicked(BrushToolMode.Decal);
        if (_btnToolText != null) _btnToolText.Pressed += () => OnToolButtonClicked(BrushToolMode.Text);

        if (_btnColor != null) _btnColor.ColorChanged += SetUniversalColor;
        if (_btnMirror != null) _btnMirror.Toggled += OnMirrorToggled;

        // Action buttons
        if (_btnUndo != null) _btnUndo.Pressed += () => EmitSignal(SignalName.UndoRequested);
        if (_btnRedo != null) _btnRedo.Pressed += () => EmitSignal(SignalName.RedoRequested);
        if (_btnClear != null) _btnClear.Pressed += () => EmitSignal(SignalName.ClearRequested);

        if (_btnToggleFlyout != null)
        {
            _btnToggleFlyout.Pressed += () =>
            {
                if (_flyoutPanel != null) _flyoutPanel.Visible = !_flyoutPanel.Visible;
            };
        }

        if (_btnPin != null)
        {
            _btnPin.Toggled += (pinned) =>
            {
                IsPinned = pinned;
                UpdatePinVisuals();
            };
        }

        if (_btnCloseFlyout != null)
        {
            _btnCloseFlyout.Pressed += () =>
            {
                if (_flyoutPanel != null) _flyoutPanel.Visible = false;
            };
        }

        // Brush parameters
        if (_optBlendMode != null)
        {
            _optBlendMode.ItemSelected += (idx) =>
            {
                int mode = _optBlendMode.GetItemId((int)idx);
                if (_painter != null) _painter.BlendMode = mode;
                EmitSignal(SignalName.BlendModeChanged, mode);
            };
        }

        if (_colorPicker != null)
        {
            _colorPicker.ColorChanged += (c) =>
            {
                if (_painter != null) _painter.BrushColor = c;
                if (_fillColorPicker != null) _fillColorPicker.Color = c;
            };
        }

        if (_optBrushShape != null)
        {
            _optBrushShape.ItemSelected += (idx) =>
            {
                if (_painter != null)
                {
                    _painter.BrushShape = (BrushShapeType)_optBrushShape.GetItemId((int)idx);
                    _painter.SyncCameraBrushProperties(force: true);
                }
            };
        }

        if (_optEraseShape != null)
        {
            _optEraseShape.ItemSelected += (idx) =>
            {
                if (_painter != null)
                {
                    _painter.EraserShape = (BrushShapeType)_optEraseShape.GetItemId((int)idx);
                    _painter.SyncCameraBrushProperties(force: true);
                }
            };
        }

        if (_sliderBrushSize != null)
        {
            _sliderBrushSize.ValueChanged += (v) =>
            {
                if (_painter != null && _currentTool == BrushToolMode.Paint) _painter.BrushSize = (float)v;
                if (_lblBrushSize != null) _lblBrushSize.Text = $"{Mathf.RoundToInt(v)} px";
            };
        }

        if (_sliderBrushFlow != null)
        {
            _sliderBrushFlow.ValueChanged += (v) =>
            {
                if (_painter != null && _currentTool == BrushToolMode.Paint) _painter.BrushFlow = (float)v;
                if (_lblBrushFlow != null) _lblBrushFlow.Text = $"{Mathf.RoundToInt(v * 100)}%";
            };
        }

        if (_sliderBrushHardness != null)
        {
            _sliderBrushHardness.ValueChanged += (v) =>
            {
                if (_painter != null && _currentTool == BrushToolMode.Paint) _painter.BrushHardness = (float)v;
                if (_lblBrushHardness != null) _lblBrushHardness.Text = $"{Mathf.RoundToInt(v * 100)}%";
            };
        }

        // Eraser parameters
        if (_sliderEraseSize != null)
        {
            _sliderEraseSize.ValueChanged += (v) =>
            {
                if (_painter != null && _currentTool == BrushToolMode.Erase) _painter.BrushSize = (float)v;
                if (_lblEraseSize != null) _lblEraseSize.Text = $"{Mathf.RoundToInt(v)} px";
            };
        }

        if (_sliderEraseHardness != null)
        {
            _sliderEraseHardness.ValueChanged += (v) =>
            {
                if (_painter != null && _currentTool == BrushToolMode.Erase) _painter.BrushHardness = (float)v;
                if (_lblEraseHardness != null) _lblEraseHardness.Text = $"{Mathf.RoundToInt(v * 100)}%";
            };
        }

        if (_sliderEraseFlow != null)
        {
            _sliderEraseFlow.ValueChanged += (v) =>
            {
                if (_painter != null && _currentTool == BrushToolMode.Erase) _painter.BrushFlow = (float)v;
                if (_lblEraseFlow != null) _lblEraseFlow.Text = $"{Mathf.RoundToInt(v * 100)}%";
            };
        }

        // Bucket Fill
        if (_fillColorPicker != null)
        {
            _fillColorPicker.ColorChanged += (c) =>
            {
                if (_painter != null) _painter.BrushColor = c;
                if (_colorPicker != null) _colorPicker.Color = c;
            };
        }

        if (_btnFillActiveSubmesh != null)
        {
            _btnFillActiveSubmesh.Pressed += () =>
            {
                Color fillCol = _fillColorPicker?.Color ?? _painter?.BrushColor ?? Colors.White;
                EmitSignal(SignalName.FillRequested, fillCol);
            };
        }

        // Magic Wand
        if (_sliderWandTolerance != null)
        {
            _sliderWandTolerance.ValueChanged += (v) =>
            {
                if (_lblWandTolerance != null) _lblWandTolerance.Text = $"{v:F3}";
                _painter?.MagicWandTool?.RecomputeWithTolerance((float)v);
            };
        }

        if (_chkContiguous != null)
        {
            _chkContiguous.Toggled += (contiguous) =>
            {
                if (_painter?.MagicWandTool != null)
                {
                    _painter.MagicWandTool.Contiguous = contiguous;
                    if (_painter.MagicWandTool.HasSelection)
                    {
                        _painter.MagicWandTool.RecomputeWithTolerance(_painter.MagicWandTool.Tolerance);
                    }
                }
            };
        }

        if (_chkUseMask != null)
        {
            _chkUseMask.Toggled += (active) =>
            {
                if (_painter?.MagicWandTool != null) _painter.MagicWandTool.UseSelectionMask = active;
                _painter?.SyncSelectionMaskState();
            };
        }

        if (_chkIsolateSubmesh != null)
        {
            _chkIsolateSubmesh.Toggled += (isolate) =>
            {
                if (_painter?.MagicWandTool != null)
                {
                    _painter.MagicWandTool.IsolateSubmesh = isolate;
                    if (_painter.MagicWandTool.HasSelection)
                    {
                        _painter.MagicWandTool.RecomputeWithTolerance(_painter.MagicWandTool.Tolerance);
                    }
                }
            };
        }

        if (_btnClearMask != null)
        {
            _btnClearMask.Pressed += () =>
            {
                _painter?.MagicWandTool?.ClearMask();
                _painter?.SyncSelectionMaskState();
                UpdateWandUI();
            };
        }

        // Decal Stamper
        if (_btnImportDecal != null && _decalFileDialog != null)
        {
            _btnImportDecal.Pressed += () => _decalFileDialog.PopupCentered();
            _decalFileDialog.FileSelected += OnDecalFileSelected;
        }

        if (_sliderDecalScale != null)
        {
            _sliderDecalScale.ValueChanged += (v) =>
            {
                if (_decalStamper != null)
                {
                    _decalStamper.DecalScale = (float)v;
                    _decalStamper.UpdatePreviewTransform();
                }
                if (_lblDecalScale != null) _lblDecalScale.Text = $"{v:F2}x";
            };
        }

        if (_sliderDecalRot != null)
        {
            _sliderDecalRot.ValueChanged += (v) =>
            {
                if (_decalStamper != null)
                {
                    _decalStamper.RotationDegrees = (float)v;
                    _decalStamper.UpdatePreviewTransform();
                }
                if (_lblDecalRot != null) _lblDecalRot.Text = $"{Mathf.RoundToInt(v)}°";
            };
        }

        if (_btnBakeDecal != null)
        {
            _btnBakeDecal.Pressed += () =>
            {
                if (_decalStamper != null && _decalStamper.BakeToActiveLayer())
                {
                    EmitSignal(SignalName.DecalBaked);
                }
            };
        }

        // Text Projector
        PopulateSystemFontsDropdown();

        if (_editText != null)
        {
            _editText.TextChanged += (txt) =>
            {
                if (_textProjector == null) return;
                if (!_textProjector.CanAddMoreText(txt))
                {
                    string fitted = _textProjector.ClampTextToFit(txt);
                    if (fitted != txt)
                    {
                        _editText.Text = fitted;
                        _editText.CaretColumn = fitted.Length;
                        txt = fitted;
                    }
                }
                _textProjector.Text = txt;
            };
        }

        if (_btnLoadFont != null && _textFileDialog != null)
        {
            _btnLoadFont.Pressed += () => _textFileDialog.PopupCentered();
            _textFileDialog.FileSelected += (path) =>
            {
                if (_textProjector != null && _textProjector.LoadFontFromFile(path))
                {
                    string name = Path.GetFileName(path);
                    _btnLoadFont.TooltipText = $"Loaded: {name}";
                    if (_optSystemFont != null)
                    {
                        _optSystemFont.AddItem($"Custom: {name}", _optSystemFont.ItemCount);
                        _optSystemFont.Select(_optSystemFont.ItemCount - 1);
                    }
                }
            };
        }

        if (_sliderFontSize != null)
        {
            _sliderFontSize.ValueChanged += (v) =>
            {
                if (_textProjector != null)
                {
                    _textProjector.FontSize = (int)v;
                    string curr = _editText?.Text ?? "";
                    if (!_textProjector.CanAddMoreText(curr))
                    {
                        string fitted = _textProjector.ClampTextToFit(curr);
                        if (_editText != null && fitted != curr)
                        {
                            _editText.Text = fitted;
                            _textProjector.Text = fitted;
                        }
                    }
                }
                if (_lblFontSize != null) _lblFontSize.Text = $"{Mathf.RoundToInt(v)} px";
            };
        }

        if (_pickerTextColor != null)
        {
            _pickerTextColor.ColorChanged += (c) =>
            {
                if (_textProjector != null) _textProjector.TextColor = c;
            };
        }

        if (_sliderOutlineSize != null)
        {
            _sliderOutlineSize.ValueChanged += (v) =>
            {
                if (_textProjector != null) _textProjector.OutlineSize = (int)v;
                if (_lblOutlineSize != null) _lblOutlineSize.Text = $"{Mathf.RoundToInt(v)} px";
            };
        }

        if (_pickerOutlineColor != null)
        {
            _pickerOutlineColor.ColorChanged += (c) =>
            {
                if (_textProjector != null) _textProjector.OutlineColor = c;
            };
        }

        if (_sliderTextScale != null)
        {
            _sliderTextScale.ValueChanged += (v) =>
            {
                if (_textProjector != null) _textProjector.TextScale = (float)v;
                if (_lblTextScale != null) _lblTextScale.Text = $"{v:F2}x";
            };
        }

        if (_sliderTextRot != null)
        {
            _sliderTextRot.ValueChanged += (v) =>
            {
                if (_textProjector != null) _textProjector.RotationDegrees = (float)v;
                if (_lblTextRot != null) _lblTextRot.Text = $"{Mathf.RoundToInt(v)}°";
            };
        }

        if (_btnBakeText != null)
        {
            _btnBakeText.Pressed += () =>
            {
                if (_textProjector != null && _textProjector.BakeToActiveLayer())
                {
                    EmitSignal(SignalName.DecalBaked);
                }
            };
        }

        // Dragging support
        if (_dragHandle != null)
        {
            _dragHandle.GuiInput += OnDragHandleGuiInput;
        }
    }

    private void OnDragHandleGuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed)
            {
                _isDragging = true;
                _dragOffset = GetGlobalMousePosition() - GlobalPosition;
            }
            else
            {
                _isDragging = false;
            }
        }
        else if (@event is InputEventMouseMotion && _isDragging)
        {
            GlobalPosition = GetGlobalMousePosition() - _dragOffset;
        }
    }

    private void PopulateSystemFontsDropdown()
    {
        if (_optSystemFont == null) return;
        _optSystemFont.Clear();
        _optSystemFont.AddItem("Colus (Theme)", 0);

        try
        {
            var systemFonts = OS.GetSystemFonts();
            if (systemFonts != null && systemFonts.Length > 0)
            {
                var prioritized = new string[] { "Arial", "Impact", "Segoe UI", "Consolas", "Times New Roman", "Comic Sans MS", "Calibri", "Verdana", "Tahoma" };
                var fontSet = new HashSet<string>(systemFonts, StringComparer.OrdinalIgnoreCase);

                int id = 1;
                foreach (var pFont in prioritized)
                {
                    if (fontSet.Contains(pFont))
                    {
                        _optSystemFont.AddItem(pFont, id++);
                        fontSet.Remove(pFont);
                    }
                }

                var remaining = fontSet.OrderBy(f => f).Take(40);
                foreach (var rFont in remaining)
                {
                    _optSystemFont.AddItem(rFont, id++);
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[FloatingBrushPaletteUI] Error fetching system fonts: {ex.Message}");
        }

        _optSystemFont.Select(0);
        _optSystemFont.ItemSelected += (idx) =>
        {
            if (idx == 0)
            {
                if (_textProjector != null) _textProjector.CustomFont = null;
            }
            else
            {
                string fontName = _optSystemFont.GetItemText((int)idx);
                if (fontName.StartsWith("Custom: ")) return;
                var sysFont = new SystemFont { FontNames = new string[] { fontName } };
                if (_textProjector != null) _textProjector.CustomFont = sysFont;
            }
        };
    }

    private void OnDecalFileSelected(string path)
    {
        if (_decalStamper == null) return;
        if (_decalStamper.LoadDecalFromFile(path))
        {
            if (_decalPreview != null)
            {
                _decalPreview.Texture = _decalStamper.DecalTexture;
            }
        }
    }

    private void OnToolButtonClicked(BrushToolMode mode)
    {
        if (_currentTool == mode && _flyoutPanel != null && _flyoutPanel.Visible && !IsPinned)
        {
            _flyoutPanel.Visible = false;
            return;
        }

        SelectTool(mode, forceFlyoutOpen: true);
        if (_flyoutPanel != null)
        {
            _flyoutPanel.Visible = true;
        }
    }

    public void CloseFlyoutIfUnpinned()
    {
        if (!IsPinned && _flyoutPanel != null && _flyoutPanel.Visible)
        {
            _flyoutPanel.Visible = false;
        }
    }

    public void SelectDecalTool()
    {
        SelectTool(BrushToolMode.Decal, forceFlyoutOpen: true);
    }

    public void SelectTool(BrushToolMode mode, bool forceFlyoutOpen = false)
    {
        _currentTool = mode;

        if (_painter != null)
        {
            _painter.ToolMode = mode;
            if (mode == BrushToolMode.Erase)
            {
                if (_sliderEraseSize != null) _painter.BrushSize = (float)_sliderEraseSize.Value;
                if (_sliderEraseHardness != null) _painter.BrushHardness = (float)_sliderEraseHardness.Value;
                if (_sliderEraseFlow != null) _painter.BrushFlow = (float)_sliderEraseFlow.Value;
            }
            else if (mode == BrushToolMode.Paint)
            {
                if (_sliderBrushSize != null) _painter.BrushSize = (float)_sliderBrushSize.Value;
                if (_sliderBrushHardness != null) _painter.BrushHardness = (float)_sliderBrushHardness.Value;
                if (_sliderBrushFlow != null) _painter.BrushFlow = (float)_sliderBrushFlow.Value;
            }
            _painter.SyncCameraBrushProperties(force: true);
        }

        // Update button visual states
        UpdateButtonState(_btnToolSelect, mode == BrushToolMode.SelectSubmesh);
        UpdateButtonState(_btnToolBrush, mode == BrushToolMode.Paint);
        UpdateButtonState(_btnToolErase, mode == BrushToolMode.Erase);
        UpdateButtonState(_btnToolFill, mode == BrushToolMode.BucketFill);
        UpdateButtonState(_btnToolWand, mode == BrushToolMode.MagicWand);
        UpdateButtonState(_btnToolDecal, mode == BrushToolMode.Decal);
        UpdateButtonState(_btnToolText, mode == BrushToolMode.Text);

        UpdateSelectTargetLabel();
        UpdateFlyoutSections(mode);

        if (forceFlyoutOpen && _flyoutPanel != null)
        {
            _flyoutPanel.Visible = true;
        }

        EmitSignal(SignalName.ToolChanged, (int)mode);
    }

    private void UpdateButtonState(Button btn, bool active)
    {
        if (btn == null) return;
        btn.ButtonPressed = active;
        btn.AddThemeStyleboxOverride("normal", active ? _activeButtonStyle : _normalButtonStyle);
        btn.AddThemeStyleboxOverride("pressed", _activeButtonStyle);
        btn.AddThemeStyleboxOverride("hover", active ? _activeButtonStyle : _normalButtonStyle);
        btn.Modulate = active ? new Color(1.0f, 0.85f, 0.4f, 1.0f) : new Color(1.0f, 1.0f, 1.0f, 0.75f);
    }

    private void UpdateFlyoutSections(BrushToolMode mode)
    {
        if (_selectOptions != null) _selectOptions.Visible = (mode == BrushToolMode.SelectSubmesh);
        if (_brushOptions != null) _brushOptions.Visible = (mode == BrushToolMode.Paint);
        if (_eraserOptions != null) _eraserOptions.Visible = (mode == BrushToolMode.Erase);
        if (_bucketFillOptions != null) _bucketFillOptions.Visible = (mode == BrushToolMode.BucketFill);
        if (_wandOptions != null) _wandOptions.Visible = (mode == BrushToolMode.MagicWand);
        if (_decalOptions != null) _decalOptions.Visible = (mode == BrushToolMode.Decal);
        if (_textOptions != null) _textOptions.Visible = (mode == BrushToolMode.Text);

        if (_lblFlyoutTitle != null)
        {
            _lblFlyoutTitle.Text = mode switch
            {
                BrushToolMode.SelectSubmesh => "SUBMESH SELECTOR",
                BrushToolMode.Paint => "BRUSH OPTIONS",
                BrushToolMode.Erase => "ERASER OPTIONS",
                BrushToolMode.BucketFill => "BUCKET FILL",
                BrushToolMode.MagicWand => "MAGIC WAND MASK",
                BrushToolMode.Decal => "DECAL STAMPER",
                BrushToolMode.Text => "TEXT PROJECTOR",
                _ => "TOOL OPTIONS"
            };
        }
    }

    public void SetUniversalColor(Color color)
    {
        if (_painter != null) _painter.BrushColor = color;
        if (_btnColor != null) _btnColor.Color = color;
        if (_colorPicker != null) _colorPicker.Color = color;
        if (_fillColorPicker != null) _fillColorPicker.Color = color;
    }

    private void OnMirrorToggled(bool enabled)
    {
        if (_painter != null) _painter.IsMirroringEnabled = enabled;
        if (_btnMirror != null)
        {
            _btnMirror.Modulate = enabled ? new Color(1.0f, 0.85f, 0.4f, 1.0f) : new Color(1.0f, 1.0f, 1.0f, 0.75f);
        }
    }

public void UpdateTooltipsAndKeymaps()
    {
        if (_btnToolSelect != null) _btnToolSelect.TooltipText = "Select Submesh\nClick any submesh in 3D viewport to select";
        if (_btnToolBrush != null) _btnToolBrush.TooltipText = $"Paint Brush [{KeybindsManager.GetShortcutText("paint_brush")}]";
        if (_btnToolErase != null) _btnToolErase.TooltipText = $"Eraser [{KeybindsManager.GetShortcutText("paint_eraser")}]";
        if (_btnToolFill != null) _btnToolFill.TooltipText = $"Bucket Fill Submesh [{KeybindsManager.GetShortcutText("paint_bucket")}]";
        if (_btnToolWand != null) _btnToolWand.TooltipText = $"Magic Wand Color Mask [{KeybindsManager.GetShortcutText("paint_wand")}]";
        if (_btnToolDecal != null) _btnToolDecal.TooltipText = $"Decal Stamper [{KeybindsManager.GetShortcutText("paint_decal")}]";
        if (_btnToolText != null) _btnToolText.TooltipText = $"Text Projector [{KeybindsManager.GetShortcutText("paint_text")}]";
        if (_btnMirror != null) _btnMirror.TooltipText = $"Mirror Symmetry [{KeybindsManager.GetShortcutText("paint_mirror")}]";
        if (_btnUndo != null) _btnUndo.TooltipText = $"Undo [{KeybindsManager.GetShortcutText("paint_undo")}]";
        if (_btnRedo != null) _btnRedo.TooltipText = $"Redo [{KeybindsManager.GetShortcutText("paint_redo")}]";
        if (_btnClear != null) _btnClear.TooltipText = "Clear Active Layer";

        if (_lblKeymapHint != null)
        {
            _lblKeymapHint.Text = $"[{KeybindsManager.GetShortcutText("paint_brush")}] Brush   " +
                                  $"[{KeybindsManager.GetShortcutText("paint_eraser")}] Erase   " +
                                  $"[{KeybindsManager.GetShortcutText("paint_bucket")}] Fill   " +
                                  $"[{KeybindsManager.GetShortcutText("paint_wand")}] Wand   " +
                                  $"[{KeybindsManager.GetShortcutText("paint_decal")}] Decal   " +
                                  $"[{KeybindsManager.GetShortcutText("paint_text")}] Text   " +
                                  $"[{KeybindsManager.GetShortcutText("paint_mirror")}] Mirror";
        }
    }

    private void OnColorSampled(Color color)
    {
        SetUniversalColor(color);
        UpdateWandUI();
        if (_currentTool != BrushToolMode.MagicWand)
        {
            SelectTool(BrushToolMode.Paint, forceFlyoutOpen: false);
        }
    }

    public void UpdateWandUI()
    {
        if (_painter?.MagicWandTool == null) return;
        var wand = _painter.MagicWandTool;
        if (_rectWandColorPreview != null)
        {
            _rectWandColorPreview.Color = wand.HasSelection ? wand.TargetColor : Colors.Transparent;
        }
        if (_lblWandColorHex != null)
        {
            _lblWandColorHex.Text = wand.HasSelection ? $"#{wand.TargetColor.ToHtml(false).ToUpper()}" : "Click mesh to sample";
        }
        if (_sliderWandTolerance != null)
        {
            _sliderWandTolerance.Value = wand.Tolerance;
        }
        if (_lblWandTolerance != null)
        {
            _lblWandTolerance.Text = $"{wand.Tolerance:F3}";
        }
        if (_chkContiguous != null)
        {
            _chkContiguous.ButtonPressed = wand.Contiguous;
        }
        if (_chkUseMask != null)
        {
            _chkUseMask.ButtonPressed = wand.UseSelectionMask;
        }
        if (_chkIsolateSubmesh != null)
        {
            _chkIsolateSubmesh.ButtonPressed = wand.IsolateSubmesh;
        }
    }

    public void SyncFromPainter()
    {
        if (_painter == null) return;

        if (_btnColor != null) _btnColor.Color = _painter.BrushColor;
        if (_colorPicker != null) _colorPicker.Color = _painter.BrushColor;
        if (_fillColorPicker != null) _fillColorPicker.Color = _painter.BrushColor;
        if (_sliderBrushSize != null) _sliderBrushSize.Value = _painter.BrushSize;
        if (_sliderBrushHardness != null) _sliderBrushHardness.Value = _painter.BrushHardness;
        if (_sliderBrushFlow != null) _sliderBrushFlow.Value = _painter.BrushFlow;
        if (_optBrushShape != null) _optBrushShape.Select((int)_painter.BrushShape);
        if (_optEraseShape != null) _optEraseShape.Select((int)_painter.EraserShape);
        if (_optBlendMode != null) _optBlendMode.Select(_painter.BlendMode);
        UpdateSelectTargetLabel();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Visible || IsPinned || _flyoutPanel == null || !_flyoutPanel.Visible) return;

        if (@event is InputEventMouseButton mb && mb.Pressed)
        {
            if (!GetGlobalRect().HasPoint(mb.GlobalPosition))
            {
                _flyoutPanel.Visible = false;
            }
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (!Visible || @event is not InputEventKey keyEvent || !keyEvent.Pressed || keyEvent.Echo) return;

        if (@event.IsActionPressed("paint_undo"))
        {
            EmitSignal(SignalName.UndoRequested);
            GetViewport()?.SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("paint_redo"))
        {
            EmitSignal(SignalName.RedoRequested);
            GetViewport()?.SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("paint_brush"))
        {
            SelectTool(BrushToolMode.Paint, forceFlyoutOpen: true);
            GetViewport()?.SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("paint_eraser"))
        {
            SelectTool(BrushToolMode.Erase, forceFlyoutOpen: true);
            GetViewport()?.SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("paint_bucket"))
        {
            SelectTool(BrushToolMode.BucketFill, forceFlyoutOpen: true);
            GetViewport()?.SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("paint_wand"))
        {
            SelectTool(BrushToolMode.MagicWand, forceFlyoutOpen: true);
            GetViewport()?.SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("paint_decal"))
        {
            SelectTool(BrushToolMode.Decal, forceFlyoutOpen: true);
            GetViewport()?.SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("paint_text"))
        {
            SelectTool(BrushToolMode.Text, forceFlyoutOpen: true);
            GetViewport()?.SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("paint_mirror"))
        {
            if (_btnMirror != null)
            {
                _btnMirror.ButtonPressed = !_btnMirror.ButtonPressed;
                OnMirrorToggled(_btnMirror.ButtonPressed);
            }
            GetViewport()?.SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("brush_size_down"))
        {
            if (_sliderBrushSize != null) _sliderBrushSize.Value = Mathf.Max(1.0f, _sliderBrushSize.Value - 5.0f);
            if (_sliderEraseSize != null) _sliderEraseSize.Value = Mathf.Max(1.0f, _sliderEraseSize.Value - 5.0f);
            GetViewport()?.SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("brush_size_up"))
        {
            if (_sliderBrushSize != null) _sliderBrushSize.Value = Mathf.Min(256.0f, _sliderBrushSize.Value + 5.0f);
            if (_sliderEraseSize != null) _sliderEraseSize.Value = Mathf.Min(256.0f, _sliderEraseSize.Value + 5.0f);
            GetViewport()?.SetInputAsHandled();
            return;
        }

        // Fallback for submesh select tool
        if (keyEvent.Keycode == Key.V && !keyEvent.CtrlPressed && !keyEvent.AltPressed)
        {
            SelectTool(BrushToolMode.SelectSubmesh, forceFlyoutOpen: true);
            GetViewport()?.SetInputAsHandled();
            return;
        }
    }

    private void OnPainterTargetMeshChanged(MeshInstance3D mesh, int surfaceIndex)
    {
        UpdateSelectTargetLabel();
    }

    public void UpdateSelectTargetLabel()
    {
        if (_lblSelectTarget == null) return;
        string name = _painter?.MeshHierarchy?.ActiveTarget?.DisplayName
                   ?? _painter?.CurrentMesh?.Name.ToString()
                   ?? "None";
        _lblSelectTarget.Text = $"Target: {name}";
    }

    public override void _ExitTree()
    {
        KeybindsManager.OnKeybindsChanged -= UpdateTooltipsAndKeymaps;
        if (_painter != null && GodotObject.IsInstanceValid(_painter))
        {
            _painter.ColorSampled -= OnColorSampled;
            if (_painter.MeshHierarchy != null)
            {
                _painter.MeshHierarchy.TargetMeshChanged -= OnPainterTargetMeshChanged;
            }
        }
        base._ExitTree();
    }
}