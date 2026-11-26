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
        #region Case A: Unrigged Model Auto-Skinning

        private static RiggingResult RigUnriggedModel(
            Node3D importedScene,
            List<MeshInstance3D> importedMeshes,
            Node3D heroNode,
            Skeleton3D heroSkeleton,
            Skin heroSkin,
            RiggingOptions options)
        {
            var result = new RiggingResult
            {
                Success = true,
                RiggedHeroRoot = heroNode,
                WasAlreadyRigged = false
            };

            // 1. Build skin bind lookup: maps Skeleton3D bone index -> Skin bind index
            var (skeletonToBindMap, bindToSkeletonMap) = BuildSkinIndexMappings(heroSkeleton, heroSkin);

            // 2. Pose hero skeleton if T-Pose or unrigged calibration requested, to create pose-matched donor cloud
            Dictionary<int, Quaternion> originalLocalRotations = BackupSkeletonLocalRotations(heroSkeleton);
            heroSkeleton.ResetBonePoses(); // Force 100% symmetrical bind pose before arm adjustments!

            Transform3D[] posedGlobalBoneTransforms = new Transform3D[heroSkeleton.GetBoneCount()];
            Transform3D[] restGlobalBoneTransforms = new Transform3D[heroSkeleton.GetBoneCount()];

            for (int b = 0; b < heroSkeleton.GetBoneCount(); b++)
            {
                restGlobalBoneTransforms[b] = heroSkeleton.GetBoneGlobalRest(b);
            }

            // In Partial-Body Hero Transfer mode, automatically apply custom limb angle sliders even if Preset was left at MatchHeroBindPose
            var prevPresetForPartial = options.Preset;
            bool forcedPartialAnglePreset = options.Method == RiggingMethod.PartialBodyAutoRig &&
                                            options.Preset == PosePreset.MatchHeroBindPose &&
                                            (MathF.Abs(options.CustomArmAngleDeg) > 0.1f ||
                                             MathF.Abs(options.CustomArmForwardDeg) > 0.1f ||
                                             MathF.Abs(options.CustomElbowStraightenPct) > 0.1f ||
                                             MathF.Abs(options.CustomLegSpreadDeg) > 0.1f);
            if (forcedPartialAnglePreset) options.Preset = PosePreset.APoseCustomAngle;
            bool isPoseMatched = PoseHeroSkeletonForOptions(heroSkeleton, options);
            if (forcedPartialAnglePreset) options.Preset = prevPresetForPartial;

            for (int b = 0; b < heroSkeleton.GetBoneCount(); b++)
            {
                posedGlobalBoneTransforms[b] = heroSkeleton.GetBoneGlobalPose(b);
            }

            // 3. Extract donor vertices from Deadlock hero meshes using CPU LBS in posed space
            var swDonor = System.Diagnostics.Stopwatch.StartNew();
            List<DonorVertex> donorCloud = ExtractHeroDonorVertices(heroNode, heroSkeleton, restGlobalBoneTransforms, posedGlobalBoneTransforms, bindToSkeletonMap, options);
            swDonor.Stop();
            if (donorCloud.Count == 0)
            {
                result.Success = false;
                result.ErrorMessage = "Failed to extract reference donor vertices from Deadlock hero.";
                RestoreSkeletonLocalRotations(heroSkeleton, originalLocalRotations);
                return result;
            }

            if (options.KeepFingersRigidOnHand)
            {
                CollapseDonorFingerWeights(donorCloud, heroSkeleton);
            }

            // Build anatomical region tags on all donor vertices to prevent coat skirts / torso from
            // contaminating hanging arms or thighs in tight proximity.
            var boneRegions = BuildBoneRegionLookup(heroSkeleton);
            for (int d = 0; d < donorCloud.Count; d++)
            {
                var dv = donorCloud[d];
                if (dv.BoneIndices != null && dv.BoneIndices.Length > 0 && boneRegions.Length > 0)
                {
                    int b0 = dv.BoneIndices[0];
                    dv.Region = (byte)((b0 >= 0 && b0 < boneRegions.Length) ? boneRegions[b0] : AnatomicalRegion.TorsoHead);
                    donorCloud[d] = dv;
                }
            }

            GD.Print($"[CustomModelAutoRigger] Built donor vertex cloud in {swDonor.ElapsedMilliseconds} ms: {donorCloud.Count} posed donor vertices.");

            // 4. Build Spatial Hash Grid for fast KNN querying
            var swGrid = System.Diagnostics.Stopwatch.StartNew();
            float cellSize = 0.20f; // 20 cm
            var spatialGrid = BuildSpatialHashGrid(donorCloud, cellSize);
            swGrid.Stop();

            // 5. Build core anatomical capsule segments for fallback & regional corridor isolation
            var swCap = System.Diagnostics.Stopwatch.StartNew();
            var capsuleSegments = BuildCapsuleSegments(heroSkeleton, posedGlobalBoneTransforms, options);
            var capsuleRegions = BuildCapsuleRegionLookup(capsuleSegments, boneRegions);
            swCap.Stop();
            GD.Print($"[CustomModelAutoRigger] Grid={swGrid.ElapsedMilliseconds} ms, Capsules={swCap.ElapsedMilliseconds} ms ({capsuleSegments.Count} segments).");

            // 6. Process each imported mesh
            var containerNode = new Node3D { Name = GetContainerNameForSlot(options?.SlotId) };
            heroNode.AddChild(containerNode);
            result.ContainerNode = containerNode;

            int totalVerts = 0;
            int totalTris = 0;
            long totalKnnMs = 0, totalLapMs = 0, totalBakeMs = 0, totalMeshMs = 0;

            var socketFrames = ComputeSocketGroupFrames(importedMeshes, importedScene, options, heroNode, heroSkeleton);
            Transform3D heroSkelToHero = ComputeTransformRelativeToRoot(heroSkeleton, heroNode);
            LimbTwistProfile limbTwistProfile = options.AutoCounterTwistWeights
                ? BuildLimbTwistProfile(heroSkeleton, restGlobalBoneTransforms, heroSkelToHero)
                : null;

            Skeleton3D srcFallbackSkel = FindSkeleton(importedScene);
            foreach (var srcMi in importedMeshes)
            {
                var srcMesh = srcMi.Mesh;
                if (srcMesh == null || srcMesh.GetSurfaceCount() == 0) continue;

                // Deterministic mesh transform relative to imported root (Works without being inside scene tree!)
                Transform3D toHeroSpace = ComputeImportedMeshToHeroTransform(srcMi, importedScene, options.AlignedTransform);

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

                    // Transform vertices and normals into the posed Hero space (evaluating skin rest pose when skinned)
                    EvaluateMeshSurfaceInTargetSpace(srcMi, importedScene, options.AlignedTransform, srcFallbackSkel, rawVerts, rawNorms, rawBones, rawWeights, out Vector3[] alignedVerts, out Vector3[] alignedNorms);

                    // Check if this submesh is designated as a rigid socket attachment (e.g. weapon / prop attached to hand_R)
                    string socketBoneName = null;
                    bool isSocketProp = options.SubmeshAttachmentSockets != null &&
                                        options.SubmeshAttachmentSockets.TryGetValue(srcMi.Name.ToString(), out socketBoneName) &&
                                        !string.IsNullOrEmpty(socketBoneName);

                    int[] boneIndices = new int[vCount * 4];
                    float[] boneWeights = new float[vCount * 4];
                    Skin propSkin = null;

                    var swKnnStep = System.Diagnostics.Stopwatch.StartNew();
                    SocketBakeMeta? socketMeta = null;
                    if (isSocketProp)
                    {
                        var baked = BakeSocketPropSurface(
                            heroSkeleton,
                            socketBoneName,
                            srcMi.Name.ToString(),
                            options,
                            socketFrames,
                            rawVerts,
                            rawNorms,
                            alignedVerts,
                            alignedNorms,
                            boneIndices,
                            boneWeights,
                            toHeroSpace);
                        propSkin = baked.PropSkin;
                        socketMeta = baked.Meta;
                    }

                    bool hasSurfaceOverride = !isSocketProp &&
                        TryApplySkinWeightOverrideSkeletonIndices(
                            options, srcMi.Name.ToString(), s, vCount, heroSkeleton, boneIndices, boneWeights);

                    if (!isSocketProp && !hasSurfaceOverride)
                    {
                        System.Threading.Tasks.Parallel.For(0, vCount, v =>
                        {
                            QueryWeightsWithCapsuleFallback(
                                alignedVerts[v],
                                spatialGrid,
                                donorCloud,
                                capsuleSegments,
                                capsuleRegions,
                                options.KNearest,
                                options.SearchRadius,
                                cellSize,
                                boneIndices,
                                boneWeights,
                                v * 4);
                        });
                    }
                    swKnnStep.Stop();
                    totalKnnMs += swKnnStep.ElapsedMilliseconds;

                    // Laplacian weight smoothing pass across triangle adjacency with UV seam welding
                    if (!isSocketProp && !hasSurfaceOverride && options.SmoothingIterations > 0 && rawIndices != null && rawIndices.Length >= 3)
                    {
                        var swLapStep = System.Diagnostics.Stopwatch.StartNew();
                        SmoothWeightsLaplacian(rawIndices, vCount, boneIndices, boneWeights, options.SmoothingIterations, alignedVerts);
                        swLapStep.Stop();
                        totalLapMs += swLapStep.ElapsedMilliseconds;
                    }

                    // Stabilize neck weights against floating-head stretch (e.g. Paradox/Chrono)
                    if (!isSocketProp && !hasSurfaceOverride && options.StabilizeNeckFloatingHead)
                    {
                        StabilizeNeckFloatingHeadWeights(heroSkeleton, vCount, boneIndices, boneWeights, alignedVerts);
                    }

                    // Bake T-Pose vertices & normals back into Deadlock's native Rest / Bind Pose
                    Vector3[] restVerts = new Vector3[vCount];
                    Vector3[] restNorms = new Vector3[vCount];

                    var swBakeStep = System.Diagnostics.Stopwatch.StartNew();
                    if (!isSocketProp && isPoseMatched)
                    {
                        BakeInversePose(alignedVerts, alignedNorms, boneIndices, boneWeights, restGlobalBoneTransforms, posedGlobalBoneTransforms, restVerts, restNorms);
                    }
                    else
                    {
                        Array.Copy(alignedVerts, restVerts, vCount);
                        Array.Copy(alignedNorms, restNorms, vCount);
                    }
                    swBakeStep.Stop();
                    totalBakeMs += swBakeStep.ElapsedMilliseconds;

                    // Redistribute proximal limb weights onto procedural counter-twist bones (*_TWIST / *_TWIST1),
                    // soften sharp 1-bone hip seams, and prune stray distal ankle/hand bleeds in hero rest space.
                    if (!isSocketProp && !hasSurfaceOverride && options.AutoCounterTwistWeights)
                    {
                        AdjustLimbTwistAndHipWeights(heroSkeleton, vCount, boneIndices, boneWeights, restVerts, rawIndices, limbTwistProfile);
                    }

                    // Phase 2: Make manual remap & disabled bones authoritative in KNN transfer too
                    if (!isSocketProp && !hasSurfaceOverride)
                    {
                        ApplyAuthoritativeKnnBoneRemap(heroSkeleton, vCount, boneIndices, boneWeights, options.CustomBoneRemap, options.DisabledSourceBones);
                    }
                    else if (isSocketProp)
                    {
                        result.Report.Stats.RigidPartCount++;
                    }

                    // Remap bone indices from Skeleton3D indices to Skin bind indices
                    int[] skinBoneIndices = new int[vCount * 4];
                    for (int i = 0; i < vCount * 4; i++)
                    {
                        int skelBone = boneIndices[i];
                        if (!skeletonToBindMap.TryGetValue(skelBone, out int bindIdx))
                        {
                            bindIdx = GetOrAddSkinBindIndex(heroSkin, heroSkeleton, skelBone);
                            skeletonToBindMap[skelBone] = bindIdx;
                        }
                        skinBoneIndices[i] = bindIdx;
                    }

                    // Build Godot ArrayMesh surface
                    var swMeshStep = System.Diagnostics.Stopwatch.StartNew();
                    var surfaceArrays = new Godot.Collections.Array();
                    surfaceArrays.Resize((int)Mesh.ArrayType.Max);
                    surfaceArrays[(int)Mesh.ArrayType.Vertex] = restVerts;
                    surfaceArrays[(int)Mesh.ArrayType.Normal] = restNorms;
                    if (rawUvs != null && rawUvs.Length == vCount)
                    {
                        surfaceArrays[(int)Mesh.ArrayType.TexUV] = rawUvs;
                    }
                    if (rawIndices != null && rawIndices.Length > 0)
                    {
                        surfaceArrays[(int)Mesh.ArrayType.Index] = rawIndices;
                    }
                    surfaceArrays[(int)Mesh.ArrayType.Bones] = skinBoneIndices;
                    surfaceArrays[(int)Mesh.ArrayType.Weights] = boneWeights;

                    var newMesh = new ArrayMesh();
                    newMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, surfaceArrays);

                    // Material handling: preserve original or create standard PBR
                    Material surfMat = srcMesh.SurfaceGetMaterial(s) ?? srcMi.GetSurfaceOverrideMaterial(s);
                    if (surfMat == null)
                    {
                        surfMat = CreateDefaultPbrMaterial(srcMi.Name + $"_surf_{s}");
                    }
                    newMesh.SurfaceSetMaterial(0, surfMat);

                    string meshPartName = $"{srcMi.Name}_surf{s}";
                    var newMi = new MeshInstance3D
                    {
                        Name = meshPartName,
                        Mesh = newMesh,
                        Skin = (isSocketProp && propSkin != null) ? propSkin : heroSkin,
                        Transform = Transform3D.Identity
                    };

                    newMi.Layers &= ~(uint)(1 << 20); // Clear bit 20 (CameraBrush isolation)
                    newMi.SetMeta("IsCustomImportedMesh", true);
                    newMi.SetMeta("SourceMeshName", srcMi.Name.ToString());
                    newMi.SetMeta("SourceSurfaceIndex", s);
                    newMi.SetMeta("OriginalMaterial_0", surfMat);
                    ConfigureRiggedSubmeshMetadata(newMi, srcMi, surfMat, options, heroNode);
                    if (socketMeta.HasValue) ApplySocketMetadata(newMi, srcMi.Name.ToString(), socketMeta.Value);

                    containerNode.AddChild(newMi);
                    newMi.Skeleton = newMi.GetPathTo(heroSkeleton);
                    result.CustomSubmeshes.Add(newMi);
                    swMeshStep.Stop();
                    totalMeshMs += swMeshStep.ElapsedMilliseconds;
                }
            }

            GD.Print($"[CustomModelAutoRigger] Timings Breakdown: KNN={totalKnnMs} ms, Laplacian={totalLapMs} ms, Bake={totalBakeMs} ms, GodotMesh={totalMeshMs} ms");

            // Restore hero skeleton rotations back to original
            RestoreSkeletonLocalRotations(heroSkeleton, originalLocalRotations);

            result.TotalVertices = totalVerts;
            result.TotalTriangles = totalTris;
            GD.Print($"[CustomModelAutoRigger] Unrigged auto-rigging complete: {totalVerts} verts, {totalTris} tris bound to {heroSkeleton.Name}.");

            return result;
        }

        #endregion

        #region Case B: Pre-Rigged Model Remapping

        private static RiggingResult RigPreRiggedModel(
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

            int srcBoneCount = sourceSkeleton.GetBoneCount();
            var srcNames = new string[srcBoneCount];
            var srcParents = new int[srcBoneCount];
            var srcWorldPos = new Vector3[srcBoneCount];
            Transform3D skelToHero = options.AlignedTransform * ComputeTransformRelativeToRoot(sourceSkeleton, importedScene);
            for (int sb = 0; sb < srcBoneCount; sb++)
            {
                srcNames[sb] = sourceSkeleton.GetBoneName(sb);
                srcParents[sb] = sourceSkeleton.GetBoneParent(sb);
                srcWorldPos[sb] = skelToHero * sourceSkeleton.GetBoneGlobalRest(sb).Origin;
            }

            var weightedSrcBones = RigBoneClassifier.ComputeWeightedBoneIndices(sourceSkeleton, importedMeshes);
            var analysis = RigBoneClassifier.Analyze(srcNames, srcParents, options.RigFamilyOverride, weightedSrcBones, srcWorldPos);
            result.Report.DetectedFamily = analysis.DetectedFamily;
            result.Report.EffectiveFamily = analysis.Family;
            result.Report.FamilyConfidence = analysis.Confidence;

            var disabledIdxSet = new HashSet<int>();
            if (options.DisabledSourceBones != null && options.DisabledSourceBones.Count > 0)
            {
                for (int sb = 0; sb < srcBoneCount; sb++)
                {
                    if (options.DisabledSourceBones.Contains(srcNames[sb]))
                        disabledIdxSet.Add(sb);
                }
            }

            bool[] keepMask = RigBoneClassifier.ComputeExportKeepMask(analysis, weightedSrcBones, options.KeepUnweightedBones, disabledIdxSet);

            // 1. Map source skeleton bones to Deadlock hero skeleton bones using HumanoidBoneMapper, RigBoneClassifier, and authoritative user overrides
            Dictionary<int, int> sourceToTargetBoneMap = BuildHumanoidBoneMap(
                sourceSkeleton, targetSkeleton, options.CustomBoneRemap, analysis, options.DisabledSourceBones);

            LogEffectiveBoneRemap(sourceSkeleton, targetSkeleton, sourceToTargetBoneMap, options.CustomBoneRemap, result.Report);
            GD.Print($"[CustomModelAutoRigger] Mapped {sourceToTargetBoneMap.Count} of {srcBoneCount} bones (Family={analysis.Family}, Confidence={analysis.Confidence:P0}) to hero skeleton.");

            var containerNode = new Node3D { Name = GetContainerNameForSlot(options?.SlotId) };
            heroNode.AddChild(containerNode);
            result.ContainerNode = containerNode;

            // 2. Build Hero-Space Bone-Segment Registrations (Arms, Legs/Feet, Neck/Head)
            bool keepFingersRigid = options.KeepFingersRigidOnHand || options.FingerMode == FingerRigMode.RigidHand;
            bool doSnapArms = options.AutoSnapArmsToHero && options.ConformToSkeleton;
            bool doSnapLegs = options.AutoSnapLegsToHero && options.ConformToSkeleton;
            bool doSnapHead = options.AutoSnapHeadToHero && options.ConformToSkeleton;

            ArmSegmentRegistration leftArm = doSnapArms
                ? BuildArmSegmentRegistration(
                    sourceSkeleton, targetSkeleton, sourceToTargetBoneMap,
                    "arm_upper_L", "arm_lower_L", "hand_L",
                    skelToHero, keepFingersRigid,
                    options.AutoAlignHandOrientation,
                    options.LeftHandSnapOffsetDeg, options.LeftHandSnapScale,
                    importedMeshes, importedScene, options.AlignedTransform)
                : null;
            ArmSegmentRegistration rightArm = doSnapArms
                ? BuildArmSegmentRegistration(
                    sourceSkeleton, targetSkeleton, sourceToTargetBoneMap,
                    "arm_upper_R", "arm_lower_R", "hand_R",
                    skelToHero, keepFingersRigid,
                    options.AutoAlignHandOrientation,
                    options.RightHandSnapOffsetDeg, options.RightHandSnapScale,
                    importedMeshes, importedScene, options.AlignedTransform)
                : null;

            LegSegmentRegistration leftLeg = doSnapLegs
                ? BuildLegSegmentRegistration(sourceSkeleton, targetSkeleton, sourceToTargetBoneMap, "leg_upper_L", "leg_lower_L", "ankle_L", skelToHero)
                : null;
            LegSegmentRegistration rightLeg = doSnapLegs
                ? BuildLegSegmentRegistration(sourceSkeleton, targetSkeleton, sourceToTargetBoneMap, "leg_upper_R", "leg_lower_R", "ankle_R", skelToHero)
                : null;

            HeadSegmentRegistration headSeg = doSnapHead
                ? BuildHeadSegmentRegistration(sourceSkeleton, targetSkeleton, sourceToTargetBoneMap, "neck_0", "head", skelToHero)
                : null;

            int pelvisBone = targetSkeleton.FindBone("pelvis");
            int srcPelvis = FindSourceBoneForTarget(sourceToTargetBoneMap, pelvisBone);
            Vector3 pelvisOffset = Vector3.Zero;
            if (doSnapLegs && pelvisBone != -1 && srcPelvis != -1)
            {
                Vector3 PsPelvis = skelToHero * sourceSkeleton.GetBoneGlobalRest(srcPelvis).Origin;
                Vector3 PtPelvis = targetSkeleton.GetBoneGlobalRest(pelvisBone).Origin;
                Vector3 rawPelvisDelta = (PtPelvis - PsPelvis) * 0.5f;
                if (rawPelvisDelta.Length() > 0.45f)
                    rawPelvisDelta = rawPelvisDelta.Normalized() * 0.45f;
                pelvisOffset = rawPelvisDelta;
            }

            // 2b. Phase 2: Detect & apply per-chain clothing/accessory bone preservation policies
            var mappedSourceSet = new HashSet<int>(sourceToTargetBoneMap.Keys);
            var detectedChains = RigBoneClassifier.FindExtraChains(analysis, mappedSourceSet, weightedSrcBones);
            string activeSlotKey = string.IsNullOrWhiteSpace(options?.SlotId) ? "__main__" : options.SlotId;

            if (options.BoneChains == null) options.BoneChains = new List<BoneChainDef>();
            var existingByRoot = new Dictionary<string, BoneChainDef>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in options.BoneChains)
            {
                if (c != null && !c.IsSynthetic && !string.IsNullOrEmpty(c.RootSourceBone))
                    existingByRoot[c.RootSourceBone] = c;
            }

            // Guarantee every explicitly authored ReferenceRig cloth chain (refcloth_*) is preserved even if the user
            // named it something RigBoneClassifier categorizes as Face/Core/Twist/Helper (e.g. "cheek_hair", "chest_flap").
            var detectedRoots = new HashSet<string>(detectedChains.Select(d => d.RootName), StringComparer.OrdinalIgnoreCase);
            if (string.Equals(sourceSkeleton.Name.ToString(), "LandmarkSyntheticSkeleton", StringComparison.Ordinal))
            {
                foreach (var ac in options.BoneChains)
                {
                    if (ac == null || ac.IsSynthetic || string.IsNullOrEmpty(ac.ChainId) ||
                        !ac.ChainId.StartsWith("refcloth_", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrEmpty(ac.RootSourceBone) || detectedRoots.Contains(ac.RootSourceBone))
                        continue;

                    var idxs = new List<int>();
                    foreach (var bn in ac.SourceBoneNames)
                    {
                        int sIdx = sourceSkeleton.FindBone(bn);
                        if (sIdx >= 0 && !mappedSourceSet.Contains(sIdx))
                            idxs.Add(sIdx);
                    }
                    if (idxs.Count == 0) continue;
                    var syntheticDc = new ExtraBoneChain
                    {
                        RootIndex = idxs[0],
                        RootName = srcNames[idxs[0]],
                        Category = string.IsNullOrWhiteSpace(ac.Category) ? RigBoneClassifier.CatClothing : ac.Category
                    };
                    syntheticDc.BoneIndices.AddRange(idxs);
                    detectedChains.Add(syntheticDc);
                    detectedRoots.Add(syntheticDc.RootName);
                }
            }

            var resolvedChains = new List<BoneChainDef>();
            var newlyDetectedChains = new List<BoneChainDef>();
            foreach (var dc in detectedChains)
            {
                if (existingByRoot.TryGetValue(dc.RootName, out var userChain))
                {
                    userChain.SourceBoneNames = dc.BoneIndices.Select(i => srcNames[i]).ToList();
                    if (string.IsNullOrWhiteSpace(userChain.AnchorHeroBone))
                        userChain.AnchorHeroBone = RigBoneClassifier.ResolveAnchorHeroBoneName(analysis, dc, sourceToTargetBoneMap, targetSkeleton);
                    resolvedChains.Add(userChain);
                }
                else
                {
                    var def = new BoneChainDef
                    {
                        ChainId = $"chain_{dc.RootName}",
                        Name = dc.DisplayName,
                        Category = dc.Category,
                        RootSourceBone = dc.RootName,
                        SourceBoneNames = dc.BoneIndices.Select(i => srcNames[i]).ToList(),
                        AnchorHeroBone = RigBoneClassifier.ResolveAnchorHeroBoneName(analysis, dc, sourceToTargetBoneMap, targetSkeleton),
                        SlotId = options.SlotId ?? ""
                    };
                    options.BoneChains.Add(def);
                    resolvedChains.Add(def);
                    newlyDetectedChains.Add(def);
                }
            }
            if (newlyDetectedChains.Count > 0)
            {
                RigBoneClassifier.ApplyRecommendedPhysicsModes(
                    newlyDetectedChains,
                    enableCustomPhysics: options.PreserveSourceClothingChains && options.EnableCustomPhysics,
                    resetTuningValues: true);
            }

            var preservedSrcToAppendedTgt = new Dictionary<int, int>();
            var droppedSrcBones = new HashSet<int>();
            foreach (var chain in resolvedChains)
            {
                chain.AppendedHeroBoneNames.Clear();
                bool anyDisabled = chain.SourceBoneNames.All(n => options.DisabledSourceBones != null && options.DisabledSourceBones.Contains(n));
                if (anyDisabled || chain.Policy == ChainPolicy.Drop)
                {
                    foreach (var bn in chain.SourceBoneNames)
                    {
                        int sIdx = sourceSkeleton.FindBone(bn);
                        if (sIdx >= 0) droppedSrcBones.Add(sIdx);
                    }
                    continue;
                }

                if (options.EnableCustomPhysics && chain.Policy == ChainPolicy.PreservePhysicsChain)
                {
                    AppendPreservedSourceChainToHeroSkeleton(
                        sourceSkeleton, skelToHero, targetSkeleton, heroSkin,
                        chain, activeSlotKey, options.DisabledSourceBones,
                        preservedSrcToAppendedTgt, result.Report, headSeg, pelvisOffset);
                    if (result.Report != null)
                        result.Report.Stats.ClothBoneCount += chain.AppendedHeroBoneNames.Count;
                }
            }

            int tgtTwistL = FindBoneAny(targetSkeleton, "arm_lower_L_TWIST", "arm_lower_twist_L", "arm_lower_l_twist");
            if (tgtTwistL == -1) tgtTwistL = EnsureForearmTwistBone(targetSkeleton, isLeft: true);
            int tgtTwistR = FindBoneAny(targetSkeleton, "arm_lower_R_TWIST", "arm_lower_twist_R", "arm_lower_r_twist");
            if (tgtTwistR == -1) tgtTwistR = EnsureForearmTwistBone(targetSkeleton, isLeft: false);

            if (heroSkin != null)
            {
                EnsureSkinHasBind(heroSkin, targetSkeleton, tgtTwistL);
                EnsureSkinHasBind(heroSkin, targetSkeleton, tgtTwistR);
            }

            // Refresh skin bind mappings AFTER appending preserved chains and forearm twist bones
            // so newly created twist bones always resolve to their own Skin bind index (never bind 0).
            var (skeletonToBindMap, _) = BuildSkinIndexMappings(targetSkeleton, heroSkin);

            // Precompute thread-safe managed lookups BEFORE entering Parallel.For (eliminates CowData race crashes on Skeleton3D)
            int[] srcBoneToResolvedTgt = new int[srcBoneCount];
            for (int sb = 0; sb < srcBoneCount; sb++)
            {
                if (preservedSrcToAppendedTgt.TryGetValue(sb, out int appIdx))
                {
                    srcBoneToResolvedTgt[sb] = appIdx;
                }
                else if (disabledIdxSet.Contains(sb) || droppedSrcBones.Contains(sb))
                {
                    // Collapse disabled/dropped bone onto nearest enabled mapped ancestor
                    srcBoneToResolvedTgt[sb] = ResolveTargetBoneWithAncestorFallback(sb, sourceSkeleton, sourceToTargetBoneMap, targetSkeleton, keepFingersRigid, disabledIdxSet);
                }
                else
                {
                    srcBoneToResolvedTgt[sb] = ResolveTargetBoneWithAncestorFallback(sb, sourceSkeleton, sourceToTargetBoneMap, targetSkeleton, keepFingersRigid, disabledIdxSet);
                }

                // Populate ImportReport bone entry
                var bc = analysis.Bones[sb];
                int resolvedTgt = srcBoneToResolvedTgt[sb];
                string tgtName = (resolvedTgt >= 0 && resolvedTgt < targetSkeleton.GetBoneCount()) ? targetSkeleton.GetBoneName(resolvedTgt) : "";
                BoneImportDisposition disp;
                if (disabledIdxSet.Contains(sb)) disp = BoneImportDisposition.Disabled;
                else if (droppedSrcBones.Contains(sb)) disp = BoneImportDisposition.Dropped;
                else if (preservedSrcToAppendedTgt.ContainsKey(sb)) disp = BoneImportDisposition.Appended;
                else if (sourceToTargetBoneMap.ContainsKey(sb)) disp = BoneImportDisposition.Mapped;
                else if (!keepMask[sb]) disp = BoneImportDisposition.Dropped;
                else disp = BoneImportDisposition.Collapsed;

                result.Report.Bones.Add(new BoneReportEntry
                {
                    SourceIndex = sb,
                    SourceName = bc.Name,
                    Kind = bc.Kind,
                    Category = bc.Category,
                    Disposition = disp,
                    TargetBone = tgtName,
                    Reason = bc.Reason,
                    HasWeights = weightedSrcBones.Contains(sb)
                });
            }

            int tgtBoneCount = targetSkeleton.GetBoneCount();
            int origCoreBoneCount = targetSkeleton.HasMeta("OriginalHeroBoneCount")
                ? targetSkeleton.GetMeta("OriginalHeroBoneCount").AsInt32()
                : tgtBoneCount;
            // 0 = none, 1 = leftArm, 2 = rightArm, 3 = leftLeg, 4 = rightLeg, 5 = headSeg, 6 = pelvis, 7 = lowerSpine (0.60x), 8 = midSpine (0.25x)
            int spine0Bone = targetSkeleton.FindBone("spine_0");
            int spine1Bone = targetSkeleton.FindBone("spine_1");
            bool hasMappedSpine0 = spine0Bone >= 0 && FindSourceBoneForTarget(sourceToTargetBoneMap, spine0Bone) >= 0;
            byte[] tgtSegmentKind = new byte[tgtBoneCount];
            for (int tb = 0; tb < tgtBoneCount; tb++)
            {
                if (leftArm != null && leftArm.AppliesTo(tb, targetSkeleton)) tgtSegmentKind[tb] = 1;
                else if (rightArm != null && rightArm.AppliesTo(tb, targetSkeleton)) tgtSegmentKind[tb] = 2;
                else if (leftLeg != null && leftLeg.AppliesTo(tb, targetSkeleton)) tgtSegmentKind[tb] = 3;
                else if (rightLeg != null && rightLeg.AppliesTo(tb, targetSkeleton)) tgtSegmentKind[tb] = 4;
                else if (headSeg != null && headSeg.AppliesTo(tb, targetSkeleton)) tgtSegmentKind[tb] = 5;
                else
                {
                    // Walk appended/synthetic bones (e.g. skirt chains anchored to pelvis/spine_0/spine_1)
                    // up to their hero anchor so painted overrides inherit the exact same alignment offset.
                    int anchor = tb;
                    int guard = 0;
                    while (anchor >= origCoreBoneCount && guard++ < 32)
                    {
                        int p = targetSkeleton.GetBoneParent(anchor);
                        if (p < 0) break;
                        anchor = p;
                    }
                    if (anchor == pelvisBone) tgtSegmentKind[tb] = 6;
                    else if (anchor == spine0Bone) tgtSegmentKind[tb] = 7;
                    else if (anchor == spine1Bone) tgtSegmentKind[tb] = hasMappedSpine0 ? (byte)8 : (byte)7;
                }
            }

            int headBonePre = targetSkeleton.FindBone("head");
            int neckBonePre = targetSkeleton.FindBone("neck_0");
            if (neckBonePre < 0) neckBonePre = targetSkeleton.FindBone("neck");
            int spine3BonePre = targetSkeleton.FindBone("spine_3");
            if (spine3BonePre < 0) spine3BonePre = targetSkeleton.FindBone("spine_2");
            int spine2BonePre = targetSkeleton.FindBone("spine_2");

            Transform3D targetSkelToHero = ComputeTransformRelativeToRoot(targetSkeleton, heroNode);
            LimbTwistProfile limbTwistProfile = options.AutoCounterTwistWeights
                ? BuildLimbTwistProfile(targetSkeleton, null, targetSkelToHero)
                : null;

            int totalVerts = 0;
            int totalTris = 0;

            var socketFrames = ComputeSocketGroupFrames(importedMeshes, importedScene, options, heroNode, targetSkeleton);

            var pendingSurfaces = new List<(
                MeshInstance3D SrcMi,
                Mesh SrcMesh,
                int SurfaceIndex,
                Vector3[] AlignedVerts,
                Vector3[] AlignedNorms,
                Vector2[] RawUvs,
                int[] RawIndices,
                int[] SkelBoneIndices,
                float[] OutWeights,
                bool IsSocketProp,
                bool HasSurfaceOverride,
                Skin PropSkin,
                SocketBakeMeta? SocketMeta)>();

            foreach (var srcMi in importedMeshes)
            {
                var srcMesh = srcMi.Mesh;
                if (srcMesh == null || srcMesh.GetSurfaceCount() == 0) continue;

                // Deterministic mesh transform relative to imported root (Works without being inside scene tree!)
                Transform3D toHeroSpace = ComputeImportedMeshToHeroTransform(srcMi, importedScene, options.AlignedTransform);

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

                    Vector3[] alignedVerts = new Vector3[vCount];
                    Vector3[] alignedNorms = new Vector3[vCount];
                    bool[] wristMask = new bool[vCount];
                    int wristCount = 0;

                    // Check if this submesh is designated as a rigid socket attachment (e.g. weapon / prop attached to hand_R)
                    string socketBoneName = null;
                    bool isSocketProp = options.SubmeshAttachmentSockets != null &&
                                        options.SubmeshAttachmentSockets.TryGetValue(srcMi.Name.ToString(), out socketBoneName) &&
                                        !string.IsNullOrEmpty(socketBoneName);


                    int[] skelBoneIndices = new int[vCount * 4];
                    float[] outWeights = new float[vCount * 4];
                    Skin propSkin = null;
                    SocketBakeMeta? socketMeta = null;
                    bool hasSurfaceOverride = false;

                    if (isSocketProp)
                    {
                        // Evaluate accurate rest-pose geometry in Hero space before optional socket offsets
                        EvaluateMeshSurfaceInTargetSpace(srcMi, importedScene, options.AlignedTransform, sourceSkeleton, rawVerts, rawNorms, rawBones, rawWeights, out Vector3[] sockVerts, out Vector3[] sockNorms, useExplicitSkeleton: true);

                        var baked = BakeSocketPropSurface(
                            targetSkeleton,
                            socketBoneName,
                            srcMi.Name.ToString(),
                            options,
                            socketFrames,
                            sockVerts,
                            sockNorms,
                            alignedVerts,
                            alignedNorms,
                            skelBoneIndices,
                            outWeights,
                            localToHeroSpace: null,
                            hasInputNormals: rawNorms != null && rawNorms.Length == vCount);
                        propSkin = baked.PropSkin;
                        socketMeta = baked.Meta;
                    }
                    else
                    {
                        EvaluateMeshSurfaceInTargetSpace(srcMi, importedScene, options.AlignedTransform, sourceSkeleton, rawVerts, rawNorms, rawBones, rawWeights, out Vector3[] baseHeroVerts, out Vector3[] baseHeroNorms, useExplicitSkeleton: true);

                        int srcStride = (rawBones != null && rawWeights != null && vCount > 0 && rawBones.Length >= vCount * 8 && rawWeights.Length >= vCount * 8) ? 8 : 4;
                        int[] srcBindToSkel = null;
                        if (srcMi.Skin != null && srcMi.Skin.GetBindCount() > 0)
                        {
                            int sbCount = srcMi.Skin.GetBindCount();
                            srcBindToSkel = new int[sbCount];
                            for (int bi = 0; bi < sbCount; bi++)
                            {
                                srcBindToSkel[bi] = ResolveSkinBindBone(srcMi.Skin, bi, sourceSkeleton);
                            }
                        }

                        SkinWeightOverride surfaceOverride = null;
                        hasSurfaceOverride = options.CustomSkinWeightOverrides != null &&
                            options.CustomSkinWeightOverrides.TryGetValue(MakeSurfaceKeepKey(srcMi.Name.ToString(), s), out surfaceOverride) &&
                            surfaceOverride != null &&
                            surfaceOverride.VertexCount == vCount &&
                            surfaceOverride.PaletteIndices != null && surfaceOverride.PaletteIndices.Length >= vCount * 4 &&
                            surfaceOverride.Weights != null && surfaceOverride.Weights.Length >= vCount * 4;

                        int[] ovPaletteToTgt = null;
                        if (hasSurfaceOverride && surfaceOverride.BonePalette != null)
                        {
                            ovPaletteToTgt = new int[surfaceOverride.BonePalette.Length];
                            for (int p = 0; p < surfaceOverride.BonePalette.Length; p++)
                            {
                                // Keep -1 when a bone was deleted (e.g. deleted synthetic cloth chain) so we NEVER dump its weight onto bone 0 (root_motion)!
                                ovPaletteToTgt[p] = FindBoneIndexCaseInsensitive(targetSkeleton, surfaceOverride.BonePalette[p]);
                            }
                        }

                        bool isUnskinnedSurface = rawBones == null || rawWeights == null || rawBones.Length == 0 || rawWeights.Length == 0;
                        int unskinnedFallbackTgtBone = 0;
                        if ((isUnskinnedSurface || hasSurfaceOverride) && vCount > 0)
                        {
                            Vector3 surfCenter = Vector3.Zero;
                            float surfMaxY = float.MinValue;
                            for (int vi = 0; vi < vCount; vi++)
                            {
                                surfCenter += baseHeroVerts[vi];
                                if (baseHeroVerts[vi].Y > surfMaxY) surfMaxY = baseHeroVerts[vi].Y;
                            }
                            surfCenter /= vCount;
                            unskinnedFallbackTgtBone = FindNearestCoreHeroBone(targetSkeleton, surfCenter, surfMaxY);
                        }

                        System.Threading.Tasks.Parallel.For(0, vCount, v =>
                        {
                            Vector3 vHero = baseHeroVerts[v];
                            Vector3 nHero = baseHeroNorms[v];

                            Span<int> topBones = stackalloc int[4];
                            Span<float> topWeights = stackalloc float[4];

                            if (hasSurfaceOverride)
                            {
                                int baseOff = v * 4;
                                float sumOv = 0f;
                                for (int k = 0; k < 4; k++)
                                {
                                    int pIdx = surfaceOverride.PaletteIndices[baseOff + k];
                                    int tb = (ovPaletteToTgt != null && pIdx >= 0 && pIdx < ovPaletteToTgt.Length) ? ovPaletteToTgt[pIdx] : -1;
                                    float w = tb >= 0 ? Math.Max(0f, surfaceOverride.Weights[baseOff + k]) : 0f;
                                    topBones[k] = tb >= 0 ? tb : 0;
                                    topWeights[k] = w;
                                    sumOv += w;
                                }
                                if (sumOv > 0.0001f)
                                {
                                    for (int k = 0; k < 4; k++) topWeights[k] /= sumOv;
                                }
                                else
                                {
                                    topBones[0] = unskinnedFallbackTgtBone; topWeights[0] = 1f;
                                    topBones[1] = 0; topWeights[1] = 0f;
                                    topBones[2] = 0; topWeights[2] = 0f;
                                    topBones[3] = 0; topWeights[3] = 0f;
                                }
                            }
                            else if (isUnskinnedSurface)
                            {
                                // Unskinned static submesh inside a pre-rigged model (e.g. Roblox Blender export where the
                                // Head + Hair + Face mesh has skin=None): bind to nearest core hero bone instead of bone 0 (pelvis)!
                                topBones[0] = unskinnedFallbackTgtBone;
                                topWeights[0] = 1.0f;
                                topBones[1] = 0; topWeights[1] = 0f;
                                topBones[2] = 0; topWeights[2] = 0f;
                                topBones[3] = 0; topWeights[3] = 0f;
                            }
                            else
                            {
                                Span<int> localAccumBones = stackalloc int[8];
                                Span<float> localAccumWeights = stackalloc float[8];
                                int accumCount = 0;

                                for (int k = 0; k < srcStride; k++)
                                {
                                    int rawBoneVal = rawBones != null && rawBones.Length > (v * srcStride + k) ? rawBones[v * srcStride + k] : 0;
                                    float w = rawWeights != null && rawWeights.Length > (v * srcStride + k) ? rawWeights[v * srcStride + k] : (k == 0 ? 1f : 0f);

                                    if (w <= 0.0001f) continue;

                                    int srcBone = (srcBindToSkel != null && rawBoneVal >= 0 && rawBoneVal < srcBindToSkel.Length)
                                        ? srcBindToSkel[rawBoneVal]
                                        : Math.Clamp(rawBoneVal, 0, Math.Max(0, srcBoneCount - 1));
                                    int tgtBone = (srcBone >= 0 && srcBone < srcBoneToResolvedTgt.Length) ? srcBoneToResolvedTgt[srcBone] : 0;

                                    int found = -1;
                                    for (int a = 0; a < accumCount; a++)
                                    {
                                        if (localAccumBones[a] == tgtBone) { found = a; break; }
                                    }
                                    if (found != -1)
                                    {
                                        localAccumWeights[found] += w;
                                    }
                                    else if (accumCount < 8)
                                    {
                                        localAccumBones[accumCount] = tgtBone;
                                        localAccumWeights[accumCount] = w;
                                        accumCount++;
                                    }
                                }

                                // Extract top 4 influences in descending order
                                for (int top = 0; top < 4; top++)
                                {
                                    int bestIdx = -1;
                                    float maxVal = 0f;
                                    for (int a = 0; a < accumCount; a++)
                                    {
                                        if (localAccumWeights[a] > maxVal)
                                        {
                                            maxVal = localAccumWeights[a];
                                            bestIdx = a;
                                        }
                                    }
                                    if (bestIdx != -1 && maxVal > 0.0001f)
                                    {
                                        topBones[top] = localAccumBones[bestIdx];
                                        topWeights[top] = maxVal;
                                        localAccumWeights[bestIdx] = -1f; // mark used
                                    }
                                    else
                                    {
                                        topBones[top] = 0;
                                        topWeights[top] = 0f;
                                    }
                                }

                                float sumW = topWeights[0] + topWeights[1] + topWeights[2] + topWeights[3];
                                if (sumW > 0.0001f)
                                {
                                    topWeights[0] /= sumW;
                                    topWeights[1] /= sumW;
                                    topWeights[2] /= sumW;
                                    topWeights[3] /= sumW;
                                }
                                else
                                {
                                    topBones[0] = 0;
                                    topWeights[0] = 1.0f;
                                }

                                // Axial Wrist Twist Gradient Blending (for already-posed source rigs; synthetic pin rigs transform vertices first)
                                bool isSyntheticPinRigPre = options.Method == RiggingMethod.LandmarkAutoRig ||
                                    (options.Method == RiggingMethod.PartialBodyAutoRig && options.PartialEngine == PartialBodyEngine.SemiBodyPins);
                                if (!isSyntheticPinRigPre &&
                                    ApplyWristTwistBlending(vHero, topBones, topWeights, leftArm, rightArm, tgtTwistL, tgtTwistR))
                                {
                                    wristMask[v] = true;
                                    System.Threading.Interlocked.Increment(ref wristCount);
                                }

                                // Stabilize neck weights against floating-head stretch (e.g. Paradox/Chrono)
                                if (options.StabilizeNeckFloatingHead)
                                {
                                    StabilizeNeckFloatingHeadVertex(topBones, topWeights, headBonePre, neckBonePre, spine3BonePre, spine2BonePre, vHero.Y);
                                }
                            }

                            // Assign Skeleton3D bone indices & weights (remapped to Skin bind indices after post-passes)
                            for (int k = 0; k < 4; k++)
                            {
                                skelBoneIndices[v * 4 + k] = topBones[k];
                                outWeights[v * 4 + k] = topWeights[k];
                            }

                            // Pure Hero-Space Linear Blend Alignment (T_arm_upper, T_arm_lower, T_hand, T_body)
                            Vector3 blendedPos = Vector3.Zero;
                            Vector3 blendedNorm = Vector3.Zero;

                            for (int k = 0; k < 4; k++)
                            {
                                float w = topWeights[k];
                                if (w <= 0.0001f) continue;
                                int tgtBone = topBones[k];
                                byte segKind = (tgtBone >= 0 && tgtBone < tgtSegmentKind.Length) ? tgtSegmentKind[tgtBone] : (byte)0;

                                Vector3 tPos;
                                Quaternion tRot;

                                switch (segKind)
                                {
                                    case 1:
                                        (tPos, tRot) = leftArm.Evaluate(tgtBone, null, vHero);
                                        break;
                                    case 2:
                                        (tPos, tRot) = rightArm.Evaluate(tgtBone, null, vHero);
                                        break;
                                    case 3:
                                        (tPos, tRot) = leftLeg.Evaluate(tgtBone, null, vHero);
                                        break;
                                    case 4:
                                        (tPos, tRot) = rightLeg.Evaluate(tgtBone, null, vHero);
                                        break;
                                    case 5:
                                        (tPos, tRot) = headSeg.Evaluate(tgtBone, null, vHero);
                                        break;
                                    case 6:
                                        tPos = vHero + pelvisOffset;
                                        tRot = Quaternion.Identity;
                                        break;
                                    case 7:
                                        tPos = vHero + pelvisOffset * 0.60f;
                                        tRot = Quaternion.Identity;
                                        break;
                                    case 8:
                                        tPos = vHero + pelvisOffset * 0.25f;
                                        tRot = Quaternion.Identity;
                                        break;
                                    default:
                                        tPos = vHero;
                                        tRot = Quaternion.Identity;
                                        break;
                                }

                                blendedPos += w * tPos;
                                blendedNorm += w * (tRot * nHero);
                            }

                            alignedVerts[v] = blendedPos;
                            alignedNorms[v] = blendedNorm.LengthSquared() > 0.0001f ? blendedNorm.Normalized() : nHero;
                        });

                        if (!hasSurfaceOverride && wristCount > 0 && rawIndices != null && rawIndices.Length > 0)
                        {
                            SmoothWeightsLaplacian(rawIndices, vCount, skelBoneIndices, outWeights, 2, alignedVerts, wristMask);
                        }
                    }

                    pendingSurfaces.Add((
                        srcMi, srcMesh, s, alignedVerts, alignedNorms, rawUvs, rawIndices,
                        skelBoneIndices, outWeights, isSocketProp, hasSurfaceOverride, propSkin, socketMeta));
                }
            }

            // Run counter-twist & hip-seam smoothing JOINTLY across all non-socket, non-overridden surfaces so
            // coincident boundary vertices between separate meshes (e.g. shorts/upper-body mesh vs bare-legs mesh)
            // weld and smooth across the seam instead of diverging by 15-20% and ripping open a gap!
            if (options.AutoCounterTwistWeights)
            {
                var twistSurfaces = new List<(Vector3[] Verts, int[] Indices, int[] BoneIndices, float[] BoneWeights)>();
                foreach (var ps in pendingSurfaces)
                {
                    if (!ps.IsSocketProp && !ps.HasSurfaceOverride)
                        twistSurfaces.Add((ps.AlignedVerts, ps.RawIndices, ps.SkelBoneIndices, ps.OutWeights));
                }
                if (twistSurfaces.Count > 0)
                {
                    AdjustLimbTwistAndHipWeightsMultiSurface(targetSkeleton, twistSurfaces, limbTwistProfile);
                }
            }

            foreach (var ps in pendingSurfaces)
            {
                var srcMi = ps.SrcMi;
                var srcMesh = ps.SrcMesh;
                int s = ps.SurfaceIndex;
                var alignedVerts = ps.AlignedVerts;
                var alignedNorms = ps.AlignedNorms;
                var rawUvs = ps.RawUvs;
                var rawIndices = ps.RawIndices;
                var skelBoneIndices = ps.SkelBoneIndices;
                var outWeights = ps.OutWeights;
                bool isSocketProp = ps.IsSocketProp;
                var propSkin = ps.PropSkin;
                var socketMeta = ps.SocketMeta;
                int vCount = alignedVerts.Length;

                int[] skinBoneIndices;
                if (isSocketProp)
                {
                    // BakeSocketPropSurface writes Skin bind indices directly for its dedicated propSkin.
                    skinBoneIndices = skelBoneIndices;
                }
                else
                {
                    skinBoneIndices = new int[vCount * 4];
                    for (int i = 0; i < vCount * 4; i++)
                    {
                        int skelBone = skelBoneIndices[i];
                        skinBoneIndices[i] = skeletonToBindMap.TryGetValue(skelBone, out int bindIdx) ? bindIdx : 0;
                    }
                }

                var surfaceArrays = new Godot.Collections.Array();
                surfaceArrays.Resize((int)Mesh.ArrayType.Max);
                surfaceArrays[(int)Mesh.ArrayType.Vertex] = alignedVerts;
                surfaceArrays[(int)Mesh.ArrayType.Normal] = alignedNorms;
                if (rawUvs != null && rawUvs.Length == vCount)
                {
                    surfaceArrays[(int)Mesh.ArrayType.TexUV] = rawUvs;
                }
                if (rawIndices != null && rawIndices.Length > 0)
                {
                    surfaceArrays[(int)Mesh.ArrayType.Index] = rawIndices;
                }
                surfaceArrays[(int)Mesh.ArrayType.Bones] = skinBoneIndices;
                surfaceArrays[(int)Mesh.ArrayType.Weights] = outWeights;

                var newMesh = new ArrayMesh();
                newMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, surfaceArrays);

                Material surfMat = srcMesh.SurfaceGetMaterial(s) ?? srcMi.GetSurfaceOverrideMaterial(s) ?? CreateDefaultPbrMaterial(srcMi.Name);
                newMesh.SurfaceSetMaterial(0, surfMat);

                var newMi = new MeshInstance3D
                {
                    Name = $"{srcMi.Name}_surf{s}",
                    Mesh = newMesh,
                    Skin = (isSocketProp && propSkin != null) ? propSkin : heroSkin,
                    Transform = Transform3D.Identity
                };

                newMi.Layers &= ~(uint)(1 << 20);
                newMi.SetMeta("IsCustomImportedMesh", true);
                newMi.SetMeta("SourceMeshName", srcMi.Name.ToString());
                newMi.SetMeta("SourceSurfaceIndex", s);
                newMi.SetMeta("OriginalMaterial_0", surfMat);
                ConfigureRiggedSubmeshMetadata(newMi, srcMi, surfMat, options, heroNode);
                if (socketMeta.HasValue) ApplySocketMetadata(newMi, srcMi.Name.ToString(), socketMeta.Value);

                containerNode.AddChild(newMi);
                newMi.Skeleton = newMi.GetPathTo(targetSkeleton);
                result.CustomSubmeshes.Add(newMi);
            }


            result.TotalVertices = totalVerts;
            result.TotalTriangles = totalTris;
            GD.Print($"[CustomModelAutoRigger] Pre-rigged model remapped successfully: {totalVerts} verts, {totalTris} tris.");

            return result;
        }

        #endregion

        #region Case C: Rigid Bone Attachment

        private static RiggingResult RigRigidBoneModel(
            Node3D importedScene,
            List<MeshInstance3D> importedMeshes,
            Node3D heroNode,
            Skeleton3D skeleton,
            Skin heroSkin,
            RiggingOptions options)
        {
            var result = new RiggingResult
            {
                Success = true,
                RiggedHeroRoot = heroNode,
                WasAlreadyRigged = false
            };

            int defaultTargetBoneIdx = skeleton.FindBone(options.RigidBoneName);
            if (defaultTargetBoneIdx == -1)
            {
                defaultTargetBoneIdx = skeleton.FindBone("head");
            }
            if (defaultTargetBoneIdx == -1)
            {
                defaultTargetBoneIdx = 0;
            }

            var (skeletonToBindMap, _) = BuildSkinIndexMappings(skeleton, heroSkin);

            var containerNode = new Node3D { Name = GetContainerNameForSlot(options?.SlotId) };
            heroNode.AddChild(containerNode);
            result.ContainerNode = containerNode;

            int totalVerts = 0;
            int totalTris = 0;

            Skeleton3D hybridFallbackSkel = FindSkeleton(importedScene);
            foreach (var srcMi in importedMeshes)
            {
                var srcMesh = srcMi.Mesh;
                if (srcMesh == null || srcMesh.GetSurfaceCount() == 0) continue;

                string srcMeshName = srcMi.Name.ToString();
                string assignedBoneName = options.RigidBoneName;
                if (options.SubmeshAttachmentSockets != null &&
                    options.SubmeshAttachmentSockets.TryGetValue(srcMeshName, out string subSocketBone) &&
                    !string.IsNullOrWhiteSpace(subSocketBone))
                {
                    assignedBoneName = subSocketBone;
                }

                int targetBoneIdx = !string.IsNullOrWhiteSpace(assignedBoneName)
                    ? skeleton.FindBone(assignedBoneName)
                    : defaultTargetBoneIdx;
                if (targetBoneIdx == -1) targetBoneIdx = defaultTargetBoneIdx;
                int targetBindIdx = skeletonToBindMap.TryGetValue(targetBoneIdx, out int bIdx) ? bIdx : 0;

                bool hasExplicitSocketOffset =
                    (options.SubmeshSnapToSocket != null && options.SubmeshSnapToSocket.TryGetValue(srcMeshName, out bool snapFlag) && snapFlag) ||
                    (options.SubmeshSocketOffsetPos != null && options.SubmeshSocketOffsetPos.TryGetValue(srcMeshName, out var sPos) && sPos.LengthSquared() > 1e-6f) ||
                    (options.SubmeshSocketOffsetRot != null && options.SubmeshSocketOffsetRot.TryGetValue(srcMeshName, out var sRot) && sRot.LengthSquared() > 1e-4f) ||
                    (options.SubmeshSocketScale != null && options.SubmeshSocketScale.TryGetValue(srcMeshName, out float sSc) && Math.Abs(sSc - 1.0f) > 1e-3f);
                Transform3D boneRest = skeleton.GetBoneGlobalRest(targetBoneIdx);
                Transform3D socketDelta = Transform3D.Identity;
                if (hasExplicitSocketOffset)
                {
                    Vector3 uPos = options.SubmeshSocketOffsetPos != null && options.SubmeshSocketOffsetPos.TryGetValue(srcMeshName, out var p) ? p : Vector3.Zero;
                    Vector3 uRot = options.SubmeshSocketOffsetRot != null && options.SubmeshSocketOffsetRot.TryGetValue(srcMeshName, out var r) ? r : Vector3.Zero;
                    float uScale = options.SubmeshSocketScale != null && options.SubmeshSocketScale.TryGetValue(srcMeshName, out float sc) && sc > 0.001f ? sc : 1.0f;
                    socketDelta = boneRest * new Transform3D(
                        Basis.FromEuler(new Vector3(Mathf.DegToRad(uRot.X), Mathf.DegToRad(uRot.Y), Mathf.DegToRad(uRot.Z))).Scaled(new Vector3(uScale, uScale, uScale)),
                        uPos) * boneRest.AffineInverse();
                }

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

                    EvaluateMeshSurfaceInTargetSpace(srcMi, importedScene, options.AlignedTransform, hybridFallbackSkel, rawVerts, rawNorms, rawBones, rawWeights, out Vector3[] alignedVerts, out Vector3[] alignedNorms);

                    if (hasExplicitSocketOffset)
                    {
                        for (int v = 0; v < vCount; v++)
                        {
                            alignedVerts[v] = socketDelta * alignedVerts[v];
                            alignedNorms[v] = (socketDelta.Basis * alignedNorms[v]).Normalized();
                        }
                    }

                    int[] boneIndices = new int[vCount * 4];
                    float[] boneWeights = new float[vCount * 4];

                    if (!TryApplySkinWeightOverrideSkinBindIndices(
                            options, srcMi.Name.ToString(), s, vCount, skeleton, heroSkin, skeletonToBindMap, targetBindIdx, boneIndices, boneWeights))
                    {
                        for (int v = 0; v < vCount; v++)
                        {
                            boneIndices[v * 4 + 0] = targetBindIdx;
                            boneWeights[v * 4 + 0] = 1.0f;
                            boneIndices[v * 4 + 1] = 0;
                            boneWeights[v * 4 + 1] = 0f;
                            boneIndices[v * 4 + 2] = 0;
                            boneWeights[v * 4 + 2] = 0f;
                            boneIndices[v * 4 + 3] = 0;
                            boneWeights[v * 4 + 3] = 0f;
                        }
                    }

                    var surfaceArrays = new Godot.Collections.Array();
                    surfaceArrays.Resize((int)Mesh.ArrayType.Max);
                    surfaceArrays[(int)Mesh.ArrayType.Vertex] = alignedVerts;
                    surfaceArrays[(int)Mesh.ArrayType.Normal] = alignedNorms;
                    if (rawUvs != null && rawUvs.Length == vCount)
                    {
                        surfaceArrays[(int)Mesh.ArrayType.TexUV] = rawUvs;
                    }
                    if (rawIndices != null && rawIndices.Length > 0)
                    {
                        surfaceArrays[(int)Mesh.ArrayType.Index] = rawIndices;
                    }
                    surfaceArrays[(int)Mesh.ArrayType.Bones] = boneIndices;
                    surfaceArrays[(int)Mesh.ArrayType.Weights] = boneWeights;

                    var newMesh = new ArrayMesh();
                    newMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, surfaceArrays);

                    Material surfMat = srcMesh.SurfaceGetMaterial(s) ?? srcMi.GetSurfaceOverrideMaterial(s) ?? CreateDefaultPbrMaterial(srcMi.Name);
                    newMesh.SurfaceSetMaterial(0, surfMat);

                    var newMi = new MeshInstance3D
                    {
                        Name = $"{srcMi.Name}_prop_surf{s}",
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
                    newMi.Skeleton = newMi.GetPathTo(skeleton);
                    result.CustomSubmeshes.Add(newMi);
                }
            }

            result.TotalVertices = totalVerts;
            result.TotalTriangles = totalTris;
            GD.Print($"[CustomModelAutoRigger] Rigid bone attachment complete to bone '{options.RigidBoneName}': {totalVerts} verts.");

            return result;
        }

        #endregion
    }
}
