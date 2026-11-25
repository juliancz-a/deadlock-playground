using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using DeadlockPlayground.Importer;
using DeadlockPlayground.Theming;

namespace DeadlockPlayground.UI
{
    public enum ImportedSculptBrushMode
    {
        Grab = 0,
        Inflate = 1,
        Shrink = 2,
        Smooth = 3,
        Erase = 4
    }

    public partial class CustomModelImporterModalUI
    {
        // =========================================================================
        // Imported Accessory / Clothing Reshape & Interactive 3D Fit Sculpt Brush
        // =========================================================================

        private Control _boxImportedSculptCard;
        private HSlider _sliderImpBottomFlare;
        private Label _lblImpBottomFlareVal;
        private HSlider _sliderImpTopFlare;
        private Label _lblImpTopFlareVal;
        private HSlider _sliderImpInflate;
        private Label _lblImpInflateVal;

        private Button _btnImpSculptToggle;
        private Control _importedSculptViewportHud;
        private readonly Button[] _hudSculptModeBtns = new Button[5];
        private readonly Button[] _hudSculptHeroVisBtns = new Button[3];
        private ImportedSculptBrushMode _activeImpSculptMode = ImportedSculptBrushMode.Grab;
        private HSlider _sliderImpSculptRadius;
        private Label _lblImpSculptRadiusVal;
        private HSlider _sliderImpSculptStrength;
        private Label _lblImpSculptStrengthVal;
        private Button _btnImpSculptMirror;
        private Button _btnImpSculptUndo;
        private Button _btnImpSculptReset;
        private Button _btnImpSculptCardReset;
        private Label _lblImpSculptStatus;
        private Action _impSculptOriginalMeshRestore;

        private readonly Dictionary<string, Dictionary<int, Vector3>> _importedSculptDeltas = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Dictionary<string, Dictionary<int, Vector3>>> _importedSculptUndoStack = new();
        private Dictionary<string, Dictionary<int, Vector3>> _pendingImpSculptUndoSnapshot;
        private bool _isImpSculptBrushing;
        private bool _isSyncingImpSculptUi;

        // Live grab-drag state
        private Vector3 _impGrabStartWorld;
        private Vector3 _impGrabPlaneNormal;
        private readonly List<(ImpSculptSurfaceCache Cache, int VertIdx, float Weight, bool IsMirrored)> _impGrabVerts = new();
        private readonly Dictionary<(ImpSculptSurfaceCache Cache, int VertIdx), Vector3> _impGrabInitialDeltas = new();

        private sealed class ImpSculptSurfaceCache
        {
            public MeshInstance3D SourceMi;      // mesh inside _previewModelInstance
            public MeshInstance3D DisplayMi;     // live mesh in _previewRiggedContainer (if bound) or SourceMi
            public int SurfaceIndex;
            public string SurfaceKey;
            public Transform3D LocalToWorld;
            public Transform3D WorldToLocal;
            public Vector3[] BaseLocalVerts;
            public Vector3[] CurrentWorldVerts;
            public Vector3[] CurrentWorldNorms;
            public int[] Indices;
            public int[][] Neighbors;
        }

        private readonly List<ImpSculptSurfaceCache> _impSculptCaches = new();

        private bool IsImportedSculptActive() =>
            _btnImpSculptToggle != null && _btnImpSculptToggle.ButtonPressed;

        private static Dictionary<string, Dictionary<int, Vector3>> CloneImportedSculptDeltas(
            Dictionary<string, Dictionary<int, Vector3>> src)
        {
            if (src == null || src.Count == 0) return null;
            var copy = new Dictionary<string, Dictionary<int, Vector3>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (sk, vMap) in src)
            {
                if (!string.IsNullOrEmpty(sk) && vMap != null && vMap.Count > 0)
                    copy[sk] = new Dictionary<int, Vector3>(vMap);
            }
            return copy.Count > 0 ? copy : null;
        }

        private Control BuildImportedSculptCard()
        {
            var (card, body) = CreateInspectorCard(
                "Reshape / Fit Imported Mesh",
                "Widen the bottom of a sweater/skirt, puff out tight spots, or sculpt directly in 3D so clothing fits cleanly over the hero.");
            _boxImportedSculptCard = card;

            // --- Quick Parametric Shape Sliders ---
            (HSlider sld, Label lbl) AddShapeSlider(string label, double min, double max, double step, string formatSuffix, bool asPercent, string tip)
            {
                var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                var l = new Label { Text = label, CustomMinimumSize = new Vector2(106, 0), TooltipText = tip };
                ImporterStyle.ApplyFontSize(l, ImporterStyle.FontHint);
                var s = new HSlider
                {
                    MinValue = min, MaxValue = max, Step = step, Value = 0.0,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                    TooltipText = tip
                };
                var v = new Label
                {
                    Text = asPercent ? "0%" : $"0.0{formatSuffix}",
                    CustomMinimumSize = new Vector2(52, 0),
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                ImporterStyle.ApplyFontSize(v, ImporterStyle.FontHint);
                s.ValueChanged += (val) =>
                {
                    v.Text = asPercent
                        ? $"{(val > 0 ? "+" : "")}{Mathf.RoundToInt((float)val * 100)}%"
                        : $"{(val > 0 ? "+" : "")}{val * 100.0:F1}cm";
                    if (!_isSyncingImpSculptUi)
                    {
                        UpdateImportedSculptStatusLabel();
                        if (IsImportedSculptActive())
                            RefreshImportedSculptPreviewFromCurrentSettings();
                        else
                            ScheduleDebouncedLiveRigUpdate();
                    }
                };
                row.AddChild(l);
                row.AddChild(s);
                row.AddChild(v);
                body.AddChild(row);
                return (s, v);
            }

            (_sliderImpBottomFlare, _lblImpBottomFlareVal) = AddShapeSlider(
                "Bottom Flare:", -0.50, 0.80, 0.01, "%", asPercent: true,
                "Smoothly widens (+) or narrows (-) the bottom hem of a sweater, jacket, skirt, or pants.");
            (_sliderImpTopFlare, _lblImpTopFlareVal) = AddShapeSlider(
                "Top Width:", -0.50, 0.80, 0.01, "%", asPercent: true,
                "Smoothly widens (+) or narrows (-) the upper shoulders/chest/collar area.");
            (_sliderImpInflate, _lblImpInflateVal) = AddShapeSlider(
                "Inflate / Expand:", -0.05, 0.10, 0.002, "cm", asPercent: false,
                "Uniformly pushes vertices outward (+) or inward (-) along surface normals to eliminate body clipping.");

            body.AddChild(new HSeparator());

            // --- Compact Action Row: Launch Floating 3D Sculpt HUD + Reset ---
            var launchRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            launchRow.AddThemeConstantOverride("separation", 6);

            _btnImpSculptToggle = new Button
            {
                Text = "Sculpt Mesh in 3D",
                ToggleMode = true,
                ButtonPressed = false,
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(0, 34),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Opens the floating 3D Sculpt HUD and switches to live Sculpt Pose so Grab / Inflate / Shrink / Smooth strokes update at 60 FPS without Auto-Snap interference.\nPress Esc or Done to bake back onto the rigged mesh."
            };
            SetButtonIcon(_btnImpSculptToggle, "res://assets/at-icons/mesh_select.svg", 16);
            ImporterStyle.ApplyFontSize(_btnImpSculptToggle, ImporterStyle.FontBody);
            _btnImpSculptToggle.Toggled += OnImportedSculptModeToggled;
            launchRow.AddChild(_btnImpSculptToggle);

            _btnImpSculptCardReset = new Button
            {
                Text = "Reset",
                ThemeTypeVariation = "ToolbarButton",
                Disabled = true,
                CustomMinimumSize = new Vector2(68, 34),
                TooltipText = "Reset all parametric reshape sliders and sculpted vertex offsets."
            };
            ImporterStyle.ApplyFontSize(_btnImpSculptCardReset, ImporterStyle.FontHint);
            _btnImpSculptCardReset.Pressed += OnImportedSculptResetPressed;
            launchRow.AddChild(_btnImpSculptCardReset);
            body.AddChild(launchRow);

            _lblImpSculptStatus = new Label { Text = "Original mesh dimensions (no reshape applied).", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            ImporterStyle.ApplyHintStyle(_lblImpSculptStatus);
            _secondaryLabels.Add(_lblImpSculptStatus);
            body.AddChild(_lblImpSculptStatus);

            return card;
        }

        private void BuildImportedSculptViewportHud(Control viewportPanel)
        {
            if (viewportPanel == null) return;

            _importedSculptViewportHud = CreateViewportFloatingTopHud(viewportPanel, out var row);

            // Brush Mode Chips
            string[] modeLabels = { "Grab", "Inflate", "Shrink", "Smooth", "Erase" };
            string[] modeTips =
            {
                "Grab / Pull — drag vertices freely to widen or stretch hems",
                "Inflate — push vertices outward along normals",
                "Shrink — tuck vertices inward along normals",
                "Smooth — relax sharp creases",
                "Erase — restore sculpted vertices back to original"
            };
            for (int i = 0; i < 5; i++)
            {
                int cap = i;
                var mb = CreateViewportHudButton(modeLabels[i], 46, 24, toggle: true, pressed: i == 0, tip: modeTips[i]);
                mb.Pressed += () => SetActiveImportedSculptMode((ImportedSculptBrushMode)cap);
                _hudSculptModeBtns[i] = mb;
                row.AddChild(mb);
            }

            row.AddChild(CreateViewportHudSeparator());

            // Compact Size & Power sliders
            var lblSz = new Label { Text = "Size:", SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            ImporterStyle.ApplyHintStyle(lblSz, wrap: false);
            row.AddChild(lblSz);
            _sliderImpSculptRadius = new HSlider
            {
                MinValue = 0.03, MaxValue = 0.35, Step = 0.005, Value = 0.12,
                CustomMinimumSize = new Vector2(72, 20),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = "Brush radius in meters ([ / ] keys)"
            };
            _lblImpSculptRadiusVal = new Label { Text = "0.12m", CustomMinimumSize = new Vector2(38, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            ImporterStyle.ApplyHintStyle(_lblImpSculptRadiusVal, wrap: false);
            _sliderImpSculptRadius.ValueChanged += (v) => { if (_lblImpSculptRadiusVal != null) _lblImpSculptRadiusVal.Text = $"{v:F2}m"; };
            row.AddChild(_sliderImpSculptRadius);
            row.AddChild(_lblImpSculptRadiusVal);

            var lblPw = new Label { Text = "Pwr:", SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            ImporterStyle.ApplyHintStyle(lblPw, wrap: false);
            row.AddChild(lblPw);
            _sliderImpSculptStrength = new HSlider
            {
                MinValue = 0.05, MaxValue = 1.0, Step = 0.01, Value = 0.55,
                CustomMinimumSize = new Vector2(60, 20),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = "Brush strength"
            };
            _lblImpSculptStrengthVal = new Label { Text = "55%", CustomMinimumSize = new Vector2(32, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            ImporterStyle.ApplyHintStyle(_lblImpSculptStrengthVal, wrap: false);
            _sliderImpSculptStrength.ValueChanged += (v) => { if (_lblImpSculptStrengthVal != null) _lblImpSculptStrengthVal.Text = $"{Mathf.RoundToInt((float)v * 100)}%"; };
            row.AddChild(_sliderImpSculptStrength);
            row.AddChild(_lblImpSculptStrengthVal);

            _btnImpSculptMirror = CreateViewportHudButton("Mirror", 48, 24, toggle: true, pressed: true, tip: "Mirror sculpt strokes across X");
            _btnImpSculptMirror.Toggled += (on) => ApplyHudSubtleToggleVisual(_btnImpSculptMirror, on);
            row.AddChild(_btnImpSculptMirror);

            row.AddChild(CreateViewportHudSeparator());

            // Hero Visibility (Show | Ghost | Hide)
            string[] hvLabels = { "Show", "Ghost", "Hide" };
            for (int hv = 0; hv < 3; hv++)
            {
                int modeIdx = hv;
                var hb = CreateViewportHudButton(hvLabels[hv], hv == 1 ? 42 : 38, 24, toggle: true, pressed: hv == _pinEditHeroVisMode, tip: "Original hero mesh visibility while sculpting (Shortcut: H)");
                hb.Pressed += () =>
                {
                    SetPinEditHeroVisMode(modeIdx);
                    SyncImportedSculptViewportHud();
                };
                _hudSculptHeroVisBtns[hv] = hb;
                row.AddChild(hb);
            }

            row.AddChild(CreateViewportHudSeparator());

            _btnImpSculptUndo = CreateViewportHudButton("", 26, 24, tip: "Undo last sculpt stroke (Ctrl+Z)");
            SetButtonIcon(_btnImpSculptUndo, "res://assets/at-icons/undo.svg", 14);
            _btnImpSculptUndo.Disabled = true;
            _btnImpSculptUndo.Pressed += OnImportedSculptUndoPressed;
            row.AddChild(_btnImpSculptUndo);

            _btnImpSculptReset = CreateViewportHudButton("Reset", 46, 24, tip: "Reset all reshape & sculpt offsets");
            _btnImpSculptReset.Disabled = true;
            _btnImpSculptReset.Pressed += OnImportedSculptResetPressed;
            row.AddChild(_btnImpSculptReset);

            var btnDone = CreateViewportHudButton("Done ✓", 58, 24, variation: "ImporterPrimaryButton", tip: "Finish sculpting and bake onto the rigged mesh (Esc)");
            btnDone.Pressed += () => { if (_btnImpSculptToggle != null) _btnImpSculptToggle.ButtonPressed = false; };
            row.AddChild(btnDone);

            SyncImportedSculptViewportHud();
        }

        private void SetActiveImportedSculptMode(ImportedSculptBrushMode mode)
        {
            _activeImpSculptMode = mode;
            SyncImportedSculptViewportHud();
        }

        private void SyncImportedSculptViewportHud()
        {
            for (int i = 0; i < _hudSculptModeBtns.Length; i++)
                ApplyHudSubtleToggleVisual(_hudSculptModeBtns[i], i == (int)_activeImpSculptMode);
            ApplyHudSubtleToggleVisual(_btnImpSculptMirror, _btnImpSculptMirror?.ButtonPressed ?? true);
            for (int hv = 0; hv < _hudSculptHeroVisBtns.Length; hv++)
                ApplyHudSubtleToggleVisual(_hudSculptHeroVisBtns[hv], hv == _pinEditHeroVisMode);
        }

        private void OnImportedSculptModeToggled(bool enabled)
        {
            if (_btnImpSculptToggle != null)
                _btnImpSculptToggle.ThemeTypeVariation = enabled ? "ImporterPrimaryButton" : "ToolbarButton";

            if (enabled)
            {
                if (_isMeshCutModeActive) ExitMeshCutMode();
                if (_btnWeightPaintMode != null && _btnWeightPaintMode.ButtonPressed)
                    _btnWeightPaintMode.ButtonPressed = false;
                if (_btnDeformBrushToggle != null && _btnDeformBrushToggle.ButtonPressed)
                    _btnDeformBrushToggle.ButtonPressed = false;
                if (_isEditReferenceBonesMode)
                    _isEditReferenceBonesMode = false;
                if (_optGizmoTarget != null) SelectGizmoTargetById(0);

                // Automatically switch preview into live Sculpt Pose (unposed _previewModelRoot visible, _previewRiggedContainer hidden)
                ApplyLandmarkPreviewState();
                RefreshImportedSculptPreviewFromCurrentSettings();
                if (_importedSculptViewportHud != null) _importedSculptViewportHud.Visible = true;
                SyncImportedSculptViewportHud();
                SetBottomStatus("3D Sculpt Mode active — Left-drag to sculpt live (Grab/Inflate/Shrink/Smooth/Erase). Press H for Hero Ghost, Esc or Done to bake.", StatusTone.Info);
            }
            else
            {
                _isImpSculptBrushing = false;
                _impGrabVerts.Clear();
                _impGrabInitialDeltas.Clear();
                UpdateBrushRingVisual(Vector3.Zero, Vector3.Up, 0.05f, false);
                if (_importedSculptViewportHud != null) _importedSculptViewportHud.Visible = false;

                // Restore pristine un-baked preview mesh before ExecuteLiveRigUpdate bakes final sculpts into rigged mesh!
                _impSculptOriginalMeshRestore?.Invoke();
                _impSculptOriginalMeshRestore = null;
                _impSculptCaches.Clear();

                ExecuteLiveRigUpdate();
                ApplyLandmarkPreviewState();
                SetBottomStatus("Baked sculpted mesh onto hero rig.", StatusTone.Success);
            }
        }

        private void RefreshImportedSculptPreviewFromCurrentSettings()
        {
            _impSculptOriginalMeshRestore?.Invoke();
            _impSculptOriginalMeshRestore = null;
            if (_previewModelInstance == null || !GodotObject.IsInstanceValid(_previewModelInstance)) return;

            var opts = GatherRiggingOptions();
            // Keep the sculpted preview applied directly on _previewModelInstance while sculpting so every stroke & slider is 100% visible!
            _impSculptOriginalMeshRestore = CustomModelAutoRigger.ApplyImportedMeshSculptToScene(_previewModelInstance, opts);
            RebuildImportedSculptCaches();
        }

        private void JumpToImportedSculptFromMeshesTab()
        {
            if (_isMeshCutModeActive) ExitMeshCutMode();
            if (_rightTabs != null && _rightTabs.GetTabCount() > 0)
                _rightTabs.CurrentTab = 0;
            if (_btnImpSculptToggle != null && !_btnImpSculptToggle.ButtonPressed)
                _btnImpSculptToggle.ButtonPressed = true;
        }

        private void UpdateImportedSculptStatusLabel()
        {
            int vCnt = 0;
            foreach (var map in _importedSculptDeltas.Values) vCnt += map.Count;
            bool hasParam = MathF.Abs((float)(_sliderImpBottomFlare?.Value ?? 0.0)) > 0.001f ||
                            MathF.Abs((float)(_sliderImpTopFlare?.Value ?? 0.0)) > 0.001f ||
                            MathF.Abs((float)(_sliderImpInflate?.Value ?? 0.0)) > 0.0005f;
            if (_lblImpSculptStatus != null)
            {
                if (vCnt > 0 && hasParam)
                    _lblImpSculptStatus.Text = $"Parametric fit + {vCnt:N0} sculpted vertex offset(s) active.";
                else if (vCnt > 0)
                    _lblImpSculptStatus.Text = $"Sculpted {vCnt:N0} vertex offset(s) across {_importedSculptDeltas.Count} surface(s).";
                else if (hasParam)
                    _lblImpSculptStatus.Text = "Parametric reshape active (Bottom Flare / Top Width / Inflate).";
                else
                    _lblImpSculptStatus.Text = "Original mesh dimensions (no reshape applied).";
            }
            if (_btnImpSculptUndo != null) _btnImpSculptUndo.Disabled = _importedSculptUndoStack.Count == 0;
            bool canReset = vCnt > 0 || hasParam;
            if (_btnImpSculptReset != null) _btnImpSculptReset.Disabled = !canReset;
            if (_btnImpSculptCardReset != null) _btnImpSculptCardReset.Disabled = !canReset;
        }

        private void OnImportedSculptUndoPressed()
        {
            if (_importedSculptUndoStack.Count == 0) return;
            var prev = _importedSculptUndoStack[^1];
            _importedSculptUndoStack.RemoveAt(_importedSculptUndoStack.Count - 1);
            _importedSculptDeltas.Clear();
            foreach (var (k, v) in prev) _importedSculptDeltas[k] = new Dictionary<int, Vector3>(v);
            UpdateImportedSculptStatusLabel();
            if (IsImportedSculptActive())
                RefreshImportedSculptPreviewFromCurrentSettings();
            else
                ExecuteLiveRigUpdate();
        }

        private void OnImportedSculptResetPressed()
        {
            if (_importedSculptDeltas.Count > 0)
            {
                _importedSculptUndoStack.Add(CloneImportedSculptDeltas(_importedSculptDeltas) ?? new());
                if (_importedSculptUndoStack.Count > 30) _importedSculptUndoStack.RemoveAt(0);
            }
            _importedSculptDeltas.Clear();
            _isSyncingImpSculptUi = true;
            try
            {
                _sliderImpBottomFlare?.SetValueNoSignal(0.0);
                if (_lblImpBottomFlareVal != null) _lblImpBottomFlareVal.Text = "0%";
                _sliderImpTopFlare?.SetValueNoSignal(0.0);
                if (_lblImpTopFlareVal != null) _lblImpTopFlareVal.Text = "0%";
                _sliderImpInflate?.SetValueNoSignal(0.0);
                if (_lblImpInflateVal != null) _lblImpInflateVal.Text = "0.0cm";
            }
            finally
            {
                _isSyncingImpSculptUi = false;
            }
            UpdateImportedSculptStatusLabel();
            if (IsImportedSculptActive())
                RefreshImportedSculptPreviewFromCurrentSettings();
            else
                ExecuteLiveRigUpdate();
        }

        private void ResetImportedSculptStateForModalOpen()
        {
            _impSculptOriginalMeshRestore?.Invoke();
            _impSculptOriginalMeshRestore = null;
            _importedSculptDeltas.Clear();
            _importedSculptUndoStack.Clear();
            _impSculptCaches.Clear();
            _isImpSculptBrushing = false;
            _pendingImpSculptUndoSnapshot = null;
            if (_importedSculptViewportHud != null) _importedSculptViewportHud.Visible = false;
            _isSyncingImpSculptUi = true;
            try
            {
                _btnImpSculptToggle?.SetPressedNoSignal(false);
                if (_btnImpSculptToggle != null) _btnImpSculptToggle.ThemeTypeVariation = "ToolbarButton";
                _sliderImpBottomFlare?.SetValueNoSignal(0.0);
                if (_lblImpBottomFlareVal != null) _lblImpBottomFlareVal.Text = "0%";
                _sliderImpTopFlare?.SetValueNoSignal(0.0);
                if (_lblImpTopFlareVal != null) _lblImpTopFlareVal.Text = "0%";
                _sliderImpInflate?.SetValueNoSignal(0.0);
                if (_lblImpInflateVal != null) _lblImpInflateVal.Text = "0.0cm";
            }
            finally
            {
                _isSyncingImpSculptUi = false;
            }
            UpdateImportedSculptStatusLabel();
        }

        private void RestoreImportedSculptOptions(CustomModelAutoRigger.RiggingOptions options)
        {
            if (options == null) return;
            _importedSculptDeltas.Clear();
            _importedSculptUndoStack.Clear();
            if (options.ImportedMeshSculptDeltas != null)
            {
                foreach (var (sk, vMap) in options.ImportedMeshSculptDeltas)
                {
                    if (!string.IsNullOrEmpty(sk) && vMap != null && vMap.Count > 0)
                        _importedSculptDeltas[sk] = new Dictionary<int, Vector3>(vMap);
                }
            }
            _isSyncingImpSculptUi = true;
            try
            {
                if (_sliderImpBottomFlare != null)
                {
                    _sliderImpBottomFlare.SetValueNoSignal(options.ImportedMeshBottomFlare);
                    if (_lblImpBottomFlareVal != null) _lblImpBottomFlareVal.Text = $"{(options.ImportedMeshBottomFlare > 0 ? "+" : "")}{Mathf.RoundToInt(options.ImportedMeshBottomFlare * 100)}%";
                }
                if (_sliderImpTopFlare != null)
                {
                    _sliderImpTopFlare.SetValueNoSignal(options.ImportedMeshTopFlare);
                    if (_lblImpTopFlareVal != null) _lblImpTopFlareVal.Text = $"{(options.ImportedMeshTopFlare > 0 ? "+" : "")}{Mathf.RoundToInt(options.ImportedMeshTopFlare * 100)}%";
                }
                if (_sliderImpInflate != null)
                {
                    _sliderImpInflate.SetValueNoSignal(options.ImportedMeshInflateMeters);
                    if (_lblImpInflateVal != null) _lblImpInflateVal.Text = $"{(options.ImportedMeshInflateMeters > 0 ? "+" : "")}{options.ImportedMeshInflateMeters * 100f:F1}cm";
                }
            }
            finally
            {
                _isSyncingImpSculptUi = false;
            }
            UpdateImportedSculptStatusLabel();
        }

        private void RebuildImportedSculptCaches()
        {
            _impSculptCaches.Clear();
            if (_previewModelInstance == null || !GodotObject.IsInstanceValid(_previewModelInstance)) return;

            var alignedXf = BuildCurrentAlignedTransform();
            var worldRootXf = (_previewWorldRoot != null && GodotObject.IsInstanceValid(_previewWorldRoot))
                ? _previewWorldRoot.GlobalTransform
                : Transform3D.Identity;

            var riggedByKey = new Dictionary<string, MeshInstance3D>(StringComparer.OrdinalIgnoreCase);
            if (_previewRiggedContainer != null && GodotObject.IsInstanceValid(_previewRiggedContainer) && _previewRiggedContainer.Visible)
            {
                foreach (var rmi in CustomModelAutoRigger.CollectMeshes(_previewRiggedContainer))
                {
                    if (rmi?.Mesh == null) continue;
                    string srcName = rmi.HasMeta("SourceMeshName") ? rmi.GetMeta("SourceMeshName").AsString() : rmi.Name.ToString();
                    int srcSurf = rmi.HasMeta("SourceSurfaceIndex") ? rmi.GetMeta("SourceSurfaceIndex").AsInt32() : 0;
                    riggedByKey[CustomModelAutoRigger.MakeSurfaceKeepKey(srcName, srcSurf)] = rmi;
                }
            }

            var activeFilter = GetActiveImportedSubmeshFilter();
            var srcMeshes = CustomModelAutoRigger.CollectMeshes(_previewModelInstance);
            // In Sculpt Mode, _previewModelInstance already has parametric + current sculpt deltas applied via RefreshImportedSculptPreviewFromCurrentSettings()!
            // Store BaseLocalVerts = current local verts MINUS existing _importedSculptDeltas so subsequent dabs accumulate cleanly with zero double-offset.
            foreach (var srcMi in srcMeshes)
            {
                if (srcMi?.Mesh == null) continue;
                string mName = srcMi.Name.ToString();
                if (activeFilter != null && activeFilter.Count > 0 && !activeFilter.Contains(mName)) continue;

                Transform3D localToHero = CustomModelAutoRigger.ComputeImportedMeshToHeroTransform(srcMi, _previewModelInstance, alignedXf);
                Transform3D localToWorld = worldRootXf * localToHero;
                Transform3D worldToLocal = localToWorld.AffineInverse();
                Basis normToWorld = MathF.Abs(localToWorld.Basis.Determinant()) > 1e-8f
                    ? localToWorld.Basis.Inverse().Transposed()
                    : localToWorld.Basis;

                for (int s = 0; s < srcMi.Mesh.GetSurfaceCount(); s++)
                {
                    var arrays = srcMi.Mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.Vertex) continue;
                    var verts = CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    if (verts == null || verts.Length == 0) continue;
                    var norms = arrays.Count > (int)Mesh.ArrayType.Normal ? CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Normal]) : null;
                    var idx = arrays.Count > (int)Mesh.ArrayType.Index ? CustomModelAutoRigger.GetInt32Array(arrays[(int)Mesh.ArrayType.Index]) : null;
                    if (idx == null || idx.Length < 3)
                    {
                        idx = new int[verts.Length - (verts.Length % 3)];
                        for (int i = 0; i < idx.Length; i++) idx[i] = i;
                    }

                    string surfKey = CustomModelAutoRigger.MakeSurfaceKeepKey(mName, s);
                    _importedSculptDeltas.TryGetValue(surfKey, out var existingMap);
                    var baseLocalVerts = new Vector3[verts.Length];
                    var wVerts = new Vector3[verts.Length];
                    var wNorms = new Vector3[verts.Length];
                    for (int v = 0; v < verts.Length; v++)
                    {
                        Vector3 d = (existingMap != null && existingMap.TryGetValue(v, out var exD)) ? exD : Vector3.Zero;
                        baseLocalVerts[v] = verts[v] - d;
                        wVerts[v] = localToWorld * verts[v];
                        Vector3 n = (norms != null && v < norms.Length) ? (normToWorld * norms[v]) : Vector3.Up;
                        wNorms[v] = n.LengthSquared() > 1e-8f ? n.Normalized() : Vector3.Up;
                    }

                    // Build 1-ring vertex adjacency for Smooth brush
                    var adjSets = new HashSet<int>[verts.Length];
                    for (int t = 0; t + 2 < idx.Length; t += 3)
                    {
                        int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                        if ((uint)a >= (uint)verts.Length || (uint)b >= (uint)verts.Length || (uint)c >= (uint)verts.Length) continue;
                        (adjSets[a] ??= new HashSet<int>()).Add(b); adjSets[a].Add(c);
                        (adjSets[b] ??= new HashSet<int>()).Add(a); adjSets[b].Add(c);
                        (adjSets[c] ??= new HashSet<int>()).Add(a); adjSets[c].Add(b);
                    }
                    var neighbors = new int[verts.Length][];
                    for (int v = 0; v < verts.Length; v++)
                        neighbors[v] = adjSets[v] != null ? adjSets[v].ToArray() : Array.Empty<int>();

                    _impSculptCaches.Add(new ImpSculptSurfaceCache
                    {
                        SourceMi = srcMi,
                        DisplayMi = srcMi, // Always update visible _previewModelInstance directly at 60 FPS!
                        SurfaceIndex = s,
                        SurfaceKey = surfKey,
                        LocalToWorld = localToWorld,
                        WorldToLocal = worldToLocal,
                        BaseLocalVerts = baseLocalVerts,
                        CurrentWorldVerts = wVerts,
                        CurrentWorldNorms = wNorms,
                        Indices = idx,
                        Neighbors = neighbors
                    });
                }
            }
        }

        private bool RaycastImportedSculpt(Vector2 mousePos, out ImpSculptSurfaceCache hitCache, out Vector3 hitWorld, out Vector3 hitNorm)
        {
            hitCache = null;
            hitWorld = Vector3.Zero;
            hitNorm = Vector3.Up;
            if (_previewCamera == null || _impSculptCaches.Count == 0) return false;

            Vector3 rayOrig = _previewCamera.ProjectRayOrigin(mousePos);
            Vector3 rayDir = _previewCamera.ProjectRayNormal(mousePos);
            float bestT = float.MaxValue;

            foreach (var c in _impSculptCaches)
            {
                var verts = c.CurrentWorldVerts;
                var idx = c.Indices;
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int a = idx[t], b = idx[t + 1], d = idx[t + 2];
                    if (CustomWeightPaintEditor.RayTriangle(rayOrig, rayDir, verts[a], verts[b], verts[d], out float hitT) && hitT < bestT)
                    {
                        bestT = hitT;
                        hitCache = c;
                        hitWorld = rayOrig + rayDir * hitT;
                        Vector3 n = c.CurrentWorldNorms[a] + c.CurrentWorldNorms[b] + c.CurrentWorldNorms[d];
                        hitNorm = n.LengthSquared() > 1e-8f ? n.Normalized() : Vector3.Up;
                    }
                }
            }
            return hitCache != null;
        }

        private bool HandleImportedSculptInput(InputEvent @event)
        {
            if (!IsImportedSculptActive() || _previewCamera == null) return false;

            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    if (_impSculptCaches.Count == 0) RebuildImportedSculptCaches();
                    if (RaycastImportedSculpt(mb.Position, out _, out var hitPt, out var hitNorm))
                    {
                        _isOrbiting = false;
                        _isPanning = false;
                        _isImpSculptBrushing = true;
                        _pendingImpSculptUndoSnapshot = CloneImportedSculptDeltas(_importedSculptDeltas) ?? new();

                        var mode = _activeImpSculptMode;

                        if (mode == ImportedSculptBrushMode.Grab)
                        {
                            BeginImportedGrabStroke(hitPt);
                        }
                        else
                        {
                            ApplyImportedSculptDab(hitPt, hitNorm, mode);
                        }
                        UpdateBrushRingVisual(hitPt, hitNorm, (float)(_sliderImpSculptRadius?.Value ?? 0.12), true);
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
                }
                else if (_isImpSculptBrushing)
                {
                    _isImpSculptBrushing = false;
                    _impGrabVerts.Clear();
                    _impGrabInitialDeltas.Clear();
                    if (_pendingImpSculptUndoSnapshot != null)
                    {
                        _importedSculptUndoStack.Add(_pendingImpSculptUndoSnapshot);
                        if (_importedSculptUndoStack.Count > 30) _importedSculptUndoStack.RemoveAt(0);
                        _pendingImpSculptUndoSnapshot = null;
                    }
                    UpdateImportedSculptStatusLabel();
                    // Stay seamlessly in live Sculpt Pose without Auto-Snap resetting the view!
                    // Final rig bake runs automatically when exiting Sculpt Mode (Done / Esc).
                    GetViewport().SetInputAsHandled();
                    return true;
                }
            }
            else if (@event is InputEventMouseMotion mm)
            {
                if (_isOrbiting || _isPanning) return false;
                float radius = (float)(_sliderImpSculptRadius?.Value ?? 0.12);

                if (_isImpSculptBrushing)
                {
                    var mode = _activeImpSculptMode;

                    if (mode == ImportedSculptBrushMode.Grab)
                    {
                        UpdateImportedGrabStroke(mm.Position);
                    }
                    else if (RaycastImportedSculpt(mm.Position, out _, out var hitPt, out var hitNorm))
                    {
                        ApplyImportedSculptDab(hitPt, hitNorm, mode);
                        UpdateBrushRingVisual(hitPt, hitNorm, radius, true);
                    }
                    GetViewport().SetInputAsHandled();
                    return true;
                }
                else if (RaycastImportedSculpt(mm.Position, out _, out var hoverPt, out var hoverNorm))
                {
                    UpdateBrushRingVisual(hoverPt, hoverNorm, radius, true);
                }
                else
                {
                    UpdateBrushRingVisual(Vector3.Zero, Vector3.Up, 0.05f, false);
                }
            }
            return false;
        }

        private void BeginImportedGrabStroke(Vector3 hitWorld)
        {
            _impGrabStartWorld = hitWorld;
            _impGrabPlaneNormal = (-_previewCamera.GlobalTransform.Basis.Z).Normalized();
            _impGrabVerts.Clear();
            _impGrabInitialDeltas.Clear();

            float radius = (float)(_sliderImpSculptRadius?.Value ?? 0.12);
            float strength = (float)(_sliderImpSculptStrength?.Value ?? 0.55);
            bool mirrorX = _btnImpSculptMirror?.ButtonPressed ?? true;
            float rSq = radius * radius;

            Transform3D heroGlobal = (_previewHeroClone != null && GodotObject.IsInstanceValid(_previewHeroClone))
                ? _previewHeroClone.GlobalTransform
                : Transform3D.Identity;
            Transform3D heroInv = heroGlobal.AffineInverse();
            Vector3 hitHero = heroInv * hitWorld;
            Vector3 mirWorld = heroGlobal * new Vector3(-hitHero.X, hitHero.Y, hitHero.Z);

            foreach (var c in _impSculptCaches)
            {
                _importedSculptDeltas.TryGetValue(c.SurfaceKey, out var existingMap);
                for (int v = 0; v < c.CurrentWorldVerts.Length; v++)
                {
                    Vector3 wp = c.CurrentWorldVerts[v];
                    float d1Sq = wp.DistanceSquaredTo(hitWorld);
                    float d2Sq = mirrorX ? wp.DistanceSquaredTo(mirWorld) : float.MaxValue;
                    bool isMir = mirrorX && d2Sq < d1Sq;
                    float bestSq = isMir ? d2Sq : d1Sq;
                    if (bestSq >= rSq) continue;

                    float t = 1f - MathF.Sqrt(bestSq) / radius;
                    float w = t * t * (3f - 2f * t) * Math.Clamp(strength * 1.4f, 0.1f, 1.0f);
                    _impGrabVerts.Add((c, v, w, isMir));
                    Vector3 initDelta = (existingMap != null && existingMap.TryGetValue(v, out var d)) ? d : Vector3.Zero;
                    _impGrabInitialDeltas[(c, v)] = initDelta;
                }
            }
        }

        private void UpdateImportedGrabStroke(Vector2 mousePos)
        {
            if (_impGrabVerts.Count == 0 || _previewCamera == null) return;
            Vector3 rayOrig = _previewCamera.ProjectRayOrigin(mousePos);
            Vector3 rayDir = _previewCamera.ProjectRayNormal(mousePos);
            float denom = rayDir.Dot(_impGrabPlaneNormal);
            if (MathF.Abs(denom) < 1e-5f) return;
            float tHit = (_impGrabStartWorld - rayOrig).Dot(_impGrabPlaneNormal) / denom;
            if (tHit < 0f) return;

            Vector3 curWorld = rayOrig + rayDir * tHit;
            Vector3 deltaWorld = curWorld - _impGrabStartWorld;

            Transform3D heroGlobal = (_previewHeroClone != null && GodotObject.IsInstanceValid(_previewHeroClone))
                ? _previewHeroClone.GlobalTransform
                : Transform3D.Identity;
            Transform3D heroInv = heroGlobal.AffineInverse();
            Vector3 deltaHero = heroInv.Basis * deltaWorld;
            Vector3 mirDeltaWorld = heroGlobal.Basis * new Vector3(-deltaHero.X, deltaHero.Y, deltaHero.Z);

            var dirtyCaches = new HashSet<ImpSculptSurfaceCache>();
            foreach (var (c, v, w, isMir) in _impGrabVerts)
            {
                if (!_importedSculptDeltas.TryGetValue(c.SurfaceKey, out var map))
                {
                    map = new Dictionary<int, Vector3>();
                    _importedSculptDeltas[c.SurfaceKey] = map;
                }
                Vector3 wShift = (isMir ? mirDeltaWorld : deltaWorld) * w;
                Vector3 localShift = c.WorldToLocal.Basis * wShift;
                Vector3 initD = _impGrabInitialDeltas[(c, v)];
                map[v] = initD + localShift;
                dirtyCaches.Add(c);
            }

            if (dirtyCaches.Count > 0)
            {
                ApplyLiveImportedSculptPreview(dirtyCaches);
                UpdateBrushRingVisual(curWorld, _impGrabPlaneNormal, (float)(_sliderImpSculptRadius?.Value ?? 0.12), true);
            }
        }

        private void ApplyImportedSculptDab(Vector3 hitWorld, Vector3 hitNorm, ImportedSculptBrushMode mode)
        {
            float radius = (float)(_sliderImpSculptRadius?.Value ?? 0.12);
            float strength = (float)(_sliderImpSculptStrength?.Value ?? 0.55);
            bool mirrorX = _btnImpSculptMirror?.ButtonPressed ?? true;
            float rSq = radius * radius;
            float stepMeters = MathF.Max(0.002f, radius * 0.12f) * strength;

            Transform3D heroGlobal = (_previewHeroClone != null && GodotObject.IsInstanceValid(_previewHeroClone))
                ? _previewHeroClone.GlobalTransform
                : Transform3D.Identity;
            Transform3D heroInv = heroGlobal.AffineInverse();
            Vector3 hitHero = heroInv * hitWorld;
            Vector3 mirWorld = heroGlobal * new Vector3(-hitHero.X, hitHero.Y, hitHero.Z);

            var dirtyCaches = new HashSet<ImpSculptSurfaceCache>();
            foreach (var c in _impSculptCaches)
            {
                Dictionary<int, Vector3> map = null;
                bool touched = false;
                for (int v = 0; v < c.CurrentWorldVerts.Length; v++)
                {
                    Vector3 wp = c.CurrentWorldVerts[v];
                    float d1Sq = wp.DistanceSquaredTo(hitWorld);
                    float d2Sq = mirrorX ? wp.DistanceSquaredTo(mirWorld) : float.MaxValue;
                    float bestSq = MathF.Min(d1Sq, d2Sq);
                    if (bestSq >= rSq) continue;

                    float t = 1f - MathF.Sqrt(bestSq) / radius;
                    float falloff = t * t * (3f - 2f * t);

                    if (map == null && !_importedSculptDeltas.TryGetValue(c.SurfaceKey, out map))
                    {
                        map = new Dictionary<int, Vector3>();
                        _importedSculptDeltas[c.SurfaceKey] = map;
                    }
                    Vector3 curDelta = map.TryGetValue(v, out var ex) ? ex : Vector3.Zero;

                    if (mode == ImportedSculptBrushMode.Erase)
                    {
                        if (curDelta.LengthSquared() < 1e-10f) continue;
                        Vector3 next = curDelta * Math.Clamp(1f - falloff * strength * 0.5f, 0f, 1f);
                        if (next.LengthSquared() < 1e-9f) map.Remove(v);
                        else map[v] = next;
                        touched = true;
                    }
                    else if (mode == ImportedSculptBrushMode.Smooth)
                    {
                        var nbs = c.Neighbors[v];
                        if (nbs.Length == 0) continue;
                        Vector3 avgWorld = Vector3.Zero;
                        for (int ni = 0; ni < nbs.Length; ni++) avgWorld += c.CurrentWorldVerts[nbs[ni]];
                        avgWorld /= nbs.Length;
                        Vector3 shiftLocal = c.WorldToLocal.Basis * ((avgWorld - wp) * (falloff * strength * 0.35f));
                        map[v] = curDelta + shiftLocal;
                        touched = true;
                    }
                    else
                    {
                        float sign = mode == ImportedSculptBrushMode.Shrink ? -1f : 1f;
                        Vector3 wn = c.CurrentWorldNorms[v];
                        Vector3 shiftWorld = wn * (sign * stepMeters * falloff);
                        map[v] = curDelta + (c.WorldToLocal.Basis * shiftWorld);
                        touched = true;
                    }
                }
                if (touched) dirtyCaches.Add(c);
            }

            if (dirtyCaches.Count > 0)
            {
                ApplyLiveImportedSculptPreview(dirtyCaches);
            }
        }

        private void ApplyLiveImportedSculptPreview(HashSet<ImpSculptSurfaceCache> dirtyCaches)
        {
            var heroInv = (_previewHeroClone != null && GodotObject.IsInstanceValid(_previewHeroClone))
                ? _previewHeroClone.GlobalTransform.AffineInverse()
                : Transform3D.Identity;

            foreach (var c in dirtyCaches)
            {
                if (c.DisplayMi == null || !GodotObject.IsInstanceValid(c.DisplayMi) || c.DisplayMi.Mesh == null) continue;
                if (!_importedSculptDeltas.TryGetValue(c.SurfaceKey, out var map)) continue;

                for (int v = 0; v < c.BaseLocalVerts.Length; v++)
                {
                    Vector3 d = map.TryGetValue(v, out var dl) ? dl : Vector3.Zero;
                    c.CurrentWorldVerts[v] = c.LocalToWorld * (c.BaseLocalVerts[v] + d);
                }

                // Instant 60 FPS visual update directly on DisplayMi while dragging (re-rigged cleanly on release)
                int targetSurf = c.DisplayMi == c.SourceMi ? c.SurfaceIndex : 0;
                if (targetSurf < c.DisplayMi.Mesh.GetSurfaceCount())
                {
                    var arrays = c.DisplayMi.Mesh.SurfaceGetArrays(targetSurf);
                    var curVerts = arrays != null && arrays.Count > (int)Mesh.ArrayType.Vertex
                        ? CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex])
                        : null;
                    if (curVerts != null && curVerts.Length == c.BaseLocalVerts.Length)
                    {
                        var dispInv = c.DisplayMi.IsInsideTree() ? c.DisplayMi.GlobalTransform.AffineInverse() : heroInv;
                        var nextVerts = new Vector3[curVerts.Length];
                        for (int v = 0; v < curVerts.Length; v++)
                            nextVerts[v] = dispInv * c.CurrentWorldVerts[v];
                        arrays[(int)Mesh.ArrayType.Vertex] = nextVerts;

                        Mesh.ArrayFormat fmt = default;
                        if (arrays.Count > (int)Mesh.ArrayType.Bones && arrays[(int)Mesh.ArrayType.Bones].VariantType != Variant.Type.Nil)
                        {
                            var bArr = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                            if (bArr != null && bArr.Length == nextVerts.Length * 8)
                                fmt |= Mesh.ArrayFormat.FlagUse8BoneWeights;
                        }
                        var liveMesh = new ArrayMesh();
                        for (int s = 0; s < c.DisplayMi.Mesh.GetSurfaceCount(); s++)
                        {
                            var sArr = s == targetSurf ? arrays : c.DisplayMi.Mesh.SurfaceGetArrays(s);
                            var sMat = c.DisplayMi.Mesh.SurfaceGetMaterial(s);
                            liveMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, sArr, null, null, s == targetSurf ? fmt : default);
                            if (sMat != null && liveMesh.GetSurfaceCount() > 0)
                                liveMesh.SurfaceSetMaterial(liveMesh.GetSurfaceCount() - 1, sMat);
                        }
                        c.DisplayMi.Mesh = liveMesh;
                    }
                }
            }
        }
    }
}
