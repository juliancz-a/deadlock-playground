using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using DeadlockPlayground.Catalog;
using DeadlockPlayground.Importer;
using DeadlockPlayground.Painter;
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

        // Contained scrollable mesh areas & filters
        private ScrollContainer _scrollImportedSubmeshes;
        private ScrollContainer _scrollHeroSubmeshes;
        private Label _lblImportedPartsCount;
        private Label _lblHeroKeepCount;
        private Label _lblHeroKeepSectionTitle;
        private LineEdit _txtImportedPartsSearch;
        private GridContainer _importedSubmeshGrid;

        // Lazy-loaded Vanilla Game Model Donor Meshes
        private sealed class DonorMeshTransformState
        {
            public Vector3 PositionOffset = Vector3.Zero;
            public Vector3 RotationDeg = Vector3.Zero;
            public float Scale = 1.0f;
            public bool IsRigid = true;
            public string ParentBone = "spine_3";
            public bool SnapToBone = false;
            public Vector3 LocalBoundsCenter = Vector3.Zero;
        }

        private OptionButton _optDonorVanillaHero;
        private Button _btnLoadDonorVanillaHero;
        private Button _btnUnloadDonorVanillaHero;
        private Label _lblDonorVanillaStatus;
        private PanelContainer _panelDonorVanillaScroll;
        private ScrollContainer _scrollDonorVanillaMeshes;
        private VBoxContainer _boxDonorVanillaMeshes;
        private bool _isLoadingDonorVanillaHero = false;
        private string _activeDonorVanillaCodename = "";
        private readonly Dictionary<string, Node3D> _loadedDonorVanillaHeroes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _activeDonorMeshNamesByKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MeshThumbCard> _donorVanillaCardsByKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DonorMeshTransformState> _donorMeshStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Transform3D> _bakedDonorLocalTransforms = new(StringComparer.OrdinalIgnoreCase);

        // Added Vanilla Mesh Settings (Accessory-style Bone, Position, Rotation, Scale, Gizmo)
        private PanelContainer _boxDonorEditCard;
        private OptionButton _optDonorEditTarget;
        private Button _btnDonorEditGizmo;
        private OptionButton _optDonorEditMode;
        private Control _boxDonorBoneRow;
        private OptionButton _optDonorEditBone;
        private CheckBox _chkDonorEditAllBones;
        private CheckBox _chkDonorEditSnapBone;
        private Button _btnDonorEditMirrorLr;
        private SpinBox _spinDonorPosX;
        private SpinBox _spinDonorPosY;
        private SpinBox _spinDonorPosZ;
        private SpinBox _spinDonorRotPitch;
        private SpinBox _spinDonorRotYaw;
        private SpinBox _spinDonorRotRoll;
        private SpinBox _spinDonorScale;
        private Button _btnDonorEditReset;
        private bool _isSyncingDonorEditUi = false;

        private static PanelContainer CreateScrollableMeshArea(int minHeightPx, out ScrollContainer scroll, out VBoxContainer innerBox)
        {
            var panel = new PanelContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, minHeightPx)
            };

            var style = new StyleBoxFlat
            {
                BgColor = new Color(0.08f, 0.09f, 0.11f, 0.72f),
                BorderColor = new Color(0.26f, 0.30f, 0.29f, 0.85f),
                BorderWidthLeft = 1,
                BorderWidthTop = 1,
                BorderWidthRight = 1,
                BorderWidthBottom = 1,
                CornerRadiusTopLeft = 8,
                CornerRadiusTopRight = 8,
                CornerRadiusBottomLeft = 8,
                CornerRadiusBottomRight = 8,
                ContentMarginLeft = 6,
                ContentMarginTop = 6,
                ContentMarginRight = 6,
                ContentMarginBottom = 6
            };
            panel.AddThemeStyleboxOverride("panel", style);

            scroll = new ScrollContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, Math.Max(120, minHeightPx - 14)),
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Auto
            };
            panel.AddChild(scroll);

            var pad = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            pad.AddThemeConstantOverride("margin_left", 2);
            pad.AddThemeConstantOverride("margin_top", 2);
            pad.AddThemeConstantOverride("margin_right", 6);
            pad.AddThemeConstantOverride("margin_bottom", 4);
            scroll.AddChild(pad);

            innerBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            innerBox.AddThemeConstantOverride("separation", ImporterStyle.Gap);
            pad.AddChild(innerBox);

            return panel;
        }

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

            // =================================================================
            // Section 1: Your model's parts (inside a dedicated scrollable box)
            // =================================================================
            var partsHeaderRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            partsHeaderRow.AddThemeConstantOverride("separation", 6);
            var lblParts = new Label
            {
                Text = "Your Model's Parts",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            ImporterStyle.ApplySectionTitleStyle(lblParts);
            partsHeaderRow.AddChild(lblParts);

            _lblImportedPartsCount = new Label { Text = "0 parts" };
            ImporterStyle.ApplyHintStyle(_lblImportedPartsCount, wrap: false);
            partsHeaderRow.AddChild(_lblImportedPartsCount);

            var btnAllParts = new Button
            {
                Text = "All",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(40, 26),
                TooltipText = "Enable all meshes in Your Model's Parts"
            };
            ImporterStyle.ApplyFontSize(btnAllParts, ImporterStyle.FontHint);
            btnAllParts.Pressed += () =>
            {
                foreach (var chk in _importedSubmeshCheckboxes)
                {
                    if (GodotObject.IsInstanceValid(chk)) chk.SetPressedNoSignal(true);
                    if (_importedSubmeshCards.TryGetValue(chk, out var c) && GodotObject.IsInstanceValid(c)) c.SetActive(true);
                }
                OnImportedSubmeshesChanged();
                UpdateImportedPartsCountBadge();
            };
            partsHeaderRow.AddChild(btnAllParts);

            var btnNoneParts = new Button
            {
                Text = "None",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(48, 26),
                TooltipText = "Disable all meshes in Your Model's Parts"
            };
            ImporterStyle.ApplyFontSize(btnNoneParts, ImporterStyle.FontHint);
            btnNoneParts.Pressed += () =>
            {
                foreach (var chk in _importedSubmeshCheckboxes)
                {
                    if (GodotObject.IsInstanceValid(chk)) chk.SetPressedNoSignal(false);
                    if (_importedSubmeshCards.TryGetValue(chk, out var c) && GodotObject.IsInstanceValid(c)) c.SetActive(false);
                }
                OnImportedSubmeshesChanged();
                UpdateImportedPartsCountBadge();
            };
            partsHeaderRow.AddChild(btnNoneParts);

            var btnSculptImported = new Button
            {
                Text = "Reshape / Sculpt",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(110, 26),
                TooltipText = "Open Bottom Flare / Top Width / Inflate sliders and 3D Fit Sculpt Brush to reshape your sweater or accessory"
            };
            SetButtonIcon(btnSculptImported, "res://assets/at-icons/mesh_select.svg", 14);
            ImporterStyle.ApplyFontSize(btnSculptImported, ImporterStyle.FontHint);
            btnSculptImported.Pressed += JumpToImportedSculptFromMeshesTab;
            partsHeaderRow.AddChild(btnSculptImported);
            vBox.AddChild(partsHeaderRow);

            // Filter search + chip filters above the scrollable mesh grid
            var chipRow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            chipRow.AddThemeConstantOverride("h_separation", 8);
            chipRow.AddThemeConstantOverride("v_separation", 6);

            _txtImportedPartsSearch = new LineEdit
            {
                PlaceholderText = "Filter meshes...",
                ClearButtonEnabled = true,
                CustomMinimumSize = new Vector2(150, 30),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            ImporterStyle.ApplyFontSize(_txtImportedPartsSearch, ImporterStyle.FontHint);
            _txtImportedPartsSearch.TextChanged += ApplyImportedPartsSearchFilter;
            chipRow.AddChild(_txtImportedPartsSearch);

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
                        if (_importedSubmeshCards.TryGetValue(chk, out var c) && GodotObject.IsInstanceValid(c))
                            c.SetActive(includeOutlines);
                    }
                }
                OnImportedSubmeshesChanged();
                UpdateImportedPartsCountBadge();
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

            // Contained scrollable mesh box so long mesh lists scroll cleanly inside this area
            var importedScrollPanel = CreateScrollableMeshArea(320, out _scrollImportedSubmeshes, out _boxImportedSubmeshes);
            vBox.AddChild(importedScrollPanel);

            // =================================================================
            // Section 2: Keep from original hero (shown only in Partial mode)
            // =================================================================
            var heroKeepBox = new VBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                Visible = false
            };
            heroKeepBox.AddThemeConstantOverride("separation", ImporterStyle.Gap);
            _boxHeroKeepSection = heroKeepBox;
            vBox.AddChild(heroKeepBox);

            heroKeepBox.AddChild(new HSeparator());

            var heroKeepHeaderRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            heroKeepHeaderRow.AddThemeConstantOverride("separation", 6);
            _lblHeroKeepSectionTitle = new Label
            {
                Text = "Keep from Original Hero",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            ImporterStyle.ApplySectionTitleStyle(_lblHeroKeepSectionTitle);
            heroKeepHeaderRow.AddChild(_lblHeroKeepSectionTitle);

            _lblHeroKeepCount = new Label { Text = "0 kept" };
            ImporterStyle.ApplyHintStyle(_lblHeroKeepCount, wrap: false);
            heroKeepHeaderRow.AddChild(_lblHeroKeepCount);
            heroKeepBox.AddChild(heroKeepHeaderRow);

            var lblKeepHint = new Label { Text = "Click a part card to keep it visible. Kept parts show a green border." };
            ImporterStyle.ApplyHintStyle(lblKeepHint);
            heroKeepBox.AddChild(lblKeepHint);

            var heroKeepScrollPanel = CreateScrollableMeshArea(260, out _scrollHeroSubmeshes, out _boxHeroSubmeshes);
            heroKeepBox.AddChild(heroKeepScrollPanel);

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

            // =================================================================
            // Section 3 (Bottom): Lazy-load other Vanilla Game Models + Settings
            // =================================================================
            vBox.AddChild(new HSeparator());

            var (donorCard, donorBody) = CreateInspectorCard(
                "Add Meshes from Other Vanilla Heroes",
                "Lazy-load another Deadlock hero (other than the one you are replacing) to borrow its meshes, weapons, or outfit parts.");
            vBox.AddChild(donorCard);

            var donorPickerRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            donorPickerRow.AddThemeConstantOverride("separation", 6);

            _optDonorVanillaHero = new OptionButton
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(150, 34),
                TooltipText = "Choose another vanilla Deadlock hero to lazy-load its meshes"
            };
            ConfigureOptionButton(_optDonorVanillaHero);
            donorPickerRow.AddChild(_optDonorVanillaHero);

            _btnLoadDonorVanillaHero = new Button
            {
                Text = "Load Hero",
                ThemeTypeVariation = "ImporterPrimaryButton",
                CustomMinimumSize = new Vector2(96, 34),
                TooltipText = "Lazy-load this vanilla hero's meshes from pak01_dir.vpk without changing the active stage hero"
            };
            ImporterStyle.ApplyFontSize(_btnLoadDonorVanillaHero, ImporterStyle.FontHint);
            _btnLoadDonorVanillaHero.Pressed += OnLoadDonorVanillaHeroPressed;
            donorPickerRow.AddChild(_btnLoadDonorVanillaHero);

            _btnUnloadDonorVanillaHero = new Button
            {
                Text = "Unload",
                ThemeTypeVariation = "ImporterSecondaryButton",
                CustomMinimumSize = new Vector2(72, 34),
                Visible = false,
                TooltipText = "Unload the cached donor hero from RAM (any meshes already added to Your Model's Parts remain)"
            };
            ImporterStyle.ApplyFontSize(_btnUnloadDonorVanillaHero, ImporterStyle.FontHint);
            _btnUnloadDonorVanillaHero.Pressed += OnUnloadDonorVanillaHeroPressed;
            donorPickerRow.AddChild(_btnUnloadDonorVanillaHero);

            donorBody.AddChild(donorPickerRow);

            _lblDonorVanillaStatus = new Label
            {
                Text = "No other vanilla hero loaded yet (lazy-loaded on demand).",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            ImporterStyle.ApplyHintStyle(_lblDonorVanillaStatus);
            _secondaryLabels.Add(_lblDonorVanillaStatus);
            donorBody.AddChild(_lblDonorVanillaStatus);

            _panelDonorVanillaScroll = CreateScrollableMeshArea(240, out _scrollDonorVanillaMeshes, out _boxDonorVanillaMeshes);
            _panelDonorVanillaScroll.Visible = false;
            donorBody.AddChild(_panelDonorVanillaScroll);

            // Added Vanilla Mesh Settings Drawer (Accessory-style Parent Bone, Pos, Rot, Scale, Mirror, Gizmo)
            var (donorEditCard, donorEditBody) = CreateInspectorCard(
                "Added Vanilla Mesh Settings (Parent Bone & Transform)",
                "Adjust parent bone, position, rotation, and scale for any added vanilla hero mesh.");
            _boxDonorEditCard = donorEditCard;
            donorEditCard.Visible = false;
            donorBody.AddChild(donorEditCard);

            var donorEditTargetRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            donorEditTargetRow.AddThemeConstantOverride("separation", 6);
            _optDonorEditTarget = new OptionButton
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(140, 34)
            };
            ConfigureOptionButton(_optDonorEditTarget);
            _optDonorEditTarget.ItemSelected += (_) =>
            {
                SyncDonorEditUiFromSelected();
                int gTgt = _optGizmoTarget?.Selected >= 0 ? _optGizmoTarget.GetItemId(_optGizmoTarget.Selected) : 0;
                if (gTgt == 5) UpdateGizmoTargetTransform();
            };
            donorEditTargetRow.AddChild(_optDonorEditTarget);

            _btnDonorEditGizmo = new Button
            {
                Text = "3D Gizmo",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(88, 34),
                TooltipText = "Selects this added vanilla mesh in the 3D viewport gizmo so you can move or rotate it directly."
            };
            ImporterStyle.ApplyFontSize(_btnDonorEditGizmo, ImporterStyle.FontHint);
            _btnDonorEditGizmo.Pressed += () => SelectGizmoTargetById(5);
            donorEditTargetRow.AddChild(_btnDonorEditGizmo);
            donorEditBody.AddChild(donorEditTargetRow);

            var donorModeRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            donorModeRow.AddThemeConstantOverride("separation", 6);
            var lblDonorMode = new Label { Text = "Attach:", CustomMinimumSize = new Vector2(56, 0) };
            _secondaryLabels.Add(lblDonorMode);
            donorModeRow.AddChild(lblDonorMode);

            _optDonorEditMode = new OptionButton
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(140, 32)
            };
            ConfigureOptionButton(_optDonorEditMode);
            _optDonorEditMode.AddItem("Rigid to Parent Bone (Hat / Weapon / Prop)", 1);
            _optDonorEditMode.AddItem("Skinned Body (KNN Auto-Rig to Hero)", 0);
            _optDonorEditMode.Select(0);
            _optDonorEditMode.ItemSelected += (_) => OnDonorEditControlsChanged();
            donorModeRow.AddChild(_optDonorEditMode);
            donorEditBody.AddChild(donorModeRow);

            var donorBoneBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            donorBoneBox.AddThemeConstantOverride("separation", 4);
            _boxDonorBoneRow = donorBoneBox;

            var donorBoneRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            donorBoneRow.AddThemeConstantOverride("separation", 6);
            var lblDonorBone = new Label { Text = "Bone:", CustomMinimumSize = new Vector2(56, 0) };
            _secondaryLabels.Add(lblDonorBone);
            donorBoneRow.AddChild(lblDonorBone);

            _optDonorEditBone = new OptionButton
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(120, 32)
            };
            ConfigureOptionButton(_optDonorEditBone);
            _optDonorEditBone.ItemSelected += (_) => OnDonorEditControlsChanged();
            donorBoneRow.AddChild(_optDonorEditBone);

            _chkDonorEditAllBones = new CheckBox
            {
                Text = "All",
                ButtonPressed = false,
                TooltipText = "Show all hero skeleton bones instead of only common attachment sockets"
            };
            ImporterStyle.ApplyFontSize(_chkDonorEditAllBones, ImporterStyle.FontHint);
            _chkDonorEditAllBones.Toggled += (_) => PopulateDonorEditBonePicker(GetSelectedDonorMeshState()?.ParentBone);
            donorBoneRow.AddChild(_chkDonorEditAllBones);
            donorBoneBox.AddChild(donorBoneRow);

            var donorSnapRow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            donorSnapRow.AddThemeConstantOverride("h_separation", 8);
            donorSnapRow.AddThemeConstantOverride("v_separation", 4);

            _chkDonorEditSnapBone = new CheckBox
            {
                Text = "Snap to Bone Joint",
                ButtonPressed = false,
                TooltipText = "Center the mesh directly onto the chosen parent bone joint"
            };
            ImporterStyle.ApplyFontSize(_chkDonorEditSnapBone, ImporterStyle.FontHint);
            _chkDonorEditSnapBone.Toggled += (_) => OnDonorEditControlsChanged();
            donorSnapRow.AddChild(_chkDonorEditSnapBone);

            _btnDonorEditMirrorLr = new Button
            {
                Text = "Mirror L ↔ R",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(96, 28),
                TooltipText = "Swap Left/Right parent bone and mirror X position & Yaw/Roll"
            };
            ImporterStyle.ApplyFontSize(_btnDonorEditMirrorLr, ImporterStyle.FontHint);
            _btnDonorEditMirrorLr.Pressed += OnDonorEditMirrorLrPressed;
            donorSnapRow.AddChild(_btnDonorEditMirrorLr);
            donorBoneBox.AddChild(donorSnapRow);

            donorEditBody.AddChild(donorBoneBox);

            SpinBox MakeDonorSpin(double min, double max, double step, double val, string prefix, double arrowStep)
            {
                var sb = CreateCoordSpinBox(min, max, step, prefix);
                sb.Value = val;
                sb.CustomArrowStep = arrowStep;
                sb.ValueChanged += (_) => OnDonorEditControlsChanged();
                return sb;
            }

            var donorPosGrid = new GridContainer { Columns = 3, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            donorPosGrid.AddThemeConstantOverride("h_separation", 4);
            _spinDonorPosX = MakeDonorSpin(-2.0, 2.0, 0.001, 0.0, "X", 0.01);
            _spinDonorPosY = MakeDonorSpin(-2.0, 2.0, 0.001, 0.0, "Y", 0.01);
            _spinDonorPosZ = MakeDonorSpin(-2.0, 2.0, 0.001, 0.0, "Z", 0.01);
            donorPosGrid.AddChild(_spinDonorPosX); donorPosGrid.AddChild(_spinDonorPosY); donorPosGrid.AddChild(_spinDonorPosZ);
            donorEditBody.AddChild(donorPosGrid);

            var donorRotGrid = new GridContainer { Columns = 3, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            donorRotGrid.AddThemeConstantOverride("h_separation", 4);
            _spinDonorRotPitch = MakeDonorSpin(-180.0, 180.0, 0.5, 0.0, "Pitch", 2.0);
            _spinDonorRotYaw = MakeDonorSpin(-180.0, 180.0, 0.5, 0.0, "Yaw", 2.0);
            _spinDonorRotRoll = MakeDonorSpin(-180.0, 180.0, 0.5, 0.0, "Roll", 2.0);
            donorRotGrid.AddChild(_spinDonorRotPitch); donorRotGrid.AddChild(_spinDonorRotYaw); donorRotGrid.AddChild(_spinDonorRotRoll);
            donorEditBody.AddChild(donorRotGrid);

            var donorScaleRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            donorScaleRow.AddThemeConstantOverride("separation", 6);
            _spinDonorScale = MakeDonorSpin(0.05, 5.0, 0.01, 1.0, "Scale", 0.05);
            donorScaleRow.AddChild(_spinDonorScale);

            _btnDonorEditReset = new Button
            {
                Text = "Reset",
                ThemeTypeVariation = "ImporterSecondaryButton",
                CustomMinimumSize = new Vector2(84, 34),
                TooltipText = "Reset position, rotation, and scale of this added vanilla mesh"
            };
            ImporterStyle.ApplyFontSize(_btnDonorEditReset, ImporterStyle.FontHint);
            _btnDonorEditReset.Pressed += OnDonorEditResetPressed;
            donorScaleRow.AddChild(_btnDonorEditReset);
            donorEditBody.AddChild(donorScaleRow);

            return scroll;
        }

        private void UpdateImportedPartsCountBadge()
        {
            if (_lblImportedPartsCount == null || !GodotObject.IsInstanceValid(_lblImportedPartsCount)) return;
            int total = 0;
            int active = 0;
            foreach (var chk in _importedSubmeshCheckboxes)
            {
                if (!GodotObject.IsInstanceValid(chk)) continue;
                total++;
                if (chk.ButtonPressed) active++;
            }
            _lblImportedPartsCount.Text = $"{active}/{total} active";
        }

        private void UpdateHeroKeepCountBadge()
        {
            if (_lblHeroKeepCount == null || !GodotObject.IsInstanceValid(_lblHeroKeepCount)) return;
            int total = 0;
            int kept = 0;
            foreach (var chk in _heroKeepCheckboxes)
            {
                if (!GodotObject.IsInstanceValid(chk)) continue;
                total++;
                if (chk.ButtonPressed) kept++;
            }
            _lblHeroKeepCount.Text = $"{kept}/{total} kept";
        }

        private void ApplyImportedPartsSearchFilter(string query)
        {
            string q = (query ?? "").Trim();
            foreach (var (chk, card) in _importedSubmeshCards)
            {
                if (!GodotObject.IsInstanceValid(chk) || !GodotObject.IsInstanceValid(card)) continue;
                string name = chk.HasMeta("SubmeshName") ? chk.GetMeta("SubmeshName").AsString() : chk.Text;
                card.Visible = string.IsNullOrEmpty(q) || name.Contains(q, StringComparison.OrdinalIgnoreCase);
            }
        }

        private void PopulateDonorVanillaHeroDropdown()
        {
            if (_optDonorVanillaHero == null || !GodotObject.IsInstanceValid(_optDonorVanillaHero)) return;

            _optDonorVanillaHero.Clear();
            string currentHeroCodename = GetVpkLoader()?.LastLoadedHeroEntry?.InternalCodename ?? _heroCodename ?? "";
            string currentVmdl = (GetVpkLoader()?.LastLoadedVmdlPath ?? "").Replace('\\', '/');

            var candidates = DeadlockHeroCatalog.Entries
                .Where(e =>
                    !string.Equals(e.InternalCodename, currentHeroCodename, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(e.WipAlias ?? "", currentHeroCodename, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrEmpty(currentVmdl) || !string.Equals(e.VmdlRelativePath.Replace('\\', '/'), currentVmdl, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int idx = 0;
            foreach (var entry in candidates)
            {
                string label = entry.Category == HeroCategory.LegacyStaging
                    ? $"{entry.DisplayName} (Legacy)"
                    : entry.DisplayName;
                _optDonorVanillaHero.AddItem(label, idx);
                _optDonorVanillaHero.SetItemMetadata(idx, $"{entry.InternalCodename}|{entry.VmdlRelativePath}|{entry.DisplayName}");
                idx++;
            }

            if (_optDonorVanillaHero.ItemCount > 0)
            {
                _optDonorVanillaHero.Select(0);
                if (_btnLoadDonorVanillaHero != null) _btnLoadDonorVanillaHero.Disabled = false;
            }
            else if (_btnLoadDonorVanillaHero != null)
            {
                _btnLoadDonorVanillaHero.Disabled = true;
            }
        }

        private async void OnLoadDonorVanillaHeroPressed()
        {
            if (_isLoadingDonorVanillaHero || _optDonorVanillaHero == null || _optDonorVanillaHero.Selected < 0)
                return;

            string meta = _optDonorVanillaHero.GetItemMetadata(_optDonorVanillaHero.Selected).AsString();
            var parts = meta.Split('|');
            if (parts.Length < 3) return;

            string codename = parts[0];
            string vmdlPath = parts[1];
            string displayName = parts[2];

            // Already cached? Show immediately without re-reading VPK!
            if (_loadedDonorVanillaHeroes.TryGetValue(codename, out var cachedRoot) &&
                cachedRoot != null && GodotObject.IsInstanceValid(cachedRoot))
            {
                _activeDonorVanillaCodename = codename;
                PopulateDonorVanillaHeroMeshCards(codename, displayName, cachedRoot);
                if (_btnUnloadDonorVanillaHero != null) _btnUnloadDonorVanillaHero.Visible = true;
                return;
            }

            var vpkLoader = GetVpkLoader();
            if (vpkLoader == null)
            {
                if (_lblDonorVanillaStatus != null)
                    _lblDonorVanillaStatus.Text = "VPK loader unavailable — cannot load vanilla hero.";
                return;
            }

            _isLoadingDonorVanillaHero = true;
            if (_btnLoadDonorVanillaHero != null)
            {
                _btnLoadDonorVanillaHero.Disabled = true;
                _btnLoadDonorVanillaHero.Text = "Loading...";
            }
            if (_optDonorVanillaHero != null) _optDonorVanillaHero.Disabled = true;
            if (_lblDonorVanillaStatus != null)
                _lblDonorVanillaStatus.Text = $"Lazy-loading {displayName} from pak01_dir.vpk...";

            try
            {
                Node3D donorRoot = await vpkLoader.LoadStandaloneVanillaModelAsync(
                    vmdlPath,
                    codename,
                    progressMsg =>
                    {
                        if (_lblDonorVanillaStatus != null && GodotObject.IsInstanceValid(_lblDonorVanillaStatus))
                            _lblDonorVanillaStatus.Text = progressMsg;
                    });

                if (!GodotObject.IsInstanceValid(this))
                {
                    donorRoot?.QueueFree();
                    return;
                }

                if (donorRoot == null)
                {
                    if (_lblDonorVanillaStatus != null)
                        _lblDonorVanillaStatus.Text = $"Failed to load {displayName} ({vmdlPath}).";
                    return;
                }

                _loadedDonorVanillaHeroes[codename] = donorRoot;
                _activeDonorVanillaCodename = codename;
                PopulateDonorVanillaHeroMeshCards(codename, displayName, donorRoot);
                if (_btnUnloadDonorVanillaHero != null) _btnUnloadDonorVanillaHero.Visible = true;
                AppendConsoleMessage($"Lazy-loaded vanilla hero '{displayName}' ({codename}) for donor meshes.");
            }
            finally
            {
                _isLoadingDonorVanillaHero = false;
                if (_btnLoadDonorVanillaHero != null && GodotObject.IsInstanceValid(_btnLoadDonorVanillaHero))
                {
                    _btnLoadDonorVanillaHero.Disabled = false;
                    _btnLoadDonorVanillaHero.Text = "Load Hero";
                }
                if (_optDonorVanillaHero != null && GodotObject.IsInstanceValid(_optDonorVanillaHero))
                    _optDonorVanillaHero.Disabled = false;
            }
        }

        private void OnUnloadDonorVanillaHeroPressed()
        {
            if (string.IsNullOrEmpty(_activeDonorVanillaCodename)) return;

            if (_loadedDonorVanillaHeroes.TryGetValue(_activeDonorVanillaCodename, out var cached) &&
                cached != null && GodotObject.IsInstanceValid(cached))
            {
                cached.QueueFree();
            }
            _loadedDonorVanillaHeroes.Remove(_activeDonorVanillaCodename);
            _activeDonorVanillaCodename = "";

            if (_boxDonorVanillaMeshes != null)
            {
                foreach (Node child in _boxDonorVanillaMeshes.GetChildren()) child.QueueFree();
            }
            _donorVanillaCardsByKey.Clear();
            if (_panelDonorVanillaScroll != null) _panelDonorVanillaScroll.Visible = false;
            if (_btnUnloadDonorVanillaHero != null) _btnUnloadDonorVanillaHero.Visible = false;
            if (_lblDonorVanillaStatus != null)
                _lblDonorVanillaStatus.Text = "Unloaded cached donor hero. Any meshes already added remain in Your Model's Parts.";
            GC.Collect();
        }

        private void PopulateDonorVanillaHeroMeshCards(string heroCodename, string displayName, Node3D donorRoot)
        {
            if (_boxDonorVanillaMeshes == null || donorRoot == null || !GodotObject.IsInstanceValid(donorRoot))
                return;

            foreach (Node child in _boxDonorVanillaMeshes.GetChildren()) child.QueueFree();
            _donorVanillaCardsByKey.Clear();

            var donorGrid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            donorGrid.AddThemeConstantOverride("h_separation", 8);
            donorGrid.AddThemeConstantOverride("v_separation", 8);
            _boxDonorVanillaMeshes.AddChild(donorGrid);

            var donorMeshes = CustomModelAutoRigger.CollectMeshes(donorRoot);
            int cardCount = 0;

            foreach (var mi in donorMeshes)
            {
                if (mi?.Mesh == null) continue;
                int surfCount = mi.Mesh.GetSurfaceCount();
                if (surfCount == 0) continue;

                for (int s = 0; s < surfCount; s++)
                {
                    string matName = HeroMeshHierarchy.ResolveSurfaceMaterialName(mi, s);
                    string cleanPart = !string.IsNullOrEmpty(matName)
                        ? HeroMeshHierarchy.FormatCleanMaterialDisplayName(matName, heroCodename)
                        : (surfCount > 1 ? $"{mi.Name}_s{s}" : mi.Name.ToString());

                    string donorKey = $"{heroCodename}::{mi.Name}::s{s}";
                    bool isAlreadyAdded = _activeDonorMeshNamesByKey.ContainsKey(donorKey);

                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    int vCount = 0;
                    if (arrays != null && arrays.Count > (int)Mesh.ArrayType.Vertex)
                    {
                        var vVar = arrays[(int)Mesh.ArrayType.Vertex];
                        if (vVar.VariantType != Variant.Type.Nil)
                        {
                            var verts = vVar.As<Vector3[]>();
                            if (verts != null) vCount = verts.Length;
                        }
                    }
                    if (vCount == 0) continue;

                    var dCard = new MeshThumbCard();
                    dCard.Build(
                        cleanPart,
                        $"{displayName} · {vCount:N0} v",
                        "Vanilla",
                        new Color(0.45f, 0.78f, 0.96f),
                        isAlreadyAdded,
                        isHeroKeepMode: true);
                    dCard.SetCutButtonVisible(false);

                    MeshInstance3D capturedMi = mi;
                    int capturedSurf = s;
                    string capturedKey = donorKey;
                    string capturedClean = cleanPart;

                    dCard.Toggled += (useMesh) =>
                    {
                        OnDonorVanillaPartToggled(capturedKey, capturedClean, heroCodename, capturedMi, capturedSurf, donorRoot, useMesh);
                    };
                    dCard.IsolatePressed += () =>
                    {
                        if (!_activeDonorMeshNamesByKey.ContainsKey(capturedKey))
                        {
                            dCard.SetActive(true);
                            OnDonorVanillaPartToggled(capturedKey, capturedClean, heroCodename, capturedMi, capturedSurf, donorRoot, true);
                        }
                        if (_activeDonorMeshNamesByKey.TryGetValue(capturedKey, out string addedName))
                        {
                            IsolateSubmeshIn3D(addedName);
                        }
                    };

                    _donorVanillaCardsByKey[donorKey] = dCard;
                    donorGrid.AddChild(dCard);
                    _meshThumbService?.RequestThumbnail(
                        $"donor_{donorKey}",
                        mi.Mesh,
                        s,
                        new Color(0.70f, 0.84f, 0.96f),
                        tex => dCard.SetThumbnail(tex));
                    cardCount++;
                }
            }

            if (_panelDonorVanillaScroll != null) _panelDonorVanillaScroll.Visible = cardCount > 0;
            if (_lblDonorVanillaStatus != null)
            {
                _lblDonorVanillaStatus.Text = cardCount > 0
                    ? $"Loaded {displayName} — {cardCount} mesh part(s). Click Keep to add any part to Your Model's Parts."
                    : $"Loaded {displayName}, but no extractable mesh surfaces were found.";
            }
        }

        private static MeshInstance3D ExtractStandaloneHeroSpaceDonorMesh(
            MeshInstance3D sourceMi,
            int surfaceIndex,
            Node3D donorRoot,
            string uniqueMeshName,
            string heroCodename,
            string donorKey)
        {
            if (sourceMi?.Mesh == null || surfaceIndex < 0 || surfaceIndex >= sourceMi.Mesh.GetSurfaceCount())
                return null;

            var arrays = sourceMi.Mesh.SurfaceGetArrays(surfaceIndex);
            if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.Vertex)
                return null;

            var rawVerts = CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
            if (rawVerts == null || rawVerts.Length == 0)
                return null;

            var rawNorms = arrays.Count > (int)Mesh.ArrayType.Normal
                ? CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Normal])
                : null;
            var rawTangents = arrays.Count > (int)Mesh.ArrayType.Tangent
                ? CustomModelAutoRigger.GetFloat32Array(arrays[(int)Mesh.ArrayType.Tangent])
                : null;
            var rawUv = arrays.Count > (int)Mesh.ArrayType.TexUV
                ? arrays[(int)Mesh.ArrayType.TexUV].As<Vector2[]>()
                : null;
            var rawUv2 = arrays.Count > (int)Mesh.ArrayType.TexUV2
                ? arrays[(int)Mesh.ArrayType.TexUV2].As<Vector2[]>()
                : null;
            var rawColors = arrays.Count > (int)Mesh.ArrayType.Color
                ? arrays[(int)Mesh.ArrayType.Color].As<Color[]>()
                : null;
            var rawIndices = arrays.Count > (int)Mesh.ArrayType.Index
                ? CustomModelAutoRigger.GetInt32Array(arrays[(int)Mesh.ArrayType.Index])
                : null;

            // Bake the mesh's rest transform relative to the vanilla donor hero root into Hero Space
            Transform3D toHero = CustomModelAutoRigger.ComputeTransformRelativeToRoot(sourceMi, donorRoot);
            Basis normBasis = MathF.Abs(toHero.Basis.Determinant()) > 1e-8f
                ? toHero.Basis.Inverse().Transposed()
                : toHero.Basis;

            var bakedVerts = new Vector3[rawVerts.Length];
            Vector3[] bakedNorms = rawNorms != null && rawNorms.Length == rawVerts.Length
                ? new Vector3[rawVerts.Length]
                : null;

            for (int i = 0; i < rawVerts.Length; i++)
            {
                bakedVerts[i] = toHero * rawVerts[i];
                if (bakedNorms != null)
                {
                    bakedNorms[i] = (normBasis * rawNorms[i]).Normalized();
                }
            }

            var newArrays = new Godot.Collections.Array();
            newArrays.Resize((int)Mesh.ArrayType.Max);
            newArrays[(int)Mesh.ArrayType.Vertex] = bakedVerts;
            if (bakedNorms != null) newArrays[(int)Mesh.ArrayType.Normal] = bakedNorms;
            if (rawTangents != null && rawTangents.Length == rawVerts.Length * 4)
                newArrays[(int)Mesh.ArrayType.Tangent] = rawTangents;
            if (rawUv != null && rawUv.Length == rawVerts.Length)
                newArrays[(int)Mesh.ArrayType.TexUV] = rawUv;
            if (rawUv2 != null && rawUv2.Length == rawVerts.Length)
                newArrays[(int)Mesh.ArrayType.TexUV2] = rawUv2;
            if (rawColors != null && rawColors.Length == rawVerts.Length)
                newArrays[(int)Mesh.ArrayType.Color] = rawColors;
            if (rawIndices != null && rawIndices.Length > 0)
                newArrays[(int)Mesh.ArrayType.Index] = rawIndices;

            var outMesh = new ArrayMesh();
            outMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, newArrays);

            Material mat = sourceMi.GetSurfaceOverrideMaterial(surfaceIndex)
                        ?? sourceMi.MaterialOverride
                        ?? sourceMi.Mesh.SurfaceGetMaterial(surfaceIndex);
            if (mat != null)
            {
                outMesh.SurfaceSetMaterial(0, mat);
            }

            var outMi = new MeshInstance3D
            {
                Name = uniqueMeshName,
                Mesh = outMesh,
                Transform = Transform3D.Identity
            };
            outMi.Layers &= ~(uint)(1 << 20); // Strictly clear Visual Layer 21
            outMi.SetMeta("IsHeroSpaceDonorMesh", true);
            outMi.SetMeta("VanillaDonorHero", heroCodename);
            outMi.SetMeta("VanillaDonorKey", donorKey);
            return outMi;
        }

        private Node3D GetOrCreateDonorContainer(Node3D sceneRoot)
        {
            if (sceneRoot == null || !GodotObject.IsInstanceValid(sceneRoot)) return null;
            var existing = sceneRoot.GetNodeOrNull<Node3D>("VanillaDonorMeshes");
            if (existing != null && GodotObject.IsInstanceValid(existing)) return existing;
            var container = new Node3D { Name = "VanillaDonorMeshes" };
            sceneRoot.AddChild(container);
            return container;
        }

        private static string SanitizeDonorMeshNodeName(string heroCodename, string partName)
        {
            string raw = $"Vanilla_{heroCodename}_{partName}";
            var chars = raw.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
            return new string(chars);
        }

        private void OnDonorVanillaPartToggled(
            string donorKey,
            string cleanPartName,
            string heroCodename,
            MeshInstance3D sourceMi,
            int surfaceIndex,
            Node3D donorRoot,
            bool useMesh)
        {
            if (_liveImportedScene == null || !GodotObject.IsInstanceValid(_liveImportedScene))
                return;

            if (useMesh)
            {
                if (_activeDonorMeshNamesByKey.ContainsKey(donorKey))
                    return;

                string baseName = SanitizeDonorMeshNodeName(heroCodename, cleanPartName);
                string uniqueName = baseName;
                int suffix = 1;
                while (_submeshCardControls.ContainsKey(uniqueName))
                {
                    uniqueName = $"{baseName}_{suffix++}";
                }

                var liveMesh = ExtractStandaloneHeroSpaceDonorMesh(sourceMi, surfaceIndex, donorRoot, uniqueName, heroCodename, donorKey);
                if (liveMesh == null) return;

                var liveContainer = GetOrCreateDonorContainer(_liveImportedScene);
                liveContainer?.AddChild(liveMesh);

                MeshInstance3D previewMesh = null;
                if (_previewModelInstance != null && GodotObject.IsInstanceValid(_previewModelInstance))
                {
                    previewMesh = (MeshInstance3D)liveMesh.Duplicate();
                    previewMesh.Name = uniqueName;
                    previewMesh.SetMeta("IsHeroSpaceDonorMesh", true);
                    previewMesh.SetMeta("VanillaDonorHero", heroCodename);
                    previewMesh.SetMeta("VanillaDonorKey", donorKey);
                    var prevContainer = GetOrCreateDonorContainer(_previewModelInstance);
                    prevContainer?.AddChild(previewMesh);
                }

                _activeDonorMeshNamesByKey[donorKey] = uniqueName;

                Vector3 boundsCenter = liveMesh.Mesh != null ? liveMesh.Mesh.GetAabb().GetCenter() : Vector3.Zero;
                string lowerPart = cleanPartName.ToLowerInvariant();
                bool guessWeapon = lowerPart.Contains("gun") || lowerPart.Contains("pistol") || lowerPart.Contains("weapon") ||
                                   lowerPart.Contains("rifle") || lowerPart.Contains("sword") || lowerPart.Contains("shotgun");
                bool guessHead = lowerPart.Contains("hat") || lowerPart.Contains("head") || lowerPart.Contains("hair") ||
                                 lowerPart.Contains("mask") || lowerPart.Contains("glasses") || lowerPart.Contains("helmet");
                string defaultBone = guessWeapon ? "weapon_hand_R" : (guessHead ? "head" : "spine_3");
                if (_previewHeroSkeleton != null && _previewHeroSkeleton.FindBone(defaultBone) < 0)
                {
                    defaultBone = guessWeapon ? "hand_R" : "spine_2";
                }
                bool defaultRigid = guessWeapon || guessHead;

                var state = new DonorMeshTransformState
                {
                    LocalBoundsCenter = boundsCenter,
                    IsRigid = defaultRigid,
                    ParentBone = defaultBone,
                    SnapToBone = guessWeapon
                };
                _donorMeshStates[uniqueName] = state;

                if (_importedSubmeshGrid != null && GodotObject.IsInstanceValid(_importedSubmeshGrid))
                {
                    var socketBones = GetHeroSocketBoneNames();
                    var heroMeshes = CustomModelAutoRigger.CollectOriginalHeroMeshes(_liveHeroNode);
                    int weaponCount = 0;
                    AddImportedSubmeshCard(previewMesh ?? liveMesh, _importedSubmeshGrid, socketBones, heroMeshes, ref weaponCount);
                    ApplyImportedPartsSearchFilter(_txtImportedPartsSearch?.Text ?? "");
                }

                RefreshDonorEditTargetDropdown(selectMeshName: uniqueName);
                ApplyDonorMeshStateToPipeline(uniqueName, state, triggerRig: false);
                UpdateImportedPartsCountBadge();
                TriggerRealtimeRigUpdate();
                AppendConsoleMessage($"Added vanilla donor mesh '{uniqueName}' from '{heroCodename}'.");
            }
            else
            {
                if (!_activeDonorMeshNamesByKey.TryGetValue(donorKey, out string addedName))
                    return;

                _activeDonorMeshNamesByKey.Remove(donorKey);
                _donorMeshStates.Remove(addedName);

                // Remove from live and preview imported scenes
                var liveContainer = _liveImportedScene.GetNodeOrNull<Node3D>("VanillaDonorMeshes");
                var liveNode = liveContainer?.GetNodeOrNull<Node>(addedName);
                if (liveNode != null && GodotObject.IsInstanceValid(liveNode))
                {
                    liveNode.GetParent()?.RemoveChild(liveNode);
                    liveNode.QueueFree();
                }

                var prevContainer = _previewModelInstance?.GetNodeOrNull<Node3D>("VanillaDonorMeshes");
                var prevNode = prevContainer?.GetNodeOrNull<Node>(addedName);
                if (prevNode != null && GodotObject.IsInstanceValid(prevNode))
                {
                    prevNode.GetParent()?.RemoveChild(prevNode);
                    prevNode.QueueFree();
                }

                // Remove its card and tracking entries from Your Model's Parts
                CheckBox targetChk = _importedSubmeshCheckboxes.FirstOrDefault(c =>
                    GodotObject.IsInstanceValid(c) &&
                    c.HasMeta("SubmeshName") &&
                    string.Equals(c.GetMeta("SubmeshName").AsString(), addedName, StringComparison.Ordinal));

                if (targetChk != null)
                {
                    if (_importedSubmeshCards.TryGetValue(targetChk, out var card) && GodotObject.IsInstanceValid(card))
                    {
                        card.GetParent()?.RemoveChild(card);
                        card.QueueFree();
                    }
                    _importedSubmeshCards.Remove(targetChk);
                    _importedSubmeshCheckboxes.Remove(targetChk);
                }

                _submeshCardControls.Remove(addedName);
                _submeshSocketAssignments.Remove(addedName);
                _submeshHeroReplacements.Remove(addedName);
                _submeshSnapToSocket.Remove(addedName);
                _submeshSocketOffsetPos.Remove(addedName);
                _submeshSocketOffsetRot.Remove(addedName);
                _submeshSocketScale.Remove(addedName);
                _submeshIsOutlineShell.Remove(addedName);
                _submeshOutlineCheckboxes.Remove(addedName);
                _submeshOutlineBadges.Remove(addedName);
                _importedMeshCutMasks.Remove(addedName);
                _importedMeshCutMasks.Remove(CustomModelAutoRigger.MakeSurfaceKeepKey(addedName, 0));

                RefreshDonorEditTargetDropdown();
                UpdateImportedPartsCountBadge();
                TriggerRealtimeRigUpdate();
                AppendConsoleMessage($"Removed vanilla donor mesh '{addedName}'.");
            }
        }

        private string GetSelectedDonorEditMeshName()
        {
            if (_optDonorEditTarget == null || _optDonorEditTarget.Selected < 0) return "";
            return _optDonorEditTarget.GetItemMetadata(_optDonorEditTarget.Selected).AsString();
        }

        private DonorMeshTransformState GetSelectedDonorMeshState()
        {
            string meshName = GetSelectedDonorEditMeshName();
            if (string.IsNullOrEmpty(meshName)) return null;
            return _donorMeshStates.TryGetValue(meshName, out var st) ? st : null;
        }

        private void PopulateDonorEditBonePicker(string preferredBone = null)
        {
            if (_optDonorEditBone == null || !GodotObject.IsInstanceValid(_optDonorEditBone)) return;

            string prevText = preferredBone;
            if (string.IsNullOrEmpty(prevText) && _optDonorEditBone.Selected >= 0)
            {
                prevText = _optDonorEditBone.GetItemText(_optDonorEditBone.Selected).Replace("★ ", "");
            }

            _optDonorEditBone.Clear();
            bool showAll = _chkDonorEditAllBones?.ButtonPressed ?? false;
            var bones = new List<string>();
            if (showAll && _previewHeroSkeleton != null)
            {
                for (int b = 0; b < _previewHeroSkeleton.GetBoneCount(); b++)
                    bones.Add(_previewHeroSkeleton.GetBoneName(b));
            }
            else
            {
                bones = GetHeroSocketBoneNames();
            }

            int selIdx = 0;
            for (int i = 0; i < bones.Count; i++)
            {
                string bName = bones[i];
                _optDonorEditBone.AddItem(bName, i);
                if (!string.IsNullOrEmpty(prevText) &&
                    string.Equals(bName.Replace("★ ", ""), prevText.Replace("★ ", ""), StringComparison.OrdinalIgnoreCase))
                {
                    selIdx = i;
                }
            }
            if (_optDonorEditBone.ItemCount > 0) _optDonorEditBone.Select(selIdx);
        }

        private void RefreshDonorEditTargetDropdown(string selectMeshName = null)
        {
            if (_optDonorEditTarget == null || !GodotObject.IsInstanceValid(_optDonorEditTarget)) return;

            string prevSelected = selectMeshName ?? GetSelectedDonorEditMeshName();
            _optDonorEditTarget.Clear();

            var addedNames = _activeDonorMeshNamesByKey.Values.Distinct(StringComparer.Ordinal).ToList();
            if (addedNames.Count == 0)
            {
                if (_boxDonorEditCard != null) _boxDonorEditCard.Visible = false;
                return;
            }

            if (_boxDonorEditCard != null) _boxDonorEditCard.Visible = true;
            int selIdx = 0;
            for (int i = 0; i < addedNames.Count; i++)
            {
                _optDonorEditTarget.AddItem(addedNames[i], i);
                _optDonorEditTarget.SetItemMetadata(i, addedNames[i]);
                if (!string.IsNullOrEmpty(prevSelected) && string.Equals(addedNames[i], prevSelected, StringComparison.Ordinal))
                {
                    selIdx = i;
                }
            }
            _optDonorEditTarget.Select(selIdx);
            SyncDonorEditUiFromSelected();
        }

        private void SyncDonorEditUiFromSelected(bool repopulateBones = true)
        {
            var st = GetSelectedDonorMeshState();
            if (st == null) return;

            _isSyncingDonorEditUi = true;
            try
            {
                if (repopulateBones)
                {
                    if (_optDonorEditMode != null)
                    {
                        for (int i = 0; i < _optDonorEditMode.ItemCount; i++)
                        {
                            if (_optDonorEditMode.GetItemId(i) == (st.IsRigid ? 1 : 0))
                            {
                                _optDonorEditMode.Select(i);
                                break;
                            }
                        }
                    }
                    if (_boxDonorBoneRow != null) _boxDonorBoneRow.Visible = st.IsRigid;
                    PopulateDonorEditBonePicker(st.ParentBone);
                    _chkDonorEditSnapBone?.SetPressedNoSignal(st.SnapToBone);
                }
                _spinDonorPosX?.SetValueNoSignal(Math.Round(st.PositionOffset.X, 4));
                _spinDonorPosY?.SetValueNoSignal(Math.Round(st.PositionOffset.Y, 4));
                _spinDonorPosZ?.SetValueNoSignal(Math.Round(st.PositionOffset.Z, 4));
                _spinDonorRotPitch?.SetValueNoSignal(Math.Round(st.RotationDeg.X, 2));
                _spinDonorRotYaw?.SetValueNoSignal(Math.Round(st.RotationDeg.Y, 2));
                _spinDonorRotRoll?.SetValueNoSignal(Math.Round(st.RotationDeg.Z, 2));
                _spinDonorScale?.SetValueNoSignal(Math.Round(st.Scale, 3));
            }
            finally
            {
                _isSyncingDonorEditUi = false;
            }
        }

        private void OnDonorEditControlsChanged()
        {
            if (_isSyncingDonorEditUi) return;
            string meshName = GetSelectedDonorEditMeshName();
            if (string.IsNullOrEmpty(meshName) || !_donorMeshStates.TryGetValue(meshName, out var st)) return;

            bool prevRigid = st.IsRigid;
            string prevBone = st.ParentBone;
            bool prevSnap = st.SnapToBone;

            st.IsRigid = _optDonorEditMode != null && _optDonorEditMode.Selected >= 0 && _optDonorEditMode.GetItemId(_optDonorEditMode.Selected) == 1;
            if (_boxDonorBoneRow != null) _boxDonorBoneRow.Visible = st.IsRigid;
            if (_optDonorEditBone != null && _optDonorEditBone.Selected >= 0)
            {
                st.ParentBone = _optDonorEditBone.GetItemText(_optDonorEditBone.Selected).Replace("★ ", "");
            }
            st.SnapToBone = _chkDonorEditSnapBone?.ButtonPressed ?? false;
            st.PositionOffset = new Vector3(
                (float)(_spinDonorPosX?.Value ?? 0.0),
                (float)(_spinDonorPosY?.Value ?? 0.0),
                (float)(_spinDonorPosZ?.Value ?? 0.0));
            st.RotationDeg = new Vector3(
                (float)(_spinDonorRotPitch?.Value ?? 0.0),
                (float)(_spinDonorRotYaw?.Value ?? 0.0),
                (float)(_spinDonorRotRoll?.Value ?? 0.0));
            st.Scale = Math.Max(0.05f, (float)(_spinDonorScale?.Value ?? 1.0));

            bool structuralChange = (st.IsRigid != prevRigid) ||
                                    !string.Equals(st.ParentBone, prevBone, StringComparison.OrdinalIgnoreCase) ||
                                    (st.SnapToBone != prevSnap);
            ApplyDonorMeshStateToPipeline(meshName, st, triggerRig: true, forceFullRebake: structuralChange);
        }

        private void OnDonorEditMirrorLrPressed()
        {
            string meshName = GetSelectedDonorEditMeshName();
            if (string.IsNullOrEmpty(meshName) || !_donorMeshStates.TryGetValue(meshName, out var st)) return;

            string mirroredBone = CustomModelAutoRigger.MirrorBoneNameLtoR(st.ParentBone);
            if (!string.IsNullOrEmpty(mirroredBone) && _previewHeroSkeleton != null && _previewHeroSkeleton.FindBone(mirroredBone) >= 0)
            {
                st.ParentBone = mirroredBone;
            }
            st.PositionOffset = new Vector3(-st.PositionOffset.X, st.PositionOffset.Y, st.PositionOffset.Z);
            st.RotationDeg = new Vector3(st.RotationDeg.X, -st.RotationDeg.Y, -st.RotationDeg.Z);

            SyncDonorEditUiFromSelected();
            ApplyDonorMeshStateToPipeline(meshName, st, triggerRig: true, forceFullRebake: true);
        }

        private void OnDonorEditResetPressed()
        {
            string meshName = GetSelectedDonorEditMeshName();
            if (string.IsNullOrEmpty(meshName) || !_donorMeshStates.TryGetValue(meshName, out var st)) return;

            st.PositionOffset = Vector3.Zero;
            st.RotationDeg = Vector3.Zero;
            st.Scale = 1.0f;
            SyncDonorEditUiFromSelected();
            ApplyDonorMeshStateToPipeline(meshName, st, triggerRig: true, forceFullRebake: true);
        }

        private void SnapshotBakedDonorLocalTransforms()
        {
            _bakedDonorLocalTransforms.Clear();
            var liveContainer = _liveImportedScene?.GetNodeOrNull<Node3D>("VanillaDonorMeshes");
            if (liveContainer == null || !GodotObject.IsInstanceValid(liveContainer)) return;
            foreach (Node child in liveContainer.GetChildren())
            {
                if (child is MeshInstance3D mi && GodotObject.IsInstanceValid(mi))
                {
                    _bakedDonorLocalTransforms[mi.Name.ToString()] = mi.Transform;
                }
            }
        }

        private bool TryApplyFastDonorPreviewTransform(string meshName, DonorMeshTransformState st, Transform3D nodeLocalTransform)
        {
            if (_previewRiggedContainer == null || !GodotObject.IsInstanceValid(_previewRiggedContainer) || st == null)
                return false;

            if (st.IsRigid)
            {
                foreach (Node child in _previewRiggedContainer.GetChildren())
                {
                    if (child is not MeshInstance3D mi || !mi.HasMeta("IsSocketProp") || !mi.GetMeta("IsSocketProp").AsBool())
                        continue;
                    if (mi.HasMeta("SocketMeshKey") &&
                        string.Equals(mi.GetMeta("SocketMeshKey").AsString(), meshName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (CustomModelAutoRigger.TryUpdateSocketSkinBindPose(mi, st.PositionOffset, st.RotationDeg, st.Scale))
                        {
                            SetRigChipStatus("Live Rig: Up to date", StatusTone.Success);
                            return true;
                        }
                    }
                }
                return false;
            }

            // Skinned Body (KNN): apply instant 0.01ms visual delta transform to the preview MeshInstance3D
            // while dragging/adjusting; full KNN weight re-projection runs once on mouse release.
            Transform3D baked = _bakedDonorLocalTransforms.TryGetValue(meshName, out var bxf) ? bxf : Transform3D.Identity;
            if (Mathf.Abs(baked.Basis.Determinant()) <= 1e-8f) return false;
            Transform3D delta = nodeLocalTransform * baked.AffineInverse();

            string expectedPrefix = $"Custom_{meshName}";
            bool updatedAny = false;
            foreach (Node child in _previewRiggedContainer.GetChildren())
            {
                if (child is not MeshInstance3D mi || !GodotObject.IsInstanceValid(mi)) continue;
                string cName = mi.Name.ToString();
                if (cName.Equals(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
                    cName.StartsWith(expectedPrefix + "_", StringComparison.OrdinalIgnoreCase))
                {
                    mi.Transform = delta;
                    updatedAny = true;
                }
            }
            return updatedAny;
        }

        private void ApplyDonorMeshStateToPipeline(string meshName, DonorMeshTransformState st, bool triggerRig, bool forceFullRebake = false)
        {
            if (string.IsNullOrEmpty(meshName) || st == null) return;

            Transform3D nodeLocalTransform = Transform3D.Identity;
            if (!st.IsRigid)
            {
                Basis rotBasis = Basis.FromEuler(
                    new Vector3(Mathf.DegToRad(st.RotationDeg.X), Mathf.DegToRad(st.RotationDeg.Y), Mathf.DegToRad(st.RotationDeg.Z)),
                    EulerOrder.Yxz).Scaled(new Vector3(st.Scale, st.Scale, st.Scale));
                Vector3 c = st.LocalBoundsCenter;
                nodeLocalTransform = new Transform3D(rotBasis, c + st.PositionOffset - rotBasis * c);
            }

            // Apply local transform on both live & preview donor MeshInstance3D nodes
            var liveMi = _liveImportedScene?.GetNodeOrNull<Node3D>("VanillaDonorMeshes")?.GetNodeOrNull<MeshInstance3D>(meshName);
            if (liveMi != null && GodotObject.IsInstanceValid(liveMi)) liveMi.Transform = nodeLocalTransform;
            var prevMi = _previewModelInstance?.GetNodeOrNull<Node3D>("VanillaDonorMeshes")?.GetNodeOrNull<MeshInstance3D>(meshName);
            if (prevMi != null && GodotObject.IsInstanceValid(prevMi)) prevMi.Transform = nodeLocalTransform;

            if (st.IsRigid)
            {
                _submeshSocketAssignments[meshName] = st.ParentBone;
                _submeshSnapToSocket[meshName] = st.SnapToBone;
                _submeshSocketOffsetPos[meshName] = st.PositionOffset;
                _submeshSocketOffsetRot[meshName] = st.RotationDeg;
                _submeshSocketScale[meshName] = st.Scale;
            }
            else
            {
                _submeshSocketAssignments.Remove(meshName);
                _submeshSnapToSocket.Remove(meshName);
                _submeshSocketOffsetPos.Remove(meshName);
                _submeshSocketOffsetRot.Remove(meshName);
                _submeshSocketScale.Remove(meshName);
            }

            // Keep the matching card inside "Your Model's Parts" synchronized
            if (_submeshCardControls.TryGetValue(meshName, out var ctrl))
            {
                bool prevGuard = _isGizmoUpdatingFromUI;
                _isGizmoUpdatingFromUI = true;
                try
                {
                    if (forceFullRebake)
                    {
                        ctrl.Role?.Select(st.IsRigid ? 1 : 0);
                        ctrl.Placement?.Select(st.SnapToBone ? 1 : 0);
                        if (ctrl.Socket != null && !string.IsNullOrEmpty(st.ParentBone))
                        {
                            for (int i = 0; i < ctrl.Socket.ItemCount; i++)
                            {
                                if (string.Equals(ctrl.Socket.GetItemText(i).Replace("★ ", ""), st.ParentBone, StringComparison.OrdinalIgnoreCase))
                                {
                                    ctrl.Socket.Select(i);
                                    break;
                                }
                            }
                        }
                    }
                    ctrl.Pitch?.SetValueNoSignal(st.RotationDeg.X);
                    ctrl.Yaw?.SetValueNoSignal(st.RotationDeg.Y);
                    ctrl.Roll?.SetValueNoSignal(st.RotationDeg.Z);
                    ctrl.PosX?.SetValueNoSignal(st.PositionOffset.X);
                    ctrl.PosY?.SetValueNoSignal(st.PositionOffset.Y);
                    ctrl.PosZ?.SetValueNoSignal(st.PositionOffset.Z);
                    ctrl.Scale?.SetValueNoSignal(st.Scale);
                }
                finally
                {
                    _isGizmoUpdatingFromUI = prevGuard;
                }
            }

            if (triggerRig)
            {
                bool fastApplied = !forceFullRebake && TryApplyFastDonorPreviewTransform(meshName, st, nodeLocalTransform);
                if (!fastApplied)
                {
                    ScheduleDebouncedLiveRigUpdate();
                }
                else if (!st.IsRigid)
                {
                    // Visual delta applied instantaneously at 60 FPS; schedule a single deferred KNN re-projection after drag ends
                    ScheduleDebouncedLiveRigUpdate();
                }

                int gTgt = _optGizmoTarget?.Selected >= 0 ? _optGizmoTarget.GetItemId(_optGizmoTarget.Selected) : 0;
                if (gTgt == 5 && !_isGizmoDragging && !(_previewGizmo?.Editing ?? false))
                {
                    UpdateGizmoTargetTransform();
                }
            }
        }

        private void CleanupDonorVanillaHeroCache()
        {
            foreach (var kv in _loadedDonorVanillaHeroes)
            {
                if (kv.Value != null && GodotObject.IsInstanceValid(kv.Value))
                {
                    kv.Value.QueueFree();
                }
            }
            _loadedDonorVanillaHeroes.Clear();
            _activeDonorMeshNamesByKey.Clear();
            _donorVanillaCardsByKey.Clear();
            _donorMeshStates.Clear();
            _bakedDonorLocalTransforms.Clear();
            _activeDonorVanillaCodename = "";
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
