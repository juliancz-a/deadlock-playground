using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using DeadlockPlayground.Animation;

namespace DeadlockPlayground.Importer
{
    public static partial class CustomModelAutoRigger
    {
        /// <summary>
        /// Non-destructively mirrors all mesh surfaces in <paramref name="scene"/> across the active model bounds center
        /// along the requested axes, transforming normals and tangents (including binormal handedness W) and reversing
        /// triangle winding whenever an odd number of axes is flipped so front-face culling never turns inside out.
        /// Returns an Action that restores all original Mesh/Skin/metadata references.
        /// </summary>
        public static Action ApplyGeometryFlipToScene(
            Node3D scene,
            bool flipX,
            bool flipY,
            bool flipZ,
            HashSet<string> boundsFilterNames = null,
            bool flattenSkinnedToRest = true)
        {
            int mask = (flipX ? 1 : 0) | (flipY ? 2 : 0) | (flipZ ? 4 : 0);
            if (scene == null || !GodotObject.IsInstanceValid(scene) || mask == 0)
                return null;

            var (minV, maxV, boundsOk) = ComputePristineModelBounds(scene, boundsFilterNames);
            if (!boundsOk && boundsFilterNames != null)
                (minV, maxV, boundsOk) = ComputePristineModelBounds(scene, null);
            Vector3 sceneCenter = boundsOk ? (minV + maxV) * 0.5f : Vector3.Zero;

            Vector3 flipSigns = new Vector3(flipX ? -1f : 1f, flipY ? -1f : 1f, flipZ ? -1f : 1f);
            bool reverseWinding = flipX ^ flipY ^ flipZ;

            Transform3D flipInScene = new Transform3D(Basis.FromScale(flipSigns), Vector3.Zero);
            flipInScene = new Transform3D(Basis.Identity, sceneCenter) * flipInScene * new Transform3D(Basis.Identity, -sceneCenter);

            var backups = new List<(MeshInstance3D Mi, Mesh Mesh, Skin Skin, Material OverrideMat)>();
            Skeleton3D fallbackSkel = FindSkeleton(scene);
            var meshes = CollectMeshes(scene);

            foreach (var mi in meshes)
            {
                if (mi?.Mesh == null) continue;
                Mesh origMesh = mi.Mesh;
                Skin origSkin = mi.Skin;
                backups.Add((mi, origMesh, origSkin, mi.MaterialOverride));

                Transform3D relToScene = ComputeTransformRelativeToRoot(mi, scene);
                Transform3D sceneToLocal = MathF.Abs(relToScene.Basis.Determinant()) > 1e-8f
                    ? relToScene.AffineInverse()
                    : Transform3D.Identity;
                Transform3D localFlip = sceneToLocal * flipInScene * relToScene;
                Basis localNormFlip = sceneToLocal.Basis * Basis.FromScale(flipSigns) * relToScene.Basis;

                bool miHasSkel = origSkin != null && origSkin.GetBindCount() > 0 && fallbackSkel != null;
                bool useRestBake = miHasSkel && flattenSkinnedToRest;

                var newMesh = new ArrayMesh();
                for (int s = 0; s < origMesh.GetSurfaceCount(); s++)
                {
                    var arrays = origMesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.Vertex) continue;

                    var rawVerts = GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    if (rawVerts == null || rawVerts.Length == 0) continue;

                    int vCount = rawVerts.Length;
                    var rawNorms = arrays.Count > (int)Mesh.ArrayType.Normal ? GetVector3Array(arrays[(int)Mesh.ArrayType.Normal]) : null;
                    var rawTangents = arrays.Count > (int)Mesh.ArrayType.Tangent && arrays[(int)Mesh.ArrayType.Tangent].VariantType == Variant.Type.PackedFloat32Array
                        ? arrays[(int)Mesh.ArrayType.Tangent].AsFloat32Array()
                        : null;
                    var rawUvs = arrays.Count > (int)Mesh.ArrayType.TexUV ? GetVector2Array(arrays[(int)Mesh.ArrayType.TexUV]) : null;
                    var rawUvs2 = arrays.Count > (int)Mesh.ArrayType.TexUV2 ? GetVector2Array(arrays[(int)Mesh.ArrayType.TexUV2]) : null;
                    var rawColors = arrays.Count > (int)Mesh.ArrayType.Color && arrays[(int)Mesh.ArrayType.Color].VariantType == Variant.Type.PackedColorArray
                        ? arrays[(int)Mesh.ArrayType.Color].AsColorArray()
                        : null;
                    var rawIndices = arrays.Count > (int)Mesh.ArrayType.Index ? GetInt32Array(arrays[(int)Mesh.ArrayType.Index]) : null;
                    var rawBones = arrays.Count > (int)Mesh.ArrayType.Bones ? GetInt32Array(arrays[(int)Mesh.ArrayType.Bones]) : null;
                    var rawWeights = arrays.Count > (int)Mesh.ArrayType.Weights ? GetFloat32Array(arrays[(int)Mesh.ArrayType.Weights]) : null;

                    var outVerts = new Vector3[vCount];
                    Vector3[] outNorms = (rawNorms != null && rawNorms.Length == vCount) ? new Vector3[vCount] : null;
                    float[] outTangents = (rawTangents != null && rawTangents.Length == vCount * 4) ? new float[vCount * 4] : null;

                    if (useRestBake && rawBones != null && rawWeights != null)
                    {
                        EvaluateMeshSurfaceInTargetSpace(mi, scene, flipInScene, fallbackSkel, rawVerts, rawNorms, rawBones, rawWeights, out var sceneVerts, out var sceneNorms);
                        for (int i = 0; i < vCount; i++)
                        {
                            outVerts[i] = sceneToLocal * sceneVerts[i];
                            if (outNorms != null)
                            {
                                Vector3 ln = sceneToLocal.Basis * sceneNorms[i];
                                outNorms[i] = ln.LengthSquared() > 1e-8f ? ln.Normalized() : Vector3.Up;
                            }
                            if (outTangents != null)
                            {
                                int ti = i * 4;
                                Vector3 lt = localNormFlip * new Vector3(rawTangents[ti], rawTangents[ti + 1], rawTangents[ti + 2]);
                                lt = lt.LengthSquared() > 1e-8f ? lt.Normalized() : Vector3.Right;
                                outTangents[ti] = lt.X;
                                outTangents[ti + 1] = lt.Y;
                                outTangents[ti + 2] = lt.Z;
                                outTangents[ti + 3] = reverseWinding ? -rawTangents[ti + 3] : rawTangents[ti + 3];
                            }
                        }
                    }
                    else
                    {
                        for (int i = 0; i < vCount; i++)
                        {
                            outVerts[i] = localFlip * rawVerts[i];
                            if (outNorms != null)
                            {
                                Vector3 ln = localNormFlip * rawNorms[i];
                                outNorms[i] = ln.LengthSquared() > 1e-8f ? ln.Normalized() : Vector3.Up;
                            }
                            if (outTangents != null)
                            {
                                int ti = i * 4;
                                Vector3 lt = localNormFlip * new Vector3(rawTangents[ti], rawTangents[ti + 1], rawTangents[ti + 2]);
                                lt = lt.LengthSquared() > 1e-8f ? lt.Normalized() : Vector3.Right;
                                outTangents[ti] = lt.X;
                                outTangents[ti + 1] = lt.Y;
                                outTangents[ti + 2] = lt.Z;
                                outTangents[ti + 3] = reverseWinding ? -rawTangents[ti + 3] : rawTangents[ti + 3];
                            }
                        }
                    }

                    int[] outIndices = rawIndices;
                    if (reverseWinding)
                    {
                        if (rawIndices != null && rawIndices.Length >= 3)
                        {
                            outIndices = (int[])rawIndices.Clone();
                            for (int t = 0; t + 2 < outIndices.Length; t += 3)
                            {
                                (outIndices[t + 1], outIndices[t + 2]) = (outIndices[t + 2], outIndices[t + 1]);
                            }
                        }
                        else if (vCount >= 3)
                        {
                            int triVerts = vCount - (vCount % 3);
                            outIndices = new int[triVerts];
                            for (int t = 0; t + 2 < triVerts; t += 3)
                            {
                                outIndices[t] = t;
                                outIndices[t + 1] = t + 2;
                                outIndices[t + 2] = t + 1;
                            }
                        }
                    }

                    var newArrays = new Godot.Collections.Array();
                    newArrays.Resize((int)Mesh.ArrayType.Max);
                    newArrays[(int)Mesh.ArrayType.Vertex] = outVerts;
                    if (outNorms != null) newArrays[(int)Mesh.ArrayType.Normal] = outNorms;
                    if (outTangents != null) newArrays[(int)Mesh.ArrayType.Tangent] = outTangents;
                    if (rawUvs != null && rawUvs.Length == vCount) newArrays[(int)Mesh.ArrayType.TexUV] = rawUvs;
                    if (rawUvs2 != null && rawUvs2.Length == vCount) newArrays[(int)Mesh.ArrayType.TexUV2] = rawUvs2;
                    if (rawColors != null && rawColors.Length == vCount) newArrays[(int)Mesh.ArrayType.Color] = rawColors;
                    if (outIndices != null && outIndices.Length > 0) newArrays[(int)Mesh.ArrayType.Index] = outIndices;
                    if (!useRestBake && rawBones != null && rawWeights != null)
                    {
                        newArrays[(int)Mesh.ArrayType.Bones] = rawBones;
                        newArrays[(int)Mesh.ArrayType.Weights] = rawWeights;
                    }

                    var prim = (origMesh as ArrayMesh)?.SurfaceGetPrimitiveType(s) ?? Mesh.PrimitiveType.Triangles;
                    newMesh.AddSurfaceFromArrays(prim, newArrays);
                    Material mat = origMesh.SurfaceGetMaterial(s);
                    if (mat != null) newMesh.SurfaceSetMaterial(newMesh.GetSurfaceCount() - 1, mat);
                }

                mi.Mesh = newMesh;
                if (useRestBake)
                {
                    mi.Skin = null;
                }
                else if (miHasSkel && MathF.Abs(localFlip.Basis.Determinant()) > 1e-8f)
                {
                    Transform3D localFlipInv = localFlip.AffineInverse();
                    Transform3D skelToScene = ComputeTransformRelativeToRoot(fallbackSkel, scene);
                    Transform3D flipInSkel = skelToScene.AffineInverse() * flipInScene * skelToScene;
                    var dupSkin = (Skin)origSkin.Duplicate();
                    int skelBoneCount = fallbackSkel.GetBoneCount();
                    for (int bi = 0; bi < origSkin.GetBindCount(); bi++)
                    {
                        int boneIdx = ResolveSkinBindBone(origSkin, bi, fallbackSkel);
                        Transform3D gRest = (boneIdx >= 0 && boneIdx < skelBoneCount) ? fallbackSkel.GetBoneGlobalRest(boneIdx) : Transform3D.Identity;
                        Transform3D gRestInv = MathF.Abs(gRest.Basis.Determinant()) > 1e-8f ? gRest.AffineInverse() : Transform3D.Identity;
                        dupSkin.SetBindPose(bi, gRestInv * flipInSkel * gRest * origSkin.GetBindPose(bi) * localFlipInv);
                    }
                    mi.Skin = dupSkin;
                }
            }

            scene.SetMeta("BakedFlipMask", mask);
            return () =>
            {
                if (GodotObject.IsInstanceValid(scene))
                {
                    scene.RemoveMeta("BakedFlipMask");
                }
                foreach (var (mi, origMesh, origSkin, origOverride) in backups)
                {
                    if (!GodotObject.IsInstanceValid(mi)) continue;
                    mi.Mesh = origMesh;
                    mi.Skin = origSkin;
                    mi.MaterialOverride = origOverride;
                }
            };
        }

        private static void ApplyReplacementMode(Node3D heroNode, List<MeshInstance3D> customMeshes, RiggingOptions options, Node3D slotContainer = null)
        {
            // In preview mode inside modal, NEVER hide ghost reference mannequin meshes!
            if (options.IsPreviewOnly) return;

            var existingMeshes = CollectOriginalHeroMeshes(heroNode);
            var slotHiddenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool isAddOnlyAccessory = options.Intent == AccessoryIntent.AddAccessory;

            if (!isAddOnlyAccessory)
            {
                // Explicit submesh replacement targets (including gun holster/hip companions)
                if (options.SubmeshReplacementMap != null)
                {
                    foreach (var repTarget in options.SubmeshReplacementMap.Values)
                    {
                        if (string.IsNullOrEmpty(repTarget) || repTarget == "[ None ]") continue;
                        slotHiddenTargets.Add(repTarget);
                        string repLower = repTarget.ToLowerInvariant();
                        if (repLower.Contains("gun"))
                        {
                            foreach (var origMi in existingMeshes)
                            {
                                string rawLower = origMi.Name.ToString().ToLowerInvariant();
                                if (rawLower.Contains("gun") &&
                                    (rawLower.Contains("hip") || rawLower.Contains("holster") || rawLower.Contains("back") || rawLower.Contains("sheath")))
                                {
                                    slotHiddenTargets.Add(origMi.Name.ToString());
                                }
                            }
                        }
                    }
                }

                // Explicit target replacements requested by Part-Replacement UI
                if (options.ExplicitReplacedTargets != null && options.ExplicitReplacedTargets.Count > 0)
                {
                    foreach (var exp in options.ExplicitReplacedTargets)
                    {
                        if (!string.IsNullOrWhiteSpace(exp))
                            slotHiddenTargets.Add(exp);
                    }
                }
                else
                {
                    // Legacy FullReplacement / KeptOriginalSubmeshNames filtering
                    foreach (var mi in existingMeshes)
                    {
                        if (mi.HasMeta("IsCustomImportedMesh")) continue;
                        string rawName = mi.Name.ToString();
                        int surfCount = mi.Mesh?.GetSurfaceCount() ?? 0;
                        if (surfCount > 1)
                        {
                            for (int s = 0; s < surfCount; s++)
                            {
                                string genName = null;
                                if (mi.HasMeta("GeneratedSubmeshes"))
                                {
                                    var genArr = mi.GetMeta("GeneratedSubmeshes").AsGodotArray();
                                    foreach (var gv in genArr)
                                    {
                                        if (gv.AsGodotObject() is MeshInstance3D subMi && GodotObject.IsInstanceValid(subMi))
                                        {
                                            int sIdx = subMi.HasMeta("SurfaceIndex") ? subMi.GetMeta("SurfaceIndex").AsInt32() : -1;
                                            if (sIdx == s) { genName = subMi.Name.ToString(); break; }
                                        }
                                    }
                                }
                                if (!IsOriginalSurfaceKept(options.KeptOriginalSubmeshNames, rawName, s, genName))
                                {
                                    slotHiddenTargets.Add(MakeSurfaceKeepKey(rawName, s));
                                }
                            }
                        }
                        else
                        {
                            if (!IsOriginalSurfaceKept(options.KeptOriginalSubmeshNames, rawName, 0))
                            {
                                slotHiddenTargets.Add(rawName);
                            }
                        }
                    }
                }
            }

            if (slotContainer != null && GodotObject.IsInstanceValid(slotContainer))
            {
                slotContainer.SetMeta("SlotHiddenTargets", slotHiddenTargets.ToArray());
                slotContainer.SetMeta("SlotIsAddAccessory", isAddOnlyAccessory);
                slotContainer.SetMeta("SlotDeformEnabled", options.OriginalMeshDeformEnabled);
                slotContainer.SetMeta("SlotDeformTarget", options.OriginalMeshDeformTargetFilter ?? "hair");
                slotContainer.SetMeta("SlotDeformBone", options.OriginalMeshDeformBoneName ?? options.RigidBoneName ?? "head");
                slotContainer.SetMeta("SlotDeformMode", (int)options.OriginalMeshDeformMode);
                slotContainer.SetMeta("SlotDeformStrength", options.OriginalMeshDeformStrength);
                slotContainer.SetMeta("SlotDeformRadius", options.OriginalMeshDeformRadius);
                slotContainer.SetMeta("SlotDeformHeightOffset", options.OriginalMeshDeformHeightOffset);
                if (options.OriginalMeshDeformSculpts != null && options.OriginalMeshDeformSculpts.Count > 0)
                {
                    slotContainer.SetMeta("SlotDeformSculpts", SerializeSculptsToGodotDict(options.OriginalMeshDeformSculpts));
                }
                else if (slotContainer.HasMeta("SlotDeformSculpts"))
                {
                    slotContainer.RemoveMeta("SlotDeformSculpts");
                }
                if (options.KeptOriginalPartTransforms != null && options.KeptOriginalPartTransforms.Count > 0)
                {
                    var serialized = SerializeKeptPartTransformsToGodotDict(options.KeptOriginalPartTransforms);
                    if (serialized.Count > 0)
                        slotContainer.SetMeta("SlotKeptPartTransforms", serialized);
                    else if (slotContainer.HasMeta("SlotKeptPartTransforms"))
                        slotContainer.RemoveMeta("SlotKeptPartTransforms");
                }
                else if (slotContainer.HasMeta("SlotKeptPartTransforms"))
                {
                    slotContainer.RemoveMeta("SlotKeptPartTransforms");
                }
                if (options.OriginalMeshCutMasks != null && options.OriginalMeshCutMasks.Count > 0)
                {
                    var serializedCuts = SerializeCutMasksToGodotDict(options.OriginalMeshCutMasks);
                    if (serializedCuts.Count > 0)
                        slotContainer.SetMeta("SlotOriginalCutMasks", serializedCuts);
                    else if (slotContainer.HasMeta("SlotOriginalCutMasks"))
                        slotContainer.RemoveMeta("SlotOriginalCutMasks");
                }
                else if (slotContainer.HasMeta("SlotOriginalCutMasks"))
                {
                    slotContainer.RemoveMeta("SlotOriginalCutMasks");
                }
            }

            if (options.ExplicitRestoredTargets != null && options.ExplicitRestoredTargets.Count > 0)
            {
                ClearTargetFromAccessoryHiddenLists(heroNode, options.ExplicitRestoredTargets.ToArray());
            }

            RecomputeHeroReplacementVisibility(heroNode, customMeshes);
        }

        /// <summary>
        /// Removes the specified mesh/surface keys from every <c>Custom_Accessory_*</c> container's
        /// <c>SlotHiddenTargets</c> metadata so a user can always recover hidden vanilla hero meshes
        /// even when multiple accessories are stacked.
        /// </summary>
        public static void ClearTargetFromAccessoryHiddenLists(Node3D heroNode, params string[] keys)
        {
            if (heroNode == null || !GodotObject.IsInstanceValid(heroNode) || keys == null || keys.Length == 0) return;
            var removeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string k in keys)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                removeSet.Add(k);
                int sep = k.IndexOf(SurfaceKeepSeparator, StringComparison.Ordinal);
                if (sep > 0) removeSet.Add(k[..sep]);
                else removeSet.Add(MakeSurfaceKeepKey(k, 0));
            }
            if (removeSet.Count == 0) return;

            foreach (var container in CollectCustomContainers(heroNode))
            {
                if (container == null || !GodotObject.IsInstanceValid(container)) continue;
                if (!container.Name.ToString().StartsWith("Custom_Accessory_", StringComparison.Ordinal)) continue;
                if (!container.HasMeta("SlotHiddenTargets")) continue;

                var arr = container.GetMeta("SlotHiddenTargets").AsStringArray();
                if (arr == null || arr.Length == 0) continue;
                var filtered = arr.Where(t => !string.IsNullOrEmpty(t) && !removeSet.Contains(t)).ToArray();
                if (filtered.Length != arr.Length)
                {
                    container.SetMeta("SlotHiddenTargets", filtered);
                }
            }
        }

        public static void RecomputeHeroReplacementVisibility(Node3D heroNode, List<MeshInstance3D> newlyBuiltMeshes = null)
        {
            if (heroNode == null || !GodotObject.IsInstanceValid(heroNode)) return;

            var combinedHiddenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var container in CollectCustomContainers(heroNode))
            {
                if (!container.Visible) continue;
                if (container.HasMeta("SlotIsAddAccessory") && container.GetMeta("SlotIsAddAccessory").AsBool())
                    continue;
                if (container.HasMeta("SlotHiddenTargets"))
                {
                    var arr = container.GetMeta("SlotHiddenTargets").AsStringArray();
                    if (arr != null)
                    {
                        foreach (var t in arr)
                        {
                            if (!string.IsNullOrEmpty(t)) combinedHiddenTargets.Add(t);
                        }
                    }
                }
            }

            var existingMeshes = CollectOriginalHeroMeshes(heroNode);
            foreach (var mi in existingMeshes)
            {
                if (mi.HasMeta("IsCustomImportedMesh")) continue;
                string rawName = mi.Name.ToString();
                bool wholeMeshHidden = combinedHiddenTargets.Contains(rawName);
                int surfCount = mi.Mesh?.GetSurfaceCount() ?? 0;

                if (mi.HasMeta("GeneratedSubmeshes"))
                {
                    mi.Visible = false;
                    bool anySubKept = false;
                    var genArr = mi.GetMeta("GeneratedSubmeshes").AsGodotArray();
                    for (int gIdx = 0; gIdx < genArr.Count; gIdx++)
                    {
                        if (genArr[gIdx].AsGodotObject() is MeshInstance3D subMi && GodotObject.IsInstanceValid(subMi))
                        {
                            int sIdx = subMi.HasMeta("SurfaceIndex") ? subMi.GetMeta("SurfaceIndex").AsInt32() : gIdx;
                            string surfKey = MakeSurfaceKeepKey(rawName, sIdx);
                            bool keepSub = !wholeMeshHidden &&
                                           !combinedHiddenTargets.Contains(surfKey) &&
                                           !combinedHiddenTargets.Contains(subMi.Name.ToString());
                            mi.SetMeta($"SurfaceKept_{sIdx}", keepSub);
                            subMi.SetMeta("SurfaceIndex", sIdx);
                            subMi.Visible = keepSub;
                            subMi.SetMeta("UserVisibility", keepSub);
                            if (keepSub)
                            {
                                subMi.RemoveMeta("UserHiddenByCustomModel");
                                anySubKept = true;
                            }
                            else
                            {
                                subMi.SetMeta("UserHiddenByCustomModel", true);
                            }
                        }
                    }
                    mi.SetMeta("UserVisibility", anySubKept);
                    if (anySubKept) mi.RemoveMeta("UserHiddenByCustomModel");
                    else mi.SetMeta("UserHiddenByCustomModel", true);
                }
                else if (surfCount > 1)
                {
                    bool anyKept = false;
                    bool allKept = true;
                    for (int s = 0; s < surfCount; s++)
                    {
                        string surfKey = MakeSurfaceKeepKey(rawName, s);
                        bool keepSurf = !wholeMeshHidden && !combinedHiddenTargets.Contains(surfKey);
                        if (keepSurf) anyKept = true;
                        else allKept = false;
                    }

                    mi.Visible = anyKept;
                    mi.SetMeta("UserVisibility", anyKept);
                    if (anyKept && !allKept)
                    {
                        var hiddenMat = CreateHiddenSurfaceMaterial();
                        for (int s = 0; s < surfCount; s++)
                        {
                            string surfKey = MakeSurfaceKeepKey(rawName, s);
                            bool keepSurf = !wholeMeshHidden && !combinedHiddenTargets.Contains(surfKey);
                            mi.SetMeta($"SurfaceKept_{s}", keepSurf);
                            var curOverride = mi.GetSurfaceOverrideMaterial(s);
                            if (keepSurf)
                            {
                                Material origMat = mi.HasMeta($"OriginalMaterial_{s}") ? mi.GetMeta($"OriginalMaterial_{s}").As<Material>() : null;
                                if (origMat != null)
                                    mi.SetSurfaceOverrideMaterial(s, origMat);
                                else if (curOverride != null && curOverride.HasMeta("IsHiddenSurfaceMaterial"))
                                    mi.SetSurfaceOverrideMaterial(s, null);
                            }
                            else
                            {
                                if (!mi.HasMeta($"OriginalMaterial_{s}") && curOverride != null && !curOverride.HasMeta("IsHiddenSurfaceMaterial"))
                                    mi.SetMeta($"OriginalMaterial_{s}", curOverride);
                                mi.SetSurfaceOverrideMaterial(s, hiddenMat);
                            }
                        }
                    }
                    else
                    {
                        for (int s = 0; s < surfCount; s++)
                        {
                            mi.SetMeta($"SurfaceKept_{s}", anyKept);
                            if (allKept)
                            {
                                Material origMat = mi.HasMeta($"OriginalMaterial_{s}") ? mi.GetMeta($"OriginalMaterial_{s}").As<Material>() : null;
                                var curOverride = mi.GetSurfaceOverrideMaterial(s);
                                if (origMat != null)
                                    mi.SetSurfaceOverrideMaterial(s, origMat);
                                else if (curOverride != null && curOverride.HasMeta("IsHiddenSurfaceMaterial"))
                                    mi.SetSurfaceOverrideMaterial(s, null);
                            }
                        }
                    }

                    if (anyKept) mi.RemoveMeta("UserHiddenByCustomModel");
                    else mi.SetMeta("UserHiddenByCustomModel", true);
                }
                else
                {
                    string surfKey = MakeSurfaceKeepKey(rawName, 0);
                    bool keep = !wholeMeshHidden && !combinedHiddenTargets.Contains(surfKey);
                    mi.Visible = keep;
                    mi.SetMeta("UserVisibility", keep);
                    if (keep) mi.RemoveMeta("UserHiddenByCustomModel");
                    else mi.SetMeta("UserHiddenByCustomModel", true);
                }
            }

            // Also apply explicit accessory replacement targets against a complete custom replacement model
            // so independent accessories can replace/hide specific parts of Custom_Rigged_Character cleanly.
            var mainReplacementContainer = heroNode.FindChild("Custom_Rigged_Character", true, false) as Node3D;
            if (mainReplacementContainer != null && GodotObject.IsInstanceValid(mainReplacementContainer))
            {
                var accessoryHiddenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var container in CollectCustomContainers(heroNode))
                {
                    if (!container.Visible || container == mainReplacementContainer) continue;
                    if (container.HasMeta("SlotIsAddAccessory") && container.GetMeta("SlotIsAddAccessory").AsBool())
                        continue;
                    if (container.HasMeta("SlotHiddenTargets"))
                    {
                        var arr = container.GetMeta("SlotHiddenTargets").AsStringArray();
                        if (arr != null)
                        {
                            foreach (var t in arr)
                            {
                                if (!string.IsNullOrEmpty(t)) accessoryHiddenTargets.Add(t);
                            }
                        }
                    }
                }

                foreach (var customMi in CollectMeshes(mainReplacementContainer))
                {
                    if (customMi == null || !GodotObject.IsInstanceValid(customMi)) continue;
                    string cName = customMi.Name.ToString();
                    bool hiddenByAccessory = accessoryHiddenTargets.Contains(cName) ||
                                             accessoryHiddenTargets.Contains(MakeSurfaceKeepKey(cName, 0));
                    customMi.Visible = !hiddenByAccessory;
                    customMi.SetMeta("UserVisibility", !hiddenByAccessory);
                    if (hiddenByAccessory) customMi.SetMeta("UserHiddenByCustomModel", true);
                    else customMi.RemoveMeta("UserHiddenByCustomModel");
                }
            }

            if (newlyBuiltMeshes != null)
            {
                foreach (var cm in newlyBuiltMeshes)
                {
                    if (cm != null && GodotObject.IsInstanceValid(cm)) cm.Visible = true;
                }
            }

            RecomputeHeroDeformations(heroNode);
        }

        private readonly struct ActiveHeroDeformSpec
        {
            public readonly string TargetFilter;
            public readonly string BoneName;
            public readonly HeroDeformMode Mode;
            public readonly float Strength;
            public readonly float Radius;
            public readonly float HeightOffset;

            public ActiveHeroDeformSpec(string targetFilter, string boneName, HeroDeformMode mode, float strength, float radius, float heightOffset)
            {
                TargetFilter = targetFilter ?? "hair";
                BoneName = string.IsNullOrWhiteSpace(boneName) ? "head" : boneName;
                Mode = mode;
                Strength = Mathf.Clamp(strength, 0f, 1f);
                Radius = MathF.Max(0.03f, radius);
                HeightOffset = heightOffset;
            }
        }

        public static string MakeOriginalSurfaceDeformKey(MeshInstance3D mi, int surfIdx)
        {
            if (mi == null || !GodotObject.IsInstanceValid(mi)) return "";
            if (mi.HasMeta("ParentCompositeMesh"))
            {
                var parent = mi.GetMeta("ParentCompositeMesh").As<MeshInstance3D>();
                if (parent != null && GodotObject.IsInstanceValid(parent))
                {
                    int origSurf = mi.HasMeta("SurfaceIndex") ? mi.GetMeta("SurfaceIndex").AsInt32() : surfIdx;
                    return MakeSurfaceKeepKey(parent.Name.ToString(), origSurf);
                }
            }
            return MakeSurfaceKeepKey(mi.Name.ToString(), surfIdx);
        }

        private static Godot.Collections.Dictionary<string, Variant> SerializeSculptsToGodotDict(
            Dictionary<string, Dictionary<int, Vector3>> sculpts)
        {
            var root = new Godot.Collections.Dictionary<string, Variant>();
            if (sculpts == null) return root;
            foreach (var (surfKey, vertMap) in sculpts)
            {
                if (string.IsNullOrEmpty(surfKey) || vertMap == null || vertMap.Count == 0) continue;
                var idxList = new List<int>(vertMap.Count);
                var deltaList = new List<Vector3>(vertMap.Count);
                foreach (var (vi, d) in vertMap)
                {
                    if (d.LengthSquared() < 1e-10f) continue;
                    idxList.Add(vi);
                    deltaList.Add(d);
                }
                if (idxList.Count == 0) continue;
                var entryDict = new Godot.Collections.Dictionary<string, Variant>
                {
                    ["indices"] = idxList.ToArray(),
                    ["deltas"] = deltaList.ToArray()
                };
                root[surfKey] = entryDict;
            }
            return root;
        }

        public static void MergeSerializedSculpts(
            Godot.Collections.Dictionary<string, Variant> srcDict,
            Dictionary<string, Dictionary<int, Vector3>> target)
        {
            if (srcDict == null || target == null) return;
            foreach (var key in srcDict.Keys)
            {
                if (srcDict[key].VariantType != Variant.Type.Dictionary) continue;
                var entry = srcDict[key].AsGodotDictionary();
                if (!entry.ContainsKey("indices") || !entry.ContainsKey("deltas")) continue;
                var indices = entry["indices"].AsInt32Array();
                var deltas = entry["deltas"].AsVector3Array();
                int count = Math.Min(indices.Length, deltas.Length);
                if (count == 0) continue;
                if (!target.TryGetValue(key, out var vertMap))
                {
                    vertMap = new Dictionary<int, Vector3>();
                    target[key] = vertMap;
                }
                for (int i = 0; i < count; i++)
                {
                    int vi = indices[i];
                    Vector3 cur = vertMap.TryGetValue(vi, out var ex) ? ex : Vector3.Zero;
                    vertMap[vi] = cur + deltas[i];
                }
            }
        }

        private static bool IsHeroOriginalMeshForDeform(MeshInstance3D mi, Node3D heroNode)
        {
            if (mi == null || !GodotObject.IsInstanceValid(mi) || mi.Mesh == null || mi.Mesh.GetSurfaceCount() == 0)
                return false;
            if (mi.Mesh is ImmediateMesh) return false;
            if (mi.HasMeta("IsCustomImportedMesh") && mi.GetMeta("IsCustomImportedMesh").AsBool())
                return false;

            string lowerName = mi.Name.ToString().ToLowerInvariant();
            if (lowerName.StartsWith("@", StringComparison.Ordinal) ||
                lowerName.Contains("picker") || lowerName.Contains("handle") || lowerName.Contains("visual") ||
                lowerName.Contains("gizmo") || lowerName.Contains("marker") || lowerName.Contains("preview") ||
                lowerName.Contains("tip") || lowerName.Contains("pole") || lowerName.Contains("outline"))
            {
                return false;
            }

            Node p = mi.GetParent();
            while (p != null && p != heroNode)
            {
                string pName = p.Name.ToString();
                string pLower = pName.ToLowerInvariant();
                if (pName.StartsWith("@", StringComparison.Ordinal) ||
                    pLower.Contains("picker") || pLower.Contains("gizmo") || pLower.Contains("handle") ||
                    pName.Contains("Custom_Rigged_Character") ||
                    pName.StartsWith("Custom_Accessory_", StringComparison.Ordinal) ||
                    pName == "Custom_Rigid_Accessory" ||
                    (p.HasMeta("IsCustomImportedMesh") && p.GetMeta("IsCustomImportedMesh").AsBool()))
                {
                    return false;
                }
                p = p.GetParent();
            }
            return true;
        }

        private static int FindDeformBoneIndex(Skeleton3D skel, string boneName)
        {
            if (skel == null) return -1;
            if (!string.IsNullOrWhiteSpace(boneName))
            {
                int exact = skel.FindBone(boneName);
                if (exact >= 0) return exact;
            }
            int headExact = skel.FindBone("head");
            if (headExact >= 0) return headExact;

            int fallback = -1;
            for (int b = 0; b < skel.GetBoneCount(); b++)
            {
                string bn = skel.GetBoneName(b).ToLowerInvariant();
                if (!bn.Contains("head")) continue;
                if (bn.Contains("_end") || bn.Contains("prop") || bn.Contains("weapon") || bn.Contains("attach"))
                {
                    if (fallback < 0) fallback = b;
                    continue;
                }
                return b;
            }
            return fallback;
        }

        public static void RecomputeHeroDeformations(Node3D heroNode, RiggingOptions previewDeformOverride = null)
        {
            if (heroNode == null || !GodotObject.IsInstanceValid(heroNode)) return;

            // Gather both composite/standalone original meshes, any HeroMeshHierarchy-split _surf_* siblings,
            // and active Custom_Rigged_Character base meshes (so accessories added on top of a complete replacement can cut them cleanly).
            var allOriginalMeshes = new List<MeshInstance3D>();
            var mainReplContainer = heroNode.FindChild("Custom_Rigged_Character", true, false) as Node3D;
            var stack = new Stack<Node>();
            stack.Push(heroNode);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n is MeshInstance3D mi && (IsHeroOriginalMeshForDeform(mi, heroNode) ||
                    (mainReplContainer != null && mi.GetParent() == mainReplContainer && mi.Mesh != null && mi.Mesh.GetSurfaceCount() > 0)))
                {
                    allOriginalMeshes.Add(mi);
                }
                foreach (Node ch in n.GetChildren()) stack.Push(ch);
            }

            // 1. Always restore every previously deformed mesh cleanly back to its original pre-deform Mesh
            foreach (var mi in allOriginalMeshes)
            {
                if (mi.HasMeta("OriginalPreDeformMesh"))
                {
                    var origMesh = mi.GetMeta("OriginalPreDeformMesh").As<Mesh>();
                    if (origMesh != null)
                    {
                        mi.Mesh = origMesh;
                    }
                }
                if (mi.HasMeta("IsDeformedOriginalMesh"))
                {
                    mi.RemoveMeta("IsDeformedOriginalMesh");
                }
            }

            // 2. Gather active parametric deform specs, sculpted vertex deltas, kept-hero-part transforms & triangle cut masks
            var specs = new List<ActiveHeroDeformSpec>();
            var sculptDeltasByKey = new Dictionary<string, Dictionary<int, Vector3>>(StringComparer.OrdinalIgnoreCase);
            var keptPartTransforms = new Dictionary<string, KeptHeroPartTransform>(StringComparer.OrdinalIgnoreCase);
            var cutMasksByKey = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

            // Always union specs, sculpts, kept-part transforms & cut masks from all OTHER visible sibling containers
            // so opening a second accessory in preview still displays existing cuts & deforms from earlier accessories!
            string overrideContainerName = (previewDeformOverride != null && !string.IsNullOrWhiteSpace(previewDeformOverride.SlotId))
                ? GetContainerNameForSlot(previewDeformOverride.SlotId)
                : null;
            foreach (var container in CollectCustomContainers(heroNode))
            {
                if (!container.Visible) continue;
                if (previewDeformOverride != null &&
                    !string.IsNullOrEmpty(overrideContainerName) &&
                    string.Equals(container.Name.ToString(), overrideContainerName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (container.HasMeta("SlotDeformSculpts") && container.GetMeta("SlotDeformSculpts").VariantType == Variant.Type.Dictionary)
                {
                    MergeSerializedSculpts(container.GetMeta("SlotDeformSculpts").AsGodotDictionary<string, Variant>(), sculptDeltasByKey);
                }

                if (container.HasMeta("SlotKeptPartTransforms") && container.GetMeta("SlotKeptPartTransforms").VariantType == Variant.Type.Dictionary)
                {
                    MergeSerializedKeptPartTransforms(container.GetMeta("SlotKeptPartTransforms").AsGodotDictionary<string, Variant>(), keptPartTransforms);
                }

                if (container.HasMeta("SlotOriginalCutMasks") && container.GetMeta("SlotOriginalCutMasks").VariantType == Variant.Type.Dictionary)
                {
                    MergeSerializedCutMasks(container.GetMeta("SlotOriginalCutMasks").AsGodotDictionary<string, Variant>(), cutMasksByKey);
                }

                if (!container.HasMeta("SlotDeformEnabled") || !container.GetMeta("SlotDeformEnabled").AsBool())
                    continue;

                float str = container.HasMeta("SlotDeformStrength") ? (float)container.GetMeta("SlotDeformStrength").AsDouble() : 0.55f;
                if (str <= 0.001f) continue;

                string filter = container.HasMeta("SlotDeformTarget") ? container.GetMeta("SlotDeformTarget").AsString() : "hair";
                string bone = container.HasMeta("SlotDeformBone") ? container.GetMeta("SlotDeformBone").AsString() : "head";
                var mode = container.HasMeta("SlotDeformMode") ? (HeroDeformMode)container.GetMeta("SlotDeformMode").AsInt32() : HeroDeformMode.FlattenTopDown;
                float rad = container.HasMeta("SlotDeformRadius") ? (float)container.GetMeta("SlotDeformRadius").AsDouble() : 0.24f;
                float hOff = container.HasMeta("SlotDeformHeightOffset") ? (float)container.GetMeta("SlotDeformHeightOffset").AsDouble() : 0.06f;

                specs.Add(new ActiveHeroDeformSpec(filter, bone, mode, str, rad, hOff));
            }

            if (previewDeformOverride != null)
            {
                if (previewDeformOverride.OriginalMeshDeformEnabled && previewDeformOverride.OriginalMeshDeformStrength > 0.001f)
                {
                    specs.Add(new ActiveHeroDeformSpec(
                        previewDeformOverride.OriginalMeshDeformTargetFilter,
                        !string.IsNullOrWhiteSpace(previewDeformOverride.OriginalMeshDeformBoneName)
                            ? previewDeformOverride.OriginalMeshDeformBoneName
                            : previewDeformOverride.RigidBoneName,
                        previewDeformOverride.OriginalMeshDeformMode,
                        previewDeformOverride.OriginalMeshDeformStrength,
                        previewDeformOverride.OriginalMeshDeformRadius,
                        previewDeformOverride.OriginalMeshDeformHeightOffset));
                }
                if (previewDeformOverride.OriginalMeshDeformSculpts != null)
                {
                    foreach (var (sk, vMap) in previewDeformOverride.OriginalMeshDeformSculpts)
                    {
                        if (string.IsNullOrEmpty(sk) || vMap == null || vMap.Count == 0) continue;
                        if (!sculptDeltasByKey.TryGetValue(sk, out var dst))
                        {
                            dst = new Dictionary<int, Vector3>();
                            sculptDeltasByKey[sk] = dst;
                        }
                        foreach (var (vi, delta) in vMap)
                            dst[vi] = dst.TryGetValue(vi, out var ex) ? ex + delta : delta;
                    }
                }
                if (previewDeformOverride.KeptOriginalPartTransforms != null)
                {
                    foreach (var (pk, px) in previewDeformOverride.KeptOriginalPartTransforms)
                    {
                        if (string.IsNullOrEmpty(pk) || px == null || px.IsIdentity) continue;
                        keptPartTransforms[pk] = new KeptHeroPartTransform
                        {
                            PositionOffset = px.PositionOffset,
                            RotationDeg = px.RotationDeg,
                            Scale = px.Scale > 0.001f ? px.Scale : 1.0f
                        };
                    }
                }
                if (previewDeformOverride.OriginalMeshCutMasks != null)
                {
                    foreach (var (ck, rle) in previewDeformOverride.OriginalMeshCutMasks)
                    {
                        if (string.IsNullOrWhiteSpace(ck) || rle == null || rle.Length < 2) continue;
                        var decoded = MeshCutEditorCore.DecodeCutMask(rle);
                        if (decoded.Count > 0)
                        {
                            if (!cutMasksByKey.TryGetValue(ck, out var existing))
                            {
                                existing = new HashSet<int>();
                                cutMasksByKey[ck] = existing;
                            }
                            existing.UnionWith(decoded);
                        }
                    }
                }
            }

            if (specs.Count == 0 && sculptDeltasByKey.Count == 0 && keptPartTransforms.Count == 0 && cutMasksByKey.Count == 0) return;

            Skeleton3D heroSkeleton = FindSkeleton(heroNode);
            Transform3D skelToHero = heroSkeleton != null
                ? ComputeTransformRelativeToRoot(heroSkeleton, heroNode)
                : Transform3D.Identity;

            var resolvedSpecs = new (ActiveHeroDeformSpec Spec, Vector3 CenterHero)[specs.Count];
            for (int i = 0; i < specs.Count; i++)
            {
                var sp = specs[i];
                Vector3 boneHeroPos = new Vector3(0f, 1.55f, 0f);
                if (heroSkeleton != null)
                {
                    int bIdx = FindDeformBoneIndex(heroSkeleton, sp.BoneName);
                    if (bIdx >= 0)
                    {
                        boneHeroPos = (skelToHero * heroSkeleton.GetBoneGlobalRest(bIdx)).Origin;
                    }
                }
                resolvedSpecs[i] = (sp, boneHeroPos + new Vector3(0f, sp.HeightOffset, 0f));
            }

            foreach (var mi in allOriginalMeshes)
            {
                Mesh srcMesh = mi.Mesh;
                if (srcMesh == null) continue;
                int surfCount = srcMesh.GetSurfaceCount();
                if (surfCount == 0) continue;

                // Determine if any surface on this MeshInstance3D matches any deform spec, sculpt delta, kept-part transform, or cut mask
                bool anySurfaceMatched = false;
                for (int s = 0; s < surfCount; s++)
                {
                    string surfDeformKey = MakeOriginalSurfaceDeformKey(mi, s);
                    string surfOwnKey = MakeSurfaceKeepKey(mi.Name.ToString(), s);
                    if (sculptDeltasByKey.ContainsKey(surfDeformKey) ||
                        sculptDeltasByKey.ContainsKey(surfOwnKey) ||
                        TryResolveKeptPartTransform(keptPartTransforms, mi, s, surfCount, out _) ||
                        TryResolveCutMask(cutMasksByKey, mi, s, surfCount, out _))
                    {
                        anySurfaceMatched = true;
                        break;
                    }
                    for (int spIdx = 0; spIdx < resolvedSpecs.Length; spIdx++)
                    {
                        if (DoesHeroSurfaceMatchDeformFilter(mi, s, resolvedSpecs[spIdx].Spec.TargetFilter))
                        {
                            anySurfaceMatched = true;
                            break;
                        }
                    }
                    if (anySurfaceMatched) break;
                }
                if (!anySurfaceMatched) continue;

                if (!mi.HasMeta("OriginalPreDeformMesh"))
                {
                    mi.SetMeta("OriginalPreDeformMesh", srcMesh);
                }

                Transform3D miToHero = ComputeTransformRelativeToRoot(mi, heroNode);
                Transform3D heroToMi = miToHero.AffineInverse();

                var deformedMesh = new ArrayMesh();
                bool meshActuallyModified = false;

                for (int s = 0; s < surfCount; s++)
                {
                    var arrays = srcMesh.SurfaceGetArrays(s);
                    Material surfMat = srcMesh.SurfaceGetMaterial(s);
                    // Only set optional creation flags (FlagUse8BoneWeights when 8-bone); passing raw SurfaceGetFormat(s)
                    // carries compressed attribute bits that corrupt bone weights on rebuilt uncompressed ArrayMeshes!
                    Mesh.ArrayFormat formatFlags = default;
                    if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.Vertex)
                    {
                        continue;
                    }

                    Vector3[] verts = GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    if (verts == null || verts.Length == 0) continue;

                    if (arrays.Count > (int)Mesh.ArrayType.Bones && arrays[(int)Mesh.ArrayType.Bones].VariantType != Variant.Type.Nil)
                    {
                        int[] boneIdxArr = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                        if (boneIdxArr != null && boneIdxArr.Length == verts.Length * 8)
                        {
                            formatFlags |= Mesh.ArrayFormat.FlagUse8BoneWeights;
                        }
                    }

                    var matchedSpecs = new List<(ActiveHeroDeformSpec Spec, Vector3 CenterHero)>();
                    for (int spIdx = 0; spIdx < resolvedSpecs.Length; spIdx++)
                    {
                        if (DoesHeroSurfaceMatchDeformFilter(mi, s, resolvedSpecs[spIdx].Spec.TargetFilter))
                        {
                            matchedSpecs.Add(resolvedSpecs[spIdx]);
                        }
                    }

                    string surfDeformKey = MakeOriginalSurfaceDeformKey(mi, s);
                    string surfOwnKey = MakeSurfaceKeepKey(mi.Name.ToString(), s);
                    Dictionary<int, Vector3> surfSculpt = null;
                    if (!sculptDeltasByKey.TryGetValue(surfDeformKey, out surfSculpt))
                    {
                        sculptDeltasByKey.TryGetValue(surfOwnKey, out surfSculpt);
                    }
                    bool hasKeptPartXform = TryResolveKeptPartTransform(keptPartTransforms, mi, s, surfCount, out var keptPartXform);

                    if (matchedSpecs.Count > 0 || (surfSculpt != null && surfSculpt.Count > 0) || hasKeptPartXform)
                    {
                        var clonedVerts = (Vector3[])verts.Clone();
                        Vector3[] norms = arrays.Count > (int)Mesh.ArrayType.Normal
                            ? GetVector3Array(arrays[(int)Mesh.ArrayType.Normal])
                            : null;
                        Vector3[] clonedNorms = norms != null && norms.Length == verts.Length
                            ? (Vector3[])norms.Clone()
                            : null;
                        float[] vertexWeights = new float[verts.Length];
                        bool surfDisplaced = false;

                        if (matchedSpecs.Count > 0 || (surfSculpt != null && surfSculpt.Count > 0))
                        {

                        for (int v = 0; v < clonedVerts.Length; v++)
                        {
                            Vector3 vHero = miToHero * clonedVerts[v];
                            Vector3 origHero = vHero;
                            float maxW = 0f;

                            foreach (var (sp, center) in matchedSpecs)
                            {
                                Vector3 dVec = vHero - center;
                                float w = 0f;

                                if (sp.Mode == HeroDeformMode.FlattenTopDown)
                                {
                                    float horiz = MathF.Sqrt(dVec.X * dVec.X + dVec.Z * dVec.Z);
                                    float rHoriz = sp.Radius * 1.25f;
                                    if (horiz < rHoriz)
                                    {
                                        float tH = Math.Clamp(1f - horiz / rHoriz, 0f, 1f);
                                        float wH = tH * tH * (3f - 2f * tH);
                                        float minY = center.Y - sp.Radius * 0.35f;
                                        float band = MathF.Max(0.03f, sp.Radius * 0.45f);
                                        float tV = Math.Clamp((vHero.Y - minY) / band, 0f, 1f);
                                        float wV = tV * tV * (3f - 2f * tV);
                                        w = wH * wV * sp.Strength;
                                        if (w > 0.0001f)
                                        {
                                            float excessY = MathF.Max(0f, vHero.Y - center.Y);
                                            if (excessY > 0f)
                                            {
                                                float topT = Math.Clamp(excessY / MathF.Max(0.02f, sp.Radius * 0.3f), 0f, 1f);
                                                float topSmooth = topT * topT * (3f - 2f * topT);
                                                vHero.Y -= excessY * wH * sp.Strength * (0.65f + 0.27f * topSmooth);
                                            }
                                            float tuck = 1f - (w * 0.22f);
                                            vHero.X = center.X + dVec.X * tuck;
                                            vHero.Z = center.Z + dVec.Z * tuck;
                                        }
                                    }
                                }
                                else
                                {
                                    float dist = dVec.Length();
                                    if (dist < sp.Radius)
                                    {
                                        float tR = Math.Clamp(1f - dist / sp.Radius, 0f, 1f);
                                        w = tR * tR * (3f - 2f * tR) * sp.Strength;
                                        if (w > 0.0001f)
                                        {
                                            vHero = vHero.Lerp(center, w * 0.65f);
                                        }
                                    }
                                }
                                if (w > maxW) maxW = w;
                            }

                            if (surfSculpt != null && surfSculpt.TryGetValue(v, out var sDelta) && sDelta.LengthSquared() > 1e-10f)
                            {
                                vHero += sDelta;
                                float sw = Math.Clamp(sDelta.Length() * 14f, 0.25f, 1.0f);
                                if (sw > maxW) maxW = sw;
                            }

                            if (maxW > 0.0001f && vHero.DistanceSquaredTo(origHero) > 1e-10f)
                            {
                                clonedVerts[v] = heroToMi * vHero;
                                vertexWeights[v] = maxW;
                                surfDisplaced = true;
                            }
                        }

                        if (surfDisplaced)
                        {
                            meshActuallyModified = true;
                            if (clonedNorms != null)
                            {
                                int[] triIdx = arrays.Count > (int)Mesh.ArrayType.Index
                                    ? GetInt32Array(arrays[(int)Mesh.ArrayType.Index])
                                    : null;
                                Vector3[] accumNorm = new Vector3[clonedVerts.Length];
                                int triCount = triIdx != null ? triIdx.Length / 3 : clonedVerts.Length / 3;
                                for (int t = 0; t < triCount; t++)
                                {
                                    int i0 = triIdx != null ? triIdx[t * 3 + 0] : t * 3 + 0;
                                    int i1 = triIdx != null ? triIdx[t * 3 + 1] : t * 3 + 1;
                                    int i2 = triIdx != null ? triIdx[t * 3 + 2] : t * 3 + 2;
                                    if ((uint)i0 >= (uint)clonedVerts.Length || (uint)i1 >= (uint)clonedVerts.Length || (uint)i2 >= (uint)clonedVerts.Length)
                                        continue;
                                    Vector3 e1 = clonedVerts[i1] - clonedVerts[i0];
                                    Vector3 e2 = clonedVerts[i2] - clonedVerts[i0];
                                    Vector3 fn = e2.Cross(e1);
                                    accumNorm[i0] += fn;
                                    accumNorm[i1] += fn;
                                    accumNorm[i2] += fn;
                                }
                                for (int v = 0; v < clonedVerts.Length; v++)
                                {
                                    if (vertexWeights[v] > 0.001f && accumNorm[v].LengthSquared() > 1e-10f)
                                    {
                                        Vector3 smoothN = accumNorm[v].Normalized();
                                        float blend = MathF.Min(1f, vertexWeights[v] * 1.4f);
                                        Vector3 n = clonedNorms[v].Lerp(smoothN, blend);
                                        if (n.LengthSquared() > 1e-10f) clonedNorms[v] = n.Normalized();
                                    }
                                }
                                arrays[(int)Mesh.ArrayType.Normal] = clonedNorms;
                            }
                            arrays[(int)Mesh.ArrayType.Vertex] = clonedVerts;
                        }
                        }

                        if (hasKeptPartXform && keptPartXform != null && !keptPartXform.IsIdentity)
                        {
                            Transform3D boneRestHero = ResolveKeptHeroPartPivotRestHero(
                                mi, s, arrays, verts, heroSkeleton, skelToHero, miToHero);
                            Basis deltaRot = Basis.FromEuler(new Vector3(
                                Mathf.DegToRad(keptPartXform.RotationDeg.X),
                                Mathf.DegToRad(keptPartXform.RotationDeg.Y),
                                Mathf.DegToRad(keptPartXform.RotationDeg.Z)), EulerOrder.Yxz);
                            float sc = Mathf.Clamp(keptPartXform.Scale > 0.001f ? keptPartXform.Scale : 1.0f, 0.05f, 10.0f);
                            Transform3D deltaLocal = new Transform3D(deltaRot.Scaled(new Vector3(sc, sc, sc)), keptPartXform.PositionOffset);
                            Transform3D warpHero = boneRestHero * deltaLocal * boneRestHero.AffineInverse();
                            Basis normalRotHero = (boneRestHero.Basis * deltaRot * boneRestHero.Basis.Inverse()).Orthonormalized();
                            Basis normalRotMi = (heroToMi.Basis * normalRotHero * miToHero.Basis).Orthonormalized();

                            for (int v = 0; v < clonedVerts.Length; v++)
                            {
                                Vector3 vHero = miToHero * clonedVerts[v];
                                clonedVerts[v] = heroToMi * (warpHero * vHero);
                                if (clonedNorms != null && clonedNorms[v].LengthSquared() > 1e-10f)
                                {
                                    clonedNorms[v] = (normalRotMi * clonedNorms[v]).Normalized();
                                }
                            }
                            arrays[(int)Mesh.ArrayType.Vertex] = clonedVerts;
                            if (clonedNorms != null)
                            {
                                arrays[(int)Mesh.ArrayType.Normal] = clonedNorms;
                            }
                            if (arrays.Count > (int)Mesh.ArrayType.Tangent && arrays[(int)Mesh.ArrayType.Tangent].VariantType != Variant.Type.Nil)
                            {
                                float[] srcTan = arrays[(int)Mesh.ArrayType.Tangent].AsFloat32Array();
                                if (srcTan != null && srcTan.Length == clonedVerts.Length * 4)
                                {
                                    var clonedTan = (float[])srcTan.Clone();
                                    for (int v = 0; v < clonedVerts.Length; v++)
                                    {
                                        int off = v * 4;
                                        Vector3 tVec = new Vector3(clonedTan[off], clonedTan[off + 1], clonedTan[off + 2]);
                                        if (tVec.LengthSquared() > 1e-10f)
                                        {
                                            Vector3 tRot = (normalRotMi * tVec).Normalized();
                                            clonedTan[off] = tRot.X;
                                            clonedTan[off + 1] = tRot.Y;
                                            clonedTan[off + 2] = tRot.Z;
                                        }
                                    }
                                    arrays[(int)Mesh.ArrayType.Tangent] = clonedTan;
                                }
                            }
                            meshActuallyModified = true;
                        }
                    }

                    if (TryResolveCutMask(cutMasksByKey, mi, s, surfCount, out var surfCutMask) &&
                        surfCutMask != null && surfCutMask.Count > 0)
                    {
                        int[] rawIdx = arrays.Count > (int)Mesh.ArrayType.Index
                            ? GetInt32Array(arrays[(int)Mesh.ArrayType.Index])
                            : null;
                        int[] filteredIdx = MeshCutEditorCore.FilterTriangleIndices(rawIdx, verts.Length, surfCutMask, out _, out _);
                        arrays[(int)Mesh.ArrayType.Index] = filteredIdx;
                        meshActuallyModified = true;
                    }

                    deformedMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, formatFlags);
                    int addedSurfIdx = deformedMesh.GetSurfaceCount() - 1;
                    if (surfMat != null) deformedMesh.SurfaceSetMaterial(addedSurfIdx, surfMat);
                    if (srcMesh is ArrayMesh srcArrMeshForName)
                    {
                        deformedMesh.SurfaceSetName(addedSurfIdx, srcArrMeshForName.SurfaceGetName(s));
                    }
                }

                if (meshActuallyModified && deformedMesh.GetSurfaceCount() == surfCount)
                {
                    deformedMesh.LightmapSizeHint = srcMesh.LightmapSizeHint;
                    mi.Mesh = deformedMesh;
                    mi.SetMeta("IsDeformedOriginalMesh", true);
                }
            }
        }

        public static Godot.Collections.Dictionary<string, Variant> SerializeKeptPartTransformsToGodotDict(
            Dictionary<string, KeptHeroPartTransform> transforms)
        {
            var root = new Godot.Collections.Dictionary<string, Variant>();
            if (transforms == null) return root;
            foreach (var (key, xf) in transforms)
            {
                if (string.IsNullOrWhiteSpace(key) || xf == null || xf.IsIdentity) continue;
                root[key] = new Godot.Collections.Dictionary<string, Variant>
                {
                    ["pos"] = xf.PositionOffset,
                    ["rot"] = xf.RotationDeg,
                    ["scale"] = xf.Scale > 0.001f ? xf.Scale : 1.0f
                };
            }
            return root;
        }

        public static void MergeSerializedKeptPartTransforms(
            Godot.Collections.Dictionary<string, Variant> srcDict,
            Dictionary<string, KeptHeroPartTransform> target)
        {
            if (srcDict == null || target == null) return;
            foreach (var key in srcDict.Keys)
            {
                if (string.IsNullOrWhiteSpace(key) || srcDict[key].VariantType != Variant.Type.Dictionary) continue;
                var d = srcDict[key].AsGodotDictionary();
                Vector3 pos = d.ContainsKey("pos") ? d["pos"].AsVector3() : Vector3.Zero;
                Vector3 rot = d.ContainsKey("rot") ? d["rot"].AsVector3() : Vector3.Zero;
                float scale = d.ContainsKey("scale") ? (float)d["scale"].AsDouble() : 1.0f;
                var xf = new KeptHeroPartTransform
                {
                    PositionOffset = pos,
                    RotationDeg = rot,
                    Scale = scale > 0.001f ? scale : 1.0f
                };
                if (!xf.IsIdentity) target[key] = xf;
            }
        }

        private static bool TryResolveKeptPartTransform(
            Dictionary<string, KeptHeroPartTransform> map,
            MeshInstance3D mi,
            int surfIdx,
            int surfCount,
            out KeptHeroPartTransform transform)
        {
            transform = null;
            if (map == null || map.Count == 0 || mi == null || !GodotObject.IsInstanceValid(mi)) return false;

            string canonicalKey = MakeOriginalSurfaceDeformKey(mi, surfIdx);
            if (!string.IsNullOrEmpty(canonicalKey) && map.TryGetValue(canonicalKey, out transform) && transform != null && !transform.IsIdentity)
                return true;

            string ownKey = MakeSurfaceKeepKey(mi.Name.ToString(), surfIdx);
            if (!string.IsNullOrEmpty(ownKey) && map.TryGetValue(ownKey, out transform) && transform != null && !transform.IsIdentity)
                return true;

            if (surfCount == 1)
            {
                string bareName = mi.Name.ToString();
                if (map.TryGetValue(bareName, out transform) && transform != null && !transform.IsIdentity)
                    return true;
                if (mi.HasMeta("ParentCompositeMesh"))
                {
                    var p = mi.GetMeta("ParentCompositeMesh").As<MeshInstance3D>();
                    if (p != null && GodotObject.IsInstanceValid(p))
                    {
                        int pSurf = mi.HasMeta("SurfaceIndex") ? mi.GetMeta("SurfaceIndex").AsInt32() : surfIdx;
                        string parentKey = MakeSurfaceKeepKey(p.Name.ToString(), pSurf);
                        if (map.TryGetValue(parentKey, out transform) && transform != null && !transform.IsIdentity)
                            return true;
                    }
                }
            }
            transform = null;
            return false;
        }

        public static Godot.Collections.Dictionary<string, Variant> SerializeCutMasksToGodotDict(
            Dictionary<string, int[]> cutMasks)
        {
            var root = new Godot.Collections.Dictionary<string, Variant>();
            if (cutMasks == null) return root;
            foreach (var (key, rle) in cutMasks)
            {
                if (string.IsNullOrWhiteSpace(key) || rle == null || rle.Length < 2) continue;
                root[key] = (int[])rle.Clone();
            }
            return root;
        }

        public static void MergeSerializedCutMasks(
            Godot.Collections.Dictionary<string, Variant> srcDict,
            Dictionary<string, HashSet<int>> target)
        {
            if (srcDict == null || target == null) return;
            foreach (var key in srcDict.Keys)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                int[] rle = srcDict[key].AsInt32Array();
                if (rle == null || rle.Length < 2) continue;
                var decoded = MeshCutEditorCore.DecodeCutMask(rle);
                if (decoded.Count == 0) continue;
                if (!target.TryGetValue(key, out var existing))
                {
                    existing = new HashSet<int>();
                    target[key] = existing;
                }
                existing.UnionWith(decoded);
            }
        }

        private static bool TryResolveCutMask(
            Dictionary<string, HashSet<int>> map,
            MeshInstance3D mi,
            int surfIdx,
            int surfCount,
            out HashSet<int> mask)
        {
            mask = null;
            if (map == null || map.Count == 0 || mi == null || !GodotObject.IsInstanceValid(mi)) return false;

            string canonicalKey = MakeOriginalSurfaceDeformKey(mi, surfIdx);
            if (!string.IsNullOrEmpty(canonicalKey) && map.TryGetValue(canonicalKey, out mask) && mask != null && mask.Count > 0)
                return true;

            string ownKey = MakeSurfaceKeepKey(mi.Name.ToString(), surfIdx);
            if (!string.IsNullOrEmpty(ownKey) && map.TryGetValue(ownKey, out mask) && mask != null && mask.Count > 0)
                return true;

            if (surfCount == 1)
            {
                string bareName = mi.Name.ToString();
                if (map.TryGetValue(bareName, out mask) && mask != null && mask.Count > 0)
                    return true;
                if (mi.HasMeta("ParentCompositeMesh"))
                {
                    var p = mi.GetMeta("ParentCompositeMesh").As<MeshInstance3D>();
                    if (p != null && GodotObject.IsInstanceValid(p))
                    {
                        int pSurf = mi.HasMeta("SurfaceIndex") ? mi.GetMeta("SurfaceIndex").AsInt32() : surfIdx;
                        string parentKey = MakeSurfaceKeepKey(p.Name.ToString(), pSurf);
                        if (map.TryGetValue(parentKey, out mask) && mask != null && mask.Count > 0)
                            return true;
                    }
                }
            }
            mask = null;
            return false;
        }

        /// <summary>
        /// Non-destructively applies RLE triangle cut masks to imported or vanilla donor MeshInstance3D nodes,
        /// preserving original vertices, normals, UVs, and skinning weights while storing OriginalPreCutMesh
        /// so cuts remain 100% undoable and restorable.
        /// </summary>
        public static void ApplyImportedMeshCutMasks(
            IEnumerable<MeshInstance3D> meshes,
            Dictionary<string, int[]> importedCutMasks)
        {
            if (meshes == null) return;
            foreach (var mi in meshes)
            {
                if (mi == null || !GodotObject.IsInstanceValid(mi) || mi.Mesh == null) continue;

                Mesh pristineMesh = mi.HasMeta("OriginalPreCutMesh")
                    ? (mi.GetMeta("OriginalPreCutMesh").As<Mesh>() ?? mi.Mesh)
                    : mi.Mesh;

                int surfCount = pristineMesh.GetSurfaceCount();
                if (surfCount <= 0) continue;

                string srcName = mi.HasMeta("SourceMeshName") ? mi.GetMeta("SourceMeshName").AsString() : mi.Name.ToString();
                int srcSurfBase = mi.HasMeta("SourceSurfaceIndex") ? mi.GetMeta("SourceSurfaceIndex").AsInt32() : 0;

                bool anySurfaceCut = false;
                var surfMasks = new HashSet<int>[surfCount];
                if (importedCutMasks != null && importedCutMasks.Count > 0)
                {
                    for (int s = 0; s < surfCount; s++)
                    {
                        int effSurf = mi.HasMeta("SourceSurfaceIndex") ? srcSurfBase : s;
                        int[] rle = null;
                        string k1 = MakeSurfaceKeepKey(srcName, effSurf);
                        string k2 = MakeSurfaceKeepKey(mi.Name.ToString(), s);
                        if (!importedCutMasks.TryGetValue(k1, out rle) &&
                            !importedCutMasks.TryGetValue(k2, out rle) &&
                            surfCount == 1)
                        {
                            if (!importedCutMasks.TryGetValue(srcName, out rle))
                                importedCutMasks.TryGetValue(mi.Name.ToString(), out rle);
                        }
                        if (rle != null && rle.Length >= 2)
                        {
                            var decoded = MeshCutEditorCore.DecodeCutMask(rle);
                            if (decoded.Count > 0)
                            {
                                surfMasks[s] = decoded;
                                anySurfaceCut = true;
                            }
                        }
                    }
                }

                if (!anySurfaceCut)
                {
                    if (mi.HasMeta("OriginalPreCutMesh") && !ReferenceEquals(mi.Mesh, pristineMesh))
                    {
                        mi.Mesh = pristineMesh;
                    }
                    continue;
                }

                if (!mi.HasMeta("OriginalPreCutMesh"))
                {
                    mi.SetMeta("OriginalPreCutMesh", pristineMesh);
                }

                var cutMesh = new ArrayMesh();
                for (int s = 0; s < surfCount; s++)
                {
                    var arrays = pristineMesh.SurfaceGetArrays(s);
                    Material surfMat = pristineMesh.SurfaceGetMaterial(s);
                    Mesh.ArrayFormat fmt = default;
                    if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.Vertex) continue;
                    var verts = GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    if (verts == null || verts.Length == 0) continue;

                    if (arrays.Count > (int)Mesh.ArrayType.Bones && arrays[(int)Mesh.ArrayType.Bones].VariantType != Variant.Type.Nil)
                    {
                        var bArr = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                        if (bArr != null && bArr.Length == verts.Length * 8)
                            fmt |= Mesh.ArrayFormat.FlagUse8BoneWeights;
                    }

                    if (surfMasks[s] != null && surfMasks[s].Count > 0)
                    {
                        int[] rawIdx = arrays.Count > (int)Mesh.ArrayType.Index
                            ? GetInt32Array(arrays[(int)Mesh.ArrayType.Index])
                            : null;
                        arrays[(int)Mesh.ArrayType.Index] = MeshCutEditorCore.FilterTriangleIndices(
                            rawIdx, verts.Length, surfMasks[s], out _, out _);
                    }

                    cutMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, fmt);
                    int idx = cutMesh.GetSurfaceCount() - 1;
                    if (surfMat != null) cutMesh.SurfaceSetMaterial(idx, surfMat);
                    if (pristineMesh is ArrayMesh srcArrForName)
                        cutMesh.SurfaceSetName(idx, srcArrForName.SurfaceGetName(s));
                }

                if (cutMesh.GetSurfaceCount() == surfCount)
                {
                    cutMesh.LightmapSizeHint = pristineMesh.LightmapSizeHint;
                    mi.Mesh = cutMesh;
                }
            }
        }

        private static int ResolveDominantSkeletonBoneForSurface(
            MeshInstance3D mi,
            Godot.Collections.Array arrays,
            Vector3[] verts,
            Skeleton3D heroSkeleton)
        {
            if (heroSkeleton == null || arrays == null || verts == null || verts.Length == 0) return -1;
            int[] rawBones = arrays.Count > (int)Mesh.ArrayType.Bones ? GetInt32Array(arrays[(int)Mesh.ArrayType.Bones]) : null;
            float[] rawWeights = arrays.Count > (int)Mesh.ArrayType.Weights ? GetFloat32Array(arrays[(int)Mesh.ArrayType.Weights]) : null;
            if (rawBones == null || rawWeights == null || rawBones.Length == 0 || rawWeights.Length == 0) return -1;

            int bpv = rawWeights.Length / verts.Length;
            if (bpv <= 0 || rawBones.Length < verts.Length * bpv) return -1;

            var weightBySkelBone = new Dictionary<int, float>();
            int step = verts.Length > 2000 ? 4 : 1;
            for (int v = 0; v < verts.Length; v += step)
            {
                int off = v * bpv;
                for (int k = 0; k < bpv; k++)
                {
                    float w = rawWeights[off + k];
                    if (w <= 0.001f) continue;
                    int rawIdx = rawBones[off + k];
                    int skelIdx = -1;
                    if (mi?.Skin != null && rawIdx >= 0 && rawIdx < mi.Skin.GetBindCount())
                    {
                        skelIdx = mi.Skin.GetBindBone(rawIdx);
                        if (skelIdx < 0)
                        {
                            string bName = mi.Skin.GetBindName(rawIdx);
                            if (!string.IsNullOrEmpty(bName)) skelIdx = heroSkeleton.FindBone(bName);
                        }
                    }
                    else if (rawIdx >= 0 && rawIdx < heroSkeleton.GetBoneCount())
                    {
                        skelIdx = rawIdx;
                    }
                    if (skelIdx >= 0)
                    {
                        weightBySkelBone[skelIdx] = weightBySkelBone.TryGetValue(skelIdx, out float cur) ? cur + w : w;
                    }
                }
            }

            int bestBone = -1;
            float bestW = 0f;
            foreach (var (bIdx, sum) in weightBySkelBone)
            {
                if (sum > bestW)
                {
                    bestW = sum;
                    bestBone = bIdx;
                }
            }
            return bestBone;
        }

        private static Transform3D ResolveKeptHeroPartPivotRestHero(
            MeshInstance3D mi,
            int surfIdx,
            Godot.Collections.Array arrays,
            Vector3[] verts,
            Skeleton3D heroSkeleton,
            Transform3D skelToHero,
            Transform3D miToHero)
        {
            if (heroSkeleton != null)
            {
                int bestBone = ResolveDominantSkeletonBoneForSurface(mi, arrays, verts, heroSkeleton);
                if (bestBone >= 0 && bestBone < heroSkeleton.GetBoneCount())
                {
                    Transform3D bRest = skelToHero * heroSkeleton.GetBoneGlobalRest(bestBone);
                    return new Transform3D(bRest.Basis.Orthonormalized(), bRest.Origin);
                }
            }

            Vector3 minV = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 maxV = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int v = 0; v < verts.Length; v++)
            {
                Vector3 vh = miToHero * verts[v];
                minV = minV.Min(vh);
                maxV = maxV.Max(vh);
            }
            Vector3 center = verts.Length > 0 ? (minV + maxV) * 0.5f : Vector3.Zero;
            return new Transform3D(Basis.Identity, center);
        }

        public static bool FindDominantBoneForHeroKeepKey(
            Node3D heroNode,
            string keepKey,
            out string boneName,
            out Transform3D pivotRestHero)
        {
            boneName = null;
            pivotRestHero = Transform3D.Identity;
            if (heroNode == null || !GodotObject.IsInstanceValid(heroNode) || string.IsNullOrWhiteSpace(keepKey))
                return false;

            Skeleton3D heroSkeleton = FindSkeleton(heroNode);
            Transform3D skelToHero = heroSkeleton != null ? ComputeTransformRelativeToRoot(heroSkeleton, heroNode) : Transform3D.Identity;

            var lookup = new Dictionary<string, KeptHeroPartTransform>(StringComparer.OrdinalIgnoreCase)
            {
                [keepKey] = new KeptHeroPartTransform { Scale = 2.0f } // Sentinel non-identity
            };

            foreach (var mi in CollectMeshes(heroNode))
            {
                if (!IsHeroOriginalMeshForDeform(mi, heroNode)) continue;
                Mesh srcMesh = mi.HasMeta("OriginalPreDeformMesh") ? mi.GetMeta("OriginalPreDeformMesh").As<Mesh>() : mi.Mesh;
                if (srcMesh == null) continue;
                int surfCount = srcMesh.GetSurfaceCount();
                Transform3D miToHero = ComputeTransformRelativeToRoot(mi, heroNode);

                for (int s = 0; s < surfCount; s++)
                {
                    if (!TryResolveKeptPartTransform(lookup, mi, s, surfCount, out _)) continue;
                    var arrays = srcMesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.Vertex) continue;
                    var verts = GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    if (verts == null || verts.Length == 0) continue;

                    if (heroSkeleton != null)
                    {
                        int bestBone = ResolveDominantSkeletonBoneForSurface(mi, arrays, verts, heroSkeleton);
                        if (bestBone >= 0 && bestBone < heroSkeleton.GetBoneCount())
                        {
                            boneName = heroSkeleton.GetBoneName(bestBone);
                            Transform3D bRest = skelToHero * heroSkeleton.GetBoneGlobalRest(bestBone);
                            pivotRestHero = new Transform3D(bRest.Basis.Orthonormalized(), bRest.Origin);
                            return true;
                        }
                    }

                    pivotRestHero = ResolveKeptHeroPartPivotRestHero(mi, s, arrays, verts, heroSkeleton, skelToHero, miToHero);
                    return true;
                }
            }
            return false;
        }

        public static bool DoesHeroSurfaceMatchDeformFilter(MeshInstance3D mi, int surfIdx, string filter)
        {
            if (mi == null) return false;
            string f = (filter ?? "hair").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(f)) return false;

            string rawName = mi.Name.ToString();
            string matName = mi.Mesh?.SurfaceGetMaterial(surfIdx)?.ResourceName ?? "";
            string vmatName = "";
            try { vmatName = DeadlockPlayground.Painter.HeroMeshHierarchy.ResolveAuthenticVmatFullPath(mi, surfIdx) ?? ""; } catch { }
            string origVmat = mi.HasMeta("OriginalVmatPath") ? mi.GetMeta("OriginalVmatPath").AsString() : "";
            string surfName = (mi.Mesh is ArrayMesh arrMesh) ? arrMesh.SurfaceGetName(surfIdx) : "";
            string surfKey = MakeSurfaceKeepKey(rawName, surfIdx);

            string parentName = "";
            string parentSurfKey = "";
            if (mi.HasMeta("ParentCompositeMesh"))
            {
                var p = mi.GetMeta("ParentCompositeMesh").As<MeshInstance3D>();
                if (p != null && GodotObject.IsInstanceValid(p))
                {
                    parentName = p.Name.ToString();
                    int pSurf = mi.HasMeta("SurfaceIndex") ? mi.GetMeta("SurfaceIndex").AsInt32() : surfIdx;
                    parentSurfKey = MakeSurfaceKeepKey(parentName, pSurf);
                }
            }

            string combined = $"{rawName} {matName} {vmatName} {origVmat} {surfName} {parentName}".ToLowerInvariant();

            if (f == "all") return true;
            if (f == "hair")
            {
                return combined.Contains("hair") || combined.Contains("ponytail") ||
                       combined.Contains("braid") || combined.Contains("bangs") ||
                       combined.Contains("mohawk") || combined.Contains("fur") ||
                       combined.Contains("wig") || combined.Contains("mane") ||
                       combined.Contains("locks") || combined.Contains("tuft") ||
                       combined.Contains("crest");
            }
            if (f == "head")
            {
                return combined.Contains("hair") || combined.Contains("head") ||
                       combined.Contains("scalp") || combined.Contains("face") ||
                       combined.Contains("skull") || combined.Contains("cranium") ||
                       combined.Contains("hat") || combined.Contains("hood") ||
                       combined.Contains("helmet") || combined.Contains("cap");
            }

            return string.Equals(surfKey, f, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(parentSurfKey, f, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(rawName, f, StringComparison.OrdinalIgnoreCase) ||
                   combined.Contains(f);
        }

        private static Material CreateDefaultPbrMaterial(string name)
        {
            var mat = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.9f, 0.9f, 0.9f),
                Roughness = 0.5f,
                Metallic = 0.0f
            };
            mat.SetMeta("MaterialName", name);
            return mat;
        }

        private static int FindBoneAny(Skeleton3D skeleton, params string[] candidates)
        {
            if (skeleton == null || candidates == null) return -1;
            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c)) continue;
                int idx = skeleton.FindBone(c);
                if (idx != -1) return idx;
            }
            int count = skeleton.GetBoneCount();
            for (int b = 0; b < count; b++)
            {
                string bn = skeleton.GetBoneName(b);
                foreach (var c in candidates)
                {
                    if (!string.IsNullOrEmpty(c) && bn.Equals(c, StringComparison.OrdinalIgnoreCase))
                        return b;
                }
            }
            return -1;
        }

        public static Dictionary<int, Quaternion> BackupSkeletonLocalRotations(Skeleton3D skeleton)
        {
            var dict = new Dictionary<int, Quaternion>();
            for (int b = 0; b < skeleton.GetBoneCount(); b++)
            {
                dict[b] = skeleton.GetBonePoseRotation(b);
            }
            return dict;
        }

        public static void RestoreSkeletonLocalRotations(Skeleton3D skeleton, Dictionary<int, Quaternion> backup)
        {
            foreach (var (b, rot) in backup)
            {
                skeleton.SetBonePoseRotation(b, rot);
            }
            skeleton.ForceUpdateAllBoneTransforms();
        }

        public static Skeleton3D FindSkeleton(Node root)
        {
            if (root is Skeleton3D skel) return skel;
            foreach (Node child in root.GetChildren())
            {
                var s = FindSkeleton(child);
                if (s != null) return s;
            }
            return null;
        }

        public static Skin FindSkin(Node root)
        {
            if (root is MeshInstance3D mi && mi.Skin != null) return mi.Skin;
            foreach (Node child in root.GetChildren())
            {
                var s = FindSkin(child);
                if (s != null) return s;
            }
            return null;
        }

        public static Skin CreateFullSkeletonSkin(Skeleton3D skeleton)
        {
            var skin = new Skin();
            if (skeleton == null) return skin;
            int count = skeleton.GetBoneCount();
            for (int b = 0; b < count; b++)
            {
                skin.AddBind(b, skeleton.GetBoneGlobalRest(b).AffineInverse());
                skin.SetBindName(b, skeleton.GetBoneName(b));
            }
            return skin;
        }

        public static List<MeshInstance3D> CollectMeshes(Node root)
        {
            var list = new List<MeshInstance3D>();
            CollectMeshesRecursive(root, list);
            return list;
        }

        private static void CollectMeshesRecursive(Node node, List<MeshInstance3D> list)
        {
            if (node is MeshInstance3D mi && mi.Mesh != null)
            {
                if ((mi.HasMeta("IsWireframeOverlay") && mi.GetMeta("IsWireframeOverlay").AsBool()) ||
                    (mi.HasMeta("IsMeshCutOverlay") && mi.GetMeta("IsMeshCutOverlay").AsBool()))
                    return;
                list.Add(mi);
            }
            foreach (Node child in node.GetChildren())
            {
                CollectMeshesRecursive(child, list);
            }
        }

        private static bool HasSkinnedVertices(MeshInstance3D mi)
        {
            if (mi.Mesh == null || mi.Mesh.GetSurfaceCount() == 0) return false;
            var arrays = mi.Mesh.SurfaceGetArrays(0);
            return arrays != null && arrays.Count > (int)Mesh.ArrayType.Bones && arrays[(int)Mesh.ArrayType.Bones].VariantType != Variant.Type.Nil;
        }

        public static Vector3[] GetVector3Array(Variant v) => v.VariantType != Variant.Type.Nil ? v.As<Vector3[]>() : null;
        public static Vector2[] GetVector2Array(Variant v) => v.VariantType != Variant.Type.Nil ? v.As<Vector2[]>() : null;
        public static int[] GetInt32Array(Variant v) => v.VariantType != Variant.Type.Nil ? v.As<int[]>() : null;
        public static float[] GetFloat32Array(Variant v) => v.VariantType != Variant.Type.Nil ? v.As<float[]>() : null;

        /// <summary>
        /// Computes exact relative Transform3D of a descendant node relative to a given ancestor root.
        /// Does NOT require nodes to be inside the SceneTree, avoiding C++ "!is_inside_tree()" errors.
        /// </summary>
        public static Transform3D ComputeTransformRelativeToRoot(Node3D node, Node3D root)
        {
            if (node == null || node == root) return Transform3D.Identity;
            Transform3D t = node.Transform;
            Node parent = node.GetParent();
            while (parent != null && parent is Node3D p3d && parent != root)
            {
                t = p3d.Transform * t;
                parent = parent.GetParent();
            }
            return t;
        }

        public static List<MeshInstance3D> CollectOriginalHeroMeshes(Node root)
        {
            var list = new List<MeshInstance3D>();
            CollectOriginalHeroMeshesRecursive(root, list);
            return list;
        }

        private static void CollectOriginalHeroMeshesRecursive(Node node, List<MeshInstance3D> list)
        {
            if (node == null) return;
            string nName = node.Name.ToString();
            string lower = nName.ToLowerInvariant();
            if (nName == "SkeletonGizmoManager" || nName == "BoneLayerManager" || nName == "CharacterIKManager" ||
                lower.Contains("gizmo") || lower.Contains("picker") || lower.Contains("tipvisual") || lower.Contains("polevisual") ||
                nName.StartsWith("@") || nName.Contains("Custom_Rigged_Character") || nName.StartsWith("Custom_Accessory_") || nName == "Custom_Rigid_Accessory")
            {
                return;
            }

            if (node is MeshInstance3D mi && mi.Mesh != null)
            {
                // Skip generated submesh clones (_surf_*) when parent composite mesh is present
                if (mi.HasMeta("ParentCompositeMesh"))
                {
                    var parentComp = mi.GetMeta("ParentCompositeMesh").As<MeshInstance3D>();
                    if (parentComp != null && GodotObject.IsInstanceValid(parentComp))
                    {
                        return;
                    }
                }

                if (!mi.HasMeta("IsCustomImportedMesh") &&
                    DeadlockPlayground.Painter.HeroMeshHierarchy.IsOriginalHeroMesh(mi))
                {
                    list.Add(mi);
                }
            }

            foreach (Node child in node.GetChildren())
            {
                CollectOriginalHeroMeshesRecursive(child, list);
            }
        }

        public static List<MeshInstance3D> CollectAuthenticHeroMeshes(Node root)
        {
            var list = new List<MeshInstance3D>();
            CollectAuthenticHeroMeshesRecursive(root, list);
            return list;
        }

        private static void CollectAuthenticHeroMeshesRecursive(Node node, List<MeshInstance3D> list)
        {
            if (node == null) return;
            string nName = node.Name.ToString();
            string lower = nName.ToLowerInvariant();
            if (nName == "SkeletonGizmoManager" || nName == "BoneLayerManager" || nName == "CharacterIKManager" ||
                lower.Contains("gizmo") || lower.Contains("picker") || lower.Contains("tipvisual") || lower.Contains("polevisual") ||
                nName.StartsWith("@") || nName.Contains("Custom_Rigged_Character") || nName.StartsWith("Custom_Accessory_") || nName == "Custom_Rigid_Accessory")
            {
                return;
            }

            if (node is MeshInstance3D mi && mi.Mesh != null)
            {
                // Skip generated submesh clones (_surf_*) when parent composite mesh is present
                if (mi.HasMeta("ParentCompositeMesh"))
                {
                    var parentComp = mi.GetMeta("ParentCompositeMesh").As<MeshInstance3D>();
                    if (parentComp != null && GodotObject.IsInstanceValid(parentComp))
                    {
                        return;
                    }
                }

                if (!mi.HasMeta("IsCustomImportedMesh") &&
                    DeadlockPlayground.Painter.HeroMeshHierarchy.IsAuthenticHeroMesh(mi))
                {
                    list.Add(mi);
                }
            }

            foreach (Node child in node.GetChildren())
            {
                CollectAuthenticHeroMeshesRecursive(child, list);
            }
        }

        /// <summary>
        /// Resolves a Godot Skin bind slot index into the corresponding Skeleton3D bone index.
        /// Falls back safely when Skin.GetBindBone returns -1 (name-bound skins).
        /// </summary>
        private static int ResolveSkinBindBone(Skin skin, int bindIdx, Skeleton3D skel)
        {
            if (skel == null) return 0;
            int boneCount = skel.GetBoneCount();
            if (boneCount <= 0) return 0;
            if (skin == null || bindIdx < 0 || bindIdx >= skin.GetBindCount())
            {
                return Math.Clamp(bindIdx, 0, boneCount - 1);
            }

            int mapped = skin.GetBindBone(bindIdx);
            if (mapped >= 0 && mapped < boneCount) return mapped;

            string bName = skin.GetBindName(bindIdx);
            if (!string.IsNullOrEmpty(bName))
            {
                int found = skel.FindBone(bName);
                if (found >= 0) return found;
            }
            return Math.Clamp(bindIdx, 0, boneCount - 1);
        }

        /// <summary>
        /// Evaluates rest-pose vertices and normals for a MeshInstance3D surface into root/hero space.
        /// Accurately applies Godot Skin Inverse Bind Poses * Skeleton3D GlobalRest * SkeletonRelTransform
        /// when the mesh is skinned, avoiding double-rotation/collapse on glTFs whose Skeleton3D carries an up-axis correction.
        /// </summary>
        public static void EvaluateMeshSurfaceInTargetSpace(
            MeshInstance3D mi,
            Node3D sourceRoot,
            Transform3D prefixTransform,
            Skeleton3D fallbackSkeleton,
            Vector3[] rawVerts,
            Vector3[] rawNorms,
            int[] rawBones,
            float[] rawWeights,
            out Vector3[] outVerts,
            out Vector3[] outNorms,
            bool useExplicitSkeleton = false)
        {
            int vCount = rawVerts.Length;
            outVerts = new Vector3[vCount];
            outNorms = new Vector3[vCount];

            Transform3D fallbackXform = ComputeImportedMeshToHeroTransform(mi, sourceRoot, prefixTransform);

            Skeleton3D skel = null;
            if (useExplicitSkeleton && fallbackSkeleton != null)
            {
                skel = fallbackSkeleton;
            }
            else
            {
                if (!string.IsNullOrEmpty(mi.Skeleton.ToString()))
                {
                    skel = mi.GetNodeOrNull<Skeleton3D>(mi.Skeleton);
                }
                if (skel == null && mi.GetParent() is Skeleton3D parentSkel) skel = parentSkel;
                if (skel == null) skel = fallbackSkeleton;
            }

            int stride = (rawBones != null && rawWeights != null && vCount > 0 && rawBones.Length >= vCount * 8 && rawWeights.Length >= vCount * 8) ? 8 : 4;
            bool useSkinning = skel != null && mi.Skin != null && mi.Skin.GetBindCount() > 0 &&
                               rawBones != null && rawWeights != null &&
                               rawBones.Length >= vCount * stride && rawWeights.Length >= vCount * stride;

            if (!useSkinning)
            {
                Basis normalXform = MathF.Abs(fallbackXform.Basis.Determinant()) > 1e-8f
                    ? fallbackXform.Basis.Inverse().Transposed()
                    : fallbackXform.Basis;
                for (int v = 0; v < vCount; v++)
                {
                    outVerts[v] = fallbackXform * rawVerts[v];
                    outNorms[v] = (rawNorms != null && rawNorms.Length == vCount)
                        ? (normalXform * rawNorms[v]).Normalized()
                        : Vector3.Up;
                }
                return;
            }

            Transform3D skelToHero = prefixTransform * ComputeTransformRelativeToRoot(skel, sourceRoot);
            int bindCount = mi.Skin.GetBindCount();
            int skelBoneCount = skel.GetBoneCount();
            var bindToHero = new Transform3D[bindCount];
            var bindNormToHero = new Basis[bindCount];
            for (int bi = 0; bi < bindCount; bi++)
            {
                int boneIdx = ResolveSkinBindBone(mi.Skin, bi, skel);
                Transform3D gRest = (boneIdx >= 0 && boneIdx < skelBoneCount) ? skel.GetBoneGlobalRest(boneIdx) : Transform3D.Identity;
                Transform3D bt = skelToHero * gRest * mi.Skin.GetBindPose(bi);
                bindToHero[bi] = bt;
                bindNormToHero[bi] = MathF.Abs(bt.Basis.Determinant()) > 1e-8f
                    ? bt.Basis.Inverse().Transposed()
                    : bt.Basis;
            }

            Vector3[] vertsOut = outVerts;
            Vector3[] normsOut = outNorms;
            bool hasNorms = rawNorms != null && rawNorms.Length == vCount;

            Action<int> evalVertex = v =>
            {
                Vector3 rv = rawVerts[v];
                Vector3 rn = hasNorms ? rawNorms[v] : Vector3.Up;
                Vector3 accPos = Vector3.Zero;
                Vector3 accNorm = Vector3.Zero;
                float totalW = 0f;

                int baseOff = v * stride;
                for (int k = 0; k < stride; k++)
                {
                    float w = rawWeights[baseOff + k];
                    if (w <= 0.0001f) continue;
                    int bi = rawBones[baseOff + k];
                    if (bi < 0 || bi >= bindCount) continue;

                    Transform3D bt = bindToHero[bi];
                    accPos += (bt * rv) * w;
                    accNorm += (bindNormToHero[bi] * rn) * w;
                    totalW += w;
                }

                if (totalW > 0.0001f)
                {
                    float invW = 1.0f / totalW;
                    vertsOut[v] = accPos * invW;
                    normsOut[v] = accNorm.LengthSquared() > 0.00001f ? accNorm.Normalized() : Vector3.Up;
                }
                else
                {
                    vertsOut[v] = fallbackXform * rv;
                    normsOut[v] = hasNorms ? (fallbackXform.Basis * rn).Normalized() : Vector3.Up;
                }
            };

            if (vCount >= 512)
                System.Threading.Tasks.Parallel.For(0, vCount, evalVertex);
            else
                for (int v = 0; v < vCount; v++) evalVertex(v);
        }

        /// <summary>
        /// Computes pristine model vertex bounding box relative to its own root node (at scale 1.0, offset 0),
        /// accurately evaluating skinning bind transforms when present.
        /// Immune to current viewport slider or container transforms!
        /// When <paramref name="includedMeshNames"/> is non-null, only submeshes whose node names appear in the set are measured.
        /// </summary>
        public static (Vector3 Min, Vector3 Max, bool Valid) ComputePristineModelBounds(
            Node3D root,
            IReadOnlyCollection<string> includedMeshNames = null)
        {
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            bool found = false;

            HashSet<string> includedSet = includedMeshNames != null
                ? new HashSet<string>(includedMeshNames, StringComparer.OrdinalIgnoreCase)
                : null;

            Skeleton3D fallbackSkel = FindSkeleton(root);
            var meshes = CollectMeshes(root);
            foreach (var mi in meshes)
            {
                if (mi.Mesh == null) continue;
                if (mi.HasMeta("IsHeroSpaceDonorMesh") && mi.GetMeta("IsHeroSpaceDonorMesh").AsBool()) continue;
                if (includedSet != null && !includedSet.Contains(mi.Name.ToString())) continue;

                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                {
                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.Vertex) continue;
                    var rawVerts = GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    if (rawVerts == null || rawVerts.Length == 0) continue;
                    var rawBones = arrays.Count > (int)Mesh.ArrayType.Bones ? GetInt32Array(arrays[(int)Mesh.ArrayType.Bones]) : null;
                    var rawWeights = arrays.Count > (int)Mesh.ArrayType.Weights ? GetFloat32Array(arrays[(int)Mesh.ArrayType.Weights]) : null;

                    EvaluateMeshSurfaceInTargetSpace(mi, root, Transform3D.Identity, fallbackSkel, rawVerts, null, rawBones, rawWeights, out var verts, out _);

                    found = true;
                    for (int i = 0; i < verts.Length; i++)
                    {
                        Vector3 w = verts[i];
                        if (w.X < min.X) min.X = w.X;
                        if (w.Y < min.Y) min.Y = w.Y;
                        if (w.Z < min.Z) min.Z = w.Z;
                        if (w.X > max.X) max.X = w.X;
                        if (w.Y > max.Y) max.Y = w.Y;
                        if (w.Z > max.Z) max.Z = w.Z;
                    }
                }
            }

            if (!found) return (Vector3.Zero, new Vector3(1, 1.8f, 1), false);
            return (min, max, true);
        }

        /// <summary>
        /// Computes authentic Deadlock hero vertex bounding box relative to hero root.
        /// Strictly filters out gizmo meshes, handles, accessories, and translucent smoke/FX plumes.
        /// </summary>
        public static (Vector3 Min, Vector3 Max, bool Valid) ComputeAuthenticHeroBounds(Node3D root)
        {
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            bool found = false;

            var meshes = CollectOriginalHeroMeshes(root);
            if (meshes.Count == 0)
            {
                var allMeshes = CollectMeshes(root);
                foreach (var m in allMeshes)
                {
                    string nm = m.Name.ToString().ToLowerInvariant();
                    if (nm.Contains("picker") || nm.Contains("gizmo") || nm.Contains("handle") || nm.Contains("visual") || m.HasMeta("IsCustomImportedMesh"))
                        continue;
                    meshes.Add(m);
                }
            }

            Skeleton3D fallbackSkel = FindSkeleton(root);
            foreach (var mi in meshes)
            {
                if (mi.Mesh == null) continue;
                string lowerName = mi.Name.ToString().ToLowerInvariant();
                bool isWeaponOrProp = lowerName.Contains("gun") || lowerName.Contains("weapon");

                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                {
                    Material surfMat = mi.GetActiveMaterial(s) ?? mi.Mesh.SurfaceGetMaterial(s);
                    string matIdent = ((surfMat?.ResourceName ?? "") + " " + (surfMat?.ResourcePath ?? "")).ToLowerInvariant();
                    if (matIdent.Contains("headsmoke") || matIdent.Contains("_smoke") || matIdent.Contains("vfx"))
                        continue;

                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.Vertex) continue;
                    var rawVerts = GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    if (rawVerts == null || rawVerts.Length == 0) continue;
                    var rawBones = arrays.Count > (int)Mesh.ArrayType.Bones ? GetInt32Array(arrays[(int)Mesh.ArrayType.Bones]) : null;
                    var rawWeights = arrays.Count > (int)Mesh.ArrayType.Weights ? GetFloat32Array(arrays[(int)Mesh.ArrayType.Weights]) : null;

                    EvaluateMeshSurfaceInTargetSpace(mi, root, Transform3D.Identity, fallbackSkel, rawVerts, null, rawBones, rawWeights, out var verts, out _);

                    found = true;
                    for (int i = 0; i < verts.Length; i++)
                    {
                        Vector3 w = verts[i];
                        if (w.X < min.X) min.X = w.X;
                        if (w.Z < min.Z) min.Z = w.Z;
                        if (w.X > max.X) max.X = w.X;
                        if (w.Z > max.Z) max.Z = w.Z;
                        if (w.Y > max.Y) max.Y = w.Y;

                        // For sole/floor level (min.Y), ignore downward-pointing weapon tips dipping below ground (Y < -0.05)
                        if (isWeaponOrProp && w.Y < -0.05f) continue;
                        if (w.Y < min.Y) min.Y = w.Y;
                    }
                }
            }

            if (!found) return (new Vector3(-0.4f, 0, -0.2f), new Vector3(0.4f, 2.45f, 0.2f), false);
            return (min, max, true);
        }

        public static (Vector3 Min, Vector3 Max, bool Valid) ComputeWorldVertexBounds(Node3D root)
        {
            return ComputePristineModelBounds(root);
        }

    }
}
