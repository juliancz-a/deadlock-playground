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
        // Tab 2: Auto-Rig Pro–style Unified Rigging Workspace (Pre-Rigged Remap + Modular Reference Rig)
        private OptionButton _optRigFamilyOverride;
        private Control _boxRigFamilyRow;
        private Label _lblRigProfileConfidence;
        private Control _boxMarkersCard;
        private Control _boxFingerCard;
        private CheckBox _chkEnableCustomPhysics;
        private CheckBox _chkBindSymmetry;
        private CheckBox _chkFitSpine;
        private CheckBox _chkConformToSkeleton;
        private CheckBox _chkLooseMeshesRigid;
        private CheckBox _chkExtraTwistBones;

        // Per-group Markers Checklist (kept for compatibility)
        private CheckBox _chkMarkerHeadNeck;
        private CheckBox _chkMarkerArms;
        private CheckBox _chkMarkerLegs;
        private CheckBox _chkMarkerFingers;
        private Button _btnAutoPlaceAllMarkers;

        // Finger Auto-Rigging Controls (Pre-Rigged & Compatibility)
        private OptionButton _optFingerRigMode;
        private Control _boxFingerLandmarkTools;
        private CheckBox _chkGenMetacarpals;
        private CheckBox _chkGenWristTwist;
        private OptionButton _optFingerPosePreset;
        private Label _lblFingerPinStatus;
        private Dictionary<string, Vector3> _fingerLandmarks = new(StringComparer.OrdinalIgnoreCase);
        private IFingerRigStrategy _activeFingerStrategy = new PinBasedFingersStrategy();

        // Auto-Rig Pro Modular Reference Rig UI Controls + Sticky Secondary Top Bar
        private PanelContainer _boxTab2SubNav;
        private readonly Button[] _tab2SubButtons = new Button[4];
        private int _tab2SubView = 0; // 0=Mode, 1=ReferenceRig/PoseFit, 2=Cloth/Hands, 3=PoseFit
        private Control _boxTab2PageMode;
        private Control _boxTab2PagePoseFit;
        private Control _boxTab2PageHandsPreRigged;
        private Control _boxTab2PageFooterLinks;
        private Control _boxTab2PageRefCloth;
        private VBoxContainer _boxRefRigWorkspace;
        private Button _btnEditRefBonesMode;
        private Button _btnBindReferenceRig;
        private CheckBox _chkKeepPaintedWeightsOnRebind;
        private readonly Button[] _spineCountButtons = new Button[4];
        private CheckBox _chkRefHasNeck;
        private CheckBox _chkRefHasClavicles;
        private CheckBox _chkRefHasToes;
        private readonly Button[] _refFingerModeButtons = new Button[2];
        private VBoxContainer _boxRefFingerOptions;
        private CheckBox _chkRefThumb;
        private CheckBox _chkRefIndex;
        private CheckBox _chkRefMiddle;
        private CheckBox _chkRefRing;
        private CheckBox _chkRefPinky;
        private readonly Button[] _refPhalanxButtons = new Button[2];
        private OptionButton _optRefClothParent;
        private LineEdit _txtRefClothName;
        private SpinBox _spinRefClothSegments;
        private SpinBox _spinRefClothRadius;
        private OptionButton _optRefClothDir;
        private OptionButton _optRefClothMesh;
        private VBoxContainer _boxRefClothList;
        private bool _isSyncingRefRigUi;

        // Compatibility fields (kept so external partial files compile cleanly)
        private int _rigWizardStep = 0;
        private StepperBar _rigStepper;
        private readonly Control[] _rigStepPages = new Control[4];
        private OptionCardGroup _rigMethodCards;
        private Button _btnRigWizardBack;
        private Button _btnRigWizardNext;
        private Button _btnRigPhysicsLink;
        private Control _boxTab2WeightPaintHost;
        private Control _boxTab2WizardScroll;
        private Control _boxTab2WizardNavFooter;
        private PanelContainer _boxAccessoryRigNotice;
        private Label _lblAccessoryRigNoticeDesc;
        private Label _lblPinProgressChip;
        private Button _btnMirrorPinsToggle;
        private Button _btnShowPinsToggle;
        private OptionButton _optPoseFitPreset;
        private readonly Button[] _fingerSegButtons = new Button[3];
        private readonly Button[] _fingerPoseButtons = new Button[4];
        private Button _btnUseKnnMethod;

        private CheckBox AddDescribedCheckBox(string title, string description, string tooltip, bool defaultChecked, VBoxContainer targetContainer)
        {
            var itemVBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            itemVBox.AddThemeConstantOverride("separation", 3);

            var chk = new CheckBox
            {
                Text = title,
                ButtonPressed = defaultChecked,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = tooltip
            };
            ImporterStyle.ApplyFontSize(chk, ImporterStyle.FontBody);
            chk.Toggled += (_) => TriggerRealtimeRigUpdate();
            itemVBox.AddChild(chk);

            if (!string.IsNullOrEmpty(description))
            {
                var descMargin = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                descMargin.AddThemeConstantOverride("margin_left", 24);
                var lblDesc = new Label
                {
                    Text = description,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };
                ImporterStyle.ApplyHintStyle(lblDesc);
                _secondaryLabels.Add(lblDesc);
                descMargin.AddChild(lblDesc);
                itemVBox.AddChild(descMargin);
            }

            targetContainer.AddChild(itemVBox);
            return chk;
        }

        private Control BuildTab2Rig()
        {
            var root = new VBoxContainer
            {
                Name = "2. Rig",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill
            };
            root.AddThemeConstantOverride("separation", 8);

            // Clean notice shown in Accessory Mode when RigidToOneBone or KeepOwnBones is active
            var (accNoticeCard, accNoticeBody) = CreateInspectorCard(
                "Accessory Bone Mode Active",
                "This item attaches via Tab 1's Accessory Setup (Rigid Bone or Own Physics Chains), so full-body skeleton rigging is not required.");
            _boxAccessoryRigNotice = accNoticeCard;
            accNoticeCard.Visible = false;
            _lblAccessoryRigNoticeDesc = new Label
            {
                Text = "Switch to 'Skin to Hero Skeleton' in Tab 1 if this is a fitted coat/outfit that needs full-body skinning or reference bones.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            ImporterStyle.ApplyHintStyle(_lblAccessoryRigNoticeDesc);
            _secondaryLabels.Add(_lblAccessoryRigNoticeDesc);
            accNoticeBody.AddChild(_lblAccessoryRigNoticeDesc);
            var accRigBtnRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            accRigBtnRow.AddThemeConstantOverride("separation", 8);
            var btnJumpPhys = new Button { Text = "Configure Physics Chains →", ThemeTypeVariation = "ToolbarButton", CustomMinimumSize = new Vector2(0, 36), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            btnJumpPhys.Pressed += () => { if (_rightTabs != null && _rightTabs.GetTabCount() > 2) _rightTabs.CurrentTab = 2; SetBonesSubView(2); };
            var btnEnableSkin = new Button { Text = "Use Clothing / Partial-Body Rig", ThemeTypeVariation = "ToolbarButton", CustomMinimumSize = new Vector2(0, 36), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            btnEnableSkin.Pressed += () =>
            {
                if (_optAccBoneMode != null)
                {
                    for (int i = 0; i < _optAccBoneMode.ItemCount; i++)
                    {
                        if (_optAccBoneMode.GetItemId(i) == (int)AccessoryBoneMode.SkinToHero)
                        {
                            _optAccBoneMode.Select(i);
                            var srcSkel = GetValidSourceSkeleton();
                            bool hasSrcRig = srcSkel != null && srcSkel.GetBoneCount() > 0;
                            if (!hasSrcRig && GetCurrentRiggingMethod() == CustomModelAutoRigger.RiggingMethod.RigidSingleBone)
                            {
                                SelectRiggingMethodById((int)CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig);
                            }
                            UpdateAccessoryRigVisibility();
                            TriggerRealtimeRigUpdate();
                            break;
                        }
                    }
                }
            };
            accRigBtnRow.AddChild(btnJumpPhys);
            accRigBtnRow.AddChild(btnEnableSkin);
            accNoticeBody.AddChild(accRigBtnRow);
            root.AddChild(accNoticeCard);

            // Fixed Sticky Secondary Top Bar (follows scrolling, zero clutter — mirrors Tab 3's sticky nav bar)
            _boxTab2SubNav = new PanelContainer
            {
                ThemeTypeVariation = "SubPanelContainer",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkBegin
            };
            var subNavMargin = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            subNavMargin.AddThemeConstantOverride("margin_left", 8);
            subNavMargin.AddThemeConstantOverride("margin_right", 8);
            subNavMargin.AddThemeConstantOverride("margin_top", 6);
            subNavMargin.AddThemeConstantOverride("margin_bottom", 6);
            var subNavRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            subNavRow.AddThemeConstantOverride("separation", 6);
            string[] initSubLabels = { "Mode & Profile", "Reference Rig", "Cloth", "Pose Fit" };
            for (int si = 0; si < 4; si++)
            {
                int capturedIdx = si;
                var sBtn = new Button
                {
                    Text = initSubLabels[si],
                    ToggleMode = true,
                    ButtonPressed = si == 0,
                    ThemeTypeVariation = si == 0 ? "ImporterPrimaryButton" : "ToolbarButton",
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    CustomMinimumSize = new Vector2(0, 36)
                };
                ImporterStyle.ApplyFontSize(sBtn, ImporterStyle.FontHint);
                sBtn.Pressed += () => SetTab2SubView(capturedIdx);
                _tab2SubButtons[si] = sBtn;
                subNavRow.AddChild(sBtn);
            }
            subNavMargin.AddChild(subNavRow);
            _boxTab2SubNav.AddChild(subNavMargin);
            root.AddChild(_boxTab2SubNav);

            var scroll = new ScrollContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };
            _boxTab2WizardScroll = scroll;
            root.AddChild(scroll);

            var margin = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            margin.AddThemeConstantOverride("margin_left", 10);
            margin.AddThemeConstantOverride("margin_top", 8);
            margin.AddThemeConstantOverride("margin_right", 10);
            margin.AddThemeConstantOverride("margin_bottom", 12);
            scroll.AddChild(margin);

            var pagesHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            pagesHost.AddThemeConstantOverride("separation", ImporterStyle.Gap);
            margin.AddChild(pagesHost);

            // Hidden backing controls (single source of truth for GatherRiggingOptions / RestoreRiggingOptions)
            _optRiggingMethod = new OptionButton { Visible = false };
            PopulateRiggingMethodOptions(hasRig: true);
            _optRiggingMethod.ItemSelected += OnRiggingMethodChanged;
            pagesHost.AddChild(_optRiggingMethod);

            _chkEnableCustomPhysics = new CheckBox { ButtonPressed = true, Visible = false };
            _chkEnableCustomPhysics.Toggled += (on) =>
            {
                if (_chkPhysicsMasterTab3 != null && _chkPhysicsMasterTab3.ButtonPressed != on)
                    _chkPhysicsMasterTab3.SetPressedNoSignal(on);
                UpdateRigPhysicsLinkText();
                MarkRemapStaleAndRefresh();
            };
            pagesHost.AddChild(_chkEnableCustomPhysics);

            _chkMarkerFingers = new CheckBox { ButtonPressed = false, Visible = false };
            pagesHost.AddChild(_chkMarkerFingers);

            // Hidden WeightPaint host container so BuildWeightPaintPanel initializes once and Tab 3 hosts it cleanly
            _boxTab2WeightPaintHost = new VBoxContainer { Visible = false };
            _boxWeightPaintSection = BuildWeightPaintPanel();
            _boxTab2WeightPaintHost.AddChild(_boxWeightPaintSection);
            pagesHost.AddChild(_boxTab2WeightPaintHost);

            // 1. Rigging Mode & Promoted Rig Profile Card
            _boxTab2PageMode = BuildRigMethodAndProfileSection();
            pagesHost.AddChild(_boxTab2PageMode);

            // 2. Auto-Rig Pro Modular Reference Rig Workspace (Edit / Bind — Limb/Finger toggles live in Viewport HUD!)
            _boxRefRigWorkspace = BuildModularReferenceRigWorkspace();
            pagesHost.AddChild(_boxRefRigWorkspace);

            // 3. Dedicated Cloth Bone Chains page (Auto-Rig)
            _boxTab2PageRefCloth = BuildReferenceClothPage();
            pagesHost.AddChild(_boxTab2PageRefCloth);

            // 4. Pose Fit & Body + Pre-Rigged Hands & Fingers
            var (poseFitPage, handsPrePage) = BuildPoseFitAndHandsPages();
            _boxTab2PagePoseFit = poseFitPage;
            _boxTab2PageHandsPreRigged = handsPrePage;
            pagesHost.AddChild(_boxTab2PagePoseFit);
            pagesHost.AddChild(_boxTab2PageHandsPreRigged);

            // 5. Quick Jump Links to Tab 3 (Weights & Physics) + Legacy KNN Foldout
            _boxTab2PageFooterLinks = BuildTab2FooterLinksAndKnn();
            pagesHost.AddChild(_boxTab2PageFooterLinks);

            OnRiggingMethodChanged(_optRiggingMethod.Selected);
            SyncReferenceRigControlsFromDef();
            UpdateTab2SectionVisibility();
            return root;
        }

        private Control BuildRigMethodAndProfileSection()
        {
            var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", ImporterStyle.Gap);

            var (statusCard, statusBody) = CreateInspectorCard("Rigging Mode");
            box.AddChild(statusCard);

            _lblRigStatusBadge = new Label { Text = "Analyzing Model Rig Status...", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            ImporterStyle.ApplyFontSize(_lblRigStatusBadge, ImporterStyle.FontBody);
            SetRigBadgeStatus("Analyzing Model Rig Status...", StatusTone.Info);
            statusBody.AddChild(_lblRigStatusBadge);

            _lblRigProfileConfidence = new Label { Text = "Detected: --", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            ImporterStyle.ApplyHintStyle(_lblRigProfileConfidence);
            _secondaryLabels.Add(_lblRigProfileConfidence);
            statusBody.AddChild(_lblRigProfileConfidence);

            _rigMethodCards = new OptionCardGroup(columns: 1);
            RefreshMethodCards(hasRig: true);
            _rigMethodCards.OptionSelected += (methodId) => SelectRiggingMethodById(methodId);
            statusBody.AddChild(_rigMethodCards);

            _lblRiggingMethodDesc = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            ImporterStyle.ApplyHintStyle(_lblRiggingMethodDesc);
            _secondaryLabels.Add(_lblRiggingMethodDesc);
            statusBody.AddChild(_lblRiggingMethodDesc);

            // Promoted Rig Profile selector (always visible when Pre-Rigged Remap is active — no longer hidden in Advanced!)
            var famRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            famRow.AddThemeConstantOverride("separation", 8);
            var lblFam = new Label { Text = "Rig Profile:", CustomMinimumSize = new Vector2(88, 0) };
            ImporterStyle.ApplyBodyStyle(lblFam);
            lblFam.AddThemeColorOverride("font_color", PlaygroundThemeHelper.Parchment);
            famRow.AddChild(lblFam);

            _optRigFamilyOverride = new OptionButton
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, ImporterStyle.ButtonHeight),
                TooltipText = "Auto-detected skeleton convention, or manually override (Auto-Rig Pro, Rigify, Mixamo, VRM, Valve Biped, etc.)."
            };
            ConfigureOptionButton(_optRigFamilyOverride);
            _optRigFamilyOverride.AddItem("Auto-Detect (Heuristic)", (int)RigFamily.Unknown);
            _optRigFamilyOverride.AddItem("Auto-Rig Pro (ARP)", (int)RigFamily.AutoRigPro);
            _optRigFamilyOverride.AddItem("Blender Rigify", (int)RigFamily.Rigify);
            _optRigFamilyOverride.AddItem("Mixamo", (int)RigFamily.Mixamo);
            _optRigFamilyOverride.AddItem("VRM / VRoid", (int)RigFamily.VrmVroid);
            _optRigFamilyOverride.AddItem("Valve Biped", (int)RigFamily.ValveBiped);
            _optRigFamilyOverride.AddItem("3ds Max Biped", (int)RigFamily.Biped);
            _optRigFamilyOverride.AddItem("Unreal Mannequin", (int)RigFamily.UnrealMannequin);
            _optRigFamilyOverride.AddItem("Unity Humanoid", (int)RigFamily.UnityHumanoid);
            _optRigFamilyOverride.AddItem("Roblox R15", (int)RigFamily.RobloxR15);
            _optRigFamilyOverride.AddItem("Roblox R6", (int)RigFamily.RobloxR6);
            _optRigFamilyOverride.AddItem("Generic Humanoid", (int)RigFamily.Generic);
            _optRigFamilyOverride.ItemSelected += (_) =>
            {
                AnalyzeModelRigStatus();
                TriggerRealtimeRigUpdate();
            };
            famRow.AddChild(_optRigFamilyOverride);
            _boxRigFamilyRow = famRow;
            statusBody.AddChild(famRow);

            // Rigid Attachment Card (shown when RigidSingleBone is selected)
            var (rigidCard, rigidBody) = CreateInspectorCard("Pick Hero Bone", "Attaches the entire model rigidly to a single Deadlock hero bone.");
            rigidCard.Visible = false;
            _boxRigidBone = rigidCard;
            _optRigidBone = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, ImporterStyle.ButtonHeight) };
            ConfigureOptionButton(_optRigidBone);
            _optRigidBone.ItemSelected += (_) => TriggerRealtimeRigUpdate();
            rigidBody.AddChild(_optRigidBone);

            var quickBoneFlow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            quickBoneFlow.AddThemeConstantOverride("h_separation", 8);
            quickBoneFlow.AddThemeConstantOverride("v_separation", 8);
            void AddQuickBoneChip(string label, params string[] candidates)
            {
                var chip = new Button { Text = label, ThemeTypeVariation = "ToolbarButton", CustomMinimumSize = new Vector2(78, 34) };
                ImporterStyle.ApplyFontSize(chip, ImporterStyle.FontHint);
                chip.Pressed += () =>
                {
                    if (_optRigidBone == null) return;
                    foreach (string c in candidates)
                    {
                        for (int i = 0; i < _optRigidBone.ItemCount; i++)
                        {
                            string txt = _optRigidBone.GetItemText(i).Replace("★ ", "");
                            if (txt.Equals(c, StringComparison.OrdinalIgnoreCase))
                            {
                                _optRigidBone.Select(i);
                                TriggerRealtimeRigUpdate();
                                return;
                            }
                        }
                    }
                };
                quickBoneFlow.AddChild(chip);
            }
            AddQuickBoneChip("Head", "head");
            AddQuickBoneChip("Chest", "spine_3", "spine_2");
            AddQuickBoneChip("Hand R", "hand_R", "weapon_hand_R");
            AddQuickBoneChip("Hand L", "hand_L", "weapon_hand_L");
            rigidBody.AddChild(quickBoneFlow);
            box.AddChild(rigidCard);

            // Clothing / Partial-Body Auto-Rig Setup Card (shown when PartialBodyAutoRig is selected)
            _boxPartialBodyPanel = BuildPartialBodyRigPanel();
            box.AddChild(_boxPartialBodyPanel);

            return box;
        }

        private VBoxContainer BuildModularReferenceRigWorkspace()
        {
            var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            box.AddThemeConstantOverride("separation", ImporterStyle.Gap);

            // 1. Auto-Rig Pro Edit Reference Bones <-> Bind Action Card
            var (actionCard, actionBody) = CreateInspectorCard(
                "Reference Rig (Auto-Rig Pro Workflow)",
                "Edit reference joints in 3D with the Move Gizmo, customize limbs/fingers/cloth, then click Bind to compute weights.");
            _boxMarkersCard = actionCard;
            box.AddChild(actionCard);

            var modeRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            modeRow.AddThemeConstantOverride("separation", 8);

            _btnEditRefBonesMode = new Button
            {
                Text = "Edit Reference Bones",
                ThemeTypeVariation = "ToolbarButton",
                ToggleMode = true,
                ButtonPressed = _isEditReferenceBonesMode,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, ImporterStyle.PrimaryButtonHeight),
                TooltipText = "Freezes the mesh in clean bind pose and shows draggable 3D reference joints with Move Gizmo."
            };
            SetButtonIcon(_btnEditRefBonesMode, "res://assets/at-icons/bone.svg", 16);
            ImporterStyle.ApplyFontSize(_btnEditRefBonesMode, ImporterStyle.FontBody);
            _btnEditRefBonesMode.Toggled += (on) =>
            {
                _isEditReferenceBonesMode = on;
                if (on)
                {
                    if (_btnWeightPaintMode != null && _btnWeightPaintMode.ButtonPressed)
                        _btnWeightPaintMode.ButtonPressed = false;
                    EnsureReferenceRigInitialized(false);
                }
                else if (_isGizmoEditingRefJoint)
                {
                    _isGizmoEditingRefJoint = false;
                    UpdateGizmoTargetTransform();
                }
                ApplyLandmarkPreviewState();
                SyncEditBindButtonVisuals();
            };
            modeRow.AddChild(_btnEditRefBonesMode);

            _btnBindReferenceRig = new Button
            {
                Text = "Bind / Re-Bind",
                ThemeTypeVariation = "ImporterPrimaryButton",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, ImporterStyle.PrimaryButtonHeight),
                TooltipText = "Computes geodesic weights from the active Reference Rig, binds the mesh to Deadlock, and switches to Pose/Test mode."
            };
            SetButtonIcon(_btnBindReferenceRig, "res://assets/at-icons/sparkles.svg", 16);
            ImporterStyle.ApplyFontSize(_btnBindReferenceRig, ImporterStyle.FontBody);
            _btnBindReferenceRig.Pressed += OnBindReferenceRigPressed;
            modeRow.AddChild(_btnBindReferenceRig);
            actionBody.AddChild(modeRow);

            _chkKeepPaintedWeightsOnRebind = new CheckBox
            {
                Text = "Keep my painted weight overrides on Re-Bind",
                ButtonPressed = false,
                TooltipText = "When checked, custom brush edits painted in Tab 3 (Weights) are preserved on top of the new bind."
            };
            ImporterStyle.ApplyFontSize(_chkKeepPaintedWeightsOnRebind, ImporterStyle.FontHint);
            actionBody.AddChild(_chkKeepPaintedWeightsOnRebind);

            // Build legacy pin panel (hosts _lblActivePinBadge, _chkMirrorPins, _chkShowPins)
            _boxLandmarkPins = BuildLandmarkPinPanel();
            actionBody.AddChild(_boxLandmarkPins);

            var utilRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            utilRow.AddThemeConstantOverride("separation", 6);

            _btnAutoPlaceAllMarkers = new Button
            {
                Text = "Auto-Place",
                ThemeTypeVariation = "ToolbarButton",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 34),
                TooltipText = "Re-estimates all core & finger reference joints from the mesh silhouette."
            };
            SetButtonIcon(_btnAutoPlaceAllMarkers, "res://assets/at-icons/arrow_counterclockwise.svg", 15);
            ImporterStyle.ApplyFontSize(_btnAutoPlaceAllMarkers, ImporterStyle.FontHint);
            _btnAutoPlaceAllMarkers.Pressed += () =>
            {
                PushReferenceRigUndoSnapshot();
                _isGuidedPinPlacement = false;
                _guidedPlacedPinKeys.Clear();
                _selectedPin = null; _hoveredPin = null; _draggingPin = null;
                _selectedPins.Clear();
                EnsureReferenceRigInitialized(true);
                _userMovedLandmarkPins = true;
                UpdateActivePinBadge();
                RefreshPinVisuals();
                SyncReferenceRigViewportHud();
                UpdatePinProgressChip();
                SetRigChipStatus("Auto-Placed Reference Rig — Drag joints or click Bind", StatusTone.Info);
            };
            utilRow.AddChild(_btnAutoPlaceAllMarkers);

            _btnMirrorPinsToggle = new Button
            {
                Text = "X-Mirror",
                ThemeTypeVariation = "ToolbarButton",
                ToggleMode = true,
                ButtonPressed = _chkMirrorPins?.ButtonPressed ?? true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 34),
                TooltipText = "Mirror left/right edits symmetrically across X (core, fingers, and mirrored cloth chains)."
            };
            ImporterStyle.ApplyFontSize(_btnMirrorPinsToggle, ImporterStyle.FontHint);
            _btnMirrorPinsToggle.Toggled += (on) =>
            {
                if (_chkMirrorPins != null && _chkMirrorPins.ButtonPressed != on)
                    _chkMirrorPins.ButtonPressed = on;
            };
            utilRow.AddChild(_btnMirrorPinsToggle);

            _btnShowPinsToggle = new Button
            {
                Text = "Show Rig",
                ThemeTypeVariation = "ToolbarButton",
                ToggleMode = true,
                ButtonPressed = _chkShowPins?.ButtonPressed ?? true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 34),
                TooltipText = "Show/hide reference bones in the 3D viewport."
            };
            ImporterStyle.ApplyFontSize(_btnShowPinsToggle, ImporterStyle.FontHint);
            _btnShowPinsToggle.Toggled += (on) =>
            {
                if (_chkShowPins != null && _chkShowPins.ButtonPressed != on)
                    _chkShowPins.ButtonPressed = on;
            };
            utilRow.AddChild(_btnShowPinsToggle);
            actionBody.AddChild(utilRow);

            var lblHudTip = new Label
            {
                Text = "Tip: Spine count, neck/clavicles/toes, individual finger toggles, Focus Hand L/R, and Snap Fingers are right over the 3D viewport while Edit Reference Bones is active.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            ImporterStyle.ApplyHintStyle(lblHudTip);
            _secondaryLabels.Add(lblHudTip);
            actionBody.AddChild(lblHudTip);

            return box;
        }

        private Control BuildReferenceClothPage()
        {
            var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            box.AddThemeConstantOverride("separation", ImporterStyle.Gap);

            var (clothCard, clothBody) = CreateInspectorCard(
                "Cloth Bone Chains",
                "Add hair, cape, skirt, or ribbon bones in 3D. Each chain stays isolated to its radius and mesh island, then appears in Tab 3 (Physics) on Bind.");
            box.AddChild(clothCard);

            // Row 1: Chain Name + Parent Bone
            var clothRow1 = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            clothRow1.AddThemeConstantOverride("separation", 6);
            _txtRefClothName = new LineEdit
            {
                PlaceholderText = "Chain name (e.g. hair_tail)",
                Text = "cloth_strand",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 32)
            };
            _optRefClothParent = new OptionButton
            {
                CustomMinimumSize = new Vector2(106, 32),
                TooltipText = "Skeleton bone this cloth chain attaches to."
            };
            ConfigureOptionButton(_optRefClothParent);
            foreach (string pb in new[] { "head", "spine_3", "spine_2", "pelvis", "clavicle_L", "clavicle_R", "hand_L", "hand_R" })
                _optRefClothParent.AddItem(pb);
            _optRefClothParent.Select(1); // default spine_3
            clothRow1.AddChild(_txtRefClothName);
            clothRow1.AddChild(_optRefClothParent);
            clothBody.AddChild(clothRow1);

            // Row 2: Bone Count + Hang Direction
            var clothRow2 = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            clothRow2.AddThemeConstantOverride("separation", 6);
            _spinRefClothSegments = new SpinBox
            {
                MinValue = 1,
                MaxValue = 6,
                Step = 1,
                Value = 2,
                Prefix = "Bones:",
                CustomMinimumSize = new Vector2(96, 32),
                TooltipText = "Number of bones in chain (1 = single jiggle bone, 2–6 = bendable hair/cape/skirt chain)."
            };
            _optRefClothDir = new OptionButton
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 32),
                TooltipText = "Initial direction for newly placed cloth chain joints."
            };
            ConfigureOptionButton(_optRefClothDir);
            _optRefClothDir.AddItem("Hang Down (-Y)", 0);
            _optRefClothDir.AddItem("Back (-Z)", 1);
            _optRefClothDir.AddItem("Outward Left (+X)", 2);
            _optRefClothDir.AddItem("Outward Right (-X)", 3);
            clothRow2.AddChild(_spinRefClothSegments);
            clothRow2.AddChild(_optRefClothDir);
            clothBody.AddChild(clothRow2);

            // Row 3: Influence Radius (cm) + Optional Target Mesh Filter
            var clothRow3 = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            clothRow3.AddThemeConstantOverride("separation", 6);
            _spinRefClothRadius = new SpinBox
            {
                MinValue = 2,
                MaxValue = 30,
                Step = 1,
                Value = 8,
                Prefix = "Radius:",
                Suffix = " cm",
                CustomMinimumSize = new Vector2(124, 32),
                TooltipText = "Max weight influence radius in cm. Smaller (4–8 cm) keeps hair/ponytails off the face and hoodie; larger (12–20 cm) fits wide capes/skirts."
            };
            _optRefClothMesh = new OptionButton
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 32),
                TooltipText = "Auto (Nearest Island) isolates weights to the mesh shell pierced by the chain, or lock strictly to one submesh."
            };
            ConfigureOptionButton(_optRefClothMesh);
            _optRefClothMesh.AddItem("Auto (Mesh Island)", 0);
            clothRow3.AddChild(_spinRefClothRadius);
            clothRow3.AddChild(_optRefClothMesh);
            clothBody.AddChild(clothRow3);

            // Row 4: Full-width Add Button (never clips off right edge)
            var btnAddCloth = new Button
            {
                Text = "+ Add Cloth Chain",
                ThemeTypeVariation = "ImporterPrimaryButton",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 34)
            };
            ImporterStyle.ApplyFontSize(btnAddCloth, ImporterStyle.FontBody);
            btnAddCloth.Pressed += OnAddReferenceClothChainPressed;
            clothBody.AddChild(btnAddCloth);

            var sep = new HSeparator();
            clothBody.AddChild(sep);

            _boxRefClothList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _boxRefClothList.AddThemeConstantOverride("separation", 6);
            clothBody.AddChild(_boxRefClothList);

            return box;
        }

        private (Control PoseFitPage, Control HandsPreRiggedPage) BuildPoseFitAndHandsPages()
        {
            var poseFitBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            poseFitBox.AddThemeConstantOverride("separation", ImporterStyle.Gap);
            var handsBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            handsBox.AddThemeConstantOverride("separation", ImporterStyle.Gap);

            // Pose Fit & Body Card
            var (snapCard, snapBody) = CreateInspectorCard("Pose Fit & Body", "Aligns limbs and torso to Deadlock's bind pose before animation playback.");
            _boxAutoSnapCard = snapCard;
            _boxAutoSnapOptions = snapBody;
            poseFitBox.AddChild(snapCard);

            var presetRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            presetRow.AddThemeConstantOverride("separation", 8);
            var lblFit = new Label { Text = "Pose Fit:", CustomMinimumSize = new Vector2(88, 0) };
            ImporterStyle.ApplyBodyStyle(lblFit);
            presetRow.AddChild(lblFit);

            _optPoseFitPreset = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, ImporterStyle.ButtonHeight) };
            ConfigureOptionButton(_optPoseFitPreset);
            _optPoseFitPreset.AddItem("Full Fit (Arms, Legs, Torso)", 0);
            _optPoseFitPreset.AddItem("Arms Only", 1);
            _optPoseFitPreset.AddItem("None (Keep Original Pose)", 2);
            _optPoseFitPreset.Select(0);
            _optPoseFitPreset.ItemSelected += OnPoseFitPresetSelected;
            presetRow.AddChild(_optPoseFitPreset);
            snapBody.AddChild(presetRow);

            var bodyRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            bodyRow.AddThemeConstantOverride("separation", 10);
            _chkBindSymmetry = new CheckBox { Text = "Symmetry (L/R)", ButtonPressed = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _chkFitSpine = new CheckBox { Text = "Spine Proportions", ButtonPressed = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            ImporterStyle.ApplyFontSize(_chkBindSymmetry, ImporterStyle.FontBody);
            ImporterStyle.ApplyFontSize(_chkFitSpine, ImporterStyle.FontBody);
            _chkBindSymmetry.Toggled += (_) => TriggerRealtimeRigUpdate();
            _chkFitSpine.Toggled += (_) => TriggerRealtimeRigUpdate();
            bodyRow.AddChild(_chkBindSymmetry);
            bodyRow.AddChild(_chkFitSpine);
            snapBody.AddChild(bodyRow);

            var (customCard, customBody) = CreateInspectorCard(
                "Fine-Tune… (Limb Fit, Hand Grip & Guards)",
                "Fine-grained limb snapping, twist bones, manual wrist grip offsets, and deformation guards.",
                collapsible: true,
                startExpanded: false);
            snapBody.AddChild(customCard);

            _chkConformToSkeleton = AddDescribedCheckBox("Conform Limbs to Hero Skeleton", "Enables segment-level arm/leg/head snapping to Deadlock's rest stance.", "Toggles Auto-Snap segment conformation.", true, customBody);
            _chkAutoAlignArms = AddDescribedCheckBox("Auto-Snap Arms & Wrists", "Aligns shoulders, elbows, and wrists to the hero's weapon stance.", "Aligns arm segments.", true, customBody);
            _chkAutoAlignLegs = AddDescribedCheckBox("Auto-Snap Legs & Feet", "Aligns thighs, knees, and ankles while keeping soles on Y >= 0.", "Aligns leg segments.", true, customBody);
            _chkAutoAlignHead = AddDescribedCheckBox("Auto-Snap Torso, Neck & Head", "Aligns neck and head to the hero's cervical pivot.", "Aligns neck/head.", true, customBody);
            _chkAutoAlignHandOrientation = AddDescribedCheckBox("Auto-Align Palm & Grip Roll", "Automatically aligns palm roll around forearm axis.", "Aligns palm roll.", true, customBody);
            _chkLooseMeshesRigid = AddDescribedCheckBox("Keep Loose Meshes Rigid", "Prevents disconnected rigid accessories from tearing across joints.", "Keeps disconnected mesh islands weighted uniformly.", false, customBody);
            _chkExtraTwistBones = AddDescribedCheckBox("Extra Twist Bones", "Preserves/generates forearm & thigh twist helpers.", "Extra twist bones.", false, customBody);

            _boxHandSnapTuning = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _boxHandSnapTuning.AddThemeConstantOverride("separation", 6);
            customBody.AddChild(_boxHandSnapTuning);

            var handTuneHeader = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            var lblHandTuneTitle = new Label { Text = "Pre-Bind Hand & Grip Fine-Tune:", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            ImporterStyle.ApplyHintStyle(lblHandTuneTitle);
            _secondaryLabels.Add(lblHandTuneTitle);
            handTuneHeader.AddChild(lblHandTuneTitle);
            var btnResetHands = new Button { Text = "Reset Hands", CustomMinimumSize = new Vector2(104, 30) };
            ImporterStyle.ApplyFontSize(btnResetHands, ImporterStyle.FontHint);
            btnResetHands.Pressed += () =>
            {
                _isGizmoUpdatingFromUI = true;
                _spinRightHandPitch.Value = 0.0; _spinRightHandYaw.Value = 0.0; _spinRightHandRoll.Value = 0.0; _spinRightHandScale.Value = 1.0;
                _spinLeftHandPitch.Value = 0.0; _spinLeftHandYaw.Value = 0.0; _spinLeftHandRoll.Value = 0.0; _spinLeftHandScale.Value = 1.0;
                _isGizmoUpdatingFromUI = false;
                TriggerRealtimeRigUpdate();
            };
            handTuneHeader.AddChild(btnResetHands);
            _boxHandSnapTuning.AddChild(handTuneHeader);

            SpinBox MakeHandSpin(double min, double max, double step, double val, string prefix, string tip)
            {
                var sb = CreateCoordSpinBox(min, max, step, prefix);
                sb.Value = val;
                sb.CustomArrowStep = step < 0.1 ? 0.02 : 2.0;
                sb.TooltipText = tip;
                sb.ValueChanged += (_) => { if (!_isGizmoUpdatingFromUI) ScheduleDebouncedLiveRigUpdate(); };
                return sb;
            }

            var lblRH = new Label { Text = "Right Hand (Primary Weapon Grip):" };
            ImporterStyle.ApplyBodyStyle(lblRH);
            _boxHandSnapTuning.AddChild(lblRH);
            var rightHandGrid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            rightHandGrid.AddThemeConstantOverride("h_separation", 6); rightHandGrid.AddThemeConstantOverride("v_separation", 6);
            _spinRightHandPitch = MakeHandSpin(-90, 90, 0.5, 0, "Curl", "Wrist curl");
            _spinRightHandYaw = MakeHandSpin(-90, 90, 0.5, 0, "Tilt", "Wrist tilt");
            _spinRightHandRoll = MakeHandSpin(-180, 180, 0.5, 0, "Roll", "Forearm roll");
            _spinRightHandScale = MakeHandSpin(0.5, 1.8, 0.01, 1.0, "Size", "Hand scale");
            rightHandGrid.AddChild(_spinRightHandPitch); rightHandGrid.AddChild(_spinRightHandYaw);
            rightHandGrid.AddChild(_spinRightHandRoll); rightHandGrid.AddChild(_spinRightHandScale);
            _boxHandSnapTuning.AddChild(rightHandGrid);

            var lblLH = new Label { Text = "Left Hand:" };
            ImporterStyle.ApplyBodyStyle(lblLH);
            _boxHandSnapTuning.AddChild(lblLH);
            var leftHandGrid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            leftHandGrid.AddThemeConstantOverride("h_separation", 6); leftHandGrid.AddThemeConstantOverride("v_separation", 6);
            _spinLeftHandPitch = MakeHandSpin(-90, 90, 0.5, 0, "Curl", "Wrist curl");
            _spinLeftHandYaw = MakeHandSpin(-90, 90, 0.5, 0, "Tilt", "Wrist tilt");
            _spinLeftHandRoll = MakeHandSpin(-180, 180, 0.5, 0, "Roll", "Forearm roll");
            _spinLeftHandScale = MakeHandSpin(0.5, 1.8, 0.01, 1.0, "Size", "Hand scale");
            leftHandGrid.AddChild(_spinLeftHandPitch); leftHandGrid.AddChild(_spinLeftHandYaw);
            leftHandGrid.AddChild(_spinLeftHandRoll); leftHandGrid.AddChild(_spinLeftHandScale);
            _boxHandSnapTuning.AddChild(leftHandGrid);

            _chkAutoAlignArms.Toggled += (on) =>
            {
                if (_chkAutoAlignHandOrientation != null) _chkAutoAlignHandOrientation.Disabled = !on;
                if (_boxHandSnapTuning != null) _boxHandSnapTuning.Visible = on;
            };

            _boxDeformGuardsCard = customCard;
            _chkClampLimbStretching = AddDescribedCheckBox("Clamp Limb Stretching (1.5 cm max)", "Prevents melee/movement animations from pulling elbows/wrists apart.", "Clamps limb stretch.", true, customBody);
            _chkStabilizeNeckFloatingHead = AddDescribedCheckBox("Stabilize Neck (Floating-Head Heroes)", "Anchors head & neck to collar pivot for heroes like Paradox.", "Stabilizes floating-head necks.", _heroCodename == "chrono", customBody);
            _chkAutoCounterTwistWeights = AddDescribedCheckBox("Auto Counter-Twist Thigh & Shoulder Weights", "Distributes proximal twist weights onto *_TWIST bones.", "Procedural counter-twist distribution.", true, customBody);

            // Hands & Fingers Card (Pre-Rigged Models)
            var (fingerCard, fingerBody) = CreateInspectorCard("Hands & Fingers (Pre-Rigged)", "Choose rigid mitten hands or remap existing finger bones onto Deadlock.");
            _boxFingerCard = fingerCard;
            handsBox.AddChild(fingerCard);

            _optFingerRigMode = new OptionButton { Visible = false };
            _optFingerRigMode.AddItem("Rigid mitten", (int)FingerRigMode.RigidHand);
            _optFingerRigMode.AddItem("Remap Deadlock fingers", (int)FingerRigMode.DeadlockFingerBones);
            _optFingerRigMode.AddItem("Generate fingers", (int)FingerRigMode.GenerateFingerChains);
            _optFingerRigMode.Select(1);
            _optFingerRigMode.ItemSelected += OnFingerRigModeChanged;
            fingerBody.AddChild(_optFingerRigMode);

            _optFingerPosePreset = new OptionButton { Visible = false };
            _optFingerPosePreset.AddItem("Rest", (int)FingerPosePreset.Rest);
            _optFingerPosePreset.AddItem("Fist", (int)FingerPosePreset.Fist);
            _optFingerPosePreset.AddItem("Open", (int)FingerPosePreset.Open);
            _optFingerPosePreset.AddItem("Point", (int)FingerPosePreset.Point);
            fingerBody.AddChild(_optFingerPosePreset);

            _chkKeepFingersRigid = new CheckBox { ButtonPressed = false, Visible = false };
            fingerBody.AddChild(_chkKeepFingersRigid);

            var segRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            segRow.AddThemeConstantOverride("separation", 6);
            string[] segLabels = { "Rigid Mitten", "Remap Deadlock Fingers" };
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                var segBtn = new Button
                {
                    Text = segLabels[i],
                    ThemeTypeVariation = "ToolbarButton",
                    ToggleMode = true,
                    ButtonPressed = i == 1,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    CustomMinimumSize = new Vector2(0, 36)
                };
                ImporterStyle.ApplyFontSize(segBtn, ImporterStyle.FontHint);
                segBtn.Pressed += () =>
                {
                    _optFingerRigMode.Select(idx);
                    OnFingerRigModeChanged(idx);
                };
                _fingerSegButtons[i] = segBtn;
                segRow.AddChild(segBtn);
            }
            fingerBody.AddChild(segRow);

            var lblPoseStrip = new Label { Text = "Live Finger Pose Test:" };
            ImporterStyle.ApplyHintStyle(lblPoseStrip);
            fingerBody.AddChild(lblPoseStrip);

            var poseStrip = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            poseStrip.AddThemeConstantOverride("separation", 6);
            (string label, FingerPosePreset preset)[] poseDefs =
            {
                ("Rest", FingerPosePreset.Rest),
                ("Fist", FingerPosePreset.Fist),
                ("Open", FingerPosePreset.Open),
                ("Point", FingerPosePreset.Point)
            };
            for (int i = 0; i < poseDefs.Length; i++)
            {
                int idx = i;
                var preset = poseDefs[i].preset;
                var pBtn = new Button
                {
                    Text = poseDefs[i].label,
                    ThemeTypeVariation = "ToolbarButton",
                    ToggleMode = true,
                    ButtonPressed = i == 0,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    CustomMinimumSize = new Vector2(0, 34)
                };
                ImporterStyle.ApplyFontSize(pBtn, ImporterStyle.FontHint);
                pBtn.Pressed += () =>
                {
                    _optFingerPosePreset.Select(idx);
                    for (int k = 0; k < _fingerPoseButtons.Length; k++)
                        _fingerPoseButtons[k]?.SetPressedNoSignal(k == idx);
                    if (_previewHeroSkeleton != null)
                        CustomFingerAutoRigger.ApplyFingerPosePreset(_previewHeroSkeleton, preset);
                };
                _fingerPoseButtons[i] = pBtn;
                poseStrip.AddChild(pBtn);
            }
            fingerBody.AddChild(poseStrip);

            return (poseFitBox, handsBox);
        }

        private Control BuildTab2FooterLinksAndKnn()
        {
            var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", ImporterStyle.Gap);

            // Quick jump buttons to Tab 3 (Weights & Physics)
            var jumpRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            jumpRow.AddThemeConstantOverride("separation", 8);

            var btnJumpWeights = new Button
            {
                Text = "Paint Weights →",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(0, 38),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Opens Tab 3 directly on the dedicated Weight Paint workspace."
            };
            SetButtonIcon(btnJumpWeights, "res://assets/at-icons/brush.svg", 16);
            ImporterStyle.ApplyFontSize(btnJumpWeights, ImporterStyle.FontHint);
            btnJumpWeights.Pressed += () =>
            {
                if (_rightTabs != null && _rightTabs.GetTabCount() > 2)
                {
                    _rightTabs.CurrentTab = 2;
                    SetBonesSubView(1);
                }
            };
            jumpRow.AddChild(btnJumpWeights);

            _btnRigPhysicsLink = new Button
            {
                Text = "Physics: On → Configure",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(0, 38),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Opens Tab 3 directly on Physics & Valve Presets."
            };
            SetButtonIcon(_btnRigPhysicsLink, "res://assets/at-icons/sliders.svg", 16);
            ImporterStyle.ApplyFontSize(_btnRigPhysicsLink, ImporterStyle.FontHint);
            _btnRigPhysicsLink.Pressed += () =>
            {
                if (_rightTabs != null && _rightTabs.GetTabCount() > 2)
                {
                    _rightTabs.CurrentTab = 2;
                    SetBonesSubView(2);
                }
            };
            jumpRow.AddChild(_btnRigPhysicsLink);
            box.AddChild(jumpRow);

            // Collapsed Legacy KNN Proximity Transfer option
            var (knnFold, knnFoldBody) = CreateInspectorCard(
                "Legacy KNN Proximity Transfer",
                "Optional fallback weight transfer for non-humanoid meshes.",
                collapsible: true,
                startExpanded: false);
            box.AddChild(knnFold);

            _btnUseKnnMethod = new Button
            {
                Text = "Use Legacy KNN Proximity Transfer",
                ThemeTypeVariation = "ImporterSecondaryButton",
                ToggleMode = true,
                CustomMinimumSize = new Vector2(0, 34),
                TooltipText = "Fallback proximity weight transfer for non-humanoids."
            };
            ImporterStyle.ApplyFontSize(_btnUseKnnMethod, ImporterStyle.FontHint);
            _btnUseKnnMethod.Pressed += () => SelectRiggingMethodById((int)CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection);
            knnFoldBody.AddChild(_btnUseKnnMethod);

            var knnBody = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            knnBody.AddThemeConstantOverride("separation", 6);
            _boxKnnParameters = knnBody;

            var lblKnnR = new Label { Text = "KNN Donor Search Radius (meters):" };
            ImporterStyle.ApplyHintStyle(lblKnnR);
            knnBody.AddChild(lblKnnR);

            var knnHBox = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _spinKnnRadius = new SpinBox { MinValue = 0.05, MaxValue = 0.80, Step = 0.01, Value = 0.20, CustomMinimumSize = new Vector2(88, 0) };
            _sliderKnnRadius = new HSlider { MinValue = 0.05, MaxValue = 0.80, Step = 0.01, Value = 0.20, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            _spinKnnRadius.ValueChanged += (v) => { if (!Mathf.IsEqualApprox((float)_sliderKnnRadius.Value, (float)v)) _sliderKnnRadius.Value = v; };
            _sliderKnnRadius.ValueChanged += (v) => { if (!Mathf.IsEqualApprox((float)_spinKnnRadius.Value, (float)v)) _spinKnnRadius.Value = v; };
            knnHBox.AddChild(_spinKnnRadius); knnHBox.AddChild(_sliderKnnRadius);
            knnBody.AddChild(knnHBox);

            var lblLap = new Label { Text = "Laplacian Smoothing Iterations (0..5):" };
            ImporterStyle.ApplyHintStyle(lblLap);
            knnBody.AddChild(lblLap);
            _spinLaplacian = new SpinBox { MinValue = 0, MaxValue = 5, Step = 1, Value = 3 };
            knnBody.AddChild(_spinLaplacian);

            _chkExcludeClothHair = new CheckBox { Text = "Exclude Hero Cloth & Hair Donor Bones", ButtonPressed = true, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            ImporterStyle.ApplyFontSize(_chkExcludeClothHair, ImporterStyle.FontHint);
            knnBody.AddChild(_chkExcludeClothHair);
            knnFoldBody.AddChild(knnBody);

            return box;
        }

        private void OnBindReferenceRigPressed()
        {
            EnsureReferenceRigInitialized(false);
            _isEditReferenceBonesMode = false;
            if (_isGizmoEditingRefJoint)
            {
                _isGizmoEditingRefJoint = false;
                UpdateGizmoTargetTransform();
            }
            if (!(_chkKeepPaintedWeightsOnRebind?.ButtonPressed ?? false))
            {
                _customSkinOverrides.Clear();
                _baselineSkinOverrides.Clear();
                _paintSurfaces.Clear();
                _strokeDirty = false;
                _weightUndoStack.Clear();
                _weightRedoStack.Clear();
                UpdatePaintOverrideLabel();
                UpdateWeightHistoryUI();
            }
            SyncEditBindButtonVisuals();
            ExecuteLiveRigUpdate();
            // Auto-advance Tab 2 sub-nav to Pose Fit so the user naturally sees the next step after binding!
            if (GetCurrentRiggingMethod() == CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig)
            {
                SetTab2SubView(3);
            }
            SetBottomStatus("Bound mesh to modular Reference Rig. Adjust Pose Fit or refine in Tab 3 (Weights & Physics).", StatusTone.Success);
        }

        private List<string> GetActiveImportedSubmeshNamesForCloth()
        {
            var names = new List<string>();
            if (_liveImportedScene == null || !GodotObject.IsInstanceValid(_liveImportedScene)) return names;
            var meshes = FilterActiveImportedMeshes(CustomModelAutoRigger.CollectMeshes(_liveImportedScene));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mi in meshes)
            {
                string nm = mi?.Name.ToString() ?? "";
                if (!string.IsNullOrEmpty(nm) && seen.Add(nm)) names.Add(nm);
            }
            return names;
        }

        private void PopulateClothTargetMeshDropdown(OptionButton opt, List<string> meshNames, string selectedMesh)
        {
            if (opt == null) return;
            opt.Clear();
            opt.AddItem("Auto (Mesh Island)", 0);
            int selIdx = 0;
            for (int i = 0; i < meshNames.Count; i++)
            {
                opt.AddItem(meshNames[i], i + 1);
                if (!string.IsNullOrEmpty(selectedMesh) && string.Equals(meshNames[i], selectedMesh, StringComparison.OrdinalIgnoreCase))
                    selIdx = i + 1;
            }
            opt.Select(selIdx);
        }

        private void OnAddReferenceClothChainPressed()
        {
            EnsureReferenceRigInitialized(false);
            string parentBone = _optRefClothParent != null && _optRefClothParent.Selected >= 0
                ? _optRefClothParent.GetItemText(_optRefClothParent.Selected)
                : "spine_3";
            string rawName = string.IsNullOrWhiteSpace(_txtRefClothName?.Text)
                ? $"cloth_{(_referenceRig.ClothChains?.Count ?? 0) + 1}"
                : _txtRefClothName.Text.Trim();
            int segs = (int)(_spinRefClothSegments?.Value ?? 2);
            float radiusMeters = (float)((_spinRefClothRadius?.Value ?? 8.0) * 0.01);
            string targetMesh = (_optRefClothMesh != null && _optRefClothMesh.Selected > 0)
                ? _optRefClothMesh.GetItemText(_optRefClothMesh.Selected)
                : "";
            int dirId = _optRefClothDir != null && _optRefClothDir.Selected >= 0 ? _optRefClothDir.GetItemId(_optRefClothDir.Selected) : 0;

            Vector3 dir = dirId switch
            {
                1 => new Vector3(0f, -0.35f, -0.94f),
                2 => new Vector3(0.70f, -0.70f, 0f),
                3 => new Vector3(-0.70f, -0.70f, 0f),
                _ => Vector3.Down
            };

            // Resolve parent anchor position in model space ("Chin" supersedes legacy "Head")
            Vector3 parentPos = _referenceRig.Joints.TryGetValue("Chest", out var cp) ? cp : Vector3.Zero;
            if (parentBone == "head")
            {
                if (_referenceRig.Joints.TryGetValue("Chin", out var chp)) parentPos = chp;
                else if (_referenceRig.Joints.TryGetValue("Head", out var hp)) parentPos = hp;
                else if (_referenceRig.Joints.TryGetValue("Neck", out var np)) parentPos = np;
            }
            else if (parentBone == "pelvis" && _referenceRig.Joints.TryGetValue("Pelvis", out var pp)) parentPos = pp;
            else if (parentBone == "clavicle_L" && _referenceRig.Joints.TryGetValue("ShoulderL", out var sl)) parentPos = sl;
            else if (parentBone == "clavicle_R" && _referenceRig.Joints.TryGetValue("ShoulderR", out var sr)) parentPos = sr;
            else if (parentBone == "hand_L" && _referenceRig.Joints.TryGetValue("WristL", out var wl)) parentPos = wl;
            else if (parentBone == "hand_R" && _referenceRig.Joints.TryGetValue("WristR", out var wr)) parentPos = wr;

            float h = 1.7f;
            if ((_referenceRig.Joints.TryGetValue("Chin", out var headP) || _referenceRig.Joints.TryGetValue("Head", out headP)) &&
                _referenceRig.Joints.TryGetValue("AnkleL", out var ankP))
                h = MathF.Max(0.2f, MathF.Abs(headP.Y - ankP.Y) / 0.84f);

            var chain = ReferenceRigDef.CreateDefaultClothChain(
                rawName, parentBone, parentPos, dir, segs,
                totalLength: 0.24f * h,
                influenceRadius: radiusMeters,
                targetMeshName: targetMesh);
            _referenceRig.ClothChains.Add(chain);
            // Ensure custom physics is active so authored cloth chains are preserved and appended on Bind
            if (_chkEnableCustomPhysics != null && !_chkEnableCustomPhysics.ButtonPressed)
            {
                _chkEnableCustomPhysics.SetPressedNoSignal(true);
                _chkPhysicsMasterTab3?.SetPressedNoSignal(true);
                UpdateRigPhysicsLinkText();
            }
            _isEditReferenceBonesMode = true;
            _selectedPin = $"cloth::{chain.ChainId}::0";
            SyncEditBindButtonVisuals();
            RefreshReferenceClothListUI();
            ApplyLandmarkPreviewState();
            AttachGizmoToSelectedReferenceJoint();
            _pinsDirtyNeedsRigRebuild = true;
            SetBottomStatus($"Added cloth chain '{chain.Name}' on {parentBone} (radius {radiusMeters * 100f:F0} cm). Drag joints in 3D, then click Bind.", StatusTone.Info);
        }

        private void RefreshReferenceClothListUI()
        {
            var activeMeshes = GetActiveImportedSubmeshNamesForCloth();
            if (_optRefClothMesh != null && GodotObject.IsInstanceValid(_optRefClothMesh))
            {
                string prevSel = _optRefClothMesh.Selected > 0 ? _optRefClothMesh.GetItemText(_optRefClothMesh.Selected) : "";
                PopulateClothTargetMeshDropdown(_optRefClothMesh, activeMeshes, prevSel);
            }

            if (_boxRefClothList == null) return;
            foreach (Node ch in _boxRefClothList.GetChildren()) ch.QueueFree();
            if (_referenceRig?.ClothChains == null || _referenceRig.ClothChains.Count == 0)
            {
                var empty = new Label { Text = "No cloth chains authored yet.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
                ImporterStyle.ApplyHintStyle(empty);
                _boxRefClothList.AddChild(empty);
                return;
            }
            foreach (var cc in _referenceRig.ClothChains.ToList())
            {
                var captured = cc;
                var card = new PanelContainer
                {
                    ThemeTypeVariation = "SubPanelContainer",
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };
                var pad = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                pad.AddThemeConstantOverride("margin_left", 6);
                pad.AddThemeConstantOverride("margin_right", 6);
                pad.AddThemeConstantOverride("margin_top", 5);
                pad.AddThemeConstantOverride("margin_bottom", 5);
                card.AddChild(pad);

                var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                col.AddThemeConstantOverride("separation", 4);
                pad.AddChild(col);

                // Card Row 1: Select Chain + Mirror + Delete (zero right-edge clipping)
                var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                row.AddThemeConstantOverride("separation", 4);

                var btnSel = new Button
                {
                    Text = $"{cc.Name} · {cc.ParentBoneName} ({Math.Max(0, cc.JointPositions.Count - 1)}b)",
                    Alignment = HorizontalAlignment.Left,
                    ClipText = true,
                    TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                    ThemeTypeVariation = "ToolbarButton",
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    CustomMinimumSize = new Vector2(0, 28)
                };
                ImporterStyle.ApplyFontSize(btnSel, ImporterStyle.FontHint);
                btnSel.Pressed += () =>
                {
                    _isEditReferenceBonesMode = true;
                    _selectedPin = $"cloth::{captured.ChainId}::0";
                    SyncEditBindButtonVisuals();
                    ApplyLandmarkPreviewState();
                    AttachGizmoToSelectedReferenceJoint();
                };
                row.AddChild(btnSel);

                var btnMir = new Button
                {
                    Text = "Mirror",
                    ThemeTypeVariation = "ToolbarButton",
                    CustomMinimumSize = new Vector2(54, 28),
                    TooltipText = "Create a mirrored L↔R copy of this cloth chain."
                };
                ImporterStyle.ApplyFontSize(btnMir, ImporterStyle.FontHint);
                btnMir.Pressed += () =>
                {
                    var mirrored = _referenceRig.CreateMirroredClothChain(captured);
                    if (mirrored != null)
                    {
                        _referenceRig.ClothChains.Add(mirrored);
                        RefreshReferenceClothListUI();
                        RefreshPinVisuals();
                        _pinsDirtyNeedsRigRebuild = true;
                    }
                };
                row.AddChild(btnMir);

                var btnDel = new Button
                {
                    Text = "✕",
                    ThemeTypeVariation = "ToolbarButton",
                    CustomMinimumSize = new Vector2(28, 28),
                    TooltipText = "Delete this cloth chain."
                };
                ImporterStyle.ApplyFontSize(btnDel, ImporterStyle.FontHint);
                btnDel.Pressed += () =>
                {
                    _referenceRig.ClothChains.Remove(captured);
                    if (_selectedPin != null && _selectedPin.StartsWith($"cloth::{captured.ChainId}::", StringComparison.Ordinal))
                        _selectedPin = null;
                    RefreshReferenceClothListUI();
                    RefreshPinVisuals();
                    AttachGizmoToSelectedReferenceJoint();
                    _pinsDirtyNeedsRigRebuild = true;
                };
                row.AddChild(btnDel);
                col.AddChild(row);

                // Card Row 2: Inline Radius (cm) + Target Mesh filter (click Re-Bind after tweaking)
                var tuneRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                tuneRow.AddThemeConstantOverride("separation", 4);

                var spinRad = new SpinBox
                {
                    MinValue = 2,
                    MaxValue = 30,
                    Step = 1,
                    Value = Math.Round(Math.Clamp(captured.InfluenceRadius, 0.02f, 0.30f) * 100.0),
                    Prefix = "R:",
                    Suffix = "cm",
                    CustomMinimumSize = new Vector2(96, 26),
                    TooltipText = "Influence radius in cm. Click Re-Bind to apply."
                };
                spinRad.ValueChanged += (v) =>
                {
                    captured.InfluenceRadius = Math.Clamp((float)(v * 0.01), 0.02f, 0.35f);
                    _pinsDirtyNeedsRigRebuild = true;
                };
                tuneRow.AddChild(spinRad);

                var optMesh = new OptionButton
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    CustomMinimumSize = new Vector2(0, 26),
                    TooltipText = "Restrict this cloth chain to a specific submesh, or Auto (Mesh Island)."
                };
                ConfigureOptionButton(optMesh);
                PopulateClothTargetMeshDropdown(optMesh, activeMeshes, captured.TargetMeshName);
                optMesh.ItemSelected += (idx) =>
                {
                    captured.TargetMeshName = idx > 0 ? optMesh.GetItemText((int)idx) : "";
                    _pinsDirtyNeedsRigRebuild = true;
                };
                tuneRow.AddChild(optMesh);
                col.AddChild(tuneRow);

                _boxRefClothList.AddChild(card);
            }
        }

        private void SyncEditBindButtonVisuals()
        {
            if (_btnEditRefBonesMode != null)
            {
                _btnEditRefBonesMode.SetPressedNoSignal(_isEditReferenceBonesMode);
                _btnEditRefBonesMode.ThemeTypeVariation = _isEditReferenceBonesMode ? "ImporterPrimaryButton" : "ToolbarButton";
            }
            if (_btnBindReferenceRig != null)
            {
                _btnBindReferenceRig.Text = _previewRiggedContainer != null && GodotObject.IsInstanceValid(_previewRiggedContainer)
                    ? "Re-Bind Mesh"
                    : "Bind Mesh";
            }
        }

        private void SetTab2SubView(int idx)
        {
            _tab2SubView = Math.Clamp(idx, 0, 3);
            var method = GetCurrentRiggingMethod();
            bool supportsPins = method == CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig ||
                                (method == CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig &&
                                 _partialBodyEngine == CustomModelAutoRigger.PartialBodyEngine.SemiBodyPins);
            if (supportsPins)
            {
                if (_tab2SubView == 1)
                {
                    _isEditReferenceBonesMode = true;
                    EnsureReferenceRigInitialized(false);
                }
                else if (_tab2SubView == 0 || _tab2SubView == 3)
                {
                    _isEditReferenceBonesMode = false;
                }
                SyncPartialBodyUiState();
                ApplyLandmarkPreviewState();
            }
            UpdateTab2SectionVisibility();
        }

        private void SnapReferenceFingersToCurrentMesh()
        {
            if (_liveImportedScene == null || !GodotObject.IsInstanceValid(_liveImportedScene)) return;
            EnsureReferenceRigInitialized(false);
            var meshes = FilterActiveImportedMeshes(CustomModelAutoRigger.CollectMeshes(_liveImportedScene));
            _referenceRig.SnapFingersToMesh(_liveImportedScene, meshes, forceRegenerate: true);
            RefreshPinVisuals();
            _pinsDirtyNeedsRigRebuild = true;
            SetBottomStatus("Snapped finger joints to hand mesh geometry.", StatusTone.Success);
        }

        private static ImageTexture _hudChkUncheckedTex;
        private static ImageTexture _hudChkCheckedTex;
        private static StyleBoxFlat _hudSubtleActiveStyle;
        private static StyleBoxFlat _hudFloatingPanelStyle;

        private static (ImageTexture Unchecked, ImageTexture Checked) GetHudCheckboxTextures()
        {
            if (_hudChkUncheckedTex == null || _hudChkCheckedTex == null)
            {
                const int sz = 14;
                var imgOff = Image.CreateEmpty(sz, sz, false, Image.Format.Rgba8);
                var imgOn = Image.CreateEmpty(sz, sz, false, Image.Format.Rgba8);
                Color bgOff = new Color(0.08f, 0.10f, 0.09f, 0.96f);
                Color borderOff = new Color(0.46f, 0.56f, 0.52f, 0.95f);
                Color bgOn = new Color(0.42f, 0.64f, 0.54f, 1.0f);
                Color borderOn = new Color(0.62f, 0.84f, 0.72f, 1.0f);
                Color checkCol = new Color(0.06f, 0.09f, 0.08f, 1.0f);

                for (int y = 0; y < sz; y++)
                {
                    for (int x = 0; x < sz; x++)
                    {
                        bool corner = (x == 0 || x == sz - 1) && (y == 0 || y == sz - 1);
                        if (corner)
                        {
                            imgOff.SetPixel(x, y, Colors.Transparent);
                            imgOn.SetPixel(x, y, Colors.Transparent);
                            continue;
                        }
                        bool edge = x == 0 || y == 0 || x == sz - 1 || y == sz - 1;
                        imgOff.SetPixel(x, y, edge ? borderOff : bgOff);
                        imgOn.SetPixel(x, y, edge ? borderOn : bgOn);
                    }
                }
                // Draw crisp 2px checkmark on checked icon
                (int X, int Y)[] mark =
                {
                    (3, 7), (4, 8), (5, 9), (6, 8), (7, 7), (8, 6), (9, 5), (10, 4),
                    (3, 8), (4, 9), (5, 10), (6, 9), (7, 8), (8, 7), (9, 6), (10, 5)
                };
                foreach (var (mx, my) in mark)
                    if (mx >= 1 && mx < sz - 1 && my >= 1 && my < sz - 1) imgOn.SetPixel(mx, my, checkCol);

                _hudChkUncheckedTex = ImageTexture.CreateFromImage(imgOff);
                _hudChkCheckedTex = ImageTexture.CreateFromImage(imgOn);
            }
            return (_hudChkUncheckedTex, _hudChkCheckedTex);
        }

        private static StyleBoxFlat GetHudSubtleActiveStyle()
        {
            if (_hudSubtleActiveStyle == null)
            {
                _hudSubtleActiveStyle = new StyleBoxFlat
                {
                    BgColor = new Color(0.17f, 0.27f, 0.23f, 0.96f),
                    BorderColor = new Color(0.48f, 0.69f, 0.59f, 0.95f),
                    BorderWidthLeft = 1,
                    BorderWidthRight = 1,
                    BorderWidthTop = 1,
                    BorderWidthBottom = 1,
                    CornerRadiusTopLeft = 5,
                    CornerRadiusTopRight = 5,
                    CornerRadiusBottomLeft = 5,
                    CornerRadiusBottomRight = 5,
                    ContentMarginLeft = 6,
                    ContentMarginRight = 6,
                    ContentMarginTop = 2,
                    ContentMarginBottom = 2
                };
            }
            return _hudSubtleActiveStyle;
        }

        private static StyleBoxFlat GetHudFloatingPanelStyle()
        {
            if (_hudFloatingPanelStyle == null)
            {
                _hudFloatingPanelStyle = new StyleBoxFlat
                {
                    BgColor = new Color(0.06f, 0.08f, 0.07f, 0.86f),
                    BorderColor = new Color(0.28f, 0.35f, 0.32f, 0.78f),
                    BorderWidthLeft = 1,
                    BorderWidthRight = 1,
                    BorderWidthTop = 1,
                    BorderWidthBottom = 1,
                    CornerRadiusTopLeft = 8,
                    CornerRadiusTopRight = 8,
                    CornerRadiusBottomLeft = 8,
                    CornerRadiusBottomRight = 8
                };
            }
            return _hudFloatingPanelStyle;
        }

        private static void ApplyHudSubtleToggleVisual(Button btn, bool active)
        {
            if (btn == null) return;
            btn.SetPressedNoSignal(active);
            btn.ThemeTypeVariation = "ToolbarButton";
            if (active)
            {
                var st = GetHudSubtleActiveStyle();
                btn.AddThemeStyleboxOverride("normal", st);
                btn.AddThemeStyleboxOverride("pressed", st);
                btn.AddThemeStyleboxOverride("hover", st);
                btn.AddThemeStyleboxOverride("hover_pressed", st);
                btn.AddThemeColorOverride("font_color", PlaygroundThemeHelper.Parchment);
                btn.AddThemeColorOverride("font_pressed_color", PlaygroundThemeHelper.Parchment);
            }
            else
            {
                btn.RemoveThemeStyleboxOverride("normal");
                btn.RemoveThemeStyleboxOverride("pressed");
                btn.RemoveThemeStyleboxOverride("hover");
                btn.RemoveThemeStyleboxOverride("hover_pressed");
                btn.RemoveThemeColorOverride("font_color");
                btn.RemoveThemeColorOverride("font_pressed_color");
            }
        }

        // Shared 3D Viewport Floating HUD Helpers (reused by Auto-Rig Reference Bones & Synthetic Cloth Chains)
        private PanelContainer CreateViewportFloatingTopHud(Control viewportPanel, out HBoxContainer row)
        {
            var topOverlayMargin = new MarginContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            topOverlayMargin.AddThemeConstantOverride("margin_left", 8);
            topOverlayMargin.AddThemeConstantOverride("margin_right", 8);
            topOverlayMargin.AddThemeConstantOverride("margin_top", 8);
            topOverlayMargin.AddThemeConstantOverride("margin_bottom", 8);
            viewportPanel.AddChild(topOverlayMargin);

            var topHud = new PanelContainer
            {
                Visible = false,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
                MouseFilter = Control.MouseFilterEnum.Stop
            };
            topHud.AddThemeStyleboxOverride("panel", GetHudFloatingPanelStyle());
            topOverlayMargin.AddChild(topHud);

            var topPad = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            topPad.AddThemeConstantOverride("margin_left", 10);
            topPad.AddThemeConstantOverride("margin_right", 8);
            topPad.AddThemeConstantOverride("margin_top", 5);
            topPad.AddThemeConstantOverride("margin_bottom", 5);
            topHud.AddChild(topPad);

            row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", 6);
            topPad.AddChild(row);
            return topHud;
        }

        private static Button CreateViewportHudButton(string text, int minW, int minH = 26, bool toggle = false, bool pressed = false, string variation = "ToolbarButton", string tip = "")
        {
            var btn = new Button
            {
                Text = text,
                ToggleMode = toggle,
                ButtonPressed = pressed,
                ThemeTypeVariation = variation,
                CustomMinimumSize = new Vector2(minW, minH),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = tip
            };
            ImporterStyle.ApplyFontSize(btn, ImporterStyle.FontHint);
            return btn;
        }

        private static VSeparator CreateViewportHudSeparator() => new VSeparator
        {
            CustomMinimumSize = new Vector2(2, 18),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };

        private void BuildReferenceRigViewportHud(Control viewportPanel)
        {
            if (viewportPanel == null) return;

            var (texUnchecked, texChecked) = GetHudCheckboxTextures();

            Button MakeHudBtn(string text, int minW, int minH = 26, bool toggle = false, bool pressed = false, string variation = "ToolbarButton", string tip = "")
                => CreateViewportHudButton(text, minW, minH, toggle, pressed, variation, tip);

            CheckBox MakeHudCheckBox(string text, bool pressed, string tip = "")
            {
                var chk = new CheckBox
                {
                    Text = text,
                    ButtonPressed = pressed,
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                    FocusMode = Control.FocusModeEnum.None,
                    TooltipText = tip
                };
                chk.AddThemeIconOverride("unchecked", texUnchecked);
                chk.AddThemeIconOverride("checked", texChecked);
                chk.AddThemeConstantOverride("h_separation", 5);
                chk.AddThemeColorOverride("font_color", new Color(0.84f, 0.87f, 0.84f));
                chk.AddThemeColorOverride("font_pressed_color", PlaygroundThemeHelper.Parchment);
                ImporterStyle.ApplyFontSize(chk, ImporterStyle.FontHint);
                return chk;
            }

            VSeparator MakeHudSep() => CreateViewportHudSeparator();

            // =========================================================
            // 1. TOP SLIM BAR (Frosted translucent floating bar, clear logical groups)
            // =========================================================
            _refRigViewportHud = CreateViewportFloatingTopHud(viewportPanel, out var row1);

            // Left: Step Prompt + Prev/Next right beside it!
            _lblRefHudSelectedBone = new Label
            {
                Text = "Drag any joint to adjust",
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                AutowrapMode = TextServer.AutowrapMode.Off
            };
            ImporterStyle.ApplyFontSize(_lblRefHudSelectedBone, ImporterStyle.FontHint);
            _lblRefHudSelectedBone.AddThemeColorOverride("font_color", PlaygroundThemeHelper.Parchment);
            row1.AddChild(_lblRefHudSelectedBone);

            _btnRefHudGuidedPrev = MakeHudBtn("← Prev", 56, 26, tip: "Go back to the previous landmark step.");
            _btnRefHudGuidedPrev.Visible = false;
            _btnRefHudGuidedPrev.Pressed += PreviousGuidedPinStep;
            row1.AddChild(_btnRefHudGuidedPrev);

            _btnRefHudGuidedNext = MakeHudBtn("Next →", 66, 26, variation: "ImporterPrimaryButton", tip: "Confirm this landmark and advance to the next step (Enter).");
            _btnRefHudGuidedNext.Visible = false;
            _btnRefHudGuidedNext.Pressed += AdvanceGuidedPinStep;
            row1.AddChild(_btnRefHudGuidedNext);

            var spacer = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row1.AddChild(spacer);

            // Group A: History (Undo / Redo)
            _btnRefHudUndo = MakeHudBtn("", 26, 26, tip: "Undo last reference rig edit (Ctrl+Z)");
            SetButtonIcon(_btnRefHudUndo, "res://assets/at-icons/undo.svg", 14);
            _btnRefHudUndo.Disabled = true;
            _btnRefHudUndo.Pressed += UndoReferenceRigEdit;
            row1.AddChild(_btnRefHudUndo);

            _btnRefHudRedo = MakeHudBtn("", 26, 26, tip: "Redo reference rig edit (Ctrl+Y / Ctrl+Shift+Z)");
            SetButtonIcon(_btnRefHudRedo, "res://assets/at-icons/redo.svg", 14);
            _btnRefHudRedo.Disabled = true;
            _btnRefHudRedo.Pressed += RedoReferenceRigEdit;
            row1.AddChild(_btnRefHudRedo);

            row1.AddChild(MakeHudSep());

            // Group B: Placement Mode
            _btnRefHudGuidedPlace = MakeHudBtn("1-by-1", 56, 26, toggle: true, pressed: false, tip: "Auto-Rig Pro Smart mode: click 7 landmarks (Chin, Neck, Shoulder, Wrist, Hand Tip, Pelvis, Ankle) directly on the mesh.");
            _btnRefHudGuidedPlace.Toggled += (on) =>
            {
                if (on) StartGuidedOneByOnePinPlacement(clearExisting: true);
                else { _isGuidedPinPlacement = false; _guidedPlacedPinKeys.Clear(); EnsureReferenceRigInitialized(false); RefreshPinVisuals(); SyncReferenceRigViewportHud(); }
            };
            row1.AddChild(_btnRefHudGuidedPlace);

            var btnHudAutoPlace = MakeHudBtn("Auto-Pin", 64, 26, tip: "Automatically estimate all reference pins from the mesh silhouette.");
            btnHudAutoPlace.Pressed += () =>
            {
                PushReferenceRigUndoSnapshot();
                _isGuidedPinPlacement = false;
                _guidedPlacedPinKeys.Clear();
                EnsureReferenceRigInitialized(true);
                _userMovedLandmarkPins = true;
                RefreshPinVisuals();
                SyncReferenceRigViewportHud();
                SetBottomStatus("Auto-placed all reference pins from mesh silhouette.", StatusTone.Info);
            };
            row1.AddChild(btnHudAutoPlace);

            _btnRefHudMirror = MakeHudBtn("Mirror", 52, 26, toggle: true, pressed: true, tip: "Mirror L↔R joint edits across X.");
            _btnRefHudMirror.Toggled += (on) =>
            {
                if (_chkMirrorPins != null && _chkMirrorPins.ButtonPressed != on) _chkMirrorPins.ButtonPressed = on;
                if (_btnMirrorPinsToggle != null) _btnMirrorPinsToggle.SetPressedNoSignal(on);
                ApplyHudSubtleToggleVisual(_btnRefHudMirror, on);
            };
            row1.AddChild(_btnRefHudMirror);

            row1.AddChild(MakeHudSep());

            // Group C: Camera Focus
            var btnHudFL = MakeHudBtn("Hand L", 54, 26, tip: "Focus camera on Left Hand.");
            var btnHudFR = MakeHudBtn("Hand R", 54, 26, tip: "Focus camera on Right Hand.");
            var btnHudCenter = MakeHudBtn("Center", 56, 26, tip: "Re-center camera on character (Front view).");
            btnHudFL.Pressed += () => FocusCameraOnReferenceHand(true);
            btnHudFR.Pressed += () => FocusCameraOnReferenceHand(false);
            btnHudCenter.Pressed += () => SetCameraPreset("Front");
            row1.AddChild(btnHudFL);
            row1.AddChild(btnHudFR);
            row1.AddChild(btnHudCenter);

            row1.AddChild(MakeHudSep());

            // Group C2: Compact 3-state Original Hero Mesh visibility toggle (Show | Ghost | Hide, shortcut: H)
            var lblHeroVis = new Label
            {
                Text = "Hero:",
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                AutowrapMode = TextServer.AutowrapMode.Off,
                TooltipText = "Original hero mesh visibility while editing pins (Shortcut: H)"
            };
            ImporterStyle.ApplyHintStyle(lblHeroVis, wrap: false);
            row1.AddChild(lblHeroVis);

            string[] heroVisLabels = { "Show", "Ghost", "Hide" };
            string[] heroVisTips =
            {
                "Show original hero mesh in full color (Shortcut: H)",
                "Show original hero mesh as translucent ghost silhouette (Shortcut: H)",
                "Hide original hero mesh completely for unobstructed pin editing (Shortcut: H)"
            };
            for (int hv = 0; hv < 3; hv++)
            {
                int modeIdx = hv;
                var hb = MakeHudBtn(heroVisLabels[hv], hv == 1 ? 44 : 40, 24, toggle: true, pressed: hv == _pinEditHeroVisMode, tip: heroVisTips[hv]);
                hb.Pressed += () => SetPinEditHeroVisMode(modeIdx);
                _hudHeroVisBtns[hv] = hb;
                row1.AddChild(hb);
            }

            row1.AddChild(MakeHudSep());

            // Group D: Primary Action (Bind — highlighted green only once placement is ready!)
            _btnRefHudBind = MakeHudBtn("Bind", 58, 26, variation: "ImporterPrimaryButton", tip: "Compute geodesic weights and bind the character to Deadlock.");
            _btnRefHudBind.Pressed += OnBindReferenceRigPressed;
            row1.AddChild(_btnRefHudBind);

            // =========================================================
            // 2. BOTTOM FLOATING PILL BAR (Centered auto-width, translucent, clear checkboxes)
            // =========================================================
            var bottomOverlayMargin = new MarginContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            bottomOverlayMargin.AddThemeConstantOverride("margin_left", 12);
            bottomOverlayMargin.AddThemeConstantOverride("margin_right", 12);
            bottomOverlayMargin.AddThemeConstantOverride("margin_top", 8);
            bottomOverlayMargin.AddThemeConstantOverride("margin_bottom", 48);
            viewportPanel.AddChild(bottomOverlayMargin);

            var bottomHud = new PanelContainer
            {
                Visible = false,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
                SizeFlagsVertical = Control.SizeFlags.ShrinkEnd,
                MouseFilter = Control.MouseFilterEnum.Stop
            };
            bottomHud.AddThemeStyleboxOverride("panel", GetHudFloatingPanelStyle());
            bottomOverlayMargin.AddChild(bottomHud);
            _refRigViewportBottomHud = bottomHud;

            var botPad = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
            botPad.AddThemeConstantOverride("margin_left", 12);
            botPad.AddThemeConstantOverride("margin_right", 12);
            botPad.AddThemeConstantOverride("margin_top", 6);
            botPad.AddThemeConstantOverride("margin_bottom", 6);
            bottomHud.AddChild(botPad);

            var botRoot = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
            botRoot.AddThemeConstantOverride("separation", 5);
            botPad.AddChild(botRoot);

            var lblBotExplain = new Label
            {
                Text = "Reference Rig Options — Configure spine segments, optional limbs & fingers before clicking Bind",
                HorizontalAlignment = HorizontalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.Off,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter
            };
            ImporterStyle.ApplyHintStyle(lblBotExplain, wrap: false);
            botRoot.AddChild(lblBotExplain);

            // Bottom Row A: Spine (1-4) | [✓] Neck [✓] Clav [ ] Toes | [Mitten | Fingers]
            var row2 = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
            row2.AddThemeConstantOverride("separation", 6);

            var lblSp = new Label { Text = "Spine:", AutowrapMode = TextServer.AutowrapMode.Off, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            ImporterStyle.ApplyHintStyle(lblSp, wrap: false);
            row2.AddChild(lblSp);
            for (int sc = 1; sc <= 4; sc++)
            {
                int cap = sc;
                var b = MakeHudBtn(sc.ToString(), 24, 24, toggle: true, pressed: sc == 3);
                b.Pressed += () =>
                {
                    if (_isSyncingRefRigUi) return;
                    PushReferenceRigUndoSnapshot();
                    _referenceRig.SpineCount = cap;
                    SyncReferenceRigControlsFromDef();
                    RefreshPinVisuals();
                    _pinsDirtyNeedsRigRebuild = true;
                };
                _hudSpineBtns[sc - 1] = b;
                row2.AddChild(b);
            }

            row2.AddChild(MakeHudSep());

            _hudChkNeck = MakeHudCheckBox("Neck", true, "Include/exclude neck_0 bone");
            _hudChkClav = MakeHudCheckBox("Clav", true, "Include/exclude clavicle L/R bones");
            _hudChkToes = MakeHudCheckBox("Toes", false, "Include/exclude toe L/R bones");
            _hudChkNeck.Toggled += (on) => { if (!_isSyncingRefRigUi) { PushReferenceRigUndoSnapshot(); _referenceRig.HasNeck = on; SyncReferenceRigViewportHud(); RefreshPinVisuals(); _pinsDirtyNeedsRigRebuild = true; } };
            _hudChkClav.Toggled += (on) => { if (!_isSyncingRefRigUi) { PushReferenceRigUndoSnapshot(); _referenceRig.HasClavicles = on; SyncReferenceRigViewportHud(); RefreshPinVisuals(); _pinsDirtyNeedsRigRebuild = true; } };
            _hudChkToes.Toggled += (on) => { if (!_isSyncingRefRigUi) { PushReferenceRigUndoSnapshot(); _referenceRig.HasToes = on; if (!_isGuidedPinPlacement && _referenceRig.Joints.Count > 0) _referenceRig.EnsureDefaultJoints(); SyncReferenceRigViewportHud(); RefreshPinVisuals(); _pinsDirtyNeedsRigRebuild = true; } };
            row2.AddChild(_hudChkNeck);
            row2.AddChild(_hudChkClav);
            row2.AddChild(_hudChkToes);

            row2.AddChild(MakeHudSep());

            string[] hModes = { "Mitten", "Fingers" };
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                var b = MakeHudBtn(hModes[i], 56, 24, toggle: true, pressed: i == 1);
                b.Pressed += () =>
                {
                    if (_isSyncingRefRigUi) return;
                    PushReferenceRigUndoSnapshot();
                    _referenceRig.FingerMode = (ReferenceFingerRigMode)idx;
                    bool hasWrists = _referenceRig.Joints.ContainsKey("WristL") || _referenceRig.Joints.ContainsKey("WristR");
                    if (!_isGuidedPinPlacement && hasWrists)
                    {
                        _referenceRig.EnsureDefaultJoints();
                        if (idx == 1) SnapReferenceFingersToCurrentMesh();
                    }
                    SyncReferenceRigControlsFromDef();
                    RefreshPinVisuals();
                    _pinsDirtyNeedsRigRebuild = true;
                };
                _hudFingerModeBtns[i] = b;
                row2.AddChild(b);
            }
            botRoot.AddChild(row2);

            // Bottom Row B (shown only when Fingers is active AND 1-by-1 placement is complete)
            _hudFingerSubRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter, Visible = false };
            _hudFingerSubRow.AddThemeConstantOverride("separation", 8);
            (string lbl, Action<bool> set)[] fDefs =
            {
                ("Thumb",  v => _referenceRig.EnableThumb = v),
                ("Index",  v => _referenceRig.EnableIndex = v),
                ("Middle", v => _referenceRig.EnableMiddle = v),
                ("Ring",   v => _referenceRig.EnableRing = v),
                ("Pinky",  v => _referenceRig.EnablePinky = v)
            };
            for (int fi = 0; fi < fDefs.Length; fi++)
            {
                var setter = fDefs[fi].set;
                var fc = MakeHudCheckBox(fDefs[fi].lbl, true, $"Enable/disable {fDefs[fi].lbl} finger bones");
                fc.Toggled += (on) =>
                {
                    if (_isSyncingRefRigUi) return;
                    PushReferenceRigUndoSnapshot();
                    setter(on);
                    if (!_isGuidedPinPlacement && (_referenceRig.Joints.ContainsKey("WristL") || _referenceRig.Joints.ContainsKey("WristR")))
                        _referenceRig.EnsureDefaultJoints();
                    SyncReferenceRigViewportHud();
                    RefreshPinVisuals();
                    _pinsDirtyNeedsRigRebuild = true;
                };
                _hudFingerChecks[fi] = fc;
                _hudFingerSubRow.AddChild(fc);
            }
            _hudFingerSubRow.AddChild(MakeHudSep());
            for (int p = 0; p < 2; p++)
            {
                int phCount = p == 0 ? 2 : 3;
                var pb = MakeHudBtn($"{phCount}j", 28, 24, toggle: true, pressed: phCount == 3, tip: $"{phCount} phalanx joints per finger");
                pb.Pressed += () =>
                {
                    if (_isSyncingRefRigUi) return;
                    PushReferenceRigUndoSnapshot();
                    _referenceRig.PhalanxCount = phCount;
                    SnapReferenceFingersToCurrentMesh();
                    SyncReferenceRigControlsFromDef();
                };
                _hudPhalanxBtns[p] = pb;
                _hudFingerSubRow.AddChild(pb);
            }
            var btnSnapFingers = MakeHudBtn("Snap Fingers", 86, 24, tip: "Auto-snaps all enabled finger joints into the 3D hand mesh using Wrist + Hand Tip.");
            btnSnapFingers.Pressed += () => { PushReferenceRigUndoSnapshot(); SnapReferenceFingersToCurrentMesh(); };
            _hudFingerSubRow.AddChild(btnSnapFingers);
            botRoot.AddChild(_hudFingerSubRow);
        }

        private void SyncReferenceRigViewportHud()
        {
            if (_refRigViewportHud == null || !GodotObject.IsInstanceValid(_refRigViewportHud) || _referenceRig == null)
                return;
            if (_lblRefHudSelectedBone != null)
            {
                if (_isGuidedPinPlacement && _guidedPinStep >= 0 && _guidedPinStep < GuidedPinStepLabels.Length)
                {
                    _lblRefHudSelectedBone.Text = "▸ " + GuidedPinStepLabels[_guidedPinStep];
                    ImporterStyle.ApplyFontSize(_lblRefHudSelectedBone, ImporterStyle.FontBody);
                    _lblRefHudSelectedBone.AddThemeColorOverride("font_color", new Color(1.0f, 0.86f, 0.36f));
                }
                else
                {
                    string sel = _selectedPin ?? _hoveredPin;
                    _lblRefHudSelectedBone.Text = string.IsNullOrEmpty(sel) ? "Drag any joint to adjust" : $"Bone: {sel}";
                    ImporterStyle.ApplyFontSize(_lblRefHudSelectedBone, ImporterStyle.FontHint);
                    _lblRefHudSelectedBone.AddThemeColorOverride("font_color", PlaygroundThemeHelper.Parchment);
                }
            }
            ApplyHudSubtleToggleVisual(_btnRefHudGuidedPlace, _isGuidedPinPlacement);
            if (_btnRefHudGuidedPrev != null)
            {
                _btnRefHudGuidedPrev.Visible = _isGuidedPinPlacement;
                _btnRefHudGuidedPrev.Disabled = _guidedPinStep <= 0;
            }
            if (_btnRefHudGuidedNext != null)
            {
                _btnRefHudGuidedNext.Visible = _isGuidedPinPlacement;
                bool isLast = _guidedPinStep >= GuidedPinStepLabels.Length - 1;
                _btnRefHudGuidedNext.Text = isLast ? "Done ✓" : "Next →";
                _btnRefHudGuidedNext.Disabled = !IsGuidedStepPlaced(_guidedPinStep);
            }
            // Only ONE primary green CTA at a time: Next during 1-by-1, Bind once placement is complete!
            if (_btnRefHudBind != null)
            {
                _btnRefHudBind.ThemeTypeVariation = _isGuidedPinPlacement ? "ToolbarButton" : "ImporterPrimaryButton";
            }
            if (_btnRefHudUndo != null) _btnRefHudUndo.Disabled = _refRigUndoStack.Count == 0;
            if (_btnRefHudRedo != null) _btnRefHudRedo.Disabled = _refRigRedoStack.Count == 0;
            ApplyHudSubtleToggleVisual(_btnRefHudMirror, _chkMirrorPins?.ButtonPressed ?? true);
            for (int hv = 0; hv < _hudHeroVisBtns.Length; hv++)
            {
                ApplyHudSubtleToggleVisual(_hudHeroVisBtns[hv], hv == _pinEditHeroVisMode);
            }

            bool prev = _isSyncingRefRigUi;
            _isSyncingRefRigUi = true;
            try
            {
                for (int i = 0; i < _hudSpineBtns.Length; i++)
                {
                    ApplyHudSubtleToggleVisual(_hudSpineBtns[i], (i + 1) == _referenceRig.SpineCount);
                }
                _hudChkNeck?.SetPressedNoSignal(_referenceRig.HasNeck);
                _hudChkClav?.SetPressedNoSignal(_referenceRig.HasClavicles);
                _hudChkToes?.SetPressedNoSignal(_referenceRig.HasToes);
                for (int i = 0; i < _hudFingerModeBtns.Length; i++)
                {
                    ApplyHudSubtleToggleVisual(_hudFingerModeBtns[i], i == (int)_referenceRig.FingerMode);
                }
                if (_hudFingerSubRow != null)
                    _hudFingerSubRow.Visible = !_isGuidedPinPlacement && _referenceRig.FingerMode == ReferenceFingerRigMode.ArticulatedFingers;
                _hudFingerChecks[0]?.SetPressedNoSignal(_referenceRig.EnableThumb);
                _hudFingerChecks[1]?.SetPressedNoSignal(_referenceRig.EnableIndex);
                _hudFingerChecks[2]?.SetPressedNoSignal(_referenceRig.EnableMiddle);
                _hudFingerChecks[3]?.SetPressedNoSignal(_referenceRig.EnableRing);
                _hudFingerChecks[4]?.SetPressedNoSignal(_referenceRig.EnablePinky);
                for (int p = 0; p < _hudPhalanxBtns.Length; p++)
                {
                    ApplyHudSubtleToggleVisual(_hudPhalanxBtns[p], (p == 0 ? 2 : 3) == _referenceRig.PhalanxCount);
                }
            }
            finally
            {
                _isSyncingRefRigUi = prev;
            }
        }

        private void SyncReferenceRigControlsFromDef()
        {
            if (_referenceRig == null) return;
            _isSyncingRefRigUi = true;
            try
            {
                RefreshReferenceClothListUI();
                SyncEditBindButtonVisuals();
            }
            finally
            {
                _isSyncingRefRigUi = false;
            }
            SyncReferenceRigViewportHud();
        }

        private void UpdateTab2SectionVisibility()
        {
            var method = GetCurrentRiggingMethod();
            bool isPreRigged = method == CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap;
            bool isAutoRig = method == CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig;
            bool isPartialBody = method == CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig;
            bool isPartialPins = isPartialBody && _partialBodyEngine == CustomModelAutoRigger.PartialBodyEngine.SemiBodyPins;
            bool isRigid = method == CustomModelAutoRigger.RiggingMethod.RigidSingleBone;
            bool isKnn = method == CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection;

            if (_boxRigFamilyRow != null) _boxRigFamilyRow.Visible = isPreRigged;
            if (_boxRigidBone != null) _boxRigidBone.Visible = isRigid;
            if (_boxPartialBodyPanel != null) _boxPartialBodyPanel.Visible = isPartialBody;
            if (_boxKnnParameters != null) _boxKnnParameters.Visible = isKnn;

            // Update sticky top bar buttons dynamically for the active rigging mode
            if (_boxTab2SubNav != null) _boxTab2SubNav.Visible = !isRigid && !(isPartialBody && !isPartialPins);
            if (isAutoRig || isPartialPins)
            {
                string[] labels = isPartialPins
                    ? new[] { "Mode & Region", "Region Pins", "Cloth", "Pose Fit" }
                    : new[] { "Mode", "Reference Rig", "Cloth", "Pose Fit" };
                for (int i = 0; i < 4; i++)
                {
                    if (_tab2SubButtons[i] == null) continue;
                    _tab2SubButtons[i].Visible = true;
                    _tab2SubButtons[i].Text = labels[i];
                    bool act = _tab2SubView == i;
                    _tab2SubButtons[i].SetPressedNoSignal(act);
                    _tab2SubButtons[i].ThemeTypeVariation = act ? "ImporterPrimaryButton" : "ToolbarButton";
                }
                if (_boxTab2PageMode != null) _boxTab2PageMode.Visible = _tab2SubView == 0;
                if (_boxRefRigWorkspace != null) _boxRefRigWorkspace.Visible = _tab2SubView == 1;
                if (_boxTab2PageRefCloth != null) _boxTab2PageRefCloth.Visible = _tab2SubView == 2;
                if (_boxTab2PagePoseFit != null) _boxTab2PagePoseFit.Visible = _tab2SubView == 3;
                if (_boxTab2PageHandsPreRigged != null) _boxTab2PageHandsPreRigged.Visible = false;
                if (_boxTab2PageFooterLinks != null) _boxTab2PageFooterLinks.Visible = _tab2SubView == 0;
            }
            else if (isPartialBody) // PartialBodyAutoRig + HeroBodyTransfer (all controls inline on Mode page)
            {
                _tab2SubView = 0;
                if (_boxTab2PageMode != null) _boxTab2PageMode.Visible = true;
                if (_boxRefRigWorkspace != null) _boxRefRigWorkspace.Visible = false;
                if (_boxTab2PageRefCloth != null) _boxTab2PageRefCloth.Visible = false;
                if (_boxTab2PagePoseFit != null) _boxTab2PagePoseFit.Visible = false;
                if (_boxTab2PageHandsPreRigged != null) _boxTab2PageHandsPreRigged.Visible = false;
                if (_boxTab2PageFooterLinks != null) _boxTab2PageFooterLinks.Visible = true;
            }
            else if (isPreRigged || isKnn)
            {
                if (_tab2SubView > 2) _tab2SubView = 0;
                string[] labels = { "Mode & Profile", "Pose Fit", "Hands", "" };
                for (int i = 0; i < 4; i++)
                {
                    if (_tab2SubButtons[i] == null) continue;
                    bool vis = isPreRigged ? (i < 3) : (i < 2);
                    _tab2SubButtons[i].Visible = vis;
                    _tab2SubButtons[i].Text = labels[i];
                    bool act = _tab2SubView == i;
                    _tab2SubButtons[i].SetPressedNoSignal(act);
                    _tab2SubButtons[i].ThemeTypeVariation = act ? "ImporterPrimaryButton" : "ToolbarButton";
                }
                if (_boxTab2PageMode != null) _boxTab2PageMode.Visible = _tab2SubView == 0;
                if (_boxRefRigWorkspace != null) _boxRefRigWorkspace.Visible = false;
                if (_boxTab2PageRefCloth != null) _boxTab2PageRefCloth.Visible = false;
                if (_boxTab2PagePoseFit != null) _boxTab2PagePoseFit.Visible = _tab2SubView == 1;
                if (_boxTab2PageHandsPreRigged != null) _boxTab2PageHandsPreRigged.Visible = isPreRigged && _tab2SubView == 2;
                if (_boxTab2PageFooterLinks != null) _boxTab2PageFooterLinks.Visible = _tab2SubView == 0;
            }
            else // RigidSingleBone
            {
                if (_boxTab2PageMode != null) _boxTab2PageMode.Visible = true;
                if (_boxRefRigWorkspace != null) _boxRefRigWorkspace.Visible = false;
                if (_boxTab2PageRefCloth != null) _boxTab2PageRefCloth.Visible = false;
                if (_boxTab2PagePoseFit != null) _boxTab2PagePoseFit.Visible = false;
                if (_boxTab2PageHandsPreRigged != null) _boxTab2PageHandsPreRigged.Visible = false;
                if (_boxTab2PageFooterLinks != null) _boxTab2PageFooterLinks.Visible = false;
            }

            if (_btnUseKnnMethod != null)
                _btnUseKnnMethod.SetPressedNoSignal(isKnn);
            if (_rigMethodCards != null && !isKnn)
                _rigMethodCards.Select((int)method);
            SyncEditBindButtonVisuals();
            SyncPartialBodyUiState();
        }

        private void OnPoseFitPresetSelected(long idx)
        {
            bool full = idx == 0;
            bool armsOnly = idx == 1;
            bool conform = full || armsOnly;
            _chkConformToSkeleton?.SetPressedNoSignal(conform);
            _chkAutoAlignArms?.SetPressedNoSignal(conform);
            _chkAutoAlignHandOrientation?.SetPressedNoSignal(conform);
            _chkAutoAlignLegs?.SetPressedNoSignal(full);
            _chkAutoAlignHead?.SetPressedNoSignal(full);
            if (_boxHandSnapTuning != null) _boxHandSnapTuning.Visible = conform;
            TriggerRealtimeRigUpdate();
        }

        private void RefreshMethodCards(bool hasRig)
        {
            if (_rigMethodCards == null) return;
            var selectedMethod = _optRiggingMethod != null && _optRiggingMethod.Selected >= 0
                ? (CustomModelAutoRigger.RiggingMethod)_optRiggingMethod.GetItemId(_optRiggingMethod.Selected)
                : (hasRig ? CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap : CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig);
            bool isAccessoryFlow = _workflowIntent == ImportWorkflowIntent.AccessoryAddOrReplace;

            var options = new List<OptionCardGroup.OptionDef>();
            if (hasRig)
            {
                options.Add(new OptionCardGroup.OptionDef
                {
                    Id = (int)CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap,
                    Title = "Keep existing rig (Remap)",
                    Description = "Maps your model's skeleton directly onto Deadlock's hero rig.",
                    IconPath = "res://assets/at-icons/bone.svg",
                    Recommended = true
                });
            }
            options.Add(new OptionCardGroup.OptionDef
            {
                Id = (int)CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig,
                Title = "Clothing / Partial-Body Auto-Rig",
                Description = "Best for sweaters, jackets, pants & armor: semi-body 3D pins or region-scoped hero transfer.",
                IconPath = "res://assets/at-icons/layers.svg",
                Recommended = !hasRig && isAccessoryFlow
            });
            options.Add(new OptionCardGroup.OptionDef
            {
                Id = (int)CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig,
                Title = "Auto-Rig (Full Body Reference Bones)",
                Description = "Auto-Rig Pro workflow: edit full-body spine, fingers & cloth bones in 3D, then Bind.",
                IconPath = "res://assets/at-icons/sparkles.svg",
                Recommended = !hasRig && !isAccessoryFlow
            });
            options.Add(new OptionCardGroup.OptionDef
            {
                Id = (int)CustomModelAutoRigger.RiggingMethod.RigidSingleBone,
                Title = "Rigid attach to one bone",
                Description = "Attaches props, hats, or weapons rigidly to a single hero bone.",
                IconPath = "res://assets/at-icons/wrench.svg",
                Recommended = false
            });

            _rigMethodCards.SetOptions(options, (int)selectedMethod);
        }

        private void SelectRiggingMethodById(int methodId)
        {
            if (_optRiggingMethod == null) return;
            for (int i = 0; i < _optRiggingMethod.ItemCount; i++)
            {
                if (_optRiggingMethod.GetItemId(i) == methodId)
                {
                    _optRiggingMethod.Select(i);
                    OnRiggingMethodChanged(i);
                    UpdateTab2SectionVisibility();
                    return;
                }
            }
        }

        private CustomModelAutoRigger.RiggingMethod GetCurrentRiggingMethod()
        {
            if (_optRiggingMethod != null && _optRiggingMethod.Selected >= 0)
                return (CustomModelAutoRigger.RiggingMethod)_optRiggingMethod.GetItemId(_optRiggingMethod.Selected);
            return CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig;
        }

        private bool IsWizardStepApplicable(int stepIdx, CustomModelAutoRigger.RiggingMethod method) => true;

        private void SetRigWizardStep(int stepIdx)
        {
            _rigWizardStep = Mathf.Clamp(stepIdx, 0, 3);
            UpdateTab2SectionVisibility();
            UpdatePinProgressChip();
        }

        private void UpdateAccessoryRigVisibility()
        {
            bool isAccessory = _workflowIntent == ImportWorkflowIntent.AccessoryAddOrReplace;
            var accMode = (_optAccBoneMode != null && _optAccBoneMode.Selected >= 0)
                ? (AccessoryBoneMode)_optAccBoneMode.GetItemId(_optAccBoneMode.Selected)
                : AccessoryBoneMode.RigidToOneBone;
            bool needFullRigWizard = !isAccessory || accMode == AccessoryBoneMode.SkinToHero;

            if (_boxTab2SubNav != null) _boxTab2SubNav.Visible = needFullRigWizard;
            if (_boxTab2WizardScroll != null) _boxTab2WizardScroll.Visible = needFullRigWizard;
            if (_boxAccessoryRigNotice != null) _boxAccessoryRigNotice.Visible = !needFullRigWizard;
        }

        private void SyncRigWizardNavButtons(CustomModelAutoRigger.RiggingMethod method)
        {
            UpdateTab2SectionVisibility();
        }

        private void UpdateRigPhysicsLinkText()
        {
            if (_btnRigPhysicsLink == null) return;
            bool on = _chkEnableCustomPhysics?.ButtonPressed ?? true;
            _btnRigPhysicsLink.Text = $"Physics: {(on ? "On" : "Off")} → Configure";
        }

        private void UpdatePinProgressChip()
        {
            if (_lblPinProgressChip == null) return;
            int placed = _referenceRig?.Joints?.Count ?? _landmarkPins?.Count ?? 0;
            _lblPinProgressChip.Text = $"{placed} joints ready";
        }

        private void OnFingerRigModeChanged(long idx)
        {
            if (_optFingerRigMode == null) return;
            var mode = (FingerRigMode)_optFingerRigMode.GetItemId((int)idx);
            for (int i = 0; i < 2 && i < _fingerSegButtons.Length; i++)
            {
                if (_fingerSegButtons[i] != null)
                {
                    bool act = i == (int)idx;
                    _fingerSegButtons[i].SetPressedNoSignal(act);
                    _fingerSegButtons[i].ThemeTypeVariation = act ? "ImporterPrimaryButton" : "ToolbarButton";
                }
            }
            if (_chkKeepFingersRigid != null && !_isUpdatingBoneDropdowns)
            {
                _isUpdatingBoneDropdowns = true;
                _chkKeepFingersRigid.SetPressedNoSignal(mode == FingerRigMode.RigidHand);
                _isUpdatingBoneDropdowns = false;
            }
            RefreshPinVisuals();
            TriggerRealtimeRigUpdate();
        }

        private void AutoDetectFingerLandmarksInUI()
        {
            if (_liveImportedScene == null) return;
            EnsureReferenceRigInitialized(false);
            var meshes = FilterActiveImportedMeshes(CustomModelAutoRigger.CollectMeshes(_liveImportedScene));
            _fingerLandmarks = _activeFingerStrategy.DetectLandmarks(_liveImportedScene, meshes, _landmarkPins);
            if (_lblFingerPinStatus != null)
            {
                _lblFingerPinStatus.Text = _fingerLandmarks.Count > 0
                    ? $"Detected {_fingerLandmarks.Count} finger joint landmarks (L & R)."
                    : "Could not locate hand geometry for automatic finger detection.";
            }
        }
    }
}
