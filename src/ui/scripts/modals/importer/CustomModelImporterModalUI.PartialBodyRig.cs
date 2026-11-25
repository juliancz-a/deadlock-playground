using System;
using System.Collections.Generic;
using Godot;
using DeadlockPlayground.Importer;
using DeadlockPlayground.Theming;

namespace DeadlockPlayground.UI
{
    public partial class CustomModelImporterModalUI
    {
        // Clothing / Partial-Body Auto-Rig UI Controls (Option A: Semi-Body Pins + Option B: Quick Hero Transfer)
        private Control _boxPartialBodyPanel;
        private OptionButton _optPartialBodyRegion;
        private VBoxContainer _boxPartialCustomRegions;
        private CheckBox _chkPartialSpine;
        private CheckBox _chkPartialArms;
        private CheckBox _chkPartialHands;
        private CheckBox _chkPartialLegs;
        private CheckBox _chkPartialFeet;
        private CheckBox _chkPartialNeck;
        private CheckBox _chkPartialHead;

        private Button _btnPartialEnginePins;
        private Button _btnPartialEngineTransfer;
        private VBoxContainer _boxPartialPinsSection;
        private VBoxContainer _boxPartialTransferSection;

        private Label _lblPartialRegionSummary;
        private Button _btnPartialEditPins;
        private Button _btnPartialAutoPlacePins;
        private Button _btnPartialBindPins;
        private CheckBox _chkPartialConformArms;
        private CheckBox _chkPartialConformLegs;
        private Button _btnPartialPoseChipMid;
        private Button _btnPartialPoseChipRight;

        // Compact 3-state Original Hero Mesh visibility during Pin Edit: 0 = Show, 1 = Ghost, 2 = Hide
        private int _pinEditHeroVisMode = 1;
        private readonly Button[] _hudHeroVisBtns = new Button[3];

        private HSlider _sliderPartialArmAngle;
        private Label _lblPartialArmAngleVal;
        private HSlider _sliderPartialArmForward;
        private Label _lblPartialArmForwardVal;
        private HSlider _sliderPartialElbowStraight;
        private Label _lblPartialElbowStraightVal;
        private HSlider _sliderPartialLegSpread;
        private Label _lblPartialLegSpreadVal;
        private SpinBox _spinPartialRadius;
        private SpinBox _spinPartialSmooth;
        private Button _btnPartialBindTransfer;

        private CustomModelAutoRigger.PartialBodyEngine _partialBodyEngine = CustomModelAutoRigger.PartialBodyEngine.SemiBodyPins;
        private bool _isSyncingPartialUi;

        private bool IsPartialBodyMethod() =>
            GetCurrentRiggingMethod() == CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig;

        private bool IsPartialSemiBodyPinsActive() =>
            IsPartialBodyMethod() && _partialBodyEngine == CustomModelAutoRigger.PartialBodyEngine.SemiBodyPins;

        private bool IsPartialHeroTransferActive() =>
            IsPartialBodyMethod() && _partialBodyEngine == CustomModelAutoRigger.PartialBodyEngine.HeroBodyTransfer;

        private CustomModelAutoRigger.PartialBodyRegion GetSelectedPartialBodyRegion()
        {
            if (_optPartialBodyRegion != null && _optPartialBodyRegion.Selected >= 0)
                return (CustomModelAutoRigger.PartialBodyRegion)_optPartialBodyRegion.GetItemId(_optPartialBodyRegion.Selected);
            return CustomModelAutoRigger.PartialBodyRegion.UpperBodySweater;
        }

        private CustomModelAutoRigger.RiggingOptions BuildPartialRegionFilterOptions() => new()
        {
            Method = CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig,
            PartialRegion = GetSelectedPartialBodyRegion(),
            PartialEngine = _partialBodyEngine,
            PartialIncludeSpine = _chkPartialSpine?.ButtonPressed ?? true,
            PartialIncludeArms = _chkPartialArms?.ButtonPressed ?? true,
            PartialIncludeHands = _chkPartialHands?.ButtonPressed ?? false,
            PartialIncludeLegs = _chkPartialLegs?.ButtonPressed ?? false,
            PartialIncludeFeet = _chkPartialFeet?.ButtonPressed ?? false,
            PartialIncludeNeck = _chkPartialNeck?.ButtonPressed ?? false,
            PartialIncludeHead = _chkPartialHead?.ButtonPressed ?? false
        };

        private void SetPinEditHeroVisMode(int mode)
        {
            _pinEditHeroVisMode = Mathf.Clamp(mode, 0, 2);
            UpdatePreviewHeroKeepAndGhostMaterials();
            SyncReferenceRigViewportHud();
        }

        private void ApplyPartialPresetCheckboxes(CustomModelAutoRigger.PartialBodyRegion region)
        {
            if (region == CustomModelAutoRigger.PartialBodyRegion.Custom) return;
            bool prev = _isSyncingPartialUi;
            _isSyncingPartialUi = true;
            try
            {
                bool sp = false, ar = false, ha = false, lg = false, ft = false, nk = false, hd = false;
                switch (region)
                {
                    case CustomModelAutoRigger.PartialBodyRegion.UpperBodySweater:
                        sp = true; ar = true; break;
                    case CustomModelAutoRigger.PartialBodyRegion.TorsoVest:
                        sp = true; break;
                    case CustomModelAutoRigger.PartialBodyRegion.LowerBodyPants:
                        lg = true; break;
                    case CustomModelAutoRigger.PartialBodyRegion.CoatDress:
                        sp = true; lg = true; break;
                    case CustomModelAutoRigger.PartialBodyRegion.FullOutfit:
                        sp = true; ar = true; lg = true; break;
                    case CustomModelAutoRigger.PartialBodyRegion.GlovesSleeves:
                        ar = true; ha = true; break;
                    case CustomModelAutoRigger.PartialBodyRegion.BootsShoes:
                        lg = true; ft = true; break;
                    case CustomModelAutoRigger.PartialBodyRegion.HeadHood:
                        nk = true; hd = true; break;
                }
                _chkPartialSpine?.SetPressedNoSignal(sp);
                _chkPartialArms?.SetPressedNoSignal(ar);
                _chkPartialHands?.SetPressedNoSignal(ha);
                _chkPartialLegs?.SetPressedNoSignal(lg);
                _chkPartialFeet?.SetPressedNoSignal(ft);
                _chkPartialNeck?.SetPressedNoSignal(nk);
                _chkPartialHead?.SetPressedNoSignal(hd);

                // Auto-align Arms / Legs conform checkboxes cleanly for this preset
                _chkAutoAlignArms?.SetPressedNoSignal(ar);
                _chkAutoAlignLegs?.SetPressedNoSignal(lg);
            }
            finally
            {
                _isSyncingPartialUi = prev;
            }
        }

        private ReferenceRigDef.PartialBodyRigMask GetCurrentPartialBodyMask() =>
            CustomModelAutoRigger.GetPartialBodyRigMask(BuildPartialRegionFilterOptions());

        private HashSet<string> GetCurrentAllowedPartialPins() =>
            CustomModelAutoRigger.GetAllowedPartialRegionPins(BuildPartialRegionFilterOptions());

        private Control BuildPartialBodyRigPanel()
        {
            var (card, body) = CreateInspectorCard(
                "Clothing / Partial-Body Setup",
                "Scope auto-rigging strictly to the body region your garment covers so unrelated limbs never steal weights.");
            card.Visible = false;

            // 1. Body Region Preset Selector
            var regRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            regRow.AddThemeConstantOverride("separation", 8);
            var lblReg = new Label { Text = "Body Region:", CustomMinimumSize = new Vector2(92, 0) };
            ImporterStyle.ApplyBodyStyle(lblReg);
            lblReg.AddThemeColorOverride("font_color", PlaygroundThemeHelper.Parchment);
            regRow.AddChild(lblReg);

            _optPartialBodyRegion = new OptionButton
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, ImporterStyle.ButtonHeight),
                TooltipText = "Restricts reference joints, volumetric heat, and hero donor transfer strictly to this garment's body region."
            };
            ConfigureOptionButton(_optPartialBodyRegion);
            _optPartialBodyRegion.AddItem("Upper Body — Sweater / Jacket / Shirt", (int)CustomModelAutoRigger.PartialBodyRegion.UpperBodySweater);
            _optPartialBodyRegion.AddItem("Torso Only — Vest / Corset / Chest Armor", (int)CustomModelAutoRigger.PartialBodyRegion.TorsoVest);
            _optPartialBodyRegion.AddItem("Lower Body — Pants / Shorts / Skirt", (int)CustomModelAutoRigger.PartialBodyRegion.LowerBodyPants);
            _optPartialBodyRegion.AddItem("Coat / Dress / Skirt (Torso + Legs)", (int)CustomModelAutoRigger.PartialBodyRegion.CoatDress);
            _optPartialBodyRegion.AddItem("Full Outfit / Jumpsuit (Torso + Arms + Legs)", (int)CustomModelAutoRigger.PartialBodyRegion.FullOutfit);
            _optPartialBodyRegion.AddItem("Arms & Hands — Gloves / Sleeves", (int)CustomModelAutoRigger.PartialBodyRegion.GlovesSleeves);
            _optPartialBodyRegion.AddItem("Lower Legs & Feet — Boots / Shoes", (int)CustomModelAutoRigger.PartialBodyRegion.BootsShoes);
            _optPartialBodyRegion.AddItem("Head & Collar — Hood / Scarf / Mask", (int)CustomModelAutoRigger.PartialBodyRegion.HeadHood);
            _optPartialBodyRegion.AddItem("Custom Body Regions...", (int)CustomModelAutoRigger.PartialBodyRegion.Custom);
            _optPartialBodyRegion.Select(0);
            _optPartialBodyRegion.ItemSelected += (_) =>
            {
                ApplyPartialPresetCheckboxes(GetSelectedPartialBodyRegion());
                OnPartialRegionSettingsChanged(reEstimatePins: true);
            };
            regRow.AddChild(_optPartialBodyRegion);
            body.AddChild(regRow);

            // Always-visible compact Optional Parts toggle row (zero clutter, 1-click customization)
            _boxPartialCustomRegions = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = true };
            _boxPartialCustomRegions.AddThemeConstantOverride("separation", 2);
            var customFlow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            customFlow.AddThemeConstantOverride("h_separation", 8);
            customFlow.AddThemeConstantOverride("v_separation", 2);

            void SwitchToCustomIfCoreChanged()
            {
                if (_optPartialBodyRegion == null) return;
                var cur = GetSelectedPartialBodyRegion();
                if (cur == CustomModelAutoRigger.PartialBodyRegion.Custom) return;
                for (int i = 0; i < _optPartialBodyRegion.ItemCount; i++)
                {
                    if (_optPartialBodyRegion.GetItemId(i) == (int)CustomModelAutoRigger.PartialBodyRegion.Custom)
                    {
                        _optPartialBodyRegion.Select(i);
                        break;
                    }
                }
            }

            CheckBox MakeRegionChk(string text, bool defVal, bool isCorePart, string tip)
            {
                var c = new CheckBox { Text = text, ButtonPressed = defVal, TooltipText = tip };
                ImporterStyle.ApplyFontSize(c, ImporterStyle.FontHint);
                c.Toggled += (_) =>
                {
                    if (_isSyncingPartialUi) return;
                    if (isCorePart) SwitchToCustomIfCoreChanged();
                    OnPartialRegionSettingsChanged(reEstimatePins: true);
                };
                customFlow.AddChild(c);
                return c;
            }
            _chkPartialSpine = MakeRegionChk("Spine", true, true, "Include pelvis & spine bones (waist/torso)");
            _chkPartialArms = MakeRegionChk("Arms", true, true, "Include shoulders, upper/lower arms & wrist cuffs");
            _chkPartialHands = MakeRegionChk("Hands", false, false, "Include full hands & articulated fingers");
            _chkPartialLegs = MakeRegionChk("Legs", false, true, "Include hips, thighs, knees & shins");
            _chkPartialFeet = MakeRegionChk("Feet", false, false, "Include ankles, feet & toes (for boots/shoes)");
            _chkPartialNeck = MakeRegionChk("Neck", false, false, "Include neck collar bone without head (turtlenecks/scarves)");
            _chkPartialHead = MakeRegionChk("Head", false, false, "Include head & neck bones (hoods/masks/helmets)");
            _boxPartialCustomRegions.AddChild(customFlow);
            body.AddChild(_boxPartialCustomRegions);

            _lblPartialRegionSummary = new Label
            {
                Text = "Active bones: Pelvis, Spine, Clavicles, Upper/Lower Arms, Wrist Cuffs",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            ImporterStyle.ApplyHintStyle(_lblPartialRegionSummary);
            _secondaryLabels.Add(_lblPartialRegionSummary);
            body.AddChild(_lblPartialRegionSummary);

            body.AddChild(new HSeparator());

            // 2. Fitting Engine Switcher (Semi-Body Pins vs Quick Hero Transfer)
            var lblEng = new Label { Text = "Fitting Engine:" };
            ImporterStyle.ApplyBodyStyle(lblEng);
            lblEng.AddThemeColorOverride("font_color", PlaygroundThemeHelper.Parchment);
            body.AddChild(lblEng);

            var engRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            engRow.AddThemeConstantOverride("separation", 6);
            _btnPartialEnginePins = new Button
            {
                Text = "Semi-Body Pins (Best for Sleeves)",
                ToggleMode = true,
                ButtonPressed = true,
                ThemeTypeVariation = "ImporterPrimaryButton",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 34),
                TooltipText = "Recommended for straight-down sweaters, jackets, or pants: places only this region's 3D joints inside your garment, runs volumetric heat diffusion so sleeves stay separated from ribs, and bends sleeves into the hero's bind pose."
            };
            ImporterStyle.ApplyFontSize(_btnPartialEnginePins, ImporterStyle.FontHint);
            _btnPartialEnginePins.Pressed += () => SetPartialBodyEngine(CustomModelAutoRigger.PartialBodyEngine.SemiBodyPins, triggerUpdate: true);

            _btnPartialEngineTransfer = new Button
            {
                Text = "Quick Hero Transfer",
                ToggleMode = true,
                ButtonPressed = false,
                ThemeTypeVariation = "ToolbarButton",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 34),
                TooltipText = "Fast region-filtered weight transfer from the hero mesh. Use the Arm/Leg angle sliders below to pose the hero inside hanging sleeves before binding."
            };
            ImporterStyle.ApplyFontSize(_btnPartialEngineTransfer, ImporterStyle.FontHint);
            _btnPartialEngineTransfer.Pressed += () => SetPartialBodyEngine(CustomModelAutoRigger.PartialBodyEngine.HeroBodyTransfer, triggerUpdate: true);

            engRow.AddChild(_btnPartialEnginePins);
            engRow.AddChild(_btnPartialEngineTransfer);
            body.AddChild(engRow);

            // --- Section A: Semi-Body Reference Pins ---
            _boxPartialPinsSection = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = true };
            _boxPartialPinsSection.AddThemeConstantOverride("separation", 8);

            var lblPinsHint = new Label
            {
                Text = "Auto-Place snaps joints directly inside your garment's torso & sleeves (even straight-down sleeves). Drag any joint in 3D to refine, then click Bind.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            ImporterStyle.ApplyHintStyle(lblPinsHint);
            _secondaryLabels.Add(lblPinsHint);
            _boxPartialPinsSection.AddChild(lblPinsHint);

            var pinActionRow1 = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            pinActionRow1.AddThemeConstantOverride("separation", 6);
            _btnPartialAutoPlacePins = new Button
            {
                Text = "Auto-Place Region Pins",
                ThemeTypeVariation = "ToolbarButton",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 34),
                TooltipText = "Projects hero bones onto your garment and detects hanging sleeves/pant legs automatically."
            };
            SetButtonIcon(_btnPartialAutoPlacePins, "res://assets/at-icons/sparkles.svg", 16);
            ImporterStyle.ApplyFontSize(_btnPartialAutoPlacePins, ImporterStyle.FontHint);
            _btnPartialAutoPlacePins.Pressed += () =>
            {
                PushReferenceRigUndoSnapshot();
                _isGuidedPinPlacement = false;
                _guidedPlacedPinKeys.Clear();
                EnsurePartialBodyPinsInitialized(force: true);
                _isEditReferenceBonesMode = true;
                _userMovedLandmarkPins = true;
                _pinsDirtyNeedsRigRebuild = true;
                SyncPartialBodyUiState();
                ApplyLandmarkPreviewState();
                SetBottomStatus("Auto-placed partial-body region pins onto garment. Refine in 3D or click Bind.", StatusTone.Success);
            };
            pinActionRow1.AddChild(_btnPartialAutoPlacePins);

            _btnPartialEditPins = new Button
            {
                Text = "Edit Pins in 3D",
                ToggleMode = true,
                ButtonPressed = _isEditReferenceBonesMode,
                ThemeTypeVariation = "ToolbarButton",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 34),
                TooltipText = "Show/hide this region's draggable 3D joints in the viewport."
            };
            SetButtonIcon(_btnPartialEditPins, "res://assets/at-icons/bone.svg", 16);
            ImporterStyle.ApplyFontSize(_btnPartialEditPins, ImporterStyle.FontHint);
            _btnPartialEditPins.Toggled += (on) =>
            {
                if (_isSyncingPartialUi) return;
                _isEditReferenceBonesMode = on;
                if (on)
                {
                    if (_btnWeightPaintMode != null && _btnWeightPaintMode.ButtonPressed)
                        _btnWeightPaintMode.ButtonPressed = false;
                    EnsurePartialBodyPinsInitialized(force: false);
                }
                else if (_isGizmoEditingRefJoint)
                {
                    _isGizmoEditingRefJoint = false;
                    UpdateGizmoTargetTransform();
                }
                SyncPartialBodyUiState();
                ApplyLandmarkPreviewState();
            };
            pinActionRow1.AddChild(_btnPartialEditPins);
            _boxPartialPinsSection.AddChild(pinActionRow1);

            var conformRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            conformRow.AddThemeConstantOverride("separation", 10);
            _chkPartialConformArms = new CheckBox
            {
                Text = "Conform Arms",
                ButtonPressed = true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Automatically bends straight-down sleeves into the Deadlock hero's exact A-Pose when binding."
            };
            ImporterStyle.ApplyFontSize(_chkPartialConformArms, ImporterStyle.FontHint);
            _chkPartialConformArms.Toggled += (on) =>
            {
                if (_isSyncingPartialUi) return;
                _chkAutoAlignArms?.SetPressedNoSignal(on);
                _chkConformToSkeleton?.SetPressedNoSignal(on || (_chkPartialConformLegs?.ButtonPressed ?? false));
                _pinsDirtyNeedsRigRebuild = true;
            };
            _chkPartialConformLegs = new CheckBox
            {
                Text = "Conform Legs",
                ButtonPressed = false,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Automatically conforms wide-stance pant legs or boots into the Deadlock hero's exact leg stance when binding."
            };
            ImporterStyle.ApplyFontSize(_chkPartialConformLegs, ImporterStyle.FontHint);
            _chkPartialConformLegs.Toggled += (on) =>
            {
                if (_isSyncingPartialUi) return;
                _chkAutoAlignLegs?.SetPressedNoSignal(on);
                _chkConformToSkeleton?.SetPressedNoSignal(on || (_chkPartialConformArms?.ButtonPressed ?? false));
                _pinsDirtyNeedsRigRebuild = true;
            };
            conformRow.AddChild(_chkPartialConformArms);
            conformRow.AddChild(_chkPartialConformLegs);
            _boxPartialPinsSection.AddChild(conformRow);

            _btnPartialBindPins = new Button
            {
                Text = "Bind Garment to Hero",
                ThemeTypeVariation = "ImporterPrimaryButton",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, ImporterStyle.PrimaryButtonHeight),
                TooltipText = "Runs volumetric heat weight solving on only this region's bones and binds the garment to the hero skeleton."
            };
            SetButtonIcon(_btnPartialBindPins, "res://assets/at-icons/sparkles.svg", 16);
            ImporterStyle.ApplyFontSize(_btnPartialBindPins, ImporterStyle.FontBody);
            _btnPartialBindPins.Pressed += OnBindReferenceRigPressed;
            _boxPartialPinsSection.AddChild(_btnPartialBindPins);
            body.AddChild(_boxPartialPinsSection);

            // --- Section B: Quick Hero-Body Transfer + Pose Angle Match ---
            _boxPartialTransferSection = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            _boxPartialTransferSection.AddThemeConstantOverride("separation", 6);

            var lblTransferHint = new Label
            {
                Text = "Transfers weights only from this hero body region. Use the sliders below to align the hero's arms/legs inside your garment, then bind.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            ImporterStyle.ApplyHintStyle(lblTransferHint);
            _secondaryLabels.Add(lblTransferHint);
            _boxPartialTransferSection.AddChild(lblTransferHint);

            // Quick angle preset chips (adapt dynamically for Sleeves vs Legs!)
            var presetRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            presetRow.AddThemeConstantOverride("separation", 6);
            Button AddAngleChip(string label, Action onClick)
            {
                var btn = new Button
                {
                    Text = label,
                    ThemeTypeVariation = "ToolbarButton",
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    CustomMinimumSize = new Vector2(0, 28)
                };
                ImporterStyle.ApplyFontSize(btn, ImporterStyle.FontHint);
                btn.Pressed += onClick;
                presetRow.AddChild(btn);
                return btn;
            }
            AddAngleChip("Hero Bind (0°)", () => ApplyPartialTransferPosePreset(0f, 0f, 0f, 0f));
            _btnPartialPoseChipMid = AddAngleChip("Hanging (-40°)", () =>
            {
                var m = GetCurrentPartialBodyMask();
                if (m.IncludeLegs && !m.IncludeArms) ApplyPartialTransferPosePreset(0f, 0f, 0f, -8f);
                else ApplyPartialTransferPosePreset(-40f, 0f, 25f, 0f);
            });
            _btnPartialPoseChipRight = AddAngleChip("T-Pose (+40°)", () =>
            {
                var m = GetCurrentPartialBodyMask();
                if (m.IncludeLegs && !m.IncludeArms) ApplyPartialTransferPosePreset(0f, 0f, 0f, 15f);
                else ApplyPartialTransferPosePreset(40f, 0f, 60f, m.IncludeLegs ? 12f : 0f);
            });
            _boxPartialTransferSection.AddChild(presetRow);

            (HSlider sld, Label valLbl) AddPartialSlider(string label, double min, double max, double step, string suffix, Action<double> onChanged)
            {
                var r = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                var l = new Label { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                ImporterStyle.ApplyFontSize(l, ImporterStyle.FontHint);
                var v = new Label { Text = $"0{suffix}", CustomMinimumSize = new Vector2(46, 0), HorizontalAlignment = HorizontalAlignment.Right };
                ImporterStyle.ApplyFontSize(v, ImporterStyle.FontHint);
                r.AddChild(l);
                r.AddChild(v);
                _boxPartialTransferSection.AddChild(r);

                var s = new HSlider
                {
                    MinValue = min,
                    MaxValue = max,
                    Step = step,
                    Value = 0.0,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };
                s.ValueChanged += (val) =>
                {
                    v.Text = $"{val:0}{suffix}";
                    if (!_isSyncingPartialUi) onChanged(val);
                };
                _boxPartialTransferSection.AddChild(s);
                return (s, v);
            }

            (_sliderPartialArmAngle, _lblPartialArmAngleVal) = AddPartialSlider(
                "Arm Drop / Raise:", -65.0, 75.0, 1.0, "°",
                val => { _sliderArmAngle?.SetValueNoSignal(val); if (_lblArmAngleVal != null) _lblArmAngleVal.Text = $"{val:0}°"; OnCalibrationSliderChanged(); });
            (_sliderPartialArmForward, _lblPartialArmForwardVal) = AddPartialSlider(
                "Arm Forward / Back:", -45.0, 45.0, 1.0, "°",
                val => { _sliderArmForward?.SetValueNoSignal(val); if (_lblArmForwardVal != null) _lblArmForwardVal.Text = $"{val:0}°"; OnCalibrationSliderChanged(); });
            (_sliderPartialElbowStraight, _lblPartialElbowStraightVal) = AddPartialSlider(
                "Elbow Straighten:", 0.0, 100.0, 5.0, "%",
                val => { _sliderElbowStraight?.SetValueNoSignal(val); if (_lblElbowStraightVal != null) _lblElbowStraightVal.Text = $"{val:0}%"; OnCalibrationSliderChanged(); });
            (_sliderPartialLegSpread, _lblPartialLegSpreadVal) = AddPartialSlider(
                "Leg Stance Spread:", -25.0, 35.0, 1.0, "°",
                val => { _sliderLegSpread?.SetValueNoSignal(val); if (_lblLegSpreadVal != null) _lblLegSpreadVal.Text = $"{val:0}°"; OnCalibrationSliderChanged(); });

            var tuneRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            tuneRow.AddThemeConstantOverride("separation", 6);
            _spinPartialRadius = new SpinBox
            {
                MinValue = 0.04, MaxValue = 0.60, Step = 0.01, Value = 0.18,
                Prefix = "Radius:", Suffix = "m",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Search radius for hero surface donor vertices."
            };
            _spinPartialRadius.ValueChanged += (v) => { if (!_isSyncingPartialUi) { _spinKnnRadius?.SetValueNoSignal(v); ScheduleDebouncedLiveRigUpdate(); } };
            _spinPartialSmooth = new SpinBox
            {
                MinValue = 0, MaxValue = 6, Step = 1, Value = 2,
                Prefix = "Smooth:",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Laplacian weight smoothing passes across vertices."
            };
            _spinPartialSmooth.ValueChanged += (v) => { if (!_isSyncingPartialUi) { _spinLaplacian?.SetValueNoSignal(v); ScheduleDebouncedLiveRigUpdate(); } };
            tuneRow.AddChild(_spinPartialRadius);
            tuneRow.AddChild(_spinPartialSmooth);
            _boxPartialTransferSection.AddChild(tuneRow);

            _btnPartialBindTransfer = new Button
            {
                Text = "Bind / Update Hero Transfer",
                ThemeTypeVariation = "ImporterPrimaryButton",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, ImporterStyle.PrimaryButtonHeight),
                TooltipText = "Immediately transfers region-scoped weights from the posed hero and bakes back to hero bind pose."
            };
            SetButtonIcon(_btnPartialBindTransfer, "res://assets/at-icons/sparkles.svg", 16);
            ImporterStyle.ApplyFontSize(_btnPartialBindTransfer, ImporterStyle.FontBody);
            _btnPartialBindTransfer.Pressed += () => ExecuteLiveRigUpdate();
            _boxPartialTransferSection.AddChild(_btnPartialBindTransfer);
            body.AddChild(_boxPartialTransferSection);

            SyncPartialBodyUiState();
            return card;
        }

        private void ApplyPartialTransferPosePreset(float armDeg, float fwdDeg, float elbowPct, float legDeg)
        {
            _isSyncingPartialUi = true;
            try
            {
                _sliderPartialArmAngle?.SetValueNoSignal(armDeg);
                if (_lblPartialArmAngleVal != null) _lblPartialArmAngleVal.Text = $"{armDeg:0}°";
                _sliderArmAngle?.SetValueNoSignal(armDeg);
                if (_lblArmAngleVal != null) _lblArmAngleVal.Text = $"{armDeg:0}°";

                _sliderPartialArmForward?.SetValueNoSignal(fwdDeg);
                if (_lblPartialArmForwardVal != null) _lblPartialArmForwardVal.Text = $"{fwdDeg:0}°";
                _sliderArmForward?.SetValueNoSignal(fwdDeg);
                if (_lblArmForwardVal != null) _lblArmForwardVal.Text = $"{fwdDeg:0}°";

                _sliderPartialElbowStraight?.SetValueNoSignal(elbowPct);
                if (_lblPartialElbowStraightVal != null) _lblPartialElbowStraightVal.Text = $"{elbowPct:0}%";
                _sliderElbowStraight?.SetValueNoSignal(elbowPct);
                if (_lblElbowStraightVal != null) _lblElbowStraightVal.Text = $"{elbowPct:0}%";

                _sliderPartialLegSpread?.SetValueNoSignal(legDeg);
                if (_lblPartialLegSpreadVal != null) _lblPartialLegSpreadVal.Text = $"{legDeg:0}°";
                _sliderLegSpread?.SetValueNoSignal(legDeg);
                if (_lblLegSpreadVal != null) _lblLegSpreadVal.Text = $"{legDeg:0}°";
            }
            finally
            {
                _isSyncingPartialUi = false;
            }
            OnCalibrationSliderChanged();
        }

        private void SetPartialBodyEngine(CustomModelAutoRigger.PartialBodyEngine engine, bool triggerUpdate)
        {
            _partialBodyEngine = engine;
            if (engine == CustomModelAutoRigger.PartialBodyEngine.SemiBodyPins)
            {
                EnsurePartialBodyPinsInitialized(force: false);
            }
            else
            {
                _isEditReferenceBonesMode = false;
                if (_isGizmoEditingRefJoint)
                {
                    _isGizmoEditingRefJoint = false;
                    UpdateGizmoTargetTransform();
                }
            }
            SyncPartialBodyUiState();
            UpdateTab2SectionVisibility();
            ApplyLandmarkPreviewState();
            if (engine == CustomModelAutoRigger.PartialBodyEngine.HeroBodyTransfer)
            {
                ApplyCalibrationPoseToGhostSkeleton();
                if (triggerUpdate) TriggerRealtimeRigUpdate();
            }
            else if (triggerUpdate)
            {
                SetBottomStatus("Semi-Body Pins active — refine region joints in 3D or click Bind Garment to Hero.", StatusTone.Info);
            }
        }

        private void OnPartialRegionSettingsChanged(bool reEstimatePins)
        {
            SyncPartialBodyUiState();
            if (IsPartialSemiBodyPinsActive())
            {
                if (reEstimatePins)
                    EnsurePartialBodyPinsInitialized(force: !_userMovedLandmarkPins);
                _pinsDirtyNeedsRigRebuild = true;
                RefreshPinVisuals();
                SyncReferenceRigViewportHud();
            }
            else if (IsPartialHeroTransferActive())
            {
                TriggerRealtimeRigUpdate();
            }
        }

        private void EnsurePartialBodyPinsInitialized(bool force)
        {
            _referenceRig ??= new ReferenceRigDef();
            var mask = GetCurrentPartialBodyMask();
            if (!mask.IncludeHands)
                _referenceRig.FingerMode = ReferenceFingerRigMode.RigidMitten;

            if (!force && _referenceRig.Joints.Count >= CustomLandmarkWeightSolver.PinNames.Length)
            {
                float hExist = _previewHeroSkeleton != null
                    ? CustomModelAutoRigger.ComputeHeroHeight(_previewHeroSkeleton)
                    : 1.70f;
                _referenceRig.EnsureDefaultJoints(hExist);
                return;
            }
            if (_liveImportedScene == null || !GodotObject.IsInstanceValid(_liveImportedScene)) return;
            var meshes = FilterActiveImportedMeshes(CustomModelAutoRigger.CollectMeshes(_liveImportedScene));
            if (meshes.Count == 0) return;

            var opts = BuildPartialRegionFilterOptions();
            Transform3D alignedXf = BuildCurrentAlignedTransform();
            var est = (_previewHeroSkeleton != null && GodotObject.IsInstanceValid(_previewHeroSkeleton))
                ? CustomLandmarkWeightSolver.EstimatePartialRegionPins(_liveImportedScene, meshes, _previewHeroSkeleton, alignedXf, opts)
                : CustomLandmarkWeightSolver.EstimateLandmarks(_liveImportedScene, meshes);

            if (est.Count > 0)
            {
                _landmarkPins = est;
                if (force)
                {
                    _referenceRig.Joints = new Dictionary<string, Vector3>(est, StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    foreach (var kv in est)
                        if (!_referenceRig.Joints.ContainsKey(kv.Key))
                            _referenceRig.Joints[kv.Key] = kv.Value;
                }
                float heroH = _previewHeroSkeleton != null
                    ? CustomModelAutoRigger.ComputeHeroHeight(_previewHeroSkeleton)
                    : 1.70f;
                _referenceRig.EnsureDefaultJoints(heroH);
                if (mask.IncludeHands && _referenceRig.FingerMode == ReferenceFingerRigMode.ArticulatedFingers)
                {
                    _referenceRig.SnapFingersToMesh(_liveImportedScene, meshes, forceRegenerate: force);
                }
            }
        }

        private void SyncPartialBodyUiState()
        {
            bool prev = _isSyncingPartialUi;
            _isSyncingPartialUi = true;
            try
            {
                var mask = GetCurrentPartialBodyMask();
                if (_boxPartialCustomRegions != null)
                    _boxPartialCustomRegions.Visible = true;

                bool isPins = _partialBodyEngine == CustomModelAutoRigger.PartialBodyEngine.SemiBodyPins;
                if (_btnPartialEnginePins != null)
                {
                    _btnPartialEnginePins.SetPressedNoSignal(isPins);
                    _btnPartialEnginePins.ThemeTypeVariation = isPins ? "ImporterPrimaryButton" : "ToolbarButton";
                }
                if (_btnPartialEngineTransfer != null)
                {
                    _btnPartialEngineTransfer.SetPressedNoSignal(!isPins);
                    _btnPartialEngineTransfer.ThemeTypeVariation = !isPins ? "ImporterPrimaryButton" : "ToolbarButton";
                }
                if (_boxPartialPinsSection != null) _boxPartialPinsSection.Visible = isPins;
                if (_boxPartialTransferSection != null) _boxPartialTransferSection.Visible = !isPins;

                if (_btnPartialEditPins != null)
                {
                    _btnPartialEditPins.SetPressedNoSignal(_isEditReferenceBonesMode);
                    _btnPartialEditPins.ThemeTypeVariation = _isEditReferenceBonesMode ? "ImporterPrimaryButton" : "ToolbarButton";
                }
                if (_chkPartialConformArms != null && _chkAutoAlignArms != null)
                {
                    _chkPartialConformArms.Visible = mask.IncludeArms;
                    _chkPartialConformArms.SetPressedNoSignal(_chkAutoAlignArms.ButtonPressed);
                }
                if (_chkPartialConformLegs != null && _chkAutoAlignLegs != null)
                {
                    _chkPartialConformLegs.Visible = mask.IncludeLegs;
                    _chkPartialConformLegs.SetPressedNoSignal(_chkAutoAlignLegs.ButtonPressed);
                }
                if (_btnPartialPoseChipMid != null && _btnPartialPoseChipRight != null)
                {
                    bool legsOnly = mask.IncludeLegs && !mask.IncludeArms;
                    _btnPartialPoseChipMid.Text = legsOnly ? "Narrow (-8°)" : "Hanging (-40°)";
                    _btnPartialPoseChipRight.Text = legsOnly ? "Wide Stance (+15°)" : "T-Pose (+40°)";
                }
                if (_lblPartialRegionSummary != null)
                {
                    var parts = new List<string>();
                    if (mask.IncludePelvisAnchor) parts.Add("Pelvis");
                    if (mask.IncludeSpine) parts.Add("Spine");
                    else if (mask.IncludeCollarSpineAnchor) parts.Add("Upper Chest");
                    if (mask.IncludeClavicles) parts.Add("Clavicles");
                    if (mask.IncludeArms) parts.Add(mask.IncludeHands ? "Arms & Hands" : "Arms / Sleeves");
                    if (mask.IncludeLegs)
                    {
                        if (mask.LowerLegsOnly) parts.Add("Lower Legs & Feet");
                        else parts.Add(mask.IncludeFeet ? "Legs & Feet" : "Legs");
                    }
                    if (mask.IncludeHead) parts.Add("Neck & Head");
                    else if (mask.IncludeNeck) parts.Add("Neck");
                    _lblPartialRegionSummary.Text = $"Active region bones: {(parts.Count > 0 ? string.Join(", ", parts) : "Spine")} (all other hero bones excluded)";
                }

                // Keep transfer sliders synced with backing Tab 1 sliders
                if (_sliderPartialArmAngle != null && _sliderArmAngle != null)
                {
                    _sliderPartialArmAngle.SetValueNoSignal(_sliderArmAngle.Value);
                    if (_lblPartialArmAngleVal != null) _lblPartialArmAngleVal.Text = $"{_sliderArmAngle.Value:0}°";
                }
                if (_sliderPartialArmForward != null && _sliderArmForward != null)
                {
                    _sliderPartialArmForward.SetValueNoSignal(_sliderArmForward.Value);
                    if (_lblPartialArmForwardVal != null) _lblPartialArmForwardVal.Text = $"{_sliderArmForward.Value:0}°";
                }
                if (_sliderPartialElbowStraight != null && _sliderElbowStraight != null)
                {
                    _sliderPartialElbowStraight.SetValueNoSignal(_sliderElbowStraight.Value);
                    if (_lblPartialElbowStraightVal != null) _lblPartialElbowStraightVal.Text = $"{_sliderElbowStraight.Value:0}%";
                }
                if (_sliderPartialLegSpread != null && _sliderLegSpread != null)
                {
                    _sliderPartialLegSpread.SetValueNoSignal(_sliderLegSpread.Value);
                    if (_lblPartialLegSpreadVal != null) _lblPartialLegSpreadVal.Text = $"{_sliderLegSpread.Value:0}°";
                }
                if (_spinPartialRadius != null && _spinKnnRadius != null)
                    _spinPartialRadius.SetValueNoSignal(_spinKnnRadius.Value);
                if (_spinPartialSmooth != null && _spinLaplacian != null)
                    _spinPartialSmooth.SetValueNoSignal(_spinLaplacian.Value);
            }
            finally
            {
                _isSyncingPartialUi = prev;
            }
        }

        private void RestorePartialBodyRigOptions(CustomModelAutoRigger.RiggingOptions options)
        {
            if (options == null) return;
            _isSyncingPartialUi = true;
            try
            {
                _partialBodyEngine = options.PartialEngine;
                if (_optPartialBodyRegion != null)
                {
                    for (int i = 0; i < _optPartialBodyRegion.ItemCount; i++)
                    {
                        if (_optPartialBodyRegion.GetItemId(i) == (int)options.PartialRegion)
                        {
                            _optPartialBodyRegion.Select(i);
                            break;
                        }
                    }
                }
                _chkPartialSpine?.SetPressedNoSignal(options.PartialIncludeSpine);
                _chkPartialArms?.SetPressedNoSignal(options.PartialIncludeArms);
                _chkPartialHands?.SetPressedNoSignal(options.PartialIncludeHands);
                _chkPartialLegs?.SetPressedNoSignal(options.PartialIncludeLegs);
                _chkPartialFeet?.SetPressedNoSignal(options.PartialIncludeFeet);
                _chkPartialNeck?.SetPressedNoSignal(options.PartialIncludeNeck);
                _chkPartialHead?.SetPressedNoSignal(options.PartialIncludeHead);
            }
            finally
            {
                _isSyncingPartialUi = false;
            }
            SyncPartialBodyUiState();
        }
    }
}
