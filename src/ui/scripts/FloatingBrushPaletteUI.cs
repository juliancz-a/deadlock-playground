using Godot;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using DeadlockPlayground.Painter;
using DeadlockPlayground.Tools;
using DeadlockPlayground.UI;

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
    private Control _toolStripPanel;
    private Button _dragHandle;
    private Button _btnToolSelect;
    private Button _btnToolBrush;
    private Button _btnToolErase;
    private Button _btnToolFill;
    private Button _btnToolSelection;
    private Button _btnToolWand;
    private Button _btnToolDecal;
    private Button _btnToolText;
    private Button _btnToolShape;
    private ColorPickerButton _btnColor;
    private Button _btnMirror;
    private Button _btnUndo;
    private Button _btnRedo;
    private Button _btnToggleFlyout;
    private Button _btnPin;
    private Label _lblKeymapHint;

    // Selection tool state & controls
    private SelectionToolType _currentSelectionType = SelectionToolType.Rectangular;
    public SelectionToolType CurrentSelectionType => _currentSelectionType;
    private Texture2D _iconRect;
    private Texture2D _iconLasso;
    private Texture2D _iconPoly;
    private Texture2D _iconInvert;
    private Button _btnFlyoutRect;
    private Button _btnFlyoutLasso;
    private Button _btnFlyoutPoly;
    private Button _btnClearSelection;
    private Control _selectionOptions;

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
    private Control _shapeOptions;

    // Shape controls
    private Button _btnShapeSquare;
    private Button _btnShapeCircle;
    private Button _btnShapeLine;
    private OptionButton _optShapeFillMode;
    private HSlider _sliderStrokeWidth;
    private Label _lblStrokeWidth;
    private HSlider _sliderShapeWidth;
    private Label _lblShapeWidth;
    private HSlider _sliderShapeHeight;
    private Label _lblShapeHeight;
    private HSlider _sliderShapeRot;
    private Label _lblShapeRot;
    private Button _btnCommitShape;
    private Button _btnCancelShape;
    private CheckBox _chkFrontFacesOnly;

    // Brush controls
    private OptionButton _optBlendMode;
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
    private HSlider _sliderBucketTolerance;
    private Label _lblBucketTolerance;
    private Button _btnFillActiveSubmesh;

    // Magic Wand controls
    private ColorRect _rectWandColorPreview;
    private Label _lblWandColorHex;
    private HSlider _sliderWandTolerance;
    private Label _lblWandTolerance;
    private CheckBox _chkContiguous;
    private CheckBox _chkUseMask;
    private CheckBox _chkIsolateSubmesh;
    private CheckBox _chkAntiAliasing;
    private Button _btnClearMask;

    // Decal controls
    private Button _btnImportDecal;
    private TextureRect _decalPreview;
    private HSlider _sliderDecalScale;
    private Label _lblDecalScale;
    private HSlider _sliderDecalRot;
    private Label _lblDecalRot;
    private Button _btnBakeDecal;
    private Button _btnCancelDecal;
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
    private Button _btnFlipH;
    private Button _btnFlipV;
    private Button _btnBakeText;
    private Button _btnCancelText;
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
        SelectTool(BrushToolMode.Paint, forceFlyoutOpen: true);

        Resized += () => CallDeferred(nameof(ClampPositionToBounds));
        VisibilityChanged += () =>
        {
            if (Visible) CallDeferred(nameof(ClampPositionToBounds));
        };
        GetParentControl()?.Connect(Control.SignalName.Resized, Callable.From(() => CallDeferred(nameof(ClampPositionToBounds))));
    }

    private void ApplyIdleModulates()
    {
        var idleCol = new Color(1.0f, 1.0f, 1.0f, 0.75f);
        if (_dragHandle != null) _dragHandle.Modulate = idleCol;
        if (_btnToolSelect != null) _btnToolSelect.Modulate = idleCol;
        if (_btnToolBrush != null) _btnToolBrush.Modulate = idleCol;
        if (_btnToolErase != null) _btnToolErase.Modulate = idleCol;
        if (_btnToolFill != null) _btnToolFill.Modulate = idleCol;
        if (_btnToolSelection != null) _btnToolSelection.Modulate = idleCol;
        if (_btnToolWand != null) _btnToolWand.Modulate = idleCol;
        if (_btnToolDecal != null) _btnToolDecal.Modulate = idleCol;
        if (_btnToolText != null) _btnToolText.Modulate = idleCol;
        if (_btnToolShape != null) _btnToolShape.Modulate = idleCol;
        if (_btnMirror != null) _btnMirror.Modulate = idleCol;
        if (_btnUndo != null) _btnUndo.Modulate = idleCol;
        if (_btnRedo != null) _btnRedo.Modulate = idleCol;
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
            if (_painter.ShapeTool != null)
            {
                _painter.ShapeTool.ShapeChanged -= OnShapeToolChanged;
                _painter.ShapeTool.ShapeCommitted -= OnShapeToolCommitted;
                _painter.ShapeTool.ShapeCancelled -= OnShapeToolCancelled;
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
            if (_painter.ShapeTool != null)
            {
                _painter.ShapeTool.ShapeChanged += OnShapeToolChanged;
                _painter.ShapeTool.ShapeCommitted += OnShapeToolCommitted;
                _painter.ShapeTool.ShapeCancelled += OnShapeToolCancelled;
            }
            SyncFromPainter();
        }

        if (_textProjector != null)
        {
            if (_btnFlipH != null) _btnFlipH.ButtonPressed = _textProjector.FlipH;
            if (_btnFlipV != null) _btnFlipV.ButtonPressed = _textProjector.FlipV;
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
        _btnToolSelection = ResolveNode<Button>("BtnSelection", "BtnSelection");
        _btnToolWand = ResolveNode<Button>("BtnWand", "BtnWand");
        _iconRect = GD.Load<Texture2D>("res://assets/at-icons/selection_square.svg");
        _iconLasso = GD.Load<Texture2D>("res://assets/at-icons/lasso.svg");
        _iconPoly = GD.Load<Texture2D>("res://assets/at-icons/mesh_polygon.svg");
        _iconInvert = GD.Load<Texture2D>("res://assets/icons/loop.svg");
        if (_btnToolSelection != null)
        {
            _btnToolSelection.Icon = _iconRect;
            _btnToolSelection.ExpandIcon = true;
            _btnToolSelection.IconAlignment = HorizontalAlignment.Center;
        }
        if (_btnToolWand != null)
        {
            _btnToolWand.Icon = GD.Load<Texture2D>("res://assets/at-icons/magic_wand.svg");
            _btnToolWand.ExpandIcon = true;
            _btnToolWand.IconAlignment = HorizontalAlignment.Center;
        }
        _btnToolDecal = ResolveNode<Button>("BtnDecal", "BtnDecal");
        _btnToolText = ResolveNode<Button>("BtnText", "BtnText");
        _btnToolShape = ResolveNode<Button>("BtnShape", "BtnShape");
        _btnColor = ResolveNode<ColorPickerButton>("BtnColor", "BtnColor");
        _btnMirror = ResolveNode<Button>("BtnMirror", "BtnMirror");

        _btnUndo = ResolveNode<Button>("BtnUndo", "BtnUndo");
        _btnRedo = ResolveNode<Button>("BtnRedo", "BtnRedo");
        _btnToggleFlyout = ResolveNode<Button>("BtnToggleFlyout", "BtnToggleFlyout");
        if (_btnToggleFlyout != null && _btnToggleFlyout.Icon == null)
        {
            _btnToggleFlyout.Icon = GD.Load<Texture2D>("res://assets/at-icons/cog.svg");
            _btnToggleFlyout.ExpandIcon = true;
            _btnToggleFlyout.IconAlignment = HorizontalAlignment.Center;
            _btnToggleFlyout.Text = string.Empty;
        }
        _btnPin = ResolveNode<Button>("BtnPin", "BtnPin");
        _lblKeymapHint = ResolveNode<Label>("KeymapHintLabel", "KeymapHintLabel");

        // Set up Radio ButtonGroup so tools are mutually exclusive
        _toolButtonGroup = new ButtonGroup();
        var mutualButtons = new[] { _btnToolSelect, _btnToolBrush, _btnToolErase, _btnToolFill, _btnToolSelection, _btnToolWand, _btnToolDecal, _btnToolText, _btnToolShape };
        foreach (var btn in mutualButtons)
        {
            if (btn != null)
            {
                btn.ToggleMode = true;
                btn.ButtonGroup = _toolButtonGroup;
            }
        }

        // Toolbar strip & Flyout container
        _toolStripPanel = ResolveNode<Control>("ToolStripPanel", "ToolStripPanel")
                       ?? FindChild("ToolStripPanel", true, false) as Control;
        _flyoutPanel = ResolveNode<Control>("FlyoutPanel", "FlyoutPanel");
        _chkFrontFacesOnly = ResolveNode<CheckBox>("ChkFrontFacesOnly", "ChkFrontFacesOnly");
        if (_flyoutPanel != null)
        {
            _flyoutPanel.CustomMinimumSize = new Vector2(260, 290);
            _flyoutPanel.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        }
        _lblFlyoutTitle = ResolveNode<Label>("Title", "Title");
        _btnCloseFlyout = ResolveNode<Button>("BtnCloseFlyout", "BtnCloseFlyout");

        // Sections
        _selectOptions = ResolveNode<Control>("SelectOptions", "SelectOptions");
        _lblSelectTarget = ResolveNode<Label>("CurrentTargetLabel", "CurrentTargetLabel");
        _brushOptions = ResolveNode<Control>("BrushOptions", "BrushOptions");
        _eraserOptions = ResolveNode<Control>("EraserOptions", "EraserOptions");
        _bucketFillOptions = ResolveNode<Control>("BucketFillOptions", "BucketFillOptions");
        _wandOptions = ResolveNode<Control>("MagicWandOptions", "MagicWandOptions");
        _selectionOptions = ResolveNode<Control>("SelectionOptions", "SelectionOptions");
        _decalOptions = ResolveNode<Control>("DecalOptions", "DecalOptions");
        _textOptions = ResolveNode<Control>("TextOptions", "TextOptions");
        _shapeOptions = ResolveNode<Control>("ShapeOptions", "ShapeOptions");

        // Brush controls
        _optBlendMode = ResolveNode<OptionButton>("OptBlendMode", "OptBlendMode");
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
        _sliderBucketTolerance = ResolveNode<HSlider>("SliderBucketTolerance", "SliderBucketTolerance");
        _lblBucketTolerance = ResolveNode<Label>("LblBucketTolerance", "LblBucketTolerance");
        _btnFillActiveSubmesh = ResolveNode<Button>("BtnFillActiveSubmesh", "BtnFillActiveSubmesh");

        // Decal controls
        _btnImportDecal = ResolveNode<Button>("BtnImportDecal", "BtnImportDecal");
        _decalPreview = ResolveNode<TextureRect>("DecalPreview", "DecalPreview");
        _sliderDecalScale = ResolveNode<HSlider>("SliderDecalScale", "SliderDecalScale");
        _lblDecalScale = ResolveNode<Label>("LblDecalScale", "LblDecalScale");
        _sliderDecalRot = ResolveNode<HSlider>("SliderDecalRot", "SliderDecalRot");
        _lblDecalRot = ResolveNode<Label>("LblDecalRot", "LblDecalRot");
        _btnBakeDecal = ResolveNode<Button>("BtnBakeDecal", "BtnBakeDecal");
        _btnCancelDecal = ResolveNode<Button>("BtnCancelDecal", "BtnCancelDecal");
        _decalFileDialog = ResolveNode<FileDialog>("DecalFileDialog", "DecalFileDialog");
        if (_decalFileDialog != null)
        {
            _decalFileDialog.Transient = true;
            _decalFileDialog.Exclusive = true;
        }

        // Text controls
        _editText = ResolveNode<LineEdit>("EditText", "EditText");
        _optSystemFont = ResolveNode<OptionButton>("OptSystemFont", "OptSystemFont");
        _btnLoadFont = ResolveNode<Button>("BtnLoadFont", "BtnLoadFont");
        if (_btnLoadFont != null && _btnLoadFont.Icon == null)
        {
            _btnLoadFont.Icon = GD.Load<Texture2D>("res://assets/at-icons/folder.svg");
            _btnLoadFont.ExpandIcon = true;
            _btnLoadFont.IconAlignment = HorizontalAlignment.Center;
            _btnLoadFont.Text = string.Empty;
        }
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
        _btnFlipH = ResolveNode<Button>("BtnFlipH", "BtnFlipH");
        _btnFlipV = ResolveNode<Button>("BtnFlipV", "BtnFlipV");
        _btnBakeText = ResolveNode<Button>("BtnBakeText", "BtnBakeText");
        _btnCancelText = ResolveNode<Button>("BtnCancelText", "BtnCancelText");
        _textFileDialog = ResolveNode<FileDialog>("TextFileDialog", "TextFileDialog");
        if (_textFileDialog != null)
        {
            _textFileDialog.Transient = true;
            _textFileDialog.Exclusive = true;
        }

        // Magic Wand controls
        _rectWandColorPreview = ResolveNode<ColorRect>("ColorPreview", "ColorPreview");
        _lblWandColorHex = ResolveNode<Label>("LblColorHex", "LblColorHex");
        _sliderWandTolerance = ResolveNode<HSlider>("SliderTolerance", "SliderTolerance");
        _lblWandTolerance = ResolveNode<Label>("LblTolerance", "LblTolerance");
        _chkContiguous = ResolveNode<CheckBox>("ChkContiguous", "ChkContiguous");
        _chkUseMask = ResolveNode<CheckBox>("ChkUseMask", "ChkUseMask");
        _chkIsolateSubmesh = ResolveNode<CheckBox>("ChkIsolateSubmesh", "ChkIsolateSubmesh");
        _chkAntiAliasing = ResolveNode<CheckBox>("ChkAntiAliasing", "ChkAntiAliasing");
        _btnClearMask = ResolveNode<Button>("BtnClearMask", "BtnClearMask");

        // Shape controls
        _btnShapeSquare = ResolveNode<Button>("BtnShapeSquare", "BtnShapeSquare");
        _btnShapeCircle = ResolveNode<Button>("BtnShapeCircle", "BtnShapeCircle");
        _btnShapeLine = ResolveNode<Button>("BtnShapeLine", "BtnShapeLine");
        _optShapeFillMode = ResolveNode<OptionButton>("OptShapeFillMode", "OptShapeFillMode");
        _sliderStrokeWidth = ResolveNode<HSlider>("SliderStrokeWidth", "SliderStrokeWidth");
        _lblStrokeWidth = ResolveNode<Label>("LblStrokeWidth", "LblStrokeWidth");
        _sliderShapeWidth = ResolveNode<HSlider>("SliderShapeWidth", "SliderShapeWidth");
        _lblShapeWidth = ResolveNode<Label>("LblShapeWidth", "LblShapeWidth");
        _sliderShapeHeight = ResolveNode<HSlider>("SliderShapeHeight", "SliderShapeHeight");
        _lblShapeHeight = ResolveNode<Label>("LblShapeHeight", "LblShapeHeight");
        _sliderShapeRot = ResolveNode<HSlider>("SliderShapeRot", "SliderShapeRot");
        _lblShapeRot = ResolveNode<Label>("LblShapeRot", "LblShapeRot");
        _btnCommitShape = ResolveNode<Button>("BtnCommitShape", "BtnCommitShape");
        _btnCancelShape = ResolveNode<Button>("BtnCancelShape", "BtnCancelShape");

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
            _optBlendMode.AddItem("Color (HSL)", 7);
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

        BuildSelectionFlyoutControls();
        if (_sliderStrokeWidth != null) _sliderStrokeWidth.MaxValue = 256.0f;
        SyncShapeControls();
        SyncProjectionControls();
    }

    private void ConnectEvents()
    {
        // Tool selection
        if (_btnToolSelect != null) _btnToolSelect.Pressed += () => OnToolButtonClicked(BrushToolMode.SelectSubmesh);
        if (_btnToolBrush != null) _btnToolBrush.Pressed += () => OnToolButtonClicked(BrushToolMode.Paint);
        if (_btnToolErase != null) _btnToolErase.Pressed += () => OnToolButtonClicked(BrushToolMode.Erase);
        if (_btnToolFill != null) _btnToolFill.Pressed += () => OnToolButtonClicked(BrushToolMode.BucketFill);
        if (_btnToolSelection != null)
        {
            _btnToolSelection.Pressed += () => OnToolButtonClicked(BrushToolMode.Selection);
        }
        if (_btnToolWand != null)
        {
            _btnToolWand.Pressed += () => OnToolButtonClicked(BrushToolMode.MagicWand);
        }
        if (_btnToolDecal != null) _btnToolDecal.Pressed += () => OnToolButtonClicked(BrushToolMode.Decal);
        if (_btnToolText != null) _btnToolText.Pressed += () => OnToolButtonClicked(BrushToolMode.Text);
        if (_btnToolShape != null) _btnToolShape.Pressed += () => OnToolButtonClicked(BrushToolMode.Shape);

        if (_chkFrontFacesOnly != null)
        {
            _chkFrontFacesOnly.ButtonPressed = _painter?.FrontFacesOnly ?? true;
            _chkFrontFacesOnly.Toggled += (enabled) =>
            {
                if (_painter != null) _painter.FrontFacesOnly = enabled;
            };
        }

        if (_btnColor != null)
        {
            _btnColor.ColorChanged += OnColorPickerColorChanged;
            _btnColor.PopupClosed += OnColorPickerPopupClosed;
            var picker = _btnColor.GetPicker();
            if (picker != null)
            {
                picker.ColorChanged += OnColorPickerColorChanged;
            }
        }
        if (_btnMirror != null) _btnMirror.Toggled += OnMirrorToggled;

        // Action buttons
        if (_btnUndo != null) _btnUndo.Pressed += () => EmitSignal(SignalName.UndoRequested);
        if (_btnRedo != null) _btnRedo.Pressed += () => EmitSignal(SignalName.RedoRequested);

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

        if (_optBrushShape != null)
        {
            _optBrushShape.ItemSelected += (idx) =>
            {
                if (_painter != null)
                {
                    _painter.BrushShape = (BrushShapeType)_optBrushShape.GetItemId((int)idx);
                    _painter.SyncCameraBrushProperties(force: true);
                    QueueCanvasRedraw();
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
                    QueueCanvasRedraw();
                }
            };
        }

        if (_sliderBrushSize != null)
        {
            _sliderBrushSize.ValueChanged += (v) =>
            {
                if (_painter != null && _currentTool == BrushToolMode.Paint) _painter.BrushSize = (float)v;
                if (_lblBrushSize != null) _lblBrushSize.Text = $"{Mathf.RoundToInt(v)} px";
                QueueCanvasRedraw();
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
                QueueCanvasRedraw();
            };
        }

        // Eraser parameters
        if (_sliderEraseSize != null)
        {
            _sliderEraseSize.ValueChanged += (v) =>
            {
                if (_painter != null && _currentTool == BrushToolMode.Erase) _painter.BrushSize = (float)v;
                if (_lblEraseSize != null) _lblEraseSize.Text = $"{Mathf.RoundToInt(v)} px";
                QueueCanvasRedraw();
            };
        }

        if (_sliderEraseHardness != null)
        {
            _sliderEraseHardness.ValueChanged += (v) =>
            {
                if (_painter != null && _currentTool == BrushToolMode.Erase) _painter.BrushHardness = (float)v;
                if (_lblEraseHardness != null) _lblEraseHardness.Text = $"{Mathf.RoundToInt(v * 100)}%";
                QueueCanvasRedraw();
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
        if (_sliderBucketTolerance != null)
        {
            _sliderBucketTolerance.ValueChanged += (v) =>
            {
                if (_painter != null) _painter.BucketFillTolerance = (float)v;
                if (_lblBucketTolerance != null)
                {
                    _lblBucketTolerance.Text = v >= 0.99 ? "100%" : $"{Mathf.RoundToInt(v * 100)}%";
                }
            };
        }

        if (_btnFillActiveSubmesh != null)
        {
            _btnFillActiveSubmesh.Pressed += () =>
            {
                Color fillCol = _btnColor?.Color ?? _painter?.BrushColor ?? Colors.White;
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
                if (_painter?.MagicWandTool != null && _painter.MagicWandTool.Contiguous != contiguous)
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
                if (_painter?.MagicWandTool != null && _painter.MagicWandTool.UseSelectionMask != active)
                {
                    _painter.MagicWandTool.UseSelectionMask = active;
                    _painter.SyncSelectionMaskState();
                }
            };
        }

        if (_chkIsolateSubmesh != null)
        {
            _chkIsolateSubmesh.Toggled += (isolate) =>
            {
                if (_painter?.MagicWandTool != null && _painter.MagicWandTool.IsolateSubmesh != isolate)
                {
                    _painter.MagicWandTool.IsolateSubmesh = isolate;
                    if (_painter.MagicWandTool.HasSelection)
                    {
                        _painter.MagicWandTool.RecomputeWithTolerance(_painter.MagicWandTool.Tolerance);
                    }
                }
            };
        }

        if (_chkAntiAliasing != null)
        {
            _chkAntiAliasing.Toggled += (on) =>
            {
                if (_painter?.MagicWandTool != null && _painter.MagicWandTool.AntiAliasing != on)
                {
                    _painter.MagicWandTool.AntiAliasing = on;
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
                if (_painter != null && _painter.ProjectionGizmo.IsActive && !_painter.ProjectionGizmo.IsText)
                {
                    int atlasSize = _painter.LayerManager?.CanvasSize.X ?? 2048;
                    float baseDim = (float)v * atlasSize;
                    float aspect = 1.0f;
                    if (_painter.ProjectionGizmo.Texture != null && _painter.ProjectionGizmo.Texture.GetHeight() > 0)
                        aspect = (float)_painter.ProjectionGizmo.Texture.GetWidth() / _painter.ProjectionGizmo.Texture.GetHeight();
                    _painter.ProjectionGizmo.Size = aspect >= 1.0f ? new Vector2(baseDim * aspect, baseDim) : new Vector2(baseDim, baseDim / aspect);
                }
                if (_lblDecalScale != null) _lblDecalScale.Text = $"{v:F2}x";
                QueueCanvasRedraw();
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
                if (_painter != null && _painter.ProjectionGizmo.IsActive && !_painter.ProjectionGizmo.IsText)
                {
                    _painter.ProjectionGizmo.RotationDegrees = (float)v;
                }
                if (_lblDecalRot != null) _lblDecalRot.Text = $"{Mathf.RoundToInt(v)}°";
                QueueCanvasRedraw();
            };
        }

        if (_btnBakeDecal != null)
        {
            _btnBakeDecal.Pressed += () =>
            {
                if (_painter != null && _painter.ProjectionGizmo.IsActive && !_painter.ProjectionGizmo.IsText)
                {
                    if (_painter.CommitProjectionGizmo())
                    {
                        EmitSignal(SignalName.DecalBaked);
                    }
                }
                else if (_decalStamper != null && _decalStamper.BakeToActiveLayer())
                {
                    EmitSignal(SignalName.DecalBaked);
                }
                SyncProjectionControls();
                QueueCanvasRedraw();
            };
        }

        if (_btnCancelDecal != null)
        {
            _btnCancelDecal.Pressed += CancelProjectionGizmo;
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
                if (_painter != null && _painter.ProjectionGizmo.IsActive && _painter.ProjectionGizmo.IsText)
                {
                    int atlasSize = _painter.LayerManager?.CanvasSize.X ?? 2048;
                    float baseDim = (float)v * atlasSize;
                    float aspect = 1.0f;
                    if (_painter.ProjectionGizmo.Texture != null && _painter.ProjectionGizmo.Texture.GetHeight() > 0)
                        aspect = (float)_painter.ProjectionGizmo.Texture.GetWidth() / _painter.ProjectionGizmo.Texture.GetHeight();
                    _painter.ProjectionGizmo.Size = aspect >= 1.0f ? new Vector2(baseDim * aspect, baseDim) : new Vector2(baseDim, baseDim / aspect);
                }
                if (_lblTextScale != null) _lblTextScale.Text = $"{v:F2}x";
                QueueCanvasRedraw();
            };
        }

        if (_sliderTextRot != null)
        {
            _sliderTextRot.ValueChanged += (v) =>
            {
                if (_textProjector != null) _textProjector.RotationDegrees = (float)v;
                if (_painter != null && _painter.ProjectionGizmo.IsActive && _painter.ProjectionGizmo.IsText)
                {
                    _painter.ProjectionGizmo.RotationDegrees = (float)v;
                }
                if (_lblTextRot != null) _lblTextRot.Text = $"{Mathf.RoundToInt(v)}°";
                QueueCanvasRedraw();
            };
        }

        if (_btnFlipH != null)
        {
            _btnFlipH.Toggled += (toggled) =>
            {
                if (_textProjector != null) _textProjector.FlipH = toggled;
            };
        }

        if (_btnFlipV != null)
        {
            _btnFlipV.Toggled += (toggled) =>
            {
                if (_textProjector != null) _textProjector.FlipV = toggled;
            };
        }

        if (_btnBakeText != null)
        {
            _btnBakeText.Pressed += () =>
            {
                if (_painter != null && _painter.ProjectionGizmo.IsActive && _painter.ProjectionGizmo.IsText)
                {
                    if (_painter.CommitProjectionGizmo())
                    {
                        EmitSignal(SignalName.DecalBaked);
                    }
                }
                else if (_textProjector != null && _textProjector.BakeToActiveLayer())
                {
                    EmitSignal(SignalName.DecalBaked);
                }
                SyncProjectionControls();
                QueueCanvasRedraw();
            };
        }

        if (_btnCancelText != null)
        {
            _btnCancelText.Pressed += CancelProjectionGizmo;
        }

        // Shape Tool events
        if (_btnShapeSquare != null) _btnShapeSquare.Pressed += () => SetShapeType(CanvasShapeType.Square);
        if (_btnShapeCircle != null) _btnShapeCircle.Pressed += () => SetShapeType(CanvasShapeType.Circle);
        if (_btnShapeLine != null) _btnShapeLine.Pressed += () => SetShapeType(CanvasShapeType.Line);

        if (_optShapeFillMode != null)
        {
            _optShapeFillMode.ItemSelected += (idx) =>
            {
                if (_painter?.ShapeTool != null)
                {
                    _painter.ShapeTool.FillMode = (ShapeFillMode)idx;
                    TriggerShapePreviewUpdate();
                }
            };
        }

        if (_sliderStrokeWidth != null)
        {
            _sliderStrokeWidth.ValueChanged += (v) =>
            {
                if (_painter?.ShapeTool != null) _painter.ShapeTool.StrokeWidth = (float)v;
                if (_lblStrokeWidth != null) _lblStrokeWidth.Text = $"{Mathf.RoundToInt(v)} px";
                TriggerShapePreviewUpdate();
            };
        }

        if (_sliderShapeWidth != null)
        {
            _sliderShapeWidth.ValueChanged += (v) =>
            {
                if (_painter?.ShapeTool != null) _painter.ShapeTool.Width = (float)v;
                if (_lblShapeWidth != null) _lblShapeWidth.Text = $"{Mathf.RoundToInt(v)} px";
                TriggerShapePreviewUpdate();
            };
        }

        if (_sliderShapeHeight != null)
        {
            _sliderShapeHeight.ValueChanged += (v) =>
            {
                if (_painter?.ShapeTool != null) _painter.ShapeTool.Height = (float)v;
                if (_lblShapeHeight != null) _lblShapeHeight.Text = $"{Mathf.RoundToInt(v)} px";
                TriggerShapePreviewUpdate();
            };
        }

        if (_sliderShapeRot != null)
        {
            _sliderShapeRot.ValueChanged += (v) =>
            {
                if (_painter?.ShapeTool != null) _painter.ShapeTool.RotationDegrees = (float)v;
                if (_lblShapeRot != null) _lblShapeRot.Text = $"{Mathf.RoundToInt(v)}°";
                TriggerShapePreviewUpdate();
            };
        }

        if (_btnCommitShape != null) _btnCommitShape.Pressed += CommitCurrentShape;
        if (_btnCancelShape != null) _btnCancelShape.Pressed += CancelCurrentShape;

        // Dragging support
        if (_dragHandle != null)
        {
            _dragHandle.GuiInput += OnDragHandleGuiInput;
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (_isDragging && @event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && !mb.Pressed)
        {
            _isDragging = false;
        }
    }

    private Vector2 ClampGlobalPosition(Vector2 targetPos)
    {
        var parent = GetParentControl();
        Rect2 bounds = parent != null ? parent.GetGlobalRect() : GetViewportRect();

        float minX = bounds.Position.X + 8.0f;
        float maxX = Mathf.Max(minX, bounds.End.X - Size.X - 8.0f);
        float minY = bounds.Position.Y + 8.0f;
        float maxY = Mathf.Max(minY, bounds.End.Y - Size.Y - 8.0f);

        targetPos.X = Mathf.Clamp(targetPos.X, minX, maxX);
        targetPos.Y = Mathf.Clamp(targetPos.Y, minY, maxY);

        Button btnUV = GetNodeOrNull<Button>("../BtnOpenUVCanvas") ?? GetTree()?.Root?.FindChild("BtnOpenUVCanvas", true, false) as Button;
        if (btnUV != null && btnUV.IsVisibleInTree())
        {
            Rect2 uvRect = btnUV.GetGlobalRect().Grow(6.0f);
            Rect2 paletteRect = new Rect2(targetPos, Size);
            if (paletteRect.Intersects(uvRect))
            {
                float overlapY = uvRect.End.Y - targetPos.Y;
                float overlapX = uvRect.End.X - targetPos.X;
                if (overlapY < overlapX)
                {
                    targetPos.Y = Mathf.Clamp(uvRect.End.Y, minY, maxY);
                }
                else
                {
                    targetPos.X = Mathf.Clamp(uvRect.End.X, minX, maxX);
                }
            }
        }

        QuickColorPaletteUI quickPalette = GetNodeOrNull<QuickColorPaletteUI>("../QuickColorPalette")
            ?? GetTree()?.Root?.FindChild("QuickColorPalette", true, false) as QuickColorPaletteUI;
        if (quickPalette != null && quickPalette.IsVisibleInTree())
        {
            Rect2 palRect = quickPalette.GetPaletteGlobalRect().Grow(6.0f);
            Rect2 paletteRect = new Rect2(targetPos, Size);
            if (paletteRect.Intersects(palRect))
            {
                float overlapY = palRect.End.Y - targetPos.Y;
                float overlapX = palRect.End.X - targetPos.X;
                if (overlapY < overlapX)
                {
                    targetPos.Y = Mathf.Clamp(palRect.End.Y, minY, maxY);
                }
                else
                {
                    targetPos.X = Mathf.Clamp(palRect.End.X, minX, maxX);
                }
            }
        }

        return targetPos;
    }

    public void ClampPositionToBounds()
    {
        GlobalPosition = ClampGlobalPosition(GlobalPosition);
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
            GlobalPosition = ClampGlobalPosition(GetGlobalMousePosition() - _dragOffset);
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



    public void SelectSelectionTool(SelectionToolType type, bool forceFlyoutOpen = false)
    {
        _currentSelectionType = type;
        if (_painter?.SelectionMask != null)
        {
            _painter.SelectionMask.CurrentToolType = type;
        }
        UpdateSelectionToolIcon();
        SelectTool(BrushToolMode.Selection, forceFlyoutOpen);
        UpdateSelectionFlyoutActiveState();
    }

    private void UpdateSelectionToolIcon()
    {
        Button targetBtn = _btnToolSelection ?? _btnToolWand;
        if (targetBtn == null) return;
        Texture2D icon = _currentSelectionType switch
        {
            SelectionToolType.Lasso => _iconLasso ?? GD.Load<Texture2D>("res://assets/at-icons/lasso.svg"),
            SelectionToolType.Polygonal => _iconPoly ?? GD.Load<Texture2D>("res://assets/at-icons/mesh_polygon.svg"),
            _ => _iconRect ?? GD.Load<Texture2D>("res://assets/at-icons/selection_square.svg")
        };
        if (icon != null)
        {
            targetBtn.Icon = icon;
        }
    }

    public void InvertSelection()
    {
        if (_painter?.SelectionMask != null)
        {
            _painter.SelectionMask.Invert();
            _painter.SyncSelectionMaskState();
            UpdateWandUI();
            var uvCanvas = GetNodeOrNull<UVCanvas2DUI>("../UVCanvas2D")
                        ?? GetTree()?.Root?.FindChild("UVCanvas2D", true, false) as UVCanvas2DUI;
            uvCanvas?.QueueCanvasRedraw();
        }
    }

    public void ClearSelection()
    {
        if (_painter?.SelectionMask != null)
        {
            _painter.SelectionMask.Clear();
            _painter.SyncSelectionMaskState();
            UpdateWandUI();
            var uvCanvas = GetNodeOrNull<UVCanvas2DUI>("../UVCanvas2D")
                        ?? GetTree()?.Root?.FindChild("UVCanvas2D", true, false) as UVCanvas2DUI;
            uvCanvas?.QueueCanvasRedraw();
        }
    }

    private void BuildSelectionFlyoutControls()
    {
        if (_selectionOptions == null)
        {
            _selectionOptions = ResolveNode<Control>("SelectionOptions", "SelectionOptions");
        }
        if (_selectionOptions == null) return;

        _btnFlyoutRect = _selectionOptions.GetNodeOrNull<Button>("HBoxSelectionModes/BtnModeRect") ?? ResolveNode<Button>("BtnModeRect", "BtnModeRect");
        _btnFlyoutLasso = _selectionOptions.GetNodeOrNull<Button>("HBoxSelectionModes/BtnModeLasso") ?? ResolveNode<Button>("BtnModeLasso", "BtnModeLasso");
        _btnFlyoutPoly = _selectionOptions.GetNodeOrNull<Button>("HBoxSelectionModes/BtnModePoly") ?? ResolveNode<Button>("BtnModePoly", "BtnModePoly");

        if (_btnFlyoutRect != null)
        {
            _btnFlyoutRect.Icon = _iconRect;
            _btnFlyoutRect.Pressed += () => SelectSelectionTool(SelectionToolType.Rectangular);
        }
        if (_btnFlyoutLasso != null)
        {
            _btnFlyoutLasso.Icon = _iconLasso;
            _btnFlyoutLasso.Pressed += () => SelectSelectionTool(SelectionToolType.Lasso);
        }
        if (_btnFlyoutPoly != null)
        {
            _btnFlyoutPoly.Icon = _iconPoly;
            _btnFlyoutPoly.Pressed += () => SelectSelectionTool(SelectionToolType.Polygonal);
        }

        var btnInvert = _selectionOptions.GetNodeOrNull<Button>("HBoxSelectionActions/BtnInvertSelection") ?? ResolveNode<Button>("BtnInvertSelection", "BtnInvertSelection");
        if (btnInvert != null)
        {
            btnInvert.Icon = _iconInvert;
            btnInvert.Pressed += InvertSelection;
        }

        _btnClearSelection = _selectionOptions.GetNodeOrNull<Button>("HBoxSelectionActions/BtnClearSelection") ?? ResolveNode<Button>("BtnClearSelection", "BtnClearSelection");
        if (_btnClearSelection != null)
        {
            _btnClearSelection.Pressed += ClearSelection;
        }

        UpdateSelectionFlyoutActiveState();
    }

    private void UpdateSelectionFlyoutActiveState()
    {
        if (_btnFlyoutRect != null) _btnFlyoutRect.ButtonPressed = (_currentTool == BrushToolMode.Selection && _currentSelectionType == SelectionToolType.Rectangular);
        if (_btnFlyoutLasso != null) _btnFlyoutLasso.ButtonPressed = (_currentTool == BrushToolMode.Selection && _currentSelectionType == SelectionToolType.Lasso);
        if (_btnFlyoutPoly != null) _btnFlyoutPoly.ButtonPressed = (_currentTool == BrushToolMode.Selection && _currentSelectionType == SelectionToolType.Polygonal);
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
        UpdateButtonState(_btnToolSelection, mode == BrushToolMode.Selection);
        UpdateButtonState(_btnToolWand, mode == BrushToolMode.MagicWand);
        UpdateButtonState(_btnToolDecal, mode == BrushToolMode.Decal);
        UpdateButtonState(_btnToolText, mode == BrushToolMode.Text);
        UpdateButtonState(_btnToolShape, mode == BrushToolMode.Shape);

        UpdateSelectTargetLabel();
        UpdateFlyoutSections(mode);

        if (mode == BrushToolMode.Selection)
        {
            UpdateSelectionToolIcon();
            UpdateSelectionFlyoutActiveState();
        }
        else if (mode == BrushToolMode.Shape)
        {
            SyncShapeControls();
        }

        if (mode == BrushToolMode.BucketFill && _sliderBucketTolerance != null && _painter != null)
        {
            _sliderBucketTolerance.SetValueNoSignal(_painter.BucketFillTolerance);
            if (_lblBucketTolerance != null)
            {
                _lblBucketTolerance.Text = _painter.BucketFillTolerance >= 0.99f ? "100%" : $"{Mathf.RoundToInt(_painter.BucketFillTolerance * 100)}%";
            }
        }
        else if (mode == BrushToolMode.MagicWand && _painter?.MagicWandTool != null)
        {
            UpdateWandUI();
        }

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
        if (_selectionOptions != null) _selectionOptions.Visible = (mode == BrushToolMode.Selection);
        if (_decalOptions != null) _decalOptions.Visible = (mode == BrushToolMode.Decal);
        if (_textOptions != null) _textOptions.Visible = (mode == BrushToolMode.Text);
        if (_shapeOptions != null) _shapeOptions.Visible = (mode == BrushToolMode.Shape);

        if (_lblFlyoutTitle != null)
        {
            _lblFlyoutTitle.Text = mode switch
            {
                BrushToolMode.SelectSubmesh => "SUBMESH SELECTOR",
                BrushToolMode.Paint => "BRUSH OPTIONS",
                BrushToolMode.Erase => "ERASER OPTIONS",
                BrushToolMode.BucketFill => "BUCKET FILL",
                BrushToolMode.Selection => "SELECTION TOOLKIT",
                BrushToolMode.MagicWand => "MAGIC WAND MASK",
                BrushToolMode.Decal => "DECAL STAMPER",
                BrushToolMode.Text => "TEXT PROJECTOR",
                BrushToolMode.Shape => "SHAPE TOOL",
                _ => "TOOL OPTIONS"
            };
        }
    }

    private ulong _colorChangeTimestamp = 0;

    private void OnColorPickerColorChanged(Color color)
    {
        // 1. Live preview update (brush and button update live, palette reflects active highlight, but NO recents are added while dragging)
        SetUniversalColor(color, commitToRecents: false);

        // 2. Debounce commit: if dragging stops for 400ms without new changes, commit that single settled color
        ulong currentId = ++_colorChangeTimestamp;
        var tree = GetTree();
        if (tree != null)
        {
            tree.CreateTimer(0.4).Timeout += () =>
            {
                if (_colorChangeTimestamp == currentId)
                {
                    CommitCurrentColorToRecents();
                }
            };
        }
    }

    private void OnColorPickerPopupClosed()
    {
        // User closed the popup -> immediately commit current color and cancel any pending timer
        _colorChangeTimestamp++;
        CommitCurrentColorToRecents();
    }

    private void CommitCurrentColorToRecents()
    {
        Color col = _btnColor != null ? _btnColor.Color : (_painter != null ? _painter.BrushColor : Colors.White);
        var quickPalette = GetNodeOrNull<QuickColorPaletteUI>("../QuickColorPalette")
                        ?? GetTree()?.Root?.FindChild("QuickColorPalette", true, false) as QuickColorPaletteUI;
        quickPalette?.AddRecentColor(col);
    }

    public void SetUniversalColor(Color color)
    {
        SetUniversalColor(color, commitToRecents: true);
    }

    public void SetUniversalColor(Color color, bool commitToRecents)
    {
        if (_painter != null) _painter.BrushColor = color;
        if (_btnColor != null && _btnColor.Color != color) _btnColor.Color = color;

        var quickPalette = GetNodeOrNull<QuickColorPaletteUI>("../QuickColorPalette")
                        ?? GetTree()?.Root?.FindChild("QuickColorPalette", true, false) as QuickColorPaletteUI;
        if (commitToRecents)
        {
            quickPalette?.AddRecentColor(color);
        }
        else
        {
            quickPalette?.SetActiveColor(color, commitToRecents: false);
        }
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
        if (_btnToolSelection != null) _btnToolSelection.TooltipText = $"Selection Toolkit [M / L / P]\nLeft-click to select, right-click for modes";
        if (_btnToolWand != null) _btnToolWand.TooltipText = $"Magic Wand Mask [{KeybindsManager.GetShortcutText("paint_wand")}]\nClick surface to sample color range";
        if (_btnToolDecal != null) _btnToolDecal.TooltipText = $"Decal Stamper [{KeybindsManager.GetShortcutText("paint_decal")}]";
        if (_btnToolText != null) _btnToolText.TooltipText = $"Text Projector [{KeybindsManager.GetShortcutText("paint_text")}]";
        if (_btnToolShape != null) _btnToolShape.TooltipText = "Shape Tool [U]\nRectangle, Circle, Line";
        if (_btnMirror != null) _btnMirror.TooltipText = $"Mirror Symmetry [{KeybindsManager.GetShortcutText("paint_mirror")}]";
        if (_btnUndo != null) _btnUndo.TooltipText = $"Undo [{KeybindsManager.GetShortcutText("paint_undo")}]";
        if (_btnRedo != null) _btnRedo.TooltipText = $"Redo [{KeybindsManager.GetShortcutText("paint_redo")}]";

        if (_lblKeymapHint != null)
        {
            _lblKeymapHint.Text = $"[{KeybindsManager.GetShortcutText("paint_brush")}] Brush   " +
                                  $"[{KeybindsManager.GetShortcutText("paint_eraser")}] Erase   " +
                                  $"[{KeybindsManager.GetShortcutText("paint_bucket")}] Fill   " +
                                  $"[M/L/P] Select   " +
                                  $"[Ctrl+Shift+I] Invert   " +
                                  $"[{KeybindsManager.GetShortcutText("paint_decal")}] Decal   " +
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
            _sliderWandTolerance.SetValueNoSignal(wand.Tolerance);
        }
        if (_lblWandTolerance != null)
        {
            _lblWandTolerance.Text = $"{wand.Tolerance:F3}";
        }
        if (_chkContiguous != null)
        {
            _chkContiguous.SetPressedNoSignal(wand.Contiguous);
        }
        if (_chkUseMask != null)
        {
            _chkUseMask.SetPressedNoSignal(wand.UseSelectionMask);
        }
        if (_chkIsolateSubmesh != null)
        {
            _chkIsolateSubmesh.SetPressedNoSignal(wand.IsolateSubmesh);
        }
        if (_chkAntiAliasing != null)
        {
            _chkAntiAliasing.SetPressedNoSignal(wand.AntiAliasing);
        }

        bool hasMask = (wand?.HasSelection == true) || (_painter?.SelectionMask?.HasSelection == true);
        if (_btnClearMask != null)
        {
            _btnClearMask.Disabled = !hasMask;
            _btnClearMask.Modulate = hasMask ? Colors.White : new Color(1, 1, 1, 0.4f);
        }
        if (_btnClearSelection != null)
        {
            _btnClearSelection.Disabled = !hasMask;
            _btnClearSelection.Modulate = hasMask ? Colors.White : new Color(1, 1, 1, 0.4f);
        }
    }

    public void SyncFromPainter()
    {
        if (_painter == null) return;

        if (_btnColor != null) _btnColor.Color = _painter.BrushColor;
        if (_sliderBrushSize != null) _sliderBrushSize.SetValueNoSignal(_painter.BrushSize);
        if (_sliderBrushHardness != null) _sliderBrushHardness.SetValueNoSignal(_painter.BrushHardness);
        if (_sliderBrushFlow != null) _sliderBrushFlow.SetValueNoSignal(_painter.BrushFlow);
        if (_optBrushShape != null) _optBrushShape.Select((int)_painter.BrushShape);
        if (_optEraseShape != null) _optEraseShape.Select((int)_painter.EraserShape);
        if (_optBlendMode != null) _optBlendMode.Select(_painter.BlendMode);
        if (_chkFrontFacesOnly != null) _chkFrontFacesOnly.SetPressedNoSignal(_painter.FrontFacesOnly);
        if (_sliderBucketTolerance != null)
        {
            _sliderBucketTolerance.SetValueNoSignal(_painter.BucketFillTolerance);
            if (_lblBucketTolerance != null)
            {
                _lblBucketTolerance.Text = _painter.BucketFillTolerance >= 0.99f ? "100%" : $"{Mathf.RoundToInt(_painter.BucketFillTolerance * 100)}%";
            }
        }
        UpdateSelectTargetLabel();
        UpdateUndoRedoState(_painter?.LayerManager?.CanUndo ?? false, _painter?.LayerManager?.CanRedo ?? false);
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
            if (_painter?.LayerManager != null && !_painter.LayerManager.CanUndo)
            {
                GetViewport()?.SetInputAsHandled();
                return;
            }
            EmitSignal(SignalName.UndoRequested);
            GetViewport()?.SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("paint_redo"))
        {
            if (_painter?.LayerManager != null && !_painter.LayerManager.CanRedo)
            {
                GetViewport()?.SetInputAsHandled();
                return;
            }
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

        if (@event.IsActionPressed("paint_selection_invert") || (keyEvent.Keycode == Key.I && keyEvent.CtrlPressed && keyEvent.ShiftPressed))
        {
            InvertSelection();
            GetViewport()?.SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("paint_selection_clear") || (keyEvent.Keycode == Key.D && keyEvent.CtrlPressed && !keyEvent.ShiftPressed && !keyEvent.AltPressed))
        {
            ClearSelection();
            GetViewport()?.SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("paint_select_rect") || (keyEvent.Keycode == Key.M && !keyEvent.CtrlPressed && !keyEvent.AltPressed && !keyEvent.ShiftPressed))
        {
            SelectSelectionTool(SelectionToolType.Rectangular, forceFlyoutOpen: true);
            GetViewport()?.SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("paint_select_lasso") || (keyEvent.Keycode == Key.L && !keyEvent.CtrlPressed && !keyEvent.AltPressed && !keyEvent.ShiftPressed))
        {
            SelectSelectionTool(SelectionToolType.Lasso, forceFlyoutOpen: true);
            GetViewport()?.SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("paint_select_poly") || (keyEvent.Keycode == Key.P && !keyEvent.CtrlPressed && !keyEvent.AltPressed && !keyEvent.ShiftPressed))
        {
            SelectSelectionTool(SelectionToolType.Polygonal, forceFlyoutOpen: true);
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

        // Shape Tool shortcut
        if (keyEvent.Keycode == Key.U && !keyEvent.CtrlPressed && !keyEvent.AltPressed)
        {
            SelectTool(BrushToolMode.Shape, forceFlyoutOpen: true);
            GetViewport()?.SetInputAsHandled();
            return;
        }

        if (_currentTool == BrushToolMode.Shape)
        {
            if (keyEvent.Keycode == Key.Enter || keyEvent.Keycode == Key.KpEnter)
            {
                CommitCurrentShape();
                GetViewport()?.SetInputAsHandled();
                return;
            }
            if (keyEvent.Keycode == Key.Escape)
            {
                CancelCurrentShape();
                GetViewport()?.SetInputAsHandled();
                return;
            }
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

    public void UpdateUndoRedoState(bool canUndo, bool canRedo)
    {
        if (_btnUndo != null)
        {
            _btnUndo.Disabled = !canUndo;
            _btnUndo.Modulate = canUndo ? Colors.White : new Color(0.5f, 0.5f, 0.5f, 0.5f);
        }
        if (_btnRedo != null)
        {
            _btnRedo.Disabled = !canRedo;
            _btnRedo.Modulate = canRedo ? Colors.White : new Color(0.5f, 0.5f, 0.5f, 0.5f);
        }
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
            if (_painter.ShapeTool != null)
            {
                _painter.ShapeTool.ShapeChanged -= OnShapeToolChanged;
                _painter.ShapeTool.ShapeCommitted -= OnShapeToolCommitted;
                _painter.ShapeTool.ShapeCancelled -= OnShapeToolCancelled;
            }
        }
        base._ExitTree();
    }

    public void SetShapeType(CanvasShapeType type)
    {
        if (_painter?.ShapeTool != null)
        {
            _painter.ShapeTool.ShapeType = type;
        }
        if (_btnShapeSquare != null) _btnShapeSquare.ButtonPressed = (type == CanvasShapeType.Square);
        if (_btnShapeCircle != null) _btnShapeCircle.ButtonPressed = (type == CanvasShapeType.Circle);
        if (_btnShapeLine != null) _btnShapeLine.ButtonPressed = (type == CanvasShapeType.Line);
        TriggerShapePreviewUpdate();
    }

    public void TriggerShapePreviewUpdate()
    {
        if (_painter != null && _painter.ShapeTool != null)
        {
            _painter.LayerManager?.UpdateShapePreview(_painter.ShapeTool, _painter.BrushColor);
            _painter.Update3DShapeDecalAndSync();
            _painter.QueueSelectionOverlayRedraw();
        }
        QueueCanvasRedraw();
    }

    public void CommitCurrentShape()
    {
        if (_painter?.ShapeTool != null && _painter.LayerManager != null)
        {
            _painter.ShapeTool.CommitShape(_painter.LayerManager, _painter.BrushColor, (LayerBlendMode)_painter.BlendMode, _painter.MagicWandTool);
            QueueCanvasRedraw();
        }
    }

    public void CancelCurrentShape()
    {
        _painter?.CancelActiveShapePreview();
        if (_painter?.ShapeTool != null)
        {
            _painter.ShapeTool.CancelShape();
            _painter.LayerManager?.CancelShapePreview();
            _painter.QueueSelectionOverlayRedraw();
            QueueCanvasRedraw();
        }
    }

    public void CancelProjectionGizmo()
    {
        if (_painter != null)
        {
            _painter.CancelActiveProjectionGizmo();
            SyncProjectionControls();
            QueueCanvasRedraw();
        }
    }

    private void OnShapeToolChanged()
    {
        SyncShapeControls();
        TriggerShapePreviewUpdate();
    }

    private void OnShapeToolCommitted()
    {
        SyncShapeControls();
        QueueCanvasRedraw();
    }

    private void OnShapeToolCancelled()
    {
        SyncShapeControls();
        QueueCanvasRedraw();
    }

    public void SyncShapeControls()
    {
        bool hasActiveShape = _painter?.ShapeTool != null && _painter.ShapeTool.HasActiveShape;
        if (_btnCancelShape != null)
        {
            _btnCancelShape.Disabled = !hasActiveShape;
            _btnCancelShape.Modulate = hasActiveShape ? Colors.White : new Color(1, 1, 1, 0.4f);
        }
        if (_btnCommitShape != null)
        {
            _btnCommitShape.Disabled = !hasActiveShape;
            _btnCommitShape.Modulate = hasActiveShape ? Colors.White : new Color(1, 1, 1, 0.4f);
        }

        if (_painter?.ShapeTool == null) return;
        var st = _painter.ShapeTool;

        if (_btnShapeSquare != null) _btnShapeSquare.ButtonPressed = (st.ShapeType == CanvasShapeType.Square);
        if (_btnShapeCircle != null) _btnShapeCircle.ButtonPressed = (st.ShapeType == CanvasShapeType.Circle);
        if (_btnShapeLine != null) _btnShapeLine.ButtonPressed = (st.ShapeType == CanvasShapeType.Line);

        if (_optShapeFillMode != null) _optShapeFillMode.Select((int)st.FillMode);

        if (_sliderStrokeWidth != null)
        {
            _sliderStrokeWidth.SetValueNoSignal(st.StrokeWidth);
            if (_lblStrokeWidth != null) _lblStrokeWidth.Text = $"{Mathf.RoundToInt(st.StrokeWidth)} px";
        }

        if (_sliderShapeWidth != null)
        {
            _sliderShapeWidth.SetValueNoSignal(st.Width);
            if (_lblShapeWidth != null) _lblShapeWidth.Text = $"{Mathf.RoundToInt(st.Width)} px";
        }

        if (_sliderShapeHeight != null)
        {
            _sliderShapeHeight.SetValueNoSignal(st.Height);
            if (_lblShapeHeight != null) _lblShapeHeight.Text = $"{Mathf.RoundToInt(st.Height)} px";
        }

        if (_sliderShapeRot != null)
        {
            _sliderShapeRot.SetValueNoSignal(st.RotationDegrees);
            if (_lblShapeRot != null) _lblShapeRot.Text = $"{Mathf.RoundToInt(st.RotationDegrees)}°";
        }
    }

    public void SyncProjectionControls()
    {
        bool hasProj = _painter?.ProjectionGizmo != null && _painter.ProjectionGizmo.IsActive;
        bool hasDecal = hasProj && !_painter.ProjectionGizmo.IsText;
        bool hasText = hasProj && _painter.ProjectionGizmo.IsText;

        if (_btnCancelDecal != null)
        {
            _btnCancelDecal.Disabled = !hasDecal;
            _btnCancelDecal.Modulate = hasDecal ? Colors.White : new Color(1, 1, 1, 0.4f);
        }
        if (_btnBakeDecal != null)
        {
            _btnBakeDecal.Disabled = !hasDecal;
            _btnBakeDecal.Modulate = hasDecal ? Colors.White : new Color(1, 1, 1, 0.4f);
        }

        if (_btnCancelText != null)
        {
            _btnCancelText.Disabled = !hasText;
            _btnCancelText.Modulate = hasText ? Colors.White : new Color(1, 1, 1, 0.4f);
        }
        if (_btnBakeText != null)
        {
            _btnBakeText.Disabled = !hasText;
            _btnBakeText.Modulate = hasText ? Colors.White : new Color(1, 1, 1, 0.4f);
        }

        if (!hasProj) return;

        int atlasSize = _painter.LayerManager?.CanvasSize.X ?? 2048;
        float normScale = _painter.ProjectionGizmo.NormalizedScale(atlasSize);
        float rot = _painter.ProjectionGizmo.RotationDegrees;

        if (_painter.ProjectionGizmo.IsText)
        {
            if (_sliderTextScale != null) _sliderTextScale.SetValueNoSignal(normScale);
            if (_lblTextScale != null) _lblTextScale.Text = $"{normScale:F2}x";
            if (_sliderTextRot != null) _sliderTextRot.SetValueNoSignal(rot);
            if (_lblTextRot != null) _lblTextRot.Text = $"{Mathf.RoundToInt(rot)}°";
        }
        else
        {
            if (_sliderDecalScale != null) _sliderDecalScale.SetValueNoSignal(normScale);
            if (_lblDecalScale != null) _lblDecalScale.Text = $"{normScale:F2}x";
            if (_sliderDecalRot != null) _sliderDecalRot.SetValueNoSignal(rot);
            if (_lblDecalRot != null) _lblDecalRot.Text = $"{Mathf.RoundToInt(rot)}°";
        }
    }

    public void QueueCanvasRedraw()
    {
        var uvCanvas = GetNodeOrNull<UVCanvas2DUI>("../UVCanvas2D")
                    ?? GetTree()?.Root?.FindChild("UVCanvas2D", true, false) as UVCanvas2DUI;
        uvCanvas?.QueueCanvasRedraw();
        _painter?.Queue3DOverlayRedraw();
    }

    public bool IsMouseOverPalette(Vector2 globalMouse)
    {
        if (!Visible) return false;

        if (_toolStripPanel != null && GodotObject.IsInstanceValid(_toolStripPanel) && _toolStripPanel.Visible)
        {
            if (_toolStripPanel.GetGlobalRect().HasPoint(globalMouse)) return true;
        }

        if (_flyoutPanel != null && GodotObject.IsInstanceValid(_flyoutPanel) && _flyoutPanel.Visible)
        {
            if (_flyoutPanel.GetGlobalRect().HasPoint(globalMouse)) return true;
        }

        if (_btnColor != null && GodotObject.IsInstanceValid(_btnColor))
        {
            var picker = _btnColor.GetPopup();
            if (picker != null && GodotObject.IsInstanceValid(picker) && picker.Visible)
            {
                var pickerRect = new Rect2(picker.Position, picker.Size);
                if (pickerRect.HasPoint(globalMouse)) return true;
            }
        }

        return false;
    }
}