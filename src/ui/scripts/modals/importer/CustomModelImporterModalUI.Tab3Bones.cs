using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using DeadlockPlayground.Importer;
using DeadlockPlayground.Theming;

namespace DeadlockPlayground.UI
{
    public partial class CustomModelImporterModalUI
    {
        // Phase 2 & 5 Tab 3: Decluttered Segmented Sub-Views (Remap | Physics & Chains | Disable & Prune)
        private Label _lblRemapSyncBadge;
        private Label _lblRemapDiffSummary;
        private CheckBox _chkKeepUnweightedBones;
        private CheckBox _chkPreserveClothingChains;
        private CheckBox _chkPhysicsMasterTab3;
        private Button _btnFilterUnmappedOnly;
        private Button _btnFilterWeightedOnly;
        private Button _btnFilterShowControls;

        // Sub-view navigation (0 = Remap, 1 = Weights, 2 = Physics, 3 = Colliders, 4 = Prune)
        private Button _btnSubTabRemap;
        private Button _btnSubTabWeights;
        private Button _btnSubTabPhysics;
        private Button _btnSubTabColliders;
        private Button _btnSubTabDisable;
        private Control _boxSubViewRemap;
        private Control _boxSubViewWeights;
        private Control _boxSubViewPhysics;
        private Control _boxSubViewColliders;
        private Control _boxSubViewDisable;
        private bool _wasInCollidersSubView;

        // Visual Preset Picker & Macro Controls (Physics Chain Inspector)
        private Button _btnSelectPresetCard;
        private TextureRect _texSelectedPresetIcon;
        private Label _lblSelectedPresetName;
        private Label _lblSelectedPresetTag;
        private PopupPanel _presetPickerPopup;
        private GridContainer _presetPickerGrid;
        private List<BoneChainDef> _presetPickerTargets;
        private HSlider _sliderMacroLooseness;
        private Label _lblValMacroLooseness;
        private HSlider _sliderMacroGravity;
        private Label _lblValMacroGravity;
        private HSlider _sliderMacroDamping;
        private Label _lblValMacroDamping;
        private HSlider _sliderMacroColMargin;
        private Label _lblValMacroColMargin;
        private HSlider _sliderSelTaperPower;
        private Label _lblValTaperPower;

        // Compact Master-Detail Physics Chains Controls
        private VBoxContainer _boxClothingChainsList;
        private Label _lblClothingChainSummary;
        private LineEdit _txtChainSearch;
        private OptionButton _optChainCategoryFilter;
        private BoneChainDef _selectedChain;
        private PanelContainer _boxChainInspector;
        private Label _lblSelectedChainTitle;
        private OptionButton _optSelChainPolicy;
        private OptionButton _optSelChainAnchor;
        private OptionButton _optSelChainSim;
        private readonly Button[] _chainSimSegButtons = new Button[3];
        private Label _lblSelChainSimHint;
        private Control _boxTab3WeightPaintHost;
        private HFlowContainer _boxTab3WeightBoneChips;
        private Button _btnChainWeightPaintToggle;
        private HSlider _sliderSelStiffRoot;
        private HSlider _sliderSelStiffTip;
        private HSlider _sliderSelDamping;
        private HSlider _sliderSelGravity;
        private HSlider _sliderSelColRadius;
        private HSlider _sliderSelStrayRadius;
        private CheckBox _chkSelStrayUnlimited;
        private CheckBox _chkSelPlanarizeBack;
        private CheckBox _chkSelOpenSheet;
        private CheckBox _chkSelAnchorFreeRotate;
        private HSlider _sliderSelFriction;
        private Label _lblValFriction;
        private HSlider _sliderSelTwistRelax;
        private Label _lblValTwistRelax;
        private HSlider _sliderSelAngleLimit;
        private Label _lblValAngleLimit;
        private VBoxContainer _boxClothExtrudeSection;
        private HSlider _sliderSelExtrudeRadius;
        private Label _lblValExtrudeRadius;
        private OptionButton _optSelExtrudeAxis;
        private HSlider _sliderSelExtrudeTwist;
        private Label _lblValExtrudeTwist;
        private Label _lblValStiffRoot;
        private Label _lblValStiffTip;
        private Label _lblValDamping;
        private Label _lblValGravity;
        private Label _lblValColRadius;
        private Label _lblValStrayRadius;
        private bool _isUpdatingChainInspector;
        private readonly HashSet<string> _collapsedPhysicsGroups = new(StringComparer.OrdinalIgnoreCase);

        // Per-Child Bone Override Editor (inside Chain Inspector)
        private VBoxContainer _boxChainJointList;
        private PanelContainer _boxJointOverrideEditor;
        private Label _lblSelectedJointTitle;
        private CheckBox _chkJointSimulate;
        private HSlider _sliderJointGoal;
        private HSlider _sliderJointDamp;
        private HSlider _sliderJointColRad;
        private CheckBox _chkJointStrayUnlimited;
        private HSlider _sliderJointStray;
        private Label _lblValJointGoal;
        private Label _lblValJointDamp;
        private Label _lblValJointColRad;
        private Label _lblValJointStray;
        private int _selectedChainJointIndex = -1;
        private bool _isUpdatingJointEditor;

        // Physics Stress Test (Look Up / Crouch / Walk Backwards)
        private float _stressHeadPitchDeg = 0f;
        private float _stressCrouchLeanDeg = 0f;
        private float _stressCurHeadPitchDeg = 0f;
        private float _stressCurCrouchLeanDeg = 0f;
        private bool _stressWalkBackShake = false;
        private float _stressShakePhase = 0f;
        private bool _stressPoseWasActive = false;
        private Vector3 _stressSavedHeroPos = Vector3.Zero;
        private bool _stressHasSavedHeroPos = false;
        private HSlider _sliderStressHeadPitch;
        private Label _lblValStressHeadPitch;
        private HSlider _sliderStressCrouchLean;
        private Label _lblValStressCrouchLean;
        private Button _btnStressWalkShake;

        // Mesh-Fitted Body Colliders (anti-clipping capsules)
        private readonly List<BodyCollisionCapsuleDef> _bodyCollisionCapsules = new();
        private BodyCollisionCapsuleDef _selectedCollider;
        private VBoxContainer _boxBodyCollidersList;
        private Label _lblBodyCollidersSummary;
        private Button _btnShowCollidersInCard;
        private PanelContainer _boxColliderInspector;
        private Label _lblSelectedColliderTitle;
        private Label _lblSelectedColliderBoneHint;
        private HSlider _sliderColRadiusScale;
        private HSlider _sliderColOffsetX;
        private HSlider _sliderColOffsetY;
        private HSlider _sliderColOffsetZ;
        private HSlider _sliderColRadius0;
        private HSlider _sliderColRadius1;
        private Label _lblValColRadiusScale;
        private Label _lblValColOffsetX;
        private Label _lblValColOffsetY;
        private Label _lblValColOffsetZ;
        private Label _lblValColRadius0;
        private Label _lblValColRadius1;
        private VBoxContainer _boxColCapsuleControls;
        private VBoxContainer _boxColBoxControls;
        private HBoxContainer _rowColParentBone;
        private OptionButton _optColParentBone;
        private HSlider _sliderColBoxW;
        private HSlider _sliderColBoxH;
        private HSlider _sliderColBoxD;
        private Label _lblValColBoxW;
        private Label _lblValColBoxH;
        private Label _lblValColBoxD;
        private HSlider _sliderColRotX;
        private HSlider _sliderColRotY;
        private HSlider _sliderColRotZ;
        private Label _lblValColRotX;
        private Label _lblValColRotY;
        private Label _lblValColRotZ;
        private CheckBox _chkColPlanarizeBack;
        private Button _btnDeleteCustomCol;
        private bool _isUpdatingColliderInspector;

        // Collapsible Add-Clothing-Chain Tool controls
        private Button _btnToggleAddSynthetic;
        private VBoxContainer _boxAddSyntheticBody;
        private LineEdit _txtNewChainName;
        private OptionButton _optNewChainMesh;
        private OptionButton _optNewChainAnchor;
        private SpinBox _spinNewChainSegs;
        private SpinBox _spinNewChainLen;
        private SpinBox _spinNewChainRadius;
        private HSlider _sliderNewChainStiffRoot;
        private HSlider _sliderNewChainStiffTip;
        private HSlider _sliderNewChainDamping;

        // Disable / Prune Skeleton Picker controls
        private LineEdit _txtDisableBoneSearch;
        private OptionButton _optDisableBoneFilter;
        private VBoxContainer _boxSourceBoneDisableList;
        private VBoxContainer _boxDisabledBonesList;
        private Label _lblDisabledBonesEmpty;

        private readonly HashSet<string> _disabledSourceBones = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<BoneChainDef> _boneChains = new();
        private RigAnalysis _lastRigAnalysis;
        private HashSet<int> _lastWeightedSrcBones = new();

        private Control BuildTab3Bones()
        {
            var root = new VBoxContainer
            {
                Name = "3. Bones",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill
            };
            root.AddThemeConstantOverride("separation", 0);

            // Sticky Fixed Secondary Top-Bar (always visible while scrolling sub-views)
            var stickyMargin = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            stickyMargin.AddThemeConstantOverride("margin_left", 10);
            stickyMargin.AddThemeConstantOverride("margin_top", 8);
            stickyMargin.AddThemeConstantOverride("margin_right", 10);
            stickyMargin.AddThemeConstantOverride("margin_bottom", 8);
            root.AddChild(stickyMargin);

            // Flat 5-View Segmented Switcher (Remap | Weights | Physics | Colliders | Prune)
            var segRow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            segRow.AddThemeConstantOverride("h_separation", 6);
            segRow.AddThemeConstantOverride("v_separation", 6);

            Button MakeSubTabBtn(string text, string iconPath, string tip, int viewIdx, bool pressed = false)
            {
                var b = new Button
                {
                    Text = text,
                    ThemeTypeVariation = "ToolbarButton",
                    ToggleMode = true,
                    ButtonPressed = pressed,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    CustomMinimumSize = new Vector2(76, 38),
                    TooltipText = tip
                };
                ImporterStyle.ApplyFontSize(b, ImporterStyle.FontBody);
                SetButtonIcon(b, iconPath, 17);
                b.Pressed += () => SetBonesSubView(viewIdx);
                segRow.AddChild(b);
                return b;
            }

            _btnSubTabRemap = MakeSubTabBtn("Remap", "res://assets/at-icons/bone.svg", "Map imported skeleton bones to Deadlock core skeleton joints.", 0, pressed: true);
            _btnSubTabWeights = MakeSubTabBtn("Weights", "res://assets/at-icons/brush.svg", "Paint and inspect 3D vertex skin weights with heatmap visualization.", 1);
            _btnSubTabPhysics = MakeSubTabBtn("Physics (0)", "res://assets/at-icons/signal_wave.svg", "Choose Valve-backed cloth/hair presets and tune secondary bone chains.", 2);
            _btnSubTabColliders = MakeSubTabBtn("Colliders", "res://assets/at-icons/circle.svg", "Adjust mesh-fitted body collision capsules and custom prop boxes.", 3);
            _btnSubTabDisable = MakeSubTabBtn("Prune (0)", "res://assets/at-icons/scissors.svg", "Search and disable specific source bones or prune unweighted helpers.", 4);
            stickyMargin.AddChild(segRow);
            root.AddChild(new HSeparator());

            var scroll = new ScrollContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };
            root.AddChild(scroll);

            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 10);
            margin.AddThemeConstantOverride("margin_top", 10);
            margin.AddThemeConstantOverride("margin_right", 10);
            margin.AddThemeConstantOverride("margin_bottom", 12);
            margin.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            scroll.AddChild(margin);

            var vBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            vBox.AddThemeConstantOverride("separation", 10);
            margin.AddChild(vBox);

            // =========================================================
            // SUB-VIEW 0: HUMANOID BONE REMAP
            // =========================================================
            var remapSubBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            remapSubBox.AddThemeConstantOverride("separation", 8);
            _boxSubViewRemap = remapSubBox;
            vBox.AddChild(remapSubBox);

            var (remapCard, remapBody) = CreateInspectorCard("Bone Remapping", "Manual remaps override auto-detection across Pre-Rigged, Landmark, and KNN modes.");
            remapSubBox.AddChild(remapCard);

            var statusRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            statusRow.AddThemeConstantOverride("separation", 8);
            _lblMappingSummary = new Label
            {
                Text = "Mapped: 0 / 0 slots",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            _secondaryLabels.Add(_lblMappingSummary);
            statusRow.AddChild(_lblMappingSummary);

            _lblRemapSyncBadge = new Label
            {
                Text = "Applied",
                HorizontalAlignment = HorizontalAlignment.Right,
                TooltipText = "Indicates whether current bone remap & chain edits match the 3D preview."
            };
            _lblRemapSyncBadge.AddThemeColorOverride("font_color", ThemeManager.GetColor(nameof(ThemePaletteData.SemanticSuccess)));
            statusRow.AddChild(_lblRemapSyncBadge);
            remapBody.AddChild(statusRow);

            _lblRemapDiffSummary = new Label
            {
                Text = "Diff: 0 user overrides | 0 unmapped core",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            ImporterStyle.ApplyHintStyle(_lblRemapDiffSummary);
            _secondaryLabels.Add(_lblRemapDiffSummary);
            remapBody.AddChild(_lblRemapDiffSummary);

            var actionsFlow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            actionsFlow.AddThemeConstantOverride("h_separation", 6);
            actionsFlow.AddThemeConstantOverride("v_separation", 6);

            _btnAutoMapBones = new Button
            {
                Text = "Auto-Map Bones",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(124, 30),
                TooltipText = "Re-runs HumanoidBoneMapper + RigBoneClassifier across all core slots."
            };
            SetButtonIcon(_btnAutoMapBones, "res://assets/at-icons/sparkles.svg", 16);
            _btnAutoMapBones.Pressed += OnAutoMapBonesPressed;
            actionsFlow.AddChild(_btnAutoMapBones);

            _btnMirrorBones = new Button
            {
                Text = "Mirror L <-> R",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(114, 30),
                TooltipText = "Mirrors mapped Left limb/finger assignments to Right source bones (or vice versa)."
            };
            SetButtonIcon(_btnMirrorBones, "res://assets/at-icons/mirror_horizontally.svg", 16);
            _btnMirrorBones.Pressed += OnMirrorBonesPressed;
            actionsFlow.AddChild(_btnMirrorBones);

            _btnClearUnmatchedBones = new Button
            {
                Text = "Clear Unmatched",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(122, 30),
                TooltipText = "Clears duplicate conflicts and restores auto-detected assignments."
            };
            SetButtonIcon(_btnClearUnmatchedBones, "res://assets/at-icons/eraser.svg", 16);
            _btnClearUnmatchedBones.Pressed += OnClearUnmatchedBonesPressed;
            actionsFlow.AddChild(_btnClearUnmatchedBones);
            remapBody.AddChild(actionsFlow);

            var searchHBox = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            searchHBox.AddThemeConstantOverride("separation", 6);
            _txtBoneSearch = new LineEdit
            {
                PlaceholderText = "Search target or source bone...",
                ClearButtonEnabled = true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Filter bone mapping slots by target slot name, canonical name, or mapped source bone."
            };
            _txtBoneSearch.TextChanged += (_) => ApplyBoneMappingFilter();
            searchHBox.AddChild(_txtBoneSearch);

            _btnFilterIssuesOnly = new Button
            {
                Text = "Only mismatches",
                ThemeTypeVariation = "ToolbarButton",
                ToggleMode = true,
                ButtonPressed = false,
                CustomMinimumSize = new Vector2(130, 34),
                TooltipText = "Show only duplicate conflicts, mismatches, or missing core bones."
            };
            SetButtonIcon(_btnFilterIssuesOnly, "res://assets/at-icons/filter.svg", 16);
            _btnFilterIssuesOnly.Toggled += (_) => ApplyBoneMappingFilter();
            searchHBox.AddChild(_btnFilterIssuesOnly);
            remapBody.AddChild(searchHBox);

            var filterRow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            filterRow.AddThemeConstantOverride("h_separation", 6);
            filterRow.AddThemeConstantOverride("v_separation", 4);
            _btnFilterUnmappedOnly = new Button { Text = "Unmapped Only", ThemeTypeVariation = "ToolbarButton", ToggleMode = true, CustomMinimumSize = new Vector2(104, 26) };
            _btnFilterWeightedOnly = new Button { Text = "Has Weights Only", ThemeTypeVariation = "ToolbarButton", ToggleMode = true, CustomMinimumSize = new Vector2(114, 26) };
            _btnFilterShowControls = new Button { Text = "Show Controls", ThemeTypeVariation = "ToolbarButton", ToggleMode = true, CustomMinimumSize = new Vector2(102, 26) };
            _btnFilterUnmappedOnly.Toggled += (_) => ApplyBoneMappingFilter();
            _btnFilterWeightedOnly.Toggled += (_) => ApplyBoneMappingFilter();
            _btnFilterShowControls.Toggled += (_) => AnalyzeModelRigStatus();
            filterRow.AddChild(_btnFilterUnmappedOnly);
            filterRow.AddChild(_btnFilterWeightedOnly);
            filterRow.AddChild(_btnFilterShowControls);
            remapBody.AddChild(filterRow);

            _boxPreRiggedMapping = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = true };
            _boxPreRiggedMapping.AddThemeConstantOverride("separation", 8);
            _boxBoneMappingContainer = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _boxBoneMappingContainer.AddThemeConstantOverride("separation", 8);
            _boxPreRiggedMapping.AddChild(_boxBoneMappingContainer);
            remapSubBox.AddChild(_boxPreRiggedMapping);

            // =========================================================
            // SUB-VIEW 1: DEDICATED WEIGHT PAINT WORKSPACE
            // =========================================================
            var weightsSubBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            weightsSubBox.AddThemeConstantOverride("separation", 8);
            _boxSubViewWeights = weightsSubBox;
            vBox.AddChild(weightsSubBox);

            var (weightCard, weightBody) = CreateInspectorCard("3D Weight Paint & Heatmap", "Pick any core or chain bone below to inspect and paint skin weights cleanly.");
            weightsSubBox.AddChild(weightCard);

            var tab3WeightHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = true };
            tab3WeightHost.AddThemeConstantOverride("separation", 8);
            _boxTab3WeightPaintHost = tab3WeightHost;
            var quickLinksLbl = new Label { Text = "Quick-Pick Chain / Anchor Bones:", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            ImporterStyle.ApplyHintStyle(quickLinksLbl);
            _secondaryLabels.Add(quickLinksLbl);
            tab3WeightHost.AddChild(quickLinksLbl);
            _boxTab3WeightBoneChips = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _boxTab3WeightBoneChips.AddThemeConstantOverride("h_separation", 6);
            _boxTab3WeightBoneChips.AddThemeConstantOverride("v_separation", 4);
            tab3WeightHost.AddChild(_boxTab3WeightBoneChips);
            weightBody.AddChild(tab3WeightHost);

            // =========================================================
            // SUB-VIEW 2: PHYSICS & VALVE PRESETS
            // =========================================================
            var physSubBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            physSubBox.AddThemeConstantOverride("separation", 10);
            _boxSubViewPhysics = physSubBox;
            vBox.AddChild(physSubBox);

            var chainsPane = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = true };
            chainsPane.AddThemeConstantOverride("separation", 8);
            physSubBox.AddChild(chainsPane);

            var (chainCard, chainBody) = CreateInspectorCard("Physics & Valve Presets", "Pick a chain or group preset below. Use Stress Test in the 3D viewport toolbar to preview extreme poses.");
            chainsPane.AddChild(chainCard);

            _chkPhysicsMasterTab3 = new CheckBox
            {
                Text = "Enable Custom Physics Simulation (All Chains)",
                ButtonPressed = true,
                TooltipText = "Master switch for all custom hair/cloth physics simulation in viewport & export."
            };
            _chkPhysicsMasterTab3.Toggled += (on) =>
            {
                if (_chkEnableCustomPhysics != null && _chkEnableCustomPhysics.ButtonPressed != on)
                    _chkEnableCustomPhysics.SetPressedNoSignal(on);
                UpdateRigPhysicsLinkText();
                MarkRemapStaleAndRefresh();
                RefreshClothingChainsPanel();
            };
            chainBody.AddChild(_chkPhysicsMasterTab3);

            _chkPreserveClothingChains = new CheckBox
            {
                Text = "Auto-preserve multi-bone cloth & hair chains on load",
                ButtonPressed = true,
                TooltipText = "When checked, multi-bone hair/skirt/cape chains default to Jiggle Physics (1-bone bangs stay locked to head)."
            };
            _chkPreserveClothingChains.Toggled += (_) => MarkRemapStaleAndRefresh();
            chainBody.AddChild(_chkPreserveClothingChains);

            _lblClothingChainSummary = new Label { Text = "No extra clothing/accessory chains detected.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            ImporterStyle.ApplyHintStyle(_lblClothingChainSummary);
            _secondaryLabels.Add(_lblClothingChainSummary);
            chainBody.AddChild(_lblClothingChainSummary);

            // Filter & Bulk Action Bar
            var chainFilterRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            chainFilterRow.AddThemeConstantOverride("separation", 6);
            _txtChainSearch = new LineEdit { PlaceholderText = "Search chains (hair, skirt, ribbon)...", ClearButtonEnabled = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _txtChainSearch.TextChanged += (_) => RefreshClothingChainsPanel();
            chainFilterRow.AddChild(_txtChainSearch);

            _optChainCategoryFilter = new OptionButton { CustomMinimumSize = new Vector2(115, 28) };
            ConfigureOptionButton(_optChainCategoryFilter, 115);
            _optChainCategoryFilter.AddItem("All Categories", 0);
            _optChainCategoryFilter.AddItem("Hair", 1);
            _optChainCategoryFilter.AddItem("Cloth & Skirt", 2);
            _optChainCategoryFilter.AddItem("Accessories", 3);
            _optChainCategoryFilter.ItemSelected += (_) => RefreshClothingChainsPanel();
            chainFilterRow.AddChild(_optChainCategoryFilter);
            chainBody.AddChild(chainFilterRow);

            var bulkPhysRow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            bulkPhysRow.AddThemeConstantOverride("h_separation", 6);
            bulkPhysRow.AddThemeConstantOverride("v_separation", 4);

            void AddBulkChainBtn(string txt, string tip, Action action)
            {
                var b = new Button { Text = txt, ThemeTypeVariation = "ToolbarButton", CustomMinimumSize = new Vector2(82, 26), TooltipText = tip };
                b.Pressed += () => { action(); MarkRemapStaleAndRefresh(); RefreshClothingChainsPanel(); };
                bulkPhysRow.AddChild(b);
            }
            AddBulkChainBtn("Auto-Detect Presets", "Automatically assign Valve-backed physics presets (Silver, Yamato, Geist, Wraith, Venator, Celeste, etc.) to each chain", () =>
            {
                RigBoneClassifier.ApplyRecommendedPhysicsModes(_boneChains, _chkPhysicsMasterTab3?.ButtonPressed ?? true, resetTuningValues: true);
            });
            AddBulkChainBtn("Physics All", "Enable physics simulation on all visible chains", () => ApplyPolicyToFilteredChains(ChainPolicy.PreservePhysicsChain));
            AddBulkChainBtn("Static All", "Merge all visible chains statically onto their anchor bones", () => ApplyPolicyToFilteredChains(ChainPolicy.CollapseToNearestCore));
            AddBulkChainBtn("Freeze Hair", "Lock all hair chains static to head while keeping cloth physics", () =>
            {
                foreach (var c in _boneChains)
                    if (c.Name.Contains("hair", StringComparison.OrdinalIgnoreCase) || c.RootSourceBone.Contains("hair", StringComparison.OrdinalIgnoreCase))
                        c.Policy = ChainPolicy.CollapseToNearestCore;
            });
            chainBody.AddChild(bulkPhysRow);

            // Compact Scrollable Chain List (bounded height so inspector stays visible!)
            var chainListScroll = new ScrollContainer
            {
                CustomMinimumSize = new Vector2(0, 210),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };
            _boxClothingChainsList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _boxClothingChainsList.AddThemeConstantOverride("separation", 4);
            chainListScroll.AddChild(_boxClothingChainsList);
            chainBody.AddChild(chainListScroll);

            // Single Shared Chain Inspector Drawer (Clean Preset-First + 4 Macro Sliders + Collapsed Advanced)
            _boxChainInspector = new PanelContainer { ThemeTypeVariation = "CardPanel", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            var inspBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            inspBox.AddThemeConstantOverride("separation", 8);
            _boxChainInspector.AddChild(inspBox);

            _lblSelectedChainTitle = new Label { Text = "Selected Chain: --" };
            ImporterStyle.ApplyFontSize(_lblSelectedChainTitle, ImporterStyle.FontBody);
            _accentLabels.Add(_lblSelectedChainTitle);
            inspBox.AddChild(_lblSelectedChainTitle);

            // Clean section header above the primary Physics Preset selector button
            var presetLblRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            presetLblRow.AddThemeConstantOverride("separation", 6);
            var lblPresetSection = new Label
            {
                Text = "PHYSICS PRESET",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            ImporterStyle.ApplyFontSize(lblPresetSection, ImporterStyle.FontHint);
            lblPresetSection.AddThemeColorOverride("font_color", PlaygroundThemeHelper.Parchment);
            _accentLabels.Add(lblPresetSection);
            presetLblRow.AddChild(lblPresetSection);
            var lblPresetHintRight = new Label
            {
                Text = "Click to choose preset",
                HorizontalAlignment = HorizontalAlignment.Right
            };
            ImporterStyle.ApplyHintStyle(lblPresetHintRight);
            _secondaryLabels.Add(lblPresetHintRight);
            presetLblRow.AddChild(lblPresetHintRight);
            inspBox.AddChild(presetLblRow);

            // Prominent Primary Visual Preset Selector Card Button (opens 4-column visual gallery popup)
            _btnSelectPresetCard = new Button
            {
                ThemeTypeVariation = "ImporterPrimaryButton",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 68),
                TooltipText = "Click to open the Valve-backed Deadlock physics preset gallery (applies to all sibling chains in this garment group)."
            };
            var presetBtnPad = new MarginContainer
            {
                LayoutMode = 1,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            presetBtnPad.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            presetBtnPad.AddThemeConstantOverride("margin_left", 10);
            presetBtnPad.AddThemeConstantOverride("margin_right", 12);
            presetBtnPad.AddThemeConstantOverride("margin_top", 8);
            presetBtnPad.AddThemeConstantOverride("margin_bottom", 8);
            _btnSelectPresetCard.AddChild(presetBtnPad);

            var presetBtnRow = new HBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            presetBtnRow.AddThemeConstantOverride("separation", 12);
            presetBtnPad.AddChild(presetBtnRow);

            var thumbFrameStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.08f, 0.10f, 0.09f, 0.92f),
                BorderColor = new Color(0.24f, 0.30f, 0.27f, 0.70f),
                BorderWidthLeft = 1,
                BorderWidthRight = 1,
                BorderWidthTop = 1,
                BorderWidthBottom = 1,
                CornerRadiusTopLeft = 6,
                CornerRadiusTopRight = 6,
                CornerRadiusBottomLeft = 6,
                CornerRadiusBottomRight = 6
            };
            var presetThumbFrame = new PanelContainer
            {
                CustomMinimumSize = new Vector2(48, 48),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            presetThumbFrame.AddThemeStyleboxOverride("panel", thumbFrameStyle);
            _texSelectedPresetIcon = new TextureRect
            {
                CustomMinimumSize = new Vector2(42, 42),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            presetThumbFrame.AddChild(_texSelectedPresetIcon);
            presetBtnRow.AddChild(presetThumbFrame);

            var presetBtnTextCol = new VBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            presetBtnTextCol.AddThemeConstantOverride("separation", 2);
            _lblSelectedPresetName = new Label
            {
                Text = "Custom (Manual Tuning)",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            ImporterStyle.ApplyFontSize(_lblSelectedPresetName, ImporterStyle.FontBody);
            _lblSelectedPresetName.AddThemeColorOverride("font_color", PlaygroundThemeHelper.Parchment);
            _accentLabels.Add(_lblSelectedPresetName);
            _lblSelectedPresetTag = new Label
            {
                Text = "Select a Valve-calibrated Deadlock preset",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            ImporterStyle.ApplyHintStyle(_lblSelectedPresetTag);
            _secondaryLabels.Add(_lblSelectedPresetTag);
            presetBtnTextCol.AddChild(_lblSelectedPresetName);
            presetBtnTextCol.AddChild(_lblSelectedPresetTag);
            presetBtnRow.AddChild(presetBtnTextCol);

            var lblPresetChev = new Label
            {
                Text = "Change ▾",
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            ImporterStyle.ApplyFontSize(lblPresetChev, ImporterStyle.FontHint);
            lblPresetChev.AddThemeColorOverride("font_color", PlaygroundThemeHelper.Parchment);
            presetBtnRow.AddChild(lblPresetChev);

            _btnSelectPresetCard.Pressed += () =>
            {
                if (_selectedChain != null)
                {
                    var groupTargets = GetSiblingGroupChains(_selectedChain);
                    OpenPhysicsPresetPicker(groupTargets, _btnSelectPresetCard);
                }
            };
            inspBox.AddChild(_btnSelectPresetCard);

            // Quick Action Row: Apply to Siblings | Paint Weights -> | Pin Root Rigid
            var topQuickRow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            topQuickRow.AddThemeConstantOverride("h_separation", 6);
            topQuickRow.AddThemeConstantOverride("v_separation", 6);

            var btnApplySiblings = new Button
            {
                Text = "Apply to Siblings",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(120, 32),
                TooltipText = "Copy this preset & tuning to all sibling chains in the same garment group (e.g. all Back Hair or all Skirt strands)."
            };
            SetButtonIcon(btnApplySiblings, "res://assets/at-icons/duplicate.svg", 15);
            btnApplySiblings.Pressed += ApplySelectedChainToSiblingGroup;
            topQuickRow.AddChild(btnApplySiblings);

            _btnChainWeightPaintToggle = new Button
            {
                Text = "Paint Weights →",
                CustomMinimumSize = new Vector2(128, 32),
                ThemeTypeVariation = "ToolbarButton",
                TooltipText = "Jump directly to the dedicated Weights tab with this chain's bone selected."
            };
            SetButtonIcon(_btnChainWeightPaintToggle, "res://assets/at-icons/brush.svg", 15);
            _btnChainWeightPaintToggle.Pressed += () =>
            {
                if (_selectedChain == null) return;
                string boneToInspect = _selectedChain.AppendedHeroBoneNames?.FirstOrDefault() ?? _selectedChain.RootSourceBone;
                InspectWeightsForBone(boneToInspect);
            };
            topQuickRow.AddChild(_btnChainWeightPaintToggle);

            var btnPinCollar = new Button
            {
                Text = "Pin Root Rigid",
                CustomMinimumSize = new Vector2(108, 32),
                ThemeTypeVariation = "ToolbarButton",
                TooltipText = "Pins the root/collar bone rigidly (Simulate = false) across sibling chains so capes/skirts don't crease at the seam."
            };
            btnPinCollar.Pressed += OnPinCollarRootRigidPressed;
            topQuickRow.AddChild(btnPinCollar);
            inspBox.AddChild(topQuickRow);

            // 4 Intuitive Macro Sliders (Always Visible)
            var macroStack = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            macroStack.AddThemeConstantOverride("separation", 6);

            (_sliderMacroLooseness, _lblValMacroLooseness) = AddExplainedSlider(
                macroStack,
                "Looseness / Flow",
                "0% = firmer hold to animated pose, 50% = exact Valve preset, 100% = looser / freer drape.",
                0.0, 1.0, 0.02, "F2",
                (v) =>
                {
                    if (_lblValMacroLooseness != null) _lblValMacroLooseness.Text = $"{v * 100.0:F0}%";
                    if (!_isUpdatingChainInspector && _selectedChain != null)
                    {
                        if (string.Equals(_selectedChain.PresetId, PhysicsPresetCatalog.CustomId, StringComparison.OrdinalIgnoreCase))
                        {
                            // For Custom chains, gently scale stiffness inversely with looseness delta
                            float oldL = _selectedChain.MacroLooseness;
                            float delta = (float)v - oldL;
                            _selectedChain.MacroLooseness = (float)v;
                            _selectedChain.StiffnessRoot = Mathf.Clamp(_selectedChain.StiffnessRoot - delta * 0.6f, 0.02f, 0.98f);
                            _selectedChain.StiffnessTip = Mathf.Clamp(_selectedChain.StiffnessTip - delta * 0.4f, 0.0f, _selectedChain.StiffnessRoot);
                        }
                        else
                        {
                            PhysicsPresetCatalog.ApplyMacrosToChain(_selectedChain, (float)v, _selectedChain.Gravity, _selectedChain.Damping, _selectedChain.MacroCollisionScale);
                        }
                        SyncAdvancedSlidersFromSelectedChain();
                        ApplyChainParamsLiveToDriver();
                    }
                });

            (_sliderMacroGravity, _lblValMacroGravity) = AddExplainedSlider(
                macroStack,
                "Weight / Gravity",
                "Downward pull (1.0× = standard 360 in/s², 3.0× = heavy Yamato kimono/skirt).",
                0.0, 3.5, 0.05, "F2",
                (v) =>
                {
                    if (_lblValMacroGravity != null) _lblValMacroGravity.Text = $"{v:F2}×";
                    if (!_isUpdatingChainInspector && _selectedChain != null)
                    {
                        _selectedChain.Gravity = (float)v;
                        SyncAdvancedSlidersFromSelectedChain();
                        ApplyChainParamsLiveToDriver();
                    }
                });

            (_sliderMacroDamping, _lblValMacroDamping) = AddExplainedSlider(
                macroStack,
                "Damping / Calmness",
                "0% = lively Valve default (0 air drag), higher values calm whipping & oscillation.",
                0.0, 1.0, 0.02, "F2",
                (v) =>
                {
                    if (_lblValMacroDamping != null) _lblValMacroDamping.Text = $"{v * 100.0:F0}%";
                    if (!_isUpdatingChainInspector && _selectedChain != null)
                    {
                        _selectedChain.Damping = (float)v;
                        SyncAdvancedSlidersFromSelectedChain();
                        ApplyChainParamsLiveToDriver();
                    }
                });

            (_sliderMacroColMargin, _lblValMacroColMargin) = AddExplainedSlider(
                macroStack,
                "Collision Margin",
                "Scales the chain's body-clearance sphere radius (1.0× = exact Valve preset).",
                0.5, 2.0, 0.05, "F2",
                (v) =>
                {
                    if (_lblValMacroColMargin != null) _lblValMacroColMargin.Text = $"{v:F2}×";
                    if (!_isUpdatingChainInspector && _selectedChain != null)
                    {
                        if (string.Equals(_selectedChain.PresetId, PhysicsPresetCatalog.CustomId, StringComparison.OrdinalIgnoreCase))
                        {
                            float ratio = _selectedChain.MacroCollisionScale > 0.05f ? (float)v / _selectedChain.MacroCollisionScale : 1f;
                            _selectedChain.MacroCollisionScale = (float)v;
                            _selectedChain.CollisionRadiusTipInches = Mathf.Clamp(_selectedChain.CollisionRadiusTipInches * ratio, 0.1f, 6.0f);
                        }
                        else
                        {
                            PhysicsPresetCatalog.ApplyMacrosToChain(_selectedChain, _selectedChain.MacroLooseness, _selectedChain.Gravity, _selectedChain.Damping, (float)v);
                        }
                        SyncAdvancedSlidersFromSelectedChain();
                        ApplyChainParamsLiveToDriver();
                    }
                });
            inspBox.AddChild(macroStack);

            // Collapsed Advanced Tuning Foldout (all low-level CSDK13 & Per-Joint controls tucked cleanly away)
            var (advCard, advBody) = CreateInspectorCard(
                "Advanced Tuning (CSDK13 & Per-Joint)",
                "Low-level solver mode, root/tip curves, taper power, stray leash, and per-bone overrides.",
                collapsible: true,
                startExpanded: false);
            inspBox.AddChild(advCard);

            var polRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            polRow.AddThemeConstantOverride("separation", 6);
            polRow.AddChild(new Label { Text = "Policy:", CustomMinimumSize = new Vector2(54, 0) });
            _optSelChainPolicy = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            ConfigureOptionButton(_optSelChainPolicy);
            _optSelChainPolicy.AddItem("Simulate Physics", (int)ChainPolicy.PreservePhysicsChain);
            _optSelChainPolicy.AddItem("Static (Merge to Parent)", (int)ChainPolicy.CollapseToNearestCore);
            _optSelChainPolicy.ItemSelected += (idx) =>
            {
                if (_isUpdatingChainInspector || _selectedChain == null) return;
                _selectedChain.Policy = (ChainPolicy)_optSelChainPolicy.GetItemId((int)idx);
                MarkRemapStaleAndRefresh();
                RefreshClothingChainsPanel();
            };
            polRow.AddChild(_optSelChainPolicy);
            advBody.AddChild(polRow);

            // Hidden compatibility OptionButton backed by the prominent 3-way segmented Physics Type bar below
            _optSelChainSim = new OptionButton { Visible = false };
            _optSelChainSim.AddItem("Cloth Chain", (int)SimulationKind.Cloth);
            _optSelChainSim.AddItem("Proxy Sheet", (int)SimulationKind.ProxyCloth);
            _optSelChainSim.AddItem("Jiggle Bounce", (int)SimulationKind.Jiggle);
            _optSelChainSim.ItemSelected += (idx) =>
            {
                if (_isUpdatingChainInspector || _selectedChain == null) return;
                SetSelectedChainSimulationKind((SimulationKind)_optSelChainSim.GetItemId((int)idx));
            };
            advBody.AddChild(_optSelChainSim);

            var simHeaderRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            var lblSimHdr = new Label { Text = "Physics Type:", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            ImporterStyle.ApplyFontSize(lblSimHdr, ImporterStyle.FontBody);
            _accentLabels.Add(lblSimHdr);
            simHeaderRow.AddChild(lblSimHdr);
            advBody.AddChild(simHeaderRow);

            var simSegRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            simSegRow.AddThemeConstantOverride("separation", 6);
            (string label, SimulationKind kind, string tip)[] simOpts =
            {
                ("Proxy Sheet", SimulationKind.ProxyCloth, "Stitches adjacent hair/skirt strands into a continuous Source 2 cloth sheet with cross-strand stabilization."),
                ("Cloth Chain", SimulationKind.Cloth, "Independent hanging chain (ponytails, ribbons, ropes) with Source 2 ClothChain collision."),
                ("Jiggle Bounce", SimulationKind.Jiggle, "Compact rigid rotational bounce (breasts, glutes, ears, pouches) with zero hair/capsule collision.")
            };
            for (int si = 0; si < simOpts.Length; si++)
            {
                var (sLbl, sKind, sTip) = simOpts[si];
                var btnSim = new Button
                {
                    Text = sLbl,
                    ToggleMode = true,
                    ThemeTypeVariation = "ToolbarButton",
                    CustomMinimumSize = new Vector2(0, 34),
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    TooltipText = sTip
                };
                ImporterStyle.ApplyFontSize(btnSim, ImporterStyle.FontHint);
                btnSim.Pressed += () =>
                {
                    if (_isUpdatingChainInspector || _selectedChain == null) return;
                    MarkSelectedChainCustomPreset();
                    SetSelectedChainSimulationKind(sKind);
                };
                _chainSimSegButtons[si] = btnSim;
                simSegRow.AddChild(btnSim);
            }
            advBody.AddChild(simSegRow);

            _lblSelChainSimHint = new Label
            {
                Text = "Select a Physics Type to choose how this chain simulates in-game.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            ImporterStyle.ApplyHintStyle(_lblSelChainSimHint);
            _secondaryLabels.Add(_lblSelChainSimHint);
            advBody.AddChild(_lblSelChainSimHint);

            var ancRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            ancRow.AddThemeConstantOverride("separation", 6);
            ancRow.AddChild(new Label { Text = "Anchor:", CustomMinimumSize = new Vector2(54, 0) });
            _optSelChainAnchor = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            ConfigureOptionButton(_optSelChainAnchor);
            _optSelChainAnchor.ItemSelected += (idx) =>
            {
                if (_isUpdatingChainInspector || _selectedChain == null) return;
                _selectedChain.AnchorHeroBone = _optSelChainAnchor.GetItemText((int)idx);
                MarkRemapStaleAndRefresh();
            };
            ancRow.AddChild(_optSelChainAnchor);
            advBody.AddChild(ancRow);

            var sliderStack = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            sliderStack.AddThemeConstantOverride("separation", 8);

            (_sliderSelStiffRoot, _lblValStiffRoot) = AddExplainedSlider(
                sliderStack,
                "Root Stiffness",
                "Root pull toward animated pose (0.00 = free hinge, 1.00 = 0.90 goalF).",
                0.00, 1.00, 0.02, "F2",
                (v) =>
                {
                    float gf = PhysicsParamResolver.ResolveRootGoalForce((float)v);
                    if (_lblValStiffRoot != null) _lblValStiffRoot.Text = $"{v:F2} (gF {gf:F2})";
                    if (!_isUpdatingChainInspector && _selectedChain != null)
                    {
                        MarkSelectedChainCustomPreset();
                        _selectedChain.StiffnessRoot = (float)v;
                        if (_lblValStiffTip != null)
                        {
                            float tf = PhysicsParamResolver.ResolveTipGoalForce(_selectedChain.StiffnessTip, gf);
                            _lblValStiffTip.Text = $"{_selectedChain.StiffnessTip:F2} (gF {tf:F3})";
                        }
                        ApplyChainParamsLiveToDriver();
                    }
                });

            (_sliderSelStiffTip, _lblValStiffTip) = AddExplainedSlider(
                sliderStack,
                "Tip Stiffness",
                "Hem/tip pull toward animated pose (0.00 = free hanging under gravity).",
                0.00, 1.00, 0.02, "F2",
                (v) =>
                {
                    float rf = PhysicsParamResolver.ResolveRootGoalForce(_selectedChain?.StiffnessRoot ?? 0.55f);
                    float tf = PhysicsParamResolver.ResolveTipGoalForce((float)v, rf);
                    if (_lblValStiffTip != null) _lblValStiffTip.Text = $"{v:F2} (gF {tf:F3})";
                    if (!_isUpdatingChainInspector && _selectedChain != null)
                    {
                        MarkSelectedChainCustomPreset();
                        _selectedChain.StiffnessTip = (float)v;
                        ApplyChainParamsLiveToDriver();
                    }
                });

            (_sliderSelTaperPower, _lblValTaperPower) = AddExplainedSlider(
                sliderStack,
                "Curve Taper Exponent",
                "0.6 = holds mid-length shape (Lady Geist dress), 1.85 = standard, 2.6 = drops fast (Yamato kimono).",
                0.35, 3.80, 0.05, "F2",
                (v) =>
                {
                    if (_lblValTaperPower != null) _lblValTaperPower.Text = $"{v:F2}";
                    if (!_isUpdatingChainInspector && _selectedChain != null)
                    {
                        MarkSelectedChainCustomPreset();
                        _selectedChain.GoalTaperPower = (float)v;
                        ApplyChainParamsLiveToDriver();
                    }
                });

            (_sliderSelDamping, _lblValDamping) = AddExplainedSlider(
                sliderStack,
                "Damping / Air Drag",
                "Compiled Source 2 PointDamping (0.00 = Valve default 0.000, 1.00 = 0.750).",
                0.00, 1.00, 0.02, "F2",
                (v) =>
                {
                    float pd = PhysicsParamResolver.ResolveCompiledPointDamping((float)v);
                    if (_lblValDamping != null) _lblValDamping.Text = $"{v:F2} (pDamp {pd:F2})";
                    if (!_isUpdatingChainInspector && _selectedChain != null)
                    {
                        _selectedChain.Damping = (float)v;
                        _sliderMacroDamping?.SetValueNoSignal(v);
                        if (_lblValMacroDamping != null) _lblValMacroDamping.Text = $"{v * 100.0:F0}%";
                        ApplyChainParamsLiveToDriver();
                    }
                });

            (_sliderSelGravity, _lblValGravity) = AddExplainedSlider(
                sliderStack,
                "Gravity",
                "Downward pull (1.0 = 360 in/s² standard, 3.0 = Yamato kimono).",
                0.0, 3.5, 0.05, "F2",
                (v) =>
                {
                    if (_lblValGravity != null) _lblValGravity.Text = $"{v:F2} ({v * 360.0:F0})";
                    if (!_isUpdatingChainInspector && _selectedChain != null)
                    {
                        _selectedChain.Gravity = (float)v;
                        _sliderMacroGravity?.SetValueNoSignal(v);
                        if (_lblValMacroGravity != null) _lblValMacroGravity.Text = $"{v:F2}×";
                        ApplyChainParamsLiveToDriver();
                    }
                });

            (_sliderSelColRadius, _lblValColRadius) = AddExplainedSlider(
                sliderStack,
                "Tip Collision Radius (in)",
                "Lower collision sphere radius against body capsules.",
                0.0, 6.0, 0.1, "F1",
                (v) =>
                {
                    if (_lblValColRadius != null) _lblValColRadius.Text = $"{v:F1} in";
                    if (!_isUpdatingChainInspector && _selectedChain != null)
                    {
                        MarkSelectedChainCustomPreset();
                        _selectedChain.CollisionRadiusTipInches = (float)v;
                        ApplyChainParamsLiveToDriver();
                    }
                });

            _chkSelStrayUnlimited = new CheckBox
            {
                Text = "Unlimited / Disabled Stray Leash (-1) — Recommended for Hair & Capes",
                ButtonPressed = true,
                TooltipText = "Matches Valve's Yamato/Wraith: disables AnimStrayRadius so looking upward never overrides body collision capsules."
            };
            ImporterStyle.ApplyFontSize(_chkSelStrayUnlimited, ImporterStyle.FontHint);
            _chkSelStrayUnlimited.Toggled += (on) =>
            {
                if (_sliderSelStrayRadius != null) _sliderSelStrayRadius.Editable = !on;
                if (_lblValStrayRadius != null)
                    _lblValStrayRadius.Text = on ? "Unlimited (-1)" : $"{(_sliderSelStrayRadius?.Value ?? 40.0):F1} in";
                if (!_isUpdatingChainInspector && _selectedChain != null)
                {
                    _selectedChain.StrayRadiusInches = on ? -1.0f : (float)(_sliderSelStrayRadius?.Value ?? 40.0);
                    ApplyChainParamsLiveToDriver();
                }
            };
            sliderStack.AddChild(_chkSelStrayUnlimited);

            (_sliderSelStrayRadius, _lblValStrayRadius) = AddExplainedSlider(
                sliderStack,
                "Max Swing / Stray Leash (in)",
                "Optional max distance from animated pose (20–220 in for long hair like Vindicta, or disable above so look-up never clips).",
                1.5, 220.0, 1.0, "F1",
                (v) =>
                {
                    bool unlim = _chkSelStrayUnlimited?.ButtonPressed ?? false;
                    if (_lblValStrayRadius != null) _lblValStrayRadius.Text = unlim ? "Unlimited (-1)" : $"{v:F1} in";
                    if (!_isUpdatingChainInspector && _selectedChain != null && !unlim)
                    {
                        _selectedChain.StrayRadiusInches = (float)v;
                        ApplyChainParamsLiveToDriver();
                    }
                });

            _chkSelPlanarizeBack = new CheckBox
            {
                Text = "One-Sided Back Barrier (CollisionPlanes) — Stops Hair/Capes Clipping Chest",
                ButtonPressed = false,
                TooltipText = "Emits a CSDK13 ClothVertexMap + planarized spine_2/spine_3 barrier (matching Valve's Unicorn ponytail) so long hair & capes cannot flip into the chest."
            };
            ImporterStyle.ApplyFontSize(_chkSelPlanarizeBack, ImporterStyle.FontHint);
            _chkSelPlanarizeBack.Toggled += (on) =>
            {
                if (!_isUpdatingChainInspector && _selectedChain != null)
                {
                    _selectedChain.PlanarizeBackBarrier = on;
                    ApplyChainParamsLiveToDriver();
                }
            };
            sliderStack.AddChild(_chkSelPlanarizeBack);

            _chkSelOpenSheet = new CheckBox
            {
                Text = "Open Sheet (Cape / Curtain — uncheck for 360° Skirt)",
                ButtonPressed = true,
                TooltipText = "When checked, ProxyCloth stays an open sheet (cape/back hair). Uncheck for closed 360° wrap-around skirts/dresses."
            };
            ImporterStyle.ApplyFontSize(_chkSelOpenSheet, ImporterStyle.FontHint);
            _chkSelOpenSheet.Toggled += (on) =>
            {
                if (!_isUpdatingChainInspector && _selectedChain != null)
                {
                    _selectedChain.OpenSheet = on;
                    ApplyChainParamsLiveToDriver();
                }
            };
            sliderStack.AddChild(_chkSelOpenSheet);

            _chkSelAnchorFreeRotate = new CheckBox
            {
                Text = "Free Collar / Nape Hinge (cloth_anchor_free_rotate)",
                ButtonPressed = false,
                TooltipText = "Lets hair/cape roots hinge freely at the collar/nape instead of being levered rigidly by head or spine pitch."
            };
            ImporterStyle.ApplyFontSize(_chkSelAnchorFreeRotate, ImporterStyle.FontHint);
            _chkSelAnchorFreeRotate.Toggled += (on) =>
            {
                if (!_isUpdatingChainInspector && _selectedChain != null)
                {
                    _selectedChain.AnchorFreeRotate = on;
                    ApplyChainParamsLiveToDriver();
                }
            };
            sliderStack.AddChild(_chkSelAnchorFreeRotate);

            (_sliderSelFriction, _lblValFriction) = AddExplainedSlider(
                sliderStack,
                "Surface Friction",
                "Tangential sliding resistance against body/prop colliders (0.0 = slippery silk, 0.15 = Valve default, 0.6 = heavy wool).",
                0.0, 1.0, 0.02, "F2",
                (v) =>
                {
                    if (_lblValFriction != null) _lblValFriction.Text = $"{v:F2}";
                    if (!_isUpdatingChainInspector && _selectedChain != null)
                    {
                        _selectedChain.Friction = (float)v;
                        ApplyChainParamsLiveToDriver();
                    }
                });

            (_sliderSelAngleLimit, _lblValAngleLimit) = AddExplainedSlider(
                sliderStack,
                "Angle / Hinge Limit (°)",
                "Jiggle: max cone deflection (6–60°). Cloth Chain: stiff_hinge_angle out-of-plane swing limit (5–140°).",
                5.0, 140.0, 1.0, "F0",
                (v) =>
                {
                    if (_lblValAngleLimit != null) _lblValAngleLimit.Text = $"{v:F0}°";
                    if (!_isUpdatingChainInspector && _selectedChain != null)
                    {
                        _selectedChain.AngleLimitDeg = (float)v;
                        ApplyChainParamsLiveToDriver();
                    }
                });

            (_sliderSelTwistRelax, _lblValTwistRelax) = AddExplainedSlider(
                sliderStack,
                "Twist Relaxation (ClothChain)",
                "Torsion spring strength resisting axial twisting along ClothChain strands (0.0–2.0, default 1.0).",
                0.0, 2.0, 0.05, "F2",
                (v) =>
                {
                    if (_lblValTwistRelax != null) _lblValTwistRelax.Text = $"{v:F2}";
                    if (!_isUpdatingChainInspector && _selectedChain != null)
                    {
                        _selectedChain.TwistRelax = (float)v;
                        ApplyChainParamsLiveToDriver();
                    }
                });

            // ClothChain-only Extrude / Hinge Plane settings (matching Blender / Source 2 ClothChain)
            _boxClothExtrudeSection = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            _boxClothExtrudeSection.AddThemeConstantOverride("separation", 6);
            var lblExtHdr = new Label { Text = "ClothChain Ribbon Extrude & Hinge Plane:" };
            ImporterStyle.ApplyFontSize(lblExtHdr, ImporterStyle.FontHint);
            _accentLabels.Add(lblExtHdr);
            _boxClothExtrudeSection.AddChild(lblExtHdr);

            var extAxisRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            extAxisRow.AddThemeConstantOverride("separation", 6);
            extAxisRow.AddChild(new Label { Text = "Extrude Axis:", CustomMinimumSize = new Vector2(96, 0) });
            _optSelExtrudeAxis = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            ConfigureOptionButton(_optSelExtrudeAxis);
            _optSelExtrudeAxis.AddItem("X (Default)", 0);
            _optSelExtrudeAxis.AddItem("Y", 1);
            _optSelExtrudeAxis.AddItem("Z", 2);
            _optSelExtrudeAxis.ItemSelected += idx =>
            {
                if (!_isUpdatingChainInspector && _selectedChain != null)
                {
                    _selectedChain.ExtrudeForwardAxis = (int)idx;
                    ApplyChainParamsLiveToDriver();
                }
            };
            extAxisRow.AddChild(_optSelExtrudeAxis);
            _boxClothExtrudeSection.AddChild(extAxisRow);

            (_sliderSelExtrudeTwist, _lblValExtrudeTwist) = AddExplainedSlider(
                _boxClothExtrudeSection,
                "Extrude Twist (°)",
                "Rotates the ClothChain ribbon & hinge plane around the bone axis (e.g. 90° like Blender extrude twist).",
                -180.0, 180.0, 5.0, "F0",
                (v) =>
                {
                    if (_lblValExtrudeTwist != null) _lblValExtrudeTwist.Text = $"{v:F0}°";
                    if (!_isUpdatingChainInspector && _selectedChain != null)
                    {
                        _selectedChain.ExtrudeTwistDeg = (float)v;
                        ApplyChainParamsLiveToDriver();
                    }
                });

            (_sliderSelExtrudeRadius, _lblValExtrudeRadius) = AddExplainedSlider(
                _boxClothExtrudeSection,
                "Ribbon Extrude Radius (in)",
                "Half-width of the Source 2 ClothChain extruded ribbon in inches (default 1.0 in; widen to 2–4 in for ribbons/sashes).",
                0.2, 6.0, 0.1, "F1",
                (v) =>
                {
                    if (_lblValExtrudeRadius != null) _lblValExtrudeRadius.Text = $"{v:F1} in";
                    if (!_isUpdatingChainInspector && _selectedChain != null)
                    {
                        _selectedChain.ExtrudeRadiusInches = (float)v;
                        ApplyChainParamsLiveToDriver();
                    }
                });

            sliderStack.AddChild(_boxClothExtrudeSection);

            advBody.AddChild(sliderStack);

            // Per-Child Bone Overrides Section (inside Advanced Tuning foldout)
            var jointSectionHeader = new Label
            {
                Text = "Chain Bones — Per-Child Overrides (click any child bone to tune):",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            ImporterStyle.ApplyFontSize(jointSectionHeader, ImporterStyle.FontHint);
            _accentLabels.Add(jointSectionHeader);
            advBody.AddChild(jointSectionHeader);

            var jointScroll = new ScrollContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 96),
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };
            _boxChainJointList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _boxChainJointList.AddThemeConstantOverride("separation", 3);
            jointScroll.AddChild(_boxChainJointList);
            advBody.AddChild(jointScroll);

            _boxJointOverrideEditor = new PanelContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ThemeTypeVariation = "SubPanelContainer",
                Visible = false
            };
            var jointEdBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            jointEdBox.AddThemeConstantOverride("separation", 6);
            _boxJointOverrideEditor.AddChild(jointEdBox);

            var jointHdrRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            jointHdrRow.AddThemeConstantOverride("separation", 6);
            _lblSelectedJointTitle = new Label { Text = "Joint:", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            ImporterStyle.ApplyFontSize(_lblSelectedJointTitle, ImporterStyle.FontHint);
            _accentLabels.Add(_lblSelectedJointTitle);
            var btnResetJoint = new Button
            {
                Text = "Reset to Curve",
                CustomMinimumSize = new Vector2(118, 28),
                ThemeTypeVariation = "ToolbarButton",
                TooltipText = "Clear custom overrides on this bone so it inherits the chain's root-to-tip curve."
            };
            btnResetJoint.Pressed += ResetSelectedChainJointOverride;
            jointHdrRow.AddChild(_lblSelectedJointTitle);
            jointHdrRow.AddChild(btnResetJoint);
            jointEdBox.AddChild(jointHdrRow);

            _chkJointSimulate = new CheckBox
            {
                Text = "Simulate this bone (uncheck to pin/freeze rigid)",
                ButtonPressed = true
            };
            ImporterStyle.ApplyFontSize(_chkJointSimulate, ImporterStyle.FontHint);
            _chkJointSimulate.Toggled += (on) =>
            {
                if (_isUpdatingJointEditor) return;
                var jov = GetOrCreateSelectedJointOverride();
                if (jov == null) return;
                jov.Simulate = on;
                ApplyChainParamsLiveToDriver();
                RefreshChainJointListUI();
            };
            jointEdBox.AddChild(_chkJointSimulate);

            (_sliderJointGoal, _lblValJointGoal) = AddExplainedSlider(
                jointEdBox,
                "Bone Goal Strength",
                "0.0 = free hanging cloth/hair, 0.95 = tightly follows animation.",
                0.0, 0.99, 0.01, "F2",
                (v) =>
                {
                    if (_lblValJointGoal != null) _lblValJointGoal.Text = $"{v:F2}";
                    if (_isUpdatingJointEditor) return;
                    var jov = GetOrCreateSelectedJointOverride();
                    if (jov == null) return;
                    jov.GoalStrength = (float)v;
                    ApplyChainParamsLiveToDriver();
                    RefreshChainJointListUI();
                });

            (_sliderJointDamp, _lblValJointDamp) = AddExplainedSlider(
                jointEdBox,
                "Bone Goal Damping",
                "Keep near 0.00 on lower hair tips so tips don't pull into the torso during look-up/dash.",
                0.0, 0.50, 0.01, "F2",
                (v) =>
                {
                    if (_lblValJointDamp != null) _lblValJointDamp.Text = $"{v:F2}";
                    if (_isUpdatingJointEditor) return;
                    var jov = GetOrCreateSelectedJointOverride();
                    if (jov == null) return;
                    jov.GoalDamping = (float)v;
                    ApplyChainParamsLiveToDriver();
                    RefreshChainJointListUI();
                });

            (_sliderJointColRad, _lblValJointColRad) = AddExplainedSlider(
                jointEdBox,
                "Bone Collision Radius (in)",
                "Per-bone collision sphere radius against body capsules.",
                0.0, 8.0, 0.1, "F1",
                (v) =>
                {
                    if (_lblValJointColRad != null) _lblValJointColRad.Text = $"{v:F1} in";
                    if (_isUpdatingJointEditor) return;
                    var jov = GetOrCreateSelectedJointOverride();
                    if (jov == null) return;
                    jov.CollisionRadiusInches = (float)v;
                    ApplyChainParamsLiveToDriver();
                    RefreshChainJointListUI();
                });

            _chkJointStrayUnlimited = new CheckBox
            {
                Text = "Unlimited Stray on this bone (-1)",
                ButtonPressed = true
            };
            ImporterStyle.ApplyFontSize(_chkJointStrayUnlimited, ImporterStyle.FontHint);
            _chkJointStrayUnlimited.Toggled += (on) =>
            {
                if (_sliderJointStray != null) _sliderJointStray.Editable = !on;
                if (_lblValJointStray != null)
                    _lblValJointStray.Text = on ? "Unlimited (-1)" : $"{(_sliderJointStray?.Value ?? 40.0):F1} in";
                if (_isUpdatingJointEditor) return;
                var jov = GetOrCreateSelectedJointOverride();
                if (jov == null) return;
                jov.StrayRadiusInches = on ? -1.0f : (float)(_sliderJointStray?.Value ?? 40.0);
                ApplyChainParamsLiveToDriver();
                RefreshChainJointListUI();
            };
            jointEdBox.AddChild(_chkJointStrayUnlimited);

            (_sliderJointStray, _lblValJointStray) = AddExplainedSlider(
                jointEdBox,
                "Bone Stray Radius (in)",
                "Per-bone max drift from animated pose (disabled when Unlimited is checked).",
                1.5, 220.0, 1.0, "F1",
                (v) =>
                {
                    bool unlim = _chkJointStrayUnlimited?.ButtonPressed ?? false;
                    if (_lblValJointStray != null) _lblValJointStray.Text = unlim ? "Unlimited (-1)" : $"{v:F1} in";
                    if (_isUpdatingJointEditor || unlim) return;
                    var jov = GetOrCreateSelectedJointOverride();
                    if (jov == null) return;
                    jov.StrayRadiusInches = (float)v;
                    ApplyChainParamsLiveToDriver();
                    RefreshChainJointListUI();
                });

            advBody.AddChild(_boxJointOverrideEditor);

            var twinRow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            twinRow.AddThemeConstantOverride("h_separation", 6);
            twinRow.AddThemeConstantOverride("v_separation", 6);
            var btnCopyTwin = new Button { Text = "Copy to L/R Twin", CustomMinimumSize = new Vector2(124, 30), ThemeTypeVariation = "ToolbarButton" };
            btnCopyTwin.Pressed += CopySelectedChainToMirrorTwin;
            var btnApplyCat = new Button { Text = "Apply to Same Category", CustomMinimumSize = new Vector2(154, 30), ThemeTypeVariation = "ToolbarButton" };
            btnApplyCat.Pressed += ApplySelectedChainToCategory;
            twinRow.AddChild(btnCopyTwin);
            twinRow.AddChild(btnApplyCat);
            advBody.AddChild(twinRow);

            chainBody.AddChild(_boxChainInspector);

            // Collapsible Add-Synthetic-Chain Card (collapsed by default)
            var (synCard, synBody) = CreateInspectorCard("Custom / Synthetic Physics Chain");
            chainsPane.AddChild(synCard);

            _btnToggleAddSynthetic = new Button
            {
                Text = "▸ Add Synthetic Clothing Chain Tool",
                ThemeTypeVariation = "ToolbarButton",
                ToggleMode = true,
                ButtonPressed = false,
                Alignment = HorizontalAlignment.Left
            };
            synBody.AddChild(_btnToggleAddSynthetic);

            _boxAddSyntheticBody = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            _boxAddSyntheticBody.AddThemeConstantOverride("separation", 6);
            synBody.AddChild(_boxAddSyntheticBody);

            _btnToggleAddSynthetic.Toggled += (open) =>
            {
                _boxAddSyntheticBody.Visible = open;
                _btnToggleAddSynthetic.Text = open ? "▾ Hide Synthetic Clothing Chain Tool" : "▸ Add Synthetic Clothing Chain Tool";
            };

            var addRow1 = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            addRow1.AddThemeConstantOverride("separation", 6);
            _txtNewChainName = new LineEdit { PlaceholderText = "Chain name (e.g. cape_center)", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _optNewChainAnchor = new OptionButton { CustomMinimumSize = new Vector2(120, 0) };
            ConfigureOptionButton(_optNewChainAnchor, 120);
            foreach (string anchor in new[] { "spine_3", "pelvis", "head", "neck_0", "hand_L", "hand_R" })
                _optNewChainAnchor.AddItem(anchor);
            addRow1.AddChild(_txtNewChainName);
            addRow1.AddChild(_optNewChainAnchor);
            _boxAddSyntheticBody.AddChild(addRow1);

            var addRow2 = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            addRow2.AddThemeConstantOverride("separation", 6);
            addRow2.AddChild(new Label { Text = "Mesh:", CustomMinimumSize = new Vector2(42, 0) });
            _optNewChainMesh = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            ConfigureOptionButton(_optNewChainMesh);
            _optNewChainMesh.AddItem("All Active Submeshes", 0);
            addRow2.AddChild(_optNewChainMesh);
            _boxAddSyntheticBody.AddChild(addRow2);

            var addGrid = new GridContainer { Columns = 3, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            addGrid.AddThemeConstantOverride("h_separation", 6);
            _spinNewChainSegs = CreateCoordSpinBox(2, 10, 1, "Seg"); _spinNewChainSegs.Value = 4;
            _spinNewChainLen = CreateCoordSpinBox(0.1, 2.0, 0.05, "Len"); _spinNewChainLen.Value = 0.55;
            _spinNewChainRadius = CreateCoordSpinBox(0.05, 1.0, 0.02, "Rad"); _spinNewChainRadius.Value = 0.25;
            addGrid.AddChild(_spinNewChainSegs); addGrid.AddChild(_spinNewChainLen); addGrid.AddChild(_spinNewChainRadius);
            _boxAddSyntheticBody.AddChild(addGrid);

            var physGrid = new GridContainer { Columns = 3, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            physGrid.AddThemeConstantOverride("h_separation", 6);
            _sliderNewChainStiffRoot = new HSlider { MinValue = 0.05, MaxValue = 0.98, Step = 0.05, Value = 0.85, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _sliderNewChainStiffTip = new HSlider { MinValue = 0.05, MaxValue = 0.98, Step = 0.05, Value = 0.25, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _sliderNewChainDamping = new HSlider { MinValue = 0.05, MaxValue = 0.95, Step = 0.05, Value = 0.35, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            physGrid.AddChild(new Label { Text = "Root Stiff" }); physGrid.AddChild(new Label { Text = "Tip Stiff" }); physGrid.AddChild(new Label { Text = "Damping" });
            physGrid.AddChild(_sliderNewChainStiffRoot); physGrid.AddChild(_sliderNewChainStiffTip); physGrid.AddChild(_sliderNewChainDamping);
            _boxAddSyntheticBody.AddChild(physGrid);

            var btnAddChain = new Button
            {
                Text = "+ Add Clothing Chain to Rig",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(0, 32)
            };
            SetButtonIcon(btnAddChain, "res://assets/at-icons/sparkles.svg", 16, StatusTone.Success);
            btnAddChain.Pressed += OnAddSyntheticClothingChainPressed;
            _boxAddSyntheticBody.AddChild(btnAddChain);

            // =========================================================
            // SUB-VIEW 3: BODY COLLIDERS (DEDICATED WORKSPACE)
            // =========================================================
            var colPane = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            colPane.AddThemeConstantOverride("separation", 8);
            _boxSubViewColliders = colPane;
            vBox.AddChild(colPane);

            var (colCard, colBody) = CreateInspectorCard(
                "Body Collision Capsules",
                "Mesh-fitted capsules keep hair, skirts, and coats from clipping through the body when crouching or running — active in both live preview and Source 2 exports.");
            colPane.AddChild(colCard);

            _lblBodyCollidersSummary = new Label
            {
                Text = "Capsules auto-fit from your rigged mesh after the first rig pass.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            ImporterStyle.ApplyHintStyle(_lblBodyCollidersSummary);
            _secondaryLabels.Add(_lblBodyCollidersSummary);
            colBody.AddChild(_lblBodyCollidersSummary);

            var colActionRow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            colActionRow.AddThemeConstantOverride("h_separation", 6);
            colActionRow.AddThemeConstantOverride("v_separation", 6);

            var btnAutoFitCols = new Button
            {
                Text = "Auto-Fit from Mesh",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(142, 32),
                TooltipText = "Recompute every capsule radius & position cleanly from the rigged mesh silhouette (clears manual edits)."
            };
            SetButtonIcon(btnAutoFitCols, "res://assets/at-icons/sparkles.svg", 16, StatusTone.Success);
            btnAutoFitCols.Pressed += OnAutoFitCollidersPressed;
            colActionRow.AddChild(btnAutoFitCols);

            _btnShowCollidersInCard = new Button
            {
                Text = "Show in 3D Viewport",
                ThemeTypeVariation = "ToolbarButton",
                ToggleMode = true,
                ButtonPressed = _showColliders,
                CustomMinimumSize = new Vector2(148, 32),
                TooltipText = "Toggle 3D capsule wireframes on/off (synced with the top Colliders toolbar button)."
            };
            SetButtonIcon(_btnShowCollidersInCard, "res://assets/at-icons/eye.svg", 16);
            _btnShowCollidersInCard.Toggled += (on) =>
            {
                if (_btnToggleColliders != null && _btnToggleColliders.ButtonPressed != on)
                    _btnToggleColliders.ButtonPressed = on;
                else
                {
                    _showColliders = on;
                    if (_collidersDebugMesh != null) _collidersDebugMesh.Visible = on;
                    if (on) { _collidersDirty = true; RedrawColliderWireframe(); }
                }
            };
            colActionRow.AddChild(_btnShowCollidersInCard);

            var btnAddPropBox = new Button
            {
                Text = "+ Add Prop Box",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(126, 32),
                TooltipText = "Add an independent 3-axis ClothShapeBox collider (ideal for backpacks, shields, quivers, or large back props that a cape must drape over)."
            };
            SetButtonIcon(btnAddPropBox, "res://assets/at-icons/plus.svg", 15, StatusTone.Info);
            btnAddPropBox.Pressed += () => OnAddCustomColliderPressed(ColliderShapeKind.Box);
            colActionRow.AddChild(btnAddPropBox);

            var btnAddCustomCap = new Button
            {
                Text = "+ Add Capsule",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(118, 32),
                TooltipText = "Add an extra custom ClothShapeCapsule collider attached to any hero bone."
            };
            SetButtonIcon(btnAddCustomCap, "res://assets/at-icons/plus.svg", 15);
            btnAddCustomCap.Pressed += () => OnAddCustomColliderPressed(ColliderShapeKind.Capsule);
            colActionRow.AddChild(btnAddCustomCap);
            colBody.AddChild(colActionRow);

            var colListScroll = new ScrollContainer
            {
                CustomMinimumSize = new Vector2(0, 200),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };
            _boxBodyCollidersList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _boxBodyCollidersList.AddThemeConstantOverride("separation", 4);
            colListScroll.AddChild(_boxBodyCollidersList);
            colBody.AddChild(colListScroll);

            // Selected Collider Inspector Drawer
            _boxColliderInspector = new PanelContainer { ThemeTypeVariation = "CardPanel", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            var colInspBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            colInspBox.AddThemeConstantOverride("separation", 8);
            _boxColliderInspector.AddChild(colInspBox);

            _lblSelectedColliderTitle = new Label { Text = "Selected Capsule: --" };
            ImporterStyle.ApplyFontSize(_lblSelectedColliderTitle, ImporterStyle.FontBody);
            _accentLabels.Add(_lblSelectedColliderTitle);
            colInspBox.AddChild(_lblSelectedColliderTitle);

            _lblSelectedColliderBoneHint = new Label { Text = "Bone: --", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            ImporterStyle.ApplyHintStyle(_lblSelectedColliderBoneHint);
            _secondaryLabels.Add(_lblSelectedColliderBoneHint);
            colInspBox.AddChild(_lblSelectedColliderBoneHint);

            // Parent bone picker (for user-added custom prop boxes & capsules)
            _rowColParentBone = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            _rowColParentBone.AddThemeConstantOverride("separation", 6);
            _rowColParentBone.AddChild(new Label { Text = "Attach Bone:", CustomMinimumSize = new Vector2(88, 0) });
            _optColParentBone = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            ConfigureOptionButton(_optColParentBone);
            _optColParentBone.ItemSelected += idx =>
            {
                if (_isUpdatingColliderInspector || _selectedCollider == null) return;
                string bn = _optColParentBone.GetItemText((int)idx);
                if (!string.IsNullOrWhiteSpace(bn))
                {
                    _selectedCollider.BoneName = bn;
                    _selectedCollider.IsUserEdited = true;
                    ApplyBodyCollidersLiveToDriver();
                    RefreshBodyCollidersPanel();
                }
            };
            _rowColParentBone.AddChild(_optColParentBone);
            colInspBox.AddChild(_rowColParentBone);

            (_sliderColRadiusScale, _lblValColRadiusScale) = AddExplainedSlider(
                colInspBox,
                "Uniform Scale",
                "Uniformly scales this collider's radius or box dimensions.",
                0.50, 2.00, 0.02, "F2",
                (v) =>
                {
                    if (_lblValColRadiusScale != null) _lblValColRadiusScale.Text = $"{v:F2}x";
                    if (_isUpdatingColliderInspector || _selectedCollider == null) return;
                    _selectedCollider.RadiusScale = (float)v;
                    _selectedCollider.IsUserEdited = true;
                    ApplyBodyCollidersLiveToDriver();
                });

            (_sliderColOffsetX, _lblValColOffsetX) = AddExplainedSlider(
                colInspBox,
                "Offset X (Left / Right, m)",
                "Shifts the collider left (-) or right (+) in parent bone space.",
                -0.50, 0.50, 0.005, "F3",
                (v) =>
                {
                    if (_lblValColOffsetX != null) _lblValColOffsetX.Text = $"{v:+0.000;-0.000;0.000} m";
                    if (_isUpdatingColliderInspector || _selectedCollider == null) return;
                    var off = _selectedCollider.LocalOffsetMeters;
                    _selectedCollider.LocalOffsetMeters = new Vector3((float)v, off.Y, off.Z);
                    _selectedCollider.IsUserEdited = true;
                    ApplyBodyCollidersLiveToDriver();
                });

            (_sliderColOffsetY, _lblValColOffsetY) = AddExplainedSlider(
                colInspBox,
                "Offset Y (Up / Down, m)",
                "Shifts the collider up (+) or down (-) in parent bone space.",
                -0.50, 0.50, 0.005, "F3",
                (v) =>
                {
                    if (_lblValColOffsetY != null) _lblValColOffsetY.Text = $"{v:+0.000;-0.000;0.000} m";
                    if (_isUpdatingColliderInspector || _selectedCollider == null) return;
                    var off = _selectedCollider.LocalOffsetMeters;
                    _selectedCollider.LocalOffsetMeters = new Vector3(off.X, (float)v, off.Z);
                    _selectedCollider.IsUserEdited = true;
                    ApplyBodyCollidersLiveToDriver();
                });

            (_sliderColOffsetZ, _lblValColOffsetZ) = AddExplainedSlider(
                colInspBox,
                "Offset Z (Back / Forward, m)",
                "Shifts the collider backward (-) or forward (+) to hug backpacks, hoods, or back props.",
                -0.50, 0.50, 0.005, "F3",
                (v) =>
                {
                    if (_lblValColOffsetZ != null) _lblValColOffsetZ.Text = $"{v:+0.000;-0.000;0.000} m";
                    if (_isUpdatingColliderInspector || _selectedCollider == null) return;
                    var off = _selectedCollider.LocalOffsetMeters;
                    _selectedCollider.LocalOffsetMeters = new Vector3(off.X, off.Y, (float)v);
                    _selectedCollider.IsUserEdited = true;
                    ApplyBodyCollidersLiveToDriver();
                });

            // Capsule-only radius sliders
            _boxColCapsuleControls = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _boxColCapsuleControls.AddThemeConstantOverride("separation", 6);
            colInspBox.AddChild(_boxColCapsuleControls);

            (_sliderColRadius0, _lblValColRadius0) = AddExplainedSlider(
                _boxColCapsuleControls,
                "Base Radius 0 (m)",
                "Width of the lower/start end of the tapered capsule before Uniform Scale.",
                0.03, 0.35, 0.002, "F3",
                (v) =>
                {
                    if (_lblValColRadius0 != null) _lblValColRadius0.Text = $"{v:F3} m";
                    if (_isUpdatingColliderInspector || _selectedCollider == null) return;
                    _selectedCollider.Radius0Meters = (float)v;
                    _selectedCollider.IsUserEdited = true;
                    ApplyBodyCollidersLiveToDriver();
                });

            (_sliderColRadius1, _lblValColRadius1) = AddExplainedSlider(
                _boxColCapsuleControls,
                "Top Radius 1 (m)",
                "Width of the upper/tip end of the tapered capsule before Uniform Scale.",
                0.03, 0.35, 0.002, "F3",
                (v) =>
                {
                    if (_lblValColRadius1 != null) _lblValColRadius1.Text = $"{v:F3} m";
                    if (_isUpdatingColliderInspector || _selectedCollider == null) return;
                    _selectedCollider.Radius1Meters = (float)v;
                    _selectedCollider.IsUserEdited = true;
                    ApplyBodyCollidersLiveToDriver();
                });

            // Box-only 3-axis dimensions + rotation + planarize back barrier
            _boxColBoxControls = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            _boxColBoxControls.AddThemeConstantOverride("separation", 6);
            colInspBox.AddChild(_boxColBoxControls);

            (_sliderColBoxW, _lblValColBoxW) = AddExplainedSlider(
                _boxColBoxControls,
                "Box Width X (m)",
                "Full left-to-right width of the ClothShapeBox collider.",
                0.04, 0.90, 0.01, "F2",
                (v) =>
                {
                    if (_lblValColBoxW != null) _lblValColBoxW.Text = $"{v:F2} m";
                    if (_isUpdatingColliderInspector || _selectedCollider == null) return;
                    var h = _selectedCollider.BoxHalfExtentsMeters;
                    _selectedCollider.BoxHalfExtentsMeters = new Vector3((float)v * 0.5f, h.Y, h.Z);
                    _selectedCollider.IsUserEdited = true;
                    ApplyBodyCollidersLiveToDriver();
                });

            (_sliderColBoxH, _lblValColBoxH) = AddExplainedSlider(
                _boxColBoxControls,
                "Box Height Y (m)",
                "Full bottom-to-top height of the ClothShapeBox collider.",
                0.04, 0.90, 0.01, "F2",
                (v) =>
                {
                    if (_lblValColBoxH != null) _lblValColBoxH.Text = $"{v:F2} m";
                    if (_isUpdatingColliderInspector || _selectedCollider == null) return;
                    var h = _selectedCollider.BoxHalfExtentsMeters;
                    _selectedCollider.BoxHalfExtentsMeters = new Vector3(h.X, (float)v * 0.5f, h.Z);
                    _selectedCollider.IsUserEdited = true;
                    ApplyBodyCollidersLiveToDriver();
                });

            (_sliderColBoxD, _lblValColBoxD) = AddExplainedSlider(
                _boxColBoxControls,
                "Box Depth Z (m)",
                "Full front-to-back thickness of the ClothShapeBox collider.",
                0.04, 0.90, 0.01, "F2",
                (v) =>
                {
                    if (_lblValColBoxD != null) _lblValColBoxD.Text = $"{v:F2} m";
                    if (_isUpdatingColliderInspector || _selectedCollider == null) return;
                    var h = _selectedCollider.BoxHalfExtentsMeters;
                    _selectedCollider.BoxHalfExtentsMeters = new Vector3(h.X, h.Y, (float)v * 0.5f);
                    _selectedCollider.IsUserEdited = true;
                    ApplyBodyCollidersLiveToDriver();
                });

            (_sliderColRotX, _lblValColRotX) = AddExplainedSlider(
                _boxColBoxControls,
                "Tilt Pitch X (°)",
                "Tilts the box forward/backward to match a sloped backpack, shield, or quiver.",
                -90, 90, 1, "F0",
                (v) =>
                {
                    if (_lblValColRotX != null) _lblValColRotX.Text = $"{v:F0}°";
                    if (_isUpdatingColliderInspector || _selectedCollider == null) return;
                    var r = _selectedCollider.LocalRotationDeg;
                    _selectedCollider.LocalRotationDeg = new Vector3((float)v, r.Y, r.Z);
                    _selectedCollider.IsUserEdited = true;
                    ApplyBodyCollidersLiveToDriver();
                });

            (_sliderColRotY, _lblValColRotY) = AddExplainedSlider(
                _boxColBoxControls,
                "Turn Yaw Y (°)",
                "Rotates the box left/right around the vertical axis.",
                -90, 90, 1, "F0",
                (v) =>
                {
                    if (_lblValColRotY != null) _lblValColRotY.Text = $"{v:F0}°";
                    if (_isUpdatingColliderInspector || _selectedCollider == null) return;
                    var r = _selectedCollider.LocalRotationDeg;
                    _selectedCollider.LocalRotationDeg = new Vector3(r.X, (float)v, r.Z);
                    _selectedCollider.IsUserEdited = true;
                    ApplyBodyCollidersLiveToDriver();
                });

            (_sliderColRotZ, _lblValColRotZ) = AddExplainedSlider(
                _boxColBoxControls,
                "Roll Z (°)",
                "Rolls the box sideways (great for diagonal swords, staves, or quivers on the back).",
                -90, 90, 1, "F0",
                (v) =>
                {
                    if (_lblValColRotZ != null) _lblValColRotZ.Text = $"{v:F0}°";
                    if (_isUpdatingColliderInspector || _selectedCollider == null) return;
                    var r = _selectedCollider.LocalRotationDeg;
                    _selectedCollider.LocalRotationDeg = new Vector3(r.X, r.Y, (float)v);
                    _selectedCollider.IsUserEdited = true;
                    ApplyBodyCollidersLiveToDriver();
                });

            _chkColPlanarizeBack = new CheckBox
            {
                Text = "One-Sided Back Ramp (planarize = true)",
                TooltipText = "Pushes back-draping capes strictly behind this prop box so they slide smoothly over it instead of snapping forward into the chest."
            };
            _chkColPlanarizeBack.Toggled += on =>
            {
                if (_isUpdatingColliderInspector || _selectedCollider == null) return;
                _selectedCollider.PlanarizeBackOnly = on;
                _selectedCollider.IsUserEdited = true;
                ApplyBodyCollidersLiveToDriver();
            };
            _boxColBoxControls.AddChild(_chkColPlanarizeBack);

            var colBtnRow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            colBtnRow.AddThemeConstantOverride("h_separation", 6);
            colBtnRow.AddThemeConstantOverride("v_separation", 6);

            var btnMirrorCol = new Button
            {
                Text = "Mirror L <-> R",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(124, 30),
                TooltipText = "Copy size & offset symmetrically to the opposite left/right bone capsule."
            };
            btnMirrorCol.Pressed += OnMirrorSelectedColliderPressed;
            colBtnRow.AddChild(btnMirrorCol);

            var btnResetCol = new Button
            {
                Text = "Reset Capsule",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(124, 30),
                TooltipText = "Reset this capsule back to the auto-fitted mesh dimensions."
            };
            btnResetCol.Pressed += OnResetSelectedColliderPressed;
            colBtnRow.AddChild(btnResetCol);

            _btnDeleteCustomCol = new Button
            {
                Text = "Delete Collider",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(124, 30),
                Visible = false,
                TooltipText = "Remove this user-added custom prop collider."
            };
            _btnDeleteCustomCol.Pressed += OnDeleteSelectedCustomColliderPressed;
            colBtnRow.AddChild(_btnDeleteCustomCol);

            colInspBox.AddChild(colBtnRow);
            colBody.AddChild(_boxColliderInspector);

            // =========================================================
            // SUB-VIEW 2: DISABLE / PRUNE SPECIFIC BONES IN UI
            // =========================================================
            var disSubBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            disSubBox.AddThemeConstantOverride("separation", 8);
            _boxSubViewDisable = disSubBox;
            vBox.AddChild(disSubBox);

            var (pruneCard, pruneBody) = CreateInspectorCard("Pruning & Export Filter");
            disSubBox.AddChild(pruneCard);

            _chkKeepUnweightedBones = new CheckBox
            {
                Text = "Keep unweighted control/helper bones",
                ButtonPressed = false,
                TooltipText = "By default, only bones that carry skin weights (or have weighted descendants) are exported."
            };
            _chkKeepUnweightedBones.Toggled += (_) => MarkRemapStaleAndRefresh();
            pruneBody.AddChild(_chkKeepUnweightedBones);

            var (disCard, disBody) = CreateInspectorCard("Disable Specific Bones", "Search and disable any source bone here (collapses weights cleanly onto its parent). Also synced with 3D viewport right-click.");
            disSubBox.AddChild(disCard);

            _lblDisabledBonesEmpty = new Label { Text = "No bones disabled.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            ImporterStyle.ApplyHintStyle(_lblDisabledBonesEmpty);
            _secondaryLabels.Add(_lblDisabledBonesEmpty);
            disBody.AddChild(_lblDisabledBonesEmpty);

            _boxDisabledBonesList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _boxDisabledBonesList.AddThemeConstantOverride("separation", 4);
            disBody.AddChild(_boxDisabledBonesList);

            disBody.AddChild(new HSeparator());

            var disSearchRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            disSearchRow.AddThemeConstantOverride("separation", 6);
            _txtDisableBoneSearch = new LineEdit { PlaceholderText = "Search source bones (e.g. hair, skirt, ribbon)...", ClearButtonEnabled = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _txtDisableBoneSearch.TextChanged += (_) => RefreshSourceBoneDisableBrowser();
            disSearchRow.AddChild(_txtDisableBoneSearch);

            _optDisableBoneFilter = new OptionButton { CustomMinimumSize = new Vector2(115, 28) };
            ConfigureOptionButton(_optDisableBoneFilter, 115);
            _optDisableBoneFilter.AddItem("Weighted Only", 0);
            _optDisableBoneFilter.AddItem("Hair & Cloth", 1);
            _optDisableBoneFilter.AddItem("Disabled Only", 2);
            _optDisableBoneFilter.AddItem("All Bones", 3);
            _optDisableBoneFilter.Select(0);
            _optDisableBoneFilter.ItemSelected += (_) => RefreshSourceBoneDisableBrowser();
            disSearchRow.AddChild(_optDisableBoneFilter);
            disBody.AddChild(disSearchRow);

            var disScroll = new ScrollContainer
            {
                CustomMinimumSize = new Vector2(0, 260),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };
            _boxSourceBoneDisableList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _boxSourceBoneDisableList.AddThemeConstantOverride("separation", 3);
            disScroll.AddChild(_boxSourceBoneDisableList);
            disBody.AddChild(disScroll);

            return root;
        }

        private void SetBonesSubView(int viewIdx)
        {
            if (_btnSubTabRemap != null) _btnSubTabRemap.SetPressedNoSignal(viewIdx == 0);
            if (_btnSubTabWeights != null) _btnSubTabWeights.SetPressedNoSignal(viewIdx == 1);
            if (_btnSubTabPhysics != null) _btnSubTabPhysics.SetPressedNoSignal(viewIdx == 2);
            if (_btnSubTabColliders != null) _btnSubTabColliders.SetPressedNoSignal(viewIdx == 3);
            if (_btnSubTabDisable != null) _btnSubTabDisable.SetPressedNoSignal(viewIdx == 4);

            if (_boxSubViewRemap != null) _boxSubViewRemap.Visible = viewIdx == 0;
            if (_boxSubViewWeights != null) _boxSubViewWeights.Visible = viewIdx == 1;
            if (_boxSubViewPhysics != null) _boxSubViewPhysics.Visible = viewIdx == 2;
            if (_boxSubViewColliders != null) _boxSubViewColliders.Visible = viewIdx == 3;
            if (_boxSubViewDisable != null) _boxSubViewDisable.Visible = viewIdx == 4;

            if (_wasInCollidersSubView && viewIdx != 3)
            {
                _wasInCollidersSubView = false;
                if (_showColliders)
                {
                    if (_btnToggleColliders != null) _btnToggleColliders.ButtonPressed = false;
                    else
                    {
                        _showColliders = false;
                        if (_collidersDebugMesh != null) _collidersDebugMesh.Visible = false;
                    }
                    if (_btnShowCollidersInCard != null) _btnShowCollidersInCard.SetPressedNoSignal(false);
                }
            }

            if (viewIdx == 1)
            {
                HostWeightPaintSectionIn(_boxTab3WeightPaintHost);
                RefreshChainPaintQuickBones();
                PopulatePaintBoneDropdown();
            }
            else
            {
                if (_btnWeightPaintMode != null && _btnWeightPaintMode.ButtonPressed && _rightTabs != null && _rightTabs.CurrentTab == 2)
                    _btnWeightPaintMode.ButtonPressed = false;
            }

            if (viewIdx == 2)
            {
                RefreshClothingChainsPanel();
            }
            else if (viewIdx == 3)
            {
                _wasInCollidersSubView = true;
                if (_btnShowCollidersInCard != null) _btnShowCollidersInCard.SetPressedNoSignal(_showColliders);
                if (!_showColliders && _bodyCollisionCapsules.Count > 0 && _btnToggleColliders != null)
                {
                    _btnToggleColliders.ButtonPressed = true;
                }
                RefreshBodyCollidersPanel();
            }
            else if (viewIdx == 4)
            {
                RefreshSourceBoneDisableBrowser();
            }
        }

        private void MarkRemapStaleAndRefresh()
        {
            if (_lblRemapSyncBadge != null)
            {
                _lblRemapSyncBadge.Text = ImporterMemoryGuard.LightMode ? "Stale (Click Update)" : "Updating...";
                _lblRemapSyncBadge.AddThemeColorOverride("font_color", ThemeManager.GetColor(nameof(ThemePaletteData.SemanticWarning)));
            }
            UpdateBoneMappingValidationBadges();
            RefreshBoneTabDiffSummary();
            if (!ImporterMemoryGuard.LightMode) TriggerRealtimeRigUpdate();
        }

        private void MarkRemapAppliedBadge()
        {
            if (_lblRemapSyncBadge != null)
            {
                _lblRemapSyncBadge.Text = "Applied";
                _lblRemapSyncBadge.AddThemeColorOverride("font_color", ThemeManager.GetColor(nameof(ThemePaletteData.SemanticSuccess)));
            }
            RefreshBoneTabDiffSummary();
        }

        private void RefreshBoneTabDiffSummary()
        {
            if (_lblRemapDiffSummary == null) return;
            int overridden = 0;
            int unmappedCore = 0;
            foreach (var row in _boneRowsByCanonical.Values)
            {
                _userBoneRemap.TryGetValue(row.CanonicalName, out string cur);
                _autoMappedSnapshot.TryGetValue(row.CanonicalName, out string auto);
                if (!string.IsNullOrEmpty(cur) && !string.Equals(cur, auto ?? "", StringComparison.OrdinalIgnoreCase))
                    overridden++;
                if (string.IsNullOrEmpty(cur) && row.IsRequiredCore)
                    unmappedCore++;
            }
            _lblRemapDiffSummary.Text = $"Diff: {overridden} override(s) | {unmappedCore} unmapped core | {_disabledSourceBones.Count} disabled";
            if (_btnSubTabPhysics != null)
            {
                int physActive = _boneChains.Count(c => c.Policy == ChainPolicy.PreservePhysicsChain);
                _btnSubTabPhysics.Text = $"Physics ({physActive}/{_boneChains.Count})";
            }
            if (_btnSubTabDisable != null)
            {
                _btnSubTabDisable.Text = $"Prune ({_disabledSourceBones.Count})";
            }
        }

        private IEnumerable<BoneChainDef> EnumerateFilteredChains()
        {
            string q = _txtChainSearch?.Text?.Trim() ?? "";
            int cat = _optChainCategoryFilter?.Selected ?? 0;
            foreach (var ch in _boneChains)
            {
                if (ch == null) continue;
                string hay = (ch.Name + " " + ch.RootSourceBone + " " + ch.Category).ToLowerInvariant();
                if (!string.IsNullOrEmpty(q) && !hay.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                bool isHair = hay.Contains("hair") || hay.Contains("bang") || hay.Contains("tail");
                bool isCloth = ch.Category == "Clothing & Hair" && !isHair;
                bool isAcc = ch.Category == "Accessory" || (!isHair && !isCloth);
                if (cat == 1 && !isHair) continue;
                if (cat == 2 && !isCloth) continue;
                if (cat == 3 && !isAcc) continue;
                yield return ch;
            }
        }

        private void ApplyPolicyToFilteredChains(ChainPolicy policy)
        {
            foreach (var ch in EnumerateFilteredChains())
                ch.Policy = policy;
        }

        private void RefreshClothingChainsPanel()
        {
            if (_boxClothingChainsList == null) return;
            foreach (Node ch in _boxClothingChainsList.GetChildren()) ch.QueueFree();

            if (_optNewChainMesh != null)
            {
                _optNewChainMesh.Clear();
                _optNewChainMesh.AddItem("All Active Submeshes", 0);
                foreach (var chk in _importedSubmeshCheckboxes)
                {
                    if (chk.HasMeta("SubmeshName"))
                        _optNewChainMesh.AddItem(chk.GetMeta("SubmeshName").AsString());
                }
            }

            if (_btnSubTabPhysics != null)
            {
                int physCount = _boneChains.Count(c => c.Policy == ChainPolicy.PreservePhysicsChain);
                _btnSubTabPhysics.Text = $"Physics ({physCount}/{_boneChains.Count})";
            }

            if (_boneChains.Count == 0)
            {
                if (_lblClothingChainSummary != null)
                    _lblClothingChainSummary.Text = "No extra clothing, hair, or accessory chains detected.";
                if (_boxChainInspector != null) _boxChainInspector.Visible = false;
                return;
            }

            bool masterOn = _chkPhysicsMasterTab3?.ButtonPressed ?? true;
            int preservedCount = _boneChains.Count(c => c.Policy == ChainPolicy.PreservePhysicsChain);
            if (_lblClothingChainSummary != null)
            {
                _lblClothingChainSummary.Text = masterOn
                    ? $"{_boneChains.Count} chain(s) detected ({preservedCount} with physics active)."
                    : $"Global physics OFF — all {_boneChains.Count} chains locked rigid.";
            }

            var filtered = EnumerateFilteredChains().ToList();
            if (_selectedChain != null && !_boneChains.Contains(_selectedChain)) _selectedChain = null;
            if (_selectedChain == null && filtered.Count > 0) _selectedChain = filtered[0];

            // Build 2-level collapsible groups sorted sensibly (Head/Hair, Chest/Upper, Pelvis/Cloth, Other)
            static (string Key, string Label, int SortOrder) ClassifyPhysicsTreeGroup(BoneChainDef ch)
            {
                string anchor = (ch?.AnchorHeroBone ?? "").ToLowerInvariant();
                string nameLow = (ch?.Name ?? ch?.RootSourceBone ?? "").ToLowerInvariant();
                bool isHair = nameLow.Contains("hair") || nameLow.Contains("bang") || nameLow.Contains("braid") || nameLow.Contains("ponytail") || nameLow.Contains("strand");
                if (anchor.Contains("head") || anchor.Contains("neck") || isHair || nameLow.Contains("ear") || nameLow.Contains("hat") || nameLow.Contains("face"))
                {
                    return isHair
                        ? ("head::hair", "Head → Hair Chains", 0)
                        : ("head::acc", "Head → Ears, Face & Headgear", 1);
                }
                if (anchor.Contains("spine_2") || anchor.Contains("spine_3") || anchor.Contains("chest") ||
                    nameLow.Contains("breast") || nameLow.Contains("boob") || nameLow.Contains("hoodie") ||
                    nameLow.Contains("ribbon") || nameLow.Contains("scarf") || nameLow.Contains("cape") || nameLow.Contains("cloak"))
                {
                    return ("chest::cloth", "Chest → Hoodie, Cape, Breast & Ribbons", 2);
                }
                if (anchor.Contains("pelvis") || anchor.Contains("spine_0") || anchor.Contains("spine_1") ||
                    nameLow.Contains("skirt") || nameLow.Contains("coat") || nameLow.Contains("butt") || nameLow.Contains("tail"))
                {
                    return ("pelvis::cloth", "Pelvis → Skirt, Coat & Tails", 3);
                }
                if (anchor.Contains("arm") || anchor.Contains("hand") || anchor.Contains("clavicle"))
                {
                    return ("limbs::upper", "Arms → Sleeves & Bracers", 4);
                }
                if (anchor.Contains("leg") || anchor.Contains("ankle") || anchor.Contains("thigh"))
                {
                    return ("limbs::lower", "Legs → Holsters & Straps", 5);
                }
                string cleanAnchor = string.IsNullOrEmpty(ch?.AnchorHeroBone) ? "Root" : ch.AnchorHeroBone.ToUpperInvariant();
                return ($"other::{cleanAnchor}", $"{cleanAnchor} → Accessories", 6);
            }

            var grouped = filtered
                .Select(c => (Chain: c, Group: ClassifyPhysicsTreeGroup(c)))
                .GroupBy(x => x.Group.Key, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.First().Group.SortOrder)
                .ThenBy(g => g.First().Group.Label, StringComparer.OrdinalIgnoreCase);

            foreach (var grp in grouped)
            {
                string grpKey = grp.Key;
                string grpLabel = grp.First().Group.Label;
                var grpChains = grp.Select(x => x.Chain)
                    .OrderByDescending(c => IsRigidHubChain(c))
                    .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var toggleableChains = grpChains.Where(c => !IsRigidHubChain(c)).ToList();
                if (toggleableChains.Count == 0) toggleableChains = grpChains;
                int grpPhysCount = toggleableChains.Count(c => c.Policy == ChainPolicy.PreservePhysicsChain);
                bool grpAllOn = toggleableChains.Count > 0 && grpPhysCount == toggleableChains.Count;
                bool isCollapsed = _collapsedPhysicsGroups.Contains(grpKey);

                var grpPanel = new PanelContainer
                {
                    ThemeTypeVariation = "SubPanelContainer",
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };
                var grpVBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                grpVBox.AddThemeConstantOverride("separation", 3);
                grpPanel.AddChild(grpVBox);

                var hdrRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                hdrRow.AddThemeConstantOverride("separation", 6);

                var btnChevron = new Button
                {
                    Text = isCollapsed ? "▸" : "▾",
                    ThemeTypeVariation = "ToolbarButton",
                    CustomMinimumSize = new Vector2(24, 26),
                    TooltipText = isCollapsed ? "Expand chain group" : "Collapse chain group"
                };
                string capturedKey = grpKey;
                btnChevron.Pressed += () =>
                {
                    if (_collapsedPhysicsGroups.Contains(capturedKey))
                        _collapsedPhysicsGroups.Remove(capturedKey);
                    else
                        _collapsedPhysicsGroups.Add(capturedKey);
                    RefreshClothingChainsPanel();
                };
                hdrRow.AddChild(btnChevron);

                var chkGroupPhys = new CheckBox
                {
                    ButtonPressed = grpAllOn,
                    TooltipText = $"Toggle Jiggle Physics for all chains in '{grpLabel}'"
                };
                chkGroupPhys.Toggled += (on) =>
                {
                    var targetPolicy = on ? ChainPolicy.PreservePhysicsChain : ChainPolicy.CollapseToNearestCore;
                    foreach (var c in toggleableChains)
                        c.Policy = targetPolicy;
                    MarkRemapStaleAndRefresh();
                    RefreshClothingChainsPanel();
                };
                hdrRow.AddChild(chkGroupPhys);

                var btnGroupHdr = new Button
                {
                    Text = $"{grpLabel}   ({grpPhysCount}/{toggleableChains.Count})",
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    ClipText = true,
                    Alignment = HorizontalAlignment.Left,
                    TooltipText = "Click to expand or collapse this group."
                };
                btnGroupHdr.AddThemeColorOverride("font_color", ThemeManager.GetColor(nameof(ThemePaletteData.AccentPrimary)));
                btnGroupHdr.Pressed += () =>
                {
                    if (_collapsedPhysicsGroups.Contains(capturedKey))
                        _collapsedPhysicsGroups.Remove(capturedKey);
                    else
                        _collapsedPhysicsGroups.Add(capturedKey);
                    RefreshClothingChainsPanel();
                };
                hdrRow.AddChild(btnGroupHdr);

                var btnGroupPreset = new Button
                {
                    Text = "Preset ▾",
                    ThemeTypeVariation = "ToolbarButton",
                    CustomMinimumSize = new Vector2(72, 26),
                    TooltipText = $"Pick a Valve-backed preset for all {toggleableChains.Count} chain(s) in '{grpLabel}' at once."
                };
                ImporterStyle.ApplyFontSize(btnGroupPreset, ImporterStyle.FontHint);
                var capturedGroupChains = toggleableChains;
                btnGroupPreset.Pressed += () => OpenPhysicsPresetPicker(capturedGroupChains, btnGroupPreset);
                hdrRow.AddChild(btnGroupPreset);
                grpVBox.AddChild(hdrRow);

                if (!isCollapsed)
                {
                    var childrenIndent = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                    childrenIndent.AddThemeConstantOverride("margin_left", 20);
                    childrenIndent.AddThemeConstantOverride("margin_top", 2);
                    childrenIndent.AddThemeConstantOverride("margin_bottom", 4);
                    var childrenVBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                    childrenVBox.AddThemeConstantOverride("separation", 3);
                    childrenIndent.AddChild(childrenVBox);
                    grpVBox.AddChild(childrenIndent);

                    foreach (var chain in grpChains)
                    {
                        var rowBtn = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                        rowBtn.AddThemeConstantOverride("separation", 6);

                        bool isHub = IsRigidHubChain(chain);
                        bool isPhys = chain.Policy == ChainPolicy.PreservePhysicsChain;
                        var chkPhys = new CheckBox
                        {
                            ButtonPressed = isPhys,
                            Disabled = isHub,
                            TooltipText = isHub
                                ? "Weighted hub bone — remains rigid so branching child tails pivot cleanly."
                                : "Enable jiggle physics for this chain (uncheck to lock rigid to anchor)."
                        };
                        chkPhys.Toggled += (on) =>
                        {
                            chain.Policy = on ? ChainPolicy.PreservePhysicsChain : ChainPolicy.CollapseToNearestCore;
                            _selectedChain = chain;
                            _selectedViewportBoneName = chain.RootSourceBone;
                            MarkRemapStaleAndRefresh();
                            RefreshClothingChainsPanel();
                            UpdateSelectedBoneBadge();
                            if (_showBones) RedrawBoneWireframe();
                        };
                        rowBtn.AddChild(chkPhys);

                        string simTag = chain.Simulation switch
                        {
                            SimulationKind.ProxyCloth => "Proxy",
                            SimulationKind.Cloth => "Cloth",
                            _ => "Jiggle"
                        };
                        string badge = isHub
                            ? "Rigid Hub"
                            : chain.Policy switch
                            {
                                ChainPolicy.PreservePhysicsChain => masterOn ? simTag : "Paused",
                                _ => "Static"
                            };
                        int bCount = chain.SourceBoneNames?.Count ?? 0;
                        string prefix = isHub ? "◈ " : "└ ";
                        var btnSelect = new Button
                        {
                            Text = $"{prefix}{chain.Name}  [{badge} · {bCount}b]",
                            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                            ClipText = true,
                            Alignment = HorizontalAlignment.Left,
                            ToggleMode = true,
                            ButtonPressed = ReferenceEquals(chain, _selectedChain),
                            TooltipText = $"Root: {chain.RootSourceBone} | Anchor: {chain.AnchorHeroBone} | Bones: {bCount} | Category: {chain.Category}\nClick to inspect spring settings and highlight in 3D viewport."
                        };
                        btnSelect.Pressed += () =>
                        {
                            _selectedChain = chain;
                            string firstBone = chain.AppendedHeroBoneNames?.FirstOrDefault() ?? chain.RootSourceBone;
                            _selectedViewportBoneName = firstBone;
                            RefreshClothingChainsPanel();
                            UpdateSelectedBoneBadge();
                            if (_showBones) RedrawBoneWireframe();
                            SyncActiveWeightPaintToBone(firstBone);
                        };
                        rowBtn.AddChild(btnSelect);
                        childrenVBox.AddChild(rowBtn);
                    }
                }

                _boxClothingChainsList.AddChild(grpPanel);
            }

            SyncSharedChainInspector();
        }

        private void SyncSharedChainInspector()
        {
            if (_boxChainInspector == null) return;
            if (_selectedChain == null)
            {
                _boxChainInspector.Visible = false;
                return;
            }
            _boxChainInspector.Visible = true;
            _isUpdatingChainInspector = true;
            try
            {
                _lblSelectedChainTitle.Text = $"Selected: {_selectedChain.Name} ({_selectedChain.SourceBoneNames.Count} bones)";
                SyncSelectedPresetCardUI();
                _sliderMacroLooseness?.SetValueNoSignal(_selectedChain.MacroLooseness);
                if (_lblValMacroLooseness != null) _lblValMacroLooseness.Text = $"{_selectedChain.MacroLooseness * 100.0f:F0}%";
                _sliderMacroGravity?.SetValueNoSignal(_selectedChain.Gravity);
                if (_lblValMacroGravity != null) _lblValMacroGravity.Text = $"{_selectedChain.Gravity:F2}×";
                _sliderMacroDamping?.SetValueNoSignal(_selectedChain.Damping);
                if (_lblValMacroDamping != null) _lblValMacroDamping.Text = $"{_selectedChain.Damping * 100.0f:F0}%";
                _sliderMacroColMargin?.SetValueNoSignal(_selectedChain.MacroCollisionScale);
                if (_lblValMacroColMargin != null) _lblValMacroColMargin.Text = $"{_selectedChain.MacroCollisionScale:F2}×";

                int polTargetId = _selectedChain.Policy == ChainPolicy.PreservePhysicsChain
                    ? (int)ChainPolicy.PreservePhysicsChain
                    : (int)ChainPolicy.CollapseToNearestCore;
                for (int i = 0; i < _optSelChainPolicy.ItemCount; i++)
                {
                    if (_optSelChainPolicy.GetItemId(i) == polTargetId)
                    {
                        _optSelChainPolicy.Select(i);
                        break;
                    }
                }
                for (int i = 0; i < _optSelChainSim.ItemCount; i++)
                {
                    if (_optSelChainSim.GetItemId(i) == (int)_selectedChain.Simulation)
                    {
                        _optSelChainSim.Select(i);
                        break;
                    }
                }
                SyncPhysicsTypeSegmentedUI(_selectedChain.Simulation);
                RefreshChainPaintQuickBones();
                _optSelChainAnchor.Clear();
                var anchors = GetHeroSocketBoneNames();
                int selA = 0;
                for (int a = 0; a < anchors.Count; a++)
                {
                    _optSelChainAnchor.AddItem(anchors[a], a);
                    if (string.Equals(anchors[a], _selectedChain.AnchorHeroBone, StringComparison.OrdinalIgnoreCase)) selA = a;
                }
                _optSelChainAnchor.Select(selA);

                _sliderSelStiffRoot.SetValueNoSignal(_selectedChain.StiffnessRoot);
                _sliderSelStiffTip.SetValueNoSignal(_selectedChain.StiffnessTip);
                _sliderSelTaperPower?.SetValueNoSignal(_selectedChain.GoalTaperPower);
                if (_lblValTaperPower != null) _lblValTaperPower.Text = $"{_selectedChain.GoalTaperPower:F2}";
                _sliderSelDamping.SetValueNoSignal(_selectedChain.Damping);
                _sliderSelGravity.SetValueNoSignal(_selectedChain.Gravity);
                _sliderSelColRadius.SetValueNoSignal(_selectedChain.CollisionRadiusTipInches);
                bool strayUnlim = _selectedChain.StrayRadiusInches < 0f;
                _chkSelStrayUnlimited?.SetPressedNoSignal(strayUnlim);
                if (_sliderSelStrayRadius != null)
                {
                    _sliderSelStrayRadius.Editable = !strayUnlim;
                    _sliderSelStrayRadius.SetValueNoSignal(strayUnlim ? 40.0 : _selectedChain.StrayRadiusInches);
                }
                float rfDisp = PhysicsParamResolver.ResolveRootGoalForce(_selectedChain.StiffnessRoot);
                float tfDisp = PhysicsParamResolver.ResolveTipGoalForce(_selectedChain.StiffnessTip, rfDisp);
                float pdDisp = PhysicsParamResolver.ResolveCompiledPointDamping(_selectedChain.Damping);
                if (_lblValStiffRoot != null) _lblValStiffRoot.Text = $"{_selectedChain.StiffnessRoot:F2} (gF {rfDisp:F2})";
                if (_lblValStiffTip != null) _lblValStiffTip.Text = $"{_selectedChain.StiffnessTip:F2} (gF {tfDisp:F3})";
                if (_lblValDamping != null) _lblValDamping.Text = $"{_selectedChain.Damping:F2} (pDamp {pdDisp:F2})";
                if (_lblValGravity != null) _lblValGravity.Text = $"{_selectedChain.Gravity:F2} ({_selectedChain.Gravity * 360f:F0})";
                if (_lblValColRadius != null) _lblValColRadius.Text = $"{_selectedChain.CollisionRadiusTipInches:F1} in";
                if (_lblValStrayRadius != null) _lblValStrayRadius.Text = strayUnlim ? "Unlimited (-1)" : $"{_selectedChain.StrayRadiusInches:F1} in";
                _chkSelPlanarizeBack?.SetPressedNoSignal(_selectedChain.PlanarizeBackBarrier);
                _chkSelOpenSheet?.SetPressedNoSignal(_selectedChain.OpenSheet);
                _chkSelAnchorFreeRotate?.SetPressedNoSignal(_selectedChain.AnchorFreeRotate);
                _sliderSelFriction?.SetValueNoSignal(_selectedChain.Friction);
                if (_lblValFriction != null) _lblValFriction.Text = $"{_selectedChain.Friction:F2}";
                _sliderSelAngleLimit?.SetValueNoSignal(_selectedChain.AngleLimitDeg);
                if (_lblValAngleLimit != null) _lblValAngleLimit.Text = $"{_selectedChain.AngleLimitDeg:F0}°";
                _sliderSelTwistRelax?.SetValueNoSignal(_selectedChain.TwistRelax);
                if (_lblValTwistRelax != null) _lblValTwistRelax.Text = $"{_selectedChain.TwistRelax:F2}";

                bool isClothChain = _selectedChain.Simulation == SimulationKind.Cloth;
                if (_boxClothExtrudeSection != null) _boxClothExtrudeSection.Visible = isClothChain;
                _optSelExtrudeAxis?.Select(Math.Clamp(_selectedChain.ExtrudeForwardAxis, 0, 5) % 3);
                _sliderSelExtrudeTwist?.SetValueNoSignal(_selectedChain.ExtrudeTwistDeg);
                if (_lblValExtrudeTwist != null) _lblValExtrudeTwist.Text = $"{_selectedChain.ExtrudeTwistDeg:F0}°";
                _sliderSelExtrudeRadius?.SetValueNoSignal(_selectedChain.ExtrudeRadiusInches);
                if (_lblValExtrudeRadius != null) _lblValExtrudeRadius.Text = $"{_selectedChain.ExtrudeRadiusInches:F1} in";

                _selectedChainJointIndex = -1;
                RefreshChainJointListUI();
            }
            finally
            {
                _isUpdatingChainInspector = false;
            }
        }

        private List<string> GetActiveChainBoneNames(BoneChainDef ch)
        {
            if (ch == null) return new List<string>();
            if (ch.SourceBoneNames != null && ch.SourceBoneNames.Count > 0) return ch.SourceBoneNames;
            return ch.AppendedHeroBoneNames ?? new List<string>();
        }

        private BoneJointOverride GetOrCreateSelectedJointOverride()
        {
            if (_selectedChain == null) return null;
            var bones = GetActiveChainBoneNames(_selectedChain);
            if (_selectedChainJointIndex < 0 || _selectedChainJointIndex >= bones.Count) return null;
            return _selectedChain.GetOrCreateJointOverride(bones[_selectedChainJointIndex]);
        }

        private void RefreshChainJointListUI()
        {
            if (_boxChainJointList == null) return;
            foreach (Node ch in _boxChainJointList.GetChildren()) ch.QueueFree();

            if (_selectedChain == null)
            {
                if (_boxJointOverrideEditor != null) _boxJointOverrideEditor.Visible = false;
                return;
            }

            var bones = GetActiveChainBoneNames(_selectedChain);
            if (bones.Count == 0)
            {
                if (_boxJointOverrideEditor != null) _boxJointOverrideEditor.Visible = false;
                return;
            }

            if (_selectedChainJointIndex >= bones.Count) _selectedChainJointIndex = -1;

            for (int i = 0; i < bones.Count; i++)
            {
                int idx = i;
                string bName = bones[i];
                string appName = (_selectedChain.AppendedHeroBoneNames != null && i < _selectedChain.AppendedHeroBoneNames.Count)
                    ? _selectedChain.AppendedHeroBoneNames[i]
                    : bName;
                var ov = _selectedChain.FindJointOverride(bName, appName);
                bool hasOv = ov != null && ov.HasAnyOverride;
                bool isPinned = ov?.Simulate.HasValue == true && !ov.Simulate.Value;
                string roleTag = i == 0 ? "Root" : $"Child {i}";
                string statusTag = isPinned ? " [Pinned]" : (hasOv ? " ★ Custom" : "");
                string indent = i == 0 ? "" : "└ ";

                var btn = new Button
                {
                    Text = $"{indent}{bName}  ({roleTag}){statusTag}",
                    Alignment = HorizontalAlignment.Left,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    CustomMinimumSize = new Vector2(0, 28),
                    ThemeTypeVariation = (idx == _selectedChainJointIndex) ? "PrimaryActionButton" : "ToolbarButton"
                };
                ImporterStyle.ApplyFontSize(btn, ImporterStyle.FontHint);
                btn.Pressed += () => SelectChainJoint(idx);
                _boxChainJointList.AddChild(btn);
            }

            UpdateSelectedJointEditorUI();
        }

        private void SelectChainJoint(int jointIdx)
        {
            if (_selectedChain == null) return;
            var bones = GetActiveChainBoneNames(_selectedChain);
            if (jointIdx < 0 || jointIdx >= bones.Count) return;

            _selectedChainJointIndex = jointIdx;
            string highlightBone = (_selectedChain.AppendedHeroBoneNames != null && jointIdx < _selectedChain.AppendedHeroBoneNames.Count)
                ? _selectedChain.AppendedHeroBoneNames[jointIdx]
                : bones[jointIdx];
            _selectedViewportBoneName = highlightBone;
            UpdateSelectedBoneBadge();
            RedrawBoneWireframeColorCoded();
            RefreshChainJointListUI();
            SyncActiveWeightPaintToBone(highlightBone);
        }

        private void SyncActiveWeightPaintToBone(string boneName)
        {
            if (!IsWeightPaintActive() || string.IsNullOrWhiteSpace(boneName) || _optPaintBone == null) return;
            PopulatePaintBoneDropdown();
            for (int i = 0; i < _optPaintBone.ItemCount; i++)
            {
                if (_optPaintBone.GetItemText(i).Equals(boneName, StringComparison.OrdinalIgnoreCase))
                {
                    _optPaintBone.Select(i);
                    RefreshWeightPaintHeatmap();
                    return;
                }
            }
        }

        private void UpdateSelectedJointEditorUI()
        {
            if (_boxJointOverrideEditor == null) return;
            if (_selectedChain == null)
            {
                _boxJointOverrideEditor.Visible = false;
                return;
            }

            var bones = GetActiveChainBoneNames(_selectedChain);
            if (_selectedChainJointIndex < 0 || _selectedChainJointIndex >= bones.Count)
            {
                _boxJointOverrideEditor.Visible = false;
                return;
            }

            _boxJointOverrideEditor.Visible = true;
            _isUpdatingJointEditor = true;
            try
            {
                int k = _selectedChainJointIndex;
                string bName = bones[k];
                string appName = (_selectedChain.AppendedHeroBoneNames != null && k < _selectedChain.AppendedHeroBoneNames.Count)
                    ? _selectedChain.AppendedHeroBoneNames[k]
                    : bName;
                var ov = _selectedChain.FindJointOverride(bName, appName);

                float t = bones.Count > 1 ? k / (float)(bones.Count - 1) : 0f;
                float taper = MathF.Pow(1.0f - t, 1.85f);
                float defRootGoal = Mathf.Clamp(0.55f + 0.30f * _selectedChain.StiffnessRoot, 0.30f, 0.85f);
                float defTipGoal = Mathf.Clamp(_selectedChain.StiffnessTip * 0.28f, 0.0f, defRootGoal);
                float defGoal = Mathf.Lerp(defTipGoal, defRootGoal, taper);
                float defDamp = Mathf.Clamp(_selectedChain.Damping * 0.12f, 0.0f, 0.18f) * (1.0f - t * 0.88f);
                float defCol = Mathf.Lerp(Mathf.Min(1.6f, _selectedChain.CollisionRadiusTipInches * 0.72f), _selectedChain.CollisionRadiusTipInches, t);
                float defStray = _selectedChain.StrayRadiusInches;

                bool sim = ov?.Simulate ?? true;
                float goal = ov?.GoalStrength ?? defGoal;
                float damp = ov?.GoalDamping ?? defDamp;
                float col = ov?.CollisionRadiusInches ?? defCol;
                float stray = ov?.StrayRadiusInches ?? defStray;
                bool strayUnlim = stray < 0f;

                if (_lblSelectedJointTitle != null)
                    _lblSelectedJointTitle.Text = $"Bone: {bName} ({(k == 0 ? "Root" : $"Child {k}")})";
                _chkJointSimulate?.SetPressedNoSignal(sim);
                _sliderJointGoal?.SetValueNoSignal(goal);
                if (_lblValJointGoal != null) _lblValJointGoal.Text = $"{goal:F2}";
                _sliderJointDamp?.SetValueNoSignal(damp);
                if (_lblValJointDamp != null) _lblValJointDamp.Text = $"{damp:F2}";
                _sliderJointColRad?.SetValueNoSignal(col);
                if (_lblValJointColRad != null) _lblValJointColRad.Text = $"{col:F1} in";
                _chkJointStrayUnlimited?.SetPressedNoSignal(strayUnlim);
                if (_sliderJointStray != null)
                {
                    _sliderJointStray.Editable = !strayUnlim;
                    _sliderJointStray.SetValueNoSignal(strayUnlim ? 40.0 : stray);
                }
                if (_lblValJointStray != null) _lblValJointStray.Text = strayUnlim ? "Unlimited (-1)" : $"{stray:F1} in";
            }
            finally
            {
                _isUpdatingJointEditor = false;
            }
        }

        private void ResetSelectedChainJointOverride()
        {
            if (_selectedChain == null || _selectedChain.JointOverrides == null) return;
            var bones = GetActiveChainBoneNames(_selectedChain);
            if (_selectedChainJointIndex < 0 || _selectedChainJointIndex >= bones.Count) return;
            string bName = bones[_selectedChainJointIndex];
            _selectedChain.JointOverrides.RemoveAll(j => j != null && string.Equals(j.BoneName, bName, StringComparison.OrdinalIgnoreCase));
            ApplyChainParamsLiveToDriver();
            RefreshChainJointListUI();
        }

        private void SetPhysicsStressTestPose(float headPitchDeg, float crouchLeanDeg, bool walkShake)
        {
            _stressHeadPitchDeg = headPitchDeg;
            _stressCrouchLeanDeg = crouchLeanDeg;
            _stressWalkBackShake = walkShake;
            _sliderStressHeadPitch?.SetValueNoSignal(headPitchDeg);
            if (_lblValStressHeadPitch != null) _lblValStressHeadPitch.Text = $"{headPitchDeg:+0;-0;0}°";
            _sliderStressCrouchLean?.SetValueNoSignal(crouchLeanDeg);
            if (_lblValStressCrouchLean != null) _lblValStressCrouchLean.Text = $"{crouchLeanDeg:F0}°";
            _btnStressWalkShake?.SetPressedNoSignal(walkShake);
            if (!walkShake && _stressHasSavedHeroPos && _previewHeroClone != null && GodotObject.IsInstanceValid(_previewHeroClone))
            {
                _previewHeroClone.Position = _stressSavedHeroPos;
                _stressHasSavedHeroPos = false;
            }
        }

        /// <summary>
        /// Applies procedural Look Up / Crouch Forward Lean / Backward-Walk oscillation in _Process
        /// so artists can stress-test hair/cloth physics and body capsules live without launching Deadlock.
        /// Returns true if any stress-test pose or motion is currently active.
        /// </summary>
        private bool ApplyPhysicsStressTestInProcess(double delta)
        {
            if (_previewHeroSkeleton == null || !GodotObject.IsInstanceValid(_previewHeroSkeleton)) return false;

            // Smoothly sweep current stress pose toward target (~240 deg/s) so preset clicks behave
            // like real mouse look / crouch transitions instead of teleporting hair through the chest.
            float stepDeg = MathF.Max(1f, (float)delta * 240f);
            _stressCurHeadPitchDeg = Mathf.MoveToward(_stressCurHeadPitchDeg, _stressHeadPitchDeg, stepDeg);
            _stressCurCrouchLeanDeg = Mathf.MoveToward(_stressCurCrouchLeanDeg, _stressCrouchLeanDeg, stepDeg);

            bool anyActive = MathF.Abs(_stressHeadPitchDeg) > 0.25f ||
                             MathF.Abs(_stressCrouchLeanDeg) > 0.25f ||
                             MathF.Abs(_stressCurHeadPitchDeg) > 0.25f ||
                             MathF.Abs(_stressCurCrouchLeanDeg) > 0.25f ||
                             _stressWalkBackShake;

            if (!anyActive)
            {
                if (_stressPoseWasActive)
                {
                    _stressPoseWasActive = false;
                    if (_stressHasSavedHeroPos && _previewHeroClone != null && GodotObject.IsInstanceValid(_previewHeroClone))
                    {
                        _previewHeroClone.Position = _stressSavedHeroPos;
                        _stressHasSavedHeroPos = false;
                    }
                    if (!(_previewAnimPlayer?.IsPlaying() ?? false))
                    {
                        ResetStressTestBonesToRest();
                        if (_showColliders) RedrawColliderWireframe();
                        if (_showBones) RedrawBoneWireframeColorCoded();
                    }
                }
                return false;
            }

            _stressPoseWasActive = true;
            bool animPlaying = _previewAnimPlayer?.IsPlaying() ?? false;

            // 1. Optional backward-walk / dash oscillation along hero Z axis
            if (_previewHeroClone != null && GodotObject.IsInstanceValid(_previewHeroClone))
            {
                if (_stressWalkBackShake)
                {
                    if (!_stressHasSavedHeroPos)
                    {
                        _stressSavedHeroPos = _previewHeroClone.Position;
                        _stressHasSavedHeroPos = true;
                    }
                    _stressShakePhase += (float)delta * 3.8f;
                    // Smooth back-and-forth surge (~0.45 m) to induce backward-walk inertial swing
                    float zOff = MathF.Sin(_stressShakePhase) * 0.45f;
                    _previewHeroClone.Position = _stressSavedHeroPos + new Vector3(0f, 0f, zOff);
                }
            }

            // 2. Apply procedural forward crouch lean (spine_1..3) and head/neck pitch (positive = look up)
            Godot.Animation activeAnim = null;
            if (animPlaying && _previewAnimPlayer != null && !string.IsNullOrEmpty(_previewAnimPlayer.CurrentAnimation) &&
                _previewAnimPlayer.HasAnimation(_previewAnimPlayer.CurrentAnimation))
            {
                activeAnim = _previewAnimPlayer.GetAnimation(_previewAnimPlayer.CurrentAnimation);
            }

            bool AnimHasRotationTrack(string boneName)
            {
                if (activeAnim == null) return false;
                int tc = activeAnim.GetTrackCount();
                string suffix = ":" + boneName;
                for (int t = 0; t < tc; t++)
                {
                    if (activeAnim.TrackGetType(t) != Godot.Animation.TrackType.Rotation3D) continue;
                    string path = activeAnim.TrackGetPath(t).ToString();
                    if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            int FindBoneCI(params string[] names)
            {
                foreach (string nm in names)
                {
                    int exact = _previewHeroSkeleton.FindBone(nm);
                    if (exact >= 0) return exact;
                }
                int bc = _previewHeroSkeleton.GetBoneCount();
                for (int i = 0; i < bc; i++)
                {
                    string bn = _previewHeroSkeleton.GetBoneName(i);
                    foreach (string nm in names)
                    {
                        if (bn.Equals(nm, StringComparison.OrdinalIgnoreCase)) return i;
                    }
                }
                return -1;
            }

            void ApplyHeroPitchToBone(float degAroundHeroRight, params string[] boneNames)
            {
                int bIdx = FindBoneCI(boneNames);
                if (bIdx < 0) return;

                string resolvedName = _previewHeroSkeleton.GetBoneName(bIdx);
                int pIdx = _previewHeroSkeleton.GetBoneParent(bIdx);
                Basis parentGlobalBasis = (pIdx >= 0)
                    ? _previewHeroSkeleton.GetBoneGlobalPose(pIdx).Basis.Orthonormalized()
                    : Basis.Identity;

                bool keyedByAnim = animPlaying && AnimHasRotationTrack(resolvedName);
                Quaternion baseRot = keyedByAnim
                    ? _previewHeroSkeleton.GetBonePoseRotation(bIdx)
                    : _previewHeroSkeleton.GetBoneRest(bIdx).Basis.Orthonormalized().GetRotationQuaternion();

                if (MathF.Abs(degAroundHeroRight) < 0.05f)
                {
                    if (!keyedByAnim) _previewHeroSkeleton.SetBonePoseRotation(bIdx, baseRot);
                    return;
                }

                // In Godot hero space (+Z forward, +Y up, +X hero left), rotating around +X by +angle pitches DOWN (forward lean),
                // and rotating by -angle pitches UP (looking upward).
                Vector3 localAxis = (parentGlobalBasis.Inverse() * Vector3.Right).Normalized();
                if (localAxis.LengthSquared() < 0.5f) return;
                Quaternion deltaQ = new Quaternion(localAxis, Mathf.DegToRad(degAroundHeroRight));
                _previewHeroSkeleton.SetBonePoseRotation(bIdx, (deltaQ * baseRot).Normalized());
            }

            ApplyHeroPitchToBone(_stressCurCrouchLeanDeg * 0.30f, "spine_1");
            ApplyHeroPitchToBone(_stressCurCrouchLeanDeg * 0.35f, "spine_2");
            ApplyHeroPitchToBone(_stressCurCrouchLeanDeg * 0.35f, "spine_3");
            // Positive _stressCurHeadPitchDeg = Look Up => negative rotation around +X
            ApplyHeroPitchToBone(-_stressCurHeadPitchDeg * 0.30f, "neck_0", "neck");
            ApplyHeroPitchToBone(-_stressCurHeadPitchDeg * 0.70f, "head");
            return true;
        }

        private void ResetStressTestBonesToRest()
        {
            if (_previewHeroSkeleton == null || !GodotObject.IsInstanceValid(_previewHeroSkeleton)) return;
            string[] stressBones = { "spine_1", "spine_2", "spine_3", "neck_0", "head" };
            foreach (string bn in stressBones)
            {
                int bIdx = _previewHeroSkeleton.FindBone(bn);
                if (bIdx < 0) continue;
                _previewHeroSkeleton.SetBonePoseRotation(bIdx, _previewHeroSkeleton.GetBoneRest(bIdx).Basis.Orthonormalized().GetRotationQuaternion());
            }
        }

        private void ApplyChainParamsLiveToDriver()
        {
            if (_previewHeroClone == null || !GodotObject.IsInstanceValid(_previewHeroClone)) return;
            var driver = DeadlockPlayground.Animation.CustomJiggleBoneDriver.FindOn(_previewHeroClone);
            driver?.UpdateChainParametersInPlace(_boneChains);
        }

        private (HSlider Slider, Label ValueLabel) AddExplainedSlider(
            Control parent,
            string title,
            string explanation,
            double min,
            double max,
            double step,
            string format,
            Action<double> onChanged)
        {
            var block = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            block.AddThemeConstantOverride("separation", 2);

            var hdr = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            hdr.AddThemeConstantOverride("separation", 6);
            var lblTitle = new Label
            {
                Text = title,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
            };
            ImporterStyle.ApplyFontSize(lblTitle, ImporterStyle.FontHint);
            hdr.AddChild(lblTitle);

            var lblVal = new Label
            {
                Text = min.ToString(format),
                HorizontalAlignment = HorizontalAlignment.Right,
                CustomMinimumSize = new Vector2(68, 0)
            };
            ImporterStyle.ApplyFontSize(lblVal, ImporterStyle.FontHint);
            _accentLabels.Add(lblVal);
            hdr.AddChild(lblVal);
            block.AddChild(hdr);

            var slider = new HSlider
            {
                MinValue = min,
                MaxValue = max,
                Step = step,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 22)
            };
            slider.ValueChanged += (v) => onChanged?.Invoke(v);
            block.AddChild(slider);

            var lblHint = new Label { Text = explanation };
            ImporterStyle.ApplyHintStyle(lblHint, wrap: true);
            _secondaryLabels.Add(lblHint);
            block.AddChild(lblHint);

            parent.AddChild(block);
            return (slider, lblVal);
        }

        private void SetSelectedChainSimulationKind(SimulationKind kind)
        {
            if (_selectedChain == null) return;
            _selectedChain.Simulation = kind;
            for (int i = 0; i < _optSelChainSim.ItemCount; i++)
            {
                if (_optSelChainSim.GetItemId(i) == (int)kind)
                {
                    _optSelChainSim.Select(i);
                    break;
                }
            }
            SyncPhysicsTypeSegmentedUI(kind);
            ApplyChainParamsLiveToDriver();
            RefreshClothingChainsPanel();
        }

        private void SyncPhysicsTypeSegmentedUI(SimulationKind kind)
        {
            SimulationKind[] order = { SimulationKind.ProxyCloth, SimulationKind.Cloth, SimulationKind.Jiggle };
            for (int i = 0; i < _chainSimSegButtons.Length && i < order.Length; i++)
            {
                var b = _chainSimSegButtons[i];
                if (b == null) continue;
                bool active = order[i] == kind;
                b.SetPressedNoSignal(active);
                b.ThemeTypeVariation = active ? "ImporterPrimaryButton" : "ToolbarButton";
            }
            if (_lblSelChainSimHint != null)
            {
                _lblSelChainSimHint.Text = kind switch
                {
                    SimulationKind.ProxyCloth => "Proxy Sheet: stitches neighbor strands into one smooth fabric sheet (best for multi-strand back hair, skirts, capes).",
                    SimulationKind.Cloth => "Cloth Chain: independent flexible bone strand with body-capsule deflection (best for ponytails, tails, ribbons).",
                    _ => "Jiggle Bounce: tight rigid rotational bounce with zero hair/capsule collision (best for glutes, breasts, ears, pouches)."
                };
            }
            if (_boxClothExtrudeSection != null)
            {
                _boxClothExtrudeSection.Visible = (kind == SimulationKind.Cloth);
            }
        }

        private void ToggleChainWeightPaintInPhysics(bool open)
        {
            if (open)
            {
                if (_selectedChain == null)
                {
                    _btnChainWeightPaintToggle?.SetPressedNoSignal(false);
                    return;
                }
                string boneToInspect = _selectedChain.AppendedHeroBoneNames?.FirstOrDefault() ?? _selectedChain.RootSourceBone;
                InspectWeightsForBone(boneToInspect);
            }
            else
            {
                if (_btnWeightPaintMode != null && _btnWeightPaintMode.ButtonPressed)
                    _btnWeightPaintMode.ButtonPressed = false;
                if (_boxTab3WeightPaintHost != null)
                    _boxTab3WeightPaintHost.Visible = false;
            }
        }

        private void RefreshChainPaintQuickBones()
        {
            if (_boxTab3WeightBoneChips == null) return;
            foreach (Node child in _boxTab3WeightBoneChips.GetChildren()) child.QueueFree();
            if (_selectedChain == null) return;

            var names = new List<string>();
            if (!string.IsNullOrWhiteSpace(_selectedChain.AnchorHeroBone))
                names.Add(_selectedChain.AnchorHeroBone);
            var chainBones = _selectedChain.AppendedHeroBoneNames != null && _selectedChain.AppendedHeroBoneNames.Count > 0
                ? _selectedChain.AppendedHeroBoneNames
                : _selectedChain.SourceBoneNames;
            foreach (string bn in chainBones)
            {
                if (!string.IsNullOrWhiteSpace(bn) && !names.Contains(bn, StringComparer.OrdinalIgnoreCase))
                    names.Add(bn);
            }

            foreach (string bn in names)
            {
                var btn = new Button
                {
                    Text = bn,
                    ThemeTypeVariation = "ToolbarButton",
                    CustomMinimumSize = new Vector2(0, 28)
                };
                ImporterStyle.ApplyFontSize(btn, ImporterStyle.FontHint);
                string capName = bn;
                btn.Pressed += () =>
                {
                    PopulatePaintBoneDropdown();
                    if (_btnWeightPaintMode != null && !_btnWeightPaintMode.ButtonPressed)
                        _btnWeightPaintMode.ButtonPressed = true;
                    SelectPaintBoneByName(capName);
                };
                _boxTab3WeightBoneChips.AddChild(btn);
            }
        }

        private void SyncSelectedPresetCardUI()
        {
            if (_selectedChain == null) return;
            var preset = PhysicsPresetCatalog.Find(_selectedChain.PresetId);
            if (_lblSelectedPresetName != null)
            {
                _lblSelectedPresetName.Text = preset != null
                    ? $"{preset.DisplayName} ({preset.HeroReference})"
                    : "Custom (Manual Tuning)";
            }
            if (_lblSelectedPresetTag != null)
            {
                _lblSelectedPresetTag.Text = preset != null
                    ? preset.ShortTag
                    : $"{_selectedChain.Simulation} · Root {_selectedChain.StiffnessRoot:F2} · Tip {_selectedChain.StiffnessTip:F2} · Grav {_selectedChain.Gravity:F2}×";
            }
            if (_texSelectedPresetIcon != null)
            {
                _texSelectedPresetIcon.Texture = PhysicsPresetCatalog.LoadPresetTexture(preset);
            }
        }

        private void MarkSelectedChainCustomPreset()
        {
            if (_selectedChain == null) return;
            if (!string.Equals(_selectedChain.PresetId, PhysicsPresetCatalog.CustomId, StringComparison.OrdinalIgnoreCase))
            {
                _selectedChain.PresetId = PhysicsPresetCatalog.CustomId;
                SyncSelectedPresetCardUI();
            }
        }

        private void SyncAdvancedSlidersFromSelectedChain()
        {
            if (_selectedChain == null) return;
            bool prev = _isUpdatingChainInspector;
            _isUpdatingChainInspector = true;
            try
            {
                _sliderSelStiffRoot?.SetValueNoSignal(_selectedChain.StiffnessRoot);
                _sliderSelStiffTip?.SetValueNoSignal(_selectedChain.StiffnessTip);
                _sliderSelTaperPower?.SetValueNoSignal(_selectedChain.GoalTaperPower);
                _sliderSelDamping?.SetValueNoSignal(_selectedChain.Damping);
                _sliderSelGravity?.SetValueNoSignal(_selectedChain.Gravity);
                _sliderSelColRadius?.SetValueNoSignal(_selectedChain.CollisionRadiusTipInches);
                float rfDisp = PhysicsParamResolver.ResolveRootGoalForce(_selectedChain.StiffnessRoot);
                float tfDisp = PhysicsParamResolver.ResolveTipGoalForce(_selectedChain.StiffnessTip, rfDisp);
                float pdDisp = PhysicsParamResolver.ResolveCompiledPointDamping(_selectedChain.Damping);
                if (_lblValStiffRoot != null) _lblValStiffRoot.Text = $"{_selectedChain.StiffnessRoot:F2} (gF {rfDisp:F2})";
                if (_lblValStiffTip != null) _lblValStiffTip.Text = $"{_selectedChain.StiffnessTip:F2} (gF {tfDisp:F3})";
                if (_lblValTaperPower != null) _lblValTaperPower.Text = $"{_selectedChain.GoalTaperPower:F2}";
                if (_lblValDamping != null) _lblValDamping.Text = $"{_selectedChain.Damping:F2} (pDamp {pdDisp:F2})";
                if (_lblValGravity != null) _lblValGravity.Text = $"{_selectedChain.Gravity:F2} ({_selectedChain.Gravity * 360f:F0})";
                if (_lblValColRadius != null) _lblValColRadius.Text = $"{_selectedChain.CollisionRadiusTipInches:F1} in";
                SyncSelectedPresetCardUI();
            }
            finally
            {
                _isUpdatingChainInspector = prev;
            }
        }

        private void EnsurePhysicsPresetPickerPopup()
        {
            if (_presetPickerPopup != null && GodotObject.IsInstanceValid(_presetPickerPopup)) return;

            _presetPickerPopup = new PopupPanel
            {
                Title = "Select Valve-Backed Physics Preset",
                Size = new Vector2I(940, 680),
                MinSize = new Vector2I(860, 620),
                MaxSize = new Vector2I(1040, 760)
            };
            AddChild(_presetPickerPopup);

            var margin = new MarginContainer
            {
                LayoutMode = 1,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill
            };
            margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            margin.AddThemeConstantOverride("margin_left", 18);
            margin.AddThemeConstantOverride("margin_right", 18);
            margin.AddThemeConstantOverride("margin_top", 14);
            margin.AddThemeConstantOverride("margin_bottom", 16);
            _presetPickerPopup.AddChild(margin);

            var rootVBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            rootVBox.AddThemeConstantOverride("separation", 10);
            margin.AddChild(rootVBox);

            var lblHdr = new Label
            {
                Text = "Select Physics Preset",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            ImporterStyle.ApplyFontSize(lblHdr, 16);
            lblHdr.AddThemeColorOverride("font_color", PlaygroundThemeHelper.Parchment);
            _accentLabels.Add(lblHdr);
            rootVBox.AddChild(lblHdr);

            var lblSub = new Label
            {
                Text = "Calibrated from compiled Deadlock hero FeModels. Hover any card for physics details.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            ImporterStyle.ApplyHintStyle(lblSub);
            _secondaryLabels.Add(lblSub);
            rootVBox.AddChild(lblSub);

            var scroll = new ScrollContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 560),
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };
            rootVBox.AddChild(scroll);

            _presetPickerGrid = new GridContainer
            {
                Columns = 4,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            _presetPickerGrid.AddThemeConstantOverride("h_separation", 12);
            _presetPickerGrid.AddThemeConstantOverride("v_separation", 12);
            scroll.AddChild(_presetPickerGrid);

            var cardThumbFrameStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.08f, 0.10f, 0.09f, 0.92f),
                BorderColor = new Color(0.22f, 0.28f, 0.25f, 0.65f),
                BorderWidthLeft = 1,
                BorderWidthRight = 1,
                BorderWidthTop = 1,
                BorderWidthBottom = 1,
                CornerRadiusTopLeft = 8,
                CornerRadiusTopRight = 8,
                CornerRadiusBottomLeft = 8,
                CornerRadiusBottomRight = 8
            };

            foreach (var preset in PhysicsPresetCatalog.Presets)
            {
                var capturedPreset = preset;
                var cardBtn = new Button
                {
                    ThemeTypeVariation = "ToolbarButton",
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    CustomMinimumSize = new Vector2(204, 178),
                    TooltipText = $"{preset.DisplayName} ({preset.HeroReference})\n{preset.ShortTag}\n\n{preset.Description}"
                };

                var pad = new MarginContainer
                {
                    LayoutMode = 1,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                    MouseFilter = Control.MouseFilterEnum.Ignore
                };
                pad.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                pad.AddThemeConstantOverride("margin_left", 10);
                pad.AddThemeConstantOverride("margin_right", 10);
                pad.AddThemeConstantOverride("margin_top", 10);
                pad.AddThemeConstantOverride("margin_bottom", 10);
                cardBtn.AddChild(pad);

                var col = new VBoxContainer
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                    Alignment = BoxContainer.AlignmentMode.Center,
                    MouseFilter = Control.MouseFilterEnum.Ignore
                };
                col.AddThemeConstantOverride("separation", 8);
                pad.AddChild(col);

                var thumbFrame = new PanelContainer
                {
                    CustomMinimumSize = new Vector2(104, 104),
                    SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                    MouseFilter = Control.MouseFilterEnum.Ignore
                };
                thumbFrame.AddThemeStyleboxOverride("panel", cardThumbFrameStyle);

                var thumbPad = new MarginContainer
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                    MouseFilter = Control.MouseFilterEnum.Ignore
                };
                thumbPad.AddThemeConstantOverride("margin_left", 6);
                thumbPad.AddThemeConstantOverride("margin_right", 6);
                thumbPad.AddThemeConstantOverride("margin_top", 6);
                thumbPad.AddThemeConstantOverride("margin_bottom", 6);
                thumbFrame.AddChild(thumbPad);

                var texRect = new TextureRect
                {
                    Texture = PhysicsPresetCatalog.LoadPresetTexture(preset),
                    CustomMinimumSize = new Vector2(92, 92),
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                    MouseFilter = Control.MouseFilterEnum.Ignore
                };
                thumbPad.AddChild(texRect);
                col.AddChild(thumbFrame);

                var textBox = new VBoxContainer
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    MouseFilter = Control.MouseFilterEnum.Ignore
                };
                textBox.AddThemeConstantOverride("separation", 2);

                var lblName = new Label
                {
                    Text = preset.DisplayName,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    ClipText = true,
                    TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                    MouseFilter = Control.MouseFilterEnum.Ignore
                };
                ImporterStyle.ApplyFontSize(lblName, ImporterStyle.FontBody);
                lblName.AddThemeColorOverride("font_color", PlaygroundThemeHelper.Parchment);
                _accentLabels.Add(lblName);

                var lblHero = new Label
                {
                    Text = preset.HeroReference,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    ClipText = true,
                    TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                    MouseFilter = Control.MouseFilterEnum.Ignore
                };
                ImporterStyle.ApplyHintStyle(lblHero);
                _secondaryLabels.Add(lblHero);

                textBox.AddChild(lblName);
                textBox.AddChild(lblHero);
                col.AddChild(textBox);

                cardBtn.Pressed += () =>
                {
                    if (_presetPickerTargets != null && _presetPickerTargets.Count > 0)
                    {
                        bool anyPolicyChanged = false;
                        foreach (var ch in _presetPickerTargets)
                        {
                            if (ch == null) continue;
                            if (ch.Policy != ChainPolicy.PreservePhysicsChain)
                            {
                                ch.Policy = ChainPolicy.PreservePhysicsChain;
                                anyPolicyChanged = true;
                            }
                            PhysicsPresetCatalog.ApplyPresetToChain(ch, capturedPreset.Id, resetMacros: true);
                        }
                        if (anyPolicyChanged) MarkRemapStaleAndRefresh();
                        else ApplyChainParamsLiveToDriver();
                        RefreshClothingChainsPanel();
                        SetBottomStatus($"Applied preset '{capturedPreset.DisplayName} ({capturedPreset.HeroReference})' to {_presetPickerTargets.Count} chain(s).", StatusTone.Success);
                    }
                    _presetPickerPopup.Hide();
                };
                _presetPickerGrid.AddChild(cardBtn);
            }
        }

        private List<BoneChainDef> GetSiblingGroupChains(BoneChainDef selected)
        {
            var targets = new List<BoneChainDef>();
            if (selected == null) return targets;
            targets.Add(selected);
            string selAnchor = selected.AnchorHeroBone ?? "";
            string selCat = selected.Category ?? "";
            foreach (var ch in _boneChains)
            {
                if (ch == null || ReferenceEquals(ch, selected) || IsRigidHubChain(ch)) continue;
                bool sameAnchor = string.Equals(ch.AnchorHeroBone ?? "", selAnchor, StringComparison.OrdinalIgnoreCase);
                bool sameCat = string.Equals(ch.Category ?? "", selCat, StringComparison.OrdinalIgnoreCase);
                if (sameAnchor && sameCat)
                    targets.Add(ch);
            }
            return targets;
        }

        private void OpenPhysicsPresetPicker(List<BoneChainDef> targets, Control anchorControl)
        {
            if (targets == null || targets.Count == 0) return;
            EnsurePhysicsPresetPickerPopup();
            _presetPickerTargets = targets;
            _presetPickerPopup.Title = targets.Count > 1
                ? $"Select Physics Preset (Applies to {targets.Count} chains in group)"
                : $"Select Physics Preset ({targets[0].Name})";
            _presetPickerPopup.PopupCentered(new Vector2I(940, 680));
        }

        private void ApplySelectedChainToSiblingGroup()
        {
            if (_selectedChain == null) return;
            int count = 0;
            bool anyPolicyChanged = false;
            string selAnchor = _selectedChain.AnchorHeroBone ?? "";
            foreach (var ch in _boneChains)
            {
                if (ch == null || ch == _selectedChain || IsRigidHubChain(ch)) continue;
                bool sameAnchor = string.Equals(ch.AnchorHeroBone ?? "", selAnchor, StringComparison.OrdinalIgnoreCase);
                bool sameCat = string.Equals(ch.Category ?? "", _selectedChain.Category ?? "", StringComparison.OrdinalIgnoreCase);
                if (!sameAnchor || !sameCat) continue;

                if (ch.Policy != _selectedChain.Policy) anyPolicyChanged = true;
                ch.Policy = _selectedChain.Policy;
                ch.Simulation = _selectedChain.Simulation;
                ch.PresetId = _selectedChain.PresetId;
                ch.MacroLooseness = _selectedChain.MacroLooseness;
                ch.MacroCollisionScale = _selectedChain.MacroCollisionScale;
                ch.GoalTaperPower = _selectedChain.GoalTaperPower;
                ch.StiffnessRoot = _selectedChain.StiffnessRoot;
                ch.StiffnessTip = _selectedChain.StiffnessTip;
                ch.Damping = _selectedChain.Damping;
                ch.Gravity = _selectedChain.Gravity;
                ch.CollisionRadiusTipInches = _selectedChain.CollisionRadiusTipInches;
                ch.StrayRadiusInches = _selectedChain.StrayRadiusInches;
                ch.TwistRelax = _selectedChain.TwistRelax;
                ch.AngleLimitDeg = _selectedChain.AngleLimitDeg;
                ch.PlanarizeBackBarrier = _selectedChain.PlanarizeBackBarrier;
                ch.AnchorFreeRotate = _selectedChain.AnchorFreeRotate;
                ch.OpenSheet = _selectedChain.OpenSheet;
                ch.Friction = _selectedChain.Friction;
                ch.ExtrudeRadiusInches = _selectedChain.ExtrudeRadiusInches;
                ch.ExtrudeForwardAxis = _selectedChain.ExtrudeForwardAxis;
                ch.ExtrudeTwistDeg = _selectedChain.ExtrudeTwistDeg;
                count++;
            }
            if (anyPolicyChanged) MarkRemapStaleAndRefresh();
            else ApplyChainParamsLiveToDriver();
            RefreshClothingChainsPanel();
            SetBottomStatus($"Applied '{PhysicsPresetCatalog.GetDisplayLabel(_selectedChain.PresetId)}' to {count} sibling chain(s).", StatusTone.Success);
        }

        private void SetPhysicsSubView(int index)
        {
            SetBonesSubView(index == 1 ? 3 : 2);
        }

        private void SyncBodyCollidersFromRigResult(List<BodyCollisionCapsuleDef> fitted)
        {
            if (fitted == null || fitted.Count == 0) return;
            string prevSel = _selectedCollider?.BoneName;
            _bodyCollisionCapsules.Clear();
            foreach (var cap in fitted)
            {
                if (cap != null) _bodyCollisionCapsules.Add(cap.Clone());
            }
            _selectedCollider = !string.IsNullOrEmpty(prevSel)
                ? _bodyCollisionCapsules.FirstOrDefault(c => c.BoneName.Equals(prevSel, StringComparison.OrdinalIgnoreCase))
                : _bodyCollisionCapsules.FirstOrDefault();
            if (_selectedCollider == null && _bodyCollisionCapsules.Count > 0)
                _selectedCollider = _bodyCollisionCapsules[0];
        }

        private void RefreshBodyCollidersPanel()
        {
            if (_boxBodyCollidersList == null) return;
            foreach (Node ch in _boxBodyCollidersList.GetChildren()) ch.QueueFree();

            if (_btnShowCollidersInCard != null) _btnShowCollidersInCard.SetPressedNoSignal(_showColliders);

            if (_bodyCollisionCapsules.Count == 0)
            {
                if (_lblBodyCollidersSummary != null)
                    _lblBodyCollidersSummary.Text = "No body capsules fitted yet. Click 'Auto-Fit from Mesh' after rigging.";
                if (_boxColliderInspector != null) _boxColliderInspector.Visible = false;
                return;
            }

            int activeCount = _bodyCollisionCapsules.Count(c => c.Enabled);
            if (_lblBodyCollidersSummary != null)
                _lblBodyCollidersSummary.Text = $"{activeCount} / {_bodyCollisionCapsules.Count} capsules active in preview & Source 2 export.";

            if (_selectedCollider != null && !_bodyCollisionCapsules.Contains(_selectedCollider))
                _selectedCollider = null;
            if (_selectedCollider == null)
                _selectedCollider = _bodyCollisionCapsules[0];

            foreach (var cap in _bodyCollisionCapsules)
            {
                var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                row.AddThemeConstantOverride("separation", 6);

                var chk = new CheckBox
                {
                    ButtonPressed = cap.Enabled,
                    TooltipText = $"Enable / disable collider on {cap.BoneName}"
                };
                chk.Toggled += (on) =>
                {
                    cap.Enabled = on;
                    cap.IsUserEdited = true;
                    _selectedCollider = cap;
                    ApplyBodyCollidersLiveToDriver();
                    RefreshBodyCollidersPanel();
                };
                row.AddChild(chk);

                string shapeTag = cap.Shape == ColliderShapeKind.Box ? "[Box] " : "";
                string tag = cap.IsCustomCollider ? " · Custom" : (cap.IsUserEdited ? " · Edited" : "");
                var btn = new Button
                {
                    Text = $"{shapeTag}{cap.DisplayName}  ({cap.BoneName}{tag})",
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    ClipText = true,
                    Alignment = HorizontalAlignment.Left,
                    ToggleMode = true,
                    ButtonPressed = ReferenceEquals(cap, _selectedCollider),
                    CustomMinimumSize = new Vector2(0, 30),
                    TooltipText = $"Click to inspect & resize '{cap.DisplayName}' ({cap.BoneName})."
                };
                btn.Pressed += () =>
                {
                    _selectedCollider = cap;
                    RefreshBodyCollidersPanel();
                    _collidersDirty = true;
                    if (_showColliders) RedrawColliderWireframe();
                };
                row.AddChild(btn);
                _boxBodyCollidersList.AddChild(row);
            }

            SyncColliderInspector();
        }

        private void SyncColliderInspector()
        {
            if (_boxColliderInspector == null) return;
            if (_selectedCollider == null)
            {
                _boxColliderInspector.Visible = false;
                return;
            }
            _boxColliderInspector.Visible = true;
            _isUpdatingColliderInspector = true;
            try
            {
                bool isBox = _selectedCollider.Shape == ColliderShapeKind.Box;
                _lblSelectedColliderTitle.Text = isBox
                    ? $"Prop Box: {_selectedCollider.DisplayName}"
                    : $"Capsule: {_selectedCollider.DisplayName}";

                if (isBox)
                {
                    Vector3 full = _selectedCollider.EffectiveBoxHalfExtents * 2.0f;
                    _lblSelectedColliderBoneHint.Text = $"Bone: {_selectedCollider.BoneName} • Size {full.X:F2} × {full.Y:F2} × {full.Z:F2} m";
                }
                else
                {
                    _lblSelectedColliderBoneHint.Text = $"Bone: {_selectedCollider.BoneName} • Effective R0={_selectedCollider.EffectiveRadius0:F3}m, R1={_selectedCollider.EffectiveRadius1:F3}m";
                }

                if (_boxColCapsuleControls != null) _boxColCapsuleControls.Visible = !isBox;
                if (_boxColBoxControls != null) _boxColBoxControls.Visible = isBox;
                if (_btnDeleteCustomCol != null) _btnDeleteCustomCol.Visible = _selectedCollider.IsCustomCollider;

                if (_rowColParentBone != null && _optColParentBone != null)
                {
                    _rowColParentBone.Visible = _selectedCollider.IsCustomCollider;
                    if (_selectedCollider.IsCustomCollider)
                    {
                        _optColParentBone.Clear();
                        var candidates = new List<string> { "spine_2", "spine_3", "spine_1", "pelvis", "neck_0", "head", "arm_upper_L", "arm_upper_R", "leg_upper_L", "leg_upper_R" };
                        if (_previewHeroSkeleton != null && GodotObject.IsInstanceValid(_previewHeroSkeleton))
                        {
                            int bc = _previewHeroSkeleton.GetBoneCount();
                            for (int bi = 0; bi < bc; bi++)
                            {
                                string bn = _previewHeroSkeleton.GetBoneName(bi);
                                if (!string.IsNullOrWhiteSpace(bn) && !candidates.Contains(bn, StringComparer.OrdinalIgnoreCase))
                                    candidates.Add(bn);
                            }
                        }
                        int selIdx = 0;
                        for (int i = 0; i < candidates.Count; i++)
                        {
                            _optColParentBone.AddItem(candidates[i]);
                            if (candidates[i].Equals(_selectedCollider.BoneName, StringComparison.OrdinalIgnoreCase))
                                selIdx = i;
                        }
                        if (_optColParentBone.ItemCount > 0) _optColParentBone.Select(selIdx);
                    }
                }

                _sliderColRadiusScale.SetValueNoSignal(_selectedCollider.RadiusScale);
                _sliderColOffsetX?.SetValueNoSignal(_selectedCollider.LocalOffsetMeters.X);
                _sliderColOffsetY?.SetValueNoSignal(_selectedCollider.LocalOffsetMeters.Y);
                _sliderColOffsetZ.SetValueNoSignal(_selectedCollider.LocalOffsetMeters.Z);
                _sliderColRadius0.SetValueNoSignal(_selectedCollider.Radius0Meters);
                _sliderColRadius1.SetValueNoSignal(_selectedCollider.Radius1Meters);

                if (_lblValColRadiusScale != null) _lblValColRadiusScale.Text = $"{_selectedCollider.RadiusScale:F2}x";
                if (_lblValColOffsetX != null) _lblValColOffsetX.Text = $"{_selectedCollider.LocalOffsetMeters.X:+0.000;-0.000;0.000} m";
                if (_lblValColOffsetY != null) _lblValColOffsetY.Text = $"{_selectedCollider.LocalOffsetMeters.Y:+0.000;-0.000;0.000} m";
                if (_lblValColOffsetZ != null) _lblValColOffsetZ.Text = $"{_selectedCollider.LocalOffsetMeters.Z:+0.000;-0.000;0.000} m";
                if (_lblValColRadius0 != null) _lblValColRadius0.Text = $"{_selectedCollider.Radius0Meters:F3} m";
                if (_lblValColRadius1 != null) _lblValColRadius1.Text = $"{_selectedCollider.Radius1Meters:F3} m";

                if (isBox)
                {
                    Vector3 full = _selectedCollider.BoxHalfExtentsMeters * 2.0f;
                    _sliderColBoxW?.SetValueNoSignal(full.X);
                    _sliderColBoxH?.SetValueNoSignal(full.Y);
                    _sliderColBoxD?.SetValueNoSignal(full.Z);
                    if (_lblValColBoxW != null) _lblValColBoxW.Text = $"{full.X:F2} m";
                    if (_lblValColBoxH != null) _lblValColBoxH.Text = $"{full.Y:F2} m";
                    if (_lblValColBoxD != null) _lblValColBoxD.Text = $"{full.Z:F2} m";

                    _sliderColRotX?.SetValueNoSignal(_selectedCollider.LocalRotationDeg.X);
                    _sliderColRotY?.SetValueNoSignal(_selectedCollider.LocalRotationDeg.Y);
                    _sliderColRotZ?.SetValueNoSignal(_selectedCollider.LocalRotationDeg.Z);
                    if (_lblValColRotX != null) _lblValColRotX.Text = $"{_selectedCollider.LocalRotationDeg.X:F0}°";
                    if (_lblValColRotY != null) _lblValColRotY.Text = $"{_selectedCollider.LocalRotationDeg.Y:F0}°";
                    if (_lblValColRotZ != null) _lblValColRotZ.Text = $"{_selectedCollider.LocalRotationDeg.Z:F0}°";

                    _chkColPlanarizeBack?.SetPressedNoSignal(_selectedCollider.PlanarizeBackOnly);
                }
            }
            finally
            {
                _isUpdatingColliderInspector = false;
            }
        }

        private void OnAddCustomColliderPressed(ColliderShapeKind shape)
        {
            string defaultBone = "spine_2";
            if (_previewHeroSkeleton != null && GodotObject.IsInstanceValid(_previewHeroSkeleton))
            {
                if (_previewHeroSkeleton.FindBone(defaultBone) < 0)
                {
                    if (_previewHeroSkeleton.FindBone("spine_3") >= 0) defaultBone = "spine_3";
                    else if (_previewHeroSkeleton.FindBone("pelvis") >= 0) defaultBone = "pelvis";
                    else if (_previewHeroSkeleton.GetBoneCount() > 0) defaultBone = _previewHeroSkeleton.GetBoneName(0);
                }
            }

            int customNum = _bodyCollisionCapsules.Count(c => c.IsCustomCollider) + 1;
            var newCol = shape == ColliderShapeKind.Box
                ? new BodyCollisionCapsuleDef
                {
                    ColliderId = $"box_{Guid.NewGuid().ToString("N")[..6]}",
                    Shape = ColliderShapeKind.Box,
                    IsCustomCollider = true,
                    BoneName = defaultBone,
                    DisplayName = $"Back Prop Box {customNum}",
                    LocalOffsetMeters = new Vector3(0f, 0f, -0.18f),
                    BoxHalfExtentsMeters = new Vector3(0.14f, 0.16f, 0.09f),
                    LocalRotationDeg = Vector3.Zero,
                    PlanarizeBackOnly = true,
                    Enabled = true,
                    IsUserEdited = true
                }
                : new BodyCollisionCapsuleDef
                {
                    ColliderId = $"cap_{Guid.NewGuid().ToString("N")[..6]}",
                    Shape = ColliderShapeKind.Capsule,
                    IsCustomCollider = true,
                    BoneName = defaultBone,
                    DisplayName = $"Custom Capsule {customNum}",
                    Point0Local = new Vector3(0f, -0.10f, 0f),
                    Point1Local = new Vector3(0f, 0.10f, 0f),
                    Radius0Meters = 0.10f,
                    Radius1Meters = 0.10f,
                    LocalOffsetMeters = new Vector3(0f, 0f, -0.16f),
                    Enabled = true,
                    IsUserEdited = true
                };

            _bodyCollisionCapsules.Add(newCol);
            _selectedCollider = newCol;
            if (!_showColliders && _btnToggleColliders != null) _btnToggleColliders.ButtonPressed = true;
            ApplyBodyCollidersLiveToDriver();
            RefreshBodyCollidersPanel();
            SetBottomStatus($"Added '{newCol.DisplayName}' on '{newCol.BoneName}'. Adjust Width/Height/Depth & Offset to wrap the prop.", StatusTone.Success);
        }

        private void OnDeleteSelectedCustomColliderPressed()
        {
            if (_selectedCollider == null || !_selectedCollider.IsCustomCollider) return;
            string removedName = _selectedCollider.DisplayName;
            _bodyCollisionCapsules.Remove(_selectedCollider);
            _selectedCollider = _bodyCollisionCapsules.FirstOrDefault();
            ApplyBodyCollidersLiveToDriver();
            RefreshBodyCollidersPanel();
            SetBottomStatus($"Deleted custom collider '{removedName}'.", StatusTone.Info);
        }

        private void ApplyBodyCollidersLiveToDriver()
        {
            if (_previewHeroSkeleton != null && GodotObject.IsInstanceValid(_previewHeroSkeleton))
            {
                _previewHeroSkeleton.SetMeta(BodyColliderAutoFitter.SkeletonMetaKey,
                    BodyColliderAutoFitter.SerializeToJson(_bodyCollisionCapsules));
            }
            if (_previewHeroClone != null && GodotObject.IsInstanceValid(_previewHeroClone))
            {
                var driver = DeadlockPlayground.Animation.CustomJiggleBoneDriver.FindOn(_previewHeroClone);
                driver?.RegisterCollisionCapsules(_bodyCollisionCapsules);
            }
            if (_selectedCollider != null && _lblSelectedColliderBoneHint != null)
            {
                if (_selectedCollider.Shape == ColliderShapeKind.Box)
                {
                    Vector3 full = _selectedCollider.EffectiveBoxHalfExtents * 2.0f;
                    _lblSelectedColliderBoneHint.Text = $"Bone: {_selectedCollider.BoneName} • Size {full.X:F2} × {full.Y:F2} × {full.Z:F2} m";
                }
                else
                {
                    _lblSelectedColliderBoneHint.Text = $"Bone: {_selectedCollider.BoneName} • Effective R0={_selectedCollider.EffectiveRadius0:F3}m, R1={_selectedCollider.EffectiveRadius1:F3}m";
                }
            }
            _collidersDirty = true;
            if (_showColliders) RedrawColliderWireframe();
        }

        private void OnAutoFitCollidersPressed()
        {
            if (_previewHeroSkeleton == null || !GodotObject.IsInstanceValid(_previewHeroSkeleton))
            {
                ExecuteLiveRigUpdate();
                return;
            }
            var meshes = new List<MeshInstance3D>();
            CollectSkinnedSubmeshes(_previewRiggedContainer ?? _previewHeroClone, meshes);
            if (meshes.Count == 0)
            {
                ExecuteLiveRigUpdate();
                return;
            }
            string prevSel = _selectedCollider?.BoneName;
            var fitted = BodyColliderAutoFitter.FitFromRiggedMeshes(
                _previewHeroSkeleton, meshes, null, forceResetEdits: true, physicsChains: _boneChains);
            _bodyCollisionCapsules.Clear();
            _bodyCollisionCapsules.AddRange(fitted);
            _selectedCollider = !string.IsNullOrEmpty(prevSel)
                ? _bodyCollisionCapsules.FirstOrDefault(c => c.BoneName.Equals(prevSel, StringComparison.OrdinalIgnoreCase))
                : _bodyCollisionCapsules.FirstOrDefault();
            ApplyBodyCollidersLiveToDriver();
            RefreshBodyCollidersPanel();
            SetBottomStatus($"Auto-fitted {_bodyCollisionCapsules.Count} body collision capsules from mesh silhouette.", StatusTone.Success);
        }

        private void OnResetSelectedColliderPressed()
        {
            if (_selectedCollider == null || _previewHeroSkeleton == null) return;
            var meshes = new List<MeshInstance3D>();
            CollectSkinnedSubmeshes(_previewRiggedContainer ?? _previewHeroClone, meshes);
            var fresh = BodyColliderAutoFitter.FitFromRiggedMeshes(
                _previewHeroSkeleton, meshes, null, forceResetEdits: true, physicsChains: _boneChains);
            var match = fresh.FirstOrDefault(c => c.BoneName.Equals(_selectedCollider.BoneName, StringComparison.OrdinalIgnoreCase));
            if (match == null) return;
            _selectedCollider.Point0Local = match.Point0Local;
            _selectedCollider.Point1Local = match.Point1Local;
            _selectedCollider.Radius0Meters = match.Radius0Meters;
            _selectedCollider.Radius1Meters = match.Radius1Meters;
            _selectedCollider.RadiusScale = 1.0f;
            _selectedCollider.LocalOffsetMeters = Vector3.Zero;
            _selectedCollider.Enabled = true;
            _selectedCollider.IsUserEdited = false;
            ApplyBodyCollidersLiveToDriver();
            RefreshBodyCollidersPanel();
            SetBottomStatus($"Reset collider '{_selectedCollider.DisplayName}' to auto-fitted size.", StatusTone.Info);
        }

        private void OnMirrorSelectedColliderPressed()
        {
            if (_selectedCollider == null) return;
            if (!BodyColliderAutoFitter.TryMirrorSideCapsule(_bodyCollisionCapsules, _selectedCollider, _previewHeroSkeleton, out var twin) || twin == null)
            {
                SetBottomStatus("Mirror L <-> R applies to symmetric left/right capsules (clavicles & upper arms).", StatusTone.Info);
                return;
            }
            ApplyBodyCollidersLiveToDriver();
            RefreshBodyCollidersPanel();
            SetBottomStatus($"Mirrored '{_selectedCollider.BoneName}' -> '{twin.BoneName}'.", StatusTone.Success);
        }

        private static void CollectSkinnedSubmeshes(Node root, List<MeshInstance3D> output)
        {
            if (root == null || !GodotObject.IsInstanceValid(root)) return;
            if (root is MeshInstance3D mi && mi.Mesh != null && mi.Skin != null)
                output.Add(mi);
            foreach (Node ch in root.GetChildren())
                CollectSkinnedSubmeshes(ch, output);
        }

        private void CopySelectedChainToMirrorTwin()
        {
            if (_selectedChain == null) return;
            string n = _selectedChain.RootSourceBone;
            string twin = null;
            if (n.Contains("left", StringComparison.OrdinalIgnoreCase))
                twin = n.Replace("left", "right", StringComparison.OrdinalIgnoreCase);
            else if (n.Contains("right", StringComparison.OrdinalIgnoreCase))
                twin = n.Replace("right", "left", StringComparison.OrdinalIgnoreCase);
            else if (n.EndsWith("_L", StringComparison.OrdinalIgnoreCase))
                twin = n[..^2] + "_R";
            else if (n.EndsWith("_R", StringComparison.OrdinalIgnoreCase))
                twin = n[..^2] + "_L";
            else if (n.EndsWith(".l", StringComparison.OrdinalIgnoreCase))
                twin = n[..^2] + (n.EndsWith(".L") ? ".R" : ".r");
            else if (n.EndsWith(".r", StringComparison.OrdinalIgnoreCase))
                twin = n[..^2] + (n.EndsWith(".R") ? ".L" : ".l");

            if (string.IsNullOrEmpty(twin)) return;
            var target = _boneChains.FirstOrDefault(c => string.Equals(c.RootSourceBone, twin, StringComparison.OrdinalIgnoreCase));
            if (target == null) return;

            bool policyChanged = target.Policy != _selectedChain.Policy;
            target.Policy = _selectedChain.Policy;
            target.Simulation = _selectedChain.Simulation;
            target.PresetId = _selectedChain.PresetId;
            target.MacroLooseness = _selectedChain.MacroLooseness;
            target.MacroCollisionScale = _selectedChain.MacroCollisionScale;
            target.GoalTaperPower = _selectedChain.GoalTaperPower;
            target.StiffnessRoot = _selectedChain.StiffnessRoot;
            target.StiffnessTip = _selectedChain.StiffnessTip;
            target.Damping = _selectedChain.Damping;
            target.Gravity = _selectedChain.Gravity;
            target.CollisionRadiusTipInches = _selectedChain.CollisionRadiusTipInches;
            target.StrayRadiusInches = _selectedChain.StrayRadiusInches;
            target.TwistRelax = _selectedChain.TwistRelax;
            target.AngleLimitDeg = _selectedChain.AngleLimitDeg;
            if (policyChanged) MarkRemapStaleAndRefresh();
            else ApplyChainParamsLiveToDriver();
            RefreshClothingChainsPanel();
            SetBottomStatus($"Copied settings from '{_selectedChain.RootSourceBone}' to '{target.RootSourceBone}'.", StatusTone.Success);
        }

        private void OnPinCollarRootRigidPressed()
        {
            if (_selectedChain == null) return;
            int updated = 0;
            // Determine target toggle state from selected chain's root bone
            var selBones = GetActiveChainBoneNames(_selectedChain);
            if (selBones.Count == 0) return;
            var selRootOv = _selectedChain.FindJointOverride(selBones[0]);
            bool pinRigid = !(selRootOv?.Simulate.HasValue == true && !selRootOv.Simulate.Value);

            foreach (var ch in _boneChains)
            {
                if (ch == null || ch.Policy != ChainPolicy.PreservePhysicsChain) continue;
                if (ch.Simulation != _selectedChain.Simulation) continue;
                if (!string.Equals(ch.Category, _selectedChain.Category, StringComparison.OrdinalIgnoreCase)) continue;
                var bones = GetActiveChainBoneNames(ch);
                if (bones.Count < 2) continue;
                var ov = ch.GetOrCreateJointOverride(bones[0]);
                ov.Simulate = !pinRigid;
                updated++;
            }
            ApplyChainParamsLiveToDriver();
            RefreshChainJointListUI();
            SetBottomStatus(pinRigid
                ? $"Pinned root/collar bone rigid on {updated} chain(s) so attachment seam won't bend."
                : $"Unpinned root/collar bone on {updated} chain(s).", StatusTone.Success);
        }

        private void ApplySelectedChainToCategory()
        {
            if (_selectedChain == null) return;
            int count = 0;
            bool anyPolicyChanged = false;
            foreach (var ch in _boneChains)
            {
                if (ch == _selectedChain) continue;
                if (!string.Equals(ch.Category, _selectedChain.Category, StringComparison.OrdinalIgnoreCase)) continue;
                // Protect Jiggle chains (bust/buttocks/props) from being accidentally converted to ProxyCloth
                // when applying hair/skirt settings across the shared 'Clothing & Hair' category.
                if (ch.Simulation != _selectedChain.Simulation) continue;
                if (ch.Policy != _selectedChain.Policy) anyPolicyChanged = true;
                ch.Policy = _selectedChain.Policy;
                ch.PresetId = _selectedChain.PresetId;
                ch.MacroLooseness = _selectedChain.MacroLooseness;
                ch.MacroCollisionScale = _selectedChain.MacroCollisionScale;
                ch.GoalTaperPower = _selectedChain.GoalTaperPower;
                ch.StiffnessRoot = _selectedChain.StiffnessRoot;
                ch.StiffnessTip = _selectedChain.StiffnessTip;
                ch.Damping = _selectedChain.Damping;
                ch.Gravity = _selectedChain.Gravity;
                ch.CollisionRadiusTipInches = _selectedChain.CollisionRadiusTipInches;
                ch.StrayRadiusInches = _selectedChain.StrayRadiusInches;
                ch.TwistRelax = _selectedChain.TwistRelax;
                ch.AngleLimitDeg = _selectedChain.AngleLimitDeg;
                ch.PlanarizeBackBarrier = _selectedChain.PlanarizeBackBarrier;
                ch.AnchorFreeRotate = _selectedChain.AnchorFreeRotate;
                ch.OpenSheet = _selectedChain.OpenSheet;
                ch.Friction = _selectedChain.Friction;
                ch.ExtrudeRadiusInches = _selectedChain.ExtrudeRadiusInches;
                ch.ExtrudeForwardAxis = _selectedChain.ExtrudeForwardAxis;
                ch.ExtrudeTwistDeg = _selectedChain.ExtrudeTwistDeg;
                count++;
            }
            if (anyPolicyChanged) MarkRemapStaleAndRefresh();
            else ApplyChainParamsLiveToDriver();
            RefreshClothingChainsPanel();
            SetBottomStatus($"Applied physics settings to {count} '{_selectedChain.Category}' chain(s).", StatusTone.Success);
        }

        private void OnAddSyntheticClothingChainPressed()
        {
            string name = string.IsNullOrWhiteSpace(_txtNewChainName?.Text) ? $"cloth_chain_{_boneChains.Count + 1}" : _txtNewChainName.Text.Trim();
            string anchor = _optNewChainAnchor != null && _optNewChainAnchor.Selected >= 0 ? _optNewChainAnchor.GetItemText(_optNewChainAnchor.Selected) : "spine_3";
            string meshFilter = (_optNewChainMesh != null && _optNewChainMesh.Selected > 0) ? _optNewChainMesh.GetItemText(_optNewChainMesh.Selected) : "";

            Vector3 originHero = Vector3.Zero;
            if (_previewHeroSkeleton != null)
            {
                int bIdx = _previewHeroSkeleton.FindBone(anchor);
                if (bIdx >= 0) originHero = _previewHeroSkeleton.GetBoneGlobalRest(bIdx).Origin;
            }

            var chain = new BoneChainDef
            {
                ChainId = $"syn_{Guid.NewGuid().ToString("N")[..6]}",
                Name = name,
                Category = "Clothing & Hair",
                RootSourceBone = name,
                AnchorHeroBone = anchor,
                Policy = ChainPolicy.PreservePhysicsChain,
                Simulation = SimulationKind.Jiggle,
                StiffnessRoot = (float)(_sliderNewChainStiffRoot?.Value ?? 0.85),
                StiffnessTip = (float)(_sliderNewChainStiffTip?.Value ?? 0.25),
                Damping = (float)(_sliderNewChainDamping?.Value ?? 0.35),
                IsSynthetic = true,
                SlotId = _accessorySlotId ?? "",
                TargetMeshName = meshFilter,
                SyntheticOriginHeroSpace = originHero,
                SyntheticDirectionHeroSpace = new Vector3(0f, -1f, 0f),
                SyntheticLengthMeters = (float)(_spinNewChainLen?.Value ?? 0.55),
                SyntheticInfluenceRadius = (float)(_spinNewChainRadius?.Value ?? 0.25),
                SyntheticSegmentCount = (int)(_spinNewChainSegs?.Value ?? 4)
            };
            _boneChains.Add(chain);
            _selectedChain = chain;
            RefreshClothingChainsPanel();
            MarkRemapStaleAndRefresh();
            SetBottomStatus($"Added synthetic clothing chain '{name}' anchored to '{anchor}'.", StatusTone.Success);
        }

        public void ToggleBoneDisabled(string boneName)
        {
            if (string.IsNullOrWhiteSpace(boneName)) return;
            if (_disabledSourceBones.Contains(boneName))
                _disabledSourceBones.Remove(boneName);
            else
                _disabledSourceBones.Add(boneName);

            RefreshDisabledBonesList();
            RefreshSourceBoneDisableBrowser();
            MarkRemapStaleAndRefresh();
            if (_showBones) RedrawBoneWireframe();
        }

        private void RefreshDisabledBonesList()
        {
            if (_btnSubTabDisable != null)
                _btnSubTabDisable.Text = $"Prune ({_disabledSourceBones.Count})";

            if (_boxDisabledBonesList == null) return;
            foreach (Node ch in _boxDisabledBonesList.GetChildren()) ch.QueueFree();

            bool empty = _disabledSourceBones.Count == 0;
            if (_lblDisabledBonesEmpty != null) _lblDisabledBonesEmpty.Visible = empty;
            if (empty) return;

            foreach (string bName in _disabledSourceBones.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList())
            {
                var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                row.AddChild(new Label { Text = bName, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true });
                var btnEnable = new Button { Text = "Re-Enable", CustomMinimumSize = new Vector2(88, 26) };
                string captured = bName;
                btnEnable.Pressed += () => ToggleBoneDisabled(captured);
                row.AddChild(btnEnable);
                _boxDisabledBonesList.AddChild(row);
            }
        }

        private void RefreshSourceBoneDisableBrowser()
        {
            if (_boxSourceBoneDisableList == null) return;
            foreach (Node ch in _boxSourceBoneDisableList.GetChildren()) ch.QueueFree();

            var srcSkel = GetValidSourceSkeleton();
            if (srcSkel == null) return;

            string q = _txtDisableBoneSearch?.Text?.Trim() ?? "";
            int mode = _optDisableBoneFilter?.Selected ?? 0; // 0=Weighted, 1=Hair&Cloth, 2=Disabled, 3=All
            int total = srcSkel.GetBoneCount();
            int shown = 0;
            const int maxRows = 120;

            for (int b = 0; b < total && shown < maxRows; b++)
            {
                string bName = srcSkel.GetBoneName(b);
                bool weighted = _lastWeightedSrcBones.Contains(b);
                bool disabled = _disabledSourceBones.Contains(bName);
                string low = bName.ToLowerInvariant();
                bool isHairCloth = low.Contains("hair") || low.Contains("skirt") || low.Contains("cape") ||
                                   low.Contains("ribbon") || low.Contains("hood") || low.Contains("coat") || low.Contains("tail");

                if (mode == 0 && !weighted) continue;
                if (mode == 1 && !isHairCloth) continue;
                if (mode == 2 && !disabled) continue;
                if (!string.IsNullOrEmpty(q) && !bName.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;

                var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                row.AddThemeConstantOverride("separation", 6);
                var lbl = new Label
                {
                    Text = bName + (weighted ? "" : " [empty]"),
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    ClipText = true,
                    TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
                };
                if (disabled) lbl.AddThemeColorOverride("font_color", ThemeManager.GetColor(nameof(ThemePaletteData.SemanticDanger)));
                row.AddChild(lbl);

                var btn = new Button
                {
                    Text = disabled ? "Re-Enable" : "Disable",
                    CustomMinimumSize = new Vector2(82, 24),
                    TooltipText = "Collapses weights from this bone onto its parent bone."
                };
                string captured = bName;
                btn.Pressed += () => ToggleBoneDisabled(captured);
                row.AddChild(btn);
                _boxSourceBoneDisableList.AddChild(row);
                shown++;
            }
        }
    }
}
