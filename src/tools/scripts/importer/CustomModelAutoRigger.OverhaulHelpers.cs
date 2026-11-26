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
        #region Overhaul Phases 0, 2, 3 Helpers (Chains, KeepOwnBones, Remap, Diagnostics)

        private static string DeduplicateHeroBoneName(Skeleton3D skeleton, string rawName)
        {
            string clean = string.IsNullOrWhiteSpace(rawName) ? "extra_bone" : rawName.Trim();
            if (skeleton.FindBone(clean) < 0) return clean;
            for (int n = 1; n < 1000; n++)
            {
                string cand = $"{clean}_ext{n}";
                if (skeleton.FindBone(cand) < 0) return cand;
            }
            return $"{clean}_{Guid.NewGuid().ToString("N")[..4]}";
        }

        public static void TagAppendedBonesOnSkeleton(Skeleton3D skeleton, string slotKey, string chainId, IEnumerable<string> boneNames)
        {
            if (skeleton == null || !GodotObject.IsInstanceValid(skeleton) || boneNames == null) return;
            if (!skeleton.HasMeta("OriginalHeroBoneCount"))
            {
                // Best-effort snapshot if not already recorded before appending
                int orig = skeleton.GetBoneCount() - boneNames.Count();
                if (orig >= 0) skeleton.SetMeta("OriginalHeroBoneCount", orig);
            }

            string normSlot = string.IsNullOrWhiteSpace(slotKey) ? "__main__" : slotKey;
            var existing = skeleton.HasMeta("CustomAppendedBoneEntries")
                ? skeleton.GetMeta("CustomAppendedBoneEntries").AsStringArray().ToList()
                : new List<string>();

            var known = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
            foreach (string b in boneNames)
            {
                if (string.IsNullOrWhiteSpace(b)) continue;
                string record = $"{normSlot}|{chainId ?? ""}|{b}";
                if (known.Add(record)) existing.Add(record);
            }
            skeleton.SetMeta("CustomAppendedBoneEntries", existing.ToArray());
        }

        /// <summary>
        /// Removes appended custom bones belonging to <paramref name="slotKey"/> (or all appended bones when null)
        /// from <paramref name="skeleton"/> while preserving 100% of vanilla hero bone indices [0 .. OriginalHeroBoneCount - 1]
        /// and re-binding any remaining custom meshes by bone name.
        /// </summary>
        public static void RemoveAppendedSlotBonesFromSkeleton(Node3D heroNode, Skeleton3D skeleton, string slotKey, bool restoreAnimationPose = true)
        {
            if (skeleton == null || !GodotObject.IsInstanceValid(skeleton)) return;

            if (heroNode != null)
            {
                var driver = heroNode.FindChild("CustomJiggleBoneDriver", false, false) as DeadlockPlayground.Animation.CustomJiggleBoneDriver;
                if (driver != null)
                {
                    if (string.IsNullOrEmpty(slotKey)) driver.ClearAll();
                    else driver.RemoveSlot(slotKey);
                }
            }

            if (!skeleton.HasMeta("CustomAppendedBoneEntries")) return;
            var entries = skeleton.GetMeta("CustomAppendedBoneEntries").AsStringArray();
            if (entries == null || entries.Length == 0) return;

            var removedBones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var keptEntries = new List<string>();
            foreach (string rec in entries)
            {
                var parts = rec.Split('|', 3);
                if (parts.Length < 3) continue;
                bool match = string.IsNullOrEmpty(slotKey) || string.Equals(parts[0], slotKey, StringComparison.OrdinalIgnoreCase);
                if (match) removedBones.Add(parts[2]);
                else keptEntries.Add(rec);
            }
            if (removedBones.Count == 0) return;

            int origCount = skeleton.HasMeta("OriginalHeroBoneCount")
                ? Math.Clamp(skeleton.GetMeta("OriginalHeroBoneCount").AsInt32(), 0, skeleton.GetBoneCount())
                : skeleton.GetBoneCount();

            int curCount = skeleton.GetBoneCount();
            var keepOldIndices = new List<int>(curCount);
            var oldToNew = new Dictionary<int, int>();
            for (int b = 0; b < curCount; b++)
            {
                string bName = skeleton.GetBoneName(b);
                if (b >= origCount && removedBones.Contains(bName)) continue;
                oldToNew[b] = keepOldIndices.Count;
                keepOldIndices.Add(b);
            }

            // Snapshot remaining bones
            var snapNames = new string[keepOldIndices.Count];
            var snapParents = new int[keepOldIndices.Count];
            var snapRests = new Transform3D[keepOldIndices.Count];
            for (int i = 0; i < keepOldIndices.Count; i++)
            {
                int ob = keepOldIndices[i];
                snapNames[i] = skeleton.GetBoneName(ob);
                int op = skeleton.GetBoneParent(ob);
                snapParents[i] = (op >= 0 && oldToNew.TryGetValue(op, out int np)) ? np : -1;
                snapRests[i] = skeleton.GetBoneRest(ob);
            }

            skeleton.ClearBones();
            for (int i = 0; i < snapNames.Length; i++) skeleton.AddBone(snapNames[i]);
            for (int i = 0; i < snapNames.Length; i++)
            {
                if (snapParents[i] >= 0) skeleton.SetBoneParent(i, snapParents[i]);
                skeleton.SetBoneRest(i, snapRests[i]);
            }
            skeleton.ResetBonePoses();

            // Re-bind ALL meshes (vanilla hero, weapons, and remaining custom meshes) and invalidate
            // Godot's AnimationPlayer track caches so ClearBones() never freezes vanilla models in A-pose.
            if (heroNode != null)
            {
                foreach (var mi in CollectMeshes(heroNode))
                {
                    if (mi == null || !GodotObject.IsInstanceValid(mi)) continue;
                    if (mi.Skin != null)
                    {
                        int binds = mi.Skin.GetBindCount();
                        for (int bi = 0; bi < binds; bi++)
                        {
                            string bn = mi.Skin.GetBindName(bi);
                            // Godot 4 resolves named binds automatically by BindName when Skeleton is re-attached;
                            // calling SetBindBone() on a named bind clears BindName and corrupts bone bindings!
                            if (string.IsNullOrEmpty(bn))
                            {
                                int oldIdx = mi.Skin.GetBindBone(bi);
                                if (oldIdx >= 0 && oldToNew.TryGetValue(oldIdx, out int newIdx))
                                    mi.Skin.SetBindBone(bi, newIdx);
                            }
                        }
                        // Force Godot's RenderingServer SkinReference to re-attach after ClearBones()
                        var skelPath = mi.Skeleton;
                        if (skelPath != null && !skelPath.IsEmpty)
                        {
                            mi.Skeleton = new NodePath();
                            mi.Skeleton = skelPath;
                        }
                    }
                }

                // Refresh BoneAttachment3D weapon/prop attachments after ClearBones() without triggering -1 bone warnings
                foreach (var child in heroNode.FindChildren("*", "BoneAttachment3D", true, false))
                {
                    if (child is BoneAttachment3D ba && GodotObject.IsInstanceValid(ba) && !string.IsNullOrEmpty(ba.BoneName))
                    {
                        int foundIdx = skeleton.FindBone(ba.BoneName);
                        if (foundIdx >= 0)
                            ba.BoneIdx = foundIdx;
                    }
                }

                // Clear AnimationMixer track caches on every AnimationPlayer so tracks re-resolve against rebuilt bones
                foreach (var child in heroNode.FindChildren("*", "AnimationPlayer", true, false))
                {
                    if (child is AnimationPlayer ap && GodotObject.IsInstanceValid(ap))
                    {
                        string curAnim = !string.IsNullOrEmpty(ap.CurrentAnimation) ? ap.CurrentAnimation : ap.AssignedAnimation;
                        double curPos = !string.IsNullOrEmpty(ap.CurrentAnimation) ? ap.CurrentAnimationPosition : 0.0;
                        bool wasPlaying = ap.IsPlaying();
                        ap.ClearCaches();
                        if (restoreAnimationPose && !string.IsNullOrEmpty(curAnim) && ap.HasAnimation(curAnim))
                        {
                            ap.Play(curAnim);
                            ap.Seek(curPos, true);
                            if (!wasPlaying) ap.Pause();
                        }
                    }
                }
            }

            if (keptEntries.Count > 0) skeleton.SetMeta("CustomAppendedBoneEntries", keptEntries.ToArray());
            else skeleton.RemoveMeta("CustomAppendedBoneEntries");
        }

        /// <summary>
        /// Strips non-unit scale, shear, and handedness reflection from a basis so appended custom bones
        /// always carry pure unit-scale right-handed rotations matching Source 2's scale-less Skeleton/FeModel.
        /// </summary>
        private static Basis OrthonormalizeRightHanded(Basis b)
        {
            Basis ortho = b.Orthonormalized();
            if (ortho.Determinant() < 0f)
                ortho = new Basis(ortho.X, ortho.Y, -ortho.Z);
            return ortho;
        }

        private static void AppendPreservedSourceChainToHeroSkeleton(
            Skeleton3D sourceSkeleton,
            Transform3D skelToHero,
            Skeleton3D targetSkeleton,
            Skin heroSkin,
            BoneChainDef chain,
            string slotKey,
            HashSet<string> disabledSourceBones,
            Dictionary<int, int> srcToAppendedHeroMap,
            ImportReport report,
            HeadSegmentRegistration headSeg = null,
            Vector3 pelvisOffset = default)
        {
            if (sourceSkeleton == null || targetSkeleton == null || chain == null || chain.SourceBoneNames.Count == 0)
                return;

            if (!targetSkeleton.HasMeta("OriginalHeroBoneCount"))
                targetSkeleton.SetMeta("OriginalHeroBoneCount", targetSkeleton.GetBoneCount());

            int anchorIdx = targetSkeleton.FindBone(string.IsNullOrWhiteSpace(chain.AnchorHeroBone) ? "spine_3" : chain.AnchorHeroBone);
            if (anchorIdx < 0) anchorIdx = targetSkeleton.FindBone("pelvis");
            if (anchorIdx < 0) anchorIdx = 0;

            bool snapToHead = headSeg != null && (anchorIdx == headSeg.TargetHeadBone || anchorIdx == headSeg.TargetNeckBone);
            int pelvisIdx = targetSkeleton.FindBone("pelvis");
            bool snapToPelvis = pelvisOffset.LengthSquared() > 1e-8f && anchorIdx == pelvisIdx;

            var localSrcToTgt = new Dictionary<int, int>();
            foreach (string srcName in chain.SourceBoneNames)
            {
                if (disabledSourceBones != null && disabledSourceBones.Contains(srcName)) continue;
                int sIdx = sourceSkeleton.FindBone(srcName);
                if (sIdx < 0) continue;

                int srcParent = sourceSkeleton.GetBoneParent(sIdx);
                int tgtParent = (srcParent >= 0 && localSrcToTgt.TryGetValue(srcParent, out int chainParentTgt))
                    ? chainParentTgt
                    : anchorIdx;

                Transform3D srcGlobalHero = skelToHero * sourceSkeleton.GetBoneGlobalRest(sIdx);
                if (snapToHead)
                {
                    var (sPos, sRot) = headSeg.Evaluate(anchorIdx, targetSkeleton, srcGlobalHero.Origin);
                    srcGlobalHero = new Transform3D(new Basis(sRot) * srcGlobalHero.Basis, sPos);
                }
                else if (snapToPelvis)
                {
                    srcGlobalHero.Origin += pelvisOffset;
                }
                // Source 2 skeletons & FeModel cloth chains have NO per-bone scale.
                // Stripping cm-armature / Fitting-Room scale (e.g. 0.01x from Unreal/FBX/Blender rigs)
                // prevents parentGlobalHero.AffineInverse() from exploding child bone origins by 100x on export!
                srcGlobalHero = new Transform3D(OrthonormalizeRightHanded(srcGlobalHero.Basis), srcGlobalHero.Origin);

                Transform3D parentGlobalHero = tgtParent >= 0 ? targetSkeleton.GetBoneGlobalRest(tgtParent) : Transform3D.Identity;
                Transform3D parentRigidHero = new Transform3D(OrthonormalizeRightHanded(parentGlobalHero.Basis), parentGlobalHero.Origin);
                Transform3D localRestHero = parentRigidHero.AffineInverse() * srcGlobalHero;
                localRestHero = new Transform3D(OrthonormalizeRightHanded(localRestHero.Basis), localRestHero.Origin);

                string uniqueName = DeduplicateHeroBoneName(targetSkeleton, srcName);
                int newIdx = targetSkeleton.GetBoneCount();
                targetSkeleton.AddBone(uniqueName);
                if (tgtParent >= 0) targetSkeleton.SetBoneParent(newIdx, tgtParent);
                targetSkeleton.SetBoneRest(newIdx, localRestHero);
                targetSkeleton.SetBonePosePosition(newIdx, localRestHero.Origin);
                targetSkeleton.SetBonePoseRotation(newIdx, localRestHero.Basis.GetRotationQuaternion());
                targetSkeleton.SetBonePoseScale(newIdx, Vector3.One);

                if (heroSkin != null) GetOrAddSkinBindIndex(heroSkin, targetSkeleton, newIdx);

                localSrcToTgt[sIdx] = newIdx;
                srcToAppendedHeroMap[sIdx] = newIdx;
                chain.AppendedHeroBoneNames.Add(uniqueName);
            }

            if (chain.AppendedHeroBoneNames.Count > 0)
            {
                TagAppendedBonesOnSkeleton(targetSkeleton, slotKey, chain.ChainId, chain.AppendedHeroBoneNames);
                report?.AppendedChains.Add($"{chain.Name} -> {targetSkeleton.GetBoneName(anchorIdx)}");
            }
        }

        private static RiggingResult RigAccessoryKeepOwnBones(
            Node3D importedScene,
            List<MeshInstance3D> importedMeshes,
            Skeleton3D sourceSkeleton,
            Node3D heroNode,
            Skeleton3D targetSkeleton,
            Skin heroSkin,
            RiggingOptions options)
        {
            var result = new RiggingResult
            {
                Success = true,
                RiggedHeroRoot = heroNode,
                WasAlreadyRigged = true
            };

            if (!targetSkeleton.HasMeta("OriginalHeroBoneCount"))
                targetSkeleton.SetMeta("OriginalHeroBoneCount", targetSkeleton.GetBoneCount());

            int anchorBoneIdx = targetSkeleton.FindBone(string.IsNullOrWhiteSpace(options.RigidBoneName) ? "spine_3" : options.RigidBoneName);
            if (anchorBoneIdx < 0) anchorBoneIdx = targetSkeleton.FindBone("pelvis");
            if (anchorBoneIdx < 0) anchorBoneIdx = 0;
            string anchorName = targetSkeleton.GetBoneName(anchorBoneIdx);

            int srcCount = sourceSkeleton.GetBoneCount();
            var srcNames = new string[srcCount];
            var srcParents = new int[srcCount];
            for (int i = 0; i < srcCount; i++)
            {
                srcNames[i] = sourceSkeleton.GetBoneName(i);
                srcParents[i] = sourceSkeleton.GetBoneParent(i);
            }

            var weighted = RigBoneClassifier.ComputeWeightedBoneIndices(sourceSkeleton, importedMeshes);
            var analysis = RigBoneClassifier.Analyze(srcNames, srcParents, options.RigFamilyOverride, weighted);
            result.Report.DetectedFamily = analysis.DetectedFamily;
            result.Report.EffectiveFamily = analysis.Family;
            result.Report.FamilyConfidence = analysis.Confidence;

            var disabledSet = new HashSet<int>();
            if (options.DisabledSourceBones != null)
            {
                for (int i = 0; i < srcCount; i++)
                    if (options.DisabledSourceBones.Contains(srcNames[i])) disabledSet.Add(i);
            }
            bool[] keepMask = RigBoneClassifier.ComputeExportKeepMask(analysis, weighted, options.KeepUnweightedBones, disabledSet);

            // Build chains for accessory bones (each root bone with kept descendants becomes a BoneChainDef)
            Transform3D skelToHero = options.AlignedTransform * ComputeTransformRelativeToRoot(sourceSkeleton, importedScene);
            string slotKey = string.IsNullOrWhiteSpace(options.SlotId) ? "__acc__" : options.SlotId;

            if (options.BoneChains == null) options.BoneChains = new List<BoneChainDef>();
            var existingByRoot = options.BoneChains
                .Where(c => c != null && !string.IsNullOrEmpty(c.RootSourceBone))
                .ToDictionary(c => c.RootSourceBone, c => c, StringComparer.OrdinalIgnoreCase);

            var srcToHeroIdx = new Dictionary<int, int>();
            var keptRootIndices = new List<int>();
            for (int sb = 0; sb < srcCount; sb++)
            {
                if (!keepMask[sb]) continue;
                int sp = srcParents[sb];
                if (sp < 0 || !keepMask[sp]) keptRootIndices.Add(sb);
            }

            // Collect children map for topological order
            var children = new List<int>[srcCount];
            for (int i = 0; i < srcCount; i++) children[i] = new List<int>();
            for (int i = 0; i < srcCount; i++)
                if (srcParents[i] >= 0 && srcParents[i] < srcCount) children[srcParents[i]].Add(i);

            // Split branching hub roots (e.g. cape.glb's GLTF_created_0_rootJoint with 6 parallel strands)
            // into separate per-strand chains instead of dumping all branches into one crooked DFS snake!
            var expandedStrandRoots = new List<(int RootIdx, bool IsRigidHub)>();
            foreach (int rootIdx in keptRootIndices)
            {
                var keptKids = children[rootIdx].Where(c => keepMask[c]).ToList();
                if (keptKids.Count >= 2)
                {
                    if (weighted.Contains(rootIdx))
                        expandedStrandRoots.Add((rootIdx, true));
                    foreach (int kid in keptKids)
                        expandedStrandRoots.Add((kid, false));
                }
                else
                {
                    expandedStrandRoots.Add((rootIdx, false));
                }
            }

            // Count multi-bone parallel strands so multi-strand capes/skirts default cleanly to ProxyCloth
            int multiBoneStrandCount = 0;
            foreach (var (rIdx, isHub) in expandedStrandRoots)
            {
                if (isHub) continue;
                int cnt = 0;
                var stCount = new Stack<int>();
                stCount.Push(rIdx);
                while (stCount.Count > 0)
                {
                    int cur = stCount.Pop();
                    if (!keepMask[cur]) continue;
                    cnt++;
                    foreach (int c in children[cur]) stCount.Push(c);
                }
                if (cnt >= 2) multiBoneStrandCount++;
            }

            foreach (var (rootIdx, isRigidHub) in expandedStrandRoots)
            {
                var orderedBones = new List<int>();
                if (isRigidHub)
                {
                    orderedBones.Add(rootIdx);
                }
                else
                {
                    var st = new Stack<int>();
                    st.Push(rootIdx);
                    while (st.Count > 0)
                    {
                        int cur = st.Pop();
                        if (!keepMask[cur]) continue;
                        orderedBones.Add(cur);
                        for (int c = children[cur].Count - 1; c >= 0; c--) st.Push(children[cur][c]);
                    }
                }

                if (!existingByRoot.TryGetValue(srcNames[rootIdx], out var chain))
                {
                    bool isMultiStrandSheet = !isRigidHub && multiBoneStrandCount >= 3 && orderedBones.Count >= 2;
                    chain = new BoneChainDef
                    {
                        ChainId = $"acc_{slotKey}_{srcNames[rootIdx]}",
                        Name = isRigidHub
                            ? $"{srcNames[rootIdx]} [Rigid Hub]"
                            : $"{srcNames[rootIdx]} ({orderedBones.Count} bones)",
                        Category = "Accessory",
                        RootSourceBone = srcNames[rootIdx],
                        SourceBoneNames = orderedBones.Select(b => srcNames[b]).ToList(),
                        AnchorHeroBone = anchorName,
                        Policy = isRigidHub ? ChainPolicy.CollapseToNearestCore : ChainPolicy.PreservePhysicsChain,
                        Simulation = isMultiStrandSheet ? SimulationKind.ProxyCloth : (orderedBones.Count >= 2 ? SimulationKind.Cloth : SimulationKind.Jiggle),
                        StiffnessRoot = isMultiStrandSheet ? 0.50f : 0.75f,
                        StiffnessTip = isMultiStrandSheet ? 0.05f : 0.12f,
                        Damping = 0.25f,
                        Gravity = 1.0f,
                        CollisionRadiusTipInches = 2.0f,
                        StrayRadiusInches = -1.0f,
                        PlanarizeBackBarrier = isMultiStrandSheet,
                        OpenSheet = true,
                        SlotId = slotKey
                    };
                    // Pin collar/root bone rigidly ONLY on 3+ bone cloth/cape strands (where bone 0 is a short collar).
                    // On 2-bone chains, bone 0 spans the upper 75% of the garment and Source 2 already inserts a synthetic
                    // static shoulder anchor, so pinning bone 0 would freeze 75% of the cape in Godot preview.
                    if (!isRigidHub && orderedBones.Count >= 3 && chain.Simulation != SimulationKind.Jiggle)
                    {
                        var rootOv = chain.GetOrCreateJointOverride(srcNames[orderedBones[0]]);
                        rootOv.Simulate = false;
                    }
                    options.BoneChains.Add(chain);
                }
                else
                {
                    // Preserve user-configured physics tuning & anchor when moving/scaling the accessory!
                    chain.SourceBoneNames = orderedBones.Select(b => srcNames[b]).ToList();
                    if (string.IsNullOrWhiteSpace(chain.AnchorHeroBone))
                        chain.AnchorHeroBone = anchorName;
                    chain.SlotId = slotKey;
                }

                chain.AppendedHeroBoneNames.Clear();
                if (chain.Policy == ChainPolicy.PreservePhysicsChain)
                {
                    AppendPreservedSourceChainToHeroSkeleton(
                        sourceSkeleton, skelToHero, targetSkeleton, heroSkin,
                        chain, slotKey, options.DisabledSourceBones, srcToHeroIdx, result.Report);
                    if (result.Report != null)
                        result.Report.Stats.AccessoryBoneCount += chain.AppendedHeroBoneNames.Count;
                }
            }

            var (skelToBind, _) = BuildSkinIndexMappings(targetSkeleton, heroSkin);

            var containerNode = new Node3D { Name = GetContainerNameForSlot(options?.SlotId) };
            heroNode.AddChild(containerNode);
            result.ContainerNode = containerNode;

            int totalVerts = 0, totalTris = 0;
            foreach (var srcMi in importedMeshes)
            {
                var srcMesh = srcMi.Mesh;
                if (srcMesh == null || srcMesh.GetSurfaceCount() == 0) continue;

                for (int s = 0; s < srcMesh.GetSurfaceCount(); s++)
                {
                    var arrays = srcMesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count == 0) continue;
                    var rawVerts = GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    var rawNorms = arrays.Count > (int)Mesh.ArrayType.Normal ? GetVector3Array(arrays[(int)Mesh.ArrayType.Normal]) : null;
                    var rawUvs = ResolvePrimaryUvsForSurface(srcMesh, srcMi, s, arrays);
                    var rawIndices = arrays.Count > (int)Mesh.ArrayType.Index ? GetInt32Array(arrays[(int)Mesh.ArrayType.Index]) : null;
                    var rawBones = arrays.Count > (int)Mesh.ArrayType.Bones ? GetInt32Array(arrays[(int)Mesh.ArrayType.Bones]) : null;
                    var rawWeights = arrays.Count > (int)Mesh.ArrayType.Weights ? GetFloat32Array(arrays[(int)Mesh.ArrayType.Weights]) : null;
                    if (rawVerts == null || rawVerts.Length == 0) continue;

                    int vCount = rawVerts.Length;
                    totalVerts += vCount;
                    totalTris += (rawIndices != null) ? rawIndices.Length / 3 : vCount / 3;

                    EvaluateMeshSurfaceInTargetSpace(srcMi, importedScene, options.AlignedTransform, sourceSkeleton, rawVerts, rawNorms, rawBones, rawWeights, out Vector3[] heroVerts, out Vector3[] heroNorms, useExplicitSkeleton: true);

                    int srcStride = (rawBones != null && rawWeights != null && rawBones.Length >= vCount * 8 && rawWeights.Length >= vCount * 8) ? 8 : 4;
                    int[] srcBindToSkel = null;
                    if (srcMi.Skin != null && srcMi.Skin.GetBindCount() > 0)
                    {
                        srcBindToSkel = new int[srcMi.Skin.GetBindCount()];
                        for (int bi = 0; bi < srcBindToSkel.Length; bi++)
                            srcBindToSkel[bi] = ResolveSkinBindBone(srcMi.Skin, bi, sourceSkeleton);
                    }

                    int[] skinBones = new int[vCount * 4];
                    float[] outWeights = new float[vCount * 4];
                    int anchorBind = skelToBind.TryGetValue(anchorBoneIdx, out int ab) ? ab : 0;

                    if (!TryApplySkinWeightOverrideSkinBindIndices(
                            options, srcMi.Name.ToString(), s, vCount, targetSkeleton, heroSkin, skelToBind, anchorBind, skinBones, outWeights))
                    {
                        for (int v = 0; v < vCount; v++)
                        {
                            var merged = new Dictionary<int, float>();
                            for (int k = 0; k < srcStride; k++)
                            {
                                float w = (rawWeights != null && rawWeights.Length > v * srcStride + k) ? rawWeights[v * srcStride + k] : (k == 0 ? 1f : 0f);
                                if (w <= 1e-5f) continue;
                                int rb = (rawBones != null && rawBones.Length > v * srcStride + k) ? rawBones[v * srcStride + k] : 0;
                                int sb = (srcBindToSkel != null && rb >= 0 && rb < srcBindToSkel.Length) ? srcBindToSkel[rb] : Math.Clamp(rb, 0, Math.Max(0, srcCount - 1));
                                int tb = srcToHeroIdx.TryGetValue(sb, out int mapped) ? mapped : anchorBoneIdx;
                                merged[tb] = merged.GetValueOrDefault(tb) + w;
                            }
                            if (merged.Count == 0) merged[anchorBoneIdx] = 1f;
                            var top = merged.OrderByDescending(kv => kv.Value).Take(4).ToList();
                            float sum = top.Sum(kv => kv.Value);
                            for (int k = 0; k < 4; k++)
                            {
                                if (k < top.Count && sum > 1e-6f)
                                {
                                    skinBones[v * 4 + k] = skelToBind.TryGetValue(top[k].Key, out int bi) ? bi : anchorBind;
                                    outWeights[v * 4 + k] = top[k].Value / sum;
                                }
                                else
                                {
                                    skinBones[v * 4 + k] = 0;
                                    outWeights[v * 4 + k] = 0f;
                                }
                            }
                        }
                    }

                    var surfaceArrays = new Godot.Collections.Array();
                    surfaceArrays.Resize((int)Mesh.ArrayType.Max);
                    surfaceArrays[(int)Mesh.ArrayType.Vertex] = heroVerts;
                    surfaceArrays[(int)Mesh.ArrayType.Normal] = heroNorms;
                    if (rawUvs != null && rawUvs.Length == vCount) surfaceArrays[(int)Mesh.ArrayType.TexUV] = rawUvs;
                    if (rawIndices != null && rawIndices.Length > 0) surfaceArrays[(int)Mesh.ArrayType.Index] = rawIndices;
                    surfaceArrays[(int)Mesh.ArrayType.Bones] = skinBones;
                    surfaceArrays[(int)Mesh.ArrayType.Weights] = outWeights;

                    var newMesh = new ArrayMesh();
                    newMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, surfaceArrays);
                    Material surfMat = srcMesh.SurfaceGetMaterial(s) ?? srcMi.GetSurfaceOverrideMaterial(s) ?? CreateDefaultPbrMaterial(srcMi.Name);
                    newMesh.SurfaceSetMaterial(0, surfMat);

                    var newMi = new MeshInstance3D
                    {
                        Name = $"{srcMi.Name}_surf{s}",
                        Mesh = newMesh,
                        Skin = heroSkin,
                        Transform = Transform3D.Identity
                    };
                    newMi.Layers &= ~(uint)(1 << 20);
                    newMi.SetMeta("IsCustomImportedMesh", true);
                    newMi.SetMeta("SourceMeshName", srcMi.Name.ToString());
                    newMi.SetMeta("SourceSurfaceIndex", s);
                    newMi.SetMeta("OriginalMaterial_0", surfMat);
                    ConfigureRiggedSubmeshMetadata(newMi, srcMi, surfMat, options, heroNode);

                    containerNode.AddChild(newMi);
                    newMi.Skeleton = newMi.GetPathTo(targetSkeleton);
                    result.CustomSubmeshes.Add(newMi);
                }
            }

            result.TotalVertices = totalVerts;
            result.TotalTriangles = totalTris;
            result.Report.Stats.AccessoryBoneCount = srcToHeroIdx.Count;
            GD.Print($"[CustomModelAutoRigger] Accessory KeepOwnBones attached {srcToHeroIdx.Count} bone(s) under '{anchorName}' ({totalVerts} verts).");
            return result;
        }

        private static bool TryResolveSurfaceWeightOverride(
            RiggingOptions options,
            string srcMeshName,
            int surfaceIndex,
            int vCount,
            out SkinWeightOverride surfaceOverride)
        {
            surfaceOverride = null;
            if (options?.CustomSkinWeightOverrides == null || options.CustomSkinWeightOverrides.Count == 0)
                return false;

            string key = MakeSurfaceKeepKey(srcMeshName, surfaceIndex);
            if (!options.CustomSkinWeightOverrides.TryGetValue(key, out var ov) || ov == null)
                return false;

            if (ov.VertexCount != vCount ||
                ov.BonePalette == null || ov.BonePalette.Length == 0 ||
                ov.PaletteIndices == null || ov.PaletteIndices.Length < vCount * 4 ||
                ov.Weights == null || ov.Weights.Length < vCount * 4)
            {
                return false;
            }

            surfaceOverride = ov;
            return true;
        }

        private static int FindBoneIndexCaseInsensitive(Skeleton3D skeleton, string boneName)
        {
            if (skeleton == null || string.IsNullOrWhiteSpace(boneName)) return -1;
            int idx = skeleton.FindBone(boneName);
            if (idx >= 0) return idx;
            int count = skeleton.GetBoneCount();
            for (int i = 0; i < count; i++)
            {
                if (string.Equals(skeleton.GetBoneName(i), boneName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private static bool TryApplySkinWeightOverrideSkeletonIndices(
            RiggingOptions options,
            string srcMeshName,
            int surfaceIndex,
            int vCount,
            Skeleton3D targetSkeleton,
            int[] outSkeletonBones,
            float[] outWeights)
        {
            if (!TryResolveSurfaceWeightOverride(options, srcMeshName, surfaceIndex, vCount, out var ov))
                return false;

            var paletteToSkel = new int[ov.BonePalette.Length];
            int safeFallbackSkel = -1;
            for (int p = 0; p < ov.BonePalette.Length; p++)
            {
                int tb = FindBoneIndexCaseInsensitive(targetSkeleton, ov.BonePalette[p]);
                paletteToSkel[p] = tb;
                if (safeFallbackSkel < 0 && tb > 0) safeFallbackSkel = tb;
            }
            if (safeFallbackSkel < 0)
            {
                safeFallbackSkel = FindBoneAny(targetSkeleton, "pelvis", "spine_3", "head");
                if (safeFallbackSkel < 0) safeFallbackSkel = 0;
            }

            for (int v = 0; v < vCount; v++)
            {
                int baseOff = v * 4;
                float sum = 0f;
                for (int k = 0; k < 4; k++)
                {
                    int pIdx = ov.PaletteIndices[baseOff + k];
                    int tb = (pIdx >= 0 && pIdx < paletteToSkel.Length) ? paletteToSkel[pIdx] : -1;
                    float w = tb >= 0 ? Math.Max(0f, ov.Weights[baseOff + k]) : 0f;
                    outSkeletonBones[baseOff + k] = tb >= 0 ? tb : 0;
                    outWeights[baseOff + k] = w;
                    sum += w;
                }
                if (sum > 0.0001f)
                {
                    for (int k = 0; k < 4; k++) outWeights[baseOff + k] /= sum;
                }
                else
                {
                    outSkeletonBones[baseOff] = safeFallbackSkel;
                    outWeights[baseOff] = 1f;
                    outSkeletonBones[baseOff + 1] = 0; outWeights[baseOff + 1] = 0f;
                    outSkeletonBones[baseOff + 2] = 0; outWeights[baseOff + 2] = 0f;
                    outSkeletonBones[baseOff + 3] = 0; outWeights[baseOff + 3] = 0f;
                }
            }

            return true;
        }

        private static bool TryApplySkinWeightOverrideSkinBindIndices(
            RiggingOptions options,
            string srcMeshName,
            int surfaceIndex,
            int vCount,
            Skeleton3D targetSkeleton,
            Skin targetSkin,
            Dictionary<int, int> skelToBind,
            int fallbackBindIdx,
            int[] outSkinBones,
            float[] outWeights)
        {
            if (!TryResolveSurfaceWeightOverride(options, srcMeshName, surfaceIndex, vCount, out var ov))
                return false;

            var paletteToBind = new int[ov.BonePalette.Length];
            for (int p = 0; p < ov.BonePalette.Length; p++)
            {
                int tb = FindBoneIndexCaseInsensitive(targetSkeleton, ov.BonePalette[p]);
                if (tb >= 0)
                {
                    if (!skelToBind.TryGetValue(tb, out int bindIdx))
                    {
                        bindIdx = GetOrAddSkinBindIndex(targetSkin, targetSkeleton, tb);
                        skelToBind[tb] = bindIdx;
                    }
                    paletteToBind[p] = bindIdx;
                }
                else
                {
                    paletteToBind[p] = -1;
                }
            }

            for (int v = 0; v < vCount; v++)
            {
                int baseOff = v * 4;
                float sum = 0f;
                for (int k = 0; k < 4; k++)
                {
                    int pIdx = ov.PaletteIndices[baseOff + k];
                    int bi = (pIdx >= 0 && pIdx < paletteToBind.Length) ? paletteToBind[pIdx] : -1;
                    float w = bi >= 0 ? Math.Max(0f, ov.Weights[baseOff + k]) : 0f;
                    outSkinBones[baseOff + k] = bi >= 0 ? bi : 0;
                    outWeights[baseOff + k] = w;
                    sum += w;
                }
                if (sum > 0.0001f)
                {
                    for (int k = 0; k < 4; k++) outWeights[baseOff + k] /= sum;
                }
                else
                {
                    outSkinBones[baseOff] = fallbackBindIdx;
                    outWeights[baseOff] = 1f;
                    outSkinBones[baseOff + 1] = 0; outWeights[baseOff + 1] = 0f;
                    outSkinBones[baseOff + 2] = 0; outWeights[baseOff + 2] = 0f;
                    outSkinBones[baseOff + 3] = 0; outWeights[baseOff + 3] = 0f;
                }
            }

            return true;
        }

        private static void ApplyAuthoritativeKnnBoneRemap(
            Skeleton3D heroSkeleton,
            int vCount,
            int[] boneIndices,
            float[] boneWeights,
            Dictionary<string, string> customBoneRemap,
            HashSet<string> disabledBones)
        {
            if (heroSkeleton == null) return;
            bool hasRemap = customBoneRemap != null && customBoneRemap.Count > 0;
            bool hasDisabled = disabledBones != null && disabledBones.Count > 0;
            if (!hasRemap && !hasDisabled) return;

            int boneCount = heroSkeleton.GetBoneCount();
            int[] redirect = new int[boneCount];
            bool anyChange = false;
            for (int b = 0; b < boneCount; b++) redirect[b] = b;

            if (hasRemap)
            {
                foreach (var (canonical, mappedVal) in customBoneRemap)
                {
                    int fromIdx = heroSkeleton.FindBone(canonical);
                    if (fromIdx < 0) continue;
                    if (string.IsNullOrWhiteSpace(mappedVal))
                    {
                        int p = heroSkeleton.GetBoneParent(fromIdx);
                        redirect[fromIdx] = p >= 0 ? p : 0;
                        anyChange = true;
                    }
                    else
                    {
                        int toIdx = heroSkeleton.FindBone(mappedVal);
                        if (toIdx >= 0 && toIdx != fromIdx)
                        {
                            redirect[fromIdx] = toIdx;
                            anyChange = true;
                        }
                    }
                }
            }

            if (hasDisabled)
            {
                for (int b = 0; b < boneCount; b++)
                {
                    if (!disabledBones.Contains(heroSkeleton.GetBoneName(b))) continue;
                    int cur = heroSkeleton.GetBoneParent(b);
                    while (cur >= 0 && disabledBones.Contains(heroSkeleton.GetBoneName(cur)))
                        cur = heroSkeleton.GetBoneParent(cur);
                    redirect[b] = cur >= 0 ? cur : 0;
                    anyChange = true;
                }
            }

            if (!anyChange) return;

            for (int v = 0; v < vCount; v++)
            {
                int b0 = boneIndices[v * 4 + 0];
                int b1 = boneIndices[v * 4 + 1];
                int b2 = boneIndices[v * 4 + 2];
                int b3 = boneIndices[v * 4 + 3];
                if (b0 >= 0 && b0 < boneCount) boneIndices[v * 4 + 0] = redirect[b0];
                if (b1 >= 0 && b1 < boneCount) boneIndices[v * 4 + 1] = redirect[b1];
                if (b2 >= 0 && b2 < boneCount) boneIndices[v * 4 + 2] = redirect[b2];
                if (b3 >= 0 && b3 < boneCount) boneIndices[v * 4 + 3] = redirect[b3];
            }
        }

        private static void LogEffectiveBoneRemap(
            Skeleton3D sourceSkeleton,
            Skeleton3D targetSkeleton,
            Dictionary<int, int> sourceToTarget,
            Dictionary<string, string> userCustomRemap,
            ImportReport report)
        {
            var autoMap = HumanoidBoneMapper.AutoMap(sourceSkeleton, targetSkeleton);
            var effectiveByTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (sIdx, tIdx) in sourceToTarget)
            {
                if (sIdx >= 0 && sIdx < sourceSkeleton.GetBoneCount() && tIdx >= 0 && tIdx < targetSkeleton.GetBoneCount())
                    effectiveByTarget[targetSkeleton.GetBoneName(tIdx)] = sourceSkeleton.GetBoneName(sIdx);
            }

            if (report != null)
            {
                foreach (var def in HumanoidBoneMapper.Definitions)
                {
                    if (def.Group == HumanoidBoneMapper.BoneGroup.Secondary) continue;
                    autoMap.TryGetValue(def.CanonicalName, out string autoSrc);
                    string userSrc = null;
                    bool hasUser = userCustomRemap != null && userCustomRemap.TryGetValue(def.CanonicalName, out userSrc);
                    effectiveByTarget.TryGetValue(def.CanonicalName, out string effSrc);
                    report.RemapDiffs.Add(new RemapDiffEntry
                    {
                        CanonicalName = def.CanonicalName,
                        AutoMappedSource = autoSrc ?? "",
                        UserOverrideSource = hasUser ? (userSrc ?? "") : "",
                        EffectiveSource = effSrc ?? "",
                        IsOverridden = hasUser && !string.IsNullOrEmpty(userSrc) && !string.Equals(userSrc, autoSrc, StringComparison.OrdinalIgnoreCase),
                        IsClearedByUser = hasUser && string.IsNullOrEmpty(userSrc)
                    });
                }
            }

            string pairs = string.Join(", ", effectiveByTarget.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}<-{kv.Value}"));
            GD.Print($"[CustomModelAutoRigger] Effective Bone Remap ({effectiveByTarget.Count} targets): {pairs}");
        }

        private static void FinalizeImportReportStats(RiggingResult result, Skeleton3D heroSkeleton)
        {
            if (result?.Report == null || heroSkeleton == null) return;
            var stats = result.Report.Stats;
            stats.TotalExportBoneCount = heroSkeleton.GetBoneCount();
            if (stats.TotalExportBoneCount > 512)
                result.Report.Warnings.Add($"Skeleton bone count ({stats.TotalExportBoneCount}) exceeds 512; Source 2 export may fail or require pruning.");
            else if (stats.TotalExportBoneCount > 256)
                result.Report.Warnings.Add($"Skeleton bone count ({stats.TotalExportBoneCount}) exceeds 256; consider pruning unweighted bones.");

            int unweighted = 0;
            foreach (var mi in result.CustomSubmeshes)
            {
                if (mi?.Mesh == null) continue;
                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                {
                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.Weights) continue;
                    var wVar = arrays[(int)Mesh.ArrayType.Weights];
                    if (wVar.VariantType == Variant.Type.Nil) continue;
                    var wArr = wVar.AsFloat32Array();
                    var vArr = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    if (wArr == null || vArr == null || vArr.Length == 0) continue;
                    int stride = Math.Max(4, wArr.Length / vArr.Length);
                    for (int v = 0; v < vArr.Length; v++)
                    {
                        float sum = 0f;
                        for (int k = 0; k < stride && (v * stride + k) < wArr.Length; k++) sum += wArr[v * stride + k];
                        if (sum < 0.05f) unweighted++;
                    }
                }
            }
            stats.UnweightedVertexCount = unweighted;
            if (unweighted > 0)
                result.Report.Warnings.Add($"{unweighted} vertex(es) had near-zero skin weight sum.");
        }

        #endregion
    }
}
