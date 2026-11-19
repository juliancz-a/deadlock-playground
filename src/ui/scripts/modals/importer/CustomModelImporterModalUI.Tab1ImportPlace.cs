using System;
using Godot;
using DeadlockPlayground.Importer;
using DeadlockPlayground.Theming;

namespace DeadlockPlayground.UI
{
    public partial class CustomModelImporterModalUI
    {
        // Phase 5 Tab 1 controls: Mode Picker First + Progressive Disclosure
        private OptionButton _optWorkflowMode;
        private Button _btnTab1AdvancedToggle;
        private VBoxContainer _boxTab1Advanced;
        private OptionButton _optAccBoneMode;

        private Control BuildTab1ImportAndPlace()
        {
            var scroll = new ScrollContainer
            {
                Name = "1. Import & Place",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };

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

            // Card 0: Import Mode Picker First (Full Character / Accessory / Weapon)
            var (modeCard, modeBody) = CreateInspectorCard("Import Mode", "Choose what this 3D asset is; only relevant options will be shown.");
            vBox.AddChild(modeCard);

            _optWorkflowMode = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            ConfigureOptionButton(_optWorkflowMode);
            _optWorkflowMode.AddItem("Full Character Replacement", 0);
            _optWorkflowMode.AddItem("Accessory / Outfit Part (Hat, Cape, Shoes)", 1);
            _optWorkflowMode.AddItem("Weapon / Handheld Prop (Rigid Socket)", 2);
            _optWorkflowMode.Select(_workflowIntent == ImportWorkflowIntent.AccessoryAddOrReplace ? 1 : 0);
            _optWorkflowMode.ItemSelected += OnWorkflowModeChanged;
            modeBody.AddChild(_optWorkflowMode);

            // Accessory Setup Card (shown in Accessory or Weapon mode)
            var (accCard, accBody) = CreateInspectorCard("Accessory & Bone Mode Setup", "Add a standalone accessory (hat, glasses, cape) or swap specific hero parts.");
            accCard.Visible = false;
            _boxAccessorySetupCard = accCard;

            var accNameRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            accNameRow.AddThemeConstantOverride("separation", 6);
            accNameRow.AddChild(new Label { Text = "Slot / Mesh Name:", CustomMinimumSize = new Vector2(115, 0) });
            _txtAccName = new LineEdit
            {
                PlaceholderText = "Accessory display name...",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Display name shown in Active Accessories list and Mod Export changes"
            };
            accNameRow.AddChild(_txtAccName);
            accBody.AddChild(accNameRow);

            var accBoneModeRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            accBoneModeRow.AddThemeConstantOverride("separation", 6);
            accBoneModeRow.AddChild(new Label { Text = "Accessory Bones:", CustomMinimumSize = new Vector2(115, 0) });
            _optAccBoneMode = new OptionButton
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Rigid: attaches whole mesh to 1 hero bone.\nKeep Own Bones: preserves accessory skeleton chains (e.g. cape/tail physics) under the chosen hero bone.\nSkin to Hero: binds accessory vertices to the hero skeleton."
            };
            ConfigureOptionButton(_optAccBoneMode);
            _optAccBoneMode.AddItem("Rigid to One Bone (Hats / Props)", (int)AccessoryBoneMode.RigidToOneBone);
            _optAccBoneMode.AddItem("Keep Own Bones (Physics / Cape / Tail)", (int)AccessoryBoneMode.KeepOwnBones);
            _optAccBoneMode.AddItem("Skin to Hero Skeleton (Clothing / Armor)", (int)AccessoryBoneMode.SkinToHero);
            _optAccBoneMode.Select(0);
            _optAccBoneMode.ItemSelected += (_) =>
            {
                UpdateAccessoryRigVisibility();
                TriggerRealtimeRigUpdate();
            };
            accBoneModeRow.AddChild(_optAccBoneMode);
            accBody.AddChild(accBoneModeRow);

            var intentRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            intentRow.AddThemeConstantOverride("separation", 6);
            _btnAccIntentAdd = new Button
            {
                Text = "+ Add New Accessory",
                ThemeTypeVariation = "ToolbarButton",
                ToggleMode = true,
                ButtonPressed = true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 34),
                TooltipText = "Keep all existing hero meshes and attach a new accessory (hat, glasses, backpack, weapon)."
            };
            SetButtonIcon(_btnAccIntentAdd, "res://assets/at-icons/sparkles.svg", 18, StatusTone.Success);
            _btnAccIntentReplace = new Button
            {
                Text = "⇄ Replace Hero Part(s)",
                ThemeTypeVariation = "ToolbarButton",
                ToggleMode = true,
                ButtonPressed = false,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 34),
                TooltipText = "Hide one or more vanilla hero meshes/surfaces and replace them with this imported part."
            };
            SetButtonIcon(_btnAccIntentReplace, "res://assets/at-icons/layers.svg", 18);
            _btnAccIntentAdd.Pressed += () => SetAccessoryIntent(CustomModelAutoRigger.AccessoryIntent.AddAccessory);
            _btnAccIntentReplace.Pressed += () => SetAccessoryIntent(CustomModelAutoRigger.AccessoryIntent.ReplacePart);
            intentRow.AddChild(_btnAccIntentAdd);
            intentRow.AddChild(_btnAccIntentReplace);
            accBody.AddChild(intentRow);

            var boneHdrRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            boneHdrRow.AddChild(new Label { Text = "Attachment / Anchor Bone:", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
            _chkAccAllBones = new CheckBox
            {
                Text = "Show all skeleton bones",
                ButtonPressed = false,
                TooltipText = "Toggle between curated quick-pick bones (Head, Chest, Hands, Pelvis) and the full hero skeleton."
            };
            _chkAccAllBones.Toggled += (_) => PopulateAccQuickBonePicker();
            boneHdrRow.AddChild(_chkAccAllBones);
            accBody.AddChild(boneHdrRow);

            _optAccQuickBone = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            ConfigureOptionButton(_optAccQuickBone);
            _optAccQuickBone.ItemSelected += OnAccQuickBoneSelected;
            accBody.AddChild(_optAccQuickBone);

            var accToolsRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            accToolsRow.AddThemeConstantOverride("separation", 6);
            _btnAccSnapBone = new Button
            {
                Text = "Auto-Fit to Selected Bone",
                ThemeTypeVariation = "ToolbarButton",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 34),
                TooltipText = "Normalizes model size and snaps it onto the selected bone in 3D space."
            };
            SetButtonIcon(_btnAccSnapBone, "res://assets/at-icons/magnet.svg", 18, StatusTone.Warning);
            _btnAccSnapBone.Pressed += SnapAccessoryToSelectedBone;
            accToolsRow.AddChild(_btnAccSnapBone);

            _btnAccGizmoToggle = new Button
            {
                Text = "3D Gizmo",
                ThemeTypeVariation = "ToolbarButton",
                ToggleMode = true,
                ButtonPressed = true,
                CustomMinimumSize = new Vector2(106, 34),
                TooltipText = "Show/hide interactive 3D move & rotate gizmo right on this accessory."
            };
            SetButtonIcon(_btnAccGizmoToggle, "res://assets/at-icons/move.svg", 18);
            _btnAccGizmoToggle.Toggled += (on) =>
            {
                if (_optGizmoTarget != null) SelectGizmoTargetById(on ? 3 : 0);
            };
            accToolsRow.AddChild(_btnAccGizmoToggle);
            accBody.AddChild(accToolsRow);

            var accFlipRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            accFlipRow.AddThemeConstantOverride("separation", 6);
            _btnAccMirrorLr = new Button
            {
                Text = "Mirror L <-> R (Flip X + Swap Bone)",
                ThemeTypeVariation = "ToolbarButton",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 32),
                TooltipText = "Flips accessory geometry across X (e.g. left shoe -> right shoe), swaps _L <-> _R bone if applicable, and mirrors its X offset & yaw/roll across the hero."
            };
            SetButtonIcon(_btnAccMirrorLr, "res://assets/at-icons/mirror_horizontally.svg", 18);
            _btnAccMirrorLr.Pressed += MirrorAccessoryLeftRight;
            accFlipRow.AddChild(_btnAccMirrorLr);
            accBody.AddChild(accFlipRow);

            var replacePanel = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            replacePanel.AddThemeConstantOverride("separation", 6);
            _boxAccReplacePanel = replacePanel;
            replacePanel.AddChild(new HSeparator());
            var lblRep = new Label
            {
                Text = "Hero Part(s) to Hide & Replace (checked parts disappear on hero):",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            _secondaryLabels.Add(lblRep);
            replacePanel.AddChild(lblRep);

            var repScroll = new ScrollContainer
            {
                CustomMinimumSize = new Vector2(0, 145),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };
            _boxAccReplaceList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            repScroll.AddChild(_boxAccReplaceList);
            replacePanel.AddChild(repScroll);
            accBody.AddChild(replacePanel);

            // Hero Mesh Fit-Deform Section
            accBody.AddChild(new HSeparator());
            _chkDeformEnabled = new CheckBox
            {
                Text = "Deform Original Hero Mesh (e.g. Flatten Hair for Hat)",
                ButtonPressed = false,
                TooltipText = "Gently squashes or shrinks targeted vanilla hero geometry (like hair) around the attachment bone so accessories fit cleanly without clipping."
            };
            _chkDeformEnabled.Toggled += (on) =>
            {
                if (_boxDeformControls != null) _boxDeformControls.Visible = on;
                if (!on && _btnDeformBrushToggle != null && _btnDeformBrushToggle.ButtonPressed)
                    _btnDeformBrushToggle.ButtonPressed = false;
                ApplyPreviewHeroDeformation();
            };
            accBody.AddChild(_chkDeformEnabled);

            var deformControls = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            deformControls.AddThemeConstantOverride("separation", 5);
            _boxDeformControls = deformControls;

            var defTargetRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            defTargetRow.AddChild(new Label { Text = "Target Part:", CustomMinimumSize = new Vector2(90, 0) });
            _optDeformTarget = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            ConfigureOptionButton(_optDeformTarget);
            _optDeformTarget.ItemSelected += (_) => ApplyPreviewHeroDeformation();
            defTargetRow.AddChild(_optDeformTarget);
            deformControls.AddChild(defTargetRow);

            var defModeRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            defModeRow.AddChild(new Label { Text = "Deform Mode:", CustomMinimumSize = new Vector2(90, 0) });
            _optDeformMode = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            ConfigureOptionButton(_optDeformMode);
            _optDeformMode.AddItem("Flatten Top-Down (Hat Fit)", (int)CustomModelAutoRigger.HeroDeformMode.FlattenTopDown);
            _optDeformMode.AddItem("Radial Shrink (Inward)", (int)CustomModelAutoRigger.HeroDeformMode.RadialShrink);
            _optDeformMode.ItemSelected += (_) => ApplyPreviewHeroDeformation();
            defModeRow.AddChild(_optDeformMode);
            deformControls.AddChild(defModeRow);

            var strRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            strRow.AddChild(new Label { Text = "Strength:", CustomMinimumSize = new Vector2(90, 0) });
            _sliderDeformStrength = new HSlider { MinValue = 0.0, MaxValue = 1.0, Step = 0.01, Value = 0.55, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            _lblDeformStrengthVal = new Label { Text = "55%", CustomMinimumSize = new Vector2(44, 0), HorizontalAlignment = HorizontalAlignment.Right };
            _sliderDeformStrength.ValueChanged += (v) =>
            {
                if (_lblDeformStrengthVal != null) _lblDeformStrengthVal.Text = $"{Mathf.RoundToInt((float)v * 100)}%";
                ApplyPreviewHeroDeformation();
            };
            strRow.AddChild(_sliderDeformStrength);
            strRow.AddChild(_lblDeformStrengthVal);
            deformControls.AddChild(strRow);

            var radRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            radRow.AddChild(new Label { Text = "Radius:", CustomMinimumSize = new Vector2(90, 0) });
            _sliderDeformRadius = new HSlider { MinValue = 0.08, MaxValue = 0.55, Step = 0.01, Value = 0.24, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            _lblDeformRadiusVal = new Label { Text = "0.24m", CustomMinimumSize = new Vector2(44, 0), HorizontalAlignment = HorizontalAlignment.Right };
            _sliderDeformRadius.ValueChanged += (v) =>
            {
                if (_lblDeformRadiusVal != null) _lblDeformRadiusVal.Text = $"{v:F2}m";
                ApplyPreviewHeroDeformation();
            };
            radRow.AddChild(_sliderDeformRadius);
            radRow.AddChild(_lblDeformRadiusVal);
            deformControls.AddChild(radRow);

            var hRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            hRow.AddChild(new Label { Text = "Height Off.:", CustomMinimumSize = new Vector2(90, 0) });
            _sliderDeformHeight = new HSlider { MinValue = -0.25, MaxValue = 0.25, Step = 0.005, Value = 0.06, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            _lblDeformHeightVal = new Label { Text = "+0.06m", CustomMinimumSize = new Vector2(44, 0), HorizontalAlignment = HorizontalAlignment.Right };
            _sliderDeformHeight.ValueChanged += (v) =>
            {
                if (_lblDeformHeightVal != null) _lblDeformHeightVal.Text = $"{(v >= 0 ? "+" : "")}{v:F2}m";
                ApplyPreviewHeroDeformation();
            };
            hRow.AddChild(_sliderDeformHeight);
            hRow.AddChild(_lblDeformHeightVal);
            deformControls.AddChild(hRow);

            deformControls.AddChild(new HSeparator());
            var brushHeader = new Label { Text = "3D Deform Sculpt Brush (Interactive)" };
            ImporterStyle.ApplyFontSize(brushHeader, ImporterStyle.FontBody);
            brushHeader.AddThemeColorOverride("font_color", ThemeManager.GetColor(nameof(ThemePaletteData.AccentPrimary)));
            deformControls.AddChild(brushHeader);

            _btnDeformBrushToggle = new Button
            {
                Text = "Sculpt Hero Mesh in 3D",
                ToggleMode = true,
                CustomMinimumSize = new Vector2(0, 30),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Enable interactive sculpting directly on the original hero mesh in the 3D viewport.\nLeft-drag to sculpt, [ / ] to resize brush, Ctrl+Z to undo."
            };
            _btnDeformBrushToggle.Toggled += OnDeformBrushToggled;
            deformControls.AddChild(_btnDeformBrushToggle);

            var brushModeRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            brushModeRow.AddChild(new Label { Text = "Brush Mode:", CustomMinimumSize = new Vector2(90, 0) });
            _optDeformBrushMode = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            ConfigureOptionButton(_optDeformBrushMode);
            _optDeformBrushMode.AddItem("Push Inward (Tuck)", (int)CustomModelAutoRigger.HeroSculptBrushMode.PushIn);
            _optDeformBrushMode.AddItem("Pull Outward", (int)CustomModelAutoRigger.HeroSculptBrushMode.PullOut);
            _optDeformBrushMode.AddItem("Flatten to Surface", (int)CustomModelAutoRigger.HeroSculptBrushMode.Flatten);
            _optDeformBrushMode.AddItem("Smooth", (int)CustomModelAutoRigger.HeroSculptBrushMode.Smooth);
            _optDeformBrushMode.AddItem("Erase Sculpt", (int)CustomModelAutoRigger.HeroSculptBrushMode.Erase);
            brushModeRow.AddChild(_optDeformBrushMode);
            deformControls.AddChild(brushModeRow);

            var brushRadRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            brushRadRow.AddChild(new Label { Text = "Brush Size:", CustomMinimumSize = new Vector2(90, 0) });
            _sliderDeformBrushRadius = new HSlider { MinValue = 0.02, MaxValue = 0.30, Step = 0.005, Value = 0.08, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            _lblDeformBrushRadiusVal = new Label { Text = "0.08m", CustomMinimumSize = new Vector2(44, 0), HorizontalAlignment = HorizontalAlignment.Right };
            _sliderDeformBrushRadius.ValueChanged += (v) =>
            {
                if (_lblDeformBrushRadiusVal != null) _lblDeformBrushRadiusVal.Text = $"{v:F2}m";
            };
            brushRadRow.AddChild(_sliderDeformBrushRadius);
            brushRadRow.AddChild(_lblDeformBrushRadiusVal);
            deformControls.AddChild(brushRadRow);

            var brushStrRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            brushStrRow.AddChild(new Label { Text = "Brush Power:", CustomMinimumSize = new Vector2(90, 0) });
            _sliderDeformBrushStrength = new HSlider { MinValue = 0.05, MaxValue = 1.0, Step = 0.01, Value = 0.45, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            _lblDeformBrushStrengthVal = new Label { Text = "45%", CustomMinimumSize = new Vector2(44, 0), HorizontalAlignment = HorizontalAlignment.Right };
            _sliderDeformBrushStrength.ValueChanged += (v) =>
            {
                if (_lblDeformBrushStrengthVal != null) _lblDeformBrushStrengthVal.Text = $"{Mathf.RoundToInt((float)v * 100)}%";
            };
            brushStrRow.AddChild(_sliderDeformBrushStrength);
            brushStrRow.AddChild(_lblDeformBrushStrengthVal);
            deformControls.AddChild(brushStrRow);

            var brushActionRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _chkDeformBrushMirror = new CheckBox { Text = "Mirror X", ButtonPressed = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            brushActionRow.AddChild(_chkDeformBrushMirror);
            _btnDeformBrushUndo = new Button { Text = "Undo", Disabled = true, CustomMinimumSize = new Vector2(58, 0) };
            _btnDeformBrushUndo.Pressed += OnDeformBrushUndoPressed;
            brushActionRow.AddChild(_btnDeformBrushUndo);
            _btnDeformBrushClear = new Button { Text = "Clear Sculpt", Disabled = true, CustomMinimumSize = new Vector2(90, 0) };
            _btnDeformBrushClear.Pressed += OnDeformBrushClearPressed;
            brushActionRow.AddChild(_btnDeformBrushClear);
            deformControls.AddChild(brushActionRow);

            _lblDeformBrushStatus = new Label { Text = "No sculpted vertices yet.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            ImporterStyle.ApplyHintStyle(_lblDeformBrushStatus);
            _secondaryLabels.Add(_lblDeformBrushStatus);
            deformControls.AddChild(_lblDeformBrushStatus);
            accBody.AddChild(deformControls);
            vBox.AddChild(accCard);

            // Card A: Placement & Scale (Common Controls)
            var (transformCard, tBody) = CreateInspectorCard("Placement & Scale");
            vBox.AddChild(transformCard);

            _lblGeometryStats = new Label { Text = "Geometry: 0 vertices | 0 triangles", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            _secondaryLabels.Add(_lblGeometryStats);
            tBody.AddChild(_lblGeometryStats);

            var fitHBox = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            fitHBox.AddThemeConstantOverride("h_separation", 8);
            fitHBox.AddThemeConstantOverride("v_separation", 6);

            _btnAutoFit = new Button { Text = "Auto-Fit Height & Center", ThemeTypeVariation = "ToolbarButton", CustomMinimumSize = new Vector2(165, 34) };
            SetButtonIcon(_btnAutoFit, "res://assets/at-icons/sparkles.svg", 22, StatusTone.Warning);
            _btnAutoFit.Pressed += AutoFitToHero;
            fitHBox.AddChild(_btnAutoFit);

            _btnFlipYaw = new Button
            {
                Text = "Flip 180°",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(96, 34),
                TooltipText = "Rotates model 180° around Y axis (Yaw) to match hero facing direction"
            };
            SetButtonIcon(_btnFlipYaw, "res://assets/at-icons/rotate.svg", 22);
            _btnFlipYaw.Pressed += () =>
            {
                _userModifiedTransform = true;
                double cur = _spinRotYaw.Value;
                double next = (cur + 180.0) % 360.0;
                if (next > 180.0) next -= 360.0;
                if (next < -180.0) next += 360.0;
                _spinRotYaw.SetValueNoSignal(next);
                ApplyTransformToPreviewModel(immediate: true);
            };
            fitHBox.AddChild(_btnFlipYaw);

            _btnSnapFeet = new Button { Text = "Snap Feet (Y=0)", ThemeTypeVariation = "ToolbarButton", CustomMinimumSize = new Vector2(130, 34) };
            SetButtonIcon(_btnSnapFeet, "res://assets/at-icons/arrow_down.svg", 22);
            _btnSnapFeet.Pressed += () => { _userModifiedTransform = true; SnapFeetToFloor(); };
            fitHBox.AddChild(_btnSnapFeet);

            _btnResetTransform = new Button { Text = "Reset", ThemeTypeVariation = "ToolbarButton", CustomMinimumSize = new Vector2(82, 34) };
            SetButtonIcon(_btnResetTransform, "res://assets/at-icons/arrow_counterclockwise.svg", 22);
            _btnResetTransform.Pressed += () => { _userModifiedTransform = true; ResetTransform(); };
            fitHBox.AddChild(_btnResetTransform);
            tBody.AddChild(fitHBox);

            tBody.AddChild(new Label { Text = "Uniform Scale Factor:" });
            var scaleHBox = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _spinScale = new SpinBox { MinValue = 0.0001, MaxValue = 500.0, Step = 0.001, Value = 1.0, CustomMinimumSize = new Vector2(90, 0) };
            _spinScale.ValueChanged += (v) =>
            {
                _userModifiedTransform = true;
                if (!Mathf.IsEqualApprox((float)_sliderScale.Value, (float)v)) _sliderScale.SetValueNoSignal(v);
                ApplyScaleChangeKeepingCenter();
            };
            scaleHBox.AddChild(_spinScale);

            _sliderScale = new HSlider { MinValue = 0.001, MaxValue = 10.0, Step = 0.001, Value = 1.0, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            _sliderScale.ValueChanged += (v) =>
            {
                _userModifiedTransform = true;
                if (!Mathf.IsEqualApprox((float)_spinScale.Value, (float)v)) _spinScale.SetValueNoSignal(v);
                ApplyScaleChangeKeepingCenter();
            };
            scaleHBox.AddChild(_sliderScale);
            tBody.AddChild(scaleHBox);

            tBody.AddChild(new Label { Text = "Position Offset (X, Y, Z meters):" });
            var posHBox = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _spinPosX = CreateCoordSpinBox(-50.0, 50.0, 0.0001, "X");
            _spinPosY = CreateCoordSpinBox(-50.0, 50.0, 0.0001, "Y");
            _spinPosZ = CreateCoordSpinBox(-50.0, 50.0, 0.0001, "Z");
            _spinPosX.CustomArrowStep = 0.01; _spinPosY.CustomArrowStep = 0.01; _spinPosZ.CustomArrowStep = 0.01;
            posHBox.AddChild(_spinPosX); posHBox.AddChild(_spinPosY); posHBox.AddChild(_spinPosZ);
            tBody.AddChild(posHBox);
            _spinPosX.ValueChanged += (_) => { _userModifiedTransform = true; ApplyTransformToPreviewModel(); };
            _spinPosY.ValueChanged += (_) => { _userModifiedTransform = true; ApplyTransformToPreviewModel(); };
            _spinPosZ.ValueChanged += (_) => { _userModifiedTransform = true; ApplyTransformToPreviewModel(); };

            tBody.AddChild(new Label { Text = "Rotation (Pitch, Yaw, Roll degrees):" });
            var rotHBox = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _spinRotPitch = CreateCoordSpinBox(-180.0, 180.0, 0.01, "Pitch");
            _spinRotYaw = CreateCoordSpinBox(-180.0, 180.0, 0.01, "Yaw");
            _spinRotRoll = CreateCoordSpinBox(-180.0, 180.0, 0.01, "Roll");
            _spinRotPitch.CustomArrowStep = 1.0; _spinRotYaw.CustomArrowStep = 1.0; _spinRotRoll.CustomArrowStep = 1.0;
            rotHBox.AddChild(_spinRotPitch); rotHBox.AddChild(_spinRotYaw); rotHBox.AddChild(_spinRotRoll);
            tBody.AddChild(rotHBox);
            _spinRotPitch.ValueChanged += (_) => { _userModifiedTransform = true; ApplyTransformToPreviewModel(); };
            _spinRotYaw.ValueChanged += (_) => { _userModifiedTransform = true; ApplyTransformToPreviewModel(); };
            _spinRotRoll.ValueChanged += (_) => { _userModifiedTransform = true; ApplyTransformToPreviewModel(); };

            // Progressive Disclosure Toggle for Advanced Placement & Pose Calibration
            _btnTab1AdvancedToggle = new Button
            {
                Text = "▶ Advanced Placement, Flip & KNN Pose Calibration",
                ToggleMode = true,
                ButtonPressed = false,
                Alignment = HorizontalAlignment.Left,
                ThemeTypeVariation = "ToolbarButton",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Expand non-uniform XYZ scaling, geometry mirroring, and legacy KNN reference pose sliders."
            };
            _boxTab1Advanced = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            _boxTab1Advanced.AddThemeConstantOverride("separation", 10);
            _btnTab1AdvancedToggle.Toggled += (open) =>
            {
                _boxTab1Advanced.Visible = open;
                _btnTab1AdvancedToggle.Text = (open ? "▼ " : "▶ ") + "Advanced Placement, Flip & KNN Pose Calibration";
            };
            vBox.AddChild(_btnTab1AdvancedToggle);
            vBox.AddChild(_boxTab1Advanced);

            // Advanced Card: Non-Uniform Scale & Flip
            var (advScaleCard, advScaleBody) = CreateInspectorCard("Non-Uniform Deform Scale & Mirror");
            _boxTab1Advanced.AddChild(advScaleCard);
            advScaleBody.AddChild(new Label { Text = "Deform Scale (X, Y, Z multiplier):" });
            var scaleXyzHBox = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            scaleXyzHBox.AddThemeConstantOverride("separation", 4);
            _spinScaleX = CreateCoordSpinBox(0.05, 10.0, 0.01, "SX");
            _spinScaleY = CreateCoordSpinBox(0.05, 10.0, 0.01, "SY");
            _spinScaleZ = CreateCoordSpinBox(0.05, 10.0, 0.01, "SZ");
            _spinScaleX.SetValueNoSignal(1.0); _spinScaleY.SetValueNoSignal(1.0); _spinScaleZ.SetValueNoSignal(1.0);
            _spinScaleX.ValueChanged += (_) => { _userModifiedTransform = true; ApplyScaleChangeKeepingCenter(); };
            _spinScaleY.ValueChanged += (_) => { _userModifiedTransform = true; ApplyScaleChangeKeepingCenter(); };
            _spinScaleZ.ValueChanged += (_) => { _userModifiedTransform = true; ApplyScaleChangeKeepingCenter(); };
            var btnResetXyz = new Button { Text = "1:1", ThemeTypeVariation = "ToolbarButton", CustomMinimumSize = new Vector2(36, 0) };
            btnResetXyz.Pressed += () =>
            {
                _userModifiedTransform = true;
                _spinScaleX.SetValueNoSignal(1.0); _spinScaleY.SetValueNoSignal(1.0); _spinScaleZ.SetValueNoSignal(1.0);
                ApplyScaleChangeKeepingCenter(immediate: true);
            };
            scaleXyzHBox.AddChild(_spinScaleX); scaleXyzHBox.AddChild(_spinScaleY); scaleXyzHBox.AddChild(_spinScaleZ); scaleXyzHBox.AddChild(btnResetXyz);
            advScaleBody.AddChild(scaleXyzHBox);

            var flipGeomRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            flipGeomRow.AddThemeConstantOverride("separation", 6);
            _btnFlipX = new Button { Text = "Flip X (L↔R)", ThemeTypeVariation = "ToolbarButton", ToggleMode = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 30) };
            SetButtonIcon(_btnFlipX, "res://assets/at-icons/mirror_horizontally.svg", 16);
            _btnFlipX.Toggled += (_) => OnGeometryFlipButtonToggled();
            _btnFlipY = new Button { Text = "Flip Y", ThemeTypeVariation = "ToolbarButton", ToggleMode = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 30) };
            SetButtonIcon(_btnFlipY, "res://assets/at-icons/mirror.svg", 16);
            _btnFlipY.Toggled += (_) => OnGeometryFlipButtonToggled();
            _btnFlipZ = new Button { Text = "Flip Z", ThemeTypeVariation = "ToolbarButton", ToggleMode = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 30) };
            SetButtonIcon(_btnFlipZ, "res://assets/at-icons/mirror.svg", 16);
            _btnFlipZ.Toggled += (_) => OnGeometryFlipButtonToggled();
            flipGeomRow.AddChild(_btnFlipX); flipGeomRow.AddChild(_btnFlipY); flipGeomRow.AddChild(_btnFlipZ);
            advScaleBody.AddChild(flipGeomRow);

            // Advanced Card: Legacy KNN Reference Pose Calibration
            var (poseCard, poseBody) = CreateInspectorCard("Reference Pose Calibration (KNN Fallback)");
            poseCard.Visible = false;
            _boxPoseCalibration = poseCard;
            _boxTab1Advanced.AddChild(poseCard);

            _optPosePreset = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            ConfigureOptionButton(_optPosePreset);
            _optPosePreset.AddItem("Match Hero Bind Pose (A-Pose)", (int)CustomModelAutoRigger.PosePreset.MatchHeroBindPose);
            _optPosePreset.AddItem("T-Pose (Horizontal Arms)", (int)CustomModelAutoRigger.PosePreset.TPose);
            _optPosePreset.AddItem("A-Pose (Custom Limb Sliders)", (int)CustomModelAutoRigger.PosePreset.APoseCustomAngle);
            _optPosePreset.Select(0);
            _optPosePreset.ItemSelected += OnPosePresetChanged;
            poseBody.AddChild(_optPosePreset);

            var calBtnHBox = new HBoxContainer();
            calBtnHBox.AddThemeConstantOverride("separation", 6);
            _btnAutoCalibratePose = new Button { Text = "Auto-Fit Reference Pose", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            SetButtonIcon(_btnAutoCalibratePose, "res://assets/at-icons/sparkles.svg", 20, StatusTone.Success);
            _btnAutoCalibratePose.Pressed += () => AutoCalibratePoseFromMesh(triggerRigUpdate: true);
            calBtnHBox.AddChild(_btnAutoCalibratePose);
            _btnResetPoseCal = new Button { Text = "Reset Pose" };
            _btnResetPoseCal.Pressed += ResetPoseCalibration;
            calBtnHBox.AddChild(_btnResetPoseCal);
            poseBody.AddChild(calBtnHBox);

            _lblPoseCalHint = new Label { Text = "Adjust reference sliders so the cyan ghost matches your unrigged mesh.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            ImporterStyle.ApplyHintStyle(_lblPoseCalHint);
            _secondaryLabels.Add(_lblPoseCalHint);
            poseBody.AddChild(_lblPoseCalHint);

            _boxCustomArmAngle = new VBoxContainer { Visible = false };
            _boxCustomArmAngle.AddThemeConstantOverride("separation", 4);

            (HSlider slider, Label valLabel) AddCalSlider(string labelText, double min, double max, double step, string suffix, string tip)
            {
                var row = new HBoxContainer();
                row.AddChild(new Label { Text = labelText, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = tip });
                var valLbl = new Label { Text = $"0{suffix}" };
                row.AddChild(valLbl);
                _boxCustomArmAngle.AddChild(row);
                var sld = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = 0.0, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = tip };
                sld.ValueChanged += (v) =>
                {
                    valLbl.Text = $"{v:0}{suffix}";
                    if (!_isUpdatingPoseSliders) OnCalibrationSliderChanged();
                };
                _boxCustomArmAngle.AddChild(sld);
                return (sld, valLbl);
            }

            (_sliderArmForward, _lblArmForwardVal) = AddCalSlider("Arm Forward / Back:", -45.0, 45.0, 1.0, "°", "Swings upper arms forward/back.");
            (_sliderElbowStraight, _lblElbowStraightVal) = AddCalSlider("Elbow Straighten:", 0.0, 100.0, 5.0, "%", "Unbends hero elbows toward T/A-pose.");
            (_sliderLegSpread, _lblLegSpreadVal) = AddCalSlider("Leg Stance Spread:", -25.0, 35.0, 1.0, "°", "Narrows or widens thigh stance.");

            var armHBox = new HBoxContainer();
            armHBox.AddChild(new Label { Text = "Arm Drop / Raise:", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
            _lblArmAngleVal = new Label { Text = "0°" };
            armHBox.AddChild(_lblArmAngleVal);
            _boxCustomArmAngle.AddChild(armHBox);
            _sliderArmAngle = new HSlider { MinValue = -60.0, MaxValue = 75.0, Step = 1.0, Value = 0.0 };
            _sliderArmAngle.ValueChanged += (v) =>
            {
                _lblArmAngleVal.Text = $"{v:0}°";
                if (!_isUpdatingPoseSliders) OnCalibrationSliderChanged();
            };
            _boxCustomArmAngle.AddChild(_sliderArmAngle);
            poseBody.AddChild(_boxCustomArmAngle);

            return scroll;
        }

        private void OnWorkflowModeChanged(long idx)
        {
            if (_optWorkflowMode == null) return;
            int mode = _optWorkflowMode.GetItemId((int)idx);
            _workflowIntent = mode == 0 ? ImportWorkflowIntent.FullCharacter : ImportWorkflowIntent.AccessoryAddOrReplace;
            if (_boxAccessorySetupCard != null)
                _boxAccessorySetupCard.Visible = mode != 0;

            if (mode == 2) // Weapon / Handheld Prop
            {
                if (_optAccBoneMode != null) _optAccBoneMode.Select(0);
                SelectRigidBoneByName("weapon_hand_R");
                SyncAccBoneSelectionFromRigidBone();
            }
            UpdateToolbarForWorkflowIntent();
            TriggerRealtimeRigUpdate();
        }
    }
}

