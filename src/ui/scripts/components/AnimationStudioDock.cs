using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using DeadlockPlayground.Animation;
using DeadlockPlayground.Tools;

namespace DeadlockPlayground.UI
{
    /// <summary>
    /// Blender-style Animation Studio collapsible bottom dock.
    /// Declarative architecture integrated with DopeSheetView, GraphEditorView, and ClipAndPoseView.
    /// </summary>
    public partial class AnimationStudioDock : PanelContainer
    {
        [Signal] public delegate void VisibilityToggledEventHandler(bool isOpen);
        [Signal] public delegate void AnimationModifiedEventHandler(string animName);

        public enum StudioTab { DopeSheet = 0, GraphEditor = 1, ClipSettings = 2 }

        public const float HeaderBarHeight = 44f;
        public const float MinDockHeight = 180f;
        public const float MaxDockHeight = 650f;

        // UI References - Top Bar
        private Control _resizeGrip;
        private PanelContainer _headerBar;
        private Button _btnToggleCollapse;
        private Button _btnTabDopeSheet;
        private Button _btnTabGraphEditor;
        private Button _btnTabClipSettings;
        private Button _btnRewindStart;
        private Button _btnStepBack;
        private Button _btnPlayPause;
        private Button _btnStepNext;
        private Button _btnJumpEnd;
        private Button _btnLoop;
        private SpinBox _spinCurrentFrame;
        private Label _lblTotalFrames;
        private Label _lblTimeDisplay;
        private Button _btnAutoKey;
        private MenuButton _menuQuickTools;
        private MenuButton _menuKeyActions;
        private Button _btnUndo;
        private Button _btnRedo;

        // Viewport Floating Controls Cache
        private Control _cachedViewportArea;
        private Control _cachedCamPad;
        private Control _cachedQuickStrip;
        private Control _cachedBadges;

        // UI References - Body & Subviews
        private MarginContainer _contentContainer;
        private Control _dopeSheetView;
        private Control _graphEditorView;
        private Control _clipAndPoseView;
        private PanelContainer _boneQuickControlPanel;

        // Dope Sheet View Controls
        private LineEdit _boneSearch;
        private BoneListCanvas _boneListCanvas;
        private VScrollBar _boneListVScrollBar;
        private DopeSheetGridCanvas _dopeSheetCanvas;

        // Graph Editor View Controls
        private SpinBox _spinKeyFrame;
        private SpinBox _spinKeyValue;
        private Button _btnNormalize;
        private Button _btnFitView;
        private Label _lblTransformMode;
        private Label _lblActiveBoneChannel;
        private VBoxContainer _channelListVBox;
        private GraphEditorCanvas _graphEditorCanvas;

        // Clip & Pose View Controls
        private SpinBox _spinFps;
        private SpinBox _spinDuration;
        private CheckBox _chkLooping;
        private CheckBox _chkAdditive;
        private Button _btnMakeLoopSeamless;
        private SpinBox _spinLoopBlendFrames;
        private Button _btnSmoothCurves;
        private HSlider _sliderSmoothStrength;
        private HSlider _sliderAmplifyMotion;
        private Label _lblAmplifyValue;
        private Button _btnApplyAmplify;
        private Button _btnGenerateBreathing;
        private HSlider _sliderBreathingIntensity;
        private HSlider _sliderSwayIntensity;
        private SpinBox _spinBreathingSpeed;
        private Button _btnCopyPose;
        private Button _btnPastePose;
        private Button _btnPasteMirrorPose;
        private Button _btnMirrorWholeAnim;
        private Button _btnExportGlb;
        private Label _lblExportStatus;

        // External Animation Import Controls
        private Button _btnImportAnim;
        private Button _btnImportExternalAnim;
        private Button _btnBrowseMixamo;
        private Button _btnBrowseMesh2Motion;
        private FileDialog _fileDialogImport;

        // Bone Quick-Control Inspector Controls
        private Label _lblActiveBone;
        private Label _lblBoneRotX;
        private HSlider _sliderBoneRotX;
        private Label _lblBoneRotY;
        private HSlider _sliderBoneRotY;
        private Label _lblBoneRotZ;
        private HSlider _sliderBoneRotZ;
        private Button _btnResetBonePose;
        private Button _btnDeselectBone;
        private Button _btnToggleAdditiveOffsetMode;
        private Button _btnApplyOffsetToAllFrames;
        private bool _isUpdatingBoneSliders = false;
        private bool _isAdditiveClipOffsetMode = false;
        private Quaternion _baselineBoneRot = Quaternion.Identity;
        private Vector3 _baselineBonePos = Vector3.Zero;
        private bool _hasBaselinePose = false;

        // Active State
        private Skeleton3D _skeleton;
        private AnimationPlayer _animPlayer;
        private Godot.Animation _activeAnimation;
        private string _activeAnimName = "";
        private float _fps = 30f;
        private double _currentTime = 0.0;
        private bool _isOpen = false;
        private bool _isAutoKeyEnabled = false;
        private StudioTab _currentTab = StudioTab.DopeSheet;
        private string _selectedBoneName = "";
        private SkeletonGizmoManager _gizmoManager;

        // Undo/Redo & Subsystems
        private readonly AnimationUndoRedoManager _undoRedo = new();

        private const float EdgeMargin = 4f;

        // Dock Outer Frame & Border Styling
        private static readonly StyleBoxFlat DockPanelStyle = new()
        {
            BgColor = new Color(0.09f, 0.10f, 0.11f, 0.98f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.247059f, 0.364706f, 0.301961f, 0.8f),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomRight = 4,
            CornerRadiusBottomLeft = 4,
            ShadowColor = new Color(0f, 0f, 0f, 0.45f),
            ShadowSize = 6,
            ShadowOffset = new Vector2(0f, -2f)
        };

        // Menu Button Styles (Quick Tools, Key Actions)
        private static readonly StyleBoxFlat MenuBtnNormal = new()
        {
            ContentMarginLeft = 10f,
            ContentMarginRight = 10f,
            ContentMarginTop = 4f,
            ContentMarginBottom = 4f,
            BgColor = new Color(0.133333f, 0.12549f, 0.129412f, 0.85f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.247059f, 0.364706f, 0.301961f, 0.7f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusBottomLeft = 4
        };

        private static readonly StyleBoxFlat MenuBtnHover = new()
        {
            ContentMarginLeft = 10f,
            ContentMarginRight = 10f,
            ContentMarginTop = 4f,
            ContentMarginBottom = 4f,
            BgColor = new Color(0.247059f, 0.364706f, 0.301961f, 0.95f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.447059f, 0.580392f, 0.498039f, 1f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusBottomLeft = 4
        };

        private static readonly StyleBoxFlat MenuBtnPressed = new()
        {
            ContentMarginLeft = 10f,
            ContentMarginRight = 10f,
            ContentMarginTop = 4f,
            ContentMarginBottom = 4f,
            BgColor = new Color(0.447059f, 0.580392f, 0.498039f, 1f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.937255f, 0.870588f, 0.74902f, 1f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusBottomLeft = 4
        };

        // Resizing State
        private bool _isResizingHeight = false;
        private float _dragStartMouseY = 0f;
        private float _dragStartHeight = 320f;
        private float _currentDockHeight = 320f;

        private AnimationStudioKeymap _keymap;
        private const string ConfigPath = "user://animation_studio.cfg";
        private const string SectionName = "AnimationStudio";

        public bool IsOpen => _isOpen;
        public bool IsAutoKeyEnabled => _isAutoKeyEnabled;
        public DopeSheetGridCanvas DopeSheetCanvas => _dopeSheetCanvas;
        public GraphEditorCanvas GraphEditorCanvas => _graphEditorCanvas;
        public StudioTab CurrentTab => _currentTab;

        public void Undo() => _undoRedo.Undo();
        public void Redo() => _undoRedo.Redo();
        public void CopyPose() => AnimationMirrorHelper.CopyPose(_skeleton);
        public void PastePose()
        {
            AnimationMirrorHelper.PastePose(_skeleton);
            CommitAutoKeyForCurrentPose();
        }
        public void PasteMirrorPose()
        {
            AnimationMirrorHelper.PasteMirrorPose(_skeleton);
            CommitAutoKeyForCurrentPose();
        }

        public override void _Ready()
        {
            ZIndex = 5;
            AddThemeStyleboxOverride("panel", DockPanelStyle);
            BindSceneNodes();
            ConnectSignals();

            if (GetTree()?.Root != null)
            {
                GetTree().Root.SizeChanged += OnViewportRootSizeChanged;
            }

            // Dock starts collapsed: only header bar (44px) visible
            InitDockIcons();
            CollapseDock();

            _keymap = new AnimationStudioKeymap(this);
            LoadDockSettings();

            _undoRedo.HistoryChanged += UpdateUndoRedoButtons;
            UpdateUndoRedoButtons();

            SkeletonGizmoManager.OnBoneSelected += OnViewportBoneSelected;
            SkeletonGizmoManager.OnBoneTransformEnded += OnViewportBoneTransformEnded;

            AnimationTransportService.Instance.PlaybackStateChanged += OnTransportPlaybackStateChanged;
            AnimationTransportService.Instance.TimeChanged += OnTransportTimeChanged;
            AnimationTransportService.Instance.LoopChanged += OnTransportLoopChanged;
        }

        public override void _UnhandledKeyInput(InputEvent @event)
        {
            if (@event is InputEventKey key && _keymap != null && _keymap.HandleKeyInput(key))
            {
                GetViewport().SetInputAsHandled();
            }
        }

        private void LoadDockSettings()
        {
            var config = new ConfigFile();
            if (config.Load(ConfigPath) == Error.Ok)
            {
                _currentDockHeight = (float)config.GetValue(SectionName, "DockHeight", 320.0f);
                _currentDockHeight = Mathf.Clamp(_currentDockHeight, MinDockHeight, MaxDockHeight);
                int savedTab = (int)config.GetValue(SectionName, "LastTab", 0);
                if (savedTab >= 0 && savedTab <= 2)
                {
                    SwitchTab((StudioTab)savedTab);
                }
            }
        }

        private void SaveDockSettings()
        {
            var config = new ConfigFile();
            config.Load(ConfigPath);
            config.SetValue(SectionName, "DockHeight", _currentDockHeight);
            config.SetValue(SectionName, "IsOpen", _isOpen);
            config.SetValue(SectionName, "LastTab", (int)_currentTab);
            config.Save(ConfigPath);
        }

        private T FindSceneNode<T>(string name) where T : class
        {
            var child = FindChild(name, recursive: true, owned: false) as T;
            if (child != null) return child;

            var unique = GetNodeOrNull<Node>($"%{name}") as T;
            if (unique != null) return unique;

            return FindChildRecursive<T>(this, name);
        }

        private static T FindChildRecursive<T>(Node parent, string name) where T : class
        {
            if (parent == null) return null;
            int childCount = parent.GetChildCount();
            for (int i = 0; i < childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.Name == name && child is T match)
                    return match;

                var found = FindChildRecursive<T>(child, name);
                if (found != null)
                    return found;
            }
            return null;
        }

        private void BindSceneNodes()
        {
            // Top Bar
            _resizeGrip = GetNodeOrNull<Control>("%ResizeGrip");
            _headerBar = GetNodeOrNull<PanelContainer>("%HeaderBar");
            _btnToggleCollapse = GetNodeOrNull<Button>("%BtnToggleCollapse");
            _btnTabDopeSheet = GetNodeOrNull<Button>("%BtnTabDopeSheet");
            _btnTabGraphEditor = GetNodeOrNull<Button>("%BtnTabGraphEditor");
            _btnTabClipSettings = GetNodeOrNull<Button>("%BtnTabClipSettings");

            _btnRewindStart = GetNodeOrNull<Button>("%BtnRewindStart");
            _btnStepBack = GetNodeOrNull<Button>("%BtnStepBack");
            _btnPlayPause = GetNodeOrNull<Button>("%BtnPlayPause");
            _btnStepNext = GetNodeOrNull<Button>("%BtnStepNext");
            _btnJumpEnd = GetNodeOrNull<Button>("%BtnJumpEnd");
            _btnLoop = GetNodeOrNull<Button>("%BtnLoop");

            _spinCurrentFrame = GetNodeOrNull<SpinBox>("%SpinCurrentFrame");
            _lblTotalFrames = GetNodeOrNull<Label>("%LblTotalFrames");
            _lblTimeDisplay = GetNodeOrNull<Label>("%LblTimeDisplay");

            _btnAutoKey = GetNodeOrNull<Button>("%BtnAutoKey");
            _menuQuickTools = GetNodeOrNull<MenuButton>("%MenuQuickTools");
            _menuKeyActions = GetNodeOrNull<MenuButton>("%MenuKeyActions");
            _btnUndo = GetNodeOrNull<Button>("%BtnUndo");
            _btnRedo = GetNodeOrNull<Button>("%BtnRedo");

            // Main Body Containers
            _contentContainer = GetNodeOrNull<MarginContainer>("%ContentContainer");
            _dopeSheetView = GetNodeOrNull<Control>("%DopeSheetView");
            _graphEditorView = GetNodeOrNull<Control>("%GraphEditorView");
            _clipAndPoseView = GetNodeOrNull<Control>("%ClipAndPoseView");
            _boneQuickControlPanel = GetNodeOrNull<PanelContainer>("%BoneQuickControlPanel");

            // Dope Sheet Nodes (inside instanced DopeSheetView)
            _boneSearch = FindSceneNode<LineEdit>("BoneSearch");
            if (_boneSearch != null)
            {
                _boneSearch.AddThemeFontSizeOverride("font_size", 13);
            }
            var lblSummary = FindSceneNode<Label>("LblSummaryRow");
            if (lblSummary != null)
            {
                var fontColus = PlaygroundThemeHelper.GetFontColus();
                if (fontColus != null) lblSummary.AddThemeFontOverride("font", fontColus);
                lblSummary.AddThemeFontSizeOverride("font_size", 13);
            }
            _boneListCanvas = FindSceneNode<BoneListCanvas>("BoneListCanvas");
            _boneListVScrollBar = FindSceneNode<VScrollBar>("BoneListVScrollBar");
            _dopeSheetCanvas = FindSceneNode<DopeSheetGridCanvas>("DopeSheetCanvas");

            // Graph Editor Nodes (inside instanced GraphEditorView)
            _spinKeyFrame = FindSceneNode<SpinBox>("SpinKeyFrame");
            _spinKeyValue = FindSceneNode<SpinBox>("SpinKeyValue");
            _btnNormalize = FindSceneNode<Button>("BtnNormalize");
            _btnFitView = FindSceneNode<Button>("BtnFitView");
            _lblTransformMode = FindSceneNode<Label>("LblTransformMode");
            _lblActiveBoneChannel = FindSceneNode<Label>("LblActiveBoneChannel");
            _channelListVBox = FindSceneNode<VBoxContainer>("ChannelListVBox");
            _graphEditorCanvas = FindSceneNode<GraphEditorCanvas>("GraphCanvas");

            // Clip & Pose Nodes (inside instanced ClipAndPoseView)
            _spinFps = FindSceneNode<SpinBox>("SpinFps");
            _spinDuration = FindSceneNode<SpinBox>("SpinDuration");
            _chkLooping = FindSceneNode<CheckBox>("ChkLooping");
            _chkAdditive = FindSceneNode<CheckBox>("ChkAdditive");

            _btnMakeLoopSeamless = FindSceneNode<Button>("BtnMakeLoopSeamless");
            _spinLoopBlendFrames = FindSceneNode<SpinBox>("SpinLoopBlendFrames");
            _btnSmoothCurves = FindSceneNode<Button>("BtnSmoothCurves");
            _sliderSmoothStrength = FindSceneNode<HSlider>("SliderSmoothStrength");
            _sliderAmplifyMotion = FindSceneNode<HSlider>("SliderAmplifyMotion");
            _lblAmplifyValue = FindSceneNode<Label>("LblAmplifyValue");
            _btnApplyAmplify = FindSceneNode<Button>("BtnApplyAmplify");
            _btnGenerateBreathing = FindSceneNode<Button>("BtnGenerateBreathing");
            _sliderBreathingIntensity = FindSceneNode<HSlider>("SliderBreathingIntensity");
            _sliderSwayIntensity = FindSceneNode<HSlider>("SliderSwayIntensity");
            _spinBreathingSpeed = FindSceneNode<SpinBox>("SpinBreathingSpeed");

            _btnCopyPose = FindSceneNode<Button>("BtnCopyPose");
            _btnPastePose = FindSceneNode<Button>("BtnPastePose");
            _btnPasteMirrorPose = FindSceneNode<Button>("BtnPasteMirrorPose");
            _btnMirrorWholeAnim = FindSceneNode<Button>("BtnMirrorWholeAnim");
            _btnExportGlb = FindSceneNode<Button>("BtnExportGlb");
            _lblExportStatus = FindSceneNode<Label>("LblExportStatus");

            // External Animation Import & Browse Nodes
            _btnImportAnim = FindSceneNode<Button>("BtnImportAnim");
            _btnImportExternalAnim = FindSceneNode<Button>("BtnImportExternalAnim");
            _btnBrowseMixamo = FindSceneNode<Button>("BtnBrowseMixamo");
            _btnBrowseMesh2Motion = FindSceneNode<Button>("BtnBrowseMesh2Motion");

            var importIcon = GD.Load<Texture2D>("res://assets/at-icons/file_arrow_down.svg");
            if (importIcon != null)
            {
                if (_btnImportAnim != null && _btnImportAnim.Icon == null) _btnImportAnim.Icon = importIcon;
                if (_btnImportExternalAnim != null && _btnImportExternalAnim.Icon == null) _btnImportExternalAnim.Icon = importIcon;
            }

            var globeIcon = GD.Load<Texture2D>("res://assets/at-icons/globe.svg");
            if (globeIcon != null)
            {
                if (_btnBrowseMixamo != null && _btnBrowseMixamo.Icon == null) _btnBrowseMixamo.Icon = globeIcon;
                if (_btnBrowseMesh2Motion != null && _btnBrowseMesh2Motion.Icon == null) _btnBrowseMesh2Motion.Icon = globeIcon;
            }

            // Bone Quick-Control Inspector Nodes
            _lblActiveBone = FindSceneNode<Label>("LblActiveBone") ?? GetNodeOrNull<Label>("%LblActiveBone");
            _lblBoneRotX = FindSceneNode<Label>("LblBoneRotX") ?? GetNodeOrNull<Label>("%LblBoneRotX");
            _sliderBoneRotX = FindSceneNode<HSlider>("SliderBoneRotX") ?? GetNodeOrNull<HSlider>("%SliderBoneRotX");
            _lblBoneRotY = FindSceneNode<Label>("LblBoneRotY") ?? GetNodeOrNull<Label>("%LblBoneRotY");
            _sliderBoneRotY = FindSceneNode<HSlider>("SliderBoneRotY") ?? GetNodeOrNull<HSlider>("%SliderBoneRotY");
            _lblBoneRotZ = FindSceneNode<Label>("LblBoneRotZ") ?? GetNodeOrNull<Label>("%LblBoneRotZ");
            _sliderBoneRotZ = FindSceneNode<HSlider>("SliderBoneRotZ") ?? GetNodeOrNull<HSlider>("%SliderBoneRotZ");
            _btnResetBonePose = FindSceneNode<Button>("BtnResetBonePose") ?? GetNodeOrNull<Button>("%BtnResetBonePose");
            _btnDeselectBone = FindSceneNode<Button>("BtnDeselectBone") ?? GetNodeOrNull<Button>("%BtnDeselectBone");
            _btnToggleAdditiveOffsetMode = FindSceneNode<Button>("BtnToggleAdditiveOffsetMode") ?? GetNodeOrNull<Button>("%BtnToggleAdditiveOffsetMode");
            _btnApplyOffsetToAllFrames = FindSceneNode<Button>("BtnApplyOffsetToAllFrames") ?? GetNodeOrNull<Button>("%BtnApplyOffsetToAllFrames");
        }

        private void InitDockIcons()
        {
            DockIcons.Apply(_btnToggleCollapse, _isOpen ? "arrow_down" : "arrow_up", "Toggle Animation Studio Dock (Ctrl+Space)");
            DockIcons.Apply(_btnRewindStart, "arrow_left_to_line", "Rewind to Start (Shift+Left)");
            DockIcons.Apply(_btnStepBack, "previous", "Step Backward 1 Frame (Left Arrow)");
            DockIcons.Apply(_btnPlayPause, AnimationTransportService.Instance.IsPlaying ? "pause" : "play", "Play/Pause (Space)");
            DockIcons.Apply(_btnStepNext, "next", "Step Forward 1 Frame (Right Arrow)");
            DockIcons.Apply(_btnJumpEnd, "arrow_right_to_line", "Jump to End (Shift+Right)");
            DockIcons.Apply(_btnLoop, "arrows_clockwise", "Toggle Loop Playback");
            DockIcons.Apply(_btnAutoKey, "circle", "Toggle Auto-Keying (I)");
            DockIcons.Apply(_btnUndo, "undo", "Undo (Ctrl+Z)");
            DockIcons.Apply(_btnRedo, "redo", "Redo (Ctrl+Y)");

            if (_btnTabDopeSheet != null)
            {
                var icon = DockIcons.Load("film");
                if (icon != null) _btnTabDopeSheet.Icon = icon;
            }
            if (_btnTabGraphEditor != null)
            {
                var icon = DockIcons.Load("line_graph");
                if (icon != null) _btnTabGraphEditor.Icon = icon;
            }
            if (_btnTabClipSettings != null)
            {
                var icon = DockIcons.Load("sliders");
                if (icon != null) _btnTabClipSettings.Icon = icon;
            }

            if (_btnCopyPose != null) DockIcons.Apply(_btnCopyPose, "duplicate", "Copy Pose (Ctrl+C)", iconOnly: false);
            if (_btnPastePose != null) DockIcons.Apply(_btnPastePose, "duplicate", "Paste Pose (Ctrl+V)", iconOnly: false);
            if (_btnPasteMirrorPose != null) DockIcons.Apply(_btnPasteMirrorPose, "mirror_horizontally", "Paste Mirrored Pose (Ctrl+Shift+V)", iconOnly: false);
            if (_btnMirrorWholeAnim != null) DockIcons.Apply(_btnMirrorWholeAnim, "mirror_horizontally", "Mirror Whole Animation", iconOnly: false);
            if (_btnExportGlb != null) DockIcons.Apply(_btnExportGlb, "file_arrow_down", "Export Animation to .GLB", iconOnly: false);
        }

        private void ConnectSignals()
        {
            // Resizing & Collapse
            if (_resizeGrip != null)
            {
                _resizeGrip.GuiInput += OnResizeGripGuiInput;
                _resizeGrip.Draw += OnDrawResizeGrip;
            }
            if (_headerBar != null) _headerBar.GuiInput += OnHeaderBarGuiInput;
            if (_btnToggleCollapse != null) _btnToggleCollapse.Pressed += ToggleOpen;

            // Tabs
            if (_btnTabDopeSheet != null) _btnTabDopeSheet.Pressed += () => SwitchTab(StudioTab.DopeSheet);
            if (_btnTabGraphEditor != null) _btnTabGraphEditor.Pressed += () => SwitchTab(StudioTab.GraphEditor);
            if (_btnTabClipSettings != null) _btnTabClipSettings.Pressed += () => SwitchTab(StudioTab.ClipSettings);

            // Transport
            if (_btnRewindStart != null) _btnRewindStart.Pressed += () => AnimationTransportService.Instance.Seek(0.0);
            if (_btnStepBack != null) _btnStepBack.Pressed += () => AnimationTransportService.Instance.Step(-1);
            if (_btnPlayPause != null) _btnPlayPause.Toggled += OnPlayPauseToggled;
            if (_btnStepNext != null) _btnStepNext.Pressed += () => AnimationTransportService.Instance.Step(1);
            if (_btnJumpEnd != null) _btnJumpEnd.Pressed += () => AnimationTransportService.Instance.Seek(AnimationTransportService.Instance.Length);
            if (_btnLoop != null)
            {
                _btnLoop.ToggleMode = true;
                _btnLoop.Toggled += (looping) => AnimationTransportService.Instance.SetLoop(looping);
            }

            // Frame Input
            if (_spinCurrentFrame != null) _spinCurrentFrame.ValueChanged += (val) => AnimationTransportService.Instance.Seek((int)val / (double)_fps);

            // Auto-Key
            if (_btnAutoKey != null)
            {
                _btnAutoKey.Toggled += (toggled) =>
                {
                    _isAutoKeyEnabled = toggled;
                    _btnAutoKey.Modulate = toggled ? Colors.LightCoral : Colors.White;
                };
            }

            // Quick Tools Menu
            SetupQuickToolsMenu();

            // Key Actions Menu
            SetupKeyActionsMenu();

            // Undo / Redo
            if (_btnUndo != null) _btnUndo.Pressed += () => _undoRedo.Undo();
            if (_btnRedo != null) _btnRedo.Pressed += () => _undoRedo.Redo();

            // Dope Sheet Signals
            if (_dopeSheetCanvas != null)
            {
                _dopeSheetCanvas.SetUndoRedoManager(_undoRedo);
                _dopeSheetCanvas.PlayheadScrubbed += OnCanvasScrubbed;
                _dopeSheetCanvas.KeyframesModified += OnCanvasKeyframesModified;

                // Synchronized scrolling
                _dopeSheetCanvas.VerticalScrollChanged += (scrollY) =>
                {
                    if (_boneListCanvas != null) _boneListCanvas.ScrollY = scrollY;
                    if (_boneListVScrollBar != null) _boneListVScrollBar.SetValueNoSignal(scrollY);
                };

                _dopeSheetCanvas.TrackHovered += (idx, boneName) =>
                {
                    HighlightBoneRow(boneName, isHover: true);
                };

                _dopeSheetCanvas.TrackSelected += (idx, boneName) =>
                {
                    SelectBone(boneName);
                    ScrollToBoneRow(boneName);
                };
            }

            if (_boneListCanvas != null)
            {
                _boneListCanvas.BoneSelected += (bone) =>
                {
                    SelectBone(bone);
                    ScrollToBoneRow(bone);
                };
                _boneListCanvas.BoneHovered += (bone) =>
                {
                    _dopeSheetCanvas?.SetHoveredTrackByBone(bone);
                };
                _boneListCanvas.ScrollChanged += (scrollY) =>
                {
                    if (_dopeSheetCanvas != null) _dopeSheetCanvas.ScrollY = scrollY;
                    if (_boneListVScrollBar != null) _boneListVScrollBar.SetValueNoSignal(scrollY);
                };
            }

            if (_boneListVScrollBar != null)
            {
                _boneListVScrollBar.ValueChanged += (val) =>
                {
                    if (_boneListCanvas != null) _boneListCanvas.ScrollY = (float)val;
                    if (_dopeSheetCanvas != null) _dopeSheetCanvas.ScrollY = (float)val;
                };
            }

            if (_boneSearch != null)
            {
                _boneSearch.TextChanged += (text) =>
                {
                    _dopeSheetCanvas?.SetFilter(text);
                    PopulateBoneList();
                };
            }

            // Graph Editor Signals
            if (_graphEditorCanvas != null)
            {
                _graphEditorCanvas.SetUndoRedoManager(_undoRedo);
                _graphEditorCanvas.PlayheadScrubbed += OnCanvasScrubbed;
                _graphEditorCanvas.KeyframesModified += OnCanvasKeyframesModified;
                _graphEditorCanvas.CurvesRebuilt += RebuildChannelList;

                _graphEditorCanvas.KeySelectionChanged += (time, value, hasSel) =>
                {
                    if (_spinKeyFrame != null)
                    {
                        _spinKeyFrame.Editable = hasSel;
                        if (hasSel) _spinKeyFrame.SetValueNoSignal(Math.Round(time * _fps));
                    }
                    if (_spinKeyValue != null)
                    {
                        _spinKeyValue.Editable = hasSel;
                        if (hasSel) _spinKeyValue.SetValueNoSignal(Math.Round(value, 2));
                    }
                };

                _graphEditorCanvas.ModalStatusChanged += (status) =>
                {
                    if (_lblTransformMode != null)
                    {
                        _lblTransformMode.Text = status;
                    }
                };
            }

            if (_spinKeyFrame != null)
            {
                _spinKeyFrame.ValueChanged += (val) => _graphEditorCanvas?.SetSelectedKeyFrame((int)val);
            }
            if (_spinKeyValue != null)
            {
                _spinKeyValue.ValueChanged += (val) => _graphEditorCanvas?.SetSelectedKeyValue((float)val);
            }
            if (_btnNormalize != null)
            {
                _btnNormalize.Toggled += (pressed) =>
                {
                    if (_graphEditorCanvas != null) _graphEditorCanvas.IsNormalized = pressed;
                };
            }
            if (_btnFitView != null)
            {
                _btnFitView.Pressed += () => _graphEditorCanvas?.FitView();
            }

            // Clip & Pose Controls
            if (_spinFps != null) _spinFps.ValueChanged += (v) => SetFps((float)v);
            if (_spinDuration != null) _spinDuration.ValueChanged += (v) => SetDuration((float)v);
            if (_chkLooping != null) _chkLooping.Toggled += OnLoopingToggled;
            if (_chkAdditive != null) _chkAdditive.Toggled += OnAdditiveToggled;

            // 1-Click Assistants
            if (_btnMakeLoopSeamless != null)
            {
                _btnMakeLoopSeamless.Pressed += () =>
                {
                    int blend = _spinLoopBlendFrames != null ? (int)_spinLoopBlendFrames.Value : 6;
                    AnimationAssistantHelper.MakeLoopSeamless(_activeAnimation, _skeleton, blend, _fps);
                    OnCanvasKeyframesModified();
                };
            }

            if (_btnSmoothCurves != null)
            {
                _btnSmoothCurves.Pressed += () =>
                {
                    float strength = _sliderSmoothStrength != null ? (float)_sliderSmoothStrength.Value : 0.5f;
                    AnimationAssistantHelper.SmoothCurves(_activeAnimation, _selectedBoneName, strength);
                    OnCanvasKeyframesModified();
                };
            }

            if (_sliderAmplifyMotion != null)
            {
                _sliderAmplifyMotion.ValueChanged += (val) =>
                {
                    if (_lblAmplifyValue != null) _lblAmplifyValue.Text = $"{val:F0}%";
                };
            }

            if (_btnApplyAmplify != null)
            {
                _btnApplyAmplify.Pressed += () =>
                {
                    float scale = _sliderAmplifyMotion != null ? (float)(_sliderAmplifyMotion.Value / 100.0) : 1.0f;
                    AnimationAssistantHelper.AmplifyMotion(_activeAnimation, _selectedBoneName, scale);
                    OnCanvasKeyframesModified();
                };
            }

            if (_btnGenerateBreathing != null)
            {
                _btnGenerateBreathing.Pressed += () =>
                {
                    float intensity = _sliderBreathingIntensity != null ? (float)_sliderBreathingIntensity.Value : 1.0f;
                    float sway = _sliderSwayIntensity != null ? (float)_sliderSwayIntensity.Value : 0.5f;
                    float bpm = _spinBreathingSpeed != null ? (float)_spinBreathingSpeed.Value : 14.0f;
                    AnimationAssistantHelper.GenerateBreathingLayer(_activeAnimation, _skeleton, intensity, sway, bpm, _fps);
                    OnCanvasKeyframesModified();
                };
            }

            if (_btnCopyPose != null) _btnCopyPose.Pressed += () => AnimationMirrorHelper.CopyPose(_skeleton);
            if (_btnPastePose != null)
            {
                _btnPastePose.Pressed += () =>
                {
                    AnimationMirrorHelper.PastePose(_skeleton);
                    CommitAutoKeyForCurrentPose();
                };
            }
            if (_btnPasteMirrorPose != null)
            {
                _btnPasteMirrorPose.Pressed += () =>
                {
                    AnimationMirrorHelper.PasteMirrorPose(_skeleton);
                    CommitAutoKeyForCurrentPose();
                };
            }
            if (_btnMirrorWholeAnim != null)
            {
                _btnMirrorWholeAnim.Pressed += () =>
                {
                    AnimationAssistantHelper.MirrorWholeAnimation(_activeAnimation, _skeleton);
                    OnCanvasKeyframesModified();
                };
            }
            if (_btnExportGlb != null) _btnExportGlb.Pressed += ExportToGlb;

            // External Animation Import & Web Browse Signals
            if (_btnImportAnim != null) _btnImportAnim.Pressed += OpenImportFileDialog;
            if (_btnImportExternalAnim != null) _btnImportExternalAnim.Pressed += OpenImportFileDialog;
            if (_btnBrowseMixamo != null) _btnBrowseMixamo.Pressed += () => OS.ShellOpen("https://www.mixamo.com");
            if (_btnBrowseMesh2Motion != null) _btnBrowseMesh2Motion.Pressed += () => OS.ShellOpen("https://mesh2motion.com");

            // Bone Quick-Control Inspector
            if (_sliderBoneRotX != null)
            {
                _sliderBoneRotX.ValueChanged += (v) => OnBoneSliderChanged(v, 0);
                _sliderBoneRotX.DragEnded += (valChanged) => { if (_isAdditiveClipOffsetMode && valChanged) ApplyCurrentBoneTweakToEntireClip(); };
            }
            if (_sliderBoneRotY != null)
            {
                _sliderBoneRotY.ValueChanged += (v) => OnBoneSliderChanged(v, 1);
                _sliderBoneRotY.DragEnded += (valChanged) => { if (_isAdditiveClipOffsetMode && valChanged) ApplyCurrentBoneTweakToEntireClip(); };
            }
            if (_sliderBoneRotZ != null)
            {
                _sliderBoneRotZ.ValueChanged += (v) => OnBoneSliderChanged(v, 2);
                _sliderBoneRotZ.DragEnded += (valChanged) => { if (_isAdditiveClipOffsetMode && valChanged) ApplyCurrentBoneTweakToEntireClip(); };
            }
            if (_btnResetBonePose != null) _btnResetBonePose.Pressed += OnResetBonePosePressed;
            if (_btnDeselectBone != null) _btnDeselectBone.Pressed += () => SelectBone("");
            if (_btnToggleAdditiveOffsetMode != null) _btnToggleAdditiveOffsetMode.Toggled += OnToggleAdditiveOffsetMode;
            if (_btnApplyOffsetToAllFrames != null) _btnApplyOffsetToAllFrames.Pressed += ApplyCurrentBoneTweakToEntireClip;
        }

        private void SetupQuickToolsMenu()
        {
            if (_menuQuickTools == null) return;
            _menuQuickTools.Flat = false;
            _menuQuickTools.ExpandIcon = true;
            _menuQuickTools.AddThemeConstantOverride("icon_max_width", 16);
            _menuQuickTools.AddThemeStyleboxOverride("normal", MenuBtnNormal);
            _menuQuickTools.AddThemeStyleboxOverride("hover", MenuBtnHover);
            _menuQuickTools.AddThemeStyleboxOverride("pressed", MenuBtnPressed);
            _menuQuickTools.AddThemeStyleboxOverride("focus", MenuBtnHover);

            var popup = _menuQuickTools.GetPopup();
            popup.Clear();
            popup.AddItem("Make Loop Seamless (1-Click)", 0);
            popup.AddItem("Smooth Selected Curve (Anti-Jitter)", 1);
            popup.AddItem("Procedural Alive / Breathing Layer", 2);
            popup.AddSeparator();
            popup.AddItem("Amplify Motion (+50%)", 3);
            popup.AddItem("Dampen Motion (-50%)", 4);
            popup.AddSeparator();
            popup.AddItem("Mirror Whole Animation (Flip Clip)", 5);
            popup.AddItem("Paste Mirrored Pose (L/R)", 6);
            popup.AddSeparator();
            popup.AddItem("Reduce Keys (Active Bone Curve)", 7);
            popup.AddItem("Reduce Keys (Whole Animation)", 8);
            popup.AddSeparator();
            popup.AddItem("Retime Clip (0.5x Slower)", 9);
            popup.AddItem("Retime Clip (2.0x Faster)", 10);
            popup.AddItem("Reverse Entire Clip", 11);
            popup.AddItem("Trim Clip to Playhead Range", 12);
            popup.AddSeparator();
            popup.AddItem("Select Bone Group: Arms", 13);
            popup.AddItem("Select Bone Group: Legs", 14);
            popup.AddItem("Select Bone Group: Spine / Core", 15);
            popup.AddItem("Select Bone Group: Head", 16);
            popup.AddItem("Select Bone Group: Fingers", 17);

            var mirrorIcon = DockIcons.Load("mirror_horizontally");
            if (mirrorIcon != null)
            {
                popup.SetItemIcon(popup.GetItemIndex(5), mirrorIcon);
                popup.SetItemIcon(popup.GetItemIndex(6), mirrorIcon);
            }
            var scissorsIcon = DockIcons.Load("scissors");
            if (scissorsIcon != null)
            {
                popup.SetItemIcon(popup.GetItemIndex(7), scissorsIcon);
                popup.SetItemIcon(popup.GetItemIndex(8), scissorsIcon);
                popup.SetItemIcon(popup.GetItemIndex(12), scissorsIcon);
            }
            var reverseIcon = DockIcons.Load("reverse");
            if (reverseIcon != null)
            {
                popup.SetItemIcon(popup.GetItemIndex(11), reverseIcon);
            }

            popup.IdPressed += OnQuickToolsMenuItem;
        }

        private void OnQuickToolsMenuItem(long id)
        {
            switch (id)
            {
                case 0:
                    int blend = _spinLoopBlendFrames != null ? (int)_spinLoopBlendFrames.Value : 6;
                    AnimationAssistantHelper.MakeLoopSeamless(_activeAnimation, _skeleton, blend, _fps);
                    OnCanvasKeyframesModified();
                    break;
                case 1:
                    float strength = _sliderSmoothStrength != null ? (float)_sliderSmoothStrength.Value : 0.5f;
                    AnimationAssistantHelper.SmoothCurves(_activeAnimation, _selectedBoneName, strength);
                    OnCanvasKeyframesModified();
                    break;
                case 2:
                    float intensity = _sliderBreathingIntensity != null ? (float)_sliderBreathingIntensity.Value : 1.0f;
                    float sway = _sliderSwayIntensity != null ? (float)_sliderSwayIntensity.Value : 0.5f;
                    float bpm = _spinBreathingSpeed != null ? (float)_spinBreathingSpeed.Value : 14.0f;
                    AnimationAssistantHelper.GenerateBreathingLayer(_activeAnimation, _skeleton, intensity, sway, bpm, _fps);
                    OnCanvasKeyframesModified();
                    break;
                case 3:
                    AnimationAssistantHelper.AmplifyMotion(_activeAnimation, _selectedBoneName, 1.5f);
                    OnCanvasKeyframesModified();
                    break;
                case 4:
                    AnimationAssistantHelper.AmplifyMotion(_activeAnimation, _selectedBoneName, 0.5f);
                    OnCanvasKeyframesModified();
                    break;
                case 5:
                    AnimationAssistantHelper.MirrorWholeAnimation(_activeAnimation, _skeleton);
                    OnCanvasKeyframesModified();
                    break;
                case 6:
                    AnimationMirrorHelper.PasteMirrorPose(_skeleton);
                    CommitAutoKeyForCurrentPose();
                    break;
                case 7:
                    AnimationAssistantHelper.ReduceKeys(_activeAnimation, _selectedBoneName);
                    OnCanvasKeyframesModified();
                    break;
                case 8:
                    AnimationAssistantHelper.ReduceKeys(_activeAnimation, null);
                    OnCanvasKeyframesModified();
                    break;
                case 9:
                    AnimationAssistantHelper.RetimeClip(_activeAnimation, 0.5);
                    OnCanvasKeyframesModified();
                    break;
                case 10:
                    AnimationAssistantHelper.RetimeClip(_activeAnimation, 2.0);
                    OnCanvasKeyframesModified();
                    break;
                case 11:
                    AnimationAssistantHelper.ReverseClip(_activeAnimation);
                    OnCanvasKeyframesModified();
                    break;
                case 12:
                    AnimationAssistantHelper.TrimToRange(_activeAnimation, 0.0, _currentTime > 0.05 ? _currentTime : AnimationTransportService.Instance.Length);
                    OnCanvasKeyframesModified();
                    break;
                case 13:
                    SelectBoneGroup(HumanoidBoneMapper.BoneGroup.LeftArm, HumanoidBoneMapper.BoneGroup.RightArm);
                    break;
                case 14:
                    SelectBoneGroup(HumanoidBoneMapper.BoneGroup.LeftLeg, HumanoidBoneMapper.BoneGroup.RightLeg);
                    break;
                case 15:
                    SelectBoneGroup(HumanoidBoneMapper.BoneGroup.Pelvis, HumanoidBoneMapper.BoneGroup.Spine);
                    break;
                case 16:
                    SelectBoneGroup(HumanoidBoneMapper.BoneGroup.Head);
                    break;
                case 17:
                    SelectBoneGroup(HumanoidBoneMapper.BoneGroup.LeftHandFingers, HumanoidBoneMapper.BoneGroup.RightHandFingers);
                    break;
            }
        }

        private void SelectBoneGroup(params HumanoidBoneMapper.BoneGroup[] groups)
        {
            if (_activeAnimation == null || _skeleton == null) return;
            var targetBones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                foreach (var b in AnimationAssistantHelper.GetBonesForGroup(g))
                {
                    targetBones.Add(b);
                }
            }

            if (_dopeSheetCanvas != null)
            {
                _dopeSheetCanvas.SelectKeysForBones(targetBones);
                foreach (var row in _dopeSheetCanvas.TrackRows)
                {
                    if (targetBones.Contains(row.BoneName))
                    {
                        SelectBone(row.BoneName);
                        break;
                    }
                }
            }
        }

        private void SetupKeyActionsMenu()
        {
            if (_menuKeyActions == null) return;
            _menuKeyActions.Flat = false;
            _menuKeyActions.AddThemeStyleboxOverride("normal", MenuBtnNormal);
            _menuKeyActions.AddThemeStyleboxOverride("hover", MenuBtnHover);
            _menuKeyActions.AddThemeStyleboxOverride("pressed", MenuBtnPressed);
            _menuKeyActions.AddThemeStyleboxOverride("focus", MenuBtnHover);

            var popup = _menuKeyActions.GetPopup();
            popup.Clear();
            popup.AddItem("Insert Keyframe (Selected Bone) [I]", 0);
            popup.AddItem("Delete Selected Keys [Del]", 1);
            popup.AddItem("Duplicate Selected Keys [Shift+D]", 2);
            popup.AddSeparator();
            popup.AddItem("Select All Keys [A]", 3);
            popup.AddItem("Deselect All Keys", 4);
            popup.AddSeparator();
            popup.AddItem("Copy Pose [Ctrl+C]", 5);
            popup.AddItem("Paste Pose [Ctrl+V]", 6);
            popup.AddItem("Paste Mirror Pose [Ctrl+Shift+V]", 7);
            popup.AddSeparator();
            popup.AddItem("Export Animation to .GLB...", 8);
            popup.AddItem("Import External Animation (.glb/.fbx)...", 9);
            popup.AddSeparator();
            popup.AddItem("Apply Current Bone Tweak to Entire Clip", 10);
            var pinIcon = DockIcons.Load("pin");
            if (pinIcon != null)
            {
                popup.SetItemIcon(popup.GetItemIndex(10), pinIcon);
            }
            popup.AddItem("Toggle Additive Clip Offset Mode", 11);
            popup.AddSeparator();
            popup.AddItem("Set Ease: Linear", 12);
            popup.AddItem("Set Ease: Ease In", 13);
            popup.AddItem("Set Ease: Ease Out", 14);
            popup.AddItem("Set Ease: Ease In-Out", 15);
            popup.AddItem("Set Ease: Constant", 16);
            popup.IdPressed += OnKeyActionMenuItem;
        }

        #region Dock State & Resizing
        public void ToggleOpen()
        {
            if (_isOpen) CollapseDock(); else ExpandDock();
        }

        public void ExpandDock(float height = -1f)
        {
            _isOpen = true;
            if (height > 0) _currentDockHeight = Mathf.Clamp(height, MinDockHeight, MaxDockHeight);
            if (_contentContainer != null) _contentContainer.Visible = true;
            if (_btnToggleCollapse != null)
            {
                _btnToggleCollapse.Text = "";
                _btnToggleCollapse.Icon = DockIcons.Load("arrow_down");
            }
            ApplyDockHeight(_currentDockHeight);
            SaveDockSettings();
            if (_boneListCanvas == null || _boneListCanvas.BoneCount == 0)
            {
                PopulateBoneList();
            }
            EmitSignal(SignalName.VisibilityToggled, true);
        }

        public void CollapseDock()
        {
            _isOpen = false;
            if (_contentContainer != null) _contentContainer.Visible = false;
            if (_btnToggleCollapse != null)
            {
                _btnToggleCollapse.Text = "";
                _btnToggleCollapse.Icon = DockIcons.Load("arrow_up");
            }
            ApplyDockHeight(HeaderBarHeight);
            SaveDockSettings();
            EmitSignal(SignalName.VisibilityToggled, false);
        }

        private void ApplyDockHeight(float height)
        {
            float h = Mathf.Max(height, HeaderBarHeight);

            // In Godot, for anchors_preset = 12 (bottom-wide):
            // Firmly anchor with EdgeMargin so all borders remain cleanly visible inside the screen.
            SetAnchorAndOffset(Side.Left, 0.0f, EdgeMargin);
            SetAnchorAndOffset(Side.Right, 1.0f, -EdgeMargin);
            SetAnchorAndOffset(Side.Top, 1.0f, -(h + EdgeMargin));
            SetAnchorAndOffset(Side.Bottom, 1.0f, -EdgeMargin);
            CustomMinimumSize = new Vector2(0, h);

            UpdateFloatingViewportControls();
        }

        private void OnHeaderBarGuiInput(InputEvent @event)
        {
            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.DoubleClick)
            {
                ToggleOpen();
                GetViewport().SetInputAsHandled();
            }
        }

        private void OnResizeGripGuiInput(InputEvent @event)
        {
            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    _isResizingHeight = true;
                    _dragStartMouseY = mb.GlobalPosition.Y;
                    _dragStartHeight = _isOpen ? _currentDockHeight : HeaderBarHeight;
                    GetViewport().SetInputAsHandled();
                }
                else
                {
                    _isResizingHeight = false;
                }
            }
        }

        public override void _Input(InputEvent @event)
        {
            if (!_isResizingHeight) return;

            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && !mb.Pressed)
            {
                _isResizingHeight = false;
                SaveDockSettings();
                GetViewport().SetInputAsHandled();
                return;
            }

            if (@event is InputEventMouseMotion mm)
            {
                float deltaY = _dragStartMouseY - mm.GlobalPosition.Y;
                float targetH = _dragStartHeight + deltaY;

                if (targetH < 120f)
                {
                    if (_isOpen)
                    {
                        CollapseDock();
                    }
                }
                else
                {
                    if (!_isOpen)
                    {
                        _isOpen = true;
                        if (_contentContainer != null) _contentContainer.Visible = true;
                        if (_btnToggleCollapse != null)
                        {
                            _btnToggleCollapse.Text = "";
                            _btnToggleCollapse.Icon = DockIcons.Load("arrow_down");
                        }
                        EmitSignal(SignalName.VisibilityToggled, true);
                    }
                    _currentDockHeight = Mathf.Clamp(targetH, MinDockHeight, MaxDockHeight);
                    ApplyDockHeight(_currentDockHeight);
                }
                GetViewport().SetInputAsHandled();
            }
        }

        private void OnDrawResizeGrip()
        {
            if (_resizeGrip == null) return;
            float w = _resizeGrip.Size.X;
            float h = _resizeGrip.Size.Y;
            float midX = w * 0.5f;
            float midY = h * 0.5f;
            var col = new Color(0.65f, 0.75f, 0.85f, 0.45f);
            _resizeGrip.DrawLine(new Vector2(midX - 28f, midY), new Vector2(midX + 28f, midY), col, 2.5f);
        }
        #endregion

        #region Floating Viewport Controls Management
        private Rect2 _origCamPadOffsets;
        private Rect2 _origBadgesOffsets;
        private Rect2 _origQuickStripOffsets;
        private bool _hasSavedOrigOffsets = false;

        private void ResolveViewportControls()
        {
            if (_cachedViewportArea == null || !GodotObject.IsInstanceValid(_cachedViewportArea))
            {
                _cachedViewportArea = GetParent() as Control 
                                   ?? GetTree()?.Root?.FindChild("ViewportArea", true, false) as Control;
                if (_cachedViewportArea != null && !_cachedViewportArea.IsConnected(Control.SignalName.Resized, Callable.From(OnViewportAreaResized)))
                {
                    _cachedViewportArea.Connect(Control.SignalName.Resized, Callable.From(OnViewportAreaResized));
                }
            }

            if (_cachedViewportArea != null && GodotObject.IsInstanceValid(_cachedViewportArea))
            {
                _cachedCamPad ??= _cachedViewportArea.FindChild("CameraControlPad", true, false) as Control;
                _cachedQuickStrip ??= _cachedViewportArea.FindChild("QuickActionsStrip", true, false) as Control;
                _cachedBadges ??= _cachedViewportArea.FindChild("BottomBadgesContainer", true, false) as Control;

                if (!_hasSavedOrigOffsets)
                {
                    if (_cachedCamPad != null) _origCamPadOffsets = new Rect2(_cachedCamPad.OffsetLeft, _cachedCamPad.OffsetTop, _cachedCamPad.OffsetRight, _cachedCamPad.OffsetBottom);
                    if (_cachedBadges != null) _origBadgesOffsets = new Rect2(_cachedBadges.OffsetLeft, _cachedBadges.OffsetTop, _cachedBadges.OffsetRight, _cachedBadges.OffsetBottom);
                    if (_cachedQuickStrip != null) _origQuickStripOffsets = new Rect2(_cachedQuickStrip.OffsetLeft, _cachedQuickStrip.OffsetTop, _cachedQuickStrip.OffsetRight, _cachedQuickStrip.OffsetBottom);
                    _hasSavedOrigOffsets = true;
                }
            }
        }

        private void OnViewportAreaResized()
        {
            CallDeferred(MethodName.RefreshLayout);
        }

        private void OnViewportRootSizeChanged()
        {
            CallDeferred(MethodName.RefreshLayout);
        }

        public void RefreshLayout()
        {
            ResolveViewportControls();

            float vpHeight = _cachedViewportArea?.Size.Y ?? Size.Y;
            float maxAllowedDockH = Mathf.Clamp(vpHeight * 0.65f, MinDockHeight, MaxDockHeight);
            if (_currentDockHeight > maxAllowedDockH)
            {
                _currentDockHeight = maxAllowedDockH;
            }

            ApplyDockHeight(_isOpen ? _currentDockHeight : HeaderBarHeight);
        }

        public void UpdateFloatingViewportControls()
        {
            ResolveViewportControls();

            bool isDockActive = Visible && IsVisibleInTree();
            float vpHeight = _cachedViewportArea?.Size.Y ?? 1080f;
            float maxAllowedDockH = Mathf.Clamp(vpHeight * 0.65f, MinDockHeight, MaxDockHeight);
            if (_currentDockHeight > maxAllowedDockH) _currentDockHeight = maxAllowedDockH;

            float dockH = isDockActive ? (_isOpen ? _currentDockHeight : HeaderBarHeight) : 0f;
            float bottom = -(dockH + EdgeMargin + 14f); // 14px clear margin above dock top border
            float minTopSafe = -vpHeight + 54f; // Safe margin below top headers
            float usableHeight = vpHeight - dockH;

            if (_cachedCamPad != null && GodotObject.IsInstanceValid(_cachedCamPad))
            {
                _cachedCamPad.ZIndex = 20;
                float padH = Mathf.Max(124f, _cachedCamPad.GetCombinedMinimumSize().Y);
                float padBottom = Mathf.Clamp(bottom, minTopSafe + padH, -12f);
                _cachedCamPad.OffsetBottom = padBottom;
                _cachedCamPad.OffsetTop = padBottom - padH;
            }

            if (_cachedBadges != null && GodotObject.IsInstanceValid(_cachedBadges))
            {
                _cachedBadges.ZIndex = 20;
                if (usableHeight < 220f)
                {
                    _cachedBadges.Visible = false;
                }
                else
                {
                    _cachedBadges.Visible = true;
                    float badgeBottom = Mathf.Clamp(bottom, minTopSafe + 30f, -12f);
                    _cachedBadges.OffsetBottom = badgeBottom;
                    _cachedBadges.OffsetTop = badgeBottom - 30f;
                }
            }

            if (_cachedQuickStrip != null && GodotObject.IsInstanceValid(_cachedQuickStrip))
            {
                _cachedQuickStrip.ZIndex = 20;
                if (usableHeight < 280f)
                {
                    // Relocate to top-right when bottom is cramped
                    _cachedQuickStrip.AnchorTop = 0f;
                    _cachedQuickStrip.AnchorBottom = 0f;
                    _cachedQuickStrip.OffsetTop = 14f;
                    _cachedQuickStrip.OffsetBottom = 46f;
                }
                else
                {
                    _cachedQuickStrip.AnchorTop = 1f;
                    _cachedQuickStrip.AnchorBottom = 1f;
                    float quickBottom = Mathf.Clamp(bottom - 38f, minTopSafe + 32f, -48f);
                    _cachedQuickStrip.OffsetBottom = quickBottom;
                    _cachedQuickStrip.OffsetTop = quickBottom - 32f;
                }
            }
        }

        public void ResetFloatingViewportControls()
        {
            ResolveViewportControls();

            if (_cachedCamPad != null && GodotObject.IsInstanceValid(_cachedCamPad))
            {
                _cachedCamPad.OffsetLeft = _hasSavedOrigOffsets ? _origCamPadOffsets.Position.X : _cachedCamPad.OffsetLeft;
                _cachedCamPad.OffsetTop = _hasSavedOrigOffsets ? _origCamPadOffsets.Position.Y : -136f;
                _cachedCamPad.OffsetRight = _hasSavedOrigOffsets ? _origCamPadOffsets.Size.X : _cachedCamPad.OffsetRight;
                _cachedCamPad.OffsetBottom = _hasSavedOrigOffsets ? _origCamPadOffsets.Size.Y : -12f;
            }

            if (_cachedBadges != null && GodotObject.IsInstanceValid(_cachedBadges))
            {
                _cachedBadges.Visible = true;
                _cachedBadges.OffsetLeft = _hasSavedOrigOffsets ? _origBadgesOffsets.Position.X : _cachedBadges.OffsetLeft;
                _cachedBadges.OffsetTop = _hasSavedOrigOffsets ? _origBadgesOffsets.Position.Y : -42f;
                _cachedBadges.OffsetRight = _hasSavedOrigOffsets ? _origBadgesOffsets.Size.X : _cachedBadges.OffsetRight;
                _cachedBadges.OffsetBottom = _hasSavedOrigOffsets ? _origBadgesOffsets.Size.Y : -12f;
            }

            if (_cachedQuickStrip != null && GodotObject.IsInstanceValid(_cachedQuickStrip))
            {
                _cachedQuickStrip.AnchorTop = 1f;
                _cachedQuickStrip.AnchorBottom = 1f;
                _cachedQuickStrip.OffsetLeft = _hasSavedOrigOffsets ? _origQuickStripOffsets.Position.X : _cachedQuickStrip.OffsetLeft;
                _cachedQuickStrip.OffsetTop = _hasSavedOrigOffsets ? _origQuickStripOffsets.Position.Y : -80f;
                _cachedQuickStrip.OffsetRight = _hasSavedOrigOffsets ? _origQuickStripOffsets.Size.X : _cachedQuickStrip.OffsetRight;
                _cachedQuickStrip.OffsetBottom = _hasSavedOrigOffsets ? _origQuickStripOffsets.Size.Y : -48f;
            }
        }

        public override void _Notification(int what)
        {
            base._Notification(what);

            if (what == NotificationVisibilityChanged)
            {
                if (Visible && IsVisibleInTree())
                {
                    RefreshLayout();
                }
                else
                {
                    ResetFloatingViewportControls();
                }
            }
            else if (what == NotificationResized)
            {
                if (Visible && IsVisibleInTree())
                {
                    RefreshLayout();
                }
            }
        }

        public override void _ExitTree()
        {
            if (GetTree()?.Root != null)
            {
                GetTree().Root.SizeChanged -= OnViewportRootSizeChanged;
            }
            SkeletonGizmoManager.OnBoneSelected -= OnViewportBoneSelected;
            SkeletonGizmoManager.OnBoneTransformEnded -= OnViewportBoneTransformEnded;
            AnimationTransportService.Instance.PlaybackStateChanged -= OnTransportPlaybackStateChanged;
            AnimationTransportService.Instance.TimeChanged -= OnTransportTimeChanged;
            AnimationTransportService.Instance.LoopChanged -= OnTransportLoopChanged;
            ResetFloatingViewportControls();
        }
        #endregion

        #region Tabs & Views
        public void SwitchTab(StudioTab tab)
        {
            _currentTab = tab;
            if (_btnTabDopeSheet != null) _btnTabDopeSheet.SetPressedNoSignal(tab == StudioTab.DopeSheet);
            if (_btnTabGraphEditor != null) _btnTabGraphEditor.SetPressedNoSignal(tab == StudioTab.GraphEditor);
            if (_btnTabClipSettings != null) _btnTabClipSettings.SetPressedNoSignal(tab == StudioTab.ClipSettings);

            if (_dopeSheetView != null) _dopeSheetView.Visible = (tab == StudioTab.DopeSheet);
            if (_graphEditorView != null) _graphEditorView.Visible = (tab == StudioTab.GraphEditor);
            if (_clipAndPoseView != null) _clipAndPoseView.Visible = (tab == StudioTab.ClipSettings);

            if (tab == StudioTab.GraphEditor)
            {
                _graphEditorCanvas?.SetAnimationAndBone(_activeAnimation, _skeleton, _selectedBoneName, _fps);
            }
            else if (tab == StudioTab.DopeSheet && (_boneListCanvas == null || _boneListCanvas.BoneCount == 0))
            {
                PopulateBoneList();
            }
        }
        #endregion

        #region Target Binding
        public void BindTarget(Skeleton3D skeleton, AnimationPlayer animPlayer, Godot.Animation godotAnim, string animName, float fps = 30f)
        {
            if (skeleton == null || !GodotObject.IsInstanceValid(skeleton) || skeleton.IsQueuedForDeletion())
            {
                _skeleton = null;
                _animPlayer = null;
                _activeAnimation = null;
                _activeAnimName = "";
                _dopeSheetCanvas?.SetAnimation(null, null, fps);
                _graphEditorCanvas?.SetAnimationAndBone(null, null, "", fps);
                return;
            }

            _skeleton = skeleton;
            _animPlayer = animPlayer;
            _activeAnimation = godotAnim;
            _activeAnimName = animName;
            _fps = fps > 0 ? fps : 30f;

            AnimationTransportService.Instance.Bind(_animPlayer, _skeleton, _activeAnimName, _fps);

            if (_spinFps != null) _spinFps.SetValueNoSignal(_fps);
            if (_spinDuration != null && godotAnim != null) _spinDuration.SetValueNoSignal(godotAnim.Length);
            if (_chkLooping != null) _chkLooping.SetPressedNoSignal(AnimationTransportService.Instance.IsLooping);
            if (_btnLoop != null) _btnLoop.SetPressedNoSignal(AnimationTransportService.Instance.IsLooping);
            if (_chkAdditive != null && godotAnim != null) _chkAdditive.SetPressedNoSignal(godotAnim.HasMeta("is_additive") && godotAnim.GetMeta("is_additive").AsBool());

            // SetAnimation & SetAnimationAndBone MUST precede SetFilter so RebuildTrackRows has the new skeleton reference!
            _dopeSheetCanvas?.SetAnimation(godotAnim, skeleton, _fps);
            _graphEditorCanvas?.SetAnimationAndBone(godotAnim, skeleton, _selectedBoneName, _fps);

            string filter = _boneSearch?.Text?.Trim().ToLowerInvariant() ?? "";
            _dopeSheetCanvas?.SetFilter(filter);

            PopulateBoneList();
            UpdateTotalFramesLabel();
            ScrubToTime(0.0);

            if (!string.IsNullOrEmpty(_selectedBoneName))
            {
                _dopeSheetCanvas?.SelectTrackByBone(_selectedBoneName);
            }

            UpdateFloatingViewportControls();
        }
        #endregion

        #region Bone List (Dope Sheet Left Panel)


        private static Skeleton3D FindSkeletonRecursive(Node node)
        {
            if (node == null || !GodotObject.IsInstanceValid(node) || node.IsQueuedForDeletion()) return null;
            if (node is Skeleton3D sk && GodotObject.IsInstanceValid(sk) && !sk.IsQueuedForDeletion()) return sk;
            int childCount = node.GetChildCount();
            for (int i = 0; i < childCount; i++)
            {
                var found = FindSkeletonRecursive(node.GetChild(i));
                if (found != null) return found;
            }
            return null;
        }

        private void PopulateBoneList()
        {
            if (_boneListCanvas == null)
            {
                BindSceneNodes();
                if (_boneListCanvas == null) return;
            }

            if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton) || _skeleton.IsQueuedForDeletion())
            {
                var vpk = GetTree()?.Root?.FindChild("VpkLoaderTest", true, false) as VpkLoaderTest;
                _skeleton = FindSkeletonRecursive(vpk?.CurrentHeroNode)
                         ?? FindSkeletonRecursive(GetTree()?.Root);
            }

            if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton) || _skeleton.IsQueuedForDeletion()) return;

            string filter = _boneSearch?.Text?.Trim().ToLowerInvariant() ?? "";
            int boneCount = _skeleton.GetBoneCount();

            var filteredBones = new List<string>(boneCount);
            for (int i = 0; i < boneCount; i++)
            {
                string bName = _skeleton.GetBoneName(i);
                if (!string.IsNullOrEmpty(filter) && !bName.ToLowerInvariant().Contains(filter)) continue;
                filteredBones.Add(bName);
            }

            _boneListCanvas.SetBones(filteredBones);
            _boneListCanvas.SelectedBone = _selectedBoneName;

            if (_boneListVScrollBar != null)
            {
                float totalH = filteredBones.Count * BoneListCanvas.TrackRowHeight;
                float visibleH = _boneListCanvas.Size.Y > 0 ? _boneListCanvas.Size.Y : 200f;
                _boneListVScrollBar.MinValue = 0;
                _boneListVScrollBar.MaxValue = totalH;
                _boneListVScrollBar.Page = visibleH;
                _boneListVScrollBar.Visible = totalH > visibleH;
                _boneListVScrollBar.SetValueNoSignal(_boneListCanvas.ScrollY);
            }

            if (!string.IsNullOrEmpty(_selectedBoneName))
            {
                ScrollToBoneRow(_selectedBoneName);
            }
        }

        private void HighlightBoneRow(string boneName, bool isHover)
        {
            if (_boneListCanvas != null)
            {
                _boneListCanvas.HoveredBone = isHover ? boneName : "";
            }
        }

        public void SelectBone(string boneName)
        {
            _selectedBoneName = boneName;

            if (_boneListCanvas != null)
            {
                _boneListCanvas.SelectedBone = boneName;
            }

            // Sync with DopeSheet and GraphEditor
            _dopeSheetCanvas?.SelectTrackByBone(boneName);
            if (_currentTab == StudioTab.GraphEditor)
            {
                _graphEditorCanvas?.SetAnimationAndBone(_activeAnimation, _skeleton, boneName, _fps);
            }

            // Sync with 3D gizmo
            if (_gizmoManager == null)
            {
                _gizmoManager = GetTree()?.Root?.FindChild("SkeletonGizmoManager", true, false) as SkeletonGizmoManager;
            }
            if (_gizmoManager != null && _skeleton != null)
            {
                int bIdx = string.IsNullOrEmpty(boneName) ? -1 : _skeleton.FindBone(boneName);
                if (_gizmoManager.SelectedBoneIdx != bIdx)
                {
                    _gizmoManager.SelectBone(bIdx);
                }
            }

            // Show quick control inspector if bone is selected
            if (_boneQuickControlPanel != null)
            {
                _boneQuickControlPanel.Visible = !string.IsNullOrEmpty(boneName);
            }

            UpdateBoneQuickControlInspector();
            CaptureBaselinePose();
        }

        private void OnViewportBoneSelected(int boneIdx)
        {
            if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton) || _skeleton.IsQueuedForDeletion())
            {
                if (_gizmoManager != null && _gizmoManager.TargetSkeleton != null && GodotObject.IsInstanceValid(_gizmoManager.TargetSkeleton) && !_gizmoManager.TargetSkeleton.IsQueuedForDeletion())
                {
                    _skeleton = _gizmoManager.TargetSkeleton;
                }
            }
            if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton) || _skeleton.IsQueuedForDeletion()) return;

            string boneName = (boneIdx >= 0 && boneIdx < _skeleton.GetBoneCount()) ? _skeleton.GetBoneName(boneIdx) : "";
            if (string.Equals(_selectedBoneName, boneName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // If a search filter is currently hiding this bone, clear filter to show it
            if (!string.IsNullOrEmpty(boneName) && !string.IsNullOrEmpty(_boneSearch?.Text))
            {
                if (!boneName.ToLowerInvariant().Contains(_boneSearch.Text.Trim().ToLowerInvariant()))
                {
                    _boneSearch.Text = "";
                    _dopeSheetCanvas?.SetFilter("");
                    PopulateBoneList();
                }
            }

            SelectBone(boneName);
            ScrollToBoneRow(boneName);
        }

        private void ScrollToBoneRow(string boneName)
        {
            if (string.IsNullOrEmpty(boneName) || _boneListCanvas == null) return;
            _boneListCanvas.ScrollToBone(boneName);
            if (_boneListVScrollBar != null)
            {
                _boneListVScrollBar.SetValueNoSignal(_boneListCanvas.ScrollY);
            }
        }
        #endregion

        #region Graph Editor Channel List
        private void RebuildChannelList()
        {
            if (_channelListVBox == null || _graphEditorCanvas == null) return;

            foreach (var child in _channelListVBox.GetChildren())
            {
                child.QueueFree();
            }

            if (_lblActiveBoneChannel != null)
            {
                _lblActiveBoneChannel.Text = string.IsNullOrEmpty(_selectedBoneName) ? "No Bone Selected" : $"Channels: {_selectedBoneName}";
            }

            foreach (var curve in _graphEditorCanvas.Curves)
            {
                var row = new HBoxContainer
                {
                    CustomMinimumSize = new Vector2(0, 24),
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    MouseFilter = Control.MouseFilterEnum.Pass
                };
                row.AddThemeConstantOverride("separation", 6);

                var capturedCurve = curve;
                row.GuiInput += (ev) =>
                {
                    if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
                    {
                        if (mb.CtrlPressed)
                        {
                            _graphEditorCanvas.IsolateChannel(capturedCurve);
                        }
                        else
                        {
                            _graphEditorCanvas.SelectChannel(capturedCurve, addToSelection: mb.ShiftPressed);
                        }
                        GetViewport().SetInputAsHandled();
                    }
                };

                // Color swatch
                var swatch = new ColorRect
                {
                    CustomMinimumSize = new Vector2(10, 10),
                    Color = curve.ChannelColor,
                    SizeFlagsVertical = SizeFlags.ShrinkCenter,
                    MouseFilter = Control.MouseFilterEnum.Ignore
                };
                row.AddChild(swatch);

                // Curve name label
                var lbl = new Label
                {
                    Text = curve.Name,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                    MouseFilter = Control.MouseFilterEnum.Ignore
                };
                row.AddChild(lbl);

                // Visibility toggle button
                var btnEye = new Button
                {
                    Flat = true,
                    CustomMinimumSize = new Vector2(24, 20)
                };
                DockIcons.Apply(btnEye, curve.IsVisible ? "eye" : "eye_closed", "Toggle Visibility (Ctrl+Click row to Isolate)");
                btnEye.Modulate = curve.IsVisible ? Colors.White : new Color(0.6f, 0.6f, 0.6f, 0.7f);
                btnEye.Pressed += () =>
                {
                    capturedCurve.IsVisible = !capturedCurve.IsVisible;
                    DockIcons.Apply(btnEye, capturedCurve.IsVisible ? "eye" : "eye_closed", "Toggle Visibility (Ctrl+Click row to Isolate)");
                    btnEye.Modulate = capturedCurve.IsVisible ? Colors.White : new Color(0.6f, 0.6f, 0.6f, 0.7f);
                    _graphEditorCanvas.QueueRedraw();
                };
                row.AddChild(btnEye);

                // Lock button
                var btnLock = new Button
                {
                    Flat = true,
                    CustomMinimumSize = new Vector2(24, 20)
                };
                DockIcons.Apply(btnLock, "lock", "Toggle Lock");
                btnLock.Modulate = curve.IsLocked ? new Color(1f, 0.5f, 0.5f, 1f) : new Color(0.6f, 0.6f, 0.6f, 0.7f);
                btnLock.Pressed += () =>
                {
                    capturedCurve.IsLocked = !capturedCurve.IsLocked;
                    btnLock.Modulate = capturedCurve.IsLocked ? new Color(1f, 0.5f, 0.5f, 1f) : new Color(0.6f, 0.6f, 0.6f, 0.7f);
                };
                row.AddChild(btnLock);

                _channelListVBox.AddChild(row);
            }
        }
        #endregion

        #region 3D Bone Gizmo & Direct Controls
        private void UpdateBoneQuickControlInspector()
        {
            if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton) || _skeleton.IsQueuedForDeletion() || string.IsNullOrEmpty(_selectedBoneName))
            {
                if (_lblActiveBone != null) _lblActiveBone.Text = "No Bone Selected";
                SetBoneSlidersEnabled(false);
                return;
            }

            int boneIdx = _skeleton.FindBone(_selectedBoneName);
            if (boneIdx == -1)
            {
                if (_lblActiveBone != null) _lblActiveBone.Text = "No Bone Selected";
                SetBoneSlidersEnabled(false);
                return;
            }

            SetBoneSlidersEnabled(true);
            _isUpdatingBoneSliders = true;

            if (_lblActiveBone != null) _lblActiveBone.Text = _selectedBoneName;

            Quaternion rot = _skeleton.GetBonePoseRotation(boneIdx);
            Vector3 euler = rot.GetEuler();
            float degX = Mathf.RadToDeg(euler.X);
            float degY = Mathf.RadToDeg(euler.Y);
            float degZ = Mathf.RadToDeg(euler.Z);

            if (_sliderBoneRotX != null) _sliderBoneRotX.SetValueNoSignal(degX);
            if (_sliderBoneRotY != null) _sliderBoneRotY.SetValueNoSignal(degY);
            if (_sliderBoneRotZ != null) _sliderBoneRotZ.SetValueNoSignal(degZ);

            if (_lblBoneRotX != null) _lblBoneRotX.Text = $"Rot X: {degX:F1}°";
            if (_lblBoneRotY != null) _lblBoneRotY.Text = $"Rot Y: {degY:F1}°";
            if (_lblBoneRotZ != null) _lblBoneRotZ.Text = $"Rot Z: {degZ:F1}°";

            _isUpdatingBoneSliders = false;
        }

        private void OnBoneSliderChanged(double val, int axis)
        {
            if (_isUpdatingBoneSliders || _skeleton == null || !GodotObject.IsInstanceValid(_skeleton) || _skeleton.IsQueuedForDeletion() || string.IsNullOrEmpty(_selectedBoneName)) return;

            int boneIdx = _skeleton.FindBone(_selectedBoneName);
            if (boneIdx == -1) return;

            float degX = (float)(_sliderBoneRotX?.Value ?? 0);
            float degY = (float)(_sliderBoneRotY?.Value ?? 0);
            float degZ = (float)(_sliderBoneRotZ?.Value ?? 0);

            if (axis == 0 && _lblBoneRotX != null) _lblBoneRotX.Text = $"Rot X: {val:F1}°";
            if (axis == 1 && _lblBoneRotY != null) _lblBoneRotY.Text = $"Rot Y: {val:F1}°";
            if (axis == 2 && _lblBoneRotZ != null) _lblBoneRotZ.Text = $"Rot Z: {val:F1}°";

            Vector3 eulerRad = new(Mathf.DegToRad(degX), Mathf.DegToRad(degY), Mathf.DegToRad(degZ));
            Quaternion newRot = Quaternion.FromEuler(eulerRad).Normalized();

            _skeleton.SetBonePoseRotation(boneIdx, newRot);
            ProceduralClothSolver.Conform(_skeleton);

            // Auto-Keying trigger
            if (_isAutoKeyEnabled && !_isAdditiveClipOffsetMode)
            {
                Vector3 curPos = _skeleton.GetBonePosePosition(boneIdx);
                InsertKeyframeForBone(_selectedBoneName, curPos, newRot);
            }
        }

        private void OnResetBonePosePressed()
        {
            if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton) || _skeleton.IsQueuedForDeletion() || string.IsNullOrEmpty(_selectedBoneName)) return;
            int boneIdx = _skeleton.FindBone(_selectedBoneName);
            if (boneIdx == -1) return;

            _skeleton.ResetBonePose(boneIdx);
            ProceduralClothSolver.Conform(_skeleton);
            UpdateBoneQuickControlInspector();

            if (_isAutoKeyEnabled)
            {
                Vector3 restPos = _skeleton.GetBonePosePosition(boneIdx);
                Quaternion restRot = _skeleton.GetBonePoseRotation(boneIdx);
                InsertKeyframeForBone(_selectedBoneName, restPos, restRot);
            }
        }

        private void SetBoneSlidersEnabled(bool enabled)
        {
            if (_sliderBoneRotX != null) _sliderBoneRotX.Editable = enabled;
            if (_sliderBoneRotY != null) _sliderBoneRotY.Editable = enabled;
            if (_sliderBoneRotZ != null) _sliderBoneRotZ.Editable = enabled;
            if (_btnResetBonePose != null) _btnResetBonePose.Disabled = !enabled;
        }
        #endregion

        #region Transport & Playback
        public void SetTransportEnabled(bool enabled)
        {
            if (_btnPlayPause != null) _btnPlayPause.Disabled = !enabled;
            if (_btnRewindStart != null) _btnRewindStart.Disabled = !enabled;
            if (_btnStepBack != null) _btnStepBack.Disabled = !enabled;
            if (_btnStepNext != null) _btnStepNext.Disabled = !enabled;
            if (_btnJumpEnd != null) _btnJumpEnd.Disabled = !enabled;
            if (_btnLoop != null) _btnLoop.Disabled = !enabled;
            if (_spinCurrentFrame != null) _spinCurrentFrame.Editable = enabled;
        }

        private void OnPlayPauseToggled(bool isPlaying)
        {
            if (isPlaying)
            {
                AnimationTransportService.Instance.Play();
            }
            else
            {
                AnimationTransportService.Instance.Pause();
            }
        }

        private void OnTransportPlaybackStateChanged(bool isPlaying)
        {
            if (_btnPlayPause != null)
            {
                _btnPlayPause.SetPressedNoSignal(isPlaying);
                DockIcons.Apply(_btnPlayPause, isPlaying ? "pause" : "play", isPlaying ? "Pause Playback" : "Play Animation");
            }
        }

        private void OnTransportLoopChanged(bool looping)
        {
            _btnLoop?.SetPressedNoSignal(looping);
            _chkLooping?.SetPressedNoSignal(looping);
        }

        private string _cachedTimeText = "";
        private int _cachedFrame = -1;

        private void OnTransportTimeChanged(double time)
        {
            _currentTime = time;
            int frame = (int)Math.Round(_currentTime * _fps);
            if (frame != _cachedFrame)
            {
                _cachedFrame = frame;
                _spinCurrentFrame?.SetValueNoSignal(frame);
            }

            string timeStr = $"({_currentTime:F2}s)";
            if (_lblTimeDisplay != null && timeStr != _cachedTimeText)
            {
                _cachedTimeText = timeStr;
                _lblTimeDisplay.Text = timeStr;
            }

            _dopeSheetCanvas?.SetPlayheadTime(_currentTime);
            _graphEditorCanvas?.SetPlayheadTime(_currentTime);
        }

        public override void _Process(double delta)
        {
            AnimationTransportService.Instance.Tick(delta);
        }

        private void ScrubToFrame(int frame)
        {
            double time = frame / (double)_fps;
            ScrubToTime(time);
        }

        private void StepFrame(int delta)
        {
            AnimationTransportService.Instance.Step(delta);
        }

        public void ScrubToTime(double time)
        {
            AnimationTransportService.Instance.Seek(time, update: true, conformCloth: true);
            CaptureBaselinePose();
            UpdateBoneQuickControlInspector();
        }

        private void OnCanvasScrubbed(double time)
        {
            ScrubToTime(time);
        }

        private void OnCanvasKeyframesModified()
        {
            EmitSignal(SignalName.AnimationModified, _activeAnimName);
            UpdateTotalFramesLabel();
            _dopeSheetCanvas?.RebuildTrackRows();
            ScrubToTime(_currentTime);
        }
        #endregion

        #region Keyframe Insertion & Auto-Key
        public void InsertKeyframeForSelectedBone()
        {
            if (string.IsNullOrEmpty(_selectedBoneName) || _skeleton == null) return;
            int boneIdx = _skeleton.FindBone(_selectedBoneName);
            if (boneIdx == -1) return;

            Vector3 pos = _skeleton.GetBonePosePosition(boneIdx);
            Quaternion rot = _skeleton.GetBonePoseRotation(boneIdx);
            InsertKeyframeForBone(_selectedBoneName, pos, rot);
        }

        public void InsertKeyframeForBone(string boneName, Vector3 pos, Quaternion rot)
        {
            if (_activeAnimation == null || _skeleton == null) return;

            string skelPath = _animPlayer != null ? _animPlayer.GetPathTo(_skeleton) : "Skeleton3D";
            int posTrack = FindOrCreateTrack(Godot.Animation.TrackType.Position3D, $"{skelPath}:{boneName}");
            int rotTrack = FindOrCreateTrack(Godot.Animation.TrackType.Rotation3D, $"{skelPath}:{boneName}");

            var cmdPos = new InsertKeyframeCommand(_activeAnimation, posTrack, _currentTime, pos);
            var cmdRot = new InsertKeyframeCommand(_activeAnimation, rotTrack, _currentTime, rot);

            _undoRedo.Execute(cmdPos);
            _undoRedo.Execute(cmdRot);

            _dopeSheetCanvas?.RebuildTrackRows();
            _dopeSheetCanvas?.QueueRedraw();
            if (_currentTab == StudioTab.GraphEditor)
            {
                _graphEditorCanvas?.RebuildCurves();
                _graphEditorCanvas?.QueueRedraw();
            }

            EmitSignal(SignalName.AnimationModified, _activeAnimName);
        }

        public void RecordBoneKeyframe(string boneName, Vector3 pos, Quaternion rot)
        {
            if (!_isAutoKeyEnabled) return;
            InsertKeyframeForBone(boneName, pos, rot);
        }

        public void OnBoneTransformedByGizmo(int boneIdx, Vector3 newPos, Quaternion newRot)
        {
            if (!_isOpen || _skeleton == null || !GodotObject.IsInstanceValid(_skeleton) || _skeleton.IsQueuedForDeletion() || boneIdx < 0 || boneIdx >= _skeleton.GetBoneCount()) return;
            string boneName = _skeleton.GetBoneName(boneIdx);
            if (_isAdditiveClipOffsetMode)
            {
                UpdateBoneQuickControlInspector();
            }
            else if (_isAutoKeyEnabled)
            {
                InsertKeyframeForBone(boneName, newPos, newRot);
            }
        }

        private void OnViewportBoneTransformEnded(int boneIdx, Vector3 pos, Quaternion rot)
        {
            if (!_isOpen || _skeleton == null || !GodotObject.IsInstanceValid(_skeleton) || _skeleton.IsQueuedForDeletion()) return;
            if (_isAdditiveClipOffsetMode)
            {
                ApplyCurrentBoneTweakToEntireClip();
            }
        }

        private void CaptureBaselinePose()
        {
            if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton) || _skeleton.IsQueuedForDeletion() || string.IsNullOrEmpty(_selectedBoneName))
            {
                _hasBaselinePose = false;
                return;
            }
            int boneIdx = _skeleton.FindBone(_selectedBoneName);
            if (boneIdx == -1)
            {
                _hasBaselinePose = false;
                return;
            }
            _baselineBoneRot = _skeleton.GetBonePoseRotation(boneIdx);
            _baselineBonePos = _skeleton.GetBonePosePosition(boneIdx);
            _hasBaselinePose = true;
        }

        private void OnToggleAdditiveOffsetMode(bool enabled)
        {
            _isAdditiveClipOffsetMode = enabled;
            if (_btnToggleAdditiveOffsetMode != null)
            {
                DockIcons.Apply(_btnToggleAdditiveOffsetMode, "pin", "Clip Offset Mode", iconOnly: false);
                _btnToggleAdditiveOffsetMode.Text = enabled ? "Clip Offset: ON" : "Clip Offset: OFF";
                _btnToggleAdditiveOffsetMode.Modulate = enabled ? new Color(0.4f, 1.0f, 0.5f) : Colors.White;
            }
            if (enabled)
            {
                CaptureBaselinePose();
            }
        }

        public void ApplyCurrentBoneTweakToEntireClip()
        {
            if (_activeAnimation == null || _skeleton == null || !GodotObject.IsInstanceValid(_skeleton) || _skeleton.IsQueuedForDeletion() || string.IsNullOrEmpty(_selectedBoneName))
            {
                return;
            }

            int boneIdx = _skeleton.FindBone(_selectedBoneName);
            if (boneIdx == -1) return;

            if (!_hasBaselinePose)
            {
                CaptureBaselinePose();
                return;
            }

            Vector3 curPos = _skeleton.GetBonePosePosition(boneIdx);
            Quaternion curRot = _skeleton.GetBonePoseRotation(boneIdx).Normalized();

            Quaternion deltaRot = (_baselineBoneRot.Inverse() * curRot).Normalized();
            Vector3 deltaPos = curPos - _baselineBonePos;

            // Check if there is meaningful difference
            float rotAngle = 2f * Mathf.Acos(Mathf.Clamp(Mathf.Abs(deltaRot.W), 0f, 1f));
            if (rotAngle < 0.001f && deltaPos.LengthSquared() < 1e-6f)
            {
                return;
            }

            ApplyAdditiveOffsetToWholeClip(_selectedBoneName, deltaRot, deltaPos);

            // Update baseline to current pose after applying
            _baselineBoneRot = curRot;
            _baselineBonePos = curPos;
        }

        public void ApplyAdditiveOffsetToWholeClip(string boneName, Quaternion deltaRot, Vector3 deltaPos)
        {
            if (_activeAnimation == null || _skeleton == null || string.IsNullOrEmpty(boneName)) return;

            string skelPath = _animPlayer != null ? _animPlayer.GetPathTo(_skeleton) : "Skeleton3D";
            int rotTrack = FindOrCreateTrack(Godot.Animation.TrackType.Rotation3D, $"{skelPath}:{boneName}");
            int posTrack = FindOrCreateTrack(Godot.Animation.TrackType.Position3D, $"{skelPath}:{boneName}");

            // Ensure tracks have at least baseline keys if they were empty
            if (_activeAnimation.TrackGetKeyCount(rotTrack) == 0)
            {
                _activeAnimation.RotationTrackInsertKey(rotTrack, 0.0, _baselineBoneRot);
                if (_activeAnimation.Length > 0.01)
                {
                    _activeAnimation.RotationTrackInsertKey(rotTrack, _activeAnimation.Length, _baselineBoneRot);
                }
            }
            if (deltaPos.LengthSquared() > 1e-6f && _activeAnimation.TrackGetKeyCount(posTrack) == 0)
            {
                _activeAnimation.PositionTrackInsertKey(posTrack, 0.0, _baselineBonePos);
                if (_activeAnimation.Length > 0.01)
                {
                    _activeAnimation.PositionTrackInsertKey(posTrack, _activeAnimation.Length, _baselineBonePos);
                }
            }

            var cmd = new ApplyAdditiveClipOffsetCommand(_activeAnimation, rotTrack, posTrack, boneName, deltaRot, deltaPos);
            _undoRedo.Execute(cmd);

            OnCanvasKeyframesModified();
            GD.Print($"[AnimationStudio] Applied additive clip offset to '{boneName}' across all keyframes.");
        }

        private void CommitAutoKeyForCurrentPose()
        {
            if (!_isAutoKeyEnabled || _skeleton == null || !GodotObject.IsInstanceValid(_skeleton) || _skeleton.IsQueuedForDeletion()) return;
            for (int i = 0; i < _skeleton.GetBoneCount(); i++)
            {
                string bName = _skeleton.GetBoneName(i);
                Vector3 p = _skeleton.GetBonePosePosition(i);
                Quaternion r = _skeleton.GetBonePoseRotation(i);
                InsertKeyframeForBone(bName, p, r);
            }
        }

        private int FindOrCreateTrack(Godot.Animation.TrackType type, string trackPath)
        {
            for (int t = 0; t < _activeAnimation.GetTrackCount(); t++)
            {
                if (_activeAnimation.TrackGetPath(t) == trackPath && _activeAnimation.TrackGetType(t) == type)
                {
                    return t;
                }
            }
            int newTrack = _activeAnimation.AddTrack(type);
            _activeAnimation.TrackSetPath(newTrack, trackPath);
            return newTrack;
        }
        #endregion

        #region Clip Settings Helpers
        private void SetFps(float fps)
        {
            _fps = Math.Max(1f, fps);
            _dopeSheetCanvas?.SetAnimation(_activeAnimation, _skeleton, _fps);
            UpdateTotalFramesLabel();
        }

        private void SetDuration(float duration)
        {
            if (_activeAnimation != null)
            {
                _activeAnimation.Length = Math.Max(0.01, duration);
                _dopeSheetCanvas?.SetAnimation(_activeAnimation, _skeleton, _fps);
                UpdateTotalFramesLabel();
                EmitSignal(SignalName.AnimationModified, _activeAnimName);
            }
        }

        private void OnLoopingToggled(bool isLooping)
        {
            if (_activeAnimation != null)
            {
                _activeAnimation.LoopMode = isLooping ? Godot.Animation.LoopModeEnum.Linear : Godot.Animation.LoopModeEnum.None;
                EmitSignal(SignalName.AnimationModified, _activeAnimName);
            }
        }

        private void OnAdditiveToggled(bool isAdditive)
        {
            if (_activeAnimation != null)
            {
                _activeAnimation.SetMeta("is_additive", isAdditive);
                EmitSignal(SignalName.AnimationModified, _activeAnimName);
            }
        }

        private void UpdateTotalFramesLabel()
        {
            int total = GetTotalFrames();
            if (_lblTotalFrames != null) _lblTotalFrames.Text = $"/ {total - 1} frames";
            if (_spinCurrentFrame != null) _spinCurrentFrame.MaxValue = Math.Max(0, total - 1);
        }

        private int GetTotalFrames()
        {
            double len = _activeAnimation != null ? _activeAnimation.Length : 1.0;
            return Math.Max(1, (int)Math.Round(len * _fps) + 1);
        }

        private void UpdateUndoRedoButtons()
        {
            if (_btnUndo != null) _btnUndo.Disabled = !_undoRedo.CanUndo;
            if (_btnRedo != null) _btnRedo.Disabled = !_undoRedo.CanRedo;
        }

        private void OnKeyActionMenuItem(long id)
        {
            switch (id)
            {
                case 0: InsertKeyframeForSelectedBone(); break;
                case 1: _dopeSheetCanvas?.DeleteSelectedKeys(); break;
                case 2: _dopeSheetCanvas?.DuplicateSelectedKeys(); break;
                case 3: _dopeSheetCanvas?.SelectAllKeys(); break;
                case 4: _dopeSheetCanvas?.DeselectAllKeys(); break;
                case 5: AnimationMirrorHelper.CopyPose(_skeleton); break;
                case 6:
                    AnimationMirrorHelper.PastePose(_skeleton);
                    CommitAutoKeyForCurrentPose();
                    break;
                case 7:
                    AnimationMirrorHelper.PasteMirrorPose(_skeleton);
                    CommitAutoKeyForCurrentPose();
                    break;
                case 8: ExportToGlb(); break;
                case 9: OpenImportFileDialog(); break;
                case 10: ApplyCurrentBoneTweakToEntireClip(); break;
                case 11:
                    bool newToggled = !_isAdditiveClipOffsetMode;
                    if (_btnToggleAdditiveOffsetMode != null) _btnToggleAdditiveOffsetMode.ButtonPressed = newToggled;
                    else OnToggleAdditiveOffsetMode(newToggled);
                    break;
                case 12: _graphEditorCanvas?.SetSelectedEasePreset(GraphEditorCanvas.EasePreset.Linear); break;
                case 13: _graphEditorCanvas?.SetSelectedEasePreset(GraphEditorCanvas.EasePreset.EaseIn); break;
                case 14: _graphEditorCanvas?.SetSelectedEasePreset(GraphEditorCanvas.EasePreset.EaseOut); break;
                case 15: _graphEditorCanvas?.SetSelectedEasePreset(GraphEditorCanvas.EasePreset.EaseInOut); break;
                case 16: _graphEditorCanvas?.SetSelectedEasePreset(GraphEditorCanvas.EasePreset.Constant); break;
            }
        }

        private void ExportToGlb()
        {
            if (_skeleton == null) return;
            Node3D heroRoot = _skeleton;
            while (heroRoot.GetParent() is Node3D parent3D && !heroRoot.Name.ToString().StartsWith("Hero_"))
            {
                heroRoot = parent3D;
            }

            string exportDir = OS.GetSystemDir(OS.SystemDir.Desktop);
            string glbPath = Path.Combine(exportDir, $"{_activeAnimName ?? "hero_animation"}.glb");

            try
            {
                var gltfDoc = new GltfDocument();
                var gltfState = new GltfState();
                var err = gltfDoc.AppendFromScene(heroRoot, gltfState);
                if (err == Error.Ok)
                {
                    gltfDoc.WriteToFilesystem(gltfState, glbPath);
                    if (_lblExportStatus != null) _lblExportStatus.Text = $"Exported: {Path.GetFileName(glbPath)}";
                    GD.Print($"[AnimationStudio] Successfully exported animation to GLB: {glbPath}");
                }
            }
            catch (Exception ex)
            {
                if (_lblExportStatus != null) _lblExportStatus.Text = $"Export failed: {ex.Message}";
                GD.PrintErr($"[AnimationStudio] GLB export error: {ex.Message}");
            }
        }

        #region External Animation Importing & Retargeting
        public void OpenImportFileDialog()
        {
            if (_skeleton == null)
            {
                GD.PrintErr("[AnimationStudio] Cannot import animation: no active Skeleton3D bound.");
                return;
            }

            if (_fileDialogImport == null)
            {
                _fileDialogImport = new FileDialog
                {
                    Name = "ExternalAnimFileDialog",
                    Title = "Select External Animation (.glb, .gltf, .fbx)",
                    FileMode = FileDialog.FileModeEnum.OpenFile,
                    Access = FileDialog.AccessEnum.Filesystem,
                    Filters = new string[]
                    {
                        "*.glb ; glTF 2.0 Binary (*.glb)",
                        "*.gltf ; glTF 2.0 Text (*.gltf)",
                        "*.fbx ; FilmBox 3D (*.fbx)"
                    },
                    UseNativeDialog = true
                };

                _fileDialogImport.FileSelected += (path) =>
                {
                    Callable.From(() => OnExternalAnimFileSelected(path)).CallDeferred();
                };
                AddChild(_fileDialogImport);
            }

            _fileDialogImport.PopupCentered(new Vector2I(900, 560));
        }

        private void OnExternalAnimFileSelected(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

            GD.Print($"[AnimationStudio] Loading external animation file: {filePath}");
            var loadResult = ExternalAnimationLoader.Load(filePath);

            if (!loadResult.Success)
            {
                GD.PrintErr($"[AnimationStudio] Failed to load external animation: {loadResult.ErrorMessage}");
                if (_lblExportStatus != null)
                {
                    _lblExportStatus.Text = $"Import failed: {loadResult.ErrorMessage}";
                    _lblExportStatus.Modulate = Colors.Coral;
                }
                return;
            }

            // Instantiate Retarget Modal
            var modalScene = GD.Load<PackedScene>("res://scenes/ui/animation/AnimationRetargetModalUI.tscn")
                          ?? GD.Load<PackedScene>("res://ui/scenes/modals/AnimationRetargetModalUI.tscn");

            AnimationRetargetModalUI modal = modalScene != null
                ? modalScene.Instantiate<AnimationRetargetModalUI>()
                : new AnimationRetargetModalUI();

            modal.Name = "AnimationRetargetModal";

            var modalsLayer = GetTree()?.Root?.FindChild("ModalsLayer", true, false) as Control;
            if (modalsLayer != null)
            {
                modalsLayer.Visible = true;
                modalsLayer.AddChild(modal);
            }
            else
            {
                AddChild(modal);
            }

            modal.ModalClosed += () =>
            {
                var studioUI = GetTree()?.Root?.FindChild("UIRoot", true, false) as StudioUIManager
                            ?? GetTree()?.Root?.FindChild("StudioUIManager", true, false) as StudioUIManager;
                studioUI?.UpdateModalsState();
            };

            modal.RetargetCompleted += OnExternalRetargetCompleted;
            modal.Open(loadResult, _skeleton, _animPlayer, _activeAnimName);
        }

        private void OnExternalRetargetCompleted(Godot.Animation retargetedAnim, string clipName, bool overwriteSelected)
        {
            if (retargetedAnim == null || _animPlayer == null || _skeleton == null) return;

            string targetKey = overwriteSelected && !string.IsNullOrEmpty(_activeAnimName)
                ? _activeAnimName
                : (string.IsNullOrEmpty(clipName) ? "retarget_clip" : clipName);

            retargetedAnim.SetMeta("is_retargeted", true);
            DeadlockAnimLoader.KnownRemappedAnimations.Add(targetKey);
            string cleanTargetKey = DeadlockAnimLoader.SanitizePoseName(targetKey);
            DeadlockAnimLoader.KnownRemappedAnimations.Add(cleanTargetKey);

            var animLib = _animPlayer.HasAnimationLibrary("")
                ? _animPlayer.GetAnimationLibrary("")
                : new AnimationLibrary();

            if (!_animPlayer.HasAnimationLibrary(""))
            {
                _animPlayer.AddAnimationLibrary("", animLib);
            }

            if (animLib.HasAnimation(targetKey))
            {
                animLib.RemoveAnimation(targetKey);
            }
            animLib.AddAnimation(targetKey, retargetedAnim);

            // Re-bind Animation Studio target to retargeted animation
            BindTarget(_skeleton, _animPlayer, retargetedAnim, targetKey, fps: 30f);

            // Emit AnimationModified signal -> triggers ModWorkspaceSession.StageAnimation
            EmitSignal(SignalName.AnimationModified, targetKey);

            // Refresh PlayerTabUI animation tree so the new animation shows up under "Remapped"
            var playerTab = GetTree()?.Root?.FindChild("PoseTab", true, false) as PlayerTabUI
                         ?? GetTree()?.Root?.FindChild("PlayerTabUI", true, false) as PlayerTabUI;
            playerTab?.PopulateAnimationTreeFromPlayer();

            // Seek playhead to frame 0 and apply pose
            ScrubToTime(0.0);

            if (_lblExportStatus != null)
            {
                _lblExportStatus.Text = overwriteSelected
                    ? $"Replaced '{targetKey}' with retargeted animation (staged for VPK export)!"
                    : $"Imported & retargeted '{targetKey}' into studio!";
                _lblExportStatus.Modulate = new Color(0.40f, 0.85f, 1.0f);
            }

            GD.Print($"[AnimationStudio] Successfully applied retargeted animation '{targetKey}'. Overwrite: {overwriteSelected}");
        }
        #endregion
        #endregion
    }
}
