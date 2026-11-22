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
        private OptionButton _optRefClothDir;
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
            var btnEnableSkin = new Button { Text = "Use Full-Body Skinning", ThemeTypeVariation = "ToolbarButton", CustomMinimumSize = new Vector2(0, 36), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            btnEnableSkin.Pressed += () =>
            {
                if (_optAccBoneMode != null)
                {
                    for (int i = 0; i < _optAccBoneMode.ItemCount; i++)
                    {
                        if (_optAccBoneMode.GetItemId(i) == (int)AccessoryBoneMode.SkinToHero)
                        {
                            _optAccBoneMode.Select(i);
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
                _selectedPin = null; _hoveredPin = null; _draggingPin = null;
                _selectedPins.Clear();
                EnsureReferenceRigInitialized(true);
                UpdateActivePinBadge();
                RefreshPinVisuals();
                UpdatePinProgressChip();
                SetRigChipStatus("Reference Rig Reset — Click Bind / Re-Bind", StatusTone.Info);
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
                "Author custom hair, cape, skirt, or accessory bone chains in 3D. They automatically appear in Tab 3 (Physics) on Bind.");
            box.AddChild(clothCard);

            var clothTopRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            clothTopRow.AddThemeConstantOverride("separation", 6);
            _txtRefClothName = new LineEdit
            {
                PlaceholderText = "Chain name (e.g. cape_L)",
                Text = "cloth_strand",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 32)
            };
            _optRefClothParent = new OptionButton { CustomMinimumSize = new Vector2(110, 32) };
            ConfigureOptionButton(_optRefClothParent);
            foreach (string pb in new[] { "spine_3", "head", "pelvis", "spine_2", "clavicle_L", "clavicle_R", "hand_L", "hand_R" })
                _optRefClothParent.AddItem(pb);
            clothTopRow.AddChild(_txtRefClothName);
            clothTopRow.AddChild(_optRefClothParent);
            clothBody.AddChild(clothTopRow);

            var clothOptRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            clothOptRow.AddThemeConstantOverride("separation", 6);
            _spinRefClothSegments = new SpinBox { MinValue = 2, MaxValue = 6, Step = 1, Value = 3, Prefix = "Segs:", CustomMinimumSize = new Vector2(92, 32) };
            _optRefClothDir = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 32) };
            ConfigureOptionButton(_optRefClothDir);
            _optRefClothDir.AddItem("Hang Down (-Y)", 0);
            _optRefClothDir.AddItem("Back (-Z)", 1);
            _optRefClothDir.AddItem("Outward Left (+X)", 2);
            _optRefClothDir.AddItem("Outward Right (-X)", 3);

            var btnAddCloth = new Button
            {
                Text = "+ Add Chain",
                ThemeTypeVariation = "ImporterPrimaryButton",
                CustomMinimumSize = new Vector2(108, 32)
            };
            ImporterStyle.ApplyFontSize(btnAddCloth, ImporterStyle.FontHint);
            btnAddCloth.Pressed += OnAddReferenceClothChainPressed;
            clothOptRow.AddChild(_spinRefClothSegments);
            clothOptRow.AddChild(_optRefClothDir);
            clothOptRow.AddChild(btnAddCloth);
            clothBody.AddChild(clothOptRow);

            _boxRefClothList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _boxRefClothList.AddThemeConstantOverride("separation", 4);
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
                _weightUndoStack.Clear();
                _weightRedoStack.Clear();
                UpdatePaintOverrideLabel();
                UpdateWeightHistoryUI();
            }
            SyncEditBindButtonVisuals();
            ExecuteLiveRigUpdate();
            SetBottomStatus("Bound mesh to modular Reference Rig. Test animations or refine in Tab 3 (Weights & Physics).", StatusTone.Success);
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
            int segs = (int)(_spinRefClothSegments?.Value ?? 3);
            int dirId = _optRefClothDir != null && _optRefClothDir.Selected >= 0 ? _optRefClothDir.GetItemId(_optRefClothDir.Selected) : 0;

            Vector3 dir = dirId switch
            {
                1 => new Vector3(0f, -0.35f, -0.94f),
                2 => new Vector3(0.70f, -0.70f, 0f),
                3 => new Vector3(-0.70f, -0.70f, 0f),
                _ => Vector3.Down
            };

            // Resolve parent anchor position in model space
            Vector3 parentPos = _referenceRig.Joints.TryGetValue("Chest", out var cp) ? cp : Vector3.Zero;
            if (parentBone == "head" && _referenceRig.Joints.TryGetValue("Head", out var hp)) parentPos = hp;
            else if (parentBone == "pelvis" && _referenceRig.Joints.TryGetValue("Pelvis", out var pp)) parentPos = pp;
            else if (parentBone == "clavicle_L" && _referenceRig.Joints.TryGetValue("ShoulderL", out var sl)) parentPos = sl;
            else if (parentBone == "clavicle_R" && _referenceRig.Joints.TryGetValue("ShoulderR", out var sr)) parentPos = sr;
            else if (parentBone == "hand_L" && _referenceRig.Joints.TryGetValue("WristL", out var wl)) parentPos = wl;
            else if (parentBone == "hand_R" && _referenceRig.Joints.TryGetValue("WristR", out var wr)) parentPos = wr;

            float h = 1.7f;
            if (_referenceRig.Joints.TryGetValue("Head", out var headP) && _referenceRig.Joints.TryGetValue("AnkleL", out var ankP))
                h = MathF.Max(0.2f, MathF.Abs(headP.Y - ankP.Y) / 0.88f);

            var chain = ReferenceRigDef.CreateDefaultClothChain(rawName, parentBone, parentPos, dir, segs, totalLength: 0.28f * h);
            _referenceRig.ClothChains.Add(chain);
            _isEditReferenceBonesMode = true;
            _selectedPin = $"cloth::{chain.ChainId}::0";
            SyncEditBindButtonVisuals();
            RefreshReferenceClothListUI();
            ApplyLandmarkPreviewState();
            AttachGizmoToSelectedReferenceJoint();
            _pinsDirtyNeedsRigRebuild = true;
            SetBottomStatus($"Added cloth chain '{chain.Name}' on {parentBone}. Drag joints in 3D, then click Bind / Re-Bind.", StatusTone.Info);
        }

        private void RefreshReferenceClothListUI()
        {
            if (_boxRefClothList == null) return;
            foreach (Node ch in _boxRefClothList.GetChildren()) ch.QueueFree();
            if (_referenceRig?.ClothChains == null || _referenceRig.ClothChains.Count == 0)
            {
                var empty = new Label { Text = "No cloth chains authored yet." };
                ImporterStyle.ApplyHintStyle(empty);
                _boxRefClothList.AddChild(empty);
                return;
            }
            foreach (var cc in _referenceRig.ClothChains.ToList())
            {
                var captured = cc;
                var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                row.AddThemeConstantOverride("separation", 4);

                var btnSel = new Button
                {
                    Text = $"{cc.Name} ({cc.ParentBoneName} · {Math.Max(0, cc.JointPositions.Count - 1)}b)",
                    Alignment = HorizontalAlignment.Left,
                    ClipText = true,
                    ThemeTypeVariation = "ToolbarButton",
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    CustomMinimumSize = new Vector2(0, 30)
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
                    CustomMinimumSize = new Vector2(56, 30),
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
                    CustomMinimumSize = new Vector2(30, 30),
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

                _boxRefClothList.AddChild(row);
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

        private void BuildReferenceRigViewportHud(Control viewportPanel)
        {
            if (viewportPanel == null) return;

            var overlayMargin = new MarginContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            overlayMargin.AddThemeConstantOverride("margin_left", 10);
            overlayMargin.AddThemeConstantOverride("margin_right", 10);
            overlayMargin.AddThemeConstantOverride("margin_top", 10);
            overlayMargin.AddThemeConstantOverride("margin_bottom", 10);
            viewportPanel.AddChild(overlayMargin);

            var hud = new PanelContainer
            {
                Visible = false,
                ThemeTypeVariation = "InspectorSectionPanel",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
                MouseFilter = Control.MouseFilterEnum.Stop
            };
            overlayMargin.AddChild(hud);
            _refRigViewportHud = hud;

            var innerPad = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            innerPad.AddThemeConstantOverride("margin_left", 10);
            innerPad.AddThemeConstantOverride("margin_right", 10);
            innerPad.AddThemeConstantOverride("margin_top", 6);
            innerPad.AddThemeConstantOverride("margin_bottom", 6);
            hud.AddChild(innerPad);

            var root = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            root.AddThemeConstantOverride("separation", 6);
            innerPad.AddChild(root);

            Button MakeHudBtn(string text, int minW, int minH = 28, bool toggle = false, bool pressed = false, string variation = "ToolbarButton", string tip = "")
            {
                var btn = new Button
                {
                    Text = text,
                    ToggleMode = toggle,
                    ButtonPressed = pressed,
                    ThemeTypeVariation = variation,
                    CustomMinimumSize = new Vector2(minW, minH),
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                    TooltipText = tip
                };
                ImporterStyle.ApplyFontSize(btn, ImporterStyle.FontHint);
                return btn;
            }

            CheckBox MakeHudChk(string text, bool pressed)
            {
                var c = new CheckBox
                {
                    Text = text,
                    ButtonPressed = pressed,
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
                };
                ImporterStyle.ApplyFontSize(c, ImporterStyle.FontHint);
                return c;
            }

            // Row 1: Selected Bone Name + X-Mirror + Hand Focus + Quick Bind
            var row1 = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row1.AddThemeConstantOverride("separation", 6);

            _lblRefHudSelectedBone = new Label
            {
                Text = "Bone: Click a joint to move",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
            };
            ImporterStyle.ApplyFontSize(_lblRefHudSelectedBone, ImporterStyle.FontBody);
            _lblRefHudSelectedBone.AddThemeColorOverride("font_color", PlaygroundThemeHelper.Parchment);
            row1.AddChild(_lblRefHudSelectedBone);

            _btnRefHudMirror = MakeHudBtn("X-Mirror", 72, 28, toggle: true, pressed: true);
            _btnRefHudMirror.Toggled += (on) =>
            {
                if (_chkMirrorPins != null && _chkMirrorPins.ButtonPressed != on) _chkMirrorPins.ButtonPressed = on;
                if (_btnMirrorPinsToggle != null) _btnMirrorPinsToggle.SetPressedNoSignal(on);
            };
            row1.AddChild(_btnRefHudMirror);

            var btnHudFL = MakeHudBtn("Hand L", 60, 28);
            var btnHudFR = MakeHudBtn("Hand R", 60, 28);
            var btnHudReset = MakeHudBtn("View", 50, 28);
            var btnHudBind = MakeHudBtn("Bind", 64, 28, variation: "ImporterPrimaryButton");
            btnHudFL.Pressed += () => FocusCameraOnReferenceHand(true);
            btnHudFR.Pressed += () => FocusCameraOnReferenceHand(false);
            btnHudReset.Pressed += () => SetCameraPreset("Front");
            btnHudBind.Pressed += OnBindReferenceRigPressed;
            row1.AddChild(btnHudFL);
            row1.AddChild(btnHudFR);
            row1.AddChild(btnHudReset);
            row1.AddChild(btnHudBind);
            root.AddChild(row1);

            // Row 2: Compact Spine (1-4) + Neck/Clav/Toes + Hands (Mitten | Fingers) — wraps cleanly, zero vertical stretching!
            var row2 = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row2.AddThemeConstantOverride("h_separation", 6);
            row2.AddThemeConstantOverride("v_separation", 4);

            var lblSp = new Label { Text = "Spine:", SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            ImporterStyle.ApplyHintStyle(lblSp);
            row2.AddChild(lblSp);
            for (int sc = 1; sc <= 4; sc++)
            {
                int cap = sc;
                var b = MakeHudBtn(sc.ToString(), 28, 26, toggle: true, pressed: sc == 4);
                b.Pressed += () =>
                {
                    if (_isSyncingRefRigUi) return;
                    _referenceRig.SpineCount = cap;
                    SyncReferenceRigControlsFromDef();
                    RefreshPinVisuals();
                    _pinsDirtyNeedsRigRebuild = true;
                };
                _hudSpineBtns[sc - 1] = b;
                row2.AddChild(b);
            }

            _hudChkNeck = MakeHudChk("Neck", true);
            _hudChkClav = MakeHudChk("Clav", true);
            _hudChkToes = MakeHudChk("Toes", false);
            _hudChkNeck.Toggled += (on) => { if (!_isSyncingRefRigUi) { _referenceRig.HasNeck = on; RefreshPinVisuals(); _pinsDirtyNeedsRigRebuild = true; } };
            _hudChkClav.Toggled += (on) => { if (!_isSyncingRefRigUi) { _referenceRig.HasClavicles = on; RefreshPinVisuals(); _pinsDirtyNeedsRigRebuild = true; } };
            _hudChkToes.Toggled += (on) => { if (!_isSyncingRefRigUi) { _referenceRig.HasToes = on; _referenceRig.EnsureDefaultJoints(); RefreshPinVisuals(); _pinsDirtyNeedsRigRebuild = true; } };
            row2.AddChild(_hudChkNeck);
            row2.AddChild(_hudChkClav);
            row2.AddChild(_hudChkToes);

            var sep = new VSeparator { CustomMinimumSize = new Vector2(2, 20), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            row2.AddChild(sep);

            string[] hModes = { "Mitten", "Fingers" };
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                var b = MakeHudBtn(hModes[i], 64, 26, toggle: true, pressed: i == 0);
                b.Pressed += () =>
                {
                    if (_isSyncingRefRigUi) return;
                    _referenceRig.FingerMode = (ReferenceFingerRigMode)idx;
                    EnsureReferenceRigInitialized(false);
                    if (idx == 1) SnapReferenceFingersToCurrentMesh();
                    SyncReferenceRigControlsFromDef();
                    RefreshPinVisuals();
                    _pinsDirtyNeedsRigRebuild = true;
                };
                _hudFingerModeBtns[i] = b;
                row2.AddChild(b);
            }
            root.AddChild(row2);

            // Row 3 (shown only when Fingers is active): Individual finger toggles + Phalanges + Snap to Mesh
            _hudFingerSubRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            _hudFingerSubRow.AddThemeConstantOverride("separation", 6);
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
                var fc = MakeHudChk(fDefs[fi].lbl, true);
                fc.Toggled += (on) =>
                {
                    if (_isSyncingRefRigUi) return;
                    setter(on);
                    _referenceRig.EnsureDefaultJoints();
                    RefreshPinVisuals();
                    _pinsDirtyNeedsRigRebuild = true;
                };
                _hudFingerChecks[fi] = fc;
                _hudFingerSubRow.AddChild(fc);
            }
            for (int p = 0; p < 2; p++)
            {
                int phCount = p == 0 ? 2 : 3;
                var pb = MakeHudBtn($"{phCount}j", 32, 26, toggle: true, pressed: phCount == 3, tip: $"{phCount} phalanx joints per finger");
                pb.Pressed += () =>
                {
                    if (_isSyncingRefRigUi) return;
                    _referenceRig.PhalanxCount = phCount;
                    SnapReferenceFingersToCurrentMesh();
                    SyncReferenceRigControlsFromDef();
                };
                _hudPhalanxBtns[p] = pb;
                _hudFingerSubRow.AddChild(pb);
            }
            var btnSnapFingers = MakeHudBtn("Snap to Mesh", 96, 26, tip: "Auto-snaps all enabled finger joints into the 3D hand mesh.");
            btnSnapFingers.Pressed += SnapReferenceFingersToCurrentMesh;
            _hudFingerSubRow.AddChild(btnSnapFingers);
            root.AddChild(_hudFingerSubRow);
        }

        private void SyncReferenceRigViewportHud()
        {
            if (_refRigViewportHud == null || !GodotObject.IsInstanceValid(_refRigViewportHud) || _referenceRig == null)
                return;
            if (_lblRefHudSelectedBone != null)
            {
                string sel = _selectedPin ?? _hoveredPin;
                _lblRefHudSelectedBone.Text = string.IsNullOrEmpty(sel) ? "Bone: Click a joint to move" : $"Bone: {sel}";
            }
            if (_btnRefHudMirror != null)
                _btnRefHudMirror.SetPressedNoSignal(_chkMirrorPins?.ButtonPressed ?? true);

            bool prev = _isSyncingRefRigUi;
            _isSyncingRefRigUi = true;
            try
            {
                for (int i = 0; i < _hudSpineBtns.Length; i++)
                {
                    bool act = (i + 1) == _referenceRig.SpineCount;
                    if (_hudSpineBtns[i] != null)
                    {
                        _hudSpineBtns[i].SetPressedNoSignal(act);
                        _hudSpineBtns[i].ThemeTypeVariation = act ? "ImporterPrimaryButton" : "ToolbarButton";
                    }
                }
                _hudChkNeck?.SetPressedNoSignal(_referenceRig.HasNeck);
                _hudChkClav?.SetPressedNoSignal(_referenceRig.HasClavicles);
                _hudChkToes?.SetPressedNoSignal(_referenceRig.HasToes);
                for (int i = 0; i < _hudFingerModeBtns.Length; i++)
                {
                    bool act = i == (int)_referenceRig.FingerMode;
                    if (_hudFingerModeBtns[i] != null)
                    {
                        _hudFingerModeBtns[i].SetPressedNoSignal(act);
                        _hudFingerModeBtns[i].ThemeTypeVariation = act ? "ImporterPrimaryButton" : "ToolbarButton";
                    }
                }
                if (_hudFingerSubRow != null)
                    _hudFingerSubRow.Visible = _referenceRig.FingerMode == ReferenceFingerRigMode.ArticulatedFingers;
                if (_hudFingerChecks[0] != null) _hudFingerChecks[0].SetPressedNoSignal(_referenceRig.EnableThumb);
                if (_hudFingerChecks[1] != null) _hudFingerChecks[1].SetPressedNoSignal(_referenceRig.EnableIndex);
                if (_hudFingerChecks[2] != null) _hudFingerChecks[2].SetPressedNoSignal(_referenceRig.EnableMiddle);
                if (_hudFingerChecks[3] != null) _hudFingerChecks[3].SetPressedNoSignal(_referenceRig.EnableRing);
                if (_hudFingerChecks[4] != null) _hudFingerChecks[4].SetPressedNoSignal(_referenceRig.EnablePinky);
                for (int p = 0; p < _hudPhalanxBtns.Length; p++)
                {
                    bool act = (p == 0 ? 2 : 3) == _referenceRig.PhalanxCount;
                    if (_hudPhalanxBtns[p] != null)
                    {
                        _hudPhalanxBtns[p].SetPressedNoSignal(act);
                        _hudPhalanxBtns[p].ThemeTypeVariation = act ? "ImporterPrimaryButton" : "ToolbarButton";
                    }
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
            bool isRigid = method == CustomModelAutoRigger.RiggingMethod.RigidSingleBone;
            bool isKnn = method == CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection;

            if (_boxRigFamilyRow != null) _boxRigFamilyRow.Visible = isPreRigged;
            if (_boxRigidBone != null) _boxRigidBone.Visible = isRigid;
            if (_boxKnnParameters != null) _boxKnnParameters.Visible = isKnn;

            // Update sticky top bar buttons dynamically for the active rigging mode
            if (_boxTab2SubNav != null) _boxTab2SubNav.Visible = !isRigid;
            if (isAutoRig)
            {
                string[] labels = { "Mode", "Reference Rig", "Cloth", "Pose Fit" };
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
                Id = (int)CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig,
                Title = "Auto-Rig (Reference Bones)",
                Description = "Auto-Rig Pro workflow: edit modular spine, fingers & cloth bones in 3D, then Bind.",
                IconPath = "res://assets/at-icons/sparkles.svg",
                Recommended = !hasRig
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
