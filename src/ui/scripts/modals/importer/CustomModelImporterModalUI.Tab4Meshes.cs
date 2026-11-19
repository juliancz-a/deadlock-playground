using System;
using Godot;
using DeadlockPlayground.Importer;
using DeadlockPlayground.Theming;

namespace DeadlockPlayground.UI
{
    public partial class CustomModelImporterModalUI
    {
        private OptionCardGroup _replaceModeCards;
        private Control _boxHeroKeepSection;
        private Control _boxReplaceModeSection;
        private MeshThumbnailService _meshThumbService;
        private string _isolatedSubmeshName = "";

        private Control BuildTab4MeshesAndMaterials()
        {
            if (_meshThumbService == null)
            {
                _meshThumbService = new MeshThumbnailService { Name = "MeshThumbnailService" };
                AddChild(_meshThumbService);
            }

            var scroll = new ScrollContainer
            {
                Name = "4. Meshes",
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
            vBox.AddThemeConstantOverride("separation", ImporterStyle.Gap);
            margin.AddChild(vBox);

            // Hidden backing OptionButton for ReplaceMode
            _optReplaceMode = new OptionButton { Visible = false };
            _optReplaceMode.AddItem("Full replacement", (int)CustomModelAutoRigger.ReplaceMode.FullReplacement);
            _optReplaceMode.AddItem("Partial outfit", (int)CustomModelAutoRigger.ReplaceMode.PartialAccessory);
            _optReplaceMode.Select(0);
            _optReplaceMode.ItemSelected += (idx) => SyncReplaceModeUI((int)idx);
            vBox.AddChild(_optReplaceMode);

            // Mode selector at the top (hidden in Accessory workflow where Tab 1 manages add vs replace intent)
            var replaceModeBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            replaceModeBox.AddThemeConstantOverride("separation", 6);
            _boxReplaceModeSection = replaceModeBox;
            var lblMode = new Label { Text = "Replacement Mode" };
            ImporterStyle.ApplySectionTitleStyle(lblMode);
            replaceModeBox.AddChild(lblMode);

            _replaceModeCards = new OptionCardGroup(columns: 1);
            _replaceModeCards.SetOptions(new[]
            {
                new OptionCardGroup.OptionDef
                {
                    Id = (int)CustomModelAutoRigger.ReplaceMode.FullReplacement,
                    Title = "Full replacement",
                    Description = "Replaces the entire hero model and hides original hero submeshes.",
                    IconPath = "res://assets/at-icons/person_body.svg",
                    Recommended = true
                },
                new OptionCardGroup.OptionDef
                {
                    Id = (int)CustomModelAutoRigger.ReplaceMode.PartialAccessory,
                    Title = "Partial outfit",
                    Description = "Keep selected original hero parts alongside your custom meshes.",
                    IconPath = "res://assets/at-icons/layers.svg",
                    Recommended = false
                }
            }, (int)CustomModelAutoRigger.ReplaceMode.FullReplacement);
            _replaceModeCards.OptionSelected += (modeId) =>
            {
                for (int i = 0; i < _optReplaceMode.ItemCount; i++)
                {
                    if (_optReplaceMode.GetItemId(i) == modeId)
                    {
                        _optReplaceMode.Select(i);
                        SyncReplaceModeUI(i);
                        break;
                    }
                }
            };
            replaceModeBox.AddChild(_replaceModeCards);
            vBox.AddChild(replaceModeBox);

            // Section 1: Your model's parts
            var lblParts = new Label { Text = "Your Model's Parts" };
            ImporterStyle.ApplySectionTitleStyle(lblParts);
            vBox.AddChild(lblParts);

            // Chip filters above the grid
            var chipRow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            chipRow.AddThemeConstantOverride("h_separation", 8);
            chipRow.AddThemeConstantOverride("v_separation", 6);

            _chkIncludeBakedOutlines = new CheckBox
            {
                Text = "Outline shells",
                ButtonPressed = true,
                ThemeTypeVariation = "ToolbarButton",
                TooltipText = "Toggle all [OUTLINE SHELL] meshes"
            };
            ImporterStyle.ApplyFontSize(_chkIncludeBakedOutlines, ImporterStyle.FontHint);
            _chkIncludeBakedOutlines.Toggled += (includeOutlines) =>
            {
                if (_isGizmoUpdatingFromUI) return;
                foreach (var chk in _importedSubmeshCheckboxes)
                {
                    if (!chk.HasMeta("SubmeshName")) continue;
                    string sName = chk.GetMeta("SubmeshName").AsString();
                    if (_submeshIsOutlineShell.TryGetValue(sName, out bool isOutline) && isOutline)
                    {
                        chk.SetPressedNoSignal(includeOutlines);
                    }
                }
                OnImportedSubmeshesChanged();
            };
            chipRow.AddChild(_chkIncludeBakedOutlines);

            _chkLinkWeaponSubmeshes = new CheckBox
            {
                Text = "Link weapon parts",
                ButtonPressed = true,
                ThemeTypeVariation = "ToolbarButton",
                TooltipText = "Synchronize bone, target, rotation, position, and scale across weapon submeshes"
            };
            ImporterStyle.ApplyFontSize(_chkLinkWeaponSubmeshes, ImporterStyle.FontHint);
            chipRow.AddChild(_chkLinkWeaponSubmeshes);
            vBox.AddChild(chipRow);

            _boxImportedSubmeshes = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _boxImportedSubmeshes.AddThemeConstantOverride("separation", ImporterStyle.Gap);
            vBox.AddChild(_boxImportedSubmeshes);

            // Section 2: Keep from original hero (shown only in Partial mode)
            var heroKeepBox = new VBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                Visible = false
            };
            heroKeepBox.AddThemeConstantOverride("separation", ImporterStyle.Gap);
            _boxHeroKeepSection = heroKeepBox;
            vBox.AddChild(heroKeepBox);

            heroKeepBox.AddChild(new HSeparator());
            var lblKeep = new Label { Text = "Keep from Original Hero" };
            ImporterStyle.ApplySectionTitleStyle(lblKeep);
            heroKeepBox.AddChild(lblKeep);

            var lblKeepHint = new Label { Text = "Click a hero part card to keep it visible. Kept parts show a green border." };
            ImporterStyle.ApplyHintStyle(lblKeepHint);
            heroKeepBox.AddChild(lblKeepHint);

            _boxHeroSubmeshes = new VBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                Visible = true
            };
            _boxHeroSubmeshes.AddThemeConstantOverride("separation", ImporterStyle.Gap);
            heroKeepBox.AddChild(_boxHeroSubmeshes);

            // Reposition Kept Hero Part Drawer
            var (keptCard, keptBody) = CreateInspectorCard("Reposition Kept Hero Weapon / Part");
            _boxKeptPartCard = keptCard;
            keptCard.Visible = false;
            heroKeepBox.AddChild(keptCard);

            var lblKeptHint = new Label
            {
                Text = "Nudge or rotate a kept Deadlock weapon/part in rest pose while preserving 100% of its original bone weights and animations.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            ImporterStyle.ApplyHintStyle(lblKeptHint);
            _secondaryLabels.Add(lblKeptHint);
            keptBody.AddChild(lblKeptHint);

            var keptTargetRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            keptTargetRow.AddThemeConstantOverride("separation", 6);
            _optKeptPartTarget = new OptionButton
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(140, 34)
            };
            ConfigureOptionButton(_optKeptPartTarget);
            _optKeptPartTarget.ItemSelected += (_) =>
            {
                SyncKeptPartSpinboxesFromSelected();
                int gTgt = _optGizmoTarget?.Selected >= 0 ? _optGizmoTarget.GetItemId(_optGizmoTarget.Selected) : 0;
                if (gTgt == 4) UpdateGizmoTargetTransform();
            };
            keptTargetRow.AddChild(_optKeptPartTarget);

            _btnKeptGizmoToggle = new Button
            {
                Text = "3D Gizmo",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(88, 34),
                TooltipText = "Selects this kept hero weapon/part in the 3D viewport gizmo so you can drag or rotate it directly."
            };
            ImporterStyle.ApplyFontSize(_btnKeptGizmoToggle, ImporterStyle.FontHint);
            _btnKeptGizmoToggle.Pressed += () => SelectGizmoTargetById(4);
            keptTargetRow.AddChild(_btnKeptGizmoToggle);
            keptBody.AddChild(keptTargetRow);

            SpinBox MakeKeptSpin(double min, double max, double step, double val, string prefix, double arrowStep)
            {
                var sb = CreateCoordSpinBox(min, max, step, prefix);
                sb.Value = val;
                sb.CustomArrowStep = arrowStep;
                sb.ValueChanged += (_) => OnKeptPartSpinboxChanged();
                return sb;
            }

            var keptPosGrid = new GridContainer { Columns = 3, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            keptPosGrid.AddThemeConstantOverride("h_separation", 4);
            _spinKeptPosX = MakeKeptSpin(-2.0, 2.0, 0.001, 0.0, "X", 0.005);
            _spinKeptPosY = MakeKeptSpin(-2.0, 2.0, 0.001, 0.0, "Y", 0.005);
            _spinKeptPosZ = MakeKeptSpin(-2.0, 2.0, 0.001, 0.0, "Z", 0.005);
            keptPosGrid.AddChild(_spinKeptPosX); keptPosGrid.AddChild(_spinKeptPosY); keptPosGrid.AddChild(_spinKeptPosZ);
            keptBody.AddChild(keptPosGrid);

            var keptRotGrid = new GridContainer { Columns = 3, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            keptRotGrid.AddThemeConstantOverride("h_separation", 4);
            _spinKeptRotPitch = MakeKeptSpin(-180.0, 180.0, 0.1, 0.0, "Pitch", 1.0);
            _spinKeptRotYaw = MakeKeptSpin(-180.0, 180.0, 0.1, 0.0, "Yaw", 1.0);
            _spinKeptRotRoll = MakeKeptSpin(-180.0, 180.0, 0.1, 0.0, "Roll", 1.0);
            keptRotGrid.AddChild(_spinKeptRotPitch); keptRotGrid.AddChild(_spinKeptRotYaw); keptRotGrid.AddChild(_spinKeptRotRoll);
            keptBody.AddChild(keptRotGrid);

            var keptScaleRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            keptScaleRow.AddThemeConstantOverride("separation", 6);
            _spinKeptScale = MakeKeptSpin(0.1, 5.0, 0.01, 1.0, "Scale", 0.02);
            keptScaleRow.AddChild(_spinKeptScale);

            _btnKeptResetPart = new Button
            {
                Text = "Reset Part",
                ThemeTypeVariation = "ImporterSecondaryButton",
                CustomMinimumSize = new Vector2(104, 34),
                TooltipText = "Resets the selected kept hero weapon/part back to its original position, rotation, and scale."
            };
            ImporterStyle.ApplyFontSize(_btnKeptResetPart, ImporterStyle.FontHint);
            _btnKeptResetPart.Pressed += OnResetSelectedKeptPartPressed;
            keptScaleRow.AddChild(_btnKeptResetPart);
            keptBody.AddChild(keptScaleRow);

            return scroll;
        }

        private void SyncReplaceModeUI(int selectedIdx)
        {
            if (_optReplaceMode == null) return;
            var mode = (CustomModelAutoRigger.ReplaceMode)_optReplaceMode.GetItemId(selectedIdx);
            _replaceModeCards?.Select((int)mode);
            if (_boxHeroKeepSection != null)
                _boxHeroKeepSection.Visible = mode == CustomModelAutoRigger.ReplaceMode.PartialAccessory;
            UpdatePreviewHeroKeepAndGhostMaterials();
        }

        private void IsolateSubmeshIn3D(string meshName)
        {
            if (string.IsNullOrEmpty(meshName) || _previewModelInstance == null) return;
            bool unIsolate = string.Equals(_isolatedSubmeshName, meshName, StringComparison.Ordinal);
            _isolatedSubmeshName = unIsolate ? "" : meshName;

            var meshes = CustomModelAutoRigger.CollectMeshes(_previewModelInstance);
            foreach (var m in meshes)
            {
                if (m == null || !GodotObject.IsInstanceValid(m)) continue;
                if (unIsolate)
                {
                    bool enabled = true;
                    foreach (var chk in _importedSubmeshCheckboxes)
                    {
                        if (chk.HasMeta("SubmeshName") && chk.GetMeta("SubmeshName").AsString() == m.Name.ToString())
                        {
                            enabled = chk.ButtonPressed;
                            break;
                        }
                    }
                    m.Visible = enabled;
                }
                else
                {
                    m.Visible = m.Name.ToString() == meshName;
                }
            }
        }
    }
}
