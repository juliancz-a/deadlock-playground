using System;
using System.Collections.Generic;
using Godot;
using DeadlockPlayground.Importer;
using DeadlockPlayground.Theming;

namespace DeadlockPlayground.UI
{
    public partial class CustomModelImporterModalUI
    {
        // =========================================================================
        // Floating 3D Weapon / Socket Attachment Overlay HUD
        // =========================================================================

        private Control _weaponSocketViewportHud;
        private string _activeWeaponSocketMesh;
        private bool _isSyncingWeaponHud;
        private int _savedGizmoBeforeWeaponHud;

        private Label _lblWpHudName;
        private OptionButton _optWpHudBone;
        private OptionButton _optWpHudReplaces;
        private OptionButton _optWpHudFit;
        private SpinBox _spinWpHudPitch;
        private SpinBox _spinWpHudYaw;
        private SpinBox _spinWpHudRoll;
        private SpinBox _spinWpHudPosX;
        private SpinBox _spinWpHudPosY;
        private SpinBox _spinWpHudPosZ;
        private SpinBox _spinWpHudScale;

        private readonly Dictionary<string, Label> _weaponCardSummaryLabels = new(StringComparer.OrdinalIgnoreCase);

        private bool IsWeaponSocketHudActive() =>
            _weaponSocketViewportHud != null && GodotObject.IsInstanceValid(_weaponSocketViewportHud) && _weaponSocketViewportHud.Visible;

        private void BuildWeaponSocketViewportHud(Control viewportPanel)
        {
            if (viewportPanel == null) return;

            var margin = new MarginContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            margin.AddThemeConstantOverride("margin_left", 12);
            margin.AddThemeConstantOverride("margin_right", 12);
            margin.AddThemeConstantOverride("margin_top", 10);
            viewportPanel.AddChild(margin);

            var panel = new PanelContainer
            {
                Visible = false,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
                SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
                MouseFilter = Control.MouseFilterEnum.Stop
            };
            panel.AddThemeStyleboxOverride("panel", GetHudFloatingPanelStyle());
            margin.AddChild(panel);
            _weaponSocketViewportHud = panel;

            var pad = new MarginContainer();
            pad.AddThemeConstantOverride("margin_left", 10);
            pad.AddThemeConstantOverride("margin_right", 10);
            pad.AddThemeConstantOverride("margin_top", 6);
            pad.AddThemeConstantOverride("margin_bottom", 6);
            panel.AddChild(pad);

            var root = new VBoxContainer();
            root.AddThemeConstantOverride("separation", 5);
            pad.AddChild(root);

            // Row 1: Target Name | Bone | Replaces | Fit | Snap Grip | Straighten | Done
            var row1 = new HBoxContainer();
            row1.AddThemeConstantOverride("separation", 6);
            root.AddChild(row1);

            _lblWpHudName = new Label
            {
                Text = "Weapon",
                CustomMinimumSize = new Vector2(80, 0),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                AutowrapMode = TextServer.AutowrapMode.Off
            };
            ImporterStyle.ApplyFontSize(_lblWpHudName, ImporterStyle.FontHint);
            _lblWpHudName.AddThemeColorOverride("font_color", PlaygroundThemeHelper.Parchment);
            row1.AddChild(_lblWpHudName);

            row1.AddChild(CreateViewportHudSeparator());

            var lblB = new Label { Text = "Bone:", SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            ImporterStyle.ApplyHintStyle(lblB, wrap: false);
            row1.AddChild(lblB);
            _optWpHudBone = new OptionButton { CustomMinimumSize = new Vector2(108, 24), FocusMode = Control.FocusModeEnum.None };
            ConfigureOptionButton(_optWpHudBone, 108);
            _optWpHudBone.ItemSelected += (idx) =>
            {
                if (_isSyncingWeaponHud || string.IsNullOrEmpty(_activeWeaponSocketMesh)) return;
                if (_submeshCardControls.TryGetValue(_activeWeaponSocketMesh, out var ctrl))
                {
                    ctrl.Socket.Select((int)idx);
                    ctrl.Socket.EmitSignal(OptionButton.SignalName.ItemSelected, idx);
                    UpdateWeaponCardSummary(_activeWeaponSocketMesh);
                }
            };
            row1.AddChild(_optWpHudBone);

            var lblR = new Label { Text = "Replaces:", SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            ImporterStyle.ApplyHintStyle(lblR, wrap: false);
            row1.AddChild(lblR);
            _optWpHudReplaces = new OptionButton { CustomMinimumSize = new Vector2(106, 24), FocusMode = Control.FocusModeEnum.None };
            ConfigureOptionButton(_optWpHudReplaces, 106);
            _optWpHudReplaces.ItemSelected += (idx) =>
            {
                if (_isSyncingWeaponHud || string.IsNullOrEmpty(_activeWeaponSocketMesh)) return;
                if (_submeshCardControls.TryGetValue(_activeWeaponSocketMesh, out var ctrl))
                {
                    ctrl.Replaces.Select((int)idx);
                    ctrl.Replaces.EmitSignal(OptionButton.SignalName.ItemSelected, idx);
                    UpdateWeaponCardSummary(_activeWeaponSocketMesh);
                }
            };
            row1.AddChild(_optWpHudReplaces);

            _optWpHudFit = new OptionButton { CustomMinimumSize = new Vector2(118, 24), FocusMode = Control.FocusModeEnum.None };
            ConfigureOptionButton(_optWpHudFit, 118);
            _optWpHudFit.AddItem("Keep Authored", 0);
            _optWpHudFit.AddItem("Snap to Bone", 1);
            _optWpHudFit.ItemSelected += (idx) =>
            {
                if (_isSyncingWeaponHud || string.IsNullOrEmpty(_activeWeaponSocketMesh)) return;
                if (_submeshCardControls.TryGetValue(_activeWeaponSocketMesh, out var ctrl))
                {
                    ctrl.Placement.Select((int)idx);
                    ctrl.Placement.EmitSignal(OptionButton.SignalName.ItemSelected, idx);
                }
            };
            row1.AddChild(_optWpHudFit);

            row1.AddChild(CreateViewportHudSeparator());

            _btnSnapGripPalm = CreateViewportHudButton("Snap Grip", 76, 24, tip: "Align weapon grip into the hero's hand palm with forward rotation");
            SetButtonIcon(_btnSnapGripPalm, "res://assets/at-icons/sparkles.svg", 14);
            _btnSnapGripPalm.Pressed += () =>
            {
                OnSnapGripToPalmPressed();
                SyncWeaponSocketHudFromCard();
            };
            row1.AddChild(_btnSnapGripPalm);

            _btnStraightenWeapon = CreateViewportHudButton("Straighten", 78, 24, tip: "Auto-detect weapon barrel axis and straighten forward (+Z)");
            SetButtonIcon(_btnStraightenWeapon, "res://assets/at-icons/sparkles.svg", 14);
            _btnStraightenWeapon.Pressed += () =>
            {
                OnStraightenWeaponPressed();
                SyncWeaponSocketHudFromCard();
            };
            row1.AddChild(_btnStraightenWeapon);

            var btnDone = CreateViewportHudButton("Done ✓", 58, 24, variation: "ImporterPrimaryButton", tip: "Close Weapon Fit HUD (Esc)");
            btnDone.Pressed += ExitWeaponSocketHud;
            row1.AddChild(btnDone);

            // Row 2: Rot (P/Y/R) | Pos (X/Y/Z) | Scale | Quick Presets
            var row2 = new HBoxContainer();
            row2.AddThemeConstantOverride("separation", 5);
            root.AddChild(row2);

            SpinBox MakeHudSpin(double min, double max, double step, string prefix, int minW)
            {
                var sb = new SpinBox
                {
                    MinValue = min, MaxValue = max, Step = step,
                    Prefix = prefix,
                    CustomMinimumSize = new Vector2(minW, 24),
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
                };
                ImporterStyle.ApplyFontSize(sb, ImporterStyle.FontHint);
                sb.ValueChanged += (_) => PushWeaponSocketHudToCard();
                return sb;
            }

            _spinWpHudPitch = MakeHudSpin(-180, 180, 5, "P:", 64);
            _spinWpHudYaw = MakeHudSpin(-180, 180, 5, "Y:", 64);
            _spinWpHudRoll = MakeHudSpin(-180, 180, 5, "R:", 64);
            row2.AddChild(_spinWpHudPitch);
            row2.AddChild(_spinWpHudYaw);
            row2.AddChild(_spinWpHudRoll);

            row2.AddChild(CreateViewportHudSeparator());

            _spinWpHudPosX = MakeHudSpin(-1, 1, 0.01, "X:", 64);
            _spinWpHudPosY = MakeHudSpin(-1, 1, 0.01, "Y:", 64);
            _spinWpHudPosZ = MakeHudSpin(-1, 1, 0.01, "Z:", 64);
            row2.AddChild(_spinWpHudPosX);
            row2.AddChild(_spinWpHudPosY);
            row2.AddChild(_spinWpHudPosZ);

            row2.AddChild(CreateViewportHudSeparator());

            _spinWpHudScale = MakeHudSpin(0.1, 5.0, 0.02, "S:", 62);
            _spinWpHudScale.SetValueNoSignal(1.0);
            row2.AddChild(_spinWpHudScale);

            var btnGripFwd = CreateViewportHudButton("Grip Fwd", 62, 24, tip: "Set forward hand grip (Pitch 90°, Z +0.04)");
            btnGripFwd.Pressed += () =>
            {
                _spinWpHudPitch.SetValueNoSignal(90);
                _spinWpHudYaw.SetValueNoSignal(0);
                _spinWpHudRoll.SetValueNoSignal(0);
                _spinWpHudPosZ.SetValueNoSignal(0.04);
                PushWeaponSocketHudToCard();
            };
            row2.AddChild(btnGripFwd);

            var btnY90 = CreateViewportHudButton("+90° Y", 48, 24, tip: "Rotate +90° Yaw");
            btnY90.Pressed += () =>
            {
                double next = (_spinWpHudYaw.Value + 90.0) % 360.0;
                if (next > 180.0) next -= 360.0;
                _spinWpHudYaw.SetValueNoSignal(next);
                PushWeaponSocketHudToCard();
            };
            row2.AddChild(btnY90);

            var btnP90 = CreateViewportHudButton("+90° P", 48, 24, tip: "Rotate +90° Pitch");
            btnP90.Pressed += () =>
            {
                double next = (_spinWpHudPitch.Value + 90.0) % 360.0;
                if (next > 180.0) next -= 360.0;
                _spinWpHudPitch.SetValueNoSignal(next);
                PushWeaponSocketHudToCard();
            };
            row2.AddChild(btnP90);

            var btnReset = CreateViewportHudButton("Reset", 44, 24, tip: "Reset weapon rotation, offset, and scale");
            btnReset.Pressed += () =>
            {
                _spinWpHudPitch.SetValueNoSignal(0);
                _spinWpHudYaw.SetValueNoSignal(0);
                _spinWpHudRoll.SetValueNoSignal(0);
                _spinWpHudPosX.SetValueNoSignal(0);
                _spinWpHudPosY.SetValueNoSignal(0);
                _spinWpHudPosZ.SetValueNoSignal(0);
                _spinWpHudScale.SetValueNoSignal(1.0);
                PushWeaponSocketHudToCard();
            };
            row2.AddChild(btnReset);
        }

        private void OpenWeaponSocketHud(string meshName)
        {
            if (string.IsNullOrEmpty(meshName) || !_submeshCardControls.ContainsKey(meshName)) return;

            if (_isMeshCutModeActive) ExitMeshCutMode();
            if (IsImportedSculptActive()) ExitImportedSculptMode();
            if (_isGizmoEditingSyntheticChain) SetSyntheticChainGizmoActive(false);
            if (_btnWeightPaintMode != null && _btnWeightPaintMode.ButtonPressed) _btnWeightPaintMode.ButtonPressed = false;
            if (_isEditReferenceBonesMode)
            {
                _isEditReferenceBonesMode = false;
                ApplyLandmarkPreviewState();
            }

            _activeWeaponSocketMesh = meshName;
            _savedGizmoBeforeWeaponHud = _optGizmoTarget?.Selected >= 0 ? _optGizmoTarget.GetItemId(_optGizmoTarget.Selected) : 0;

            SyncWeaponSocketHudFromCard();
            if (_weaponSocketViewportHud != null) _weaponSocketViewportHud.Visible = true;

            // Auto-activate 3D Weapon Gizmo (Left or Right depending on assigned bone)
            string bone = _submeshSocketAssignments.TryGetValue(meshName, out var b) ? b : "weapon_hand_R";
            bool isLeft = bone.EndsWith("_L", StringComparison.OrdinalIgnoreCase) || bone.Contains("left", StringComparison.OrdinalIgnoreCase);
            SelectGizmoTargetById(isLeft ? 2 : 1);
            SetBottomStatus($"Weapon Fit HUD: '{meshName}' — drag the 3D gizmo or use Snap Grip / Straighten. Press Esc or Done to close.", StatusTone.Info);
        }

        private void ExitWeaponSocketHud()
        {
            if (!IsWeaponSocketHudActive()) return;
            if (_weaponSocketViewportHud != null) _weaponSocketViewportHud.Visible = false;
            _activeWeaponSocketMesh = null;
            if (_savedGizmoBeforeWeaponHud >= 0)
                SelectGizmoTargetById(_savedGizmoBeforeWeaponHud);
            SetBottomStatus("Closed Weapon Fit HUD.", StatusTone.Info);
        }

        private void SyncWeaponSocketHudFromCard()
        {
            if (string.IsNullOrEmpty(_activeWeaponSocketMesh) ||
                !_submeshCardControls.TryGetValue(_activeWeaponSocketMesh, out var ctrl))
                return;

            _isSyncingWeaponHud = true;
            try
            {
                if (_lblWpHudName != null) _lblWpHudName.Text = _activeWeaponSocketMesh;

                if (_optWpHudBone != null && ctrl.Socket != null)
                {
                    _optWpHudBone.Clear();
                    for (int i = 0; i < ctrl.Socket.ItemCount; i++)
                        _optWpHudBone.AddItem(ctrl.Socket.GetItemText(i), ctrl.Socket.GetItemId(i));
                    if (ctrl.Socket.Selected >= 0) _optWpHudBone.Select(ctrl.Socket.Selected);
                }
                if (_optWpHudReplaces != null && ctrl.Replaces != null)
                {
                    _optWpHudReplaces.Clear();
                    for (int i = 0; i < ctrl.Replaces.ItemCount; i++)
                        _optWpHudReplaces.AddItem(ctrl.Replaces.GetItemText(i), ctrl.Replaces.GetItemId(i));
                    if (ctrl.Replaces.Selected >= 0) _optWpHudReplaces.Select(ctrl.Replaces.Selected);
                }
                if (_optWpHudFit != null && ctrl.Placement != null && ctrl.Placement.Selected >= 0)
                {
                    _optWpHudFit.Select(ctrl.Placement.Selected);
                }

                _spinWpHudPitch?.SetValueNoSignal(ctrl.Pitch.Value);
                _spinWpHudYaw?.SetValueNoSignal(ctrl.Yaw.Value);
                _spinWpHudRoll?.SetValueNoSignal(ctrl.Roll.Value);
                _spinWpHudPosX?.SetValueNoSignal(ctrl.PosX.Value);
                _spinWpHudPosY?.SetValueNoSignal(ctrl.PosY.Value);
                _spinWpHudPosZ?.SetValueNoSignal(ctrl.PosZ.Value);
                _spinWpHudScale?.SetValueNoSignal(ctrl.Scale.Value);
            }
            finally
            {
                _isSyncingWeaponHud = false;
            }
        }

        private void PushWeaponSocketHudToCard()
        {
            if (_isSyncingWeaponHud || string.IsNullOrEmpty(_activeWeaponSocketMesh) ||
                !_submeshCardControls.TryGetValue(_activeWeaponSocketMesh, out var ctrl))
                return;

            ctrl.Pitch.SetValueNoSignal(_spinWpHudPitch.Value);
            ctrl.Yaw.SetValueNoSignal(_spinWpHudYaw.Value);
            ctrl.Roll.SetValueNoSignal(_spinWpHudRoll.Value);
            ctrl.PosX.SetValueNoSignal(_spinWpHudPosX.Value);
            ctrl.PosY.SetValueNoSignal(_spinWpHudPosY.Value);
            ctrl.PosZ.SetValueNoSignal(_spinWpHudPosZ.Value);
            ctrl.Scale.Value = _spinWpHudScale.Value; // triggers UpdateWeaponOffsets() once cleanly!
        }

        private void UpdateWeaponCardSummary(string meshName)
        {
            if (!_weaponCardSummaryLabels.TryGetValue(meshName, out var lbl) || !GodotObject.IsInstanceValid(lbl))
                return;
            string bone = _submeshSocketAssignments.TryGetValue(meshName, out var b) ? b : "hand_R";
            string rep = _submeshHeroReplacements.TryGetValue(meshName, out var r) && !string.IsNullOrEmpty(r) ? r : "";
            lbl.Text = string.IsNullOrEmpty(rep) ? $"Socket: {bone}" : $"Socket: {bone} · replaces {rep}";
        }
    }
}
