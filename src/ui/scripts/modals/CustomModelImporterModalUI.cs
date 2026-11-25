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
        private bool _hasActiveCompleteReplacement = false;
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
        private DirectionalLight3D _previewKeyLight;
        private DirectionalLight3D _previewFillLight;
        private DirectionalLight3D _previewRimLight;
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
        private readonly Dictionary<CheckBox, MeshThumbCard> _heroKeepCards = new();
        private readonly List<CheckBox> _importedSubmeshCheckboxes = new();
        private readonly Dictionary<CheckBox, MeshThumbCard> _importedSubmeshCards = new();
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

                if (IsPinEditActive())
                {
                    if (key.CtrlPressed && key.Keycode == Key.Z)
                    {
                        if (key.ShiftPressed) RedoReferenceRigEdit();
                        else UndoReferenceRigEdit();
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                    if (key.CtrlPressed && key.Keycode == Key.Y)
                    {
                        RedoReferenceRigEdit();
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                    if (_isGuidedPinPlacement && _guidedCurrentStepPlaced && (key.Keycode == Key.Enter || key.Keycode == Key.KpEnter))
                    {
                        AdvanceGuidedPinStep();
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                }

                var focusedControl = GetViewport()?.GuiGetFocusOwner();
                bool isTypingInTextField = focusedControl is LineEdit or TextEdit;

                if (IsMeshCutActive() && !isTypingInTextField)
                {
                    if (HandleMeshCutKeyShortcut(key))
                    {
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                }
                else if (IsWeightPaintActive() && !isTypingInTextField)
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
                    if (!key.CtrlPressed && key.Keycode == Key.H)
                    {
                        if (key.AltPressed) UnhideAllPaintIslands();
                        else if (key.ShiftPressed) SoloHoveredPaintIsland();
                        else HideHoveredPaintIsland();
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                    if (key.Keycode == Key.Escape && _isDraggingGradient)
                    {
                        _isDraggingGradient = false;
                        UpdateGradientGizmoVisual(Vector3.Zero, Vector3.Zero, false);
                        SetBottomStatus("Cancelled weight gradient drag.", StatusTone.Info);
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                }
                else if (IsImportedSculptActive() && !isTypingInTextField)
                {
                    if (key.CtrlPressed && key.Keycode == Key.Z && !key.ShiftPressed)
                    {
                        OnImportedSculptUndoPressed();
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                    if (!key.CtrlPressed && key.Keycode == Key.Bracketleft && _sliderImpSculptRadius != null)
                    {
                        _sliderImpSculptRadius.Value = Math.Max(_sliderImpSculptRadius.MinValue, _sliderImpSculptRadius.Value - 0.01);
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                    if (!key.CtrlPressed && key.Keycode == Key.Bracketright && _sliderImpSculptRadius != null)
                    {
                        _sliderImpSculptRadius.Value = Math.Min(_sliderImpSculptRadius.MaxValue, _sliderImpSculptRadius.Value + 0.01);
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                    if (!key.CtrlPressed && !key.AltPressed && key.Keycode == Key.H)
                    {
                        SetPinEditHeroVisMode((_pinEditHeroVisMode + 1) % 3);
                        SyncImportedSculptViewportHud();
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                    if (key.Keycode == Key.Escape)
                    {
                        if (_btnImpSculptToggle != null) _btnImpSculptToggle.ButtonPressed = false;
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
                else if (IsPinEditActive() && !isTypingInTextField)
                {
                    if (!key.CtrlPressed && !key.AltPressed && key.Keycode == Key.H)
                    {
                        SetPinEditHeroVisMode((_pinEditHeroVisMode + 1) % 3);
                        string modeName = _pinEditHeroVisMode switch { 0 => "Show", 1 => "Ghost", _ => "Hide" };
                        SetBottomStatus($"Original Hero Mesh: {modeName} (press H to cycle).", StatusTone.Info);
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

            if (_isGizmoEditingSyntheticChain && _selectedChain != null && _selectedChain.IsSynthetic && !IsWeightPaintActive())
            {
                RedrawSyntheticChainPreview();
                if (!_isGizmoDragging)
                {
                    UpdateGizmoTargetTransform();
                }
            }

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

            _btnStressTestPopover = new Button
            {
                Text = "Stress Test",
                ThemeTypeVariation = "ToolbarButton",
                ToggleMode = true,
                ButtonPressed = false,
                CustomMinimumSize = new Vector2(106, 32),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Open floating Look Up / Crouch / Walk Back physics stress-test controls"
            };
            SetButtonIcon(_btnStressTestPopover, "res://assets/at-icons/signal_wave.svg", 16);
            _btnStressTestPopover.Toggled += (open) =>
            {
                if (_boxStressTestPopover != null) _boxStressTestPopover.Visible = open;
            };
            tbRow1.AddChild(_btnStressTestPopover);

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
                    if (_isGizmoEditingSyntheticChain)
                    {
                        _synSavedAnimStateValid = false;
                        SetSyntheticChainGizmoActive(false);
                    }
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
            _chkBoneFilterCore = AddLegendFilterCheck("Core", new Color(0.25f, 0.88f, 0.98f), "Show/hide character core skeleton bones");
            _chkBoneFilterFingers = AddLegendFilterCheck("Fingers", new Color(1.0f, 0.80f, 0.25f), "Show/hide finger phalanx bones");
            _chkBoneFilterPhysics = AddLegendFilterCheck("Clothing/Jiggle", new Color(1.0f, 0.82f, 0.18f), "Show/hide imported clothing, hair, and jiggle physics bones");
            _chkBoneFilterAccessory = AddLegendFilterCheck("Accessory", new Color(0.95f, 0.35f, 0.90f), "Show/hide accessory attachment chain bones");
            _chkBoneFilterDisabled = AddLegendFilterCheck("Disabled", new Color(0.95f, 0.25f, 0.25f), "Show/hide disabled (right-clicked) bones");
            _chkBoneFilterVanilla = new CheckBox { ButtonPressed = false, Visible = false };
            _boneLegendRow.AddChild(_chkBoneFilterVanilla);

            // Explicit 3-way separator: Modded Only (default) | Vanilla Hero | Both
            var srcSep = new VSeparator { CustomMinimumSize = new Vector2(2, 18), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            _boneLegendRow.AddChild(srcSep);
            string[] srcModes = { "Modded", "Vanilla", "Both" };
            string[] srcTips =
            {
                "Show ONLY bones belonging to your modded/custom character (default).",
                "Show ONLY the original vanilla Deadlock hero skeleton.",
                "Show modded bones on top of dimmed vanilla hero bones."
            };
            for (int sm = 0; sm < 3; sm++)
            {
                int capturedMode = sm;
                var b = new Button
                {
                    Text = srcModes[sm],
                    ToggleMode = true,
                    ButtonPressed = sm == 0,
                    ThemeTypeVariation = sm == 0 ? "ImporterPrimaryButton" : "ToolbarButton",
                    CustomMinimumSize = new Vector2(58, 22),
                    TooltipText = srcTips[sm],
                    FocusMode = Control.FocusModeEnum.None
                };
                ImporterStyle.ApplyFontSize(b, ImporterStyle.FontHint);
                b.Pressed += () =>
                {
                    _boneSourceFilterMode = capturedMode;
                    for (int k = 0; k < 3; k++)
                    {
                        if (_boneSourceFilterBtns[k] == null) continue;
                        bool act = k == capturedMode;
                        _boneSourceFilterBtns[k].SetPressedNoSignal(act);
                        _boneSourceFilterBtns[k].ThemeTypeVariation = act ? "ImporterPrimaryButton" : "ToolbarButton";
                    }
                    if (_showBones) RedrawBoneWireframe();
                };
                _boneSourceFilterBtns[sm] = b;
                _boneLegendRow.AddChild(b);
            }
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
            BuildMeshCutViewportHud(viewportPanel);
            BuildImportedSculptViewportHud(viewportPanel);
            BuildReferenceRigViewportHud(viewportPanel);
            BuildSyntheticChainViewportHud(viewportPanel);
            BuildStressTestViewportPopover(viewportPanel);

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

            var btnToggleLight = new Button
            {
                Text = ImporterMemoryGuard.LightMode ? "Light: ON" : "Light",
                ThemeTypeVariation = "ToolbarButton",
                ToggleMode = true,
                ButtonPressed = ImporterMemoryGuard.LightMode,
                CustomMinimumSize = new Vector2(72, 34),
                TooltipText = "Toggle Light Mode (reduces preview resolution and switches rig updates to manual to save RAM)"
            };
            ImporterStyle.ApplyFontSize(btnToggleLight, ImporterStyle.FontHint);
            btnToggleLight.Toggled += (on) =>
            {
                ImporterMemoryGuard.LightMode = on;
                btnToggleLight.Text = on ? "Light: ON" : "Light";
                if (_subViewport != null) _subViewport.Msaa3D = on ? Viewport.Msaa.Disabled : Viewport.Msaa.Msaa2X;
                if (_subViewportContainer != null) _subViewportContainer.StretchShrink = on ? 2 : 1;
                if (_btnManualRigUpdate != null) _btnManualRigUpdate.Visible = on;
                UpdateFooterMemoryIndicator();
            };
            footerHBox.AddChild(btnToggleLight);

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
            _previewKeyLight = new DirectionalLight3D
            {
                LightColor = new Color(1.0f, 0.98f, 0.92f),
                LightEnergy = 1.25f,
                RotationDegrees = new Vector3(-35, 45, 0)
            };
            _previewWorldRoot.AddChild(_previewKeyLight);

            _previewFillLight = new DirectionalLight3D
            {
                LightColor = new Color(0.65f, 0.75f, 0.90f),
                LightEnergy = 0.65f,
                RotationDegrees = new Vector3(-15, -135, 0)
            };
            _previewWorldRoot.AddChild(_previewFillLight);

            _previewRimLight = new DirectionalLight3D
            {
                LightColor = new Color(0.88f, 0.92f, 1.0f),
                LightEnergy = 0.75f,
                RotationDegrees = new Vector3(25, 180, 0)
            };
            _previewWorldRoot.AddChild(_previewRimLight);

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
                ShowSelectionBox = false,
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

                if (HandleMeshCutInput(@event)) return;
                if (HandleWeightPaintInput(@event)) return;
                if (HandleImportedSculptInput(@event)) return;
                if (HandleHeroDeformBrushInput(@event)) return;
                if (HandlePinInput(@event)) return;
                if (TryHandleSyntheticJointLeftClick(@event))
                {
                    _isOrbiting = false;
                    GetViewport().SetInputAsHandled();
                    return;
                }
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
                        _isGuidedCameraLocked = false;
                        _orbitDist = Mathf.Max(0.25f, _orbitDist * 0.90f);
                        UpdateCamera();
                        GetViewport().SetInputAsHandled();
                    }
                    else if (mb.ButtonIndex == MouseButton.WheelDown)
                    {
                        _isGuidedCameraLocked = false;
                        _orbitDist = Mathf.Min(12f, _orbitDist * 1.10f);
                        UpdateCamera();
                        GetViewport().SetInputAsHandled();
                    }
                    else if (mb.ButtonIndex == MouseButton.Left || mb.ButtonIndex == MouseButton.Middle)
                    {
                        if (mb.Pressed)
                        {
                            // During 1-by-1 guided reference bone placement, block accidental left-drag orbit
                            // until the user explicitly moves the view (Middle-drag, Right-pan, or Scroll).
                            if (mb.ButtonIndex == MouseButton.Left && _isGuidedPinPlacement && _isGuidedCameraLocked)
                            {
                                _isOrbiting = false;
                                _isPanning = false;
                                GetViewport().SetInputAsHandled();
                                return;
                            }
                            if (mb.ButtonIndex == MouseButton.Middle)
                                _isGuidedCameraLocked = false;
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
                        if (mb.Pressed) _isGuidedCameraLocked = false;
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
    }
}
