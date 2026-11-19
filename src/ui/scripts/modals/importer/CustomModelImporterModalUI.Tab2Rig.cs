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
        // Tab 2: Rigging & Markers & Fingers (4-Step Wizard)
        private OptionButton _optRigFamilyOverride;
        private Label _lblRigProfileConfidence;
        private Control _boxMarkersCard;
        private Control _boxFingerCard;
        private CheckBox _chkEnableCustomPhysics;
        private CheckBox _chkBindSymmetry;
        private CheckBox _chkFitSpine;
        private CheckBox _chkConformToSkeleton;
        private CheckBox _chkLooseMeshesRigid;
        private CheckBox _chkExtraTwistBones;

        // Per-group Markers Checklist
        private CheckBox _chkMarkerHeadNeck;
        private CheckBox _chkMarkerArms;
        private CheckBox _chkMarkerLegs;
        private CheckBox _chkMarkerFingers;
        private Button _btnAutoPlaceAllMarkers;

        // Phase 4: Finger Auto-Rigging Controls
        private OptionButton _optFingerRigMode;
        private Control _boxFingerLandmarkTools;
        private CheckBox _chkGenMetacarpals;
        private CheckBox _chkGenWristTwist;
        private OptionButton _optFingerPosePreset;
        private Label _lblFingerPinStatus;
        private Dictionary<string, Vector3> _fingerLandmarks = new(StringComparer.OrdinalIgnoreCase);
        private IFingerRigStrategy _activeFingerStrategy = new PinBasedFingersStrategy();

        // Wizard State & UI
        private int _rigWizardStep = 0; // Persisted across main tab switches
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

            var topMargin = new MarginContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            topMargin.AddThemeConstantOverride("margin_left", 10);
            topMargin.AddThemeConstantOverride("margin_top", 8);
            topMargin.AddThemeConstantOverride("margin_right", 10);
            topMargin.AddThemeConstantOverride("margin_bottom", 0);

            _rigStepper = new StepperBar();
            _rigStepper.SetSteps("Method", "Landmarks", "Weights", "Fit & Hands");
            _rigStepper.StepSelected += (idx) => SetRigWizardStep(idx);
            topMargin.AddChild(_rigStepper);
            root.AddChild(topMargin);

            // Clean notice shown in Accessory Mode when RigidToOneBone or KeepOwnBones is active (hides irrelevant full-body auto-rig wizard)
            var (accNoticeCard, accNoticeBody) = CreateInspectorCard(
                "Accessory Bone Mode Active",
                "This item attaches via Tab 1's Accessory Setup (Rigid Bone or Own Physics Chains), so full-body skeleton rigging is not required.");
            _boxAccessoryRigNotice = accNoticeCard;
            accNoticeCard.Visible = false;
            _lblAccessoryRigNoticeDesc = new Label
            {
                Text = "Switch to 'Skin to Hero Skeleton' in Tab 1 if this is a fitted coat/outfit that needs full-body skinning or landmark pins.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            ImporterStyle.ApplyHintStyle(_lblAccessoryRigNoticeDesc);
            _secondaryLabels.Add(_lblAccessoryRigNoticeDesc);
            accNoticeBody.AddChild(_lblAccessoryRigNoticeDesc);
            var accRigBtnRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            accRigBtnRow.AddThemeConstantOverride("separation", 8);
            var btnJumpPhys = new Button { Text = "Configure Physics Chains →", ThemeTypeVariation = "ToolbarButton", CustomMinimumSize = new Vector2(0, 36), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            btnJumpPhys.Pressed += () => { if (_rightTabs != null && _rightTabs.GetTabCount() > 2) _rightTabs.CurrentTab = 2; SetBonesSubView(1); };
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

            var scroll = new ScrollContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };
            _boxTab2WizardScroll = scroll;
            root.AddChild(scroll);

            var margin = new MarginContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            margin.AddThemeConstantOverride("margin_left", 10);
            margin.AddThemeConstantOverride("margin_top", 8);
            margin.AddThemeConstantOverride("margin_right", 10);
            margin.AddThemeConstantOverride("margin_bottom", 12);
            scroll.AddChild(margin);

            var pagesHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            pagesHost.AddThemeConstantOverride("separation", ImporterStyle.Gap);
            margin.AddChild(pagesHost);

            // Hidden legacy backing controls (kept as single source of truth for GatherRiggingOptions / ImporterPresetStore)
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

            // Build the 4 wizard pages
            _rigStepPages[0] = BuildRigStep1Method();
            _rigStepPages[1] = BuildRigStep2Landmarks();
            _rigStepPages[2] = BuildRigStep3Weights();
            _rigStepPages[3] = BuildRigStep4FitAndHands();
            foreach (var p in _rigStepPages) pagesHost.AddChild(p);

            // Bottom Wizard Navigation Footer
            var navMargin = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _boxTab2WizardNavFooter = navMargin;
            navMargin.AddThemeConstantOverride("margin_left", 10);
            navMargin.AddThemeConstantOverride("margin_top", 0);
            navMargin.AddThemeConstantOverride("margin_right", 10);
            navMargin.AddThemeConstantOverride("margin_bottom", 8);

            var navRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            navRow.AddThemeConstantOverride("separation", 10);

            _btnRigWizardBack = new Button
            {
                Text = "← Back",
                ThemeTypeVariation = "ImporterSecondaryButton",
                CustomMinimumSize = new Vector2(108, ImporterStyle.ButtonHeight),
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin
            };
            ImporterStyle.ApplyFontSize(_btnRigWizardBack, ImporterStyle.FontBody);
            _btnRigWizardBack.Pressed += OnRigWizardBackPressed;
            navRow.AddChild(_btnRigWizardBack);

            _btnRigWizardNext = new Button
            {
                Text = "Next →",
                ThemeTypeVariation = "ImporterPrimaryButton",
                CustomMinimumSize = new Vector2(140, ImporterStyle.ButtonHeight),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            ImporterStyle.ApplyFontSize(_btnRigWizardNext, ImporterStyle.FontBody);
            _btnRigWizardNext.Pressed += OnRigWizardNextPressed;
            navRow.AddChild(_btnRigWizardNext);

            navMargin.AddChild(navRow);
            root.AddChild(navMargin);

            OnRiggingMethodChanged(_optRiggingMethod.Selected);
            SetRigWizardStep(_rigWizardStep);
            return root;
        }

        private Control BuildRigStep1Method()
        {
            var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", ImporterStyle.Gap);

            // Detection status chip card
            var (statusCard, statusBody) = CreateInspectorCard("1. Choose Rigging Method");
            box.AddChild(statusCard);

            _lblRigStatusBadge = new Label { Text = "Analyzing Model Rig Status...", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            ImporterStyle.ApplyFontSize(_lblRigStatusBadge, ImporterStyle.FontBody);
            SetRigBadgeStatus("Analyzing Model Rig Status...", StatusTone.Info);
            statusBody.AddChild(_lblRigStatusBadge);

            _lblRigProfileConfidence = new Label { Text = "Detected: --", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            ImporterStyle.ApplyHintStyle(_lblRigProfileConfidence);
            _secondaryLabels.Add(_lblRigProfileConfidence);
            statusBody.AddChild(_lblRigProfileConfidence);

            // Selectable 3 Method Cards
            _rigMethodCards = new OptionCardGroup(columns: 1);
            RefreshMethodCards(hasRig: true);
            _rigMethodCards.OptionSelected += (methodId) => SelectRiggingMethodById(methodId);
            statusBody.AddChild(_rigMethodCards);

            _lblRiggingMethodDesc = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            ImporterStyle.ApplyHintStyle(_lblRiggingMethodDesc);
            _secondaryLabels.Add(_lblRiggingMethodDesc);
            statusBody.AddChild(_lblRiggingMethodDesc);

            // Physics status link (single source of truth lives in Bones -> Physics)
            _btnRigPhysicsLink = new Button
            {
                Text = "Physics: On → Configure",
                ThemeTypeVariation = "ImporterSecondaryButton",
                CustomMinimumSize = new Vector2(0, ImporterStyle.ButtonHeight),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Opens the Bones tab directly on Physics & Chains to toggle or tune hair/cloth bones."
            };
            SetButtonIcon(_btnRigPhysicsLink, "res://assets/at-icons/sliders.svg", ImporterStyle.IconSize);
            ImporterStyle.ApplyFontSize(_btnRigPhysicsLink, ImporterStyle.FontBody);
            _btnRigPhysicsLink.Pressed += () =>
            {
                if (_rightTabs != null && _rightTabs.GetTabCount() > 2)
                {
                    _rightTabs.CurrentTab = 2;
                    SetBonesSubView(1);
                }
            };
            statusBody.AddChild(_btnRigPhysicsLink);

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

            // Advanced -> Other methods & Skeleton Profile (collapsed by default)
            var (advCard, advBody) = CreateInspectorCard(
                "Advanced: Other Methods & Profile",
                "Override heuristic rig profile or switch to Legacy KNN proximity transfer.",
                collapsible: true,
                startExpanded: false);
            box.AddChild(advCard);

            var famRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            famRow.AddThemeConstantOverride("separation", 8);
            var lblFam = new Label { Text = "Rig Profile:", CustomMinimumSize = new Vector2(90, 0) };
            ImporterStyle.ApplyBodyStyle(lblFam);
            famRow.AddChild(lblFam);

            _optRigFamilyOverride = new OptionButton
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Heuristic auto-detected rig convention, or manually override (ARP, Rigify, Mixamo, VRM, Biped, etc.)."
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
            advBody.AddChild(famRow);

            _btnUseKnnMethod = new Button
            {
                Text = "Switch to Legacy KNN Proximity Transfer",
                ThemeTypeVariation = "ImporterSecondaryButton",
                ToggleMode = true,
                CustomMinimumSize = new Vector2(0, ImporterStyle.ButtonHeight),
                TooltipText = "Fallback proximity weight transfer for non-humanoids."
            };
            ImporterStyle.ApplyFontSize(_btnUseKnnMethod, ImporterStyle.FontHint);
            _btnUseKnnMethod.Pressed += () => SelectRiggingMethodById((int)CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection);
            advBody.AddChild(_btnUseKnnMethod);

            var (knnCard, knnBody) = CreateInspectorCard("Legacy KNN Parameters", "Fallback proximity transfer for non-humanoids.");
            knnCard.Visible = false;
            _boxKnnParameters = knnCard;

            var lblKnnR = new Label { Text = "KNN Donor Search Radius (meters):" };
            ImporterStyle.ApplyBodyStyle(lblKnnR);
            knnBody.AddChild(lblKnnR);

            var knnHBox = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _spinKnnRadius = new SpinBox { MinValue = 0.05, MaxValue = 0.80, Step = 0.01, Value = 0.20, CustomMinimumSize = new Vector2(88, 0) };
            _sliderKnnRadius = new HSlider { MinValue = 0.05, MaxValue = 0.80, Step = 0.01, Value = 0.20, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            _spinKnnRadius.ValueChanged += (v) => { if (!Mathf.IsEqualApprox((float)_sliderKnnRadius.Value, (float)v)) _sliderKnnRadius.Value = v; };
            _sliderKnnRadius.ValueChanged += (v) => { if (!Mathf.IsEqualApprox((float)_spinKnnRadius.Value, (float)v)) _spinKnnRadius.Value = v; };
            knnHBox.AddChild(_spinKnnRadius); knnHBox.AddChild(_sliderKnnRadius);
            knnBody.AddChild(knnHBox);

            var lblLap = new Label { Text = "Laplacian Smoothing Iterations (0..5):" };
            ImporterStyle.ApplyBodyStyle(lblLap);
            knnBody.AddChild(lblLap);
            _spinLaplacian = new SpinBox { MinValue = 0, MaxValue = 5, Step = 1, Value = 3 };
            knnBody.AddChild(_spinLaplacian);

            _chkExcludeClothHair = new CheckBox { Text = "Exclude Hero Cloth & Hair Donor Bones", ButtonPressed = true, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            ImporterStyle.ApplyFontSize(_chkExcludeClothHair, ImporterStyle.FontBody);
            knnBody.AddChild(_chkExcludeClothHair);
            advBody.AddChild(knnCard);

            return box;
        }

        private Control BuildRigStep2Landmarks()
        {
            var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", ImporterStyle.Gap);

            var (markerCard, markerBody) = CreateInspectorCard("2. Place Landmark Pins", "Drag the 16 anatomical pins in the 3D viewport onto your model's joints.");
            _boxMarkersCard = markerCard;
            box.AddChild(markerCard);

            // Pin progress chip
            var progressPanel = new PanelContainer { ThemeTypeVariation = "SubPanelContainer", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            var progressPad = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            progressPad.AddThemeConstantOverride("margin_left", 10);
            progressPad.AddThemeConstantOverride("margin_right", 10);
            progressPad.AddThemeConstantOverride("margin_top", 6);
            progressPad.AddThemeConstantOverride("margin_bottom", 6);
            _lblPinProgressChip = new Label
            {
                Text = "0 / 16 placed",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            ImporterStyle.ApplyFontSize(_lblPinProgressChip, ImporterStyle.FontBody);
            _lblPinProgressChip.AddThemeColorOverride("font_color", PlaygroundThemeHelper.Parchment);
            progressPad.AddChild(_lblPinProgressChip);
            progressPanel.AddChild(progressPad);
            markerBody.AddChild(progressPanel);

            // Big action buttons: Auto-place pins, Reset, Select all
            _btnAutoPlaceAllMarkers = new Button
            {
                Text = "Auto-Place Pins",
                ThemeTypeVariation = "ImporterPrimaryButton",
                CustomMinimumSize = new Vector2(0, ImporterStyle.PrimaryButtonHeight),
                TooltipText = "Automatically detects all 16 body joint pins from mesh silhouette."
            };
            SetButtonIcon(_btnAutoPlaceAllMarkers, "res://assets/at-icons/sparkles.svg", ImporterStyle.IconSize);
            ImporterStyle.ApplyFontSize(_btnAutoPlaceAllMarkers, 18);
            _btnAutoPlaceAllMarkers.Pressed += () =>
            {
                EnsureLandmarkPins(true);
                if (_optFingerRigMode != null && (FingerRigMode)_optFingerRigMode.GetItemId(_optFingerRigMode.Selected) == FingerRigMode.GenerateFingerChains)
                {
                    AutoDetectFingerLandmarksInUI();
                }
                UpdateActivePinBadge();
                RefreshPinVisuals();
                UpdatePinProgressChip();
                TriggerRealtimeRigUpdate();
            };
            markerBody.AddChild(_btnAutoPlaceAllMarkers);

            // Build legacy pin panel (hosts _lblActivePinBadge, _chkMirrorPins, _chkShowPins, _btnSelectAllPins)
            _boxLandmarkPins = BuildLandmarkPinPanel();
            markerBody.AddChild(_boxLandmarkPins);

            // Compact icon toggle bar for Mirror & Show Pins (synced with _chkMirrorPins / _chkShowPins)
            var toggleRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            toggleRow.AddThemeConstantOverride("separation", 8);

            _btnMirrorPinsToggle = new Button
            {
                Text = "Mirror L↔R",
                ThemeTypeVariation = "ToolbarButton",
                ToggleMode = true,
                ButtonPressed = _chkMirrorPins?.ButtonPressed ?? true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, ImporterStyle.ButtonHeight),
                TooltipText = "Mirror left and right pins symmetrically"
            };
            SetButtonIcon(_btnMirrorPinsToggle, "res://assets/at-icons/arrows_clockwise.svg", ImporterStyle.IconSize);
            ImporterStyle.ApplyFontSize(_btnMirrorPinsToggle, ImporterStyle.FontHint);
            _btnMirrorPinsToggle.Toggled += (on) =>
            {
                if (_chkMirrorPins != null && _chkMirrorPins.ButtonPressed != on)
                    _chkMirrorPins.ButtonPressed = on;
            };
            toggleRow.AddChild(_btnMirrorPinsToggle);

            _btnShowPinsToggle = new Button
            {
                Text = "Show Pins",
                ThemeTypeVariation = "ToolbarButton",
                ToggleMode = true,
                ButtonPressed = _chkShowPins?.ButtonPressed ?? true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, ImporterStyle.ButtonHeight),
                TooltipText = "Show or hide joint pins in 3D viewport"
            };
            SetButtonIcon(_btnShowPinsToggle, "res://assets/at-icons/eye.svg", ImporterStyle.IconSize);
            ImporterStyle.ApplyFontSize(_btnShowPinsToggle, ImporterStyle.FontHint);
            _btnShowPinsToggle.Toggled += (on) =>
            {
                if (_chkShowPins != null && _chkShowPins.ButtonPressed != on)
                    _chkShowPins.ButtonPressed = on;
            };
            toggleRow.AddChild(_btnShowPinsToggle);

            var btnResetPins = new Button
            {
                Text = "Reset",
                ThemeTypeVariation = "ImporterSecondaryButton",
                CustomMinimumSize = new Vector2(88, ImporterStyle.ButtonHeight),
                TooltipText = "Reset all 16 joint pins from mesh geometry"
            };
            SetButtonIcon(btnResetPins, "res://assets/at-icons/arrow_counterclockwise.svg", 20);
            ImporterStyle.ApplyFontSize(btnResetPins, ImporterStyle.FontHint);
            btnResetPins.Pressed += () =>
            {
                _selectedPin = null; _hoveredPin = null; _draggingPin = null;
                _selectedPins.Clear();
                EnsureLandmarkPins(true);
                UpdateActivePinBadge();
                RefreshPinVisuals();
                UpdatePinProgressChip();
                TriggerRealtimeRigUpdate();
            };
            toggleRow.AddChild(btnResetPins);
            markerBody.AddChild(toggleRow);

            // Collapsed "Which pins to show" foldout
            var (groupsCard, groupsBody) = CreateInspectorCard(
                "Which pins to show",
                "Toggle anatomical pin groups in the 3D viewport.",
                collapsible: true,
                startExpanded: false);
            _chkMarkerHeadNeck = new CheckBox { Text = "Head / Neck / Top", ButtonPressed = true };
            _chkMarkerArms = new CheckBox { Text = "Shoulders / Elbows / Wrists", ButtonPressed = true };
            _chkMarkerLegs = new CheckBox { Text = "Groin / Knees / Ankles", ButtonPressed = true };
            ImporterStyle.ApplyFontSize(_chkMarkerHeadNeck, ImporterStyle.FontBody);
            ImporterStyle.ApplyFontSize(_chkMarkerArms, ImporterStyle.FontBody);
            ImporterStyle.ApplyFontSize(_chkMarkerLegs, ImporterStyle.FontBody);
            _chkMarkerHeadNeck.Toggled += (_) => RefreshPinVisuals();
            _chkMarkerArms.Toggled += (_) => RefreshPinVisuals();
            _chkMarkerLegs.Toggled += (_) => RefreshPinVisuals();
            groupsBody.AddChild(_chkMarkerHeadNeck);
            groupsBody.AddChild(_chkMarkerArms);
            groupsBody.AddChild(_chkMarkerLegs);
            box.AddChild(groupsCard);

            // Keep hidden step switcher row for compatibility with SetLandmarkWorkspaceStep
            _boxLandmarkModeSwitchRow = new HBoxContainer { Visible = false };
            _btnLandmarkStepPins = new Button { Visible = false, ToggleMode = true, ButtonPressed = true };
            _btnLandmarkStepPaint = new Button { Visible = false, ToggleMode = true, ButtonPressed = false };
            _boxLandmarkModeSwitchRow.AddChild(_btnLandmarkStepPins);
            _boxLandmarkModeSwitchRow.AddChild(_btnLandmarkStepPaint);
            box.AddChild(_boxLandmarkModeSwitchRow);

            return box;
        }

        private Control BuildRigStep3Weights()
        {
            var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", ImporterStyle.Gap);
            _boxTab2WeightPaintHost = box;

            _boxWeightPaintSection = BuildWeightPaintPanel();
            box.AddChild(_boxWeightPaintSection);

            if (_btnWeightPaintMode != null)
            {
                _btnWeightPaintMode.ThemeTypeVariation = "ImporterPrimaryButton";
                _btnWeightPaintMode.CustomMinimumSize = new Vector2(0, ImporterStyle.PrimaryButtonHeight);
                SetButtonIcon(_btnWeightPaintMode, "res://assets/at-icons/brush.svg", ImporterStyle.IconSize);
                ImporterStyle.ApplyFontSize(_btnWeightPaintMode, 18);
            }

            // Compact Smooth / Mirror quick action row always visible in Step 3
            var (quickCard, quickBody) = CreateInspectorCard("Quick Weight Cleanup", "Smooth joint seams or mirror weights across X.");
            var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", 8);

            var btnSmooth = new Button
            {
                Text = "Smooth Seam",
                ThemeTypeVariation = "ImporterSecondaryButton",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, ImporterStyle.ButtonHeight)
            };
            ImporterStyle.ApplyFontSize(btnSmooth, ImporterStyle.FontHint);
            btnSmooth.Pressed += OnSmoothBoundaryWeightsPressed;
            row.AddChild(btnSmooth);

            var btnMirrorL = new Button
            {
                Text = "Mirror L → R",
                ThemeTypeVariation = "ImporterSecondaryButton",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, ImporterStyle.ButtonHeight)
            };
            ImporterStyle.ApplyFontSize(btnMirrorL, ImporterStyle.FontHint);
            btnMirrorL.Pressed += () => OnMirrorSideWeightsPressed(leftToRight: true);
            row.AddChild(btnMirrorL);

            quickBody.AddChild(row);
            box.AddChild(quickCard);

            return box;
        }

        private Control BuildRigStep4FitAndHands()
        {
            var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", ImporterStyle.Gap);

            // Pose Fit Card
            var (snapCard, snapBody) = CreateInspectorCard("4. Pose Fit & Body");
            _boxAutoSnapCard = snapCard;
            _boxAutoSnapOptions = snapBody;
            box.AddChild(snapCard);

            _lblAutoSnapModeHint = new Label { Text = "Aligns limbs and torso to Deadlock's bind pose before animation playback.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            ImporterStyle.ApplyHintStyle(_lblAutoSnapModeHint);
            _secondaryLabels.Add(_lblAutoSnapModeHint);
            snapBody.AddChild(_lblAutoSnapModeHint);

            // Single Pose Fit preset dropdown (Full fit / Arms only / None)
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

            // Body row: Symmetry + Spine Proportions
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

            // Collapsed "Custom..." foldout for individual Pose Fit, Extras, Pre-bind Hand Tune, and Deform Guards
            var (customCard, customBody) = CreateInspectorCard(
                "Custom… (Limb Fit, Extras & Guards)",
                "Fine-grained limb snapping, twist bones, and deformation guards.",
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

            // Deformation Guards inside the same Custom... foldout
            _boxDeformGuardsCard = customCard;
            _chkClampLimbStretching = AddDescribedCheckBox("Clamp Limb Stretching (1.5 cm max)", "Prevents melee/movement animations from pulling elbows/wrists apart.", "Clamps limb stretch.", true, customBody);
            _chkStabilizeNeckFloatingHead = AddDescribedCheckBox("Stabilize Neck (Floating-Head Heroes)", "Anchors head & neck to collar pivot for heroes like Paradox.", "Stabilizes floating-head necks.", _heroCodename == "chrono", customBody);
            _chkAutoCounterTwistWeights = AddDescribedCheckBox("Auto Counter-Twist Thigh & Shoulder Weights", "Distributes proximal twist weights onto *_TWIST bones.", "Procedural counter-twist distribution.", true, customBody);

            // Unified Hands & Fingers Section
            var (fingerCard, fingerBody) = CreateInspectorCard("Hands & Fingers", "Choose rigid mitten hands, remap Deadlock finger bones, or generate finger chains.");
            _boxFingerCard = fingerCard;
            box.AddChild(fingerCard);

            // Hidden backing OptionButtons (data source for GatherRiggingOptions)
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

            // Segmented control: Rigid mitten | Remap Deadlock fingers | Generate fingers
            var segRow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            segRow.AddThemeConstantOverride("h_separation", 6);
            segRow.AddThemeConstantOverride("v_separation", 6);
            string[] segLabels = { "Rigid mitten", "Remap Deadlock fingers", "Generate fingers" };
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                var segBtn = new Button
                {
                    Text = segLabels[i],
                    ThemeTypeVariation = "ImporterStepChip",
                    ToggleMode = true,
                    ButtonPressed = i == 1,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    CustomMinimumSize = new Vector2(0, 38)
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

            _boxFingerLandmarkTools = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            _boxFingerLandmarkTools.AddThemeConstantOverride("separation", 8);
            fingerBody.AddChild(_boxFingerLandmarkTools);

            var fBtnRow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            fBtnRow.AddThemeConstantOverride("h_separation", 8);
            fBtnRow.AddThemeConstantOverride("v_separation", 6);
            var btnDetectFingers = new Button
            {
                Text = "Detect Fingertips",
                ThemeTypeVariation = "ImporterSecondaryButton",
                CustomMinimumSize = new Vector2(140, 36)
            };
            ImporterStyle.ApplyFontSize(btnDetectFingers, ImporterStyle.FontHint);
            btnDetectFingers.Pressed += () => { AutoDetectFingerLandmarksInUI(); RefreshPinVisuals(); TriggerRealtimeRigUpdate(); };
            fBtnRow.AddChild(btnDetectFingers);

            var btnMirrorFingers = new Button
            {
                Text = "Mirror L → R",
                ThemeTypeVariation = "ImporterSecondaryButton",
                CustomMinimumSize = new Vector2(120, 36)
            };
            ImporterStyle.ApplyFontSize(btnMirrorFingers, ImporterStyle.FontHint);
            btnMirrorFingers.Pressed += () =>
            {
                _activeFingerStrategy.MirrorLandmarks(_fingerLandmarks, leftToRight: true, _accLocalBoundsCenter);
                RefreshPinVisuals();
                TriggerRealtimeRigUpdate();
            };
            fBtnRow.AddChild(btnMirrorFingers);
            _boxFingerLandmarkTools.AddChild(fBtnRow);

            _chkGenMetacarpals = new CheckBox { Text = "Generate Metacarpal Bones", ButtonPressed = false };
            _chkGenWristTwist = new CheckBox { Text = "Generate Forearm/Wrist Twist Bone", ButtonPressed = false };
            ImporterStyle.ApplyFontSize(_chkGenMetacarpals, ImporterStyle.FontBody);
            ImporterStyle.ApplyFontSize(_chkGenWristTwist, ImporterStyle.FontBody);
            _chkGenMetacarpals.Toggled += (_) => TriggerRealtimeRigUpdate();
            _chkGenWristTwist.Toggled += (_) => TriggerRealtimeRigUpdate();
            _boxFingerLandmarkTools.AddChild(_chkGenMetacarpals);
            _boxFingerLandmarkTools.AddChild(_chkGenWristTwist);

            _lblFingerPinStatus = new Label { Text = "No finger landmarks placed yet.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            ImporterStyle.ApplyHintStyle(_lblFingerPinStatus);
            _secondaryLabels.Add(_lblFingerPinStatus);
            _boxFingerLandmarkTools.AddChild(_lblFingerPinStatus);

            // Live pose-test strip: Rest / Fist / Open / Point
            var lblPoseStrip = new Label { Text = "Live Pose Test:" };
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
                    CustomMinimumSize = new Vector2(0, 36)
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

            return box;
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
                Title = "Auto-rig from landmarks",
                Description = "Place 16 anatomical pins to build a clean Deadlock skeleton.",
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

        private bool IsWizardStepApplicable(int stepIdx, CustomModelAutoRigger.RiggingMethod method)
        {
            return method switch
            {
                CustomModelAutoRigger.RiggingMethod.RigidSingleBone => stepIdx == 0,
                CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap => stepIdx is 0 or 2 or 3,
                CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection => stepIdx is 0 or 2 or 3,
                _ => true // LandmarkAutoRig: all 4 steps apply
            };
        }

        private void SetRigWizardStep(int stepIdx)
        {
            var method = GetCurrentRiggingMethod();
            if (!IsWizardStepApplicable(stepIdx, method))
            {
                // Snap to nearest applicable step when called programmatically
                stepIdx = 0;
            }
            _rigWizardStep = Mathf.Clamp(stepIdx, 0, 3);

            for (int i = 0; i < _rigStepPages.Length; i++)
            {
                if (_rigStepPages[i] != null)
                    _rigStepPages[i].Visible = i == _rigWizardStep;
            }

            if (_rigStepper != null)
            {
                _rigStepper.SetCurrentStep(_rigWizardStep);
            }

            // Entering Step 3 (Weights) -> if Landmark mode, sync paint view
            if (_rigWizardStep == 2 && method == CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig)
            {
                if (_btnWeightPaintMode != null) _btnWeightPaintMode.Visible = true;
            }
            else if (method == CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig && _rigWizardStep == 1)
            {
                if (_btnWeightPaintMode != null && _btnWeightPaintMode.ButtonPressed)
                    _btnWeightPaintMode.ButtonPressed = false;
            }

            SyncRigWizardNavButtons(method);
            UpdatePinProgressChip();
        }

        private void UpdateAccessoryRigVisibility()
        {
            bool isAccessory = _workflowIntent == ImportWorkflowIntent.AccessoryAddOrReplace;
            var accMode = (_optAccBoneMode != null && _optAccBoneMode.Selected >= 0)
                ? (AccessoryBoneMode)_optAccBoneMode.GetItemId(_optAccBoneMode.Selected)
                : AccessoryBoneMode.RigidToOneBone;
            bool needFullRigWizard = !isAccessory || accMode == AccessoryBoneMode.SkinToHero;

            if (_rigStepper != null) _rigStepper.Visible = needFullRigWizard;
            if (_boxTab2WizardScroll != null) _boxTab2WizardScroll.Visible = needFullRigWizard;
            if (_boxTab2WizardNavFooter != null) _boxTab2WizardNavFooter.Visible = needFullRigWizard;
            if (_boxAccessoryRigNotice != null) _boxAccessoryRigNotice.Visible = !needFullRigWizard;
        }

        private void SyncRigWizardNavButtons(CustomModelAutoRigger.RiggingMethod method)
        {
            if (_rigStepper != null)
            {
                _rigStepper.SetStepTitle(0, method == CustomModelAutoRigger.RiggingMethod.RigidSingleBone ? "Pick Bone" : "Method");
                _rigStepper.SetStepTitle(2, method == CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap ? "Weights (Opt.)" : "Weights");
                for (int i = 0; i < 4; i++)
                    _rigStepper.SetStepApplicable(i, IsWizardStepApplicable(i, method));

                _rigStepper.SetStepState(0, WizardStepState.Done);
                bool pinsOk = _landmarkPins != null && _landmarkPins.Count >= 16;
                _rigStepper.SetStepState(1, pinsOk ? WizardStepState.Done : WizardStepState.Attention);
            }

            if (_btnRigWizardBack != null)
            {
                bool hasPrev = false;
                for (int i = _rigWizardStep - 1; i >= 0; i--)
                {
                    if (IsWizardStepApplicable(i, method)) { hasPrev = true; break; }
                }
                _btnRigWizardBack.Disabled = !hasPrev;
            }

            if (_btnRigWizardNext != null)
            {
                bool hasNext = false;
                for (int i = _rigWizardStep + 1; i < 4; i++)
                {
                    if (IsWizardStepApplicable(i, method)) { hasNext = true; break; }
                }
                _btnRigWizardNext.Text = hasNext ? "Next →" : "Review in Export tab →";
            }

            if (_btnUseKnnMethod != null)
                _btnUseKnnMethod.SetPressedNoSignal(method == CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection);

            if (_rigMethodCards != null && method != CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection)
                _rigMethodCards.Select((int)method);
        }

        private void OnRigWizardBackPressed()
        {
            var method = GetCurrentRiggingMethod();
            for (int i = _rigWizardStep - 1; i >= 0; i--)
            {
                if (IsWizardStepApplicable(i, method))
                {
                    SetRigWizardStep(i);
                    return;
                }
            }
        }

        private void OnRigWizardNextPressed()
        {
            var method = GetCurrentRiggingMethod();
            for (int i = _rigWizardStep + 1; i < 4; i++)
            {
                if (IsWizardStepApplicable(i, method))
                {
                    SetRigWizardStep(i);
                    return;
                }
            }
            // Last step -> Review in Export tab
            if (_rightTabs != null && _rightTabs.GetTabCount() > 4)
            {
                _rightTabs.CurrentTab = 4;
            }
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
            int placed = _landmarkPins?.Count ?? 0;
            _lblPinProgressChip.Text = $"{placed} / 16 placed";
        }

        private void OnFingerRigModeChanged(long idx)
        {
            if (_optFingerRigMode == null) return;
            var mode = (FingerRigMode)_optFingerRigMode.GetItemId((int)idx);
            for (int i = 0; i < _fingerSegButtons.Length; i++)
                _fingerSegButtons[i]?.SetPressedNoSignal(i == (int)idx);

            if (_boxFingerLandmarkTools != null)
                _boxFingerLandmarkTools.Visible = mode == FingerRigMode.GenerateFingerChains;
            if (_chkMarkerFingers != null)
                _chkMarkerFingers.SetPressedNoSignal(mode == FingerRigMode.GenerateFingerChains);
            if (_chkKeepFingersRigid != null && !_isUpdatingBoneDropdowns)
            {
                _isUpdatingBoneDropdowns = true;
                _chkKeepFingersRigid.SetPressedNoSignal(mode == FingerRigMode.RigidHand);
                _isUpdatingBoneDropdowns = false;
            }
            if (mode == FingerRigMode.GenerateFingerChains && _fingerLandmarks.Count == 0)
            {
                AutoDetectFingerLandmarksInUI();
            }
            RefreshPinVisuals();
            TriggerRealtimeRigUpdate();
        }

        private void AutoDetectFingerLandmarksInUI()
        {
            if (_liveImportedScene == null) return;
            EnsureLandmarkPins(false);
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
