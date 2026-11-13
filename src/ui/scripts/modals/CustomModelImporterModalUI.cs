using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Gizmo3DPlugin;
using DeadlockPlayground.Importer;
using DeadlockPlayground.Animation;
using DeadlockPlayground.Tools;

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
        [Signal] public delegate void RiggingConfirmedEventHandler();
        [Signal] public delegate void ModalCancelledEventHandler();

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
        private AnimationPlayer _previewAnimPlayer;
        private Node3D _previewModelRoot;
        private Node3D _previewModelInstance;
        private Node3D _previewRiggedContainer;
        private Camera3D _previewCamera;
        private MeshInstance3D _bonesDebugMesh;
        private StandardMaterial3D _ghostMaterial;
        private float _ghostOpacity = 0.35f;
        private bool _showBones = false;
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
        private OptionButton _optTestAnim;
        private Button _btnAnimPlayPause;
        private HSlider _sliderAnimTimeline;
        private Label _lblAnimTime;
        private Button _btnAnimLoop;
        private OptionButton _optAnimSpeed;

        // Tab 1: Align & Scale
        private Label _lblGeometryStats;
        private SpinBox _spinScale;
        private HSlider _sliderScale;
        private SpinBox _spinPosX, _spinPosY, _spinPosZ;
        private SpinBox _spinRotPitch, _spinRotYaw, _spinRotRoll;
        private Button _btnAutoFit;
        private Button _btnFlipYaw;
        private Button _btnSnapFeet;
        private Button _btnResetTransform;
        private OptionButton _optPosePreset;
        private Control _boxCustomArmAngle;
        private HSlider _sliderArmAngle;
        private Label _lblArmAngleVal;
        private Control _boxRigidBone;
        private OptionButton _optRigidBone;

        // Tab 2: Rigging & Bones
        private Label _lblRigStatusBadge;
        private OptionButton _optRiggingMethod;
        private Control _boxKnnParameters;
        private Control _boxPreRiggedMapping;
        private VBoxContainer _boxBoneMappingContainer;
        private readonly Dictionary<string, string> _userBoneRemap = new();
        private SpinBox _spinKnnRadius;
        private HSlider _sliderKnnRadius;
        private SpinBox _spinLaplacian;
        private CheckBox _chkExcludeClothHair;
        private CheckBox _chkKeepFingersRigid;

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

        // Rest Pose Full-Body Segment Registration Controls
        private CheckBox _chkAutoAlignArms;
        private CheckBox _chkAutoAlignLegs;
        private CheckBox _chkAutoAlignHead;
        private CheckBox _chkClampLimbStretching;
        private CheckBox _chkStabilizeNeckFloatingHead;

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
        private Button _btnSnapGripPalm;
        private Button _btnStraightenWeapon;
        private bool _isGizmoUpdatingFromUI = false;
        private bool _rigUpdatePending = false;
        private Vector3 _gizmoBaseBonePos = Vector3.Zero;

        // Submesh UI Card Controls Lookup (for bidirectional gizmo & preset synchronization)
        private readonly Dictionary<string, (SpinBox Pitch, SpinBox Yaw, SpinBox Roll, SpinBox PosX, SpinBox PosY, SpinBox PosZ, SpinBox Scale, OptionButton Socket, OptionButton Replaces, OptionButton Placement, OptionButton Role)> _submeshCardControls = new();

        // Footer Actions
        private Button _btnCancel;
        private Label _lblRigStatusChip;
        private Button _btnConfirmApply;

        public override void _Ready()
        {
            Layer = 110;
            BuildLayout();
        }

        public override void _Process(double delta)
        {
            base._Process(delta);
            if (_previewHeroSkeleton != null && _previewRiggedContainer != null && GodotObject.IsInstanceValid(_previewRiggedContainer))
            {
                bool clampStretch = _chkClampLimbStretching?.ButtonPressed ?? true;
                CustomModelAutoRigger.UpdateCustomModelRuntimePosing(_previewHeroSkeleton, clampStretch);
            }

            if (_previewAnimPlayer != null && _previewAnimPlayer.IsPlaying())
            {
                double cur = _previewAnimPlayer.CurrentAnimationPosition;
                double len = _previewAnimPlayer.CurrentAnimationLength;
                if (_sliderAnimTimeline != null && !_sliderAnimTimeline.HasFocus())
                {
                    _sliderAnimTimeline.SetValueNoSignal(cur);
                }
                if (_lblAnimTime != null)
                {
                    _lblAnimTime.Text = $"{cur:F2} / {len:F2}s";
                }
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

        private static void SetButtonIcon(Button btn, string relPath, int iconSize = 20, Color? iconColor = null)
        {
            var icon = LoadIcon(relPath);
            if (icon != null)
            {
                btn.Icon = icon;
                btn.ExpandIcon = true;
                btn.AddThemeConstantOverride("icon_max_width", iconSize);
                btn.AddThemeConstantOverride("h_separation", 8);
                if (iconColor.HasValue)
                {
                    btn.AddThemeColorOverride("icon_normal_color", iconColor.Value);
                    btn.AddThemeColorOverride("icon_hover_color", iconColor.Value.Lightened(0.2f));
                    btn.AddThemeColorOverride("icon_pressed_color", iconColor.Value.Darkened(0.2f));
                    btn.AddThemeColorOverride("icon_focus_color", iconColor.Value);
                }
            }
        }

        private void BuildLayout()
        {
            // Fullscreen Dimmed Backdrop
            var backdrop = new ColorRect
            {
                Name = "Backdrop",
                Color = new Color(0.04f, 0.05f, 0.07f, 0.78f),
                MouseFilter = Control.MouseFilterEnum.Stop
            };
            backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(backdrop);

            // Centered Modal Window
            var modalWindow = new PanelContainer
            {
                Name = "ModalWindow",
                CustomMinimumSize = new Vector2(850, 560)
            };
            modalWindow.AnchorLeft = 0.02f;
            modalWindow.AnchorRight = 0.98f;
            modalWindow.AnchorTop = 0.02f;
            modalWindow.AnchorBottom = 0.98f;
            modalWindow.OffsetLeft = 0;
            modalWindow.OffsetTop = 0;
            modalWindow.OffsetRight = 0;
            modalWindow.OffsetBottom = 0;

            var panelStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.10f, 0.11f, 0.14f, 0.98f),
                BorderColor = new Color(0.24f, 0.28f, 0.36f, 1.0f),
                BorderWidthLeft = 2,
                BorderWidthTop = 2,
                BorderWidthRight = 2,
                BorderWidthBottom = 2,
                CornerRadiusTopLeft = 10,
                CornerRadiusTopRight = 10,
                CornerRadiusBottomLeft = 10,
                CornerRadiusBottomRight = 10,
                ShadowColor = new Color(0, 0, 0, 0.70f),
                ShadowSize = 20
            };
            modalWindow.AddThemeStyleboxOverride("panel", panelStyle);
            backdrop.AddChild(modalWindow);

            var outerMargin = new MarginContainer();
            outerMargin.AddThemeConstantOverride("margin_left", 14);
            outerMargin.AddThemeConstantOverride("margin_top", 12);
            outerMargin.AddThemeConstantOverride("margin_right", 14);
            outerMargin.AddThemeConstantOverride("margin_bottom", 12);
            modalWindow.AddChild(outerMargin);

            var mainVBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            mainVBox.AddThemeConstantOverride("separation", 10);
            outerMargin.AddChild(mainVBox);

            // --- 1. HEADER BAR ---
            var headerHBox = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            headerHBox.AddThemeConstantOverride("separation", 10);

            var iconPerson = new TextureRect
            {
                Texture = LoadIcon("res://assets/at-icons/person_body.svg"),
                CustomMinimumSize = new Vector2(26, 26),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                Modulate = new Color(0.937255f, 0.870588f, 0.74902f, 1f)
            };
            headerHBox.AddChild(iconPerson);

            _lblTitle = new Label
            {
                Text = "CUSTOM 3D MODEL IMPORTER & AUTO-RIGGER",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            _lblTitle.AddThemeColorOverride("font_color", new Color(0.92f, 0.94f, 0.98f));
            headerHBox.AddChild(_lblTitle);

            _lblFileBadge = new Label
            {
                Text = "No file loaded",
                HorizontalAlignment = HorizontalAlignment.Right
            };
            _lblFileBadge.AddThemeColorOverride("font_color", new Color(0.65f, 0.70f, 0.80f));
            headerHBox.AddChild(_lblFileBadge);

            _btnCloseX = new Button
            {
                CustomMinimumSize = new Vector2(34, 34),
                TooltipText = "Cancel & Close Modal"
            };
            var closeNormal = new StyleBoxFlat
            {
                BgColor = new Color(0.16f, 0.18f, 0.22f, 0.85f),
                BorderColor = new Color(0.28f, 0.32f, 0.40f, 0.85f),
                BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
                CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
                ContentMarginLeft = 4, ContentMarginRight = 4, ContentMarginTop = 4, ContentMarginBottom = 4
            };
            var closeHover = (StyleBoxFlat)closeNormal.Duplicate();
            closeHover.BgColor = new Color(0.80f, 0.22f, 0.22f, 0.95f);
            closeHover.BorderColor = new Color(1.0f, 0.40f, 0.40f, 1.0f);
            _btnCloseX.AddThemeStyleboxOverride("normal", closeNormal);
            _btnCloseX.AddThemeStyleboxOverride("hover", closeHover);
            _btnCloseX.AddThemeStyleboxOverride("pressed", closeHover);
            SetButtonIcon(_btnCloseX, "res://assets/at-icons/cross.svg", 20, new Color(0.92f, 0.92f, 0.92f));
            _btnCloseX.Pressed += OnCancel;
            headerHBox.AddChild(_btnCloseX);
            mainVBox.AddChild(headerHBox);

            // --- 2. BODY SPLIT (3D PREVIEW on Left 60% | CONFIG INSPECTOR on Right 40%) ---
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
                SizeFlagsStretchRatio = 1.45f
            };
            previewVBox.AddThemeConstantOverride("separation", 6);
            bodySplit.AddChild(previewVBox);

            // 1. Top Preview Toolbar Panel (Dedicated compact 2-row header bar)
            var toolbarPanel = new PanelContainer
            {
                CustomMinimumSize = new Vector2(0, 68),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkBegin
            };
            var toolbarStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.08f, 0.09f, 0.12f, 0.95f),
                BorderColor = new Color(0.22f, 0.26f, 0.34f, 1.0f),
                BorderWidthLeft = 1,
                BorderWidthTop = 1,
                BorderWidthRight = 1,
                BorderWidthBottom = 1,
                CornerRadiusTopLeft = 6,
                CornerRadiusTopRight = 6,
                CornerRadiusBottomLeft = 6,
                CornerRadiusBottomRight = 6
            };
            toolbarPanel.AddThemeStyleboxOverride("panel", toolbarStyle);
            previewVBox.AddChild(toolbarPanel);

            var tbMargin = new MarginContainer();
            tbMargin.AddThemeConstantOverride("margin_left", 6);
            tbMargin.AddThemeConstantOverride("margin_top", 4);
            tbMargin.AddThemeConstantOverride("margin_right", 6);
            tbMargin.AddThemeConstantOverride("margin_bottom", 4);
            toolbarPanel.AddChild(tbMargin);

            var tbVBox = new VBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
            };
            tbVBox.AddThemeConstantOverride("separation", 4);
            tbMargin.AddChild(tbVBox);

            var tbRow1 = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            tbRow1.AddThemeConstantOverride("separation", 8);
            tbVBox.AddChild(tbRow1);

            var tbRow2 = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            tbRow2.AddThemeConstantOverride("separation", 8);
            tbVBox.AddChild(tbRow2);

            var tbBtnNormal = new StyleBoxFlat
            {
                BgColor = new Color(0.14f, 0.16f, 0.20f, 0.90f),
                BorderColor = new Color(0.26f, 0.30f, 0.38f, 0.90f),
                BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
                CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
                ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 4, ContentMarginBottom = 4
            };
            var tbBtnHover = (StyleBoxFlat)tbBtnNormal.Duplicate();
            tbBtnHover.BgColor = new Color(0.20f, 0.24f, 0.30f, 1.0f);
            tbBtnHover.BorderColor = new Color(0.38f, 0.46f, 0.58f, 1.0f);

            _btnToggleGhost = new Button
            {
                Text = "Hero Ghost",
                ToggleMode = true,
                ButtonPressed = true,
                CustomMinimumSize = new Vector2(110, 34),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Toggle Reference Hero Ghost Mannequin"
            };
            _btnToggleGhost.AddThemeStyleboxOverride("normal", tbBtnNormal);
            _btnToggleGhost.AddThemeStyleboxOverride("hover", tbBtnHover);
            SetButtonIcon(_btnToggleGhost, "res://assets/at-icons/eye.svg", 20, new Color(0.40f, 0.85f, 1.0f));
            _btnToggleGhost.Toggled += (t) =>
            {
                _showGhost = t;
                if (_previewHeroClone != null) _previewHeroClone.Visible = true;
                foreach (var mi in _previewGhostMeshes)
                {
                    if (GodotObject.IsInstanceValid(mi)) mi.Visible = t;
                }
                SetButtonIcon(_btnToggleGhost, t ? "res://assets/at-icons/eye.svg" : "res://assets/at-icons/eye_closed.svg", 20, t ? new Color(0.40f, 0.85f, 1.0f) : new Color(0.55f, 0.60f, 0.68f));
            };
            tbRow1.AddChild(_btnToggleGhost);

            _sliderGhostOpacity = new HSlider
            {
                MinValue = 0.05,
                MaxValue = 1.0,
                Step = 0.05,
                Value = 0.35,
                CustomMinimumSize = new Vector2(75, 0),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Ghost Mannequin Opacity"
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
                ToggleMode = true,
                ButtonPressed = false,
                CustomMinimumSize = new Vector2(85, 34),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Toggle Skeleton Bone Wireframe"
            };
            _btnToggleBones.AddThemeStyleboxOverride("normal", tbBtnNormal);
            _btnToggleBones.AddThemeStyleboxOverride("hover", tbBtnHover);
            SetButtonIcon(_btnToggleBones, "res://assets/at-icons/bone.svg", 20, new Color(1.0f, 0.85f, 0.30f));
            _btnToggleBones.Toggled += (t) =>
            {
                _showBones = t;
                if (_bonesDebugMesh != null) _bonesDebugMesh.Visible = t;
                if (t) RedrawBoneWireframe();
            };
            tbRow1.AddChild(_btnToggleBones);

            tbRow1.AddChild(new VSeparator());

            var lblAnim = new Label { Text = "Test Anim:" };
            lblAnim.AddThemeColorOverride("font_color", new Color(0.70f, 0.75f, 0.85f));
            tbRow1.AddChild(lblAnim);

            _optTestAnim = new OptionButton
            {
                CustomMinimumSize = new Vector2(170, 34),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Preview how the rigged model animates right inside this modal"
            };
            _optTestAnim.AddItem("Bind Pose (Rest)", 0);
            _optTestAnim.ItemSelected += OnTestAnimSelected;
            tbRow1.AddChild(_optTestAnim);

            // Transport: Play / Pause toggle
            _btnAnimPlayPause = new Button
            {
                ToggleMode = true,
                ButtonPressed = false,
                CustomMinimumSize = new Vector2(34, 34),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Play / Pause Animation"
            };
            _btnAnimPlayPause.AddThemeStyleboxOverride("normal", tbBtnNormal);
            _btnAnimPlayPause.AddThemeStyleboxOverride("hover", tbBtnHover);
            SetButtonIcon(_btnAnimPlayPause, "res://assets/at-icons/play.svg", 20, new Color(0.40f, 0.90f, 1.0f));
            _btnAnimPlayPause.Toggled += (playing) =>
            {
                if (_previewAnimPlayer == null) return;
                if (playing)
                {
                    if (!_previewAnimPlayer.IsPlaying())
                    {
                        if (!string.IsNullOrEmpty(_previewAnimPlayer.CurrentAnimation))
                        {
                            _previewAnimPlayer.Play();
                        }
                        else if (_optTestAnim.Selected > 0)
                        {
                            OnTestAnimSelected(_optTestAnim.Selected);
                        }
                    }
                    SetButtonIcon(_btnAnimPlayPause, "res://assets/at-icons/pause.svg", 20, new Color(0.40f, 0.90f, 1.0f));
                }
                else
                {
                    _previewAnimPlayer.Pause();
                    SetButtonIcon(_btnAnimPlayPause, "res://assets/at-icons/play.svg", 20, new Color(0.70f, 0.75f, 0.85f));
                }
            };
            tbRow1.AddChild(_btnAnimPlayPause);

            // Transport: Scrub Timeline Slider
            _sliderAnimTimeline = new HSlider
            {
                MinValue = 0.0,
                MaxValue = 1.0,
                Step = 0.01,
                Value = 0.0,
                CustomMinimumSize = new Vector2(90, 0),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Scrub Animation Timeline"
            };
            _sliderAnimTimeline.ValueChanged += (v) =>
            {
                if (_previewAnimPlayer != null && !string.IsNullOrEmpty(_previewAnimPlayer.CurrentAnimation))
                {
                    _previewAnimPlayer.Seek((float)v, update: true);
                    if (!_previewAnimPlayer.IsPlaying() && _lblAnimTime != null)
                    {
                        _lblAnimTime.Text = $"{v:F2} / {_previewAnimPlayer.CurrentAnimationLength:F2}s";
                    }
                }
            };
            tbRow1.AddChild(_sliderAnimTimeline);

            // Transport: Time Label
            _lblAnimTime = new Label { Text = "0.00s" };
            _lblAnimTime.AddThemeColorOverride("font_color", new Color(0.65f, 0.70f, 0.80f));
            _lblAnimTime.CustomMinimumSize = new Vector2(65, 0);
            tbRow1.AddChild(_lblAnimTime);

            // Transport: Loop Toggle
            _btnAnimLoop = new Button
            {
                ToggleMode = true,
                ButtonPressed = true,
                CustomMinimumSize = new Vector2(34, 34),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Loop Animation"
            };
            _btnAnimLoop.AddThemeStyleboxOverride("normal", tbBtnNormal);
            _btnAnimLoop.AddThemeStyleboxOverride("hover", tbBtnHover);
            SetButtonIcon(_btnAnimLoop, "res://assets/at-icons/arrows_clockwise.svg", 20, new Color(0.40f, 0.90f, 1.0f));
            _btnAnimLoop.Toggled += (loop) =>
            {
                SetButtonIcon(_btnAnimLoop, "res://assets/at-icons/arrows_clockwise.svg", 20, loop ? new Color(0.40f, 0.90f, 1.0f) : new Color(0.50f, 0.55f, 0.65f));
                if (_previewAnimPlayer != null && !string.IsNullOrEmpty(_previewAnimPlayer.CurrentAnimation))
                {
                    var anim = _previewAnimPlayer.GetAnimation(_previewAnimPlayer.CurrentAnimation);
                    if (anim != null)
                    {
                        anim.LoopMode = loop ? Godot.Animation.LoopModeEnum.Linear : Godot.Animation.LoopModeEnum.None;
                    }
                }
            };
            tbRow1.AddChild(_btnAnimLoop);

            // Transport: Speed Selector
            _optAnimSpeed = new OptionButton
            {
                CustomMinimumSize = new Vector2(65, 34),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Animation Playback Speed"
            };
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
            tbRow1.AddChild(_optAnimSpeed);

            // --- Row 2: Manipulators & Alignment Tools ---
            var lblGizmo = new Label { Text = "Gizmo:" };
            lblGizmo.AddThemeColorOverride("font_color", new Color(0.70f, 0.75f, 0.85f));
            tbRow2.AddChild(lblGizmo);

            _optGizmoTarget = new OptionButton
            {
                CustomMinimumSize = new Vector2(175, 34),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Select bone joint or weapon to control interactively in 3D viewport"
            };
            _optGizmoTarget.AddItem("Gizmo: Off", 0);
            _optGizmoTarget.AddItem("Weapon (R) [weapon_hand_R]", 1);
            _optGizmoTarget.AddItem("Weapon (L) [weapon_hand_L]", 2);
            _optGizmoTarget.ItemSelected += OnGizmoTargetChanged;
            tbRow2.AddChild(_optGizmoTarget);

            _btnSnapGripPalm = new Button
            {
                Text = "Snap Grip",
                CustomMinimumSize = new Vector2(115, 34),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Instantly aligns the active weapon grip into the hero's hand with forward rotation"
            };
            _btnSnapGripPalm.AddThemeStyleboxOverride("normal", tbBtnNormal);
            _btnSnapGripPalm.AddThemeStyleboxOverride("hover", tbBtnHover);
            SetButtonIcon(_btnSnapGripPalm, "res://assets/at-icons/sparkles.svg", 20, new Color(0.95f, 0.85f, 0.40f));
            _btnSnapGripPalm.Pressed += OnSnapGripToPalmPressed;
            tbRow2.AddChild(_btnSnapGripPalm);

            _btnStraightenWeapon = new Button
            {
                Text = "Straighten",
                CustomMinimumSize = new Vector2(115, 34),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                TooltipText = "Auto-detects barrel principal axis and straightens weapon forward (+Z)"
            };
            _btnStraightenWeapon.AddThemeStyleboxOverride("normal", tbBtnNormal);
            _btnStraightenWeapon.AddThemeStyleboxOverride("hover", tbBtnHover);
            SetButtonIcon(_btnStraightenWeapon, "res://assets/at-icons/sparkles.svg", 20, new Color(0.40f, 0.90f, 1.0f));
            _btnStraightenWeapon.Pressed += OnStraightenWeaponPressed;
            tbRow2.AddChild(_btnStraightenWeapon);

            // 2. 3D Preview Viewport Panel (occupies all remaining space below toolbar)
            var viewportPanel = new PanelContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(400, 350)
            };
            var viewportStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.06f, 0.07f, 0.09f, 1.0f),
                BorderColor = new Color(0.18f, 0.22f, 0.28f, 1.0f),
                BorderWidthLeft = 1,
                BorderWidthTop = 1,
                BorderWidthRight = 1,
                BorderWidthBottom = 1,
                CornerRadiusTopLeft = 6,
                CornerRadiusTopRight = 6,
                CornerRadiusBottomLeft = 6,
                CornerRadiusBottomRight = 6
            };
            viewportPanel.AddThemeStyleboxOverride("panel", viewportStyle);
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

            // Right Side: Tabbed Configuration Inspector
            var rightPanel = new PanelContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                SizeFlagsStretchRatio = 1.0f
            };
            var rightStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.09f, 0.10f, 0.13f, 1.0f),
                BorderColor = new Color(0.20f, 0.24f, 0.30f, 1.0f),
                BorderWidthLeft = 1,
                BorderWidthTop = 1,
                BorderWidthRight = 1,
                BorderWidthBottom = 1,
                CornerRadiusTopLeft = 6,
                CornerRadiusTopRight = 6,
                CornerRadiusBottomLeft = 6,
                CornerRadiusBottomRight = 6
            };
            rightPanel.AddThemeStyleboxOverride("panel", rightStyle);
            bodySplit.AddChild(rightPanel);

            var tabs = new TabContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill
            };
            rightPanel.AddChild(tabs);

            tabs.AddChild(BuildTab1AlignAndScale());
            tabs.AddChild(BuildTab2RiggingAndBones());
            tabs.AddChild(BuildTab3SubmeshesAndMaterials());

            // --- 3. BOTTOM FOOTER BAR ---
            var footerHBox = new HBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 42)
            };
            footerHBox.AddThemeConstantOverride("separation", 12);
            mainVBox.AddChild(footerHBox);

            _lblBottomStatus = new Label
            {
                Text = "Ready to align, test rig, or apply.",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            _lblBottomStatus.AddThemeColorOverride("font_color", new Color(0.65f, 0.75f, 0.85f));
            footerHBox.AddChild(_lblBottomStatus);

            _btnCancel = new Button
            {
                Text = "Cancel",
                CustomMinimumSize = new Vector2(105, 36)
            };
            SetButtonIcon(_btnCancel, "res://assets/at-icons/cross.svg", 20, new Color(0.95f, 0.45f, 0.45f));
            _btnCancel.Pressed += OnCancel;
            footerHBox.AddChild(_btnCancel);

            _lblRigStatusChip = new Label
            {
                Text = "Live Rig: Ready",
                VerticalAlignment = VerticalAlignment.Center,
                CustomMinimumSize = new Vector2(130, 36)
            };
            _lblRigStatusChip.AddThemeColorOverride("font_color", new Color(0.40f, 0.95f, 0.55f));
            footerHBox.AddChild(_lblRigStatusChip);

            _btnConfirmApply = new Button
            {
                Text = "Confirm & Apply to Hero",
                CustomMinimumSize = new Vector2(225, 36),
                TooltipText = "Applies rigging and binding onto the live hero in the main viewport"
            };
            _btnConfirmApply.AddThemeColorOverride("font_color", new Color(0.40f, 0.95f, 0.55f));
            SetButtonIcon(_btnConfirmApply, "res://assets/at-icons/checkmark.svg", 20, new Color(0.40f, 0.95f, 0.55f));
            _btnConfirmApply.Pressed += OnConfirmApplyToHero;
            footerHBox.AddChild(_btnConfirmApply);
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
            _bonesDebugMesh = new MeshInstance3D { Name = "BonesDebugMesh", Visible = false };
            _previewWorldRoot.AddChild(_bonesDebugMesh);

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

            _previewGizmo.TransformChanged += OnGizmoTransformChanged;
        }

        private void SetupPreviewInputHandling()
        {
            _subViewportContainer.GuiInput += (InputEvent @event) =>
            {
                // Forward input to SubViewport for Gizmo3D
                _subViewport.PushInput(@event);

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
            if (_previewHeroSkeleton == null || _bonesDebugMesh == null) return;

            var im = new ImmediateMesh();
            im.SurfaceBegin(Mesh.PrimitiveType.Lines);

            int bCount = _previewHeroSkeleton.GetBoneCount();
            for (int b = 1; b < bCount; b++)
            {
                int p = _previewHeroSkeleton.GetBoneParent(b);
                if (p < 0) continue;

                Vector3 p0 = _previewHeroSkeleton.GetBoneGlobalPose(b).Origin;
                Vector3 p1 = _previewHeroSkeleton.GetBoneGlobalPose(p).Origin;

                im.SurfaceSetColor(new Color(1.0f, 0.85f, 0.20f, 0.9f));
                im.SurfaceAddVertex(p0);
                im.SurfaceAddVertex(p1);
            }

            im.SurfaceEnd();

            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha
            };
            _bonesDebugMesh.Mesh = im;
            _bonesDebugMesh.MaterialOverride = mat;
        }

        #endregion

        #region Tab 1: Align & Scale

        private Control BuildTab1AlignAndScale()
        {
            var scroll = new ScrollContainer
            {
                Name = "1. Align & Scale",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };

            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 12);
            margin.AddThemeConstantOverride("margin_top", 12);
            margin.AddThemeConstantOverride("margin_right", 12);
            margin.AddThemeConstantOverride("margin_bottom", 12);
            margin.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            scroll.AddChild(margin);

            var vBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            vBox.AddThemeConstantOverride("separation", 10);
            margin.AddChild(vBox);

            _lblGeometryStats = new Label
            {
                Text = "Geometry: 0 vertices | 0 triangles",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            _lblGeometryStats.AddThemeColorOverride("font_color", new Color(0.70f, 0.75f, 0.85f));
            vBox.AddChild(_lblGeometryStats);

            // Quick Auto-Fit Buttons
            var fitHBox = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            fitHBox.AddThemeConstantOverride("separation", 8);

            _btnAutoFit = new Button
            {
                Text = "Auto-Fit Height & Center",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 32)
            };
            SetButtonIcon(_btnAutoFit, "res://assets/at-icons/sparkles.svg", 20, new Color(0.95f, 0.85f, 0.40f));
            _btnAutoFit.Pressed += AutoFitToHero;
            fitHBox.AddChild(_btnAutoFit);

            _btnFlipYaw = new Button
            {
                Text = "Flip 180Â°",
                CustomMinimumSize = new Vector2(95, 32),
                TooltipText = "Rotates model 180Â° around Y axis (Yaw) to match hero facing direction"
            };
            SetButtonIcon(_btnFlipYaw, "res://assets/at-icons/rotate.svg", 18, new Color(0.70f, 0.85f, 1.0f));
            _btnFlipYaw.Pressed += () =>
            {
                double cur = _spinRotYaw.Value;
                double next = (cur + 180.0) % 360.0;
                if (next > 180.0) next -= 360.0;
                if (next < -180.0) next += 360.0;
                _spinRotYaw.Value = next;
                ApplyTransformToPreviewModel();
            };
            fitHBox.AddChild(_btnFlipYaw);

            _btnSnapFeet = new Button
            {
                Text = "Snap Feet (Y=0)",
                CustomMinimumSize = new Vector2(125, 32)
            };
            SetButtonIcon(_btnSnapFeet, "res://assets/at-icons/arrow_down.svg", 20, new Color(0.75f, 0.85f, 0.95f));
            _btnSnapFeet.Pressed += SnapFeetToFloor;
            fitHBox.AddChild(_btnSnapFeet);

            _btnResetTransform = new Button
            {
                Text = "Reset",
                CustomMinimumSize = new Vector2(75, 32)
            };
            SetButtonIcon(_btnResetTransform, "res://assets/at-icons/arrow_counterclockwise.svg", 20, new Color(0.75f, 0.80f, 0.85f));
            _btnResetTransform.Pressed += ResetTransform;
            fitHBox.AddChild(_btnResetTransform);

            vBox.AddChild(fitHBox);
            vBox.AddChild(new HSeparator());

            // Uniform Scale
            var lblScale = new Label { Text = "Uniform Scale Factor:" };
            vBox.AddChild(lblScale);

            var scaleHBox = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _spinScale = new SpinBox
            {
                MinValue = 0.0001,
                MaxValue = 500.0,
                Step = 0.001,
                Value = 1.0,
                CustomMinimumSize = new Vector2(90, 0)
            };
            _spinScale.ValueChanged += (v) =>
            {
                if (!Mathf.IsEqualApprox((float)_sliderScale.Value, (float)v))
                {
                    _sliderScale.Value = v;
                }
                ApplyTransformToPreviewModel();
            };
            scaleHBox.AddChild(_spinScale);

            _sliderScale = new HSlider
            {
                MinValue = 0.001,
                MaxValue = 10.0,
                Step = 0.001,
                Value = 1.0,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
            };
            _sliderScale.ValueChanged += (v) =>
            {
                if (!Mathf.IsEqualApprox((float)_spinScale.Value, (float)v))
                {
                    _spinScale.Value = v;
                }
                ApplyTransformToPreviewModel();
            };
            scaleHBox.AddChild(_sliderScale);
            vBox.AddChild(scaleHBox);

            // Position Offset (X, Y, Z meters)
            var lblPos = new Label { Text = "Position Offset (X, Y, Z meters):" };
            vBox.AddChild(lblPos);

            var posHBox = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _spinPosX = CreateCoordSpinBox(-50.0, 50.0, 0.01, "X");
            _spinPosY = CreateCoordSpinBox(-50.0, 50.0, 0.01, "Y");
            _spinPosZ = CreateCoordSpinBox(-50.0, 50.0, 0.01, "Z");
            posHBox.AddChild(_spinPosX);
            posHBox.AddChild(_spinPosY);
            posHBox.AddChild(_spinPosZ);
            vBox.AddChild(posHBox);

            _spinPosX.ValueChanged += (_) => ApplyTransformToPreviewModel();
            _spinPosY.ValueChanged += (_) => ApplyTransformToPreviewModel();
            _spinPosZ.ValueChanged += (_) => ApplyTransformToPreviewModel();

            // Rotation (Pitch, Yaw, Roll degrees)
            var lblRot = new Label { Text = "Rotation (Pitch, Yaw, Roll degrees):" };
            vBox.AddChild(lblRot);

            var rotHBox = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _spinRotPitch = CreateCoordSpinBox(-180.0, 180.0, 1.0, "Pitch");
            _spinRotYaw = CreateCoordSpinBox(-180.0, 180.0, 1.0, "Yaw");
            _spinRotRoll = CreateCoordSpinBox(-180.0, 180.0, 1.0, "Roll");
            rotHBox.AddChild(_spinRotPitch);
            rotHBox.AddChild(_spinRotYaw);
            rotHBox.AddChild(_spinRotRoll);
            vBox.AddChild(rotHBox);

            _spinRotPitch.ValueChanged += (_) => ApplyTransformToPreviewModel();
            _spinRotYaw.ValueChanged += (_) => ApplyTransformToPreviewModel();
            _spinRotRoll.ValueChanged += (_) => ApplyTransformToPreviewModel();

            vBox.AddChild(new HSeparator());

            // Source Pose Preset
            var lblPreset = new Label { Text = "Source Mesh Pose Preset:" };
            vBox.AddChild(lblPreset);

            _optPosePreset = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _optPosePreset.AddItem("Match Hero Bind Pose (A-Pose)", (int)CustomModelAutoRigger.PosePreset.MatchHeroBindPose);
            _optPosePreset.AddItem("T-Pose (Horizontal Arms -> Auto-Conform to Hero)", (int)CustomModelAutoRigger.PosePreset.TPose);
            _optPosePreset.AddItem("A-Pose (Custom Arm Pitch Angle)", (int)CustomModelAutoRigger.PosePreset.APoseCustomAngle);
            _optPosePreset.AddItem("Rigid Attachment to Single Bone (Hat / Weapon / Prop)", (int)CustomModelAutoRigger.PosePreset.RigidSingleBone);
            _optPosePreset.Select(0);
            _optPosePreset.ItemSelected += OnPosePresetChanged;
            vBox.AddChild(_optPosePreset);

            // Custom Arm Angle Slider Box
            _boxCustomArmAngle = new VBoxContainer { Visible = false };
            var armHBox = new HBoxContainer();
            var lblArm = new Label { Text = "Arm Pitch Angle:", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            armHBox.AddChild(lblArm);

            _lblArmAngleVal = new Label { Text = "0Â°" };
            armHBox.AddChild(_lblArmAngleVal);
            _boxCustomArmAngle.AddChild(armHBox);

            _sliderArmAngle = new HSlider
            {
                MinValue = -60.0,
                MaxValue = 60.0,
                Step = 1.0,
                Value = 0.0
            };
            _sliderArmAngle.ValueChanged += (v) =>
            {
                _lblArmAngleVal.Text = $"{v:0}Â°";
            };
            _boxCustomArmAngle.AddChild(_sliderArmAngle);
            vBox.AddChild(_boxCustomArmAngle);

            // Rigid Bone Box
            _boxRigidBone = new VBoxContainer { Visible = false };
            var lblRigid = new Label { Text = "Target Hero Attachment Bone:" };
            _boxRigidBone.AddChild(lblRigid);

            _optRigidBone = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _boxRigidBone.AddChild(_optRigidBone);
            vBox.AddChild(_boxRigidBone);

            vBox.AddChild(new HSeparator());

            // Auto-Snap Full-Body Segment Registrations
            var limbVBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            limbVBox.AddThemeConstantOverride("separation", 4);

            _chkAutoAlignArms = new CheckBox
            {
                Text = "Auto-Snap Arms & Wrists to Hero Skeleton",
                ButtonPressed = true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Aligns source arm segments (shoulder, elbow, wrist) to Deadlock hero rest pivots in Hero Space. Prevents wrist breaking and aligns weapon to palm."
            };
            _chkAutoAlignArms.Toggled += (v) => TriggerRealtimeRigUpdate();
            limbVBox.AddChild(_chkAutoAlignArms);

            _chkAutoAlignLegs = new CheckBox
            {
                Text = "Auto-Snap Legs & Feet to Hero Skeleton",
                ButtonPressed = true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Aligns source leg segments (thigh, calf, ankle) to Deadlock hero athletic stance in Hero Space, keeping soles flat on the floor (Y >= 0)."
            };
            _chkAutoAlignLegs.Toggled += (v) => TriggerRealtimeRigUpdate();
            limbVBox.AddChild(_chkAutoAlignLegs);

            _chkAutoAlignHead = new CheckBox
            {
                Text = "Auto-Snap Torso, Neck & Head to Hero Skeleton",
                ButtonPressed = true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Aligns source neck and head to Deadlock hero cervical pivot in Hero Space with clamped tilt angle, keeping head upright."
            };
            _chkAutoAlignHead.Toggled += (v) => TriggerRealtimeRigUpdate();
            limbVBox.AddChild(_chkAutoAlignHead);

            _chkClampLimbStretching = new CheckBox
            {
                Text = "Clamp Limb Bone Stretching in Animations (Prevents Melee Over-Extension)",
                ButtonPressed = true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Clamps animated limb bone translation displacement (shoulders, elbows, wrists, knees) to 1.5 cm maximum, preserving 100% rotational arcs while preventing melee swings from stretching arms apart."
            };
            _chkClampLimbStretching.Toggled += (v) => TriggerRealtimeRigUpdate();
            limbVBox.AddChild(_chkClampLimbStretching);

            _chkStabilizeNeckFloatingHead = new CheckBox
            {
                Text = "Stabilize Neck Against Floating-Head Stretch (Bind Head/Neck to Cervical Pivot)",
                ButtonPressed = _heroCodename == "chrono",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Binds head and neck vertices to the cervical pivot (neck_0) so floating-head heroes (like Paradox/Chrono) don't pull or stretch the neck during jumping or zipline animations in-game."
            };
            _chkStabilizeNeckFloatingHead.Toggled += (v) => TriggerRealtimeRigUpdate();
            limbVBox.AddChild(_chkStabilizeNeckFloatingHead);

            vBox.AddChild(limbVBox);

            return scroll;
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

        #region Tab 2: Rigging & Bones

        private Control BuildTab2RiggingAndBones()
        {
            var scroll = new ScrollContainer
            {
                Name = "2. Rigging & Bones",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };

            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 12);
            margin.AddThemeConstantOverride("margin_top", 12);
            margin.AddThemeConstantOverride("margin_right", 12);
            margin.AddThemeConstantOverride("margin_bottom", 12);
            margin.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            scroll.AddChild(margin);

            var vBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            vBox.AddThemeConstantOverride("separation", 10);
            margin.AddChild(vBox);

            _lblRigStatusBadge = new Label
            {
                Text = "Analyzing Model Rig Status..."
            };
            _lblRigStatusBadge.AddThemeColorOverride("font_color", new Color(0.95f, 0.85f, 0.40f));
            vBox.AddChild(_lblRigStatusBadge);

            vBox.AddChild(new HSeparator());

            // Rigging Method Selector
            var lblMethod = new Label { Text = "Rigging Method:" };
            vBox.AddChild(lblMethod);

            _optRiggingMethod = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _optRiggingMethod.AddItem("Option B: Map Existing Imported Skeleton Bones (Pre-Rigged Remap - Recommended)", (int)CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap);
            _optRiggingMethod.AddItem("Option C: Auto-Transfer Weights from Hero Body (Sanitized KNN - for Unrigged Models)", (int)CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection);
            _optRiggingMethod.AddItem("Option A: Rigid Attachment to Single Bone (Hat / Weapon / Prop)", (int)CustomModelAutoRigger.RiggingMethod.RigidSingleBone);
            _optRiggingMethod.Select(0);
            _optRiggingMethod.ItemSelected += OnRiggingMethodChanged;
            vBox.AddChild(_optRiggingMethod);

            vBox.AddChild(new HSeparator());

            // KNN Parameters Box
            _boxKnnParameters = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _boxKnnParameters.AddThemeConstantOverride("separation", 8);

            var lblTransferTitle = new Label { Text = "Unrigged Skinning / Weight Transfer Parameters:" };
            _boxKnnParameters.AddChild(lblTransferTitle);

            // KNN Search Radius
            var lblKnn = new Label { Text = "KNN Donor Search Radius (meters):" };
            lblKnn.AddThemeColorOverride("font_color", new Color(0.70f, 0.75f, 0.85f));
            _boxKnnParameters.AddChild(lblKnn);

            var knnHBox = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _spinKnnRadius = new SpinBox
            {
                MinValue = 0.05,
                MaxValue = 0.80,
                Step = 0.01,
                Value = 0.20,
                CustomMinimumSize = new Vector2(80, 0)
            };
            _spinKnnRadius.ValueChanged += (v) =>
            {
                if (!Mathf.IsEqualApprox((float)_sliderKnnRadius.Value, (float)v))
                {
                    _sliderKnnRadius.Value = v;
                }
            };
            knnHBox.AddChild(_spinKnnRadius);

            _sliderKnnRadius = new HSlider
            {
                MinValue = 0.05,
                MaxValue = 0.80,
                Step = 0.01,
                Value = 0.20,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
            };
            _sliderKnnRadius.ValueChanged += (v) =>
            {
                if (!Mathf.IsEqualApprox((float)_spinKnnRadius.Value, (float)v))
                {
                    _spinKnnRadius.Value = v;
                }
            };
            knnHBox.AddChild(_sliderKnnRadius);
            _boxKnnParameters.AddChild(knnHBox);

            // Laplacian Smoothing Iterations
            var lblLap = new Label { Text = "Laplacian Smoothing Iterations (0..5):" };
            lblLap.AddThemeColorOverride("font_color", new Color(0.70f, 0.75f, 0.85f));
            _boxKnnParameters.AddChild(lblLap);

            _spinLaplacian = new SpinBox
            {
                MinValue = 0,
                MaxValue = 5,
                Step = 1,
                Value = 3
            };
            _boxKnnParameters.AddChild(_spinLaplacian);

            _chkExcludeClothHair = new CheckBox
            {
                Text = "Exclude Cloth & Hair Donor Bones (Protects character body)",
                ButtonPressed = true
            };
            _boxKnnParameters.AddChild(_chkExcludeClothHair);

            vBox.AddChild(_boxKnnParameters);

            // Pre-Rigged Mapping Box (in ScrollContainer)
            _boxPreRiggedMapping = new VBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                Visible = false
            };
            _boxPreRiggedMapping.AddThemeConstantOverride("separation", 8);

            var lblMapHeader = new Label { Text = "Humanoid Bone Remapping Table (Pre-Rigged Models):" };
            _boxPreRiggedMapping.AddChild(lblMapHeader);

            _chkKeepFingersRigid = new CheckBox
            {
                Text = "Keep Fingers Rigid on Hand (Prevents Finger Twisting)",
                ButtonPressed = false
            };
            _chkKeepFingersRigid.Toggled += (_) => TriggerRealtimeRigUpdate();
            _boxPreRiggedMapping.AddChild(_chkKeepFingersRigid);

            var boneScroll = new ScrollContainer
            {
                CustomMinimumSize = new Vector2(0, 220),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };
            _boxBoneMappingContainer = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _boxBoneMappingContainer.AddThemeConstantOverride("separation", 6);
            boneScroll.AddChild(_boxBoneMappingContainer);
            _boxPreRiggedMapping.AddChild(boneScroll);

            vBox.AddChild(_boxPreRiggedMapping);

            return scroll;
        }

        private void OnRiggingMethodChanged(long index)
        {
            var method = _optRiggingMethod.Selected >= 0
                ? (CustomModelAutoRigger.RiggingMethod)_optRiggingMethod.GetItemId(_optRiggingMethod.Selected)
                : CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection;

            _boxKnnParameters.Visible = (method == CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection);
            _boxPreRiggedMapping.Visible = (method == CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap);
        }

        #endregion

        #region Tab 3: Submeshes & Materials

        private Control BuildTab3SubmeshesAndMaterials()
        {
            var scroll = new ScrollContainer
            {
                Name = "3. Submeshes & Materials",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };

            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 12);
            margin.AddThemeConstantOverride("margin_top", 12);
            margin.AddThemeConstantOverride("margin_right", 12);
            margin.AddThemeConstantOverride("margin_bottom", 12);
            margin.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            scroll.AddChild(margin);

            var vBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            vBox.AddThemeConstantOverride("separation", 10);
            margin.AddChild(vBox);

            var lblMode = new Label { Text = "Character Replacement Mode:" };
            vBox.AddChild(lblMode);

            _optReplaceMode = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _optReplaceMode.AddItem("Full Character Replacement (Hide original hero submeshes)", (int)CustomModelAutoRigger.ReplaceMode.FullReplacement);
            _optReplaceMode.AddItem("Accessory / Partial Outfit (Keep selected original hero parts)", (int)CustomModelAutoRigger.ReplaceMode.PartialAccessory);
            _optReplaceMode.Select(0);
            vBox.AddChild(_optReplaceMode);

            vBox.AddChild(new HSeparator());

            var lblImported = new Label { Text = "Imported 3D Model Submeshes:" };
            vBox.AddChild(lblImported);

            _chkIncludeBakedOutlines = new CheckBox
            {
                Text = "Include Baked Outline Shells (Toggle all [OUTLINE SHELL] submeshes)",
                ButtonPressed = true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Toggles inclusion of all submeshes flagged as Inverted-Hull Outline Shells with one click."
            };
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
                TriggerRealtimeRigUpdate();
            };
            vBox.AddChild(_chkIncludeBakedOutlines);

            _chkLinkWeaponSubmeshes = new CheckBox
            {
                Text = "Link All Weapon / Socket Submeshes Together (Sync Bone, Transform & Scale)",
                ButtonPressed = true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "When enabled, changing bone, replacement target, rotation, position, or scale on any weapon submesh automatically synchronizes all other weapon submeshes."
            };
            vBox.AddChild(_chkLinkWeaponSubmeshes);

            _boxImportedSubmeshes = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            vBox.AddChild(_boxImportedSubmeshes);

            vBox.AddChild(new HSeparator());

            var lblHeroParts = new Label { Text = "Keep Visible from Original Deadlock Hero:" };
            vBox.AddChild(lblHeroParts);

            _boxHeroSubmeshes = new VBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                Visible = true
            };
            vBox.AddChild(_boxHeroSubmeshes);

            return scroll;
        }

        #endregion

        #region Public Open & Data Binding

        public void Open(Node3D heroNode, Node3D importedScene, string filePath, CustomModelAutoRigger.RiggingOptions initialOptions = null)
        {
            _liveHeroNode = heroNode;
            _liveImportedScene = importedScene;
            _filePath = filePath;
            _heroCodename = heroNode?.Name.ToString().ToLowerInvariant().Replace("hero_", "").Trim() ?? "";

            string fileName = Path.GetFileName(filePath);
            _lblFileBadge.Text = $"File: {fileName}";

            if (_liveImportedScene == null || _liveHeroNode == null)
            {
                _lblBottomStatus.Text = "Error: Invalid model or hero reference.";
                return;
            }

            // 1. Create a clean reference clone of the hero in the isolated SubViewport
            // CRITICAL: Strip SkeletonGizmoManager and non-authentic gizmo meshes BEFORE adding to tree
            // to prevent 200+ C++ "Parent node is busy setting up children, add_child() failed" errors!
            _previewHeroClone = (Node3D)_liveHeroNode.Duplicate();
            StripGizmosAndNonAuthenticNodes(_previewHeroClone);
            _previewWorldRoot.AddChild(_previewHeroClone);

            // Detach/stop any AnimationPlayer so it never animates into combat stance
            _previewAnimPlayer = null;
            foreach (var child in _previewHeroClone.FindChildren("*", "AnimationPlayer", true, false))
            {
                if (child is AnimationPlayer ap)
                {
                    ap.Stop();
                    _previewAnimPlayer = ap;
                }
            }

            // CRITICAL REQUIREMENT 2: Force 100% symmetrical bind pose (ResetBonePoses) on reference hero clone!
            _previewHeroSkeleton = CustomModelAutoRigger.FindSkeleton(_previewHeroClone);
            if (_previewHeroSkeleton != null)
            {
                _previewHeroSkeleton.ResetBonePoses();
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

            ApplyGhostMaterialToClone(_previewHeroClone);

            // 2. Setup imported preview model root and clone
            _previewModelRoot = new Node3D { Name = "PreviewModelRoot" };
            _previewWorldRoot.AddChild(_previewModelRoot);

            _previewModelInstance = (Node3D)_liveImportedScene.Duplicate();
            _previewModelRoot.AddChild(_previewModelInstance);

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

            // 6. Analyze rig status and bone mapper
            AnalyzeModelRigStatus();

            if (initialOptions != null)
            {
                RestoreRiggingOptions(initialOptions);
            }
            else
            {
                // 7. CRITICAL REQUIREMENT 3: Automatic First-Frame World-Space Auto-Fit Height & Camera Frame!
                AutoFitToHero();
            }
        }

        private void RestoreRiggingOptions(CustomModelAutoRigger.RiggingOptions options)
        {
            if (options == null) return;

            // 1. Restore Transform
            if (options.RootScale > 0.0001f)
            {
                _spinScale.Value = options.RootScale;
                _sliderScale.Value = options.RootScale;
                _spinPosX.Value = options.RootPosition.X;
                _spinPosY.Value = options.RootPosition.Y;
                _spinPosZ.Value = options.RootPosition.Z;
                _spinRotPitch.Value = options.RootRotationDeg.X;
                _spinRotYaw.Value = options.RootRotationDeg.Y;
                _spinRotRoll.Value = options.RootRotationDeg.Z;
            }
            else if (options.AlignedTransform != Transform3D.Identity)
            {
                _previewModelRoot.Transform = options.AlignedTransform;
                Vector3 s = options.AlignedTransform.Basis.Scale;
                _spinScale.Value = s.X;
                _sliderScale.Value = s.X;
                _spinPosX.Value = options.AlignedTransform.Origin.X;
                _spinPosY.Value = options.AlignedTransform.Origin.Y;
                _spinPosZ.Value = options.AlignedTransform.Origin.Z;
                Vector3 rot = options.AlignedTransform.Basis.GetEuler();
                _spinRotPitch.Value = Mathf.RadToDeg(rot.X);
                _spinRotYaw.Value = Mathf.RadToDeg(rot.Y);
                _spinRotRoll.Value = Mathf.RadToDeg(rot.Z);
            }

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
                if (_lblArmAngleVal != null) _lblArmAngleVal.Text = $"{options.CustomArmAngleDeg:0}Â°";
            }
            if (!string.IsNullOrEmpty(options.RigidBoneName) && _optRigidBone != null)
            {
                for (int i = 0; i < _optRigidBone.ItemCount; i++)
                {
                    if (_optRigidBone.GetItemText(i).Replace("â˜… ", "") == options.RigidBoneName)
                    {
                        _optRigidBone.Select(i);
                        break;
                    }
                }
            }
            if (_chkAutoAlignArms != null) _chkAutoAlignArms.ButtonPressed = options.AutoSnapArmsToHero;
            if (_chkAutoAlignLegs != null) _chkAutoAlignLegs.ButtonPressed = options.AutoSnapLegsToHero;
            if (_chkAutoAlignHead != null) _chkAutoAlignHead.ButtonPressed = options.AutoSnapHeadToHero;
            if (_chkClampLimbStretching != null) _chkClampLimbStretching.ButtonPressed = options.ClampLimbStretching;
            if (_chkStabilizeNeckFloatingHead != null) _chkStabilizeNeckFloatingHead.ButtonPressed = options.StabilizeNeckFloatingHead;

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

            // 4. Restore Replace Mode
            for (int i = 0; i < _optReplaceMode.ItemCount; i++)
            {
                if (_optReplaceMode.GetItemId(i) == (int)options.Mode)
                {
                    _optReplaceMode.Select(i);
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
                        chk.ButtonPressed = options.IncludedImportedSubmeshNames.Contains(sName);
                    }
                }
            }
            if (options.KeptOriginalSubmeshNames != null && options.KeptOriginalSubmeshNames.Count > 0)
            {
                foreach (var chk in _heroKeepCheckboxes)
                {
                    if (chk.HasMeta("SubmeshName"))
                    {
                        string sName = chk.GetMeta("SubmeshName").AsString();
                        chk.ButtonPressed = options.KeptOriginalSubmeshNames.Contains(sName);
                    }
                }
            }

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
                        if (ctrl.Socket.GetItemText(i).Replace("â˜… ", "") == bName)
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

            ApplyTransformToPreviewModel();
            TriggerRealtimeRigUpdate();
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
                mi.Visible = _showGhost;
                mi.MaterialOverride = _ghostMaterial;
                for (int s = 0; s < (mi.Mesh?.GetSurfaceCount() ?? 0); s++)
                {
                    mi.SetSurfaceOverrideMaterial(s, _ghostMaterial);
                }
                mi.Layers &= ~(uint)(1 << 20); // Clear Layer 21
            }
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
                    SetButtonIcon(_btnAnimPlayPause, "res://assets/at-icons/play.svg", 20, new Color(0.70f, 0.75f, 0.85f));
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

                _optTestAnim.AddSeparator($"â”€â”€ {title} â”€â”€");
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
                AddCategoryGroup("â˜… Stress Test (Deformation)", stressTests);
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
                    _optRigidBone.AddItem($"â˜… {p}", bIdx);
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

            // Original Deadlock Hero submeshes (strictly authentic, zero gizmo meshes!)
            var heroMeshes = CustomModelAutoRigger.CollectOriginalHeroMeshes(_liveHeroNode);
            foreach (var mi in heroMeshes)
            {
                var chk = new CheckBox
                {
                    Text = mi.Name,
                    ButtonPressed = false
                };
                chk.SetMeta("SubmeshName", mi.Name.ToString());
                _heroKeepCheckboxes.Add(chk);
                _boxHeroSubmeshes.AddChild(chk);
            }

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

                // Build interactive submesh card
                var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                var cardStyle = new StyleBoxFlat
                {
                    BgColor = new Color(0.13f, 0.15f, 0.18f, 0.70f),
                    BorderWidthBottom = 1,
                    BorderWidthTop = 1,
                    BorderWidthLeft = 1,
                    BorderWidthRight = 1,
                    BorderColor = new Color(0.24f, 0.28f, 0.35f, 0.40f),
                    CornerRadiusBottomLeft = 4,
                    CornerRadiusBottomRight = 4,
                    CornerRadiusTopLeft = 4,
                    CornerRadiusTopRight = 4,
                    ContentMarginLeft = 8,
                    ContentMarginRight = 8,
                    ContentMarginTop = 6,
                    ContentMarginBottom = 6
                };
                card.AddThemeStyleboxOverride("panel", cardStyle);

                var cardVBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                cardVBox.AddThemeConstantOverride("separation", 4);
                card.AddChild(cardVBox);

                // Row 1: Include Checkbox + [OUTLINE SHELL] Badge + Manual Outline Shell Toggle
                bool isOutlineShell = CustomModelAutoRigger.IsLikelyOutlineShell(mi);
                _submeshIsOutlineShell[rawMeshName] = isOutlineShell;

                var headerRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                headerRow.AddThemeConstantOverride("separation", 6);
                cardVBox.AddChild(headerRow);

                var chk = new CheckBox
                {
                    Text = $"{rawMeshName} ({vCount:N0} verts | {tCount:N0} tris)",
                    ButtonPressed = isOutlineShell ? (_chkIncludeBakedOutlines?.ButtonPressed ?? true) : true,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };
                chk.SetMeta("SubmeshName", rawMeshName);
                chk.Toggled += (v) => { mi.Visible = v; TriggerRealtimeRigUpdate(); };
                _importedSubmeshCheckboxes.Add(chk);
                headerRow.AddChild(chk);

                var lblOutlineBadge = new Label
                {
                    Text = "[OUTLINE SHELL]",
                    Visible = isOutlineShell
                };
                lblOutlineBadge.AddThemeColorOverride("font_color", new Color(0.35f, 0.88f, 0.98f));
                _submeshOutlineBadges[rawMeshName] = lblOutlineBadge;
                headerRow.AddChild(lblOutlineBadge);

                var chkOutlineShell = new CheckBox
                {
                    Text = "Outline Hull",
                    ButtonPressed = isOutlineShell,
                    TooltipText = "Mark or unmark this submesh as an Inverted-Hull Outline Shell (single-sided backface culling, unlit, no extra toon outline)."
                };
                chkOutlineShell.Toggled += (isOut) =>
                {
                    _submeshIsOutlineShell[rawMeshName] = isOut;
                    lblOutlineBadge.Visible = isOut;
                    mi.SetMeta("IsOutlineShell", isOut);
                    TriggerRealtimeRigUpdate();
                };
                _submeshOutlineCheckboxes[rawMeshName] = chkOutlineShell;
                headerRow.AddChild(chkOutlineShell);

                // Row 2: Role selection (Skinned Body vs Socket / Weapon)
                var roleRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                roleRow.AddThemeConstantOverride("separation", 6);
                cardVBox.AddChild(roleRow);

                var lblRole = new Label
                {
                    Text = "Role:",
                    CustomMinimumSize = new Vector2(40, 0)
                };
                lblRole.AddThemeColorOverride("font_color", new Color(0.65f, 0.70f, 0.78f));
                roleRow.AddChild(lblRole);

                var optRole = new OptionButton
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };
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
                lblSocket.AddThemeColorOverride("font_color", new Color(0.65f, 0.70f, 0.78f));
                socketRow.AddChild(lblSocket);

                var optSocket = new OptionButton
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };

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
                lblReplaces.AddThemeColorOverride("font_color", new Color(0.65f, 0.70f, 0.78f));
                socketRow.AddChild(lblReplaces);

                var optReplaces = new OptionButton
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };
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
                lblPlacement.AddThemeColorOverride("font_color", new Color(0.65f, 0.70f, 0.78f));
                placementRow.AddChild(lblPlacement);

                var optPlacement = new OptionButton
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };
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
                var lblRot = new Label { Text = "Rot (Â°):", CustomMinimumSize = new Vector2(40, 0) };
                lblRot.AddThemeColorOverride("font_color", new Color(0.65f, 0.70f, 0.78f));
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
                lblPos.AddThemeColorOverride("font_color", new Color(0.65f, 0.70f, 0.78f));
                posRow.AddChild(lblPos);

                var spinOffX = CreateCoordSpinBox(-1.0, 1.0, 0.01, "X");
                var spinOffY = CreateCoordSpinBox(-1.0, 1.0, 0.01, "Y");
                var spinOffZ = CreateCoordSpinBox(-1.0, 1.0, 0.01, "Z");
                posRow.AddChild(spinOffX);
                posRow.AddChild(spinOffY);
                posRow.AddChild(spinOffZ);
                weaponTransformBox.AddChild(posRow);

                // Scale & Preset Buttons
                var presetRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                presetRow.AddThemeConstantOverride("separation", 4);

                var lblWpScale = new Label { Text = "Scale:", CustomMinimumSize = new Vector2(40, 0) };
                lblWpScale.AddThemeColorOverride("font_color", new Color(0.65f, 0.70f, 0.78f));
                presetRow.AddChild(lblWpScale);

                var spinWpScale = new SpinBox { MinValue = 0.1, MaxValue = 5.0, Step = 0.05, Value = 1.0, CustomMinimumSize = new Vector2(65, 0) };
                presetRow.AddChild(spinWpScale);

                var btnHandGrip = new Button { Text = "Grip Forward", CustomMinimumSize = new Vector2(75, 24), TooltipText = "Sets forward hand grip rotation (Pitch 90Â°)" };
                presetRow.AddChild(btnHandGrip);

                var btnStraightenCard = new Button { Text = "Straighten", CustomMinimumSize = new Vector2(65, 24), TooltipText = "Auto-aligns weapon barrel forward (+Z)" };
                presetRow.AddChild(btnStraightenCard);

                var btnYaw90 = new Button { Text = "+90Â° Y", CustomMinimumSize = new Vector2(48, 24), TooltipText = "Rotate 90Â° Yaw" };
                presetRow.AddChild(btnYaw90);

                var btnPitch90 = new Button { Text = "+90Â° P", CustomMinimumSize = new Vector2(48, 24), TooltipText = "Rotate 90Â° Pitch" };
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

                    if (!_isGizmoUpdatingFromUI) TriggerRealtimeRigUpdate();
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
                    string chosenBone = optSocket.GetItemText(optSocket.Selected).Replace("â˜… ", "");
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
                        string bName = optSocket.GetItemText(optSocket.Selected).Replace("â˜… ", "");
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
                        string bName = optSocket.GetItemText((int)idx).Replace("â˜… ", "");
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

                _boxImportedSubmeshes.AddChild(card);
            }

            _lblGeometryStats.Text = $"Geometry: {importedMeshes.Count} submesh(es) | {totalVerts:N0} vertices | {totalTris:N0} triangles";
        }

        private void AnalyzeModelRigStatus()
        {
            var sourceSkeleton = CustomModelAutoRigger.FindSkeleton(_previewModelInstance);
            foreach (Node child in _boxBoneMappingContainer.GetChildren())
            {
                child.QueueFree();
            }
            _userBoneRemap.Clear();

            if (sourceSkeleton != null && sourceSkeleton.GetBoneCount() > 0)
            {
                _lblRigStatusBadge.Text = $"Pre-Rigged Skeleton Detected ({sourceSkeleton.GetBoneCount()} bones) | Pre-Rigged Bone Remap (Option B) Recommended";
                _lblRigStatusBadge.AddThemeColorOverride("font_color", new Color(0.40f, 0.90f, 0.50f));

                for (int i = 0; i < _optRiggingMethod.ItemCount; i++)
                {
                    if (_optRiggingMethod.GetItemId(i) == (int)CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap)
                    {
                        _optRiggingMethod.Select(i);
                        OnRiggingMethodChanged(i);
                        break;
                    }
                }

                // Populate interactive dropdowns for ALL humanoid bone definitions
                foreach (var def in HumanoidBoneMapper.Definitions)
                {
                    var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                    row.AddThemeConstantOverride("separation", 8);

                    var lblTarget = new Label
                    {
                        Text = $"{def.DisplayLabel}:",
                        CustomMinimumSize = new Vector2(150, 26)
                    };
                    row.AddChild(lblTarget);

                    var optBone = new OptionButton
                    {
                        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                        CustomMinimumSize = new Vector2(0, 26)
                    };
                    optBone.AddItem("[Auto-Collapse to Parent]", -1);

                    for (int b = 0; b < sourceSkeleton.GetBoneCount(); b++)
                    {
                        optBone.AddItem(sourceSkeleton.GetBoneName(b), b);
                    }

                    // Best initial match
                    string matchedBoneName = HumanoidBoneMapper.ResolveSourceBone(sourceSkeleton, def);
                    if (!string.IsNullOrEmpty(matchedBoneName))
                    {
                        int matchedBoneIdx = sourceSkeleton.FindBone(matchedBoneName);
                        for (int item = 1; item < optBone.ItemCount; item++)
                        {
                            if (optBone.GetItemId(item) == matchedBoneIdx)
                            {
                                optBone.Select(item);
                                break;
                            }
                        }
                        _userBoneRemap[def.CanonicalName] = matchedBoneName;
                    }
                    else
                    {
                        optBone.Select(0);
                    }

                    string boneTargetKey = def.CanonicalName;
                    optBone.ItemSelected += (long itemIdx) =>
                    {
                        long bIdx = optBone.GetItemId((int)itemIdx);
                        if (bIdx >= 0 && bIdx < sourceSkeleton.GetBoneCount())
                        {
                            _userBoneRemap[boneTargetKey] = sourceSkeleton.GetBoneName((int)bIdx);
                        }
                        else
                        {
                            _userBoneRemap.Remove(boneTargetKey);
                        }
                    };

                    row.AddChild(optBone);
                    _boxBoneMappingContainer.AddChild(row);
                }
            }
            else
            {
                _lblRigStatusBadge.Text = "Unrigged Static Mesh (Option C: Sanitized KNN Donor Cloud)";
                _lblRigStatusBadge.AddThemeColorOverride("font_color", new Color(0.95f, 0.75f, 0.35f));

                for (int i = 0; i < _optRiggingMethod.ItemCount; i++)
                {
                    if (_optRiggingMethod.GetItemId(i) == (int)CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection)
                    {
                        _optRiggingMethod.Select(i);
                        OnRiggingMethodChanged(i);
                        break;
                    }
                }

                var lblInfo = new Label
                {
                    Text = "Vertex weights will be transferred automatically from Deadlock's donor cloud using CPU Linear Blend Skinning and Laplacian smoothing.",
                    AutowrapMode = TextServer.AutowrapMode.WordSmart
                };
                lblInfo.AddThemeColorOverride("font_color", new Color(0.65f, 0.70f, 0.80f));
                _boxBoneMappingContainer.AddChild(lblInfo);
            }
        }

        #endregion

        #region Alignment, Scaling & Math (Exact World Vertex Bounding Box)

        /// <summary>
        /// Computes exact World-Space Vertex Bounding Box for hero and model using pristine unscaled bounds,
        /// scales to match height, centers X and Z, and plants feet on the exact floor plane (Y = 0).
        /// </summary>
        public void AutoFitToHero()
        {
            if (_previewHeroClone == null || _previewModelRoot == null || _previewModelInstance == null) return;

            // Ensure reference hero clone is in bind pose
            _previewHeroSkeleton?.ResetBonePoses();

            var (heroMin, heroMax, heroOk) = CustomModelAutoRigger.ComputeAuthenticHeroBounds(_previewHeroClone);
            var (modelMin, modelMax, modelOk) = CustomModelAutoRigger.ComputePristineModelBounds(_previewModelInstance);

            float heroHeight = heroOk ? (heroMax.Y - Math.Max(0.0f, heroMin.Y)) : 2.45f;
            float modelHeight = modelOk ? (modelMax.Y - modelMin.Y) : 1.0f;

            if (modelHeight < 0.0001f)
            {
                _spinScale.Value = 1.0;
                _spinPosX.Value = 0.0;
                _spinPosY.Value = 0.0;
                _spinPosZ.Value = 0.0;
                ApplyTransformToPreviewModel();
                return;
            }

            // 1. Calculate Uniform Scale
            float scale = heroHeight / modelHeight;
            _spinScale.Value = Math.Round(scale, 4);

            // 2. Default glTF character faces -Z in Godot, whereas Deadlock hero faces +Z.
            // Setting Yaw to 180Â° aligns both characters facing forward towards the camera!
            _spinRotPitch.Value = 0.0;
            _spinRotYaw.Value = 180.0;
            _spinRotRoll.Value = 0.0;

            // 3. Calculate Center X and Z (under Yaw = 180Â°, local X -> -X and local Z -> -Z)
            Vector3 heroCenter = (heroMin + heroMax) * 0.5f;
            Vector3 modelCenter = (modelMin + modelMax) * 0.5f;

            float posX = heroCenter.X - (-modelCenter.X * scale);
            float posZ = heroCenter.Z - (-modelCenter.Z * scale);

            // 4. Plant feet strictly on the ground floor plane (Y = 0.0f)
            float posY = 0.0f - (modelMin.Y * scale);

            _spinPosX.Value = Math.Round(posX, 3);
            _spinPosY.Value = Math.Round(posY, 3);
            _spinPosZ.Value = Math.Round(posZ, 3);

            ApplyTransformToPreviewModel();

            // 5. Frame Camera on the Hero and Model
            _orbitTarget = new Vector3(heroCenter.X, (heroMin.Y + heroMax.Y) * 0.5f, heroCenter.Z);
            _orbitDist = Mathf.Max(heroHeight * 1.65f, 2.4f);
            _orbitPitch = 0.12f;
            _orbitYaw = 0.0f;
            UpdateCamera();

            _lblBottomStatus.Text = $"Auto-Fit applied: Scale={scale:F3}x | Pos=({posX:F2}, {posY:F2}, {posZ:F2}) | Yaw=180Â°";
            TriggerRealtimeRigUpdate();
        }

        private void SnapFeetToFloor()
        {
            if (_previewModelInstance == null || _previewHeroClone == null) return;
            var (heroMin, _, _) = CustomModelAutoRigger.ComputeAuthenticHeroBounds(_previewHeroClone);
            var (modelMin, _, _) = CustomModelAutoRigger.ComputePristineModelBounds(_previewModelInstance);

            float scale = (float)_spinScale.Value;
            float posY = 0.0f - (modelMin.Y * scale);
            _spinPosY.Value = Math.Round(posY, 3);
            ApplyTransformToPreviewModel();
        }

        private void ResetTransform()
        {
            _spinScale.Value = 1.0;
            _spinPosX.Value = 0.0;
            _spinPosY.Value = 0.0;
            _spinPosZ.Value = 0.0;
            _spinRotPitch.Value = 0.0;
            _spinRotYaw.Value = 0.0;
            _spinRotRoll.Value = 0.0;
            ApplyTransformToPreviewModel();
        }


        private void ApplyTransformToPreviewModel()
        {
            if (_previewModelRoot == null) return;

            float scale = (float)_spinScale.Value;
            Vector3 pos = new Vector3((float)_spinPosX.Value, (float)_spinPosY.Value, (float)_spinPosZ.Value);
            Vector3 rot = new Vector3((float)_spinRotPitch.Value, (float)_spinRotYaw.Value, (float)_spinRotRoll.Value);

            Transform3D t = Transform3D.Identity;
            t = t.Scaled(new Vector3(scale, scale, scale));
            t = t.Rotated(Vector3.Up, Mathf.DegToRad(rot.Y));
            t = t.Rotated(Vector3.Right, Mathf.DegToRad(rot.X));
            t = t.Rotated(Vector3.Forward, Mathf.DegToRad(rot.Z));
            t.Origin = pos;

            _previewModelRoot.Transform = t;
            TriggerRealtimeRigUpdate();
        }

        private void OnPosePresetChanged(long index)
        {
            var preset = _optPosePreset.Selected >= 0 ? (CustomModelAutoRigger.PosePreset)_optPosePreset.GetItemId(_optPosePreset.Selected) : CustomModelAutoRigger.PosePreset.TPose;
            _boxCustomArmAngle.Visible = preset == CustomModelAutoRigger.PosePreset.APoseCustomAngle;
            _boxRigidBone.Visible = preset == CustomModelAutoRigger.PosePreset.RigidSingleBone;
            TriggerRealtimeRigUpdate();
        }

        #endregion

        #region Actions: Real-Time Test Rig & Confirm

        private void OnTestAnimSelected(long index)
        {
            if (_previewHeroSkeleton == null) return;

            if (index == 0) // Bind Pose (Rest)
            {
                _previewAnimPlayer?.Stop();
                _previewHeroSkeleton.ResetBonePoses();
                if (_sliderAnimTimeline != null) _sliderAnimTimeline.Value = 0;
                if (_lblAnimTime != null) _lblAnimTime.Text = "0.00 / 0.00s";
                if (_btnAnimPlayPause != null)
                {
                    _btnAnimPlayPause.ButtonPressed = false;
                    SetButtonIcon(_btnAnimPlayPause, "res://assets/at-icons/play.svg", 20, new Color(0.70f, 0.75f, 0.85f));
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
                    _sliderAnimTimeline.MaxValue = Math.Max(0.01f, length);
                    _sliderAnimTimeline.Value = 0.0;
                }
                if (_lblAnimTime != null)
                {
                    _lblAnimTime.Text = $"0.00 / {length:F2}s";
                }
                if (_btnAnimPlayPause != null)
                {
                    _btnAnimPlayPause.ButtonPressed = true;
                    SetButtonIcon(_btnAnimPlayPause, "res://assets/at-icons/pause.svg", 20, new Color(0.40f, 0.90f, 1.0f));
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

            return new CustomModelAutoRigger.RiggingOptions
            {
                Method = method,
                Preset = _optPosePreset.Selected >= 0 ? (CustomModelAutoRigger.PosePreset)_optPosePreset.GetItemId(_optPosePreset.Selected) : CustomModelAutoRigger.PosePreset.TPose,
                CustomArmAngleDeg = (float)_sliderArmAngle.Value,
                RigidBoneName = _optRigidBone.Selected >= 0 ? _optRigidBone.GetItemText(_optRigidBone.Selected).Replace("â˜… ", "") : "",
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
                KeepFingersRigidOnHand = _chkKeepFingersRigid?.ButtonPressed ?? false,
                AutoSnapArmsToHero = _chkAutoAlignArms?.ButtonPressed ?? true,
                AutoSnapLegsToHero = _chkAutoAlignLegs?.ButtonPressed ?? true,
                AutoSnapHeadToHero = _chkAutoAlignHead?.ButtonPressed ?? true,
                ClampLimbStretching = _chkClampLimbStretching?.ButtonPressed ?? true,
                StabilizeNeckFloatingHead = _chkStabilizeNeckFloatingHead?.ButtonPressed ?? (_heroCodename == "chrono"),
                RootPosition = new Vector3((float)_spinPosX.Value, (float)_spinPosY.Value, (float)_spinPosZ.Value),
                RootRotationDeg = new Vector3((float)_spinRotPitch.Value, (float)_spinRotYaw.Value, (float)_spinRotRoll.Value),
                RootScale = (float)_spinScale.Value
            };
        }

        private void TriggerRealtimeRigUpdate()
        {
            if (_rigUpdatePending) return;
            _rigUpdatePending = true;
            if (_lblRigStatusChip != null)
            {
                _lblRigStatusChip.Text = "Live Rig: Updating...";
                _lblRigStatusChip.AddThemeColorOverride("font_color", new Color(0.95f, 0.75f, 0.30f));
            }
            Callable.From(ExecuteLiveRigUpdate).CallDeferred();
        }

        private void ExecuteLiveRigUpdate()
        {
            _rigUpdatePending = false;
            if (_lblRigStatusChip != null)
            {
                _lblRigStatusChip.Text = "Live Rig: Up to date";
                _lblRigStatusChip.AddThemeColorOverride("font_color", new Color(0.40f, 0.95f, 0.55f));
            }
            if (_previewHeroClone == null || _previewHeroSkeleton == null || _liveImportedScene == null) return;

            // 1. Capture current animation time and playback state
            double currentAnimPos = 0.0;
            string currentAnimName = null;
            bool wasPlaying = false;
            float animSpeed = 1.0f;
            if (_previewAnimPlayer != null && _optTestAnim.Selected > 0)
            {
                currentAnimName = _optTestAnim.GetItemText(_optTestAnim.Selected);
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
            if (GodotObject.IsInstanceValid(_previewModelInstance))
            {
                _previewModelInstance.QueueFree();
            }
            _previewModelInstance = (Node3D)_liveImportedScene.Duplicate();
            _previewModelRoot.AddChild(_previewModelInstance);

            // Re-apply preview model root transform
            float scale = (float)_spinScale.Value;
            Vector3 pos = new Vector3((float)_spinPosX.Value, (float)_spinPosY.Value, (float)_spinPosZ.Value);
            Vector3 rot = new Vector3((float)_spinRotPitch.Value, (float)_spinRotYaw.Value, (float)_spinRotRoll.Value);

            Transform3D t = Transform3D.Identity;
            t = t.Scaled(new Vector3(scale, scale, scale));
            t = t.Rotated(Vector3.Up, Mathf.DegToRad(rot.Y));
            t = t.Rotated(Vector3.Right, Mathf.DegToRad(rot.X));
            t = t.Rotated(Vector3.Forward, Mathf.DegToRad(rot.Z));
            t.Origin = pos;
            _previewModelRoot.Transform = t;

            var options = GatherRiggingOptions();
            options.AlignedTransform = _previewModelRoot.Transform;
            options.IsPreviewOnly = true;

            var result = CustomModelAutoRigger.RigAndBind(_previewModelInstance, _previewHeroClone, options);
            if (result.Success)
            {
                _previewRiggedContainer = result.ContainerNode;
                _previewModelRoot.Visible = false;

                CustomModelAutoRigger.UpdateCustomModelRuntimePosing(_previewHeroSkeleton, options.ClampLimbStretching);

                ApplyGhostMaterialToClone(_previewHeroClone);
                if (_previewHeroClone != null) _previewHeroClone.Visible = true;
                foreach (var mi in _previewGhostMeshes)
                {
                    if (GodotObject.IsInstanceValid(mi)) mi.Visible = _showGhost;
                }

                _lblBottomStatus.Text = $"Rigged in real time ({result.TotalVertices:N0} vertices bound).";
                _lblBottomStatus.AddThemeColorOverride("font_color", new Color(0.40f, 0.95f, 0.55f));

                // Resume animation smoothly at exact position
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

                UpdateGizmoTargetTransform();
            }
            else
            {
                _previewModelRoot.Visible = true;
                _lblBottomStatus.Text = $"Preview Rigging Failed: {result.ErrorMessage}";
                _lblBottomStatus.AddThemeColorOverride("font_color", new Color(0.95f, 0.40f, 0.40f));
            }
        }


        private void OnGizmoTargetChanged(long index)
        {
            UpdateGizmoTargetTransform();
        }

        private void UpdateGizmoTargetTransform()
        {
            if (_previewGizmo == null || _previewHeroSkeleton == null) return;
            int target = _optGizmoTarget?.Selected ?? 0;

            if (target == 0) // Off
            {
                _previewGizmo.Visible = false;
                if (_gizmoDummyTarget != null && GodotObject.IsInstanceValid(_gizmoDummyTarget))
                {
                    _previewGizmo.Deselect(_gizmoDummyTarget);
                }
                return;
            }

            _isGizmoUpdatingFromUI = true;

            if (target == 1 || target == 2) // Weapon R or L
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
                }
            }
            if (_gizmoDummyTarget != null && !_previewGizmo.IsSelected(_gizmoDummyTarget))
            {
                _previewGizmo.Select(_gizmoDummyTarget);
            }
            _previewGizmo.Visible = true;
            _isGizmoUpdatingFromUI = false;
        }

        private void OnGizmoTransformChanged(int mode, Vector3 value)
        {
            if (_isGizmoUpdatingFromUI || _previewGizmo == null || _gizmoDummyTarget == null) return;
            int target = _optGizmoTarget?.Selected ?? 0;
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
                        ctrl.Pitch.Value = Math.Round(rot.X, 1);
                        ctrl.Yaw.Value = Math.Round(rot.Y, 1);
                        ctrl.Roll.Value = Math.Round(rot.Z, 1);
                        ctrl.PosX.Value = Math.Round(pos.X, 3);
                        ctrl.PosY.Value = Math.Round(pos.Y, 3);
                        ctrl.PosZ.Value = Math.Round(pos.Z, 3);
                    }
                    _submeshSocketOffsetRot[meshName] = rot;
                    _submeshSocketOffsetPos[meshName] = pos;
                }
                _isGizmoUpdatingFromUI = false;
                TriggerRealtimeRigUpdate();
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
                _lblBottomStatus.Text = "No weapon submeshes found to snap. Check Tab 3: Submeshes & Materials.";
                _lblBottomStatus.AddThemeColorOverride("font_color", new Color(0.95f, 0.75f, 0.40f));
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
            _lblBottomStatus.Text = $"Snapped {targetMeshes.Count} weapon(s) grip to palm forward (PCA-aligned).";
            _lblBottomStatus.AddThemeColorOverride("font_color", new Color(0.40f, 0.95f, 0.55f));
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
                _lblBottomStatus.Text = "No weapon submeshes found to straighten. Check Tab 3: Submeshes & Materials.";
                _lblBottomStatus.AddThemeColorOverride("font_color", new Color(0.95f, 0.75f, 0.40f));
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

            _lblBottomStatus.Text = $"Straightened {targetMeshes.Count} weapon mesh(es) intrinsic to barrel (0Â° Pitch/Yaw/Roll, aligned with Gizmo +Z Forward).";
            _lblBottomStatus.AddThemeColorOverride("font_color", new Color(0.40f, 0.95f, 0.55f));

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
            if (_liveHeroNode == null || _liveImportedScene == null)
            {
                OnCancel();
                return;
            }

            _lblBottomStatus.Text = "Binding custom model to live hero...";

            var options = GatherRiggingOptions();
            // CRITICAL: Use local Transform, NEVER GlobalTransform, so scale and rotation are fully preserved!
            options.AlignedTransform = _previewModelRoot.Transform;
            LastRiggingOptions = options;

            // Apply rigging and skinning directly to live hero in the main viewport!
            var result = CustomModelAutoRigger.RigAndBind(_liveImportedScene, _liveHeroNode, options);
            if (result.Success)
            {
                // Ensure replaced weapon meshes on the live hero are hidden so both guns don't render together
                var liveMeshes = CustomModelAutoRigger.CollectOriginalHeroMeshes(_liveHeroNode);
                foreach (var mi in liveMeshes)
                {
                    if (mi.HasMeta("IsCustomImportedMesh")) continue;
                    string rawName = mi.Name.ToString();
                    bool isReplaced = false;
                    if (options.SubmeshReplacementMap != null)
                    {
                        foreach (var repTarget in options.SubmeshReplacementMap.Values)
                        {
                            if (string.IsNullOrEmpty(repTarget) || repTarget == "[ None ]") continue;
                            if (rawName.Equals(repTarget, StringComparison.OrdinalIgnoreCase))
                            {
                                isReplaced = true;
                                break;
                            }
                            string repLower = repTarget.ToLowerInvariant();
                            string rawLower = rawName.ToLowerInvariant();
                            if (repLower.Contains("gun") && rawLower.Contains("gun"))
                            {
                                if (rawLower.Contains("hip") || rawLower.Contains("holster") || rawLower.Contains("back") || rawLower.Contains("sheath"))
                                {
                                    isReplaced = true;
                                    break;
                                }
                            }
                        }
                    }
                    if (isReplaced)
                    {
                        mi.Visible = false;
                        mi.SetMeta("UserVisibility", false);
                        mi.SetMeta("UserHiddenByCustomModel", true);
                    }
                }

                EmitSignal(SignalName.RiggingConfirmed);
                QueueFree();
            }
            else
            {
                _lblBottomStatus.Text = $"Rigging Error: {result.ErrorMessage}";
                _lblBottomStatus.AddThemeColorOverride("font_color", new Color(0.95f, 0.40f, 0.40f));
            }
        }

        private void OnCancel()
        {
            // If live imported scene is not parented to anything, clean it up
            if (_liveImportedScene != null && GodotObject.IsInstanceValid(_liveImportedScene) && _liveImportedScene.GetParent() == null)
            {
                _liveImportedScene.QueueFree();
            }

            EmitSignal(SignalName.ModalCancelled);
            QueueFree();
        }

        #endregion
    }
}
