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
    /// <summary>
    /// Centered Split-Screen Studio Modal with Dedicated 3D SubViewport (OwnWorld3D = true)
    /// for Custom 3D Model Importer & Auto-Rigger.
    ///
    /// The main viewport hero remains 100% untouched until [Confirm & Apply to Hero] is clicked.
    /// Includes studio lighting, interactive orbit camera, ghost hero with true bind pose (ResetBonePoses),
    /// exact world-vertex auto-fit height/center, live animation preview testing, and tabbed configuration.
    /// </summary>
    public partial class CustomModelImporterModalUI : CanvasLayer
    {
        public enum ImportWorkflowIntent
        {
            FullCharacter = 0,
            AccessoryAddOrReplace = 1
        }

        [Signal] public delegate void RiggingConfirmedEventHandler();
        [Signal] public delegate void ModalCancelledEventHandler();

        // Accessory / Outfit Workflow State
        private ImportWorkflowIntent _workflowIntent = ImportWorkflowIntent.FullCharacter;
        private string _accessorySlotId = "";
        private CustomModelAutoRigger.AccessoryIntent _accessoryIntent = CustomModelAutoRigger.AccessoryIntent.AddAccessory;
        private Control _boxAccessorySetupCard;
        private Button _btnAccIntentAdd;
        private Button _btnAccIntentReplace;
        private OptionButton _optAccQuickBone;
        private CheckBox _chkAccAllBones;
        private Button _btnAccSnapBone;
        private Button _btnAccGizmoToggle;
        private Button _btnAccMirrorLr;
        private Control _boxAccReplacePanel;
        private VBoxContainer _boxAccReplaceList;
        private readonly List<CheckBox> _accReplaceCheckboxes = new();
        private LineEdit _txtAccName;
        private CheckBox _chkDeformEnabled;
        private Control _boxDeformControls;
        private OptionButton _optDeformTarget;
        private OptionButton _optDeformMode;
        private HSlider _sliderDeformStrength;
        private Label _lblDeformStrengthVal;
        private HSlider _sliderDeformRadius;
        private Label _lblDeformRadiusVal;
        private HSlider _sliderDeformHeight;
        private Label _lblDeformHeightVal;
        private Button _btnDeformBrushToggle;
        private OptionButton _optDeformBrushMode;
        private HSlider _sliderDeformBrushRadius;
        private Label _lblDeformBrushRadiusVal;
        private HSlider _sliderDeformBrushStrength;
        private Label _lblDeformBrushStrengthVal;
        private CheckBox _chkDeformBrushMirror;
        private Button _btnDeformBrushUndo;
        private Button _btnDeformBrushClear;
        private Label _lblDeformBrushStatus;
        private bool _isDeformBrushing = false;
        private bool _deformBrushDirty = false;
        private Vector3 _lastDeformBrushDabWorld = Vector3.Zero;
        private bool _hasLastDeformBrushDab = false;
        private Vector3 _deformFlattenPlaneOriginHero = Vector3.Zero;
        private Vector3 _deformFlattenPlaneNormalHero = Vector3.Up;
        private bool _deformFlattenPlaneLocked = false;
        private readonly Dictionary<string, Dictionary<int, Vector3>> _deformSculptDeltas = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Dictionary<string, Dictionary<int, Vector3>>> _deformUndoStack = new();
        private Dictionary<string, Dictionary<int, Vector3>> _pendingDeformUndoSnapshot;
        private readonly List<HeroDeformSurfaceCache> _deformSurfaceCaches = new();
        private Vector3 _accLocalBoundsCenter = Vector3.Zero;
        private bool _isSyncingAccUi = false;

        // Target References
        private Node3D _liveHeroNode;
        private Node3D _liveImportedScene;
        private string _filePath;

        // Preview SubViewport & 3D Scene
        private SubViewportContainer _subViewportContainer;
        private SubViewport _subViewport;
        private Node3D _previewWorldRoot;
        private Node3D _previewHeroClone;
        private Skeleton3D _previewHeroSkeleton;
        private DeadlockPlayground.Animation.Source2BoneConstraintDriver _previewConstraintDriver;
        private AnimationPlayer _previewAnimPlayer;
        private Node3D _previewModelRoot;
        private Node3D _previewModelInstance;
        private Node3D _previewRiggedContainer;
        private Camera3D _previewCamera;
        private MeshInstance3D _bonesDebugMesh;
        private MeshInstance3D _collidersDebugMesh;
        private StandardMaterial3D _ghostMaterial;
        private float _ghostOpacity = 0.35f;
        private bool _showBones = false;
        private bool _showColliders = false;
        private bool _collidersDirty = true;
        private bool _showGhost = true;
        private string _heroCodename = "";

        // Camera Orbit Controls
        private Vector3 _orbitTarget = new Vector3(0, 0.9f, 0);
        private float _orbitDist = 3.0f;
        private float _orbitYaw = 0.0f;
        private float _orbitPitch = 0.15f;
        private bool _isOrbiting = false;
        private bool _isPanning = false;
        private Vector2 _lastMousePos;

        // UI Controls - Header & Info
        private Label _lblTitle;
        private Label _lblFileBadge;
        private Button _btnCloseX;
        private Label _lblBottomStatus;

        // Overlay Toolbar Controls
        private Button _btnToggleGhost;
        private HSlider _sliderGhostOpacity;
        private Button _btnToggleBones;
        private Button _btnToggleColliders;
        private Button _btnToggleXRay;
        private bool _isXRayMode = false;
        private OptionButton _optViewportShading;
        private CustomWeightPaintEditor.ViewportShadingMode _viewportShadingMode = CustomWeightPaintEditor.ViewportShadingMode.Textured;
        private Material _wireLineMaterialDark;
        private Material _wireLineMaterialBright;
        private Material _wireLineMaterialXRay;
        private Material _wireClayMaterial;
        private OptionButton _optTestAnim;
        private Button _btnAnimPlayPause;
        private HSlider _sliderAnimTimeline;
        private bool _isScrubbingAnimTimeline = false;
        private bool _isUpdatingTimelineSlider = false;
        private Label _lblAnimTime;
        private Button _btnAnimLoop;
        private OptionButton _optAnimSpeed;

        // Tab 1: Align & Scale
        private TabContainer _rightTabs;
        private Label _lblGeometryStats;
        private Label _lblPoseCalHint;
        private SpinBox _spinScale;
        private HSlider _sliderScale;
        private SpinBox _spinScaleX, _spinScaleY, _spinScaleZ;
        private SpinBox _spinPosX, _spinPosY, _spinPosZ;
        private SpinBox _spinRotPitch, _spinRotYaw, _spinRotRoll;
        private Button _btnAutoFit;
        private Button _btnFlipYaw;
        private Button _btnSnapFeet;
        private Button _btnResetTransform;
        private Button _btnFlipX;
        private Button _btnFlipY;
        private Button _btnFlipZ;
        private Action _previewFlipRestore;
        private Control _boxPoseCalibration;
        private OptionButton _optPosePreset;
        private Button _btnAutoCalibratePose;
        private Button _btnResetPoseCal;
        private Control _boxCustomArmAngle;
        private HSlider _sliderArmAngle;
        private Label _lblArmAngleVal;
        private HSlider _sliderArmForward;
        private Label _lblArmForwardVal;
        private HSlider _sliderElbowStraight;
        private Label _lblElbowStraightVal;
        private HSlider _sliderLegSpread;
        private Label _lblLegSpreadVal;
        private VBoxContainer _boxAutoSnapOptions;
        private Control _boxAutoSnapCard;
        private Control _boxDeformGuardsCard;
        private bool _isUpdatingPoseSliders = false;
        private Control _boxRigidBone;
        private OptionButton _optRigidBone;

        // Tab 2: Rigging & Bones
        private Label _lblRigStatusBadge;
        private OptionButton _optRiggingMethod;
        private Label _lblRiggingMethodDesc;
        private Control _boxKnnParameters;
        private Control _boxPreRiggedMapping;
        private Control _boxLandmarkModeSwitchRow;
        private Button _btnLandmarkStepPins;
        private Button _btnLandmarkStepPaint;
        private VBoxContainer _boxBoneMappingContainer;
        private Label _lblMappingSummary;
        private LineEdit _txtBoneSearch;
        private Button _btnFilterIssuesOnly;
        private Button _btnAutoMapBones;
        private Button _btnMirrorBones;
        private Button _btnClearUnmatchedBones;
        private Skeleton3D _cachedSourceSkeleton;
        private readonly Dictionary<string, string> _userBoneRemap = new();
        private SpinBox _spinKnnRadius;
        private HSlider _sliderKnnRadius;
        private SpinBox _spinLaplacian;
        private CheckBox _chkExcludeClothHair;
        private CheckBox _chkKeepFingersRigid;

        // Tab 2 Categorized Accordion State (plain C# structs, zero nested GodotObject classes)
        private sealed class BoneCategorySectionState
        {
            public string Key;
            public string Title;
            public PanelContainer Card;
            public Button HeaderButton;
            public Label ChevronLabel;
            public Label TitleLabel;
            public Label BadgeLabel;
            public VBoxContainer BodyBox;
            public bool UserExpanded;
            public readonly List<BoneRowEntryState> Rows = new();
        }

        private sealed class BoneRowEntryState
        {
            public string CanonicalName;
            public string TargetBoneName;
            public string DisplayLabel;
            public HumanoidBoneMapper.BoneGroup Group;
            public string CategoryKey;
            public bool IsRequiredCore;
            public HBoxContainer RowBox;
            public Label TargetLabel;
            public OptionButton BoneDropdown;
            public Label StatusBadge;
        }

        private readonly List<BoneCategorySectionState> _boneCategorySections = new();
        private readonly Dictionary<string, BoneCategorySectionState> _boneCategoryByKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BoneRowEntryState> _boneRowsByCanonical = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _autoMappedSnapshot = new(StringComparer.OrdinalIgnoreCase);
        private bool _isUpdatingBoneDropdowns = false;

        // Tab 3: Submeshes & Materials
        private OptionButton _optReplaceMode;
        private VBoxContainer _boxImportedSubmeshes;
        private VBoxContainer _boxHeroSubmeshes;
        private readonly List<CheckBox> _heroKeepCheckboxes = new();
        private readonly List<CheckBox> _importedSubmeshCheckboxes = new();
        private readonly Dictionary<string, string> _submeshSocketAssignments = new();
        private readonly Dictionary<string, string> _submeshHeroReplacements = new();
        private readonly Dictionary<string, bool> _submeshSnapToSocket = new();
        private readonly Dictionary<string, Vector3> _submeshSocketOffsetPos = new();
        private readonly Dictionary<string, Vector3> _submeshSocketOffsetRot = new();
        private readonly Dictionary<string, float> _submeshSocketScale = new();

        // Ghost Reference Mannequin Mesh Tracking
        private readonly List<MeshInstance3D> _previewGhostMeshes = new();
        private readonly Dictionary<MeshInstance3D, (Material Override, Material[] SurfaceOverrides)> _previewHeroOriginalMaterials = new();

        // Rest Pose Full-Body Segment Registration Controls
        private Label _lblAutoSnapModeHint;
        private CheckBox _chkAutoAlignArms;
        private CheckBox _chkAutoAlignLegs;
        private CheckBox _chkAutoAlignHead;
        private CheckBox _chkClampLimbStretching;
        private CheckBox _chkStabilizeNeckFloatingHead;
        private CheckBox _chkAutoCounterTwistWeights;
        private CheckBox _chkAutoAlignHandOrientation;
        private VBoxContainer _boxHandSnapTuning;
        private SpinBox _spinRightHandPitch;
        private SpinBox _spinRightHandYaw;
        private SpinBox _spinRightHandRoll;
        private SpinBox _spinRightHandScale;
        private SpinBox _spinLeftHandPitch;
        private SpinBox _spinLeftHandYaw;
        private SpinBox _spinLeftHandRoll;
        private SpinBox _spinLeftHandScale;

        // Tab 3: Reposition Kept Original Hero Part / Weapon (Artist Override)
        private readonly Dictionary<string, CustomModelAutoRigger.KeptHeroPartTransform> _keptPartTransforms = new(StringComparer.OrdinalIgnoreCase);
        private PanelContainer _boxKeptPartCard;
        private OptionButton _optKeptPartTarget;
        private Button _btnKeptGizmoToggle;
        private Button _btnKeptResetPart;
        private SpinBox _spinKeptPosX;
        private SpinBox _spinKeptPosY;
        private SpinBox _spinKeptPosZ;
        private SpinBox _spinKeptRotPitch;
        private SpinBox _spinKeptRotYaw;
        private SpinBox _spinKeptRotRoll;
        private SpinBox _spinKeptScale;
        private bool _isSyncingKeptPartUi = false;

        public CustomModelAutoRigger.RiggingOptions LastRiggingOptions { get; private set; }
        public Transform3D GetAlignedTransform() => _previewModelRoot?.Transform ?? Transform3D.Identity;

        // Weapon Submesh Group Linking & Baked Outline Shell Controls
        private CheckBox _chkLinkWeaponSubmeshes;
        private CheckBox _chkIncludeBakedOutlines;
        private readonly Dictionary<string, bool> _submeshIsOutlineShell = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CheckBox> _submeshOutlineCheckboxes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Label> _submeshOutlineBadges = new(StringComparer.OrdinalIgnoreCase);

        // 3D Viewport Interactive Gizmo & Dual Weapon Controls
        private Gizmo3D _previewGizmo;
        private Node3D _gizmoDummyTarget;
        private BoneAttachment3D _boneAttachment;
        private OptionButton _optGizmoTarget;
        private Button _btnGizmoLocalSpace;
        private Button _btnSnapGripPalm;
        private Button _btnStraightenWeapon;
        private bool _isGizmoUpdatingFromUI = false;
        private bool _isGizmoDragging = false;
        private bool _suppressScalePivotCompensation = false;
        private bool _userModifiedTransform = false;
        private bool _rigUpdatePending = false;
        private Godot.Timer _liveRigDebounceTimer;
        private Transform3D _bakedPreviewTransform = Transform3D.Identity;
        private bool _hasBakedPreviewTransform = false;
        private Vector3 _gizmoBaseBonePos = Vector3.Zero;

        // Submesh UI Card Controls Lookup (for bidirectional gizmo & preset synchronization)
        private readonly Dictionary<string, (SpinBox Pitch, SpinBox Yaw, SpinBox Roll, SpinBox PosX, SpinBox PosY, SpinBox PosZ, SpinBox Scale, OptionButton Socket, OptionButton Replaces, OptionButton Placement, OptionButton Role)> _submeshCardControls = new();

        // Footer & Theme State
        private enum StatusTone
        {
            Neutral,
            Info,
            Success,
            Warning,
            Danger,
            Accent
        }

        private Button _btnCancel;
        private Label _lblRigStatusChip;
        private Button _btnConfirmApply;
        private ColorRect _backdrop;
        private PanelContainer _modalWindow;
        private TextureRect _iconPerson;
        private StatusTone _rigBadgeTone = StatusTone.Warning;
        private StatusTone _rigChipTone = StatusTone.Success;
        private StatusTone _bottomStatusTone = StatusTone.Info;
        private readonly List<Label> _secondaryLabels = new();
        private readonly List<Label> _primaryLabels = new();
        private readonly List<Label> _accentLabels = new();
        private readonly Dictionary<Button, (string RelPath, int BaseSize, StatusTone? Tone)> _themedButtons = new();

        public override void _Ready()
        {
            Layer = 110;
            // Run _Process AFTER _previewAnimPlayer (priority 0) so Physics Stress Test
            // head/spine pitch overrides are NOT overwritten by animation playback, and BEFORE
            // Source2BoneConstraintDriver (50) and CustomJiggleBoneDriver (55).
            ProcessPriority = 45;
            BuildLayout();
            ThemeManager.ThemeChanged += OnThemeChanged;
            ApplyThemeStyling();
        }

        public override void _ExitTree()
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            base._ExitTree();
        }

        public override void _Input(InputEvent @event)
        {
            if (Visible && @event is InputEventKey key && key.Pressed && !key.Echo)
            {
                Control focusOwner = GetViewport()?.GuiGetFocusOwner();
                bool textFocused = focusOwner is LineEdit || focusOwner is TextEdit;

                if (!textFocused && IsPinEditActive())
                {
                    if (key.CtrlPressed && !key.AltPressed && key.Keycode == Key.A)
                    {
                        ToggleSelectAllPins();
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                    if (!key.CtrlPressed && key.Keycode == Key.Escape && _selectedPins.Count > 0)
                    {
                        ClearPinSelection();
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                }

                if (IsWeightPaintActive())
                {
                    if (key.CtrlPressed && key.Keycode == Key.Z)
                    {
                        if (key.ShiftPressed) OnWeightRedoPressed();
                        else OnWeightUndoPressed();
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                    if (key.CtrlPressed && key.Keycode == Key.Y)
                    {
                        OnWeightRedoPressed();
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                    if (!key.CtrlPressed && key.Keycode == Key.Bracketleft && _sliderPaintRadius != null)
                    {
                        _sliderPaintRadius.Value = Math.Max(_sliderPaintRadius.MinValue, _sliderPaintRadius.Value - 0.01);
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                    if (!key.CtrlPressed && key.Keycode == Key.Bracketright && _sliderPaintRadius != null)
                    {
                        _sliderPaintRadius.Value = Math.Min(_sliderPaintRadius.MaxValue, _sliderPaintRadius.Value + 0.01);
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                }
                else if (IsDeformBrushActive())
                {
                    if (key.CtrlPressed && key.Keycode == Key.Z && !key.ShiftPressed)
                    {
                        OnDeformBrushUndoPressed();
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                    if (!key.CtrlPressed && key.Keycode == Key.Bracketleft && _sliderDeformBrushRadius != null)
                    {
                        _sliderDeformBrushRadius.Value = Math.Max(_sliderDeformBrushRadius.MinValue, _sliderDeformBrushRadius.Value - 0.005);
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                    if (!key.CtrlPressed && key.Keycode == Key.Bracketright && _sliderDeformBrushRadius != null)
                    {
                        _sliderDeformBrushRadius.Value = Math.Min(_sliderDeformBrushRadius.MaxValue, _sliderDeformBrushRadius.Value + 0.005);
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                }
            }
            base._Input(@event);
        }

        private void OnThemeChanged(ThemeProfile _)
        {
            ApplyThemeStyling();
        }

        public override void _Process(double delta)
        {
            base._Process(delta);
            if (_previewHeroSkeleton != null && _previewRiggedContainer != null && GodotObject.IsInstanceValid(_previewRiggedContainer))
            {
                bool clampStretch = _chkClampLimbStretching?.ButtonPressed ?? true;
                CustomModelAutoRigger.UpdateCustomModelRuntimePosing(_previewHeroSkeleton, clampStretch);
            }

            bool stressActive = ApplyPhysicsStressTestInProcess(delta);

            if (_showColliders && _collidersDebugMesh != null && GodotObject.IsInstanceValid(_collidersDebugMesh) &&
                _previewHeroSkeleton != null && ((_previewAnimPlayer?.IsPlaying() ?? false) || _isScrubbingAnimTimeline || _collidersDirty || stressActive))
            {
                RedrawColliderWireframe();
            }

            if (_showBones && _bonesDebugMesh != null && GodotObject.IsInstanceValid(_bonesDebugMesh) &&
                _previewHeroSkeleton != null && ((_previewAnimPlayer?.IsPlaying() ?? false) || _isScrubbingAnimTimeline || stressActive))
            {
                RedrawBoneWireframeColorCoded();
            }

            if (_previewAnimPlayer != null && _previewAnimPlayer.IsPlaying())
            {
                double cur = _previewAnimPlayer.CurrentAnimationPosition;
                double len = _previewAnimPlayer.CurrentAnimationLength;
                if (_sliderAnimTimeline != null && !_isScrubbingAnimTimeline && !_sliderAnimTimeline.HasFocus())
                {
                    _isUpdatingTimelineSlider = true;
                    try
                    {
                        _sliderAnimTimeline.MaxValue = Math.Max(0.01, len);
                        _sliderAnimTimeline.SetValueNoSignal(Math.Clamp(cur, 0.0, _sliderAnimTimeline.MaxValue));
                    }
                    finally
                    {
                        _isUpdatingTimelineSlider = false;
                    }
                }
                if (_lblAnimTime != null)
                {
                    _lblAnimTime.Text = $"{cur:F2} / {len:F2}s";
                }
            }

            _memPollTimer += delta;
            if (_memPollTimer >= 2.0)
            {
                _memPollTimer = 0.0;
                UpdateFooterMemoryIndicator();
            }
        }

        private static Texture2D LoadIcon(string relPath)
        {
            if (ResourceLoader.Exists(relPath))
            {
                return GD.Load<Texture2D>(relPath);
            }
            return null;
        }

        private static Color GetToneColor(StatusTone tone)
        {
            return tone switch
            {
                StatusTone.Neutral => ThemeManager.GetColor(nameof(ThemePaletteData.TextPrimary)),
                StatusTone.Info => ThemeManager.GetColor(nameof(ThemePaletteData.TextSecondary)),
                StatusTone.Success => ThemeManager.GetColor(nameof(ThemePaletteData.SemanticSuccess)),
                StatusTone.Warning => ThemeManager.GetColor(nameof(ThemePaletteData.SemanticWarning)),
                StatusTone.Danger => ThemeManager.GetColor(nameof(ThemePaletteData.SemanticDanger)),
                StatusTone.Accent => ThemeManager.GetColor(nameof(ThemePaletteData.AccentPrimary)),
                _ => ThemeManager.GetColor(nameof(ThemePaletteData.TextPrimary))
            };
        }

        private static void ConfigureOptionButton(OptionButton opt, int minWidth = 0)
        {
            if (opt == null) return;
            opt.FitToLongestItem = false;
            opt.ClipText = true;
            opt.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            if (minWidth > 0)
            {
                opt.CustomMinimumSize = new Vector2(minWidth, opt.CustomMinimumSize.Y);
            }
        }

        private static void SetLabelTone(Label lbl, StatusTone tone, ref StatusTone trackedTone, string text = null)
        {
            trackedTone = tone;
            if (lbl == null || !GodotObject.IsInstanceValid(lbl)) return;
            if (text != null) lbl.Text = text;
            lbl.AddThemeColorOverride("font_color", GetToneColor(tone));
        }

        private void SetBottomStatus(string text, StatusTone tone)
        {
            SetLabelTone(_lblBottomStatus, tone, ref _bottomStatusTone, text);
        }

        private void SetRigChipStatus(string text, StatusTone tone)
        {
            SetLabelTone(_lblRigStatusChip, tone, ref _rigChipTone, text);
        }

        private void SetRigBadgeStatus(string text, StatusTone tone)
        {
            SetLabelTone(_lblRigStatusBadge, tone, ref _rigBadgeTone, text);
        }

        private void SetButtonIcon(Button btn, string relPath, int baseIconSize = 22, StatusTone? semanticTone = null)
        {
            if (btn == null || !GodotObject.IsInstanceValid(btn)) return;
            _themedButtons[btn] = (relPath, baseIconSize, semanticTone);
            var icon = LoadIcon(relPath);
            if (icon != null)
            {
                int scaledSize = ThemeManager.GetScaledIconSize(baseIconSize);
                btn.Icon = icon;
                btn.ExpandIcon = true;
                btn.AddThemeConstantOverride("icon_max_width", scaledSize);
                btn.AddThemeConstantOverride("h_separation", 8);

                if (semanticTone.HasValue)
                {
                    Color c = GetToneColor(semanticTone.Value);
                    btn.AddThemeColorOverride("icon_normal_color", c);
                    btn.AddThemeColorOverride("icon_hover_color", c);
                    btn.AddThemeColorOverride("icon_pressed_color", c);
                    btn.AddThemeColorOverride("icon_hover_pressed_color", c);
                    btn.AddThemeColorOverride("icon_focus_color", c);
                }
                else
                {
                    btn.RemoveThemeColorOverride("icon_normal_color");
                    btn.RemoveThemeColorOverride("icon_hover_color");
                    btn.RemoveThemeColorOverride("icon_pressed_color");
                    btn.RemoveThemeColorOverride("icon_hover_pressed_color");
                    btn.RemoveThemeColorOverride("icon_focus_color");
                }
            }
        }

        private void ApplyThemeStyling()
        {
            if (_backdrop != null && GodotObject.IsInstanceValid(_backdrop))
            {
                _backdrop.Color = ThemeManager.GetModalBackdropColor();
            }

            if (_modalWindow != null && GodotObject.IsInstanceValid(_modalWindow))
            {
                var modalTheme = ThemeManager.Instance?.GetModalTheme();
                if (modalTheme != null)
                {
                    _modalWindow.Theme = modalTheme;
                }
            }

            Color textPrimary = ThemeManager.GetColor(nameof(ThemePaletteData.TextPrimary));
            Color textSecondary = ThemeManager.GetColor(nameof(ThemePaletteData.TextSecondary));
            Color accentPrimary = ThemeManager.GetColor(nameof(ThemePaletteData.AccentPrimary));

            if (_iconPerson != null && GodotObject.IsInstanceValid(_iconPerson))
            {
                int headerIconPx = ThemeManager.GetScaledIconSize(26);
                _iconPerson.CustomMinimumSize = new Vector2(headerIconPx, headerIconPx);
                _iconPerson.Modulate = accentPrimary;
            }

            PruneAndRecolor(_primaryLabels, textPrimary);
            PruneAndRecolor(_secondaryLabels, textSecondary);
            PruneAndRecolor(_accentLabels, accentPrimary);

            SetLabelTone(_lblRigStatusBadge, _rigBadgeTone, ref _rigBadgeTone);
            SetLabelTone(_lblRigStatusChip, _rigChipTone, ref _rigChipTone);
            SetLabelTone(_lblBottomStatus, _bottomStatusTone, ref _bottomStatusTone);

            var deadButtons = new List<Button>();
            foreach (var (btn, meta) in _themedButtons)
            {
                if (!GodotObject.IsInstanceValid(btn))
                {
                    deadButtons.Add(btn);
                    continue;
                }
                int scaled = ThemeManager.GetScaledIconSize(meta.BaseSize);
                btn.AddThemeConstantOverride("icon_max_width", scaled);
                if (meta.Tone.HasValue)
                {
                    Color c = GetToneColor(meta.Tone.Value);
                    btn.AddThemeColorOverride("icon_normal_color", c);
                    btn.AddThemeColorOverride("icon_hover_color", c);
                    btn.AddThemeColorOverride("icon_pressed_color", c);
                    btn.AddThemeColorOverride("icon_hover_pressed_color", c);
                    btn.AddThemeColorOverride("icon_focus_color", c);
                }
            }
            foreach (var db in deadButtons) _themedButtons.Remove(db);

            if (_boneCategorySections.Count > 0)
            {
                UpdateBoneMappingValidationBadges();
            }
            if (_rightTabs != null && GodotObject.IsInstanceValid(_rightTabs))
            {
                _rightTabs.AddThemeConstantOverride("icon_max_width", ThemeManager.GetScaledIconSize(16));
            }
        }

        private static void PruneAndRecolor(List<Label> labels, Color color)
        {
            for (int i = labels.Count - 1; i >= 0; i--)
            {
                if (!GodotObject.IsInstanceValid(labels[i]))
                {
                    labels.RemoveAt(i);
                    continue;
                }
                labels[i].AddThemeColorOverride("font_color", color);
            }
        }

        private void BuildLayout()
        {
            // Fullscreen Dimmed Backdrop
            _backdrop = new ColorRect
            {
                Name = "Backdrop",
                Color = ThemeManager.GetModalBackdropColor(),
                MouseFilter = Control.MouseFilterEnum.Stop
            };
            _backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_backdrop);

            // Centered Modal Window
            _modalWindow = new PanelContainer
            {
                Name = "ModalWindow",
                ThemeTypeVariation = "ModalPanel",
                CustomMinimumSize = new Vector2(680, 480)
            };
            _modalWindow.AnchorLeft = 0.02f;
            _modalWindow.AnchorRight = 0.98f;
            _modalWindow.AnchorTop = 0.02f;
            _modalWindow.AnchorBottom = 0.98f;
            _modalWindow.OffsetLeft = 0;
            _modalWindow.OffsetTop = 0;
            _modalWindow.OffsetRight = 0;
            _modalWindow.OffsetBottom = 0;

            var modalTheme = ThemeManager.Instance?.GetModalTheme();
            if (modalTheme != null)
            {
                _modalWindow.Theme = modalTheme;
            }
            _backdrop.AddChild(_modalWindow);

            var outerMargin = new MarginContainer();
            outerMargin.AddThemeConstantOverride("margin_left", 12);
            outerMargin.AddThemeConstantOverride("margin_top", 10);
            outerMargin.AddThemeConstantOverride("margin_right", 12);
            outerMargin.AddThemeConstantOverride("margin_bottom", 10);
            _modalWindow.AddChild(outerMargin);

            var mainVBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            mainVBox.AddThemeConstantOverride("separation", 8);
            outerMargin.AddChild(mainVBox);

            // --- 1. HEADER BAR ---
            var headerHBox = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            headerHBox.AddThemeConstantOverride("separation", 10);

            _iconPerson = new TextureRect
            {
                Texture = LoadIcon("res://assets/at-icons/person_body.svg"),
                CustomMinimumSize = new Vector2(ThemeManager.GetScaledIconSize(26), ThemeManager.GetScaledIconSize(26)),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                Modulate = ThemeManager.GetColor(nameof(ThemePaletteData.AccentPrimary))
            };
            headerHBox.AddChild(_iconPerson);

            _lblTitle = new Label
            {
                Text = "CUSTOM 3D MODEL IMPORTER & AUTO-RIGGER",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsStretchRatio = 1.4f,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
            };
            _primaryLabels.Add(_lblTitle);
            headerHBox.AddChild(_lblTitle);

            _lblFileBadge = new Label
            {
                Text = "No file loaded",
                HorizontalAlignment = HorizontalAlignment.Right,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsStretchRatio = 0.6f,
                CustomMinimumSize = new Vector2(100, 0),
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
            };
            _secondaryLabels.Add(_lblFileBadge);
            ImporterStyle.ApplyHintStyle(_lblFileBadge, wrap: false);
            headerHBox.AddChild(_lblFileBadge);

            var optUiScale = new OptionButton
            {
                CustomMinimumSize = new Vector2(88, 34),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Importer UI text size (scales fonts without changing panel width)"
            };
            ConfigureOptionButton(optUiScale, 88);
            optUiScale.AddItem("100%", 100);
            optUiScale.AddItem("115%", 115);
            optUiScale.AddItem("130%", 130);
            int savedScale = ImporterStyle.ScalePercent;
            optUiScale.Select(savedScale == 130 ? 2 : (savedScale == 115 ? 1 : 0));
            optUiScale.ItemSelected += (idx) =>
            {
                int pct = optUiScale.GetItemId((int)idx);
                ImporterStyle.ScalePercent = pct;
                ImporterStyle.RefreshFontScaleRecursive(_modalWindow);
            };
            headerHBox.AddChild(optUiScale);

            _btnCloseX = new Button
            {
                ThemeTypeVariation = "IconButton",
                CustomMinimumSize = new Vector2(36, 36),
                TooltipText = "Cancel & Close Modal"
            };
            SetButtonIcon(_btnCloseX, "res://assets/at-icons/cross.svg", 22);
            _btnCloseX.Pressed += OnCancel;
            headerHBox.AddChild(_btnCloseX);
            mainVBox.AddChild(headerHBox);

            // --- 2. BODY SPLIT (3D PREVIEW on Left | CONFIG INSPECTOR on Right) ---
            var bodySplit = new HSplitContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill
            };
            mainVBox.AddChild(bodySplit);

            // Left Side: 3D Preview Column (VBoxContainer)
            var previewVBox = new VBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                SizeFlagsStretchRatio = 1.35f,
                CustomMinimumSize = new Vector2(320, 0)
            };
            previewVBox.AddThemeConstantOverride("separation", 6);
            bodySplit.AddChild(previewVBox);

            // 1. Top Preview Toolbar Panel (Theme-aware, responsive HFlowContainer rows)
            var toolbarPanel = new PanelContainer
            {
                ThemeTypeVariation = "ModalHeaderPanel",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkBegin
            };
            previewVBox.AddChild(toolbarPanel);

            var tbVBox = new VBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
            };
            tbVBox.AddThemeConstantOverride("separation", 4);
            toolbarPanel.AddChild(tbVBox);

            var tbRow1 = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            tbRow1.AddThemeConstantOverride("h_separation", 8);
            tbRow1.AddThemeConstantOverride("v_separation", 4);
            tbVBox.AddChild(tbRow1);

            var tbRow2 = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            tbRow2.AddThemeConstantOverride("h_separation", 8);
            tbRow2.AddThemeConstantOverride("v_separation", 4);
            tbVBox.AddChild(tbRow2);

            // --- Row 1: Viewport Display, X-Ray & 3D Transform Gizmo ---
            _btnToggleGhost = new Button
            {
                Text = "Ghost",
                ThemeTypeVariation = "ToolbarButton",
                ToggleMode = true,
                ButtonPressed = true,
                CustomMinimumSize = new Vector2(86, 32),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Toggle reference hero ghost mannequin visibility"
            };
            SetButtonIcon(_btnToggleGhost, "res://assets/at-icons/eye.svg", 20);
            _btnToggleGhost.Toggled += (t) => SetHeroGhostEnabled(t);
            tbRow1.AddChild(_btnToggleGhost);

            _sliderGhostOpacity = new HSlider
            {
                MinValue = 0.05,
                MaxValue = 1.0,
                Step = 0.05,
                Value = 0.35,
                CustomMinimumSize = new Vector2(58, 24),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Reference hero ghost opacity"
            };
            _sliderGhostOpacity.ValueChanged += (v) =>
            {
                _ghostOpacity = (float)v;
                if (_ghostMaterial != null)
                {
                    _ghostMaterial.AlbedoColor = new Color(0.18f, 0.75f, 0.95f, _ghostOpacity);
                }
            };
            tbRow1.AddChild(_sliderGhostOpacity);

            _btnToggleBones = new Button
            {
                Text = "Bones",
                ThemeTypeVariation = "ToolbarButton",
                ToggleMode = true,
                ButtonPressed = false,
                CustomMinimumSize = new Vector2(80, 32),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Toggle skeleton bone wireframe overlay (Left-click bone to select, Right-click bone to disable/enable)"
            };
            SetButtonIcon(_btnToggleBones, "res://assets/at-icons/bone.svg", 20);
            _btnToggleBones.Toggled += (t) =>
            {
                _showBones = t;
                if (_bonesDebugMesh != null) _bonesDebugMesh.Visible = t;
                if (_bonesJointPointsMesh != null && GodotObject.IsInstanceValid(_bonesJointPointsMesh)) _bonesJointPointsMesh.Visible = t;
                if (_boneLegendRow != null) _boneLegendRow.Visible = t;
                if (_btnToggleBonesOnTop != null) _btnToggleBonesOnTop.Disabled = !t;
                if (t) RedrawBoneWireframe();
            };
            tbRow1.AddChild(_btnToggleBones);

            _btnToggleBonesOnTop = new Button
            {
                Text = "On Top",
                ThemeTypeVariation = "ToolbarButton",
                ToggleMode = true,
                ButtonPressed = _bonesOnTop,
                Disabled = true,
                CustomMinimumSize = new Vector2(68, 32),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Render bone wireframe & joint points on top of meshes (NoDepthTest)"
            };
            SetButtonIcon(_btnToggleBonesOnTop, "res://assets/at-icons/eye.svg", 16);
            _btnToggleBonesOnTop.Toggled += (on) =>
            {
                _bonesOnTop = on;
                if (_showBones) RedrawBoneWireframe();
            };
            tbRow1.AddChild(_btnToggleBonesOnTop);

            _btnToggleColliders = new Button
            {
                Text = "Colliders",
                ThemeTypeVariation = "ToolbarButton",
                ToggleMode = true,
                ButtonPressed = _showColliders,
                CustomMinimumSize = new Vector2(96, 32),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Show / hide mesh-fitted body collision capsules (used by preview & Source 2 cloth physics)"
            };
            SetButtonIcon(_btnToggleColliders, "res://assets/at-icons/circle.svg", 16);
            _btnToggleColliders.Toggled += (t) =>
            {
                _showColliders = t;
                if (_collidersDebugMesh != null) _collidersDebugMesh.Visible = t;
                if (t) { _collidersDirty = true; RedrawColliderWireframe(); }
            };
            tbRow1.AddChild(_btnToggleColliders);

            foreach (string camPreset in new[] { "Front", "Side", "3/4", "Frame" })
            {
                var btnCam = new Button
                {
                    Text = camPreset,
                    ThemeTypeVariation = "ToolbarButton",
                    CustomMinimumSize = new Vector2(46, 30),
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                    TooltipText = $"Snap preview camera to {camPreset} view"
                };
                string capturedCam = camPreset;
                btnCam.Pressed += () => SetCameraPreset(capturedCam);
                tbRow1.AddChild(btnCam);
            }

            _btnToggleXRay = new Button
            {
                Text = "X-Ray",
                ThemeTypeVariation = "ToolbarButton",
                ToggleMode = true,
                ButtonPressed = false,
                CustomMinimumSize = new Vector2(78, 32),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "See-through translucent mesh + X-Ray wireframe to inspect internal bones and back-side vertices"
            };
            SetButtonIcon(_btnToggleXRay, "res://assets/at-icons/eye.svg", 18);
            _btnToggleXRay.Toggled += (t) =>
            {
                _isXRayMode = t;
                if (IsWeightPaintActive()) RefreshWeightPaintHeatmap();
                else RefreshViewportShadingAndWireframes();
            };
            tbRow1.AddChild(_btnToggleXRay);

            var lblShading = new Label { Text = "View:", SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            _secondaryLabels.Add(lblShading);
            tbRow1.AddChild(lblShading);

            _optViewportShading = new OptionButton
            {
                CustomMinimumSize = new Vector2(128, 32),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Viewport mesh shading mode: Textured, Solid + Wireframe overlay, or Wireframe cage"
            };
            ConfigureOptionButton(_optViewportShading, 128);
            _optViewportShading.AddItem("Textured", (int)CustomWeightPaintEditor.ViewportShadingMode.Textured);
            _optViewportShading.AddItem("Solid + Wire", (int)CustomWeightPaintEditor.ViewportShadingMode.SolidWire);
            _optViewportShading.AddItem("Wireframe", (int)CustomWeightPaintEditor.ViewportShadingMode.Wireframe);
            _optViewportShading.Select(0);
            _optViewportShading.ItemSelected += (idx) =>
            {
                _viewportShadingMode = (CustomWeightPaintEditor.ViewportShadingMode)_optViewportShading.GetItemId((int)idx);
                RefreshViewportShadingAndWireframes();
            };
            tbRow1.AddChild(_optViewportShading);

            tbRow1.AddChild(new VSeparator());

            var lblGizmo = new Label { Text = "Gizmo:", SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            _secondaryLabels.Add(lblGizmo);
            tbRow1.AddChild(lblGizmo);

            _optGizmoTarget = new OptionButton
            {
                CustomMinimumSize = new Vector2(142, 32),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Select which target the interactive 3D move/rotate gizmo controls"
            };
            ConfigureOptionButton(_optGizmoTarget, 142);
            _optGizmoTarget.AddItem("Off", 0);
            _optGizmoTarget.AddItem("Weapon (R)", 1);
            _optGizmoTarget.AddItem("Weapon (L)", 2);
            _optGizmoTarget.AddItem("Model Root", 3);
            _optGizmoTarget.AddItem("Kept Part (Hero)", 4);
            _optGizmoTarget.ItemSelected += OnGizmoTargetChanged;
            tbRow1.AddChild(_optGizmoTarget);

            _btnGizmoLocalSpace = new Button
            {
                Text = "Global",
                ThemeTypeVariation = "ToolbarButton",
                ToggleMode = true,
                ButtonPressed = false,
                Disabled = true,
                CustomMinimumSize = new Vector2(68, 32),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Toggle gizmo axes between Global (world X/Y/Z) and Local (object X/Y/Z)"
            };
            _btnGizmoLocalSpace.Toggled += (_) => SyncGizmoSpaceButton();
            tbRow1.AddChild(_btnGizmoLocalSpace);

            _btnSnapGripPalm = new Button
            {
                Text = "Snap Grip",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(104, 32),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Align active weapon grip into the hero's hand palm with forward rotation"
            };
            SetButtonIcon(_btnSnapGripPalm, "res://assets/at-icons/sparkles.svg", 18);
            _btnSnapGripPalm.Pressed += OnSnapGripToPalmPressed;
            tbRow1.AddChild(_btnSnapGripPalm);

            _btnStraightenWeapon = new Button
            {
                Text = "Straighten",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(104, 32),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Auto-detect weapon barrel axis and straighten forward (+Z)"
            };
            SetButtonIcon(_btnStraightenWeapon, "res://assets/at-icons/sparkles.svg", 18);
            _btnStraightenWeapon.Pressed += OnStraightenWeaponPressed;
            tbRow1.AddChild(_btnStraightenWeapon);

            // --- Row 2: Animation Preview Playback ---
            var lblAnim = new Label { Text = "Preview Anim:", SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            _secondaryLabels.Add(lblAnim);
            tbRow2.AddChild(lblAnim);

            _optTestAnim = new OptionButton
            {
                CustomMinimumSize = new Vector2(220, 32),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Preview how the rigged model or accessory follows animations in real time"
            };
            ConfigureOptionButton(_optTestAnim, 220);
            _optTestAnim.AddItem("Bind Pose (Rest)", 0);
            _optTestAnim.ItemSelected += OnTestAnimSelected;
            tbRow2.AddChild(_optTestAnim);

            // Transport: Play / Pause toggle
            _btnAnimPlayPause = new Button
            {
                ThemeTypeVariation = "IconButton",
                ToggleMode = true,
                ButtonPressed = false,
                CustomMinimumSize = new Vector2(34, 32),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Play / Pause Animation"
            };
            SetButtonIcon(_btnAnimPlayPause, "res://assets/at-icons/play.svg", 20);
            _btnAnimPlayPause.Toggled += (playing) =>
            {
                if (_previewAnimPlayer == null) return;
                if (playing)
                {
                    if (!_previewAnimPlayer.IsPlaying())
                    {
                        string activeAnim = !string.IsNullOrEmpty(_previewAnimPlayer.CurrentAnimation)
                            ? _previewAnimPlayer.CurrentAnimation
                            : _previewAnimPlayer.AssignedAnimation;
                        if (!string.IsNullOrEmpty(activeAnim) && _previewAnimPlayer.HasAnimation(activeAnim))
                        {
                            double scrubPos = _sliderAnimTimeline != null ? _sliderAnimTimeline.Value : 0.0;
                            _previewAnimPlayer.Play(activeAnim);
                            if (scrubPos > 0.0 && scrubPos < _previewAnimPlayer.CurrentAnimationLength)
                            {
                                _previewAnimPlayer.Seek(scrubPos, update: true);
                            }
                        }
                        else if (_optTestAnim.Selected > 0)
                        {
                            OnTestAnimSelected(_optTestAnim.Selected);
                        }
                    }
                    SetButtonIcon(_btnAnimPlayPause, "res://assets/at-icons/pause.svg", 20);
                }
                else
                {
                    _previewAnimPlayer.Pause();
                    SetButtonIcon(_btnAnimPlayPause, "res://assets/at-icons/play.svg", 20);
                }
            };
            tbRow2.AddChild(_btnAnimPlayPause);

            // Transport: Scrub Timeline Slider
            _sliderAnimTimeline = new HSlider
            {
                MinValue = 0.0,
                MaxValue = 1.0,
                Step = 0.01,
                Value = 0.0,
                CustomMinimumSize = new Vector2(140, 24),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Scrub Animation Timeline"
            };
            _sliderAnimTimeline.DragStarted += () => { _isScrubbingAnimTimeline = true; };
            _sliderAnimTimeline.DragEnded += (_) => { _isScrubbingAnimTimeline = false; };
            _sliderAnimTimeline.FocusExited += () => { _isScrubbingAnimTimeline = false; };
            _sliderAnimTimeline.ValueChanged += (v) =>
            {
                if (_isUpdatingTimelineSlider || _previewAnimPlayer == null) return;
                string activeAnim = !string.IsNullOrEmpty(_previewAnimPlayer.CurrentAnimation)
                    ? _previewAnimPlayer.CurrentAnimation
                    : _previewAnimPlayer.AssignedAnimation;

                if (string.IsNullOrEmpty(activeAnim) && _optTestAnim != null && _optTestAnim.Selected > 0)
                {
                    bool wasBtnPressed = _btnAnimPlayPause != null && _btnAnimPlayPause.ButtonPressed;
                    OnTestAnimSelected(_optTestAnim.Selected);
                    if (!wasBtnPressed)
                    {
                        _previewAnimPlayer.Pause();
                        if (_btnAnimPlayPause != null)
                        {
                            _btnAnimPlayPause.SetPressedNoSignal(false);
                            SetButtonIcon(_btnAnimPlayPause, "res://assets/at-icons/play.svg", 20);
                        }
                    }
                    activeAnim = !string.IsNullOrEmpty(_previewAnimPlayer.CurrentAnimation)
                        ? _previewAnimPlayer.CurrentAnimation
                        : _previewAnimPlayer.AssignedAnimation;
                }

                if (string.IsNullOrEmpty(activeAnim) || !_previewAnimPlayer.HasAnimation(activeAnim)) return;

                bool wasPlaying = _previewAnimPlayer.IsPlaying();
                if (string.IsNullOrEmpty(_previewAnimPlayer.CurrentAnimation))
                {
                    // Godot clears CurrentAnimation when a non-looping clip stops; re-activate so Seek updates transforms
                    _previewAnimPlayer.Play(activeAnim);
                    if (!wasPlaying) _previewAnimPlayer.Pause();
                }

                double maxLen = Math.Max(0.01, _previewAnimPlayer.CurrentAnimationLength);
                double clampedPos = Math.Clamp(v, 0.0, maxLen);
                _previewAnimPlayer.Seek(clampedPos, update: true);

                if (_previewHeroSkeleton != null && _previewRiggedContainer != null && GodotObject.IsInstanceValid(_previewRiggedContainer))
                {
                    bool clampStretch = _chkClampLimbStretching?.ButtonPressed ?? true;
                    CustomModelAutoRigger.UpdateCustomModelRuntimePosing(_previewHeroSkeleton, clampStretch);
                }
                _previewConstraintDriver?.EvaluateNow(0f);

                if (_lblAnimTime != null)
                {
                    _lblAnimTime.Text = $"{clampedPos:F2} / {maxLen:F2}s";
                }
                _collidersDirty = true;
                if (_showColliders) RedrawColliderWireframe();
            };
            tbRow2.AddChild(_sliderAnimTimeline);

            // Transport: Time Label
            _lblAnimTime = new Label
            {
                Text = "0.00s",
                CustomMinimumSize = new Vector2(82, 0),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
            };
            _secondaryLabels.Add(_lblAnimTime);
            tbRow2.AddChild(_lblAnimTime);

            // Transport: Loop Toggle
            _btnAnimLoop = new Button
            {
                ThemeTypeVariation = "IconButton",
                ToggleMode = true,
                ButtonPressed = true,
                CustomMinimumSize = new Vector2(34, 32),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Loop Animation"
            };
            SetButtonIcon(_btnAnimLoop, "res://assets/at-icons/arrows_clockwise.svg", 20);
            _btnAnimLoop.Toggled += (loop) =>
            {
                SetButtonIcon(_btnAnimLoop, "res://assets/at-icons/arrows_clockwise.svg", 20);
                if (_previewAnimPlayer != null && !string.IsNullOrEmpty(_previewAnimPlayer.CurrentAnimation))
                {
                    var anim = _previewAnimPlayer.GetAnimation(_previewAnimPlayer.CurrentAnimation);
                    if (anim != null)
                    {
                        anim.LoopMode = loop ? Godot.Animation.LoopModeEnum.Linear : Godot.Animation.LoopModeEnum.None;
                    }
                }
            };
            tbRow2.AddChild(_btnAnimLoop);

            // Transport: Speed Selector
            _optAnimSpeed = new OptionButton
            {
                CustomMinimumSize = new Vector2(68, 32),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Animation Playback Speed"
            };
            ConfigureOptionButton(_optAnimSpeed, 68);
            _optAnimSpeed.AddItem("0.25x", 0);
            _optAnimSpeed.AddItem("0.5x", 1);
            _optAnimSpeed.AddItem("1.0x", 2);
            _optAnimSpeed.AddItem("2.0x", 3);
            _optAnimSpeed.Select(2); // 1.0x
            _optAnimSpeed.ItemSelected += (idx) =>
            {
                float spd = idx switch
                {
                    0 => 0.25f,
                    1 => 0.5f,
                    2 => 1.0f,
                    3 => 2.0f,
                    _ => 1.0f
                };
                if (_previewAnimPlayer != null)
                {
                    _previewAnimPlayer.SpeedScale = spd;
                }
            };
            tbRow2.AddChild(_optAnimSpeed);

            // Interactive Skeleton Overlay Filter Legend (shown when Bones toggle is on)
            _boneLegendRow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            _boneLegendRow.AddThemeConstantOverride("h_separation", 8);
            _boneLegendRow.AddThemeConstantOverride("v_separation", 4);
            CheckBox AddLegendFilterCheck(string txt, Color col, string tip)
            {
                var chk = new CheckBox
                {
                    Text = "■ " + txt,
                    ButtonPressed = true,
                    TooltipText = tip,
                    FocusMode = Control.FocusModeEnum.None
                };
                ImporterStyle.ApplyFontSize(chk, ImporterStyle.FontHint);
                chk.AddThemeColorOverride("font_color", col);
                chk.AddThemeColorOverride("font_pressed_color", col);
                chk.AddThemeColorOverride("font_hover_color", col);
                chk.AddThemeColorOverride("font_hover_pressed_color", col);
                chk.Toggled += (_) => { if (_showBones) RedrawBoneWireframe(); };
                _boneLegendRow.AddChild(chk);
                return chk;
            }
            _chkBoneFilterCore = AddLegendFilterCheck("Core", new Color(0.25f, 0.88f, 0.98f), "Show/hide Wraith/hero core skeleton bones");
            _chkBoneFilterFingers = AddLegendFilterCheck("Fingers", new Color(0.30f, 0.95f, 0.45f), "Show/hide finger phalanx bones");
            _chkBoneFilterPhysics = AddLegendFilterCheck("Clothing/Jiggle", new Color(1.0f, 0.82f, 0.18f), "Show/hide imported clothing, hair, and jiggle physics bones");
            _chkBoneFilterAccessory = AddLegendFilterCheck("Accessory", new Color(0.95f, 0.35f, 0.90f), "Show/hide accessory attachment chain bones");
            _chkBoneFilterDisabled = AddLegendFilterCheck("Disabled", new Color(0.95f, 0.25f, 0.25f), "Show/hide disabled (right-clicked) bones");
            tbVBox.AddChild(_boneLegendRow);

            // 2. 3D Preview Viewport Panel (occupies all remaining space below toolbar)
            var viewportPanel = new PanelContainer
            {
                ThemeTypeVariation = "SubPanelContainer",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(300, 260)
            };
            previewVBox.AddChild(viewportPanel);

            // SubViewportContainer & Isolated SubViewport
            _subViewportContainer = new SubViewportContainer
            {
                Stretch = true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Stop
            };
            viewportPanel.AddChild(_subViewportContainer);

            _subViewport = new SubViewport
            {
                OwnWorld3D = true,
                World3D = new World3D(),
                TransparentBg = false,
                Msaa3D = Viewport.Msaa.Msaa2X,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always
            };
            _subViewportContainer.AddChild(_subViewport);

            SetupPreview3DScene();
            SetupPreviewInputHandling();
            BuildSelectedBoneBadgeOverlay(viewportPanel);
            BuildWeightPaintViewportHud(viewportPanel);

            // Right Side: Tabbed Configuration Inspector
            var rightPanel = new PanelContainer
            {
                ThemeTypeVariation = "InspectorSectionPanel",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                SizeFlagsStretchRatio = 1.0f,
                CustomMinimumSize = new Vector2(340, 0)
            };
            bodySplit.AddChild(rightPanel);

            _rightTabs = new TabContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                ClipTabs = true
            };
            rightPanel.AddChild(_rightTabs);

            _rightTabs.AddChild(BuildTab1ImportAndPlace());
            _rightTabs.AddChild(BuildTab2Rig());
            _rightTabs.AddChild(BuildTab3Bones());
            _rightTabs.AddChild(BuildTab4MeshesAndMaterials());
            _rightTabs.AddChild(BuildTab5ReviewAndExport());

            _rightTabs.AddThemeConstantOverride("icon_max_width", ThemeManager.GetScaledIconSize(16));
            (string title, string iconPath)[] tabMeta =
            {
                ("Import & Place", "res://assets/at-icons/person_body.svg"),
                ("Rig", "res://assets/at-icons/sliders.svg"),
                ("Bones", "res://assets/at-icons/bone.svg"),
                ("Meshes", "res://assets/at-icons/layers.svg"),
                ("Export", "res://assets/at-icons/file_arrow_down.svg")
            };
            for (int ti = 0; ti < tabMeta.Length && ti < _rightTabs.GetTabCount(); ti++)
            {
                _rightTabs.SetTabTitle(ti, tabMeta[ti].title);
                var tabIcon = LoadIcon(tabMeta[ti].iconPath);
                if (tabIcon != null) _rightTabs.SetTabIcon(ti, tabIcon);
            }
            _rightTabs.TabChanged += OnRightTabChanged;

            // --- 3. COLLAPSIBLE LOG CONSOLE & BOTTOM FOOTER BAR ---
            _consolePanel = new PanelContainer
            {
                ThemeTypeVariation = "SubPanelContainer",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 130),
                Visible = false
            };
            _txtLogConsole = new TextEdit
            {
                Editable = false,
                WrapMode = TextEdit.LineWrappingMode.Boundary,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                PlaceholderText = "Import & bind quality logs will appear here..."
            };
            ImporterStyle.ApplyFontSize(_txtLogConsole, ImporterStyle.FontHint);
            _consolePanel.AddChild(_txtLogConsole);
            mainVBox.AddChild(_consolePanel);

            var footerHBox = new HBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 42)
            };
            footerHBox.AddThemeConstantOverride("separation", 10);
            mainVBox.AddChild(footerHBox);

            _lblBottomStatus = new Label
            {
                Text = "Ready to align, test rig, or apply.",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
            };
            SetBottomStatus("Ready to align, test rig, or apply.", StatusTone.Info);
            footerHBox.AddChild(_lblBottomStatus);

            _lblMemoryStatus = new Label
            {
                Text = ImporterMemoryGuard.FormatFooterMemoryText(),
                VerticalAlignment = VerticalAlignment.Center,
                CustomMinimumSize = new Vector2(140, 38),
                TooltipText = "Current process memory usage vs free physical RAM"
            };
            ImporterStyle.ApplyHintStyle(_lblMemoryStatus, wrap: false);
            _secondaryLabels.Add(_lblMemoryStatus);
            footerHBox.AddChild(_lblMemoryStatus);

            _btnToggleConsole = new Button
            {
                Text = "Console",
                ThemeTypeVariation = "ToolbarButton",
                ToggleMode = true,
                ButtonPressed = false,
                CustomMinimumSize = new Vector2(82, 34),
                TooltipText = "Show/hide bind quality & import report log console"
            };
            _btnToggleConsole.Toggled += (on) =>
            {
                if (_consolePanel != null) _consolePanel.Visible = on;
            };
            footerHBox.AddChild(_btnToggleConsole);

            _btnManualRigUpdate = new Button
            {
                Text = "Update Rig",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(96, 34),
                Visible = ImporterMemoryGuard.LightMode,
                TooltipText = "Manually re-bake preview rig (automatic in normal mode, manual in Light Mode)"
            };
            _btnManualRigUpdate.Pressed += ExecuteLiveRigUpdate;
            footerHBox.AddChild(_btnManualRigUpdate);

            _btnCancel = new Button
            {
                Text = "Discard & Cancel",
                ThemeTypeVariation = "SecondaryButton",
                CustomMinimumSize = new Vector2(128, 38)
            };
            SetButtonIcon(_btnCancel, "res://assets/at-icons/cross.svg", 22, StatusTone.Danger);
            _btnCancel.Pressed += OnCancel;
            footerHBox.AddChild(_btnCancel);

            _lblRigStatusChip = new Label
            {
                Text = "Live Rig: Ready",
                VerticalAlignment = VerticalAlignment.Center,
                CustomMinimumSize = new Vector2(110, 38),
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
            };
            SetRigChipStatus("Live Rig: Ready", StatusTone.Success);
            footerHBox.AddChild(_lblRigStatusChip);

            _btnConfirmApply = new Button
            {
                Text = "Confirm & Apply",
                ThemeTypeVariation = "ImporterPrimaryButton",
                CustomMinimumSize = new Vector2(180, ImporterStyle.PrimaryButtonHeight),
                TooltipText = "Applies rigging and binding onto the live hero in the main viewport"
            };
            SetButtonIcon(_btnConfirmApply, "res://assets/at-icons/checkmark.svg", ImporterStyle.IconSize);
            _btnConfirmApply.Pressed += OnConfirmApplyToHero;
            footerHBox.AddChild(_btnConfirmApply);

            ImporterStyle.RefreshFontScaleRecursive(_modalWindow);
        }

        #region 3D Scene & Camera Setup

        private void SetupPreview3DScene()
        {
            _previewWorldRoot = new Node3D { Name = "PreviewWorldRoot" };
            _subViewport.AddChild(_previewWorldRoot);

            // WorldEnvironment
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.11f, 0.12f, 0.15f),
                AmbientLightColor = new Color(0.52f, 0.54f, 0.60f),
                AmbientLightEnergy = 0.8f,
                TonemapMode = Godot.Environment.ToneMapper.Aces
            };
            var worldEnv = new WorldEnvironment { Environment = env };
            _previewWorldRoot.AddChild(worldEnv);

            // Studio 3-Point Lights
            var keyLight = new DirectionalLight3D
            {
                LightColor = new Color(1.0f, 0.98f, 0.92f),
                LightEnergy = 1.25f,
                RotationDegrees = new Vector3(-35, 45, 0)
            };
            _previewWorldRoot.AddChild(keyLight);

            var fillLight = new DirectionalLight3D
            {
                LightColor = new Color(0.65f, 0.75f, 0.90f),
                LightEnergy = 0.65f,
                RotationDegrees = new Vector3(-15, -135, 0)
            };
            _previewWorldRoot.AddChild(fillLight);

            var rimLight = new DirectionalLight3D
            {
                LightColor = new Color(0.88f, 0.92f, 1.0f),
                LightEnergy = 0.75f,
                RotationDegrees = new Vector3(25, 180, 0)
            };
            _previewWorldRoot.AddChild(rimLight);

            // Studio Floor Grid
            var floorMesh = new PlaneMesh { Size = new Vector2(20, 20) };
            var floorMat = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.08f, 0.09f, 0.11f),
                Roughness = 0.9f
            };
            var floorInstance = new MeshInstance3D { Mesh = floorMesh, MaterialOverride = floorMat, Position = Vector3.Zero };
            _previewWorldRoot.AddChild(floorInstance);

            // Bones debug wireframe mesh
            _bonesDebugMesh = new MeshInstance3D { Name = "BonesDebugMesh", Visible = false, Layers = 1u };
            _previewWorldRoot.AddChild(_bonesDebugMesh);

            // Body collision capsule debug wireframe mesh (strictly Layer 1, never Layer 21)
            _collidersDebugMesh = new MeshInstance3D { Name = "CollidersDebugMesh", Visible = false, Layers = 1u };
            _previewWorldRoot.AddChild(_collidersDebugMesh);

            // Camera
            _previewCamera = new Camera3D
            {
                Fov = 45f,
                Near = 0.05f,
                Far = 100f
            };
            _previewWorldRoot.AddChild(_previewCamera);
            UpdateCamera();

            // Interactive 3D Transformation Gizmo
            _previewGizmo = new Gizmo3D
            {
                Name = "PreviewGizmo3D",
                Visible = false,
                Layers = 1,
                Size = 75f
            };
            _previewGizmo.Mode = Gizmo3D.ToolMode.Rotate;
            _previewWorldRoot.AddChild(_previewGizmo);

            _gizmoDummyTarget = new Node3D { Name = "GizmoDummyTarget" };
            _previewWorldRoot.AddChild(_gizmoDummyTarget);

            _previewGizmo.TransformBegin += (_) =>
            {
                _isGizmoDragging = true;
                _userModifiedTransform = true;
            };
            _previewGizmo.TransformChanged += OnGizmoTransformChanged;
            _previewGizmo.TransformEnd += OnGizmoTransformEnd;
        }

        private void SetupPreviewInputHandling()
        {
            _subViewportContainer.GuiInput += (InputEvent @event) =>
            {
                // Forward input to SubViewport for Gizmo3D
                _subViewport.PushInput(@event);

                if (HandleWeightPaintInput(@event)) return;
                if (HandleHeroDeformBrushInput(@event)) return;
                if (HandlePinInput(@event)) return;
                if (TryHandleViewportBoneLeftClick(@event))
                {
                    _isOrbiting = false;
                    GetViewport().SetInputAsHandled();
                    return;
                }
                if (TryHandleViewportBoneRightClick(@event))
                {
                    _isPanning = false;
                    GetViewport().SetInputAsHandled();
                    return;
                }

                if (_previewGizmo != null && (_previewGizmo.Editing || _previewGizmo.Hovering))
                {
                    if (@event is InputEventMouseButton mbPressed && mbPressed.Pressed)
                    {
                        _isOrbiting = false;
                        _isPanning = false;
                    }
                    return;
                }

                if (@event is InputEventMouseButton mb)
                {
                    if (mb.ButtonIndex == MouseButton.WheelUp)
                    {
                        _orbitDist = Mathf.Max(0.25f, _orbitDist * 0.90f);
                        UpdateCamera();
                        GetViewport().SetInputAsHandled();
                    }
                    else if (mb.ButtonIndex == MouseButton.WheelDown)
                    {
                        _orbitDist = Mathf.Min(12f, _orbitDist * 1.10f);
                        UpdateCamera();
                        GetViewport().SetInputAsHandled();
                    }
                    else if (mb.ButtonIndex == MouseButton.Left || mb.ButtonIndex == MouseButton.Middle)
                    {
                        if (mb.Pressed)
                        {
                            if (mb.ShiftPressed || mb.ButtonIndex == MouseButton.Right)
                            {
                                _isPanning = true;
                            }
                            else
                            {
                                _isOrbiting = true;
                            }
                            _lastMousePos = mb.Position;
                        }
                        else
                        {
                            _isOrbiting = false;
                            _isPanning = false;
                        }
                        GetViewport().SetInputAsHandled();
                    }
                    else if (mb.ButtonIndex == MouseButton.Right)
                    {
                        _isPanning = mb.Pressed;
                        _lastMousePos = mb.Position;
                        GetViewport().SetInputAsHandled();
                    }
                }
                else if (@event is InputEventMouseMotion mm)
                {
                    Vector2 delta = mm.Position - _lastMousePos;
                    _lastMousePos = mm.Position;

                    if (_isOrbiting)
                    {
                        _orbitYaw -= delta.X * 0.008f;
                        _orbitPitch = Mathf.Clamp(_orbitPitch - delta.Y * 0.008f, -Mathf.Pi * 0.48f, Mathf.Pi * 0.48f);
                        UpdateCamera();
                        GetViewport().SetInputAsHandled();
                    }
                    else if (_isPanning)
                    {
                        Vector3 camRight = _previewCamera.GlobalTransform.Basis.X;
                        Vector3 camUp = _previewCamera.GlobalTransform.Basis.Y;
                        float panSpeed = _orbitDist * 0.0015f;
                        _orbitTarget += (-camRight * delta.X + camUp * delta.Y) * panSpeed;
                        UpdateCamera();
                        GetViewport().SetInputAsHandled();
                    }
                }
            };
        }

        private void UpdateCamera()
        {
            if (_previewCamera == null) return;
            Vector3 camPos = _orbitTarget + new Vector3(
                _orbitDist * Mathf.Cos(_orbitPitch) * Mathf.Sin(_orbitYaw),
                _orbitDist * Mathf.Sin(_orbitPitch),
                _orbitDist * Mathf.Cos(_orbitPitch) * Mathf.Cos(_orbitYaw)
            );
            _previewCamera.GlobalPosition = camPos;
            _previewCamera.LookAt(_orbitTarget, Vector3.Up);
        }

        private void RedrawBoneWireframe()
        {
            RedrawBoneWireframeColorCoded();
        }

        #endregion

        #region Tab 1: Align & Scale

        private (PanelContainer Card, VBoxContainer Body) CreateInspectorCard(
            string sectionTitle,
            string subtitle = null,
            bool collapsible = false,
            bool startExpanded = true)
        {
            var card = new CollapsibleCard(sectionTitle, subtitle, collapsible, startExpanded);
            if (card.TitleLabel != null) _accentLabels.Add(card.TitleLabel);
            if (card.SubtitleLabel != null) _secondaryLabels.Add(card.SubtitleLabel);
            return (card, card.Body);
        }


        private static SpinBox CreateCoordSpinBox(double min, double max, double step, string prefix)
        {
            var sb = new SpinBox
            {
                MinValue = min,
                MaxValue = max,
                Step = step,
                Value = 0.0,
                Prefix = prefix + ":",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            return sb;
        }

        #endregion

        private void PopulateRiggingMethodOptions(bool hasRig)
        {
            if (_optRiggingMethod == null) return;
            _optRiggingMethod.Clear();
            if (hasRig)
            {
                _optRiggingMethod.AddItem("Skeleton Bone Remap (Recommended — Preserves Rig)", (int)CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap);
                _optRiggingMethod.AddItem("Landmark Auto-Rig (Rebuild Rig from 16 Pins)", (int)CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig);
                _optRiggingMethod.AddItem("Rigid Bone Attachment (Props / Hats / Weapons)", (int)CustomModelAutoRigger.RiggingMethod.RigidSingleBone);
                _optRiggingMethod.AddItem("Legacy: Surface Weight Transfer (KNN Fallback)", (int)CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection);
            }
            else
            {
                _optRiggingMethod.AddItem("Landmark Auto-Rig (Recommended — 16 Joint Pins)", (int)CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig);
                _optRiggingMethod.AddItem("Rigid Bone Attachment (Props / Hats / Weapons)", (int)CustomModelAutoRigger.RiggingMethod.RigidSingleBone);
                _optRiggingMethod.AddItem("Legacy: Surface Weight Transfer (KNN Fallback)", (int)CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection);
            }
            _optRiggingMethod.Select(0);
            RefreshMethodCards(hasRig);
        }

        private void SetLandmarkWorkspaceStep(bool paintStep)
        {
            if (_btnLandmarkStepPins != null) _btnLandmarkStepPins.SetPressedNoSignal(!paintStep);
            if (_btnLandmarkStepPaint != null) _btnLandmarkStepPaint.SetPressedNoSignal(paintStep);
            if (_btnWeightPaintMode != null && _btnWeightPaintMode.ButtonPressed != paintStep)
            {
                _btnWeightPaintMode.ButtonPressed = paintStep;
            }
            else
            {
                OnWeightPaintModeToggled(paintStep);
            }
            UpdateRiggingMethodPanelVisibility();
        }

        private void UpdateRiggingMethodPanelVisibility()
        {
            var method = _optRiggingMethod != null && _optRiggingMethod.Selected >= 0
                ? (CustomModelAutoRigger.RiggingMethod)_optRiggingMethod.GetItemId(_optRiggingMethod.Selected)
                : CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig;

            bool isLandmark = method == CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig;
            bool isPreRigged = method == CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap;
            bool isKnn = method == CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection;
            bool isRigid = method == CustomModelAutoRigger.RiggingMethod.RigidSingleBone;
            bool paintStep = _btnWeightPaintMode?.ButtonPressed ?? false;

            if (_boxMarkersCard != null) _boxMarkersCard.Visible = isLandmark;
            if (_boxFingerCard != null) _boxFingerCard.Visible = isLandmark || isPreRigged;
            if (_optFingerRigMode != null && _optFingerRigMode.ItemCount > 2)
            {
                _optFingerRigMode.SetItemDisabled(2, isPreRigged);
                if (_fingerSegButtons.Length > 2 && _fingerSegButtons[2] != null)
                {
                    _fingerSegButtons[2].Disabled = isPreRigged;
                    _fingerSegButtons[2].Visible = !isPreRigged;
                }
                if (isPreRigged && _optFingerRigMode.Selected == 2)
                {
                    _optFingerRigMode.Select(1);
                    for (int i = 0; i < _fingerSegButtons.Length; i++)
                        _fingerSegButtons[i]?.SetPressedNoSignal(i == 1);
                    if (_boxFingerLandmarkTools != null) _boxFingerLandmarkTools.Visible = false;
                }
            }
            if (_boxLandmarkModeSwitchRow != null) _boxLandmarkModeSwitchRow.Visible = false;
            if (_boxLandmarkPins != null) _boxLandmarkPins.Visible = isLandmark;
            if (_boxWeightPaintSection != null)
            {
                _boxWeightPaintSection.Visible = isPreRigged || isLandmark || isKnn ||
                    IsTab3PhysicsPaintHostActive() || (_btnWeightPaintMode?.ButtonPressed ?? false);
            }
            if (_btnWeightPaintMode != null) _btnWeightPaintMode.Visible = true;
            if (_boxKnnParameters != null) _boxKnnParameters.Visible = isKnn;
            if (_boxPreRiggedMapping != null) _boxPreRiggedMapping.Visible = isPreRigged;
            if (_boxRigidBone != null) _boxRigidBone.Visible = isRigid;
            if (_boxPoseCalibration != null) _boxPoseCalibration.Visible = isKnn;
            if (_boxAutoSnapCard != null) _boxAutoSnapCard.Visible = isLandmark || isPreRigged;
            if (_boxAutoSnapOptions != null) _boxAutoSnapOptions.Visible = isLandmark || isPreRigged;
            if (_boxDeformGuardsCard != null) _boxDeformGuardsCard.Visible = !isRigid;

            if (_rigStepper != null)
            {
                if (!IsWizardStepApplicable(_rigWizardStep, method))
                    SetRigWizardStep(0);
                else
                    SyncRigWizardNavButtons(method);
            }
        }

        private void OnRiggingMethodChanged(long index)
        {
            var method = _optRiggingMethod.Selected >= 0
                ? (CustomModelAutoRigger.RiggingMethod)_optRiggingMethod.GetItemId(_optRiggingMethod.Selected)
                : CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig;

            bool isKnn = method == CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection;
            bool isPreRigged = method == CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap ||
                               method == CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig;

            if (!isPreRigged && _btnWeightPaintMode != null && _btnWeightPaintMode.ButtonPressed)
            {
                _btnWeightPaintMode.ButtonPressed = false;
            }
            if (_btnLandmarkStepPins != null) _btnLandmarkStepPins.SetPressedNoSignal(!(_btnWeightPaintMode?.ButtonPressed ?? false));
            if (_btnLandmarkStepPaint != null) _btnLandmarkStepPaint.SetPressedNoSignal(_btnWeightPaintMode?.ButtonPressed ?? false);

            UpdateRiggingMethodPanelVisibility();

            if (_optPosePreset != null) _optPosePreset.Disabled = !isKnn;
            if (_btnAutoCalibratePose != null) _btnAutoCalibratePose.Disabled = !isKnn;
            if (_btnResetPoseCal != null) _btnResetPoseCal.Disabled = !isKnn;
            if (_boxCustomArmAngle != null && !isKnn) _boxCustomArmAngle.Visible = false;
            if (_lblPoseCalHint != null)
            {
                _lblPoseCalHint.Text = isKnn
                    ? "Adjust these reference sliders so the cyan hero ghost matches your unrigged mesh's rest pose."
                    : "Adjusts reference hero pose for KNN transfer.";
            }

            var srcSkeleton = GetValidSourceSkeleton();
            if (srcSkeleton == null || srcSkeleton.GetBoneCount() == 0)
            {
                SetRigBadgeStatus(method switch
                {
                    CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig => "Static Mesh — Landmark Auto-Rig Active (16 Pins)",
                    CustomModelAutoRigger.RiggingMethod.RigidSingleBone => "Static Mesh — Rigid Bone Attachment",
                    _ => "Static Mesh — Legacy KNN Transfer Fallback"
                }, method == CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection ? StatusTone.Warning : StatusTone.Info);
            }

            if (_lblRiggingMethodDesc != null)
            {
                _lblRiggingMethodDesc.Text = method switch
                {
                    CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig =>
                        "Builds a real humanoid rig inside your mesh from 16 draggable pins, computes clean surface weights, and snaps limb proportions to Deadlock.",
                    CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap =>
                        "Maps your model's existing bones to Deadlock's skeleton while preserving authored vertex weights. Use Tab 1 Auto-Snap to match stance.",
                    CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection =>
                        "Fallback for non-humanoid meshes: projects vertex weights directly from nearby hero surface donors. Configure reference pose in Tab 1.",
                    CustomModelAutoRigger.RiggingMethod.RigidSingleBone =>
                        "Attaches the entire model rigidly to a single hero bone with zero skeletal deformation (ideal for hats, masks, or standalone props).",
                    _ => ""
                };
            }

            if (_lblAutoSnapModeHint != null)
            {
                _lblAutoSnapModeHint.Text = method switch
                {
                    CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig =>
                        "Rotates the pin-built skeleton's arms, legs, and head to match Deadlock's bind pose before animation playback.",
                    CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap =>
                        "Rotates your armature's rest limbs to match Deadlock's bind pose before transferring animation.",
                    _ => ""
                };
            }
            if (method == CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig)
            {
                EnsureLandmarkPins(false);
            }
            if (_previewWorldRoot != null && GodotObject.IsInstanceValid(_previewWorldRoot))
            {
                ApplyLandmarkPreviewState();
            }
        }

        #region Public Open & Data Binding

        public void Open(
            Node3D heroNode,
            Node3D importedScene,
            string filePath,
            CustomModelAutoRigger.RiggingOptions initialOptions = null,
            ImportWorkflowIntent workflowIntent = ImportWorkflowIntent.FullCharacter,
            string accessorySlotId = null)
        {
            _liveHeroNode = heroNode;
            _liveImportedScene = importedScene;
            _filePath = filePath;
            _workflowIntent = workflowIntent;
            _accessorySlotId = !string.IsNullOrWhiteSpace(accessorySlotId)
                ? accessorySlotId
                : (initialOptions?.SlotId ?? "");
            _accessoryIntent = initialOptions != null && initialOptions.Intent != CustomModelAutoRigger.AccessoryIntent.Auto
                ? initialOptions.Intent
                : (workflowIntent == ImportWorkflowIntent.AccessoryAddOrReplace
                    ? CustomModelAutoRigger.AccessoryIntent.AddAccessory
                    : CustomModelAutoRigger.AccessoryIntent.FullCharacter);
            _disabledSourceBones.Clear();
            _boneChains.Clear();
            _selectedChain = null;
            _bodyCollisionCapsules.Clear();
            _selectedCollider = null;
            _collidersDirty = true;
            _fingerLandmarks.Clear();
            _lastRigAnalysis = null;
            _lastWeightedSrcBones.Clear();
            if (_chkEnableCustomPhysics != null) _chkEnableCustomPhysics.SetPressedNoSignal(true);
            if (_chkPhysicsMasterTab3 != null) _chkPhysicsMasterTab3.SetPressedNoSignal(true);
            SetPhysicsSubView(0);
            SetBonesSubView(0);

            if (_optWorkflowMode != null)
            {
                _optWorkflowMode.Select(workflowIntent == ImportWorkflowIntent.AccessoryAddOrReplace ? 1 : 0);
            }
            if (_lblTitle != null)
            {
                _lblTitle.Text = _workflowIntent == ImportWorkflowIntent.AccessoryAddOrReplace
                    ? "ACCESSORY & OUTFIT IMPORTER"
                    : "CUSTOM 3D MODEL IMPORTER & AUTO-RIGGER";
            }
            if (_boxAccessorySetupCard != null)
            {
                _boxAccessorySetupCard.Visible = _workflowIntent == ImportWorkflowIntent.AccessoryAddOrReplace;
            }
            UpdateToolbarForWorkflowIntent();
            _heroCodename = heroNode?.Name.ToString().ToLowerInvariant().Replace("hero_", "").Trim() ?? "";

            string fileName = Path.GetFileName(filePath);
            _lblFileBadge.Text = $"File: {fileName}";
            _lblFileBadge.TooltipText = filePath ?? fileName;
            AppendConsoleMessage($"Opening importer for '{fileName}' (intent={_workflowIntent}, LightMode={ImporterMemoryGuard.LightMode})");

            if (_liveImportedScene == null || _liveHeroNode == null)
            {
                SetBottomStatus("Error: Invalid model or hero reference.", StatusTone.Danger);
                return;
            }

            // 1. Create a clean reference clone of the hero in the isolated SubViewport, sharing heavy Meshes/Skins/Materials
            ImporterMemoryGuard.Log("modal open: before hero clone");
            if (ImporterMemoryGuard.LightMode)
            {
                if (_subViewport != null) _subViewport.Msaa3D = Viewport.Msaa.Disabled;
                if (_subViewportContainer != null) _subViewportContainer.StretchShrink = 2;
                _showGhost = false;
                _btnToggleGhost?.SetPressedNoSignal(false);
                if (_btnManualRigUpdate != null) _btnManualRigUpdate.Visible = true;
                SetBottomStatus("Low memory: light mode (reduced preview quality, click Update Rig Preview).", StatusTone.Warning);
            }
            else
            {
                if (_subViewportContainer != null) _subViewportContainer.StretchShrink = 1;
                if (_btnManualRigUpdate != null) _btnManualRigUpdate.Visible = false;
            }
            _previewHeroClone = ImporterMemoryGuard.CloneHierarchySharingResources(_liveHeroNode);
            StripGizmosAndNonAuthenticNodes(_previewHeroClone);
            _previewWorldRoot.AddChild(_previewHeroClone);

            // CRITICAL REQUIREMENT 2: Force 100% symmetrical bind pose (ResetBonePoses) on primary hero skeleton clone!
            var heroSkelCandidates = _previewHeroClone.FindChildren("*", "Skeleton3D", true, false)
                .OfType<Skeleton3D>()
                .OrderByDescending(s => s.GetBoneCount())
                .ToList();
            _previewHeroSkeleton = heroSkelCandidates.FirstOrDefault() ?? CustomModelAutoRigger.FindSkeleton(_previewHeroClone);
            _selectedViewportBoneName = "";
            UpdateSelectedBoneBadge();
            if (_previewHeroSkeleton != null)
            {
                _previewHeroSkeleton.ResetBonePoses();
            }

            // Pick the primary hero AnimationPlayer (never overwrite with a sub-prop/card AnimationPlayer such as on Wraith)
            _previewAnimPlayer = null;
            var candidatePlayers = new List<AnimationPlayer>();
            foreach (var child in _previewHeroClone.FindChildren("*", "AnimationPlayer", true, false))
            {
                if (child is AnimationPlayer ap)
                {
                    ap.Stop();
                    candidatePlayers.Add(ap);
                }
            }
            if (candidatePlayers.Count > 0)
            {
                // Prefer the player sharing a parent with _previewHeroSkeleton or whose RootNode resolves to _previewHeroSkeleton
                Node skelParent = _previewHeroSkeleton?.GetParent();
                AnimationPlayer bestAp = candidatePlayers.FirstOrDefault(ap =>
                    _previewHeroSkeleton != null &&
                    (ap.GetParent() == skelParent ||
                     (ap.GetNodeOrNull(ap.RootNode) ?? ap.GetParent())?.GetPathTo(_previewHeroSkeleton).ToString() is string p &&
                     !string.IsNullOrEmpty(p) && !p.StartsWith("../")));

                if (bestAp == null && _previewHeroSkeleton != null)
                {
                    bestAp = candidatePlayers.FirstOrDefault(ap =>
                    {
                        var r = ap.GetNodeOrNull(ap.RootNode) ?? ap.GetParent();
                        return r != null && _previewHeroSkeleton.IsInsideTree() && r == _previewHeroClone;
                    });
                }
                _previewAnimPlayer = bestAp ?? candidatePlayers[0];
            }
            if (_previewAnimPlayer == null && _previewHeroSkeleton != null)
            {
                Node animParent = _previewHeroSkeleton.GetParent() ?? _previewHeroClone;
                _previewAnimPlayer = new AnimationPlayer { Name = "AnimationPlayer", RootNode = new NodePath("..") };
                animParent.AddChild(_previewAnimPlayer);
            }
            if (_previewAnimPlayer != null && _previewHeroSkeleton != null)
            {
                Node curRoot = _previewAnimPlayer.GetNodeOrNull(_previewAnimPlayer.RootNode) ?? _previewAnimPlayer.GetParent();
                Node skelParent = _previewHeroSkeleton.GetParent() ?? _previewHeroClone;
                if (curRoot == null || (_previewHeroSkeleton.IsInsideTree() && curRoot.IsInsideTree() && !curRoot.IsAncestorOf(_previewHeroSkeleton) && curRoot != _previewHeroSkeleton))
                {
                    _previewAnimPlayer.RootNode = _previewAnimPlayer.GetPathTo(skelParent);
                }
            }

            // Re-initialize or attach Source2BoneConstraintDriver on _previewHeroClone (CloneHierarchySharingResources skips C# scripts)
            _previewConstraintDriver = null;
            var clonedDrivers = _previewHeroClone.FindChildren("*", "Node", recursive: true, owned: false)
                .OfType<DeadlockPlayground.Animation.Source2BoneConstraintDriver>()
                .ToList();
            var vrfModel = GetVpkLoader()?.CurrentVrfModel;
            if (vrfModel != null && _previewHeroSkeleton != null)
            {
                _previewConstraintDriver = clonedDrivers.Count > 0
                    ? clonedDrivers[0]
                    : new DeadlockPlayground.Animation.Source2BoneConstraintDriver { Name = "Source2BoneConstraintDriver" };
                if (_previewConstraintDriver.GetParent() == null)
                {
                    _previewHeroClone.AddChild(_previewConstraintDriver);
                }
                _previewConstraintDriver.Initialize(vrfModel, _previewHeroSkeleton, _previewHeroClone);
                _previewConstraintDriver.EvaluateNow(0f);
                for (int d = 1; d < clonedDrivers.Count; d++)
                {
                    clonedDrivers[d].GetParent()?.RemoveChild(clonedDrivers[d]);
                    clonedDrivers[d].QueueFree();
                }
            }
            else
            {
                foreach (var d in clonedDrivers)
                {
                    d.GetParent()?.RemoveChild(d);
                    d.QueueFree();
                }
            }

            // Apply cyan ghost material to reference hero clone
            _ghostMaterial = new StandardMaterial3D
            {
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = new Color(0.18f, 0.75f, 0.95f, _ghostOpacity),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Back,
                DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.OpaqueOnly
            };

            CaptureOriginalHeroMaterials(_previewHeroClone);
            ApplyGhostMaterialToClone(_previewHeroClone);

            // 2. Setup imported preview model root and clone
            _previewModelRoot = new Node3D { Name = "PreviewModelRoot" };
            _previewWorldRoot.AddChild(_previewModelRoot);

            _previewModelInstance = (Node3D)_liveImportedScene.Duplicate();
            _previewModelRoot.AddChild(_previewModelInstance);
            ImporterMemoryGuard.Log("modal open: after model clone");
            SyncPreviewModelMeshVisibility();
            _cachedSourceSkeleton = CustomModelAutoRigger.FindSkeleton(_previewModelInstance);

            foreach (var mi in CustomModelAutoRigger.CollectMeshes(_previewModelInstance))
            {
                mi.Layers &= ~(uint)(1 << 20);
            }

            // 3. Populate animations dropdown
            PopulateTestAnimations();

            // 4. Populate hero bones for rigid attachment
            PopulateHeroBones();

            // 5. Populate submesh checklists
            PopulateSubmeshChecklists();
            PopulateAccQuickBonePicker();
            PopulateAccessoryReplaceList();
            PopulateAccDeformTargetList();
            ComputeAccLocalBoundsCenter();
            if (_txtAccName != null)
            {
                _txtAccName.Text = !string.IsNullOrEmpty(_filePath) ? Path.GetFileNameWithoutExtension(_filePath) : "";
            }

            // 6. Analyze rig status and bone mapper
            AnalyzeModelRigStatus();

            _userModifiedTransform = false;
            _userMovedLandmarkPins = false;
            _pinsDirtyNeedsRigRebuild = false;
            _lastPinBadgeStateKey = "";
            _previewFlipRestore = null;
            _btnFlipX?.SetPressedNoSignal(false);
            _btnFlipY?.SetPressedNoSignal(false);
            _btnFlipZ?.SetPressedNoSignal(false);
            _selectedPins.Clear();
            _keptPartTransforms.Clear();
            _isGizmoUpdatingFromUI = true;
            if (_chkAutoAlignHandOrientation != null) _chkAutoAlignHandOrientation.SetPressedNoSignal(true);
            if (_spinRightHandPitch != null) _spinRightHandPitch.SetValueNoSignal(0.0);
            if (_spinRightHandYaw != null) _spinRightHandYaw.SetValueNoSignal(0.0);
            if (_spinRightHandRoll != null) _spinRightHandRoll.SetValueNoSignal(0.0);
            if (_spinRightHandScale != null) _spinRightHandScale.SetValueNoSignal(1.0);
            if (_spinLeftHandPitch != null) _spinLeftHandPitch.SetValueNoSignal(0.0);
            if (_spinLeftHandYaw != null) _spinLeftHandYaw.SetValueNoSignal(0.0);
            if (_spinLeftHandRoll != null) _spinLeftHandRoll.SetValueNoSignal(0.0);
            if (_spinLeftHandScale != null) _spinLeftHandScale.SetValueNoSignal(1.0);
            _isGizmoUpdatingFromUI = false;
            SyncKeptPartSpinboxesFromSelected();
            if (initialOptions != null)
            {
                _userModifiedTransform = true;
                _userMovedLandmarkPins = initialOptions.LandmarkPins != null &&
                                         initialOptions.LandmarkPins.Count >= CustomLandmarkWeightSolver.PinNames.Length;
                RestoreRiggingOptions(initialOptions);
                SetHeroGhostEnabled(_workflowIntent == ImportWorkflowIntent.AccessoryAddOrReplace ? false : _showGhost);
                if (_workflowIntent == ImportWorkflowIntent.AccessoryAddOrReplace)
                {
                    SetAccessoryIntent(_accessoryIntent, triggerUpdate: false);
                    SyncAccBoneSelectionFromRigidBone();
                    if (_rightTabs != null) _rightTabs.CurrentTab = 0;
                    if (_optGizmoTarget != null) SelectGizmoTargetById(3);
                }
            }
            else
            {
                _customSkinOverrides.Clear();
                _baselineSkinOverrides.Clear();
                _weightUndoStack.Clear();
                _weightRedoStack.Clear();
                _pendingStrokeSnapshot = null;
                _strokeDirty = false;
                _paintSurfaces.Clear();
                _deformSculptDeltas.Clear();
                _deformUndoStack.Clear();
                _deformSurfaceCaches.Clear();
                _pendingDeformUndoSnapshot = null;
                _isDeformBrushing = false;
                if (_btnDeformBrushToggle != null) _btnDeformBrushToggle.SetPressedNoSignal(false);
                UpdateDeformBrushStatusLabel();
                _viewportShadingMode = CustomWeightPaintEditor.ViewportShadingMode.Textured;
                _optViewportShading?.Select(0);
                UpdatePaintOverrideLabel();
                UpdateWeightHistoryUI();

                if (_workflowIntent == ImportWorkflowIntent.AccessoryAddOrReplace)
                {
                    SetHeroGhostEnabled(false);
                    for (int i = 0; i < _optReplaceMode.ItemCount; i++)
                    {
                        if (_optReplaceMode.GetItemId(i) == (int)CustomModelAutoRigger.ReplaceMode.PartialAccessory)
                        {
                            _optReplaceMode.Select(i);
                            SyncReplaceModeUI(i);
                            break;
                        }
                    }
                    foreach (var chk in _heroKeepCheckboxes)
                    {
                        if (GodotObject.IsInstanceValid(chk)) chk.SetPressedNoSignal(true);
                    }
                    UpdatePreviewHeroKeepAndGhostMaterials();

                    // Default unrigged accessories to RigidSingleBone on "head" (user can switch to KNN skinning on Tab 2 for coats/capes)
                    var accSrcSkeleton = GetValidSourceSkeleton();
                    if (accSrcSkeleton == null || accSrcSkeleton.GetBoneCount() == 0)
                    {
                        for (int i = 0; i < _optRiggingMethod.ItemCount; i++)
                        {
                            if (_optRiggingMethod.GetItemId(i) == (int)CustomModelAutoRigger.RiggingMethod.RigidSingleBone)
                            {
                                _optRiggingMethod.Select(i);
                                OnRiggingMethodChanged(i);
                                break;
                            }
                        }
                    }
                    SelectRigidBoneByName("head");
                    SyncAccBoneSelectionFromRigidBone();
                    SetAccessoryIntent(_accessoryIntent, triggerUpdate: false);
                    if (_rightTabs != null) _rightTabs.CurrentTab = 0;
                    SnapAccessoryToSelectedBone();
                    SelectGizmoTargetById(3);
                    return;
                }

                var fullSrcSkeleton = GetValidSourceSkeleton();
                if (fullSrcSkeleton == null || fullSrcSkeleton.GetBoneCount() == 0)
                {
                    SetHeroGhostEnabled(false);
                    if (_rightTabs != null) _rightTabs.CurrentTab = 1;
                }
                // 7. CRITICAL REQUIREMENT 3: Automatic First-Frame World-Space Auto-Fit Height & Camera Frame!
                AutoFitToHero();
            }

            RefreshPresetsDropdown();
            UpdateFooterMemoryIndicator();
        }

        private void RestoreRiggingOptions(CustomModelAutoRigger.RiggingOptions options)
        {
            if (options == null) return;

            if (_txtAccName != null)
            {
                _txtAccName.Text = !string.IsNullOrWhiteSpace(options.AccessoryName)
                    ? options.AccessoryName
                    : (!string.IsNullOrEmpty(_filePath) ? Path.GetFileNameWithoutExtension(_filePath) : "");
            }

            // 1. Restore Transform (explicitly authored origin/scale; suppress scale pivot compensation)
            _suppressScalePivotCompensation = true;
            try
            {
                Vector3 scaleXyz = new Vector3(
                    options.RootScaleXYZ.X > 0.0001f ? options.RootScaleXYZ.X : 1f,
                    options.RootScaleXYZ.Y > 0.0001f ? options.RootScaleXYZ.Y : 1f,
                    options.RootScaleXYZ.Z > 0.0001f ? options.RootScaleXYZ.Z : 1f);
                if (_spinScaleX != null) _spinScaleX.SetValueNoSignal(scaleXyz.X);
                if (_spinScaleY != null) _spinScaleY.SetValueNoSignal(scaleXyz.Y);
                if (_spinScaleZ != null) _spinScaleZ.SetValueNoSignal(scaleXyz.Z);
                _btnFlipX?.SetPressedNoSignal(options.FlipX);
                _btnFlipY?.SetPressedNoSignal(options.FlipY);
                _btnFlipZ?.SetPressedNoSignal(options.FlipZ);
                ApplyPreviewGeometryFlip();

                if (options.RootScale > 0.0001f)
                {
                    _spinScale.SetValueNoSignal(options.RootScale);
                    _sliderScale.SetValueNoSignal(options.RootScale);
                    _spinPosX.SetValueNoSignal(options.RootPosition.X);
                    _spinPosY.SetValueNoSignal(options.RootPosition.Y);
                    _spinPosZ.SetValueNoSignal(options.RootPosition.Z);
                    _spinRotPitch.SetValueNoSignal(options.RootRotationDeg.X);
                    _spinRotYaw.SetValueNoSignal(options.RootRotationDeg.Y);
                    _spinRotRoll.SetValueNoSignal(options.RootRotationDeg.Z);
                    ApplyTransformToPreviewModel(immediate: false);
                }
                else if (options.AlignedTransform != Transform3D.Identity)
                {
                    _previewModelRoot.Transform = options.AlignedTransform;
                    Vector3 s = options.AlignedTransform.Basis.Scale;
                    _spinScale.SetValueNoSignal(s.X);
                    _sliderScale.SetValueNoSignal(s.X);
                    if (s.X > 0.0001f)
                    {
                        _spinScaleX?.SetValueNoSignal(1.0);
                        _spinScaleY?.SetValueNoSignal(Math.Round(s.Y / s.X, 3));
                        _spinScaleZ?.SetValueNoSignal(Math.Round(s.Z / s.X, 3));
                    }
                    _spinPosX.SetValueNoSignal(options.AlignedTransform.Origin.X);
                    _spinPosY.SetValueNoSignal(options.AlignedTransform.Origin.Y);
                    _spinPosZ.SetValueNoSignal(options.AlignedTransform.Origin.Z);
                    Vector3 rot = options.AlignedTransform.Basis.GetEuler();
                    _spinRotPitch.SetValueNoSignal(Mathf.RadToDeg(rot.X));
                    _spinRotYaw.SetValueNoSignal(Mathf.RadToDeg(rot.Y));
                    _spinRotRoll.SetValueNoSignal(Mathf.RadToDeg(rot.Z));
                    ApplyTransformToPreviewModel(immediate: false);
                }
            }
            finally
            {
                _suppressScalePivotCompensation = false;
            }

            // Restore Hero Mesh Fit-Deform settings
            _deformSculptDeltas.Clear();
            _deformUndoStack.Clear();
            _deformSurfaceCaches.Clear();
            _pendingDeformUndoSnapshot = null;
            _isDeformBrushing = false;
            if (_btnDeformBrushToggle != null) _btnDeformBrushToggle.SetPressedNoSignal(false);
            if (options.OriginalMeshDeformSculpts != null && options.OriginalMeshDeformSculpts.Count > 0)
            {
                foreach (var (sk, vMap) in options.OriginalMeshDeformSculpts)
                {
                    if (!string.IsNullOrEmpty(sk) && vMap != null && vMap.Count > 0)
                    {
                        _deformSculptDeltas[sk] = new Dictionary<int, Vector3>(vMap);
                    }
                }
            }
            bool anyHeroDeformActive = options.OriginalMeshDeformEnabled || _deformSculptDeltas.Count > 0;
            if (_chkDeformEnabled != null) _chkDeformEnabled.SetPressedNoSignal(anyHeroDeformActive);
            if (_boxDeformControls != null) _boxDeformControls.Visible = anyHeroDeformActive;
            UpdateDeformBrushStatusLabel();
            if (_optDeformTarget != null)
            {
                string tgtFilter = string.IsNullOrWhiteSpace(options.OriginalMeshDeformTargetFilter) ? "hair" : options.OriginalMeshDeformTargetFilter;
                for (int i = 0; i < _optDeformTarget.ItemCount; i++)
                {
                    if (string.Equals(_optDeformTarget.GetItemMetadata(i).AsString(), tgtFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        _optDeformTarget.Select(i);
                        break;
                    }
                }
            }
            if (_optDeformMode != null)
            {
                for (int i = 0; i < _optDeformMode.ItemCount; i++)
                {
                    if (_optDeformMode.GetItemId(i) == (int)options.OriginalMeshDeformMode)
                    {
                        _optDeformMode.Select(i);
                        break;
                    }
                }
            }
            if (_sliderDeformStrength != null)
            {
                _sliderDeformStrength.SetValueNoSignal(options.OriginalMeshDeformStrength);
                if (_lblDeformStrengthVal != null) _lblDeformStrengthVal.Text = $"{Mathf.RoundToInt(options.OriginalMeshDeformStrength * 100)}%";
            }
            if (_sliderDeformRadius != null)
            {
                _sliderDeformRadius.SetValueNoSignal(options.OriginalMeshDeformRadius);
                if (_lblDeformRadiusVal != null) _lblDeformRadiusVal.Text = $"{options.OriginalMeshDeformRadius:F2}m";
            }
            if (_sliderDeformHeight != null)
            {
                _sliderDeformHeight.SetValueNoSignal(options.OriginalMeshDeformHeightOffset);
                if (_lblDeformHeightVal != null) _lblDeformHeightVal.Text = $"{(options.OriginalMeshDeformHeightOffset >= 0 ? "+" : "")}{options.OriginalMeshDeformHeightOffset:F2}m";
            }
            ApplyPreviewHeroDeformation();

            // 2. Restore Pose Preset & limb snaps
            for (int i = 0; i < _optPosePreset.ItemCount; i++)
            {
                if (_optPosePreset.GetItemId(i) == (int)options.Preset)
                {
                    _optPosePreset.Select(i);
                    OnPosePresetChanged(i);
                    break;
                }
            }
            if (_sliderArmAngle != null)
            {
                _sliderArmAngle.Value = options.CustomArmAngleDeg;
                if (_lblArmAngleVal != null) _lblArmAngleVal.Text = $"{options.CustomArmAngleDeg:0}°";
            }
            if (_sliderArmForward != null)
            {
                _sliderArmForward.Value = options.CustomArmForwardDeg;
                if (_lblArmForwardVal != null) _lblArmForwardVal.Text = $"{options.CustomArmForwardDeg:0}°";
            }
            if (_sliderElbowStraight != null)
            {
                _sliderElbowStraight.Value = options.CustomElbowStraightenPct;
                if (_lblElbowStraightVal != null) _lblElbowStraightVal.Text = $"{options.CustomElbowStraightenPct:0}%";
            }
            if (_sliderLegSpread != null)
            {
                _sliderLegSpread.Value = options.CustomLegSpreadDeg;
                if (_lblLegSpreadVal != null) _lblLegSpreadVal.Text = $"{options.CustomLegSpreadDeg:0}°";
            }
            if (!string.IsNullOrEmpty(options.RigidBoneName) && _optRigidBone != null)
            {
                for (int i = 0; i < _optRigidBone.ItemCount; i++)
                {
                    if (_optRigidBone.GetItemText(i).Replace("★ ", "") == options.RigidBoneName)
                    {
                        _optRigidBone.Select(i);
                        break;
                    }
                }
            }
            if (_chkAutoAlignArms != null) _chkAutoAlignArms.ButtonPressed = options.AutoSnapArmsToHero;
            if (_chkAutoAlignLegs != null) _chkAutoAlignLegs.ButtonPressed = options.AutoSnapLegsToHero;
            if (_chkAutoAlignHead != null) _chkAutoAlignHead.ButtonPressed = options.AutoSnapHeadToHero;
            if (_chkAutoAlignHandOrientation != null)
            {
                _chkAutoAlignHandOrientation.SetPressedNoSignal(options.AutoAlignHandOrientation);
                _chkAutoAlignHandOrientation.Disabled = !options.AutoSnapArmsToHero;
            }
            if (_boxHandSnapTuning != null) _boxHandSnapTuning.Visible = options.AutoSnapArmsToHero;
            _isGizmoUpdatingFromUI = true;
            if (_spinRightHandPitch != null) _spinRightHandPitch.SetValueNoSignal(options.RightHandSnapOffsetDeg.X);
            if (_spinRightHandYaw != null) _spinRightHandYaw.SetValueNoSignal(options.RightHandSnapOffsetDeg.Y);
            if (_spinRightHandRoll != null) _spinRightHandRoll.SetValueNoSignal(options.RightHandSnapOffsetDeg.Z);
            if (_spinRightHandScale != null) _spinRightHandScale.SetValueNoSignal(options.RightHandSnapScale > 0.001f ? options.RightHandSnapScale : 1.0f);
            if (_spinLeftHandPitch != null) _spinLeftHandPitch.SetValueNoSignal(options.LeftHandSnapOffsetDeg.X);
            if (_spinLeftHandYaw != null) _spinLeftHandYaw.SetValueNoSignal(options.LeftHandSnapOffsetDeg.Y);
            if (_spinLeftHandRoll != null) _spinLeftHandRoll.SetValueNoSignal(options.LeftHandSnapOffsetDeg.Z);
            if (_spinLeftHandScale != null) _spinLeftHandScale.SetValueNoSignal(options.LeftHandSnapScale > 0.001f ? options.LeftHandSnapScale : 1.0f);
            _isGizmoUpdatingFromUI = false;

            _keptPartTransforms.Clear();
            if (options.KeptOriginalPartTransforms != null)
            {
                foreach (var (k, xf) in options.KeptOriginalPartTransforms)
                {
                    if (string.IsNullOrWhiteSpace(k) || xf == null || xf.IsIdentity) continue;
                    _keptPartTransforms[k] = new CustomModelAutoRigger.KeptHeroPartTransform
                    {
                        PositionOffset = xf.PositionOffset,
                        RotationDeg = xf.RotationDeg,
                        Scale = xf.Scale > 0.001f ? xf.Scale : 1.0f
                    };
                }
            }
            if (_chkClampLimbStretching != null) _chkClampLimbStretching.ButtonPressed = options.ClampLimbStretching;
            if (_chkStabilizeNeckFloatingHead != null) _chkStabilizeNeckFloatingHead.ButtonPressed = options.StabilizeNeckFloatingHead;
            if (_chkAutoCounterTwistWeights != null) _chkAutoCounterTwistWeights.ButtonPressed = options.AutoCounterTwistWeights;

            if (options.LandmarkPins != null && options.LandmarkPins.Count > 0)
            {
                _landmarkPins = new Dictionary<string, Vector3>(options.LandmarkPins);
            }
            _customSkinOverrides.Clear();
            _baselineSkinOverrides.Clear();
            _weightUndoStack.Clear();
            _weightRedoStack.Clear();
            _pendingStrokeSnapshot = null;
            _strokeDirty = false;
            if (options.CustomSkinWeightOverrides != null)
            {
                foreach (var (k, v) in options.CustomSkinWeightOverrides) _customSkinOverrides[k] = v;
            }
            UpdatePaintOverrideLabel();
            UpdateWeightHistoryUI();

            // 3. Restore Rigging Method
            for (int i = 0; i < _optRiggingMethod.ItemCount; i++)
            {
                if (_optRiggingMethod.GetItemId(i) == (int)options.Method)
                {
                    _optRiggingMethod.Select(i);
                    OnRiggingMethodChanged(i);
                    break;
                }
            }
            if (_spinLaplacian != null) _spinLaplacian.Value = options.SmoothingIterations;
            if (_spinKnnRadius != null) _spinKnnRadius.Value = options.SearchRadius;
            if (_sliderKnnRadius != null) _sliderKnnRadius.Value = options.SearchRadius;
            if (_chkExcludeClothHair != null) _chkExcludeClothHair.ButtonPressed = options.ExcludeClothHairBones;
            if (_chkKeepFingersRigid != null) _chkKeepFingersRigid.ButtonPressed = options.KeepFingersRigidOnHand;

            // Phase 2-4 Options
            if (_optRigFamilyOverride != null)
            {
                int targetFam = options.RigFamilyOverride.HasValue ? (int)options.RigFamilyOverride.Value : -1;
                for (int i = 0; i < _optRigFamilyOverride.ItemCount; i++)
                {
                    if (_optRigFamilyOverride.GetItemId(i) == targetFam) { _optRigFamilyOverride.Select(i); break; }
                }
            }
            if (_chkKeepUnweightedBones != null) _chkKeepUnweightedBones.SetPressedNoSignal(options.KeepUnweightedBones);
            if (_chkEnableCustomPhysics != null) _chkEnableCustomPhysics.SetPressedNoSignal(options.EnableCustomPhysics);
            if (_chkPhysicsMasterTab3 != null) _chkPhysicsMasterTab3.SetPressedNoSignal(options.EnableCustomPhysics);
            if (_chkPreserveClothingChains != null) _chkPreserveClothingChains.SetPressedNoSignal(options.PreserveSourceClothingChains);
            if (_chkBindSymmetry != null) _chkBindSymmetry.SetPressedNoSignal(options.BindSymmetry);
            if (_chkFitSpine != null) _chkFitSpine.SetPressedNoSignal(options.FitSpine);
            if (_chkConformToSkeleton != null) _chkConformToSkeleton.SetPressedNoSignal(options.ConformToSkeleton);
            if (_chkLooseMeshesRigid != null) _chkLooseMeshesRigid.SetPressedNoSignal(options.LooseMeshesRigid);
            if (_chkExtraTwistBones != null) _chkExtraTwistBones.SetPressedNoSignal(options.ExtraTwistBones);

            if (_optAccBoneMode != null)
            {
                for (int i = 0; i < _optAccBoneMode.ItemCount; i++)
                {
                    if (_optAccBoneMode.GetItemId(i) == (int)options.AccessoryBoneMode) { _optAccBoneMode.Select(i); break; }
                }
            }
            if (_optFingerRigMode != null)
            {
                for (int i = 0; i < _optFingerRigMode.ItemCount; i++)
                {
                    if (_optFingerRigMode.GetItemId(i) == (int)options.FingerMode) { _optFingerRigMode.Select(i); break; }
                }
                OnFingerRigModeChanged(_optFingerRigMode.Selected);
            }
            if (_chkGenMetacarpals != null) _chkGenMetacarpals.SetPressedNoSignal(options.GenerateFingerMetacarpals);
            if (_chkGenWristTwist != null) _chkGenWristTwist.SetPressedNoSignal(options.GenerateWristTwist);

            _fingerLandmarks.Clear();
            if (options.FingerLandmarks != null)
            {
                foreach (var (fk, fv) in options.FingerLandmarks) _fingerLandmarks[fk] = fv;
            }
            if (_lblFingerPinStatus != null)
            {
                _lblFingerPinStatus.Text = _fingerLandmarks.Count > 0
                    ? $"Finger pins restored: {_fingerLandmarks.Count}."
                    : "Finger pins not placed yet.";
            }

            _disabledSourceBones.Clear();
            if (options.DisabledSourceBones != null)
            {
                foreach (string db in options.DisabledSourceBones)
                {
                    if (!string.IsNullOrWhiteSpace(db)) _disabledSourceBones.Add(db);
                }
            }

            if (options.BoneChains != null && options.BoneChains.Count > 0)
            {
                _boneChains.Clear();
                foreach (var c in options.BoneChains)
                {
                    if (c != null) _boneChains.Add(c.Clone());
                }
            }

            if (options.BodyCollisionCapsules != null && options.BodyCollisionCapsules.Count > 0)
            {
                _bodyCollisionCapsules.Clear();
                foreach (var cap in options.BodyCollisionCapsules)
                {
                    if (cap != null) _bodyCollisionCapsules.Add(cap.Clone());
                }
                _collidersDirty = true;
            }

            if (options.CustomBoneRemap != null && options.CustomBoneRemap.Count > 0)
            {
                var srcSkel = GetValidSourceSkeleton();
                _isUpdatingBoneDropdowns = true;
                try
                {
                    _userBoneRemap.Clear();
                    foreach (var (canon, srcBone) in options.CustomBoneRemap)
                    {
                        if (string.IsNullOrWhiteSpace(canon) || string.IsNullOrWhiteSpace(srcBone)) continue;
                        _userBoneRemap[canon] = srcBone;
                        if (_boneRowsByCanonical.TryGetValue(canon, out var entry) && srcSkel != null)
                        {
                            SelectBoneDropdownBySourceName(entry.BoneDropdown, srcSkel, srcBone);
                        }
                    }
                }
                finally
                {
                    _isUpdatingBoneDropdowns = false;
                }
            }

            RefreshClothingChainsPanel();
            RefreshBodyCollidersPanel();
            RefreshDisabledBonesList();
            UpdateBoneMappingValidationBadges();
            ApplyBoneMappingFilter();
            RefreshBoneTabDiffSummary();

            // 4. Restore Replace Mode
            for (int i = 0; i < _optReplaceMode.ItemCount; i++)
            {
                if (_optReplaceMode.GetItemId(i) == (int)options.Mode)
                {
                    _optReplaceMode.Select(i);
                    SyncReplaceModeUI(i);
                    break;
                }
            }

            // 5. Restore Submesh Checklist & Outline Shell state
            if (_chkIncludeBakedOutlines != null)
            {
                _chkIncludeBakedOutlines.SetPressedNoSignal(options.IncludeBakedOutlineShells);
            }
            if (options.SubmeshIsOutlineShell != null)
            {
                foreach (var (k, v) in options.SubmeshIsOutlineShell)
                {
                    _submeshIsOutlineShell[k] = v;
                    if (_submeshOutlineCheckboxes.TryGetValue(k, out var outChk))
                    {
                        outChk.SetPressedNoSignal(v);
                    }
                    if (_submeshOutlineBadges.TryGetValue(k, out var outBadge))
                    {
                        outBadge.Visible = v;
                    }
                }
            }
            if (options.IncludedImportedSubmeshNames != null && options.IncludedImportedSubmeshNames.Count > 0)
            {
                foreach (var chk in _importedSubmeshCheckboxes)
                {
                    if (chk.HasMeta("SubmeshName"))
                    {
                        string sName = chk.GetMeta("SubmeshName").AsString();
                        chk.SetPressedNoSignal(options.IncludedImportedSubmeshNames.Contains(sName));
                    }
                }
                SyncPreviewModelMeshVisibility();
                ComputeAccLocalBoundsCenter();
            }
            if (options.KeptOriginalSubmeshNames != null && options.KeptOriginalSubmeshNames.Count > 0)
            {
                foreach (var chk in _heroKeepCheckboxes)
                {
                    if (chk.HasMeta("SubmeshName"))
                    {
                        string sName = chk.GetMeta("SubmeshName").AsString();
                        int surfIdxSep = sName.IndexOf(CustomModelAutoRigger.SurfaceKeepSeparator, StringComparison.Ordinal);
                        if (surfIdxSep >= 0 && int.TryParse(sName.Substring(surfIdxSep + CustomModelAutoRigger.SurfaceKeepSeparator.Length), out int sIdx))
                        {
                            string baseMeshName = sName.Substring(0, surfIdxSep);
                            chk.ButtonPressed = CustomModelAutoRigger.IsOriginalSurfaceKept(options.KeptOriginalSubmeshNames, baseMeshName, sIdx);
                        }
                        else
                        {
                            chk.ButtonPressed = options.KeptOriginalSubmeshNames.Contains(sName);
                        }
                    }
                }
            }
            if (options.ExplicitReplacedTargets != null && options.ExplicitReplacedTargets.Count > 0)
            {
                var expSet = new HashSet<string>(options.ExplicitReplacedTargets, StringComparer.OrdinalIgnoreCase);
                foreach (var repChk in _accReplaceCheckboxes)
                {
                    if (GodotObject.IsInstanceValid(repChk) && repChk.HasMeta("SubmeshName"))
                    {
                        repChk.SetPressedNoSignal(expSet.Contains(repChk.GetMeta("SubmeshName").AsString()));
                    }
                }
            }
            UpdatePreviewHeroKeepAndGhostMaterials();

            // 6. Restore Submesh weapon / socket configurations
            if (options.SubmeshAttachmentSockets != null)
            {
                foreach (var (k, v) in options.SubmeshAttachmentSockets) _submeshSocketAssignments[k] = v;
            }
            if (options.SubmeshReplacementMap != null)
            {
                foreach (var (k, v) in options.SubmeshReplacementMap) _submeshHeroReplacements[k] = v;
            }
            if (options.SubmeshSnapToSocket != null)
            {
                foreach (var (k, v) in options.SubmeshSnapToSocket) _submeshSnapToSocket[k] = v;
            }
            if (options.SubmeshSocketOffsetPos != null)
            {
                foreach (var (k, v) in options.SubmeshSocketOffsetPos) _submeshSocketOffsetPos[k] = v;
            }
            if (options.SubmeshSocketOffsetRot != null)
            {
                foreach (var (k, v) in options.SubmeshSocketOffsetRot) _submeshSocketOffsetRot[k] = v;
            }
            if (options.SubmeshSocketScale != null)
            {
                foreach (var (k, v) in options.SubmeshSocketScale) _submeshSocketScale[k] = v;
            }

            // Synchronize UI cards with restored dictionary settings
            _isGizmoUpdatingFromUI = true;
            foreach (var (mName, ctrl) in _submeshCardControls)
            {
                if (_submeshSocketAssignments.TryGetValue(mName, out string bName))
                {
                    ctrl.Role.Select(1);
                    var socketParent = ctrl.Socket.GetParent() as Control;
                    if (socketParent != null) socketParent.Visible = true;
                    var placementParent = ctrl.Placement.GetParent() as Control;
                    if (placementParent != null) placementParent.Visible = true;

                    for (int i = 0; i < ctrl.Socket.ItemCount; i++)
                    {
                        if (ctrl.Socket.GetItemText(i).Replace("★ ", "") == bName)
                        {
                            ctrl.Socket.Select(i);
                            break;
                        }
                    }
                    if (_submeshSnapToSocket.TryGetValue(mName, out bool snap))
                    {
                        ctrl.Placement.Select(snap ? 1 : 0);
                    }
                    if (_submeshHeroReplacements.TryGetValue(mName, out string rep))
                    {
                        for (int i = 0; i < ctrl.Replaces.ItemCount; i++)
                        {
                            if (ctrl.Replaces.GetItemText(i) == rep)
                            {
                                ctrl.Replaces.Select(i);
                                break;
                            }
                        }
                    }
                    if (_submeshSocketOffsetRot.TryGetValue(mName, out Vector3 rot))
                    {
                        ctrl.Pitch.SetValueNoSignal(rot.X);
                        ctrl.Yaw.SetValueNoSignal(rot.Y);
                        ctrl.Roll.SetValueNoSignal(rot.Z);
                    }
                    if (_submeshSocketOffsetPos.TryGetValue(mName, out Vector3 pos))
                    {
                        ctrl.PosX.SetValueNoSignal(pos.X);
                        ctrl.PosY.SetValueNoSignal(pos.Y);
                        ctrl.PosZ.SetValueNoSignal(pos.Z);
                    }
                    if (_submeshSocketScale.TryGetValue(mName, out float s))
                    {
                        ctrl.Scale.SetValueNoSignal(s);
                    }
                }
            }
            _isGizmoUpdatingFromUI = false;

            RefreshKeptPartTargetList();
            SyncKeptPartSpinboxesFromSelected();
            ApplyTransformToPreviewModel(immediate: true);
        }

        private void CaptureOriginalHeroMaterials(Node3D clone)
        {
            _previewHeroOriginalMaterials.Clear();
            if (clone == null || !GodotObject.IsInstanceValid(clone)) return;

            foreach (var mi in CustomModelAutoRigger.CollectMeshes(clone))
            {
                if (mi.HasMeta("IsCustomImportedMesh") && mi.GetMeta("IsCustomImportedMesh").AsBool()) continue;
                string miName = mi.Name.ToString().ToLowerInvariant();
                if (miName.Contains("custom_rigged") || miName.Contains("previewmodel")) continue;

                int surfCount = mi.Mesh?.GetSurfaceCount() ?? 0;
                var surfOverrides = new Material[surfCount];
                for (int s = 0; s < surfCount; s++)
                {
                    surfOverrides[s] = mi.GetSurfaceOverrideMaterial(s);
                }
                _previewHeroOriginalMaterials[mi] = (mi.MaterialOverride, surfOverrides);
            }
        }

        private void ApplyGhostMaterialToClone(Node3D clone)
        {
            if (clone == null || !GodotObject.IsInstanceValid(clone)) return;
            _previewGhostMeshes.Clear();
            foreach (var mi in CustomModelAutoRigger.CollectMeshes(clone))
            {
                // Never apply ghost material to custom imported meshes!
                if (mi.HasMeta("IsCustomImportedMesh") && mi.GetMeta("IsCustomImportedMesh").AsBool())
                {
                    continue;
                }
                string miName = mi.Name.ToString().ToLowerInvariant();
                if (miName.Contains("custom_rigged") || miName.Contains("previewmodel"))
                {
                    continue;
                }

                _previewGhostMeshes.Add(mi);
                mi.Layers &= ~(uint)(1 << 20); // Clear Layer 21
            }

            UpdatePreviewHeroKeepAndGhostMaterials();
        }

        private StandardMaterial3D _hiddenSurfaceMaterial;

        private void UpdatePreviewHeroKeepAndGhostMaterials()
        {
            var keptNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var chk in _heroKeepCheckboxes)
            {
                if (GodotObject.IsInstanceValid(chk) && chk.ButtonPressed && chk.HasMeta("SubmeshName"))
                {
                    keptNames.Add(chk.GetMeta("SubmeshName").AsString());
                }
            }

            foreach (var mi in _previewGhostMeshes)
            {
                if (!GodotObject.IsInstanceValid(mi)) continue;

                string miName = mi.Name.ToString();
                int surfCount = mi.Mesh?.GetSurfaceCount() ?? 0;
                int checkCount = Math.Max(1, surfCount);
                bool anyKept = false;
                bool allKept = true;
                for (int s = 0; s < checkCount; s++)
                {
                    if (CustomModelAutoRigger.IsOriginalSurfaceKept(keptNames, miName, s)) anyKept = true;
                    else allKept = false;
                }

                if (allKept)
                {
                    if (_previewHeroOriginalMaterials.TryGetValue(mi, out var orig))
                    {
                        mi.MaterialOverride = orig.Override;
                        for (int s = 0; s < surfCount; s++)
                        {
                            Material mat = (orig.SurfaceOverrides != null && s < orig.SurfaceOverrides.Length)
                                ? orig.SurfaceOverrides[s]
                                : null;
                            mi.SetSurfaceOverrideMaterial(s, mat);
                        }
                    }
                    else
                    {
                        mi.MaterialOverride = null;
                        for (int s = 0; s < surfCount; s++) mi.SetSurfaceOverrideMaterial(s, null);
                    }
                    mi.Visible = true;
                }
                else if (anyKept && surfCount > 1)
                {
                    mi.MaterialOverride = null;
                    _previewHeroOriginalMaterials.TryGetValue(mi, out var orig);
                    if (!_showGhost) _hiddenSurfaceMaterial ??= CustomModelAutoRigger.CreateHiddenSurfaceMaterial();
                    for (int s = 0; s < surfCount; s++)
                    {
                        if (CustomModelAutoRigger.IsOriginalSurfaceKept(keptNames, miName, s))
                        {
                            Material mat = (orig.SurfaceOverrides != null && s < orig.SurfaceOverrides.Length)
                                ? orig.SurfaceOverrides[s]
                                : null;
                            mi.SetSurfaceOverrideMaterial(s, mat);
                        }
                        else
                        {
                            mi.SetSurfaceOverrideMaterial(s, _showGhost ? _ghostMaterial : _hiddenSurfaceMaterial);
                        }
                    }
                    mi.Visible = true;
                }
                else
                {
                    mi.MaterialOverride = _ghostMaterial;
                    for (int s = 0; s < surfCount; s++)
                    {
                        mi.SetSurfaceOverrideMaterial(s, _ghostMaterial);
                    }
                    mi.Visible = _showGhost;
                }
            }
            RefreshKeptPartTargetList();
        }

        private static Dictionary<string, CustomModelAutoRigger.KeptHeroPartTransform> CloneKeptPartTransforms(
            Dictionary<string, CustomModelAutoRigger.KeptHeroPartTransform> src)
        {
            if (src == null || src.Count == 0) return null;
            var copy = new Dictionary<string, CustomModelAutoRigger.KeptHeroPartTransform>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, xf) in src)
            {
                if (string.IsNullOrWhiteSpace(k) || xf == null || xf.IsIdentity) continue;
                copy[k] = new CustomModelAutoRigger.KeptHeroPartTransform
                {
                    PositionOffset = xf.PositionOffset,
                    RotationDeg = xf.RotationDeg,
                    Scale = xf.Scale > 0.001f ? xf.Scale : 1.0f
                };
            }
            return copy.Count > 0 ? copy : null;
        }

        private static bool IsWeaponLikeKeepLabel(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string l = text.ToLowerInvariant();
            return l.Contains("sword") || l.Contains("gun") || l.Contains("weapon") ||
                   l.Contains("rapier") || l.Contains("blade") || l.Contains("staff") ||
                   l.Contains("bow") || l.Contains("rifle") || l.Contains("pistol") ||
                   l.Contains("dagger") || l.Contains("shield") || l.Contains("hammer") ||
                   l.Contains("spear") || l.Contains("scythe") || l.Contains("wand");
        }

        private void RefreshKeptPartTargetList()
        {
            if (_optKeptPartTarget == null || _boxKeptPartCard == null) return;

            var checkedItems = new List<(string Key, string Label)>();
            foreach (var chk in _heroKeepCheckboxes)
            {
                if (!GodotObject.IsInstanceValid(chk) || !chk.ButtonPressed || !chk.HasMeta("SubmeshName")) continue;
                string key = chk.GetMeta("SubmeshName").AsString();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    checkedItems.Add((key, chk.Text));
                }
            }

            _boxKeptPartCard.Visible = checkedItems.Count > 0;
            string prevKey = GetSelectedKeptPartKey();

            // Skip rebuilding dropdown if exact same key list is already loaded
            if (_optKeptPartTarget.ItemCount == checkedItems.Count)
            {
                bool same = true;
                for (int i = 0; i < checkedItems.Count; i++)
                {
                    if (!string.Equals(_optKeptPartTarget.GetItemMetadata(i).AsString(), checkedItems[i].Key, StringComparison.OrdinalIgnoreCase))
                    {
                        same = false;
                        break;
                    }
                }
                if (same)
                {
                    SyncKeptPartSpinboxesFromSelected();
                    return;
                }
            }

            _isSyncingKeptPartUi = true;
            try
            {
                _optKeptPartTarget.Clear();
                int selectedIdx = -1;
                int firstWeaponIdx = -1;
                for (int i = 0; i < checkedItems.Count; i++)
                {
                    var (key, label) = checkedItems[i];
                    _optKeptPartTarget.AddItem(label, i);
                    _optKeptPartTarget.SetItemMetadata(i, key);
                    if (!string.IsNullOrEmpty(prevKey) && string.Equals(key, prevKey, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedIdx = i;
                    }
                    if (firstWeaponIdx < 0 && (IsWeaponLikeKeepLabel(label) || IsWeaponLikeKeepLabel(key)))
                    {
                        firstWeaponIdx = i;
                    }
                }
                if (_optKeptPartTarget.ItemCount > 0)
                {
                    _optKeptPartTarget.Select(selectedIdx >= 0 ? selectedIdx : (firstWeaponIdx >= 0 ? firstWeaponIdx : 0));
                }
            }
            finally
            {
                _isSyncingKeptPartUi = false;
            }
            SyncKeptPartSpinboxesFromSelected();
        }

        private string GetSelectedKeptPartKey()
        {
            if (_optKeptPartTarget == null || _optKeptPartTarget.Selected < 0) return "";
            return _optKeptPartTarget.GetItemMetadata(_optKeptPartTarget.Selected).AsString() ?? "";
        }

        private void SyncKeptPartSpinboxesFromSelected()
        {
            if (_spinKeptPosX == null) return;
            string key = GetSelectedKeptPartKey();
            var xf = (!string.IsNullOrEmpty(key) && _keptPartTransforms.TryGetValue(key, out var found) && found != null)
                ? found
                : new CustomModelAutoRigger.KeptHeroPartTransform();

            _isSyncingKeptPartUi = true;
            try
            {
                _spinKeptPosX.SetValueNoSignal(Math.Round(xf.PositionOffset.X, 4));
                _spinKeptPosY.SetValueNoSignal(Math.Round(xf.PositionOffset.Y, 4));
                _spinKeptPosZ.SetValueNoSignal(Math.Round(xf.PositionOffset.Z, 4));
                _spinKeptRotPitch.SetValueNoSignal(Math.Round(xf.RotationDeg.X, 2));
                _spinKeptRotYaw.SetValueNoSignal(Math.Round(xf.RotationDeg.Y, 2));
                _spinKeptRotRoll.SetValueNoSignal(Math.Round(xf.RotationDeg.Z, 2));
                _spinKeptScale.SetValueNoSignal(Math.Round(xf.Scale > 0.001f ? xf.Scale : 1.0f, 3));
            }
            finally
            {
                _isSyncingKeptPartUi = false;
            }
        }

        private void OnKeptPartSpinboxChanged()
        {
            if (_isSyncingKeptPartUi) return;
            string key = GetSelectedKeptPartKey();
            if (string.IsNullOrEmpty(key)) return;

            var xf = new CustomModelAutoRigger.KeptHeroPartTransform
            {
                PositionOffset = new Vector3((float)_spinKeptPosX.Value, (float)_spinKeptPosY.Value, (float)_spinKeptPosZ.Value),
                RotationDeg = new Vector3((float)_spinKeptRotPitch.Value, (float)_spinKeptRotYaw.Value, (float)_spinKeptRotRoll.Value),
                Scale = (float)_spinKeptScale.Value
            };
            if (xf.IsIdentity) _keptPartTransforms.Remove(key);
            else _keptPartTransforms[key] = xf;

            ApplyPreviewHeroDeformation();
            int gTarget = _optGizmoTarget?.Selected >= 0 ? _optGizmoTarget.GetItemId(_optGizmoTarget.Selected) : 0;
            if (gTarget == 4 && !_isGizmoDragging && !(_previewGizmo?.Editing ?? false))
            {
                UpdateGizmoTargetTransform();
            }
        }

        private void OnResetSelectedKeptPartPressed()
        {
            string key = GetSelectedKeptPartKey();
            if (string.IsNullOrEmpty(key)) return;
            _keptPartTransforms.Remove(key);
            SyncKeptPartSpinboxesFromSelected();
            ApplyPreviewHeroDeformation();
            int gTarget = _optGizmoTarget?.Selected >= 0 ? _optGizmoTarget.GetItemId(_optGizmoTarget.Selected) : 0;
            if (gTarget == 4) UpdateGizmoTargetTransform();
        }

        private List<string> GetHeroSocketBoneNames()
        {
            var list = new List<string>();
            string[] priority = { "weapon_hand_R", "hand_R", "weapon_hand_L", "hand_L", "head", "spine_3", "pelvis" };
            if (_previewHeroSkeleton != null)
            {
                foreach (var p in priority)
                {
                    if (_previewHeroSkeleton.FindBone(p) >= 0)
                    {
                        list.Add(p);
                    }
                }
                for (int b = 0; b < _previewHeroSkeleton.GetBoneCount(); b++)
                {
                    string bName = _previewHeroSkeleton.GetBoneName(b);
                    if (!list.Contains(bName))
                    {
                        list.Add(bName);
                    }
                }
            }
            if (list.Count == 0)
            {
                list.AddRange(priority);
            }
            return list;
        }

        private static void StripGizmosAndNonAuthenticNodes(Node root)
        {
            if (root == null) return;
            var toRemove = new List<Node>();
            FindNodesToStrip(root, toRemove);
            foreach (var node in toRemove)
            {
                if (GodotObject.IsInstanceValid(node))
                {
                    node.GetParent()?.RemoveChild(node);
                    node.Free();
                }
            }
        }

        private static void FindNodesToStrip(Node node, List<Node> toRemove)
        {
            foreach (var child in node.GetChildren())
            {
                string cName = child.Name.ToString();
                string cType = child.GetType().Name;
                string lower = cName.ToLowerInvariant();

                if (cType.Contains("SkeletonGizmo") || cType.Contains("BoneLayer") || cType.Contains("CharacterIK") ||
                    cName.StartsWith("@") || lower.Contains("gizmo") || lower.Contains("picker") ||
                    lower.Contains("handle") || lower.Contains("tipvisual") || lower.Contains("polevisual"))
                {
                    toRemove.Add(child);
                    continue;
                }

                if (child is MeshInstance3D mi)
                {
                    // Remove duplicate generated split submeshes on preview clone
                    if (mi.HasMeta("ParentCompositeMesh") || mi.Name.ToString().Contains("_surf_"))
                    {
                        toRemove.Add(child);
                        continue;
                    }

                    // Restore original composite meshes
                    if (mi.HasMeta("IsHiddenComposite"))
                    {
                        mi.Visible = true;
                        mi.RemoveMeta("IsHiddenComposite");
                        if (mi.HasMeta("GeneratedSubmeshes")) mi.RemoveMeta("GeneratedSubmeshes");
                    }

                    if (!DeadlockPlayground.Painter.HeroMeshHierarchy.IsOriginalHeroMesh(mi) || mi.HasMeta("IsCustomImportedMesh"))
                    {
                        toRemove.Add(child);
                        continue;
                    }

                    // Restore authentic hero submesh to full visibility on reference ghost clone
                    mi.Visible = true;
                    if (mi.HasMeta("UserHiddenByCustomModel")) mi.RemoveMeta("UserHiddenByCustomModel");
                    if (mi.HasMeta("UserVisibility")) mi.SetMeta("UserVisibility", true);
                }

                FindNodesToStrip(child, toRemove);
            }
        }

        private VpkLoaderTest GetVpkLoader()
        {
            return _liveHeroNode?.GetTree()?.Root?.FindChild("VpkLoaderTest", true, false) as VpkLoaderTest;
        }

        private DeadlockAnimLoader.AnimSequenceInfo FindDefaultIdleAnim()
        {
            var vpkLoader = GetVpkLoader();
            var anims = vpkLoader?.CurrentHeroAnimations;
            if (anims == null || anims.Count == 0) return null;

            static bool IsExcluded(string name)
            {
                string lower = name.ToLowerInvariant();
                return lower.Contains("menu") || lower.Contains("intro") || lower.Contains("select") ||
                       lower.Contains("shop") || lower.Contains("teleport") || lower.Contains("spawn") ||
                       lower.Contains("hero_test") || lower.Contains("death");
            }

            string[] primaryCandidates = { "primary_stand_idle", "stand_idle", "idle", "idle_stand" };
            foreach (var candidate in primaryCandidates)
            {
                var match = anims.FirstOrDefault(a =>
                    a.IsCompatible &&
                    !IsExcluded(a.CleanName) &&
                    a.CleanName.Equals(candidate, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }

            var idleMatch = anims.FirstOrDefault(a =>
                a.IsCompatible &&
                (a.Category == "Idles & Stances" || a.Category == "Idle") &&
                !IsExcluded(a.CleanName) &&
                (a.CleanName.Contains("stand_idle", StringComparison.OrdinalIgnoreCase) ||
                 a.CleanName.Contains("idle", StringComparison.OrdinalIgnoreCase)));
            if (idleMatch != null) return idleMatch;

            return anims.FirstOrDefault(a => a.IsCompatible && !a.IsAdditive && !IsExcluded(a.CleanName));
        }

        private Godot.Animation GetOrDecodePreviewAnimation(DeadlockAnimLoader.AnimSequenceInfo info)
        {
            if (info == null || _previewAnimPlayer == null) return null;

            string targetName = _previewAnimPlayer.HasAnimation(info.CleanName) ? info.CleanName :
                               (_previewAnimPlayer.HasAnimation(info.RawName) ? info.RawName : null);

            if (targetName != null)
            {
                return _previewAnimPlayer.GetAnimation(targetName);
            }

            var vpkLoader = GetVpkLoader();
            if (vpkLoader != null)
            {
                return vpkLoader.DecodeAnimation(info, _previewHeroSkeleton, _previewAnimPlayer, inPlace: true);
            }

            return null;
        }

        private void OnPreviewAnimationFinished(StringName animName)
        {
            if (_btnAnimLoop != null && !_btnAnimLoop.ButtonPressed)
            {
                if (_btnAnimPlayPause != null)
                {
                    _btnAnimPlayPause.ButtonPressed = false;
                    SetButtonIcon(_btnAnimPlayPause, "res://assets/at-icons/play.svg", 22);
                }
            }
        }

        private void PopulateTestAnimations()
        {
            _optTestAnim.Clear();
            _optTestAnim.AddItem("Bind Pose (Rest)", 0);
            _optTestAnim.SetItemMetadata(0, "");

            var vpkLoader = GetVpkLoader();
            var allHeroAnims = vpkLoader?.CurrentHeroAnimations;

            var candidates = new List<DeadlockAnimLoader.AnimSequenceInfo>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (allHeroAnims != null && allHeroAnims.Count > 0)
            {
                foreach (var anim in allHeroAnims)
                {
                    if (!anim.IsCompatible) continue;
                    if (seenNames.Add(anim.CleanName))
                    {
                        candidates.Add(anim);
                    }
                }
            }

            // Also include any animations already loaded on _previewAnimPlayer that might not be in vpkLoader
            if (_previewAnimPlayer != null)
            {
                foreach (var animName in _previewAnimPlayer.GetAnimationList())
                {
                    string clean = DeadlockAnimLoader.SanitizePoseName(animName);
                    if (seenNames.Add(clean))
                    {
                        candidates.Add(new DeadlockAnimLoader.AnimSequenceInfo
                        {
                            RawName = animName,
                            CleanName = clean,
                            Category = DeadlockAnimLoader.ClassifyCategory(clean),
                            IsCompatible = true
                        });
                    }
                }
            }

            if (candidates.Count == 0) return;

            // 1. Identify Stress Test animations (worst-case deformations: melee, heavy melee, jump, slide, dash, crouch, run)
            var stressTests = new List<DeadlockAnimLoader.AnimSequenceInfo>();
            foreach (var anim in candidates)
            {
                string cLow = anim.CleanName.ToLowerInvariant();
                if (cLow.Contains("melee") || cLow.Contains("jump_in_air") || cLow.Contains("dash") ||
                    cLow.Contains("slide") || cLow.Contains("crouch") || cLow.Contains("run") ||
                    cLow == "primary_stand_idle")
                {
                    stressTests.Add(anim);
                }
            }

            int nextId = 1;

            void AddCategoryGroup(string title, IEnumerable<DeadlockAnimLoader.AnimSequenceInfo> items)
            {
                var list = items.OrderBy(a => a.CleanName).ToList();
                if (list.Count == 0) return;

                _optTestAnim.AddSeparator($"── {title} ──");
                foreach (var item in list)
                {
                    int idx = _optTestAnim.ItemCount;
                    _optTestAnim.AddItem(item.CleanName, nextId++);
                    _optTestAnim.SetItemMetadata(idx, item.CleanName);
                }
            }

            // Add Stress Test first!
            if (stressTests.Count > 0)
            {
                AddCategoryGroup("★ Stress Test (Deformation)", stressTests);
            }

            // Standard categories
            string[] categories = {
                "Idles & Stances",
                "Locomotion",
                "Combat & Attacks",
                "Abilities & Magic",
                "Traversal & Zipline",
                "Reactions & Status",
                "Emotes & Taunts"
            };

            foreach (var cat in categories)
            {
                var inCat = candidates.Where(a =>
                    a.Category.Equals(cat, StringComparison.OrdinalIgnoreCase) ||
                    (cat == "Idles & Stances" && a.Category.Equals("Idle", StringComparison.OrdinalIgnoreCase))
                );
                AddCategoryGroup(cat, inCat);
            }

            // Other / remaining sequences
            var others = candidates.Where(a =>
                !categories.Any(c => c.Equals(a.Category, StringComparison.OrdinalIgnoreCase)) &&
                !a.Category.Equals("Idle", StringComparison.OrdinalIgnoreCase) &&
                !stressTests.Contains(a)
            );
            AddCategoryGroup("Other Sequences", others);
        }

        private void PopulateHeroBones()
        {
            _optRigidBone.Clear();
            if (_previewHeroSkeleton == null) return;

            string[] priorityBones = { "weapon_hand_R", "hand_R", "weapon_hand_L", "hand_L", "head", "arm_lower_R", "arm_lower_L", "spine_2", "spine_3", "pelvis" };
            foreach (var p in priorityBones)
            {
                int bIdx = _previewHeroSkeleton.FindBone(p);
                if (bIdx >= 0)
                {
                    _optRigidBone.AddItem($"★ {p}", bIdx);
                }
            }

            for (int b = 0; b < _previewHeroSkeleton.GetBoneCount(); b++)
            {
                string name = _previewHeroSkeleton.GetBoneName(b);
                if (!priorityBones.Contains(name))
                {
                    _optRigidBone.AddItem(name, b);
                }
            }
        }

        private void PopulateSubmeshChecklists()
        {
            // Clear existing
            foreach (var c in _heroKeepCheckboxes) c.QueueFree();
            _heroKeepCheckboxes.Clear();
            foreach (var c in _importedSubmeshCheckboxes) c.QueueFree();
            _importedSubmeshCheckboxes.Clear();
            foreach (Node child in _boxImportedSubmeshes.GetChildren()) child.QueueFree();
            foreach (Node child in _boxHeroSubmeshes.GetChildren()) child.QueueFree();

            _submeshSocketAssignments.Clear();
            _submeshHeroReplacements.Clear();
            _submeshCardControls.Clear();
            _submeshIsOutlineShell.Clear();
            _submeshOutlineCheckboxes.Clear();
            _submeshOutlineBadges.Clear();

            _meshThumbService?.ClearCache();
            _isolatedSubmeshName = "";

            var heroGrid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            heroGrid.AddThemeConstantOverride("h_separation", 8);
            heroGrid.AddThemeConstantOverride("v_separation", 8);
            _boxHeroSubmeshes.AddChild(heroGrid);

            // Original Deadlock Hero submeshes (strictly authentic, zero gizmo meshes!)
            // Expand composite multi-surface meshes (e.g. _priest -> priest_lower, priest_upper, priest_shotgun, ...)
            // so users can keep individual weapons/parts active without restoring the entire hero body.
            var heroMeshes = CustomModelAutoRigger.CollectOriginalHeroMeshes(_liveHeroNode);
            foreach (var mi in heroMeshes)
            {
                int surfCount = mi.Mesh?.GetSurfaceCount() ?? 0;
                if (surfCount > 1)
                {
                    var genArr = mi.HasMeta("GeneratedSubmeshes") ? mi.GetMeta("GeneratedSubmeshes").AsGodotArray<MeshInstance3D>() : null;
                    for (int s = 0; s < surfCount; s++)
                    {
                        string surfMatName = null;
                        if (genArr != null)
                        {
                            foreach (var subMi in genArr)
                            {
                                if (subMi != null && GodotObject.IsInstanceValid(subMi) &&
                                    subMi.HasMeta("SurfaceIndex") && subMi.GetMeta("SurfaceIndex").AsInt32() == s)
                                {
                                    surfMatName = DeadlockPlayground.Painter.HeroMeshHierarchy.ResolveSurfaceMaterialName(subMi, 0);
                                    break;
                                }
                            }
                        }
                        if (string.IsNullOrEmpty(surfMatName))
                        {
                            surfMatName = DeadlockPlayground.Painter.HeroMeshHierarchy.ResolveSurfaceMaterialName(mi, s);
                        }

                        string cleanLabel = !string.IsNullOrEmpty(surfMatName)
                            ? DeadlockPlayground.Painter.HeroMeshHierarchy.FormatCleanMaterialDisplayName(surfMatName, _heroCodename)
                            : $"Surface {s}";
                        string keepKey = CustomModelAutoRigger.MakeSurfaceKeepKey(mi.Name.ToString(), s);

                        var chk = new CheckBox
                        {
                            Text = cleanLabel,
                            ButtonPressed = false,
                            Visible = false
                        };
                        chk.SetMeta("SubmeshName", keepKey);
                        chk.Toggled += (_) => UpdatePreviewHeroKeepAndGhostMaterials();
                        _heroKeepCheckboxes.Add(chk);
                        _boxHeroSubmeshes.AddChild(chk);

                        var hCard = new MeshThumbCard();
                        hCard.Build(cleanLabel, mi.Name.ToString(), "Hero Part", new Color(0.32f, 0.82f, 0.52f), false, isHeroKeepMode: true);
                        hCard.Toggled += (on) =>
                        {
                            if (chk.ButtonPressed != on) chk.ButtonPressed = on;
                        };
                        hCard.IsolatePressed += () =>
                        {
                            if (_optReplaceMode != null && _optReplaceMode.Selected != 1)
                            {
                                _optReplaceMode.Select(1);
                                SyncReplaceModeUI(1);
                            }
                            if (!chk.ButtonPressed)
                            {
                                chk.ButtonPressed = true;
                                hCard.SetActive(true);
                            }
                        };
                        heroGrid.AddChild(hCard);
                        _meshThumbService?.RequestThumbnail($"hero_{keepKey}", mi.Mesh, s, new Color(0.68f, 0.85f, 0.78f), tex => hCard.SetThumbnail(tex));
                    }
                }
                else
                {
                    string hName = mi.Name.ToString();
                    var chk = new CheckBox
                    {
                        Text = hName,
                        ButtonPressed = false,
                        Visible = false
                    };
                    chk.SetMeta("SubmeshName", hName);
                    chk.Toggled += (_) => UpdatePreviewHeroKeepAndGhostMaterials();
                    _heroKeepCheckboxes.Add(chk);
                    _boxHeroSubmeshes.AddChild(chk);

                    var hCard = new MeshThumbCard();
                    hCard.Build(hName, "Original mesh", "Hero Part", new Color(0.32f, 0.82f, 0.52f), false, isHeroKeepMode: true);
                    hCard.Toggled += (on) =>
                    {
                        if (chk.ButtonPressed != on) chk.ButtonPressed = on;
                    };
                    hCard.IsolatePressed += () =>
                    {
                        if (!chk.ButtonPressed)
                        {
                            chk.ButtonPressed = true;
                            hCard.SetActive(true);
                        }
                    };
                    heroGrid.AddChild(hCard);
                    _meshThumbService?.RequestThumbnail($"hero_{hName}", mi.Mesh, -1, new Color(0.68f, 0.85f, 0.78f), tex => hCard.SetThumbnail(tex));
                }
            }

            var importedGrid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            importedGrid.AddThemeConstantOverride("h_separation", 8);
            importedGrid.AddThemeConstantOverride("v_separation", 8);
            _boxImportedSubmeshes.AddChild(importedGrid);

            // Socket bone candidate list
            var socketBones = GetHeroSocketBoneNames();

            // Imported model submeshes
            var importedMeshes = CustomModelAutoRigger.CollectMeshes(_previewModelInstance);
            int totalVerts = 0;
            int totalTris = 0;
            int weaponCount = 0;

            foreach (var mi in importedMeshes)
            {
                if (mi.Mesh == null) continue;
                int vCount = 0;
                int tCount = 0;

                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                {
                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays != null && arrays.Count > (int)Mesh.ArrayType.Vertex)
                    {
                        var vVar = arrays[(int)Mesh.ArrayType.Vertex];
                        if (vVar.VariantType != Variant.Type.Nil)
                        {
                            var verts = vVar.As<Vector3[]>();
                            if (verts != null) vCount += verts.Length;
                        }
                        if (arrays.Count > (int)Mesh.ArrayType.Index)
                        {
                            var idxVar = arrays[(int)Mesh.ArrayType.Index];
                            if (idxVar.VariantType != Variant.Type.Nil)
                            {
                                var idxs = idxVar.As<int[]>();
                                if (idxs != null) tCount += idxs.Length / 3;
                            }
                        }
                    }
                }

                totalVerts += vCount;
                totalTris += tCount;

                string rawMeshName = mi.Name.ToString();
                string meshNameLower = rawMeshName.ToLowerInvariant();

                // Check for weapon or accessory keywords
                bool isLikelyWeapon = meshNameLower.Contains("gun") || meshNameLower.Contains("pistol") ||
                                      meshNameLower.Contains("weapon") || meshNameLower.Contains("rifle") ||
                                      meshNameLower.Contains("sword") || meshNameLower.Contains("blade") ||
                                      meshNameLower.Contains("holster") || meshNameLower.Contains("shotgun") ||
                                      meshNameLower.Contains("cannon");

                bool isLeftWeapon = false;
                if (isLikelyWeapon)
                {
                    isLeftWeapon = meshNameLower.Contains("_l") || meshNameLower.Contains(".l") ||
                                   meshNameLower.Contains("left") || meshNameLower.Contains("dual_l") ||
                                   meshNameLower.Contains("offhand");
                    weaponCount++;
                }

                // Build interactive visual submesh gallery card
                bool isOutlineShell = CustomModelAutoRigger.IsLikelyOutlineShell(mi);
                _submeshIsOutlineShell[rawMeshName] = isOutlineShell;

                bool initialIncluded = isOutlineShell ? (_chkIncludeBakedOutlines?.ButtonPressed ?? true) : true;
                string initialRoleText = isOutlineShell ? "Outline" : (isLikelyWeapon ? "Prop" : "Body");
                Color initialRoleCol = isOutlineShell
                    ? new Color(0.85f, 0.65f, 0.25f)
                    : (isLikelyWeapon ? new Color(0.45f, 0.72f, 0.96f) : new Color(0.45f, 0.85f, 0.62f));

                var card = new MeshThumbCard();
                card.Build(
                    rawMeshName,
                    $"{vCount:N0} v · {tCount:N0} t",
                    initialRoleText,
                    initialRoleCol,
                    initialIncluded,
                    isHeroKeepMode: false);
                var cardVBox = card.ExtraContent;

                var chk = new CheckBox
                {
                    Text = rawMeshName,
                    ButtonPressed = initialIncluded,
                    Visible = false
                };
                chk.SetMeta("SubmeshName", rawMeshName);
                chk.Toggled += (on) =>
                {
                    card.SetActive(on);
                    OnImportedSubmeshesChanged();
                };
                card.Toggled += (on) =>
                {
                    if (chk.ButtonPressed != on) chk.ButtonPressed = on;
                };
                card.IsolatePressed += () => IsolateSubmeshIn3D(rawMeshName);
                _importedSubmeshCheckboxes.Add(chk);
                cardVBox.AddChild(chk);

                _meshThumbService?.RequestThumbnail($"imp_{rawMeshName}", mi.Mesh, -1, new Color(0.86f, 0.84f, 0.78f), tex => card.SetThumbnail(tex));

                var lblOutlineBadge = new Label
                {
                    Text = "[OUTLINE SHELL]",
                    Visible = false
                };
                _submeshOutlineBadges[rawMeshName] = lblOutlineBadge;
                cardVBox.AddChild(lblOutlineBadge);

                var chkOutlineShell = new CheckBox
                {
                    Text = "Outline Hull",
                    ButtonPressed = isOutlineShell,
                    TooltipText = "Mark or unmark this submesh as an Inverted-Hull Outline Shell (single-sided backface culling, unlit, no extra toon outline)."
                };
                ImporterStyle.ApplyFontSize(chkOutlineShell, ImporterStyle.FontHint);
                chkOutlineShell.Toggled += (isOut) =>
                {
                    _submeshIsOutlineShell[rawMeshName] = isOut;
                    card.SetRoleBadge(
                        isOut ? "Outline" : (isLikelyWeapon ? "Prop" : "Body"),
                        isOut ? new Color(0.85f, 0.65f, 0.25f) : (isLikelyWeapon ? new Color(0.45f, 0.72f, 0.96f) : new Color(0.45f, 0.85f, 0.62f)));
                    if (!GodotObject.IsInstanceValid(mi)) return;
                    mi.SetMeta("IsOutlineShell", isOut);
                    TriggerRealtimeRigUpdate();
                };
                _submeshOutlineCheckboxes[rawMeshName] = chkOutlineShell;
                cardVBox.AddChild(chkOutlineShell);

                // Row 2: Role selection (Skinned Body vs Socket / Weapon)
                var roleRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                roleRow.AddThemeConstantOverride("separation", 6);
                cardVBox.AddChild(roleRow);

                var lblRole = new Label
                {
                    Text = "Role:",
                    CustomMinimumSize = new Vector2(40, 0)
                };
                _secondaryLabels.Add(lblRole);
                roleRow.AddChild(lblRole);

                var optRole = new OptionButton
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };
                ConfigureOptionButton(optRole);
                optRole.AddItem("Skinned Body (Deforms with Skeleton)", 0);
                optRole.AddItem("Socket / Weapon (Rigid Attachment to Bone)", 1);
                roleRow.AddChild(optRole);

                // Row 3: Socket & Replacement Config
                var socketRow = new HBoxContainer
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    Visible = isLikelyWeapon
                };
                socketRow.AddThemeConstantOverride("separation", 6);
                cardVBox.AddChild(socketRow);

                var lblSocket = new Label
                {
                    Text = "Bone:",
                    CustomMinimumSize = new Vector2(40, 0)
                };
                _secondaryLabels.Add(lblSocket);
                socketRow.AddChild(lblSocket);

                var optSocket = new OptionButton
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };
                ConfigureOptionButton(optSocket);

                string preferredSocket = isLeftWeapon ? "weapon_hand_L" : "weapon_hand_R";
                string fallbackSocket = isLeftWeapon ? "hand_L" : "hand_R";

                int selectedSocketIdx = 0;
                for (int b = 0; b < socketBones.Count; b++)
                {
                    optSocket.AddItem(socketBones[b], b);
                    if (isLikelyWeapon)
                    {
                        if (socketBones[b] == preferredSocket)
                        {
                            selectedSocketIdx = b;
                        }
                        else if (selectedSocketIdx == 0 && socketBones[b] == fallbackSocket)
                        {
                            selectedSocketIdx = b;
                        }
                    }
                }
                optSocket.Select(selectedSocketIdx);
                socketRow.AddChild(optSocket);

                var lblReplaces = new Label
                {
                    Text = "Replaces:",
                    CustomMinimumSize = new Vector2(55, 0)
                };
                _secondaryLabels.Add(lblReplaces);
                socketRow.AddChild(lblReplaces);

                var optReplaces = new OptionButton
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };
                ConfigureOptionButton(optReplaces);
                optReplaces.AddItem("[ None ]", 0);
                int selectedReplIdx = 0;
                for (int h = 0; h < heroMeshes.Count; h++)
                {
                    string hName = heroMeshes[h].Name.ToString();
                    string hLower = hName.ToLowerInvariant();
                    optReplaces.AddItem(hName, h + 1);
                    if (isLikelyWeapon)
                    {
                        if (isLeftWeapon)
                        {
                            if (hLower.Contains("weapon_l") || hLower.Contains("gun_l") || hLower.Contains("pistol_l") || hLower.Contains("_l"))
                            {
                                selectedReplIdx = h + 1;
                            }
                        }
                        else
                        {
                            // Prefer primary weapon mesh (e.g. _CHRONO_GUN_MODEL)
                            if (hLower.Contains("gun_model") || hLower.Contains("weapon_model") ||
                                (selectedReplIdx == 0 && (hLower.Contains("weapon_r") || hLower.Contains("gun_r") || hLower.Contains("pistol_r") || hLower.Contains("gun") || hLower.Contains("weapon"))))
                            {
                                selectedReplIdx = h + 1;
                            }
                        }
                    }
                }
                optReplaces.Select(selectedReplIdx);
                socketRow.AddChild(optReplaces);

                // Row 4: Placement Row (Snap to Joint vs Keep Authored Position)
                var placementRow = new HBoxContainer
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    Visible = isLikelyWeapon
                };
                placementRow.AddThemeConstantOverride("separation", 6);
                cardVBox.AddChild(placementRow);

                var lblPlacement = new Label
                {
                    Text = "Fit:",
                    CustomMinimumSize = new Vector2(40, 0)
                };
                _secondaryLabels.Add(lblPlacement);
                placementRow.AddChild(lblPlacement);

                var optPlacement = new OptionButton
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };
                ConfigureOptionButton(optPlacement);
                optPlacement.AddItem("Keep Authored Position (e.g. Holstered on Hip)", 0);
                optPlacement.AddItem("Snap to Bone Joint (e.g. Grip in Hand)", 1);
                placementRow.AddChild(optPlacement);

                // Row 5: Weapon Socket Transform & Alignment Box
                var weaponTransformBox = new VBoxContainer
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    Visible = isLikelyWeapon
                };
                weaponTransformBox.AddThemeConstantOverride("separation", 4);
                cardVBox.AddChild(weaponTransformBox);

                // Rotations: Pitch, Yaw, Roll
                var rotRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                rotRow.AddThemeConstantOverride("separation", 4);
                var lblRot = new Label { Text = "Rot (°):", CustomMinimumSize = new Vector2(40, 0) };
                _secondaryLabels.Add(lblRot);
                rotRow.AddChild(lblRot);

                var spinPitch = CreateCoordSpinBox(-180.0, 180.0, 5.0, "P");
                var spinYaw = CreateCoordSpinBox(-180.0, 180.0, 5.0, "Y");
                var spinRoll = CreateCoordSpinBox(-180.0, 180.0, 5.0, "R");
                rotRow.AddChild(spinPitch);
                rotRow.AddChild(spinYaw);
                rotRow.AddChild(spinRoll);
                weaponTransformBox.AddChild(rotRow);

                // Position Offsets: X, Y, Z
                var posRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                posRow.AddThemeConstantOverride("separation", 4);
                var lblPos = new Label { Text = "Pos (m):", CustomMinimumSize = new Vector2(40, 0) };
                _secondaryLabels.Add(lblPos);
                posRow.AddChild(lblPos);

                var spinOffX = CreateCoordSpinBox(-1.0, 1.0, 0.01, "X");
                var spinOffY = CreateCoordSpinBox(-1.0, 1.0, 0.01, "Y");
                var spinOffZ = CreateCoordSpinBox(-1.0, 1.0, 0.01, "Z");
                posRow.AddChild(spinOffX);
                posRow.AddChild(spinOffY);
                posRow.AddChild(spinOffZ);
                weaponTransformBox.AddChild(posRow);

                // Scale & Preset Buttons
                var presetRow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                presetRow.AddThemeConstantOverride("h_separation", 4);
                presetRow.AddThemeConstantOverride("v_separation", 4);

                var lblWpScale = new Label { Text = "Scale:", CustomMinimumSize = new Vector2(40, 0) };
                _secondaryLabels.Add(lblWpScale);
                presetRow.AddChild(lblWpScale);

                var spinWpScale = new SpinBox { MinValue = 0.1, MaxValue = 5.0, Step = 0.05, Value = 1.0, CustomMinimumSize = new Vector2(65, 0) };
                presetRow.AddChild(spinWpScale);

                var btnHandGrip = new Button { Text = "Grip Forward", CustomMinimumSize = new Vector2(75, 24), TooltipText = "Sets forward hand grip rotation (Pitch 90°)" };
                presetRow.AddChild(btnHandGrip);

                var btnStraightenCard = new Button { Text = "Straighten", CustomMinimumSize = new Vector2(65, 24), TooltipText = "Auto-aligns weapon barrel forward (+Z)" };
                presetRow.AddChild(btnStraightenCard);

                var btnYaw90 = new Button { Text = "+90° Y", CustomMinimumSize = new Vector2(48, 24), TooltipText = "Rotate 90° Yaw" };
                presetRow.AddChild(btnYaw90);

                var btnPitch90 = new Button { Text = "+90° P", CustomMinimumSize = new Vector2(48, 24), TooltipText = "Rotate 90° Pitch" };
                presetRow.AddChild(btnPitch90);

                var btnResetWp = new Button { Text = "Reset", CustomMinimumSize = new Vector2(44, 24) };
                presetRow.AddChild(btnResetWp);

                weaponTransformBox.AddChild(presetRow);

                _submeshCardControls[rawMeshName] = (spinPitch, spinYaw, spinRoll, spinOffX, spinOffY, spinOffZ, spinWpScale, optSocket, optReplaces, optPlacement, optRole);

                void UpdateWeaponOffsets(bool syncLinked = true)
                {
                    _submeshSocketOffsetRot[rawMeshName] = new Vector3((float)spinPitch.Value, (float)spinYaw.Value, (float)spinRoll.Value);
                    _submeshSocketOffsetPos[rawMeshName] = new Vector3((float)spinOffX.Value, (float)spinOffY.Value, (float)spinOffZ.Value);
                    _submeshSocketScale[rawMeshName] = (float)spinWpScale.Value;

                    if (syncLinked && (_chkLinkWeaponSubmeshes?.ButtonPressed ?? true) && optRole.Selected == 1)
                    {
                        foreach (var (otherName, ctrl) in _submeshCardControls)
                        {
                            if (otherName == rawMeshName || ctrl.Role.Selected != 1) continue;
                            ctrl.Pitch.SetValueNoSignal(spinPitch.Value);
                            ctrl.Yaw.SetValueNoSignal(spinYaw.Value);
                            ctrl.Roll.SetValueNoSignal(spinRoll.Value);
                            ctrl.PosX.SetValueNoSignal(spinOffX.Value);
                            ctrl.PosY.SetValueNoSignal(spinOffY.Value);
                            ctrl.PosZ.SetValueNoSignal(spinOffZ.Value);
                            ctrl.Scale.SetValueNoSignal(spinWpScale.Value);

                            _submeshSocketOffsetRot[otherName] = _submeshSocketOffsetRot[rawMeshName];
                            _submeshSocketOffsetPos[otherName] = _submeshSocketOffsetPos[rawMeshName];
                            _submeshSocketScale[otherName] = _submeshSocketScale[rawMeshName];
                        }
                    }

                    if (!_isGizmoUpdatingFromUI)
                    {
                        if (!TryApplyFastSocketPreviewTransform())
                        {
                            ScheduleDebouncedLiveRigUpdate();
                        }
                        if (!_isGizmoDragging && !(_previewGizmo?.Editing ?? false))
                {
                    UpdateGizmoTargetTransform();
                }
                    }
                }

                spinPitch.ValueChanged += (_) => UpdateWeaponOffsets();
                spinYaw.ValueChanged += (_) => UpdateWeaponOffsets();
                spinRoll.ValueChanged += (_) => UpdateWeaponOffsets();
                spinOffX.ValueChanged += (_) => UpdateWeaponOffsets();
                spinOffY.ValueChanged += (_) => UpdateWeaponOffsets();
                spinOffZ.ValueChanged += (_) => UpdateWeaponOffsets();
                spinWpScale.ValueChanged += (_) => UpdateWeaponOffsets();

                btnHandGrip.Pressed += () =>
                {
                    spinPitch.Value = 90.0;
                    spinYaw.Value = 0.0;
                    spinRoll.Value = 0.0;
                    spinOffZ.Value = 0.04;
                    UpdateWeaponOffsets();
                };

                btnStraightenCard.Pressed += () =>
                {
                    OnStraightenWeaponPressed();
                };

                btnYaw90.Pressed += () =>
                {
                    double next = (spinYaw.Value + 90.0) % 360.0;
                    if (next > 180.0) next -= 360.0;
                    spinYaw.Value = next;
                    UpdateWeaponOffsets();
                };

                btnPitch90.Pressed += () =>
                {
                    double next = (spinPitch.Value + 90.0) % 360.0;
                    if (next > 180.0) next -= 360.0;
                    spinPitch.Value = next;
                    UpdateWeaponOffsets();
                };

                btnResetWp.Pressed += () =>
                {
                    spinPitch.Value = 0.0;
                    spinYaw.Value = 0.0;
                    spinRoll.Value = 0.0;
                    spinOffX.Value = 0.0;
                    spinOffY.Value = 0.0;
                    spinOffZ.Value = 0.0;
                    spinWpScale.Value = 1.0;
                    UpdateWeaponOffsets();
                };

                // Initialize role selection and state
                if (isLikelyWeapon)
                {
                    optRole.Select(1); // Socket / Weapon
                    string chosenBone = optSocket.GetItemText(optSocket.Selected).Replace("★ ", "");
                    _submeshSocketAssignments[rawMeshName] = chosenBone;
                    if (optReplaces.Selected > 0)
                    {
                        _submeshHeroReplacements[rawMeshName] = optReplaces.GetItemText(optReplaces.Selected);
                    }
                    bool shouldSnap = chosenBone.Contains("hand");
                    optPlacement.Select(shouldSnap ? 1 : 0);
                    _submeshSnapToSocket[rawMeshName] = shouldSnap;
                    UpdateWeaponOffsets(false);
                }
                else
                {
                    optRole.Select(0); // Skinned Body
                }

                // Event Listeners
                optRole.ItemSelected += (idx) =>
                {
                    bool isSocket = (idx == 1);
                    socketRow.Visible = isSocket;
                    placementRow.Visible = isSocket;
                    weaponTransformBox.Visible = isSocket;
                    if (isSocket)
                    {
                        string bName = optSocket.GetItemText(optSocket.Selected).Replace("★ ", "");
                        _submeshSocketAssignments[rawMeshName] = bName;
                        if (optReplaces.Selected > 0)
                        {
                            _submeshHeroReplacements[rawMeshName] = optReplaces.GetItemText(optReplaces.Selected);
                        }
                        else
                        {
                            _submeshHeroReplacements.Remove(rawMeshName);
                        }
                        _submeshSnapToSocket[rawMeshName] = (optPlacement.Selected == 1);
                        UpdateWeaponOffsets();
                    }
                    else
                    {
                        _submeshSocketAssignments.Remove(rawMeshName);
                        _submeshHeroReplacements.Remove(rawMeshName);
                        _submeshSnapToSocket.Remove(rawMeshName);
                        _submeshSocketOffsetPos.Remove(rawMeshName);
                        _submeshSocketOffsetRot.Remove(rawMeshName);
                        _submeshSocketScale.Remove(rawMeshName);
                        TriggerRealtimeRigUpdate();
                    }
                };

                optPlacement.ItemSelected += (pIdx) =>
                {
                    _submeshSnapToSocket[rawMeshName] = (pIdx == 1);
                    if ((_chkLinkWeaponSubmeshes?.ButtonPressed ?? true) && optRole.Selected == 1)
                    {
                        foreach (var (otherName, ctrl) in _submeshCardControls)
                        {
                            if (otherName == rawMeshName || ctrl.Role.Selected != 1) continue;
                            ctrl.Placement.Select((int)pIdx);
                            _submeshSnapToSocket[otherName] = (pIdx == 1);
                        }
                    }
                    TriggerRealtimeRigUpdate();
                };

                optSocket.ItemSelected += (idx) =>
                {
                    if (optRole.Selected == 1)
                    {
                        string bName = optSocket.GetItemText((int)idx).Replace("★ ", "");
                        _submeshSocketAssignments[rawMeshName] = bName;
                        if (bName.Contains("hand"))
                        {
                            optPlacement.Select(1);
                            _submeshSnapToSocket[rawMeshName] = true;
                        }

                        if (_chkLinkWeaponSubmeshes?.ButtonPressed ?? true)
                        {
                            foreach (var (otherName, ctrl) in _submeshCardControls)
                            {
                                if (otherName == rawMeshName || ctrl.Role.Selected != 1) continue;
                                ctrl.Socket.Select((int)idx);
                                _submeshSocketAssignments[otherName] = bName;
                                if (bName.Contains("hand"))
                                {
                                    ctrl.Placement.Select(1);
                                    _submeshSnapToSocket[otherName] = true;
                                }
                            }
                        }
                        TriggerRealtimeRigUpdate();
                    }
                };

                optReplaces.ItemSelected += (idx) =>
                {
                    if (optRole.Selected == 1 && idx > 0)
                    {
                        string repName = optReplaces.GetItemText((int)idx);
                        _submeshHeroReplacements[rawMeshName] = repName;
                        if (_chkLinkWeaponSubmeshes?.ButtonPressed ?? true)
                        {
                            foreach (var (otherName, ctrl) in _submeshCardControls)
                            {
                                if (otherName == rawMeshName || ctrl.Role.Selected != 1) continue;
                                ctrl.Replaces.Select((int)idx);
                                _submeshHeroReplacements[otherName] = repName;
                            }
                        }
                    }
                    else
                    {
                        _submeshHeroReplacements.Remove(rawMeshName);
                        if (_chkLinkWeaponSubmeshes?.ButtonPressed ?? true)
                        {
                            foreach (var (otherName, ctrl) in _submeshCardControls)
                            {
                                if (otherName == rawMeshName || ctrl.Role.Selected != 1) continue;
                                ctrl.Replaces.Select(0);
                                _submeshHeroReplacements.Remove(otherName);
                            }
                        }
                    }
                    TriggerRealtimeRigUpdate();
                };

                importedGrid.AddChild(card);
            }

            _lblGeometryStats.Text = $"Geometry: {importedMeshes.Count} submesh(es) | {totalVerts:N0} vertices | {totalTris:N0} triangles";
        }

        private static readonly HashSet<string> RequiredCoreCanonicalBones = new(StringComparer.OrdinalIgnoreCase)
        {
            "pelvis", "spine_0", "neck_0", "head",
            "arm_upper_L", "arm_lower_L", "hand_L",
            "arm_upper_R", "arm_lower_R", "hand_R",
            "leg_upper_L", "leg_lower_L", "ankle_L",
            "leg_upper_R", "leg_lower_R", "ankle_R"
        };

        private static string GetBoneCategoryKey(HumanoidBoneMapper.BoneGroup group)
        {
            return group switch
            {
                HumanoidBoneMapper.BoneGroup.Root => "torso",
                HumanoidBoneMapper.BoneGroup.Pelvis => "torso",
                HumanoidBoneMapper.BoneGroup.Spine => "torso",
                HumanoidBoneMapper.BoneGroup.Head => "torso",
                HumanoidBoneMapper.BoneGroup.LeftArm => "left_arm",
                HumanoidBoneMapper.BoneGroup.LeftHandFingers => "left_fingers",
                HumanoidBoneMapper.BoneGroup.RightArm => "right_arm",
                HumanoidBoneMapper.BoneGroup.RightHandFingers => "right_fingers",
                HumanoidBoneMapper.BoneGroup.LeftLeg => "left_leg",
                HumanoidBoneMapper.BoneGroup.RightLeg => "right_leg",
                HumanoidBoneMapper.BoneGroup.Secondary => "secondary",
                _ => "secondary"
            };
        }

        private BoneCategorySectionState CreateBoneCategorySection(
            Control parent,
            string key,
            string title,
            bool defaultExpanded)
        {
            var card = new PanelContainer
            {
                ThemeTypeVariation = "CardPanel",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            parent.AddChild(card);

            var cardVBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            cardVBox.AddThemeConstantOverride("separation", 6);
            card.AddChild(cardVBox);

            var headerBtn = new Button
            {
                ThemeTypeVariation = "ToolbarButton",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 32),
                Text = "",
                TooltipText = $"Expand or collapse {title}"
            };
            cardVBox.AddChild(headerBtn);

            var headerMargin = new MarginContainer();
            headerMargin.AddThemeConstantOverride("margin_left", 8);
            headerMargin.AddThemeConstantOverride("margin_right", 8);
            headerMargin.MouseFilter = Control.MouseFilterEnum.Ignore;
            headerMargin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            headerBtn.AddChild(headerMargin);

            var headerRow = new HBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            headerRow.AddThemeConstantOverride("separation", 6);
            headerMargin.AddChild(headerRow);

            var lblChevron = new Label
            {
                Text = defaultExpanded ? "v" : ">",
                CustomMinimumSize = new Vector2(14, 0),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            _primaryLabels.Add(lblChevron);
            headerRow.AddChild(lblChevron);

            var lblTitle = new Label
            {
                Text = title,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            _primaryLabels.Add(lblTitle);
            headerRow.AddChild(lblTitle);

            var lblBadge = new Label
            {
                Text = "0 / 0",
                HorizontalAlignment = HorizontalAlignment.Right,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            headerRow.AddChild(lblBadge);

            var bodyBox = new VBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                Visible = defaultExpanded
            };
            bodyBox.AddThemeConstantOverride("separation", 5);
            cardVBox.AddChild(bodyBox);

            var section = new BoneCategorySectionState
            {
                Key = key,
                Title = title,
                Card = card,
                HeaderButton = headerBtn,
                ChevronLabel = lblChevron,
                TitleLabel = lblTitle,
                BadgeLabel = lblBadge,
                BodyBox = bodyBox,
                UserExpanded = defaultExpanded
            };

            headerBtn.Pressed += () =>
            {
                section.UserExpanded = !section.UserExpanded;
                ApplyBoneSectionExpansion(section, section.UserExpanded);
            };

            _boneCategorySections.Add(section);
            _boneCategoryByKey[key] = section;
            return section;
        }

        private static void ApplyBoneSectionExpansion(BoneCategorySectionState section, bool expanded)
        {
            if (section == null || !GodotObject.IsInstanceValid(section.BodyBox)) return;
            section.BodyBox.Visible = expanded;
            if (GodotObject.IsInstanceValid(section.ChevronLabel))
            {
                section.ChevronLabel.Text = expanded ? "v" : ">";
            }
        }

        private static string FormatSourceBoneDisplayName(string rawName, int parentIdx, Skeleton3D sourceSkeleton)
        {
            string baseName = HumanoidBoneMapper.SplitGltfNumericSuffix(rawName, out string suffix);
            return string.IsNullOrEmpty(suffix)
                ? baseName
                : $"{baseName}  [{suffix.TrimStart('_')}]";
        }

        private static string FormatSourceBoneTooltip(string rawName, Skeleton3D sourceSkeleton)
        {
            if (sourceSkeleton == null || string.IsNullOrEmpty(rawName)) return "Auto-collapse to parent bone";
            int bIdx = sourceSkeleton.FindBone(rawName);
            if (bIdx < 0) return rawName;
            int pIdx = sourceSkeleton.GetBoneParent(bIdx);
            string parentName = (pIdx >= 0 && pIdx < sourceSkeleton.GetBoneCount()) ? sourceSkeleton.GetBoneName(pIdx) : "<Root>";
            return $"Source Bone: {rawName}\nParent: {parentName}";
        }

        private Skeleton3D GetValidSourceSkeleton()
        {
            if (_cachedSourceSkeleton != null && GodotObject.IsInstanceValid(_cachedSourceSkeleton) && !_cachedSourceSkeleton.IsQueuedForDeletion())
            {
                return _cachedSourceSkeleton;
            }

            Skeleton3D found = null;
            if (_previewModelInstance != null && GodotObject.IsInstanceValid(_previewModelInstance) && !_previewModelInstance.IsQueuedForDeletion())
            {
                found = CustomModelAutoRigger.FindSkeleton(_previewModelInstance);
            }
            if (found == null && _liveImportedScene != null && GodotObject.IsInstanceValid(_liveImportedScene))
            {
                found = CustomModelAutoRigger.FindSkeleton(_liveImportedScene);
            }
            _cachedSourceSkeleton = found;
            return found;
        }

        private void AnalyzeModelRigStatus()
        {
            var sourceSkeleton = CustomModelAutoRigger.FindSkeleton(_previewModelInstance);
            _cachedSourceSkeleton = sourceSkeleton;

            foreach (Node child in _boxBoneMappingContainer.GetChildren())
            {
                child.QueueFree();
            }
            _userBoneRemap.Clear();
            _boneCategorySections.Clear();
            _boneCategoryByKey.Clear();
            _boneRowsByCanonical.Clear();
            _autoMappedSnapshot.Clear();

            if (sourceSkeleton != null && sourceSkeleton.GetBoneCount() > 0)
            {
                PopulateRiggingMethodOptions(hasRig: true);
                SetRigBadgeStatus($"Pre-Rigged Skeleton ({sourceSkeleton.GetBoneCount()} bones) — Remap Ready", StatusTone.Success);

                for (int i = 0; i < _optRiggingMethod.ItemCount; i++)
                {
                    if (_optRiggingMethod.GetItemId(i) == (int)CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap)
                    {
                        _optRiggingMethod.Select(i);
                        OnRiggingMethodChanged(i);
                        break;
                    }
                }

                // Analyze rig family, weighted bones, and clothing/cape extra chains via RigBoneClassifier
                var activeMeshes = FilterActiveImportedMeshes(CustomModelAutoRigger.CollectMeshes(_previewModelInstance));
                _lastWeightedSrcBones = RigBoneClassifier.ComputeWeightedBoneIndices(sourceSkeleton, activeMeshes);

                int srcCount = sourceSkeleton.GetBoneCount();
                var boneNamesList = new string[srcCount];
                var boneParentsList = new int[srcCount];
                var boneWorldPositions = new Vector3[srcCount];
                for (int b = 0; b < srcCount; b++)
                {
                    boneNamesList[b] = sourceSkeleton.GetBoneName(b);
                    boneParentsList[b] = sourceSkeleton.GetBoneParent(b);
                    boneWorldPositions[b] = sourceSkeleton.GetBoneGlobalPose(b).Origin;
                }

                RigFamily? famOverride = null;
                if (_optRigFamilyOverride != null && _optRigFamilyOverride.Selected >= 0)
                {
                    int fid = _optRigFamilyOverride.GetItemId(_optRigFamilyOverride.Selected);
                    if (fid >= 0) famOverride = (RigFamily)fid;
                }

                _lastRigAnalysis = RigBoneClassifier.Analyze(
                    boneNamesList,
                    boneParentsList,
                    familyOverride: famOverride,
                    weightedBones: _lastWeightedSrcBones,
                    boneWorldPositions: boneWorldPositions);

                if (_lblRigProfileConfidence != null)
                {
                    string detName = _lastRigAnalysis.DetectedFamily.ToString();
                    int confPct = Mathf.RoundToInt((float)(_lastRigAnalysis.Confidence * 100.0));
                    _lblRigProfileConfidence.Text = $"Detected: {detName} ({confPct}% conf) · {_lastWeightedSrcBones.Count}/{srcCount} weighted";
                }

                // Compute conflict-free 1-to-1 AutoMap (matching CustomModelAutoRigger exactly)
                Dictionary<string, string> autoMapByTarget = _previewHeroSkeleton != null
                    ? HumanoidBoneMapper.AutoMap(sourceSkeleton, _previewHeroSkeleton)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // Create categorized accordion sections
                var torsoSec = CreateBoneCategorySection(_boxBoneMappingContainer, "torso", "Torso & Head", defaultExpanded: true);
                var leftArmSec = CreateBoneCategorySection(_boxBoneMappingContainer, "left_arm", "Left Arm & Hand", defaultExpanded: true);
                var leftFingersSec = CreateBoneCategorySection(leftArmSec.BodyBox, "left_fingers", "Left Fingers", defaultExpanded: false);
                var rightArmSec = CreateBoneCategorySection(_boxBoneMappingContainer, "right_arm", "Right Arm & Hand", defaultExpanded: true);
                var rightFingersSec = CreateBoneCategorySection(rightArmSec.BodyBox, "right_fingers", "Right Fingers", defaultExpanded: false);
                var leftLegSec = CreateBoneCategorySection(_boxBoneMappingContainer, "left_leg", "Left Leg & Foot", defaultExpanded: true);
                var rightLegSec = CreateBoneCategorySection(_boxBoneMappingContainer, "right_leg", "Right Leg & Foot", defaultExpanded: true);
                var secondarySec = CreateBoneCategorySection(_boxBoneMappingContainer, "secondary", "Secondary & Twist Bones", defaultExpanded: false);

                // Cache source dropdown item entries once (clean glTF suffix de-emphasis + hierarchy tooltip)
                var srcRawNames = new string[srcCount];
                var srcDisplayLabels = new string[srcCount];
                var srcTooltips = new string[srcCount];
                for (int b = 0; b < srcCount; b++)
                {
                    string raw = boneNamesList[b];
                    srcRawNames[b] = raw;
                    srcDisplayLabels[b] = FormatSourceBoneDisplayName(raw, boneParentsList[b], sourceSkeleton);
                    string weightTag = _lastWeightedSrcBones.Contains(b) ? "Weighted" : "Unweighted";
                    srcTooltips[b] = FormatSourceBoneTooltip(raw, sourceSkeleton) + $"\nStatus: {weightTag} | Class: {_lastRigAnalysis.Bones[b].Kind}";
                }

                foreach (var def in HumanoidBoneMapper.Definitions)
                {
                    string targetHeroBone = _previewHeroSkeleton != null
                        ? HumanoidBoneMapper.ResolveTargetBone(_previewHeroSkeleton, def)
                        : def.CanonicalName;
                    if (string.IsNullOrEmpty(targetHeroBone)) continue;

                    string matchedBoneName = "";
                    if (_previewHeroSkeleton != null)
                    {
                        autoMapByTarget.TryGetValue(targetHeroBone, out matchedBoneName);
                    }
                    else
                    {
                        matchedBoneName = HumanoidBoneMapper.ResolveSourceBone(sourceSkeleton, def);
                    }
                    if (string.IsNullOrEmpty(matchedBoneName) &&
                        _lastRigAnalysis != null &&
                        _lastRigAnalysis.CanonicalToSource.TryGetValue(def.CanonicalName, out int clsSrcIdx) &&
                        clsSrcIdx >= 0 && clsSrcIdx < srcRawNames.Length)
                    {
                        matchedBoneName = srcRawNames[clsSrcIdx];
                    }
                    matchedBoneName ??= "";
                    _autoMappedSnapshot[def.CanonicalName] = matchedBoneName;

                    string catKey = GetBoneCategoryKey(def.Group);
                    if (!_boneCategoryByKey.TryGetValue(catKey, out var section)) section = secondarySec;

                    var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                    row.AddThemeConstantOverride("separation", 6);

                    bool isCore = RequiredCoreCanonicalBones.Contains(def.CanonicalName);
                    bool isMatched = !string.IsNullOrEmpty(matchedBoneName);
                    string statusGlyph = isMatched ? "✓ " : (isCore ? "⚠ " : "○ ");
                    var lblTarget = new Label
                    {
                        Text = statusGlyph + def.DisplayLabel,
                        CustomMinimumSize = new Vector2(128, 28),
                        SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
                        ClipText = true,
                        TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                        TooltipText = $"Hero Target: {targetHeroBone} (canonical: {def.CanonicalName})" + (isCore ? "\nRequired core limb/torso joint" : "\nOptional joint (auto-collapses cleanly)")
                    };
                    ImporterStyle.ApplyFontSize(lblTarget, ImporterStyle.FontHint);
                    row.AddChild(lblTarget);

                    var optBone = new OptionButton
                    {
                        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                        CustomMinimumSize = new Vector2(0, 28)
                    };
                    ConfigureOptionButton(optBone);
                    optBone.AddItem("[ None / Auto-Collapse ]", 0);

                    PopupMenu popup = optBone.GetPopup();
                    for (int b = 0; b < srcCount; b++)
                    {
                        optBone.AddItem(srcDisplayLabels[b], b + 1);
                        if (popup != null)
                        {
                            popup.SetItemTooltip(b + 1, srcTooltips[b]);
                        }
                    }

                    SelectBoneDropdownBySourceName(optBone, sourceSkeleton, matchedBoneName);
                    if (!string.IsNullOrEmpty(matchedBoneName))
                    {
                        _userBoneRemap[def.CanonicalName] = matchedBoneName;
                        optBone.TooltipText = FormatSourceBoneTooltip(matchedBoneName, sourceSkeleton);
                    }
                    else
                    {
                        optBone.TooltipText = "Unmapped (auto-collapses to parent bone)";
                    }

                    var lblRowStatus = new Label
                    {
                        Text = "",
                        CustomMinimumSize = new Vector2(34, 0),
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    var btnDisableSlot = new Button
                    {
                        Text = "×",
                        CustomMinimumSize = new Vector2(24, 24),
                        TooltipText = "Disable mapped source bone (collapses weights to parent bone). Or right-click a bone in 3D viewport."
                    };
                    row.AddChild(optBone);
                    row.AddChild(lblRowStatus);
                    row.AddChild(btnDisableSlot);
                    section.BodyBox.AddChild(row);

                    var entry = new BoneRowEntryState
                    {
                        CanonicalName = def.CanonicalName,
                        TargetBoneName = targetHeroBone,
                        DisplayLabel = def.DisplayLabel,
                        Group = def.Group,
                        CategoryKey = catKey,
                        IsRequiredCore = isCore,
                        RowBox = row,
                        TargetLabel = lblTarget,
                        BoneDropdown = optBone,
                        StatusBadge = lblRowStatus
                    };
                    section.Rows.Add(entry);
                    _boneRowsByCanonical[def.CanonicalName] = entry;

                    btnDisableSlot.Pressed += () =>
                    {
                        if (_userBoneRemap.TryGetValue(entry.CanonicalName, out string curSrc) && !string.IsNullOrEmpty(curSrc))
                        {
                            if (!_disabledSourceBones.Add(curSrc)) _disabledSourceBones.Remove(curSrc);
                            RefreshDisabledBonesList();
                            ApplyBoneMappingFilter();
                            MarkRemapStaleAndRefresh();
                        }
                    };

                    optBone.ItemSelected += (long itemIdx) =>
                    {
                        if (_isUpdatingBoneDropdowns) return;
                        long rawId = optBone.GetItemId((int)itemIdx);
                        int bIdx = (int)rawId - 1;
                        if (bIdx >= 0 && bIdx < srcRawNames.Length)
                        {
                            string rawBone = srcRawNames[bIdx];
                            _userBoneRemap[entry.CanonicalName] = rawBone;
                            optBone.TooltipText = srcTooltips[bIdx];
                        }
                        else
                        {
                            _userBoneRemap.Remove(entry.CanonicalName);
                            optBone.TooltipText = "Unmapped (auto-collapses to parent bone)";
                        }
                        ApplyBoneMappingFilter();
                        MarkRemapStaleAndRefresh();
                    };
                }

                // Populate detected clothing/cape/hair chains into _boneChains (preserving any synthetic chains)
                var mappedSrcIndices = new HashSet<int>();
                var srcToTarget = new Dictionary<int, int>();
                foreach (var (canon, srcName) in _userBoneRemap)
                {
                    if (!string.IsNullOrEmpty(srcName))
                    {
                        int sb = sourceSkeleton.FindBone(srcName);
                        if (sb >= 0)
                        {
                            mappedSrcIndices.Add(sb);
                            if (_previewHeroSkeleton != null && _boneRowsByCanonical.TryGetValue(canon, out var brow))
                            {
                                int tb = _previewHeroSkeleton.FindBone(brow.TargetBoneName);
                                if (tb >= 0) srcToTarget[sb] = tb;
                            }
                        }
                    }
                }
                var existingSynthetic = _boneChains.Where(c => c.IsSynthetic).ToList();
                var existingByRoot = _boneChains.Where(c => !c.IsSynthetic)
                    .ToDictionary(c => c.RootSourceBone, StringComparer.OrdinalIgnoreCase);
                _boneChains.Clear();
                _boneChains.AddRange(existingSynthetic);
                var extraChains = RigBoneClassifier.FindExtraChains(_lastRigAnalysis, mappedSrcIndices, _lastWeightedSrcBones);
                var newlyAddedChains = new List<BoneChainDef>();
                foreach (var ec in extraChains)
                {
                    if (existingByRoot.TryGetValue(ec.RootName, out var prevDef))
                    {
                        _boneChains.Add(prevDef);
                        continue;
                    }
                    string anchor = _previewHeroSkeleton != null
                        ? RigBoneClassifier.ResolveAnchorHeroBoneName(_lastRigAnalysis, ec, srcToTarget, _previewHeroSkeleton)
                        : "spine_3";
                    var newDef = new BoneChainDef
                    {
                        ChainId = Guid.NewGuid().ToString("N")[..8],
                        Name = ec.RootName,
                        Category = ec.Category,
                        RootSourceBone = ec.RootName,
                        SourceBoneNames = ec.BoneIndices.Select(i => boneNamesList[i]).ToList(),
                        AnchorHeroBone = anchor
                    };
                    _boneChains.Add(newDef);
                    newlyAddedChains.Add(newDef);
                }
                if (newlyAddedChains.Count > 0)
                {
                    bool physOn = (_chkEnableCustomPhysics?.ButtonPressed ?? true) && (_chkPreserveClothingChains?.ButtonPressed ?? true);
                    RigBoneClassifier.ApplyRecommendedPhysicsModes(newlyAddedChains, physOn, resetTuningValues: true);
                }

                // Hide empty categories (e.g., Secondary if hero has no twist slots)
                foreach (var sec in _boneCategorySections)
                {
                    if (GodotObject.IsInstanceValid(sec.Card))
                    {
                        sec.Card.Visible = sec.Rows.Count > 0;
                    }
                }

                UpdateBoneMappingValidationBadges();
                ApplyBoneMappingFilter();
                RefreshClothingChainsPanel();
            RefreshBodyCollidersPanel();
                RefreshDisabledBonesList();
                RefreshSourceBoneDisableBrowser();
                RefreshBoneTabDiffSummary();
                MarkRemapAppliedBadge();
                ApplyThemeStyling();
            }
            else
            {
                _lastRigAnalysis = null;
                _lastWeightedSrcBones.Clear();
                _boneChains.RemoveAll(c => !c.IsSynthetic);
                if (_lblRigProfileConfidence != null) _lblRigProfileConfidence.Text = "Static mesh (0 source bones)";
                PopulateRiggingMethodOptions(hasRig: false);
                SetRigBadgeStatus("Static Mesh — Landmark Auto-Rig Active (16 Pins)", StatusTone.Info);
                if (_chkKeepFingersRigid != null) _chkKeepFingersRigid.ButtonPressed = true;

                for (int i = 0; i < _optRiggingMethod.ItemCount; i++)
                {
                    if (_optRiggingMethod.GetItemId(i) == (int)CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig)
                    {
                        _optRiggingMethod.Select(i);
                        OnRiggingMethodChanged(i);
                        break;
                    }
                }

                var lblInfo = new Label
                {
                    Text = "This model has no skeleton. Use Landmark Auto-Rig pins on Tab 2 to rig it, or choose Rigid Attachment / KNN Fallback.",
                    AutowrapMode = TextServer.AutowrapMode.WordSmart
                };
                _secondaryLabels.Add(lblInfo);
                lblInfo.AddThemeColorOverride("font_color", ThemeManager.GetColor(nameof(ThemePaletteData.TextSecondary)));
                _boxBoneMappingContainer.AddChild(lblInfo);
                RefreshClothingChainsPanel();
            RefreshBodyCollidersPanel();
                RefreshDisabledBonesList();
                RefreshBoneTabDiffSummary();
            }
        }

        private static void SelectBoneDropdownBySourceName(OptionButton optBone, Skeleton3D sourceSkeleton, string sourceBoneName)
        {
            if (optBone == null || sourceSkeleton == null) return;
            if (string.IsNullOrEmpty(sourceBoneName))
            {
                optBone.Select(0);
                return;
            }
            int matchedBoneIdx = sourceSkeleton.FindBone(sourceBoneName);
            if (matchedBoneIdx < 0)
            {
                optBone.Select(0);
                return;
            }
            for (int item = 1; item < optBone.ItemCount; item++)
            {
                if (optBone.GetItemId(item) == matchedBoneIdx + 1)
                {
                    optBone.Select(item);
                    return;
                }
            }
            optBone.Select(0);
        }

        private void UpdateBoneMappingValidationBadges()
        {
            if (_boneCategorySections.Count == 0) return;

            // Count how many target slots reference each source bone to catch duplicates
            var sourceUsage = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (canonical, srcBone) in _userBoneRemap)
            {
                if (string.IsNullOrWhiteSpace(srcBone)) continue;
                if (!sourceUsage.TryGetValue(srcBone, out var users))
                {
                    users = new List<string>();
                    sourceUsage[srcBone] = users;
                }
                users.Add(canonical);
            }

            int totalSlots = 0;
            int totalMapped = 0;
            int totalConflicts = 0;
            int totalMissingCore = 0;

            Color textPrimary = ThemeManager.GetColor(nameof(ThemePaletteData.TextPrimary));
            Color textSecondary = ThemeManager.GetColor(nameof(ThemePaletteData.TextSecondary));
            Color semSuccess = ThemeManager.GetColor(nameof(ThemePaletteData.SemanticSuccess));
            Color semWarning = ThemeManager.GetColor(nameof(ThemePaletteData.SemanticWarning));
            Color semDanger = ThemeManager.GetColor(nameof(ThemePaletteData.SemanticDanger));

            // First update leaf/flat sections (so Left/Right Fingers stats exist when rolling up Arm summaries if needed)
            foreach (var sec in _boneCategorySections)
            {
                if (sec.Rows.Count == 0) continue;

                int secMapped = 0;
                int secConflicts = 0;
                int secMissingCore = 0;

                foreach (var row in sec.Rows)
                {
                    totalSlots++;
                    _userBoneRemap.TryGetValue(row.CanonicalName, out string mapped);
                    bool isMapped = !string.IsNullOrEmpty(mapped);
                    bool isConflict = isMapped && sourceUsage.TryGetValue(mapped, out var sharedUsers) && sharedUsers.Count > 1;

                    if (isMapped)
                    {
                        secMapped++;
                        totalMapped++;
                    }
                    else if (row.IsRequiredCore)
                    {
                        secMissingCore++;
                        totalMissingCore++;
                    }

                    if (isConflict)
                    {
                        secConflicts++;
                        totalConflicts++;
                        string others = string.Join(", ", sourceUsage[mapped].Where(c => !c.Equals(row.CanonicalName, StringComparison.OrdinalIgnoreCase)));
                        row.StatusBadge.Text = "!";
                        row.StatusBadge.TooltipText = $"Duplicate conflict: '{mapped}' is also assigned to {others}. Auto-Snap may collapse a zero-length segment!";
                        row.StatusBadge.AddThemeColorOverride("font_color", semDanger);
                        row.TargetLabel.AddThemeColorOverride("font_color", semDanger);
                    }
                    else if (!isMapped && row.IsRequiredCore)
                    {
                        row.StatusBadge.Text = "!";
                        row.StatusBadge.TooltipText = $"Required core joint '{row.DisplayLabel}' is unmapped.";
                        row.StatusBadge.AddThemeColorOverride("font_color", semWarning);
                        row.TargetLabel.AddThemeColorOverride("font_color", semWarning);
                    }
                    else if (isMapped)
                    {
                        bool isDisabled = _disabledSourceBones.Contains(mapped);
                        _autoMappedSnapshot.TryGetValue(row.CanonicalName, out string autoSrc);
                        bool isOverridden = !string.Equals(mapped, autoSrc ?? "", StringComparison.OrdinalIgnoreCase);

                        if (isDisabled)
                        {
                            row.StatusBadge.Text = "Dis";
                            row.StatusBadge.TooltipText = $"Source bone '{mapped}' is disabled (weights collapse to parent bone).";
                            row.StatusBadge.AddThemeColorOverride("font_color", semDanger);
                            row.TargetLabel.AddThemeColorOverride("font_color", semDanger);
                        }
                        else if (isOverridden)
                        {
                            row.StatusBadge.Text = "Ovr";
                            row.StatusBadge.TooltipText = $"User override -> {mapped} (Auto was '{(string.IsNullOrEmpty(autoSrc) ? "None" : autoSrc)}')";
                            row.StatusBadge.AddThemeColorOverride("font_color", semWarning);
                            row.TargetLabel.AddThemeColorOverride("font_color", textPrimary);
                        }
                        else
                        {
                            row.StatusBadge.Text = "OK";
                            row.StatusBadge.TooltipText = $"Mapped cleanly -> {mapped}";
                            row.StatusBadge.AddThemeColorOverride("font_color", semSuccess);
                            row.TargetLabel.AddThemeColorOverride("font_color", textPrimary);
                        }
                    }
                    else
                    {
                        row.StatusBadge.Text = "--";
                        row.StatusBadge.TooltipText = "Optional joint unmapped (auto-collapses to parent)";
                        row.StatusBadge.AddThemeColorOverride("font_color", textSecondary);
                        row.TargetLabel.AddThemeColorOverride("font_color", textSecondary);
                    }
                }

                if (GodotObject.IsInstanceValid(sec.BadgeLabel))
                {
                    if (secConflicts > 0)
                    {
                        sec.BadgeLabel.Text = $"! {secConflicts} Conflict · {secMapped}/{sec.Rows.Count}";
                        sec.BadgeLabel.AddThemeColorOverride("font_color", semDanger);
                    }
                    else if (secMissingCore > 0)
                    {
                        sec.BadgeLabel.Text = $"! {secMissingCore} Missing · {secMapped}/{sec.Rows.Count}";
                        sec.BadgeLabel.AddThemeColorOverride("font_color", semWarning);
                    }
                    else if (secMapped == sec.Rows.Count)
                    {
                        sec.BadgeLabel.Text = $"{secMapped} / {sec.Rows.Count} Mapped";
                        sec.BadgeLabel.AddThemeColorOverride("font_color", semSuccess);
                    }
                    else
                    {
                        sec.BadgeLabel.Text = $"{secMapped} / {sec.Rows.Count} Mapped";
                        sec.BadgeLabel.AddThemeColorOverride("font_color", textSecondary);
                    }
                }
            }

            if (_lblMappingSummary != null && GodotObject.IsInstanceValid(_lblMappingSummary))
            {
                if (totalConflicts > 0)
                {
                    _lblMappingSummary.Text = $"Mapped {totalMapped} / {totalSlots} slots · ! {totalConflicts} duplicate conflict(s)";
                    _lblMappingSummary.AddThemeColorOverride("font_color", semDanger);
                }
                else if (totalMissingCore > 0)
                {
                    _lblMappingSummary.Text = $"Mapped {totalMapped} / {totalSlots} slots · ! {totalMissingCore} core bone(s) unmapped";
                    _lblMappingSummary.AddThemeColorOverride("font_color", semWarning);
                }
                else
                {
                    _lblMappingSummary.Text = $"Mapped {totalMapped} / {totalSlots} slots · All core joints unique & valid";
                    _lblMappingSummary.AddThemeColorOverride("font_color", semSuccess);
                }
            }
        }

        private void ApplyBoneMappingFilter()
        {
            string query = _txtBoneSearch?.Text?.Trim().ToLowerInvariant() ?? "";
            bool issuesOnly = _btnFilterIssuesOnly?.ButtonPressed ?? false;
            bool unmappedOnly = _btnFilterUnmappedOnly?.ButtonPressed ?? false;
            bool weightedOnly = _btnFilterWeightedOnly?.ButtonPressed ?? false;
            bool showControls = _btnFilterShowControls?.ButtonPressed ?? false;
            bool isFiltering = !string.IsNullOrEmpty(query) || issuesOnly || unmappedOnly || weightedOnly || showControls;
            var srcSkel = GetValidSourceSkeleton();

            // Count duplicates for issues filter
            var sourceUsage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var srcBone in _userBoneRemap.Values)
            {
                if (string.IsNullOrWhiteSpace(srcBone)) continue;
                sourceUsage[srcBone] = sourceUsage.GetValueOrDefault(srcBone, 0) + 1;
            }

            // Process leaf sections first, then parent sections so parent visibility includes child finger sections!
            for (int sIdx = _boneCategorySections.Count - 1; sIdx >= 0; sIdx--)
            {
                var sec = _boneCategorySections[sIdx];
                if (sec.Rows.Count == 0 || !GodotObject.IsInstanceValid(sec.Card)) continue;

                int visibleRows = 0;
                foreach (var row in sec.Rows)
                {
                    _userBoneRemap.TryGetValue(row.CanonicalName, out string mapped);
                    bool isMapped = !string.IsNullOrEmpty(mapped);
                    bool isConflict = isMapped && sourceUsage.GetValueOrDefault(mapped, 0) > 1;
                    bool isDisabled = isMapped && _disabledSourceBones.Contains(mapped);
                    bool isIssue = isConflict || isDisabled || (!isMapped && row.IsRequiredCore);

                    int srcIdx = (isMapped && srcSkel != null) ? srcSkel.FindBone(mapped) : -1;
                    bool isWeighted = srcIdx >= 0 && _lastWeightedSrcBones.Contains(srcIdx);
                    bool isCtrl = srcIdx >= 0 && _lastRigAnalysis != null && _lastRigAnalysis.IsControlOrHelper(srcIdx);

                    bool matchesQuery = string.IsNullOrEmpty(query) ||
                        row.DisplayLabel.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        row.CanonicalName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        row.TargetBoneName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(mapped) && mapped.Contains(query, StringComparison.OrdinalIgnoreCase));

                    bool matchesControlRule = showControls || !isCtrl || isMapped;
                    bool visible = matchesQuery
                        && (!issuesOnly || isIssue)
                        && (!unmappedOnly || !isMapped)
                        && (!weightedOnly || isWeighted)
                        && matchesControlRule;
                    if (GodotObject.IsInstanceValid(row.RowBox))
                    {
                        row.RowBox.Visible = visible;
                    }
                    if (visible) visibleRows++;
                }

                // If this is left_arm / right_arm, check whether nested fingers card has visible matches
                bool hasVisibleChildCard = false;
                if (sec.Key == "left_arm" && _boneCategoryByKey.TryGetValue("left_fingers", out var lf) && GodotObject.IsInstanceValid(lf.Card) && lf.Card.Visible)
                {
                    hasVisibleChildCard = true;
                }
                else if (sec.Key == "right_arm" && _boneCategoryByKey.TryGetValue("right_fingers", out var rf) && GodotObject.IsInstanceValid(rf.Card) && rf.Card.Visible)
                {
                    hasVisibleChildCard = true;
                }

                bool sectionVisible = visibleRows > 0 || hasVisibleChildCard || !isFiltering;
                sec.Card.Visible = sectionVisible;

                if (isFiltering && (visibleRows > 0 || hasVisibleChildCard))
                {
                    ApplyBoneSectionExpansion(sec, true);
                }
                else
                {
                    ApplyBoneSectionExpansion(sec, sec.UserExpanded);
                }
            }
        }

        private void OnAutoMapBonesPressed()
        {
            var srcSkel = GetValidSourceSkeleton();
            if (srcSkel == null) return;

            var autoMap = _previewHeroSkeleton != null
                ? HumanoidBoneMapper.AutoMap(srcSkel, _previewHeroSkeleton)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            _isUpdatingBoneDropdowns = true;
            try
            {
                _userBoneRemap.Clear();
                foreach (var entry in _boneRowsByCanonical.Values)
                {
                    string matched = "";
                    if (_previewHeroSkeleton != null)
                    {
                        autoMap.TryGetValue(entry.TargetBoneName, out matched);
                    }
                    else
                    {
                        var def = HumanoidBoneMapper.Definitions.FirstOrDefault(d => d.CanonicalName == entry.CanonicalName);
                        if (def != null) matched = HumanoidBoneMapper.ResolveSourceBone(srcSkel, def);
                    }
                    matched ??= "";
                    _autoMappedSnapshot[entry.CanonicalName] = matched;
                    SelectBoneDropdownBySourceName(entry.BoneDropdown, srcSkel, matched);
                    if (!string.IsNullOrEmpty(matched))
                    {
                        _userBoneRemap[entry.CanonicalName] = matched;
                        entry.BoneDropdown.TooltipText = FormatSourceBoneTooltip(matched, srcSkel);
                    }
                    else
                    {
                        entry.BoneDropdown.TooltipText = "Unmapped (auto-collapses to parent bone)";
                    }
                }
            }
            finally
            {
                _isUpdatingBoneDropdowns = false;
            }

            UpdateBoneMappingValidationBadges();
            ApplyBoneMappingFilter();
            TriggerRealtimeRigUpdate();
            SetBottomStatus("Auto-mapped humanoid bone hierarchy.", StatusTone.Success);
        }

        private static string TryFindMirroredSourceBone(Skeleton3D skel, string sourceBone, bool leftToRight)
        {
            if (skel == null || string.IsNullOrEmpty(sourceBone)) return null;

            string fromLower = leftToRight ? "left" : "right";
            string toLower = leftToRight ? "right" : "left";

            var candidates = new List<string>();
            void TryReplaceCaseAware(string pat, string rep)
            {
                int idx = sourceBone.IndexOf(pat, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    bool upper = char.IsUpper(sourceBone[idx + pat.Length - 1]) || (pat.Length > 1 && char.IsUpper(sourceBone[idx]));
                    string actualRep = upper ? rep.ToUpperInvariant() : rep;
                    candidates.Add(sourceBone[..idx] + actualRep + sourceBone[(idx + pat.Length)..]);
                }
            }

            TryReplaceCaseAware(leftToRight ? "_L_" : "_R_", leftToRight ? "_R_" : "_L_");
            TryReplaceCaseAware(leftToRight ? ".L_" : ".R_", leftToRight ? ".R_" : ".L_");
            TryReplaceCaseAware(leftToRight ? " L " : " R ", leftToRight ? " R " : " L ");
            TryReplaceCaseAware(fromLower, toLower);

            if (sourceBone.EndsWith(leftToRight ? "_L" : "_R", StringComparison.OrdinalIgnoreCase))
            {
                bool upper = char.IsUpper(sourceBone[^1]);
                candidates.Add(sourceBone[..^1] + (leftToRight ? (upper ? "R" : "r") : (upper ? "L" : "l")));
            }

            foreach (string cand in candidates)
            {
                for (int i = 0; i < skel.GetBoneCount(); i++)
                {
                    string name = skel.GetBoneName(i);
                    if (string.Equals(name, cand, StringComparison.OrdinalIgnoreCase)) return name;
                }
            }
            return null;
        }

        private void OnMirrorBonesPressed()
        {
            var srcSkel = GetValidSourceSkeleton();
            if (srcSkel == null) return;

            int mirroredCount = 0;
            _isUpdatingBoneDropdowns = true;
            try
            {
                foreach (var entry in _boneRowsByCanonical.Values.ToList())
                {
                    if (!entry.CanonicalName.EndsWith("_L", StringComparison.Ordinal)) continue;
                    string rightCanonical = entry.CanonicalName[..^2] + "_R";
                    if (!_boneRowsByCanonical.TryGetValue(rightCanonical, out var rightEntry)) continue;

                    _userBoneRemap.TryGetValue(entry.CanonicalName, out string leftSrc);
                    _userBoneRemap.TryGetValue(rightCanonical, out string rightSrc);

                    if (!string.IsNullOrEmpty(leftSrc))
                    {
                        string mirroredR = TryFindMirroredSourceBone(srcSkel, leftSrc, leftToRight: true);
                        if (!string.IsNullOrEmpty(mirroredR))
                        {
                            _userBoneRemap[rightCanonical] = mirroredR;
                            SelectBoneDropdownBySourceName(rightEntry.BoneDropdown, srcSkel, mirroredR);
                            rightEntry.BoneDropdown.TooltipText = FormatSourceBoneTooltip(mirroredR, srcSkel);
                            mirroredCount++;
                        }
                    }
                    else if (!string.IsNullOrEmpty(rightSrc))
                    {
                        string mirroredL = TryFindMirroredSourceBone(srcSkel, rightSrc, leftToRight: false);
                        if (!string.IsNullOrEmpty(mirroredL))
                        {
                            _userBoneRemap[entry.CanonicalName] = mirroredL;
                            SelectBoneDropdownBySourceName(entry.BoneDropdown, srcSkel, mirroredL);
                            entry.BoneDropdown.TooltipText = FormatSourceBoneTooltip(mirroredL, srcSkel);
                            mirroredCount++;
                        }
                    }
                }
            }
            finally
            {
                _isUpdatingBoneDropdowns = false;
            }

            UpdateBoneMappingValidationBadges();
            ApplyBoneMappingFilter();
            TriggerRealtimeRigUpdate();
            SetBottomStatus($"Mirrored {mirroredCount} L <-> R bone pair(s).", StatusTone.Success);
        }

        private void OnClearUnmatchedBonesPressed()
        {
            var srcSkel = GetValidSourceSkeleton();
            if (srcSkel == null) return;

            int clearedCount = 0;
            var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            _isUpdatingBoneDropdowns = true;
            try
            {
                foreach (var entry in _boneRowsByCanonical.Values)
                {
                    if (!_userBoneRemap.TryGetValue(entry.CanonicalName, out string mapped) || string.IsNullOrEmpty(mapped))
                    {
                        continue;
                    }

                    bool existsInSource = srcSkel.FindBone(mapped) >= 0;
                    _autoMappedSnapshot.TryGetValue(entry.CanonicalName, out string autoExpected);
                    bool matchesAuto = !string.IsNullOrEmpty(autoExpected) && string.Equals(autoExpected, mapped, StringComparison.OrdinalIgnoreCase);
                    bool isDuplicate = !seenSources.Add(mapped);

                    if (!existsInSource || isDuplicate || (!matchesAuto && string.IsNullOrEmpty(autoExpected)))
                    {
                        _userBoneRemap.Remove(entry.CanonicalName);
                        SelectBoneDropdownBySourceName(entry.BoneDropdown, srcSkel, "");
                        entry.BoneDropdown.TooltipText = "Unmapped (auto-collapses to parent bone)";
                        clearedCount++;
                    }
                }
            }
            finally
            {
                _isUpdatingBoneDropdowns = false;
            }

            UpdateBoneMappingValidationBadges();
            ApplyBoneMappingFilter();
            RefreshBoneTabDiffSummary();
            MarkRemapStaleAndRefresh();
            SetBottomStatus($"Cleared {clearedCount} unmatched/conflicting bone slot(s).", StatusTone.Info);
        }

        #endregion

        #region Alignment, Scaling & Math (Exact World Vertex Bounding Box)

        /// <summary>
        /// Computes exact World-Space Vertex Bounding Box for hero and model using pristine unscaled bounds,
        /// scales to match height, centers X and Z, and plants feet on the exact floor plane (Y = 0).
        /// </summary>
        private void ComputeAccLocalBoundsCenter()
        {
            _accLocalBoundsCenter = Vector3.Zero;
            if (_liveImportedScene == null) return;
            var (minV, maxV, ok) = CustomModelAutoRigger.ComputePristineModelBounds(
                _liveImportedScene, GetActiveImportedSubmeshFilter());
            if (ok)
            {
                _accLocalBoundsCenter = (minV + maxV) * 0.5f;
            }
        }

        private HashSet<string> GetActiveImportedSubmeshFilter()
        {
            if (_importedSubmeshCheckboxes.Count == 0) return null;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var chk in _importedSubmeshCheckboxes)
            {
                if (!GodotObject.IsInstanceValid(chk) || !chk.ButtonPressed) continue;
                string name = chk.HasMeta("SubmeshName") ? chk.GetMeta("SubmeshName").AsString() : chk.Text;
                if (!string.IsNullOrEmpty(name)) set.Add(name);
            }
            return set;
        }

        private List<MeshInstance3D> FilterActiveImportedMeshes(IEnumerable<MeshInstance3D> meshes)
        {
            var filter = GetActiveImportedSubmeshFilter();
            if (filter == null) return meshes.ToList();
            return meshes.Where(m => filter.Contains(m.Name.ToString())).ToList();
        }

        private void SyncPreviewModelMeshVisibility()
        {
            if (_previewModelInstance == null || !GodotObject.IsInstanceValid(_previewModelInstance)) return;
            var filter = GetActiveImportedSubmeshFilter();
            foreach (var mi in CustomModelAutoRigger.CollectMeshes(_previewModelInstance))
            {
                mi.Visible = filter == null || filter.Contains(mi.Name.ToString());
            }
        }

        private void OnImportedSubmeshesChanged()
        {
            SyncPreviewModelMeshVisibility();
            ComputeAccLocalBoundsCenter();

            var filter = GetActiveImportedSubmeshFilter();
            bool hasAnyActive = filter == null || filter.Count > 0;
            if (hasAnyActive && _liveImportedScene != null && GodotObject.IsInstanceValid(_liveImportedScene))
            {
                EnsureLandmarkPins(force: true);
                UpdateActivePinBadge();
                RefreshPinVisuals();
            }

            if (hasAnyActive && _workflowIntent == ImportWorkflowIntent.FullCharacter && !_userModifiedTransform)
            {
                AutoFitToHero();
                return;
            }

            if (hasAnyActive && GetValidSourceSkeleton() == null)
            {
                AutoCalibratePoseFromMesh(triggerRigUpdate: false);
            }

            TriggerRealtimeRigUpdate();
        }

        private string GetSelectedAccBoneName()
        {
            if (_optAccQuickBone != null && _optAccQuickBone.Selected >= 0)
            {
                string meta = _optAccQuickBone.GetItemMetadata(_optAccQuickBone.Selected).AsString();
                if (!string.IsNullOrEmpty(meta)) return meta;
            }
            if (_optRigidBone != null && _optRigidBone.Selected >= 0)
            {
                return _optRigidBone.GetItemText(_optRigidBone.Selected).Replace("★ ", "");
            }
            return "head";
        }

        private void SelectRigidBoneByName(string boneName)
        {
            if (_optRigidBone == null || string.IsNullOrEmpty(boneName)) return;
            for (int i = 0; i < _optRigidBone.ItemCount; i++)
            {
                if (_optRigidBone.GetItemText(i).Replace("★ ", "").Equals(boneName, StringComparison.OrdinalIgnoreCase))
                {
                    _optRigidBone.Select(i);
                    return;
                }
            }
        }

        private void SyncAccBoneSelectionFromRigidBone()
        {
            if (_optAccQuickBone == null || _optRigidBone == null || _optRigidBone.Selected < 0) return;
            string boneName = _optRigidBone.GetItemText(_optRigidBone.Selected).Replace("★ ", "");
            _isSyncingAccUi = true;
            try
            {
                bool found = false;
                for (int i = 0; i < _optAccQuickBone.ItemCount; i++)
                {
                    if (_optAccQuickBone.GetItemMetadata(i).AsString().Equals(boneName, StringComparison.OrdinalIgnoreCase))
                    {
                        _optAccQuickBone.Select(i);
                        found = true;
                        break;
                    }
                }
                if (!found && _chkAccAllBones != null && !_chkAccAllBones.ButtonPressed)
                {
                    _chkAccAllBones.SetPressedNoSignal(true);
                    PopulateAccQuickBonePicker();
                    for (int i = 0; i < _optAccQuickBone.ItemCount; i++)
                    {
                        if (_optAccQuickBone.GetItemMetadata(i).AsString().Equals(boneName, StringComparison.OrdinalIgnoreCase))
                        {
                            _optAccQuickBone.Select(i);
                            break;
                        }
                    }
                }
            }
            finally
            {
                _isSyncingAccUi = false;
            }
        }

        private void PopulateAccQuickBonePicker()
        {
            if (_optAccQuickBone == null || _previewHeroSkeleton == null) return;
            string currentBone = _optAccQuickBone.Selected >= 0
                ? _optAccQuickBone.GetItemMetadata(_optAccQuickBone.Selected).AsString()
                : (_optRigidBone?.Selected >= 0 ? _optRigidBone.GetItemText(_optRigidBone.Selected).Replace("★ ", "") : "head");

            _optAccQuickBone.Clear();
            var curated = new (string Label, string Bone)[]
            {
                ("★ Head (Hats / Glasses)", "head"),
                ("★ Neck / Collar", "neck_0"),
                ("★ Chest / Back (Capes / Wings)", "spine_3"),
                ("★ Mid Torso (Armor / Vest)", "spine_2"),
                ("★ Pelvis / Belt / Waist", "pelvis"),
                ("★ Right Foot / Ankle (Shoes)", "ankle_R"),
                ("★ Left Foot / Ankle (Shoes)", "ankle_L"),
                ("★ Right Toe / Ball", "ball_R"),
                ("★ Left Toe / Foot Tip", "ball_L"),
                ("★ Right Knee / Shin", "leg_lower_R"),
                ("★ Left Knee / Shin", "leg_lower_L"),
                ("★ Right Hand / Weapon", "weapon_hand_R"),
                ("★ Right Palm / Wrist", "hand_R"),
                ("★ Left Hand / Shield", "weapon_hand_L"),
                ("★ Left Palm / Wrist", "hand_L"),
                ("★ Right Forearm / Bracer", "arm_lower_R"),
                ("★ Left Forearm / Bracer", "arm_lower_L"),
                ("★ Right Upper Arm / Pauldron", "arm_upper_R"),
                ("★ Left Upper Arm / Pauldron", "arm_upper_L"),
                ("★ Left Shoulder", "clavicle_L"),
                ("★ Right Shoulder", "clavicle_R")
            };

            var addedBones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (label, bone) in curated)
            {
                int bIdx = _previewHeroSkeleton.FindBone(bone);
                if (bIdx >= 0 && addedBones.Add(bone))
                {
                    int idx = _optAccQuickBone.ItemCount;
                    _optAccQuickBone.AddItem(label, idx);
                    _optAccQuickBone.SetItemMetadata(idx, bone);
                }
            }

            if (_chkAccAllBones != null && _chkAccAllBones.ButtonPressed)
            {
                for (int b = 0; b < _previewHeroSkeleton.GetBoneCount(); b++)
                {
                    string bName = _previewHeroSkeleton.GetBoneName(b);
                    if (addedBones.Add(bName))
                    {
                        int idx = _optAccQuickBone.ItemCount;
                        _optAccQuickBone.AddItem(bName, idx);
                        _optAccQuickBone.SetItemMetadata(idx, bName);
                    }
                }
            }

            for (int i = 0; i < _optAccQuickBone.ItemCount; i++)
            {
                if (_optAccQuickBone.GetItemMetadata(i).AsString().Equals(currentBone, StringComparison.OrdinalIgnoreCase))
                {
                    _optAccQuickBone.Select(i);
                    break;
                }
            }
        }

        private void OnAccQuickBoneSelected(long index)
        {
            if (_isSyncingAccUi || _optAccQuickBone == null) return;
            string boneName = _optAccQuickBone.GetItemMetadata((int)index).AsString();
            if (string.IsNullOrEmpty(boneName)) return;
            SelectRigidBoneByName(boneName);
            ApplyPreviewHeroDeformation();
            TriggerRealtimeRigUpdate();
        }

        private void PopulateAccDeformTargetList()
        {
            if (_optDeformTarget == null) return;
            string prevMeta = _optDeformTarget.Selected >= 0 ? _optDeformTarget.GetItemMetadata(_optDeformTarget.Selected).AsString() : "";
            _optDeformTarget.Clear();

            void AddOpt(string label, string filterKey)
            {
                int idx = _optDeformTarget.ItemCount;
                _optDeformTarget.AddItem(label, idx);
                _optDeformTarget.SetItemMetadata(idx, filterKey);
            }

            AddOpt("Hair / Top of Head (Auto)", "hair");
            AddOpt("Entire Head / Face Region", "head");
            AddOpt("All Original Meshes Within Radius", "all");

            foreach (var chk in _heroKeepCheckboxes)
            {
                if (!GodotObject.IsInstanceValid(chk) || !chk.HasMeta("SubmeshName")) continue;
                string key = chk.GetMeta("SubmeshName").AsString();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    AddOpt($"Surface: {chk.Text}", key);
                }
            }

            if (!string.IsNullOrEmpty(prevMeta))
            {
                for (int i = 0; i < _optDeformTarget.ItemCount; i++)
                {
                    if (string.Equals(_optDeformTarget.GetItemMetadata(i).AsString(), prevMeta, StringComparison.OrdinalIgnoreCase))
                    {
                        _optDeformTarget.Select(i);
                        return;
                    }
                }
            }

            if (_optDeformTarget.ItemCount > 0)
            {
                string smartDefault = HasHeroDeformSurfaceMatch("hair")
                    ? "hair"
                    : (HasHeroDeformSurfaceMatch("head") ? "head" : "all");
                for (int i = 0; i < _optDeformTarget.ItemCount; i++)
                {
                    if (string.Equals(_optDeformTarget.GetItemMetadata(i).AsString(), smartDefault, StringComparison.OrdinalIgnoreCase))
                    {
                        _optDeformTarget.Select(i);
                        return;
                    }
                }
                _optDeformTarget.Select(0);
            }
        }

        private bool HasHeroDeformSurfaceMatch(string filter)
        {
            if (_previewHeroClone == null || !GodotObject.IsInstanceValid(_previewHeroClone)) return false;
            var stack = new Stack<Node>();
            stack.Push(_previewHeroClone);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n is MeshInstance3D mi && mi.Mesh != null)
                {
                    if (_previewGhostMeshes.Count > 0 && !_previewGhostMeshes.Contains(mi)) continue;
                    int sc = mi.Mesh.GetSurfaceCount();
                    for (int s = 0; s < sc; s++)
                    {
                        if (CustomModelAutoRigger.DoesHeroSurfaceMatchDeformFilter(mi, s, filter)) return true;
                    }
                }
                foreach (Node ch in n.GetChildren()) stack.Push(ch);
            }
            return false;
        }

        private void ApplyPreviewHeroDeformation()
        {
            if (_previewHeroClone == null || !GodotObject.IsInstanceValid(_previewHeroClone)) return;
            var opts = GatherRiggingOptions();
            CustomModelAutoRigger.RecomputeHeroDeformations(_previewHeroClone, opts);
            ApplyGhostMaterialToClone(_previewHeroClone);
            if (_deformSurfaceCaches.Count > 0)
            {
                RefreshHeroDeformSurfaceCacheVertices();
            }
        }

        private void SetAccessoryIntent(CustomModelAutoRigger.AccessoryIntent intent, bool triggerUpdate = true)
        {
            if (intent != CustomModelAutoRigger.AccessoryIntent.AddAccessory &&
                intent != CustomModelAutoRigger.AccessoryIntent.ReplacePart)
            {
                intent = CustomModelAutoRigger.AccessoryIntent.AddAccessory;
            }
            _accessoryIntent = intent;
            bool isReplace = intent == CustomModelAutoRigger.AccessoryIntent.ReplacePart;

            if (_btnAccIntentAdd != null) _btnAccIntentAdd.SetPressedNoSignal(!isReplace);
            if (_btnAccIntentReplace != null) _btnAccIntentReplace.SetPressedNoSignal(isReplace);
            if (_boxAccReplacePanel != null) _boxAccReplacePanel.Visible = isReplace;

            _isSyncingAccUi = true;
            try
            {
                if (!isReplace)
                {
                    foreach (var chk in _heroKeepCheckboxes)
                    {
                        if (GodotObject.IsInstanceValid(chk)) chk.SetPressedNoSignal(true);
                    }
                    foreach (var repChk in _accReplaceCheckboxes)
                    {
                        if (GodotObject.IsInstanceValid(repChk)) repChk.SetPressedNoSignal(false);
                    }
                }
                else
                {
                    var repSet = new HashSet<string>(
                        _accReplaceCheckboxes.Where(c => GodotObject.IsInstanceValid(c) && c.ButtonPressed && c.HasMeta("SubmeshName"))
                                             .Select(c => c.GetMeta("SubmeshName").AsString()),
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var keepChk in _heroKeepCheckboxes)
                    {
                        if (GodotObject.IsInstanceValid(keepChk) && keepChk.HasMeta("SubmeshName"))
                        {
                            string sName = keepChk.GetMeta("SubmeshName").AsString();
                            keepChk.SetPressedNoSignal(!repSet.Contains(sName));
                        }
                    }
                }
            }
            finally
            {
                _isSyncingAccUi = false;
            }

            UpdatePreviewHeroKeepAndGhostMaterials();
            if (triggerUpdate) TriggerRealtimeRigUpdate();
        }

        private void PopulateAccessoryReplaceList()
        {
            if (_boxAccReplaceList == null) return;
            foreach (var c in _accReplaceCheckboxes) c.QueueFree();
            _accReplaceCheckboxes.Clear();
            foreach (Node child in _boxAccReplaceList.GetChildren()) child.QueueFree();

            foreach (var keepChk in _heroKeepCheckboxes)
            {
                if (!GodotObject.IsInstanceValid(keepChk) || !keepChk.HasMeta("SubmeshName")) continue;
                string key = keepChk.GetMeta("SubmeshName").AsString();
                var repChk = new CheckBox
                {
                    Text = keepChk.Text,
                    ButtonPressed = !keepChk.ButtonPressed,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };
                repChk.SetMeta("SubmeshName", key);
                repChk.Toggled += (replacePressed) =>
                {
                    if (_isSyncingAccUi) return;
                    _isSyncingAccUi = true;
                    try
                    {
                        if (GodotObject.IsInstanceValid(keepChk))
                        {
                            keepChk.SetPressedNoSignal(!replacePressed);
                        }
                    }
                    finally { _isSyncingAccUi = false; }
                    UpdatePreviewHeroKeepAndGhostMaterials();
                };
                _accReplaceCheckboxes.Add(repChk);
                _boxAccReplaceList.AddChild(repChk);
            }
        }

        private void SnapAccessoryToSelectedBone()
        {
            if (_liveImportedScene == null || _previewHeroSkeleton == null || _previewModelRoot == null) return;
            _previewHeroSkeleton.ResetBonePoses();
            string boneName = GetSelectedAccBoneName();
            Transform3D fit = CustomModelAutoRigger.ComputeAutoFitTransformToBone(
                _previewModelInstance ?? _liveImportedScene, _previewHeroSkeleton, boneName, GetActiveImportedSubmeshFilter());

            Vector3 scaleVec = fit.Basis.Scale;
            float scale = MathF.Max(0.0005f, scaleVec.X);
            Vector3 eulerRad = fit.Basis.Orthonormalized().GetEuler();

            _spinScale.SetValueNoSignal(Math.Round(scale, 4));
            _sliderScale.SetValueNoSignal(Math.Round(scale, 4));
            _spinPosX.SetValueNoSignal(Math.Round(fit.Origin.X, 3));
            _spinPosY.SetValueNoSignal(Math.Round(fit.Origin.Y, 3));
            _spinPosZ.SetValueNoSignal(Math.Round(fit.Origin.Z, 3));
            _spinRotPitch.SetValueNoSignal(Math.Round(Mathf.RadToDeg(eulerRad.X), 1));
            _spinRotYaw.SetValueNoSignal(Math.Round(Mathf.RadToDeg(eulerRad.Y), 1));
            _spinRotRoll.SetValueNoSignal(Math.Round(Mathf.RadToDeg(eulerRad.Z), 1));

            ApplyTransformToPreviewModel(immediate: true);

            int bIdx = _previewHeroSkeleton.FindBone(boneName);
            if (bIdx < 0) bIdx = _previewHeroSkeleton.FindBone("head");
            if (bIdx >= 0)
            {
                Vector3 boneOrigin = _previewHeroSkeleton.Transform * _previewHeroSkeleton.GetBoneGlobalRest(bIdx).Origin;
                _orbitTarget = boneOrigin;
                bool closeZoom = boneName.Contains("head", StringComparison.OrdinalIgnoreCase) ||
                                 boneName.Contains("ankle", StringComparison.OrdinalIgnoreCase) ||
                                 boneName.Contains("foot", StringComparison.OrdinalIgnoreCase) ||
                                 boneName.Contains("ball", StringComparison.OrdinalIgnoreCase) ||
                                 boneName.Contains("hand", StringComparison.OrdinalIgnoreCase);
                _orbitDist = closeZoom ? 1.05f : 1.75f;
                _orbitPitch = 0.14f;
                _orbitYaw = 0.0f;
                UpdateCamera();
            }

            SetBottomStatus($"Auto-fitted accessory to bone '{boneName}' (Scale={scale:F3}x).", StatusTone.Success);
        }

        public void AutoFitToHero()
        {
            if (_previewHeroClone == null || _previewModelRoot == null || _previewModelInstance == null) return;

            // Ensure reference hero clone is in bind pose
            _previewHeroSkeleton?.ResetBonePoses();

            var activeFilter = GetActiveImportedSubmeshFilter();
            var (heroMin, heroMax, heroOk) = CustomModelAutoRigger.ComputeAuthenticHeroBounds(_previewHeroClone);
            var (modelMin, modelMax, modelOk) = CustomModelAutoRigger.ComputePristineModelBounds(_previewModelInstance, activeFilter);

            float heroHeight = heroOk ? (heroMax.Y - Math.Max(0.0f, heroMin.Y)) : 2.45f;
            float modelHeight = modelOk ? (modelMax.Y - modelMin.Y) : 1.0f;

            if (modelHeight < 0.0001f)
            {
                _spinScale.SetValueNoSignal(1.0);
                _sliderScale.SetValueNoSignal(1.0);
                _spinPosX.SetValueNoSignal(0.0);
                _spinPosY.SetValueNoSignal(0.0);
                _spinPosZ.SetValueNoSignal(0.0);
                ApplyTransformToPreviewModel(immediate: true);
                return;
            }

            // 1. Calculate Uniform Scale
            float scale = heroHeight / modelHeight;
            double roundedScale = Math.Round(scale, 4);
            _spinScale.SetValueNoSignal(roundedScale);
            _sliderScale.SetValueNoSignal(roundedScale);

            // 2. Default Yaw = 0° so imported models keep their authored forward orientation without manual rotation
            _spinRotPitch.SetValueNoSignal(0.0);
            _spinRotYaw.SetValueNoSignal(0.0);
            _spinRotRoll.SetValueNoSignal(0.0);

            // 3. Calculate Center X and Z (under Yaw = 0°, local X/Z map directly without sign inversion)
            Vector3 heroCenter = (heroMin + heroMax) * 0.5f;
            Vector3 modelCenter = (modelMin + modelMax) * 0.5f;

            float posX = heroCenter.X - (modelCenter.X * scale);
            float posZ = heroCenter.Z - (modelCenter.Z * scale);

            // 4. Plant feet strictly on the ground floor plane (Y = 0.0f)
            float posY = 0.0f - (modelMin.Y * scale);

            _spinPosX.SetValueNoSignal(Math.Round(posX, 3));
            _spinPosY.SetValueNoSignal(Math.Round(posY, 3));
            _spinPosZ.SetValueNoSignal(Math.Round(posZ, 3));

            ApplyTransformToPreviewModel(immediate: true);

            // 5. Frame Camera on the Hero and Model
            _orbitTarget = new Vector3(heroCenter.X, (heroMin.Y + heroMax.Y) * 0.5f, heroCenter.Z);
            _orbitDist = Mathf.Max(heroHeight * 1.65f, 2.4f);
            _orbitPitch = 0.12f;
            _orbitYaw = 0.0f;
            UpdateCamera();

            SetBottomStatus($"Auto-Fit applied: Scale={scale:F3}x | Pos=({posX:F2}, {posY:F2}, {posZ:F2}) | Yaw=0°", StatusTone.Success);
            if (GetValidSourceSkeleton() == null)
            {
                AutoCalibratePoseFromMesh(triggerRigUpdate: false);
            }
        }

        private void SnapFeetToFloor()
        {
            if (_previewModelInstance == null || _previewHeroClone == null) return;
            var (heroMin, _, _) = CustomModelAutoRigger.ComputeAuthenticHeroBounds(_previewHeroClone);
            var (modelMin, _, _) = CustomModelAutoRigger.ComputePristineModelBounds(_previewModelInstance, GetActiveImportedSubmeshFilter());

            float scale = (float)_spinScale.Value;
            float posY = 0.0f - (modelMin.Y * scale);
            _spinPosY.SetValueNoSignal(Math.Round(posY, 3));
            ApplyTransformToPreviewModel(immediate: true);
        }

        private Vector3 GetScaleXyzVector()
        {
            return new Vector3(
                MathF.Max(0.01f, (float)(_spinScaleX?.Value ?? 1.0)),
                MathF.Max(0.01f, (float)(_spinScaleY?.Value ?? 1.0)),
                MathF.Max(0.01f, (float)(_spinScaleZ?.Value ?? 1.0)));
        }

        private void ResetTransform()
        {
            _spinScale.SetValueNoSignal(1.0);
            _sliderScale.SetValueNoSignal(1.0);
            _spinScaleX?.SetValueNoSignal(1.0);
            _spinScaleY?.SetValueNoSignal(1.0);
            _spinScaleZ?.SetValueNoSignal(1.0);
            _btnFlipX?.SetPressedNoSignal(false);
            _btnFlipY?.SetPressedNoSignal(false);
            _btnFlipZ?.SetPressedNoSignal(false);
            ApplyPreviewGeometryFlip();
            _spinPosX.SetValueNoSignal(0.0);
            _spinPosY.SetValueNoSignal(0.0);
            _spinPosZ.SetValueNoSignal(0.0);
            _spinRotPitch.SetValueNoSignal(0.0);
            _spinRotYaw.SetValueNoSignal(0.0);
            _spinRotRoll.SetValueNoSignal(0.0);
            ApplyTransformToPreviewModel(immediate: true);
        }

        private void OnGeometryFlipButtonToggled()
        {
            _userModifiedTransform = true;
            ApplyPreviewGeometryFlip();
            ApplyTransformToPreviewModel(immediate: true);
        }

        private void ApplyPreviewGeometryFlip()
        {
            _previewFlipRestore?.Invoke();
            _previewFlipRestore = null;

            if (_previewModelInstance == null || !GodotObject.IsInstanceValid(_previewModelInstance))
                return;

            bool fx = _btnFlipX?.ButtonPressed ?? false;
            bool fy = _btnFlipY?.ButtonPressed ?? false;
            bool fz = _btnFlipZ?.ButtonPressed ?? false;
            if (!fx && !fy && !fz) return;

            _previewFlipRestore = CustomModelAutoRigger.ApplyGeometryFlipToScene(
                _previewModelInstance,
                fx,
                fy,
                fz,
                GetActiveImportedSubmeshFilter(),
                flattenSkinnedToRest: true);
        }

        private void MirrorAccessoryLeftRight()
        {
            _userModifiedTransform = true;

            // 1. Toggle X geometry flip so L sneaker <-> R sneaker
            bool nextFlipX = !(_btnFlipX?.ButtonPressed ?? false);
            _btnFlipX?.SetPressedNoSignal(nextFlipX);
            ApplyPreviewGeometryFlip();

            // 2. Swap selected attachment bone L <-> R if symmetrical
            string curBone = GetSelectedAccBoneName();
            string mirroredBone = CustomModelAutoRigger.MirrorBoneNameLtoR(curBone);
            bool boneSwapped = false;
            if (!string.IsNullOrEmpty(mirroredBone) && _previewHeroSkeleton != null && _previewHeroSkeleton.FindBone(mirroredBone) >= 0)
            {
                SelectRigidBoneByName(mirroredBone);
                SyncAccBoneSelectionFromRigidBone();
                boneSwapped = true;
            }

            // 3. Mirror position & rotation across hero X = 0 around model's world center
            Transform3D curXf = BuildCurrentAlignedTransform();
            Vector3 worldCenter = curXf * _accLocalBoundsCenter;
            Vector3 mirroredWorldCenter = new Vector3(-worldCenter.X, worldCenter.Y, worldCenter.Z);

            float pitch = (float)(_spinRotPitch?.Value ?? 0.0);
            float yaw = (float)(_spinRotYaw?.Value ?? 0.0);
            float roll = (float)(_spinRotRoll?.Value ?? 0.0);
            _spinRotPitch?.SetValueNoSignal(pitch);
            _spinRotYaw?.SetValueNoSignal(-yaw);
            _spinRotRoll?.SetValueNoSignal(-roll);

            // Recompute Basis after negated Yaw/Roll so even non-origin local centers land cleanly at (-Wx, Wy, Wz)
            Transform3D newRotScale = BuildCurrentAlignedTransform();
            Vector3 newOrigin = mirroredWorldCenter - (newRotScale.Basis * _accLocalBoundsCenter);
            _spinPosX?.SetValueNoSignal(Math.Round(newOrigin.X, 4));
            _spinPosY?.SetValueNoSignal(Math.Round(newOrigin.Y, 4));
            _spinPosZ?.SetValueNoSignal(Math.Round(newOrigin.Z, 4));

            ApplyPreviewHeroDeformation();
            ApplyTransformToPreviewModel(immediate: true);

            string boneNote = boneSwapped ? $"bone '{curBone}' -> '{mirroredBone}'" : $"bone '{curBone}' kept";
            SetBottomStatus($"Mirrored L <-> R: FlipX={(nextFlipX ? "ON" : "OFF")}, {boneNote}, X mirrored.", StatusTone.Success);
        }


        private void ApplyScaleChangeKeepingCenter(bool immediate = false)
        {
            if (!_suppressScalePivotCompensation && _previewModelRoot != null && _accLocalBoundsCenter != Vector3.Zero)
            {
                Vector3 oldWorldCenter = _previewModelRoot.Transform * _accLocalBoundsCenter;
                Transform3D newBasisOnly = BuildCurrentAlignedTransform();
                Vector3 compensatedOrigin = oldWorldCenter - (newBasisOnly.Basis * _accLocalBoundsCenter);
                _spinPosX?.SetValueNoSignal(compensatedOrigin.X);
                _spinPosY?.SetValueNoSignal(compensatedOrigin.Y);
                _spinPosZ?.SetValueNoSignal(compensatedOrigin.Z);
            }
            ApplyTransformToPreviewModel(immediate);
        }

        private void ApplyTransformToPreviewModel(bool immediate = false)
        {
            if (_previewModelRoot == null) return;

            Transform3D t = BuildCurrentAlignedTransform();

            _previewModelRoot.Transform = t;
            int gTarget = _optGizmoTarget?.Selected >= 0 ? _optGizmoTarget.GetItemId(_optGizmoTarget.Selected) : 0;
            if (gTarget == 3 && !_isGizmoUpdatingFromUI && !_isGizmoDragging && !(_previewGizmo?.Editing ?? false))
            {
                if (!_isGizmoDragging && !(_previewGizmo?.Editing ?? false))
                {
                    UpdateGizmoTargetTransform();
                }
            }
            if (immediate)
            {
                TriggerRealtimeRigUpdate();
                return;
            }

            if (_previewRiggedContainer != null && GodotObject.IsInstanceValid(_previewRiggedContainer) && _hasBakedPreviewTransform &&
                Mathf.Abs(_bakedPreviewTransform.Basis.Determinant()) > 1e-8f)
            {
                // Move/scale the rigged preview node on GPU immediately while debouncing full KNN/weight re-bake
                _previewRiggedContainer.Transform = t * _bakedPreviewTransform.AffineInverse();
            }
            ScheduleDebouncedLiveRigUpdate();
        }

        private void AutoCalibratePoseFromMesh(bool triggerRigUpdate = true)
        {
            if (_previewHeroSkeleton == null || _previewModelInstance == null) return;

            var cal = CustomModelAutoRigger.AutoEstimateUnriggedPoseCalibration(
                _liveImportedScene ?? _previewModelInstance,
                _previewHeroSkeleton,
                BuildCurrentAlignedTransform(),
                GetSelectedImportedSubmeshNames());

            _isUpdatingPoseSliders = true;
            try
            {
                if (_sliderArmAngle != null) { _sliderArmAngle.Value = cal.ArmAngleDeg; if (_lblArmAngleVal != null) _lblArmAngleVal.Text = $"{cal.ArmAngleDeg:0}°"; }
                if (_sliderArmForward != null) { _sliderArmForward.Value = cal.ArmForwardDeg; if (_lblArmForwardVal != null) _lblArmForwardVal.Text = $"{cal.ArmForwardDeg:0}°"; }
                if (_sliderElbowStraight != null) { _sliderElbowStraight.Value = cal.ElbowStraightenPct; if (_lblElbowStraightVal != null) _lblElbowStraightVal.Text = $"{cal.ElbowStraightenPct:0}%"; }
                if (_sliderLegSpread != null) { _sliderLegSpread.Value = cal.LegSpreadDeg; if (_lblLegSpreadVal != null) _lblLegSpreadVal.Text = $"{cal.LegSpreadDeg:0}°"; }

                bool nonTrivial = MathF.Abs(cal.ArmAngleDeg) > 4f || MathF.Abs(cal.ArmForwardDeg) > 4f ||
                                  cal.ElbowStraightenPct > 15f || MathF.Abs(cal.LegSpreadDeg) > 4f;
                if (nonTrivial && _optPosePreset != null)
                {
                    SelectPosePresetById((int)CustomModelAutoRigger.PosePreset.APoseCustomAngle);
                    if (_boxCustomArmAngle != null) _boxCustomArmAngle.Visible = true;
                }
            }
            finally
            {
                _isUpdatingPoseSliders = false;
            }

            ApplyCalibrationPoseToGhostSkeleton();
            SetBottomStatus($"Pose Auto-Fit: Arm={cal.ArmAngleDeg:0}°, Fwd={cal.ArmForwardDeg:0}°, Elbow={cal.ElbowStraightenPct:0}%, Leg={cal.LegSpreadDeg:0}°", StatusTone.Success);
            if (triggerRigUpdate) TriggerRealtimeRigUpdate();
        }

        private void ResetPoseCalibration()
        {
            _isUpdatingPoseSliders = true;
            try
            {
                if (_sliderArmAngle != null) { _sliderArmAngle.Value = 0; if (_lblArmAngleVal != null) _lblArmAngleVal.Text = "0°"; }
                if (_sliderArmForward != null) { _sliderArmForward.Value = 0; if (_lblArmForwardVal != null) _lblArmForwardVal.Text = "0°"; }
                if (_sliderElbowStraight != null) { _sliderElbowStraight.Value = 0; if (_lblElbowStraightVal != null) _lblElbowStraightVal.Text = "0%"; }
                if (_sliderLegSpread != null) { _sliderLegSpread.Value = 0; if (_lblLegSpreadVal != null) _lblLegSpreadVal.Text = "0°"; }
                SelectPosePresetById((int)CustomModelAutoRigger.PosePreset.MatchHeroBindPose);
                if (_boxCustomArmAngle != null) _boxCustomArmAngle.Visible = false;
            }
            finally
            {
                _isUpdatingPoseSliders = false;
            }

            ApplyCalibrationPoseToGhostSkeleton();
            SetBottomStatus("Reference hero pose reset to bind pose.", StatusTone.Info);
            TriggerRealtimeRigUpdate();
        }

        private void SelectPosePresetById(int presetId)
        {
            if (_optPosePreset == null) return;
            for (int i = 0; i < _optPosePreset.ItemCount; i++)
            {
                if (_optPosePreset.GetItemId(i) == presetId)
                {
                    _optPosePreset.Select(i);
                    break;
                }
            }
        }

        #region Landmark Auto-Rig Pin Editor & Weight Paint

        // Pins are stored in imported-model space; visuals are mapped through the current aligned transform.
        private Dictionary<string, Vector3> _landmarkPins = new();
        private readonly Dictionary<string, MeshInstance3D> _pinSpheres = new();
        private MeshInstance3D _pinSkeletonLines;
        private Control _boxLandmarkPins;
        private Label _lblActivePinBadge;
        private CheckBox _chkMirrorPins;
        private CheckBox _chkShowPins;
        private string _draggingPin;
        private string _selectedPin;
        private string _hoveredPin;
        private readonly HashSet<string> _selectedPins = new(StringComparer.Ordinal);
        private Button _btnSelectAllPins;
        private bool _userMovedLandmarkPins = false;
        private bool _pinsDirtyNeedsRigRebuild = false;
        private string _lastPinBadgeStateKey = "";

        // Interactive Bone Weight Paint state
        private Control _boxWeightPaintSection;
        private VBoxContainer _boxWeightPaintControls;
        private Button _btnWeightPaintMode;
        private OptionButton _optPaintBone;
        private OptionButton _optPaintMode;
        private HSlider _sliderPaintRadius;
        private Label _lblPaintRadiusVal;
        private HSlider _sliderPaintStrength;
        private Label _lblPaintStrengthVal;
        private CheckBox _chkPaintMirror;
        private CheckBox _chkPaintWireframe;
        private Button _btnPaintUndo;
        private Button _btnPaintRedo;
        private Button _btnResetBoneWeights;
        private Label _lblPaintHistoryStatus;
        private Label _lblPaintStatus;
        // Floating 3D Viewport Weight-Paint HUD & Searchable Filtered Bone List
        private PanelContainer _weightPaintViewportHud;
        private Label _lblHudActiveBone;
        private readonly Button[] _hudBrushModeBtns = new Button[4];
        private HSlider _hudSliderRadius;
        private Label _hudLblRadius;
        private HSlider _hudSliderStrength;
        private Label _hudLblStrength;
        private Button _hudBtnMirror;
        private Button _hudBtnUndo;
        private Button _hudBtnRedo;
        private LineEdit _txtPaintBoneSearch;
        private int _paintBoneFilterMode = 0; // 0 = Weighted on Mesh, 1 = Physics / Imported, 2 = All Bones
        private readonly Button[] _paintBoneFilterBtns = new Button[3];
        private VBoxContainer _boxPaintBoneList;
        private readonly List<(string Name, int SkelIdx, int VertCount, bool IsPhysics)> _paintBoneCatalog = new();
        private readonly Dictionary<string, CustomModelAutoRigger.SkinWeightOverride> _customSkinOverrides = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CustomModelAutoRigger.SkinWeightOverride> _baselineSkinOverrides = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<CustomWeightPaintEditor.WeightHistoryEntry> _weightUndoStack = new();
        private readonly List<CustomWeightPaintEditor.WeightHistoryEntry> _weightRedoStack = new();
        private CustomWeightPaintEditor.WeightHistoryEntry _pendingStrokeSnapshot;
        private bool _strokeDirty = false;
        private List<CustomWeightPaintEditor.SurfaceState> _paintSurfaces = new();
        private StandardMaterial3D _weightHeatmapMaterial;
        private MeshInstance3D _paintBrushRing;
        private bool _isPaintingWeights = false;

        private static readonly (string A, string B)[] PinConnections =
        {
            ("Pelvis", "Chest"), ("Chest", "Neck"), ("Neck", "Head"),
            ("Neck", "ShoulderL"), ("ShoulderL", "ElbowL"), ("ElbowL", "WristL"),
            ("Neck", "ShoulderR"), ("ShoulderR", "ElbowR"), ("ElbowR", "WristR"),
            ("Pelvis", "HipL"), ("HipL", "KneeL"), ("KneeL", "AnkleL"),
            ("Pelvis", "HipR"), ("HipR", "KneeR"), ("KneeR", "AnkleR")
        };

        private bool IsLandmarkMethod()
        {
            return _optRiggingMethod != null && _optRiggingMethod.Selected >= 0 &&
                   (CustomModelAutoRigger.RiggingMethod)_optRiggingMethod.GetItemId(_optRiggingMethod.Selected) == CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig;
        }

        private bool IsKnnMethod()
        {
            return _optRiggingMethod != null && _optRiggingMethod.Selected >= 0 &&
                   (CustomModelAutoRigger.RiggingMethod)_optRiggingMethod.GetItemId(_optRiggingMethod.Selected) == CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection;
        }

        private bool IsTab2Active() => _rightTabs != null && _rightTabs.CurrentTab == 1;
        private bool IsBindPoseView() => _optTestAnim == null || _optTestAnim.Selected <= 0;
        private bool IsTab3PhysicsPaintHostActive() =>
            _rightTabs != null && _rightTabs.CurrentTab == 2 &&
            _boxSubViewPhysics != null && _boxSubViewPhysics.Visible &&
            _boxPhysicsChainsPane != null && _boxPhysicsChainsPane.Visible &&
            _boxTab3WeightPaintHost != null && _boxWeightPaintSection != null &&
            _boxWeightPaintSection.GetParent() == _boxTab3WeightPaintHost;
        private bool IsWeightPaintActive() =>
            (_btnWeightPaintMode?.ButtonPressed ?? false) &&
            (IsTab2Active() || IsTab3PhysicsPaintHostActive());
        private bool IsDeformBrushActive() => _btnDeformBrushToggle != null && _btnDeformBrushToggle.ButtonPressed && (_chkDeformEnabled?.ButtonPressed ?? false);
        private bool IsPinEditActive() => IsLandmarkMethod() && IsTab2Active() && IsBindPoseView() && !IsWeightPaintActive() && !IsDeformBrushActive();

        private void HostWeightPaintSectionIn(Control targetHost)
        {
            if (_boxWeightPaintSection == null || targetHost == null || !GodotObject.IsInstanceValid(targetHost)) return;
            var curParent = _boxWeightPaintSection.GetParent();
            if (curParent != targetHost)
            {
                curParent?.RemoveChild(_boxWeightPaintSection);
                targetHost.AddChild(_boxWeightPaintSection);
                if (targetHost == _boxTab2WeightPaintHost && _boxWeightPaintSection.GetIndex() > 0)
                    targetHost.MoveChild(_boxWeightPaintSection, 0);
            }
            if (targetHost == _boxTab3WeightPaintHost)
            {
                _boxWeightPaintSection.Visible = true;
                _boxTab3WeightPaintHost.Visible = true;
                if (_boxWeightPaintControls != null && (_btnWeightPaintMode?.ButtonPressed ?? false))
                    _boxWeightPaintControls.Visible = true;
            }
            else if (_boxTab3WeightPaintHost != null)
            {
                _boxTab3WeightPaintHost.Visible = false;
            }
        }

        private void SetHeroGhostEnabled(bool enabled)
        {
            _showGhost = enabled;
            if (_btnToggleGhost != null && _btnToggleGhost.ButtonPressed != enabled)
            {
                _btnToggleGhost.SetPressedNoSignal(enabled);
                SetButtonIcon(_btnToggleGhost, enabled ? "res://assets/at-icons/eye.svg" : "res://assets/at-icons/eye_closed.svg", 22);
            }
            UpdatePreviewHeroKeepAndGhostMaterials();
        }

        private void OnRightTabChanged(long tabIdx)
        {
            if (tabIdx == 1)
            {
                HostWeightPaintSectionIn(_boxTab2WeightPaintHost);
                UpdateRiggingMethodPanelVisibility();
                UpdateAccessoryRigVisibility();
            }
            else if (tabIdx == 2)
            {
                if (_boxSubViewPhysics != null && _boxSubViewPhysics.Visible && _boxPhysicsChainsPane != null && _boxPhysicsChainsPane.Visible && (_btnWeightPaintMode?.ButtonPressed ?? false))
                {
                    HostWeightPaintSectionIn(_boxTab3WeightPaintHost);
                }
            }
            else if (_btnWeightPaintMode != null && _btnWeightPaintMode.ButtonPressed)
            {
                _btnWeightPaintMode.ButtonPressed = false;
            }
            _draggingPin = null;
            _hoveredPin = null;
            if (tabIdx != 1 && _pinsDirtyNeedsRigRebuild)
            {
                ExecuteLiveRigUpdate();
            }
            ApplyLandmarkPreviewState();
            if (IsWeightPaintActive()) RefreshWeightPaintHeatmap();
        }

        private static string FormatPinDisplayName(string pin)
        {
            if (string.IsNullOrEmpty(pin)) return "";
            if (pin.EndsWith("L") && pin != "Pelvis") return pin.Substring(0, pin.Length - 1) + " L";
            if (pin.EndsWith("R")) return pin.Substring(0, pin.Length - 1) + " R";
            return pin;
        }

        private static string LandmarkPinToDeadlockBone(string pin)
        {
            return pin switch
            {
                "Head" => "head",
                "Neck" => "neck_0",
                "Chest" => "spine_2",
                "Pelvis" => "pelvis",
                "ShoulderL" => "arm_upper_L",
                "ShoulderR" => "arm_upper_R",
                "ElbowL" => "arm_lower_L",
                "ElbowR" => "arm_lower_R",
                "WristL" => "hand_L",
                "WristR" => "hand_R",
                "HipL" => "leg_upper_L",
                "HipR" => "leg_upper_R",
                "KneeL" => "leg_lower_L",
                "KneeR" => "leg_lower_R",
                "AnkleL" => "ankle_L",
                "AnkleR" => "ankle_R",
                _ => pin
            };
        }

        private bool AreAllPinsSelected() => _selectedPins.Count >= CustomLandmarkWeightSolver.PinNames.Length;

        private void UpdateSelectAllPinsButtonText()
        {
            if (_btnSelectAllPins == null) return;
            bool all = AreAllPinsSelected();
            _btnSelectAllPins.SetPressedNoSignal(all);
            _btnSelectAllPins.Text = all ? "Deselect All (16)" : "Select All Bones";
        }

        private void SelectAllPins()
        {
            _selectedPins.Clear();
            foreach (string pin in CustomLandmarkWeightSolver.PinNames)
            {
                _selectedPins.Add(pin);
            }
            if (string.IsNullOrEmpty(_selectedPin))
            {
                _selectedPin = "Pelvis";
            }
            UpdateSelectAllPinsButtonText();
            UpdateActivePinBadge();
            RefreshPinVisuals();
        }

        private void ClearPinSelection()
        {
            _selectedPins.Clear();
            _selectedPin = null;
            UpdateSelectAllPinsButtonText();
            UpdateActivePinBadge();
            RefreshPinVisuals();
        }

        private void ToggleSelectAllPins()
        {
            if (AreAllPinsSelected()) ClearPinSelection();
            else SelectAllPins();
        }

        private void TogglePinInSelection(string pinName, bool includeMirror)
        {
            if (string.IsNullOrEmpty(pinName)) return;
            if (_selectedPins.Count == 0 && !string.IsNullOrEmpty(_selectedPin) && _selectedPin != pinName)
            {
                _selectedPins.Add(_selectedPin);
            }

            bool add = !_selectedPins.Contains(pinName);
            if (add) _selectedPins.Add(pinName);
            else _selectedPins.Remove(pinName);

            if (includeMirror)
            {
                string mir = CustomLandmarkWeightSolver.MirrorName(pinName);
                if (!string.IsNullOrEmpty(mir))
                {
                    if (add) _selectedPins.Add(mir);
                    else _selectedPins.Remove(mir);
                }
            }

            _selectedPin = add ? pinName : _selectedPins.FirstOrDefault();
            UpdateSelectAllPinsButtonText();
            UpdateActivePinBadge();
            RefreshPinVisuals();
        }

        private void UpdateActivePinBadge()
        {
            string activeKey = _draggingPin ?? _hoveredPin ?? _selectedPin ?? "";
            string stateKey = $"{activeKey}|{_selectedPins.Count}|{(_chkMirrorPins?.ButtonPressed ?? true)}";
            if (stateKey == _lastPinBadgeStateKey) return;
            _lastPinBadgeStateKey = stateKey;

            UpdateSelectAllPinsButtonText();
            if (_lblActivePinBadge == null) return;
            if (AreAllPinsSelected())
            {
                _lblActivePinBadge.Text = "Editing: ALL 16 Joint Pins (drag any pin to move all together)";
                return;
            }
            if (_selectedPins.Count > 1)
            {
                _lblActivePinBadge.Text = $"Editing: {_selectedPins.Count} Joint Pins selected (drag any to move together, Shift+Click to add/remove)";
                return;
            }
            string active = _draggingPin ?? _hoveredPin ?? _selectedPin;
            if (string.IsNullOrEmpty(active))
            {
                _lblActivePinBadge.Text = "Active Pin: None (drag a pin, or Shift+Click to multi-select)";
                return;
            }
            string mirror = CustomLandmarkWeightSolver.MirrorName(active);
            bool mirrored = mirror != null && (_chkMirrorPins?.ButtonPressed ?? true);
            string b1 = LandmarkPinToDeadlockBone(active);
            if (mirrored)
            {
                string b2 = LandmarkPinToDeadlockBone(mirror);
                _lblActivePinBadge.Text = $"Editing: {FormatPinDisplayName(active)}  <->  Mirror: {FormatPinDisplayName(mirror)}   ({b1} / {b2})";
            }
            else
            {
                _lblActivePinBadge.Text = $"Editing: {FormatPinDisplayName(active)}   (Maps to: {b1})";
            }
        }

        private Control BuildLandmarkPinPanel()
        {
            var (card, box) = CreateInspectorCard(
                "Step 1: Place Joint Pins (16 Joints)",
                "Drag pins in 3D onto your model's joints. Shift+Click toggles multiple pins (Shift+Double-Click or Ctrl+A selects all 16). Switch to Step 2 or Tab 1/3 to inspect.");
            card.Visible = false;

            var badgePanel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ThemeTypeVariation = "SubPanelContainer" };
            var badgePad = new MarginContainer();
            badgePad.AddThemeConstantOverride("margin_left", 8);
            badgePad.AddThemeConstantOverride("margin_right", 8);
            badgePad.AddThemeConstantOverride("margin_top", 6);
            badgePad.AddThemeConstantOverride("margin_bottom", 6);
            _lblActivePinBadge = new Label
            {
                Text = "Active Pin: None (drag a pin, or Shift+Click to multi-select)",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            _lblActivePinBadge.AddThemeColorOverride("font_color", ThemeManager.GetColor(nameof(ThemePaletteData.AccentPrimary)));
            badgePad.AddChild(_lblActivePinBadge);
            badgePanel.AddChild(badgePad);
            box.AddChild(badgePanel);

            _chkMirrorPins = new CheckBox { Text = "Mirror Left <-> Right pins", ButtonPressed = true, Visible = false };
            _chkMirrorPins.Toggled += on =>
            {
                if (_btnMirrorPinsToggle != null && _btnMirrorPinsToggle.ButtonPressed != on)
                    _btnMirrorPinsToggle.SetPressedNoSignal(on);
                UpdateActivePinBadge();
                RefreshPinVisuals();
            };
            box.AddChild(_chkMirrorPins);

            _chkShowPins = new CheckBox { Text = "Show pins and joint skeleton", ButtonPressed = true, Visible = false };
            _chkShowPins.Toggled += on =>
            {
                if (_btnShowPinsToggle != null && _btnShowPinsToggle.ButtonPressed != on)
                    _btnShowPinsToggle.SetPressedNoSignal(on);
                RefreshPinVisuals();
            };
            box.AddChild(_chkShowPins);

            var ghostHint = new Label
            {
                Text = "Tip: Use 'Select All Bones' (or Shift+Click / Ctrl+A) to shift multiple pins at once; toggle Hero Ghost to compare silhouettes.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            ImporterStyle.ApplyHintStyle(ghostHint);
            _secondaryLabels.Add(ghostHint);
            box.AddChild(ghostHint);

            var btnRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            btnRow.AddThemeConstantOverride("separation", 6);

            _btnSelectAllPins = new Button
            {
                Text = "Select All Bones",
                ToggleMode = true,
                ThemeTypeVariation = "ToolbarButton",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Selects all 16 joint pins so dragging any pin moves the whole auto-rig skeleton together (Shortcut: Ctrl+A or Shift+Double-Click)."
            };
            SetButtonIcon(_btnSelectAllPins, "res://assets/at-icons/bone.svg", 16);
            _btnSelectAllPins.Pressed += ToggleSelectAllPins;
            btnRow.AddChild(_btnSelectAllPins);

            var btn = new Button
            {
                Text = "Auto-Place Pins (Reset)",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Re-estimates all 16 joints from your enabled mesh geometry."
            };
            btn.Pressed += () =>
            {
                _selectedPin = null; _hoveredPin = null; _draggingPin = null;
                _selectedPins.Clear();
                EnsureLandmarkPins(true);
                UpdateActivePinBadge();
                RefreshPinVisuals();
                if (IsPinEditActive())
                {
                    _pinsDirtyNeedsRigRebuild = true;
                    SetRigChipStatus("Pins Modified (Step 2 / Preview applies)", StatusTone.Info);
                }
                else
                {
                    TriggerRealtimeRigUpdate();
                }
            };
            btnRow.AddChild(btn);
            box.AddChild(btnRow);
            return card;
        }

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

            _chkPaintMirror = new CheckBox { Text = "Mirror L <-> R Brush", ButtonPressed = true };
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

        private void EnsureLandmarkPins(bool force)
        {
            if (!force && _landmarkPins.Count >= CustomLandmarkWeightSolver.PinNames.Length) return;
            if (_liveImportedScene == null || !GodotObject.IsInstanceValid(_liveImportedScene)) return;
            var meshes = FilterActiveImportedMeshes(CustomModelAutoRigger.CollectMeshes(_liveImportedScene));
            if (meshes.Count == 0) return;
            var est = CustomLandmarkWeightSolver.EstimateLandmarks(_liveImportedScene, meshes);
            if (est.Count > 0)
            {
                _landmarkPins = est;
                if (force) _userMovedLandmarkPins = false;
            }
        }

        private Color PinColor(string name)
        {
            if (name == "Head") return new Color(0.2f, 0.9f, 1f);
            if (name.EndsWith("L") && name != "Pelvis") return new Color(0.3f, 0.5f, 1f);
            if (name.EndsWith("R")) return new Color(1f, 0.55f, 0.15f);
            return new Color(1f, 0.85f, 0.2f);
        }

        private void RefreshPinVisuals()
        {
            bool show = IsPinEditActive() && (_chkShowPins?.ButtonPressed ?? true) &&
                        _landmarkPins.Count >= CustomLandmarkWeightSolver.PinNames.Length &&
                        _previewWorldRoot != null && GodotObject.IsInstanceValid(_previewWorldRoot);

            foreach (var kv in _pinSpheres)
                if (GodotObject.IsInstanceValid(kv.Value)) kv.Value.Visible = show;
            if (_pinSkeletonLines != null && GodotObject.IsInstanceValid(_pinSkeletonLines)) _pinSkeletonLines.Visible = show;
            if (!show) return;

            string active = _draggingPin ?? _hoveredPin ?? _selectedPin;
            string mirrorActive = (active != null && (_chkMirrorPins?.ButtonPressed ?? true))
                ? CustomLandmarkWeightSolver.MirrorName(active)
                : null;

            Transform3D xf = BuildCurrentAlignedTransform();
            foreach (var name in CustomLandmarkWeightSolver.PinNames)
            {
                if (!_pinSpheres.TryGetValue(name, out var sphere) || !GodotObject.IsInstanceValid(sphere))
                {
                    var mat = new StandardMaterial3D
                    {
                        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                        AlbedoColor = PinColor(name),
                        NoDepthTest = true,
                        RenderPriority = 100
                    };
                    sphere = new MeshInstance3D
                    {
                        Name = "LandmarkPin_" + name,
                        Mesh = new SphereMesh { Radius = 0.025f, Height = 0.05f, RadialSegments = 12, Rings = 6, Material = mat },
                        Layers = 1u // never Visual Layer 21 (CameraBrush)
                    };
                    _previewWorldRoot.AddChild(sphere);
                    _pinSpheres[name] = sphere;
                }
                bool inMulti = _selectedPins.Contains(name);
                bool isLit = inMulti || name == active || (mirrorActive != null && name == mirrorActive);
                float s = (name == active || (mirrorActive != null && name == mirrorActive))
                    ? 1.55f
                    : (inMulti ? 1.40f : 1f);
                sphere.Scale = new Vector3(s, s, s);
                sphere.Position = xf * _landmarkPins[name];
                if (sphere.Mesh is SphereMesh sm && sm.Material is StandardMaterial3D sphereMat)
                {
                    Color targetCol = inMulti ? PinColor(name).Lerp(Colors.White, 0.45f) : PinColor(name);
                    if (!sphereMat.AlbedoColor.IsEqualApprox(targetCol))
                    {
                        sphereMat.AlbedoColor = targetCol;
                    }
                }
                sphere.Visible = true;
            }

            if (_pinSkeletonLines == null || !GodotObject.IsInstanceValid(_pinSkeletonLines))
            {
                _pinSkeletonLines = new MeshInstance3D
                {
                    Name = "LandmarkPinSkeleton",
                    Mesh = new ImmediateMesh(),
                    Layers = 1u,
                    MaterialOverride = new StandardMaterial3D
                    {
                        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                        VertexColorUseAsAlbedo = true,
                        NoDepthTest = true,
                        RenderPriority = 99
                    }
                };
                _previewWorldRoot.AddChild(_pinSkeletonLines);
            }
            var im = (ImmediateMesh)_pinSkeletonLines.Mesh;
            im.ClearSurfaces();
            im.SurfaceBegin(Mesh.PrimitiveType.Lines);
            foreach (var (a, b) in PinConnections)
            {
                im.SurfaceSetColor(PinColor(b)); im.SurfaceAddVertex(xf * _landmarkPins[a]);
                im.SurfaceSetColor(PinColor(b)); im.SurfaceAddVertex(xf * _landmarkPins[b]);
            }
            im.SurfaceEnd();
        }

        private string PickPinUnderCursor(Vector2 pos)
        {
            if (_previewCamera == null) return null;
            Transform3D xf = BuildCurrentAlignedTransform();
            string best = null; float bestD = 22f;
            foreach (var name in CustomLandmarkWeightSolver.PinNames)
            {
                Vector3 w = xf * _landmarkPins[name];
                if (_previewCamera.IsPositionBehind(w)) continue;
                float d = _previewCamera.UnprojectPosition(w).DistanceTo(pos);
                if (d < bestD) { bestD = d; best = name; }
            }
            return best;
        }

        private bool HandlePinInput(InputEvent @event)
        {
            if (!IsPinEditActive() || !(_chkShowPins?.ButtonPressed ?? true) ||
                _landmarkPins.Count < CustomLandmarkWeightSolver.PinNames.Length || _previewCamera == null)
                return false;

            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    string best = PickPinUnderCursor(mb.Position);
                    if (best != null)
                    {
                        if (mb.ShiftPressed)
                        {
                            if (mb.DoubleClick)
                            {
                                SelectAllPins();
                            }
                            else
                            {
                                TogglePinInSelection(best, _chkMirrorPins?.ButtonPressed ?? true);
                            }
                            _isOrbiting = false; _isPanning = false;
                            GetViewport().SetInputAsHandled();
                            return true;
                        }

                        if (!_selectedPins.Contains(best))
                        {
                            _selectedPins.Clear();
                            _selectedPins.Add(best);
                        }
                        _draggingPin = best;
                        _selectedPin = best;
                        _hoveredPin = best;
                        _isOrbiting = false; _isPanning = false;
                        UpdateActivePinBadge();
                        RefreshPinVisuals();
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
                }
                else if (!mb.Pressed && _draggingPin != null)
                {
                    _draggingPin = null;
                    UpdateActivePinBadge();
                    if (IsPinEditActive())
                    {
                        _pinsDirtyNeedsRigRebuild = true;
                        SetRigChipStatus("Pins Modified (Step 2 / Preview applies)", StatusTone.Info);
                    }
                    else
                    {
                        ScheduleDebouncedLiveRigUpdate();
                    }
                    GetViewport().SetInputAsHandled();
                    return true;
                }
            }
            else if (@event is InputEventMouseMotion mm)
            {
                if (_draggingPin != null)
                {
                    Transform3D xf = BuildCurrentAlignedTransform();
                    Vector3 oldLocal = _landmarkPins[_draggingPin];
                    Vector3 worldPin = xf * oldLocal;
                    Vector3 origin = _previewCamera.ProjectRayOrigin(mm.Position);
                    Vector3 dir = _previewCamera.ProjectRayNormal(mm.Position);
                    Vector3 normal = -_previewCamera.GlobalTransform.Basis.Z;
                    float denom = dir.Dot(normal);
                    if (Mathf.Abs(denom) > 1e-4f)
                    {
                        float t = (worldPin - origin).Dot(normal) / denom;
                        Vector3 hit = origin + dir * t;
                        Vector3 local = xf.AffineInverse() * hit;
                        Vector3 delta = local - oldLocal;
                        _userMovedLandmarkPins = true;

                        if (_selectedPins.Count > 1 && _selectedPins.Contains(_draggingPin))
                        {
                            float cx = _landmarkPins["Pelvis"].X;
                            foreach (string p in _selectedPins)
                            {
                                if (_landmarkPins.TryGetValue(p, out Vector3 cur))
                                {
                                    _landmarkPins[p] = cur + delta;
                                }
                            }
                            if (_chkMirrorPins?.ButtonPressed ?? true)
                            {
                                foreach (string p in _selectedPins)
                                {
                                    string mir = CustomLandmarkWeightSolver.MirrorName(p);
                                    if (mir != null && !_selectedPins.Contains(mir) && _landmarkPins.TryGetValue(p, out Vector3 moved))
                                    {
                                        _landmarkPins[mir] = new Vector3(2f * cx - moved.X, moved.Y, moved.Z);
                                    }
                                }
                            }
                        }
                        else
                        {
                            _landmarkPins[_draggingPin] = local;

                            string mirror = CustomLandmarkWeightSolver.MirrorName(_draggingPin);
                            if (mirror != null && (_chkMirrorPins?.ButtonPressed ?? true))
                            {
                                float cx = _landmarkPins["Pelvis"].X;
                                _landmarkPins[mirror] = new Vector3(2f * cx - local.X, local.Y, local.Z);
                            }
                        }
                        UpdateActivePinBadge();
                        RefreshPinVisuals();
                    }
                    GetViewport().SetInputAsHandled();
                    return true;
                }
                else if (!_isOrbiting && !_isPanning)
                {
                    string hover = PickPinUnderCursor(mm.Position);
                    if (hover != _hoveredPin)
                    {
                        _hoveredPin = hover;
                        UpdateActivePinBadge();
                        RefreshPinVisuals();
                    }
                }
            }
            return false;
        }

        private void SetPreviewPhysicsPausedForWeightPaint(bool paused)
        {
            if (_previewHeroSkeleton == null || !GodotObject.IsInstanceValid(_previewHeroSkeleton)) return;
            foreach (Node ch in _previewHeroSkeleton.GetChildren())
            {
                if (ch is DeadlockPlayground.Animation.CustomJiggleBoneDriver driver && GodotObject.IsInstanceValid(driver))
                {
                    driver.SetPausedForEditing(paused);
                }
            }
        }

        private void OnWeightPaintModeToggled(bool enabled)
        {
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
                _pendingStrokeSnapshot = null;
                _strokeDirty = false;
                if (_btnChainWeightPaintToggle != null && _btnChainWeightPaintToggle.ButtonPressed)
                    _btnChainWeightPaintToggle.SetPressedNoSignal(false);
                if (_boxTab3WeightPaintHost != null)
                    _boxTab3WeightPaintHost.Visible = false;
                if (_paintBrushRing != null && GodotObject.IsInstanceValid(_paintBrushRing)) _paintBrushRing.Visible = false;
                foreach (var s in _paintSurfaces)
                    CustomWeightPaintEditor.RefreshSurfaceHeatmap(s, -1, -1, false, null);
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
                ButtonPressed = true,
                CustomMinimumSize = new Vector2(58, 24),
                ThemeTypeVariation = "ToolbarButton"
            };
            _hudBtnMirror.Toggled += on =>
            {
                _chkPaintMirror?.SetPressedNoSignal(on);
                UpdateResetBoneButtonLabel();
                RefreshWeightPaintHeatmap();
            };
            row2.AddChild(_hudBtnMirror);
            root.AddChild(row2);
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
            if (_paintSurfaces.Count == 0 || _previewHeroSkeleton == null || _optPaintBone == null || _optPaintBone.Selected < 0)
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

            int shown = 0;
            foreach (var entry in _paintBoneCatalog)
            {
                if (_paintBoneFilterMode == 0 && entry.VertCount == 0) continue;
                if (_paintBoneFilterMode == 1 && !entry.IsPhysics) continue;
                if (!string.IsNullOrEmpty(query) && !entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                bool isSel = entry.Name.Equals(activeBone, StringComparison.OrdinalIgnoreCase);
                string badge = entry.VertCount > 0 ? $"  ★ {entry.VertCount}v" : (entry.IsPhysics ? "  [phys]" : "");
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
                _boxPaintBoneList.AddChild(btn);
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

        private void CommitPaintSurfaceOverrides()
        {
            if (_paintSurfaces.Count == 0 || _previewHeroSkeleton == null) return;
            var skin = _paintSurfaces[0].MeshInstance?.Skin;
            var exported = CustomWeightPaintEditor.ExportOverrides(_paintSurfaces, _previewHeroSkeleton, skin);
            foreach (var (k, v) in exported) _customSkinOverrides[k] = v;
            UpdatePaintOverrideLabel();
        }

        private bool EnsureBaselineOverrides()
        {
            if (_baselineSkinOverrides.Count > 0) return true;

            if (_customSkinOverrides.Count == 0 && _paintSurfaces.Count > 0 && _previewHeroSkeleton != null)
            {
                var skin = _paintSurfaces[0].MeshInstance?.Skin;
                var baseExp = CustomWeightPaintEditor.ExportOverrides(_paintSurfaces, _previewHeroSkeleton, skin);
                foreach (var (k, v) in baseExp) _baselineSkinOverrides[k] = v;
                return _baselineSkinOverrides.Count > 0;
            }

            if (_liveImportedScene == null || _previewHeroClone == null || _previewHeroSkeleton == null)
                return false;

            var baselineOpts = GatherRiggingOptions();
            baselineOpts.CustomSkinWeightOverrides = null;
            baselineOpts.AlignedTransform = BuildCurrentAlignedTransform();
            baselineOpts.IsPreviewOnly = true;

            var tempModel = (Node3D)_liveImportedScene.Duplicate();
            _previewModelRoot?.AddChild(tempModel);
            tempModel.Transform = Transform3D.Identity;
            tempModel.Visible = false;

            var res = CustomModelAutoRigger.RigAndBind(tempModel, _previewHeroClone, baselineOpts);
            if (res.Success && res.ContainerNode != null && GodotObject.IsInstanceValid(res.ContainerNode))
            {
                var tmpSurfaces = CustomWeightPaintEditor.CaptureSurfaces(res.ContainerNode);
                var tmpSkin = tmpSurfaces.Count > 0 ? tmpSurfaces[0].MeshInstance?.Skin : null;
                if (tmpSkin != null)
                {
                    var exported = CustomWeightPaintEditor.ExportOverrides(tmpSurfaces, _previewHeroSkeleton, tmpSkin);
                    foreach (var (k, v) in exported) _baselineSkinOverrides[k] = v;
                }
            }
            if (res.ContainerNode != null && GodotObject.IsInstanceValid(res.ContainerNode))
            {
                res.ContainerNode.GetParent()?.RemoveChild(res.ContainerNode);
                res.ContainerNode.QueueFree();
            }
            if (GodotObject.IsInstanceValid(tempModel))
            {
                tempModel.GetParent()?.RemoveChild(tempModel);
                tempModel.QueueFree();
            }
            return _baselineSkinOverrides.Count > 0;
        }

        private void OnWeightUndoPressed()
        {
            if (!IsWeightPaintActive() || _paintSurfaces.Count == 0 || _previewHeroSkeleton == null || _weightUndoStack.Count == 0)
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
            if (!IsWeightPaintActive() || _paintSurfaces.Count == 0 || _previewHeroSkeleton == null || _weightRedoStack.Count == 0)
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
            if (_paintSurfaces.Count == 0 || _previewHeroSkeleton == null) return;
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
            if (_paintSurfaces.Count == 0 || _previewHeroSkeleton == null) return;
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
            if (_paintSurfaces.Count == 0 || _previewHeroSkeleton == null) return;
            var skin = _paintSurfaces[0].MeshInstance?.Skin;
            if (skin == null || !EnsureBaselineOverrides()) return;

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

            if (_previewRiggedContainer != null && GodotObject.IsInstanceValid(_previewRiggedContainer))
            {
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
            if (!IsWeightPaintActive() || _paintSurfaces.Count == 0 || _previewHeroSkeleton == null) return;
            var skin = _paintSurfaces[0].MeshInstance?.Skin;
            if (skin == null) return;

            RefreshViewportShadingAndWireframes();

            _weightHeatmapMaterial ??= new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel,
                Roughness = 0.85f
            };
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

        private bool HandleWeightPaintInput(InputEvent @event)
        {
            if (!IsWeightPaintActive() || _previewCamera == null || _paintSurfaces.Count == 0 || _previewHeroSkeleton == null)
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
                    if (CustomWeightPaintEditor.RaycastSurfaces(orig, dir, _paintSurfaces, out var hitSurf, out var hitPt, out _) &&
                        CustomWeightPaintEditor.PickStrongestBoneAtPoint(hitSurf, hitPt, _previewHeroSkeleton, skin, out string pickedBone, out float pickedW))
                    {
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
                    if (CustomWeightPaintEditor.RaycastSurfaces(orig, dir, _paintSurfaces, out _, out var hitPt, out var hitNorm))
                    {
                        string boneLabel = _optPaintBone != null && _optPaintBone.Selected >= 0 ? _optPaintBone.GetItemText(_optPaintBone.Selected) : "weight";
                        _pendingStrokeSnapshot = CustomWeightPaintEditor.CaptureHistoryEntry(_paintSurfaces, $"Brush ({boneLabel})");
                        _strokeDirty = false;
                        _isPaintingWeights = true;
                        _isOrbiting = false; _isPanning = false;
                        _strokeDirty |= ApplyBrushAtHit(hitPt, hitNorm);
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
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
                if (CustomWeightPaintEditor.RaycastSurfaces(orig, dir, _paintSurfaces, out _, out var hitPt, out var hitNorm))
                {
                    float r = (float)(_sliderPaintRadius?.Value ?? 0.08);
                    UpdateBrushRingVisual(hitPt, hitNorm, r, true);
                    if (_isPaintingWeights)
                    {
                        _strokeDirty |= ApplyBrushAtHit(hitPt, hitNorm);
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
                }
                else
                {
                    UpdateBrushRingVisual(Vector3.Zero, Vector3.Up, 0.05f, false);
                }
            }
            return false;
        }

        private bool ApplyBrushAtHit(Vector3 hitPt, Vector3 hitNorm)
        {
            if (_paintSurfaces.Count == 0 || _previewHeroSkeleton == null) return false;
            var skin = _paintSurfaces[0].MeshInstance?.Skin;
            if (skin == null) return false;

            string boneName = _optPaintBone != null && _optPaintBone.Selected >= 0 ? _optPaintBone.GetItemText(_optPaintBone.Selected) : "hand_L";
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

            UpdateBrushRingVisual(hitPt, hitNorm, r, true);
            if (CustomWeightPaintEditor.ApplyBrush(_paintSurfaces, hitPt, mode, r, strength, tgtBi, fbBi, mirror, mirBi, mirFbBi))
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

            if (_previewModelRoot != null && GodotObject.IsInstanceValid(_previewModelRoot))
            {
                bool riggedValid = _previewRiggedContainer != null && GodotObject.IsInstanceValid(_previewRiggedContainer);
                if (pinEdit)
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
            RefreshPinVisuals();
        }

        #endregion

        private void OnCalibrationSliderChanged()
        {
            if (!IsKnnMethod()) return;
            // During active slider dragging in Bind Pose, show the static unskinned imported model
            // and hide the previously rigged mesh so only the Deadlock cyan ghost moves around it.
            bool inBindPose = _optTestAnim == null || _optTestAnim.Selected <= 0;
            if (inBindPose && _previewModelRoot != null && GodotObject.IsInstanceValid(_previewModelRoot))
            {
                if (_previewRiggedContainer != null && GodotObject.IsInstanceValid(_previewRiggedContainer))
                {
                    _previewRiggedContainer.Visible = false;
                }
                _previewModelRoot.Transform = BuildCurrentAlignedTransform();
                SyncPreviewModelMeshVisibility();
                _previewModelRoot.Visible = true;
            }

            ApplyCalibrationPoseToGhostSkeleton();
            ScheduleDebouncedLiveRigUpdate();
        }

        private void ApplyCalibrationPoseToGhostSkeleton()
        {
            if (_previewHeroSkeleton == null || !GodotObject.IsInstanceValid(_previewHeroSkeleton)) return;
            if (_optTestAnim != null && _optTestAnim.Selected > 0) return;

            _previewAnimPlayer?.Stop();
            _previewHeroSkeleton.ResetBonePoses();
            var opt = BuildCurrentCalibrationPreviewOptions();
            CustomModelAutoRigger.PoseHeroSkeletonForOptions(_previewHeroSkeleton, opt);
            if (_bonesDebugMesh != null && _bonesDebugMesh.Visible) RedrawBoneWireframe();
            _collidersDirty = true;
            if (_showColliders) RedrawColliderWireframe();
        }

        private Transform3D BuildCurrentAlignedTransform()
        {
            float scale = _spinScale != null ? (float)_spinScale.Value : 1f;
            Vector3 scaleXyz = GetScaleXyzVector();
            Vector3 pos = new Vector3(
                _spinPosX != null ? (float)_spinPosX.Value : 0f,
                _spinPosY != null ? (float)_spinPosY.Value : 0f,
                _spinPosZ != null ? (float)_spinPosZ.Value : 0f);
            Vector3 rot = new Vector3(
                _spinRotPitch != null ? (float)_spinRotPitch.Value : 0f,
                _spinRotYaw != null ? (float)_spinRotYaw.Value : 0f,
                _spinRotRoll != null ? (float)_spinRotRoll.Value : 0f);

            Basis rotBasis = Basis.FromEuler(
                new Vector3(Mathf.DegToRad(rot.X), Mathf.DegToRad(rot.Y), Mathf.DegToRad(rot.Z)),
                EulerOrder.Yxz);
            Basis scaleBasis = Basis.FromScale(scaleXyz * scale);
            return new Transform3D(rotBasis * scaleBasis, pos);
        }

        private List<string> GetSelectedImportedSubmeshNames()
        {
            return _importedSubmeshCheckboxes
                .Where(c => c.ButtonPressed)
                .Select(c => c.HasMeta("SubmeshName") ? c.GetMeta("SubmeshName").AsString() : c.Text)
                .ToList();
        }

        private CustomModelAutoRigger.RiggingOptions BuildCurrentCalibrationPreviewOptions()
        {
            var preset = _optPosePreset != null && _optPosePreset.Selected >= 0
                ? (CustomModelAutoRigger.PosePreset)_optPosePreset.GetItemId(_optPosePreset.Selected)
                : CustomModelAutoRigger.PosePreset.MatchHeroBindPose;
            return new CustomModelAutoRigger.RiggingOptions
            {
                Preset = preset,
                CustomArmAngleDeg = _sliderArmAngle != null ? (float)_sliderArmAngle.Value : 0f,
                CustomArmForwardDeg = _sliderArmForward != null ? (float)_sliderArmForward.Value : 0f,
                CustomElbowStraightenPct = _sliderElbowStraight != null ? (float)_sliderElbowStraight.Value : 0f,
                CustomLegSpreadDeg = _sliderLegSpread != null ? (float)_sliderLegSpread.Value : 0f
            };
        }

        private void OnPosePresetChanged(long index)
        {
            if (!IsKnnMethod()) return;
            var preset = _optPosePreset.Selected >= 0 ? (CustomModelAutoRigger.PosePreset)_optPosePreset.GetItemId(_optPosePreset.Selected) : CustomModelAutoRigger.PosePreset.TPose;
            _boxCustomArmAngle.Visible = preset == CustomModelAutoRigger.PosePreset.APoseCustomAngle;
            ApplyCalibrationPoseToGhostSkeleton();
            TriggerRealtimeRigUpdate();
        }

        #endregion

        #region Actions: Real-Time Test Rig & Confirm

        private void OnTestAnimSelected(long index)
        {
            if (_previewHeroSkeleton == null) return;
            if (index > 0 && _pinsDirtyNeedsRigRebuild)
            {
                ExecuteLiveRigUpdate();
            }
            Callable.From(ApplyLandmarkPreviewState).CallDeferred();

            if (index == 0) // Bind Pose (Rest)
            {
                _previewAnimPlayer?.Stop();
                _previewHeroSkeleton.ResetBonePoses();
                ApplyCalibrationPoseToGhostSkeleton();
                if (_sliderAnimTimeline != null) _sliderAnimTimeline.SetValueNoSignal(0.0);
                if (_lblAnimTime != null) _lblAnimTime.Text = "0.00 / 0.00s";
                if (_btnAnimPlayPause != null)
                {
                    _btnAnimPlayPause.ButtonPressed = false;
                    SetButtonIcon(_btnAnimPlayPause, "res://assets/at-icons/play.svg", 22);
                }
                return;
            }

            if (_previewAnimPlayer == null) return;

            string animName = _optTestAnim.GetItemText((int)index);
            var vpkLoader = GetVpkLoader();
            var animInfo = vpkLoader?.CurrentHeroAnimations?.FirstOrDefault(a =>
                a.IsCompatible &&
                (a.CleanName.Equals(animName, StringComparison.OrdinalIgnoreCase) ||
                 a.RawName.Equals(animName, StringComparison.OrdinalIgnoreCase)));

            string targetAnimName = animName;

            if (animInfo != null)
            {
                bool isAdditive = (animInfo.Category == "Additive Layers") ||
                                  (animInfo.VrfAnimation != null ? animInfo.VrfAnimation.IsAdditive : animInfo.IsAdditive) ||
                                  DeadlockAnimLoader.IsDeltaSequence(animInfo);

                if (animInfo.Category == "Idles & Stances" || animInfo.Category == "Idle" || animInfo.Category == "Remapped" ||
                    DeadlockAnimLoader.KnownRemappedAnimations.Contains(animInfo.CleanName) ||
                    DeadlockAnimLoader.KnownRemappedAnimations.Contains(animInfo.RawName))
                {
                    isAdditive = false;
                }

                DeadlockAnimLoader.AnimSequenceInfo idleInfo = null;
                if (isAdditive)
                {
                    idleInfo = FindDefaultIdleAnim();
                    if (idleInfo == animInfo) idleInfo = null;
                }

                string targetAnim = (isAdditive && idleInfo != null)
                    ? $"{animInfo.CleanName}_underlaid"
                    : animInfo.CleanName;

                if (isAdditive && idleInfo != null)
                {
                    if (!_previewAnimPlayer.HasAnimation(targetAnim))
                    {
                        var rawAnim = GetOrDecodePreviewAnimation(animInfo);
                        var idleAnim = GetOrDecodePreviewAnimation(idleInfo);
                        if (rawAnim != null && idleAnim != null)
                        {
                            var composed = DeadlockAnimLoader.ComposeOverIdle(rawAnim, idleAnim, _previewHeroSkeleton);
                            if (composed != null)
                            {
                                var animLib = _previewAnimPlayer.HasAnimationLibrary("")
                                    ? _previewAnimPlayer.GetAnimationLibrary("")
                                    : new AnimationLibrary();
                                if (!_previewAnimPlayer.HasAnimationLibrary(""))
                                {
                                    _previewAnimPlayer.AddAnimationLibrary("", animLib);
                                }
                                if (animLib.HasAnimation(targetAnim)) animLib.RemoveAnimation(targetAnim);
                                animLib.AddAnimation(targetAnim, composed);
                            }
                        }
                    }
                }

                if (!_previewAnimPlayer.HasAnimation(targetAnim))
                {
                    if (vpkLoader != null)
                    {
                        vpkLoader.DecodeAnimation(animInfo, _previewHeroSkeleton, _previewAnimPlayer, idleInfo, targetAnim, inPlace: true);
                    }
                }

                if (_previewAnimPlayer.HasAnimation(targetAnim))
                {
                    targetAnimName = targetAnim;
                }
                else if (_previewAnimPlayer.HasAnimation(animInfo.CleanName))
                {
                    targetAnimName = animInfo.CleanName;
                }
            }

            if (_previewAnimPlayer.HasAnimation(targetAnimName))
            {
                _previewAnimPlayer.Play(targetAnimName);
                float length = (float)_previewAnimPlayer.CurrentAnimationLength;
                if (_sliderAnimTimeline != null)
                {
                    _isUpdatingTimelineSlider = true;
                    try
                    {
                        _sliderAnimTimeline.MaxValue = Math.Max(0.01f, length);
                        _sliderAnimTimeline.SetValueNoSignal(0.0);
                    }
                    finally
                    {
                        _isUpdatingTimelineSlider = false;
                    }
                }
                if (_lblAnimTime != null)
                {
                    _lblAnimTime.Text = $"0.00 / {length:F2}s";
                }
                if (_btnAnimPlayPause != null)
                {
                    _btnAnimPlayPause.ButtonPressed = true;
                    SetButtonIcon(_btnAnimPlayPause, "res://assets/at-icons/pause.svg", 22);
                }
                if (_btnAnimLoop != null)
                {
                    var anim = _previewAnimPlayer.GetAnimation(targetAnimName);
                    if (anim != null)
                    {
                        anim.LoopMode = _btnAnimLoop.ButtonPressed ? Godot.Animation.LoopModeEnum.Linear : Godot.Animation.LoopModeEnum.None;
                    }
                }
            }
        }

        public CustomModelAutoRigger.RiggingOptions GatherRiggingOptions()
        {
            var method = _optRiggingMethod.Selected >= 0
                ? (CustomModelAutoRigger.RiggingMethod)_optRiggingMethod.GetItemId(_optRiggingMethod.Selected)
                : CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection;

            string resolvedAccName = (_txtAccName != null && !string.IsNullOrWhiteSpace(_txtAccName.Text))
                ? _txtAccName.Text.Trim()
                : (!string.IsNullOrEmpty(_filePath) ? Path.GetFileNameWithoutExtension(_filePath) : "");
            string deformTargetFilter = (_optDeformTarget != null && _optDeformTarget.Selected >= 0)
                ? _optDeformTarget.GetItemMetadata(_optDeformTarget.Selected).AsString()
                : "hair";
            if (string.IsNullOrWhiteSpace(deformTargetFilter)) deformTargetFilter = "hair";
            var deformMode = (_optDeformMode != null && _optDeformMode.Selected >= 0)
                ? (CustomModelAutoRigger.HeroDeformMode)_optDeformMode.GetItemId(_optDeformMode.Selected)
                : CustomModelAutoRigger.HeroDeformMode.FlattenTopDown;
            string rigidBoneStr = _optRigidBone.Selected >= 0 ? _optRigidBone.GetItemText(_optRigidBone.Selected).Replace("★ ", "") : "";

            var fingerMode = _optFingerRigMode != null && _optFingerRigMode.Selected >= 0
                ? (FingerRigMode)_optFingerRigMode.GetItemId(_optFingerRigMode.Selected)
                : ((_chkKeepFingersRigid?.ButtonPressed ?? false) ? FingerRigMode.RigidHand : FingerRigMode.DeadlockFingerBones);
            var accBoneMode = _optAccBoneMode != null && _optAccBoneMode.Selected >= 0
                ? (AccessoryBoneMode)_optAccBoneMode.GetItemId(_optAccBoneMode.Selected)
                : AccessoryBoneMode.RigidToOneBone;
            int famId = _optRigFamilyOverride != null && _optRigFamilyOverride.Selected >= 0
                ? _optRigFamilyOverride.GetItemId(_optRigFamilyOverride.Selected)
                : -1;
            RigFamily? rigFamilyOverride = famId >= 0 ? (RigFamily)famId : null;

            return new CustomModelAutoRigger.RiggingOptions
            {
                SlotId = _accessorySlotId ?? "",
                AccessoryName = resolvedAccName,
                Intent = _workflowIntent == ImportWorkflowIntent.AccessoryAddOrReplace
                    ? _accessoryIntent
                    : CustomModelAutoRigger.AccessoryIntent.FullCharacter,
                ExplicitReplacedTargets = (_workflowIntent == ImportWorkflowIntent.AccessoryAddOrReplace &&
                                           _accessoryIntent == CustomModelAutoRigger.AccessoryIntent.ReplacePart)
                    ? _accReplaceCheckboxes
                        .Where(c => GodotObject.IsInstanceValid(c) && c.ButtonPressed && c.HasMeta("SubmeshName"))
                        .Select(c => c.GetMeta("SubmeshName").AsString())
                        .ToList()
                    : new List<string>(),
                Method = method,
                Preset = _optPosePreset.Selected >= 0 ? (CustomModelAutoRigger.PosePreset)_optPosePreset.GetItemId(_optPosePreset.Selected) : CustomModelAutoRigger.PosePreset.TPose,
                CustomArmAngleDeg = (float)_sliderArmAngle.Value,
                CustomArmForwardDeg = (float)(_sliderArmForward?.Value ?? 0.0),
                CustomElbowStraightenPct = (float)(_sliderElbowStraight?.Value ?? 0.0),
                CustomLegSpreadDeg = (float)(_sliderLegSpread?.Value ?? 0.0),
                RigidBoneName = rigidBoneStr,
                Mode = _optReplaceMode.Selected >= 0 ? (CustomModelAutoRigger.ReplaceMode)_optReplaceMode.GetItemId(_optReplaceMode.Selected) : CustomModelAutoRigger.ReplaceMode.FullReplacement,
                IncludedImportedSubmeshNames = _importedSubmeshCheckboxes
                    .Where(c => c.ButtonPressed && c.HasMeta("SubmeshName"))
                    .Select(c => c.GetMeta("SubmeshName").AsString())
                    .ToList(),
                KeptOriginalSubmeshNames = _heroKeepCheckboxes
                    .Where(c => c.ButtonPressed && c.HasMeta("SubmeshName"))
                    .Select(c => c.GetMeta("SubmeshName").AsString())
                    .ToList(),
                CustomBoneRemap = new Dictionary<string, string>(_userBoneRemap),
                SmoothingIterations = (int)_spinLaplacian.Value,
                SearchRadius = (float)_spinKnnRadius.Value,
                ExcludeClothHairBones = _chkExcludeClothHair.ButtonPressed,
                SubmeshAttachmentSockets = new Dictionary<string, string>(_submeshSocketAssignments),
                SubmeshReplacementMap = new Dictionary<string, string>(_submeshHeroReplacements),
                SubmeshSnapToSocket = new Dictionary<string, bool>(_submeshSnapToSocket),
                SubmeshSocketOffsetPos = new Dictionary<string, Vector3>(_submeshSocketOffsetPos),
                SubmeshSocketOffsetRot = new Dictionary<string, Vector3>(_submeshSocketOffsetRot),
                SubmeshSocketScale = new Dictionary<string, float>(_submeshSocketScale),
                IncludeBakedOutlineShells = _chkIncludeBakedOutlines?.ButtonPressed ?? true,
                SubmeshIsOutlineShell = new Dictionary<string, bool>(_submeshIsOutlineShell, StringComparer.OrdinalIgnoreCase),
                KeepFingersRigidOnHand = (_chkKeepFingersRigid?.ButtonPressed ?? false) || fingerMode == FingerRigMode.RigidHand,
                RigFamilyOverride = rigFamilyOverride,
                KeepUnweightedBones = _chkKeepUnweightedBones?.ButtonPressed ?? false,
                EnableCustomPhysics = _chkEnableCustomPhysics?.ButtonPressed ?? _chkPhysicsMasterTab3?.ButtonPressed ?? true,
                PreserveSourceClothingChains = _chkPreserveClothingChains?.ButtonPressed ?? true,
                DisabledSourceBones = new HashSet<string>(_disabledSourceBones, StringComparer.OrdinalIgnoreCase),
                BoneChains = _boneChains.Select(c => c.Clone()).ToList(),
                BodyCollisionCapsules = _bodyCollisionCapsules.Select(c => c.Clone()).ToList(),
                AccessoryBoneMode = accBoneMode,
                FingerMode = fingerMode,
                FingerLandmarks = _fingerLandmarks.Count > 0 ? new Dictionary<string, Vector3>(_fingerLandmarks, StringComparer.OrdinalIgnoreCase) : null,
                GenerateFingerMetacarpals = _chkGenMetacarpals?.ButtonPressed ?? false,
                GenerateWristTwist = _chkGenWristTwist?.ButtonPressed ?? false,
                BindSymmetry = _chkBindSymmetry?.ButtonPressed ?? true,
                FitSpine = _chkFitSpine?.ButtonPressed ?? true,
                ConformToSkeleton = _chkConformToSkeleton?.ButtonPressed ?? true,
                LooseMeshesRigid = _chkLooseMeshesRigid?.ButtonPressed ?? true,
                ExtraTwistBones = _chkExtraTwistBones?.ButtonPressed ?? true,
                AutoSnapArmsToHero = _chkAutoAlignArms?.ButtonPressed ?? true,
                AutoSnapLegsToHero = _chkAutoAlignLegs?.ButtonPressed ?? true,
                AutoSnapHeadToHero = _chkAutoAlignHead?.ButtonPressed ?? true,
                AutoAlignHandOrientation = _chkAutoAlignHandOrientation?.ButtonPressed ?? true,
                LeftHandSnapOffsetDeg = new Vector3(
                    (float)(_spinLeftHandPitch?.Value ?? 0.0),
                    (float)(_spinLeftHandYaw?.Value ?? 0.0),
                    (float)(_spinLeftHandRoll?.Value ?? 0.0)),
                RightHandSnapOffsetDeg = new Vector3(
                    (float)(_spinRightHandPitch?.Value ?? 0.0),
                    (float)(_spinRightHandYaw?.Value ?? 0.0),
                    (float)(_spinRightHandRoll?.Value ?? 0.0)),
                LeftHandSnapScale = (float)(_spinLeftHandScale?.Value ?? 1.0),
                RightHandSnapScale = (float)(_spinRightHandScale?.Value ?? 1.0),
                KeptOriginalPartTransforms = CloneKeptPartTransforms(_keptPartTransforms),
                ClampLimbStretching = _chkClampLimbStretching?.ButtonPressed ?? true,
                StabilizeNeckFloatingHead = _chkStabilizeNeckFloatingHead?.ButtonPressed ?? (_heroCodename == "chrono"),
                AutoCounterTwistWeights = _chkAutoCounterTwistWeights?.ButtonPressed ?? true,
                RootPosition = new Vector3((float)_spinPosX.Value, (float)_spinPosY.Value, (float)_spinPosZ.Value),
                RootRotationDeg = new Vector3((float)_spinRotPitch.Value, (float)_spinRotYaw.Value, (float)_spinRotRoll.Value),
                RootScale = (float)_spinScale.Value,
                RootScaleXYZ = GetScaleXyzVector(),
                FlipX = _btnFlipX?.ButtonPressed ?? false,
                FlipY = _btnFlipY?.ButtonPressed ?? false,
                FlipZ = _btnFlipZ?.ButtonPressed ?? false,
                OriginalMeshDeformEnabled = _chkDeformEnabled?.ButtonPressed ?? false,
                OriginalMeshDeformTargetFilter = deformTargetFilter,
                OriginalMeshDeformBoneName = string.IsNullOrWhiteSpace(rigidBoneStr) ? "head" : rigidBoneStr,
                OriginalMeshDeformMode = deformMode,
                OriginalMeshDeformStrength = (float)(_sliderDeformStrength?.Value ?? 0.55),
                OriginalMeshDeformRadius = (float)(_sliderDeformRadius?.Value ?? 0.24),
                OriginalMeshDeformHeightOffset = (float)(_sliderDeformHeight?.Value ?? 0.06),
                OriginalMeshDeformSculpts = ((_chkDeformEnabled?.ButtonPressed ?? false) && _deformSculptDeltas.Count > 0)
                    ? CloneDeformSculpts(_deformSculptDeltas)
                    : null,
                LandmarkPins = (method == CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig && _landmarkPins.Count >= CustomLandmarkWeightSolver.PinNames.Length)
                    ? new Dictionary<string, Vector3>(_landmarkPins)
                    : null,
                CustomSkinWeightOverrides = _customSkinOverrides.Count > 0
                    ? new Dictionary<string, CustomModelAutoRigger.SkinWeightOverride>(_customSkinOverrides, StringComparer.OrdinalIgnoreCase)
                    : null
            };
        }

        private void TriggerRealtimeRigUpdate()
        {
            _liveRigDebounceTimer?.Stop();
            if (_rigUpdatePending) return;
            _rigUpdatePending = true;
            SetRigChipStatus("Live Rig: Updating...", StatusTone.Warning);
            Callable.From(ExecuteLiveRigUpdate).CallDeferred();
        }

        private void ScheduleDebouncedLiveRigUpdate()
        {
            if (ImporterMemoryGuard.LightMode)
            {
                SetRigChipStatus("Live Rig: Stale (Click Update)", StatusTone.Warning);
                if (_lblRemapSyncBadge != null)
                {
                    _lblRemapSyncBadge.Text = "Stale (Click Update)";
                    _lblRemapSyncBadge.AddThemeColorOverride("font_color", ThemeManager.GetColor(nameof(ThemePaletteData.SemanticWarning)));
                }
                return;
            }

            if (_liveRigDebounceTimer == null || !GodotObject.IsInstanceValid(_liveRigDebounceTimer))
            {
                _liveRigDebounceTimer = new Godot.Timer
                {
                    OneShot = true,
                    WaitTime = 0.45
                };
                _liveRigDebounceTimer.Timeout += OnLiveRigDebounceTimeout;
                AddChild(_liveRigDebounceTimer);
            }
            SetRigChipStatus("Live Rig: Updating...", StatusTone.Warning);
            _liveRigDebounceTimer.Start(0.45);
        }

        private void OnLiveRigDebounceTimeout()
        {
            // Defer heavy re-bake while the user is actively holding a SpinBox arrow, slider, or 3D gizmo handle
            if (Input.IsMouseButtonPressed(MouseButton.Left) || _isGizmoDragging || (_previewGizmo?.Editing ?? false))
            {
                _liveRigDebounceTimer?.Start(0.15);
                return;
            }
            ExecuteLiveRigUpdate();
        }

        private bool TryApplyFastSocketPreviewTransform()
        {
            if (_previewRiggedContainer == null || !GodotObject.IsInstanceValid(_previewRiggedContainer))
                return false;

            int updated = 0;
            foreach (Node child in _previewRiggedContainer.GetChildren())
            {
                if (child is not MeshInstance3D mi || !mi.HasMeta("IsSocketProp") || !mi.GetMeta("IsSocketProp").AsBool())
                    continue;
                if (!mi.HasMeta("SocketMeshKey"))
                    return false;

                string meshKey = mi.GetMeta("SocketMeshKey").AsString();
                Vector3 pos = _submeshSocketOffsetPos.TryGetValue(meshKey, out var p) ? p : Vector3.Zero;
                Vector3 rot = _submeshSocketOffsetRot.TryGetValue(meshKey, out var r) ? r : Vector3.Zero;
                float scale = _submeshSocketScale.TryGetValue(meshKey, out float s) && s > 0.001f ? s : 1.0f;

                if (!CustomModelAutoRigger.TryUpdateSocketSkinBindPose(mi, pos, rot, scale))
                    return false;
                updated++;
            }

            if (updated > 0)
            {
                SetRigChipStatus("Live Rig: Up to date", StatusTone.Success);
                return true;
            }
            return false;
        }

        private void ExecuteLiveRigUpdate()
        {
            _liveRigDebounceTimer?.Stop();
            _rigUpdatePending = false;
            _pinsDirtyNeedsRigRebuild = false;
            SetRigChipStatus("Live Rig: Up to date", StatusTone.Success);
            if (_previewHeroClone == null || _previewHeroSkeleton == null || _liveImportedScene == null) return;

            // CRITICAL: Flush active painted weights into _customSkinOverrides BEFORE rebuilding/moving
            // the preview rig so moving, rotating, or scaling a layer NEVER resets painted weights!
            if (_paintSurfaces.Count > 0)
            {
                CommitPaintSurfaceOverrides();
            }

            // 1. Capture current animation time and playback state
            double currentAnimPos = 0.0;
            string currentAnimName = null;
            bool wasPlaying = false;
            float animSpeed = 1.0f;
            if (_previewAnimPlayer != null && _optTestAnim.Selected > 0)
            {
                string active = !string.IsNullOrEmpty(_previewAnimPlayer.CurrentAnimation)
                    ? _previewAnimPlayer.CurrentAnimation
                    : _previewAnimPlayer.AssignedAnimation;
                currentAnimName = (!string.IsNullOrEmpty(active) && _previewAnimPlayer.HasAnimation(active))
                    ? active
                    : _optTestAnim.GetItemText(_optTestAnim.Selected);
                currentAnimPos = _previewAnimPlayer.CurrentAnimationPosition;
                wasPlaying = _previewAnimPlayer.IsPlaying();
                animSpeed = (float)_previewAnimPlayer.SpeedScale;
            }

            // 2. Clean up previous test rig preview
            if (_previewRiggedContainer != null && GodotObject.IsInstanceValid(_previewRiggedContainer))
            {
                _previewRiggedContainer.QueueFree();
                _previewRiggedContainer = null;
            }
            _previewHeroSkeleton.ResetBonePoses();

            // 3. Re-create pristine preview model instance
            _previewFlipRestore = null;
            if (GodotObject.IsInstanceValid(_previewModelInstance))
            {
                _previewModelInstance.QueueFree();
            }
            _previewModelInstance = (Node3D)_liveImportedScene.Duplicate();
            _previewModelRoot.AddChild(_previewModelInstance);
            SyncPreviewModelMeshVisibility();
            ApplyPreviewGeometryFlip();
            _cachedSourceSkeleton = CustomModelAutoRigger.FindSkeleton(_previewModelInstance);

            // Re-apply preview model root transform
            _previewModelRoot.Transform = BuildCurrentAlignedTransform();

            var options = GatherRiggingOptions();
            options.AlignedTransform = _previewModelRoot.Transform;
            options.IsPreviewOnly = true;

            var result = CustomModelAutoRigger.RigAndBind(_previewModelInstance, _previewHeroClone, options);
            if (result.Success)
            {
                _previewRiggedContainer = result.ContainerNode;
                _bakedPreviewTransform = _previewModelRoot.Transform;
                _hasBakedPreviewTransform = true;
                _previewModelRoot.Visible = false;

                CustomModelAutoRigger.UpdateCustomModelRuntimePosing(_previewHeroSkeleton, options.ClampLimbStretching);
                _previewConstraintDriver?.EvaluateNow(0f);

                CustomModelAutoRigger.RecomputeHeroDeformations(_previewHeroClone, options);
                ApplyGhostMaterialToClone(_previewHeroClone);
                if (_previewHeroClone != null) _previewHeroClone.Visible = true;

                SetBottomStatus($"Rigged in real time ({result.TotalVertices:N0} vertices bound).", StatusTone.Success);

                // Resume animation smoothly at exact position, or apply calibrated ghost pose in Bind Pose
                if (!string.IsNullOrEmpty(currentAnimName) && _previewAnimPlayer != null && _previewAnimPlayer.HasAnimation(currentAnimName))
                {
                    _previewAnimPlayer.Play(currentAnimName);
                    _previewAnimPlayer.SpeedScale = animSpeed;
                    _previewAnimPlayer.Seek(currentAnimPos, true);
                    if (!wasPlaying)
                    {
                        _previewAnimPlayer.Pause();
                    }
                }
                else
                {
                    ApplyCalibrationPoseToGhostSkeleton();
                }

                if (!_isGizmoDragging && !(_previewGizmo?.Editing ?? false))
                {
                    UpdateGizmoTargetTransform();
                }
                _paintSurfaces.Clear();
                if (_customSkinOverrides.Count == 0) _baselineSkinOverrides.Clear();
                ApplyLandmarkPreviewState();
                RefreshReviewTabFromReport(result.Report);
                MarkRemapAppliedBadge();
                // Sync any newly discovered accessory chains back into _boneChains without overwriting
                // existing user-tuned chains (prevents moving/scaling an accessory from resetting physics settings).
                if (options.BoneChains != null && options.BoneChains.Count > 0)
                {
                    var existingRoots = new HashSet<string>(
                        _boneChains.Where(c => c != null && !string.IsNullOrEmpty(c.RootSourceBone)).Select(c => c.RootSourceBone),
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var oc in options.BoneChains)
                    {
                        if (oc == null || string.IsNullOrEmpty(oc.RootSourceBone)) continue;
                        if (existingRoots.Add(oc.RootSourceBone))
                        {
                            _boneChains.Add(oc.Clone());
                        }
                    }
                }

                // Keep physics frozen & re-enter Weight Paint surfaces seamlessly if Weight Paint is active
                if (_btnWeightPaintMode?.ButtonPressed == true)
                {
                    EnterWeightPaintSurfaces();
                    SetPreviewPhysicsPausedForWeightPaint(true);
                }

                SyncBodyCollidersFromRigResult(options.BodyCollisionCapsules);
                RefreshClothingChainsPanel();
                RefreshBodyCollidersPanel();
                _collidersDirty = true;
                if (_showColliders) RedrawColliderWireframe();
                UpdateFooterMemoryIndicator();
                if (result.Report != null)
                {
                    AppendConsoleMessage($"Rigged preview: {result.TotalVertices:N0} verts, {result.Report.BuildSummaryLine()}");
                }
            }
            else
            {
                _previewModelRoot.Visible = true;
                AppendConsoleMessage($"Preview Rigging Failed: {result.ErrorMessage}");
                SetBottomStatus($"Preview Rigging Failed: {result.ErrorMessage}", StatusTone.Danger);
            }
        }


        private void SyncGizmoSpaceButton()
        {
            int target = _optGizmoTarget?.Selected >= 0 ? _optGizmoTarget.GetItemId(_optGizmoTarget.Selected) : 0;
            bool hasTarget = target == 1 || target == 2 || target == 3 || target == 4;
            bool useLocal = _btnGizmoLocalSpace?.ButtonPressed ?? false;
            if (_btnGizmoLocalSpace != null)
            {
                _btnGizmoLocalSpace.Disabled = !hasTarget;
                _btnGizmoLocalSpace.Text = useLocal ? "Local" : "Global";
            }
            if (_previewGizmo != null)
            {
                _previewGizmo.UseLocalSpace = useLocal;
            }
        }

        private void SelectGizmoTargetById(int targetId)
        {
            if (_optGizmoTarget == null) return;
            for (int i = 0; i < _optGizmoTarget.ItemCount; i++)
            {
                if (_optGizmoTarget.GetItemId(i) == targetId)
                {
                    _optGizmoTarget.Select(i);
                    if (!_isGizmoDragging && !(_previewGizmo?.Editing ?? false))
                {
                    UpdateGizmoTargetTransform();
                }
                    return;
                }
            }
        }

        private void OnGizmoTargetChanged(long index)
        {
            UpdateGizmoTargetTransform();
        }

        private void UpdateGizmoTargetTransform()
        {
            SyncGizmoSpaceButton();
            if (_previewGizmo == null || _previewHeroSkeleton == null) return;
            int target = _optGizmoTarget?.Selected >= 0 ? _optGizmoTarget.GetItemId(_optGizmoTarget.Selected) : 0;
            if (_btnAccGizmoToggle != null) _btnAccGizmoToggle.SetPressedNoSignal(target == 3);

            if (target == 0 || IsWeightPaintActive()) // Off or temporarily hidden during Weight Paint
            {
                _previewGizmo.Visible = false;
                if (_gizmoDummyTarget != null && GodotObject.IsInstanceValid(_gizmoDummyTarget))
                {
                    _previewGizmo.Deselect(_gizmoDummyTarget);
                }
                return;
            }

            _isGizmoUpdatingFromUI = true;

            if (target == 3 && _previewModelRoot != null && _previewWorldRoot != null)
            {
                _previewGizmo.Mode = Gizmo3D.ToolMode.Rotate | Gizmo3D.ToolMode.Move;
                if (_gizmoDummyTarget == null || !GodotObject.IsInstanceValid(_gizmoDummyTarget))
                {
                    _gizmoDummyTarget = new Node3D { Name = "GizmoDummyTarget" };
                }
                if (_gizmoDummyTarget.GetParent() != _previewWorldRoot)
                {
                    _gizmoDummyTarget.GetParent()?.RemoveChild(_gizmoDummyTarget);
                    _previewWorldRoot.AddChild(_gizmoDummyTarget);
                }
                Transform3D rootT = _previewModelRoot.Transform;
                Basis pureRot = rootT.Basis.Orthonormalized();
                _gizmoDummyTarget.GlobalTransform = new Transform3D(pureRot, rootT * _accLocalBoundsCenter);
            }
            else if (target == 1 || target == 2) // Weapon R or L
            {
                _previewGizmo.Mode = Gizmo3D.ToolMode.Rotate | Gizmo3D.ToolMode.Move;
                string socketBone = (target == 1) ? "weapon_hand_R" : "weapon_hand_L";
                int bIdx = _previewHeroSkeleton.FindBone(socketBone);
                if (bIdx < 0) bIdx = _previewHeroSkeleton.FindBone(target == 1 ? "hand_R" : "hand_L");

                if (bIdx >= 0)
                {
                    string actualBoneName = _previewHeroSkeleton.GetBoneName(bIdx);

                    // Ensure _boneAttachment is attached to _previewHeroSkeleton
                    if (_boneAttachment == null || !GodotObject.IsInstanceValid(_boneAttachment) || _boneAttachment.GetParent() != _previewHeroSkeleton)
                    {
                        if (_boneAttachment != null && GodotObject.IsInstanceValid(_boneAttachment))
                        {
                            _boneAttachment.QueueFree();
                        }
                        _boneAttachment = new BoneAttachment3D();
                        _previewHeroSkeleton.AddChild(_boneAttachment);
                    }

                    _boneAttachment.BoneName = actualBoneName;

                    if (_gizmoDummyTarget == null || !GodotObject.IsInstanceValid(_gizmoDummyTarget))
                    {
                        _gizmoDummyTarget = new Node3D { Name = "GizmoDummyTarget" };
                    }

                    if (_gizmoDummyTarget.GetParent() != _boneAttachment)
                    {
                        if (_gizmoDummyTarget.GetParent() != null)
                        {
                            _gizmoDummyTarget.GetParent().RemoveChild(_gizmoDummyTarget);
                        }
                        _boneAttachment.AddChild(_gizmoDummyTarget);
                    }

                    string wpMesh = FindWeaponMeshForSocket(socketBone);
                    Vector3 offPos = (wpMesh != null && _submeshSocketOffsetPos.TryGetValue(wpMesh, out var p)) ? p : Vector3.Zero;
                    Vector3 offRot = (wpMesh != null && _submeshSocketOffsetRot.TryGetValue(wpMesh, out var r)) ? r : Vector3.Zero;
                    _gizmoDummyTarget.Position = offPos;
                    _gizmoDummyTarget.RotationDegrees = offRot;
                    _gizmoDummyTarget.RemoveMeta("KeptPartFallbackOrigin");
                }
            }
            else if (target == 4) // Kept Hero Part / Weapon (Rest-Pose Override)
            {
                string keepKey = GetSelectedKeptPartKey();
                if (string.IsNullOrEmpty(keepKey))
                {
                    _previewGizmo.Visible = false;
                    if (_gizmoDummyTarget != null && GodotObject.IsInstanceValid(_gizmoDummyTarget))
                        _previewGizmo.Deselect(_gizmoDummyTarget);
                    _isGizmoUpdatingFromUI = false;
                    return;
                }

                _previewGizmo.Mode = Gizmo3D.ToolMode.Rotate | Gizmo3D.ToolMode.Move;
                var xf = _keptPartTransforms.TryGetValue(keepKey, out var existing) && existing != null
                    ? existing
                    : new CustomModelAutoRigger.KeptHeroPartTransform();

                CustomModelAutoRigger.FindDominantBoneForHeroKeepKey(_previewHeroClone, keepKey, out string domBoneName, out Transform3D pivotRestHero);
                int bIdx = !string.IsNullOrEmpty(domBoneName) ? _previewHeroSkeleton.FindBone(domBoneName) : -1;

                if (_gizmoDummyTarget == null || !GodotObject.IsInstanceValid(_gizmoDummyTarget))
                {
                    _gizmoDummyTarget = new Node3D { Name = "GizmoDummyTarget" };
                }

                if (bIdx >= 0)
                {
                    string actualBoneName = _previewHeroSkeleton.GetBoneName(bIdx);
                    if (_boneAttachment == null || !GodotObject.IsInstanceValid(_boneAttachment) || _boneAttachment.GetParent() != _previewHeroSkeleton)
                    {
                        if (_boneAttachment != null && GodotObject.IsInstanceValid(_boneAttachment))
                            _boneAttachment.QueueFree();
                        _boneAttachment = new BoneAttachment3D();
                        _previewHeroSkeleton.AddChild(_boneAttachment);
                    }
                    _boneAttachment.BoneName = actualBoneName;
                    if (_gizmoDummyTarget.GetParent() != _boneAttachment)
                    {
                        _gizmoDummyTarget.GetParent()?.RemoveChild(_gizmoDummyTarget);
                        _boneAttachment.AddChild(_gizmoDummyTarget);
                    }
                    _gizmoDummyTarget.RemoveMeta("KeptPartFallbackOrigin");
                    _gizmoDummyTarget.Position = xf.PositionOffset;
                    _gizmoDummyTarget.RotationDegrees = xf.RotationDeg;
                }
                else if (_previewWorldRoot != null)
                {
                    if (_gizmoDummyTarget.GetParent() != _previewWorldRoot)
                    {
                        _gizmoDummyTarget.GetParent()?.RemoveChild(_gizmoDummyTarget);
                        _previewWorldRoot.AddChild(_gizmoDummyTarget);
                    }
                    _gizmoDummyTarget.SetMeta("KeptPartFallbackOrigin", pivotRestHero.Origin);
                    _gizmoDummyTarget.Position = pivotRestHero.Origin + xf.PositionOffset;
                    _gizmoDummyTarget.RotationDegrees = xf.RotationDeg;
                }
            }
            if (_gizmoDummyTarget != null && !_previewGizmo.IsSelected(_gizmoDummyTarget))
            {
                _previewGizmo.Select(_gizmoDummyTarget);
            }
            _previewGizmo.Visible = true;
            _isGizmoUpdatingFromUI = false;
        }

        private void UpdateToolbarForWorkflowIntent()
        {
            bool isFullCharacter = _workflowIntent == ImportWorkflowIntent.FullCharacter;
            if (_btnSnapGripPalm != null) _btnSnapGripPalm.Visible = isFullCharacter;
            if (_btnStraightenWeapon != null) _btnStraightenWeapon.Visible = isFullCharacter;
            if (_btnAutoFit != null) _btnAutoFit.Visible = isFullCharacter;
            if (_boxReplaceModeSection != null) _boxReplaceModeSection.Visible = isFullCharacter;
            if (_btnPhysTabColliders != null)
            {
                // Keep Body Colliders accessible in Accessory mode too so physics capes/skirts/hair can adapt hero colliders
                _btnPhysTabColliders.Visible = true;
            }

            if (_optGizmoTarget != null)
            {
                int prevId = _optGizmoTarget.Selected >= 0 ? _optGizmoTarget.GetItemId(_optGizmoTarget.Selected) : 0;
                _optGizmoTarget.Clear();
                _optGizmoTarget.AddItem("Off", 0);
                if (isFullCharacter)
                {
                    _optGizmoTarget.AddItem("Weapon (R)", 1);
                    _optGizmoTarget.AddItem("Weapon (L)", 2);
                }
                _optGizmoTarget.AddItem("Model Root", 3);
                _optGizmoTarget.AddItem("Kept Part (Hero)", 4);

                int targetToSelect = (!isFullCharacter && (prevId == 1 || prevId == 2)) ? 3 : prevId;
                SelectGizmoTargetById(targetToSelect);
            }
            UpdateAccessoryRigVisibility();
        }

        private void OnGizmoTransformEnd(int mode)
        {
            _isGizmoDragging = false;
            int target = _optGizmoTarget?.Selected >= 0 ? _optGizmoTarget.GetItemId(_optGizmoTarget.Selected) : 0;
            if (target == 3)
            {
                ScheduleDebouncedLiveRigUpdate();
            }
            else if (target == 1 || target == 2)
            {
                if (!TryApplyFastSocketPreviewTransform())
                {
                    ScheduleDebouncedLiveRigUpdate();
                }
            }
            else if (target == 4)
            {
                ApplyPreviewHeroDeformation();
            }
        }

        private void OnGizmoTransformChanged(int mode, Vector3 value)
        {
            if (_isGizmoUpdatingFromUI || _previewGizmo == null || _gizmoDummyTarget == null) return;
            int target = _optGizmoTarget?.Selected >= 0 ? _optGizmoTarget.GetItemId(_optGizmoTarget.Selected) : 0;
            if (target == 4)
            {
                string keepKey = GetSelectedKeptPartKey();
                if (string.IsNullOrEmpty(keepKey)) return;

                Vector3 pos = _gizmoDummyTarget.Position;
                if (_gizmoDummyTarget.HasMeta("KeptPartFallbackOrigin"))
                {
                    pos -= _gizmoDummyTarget.GetMeta("KeptPartFallbackOrigin").AsVector3();
                }
                Vector3 rot = _gizmoDummyTarget.RotationDegrees;
                float scale = (float)(_spinKeptScale?.Value ?? 1.0);

                var xf = new CustomModelAutoRigger.KeptHeroPartTransform
                {
                    PositionOffset = pos,
                    RotationDeg = rot,
                    Scale = scale
                };
                if (xf.IsIdentity) _keptPartTransforms.Remove(keepKey);
                else _keptPartTransforms[keepKey] = xf;

                _isSyncingKeptPartUi = true;
                try
                {
                    _spinKeptPosX?.SetValueNoSignal(Math.Round(pos.X, 4));
                    _spinKeptPosY?.SetValueNoSignal(Math.Round(pos.Y, 4));
                    _spinKeptPosZ?.SetValueNoSignal(Math.Round(pos.Z, 4));
                    _spinKeptRotPitch?.SetValueNoSignal(Math.Round(rot.X, 2));
                    _spinKeptRotYaw?.SetValueNoSignal(Math.Round(rot.Y, 2));
                    _spinKeptRotRoll?.SetValueNoSignal(Math.Round(rot.Z, 2));
                }
                finally
                {
                    _isSyncingKeptPartUi = false;
                }
                ApplyPreviewHeroDeformation();
                return;
            }
            if (target == 3 && _previewModelRoot != null)
            {
                Basis rotBasis = _gizmoDummyTarget.GlobalTransform.Basis.Orthonormalized();
                Vector3 eulerRad = rotBasis.GetEuler(EulerOrder.Yxz);
                Vector3 rotDeg = new Vector3(
                    Mathf.RadToDeg(eulerRad.X),
                    Mathf.RadToDeg(eulerRad.Y),
                    Mathf.RadToDeg(eulerRad.Z));
                float scale = (float)_spinScale.Value;
                Basis rotScaleBasis = rotBasis * Basis.FromScale(GetScaleXyzVector() * scale);
                Vector3 newOrigin = _gizmoDummyTarget.GlobalTransform.Origin - (rotScaleBasis * _accLocalBoundsCenter);
                Transform3D directTransform = new Transform3D(rotScaleBasis, newOrigin);

                _isGizmoUpdatingFromUI = true;
                _spinPosX.SetValueNoSignal(newOrigin.X);
                _spinPosY.SetValueNoSignal(newOrigin.Y);
                _spinPosZ.SetValueNoSignal(newOrigin.Z);
                _spinRotPitch.SetValueNoSignal(rotDeg.X);
                _spinRotYaw.SetValueNoSignal(rotDeg.Y);
                _spinRotRoll.SetValueNoSignal(rotDeg.Z);
                _isGizmoUpdatingFromUI = false;

                _previewModelRoot.Transform = directTransform;
                if (_previewRiggedContainer != null && GodotObject.IsInstanceValid(_previewRiggedContainer) && _hasBakedPreviewTransform &&
                    Mathf.Abs(_bakedPreviewTransform.Basis.Determinant()) > 1e-8f)
                {
                    _previewRiggedContainer.Transform = directTransform * _bakedPreviewTransform.AffineInverse();
                }
                ScheduleDebouncedLiveRigUpdate();
                return;
            }
            if (target != 1 && target != 2) return;

            string socket = (target == 1) ? "weapon_hand_R" : "weapon_hand_L";
            var meshes = FindAllWeaponMeshesForSocket(socket);
            if (meshes.Count > 0)
            {
                Vector3 rot = _gizmoDummyTarget.RotationDegrees;
                Vector3 pos = _gizmoDummyTarget.Position; // Bone-local offset directly!
                _isGizmoUpdatingFromUI = true;
                foreach (var meshName in meshes)
                {
                    if (_submeshCardControls.TryGetValue(meshName, out var ctrl))
                    {
                        ctrl.Pitch.SetValueNoSignal(Math.Round(rot.X, 1));
                        ctrl.Yaw.SetValueNoSignal(Math.Round(rot.Y, 1));
                        ctrl.Roll.SetValueNoSignal(Math.Round(rot.Z, 1));
                        ctrl.PosX.SetValueNoSignal(Math.Round(pos.X, 3));
                        ctrl.PosY.SetValueNoSignal(Math.Round(pos.Y, 3));
                        ctrl.PosZ.SetValueNoSignal(Math.Round(pos.Z, 3));
                    }
                    _submeshSocketOffsetRot[meshName] = rot;
                    _submeshSocketOffsetPos[meshName] = pos;
                }
                _isGizmoUpdatingFromUI = false;
                if (!TryApplyFastSocketPreviewTransform())
                {
                    ScheduleDebouncedLiveRigUpdate();
                }
            }
        }

        private void OnSnapGripToPalmPressed()
        {
            int target = _optGizmoTarget?.Selected ?? 0;
            var targetMeshes = new List<string>();

            if (target == 2)
            {
                var mL = FindAllWeaponMeshesForSocket("weapon_hand_L");
                targetMeshes.AddRange(mL);
            }
            else if (target == 1)
            {
                var mR = FindAllWeaponMeshesForSocket("weapon_hand_R");
                targetMeshes.AddRange(mR);
            }
            else
            {
                var mR = FindAllWeaponMeshesForSocket("weapon_hand_R");
                targetMeshes.AddRange(mR);
                var mL = FindAllWeaponMeshesForSocket("weapon_hand_L");
                foreach (var m in mL) if (!targetMeshes.Contains(m)) targetMeshes.Add(m);
            }

            if (targetMeshes.Count == 0)
            {
                foreach (var (meshName, socket) in _submeshSocketAssignments)
                {
                    if (socket.Contains("weapon") || socket.Contains("hand"))
                    {
                        targetMeshes.Add(meshName);
                    }
                }
            }

            if (targetMeshes.Count == 0)
            {
                SetBottomStatus("No weapon submeshes found to snap. Check Tab 3: Submeshes.", StatusTone.Warning);
                return;
            }

            _isGizmoUpdatingFromUI = true;
            foreach (var meshName in targetMeshes)
            {
                _submeshSnapToSocket[meshName] = true;
                _submeshSocketOffsetRot[meshName] = Vector3.Zero;
                _submeshSocketOffsetPos[meshName] = Vector3.Zero;
                _submeshSocketScale[meshName] = 1.0f;

                if (_submeshCardControls.TryGetValue(meshName, out var ctrl))
                {
                    ctrl.Placement.Select(1);
                    ctrl.Pitch.Value = 0.0;
                    ctrl.Yaw.Value = 0.0;
                    ctrl.Roll.Value = 0.0;
                    ctrl.PosX.Value = 0.0;
                    ctrl.PosY.Value = 0.0;
                    ctrl.PosZ.Value = 0.0;
                    ctrl.Scale.Value = 1.0;
                }
            }
            _isGizmoUpdatingFromUI = false;

            TriggerRealtimeRigUpdate();
            SetBottomStatus($"Snapped {targetMeshes.Count} weapon(s) grip to palm forward (PCA-aligned).", StatusTone.Success);
        }

        private void OnStraightenWeaponPressed()
        {
            int target = _optGizmoTarget?.Selected ?? 0;
            string socket = (target == 2) ? "weapon_hand_L" : "weapon_hand_R";

            var targetMeshes = FindAllWeaponMeshesForSocket(socket);
            if (targetMeshes.Count == 0 && target == 0)
            {
                targetMeshes = FindAllWeaponMeshesForSocket("weapon_hand_R");
                if (targetMeshes.Count == 0) targetMeshes = FindAllWeaponMeshesForSocket("weapon_hand_L");
            }

            if (targetMeshes.Count == 0)
            {
                SetBottomStatus("No weapon submeshes found to straighten. Check Tab 3: Submeshes.", StatusTone.Warning);
                return;
            }

            _isGizmoUpdatingFromUI = true;
            foreach (var meshName in targetMeshes)
            {
                _submeshSnapToSocket[meshName] = true;
                _submeshSocketOffsetRot[meshName] = Vector3.Zero;
                _submeshSocketOffsetPos[meshName] = Vector3.Zero;
                _submeshSocketScale[meshName] = 1.0f;

                if (_submeshCardControls.TryGetValue(meshName, out var ctrl))
                {
                    ctrl.Placement.Select(1);
                    ctrl.Pitch.Value = 0.0;
                    ctrl.Yaw.Value = 0.0;
                    ctrl.Roll.Value = 0.0;
                    ctrl.PosX.Value = 0.0;
                    ctrl.PosY.Value = 0.0;
                    ctrl.PosZ.Value = 0.0;
                    ctrl.Scale.Value = 1.0;
                }
            }
            _isGizmoUpdatingFromUI = false;

            SetBottomStatus($"Straightened {targetMeshes.Count} weapon mesh(es) intrinsic to barrel (0° Pitch/Yaw/Roll, aligned with Gizmo +Z Forward).", StatusTone.Success);

            OnGizmoTargetChanged(_optGizmoTarget?.Selected ?? 0);
            TriggerRealtimeRigUpdate();
        }

        private List<string> FindAllWeaponMeshesForSocket(string socketBone)
        {
            var list = new List<string>();
            foreach (var (meshName, socket) in _submeshSocketAssignments)
            {
                if (socket.Equals(socketBone, StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(meshName);
                }
            }
            if (list.Count == 0)
            {
                string fallback = socketBone.Contains("_L") ? "hand_L" : "hand_R";
                foreach (var (meshName, socket) in _submeshSocketAssignments)
                {
                    if (socket.Equals(fallback, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(meshName);
                    }
                }
            }
            return list;
        }

        private string FindWeaponMeshForSocket(string socketBone)
        {
            var list = FindAllWeaponMeshesForSocket(socketBone);
            return list.Count > 0 ? list[0] : null;
        }

        private void OnConfirmApplyToHero()
        {
            _liveRigDebounceTimer?.Stop();
            if (_liveHeroNode == null || _liveImportedScene == null)
            {
                OnCancel();
                return;
            }

            SetBottomStatus("Binding custom model to live hero...", StatusTone.Info);

            // Flush any live painted weights before gathering final rigging options
            if (_isPaintingWeights && _paintSurfaces.Count > 0)
            {
                CommitPaintSurfaceOverrides();
            }

            var options = GatherRiggingOptions();
            // CRITICAL: Use local Transform, NEVER GlobalTransform, so scale and rotation are fully preserved!
            options.AlignedTransform = _previewModelRoot.Transform;
            LastRiggingOptions = options;

            // Apply rigging and skinning directly to live hero in the main viewport!
            var result = CustomModelAutoRigger.RigAndBind(_liveImportedScene, _liveHeroNode, options);
            if (result.Success)
            {
                // Live hero visibility for all stacked slots & replaced surfaces has already been updated by RecomputeHeroReplacementVisibility
                EmitSignal(SignalName.RiggingConfirmed);
                CleanupPreviewResources();
                QueueFree();
            }
            else
            {
                SetBottomStatus($"Rigging Error: {result.ErrorMessage}", StatusTone.Danger);
            }
        }

        private void OnCancel()
        {
            _liveRigDebounceTimer?.Stop();
            _hasBakedPreviewTransform = false;
            // If live imported scene is not parented to anything, clean it up
            if (_liveImportedScene != null && GodotObject.IsInstanceValid(_liveImportedScene) && _liveImportedScene.GetParent() == null)
            {
                _liveImportedScene.QueueFree();
            }

            CleanupPreviewResources();
            EmitSignal(SignalName.ModalCancelled);
            QueueFree();
        }

        #region Interactive 3D Hero Deform Sculpt Brush

        private sealed class HeroDeformSurfaceCache
        {
            public MeshInstance3D MeshInstance;
            public int SurfaceIndex;
            public string DeformKey;
            public Mesh CachedMeshRef;
            public Transform3D MiToHero;
            public Transform3D HeroToWorld;
            public Vector3[] HeroVerts;
            public Vector3[] WorldVerts;
            public Vector3[] HeroNorms;
            public int[] Indices;
            public int[][] Neighbors;
        }

        private static Dictionary<string, Dictionary<int, Vector3>> CloneDeformSculpts(
            Dictionary<string, Dictionary<int, Vector3>> src)
        {
            var copy = new Dictionary<string, Dictionary<int, Vector3>>(StringComparer.OrdinalIgnoreCase);
            if (src == null) return copy;
            foreach (var (sk, vMap) in src)
            {
                if (!string.IsNullOrEmpty(sk) && vMap != null && vMap.Count > 0)
                {
                    copy[sk] = new Dictionary<int, Vector3>(vMap);
                }
            }
            return copy;
        }

        private void UpdateDeformBrushStatusLabel()
        {
            int vertCount = 0;
            foreach (var vMap in _deformSculptDeltas.Values) vertCount += vMap.Count;
            if (_lblDeformBrushStatus != null)
            {
                _lblDeformBrushStatus.Text = vertCount > 0
                    ? $"Sculpted {vertCount} hero vertex offset(s) across {_deformSculptDeltas.Count} surface(s)."
                    : "No sculpted vertices yet.";
            }
            if (_btnDeformBrushUndo != null) _btnDeformBrushUndo.Disabled = _deformUndoStack.Count == 0;
            if (_btnDeformBrushClear != null) _btnDeformBrushClear.Disabled = vertCount == 0;
        }

        private void OnDeformBrushToggled(bool enabled)
        {
            if (enabled)
            {
                if (_btnWeightPaintMode != null && _btnWeightPaintMode.ButtonPressed)
                {
                    _btnWeightPaintMode.ButtonPressed = false;
                }
                if (_chkDeformEnabled != null && !_chkDeformEnabled.ButtonPressed)
                {
                    _chkDeformEnabled.SetPressedNoSignal(true);
                    if (_boxDeformControls != null) _boxDeformControls.Visible = true;
                }
                if (_pinsDirtyNeedsRigRebuild)
                {
                    ExecuteLiveRigUpdate();
                }
                else
                {
                    ApplyLandmarkPreviewState();
                }
                RebuildHeroDeformSurfaceCache();
                SetBottomStatus("Deform Sculpt Brush active: drag directly on hero mesh (Shift+Drag pans, [ / ] resizes, Ctrl+Z undoes).", StatusTone.Info);
            }
            else
            {
                _isDeformBrushing = false;
                _deformFlattenPlaneLocked = false;
                _hasLastDeformBrushDab = false;
                _pendingDeformUndoSnapshot = null;
                UpdateBrushRingVisual(Vector3.Zero, Vector3.Up, 0.05f, false);
            }
        }

        private void OnDeformBrushUndoPressed()
        {
            if (_deformUndoStack.Count == 0) return;
            var prev = _deformUndoStack[^1];
            _deformUndoStack.RemoveAt(_deformUndoStack.Count - 1);
            _deformSculptDeltas.Clear();
            foreach (var (sk, vMap) in prev)
            {
                _deformSculptDeltas[sk] = new Dictionary<int, Vector3>(vMap);
            }
            UpdateDeformBrushStatusLabel();
            ApplyPreviewHeroDeformation();
            SetBottomStatus("Undid last hero deform brush stroke.", StatusTone.Info);
        }

        private void OnDeformBrushClearPressed()
        {
            if (_deformSculptDeltas.Count == 0) return;
            _deformUndoStack.Add(CloneDeformSculpts(_deformSculptDeltas));
            if (_deformUndoStack.Count > 25) _deformUndoStack.RemoveAt(0);
            _deformSculptDeltas.Clear();
            UpdateDeformBrushStatusLabel();
            ApplyPreviewHeroDeformation();
            SetBottomStatus("Cleared sculpted hero vertex offsets (Undo available).", StatusTone.Info);
        }

        private void RebuildHeroDeformSurfaceCache()
        {
            _deformSurfaceCaches.Clear();
            if (_previewHeroClone == null || !GodotObject.IsInstanceValid(_previewHeroClone)) return;

            Transform3D heroToWorld = _previewHeroClone.GlobalTransform;
            Transform3D heroInvWorld = heroToWorld.AffineInverse();

            var keptNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var chk in _heroKeepCheckboxes)
            {
                if (GodotObject.IsInstanceValid(chk) && chk.ButtonPressed && chk.HasMeta("SubmeshName"))
                    keptNames.Add(chk.GetMeta("SubmeshName").AsString());
            }

            var meshSet = new List<MeshInstance3D>();
            if (_previewGhostMeshes.Count > 0)
            {
                foreach (var gm in _previewGhostMeshes)
                    if (gm != null && GodotObject.IsInstanceValid(gm)) meshSet.Add(gm);
            }
            else
            {
                foreach (var cm in CustomModelAutoRigger.CollectMeshes(_previewHeroClone))
                    if (cm != null && GodotObject.IsInstanceValid(cm)) meshSet.Add(cm);
            }

            foreach (var mi in meshSet)
            {
                if (mi.Mesh == null || !mi.Visible) continue;
                int surfCount = mi.Mesh.GetSurfaceCount();
                Transform3D miToWorld = mi.GlobalTransform;
                Transform3D miToHero = heroInvWorld * miToWorld;

                for (int s = 0; s < surfCount; s++)
                {
                    if (keptNames.Count > 0 && !CustomModelAutoRigger.IsOriginalSurfaceKept(keptNames, mi.Name.ToString(), s))
                        continue;

                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.Vertex) continue;
                    var verts = CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    if (verts == null || verts.Length == 0) continue;

                    var norms = arrays.Count > (int)Mesh.ArrayType.Normal
                        ? CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Normal])
                        : null;
                    var indices = arrays.Count > (int)Mesh.ArrayType.Index
                        ? CustomModelAutoRigger.GetInt32Array(arrays[(int)Mesh.ArrayType.Index])
                        : null;
                    if (indices == null || indices.Length == 0)
                    {
                        indices = new int[verts.Length - verts.Length % 3];
                        for (int i = 0; i < indices.Length; i++) indices[i] = i;
                    }

                    var neighborSets = new HashSet<int>[verts.Length];
                    for (int i = 0; i < verts.Length; i++) neighborSets[i] = new HashSet<int>();
                    for (int t = 0; t + 2 < indices.Length; t += 3)
                    {
                        int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                        if ((uint)a >= (uint)verts.Length || (uint)b >= (uint)verts.Length || (uint)c >= (uint)verts.Length) continue;
                        neighborSets[a].Add(b); neighborSets[a].Add(c);
                        neighborSets[b].Add(a); neighborSets[b].Add(c);
                        neighborSets[c].Add(a); neighborSets[c].Add(b);
                    }
                    var neighbors = new int[verts.Length][];
                    for (int i = 0; i < verts.Length; i++)
                    {
                        neighbors[i] = neighborSets[i].Count > 0 ? neighborSets[i].ToArray() : Array.Empty<int>();
                    }

                    var cache = new HeroDeformSurfaceCache
                    {
                        MeshInstance = mi,
                        SurfaceIndex = s,
                        DeformKey = CustomModelAutoRigger.MakeOriginalSurfaceDeformKey(mi, s),
                        CachedMeshRef = mi.Mesh,
                        MiToHero = miToHero,
                        HeroToWorld = heroToWorld,
                        HeroVerts = new Vector3[verts.Length],
                        WorldVerts = new Vector3[verts.Length],
                        HeroNorms = new Vector3[verts.Length],
                        Indices = indices,
                        Neighbors = neighbors
                    };
                    SyncSingleDeformSurfaceCache(cache, arrays);
                    _deformSurfaceCaches.Add(cache);
                }
            }
        }

        private void RefreshHeroDeformSurfaceCacheVertices()
        {
            if (_previewHeroClone == null || !GodotObject.IsInstanceValid(_previewHeroClone)) return;
            Transform3D heroToWorld = _previewHeroClone.GlobalTransform;
            Transform3D heroInvWorld = heroToWorld.AffineInverse();

            for (int i = 0; i < _deformSurfaceCaches.Count; i++)
            {
                var c = _deformSurfaceCaches[i];
                if (c.MeshInstance == null || !GodotObject.IsInstanceValid(c.MeshInstance) || c.MeshInstance.Mesh == null) continue;
                if (c.SurfaceIndex >= c.MeshInstance.Mesh.GetSurfaceCount()) continue;
                if (ReferenceEquals(c.CachedMeshRef, c.MeshInstance.Mesh) && c.HeroToWorld == heroToWorld) continue;

                c.HeroToWorld = heroToWorld;
                c.MiToHero = heroInvWorld * c.MeshInstance.GlobalTransform;
                c.CachedMeshRef = c.MeshInstance.Mesh;
                var arrays = c.MeshInstance.Mesh.SurfaceGetArrays(c.SurfaceIndex);
                SyncSingleDeformSurfaceCache(c, arrays);
            }
        }

        private static void SyncSingleDeformSurfaceCache(HeroDeformSurfaceCache cache, Godot.Collections.Array arrays)
        {
            if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.Vertex) return;
            var verts = CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
            if (verts == null || verts.Length != cache.HeroVerts.Length) return;
            var norms = arrays.Count > (int)Mesh.ArrayType.Normal
                ? CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Normal])
                : null;
            Basis heroBasis = cache.MiToHero.Basis;
            for (int v = 0; v < verts.Length; v++)
            {
                Vector3 vh = cache.MiToHero * verts[v];
                cache.HeroVerts[v] = vh;
                cache.WorldVerts[v] = cache.HeroToWorld * vh;
                Vector3 nh = (norms != null && v < norms.Length) ? (heroBasis * norms[v]) : Vector3.Up;
                cache.HeroNorms[v] = nh.LengthSquared() > 1e-8f ? nh.Normalized() : Vector3.Up;
            }
        }

        private bool RaycastHeroDeformSurfaces(Vector3 rayOrigin, Vector3 rayDir, out HeroDeformSurfaceCache hitSurface, out Vector3 hitPointWorld, out Vector3 hitNormalWorld)
        {
            hitSurface = null;
            hitPointWorld = Vector3.Zero;
            hitNormalWorld = Vector3.Up;
            float bestT = float.MaxValue;

            for (int si = 0; si < _deformSurfaceCaches.Count; si++)
            {
                var c = _deformSurfaceCaches[si];
                if (c.MeshInstance == null || !GodotObject.IsInstanceValid(c.MeshInstance) || !c.MeshInstance.Visible) continue;
                var verts = c.WorldVerts;
                var idx = c.Indices;
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int i0 = idx[t], i1 = idx[t + 1], i2 = idx[t + 2];
                    if ((uint)i0 >= (uint)verts.Length || (uint)i1 >= (uint)verts.Length || (uint)i2 >= (uint)verts.Length) continue;
                    Vector3 a = verts[i0], b = verts[i1], cPt = verts[i2];
                    if (CustomWeightPaintEditor.RayTriangle(rayOrigin, rayDir, a, b, cPt, out float hitT) && hitT < bestT)
                    {
                        bestT = hitT;
                        hitSurface = c;
                        hitPointWorld = rayOrigin + rayDir * hitT;
                        Vector3 n = (b - a).Cross(cPt - a);
                        hitNormalWorld = n.LengthSquared() > 1e-8f ? n.Normalized() : Vector3.Up;
                    }
                }
            }
            return hitSurface != null;
        }

        private bool HandleHeroDeformBrushInput(InputEvent @event)
        {
            if (!IsDeformBrushActive() || _previewCamera == null || _previewHeroClone == null || !GodotObject.IsInstanceValid(_previewHeroClone))
                return false;

            if (_deformSurfaceCaches.Count == 0)
            {
                RebuildHeroDeformSurfaceCache();
                if (_deformSurfaceCaches.Count == 0) return false;
            }

            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed && !mb.ShiftPressed)
                {
                    RefreshHeroDeformSurfaceCacheVertices();
                    Vector3 orig = _previewCamera.ProjectRayOrigin(mb.Position);
                    Vector3 dir = _previewCamera.ProjectRayNormal(mb.Position);
                    if (RaycastHeroDeformSurfaces(orig, dir, out var hitSurf, out var hitPt, out var hitNorm))
                    {
                        _pendingDeformUndoSnapshot = CloneDeformSculpts(_deformSculptDeltas);
                        _deformBrushDirty = false;
                        _isDeformBrushing = true;
                        _hasLastDeformBrushDab = true;
                        _lastDeformBrushDabWorld = hitPt;
                        Transform3D heroInv = _previewHeroClone.GlobalTransform.AffineInverse();
                        _deformFlattenPlaneOriginHero = heroInv * hitPt;
                        Vector3 hnHero = heroInv.Basis * hitNorm;
                        _deformFlattenPlaneNormalHero = hnHero.LengthSquared() > 1e-8f ? hnHero.Normalized() : Vector3.Up;
                        _deformFlattenPlaneLocked = true;
                        _isOrbiting = false;
                        _isPanning = false;
                        _deformBrushDirty |= ApplyDeformBrushDab(hitPt, hitNorm);
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
                }
                else if (!mb.Pressed && _isDeformBrushing)
                {
                    _isDeformBrushing = false;
                    _deformFlattenPlaneLocked = false;
                    _hasLastDeformBrushDab = false;
                    if (_deformBrushDirty && _pendingDeformUndoSnapshot != null)
                    {
                        _deformUndoStack.Add(_pendingDeformUndoSnapshot);
                        if (_deformUndoStack.Count > 25) _deformUndoStack.RemoveAt(0);
                    }
                    _pendingDeformUndoSnapshot = null;
                    _deformBrushDirty = false;
                    UpdateDeformBrushStatusLabel();
                    GetViewport().SetInputAsHandled();
                    return true;
                }
            }
            else if (@event is InputEventMouseMotion mm && !_isOrbiting && !_isPanning)
            {
                RefreshHeroDeformSurfaceCacheVertices();
                Vector3 orig = _previewCamera.ProjectRayOrigin(mm.Position);
                Vector3 dir = _previewCamera.ProjectRayNormal(mm.Position);
                if (RaycastHeroDeformSurfaces(orig, dir, out _, out var hitPt, out var hitNorm))
                {
                    float r = (float)(_sliderDeformBrushRadius?.Value ?? 0.08);
                    UpdateBrushRingVisual(hitPt, hitNorm, r, true);
                    if (_isDeformBrushing)
                    {
                        float stepDist = MathF.Max(0.004f, r * 0.14f);
                        if (!_hasLastDeformBrushDab || hitPt.DistanceSquaredTo(_lastDeformBrushDabWorld) >= stepDist * stepDist)
                        {
                            _lastDeformBrushDabWorld = hitPt;
                            _hasLastDeformBrushDab = true;
                            _deformBrushDirty |= ApplyDeformBrushDab(hitPt, hitNorm);
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
            return false;
        }

        private bool ApplyDeformBrushDab(Vector3 hitWorld, Vector3 hitNormalWorld)
        {
            if (_previewHeroClone == null || !GodotObject.IsInstanceValid(_previewHeroClone)) return false;

            float radius = (float)(_sliderDeformBrushRadius?.Value ?? 0.08);
            float strength = (float)(_sliderDeformBrushStrength?.Value ?? 0.45);
            bool mirror = _chkDeformBrushMirror?.ButtonPressed ?? true;
            var mode = _optDeformBrushMode != null && _optDeformBrushMode.Selected >= 0
                ? (CustomModelAutoRigger.HeroSculptBrushMode)_optDeformBrushMode.GetItemId(_optDeformBrushMode.Selected)
                : CustomModelAutoRigger.HeroSculptBrushMode.PushIn;

            Transform3D heroInv = _previewHeroClone.GlobalTransform.AffineInverse();
            Vector3 hitHero = heroInv * hitWorld;
            Vector3 hitNormHero = (heroInv.Basis * hitNormalWorld);
            hitNormHero = hitNormHero.LengthSquared() > 1e-8f ? hitNormHero.Normalized() : Vector3.Up;

            Vector3 mirHitHero = new Vector3(-hitHero.X, hitHero.Y, hitHero.Z);
            Vector3 mirHitNormHero = new Vector3(-hitNormHero.X, hitNormHero.Y, hitNormHero.Z);

            float rSq = radius * radius;
            bool anyChanged = false;

            for (int si = 0; si < _deformSurfaceCaches.Count; si++)
            {
                var c = _deformSurfaceCaches[si];
                if (c.MeshInstance == null || !GodotObject.IsInstanceValid(c.MeshInstance) || !c.MeshInstance.Visible) continue;
                if (string.IsNullOrEmpty(c.DeformKey)) continue;

                if (!_deformSculptDeltas.TryGetValue(c.DeformKey, out var vertMap))
                {
                    vertMap = null;
                }

                Dictionary<int, Vector3> smoothSnapshot = (mode == CustomModelAutoRigger.HeroSculptBrushMode.Smooth && vertMap != null)
                    ? new Dictionary<int, Vector3>(vertMap)
                    : null;

                var hVerts = c.HeroVerts;
                var hNorms = c.HeroNorms;
                bool surfChanged = false;

                for (int v = 0; v < hVerts.Length; v++)
                {
                    Vector3 vp = hVerts[v];
                    float dSqPrimary = vp.DistanceSquaredTo(hitHero);
                    float dSqMirror = mirror ? vp.DistanceSquaredTo(mirHitHero) : float.MaxValue;
                    bool useMirror = dSqMirror < dSqPrimary;
                    float bestDSq = useMirror ? dSqMirror : dSqPrimary;
                    if (bestDSq >= rSq) continue;

                    float t = 1f - MathF.Sqrt(bestDSq) / radius;
                    float falloff = t * t * (3f - 2f * t);
                    if (falloff <= 0.0001f) continue;

                    Vector3 refNorm = useMirror ? mirHitNormHero : hitNormHero;
                    Vector3 curDelta = (vertMap != null && vertMap.TryGetValue(v, out var ex)) ? ex : Vector3.Zero;
                    Vector3 nextDelta = curDelta;

                    switch (mode)
                    {
                        case CustomModelAutoRigger.HeroSculptBrushMode.PushIn:
                        {
                            Vector3 dirIn = (-refNorm * 0.65f - hNorms[v] * 0.35f);
                            dirIn = dirIn.LengthSquared() > 1e-8f ? dirIn.Normalized() : -refNorm;
                            nextDelta = curDelta + dirIn * (strength * 0.016f * falloff);
                            break;
                        }
                        case CustomModelAutoRigger.HeroSculptBrushMode.PullOut:
                        {
                            Vector3 dirOut = (refNorm * 0.65f + hNorms[v] * 0.35f);
                            dirOut = dirOut.LengthSquared() > 1e-8f ? dirOut.Normalized() : refNorm;
                            nextDelta = curDelta + dirOut * (strength * 0.016f * falloff);
                            break;
                        }
                        case CustomModelAutoRigger.HeroSculptBrushMode.Flatten:
                        {
                            Vector3 pOrig = _deformFlattenPlaneLocked
                                ? (useMirror ? new Vector3(-_deformFlattenPlaneOriginHero.X, _deformFlattenPlaneOriginHero.Y, _deformFlattenPlaneOriginHero.Z) : _deformFlattenPlaneOriginHero)
                                : (useMirror ? mirHitHero : hitHero);
                            Vector3 pNorm = _deformFlattenPlaneLocked
                                ? (useMirror ? new Vector3(-_deformFlattenPlaneNormalHero.X, _deformFlattenPlaneNormalHero.Y, _deformFlattenPlaneNormalHero.Z) : _deformFlattenPlaneNormalHero)
                                : refNorm;
                            float signedDist = (vp - pOrig).Dot(pNorm);
                            Vector3 targetPos = vp - pNorm * signedDist;
                            nextDelta = curDelta + (targetPos - vp) * (strength * 0.42f * falloff);
                            break;
                        }
                        case CustomModelAutoRigger.HeroSculptBrushMode.Smooth:
                        {
                            int[] nbs = c.Neighbors[v];
                            if (nbs == null || nbs.Length == 0 || smoothSnapshot == null) continue;
                            Vector3 avg = Vector3.Zero;
                            for (int ni = 0; ni < nbs.Length; ni++)
                            {
                                if (smoothSnapshot.TryGetValue(nbs[ni], out var nd)) avg += nd;
                            }
                            avg /= nbs.Length;
                            nextDelta = curDelta.Lerp(avg, Math.Clamp(strength * 0.55f * falloff, 0f, 1f));
                            break;
                        }
                        case CustomModelAutoRigger.HeroSculptBrushMode.Erase:
                        {
                            if (curDelta.LengthSquared() < 1e-10f) continue;
                            nextDelta = curDelta * Math.Clamp(1f - strength * 0.55f * falloff, 0f, 1f);
                            break;
                        }
                    }

                    // Keep sculpt delta within sensible bounds (max 0.35m from rest) so vertices never shoot away
                    if (nextDelta.LengthSquared() > 0.35f * 0.35f)
                    {
                        nextDelta = nextDelta.Normalized() * 0.35f;
                    }

                    if (nextDelta.LengthSquared() < 1e-9f)
                    {
                        if (vertMap != null && vertMap.Remove(v))
                        {
                            surfChanged = true;
                        }
                    }
                    else
                    {
                        if (vertMap == null)
                        {
                            vertMap = new Dictionary<int, Vector3>();
                            _deformSculptDeltas[c.DeformKey] = vertMap;
                        }
                        vertMap[v] = nextDelta;
                        surfChanged = true;
                    }
                }

                if (vertMap != null && vertMap.Count == 0)
                {
                    _deformSculptDeltas.Remove(c.DeformKey);
                }

                if (surfChanged) anyChanged = true;
            }

            if (anyChanged)
            {
                ApplyPreviewHeroDeformation();
                UpdateDeformBrushStatusLabel();
            }
            return anyChanged;
        }

        #endregion

        #endregion
    }
}
