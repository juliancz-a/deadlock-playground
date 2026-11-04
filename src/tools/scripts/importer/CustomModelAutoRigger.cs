using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using DeadlockPlayground.Animation;

namespace DeadlockPlayground.Importer
{
    public static class CustomModelAutoRigger
    {
        public enum RiggingMethod
        {
            KnnSurfaceProjection = 0,    // Case A: Transfer weights from hero body (DEFAULT & RECOMMENDED)
            PreRiggedSkeletonRemap = 1,  // Case B: Remap existing bones
            RigidSingleBone = 2          // Case C: Rigid attachment to single bone
        }

        public enum PosePreset
        {
            MatchHeroBindPose,
            TPose,
            APoseCustomAngle,
            RigidSingleBone
        }

        public enum ReplaceMode
        {
            FullReplacement,
            PartialAccessory
        }

        public class RiggingOptions
        {
            public RiggingMethod Method { get; set; } = RiggingMethod.KnnSurfaceProjection;
            public PosePreset Preset { get; set; } = PosePreset.MatchHeroBindPose;
            public float CustomArmAngleDeg { get; set; } = 0f;
            public string RigidBoneName { get; set; } = "head";
            public ReplaceMode Mode { get; set; } = ReplaceMode.FullReplacement;
            public List<string> IncludedImportedSubmeshNames { get; set; } = new();
            public List<string> KeptOriginalSubmeshNames { get; set; } = new();
            public Dictionary<string, string> SubmeshAttachmentSockets { get; set; } = new();
            public Dictionary<string, string> SubmeshReplacementMap { get; set; } = new();
            public Dictionary<string, bool> SubmeshSnapToSocket { get; set; } = new();
            public Dictionary<string, Vector3> SubmeshSocketOffsetPos { get; set; } = new();
            public Dictionary<string, Vector3> SubmeshSocketOffsetRot { get; set; } = new();
            public Dictionary<string, float> SubmeshSocketScale { get; set; } = new();
            public bool AutoSnapArmsToHero { get; set; } = true;
            public bool AutoSnapLegsToHero { get; set; } = true;
            public bool AutoSnapHeadToHero { get; set; } = true;
            public bool AutoSnapLimbsToHero
            {
                get => AutoSnapArmsToHero && AutoSnapLegsToHero && AutoSnapHeadToHero;
                set
                {
                    AutoSnapArmsToHero = value;
                    AutoSnapLegsToHero = value;
                    AutoSnapHeadToHero = value;
                }
            }
            public bool IsPreviewOnly { get; set; } = false;
            public Dictionary<string, string> CustomBoneRemap { get; set; } = new();
            public Transform3D AlignedTransform { get; set; } = Transform3D.Identity;
            public int SmoothingIterations { get; set; } = 1;
            public float SearchRadius { get; set; } = 0.20f;
            public int KNearest { get; set; } = 4;
            public bool ExcludeClothHairBones { get; set; } = true;
            public bool KeepFingersRigidOnHand { get; set; } = false;
            public bool ClampLimbStretching { get; set; } = true;
            public Vector3 RootPosition { get; set; } = Vector3.Zero;
            public Vector3 RootRotationDeg { get; set; } = Vector3.Zero;
            public float RootScale { get; set; } = 1.0f;
        }

        public class FingerPhalanxSegment
        {
            public int TargetBone { get; set; }
            public string Name { get; set; }
            public Vector3 As { get; set; }
            public Vector3 Bs { get; set; }
            public Vector3 At { get; set; }
            public Vector3 Bt { get; set; }
            public Vector3 UnitSource { get; set; }
            public Vector3 UnitTarget { get; set; }
            public float LengthSource { get; set; }
            public float LengthTarget { get; set; }
            public float Sigma { get; set; }
            public Quaternion Rotation { get; set; }
        }

        public class HandFingersRegistration
        {
            public bool IsLeft { get; set; }
            public int TargetHandBone { get; set; }
            public readonly Dictionary<int, FingerPhalanxSegment> PhalanxesByTargetBone = new();

            public bool AppliesTo(int targetBoneIdx) => PhalanxesByTargetBone.ContainsKey(targetBoneIdx);

            public (Vector3 pos, Quaternion rot) Evaluate(int targetBoneIdx, Vector3 vHandAligned, Quaternion rotHand)
            {
                if (PhalanxesByTargetBone.TryGetValue(targetBoneIdx, out var p))
                {
                    Vector3 d = vHandAligned - p.As;
                    float proj = d.Dot(p.UnitSource);
                    // Axial-only scaling clamped to [0.85, 1.15] - zero lateral thickness scaling!
                    Vector3 dScaled = p.UnitSource * (proj * p.Sigma) + (d - p.UnitSource * proj);
                    Vector3 pos = p.At + p.Rotation * dScaled;
                    Quaternion rot = p.Rotation * rotHand;
                    return (pos, rot);
                }
                return (vHandAligned, rotHand);
            }
        }

        public class ArmSegmentRegistration
        {
            public int TargetUpperBone { get; set; }
            public int TargetLowerBone { get; set; }
            public int TargetHandBone { get; set; }
            public HandFingersRegistration Fingers { get; set; }

            public Vector3 As { get; set; }
            public Vector3 Bs { get; set; }
            public Vector3 Cs { get; set; }

            public Vector3 At { get; set; }
            public Vector3 Bt { get; set; }
            public Vector3 Ct { get; set; }

            public Vector3 UnitSourceUpper { get; set; }
            public Vector3 UnitTargetUpper { get; set; }
            public float LengthSourceUpper { get; set; }
            public float LengthTargetUpper { get; set; }
            public float SigmaUpper { get; set; }
            public Quaternion RotationUpper { get; set; }

            public Vector3 UnitSourceLower { get; set; }
            public Vector3 UnitTargetLower { get; set; }
            public float LengthSourceLower { get; set; }
            public float LengthTargetLower { get; set; }
            public float SigmaLower { get; set; }
            public Quaternion RotationLower { get; set; }

            public bool AppliesTo(int targetBoneIdx, Skeleton3D targetSkeleton)
            {
                if (targetBoneIdx == TargetUpperBone || targetBoneIdx == TargetLowerBone || targetBoneIdx == TargetHandBone)
                    return true;

                if (Fingers != null && Fingers.AppliesTo(targetBoneIdx))
                    return true;

                if (targetSkeleton != null && targetBoneIdx >= 0 && targetBoneIdx < targetSkeleton.GetBoneCount())
                {
                    int parent = targetSkeleton.GetBoneParent(targetBoneIdx);
                    while (parent >= 0)
                    {
                        if (parent == TargetHandBone || parent == TargetLowerBone) return true;
                        parent = targetSkeleton.GetBoneParent(parent);
                    }
                }
                return false;
            }

            public (Vector3 pos, Quaternion rot) Evaluate(int targetBoneIdx, Skeleton3D targetSkeleton, Vector3 vHero)
            {
                if (targetBoneIdx == TargetUpperBone)
                {
                    Vector3 d = vHero - As;
                    float proj = d.Dot(UnitSourceUpper);
                    Vector3 dScaled = UnitSourceUpper * (proj * SigmaUpper) + (d - UnitSourceUpper * proj);
                    return (At + RotationUpper * dScaled, RotationUpper);
                }

                if (targetBoneIdx == TargetLowerBone)
                {
                    Vector3 d = vHero - Bs;
                    float proj = d.Dot(UnitSourceLower);
                    Vector3 dScaled = UnitSourceLower * (proj * SigmaLower) + (d - UnitSourceLower * proj);
                    return (Bt + RotationLower * dScaled, RotationLower);
                }

                // TargetHandBone or finger descendants
                Vector3 vHand = Ct + RotationLower * (vHero - Cs);
                Quaternion rotHand = RotationLower;

                if (Fingers != null && Fingers.AppliesTo(targetBoneIdx))
                {
                    return Fingers.Evaluate(targetBoneIdx, vHand, rotHand);
                }

                return (vHand, rotHand);
            }
        }

        public class LegSegmentRegistration
        {
            public int TargetThighBone { get; set; }
            public int TargetCalfBone { get; set; }
            public int TargetAnkleBone { get; set; }

            public Vector3 As { get; set; }
            public Vector3 Bs { get; set; }
            public Vector3 Cs { get; set; }

            public Vector3 At { get; set; }
            public Vector3 Bt { get; set; }
            public Vector3 Ct { get; set; }
            public Vector3 CtGrounded { get; set; }

            public Vector3 UnitSourceThigh { get; set; }
            public Vector3 UnitTargetThigh { get; set; }
            public float LengthSourceThigh { get; set; }
            public float LengthTargetThigh { get; set; }
            public float SigmaThigh { get; set; }
            public Quaternion RotationThigh { get; set; }

            public Vector3 UnitSourceCalf { get; set; }
            public Vector3 UnitTargetCalf { get; set; }
            public float LengthSourceCalf { get; set; }
            public float LengthTargetCalf { get; set; }
            public float SigmaCalf { get; set; }
            public Quaternion RotationCalf { get; set; }

            public bool AppliesTo(int targetBoneIdx, Skeleton3D targetSkeleton)
            {
                if (targetBoneIdx == TargetThighBone || targetBoneIdx == TargetCalfBone || targetBoneIdx == TargetAnkleBone)
                    return true;

                if (targetSkeleton != null && targetBoneIdx >= 0 && targetBoneIdx < targetSkeleton.GetBoneCount())
                {
                    int parent = targetSkeleton.GetBoneParent(targetBoneIdx);
                    while (parent >= 0)
                    {
                        if (parent == TargetAnkleBone || parent == TargetCalfBone) return true;
                        parent = targetSkeleton.GetBoneParent(parent);
                    }
                }
                return false;
            }

            public (Vector3 pos, Quaternion rot) Evaluate(int targetBoneIdx, Skeleton3D targetSkeleton, Vector3 vHero)
            {
                if (targetBoneIdx == TargetThighBone)
                {
                    Vector3 d = vHero - As;
                    float proj = d.Dot(UnitSourceThigh);
                    Vector3 dScaled = UnitSourceThigh * (proj * SigmaThigh) + (d - UnitSourceThigh * proj);
                    return (At + RotationThigh * dScaled, RotationThigh);
                }

                if (targetBoneIdx == TargetCalfBone)
                {
                    Vector3 d = vHero - Bs;
                    float proj = d.Dot(UnitSourceCalf);
                    Vector3 dScaled = UnitSourceCalf * (proj * SigmaCalf) + (d - UnitSourceCalf * proj);
                    Vector3 posKnee = Bt + RotationCalf * dScaled;

                    // Smoothly attenuate RotationCalf -> Identity in the bottom 25% of the calf right above the ankle (boot cuff)
                    float tCalf = LengthSourceCalf > 0.001f ? (proj / LengthSourceCalf) : 0f;
                    if (tCalf >= 0.75f)
                    {
                        float wAnkle = Mathf.Clamp((tCalf - 0.75f) / 0.25f, 0.0f, 1.0f);
                        Vector3 posAnkleRigid = CtGrounded + (vHero - Cs);
                        Vector3 blendedPos = posKnee.Lerp(posAnkleRigid, wAnkle);
                        Quaternion blendedRot = RotationCalf.Slerp(Quaternion.Identity, wAnkle);
                        return (blendedPos, blendedRot);
                    }

                    return (posKnee, RotationCalf);
                }

                // TargetAnkleBone or foot/toe descendants (ball, ball_end, sole, heel)
                // STRICT sigma_foot = 1.0 (zero scaling, zero horizontal/vertical distortion)
                // Unified rigid translation anchored at CtGrounded: T_foot(v) = CtGrounded + (v - Cs)
                Vector3 footPos = CtGrounded + (vHero - Cs);
                return (footPos, Quaternion.Identity);
            }
        }

        public class HeadSegmentRegistration
        {
            public int TargetNeckBone { get; set; }
            public int TargetHeadBone { get; set; }

            public Vector3 Ns { get; set; }
            public Vector3 Hs { get; set; }

            public Vector3 Nt { get; set; }
            public Vector3 Ht { get; set; }

            public Quaternion RotationNeck { get; set; }

            public bool AppliesTo(int targetBoneIdx, Skeleton3D targetSkeleton)
            {
                if (targetBoneIdx == TargetNeckBone || targetBoneIdx == TargetHeadBone)
                    return true;

                if (targetSkeleton != null && targetBoneIdx >= 0 && targetBoneIdx < targetSkeleton.GetBoneCount())
                {
                    int parent = targetSkeleton.GetBoneParent(targetBoneIdx);
                    while (parent >= 0)
                    {
                        if (parent == TargetHeadBone || parent == TargetNeckBone) return true;
                        parent = targetSkeleton.GetBoneParent(parent);
                    }
                }
                return false;
            }

            public (Vector3 pos, Quaternion rot) Evaluate(int targetBoneIdx, Skeleton3D targetSkeleton, Vector3 vHero)
            {
                if (targetBoneIdx == TargetNeckBone)
                {
                    Vector3 d = vHero - Ns;
                    return (Nt + RotationNeck * d, RotationNeck);
                }

                // Head and all descendants (face, hair, eyes)
                return (Ht + RotationNeck * (vHero - Hs), RotationNeck);
            }
        }

        public class RiggingResult
        {
            public bool Success { get; set; }
            public string ErrorMessage { get; set; }
            public Node3D RiggedHeroRoot { get; set; }
            public Node3D ContainerNode { get; set; }
            public List<MeshInstance3D> CustomSubmeshes { get; } = new();
            public int TotalVertices { get; set; }
            public int TotalTriangles { get; set; }
            public bool WasAlreadyRigged { get; set; }
        }

        public struct DonorVertex
        {
            public Vector3 RestPosition;
            public Vector3 PosedPosition;
            public int[] BoneIndices; // 4 indices into Skeleton3D
            public float[] BoneWeights; // 4 normalized weights
        }

        public struct BoneCapsuleSegment
        {
            public int BoneIndex;
            public string BoneName;
            public Vector3 StartPos;
            public Vector3 EndPos;
            public float Radius;
        }

        /// <summary>
        /// Rigs and binds an imported custom 3D scene onto the target Deadlock hero's Skeleton3D and Skin.
        /// Handles Unrigged (T-Pose / A-Pose), Pre-Rigged (Mixamo/Blender/UE5), and Rigid Bone attachments.
        /// </summary>
        public static RiggingResult RigAndBind(Node3D importedScene, Node3D heroNode, RiggingOptions options)
        {
            var result = new RiggingResult { RiggedHeroRoot = heroNode };

            if (importedScene == null || !GodotObject.IsInstanceValid(importedScene))
            {
                result.Success = false;
                result.ErrorMessage = "Imported 3D scene is null or invalid.";
                return result;
            }

            if (heroNode == null || !GodotObject.IsInstanceValid(heroNode))
            {
                result.Success = false;
                result.ErrorMessage = "Deadlock Hero root node is null or invalid.";
                return result;
            }

            var heroSkeleton = FindSkeleton(heroNode);
            if (heroSkeleton == null)
            {
                result.Success = false;
                result.ErrorMessage = "Target Deadlock Hero has no Skeleton3D.";
                return result;
            }

            // Clean up any previously injected Custom_Rigged_Character to prevent stacking duplicates on re-confirm
            var existingCustom = heroNode.FindChild("Custom_Rigged_Character", true, false);
            if (existingCustom != null && GodotObject.IsInstanceValid(existingCustom))
            {
                existingCustom.GetParent()?.RemoveChild(existingCustom);
                existingCustom.QueueFree();
            }

            // Ensure forearm twist bones exist with clean local rest basis for 50% twist distribution
            EnsureForearmTwistBone(heroSkeleton, isLeft: true);
            EnsureForearmTwistBone(heroSkeleton, isLeft: false);

            var heroSkin = FindSkin(heroNode);
            if (heroSkin == null || heroSkin.GetBindCount() < heroSkeleton.GetBoneCount())
            {
                heroSkin = CreateFullSkeletonSkin(heroSkeleton);
            }
            var allImportedMeshes = CollectMeshes(importedScene);
            var importedMeshes = options.IncludedImportedSubmeshNames != null && options.IncludedImportedSubmeshNames.Count > 0
                ? allImportedMeshes.Where(m => options.IncludedImportedSubmeshNames.Contains(m.Name.ToString())).ToList()
                : allImportedMeshes;

            if (importedMeshes.Count == 0)
            {
                result.Success = false;
                result.ErrorMessage = "No matching MeshInstance3D nodes selected for import.";
                return result;
            }

            var importedSkeleton = FindSkeleton(importedScene);
            bool hasImportedRig = importedSkeleton != null && importedMeshes.Any(m => m.Skin != null || HasSkinnedVertices(m));

            GD.Print($"[CustomModelAutoRigger] Processing imported model: {importedMeshes.Count} submesh(es), HasRig={hasImportedRig}, Method={options.Method}, Preset={options.Preset}, Mode={options.Mode}");

            try
            {
                if (options.Method == RiggingMethod.RigidSingleBone || options.Preset == PosePreset.RigidSingleBone)
                {
                    result = RigRigidBoneModel(importedScene, importedMeshes, heroNode, heroSkeleton, heroSkin, options);
                }
                else if (options.Method == RiggingMethod.PreRiggedSkeletonRemap && hasImportedRig)
                {
                    result = RigPreRiggedModel(importedScene, importedMeshes, importedSkeleton, heroNode, heroSkeleton, heroSkin, options);
                }
                else
                {
                    // Default & Recommended: KNN Surface Projection!
                    result = RigUnriggedModel(importedScene, importedMeshes, heroNode, heroSkeleton, heroSkin, options);
                }

                if (result.Success)
                {
                    ApplyReplacementMode(heroNode, result.CustomSubmeshes, options);
                }

                return result;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[CustomModelAutoRigger] Error during auto-rigging: {ex.Message}\n{ex.StackTrace}");
                result.Success = false;
                result.ErrorMessage = $"Auto-rigging failed: {ex.Message}";
                return result;
            }
        }

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

            // 2. Pose hero skeleton if T-Pose or custom arm angle requested, to create pose-matched donor cloud
            Dictionary<int, Quaternion> originalLocalRotations = BackupSkeletonLocalRotations(heroSkeleton);
            heroSkeleton.ResetBonePoses(); // Force 100% symmetrical bind pose before arm adjustments!

            bool isPoseMatched = options.Preset == PosePreset.TPose || options.Preset == PosePreset.APoseCustomAngle;

            Transform3D[] posedGlobalBoneTransforms = new Transform3D[heroSkeleton.GetBoneCount()];
            Transform3D[] restGlobalBoneTransforms = new Transform3D[heroSkeleton.GetBoneCount()];

            for (int b = 0; b < heroSkeleton.GetBoneCount(); b++)
            {
                restGlobalBoneTransforms[b] = heroSkeleton.GetBoneGlobalRest(b);
            }

            if (isPoseMatched)
            {
                PoseHeroArmsForPreset(heroSkeleton, options.Preset, options.CustomArmAngleDeg);
            }

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

            GD.Print($"[CustomModelAutoRigger] Built donor vertex cloud in {swDonor.ElapsedMilliseconds} ms: {donorCloud.Count} posed donor vertices.");

            // 4. Build Spatial Hash Grid for fast KNN querying
            var swGrid = System.Diagnostics.Stopwatch.StartNew();
            float cellSize = 0.20f; // 20 cm
            var spatialGrid = BuildSpatialHashGrid(donorCloud, cellSize);
            swGrid.Stop();

            // 5. Build core anatomical capsule segments for fallback
            var swCap = System.Diagnostics.Stopwatch.StartNew();
            var capsuleSegments = BuildCapsuleSegments(heroSkeleton, posedGlobalBoneTransforms);
            swCap.Stop();
            GD.Print($"[CustomModelAutoRigger] Grid={swGrid.ElapsedMilliseconds} ms, Capsules={swCap.ElapsedMilliseconds} ms ({capsuleSegments.Count} segments).");

            // 6. Process each imported mesh
            var containerNode = new Node3D { Name = "Custom_Rigged_Character" };
            heroNode.AddChild(containerNode);
            result.ContainerNode = containerNode;

            int totalVerts = 0;
            int totalTris = 0;
            long totalKnnMs = 0, totalLapMs = 0, totalBakeMs = 0, totalMeshMs = 0;

            var socketFrames = ComputeSocketGroupFrames(importedMeshes, importedScene, options, heroNode, heroSkeleton);

            foreach (var srcMi in importedMeshes)
            {
                var srcMesh = srcMi.Mesh;
                if (srcMesh == null || srcMesh.GetSurfaceCount() == 0) continue;

                // Deterministic mesh transform relative to imported root (Works without being inside scene tree!)
                Transform3D toHeroSpace = options.AlignedTransform * ComputeTransformRelativeToRoot(srcMi, importedScene);

                for (int s = 0; s < srcMesh.GetSurfaceCount(); s++)
                {
                    var arrays = srcMesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count == 0) continue;

                    var rawVerts = GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    var rawNorms = arrays.Count > (int)Mesh.ArrayType.Normal ? GetVector3Array(arrays[(int)Mesh.ArrayType.Normal]) : null;
                    var rawUvs = arrays.Count > (int)Mesh.ArrayType.TexUV ? GetVector2Array(arrays[(int)Mesh.ArrayType.TexUV]) : null;
                    var rawIndices = arrays.Count > (int)Mesh.ArrayType.Index ? GetInt32Array(arrays[(int)Mesh.ArrayType.Index]) : null;

                    if (rawVerts == null || rawVerts.Length == 0) continue;

                    int vCount = rawVerts.Length;
                    totalVerts += vCount;
                    totalTris += (rawIndices != null) ? rawIndices.Length / 3 : vCount / 3;

                    // Transform vertices and normals into the posed Hero space
                    Vector3[] alignedVerts = new Vector3[vCount];
                    Vector3[] alignedNorms = rawNorms != null && rawNorms.Length == vCount ? new Vector3[vCount] : new Vector3[vCount];

                    for (int v = 0; v < vCount; v++)
                    {
                        alignedVerts[v] = toHeroSpace * rawVerts[v];
                        if (rawNorms != null && rawNorms.Length == vCount)
                        {
                            alignedNorms[v] = (toHeroSpace.Basis * rawNorms[v]).Normalized();
                        }
                        else
                        {
                            alignedNorms[v] = Vector3.Up;
                        }
                    }

                    // Check if this submesh is designated as a rigid socket attachment (e.g. weapon / prop attached to hand_R)
                    string socketBoneName = null;
                    bool isSocketProp = options.SubmeshAttachmentSockets != null &&
                                        options.SubmeshAttachmentSockets.TryGetValue(srcMi.Name.ToString(), out socketBoneName) &&
                                        !string.IsNullOrEmpty(socketBoneName);

                    int[] boneIndices = new int[vCount * 4];
                    float[] boneWeights = new float[vCount * 4];
                    Skin propSkin = null;

                    var swKnnStep = System.Diagnostics.Stopwatch.StartNew();
                    if (isSocketProp)
                    {
                        int tgtBone = heroSkeleton.FindBone(socketBoneName);
                        if (tgtBone == -1) tgtBone = FindBoneAny(heroSkeleton, "weapon_hand_R", "weapon_hand_L", "hand_R", "hand_L", "head");
                        if (tgtBone == -1) tgtBone = 0;

                        propSkin = new Skin();
                        propSkin.AddBind(tgtBone, heroSkeleton.GetBoneGlobalRest(tgtBone).AffineInverse());

                        bool snapToBone = options.SubmeshSnapToSocket != null &&
                                          options.SubmeshSnapToSocket.TryGetValue(srcMi.Name.ToString(), out bool snap) && snap;

                        Vector3 userPosOffset = options.SubmeshSocketOffsetPos != null &&
                                                options.SubmeshSocketOffsetPos.TryGetValue(srcMi.Name.ToString(), out var uPos) ? uPos : Vector3.Zero;
                        Vector3 userRotOffset = options.SubmeshSocketOffsetRot != null &&
                                                options.SubmeshSocketOffsetRot.TryGetValue(srcMi.Name.ToString(), out var uRot) ? uRot : Vector3.Zero;
                        float userScale = options.SubmeshSocketScale != null &&
                                          options.SubmeshSocketScale.TryGetValue(srcMi.Name.ToString(), out float uScale) && uScale > 0.001f ? uScale : 1.0f;

                        Basis rotBasis = Basis.FromEuler(new Vector3(
                            Mathf.DegToRad(userRotOffset.X),
                            Mathf.DegToRad(userRotOffset.Y),
                            Mathf.DegToRad(userRotOffset.Z)
                        ), EulerOrder.Yxz);

                        Transform3D boneRest = tgtBone >= 0 ? heroSkeleton.GetBoneGlobalRest(tgtBone) : Transform3D.Identity;
                        Vector3 fallbackOrigin = boneRest.Origin;

                        SocketFrame frame = default;
                        bool hasFrame = socketFrames != null && socketFrames.TryGetValue(socketBoneName, out frame);

                        Vector3 socketPos = hasFrame ? frame.SocketCenter : fallbackOrigin;
                        Vector3 centerPoint = hasFrame ? frame.Center : Vector3.Zero;
                        Basis pcaBasis = hasFrame ? frame.PcaBasis : Basis.Identity;
                        Basis targetOrient = hasFrame ? frame.TargetOrientation : Basis.Identity;

                        if (snapToBone && vCount > 0)
                        {
                            for (int v = 0; v < vCount; v++)
                            {
                                Vector3 rel = (toHeroSpace * rawVerts[v]) - centerPoint;
                                Vector3 localAligned = pcaBasis * rel;
                                Vector3 oriented = targetOrient * (localAligned * userScale);
                                alignedVerts[v] = socketPos + (boneRest.Basis * userPosOffset) + ((boneRest.Basis * rotBasis) * oriented);

                                Vector3 heroSpaceNorm = (rawNorms != null && rawNorms.Length == vCount)
                                    ? (toHeroSpace.Basis * rawNorms[v]).Normalized()
                                    : Vector3.Up;
                                Vector3 localNorm = pcaBasis * heroSpaceNorm;
                                alignedNorms[v] = ((boneRest.Basis * rotBasis) * (targetOrient * localNorm)).Normalized();
                            }
                        }
                        else
                        {
                            for (int v = 0; v < vCount; v++)
                            {
                                Vector3 rel = toHeroSpace * rawVerts[v];
                                rel = (boneRest.Basis * rotBasis) * (rel * userScale);
                                alignedVerts[v] = rel + (boneRest.Basis * userPosOffset);
                                if (rawNorms != null && rawNorms.Length == vCount)
                                {
                                    alignedNorms[v] = ((boneRest.Basis * rotBasis) * (toHeroSpace.Basis * rawNorms[v])).Normalized();
                                }
                                else
                                {
                                    alignedNorms[v] = Vector3.Up;
                                }
                            }
                        }

                        for (int v = 0; v < vCount; v++)
                        {
                            boneIndices[v * 4 + 0] = 0; // Bind 0 in propSkin
                            boneWeights[v * 4 + 0] = 1.0f;
                            boneIndices[v * 4 + 1] = 0;
                            boneWeights[v * 4 + 1] = 0f;
                            boneIndices[v * 4 + 2] = 0;
                            boneWeights[v * 4 + 2] = 0f;
                            boneIndices[v * 4 + 3] = 0;
                            boneWeights[v * 4 + 3] = 0f;
                        }
                    }
                    else
                    {
                        System.Threading.Tasks.Parallel.For(0, vCount, v =>
                        {
                            QueryWeightsWithCapsuleFallback(
                                alignedVerts[v],
                                spatialGrid,
                                donorCloud,
                                capsuleSegments,
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
                    if (!isSocketProp && options.SmoothingIterations > 0 && rawIndices != null && rawIndices.Length >= 3)
                    {
                        var swLapStep = System.Diagnostics.Stopwatch.StartNew();
                        SmoothWeightsLaplacian(rawIndices, vCount, boneIndices, boneWeights, options.SmoothingIterations, alignedVerts);
                        swLapStep.Stop();
                        totalLapMs += swLapStep.ElapsedMilliseconds;
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

                    // Remap bone indices from Skeleton3D indices to Skin bind indices
                    int[] skinBoneIndices = new int[vCount * 4];
                    for (int i = 0; i < vCount * 4; i++)
                    {
                        int skelBone = boneIndices[i];
                        skinBoneIndices[i] = skeletonToBindMap.TryGetValue(skelBone, out int bindIdx) ? bindIdx : 0;
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
                    newMi.SetMeta("OriginalMaterial_0", surfMat);

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

            var (skeletonToBindMap, _) = BuildSkinIndexMappings(targetSkeleton, heroSkin);

            // 1. Map source skeleton bones to Deadlock hero skeleton bones using HumanoidBoneMapper and user overrides
            Dictionary<int, int> sourceToTargetBoneMap = BuildHumanoidBoneMap(sourceSkeleton, targetSkeleton, options.CustomBoneRemap);

            GD.Print($"[CustomModelAutoRigger] Mapped {sourceToTargetBoneMap.Count} of {sourceSkeleton.GetBoneCount()} bones from source rig to hero skeleton.");

            var containerNode = new Node3D { Name = "Custom_Rigged_Character" };
            heroNode.AddChild(containerNode);
            result.ContainerNode = containerNode;

            // 2. Build Hero-Space Bone-Segment Registrations (Arms, Legs/Feet, Neck/Head)
            Transform3D skelToHero = options.AlignedTransform * ComputeTransformRelativeToRoot(sourceSkeleton, importedScene);
            ArmSegmentRegistration leftArm = options.AutoSnapArmsToHero
                ? BuildArmSegmentRegistration(sourceSkeleton, targetSkeleton, sourceToTargetBoneMap, "arm_upper_L", "arm_lower_L", "hand_L", skelToHero, options.KeepFingersRigidOnHand)
                : null;
            ArmSegmentRegistration rightArm = options.AutoSnapArmsToHero
                ? BuildArmSegmentRegistration(sourceSkeleton, targetSkeleton, sourceToTargetBoneMap, "arm_upper_R", "arm_lower_R", "hand_R", skelToHero, options.KeepFingersRigidOnHand)
                : null;

            LegSegmentRegistration leftLeg = options.AutoSnapLegsToHero
                ? BuildLegSegmentRegistration(sourceSkeleton, targetSkeleton, sourceToTargetBoneMap, "leg_upper_L", "leg_lower_L", "ankle_L", skelToHero)
                : null;
            LegSegmentRegistration rightLeg = options.AutoSnapLegsToHero
                ? BuildLegSegmentRegistration(sourceSkeleton, targetSkeleton, sourceToTargetBoneMap, "leg_upper_R", "leg_lower_R", "ankle_R", skelToHero)
                : null;

            HeadSegmentRegistration headSeg = options.AutoSnapHeadToHero
                ? BuildHeadSegmentRegistration(sourceSkeleton, targetSkeleton, sourceToTargetBoneMap, "neck_0", "head", skelToHero)
                : null;

            int pelvisBone = targetSkeleton.FindBone("pelvis");
            int srcPelvis = FindSourceBoneForTarget(sourceToTargetBoneMap, pelvisBone);
            Vector3 pelvisOffset = Vector3.Zero;
            if (options.AutoSnapLegsToHero && pelvisBone != -1 && srcPelvis != -1)
            {
                Vector3 PsPelvis = skelToHero * sourceSkeleton.GetBoneGlobalRest(srcPelvis).Origin;
                Vector3 PtPelvis = targetSkeleton.GetBoneGlobalRest(pelvisBone).Origin;
                pelvisOffset = (PtPelvis - PsPelvis) * 0.5f;
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

            int totalVerts = 0;
            int totalTris = 0;

            var socketFrames = ComputeSocketGroupFrames(importedMeshes, importedScene, options, heroNode, targetSkeleton);

            foreach (var srcMi in importedMeshes)
            {
                var srcMesh = srcMi.Mesh;
                if (srcMesh == null || srcMesh.GetSurfaceCount() == 0) continue;

                // Deterministic mesh transform relative to imported root (Works without being inside scene tree!)
                Transform3D toHeroSpace = options.AlignedTransform * ComputeTransformRelativeToRoot(srcMi, importedScene);

                for (int s = 0; s < srcMesh.GetSurfaceCount(); s++)
                {
                    var arrays = srcMesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count == 0) continue;

                    var rawVerts = GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    var rawNorms = arrays.Count > (int)Mesh.ArrayType.Normal ? GetVector3Array(arrays[(int)Mesh.ArrayType.Normal]) : null;
                    var rawUvs = arrays.Count > (int)Mesh.ArrayType.TexUV ? GetVector2Array(arrays[(int)Mesh.ArrayType.TexUV]) : null;
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


                    int[] skinBoneIndices = new int[vCount * 4];
                    float[] outWeights = new float[vCount * 4];
                    Skin propSkin = null;

                    if (isSocketProp)
                    {
                        int tgtBone = targetSkeleton.FindBone(socketBoneName);
                        if (tgtBone == -1) tgtBone = FindBoneAny(targetSkeleton, "weapon_hand_R", "weapon_hand_L", "hand_R", "hand_L", "head");
                        if (tgtBone == -1) tgtBone = 0;

                        propSkin = new Skin();
                        propSkin.AddBind(tgtBone, targetSkeleton.GetBoneGlobalRest(tgtBone).AffineInverse());

                        bool snapToBone = options.SubmeshSnapToSocket != null &&
                                          options.SubmeshSnapToSocket.TryGetValue(srcMi.Name.ToString(), out bool snap) && snap;

                        Vector3 userPosOffset = options.SubmeshSocketOffsetPos != null &&
                                                options.SubmeshSocketOffsetPos.TryGetValue(srcMi.Name.ToString(), out var uPos) ? uPos : Vector3.Zero;
                        Vector3 userRotOffset = options.SubmeshSocketOffsetRot != null &&
                                                options.SubmeshSocketOffsetRot.TryGetValue(srcMi.Name.ToString(), out var uRot) ? uRot : Vector3.Zero;
                        float userScale = options.SubmeshSocketScale != null &&
                                          options.SubmeshSocketScale.TryGetValue(srcMi.Name.ToString(), out float uScale) && uScale > 0.001f ? uScale : 1.0f;

                        Basis rotBasis = Basis.FromEuler(new Vector3(
                            Mathf.DegToRad(userRotOffset.X),
                            Mathf.DegToRad(userRotOffset.Y),
                            Mathf.DegToRad(userRotOffset.Z)
                        ), EulerOrder.Yxz);

                        Transform3D boneRest = tgtBone >= 0 ? targetSkeleton.GetBoneGlobalRest(tgtBone) : Transform3D.Identity;
                        Vector3 fallbackOrigin = boneRest.Origin;

                        SocketFrame frame = default;
                        bool hasFrame = socketFrames != null && socketFrames.TryGetValue(socketBoneName, out frame);

                        Vector3 socketPos = hasFrame ? frame.SocketCenter : fallbackOrigin;
                        Vector3 centerPoint = hasFrame ? frame.Center : Vector3.Zero;
                        Basis pcaBasis = hasFrame ? frame.PcaBasis : Basis.Identity;
                        Basis targetOrient = hasFrame ? frame.TargetOrientation : Basis.Identity;

                        if (snapToBone && vCount > 0)
                        {
                            for (int v = 0; v < vCount; v++)
                            {
                                Vector3 rel = (toHeroSpace * rawVerts[v]) - centerPoint;
                                Vector3 localAligned = pcaBasis * rel;
                                Vector3 oriented = targetOrient * (localAligned * userScale);
                                alignedVerts[v] = socketPos + (boneRest.Basis * userPosOffset) + ((boneRest.Basis * rotBasis) * oriented);

                                Vector3 heroSpaceNorm = (rawNorms != null && rawNorms.Length == vCount)
                                    ? (toHeroSpace.Basis * rawNorms[v]).Normalized()
                                    : Vector3.Up;
                                Vector3 localNorm = pcaBasis * heroSpaceNorm;
                                alignedNorms[v] = ((boneRest.Basis * rotBasis) * (targetOrient * localNorm)).Normalized();
                            }
                        }
                        else
                        {
                            for (int v = 0; v < vCount; v++)
                            {
                                Vector3 rel = toHeroSpace * rawVerts[v];
                                rel = (boneRest.Basis * rotBasis) * (rel * userScale);
                                alignedVerts[v] = rel + (boneRest.Basis * userPosOffset);
                                if (rawNorms != null && rawNorms.Length == vCount)
                                {
                                    alignedNorms[v] = ((boneRest.Basis * rotBasis) * (toHeroSpace.Basis * rawNorms[v])).Normalized();
                                }
                                else
                                {
                                    alignedNorms[v] = Vector3.Up;
                                }
                            }
                        }

                        for (int v = 0; v < vCount; v++)
                        {
                            skinBoneIndices[v * 4 + 0] = 0; // Bind 0 in propSkin
                            outWeights[v * 4 + 0] = 1.0f;
                            skinBoneIndices[v * 4 + 1] = 0;
                            outWeights[v * 4 + 1] = 0f;
                            skinBoneIndices[v * 4 + 2] = 0;
                            outWeights[v * 4 + 2] = 0f;
                            skinBoneIndices[v * 4 + 3] = 0;
                            outWeights[v * 4 + 3] = 0f;
                        }
                    }
                    else
                    {
                        System.Threading.Tasks.Parallel.For(0, vCount, v =>
                        {
                            Vector3 rawV = rawVerts[v];
                            Vector3 vHero = toHeroSpace * rawV;
                            Vector3 nHero = (rawNorms != null && rawNorms.Length == vCount)
                                ? (toHeroSpace.Basis * rawNorms[v]).Normalized()
                                : Vector3.Up;

                            Span<int> localAccumBones = stackalloc int[4];
                            Span<float> localAccumWeights = stackalloc float[4];
                            int accumCount = 0;

                            for (int k = 0; k < 4; k++)
                            {
                                int srcBone = rawBones != null && rawBones.Length > (v * 4 + k) ? rawBones[v * 4 + k] : 0;
                                float w = rawWeights != null && rawWeights.Length > (v * 4 + k) ? rawWeights[v * 4 + k] : (k == 0 ? 1f : 0f);

                                if (w <= 0.0001f) continue;

                                int tgtBone = ResolveTargetBoneWithAncestorFallback(srcBone, sourceSkeleton, sourceToTargetBoneMap, targetSkeleton, options.KeepFingersRigidOnHand);

                                int found = -1;
                                for (int a = 0; a < accumCount; a++)
                                {
                                    if (localAccumBones[a] == tgtBone) { found = a; break; }
                                }
                                if (found != -1)
                                {
                                    localAccumWeights[found] += w;
                                }
                                else if (accumCount < 4)
                                {
                                    localAccumBones[accumCount] = tgtBone;
                                    localAccumWeights[accumCount] = w;
                                    accumCount++;
                                }
                            }

                            // Extract top influences in descending order
                            Span<int> topBones = stackalloc int[4];
                            Span<float> topWeights = stackalloc float[4];

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

                            // Axial Wrist Twist Gradient Blending
                            if (ApplyWristTwistBlending(vHero, topBones, topWeights, leftArm, rightArm, tgtTwistL, tgtTwistR))
                            {
                                wristMask[v] = true;
                                System.Threading.Interlocked.Increment(ref wristCount);
                            }

                            // Assign Skin bone indices & weights
                            for (int k = 0; k < 4; k++)
                            {
                                int skelBone = topBones[k];
                                skinBoneIndices[v * 4 + k] = skeletonToBindMap.TryGetValue(skelBone, out int bindIdx) ? bindIdx : 0;
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

                                Vector3 tPos;
                                Quaternion tRot;

                                if (leftArm != null && leftArm.AppliesTo(tgtBone, targetSkeleton))
                                {
                                    (tPos, tRot) = leftArm.Evaluate(tgtBone, targetSkeleton, vHero);
                                }
                                else if (rightArm != null && rightArm.AppliesTo(tgtBone, targetSkeleton))
                                {
                                    (tPos, tRot) = rightArm.Evaluate(tgtBone, targetSkeleton, vHero);
                                }
                                else if (leftLeg != null && leftLeg.AppliesTo(tgtBone, targetSkeleton))
                                {
                                    (tPos, tRot) = leftLeg.Evaluate(tgtBone, targetSkeleton, vHero);
                                }
                                else if (rightLeg != null && rightLeg.AppliesTo(tgtBone, targetSkeleton))
                                {
                                    (tPos, tRot) = rightLeg.Evaluate(tgtBone, targetSkeleton, vHero);
                                }
                                else if (headSeg != null && headSeg.AppliesTo(tgtBone, targetSkeleton))
                                {
                                    (tPos, tRot) = headSeg.Evaluate(tgtBone, targetSkeleton, vHero);
                                }
                                else if (tgtBone == pelvisBone)
                                {
                                    tPos = vHero + pelvisOffset;
                                    tRot = Quaternion.Identity;
                                }
                                else
                                {
                                    tPos = vHero;
                                    tRot = Quaternion.Identity;
                                }

                                blendedPos += w * tPos;
                                blendedNorm += w * (tRot * nHero);
                            }

                            alignedVerts[v] = blendedPos;
                            alignedNorms[v] = blendedNorm.LengthSquared() > 0.0001f ? blendedNorm.Normalized() : nHero;
                        });

                        if (wristCount > 0 && rawIndices != null && rawIndices.Length > 0)
                        {
                            SmoothWeightsLaplacian(rawIndices, vCount, skinBoneIndices, outWeights, 2, alignedVerts, wristMask);
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
                    newMi.SetMeta("OriginalMaterial_0", surfMat);

                    containerNode.AddChild(newMi);
                    newMi.Skeleton = newMi.GetPathTo(targetSkeleton);
                    result.CustomSubmeshes.Add(newMi);
                }
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

            int targetBoneIdx = skeleton.FindBone(options.RigidBoneName);
            if (targetBoneIdx == -1)
            {
                targetBoneIdx = skeleton.FindBone("head");
            }
            if (targetBoneIdx == -1)
            {
                targetBoneIdx = 0;
            }

            var (skeletonToBindMap, _) = BuildSkinIndexMappings(skeleton, heroSkin);
            int targetBindIdx = skeletonToBindMap.TryGetValue(targetBoneIdx, out int bIdx) ? bIdx : 0;

            var containerNode = new Node3D { Name = "Custom_Rigid_Accessory" };
            heroNode.AddChild(containerNode);
            result.ContainerNode = containerNode;

            int totalVerts = 0;
            int totalTris = 0;

            foreach (var srcMi in importedMeshes)
            {
                var srcMesh = srcMi.Mesh;
                if (srcMesh == null || srcMesh.GetSurfaceCount() == 0) continue;

                // Deterministic mesh transform relative to imported root (Works without being inside scene tree!)
                Transform3D toHeroSpace = options.AlignedTransform * ComputeTransformRelativeToRoot(srcMi, importedScene);

                for (int s = 0; s < srcMesh.GetSurfaceCount(); s++)
                {
                    var arrays = srcMesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count == 0) continue;

                    var rawVerts = GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    var rawNorms = arrays.Count > (int)Mesh.ArrayType.Normal ? GetVector3Array(arrays[(int)Mesh.ArrayType.Normal]) : null;
                    var rawUvs = arrays.Count > (int)Mesh.ArrayType.TexUV ? GetVector2Array(arrays[(int)Mesh.ArrayType.TexUV]) : null;
                    var rawIndices = arrays.Count > (int)Mesh.ArrayType.Index ? GetInt32Array(arrays[(int)Mesh.ArrayType.Index]) : null;

                    if (rawVerts == null || rawVerts.Length == 0) continue;

                    int vCount = rawVerts.Length;
                    totalVerts += vCount;
                    totalTris += (rawIndices != null) ? rawIndices.Length / 3 : vCount / 3;

                    Vector3[] alignedVerts = new Vector3[vCount];
                    Vector3[] alignedNorms = new Vector3[vCount];

                    for (int v = 0; v < vCount; v++)
                    {
                        alignedVerts[v] = toHeroSpace * rawVerts[v];
                        if (rawNorms != null && rawNorms.Length == vCount)
                        {
                            alignedNorms[v] = (toHeroSpace.Basis * rawNorms[v]).Normalized();
                        }
                        else
                        {
                            alignedNorms[v] = Vector3.Up;
                        }
                    }

                    int[] boneIndices = new int[vCount * 4];
                    float[] boneWeights = new float[vCount * 4];

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
                    newMi.SetMeta("OriginalMaterial_0", surfMat);

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

        #region Helper Math & Algorithms

        public struct SocketFrame
        {
            public Vector3 Center;
            public Basis PcaBasis;
            public Vector3 SocketCenter;
            public Basis TargetOrientation;
            public bool HasTargetReplacement;
        }

        public static void Jacobi3x3(
            float[,] a,
            out float[] eigenvalues,
            out Vector3[] eigenvectors)
        {
            float[,] v = new float[3, 3]
            {
                { 1f, 0f, 0f },
                { 0f, 1f, 0f },
                { 0f, 0f, 1f }
            };

            for (int sweep = 0; sweep < 50; sweep++)
            {
                float offDiag = MathF.Abs(a[0, 1]) + MathF.Abs(a[0, 2]) + MathF.Abs(a[1, 2]);
                if (offDiag < 1e-6f) break;

                for (int p = 0; p < 2; p++)
                {
                    for (int q = p + 1; q < 3; q++)
                    {
                        float apq = a[p, q];
                        if (MathF.Abs(apq) < 1e-8f) continue;

                        float app = a[p, p];
                        float aqq = a[q, q];
                        float theta = (aqq - app) / (2.0f * apq);
                        float t = 1.0f / (MathF.Abs(theta) + MathF.Sqrt(1.0f + theta * theta));
                        if (theta < 0f) t = -t;

                        float c = 1.0f / MathF.Sqrt(1.0f + t * t);
                        float s = t * c;
                        float tau = s / (1.0f + c);

                        a[p, q] = 0f;
                        a[p, p] -= t * apq;
                        a[q, q] += t * apq;

                        for (int j = 0; j < p; j++)
                        {
                            float ajp = a[j, p];
                            float ajq = a[j, q];
                            a[j, p] = ajp - s * (ajq + tau * ajp);
                            a[j, q] = ajq + s * (ajp - tau * ajq);
                        }
                        for (int j = p + 1; j < q; j++)
                        {
                            float apj = a[p, j];
                            float ajq = a[j, q];
                            a[p, j] = apj - s * (ajq + tau * apj);
                            a[j, q] = ajq + s * (apj - tau * ajq);
                        }
                        for (int j = q + 1; j < 3; j++)
                        {
                            float apj = a[p, j];
                            float aqj = a[q, j];
                            a[p, j] = apj - s * (aqj + tau * apj);
                            a[q, j] = aqj + s * (apj - tau * aqj);
                        }

                        for (int j = 0; j < 3; j++)
                        {
                            float vjp = v[j, p];
                            float vjq = v[j, q];
                            v[j, p] = vjp - s * (vjq + tau * vjp);
                            v[j, q] = vjq + s * (vjp - tau * vjq);
                        }
                    }
                }
            }

            eigenvalues = new float[3] { a[0, 0], a[1, 1], a[2, 2] };
            eigenvectors = new Vector3[3]
            {
                new Vector3(v[0, 0], v[1, 0], v[2, 0]).Normalized(),
                new Vector3(v[0, 1], v[1, 1], v[2, 1]).Normalized(),
                new Vector3(v[0, 2], v[1, 2], v[2, 2]).Normalized()
            };
        }

        public static (Vector3 Center, Basis PcaBasis, Vector3 Extents) ComputeMeshPrincipalFrame(IList<Vector3> verts)
        {
            if (verts == null || verts.Count == 0)
            {
                return (Vector3.Zero, Basis.Identity, Vector3.One);
            }

            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            Vector3 mean = Vector3.Zero;
            int count = verts.Count;
            int step = Math.Max(1, count / 1000);
            int sampleCount = 0;

            for (int i = 0; i < count; i += step)
            {
                Vector3 p = verts[i];
                min = min.Min(p);
                max = max.Max(p);
                mean += p;
                sampleCount++;
            }
            if (sampleCount > 0) mean /= sampleCount;
            Vector3 center = (min + max) * 0.5f;

            float cxx = 0, cyy = 0, czz = 0, cxy = 0, cxz = 0, cyz = 0;
            for (int i = 0; i < count; i += step)
            {
                Vector3 d = verts[i] - center;
                cxx += d.X * d.X;
                cyy += d.Y * d.Y;
                czz += d.Z * d.Z;
                cxy += d.X * d.Y;
                cxz += d.X * d.Z;
                cyz += d.Y * d.Z;
            }
            if (sampleCount > 0)
            {
                cxx /= sampleCount; cyy /= sampleCount; czz /= sampleCount;
                cxy /= sampleCount; cxz /= sampleCount; cyz /= sampleCount;
            }

            float[,] cov = new float[3, 3]
            {
                { cxx, cxy, cxz },
                { cxy, cyy, cyz },
                { cxz, cyz, czz }
            };

            Jacobi3x3(cov, out float[] eigenvalues, out Vector3[] eigenvectors);

            int[] order = new int[3] { 0, 1, 2 };
            Array.Sort(order, (a, b) => eigenvalues[b].CompareTo(eigenvalues[a]));

            Vector3 eMax = eigenvectors[order[0]]; // Longitudinal / Barrel axis (longest)
            Vector3 eMid = eigenvectors[order[1]]; // Height / Slide axis (middle)
            Vector3 eMin = eigenvectors[order[2]]; // Thickness / Lateral axis (smallest)

            // Sign consistency
            // 1. Barrel axis: eMax points toward the muzzle (end with smaller vertical height variance)
            float tMin = float.MaxValue, tMax = float.MinValue;
            for (int i = 0; i < count; i += step)
            {
                float t = eMax.Dot(verts[i] - center);
                if (t < tMin) tMin = t;
                if (t > tMax) tMax = t;
            }
            float tMid = (tMin + tMax) * 0.5f;
            float varPos = 0, varNeg = 0;
            int cPos = 0, cNeg = 0;
            for (int i = 0; i < count; i += step)
            {
                Vector3 d = verts[i] - center;
                float t = eMax.Dot(d);
                float h = eMid.Dot(d);
                if (t > tMid) { varPos += h * h; cPos++; }
                else { varNeg += h * h; cNeg++; }
            }
            varPos = cPos > 0 ? varPos / cPos : 0f;
            varNeg = cNeg > 0 ? varNeg / cNeg : 0f;

            if (varPos > varNeg)
            {
                eMax = -eMax;
            }

            // 2. Upward axis: eMid points towards top sight/slide
            if (eMid.Dot(Vector3.Up) < -0.1f)
            {
                eMid = -eMid;
            }

            // 3. Lateral axis: eMin = eMid x eMax forms a pure right-handed orthonormal triad
            eMin = eMid.Cross(eMax).Normalized();
            eMid = eMax.Cross(eMin).Normalized();

            Basis pcaBasis = new Basis(
                eMin.X, eMin.Y, eMin.Z,
                eMid.X, eMid.Y, eMid.Z,
                eMax.X, eMax.Y, eMax.Z
            );

            Vector3 extMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 extMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < count; i += step)
            {
                Vector3 local = pcaBasis * (verts[i] - center);
                extMin = extMin.Min(local);
                extMax = extMax.Max(local);
            }
            Vector3 extents = extMax - extMin;

            return (center, pcaBasis, extents);
        }

        public static Dictionary<string, SocketFrame> ComputeSocketGroupFrames(
            List<MeshInstance3D> meshes,
            Node3D importedScene,
            RiggingOptions options,
            Node3D heroNode,
            Skeleton3D heroSkeleton)
        {
            var frames = new Dictionary<string, SocketFrame>();
            if (options?.SubmeshAttachmentSockets == null || meshes == null) return frames;

            var socketVerts = new Dictionary<string, List<Vector3>>();
            var socketReplacements = new Dictionary<string, string>();

            foreach (var mi in meshes)
            {
                if (mi.Mesh == null || mi.Mesh.GetSurfaceCount() == 0) continue;
                string meshName = mi.Name.ToString();
                if (!options.SubmeshAttachmentSockets.TryGetValue(meshName, out string socket) || string.IsNullOrEmpty(socket))
                    continue;

                if (options.SubmeshReplacementMap != null && options.SubmeshReplacementMap.TryGetValue(meshName, out string rep) && !string.IsNullOrEmpty(rep) && rep != "[NONE]")
                {
                    socketReplacements[socket] = rep;
                }

                Transform3D toHero = options.AlignedTransform * ComputeTransformRelativeToRoot(mi, importedScene);

                if (!socketVerts.TryGetValue(socket, out var vList))
                {
                    vList = new List<Vector3>();
                    socketVerts[socket] = vList;
                }

                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                {
                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count == 0) continue;
                    var verts = GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    if (verts == null || verts.Length == 0) continue;

                    for (int v = 0; v < verts.Length; v++)
                    {
                        vList.Add(toHero * verts[v]);
                    }
                }
            }

            foreach (var (socket, verts) in socketVerts)
            {
                if (verts.Count == 0) continue;

                var (center, pcaBasis, _) = ComputeMeshPrincipalFrame(verts);

                var frame = new SocketFrame
                {
                    Center = center,
                    PcaBasis = pcaBasis,
                    TargetOrientation = Basis.Identity,
                    HasTargetReplacement = false
                };

                int tgtBone = heroSkeleton != null ? heroSkeleton.FindBone(socket) : -1;
                if (tgtBone < 0 && heroSkeleton != null)
                {
                    tgtBone = FindBoneAny(heroSkeleton, "weapon_hand_R", "weapon_hand_L", "hand_R", "hand_L", "head");
                }

                Vector3 boneOrigin = (tgtBone >= 0 && heroSkeleton != null)
                    ? heroSkeleton.GetBoneGlobalRest(tgtBone).Origin
                    : Vector3.Zero;
                frame.SocketCenter = boneOrigin;

                if (socketReplacements.TryGetValue(socket, out string repName) && heroNode != null)
                {
                    MeshInstance3D heroWeaponMesh = null;
                    foreach (var mi in CollectMeshes(heroNode))
                    {
                        if (mi.Name.ToString().Equals(repName, StringComparison.OrdinalIgnoreCase))
                        {
                            heroWeaponMesh = mi;
                            break;
                        }
                    }

                    if (heroWeaponMesh != null && heroWeaponMesh.Mesh != null)
                    {
                        var heroWpVerts = new List<Vector3>();
                        Transform3D heroMiToHero = ComputeTransformRelativeToRoot(heroWeaponMesh, heroNode);

                        for (int s = 0; s < heroWeaponMesh.Mesh.GetSurfaceCount(); s++)
                        {
                            var arrays = heroWeaponMesh.Mesh.SurfaceGetArrays(s);
                            if (arrays == null || arrays.Count == 0) continue;
                            var wVerts = GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                            if (wVerts != null)
                            {
                                for (int v = 0; v < wVerts.Length; v++)
                                {
                                    heroWpVerts.Add(heroMiToHero * wVerts[v]);
                                }
                            }
                        }

                        if (heroWpVerts.Count >= 4)
                        {
                            var (hCenter, hPcaBasis, _) = ComputeMeshPrincipalFrame(heroWpVerts);
                            frame.SocketCenter = hCenter;
                            frame.TargetOrientation = hPcaBasis.Inverse();
                            frame.HasTargetReplacement = true;
                            GD.Print($"[CustomModelAutoRigger] Matched hero replacement weapon '{repName}': center={hCenter}");
                        }
                    }
                }

                frames[socket] = frame;
            }

            return frames;
        }

        public static Dictionary<string, Vector3> ComputeSocketGroupCenters(
            List<MeshInstance3D> meshes,
            Node3D importedScene,
            RiggingOptions options)
        {
            var frames = ComputeSocketGroupFrames(meshes, importedScene, options, null, null);
            var dict = new Dictionary<string, Vector3>();
            foreach (var (k, v) in frames) dict[k] = v.Center;
            return dict;
        }

        public static Vector3 ComputeWeaponStraightenEulerDeg(IList<Vector3> verts)
        {
            return Vector3.Zero;
        }

        public static (float ArmSpreadRad, float ArmPitchRad, float LegSpreadRad) MeasureLimbAngleDeltas(
            Skeleton3D sourceSkeleton,
            Skeleton3D targetSkeleton,
            Dictionary<int, int> sourceToTargetMap)
        {
            float armSpread = 0f;
            float armPitch = 0f;
            float legSpread = 0f;

            if (sourceSkeleton == null || targetSkeleton == null) return (0f, 0f, 0f);

            int tgtUpperL = FindBoneAny(targetSkeleton, "arm_upper_L", "arm_upper_l", "upperarm_l", "leftarm");
            int tgtLowerL = FindBoneAny(targetSkeleton, "arm_lower_L", "arm_lower_l", "forearm_l");
            int srcUpperL = FindSourceBoneForTarget(sourceToTargetMap, tgtUpperL);
            int srcLowerL = FindSourceBoneForTarget(sourceToTargetMap, tgtLowerL);

            if (tgtUpperL != -1 && tgtLowerL != -1 && srcUpperL != -1 && srcLowerL != -1)
            {
                int tgtHandL = FindBoneAny(targetSkeleton, "hand_L", "hand_l", "wrist_l");
                int srcHandL = FindSourceBoneForTarget(sourceToTargetMap, tgtHandL);

                Vector3 tgtArmDir;
                Vector3 srcArmDir;

                // Prefer full arm direction (shoulder -> wrist) to capture overall limb angle and elbow pitch
                if (tgtHandL != -1 && srcHandL != -1)
                {
                    tgtArmDir = (targetSkeleton.GetBoneGlobalRest(tgtHandL).Origin - targetSkeleton.GetBoneGlobalRest(tgtUpperL).Origin).Normalized();
                    srcArmDir = (sourceSkeleton.GetBoneGlobalRest(srcHandL).Origin - sourceSkeleton.GetBoneGlobalRest(srcUpperL).Origin).Normalized();
                }
                else
                {
                    tgtArmDir = (targetSkeleton.GetBoneGlobalRest(tgtLowerL).Origin - targetSkeleton.GetBoneGlobalRest(tgtUpperL).Origin).Normalized();
                    srcArmDir = (sourceSkeleton.GetBoneGlobalRest(srcLowerL).Origin - sourceSkeleton.GetBoneGlobalRest(srcUpperL).Origin).Normalized();
                }

                // Convert source direction from glTF coordinate space (facing -Z, +X right) to hero space (facing +Z, +X left)
                Vector3 srcInHero = new Vector3(-srcArmDir.X, srcArmDir.Y, -srcArmDir.Z).Normalized();

                // Pitch angle around X axis (sagittal plane: forward angle from down -Y towards forward +Z)
                float tgtPitch = Mathf.Atan2(tgtArmDir.Z, -tgtArmDir.Y);
                float srcPitch = Mathf.Atan2(srcInHero.Z, -srcInHero.Y);
                armPitch = tgtPitch - srcPitch;

                // Spread angle around Z axis (coronal plane: lateral elevation angle from down -Y towards left +X)
                float tgtSpread = Mathf.Atan2(tgtArmDir.X, -tgtArmDir.Y);
                float srcSpread = Mathf.Atan2(srcInHero.X, -srcInHero.Y);
                armSpread = tgtSpread - srcSpread;

                GD.Print($"[MeasureLimbAngleDeltas] Arm deltas: Pitch = {Mathf.RadToDeg(armPitch):+0.0;-0.0;0.0}°, Spread = {Mathf.RadToDeg(armSpread):+0.0;-0.0;0.0}°");
            }

            // Leg spread: compare source thigh elevation vs target hero stance
            int tgtThighL = targetSkeleton.FindBone("leg_upper_L");
            int tgtCalfL = targetSkeleton.FindBone("leg_lower_L");
            int srcThighL = FindSourceBoneForTarget(sourceToTargetMap, tgtThighL);
            int srcCalfL = FindSourceBoneForTarget(sourceToTargetMap, tgtCalfL);

            if (tgtThighL != -1 && tgtCalfL != -1 && srcThighL != -1 && srcCalfL != -1)
            {
                Vector3 tgtLegDir = (targetSkeleton.GetBoneGlobalRest(tgtCalfL).Origin - targetSkeleton.GetBoneGlobalRest(tgtThighL).Origin).Normalized();
                Vector3 srcLegDir = (sourceSkeleton.GetBoneGlobalRest(srcCalfL).Origin - sourceSkeleton.GetBoneGlobalRest(srcThighL).Origin).Normalized();

                Vector3 srcLegInHero = new Vector3(-srcLegDir.X, srcLegDir.Y, -srcLegDir.Z).Normalized();

                float tgtLegSpread = Mathf.Atan2(tgtLegDir.X, -tgtLegDir.Y);
                float srcLegSpread = Mathf.Atan2(srcLegInHero.X, -srcLegInHero.Y);

                float diff = tgtLegSpread - srcLegSpread;
                if (Mathf.Abs(diff) > 0.02f)
                {
                    legSpread = diff;
                }
                GD.Print($"[MeasureLimbAngleDeltas] Leg stance delta: Spread = {Mathf.RadToDeg(legSpread):+0.0;-0.0;0.0}°");
            }

            return (armSpread, armPitch, legSpread);
        }

        private static int FindSourceBoneForTarget(Dictionary<int, int> map, int tgtBone)
        {
            if (map == null || tgtBone == -1) return -1;
            foreach (var (s, t) in map)
            {
                if (t == tgtBone) return s;
            }
            return -1;
        }

        public static Quaternion ShortestArc(Vector3 vFrom, Vector3 vTo)
        {
            float lenFrom = vFrom.Length();
            float lenTo = vTo.Length();
            if (lenFrom < 0.0001f || lenTo < 0.0001f) return Quaternion.Identity;

            Vector3 u = vFrom / lenFrom;
            Vector3 v = vTo / lenTo;

            float dot = Mathf.Clamp(u.Dot(v), -1f, 1f);
            if (dot > 0.999999f) return Quaternion.Identity;
            if (dot < -0.999999f)
            {
                Vector3 orth = MathF.Abs(u.X) < 0.8f ? u.Cross(Vector3.Right) : u.Cross(Vector3.Up);
                return new Quaternion(orth.Normalized(), Mathf.Pi);
            }

            Vector3 c = u.Cross(v);
            return new Quaternion(c.X, c.Y, c.Z, 1.0f + dot).Normalized();
        }

        private static ArmSegmentRegistration BuildArmSegmentRegistration(
            Skeleton3D sourceSkeleton,
            Skeleton3D targetSkeleton,
            Dictionary<int, int> sourceToTargetMap,
            string upperName,
            string lowerName,
            string handName,
            Transform3D skelToHero,
            bool keepFingersRigid = false)
        {
            if (sourceSkeleton == null || targetSkeleton == null || sourceToTargetMap == null) return null;

            int tgtUpper = FindBoneAny(targetSkeleton, upperName);
            int tgtLower = FindBoneAny(targetSkeleton, lowerName);
            int tgtHand = FindBoneAny(targetSkeleton, handName);

            if (tgtUpper == -1 || tgtLower == -1 || tgtHand == -1) return null;

            int srcUpper = FindSourceBoneForTarget(sourceToTargetMap, tgtUpper);
            int srcLower = FindSourceBoneForTarget(sourceToTargetMap, tgtLower);
            int srcHand = FindSourceBoneForTarget(sourceToTargetMap, tgtHand);

            if (srcUpper == -1 || srcLower == -1 || srcHand == -1) return null;

            Vector3 As = skelToHero * sourceSkeleton.GetBoneGlobalRest(srcUpper).Origin;
            Vector3 Bs = skelToHero * sourceSkeleton.GetBoneGlobalRest(srcLower).Origin;
            Vector3 Cs = skelToHero * sourceSkeleton.GetBoneGlobalRest(srcHand).Origin;

            Vector3 At = targetSkeleton.GetBoneGlobalRest(tgtUpper).Origin;
            Vector3 Bt = targetSkeleton.GetBoneGlobalRest(tgtLower).Origin;
            Vector3 Ct = targetSkeleton.GetBoneGlobalRest(tgtHand).Origin;

            float Ls1 = (Bs - As).Length();
            float Lt1 = (Bt - At).Length();
            if (Ls1 < 0.001f || Lt1 < 0.001f) return null;

            float Ls2 = (Cs - Bs).Length();
            float Lt2 = (Ct - Bt).Length();
            if (Ls2 < 0.001f || Lt2 < 0.001f) return null;

            Vector3 uS1 = (Bs - As) / Ls1;
            Vector3 uT1 = (Bt - At) / Lt1;
            Quaternion R1 = ShortestArc(uS1, uT1);
            float sigma1 = Mathf.Clamp(Lt1 / Ls1, 0.75f, 1.35f);

            Vector3 uS2 = (Cs - Bs) / Ls2;
            Vector3 uT2 = (Ct - Bt) / Lt2;
            Quaternion R2 = ShortestArc(uS2, uT2);
            float sigma2 = Mathf.Clamp(Lt2 / Ls2, 0.75f, 1.35f);

            GD.Print($"[CustomModelAutoRigger] Auto-Snap Arm Registration for '{upperName}': Upper L_src={Ls1:F3}m, L_tgt={Lt1:F3}m, sigma={sigma1:F2}; Forearm L_src={Ls2:F3}m, L_tgt={Lt2:F3}m, sigma={sigma2:F2}");

            bool isLeft = upperName.EndsWith("_L") || upperName.Contains("_l");
            HandFingersRegistration fingersReg = null;
            if (!keepFingersRigid)
            {
                fingersReg = BuildFingerSegmentRegistration(sourceSkeleton, targetSkeleton, sourceToTargetMap, isLeft, tgtHand, Cs, Ct, R2, skelToHero);
            }

            return new ArmSegmentRegistration
            {
                TargetUpperBone = tgtUpper,
                TargetLowerBone = tgtLower,
                TargetHandBone = tgtHand,
                Fingers = fingersReg,
                As = As,
                Bs = Bs,
                Cs = Cs,
                At = At,
                Bt = Bt,
                Ct = Ct,
                UnitSourceUpper = uS1,
                UnitTargetUpper = uT1,
                LengthSourceUpper = Ls1,
                LengthTargetUpper = Lt1,
                SigmaUpper = sigma1,
                RotationUpper = R1,
                UnitSourceLower = uS2,
                UnitTargetLower = uT2,
                LengthSourceLower = Ls2,
                LengthTargetLower = Lt2,
                SigmaLower = sigma2,
                RotationLower = R2
            };
        }

        private static HandFingersRegistration BuildFingerSegmentRegistration(
            Skeleton3D sourceSkeleton,
            Skeleton3D targetSkeleton,
            Dictionary<int, int> sourceToTargetMap,
            bool isLeft,
            int tgtHandBone,
            Vector3 Cs,
            Vector3 Ct,
            Quaternion Rlower,
            Transform3D skelToHero)
        {
            if (sourceSkeleton == null || targetSkeleton == null || sourceToTargetMap == null) return null;

            string side = isLeft ? "L" : "R";
            var fingersReg = new HandFingersRegistration
            {
                IsLeft = isLeft,
                TargetHandBone = tgtHandBone
            };

            string[] fingerNames = { "thumb", "index", "middle", "ring", "pinky" };

            foreach (var fname in fingerNames)
            {
                for (int i = 0; i < 3; i++)
                {
                    string tgtBoneAName = $"finger_{fname}_{i}_{side}";
                    int tgtBoneA = FindBoneAny(targetSkeleton, tgtBoneAName, tgtBoneAName.ToLowerInvariant());
                    if (tgtBoneA == -1) continue;

                    int srcBoneA = FindSourceBoneForTarget(sourceToTargetMap, tgtBoneA);
                    if (srcBoneA == -1) continue;

                    int tgtBoneB = -1;
                    int srcBoneB = -1;

                    if (i < 2)
                    {
                        string tgtBoneBName = $"finger_{fname}_{i + 1}_{side}";
                        tgtBoneB = FindBoneAny(targetSkeleton, tgtBoneBName, tgtBoneBName.ToLowerInvariant());
                        if (tgtBoneB != -1)
                        {
                            srcBoneB = FindSourceBoneForTarget(sourceToTargetMap, tgtBoneB);
                        }
                    }
                    else
                    {
                        tgtBoneB = FindBoneAny(targetSkeleton,
                            $"finger_{fname}_end_{side}", $"finger_{fname}_leaf_{side}",
                            $"finger_{fname}_end_{side}".ToLowerInvariant(), $"finger_{fname}_leaf_{side}".ToLowerInvariant());
                        if (tgtBoneB != -1)
                        {
                            srcBoneB = FindSourceBoneForTarget(sourceToTargetMap, tgtBoneB);
                        }
                    }

                    Vector3 At = targetSkeleton.GetBoneGlobalRest(tgtBoneA).Origin;
                    Vector3 Bt;

                    if (tgtBoneB != -1)
                    {
                        Bt = targetSkeleton.GetBoneGlobalRest(tgtBoneB).Origin;
                    }
                    else if (i > 0)
                    {
                        string tgtPrevName = $"finger_{fname}_{i - 1}_{side}";
                        int tgtPrev = FindBoneAny(targetSkeleton, tgtPrevName, tgtPrevName.ToLowerInvariant());
                        Vector3 prevPos = tgtPrev != -1 ? targetSkeleton.GetBoneGlobalRest(tgtPrev).Origin : At - Vector3.Forward * 0.02f;
                        Vector3 dir = (At - prevPos);
                        Bt = At + (dir.Length() > 0.001f ? dir.Normalized() * (dir.Length() * 0.8f) : Vector3.Forward * 0.02f);
                    }
                    else
                    {
                        Bt = At + Vector3.Forward * 0.025f;
                    }

                    Vector3 rawAs = skelToHero * sourceSkeleton.GetBoneGlobalRest(srcBoneA).Origin;
                    Vector3 As = Ct + Rlower * (rawAs - Cs);
                    Vector3 Bs;

                    if (srcBoneB != -1)
                    {
                        Vector3 rawBs = skelToHero * sourceSkeleton.GetBoneGlobalRest(srcBoneB).Origin;
                        Bs = Ct + Rlower * (rawBs - Cs);
                    }
                    else if (sourceSkeleton.GetBoneChildren(srcBoneA).Length > 0)
                    {
                        int childSrc = sourceSkeleton.GetBoneChildren(srcBoneA)[0];
                        Vector3 rawBs = skelToHero * sourceSkeleton.GetBoneGlobalRest(childSrc).Origin;
                        Bs = Ct + Rlower * (rawBs - Cs);
                    }
                    else if (i > 0)
                    {
                        string tgtPrevName = $"finger_{fname}_{i - 1}_{side}";
                        int tgtPrev = FindBoneAny(targetSkeleton, tgtPrevName, tgtPrevName.ToLowerInvariant());
                        int srcPrev = tgtPrev != -1 ? FindSourceBoneForTarget(sourceToTargetMap, tgtPrev) : -1;
                        Vector3 rawPrev = srcPrev != -1 ? (skelToHero * sourceSkeleton.GetBoneGlobalRest(srcPrev).Origin) : rawAs - Vector3.Forward * 0.02f;
                        Vector3 prevAs = Ct + Rlower * (rawPrev - Cs);
                        Vector3 dir = (As - prevAs);
                        Bs = As + (dir.Length() > 0.001f ? dir.Normalized() * (dir.Length() * 0.8f) : Vector3.Forward * 0.02f);
                    }
                    else
                    {
                        Bs = As + Vector3.Forward * 0.025f;
                    }

                    float Ls = (Bs - As).Length();
                    float Lt = (Bt - At).Length();
                    if (Ls < 0.001f || Lt < 0.001f) continue;

                    Vector3 uS = (Bs - As) / Ls;
                    Vector3 uT = (Bt - At) / Lt;
                    Quaternion R = ShortestArc(uS, uT);
                    float sigma = Mathf.Clamp(Lt / Ls, 0.85f, 1.15f);

                    fingersReg.PhalanxesByTargetBone[tgtBoneA] = new FingerPhalanxSegment
                    {
                        TargetBone = tgtBoneA,
                        Name = tgtBoneAName,
                        As = As,
                        Bs = Bs,
                        At = At,
                        Bt = Bt,
                        UnitSource = uS,
                        UnitTarget = uT,
                        LengthSource = Ls,
                        LengthTarget = Lt,
                        Sigma = sigma,
                        Rotation = R
                    };
                }
            }

            GD.Print($"[CustomModelAutoRigger] Registered {fingersReg.PhalanxesByTargetBone.Count} finger phalanxes for Hand {side}.");
            return fingersReg.PhalanxesByTargetBone.Count > 0 ? fingersReg : null;
        }

        private static LegSegmentRegistration BuildLegSegmentRegistration(
            Skeleton3D sourceSkeleton,
            Skeleton3D targetSkeleton,
            Dictionary<int, int> sourceToTargetMap,
            string thighName,
            string calfName,
            string ankleName,
            Transform3D skelToHero)
        {
            if (sourceSkeleton == null || targetSkeleton == null || sourceToTargetMap == null) return null;

            int tgtThigh = FindBoneAny(targetSkeleton, thighName, thighName.ToLowerInvariant());
            int tgtCalf = FindBoneAny(targetSkeleton, calfName, calfName.ToLowerInvariant());
            int tgtAnkle = FindBoneAny(targetSkeleton, ankleName, ankleName.ToLowerInvariant());

            if (tgtThigh == -1 || tgtCalf == -1 || tgtAnkle == -1) return null;

            int srcThigh = FindSourceBoneForTarget(sourceToTargetMap, tgtThigh);
            int srcCalf = FindSourceBoneForTarget(sourceToTargetMap, tgtCalf);
            int srcAnkle = FindSourceBoneForTarget(sourceToTargetMap, tgtAnkle);

            if (srcThigh == -1 || srcCalf == -1 || srcAnkle == -1) return null;

            Vector3 As = skelToHero * sourceSkeleton.GetBoneGlobalRest(srcThigh).Origin;
            Vector3 Bs = skelToHero * sourceSkeleton.GetBoneGlobalRest(srcCalf).Origin;
            Vector3 Cs = skelToHero * sourceSkeleton.GetBoneGlobalRest(srcAnkle).Origin;

            Vector3 At = targetSkeleton.GetBoneGlobalRest(tgtThigh).Origin;
            Vector3 Bt = targetSkeleton.GetBoneGlobalRest(tgtCalf).Origin;
            Vector3 Ct = targetSkeleton.GetBoneGlobalRest(tgtAnkle).Origin;

            float Ls1 = (Bs - As).Length();
            float Lt1 = (Bt - At).Length();
            if (Ls1 < 0.001f || Lt1 < 0.001f) return null;

            float Ls2 = (Cs - Bs).Length();
            float Lt2 = (Ct - Bt).Length();
            if (Ls2 < 0.001f || Lt2 < 0.001f) return null;

            Vector3 uS1 = (Bs - As) / Ls1;
            Vector3 uT1 = (Bt - At) / Lt1;
            Quaternion R1 = ShortestArc(uS1, uT1);
            float sigma1 = Mathf.Clamp(Lt1 / Ls1, 0.75f, 1.35f);

            Vector3 uS2 = (Cs - Bs) / Ls2;
            Vector3 uT2 = (Ct - Bt) / Lt2;
            Quaternion R2 = ShortestArc(uS2, uT2);
            float sigma2 = Mathf.Clamp(Lt2 / Ls2, 0.75f, 1.35f);

            GD.Print($"[CustomModelAutoRigger] Auto-Snap Leg Registration for '{thighName}': Thigh L_src={Ls1:F3}m, L_tgt={Lt1:F3}m, sigma={sigma1:F2}; Calf L_src={Ls2:F3}m, L_tgt={Lt2:F3}m, sigma={sigma2:F2}");

            // Shift uniform vertical offset of Ct once for the whole foot so the lowest vertex of the boot touches Y = 0 rigidly
            float bootSoleOffset = Mathf.Max(0.0f, Cs.Y);
            Vector3 CtGrounded = new Vector3(Ct.X, bootSoleOffset, Ct.Z);

            return new LegSegmentRegistration
            {
                TargetThighBone = tgtThigh,
                TargetCalfBone = tgtCalf,
                TargetAnkleBone = tgtAnkle,
                As = As,
                Bs = Bs,
                Cs = Cs,
                At = At,
                Bt = Bt,
                Ct = Ct,
                CtGrounded = CtGrounded,
                UnitSourceThigh = uS1,
                UnitTargetThigh = uT1,
                LengthSourceThigh = Ls1,
                LengthTargetThigh = Lt1,
                SigmaThigh = sigma1,
                RotationThigh = R1,
                UnitSourceCalf = uS2,
                UnitTargetCalf = uT2,
                LengthSourceCalf = Ls2,
                LengthTargetCalf = Lt2,
                SigmaCalf = sigma2,
                RotationCalf = R2
            };
        }

        private static HeadSegmentRegistration BuildHeadSegmentRegistration(
            Skeleton3D sourceSkeleton,
            Skeleton3D targetSkeleton,
            Dictionary<int, int> sourceToTargetMap,
            string neckName,
            string headName,
            Transform3D skelToHero)
        {
            if (sourceSkeleton == null || targetSkeleton == null || sourceToTargetMap == null) return null;

            int tgtNeck = FindBoneAny(targetSkeleton, neckName, "neck_0", "neck");
            int tgtHead = FindBoneAny(targetSkeleton, headName, "head");

            if (tgtNeck == -1 || tgtHead == -1) return null;

            int srcNeck = FindSourceBoneForTarget(sourceToTargetMap, tgtNeck);
            int srcHead = FindSourceBoneForTarget(sourceToTargetMap, tgtHead);

            if (srcNeck == -1 || srcHead == -1) return null;

            Vector3 Ns = skelToHero * sourceSkeleton.GetBoneGlobalRest(srcNeck).Origin;
            Vector3 Hs = skelToHero * sourceSkeleton.GetBoneGlobalRest(srcHead).Origin;

            Vector3 Nt = targetSkeleton.GetBoneGlobalRest(tgtNeck).Origin;
            Vector3 Ht = targetSkeleton.GetBoneGlobalRest(tgtHead).Origin;

            Vector3 dirS = Hs - Ns;
            Vector3 dirT = Ht - Nt;
            Quaternion Rneck = Quaternion.Identity;

            if (dirS.Length() > 0.001f && dirT.Length() > 0.001f)
            {
                Rneck = ShortestArc(dirS.Normalized(), dirT.Normalized());
                // Clamp tilt angle to max 15 degrees to keep head upright
                float angle = Rneck.GetAngle();
                if (angle > Mathf.DegToRad(15f))
                {
                    Vector3 axis = Rneck.GetAxis().Normalized();
                    Rneck = new Quaternion(axis, Mathf.DegToRad(15f));
                }
            }

            GD.Print($"[CustomModelAutoRigger] Auto-Snap Head Registration: Head offset={Ht - Hs}, Neck tilt={Mathf.RadToDeg(Rneck.GetAngle()):F1}°");

            return new HeadSegmentRegistration
            {
                TargetNeckBone = tgtNeck,
                TargetHeadBone = tgtHead,
                Ns = Ns,
                Hs = Hs,
                Nt = Nt,
                Ht = Ht,
                RotationNeck = Rneck
            };
        }

        private static void PoseHeroArmsForPreset(Skeleton3D skeleton, PosePreset preset, float customAngleDeg)
        {
            float targetAngleDeg = preset == PosePreset.TPose ? 45f : customAngleDeg;

            // Pose upper arms up towards horizontal
            int armUpperL = FindBoneAny(skeleton, "arm_upper_L", "arm_upper_l", "upperarm_l", "leftarm");
            int armUpperR = FindBoneAny(skeleton, "arm_upper_R", "arm_upper_r", "upperarm_r", "rightarm");

            if (armUpperL != -1)
            {
                // In Deadlock, positive Z rotation on arm_upper_L raises the arm up
                float radL = Mathf.DegToRad(targetAngleDeg);
                Quaternion rotL = new Quaternion(Vector3.Forward, radL);
                skeleton.SetBonePoseRotation(armUpperL, skeleton.GetBonePoseRotation(armUpperL) * rotL);
            }

            if (armUpperR != -1)
            {
                // Opposite Z rotation on arm_upper_R raises the right arm up
                float radR = Mathf.DegToRad(-targetAngleDeg);
                Quaternion rotR = new Quaternion(Vector3.Forward, radR);
                skeleton.SetBonePoseRotation(armUpperR, skeleton.GetBonePoseRotation(armUpperR) * rotR);
            }

            skeleton.ForceUpdateAllBoneTransforms();
        }

        private static List<DonorVertex> ExtractHeroDonorVertices(
            Node3D heroNode,
            Skeleton3D skeleton,
            Transform3D[] restGlobal,
            Transform3D[] posedGlobal,
            Dictionary<int, int> bindToSkeletonMap,
            RiggingOptions options)
        {
            var result = new List<DonorVertex>(1500);
            var meshes = CollectOriginalHeroMeshes(heroNode);

            int boneCount = Math.Min(restGlobal.Length, posedGlobal.Length);
            Transform3D[] skinMatrices = new Transform3D[boneCount];
            for (int b = 0; b < boneCount; b++)
            {
                skinMatrices[b] = posedGlobal[b] * restGlobal[b].AffineInverse();
            }

            float subGridSize = 0.095f; // 9.5 cm on-the-fly subsampling
            var seenCells = new HashSet<Vector3I>(1000);

            foreach (var mi in meshes)
            {
                if (mi.Mesh == null || mi.Mesh.GetSurfaceCount() == 0) continue;
                if (mi.HasMeta("IsCustomImportedMesh")) continue;

                string rawName = mi.Name.ToString().ToLowerInvariant();
                if (rawName.Contains("weapon") || rawName.Contains("gun") || rawName.Contains("cloth") || rawName.Contains("cape"))
                {
                    // Skip weapon and cloth props from donor cloud
                    continue;
                }

                Transform3D meshToHero = ComputeTransformRelativeToRoot(mi, heroNode);

                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                {
                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count == 0) continue;

                    var verts = GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    var bones = arrays.Count > (int)Mesh.ArrayType.Bones ? GetInt32Array(arrays[(int)Mesh.ArrayType.Bones]) : null;
                    var weights = arrays.Count > (int)Mesh.ArrayType.Weights ? GetFloat32Array(arrays[(int)Mesh.ArrayType.Weights]) : null;

                    if (verts == null || bones == null || weights == null) continue;

                    for (int v = 0; v < verts.Length; v++)
                    {
                        int rawBind = bones[v * 4 + 0];
                        int skelBone = bindToSkeletonMap.TryGetValue(rawBind, out int sb) ? sb : rawBind;
                        bool isHandOrFinger = false;
                        if (skelBone >= 0 && skelBone < skeleton.GetBoneCount())
                        {
                            string bName = skeleton.GetBoneName(skelBone).ToLowerInvariant();
                            isHandOrFinger = bName.Contains("hand") || bName.Contains("finger") || bName.Contains("thumb") ||
                                             bName.Contains("wrist") || bName.Contains("index") || bName.Contains("pinky") || bName.Contains("ring");
                        }

                        float effectiveSubGrid = isHandOrFinger ? 0.015f : subGridSize;
                        Vector3 restLocal = meshToHero * verts[v];
                        Vector3I cell = new Vector3I(
                            Mathf.FloorToInt(restLocal.X / effectiveSubGrid),
                            Mathf.FloorToInt(restLocal.Y / effectiveSubGrid),
                            Mathf.FloorToInt(restLocal.Z / effectiveSubGrid)
                        );
                        if (!seenCells.Add(cell)) continue;

                        int[] bIndices = new int[4];
                        float[] bWeights = new float[4];

                        for (int k = 0; k < 4; k++)
                        {
                            int rb = bones[v * 4 + k];
                            int sk = bindToSkeletonMap.TryGetValue(rb, out int sVal) ? sVal : rb;
                            bIndices[k] = sk;
                            bWeights[k] = weights[v * 4 + k];
                        }

                        if (options != null && options.ExcludeClothHairBones)
                        {
                            for (int k = 0; k < 4; k++)
                            {
                                if (bWeights[k] > 0.001f && bIndices[k] >= 0 && bIndices[k] < skeleton.GetBoneCount())
                                {
                                    string bName = skeleton.GetBoneName(bIndices[k]).ToLowerInvariant();
                                    if (bName.Contains("cloth") || bName.Contains("cape") || bName.Contains("skirt") ||
                                        bName.Contains("hair") || bName.Contains("coat") || bName.Contains("dress") ||
                                        bName.Contains("tassel") || bName.Contains("ponytail") || bName.Contains("ribbon") ||
                                        bName.Contains("root") || bName.Contains("weapon") || bName.Contains("gun") ||
                                        bName.Contains("world") || bName.Contains("scene root") || bName.Contains("attach"))
                                    {
                                        bWeights[k] = 0f;
                                    }
                                }
                            }
                            float sum = bWeights[0] + bWeights[1] + bWeights[2] + bWeights[3];
                            if (sum <= 0.0001f)
                            {
                                continue;
                            }
                            for (int k = 0; k < 4; k++) bWeights[k] /= sum;
                        }

                        // Compute deformed position via CPU LBS using precomputed skinMatrices
                        Vector3 posedPos = Vector3.Zero;
                        for (int k = 0; k < 4; k++)
                        {
                            float w = bWeights[k];
                            if (w <= 0.0001f) continue;

                            int b = bIndices[k];
                            if (b >= 0 && b < boneCount)
                            {
                                posedPos += w * (skinMatrices[b] * restLocal);
                            }
                            else
                            {
                                posedPos += w * restLocal;
                            }
                        }

                        result.Add(new DonorVertex
                        {
                            RestPosition = restLocal,
                            PosedPosition = posedPos,
                            BoneIndices = bIndices,
                            BoneWeights = bWeights
                        });
                    }
                }
            }

            // Fallback: If 0 donor vertices extracted, generate synthetic donor vertices along Anatomical Skeleton Bone Capsules so KNN NEVER fails!
            if (result.Count == 0)
            {
                GD.Print("[CustomModelAutoRigger] Fallback: Generating anatomical capsule donor cloud from skeleton...");
                var capsules = BuildCapsuleSegments(skeleton, posedGlobal);
                foreach (var seg in capsules)
                {
                    Vector3 delta = seg.EndPos - seg.StartPos;
                    float len = delta.Length();
                    Vector3 dir = len > 0.001f ? delta / len : Vector3.Up;
                    Vector3 side = MathF.Abs(dir.Y) < 0.9f ? dir.Cross(Vector3.Up).Normalized() : dir.Cross(Vector3.Right).Normalized();
                    Vector3 up = dir.Cross(side).Normalized();

                    for (int step = 0; step <= 8; step++)
                    {
                        float t = step / 8.0f;
                        Vector3 c = seg.StartPos.Lerp(seg.EndPos, t);
                        float r = seg.Radius * 0.8f;

                        result.Add(new DonorVertex { RestPosition = c, PosedPosition = c, BoneIndices = new[] { seg.BoneIndex, 0, 0, 0 }, BoneWeights = new[] { 1f, 0f, 0f, 0f } });
                        result.Add(new DonorVertex { RestPosition = c + side * r, PosedPosition = c + side * r, BoneIndices = new[] { seg.BoneIndex, 0, 0, 0 }, BoneWeights = new[] { 1f, 0f, 0f, 0f } });
                        result.Add(new DonorVertex { RestPosition = c - side * r, PosedPosition = c - side * r, BoneIndices = new[] { seg.BoneIndex, 0, 0, 0 }, BoneWeights = new[] { 1f, 0f, 0f, 0f } });
                        result.Add(new DonorVertex { RestPosition = c + up * r, PosedPosition = c + up * r, BoneIndices = new[] { seg.BoneIndex, 0, 0, 0 }, BoneWeights = new[] { 1f, 0f, 0f, 0f } });
                        result.Add(new DonorVertex { RestPosition = c - up * r, PosedPosition = c - up * r, BoneIndices = new[] { seg.BoneIndex, 0, 0, 0 }, BoneWeights = new[] { 1f, 0f, 0f, 0f } });
                    }
                }
            }

            return result;
        }

        private static Dictionary<Vector3I, List<int>> BuildSpatialHashGrid(List<DonorVertex> donors, float cellSize)
        {
            var grid = new Dictionary<Vector3I, List<int>>();
            for (int i = 0; i < donors.Count; i++)
            {
                Vector3 p = donors[i].PosedPosition;
                Vector3I cell = new Vector3I(
                    Mathf.FloorToInt(p.X / cellSize),
                    Mathf.FloorToInt(p.Y / cellSize),
                    Mathf.FloorToInt(p.Z / cellSize)
                );

                if (!grid.TryGetValue(cell, out var list))
                {
                    list = new List<int>();
                    grid[cell] = list;
                }
                list.Add(i);
            }
            return grid;
        }

        private static void QueryWeightsWithCapsuleFallback(
            Vector3 queryPoint,
            Dictionary<Vector3I, List<int>> grid,
            List<DonorVertex> donors,
            List<BoneCapsuleSegment> capsules,
            int kNearest,
            float searchRadius,
            float cellSize,
            int[] outIndices,
            float[] outWeights,
            int outOffset)
        {
            float r = searchRadius;

            Vector3I centerCell = new Vector3I(
                Mathf.FloorToInt(queryPoint.X / cellSize),
                Mathf.FloorToInt(queryPoint.Y / cellSize),
                Mathf.FloorToInt(queryPoint.Z / cellSize)
            );

            Span<int> bestDonors = stackalloc int[8];
            Span<float> bestDistSq = stackalloc float[8];
            bestDistSq.Fill(float.MaxValue);
            int candidateCount = 0;

            float rSq = r * r;

            // 1. Query center cell first (closest potential candidates)
            if (grid.TryGetValue(centerCell, out var centerList))
            {
                int listCount = centerList.Count;
                for (int li = 0; li < listCount; li++)
                {
                    int dIdx = centerList[li];
                    var dPos = donors[dIdx].PosedPosition;
                    if (queryPoint.X > 0.02f && dPos.X < -0.02f) continue;
                    if (queryPoint.X < -0.02f && dPos.X > 0.02f) continue;

                    float dSq = queryPoint.DistanceSquaredTo(dPos);
                    if (dSq <= rSq && dSq < bestDistSq[7])
                    {
                        int insertPos = 7;
                        while (insertPos > 0 && dSq < bestDistSq[insertPos - 1])
                        {
                            bestDistSq[insertPos] = bestDistSq[insertPos - 1];
                            bestDonors[insertPos] = bestDonors[insertPos - 1];
                            insertPos--;
                        }
                        bestDistSq[insertPos] = dSq;
                        bestDonors[insertPos] = dIdx;
                        candidateCount++;
                        if (candidateCount >= kNearest && bestDistSq[kNearest - 1] < rSq)
                        {
                            rSq = bestDistSq[kNearest - 1];
                        }
                    }
                }
            }

            // 2. Spatial boundary pruning: if closest candidates are nearer than cell boundaries, skip all 26 neighbor cells!
            float minXDist = queryPoint.X - centerCell.X * cellSize;
            float maxXDist = (centerCell.X + 1) * cellSize - queryPoint.X;
            float minYDist = queryPoint.Y - centerCell.Y * cellSize;
            float maxYDist = (centerCell.Y + 1) * cellSize - queryPoint.Y;
            float minZDist = queryPoint.Z - centerCell.Z * cellSize;
            float maxZDist = (centerCell.Z + 1) * cellSize - queryPoint.Z;
            float dMinBorder = MathF.Min(MathF.Min(MathF.Min(minXDist, maxXDist), MathF.Min(minYDist, maxYDist)), MathF.Min(minZDist, maxZDist));

            bool canPruneNeighbors = candidateCount >= kNearest && bestDistSq[kNearest - 1] <= dMinBorder * dMinBorder;

            if (!canPruneNeighbors)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (dx == 0 && dy == 0 && dz == 0) continue; // Already queried center

                            Vector3I c = new Vector3I(centerCell.X + dx, centerCell.Y + dy, centerCell.Z + dz);
                            if (grid.TryGetValue(c, out var list))
                            {
                                int listCount = list.Count;
                                for (int li = 0; li < listCount; li++)
                                {
                                    int dIdx = list[li];
                                    var dPos = donors[dIdx].PosedPosition;
                                    if (queryPoint.X > 0.02f && dPos.X < -0.02f) continue;
                                    if (queryPoint.X < -0.02f && dPos.X > 0.02f) continue;

                                    float dSq = queryPoint.DistanceSquaredTo(dPos);
                                    if (dSq <= rSq && dSq < bestDistSq[7])
                                    {
                                        int insertPos = 7;
                                        while (insertPos > 0 && dSq < bestDistSq[insertPos - 1])
                                        {
                                            bestDistSq[insertPos] = bestDistSq[insertPos - 1];
                                            bestDonors[insertPos] = bestDonors[insertPos - 1];
                                            insertPos--;
                                        }
                                        bestDistSq[insertPos] = dSq;
                                        bestDonors[insertPos] = dIdx;
                                        candidateCount++;
                                        if (candidateCount >= kNearest && bestDistSq[kNearest - 1] < rSq)
                                        {
                                            rSq = bestDistSq[kNearest - 1];
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            Span<int> accumBones = stackalloc int[16];
            Span<float> accumWeights = stackalloc float[16];
            int accumCount = 0;

            if (candidateCount > 0)
            {
                int kMax = Math.Min(Math.Min(candidateCount, kNearest), 8);
                for (int i = 0; i < kMax; i++)
                {
                    if (bestDistSq[i] == float.MaxValue) break;
                    float dist = Mathf.Sqrt(bestDistSq[i]);
                    float wNeighbor = 1.0f / ((dist + 0.002f) * (dist + 0.002f));

                    var donor = donors[bestDonors[i]];
                    for (int k = 0; k < 4; k++)
                    {
                        float bw = donor.BoneWeights[k];
                        if (bw <= 0.0001f) continue;
                        int b = donor.BoneIndices[k];

                        int found = -1;
                        for (int a = 0; a < accumCount; a++)
                        {
                            if (accumBones[a] == b) { found = a; break; }
                        }
                        if (found != -1)
                        {
                            accumWeights[found] += wNeighbor * bw;
                        }
                        else if (accumCount < 16)
                        {
                            accumBones[accumCount] = b;
                            accumWeights[accumCount] = wNeighbor * bw;
                            accumCount++;
                        }
                    }
                }
            }

            // Capsule fallback if few candidates or far from surface
            float closestDist = candidateCount > 0 ? Mathf.Sqrt(bestDistSq[0]) : float.MaxValue;
            if (candidateCount < 2 || closestDist > 0.12f)
            {
                float blendAlpha = Mathf.Clamp((closestDist - 0.08f) / 0.15f, 0f, 1f);
                int capCount = capsules.Count;
                for (int ci = 0; ci < capCount; ci++)
                {
                    var seg = capsules[ci];
                    if (queryPoint.X > 0.02f && (seg.BoneName.EndsWith("_r") || seg.BoneName.Contains("_r_") || seg.BoneName.Contains("right"))) continue;
                    if (queryPoint.X < -0.02f && (seg.BoneName.EndsWith("_l") || seg.BoneName.Contains("_l_") || seg.BoneName.Contains("left"))) continue;

                    float dCapsule = DistancePointToSegment(queryPoint, seg.StartPos, seg.EndPos);
                    float denom = dCapsule + seg.Radius + 0.02f;
                    float wCapsule = 1.0f / (denom * denom * denom * Mathf.Sqrt(denom));
                    float addW = wCapsule * blendAlpha * 10.0f;

                    int found = -1;
                    for (int a = 0; a < accumCount; a++)
                    {
                        if (accumBones[a] == seg.BoneIndex) { found = a; break; }
                    }
                    if (found != -1)
                    {
                        accumWeights[found] += addW;
                    }
                    else if (accumCount < 16)
                    {
                        accumBones[accumCount] = seg.BoneIndex;
                        accumWeights[accumCount] = addW;
                        accumCount++;
                    }
                }
            }

            for (int top = 0; top < 4; top++)
            {
                int bestIdx = -1;
                float maxVal = 0f;
                for (int a = 0; a < accumCount; a++)
                {
                    if (accumWeights[a] > maxVal)
                    {
                        maxVal = accumWeights[a];
                        bestIdx = a;
                    }
                }
                if (bestIdx != -1 && maxVal > 0.0001f)
                {
                    outIndices[outOffset + top] = accumBones[bestIdx];
                    outWeights[outOffset + top] = maxVal;
                    accumWeights[bestIdx] = -1f;
                }
                else
                {
                    outIndices[outOffset + top] = 0;
                    outWeights[outOffset + top] = 0f;
                }
            }

            float sumW = outWeights[outOffset + 0] + outWeights[outOffset + 1] + outWeights[outOffset + 2] + outWeights[outOffset + 3];
            if (sumW > 0.0001f)
            {
                outWeights[outOffset + 0] /= sumW;
                outWeights[outOffset + 1] /= sumW;
                outWeights[outOffset + 2] /= sumW;
                outWeights[outOffset + 3] /= sumW;
            }
            else
            {
                outIndices[outOffset + 0] = 0;
                outWeights[outOffset + 0] = 1.0f;
            }
        }

        private static List<BoneCapsuleSegment> BuildCapsuleSegments(Skeleton3D skeleton, Transform3D[] posedGlobal)
        {
            var segments = new List<BoneCapsuleSegment>();
            int boneCount = skeleton.GetBoneCount();

            for (int b = 0; b < boneCount; b++)
            {
                string bName = skeleton.GetBoneName(b).ToLowerInvariant();

                // Filter to core anatomical bones only (reduces from 270 bones to ~16, eliminating millions of distance checks)
                bool isCoreAnatomical = bName.Contains("pelvis") || bName.Contains("spine") || bName.Contains("chest") ||
                                       bName.Contains("neck") || bName.Contains("head") ||
                                       bName.Contains("arm_upper") || bName.Contains("arm_lower") || bName.Contains("hand") ||
                                       bName.Contains("leg_upper") || bName.Contains("leg_lower") || bName.Contains("ankle") ||
                                       bName.Contains("thigh") || bName.Contains("calf") || bName.Contains("foot");

                if (!isCoreAnatomical) continue;

                int parent = skeleton.GetBoneParent(b);

                Vector3 startPos = posedGlobal[b].Origin;
                Vector3 endPos;

                // Find first child bone or project forward
                int firstChild = -1;
                for (int c = 0; c < boneCount; c++)
                {
                    if (skeleton.GetBoneParent(c) == b)
                    {
                        firstChild = c;
                        break;
                    }
                }

                if (firstChild != -1)
                {
                    endPos = posedGlobal[firstChild].Origin;
                }
                else if (parent != -1)
                {
                    Vector3 fromParent = startPos - posedGlobal[parent].Origin;
                    endPos = startPos + fromParent.Normalized() * 0.10f;
                }
                else
                {
                    endPos = startPos + Vector3.Up * 0.15f;
                }

                float radius = 0.04f;
                if (bName.Contains("pelvis") || bName.Contains("spine") || bName.Contains("chest")) radius = 0.12f;
                else if (bName.Contains("head")) radius = 0.10f;
                else if (bName.Contains("leg_upper") || bName.Contains("thigh")) radius = 0.08f;
                else if (bName.Contains("leg_lower") || bName.Contains("calf")) radius = 0.06f;
                else if (bName.Contains("arm_upper")) radius = 0.05f;
                else if (bName.Contains("arm_lower")) radius = 0.04f;

                segments.Add(new BoneCapsuleSegment
                {
                    BoneIndex = b,
                    BoneName = bName,
                    StartPos = startPos,
                    EndPos = endPos,
                    Radius = radius
                });
            }

            return segments;
        }

        private static float DistancePointToSegment(Vector3 point, Vector3 start, Vector3 end)
        {
            Vector3 ab = end - start;
            float lenSq = ab.LengthSquared();
            if (lenSq <= 0.00001f) return point.DistanceTo(start);

            float t = Mathf.Clamp((point - start).Dot(ab) / lenSq, 0.0f, 1.0f);
            Vector3 projection = start + t * ab;
            return point.DistanceTo(projection);
        }

        private static bool ApplyWristTwistBlending(
            Vector3 vHero,
            Span<int> topBones,
            Span<float> topWeights,
            ArmSegmentRegistration leftArm,
            ArmSegmentRegistration rightArm,
            int twistL,
            int twistR)
        {
            bool isWrist = false;
            if (leftArm != null)
            {
                if (CheckAndBlendWrist(vHero, topBones, topWeights, leftArm.Bt, leftArm.Ct, leftArm.TargetLowerBone, leftArm.TargetHandBone, twistL))
                {
                    isWrist = true;
                }
            }
            if (rightArm != null)
            {
                if (CheckAndBlendWrist(vHero, topBones, topWeights, rightArm.Bt, rightArm.Ct, rightArm.TargetLowerBone, rightArm.TargetHandBone, twistR))
                {
                    isWrist = true;
                }
            }
            return isWrist;
        }

        private static bool CheckAndBlendWrist(
            Vector3 vHero,
            Span<int> topBones,
            Span<float> topWeights,
            Vector3 Bt,
            Vector3 Ct,
            int lowerBone,
            int handBone,
            int twistBone)
        {
            Vector3 armDir = Ct - Bt;
            float armLen = armDir.Length();
            if (armLen < 0.01f) return false;

            Vector3 uArm = armDir / armLen;
            Vector3 d = vHero - Bt;
            float proj = d.Dot(uArm);
            float t = proj / armLen;

            // Boundary: within final 35% of forearm approaching wrist [0.65, 1.05]
            if (t < 0.65f || t > 1.05f) return false;

            // Radial distance check (must be on forearm/wrist cylinder, r <= 18cm)
            float r = (d - uArm * proj).Length();
            if (r > 0.18f) return false;

            // Check if vertex has influence from forearm or forearm twist
            int lowerIdx = -1;
            int handIdx = -1;
            float forearmWeight = 0f;

            for (int k = 0; k < 4; k++)
            {
                if (topWeights[k] <= 0.0001f) continue;
                if (topBones[k] == lowerBone || (twistBone != -1 && topBones[k] == twistBone))
                {
                    lowerIdx = k;
                    forearmWeight += topWeights[k];
                }
                else if (topBones[k] == handBone)
                {
                    handIdx = k;
                }
            }

            if (forearmWeight <= 0.05f) return false;

            // Smoothstep gradient: alpha = smoothstep(0.65, 1.0, t) * 0.45
            float normT = Mathf.Clamp((t - 0.65f) / 0.35f, 0.0f, 1.0f);
            float alpha = normT * normT * (3.0f - 2.0f * normT) * 0.45f;
            float transferW = forearmWeight * alpha;

            if (transferW <= 0.001f) return false;

            // Deduct from forearm bone(s)
            for (int k = 0; k < 4; k++)
            {
                if (topBones[k] == lowerBone || (twistBone != -1 && topBones[k] == twistBone))
                {
                    topWeights[k] *= (1.0f - alpha);
                }
            }

            // Add to destination bone (prefer twistBone if present, so pronation roll is only 50% without 180 deg collapse!)
            int destBone = (twistBone != -1) ? twistBone : handBone;
            int destIdx = -1;
            for (int k = 0; k < 4; k++)
            {
                if (topBones[k] == destBone) { destIdx = k; break; }
            }

            if (destIdx != -1)
            {
                topWeights[destIdx] += transferW;
            }
            else
            {
                // Replace lowest weight slot
                int minIdx = 0;
                float minVal = topWeights[0];
                for (int k = 1; k < 4; k++)
                {
                    if (topWeights[k] < minVal) { minVal = topWeights[k]; minIdx = k; }
                }
                topBones[minIdx] = destBone;
                topWeights[minIdx] = transferW;
            }

            // Re-normalize
            float sum = topWeights[0] + topWeights[1] + topWeights[2] + topWeights[3];
            if (sum > 0.0001f)
            {
                topWeights[0] /= sum;
                topWeights[1] /= sum;
                topWeights[2] /= sum;
                topWeights[3] /= sum;
            }

            return true;
        }

        private static void SmoothWeightsLaplacian(int[] indices, int vCount, int[] boneIndices, float[] boneWeights, int iterations, Vector3[] verts = null, bool[] vertexMask = null)
        {
            if (vCount <= 0 || iterations <= 0) return;

            // 1. Build adjacency with 1mm spatial hash for coincident vertex welding
            List<int>[] adj = new List<int>[vCount];
            for (int i = 0; i < vCount; i++) adj[i] = new List<int>(6);

            Dictionary<Vector3I, List<int>> spatialClusters = null;
            if (verts != null && verts.Length == vCount)
            {
                spatialClusters = new Dictionary<Vector3I, List<int>>(vCount);
                for (int v = 0; v < vCount; v++)
                {
                    Vector3 p = verts[v];
                    Vector3I q = new Vector3I(
                        Mathf.RoundToInt(p.X * 1000f),
                        Mathf.RoundToInt(p.Y * 1000f),
                        Mathf.RoundToInt(p.Z * 1000f)
                    );
                    if (!spatialClusters.TryGetValue(q, out var list))
                    {
                        list = new List<int>(2);
                        spatialClusters[q] = list;
                    }
                    list.Add(v);
                }

                // Connect coincident vertices in adjacency graph
                foreach (var list in spatialClusters.Values)
                {
                    if (list.Count <= 1) continue;
                    for (int a = 0; a < list.Count; a++)
                    {
                        for (int b = a + 1; b < list.Count; b++)
                        {
                            int vA = list[a];
                            int vB = list[b];
                            adj[vA].Add(vB);
                            adj[vB].Add(vA);
                        }
                    }
                }
            }

            if (indices != null && indices.Length >= 3)
            {
                for (int i = 0; i < indices.Length; i += 3)
                {
                    int i0 = indices[i];
                    int i1 = indices[i + 1];
                    int i2 = indices[i + 2];

                    if (i0 < vCount && i1 < vCount && i2 < vCount)
                    {
                        adj[i0].Add(i1); adj[i0].Add(i2);
                        adj[i1].Add(i0); adj[i1].Add(i2);
                        adj[i2].Add(i0); adj[i2].Add(i1);
                    }
                }
            }

            float lambda = 0.35f;
            int[] newIndices = new int[vCount * 4];
            float[] newWeights = new float[vCount * 4];

            for (int it = 0; it < iterations; it++)
            {
                System.Threading.Tasks.Parallel.For(0, vCount, v =>
                {
                    if (vertexMask != null && !vertexMask[v])
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            newIndices[v * 4 + k] = boneIndices[v * 4 + k];
                            newWeights[v * 4 + k] = boneWeights[v * 4 + k];
                        }
                        return;
                    }
                    SmoothSingleVertex(v, adj, boneIndices, boneWeights, newIndices, newWeights, lambda);
                });

                Array.Copy(newIndices, boneIndices, vCount * 4);
                Array.Copy(newWeights, boneWeights, vCount * 4);
            }

            // Post-pass: Average weights across coincident 3D vertex positions (within 1mm)
            // Ensures inverted-hull black outline shells and split UV seams receive 100% identical weights to skin!
            if (spatialClusters != null)
            {
                var accumDict = new Dictionary<int, float>(8);
                Span<int> canonBones = stackalloc int[4];
                Span<float> canonWeights = stackalloc float[4];
                foreach (var list in spatialClusters.Values)
                {
                    if (list.Count <= 1) continue;

                    accumDict.Clear();
                    float totalWeight = 0f;
                    foreach (int v in list)
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            float w = boneWeights[v * 4 + k];
                            if (w <= 0.0001f) continue;
                            int b = boneIndices[v * 4 + k];
                            accumDict[b] = accumDict.GetValueOrDefault(b, 0f) + w;
                            totalWeight += w;
                        }
                    }

                    if (totalWeight <= 0.0001f) continue;

                    var topBones = accumDict.OrderByDescending(kv => kv.Value).Take(4).ToList();
                    float normSum = topBones.Sum(kv => kv.Value);

                    for (int k = 0; k < 4; k++)
                    {
                        if (k < topBones.Count && normSum > 0.0001f)
                        {
                            canonBones[k] = topBones[k].Key;
                            canonWeights[k] = topBones[k].Value / normSum;
                        }
                        else
                        {
                            canonBones[k] = 0;
                            canonWeights[k] = 0f;
                        }
                    }

                    foreach (int v in list)
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            boneIndices[v * 4 + k] = canonBones[k];
                            boneWeights[v * 4 + k] = canonWeights[k];
                        }
                    }
                }
            }
        }

        private static void SmoothSingleVertex(
            int v,
            List<int>[] adj,
            int[] boneIndices,
            float[] boneWeights,
            int[] newIndices,
            float[] newWeights,
            float lambda)
        {
            var neighbors = adj[v];
            if (neighbors.Count == 0)
            {
                for (int k = 0; k < 4; k++)
                {
                    newIndices[v * 4 + k] = boneIndices[v * 4 + k];
                    newWeights[v * 4 + k] = boneWeights[v * 4 + k];
                }
                return;
            }

            Span<int> blendBones = stackalloc int[16];
            Span<float> blendWeights = stackalloc float[16];
            int blendCount = 0;

            float selfWeight = 1.0f - lambda;
            for (int k = 0; k < 4; k++)
            {
                int b = boneIndices[v * 4 + k];
                float w = boneWeights[v * 4 + k];
                if (w > 0.0001f)
                {
                    blendBones[blendCount] = b;
                    blendWeights[blendCount] = w * selfWeight;
                    blendCount++;
                }
            }

            float neighborScale = lambda / neighbors.Count;
            int nCount = neighbors.Count;
            for (int ni = 0; ni < nCount; ni++)
            {
                int n = neighbors[ni];
                for (int k = 0; k < 4; k++)
                {
                    int b = boneIndices[n * 4 + k];
                    float w = boneWeights[n * 4 + k];
                    if (w > 0.0001f)
                    {
                        float add = w * neighborScale;
                        int found = -1;
                        for (int bi = 0; bi < blendCount; bi++)
                        {
                            if (blendBones[bi] == b) { found = bi; break; }
                        }
                        if (found != -1)
                        {
                            blendWeights[found] += add;
                        }
                        else if (blendCount < 16)
                        {
                            blendBones[blendCount] = b;
                            blendWeights[blendCount] = add;
                            blendCount++;
                        }
                    }
                }
            }

            for (int top = 0; top < 4; top++)
            {
                int bestIdx = -1;
                float maxVal = 0f;
                for (int bi = 0; bi < blendCount; bi++)
                {
                    if (blendWeights[bi] > maxVal)
                    {
                        maxVal = blendWeights[bi];
                        bestIdx = bi;
                    }
                }
                if (bestIdx != -1 && maxVal > 0.0001f)
                {
                    newIndices[v * 4 + top] = blendBones[bestIdx];
                    newWeights[v * 4 + top] = maxVal;
                    blendWeights[bestIdx] = -1f;
                }
                else
                {
                    newIndices[v * 4 + top] = 0;
                    newWeights[v * 4 + top] = 0f;
                }
            }

            float sumW = newWeights[v * 4 + 0] + newWeights[v * 4 + 1] + newWeights[v * 4 + 2] + newWeights[v * 4 + 3];
            if (sumW > 0.0001f)
            {
                newWeights[v * 4 + 0] /= sumW;
                newWeights[v * 4 + 1] /= sumW;
                newWeights[v * 4 + 2] /= sumW;
                newWeights[v * 4 + 3] /= sumW;
            }
            else
            {
                newIndices[v * 4 + 0] = 0;
                newWeights[v * 4 + 0] = 1.0f;
            }
        }

        private static void BakeInversePose(
            Vector3[] posedVerts,
            Vector3[] posedNorms,
            int[] boneIndices,
            float[] boneWeights,
            Transform3D[] restGlobal,
            Transform3D[] posedGlobal,
            Vector3[] outRestVerts,
            Vector3[] outRestNorms)
        {
            int vCount = posedVerts.Length;
            int boneCount = Math.Min(restGlobal.Length, posedGlobal.Length);
            Transform3D[] invSkinMatrices = new Transform3D[boneCount];
            for (int b = 0; b < boneCount; b++)
            {
                invSkinMatrices[b] = restGlobal[b] * posedGlobal[b].AffineInverse();
            }

            System.Threading.Tasks.Parallel.For(0, vCount, v =>
            {
                Vector3 pPosed = posedVerts[v];
                Vector3 nPosed = posedNorms[v];

                Vector3 pRest = Vector3.Zero;
                Vector3 nRest = Vector3.Zero;

                for (int k = 0; k < 4; k++)
                {
                    float w = boneWeights[v * 4 + k];
                    if (w <= 0.0001f) continue;

                    int b = boneIndices[v * 4 + k];
                    if (b >= 0 && b < boneCount)
                    {
                        Transform3D invSkinMatrix = invSkinMatrices[b];
                        pRest += w * (invSkinMatrix * pPosed);
                        nRest += w * (invSkinMatrix.Basis * nPosed);
                    }
                    else
                    {
                        pRest += w * pPosed;
                        nRest += w * nPosed;
                    }
                }

                outRestVerts[v] = pRest;
                outRestNorms[v] = nRest.Normalized();
            });
        }

        private static (Dictionary<int, int> skelToBind, Dictionary<int, int> bindToSkel) BuildSkinIndexMappings(Skeleton3D skeleton, Skin skin)
        {
            var skelToBind = new Dictionary<int, int>();
            var bindToSkel = new Dictionary<int, int>();

            int boneCount = skeleton.GetBoneCount();

            if (skin != null && skin.GetBindCount() > 0)
            {
                for (int i = 0; i < skin.GetBindCount(); i++)
                {
                    int bone = skin.GetBindBone(i);
                    if (bone == -1 && !string.IsNullOrEmpty(skin.GetBindName(i)))
                    {
                        bone = skeleton.FindBone(skin.GetBindName(i));
                    }

                    if (bone >= 0)
                    {
                        skelToBind[bone] = i;
                        bindToSkel[i] = bone;
                    }
                }
            }

            // Fill unmapped fallback identity
            for (int b = 0; b < boneCount; b++)
            {
                if (!skelToBind.ContainsKey(b))
                {
                    skelToBind[b] = b;
                    bindToSkel[b] = b;
                }
            }

            return (skelToBind, bindToSkel);
        }

        public static Dictionary<int, int> BuildHumanoidBoneMap(
            Skeleton3D sourceSkeleton,
            Skeleton3D targetSkeleton,
            Dictionary<string, string> userCustomRemap = null)
        {
            var map = new Dictionary<int, int>();

            // 1. Apply user custom overrides if supplied
            if (userCustomRemap != null && userCustomRemap.Count > 0)
            {
                foreach (var (tName, sName) in userCustomRemap)
                {
                    int tIdx = targetSkeleton.FindBone(tName);
                    int sIdx = sourceSkeleton.FindBone(sName);
                    if (tIdx != -1 && sIdx != -1)
                    {
                        map[sIdx] = tIdx;
                    }
                }
            }

            for (int sIdx = 0; sIdx < sourceSkeleton.GetBoneCount(); sIdx++)
            {
                if (map.ContainsKey(sIdx)) continue;
                string sName = sourceSkeleton.GetBoneName(sIdx).ToLowerInvariant();

                // Check exact name match first
                int exact = targetSkeleton.FindBone(sourceSkeleton.GetBoneName(sIdx));
                if (exact != -1)
                {
                    map[sIdx] = exact;
                    continue;
                }

                // Match against HumanoidBoneMapper definitions
                foreach (var def in HumanoidBoneMapper.Definitions)
                {
                    bool isCentral = def.Group == HumanoidBoneMapper.BoneGroup.Pelvis || def.Group == HumanoidBoneMapper.BoneGroup.Spine || def.Group == HumanoidBoneMapper.BoneGroup.Head || def.Group == HumanoidBoneMapper.BoneGroup.Root;
                    bool hasLateralTag = sName.EndsWith("_l") || sName.EndsWith("_r") || sName.Contains("_l_") || sName.Contains("_r_") || sName.Contains(".l") || sName.Contains(".r") || sName.Contains("left") || sName.Contains("right");
                    if (isCentral && hasLateralTag) continue;
                    if (def.CanonicalName == "head" && sName.Contains("forehead")) continue;

                    bool sourceMatch = def.SourceAliases.Any(alias =>
                    {
                        string cAlias = alias.ToLowerInvariant();
                        return sName == cAlias || sName.StartsWith(cAlias + "_") || sName.EndsWith("_" + cAlias) || sName.Contains("_" + cAlias + "_");
                    }) || sName.Equals(def.CanonicalName, StringComparison.OrdinalIgnoreCase);

                    if (sourceMatch)
                    {
                        foreach (var tAlias in def.TargetAliases)
                        {
                            int tIdx = targetSkeleton.FindBone(tAlias);
                            if (tIdx != -1)
                            {
                                map[sIdx] = tIdx;
                                break;
                            }
                        }

                        if (map.ContainsKey(sIdx)) break;
                    }
                }
            }

            // Check for Deadlock Forearm Twist Bones (arm_lower_L_TWIST, arm_lower_R_TWIST, etc.)
            for (int sIdx = 0; sIdx < sourceSkeleton.GetBoneCount(); sIdx++)
            {
                if (map.ContainsKey(sIdx)) continue;
                string sName = sourceSkeleton.GetBoneName(sIdx);
                string clean = HumanoidBoneMapper.CleanBoneName(sName);

                if (clean.Contains("forearm") && (clean.EndsWith("_001") || clean.EndsWith(".001") || clean.Contains("twist")))
                {
                    bool isLeft = clean.EndsWith("_l") || clean.Contains("_l_") || clean.Contains("left") || sName.ToLowerInvariant().Contains(".l");
                    int tgtTwist = FindBoneAny(targetSkeleton,
                        isLeft ? "arm_lower_L_TWIST" : "arm_lower_R_TWIST",
                        isLeft ? "arm_lower_twist_L" : "arm_lower_twist_R",
                        isLeft ? "arm_lower_l_twist" : "arm_lower_r_twist");
                    if (tgtTwist == -1)
                    {
                        tgtTwist = EnsureForearmTwistBone(targetSkeleton, isLeft);
                    }
                    if (tgtTwist != -1)
                    {
                        map[sIdx] = tgtTwist;
                    }
                }
            }

            // Collapse unmapped .001 twist bones into their mapped parent limb bone
            for (int sIdx = 0; sIdx < sourceSkeleton.GetBoneCount(); sIdx++)
            {
                if (map.ContainsKey(sIdx)) continue;
                string sName = sourceSkeleton.GetBoneName(sIdx);
                string clean = HumanoidBoneMapper.CleanBoneName(sName);

                if (clean.EndsWith("_001") || clean.EndsWith(".001") || clean.Contains("twist"))
                {
                    string baseName = clean.Replace("_001", "").Replace(".001", "").Replace("twist", "");
                    for (int otherIdx = 0; otherIdx < sourceSkeleton.GetBoneCount(); otherIdx++)
                    {
                        if (map.TryGetValue(otherIdx, out int tgtMapped))
                        {
                            string otherClean = HumanoidBoneMapper.CleanBoneName(sourceSkeleton.GetBoneName(otherIdx));
                            if (otherClean == baseName || otherClean.StartsWith(baseName + "_") || baseName.StartsWith(otherClean))
                            {
                                map[sIdx] = tgtMapped;
                                break;
                            }
                        }
                    }
                }
            }

            return map;
        }

        private static int ResolveTargetBoneWithAncestorFallback(
            int sourceBone,
            Skeleton3D sourceSkeleton,
            Dictionary<int, int> sourceToTargetMap,
            Skeleton3D targetSkeleton,
            bool keepFingersRigid = false)
        {
            string srcNameRaw = sourceSkeleton.GetBoneName(sourceBone).ToLowerInvariant();

            // Palm bones (e.g. Rigify DEF-palm.01.L..04.L) always collapse into hand_L / hand_R
            if (srcNameRaw.Contains("palm"))
            {
                bool isLeft = srcNameRaw.EndsWith(".l") || srcNameRaw.EndsWith("_l") || srcNameRaw.Contains("left");
                int handIdx = FindBoneAny(targetSkeleton, isLeft ? "hand_L" : "hand_R");
                if (handIdx != -1) return handIdx;
            }

            int resolved = -1;
            if (sourceToTargetMap.TryGetValue(sourceBone, out int tgt))
            {
                resolved = tgt;
            }
            else
            {
                // Climb up the source hierarchy to find mapped ancestor
                int cur = sourceBone;
                while (cur != -1)
                {
                    cur = sourceSkeleton.GetBoneParent(cur);
                    if (cur != -1 && sourceToTargetMap.TryGetValue(cur, out int mappedAncestor))
                    {
                        resolved = mappedAncestor;
                        break;
                    }
                }
            }

            if (resolved == -1)
            {
                int pelvis = targetSkeleton.FindBone("pelvis");
                resolved = pelvis != -1 ? pelvis : 0;
            }

            if (keepFingersRigid)
            {
                string tgtName = targetSkeleton.GetBoneName(resolved).ToLowerInvariant();
                string srcName = sourceSkeleton.GetBoneName(sourceBone).ToLowerInvariant();
                if (tgtName.StartsWith("finger_") || tgtName.Contains("thumb") || tgtName.Contains("index") ||
                    tgtName.Contains("middle") || tgtName.Contains("ring") || tgtName.Contains("pinky") ||
                    srcName.Contains("finger") || srcName.Contains("thumb") || srcName.Contains("index") ||
                    srcName.Contains("middle") || srcName.Contains("ring") || srcName.Contains("pinky") ||
                    srcName.Contains("f_"))
                {
                    bool isLeft = tgtName.EndsWith("_l") || tgtName.Contains("_l_") || srcName.EndsWith(".l") || srcName.EndsWith("_l") || srcName.Contains("left");
                    int handIdx = targetSkeleton.FindBone(isLeft ? "hand_L" : "hand_R");
                    if (handIdx != -1) return handIdx;
                }
            }

            return resolved;
        }

        private static void ApplyReplacementMode(Node3D heroNode, List<MeshInstance3D> customMeshes, RiggingOptions options)
        {
            // In preview mode inside modal, NEVER hide ghost reference mannequin meshes!
            if (options.IsPreviewOnly) return;

            var existingMeshes = CollectOriginalHeroMeshes(heroNode);

            foreach (var mi in existingMeshes)
            {
                if (mi.HasMeta("IsCustomImportedMesh")) continue;

                string rawName = mi.Name.ToString();
                bool keep = options.KeptOriginalSubmeshNames != null && options.KeptOriginalSubmeshNames.Contains(rawName);

                // If explicitly replaced by a custom submesh (e.g. custom gun replacing hero gun), force hidden
                if (options.SubmeshReplacementMap != null)
                {
                    foreach (var repTarget in options.SubmeshReplacementMap.Values)
                    {
                        if (string.IsNullOrEmpty(repTarget) || repTarget == "[ None ]") continue;
                        if (rawName.Equals(repTarget, StringComparison.OrdinalIgnoreCase))
                        {
                            keep = false;
                            break;
                        }
                        // If replacing primary gun model (e.g. _CHRONO_GUN_MODEL), also hide corresponding hip/holstered version (_CHRONO_GUN_ON_HIP)
                        string repLower = repTarget.ToLowerInvariant();
                        string rawLower = rawName.ToLowerInvariant();
                        if (repLower.Contains("gun") && rawLower.Contains("gun"))
                        {
                            if (rawLower.Contains("hip") || rawLower.Contains("holster") || rawLower.Contains("back") || rawLower.Contains("sheath"))
                            {
                                keep = false;
                                break;
                            }
                        }
                    }
                }

                // Both FullReplacement and PartialAccessory modes respect explicitly kept meshes (e.g. Paradox's gun)
                mi.Visible = keep;
                mi.SetMeta("UserVisibility", keep);
                if (keep)
                {
                    mi.RemoveMeta("UserHiddenByCustomModel");
                }
                else
                {
                    mi.SetMeta("UserHiddenByCustomModel", true);
                }
            }

            foreach (var cm in customMeshes)
            {
                cm.Visible = true;
            }
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
            foreach (var c in candidates)
            {
                int idx = skeleton.FindBone(c);
                if (idx != -1) return idx;
            }
            return -1;
        }

        private static Dictionary<int, Quaternion> BackupSkeletonLocalRotations(Skeleton3D skeleton)
        {
            var dict = new Dictionary<int, Quaternion>();
            for (int b = 0; b < skeleton.GetBoneCount(); b++)
            {
                dict[b] = skeleton.GetBonePoseRotation(b);
            }
            return dict;
        }

        private static void RestoreSkeletonLocalRotations(Skeleton3D skeleton, Dictionary<int, Quaternion> backup)
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
        private static Vector2[] GetVector2Array(Variant v) => v.VariantType != Variant.Type.Nil ? v.As<Vector2[]>() : null;
        private static int[] GetInt32Array(Variant v) => v.VariantType != Variant.Type.Nil ? v.As<int[]>() : null;
        private static float[] GetFloat32Array(Variant v) => v.VariantType != Variant.Type.Nil ? v.As<float[]>() : null;

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
                nName.StartsWith("@") || nName.Contains("Custom_Rigged_Character"))
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
                nName.StartsWith("@") || nName.Contains("Custom_Rigged_Character"))
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
        /// Computes pristine model vertex bounding box relative to its own root node (at scale 1.0, offset 0).
        /// Immune to current viewport slider or container transforms!
        /// </summary>
        public static (Vector3 Min, Vector3 Max, bool Valid) ComputePristineModelBounds(Node3D root)
        {
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            bool found = false;

            var meshes = CollectMeshes(root);
            foreach (var mi in meshes)
            {
                if (mi.Mesh == null) continue;
                Transform3D relT = ComputeTransformRelativeToRoot(mi, root);

                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                {
                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.Vertex) continue;
                    var vVar = arrays[(int)Mesh.ArrayType.Vertex];
                    if (vVar.VariantType == Variant.Type.Nil) continue;
                    var verts = vVar.As<Vector3[]>();
                    if (verts == null || verts.Length == 0) continue;

                    found = true;
                    for (int i = 0; i < verts.Length; i++)
                    {
                        Vector3 w = relT * verts[i];
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
        /// Strictly filters out gizmo meshes, handles, and accessories.
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

            foreach (var mi in meshes)
            {
                if (mi.Mesh == null) continue;
                string lowerName = mi.Name.ToString().ToLowerInvariant();
                bool isWeaponOrProp = lowerName.Contains("gun") || lowerName.Contains("weapon");

                Transform3D relT = ComputeTransformRelativeToRoot(mi, root);

                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                {
                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.Vertex) continue;
                    var vVar = arrays[(int)Mesh.ArrayType.Vertex];
                    if (vVar.VariantType == Variant.Type.Nil) continue;
                    var verts = vVar.As<Vector3[]>();
                    if (verts == null || verts.Length == 0) continue;

                    found = true;
                    for (int i = 0; i < verts.Length; i++)
                    {
                        Vector3 w = relT * verts[i];
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

        #endregion

        #region Runtime Twist & Limb Stretch Driver

        public static int EnsureForearmTwistBone(Skeleton3D skeleton, bool isLeft)
        {
            if (skeleton == null || !GodotObject.IsInstanceValid(skeleton)) return -1;
            string twistName = isLeft ? "arm_lower_L_TWIST" : "arm_lower_R_TWIST";
            int idx = skeleton.FindBone(twistName);
            if (idx == -1)
            {
                idx = skeleton.FindBone(isLeft ? "arm_lower_twist_L" : "arm_lower_twist_R");
            }

            string lowerName = isLeft ? "arm_lower_L" : "arm_lower_R";
            int lowerIdx = skeleton.FindBone(lowerName);
            if (lowerIdx == -1) return idx;

            string handName = isLeft ? "hand_L" : "hand_R";
            int handIdx = skeleton.FindBone(handName);

            Vector3 localHandPos = handIdx != -1
                ? skeleton.GetBoneRest(handIdx).Origin
                : (isLeft ? Vector3.Right * 0.25f : Vector3.Left * 0.25f);
            Transform3D twistRest = new Transform3D(Basis.Identity, localHandPos * 0.65f);

            if (idx == -1)
            {
                idx = skeleton.GetBoneCount();
                skeleton.AddBone(twistName);
                skeleton.SetBoneParent(idx, lowerIdx);
                skeleton.SetBoneRest(idx, twistRest);
            }
            else
            {
                if (skeleton.GetBoneParent(idx) != lowerIdx)
                {
                    skeleton.SetBoneParent(idx, lowerIdx);
                }
                skeleton.SetBoneRest(idx, twistRest);
            }

            return idx;
        }

        public static void EnsureSkinHasBind(Skin skin, Skeleton3D skeleton, int boneIdx)
        {
            if (skin == null || skeleton == null || boneIdx < 0 || boneIdx >= skeleton.GetBoneCount()) return;
            int bindCount = skin.GetBindCount();
            for (int b = 0; b < bindCount; b++)
            {
                if (skin.GetBindBone(b) == boneIdx) return;
            }
            skin.AddBind(boneIdx, skeleton.GetBoneGlobalRest(boneIdx).AffineInverse());
        }

        public static void DriveForearmTwist(Skeleton3D skeleton)
        {
            if (skeleton == null || !GodotObject.IsInstanceValid(skeleton)) return;
            DriveForearmTwistForArm(skeleton, isLeft: true);
            DriveForearmTwistForArm(skeleton, isLeft: false);
        }

        private static void DriveForearmTwistForArm(Skeleton3D skeleton, bool isLeft)
        {
            int lowerIdx = skeleton.FindBone(isLeft ? "arm_lower_L" : "arm_lower_R");
            int handIdx = skeleton.FindBone(isLeft ? "hand_L" : "hand_R");
            int twistIdx = skeleton.FindBone(isLeft ? "arm_lower_L_TWIST" : "arm_lower_R_TWIST");
            if (twistIdx == -1) twistIdx = skeleton.FindBone(isLeft ? "arm_lower_twist_L" : "arm_lower_twist_R");
            if (lowerIdx == -1 || handIdx == -1 || twistIdx == -1) return;

            Transform3D tHandRest = skeleton.GetBoneRest(handIdx);
            Vector3 handRestOffset = tHandRest.Origin;
            if (handRestOffset.LengthSquared() < 1e-6f) handRestOffset = isLeft ? Vector3.Right : Vector3.Left;
            Vector3 boneAxisInLower = handRestOffset.Normalized();

            // Transform forearm axis into hand local coordinate space
            Vector3 boneAxisInHand = (tHandRest.Basis.Inverse() * boneAxisInLower).Normalized();

            // Pose rotation of hand is already relative to rest
            Quaternion qHandPose = skeleton.GetBonePoseRotation(handIdx);

            // Swing-twist decomposition around forearm axis in hand space
            Vector3 qVec = new Vector3(qHandPose.X, qHandPose.Y, qHandPose.Z);
            float proj = qVec.Dot(boneAxisInHand);

            Quaternion qTwistInHand = new Quaternion(boneAxisInHand.X * proj, boneAxisInHand.Y * proj, boneAxisInHand.Z * proj, qHandPose.W);
            if (qTwistInHand.LengthSquared() > 1e-8f)
            {
                qTwistInHand = qTwistInHand.Normalized();
                if (qTwistInHand.W < 0)
                {
                    qTwistInHand = new Quaternion(-qTwistInHand.X, -qTwistInHand.Y, -qTwistInHand.Z, -qTwistInHand.W);
                }

                float twistAngle = 2.0f * Mathf.Atan2(new Vector3(qTwistInHand.X, qTwistInHand.Y, qTwistInHand.Z).Length(), qTwistInHand.W);
                if (proj < 0) twistAngle = -twistAngle;

                // Drive twist bone at 50% roll along the forearm axis in lower arm space
                Quaternion qHalfTwist = new Quaternion(boneAxisInLower, twistAngle * 0.5f).Normalized();
                skeleton.SetBonePoseRotation(twistIdx, qHalfTwist);
            }
            else
            {
                skeleton.SetBonePoseRotation(twistIdx, Quaternion.Identity);
            }
        }

        public static void ClampLimbBoneTranslations(Skeleton3D skeleton, float maxStretchMeters = 0.015f)
        {
            if (skeleton == null || !GodotObject.IsInstanceValid(skeleton)) return;
            int count = skeleton.GetBoneCount();
            float maxSq = maxStretchMeters * maxStretchMeters;

            for (int b = 0; b < count; b++)
            {
                string name = skeleton.GetBoneName(b).ToLowerInvariant();
                if (name.Contains("pelvis") || name.Contains("root") || name.Contains("weapon") || 
                    name.Contains("prop") || name.Contains("camera"))
                {
                    continue;
                }

                bool isAnatomical = name.Contains("clavicle") || name.Contains("arm_upper") || 
                                   name.Contains("arm_lower") || name.Contains("hand") || 
                                   name.Contains("finger") || name.Contains("leg_upper") || 
                                   name.Contains("leg_lower") || name.Contains("ankle") || 
                                   name.Contains("ball") || name.Contains("thigh") || 
                                   name.Contains("shin") || name.Contains("calf") || 
                                   name.Contains("foot") || name.Contains("neck") || 
                                   name.Contains("head");

                if (!isAnatomical) continue;

                Vector3 restOrigin = skeleton.GetBoneRest(b).Origin;
                Vector3 curOrigin = skeleton.GetBonePosePosition(b);
                Vector3 delta = curOrigin - restOrigin;
                if (delta.LengthSquared() > maxSq)
                {
                    skeleton.SetBonePosePosition(b, restOrigin + delta.Normalized() * maxStretchMeters);
                }
            }
        }

        public static void UpdateCustomModelRuntimePosing(Skeleton3D skeleton, bool clampStretch = true)
        {
            if (skeleton == null || !GodotObject.IsInstanceValid(skeleton)) return;
            if (clampStretch)
            {
                ClampLimbBoneTranslations(skeleton, 0.015f);
            }
            DriveForearmTwist(skeleton);
        }

        public static void RemoveCustomModelAndRestoreHero(Node3D heroNode)
        {
            if (heroNode == null || !GodotObject.IsInstanceValid(heroNode)) return;

            // 1. Remove all Custom_Rigged_Character container nodes
            var containers = heroNode.FindChildren("Custom_Rigged_Character", recursive: true, owned: false);
            foreach (var node in containers)
            {
                node.GetParent()?.RemoveChild(node);
                node.QueueFree();
            }

            // Also remove any standalone custom meshes
            var allMeshes = CollectMeshes(heroNode);
            foreach (var mi in allMeshes)
            {
                if (mi.HasMeta("IsCustomImportedMesh") || mi.Name.ToString().StartsWith("Custom_"))
                {
                    mi.GetParent()?.RemoveChild(mi);
                    mi.QueueFree();
                }
            }

            // 2. Restore all authentic Deadlock hero submeshes to full visibility
            var heroMeshes = CollectOriginalHeroMeshes(heroNode);
            foreach (var mi in heroMeshes)
            {
                mi.Visible = true;
                if (mi.HasMeta("UserVisibility")) mi.SetMeta("UserVisibility", true);
                if (mi.HasMeta("UserHiddenByCustomModel")) mi.RemoveMeta("UserHiddenByCustomModel");
                if (mi.HasMeta("IsHiddenComposite")) mi.RemoveMeta("IsHiddenComposite");
            }

            // 3. Re-scan hero hierarchy
            new DeadlockPlayground.Painter.HeroMeshHierarchy().ScanHero(heroNode);
        }

        #endregion
    }
}
