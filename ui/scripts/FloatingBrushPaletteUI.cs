using Godot;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using DeadlockPlayground.Painter;

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
    private Button _btnToolDecal;
    private Button _btnToolText;
    private ColorPickerButton _btnColor;
    private Button _btnMirror;
    private Button _btnUndo;
    private Button _btnRedo;
    private Button _btnClear;
    private Button _btnToggleFlyout;
    private Button _btnPin;

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

    public override void _Ready()
    {
        CreateButtonStyles();
        LinkControls();
        ConnectEvents();
        if (_btnPin != null)
        {
            _btnPin.ToggleMode = true;
            _btnPin.ButtonPressed = IsPinned;
            UpdatePinVisuals();
        }
        SelectTool(BrushToolMode.Paint, forceFlyoutOpen: true);
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
            BorderColor = new Color(0.95f, 0.8f, 0.4f, 1.0f), // Warm gold accent
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
            SyncFromPainter();
        }
    }

    private void LinkControls()
    {
        // Strip buttons
        _dragHandle = GetNodeOrNull<Button>("HBoxContainer/ToolStripPanel/Margin/ToolButtons/DragHandle");
        _btnToolSelect = GetNodeOrNull<Button>("HBoxContainer/ToolStripPanel/Margin/ToolButtons/BtnSelect");
        _btnToolBrush = GetNodeOrNull<Button>("HBoxContainer/ToolStripPanel/Margin/ToolButtons/BtnBrush");
        _btnToolErase = GetNodeOrNull<Button>("HBoxContainer/ToolStripPanel/Margin/ToolButtons/BtnErase");
        _btnToolFill = GetNodeOrNull<Button>("HBoxContainer/ToolStripPanel/Margin/ToolButtons/BtnFill");
        _btnToolDecal = GetNodeOrNull<Button>("HBoxContainer/ToolStripPanel/Margin/ToolButtons/BtnDecal");
        _btnToolText = GetNodeOrNull<Button>("HBoxContainer/ToolStripPanel/Margin/ToolButtons/BtnText");
        _btnColor = GetNodeOrNull<ColorPickerButton>("HBoxContainer/ToolStripPanel/Margin/ToolButtons/BtnColor");
        _btnMirror = GetNodeOrNull<Button>("HBoxContainer/ToolStripPanel/Margin/ToolButtons/BtnMirror");

        _btnUndo = GetNodeOrNull<Button>("HBoxContainer/ToolStripPanel/Margin/ToolButtons/BtnUndo");
        _btnRedo = GetNodeOrNull<Button>("HBoxContainer/ToolStripPanel/Margin/ToolButtons/BtnRedo");
        _btnClear = GetNodeOrNull<Button>("HBoxContainer/ToolStripPanel/Margin/ToolButtons/BtnClear");
        _btnToggleFlyout = GetNodeOrNull<Button>("HBoxContainer/ToolStripPanel/Margin/ToolButtons/BtnToggleFlyout");
        _btnPin = GetNodeOrNull<Button>("HBoxContainer/ToolStripPanel/Margin/ToolButtons/BtnPin");

        // Flyout container
        _flyoutPanel = GetNodeOrNull<Control>("HBoxContainer/FlyoutPanel");
        _lblFlyoutTitle = GetNodeOrNull<Label>("HBoxContainer/FlyoutPanel/Margin/VBox/HeaderBar/Title");
        _btnCloseFlyout = GetNodeOrNull<Button>("HBoxContainer/FlyoutPanel/Margin/VBox/HeaderBar/BtnCloseFlyout");

        // Sections
        _selectOptions = GetNodeOrNull<Control>("HBoxContainer/FlyoutPanel/Margin/VBox/SelectOptions");
        _lblSelectTarget = GetNodeOrNull<Label>("HBoxContainer/FlyoutPanel/Margin/VBox/SelectOptions/CurrentTargetLabel");
        _brushOptions = GetNodeOrNull<Control>("HBoxContainer/FlyoutPanel/Margin/VBox/BrushOptions");
        _eraserOptions = GetNodeOrNull<Control>("HBoxContainer/FlyoutPanel/Margin/VBox/EraserOptions");
        _bucketFillOptions = GetNodeOrNull<Control>("HBoxContainer/FlyoutPanel/Margin/VBox/BucketFillOptions");
        _decalOptions = GetNodeOrNull<Control>("HBoxContainer/FlyoutPanel/Margin/VBox/DecalOptions");
        _textOptions = GetNodeOrNull<Control>("HBoxContainer/FlyoutPanel/Margin/VBox/TextOptions");

        // Brush controls
        _optBlendMode = GetNodeOrNull<OptionButton>("HBoxContainer/FlyoutPanel/Margin/VBox/BrushOptions/BlendRow/OptBlendMode");
        _colorPicker = GetNodeOrNull<ColorPickerButton>("HBoxContainer/FlyoutPanel/Margin/VBox/BrushOptions/ColorRow/ColorPickerButton");
        _optBrushShape = GetNodeOrNull<OptionButton>("HBoxContainer/FlyoutPanel/Margin/VBox/BrushOptions/ShapeRow/OptShape");
        _sliderBrushSize = GetNodeOrNull<HSlider>("HBoxContainer/FlyoutPanel/Margin/VBox/BrushOptions/SizeRow/SliderSize");
        _lblBrushSize = GetNodeOrNull<Label>("HBoxContainer/FlyoutPanel/Margin/VBox/BrushOptions/SizeRow/LblSize");
        _sliderBrushFlow = GetNodeOrNull<HSlider>("HBoxContainer/FlyoutPanel/Margin/VBox/BrushOptions/FlowRow/SliderFlow");
        _lblBrushFlow = GetNodeOrNull<Label>("HBoxContainer/FlyoutPanel/Margin/VBox/BrushOptions/FlowRow/LblFlow");
        _sliderBrushHardness = GetNodeOrNull<HSlider>("HBoxContainer/FlyoutPanel/Margin/VBox/BrushOptions/HardnessRow/SliderHardness");
        _lblBrushHardness = GetNodeOrNull<Label>("HBoxContainer/FlyoutPanel/Margin/VBox/BrushOptions/HardnessRow/LblHardness");

        // Eraser controls
        _optEraseShape = GetNodeOrNull<OptionButton>("HBoxContainer/FlyoutPanel/Margin/VBox/EraserOptions/EraseShapeRow/OptEraseShape");
        _sliderEraseSize = GetNodeOrNull<HSlider>("HBoxContainer/FlyoutPanel/Margin/VBox/EraserOptions/EraseSizeRow/SliderEraseSize");
        _lblEraseSize = GetNodeOrNull<Label>("HBoxContainer/FlyoutPanel/Margin/VBox/EraserOptions/EraseSizeRow/LblEraseSize");
        _sliderEraseHardness = GetNodeOrNull<HSlider>("HBoxContainer/FlyoutPanel/Margin/VBox/EraserOptions/EraseHardnessRow/SliderEraseHardness");
        _lblEraseHardness = GetNodeOrNull<Label>("HBoxContainer/FlyoutPanel/Margin/VBox/EraserOptions/EraseHardnessRow/LblEraseHardness");
        _sliderEraseFlow = GetNodeOrNull<HSlider>("HBoxContainer/FlyoutPanel/Margin/VBox/EraserOptions/EraseFlowRow/SliderEraseFlow");
        _lblEraseFlow = GetNodeOrNull<Label>("HBoxContainer/FlyoutPanel/Margin/VBox/EraserOptions/EraseFlowRow/LblEraseFlow");

        // Bucket Fill controls
        _fillColorPicker = GetNodeOrNull<ColorPickerButton>("HBoxContainer/FlyoutPanel/Margin/VBox/BucketFillOptions/FillColorRow/FillColorPicker");
        _btnFillActiveSubmesh = GetNodeOrNull<Button>("HBoxContainer/FlyoutPanel/Margin/VBox/BucketFillOptions/BtnFillActiveSubmesh");

        // Decal controls
        _btnImportDecal = GetNodeOrNull<Button>("HBoxContainer/FlyoutPanel/Margin/VBox/DecalOptions/TopRow/BtnImportDecal");
        _decalPreview = GetNodeOrNull<TextureRect>("HBoxContainer/FlyoutPanel/Margin/VBox/DecalOptions/TopRow/DecalPreview");
        _sliderDecalScale = GetNodeOrNull<HSlider>("HBoxContainer/FlyoutPanel/Margin/VBox/DecalOptions/ScaleRow/SliderDecalScale");
        _lblDecalScale = GetNodeOrNull<Label>("HBoxContainer/FlyoutPanel/Margin/VBox/DecalOptions/ScaleRow/LblDecalScale");
        _sliderDecalRot = GetNodeOrNull<HSlider>("HBoxContainer/FlyoutPanel/Margin/VBox/DecalOptions/RotRow/SliderDecalRot");
        _lblDecalRot = GetNodeOrNull<Label>("HBoxContainer/FlyoutPanel/Margin/VBox/DecalOptions/RotRow/LblDecalRot");
        _btnBakeDecal = GetNodeOrNull<Button>("HBoxContainer/FlyoutPanel/Margin/VBox/DecalOptions/BtnBakeDecal");
        _decalFileDialog = GetNodeOrNull<FileDialog>("DecalFileDialog");

        // Text controls
        _editText = GetNodeOrNull<LineEdit>("HBoxContainer/FlyoutPanel/Margin/VBox/TextOptions/TextStringRow/EditText");
        _optSystemFont = GetNodeOrNull<OptionButton>("HBoxContainer/FlyoutPanel/Margin/VBox/TextOptions/FontRow/OptSystemFont");
        _btnLoadFont = GetNodeOrNull<Button>("HBoxContainer/FlyoutPanel/Margin/VBox/TextOptions/FontRow/BtnLoadFont");
        _sliderFontSize = GetNodeOrNull<HSlider>("HBoxContainer/FlyoutPanel/Margin/VBox/TextOptions/FontSizeRow/SliderFontSize");
        _lblFontSize = GetNodeOrNull<Label>("HBoxContainer/FlyoutPanel/Margin/VBox/TextOptions/FontSizeRow/LblFontSize");
        _pickerTextColor = GetNodeOrNull<ColorPickerButton>("HBoxContainer/FlyoutPanel/Margin/VBox/TextOptions/TextColorRow/PickerTextColor");
        _sliderOutlineSize = GetNodeOrNull<HSlider>("HBoxContainer/FlyoutPanel/Margin/VBox/TextOptions/OutlineSizeRow/SliderOutlineSize");
        _lblOutlineSize = GetNodeOrNull<Label>("HBoxContainer/FlyoutPanel/Margin/VBox/TextOptions/OutlineSizeRow/LblOutlineSize");
        _pickerOutlineColor = GetNodeOrNull<ColorPickerButton>("HBoxContainer/FlyoutPanel/Margin/VBox/TextOptions/OutlineColorRow/PickerOutlineColor");
        _sliderTextScale = GetNodeOrNull<HSlider>("HBoxContainer/FlyoutPanel/Margin/VBox/TextOptions/TextScaleRow/SliderTextScale");
        _lblTextScale = GetNodeOrNull<Label>("HBoxContainer/FlyoutPanel/Margin/VBox/TextOptions/TextScaleRow/LblTextScale");
        _sliderTextRot = GetNodeOrNull<HSlider>("HBoxContainer/FlyoutPanel/Margin/VBox/TextOptions/TextRotRow/SliderTextRot");
        _lblTextRot = GetNodeOrNull<Label>("HBoxContainer/FlyoutPanel/Margin/VBox/TextOptions/TextRotRow/LblTextRot");
        _btnBakeText = GetNodeOrNull<Button>("HBoxContainer/FlyoutPanel/Margin/VBox/TextOptions/BtnBakeText");
        _textFileDialog = GetNodeOrNull<FileDialog>("TextFileDialog");

        // Populate Blend Modes
        if (_optBlendMode != null)
        {
            _optBlendMode.Clear();
            _optBlendMode.AddItem("Normal", 0);
            _optBlendMode.AddItem("Multiply (Shadows)", 1);
            _optBlendMode.AddItem("Overlay", 2);
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
        if (_btnToolDecal != null) _btnToolDecal.Pressed += () => OnToolButtonClicked(BrushToolMode.Decal);
        if (_btnToolText != null) _btnToolText.Pressed += () => OnToolButtonClicked(BrushToolMode.Text);

        if (_btnColor != null)
        {
            _btnColor.ColorChanged += SetUniversalColor;
        }

        if (_btnMirror != null)
        {
            _btnMirror.Toggled += OnMirrorToggled;
        }

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
            // Toggle closed if clicked again when unpinned
            _flyoutPanel.Visible = false;
            return;
        }

        SelectTool(mode, forceFlyoutOpen: true);
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

        // Update button states
        UpdateButtonState(_btnToolSelect, mode == BrushToolMode.SelectSubmesh);
        UpdateButtonState(_btnToolBrush, mode == BrushToolMode.Paint);
        UpdateButtonState(_btnToolErase, mode == BrushToolMode.Erase);
        UpdateButtonState(_btnToolFill, mode == BrushToolMode.BucketFill);
        UpdateButtonState(_btnToolDecal, mode == BrushToolMode.Decal);
        UpdateButtonState(_btnToolText, mode == BrushToolMode.Text);

        UpdateSelectTargetLabel();

        // Update Flyout contents
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
        btn.Modulate = active ? new Color(1.0f, 0.9f, 0.5f, 1.0f) : Colors.White;
    }

    private void UpdateFlyoutSections(BrushToolMode mode)
    {
        if (_selectOptions != null) _selectOptions.Visible = (mode == BrushToolMode.SelectSubmesh);
        if (_brushOptions != null) _brushOptions.Visible = (mode == BrushToolMode.Paint);
        if (_eraserOptions != null) _eraserOptions.Visible = (mode == BrushToolMode.Erase);
        if (_bucketFillOptions != null) _bucketFillOptions.Visible = (mode == BrushToolMode.BucketFill);
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
            _btnMirror.Modulate = enabled ? new Color(1.0f, 0.85f, 0.4f, 1.0f) : Colors.White;
        }
    }

    private void OnColorSampled(Color color)
    {
        SetUniversalColor(color);
        SelectTool(BrushToolMode.Paint, forceFlyoutOpen: false);
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

        bool ctrl = keyEvent.CtrlPressed || keyEvent.MetaPressed;
        bool shift = keyEvent.ShiftPressed;

        if (ctrl && keyEvent.Keycode == Key.Z)
        {
            if (shift)
            {
                EmitSignal(SignalName.RedoRequested);
            }
            else
            {
                EmitSignal(SignalName.UndoRequested);
            }
            GetViewport()?.SetInputAsHandled();
            return;
        }
        else if (ctrl && keyEvent.Keycode == Key.Y)
        {
            EmitSignal(SignalName.RedoRequested);
            GetViewport()?.SetInputAsHandled();
            return;
        }

        switch (keyEvent.Keycode)
        {
            case Key.V:
            case Key.S:
                SelectTool(BrushToolMode.SelectSubmesh, forceFlyoutOpen: true);
                GetViewport()?.SetInputAsHandled();
                break;
            case Key.B:
                SelectTool(BrushToolMode.Paint, forceFlyoutOpen: true);
                GetViewport()?.SetInputAsHandled();
                break;
            case Key.E:
                SelectTool(BrushToolMode.Erase, forceFlyoutOpen: true);
                GetViewport()?.SetInputAsHandled();
                break;
            case Key.G:
                SelectTool(BrushToolMode.BucketFill, forceFlyoutOpen: true);
                GetViewport()?.SetInputAsHandled();
                break;
            case Key.T:
                SelectTool(BrushToolMode.Decal, forceFlyoutOpen: true);
                GetViewport()?.SetInputAsHandled();
                break;
            case Key.X:
                SelectTool(BrushToolMode.Text, forceFlyoutOpen: true);
                GetViewport()?.SetInputAsHandled();
                break;
            case Key.M:
                if (_btnMirror != null)
                {
                    _btnMirror.ButtonPressed = !_btnMirror.ButtonPressed;
                    OnMirrorToggled(_btnMirror.ButtonPressed);
                }
                GetViewport()?.SetInputAsHandled();
                break;
            case Key.Bracketleft:
                if (shift)
                {
                    if (_sliderBrushHardness != null) _sliderBrushHardness.Value = Mathf.Max(0.0f, _sliderBrushHardness.Value - 0.1f);
                }
                else
                {
                    if (_sliderBrushSize != null) _sliderBrushSize.Value = Mathf.Max(1.0f, _sliderBrushSize.Value - 5.0f);
                }
                GetViewport()?.SetInputAsHandled();
                break;
            case Key.Bracketright:
                if (shift)
                {
                    if (_sliderBrushHardness != null) _sliderBrushHardness.Value = Mathf.Min(1.0f, _sliderBrushHardness.Value + 0.1f);
                }
                else
                {
                    if (_sliderBrushSize != null) _sliderBrushSize.Value = Mathf.Min(256.0f, _sliderBrushSize.Value + 5.0f);
                }
                GetViewport()?.SetInputAsHandled();
                break;
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
