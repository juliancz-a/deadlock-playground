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
        // =========================================================================
        // Inline 3D Mesh Cut / Trim Mode State & UI
        // =========================================================================

        private readonly Dictionary<string, int[]> _originalMeshCutMasks = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int[]> _importedMeshCutMasks = new(StringComparer.OrdinalIgnoreCase);
        private readonly MeshCutEditorCore.MeshCutHistory _meshCutHistory = new();

        private bool _isMeshCutModeActive;
        private MeshCutEditorCore.SurfaceCutSessionData _activeCutSession;
        private MeshCutTool _activeCutTool = MeshCutTool.Lasso;
        private MeshCutReferenceMode _activeCutRefMode = MeshCutReferenceMode.IsolateTarget;
        private bool _cutRestoreMode;
        private bool _cutThroughEnabled = true;
        private bool _cutXRayEnabled = false;
        private bool _cutMirrorX;
        private bool _cutShowGhost = true;
        private bool _cutBoneIncludeChildren = true;
        private float _cutBrushRadius = 0.06f;

        private bool _isCutDraggingLasso;
        private bool _isCutDraggingBox;
        private bool _isCutDraggingBrush;
        private bool _cutBrushMutatedSelection;
        private MeshCutSelectOp _cutDragOp = MeshCutSelectOp.Replace;
        private Vector3 _lastCutBrushWorld;
        private bool _hasLastCutBrushDab;

        // 2D & 3D Overlays
        private MeshCutOverlay2D _meshCutOverlay2D;
        private MeshInstance3D _meshCutSelectedOverlay3D;
        private MeshInstance3D _meshCutGhostOverlay3D;
        private StandardMaterial3D _meshCutSelectMat;
        private StandardMaterial3D _meshCutGhostMat;
        private StandardMaterial3D _meshCutXRayTargetMat;

        // Floating Viewport Cut HUD controls
        private PanelContainer _meshCutViewportHud;
        private Label _lblCutTargetTitle;
        private Label _lblCutStats;
        private readonly Button[] _cutToolButtons = new Button[5];
        private Button _btnCutModeCut;
        private Button _btnCutModeRestore;
        private CheckBox _chkCutThrough;
        private CheckBox _chkCutXRay;
        private CheckBox _chkCutMirrorX;
        private CheckBox _chkCutShowGhost;
        private Button _btnCutShowOthers;
        private Button _btnCutGhostOthers;
        private HBoxContainer _boxCutBrushRow;
        private HSlider _sliderCutBrushRadius;
        private Label _lblCutBrushRadiusVal;
        private HBoxContainer _boxCutBoneRow;
        private OptionButton _optCutBonePicker;
        private CheckBox _chkCutBoneChildren;
        private Button _btnCutSelectBoneApply;
        private Button _btnCutSelected;
        private Button _btnCutKeepOnly;
        private Button _btnCutRestoreSelected;
        private Button _btnCutUndo;
        private Button _btnCutRedo;
        private Button _btnCutResetMesh;
        private Button _btnCutGrow;
        private Button _btnCutShrink;
        private Button _btnCutInvert;
        private Button _btnCutClearSel;
        private int _savedGizmoTargetBeforeCut;

        private bool IsMeshCutActive() => _isMeshCutModeActive && _activeCutSession != null;

        private static Dictionary<string, int[]> CloneCutMasks(Dictionary<string, int[]> src)
        {
            if (src == null || src.Count == 0) return null;
            var copy = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in src)
            {
                if (!string.IsNullOrWhiteSpace(k) && v != null && v.Length >= 2)
                    copy[k] = (int[])v.Clone();
            }
            return copy.Count > 0 ? copy : null;
        }

        private void BuildMeshCutViewportHud(Control viewportPanel)
        {
            if (viewportPanel == null) return;

            // Mount top-level 2D Lasso/Box overlay directly inside viewportPanel above SubViewportContainer
            _meshCutOverlay2D = new MeshCutOverlay2D { Name = "MeshCutOverlay2D" };
            viewportPanel.AddChild(_meshCutOverlay2D);

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
            _meshCutViewportHud = hud;

            var pad = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            pad.AddThemeConstantOverride("margin_left", 8);
            pad.AddThemeConstantOverride("margin_right", 8);
            pad.AddThemeConstantOverride("margin_top", 6);
            pad.AddThemeConstantOverride("margin_bottom", 6);
            hud.AddChild(pad);

            var root = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            root.AddThemeConstantOverride("separation", 5);
            pad.AddChild(root);

            // Row 1: Title, live triangle counts, Undo/Redo, Reset, Done
            var row1 = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row1.AddThemeConstantOverride("separation", 6);

            _lblCutTargetTitle = new Label
            {
                Text = "Mesh Cut Mode",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
            };
            ImporterStyle.ApplyFontSize(_lblCutTargetTitle, ImporterStyle.FontBody);
            _accentLabels.Add(_lblCutTargetTitle);
            row1.AddChild(_lblCutTargetTitle);

            _lblCutStats = new Label { Text = "0 / 0 tris" };
            ImporterStyle.ApplyHintStyle(_lblCutStats, wrap: false);
            _secondaryLabels.Add(_lblCutStats);
            row1.AddChild(_lblCutStats);

            _btnCutUndo = new Button
            {
                Text = "Undo",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(54, 26),
                Disabled = true,
                TooltipText = "Undo last cut or selection change (Ctrl+Z)"
            };
            ImporterStyle.ApplyFontSize(_btnCutUndo, ImporterStyle.FontHint);
            _btnCutUndo.Pressed += OnMeshCutUndoPressed;
            row1.AddChild(_btnCutUndo);

            _btnCutRedo = new Button
            {
                Text = "Redo",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(54, 26),
                Disabled = true,
                TooltipText = "Redo last undone action (Ctrl+Y / Ctrl+Shift+Z)"
            };
            ImporterStyle.ApplyFontSize(_btnCutRedo, ImporterStyle.FontHint);
            _btnCutRedo.Pressed += OnMeshCutRedoPressed;
            row1.AddChild(_btnCutRedo);

            _btnCutResetMesh = new Button
            {
                Text = "Reset Mesh",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(82, 26),
                Disabled = true,
                TooltipText = "Restore all cut triangles on this mesh back to original (undoable)"
            };
            ImporterStyle.ApplyFontSize(_btnCutResetMesh, ImporterStyle.FontHint);
            _btnCutResetMesh.Pressed += OnMeshCutResetPressed;
            row1.AddChild(_btnCutResetMesh);

            var btnDone = new Button
            {
                Text = "Done",
                ThemeTypeVariation = "ImporterPrimaryButton",
                CustomMinimumSize = new Vector2(68, 26),
                TooltipText = "Exit Mesh Cut Mode and keep your changes (Esc)"
            };
            ImporterStyle.ApplyFontSize(btnDone, ImporterStyle.FontHint);
            btnDone.Pressed += ExitMeshCutMode;
            row1.AddChild(btnDone);
            root.AddChild(row1);

            // Row 2: Tool selector + Select/Restore mode + Reference overlay mode
            var row2 = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row2.AddThemeConstantOverride("h_separation", 6);
            row2.AddThemeConstantOverride("v_separation", 4);

            (string Label, string Tip, string Icon, MeshCutTool Tool)[] toolDefs =
            {
                ("Lasso (L)", "Draw a freehand 2D lasso polygon to select triangles (Shift: +Add, Alt: -Sub)", "res://assets/at-icons/lasso.svg", MeshCutTool.Lasso),
                ("Box (B)", "Drag a 2D marquee box to select triangles (Shift: +Add, Alt: -Sub)", "res://assets/at-icons/selection_square.svg", MeshCutTool.Box),
                ("Brush (P)", "Paint-select triangles directly in 3D ([ / ] to resize, Alt to erase selection)", "res://assets/at-icons/mesh_select.svg", MeshCutTool.Brush),
                ("Island (I)", "Click any triangle to select its entire connected mesh island", "res://assets/at-icons/mesh_polygon.svg", MeshCutTool.Island),
                ("Bone (K)", "Select all triangles weighted to a skeleton bone (great for isolating hands/limbs!)", "res://assets/at-icons/person_body.svg", MeshCutTool.BoneRegion)
            };

            for (int i = 0; i < toolDefs.Length; i++)
            {
                var def = toolDefs[i];
                var tb = new Button
                {
                    Text = def.Label,
                    ToggleMode = true,
                    ButtonPressed = def.Tool == _activeCutTool,
                    ThemeTypeVariation = "ToolbarButton",
                    CustomMinimumSize = new Vector2(72, 26),
                    TooltipText = def.Tip
                };
                SetButtonIcon(tb, def.Icon, 14);
                ImporterStyle.ApplyFontSize(tb, ImporterStyle.FontHint);
                MeshCutTool capturedTool = def.Tool;
                tb.Pressed += () => SetActiveMeshCutTool(capturedTool);
                _cutToolButtons[i] = tb;
                row2.AddChild(tb);
            }

            _btnCutModeCut = new Button
            {
                Text = "Cut Mode",
                ToggleMode = true,
                ButtonPressed = true,
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(74, 26),
                TooltipText = "Select visible triangles to cut away or isolate"
            };
            ImporterStyle.ApplyFontSize(_btnCutModeCut, ImporterStyle.FontHint);
            _btnCutModeCut.Pressed += () => SetMeshCutRestoreMode(false);
            row2.AddChild(_btnCutModeCut);

            _btnCutModeRestore = new Button
            {
                Text = "Restore",
                ToggleMode = true,
                ButtonPressed = false,
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(68, 26),
                TooltipText = "Select previously cut (red ghost) triangles to restore them"
            };
            ImporterStyle.ApplyFontSize(_btnCutModeRestore, ImporterStyle.FontHint);
            _btnCutModeRestore.Pressed += () => SetMeshCutRestoreMode(true);
            row2.AddChild(_btnCutModeRestore);

            _btnCutShowOthers = new Button
            {
                Text = "Show Other Meshes",
                ToggleMode = true,
                ButtonPressed = false,
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(126, 26),
                TooltipText = "Show/hide all other hero & accessory meshes (hidden by default for unobstructed cutting)"
            };
            ImporterStyle.ApplyFontSize(_btnCutShowOthers, ImporterStyle.FontHint);
            _btnCutShowOthers.Toggled += (showOthers) =>
            {
                if (!showOthers)
                {
                    _activeCutRefMode = MeshCutReferenceMode.IsolateTarget;
                    _btnCutGhostOthers?.SetPressedNoSignal(false);
                }
                else
                {
                    _activeCutRefMode = (_btnCutGhostOthers?.ButtonPressed ?? false)
                        ? MeshCutReferenceMode.GhostOthers
                        : MeshCutReferenceMode.FitCheck;
                }
                if (_btnCutGhostOthers != null) _btnCutGhostOthers.Disabled = !showOthers;
                ApplyMeshCutReferenceContext();
            };
            row2.AddChild(_btnCutShowOthers);

            _btnCutGhostOthers = new Button
            {
                Text = "Ghost Others",
                ToggleMode = true,
                ButtonPressed = false,
                Disabled = true,
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(92, 26),
                TooltipText = "Render other meshes semi-transparent so you can check accessory fit without blocking clicks"
            };
            ImporterStyle.ApplyFontSize(_btnCutGhostOthers, ImporterStyle.FontHint);
            _btnCutGhostOthers.Toggled += (ghost) =>
            {
                if (!(_btnCutShowOthers?.ButtonPressed ?? false)) return;
                _activeCutRefMode = ghost ? MeshCutReferenceMode.GhostOthers : MeshCutReferenceMode.FitCheck;
                ApplyMeshCutReferenceContext();
            };
            row2.AddChild(_btnCutGhostOthers);
            root.AddChild(row2);

            // Row 3: Tool-specific Brush / Bone options (shown conditionally)
            _boxCutBrushRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            _boxCutBrushRow.AddThemeConstantOverride("separation", 8);
            var lblBR = new Label { Text = "Brush Radius:" };
            ImporterStyle.ApplyHintStyle(lblBR, wrap: false);
            _boxCutBrushRow.AddChild(lblBR);
            _sliderCutBrushRadius = new HSlider
            {
                MinValue = 0.01,
                MaxValue = 0.25,
                Step = 0.005,
                Value = _cutBrushRadius,
                CustomMinimumSize = new Vector2(140, 20),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
            };
            _lblCutBrushRadiusVal = new Label { Text = $"{_cutBrushRadius:F2}m", CustomMinimumSize = new Vector2(46, 0) };
            ImporterStyle.ApplyHintStyle(_lblCutBrushRadiusVal, wrap: false);
            _sliderCutBrushRadius.ValueChanged += (v) =>
            {
                _cutBrushRadius = (float)v;
                if (_lblCutBrushRadiusVal != null) _lblCutBrushRadiusVal.Text = $"{v:F2}m";
            };
            _boxCutBrushRow.AddChild(_sliderCutBrushRadius);
            _boxCutBrushRow.AddChild(_lblCutBrushRadiusVal);
            root.AddChild(_boxCutBrushRow);

            _boxCutBoneRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            _boxCutBoneRow.AddThemeConstantOverride("separation", 6);
            var lblBone = new Label { Text = "Bone:" };
            ImporterStyle.ApplyHintStyle(lblBone, wrap: false);
            _boxCutBoneRow.AddChild(lblBone);
            _optCutBonePicker = new OptionButton
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(150, 26),
                TooltipText = "Pick a bone to select all triangles influenced by it (or click directly on the mesh in 3D)"
            };
            ConfigureOptionButton(_optCutBonePicker);
            _boxCutBoneRow.AddChild(_optCutBonePicker);

            _chkCutBoneChildren = new CheckBox
            {
                Text = "Include Child Bones",
                ButtonPressed = _cutBoneIncludeChildren,
                TooltipText = "Also include all descendant bones (e.g. picking hand_L selects all 5 fingers too!)"
            };
            ImporterStyle.ApplyFontSize(_chkCutBoneChildren, ImporterStyle.FontHint);
            _chkCutBoneChildren.Toggled += (on) => _cutBoneIncludeChildren = on;
            _boxCutBoneRow.AddChild(_chkCutBoneChildren);

            _btnCutSelectBoneApply = new Button
            {
                Text = "Select Bone Region",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(128, 26),
                TooltipText = "Select all triangles weighted to the chosen bone (Shift+Click to add, Alt+Click to subtract)"
            };
            ImporterStyle.ApplyFontSize(_btnCutSelectBoneApply, ImporterStyle.FontHint);
            _btnCutSelectBoneApply.Pressed += OnMeshCutSelectChosenBonePressed;
            _boxCutBoneRow.AddChild(_btnCutSelectBoneApply);
            root.AddChild(_boxCutBoneRow);

            // Row 4: Modifiers + Selection helpers + Cut / Keep Only / Restore actions
            var row4 = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row4.AddThemeConstantOverride("h_separation", 6);
            row4.AddThemeConstantOverride("v_separation", 4);

            _chkCutThrough = new CheckBox
            {
                Text = "Cut Through (T)",
                ButtonPressed = _cutThroughEnabled,
                TooltipText = "Select & cut all the way through front, interior, and back faces simultaneously (uncheck for Front Surface Only)"
            };
            ImporterStyle.ApplyFontSize(_chkCutThrough, ImporterStyle.FontHint);
            _chkCutThrough.Toggled += (on) =>
            {
                _cutThroughEnabled = on;
                RefreshMeshCutOverlays3D();
            };
            row4.AddChild(_chkCutThrough);

            _chkCutXRay = new CheckBox
            {
                Text = "X-Ray View (Z)",
                ButtonPressed = _cutXRayEnabled,
                TooltipText = "Make the mesh semi-transparent with see-through highlights so you can inspect back/interior faces"
            };
            ImporterStyle.ApplyFontSize(_chkCutXRay, ImporterStyle.FontHint);
            _chkCutXRay.Toggled += (on) =>
            {
                _cutXRayEnabled = on;
                ApplyMeshCutReferenceContext();
                RefreshMeshCutOverlays3D();
            };
            row4.AddChild(_chkCutXRay);

            _chkCutMirrorX = new CheckBox
            {
                Text = "Mirror X (M)",
                ButtonPressed = _cutMirrorX,
                TooltipText = "Symmetrically mirror selections across left/right (works for Lasso, Box, Brush, Island, and Bone!)"
            };
            ImporterStyle.ApplyFontSize(_chkCutMirrorX, ImporterStyle.FontHint);
            _chkCutMirrorX.Toggled += (on) => _cutMirrorX = on;
            row4.AddChild(_chkCutMirrorX);

            _chkCutShowGhost = new CheckBox
            {
                Text = "Show Cut Ghost",
                ButtonPressed = _cutShowGhost,
                TooltipText = "Show already-cut triangles as a faint red translucent ghost"
            };
            ImporterStyle.ApplyFontSize(_chkCutShowGhost, ImporterStyle.FontHint);
            _chkCutShowGhost.Toggled += (on) =>
            {
                _cutShowGhost = on;
                RefreshMeshCutOverlays3D();
            };
            row4.AddChild(_chkCutShowGhost);

            _btnCutGrow = new Button { Text = "Grow +", ThemeTypeVariation = "ToolbarButton", CustomMinimumSize = new Vector2(58, 26), TooltipText = "Expand selection by 1 face ring (+)" };
            ImporterStyle.ApplyFontSize(_btnCutGrow, ImporterStyle.FontHint);
            _btnCutGrow.Pressed += OnMeshCutGrowPressed;
            row4.AddChild(_btnCutGrow);

            _btnCutShrink = new Button { Text = "Shrink -", ThemeTypeVariation = "ToolbarButton", CustomMinimumSize = new Vector2(62, 26), TooltipText = "Shrink selection by 1 face ring (-)" };
            ImporterStyle.ApplyFontSize(_btnCutShrink, ImporterStyle.FontHint);
            _btnCutShrink.Pressed += OnMeshCutShrinkPressed;
            row4.AddChild(_btnCutShrink);

            _btnCutInvert = new Button { Text = "Invert", ThemeTypeVariation = "ToolbarButton", CustomMinimumSize = new Vector2(52, 26), TooltipText = "Invert current selection (Ctrl+I)" };
            ImporterStyle.ApplyFontSize(_btnCutInvert, ImporterStyle.FontHint);
            _btnCutInvert.Pressed += OnMeshCutInvertPressed;
            row4.AddChild(_btnCutInvert);

            _btnCutClearSel = new Button { Text = "Clear", ThemeTypeVariation = "ToolbarButton", CustomMinimumSize = new Vector2(48, 26), TooltipText = "Clear current selection (Ctrl+D)" };
            ImporterStyle.ApplyFontSize(_btnCutClearSel, ImporterStyle.FontHint);
            _btnCutClearSel.Pressed += OnMeshCutClearSelectionPressed;
            row4.AddChild(_btnCutClearSel);

            _btnCutSelected = new Button
            {
                Text = "Cut Selected (Del)",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(118, 26),
                Disabled = true,
                TooltipText = "Remove all currently selected triangles (non-destructive, undoable)"
            };
            _btnCutSelected.AddThemeColorOverride("font_color", new Color(1.0f, 0.48f, 0.46f));
            ImporterStyle.ApplyFontSize(_btnCutSelected, ImporterStyle.FontHint);
            _btnCutSelected.Pressed += OnMeshCutDeleteSelectedPressed;
            row4.AddChild(_btnCutSelected);

            _btnCutKeepOnly = new Button
            {
                Text = "Keep Only Selected",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(126, 26),
                Disabled = true,
                TooltipText = "Keep ONLY the selected triangles and cut everything else away (Shift+X)"
            };
            _btnCutKeepOnly.AddThemeColorOverride("font_color", new Color(0.42f, 0.92f, 0.60f));
            ImporterStyle.ApplyFontSize(_btnCutKeepOnly, ImporterStyle.FontHint);
            _btnCutKeepOnly.Pressed += OnMeshCutKeepOnlySelectedPressed;
            row4.AddChild(_btnCutKeepOnly);

            _btnCutRestoreSelected = new Button
            {
                Text = "Restore Selected",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(114, 26),
                Visible = false,
                Disabled = true,
                TooltipText = "Bring back the selected cut triangles (Del / Enter)"
            };
            _btnCutRestoreSelected.AddThemeColorOverride("font_color", new Color(0.32f, 0.90f, 0.98f));
            ImporterStyle.ApplyFontSize(_btnCutRestoreSelected, ImporterStyle.FontHint);
            _btnCutRestoreSelected.Pressed += OnMeshCutRestoreSelectedPressed;
            row4.AddChild(_btnCutRestoreSelected);
            root.AddChild(row4);

            // Row 5: Compact ergonomics hint
            var lblHint = new Label
            {
                Text = "LMB: Select · Shift: +Add · Alt: -Sub · RMB / MMB: Orbit (Shift: Pan) · Wheel: Zoom",
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
            };
            ImporterStyle.ApplyHintStyle(lblHint, wrap: false);
            _secondaryLabels.Add(lblHint);
            root.AddChild(lblHint);
        }

        // =========================================================================
        // Enter / Exit Inline Mesh Cut Mode
        // =========================================================================

        private void EnterMeshCutModeForOriginalHero(string keepKey, string displayName)
        {
            if (_previewHeroClone == null || !GodotObject.IsInstanceValid(_previewHeroClone) || string.IsNullOrWhiteSpace(keepKey))
                return;

            // Ensure Partial mode & this hero part is kept visible so the user can see and cut it cleanly
            if (_workflowIntent != ImportWorkflowIntent.AccessoryAddOrReplace && _optReplaceMode != null && _optReplaceMode.Selected != 1)
            {
                _optReplaceMode.Select(1);
                SyncReplaceModeUI(1);
            }
            foreach (var chk in _heroKeepCheckboxes)
            {
                if (GodotObject.IsInstanceValid(chk) && chk.HasMeta("SubmeshName") &&
                    string.Equals(chk.GetMeta("SubmeshName").AsString(), keepKey, StringComparison.OrdinalIgnoreCase))
                {
                    if (!chk.ButtonPressed) chk.ButtonPressed = true;
                    break;
                }
            }

            if (!TryResolvePreviewHeroSurface(keepKey, out var targetMi, out int surfIdx))
            {
                SetBottomStatus($"Could not locate hero surface '{displayName}' in 3D preview.", StatusTone.Warning);
                return;
            }

            string canonicalKey = CustomModelAutoRigger.MakeOriginalSurfaceDeformKey(targetMi, surfIdx);
            int[] existingRle = null;
            if (!_originalMeshCutMasks.TryGetValue(canonicalKey, out existingRle))
                _originalMeshCutMasks.TryGetValue(keepKey, out existingRle);

            BeginMeshCutSession(
                targetMi,
                surfIdx,
                canonicalKey,
                displayName,
                isOriginalHeroMesh: true,
                existingRle);
        }

        private void EnterMeshCutModeForImportedMesh(string rawMeshName, string displayName)
        {
            if (string.IsNullOrWhiteSpace(rawMeshName)) return;

            // Ensure the imported part is enabled
            foreach (var chk in _importedSubmeshCheckboxes)
            {
                if (GodotObject.IsInstanceValid(chk) && chk.HasMeta("SubmeshName") &&
                    string.Equals(chk.GetMeta("SubmeshName").AsString(), rawMeshName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!chk.ButtonPressed) chk.ButtonPressed = true;
                    break;
                }
            }

            MeshInstance3D targetMi = null;
            int surfIdx = 0;

            // Prefer rigged preview mesh so Bone-Region select and live animations work seamlessly!
            if (_previewRiggedContainer != null && GodotObject.IsInstanceValid(_previewRiggedContainer))
            {
                foreach (var mi in CustomModelAutoRigger.CollectMeshes(_previewRiggedContainer))
                {
                    if (mi?.Mesh == null) continue;
                    string srcName = mi.HasMeta("SourceMeshName") ? mi.GetMeta("SourceMeshName").AsString() : mi.Name.ToString();
                    if (string.Equals(srcName, rawMeshName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetMi = mi;
                        surfIdx = 0; // Rigged container splits per surface into 1-surface MeshInstance3D
                        break;
                    }
                }
            }

            if (targetMi == null && _previewModelInstance != null && GodotObject.IsInstanceValid(_previewModelInstance))
            {
                foreach (var mi in CustomModelAutoRigger.CollectMeshes(_previewModelInstance))
                {
                    if (mi?.Mesh == null) continue;
                    if (string.Equals(mi.Name.ToString(), rawMeshName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetMi = mi;
                        surfIdx = 0;
                        break;
                    }
                }
            }

            if (targetMi == null)
            {
                SetBottomStatus($"Could not locate imported mesh '{displayName}' in 3D preview.", StatusTone.Warning);
                return;
            }

            int srcSurf = targetMi.HasMeta("SourceSurfaceIndex") ? targetMi.GetMeta("SourceSurfaceIndex").AsInt32() : surfIdx;
            string cutKey = CustomModelAutoRigger.MakeSurfaceKeepKey(rawMeshName, srcSurf);
            int[] existingRle = null;
            if (!_importedMeshCutMasks.TryGetValue(cutKey, out existingRle))
                _importedMeshCutMasks.TryGetValue(rawMeshName, out existingRle);

            BeginMeshCutSession(
                targetMi,
                surfIdx,
                cutKey,
                displayName,
                isOriginalHeroMesh: false,
                existingRle);
        }

        private bool TryResolvePreviewHeroSurface(string keepKey, out MeshInstance3D targetMi, out int surfIdx)
        {
            targetMi = null;
            surfIdx = 0;
            if (_previewHeroClone == null || !GodotObject.IsInstanceValid(_previewHeroClone)) return false;

            string expectedMesh = keepKey;
            int expectedSurf = -1;
            const string marker = "::surf::";
            int sep = keepKey.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (sep >= 0)
            {
                expectedMesh = keepKey[..sep];
                int.TryParse(keepKey[(sep + marker.Length)..], out expectedSurf);
            }

            foreach (var mi in CustomModelAutoRigger.CollectMeshes(_previewHeroClone))
            {
                if (mi?.Mesh == null || IsUnderActivePreviewAccessoryContainer(mi)) continue;
                string nm = mi.Name.ToString();
                if (!string.Equals(nm, expectedMesh, StringComparison.OrdinalIgnoreCase)) continue;
                int s = expectedSurf >= 0 ? expectedSurf : 0;
                if (s < mi.Mesh.GetSurfaceCount())
                {
                    targetMi = mi;
                    surfIdx = s;
                    return true;
                }
            }
            return false;
        }

        private void BeginMeshCutSession(
            MeshInstance3D targetMi,
            int surfIdx,
            string surfaceKey,
            string displayName,
            bool isOriginalHeroMesh,
            int[] existingCutRle)
        {
            // Exit any conflicting viewport edit modes cleanly
            if (_isMeshCutModeActive) ExitMeshCutMode();
            if (_btnWeightPaintMode != null && _btnWeightPaintMode.ButtonPressed)
                _btnWeightPaintMode.ButtonPressed = false;
            if (_btnDeformBrushToggle != null && _btnDeformBrushToggle.ButtonPressed)
                _btnDeformBrushToggle.ButtonPressed = false;

            _savedGizmoTargetBeforeCut = _optGizmoTarget?.Selected >= 0 ? _optGizmoTarget.GetItemId(_optGizmoTarget.Selected) : 0;
            if (_savedGizmoTargetBeforeCut != 0) SelectGizmoTargetById(0);

            var session = MeshCutEditorCore.CaptureSurface(
                targetMi,
                surfIdx,
                surfaceKey,
                displayName,
                isOriginalHeroMesh,
                _previewHeroClone,
                _previewHeroSkeleton,
                existingCutRle);

            if (session == null || session.TriCount == 0)
            {
                SetBottomStatus($"Cannot cut '{displayName}' (surface has no triangles).", StatusTone.Warning);
                return;
            }

            _activeCutSession = session;
            _isMeshCutModeActive = true;
            _meshCutHistory.Clear();
            _cutRestoreMode = false;
            // Default to Isolated so all other meshes are hidden for unobstructed cutting!
            _activeCutRefMode = MeshCutReferenceMode.IsolateTarget;
            _btnCutShowOthers?.SetPressedNoSignal(false);
            _btnCutGhostOthers?.SetPressedNoSignal(false);
            if (_btnCutGhostOthers != null) _btnCutGhostOthers.Disabled = true;

            PopulateMeshCutBonePicker();
            EnsureMeshCutOverlays3D();
            SyncMeshCutHudUi();
            ApplyMeshCutReferenceContext();
            RefreshMeshCutOverlays3D();

            if (_meshCutViewportHud != null) _meshCutViewportHud.Visible = true;
            SetBottomStatus($"Mesh Cut Mode: '{displayName}' ({session.TriCount:N0} tris). Drag to select, Delete to cut, or use Bone + Keep Only.", StatusTone.Info);
        }

        private void ExitMeshCutMode()
        {
            if (!_isMeshCutModeActive) return;

            _isMeshCutModeActive = false;
            _isCutDraggingLasso = false;
            _isCutDraggingBox = false;
            _isCutDraggingBrush = false;
            _meshCutOverlay2D?.Cancel();
            UpdateBrushRingVisual(Vector3.Zero, Vector3.Up, 0.05f, false);

            DestroyMeshCutOverlays3D();
            if (_meshCutViewportHud != null) _meshCutViewportHud.Visible = false;

            _activeCutSession = null;
            _meshCutHistory.Clear();

            // Restore preview visibilities & materials cleanly
            RestoreMeshCutReferenceContext();
            RefreshAllMeshCutCardBadges();

            if (_savedGizmoTargetBeforeCut != 0)
            {
                SelectGizmoTargetById(_savedGizmoTargetBeforeCut);
                _savedGizmoTargetBeforeCut = 0;
            }
            SetBottomStatus("Exited Mesh Cut Mode.", StatusTone.Info);
        }

        private void EnsureMeshCutOverlays3D()
        {
            DestroyMeshCutOverlays3D();
            if (_activeCutSession?.TargetMeshInstance == null || !GodotObject.IsInstanceValid(_activeCutSession.TargetMeshInstance))
                return;

            var parent = _activeCutSession.TargetMeshInstance.GetParent();
            if (parent == null) return;

            _meshCutSelectMat = MeshCutEditorCore.CreateSelectionOverlayMaterial(_cutRestoreMode);
            _meshCutGhostMat = MeshCutEditorCore.CreateCutGhostOverlayMaterial();

            _meshCutSelectedOverlay3D = new MeshInstance3D
            {
                Name = "MeshCutSelectedOverlay3D",
                Transform = _activeCutSession.TargetMeshInstance.Transform,
                Skin = _activeCutSession.Skin,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            _meshCutSelectedOverlay3D.SetMeta("IsMeshCutOverlay", true);
            _meshCutSelectedOverlay3D.Layers &= ~(uint)(1 << 20); // Clear Visual Layer 21
            parent.AddChild(_meshCutSelectedOverlay3D);
            if (_activeCutSession.Skeleton != null && GodotObject.IsInstanceValid(_activeCutSession.Skeleton))
                _meshCutSelectedOverlay3D.Skeleton = _meshCutSelectedOverlay3D.GetPathTo(_activeCutSession.Skeleton);

            _meshCutGhostOverlay3D = new MeshInstance3D
            {
                Name = "MeshCutGhostOverlay3D",
                Transform = _activeCutSession.TargetMeshInstance.Transform,
                Skin = _activeCutSession.Skin,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            _meshCutGhostOverlay3D.SetMeta("IsMeshCutOverlay", true);
            _meshCutGhostOverlay3D.Layers &= ~(uint)(1 << 20); // Clear Visual Layer 21
            parent.AddChild(_meshCutGhostOverlay3D);
            if (_activeCutSession.Skeleton != null && GodotObject.IsInstanceValid(_activeCutSession.Skeleton))
                _meshCutGhostOverlay3D.Skeleton = _meshCutGhostOverlay3D.GetPathTo(_activeCutSession.Skeleton);
        }

        private void DestroyMeshCutOverlays3D()
        {
            if (_meshCutSelectedOverlay3D != null && GodotObject.IsInstanceValid(_meshCutSelectedOverlay3D))
            {
                _meshCutSelectedOverlay3D.GetParent()?.RemoveChild(_meshCutSelectedOverlay3D);
                _meshCutSelectedOverlay3D.QueueFree();
            }
            _meshCutSelectedOverlay3D = null;

            if (_meshCutGhostOverlay3D != null && GodotObject.IsInstanceValid(_meshCutGhostOverlay3D))
            {
                _meshCutGhostOverlay3D.GetParent()?.RemoveChild(_meshCutGhostOverlay3D);
                _meshCutGhostOverlay3D.QueueFree();
            }
            _meshCutGhostOverlay3D = null;
        }

        private void RefreshMeshCutOverlays3D()
        {
            if (!IsMeshCutActive()) return;

            bool seeThroughHighlight = _cutXRayEnabled || _cutThroughEnabled;
            _meshCutSelectMat = MeshCutEditorCore.CreateSelectionOverlayMaterial(_cutRestoreMode, seeThroughHighlight);
            if (_meshCutSelectedOverlay3D != null && GodotObject.IsInstanceValid(_meshCutSelectedOverlay3D))
            {
                _meshCutSelectedOverlay3D.Visible = true;
                _meshCutSelectedOverlay3D.MaterialOverride = null;
                _meshCutSelectedOverlay3D.Transform = _activeCutSession.TargetMeshInstance.Transform;
                _meshCutSelectedOverlay3D.Mesh = MeshCutEditorCore.BuildTriangleSubsetOverlayMesh(
                    _activeCutSession,
                    _activeCutSession.SelectedTriangles,
                    _meshCutSelectMat);
            }

            if (_meshCutGhostOverlay3D != null && GodotObject.IsInstanceValid(_meshCutGhostOverlay3D))
            {
                bool showGhost = (_cutShowGhost || _cutRestoreMode) && _activeCutSession.CutTriangles.Count > 0;
                _meshCutGhostOverlay3D.Visible = showGhost;
                _meshCutGhostOverlay3D.MaterialOverride = null;
                _meshCutGhostOverlay3D.Transform = _activeCutSession.TargetMeshInstance.Transform;
                _meshCutGhostOverlay3D.Mesh = showGhost
                    ? MeshCutEditorCore.BuildTriangleSubsetOverlayMesh(_activeCutSession, _activeCutSession.CutTriangles, _meshCutGhostMat)
                    : null;
            }
        }

        private bool IsMeshCutOverlayNode(MeshInstance3D mi) =>
            mi != null && (ReferenceEquals(mi, _meshCutSelectedOverlay3D) ||
                           ReferenceEquals(mi, _meshCutGhostOverlay3D) ||
                           (mi.HasMeta("IsMeshCutOverlay") && mi.GetMeta("IsMeshCutOverlay").AsBool()));

        private void ApplyMeshCutReferenceContext()
        {
            if (!IsMeshCutActive()) return;
            var targetMi = _activeCutSession.TargetMeshInstance;

            // First restore normal hero keep/ghost state as baseline
            UpdatePreviewHeroKeepAndGhostMaterials();

            if (_previewRiggedContainer != null && GodotObject.IsInstanceValid(_previewRiggedContainer))
            {
                foreach (var mi in CustomModelAutoRigger.CollectMeshes(_previewRiggedContainer))
                {
                    if (mi == null || !GodotObject.IsInstanceValid(mi) || IsMeshCutOverlayNode(mi)) continue;
                    if (ReferenceEquals(mi, targetMi))
                    {
                        mi.Visible = true;
                        mi.MaterialOverride = _cutXRayEnabled
                            ? (_meshCutXRayTargetMat ??= MeshCutEditorCore.CreateXRayTargetMaterial())
                            : null;
                        continue;
                    }
                    if (_activeCutRefMode == MeshCutReferenceMode.IsolateTarget)
                    {
                        mi.Visible = false;
                    }
                    else if (_activeCutRefMode == MeshCutReferenceMode.GhostOthers)
                    {
                        mi.Visible = true;
                        mi.MaterialOverride = _ghostMaterial;
                    }
                    else
                    {
                        mi.Visible = true;
                        mi.MaterialOverride = null;
                    }
                }
            }

            // Also hide unrigged preview model meshes when isolated so they never obstruct cutting!
            if (_previewModelInstance != null && GodotObject.IsInstanceValid(_previewModelInstance))
            {
                foreach (var mi in CustomModelAutoRigger.CollectMeshes(_previewModelInstance))
                {
                    if (mi == null || !GodotObject.IsInstanceValid(mi) || IsMeshCutOverlayNode(mi)) continue;
                    if (ReferenceEquals(mi, targetMi))
                    {
                        mi.Visible = true;
                        mi.MaterialOverride = _cutXRayEnabled
                            ? (_meshCutXRayTargetMat ??= MeshCutEditorCore.CreateXRayTargetMaterial())
                            : null;
                        continue;
                    }
                    if (_activeCutRefMode == MeshCutReferenceMode.IsolateTarget)
                    {
                        mi.Visible = false;
                    }
                }
            }

            foreach (var mi in _previewGhostMeshes)
            {
                if (mi == null || !GodotObject.IsInstanceValid(mi) || IsMeshCutOverlayNode(mi)) continue;
                if (ReferenceEquals(mi, targetMi))
                {
                    mi.Visible = true;
                    if (_cutXRayEnabled)
                    {
                        _meshCutXRayTargetMat ??= MeshCutEditorCore.CreateXRayTargetMaterial();
                        mi.MaterialOverride = null;
                        int sc = mi.Mesh?.GetSurfaceCount() ?? 1;
                        for (int s = 0; s < sc; s++)
                        {
                            mi.SetSurfaceOverrideMaterial(s, s == _activeCutSession.SurfaceIndex
                                ? _meshCutXRayTargetMat
                                : (_hiddenSurfaceMaterial ??= CustomModelAutoRigger.CreateHiddenSurfaceMaterial()));
                        }
                    }
                    else if (mi.Mesh != null && mi.Mesh.GetSurfaceCount() > 1 && _activeCutRefMode == MeshCutReferenceMode.IsolateTarget)
                    {
                        // Hide non-target surfaces on the same multi-surface hero mesh when isolated!
                        int sc = mi.Mesh.GetSurfaceCount();
                        _hiddenSurfaceMaterial ??= CustomModelAutoRigger.CreateHiddenSurfaceMaterial();
                        for (int s = 0; s < sc; s++)
                        {
                            if (s != _activeCutSession.SurfaceIndex)
                                mi.SetSurfaceOverrideMaterial(s, _hiddenSurfaceMaterial);
                        }
                    }
                    continue;
                }
                if (_activeCutRefMode == MeshCutReferenceMode.IsolateTarget)
                {
                    mi.Visible = false;
                }
                else if (_activeCutRefMode == MeshCutReferenceMode.GhostOthers)
                {
                    mi.Visible = true;
                    mi.MaterialOverride = _ghostMaterial;
                }
            }
        }

        private void RestoreMeshCutReferenceContext()
        {
            if (_previewRiggedContainer != null && GodotObject.IsInstanceValid(_previewRiggedContainer))
            {
                foreach (var mi in CustomModelAutoRigger.CollectMeshes(_previewRiggedContainer))
                {
                    if (mi == null || !GodotObject.IsInstanceValid(mi)) continue;
                    mi.Visible = true;
                    mi.MaterialOverride = null;
                }
            }
            if (_previewModelInstance != null && GodotObject.IsInstanceValid(_previewModelInstance))
            {
                foreach (var mi in CustomModelAutoRigger.CollectMeshes(_previewModelInstance))
                {
                    if (mi == null || !GodotObject.IsInstanceValid(mi)) continue;
                    mi.MaterialOverride = null;
                }
                // Only restore preview mesh visibility — NEVER call OnImportedSubmeshesChanged() here,
                // which would re-rig or reset Reference Bones after cutting a vanilla hero or accessory mesh!
                SyncPreviewModelMeshVisibility();
            }
            UpdatePreviewHeroKeepAndGhostMaterials();
        }

        // =========================================================================
        // Tool & Action Handlers
        // =========================================================================

        private void SetActiveMeshCutTool(MeshCutTool tool)
        {
            _activeCutTool = tool;
            // Respect the user's X-Ray toggle instead of silently overriding it on tool switches!
            if (tool != MeshCutTool.Brush)
                UpdateBrushRingVisual(Vector3.Zero, Vector3.Up, 0.05f, false);

            SyncMeshCutHudUi();
        }

        private void SetMeshCutRestoreMode(bool restore)
        {
            if (_cutRestoreMode == restore)
            {
                SyncMeshCutHudUi();
                return;
            }
            _cutRestoreMode = restore;
            _activeCutSession?.SelectedTriangles.Clear();
            SyncMeshCutHudUi();
            RefreshMeshCutOverlays3D();
        }

        private void PopulateMeshCutBonePicker()
        {
            if (_optCutBonePicker == null || !IsMeshCutActive()) return;
            _optCutBonePicker.Clear();
            var weightedBones = MeshCutEditorCore.GetWeightedBonesForSurface(_activeCutSession);
            for (int i = 0; i < weightedBones.Count; i++)
            {
                var (bIdx, bName, tCnt) = weightedBones[i];
                _optCutBonePicker.AddItem($"{bName} ({tCnt:N0} tris)", bIdx);
            }
            if (_optCutBonePicker.ItemCount > 0)
            {
                _optCutBonePicker.Select(0);
                if (_btnCutSelectBoneApply != null) _btnCutSelectBoneApply.Disabled = false;
            }
            else
            {
                _optCutBonePicker.AddItem("(Unweighted / static mesh)", -1);
                _optCutBonePicker.Select(0);
                if (_btnCutSelectBoneApply != null) _btnCutSelectBoneApply.Disabled = true;
            }
        }

        private void SyncMeshCutHudUi()
        {
            if (!IsMeshCutActive()) return;
            var s = _activeCutSession;
            int kept = Math.Max(0, s.TriCount - s.CutTriangles.Count);

            if (_lblCutTargetTitle != null)
            {
                string kind = s.IsOriginalHeroMesh ? "Hero Mesh" : "Custom Mesh";
                _lblCutTargetTitle.Text = $"Cut {kind}: {s.DisplayName}";
            }
            if (_lblCutStats != null)
                _lblCutStats.Text = $"{kept:N0} / {s.TriCount:N0} kept · {s.CutTriangles.Count:N0} cut · {s.SelectedTriangles.Count:N0} sel";

            for (int i = 0; i < _cutToolButtons.Length; i++)
                _cutToolButtons[i]?.SetPressedNoSignal(i == (int)_activeCutTool);

            _btnCutModeCut?.SetPressedNoSignal(!_cutRestoreMode);
            _btnCutModeRestore?.SetPressedNoSignal(_cutRestoreMode);
            _chkCutThrough?.SetPressedNoSignal(_cutThroughEnabled);
            _chkCutXRay?.SetPressedNoSignal(_cutXRayEnabled);
            _chkCutMirrorX?.SetPressedNoSignal(_cutMirrorX);
            _chkCutShowGhost?.SetPressedNoSignal(_cutShowGhost);

            if (_boxCutBrushRow != null) _boxCutBrushRow.Visible = _activeCutTool == MeshCutTool.Brush;
            if (_boxCutBoneRow != null) _boxCutBoneRow.Visible = _activeCutTool == MeshCutTool.BoneRegion;

            bool hasSel = s.SelectedTriangles.Count > 0;
            bool hasCut = s.CutTriangles.Count > 0;
            if (_btnCutSelected != null)
            {
                _btnCutSelected.Visible = !_cutRestoreMode;
                _btnCutSelected.Disabled = !hasSel;
            }
            if (_btnCutKeepOnly != null)
            {
                _btnCutKeepOnly.Visible = !_cutRestoreMode;
                _btnCutKeepOnly.Disabled = !hasSel;
            }
            if (_btnCutRestoreSelected != null)
            {
                _btnCutRestoreSelected.Visible = _cutRestoreMode;
                _btnCutRestoreSelected.Disabled = !hasSel;
            }
            if (_btnCutGrow != null) _btnCutGrow.Disabled = !hasSel;
            if (_btnCutShrink != null) _btnCutShrink.Disabled = !hasSel;
            if (_btnCutClearSel != null) _btnCutClearSel.Disabled = !hasSel;
            if (_btnCutResetMesh != null) _btnCutResetMesh.Disabled = !hasCut;

            if (_btnCutUndo != null)
            {
                _btnCutUndo.Disabled = !_meshCutHistory.CanUndo;
                _btnCutUndo.TooltipText = _meshCutHistory.CanUndo
                    ? $"Undo: {_meshCutHistory.NextUndoLabel} (Ctrl+Z)"
                    : "Nothing to undo";
            }
            if (_btnCutRedo != null)
            {
                _btnCutRedo.Disabled = !_meshCutHistory.CanRedo;
                _btnCutRedo.TooltipText = _meshCutHistory.CanRedo
                    ? $"Redo: {_meshCutHistory.NextRedoLabel} (Ctrl+Y)"
                    : "Nothing to redo";
            }
        }

        private void CommitActiveCutMaskToPreview()
        {
            if (!IsMeshCutActive()) return;
            var s = _activeCutSession;
            var targetDict = s.IsOriginalHeroMesh ? _originalMeshCutMasks : _importedMeshCutMasks;

            if (s.CutTriangles.Count > 0)
                targetDict[s.SurfaceKey] = MeshCutEditorCore.EncodeCutMask(s.CutTriangles);
            else
                targetDict.Remove(s.SurfaceKey);

            if (s.IsOriginalHeroMesh)
            {
                ApplyPreviewHeroDeformation();
            }
            else
            {
                if (_previewRiggedContainer != null && GodotObject.IsInstanceValid(_previewRiggedContainer))
                    CustomModelAutoRigger.ApplyImportedMeshCutMasks(CustomModelAutoRigger.CollectMeshes(_previewRiggedContainer), _importedMeshCutMasks);
                if (_previewModelInstance != null && GodotObject.IsInstanceValid(_previewModelInstance))
                    CustomModelAutoRigger.ApplyImportedMeshCutMasks(CustomModelAutoRigger.CollectMeshes(_previewModelInstance), _importedMeshCutMasks);
            }

            ApplyMeshCutReferenceContext();
            RefreshMeshCutOverlays3D();
            SyncMeshCutHudUi();
            RefreshAllMeshCutCardBadges();
        }

        private void OnMeshCutSelectChosenBonePressed()
        {
            if (!IsMeshCutActive() || _optCutBonePicker == null || _optCutBonePicker.Selected < 0) return;
            int boneIdx = _optCutBonePicker.GetItemId(_optCutBonePicker.Selected);
            if (boneIdx < 0) return;

            var op = Input.IsKeyPressed(Key.Alt)
                ? MeshCutSelectOp.Subtract
                : (Input.IsKeyPressed(Key.Shift) ? MeshCutSelectOp.Add : MeshCutSelectOp.Replace);

            _meshCutHistory.RecordBeforeMutation("Bone Select", _activeCutSession.CutTriangles, _activeCutSession.SelectedTriangles);
            if (MeshCutEditorCore.SelectByBoneRegion(_activeCutSession, boneIdx, _cutBoneIncludeChildren, _cutMirrorX, op, _cutRestoreMode))
            {
                RefreshMeshCutOverlays3D();
                SyncMeshCutHudUi();
            }
        }

        private void OnMeshCutGrowPressed()
        {
            if (!IsMeshCutActive() || _activeCutSession.SelectedTriangles.Count == 0) return;
            _meshCutHistory.RecordBeforeMutation("Grow Selection", _activeCutSession.CutTriangles, _activeCutSession.SelectedTriangles);
            if (MeshCutEditorCore.GrowSelection(_activeCutSession, _cutRestoreMode))
            {
                RefreshMeshCutOverlays3D();
                SyncMeshCutHudUi();
            }
        }

        private void OnMeshCutShrinkPressed()
        {
            if (!IsMeshCutActive() || _activeCutSession.SelectedTriangles.Count == 0) return;
            _meshCutHistory.RecordBeforeMutation("Shrink Selection", _activeCutSession.CutTriangles, _activeCutSession.SelectedTriangles);
            if (MeshCutEditorCore.ShrinkSelection(_activeCutSession, _cutRestoreMode))
            {
                RefreshMeshCutOverlays3D();
                SyncMeshCutHudUi();
            }
        }

        private void OnMeshCutInvertPressed()
        {
            if (!IsMeshCutActive()) return;
            _meshCutHistory.RecordBeforeMutation("Invert Selection", _activeCutSession.CutTriangles, _activeCutSession.SelectedTriangles);
            if (MeshCutEditorCore.InvertSelection(_activeCutSession, _cutRestoreMode))
            {
                RefreshMeshCutOverlays3D();
                SyncMeshCutHudUi();
            }
        }

        private void OnMeshCutClearSelectionPressed()
        {
            if (!IsMeshCutActive() || _activeCutSession.SelectedTriangles.Count == 0) return;
            _meshCutHistory.RecordBeforeMutation("Clear Selection", _activeCutSession.CutTriangles, _activeCutSession.SelectedTriangles);
            _activeCutSession.SelectedTriangles.Clear();
            RefreshMeshCutOverlays3D();
            SyncMeshCutHudUi();
        }

        private void OnMeshCutDeleteSelectedPressed()
        {
            if (!IsMeshCutActive() || _activeCutSession.SelectedTriangles.Count == 0) return;
            int cnt = _activeCutSession.SelectedTriangles.Count;
            _meshCutHistory.RecordBeforeMutation($"Cut {cnt:N0} tris", _activeCutSession.CutTriangles, _activeCutSession.SelectedTriangles);
            int removed = MeshCutEditorCore.CutSelected(_activeCutSession);
            if (removed > 0)
            {
                CommitActiveCutMaskToPreview();
                SetBottomStatus($"Cut {removed:N0} triangle(s) from '{_activeCutSession.DisplayName}'.", StatusTone.Info);
            }
        }

        private void OnMeshCutKeepOnlySelectedPressed()
        {
            if (!IsMeshCutActive() || _activeCutSession.SelectedTriangles.Count == 0) return;
            int keepCnt = _activeCutSession.SelectedTriangles.Count;
            _meshCutHistory.RecordBeforeMutation($"Keep Only {keepCnt:N0} tris", _activeCutSession.CutTriangles, _activeCutSession.SelectedTriangles);
            int cut = MeshCutEditorCore.KeepOnlySelected(_activeCutSession);
            CommitActiveCutMaskToPreview();
            SetBottomStatus($"Isolated {keepCnt:N0} triangle(s) on '{_activeCutSession.DisplayName}' (cut {cut:N0}).", StatusTone.Success);
        }

        private void OnMeshCutRestoreSelectedPressed()
        {
            if (!IsMeshCutActive() || _activeCutSession.SelectedTriangles.Count == 0) return;
            int cnt = _activeCutSession.SelectedTriangles.Count;
            _meshCutHistory.RecordBeforeMutation($"Restore {cnt:N0} tris", _activeCutSession.CutTriangles, _activeCutSession.SelectedTriangles);
            int restored = MeshCutEditorCore.RestoreSelected(_activeCutSession);
            if (restored > 0)
            {
                CommitActiveCutMaskToPreview();
                SetBottomStatus($"Restored {restored:N0} triangle(s) on '{_activeCutSession.DisplayName}'.", StatusTone.Info);
            }
        }

        private void OnMeshCutResetPressed()
        {
            if (!IsMeshCutActive() || _activeCutSession.CutTriangles.Count == 0) return;
            _meshCutHistory.RecordBeforeMutation("Reset Mesh", _activeCutSession.CutTriangles, _activeCutSession.SelectedTriangles);
            int restored = MeshCutEditorCore.RestoreAll(_activeCutSession);
            if (restored > 0)
            {
                CommitActiveCutMaskToPreview();
                SetBottomStatus($"Restored all {restored:N0} cut triangle(s) on '{_activeCutSession.DisplayName}'.", StatusTone.Info);
            }
        }

        private void OnMeshCutUndoPressed()
        {
            if (!IsMeshCutActive()) return;
            if (_meshCutHistory.TryUndo(_activeCutSession.CutTriangles, _activeCutSession.SelectedTriangles, _activeCutSession.TriCount, out string label))
            {
                CommitActiveCutMaskToPreview();
                SetBottomStatus($"Undid '{label}'.", StatusTone.Info);
            }
        }

        private void OnMeshCutRedoPressed()
        {
            if (!IsMeshCutActive()) return;
            if (_meshCutHistory.TryRedo(_activeCutSession.CutTriangles, _activeCutSession.SelectedTriangles, _activeCutSession.TriCount, out string label))
            {
                CommitActiveCutMaskToPreview();
                SetBottomStatus($"Redid '{label}'.", StatusTone.Info);
            }
        }

        // =========================================================================
        // 3D Viewport Mouse & Keyboard Routing
        // =========================================================================

        private static MeshCutSelectOp ResolveSelectOpFromModifiers(InputEventWithModifiers mod, bool defaultAdd)
        {
            if (mod.AltPressed) return MeshCutSelectOp.Subtract;
            if (mod.ShiftPressed) return MeshCutSelectOp.Add;
            return defaultAdd ? MeshCutSelectOp.Add : MeshCutSelectOp.Replace;
        }

        private bool HandleMeshCutInput(InputEvent @event)
        {
            if (!IsMeshCutActive() || _previewCamera == null) return false;

            // Allow Right/Middle drag to orbit/pan and Wheel to zoom seamlessly while in Cut Mode!
            if (@event is InputEventMouseButton mbOrbit)
            {
                if (mbOrbit.ButtonIndex == MouseButton.Right)
                {
                    if (mbOrbit.Pressed)
                    {
                        if (mbOrbit.ShiftPressed) _isPanning = true;
                        else _isOrbiting = true;
                        _lastMousePos = mbOrbit.Position;
                    }
                    else
                    {
                        _isOrbiting = false;
                        _isPanning = false;
                    }
                    GetViewport().SetInputAsHandled();
                    return true;
                }
                if (mbOrbit.ButtonIndex == MouseButton.Middle ||
                    mbOrbit.ButtonIndex == MouseButton.WheelUp ||
                    mbOrbit.ButtonIndex == MouseButton.WheelDown)
                {
                    return false; // Let standard viewport camera zoom/orbit handle MMB & wheel
                }
            }

            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    _isOrbiting = false;
                    _isPanning = false;
                    MeshCutEditorCore.UpdatePosedGeometry(_activeCutSession);
                    _cutDragOp = ResolveSelectOpFromModifiers(mb, defaultAdd: _activeCutTool == MeshCutTool.Brush);

                    switch (_activeCutTool)
                    {
                        case MeshCutTool.Lasso:
                            _isCutDraggingLasso = true;
                            _meshCutOverlay2D?.BeginLasso(mb.Position, _cutDragOp, _cutRestoreMode);
                            GetViewport().SetInputAsHandled();
                            return true;

                        case MeshCutTool.Box:
                            _isCutDraggingBox = true;
                            _meshCutOverlay2D?.BeginBox(mb.Position, _cutDragOp, _cutRestoreMode);
                            GetViewport().SetInputAsHandled();
                            return true;

                        case MeshCutTool.Brush:
                        {
                            Vector3 orig = _previewCamera.ProjectRayOrigin(mb.Position);
                            Vector3 dir = _previewCamera.ProjectRayNormal(mb.Position);
                            if (MeshCutEditorCore.RaycastTriangle(_activeCutSession, orig, dir, _cutRestoreMode, out _, out var hitPt, out var hitNorm))
                            {
                                _meshCutHistory.RecordBeforeMutation("Brush Select", _activeCutSession.CutTriangles, _activeCutSession.SelectedTriangles);
                                _isCutDraggingBrush = true;
                                _hasLastCutBrushDab = true;
                                _lastCutBrushWorld = hitPt;
                                _cutBrushMutatedSelection = MeshCutEditorCore.SelectByBrush(
                                    _activeCutSession, _previewCamera, hitPt, orig, dir, _cutBrushRadius,
                                    _cutDragOp, _cutThroughEnabled, _cutMirrorX, _cutRestoreMode);
                                UpdateBrushRingVisual(hitPt, hitNorm, _cutBrushRadius, true);
                                if (_cutBrushMutatedSelection)
                                {
                                    RefreshMeshCutOverlays3D();
                                    SyncMeshCutHudUi();
                                }
                            }
                            GetViewport().SetInputAsHandled();
                            return true;
                        }

                        case MeshCutTool.Island:
                        {
                            Vector3 orig = _previewCamera.ProjectRayOrigin(mb.Position);
                            Vector3 dir = _previewCamera.ProjectRayNormal(mb.Position);
                            if (MeshCutEditorCore.RaycastTriangle(_activeCutSession, orig, dir, _cutRestoreMode, out int hitTri, out _, out _))
                            {
                                _meshCutHistory.RecordBeforeMutation("Island Select", _activeCutSession.CutTriangles, _activeCutSession.SelectedTriangles);
                                if (MeshCutEditorCore.SelectByIsland(_activeCutSession, hitTri, _cutDragOp, _cutMirrorX, _cutRestoreMode))
                                {
                                    RefreshMeshCutOverlays3D();
                                    SyncMeshCutHudUi();
                                }
                            }
                            GetViewport().SetInputAsHandled();
                            return true;
                        }

                        case MeshCutTool.BoneRegion:
                        {
                            Vector3 orig = _previewCamera.ProjectRayOrigin(mb.Position);
                            Vector3 dir = _previewCamera.ProjectRayNormal(mb.Position);
                            if (MeshCutEditorCore.RaycastTriangle(_activeCutSession, orig, dir, _cutRestoreMode, out int hitTri, out _, out _))
                            {
                                int boneIdx = _activeCutSession.TriDominantBone[hitTri];
                                if (boneIdx >= 0)
                                {
                                    // Sync bone picker dropdown to clicked bone
                                    if (_optCutBonePicker != null)
                                    {
                                        for (int i = 0; i < _optCutBonePicker.ItemCount; i++)
                                        {
                                            if (_optCutBonePicker.GetItemId(i) == boneIdx)
                                            {
                                                _optCutBonePicker.Select(i);
                                                break;
                                            }
                                        }
                                    }
                                    string bName = _activeCutSession.Skeleton?.GetBoneName(boneIdx) ?? $"Bone {boneIdx}";
                                    _meshCutHistory.RecordBeforeMutation($"Bone Select ({bName})", _activeCutSession.CutTriangles, _activeCutSession.SelectedTriangles);
                                    if (MeshCutEditorCore.SelectByBoneRegion(_activeCutSession, boneIdx, _cutBoneIncludeChildren, _cutMirrorX, _cutDragOp, _cutRestoreMode))
                                    {
                                        RefreshMeshCutOverlays3D();
                                        SyncMeshCutHudUi();
                                        SetBottomStatus($"Selected bone region '{bName}'{(_cutBoneIncludeChildren ? " + children" : "")}{(_cutMirrorX ? " (mirrored)" : "")}.", StatusTone.Info);
                                    }
                                }
                            }
                            GetViewport().SetInputAsHandled();
                            return true;
                        }
                    }
                }
                else
                {
                    if (_isCutDraggingLasso)
                    {
                        _isCutDraggingLasso = false;
                        var poly = _meshCutOverlay2D?.EndLasso();
                        if (poly != null && poly.Length >= 3)
                        {
                            _meshCutHistory.RecordBeforeMutation("Lasso Select", _activeCutSession.CutTriangles, _activeCutSession.SelectedTriangles);
                            if (MeshCutEditorCore.SelectByLasso(_activeCutSession, _previewCamera, poly, _cutDragOp, _cutThroughEnabled, _cutMirrorX, _cutRestoreMode))
                            {
                                RefreshMeshCutOverlays3D();
                                SyncMeshCutHudUi();
                            }
                        }
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
                    if (_isCutDraggingBox)
                    {
                        _isCutDraggingBox = false;
                        Rect2 rect = _meshCutOverlay2D?.EndBox() ?? new Rect2();
                        if (rect.Size.X >= 3f || rect.Size.Y >= 3f)
                        {
                            _meshCutHistory.RecordBeforeMutation("Box Select", _activeCutSession.CutTriangles, _activeCutSession.SelectedTriangles);
                            if (MeshCutEditorCore.SelectByBox(_activeCutSession, _previewCamera, rect, _cutDragOp, _cutThroughEnabled, _cutMirrorX, _cutRestoreMode))
                            {
                                RefreshMeshCutOverlays3D();
                                SyncMeshCutHudUi();
                            }
                        }
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
                    if (_isCutDraggingBrush)
                    {
                        _isCutDraggingBrush = false;
                        _hasLastCutBrushDab = false;
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
                }
            }
            else if (@event is InputEventMouseMotion mm)
            {
                if (_isOrbiting || _isPanning) return false;

                if (_isCutDraggingLasso)
                {
                    _meshCutOverlay2D?.AppendLassoPoint(mm.Position);
                    GetViewport().SetInputAsHandled();
                    return true;
                }
                if (_isCutDraggingBox)
                {
                    _meshCutOverlay2D?.UpdateBox(mm.Position);
                    GetViewport().SetInputAsHandled();
                    return true;
                }
                if (_activeCutTool == MeshCutTool.Brush)
                {
                    MeshCutEditorCore.UpdatePosedGeometry(_activeCutSession);
                    Vector3 orig = _previewCamera.ProjectRayOrigin(mm.Position);
                    Vector3 dir = _previewCamera.ProjectRayNormal(mm.Position);
                    if (MeshCutEditorCore.RaycastTriangle(_activeCutSession, orig, dir, _cutRestoreMode, out _, out var hitPt, out var hitNorm))
                    {
                        UpdateBrushRingVisual(hitPt, hitNorm, _cutBrushRadius, true);
                        if (_isCutDraggingBrush)
                        {
                            float step = MathF.Max(0.004f, _cutBrushRadius * 0.18f);
                            if (!_hasLastCutBrushDab || hitPt.DistanceSquaredTo(_lastCutBrushWorld) >= step * step)
                            {
                                _lastCutBrushWorld = hitPt;
                                _hasLastCutBrushDab = true;
                                if (MeshCutEditorCore.SelectByBrush(
                                    _activeCutSession, _previewCamera, hitPt, orig, dir, _cutBrushRadius,
                                    _cutDragOp, _cutThroughEnabled, _cutMirrorX, _cutRestoreMode))
                                {
                                    RefreshMeshCutOverlays3D();
                                    SyncMeshCutHudUi();
                                }
                            }
                            GetViewport().SetInputAsHandled();
                            return true;
                        }
                    }
                    else
                    {
                        UpdateBrushRingVisual(Vector3.Zero, Vector3.Up, 0.05f, false);
                    }
                }
            }

            return false;
        }

        private bool HandleMeshCutKeyShortcut(InputEventKey key)
        {
            if (!IsMeshCutActive()) return false;

            if (key.CtrlPressed && key.Keycode == Key.Z)
            {
                if (key.ShiftPressed) OnMeshCutRedoPressed();
                else OnMeshCutUndoPressed();
                return true;
            }
            if (key.CtrlPressed && key.Keycode == Key.Y)
            {
                OnMeshCutRedoPressed();
                return true;
            }
            if (key.CtrlPressed && key.Keycode == Key.I)
            {
                OnMeshCutInvertPressed();
                return true;
            }
            if (key.CtrlPressed && key.Keycode == Key.D)
            {
                OnMeshCutClearSelectionPressed();
                return true;
            }
            if (key.Keycode == Key.Delete || (!key.CtrlPressed && !key.ShiftPressed && key.Keycode == Key.X))
            {
                if (_cutRestoreMode) OnMeshCutRestoreSelectedPressed();
                else OnMeshCutDeleteSelectedPressed();
                return true;
            }
            if (!key.CtrlPressed && key.ShiftPressed && key.Keycode == Key.X && !_cutRestoreMode)
            {
                OnMeshCutKeepOnlySelectedPressed();
                return true;
            }
            if (key.Keycode == Key.Escape)
            {
                if (_isCutDraggingLasso || _isCutDraggingBox)
                {
                    _isCutDraggingLasso = false;
                    _isCutDraggingBox = false;
                    _meshCutOverlay2D?.Cancel();
                }
                else if (_activeCutSession.SelectedTriangles.Count > 0)
                {
                    OnMeshCutClearSelectionPressed();
                }
                else
                {
                    ExitMeshCutMode();
                }
                return true;
            }
            if (!key.CtrlPressed && !key.AltPressed)
            {
                switch (key.Keycode)
                {
                    case Key.L: SetActiveMeshCutTool(MeshCutTool.Lasso); return true;
                    case Key.B: SetActiveMeshCutTool(MeshCutTool.Box); return true;
                    case Key.P: SetActiveMeshCutTool(MeshCutTool.Brush); return true;
                    case Key.I: SetActiveMeshCutTool(MeshCutTool.Island); return true;
                    case Key.K: SetActiveMeshCutTool(MeshCutTool.BoneRegion); return true;
                    case Key.M:
                        _cutMirrorX = !_cutMirrorX;
                        SyncMeshCutHudUi();
                        return true;
                    case Key.T:
                        _cutThroughEnabled = !_cutThroughEnabled;
                        RefreshMeshCutOverlays3D();
                        SyncMeshCutHudUi();
                        return true;
                    case Key.Z:
                        _cutXRayEnabled = !_cutXRayEnabled;
                        ApplyMeshCutReferenceContext();
                        RefreshMeshCutOverlays3D();
                        SyncMeshCutHudUi();
                        return true;
                    case Key.Equal:
                    case Key.KpAdd:
                        OnMeshCutGrowPressed();
                        return true;
                    case Key.Minus:
                    case Key.KpSubtract:
                        OnMeshCutShrinkPressed();
                        return true;
                    case Key.Bracketleft:
                        if (_sliderCutBrushRadius != null)
                            _sliderCutBrushRadius.Value = Math.Max(_sliderCutBrushRadius.MinValue, _sliderCutBrushRadius.Value - 0.005);
                        return true;
                    case Key.Bracketright:
                        if (_sliderCutBrushRadius != null)
                            _sliderCutBrushRadius.Value = Math.Min(_sliderCutBrushRadius.MaxValue, _sliderCutBrushRadius.Value + 0.005);
                        return true;
                }
            }
            return false;
        }

        // =========================================================================
        // Card Badge Synchronization
        // =========================================================================

        private void RefreshAllMeshCutCardBadges()
        {
            // Original hero keep cards
            foreach (var (chk, card) in _heroKeepCards)
            {
                if (!GodotObject.IsInstanceValid(chk) || !GodotObject.IsInstanceValid(card) || !chk.HasMeta("SubmeshName"))
                    continue;
                string keepKey = chk.GetMeta("SubmeshName").AsString();
                int cutCount = 0;
                int totalTris = 0;
                if (TryResolvePreviewHeroSurface(keepKey, out var mi, out int surfIdx))
                {
                    Mesh pristine = mi.HasMeta("OriginalPreDeformMesh")
                        ? (mi.GetMeta("OriginalPreDeformMesh").As<Mesh>() ?? mi.Mesh)
                        : mi.Mesh;
                    totalTris = GetSurfaceTriangleCount(pristine, surfIdx);
                    string canonicalKey = CustomModelAutoRigger.MakeOriginalSurfaceDeformKey(mi, surfIdx);
                    if (_originalMeshCutMasks.TryGetValue(canonicalKey, out var rle) ||
                        _originalMeshCutMasks.TryGetValue(keepKey, out rle))
                    {
                        cutCount = MeshCutEditorCore.CountCutTriangles(rle);
                    }
                }
                card.SetCutBadge(cutCount, totalTris);
            }

            // Imported / donor mesh cards
            foreach (var (chk, card) in _importedSubmeshCards)
            {
                if (!GodotObject.IsInstanceValid(chk) || !GodotObject.IsInstanceValid(card) || !chk.HasMeta("SubmeshName"))
                    continue;
                string rawName = chk.GetMeta("SubmeshName").AsString();
                string surfKey = CustomModelAutoRigger.MakeSurfaceKeepKey(rawName, 0);
                int cutCount = 0;
                if (_importedMeshCutMasks.TryGetValue(surfKey, out var rle) ||
                    _importedMeshCutMasks.TryGetValue(rawName, out rle))
                {
                    cutCount = MeshCutEditorCore.CountCutTriangles(rle);
                }
                int totalTris = 0;
                if (cutCount > 0 && _previewModelInstance != null && GodotObject.IsInstanceValid(_previewModelInstance))
                {
                    foreach (var mi in CustomModelAutoRigger.CollectMeshes(_previewModelInstance))
                    {
                        if (mi?.Mesh != null && string.Equals(mi.Name.ToString(), rawName, StringComparison.OrdinalIgnoreCase))
                        {
                            Mesh pristine = mi.HasMeta("OriginalPreCutMesh")
                                ? (mi.GetMeta("OriginalPreCutMesh").As<Mesh>() ?? mi.Mesh)
                                : mi.Mesh;
                            totalTris = GetSurfaceTriangleCount(pristine, 0);
                            break;
                        }
                    }
                }
                card.SetCutBadge(cutCount, Math.Max(totalTris, cutCount));
            }
        }

        private static int GetSurfaceTriangleCount(Mesh mesh, int surfIdx)
        {
            if (mesh == null || surfIdx < 0 || surfIdx >= mesh.GetSurfaceCount()) return 0;
            var arrays = mesh.SurfaceGetArrays(surfIdx);
            if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.Vertex) return 0;
            if (arrays.Count > (int)Mesh.ArrayType.Index && arrays[(int)Mesh.ArrayType.Index].VariantType != Variant.Type.Nil)
            {
                var idxs = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                if (idxs != null && idxs.Length >= 3) return idxs.Length / 3;
            }
            var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            return verts != null ? verts.Length / 3 : 0;
        }
    }
}

