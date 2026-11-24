using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace DeadlockPlayground.Importer
{
    /// <summary>
    /// Phase 2 "Add-clothing-bones tool": generates synthetic bone chains anchored to a hero bone
    /// and computes smooth geodesic/capsule weights over vertices inside the target mesh/region.
    /// </summary>
    public static class AddClothingChainTool
    {
        public sealed class PreparedSyntheticChain
        {
            public BoneChainDef Chain;
            public Vector3[] SegPoints;
            public int[] ChainBoneIndices;
            public int AnchorBoneIdx;
            public float Radius;
            public bool ProtectUnrelated;
            public string AnchorLow;
            public Vector3 RootSegDir;
            public float RootSegLen;
        }

        public static string SanitizeBoneBasePublic(string raw) => SanitizeBoneBase(raw);

        /// <summary>
        /// Stage 1: Appends all active synthetic cloth/hair bones onto <paramref name="targetSkeleton"/>
        /// and registers their inverse-bind matrices in <paramref name="customSkin"/>.
        /// Must run BEFORE mesh rigging so hand-painted <c>CustomSkinWeightOverrides</c> referencing
        /// synthetic bones resolve cleanly instead of falling back to bone 0 (root_motion).
        /// </summary>
        public static List<PreparedSyntheticChain> CreateSyntheticChainBones(
            Skeleton3D targetSkeleton,
            Skin customSkin,
            IEnumerable<BoneChainDef> syntheticChains,
            out int totalAddedBones)
        {
            totalAddedBones = 0;
            var preparedChains = new List<PreparedSyntheticChain>();
            if (targetSkeleton == null || customSkin == null || syntheticChains == null)
                return preparedChains;

            var activeSynthetic = syntheticChains
                .Where(c => c != null && c.IsSynthetic && c.Policy == ChainPolicy.PreservePhysicsChain)
                .ToList();
            if (activeSynthetic.Count == 0) return preparedChains;

            foreach (var chain in activeSynthetic)
            {
                chain.AppendedHeroBoneNames.Clear();
                int anchorIdx = targetSkeleton.FindBone(string.IsNullOrWhiteSpace(chain.AnchorHeroBone) ? "spine_3" : chain.AnchorHeroBone);
                if (anchorIdx < 0) anchorIdx = targetSkeleton.FindBone("pelvis");
                if (anchorIdx < 0) anchorIdx = 0;

                Vector3 dir = chain.SyntheticDirectionHeroSpace.LengthSquared() > 1e-5f
                    ? chain.SyntheticDirectionHeroSpace.Normalized()
                    : new Vector3(0f, -1f, 0f);
                bool hasCustomJoints = chain.SyntheticJointPositionsHeroSpace != null && chain.SyntheticJointPositionsHeroSpace.Count >= 2;
                int segCount = hasCustomJoints
                    ? Math.Clamp(chain.SyntheticJointPositionsHeroSpace.Count - 1, 2, 12)
                    : Math.Clamp(chain.SyntheticSegmentCount, 2, 12);
                float totalLen = Math.Clamp(chain.SyntheticLengthMeters, 0.08f, 2.5f);
                float stepLen = totalLen / segCount;

                Vector3 startWorld = chain.SyntheticOriginHeroSpace.LengthSquared() > 1e-5f
                    ? chain.SyntheticOriginHeroSpace
                    : targetSkeleton.GetBoneGlobalRest(anchorIdx).Origin;

                var segPoints = new Vector3[segCount + 1];
                if (hasCustomJoints)
                {
                    for (int i = 0; i <= segCount; i++)
                    {
                        int srcIdx = Math.Min(i, chain.SyntheticJointPositionsHeroSpace.Count - 1);
                        segPoints[i] = chain.SyntheticJointPositionsHeroSpace[srcIdx];
                    }
                }
                else
                {
                    for (int i = 0; i <= segCount; i++)
                        segPoints[i] = startWorld + dir * (stepLen * i);
                }

                var chainBoneIndices = new int[segCount];
                int parentIdx = anchorIdx;

                string safeBase = SanitizeBoneBase(string.IsNullOrWhiteSpace(chain.Name) ? "cloth_chain" : chain.Name);
                for (int i = 0; i < segCount; i++)
                {
                    string candidateName = $"{safeBase}_{i}";
                    string uniqueName = DeduplicateBoneName(targetSkeleton, candidateName);

                    int boneIdx = targetSkeleton.GetBoneCount();
                    targetSkeleton.AddBone(uniqueName);
                    targetSkeleton.SetBoneParent(boneIdx, parentIdx);

                    Transform3D parentGlobalRest = parentIdx >= 0 ? targetSkeleton.GetBoneGlobalRest(parentIdx) : Transform3D.Identity;
                    Vector3 localPos = parentGlobalRest.AffineInverse() * segPoints[i];
                    Vector3 segDirWorld = (segPoints[i + 1] - segPoints[i]);
                    Basis boneWorldBasis = segDirWorld.LengthSquared() > 1e-6f
                        ? BuildSegmentBasis(segDirWorld.Normalized())
                        : Basis.Identity;
                    Basis localBasis = (parentGlobalRest.Basis.Orthonormalized().Inverse() * boneWorldBasis).Orthonormalized();
                    targetSkeleton.SetBoneRest(boneIdx, new Transform3D(localBasis, localPos));
                    targetSkeleton.SetBonePosePosition(boneIdx, localPos);
                    targetSkeleton.SetBonePoseRotation(boneIdx, localBasis.GetRotationQuaternion());
                    targetSkeleton.SetBonePoseScale(boneIdx, Vector3.One);

                    CustomModelAutoRigger.GetOrAddSkinBindIndex(customSkin, targetSkeleton, boneIdx);
                    chain.AppendedHeroBoneNames.Add(uniqueName);
                    chainBoneIndices[i] = boneIdx;
                    parentIdx = boneIdx;
                    totalAddedBones++;
                }

                float radius = Math.Clamp(chain.SyntheticInfluenceRadius, 0.04f, 1.5f);
                bool protectUnrelated = chain.SyntheticProtectCoreLimbs && string.IsNullOrWhiteSpace(chain.TargetMeshName);
                string anchorLow = (chain.AnchorHeroBone ?? "spine_3").ToLowerInvariant();
                Vector3 rootSegDir = (segPoints[1] - segPoints[0]).LengthSquared() > 1e-6f
                    ? (segPoints[1] - segPoints[0]).Normalized()
                    : dir;
                float rootSegLen = MathF.Max(0.03f, segPoints[0].DistanceTo(segPoints[1]));

                preparedChains.Add(new PreparedSyntheticChain
                {
                    Chain = chain,
                    SegPoints = segPoints,
                    ChainBoneIndices = chainBoneIndices,
                    AnchorBoneIdx = anchorIdx,
                    Radius = radius,
                    ProtectUnrelated = protectUnrelated,
                    AnchorLow = anchorLow,
                    RootSegDir = rootSegDir,
                    RootSegLen = rootSegLen
                });
            }

            return preparedChains;
        }

        public static int ApplySyntheticChains(
            Skeleton3D targetSkeleton,
            Skin customSkin,
            List<MeshInstance3D> riggedMeshes,
            IEnumerable<BoneChainDef> syntheticChains,
            List<string> warnings = null,
            Dictionary<string, CustomModelAutoRigger.SkinWeightOverride> customSkinOverrides = null)
        {
            var preparedChains = CreateSyntheticChainBones(targetSkeleton, customSkin, syntheticChains, out int totalAddedBones);
            if (preparedChains.Count == 0) return 0;
            ApplySyntheticChainWeights(targetSkeleton, customSkin, riggedMeshes, preparedChains, customSkinOverrides, warnings);
            return totalAddedBones;
        }

        /// <summary>
        /// Stage 2: Blends distance-based weights for prepared synthetic chains onto rigged submeshes,
        /// skipping chains whose bone weights are already authored in <paramref name="customSkinOverrides"/>
        /// for that surface so manual weight painting is never overwritten on re-bind.
        /// </summary>
        public static void ApplySyntheticChainWeights(
            Skeleton3D targetSkeleton,
            Skin customSkin,
            List<MeshInstance3D> riggedMeshes,
            List<PreparedSyntheticChain> preparedChains,
            Dictionary<string, CustomModelAutoRigger.SkinWeightOverride> customSkinOverrides = null,
            List<string> warnings = null,
            HashSet<string> explicitRebindChainIds = null)
        {
            if (targetSkeleton == null || customSkin == null || riggedMeshes == null || preparedChains == null || preparedChains.Count == 0)
                return;

            // Collect every synthetic bone index across all prepared chains so we can protect
            // already-painted sibling chains from weight theft when re-binding a single dirty chain.
            var allSyntheticBoneIndices = new HashSet<int>();
            foreach (var pc in preparedChains)
                if (pc?.ChainBoneIndices != null)
                    foreach (int bi in pc.ChainBoneIndices) allSyntheticBoneIndices.Add(bi);

            // Pass 2: Simultaneously blend all overlapping synthetic chains onto each submesh vertex (no order-dependent weight theft!).
            var (_, bindToSkel) = CustomModelAutoRigger.BuildSkinIndexMappings(targetSkeleton, customSkin);
            var synAccum = new Dictionary<int, float>();
            var merged = new Dictionary<int, float>();

            foreach (var mi in riggedMeshes)
            {
                if (mi?.Mesh is not ArrayMesh arrMesh) continue;
                bool isSocketProp = mi.HasMeta("IsSocketProp") && mi.GetMeta("IsSocketProp").AsBool();
                string miName = mi.Name.ToString();

                // Check if this submesh has a user-painted skin override.
                bool surfaceHasOverride = false;
                HashSet<string> paintedBonesOnSurface = null;
                if (customSkinOverrides != null && customSkinOverrides.Count > 0 &&
                    mi.HasMeta("SourceMeshName") && mi.HasMeta("SourceSurfaceIndex"))
                {
                    string srcMeshName = mi.GetMeta("SourceMeshName").AsString();
                    int srcSurfIdx = mi.GetMeta("SourceSurfaceIndex").AsInt32();
                    string keepKey = CustomModelAutoRigger.MakeSurfaceKeepKey(srcMeshName, srcSurfIdx);
                    if (customSkinOverrides.TryGetValue(keepKey, out var ov) &&
                        ov?.BonePalette != null && ov.PaletteIndices != null && ov.Weights != null)
                    {
                        surfaceHasOverride = true;
                        var nonZeroPalette = new bool[ov.BonePalette.Length];
                        int n = Math.Min(ov.PaletteIndices.Length, ov.Weights.Length);
                        for (int idx = 0; idx < n; idx++)
                        {
                            if (ov.Weights[idx] > 1e-4f)
                            {
                                int pIdx = ov.PaletteIndices[idx];
                                if (pIdx >= 0 && pIdx < nonZeroPalette.Length) nonZeroPalette[pIdx] = true;
                            }
                        }
                        paintedBonesOnSurface = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        for (int p = 0; p < nonZeroPalette.Length; p++)
                        {
                            if (nonZeroPalette[p] && !string.IsNullOrEmpty(ov.BonePalette[p]))
                                paintedBonesOnSurface.Add(ov.BonePalette[p]);
                        }
                    }
                }

                var applicable = preparedChains
                    .Where(pc =>
                    {
                        bool meshMatch = (!isSocketProp && string.IsNullOrWhiteSpace(pc.Chain.TargetMeshName)) ||
                                         (!string.IsNullOrWhiteSpace(pc.Chain.TargetMeshName) &&
                                          miName.Contains(pc.Chain.TargetMeshName, StringComparison.OrdinalIgnoreCase));
                        if (!meshMatch) return false;
                        if (surfaceHasOverride)
                        {
                            // When the UI supplies an explicit set of dirty/newly-added ChainIds, ONLY those chains
                            // run procedural weighting on painted surfaces (so intentionally erased chains stay erased
                            // and sibling painted chains are never overwritten!).
                            if (explicitRebindChainIds != null)
                            {
                                string cid = pc.Chain.ChainId ?? pc.Chain.Name ?? "";
                                return !string.IsNullOrEmpty(cid) && explicitRebindChainIds.Contains(cid);
                            }
                            if (paintedBonesOnSurface != null &&
                                pc.Chain.AppendedHeroBoneNames.Any(bn => paintedBonesOnSurface.Contains(bn)))
                            {
                                return false;
                            }
                        }
                        return true;
                    })
                    .ToList();
                if (applicable.Count == 0) continue;

                // Bones belonging to the chains currently being bound on this surface vs protected sibling chains
                var activeChainBones = new HashSet<int>();
                var activeAnchorBones = new HashSet<int>();
                foreach (var pc in applicable)
                {
                    if (pc.AnchorBoneIdx >= 0) activeAnchorBones.Add(pc.AnchorBoneIdx);
                    foreach (int bi in pc.ChainBoneIndices) activeChainBones.Add(bi);
                }

                bool meshModified = false;
                var rebuilt = new ArrayMesh();
                for (int s = 0; s < arrMesh.GetSurfaceCount(); s++)
                {
                    var arrays = arrMesh.SurfaceGetArrays(s);
                    var origFmt = arrMesh.SurfaceGetFormat(s);
                    var verts = CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    var bones = CustomModelAutoRigger.GetInt32Array(arrays[(int)Mesh.ArrayType.Bones]);
                    var weights = CustomModelAutoRigger.GetFloat32Array(arrays[(int)Mesh.ArrayType.Weights]);
                    if (verts == null || bones == null || weights == null)
                    {
                        Mesh.ArrayFormat keepFlags = (origFmt & Mesh.ArrayFormat.FlagUse8BoneWeights) != 0
                            ? Mesh.ArrayFormat.FlagUse8BoneWeights
                            : default;
                        rebuilt.AddSurfaceFromArrays(arrMesh.SurfaceGetPrimitiveType(s), arrays, null, null, keepFlags);
                        var keepMat = arrMesh.SurfaceGetMaterial(s);
                        if (keepMat != null) rebuilt.SurfaceSetMaterial(rebuilt.GetSurfaceCount() - 1, keepMat);
                        rebuilt.SurfaceSetName(rebuilt.GetSurfaceCount() - 1, arrMesh.SurfaceGetName(s));
                        continue;
                    }

                    int stride = Math.Max(4, bones.Length / verts.Length);
                    if (stride != 4 && stride != 8) stride = 4;
                    Mesh.ArrayFormat buildFlags = stride == 8 ? Mesh.ArrayFormat.FlagUse8BoneWeights : default;

                    for (int v = 0; v < verts.Length; v++)
                    {
                        Vector3 p = verts[v];
                        synAccum.Clear();
                        float maxChainBlend = 0f;
                        float totalSynScore = 0f;

                        foreach (var pc in applicable)
                        {
                            int segCount = pc.ChainBoneIndices.Length;
                            float minDist = float.MaxValue;
                            float bestArcLen = 0f;
                            float runningArc = 0f;
                            float segSum = 0f;

                            // Proximity + polyline arc-length check (works cleanly even on curved/bent custom chains!)
                            for (int i = 0; i < segCount; i++)
                            {
                                Vector3 a = pc.SegPoints[i];
                                Vector3 b = pc.SegPoints[i + 1];
                                Vector3 ab = b - a;
                                float segLenSq = ab.LengthSquared();
                                float segLen = segLenSq > 1e-8f ? MathF.Sqrt(segLenSq) : 0f;
                                float tSeg = segLenSq > 1e-8f ? Mathf.Clamp((p - a).Dot(ab) / segLenSq, 0f, 1f) : 0f;
                                float d = p.DistanceTo(a + ab * tSeg);
                                if (d < minDist)
                                {
                                    minDist = d;
                                    bestArcLen = runningArc + segLen * tSeg;
                                }
                                runningArc += segLen;
                            }
                            if (minDist >= pc.Radius) continue;

                            if (pc.ProtectUnrelated && IsDominantBoneUnrelatedToAnchor(targetSkeleton, bindToSkel, bones, weights, v, stride, pc.AnchorLow))
                                continue;

                            float rootRamp;
                            if (bestArcLen <= 1e-4f)
                            {
                                float alongRoot = (p - pc.SegPoints[0]).Dot(pc.RootSegDir);
                                rootRamp = Mathf.Clamp(1.0f + alongRoot / (pc.Radius * 0.65f), 0.0f, 0.35f);
                            }
                            else
                            {
                                rootRamp = Mathf.Clamp(0.35f + 0.65f * (bestArcLen / pc.RootSegLen), 0.35f, 1.0f);
                            }
                            if (rootRamp <= 0.02f) continue;

                            // C1-smooth cubic smoothstep falloff (zero hard crease jump at radius boundary)
                            float u = Mathf.Clamp(1.0f - (minDist / pc.Radius), 0f, 1f);
                            float smoothU = u * u * (3.0f - 2.0f * u);
                            float chainBlend = Mathf.Clamp(smoothU * rootRamp, 0.0f, 0.92f);
                            if (chainBlend <= 0.005f) continue;
                            if (chainBlend > maxChainBlend) maxChainBlend = chainBlend;

                            for (int i = 0; i < segCount; i++)
                            {
                                float d = DistancePointToSegment(p, pc.SegPoints[i], pc.SegPoints[i + 1]);
                                if (d < pc.Radius)
                                {
                                    float falloff = 1.0f - (d / pc.Radius);
                                    float w = falloff * falloff * rootRamp;
                                    if (w > 1e-5f)
                                    {
                                        int bIdx = pc.ChainBoneIndices[i];
                                        synAccum[bIdx] = synAccum.GetValueOrDefault(bIdx) + w;
                                        segSum += w;
                                    }
                                }
                            }
                            totalSynScore += segSum;
                        }

                        if (totalSynScore <= 1e-6f || maxChainBlend <= 1e-5f) continue;

                        merged.Clear();
                        if (surfaceHasOverride)
                        {
                            // Protect already-painted sibling synthetic chains from dilution/eviction:
                            // borrow weight strictly from anchor/non-sibling bones first (e.g. head).
                            float protectedSum = 0f;
                            float borrowableSum = 0f;
                            for (int k = 0; k < stride; k++)
                            {
                                float w = weights[v * stride + k];
                                if (w <= 1e-5f) continue;
                                int bind = bones[v * stride + k];
                                int skel = bindToSkel.TryGetValue(bind, out int si) ? si : bind;
                                if (allSyntheticBoneIndices.Contains(skel) && !activeChainBones.Contains(skel))
                                {
                                    merged[skel] = merged.GetValueOrDefault(skel) + w;
                                    protectedSum += w;
                                }
                                else
                                {
                                    borrowableSum += w;
                                }
                            }

                            // If this vertex is almost entirely owned by another painted chain, leave it untouched.
                            float availableBlend = MathF.Min(maxChainBlend, borrowableSum);
                            if (availableBlend <= 0.005f || protectedSum >= 0.92f) continue;
                            meshModified = true;

                            float keepBorrowScale = borrowableSum > 1e-5f ? MathF.Max(0f, borrowableSum - availableBlend) / borrowableSum : 0f;
                            for (int k = 0; k < stride; k++)
                            {
                                float w = weights[v * stride + k];
                                if (w <= 1e-5f) continue;
                                int bind = bones[v * stride + k];
                                int skel = bindToSkel.TryGetValue(bind, out int si) ? si : bind;
                                if (allSyntheticBoneIndices.Contains(skel) && !activeChainBones.Contains(skel)) continue;
                                float kept = w * keepBorrowScale;
                                if (kept > 1e-5f) merged[skel] = merged.GetValueOrDefault(skel) + kept;
                            }
                            foreach (var kv in synAccum)
                            {
                                merged[kv.Key] = merged.GetValueOrDefault(kv.Key) + (kv.Value / totalSynScore) * availableBlend;
                            }
                        }
                        else
                        {
                            meshModified = true;
                            for (int k = 0; k < stride; k++)
                            {
                                float w = weights[v * stride + k];
                                if (w <= 1e-5f) continue;
                                int bind = bones[v * stride + k];
                                int skel = bindToSkel.TryGetValue(bind, out int si) ? si : bind;
                                merged[skel] = merged.GetValueOrDefault(skel) + w * (1.0f - maxChainBlend);
                            }
                            foreach (var kv in synAccum)
                            {
                                merged[kv.Key] = merged.GetValueOrDefault(kv.Key) + (kv.Value / totalSynScore) * maxChainBlend;
                            }
                        }

                        var top = merged.OrderByDescending(kv => kv.Value).Take(stride).ToList();
                        float sum = top.Sum(kv => kv.Value);
                        for (int k = 0; k < stride; k++)
                        {
                            if (k < top.Count && sum > 1e-6f)
                            {
                                int bIdx = CustomModelAutoRigger.GetOrAddSkinBindIndex(customSkin, targetSkeleton, top[k].Key);
                                bones[v * stride + k] = bIdx;
                                weights[v * stride + k] = top[k].Value / sum;
                            }
                            else
                            {
                                bones[v * stride + k] = 0;
                                weights[v * stride + k] = 0f;
                            }
                        }
                    }

                    arrays[(int)Mesh.ArrayType.Bones] = bones;
                    arrays[(int)Mesh.ArrayType.Weights] = weights;
                    rebuilt.AddSurfaceFromArrays(arrMesh.SurfaceGetPrimitiveType(s), arrays, null, null, buildFlags);
                    var mat = arrMesh.SurfaceGetMaterial(s);
                    if (mat != null) rebuilt.SurfaceSetMaterial(rebuilt.GetSurfaceCount() - 1, mat);
                    rebuilt.SurfaceSetName(rebuilt.GetSurfaceCount() - 1, arrMesh.SurfaceGetName(s));
                }

                if (!meshModified) continue;

                mi.Mesh = rebuilt;
                mi.Skin = customSkin;
                // Force Godot's RenderingServer SkinReference to rebuild with newly appended synthetic bones!
                var skelPath = mi.Skeleton;
                if (skelPath != null && !skelPath.IsEmpty)
                {
                    mi.Skeleton = new NodePath();
                    mi.Skeleton = skelPath;
                }
            }

            if (targetSkeleton.GetBoneCount() > 256)
            {
                warnings?.Add($"Total hero skeleton bone count ({targetSkeleton.GetBoneCount()}) is nearing Source 2 limits after adding synthetic chains.");
            }
        }

        private static string SanitizeBoneBase(string raw)
        {
            var chars = raw.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
            string s = new string(chars).Trim('_');
            return string.IsNullOrEmpty(s) ? "cloth_chain" : s;
        }

        private static string DeduplicateBoneName(Skeleton3D skel, string baseName)
        {
            if (skel.FindBone(baseName) < 0) return baseName;
            for (int n = 1; n < 1000; n++)
            {
                string cand = $"{baseName}_ext{n}";
                if (skel.FindBone(cand) < 0) return cand;
            }
            return $"{baseName}_{Guid.NewGuid().ToString("N")[..4]}";
        }

        private static Basis BuildSegmentBasis(Vector3 segDir)
        {
            Vector3 yAxis = segDir.Normalized();
            Vector3 refUp = MathF.Abs(yAxis.Dot(Vector3.Forward)) < 0.92f ? Vector3.Forward : Vector3.Right;
            Vector3 xAxis = refUp.Cross(yAxis).Normalized();
            Vector3 zAxis = xAxis.Cross(yAxis).Normalized();
            return new Basis(xAxis, yAxis, zAxis).Orthonormalized();
        }

        private static bool IsDominantBoneUnrelatedToAnchor(
            Skeleton3D skeleton,
            Dictionary<int, int> bindToSkel,
            int[] bones,
            float[] weights,
            int vertexIdx,
            int stride,
            string anchorLow)
        {
            int bestSkel = -1;
            float bestW = 0f;
            for (int k = 0; k < stride; k++)
            {
                float w = weights[vertexIdx * stride + k];
                if (w > bestW)
                {
                    bestW = w;
                    int bind = bones[vertexIdx * stride + k];
                    bestSkel = bindToSkel.TryGetValue(bind, out int si) ? si : bind;
                }
            }
            if (bestSkel < 0 || bestSkel >= skeleton.GetBoneCount()) return false;
            string bLow = skeleton.GetBoneName(bestSkel).ToLowerInvariant();

            if (anchorLow.Contains("head") || anchorLow.Contains("neck"))
            {
                // Head/hair chain should not hijack arms, hands, pelvis, or legs
                return bLow.Contains("arm") || bLow.Contains("hand") || bLow.Contains("finger") ||
                       bLow.Contains("pelvis") || bLow.Contains("leg") || bLow.Contains("ankle") || bLow.Contains("ball");
            }
            if (anchorLow.Contains("spine"))
            {
                // Torso/cape chain should not hijack hands/fingers or lower legs/feet
                return bLow.Contains("hand") || bLow.Contains("finger") || bLow.Contains("ankle") || bLow.Contains("ball");
            }
            if (anchorLow.Contains("pelvis"))
            {
                // Skirt/hip chain should not hijack arms, hands, or head
                return bLow.Contains("arm") || bLow.Contains("hand") || bLow.Contains("finger") || bLow.Contains("head") || bLow.Contains("neck");
            }
            return false;
        }

        private static float DistancePointToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float lenSq = ab.LengthSquared();
            if (lenSq < 1e-8f) return p.DistanceTo(a);
            float t = Mathf.Clamp((p - a).Dot(ab) / lenSq, 0f, 1f);
            return p.DistanceTo(a + ab * t);
        }
    }
}

