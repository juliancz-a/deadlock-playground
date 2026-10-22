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
    /// onto Deadlock Source 2 hero skeletons with live calibration controls, bone mapping table,
    /// and VPK replacement mode.
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

        // Clip & Mode Selection
        private Label _lblFileInfo;
        private OptionButton _optSourceClip;
        private LineEdit _txtTargetAnimName;
        private CheckBox _chkOverwriteActive;
        private Label _lblActiveAnimInfo;

        // Calibration Sliders
        private HSlider _sliderArmPitch;
        private Label _lblArmPitchVal;
        private HSlider _sliderArmClearance;
        private Label _lblArmClearanceVal;
        private HSlider _sliderSpinePitch;
        private Label _lblSpinePitchVal;
        private HSlider _sliderPelvisScale;
        private Label _lblPelvisScaleVal;
        private CheckBox _chkLockHorizontalRootMotion;

        // Bone Mapping
        private LineEdit _txtBoneSearch;
        private Label _lblMappingStats;
        private ScrollContainer _scrollBones;
        private VBoxContainer _vboxBoneRows;

        // Action Buttons
        private Button _btnBrowseMixamo;
        private Button _btnBrowseMesh2Motion;
        private Button _btnResetMapping;
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

            // Rest pose alignment is 100% dynamic and computed from bone geometry. Sliders default to 0° (artistic tweaks only).
            if (_sliderArmPitch != null)
            {
                _sliderArmPitch.Value = 0.0;
                UpdateArmPitchLabel();
            }

            if (_sliderArmClearance != null)
            {
                _sliderArmClearance.Value = 0.0;
                UpdateArmClearanceLabel();
            }

            if (_sliderSpinePitch != null)
            {
                _sliderSpinePitch.Value = 0.0;
                UpdateSpinePitchLabel();
            }

            if (_sliderPelvisScale != null)
            {
                _sliderPelvisScale.Value = 1.0;
                UpdatePelvisScaleLabel();
            }

            if (_chkLockHorizontalRootMotion != null)
            {
                _chkLockHorizontalRootMotion.ButtonPressed = true;
            }

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
            var dimmerStyle = new StyleBoxFlat { BgColor = new Color(0.04f, 0.05f, 0.08f, 0.75f) };
            _backdrop.AddThemeStyleboxOverride("panel", dimmerStyle);
            AddChild(_backdrop);

            // Centered Main Card Panel
            _cardContainer = new PanelContainer
            {
                Name = "CardContainer",
                CustomMinimumSize = new Vector2(1040, 720),
                AnchorsPreset = (int)LayoutPreset.Center,
                AnchorLeft = 0.5f,
                AnchorTop = 0.5f,
                AnchorRight = 0.5f,
                AnchorBottom = 0.5f,
                GrowHorizontal = GrowDirection.Both,
                GrowVertical = GrowDirection.Both,
                OffsetLeft = -520f,
                OffsetTop = -360f,
                OffsetRight = 520f,
                OffsetBottom = 360f
            };
            AddChild(_cardContainer);

            var cardMargin = new MarginContainer
            {
                Name = "CardMargin"
            };
            cardMargin.AddThemeConstantOverride("margin_left", 18);
            cardMargin.AddThemeConstantOverride("margin_top", 16);
            cardMargin.AddThemeConstantOverride("margin_right", 18);
            cardMargin.AddThemeConstantOverride("margin_bottom", 16);
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
            _lblTitle.AddThemeFontSizeOverride("font_size", 20);
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
                Text = "✕",
                CustomMinimumSize = new Vector2(30, 30)
            };
            _btnClose.Pressed += Close;
            headerHBox.AddChild(_btnClose);

            mainVBox.AddChild(new HSeparator());

            // 2. Source Clip & Target Settings Panel
            var clipPanel = new PanelContainer();
            mainVBox.AddChild(clipPanel);

            var clipMargin = new MarginContainer();
            clipMargin.AddThemeConstantOverride("margin_left", 12);
            clipMargin.AddThemeConstantOverride("margin_top", 10);
            clipMargin.AddThemeConstantOverride("margin_right", 12);
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
            _lblFileInfo.AddThemeFontSizeOverride("font_size", 14);
            clipVBox.AddChild(_lblFileInfo);

            var clipHBox = new HBoxContainer();
            clipHBox.AddThemeConstantOverride("separation", 12);
            clipVBox.AddChild(clipHBox);

            var lblClip = new Label { Text = "Source Clip:" };
            lblClip.AddThemeFontSizeOverride("font_size", 14);
            clipHBox.AddChild(lblClip);

            _optSourceClip = new OptionButton
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            _optSourceClip.AddThemeFontSizeOverride("font_size", 13);
            _optSourceClip.ItemSelected += (long id) => OnSourceClipSelected((int)id);
            clipHBox.AddChild(_optSourceClip);

            var lblTarget = new Label { Text = "Target Clip Name:" };
            lblTarget.AddThemeFontSizeOverride("font_size", 14);
            clipHBox.AddChild(lblTarget);

            _txtTargetAnimName = new LineEdit
            {
                CustomMinimumSize = new Vector2(200, 28),
                PlaceholderText = "retarget_animation"
            };
            _txtTargetAnimName.AddThemeFontSizeOverride("font_size", 14);
            clipHBox.AddChild(_txtTargetAnimName);

            // VPK Overwrite Mode row
            var overwriteHBox = new HBoxContainer();
            overwriteHBox.AddThemeConstantOverride("separation", 10);
            clipVBox.AddChild(overwriteHBox);

            _chkOverwriteActive = new CheckBox
            {
                Text = "Apply to Currently Selected Hero Animation (For VPK Export)",
                TooltipText = "Directly replaces the currently selected animation clip in Animation Studio, staging it for instant .vpk export via Source2ClipPatcher!"
            };
            _chkOverwriteActive.AddThemeFontSizeOverride("font_size", 14);
            _chkOverwriteActive.Toggled += OnOverwriteActiveToggled;
            overwriteHBox.AddChild(_chkOverwriteActive);

            _lblActiveAnimInfo = new Label();
            _lblActiveAnimInfo.AddThemeColorOverride("font_color", new Color(0.40f, 0.85f, 1.0f, 0.9f));
            _lblActiveAnimInfo.AddThemeFontSizeOverride("font_size", 13);
            overwriteHBox.AddChild(_lblActiveAnimInfo);

            // 3. Live Calibration Sliders Panel
            var calibPanel = new PanelContainer();
            mainVBox.AddChild(calibPanel);

            var calibMargin = new MarginContainer();
            calibMargin.AddThemeConstantOverride("margin_left", 12);
            calibMargin.AddThemeConstantOverride("margin_top", 10);
            calibMargin.AddThemeConstantOverride("margin_right", 12);
            calibMargin.AddThemeConstantOverride("margin_bottom", 10);
            calibPanel.AddChild(calibMargin);

            var calibGrid = new GridContainer
            {
                Columns = 4
            };
            calibGrid.AddThemeConstantOverride("h_separation", 16);
            calibGrid.AddThemeConstantOverride("v_separation", 8);
            calibMargin.AddChild(calibGrid);

            // Arm Pitch (T-Pose vs A-Pose)
            var lblArm = new Label { Text = "Arm T-Pose / A-Pose Pitch:" };
            lblArm.AddThemeFontSizeOverride("font_size", 14);
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
                TooltipText = "Manual arm pitch artistic tweak (-60° to +60°). Defaults to 0°: rest pose difference between source and target is automatically computed from bone geometry."
            };
            _sliderArmPitch.ValueChanged += (double _) => UpdateArmPitchLabel();
            armBox.AddChild(_sliderArmPitch);

            _lblArmPitchVal = new Label { CustomMinimumSize = new Vector2(45, 0), Text = "0°" };
            _lblArmPitchVal.AddThemeFontSizeOverride("font_size", 14);
            armBox.AddChild(_lblArmPitchVal);
            calibGrid.AddChild(armBox);

            // Arm Clearance / Width (Outward spread for bulky jackets/armor)
            var lblClear = new Label { Text = "Arm Clearance / Width:" };
            lblClear.AddThemeFontSizeOverride("font_size", 14);
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
                TooltipText = "Adjusts outward arm clearance angle (-20° to +20°). Expand outward to prevent arms sinking into bulky jackets (e.g. Holliday) or chest armor."
            };
            _sliderArmClearance.ValueChanged += (double _) => UpdateArmClearanceLabel();
            clearBox.AddChild(_sliderArmClearance);

            _lblArmClearanceVal = new Label { CustomMinimumSize = new Vector2(45, 0), Text = "0°" };
            _lblArmClearanceVal.AddThemeFontSizeOverride("font_size", 14);
            clearBox.AddChild(_lblArmClearanceVal);
            calibGrid.AddChild(clearBox);

            // Spine Pitch
            var lblSpine = new Label { Text = "Spine Posture Pitch:" };
            lblSpine.AddThemeFontSizeOverride("font_size", 14);
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
            _lblSpinePitchVal.AddThemeFontSizeOverride("font_size", 14);
            spineBox.AddChild(_lblSpinePitchVal);
            calibGrid.AddChild(spineBox);

            // Pelvis Scale
            var lblPelvis = new Label { Text = "Pelvis Height Scale:" };
            lblPelvis.AddThemeFontSizeOverride("font_size", 14);
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
                TooltipText = "Scales vertical pelvis bounce displacement relative to character leg length."
            };
            _sliderPelvisScale.ValueChanged += (double _) => UpdatePelvisScaleLabel();
            pelvisBox.AddChild(_sliderPelvisScale);

            _lblPelvisScaleVal = new Label { CustomMinimumSize = new Vector2(45, 0), Text = "1.00x" };
            _lblPelvisScaleVal.AddThemeFontSizeOverride("font_size", 14);
            pelvisBox.AddChild(_lblPelvisScaleVal);
            calibGrid.AddChild(pelvisBox);

            // Lock Horizontal Root Motion
            calibGrid.AddChild(new Control()); // Spacer
            _chkLockHorizontalRootMotion = new CheckBox
            {
                Text = "Lock Horizontal Root Motion (In-Place)",
                ButtonPressed = true,
                TooltipText = "Zeros out X and Z hip displacement deltas so animations play in-place without character moving away."
            };
            _chkLockHorizontalRootMotion.AddThemeFontSizeOverride("font_size", 14);
            calibGrid.AddChild(_chkLockHorizontalRootMotion);

            // 4. Interactive Bone Mapping Table
            var mapHeaderHBox = new HBoxContainer();
            mainVBox.AddChild(mapHeaderHBox);

            var lblBoneTable = new Label
            {
                Text = "Bone Mapping Dictionary (Deadlock ← Source Rig):",
                ThemeTypeVariation = "HeaderSmall"
            };
            lblBoneTable.AddThemeFontSizeOverride("font_size", 16);
            mapHeaderHBox.AddChild(lblBoneTable);

            _lblMappingStats = new Label
            {
                Text = "0 / 0 Mapped",
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            _lblMappingStats.AddThemeColorOverride("font_color", new Color(0.40f, 0.85f, 1.0f, 1.0f));
            _lblMappingStats.AddThemeFontSizeOverride("font_size", 14);
            mapHeaderHBox.AddChild(_lblMappingStats);

            _txtBoneSearch = new LineEdit
            {
                CustomMinimumSize = new Vector2(200, 26),
                PlaceholderText = "Filter bones..."
            };
            _txtBoneSearch.AddThemeFontSizeOverride("font_size", 14);
            _txtBoneSearch.TextChanged += OnBoneSearchTextChanged;
            mapHeaderHBox.AddChild(_txtBoneSearch);

            var tablePanel = new PanelContainer
            {
                SizeFlagsVertical = SizeFlags.ExpandFill
            };
            mainVBox.AddChild(tablePanel);

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

            // 5. Action Bar & Browse Links
            var footerHBox = new HBoxContainer();
            footerHBox.AddThemeConstantOverride("separation", 8);
            mainVBox.AddChild(footerHBox);

            // Web browse buttons
            _btnBrowseMixamo = new Button
            {
                Text = "Browse Mixamo (Web)",
                TooltipText = "Open Mixamo animation library in your default web browser"
            };
            var globeIcon = GD.Load<Texture2D>("res://assets/at-icons/globe.svg");
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

            _btnResetMapping = new Button
            {
                Text = "Reset Mapping",
                TooltipText = "Resets all bone mappings to the auto-detected defaults"
            };
            _btnResetMapping.Pressed += ResetToAutoMapping;
            footerHBox.AddChild(_btnResetMapping);

            _btnCancel = new Button
            {
                Text = "Cancel"
            };
            _btnCancel.Pressed += Close;
            footerHBox.AddChild(_btnCancel);

            _btnRetarget = new Button
            {
                Text = "Retarget & Import Animation",
                ThemeTypeVariation = "ButtonPrimary"
            };
            var importIcon = GD.Load<Texture2D>("res://assets/at-icons/file_arrow_down.svg");
            if (importIcon != null) _btnRetarget.Icon = importIcon;
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

        private void BuildBoneMappingRows()
        {
            if (_vboxBoneRows == null || _loadResult?.SourceSkeleton == null || _targetSkeleton == null) return;

            foreach (var row in _boneRows)
            {
                row.RowContainer.QueueFree();
            }
            _boneRows.Clear();

            // Collect all source bones for dropdown options
            int sourceBoneCount = _loadResult.SourceSkeleton.GetBoneCount();
            var sourceBoneNames = new List<string>(sourceBoneCount);
            for (int s = 0; s < sourceBoneCount; s++)
            {
                sourceBoneNames.Add(_loadResult.SourceSkeleton.GetBoneName(s));
            }

            // Build rows for each HumanoidBoneMapper definition found on target skeleton
            foreach (var def in HumanoidBoneMapper.Definitions)
            {
                string targetBone = HumanoidBoneMapper.ResolveTargetBone(_targetSkeleton, def);
                if (string.IsNullOrEmpty(targetBone)) continue;

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
                    CustomMinimumSize = new Vector2(260, 24),
                    SizeFlagsHorizontal = SizeFlags.ShrinkEnd
                };
                optSource.AddItem("(None / Skip)", 0);
                optSource.SetItemMetadata(0, "");

                int selectIdx = 0;
                string prefilledSource = _initialAutoMapping.TryGetValue(targetBone, out string src) ? src : "";

                for (int s = 0; s < sourceBoneNames.Count; s++)
                {
                    string sName = sourceBoneNames[s];
                    int itemIdx = s + 1;
                    optSource.AddItem(sName, itemIdx);
                    optSource.SetItemMetadata(itemIdx, sName);

                    if (!string.IsNullOrEmpty(prefilledSource) && string.Equals(sName, prefilledSource, StringComparison.OrdinalIgnoreCase))
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
                    RegionBadge = badge
                });
            }

            UpdateMappingStats();
        }

        private void ResetToAutoMapping()
        {
            if (_loadResult?.SourceSkeleton == null || _targetSkeleton == null) return;
            _initialAutoMapping = HumanoidBoneMapper.AutoMap(_loadResult.SourceSkeleton, _targetSkeleton);

            foreach (var row in _boneRows)
            {
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
        }

        private void OnBoneSearchTextChanged(string text)
        {
            string filter = (text ?? "").Trim().ToLowerInvariant();

            foreach (var row in _boneRows)
            {
                if (string.IsNullOrEmpty(filter))
                {
                    row.RowContainer.Visible = true;
                }
                else
                {
                    bool matchTarget = row.TargetBoneName.ToLowerInvariant().Contains(filter) ||
                                       row.Definition.DisplayLabel.ToLowerInvariant().Contains(filter);
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
                if (row.OptionSourceBone.Selected > 0)
                {
                    string sourceBone = row.OptionSourceBone.GetItemMetadata(row.OptionSourceBone.Selected).AsString();
                    if (!string.IsNullOrEmpty(sourceBone))
                    {
                        currentMapping[row.TargetBoneName] = sourceBone;
                    }
                }
            }

            // Gather calibration options
            string resolvedSkelPath = AnimationRetargeter.ResolveSkeletonTrackPrefix(_animPlayer, _targetSkeleton);
            var options = new AnimationRetargeter.RetargetOptions
            {
                ArmPitchOffsetDeg = _sliderArmPitch != null ? (float)_sliderArmPitch.Value : 0f,
                ArmClearanceOffsetDeg = _sliderArmClearance != null ? (float)_sliderArmClearance.Value : 0f,
                SpinePitchOffsetDeg = _sliderSpinePitch != null ? (float)_sliderSpinePitch.Value : 0f,
                PelvisHeightScale = _sliderPelvisScale != null ? (float)_sliderPelvisScale.Value : 1.0f,
                LockHorizontalRootMotion = _chkLockHorizontalRootMotion == null || _chkLockHorizontalRootMotion.ButtonPressed,
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
