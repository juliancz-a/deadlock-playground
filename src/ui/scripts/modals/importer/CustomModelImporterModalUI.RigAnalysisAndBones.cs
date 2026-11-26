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
        private void PopulateRiggingMethodOptions(bool hasRig)
        {
            if (_optRiggingMethod == null) return;
            _optRiggingMethod.Clear();
            if (hasRig)
            {
                _optRiggingMethod.AddItem("Skeleton Bone Remap (Recommended — Preserves Rig)", (int)CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap);
                _optRiggingMethod.AddItem("Clothing / Partial-Body Auto-Rig (Sweaters / Pants / Armor)", (int)CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig);
                _optRiggingMethod.AddItem("Landmark Auto-Rig (Rebuild Rig from 16 Pins)", (int)CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig);
                _optRiggingMethod.AddItem("Rigid Bone Attachment (Props / Hats / Weapons)", (int)CustomModelAutoRigger.RiggingMethod.RigidSingleBone);
                _optRiggingMethod.AddItem("Legacy: Surface Weight Transfer (KNN Fallback)", (int)CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection);
            }
            else
            {
                _optRiggingMethod.AddItem("Clothing / Partial-Body Auto-Rig (Sweaters / Pants / Armor)", (int)CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig);
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

            bool isPartial = method == CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig;
            bool isPartialPins = isPartial && _partialBodyEngine == CustomModelAutoRigger.PartialBodyEngine.SemiBodyPins;
            bool isPartialTransfer = isPartial && _partialBodyEngine == CustomModelAutoRigger.PartialBodyEngine.HeroBodyTransfer;
            bool isLandmark = method == CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig || isPartialPins;
            bool isPreRigged = method == CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap;
            bool isKnn = method == CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection || isPartialTransfer;
            bool isRigid = method == CustomModelAutoRigger.RiggingMethod.RigidSingleBone;
            bool paintStep = _btnWeightPaintMode?.ButtonPressed ?? false;

            if (_boxPartialBodyPanel != null) _boxPartialBodyPanel.Visible = isPartial;
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

            bool isPartialPins = method == CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig &&
                                 _partialBodyEngine == CustomModelAutoRigger.PartialBodyEngine.SemiBodyPins;
            bool isPartialTransfer = method == CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig &&
                                     _partialBodyEngine == CustomModelAutoRigger.PartialBodyEngine.HeroBodyTransfer;
            bool isKnn = method == CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection || isPartialTransfer;
            bool isPreRigged = method == CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap ||
                               method == CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig ||
                               isPartialPins;

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
                    CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig => "Static Mesh — Clothing / Partial-Body Auto-Rig Active",
                    CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig => "Static Mesh — Landmark Auto-Rig Active (16 Pins)",
                    CustomModelAutoRigger.RiggingMethod.RigidSingleBone => "Static Mesh — Rigid Bone Attachment",
                    _ => "Static Mesh — Legacy KNN Transfer Fallback"
                }, method == CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection ? StatusTone.Warning : StatusTone.Info);
            }

            if (_lblRiggingMethodDesc != null)
            {
                _lblRiggingMethodDesc.Text = method switch
                {
                    CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig =>
                        "Scopes auto-rigging strictly to your garment's body region (sweaters, jackets, pants, gloves, boots) using either Semi-Body 3D Pins or Quick Hero Transfer.",
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
            if (method == CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig)
            {
                _isGuidedPinPlacement = false;
                _guidedPlacedPinKeys.Clear();
                if (_partialBodyEngine == CustomModelAutoRigger.PartialBodyEngine.SemiBodyPins)
                {
                    EnsurePartialBodyPinsInitialized(force: !_userMovedLandmarkPins);
                }
                // Keep Mode & Region as the default sub-tab and do NOT auto-enter 3D Reference Bones edit mode
                // until the user explicitly clicks "Edit Pins in 3D" or the "Region Pins" sub-tab!
                _isEditReferenceBonesMode = false;
                _tab2SubView = 0;
                SyncPartialBodyUiState();
            }
            else if (method == CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig)
            {
                if (!_userMovedLandmarkPins && (_referenceRig == null || _referenceRig.Joints.Count == 0))
                {
                    StartGuidedOneByOnePinPlacement(clearExisting: true);
                }
                else
                {
                    EnsureLandmarkPins(false);
                }
            }
            if (_previewWorldRoot != null && GodotObject.IsInstanceValid(_previewWorldRoot))
            {
                ApplyLandmarkPreviewState();
            }
        }

        private void PopulateSubmeshChecklists()
        {
            // Clear existing
            foreach (var c in _heroKeepCheckboxes) c.QueueFree();
            _heroKeepCheckboxes.Clear();
            _heroKeepCards.Clear();
            foreach (var c in _importedSubmeshCheckboxes) c.QueueFree();
            _importedSubmeshCheckboxes.Clear();
            _importedSubmeshCards.Clear();
            _weaponCardSummaryLabels.Clear();
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

            // Original Deadlock Hero submeshes (or active complete replacement model submeshes in Accessory mode!)
            var heroMeshes = CustomModelAutoRigger.CollectOriginalHeroMeshes(_liveHeroNode);
            if (_lblHeroKeepSectionTitle != null)
            {
                _lblHeroKeepSectionTitle.Text = _hasActiveCompleteReplacement
                    ? "Active Replacement Model Parts"
                    : "Keep from Original Hero";
            }

            if (_hasActiveCompleteReplacement)
            {
                var mainReplacement = _liveHeroNode?.FindChild("Custom_Rigged_Character", true, false) as Node3D;
                if (mainReplacement != null && GodotObject.IsInstanceValid(mainReplacement))
                {
                    foreach (var cMi in CustomModelAutoRigger.CollectMeshes(mainReplacement))
                    {
                        if (cMi?.Mesh == null) continue;
                        string cName = cMi.Name.ToString();
                        bool isVis = cMi.Visible && !(cMi.HasMeta("UserHiddenByCustomModel") && cMi.GetMeta("UserHiddenByCustomModel").AsBool());
                        var chk = new CheckBox
                        {
                            Text = cName,
                            ButtonPressed = isVis,
                            Visible = false
                        };
                        chk.SetMeta("SubmeshName", cName);
                        var hCard = new MeshThumbCard();
                        hCard.Build(cName, "Replacement model", "Model Part", new Color(0.32f, 0.82f, 0.52f), isVis, isHeroKeepMode: true);
                        chk.Toggled += (on) =>
                        {
                            hCard.SetActive(on);
                            if (chk.HasMeta("SubmeshName"))
                            {
                                string k = chk.GetMeta("SubmeshName").AsString();
                                if (on) _userRestoredHeroTargets.Add(k);
                                else _userRestoredHeroTargets.Remove(k);
                            }
                            UpdatePreviewHeroKeepAndGhostMaterials();
                        };
                        _heroKeepCheckboxes.Add(chk);
                        _heroKeepCards[chk] = hCard;
                        _boxHeroSubmeshes.AddChild(chk);

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
                        string capturedReplName = cName;
                        hCard.CutPressed += () => EnterMeshCutModeForOriginalHero(capturedReplName, capturedReplName);
                        heroGrid.AddChild(hCard);
                        _meshThumbService?.RequestThumbnail($"repl_{cName}", cMi.Mesh, -1, new Color(0.75f, 0.86f, 0.78f), tex => hCard.SetThumbnail(tex));
                    }
                }
            }
            else
            {
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
                        var hCard = new MeshThumbCard();
                        hCard.Build(cleanLabel, mi.Name.ToString(), "Hero Part", new Color(0.32f, 0.82f, 0.52f), false, isHeroKeepMode: true);
                        chk.Toggled += (on) =>
                        {
                            hCard.SetActive(on);
                            if (chk.HasMeta("SubmeshName"))
                            {
                                string k = chk.GetMeta("SubmeshName").AsString();
                                if (on) _userRestoredHeroTargets.Add(k);
                                else _userRestoredHeroTargets.Remove(k);
                            }
                            UpdatePreviewHeroKeepAndGhostMaterials();
                        };
                        _heroKeepCheckboxes.Add(chk);
                        _heroKeepCards[chk] = hCard;
                        _boxHeroSubmeshes.AddChild(chk);

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
                        string capturedHeroKeepKey = keepKey;
                        string capturedHeroLabel = cleanLabel;
                        hCard.CutPressed += () => EnterMeshCutModeForOriginalHero(capturedHeroKeepKey, capturedHeroLabel);
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
                    var hCard = new MeshThumbCard();
                    hCard.Build(hName, "Original mesh", "Hero Part", new Color(0.32f, 0.82f, 0.52f), false, isHeroKeepMode: true);
                    chk.Toggled += (on) =>
                    {
                        hCard.SetActive(on);
                        if (chk.HasMeta("SubmeshName"))
                        {
                            string k = chk.GetMeta("SubmeshName").AsString();
                            if (on) _userRestoredHeroTargets.Add(k);
                            else _userRestoredHeroTargets.Remove(k);
                        }
                        UpdatePreviewHeroKeepAndGhostMaterials();
                    };
                    _heroKeepCheckboxes.Add(chk);
                    _heroKeepCards[chk] = hCard;
                    _boxHeroSubmeshes.AddChild(chk);

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
                    string capturedSingleHeroName = hName;
                    hCard.CutPressed += () => EnterMeshCutModeForOriginalHero(capturedSingleHeroName, capturedSingleHeroName);
                    heroGrid.AddChild(hCard);
                    _meshThumbService?.RequestThumbnail($"hero_{hName}", mi.Mesh, -1, new Color(0.68f, 0.85f, 0.78f), tex => hCard.SetThumbnail(tex));
                }
            }
            }

            var importedGrid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            importedGrid.AddThemeConstantOverride("h_separation", 8);
            importedGrid.AddThemeConstantOverride("v_separation", 8);
            _importedSubmeshGrid = importedGrid;
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
                var (vCount, tCount) = AddImportedSubmeshCard(mi, importedGrid, socketBones, heroMeshes, ref weaponCount);
                totalVerts += vCount;
                totalTris += tCount;
            }

            _lblGeometryStats.Text = $"Geometry: {importedMeshes.Count} submesh(es) | {totalVerts:N0} vertices | {totalTris:N0} triangles";
            UpdateImportedPartsCountBadge();
            UpdateHeroKeepCountBadge();
        }

        private (int Verts, int Tris) AddImportedSubmeshCard(
            MeshInstance3D mi,
            GridContainer importedGrid,
            List<string> socketBones,
            List<MeshInstance3D> heroMeshes,
            ref int weaponCount)
        {
                if (mi?.Mesh == null) return (0, 0);
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

                string rawMeshName = mi.Name.ToString();
                string meshNameLower = rawMeshName.ToLowerInvariant();
                bool isVanillaDonor = mi.HasMeta("IsHeroSpaceDonorMesh") && mi.GetMeta("IsHeroSpaceDonorMesh").AsBool();

                // Check for weapon or accessory keywords
                bool isLikelyWeapon = meshNameLower.Contains("gun") || meshNameLower.Contains("pistol") ||
                                      meshNameLower.Contains("weapon") || meshNameLower.Contains("rifle") ||
                                      meshNameLower.Contains("sword") || meshNameLower.Contains("blade") ||
                                      meshNameLower.Contains("holster") || meshNameLower.Contains("shotgun") ||
                                      meshNameLower.Contains("cannon");

                // If this weapon mesh is already skinned onto a source skeleton that shares a matching weapon bone
                // with the target hero skeleton (e.g. pre-rigged Deadlock hero model like hornet_gun -> weapon_bone),
                // keep it as Skinned Body by default so we never hijack its native bone binding & rotation!
                bool hasNativeSkinnedWeaponBone = false;
                if (isLikelyWeapon && mi.Skin != null && mi.Skin.GetBindCount() > 0 &&
                    _cachedSourceSkeleton != null && _previewHeroSkeleton != null)
                {
                    int srcBoneCount = _cachedSourceSkeleton.GetBoneCount();
                    for (int bi = 0; bi < srcBoneCount; bi++)
                    {
                        string bn = _cachedSourceSkeleton.GetBoneName(bi);
                        string bnl = bn.ToLowerInvariant();
                        if ((bnl.Contains("weapon") || bnl.Contains("gun")) && _previewHeroSkeleton.FindBone(bn) >= 0)
                        {
                            hasNativeSkinnedWeaponBone = true;
                            break;
                        }
                    }
                }
                if (hasNativeSkinnedWeaponBone)
                {
                    isLikelyWeapon = false;
                }

                bool isLeftWeapon = false;
                if (isLikelyWeapon)
                {
                    isLeftWeapon = meshNameLower.Contains("_l") || meshNameLower.Contains(".l") ||
                                   meshNameLower.Contains("left") || meshNameLower.Contains("dual_l") ||
                                   meshNameLower.Contains("offhand");
                    weaponCount++;
                }

                // Build interactive visual submesh gallery card
                bool isOutlineShell = !isVanillaDonor && CustomModelAutoRigger.IsLikelyOutlineShell(mi);
                _submeshIsOutlineShell[rawMeshName] = isOutlineShell;

                bool initialIncluded = isOutlineShell ? (_chkIncludeBakedOutlines?.ButtonPressed ?? true) : true;
                string initialRoleText = isVanillaDonor ? "Vanilla" : (isOutlineShell ? "Outline" : (isLikelyWeapon ? "Prop" : "Body"));
                Color initialRoleCol = isVanillaDonor
                    ? new Color(0.45f, 0.78f, 0.96f)
                    : (isOutlineShell
                        ? new Color(0.85f, 0.65f, 0.25f)
                        : (isLikelyWeapon ? new Color(0.45f, 0.72f, 0.96f) : new Color(0.45f, 0.85f, 0.62f)));

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
                    UpdateImportedPartsCountBadge();
                };
                card.Toggled += (on) =>
                {
                    if (chk.ButtonPressed != on) chk.ButtonPressed = on;
                };
                card.IsolatePressed += () => IsolateSubmeshIn3D(rawMeshName);
                card.CutPressed += () => EnterMeshCutModeForImportedMesh(rawMeshName, rawMeshName);
                card.SculptPressed += () => EnterImportedSculptModeForMesh(rawMeshName);
                _importedSubmeshCheckboxes.Add(chk);
                _importedSubmeshCards[chk] = card;
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

                // Compact Weapon Fit summary + Floating HUD launcher (replaces bulky inline weapon form!)
                var weaponCompactBox = new VBoxContainer
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    Visible = isLikelyWeapon
                };
                weaponCompactBox.AddThemeConstantOverride("separation", 4);
                cardVBox.AddChild(weaponCompactBox);

                var lblWeaponSummary = new Label
                {
                    Text = $"Socket: {(isLeftWeapon ? "weapon_hand_L" : "weapon_hand_R")}",
                    ClipText = true,
                    TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };
                ImporterStyle.ApplyHintStyle(lblWeaponSummary);
                _weaponCardSummaryLabels[rawMeshName] = lblWeaponSummary;
                weaponCompactBox.AddChild(lblWeaponSummary);

                var btnFitWeaponHud = new Button
                {
                    Text = "Fit Weapon / Socket in 3D",
                    ThemeTypeVariation = "ImporterPrimaryButton",
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    CustomMinimumSize = new Vector2(0, 30),
                    TooltipText = "Opens the floating 3D Weapon Socket overlay (Bone, Replaces, Snap Grip, Straighten, Pos/Rot/Scale + 3D Gizmo)"
                };
                SetButtonIcon(btnFitWeaponHud, "res://assets/at-icons/wrench.svg", 14);
                ImporterStyle.ApplyFontSize(btnFitWeaponHud, ImporterStyle.FontHint);
                btnFitWeaponHud.Pressed += () => OpenWeaponSocketHud(rawMeshName);
                weaponCompactBox.AddChild(btnFitWeaponHud);

                // Keep backing socket controls in a hidden container so all existing Gizmo, Preset, and Restore bindings stay 100% intact!
                var hiddenSocketBacking = new VBoxContainer { Visible = false };
                cardVBox.AddChild(hiddenSocketBacking);

                // Row 3: Socket & Replacement Config (backing controls for Floating Weapon HUD)
                var socketRow = new HBoxContainer
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    Visible = isLikelyWeapon
                };
                socketRow.AddThemeConstantOverride("separation", 6);
                hiddenSocketBacking.AddChild(socketRow);

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

                // Row 4: Placement Row (backing controls for Floating Weapon HUD)
                var placementRow = new HBoxContainer
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    Visible = isLikelyWeapon
                };
                placementRow.AddThemeConstantOverride("separation", 6);
                hiddenSocketBacking.AddChild(placementRow);

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

                // Row 5: Weapon Socket Transform & Alignment Box (backing controls for Floating Weapon HUD)
                var weaponTransformBox = new VBoxContainer
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    Visible = isLikelyWeapon
                };
                weaponTransformBox.AddThemeConstantOverride("separation", 4);
                hiddenSocketBacking.AddChild(weaponTransformBox);

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
                    weaponCompactBox.Visible = isSocket;
                    socketRow.Visible = isSocket;
                    placementRow.Visible = isSocket;
                    weaponTransformBox.Visible = isSocket;
                    UpdateWeaponCardSummary(rawMeshName);
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

                        if (_chkLinkWeaponSubmeshes?.ButtonPressed ?? true)
                        {
                            foreach (var (otherName, ctrl) in _submeshCardControls)
                            {
                                if (otherName == rawMeshName || ctrl.Role.Selected != 1) continue;
                                ctrl.Socket.Select((int)idx);
                                _submeshSocketAssignments[otherName] = bName;
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
                return (vCount, tCount);
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

                // Track already-claimed source bones so RigBoneClassifier fallback never creates duplicate conflicts
                var usedAutoMappedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in autoMapByTarget.Values)
                {
                    if (!string.IsNullOrEmpty(v)) usedAutoMappedSources.Add(v);
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
                        string clsCandidate = srcRawNames[clsSrcIdx];
                        if (!usedAutoMappedSources.Contains(clsCandidate))
                        {
                            matchedBoneName = clsCandidate;
                            usedAutoMappedSources.Add(clsCandidate);
                        }
                    }
                    else if (!string.IsNullOrEmpty(matchedBoneName))
                    {
                        usedAutoMappedSources.Add(matchedBoneName);
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
                        TooltipText = "Clear / unmap this bone slot (sets it back to [ None / Auto-Collapse ])"
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
                        _userBoneRemap.Remove(entry.CanonicalName);
                        _isUpdatingBoneDropdowns = true;
                        try
                        {
                            entry.BoneDropdown.Select(0);
                            entry.BoneDropdown.TooltipText = "Unmapped (auto-collapses to parent bone)";
                        }
                        finally
                        {
                            _isUpdatingBoneDropdowns = false;
                        }
                        UpdateBoneMappingValidationBadges();
                        RefreshBoneTabDiffSummary();
                        ApplyBoneMappingFilter();
                        MarkRemapStaleAndRefresh();
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
                        UpdateBoneMappingValidationBadges();
                        RefreshBoneTabDiffSummary();
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

                if (!_hasShownInitialMissingBonesPopup)
                {
                    CallDeferred(nameof(MaybeShowInitialMissingBonesWarning));
                }
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

    }
}
