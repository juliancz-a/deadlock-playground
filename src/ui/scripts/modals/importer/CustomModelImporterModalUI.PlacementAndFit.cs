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
            bool hasExistingPins = _referenceRig != null && _referenceRig.Joints.Count >= CustomLandmarkWeightSolver.PinNames.Length;
            if (hasAnyActive && _liveImportedScene != null && GodotObject.IsInstanceValid(_liveImportedScene))
            {
                // NEVER wipe existing or user-edited reference bones when toggling accessory/imported submeshes!
                if (!hasExistingPins && !_userMovedLandmarkPins)
                {
                    EnsureLandmarkPins(force: false);
                }
                UpdateActivePinBadge();
                RefreshPinVisuals();
            }

            if (hasAnyActive && _workflowIntent == ImportWorkflowIntent.FullCharacter && !_userModifiedTransform)
            {
                AutoFitToHero();
                return;
            }

            // Only auto-calibrate pose sliders for legacy KNN unrigged flow when no pins have been set yet
            var method = GetCurrentRiggingMethod();
            if (hasAnyActive && GetValidSourceSkeleton() == null &&
                !hasExistingPins && !_userMovedLandmarkPins &&
                method == CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection)
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
                        if (GodotObject.IsInstanceValid(chk))
                        {
                            chk.SetPressedNoSignal(true);
                            if (triggerUpdate && chk.HasMeta("SubmeshName"))
                                _userRestoredHeroTargets.Add(chk.GetMeta("SubmeshName").AsString());
                        }
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

        private void SnapAccessoryToSelectedBone() => SnapAccessoryToSelectedBone(preserveCurrentScale: false);

        private void SnapAccessoryToSelectedBone(bool preserveCurrentScale)
        {
            if (_liveImportedScene == null || _previewHeroSkeleton == null || _previewModelRoot == null) return;
            _previewHeroSkeleton.ResetBonePoses();
            string boneName = GetSelectedAccBoneName();
            Transform3D fit = CustomModelAutoRigger.ComputeAutoFitTransformToBone(
                _previewModelInstance ?? _liveImportedScene, _previewHeroSkeleton, boneName, GetActiveImportedSubmeshFilter());

            float fitScale = MathF.Max(0.0005f, fit.Basis.Scale.X);
            float scale = preserveCurrentScale ? MathF.Max(0.0005f, (float)_spinScale.Value) : fitScale;
            Vector3 eulerRad = fit.Basis.Orthonormalized().GetEuler();

            int bIdx = _previewHeroSkeleton.FindBone(boneName);
            if (bIdx < 0) bIdx = _previewHeroSkeleton.FindBone("head");
            Vector3 boneOrigin = bIdx >= 0
                ? _previewHeroSkeleton.Transform * _previewHeroSkeleton.GetBoneGlobalRest(bIdx).Origin
                : Vector3.Zero;

            Vector3 targetOrigin = fit.Origin;
            if (preserveCurrentScale && bIdx >= 0 && fitScale > 1e-5f)
            {
                // Preserve user's chosen Uniform Scale while still centering the accessory cleanly on the selected bone
                targetOrigin = boneOrigin + (fit.Origin - boneOrigin) * (scale / fitScale);
            }

            if (!preserveCurrentScale)
            {
                _spinScale.SetValueNoSignal(Math.Round(scale, 4));
                _sliderScale.SetValueNoSignal(Math.Round(scale, 4));
            }
            _spinPosX.SetValueNoSignal(Math.Round(targetOrigin.X, 3));
            _spinPosY.SetValueNoSignal(Math.Round(targetOrigin.Y, 3));
            _spinPosZ.SetValueNoSignal(Math.Round(targetOrigin.Z, 3));
            _spinRotPitch.SetValueNoSignal(Math.Round(Mathf.RadToDeg(eulerRad.X), 1));
            _spinRotYaw.SetValueNoSignal(Math.Round(Mathf.RadToDeg(eulerRad.Y), 1));
            _spinRotRoll.SetValueNoSignal(Math.Round(Mathf.RadToDeg(eulerRad.Z), 1));

            ApplyTransformToPreviewModel(immediate: true);

            if (bIdx >= 0)
            {
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

            SetBottomStatus(preserveCurrentScale
                ? $"Snapped accessory to bone '{boneName}' (kept Scale={scale:F3}x)."
                : $"Auto-fitted accessory to bone '{boneName}' (Scale={scale:F3}x).", StatusTone.Success);
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
            // Center-pivot compensation is strictly for floating Accessories (hats/props).
            // For Full Character imports, keep the Y-axis offset locked to the floor so rescaling never floats or sinks the character!
            if (_workflowIntent == ImportWorkflowIntent.AccessoryAddOrReplace &&
                !_suppressScalePivotCompensation && _previewModelRoot != null && _accLocalBoundsCenter != Vector3.Zero)
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
            float armDeg = _sliderArmAngle != null ? (float)_sliderArmAngle.Value : 0f;
            float fwdDeg = _sliderArmForward != null ? (float)_sliderArmForward.Value : 0f;
            float elbowPct = _sliderElbowStraight != null ? (float)_sliderElbowStraight.Value : 0f;
            float legDeg = _sliderLegSpread != null ? (float)_sliderLegSpread.Value : 0f;
            if (IsPartialHeroTransferActive() && preset == CustomModelAutoRigger.PosePreset.MatchHeroBindPose &&
                (MathF.Abs(armDeg) > 0.1f || MathF.Abs(fwdDeg) > 0.1f || MathF.Abs(elbowPct) > 0.1f || MathF.Abs(legDeg) > 0.1f))
            {
                preset = CustomModelAutoRigger.PosePreset.APoseCustomAngle;
            }
            return new CustomModelAutoRigger.RiggingOptions
            {
                Preset = preset,
                CustomArmAngleDeg = armDeg,
                CustomArmForwardDeg = fwdDeg,
                CustomElbowStraightenPct = elbowPct,
                CustomLegSpreadDeg = legDeg
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
    }
}
