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
        private ScrollContainer _boneScrollContainer;
        private VBoxContainer _boneRowsVBox;
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
        private bool _isUpdatingBoneSliders = false;

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

        // Bone Row Button Styles
        private StyleBoxFlat _boneBtnNormalStyle;
        private StyleBoxFlat _boneBtnHoverStyle;
        private StyleBoxFlat _boneBtnSelectedStyle;

        // Resizing State
        private bool _isResizingHeight = false;
        private float _dragStartMouseY = 0f;
        private float _dragStartHeight = 320f;
        private float _currentDockHeight = 320f;

        public bool IsOpen => _isOpen;
        public bool IsAutoKeyEnabled => _isAutoKeyEnabled;

        public override void _Ready()
        {
            ZIndex = 5;
            BindSceneNodes();
            ConnectSignals();

            // Dock starts collapsed: only header bar (44px) visible
            CollapseDock();

            _undoRedo.HistoryChanged += UpdateUndoRedoButtons;
            UpdateUndoRedoButtons();

            SkeletonGizmoManager.OnBoneSelected += OnViewportBoneSelected;
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
            _boneScrollContainer = FindSceneNode<ScrollContainer>("BoneScrollContainer");
            _boneRowsVBox = FindSceneNode<VBoxContainer>("BoneRowsVBox");
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
            if (_btnRewindStart != null) _btnRewindStart.Pressed += () => ScrubToFrame(0);
            if (_btnStepBack != null) _btnStepBack.Pressed += () => StepFrame(-1);
            if (_btnPlayPause != null) _btnPlayPause.Toggled += OnPlayPauseToggled;
            if (_btnStepNext != null) _btnStepNext.Pressed += () => StepFrame(1);
            if (_btnJumpEnd != null) _btnJumpEnd.Pressed += () => ScrubToFrame(GetTotalFrames() - 1);

            // Frame Input
            if (_spinCurrentFrame != null) _spinCurrentFrame.ValueChanged += (val) => ScrubToFrame((int)val);

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
                    if (_boneScrollContainer != null)
                    {
                        _boneScrollContainer.ScrollVertical = (int)scrollY;
                    }
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

            if (_boneScrollContainer != null)
            {
                var vScroll = _boneScrollContainer.GetVScrollBar();
                if (vScroll != null)
                {
                    vScroll.ValueChanged += (val) =>
                    {
                        if (_dopeSheetCanvas != null)
                        {
                            _dopeSheetCanvas.ScrollY = (float)val;
                        }
                    };
                }
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
            if (_sliderBoneRotX != null) _sliderBoneRotX.ValueChanged += (v) => OnBoneSliderChanged(v, 0);
            if (_sliderBoneRotY != null) _sliderBoneRotY.ValueChanged += (v) => OnBoneSliderChanged(v, 1);
            if (_sliderBoneRotZ != null) _sliderBoneRotZ.ValueChanged += (v) => OnBoneSliderChanged(v, 2);
            if (_btnResetBonePose != null) _btnResetBonePose.Pressed += OnResetBonePosePressed;
            if (_btnDeselectBone != null) _btnDeselectBone.Pressed += () => SelectBone("");
        }

        private void SetupQuickToolsMenu()
        {
            if (_menuQuickTools == null) return;
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
            popup.AddItem("Paste Mirrored Pose (L ↔ R)", 6);
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
            }
        }

        private void SetupKeyActionsMenu()
        {
            if (_menuKeyActions == null) return;
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
            if (_btnToggleCollapse != null) _btnToggleCollapse.Text = "▼";
            ApplyDockHeight(_currentDockHeight);
            EmitSignal(SignalName.VisibilityToggled, true);
        }

        public void CollapseDock()
        {
            _isOpen = false;
            if (_contentContainer != null) _contentContainer.Visible = false;
            if (_btnToggleCollapse != null) _btnToggleCollapse.Text = "▲";
            ApplyDockHeight(HeaderBarHeight);
            EmitSignal(SignalName.VisibilityToggled, false);
        }

        private void ApplyDockHeight(float height)
        {
            float h = Mathf.Max(height, HeaderBarHeight);

            // In Godot, for anchors_preset = 12 (bottom-wide):
            // Firmly anchor the bottom edge to parent's bottom (offset 0) and top edge to -h.
            SetAnchorAndOffset(Side.Left, 0.0f, 0f);
            SetAnchorAndOffset(Side.Right, 1.0f, 0f);
            SetAnchorAndOffset(Side.Top, 1.0f, -h);
            SetAnchorAndOffset(Side.Bottom, 1.0f, 0f);
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
                GetViewport().SetInputAsHandled();
                return;
            }

            if (@event is InputEventMouseMotion mm)
            {
                float deltaY = _dragStartMouseY - mm.GlobalPosition.Y;
                float targetH = _dragStartHeight + deltaY;

                if (targetH < HeaderBarHeight + 16f)
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
                        if (_btnToggleCollapse != null) _btnToggleCollapse.Text = "▼";
                        EmitSignal(SignalName.VisibilityToggled, true);
                    }
                    _currentDockHeight = Mathf.Clamp(targetH, HeaderBarHeight + 20f, MaxDockHeight);
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
        private void ResolveViewportControls()
        {
            if (_cachedViewportArea == null || !GodotObject.IsInstanceValid(_cachedViewportArea))
            {
                _cachedViewportArea = GetParent() as Control 
                                   ?? GetTree()?.Root?.FindChild("ViewportArea", true, false) as Control;
            }

            if (_cachedViewportArea != null && GodotObject.IsInstanceValid(_cachedViewportArea))
            {
                _cachedCamPad ??= _cachedViewportArea.FindChild("CameraControlPad", true, false) as Control;
                _cachedQuickStrip ??= _cachedViewportArea.FindChild("QuickActionsStrip", true, false) as Control;
                _cachedBadges ??= _cachedViewportArea.FindChild("BottomBadgesContainer", true, false) as Control;
            }
        }

        public void UpdateFloatingViewportControls()
        {
            ResolveViewportControls();

            bool isDockActive = Visible && IsVisibleInTree();
            float dockH = isDockActive ? (_isOpen ? _currentDockHeight : HeaderBarHeight) : 0f;
            float bottom = -(dockH + 14f); // 14px clear margin above dock top

            if (_cachedCamPad != null && GodotObject.IsInstanceValid(_cachedCamPad))
            {
                _cachedCamPad.ZIndex = 20;
                float padH = Mathf.Max(124f, _cachedCamPad.GetCombinedMinimumSize().Y);
                _cachedCamPad.OffsetBottom = bottom;
                _cachedCamPad.OffsetTop = bottom - padH;
            }

            if (_cachedBadges != null && GodotObject.IsInstanceValid(_cachedBadges))
            {
                _cachedBadges.ZIndex = 20;
                _cachedBadges.OffsetBottom = bottom;
                _cachedBadges.OffsetTop = bottom - 30f;
            }

            if (_cachedQuickStrip != null && GodotObject.IsInstanceValid(_cachedQuickStrip))
            {
                _cachedQuickStrip.ZIndex = 20;
                _cachedQuickStrip.OffsetBottom = bottom - 38f;
                _cachedQuickStrip.OffsetTop = bottom - 70f;
            }
        }

        public void ResetFloatingViewportControls()
        {
            ResolveViewportControls();

            if (_cachedCamPad != null && GodotObject.IsInstanceValid(_cachedCamPad))
            {
                _cachedCamPad.OffsetBottom = -12f;
                _cachedCamPad.OffsetTop = -136f;
            }

            if (_cachedBadges != null && GodotObject.IsInstanceValid(_cachedBadges))
            {
                _cachedBadges.OffsetBottom = -12f;
                _cachedBadges.OffsetTop = -42f;
            }

            if (_cachedQuickStrip != null && GodotObject.IsInstanceValid(_cachedQuickStrip))
            {
                _cachedQuickStrip.OffsetBottom = -48f;
                _cachedQuickStrip.OffsetTop = -80f;
            }
        }

        public override void _Notification(int what)
        {
            base._Notification(what);

            if (what == NotificationVisibilityChanged)
            {
                if (Visible && IsVisibleInTree())
                {
                    UpdateFloatingViewportControls();
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
                    UpdateFloatingViewportControls();
                }
            }
        }

        public override void _ExitTree()
        {
            base._ExitTree();
            SkeletonGizmoManager.OnBoneSelected -= OnViewportBoneSelected;
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
        }
        #endregion

        #region Target Binding
        public void BindTarget(Skeleton3D skeleton, AnimationPlayer animPlayer, Godot.Animation godotAnim, string animName, float fps = 30f)
        {
            _skeleton = skeleton;
            _animPlayer = animPlayer;
            _activeAnimation = godotAnim;
            _activeAnimName = animName;
            _fps = fps > 0 ? fps : 30f;

            if (_spinFps != null) _spinFps.SetValueNoSignal(_fps);
            if (_spinDuration != null && godotAnim != null) _spinDuration.SetValueNoSignal(godotAnim.Length);
            if (_chkLooping != null && godotAnim != null) _chkLooping.SetPressedNoSignal(godotAnim.LoopMode != Godot.Animation.LoopModeEnum.None);
            if (_chkAdditive != null && godotAnim != null) _chkAdditive.SetPressedNoSignal(godotAnim.HasMeta("is_additive") && godotAnim.GetMeta("is_additive").AsBool());

            string filter = _boneSearch?.Text?.Trim().ToLowerInvariant() ?? "";
            _dopeSheetCanvas?.SetFilter(filter);
            _dopeSheetCanvas?.SetAnimation(godotAnim, skeleton, _fps);
            _graphEditorCanvas?.SetAnimationAndBone(godotAnim, skeleton, _selectedBoneName, _fps);

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
        private void InitBoneRowStyles()
        {
            if (_boneBtnNormalStyle != null) return;

            _boneBtnNormalStyle = new StyleBoxFlat
            {
                BgColor = new Color(0, 0, 0, 0),
                ContentMarginLeft = 8,
                ContentMarginRight = 4,
                ContentMarginTop = 0,
                ContentMarginBottom = 0
            };

            _boneBtnHoverStyle = new StyleBoxFlat
            {
                BgColor = new Color(1f, 1f, 1f, 0.08f),
                CornerRadiusTopLeft = 3,
                CornerRadiusBottomLeft = 3,
                CornerRadiusTopRight = 3,
                CornerRadiusBottomRight = 3,
                ContentMarginLeft = 8,
                ContentMarginRight = 4,
                ContentMarginTop = 0,
                ContentMarginBottom = 0
            };

            _boneBtnSelectedStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.20f, 0.40f, 0.70f, 0.90f), // Deep active blue
                BorderWidthLeft = 3,
                BorderColor = new Color(0.40f, 0.85f, 1.0f, 1.0f), // Cyan indicator
                CornerRadiusTopRight = 3,
                CornerRadiusBottomRight = 3,
                ContentMarginLeft = 8,
                ContentMarginRight = 4,
                ContentMarginTop = 0,
                ContentMarginBottom = 0
            };
        }

        private void PopulateBoneList()
        {
            if (_boneRowsVBox == null)
            {
                BindSceneNodes();
                if (_boneRowsVBox == null) return;
            }

            InitBoneRowStyles();

            foreach (var child in _boneRowsVBox.GetChildren())
            {
                _boneRowsVBox.RemoveChild(child);
                child.QueueFree();
            }

            if (_skeleton == null)
            {
                var vpk = GetTree()?.Root?.FindChild("VpkLoaderTest", true, false) as VpkLoaderTest;
                _skeleton = vpk?.CurrentHeroNode?.FindChild("Skeleton3D", true, false) as Skeleton3D
                         ?? GetTree()?.Root?.FindChild("Skeleton3D", true, false) as Skeleton3D;
            }

            if (_skeleton == null) return;

            string filter = _boneSearch?.Text?.Trim().ToLowerInvariant() ?? "";
            int boneCount = _skeleton.GetBoneCount();

            for (int i = 0; i < boneCount; i++)
            {
                string bName = _skeleton.GetBoneName(i);
                if (!string.IsNullOrEmpty(filter) && !bName.ToLowerInvariant().Contains(filter)) continue;

                var rowBtn = new Button
                {
                    Name = $"Row_{bName}",
                    Text = bName,
                    Alignment = HorizontalAlignment.Left,
                    CustomMinimumSize = new Vector2(0, 26),
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    FocusMode = FocusModeEnum.None
                };
                rowBtn.SetMeta("bone_name", bName);

                var fontColus = PlaygroundThemeHelper.GetFontColus();
                if (fontColus != null)
                {
                    rowBtn.AddThemeFontOverride("font", fontColus);
                }
                rowBtn.AddThemeFontSizeOverride("font_size", 13);

                bool isSelected = !string.IsNullOrEmpty(_selectedBoneName) && _selectedBoneName.Equals(bName, StringComparison.OrdinalIgnoreCase);
                ApplyBoneButtonStyle(rowBtn, isSelected, isHover: false);

                string capturedName = bName;
                rowBtn.Pressed += () => SelectBone(capturedName);
                rowBtn.MouseEntered += () =>
                {
                    _dopeSheetCanvas?.SetHoveredTrackByBone(capturedName);
                    HighlightBoneRow(capturedName, isHover: true);
                };
                rowBtn.MouseExited += () =>
                {
                    _dopeSheetCanvas?.SetHoveredTrackByBone("");
                    HighlightBoneRow(capturedName, isHover: false);
                };

                _boneRowsVBox.AddChild(rowBtn);
            }

            if (!string.IsNullOrEmpty(_selectedBoneName))
            {
                HighlightBoneRow(_selectedBoneName, isHover: false);
                ScrollToBoneRow(_selectedBoneName);
            }
        }

        private void HighlightBoneRow(string boneName, bool isHover)
        {
            if (_boneRowsVBox == null) return;
            foreach (var child in _boneRowsVBox.GetChildren())
            {
                if (child is Button btn)
                {
                    string btnBone = btn.HasMeta("bone_name")
                        ? btn.GetMeta("bone_name").AsString()
                        : (btn.Name.ToString().StartsWith("Row_") ? btn.Name.ToString().Substring(4) : btn.Name.ToString());

                    bool isSelected = !string.IsNullOrEmpty(_selectedBoneName) && _selectedBoneName.Equals(btnBone, StringComparison.OrdinalIgnoreCase);
                    bool isTarget = !string.IsNullOrEmpty(boneName) && boneName.Equals(btnBone, StringComparison.OrdinalIgnoreCase);

                    ApplyBoneButtonStyle(btn, isSelected, isHover && isTarget);
                }
            }
        }

        private void ApplyBoneButtonStyle(Button btn, bool isSelected, bool isHover)
        {
            if (isSelected)
            {
                btn.AddThemeStyleboxOverride("normal", _boneBtnSelectedStyle);
                btn.AddThemeStyleboxOverride("hover", _boneBtnSelectedStyle);
                btn.AddThemeStyleboxOverride("pressed", _boneBtnSelectedStyle);
                btn.AddThemeStyleboxOverride("focus", _boneBtnSelectedStyle);
                btn.AddThemeColorOverride("font_color", Colors.White);
                btn.AddThemeColorOverride("font_hover_color", Colors.White);
            }
            else if (isHover)
            {
                btn.AddThemeStyleboxOverride("normal", _boneBtnHoverStyle);
                btn.AddThemeStyleboxOverride("hover", _boneBtnHoverStyle);
                btn.AddThemeStyleboxOverride("pressed", _boneBtnNormalStyle);
                btn.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
                btn.AddThemeColorOverride("font_color", Colors.White);
                btn.AddThemeColorOverride("font_hover_color", Colors.White);
            }
            else
            {
                btn.AddThemeStyleboxOverride("normal", _boneBtnNormalStyle);
                btn.AddThemeStyleboxOverride("hover", _boneBtnHoverStyle);
                btn.AddThemeStyleboxOverride("pressed", _boneBtnNormalStyle);
                btn.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
                btn.AddThemeColorOverride("font_color", new Color(0.82f, 0.86f, 0.90f, 0.85f));
                btn.AddThemeColorOverride("font_hover_color", Colors.White);
            }
        }

        public void SelectBone(string boneName)
        {
            _selectedBoneName = boneName;

            // Highlight bone row
            HighlightBoneRow(boneName, isHover: false);

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
        }

        private void OnViewportBoneSelected(int boneIdx)
        {
            if (_skeleton == null)
            {
                if (_gizmoManager != null && _gizmoManager.TargetSkeleton != null)
                {
                    _skeleton = _gizmoManager.TargetSkeleton;
                }
            }
            if (_skeleton == null) return;

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
            if (string.IsNullOrEmpty(boneName) || _boneRowsVBox == null || _boneScrollContainer == null) return;

            int index = 0;
            foreach (var child in _boneRowsVBox.GetChildren())
            {
                if (child is Button btn)
                {
                    string btnBone = btn.HasMeta("bone_name")
                        ? btn.GetMeta("bone_name").AsString()
                        : (btn.Name.ToString().StartsWith("Row_") ? btn.Name.ToString().Substring(4) : btn.Name.ToString());
                    if (btnBone.Equals(boneName, StringComparison.OrdinalIgnoreCase))
                    {
                        float targetY = index * 26f;
                        float currentScroll = _boneScrollContainer.ScrollVertical;
                        float visibleHeight = _boneScrollContainer.Size.Y;
                        if (visibleHeight <= 0) visibleHeight = 150f;

                        if (targetY < currentScroll || targetY + 26f > currentScroll + visibleHeight)
                        {
                            int newScroll = Mathf.Max(0, (int)(targetY - visibleHeight / 2f + 13f));
                            _boneScrollContainer.ScrollVertical = newScroll;
                            if (_dopeSheetCanvas != null)
                            {
                                _dopeSheetCanvas.ScrollY = newScroll;
                            }
                        }
                        break;
                    }
                    index++;
                }
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
                    SizeFlagsHorizontal = SizeFlags.ExpandFill
                };
                row.AddThemeConstantOverride("separation", 6);

                // Color swatch
                var swatch = new ColorRect
                {
                    CustomMinimumSize = new Vector2(10, 10),
                    Color = curve.ChannelColor,
                    SizeFlagsVertical = SizeFlags.ShrinkCenter
                };
                row.AddChild(swatch);

                // Curve name label
                var lbl = new Label
                {
                    Text = curve.Name,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
                };
                row.AddChild(lbl);

                // Visibility toggle button
                var btnEye = new Button
                {
                    Text = curve.IsVisible ? "Vis" : "Hide",
                    Flat = true,
                    CustomMinimumSize = new Vector2(32, 20)
                };
                btnEye.AddThemeFontSizeOverride("font_size", 10);
                var capturedCurve = curve;
                btnEye.Pressed += () =>
                {
                    capturedCurve.IsVisible = !capturedCurve.IsVisible;
                    btnEye.Text = capturedCurve.IsVisible ? "Vis" : "Hide";
                    btnEye.Modulate = capturedCurve.IsVisible ? Colors.White : new Color(0.6f, 0.6f, 0.6f, 0.7f);
                    _graphEditorCanvas.QueueRedraw();
                };
                row.AddChild(btnEye);

                // Lock button
                var btnLock = new Button
                {
                    Text = curve.IsLocked ? "Lock" : "Free",
                    Flat = true,
                    CustomMinimumSize = new Vector2(32, 20)
                };
                btnLock.AddThemeFontSizeOverride("font_size", 10);
                btnLock.Pressed += () =>
                {
                    capturedCurve.IsLocked = !capturedCurve.IsLocked;
                    btnLock.Text = capturedCurve.IsLocked ? "Lock" : "Free";
                    btnLock.Modulate = capturedCurve.IsLocked ? new Color(1f, 0.5f, 0.5f, 1f) : Colors.White;
                };
                row.AddChild(btnLock);

                _channelListVBox.AddChild(row);
            }
        }
        #endregion

        #region 3D Bone Gizmo & Direct Controls
        private void UpdateBoneQuickControlInspector()
        {
            if (_skeleton == null || string.IsNullOrEmpty(_selectedBoneName))
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
            if (_isUpdatingBoneSliders || _skeleton == null || string.IsNullOrEmpty(_selectedBoneName)) return;

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
            if (_isAutoKeyEnabled)
            {
                Vector3 curPos = _skeleton.GetBonePosePosition(boneIdx);
                InsertKeyframeForBone(_selectedBoneName, curPos, newRot);
            }
        }

        private void OnResetBonePosePressed()
        {
            if (_skeleton == null || string.IsNullOrEmpty(_selectedBoneName)) return;
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
        private void OnPlayPauseToggled(bool isPlaying)
        {
            if (_animPlayer == null || string.IsNullOrEmpty(_activeAnimName)) return;

            if (isPlaying)
            {
                if (_btnPlayPause != null) _btnPlayPause.Text = "⏸";
                _animPlayer.Play(_activeAnimName);
            }
            else
            {
                if (_btnPlayPause != null) _btnPlayPause.Text = "▶";
                _animPlayer.Pause();
            }
        }

        public override void _Process(double delta)
        {
            if (Visible && IsVisibleInTree())
            {
                ResolveViewportControls();
                float expectedBottom = -((_isOpen ? _currentDockHeight : HeaderBarHeight) + 14f);
                if (_cachedCamPad != null && GodotObject.IsInstanceValid(_cachedCamPad))
                {
                    if (Mathf.Abs(_cachedCamPad.OffsetBottom - expectedBottom) > 1.0f)
                    {
                        UpdateFloatingViewportControls();
                    }
                }
            }

            if (_isOpen && _animPlayer != null && _animPlayer.IsPlaying())
            {
                _currentTime = _animPlayer.CurrentAnimationPosition;
                int frame = (int)Math.Round(_currentTime * _fps);
                _spinCurrentFrame?.SetValueNoSignal(frame);
                if (_lblTimeDisplay != null) _lblTimeDisplay.Text = $"({_currentTime:F2}s)";

                _dopeSheetCanvas?.SetPlayheadTime(_currentTime);
                _graphEditorCanvas?.SetPlayheadTime(_currentTime);

                if (_skeleton != null)
                {
                    _skeleton.ForceUpdateAllBoneTransforms();
                    ProceduralClothSolver.MarkDirty();
                    ProceduralClothSolver.Conform(_skeleton, force: true, _animPlayer);
                }
            }
        }

        private void ScrubToFrame(int frame)
        {
            double time = frame / (double)_fps;
            ScrubToTime(time);
        }

        private void StepFrame(int delta)
        {
            int curFrame = (int)Math.Round(_currentTime * _fps);
            ScrubToFrame(curFrame + delta);
        }

        public void ScrubToTime(double time)
        {
            double dur = _activeAnimation != null ? Math.Max(0.01, _activeAnimation.Length) : 1.0;
            _currentTime = Math.Clamp(time, 0.0, dur);

            int frame = (int)Math.Round(_currentTime * _fps);
            _spinCurrentFrame?.SetValueNoSignal(frame);
            if (_lblTimeDisplay != null) _lblTimeDisplay.Text = $"({_currentTime:F2}s)";

            _dopeSheetCanvas?.SetPlayheadTime(_currentTime);
            _graphEditorCanvas?.SetPlayheadTime(_currentTime);

            if (_animPlayer != null && !string.IsNullOrEmpty(_activeAnimName) && _animPlayer.HasAnimation(_activeAnimName))
            {
                _animPlayer.Seek(_currentTime, update: true);
            }

            if (_skeleton != null)
            {
                _skeleton.ForceUpdateAllBoneTransforms();
                ProceduralClothSolver.MarkDirty();
                ProceduralClothSolver.Conform(_skeleton, force: true, _animPlayer);
            }
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
            if (!_isOpen || !_isAutoKeyEnabled || _skeleton == null || boneIdx < 0 || boneIdx >= _skeleton.GetBoneCount()) return;
            string boneName = _skeleton.GetBoneName(boneIdx);
            InsertKeyframeForBone(boneName, newPos, newRot);
        }

        private void CommitAutoKeyForCurrentPose()
        {
            if (!_isAutoKeyEnabled || _skeleton == null) return;
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
