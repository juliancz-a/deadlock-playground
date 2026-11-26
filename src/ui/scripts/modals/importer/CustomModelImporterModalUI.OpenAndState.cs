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
            DeadlockPlayground.Core.SessionCrashGuard.RecordBreadcrumb(
                $"Opened importer: {fileName} ({_workflowIntent})",
                activeHeroId: _heroCodename);
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
            _hasActiveCompleteReplacement =
                _workflowIntent == ImportWorkflowIntent.AccessoryAddOrReplace &&
                _liveHeroNode.FindChild("Custom_Rigged_Character", true, false) is Node3D activeReplacementNode &&
                GodotObject.IsInstanceValid(activeReplacementNode) &&
                activeReplacementNode.Visible;

            _previewHeroClone = ImporterMemoryGuard.CloneHierarchySharingResources(_liveHeroNode);
            StripGizmosAndNonAuthenticNodes(
                _previewHeroClone,
                preserveActiveReplacement: _hasActiveCompleteReplacement,
                editingSlotId: _accessorySlotId,
                preserveSiblingAccessories: _workflowIntent == ImportWorkflowIntent.AccessoryAddOrReplace);
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
            PopulateDonorVanillaHeroDropdown();
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
            _refRigUndoStack.Clear();
            _refRigRedoStack.Clear();
            _isGuidedPinPlacement = false;
            _guidedPinStep = 0;
            _guidedPlacedPinKeys.Clear();
            _showOriginalPoseInTab1 = false;
            if (_btnPreviewOriginalPose != null)
            {
                _btnPreviewOriginalPose.SetPressedNoSignal(false);
                _btnPreviewOriginalPose.ThemeTypeVariation = "ToolbarButton";
            }
            _referenceRig = new ReferenceRigDef();
            _boneSourceFilterMode = 0;
            for (int k = 0; k < 3; k++)
            {
                if (_boneSourceFilterBtns[k] == null) continue;
                _boneSourceFilterBtns[k].SetPressedNoSignal(k == 0);
                _boneSourceFilterBtns[k].ThemeTypeVariation = k == 0 ? "ImporterPrimaryButton" : "ToolbarButton";
            }
            _previewFlipRestore = null;
            _btnFlipX?.SetPressedNoSignal(false);
            _btnFlipY?.SetPressedNoSignal(false);
            _btnFlipZ?.SetPressedNoSignal(false);
            _selectedPins.Clear();
            _keptPartTransforms.Clear();
            _originalMeshCutMasks.Clear();
            _importedMeshCutMasks.Clear();
            _userRestoredHeroTargets.Clear();
            ResetImportedSculptStateForModalOpen();
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
                // Reopening an existing accessory/model should show the bound rigged preview cleanly,
                // NOT jump into Reference Bones editing or Region Pins!
                _isEditReferenceBonesMode = false;
                _tab2SubView = 0;
                SyncPartialBodyUiState();
                UpdateTab2SectionVisibility();
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
                if (fullSrcSkeleton == null || fullSrcSkeleton.GetBoneCount() == 0)
                {
                    StartGuidedOneByOnePinPlacement(clearExisting: true);
                }
                UpdateTab2SectionVisibility();
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
            _originalMeshCutMasks.Clear();
            if (options.OriginalMeshCutMasks != null)
            {
                foreach (var (k, rle) in options.OriginalMeshCutMasks)
                {
                    if (!string.IsNullOrWhiteSpace(k) && rle != null && rle.Length >= 2)
                        _originalMeshCutMasks[k] = (int[])rle.Clone();
                }
            }
            _importedMeshCutMasks.Clear();
            if (options.ImportedMeshCutMasks != null)
            {
                foreach (var (k, rle) in options.ImportedMeshCutMasks)
                {
                    if (!string.IsNullOrWhiteSpace(k) && rle != null && rle.Length >= 2)
                        _importedMeshCutMasks[k] = (int[])rle.Clone();
                }
            }
            RefreshAllMeshCutCardBadges();
            if (_chkClampLimbStretching != null) _chkClampLimbStretching.ButtonPressed = options.ClampLimbStretching;
            if (_chkStabilizeNeckFloatingHead != null) _chkStabilizeNeckFloatingHead.ButtonPressed = options.StabilizeNeckFloatingHead;
            if (_chkAutoCounterTwistWeights != null) _chkAutoCounterTwistWeights.ButtonPressed = options.AutoCounterTwistWeights;

            if (options.ReferenceRig != null)
            {
                _referenceRig = options.ReferenceRig.Clone();
                _landmarkPins = new Dictionary<string, Vector3>(_referenceRig.Joints, StringComparer.OrdinalIgnoreCase);
                _isEditReferenceBonesMode = false;
                SyncReferenceRigControlsFromDef();
            }
            else if (options.LandmarkPins != null && options.LandmarkPins.Count > 0)
            {
                _landmarkPins = new Dictionary<string, Vector3>(options.LandmarkPins);
                _referenceRig = new ReferenceRigDef { Joints = new Dictionary<string, Vector3>(_landmarkPins, StringComparer.OrdinalIgnoreCase) };
                _isEditReferenceBonesMode = false;
                SyncReferenceRigControlsFromDef();
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

            // 3. Restore Rigging Method (restore partial-body & imported sculpt options first)
            RestoreImportedSculptOptions(options);
            RestorePartialBodyRigOptions(options);
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
                        bool included = options.IncludedImportedSubmeshNames.Contains(sName);
                        chk.SetPressedNoSignal(included);
                        if (_importedSubmeshCards.TryGetValue(chk, out var impCard) && GodotObject.IsInstanceValid(impCard))
                            impCard.SetActive(included);
                    }
                }
                SyncPreviewModelMeshVisibility();
                ComputeAccLocalBoundsCenter();
            }
            if (options.KeptOriginalSubmeshNames != null)
            {
                foreach (var chk in _heroKeepCheckboxes)
                {
                    if (chk.HasMeta("SubmeshName"))
                    {
                        string sName = chk.GetMeta("SubmeshName").AsString();
                        bool kept;
                        int surfIdxSep = sName.IndexOf(CustomModelAutoRigger.SurfaceKeepSeparator, StringComparison.Ordinal);
                        if (surfIdxSep >= 0 && int.TryParse(sName.Substring(surfIdxSep + CustomModelAutoRigger.SurfaceKeepSeparator.Length), out int sIdx))
                        {
                            string baseMeshName = sName.Substring(0, surfIdxSep);
                            kept = CustomModelAutoRigger.IsOriginalSurfaceKept(options.KeptOriginalSubmeshNames, baseMeshName, sIdx);
                        }
                        else
                        {
                            kept = options.KeptOriginalSubmeshNames.Contains(sName);
                        }
                        chk.SetPressedNoSignal(kept);
                        if (_heroKeepCards.TryGetValue(chk, out var keepCard) && GodotObject.IsInstanceValid(keepCard))
                            keepCard.SetActive(kept);
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
            // Clear auto-detected fresh-import defaults first so saved Skinned Body choices and custom
            // socket bone / rotation edits are faithfully restored instead of being overwritten!
            _submeshSocketAssignments.Clear();
            _submeshHeroReplacements.Clear();
            _submeshSnapToSocket.Clear();
            _submeshSocketOffsetPos.Clear();
            _submeshSocketOffsetRot.Clear();
            _submeshSocketScale.Clear();

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
                    var transformBox = ctrl.Pitch?.GetParent()?.GetParent() as Control;
                    if (transformBox != null) transformBox.Visible = true;

                    for (int i = 0; i < ctrl.Socket.ItemCount; i++)
                    {
                        if (string.Equals(ctrl.Socket.GetItemText(i).Replace("★ ", ""), bName, StringComparison.OrdinalIgnoreCase))
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
                else
                {
                    // Explicitly reset to Skinned Body if saved options did not assign a rigid socket
                    ctrl.Role.Select(0);
                    var socketParent = ctrl.Socket.GetParent() as Control;
                    if (socketParent != null) socketParent.Visible = false;
                    var placementParent = ctrl.Placement.GetParent() as Control;
                    if (placementParent != null) placementParent.Visible = false;
                    var transformBox = ctrl.Pitch?.GetParent()?.GetParent() as Control;
                    if (transformBox != null) transformBox.Visible = false;
                }
            }
            _isGizmoUpdatingFromUI = false;

            RefreshKeptPartTargetList();
            SyncKeptPartSpinboxesFromSelected();
            ApplyTransformToPreviewModel(immediate: true);
        }

        private bool IsUnderCustomReplacementContainer(Node node)
        {
            Node cur = node;
            while (cur != null)
            {
                if (string.Equals(cur.Name.ToString(), "Custom_Rigged_Character", StringComparison.Ordinal))
                    return true;
                cur = cur.GetParent();
            }
            return false;
        }

        private bool IsUnderActivePreviewAccessoryContainer(Node node)
        {
            if (_previewRiggedContainer != null && GodotObject.IsInstanceValid(_previewRiggedContainer))
            {
                Node cur = node;
                while (cur != null)
                {
                    if (cur == _previewRiggedContainer) return true;
                    cur = cur.GetParent();
                }
            }
            return false;
        }

        private void CaptureOriginalHeroMaterials(Node3D clone)
        {
            _previewHeroOriginalMaterials.Clear();
            if (clone == null || !GodotObject.IsInstanceValid(clone)) return;

            foreach (var mi in CustomModelAutoRigger.CollectMeshes(clone))
            {
                if (IsUnderActivePreviewAccessoryContainer(mi)) continue;
                bool isMainReplacement = _hasActiveCompleteReplacement && IsUnderCustomReplacementContainer(mi);
                if (!isMainReplacement && mi.HasMeta("IsCustomImportedMesh") && mi.GetMeta("IsCustomImportedMesh").AsBool()) continue;
                string miName = mi.Name.ToString().ToLowerInvariant();
                if (!isMainReplacement && (miName.Contains("custom_rigged") || miName.Contains("previewmodel"))) continue;

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
                if (IsUnderActivePreviewAccessoryContainer(mi)) continue;
                bool isMainReplacement = _hasActiveCompleteReplacement && IsUnderCustomReplacementContainer(mi);
                // Never apply ghost material to custom imported meshes unless it is the active complete replacement base in Accessory mode
                if (!isMainReplacement && mi.HasMeta("IsCustomImportedMesh") && mi.GetMeta("IsCustomImportedMesh").AsBool())
                {
                    continue;
                }
                string miName = mi.Name.ToString().ToLowerInvariant();
                if (!isMainReplacement && (miName.Contains("custom_rigged") || miName.Contains("previewmodel")))
                {
                    continue;
                }

                // In Accessory mode on a complete replacement model, keep hidden original vanilla meshes hidden
                if (_hasActiveCompleteReplacement && !isMainReplacement &&
                    mi.HasMeta("UserHiddenByCustomModel") && mi.GetMeta("UserHiddenByCustomModel").AsBool())
                {
                    mi.Visible = false;
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
                if (!GodotObject.IsInstanceValid(chk)) continue;
                if (_heroKeepCards.TryGetValue(chk, out var keepCard) && GodotObject.IsInstanceValid(keepCard))
                {
                    keepCard.SetActive(chk.ButtonPressed);
                }
                if (chk.ButtonPressed && chk.HasMeta("SubmeshName"))
                {
                    keptNames.Add(chk.GetMeta("SubmeshName").AsString());
                }
            }

            UpdateHeroKeepCountBadge();
            bool pinEditOverride = IsPinEditActive() || IsImportedSculptActive();
            foreach (var mi in _previewGhostMeshes)
            {
                if (!GodotObject.IsInstanceValid(mi)) continue;

                string miName = mi.Name.ToString();
                int surfCount = mi.Mesh?.GetSurfaceCount() ?? 0;

                // During Reference Pin Edit mode, apply the compact 3-state Hero Mesh visibility override (Show / Ghost / Hide)
                if (pinEditOverride)
                {
                    if (_pinEditHeroVisMode == 2) // Hide
                    {
                        mi.Visible = false;
                        continue;
                    }
                    if (_pinEditHeroVisMode == 1) // Ghost
                    {
                        mi.MaterialOverride = _ghostMaterial;
                        for (int s = 0; s < surfCount; s++)
                            mi.SetSurfaceOverrideMaterial(s, _ghostMaterial);
                        mi.Visible = true;
                        continue;
                    }
                    // _pinEditHeroVisMode == 0: Show full original hero materials
                    if (_previewHeroOriginalMaterials.TryGetValue(mi, out var origFull))
                    {
                        mi.MaterialOverride = origFull.Override;
                        for (int s = 0; s < surfCount; s++)
                        {
                            Material mat = (origFull.SurfaceOverrides != null && s < origFull.SurfaceOverrides.Length)
                                ? origFull.SurfaceOverrides[s]
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
                    continue;
                }

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

        private readonly HashSet<string> _userRestoredHeroTargets = new(StringComparer.OrdinalIgnoreCase);

        private static void StripGizmosAndNonAuthenticNodes(Node root, bool preserveActiveReplacement = false, string editingSlotId = null, bool preserveSiblingAccessories = false)
        {
            if (root == null) return;
            var toRemove = new List<Node>();
            FindNodesToStrip(root, toRemove, preserveActiveReplacement, editingSlotId, preserveSiblingAccessories);
            foreach (var node in toRemove)
            {
                if (GodotObject.IsInstanceValid(node))
                {
                    node.GetParent()?.RemoveChild(node);
                    node.Free();
                }
            }
        }

        private static void FindNodesToStrip(Node node, List<Node> toRemove, bool preserveActiveReplacement = false, string editingSlotId = null, bool preserveSiblingAccessories = false)
        {
            string editingContainerName = !string.IsNullOrWhiteSpace(editingSlotId)
                ? CustomModelAutoRigger.GetContainerNameForSlot(editingSlotId)
                : null;

            foreach (var child in node.GetChildren())
            {
                string cName = child.Name.ToString();
                string cType = child.GetType().Name;
                string lower = cName.ToLowerInvariant();

                if (cType.Contains("SkeletonGizmo") || cType.Contains("BoneLayer") || cType.Contains("CharacterIK") ||
                    cName.StartsWith("@") || (child is BoneAttachment3D && cName.StartsWith("Pick_", StringComparison.Ordinal)) ||
                    lower.Contains("gizmo") || lower.Contains("bonepicker") ||
                    lower.Contains("pickerhandle") || lower.Contains("pickerarea") || lower.Contains("pickershape") ||
                    lower.Contains("ikhandle") || lower.Contains("bonehandle") || lower.Contains("resizehandle") ||
                    lower.Contains("tipvisual") || lower.Contains("polevisual") ||
                    lower == "customjigglebonedriver" || lower == "source2boneconstraintdriver")
                {
                    toRemove.Add(child);
                    continue;
                }

                // Keep Custom_Rigged_Character when adding/editing an accessory on top of a complete replacement model
                if (preserveActiveReplacement && string.Equals(cName, "Custom_Rigged_Character", StringComparison.Ordinal))
                {
                    continue;
                }

                // When adding/editing an accessory, keep ALL OTHER visible sibling accessories intact in the preview
                // (so previous accessories & their cut masks remain visible while fitting the new accessory!)
                if (preserveActiveReplacement || preserveSiblingAccessories)
                {
                    if (cName.StartsWith("Custom_Accessory_", StringComparison.Ordinal))
                    {
                        if (!string.IsNullOrEmpty(editingContainerName) &&
                            string.Equals(cName, editingContainerName, StringComparison.OrdinalIgnoreCase))
                        {
                            toRemove.Add(child);
                        }
                        continue;
                    }
                }

                if (child is MeshInstance3D mi)
                {
                    // Remove duplicate generated split submeshes on preview clone
                    if (mi.HasMeta("ParentCompositeMesh") || mi.Name.ToString().Contains("_surf_"))
                    {
                        toRemove.Add(child);
                        continue;
                    }

                    // Restore original composite meshes (unless preserving an active complete replacement)
                    if (mi.HasMeta("IsHiddenComposite"))
                    {
                        bool hiddenByCustom = preserveActiveReplacement &&
                                              mi.HasMeta("UserHiddenByCustomModel") &&
                                              mi.GetMeta("UserHiddenByCustomModel").AsBool();
                        mi.Visible = !hiddenByCustom;
                        mi.RemoveMeta("IsHiddenComposite");
                        if (mi.HasMeta("GeneratedSubmeshes")) mi.RemoveMeta("GeneratedSubmeshes");
                    }

                    if (!DeadlockPlayground.Painter.HeroMeshHierarchy.IsOriginalHeroMesh(mi) || mi.HasMeta("IsCustomImportedMesh"))
                    {
                        toRemove.Add(child);
                        continue;
                    }

                    if (preserveActiveReplacement && mi.HasMeta("UserHiddenByCustomModel") && mi.GetMeta("UserHiddenByCustomModel").AsBool())
                    {
                        mi.Visible = false;
                    }
                    else
                    {
                        // Restore authentic hero submesh to full visibility on reference ghost clone
                        mi.Visible = true;
                        if (mi.HasMeta("UserHiddenByCustomModel")) mi.RemoveMeta("UserHiddenByCustomModel");
                        if (mi.HasMeta("UserVisibility")) mi.SetMeta("UserVisibility", true);
                    }
                }

                FindNodesToStrip(child, toRemove, preserveActiveReplacement, editingSlotId, preserveSiblingAccessories);
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
        #endregion
    }
}
