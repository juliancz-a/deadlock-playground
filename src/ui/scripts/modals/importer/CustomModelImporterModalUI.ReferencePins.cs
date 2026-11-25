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
        #region Landmark Auto-Rig Pin Editor & Weight Paint

        // Modular Auto-Rig Pro–style Reference Rig (stored in imported-model space; visuals mapped via aligned transform).
        private ReferenceRigDef _referenceRig = new();
        private bool _isEditReferenceBonesMode = true;
        private bool _isGizmoEditingRefJoint = false;
        private bool _isGuidedPinPlacement = false;
        private int _guidedPinStep = 0; // 0=Chin, 1=Neck, 2=Shoulder, 3=Wrist, 4=HandTip, 5=Pelvis, 6=Ankle
        private readonly HashSet<string> _guidedPlacedPinKeys = new(StringComparer.OrdinalIgnoreCase);
        private static readonly string[] GuidedPinStepLabels =
        {
            "Step 1/7: Click on the CHIN tip",
            "Step 2/7: Click on the NECK base",
            "Step 3/7: Click on a SHOULDER (mirrors L↔R)",
            "Step 4/7: Click on a WRIST (computes Elbow & mirrors)",
            "Step 5/7: Click on a HAND TIP (middle fingertip, mirrors)",
            "Step 6/7: Click on the PELVIS / GROIN center",
            "Step 7/7: Click on an ANKLE (computes Hip/Knee & mirrors)"
        };
        private sealed class RefRigUndoSnapshot
        {
            public ReferenceRigDef Rig;
            public bool GuidedMode;
            public int GuidedStep;
            public HashSet<string> GuidedPlacedKeys;
        }
        private readonly List<RefRigUndoSnapshot> _refRigUndoStack = new();
        private readonly List<RefRigUndoSnapshot> _refRigRedoStack = new();
        private bool _guidedCurrentStepPlaced = false;
        private bool _isGuidedCameraLocked = false;
        private Button _btnRefHudGuidedPlace;
        private Button _btnRefHudGuidedPrev;
        private Button _btnRefHudGuidedNext;
        private Button _btnRefHudUndo;
        private Button _btnRefHudRedo;
        private Button _btnRefHudBind;
        private Vector2 _pinPressScreenPos;
        private bool _pinDragExceededThreshold = false;
        private PanelContainer _refRigViewportHud;
        private PanelContainer _refRigViewportBottomHud;
        private Label _lblRefHudSelectedBone;
        private Button _btnRefHudMirror;
        private readonly Button[] _hudSpineBtns = new Button[4];
        private CheckBox _hudChkNeck;
        private CheckBox _hudChkClav;
        private CheckBox _hudChkToes;
        private readonly Button[] _hudFingerModeBtns = new Button[2];
        private HBoxContainer _hudFingerSubRow;
        private readonly CheckBox[] _hudFingerChecks = new CheckBox[5];
        private readonly Button[] _hudPhalanxBtns = new Button[2];
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
        private Button _hudBtnFrontOnly;
        private Button _hudBtnSameIsland;
        private Button _hudBtnPaintThrough;
        private Button _hudBtnFillIsland;
        private Button _hudBtnGradientFill;
        private Button _hudBtnUnhideAll;
        private HSlider _hudSliderNearClip;
        private Label _hudLblNearClip;
        private bool _paintFrontFacingOnly = true;
        private bool _paintSameIslandOnly = true;
        private bool _paintThrough = false;
        private int _paintToolSubMode = 0; // 0 = Brush, 1 = Flood Fill Island, 2 = Gradient Drag
        private bool _isDraggingGradient = false;
        private Vector3 _gradientStartWorld;
        private CustomWeightPaintEditor.SurfaceState _gradientHitSurface;
        private int _gradientHitIslandId = -1;
        private CustomWeightPaintEditor.SurfaceState _lastHoveredPaintSurface;
        private int _lastHoveredPaintVertex = -1;
        private readonly HashSet<string> _lockedPaintBones = new(StringComparer.OrdinalIgnoreCase);
        private Texture2D _iconPaintLock;
        private Texture2D _iconPaintUnlock;
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
        private MeshInstance3D _paintGradientGizmo;
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
            if (_optRiggingMethod == null || _optRiggingMethod.Selected < 0) return false;
            var m = (CustomModelAutoRigger.RiggingMethod)_optRiggingMethod.GetItemId(_optRiggingMethod.Selected);
            return m == CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig ||
                   (m == CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig &&
                    _partialBodyEngine == CustomModelAutoRigger.PartialBodyEngine.SemiBodyPins);
        }

        private bool IsKnnMethod()
        {
            if (_optRiggingMethod == null || _optRiggingMethod.Selected < 0) return false;
            var m = (CustomModelAutoRigger.RiggingMethod)_optRiggingMethod.GetItemId(_optRiggingMethod.Selected);
            return m == CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection ||
                   (m == CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig &&
                    _partialBodyEngine == CustomModelAutoRigger.PartialBodyEngine.HeroBodyTransfer);
        }

        private bool IsTab2Active() => _rightTabs != null && _rightTabs.CurrentTab == 1;
        private bool IsBindPoseView() => _optTestAnim == null || _optTestAnim.Selected <= 0;
        private bool IsTab3PhysicsPaintHostActive() =>
            _rightTabs != null && _rightTabs.CurrentTab == 2 &&
            _boxSubViewWeights != null && _boxSubViewWeights.Visible &&
            _boxTab3WeightPaintHost != null && _boxWeightPaintSection != null &&
            _boxWeightPaintSection.GetParent() == _boxTab3WeightPaintHost;
        private bool IsWeightPaintActive() =>
            (_btnWeightPaintMode?.ButtonPressed ?? false) &&
            (IsTab2Active() || IsTab3PhysicsPaintHostActive());
        private bool IsDeformBrushActive() => _btnDeformBrushToggle != null && _btnDeformBrushToggle.ButtonPressed && (_chkDeformEnabled?.ButtonPressed ?? false);
        private bool IsPinEditActive() => IsLandmarkMethod() && _isEditReferenceBonesMode && !IsWeightPaintActive() && !IsDeformBrushActive() && !IsImportedSculptActive();

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
            if (tabIdx != 0 && IsImportedSculptActive() && _btnImpSculptToggle != null)
            {
                _btnImpSculptToggle.ButtonPressed = false;
            }
            if (tabIdx != 2)
            {
                SetSyntheticPropsExpanded(false);
                if (_isGizmoEditingSyntheticChain) SetSyntheticChainGizmoActive(false);
                else ClearSyntheticChainPreview();
                UpdateSyntheticChainViewportHud();
            }
            if (tabIdx != 2 && _wasInCollidersSubView)
            {
                _wasInCollidersSubView = false;
                if (_showColliders)
                {
                    if (_btnToggleColliders != null) _btnToggleColliders.ButtonPressed = false;
                    else
                    {
                        _showColliders = false;
                        if (_collidersDebugMesh != null) _collidersDebugMesh.Visible = false;
                    }
                    if (_btnShowCollidersInCard != null) _btnShowCollidersInCard.SetPressedNoSignal(false);
                }
            }
            if (tabIdx == 1)
            {
                HostWeightPaintSectionIn(_boxTab2WeightPaintHost);
                UpdateRiggingMethodPanelVisibility();
                UpdateAccessoryRigVisibility();
            }
            else if (tabIdx == 2)
            {
                if (_boxSubViewWeights != null && _boxSubViewWeights.Visible)
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

        private void EnsureReferenceRigInitialized(bool force)
        {
            if (IsPartialSemiBodyPinsActive())
            {
                EnsurePartialBodyPinsInitialized(force);
                return;
            }
            _referenceRig ??= new ReferenceRigDef();
            if (!force && _referenceRig.Joints.Count >= CustomLandmarkWeightSolver.PinNames.Length)
            {
                _referenceRig.EnsureDefaultJoints();
                return;
            }
            if (_liveImportedScene == null || !GodotObject.IsInstanceValid(_liveImportedScene)) return;
            var meshes = FilterActiveImportedMeshes(CustomModelAutoRigger.CollectMeshes(_liveImportedScene));
            if (meshes.Count == 0) return;
            var est = CustomLandmarkWeightSolver.EstimateLandmarks(_liveImportedScene, meshes);
            if (est.Count > 0)
            {
                _landmarkPins = est;
                if (force)
                {
                    _referenceRig.Joints = new Dictionary<string, Vector3>(est, StringComparer.OrdinalIgnoreCase);
                    _userMovedLandmarkPins = false;
                }
                else
                {
                    foreach (var kv in est)
                        if (!_referenceRig.Joints.ContainsKey(kv.Key))
                            _referenceRig.Joints[kv.Key] = kv.Value;
                }
                _referenceRig.EnsureDefaultJoints();
                if (_referenceRig.FingerMode == ReferenceFingerRigMode.ArticulatedFingers)
                {
                    _referenceRig.SnapFingersToMesh(_liveImportedScene, meshes, forceRegenerate: force);
                }
            }
        }

        private void EnsureLandmarkPins(bool force)
        {
            EnsureReferenceRigInitialized(force);
            if (_referenceRig != null && _referenceRig.Joints.Count >= CustomLandmarkWeightSolver.PinNames.Length)
            {
                _landmarkPins = new Dictionary<string, Vector3>(_referenceRig.Joints, StringComparer.OrdinalIgnoreCase);
            }
        }

        private bool TryGetReferenceJointPosition(string key, out Vector3 localPos)
        {
            localPos = Vector3.Zero;
            if (string.IsNullOrEmpty(key) || _referenceRig == null) return false;
            if (key.StartsWith("cloth::", StringComparison.Ordinal))
            {
                var parts = key.Split("::");
                if (parts.Length == 3 && int.TryParse(parts[2], out int jIdx))
                {
                    var cc = _referenceRig.ClothChains.FirstOrDefault(c => string.Equals(c.ChainId, parts[1], StringComparison.OrdinalIgnoreCase));
                    if (cc != null && jIdx >= 0 && jIdx < cc.JointPositions.Count)
                    {
                        localPos = cc.JointPositions[jIdx];
                        return true;
                    }
                }
                return false;
            }
            return _referenceRig.Joints.TryGetValue(key, out localPos);
        }

        private void SetReferenceJointPosition(string key, Vector3 localPos, bool applyMirror)
        {
            if (string.IsNullOrEmpty(key) || _referenceRig == null) return;
            float cx = _referenceRig.Joints.TryGetValue("Pelvis", out var pel) ? pel.X : 0f;

            if (key.StartsWith("cloth::", StringComparison.Ordinal))
            {
                var parts = key.Split("::");
                if (parts.Length == 3 && int.TryParse(parts[2], out int jIdx))
                {
                    var cc = _referenceRig.ClothChains.FirstOrDefault(c => string.Equals(c.ChainId, parts[1], StringComparison.OrdinalIgnoreCase));
                    if (cc != null && jIdx >= 0 && jIdx < cc.JointPositions.Count)
                    {
                        cc.JointPositions[jIdx] = localPos;
                        if (applyMirror && !string.IsNullOrEmpty(cc.MirrorPairId))
                        {
                            var mirCc = _referenceRig.ClothChains.FirstOrDefault(c => string.Equals(c.ChainId, cc.MirrorPairId, StringComparison.OrdinalIgnoreCase));
                            if (mirCc != null && jIdx < mirCc.JointPositions.Count)
                                mirCc.JointPositions[jIdx] = new Vector3(2f * cx - localPos.X, localPos.Y, localPos.Z);
                        }
                    }
                }
                return;
            }

            _referenceRig.Joints[key] = localPos;
            _landmarkPins[key] = localPos;
            if (applyMirror)
            {
                string mir = ReferenceRigDef.MirrorJointKey(key);
                if (!string.IsNullOrEmpty(mir) && _referenceRig.Joints.ContainsKey(mir))
                {
                    Vector3 mirPos = new Vector3(2f * cx - localPos.X, localPos.Y, localPos.Z);
                    _referenceRig.Joints[mir] = mirPos;
                    _landmarkPins[mir] = mirPos;
                }
            }
        }

        private List<(string Key, Vector3 LocalPos, ReferenceBoneRole Role)> CollectActiveReferenceJoints()
        {
            var result = new List<(string, Vector3, ReferenceBoneRole)>();
            if (_referenceRig == null) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            bool filterGuided = _isGuidedPinPlacement && _guidedPlacedPinKeys.Count > 0;
            bool isPartialPins = IsPartialSemiBodyPinsActive();
            HashSet<string> partialAllowed = isPartialPins ? GetCurrentAllowedPartialPins() : null;
            bool IsAllowed(string key) => (!filterGuided || _guidedPlacedPinKeys.Contains(key)) &&
                                          (partialAllowed == null || partialAllowed.Contains(key));

            // Chin landmark replaces Head (with fallback if legacy Head exists without Chin)
            if (_referenceRig.Joints.TryGetValue("Chin", out var chinPos) && IsAllowed("Chin") && seen.Add("Chin"))
                result.Add(("Chin", chinPos, ReferenceBoneRole.Core));
            else if (!_referenceRig.Joints.ContainsKey("Chin") && _referenceRig.Joints.TryGetValue("Head", out var headPos) && IsAllowed("Head") && seen.Add("Head"))
                result.Add(("Head", headPos, ReferenceBoneRole.Core));

            foreach (string name in CustomLandmarkWeightSolver.PinNames)
            {
                if (name == "Head") continue; // superseded by Chin
                if (name == "Neck" && !_referenceRig.HasNeck) continue;
                if (IsAllowed(name) && _referenceRig.Joints.TryGetValue(name, out var p) && seen.Add(name))
                    result.Add((name, p, ReferenceBoneRole.Core));
            }
            // Auto-Rig Pro–style HandTipL / HandTipR markers
            foreach (string hk in new[] { "HandTipL", "HandTipR" })
            {
                if (IsAllowed(hk) && _referenceRig.Joints.TryGetValue(hk, out var htp) && seen.Add(hk))
                    result.Add((hk, htp, ReferenceBoneRole.Core));
            }
            if (!filterGuided && _referenceRig.HasToes)
            {
                foreach (string tk in new[] { "ToeL", "ToeR" })
                    if (IsAllowed(tk) && _referenceRig.Joints.TryGetValue(tk, out var tp) && seen.Add(tk))
                        result.Add((tk, tp, ReferenceBoneRole.Core));
            }
            bool allowFingers = !isPartialPins || GetCurrentPartialBodyMask().IncludeHands;
            if (!filterGuided && allowFingers && _referenceRig.FingerMode == ReferenceFingerRigMode.ArticulatedFingers)
            {
                int ph = Math.Clamp(_referenceRig.PhalanxCount, 2, 3);
                foreach (string side in new[] { "L", "R" })
                {
                    foreach (string fn in ReferenceRigDef.FingerNames)
                    {
                        if (!_referenceRig.IsFingerEnabled(fn)) continue;
                        for (int j = 0; j <= ph; j++)
                        {
                            string fk = $"finger_{fn}_{j}_{side}";
                            if (_referenceRig.Joints.TryGetValue(fk, out var fp) && seen.Add(fk))
                                result.Add((fk, fp, ReferenceBoneRole.Finger));
                        }
                    }
                }
            }
            if (!filterGuided && _referenceRig.ClothChains != null)
            {
                foreach (var cc in _referenceRig.ClothChains)
                {
                    if (cc?.JointPositions == null) continue;
                    for (int k = 0; k < cc.JointPositions.Count; k++)
                    {
                        string ck = $"cloth::{cc.ChainId}::{k}";
                        if (seen.Add(ck))
                            result.Add((ck, cc.JointPositions[k], ReferenceBoneRole.Cloth));
                    }
                }
            }
            return result;
        }

        private void PushReferenceRigUndoSnapshot()
        {
            if (_referenceRig == null) return;
            _refRigUndoStack.Add(new RefRigUndoSnapshot
            {
                Rig = _referenceRig.Clone(),
                GuidedMode = _isGuidedPinPlacement,
                GuidedStep = _guidedPinStep,
                GuidedPlacedKeys = new HashSet<string>(_guidedPlacedPinKeys, StringComparer.OrdinalIgnoreCase)
            });
            if (_refRigUndoStack.Count > 40) _refRigUndoStack.RemoveAt(0);
            _refRigRedoStack.Clear();
            SyncReferenceRigViewportHud();
        }

        private void UndoReferenceRigEdit()
        {
            if (_refRigUndoStack.Count == 0 || _referenceRig == null) return;
            _refRigRedoStack.Add(new RefRigUndoSnapshot
            {
                Rig = _referenceRig.Clone(),
                GuidedMode = _isGuidedPinPlacement,
                GuidedStep = _guidedPinStep,
                GuidedPlacedKeys = new HashSet<string>(_guidedPlacedPinKeys, StringComparer.OrdinalIgnoreCase)
            });
            var snap = _refRigUndoStack[^1];
            _refRigUndoStack.RemoveAt(_refRigUndoStack.Count - 1);
            ApplyReferenceRigUndoSnapshot(snap);
            SetBottomStatus("Undid reference rig change.", StatusTone.Info);
        }

        private void RedoReferenceRigEdit()
        {
            if (_refRigRedoStack.Count == 0 || _referenceRig == null) return;
            _refRigUndoStack.Add(new RefRigUndoSnapshot
            {
                Rig = _referenceRig.Clone(),
                GuidedMode = _isGuidedPinPlacement,
                GuidedStep = _guidedPinStep,
                GuidedPlacedKeys = new HashSet<string>(_guidedPlacedPinKeys, StringComparer.OrdinalIgnoreCase)
            });
            var snap = _refRigRedoStack[^1];
            _refRigRedoStack.RemoveAt(_refRigRedoStack.Count - 1);
            ApplyReferenceRigUndoSnapshot(snap);
            SetBottomStatus("Redid reference rig change.", StatusTone.Info);
        }

        private bool IsGuidedStepPlaced(int step)
        {
            return step switch
            {
                0 => _guidedPlacedPinKeys.Contains("Chin"),
                1 => _guidedPlacedPinKeys.Contains("Neck"),
                2 => _guidedPlacedPinKeys.Contains("ShoulderL") || _guidedPlacedPinKeys.Contains("ShoulderR"),
                3 => _guidedPlacedPinKeys.Contains("WristL") || _guidedPlacedPinKeys.Contains("WristR"),
                4 => _guidedPlacedPinKeys.Contains("HandTipL") || _guidedPlacedPinKeys.Contains("HandTipR"),
                5 => _guidedPlacedPinKeys.Contains("Pelvis"),
                6 => _guidedPlacedPinKeys.Contains("AnkleL") || _guidedPlacedPinKeys.Contains("AnkleR"),
                _ => false
            };
        }

        private void ApplyReferenceRigUndoSnapshot(RefRigUndoSnapshot snap)
        {
            if (snap == null) return;
            _referenceRig = snap.Rig.Clone();
            _landmarkPins = new Dictionary<string, Vector3>(_referenceRig.Joints, StringComparer.OrdinalIgnoreCase);
            _isGuidedPinPlacement = snap.GuidedMode;
            _guidedPinStep = snap.GuidedStep;
            _guidedPlacedPinKeys.Clear();
            if (snap.GuidedPlacedKeys != null)
                foreach (var k in snap.GuidedPlacedKeys) _guidedPlacedPinKeys.Add(k);
            _guidedCurrentStepPlaced = IsGuidedStepPlaced(_guidedPinStep);
            _pinsDirtyNeedsRigRebuild = true;
            SyncReferenceRigControlsFromDef();
            RefreshPinVisuals();
            UpdateActivePinBadge();
            UpdatePinProgressChip();
        }

        private void AttachGizmoToSelectedReferenceJoint()
        {
            // Keep 3D Gizmo hidden during Reference Bone editing — direct click-drag + 1-by-1 Smart placement is much cleaner.
            if (_previewGizmo != null && GodotObject.IsInstanceValid(_previewGizmo))
            {
                if (IsPinEditActive())
                {
                    _isGizmoEditingRefJoint = false;
                    _previewGizmo.Visible = false;
                }
                else if (_isGizmoEditingRefJoint)
                {
                    _isGizmoEditingRefJoint = false;
                    UpdateGizmoTargetTransform();
                }
            }
            SyncReferenceRigViewportHud();
        }

        private bool TryRaycastPreviewModelSurface(Vector2 screenPos, out Vector3 hitModelSpace)
        {
            hitModelSpace = Vector3.Zero;
            if (_previewCamera == null || _liveImportedScene == null || !GodotObject.IsInstanceValid(_liveImportedScene))
                return false;

            Transform3D xf = BuildCurrentAlignedTransform();
            Vector3 rayOrig = _previewCamera.ProjectRayOrigin(screenPos);
            Vector3 rayDir = _previewCamera.ProjectRayNormal(screenPos);

            // Raycast against all triangles of active imported meshes to find front & back hits,
            // then place the joint cleanly at the midpoint depth inside the limb!
            var meshes = FilterActiveImportedMeshes(CustomModelAutoRigger.CollectMeshes(_liveImportedScene));
            var fallbackSkel = CustomModelAutoRigger.FindSkeleton(_liveImportedScene);
            float tNear = float.MaxValue;
            float tFar = float.MinValue;
            int hitCount = 0;

            foreach (var mi in meshes)
            {
                if (mi?.Mesh == null) continue;
                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                {
                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count == 0) continue;
                    var verts = CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    if (verts == null || verts.Length < 3) continue;
                    var rawBones = arrays.Count > (int)Mesh.ArrayType.Bones ? CustomModelAutoRigger.GetInt32Array(arrays[(int)Mesh.ArrayType.Bones]) : null;
                    var rawWeights = arrays.Count > (int)Mesh.ArrayType.Weights ? CustomModelAutoRigger.GetFloat32Array(arrays[(int)Mesh.ArrayType.Weights]) : null;
                    CustomModelAutoRigger.EvaluateMeshSurfaceInTargetSpace(
                        mi, _liveImportedScene, xf, fallbackSkel,
                        verts, null, rawBones, rawWeights,
                        out var worldVerts, out _);
                    var idx = arrays.Count > (int)Mesh.ArrayType.Index ? CustomModelAutoRigger.GetInt32Array(arrays[(int)Mesh.ArrayType.Index]) : null;
                    int triCount = idx != null && idx.Length >= 3 ? idx.Length / 3 : worldVerts.Length / 3;
                    for (int ti = 0; ti < triCount; ti++)
                    {
                        int i0 = idx != null ? idx[ti * 3] : ti * 3;
                        int i1 = idx != null ? idx[ti * 3 + 1] : ti * 3 + 1;
                        int i2 = idx != null ? idx[ti * 3 + 2] : ti * 3 + 2;
                        Vector3 v0 = worldVerts[i0];
                        Vector3 v1 = worldVerts[i1];
                        Vector3 v2 = worldVerts[i2];

                        // Möller–Trumbore ray-triangle intersection
                        Vector3 e1 = v1 - v0, e2 = v2 - v0;
                        Vector3 pvec = rayDir.Cross(e2);
                        float det = e1.Dot(pvec);
                        if (MathF.Abs(det) < 1e-7f) continue;
                        float invDet = 1f / det;
                        Vector3 tvec = rayOrig - v0;
                        float u = tvec.Dot(pvec) * invDet;
                        if (u < 0f || u > 1f) continue;
                        Vector3 qvec = tvec.Cross(e1);
                        float v = rayDir.Dot(qvec) * invDet;
                        if (v < 0f || u + v > 1f) continue;
                        float t = e2.Dot(qvec) * invDet;
                        if (t > 0.01f)
                        {
                            if (t < tNear) tNear = t;
                            if (t > tFar && t < tNear + 0.45f) tFar = t;
                            hitCount++;
                        }
                    }
                }
            }

            if (hitCount > 0)
            {
                float tMid = tFar > tNear ? (tNear + tFar) * 0.5f : tNear;
                Vector3 hitWorld = rayOrig + rayDir * tMid;
                hitModelSpace = xf.AffineInverse() * hitWorld;
                return true;
            }
            return false;
        }

        private void StartGuidedOneByOnePinPlacement(bool clearExisting = false)
        {
            _referenceRig ??= new ReferenceRigDef();
            if (clearExisting)
            {
                _referenceRig.Joints.Clear();
                _landmarkPins.Clear();
            }
            else if (_referenceRig.Joints.Count == 0)
            {
                EnsureReferenceRigInitialized(false);
            }
            _isEditReferenceBonesMode = true;
            _isGuidedPinPlacement = true;
            _isGuidedCameraLocked = true;
            _guidedPinStep = 0;
            _guidedCurrentStepPlaced = false;
            _guidedPlacedPinKeys.Clear();
            _selectedPin = null;
            _draggingPin = null;
            SetCameraPreset("Front");
            SetTab2SubView(1); // Auto-switch Tab 2 sub-nav to 'Reference Rig'
            SyncEditBindButtonVisuals();
            ApplyLandmarkPreviewState();
            RefreshPinVisuals();
            SyncReferenceRigViewportHud();
            SetBottomStatus(GuidedPinStepLabels[0] + " — click mesh to place/adjust, then click Next → (view locked until Middle/Right move)", StatusTone.Info);
        }

        private void PreviousGuidedPinStep()
        {
            if (!_isGuidedPinPlacement || _guidedPinStep <= 0) return;
            PushReferenceRigUndoSnapshot();
            _guidedPinStep--;
            _guidedCurrentStepPlaced = IsGuidedStepPlaced(_guidedPinStep);
            SyncReferenceRigViewportHud();
            SetBottomStatus(GuidedPinStepLabels[_guidedPinStep] + " — click/drag to adjust, then Next →", StatusTone.Info);
        }

        private void AdvanceGuidedPinStep()
        {
            if (!_isGuidedPinPlacement || !IsGuidedStepPlaced(_guidedPinStep)) return;
            PushReferenceRigUndoSnapshot();
            _guidedPinStep++;
            _guidedCurrentStepPlaced = IsGuidedStepPlaced(_guidedPinStep);

            if (_guidedPinStep >= GuidedPinStepLabels.Length)
            {
                _isGuidedPinPlacement = false;
                _isGuidedCameraLocked = false;
                _guidedPinStep = 0;
                _guidedPlacedPinKeys.Clear();
                _referenceRig.EnsureDefaultJoints();
                if (_referenceRig.FingerMode == ReferenceFingerRigMode.ArticulatedFingers)
                    SnapReferenceFingersToCurrentMesh();
                SetBottomStatus("All 7 smart pins placed! Fine-tune any joint by dragging, or click Bind.", StatusTone.Success);
            }
            else
            {
                SetBottomStatus(GuidedPinStepLabels[_guidedPinStep] + " — click mesh to place/adjust, then click Next →", StatusTone.Info);
            }
            RefreshPinVisuals();
            SyncReferenceRigViewportHud();
        }

        private void ApplyGuidedPinClick(Vector3 localPos)
        {
            _referenceRig ??= new ReferenceRigDef();
            PushReferenceRigUndoSnapshot();

            float cx = _referenceRig.Joints.TryGetValue("Pelvis", out var pelCur)
                ? pelCur.X
                : (_referenceRig.Joints.TryGetValue("Chin", out var chinCur) ? chinCur.X : localPos.X);

            void SetJointSilent(string key, Vector3 pos, bool mirror)
            {
                SetReferenceJointPosition(key, pos, applyMirror: mirror);
                if (mirror)
                {
                    string mir = ReferenceRigDef.MirrorJointKey(key);
                    if (!string.IsNullOrEmpty(mir))
                    {
                        Vector3 mirPos = new Vector3(2f * cx - pos.X, pos.Y, pos.Z);
                        _referenceRig.Joints[mir] = mirPos;
                        _landmarkPins[mir] = mirPos;
                    }
                }
            }

            void PlaceGuided(string key, Vector3 pos, bool mirror)
            {
                SetJointSilent(key, pos, mirror);
                _guidedPlacedPinKeys.Add(key);
                if (mirror)
                {
                    string mir = ReferenceRigDef.MirrorJointKey(key);
                    if (!string.IsNullOrEmpty(mir)) _guidedPlacedPinKeys.Add(mir);
                }
            }

            switch (_guidedPinStep)
            {
                case 0: // Chin
                    cx = localPos.X;
                    PlaceGuided("Chin", localPos, mirror: false);
                    _referenceRig.Joints["Head"] = localPos + Vector3.Up * 0.08f;
                    break;
                case 1: // Neck
                    PlaceGuided("Neck", localPos, mirror: false);
                    if (_referenceRig.Joints.TryGetValue("Pelvis", out var p0))
                        SetJointSilent("Chest", p0.Lerp(localPos, 0.60f), mirror: false);
                    break;
                case 2: // Shoulder (auto-mirror)
                {
                    string side = localPos.X >= cx ? "L" : "R";
                    PlaceGuided("Shoulder" + side, localPos, mirror: true);
                    break;
                }
                case 3: // Wrist (auto-computes Elbow silently; only Wrist pin is shown during 1-by-1!)
                {
                    string side = localPos.X >= cx ? "L" : "R";
                    PlaceGuided("Wrist" + side, localPos, mirror: true);
                    if (_referenceRig.Joints.TryGetValue("Shoulder" + side, out var sh))
                        SetJointSilent("Elbow" + side, sh.Lerp(localPos, 0.50f), mirror: true);
                    break;
                }
                case 4: // HandTip (middle fingertip, auto-mirrors)
                {
                    string side = localPos.X >= cx ? "L" : "R";
                    PlaceGuided("HandTip" + side, localPos, mirror: true);
                    break;
                }
                case 5: // Pelvis (auto-computes Chest silently)
                    PlaceGuided("Pelvis", localPos, mirror: false);
                    if (_referenceRig.Joints.TryGetValue("Neck", out var nk))
                        SetJointSilent("Chest", localPos.Lerp(nk, 0.60f), mirror: false);
                    break;
                case 6: // Ankle (auto-computes Hip & Knee silently with clean pelvis/hip separation)
                {
                    string side = localPos.X >= cx ? "L" : "R";
                    PlaceGuided("Ankle" + side, localPos, mirror: true);
                    if (_referenceRig.Joints.TryGetValue("Pelvis", out var pel))
                    {
                        float legLen = MathF.Max(0.15f, MathF.Abs(pel.Y - localPos.Y));
                        float sign = localPos.X >= pel.X ? 1f : -1f;
                        float hipOffsetX = MathF.Max(MathF.Abs(localPos.X - pel.X) * 0.88f, legLen * 0.13f);
                        Vector3 hip = new Vector3(pel.X + sign * hipOffsetX, pel.Y - legLen * 0.09f, pel.Z);
                        SetJointSilent("Hip" + side, hip, mirror: true);
                        SetJointSilent("Knee" + side, hip.Lerp(localPos, 0.50f), mirror: true);
                    }
                    break;
                }
            }

            _guidedCurrentStepPlaced = IsGuidedStepPlaced(_guidedPinStep);
            _userMovedLandmarkPins = true;
            _pinsDirtyNeedsRigRebuild = true;

            string stepName = _guidedPinStep < GuidedPinStepLabels.Length ? GuidedPinStepLabels[_guidedPinStep] : "";
            SetBottomStatus($"{stepName} — drag pin to refine or click Next → (Enter).", StatusTone.Success);
            RefreshPinVisuals();
            SyncReferenceRigViewportHud();
        }

        private Color PinColor(string name, ReferenceBoneRole role = ReferenceBoneRole.Core)
        {
            if (role == ReferenceBoneRole.Cloth || (name != null && name.StartsWith("cloth::", StringComparison.Ordinal)))
                return new Color(0.96f, 0.32f, 0.90f); // Magenta: Cloth chain
            if (role == ReferenceBoneRole.Finger || (name != null && name.StartsWith("finger_", StringComparison.OrdinalIgnoreCase)))
                return new Color(1.0f, 0.80f, 0.22f); // Gold: Fingers
            if (name == "Chin" || name == "Head") return new Color(0.25f, 0.95f, 1f);
            if (name != null && name.StartsWith("HandTip", StringComparison.OrdinalIgnoreCase)) return new Color(0.45f, 1.0f, 0.65f);
            if (name != null && name.EndsWith("L") && name != "Pelvis") return new Color(0.30f, 0.68f, 1f);
            if (name != null && name.EndsWith("R")) return new Color(1f, 0.58f, 0.20f);
            return new Color(0.25f, 0.88f, 0.98f);
        }

        private void RefreshPinVisuals()
        {
            bool hasPinsToDraw = _referenceRig != null &&
                                 (_isGuidedPinPlacement ? _guidedPlacedPinKeys.Count > 0 : _referenceRig.Joints.Count > 0);
            bool show = IsPinEditActive() && (_chkShowPins?.ButtonPressed ?? true) && hasPinsToDraw &&
                        _previewWorldRoot != null && GodotObject.IsInstanceValid(_previewWorldRoot);

            foreach (var kv in _pinSpheres)
                if (GodotObject.IsInstanceValid(kv.Value)) kv.Value.Visible = false;
            if (_pinSkeletonLines != null && GodotObject.IsInstanceValid(_pinSkeletonLines)) _pinSkeletonLines.Visible = show;
            if (!show)
            {
                if (_isGizmoEditingRefJoint)
                {
                    _isGizmoEditingRefJoint = false;
                    UpdateGizmoTargetTransform();
                }
                return;
            }

            string active = _draggingPin ?? _hoveredPin ?? _selectedPin;
            string mirrorActive = (active != null && (_chkMirrorPins?.ButtonPressed ?? true))
                ? ReferenceRigDef.MirrorJointKey(active)
                : null;

            Transform3D xf = BuildCurrentAlignedTransform();
            var activeJoints = CollectActiveReferenceJoints();
            var ringShader = GetJointRingShader();

            foreach (var (name, localPos, role) in activeJoints)
            {
                float quadSize = role == ReferenceBoneRole.Finger ? 0.022f : (role == ReferenceBoneRole.Cloth ? 0.042f : 0.056f);
                if (!_pinSpheres.TryGetValue(name, out var marker) || !GodotObject.IsInstanceValid(marker))
                {
                    Material mat;
                    if (ringShader != null)
                    {
                        var sm = new ShaderMaterial { Shader = ringShader, RenderPriority = 100 };
                        sm.SetShaderParameter("ring_color", PinColor(name, role));
                        sm.SetShaderParameter("is_selected", 0.0f);
                        mat = sm;
                    }
                    else
                    {
                        mat = new StandardMaterial3D
                        {
                            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                            AlbedoColor = PinColor(name, role),
                            NoDepthTest = true,
                            RenderPriority = 100
                        };
                    }
                    marker = new MeshInstance3D
                    {
                        Name = "RefJoint_" + name.Replace("::", "_"),
                        Mesh = new QuadMesh { Size = new Vector2(quadSize, quadSize), Material = mat },
                        Layers = 1u // never Visual Layer 21 (CameraBrush)
                    };
                    _previewWorldRoot.AddChild(marker);
                    _pinSpheres[name] = marker;
                }
                bool inMulti = _selectedPins.Contains(name);
                bool isLit = inMulti || name == active || (mirrorActive != null && name == mirrorActive);
                float s = (name == active || (mirrorActive != null && name == mirrorActive))
                    ? 1.35f
                    : (inMulti ? 1.25f : 1f);
                marker.Scale = new Vector3(s, s, s);
                marker.Position = xf * localPos;
                if (marker.Mesh is QuadMesh qm && qm.Material is ShaderMaterial smMat)
                {
                    smMat.SetShaderParameter("ring_color", PinColor(name, role));
                    smMat.SetShaderParameter("is_selected", isLit ? 1.0f : 0.0f);
                }
                else if (marker.Mesh is PrimitiveMesh pm && pm.Material is StandardMaterial3D stdMat)
                {
                    Color targetCol = isLit ? Colors.White : PinColor(name, role);
                    if (!stdMat.AlbedoColor.IsEqualApprox(targetCol)) stdMat.AlbedoColor = targetCol;
                }
                marker.Visible = true;
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

            // During progressive 1-by-1 placement, only draw lines once core anchors exist; otherwise draw modular (or partial-body) bones
            bool isPartialVisual = IsPartialSemiBodyPinsActive();
            bool drawFullRig = !_isGuidedPinPlacement && (isPartialVisual
                ? _referenceRig.Joints.Count > 0
                : (_referenceRig.Joints.ContainsKey("Pelvis") && _referenceRig.Joints.ContainsKey("AnkleL")));
            if (drawFullRig)
            {
                float h = isPartialVisual && _previewHeroSkeleton != null
                    ? CustomModelAutoRigger.ComputeHeroHeight(_previewHeroSkeleton)
                    : 1.7f;
                if (!isPartialVisual &&
                    (_referenceRig.Joints.TryGetValue("Chin", out var hp) || _referenceRig.Joints.TryGetValue("Head", out hp)) &&
                    _referenceRig.Joints.TryGetValue("AnkleL", out var al))
                    h = MathF.Max(0.2f, MathF.Abs((xf * hp).Y - (xf * al).Y) / 0.84f);
                ReferenceRigDef.PartialBodyRigMask? pMask = isPartialVisual ? GetCurrentPartialBodyMask() : (ReferenceRigDef.PartialBodyRigMask?)null;
                var specs = _referenceRig.BuildResolvedBones(xf, h, 1f, partialMask: pMask);
                if (specs.Count > 0)
                {
                    im.SurfaceBegin(Mesh.PrimitiveType.Lines);
                    foreach (var sp in specs)
                    {
                        Color col = PinColor(sp.HeadJointKey, sp.Role);
                        if (!string.IsNullOrEmpty(active) && (sp.HeadJointKey == active || sp.TailJointKey == active))
                            col = Colors.White;
                        im.SurfaceSetColor(col); im.SurfaceAddVertex(sp.SegStart);
                        im.SurfaceSetColor(col); im.SurfaceAddVertex(sp.SegEnd);
                        if (sp.ParentIndex >= 0 && sp.ParentIndex < specs.Count && sp.Role == ReferenceBoneRole.Cloth)
                        {
                            var par = specs[sp.ParentIndex];
                            if (par.Role != ReferenceBoneRole.Cloth)
                            {
                                im.SurfaceSetColor(new Color(0.96f, 0.32f, 0.90f, 0.45f)); im.SurfaceAddVertex(par.Origin);
                                im.SurfaceSetColor(new Color(0.96f, 0.32f, 0.90f, 0.45f)); im.SurfaceAddVertex(sp.SegStart);
                            }
                        }
                    }
                    // Also draw subtle guide line from Wrist -> HandTip
                    foreach (string side in new[] { "L", "R" })
                    {
                        if (_referenceRig.Joints.TryGetValue("Wrist" + side, out var wr) &&
                            _referenceRig.Joints.TryGetValue("HandTip" + side, out var ht))
                        {
                            Color hc = new Color(0.45f, 1.0f, 0.65f, 0.70f);
                            im.SurfaceSetColor(hc); im.SurfaceAddVertex(xf * wr);
                            im.SurfaceSetColor(hc); im.SurfaceAddVertex(xf * ht);
                        }
                    }
                    im.SurfaceEnd();
                }
            }
            _pinSkeletonLines.Visible = drawFullRig;
        }

        private string PickPinUnderCursor(Vector2 pos)
        {
            if (_previewCamera == null || _referenceRig == null) return null;
            Transform3D xf = BuildCurrentAlignedTransform();
            string best = null; float bestD = 20f;
            foreach (var (name, localPos, role) in CollectActiveReferenceJoints())
            {
                Vector3 w = xf * localPos;
                if (_previewCamera.IsPositionBehind(w)) continue;
                float d = _previewCamera.UnprojectPosition(w).DistanceTo(pos);
                float hitR = role == ReferenceBoneRole.Finger ? 14f : 20f;
                if (d < hitR && d < bestD) { bestD = d; best = name; }
            }
            return best;
        }

        private bool HandlePinInput(InputEvent @event)
        {
            if (!IsPinEditActive() || !(_chkShowPins?.ButtonPressed ?? true) ||
                _referenceRig == null || (!_isGuidedPinPlacement && _referenceRig.Joints.Count == 0) || _previewCamera == null)
                return false;

            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    // Guided 1-by-1 Smart pin placement mode (Auto-Rig Pro style):
                    // If clicking directly on an already-placed pin, allow dragging to fine-tune it;
                    // otherwise raycast the mesh to place/reposition the current step's landmark!
                    if (_isGuidedPinPlacement)
                    {
                        string hitExisting = PickPinUnderCursor(mb.Position);
                        if (hitExisting == null)
                        {
                            if (TryRaycastPreviewModelSurface(mb.Position, out Vector3 hitLocal))
                            {
                                ApplyGuidedPinClick(hitLocal);
                                _isOrbiting = false; _isPanning = false;
                                GetViewport().SetInputAsHandled();
                                return true;
                            }
                            return false;
                        }
                    }

                    string best = PickPinUnderCursor(mb.Position);
                    if (best != null)
                    {
                        if (mb.ShiftPressed)
                        {
                            if (mb.DoubleClick) SelectAllPins();
                            else TogglePinInSelection(best, _chkMirrorPins?.ButtonPressed ?? true);
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
                        _pinPressScreenPos = mb.Position;
                        _pinDragExceededThreshold = false;
                        _selectedPin = best;
                        _hoveredPin = best;
                        _isOrbiting = false; _isPanning = false;
                        UpdateActivePinBadge();
                        RefreshPinVisuals();
                        AttachGizmoToSelectedReferenceJoint();
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
                }
                else if (!mb.Pressed && _draggingPin != null)
                {
                    string releasedPin = _draggingPin;
                    bool moved = _pinDragExceededThreshold;
                    _draggingPin = null;
                    _pinDragExceededThreshold = false;
                    UpdateActivePinBadge();
                    AttachGizmoToSelectedReferenceJoint();
                    if (moved)
                    {
                        // If the user adjusted Wrist or HandTip after 1-by-1 finished, automatically re-snap fingers to match!
                        // Dragging individual finger_* joints never re-snaps so manual per-joint tweaks are preserved.
                        if (!_isGuidedPinPlacement &&
                            _referenceRig != null &&
                            _referenceRig.FingerMode == ReferenceFingerRigMode.ArticulatedFingers &&
                            (releasedPin.StartsWith("Wrist", StringComparison.OrdinalIgnoreCase) ||
                             releasedPin.StartsWith("HandTip", StringComparison.OrdinalIgnoreCase)))
                        {
                            SnapReferenceFingersToCurrentMesh();
                        }
                        _pinsDirtyNeedsRigRebuild = true;
                        SetRigChipStatus("Reference Rig Modified — Click Bind / Re-Bind", StatusTone.Warning);
                    }
                    GetViewport().SetInputAsHandled();
                    return true;
                }
            }
            else if (@event is InputEventMouseMotion mm)
            {
                if (_draggingPin != null && TryGetReferenceJointPosition(_draggingPin, out Vector3 oldLocal))
                {
                    // Require a small 6px deadzone before screen-space dragging so simple clicks cleanly select without jumping
                    if (!_pinDragExceededThreshold)
                    {
                        if (mm.Position.DistanceTo(_pinPressScreenPos) < 6.0f)
                        {
                            GetViewport().SetInputAsHandled();
                            return true;
                        }
                        PushReferenceRigUndoSnapshot();
                        _pinDragExceededThreshold = true;
                    }
                    Transform3D xf = BuildCurrentAlignedTransform();
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
                        bool mirror = _chkMirrorPins?.ButtonPressed ?? true;

                        if (_selectedPins.Count > 1 && _selectedPins.Contains(_draggingPin))
                        {
                            foreach (string p in _selectedPins)
                            {
                                if (TryGetReferenceJointPosition(p, out Vector3 cur))
                                    SetReferenceJointPosition(p, cur + delta, applyMirror: mirror && !_selectedPins.Contains(ReferenceRigDef.MirrorJointKey(p) ?? ""));
                            }
                        }
                        else
                        {
                            SetReferenceJointPosition(_draggingPin, local, applyMirror: mirror);
                        }
                        UpdateActivePinBadge();
                        RefreshPinVisuals();
                        AttachGizmoToSelectedReferenceJoint();
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
        #endregion
    }
}
