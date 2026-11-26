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
        // Per-Mesh Reshape, Non-Uniform Scale, 3D Sculpt Brush & Face Edit HUD
        // =========================================================================

        private bool _isImpSculptModeActive;
        private string _activeImpSculptTargetMesh; // null or empty = All Active Meshes
        private bool _impSculptIsolateTarget;
        private bool _impSculptShowShapeDrawer;

        private Control _importedSculptViewportHud;
        private Button _btnHudSculptModeBrush;
        private Button _btnHudSculptModeFaces;
        private OptionButton _optHudSculptTarget;
        private Button _btnHudSculptIsolate;

        private HBoxContainer _boxHudBrushTools;
        private HBoxContainer _boxHudFaceTools;
        private HBoxContainer _boxHudBrushSubRow;
        private HBoxContainer _boxHudFaceSubRow;
        private HBoxContainer _boxHudShapeScaleRow;

        private readonly Button[] _hudSculptModeBtns = new Button[5];
        private readonly Button[] _hudFaceSelectBtns = new Button[4];
        private readonly Button[] _hudFaceTransformBtns = new Button[3];
        private readonly Button[] _hudFaceAxisBtns = new Button[4];
        private readonly Button[] _hudSculptHeroVisBtns = new Button[3];

        private ImportedSculptBrushMode _activeImpSculptMode = ImportedSculptBrushMode.Grab;
        private HSlider _sliderImpSculptRadius;
        private Label _lblImpSculptRadiusVal;
        private HSlider _sliderImpSculptStrength;
        private Label _lblImpSculptStrengthVal;
        private Button _btnImpSculptMirror;
        private Button _btnHudShapeDrawerToggle;
        private Button _btnImpSculptUndo;
        private Button _btnImpSculptReset;

        // Face Edit sub-row controls
        private Button _btnFaceSelThrough;
        private Button _btnFacePropEdit;
        private HSlider _sliderFacePropRadius;
        private Label _lblFacePropRadiusVal;
        private Label _lblFaceSelCount;

        // Per-mesh Non-Uniform Scale & Parametric Fit controls inside Shape & Scale drawer
        private SpinBox _spinMeshScaleX;
        private SpinBox _spinMeshScaleY;
        private SpinBox _spinMeshScaleZ;
        private HSlider _sliderImpBottomFlare;
        private Label _lblImpBottomFlareVal;
        private HSlider _sliderImpTopFlare;
        private Label _lblImpTopFlareVal;
        private HSlider _sliderImpInflate;
        private Label _lblImpInflateVal;

        // Global fallback parametric values (backward compat with older .dpp) + Per-Mesh dictionaries
        private float _globalImpBottomFlare;
        private float _globalImpTopFlare;
        private float _globalImpInflateMeters;
        private readonly Dictionary<string, Vector3> _perMeshScaleXyz = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, float> _perMeshBottomFlare = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, float> _perMeshTopFlare = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, float> _perMeshInflateMeters = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, Dictionary<int, Vector3>> _importedSculptDeltas = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Dictionary<string, Dictionary<int, Vector3>>> _importedSculptUndoStack = new();
        private Dictionary<string, Dictionary<int, Vector3>> _pendingImpSculptUndoSnapshot;
        private bool _isImpSculptBrushing;
        private bool _isSyncingImpSculptUi;
        private Action _impSculptOriginalMeshRestore;

        // Live grab-drag state
        private Vector3 _impGrabStartWorld;
        private Vector3 _impGrabPlaneNormal;
        private readonly List<(ImpSculptSurfaceCache Cache, int VertIdx, float Weight, bool IsMirrored)> _impGrabVerts = new();
        private readonly Dictionary<(ImpSculptSurfaceCache Cache, int VertIdx), Vector3> _impGrabInitialDeltas = new();

        private sealed class ImpSculptSurfaceCache
        {
            public MeshInstance3D SourceMi;
            public MeshInstance3D DisplayMi;
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

        private bool IsImportedSculptActive() => _isImpSculptModeActive;

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

        private void BuildImportedSculptViewportHud(Control viewportPanel)
        {
            if (viewportPanel == null) return;

            var margin = new MarginContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            margin.AddThemeConstantOverride("margin_left", 10);
            margin.AddThemeConstantOverride("margin_right", 10);
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
            _importedSculptViewportHud = panel;

            var pad = new MarginContainer();
            pad.AddThemeConstantOverride("margin_left", 10);
            pad.AddThemeConstantOverride("margin_right", 10);
            pad.AddThemeConstantOverride("margin_top", 6);
            pad.AddThemeConstantOverride("margin_bottom", 6);
            panel.AddChild(pad);

            var root = new VBoxContainer();
            root.AddThemeConstantOverride("separation", 5);
            pad.AddChild(root);

            // =====================================================================
            // ROW 1: [Brush | Edit Faces] | Target + Isolate | Tools | Mirror | Shape&Scale | Hero | Undo | Reset | Done
            // =====================================================================
            var row1 = new HBoxContainer();
            row1.AddThemeConstantOverride("separation", 5);
            root.AddChild(row1);

            _btnHudSculptModeBrush = CreateViewportHudButton("Brush", 48, 24, toggle: true, pressed: true, tip: "Freeform 3D sculpt brushes (Grab, Inflate, Shrink, Smooth, Erase)");
            _btnHudSculptModeBrush.Pressed += () => SetSculptSubMode(faceEdit: false);
            row1.AddChild(_btnHudSculptModeBrush);

            _btnHudSculptModeFaces = CreateViewportHudButton("Edit Faces", 68, 24, toggle: true, pressed: false, tip: "Blender-style Face Selection & Proportional Drag (Lasso/Box/Paint/Island + G/S/Alt+S)");
            _btnHudSculptModeFaces.Pressed += () => SetSculptSubMode(faceEdit: true);
            row1.AddChild(_btnHudSculptModeFaces);

            row1.AddChild(CreateViewportHudSeparator());

            _optHudSculptTarget = new OptionButton { CustomMinimumSize = new Vector2(108, 24), FocusMode = Control.FocusModeEnum.None, TooltipText = "Mesh targeted by Sculpt, Face Edit, and per-mesh Scale/Reshape" };
            ConfigureOptionButton(_optHudSculptTarget, 108);
            _optHudSculptTarget.ItemSelected += (idx) =>
            {
                if (_isSyncingImpSculptUi) return;
                string meta = _optHudSculptTarget.GetItemMetadata((int)idx).AsString();
                _activeImpSculptTargetMesh = string.IsNullOrEmpty(meta) ? null : meta;
                ClearFaceEditSelection(refreshVisual: false);
                ApplyImpSculptIsolationVisibility();
                RefreshImportedSculptPreviewFromCurrentSettings();
                SyncShapeScaleControlsFromTarget();
                SyncImportedSculptViewportHud();
            };
            row1.AddChild(_optHudSculptTarget);

            _btnHudSculptIsolate = CreateViewportHudButton("Isolate", 50, 24, toggle: true, pressed: false, tip: "Temporarily hide other submeshes while editing this mesh");
            _btnHudSculptIsolate.Toggled += (on) =>
            {
                _impSculptIsolateTarget = on;
                ApplyImpSculptIsolationVisibility();
                ApplyHudSubtleToggleVisual(_btnHudSculptIsolate, on);
            };
            row1.AddChild(_btnHudSculptIsolate);

            row1.AddChild(CreateViewportHudSeparator());

            // Brush tool chips
            _boxHudBrushTools = new HBoxContainer();
            _boxHudBrushTools.AddThemeConstantOverride("separation", 4);
            string[] modeLabels = { "Grab", "Inflate", "Shrink", "Smooth", "Erase" };
            string[] modeTips =
            {
                "Grab / Pull — drag vertices freely",
                "Inflate — push vertices outward along normals",
                "Shrink — tuck vertices inward along normals",
                "Smooth — relax creases",
                "Erase — restore sculpted vertices"
            };
            for (int i = 0; i < 5; i++)
            {
                int cap = i;
                var mb = CreateViewportHudButton(modeLabels[i], 44, 24, toggle: true, pressed: i == 0, tip: modeTips[i]);
                mb.Pressed += () => SetActiveImportedSculptMode((ImportedSculptBrushMode)cap);
                _hudSculptModeBtns[i] = mb;
                _boxHudBrushTools.AddChild(mb);
            }
            row1.AddChild(_boxHudBrushTools);

            // Face Edit tool chips
            _boxHudFaceTools = new HBoxContainer { Visible = false };
            _boxHudFaceTools.AddThemeConstantOverride("separation", 4);
            string[] fSelLabels = { "Lasso", "Box", "Paint", "Island" };
            string[] fSelTips = { "Lasso face select (L)", "Box face select (B)", "Brush paint select (C)", "Linked island select (I)" };
            for (int i = 0; i < 4; i++)
            {
                int cap = i;
                var fb = CreateViewportHudButton(fSelLabels[i], 44, 24, toggle: true, pressed: i == 0, tip: fSelTips[i]);
                fb.Pressed += () => { _faceEditSelectTool = (FaceEditSelectTool)cap; SyncImportedSculptViewportHud(); };
                _hudFaceSelectBtns[i] = fb;
                _boxHudFaceTools.AddChild(fb);
            }
            _boxHudFaceTools.AddChild(CreateViewportHudSeparator());
            string[] fTrLabels = { "Move (G)", "Scale (S)", "Inflate" };
            string[] fTrTips = { "Move selected faces (G) — X/Y/Z axis lock", "Scale selected faces (S) — X/Y/Z axis lock", "Inflate/Shrink selected faces along normals (Alt+S)" };
            for (int i = 0; i < 3; i++)
            {
                int cap = i + 1;
                var tb = CreateViewportHudButton(fTrLabels[i], 54, 24, toggle: true, pressed: false, tip: fTrTips[i]);
                tb.Pressed += () => BeginFaceEditTransform((FaceEditTransformMode)cap);
                _hudFaceTransformBtns[i] = tb;
                _boxHudFaceTools.AddChild(tb);
            }
            row1.AddChild(_boxHudFaceTools);

            row1.AddChild(CreateViewportHudSeparator());

            _btnImpSculptMirror = CreateViewportHudButton("Mirror", 46, 24, toggle: true, pressed: true, tip: "Mirror strokes & face selections across X");
            _btnImpSculptMirror.Toggled += (on) => ApplyHudSubtleToggleVisual(_btnImpSculptMirror, on);
            row1.AddChild(_btnImpSculptMirror);

            _btnHudShapeDrawerToggle = CreateViewportHudButton("Scale/Fit ▾", 70, 24, toggle: true, pressed: false, tip: "Toggle per-mesh Non-Uniform Scale (SX/SY/SZ) and Bottom Flare / Top Width / Inflate sliders");
            _btnHudShapeDrawerToggle.Toggled += (on) =>
            {
                _impSculptShowShapeDrawer = on;
                if (_boxHudShapeScaleRow != null) _boxHudShapeScaleRow.Visible = on;
                ApplyHudSubtleToggleVisual(_btnHudShapeDrawerToggle, on);
            };
            row1.AddChild(_btnHudShapeDrawerToggle);

            row1.AddChild(CreateViewportHudSeparator());

            // Hero Visibility (Show | Ghost | Hide)
            string[] hvLabels = { "Show", "Ghost", "Hide" };
            for (int hv = 0; hv < 3; hv++)
            {
                int modeIdx = hv;
                var hb = CreateViewportHudButton(hvLabels[hv], hv == 1 ? 42 : 38, 24, toggle: true, pressed: hv == _pinEditHeroVisMode, tip: "Original hero mesh visibility (Shortcut: H)");
                hb.Pressed += () => { SetPinEditHeroVisMode(modeIdx); SyncImportedSculptViewportHud(); };
                _hudSculptHeroVisBtns[hv] = hb;
                row1.AddChild(hb);
            }

            row1.AddChild(CreateViewportHudSeparator());

            _btnImpSculptUndo = CreateViewportHudButton("", 26, 24, tip: "Undo last sculpt/face transform (Ctrl+Z)");
            SetButtonIcon(_btnImpSculptUndo, "res://assets/at-icons/undo.svg", 14);
            _btnImpSculptUndo.Disabled = true;
            _btnImpSculptUndo.Pressed += OnImportedSculptUndoPressed;
            row1.AddChild(_btnImpSculptUndo);

            _btnImpSculptReset = CreateViewportHudButton("Reset", 44, 24, tip: "Reset reshape, non-uniform scale & sculpt offsets on current target");
            _btnImpSculptReset.Disabled = true;
            _btnImpSculptReset.Pressed += OnImportedSculptResetPressed;
            row1.AddChild(_btnImpSculptReset);

            var btnDone = CreateViewportHudButton("Done ✓", 56, 24, variation: "ImporterPrimaryButton", tip: "Finish sculpting and bake onto the hero rig (Esc)");
            btnDone.Pressed += ExitImportedSculptMode;
            row1.AddChild(btnDone);

            // =====================================================================
            // ROW 2A: Brush Size & Power (shown in Brush mode)
            // =====================================================================
            _boxHudBrushSubRow = new HBoxContainer();
            _boxHudBrushSubRow.AddThemeConstantOverride("separation", 6);
            root.AddChild(_boxHudBrushSubRow);

            var lblSz = new Label { Text = "Brush Size:", SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            ImporterStyle.ApplyHintStyle(lblSz, wrap: false);
            _boxHudBrushSubRow.AddChild(lblSz);
            _sliderImpSculptRadius = new HSlider
            {
                MinValue = 0.03, MaxValue = 0.35, Step = 0.005, Value = 0.12,
                CustomMinimumSize = new Vector2(110, 20),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                FocusMode = Control.FocusModeEnum.None
            };
            _lblImpSculptRadiusVal = new Label { Text = "0.12m", CustomMinimumSize = new Vector2(40, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            ImporterStyle.ApplyHintStyle(_lblImpSculptRadiusVal, wrap: false);
            _sliderImpSculptRadius.ValueChanged += (v) => { if (_lblImpSculptRadiusVal != null) _lblImpSculptRadiusVal.Text = $"{v:F2}m"; };
            _boxHudBrushSubRow.AddChild(_sliderImpSculptRadius);
            _boxHudBrushSubRow.AddChild(_lblImpSculptRadiusVal);

            _boxHudBrushSubRow.AddChild(CreateViewportHudSeparator());

            var lblPw = new Label { Text = "Power:", SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            ImporterStyle.ApplyHintStyle(lblPw, wrap: false);
            _boxHudBrushSubRow.AddChild(lblPw);
            _sliderImpSculptStrength = new HSlider
            {
                MinValue = 0.05, MaxValue = 1.0, Step = 0.01, Value = 0.55,
                CustomMinimumSize = new Vector2(100, 20),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                FocusMode = Control.FocusModeEnum.None
            };
            _lblImpSculptStrengthVal = new Label { Text = "55%", CustomMinimumSize = new Vector2(34, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            ImporterStyle.ApplyHintStyle(_lblImpSculptStrengthVal, wrap: false);
            _sliderImpSculptStrength.ValueChanged += (v) => { if (_lblImpSculptStrengthVal != null) _lblImpSculptStrengthVal.Text = $"{Mathf.RoundToInt((float)v * 100)}%"; };
            _boxHudBrushSubRow.AddChild(_sliderImpSculptStrength);
            _boxHudBrushSubRow.AddChild(_lblImpSculptStrengthVal);

            // =====================================================================
            // ROW 2B: Face Edit Controls (Proportional Editing, Axis Lock, Grow/Shrink)
            // =====================================================================
            _boxHudFaceSubRow = new HBoxContainer { Visible = false };
            _boxHudFaceSubRow.AddThemeConstantOverride("separation", 5);
            root.AddChild(_boxHudFaceSubRow);

            _btnFaceSelThrough = CreateViewportHudButton("Through (T)", 72, 22, toggle: true, pressed: true, tip: "Select faces through front & back sides (T)");
            _btnFaceSelThrough.Toggled += (on) => { _faceEditSelectThrough = on; RefreshFaceEditOverlay3D(); ApplyHudSubtleToggleVisual(_btnFaceSelThrough, on); };
            _boxHudFaceSubRow.AddChild(_btnFaceSelThrough);

            var btnGrow = CreateViewportHudButton("Grow +", 50, 22, tip: "Grow selection (+)");
            btnGrow.Pressed += () => GrowOrShrinkFaceSelection(true);
            var btnShrink = CreateViewportHudButton("Shrink -", 54, 22, tip: "Shrink selection (-)");
            btnShrink.Pressed += () => GrowOrShrinkFaceSelection(false);
            var btnClearSel = CreateViewportHudButton("Clear", 42, 22, tip: "Clear face selection (Ctrl+D)");
            btnClearSel.Pressed += () => ClearFaceEditSelection();
            _boxHudFaceSubRow.AddChild(btnGrow);
            _boxHudFaceSubRow.AddChild(btnShrink);
            _boxHudFaceSubRow.AddChild(btnClearSel);

            _boxHudFaceSubRow.AddChild(CreateViewportHudSeparator());

            _btnFacePropEdit = CreateViewportHudButton("Prop. Edit (O)", 82, 22, toggle: true, pressed: true, tip: "Proportional Editing (O) — smoothly pulls nearby vertices within falloff radius");
            _btnFacePropEdit.Toggled += (on) => { _faceEditProportional = on; ApplyHudSubtleToggleVisual(_btnFacePropEdit, on); };
            _boxHudFaceSubRow.AddChild(_btnFacePropEdit);

            _sliderFacePropRadius = new HSlider
            {
                MinValue = 0.02, MaxValue = 0.50, Step = 0.005, Value = _faceEditPropRadius,
                CustomMinimumSize = new Vector2(80, 20),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = "Proportional falloff radius in meters (scroll wheel while dragging)"
            };
            _lblFacePropRadiusVal = new Label { Text = "0.14m", CustomMinimumSize = new Vector2(38, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            ImporterStyle.ApplyHintStyle(_lblFacePropRadiusVal, wrap: false);
            _sliderFacePropRadius.ValueChanged += (v) => { _faceEditPropRadius = (float)v; if (_lblFacePropRadiusVal != null) _lblFacePropRadiusVal.Text = $"{v:F2}m"; };
            _boxHudFaceSubRow.AddChild(_sliderFacePropRadius);
            _boxHudFaceSubRow.AddChild(_lblFacePropRadiusVal);

            _boxHudFaceSubRow.AddChild(CreateViewportHudSeparator());

            string[] axisLabels = { "Free", "X", "Y", "Z" };
            for (int a = 0; a < 4; a++)
            {
                int cap = a;
                var ab = CreateViewportHudButton(axisLabels[a], a == 0 ? 36 : 24, 22, toggle: true, pressed: a == 0, tip: $"Axis lock: {axisLabels[a]}");
                ab.Pressed += () => { _faceEditAxisLock = cap; UpdateFaceEditAxisGuideVisual(); SyncImportedSculptViewportHud(); };
                _hudFaceAxisBtns[a] = ab;
                _boxHudFaceSubRow.AddChild(ab);
            }

            _lblFaceSelCount = new Label { Text = "0 faces", SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            ImporterStyle.ApplyHintStyle(_lblFaceSelCount, wrap: false);
            _boxHudFaceSubRow.AddChild(_lblFaceSelCount);

            // =====================================================================
            // ROW 3: Per-Mesh Non-Uniform Scale (SX/SY/SZ) + Parametric Fit Drawer
            // =====================================================================
            _boxHudShapeScaleRow = new HBoxContainer { Visible = false };
            _boxHudShapeScaleRow.AddThemeConstantOverride("separation", 5);
            root.AddChild(_boxHudShapeScaleRow);

            SpinBox MakeMeshScaleSpin(string prefix)
            {
                var sb = new SpinBox
                {
                    MinValue = 0.05, MaxValue = 5.0, Step = 0.001, CustomArrowStep = 0.01, Value = 1.0,
                    Prefix = prefix, CustomMinimumSize = new Vector2(72, 24),
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
                };
                ImporterStyle.ApplyFontSize(sb, ImporterStyle.FontHint);
                sb.ValueChanged += (_) => OnPerMeshScaleOrParamChanged();
                return sb;
            }

            _spinMeshScaleX = MakeMeshScaleSpin("SX:");
            _spinMeshScaleY = MakeMeshScaleSpin("SY:");
            _spinMeshScaleZ = MakeMeshScaleSpin("SZ:");
            _boxHudShapeScaleRow.AddChild(_spinMeshScaleX);
            _boxHudShapeScaleRow.AddChild(_spinMeshScaleY);
            _boxHudShapeScaleRow.AddChild(_spinMeshScaleZ);

            var btnScale11 = CreateViewportHudButton("1:1", 30, 24, tip: "Reset non-uniform scale on this mesh to 1:1:1");
            btnScale11.Pressed += () =>
            {
                _isSyncingImpSculptUi = true;
                _spinMeshScaleX.SetValueNoSignal(1.0);
                _spinMeshScaleY.SetValueNoSignal(1.0);
                _spinMeshScaleZ.SetValueNoSignal(1.0);
                _isSyncingImpSculptUi = false;
                OnPerMeshScaleOrParamChanged();
            };
            _boxHudShapeScaleRow.AddChild(btnScale11);

            _boxHudShapeScaleRow.AddChild(CreateViewportHudSeparator());

            (HSlider sld, Label val) AddCompactParam(string lbl, double min, double max, double step, bool pct)
            {
                var l = new Label { Text = lbl, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
                ImporterStyle.ApplyHintStyle(l, wrap: false);
                var s = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = 0.0, CustomMinimumSize = new Vector2(72, 20), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, FocusMode = Control.FocusModeEnum.None };
                var v = new Label { Text = pct ? "0%" : "0.0cm", CustomMinimumSize = new Vector2(40, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
                ImporterStyle.ApplyHintStyle(v, wrap: false);
                s.ValueChanged += (nv) =>
                {
                    v.Text = pct ? $"{(nv > 0 ? "+" : "")}{Mathf.RoundToInt((float)nv * 100)}%" : $"{(nv > 0 ? "+" : "")}{nv * 100.0:F1}cm";
                    OnPerMeshScaleOrParamChanged();
                };
                _boxHudShapeScaleRow.AddChild(l);
                _boxHudShapeScaleRow.AddChild(s);
                _boxHudShapeScaleRow.AddChild(v);
                return (s, v);
            }

            (_sliderImpBottomFlare, _lblImpBottomFlareVal) = AddCompactParam("Bot:", -0.50, 0.80, 0.01, true);
            (_sliderImpTopFlare, _lblImpTopFlareVal) = AddCompactParam("Top:", -0.50, 0.80, 0.01, true);
            (_sliderImpInflate, _lblImpInflateVal) = AddCompactParam("Inf:", -0.05, 0.10, 0.002, false);

            SyncImportedSculptViewportHud();
        }

        private void PopulateSculptTargetDropdown()
        {
            if (_optHudSculptTarget == null) return;
            bool prev = _isSyncingImpSculptUi;
            _isSyncingImpSculptUi = true;
            try
            {
                _optHudSculptTarget.Clear();
                _optHudSculptTarget.AddItem("All Active Meshes", 0);
                _optHudSculptTarget.SetItemMetadata(0, "");

                int selIdx = 0;
                int id = 1;
                foreach (var chk in _importedSubmeshCheckboxes)
                {
                    if (!GodotObject.IsInstanceValid(chk) || !chk.ButtonPressed || !chk.HasMeta("SubmeshName")) continue;
                    string mName = chk.GetMeta("SubmeshName").AsString();
                    _optHudSculptTarget.AddItem(mName, id);
                    _optHudSculptTarget.SetItemMetadata(id, mName);
                    if (!string.IsNullOrEmpty(_activeImpSculptTargetMesh) &&
                        string.Equals(_activeImpSculptTargetMesh, mName, StringComparison.OrdinalIgnoreCase))
                    {
                        selIdx = id;
                    }
                    id++;
                }
                _optHudSculptTarget.Select(selIdx);
                if (selIdx == 0) _activeImpSculptTargetMesh = null;
            }
            finally
            {
                _isSyncingImpSculptUi = prev;
            }
        }

        private void SyncShapeScaleControlsFromTarget()
        {
            bool prev = _isSyncingImpSculptUi;
            _isSyncingImpSculptUi = true;
            try
            {
                Vector3 sc = Vector3.One;
                float bot = _globalImpBottomFlare, top = _globalImpTopFlare, inf = _globalImpInflateMeters;
                if (!string.IsNullOrEmpty(_activeImpSculptTargetMesh))
                {
                    if (_perMeshScaleXyz.TryGetValue(_activeImpSculptTargetMesh, out var ps)) sc = ps;
                    bot = _perMeshBottomFlare.TryGetValue(_activeImpSculptTargetMesh, out var pb) ? pb : 0f;
                    top = _perMeshTopFlare.TryGetValue(_activeImpSculptTargetMesh, out var pt) ? pt : 0f;
                    inf = _perMeshInflateMeters.TryGetValue(_activeImpSculptTargetMesh, out var pi) ? pi : 0f;
                }
                _spinMeshScaleX?.SetValueNoSignal(sc.X);
                _spinMeshScaleY?.SetValueNoSignal(sc.Y);
                _spinMeshScaleZ?.SetValueNoSignal(sc.Z);
                _sliderImpBottomFlare?.SetValueNoSignal(bot);
                if (_lblImpBottomFlareVal != null) _lblImpBottomFlareVal.Text = $"{(bot > 0 ? "+" : "")}{Mathf.RoundToInt(bot * 100)}%";
                _sliderImpTopFlare?.SetValueNoSignal(top);
                if (_lblImpTopFlareVal != null) _lblImpTopFlareVal.Text = $"{(top > 0 ? "+" : "")}{Mathf.RoundToInt(top * 100)}%";
                _sliderImpInflate?.SetValueNoSignal(inf);
                if (_lblImpInflateVal != null) _lblImpInflateVal.Text = $"{(inf > 0 ? "+" : "")}{inf * 100f:F1}cm";
            }
            finally
            {
                _isSyncingImpSculptUi = prev;
            }
        }

        private void OnPerMeshScaleOrParamChanged()
        {
            if (_isSyncingImpSculptUi) return;
            Vector3 sc = new Vector3(
                (float)(_spinMeshScaleX?.Value ?? 1.0),
                (float)(_spinMeshScaleY?.Value ?? 1.0),
                (float)(_spinMeshScaleZ?.Value ?? 1.0));
            float bot = (float)(_sliderImpBottomFlare?.Value ?? 0.0);
            float top = (float)(_sliderImpTopFlare?.Value ?? 0.0);
            float inf = (float)(_sliderImpInflate?.Value ?? 0.0);

            if (!string.IsNullOrEmpty(_activeImpSculptTargetMesh))
            {
                if (sc.IsEqualApprox(Vector3.One)) _perMeshScaleXyz.Remove(_activeImpSculptTargetMesh);
                else _perMeshScaleXyz[_activeImpSculptTargetMesh] = sc;

                if (MathF.Abs(bot) < 1e-4f) _perMeshBottomFlare.Remove(_activeImpSculptTargetMesh);
                else _perMeshBottomFlare[_activeImpSculptTargetMesh] = bot;

                if (MathF.Abs(top) < 1e-4f) _perMeshTopFlare.Remove(_activeImpSculptTargetMesh);
                else _perMeshTopFlare[_activeImpSculptTargetMesh] = top;

                if (MathF.Abs(inf) < 1e-5f) _perMeshInflateMeters.Remove(_activeImpSculptTargetMesh);
                else _perMeshInflateMeters[_activeImpSculptTargetMesh] = inf;
            }
            else
            {
                // Apply to all active meshes when "All Active Meshes" is selected
                _globalImpBottomFlare = bot;
                _globalImpTopFlare = top;
                _globalImpInflateMeters = inf;
                if (!sc.IsEqualApprox(Vector3.One))
                {
                    foreach (var chk in _importedSubmeshCheckboxes)
                        if (GodotObject.IsInstanceValid(chk) && chk.ButtonPressed && chk.HasMeta("SubmeshName"))
                            _perMeshScaleXyz[chk.GetMeta("SubmeshName").AsString()] = sc;
                }
            }

            UpdateImportedSculptStatusLabel();
            if (IsImportedSculptActive())
                RefreshImportedSculptPreviewFromCurrentSettings();
            else
                ScheduleDebouncedLiveRigUpdate();
        }

        private void SetSculptSubMode(bool faceEdit)
        {
            if (_isFaceEditSubMode == faceEdit) return;
            CancelFaceEditTransform(restore: true);
            _isFaceEditSubMode = faceEdit;
            if (!faceEdit) ClearFaceEditSelection(refreshVisual: false);
            RefreshFaceEditOverlay3D();
            SyncImportedSculptViewportHud();
            SetBottomStatus(faceEdit
                ? "Edit Faces — Select faces (Lasso/Box/Paint/Island), then press G (Move), S (Scale), or Alt+S (Inflate) with X/Y/Z axis lock & O Proportional Edit."
                : "Brush Sculpt — Left-drag to sculpt live (Grab/Inflate/Shrink/Smooth/Erase). [ / ] resizes brush.", StatusTone.Info);
        }

        private void SetActiveImportedSculptMode(ImportedSculptBrushMode mode)
        {
            _activeImpSculptMode = mode;
            SyncImportedSculptViewportHud();
        }

        private void SyncImportedSculptViewportHud()
        {
            ApplyHudSubtleToggleVisual(_btnHudSculptModeBrush, !_isFaceEditSubMode);
            ApplyHudSubtleToggleVisual(_btnHudSculptModeFaces, _isFaceEditSubMode);
            if (_boxHudBrushTools != null) _boxHudBrushTools.Visible = !_isFaceEditSubMode;
            if (_boxHudFaceTools != null) _boxHudFaceTools.Visible = _isFaceEditSubMode;
            if (_boxHudBrushSubRow != null) _boxHudBrushSubRow.Visible = !_isFaceEditSubMode;
            if (_boxHudFaceSubRow != null) _boxHudFaceSubRow.Visible = _isFaceEditSubMode;
            if (_boxHudShapeScaleRow != null) _boxHudShapeScaleRow.Visible = _impSculptShowShapeDrawer;

            for (int i = 0; i < _hudSculptModeBtns.Length; i++)
                ApplyHudSubtleToggleVisual(_hudSculptModeBtns[i], i == (int)_activeImpSculptMode);
            for (int i = 0; i < _hudFaceSelectBtns.Length; i++)
                ApplyHudSubtleToggleVisual(_hudFaceSelectBtns[i], i == (int)_faceEditSelectTool);
            for (int i = 0; i < _hudFaceTransformBtns.Length; i++)
                ApplyHudSubtleToggleVisual(_hudFaceTransformBtns[i], (i + 1) == (int)_faceEditTransformMode);
            for (int a = 0; a < _hudFaceAxisBtns.Length; a++)
                ApplyHudSubtleToggleVisual(_hudFaceAxisBtns[a], a == _faceEditAxisLock);

            ApplyHudSubtleToggleVisual(_btnImpSculptMirror, _btnImpSculptMirror?.ButtonPressed ?? true);
            ApplyHudSubtleToggleVisual(_btnHudSculptIsolate, _impSculptIsolateTarget);
            ApplyHudSubtleToggleVisual(_btnHudShapeDrawerToggle, _impSculptShowShapeDrawer);
            ApplyHudSubtleToggleVisual(_btnFaceSelThrough, _faceEditSelectThrough);
            ApplyHudSubtleToggleVisual(_btnFacePropEdit, _faceEditProportional);
            if (_sliderFacePropRadius != null) _sliderFacePropRadius.SetValueNoSignal(_faceEditPropRadius);
            if (_lblFacePropRadiusVal != null) _lblFacePropRadiusVal.Text = $"{_faceEditPropRadius:F2}m";
            if (_lblFaceSelCount != null) _lblFaceSelCount.Text = $"{GetFaceEditSelectedTriCount():N0} faces";

            for (int hv = 0; hv < _hudSculptHeroVisBtns.Length; hv++)
                ApplyHudSubtleToggleVisual(_hudSculptHeroVisBtns[hv], hv == _pinEditHeroVisMode);
        }

        private void ApplyImpSculptIsolationVisibility()
        {
            if (_previewModelInstance == null || !GodotObject.IsInstanceValid(_previewModelInstance)) return;
            var filter = GetActiveImportedSubmeshFilter();
            foreach (var mi in CustomModelAutoRigger.CollectMeshes(_previewModelInstance))
            {
                string mName = mi.Name.ToString();
                bool active = filter == null || filter.Contains(mName);
                if (!active) { mi.Visible = false; continue; }
                if (_isImpSculptModeActive && _impSculptIsolateTarget && !string.IsNullOrEmpty(_activeImpSculptTargetMesh))
                    mi.Visible = string.Equals(mName, _activeImpSculptTargetMesh, StringComparison.OrdinalIgnoreCase);
                else
                    mi.Visible = true;
            }
        }

        private void EnterImportedSculptModeForMesh(string meshName)
        {
            if (_isMeshCutModeActive) ExitMeshCutMode();
            if (IsWeaponSocketHudActive()) ExitWeaponSocketHud();
            if (_isGizmoEditingSyntheticChain) SetSyntheticChainGizmoActive(false);
            if (_btnWeightPaintMode != null && _btnWeightPaintMode.ButtonPressed)
                _btnWeightPaintMode.ButtonPressed = false;
            if (_btnDeformBrushToggle != null && _btnDeformBrushToggle.ButtonPressed)
                _btnDeformBrushToggle.ButtonPressed = false;
            if (_isEditReferenceBonesMode)
                _isEditReferenceBonesMode = false;
            if (_optGizmoTarget != null) SelectGizmoTargetById(0);

            _activeImpSculptTargetMesh = string.IsNullOrEmpty(meshName) ? null : meshName;
            _isImpSculptModeActive = true;
            ClearFaceEditSelection(refreshVisual: false);

            PopulateSculptTargetDropdown();
            SyncShapeScaleControlsFromTarget();

            // Automatically switch preview into live Sculpt Pose (unposed _previewModelRoot visible, _previewRiggedContainer hidden)
            ApplyLandmarkPreviewState();
            ApplyImpSculptIsolationVisibility();
            RefreshImportedSculptPreviewFromCurrentSettings();
            if (_importedSculptViewportHud != null) _importedSculptViewportHud.Visible = true;
            UpdateImportedSculptStatusLabel();
            SyncImportedSculptViewportHud();

            string scopeLbl = string.IsNullOrEmpty(_activeImpSculptTargetMesh) ? "All Meshes" : _activeImpSculptTargetMesh;
            SetBottomStatus($"3D Sculpt & Face Edit ({scopeLbl}) — Brush or Edit Faces (G/S/Alt+S), Scale/Fit drawer, H for Hero Ghost, Esc or Done to bake.", StatusTone.Info);
        }

        private void ExitImportedSculptMode()
        {
            if (!_isImpSculptModeActive) return;
            CancelFaceEditTransform(restore: true);
            ClearFaceEditSelection(refreshVisual: false);
            DestroyFaceEditOverlays3D();
            _meshCutOverlay2D?.Cancel();

            _isImpSculptModeActive = false;
            _isImpSculptBrushing = false;
            _impSculptIsolateTarget = false;
            _impGrabVerts.Clear();
            _impGrabInitialDeltas.Clear();
            UpdateBrushRingVisual(Vector3.Zero, Vector3.Up, 0.05f, false);
            if (_importedSculptViewportHud != null) _importedSculptViewportHud.Visible = false;

            _impSculptOriginalMeshRestore?.Invoke();
            _impSculptOriginalMeshRestore = null;
            _impSculptCaches.Clear();
            SyncPreviewModelMeshVisibility();

            ExecuteLiveRigUpdate();
            ApplyLandmarkPreviewState();
            RefreshAllMeshSculptCardBadges();
            SetBottomStatus("Baked sculpted & scaled mesh onto hero rig.", StatusTone.Success);
        }

        private void RefreshImportedSculptPreviewFromCurrentSettings()
        {
            _impSculptOriginalMeshRestore?.Invoke();
            _impSculptOriginalMeshRestore = null;
            if (_previewModelInstance == null || !GodotObject.IsInstanceValid(_previewModelInstance)) return;

            var opts = GatherRiggingOptions();
            _impSculptOriginalMeshRestore = CustomModelAutoRigger.ApplyImportedMeshSculptToScene(_previewModelInstance, opts);
            RebuildImportedSculptCaches();
            RefreshFaceEditOverlay3D();
        }

        private void RefreshAllMeshSculptCardBadges()
        {
            foreach (var (chk, card) in _importedSubmeshCards)
            {
                if (!GodotObject.IsInstanceValid(chk) || !GodotObject.IsInstanceValid(card) || !chk.HasMeta("SubmeshName"))
                    continue;
                string mName = chk.GetMeta("SubmeshName").AsString();
                bool hasScale = _perMeshScaleXyz.TryGetValue(mName, out var sc) && !sc.IsEqualApprox(Vector3.One);
                bool hasParam = (_perMeshBottomFlare.TryGetValue(mName, out var pb) && MathF.Abs(pb) > 1e-4f) ||
                                (_perMeshTopFlare.TryGetValue(mName, out var pt) && MathF.Abs(pt) > 1e-4f) ||
                                (_perMeshInflateMeters.TryGetValue(mName, out var pi) && MathF.Abs(pi) > 1e-5f);
                bool hasSculpt = _importedSculptDeltas.Keys.Any(k => k.StartsWith(mName + CustomModelAutoRigger.SurfaceKeepSeparator, StringComparison.OrdinalIgnoreCase));
                card.SetSculptBadge(hasScale || hasParam || hasSculpt);
            }
        }

        private void UpdateImportedSculptStatusLabel()
        {
            int vCnt = 0;
            foreach (var map in _importedSculptDeltas.Values) vCnt += map.Count;
            bool hasParam = MathF.Abs(_globalImpBottomFlare) > 0.001f ||
                            MathF.Abs(_globalImpTopFlare) > 0.001f ||
                            MathF.Abs(_globalImpInflateMeters) > 0.0005f ||
                            _perMeshScaleXyz.Count > 0 || _perMeshBottomFlare.Count > 0 ||
                            _perMeshTopFlare.Count > 0 || _perMeshInflateMeters.Count > 0;
            if (_btnImpSculptUndo != null) _btnImpSculptUndo.Disabled = _importedSculptUndoStack.Count == 0;
            if (_btnImpSculptReset != null) _btnImpSculptReset.Disabled = vCnt == 0 && !hasParam;
            RefreshAllMeshSculptCardBadges();
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

            if (!string.IsNullOrEmpty(_activeImpSculptTargetMesh))
            {
                _perMeshScaleXyz.Remove(_activeImpSculptTargetMesh);
                _perMeshBottomFlare.Remove(_activeImpSculptTargetMesh);
                _perMeshTopFlare.Remove(_activeImpSculptTargetMesh);
                _perMeshInflateMeters.Remove(_activeImpSculptTargetMesh);
                foreach (var k in _importedSculptDeltas.Keys.Where(k => k.StartsWith(_activeImpSculptTargetMesh + CustomModelAutoRigger.SurfaceKeepSeparator, StringComparison.OrdinalIgnoreCase)).ToList())
                    _importedSculptDeltas.Remove(k);
            }
            else
            {
                _importedSculptDeltas.Clear();
                _perMeshScaleXyz.Clear();
                _perMeshBottomFlare.Clear();
                _perMeshTopFlare.Clear();
                _perMeshInflateMeters.Clear();
                _globalImpBottomFlare = 0f;
                _globalImpTopFlare = 0f;
                _globalImpInflateMeters = 0f;
            }

            SyncShapeScaleControlsFromTarget();
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
            DestroyFaceEditOverlays3D();
            _isImpSculptModeActive = false;
            _activeImpSculptTargetMesh = null;
            _impSculptIsolateTarget = false;
            _isFaceEditSubMode = false;
            _faceEditSelectedTris.Clear();
            _faceEditTransformMode = FaceEditTransformMode.None;
            _globalImpBottomFlare = 0f;
            _globalImpTopFlare = 0f;
            _globalImpInflateMeters = 0f;
            _perMeshScaleXyz.Clear();
            _perMeshBottomFlare.Clear();
            _perMeshTopFlare.Clear();
            _perMeshInflateMeters.Clear();
            _importedSculptDeltas.Clear();
            _importedSculptUndoStack.Clear();
            _impSculptCaches.Clear();
            _isImpSculptBrushing = false;
            _pendingImpSculptUndoSnapshot = null;
            if (_importedSculptViewportHud != null) _importedSculptViewportHud.Visible = false;
            SyncShapeScaleControlsFromTarget();
            UpdateImportedSculptStatusLabel();
        }

        private void RestoreImportedSculptOptions(CustomModelAutoRigger.RiggingOptions options)
        {
            if (options == null) return;
            _globalImpBottomFlare = options.ImportedMeshBottomFlare;
            _globalImpTopFlare = options.ImportedMeshTopFlare;
            _globalImpInflateMeters = options.ImportedMeshInflateMeters;

            _perMeshScaleXyz.Clear();
            if (options.PerMeshScaleXYZ != null)
                foreach (var (k, v) in options.PerMeshScaleXYZ) _perMeshScaleXyz[k] = v;
            _perMeshBottomFlare.Clear();
            if (options.PerMeshBottomFlare != null)
                foreach (var (k, v) in options.PerMeshBottomFlare) _perMeshBottomFlare[k] = v;
            _perMeshTopFlare.Clear();
            if (options.PerMeshTopFlare != null)
                foreach (var (k, v) in options.PerMeshTopFlare) _perMeshTopFlare[k] = v;
            _perMeshInflateMeters.Clear();
            if (options.PerMeshInflateMeters != null)
                foreach (var (k, v) in options.PerMeshInflateMeters) _perMeshInflateMeters[k] = v;

            _importedSculptDeltas.Clear();
            if (options.ImportedMeshSculptDeltas != null)
            {
                foreach (var (sk, vMap) in options.ImportedMeshSculptDeltas)
                    if (!string.IsNullOrEmpty(sk) && vMap != null && vMap.Count > 0)
                        _importedSculptDeltas[sk] = new Dictionary<int, Vector3>(vMap);
            }
            SyncShapeScaleControlsFromTarget();
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

            var activeFilter = GetActiveImportedSubmeshFilter();
            var srcMeshes = CustomModelAutoRigger.CollectMeshes(_previewModelInstance);
            foreach (var srcMi in srcMeshes)
            {
                if (srcMi?.Mesh == null) continue;
                string mName = srcMi.Name.ToString();
                if (activeFilter != null && activeFilter.Count > 0 && !activeFilter.Contains(mName)) continue;
                if (!string.IsNullOrEmpty(_activeImpSculptTargetMesh) &&
                    !string.Equals(mName, _activeImpSculptTargetMesh, StringComparison.OrdinalIgnoreCase))
                    continue;

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
                        DisplayMi = srcMi,
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
            return RaycastImportedSculptTriangle(mousePos, out hitCache, out _, out hitWorld, out hitNorm);
        }

        private bool HandleImportedSculptInput(InputEvent @event)
        {
            if (!IsImportedSculptActive() || _previewCamera == null) return false;
            if (IsFaceEditActive()) return HandleFaceEditInput(@event);

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

                        if (_activeImpSculptMode == ImportedSculptBrushMode.Grab)
                            BeginImportedGrabStroke(hitPt);
                        else
                            ApplyImportedSculptDab(hitPt, hitNorm, _activeImpSculptMode);

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
                    if (_activeImpSculptMode == ImportedSculptBrushMode.Grab)
                        UpdateImportedGrabStroke(mm.Position);
                    else if (RaycastImportedSculpt(mm.Position, out _, out var hitPt, out var hitNorm))
                    {
                        ApplyImportedSculptDab(hitPt, hitNorm, _activeImpSculptMode);
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
                _importedSculptDeltas.TryGetValue(c.SurfaceKey, out var map);

                for (int v = 0; v < c.BaseLocalVerts.Length; v++)
                {
                    Vector3 d = (map != null && map.TryGetValue(v, out var dl)) ? dl : Vector3.Zero;
                    c.CurrentWorldVerts[v] = c.LocalToWorld * (c.BaseLocalVerts[v] + d);
                }

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
