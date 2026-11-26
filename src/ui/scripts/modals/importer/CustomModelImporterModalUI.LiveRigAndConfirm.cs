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
        #region Actions: Real-Time Test Rig & Confirm

        private void OnTestAnimSelected(long index)
        {
            if (_previewHeroSkeleton == null) return;
            // If user explicitly picks a preview animation while in Bind-Pose 3D synthetic joint edit mode,
            // exit 3D edit mode cleanly so CustomJiggleBoneDriver resumes and animates all hair/cloth chains live!
            if (index > 0 && _isGizmoEditingSyntheticChain)
            {
                _synSavedAnimStateValid = false;
                SetSyntheticChainGizmoActive(false);
            }
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
            bool keepFingersRigid = (_chkKeepFingersRigid?.ButtonPressed ?? false) || fingerMode == FingerRigMode.RigidHand;
            if (method == CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig && _referenceRig != null)
            {
                bool articulated = _referenceRig.FingerMode == ReferenceFingerRigMode.ArticulatedFingers;
                fingerMode = articulated ? FingerRigMode.DeadlockFingerBones : FingerRigMode.RigidHand;
                keepFingersRigid = !articulated;
            }
            var accBoneMode = _optAccBoneMode != null && _optAccBoneMode.Selected >= 0
                ? (AccessoryBoneMode)_optAccBoneMode.GetItemId(_optAccBoneMode.Selected)
                : AccessoryBoneMode.RigidToOneBone;
            int famId = _optRigFamilyOverride != null && _optRigFamilyOverride.Selected >= 0
                ? _optRigFamilyOverride.GetItemId(_optRigFamilyOverride.Selected)
                : -1;
            RigFamily? rigFamilyOverride = famId >= 0 ? (RigFamily)famId : null;

            // In Accessory workflow, honor BOTH explicit Tab 1 "Hide/Replace" checkboxes AND any vanilla hero meshes
            // unchecked in Tab 4 ("Keep from Original Hero") so hidden vanilla meshes NEVER come back after Confirm/Export!
            var effectiveReplacedTargets = new List<string>();
            var effectiveIntent = _workflowIntent == ImportWorkflowIntent.AccessoryAddOrReplace
                ? _accessoryIntent
                : CustomModelAutoRigger.AccessoryIntent.FullCharacter;
            if (_workflowIntent == ImportWorkflowIntent.AccessoryAddOrReplace)
            {
                var hiddenSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (_accessoryIntent == CustomModelAutoRigger.AccessoryIntent.ReplacePart)
                {
                    foreach (var c in _accReplaceCheckboxes)
                    {
                        if (GodotObject.IsInstanceValid(c) && c.ButtonPressed && c.HasMeta("SubmeshName"))
                            hiddenSet.Add(c.GetMeta("SubmeshName").AsString());
                    }
                }
                foreach (var keepChk in _heroKeepCheckboxes)
                {
                    if (GodotObject.IsInstanceValid(keepChk) && !keepChk.ButtonPressed && keepChk.HasMeta("SubmeshName"))
                        hiddenSet.Add(keepChk.GetMeta("SubmeshName").AsString());
                }
                if (hiddenSet.Count > 0)
                {
                    effectiveReplacedTargets.AddRange(hiddenSet);
                    effectiveIntent = CustomModelAutoRigger.AccessoryIntent.ReplacePart;
                }
            }

            return new CustomModelAutoRigger.RiggingOptions
            {
                SlotId = _accessorySlotId ?? "",
                AccessoryName = resolvedAccName,
                Intent = effectiveIntent,
                ExplicitReplacedTargets = effectiveReplacedTargets,
                ExplicitRestoredTargets = _userRestoredHeroTargets.Count > 0 ? new List<string>(_userRestoredHeroTargets) : new List<string>(),
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
                SyntheticChainsToRebind = new HashSet<string>(_dirtySyntheticChainIds, StringComparer.OrdinalIgnoreCase),
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
                KeepFingersRigidOnHand = keepFingersRigid,
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
                OriginalMeshCutMasks = CloneCutMasks(_originalMeshCutMasks),
                ImportedMeshCutMasks = CloneCutMasks(_importedMeshCutMasks),
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
                PartialRegion = GetSelectedPartialBodyRegion(),
                PartialEngine = _partialBodyEngine,
                PartialIncludeSpine = _chkPartialSpine?.ButtonPressed ?? true,
                PartialIncludeArms = _chkPartialArms?.ButtonPressed ?? true,
                PartialIncludeHands = _chkPartialHands?.ButtonPressed ?? false,
                PartialIncludeLegs = _chkPartialLegs?.ButtonPressed ?? false,
                PartialIncludeFeet = _chkPartialFeet?.ButtonPressed ?? false,
                PartialIncludeNeck = _chkPartialNeck?.ButtonPressed ?? false,
                PartialIncludeHead = _chkPartialHead?.ButtonPressed ?? false,
                ImportedMeshBottomFlare = _globalImpBottomFlare,
                ImportedMeshTopFlare = _globalImpTopFlare,
                ImportedMeshInflateMeters = _globalImpInflateMeters,
                PerMeshScaleXYZ = _perMeshScaleXyz.Count > 0 ? new Dictionary<string, Vector3>(_perMeshScaleXyz, StringComparer.OrdinalIgnoreCase) : null,
                PerMeshBottomFlare = _perMeshBottomFlare.Count > 0 ? new Dictionary<string, float>(_perMeshBottomFlare, StringComparer.OrdinalIgnoreCase) : null,
                PerMeshTopFlare = _perMeshTopFlare.Count > 0 ? new Dictionary<string, float>(_perMeshTopFlare, StringComparer.OrdinalIgnoreCase) : null,
                PerMeshInflateMeters = _perMeshInflateMeters.Count > 0 ? new Dictionary<string, float>(_perMeshInflateMeters, StringComparer.OrdinalIgnoreCase) : null,
                ImportedMeshSculptDeltas = CloneImportedSculptDeltas(_importedSculptDeltas),
                LandmarkPins = ((method == CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig ||
                                (method == CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig && _partialBodyEngine == CustomModelAutoRigger.PartialBodyEngine.SemiBodyPins)) &&
                                _landmarkPins.Count >= CustomLandmarkWeightSolver.PinNames.Length)
                    ? new Dictionary<string, Vector3>(_landmarkPins)
                    : null,
                ReferenceRig = ((method == CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig ||
                                (method == CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig && _partialBodyEngine == CustomModelAutoRigger.PartialBodyEngine.SemiBodyPins)) &&
                                _referenceRig != null && _referenceRig.Joints.Count >= CustomLandmarkWeightSolver.PinNames.Length)
                    ? _referenceRig.Clone()
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

            // CRITICAL: Only flush active painted weights when a live brush stroke is actually dirty!
            // Completed strokes, Smooth All, Mirror, Undo/Redo, and Reset Bone already call
            // CommitPaintSurfaceOverrides() directly. Unconditionally flushing unedited _paintSurfaces
            // froze weights on inspection or after Re-Bind / Reset All and skipped AutoCounterTwistWeights!
            if (_paintSurfaces.Count > 0 && _strokeDirty)
            {
                CommitPaintSurfaceOverrides();
                _strokeDirty = false;
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

            // 2. Stop active animation cleanly & reset skeleton to pure Bind Pose (Rest Pose) before rigging
            _previewAnimPlayer?.Stop();
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
                SnapshotBakedDonorLocalTransforms();
                _previewModelRoot.Visible = false;

                CustomModelAutoRigger.UpdateCustomModelRuntimePosing(_previewHeroSkeleton, options.ClampLimbStretching);
                _previewConstraintDriver?.EvaluateNow(0f);

                CustomModelAutoRigger.RecomputeHeroDeformations(_previewHeroClone, options);
                ApplyGhostMaterialToClone(_previewHeroClone);
                if (_previewHeroClone != null) _previewHeroClone.Visible = true;

                SetBottomStatus($"Rigged in real time ({result.TotalVertices:N0} vertices bound).", StatusTone.Success);

                // Resume animation smoothly at exact position ONLY when not in 3D synthetic joint Bind Pose edit mode
                if (!_isGizmoEditingSyntheticChain && !string.IsNullOrEmpty(currentAnimName) && _previewAnimPlayer != null && _previewAnimPlayer.HasAnimation(currentAnimName))
                {
                    _previewAnimPlayer.Play(currentAnimName);
                    _previewAnimPlayer.SpeedScale = animSpeed;
                    _previewAnimPlayer.Seek(currentAnimPos, true);
                    if (!wasPlaying)
                    {
                        _previewAnimPlayer.Pause();
                    }
                }
                else if (!_isGizmoEditingSyntheticChain)
                {
                    ApplyCalibrationPoseToGhostSkeleton();
                }

                // Reset jiggle/cloth simulation state AFTER restoring the active animation pose so re-binding
                // never injects a T-Pose -> Animation teleport velocity spike into synthetic hair/cloth bones!
                if (_previewHeroClone?.FindChild("CustomJiggleBoneDriver", false, false) is DeadlockPlayground.Animation.CustomJiggleBoneDriver postBindDriver)
                {
                    if (_isGizmoEditingSyntheticChain)
                        postBindDriver.SetSimulationEnabled(false);
                    postBindDriver.ResetChainsToRestPose();
                }

                if (!_isGizmoDragging && !(_previewGizmo?.Editing ?? false))
                {
                    UpdateGizmoTargetTransform();
                }
                _paintSurfaces.Clear();
                if (_customSkinOverrides.Count == 0)
                {
                    _baselineSkinOverrides.Clear();
                }
                else if (options.SyntheticChainsToRebind != null && options.SyntheticChainsToRebind.Count > 0 &&
                         result.ContainerNode != null && GodotObject.IsInstanceValid(result.ContainerNode) && _previewHeroSkeleton != null)
                {
                    // Sync newly bound synthetic chain weights back into _customSkinOverrides so subsequent
                    // re-binds / preset swaps never re-run procedural weighting or dilute overlapping painted chains!
                    var syncedSurfs = CustomWeightPaintEditor.CaptureSurfaces(result.ContainerNode);
                    var syncedSkin = syncedSurfs.Count > 0 ? syncedSurfs[0].MeshInstance?.Skin : null;
                    if (syncedSkin != null)
                    {
                        var syncedExp = CustomWeightPaintEditor.ExportOverrides(syncedSurfs, _previewHeroSkeleton, syncedSkin);
                        foreach (var (k, v) in syncedExp) _customSkinOverrides[k] = v;
                        UpdatePaintOverrideLabel();
                    }
                }
                ApplyLandmarkPreviewState();
                RebuildActiveCustomWeightedBoneSet();
                if (_showBones) RedrawBoneWireframe();
                RefreshReviewTabFromReport(result.Report);
                MarkRemapAppliedBadge();
                // Sync newly discovered & updated authored chains back into _boneChains without overwriting
                // existing user-tuned physics presets (prevents moving/scaling an accessory from resetting physics settings).
                if (options.Method == CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig ||
                    (options.Method == CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig &&
                     options.PartialEngine == CustomModelAutoRigger.PartialBodyEngine.SemiBodyPins))
                {
                    var activeRefIds = new HashSet<string>(
                        (options.BoneChains ?? new List<BoneChainDef>())
                            .Where(c => c != null && !string.IsNullOrEmpty(c.ChainId) && c.ChainId.StartsWith("refcloth_", StringComparison.OrdinalIgnoreCase))
                            .Select(c => c.ChainId),
                        StringComparer.OrdinalIgnoreCase);
                    _boneChains.RemoveAll(c =>
                        c != null && !c.IsSynthetic &&
                        !string.IsNullOrEmpty(c.ChainId) &&
                        c.ChainId.StartsWith("refcloth_", StringComparison.OrdinalIgnoreCase) &&
                        !activeRefIds.Contains(c.ChainId));
                }
                if (options.BoneChains != null && options.BoneChains.Count > 0)
                {
                    var existingById = new Dictionary<string, BoneChainDef>(StringComparer.OrdinalIgnoreCase);
                    var existingByRoot = new Dictionary<string, BoneChainDef>(StringComparer.OrdinalIgnoreCase);
                    foreach (var c in _boneChains)
                    {
                        if (c == null) continue;
                        if (!string.IsNullOrEmpty(c.ChainId)) existingById[c.ChainId] = c;
                        if (!string.IsNullOrEmpty(c.RootSourceBone)) existingByRoot[c.RootSourceBone] = c;
                    }
                    foreach (var oc in options.BoneChains)
                    {
                        if (oc == null || string.IsNullOrEmpty(oc.RootSourceBone)) continue;
                        BoneChainDef match = (!string.IsNullOrEmpty(oc.ChainId) && existingById.TryGetValue(oc.ChainId, out var byId))
                            ? byId
                            : (existingByRoot.TryGetValue(oc.RootSourceBone, out var byRoot) ? byRoot : null);
                        if (match != null)
                        {
                            match.Name = oc.Name;
                            match.RootSourceBone = oc.RootSourceBone;
                            match.AnchorHeroBone = oc.AnchorHeroBone;
                            match.SourceBoneNames = new List<string>(oc.SourceBoneNames);
                            match.AppendedHeroBoneNames = new List<string>(oc.AppendedHeroBoneNames);
                        }
                        else
                        {
                            var cloned = oc.Clone();
                            _boneChains.Add(cloned);
                            if (!string.IsNullOrEmpty(cloned.ChainId)) existingById[cloned.ChainId] = cloned;
                            existingByRoot[cloned.RootSourceBone] = cloned;
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
                DeadlockPlayground.Core.SessionCrashGuard.ScheduleImporterDraftSave(
                    _heroCodename,
                    _filePath,
                    (int)_workflowIntent,
                    _accessorySlotId,
                    options);
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

        private bool _isGizmoEditingSyntheticChain = false;
        private int _selectedSyntheticJointIndex = 0;
        private bool _showSyntheticWeightRadius = true;
        private readonly HashSet<string> _dirtySyntheticChainIds = new(StringComparer.OrdinalIgnoreCase);

        private void MarkSyntheticChainDirty(BoneChainDef chain)
        {
            if (chain == null || !chain.IsSynthetic) return;
            string cid = !string.IsNullOrEmpty(chain.ChainId) ? chain.ChainId : (chain.Name ?? "");
            if (!string.IsNullOrEmpty(cid)) _dirtySyntheticChainIds.Add(cid);
        }

        private void UnmarkSyntheticChainDirty(BoneChainDef chain)
        {
            if (chain == null) return;
            string cid = !string.IsNullOrEmpty(chain.ChainId) ? chain.ChainId : (chain.Name ?? "");
            if (!string.IsNullOrEmpty(cid)) _dirtySyntheticChainIds.Remove(cid);
        }

        private bool IsSyntheticChainDirty(BoneChainDef chain)
        {
            if (chain == null || !chain.IsSynthetic) return false;
            string cid = !string.IsNullOrEmpty(chain.ChainId) ? chain.ChainId : (chain.Name ?? "");
            return !string.IsNullOrEmpty(cid) && _dirtySyntheticChainIds.Contains(cid);
        }
        private bool _synSavedAnimStateValid = false;
        private string _synSavedAnimName = null;
        private double _synSavedAnimPos = 0.0;
        private bool _synSavedAnimWasPlaying = false;
        private float _synSavedAnimSpeed = 1.0f;
        private MeshInstance3D _syntheticChainWireframeMesh;
        private ImmediateMesh _syntheticChainImmediateMesh;
        private StandardMaterial3D _syntheticChainWireMat;

        private static Basis BuildSyntheticGizmoBasisFromDir(Vector3 dir)
        {
            Vector3 downDir = dir.LengthSquared() > 1e-6f ? dir.Normalized() : Vector3.Down;
            Vector3 upAxis = -downDir;
            Vector3 refFwd = MathF.Abs(upAxis.Dot(Vector3.Back)) < 0.92f ? Vector3.Back : Vector3.Right;
            Vector3 xAxis = upAxis.Cross(refFwd).Normalized();
            Vector3 zAxis = xAxis.Cross(upAxis).Normalized();
            return new Basis(xAxis, upAxis, zAxis).Orthonormalized();
        }

        private List<Vector3> EnsureSyntheticJointPositions(BoneChainDef chain)
        {
            if (chain == null) return null;
            int segs = Math.Clamp(chain.SyntheticSegmentCount, 2, 12);
            int needed = segs + 1;
            if (chain.SyntheticJointPositionsHeroSpace != null && chain.SyntheticJointPositionsHeroSpace.Count == needed)
                return chain.SyntheticJointPositionsHeroSpace;

            Vector3 origin = chain.SyntheticOriginHeroSpace;
            if (origin.LengthSquared() < 1e-6f && _previewHeroSkeleton != null)
            {
                int aIdx = _previewHeroSkeleton.FindBone(chain.AnchorHeroBone ?? "spine_3");
                if (aIdx >= 0) origin = _previewHeroSkeleton.GetBoneGlobalRest(aIdx).Origin;
                chain.SyntheticOriginHeroSpace = origin;
            }
            Vector3 dir = chain.SyntheticDirectionHeroSpace.LengthSquared() > 1e-5f
                ? chain.SyntheticDirectionHeroSpace.Normalized()
                : Vector3.Down;
            float len = Math.Clamp(chain.SyntheticLengthMeters, 0.08f, 2.5f);

            chain.SyntheticJointPositionsHeroSpace ??= new List<Vector3>(needed);
            chain.SyntheticJointPositionsHeroSpace.Clear();
            for (int i = 0; i <= segs; i++)
                chain.SyntheticJointPositionsHeroSpace.Add(origin + dir * (len * i / segs));
            return chain.SyntheticJointPositionsHeroSpace;
        }

        private void SetSyntheticChainGizmoActive(bool active)
        {
            bool wasActive = _isGizmoEditingSyntheticChain;
            _isGizmoEditingSyntheticChain = active && _selectedChain != null && _selectedChain.IsSynthetic;

            if (_isGizmoEditingSyntheticChain && !wasActive)
            {
                // Entering 3D cloth joint edit: snapshot active animation and freeze in pure Bind Pose (Rest Pose)
                // so joints are placed & bound against undistorted rest geometry (matching Auto-Rig Reference Bones!).
                _synSavedAnimStateValid = false;
                if (_previewAnimPlayer != null && _optTestAnim != null && _optTestAnim.Selected > 0)
                {
                    string actAnim = !string.IsNullOrEmpty(_previewAnimPlayer.CurrentAnimation)
                        ? _previewAnimPlayer.CurrentAnimation
                        : _previewAnimPlayer.AssignedAnimation;
                    if (string.IsNullOrEmpty(actAnim) || !_previewAnimPlayer.HasAnimation(actAnim))
                        actAnim = _optTestAnim.GetItemText(_optTestAnim.Selected);
                    if (!string.IsNullOrEmpty(actAnim) && _previewAnimPlayer.HasAnimation(actAnim))
                    {
                        _synSavedAnimName = actAnim;
                        _synSavedAnimPos = _previewAnimPlayer.CurrentAnimationPosition;
                        _synSavedAnimWasPlaying = _previewAnimPlayer.IsPlaying();
                        _synSavedAnimSpeed = (float)_previewAnimPlayer.SpeedScale;
                        _synSavedAnimStateValid = true;
                    }
                    _previewAnimPlayer.Stop();
                }
                if (_previewHeroClone?.FindChild("CustomJiggleBoneDriver", false, false) is DeadlockPlayground.Animation.CustomJiggleBoneDriver drvEnter)
                {
                    drvEnter.SetSimulationEnabled(false);
                    drvEnter.ResetChainsToRestPose();
                }
                _previewHeroSkeleton?.ResetBonePoses();
            }
            else if (!_isGizmoEditingSyntheticChain && wasActive)
            {
                // Exiting 3D cloth joint edit: re-enable physics and restore previous test animation smoothly!
                if (_previewHeroClone?.FindChild("CustomJiggleBoneDriver", false, false) is DeadlockPlayground.Animation.CustomJiggleBoneDriver drvExit)
                {
                    bool masterPhys = _chkPhysicsMasterTab3?.ButtonPressed ?? true;
                    drvExit.SetSimulationEnabled(masterPhys && !IsWeightPaintActive());
                    drvExit.ResetChainsToRestPose();
                }
                if (_synSavedAnimStateValid && _previewAnimPlayer != null && !string.IsNullOrEmpty(_synSavedAnimName) && _previewAnimPlayer.HasAnimation(_synSavedAnimName))
                {
                    _previewAnimPlayer.Play(_synSavedAnimName);
                    _previewAnimPlayer.SpeedScale = _synSavedAnimSpeed;
                    _previewAnimPlayer.Seek(_synSavedAnimPos, true);
                    if (!_synSavedAnimWasPlaying) _previewAnimPlayer.Pause();
                    if (_previewHeroClone?.FindChild("CustomJiggleBoneDriver", false, false) is DeadlockPlayground.Animation.CustomJiggleBoneDriver drvPost)
                    {
                        drvPost.ResetChainsToRestPose();
                    }
                }
                _synSavedAnimStateValid = false;
            }

            if (_isGizmoEditingSyntheticChain)
            {
                _isGizmoEditingRefJoint = false;
                var pts = EnsureSyntheticJointPositions(_selectedChain);
                _selectedSyntheticJointIndex = Math.Clamp(_selectedSyntheticJointIndex, 0, Math.Max(0, (pts?.Count ?? 1) - 1));
            }
            UpdateGizmoTargetTransform();
            RedrawSyntheticChainPreview();
            SyncSyntheticTransformControlsFromSelected();
            UpdateSyntheticChainViewportHud();
        }

        private void SelectSyntheticJointIndex(int jointIdx)
        {
            if (_selectedChain == null || !_selectedChain.IsSynthetic) return;
            var pts = EnsureSyntheticJointPositions(_selectedChain);
            if (pts == null || pts.Count == 0) return;
            _selectedSyntheticJointIndex = Math.Clamp(jointIdx, 0, pts.Count - 1);
            if (!_isGizmoEditingSyntheticChain) _isGizmoEditingSyntheticChain = true;
            UpdateGizmoTargetTransform();
            RedrawSyntheticChainPreview();
            SyncSyntheticTransformControlsFromSelected();
            UpdateSyntheticChainViewportHud();
        }

        private Transform3D GetSyntheticAnchorAnimDelta(BoneChainDef chain)
        {
            if (chain == null || _previewHeroSkeleton == null || !GodotObject.IsInstanceValid(_previewHeroSkeleton))
                return Transform3D.Identity;
            int aIdx = _previewHeroSkeleton.FindBone(chain.AnchorHeroBone ?? "spine_3");
            if (aIdx < 0) return Transform3D.Identity;
            Transform3D rest = _previewHeroSkeleton.GetBoneGlobalRest(aIdx);
            Transform3D pose = _previewHeroSkeleton.GetBoneGlobalPose(aIdx);
            return pose * rest.AffineInverse();
        }

        private bool TryHandleSyntheticJointLeftClick(InputEvent @event)
        {
            if (!_isGizmoEditingSyntheticChain || _selectedChain == null || !_selectedChain.IsSynthetic || IsWeightPaintActive()) return false;
            if (_previewCamera == null || _subViewportContainer == null) return false;
            if (_previewGizmo != null && (_previewGizmo.Editing || _previewGizmo.Hovering)) return false;
            if (@event is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Left || !mb.Pressed || mb.ShiftPressed)
                return false;

            var pts = EnsureSyntheticJointPositions(_selectedChain);
            if (pts == null || pts.Count == 0) return false;

            Transform3D animDelta = GetSyntheticAnchorAnimDelta(_selectedChain);
            Vector2 clickPos = mb.Position;
            int bestIdx = -1;
            float bestDist = 24f;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 wp = animDelta * pts[i];
                if (_previewCamera.IsPositionBehind(wp)) continue;
                Vector2 proj = _previewCamera.UnprojectPosition(wp);
                float d = proj.DistanceTo(clickPos);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestIdx = i;
                }
            }

            if (bestIdx >= 0)
            {
                SelectSyntheticJointIndex(bestIdx);
                return true;
            }

            // Fallback: allow clicking any other visible synthetic chain's joint directly in 3D to switch to it!
            BoneChainDef bestOtherChain = null;
            int bestOtherJoint = -1;
            float bestOtherDist = 22f;
            foreach (var ch in _boneChains)
            {
                if (ch == null || !ch.IsSynthetic || ReferenceEquals(ch, _selectedChain)) continue;
                var oPts = EnsureSyntheticJointPositions(ch);
                if (oPts == null || oPts.Count == 0) continue;
                Transform3D oDelta = GetSyntheticAnchorAnimDelta(ch);
                for (int i = 0; i < oPts.Count; i++)
                {
                    Vector3 wp = oDelta * oPts[i];
                    if (_previewCamera.IsPositionBehind(wp)) continue;
                    Vector2 proj = _previewCamera.UnprojectPosition(wp);
                    float d = proj.DistanceTo(clickPos);
                    if (d < bestOtherDist)
                    {
                        bestOtherDist = d;
                        bestOtherChain = ch;
                        bestOtherJoint = i;
                    }
                }
            }
            if (bestOtherChain != null && bestOtherJoint >= 0)
            {
                _selectedChain = bestOtherChain;
                _selectedSyntheticJointIndex = bestOtherJoint;
                RefreshClothingChainsPanel();
                UpdateGizmoTargetTransform();
                RedrawSyntheticChainPreview();
                return true;
            }
            return false;
        }

        private void ClearSyntheticChainPreview()
        {
            if (_syntheticChainImmediateMesh != null)
            {
                _syntheticChainImmediateMesh.ClearSurfaces();
            }
            if (_syntheticChainWireframeMesh != null && GodotObject.IsInstanceValid(_syntheticChainWireframeMesh))
            {
                _syntheticChainWireframeMesh.Visible = false;
            }
        }

        private void RedrawSyntheticChainPreview()
        {
            if (_previewWorldRoot == null || !GodotObject.IsInstanceValid(_previewWorldRoot)) return;
            bool onBonesTab = _rightTabs == null || _rightTabs.CurrentTab == 2;
            bool inPhysicsSubView = _boxSubViewPhysics == null || _boxSubViewPhysics.Visible;
            if (!_isGizmoEditingSyntheticChain || _selectedChain == null || !_selectedChain.IsSynthetic || IsWeightPaintActive() || !onBonesTab || !inPhysicsSubView)
            {
                ClearSyntheticChainPreview();
                return;
            }

            // Reuse a single persistent ImmediateMesh + MeshInstance3D so dragging runs at 60 FPS with zero allocation lag!
            if (_syntheticChainWireframeMesh == null || !GodotObject.IsInstanceValid(_syntheticChainWireframeMesh))
            {
                _syntheticChainImmediateMesh = new ImmediateMesh();
                _syntheticChainWireMat = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    VertexColorUseAsAlbedo = true,
                    NoDepthTest = true,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha
                };
                _syntheticChainWireframeMesh = new MeshInstance3D
                {
                    Name = "SyntheticChainPreviewOverlay",
                    Mesh = _syntheticChainImmediateMesh,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
                };
                // Strictly clear Visual Layer 21 (bit 20)
                _syntheticChainWireframeMesh.Layers &= ~(1u << 20);
                _previewWorldRoot.AddChild(_syntheticChainWireframeMesh);
            }

            _syntheticChainWireframeMesh.Visible = true;
            _syntheticChainImmediateMesh.ClearSurfaces();

            var pts = EnsureSyntheticJointPositions(_selectedChain);
            if (pts == null || pts.Count < 2) return;
            int segs = pts.Count - 1;
            float rad = Math.Clamp(_selectedChain.SyntheticInfluenceRadius, 0.04f, 1.5f);
            Transform3D animDelta = GetSyntheticAnchorAnimDelta(_selectedChain);

            var im = _syntheticChainImmediateMesh;
            im.SurfaceBegin(Mesh.PrimitiveType.Lines, _syntheticChainWireMat);

            // 0. Draw all OTHER synthetic chains subtly so mirrored/multiple chains are visible simultaneously in 3D!
            foreach (var other in _boneChains)
            {
                if (other == null || !other.IsSynthetic || ReferenceEquals(other, _selectedChain)) continue;
                var oPts = EnsureSyntheticJointPositions(other);
                if (oPts == null || oPts.Count < 2) continue;
                int oSegs = oPts.Count - 1;
                Transform3D oDelta = GetSyntheticAnchorAnimDelta(other);
                Color oSegCol = new Color(0.45f, 0.78f, 0.74f, 0.52f);
                Color oJntCol = new Color(0.70f, 0.88f, 0.85f, 0.55f);

                for (int i = 0; i < oSegs; i++)
                {
                    im.SurfaceSetColor(oSegCol);
                    im.SurfaceAddVertex(oDelta * oPts[i]);
                    im.SurfaceSetColor(oSegCol);
                    im.SurfaceAddVertex(oDelta * oPts[i + 1]);
                }
                for (int i = 0; i <= oSegs; i++)
                {
                    float m = 0.014f;
                    Vector3 jp = oDelta * oPts[i];
                    Vector3 px = jp + Vector3.Right * m, nx = jp - Vector3.Right * m;
                    Vector3 py = jp + Vector3.Up * m, ny = jp - Vector3.Up * m;
                    Vector3 pz = jp + Vector3.Back * m, nz = jp - Vector3.Back * m;
                    void OEdge(Vector3 a, Vector3 b) { im.SurfaceSetColor(oJntCol); im.SurfaceAddVertex(a); im.SurfaceSetColor(oJntCol); im.SurfaceAddVertex(b); }
                    OEdge(py, px); OEdge(py, nx); OEdge(py, pz); OEdge(py, nz);
                    OEdge(ny, px); OEdge(ny, nx); OEdge(ny, pz); OEdge(ny, nz);
                }
            }

            // 1. Draw active chain bone segments transformed by live anchor animation pose (gold -> cyan tip)
            for (int i = 0; i < segs; i++)
            {
                float t = i / (float)Math.Max(1, segs - 1);
                Color col = new Color(1.0f - 0.55f * t, 0.85f + 0.10f * t, 0.25f + 0.70f * t, 0.98f);
                im.SurfaceSetColor(col);
                im.SurfaceAddVertex(animDelta * pts[i]);
                im.SurfaceSetColor(col);
                im.SurfaceAddVertex(animDelta * pts[i + 1]);
            }

            // 2. Draw clickable 3D joint diamond markers (selected joint highlighted bright gold/white)
            for (int i = 0; i <= segs; i++)
            {
                bool isSel = _isGizmoEditingSyntheticChain && i == _selectedSyntheticJointIndex;
                float m = isSel ? 0.032f : 0.020f;
                Color jCol = isSel ? new Color(1.0f, 0.95f, 0.35f, 1.0f) : new Color(0.95f, 0.95f, 0.95f, 0.90f);
                Vector3 jp = animDelta * pts[i];
                Vector3 px = jp + Vector3.Right * m, nx = jp - Vector3.Right * m;
                Vector3 py = jp + Vector3.Up * m, ny = jp - Vector3.Up * m;
                Vector3 pz = jp + Vector3.Back * m, nz = jp - Vector3.Back * m;

                void Edge(Vector3 a, Vector3 b) { im.SurfaceSetColor(jCol); im.SurfaceAddVertex(a); im.SurfaceSetColor(jCol); im.SurfaceAddVertex(b); }
                Edge(py, px); Edge(py, nx); Edge(py, pz); Edge(py, nz);
                Edge(ny, px); Edge(ny, nx); Edge(ny, pz); Edge(ny, nz);
                Edge(px, pz); Edge(pz, nx); Edge(nx, nz); Edge(nz, px);
            }

            // 3. Optional Weight Influence Radius wireframe (clearly labeled, toggleable so it isn't confused with collision)
            if (_showSyntheticWeightRadius)
            {
                Color radCol = new Color(0.25f, 0.88f, 0.95f, 0.38f);
                const int ringSteps = 16;
                for (int ri = 0; ri <= segs; ri++)
                {
                    Vector3 segDir = ri < segs ? (pts[ri + 1] - pts[ri]) : (pts[segs] - pts[segs - 1]);
                    Basis ringBasis = (animDelta.Basis * BuildSyntheticGizmoBasisFromDir(segDir)).Orthonormalized();
                    Vector3 rx = ringBasis.X;
                    Vector3 rz = ringBasis.Z;
                    Vector3 center = animDelta * pts[ri];
                    for (int k = 0; k < ringSteps; k++)
                    {
                        float a0 = k * Mathf.Tau / ringSteps;
                        float a1 = (k + 1) * Mathf.Tau / ringSteps;
                        Vector3 p0 = center + (rx * MathF.Cos(a0) + rz * MathF.Sin(a0)) * rad;
                        Vector3 p1 = center + (rx * MathF.Cos(a1) + rz * MathF.Sin(a1)) * rad;
                        im.SurfaceSetColor(radCol);
                        im.SurfaceAddVertex(p0);
                        im.SurfaceSetColor(radCol);
                        im.SurfaceAddVertex(p1);
                    }
                }
            }

            im.SurfaceEnd();
        }

        private void UpdateGizmoTargetTransform()
        {
            SyncGizmoSpaceButton();
            if (_previewGizmo == null || _previewHeroSkeleton == null) return;

            bool onBonesTab = _rightTabs == null || _rightTabs.CurrentTab == 2;
            bool inPhysicsSubView = _boxSubViewPhysics == null || _boxSubViewPhysics.Visible;
            if (_isGizmoEditingSyntheticChain && _selectedChain != null && _selectedChain.IsSynthetic && _previewWorldRoot != null && !IsWeightPaintActive() && onBonesTab && inPhysicsSubView)
            {
                _isGizmoUpdatingFromUI = true;
                var pts = EnsureSyntheticJointPositions(_selectedChain);
                int jIdx = Math.Clamp(_selectedSyntheticJointIndex, 0, Math.Max(0, (pts?.Count ?? 1) - 1));
                _selectedSyntheticJointIndex = jIdx;

                // Joint 0 (Root) allows Move + Rotate (moves/aims entire chain); child joints allow free 3D Move!
                _previewGizmo.Mode = jIdx == 0
                    ? (Gizmo3D.ToolMode.Move | Gizmo3D.ToolMode.Rotate)
                    : Gizmo3D.ToolMode.Move;

                if (_gizmoDummyTarget == null || !GodotObject.IsInstanceValid(_gizmoDummyTarget))
                {
                    _gizmoDummyTarget = new Node3D { Name = "GizmoDummyTarget" };
                }
                if (_gizmoDummyTarget.GetParent() != _previewWorldRoot)
                {
                    _gizmoDummyTarget.GetParent()?.RemoveChild(_gizmoDummyTarget);
                    _previewWorldRoot.AddChild(_gizmoDummyTarget);
                }

                Transform3D animDelta = GetSyntheticAnchorAnimDelta(_selectedChain);
                Vector3 jointPosRest = (pts != null && jIdx < pts.Count) ? pts[jIdx] : _selectedChain.SyntheticOriginHeroSpace;
                Basis synBasisRest = jIdx == 0
                    ? BuildSyntheticGizmoBasisFromDir(_selectedChain.SyntheticDirectionHeroSpace)
                    : Basis.Identity;
                _gizmoDummyTarget.GlobalTransform = new Transform3D((animDelta.Basis * synBasisRest).Orthonormalized(), animDelta * jointPosRest);
                _previewGizmo.Visible = true;
                if (!_previewGizmo.IsSelected(_gizmoDummyTarget))
                {
                    _previewGizmo.ClearSelection();
                    _previewGizmo.Select(_gizmoDummyTarget);
                }
                _isGizmoUpdatingFromUI = false;
                return;
            }

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
            else if (target == 5) // Added Vanilla Donor Mesh
            {
                var st = GetSelectedDonorMeshState();
                if (st == null || _previewWorldRoot == null)
                {
                    _previewGizmo.Visible = false;
                    if (_gizmoDummyTarget != null && GodotObject.IsInstanceValid(_gizmoDummyTarget))
                        _previewGizmo.Deselect(_gizmoDummyTarget);
                    _isGizmoUpdatingFromUI = false;
                    return;
                }

                _previewGizmo.Mode = Gizmo3D.ToolMode.Rotate | Gizmo3D.ToolMode.Move;
                if (_gizmoDummyTarget == null || !GodotObject.IsInstanceValid(_gizmoDummyTarget))
                {
                    _gizmoDummyTarget = new Node3D { Name = "GizmoDummyTarget" };
                }

                int bIdx = st.IsRigid && !string.IsNullOrEmpty(st.ParentBone) ? _previewHeroSkeleton.FindBone(st.ParentBone) : -1;
                if (st.IsRigid && bIdx >= 0)
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
                    _gizmoDummyTarget.RemoveMeta("DonorFallbackOrigin");
                    _gizmoDummyTarget.Position = st.PositionOffset;
                    _gizmoDummyTarget.RotationDegrees = st.RotationDeg;
                }
                else
                {
                    if (_gizmoDummyTarget.GetParent() != _previewWorldRoot)
                    {
                        _gizmoDummyTarget.GetParent()?.RemoveChild(_gizmoDummyTarget);
                        _previewWorldRoot.AddChild(_gizmoDummyTarget);
                    }
                    _gizmoDummyTarget.SetMeta("DonorFallbackOrigin", st.LocalBoundsCenter);
                    _gizmoDummyTarget.Position = st.LocalBoundsCenter + st.PositionOffset;
                    _gizmoDummyTarget.RotationDegrees = st.RotationDeg;
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
            if (_btnSubTabColliders != null)
            {
                // Keep Body Colliders accessible in Accessory mode too so physics capes/skirts/hair can adapt hero colliders
                _btnSubTabColliders.Visible = true;
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
                _optGizmoTarget.AddItem("Vanilla Donor Mesh", 5);

                int targetToSelect = (!isFullCharacter && (prevId == 1 || prevId == 2)) ? 3 : prevId;
                SelectGizmoTargetById(targetToSelect);
            }
            UpdateAccessoryRigVisibility();
        }

        private void OnGizmoTransformEnd(int mode)
        {
            _isGizmoDragging = false;
            if (_isGizmoEditingSyntheticChain && _selectedChain != null && _selectedChain.IsSynthetic)
            {
                // Zero-lag workflow (matching Auto-Rig Reference Bones): do NOT freeze with a full re-rig on mouse release!
                MarkSyntheticChainDirty(_selectedChain);
                UpdateSyntheticApplyButtonVisual();
                RedrawSyntheticChainPreview();
                SetRigChipStatus("Cloth Bones Modified — Click Apply / Bind Chain", StatusTone.Warning);
                return;
            }
            if (_isGizmoEditingRefJoint && IsPinEditActive())
            {
                _pinsDirtyNeedsRigRebuild = true;
                SetRigChipStatus("Reference Rig Modified — Click Bind / Re-Bind", StatusTone.Warning);
                return;
            }
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
            else if (target == 5)
            {
                string meshName = GetSelectedDonorEditMeshName();
                var st = GetSelectedDonorMeshState();
                // For Rigid to Parent Bone donor meshes, fast 1-bone Skin bind-pose update already applied at 60 FPS — zero full re-rig needed!
                if (st != null && st.IsRigid && TryApplyFastDonorPreviewTransform(meshName, st, Transform3D.Identity))
                {
                    return;
                }
                ScheduleDebouncedLiveRigUpdate();
            }
        }

        private void OnGizmoTransformChanged(int mode, Vector3 value)
        {
            if (_isGizmoUpdatingFromUI || _previewGizmo == null || _gizmoDummyTarget == null) return;
            if (_isGizmoEditingSyntheticChain && _selectedChain != null && _selectedChain.IsSynthetic)
            {
                _isGizmoDragging = true;
                var pts = EnsureSyntheticJointPositions(_selectedChain);
                if (pts != null && pts.Count >= 2)
                {
                    int jIdx = Math.Clamp(_selectedSyntheticJointIndex, 0, pts.Count - 1);
                    Transform3D invAnim = GetSyntheticAnchorAnimDelta(_selectedChain).AffineInverse();
                    Vector3 newPos = invAnim * _gizmoDummyTarget.Position;
                    if (jIdx == 0)
                    {
                        Vector3 oldDir = _selectedChain.SyntheticDirectionHeroSpace.LengthSquared() > 1e-5f
                            ? _selectedChain.SyntheticDirectionHeroSpace.Normalized()
                            : Vector3.Down;
                        Vector3 newDir = (invAnim.Basis * (-_gizmoDummyTarget.Basis.Y)).Normalized();
                        Quaternion rotDelta = (oldDir.Dot(newDir) < 0.9999f && oldDir.Dot(newDir) > -0.9999f)
                            ? new Quaternion(oldDir, newDir)
                            : Quaternion.Identity;

                        Vector3 oldRoot = pts[0];
                        for (int i = 0; i < pts.Count; i++)
                        {
                            pts[i] = newPos + rotDelta * (pts[i] - oldRoot);
                        }
                        _selectedChain.SyntheticOriginHeroSpace = newPos;
                        if (newDir.LengthSquared() > 1e-5f)
                            _selectedChain.SyntheticDirectionHeroSpace = newDir;
                    }
                    else
                    {
                        // Move individual mid/tip joint freely in 3D space!
                        pts[jIdx] = newPos;
                        Vector3 rootToTip = pts[^1] - pts[0];
                        if (rootToTip.LengthSquared() > 1e-5f)
                        {
                            _selectedChain.SyntheticDirectionHeroSpace = rootToTip.Normalized();
                        }
                    }
                }
                SyncSyntheticTransformControlsFromSelected();
                RedrawSyntheticChainPreview();
                return;
            }
            if (_isGizmoEditingRefJoint && IsPinEditActive() && !string.IsNullOrEmpty(_selectedPin))
            {
                _isGizmoDragging = true;
                Vector3 localPos = BuildCurrentAlignedTransform().AffineInverse() * _gizmoDummyTarget.Position;
                _userMovedLandmarkPins = true;
                SetReferenceJointPosition(_selectedPin, localPos, applyMirror: _chkMirrorPins?.ButtonPressed ?? true);
                UpdateActivePinBadge();
                RefreshPinVisuals();
                return;
            }
            int target = _optGizmoTarget?.Selected >= 0 ? _optGizmoTarget.GetItemId(_optGizmoTarget.Selected) : 0;
            if (target == 5)
            {
                string meshName = GetSelectedDonorEditMeshName();
                var st = GetSelectedDonorMeshState();
                if (string.IsNullOrEmpty(meshName) || st == null) return;

                _isGizmoDragging = true;
                Vector3 pos = _gizmoDummyTarget.Position;
                if (_gizmoDummyTarget.HasMeta("DonorFallbackOrigin"))
                {
                    pos -= _gizmoDummyTarget.GetMeta("DonorFallbackOrigin").AsVector3();
                }
                Vector3 rot = _gizmoDummyTarget.RotationDegrees;
                st.PositionOffset = pos;
                st.RotationDeg = rot;

                SyncDonorEditUiFromSelected(repopulateBones: false);
                ApplyDonorMeshStateToPipeline(meshName, st, triggerRig: true, forceFullRebake: false);
                return;
            }
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
                bool isLeft = socketBone.Contains("_L", StringComparison.OrdinalIgnoreCase);
                string[] fallbacks = isLeft
                    ? new[] { "weaponHand_L", "hand_L" }
                    : new[] { "weaponHand_R", "weapon_bone", "hand_R" };
                foreach (string fb in fallbacks)
                {
                    foreach (var (meshName, socket) in _submeshSocketAssignments)
                    {
                        if (socket.Equals(fb, StringComparison.OrdinalIgnoreCase) && !list.Contains(meshName))
                        {
                            list.Add(meshName);
                        }
                    }
                    if (list.Count > 0) break;
                }
            }
            return list;
        }

        private string FindWeaponMeshForSocket(string socketBone)
        {
            var list = FindAllWeaponMeshesForSocket(socketBone);
            return list.Count > 0 ? list[0] : null;
        }

        private bool _hasShownInitialMissingBonesPopup;
        private Control _missingBonesOverlay;

        private bool EvaluateCriticalBoneHealth(
            out List<(string Canonical, string Label, string Impact)> missingCritical,
            out List<string> missingRecommended,
            out string rigHint)
        {
            var critList = new List<(string, string, string)>();
            missingCritical = critList;
            missingRecommended = new List<string>();
            rigHint = "";

            if (_workflowIntent != ImportWorkflowIntent.FullCharacter)
                return true;

            if (GetCurrentRiggingMethod() != CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap)
                return true;

            if (_cachedSourceSkeleton == null || _boneRowsByCanonical.Count == 0)
                return true;

            bool HasMapped(string canonical)
                => _userBoneRemap.TryGetValue(canonical, out string mapped) && !string.IsNullOrWhiteSpace(mapped);

            void CheckCritical(string canonical, string label, string impact)
            {
                if (_boneRowsByCanonical.ContainsKey(canonical) && !HasMapped(canonical))
                    critList.Add((canonical, label, impact));
            }

            CheckCritical("pelvis", "Pelvis / Hips", "Legs and torso will detach, collapse, or rotate incorrectly during animations.");

            bool hasAnySpineSlot = _boneRowsByCanonical.ContainsKey("spine_0") || _boneRowsByCanonical.ContainsKey("spine_1")
                                || _boneRowsByCanonical.ContainsKey("spine_2") || _boneRowsByCanonical.ContainsKey("spine_3");
            bool hasAnySpineMapped = HasMapped("spine_0") || HasMapped("spine_1") || HasMapped("spine_2") || HasMapped("spine_3");
            if (hasAnySpineSlot && !hasAnySpineMapped)
            {
                critList.Add(("spine_2", "Torso / Spine", "Upper body will stay rigid or detach from the hips during movement."));
            }

            CheckCritical("head", "Head", "Head will remain frozen or twist unnaturally with the torso.");
            CheckCritical("arm_upper_L", "Left Upper Arm", "Left arm will collapse into the torso or stretch across the body.");
            CheckCritical("arm_lower_L", "Left Forearm", "Left elbow cannot bend; arm will deform rigidly or pinch.");
            CheckCritical("arm_upper_R", "Right Upper Arm", "Right arm will collapse into the torso or stretch across the body.");
            CheckCritical("arm_lower_R", "Right Forearm", "Right elbow cannot bend; arm will deform rigidly or pinch.");
            CheckCritical("leg_upper_L", "Left Thigh", "Left leg cannot stride or crouch properly.");
            CheckCritical("leg_lower_L", "Left Shin / Knee", "Left knee cannot bend; walk/run cycles will break.");
            CheckCritical("leg_upper_R", "Right Thigh", "Right leg cannot stride or crouch properly.");
            CheckCritical("leg_lower_R", "Right Shin / Knee", "Right knee cannot bend; walk/run cycles will break.");

            foreach (var (canon, label) in new[]
            {
                ("neck_0", "Neck"),
                ("hand_L", "Left Hand"),
                ("hand_R", "Right Hand"),
                ("ankle_L", "Left Foot"),
                ("ankle_R", "Right Foot")
            })
            {
                if (_boneRowsByCanonical.ContainsKey(canon) && !HasMapped(canon))
                    missingRecommended.Add(label);
            }

            if (_lastRigAnalysis?.Family == RigFamily.RobloxR6)
            {
                rigHint = "Roblox R6 rig detected: R6 lacks elbows, knees, and a pelvis split. Switching to Auto-Rig (Reference Bones) is strongly recommended so Deadlock animations deform cleanly.";
            }
            else if (_lastRigAnalysis?.Family == RigFamily.RobloxR15)
            {
                rigHint = "Roblox R15 rig detected: R15 has no neck or clavicle bones (these auto-collapse cleanly), but LowerTorso (Pelvis), UpperTorso (Spine), Head, and all 4 limb chains should be mapped.";
            }

            return missingCritical.Count == 0;
        }

        private void MaybeShowInitialMissingBonesWarning()
        {
            if (_hasShownInitialMissingBonesPopup || !Visible) return;
            if (!EvaluateCriticalBoneHealth(out var missingCritical, out var missingRecommended, out var rigHint))
            {
                _hasShownInitialMissingBonesPopup = true;
                ShowMissingCriticalBonesPopup(missingCritical, missingRecommended, rigHint, isConfirmGuard: false);
            }
        }

        private void DismissMissingBonesPopup()
        {
            if (_missingBonesOverlay != null && GodotObject.IsInstanceValid(_missingBonesOverlay))
            {
                _missingBonesOverlay.QueueFree();
            }
            _missingBonesOverlay = null;
        }

        private void ShowMissingCriticalBonesPopup(
            List<(string Canonical, string Label, string Impact)> missingCritical,
            List<string> missingRecommended,
            string rigHint,
            bool isConfirmGuard)
        {
            DismissMissingBonesPopup();

            var backdrop = new ColorRect
            {
                Name = "MissingCriticalBonesOverlay",
                Color = new Color(0f, 0f, 0f, 0.62f),
                MouseFilter = Control.MouseFilterEnum.Stop
            };
            backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(backdrop);
            _missingBonesOverlay = backdrop;

            var center = new CenterContainer();
            center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            backdrop.AddChild(center);

            var panel = new PanelContainer
            {
                ThemeTypeVariation = "ModalPanel",
                CustomMinimumSize = new Vector2(560, 0)
            };
            center.AddChild(panel);

            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 22);
            margin.AddThemeConstantOverride("margin_right", 22);
            margin.AddThemeConstantOverride("margin_top", 18);
            margin.AddThemeConstantOverride("margin_bottom", 18);
            panel.AddChild(margin);

            var vbox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            vbox.AddThemeConstantOverride("separation", 12);
            margin.AddChild(vbox);

            var titleLbl = new Label
            {
                Text = $"⚠ Missing Critical Bones ({missingCritical.Count})",
                ThemeTypeVariation = "ModalTitleLabel"
            };
            titleLbl.AddThemeColorOverride("font_color", ThemeManager.GetColor(nameof(ThemePaletteData.SemanticWarning)));
            vbox.AddChild(titleLbl);

            var introLbl = new Label
            {
                Text = "This skeleton is missing core humanoid bones required for Deadlock animation playback. Applying without fixing these bones will cause severe mesh distortion or detached limbs:",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            ImporterStyle.ApplyBodyStyle(introLbl);
            vbox.AddChild(introLbl);

            if (!string.IsNullOrEmpty(rigHint))
            {
                var hintPanel = new PanelContainer { ThemeTypeVariation = "SubPanelContainer" };
                var hintMargin = new MarginContainer();
                hintMargin.AddThemeConstantOverride("margin_left", 10);
                hintMargin.AddThemeConstantOverride("margin_right", 10);
                hintMargin.AddThemeConstantOverride("margin_top", 8);
                hintMargin.AddThemeConstantOverride("margin_bottom", 8);
                hintPanel.AddChild(hintMargin);
                var hintLbl = new Label
                {
                    Text = rigHint,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };
                ImporterStyle.ApplyHintStyle(hintLbl);
                hintMargin.AddChild(hintLbl);
                vbox.AddChild(hintPanel);
            }

            var listScroll = new ScrollContainer
            {
                CustomMinimumSize = new Vector2(0, Math.Min(220, Math.Max(90, missingCritical.Count * 38))),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };
            var listBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            listBox.AddThemeConstantOverride("separation", 6);
            listScroll.AddChild(listBox);

            foreach (var (_, label, impact) in missingCritical)
            {
                var itemLbl = new Label
                {
                    Text = $"• {label} — {impact}",
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };
                ImporterStyle.ApplyFontSize(itemLbl, ImporterStyle.FontHint);
                itemLbl.AddThemeColorOverride("font_color", ThemeManager.GetColor(nameof(ThemePaletteData.SemanticWarning)));
                listBox.AddChild(itemLbl);
            }
            vbox.AddChild(listScroll);

            if (missingRecommended.Count > 0)
            {
                var recLbl = new Label
                {
                    Text = $"Also unmapped (optional / auto-collapsed): {string.Join(", ", missingRecommended)}",
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };
                ImporterStyle.ApplyHintStyle(recLbl);
                vbox.AddChild(recLbl);
            }

            vbox.AddChild(new HSeparator());

            var btnRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            btnRow.AddThemeConstantOverride("separation", 8);

            var btnFixBones = new Button
            {
                Text = "Fix in Bones Tab",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(140, 36),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            btnFixBones.Pressed += () =>
            {
                DismissMissingBonesPopup();
                if (_rightTabs != null && _rightTabs.GetTabCount() > 2)
                    _rightTabs.CurrentTab = 2;
                SetBonesSubView(0);
                if (_btnFilterUnmappedOnly != null)
                    _btnFilterUnmappedOnly.ButtonPressed = true;
                ApplyBoneMappingFilter();
            };
            btnRow.AddChild(btnFixBones);

            var btnAutoRig = new Button
            {
                Text = "Switch to Auto-Rig",
                ThemeTypeVariation = "ImporterPrimaryButton",
                CustomMinimumSize = new Vector2(160, 36),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Rebuild a complete Deadlock-compatible skeleton using 16 draggable Reference Bones."
            };
            btnAutoRig.Pressed += () =>
            {
                DismissMissingBonesPopup();
                SelectRiggingMethodById((int)CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig);
                if (_rightTabs != null && _rightTabs.GetTabCount() > 1)
                    _rightTabs.CurrentTab = 1;
                SetBottomStatus("Switched to Auto-Rig (Reference Bones) — position joints and click Bind.", StatusTone.Info);
            };
            btnRow.AddChild(btnAutoRig);

            var btnContinue = new Button
            {
                Text = isConfirmGuard ? "Apply Anyway" : "Continue Anyway",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(130, 36)
            };
            btnContinue.Pressed += () =>
            {
                DismissMissingBonesPopup();
                if (isConfirmGuard)
                {
                    ExecuteConfirmApplyToHero();
                }
            };
            btnRow.AddChild(btnContinue);

            vbox.AddChild(btnRow);
        }

        private void OnConfirmApplyToHero()
        {
            if (!EvaluateCriticalBoneHealth(out var missingCritical, out var missingRecommended, out var rigHint))
            {
                ShowMissingCriticalBonesPopup(missingCritical, missingRecommended, rigHint, isConfirmGuard: true);
                return;
            }
            ExecuteConfirmApplyToHero();
        }

        private void ExecuteConfirmApplyToHero()
        {
            _liveRigDebounceTimer?.Stop();
            if (_liveHeroNode == null || _liveImportedScene == null)
            {
                OnCancel();
                return;
            }

            SetBottomStatus("Binding custom model to live hero...", StatusTone.Info);

            // Exit Mesh Cut Mode cleanly before gathering final options
            if (IsMeshCutActive())
            {
                ExitMeshCutMode();
            }

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
                Visible = false;
                Input.MouseMode = Input.MouseModeEnum.Visible;
                GetViewport()?.GuiReleaseFocus();
                try
                {
                    DeadlockPlayground.Core.SessionCrashGuard.ClearImporterDraft();
                    DeadlockPlayground.Core.SessionCrashGuard.RecordBreadcrumb(
                        $"Confirmed model import: {Path.GetFileName(_filePath)}",
                        activeHeroId: _heroCodename);
                    // Live hero visibility for all stacked slots & replaced surfaces has already been updated by RecomputeHeroReplacementVisibility
                    EmitSignal(SignalName.RiggingConfirmed);
                }
                finally
                {
                    CleanupPreviewResources();
                    QueueFree();
                }
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
            Visible = false;
            Input.MouseMode = Input.MouseModeEnum.Visible;
            GetViewport()?.GuiReleaseFocus();
            // If live imported scene is not parented to anything, clean it up
            if (_liveImportedScene != null && GodotObject.IsInstanceValid(_liveImportedScene) && _liveImportedScene.GetParent() == null)
            {
                _liveImportedScene.QueueFree();
            }

            CleanupPreviewResources();
            DeadlockPlayground.Core.SessionCrashGuard.ClearImporterDraft();
            DeadlockPlayground.Core.SessionCrashGuard.RecordBreadcrumb("Cancelled model importer", activeHeroId: _heroCodename);
            EmitSignal(SignalName.ModalCancelled);
            QueueFree();
        }
        #endregion
    }
}
