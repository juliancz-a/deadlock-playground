using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Gizmo3DPlugin;
using DeadlockPlayground.Importer;
using DeadlockPlayground.Animation;
using DeadlockPlayground.Tools;
using DeadlockPlayground.Theming;

namespace DeadlockPlayground.UI
{
    public partial class CustomModelImporterModalUI
    {
        private Control BuildWeightPaintPanel()
        {
            var (card, box) = CreateInspectorCard(
                "Bone Weight Editor",
                "Refine hands, shoulders, and joint creases with an interactive 3D weight brush.");
            card.Visible = false;

            _btnWeightPaintMode = new Button
            {
                Text = "Paint Bone Weights (Heatmap)",
                ToggleMode = true,
                ButtonPressed = false,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Switch viewport into interactive 3D weight paint mode with a heat-map overlay (Works in Bind Pose or any Test Animation)."
            };
            _btnWeightPaintMode.Toggled += t => OnWeightPaintModeToggled(t);
            box.AddChild(_btnWeightPaintMode);

            _boxWeightPaintControls = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            _boxWeightPaintControls.AddThemeConstantOverride("separation", 7);
            box.AddChild(_boxWeightPaintControls);

            var legendLbl = new Label
            {
                Text = "Heatmap: 0.0 Blue • 0.25 Cyan • 0.5 Green • 0.75 Yellow • 1.0 Red",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            ImporterStyle.ApplyFontSize(legendLbl, ImporterStyle.FontHint);
            _accentLabels.Add(legendLbl);
            _boxWeightPaintControls.AddChild(legendLbl);

            var paintHint = new Label
            {
                Text = "LMB Paint • Alt/Ctrl+Click Pick Bone • RMB/MMB Orbit • [ / ] Radius • Ctrl+Z/Y",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            ImporterStyle.ApplyHintStyle(paintHint);
            _secondaryLabels.Add(paintHint);
            _boxWeightPaintControls.AddChild(paintHint);

            _chkPaintWireframe = new CheckBox
            {
                Text = "Show Wireframe Overlay",
                ButtonPressed = true,
                TooltipText = "Draws crisp Blender-style topology edges over the heat-map while painting."
            };
            _chkPaintWireframe.Toggled += _ =>
            {
                RefreshViewportShadingAndWireframes();
                RefreshWeightPaintHeatmap();
            };
            _boxWeightPaintControls.AddChild(_chkPaintWireframe);

            // Backing state controls (kept hidden so the floating Viewport Weight-Paint HUD
            // owns brush/radius/strength/mirror/undo/redo without duplicating controls in the right tab).
            var hiddenBacking = new Control { Visible = false };
            _boxWeightPaintControls.AddChild(hiddenBacking);

            _optPaintBone = new OptionButton();
            ConfigureOptionButton(_optPaintBone);
            _optPaintBone.ItemSelected += _ =>
            {
                UpdateResetBoneButtonLabel();
                RefreshWeightPaintHeatmap();
                SyncWeightPaintHudFromControls();
            };
            hiddenBacking.AddChild(_optPaintBone);

            _optPaintMode = new OptionButton();
            ConfigureOptionButton(_optPaintMode);
            _optPaintMode.AddItem("Add (+)", (int)CustomWeightPaintEditor.BrushMode.Add);
            _optPaintMode.AddItem("Subtract (-)", (int)CustomWeightPaintEditor.BrushMode.Subtract);
            _optPaintMode.AddItem("Smooth", (int)CustomWeightPaintEditor.BrushMode.Smooth);
            _optPaintMode.AddItem("Set 1.0 (Solid)", (int)CustomWeightPaintEditor.BrushMode.SetOne);
            _optPaintMode.Select(0);
            hiddenBacking.AddChild(_optPaintMode);

            _lblPaintRadiusVal = new Label { Text = "0.08m" };
            hiddenBacking.AddChild(_lblPaintRadiusVal);
            _sliderPaintRadius = new HSlider { MinValue = 0.02, MaxValue = 0.60, Step = 0.01, Value = 0.08 };
            _sliderPaintRadius.ValueChanged += v =>
            {
                _lblPaintRadiusVal.Text = $"{v:0.00}m";
                if (_hudSliderRadius != null) _hudSliderRadius.SetValueNoSignal(v);
                if (_hudLblRadius != null) _hudLblRadius.Text = $"{v:F2}";
            };
            hiddenBacking.AddChild(_sliderPaintRadius);

            _lblPaintStrengthVal = new Label { Text = "0.35" };
            hiddenBacking.AddChild(_lblPaintStrengthVal);
            _sliderPaintStrength = new HSlider { MinValue = 0.05, MaxValue = 1.0, Step = 0.05, Value = 0.35 };
            _sliderPaintStrength.ValueChanged += v =>
            {
                _lblPaintStrengthVal.Text = $"{v:0.00}";
                if (_hudSliderStrength != null) _hudSliderStrength.SetValueNoSignal(v);
                if (_hudLblStrength != null) _hudLblStrength.Text = $"{v:F2}";
            };
            hiddenBacking.AddChild(_sliderPaintStrength);

            _chkPaintMirror = new CheckBox { Text = "Mirror L <-> R Brush", ButtonPressed = false };
            _chkPaintMirror.Toggled += _ =>
            {
                UpdateResetBoneButtonLabel();
                RefreshWeightPaintHeatmap();
            };
            hiddenBacking.AddChild(_chkPaintMirror);

            _btnPaintUndo = new Button { Disabled = true };
            hiddenBacking.AddChild(_btnPaintUndo);
            _btnPaintRedo = new Button { Disabled = true };
            hiddenBacking.AddChild(_btnPaintRedo);
            _lblPaintHistoryStatus = new Label { Text = "Undo: 0 | Redo: 0" };
            hiddenBacking.AddChild(_lblPaintHistoryStatus);

            // Searchable & filtered bone picker (guarantees every weighted bone on the mesh appears!)
            var boneFilterRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            boneFilterRow.AddThemeConstantOverride("separation", 4);
            string[] fLabels = { "Weighted", "Physics", "All" };
            for (int fi = 0; fi < 3; fi++)
            {
                int capFi = fi;
                var fb = new Button
                {
                    Text = fLabels[fi],
                    ToggleMode = true,
                    ButtonPressed = fi == 0,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    CustomMinimumSize = new Vector2(0, 24),
                    ThemeTypeVariation = "ToolbarButton"
                };
                fb.Pressed += () =>
                {
                    _paintBoneFilterMode = capFi;
                    for (int k = 0; k < 3; k++) _paintBoneFilterBtns[k]?.SetPressedNoSignal(k == capFi);
                    RebuildPaintBoneListUI();
                };
                _paintBoneFilterBtns[fi] = fb;
                boneFilterRow.AddChild(fb);
            }
            _boxWeightPaintControls.AddChild(boneFilterRow);

            _txtPaintBoneSearch = new LineEdit
            {
                PlaceholderText = "Search bone (or Alt+Click mesh)...",
                ClearButtonEnabled = true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            _txtPaintBoneSearch.TextChanged += _ => RebuildPaintBoneListUI();
            _boxWeightPaintControls.AddChild(_txtPaintBoneSearch);

            var boneListScroll = new ScrollContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 280),
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };
            _boxPaintBoneList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _boxPaintBoneList.AddThemeConstantOverride("separation", 2);
            boneListScroll.AddChild(_boxPaintBoneList);
            _boxWeightPaintControls.AddChild(boneListScroll);

            _lblPaintStatus = new Label { Text = "Using procedural solver weights (no manual brush edits).", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            ImporterStyle.ApplyHintStyle(_lblPaintStatus);
            _secondaryLabels.Add(_lblPaintStatus);
            _boxWeightPaintControls.AddChild(_lblPaintStatus);

            var toolsRow = new HBoxContainer();
            toolsRow.AddThemeConstantOverride("separation", 6);
            var btnSmoothBoundary = new Button
            {
                Text = "Smooth Seam",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Softens the selected bone's weight falloff edge without disturbing interior weights (Ctrl+Z to undo)."
            };
            btnSmoothBoundary.Pressed += OnSmoothBoundaryWeightsPressed;
            toolsRow.AddChild(btnSmoothBoundary);

            var btnMirrorL2R = new Button
            {
                Text = "Mirror L -> R",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Mirrors all painted/auto weights from the Left side to the Right side across center X."
            };
            btnMirrorL2R.Pressed += () => OnMirrorSideWeightsPressed(leftToRight: true);
            toolsRow.AddChild(btnMirrorL2R);

            var btnMirrorR2L = new Button
            {
                Text = "Mirror R -> L",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Mirrors all painted/auto weights from the Right side to the Left side across center X."
            };
            btnMirrorR2L.Pressed += () => OnMirrorSideWeightsPressed(leftToRight: false);
            toolsRow.AddChild(btnMirrorR2L);
            _boxWeightPaintControls.AddChild(toolsRow);

            var resetRow = new HBoxContainer();
            resetRow.AddThemeConstantOverride("separation", 6);

            _btnResetBoneWeights = new Button
            {
                Text = "Reset Bone",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Resets ONLY the currently selected bone (plus its L/R mirror if enabled) back to procedural weights while keeping edits on other bones."
            };
            _btnResetBoneWeights.Pressed += OnResetSelectedBoneWeightsPressed;
            resetRow.AddChild(_btnResetBoneWeights);

            var btnResetWeights = new Button
            {
                Text = "Reset All",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Resets all surfaces back to clean procedural solver weights."
            };
            btnResetWeights.Pressed += OnResetAllWeightsPressed;
            resetRow.AddChild(btnResetWeights);
            _boxWeightPaintControls.AddChild(resetRow);

            return card;
        }

        private void UpdatePaintOverrideLabel()
        {
            if (_lblPaintStatus == null) return;
            _lblPaintStatus.Text = _customSkinOverrides.Count > 0
                ? $"Custom weight edits active on {_customSkinOverrides.Count} surface(s)."
                : "Using procedural solver weights (no manual brush edits).";
        }

        private void SetPreviewPhysicsPausedForWeightPaint(bool paused)
        {
            // CustomJiggleBoneDriver is attached to _previewHeroClone (heroRoot), not _previewHeroSkeleton;
            // scan both _previewHeroClone and _previewHeroSkeleton recursively so it is always found.
            void PauseDriversInSubtree(Node root)
            {
                if (root == null || !GodotObject.IsInstanceValid(root)) return;
                if (root is DeadlockPlayground.Animation.CustomJiggleBoneDriver driver)
                {
                    driver.SetPausedForEditing(paused);
                }
                foreach (Node ch in root.GetChildren())
                {
                    PauseDriversInSubtree(ch);
                }
            }

            PauseDriversInSubtree(_previewHeroClone);
            PauseDriversInSubtree(_previewHeroSkeleton);

            if (_previewConstraintDriver != null && GodotObject.IsInstanceValid(_previewConstraintDriver))
            {
                _previewConstraintDriver.SetProcess(!paused);
            }
        }

        private void OnWeightPaintModeToggled(bool enabled)
        {
            if (enabled && IsMeshCutActive())
            {
                ExitMeshCutMode();
            }
            if (_btnLandmarkStepPins != null) _btnLandmarkStepPins.SetPressedNoSignal(!enabled);
            if (_btnLandmarkStepPaint != null) _btnLandmarkStepPaint.SetPressedNoSignal(enabled);
            if (_boxWeightPaintControls != null) _boxWeightPaintControls.Visible = enabled;
            UpdateRiggingMethodPanelVisibility();

            // Freeze live cloth/jiggle physics in Bind Pose while Weight Paint is active so the mesh
            // holds completely still under the brush; resume automatically when exiting Weight Paint.
            SetPreviewPhysicsPausedForWeightPaint(enabled);
            if (_weightPaintViewportHud != null && GodotObject.IsInstanceValid(_weightPaintViewportHud))
            {
                _weightPaintViewportHud.Visible = enabled;
            }
            if (enabled) SyncWeightPaintHudFromControls();

            if (enabled)
            {
                if (_isGizmoEditingSyntheticChain) SetSyntheticChainGizmoActive(false);
                else ClearSyntheticChainPreview();
                UpdateSyntheticChainViewportHud();

                if (_btnDeformBrushToggle != null && _btnDeformBrushToggle.ButtonPressed)
                {
                    _btnDeformBrushToggle.ButtonPressed = false;
                }
                if (_previewGizmo != null && GodotObject.IsInstanceValid(_previewGizmo))
                {
                    _previewGizmo.Visible = false;
                    if (_gizmoDummyTarget != null && GodotObject.IsInstanceValid(_gizmoDummyTarget))
                        _previewGizmo.Deselect(_gizmoDummyTarget);
                }
                if (_pinsDirtyNeedsRigRebuild || _previewRiggedContainer == null || !GodotObject.IsInstanceValid(_previewRiggedContainer))
                {
                    ExecuteLiveRigUpdate();
                }
                else
                {
                    EnterWeightPaintSurfaces();
                }
            }
            else
            {
                _isPaintingWeights = false;
                _isDraggingGradient = false;
                _pendingStrokeSnapshot = null;
                _strokeDirty = false;
                if (_previewCamera != null && GodotObject.IsInstanceValid(_previewCamera))
                    _previewCamera.Near = 0.05f;
                if (_hudSliderNearClip != null) _hudSliderNearClip.SetValueNoSignal(0.05);
                if (_hudLblNearClip != null) _hudLblNearClip.Text = "0.05";
                if (_btnChainWeightPaintToggle != null && _btnChainWeightPaintToggle.ButtonPressed)
                    _btnChainWeightPaintToggle.SetPressedNoSignal(false);
                // Keep _boxTab3WeightPaintHost visible so the "Paint Bone Weights (Heatmap)" toggle button
                // remains accessible inside Tab 3 -> Weights after exiting weight paint mode!
                if (_paintBrushRing != null && GodotObject.IsInstanceValid(_paintBrushRing)) _paintBrushRing.Visible = false;
                if (_paintGradientGizmo != null && GodotObject.IsInstanceValid(_paintGradientGizmo)) _paintGradientGizmo.Visible = false;
                foreach (var s in _paintSurfaces)
                {
                    s.HiddenIslands?.Clear();
                    CustomWeightPaintEditor.RefreshSurfaceHeatmap(s, -1, -1, false, null);
                }
                _paintSurfaces.Clear();
                UpdateGizmoTargetTransform();
            }
            ApplyLandmarkPreviewState();
        }

        private void BuildWeightPaintViewportHud(Control viewportPanel)
        {
            if (viewportPanel == null) return;

            // Wrap in a pass-through MarginContainer so viewportPanel (a PanelContainer)
            // does NOT stretch the HUD across the entire 3D viewport.
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
            _weightPaintViewportHud = hud;

            var root = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            root.AddThemeConstantOverride("separation", 4);
            hud.AddChild(root);

            // Row 1: Active bone pill + Alt/Ctrl+Click hint + 1-click Smooth All + Undo/Redo
            var row1 = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row1.AddThemeConstantOverride("separation", 6);
            _lblHudActiveBone = new Label
            {
                Text = "Bone: --",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
            };
            ImporterStyle.ApplyFontSize(_lblHudActiveBone, ImporterStyle.FontBody);
            _accentLabels.Add(_lblHudActiveBone);
            row1.AddChild(_lblHudActiveBone);

            var lblPickHint = new Label
            {
                Text = "Alt+Click: Pick Bone",
                VerticalAlignment = VerticalAlignment.Center
            };
            ImporterStyle.ApplyHintStyle(lblPickHint);
            lblPickHint.AutowrapMode = TextServer.AutowrapMode.Off;
            _secondaryLabels.Add(lblPickHint);
            row1.AddChild(lblPickHint);

            var btnSmoothAll = new Button
            {
                Text = "Smooth All",
                CustomMinimumSize = new Vector2(84, 24),
                ThemeTypeVariation = "ToolbarButton",
                TooltipText = "Runs a 1-click Laplacian smooth pass on the active bone across the whole mesh (undoable)."
            };
            btnSmoothAll.Pressed += OnSmoothAllActiveBonePressed;
            row1.AddChild(btnSmoothAll);

            _hudBtnUndo = new Button { Text = "Undo", CustomMinimumSize = new Vector2(54, 24), ThemeTypeVariation = "ToolbarButton", Disabled = true };
            _hudBtnUndo.Pressed += OnWeightUndoPressed;
            row1.AddChild(_hudBtnUndo);

            _hudBtnRedo = new Button { Text = "Redo", CustomMinimumSize = new Vector2(54, 24), ThemeTypeVariation = "ToolbarButton", Disabled = true };
            _hudBtnRedo.Pressed += OnWeightRedoPressed;
            row1.AddChild(_hudBtnRedo);
            root.AddChild(row1);

            // Row 2: 4 Brush Mode pills + compact Radius + Strength + Mirror toggle
            var row2 = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row2.AddThemeConstantOverride("separation", 6);

            string[] modeNames = { "Add +", "Sub -", "Smooth", "Solid 1" };
            for (int m = 0; m < 4; m++)
            {
                int capM = m;
                var mb = new Button
                {
                    Text = modeNames[m],
                    ToggleMode = true,
                    ButtonPressed = m == 0,
                    CustomMinimumSize = new Vector2(62, 24),
                    ThemeTypeVariation = "ToolbarButton"
                };
                mb.Pressed += () =>
                {
                    _optPaintMode?.Select(capM);
                    SyncWeightPaintHudFromControls();
                };
                _hudBrushModeBtns[m] = mb;
                row2.AddChild(mb);
            }

            row2.AddChild(new Label { Text = "R:", VerticalAlignment = VerticalAlignment.Center });
            _hudSliderRadius = new HSlider { MinValue = 0.02, MaxValue = 0.60, Step = 0.01, Value = 0.08, CustomMinimumSize = new Vector2(80, 0), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            _hudLblRadius = new Label { Text = "0.08", CustomMinimumSize = new Vector2(34, 0), VerticalAlignment = VerticalAlignment.Center };
            _hudSliderRadius.ValueChanged += v =>
            {
                _hudLblRadius.Text = $"{v:F2}";
                _sliderPaintRadius?.SetValueNoSignal(v);
                if (_lblPaintRadiusVal != null) _lblPaintRadiusVal.Text = $"{v:F2}m";
            };
            row2.AddChild(_hudSliderRadius);
            row2.AddChild(_hudLblRadius);

            row2.AddChild(new Label { Text = "Str:", VerticalAlignment = VerticalAlignment.Center });
            _hudSliderStrength = new HSlider { MinValue = 0.05, MaxValue = 1.0, Step = 0.05, Value = 0.35, CustomMinimumSize = new Vector2(76, 0), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            _hudLblStrength = new Label { Text = "0.35", CustomMinimumSize = new Vector2(34, 0), VerticalAlignment = VerticalAlignment.Center };
            _hudSliderStrength.ValueChanged += v =>
            {
                _hudLblStrength.Text = $"{v:F2}";
                _sliderPaintStrength?.SetValueNoSignal(v);
                if (_lblPaintStrengthVal != null) _lblPaintStrengthVal.Text = $"{v:F2}";
            };
            row2.AddChild(_hudSliderStrength);
            row2.AddChild(_hudLblStrength);

            _hudBtnMirror = new Button
            {
                Text = "Mirror",
                ToggleMode = true,
                ButtonPressed = false,
                CustomMinimumSize = new Vector2(58, 24),
                ThemeTypeVariation = "ToolbarButton",
                TooltipText = "Mirror L <-> R weight brushstrokes across X (disabled by default for cloth/physics bones)."
            };
            _hudBtnMirror.Toggled += on =>
            {
                _chkPaintMirror?.SetPressedNoSignal(on);
                UpdateResetBoneButtonLabel();
                RefreshWeightPaintHeatmap();
            };
            row2.AddChild(_hudBtnMirror);
            root.AddChild(row2);

            // Row 3: Precision masking & buried-vertex tools (Front Only, Same Island, Paint Through, Fill Island, Gradient, Show All, Slice)
            var row3 = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row3.AddThemeConstantOverride("h_separation", 5);
            row3.AddThemeConstantOverride("v_separation", 4);

            _hudBtnFrontOnly = new Button
            {
                Text = "Front Only",
                ToggleMode = true,
                ButtonPressed = _paintFrontFacingOnly,
                CustomMinimumSize = new Vector2(74, 22),
                ThemeTypeVariation = "ToolbarButton",
                TooltipText = "Only paint vertices facing the camera (prevents strokes from bleeding through to the back side)."
            };
            ImporterStyle.ApplyFontSize(_hudBtnFrontOnly, ImporterStyle.FontHint);
            _hudBtnFrontOnly.Toggled += on =>
            {
                _paintFrontFacingOnly = on;
                if (on && _paintThrough)
                {
                    _paintThrough = false;
                    _hudBtnPaintThrough?.SetPressedNoSignal(false);
                }
            };
            row3.AddChild(_hudBtnFrontOnly);

            _hudBtnSameIsland = new Button
            {
                Text = "Same Island",
                ToggleMode = true,
                ButtonPressed = _paintSameIslandOnly,
                CustomMinimumSize = new Vector2(82, 22),
                ThemeTypeVariation = "ToolbarButton",
                TooltipText = "Geodesic brush: walks connected mesh topology only so strokes never jump air gaps onto nearby hair strands or limbs."
            };
            ImporterStyle.ApplyFontSize(_hudBtnSameIsland, ImporterStyle.FontHint);
            _hudBtnSameIsland.Toggled += on => _paintSameIslandOnly = on;
            row3.AddChild(_hudBtnSameIsland);

            _hudBtnPaintThrough = new Button
            {
                Text = "Paint Through",
                ToggleMode = true,
                ButtonPressed = _paintThrough,
                CustomMinimumSize = new Vector2(90, 22),
                ThemeTypeVariation = "ToolbarButton",
                TooltipText = "Reach buried/occluded back vertices inside dense hair or cloth layers (combine with Same Island to protect other parts)."
            };
            ImporterStyle.ApplyFontSize(_hudBtnPaintThrough, ImporterStyle.FontHint);
            _hudBtnPaintThrough.Toggled += on =>
            {
                _paintThrough = on;
                if (on)
                {
                    _paintFrontFacingOnly = false;
                    _hudBtnFrontOnly?.SetPressedNoSignal(false);
                }
            };
            row3.AddChild(_hudBtnPaintThrough);

            _hudBtnFillIsland = new Button
            {
                Text = "Fill Island",
                ToggleMode = true,
                ButtonPressed = false,
                CustomMinimumSize = new Vector2(72, 22),
                ThemeTypeVariation = "ToolbarButton",
                TooltipText = "Click any connected hair strand or mesh island to flood-fill it with the active brush mode (Add / Sub / Solid 1)."
            };
            ImporterStyle.ApplyFontSize(_hudBtnFillIsland, ImporterStyle.FontHint);
            _hudBtnFillIsland.Toggled += on =>
            {
                _paintToolSubMode = on ? 1 : (_paintToolSubMode == 1 ? 0 : _paintToolSubMode);
                if (on) _hudBtnGradientFill?.SetPressedNoSignal(false);
            };
            row3.AddChild(_hudBtnFillIsland);

            _hudBtnGradientFill = new Button
            {
                Text = "Gradient",
                ToggleMode = true,
                ButtonPressed = false,
                CustomMinimumSize = new Vector2(68, 22),
                ThemeTypeVariation = "ToolbarButton",
                TooltipText = "Drag in 3D from root (0.0) to tip (1.0) to paint a smooth falloff gradient across the clicked island."
            };
            ImporterStyle.ApplyFontSize(_hudBtnGradientFill, ImporterStyle.FontHint);
            _hudBtnGradientFill.Toggled += on =>
            {
                _paintToolSubMode = on ? 2 : (_paintToolSubMode == 2 ? 0 : _paintToolSubMode);
                if (on) _hudBtnFillIsland?.SetPressedNoSignal(false);
                _isDraggingGradient = false;
            };
            row3.AddChild(_hudBtnGradientFill);

            _hudBtnUnhideAll = new Button
            {
                Text = "Show All",
                CustomMinimumSize = new Vector2(68, 22),
                ThemeTypeVariation = "ToolbarButton",
                Disabled = true,
                TooltipText = "Unhide all hidden mesh islands (Shortcuts: H = Hide hovered island, Shift+H = Solo island, Alt+H = Show All)."
            };
            ImporterStyle.ApplyFontSize(_hudBtnUnhideAll, ImporterStyle.FontHint);
            _hudBtnUnhideAll.Pressed += UnhideAllPaintIslands;
            row3.AddChild(_hudBtnUnhideAll);

            var lblSlice = new Label { Text = "Slice:", VerticalAlignment = VerticalAlignment.Center };
            ImporterStyle.ApplyFontSize(lblSlice, ImporterStyle.FontHint);
            row3.AddChild(lblSlice);

            _hudSliderNearClip = new HSlider
            {
                MinValue = 0.05,
                MaxValue = 2.00,
                Step = 0.02,
                Value = 0.05,
                CustomMinimumSize = new Vector2(68, 0),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Camera near-clip slice plane: slide right to cut open dense outer layers and inspect/paint buried interior vertices."
            };
            _hudLblNearClip = new Label { Text = "0.05", CustomMinimumSize = new Vector2(30, 0), VerticalAlignment = VerticalAlignment.Center };
            ImporterStyle.ApplyFontSize(_hudLblNearClip, ImporterStyle.FontHint);
            _hudSliderNearClip.ValueChanged += v =>
            {
                if (_previewCamera != null && GodotObject.IsInstanceValid(_previewCamera))
                    _previewCamera.Near = (float)v;
                if (_hudLblNearClip != null) _hudLblNearClip.Text = $"{v:F2}";
            };
            row3.AddChild(_hudSliderNearClip);
            row3.AddChild(_hudLblNearClip);

            root.AddChild(row3);
        }

        private void SyncWeightPaintHudFromControls()
        {
            string boneName = _optPaintBone != null && _optPaintBone.Selected >= 0
                ? _optPaintBone.GetItemText(_optPaintBone.Selected)
                : "--";
            if (_lblHudActiveBone != null) _lblHudActiveBone.Text = $"Bone: {boneName}";
            int curMode = _optPaintMode?.Selected ?? 0;
            for (int m = 0; m < 4; m++) _hudBrushModeBtns[m]?.SetPressedNoSignal(m == curMode);
            if (_hudSliderRadius != null && _sliderPaintRadius != null)
            {
                _hudSliderRadius.SetValueNoSignal(_sliderPaintRadius.Value);
                if (_hudLblRadius != null) _hudLblRadius.Text = $"{_sliderPaintRadius.Value:F2}";
            }
            if (_hudSliderStrength != null && _sliderPaintStrength != null)
            {
                _hudSliderStrength.SetValueNoSignal(_sliderPaintStrength.Value);
                if (_hudLblStrength != null) _hudLblStrength.Text = $"{_sliderPaintStrength.Value:F2}";
            }
            if (_hudBtnMirror != null && _chkPaintMirror != null)
                _hudBtnMirror.SetPressedNoSignal(_chkPaintMirror.ButtonPressed);
            if (_hudBtnUndo != null) _hudBtnUndo.Disabled = _weightUndoStack.Count == 0;
            if (_hudBtnRedo != null) _hudBtnRedo.Disabled = _weightRedoStack.Count == 0;
        }

        private bool SelectPaintBoneInEditor(string boneName)
        {
            if (string.IsNullOrWhiteSpace(boneName) || _optPaintBone == null) return false;
            for (int i = 0; i < _optPaintBone.ItemCount; i++)
            {
                if (_optPaintBone.IsItemDisabled(i)) continue;
                if (_optPaintBone.GetItemText(i).Equals(boneName, StringComparison.OrdinalIgnoreCase))
                {
                    _optPaintBone.Select(i);
                    UpdateResetBoneButtonLabel();
                    RefreshWeightPaintHeatmap();
                    SyncWeightPaintHudFromControls();
                    RebuildPaintBoneListUI();
                    return true;
                }
            }
            return false;
        }

        private void OnSmoothAllActiveBonePressed()
        {
            if (!HasValidPaintSurfaces() || _previewHeroSkeleton == null || _optPaintBone == null || _optPaintBone.Selected < 0)
                return;
            var skin = _paintSurfaces[0].MeshInstance?.Skin;
            if (skin == null) return;
            string boneName = _optPaintBone.GetItemText(_optPaintBone.Selected);
            int skelIdx = _previewHeroSkeleton.FindBone(boneName);
            if (skelIdx < 0) return;
            int targetBind = CustomModelAutoRigger.GetOrAddSkinBindIndex(skin, _previewHeroSkeleton, skelIdx);
            if (targetBind < 0) return;

            var snap = CustomWeightPaintEditor.CaptureHistoryEntry(_paintSurfaces, $"Smooth All ({boneName})");
            float str = (float)(_sliderPaintStrength?.Value ?? 0.45);
            if (CustomWeightPaintEditor.SmoothAllVerticesForBone(_paintSurfaces, targetBind, str))
            {
                PushWeightUndoSnapshot(snap);
                CommitPaintSurfaceOverrides();
                RefreshWeightPaintHeatmap();
                SetBottomStatus($"Smoothed weights for '{boneName}' across all vertices.", StatusTone.Success);
            }
        }

        private void RebuildPaintBoneListUI()
        {
            if (_boxPaintBoneList == null) return;
            foreach (Node ch in _boxPaintBoneList.GetChildren()) ch.QueueFree();

            string query = _txtPaintBoneSearch?.Text?.Trim() ?? "";
            string activeBone = _optPaintBone != null && _optPaintBone.Selected >= 0
                ? _optPaintBone.GetItemText(_optPaintBone.Selected)
                : "";

            _iconPaintLock ??= ResourceLoader.Load<Texture2D>("res://assets/at-icons/lock.svg");
            _iconPaintUnlock ??= ResourceLoader.Load<Texture2D>("res://assets/at-icons/unlock.svg");

            int shown = 0;
            foreach (var entry in _paintBoneCatalog)
            {
                if (_paintBoneFilterMode == 0 && entry.VertCount == 0) continue;
                if (_paintBoneFilterMode == 1 && !entry.IsPhysics) continue;
                if (!string.IsNullOrEmpty(query) && !entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                bool isSel = entry.Name.Equals(activeBone, StringComparison.OrdinalIgnoreCase);
                bool isLocked = _lockedPaintBones.Contains(entry.Name);
                string badge = entry.VertCount > 0 ? $"  ★ {entry.VertCount}v" : (entry.IsPhysics ? "  [phys]" : "");

                var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                row.AddThemeConstantOverride("separation", 3);

                var btn = new Button
                {
                    Text = entry.Name + badge,
                    Alignment = HorizontalAlignment.Left,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    CustomMinimumSize = new Vector2(0, 24),
                    ThemeTypeVariation = isSel ? "PrimaryActionButton" : "ToolbarButton",
                    ClipText = true,
                    TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
                };
                string capName = entry.Name;
                btn.Pressed += () => SelectPaintBoneInEditor(capName);
                row.AddChild(btn);

                var btnLock = new Button
                {
                    ToggleMode = true,
                    ButtonPressed = isLocked,
                    Icon = isLocked ? _iconPaintLock : _iconPaintUnlock,
                    IconAlignment = HorizontalAlignment.Center,
                    ExpandIcon = true,
                    CustomMinimumSize = new Vector2(24, 24),
                    ThemeTypeVariation = isLocked ? "PrimaryActionButton" : "IconButton",
                    TooltipText = isLocked
                        ? $"Unlock '{entry.Name}' so brushes can modify its weights."
                        : $"Lock '{entry.Name}' so painting other bones never alters its weights."
                };
                if (!isLocked) btnLock.Modulate = new Color(0.70f, 0.76f, 0.80f, 0.65f);
                btnLock.Toggled += on =>
                {
                    if (on) _lockedPaintBones.Add(capName);
                    else _lockedPaintBones.Remove(capName);
                    RebuildPaintBoneListUI();
                    SetBottomStatus(on
                        ? $"Locked '{capName}' weights — strokes on other bones will not disturb it."
                        : $"Unlocked '{capName}' weights.", StatusTone.Info);
                };
                row.AddChild(btnLock);

                _boxPaintBoneList.AddChild(row);
                shown++;
                if (shown >= 80) break;
            }

            if (shown == 0)
            {
                var emptyLbl = new Label { Text = "No bones match filter — try 'All' or Alt+Click mesh." };
                ImporterStyle.ApplyHintStyle(emptyLbl);
                _boxPaintBoneList.AddChild(emptyLbl);
            }
        }

        private void PopulatePaintBoneDropdown()
        {
            if (_optPaintBone == null || _previewHeroSkeleton == null) return;
            string prev = _optPaintBone.Selected >= 0 ? _optPaintBone.GetItemText(_optPaintBone.Selected) : "";
            _optPaintBone.Clear();
            _paintBoneCatalog.Clear();

            var skin = _paintSurfaces.Count > 0 ? _paintSurfaces[0].MeshInstance?.Skin : null;
            var weightedCounts = CustomWeightPaintEditor.CollectWeightedBones(_paintSurfaces, _previewHeroSkeleton, skin);

            var physicsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ch in _boneChains)
            {
                if (ch == null) continue;
                var candidates = ch.AppendedHeroBoneNames != null && ch.AppendedHeroBoneNames.Count > 0
                    ? ch.AppendedHeroBoneNames
                    : ch.SourceBoneNames;
                if (candidates == null) continue;
                foreach (string bn in candidates)
                    if (!string.IsNullOrWhiteSpace(bn)) physicsSet.Add(bn);
            }
            int origCount = _previewHeroSkeleton.HasMeta("OriginalHeroBoneCount")
                ? _previewHeroSkeleton.GetMeta("OriginalHeroBoneCount").AsInt32()
                : _previewHeroSkeleton.GetBoneCount();
            int totalBones = _previewHeroSkeleton.GetBoneCount();
            for (int b = origCount; b < totalBones; b++)
            {
                string bn = _previewHeroSkeleton.GetBoneName(b);
                if (!string.IsNullOrWhiteSpace(bn)) physicsSet.Add(bn);
            }

            var addedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. ALL bones actually weighted on the current mesh (sorted by vertex count descending)
            foreach (var kv in weightedCounts.OrderByDescending(x => x.Value))
            {
                int sb = _previewHeroSkeleton.FindBone(kv.Key);
                if (sb < 0 || !addedNames.Add(kv.Key)) continue;
                _paintBoneCatalog.Add((kv.Key, sb, kv.Value, physicsSet.Contains(kv.Key)));
            }

            // 2. Remaining physics / imported / accessory chain bones
            foreach (string bn in physicsSet)
            {
                int sb = _previewHeroSkeleton.FindBone(bn);
                if (sb < 0 || !addedNames.Add(bn)) continue;
                _paintBoneCatalog.Add((bn, sb, 0, true));
            }

            // 3. Core Deadlock landmark bones + all remaining hero skeleton bones
            foreach (string bName in CustomLandmarkWeightSolver.DeadlockBoneNames)
            {
                int sb = _previewHeroSkeleton.FindBone(bName);
                if (sb < 0 || !addedNames.Add(bName)) continue;
                _paintBoneCatalog.Add((bName, sb, 0, false));
            }
            for (int b = 0; b < totalBones; b++)
            {
                string bn = _previewHeroSkeleton.GetBoneName(b);
                if (string.IsNullOrWhiteSpace(bn) || !addedNames.Add(bn)) continue;
                _paintBoneCatalog.Add((bn, b, 0, false));
            }

            int selIdx = 0;
            foreach (var entry in _paintBoneCatalog)
            {
                int itemIdx = _optPaintBone.ItemCount;
                _optPaintBone.AddItem(entry.Name, entry.SkelIdx);
                if (!string.IsNullOrEmpty(prev) && entry.Name.Equals(prev, StringComparison.OrdinalIgnoreCase))
                    selIdx = itemIdx;
            }

            if (_optPaintBone.ItemCount > 0)
            {
                if (_optPaintBone.IsItemDisabled(selIdx) && _optPaintBone.ItemCount > 1) selIdx = 0;
                _optPaintBone.Select(selIdx);
            }

            // Default filter to 'Weighted' if we have weighted bones, otherwise 'Physics' or 'All'
            if (weightedCounts.Count == 0 && _paintBoneFilterMode == 0)
            {
                _paintBoneFilterMode = physicsSet.Count > 0 ? 1 : 2;
                for (int k = 0; k < 3; k++) _paintBoneFilterBtns[k]?.SetPressedNoSignal(k == _paintBoneFilterMode);
            }

            UpdateResetBoneButtonLabel();
            RebuildPaintBoneListUI();
            SyncWeightPaintHudFromControls();
        }

        private void UpdateResetBoneButtonLabel()
        {
            if (_btnResetBoneWeights == null) return;
            string boneName = _optPaintBone != null && _optPaintBone.Selected >= 0
                ? _optPaintBone.GetItemText(_optPaintBone.Selected)
                : "hand_L";
            bool hasMirror = (_chkPaintMirror?.ButtonPressed ?? true) && CustomWeightPaintEditor.MirrorBoneName(boneName) != null;
            _btnResetBoneWeights.Text = hasMirror ? $"Reset {boneName} (+Mir)" : $"Reset {boneName}";
        }

        private void UpdateWeightHistoryUI()
        {
            if (_btnPaintUndo != null) _btnPaintUndo.Disabled = _weightUndoStack.Count == 0;
            if (_btnPaintRedo != null) _btnPaintRedo.Disabled = _weightRedoStack.Count == 0;
            if (_hudBtnUndo != null) _hudBtnUndo.Disabled = _weightUndoStack.Count == 0;
            if (_hudBtnRedo != null) _hudBtnRedo.Disabled = _weightRedoStack.Count == 0;
            if (_lblPaintHistoryStatus != null)
            {
                _lblPaintHistoryStatus.Text = $"Undo: {_weightUndoStack.Count} | Redo: {_weightRedoStack.Count}";
            }
        }

        private void PushWeightUndoSnapshot(CustomWeightPaintEditor.WeightHistoryEntry snapshot)
        {
            if (snapshot == null) return;
            _weightUndoStack.Add(snapshot);
            if (_weightUndoStack.Count > 40) _weightUndoStack.RemoveAt(0);
            _weightRedoStack.Clear();
            UpdateWeightHistoryUI();
        }

        private bool HasValidPaintSurfaces(bool tryRecapture = true)
        {
            if (_paintSurfaces.Count > 0)
            {
                bool allValid = true;
                for (int i = 0; i < _paintSurfaces.Count; i++)
                {
                    var mi = _paintSurfaces[i]?.MeshInstance;
                    if (mi == null || !GodotObject.IsInstanceValid(mi) || mi.IsQueuedForDeletion())
                    {
                        allValid = false;
                        break;
                    }
                }
                if (allValid) return true;
                _paintSurfaces.Clear();
            }
            if (tryRecapture && IsWeightPaintActive() && _previewRiggedContainer != null && GodotObject.IsInstanceValid(_previewRiggedContainer))
            {
                EnterWeightPaintSurfaces();
                return _paintSurfaces.Count > 0 && GodotObject.IsInstanceValid(_paintSurfaces[0].MeshInstance);
            }
            return false;
        }

        private HashSet<int> CollectLockedBindIndices(Skin skin)
        {
            if (_lockedPaintBones.Count == 0 || skin == null || _previewHeroSkeleton == null) return null;
            var set = new HashSet<int>();
            foreach (string bn in _lockedPaintBones)
            {
                int bi = ResolveSkinBindIndexByBoneName(skin, _previewHeroSkeleton, bn);
                if (bi >= 0) set.Add(bi);
            }
            return set.Count > 0 ? set : null;
        }

        private void UpdateHiddenIslandsButtonVisual()
        {
            if (_hudBtnUnhideAll == null || !GodotObject.IsInstanceValid(_hudBtnUnhideAll)) return;
            int hiddenCount = 0;
            foreach (var s in _paintSurfaces) hiddenCount += s?.HiddenIslands?.Count ?? 0;
            _hudBtnUnhideAll.Disabled = hiddenCount == 0;
            _hudBtnUnhideAll.Text = hiddenCount > 0 ? $"Show All ({hiddenCount})" : "Show All";
        }

        private void HideHoveredPaintIsland()
        {
            if (!HasValidPaintSurfaces() || _lastHoveredPaintSurface == null || _lastHoveredPaintVertex < 0) return;
            var surf = _lastHoveredPaintSurface;
            if (surf.IslandIds == null || _lastHoveredPaintVertex >= surf.IslandIds.Length) return;
            int isl = surf.IslandIds[_lastHoveredPaintVertex];
            surf.HiddenIslands.Add(isl);
            UpdateHiddenIslandsButtonVisual();
            RefreshWeightPaintHeatmap();
            SetBottomStatus("Hid hovered mesh island (Alt+H to show all).", StatusTone.Info);
        }

        private void SoloHoveredPaintIsland()
        {
            if (!HasValidPaintSurfaces() || _lastHoveredPaintSurface == null || _lastHoveredPaintVertex < 0) return;
            var surf = _lastHoveredPaintSurface;
            if (surf.IslandIds == null || _lastHoveredPaintVertex >= surf.IslandIds.Length) return;
            int keepIsl = surf.IslandIds[_lastHoveredPaintVertex];
            foreach (var s in _paintSurfaces)
            {
                if (s?.IslandIds == null) continue;
                s.HiddenIslands.Clear();
                for (int id = 0; id < s.IslandCount; id++)
                {
                    if (!ReferenceEquals(s, surf) || id != keepIsl)
                        s.HiddenIslands.Add(id);
                }
            }
            UpdateHiddenIslandsButtonVisual();
            RefreshWeightPaintHeatmap();
            SetBottomStatus("Soloed hovered mesh island (Alt+H to show all).", StatusTone.Success);
        }

        private void UnhideAllPaintIslands()
        {
            if (!HasValidPaintSurfaces()) return;
            foreach (var s in _paintSurfaces) s?.HiddenIslands?.Clear();
            UpdateHiddenIslandsButtonVisual();
            RefreshWeightPaintHeatmap();
            SetBottomStatus("Restored all hidden mesh islands.", StatusTone.Info);
        }

        private void CommitPaintSurfaceOverrides()
        {
            if (!HasValidPaintSurfaces(tryRecapture: false) || _previewHeroSkeleton == null) return;
            var skin = _paintSurfaces[0].MeshInstance?.Skin;
            var exported = CustomWeightPaintEditor.ExportOverrides(_paintSurfaces, _previewHeroSkeleton, skin);
            foreach (var (k, v) in exported) _customSkinOverrides[k] = v;
            UpdatePaintOverrideLabel();
        }

        private bool EnsureBaselineOverrides()
        {
            if (_baselineSkinOverrides.Count > 0) return true;

            if (_customSkinOverrides.Count == 0 && HasValidPaintSurfaces(tryRecapture: false) && _previewHeroSkeleton != null)
            {
                var skin = _paintSurfaces[0].MeshInstance?.Skin;
                var baseExp = CustomWeightPaintEditor.ExportOverrides(_paintSurfaces, _previewHeroSkeleton, skin);
                foreach (var (k, v) in baseExp) _baselineSkinOverrides[k] = v;
                return _baselineSkinOverrides.Count > 0;
            }

            if (_liveImportedScene == null || _previewHeroClone == null || _previewHeroSkeleton == null)
                return false;

            // CRITICAL: Run the baseline RigAndBind pass on an isolated throwaway duplicate of _previewHeroClone!
            // Running RigAndBind directly on _previewHeroClone with the same SlotId destroys the live
            // _previewRiggedContainer and mutates _previewHeroSkeleton, causing the entire model to disappear
            // and throwing ObjectDisposedException on _paintSurfaces[0].MeshInstance!
            var baselineOpts = GatherRiggingOptions();
            baselineOpts.CustomSkinWeightOverrides = null;
            baselineOpts.SyntheticChainsToRebind = null;
            baselineOpts.AlignedTransform = BuildCurrentAlignedTransform();
            baselineOpts.IsPreviewOnly = true;

            var tempHeroClone = (Node3D)_previewHeroClone.Duplicate();
            tempHeroClone.Name = "__TempBaselineHeroClone";
            tempHeroClone.Visible = false;
            var hostParent = _previewHeroClone.GetParent() ?? _previewWorldRoot;
            hostParent?.AddChild(tempHeroClone);

            var tempModel = (Node3D)_liveImportedScene.Duplicate();
            _previewModelRoot?.AddChild(tempModel);
            tempModel.Transform = Transform3D.Identity;
            tempModel.Visible = false;

            try
            {
                var tempHeroSkel = CustomModelAutoRigger.FindSkeleton(tempHeroClone);
                var res = CustomModelAutoRigger.RigAndBind(tempModel, tempHeroClone, baselineOpts);
                if (res.Success && res.ContainerNode != null && GodotObject.IsInstanceValid(res.ContainerNode) && tempHeroSkel != null)
                {
                    var tmpSurfaces = CustomWeightPaintEditor.CaptureSurfaces(res.ContainerNode);
                    var tmpSkin = tmpSurfaces.Count > 0 ? tmpSurfaces[0].MeshInstance?.Skin : null;
                    if (tmpSkin != null)
                    {
                        var exported = CustomWeightPaintEditor.ExportOverrides(tmpSurfaces, tempHeroSkel, tmpSkin);
                        foreach (var (k, v) in exported) _baselineSkinOverrides[k] = v;
                    }
                }
            }
            finally
            {
                if (GodotObject.IsInstanceValid(tempHeroClone))
                {
                    tempHeroClone.GetParent()?.RemoveChild(tempHeroClone);
                    tempHeroClone.QueueFree();
                }
                if (GodotObject.IsInstanceValid(tempModel))
                {
                    tempModel.GetParent()?.RemoveChild(tempModel);
                    tempModel.QueueFree();
                }
            }
            return _baselineSkinOverrides.Count > 0;
        }

        private void OnWeightUndoPressed()
        {
            if (!IsWeightPaintActive() || !HasValidPaintSurfaces() || _previewHeroSkeleton == null || _weightUndoStack.Count == 0)
                return;
            int idx = _weightUndoStack.Count - 1;
            var entry = _weightUndoStack[idx];
            _weightUndoStack.RemoveAt(idx);

            var redoSnap = CustomWeightPaintEditor.CaptureHistoryEntry(_paintSurfaces, entry.Label);
            if (CustomWeightPaintEditor.RestoreHistoryEntry(_paintSurfaces, entry))
            {
                _weightRedoStack.Add(redoSnap);
                CommitPaintSurfaceOverrides();
                RefreshWeightPaintHeatmap();
                SetBottomStatus($"Undid weight edit: {entry.Label}", StatusTone.Info);
            }
            UpdateWeightHistoryUI();
        }

        private void OnWeightRedoPressed()
        {
            if (!IsWeightPaintActive() || !HasValidPaintSurfaces() || _previewHeroSkeleton == null || _weightRedoStack.Count == 0)
                return;
            int idx = _weightRedoStack.Count - 1;
            var entry = _weightRedoStack[idx];
            _weightRedoStack.RemoveAt(idx);

            var undoSnap = CustomWeightPaintEditor.CaptureHistoryEntry(_paintSurfaces, entry.Label);
            if (CustomWeightPaintEditor.RestoreHistoryEntry(_paintSurfaces, entry))
            {
                _weightUndoStack.Add(undoSnap);
                CommitPaintSurfaceOverrides();
                RefreshWeightPaintHeatmap();
                SetBottomStatus($"Redid weight edit: {entry.Label}", StatusTone.Info);
            }
            UpdateWeightHistoryUI();
        }

        private void OnSmoothBoundaryWeightsPressed()
        {
            if (!HasValidPaintSurfaces() || _previewHeroSkeleton == null) return;
            var skin = _paintSurfaces[0].MeshInstance?.Skin;
            if (skin == null) return;

            string boneName = _optPaintBone != null && _optPaintBone.Selected >= 0
                ? _optPaintBone.GetItemText(_optPaintBone.Selected)
                : "hand_L";
            int tgtBi = ResolveSkinBindIndexByBoneName(skin, _previewHeroSkeleton, boneName);
            if (tgtBi < 0) return;

            string mirName = (_chkPaintMirror?.ButtonPressed ?? true) ? CustomWeightPaintEditor.MirrorBoneName(boneName) : null;
            int mirBi = mirName != null ? ResolveSkinBindIndexByBoneName(skin, _previewHeroSkeleton, mirName) : -1;

            var snap = CustomWeightPaintEditor.CaptureHistoryEntry(_paintSurfaces, $"Smooth {boneName}");
            if (CustomWeightPaintEditor.SmoothBoneBoundary(_paintSurfaces, tgtBi, mirBi, iterations: 2))
            {
                PushWeightUndoSnapshot(snap);
                CommitPaintSurfaceOverrides();
                RefreshWeightPaintHeatmap();
                SetBottomStatus($"Smoothed '{boneName}' boundary weights (Ctrl+Z to undo).", StatusTone.Success);
            }
            else
            {
                SetBottomStatus($"No adjustable boundary vertices found for '{boneName}'.", StatusTone.Info);
            }
        }

        private void OnMirrorSideWeightsPressed(bool leftToRight)
        {
            if (!HasValidPaintSurfaces() || _previewHeroSkeleton == null) return;
            var skin = _paintSurfaces[0].MeshInstance?.Skin;
            if (skin == null) return;

            int pelvisIdx = _previewHeroSkeleton.FindBone("pelvis");
            float centerX = pelvisIdx >= 0 ? _previewHeroSkeleton.GetBoneGlobalRest(pelvisIdx).Origin.X : 0f;

            string label = leftToRight ? "Mirror L -> R" : "Mirror R -> L";
            var snap = CustomWeightPaintEditor.CaptureHistoryEntry(_paintSurfaces, label);
            if (CustomWeightPaintEditor.MirrorWeightsSide(_paintSurfaces, _previewHeroSkeleton, skin, centerX, leftToRight))
            {
                PushWeightUndoSnapshot(snap);
                CommitPaintSurfaceOverrides();
                RefreshWeightPaintHeatmap();
                SetBottomStatus($"Mirrored weights ({label}) across pelvis X={centerX:0.00} (Ctrl+Z to undo).", StatusTone.Success);
            }
            else
            {
                SetBottomStatus("Weights are already symmetric across the midline.", StatusTone.Info);
            }
        }

        private void OnResetSelectedBoneWeightsPressed()
        {
            if (!HasValidPaintSurfaces() || _previewHeroSkeleton == null) return;
            var skin = _paintSurfaces[0].MeshInstance?.Skin;
            if (skin == null || !EnsureBaselineOverrides()) return;
            // Re-verify paint surfaces are still valid after EnsureBaselineOverrides
            if (!HasValidPaintSurfaces()) return;
            skin = _paintSurfaces[0].MeshInstance?.Skin;
            if (skin == null) return;

            string boneName = _optPaintBone != null && _optPaintBone.Selected >= 0
                ? _optPaintBone.GetItemText(_optPaintBone.Selected)
                : "hand_L";
            bool includeMirror = _chkPaintMirror?.ButtonPressed ?? true;

            var snap = CustomWeightPaintEditor.CaptureHistoryEntry(_paintSurfaces, $"Reset {boneName}");
            if (CustomWeightPaintEditor.ResetBoneToBaseline(_paintSurfaces, _baselineSkinOverrides, _previewHeroSkeleton, skin, boneName, includeMirror))
            {
                PushWeightUndoSnapshot(snap);
                CommitPaintSurfaceOverrides();
                RefreshWeightPaintHeatmap();
                SetBottomStatus($"Reset '{boneName}' weights back to procedural baseline.", StatusTone.Success);
            }
            else
            {
                SetBottomStatus($"'{boneName}' already matches procedural baseline weights.", StatusTone.Info);
            }
        }

        private void OnResetAllWeightsPressed()
        {
            if (IsWeightPaintActive() && _paintSurfaces.Count > 0 && _previewHeroSkeleton != null && EnsureBaselineOverrides())
            {
                var skin = _paintSurfaces[0].MeshInstance?.Skin;
                var snap = CustomWeightPaintEditor.CaptureHistoryEntry(_paintSurfaces, "Reset All");
                if (CustomWeightPaintEditor.ResetAllToBaseline(_paintSurfaces, _baselineSkinOverrides, _previewHeroSkeleton, skin))
                {
                    PushWeightUndoSnapshot(snap);
                    _customSkinOverrides.Clear();
                    UpdatePaintOverrideLabel();
                    RefreshWeightPaintHeatmap();
                    SetBottomStatus("All painted weights reset to procedural baseline (Ctrl+Z to undo).", StatusTone.Info);
                    return;
                }
            }

            _customSkinOverrides.Clear();
            _baselineSkinOverrides.Clear();
            _paintSurfaces.Clear();
            _strokeDirty = false;
            _weightUndoStack.Clear();
            _weightRedoStack.Clear();
            UpdateWeightHistoryUI();
            UpdatePaintOverrideLabel();
            ExecuteLiveRigUpdate();
        }

        private void EnterWeightPaintSurfaces()
        {
            if (_previewRiggedContainer == null || !GodotObject.IsInstanceValid(_previewRiggedContainer)) return;
            _paintSurfaces = CustomWeightPaintEditor.CaptureSurfaces(_previewRiggedContainer);
            if (_customSkinOverrides.Count == 0 && _baselineSkinOverrides.Count == 0 && _paintSurfaces.Count > 0 && _previewHeroSkeleton != null)
            {
                var skin = _paintSurfaces[0].MeshInstance?.Skin;
                var baseExp = CustomWeightPaintEditor.ExportOverrides(_paintSurfaces, _previewHeroSkeleton, skin);
                foreach (var (k, v) in baseExp) _baselineSkinOverrides[k] = v;
            }
            PopulatePaintBoneDropdown();
            UpdateWeightHistoryUI();
            RefreshWeightPaintHeatmap();
        }

        private void RefreshViewportShadingAndWireframes()
        {
            bool paintActive = IsWeightPaintActive();
            bool paintWire = _chkPaintWireframe?.ButtonPressed ?? true;
            bool showWire = _viewportShadingMode != CustomWeightPaintEditor.ViewportShadingMode.Textured || _isXRayMode || (paintActive && paintWire);
            bool wireOnlyClay = _viewportShadingMode == CustomWeightPaintEditor.ViewportShadingMode.Wireframe;

            _wireLineMaterialDark ??= CustomWeightPaintEditor.CreateWireframeLineMaterial(new Color(0.08f, 0.12f, 0.16f, 0.85f));
            _wireLineMaterialBright ??= CustomWeightPaintEditor.CreateWireframeLineMaterial(new Color(0.55f, 0.85f, 0.95f, 0.95f));
            _wireLineMaterialXRay ??= CustomWeightPaintEditor.CreateWireframeLineMaterial(new Color(0.32f, 0.82f, 0.98f, 0.52f), normalBias: 0.0004f, xRayNoDepth: true);
            _wireClayMaterial ??= CustomWeightPaintEditor.CreateWireframeClayMaterial();

            Material lineMat = _isXRayMode
                ? _wireLineMaterialXRay
                : (wireOnlyClay ? _wireLineMaterialBright : _wireLineMaterialDark);

            if (_previewKeyLight != null && GodotObject.IsInstanceValid(_previewKeyLight))
                _previewKeyLight.ShadowEnabled = !paintActive;
            if (_previewFillLight != null && GodotObject.IsInstanceValid(_previewFillLight))
                _previewFillLight.ShadowEnabled = !paintActive;
            if (_previewRimLight != null && GodotObject.IsInstanceValid(_previewRimLight))
                _previewRimLight.ShadowEnabled = !paintActive;

            if (_previewRiggedContainer != null && GodotObject.IsInstanceValid(_previewRiggedContainer))
            {
                foreach (var rmi in CustomModelAutoRigger.CollectMeshes(_previewRiggedContainer))
                {
                    if (rmi != null && GodotObject.IsInstanceValid(rmi))
                        rmi.CastShadow = paintActive ? GeometryInstance3D.ShadowCastingSetting.Off : GeometryInstance3D.ShadowCastingSetting.On;
                }
                CustomWeightPaintEditor.SyncContainerShadingAndWireframes(
                    _previewRiggedContainer,
                    showWire,
                    wireOnlyClay,
                    paintActive,
                    _wireClayMaterial,
                    lineMat,
                    _paintSurfaces,
                    _isXRayMode);
            }

            if (_previewModelRoot != null && GodotObject.IsInstanceValid(_previewModelRoot))
            {
                CustomWeightPaintEditor.SyncContainerShadingAndWireframes(
                    _previewModelRoot,
                    showWire,
                    wireOnlyClay,
                    false,
                    _wireClayMaterial,
                    lineMat,
                    null,
                    _isXRayMode);
            }
        }

        private int ResolveSkinBindIndexByBoneName(Skin skin, Skeleton3D skel, string boneName)
        {
            if (skin == null || skel == null || string.IsNullOrEmpty(boneName)) return -1;
            int targetB = skel.FindBone(boneName);
            for (int bi = 0; bi < skin.GetBindCount(); bi++)
            {
                if (targetB >= 0 && skin.GetBindBone(bi) == targetB) return bi;
                if (boneName.Equals(skin.GetBindName(bi), StringComparison.Ordinal)) return bi;
            }
            return -1;
        }

        private void RefreshWeightPaintHeatmap()
        {
            if (!IsWeightPaintActive() || !HasValidPaintSurfaces() || _previewHeroSkeleton == null) return;
            var skin = _paintSurfaces[0].MeshInstance?.Skin;
            if (skin == null) return;

            RefreshViewportShadingAndWireframes();

            _weightHeatmapMaterial ??= new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                DisableReceiveShadows = true,
                DisableAmbientLight = true
            };
            _weightHeatmapMaterial.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            _weightHeatmapMaterial.DisableReceiveShadows = true;
            _weightHeatmapMaterial.Transparency = _isXRayMode ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled;
            _weightHeatmapMaterial.DepthDrawMode = _isXRayMode ? BaseMaterial3D.DepthDrawModeEnum.Disabled : BaseMaterial3D.DepthDrawModeEnum.OpaqueOnly;
            _weightHeatmapMaterial.AlbedoColor = _isXRayMode ? new Color(1f, 1f, 1f, 0.45f) : Colors.White;

            string boneName = _optPaintBone != null && _optPaintBone.Selected >= 0 ? _optPaintBone.GetItemText(_optPaintBone.Selected) : "hand_L";
            int tgtBi = ResolveSkinBindIndexByBoneName(skin, _previewHeroSkeleton, boneName);
            string mirName = (_chkPaintMirror?.ButtonPressed ?? true) ? CustomWeightPaintEditor.MirrorBoneName(boneName) : null;
            int mirBi = mirName != null ? ResolveSkinBindIndexByBoneName(skin, _previewHeroSkeleton, mirName) : -1;

            foreach (var s in _paintSurfaces)
                CustomWeightPaintEditor.RefreshSurfaceHeatmap(s, tgtBi, mirBi, true, _weightHeatmapMaterial);
        }

        private void UpdateBrushRingVisual(Vector3 hitPt, Vector3 normal, float radius, bool visible)
        {
            if (_paintBrushRing == null || !GodotObject.IsInstanceValid(_paintBrushRing))
            {
                _paintBrushRing = new MeshInstance3D
                {
                    Name = "WeightPaintBrushRing",
                    Mesh = new ImmediateMesh(),
                    Layers = 1u,
                    MaterialOverride = new StandardMaterial3D
                    {
                        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                        AlbedoColor = new Color(1f, 0.9f, 0.25f),
                        NoDepthTest = true,
                        RenderPriority = 110
                    }
                };
                _previewWorldRoot?.AddChild(_paintBrushRing);
            }
            _paintBrushRing.Visible = visible;
            if (!visible) return;

            Vector3 n = normal.LengthSquared() > 1e-6f ? normal.Normalized() : Vector3.Up;
            Vector3 t1 = (MathF.Abs(n.Y) < 0.9f ? n.Cross(Vector3.Up) : n.Cross(Vector3.Right)).Normalized();
            Vector3 t2 = n.Cross(t1).Normalized();
            Vector3 c = hitPt + n * 0.004f;
            var im = (ImmediateMesh)_paintBrushRing.Mesh;
            im.ClearSurfaces();
            im.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
            const int segs = 28;
            for (int i = 0; i <= segs; i++)
            {
                float ang = i * (Mathf.Tau / segs);
                im.SurfaceAddVertex(c + (t1 * Mathf.Cos(ang) + t2 * Mathf.Sin(ang)) * radius);
            }
            im.SurfaceEnd();
        }

        private void UpdateGradientGizmoVisual(Vector3 startWorld, Vector3 endWorld, bool visible)
        {
            if (_paintGradientGizmo == null || !GodotObject.IsInstanceValid(_paintGradientGizmo))
            {
                _paintGradientGizmo = new MeshInstance3D
                {
                    Name = "WeightPaintGradientGizmo",
                    Mesh = new ImmediateMesh(),
                    Layers = 1u, // strictly isolated from Visual Layer 21
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    MaterialOverride = new StandardMaterial3D
                    {
                        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                        VertexColorUseAsAlbedo = true,
                        NoDepthTest = true,
                        RenderPriority = 25
                    }
                };
                _previewWorldRoot?.AddChild(_paintGradientGizmo);
            }
            _paintGradientGizmo.Visible = visible;
            if (!visible || _paintGradientGizmo.Mesh is not ImmediateMesh im) return;

            im.ClearSurfaces();
            Vector3 axis = endWorld - startWorld;
            float len = axis.Length();
            if (len < 1e-4f) return;

            // Camera-facing orthonormal basis for crisp endpoint diamond markers & perpendicular tick bars
            Vector3 camFwd = _previewCamera != null ? (-_previewCamera.GlobalTransform.Basis.Z) : Vector3.Forward;
            Vector3 dir = axis / len;
            Vector3 side = dir.Cross(camFwd);
            if (side.LengthSquared() < 1e-5f) side = dir.Cross(Vector3.Up);
            if (side.LengthSquared() < 1e-5f) side = dir.Cross(Vector3.Right);
            side = side.Normalized();
            Vector3 up = side.Cross(dir).Normalized();

            im.SurfaceBegin(Mesh.PrimitiveType.Lines);

            // 1. Segmented heat-colored line from Start (0.0 Blue) -> End (1.0 Red)
            const int segs = 16;
            for (int i = 0; i < segs; i++)
            {
                float t0 = i / (float)segs;
                float t1 = (i + 1) / (float)segs;
                im.SurfaceSetColor(CustomWeightPaintEditor.HeatColor(t0));
                im.SurfaceAddVertex(startWorld.Lerp(endWorld, t0));
                im.SurfaceSetColor(CustomWeightPaintEditor.HeatColor(t1));
                im.SurfaceAddVertex(startWorld.Lerp(endWorld, t1));
            }

            // 2. Start marker (0.0 cool cyan-blue cross + diamond) & End marker (1.0 hot red-gold cross + diamond)
            float markR = Mathf.Clamp(len * 0.08f, 0.012f, 0.045f);
            void DrawDiamond(Vector3 pt, Color col)
            {
                Vector3 a = pt + side * markR;
                Vector3 b = pt + dir * markR;
                Vector3 c = pt - side * markR;
                Vector3 d = pt - dir * markR;
                Vector3 u = pt + up * markR;
                Vector3 v = pt - up * markR;
                void Seg(Vector3 p0, Vector3 p1) { im.SurfaceSetColor(col); im.SurfaceAddVertex(p0); im.SurfaceSetColor(col); im.SurfaceAddVertex(p1); }
                Seg(a, b); Seg(b, c); Seg(c, d); Seg(d, a);
                Seg(a, u); Seg(u, c); Seg(c, v); Seg(v, a);
            }
            DrawDiamond(startWorld, new Color(0.22f, 0.78f, 1.0f));
            DrawDiamond(endWorld, new Color(1.0f, 0.25f, 0.18f));

            // 3. Perpendicular start/end band ticks showing the 0.0 and 1.0 planes
            float bandW = Mathf.Clamp(len * 0.22f, 0.03f, 0.12f);
            im.SurfaceSetColor(new Color(0.22f, 0.78f, 1.0f));
            im.SurfaceAddVertex(startWorld - side * bandW);
            im.SurfaceSetColor(new Color(0.22f, 0.78f, 1.0f));
            im.SurfaceAddVertex(startWorld + side * bandW);

            im.SurfaceSetColor(new Color(1.0f, 0.25f, 0.18f));
            im.SurfaceAddVertex(endWorld - side * bandW);
            im.SurfaceSetColor(new Color(1.0f, 0.25f, 0.18f));
            im.SurfaceAddVertex(endWorld + side * bandW);

            im.SurfaceEnd();
        }

        private bool HandleWeightPaintInput(InputEvent @event)
        {
            if (!IsWeightPaintActive() || _previewCamera == null || _previewHeroSkeleton == null || !HasValidPaintSurfaces())
                return false;

            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                // Alt+Click or Ctrl+Click directly on the 3D mesh = Blender-style bone eyedropper!
                if (mb.Pressed && (mb.AltPressed || mb.CtrlPressed))
                {
                    var skin = _paintSurfaces[0].MeshInstance?.Skin;
                    CustomWeightPaintEditor.UpdatePosedVertices(_paintSurfaces, _previewHeroSkeleton, skin);
                    Vector3 orig = _previewCamera.ProjectRayOrigin(mb.Position);
                    Vector3 dir = _previewCamera.ProjectRayNormal(mb.Position);
                    if (CustomWeightPaintEditor.RaycastSurfaces(orig, dir, _paintSurfaces, out var hitSurf, out var hitPt, out _, out int hitV) &&
                        CustomWeightPaintEditor.PickStrongestBoneAtPoint(hitSurf, hitPt, _previewHeroSkeleton, skin, out string pickedBone, out float pickedW))
                    {
                        _lastHoveredPaintSurface = hitSurf;
                        _lastHoveredPaintVertex = hitV;
                        if (!SelectPaintBoneInEditor(pickedBone))
                        {
                            PopulatePaintBoneDropdown();
                            SelectPaintBoneInEditor(pickedBone);
                        }
                        SetBottomStatus($"Picked bone '{pickedBone}' (weight {pickedW:F2}).", StatusTone.Info);
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
                }
                else if (mb.Pressed && !mb.ShiftPressed)
                {
                    var skin = _paintSurfaces[0].MeshInstance?.Skin;
                    CustomWeightPaintEditor.UpdatePosedVertices(_paintSurfaces, _previewHeroSkeleton, skin);
                    Vector3 orig = _previewCamera.ProjectRayOrigin(mb.Position);
                    Vector3 dir = _previewCamera.ProjectRayNormal(mb.Position);
                    if (CustomWeightPaintEditor.RaycastSurfaces(orig, dir, _paintSurfaces, out var hitSurf, out var hitPt, out var hitNorm, out int hitV))
                    {
                        _lastHoveredPaintSurface = hitSurf;
                        _lastHoveredPaintVertex = hitV;
                        string boneLabel = _optPaintBone != null && _optPaintBone.Selected >= 0 ? _optPaintBone.GetItemText(_optPaintBone.Selected) : "weight";

                        if (_paintToolSubMode == 1)
                        {
                            int isl = (hitSurf.IslandIds != null && hitV >= 0 && hitV < hitSurf.IslandIds.Length) ? hitSurf.IslandIds[hitV] : -1;
                            if (isl >= 0) ApplyFloodFillIsland(hitSurf, isl, boneLabel);
                            GetViewport().SetInputAsHandled();
                            return true;
                        }
                        if (_paintToolSubMode == 2)
                        {
                            _isDraggingGradient = true;
                            _gradientStartWorld = hitPt;
                            _gradientHitSurface = _paintSameIslandOnly ? hitSurf : null;
                            _gradientHitIslandId = (_paintSameIslandOnly && hitSurf.IslandIds != null && hitV >= 0 && hitV < hitSurf.IslandIds.Length)
                                ? hitSurf.IslandIds[hitV] : -1;
                            _isOrbiting = false; _isPanning = false;
                            UpdateGradientGizmoVisual(_gradientStartWorld, hitPt, true);
                            SetBottomStatus($"Gradient start placed — drag to tip and release to fill '{boneLabel}' (Esc to cancel).", StatusTone.Info);
                            GetViewport().SetInputAsHandled();
                            return true;
                        }

                        _pendingStrokeSnapshot = CustomWeightPaintEditor.CaptureHistoryEntry(_paintSurfaces, $"Brush ({boneLabel})");
                        _strokeDirty = false;
                        _isPaintingWeights = true;
                        _isOrbiting = false; _isPanning = false;
                        _strokeDirty |= ApplyBrushAtHit(hitPt, hitNorm, hitSurf, hitV, orig, dir);
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
                }
                else if (!mb.Pressed && _isDraggingGradient)
                {
                    _isDraggingGradient = false;
                    UpdateGradientGizmoVisual(Vector3.Zero, Vector3.Zero, false);
                    var skin = _paintSurfaces[0].MeshInstance?.Skin;
                    CustomWeightPaintEditor.UpdatePosedVertices(_paintSurfaces, _previewHeroSkeleton, skin);
                    Vector3 orig = _previewCamera.ProjectRayOrigin(mb.Position);
                    Vector3 dir = _previewCamera.ProjectRayNormal(mb.Position);
                    Vector3 endWorld = ResolveGradientDragPoint(orig, dir);
                    string boneLabel = _optPaintBone != null && _optPaintBone.Selected >= 0 ? _optPaintBone.GetItemText(_optPaintBone.Selected) : "weight";
                    ApplyGradientDrag(endWorld, boneLabel);
                    GetViewport().SetInputAsHandled();
                    return true;
                }
                else if (!mb.Pressed && _isPaintingWeights)
                {
                    _isPaintingWeights = false;
                    if (_strokeDirty && _pendingStrokeSnapshot != null &&
                        CustomWeightPaintEditor.HasSurfaceWeightDifference(_pendingStrokeSnapshot, _paintSurfaces))
                    {
                        PushWeightUndoSnapshot(_pendingStrokeSnapshot);
                    }
                    _pendingStrokeSnapshot = null;
                    _strokeDirty = false;
                    CommitPaintSurfaceOverrides();
                    GetViewport().SetInputAsHandled();
                    return true;
                }
            }
            else if (@event is InputEventMouseMotion mm && !_isOrbiting && !_isPanning)
            {
                var skin = _paintSurfaces[0].MeshInstance?.Skin;
                CustomWeightPaintEditor.UpdatePosedVertices(_paintSurfaces, _previewHeroSkeleton, skin);
                Vector3 orig = _previewCamera.ProjectRayOrigin(mm.Position);
                Vector3 dir = _previewCamera.ProjectRayNormal(mm.Position);
                if (_isDraggingGradient)
                {
                    Vector3 endWorld = ResolveGradientDragPoint(orig, dir);
                    UpdateGradientGizmoVisual(_gradientStartWorld, endWorld, true);
                    GetViewport().SetInputAsHandled();
                    return true;
                }
                if (CustomWeightPaintEditor.RaycastSurfaces(orig, dir, _paintSurfaces, out var hitSurf, out var hitPt, out var hitNorm, out int hitV))
                {
                    _lastHoveredPaintSurface = hitSurf;
                    _lastHoveredPaintVertex = hitV;
                    float r = (float)(_sliderPaintRadius?.Value ?? 0.08);
                    UpdateBrushRingVisual(hitPt, hitNorm, r, _paintToolSubMode == 0);
                    if (_isPaintingWeights && _paintToolSubMode == 0)
                    {
                        _strokeDirty |= ApplyBrushAtHit(hitPt, hitNorm, hitSurf, hitV, orig, dir);
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
                }
                else
                {
                    _lastHoveredPaintSurface = null;
                    _lastHoveredPaintVertex = -1;
                    UpdateBrushRingVisual(Vector3.Zero, Vector3.Up, 0.05f, false);
                }
            }
            return false;
        }

        private Vector3 ResolveGradientDragPoint(Vector3 orig, Vector3 dir)
        {
            if (CustomWeightPaintEditor.RaycastSurfaces(orig, dir, _paintSurfaces, out _, out var endHitPt, out _, out _))
                return endHitPt;
            Vector3 planeN = _previewCamera != null ? (-_previewCamera.GlobalTransform.Basis.Z) : Vector3.Forward;
            float denom = dir.Dot(planeN);
            float t = MathF.Abs(denom) > 1e-5f ? (_gradientStartWorld - orig).Dot(planeN) / denom : 0f;
            return t > 0f ? orig + dir * t : _gradientStartWorld;
        }

        private bool ApplyFloodFillIsland(CustomWeightPaintEditor.SurfaceState surf, int islandId, string boneName)
        {
            if (!HasValidPaintSurfaces() || _previewHeroSkeleton == null) return false;
            var skin = _paintSurfaces[0].MeshInstance?.Skin;
            if (skin == null) return false;
            if (_lockedPaintBones.Contains(boneName))
            {
                SetBottomStatus($"'{boneName}' is locked — unlock it in the bone list to edit its weights.", StatusTone.Warning);
                return false;
            }
            int tgtBi = ResolveSkinBindIndexByBoneName(skin, _previewHeroSkeleton, boneName);
            if (tgtBi < 0) return false;
            int pIdx = _previewHeroSkeleton.FindBone(boneName);
            int parentB = pIdx >= 0 ? _previewHeroSkeleton.GetBoneParent(pIdx) : -1;
            string parentName = parentB >= 0 ? _previewHeroSkeleton.GetBoneName(parentB) : "pelvis";
            int fbBi = ResolveSkinBindIndexByBoneName(skin, _previewHeroSkeleton, parentName);
            if (fbBi < 0) fbBi = ResolveSkinBindIndexByBoneName(skin, _previewHeroSkeleton, "pelvis");

            var mode = _optPaintMode != null && _optPaintMode.Selected >= 0
                ? (CustomWeightPaintEditor.BrushMode)_optPaintMode.GetItemId(_optPaintMode.Selected)
                : CustomWeightPaintEditor.BrushMode.SetOne;
            float strength = (float)(_sliderPaintStrength?.Value ?? 1.0);
            var locked = CollectLockedBindIndices(skin);
            var snap = CustomWeightPaintEditor.CaptureHistoryEntry(_paintSurfaces, $"Fill Island ({boneName})");
            if (CustomWeightPaintEditor.FloodFillIsland(surf, islandId, mode, strength, tgtBi, fbBi, locked))
            {
                PushWeightUndoSnapshot(snap);
                CommitPaintSurfaceOverrides();
                RefreshWeightPaintHeatmap();
                SetBottomStatus($"Flood-filled island for '{boneName}' (Ctrl+Z to undo).", StatusTone.Success);
                return true;
            }
            return false;
        }

        private void ApplyGradientDrag(Vector3 endWorld, string boneName)
        {
            if (!HasValidPaintSurfaces() || _previewHeroSkeleton == null) return;
            var skin = _paintSurfaces[0].MeshInstance?.Skin;
            if (skin == null) return;
            if (_lockedPaintBones.Contains(boneName))
            {
                SetBottomStatus($"'{boneName}' is locked — unlock it in the bone list to edit its weights.", StatusTone.Warning);
                return;
            }
            int tgtBi = ResolveSkinBindIndexByBoneName(skin, _previewHeroSkeleton, boneName);
            if (tgtBi < 0) return;
            int pIdx = _previewHeroSkeleton.FindBone(boneName);
            int parentB = pIdx >= 0 ? _previewHeroSkeleton.GetBoneParent(pIdx) : -1;
            string parentName = parentB >= 0 ? _previewHeroSkeleton.GetBoneName(parentB) : "pelvis";
            int fbBi = ResolveSkinBindIndexByBoneName(skin, _previewHeroSkeleton, parentName);
            if (fbBi < 0) fbBi = ResolveSkinBindIndexByBoneName(skin, _previewHeroSkeleton, "pelvis");

            float strength = (float)(_sliderPaintStrength?.Value ?? 1.0);
            var locked = CollectLockedBindIndices(skin);
            var snap = CustomWeightPaintEditor.CaptureHistoryEntry(_paintSurfaces, $"Gradient ({boneName})");
            if (CustomWeightPaintEditor.ApplyGradientFill(_paintSurfaces, _gradientHitSurface, _gradientHitIslandId, _gradientStartWorld, endWorld, strength, tgtBi, fbBi, locked))
            {
                PushWeightUndoSnapshot(snap);
                CommitPaintSurfaceOverrides();
                RefreshWeightPaintHeatmap();
                SetBottomStatus($"Applied smooth gradient for '{boneName}' (Ctrl+Z to undo).", StatusTone.Success);
            }
        }

        private bool ApplyBrushAtHit(
            Vector3 hitPt,
            Vector3 hitNorm,
            CustomWeightPaintEditor.SurfaceState hitSurf = null,
            int hitVertex = -1,
            Vector3 cameraRayOrigin = default,
            Vector3 cameraRayDir = default)
        {
            if (!HasValidPaintSurfaces() || _previewHeroSkeleton == null) return false;
            var skin = _paintSurfaces[0].MeshInstance?.Skin;
            if (skin == null) return false;

            string boneName = _optPaintBone != null && _optPaintBone.Selected >= 0 ? _optPaintBone.GetItemText(_optPaintBone.Selected) : "hand_L";
            if (_lockedPaintBones.Contains(boneName))
            {
                SetBottomStatus($"'{boneName}' is locked — unlock it in the bone list to paint it.", StatusTone.Warning);
                return false;
            }
            int tgtBi = ResolveSkinBindIndexByBoneName(skin, _previewHeroSkeleton, boneName);
            int pIdx = _previewHeroSkeleton.FindBone(boneName);
            int parentB = pIdx >= 0 ? _previewHeroSkeleton.GetBoneParent(pIdx) : -1;
            string parentName = parentB >= 0 ? _previewHeroSkeleton.GetBoneName(parentB) : "pelvis";
            int fbBi = ResolveSkinBindIndexByBoneName(skin, _previewHeroSkeleton, parentName);
            if (fbBi < 0) fbBi = ResolveSkinBindIndexByBoneName(skin, _previewHeroSkeleton, "pelvis");

            bool mirror = _chkPaintMirror?.ButtonPressed ?? true;
            string mirName = mirror ? CustomWeightPaintEditor.MirrorBoneName(boneName) : null;
            int mirBi = mirName != null ? ResolveSkinBindIndexByBoneName(skin, _previewHeroSkeleton, mirName) : -1;
            string mirFbName = CustomWeightPaintEditor.MirrorBoneName(parentName);
            int mirFbBi = mirFbName != null ? ResolveSkinBindIndexByBoneName(skin, _previewHeroSkeleton, mirFbName) : fbBi;

            var mode = _optPaintMode != null && _optPaintMode.Selected >= 0
                ? (CustomWeightPaintEditor.BrushMode)_optPaintMode.GetItemId(_optPaintMode.Selected)
                : CustomWeightPaintEditor.BrushMode.Add;
            float r = (float)(_sliderPaintRadius?.Value ?? 0.08);
            float strength = (float)(_sliderPaintStrength?.Value ?? 0.35);

            var opts = new CustomWeightPaintEditor.BrushOptions
            {
                FrontFacingOnly = _paintFrontFacingOnly,
                SameIslandOnly = _paintSameIslandOnly,
                PaintThrough = _paintThrough,
                HitSurface = hitSurf,
                HitVertex = hitVertex,
                CameraRayOrigin = cameraRayOrigin,
                CameraRayDir = cameraRayDir,
                LockedBindIndices = CollectLockedBindIndices(skin)
            };

            UpdateBrushRingVisual(hitPt, hitNorm, r, true);
            if (CustomWeightPaintEditor.ApplyBrush(_paintSurfaces, hitPt, mode, r, strength, tgtBi, fbBi, mirror, mirBi, mirFbBi, opts))
            {
                foreach (var s in _paintSurfaces)
                    CustomWeightPaintEditor.RefreshSurfaceHeatmap(s, tgtBi, mirBi, true, _weightHeatmapMaterial);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Applies Landmark/Weight-Paint viewport visibility: static unskinned model + pins ONLY on Tab 2 Bind Pose,
        /// leaving the snapped rigged model active on Tab 1/3 or during animations. Respects user's Hero Ghost toggle.
        /// </summary>
        private void ApplyLandmarkPreviewState()
        {
            bool pinEdit = IsPinEditActive();
            bool paintMode = IsWeightPaintActive();
            bool sculptActive = IsImportedSculptActive();
            bool tab1OriginalPose = _showOriginalPoseInTab1 && _rightTabs != null && _rightTabs.CurrentTab == 0;

            if (_previewModelRoot != null && GodotObject.IsInstanceValid(_previewModelRoot))
            {
                bool riggedValid = _previewRiggedContainer != null && GodotObject.IsInstanceValid(_previewRiggedContainer);
                if (pinEdit || tab1OriginalPose || sculptActive)
                {
                    if (riggedValid) _previewRiggedContainer.Visible = false;
                    _previewModelRoot.Transform = BuildCurrentAlignedTransform();
                    SyncPreviewModelMeshVisibility();
                    _previewModelRoot.Visible = true;
                }
                else if (riggedValid)
                {
                    _previewRiggedContainer.Visible = true;
                    _previewModelRoot.Visible = false;
                    if (paintMode && _paintSurfaces.Count == 0) EnterWeightPaintSurfaces();
                }
            }

            if (!paintMode && _paintBrushRing != null && GodotObject.IsInstanceValid(_paintBrushRing))
                _paintBrushRing.Visible = false;

            RefreshViewportShadingAndWireframes();
            UpdatePreviewHeroKeepAndGhostMaterials();
            RefreshPinVisuals();
            if (_showBones) RedrawBoneWireframe();
            if (_refRigViewportHud != null && GodotObject.IsInstanceValid(_refRigViewportHud))
            {
                _refRigViewportHud.Visible = pinEdit;
                if (pinEdit) SyncReferenceRigViewportHud();
            }
            if (_refRigViewportBottomHud != null && GodotObject.IsInstanceValid(_refRigViewportBottomHud))
            {
                _refRigViewportBottomHud.Visible = pinEdit;
            }
            if (pinEdit && _boxSelectedBoneBadge != null && GodotObject.IsInstanceValid(_boxSelectedBoneBadge))
            {
                _boxSelectedBoneBadge.Visible = false;
            }
        }

    }
}
