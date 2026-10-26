using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using DeadlockPlayground.Animation;

namespace DeadlockPlayground.UI
{
    /// <summary>
    /// Interactive dialog for retargeting external animation clips (.glb, .gltf, .fbx)
    /// onto Deadlock Source 2 hero skeletons with live calibration controls, precision options,
    /// tabbed layout, bone mapping dictionary, and VPK replacement mode.
    /// </summary>
    public partial class AnimationRetargetModalUI : PanelContainer
    {
        [Signal] public delegate void RetargetCompletedEventHandler(Godot.Animation anim, string clipName, bool overwriteSelected);
        [Signal] public delegate void ModalClosedEventHandler();

        private Control _backdrop;
        private PanelContainer _cardContainer;

        // Header
        private Label _lblTitle;
        private Label _lblSubtitle;
        private Button _btnClose;

        // Top Section: Clip & Mode Selection
        private Label _lblFileInfo;
        private OptionButton _optSourceClip;
        private LineEdit _txtTargetAnimName;
        private CheckBox _chkOverwriteActive;
        private Label _lblActiveAnimInfo;

        // Main Tab Container
        private TabContainer _tabContainer;

        // Tab 1: Settings & Precision Options
        private OptionButton _optSourcePoseMode;
        private OptionButton _optSpineMode;

        // Calibration Sliders
        private HSlider _sliderArmPitch;
        private Label _lblArmPitchVal;
        private HSlider _sliderArmClearance;
        private Label _lblArmClearanceVal;
        private HSlider _sliderSpinePitch;
        private Label _lblSpinePitchVal;
        private HSlider _sliderPelvisScale;
        private Label _lblPelvisScaleVal;
        private HSlider _sliderFloorOffset;
        private Label _lblFloorOffsetVal;
        private CheckBox _chkLockHorizontalRootMotion;
        private CheckBox _chkAutoGroundFeet;
        private CheckBox _chkEnableFootIK;
        private CheckBox _chkSoftIK;
        private CheckBox _chkSmoothKnees;
        private CheckBox _chkAnatomicalKneePole;
        private Label _lblPoseStatus;

        // Quick Presets
        private Button _btnPresetMixamo;
        private Button _btnPresetMesh2Motion;
        private Button _btnResetSettings;

        // Tab 2: Bone Mapping
        private LineEdit _txtBoneSearch;
        private Label _lblMappingStats;
        private CheckBox _chkShowAllHeroBones;
        private Control _heroBonesHeaderRow;
        private ScrollContainer _scrollBones;
        private VBoxContainer _vboxBoneRows;
        private Button _btnResetMapping;

        // Footer Action Buttons
        private Button _btnBrowseMixamo;
        private Button _btnBrowseMesh2Motion;
        private Button _btnCancel;
        private Button _btnRetarget;

        // State
        private ExternalAnimationLoader.LoadResult _loadResult;
        private Skeleton3D _targetSkeleton;
        private AnimationPlayer _animPlayer;
        private string _activeAnimName = "";
        private readonly List<BoneRowData> _boneRows = new();
        private Dictionary<string, string> _initialAutoMapping = new(StringComparer.OrdinalIgnoreCase);

        private class BoneRowData
        {
            public string TargetBoneName;
            public HumanoidBoneMapper.BoneDefinition Definition;
            public Control RowContainer;
            public OptionButton OptionSourceBone;
            public Label LabelTarget;
            public Panel RegionBadge;
            public bool IsHeroSpecific;
        }

        public override void _Ready()
        {
            EnsureUIBuilt();
        }

        private void EnsureUIBuilt()
        {
            if (_cardContainer != null) return;
            Theme = GD.Load<Theme>("res://assets/themes/PlaygroundTheme.tres");
            BuildModalUI();
        }

        public void Open(ExternalAnimationLoader.LoadResult loadResult, Skeleton3D targetSkeleton, string currentActiveAnimName = "")
            => Open(loadResult, targetSkeleton, null, currentActiveAnimName);

        public void Open(ExternalAnimationLoader.LoadResult loadResult, Skeleton3D targetSkeleton, AnimationPlayer animPlayer, string currentActiveAnimName = "")
        {
            EnsureUIBuilt();

            _loadResult = loadResult;
            _targetSkeleton = targetSkeleton;
            _animPlayer = animPlayer;
            _activeAnimName = currentActiveAnimName ?? "";

            if (_loadResult == null || _loadResult.SourceSkeleton == null || _targetSkeleton == null)
            {
                GD.PrintErr("[AnimationRetargetModal] Cannot open: invalid load result or skeleton.");
                Close();
                return;
            }

            // Populate File & Clip Info
            if (_lblFileInfo != null)
            {
                _lblFileInfo.Text = $"{_loadResult.FileName}  •  Source Skeleton: {_loadResult.SourceSkeleton.GetBoneCount()} bones  •  {_loadResult.Animations.Count} clip(s)";
            }

            if (_lblPoseStatus != null)
            {
                if (!string.IsNullOrEmpty(_loadResult.ReferencePoseDescription))
                {
                    _lblPoseStatus.Text = _loadResult.ReferencePoseDescription;
                    _lblPoseStatus.Visible = true;
                }
                else
                {
                    _lblPoseStatus.Visible = false;
                }
            }

            // Populate Clip Dropdown
            if (_optSourceClip != null)
            {
                _optSourceClip.Clear();
                int idx = 0;
                foreach (var (cName, anim) in _loadResult.Animations)
                {
                    int frames = (int)Math.Round(anim.Length * 30.0) + 1;
                    _optSourceClip.AddItem($"{cName} ({anim.Length:F2}s, {frames}f)", idx);
                    _optSourceClip.SetItemMetadata(idx, cName);
                    idx++;
                }

                if (_optSourceClip.ItemCount > 0)
                {
                    _optSourceClip.Select(0);
                    OnSourceClipSelected(0);
                }
            }

            // Configure Overwrite Active Mode
            if (_chkOverwriteActive != null)
            {
                bool hasActive = !string.IsNullOrEmpty(_activeAnimName);
                _chkOverwriteActive.Disabled = !hasActive;
                _chkOverwriteActive.ButtonPressed = false;

                if (_lblActiveAnimInfo != null)
                {
                    _lblActiveAnimInfo.Text = hasActive
                        ? $"(Active Clip: '{_activeAnimName}' - stages for VPK export)"
                        : "(No active clip selected in Animation Studio)";
                    _lblActiveAnimInfo.Visible = hasActive;
                }
            }

            // Generate Auto-Mapping
            _initialAutoMapping = HumanoidBoneMapper.AutoMap(_loadResult.SourceSkeleton, _targetSkeleton);
            BuildBoneMappingRows();

            // Reset Settings to Defaults
            ResetSettingsToDefaults();

            Visible = true;
        }

        public void Close()
        {
            Visible = false;
            _loadResult?.Dispose();
            _loadResult = null;
            EmitSignal(SignalName.ModalClosed);
            QueueFree();
        }

        private void BuildModalUI()
        {
            // Fullscreen backdrop overlay
            AnchorsPreset = (int)LayoutPreset.FullRect;
            AnchorLeft = 0f;
            AnchorTop = 0f;
            AnchorRight = 1f;
            AnchorBottom = 1f;
            OffsetLeft = 0f;
            OffsetTop = 0f;
            OffsetRight = 0f;
            OffsetBottom = 0f;
            ZIndex = 50;

            // Semi-transparent backdrop dimmer
            _backdrop = new Panel
            {
                Name = "BackdropDimmer",
                AnchorsPreset = (int)LayoutPreset.FullRect,
                AnchorLeft = 0f,
                AnchorTop = 0f,
                AnchorRight = 1f,
                AnchorBottom = 1f,
                OffsetLeft = 0f,
                OffsetTop = 0f,
                OffsetRight = 0f,
                OffsetBottom = 0f,
                MouseFilter = MouseFilterEnum.Stop
            };
            var dimmerStyle = new StyleBoxFlat { BgColor = new Color(0.04f, 0.05f, 0.08f, 0.80f) };
            _backdrop.AddThemeStyleboxOverride("panel", dimmerStyle);
            AddChild(_backdrop);

            // Centered Main Card Panel
            _cardContainer = new PanelContainer
            {
                Name = "CardContainer",
                CustomMinimumSize = new Vector2(1060, 720),
                AnchorsPreset = (int)LayoutPreset.Center,
                AnchorLeft = 0.5f,
                AnchorTop = 0.5f,
                AnchorRight = 0.5f,
                AnchorBottom = 0.5f,
                GrowHorizontal = GrowDirection.Both,
                GrowVertical = GrowDirection.Both,
                OffsetLeft = -530f,
                OffsetTop = -360f,
                OffsetRight = 530f,
                OffsetBottom = 360f
            };
            AddChild(_cardContainer);

            var cardMargin = new MarginContainer
            {
                Name = "CardMargin"
            };
            cardMargin.AddThemeConstantOverride("margin_left", 20);
            cardMargin.AddThemeConstantOverride("margin_top", 18);
            cardMargin.AddThemeConstantOverride("margin_right", 20);
            cardMargin.AddThemeConstantOverride("margin_bottom", 18);
            _cardContainer.AddChild(cardMargin);

            var mainVBox = new VBoxContainer
            {
                Name = "MainVBox"
            };
            mainVBox.AddThemeConstantOverride("separation", 10);
            cardMargin.AddChild(mainVBox);

            // 1. Header Bar
            var headerHBox = new HBoxContainer();
            mainVBox.AddChild(headerHBox);

            var titleVBox = new VBoxContainer();
            titleVBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            headerHBox.AddChild(titleVBox);

            _lblTitle = new Label
            {
                Text = "External Animation Retargeting & Importer",
                ThemeTypeVariation = "HeaderLarge"
            };
            var fontColus = PlaygroundThemeHelper.GetFontColus();
            if (fontColus != null) _lblTitle.AddThemeFontOverride("font", fontColus);
            _lblTitle.AddThemeFontSizeOverride("font_size", 21);
            titleVBox.AddChild(_lblTitle);

            _lblSubtitle = new Label
            {
                Text = "Map Mixamo, Mesh2Motion, or external .glb/.gltf/.fbx clips to Deadlock skeletons for live studio editing and VPK mod export."
            };
            _lblSubtitle.AddThemeColorOverride("font_color", new Color(0.70f, 0.75f, 0.85f, 0.85f));
            _lblSubtitle.AddThemeFontSizeOverride("font_size", 13);
            titleVBox.AddChild(_lblSubtitle);

            _btnClose = new Button
            {
                CustomMinimumSize = new Vector2(32, 32),
                TooltipText = "Close"
            };
            var crossIcon = GD.Load<Texture2D>("res://assets/at-icons/cross.svg");
            if (crossIcon != null) _btnClose.Icon = crossIcon;
            else _btnClose.Text = "✕";
            _btnClose.Pressed += Close;
            headerHBox.AddChild(_btnClose);

            mainVBox.AddChild(new HSeparator());

            // 2. Top Section: Clip Selection & Target Naming (2-Column Spacious Layout)
            var clipPanel = new PanelContainer();
            mainVBox.AddChild(clipPanel);

            var clipMargin = new MarginContainer();
            clipMargin.AddThemeConstantOverride("margin_left", 14);
            clipMargin.AddThemeConstantOverride("margin_top", 10);
            clipMargin.AddThemeConstantOverride("margin_right", 14);
            clipMargin.AddThemeConstantOverride("margin_bottom", 10);
            clipPanel.AddChild(clipMargin);

            var clipVBox = new VBoxContainer();
            clipVBox.AddThemeConstantOverride("separation", 8);
            clipMargin.AddChild(clipVBox);

            _lblFileInfo = new Label
            {
                Text = "Source File: (None)"
            };
            _lblFileInfo.AddThemeColorOverride("font_color", new Color(0.95f, 0.75f, 0.30f, 1f));
            _lblFileInfo.AddThemeFontSizeOverride("font_size", 13);
            clipVBox.AddChild(_lblFileInfo);

            _lblPoseStatus = new Label
            {
                Text = "",
                Visible = false
            };
            _lblPoseStatus.AddThemeColorOverride("font_color", new Color(0.35f, 0.95f, 0.60f, 1f));
            _lblPoseStatus.AddThemeFontSizeOverride("font_size", 12);
            clipVBox.AddChild(_lblPoseStatus);

            var clipGrid = new GridContainer
            {
                Columns = 2
            };
            clipGrid.AddThemeConstantOverride("h_separation", 20);
            clipGrid.AddThemeConstantOverride("v_separation", 4);
            clipVBox.AddChild(clipGrid);

            // Column 1: Source Clip
            var colSourceVBox = new VBoxContainer();
            colSourceVBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            var lblSourceClip = new Label { Text = "Source Animation Clip:" };
            lblSourceClip.AddThemeFontSizeOverride("font_size", 13);
            colSourceVBox.AddChild(lblSourceClip);

            _optSourceClip = new OptionButton
            {
                CustomMinimumSize = new Vector2(300, 32),
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            _optSourceClip.AddThemeFontSizeOverride("font_size", 14);
            _optSourceClip.ItemSelected += (long id) => OnSourceClipSelected((int)id);
            colSourceVBox.AddChild(_optSourceClip);
            clipGrid.AddChild(colSourceVBox);

            // Column 2: Target Clip Name
            var colTargetVBox = new VBoxContainer();
            colTargetVBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            var lblTargetName = new Label { Text = "Target Clip Name:" };
            lblTargetName.AddThemeFontSizeOverride("font_size", 13);
            colTargetVBox.AddChild(lblTargetName);

            _txtTargetAnimName = new LineEdit
            {
                CustomMinimumSize = new Vector2(340, 32),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                PlaceholderText = "retarget_animation"
            };
            if (fontColus != null) _txtTargetAnimName.AddThemeFontOverride("font", fontColus);
            _txtTargetAnimName.AddThemeFontSizeOverride("font_size", 14);
            colTargetVBox.AddChild(_txtTargetAnimName);
            clipGrid.AddChild(colTargetVBox);

            // VPK Overwrite Mode row
            var overwriteHBox = new HBoxContainer();
            overwriteHBox.AddThemeConstantOverride("separation", 10);
            clipVBox.AddChild(overwriteHBox);

            _chkOverwriteActive = new CheckBox
            {
                Text = "Apply to Currently Selected Hero Animation (For VPK Export)",
                TooltipText = "Directly replaces the currently selected animation clip in Animation Studio, staging it for instant .vpk export via Source2ClipPatcher!"
            };
            _chkOverwriteActive.AddThemeFontSizeOverride("font_size", 13);
            _chkOverwriteActive.Toggled += OnOverwriteActiveToggled;
            overwriteHBox.AddChild(_chkOverwriteActive);

            _lblActiveAnimInfo = new Label();
            _lblActiveAnimInfo.AddThemeColorOverride("font_color", new Color(0.40f, 0.85f, 1.0f, 0.9f));
            _lblActiveAnimInfo.AddThemeFontSizeOverride("font_size", 13);
            overwriteHBox.AddChild(_lblActiveAnimInfo);

            // 3. Tab Container: [Retarget Settings] | [Bone Mapping]
            _tabContainer = new TabContainer
            {
                SizeFlagsVertical = SizeFlags.ExpandFill
            };
            mainVBox.AddChild(_tabContainer);

            var slidersIcon = GD.Load<Texture2D>("res://assets/at-icons/sliders.svg");
            var boneIcon = GD.Load<Texture2D>("res://assets/at-icons/bone.svg");
            var cogIcon = GD.Load<Texture2D>("res://assets/at-icons/cog.svg");
            var resetIcon = GD.Load<Texture2D>("res://assets/at-icons/arrow_counterclockwise.svg");
            var footstepsIcon = GD.Load<Texture2D>("res://assets/at-icons/footsteps.svg");

            // ==========================================
            // TAB 1: Retarget Settings & Calibration
            // ==========================================
            var tabSettings = new MarginContainer
            {
                Name = "RetargetSettingsTab"
            };
            tabSettings.AddThemeConstantOverride("margin_left", 14);
            tabSettings.AddThemeConstantOverride("margin_top", 12);
            tabSettings.AddThemeConstantOverride("margin_right", 14);
            tabSettings.AddThemeConstantOverride("margin_bottom", 12);
            _tabContainer.AddChild(tabSettings);
            _tabContainer.SetTabTitle(0, "Retarget Settings");
            if (slidersIcon != null) _tabContainer.SetTabIcon(0, slidersIcon);

            var settingsScroll = new ScrollContainer
            {
                SizeFlagsVertical = SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };
            tabSettings.AddChild(settingsScroll);

            var settingsVBox = new VBoxContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            settingsVBox.AddThemeConstantOverride("separation", 14);
            settingsScroll.AddChild(settingsVBox);

            // A. Precision Retargeting Modes
            var precisionPanel = new PanelContainer();
            settingsVBox.AddChild(precisionPanel);
            var precisionMargin = new MarginContainer();
            precisionMargin.AddThemeConstantOverride("margin_left", 12);
            precisionMargin.AddThemeConstantOverride("margin_top", 10);
            precisionMargin.AddThemeConstantOverride("margin_right", 12);
            precisionMargin.AddThemeConstantOverride("margin_bottom", 10);
            precisionPanel.AddChild(precisionMargin);

            var precisionVBox = new VBoxContainer();
            precisionVBox.AddThemeConstantOverride("separation", 10);
            precisionMargin.AddChild(precisionVBox);

            var lblPrecisionHead = new Label { Text = "Precision Alignment & Resampling Modes:", ThemeTypeVariation = "HeaderSmall" };
            lblPrecisionHead.AddThemeFontSizeOverride("font_size", 15);
            precisionVBox.AddChild(lblPrecisionHead);

            var precisionGrid = new GridContainer { Columns = 2 };
            precisionGrid.AddThemeConstantOverride("h_separation", 24);
            precisionGrid.AddThemeConstantOverride("v_separation", 8);
            precisionVBox.AddChild(precisionGrid);

            // Source Pose Mode
            var poseVBox = new VBoxContainer();
            poseVBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            var lblPoseMode = new Label { Text = "Source Reference Pose Mode:" };
            lblPoseMode.AddThemeFontSizeOverride("font_size", 13);
            poseVBox.AddChild(lblPoseMode);

            _optSourcePoseMode = new OptionButton
            {
                CustomMinimumSize = new Vector2(280, 28),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TooltipText = "Select how the source skeleton's reference rest pose is established."
            };
            _optSourcePoseMode.AddItem("Auto-Detect (Geometry Vector Match)", 0);
            _optSourcePoseMode.AddItem("Strict T-Pose (Mixamo / Mesh2Motion Default)", 1);
            _optSourcePoseMode.AddItem("A-Pose (30° / 45° Arms)", 2);
            _optSourcePoseMode.AddItem("Use Frame 0 as Reference Pose", 3);
            _optSourcePoseMode.Select(0);
            poseVBox.AddChild(_optSourcePoseMode);
            precisionGrid.AddChild(poseVBox);

            // Spine Resampling Mode
            var spineModeVBox = new VBoxContainer();
            spineModeVBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            var lblSpineMode = new Label { Text = "Spine Retargeting Mode:" };
            lblSpineMode.AddThemeFontSizeOverride("font_size", 13);
            spineModeVBox.AddChild(lblSpineMode);

            _optSpineMode = new OptionButton
            {
                CustomMinimumSize = new Vector2(280, 28),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TooltipText = "Smooth 4-Bone Spline eliminates kinks and body-roll deformation by resampling 3-spine rigs across Deadlock's 4 vertebrae."
            };
            _optSpineMode.AddItem("Smooth 4-Bone Spline (Recommended)", 0);
            _optSpineMode.AddItem("Direct 1:1 Bone Map", 1);
            _optSpineMode.Select(0);
            spineModeVBox.AddChild(_optSpineMode);
            precisionGrid.AddChild(spineModeVBox);

            // B. Calibration Sliders Panel
            var calibPanel = new PanelContainer();
            settingsVBox.AddChild(calibPanel);

            var calibMargin = new MarginContainer();
            calibMargin.AddThemeConstantOverride("margin_left", 12);
            calibMargin.AddThemeConstantOverride("margin_top", 10);
            calibMargin.AddThemeConstantOverride("margin_right", 12);
            calibMargin.AddThemeConstantOverride("margin_bottom", 10);
            calibPanel.AddChild(calibMargin);

            var calibVBox = new VBoxContainer();
            calibVBox.AddThemeConstantOverride("separation", 10);
            calibMargin.AddChild(calibVBox);

            var lblCalibHead = new Label { Text = "Stance, Clearance & Posture Calibration:", ThemeTypeVariation = "HeaderSmall" };
            lblCalibHead.AddThemeFontSizeOverride("font_size", 15);
            calibVBox.AddChild(lblCalibHead);

            var calibGrid = new GridContainer
            {
                Columns = 4
            };
            calibGrid.AddThemeConstantOverride("h_separation", 16);
            calibGrid.AddThemeConstantOverride("v_separation", 8);
            calibVBox.AddChild(calibGrid);

            // Arm Pitch (T-Pose vs A-Pose)
            var lblArm = new Label { Text = "Arm Pitch Offset:" };
            lblArm.AddThemeFontSizeOverride("font_size", 13);
            calibGrid.AddChild(lblArm);

            var armBox = new HBoxContainer();
            armBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _sliderArmPitch = new HSlider
            {
                MinValue = -60.0,
                MaxValue = 60.0,
                Step = 1.0,
                Value = 0.0,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                TooltipText = "Manual arm pitch artistic tweak (-60° to +60°). Defaults to 0°: computed dynamically from geometry."
            };
            _sliderArmPitch.ValueChanged += (double _) => UpdateArmPitchLabel();
            armBox.AddChild(_sliderArmPitch);

            _lblArmPitchVal = new Label { CustomMinimumSize = new Vector2(45, 0), Text = "0°" };
            _lblArmPitchVal.AddThemeFontSizeOverride("font_size", 13);
            armBox.AddChild(_lblArmPitchVal);
            calibGrid.AddChild(armBox);

            // Arm Clearance / Width (Outward spread for bulky jackets/armor)
            var lblClear = new Label { Text = "Arm Clearance / Width:" };
            lblClear.AddThemeFontSizeOverride("font_size", 13);
            calibGrid.AddChild(lblClear);

            var clearBox = new HBoxContainer();
            clearBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _sliderArmClearance = new HSlider
            {
                MinValue = -20.0,
                MaxValue = 20.0,
                Step = 1.0,
                Value = 0.0,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                TooltipText = "Adjusts outward arm clearance angle (-20° to +20°). Expands outward to prevent arms sinking into bulky jackets (e.g. Holliday) or armor."
            };
            _sliderArmClearance.ValueChanged += (double _) => UpdateArmClearanceLabel();
            clearBox.AddChild(_sliderArmClearance);

            _lblArmClearanceVal = new Label { CustomMinimumSize = new Vector2(45, 0), Text = "0°" };
            _lblArmClearanceVal.AddThemeFontSizeOverride("font_size", 13);
            clearBox.AddChild(_lblArmClearanceVal);
            calibGrid.AddChild(clearBox);

            // Spine Pitch
            var lblSpine = new Label { Text = "Spine Posture Pitch:" };
            lblSpine.AddThemeFontSizeOverride("font_size", 13);
            calibGrid.AddChild(lblSpine);

            var spineBox = new HBoxContainer();
            spineBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _sliderSpinePitch = new HSlider
            {
                MinValue = -30.0,
                MaxValue = 30.0,
                Step = 1.0,
                Value = 0.0,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                TooltipText = "Adjusts forward/backward torso spine pitch angle (-30° to +30°)."
            };
            _sliderSpinePitch.ValueChanged += (double _) => UpdateSpinePitchLabel();
            spineBox.AddChild(_sliderSpinePitch);

            _lblSpinePitchVal = new Label { CustomMinimumSize = new Vector2(40, 0), Text = "0°" };
            _lblSpinePitchVal.AddThemeFontSizeOverride("font_size", 13);
            spineBox.AddChild(_lblSpinePitchVal);
            calibGrid.AddChild(spineBox);

            // Pelvis Scale
            var lblPelvis = new Label { Text = "Pelvis Height Scale:" };
            lblPelvis.AddThemeFontSizeOverride("font_size", 13);
            calibGrid.AddChild(lblPelvis);

            var pelvisBox = new HBoxContainer();
            pelvisBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _sliderPelvisScale = new HSlider
            {
                MinValue = 0.50,
                MaxValue = 1.50,
                Step = 0.05,
                Value = 1.0,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                TooltipText = "Scales vertical pelvis displacement relative to character leg length."
            };
            _sliderPelvisScale.ValueChanged += (double _) => UpdatePelvisScaleLabel();
            pelvisBox.AddChild(_sliderPelvisScale);

            _lblPelvisScaleVal = new Label { CustomMinimumSize = new Vector2(45, 0), Text = "1.00x" };
            _lblPelvisScaleVal.AddThemeFontSizeOverride("font_size", 13);
            pelvisBox.AddChild(_lblPelvisScaleVal);
            calibGrid.AddChild(pelvisBox);

            // Floor / Ground Offset (Y)
            var lblFloor = new Label { Text = "Floor Height Offset (Y):" };
            lblFloor.AddThemeFontSizeOverride("font_size", 13);
            calibGrid.AddChild(lblFloor);

            var floorBox = new HBoxContainer();
            floorBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _sliderFloorOffset = new HSlider
            {
                MinValue = -0.50,
                MaxValue = 0.50,
                Step = 0.01,
                Value = 0.0,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                TooltipText = "Fine-tune vertical foot floor contact by centimeters (-0.50m to +0.50m)."
            };
            _sliderFloorOffset.ValueChanged += (double _) => UpdateFloorOffsetLabel();
            floorBox.AddChild(_sliderFloorOffset);

            _lblFloorOffsetVal = new Label { CustomMinimumSize = new Vector2(50, 0), Text = "0.00m" };
            _lblFloorOffsetVal.AddThemeFontSizeOverride("font_size", 13);
            floorBox.AddChild(_lblFloorOffsetVal);
            calibGrid.AddChild(floorBox);

            // Empty spacer in grid
            calibGrid.AddChild(new Control());
            calibGrid.AddChild(new Control());

            // C. Grounding & Root Motion Toggles
            var togglesGrid = new GridContainer
            {
                Columns = 3
            };
            togglesGrid.AddThemeConstantOverride("h_separation", 20);
            togglesGrid.AddThemeConstantOverride("v_separation", 8);
            settingsVBox.AddChild(togglesGrid);

            _chkAutoGroundFeet = new CheckBox
            {
                Text = "Auto-Ground Feet to Floor",
                ButtonPressed = true,
                TooltipText = "Enables dynamic height compensation so feet plant firmly on the floor without mid-air floating."
            };
            _chkAutoGroundFeet.AddThemeFontSizeOverride("font_size", 13);
            if (footstepsIcon != null) _chkAutoGroundFeet.Icon = footstepsIcon;
            togglesGrid.AddChild(_chkAutoGroundFeet);

            _chkEnableFootIK = new CheckBox
            {
                Text = "Foot IK Ground Lock",
                ButtonPressed = true,
                TooltipText = "Solves two-bone inverse kinematics on both legs independently to ensure both feet plant firmly on the floor simultaneously without floating."
            };
            _chkEnableFootIK.AddThemeFontSizeOverride("font_size", 13);
            if (footstepsIcon != null) _chkEnableFootIK.Icon = footstepsIcon;
            togglesGrid.AddChild(_chkEnableFootIK);

            _chkLockHorizontalRootMotion = new CheckBox
            {
                Text = "Lock Horizontal Root Motion",
                ButtonPressed = false,
                TooltipText = "Subtracts linear travel drift so locomotion clips play in-place while preserving local pelvic sway and weight shift."
            };
            _chkLockHorizontalRootMotion.AddThemeFontSizeOverride("font_size", 13);
            togglesGrid.AddChild(_chkLockHorizontalRootMotion);

            _chkSoftIK = new CheckBox
            {
                Text = "Soft IK Reach Ease-In",
                ButtonPressed = true,
                TooltipText = "Exponential ease-in curve starting at 92% reach to prevent sudden straight-leg snapping, popping, and hyperextension."
            };
            _chkSoftIK.AddThemeFontSizeOverride("font_size", 13);
            togglesGrid.AddChild(_chkSoftIK);

            _chkSmoothKnees = new CheckBox
            {
                Text = "Smooth Knee Jitter",
                ButtonPressed = true,
                TooltipText = "Gaussian temporal smoothing on baked knee rotations across frames to eliminate single-frame IK micro-jitter."
            };
            _chkSmoothKnees.AddThemeFontSizeOverride("font_size", 13);
            togglesGrid.AddChild(_chkSmoothKnees);

            _chkAnatomicalKneePole = new CheckBox
            {
                Text = "Anatomical Knee Pole",
                ButtonPressed = true,
                TooltipText = "Enforces authentic anatomical forward knee bend plane from rest pose instead of FK cross-product noise on straight legs."
            };
            _chkAnatomicalKneePole.AddThemeFontSizeOverride("font_size", 13);
            togglesGrid.AddChild(_chkAnatomicalKneePole);

            // D. Quick Presets Bar
            var presetPanel = new PanelContainer();
            settingsVBox.AddChild(presetPanel);
            var presetMargin = new MarginContainer();
            presetMargin.AddThemeConstantOverride("margin_left", 12);
            presetMargin.AddThemeConstantOverride("margin_top", 8);
            presetMargin.AddThemeConstantOverride("margin_right", 12);
            presetMargin.AddThemeConstantOverride("margin_bottom", 8);
            presetPanel.AddChild(presetMargin);

            var presetHBox = new HBoxContainer();
            presetHBox.AddThemeConstantOverride("separation", 10);
            presetMargin.AddChild(presetHBox);

            var lblPresets = new Label { Text = "Quick Calibration Presets:" };
            lblPresets.AddThemeFontSizeOverride("font_size", 13);
            presetHBox.AddChild(lblPresets);

            _btnPresetMixamo = new Button
            {
                Text = "Mixamo Preset",
                TooltipText = "Apply recommended settings for Mixamo clips (Strict T-Pose, Smooth 4-Bone Spline, Auto-Grounded)"
            };
            _btnPresetMixamo.Pressed += ApplyMixamoPreset;
            presetHBox.AddChild(_btnPresetMixamo);

            _btnPresetMesh2Motion = new Button
            {
                Text = "Mesh2Motion Preset",
                TooltipText = "Apply recommended settings for Mesh2Motion clips (Strict T-Pose, Smooth 4-Bone Spline, Auto-Grounded)"
            };
            _btnPresetMesh2Motion.Pressed += ApplyMesh2MotionPreset;
            presetHBox.AddChild(_btnPresetMesh2Motion);

            var presetSpacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            presetHBox.AddChild(presetSpacer);

            _btnResetSettings = new Button
            {
                Text = "Reset Settings",
                TooltipText = "Reset calibration sliders to 0 and precision modes to defaults"
            };
            if (resetIcon != null) _btnResetSettings.Icon = resetIcon;
            _btnResetSettings.Pressed += ResetSettingsToDefaults;
            presetHBox.AddChild(_btnResetSettings);

            // ==========================================
            // TAB 2: Interactive Bone Mapping Dictionary
            // ==========================================
            var tabBones = new MarginContainer
            {
                Name = "BoneMappingTab"
            };
            tabBones.AddThemeConstantOverride("margin_left", 14);
            tabBones.AddThemeConstantOverride("margin_top", 12);
            tabBones.AddThemeConstantOverride("margin_right", 14);
            tabBones.AddThemeConstantOverride("margin_bottom", 12);
            _tabContainer.AddChild(tabBones);
            _tabContainer.SetTabTitle(1, "Bone Mapping");
            if (boneIcon != null) _tabContainer.SetTabIcon(1, boneIcon);

            var bonesVBox = new VBoxContainer
            {
                SizeFlagsVertical = SizeFlags.ExpandFill
            };
            bonesVBox.AddThemeConstantOverride("separation", 8);
            tabBones.AddChild(bonesVBox);

            var mapHeaderHBox = new HBoxContainer();
            mapHeaderHBox.AddThemeConstantOverride("separation", 10);
            bonesVBox.AddChild(mapHeaderHBox);

            _lblMappingStats = new Label
            {
                Text = "0 / 0 Mapped",
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            _lblMappingStats.AddThemeColorOverride("font_color", new Color(0.40f, 0.85f, 1.0f, 1.0f));
            _lblMappingStats.AddThemeFontSizeOverride("font_size", 14);
            mapHeaderHBox.AddChild(_lblMappingStats);

            _chkShowAllHeroBones = new CheckBox
            {
                Text = "Show All Hero Skeleton Bones",
                ButtonPressed = true,
                TooltipText = "Displays all remaining hero skeleton bones (including attachfoot, sole, ball_end, cloth, and weapon bones) in the mapping table"
            };
            _chkShowAllHeroBones.AddThemeFontSizeOverride("font_size", 13);
            _chkShowAllHeroBones.Toggled += (bool _) => FilterBoneRows();
            mapHeaderHBox.AddChild(_chkShowAllHeroBones);

            _txtBoneSearch = new LineEdit
            {
                CustomMinimumSize = new Vector2(240, 28),
                PlaceholderText = "Filter bones..."
            };
            _txtBoneSearch.AddThemeFontSizeOverride("font_size", 13);
            _txtBoneSearch.TextChanged += OnBoneSearchTextChanged;
            mapHeaderHBox.AddChild(_txtBoneSearch);

            _btnResetMapping = new Button
            {
                Text = "Reset to Auto-Mapping",
                TooltipText = "Resets all bone mappings to the auto-detected defaults"
            };
            if (resetIcon != null) _btnResetMapping.Icon = resetIcon;
            _btnResetMapping.Pressed += ResetToAutoMapping;
            mapHeaderHBox.AddChild(_btnResetMapping);

            var tablePanel = new PanelContainer
            {
                SizeFlagsVertical = SizeFlags.ExpandFill
            };
            bonesVBox.AddChild(tablePanel);

            _scrollBones = new ScrollContainer
            {
                SizeFlagsVertical = SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };
            tablePanel.AddChild(_scrollBones);

            _vboxBoneRows = new VBoxContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            _vboxBoneRows.AddThemeConstantOverride("separation", 2);
            _scrollBones.AddChild(_vboxBoneRows);

            // 4. Footer Action Bar & Browse Links
            var footerHBox = new HBoxContainer();
            footerHBox.AddThemeConstantOverride("separation", 10);
            mainVBox.AddChild(footerHBox);

            // Web browse buttons
            var globeIcon = GD.Load<Texture2D>("res://assets/at-icons/globe.svg");
            _btnBrowseMixamo = new Button
            {
                Text = "Browse Mixamo (Web)",
                TooltipText = "Open Mixamo animation library in your default web browser"
            };
            if (globeIcon != null) _btnBrowseMixamo.Icon = globeIcon;
            _btnBrowseMixamo.Pressed += () => OS.ShellOpen("https://www.mixamo.com");
            footerHBox.AddChild(_btnBrowseMixamo);

            _btnBrowseMesh2Motion = new Button
            {
                Text = "Browse Mesh2Motion (Web)",
                TooltipText = "Open Mesh2Motion website in your default web browser"
            };
            if (globeIcon != null) _btnBrowseMesh2Motion.Icon = globeIcon;
            _btnBrowseMesh2Motion.Pressed += () => OS.ShellOpen("https://mesh2motion.com");
            footerHBox.AddChild(_btnBrowseMesh2Motion);

            var footerSpacer = new Control
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            footerHBox.AddChild(footerSpacer);

            _btnCancel = new Button
            {
                Text = "Cancel",
                CustomMinimumSize = new Vector2(100, 32)
            };
            if (crossIcon != null) _btnCancel.Icon = crossIcon;
            _btnCancel.Pressed += Close;
            footerHBox.AddChild(_btnCancel);

            _btnRetarget = new Button
            {
                Text = "Retarget & Import Animation",
                ThemeTypeVariation = "ButtonPrimary",
                CustomMinimumSize = new Vector2(230, 32)
            };
            var checkIcon = GD.Load<Texture2D>("res://assets/at-icons/checkmark.svg");
            if (checkIcon != null) _btnRetarget.Icon = checkIcon;
            _btnRetarget.Pressed += OnRetargetPressed;
            footerHBox.AddChild(_btnRetarget);
        }

        private void OnSourceClipSelected(int itemIndex)
        {
            if (_optSourceClip == null || itemIndex < 0) return;
            string clipKey = _optSourceClip.GetItemMetadata(itemIndex).AsString();
            if (string.IsNullOrEmpty(clipKey)) return;

            if (_txtTargetAnimName != null && !_chkOverwriteActive.ButtonPressed)
            {
                string clean = clipKey.Replace(' ', '_').ToLowerInvariant();
                _txtTargetAnimName.Text = $"retarget_{clean}";
            }
        }

        private void OnOverwriteActiveToggled(bool isOverwrite)
        {
            if (_txtTargetAnimName != null)
            {
                _txtTargetAnimName.Editable = !isOverwrite;
                if (isOverwrite && !string.IsNullOrEmpty(_activeAnimName))
                {
                    _txtTargetAnimName.Text = _activeAnimName;
                }
                else if (_optSourceClip != null && _optSourceClip.Selected >= 0)
                {
                    OnSourceClipSelected(_optSourceClip.Selected);
                }
            }
        }

        private void ApplyMixamoPreset()
        {
            if (_optSourcePoseMode != null) _optSourcePoseMode.Select(1); // Strict T-Pose
            if (_optSpineMode != null) _optSpineMode.Select(0);          // Smooth 4-Bone Spline
            if (_sliderArmPitch != null) _sliderArmPitch.Value = 0.0;
            if (_sliderArmClearance != null) _sliderArmClearance.Value = 0.0;
            if (_sliderSpinePitch != null) _sliderSpinePitch.Value = 0.0;
            if (_sliderPelvisScale != null) _sliderPelvisScale.Value = 1.0;
            if (_sliderFloorOffset != null) _sliderFloorOffset.Value = 0.0;
            if (_chkAutoGroundFeet != null) _chkAutoGroundFeet.ButtonPressed = true;
            if (_chkEnableFootIK != null) _chkEnableFootIK.ButtonPressed = true;
            if (_chkLockHorizontalRootMotion != null) _chkLockHorizontalRootMotion.ButtonPressed = false;
            UpdateAllSliderLabels();
        }

        private void ApplyMesh2MotionPreset()
        {
            if (_optSourcePoseMode != null) _optSourcePoseMode.Select(1); // Strict T-Pose
            if (_optSpineMode != null) _optSpineMode.Select(0);          // Smooth 4-Bone Spline
            if (_sliderArmPitch != null) _sliderArmPitch.Value = 0.0;
            if (_sliderArmClearance != null) _sliderArmClearance.Value = 0.0;
            if (_sliderSpinePitch != null) _sliderSpinePitch.Value = 0.0;
            if (_sliderPelvisScale != null) _sliderPelvisScale.Value = 1.0;
            if (_sliderFloorOffset != null) _sliderFloorOffset.Value = 0.0;
            if (_chkAutoGroundFeet != null) _chkAutoGroundFeet.ButtonPressed = true;
            if (_chkEnableFootIK != null) _chkEnableFootIK.ButtonPressed = true;
            if (_chkLockHorizontalRootMotion != null) _chkLockHorizontalRootMotion.ButtonPressed = false;
            UpdateAllSliderLabels();
        }

        private void ResetSettingsToDefaults()
        {
            if (_optSourcePoseMode != null) _optSourcePoseMode.Select(0); // Auto-Detect
            if (_optSpineMode != null) _optSpineMode.Select(0);          // Smooth 4-Bone Spline
            if (_sliderArmPitch != null) _sliderArmPitch.Value = 0.0;
            if (_sliderArmClearance != null) _sliderArmClearance.Value = 0.0;
            if (_sliderSpinePitch != null) _sliderSpinePitch.Value = 0.0;
            if (_sliderPelvisScale != null) _sliderPelvisScale.Value = 1.0;
            if (_sliderFloorOffset != null) _sliderFloorOffset.Value = 0.0;
            if (_chkAutoGroundFeet != null) _chkAutoGroundFeet.ButtonPressed = true;
            if (_chkEnableFootIK != null) _chkEnableFootIK.ButtonPressed = true;
            if (_chkLockHorizontalRootMotion != null) _chkLockHorizontalRootMotion.ButtonPressed = false;
            UpdateAllSliderLabels();
        }

        private void UpdateAllSliderLabels()
        {
            UpdateArmPitchLabel();
            UpdateArmClearanceLabel();
            UpdateSpinePitchLabel();
            UpdatePelvisScaleLabel();
            UpdateFloorOffsetLabel();
        }

        private void UpdateArmPitchLabel()
        {
            if (_sliderArmPitch != null && _lblArmPitchVal != null)
            {
                double v = _sliderArmPitch.Value;
                _lblArmPitchVal.Text = $"{(v >= 0 ? "+" : "")}{v:F0}°";
            }
        }

        private void UpdateArmClearanceLabel()
        {
            if (_sliderArmClearance != null && _lblArmClearanceVal != null)
            {
                double v = _sliderArmClearance.Value;
                _lblArmClearanceVal.Text = $"{(v > 0 ? "+" : "")}{v:F0}°";
            }
        }

        private void UpdateSpinePitchLabel()
        {
            if (_sliderSpinePitch != null && _lblSpinePitchVal != null)
            {
                double v = _sliderSpinePitch.Value;
                _lblSpinePitchVal.Text = $"{(v >= 0 ? "+" : "")}{v:F0}°";
            }
        }

        private void UpdatePelvisScaleLabel()
        {
            if (_sliderPelvisScale != null && _lblPelvisScaleVal != null)
            {
                _lblPelvisScaleVal.Text = $"{_sliderPelvisScale.Value:F2}x";
            }
        }

        private void UpdateFloorOffsetLabel()
        {
            if (_sliderFloorOffset != null && _lblFloorOffsetVal != null)
            {
                double v = _sliderFloorOffset.Value;
                _lblFloorOffsetVal.Text = $"{(v >= 0 ? "+" : "")}{v:F2}m";
            }
        }

        private void BuildBoneMappingRows()
        {
            if (_vboxBoneRows == null || _loadResult?.SourceSkeleton == null || _targetSkeleton == null) return;

            foreach (var row in _boneRows)
            {
                row.RowContainer.QueueFree();
            }
            _boneRows.Clear();

            if (_heroBonesHeaderRow != null && GodotObject.IsInstanceValid(_heroBonesHeaderRow))
            {
                _heroBonesHeaderRow.QueueFree();
                _heroBonesHeaderRow = null;
            }

            // Collect all source bones for dropdown options
            int sourceBoneCount = _loadResult.SourceSkeleton.GetBoneCount();
            var sourceBoneNames = new List<string>(sourceBoneCount);
            for (int s = 0; s < sourceBoneCount; s++)
            {
                sourceBoneNames.Add(_loadResult.SourceSkeleton.GetBoneName(s));
            }

            var handledTargetBones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Build rows for each standard HumanoidBoneMapper definition found on target skeleton
            foreach (var def in HumanoidBoneMapper.Definitions)
            {
                string targetBone = HumanoidBoneMapper.ResolveTargetBone(_targetSkeleton, def);
                if (string.IsNullOrEmpty(targetBone)) continue;

                handledTargetBones.Add(targetBone);

                var rowBox = new HBoxContainer
                {
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    CustomMinimumSize = new Vector2(0, 26)
                };
                rowBox.AddThemeConstantOverride("separation", 8);

                // Region color badge indicator
                Color groupColor = HumanoidBoneMapper.GetGroupColor(def.Group);
                var badge = new Panel
                {
                    CustomMinimumSize = new Vector2(4, 20),
                    SizeFlagsVertical = SizeFlags.ShrinkCenter
                };
                var badgeStyle = new StyleBoxFlat
                {
                    BgColor = groupColor,
                    CornerRadiusBottomLeft = 2,
                    CornerRadiusBottomRight = 2,
                    CornerRadiusTopLeft = 2,
                    CornerRadiusTopRight = 2
                };
                badge.AddThemeStyleboxOverride("panel", badgeStyle);
                rowBox.AddChild(badge);

                // Target Bone Name
                var lblTarget = new Label
                {
                    Text = $"{targetBone} ({def.DisplayLabel})",
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    ClipText = true
                };
                var fontColus = PlaygroundThemeHelper.GetFontColus();
                if (fontColus != null)
                {
                    lblTarget.AddThemeFontOverride("font", fontColus);
                }
                lblTarget.AddThemeFontSizeOverride("font_size", 14);
                rowBox.AddChild(lblTarget);

                // Arrow
                var lblArrow = new Label
                {
                    Text = "←",
                    CustomMinimumSize = new Vector2(16, 0)
                };
                lblArrow.AddThemeColorOverride("font_color", new Color(0.6f, 0.65f, 0.75f, 0.7f));
                rowBox.AddChild(lblArrow);

                // Source Bone Dropdown
                var optSource = new OptionButton
                {
                    CustomMinimumSize = new Vector2(280, 24),
                    SizeFlagsHorizontal = SizeFlags.ShrinkEnd
                };

                string defaultSkipLabel = "(None / Skip)";
                if (targetBone.Contains("_meta_", StringComparison.OrdinalIgnoreCase))
                {
                    defaultSkipLabel = "(Auto-Follow Hand)";
                }
                else if (targetBone.Contains("_end_", StringComparison.OrdinalIgnoreCase))
                {
                    defaultSkipLabel = "(Auto-Align Tip)";
                }

                optSource.AddItem(defaultSkipLabel, 0);
                optSource.SetItemMetadata(0, "");

                int selectIdx = 0;
                string prefilledSource = _initialAutoMapping.TryGetValue(targetBone, out string src) ? src : "";

                // Special option for spine_1: Auto-Spline Resampling
                if (targetBone.Equals("spine_1", StringComparison.OrdinalIgnoreCase))
                {
                    int autoSplineIdx = optSource.ItemCount;
                    optSource.AddItem("[AUTO-SPLINE] Smooth 4-Bone Spline", autoSplineIdx);
                    optSource.SetItemMetadata(autoSplineIdx, "__AUTO_SPLINE__");
                    selectIdx = autoSplineIdx;
                }

                for (int s = 0; s < sourceBoneNames.Count; s++)
                {
                    string sName = sourceBoneNames[s];
                    int itemIdx = optSource.ItemCount;
                    optSource.AddItem(sName, itemIdx);
                    optSource.SetItemMetadata(itemIdx, sName);

                    if (selectIdx == 0 && !string.IsNullOrEmpty(prefilledSource) && string.Equals(sName, prefilledSource, StringComparison.OrdinalIgnoreCase))
                    {
                        selectIdx = itemIdx;
                    }
                }

                optSource.Select(selectIdx);
                optSource.ItemSelected += (long _) => UpdateMappingStats();
                rowBox.AddChild(optSource);

                _vboxBoneRows.AddChild(rowBox);

                _boneRows.Add(new BoneRowData
                {
                    TargetBoneName = targetBone,
                    Definition = def,
                    RowContainer = rowBox,
                    OptionSourceBone = optSource,
                    LabelTarget = lblTarget,
                    RegionBadge = badge,
                    IsHeroSpecific = false
                });
            }

            // 2. Build rows for all remaining hero-specific bones (attachfoot, sole, ball_end, cloth, weapon bones)
            var heroSpecificBones = new List<string>();
            int targetTotal = _targetSkeleton.GetBoneCount();
            for (int b = 0; b < targetTotal; b++)
            {
                string bName = _targetSkeleton.GetBoneName(b);
                if (!handledTargetBones.Contains(bName))
                {
                    heroSpecificBones.Add(bName);
                }
            }

            if (heroSpecificBones.Count > 0)
            {
                var fontColus = PlaygroundThemeHelper.GetFontColus();

                var secHeaderBox = new HBoxContainer
                {
                    CustomMinimumSize = new Vector2(0, 32),
                    SizeFlagsHorizontal = SizeFlags.ExpandFill
                };
                secHeaderBox.AddThemeConstantOverride("separation", 10);

                var secLine1 = new HSeparator { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
                secHeaderBox.AddChild(secLine1);

                var secLbl = new Label
                {
                    Text = $"── Additional / Hero-Specific Bones ({heroSpecificBones.Count}) ──",
                    SizeFlagsVertical = SizeFlags.ShrinkCenter
                };
                secLbl.AddThemeColorOverride("font_color", new Color(0.70f, 0.55f, 0.95f, 1f));
                if (fontColus != null) secLbl.AddThemeFontOverride("font", fontColus);
                secLbl.AddThemeFontSizeOverride("font_size", 13);
                secHeaderBox.AddChild(secLbl);

                var secLine2 = new HSeparator { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
                secHeaderBox.AddChild(secLine2);

                _heroBonesHeaderRow = secHeaderBox;
                _vboxBoneRows.AddChild(secHeaderBox);

                foreach (string hBone in heroSpecificBones)
                {
                    var rowBox = new HBoxContainer
                    {
                        SizeFlagsHorizontal = SizeFlags.ExpandFill,
                        CustomMinimumSize = new Vector2(0, 26)
                    };
                    rowBox.AddThemeConstantOverride("separation", 8);

                    var badge = new Panel
                    {
                        CustomMinimumSize = new Vector2(4, 20),
                        SizeFlagsVertical = SizeFlags.ShrinkCenter
                    };
                    var badgeStyle = new StyleBoxFlat
                    {
                        BgColor = new Color(0.60f, 0.40f, 0.85f),
                        CornerRadiusBottomLeft = 2,
                        CornerRadiusBottomRight = 2,
                        CornerRadiusTopLeft = 2,
                        CornerRadiusTopRight = 2
                    };
                    badge.AddThemeStyleboxOverride("panel", badgeStyle);
                    rowBox.AddChild(badge);

                    var lblTarget = new Label
                    {
                        Text = $"{hBone} (Hero Bone)",
                        SizeFlagsHorizontal = SizeFlags.ExpandFill,
                        ClipText = true
                    };
                    if (fontColus != null) lblTarget.AddThemeFontOverride("font", fontColus);
                    lblTarget.AddThemeFontSizeOverride("font_size", 14);
                    rowBox.AddChild(lblTarget);

                    var lblArrow = new Label
                    {
                        Text = "←",
                        CustomMinimumSize = new Vector2(16, 0)
                    };
                    lblArrow.AddThemeColorOverride("font_color", new Color(0.6f, 0.65f, 0.75f, 0.7f));
                    rowBox.AddChild(lblArrow);

                    var optSource = new OptionButton
                    {
                        CustomMinimumSize = new Vector2(280, 24),
                        SizeFlagsHorizontal = SizeFlags.ShrinkEnd
                    };
                    optSource.AddItem("(None / Skip)", 0);
                    optSource.SetItemMetadata(0, "");

                    int selectIdx = 0;
                    string prefilledSource = _initialAutoMapping.TryGetValue(hBone, out string src) ? src : "";

                    for (int s = 0; s < sourceBoneNames.Count; s++)
                    {
                        string sName = sourceBoneNames[s];
                        int itemIdx = optSource.ItemCount;
                        optSource.AddItem(sName, itemIdx);
                        optSource.SetItemMetadata(itemIdx, sName);

                        if (selectIdx == 0 && !string.IsNullOrEmpty(prefilledSource) && string.Equals(sName, prefilledSource, StringComparison.OrdinalIgnoreCase))
                        {
                            selectIdx = itemIdx;
                        }
                    }

                    optSource.Select(selectIdx);
                    optSource.ItemSelected += (long _) => UpdateMappingStats();
                    rowBox.AddChild(optSource);

                    _vboxBoneRows.AddChild(rowBox);

                    _boneRows.Add(new BoneRowData
                    {
                        TargetBoneName = hBone,
                        Definition = new HumanoidBoneMapper.BoneDefinition(hBone, "Hero Bone", HumanoidBoneMapper.BoneGroup.Secondary, new[] { hBone }, Array.Empty<string>()),
                        RowContainer = rowBox,
                        OptionSourceBone = optSource,
                        LabelTarget = lblTarget,
                        RegionBadge = badge,
                        IsHeroSpecific = true
                    });
                }
            }

            FilterBoneRows();
            UpdateMappingStats();
        }

        private void ResetToAutoMapping()
        {
            if (_loadResult?.SourceSkeleton == null || _targetSkeleton == null) return;
            _initialAutoMapping = HumanoidBoneMapper.AutoMap(_loadResult.SourceSkeleton, _targetSkeleton);

            foreach (var row in _boneRows)
            {
                if (row.TargetBoneName.Equals("spine_1", StringComparison.OrdinalIgnoreCase))
                {
                    // Default spine_1 to Auto-Spline option
                    for (int i = 0; i < row.OptionSourceBone.ItemCount; i++)
                    {
                        if (row.OptionSourceBone.GetItemMetadata(i).AsString() == "__AUTO_SPLINE__")
                        {
                            row.OptionSourceBone.Select(i);
                            break;
                        }
                    }
                    continue;
                }

                string prefilled = _initialAutoMapping.TryGetValue(row.TargetBoneName, out string src) ? src : "";
                int selectIdx = 0;

                for (int i = 0; i < row.OptionSourceBone.ItemCount; i++)
                {
                    string itemMeta = row.OptionSourceBone.GetItemMetadata(i).AsString();
                    if (!string.IsNullOrEmpty(prefilled) && string.Equals(itemMeta, prefilled, StringComparison.OrdinalIgnoreCase))
                    {
                        selectIdx = i;
                        break;
                    }
                }

                row.OptionSourceBone.Select(selectIdx);
            }

            UpdateMappingStats();
        }

        private void UpdateMappingStats()
        {
            int mapped = 0;
            int total = _boneRows.Count;

            foreach (var row in _boneRows)
            {
                if (row.OptionSourceBone.Selected > 0) mapped++;
            }

            int pct = total > 0 ? (int)Math.Round((mapped / (double)total) * 100.0) : 0;
            if (_lblMappingStats != null)
            {
                _lblMappingStats.Text = $"{mapped} / {total} Bones Mapped ({pct}%)";
            }
            if (_tabContainer != null && _tabContainer.GetTabCount() > 1)
            {
                _tabContainer.SetTabTitle(1, $"Bone Mapping ({mapped}/{total})");
            }
        }

        private void OnBoneSearchTextChanged(string text)
        {
            FilterBoneRows();
        }

        private void FilterBoneRows()
        {
            string filter = (_txtBoneSearch?.Text ?? "").Trim().ToLowerInvariant();
            bool showHeroBones = _chkShowAllHeroBones == null || _chkShowAllHeroBones.ButtonPressed;

            if (_heroBonesHeaderRow != null && GodotObject.IsInstanceValid(_heroBonesHeaderRow))
            {
                _heroBonesHeaderRow.Visible = showHeroBones;
            }

            foreach (var row in _boneRows)
            {
                if (row.IsHeroSpecific && !showHeroBones)
                {
                    row.RowContainer.Visible = false;
                    continue;
                }

                if (string.IsNullOrEmpty(filter))
                {
                    row.RowContainer.Visible = true;
                }
                else
                {
                    bool matchTarget = row.TargetBoneName.ToLowerInvariant().Contains(filter) ||
                                       (row.Definition != null && row.Definition.DisplayLabel.ToLowerInvariant().Contains(filter));
                    int selIdx = row.OptionSourceBone != null ? row.OptionSourceBone.Selected : -1;
                    string selectedSource = selIdx >= 0 ? row.OptionSourceBone.GetItemMetadata(selIdx).AsString() : "";
                    bool matchSource = selectedSource.ToLowerInvariant().Contains(filter);

                    row.RowContainer.Visible = matchTarget || matchSource;
                }
            }
        }

        private void OnRetargetPressed()
        {
            if (_loadResult?.SourceSkeleton == null || _targetSkeleton == null) return;

            if (_optSourceClip == null || _optSourceClip.Selected < 0)
            {
                GD.PrintErr("[AnimationRetargetModal] No source clip selected.");
                return;
            }

            string clipKey = _optSourceClip.GetItemMetadata(_optSourceClip.Selected).AsString();
            if (!_loadResult.Animations.TryGetValue(clipKey, out var sourceAnim) || sourceAnim == null)
            {
                GD.PrintErr($"[AnimationRetargetModal] Clip '{clipKey}' not found in loaded animations.");
                return;
            }

            bool isOverwrite = _chkOverwriteActive != null && _chkOverwriteActive.ButtonPressed;
            string targetName = _txtTargetAnimName != null && !string.IsNullOrWhiteSpace(_txtTargetAnimName.Text)
                ? _txtTargetAnimName.Text.Trim()
                : (isOverwrite && !string.IsNullOrEmpty(_activeAnimName) ? _activeAnimName : $"retarget_{clipKey}");

            // Gather active bone mappings from table
            var currentMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _boneRows)
            {
                int selIdx = row.OptionSourceBone != null ? row.OptionSourceBone.Selected : -1;
                string sourceBone = "";
                if (selIdx > 0 && selIdx < row.OptionSourceBone.ItemCount)
                {
                    Variant meta = row.OptionSourceBone.GetItemMetadata(selIdx);
                    sourceBone = meta.VariantType == Variant.Type.String ? meta.AsString() : "";
                    if (sourceBone == "__AUTO_SPLINE__")
                    {
                        // Use default mapped bone for spline resampling
                        sourceBone = _initialAutoMapping.TryGetValue(row.TargetBoneName, out string dSrc) ? dSrc : "";
                    }
                    else if (string.IsNullOrEmpty(sourceBone))
                    {
                        sourceBone = row.OptionSourceBone.GetItemText(selIdx);
                    }
                }
                currentMapping[row.TargetBoneName] = sourceBone;
            }

            // Gather calibration options
            string resolvedSkelPath = AnimationRetargeter.ResolveSkeletonTrackPrefix(_animPlayer, _targetSkeleton);
            var options = new AnimationRetargeter.RetargetOptions
            {
                PoseMode = _optSourcePoseMode != null
                    ? (AnimationRetargeter.SourcePoseMode)_optSourcePoseMode.Selected
                    : AnimationRetargeter.SourcePoseMode.AutoDetect,
                SpineMode = _optSpineMode != null
                    ? (AnimationRetargeter.SpineRetargetMode)_optSpineMode.Selected
                    : AnimationRetargeter.SpineRetargetMode.SmoothSpline4Bone,
                ArmPitchOffsetDeg = _sliderArmPitch != null ? (float)_sliderArmPitch.Value : 0f,
                ArmClearanceOffsetDeg = _sliderArmClearance != null ? (float)_sliderArmClearance.Value : 0f,
                SpinePitchOffsetDeg = _sliderSpinePitch != null ? (float)_sliderSpinePitch.Value : 0f,
                PelvisHeightScale = _sliderPelvisScale != null ? (float)_sliderPelvisScale.Value : 1.0f,
                FloorOffsetMeters = _sliderFloorOffset != null ? (float)_sliderFloorOffset.Value : 0.0f,
                LockHorizontalRootMotion = _chkLockHorizontalRootMotion != null && _chkLockHorizontalRootMotion.ButtonPressed,
                AutoGroundFeet = _chkAutoGroundFeet != null && _chkAutoGroundFeet.ButtonPressed,
                EnableFootIK = _chkEnableFootIK != null && _chkEnableFootIK.ButtonPressed,
                SoftIK = _chkSoftIK == null || _chkSoftIK.ButtonPressed,
                SmoothKnees = _chkSmoothKnees == null || _chkSmoothKnees.ButtonPressed,
                AnatomicalKneePole = _chkAnatomicalKneePole == null || _chkAnatomicalKneePole.ButtonPressed,
                TargetFps = 30f,
                TargetAnimName = targetName,
                SkeletonNodePath = resolvedSkelPath
            };

            // Execute retargeting
            var retargetedAnim = AnimationRetargeter.Retarget(
                _loadResult.SourceSkeleton,
                sourceAnim,
                _targetSkeleton,
                currentMapping,
                options
            );

            if (retargetedAnim != null)
            {
                EmitSignal(SignalName.RetargetCompleted, retargetedAnim, targetName, isOverwrite);
                Close();
            }
            else
            {
                GD.PrintErr("[AnimationRetargetModal] Retargeting failed. See console logs.");
            }
        }
    }
}
