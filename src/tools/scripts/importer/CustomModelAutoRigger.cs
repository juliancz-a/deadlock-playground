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
            public float CustomArmForwardDeg { get; set; } = 0f;
            public float CustomElbowStraightenPct { get; set; } = 0f;
            public float CustomLegSpreadDeg { get; set; } = 0f;
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
            public bool StabilizeNeckFloatingHead { get; set; } = true;
            public bool AutoCounterTwistWeights { get; set; } = true;
            public bool IncludeBakedOutlineShells { get; set; } = true;
            public Dictionary<string, bool> SubmeshIsOutlineShell { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
            public byte Region; // 0=Unassigned, 1=TorsoHead, 2=ArmL, 3=ArmR, 4=LegL, 5=LegR
        }

        public struct BoneCapsuleSegment
        {
            public int BoneIndex;
            public string BoneName;
            public Vector3 StartPos;
            public Vector3 EndPos;
            public float Radius;
        }

        public struct UnriggedPoseCalibration
        {
            public float ArmAngleDeg;
            public float ArmForwardDeg;
            public float ElbowStraightenPct;
            public float LegSpreadDeg;
            public int DetectedArmSamples;
            public int DetectedLegSamples;
        }

        private enum AnatomicalRegion : byte
        {
            Unassigned = 0,
            TorsoHead = 1,
            ArmL = 2,
            ArmR = 3,
            LegL = 4,
            LegR = 5
        }

        private static AnatomicalRegion ClassifyBoneRegion(string boneName)
        {
            if (string.IsNullOrEmpty(boneName)) return AnatomicalRegion.TorsoHead;
            string n = boneName.ToLowerInvariant();
            bool isL = n.EndsWith("_l") || n.Contains("_l_");
            bool isR = n.EndsWith("_r") || n.Contains("_r_");

            if (n.Contains("arm_") || n.Contains("hand") || n.Contains("finger") || n.Contains("clavicle"))
            {
                if (isL) return AnatomicalRegion.ArmL;
                if (isR) return AnatomicalRegion.ArmR;
            }
            if (n.Contains("leg_") || n.Contains("ankle") || n.Contains("toe") || n.Contains("foot") || n.Contains("knee"))
            {
                if (isL) return AnatomicalRegion.LegL;
                if (isR) return AnatomicalRegion.LegR;
            }
            return AnatomicalRegion.TorsoHead;
        }

        private static AnatomicalRegion[] BuildBoneRegionLookup(Skeleton3D skeleton)
        {
            if (skeleton == null) return Array.Empty<AnatomicalRegion>();
            int count = skeleton.GetBoneCount();
            var lookup = new AnatomicalRegion[count];
            for (int b = 0; b < count; b++)
            {
                lookup[b] = ClassifyBoneRegion(skeleton.GetBoneName(b));
            }
            return lookup;
        }

        private static AnatomicalRegion[] BuildCapsuleRegionLookup(List<BoneCapsuleSegment> capsules, AnatomicalRegion[] boneRegions)
        {
            if (capsules == null || capsules.Count == 0) return Array.Empty<AnatomicalRegion>();
            var lookup = new AnatomicalRegion[capsules.Count];
            for (int i = 0; i < capsules.Count; i++)
            {
                int bIdx = capsules[i].BoneIndex;
                lookup[i] = (bIdx >= 0 && bIdx < boneRegions.Length) ? boneRegions[bIdx] : ClassifyBoneRegion(capsules[i].BoneName);
            }
            return lookup;
        }

        /// <summary>
        /// Collapses individual finger phalanx donor weights onto hand_L / hand_R so low-poly
        /// or fingerless unrigged meshes (e.g. solid mitten hands) do not tear when fingers curl.
        /// </summary>
        private static void CollapseDonorFingerWeights(List<DonorVertex> donors, Skeleton3D heroSkeleton)
        {
            if (donors == null || heroSkeleton == null) return;
            int handL = FindBoneAny(heroSkeleton, "hand_L", "wrist_L");
            int handR = FindBoneAny(heroSkeleton, "hand_R", "wrist_R");
            int boneCount = heroSkeleton.GetBoneCount();
            if (boneCount <= 0 || (handL < 0 && handR < 0)) return;

            int[] remap = new int[boneCount];
            bool anyRemap = false;
            for (int b = 0; b < boneCount; b++)
            {
                remap[b] = b;
                string bn = heroSkeleton.GetBoneName(b).ToLowerInvariant();
                if (bn.Contains("finger") || bn.Contains("thumb") || bn.Contains("index") || bn.Contains("middle") || bn.Contains("ring") || bn.Contains("pinky"))
                {
                    if ((bn.EndsWith("_l") || bn.Contains("_l_")) && handL >= 0) { remap[b] = handL; anyRemap = true; }
                    else if ((bn.EndsWith("_r") || bn.Contains("_r_")) && handR >= 0) { remap[b] = handR; anyRemap = true; }
                }
            }
            if (!anyRemap) return;

            Span<int> mergedB = stackalloc int[4];
            Span<float> mergedW = stackalloc float[4];

            for (int d = 0; d < donors.Count; d++)
            {
                var dv = donors[d];
                if (dv.BoneIndices == null || dv.BoneWeights == null) continue;
                bool touched = false;
                for (int i = 0; i < 4; i++)
                {
                    int b = dv.BoneIndices[i];
                    if (b >= 0 && b < boneCount && remap[b] != b && dv.BoneWeights[i] > 0f)
                    {
                        touched = true;
                        break;
                    }
                }
                if (!touched) continue;

                mergedB.Clear();
                mergedW.Clear();
                int mCount = 0;
                for (int i = 0; i < 4; i++)
                {
                    float w = dv.BoneWeights[i];
                    if (w <= 0f) continue;
                    int b = dv.BoneIndices[i];
                    int targetB = (b >= 0 && b < boneCount) ? remap[b] : b;
                    int existing = -1;
                    for (int m = 0; m < mCount; m++)
                    {
                        if (mergedB[m] == targetB) { existing = m; break; }
                    }
                    if (existing >= 0) mergedW[existing] += w;
                    else if (mCount < 4) { mergedB[mCount] = targetB; mergedW[mCount] = w; mCount++; }
                }
                float sum = 0f;
                for (int m = 0; m < mCount; m++) sum += mergedW[m];
                for (int i = 0; i < 4; i++)
                {
                    dv.BoneIndices[i] = i < mCount ? mergedB[i] : 0;
                    dv.BoneWeights[i] = (i < mCount && sum > 1e-6f) ? mergedW[i] / sum : 0f;
                }
                donors[d] = dv;
            }
        }

        private static void ApplyWorldRotationToBone(Skeleton3D skeleton, int boneIdx, Quaternion worldRot)
        {
            if (skeleton == null || boneIdx < 0) return;
            int parentIdx = skeleton.GetBoneParent(boneIdx);
            Basis parentGlobalBasis = parentIdx >= 0 ? skeleton.GetBoneGlobalPose(parentIdx).Basis.Orthonormalized() : Basis.Identity;
            Basis currGlobalBasis = skeleton.GetBoneGlobalPose(boneIdx).Basis.Orthonormalized();
            Basis desiredGlobalBasis = (new Basis(worldRot) * currGlobalBasis).Orthonormalized();
            Basis desiredLocalBasis = (parentGlobalBasis.Inverse() * desiredGlobalBasis).Orthonormalized();
            skeleton.SetBonePoseRotation(boneIdx, desiredLocalBasis.GetRotationQuaternion());
        }

        /// <summary>
        /// Poses the reference Deadlock hero skeleton to match the active unrigged calibration parameters
        /// (coronal arm raise/drop, sagittal forward pitch, elbow straightening, and leg stance spread).
        /// Used both for live ghost preview feedback and for generating pose-matched donor weights.
        /// </summary>
        public static bool PoseHeroSkeletonForOptions(Skeleton3D skeleton, RiggingOptions options)
        {
            if (skeleton == null || !GodotObject.IsInstanceValid(skeleton) || options == null) return false;

            bool useTPose = options.Preset == PosePreset.TPose;
            bool hasCustomCalibration = options.Preset == PosePreset.APoseCustomAngle ||
                                        MathF.Abs(options.CustomArmAngleDeg) > 0.25f ||
                                        MathF.Abs(options.CustomArmForwardDeg) > 0.25f ||
                                        options.CustomElbowStraightenPct > 0.5f ||
                                        MathF.Abs(options.CustomLegSpreadDeg) > 0.25f;

            if (!useTPose && !hasCustomCalibration) return false;

            float armAngleDeg = useTPose ? 45f : options.CustomArmAngleDeg;
            float armForwardDeg = useTPose ? 0f : options.CustomArmForwardDeg;
            float elbowStraightenPct = useTPose ? 0f : options.CustomElbowStraightenPct;
            float legSpreadDeg = useTPose ? 0f : options.CustomLegSpreadDeg;

            int armUpperL = FindBoneAny(skeleton, "arm_upper_L", "upperarm_L", "upper_arm_L", "Bip01_L_UpperArm");
            int armLowerL = FindBoneAny(skeleton, "arm_lower_L", "forearm_L", "lower_arm_L", "Bip01_L_Forearm");
            int handL = FindBoneAny(skeleton, "hand_L", "wrist_L");
            int armUpperR = FindBoneAny(skeleton, "arm_upper_R", "upperarm_R", "upper_arm_R", "Bip01_R_UpperArm");
            int armLowerR = FindBoneAny(skeleton, "arm_lower_R", "forearm_R", "lower_arm_R", "Bip01_R_Forearm");
            int handR = FindBoneAny(skeleton, "hand_R", "wrist_R");
            int legUpperL = FindBoneAny(skeleton, "leg_upper_L", "thigh_L", "Bip01_L_Thigh");
            int legUpperR = FindBoneAny(skeleton, "leg_upper_R", "thigh_R", "Bip01_R_Thigh");

            // Determine skeleton facing & left/right sign from actual rest landmarks
            float latSign = 1f;
            if (armUpperL >= 0 && armUpperR >= 0)
            {
                float dx = skeleton.GetBoneGlobalRest(armUpperL).Origin.X - skeleton.GetBoneGlobalRest(armUpperR).Origin.X;
                if (MathF.Abs(dx) > 0.01f) latSign = MathF.Sign(dx);
            }
            Vector3 forwardDir = Vector3.Back; // +Z forward in Godot glTF hero rigs
            int ankleL = FindBoneAny(skeleton, "ankle_L", "foot_L");
            int toeL = FindBoneAny(skeleton, "toe_L", "ball_L");
            if (ankleL >= 0 && toeL >= 0)
            {
                float dz = skeleton.GetBoneGlobalRest(toeL).Origin.Z - skeleton.GetBoneGlobalRest(ankleL).Origin.Z;
                if (MathF.Abs(dz) > 0.01f) forwardDir = new Vector3(0f, 0f, MathF.Sign(dz));
            }
            Vector3 sagittalAxis = Vector3.Down.Cross(forwardDir).Normalized();

            void PoseUpperLimb(int upperIdx, bool isLeft, float coronalDeg, float fwdDeg)
            {
                if (upperIdx < 0) return;
                if (MathF.Abs(coronalDeg) < 0.05f && MathF.Abs(fwdDeg) < 0.05f) return;
                Vector3 outwardDir = new Vector3(latSign * (isLeft ? 1f : -1f), 0f, 0f);
                Vector3 coronalAxis = Vector3.Down.Cross(outwardDir).Normalized();
                Quaternion qCoronal = new Quaternion(coronalAxis, Mathf.DegToRad(coronalDeg));
                Quaternion qFwd = new Quaternion(sagittalAxis, Mathf.DegToRad(fwdDeg));
                ApplyWorldRotationToBone(skeleton, upperIdx, qFwd * qCoronal);
            }

            void StraightenElbow(int upperIdx, int lowerIdx, int handIdx, float pct)
            {
                if (upperIdx < 0 || lowerIdx < 0 || handIdx < 0 || pct <= 0.1f) return;
                float t = Mathf.Clamp(pct / 100f, 0f, 1f);
                Vector3 pUpper = skeleton.GetBoneGlobalPose(upperIdx).Origin;
                Vector3 pLower = skeleton.GetBoneGlobalPose(lowerIdx).Origin;
                Vector3 pHand = skeleton.GetBoneGlobalPose(handIdx).Origin;
                Vector3 vUpper = (pLower - pUpper).Normalized();
                Vector3 vLower = (pHand - pLower).Normalized();
                if (vUpper.LengthSquared() < 1e-4f || vLower.LengthSquared() < 1e-4f) return;
                if (vUpper.Dot(vLower) < 0.9995f)
                {
                    Quaternion qAlign = new Quaternion(vLower, vUpper);
                    Quaternion qStep = Quaternion.Identity.Slerp(qAlign, t);
                    ApplyWorldRotationToBone(skeleton, lowerIdx, qStep);
                }
            }

            // 1. Pose shoulders (coronal raise/drop + sagittal pitch)
            PoseUpperLimb(armUpperL, true, armAngleDeg, armForwardDeg);
            PoseUpperLimb(armUpperR, false, armAngleDeg, armForwardDeg);

            // 2. Pose legs (coronal stance spread)
            PoseUpperLimb(legUpperL, true, legSpreadDeg, 0f);
            PoseUpperLimb(legUpperR, false, legSpreadDeg, 0f);
            skeleton.ForceUpdateAllBoneTransforms();

            // 3. Straighten elbows after upper arm is posed
            StraightenElbow(armUpperL, armLowerL, handL, elbowStraightenPct);
            StraightenElbow(armUpperR, armLowerR, handR, elbowStraightenPct);
            skeleton.ForceUpdateAllBoneTransforms();
            return true;
        }

        /// <summary>
        /// Geometrically analyzes an aligned unrigged mesh against the Deadlock hero's rest skeleton
        /// and estimates macro limb pose adjustments (ArmAngleDeg, ArmForwardDeg, ElbowStraightenPct, LegSpreadDeg)
        /// without requiring manual bone-by-bone posing.
        /// </summary>
        public static UnriggedPoseCalibration AutoEstimateUnriggedPoseCalibration(
            Node3D importedScene,
            Skeleton3D heroSkeleton,
            Transform3D alignedTransform,
            List<string> includedSubmeshNames = null)
        {
            var cal = new UnriggedPoseCalibration();
            if (importedScene == null || heroSkeleton == null) return cal;

            int pelvisIdx = FindBoneAny(heroSkeleton, "pelvis", "hips");
            int armUpperL = FindBoneAny(heroSkeleton, "arm_upper_L", "upperarm_L", "upper_arm_L", "Bip01_L_UpperArm");
            int armLowerL = FindBoneAny(heroSkeleton, "arm_lower_L", "forearm_L", "lower_arm_L", "Bip01_L_Forearm");
            int handL = FindBoneAny(heroSkeleton, "hand_L", "wrist_L");
            int armUpperR = FindBoneAny(heroSkeleton, "arm_upper_R", "upperarm_R", "upper_arm_R", "Bip01_R_UpperArm");
            int armLowerR = FindBoneAny(heroSkeleton, "arm_lower_R", "forearm_R", "lower_arm_R", "Bip01_R_Forearm");
            int handR = FindBoneAny(heroSkeleton, "hand_R", "wrist_R");
            int legUpperL = FindBoneAny(heroSkeleton, "leg_upper_L", "thigh_L", "Bip01_L_Thigh");
            int ankleL = FindBoneAny(heroSkeleton, "ankle_L", "foot_L");
            int legUpperR = FindBoneAny(heroSkeleton, "leg_upper_R", "thigh_R", "Bip01_R_Thigh");
            int ankleR = FindBoneAny(heroSkeleton, "ankle_R", "foot_R");

            if (pelvisIdx < 0 || armUpperL < 0 || armUpperR < 0 || armLowerL < 0 || armLowerR < 0 || handL < 0 || handR < 0)
                return cal;

            Vector3 pelvisPos = heroSkeleton.GetBoneGlobalRest(pelvisIdx).Origin;
            Vector3 shL = heroSkeleton.GetBoneGlobalRest(armUpperL).Origin;
            Vector3 elL = heroSkeleton.GetBoneGlobalRest(armLowerL).Origin;
            Vector3 wrL = heroSkeleton.GetBoneGlobalRest(handL).Origin;
            Vector3 shR = heroSkeleton.GetBoneGlobalRest(armUpperR).Origin;
            Vector3 elR = heroSkeleton.GetBoneGlobalRest(armLowerR).Origin;
            Vector3 wrR = heroSkeleton.GetBoneGlobalRest(handR).Origin;

            float latSign = MathF.Abs(shL.X - shR.X) > 0.01f ? MathF.Sign(shL.X - shR.X) : 1f;
            float shHalfW = MathF.Max(MathF.Abs(shL.X - pelvisPos.X), MathF.Abs(shR.X - pelvisPos.X));
            float shMidY = (shL.Y + shR.Y) * 0.5f;
            float armLenL = shL.DistanceTo(elL) + elL.DistanceTo(wrL);
            float armLenR = shR.DistanceTo(elR) + elR.DistanceTo(wrR);
            float avgArmLen = (armLenL + armLenR) * 0.5f;

            var allMeshes = CollectMeshes(importedScene);
            var targetMeshes = (includedSubmeshNames != null && includedSubmeshNames.Count > 0)
                ? allMeshes.Where(m => includedSubmeshNames.Contains(m.Name.ToString())).ToList()
                : allMeshes;
            if (targetMeshes.Count == 0) targetMeshes = allMeshes;

            var worldVerts = new List<Vector3>(2048);
            foreach (var mi in targetMeshes)
            {
                if (mi?.Mesh == null) continue;
                Transform3D xf = alignedTransform * ComputeTransformRelativeToRoot(mi, importedScene);
                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                {
                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count == 0) continue;
                    var raw = GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    if (raw == null) continue;
                    for (int i = 0; i < raw.Length; i++) worldVerts.Add(xf * raw[i]);
                }
            }
            if (worldVerts.Count < 20) return cal;

            Vector3 fwdDir = Vector3.Back;
            if (ankleL >= 0)
            {
                int toeL = FindBoneAny(heroSkeleton, "toe_L", "ball_L");
                if (toeL >= 0)
                {
                    float dz = heroSkeleton.GetBoneGlobalRest(toeL).Origin.Z - heroSkeleton.GetBoneGlobalRest(ankleL).Origin.Z;
                    if (MathF.Abs(dz) > 0.01f) fwdDir = new Vector3(0f, 0f, MathF.Sign(dz));
                }
            }
            float fwdSign = fwdDir.Z >= 0f ? 1f : -1f;

            // 1. Estimate coronal arm angle + straightness + forward sweep across Left & Right sides
            float sumArmAngle = 0f;
            float sumElbowStraight = 0f;
            int armSides = 0;
            var armDistVecs = new List<(bool isLeft, Vector3 vec)>();

            void AnalyzeArmSide(bool isLeft, Vector3 sh, Vector3 el, Vector3 wr, float armLen)
            {
                float sideMul = latSign * (isLeft ? 1f : -1f);
                var distalPts = new List<Vector3>();
                var allArmPts = new List<Vector3>();

                for (int i = 0; i < worldVerts.Count; i++)
                {
                    Vector3 v = worldVerts[i];
                    float lat = (v.X - pelvisPos.X) * sideMul;
                    // Strictly outside torso/thigh core width, or clearly raised above shoulder crease
                    float minLat = v.Y > sh.Y - 0.06f ? shHalfW * 0.72f : shHalfW * 0.90f;
                    if (lat < minLat) continue;
                    if (v.Y > sh.Y + armLen * 0.85f || v.Y < sh.Y - armLen * 1.15f) continue;
                    float dist = v.DistanceTo(sh);
                    if (dist < 0.04f || dist > armLen * 1.18f) continue;

                    allArmPts.Add(v);
                    if (dist > armLen * 0.56f && dist <= armLen * 1.10f) distalPts.Add(v);
                }

                cal.DetectedArmSamples += allArmPts.Count;
                if (allArmPts.Count < 4) return;

                Vector3 distCentroid = Vector3.Zero;
                var distSrc = distalPts.Count >= 3 ? distalPts : allArmPts;
                foreach (var p in distSrc) distCentroid += p;
                distCentroid /= distSrc.Count;

                Vector3 impFullDir = distCentroid - sh;
                if (impFullDir.LengthSquared() < 1e-4f) return;

                // Coronal angle from downward -Y toward outward side
                float impLat = (impFullDir.X) * sideMul;
                float impDown = -impFullDir.Y;
                float impCoronalRad = MathF.Atan2(impLat, impDown);

                Vector3 heroFullDir = wr - sh;
                float heroLat = heroFullDir.X * sideMul;
                float heroDown = -heroFullDir.Y;
                float heroCoronalRad = MathF.Atan2(heroLat, heroDown);

                sumArmAngle += Mathf.Clamp(Mathf.RadToDeg(impCoronalRad - heroCoronalRad), -60f, 75f);

                // Elbow straightness: collect upper-arm samples along the shoulder->wrist corridor and compare bend vs hero rest
                Vector3 armAxis = impFullDir.Normalized();
                var upperPts = new List<Vector3>();
                float maxRadial = armLen * 0.30f;
                for (int i = 0; i < allArmPts.Count; i++)
                {
                    Vector3 rel = allArmPts[i] - sh;
                    float proj = rel.Dot(armAxis);
                    if (proj < armLen * 0.20f || proj > armLen * 0.56f) continue;
                    float radial = (rel - armAxis * proj).Length();
                    if (radial <= maxRadial) upperPts.Add(allArmPts[i]);
                }

                Vector3 hU = (el - sh).Normalized();
                Vector3 hL = (wr - el).Normalized();
                float heroBendRad = MathF.Acos(Mathf.Clamp(hU.Dot(hL), -1f, 1f));
                if (heroBendRad > 0.20f && upperPts.Count >= 2 && distalPts.Count >= 2)
                {
                    Vector3 upCentroid = Vector3.Zero;
                    foreach (var p in upperPts) upCentroid += p;
                    upCentroid /= upperPts.Count;
                    Vector3 iU = (upCentroid - sh).Normalized();
                    Vector3 iL = (distCentroid - upCentroid).Normalized();
                    float impBendRad = MathF.Acos(Mathf.Clamp(iU.Dot(iL), -1f, 1f));
                    float straightPct = Mathf.Clamp((1f - impBendRad / heroBendRad) * 100f, 0f, 100f);
                    sumElbowStraight += straightPct;
                }

                armSides++;
                armDistVecs.Add((isLeft, impFullDir));
            }

            AnalyzeArmSide(true, shL, elL, wrL, armLenL);
            AnalyzeArmSide(false, shR, elR, wrR, armLenR);

            if (armSides > 0)
            {
                cal.ArmAngleDeg = MathF.Round(sumArmAngle / armSides, 1);
                cal.ElbowStraightenPct = MathF.Round(sumElbowStraight / armSides, 0);

                // Evaluate sagittal forward pitch AFTER coronal + elbow straightening on a cloned/temporary pose
                // so forward sweep accounts accurately for the straightened elbow reach.
                Dictionary<int, Quaternion> backup = BackupSkeletonLocalRotations(heroSkeleton);
                var tmpOpt = new RiggingOptions
                {
                    Preset = PosePreset.APoseCustomAngle,
                    CustomArmAngleDeg = cal.ArmAngleDeg,
                    CustomArmForwardDeg = 0f,
                    CustomElbowStraightenPct = cal.ElbowStraightenPct,
                    CustomLegSpreadDeg = 0f
                };
                heroSkeleton.ResetBonePoses();
                PoseHeroSkeletonForOptions(heroSkeleton, tmpOpt);

                float sumFwd = 0f;
                int fwdCount = 0;
                foreach (var (isL, impVec) in armDistVecs)
                {
                    int uIdx = isL ? armUpperL : armUpperR;
                    int hIdx = isL ? handL : handR;
                    Vector3 posedVec = heroSkeleton.GetBoneGlobalPose(hIdx).Origin - heroSkeleton.GetBoneGlobalPose(uIdx).Origin;
                    // Project in YZ sagittal plane (using radial magnitude in XY as "down-out" lever so horizontal arms work too)
                    float impSagVert = MathF.Max(0.05f, MathF.Sqrt(impVec.X * impVec.X + impVec.Y * impVec.Y));
                    float impPitch = MathF.Atan2(impVec.Z * fwdSign, impSagVert);
                    float posedSagVert = MathF.Max(0.05f, MathF.Sqrt(posedVec.X * posedVec.X + posedVec.Y * posedVec.Y));
                    float posedPitch = MathF.Atan2(posedVec.Z * fwdSign, posedSagVert);
                    sumFwd += Mathf.Clamp(Mathf.RadToDeg(impPitch - posedPitch), -45f, 45f);
                    fwdCount++;
                }
                if (fwdCount > 0) cal.ArmForwardDeg = MathF.Round(sumFwd / fwdCount, 1);

                RestoreSkeletonLocalRotations(heroSkeleton, backup);
            }

            // 2. Estimate leg stance spread
            if (legUpperL >= 0 && ankleL >= 0 && legUpperR >= 0 && ankleR >= 0)
            {
                Vector3 hipL = heroSkeleton.GetBoneGlobalRest(legUpperL).Origin;
                Vector3 ankL = heroSkeleton.GetBoneGlobalRest(ankleL).Origin;
                Vector3 hipR = heroSkeleton.GetBoneGlobalRest(legUpperR).Origin;
                Vector3 ankR = heroSkeleton.GetBoneGlobalRest(ankleR).Origin;

                float sumLeg = 0f;
                int legSides = 0;

                void AnalyzeLegSide(bool isLeft, Vector3 hip, Vector3 ank)
                {
                    float sideMul = latSign * (isLeft ? 1f : -1f);
                    float maxLegLat = MathF.Max(MathF.Abs(ank.X - pelvisPos.X) + 0.14f, shHalfW * 0.86f);
                    Vector3 legSum = Vector3.Zero;
                    int count = 0;
                    for (int i = 0; i < worldVerts.Count; i++)
                    {
                        Vector3 v = worldVerts[i];
                        float latFromCenter = (v.X - pelvisPos.X) * sideMul;
                        if (latFromCenter < 0.015f || latFromCenter > maxLegLat) continue;
                        if (v.Y > hip.Y - 0.14f || v.Y < ank.Y - 0.05f) continue;
                        legSum += (v - hip);
                        count++;
                    }
                    cal.DetectedLegSamples += count;
                    if (count < 4) return;
                    Vector3 impLeg = legSum / count;
                    Vector3 heroLeg = ank - hip;
                    float impAng = MathF.Atan2(impLeg.X * sideMul, -impLeg.Y);
                    float heroAng = MathF.Atan2(heroLeg.X * sideMul, -heroLeg.Y);
                    sumLeg += Mathf.Clamp(Mathf.RadToDeg(impAng - heroAng), -25f, 35f);
                    legSides++;
                }

                AnalyzeLegSide(true, hipL, ankL);
                AnalyzeLegSide(false, hipR, ankR);
                if (legSides > 0) cal.LegSpreadDeg = MathF.Round(sumLeg / legSides, 1);
            }

            return cal;
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
            // Create a dedicated Skin instance duplicated from heroSkin (or freshly created)
            // so we never mutate the vanilla hero model's skin and guarantee every bone in heroSkeleton
            // has a valid, non-zero InverseBindMatrix entry!
            Skin customSkin = heroSkin != null ? (Skin)heroSkin.Duplicate() : CreateFullSkeletonSkin(heroSkeleton);
            for (int b = 0; b < heroSkeleton.GetBoneCount(); b++)
            {
                GetOrAddSkinBindIndex(customSkin, heroSkeleton, b);
            }

            // Ensure unlit/emissive materials and outline shell metadata are sanitized on the imported scene
            SanitizeImportedModelMaterials(importedScene, options.SubmeshIsOutlineShell);

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
                    result = RigRigidBoneModel(importedScene, importedMeshes, heroNode, heroSkeleton, customSkin, options);
                }
                else if (options.Method == RiggingMethod.PreRiggedSkeletonRemap && hasImportedRig)
                {
                    result = RigPreRiggedModel(importedScene, importedMeshes, importedSkeleton, heroNode, heroSkeleton, customSkin, options);
                }
                else
                {
                    // Default & Recommended: KNN Surface Projection!
                    result = RigUnriggedModel(importedScene, importedMeshes, heroNode, heroSkeleton, customSkin, options);
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

            // 2. Pose hero skeleton if T-Pose or unrigged calibration requested, to create pose-matched donor cloud
            Dictionary<int, Quaternion> originalLocalRotations = BackupSkeletonLocalRotations(heroSkeleton);
            heroSkeleton.ResetBonePoses(); // Force 100% symmetrical bind pose before arm adjustments!

            Transform3D[] posedGlobalBoneTransforms = new Transform3D[heroSkeleton.GetBoneCount()];
            Transform3D[] restGlobalBoneTransforms = new Transform3D[heroSkeleton.GetBoneCount()];

            for (int b = 0; b < heroSkeleton.GetBoneCount(); b++)
            {
                restGlobalBoneTransforms[b] = heroSkeleton.GetBoneGlobalRest(b);
            }

            bool isPoseMatched = PoseHeroSkeletonForOptions(heroSkeleton, options);

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
            var capsuleSegments = BuildCapsuleSegments(heroSkeleton, posedGlobalBoneTransforms);
            var capsuleRegions = BuildCapsuleRegionLookup(capsuleSegments, boneRegions);
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
            LimbTwistProfile limbTwistProfile = options.AutoCounterTwistWeights
                ? BuildLimbTwistProfile(heroSkeleton, restGlobalBoneTransforms)
                : null;

            Skeleton3D srcFallbackSkel = FindSkeleton(importedScene);
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
                    else
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
                    if (!isSocketProp && options.SmoothingIterations > 0 && rawIndices != null && rawIndices.Length >= 3)
                    {
                        var swLapStep = System.Diagnostics.Stopwatch.StartNew();
                        SmoothWeightsLaplacian(rawIndices, vCount, boneIndices, boneWeights, options.SmoothingIterations, alignedVerts);
                        swLapStep.Stop();
                        totalLapMs += swLapStep.ElapsedMilliseconds;
                    }

                    // Stabilize neck weights against floating-head stretch (e.g. Paradox/Chrono)
                    if (!isSocketProp && options.StabilizeNeckFloatingHead)
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
                    if (!isSocketProp && options.AutoCounterTwistWeights)
                    {
                        AdjustLimbTwistAndHipWeights(heroSkeleton, vCount, boneIndices, boneWeights, restVerts, rawIndices, limbTwistProfile);
                    }

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
                    ConfigureRiggedSubmeshMetadata(newMi, srcMi, surfMat, options);
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
                Vector3 rawPelvisDelta = (PtPelvis - PsPelvis) * 0.5f;
                if (rawPelvisDelta.Length() > 0.45f)
                    rawPelvisDelta = rawPelvisDelta.Normalized() * 0.45f;
                pelvisOffset = rawPelvisDelta;
            }

            // Precompute thread-safe managed lookups BEFORE entering Parallel.For (eliminates CowData race crashes on Skeleton3D)
            int srcBoneCount = sourceSkeleton.GetBoneCount();
            int[] srcBoneToResolvedTgt = new int[srcBoneCount];
            for (int sb = 0; sb < srcBoneCount; sb++)
            {
                srcBoneToResolvedTgt[sb] = ResolveTargetBoneWithAncestorFallback(sb, sourceSkeleton, sourceToTargetBoneMap, targetSkeleton, options.KeepFingersRigidOnHand);
            }

            int tgtBoneCount = targetSkeleton.GetBoneCount();
            // 0 = none, 1 = leftArm, 2 = rightArm, 3 = leftLeg, 4 = rightLeg, 5 = headSeg, 6 = pelvis
            byte[] tgtSegmentKind = new byte[tgtBoneCount];
            for (int tb = 0; tb < tgtBoneCount; tb++)
            {
                if (leftArm != null && leftArm.AppliesTo(tb, targetSkeleton)) tgtSegmentKind[tb] = 1;
                else if (rightArm != null && rightArm.AppliesTo(tb, targetSkeleton)) tgtSegmentKind[tb] = 2;
                else if (leftLeg != null && leftLeg.AppliesTo(tb, targetSkeleton)) tgtSegmentKind[tb] = 3;
                else if (rightLeg != null && rightLeg.AppliesTo(tb, targetSkeleton)) tgtSegmentKind[tb] = 4;
                else if (headSeg != null && headSeg.AppliesTo(tb, targetSkeleton)) tgtSegmentKind[tb] = 5;
                else if (tb == pelvisBone) tgtSegmentKind[tb] = 6;
            }

            int headBonePre = targetSkeleton.FindBone("head");
            int neckBonePre = targetSkeleton.FindBone("neck_0");
            if (neckBonePre < 0) neckBonePre = targetSkeleton.FindBone("neck");
            int spine3BonePre = targetSkeleton.FindBone("spine_3");
            if (spine3BonePre < 0) spine3BonePre = targetSkeleton.FindBone("spine_2");
            int spine2BonePre = targetSkeleton.FindBone("spine_2");

            int tgtTwistL = FindBoneAny(targetSkeleton, "arm_lower_L_TWIST", "arm_lower_twist_L", "arm_lower_l_twist");
            if (tgtTwistL == -1) tgtTwistL = EnsureForearmTwistBone(targetSkeleton, isLeft: true);
            int tgtTwistR = FindBoneAny(targetSkeleton, "arm_lower_R_TWIST", "arm_lower_twist_R", "arm_lower_r_twist");
            if (tgtTwistR == -1) tgtTwistR = EnsureForearmTwistBone(targetSkeleton, isLeft: false);

            if (heroSkin != null)
            {
                EnsureSkinHasBind(heroSkin, targetSkeleton, tgtTwistL);
                EnsureSkinHasBind(heroSkin, targetSkeleton, tgtTwistR);
            }

            LimbTwistProfile limbTwistProfile = options.AutoCounterTwistWeights
                ? BuildLimbTwistProfile(targetSkeleton)
                : null;

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


                    int[] skelBoneIndices = new int[vCount * 4];
                    float[] outWeights = new float[vCount * 4];
                    Skin propSkin = null;
                    SocketBakeMeta? socketMeta = null;

                    if (isSocketProp)
                    {
                        // Evaluate accurate rest-pose geometry in Hero space before optional socket offsets
                        EvaluateMeshSurfaceInTargetSpace(srcMi, importedScene, options.AlignedTransform, sourceSkeleton, rawVerts, rawNorms, rawBones, rawWeights, out Vector3[] sockVerts, out Vector3[] sockNorms);

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
                        EvaluateMeshSurfaceInTargetSpace(srcMi, importedScene, options.AlignedTransform, sourceSkeleton, rawVerts, rawNorms, rawBones, rawWeights, out Vector3[] baseHeroVerts, out Vector3[] baseHeroNorms);

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

                        System.Threading.Tasks.Parallel.For(0, vCount, v =>
                        {
                            Vector3 vHero = baseHeroVerts[v];
                            Vector3 nHero = baseHeroNorms[v];

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

                            // Stabilize neck weights against floating-head stretch (e.g. Paradox/Chrono)
                            if (options.StabilizeNeckFloatingHead)
                            {
                                StabilizeNeckFloatingHeadVertex(topBones, topWeights, headBonePre, neckBonePre, spine3BonePre, spine2BonePre, vHero.Y);
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

                        if (wristCount > 0 && rawIndices != null && rawIndices.Length > 0)
                        {
                            SmoothWeightsLaplacian(rawIndices, vCount, skelBoneIndices, outWeights, 2, alignedVerts, wristMask);
                        }

                        if (options.AutoCounterTwistWeights)
                        {
                            AdjustLimbTwistAndHipWeights(targetSkeleton, vCount, skelBoneIndices, outWeights, alignedVerts, rawIndices, limbTwistProfile);
                        }
                    }

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
                    newMi.SetMeta("OriginalMaterial_0", surfMat);
                    ConfigureRiggedSubmeshMetadata(newMi, srcMi, surfMat, options);
                    if (socketMeta.HasValue) ApplySocketMetadata(newMi, srcMi.Name.ToString(), socketMeta.Value);

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

            Skeleton3D hybridFallbackSkel = FindSkeleton(importedScene);
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
                    var rawUvs = arrays.Count > (int)Mesh.ArrayType.TexUV ? GetVector2Array(arrays[(int)Mesh.ArrayType.TexUV]) : null;
                    var rawIndices = arrays.Count > (int)Mesh.ArrayType.Index ? GetInt32Array(arrays[(int)Mesh.ArrayType.Index]) : null;
                    var rawBones = arrays.Count > (int)Mesh.ArrayType.Bones ? GetInt32Array(arrays[(int)Mesh.ArrayType.Bones]) : null;
                    var rawWeights = arrays.Count > (int)Mesh.ArrayType.Weights ? GetFloat32Array(arrays[(int)Mesh.ArrayType.Weights]) : null;

                    if (rawVerts == null || rawVerts.Length == 0) continue;

                    int vCount = rawVerts.Length;
                    totalVerts += vCount;
                    totalTris += (rawIndices != null) ? rawIndices.Length / 3 : vCount / 3;

                    EvaluateMeshSurfaceInTargetSpace(srcMi, importedScene, options.AlignedTransform, hybridFallbackSkel, rawVerts, rawNorms, rawBones, rawWeights, out Vector3[] alignedVerts, out Vector3[] alignedNorms);

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
                    ConfigureRiggedSubmeshMetadata(newMi, srcMi, surfMat, options);

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

            Skeleton3D sockFallbackSkel = FindSkeleton(importedScene);
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
                    var rawBones = arrays.Count > (int)Mesh.ArrayType.Bones ? GetInt32Array(arrays[(int)Mesh.ArrayType.Bones]) : null;
                    var rawWeights = arrays.Count > (int)Mesh.ArrayType.Weights ? GetFloat32Array(arrays[(int)Mesh.ArrayType.Weights]) : null;

                    EvaluateMeshSurfaceInTargetSpace(mi, importedScene, options.AlignedTransform, sockFallbackSkel, verts, null, rawBones, rawWeights, out var xformVerts, out _);
                    for (int v = 0; v < xformVerts.Length; v++)
                    {
                        vList.Add(xformVerts[v]);
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
            if (srcUpper == srcLower || srcLower == srcHand || srcUpper == srcHand)
            {
                GD.PushWarning($"[CustomModelAutoRigger] Skipping Auto-Snap Arm Registration for '{upperName}': source bones collapsed/duplicated.");
                return null;
            }

            Vector3 As = skelToHero * sourceSkeleton.GetBoneGlobalRest(srcUpper).Origin;
            Vector3 Bs = skelToHero * sourceSkeleton.GetBoneGlobalRest(srcLower).Origin;
            Vector3 Cs = skelToHero * sourceSkeleton.GetBoneGlobalRest(srcHand).Origin;

            Vector3 At = targetSkeleton.GetBoneGlobalRest(tgtUpper).Origin;
            Vector3 Bt = targetSkeleton.GetBoneGlobalRest(tgtLower).Origin;
            Vector3 Ct = targetSkeleton.GetBoneGlobalRest(tgtHand).Origin;

            float Ls1 = (Bs - As).Length();
            float Lt1 = (Bt - At).Length();
            if (Ls1 < 0.02f || Lt1 < 0.02f) return null;

            float Ls2 = (Cs - Bs).Length();
            float Lt2 = (Ct - Bt).Length();
            if (Ls2 < 0.02f || Lt2 < 0.02f) return null;

            Vector3 uS1 = (Bs - As) / Ls1;
            Vector3 uT1 = (Bt - At) / Lt1;
            Quaternion R1 = ShortestArc(uS1, uT1);
            float sigma1 = Mathf.Clamp(Lt1 / Ls1, 0.75f, 1.35f);

            Vector3 uS2 = (Cs - Bs) / Ls2;
            Vector3 uT2 = (Ct - Bt) / Lt2;
            Quaternion R2 = ShortestArc(uS2, uT2);
            float sigma2 = Mathf.Clamp(Lt2 / Ls2, 0.75f, 1.35f);

            float maxArmAngleDeg = Mathf.RadToDeg(Mathf.Max(R1.GetAngle(), R2.GetAngle()));
            if (maxArmAngleDeg > 115f)
            {
                GD.PushWarning($"[CustomModelAutoRigger] Skipping Auto-Snap Arm Registration for '{upperName}': extreme angle mismatch ({maxArmAngleDeg:F1}° > 115°), likely mismatched axis or side.");
                return null;
            }

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
            if (srcThigh == srcCalf || srcCalf == srcAnkle || srcThigh == srcAnkle)
            {
                GD.PushWarning($"[CustomModelAutoRigger] Skipping Auto-Snap Leg Registration for '{thighName}': source bones collapsed/duplicated.");
                return null;
            }

            Vector3 As = skelToHero * sourceSkeleton.GetBoneGlobalRest(srcThigh).Origin;
            Vector3 Bs = skelToHero * sourceSkeleton.GetBoneGlobalRest(srcCalf).Origin;
            Vector3 Cs = skelToHero * sourceSkeleton.GetBoneGlobalRest(srcAnkle).Origin;

            Vector3 At = targetSkeleton.GetBoneGlobalRest(tgtThigh).Origin;
            Vector3 Bt = targetSkeleton.GetBoneGlobalRest(tgtCalf).Origin;
            Vector3 Ct = targetSkeleton.GetBoneGlobalRest(tgtAnkle).Origin;

            float Ls1 = (Bs - As).Length();
            float Lt1 = (Bt - At).Length();
            if (Ls1 < 0.02f || Lt1 < 0.02f) return null;

            float Ls2 = (Cs - Bs).Length();
            float Lt2 = (Ct - Bt).Length();
            if (Ls2 < 0.02f || Lt2 < 0.02f) return null;

            Vector3 uS1 = (Bs - As) / Ls1;
            Vector3 uT1 = (Bt - At) / Lt1;
            Quaternion R1 = ShortestArc(uS1, uT1);
            float sigma1 = Mathf.Clamp(Lt1 / Ls1, 0.75f, 1.35f);

            Vector3 uS2 = (Cs - Bs) / Ls2;
            Vector3 uT2 = (Ct - Bt) / Lt2;
            Quaternion R2 = ShortestArc(uS2, uT2);
            float sigma2 = Mathf.Clamp(Lt2 / Ls2, 0.75f, 1.35f);

            float maxLegAngleDeg = Mathf.RadToDeg(Mathf.Max(R1.GetAngle(), R2.GetAngle()));
            if (maxLegAngleDeg > 115f)
            {
                GD.PushWarning($"[CustomModelAutoRigger] Skipping Auto-Snap Leg Registration for '{thighName}': extreme angle mismatch ({maxLegAngleDeg:F1}° > 115°), likely mismatched axis or side.");
                return null;
            }

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

        private static bool IsAlwaysExcludedNonBodyBone(string lowerBoneName)
        {
            return lowerBoneName.Contains("weapon") || lowerBoneName.Contains("gun") ||
                   lowerBoneName.Contains("iktarget") || lowerBoneName.Contains("_ik") || lowerBoneName.StartsWith("ik_") ||
                   lowerBoneName.Contains("pivot") || lowerBoneName.Contains("follow") || lowerBoneName.Contains("muzzle") ||
                   lowerBoneName.Contains("_fx") || lowerBoneName.EndsWith("fx") ||
                   lowerBoneName.Contains("root") || lowerBoneName.Contains("world") ||
                   lowerBoneName.Contains("attach") || lowerBoneName.Contains("camera") ||
                   lowerBoneName.Contains("prop") || lowerBoneName.Contains("stake") ||
                   lowerBoneName.Contains("cigar") || lowerBoneName.Contains("drum");
        }

        private static bool IsExcludedClothOrAccessoryBone(string lowerBoneName)
        {
            return IsAlwaysExcludedNonBodyBone(lowerBoneName) ||
                   lowerBoneName.Contains("cloth") || lowerBoneName.Contains("cape") ||
                   lowerBoneName.Contains("skirt") || lowerBoneName.Contains("hair") ||
                   lowerBoneName.Contains("coat") || lowerBoneName.Contains("dress") ||
                   lowerBoneName.Contains("tassel") || lowerBoneName.Contains("ponytail") ||
                   lowerBoneName.Contains("ribbon") || lowerBoneName.Contains("sash") ||
                   lowerBoneName.Contains("belt") || lowerBoneName.Contains("holster") ||
                   lowerBoneName.Contains("scabbard") || lowerBoneName.Contains("cloak") ||
                   lowerBoneName.Contains("scarf") || lowerBoneName.Contains("apron") ||
                   lowerBoneName.Contains("strap") || lowerBoneName.Contains("flap") ||
                   lowerBoneName.Contains("chain") || lowerBoneName.Contains("rope") ||
                   lowerBoneName.Contains("shoulder_armor");
        }

        private static void PoseArmUpperBoneCoronal(Skeleton3D skeleton, int upperIdx, int lowerIdx, bool isLeft, float angleRad)
        {
            if (upperIdx < 0) return;
            Vector3 worldLiftAxis = isLeft ? Vector3.Back : Vector3.Forward;
            if (lowerIdx >= 0)
            {
                Vector3 armDir = skeleton.GetBoneGlobalRest(lowerIdx).Origin - skeleton.GetBoneGlobalRest(upperIdx).Origin;
                Vector3 cross = armDir.Cross(Vector3.Up);
                if (cross.LengthSquared() > 1e-6f)
                {
                    worldLiftAxis = cross.Normalized();
                }
            }

            Basis boneRestBasis = skeleton.GetBoneGlobalRest(upperIdx).Basis;
            Vector3 localAxis = (boneRestBasis.Inverse() * worldLiftAxis).Normalized();
            Quaternion qDelta = new Quaternion(localAxis, angleRad);
            skeleton.SetBonePoseRotation(upperIdx, qDelta * skeleton.GetBonePoseRotation(upperIdx));
        }

        private static void PoseHeroArmsForPreset(Skeleton3D skeleton, PosePreset preset, float customAngleDeg)
        {
            float targetAngleDeg = preset == PosePreset.TPose ? 45f : customAngleDeg;
            float angleRad = Mathf.DegToRad(targetAngleDeg);

            int armUpperL = FindBoneAny(skeleton, "arm_upper_L", "arm_upper_l", "upperarm_l", "leftarm");
            int armLowerL = FindBoneAny(skeleton, "arm_lower_L", "arm_lower_l", "forearm_l", "leftforearm");
            int armUpperR = FindBoneAny(skeleton, "arm_upper_R", "arm_upper_r", "upperarm_r", "rightarm");
            int armLowerR = FindBoneAny(skeleton, "arm_lower_R", "arm_lower_r", "forearm_r", "rightforearm");

            PoseArmUpperBoneCoronal(skeleton, armUpperL, armLowerL, isLeft: true, angleRad);
            PoseArmUpperBoneCoronal(skeleton, armUpperR, armLowerR, isLeft: false, angleRad);

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
            var result = new List<DonorVertex>(3500);
            var meshes = CollectOriginalHeroMeshes(heroNode);
            Skin fallbackHeroSkin = FindSkin(heroNode);

            int boneCount = Math.Min(restGlobal.Length, posedGlobal.Length);
            Transform3D[] skinMatrices = new Transform3D[boneCount];
            for (int b = 0; b < boneCount; b++)
            {
                skinMatrices[b] = posedGlobal[b] * restGlobal[b].AffineInverse();
            }

            float subGridSize = 0.045f; // 4.5 cm subsampling for dense limb coverage without spatial holes
            var seenCells = new HashSet<Vector3I>(3500);
            bool excludeClothHair = options?.ExcludeClothHairBones ?? true;
            Span<int> topBones = stackalloc int[4];
            Span<float> topWeights = stackalloc float[4];

            foreach (var mi in meshes)
            {
                if (mi.Mesh == null || mi.Mesh.GetSurfaceCount() == 0) continue;
                if (mi.HasMeta("IsCustomImportedMesh")) continue;

                string rawName = mi.Name.ToString().ToLowerInvariant();
                if (rawName.Contains("weapon") || rawName.Contains("gun") || rawName.Contains("cloth") ||
                    rawName.Contains("cape") || rawName.Contains("stake") || rawName.Contains("drum") ||
                    rawName.Contains("cigar") || rawName.Contains("holster") || rawName.Contains("prop") ||
                    rawName.Contains("_ui_") || rawName.Contains("_lod"))
                {
                    // Skip weapon, prop, cloth, UI-head swap, and LOD meshes from donor cloud
                    continue;
                }

                Transform3D meshToHero = ComputeTransformRelativeToRoot(mi, heroNode);
                Skin meshSkin = mi.Skin ?? fallbackHeroSkin;

                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                {
                    Material surfMat = mi.GetActiveMaterial(s) ?? mi.Mesh.SurfaceGetMaterial(s);
                    string matLabel = ((surfMat?.ResourceName ?? "") + " " + (surfMat?.ResourcePath ?? "")).ToLowerInvariant();
                    if (matLabel.Contains("weapon") || matLabel.Contains("gun") || matLabel.Contains("shotgun") ||
                        matLabel.Contains("crossbow") || matLabel.Contains("stake") || matLabel.Contains("cigar") ||
                        matLabel.Contains("drum") || matLabel.Contains("holster") || matLabel.Contains("prop") ||
                        matLabel.Contains("sword") || matLabel.Contains("knife") || matLabel.Contains("rifle") ||
                        matLabel.Contains("pistol"))
                    {
                        continue;
                    }

                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count == 0) continue;

                    var verts = GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    var bones = arrays.Count > (int)Mesh.ArrayType.Bones ? GetInt32Array(arrays[(int)Mesh.ArrayType.Bones]) : null;
                    var weights = arrays.Count > (int)Mesh.ArrayType.Weights ? GetFloat32Array(arrays[(int)Mesh.ArrayType.Weights]) : null;

                    if (verts == null || bones == null || weights == null || verts.Length == 0) continue;

                    int stride = (bones.Length >= verts.Length * 8 && weights.Length >= verts.Length * 8) ? 8 : 4;
                    if (bones.Length < verts.Length * stride || weights.Length < verts.Length * stride) continue;

                    for (int v = 0; v < verts.Length; v++)
                    {
                        topBones.Clear();
                        topWeights.Clear();

                        int baseOff = v * stride;
                        for (int k = 0; k < stride; k++)
                        {
                            float bw = weights[baseOff + k];
                            if (bw <= 0.0001f) continue;

                            int rb = bones[baseOff + k];
                            int sk = meshSkin != null
                                ? ResolveSkinBindBone(meshSkin, rb, skeleton)
                                : (bindToSkeletonMap.TryGetValue(rb, out int sVal) ? sVal : rb);

                            if (sk < 0 || sk >= skeleton.GetBoneCount()) continue;

                            string bName = skeleton.GetBoneName(sk).ToLowerInvariant();
                            if (excludeClothHair ? IsExcludedClothOrAccessoryBone(bName) : IsAlwaysExcludedNonBodyBone(bName))
                            {
                                continue;
                            }

                            // Merge duplicate bone entries or insert into sorted top-4 slots
                            bool merged = false;
                            for (int t = 0; t < 4; t++)
                            {
                                if (topWeights[t] > 0f && topBones[t] == sk)
                                {
                                    topWeights[t] += bw;
                                    merged = true;
                                    break;
                                }
                            }
                            if (merged) continue;

                            int minSlot = 0;
                            for (int t = 1; t < 4; t++)
                            {
                                if (topWeights[t] < topWeights[minSlot]) minSlot = t;
                            }
                            if (bw > topWeights[minSlot])
                            {
                                topBones[minSlot] = sk;
                                topWeights[minSlot] = bw;
                            }
                        }

                        float sum = topWeights[0] + topWeights[1] + topWeights[2] + topWeights[3];
                        if (sum <= 0.0001f) continue;

                        int primarySkelBone = topBones[0];
                        float bestW = topWeights[0];
                        for (int t = 1; t < 4; t++)
                        {
                            if (topWeights[t] > bestW)
                            {
                                bestW = topWeights[t];
                                primarySkelBone = topBones[t];
                            }
                        }

                        bool isHandOrFinger = false;
                        if (primarySkelBone >= 0 && primarySkelBone < skeleton.GetBoneCount())
                        {
                            string bName = skeleton.GetBoneName(primarySkelBone).ToLowerInvariant();
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
                        for (int t = 0; t < 4; t++)
                        {
                            bIndices[t] = topBones[t];
                            bWeights[t] = topWeights[t] / sum;
                        }

                        // Compute deformed position via CPU LBS using precomputed skinMatrices
                        Vector3 posedPos = Vector3.Zero;
                        for (int t = 0; t < 4; t++)
                        {
                            float w = bWeights[t];
                            if (w <= 0.0001f) continue;

                            int b = bIndices[t];
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

        private static float QueryPointToSegDistSq(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float lenSq = ab.LengthSquared();
            if (lenSq < 1e-8f) return p.DistanceSquaredTo(a);
            float t = Mathf.Clamp((p - a).Dot(ab) / lenSq, 0f, 1f);
            return p.DistanceSquaredTo(a + ab * t);
        }

        private static AnatomicalRegion InferStrictLimbRegion(Vector3 queryPoint, List<BoneCapsuleSegment> capsules, AnatomicalRegion[] capsuleRegions)
        {
            if (capsules == null || capsules.Count == 0 || capsuleRegions == null || capsuleRegions.Length < capsules.Count)
                return AnatomicalRegion.Unassigned;

            float minTorsoSq = float.MaxValue;
            float minArmLSq = float.MaxValue;
            float minArmRSq = float.MaxValue;
            float minLegLSq = float.MaxValue;
            float minLegRSq = float.MaxValue;

            for (int c = 0; c < capsules.Count; c++)
            {
                var cap = capsules[c];
                float dSq = QueryPointToSegDistSq(queryPoint, cap.StartPos, cap.EndPos);
                switch (capsuleRegions[c])
                {
                    case AnatomicalRegion.TorsoHead: if (dSq < minTorsoSq) minTorsoSq = dSq; break;
                    case AnatomicalRegion.ArmL:      if (dSq < minArmLSq)  minArmLSq = dSq;  break;
                    case AnatomicalRegion.ArmR:      if (dSq < minArmRSq)  minArmRSq = dSq;  break;
                    case AnatomicalRegion.LegL:      if (dSq < minLegLSq)  minLegLSq = dSq;  break;
                    case AnatomicalRegion.LegR:      if (dSq < minLegRSq)  minLegRSq = dSq;  break;
                }
            }

            float minTorso = minTorsoSq < float.MaxValue ? MathF.Sqrt(minTorsoSq) : float.MaxValue;
            float minArmL  = minArmLSq  < float.MaxValue ? MathF.Sqrt(minArmLSq)  : float.MaxValue;
            float minArmR  = minArmRSq  < float.MaxValue ? MathF.Sqrt(minArmRSq)  : float.MaxValue;
            float minLegL  = minLegLSq  < float.MaxValue ? MathF.Sqrt(minLegLSq)  : float.MaxValue;
            float minLegR  = minLegRSq  < float.MaxValue ? MathF.Sqrt(minLegRSq)  : float.MaxValue;

            // Require clear limb proximity (< 30cm) and a 3cm safety lead over competing regions so
            // shoulder/hip junction vertices continue to blend freely while hanging forearms and bulky
            // gauntlets stay isolated from adjacent trenchcoats or thigh surfaces.
            const float margin = 0.03f;
            const float maxClearDist = 0.30f;
            if (minArmL < maxClearDist && minArmL + margin < minTorso && minArmL + margin < minLegL && minArmL + margin < minArmR)
                return AnatomicalRegion.ArmL;
            if (minArmR < maxClearDist && minArmR + margin < minTorso && minArmR + margin < minLegR && minArmR + margin < minArmL)
                return AnatomicalRegion.ArmR;
            if (minLegL < maxClearDist && minLegL + margin < minTorso && minLegL + margin < minArmL && minLegL + margin < minLegR)
                return AnatomicalRegion.LegL;
            if (minLegR < maxClearDist && minLegR + margin < minTorso && minLegR + margin < minArmR && minLegR + margin < minLegL)
                return AnatomicalRegion.LegR;

            return AnatomicalRegion.Unassigned;
        }

        private static void QueryWeightsWithCapsuleFallback(
            Vector3 queryPoint,
            Dictionary<Vector3I, List<int>> grid,
            List<DonorVertex> donors,
            List<BoneCapsuleSegment> capsules,
            AnatomicalRegion[] capsuleRegions,
            int kNearest,
            float searchRadius,
            float cellSize,
            int[] outIndices,
            float[] outWeights,
            int outOffset)
        {
            float r = searchRadius;
            AnatomicalRegion strictRegion = InferStrictLimbRegion(queryPoint, capsules, capsuleRegions);

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
                    var donor = donors[dIdx];
                    if (strictRegion != AnatomicalRegion.Unassigned && donor.Region != 0 && donor.Region != (byte)strictRegion)
                        continue;

                    var dPos = donor.PosedPosition;
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
                                    var donor = donors[dIdx];
                                    if (strictRegion != AnatomicalRegion.Unassigned && donor.Region != 0 && donor.Region != (byte)strictRegion)
                                        continue;

                                    var dPos = donor.PosedPosition;
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
                bool hasStrictCapsules = false;
                if (strictRegion != AnatomicalRegion.Unassigned && capsuleRegions != null && capsuleRegions.Length >= capCount)
                {
                    for (int ci = 0; ci < capCount; ci++)
                    {
                        if (capsuleRegions[ci] == strictRegion)
                        {
                            hasStrictCapsules = true;
                            break;
                        }
                    }
                }
                for (int ci = 0; ci < capCount; ci++)
                {
                    var seg = capsules[ci];
                    if (hasStrictCapsules && capsuleRegions[ci] != strictRegion) continue;
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
                if (IsExcludedClothOrAccessoryBone(bName) || bName.Contains("twist"))
                {
                    continue;
                }

                // Filter to core anatomical bones only (reduces from 270 bones to ~16, eliminating millions of distance checks)
                bool isCoreAnatomical = bName.Contains("pelvis") || bName.Contains("spine") || bName.Contains("chest") ||
                                       bName.Contains("neck") || bName.Contains("head") ||
                                       bName.Contains("arm_upper") || bName.Contains("arm_lower") || bName == "hand_l" || bName == "hand_r" ||
                                       bName.Contains("leg_upper") || bName.Contains("leg_lower") || bName.Contains("ankle") ||
                                       bName.Contains("thigh") || bName.Contains("calf") || bName == "foot_l" || bName == "foot_r";

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

        /// <summary>
        /// Stabilizes neck and head bone weights against floating-head stretch (e.g. Paradox/Chrono)
        /// where neck_0 and head translate upward away from spine_3 during jumps and traversal animations.
        /// Anchors upper chest/collar to spine_3 and blends head/cervical rotation onto spine_3 (75%) and neck_0 (25%),
        /// keeping chin-to-collar stretch strictly <= 1.10x without preview-only position clamping.
        /// Operates strictly in Skeleton3D bone index space BEFORE bone indices are remapped to Skin bind indices.
        /// </summary>
        public static void StabilizeNeckFloatingHeadVertex(
            Span<int> bones,
            Span<float> weights,
            int headBone,
            int neckBone,
            int spine3Bone,
            int spine2Bone = -1,
            float heroY = 0f,
            float throatYThreshold = 1.95f)
        {
            if (headBone < 0 || spine3Bone < 0) return;

            float headW = 0f;
            float neckW = 0f;
            float spine2W = 0f;
            for (int k = 0; k < 4; k++)
            {
                if (weights[k] <= 0.0001f) continue;
                if (bones[k] == headBone) headW += weights[k];
                else if (neckBone >= 0 && bones[k] == neckBone) neckW += weights[k];
                else if (spine2Bone >= 0 && bones[k] == spine2Bone) spine2W += weights[k];
            }

            bool isCervicalOrHead = headW > 0.0001f || neckW > 0.0001f;
            bool isUpperThroatSpine2 = spine2W > 0.0001f && (heroY >= throatYThreshold || isCervicalOrHead);

            if (!isCervicalOrHead && !isUpperThroatSpine2) return;

            // Anchor upper neck/head: 92% spine_3, 8% neck_0 (or 100% spine_3 if neckBone < 0)
            // Anchor lower neck: 95% spine_3, 5% neck_0
            // Transfer spine_2 influence on throat/collarline to spine_3 to eliminate accordion expansion
            float spine3Add = (headW * 0.92f) + (neckW * 0.95f) + (isUpperThroatSpine2 ? spine2W : 0f);
            float neckAdd = (neckBone >= 0) ? ((headW * 0.08f) + (neckW * 0.05f)) : 0f;
            if (neckBone < 0) spine3Add += neckAdd;

            Span<int> uBones = stackalloc int[6];
            Span<float> uWeights = stackalloc float[6];
            int uCount = 0;

            // Collect other unaffected influences
            for (int k = 0; k < 4; k++)
            {
                int b = bones[k];
                float w = weights[k];
                if (w <= 0.0001f) continue;
                if (b == headBone || (neckBone >= 0 && b == neckBone)) continue;
                if (isUpperThroatSpine2 && b == spine2Bone) continue;

                int found = -1;
                for (int u = 0; u < uCount; u++)
                {
                    if (uBones[u] == b) { found = u; break; }
                }
                if (found != -1) uWeights[found] += w;
                else if (uCount < 6) { uBones[uCount] = b; uWeights[uCount] = w; uCount++; }
            }

            // Add spine_3 influence
            if (spine3Add > 0.0001f && spine3Bone >= 0)
            {
                int found = -1;
                for (int u = 0; u < uCount; u++)
                {
                    if (uBones[u] == spine3Bone) { found = u; break; }
                }
                if (found != -1) uWeights[found] += spine3Add;
                else if (uCount < 6) { uBones[uCount] = spine3Bone; uWeights[uCount] = spine3Add; uCount++; }
            }

            // Add neck_0 influence
            if (neckAdd > 0.0001f && neckBone >= 0)
            {
                int found = -1;
                for (int u = 0; u < uCount; u++)
                {
                    if (uBones[u] == neckBone) { found = u; break; }
                }
                if (found != -1) uWeights[found] += neckAdd;
                else if (uCount < 6) { uBones[uCount] = neckBone; uWeights[uCount] = neckAdd; uCount++; }
            }

            // Sort top 4 influences descending by weight and normalize kept 4 to sum to 1.00000
            for (int k = 0; k < 4; k++)
            {
                int best = -1;
                float maxW = 0f;
                for (int u = 0; u < uCount; u++)
                {
                    if (uWeights[u] > maxW)
                    {
                        maxW = uWeights[u];
                        best = u;
                    }
                }
                if (best != -1 && maxW > 0.0001f)
                {
                    bones[k] = uBones[best];
                    weights[k] = maxW;
                    uWeights[best] = -1f;
                }
                else
                {
                    bones[k] = 0;
                    weights[k] = 0f;
                }
            }

            float keptSum = weights[0] + weights[1] + weights[2] + weights[3];
            if (keptSum > 0.0001f)
            {
                float invKept = 1.0f / keptSum;
                weights[0] *= invKept;
                weights[1] *= invKept;
                weights[2] *= invKept;
                weights[3] *= invKept;
            }
            else if (spine3Bone >= 0)
            {
                bones[0] = spine3Bone;
                weights[0] = 1.0f;
            }
        }

        public static void StabilizeNeckFloatingHeadWeights(
            Skeleton3D skeleton,
            int vCount,
            int[] boneIndices,
            float[] boneWeights,
            Vector3[] verts = null)
        {
            if (skeleton == null || boneIndices == null || boneWeights == null || vCount <= 0) return;
            int headBone = skeleton.FindBone("head");
            int neckBone = skeleton.FindBone("neck_0");
            if (neckBone < 0) neckBone = skeleton.FindBone("neck");
            int spine3Bone = skeleton.FindBone("spine_3");
            if (spine3Bone < 0) spine3Bone = skeleton.FindBone("spine_2");
            int spine2Bone = skeleton.FindBone("spine_2");
            if (headBone < 0 || spine3Bone < 0) return;

            for (int v = 0; v < vCount; v++)
            {
                int baseIdx = v * 4;
                float y = verts != null && v < verts.Length ? verts[v].Y : 0f;
                StabilizeNeckFloatingHeadVertex(
                    boneIndices.AsSpan(baseIdx, 4),
                    boneWeights.AsSpan(baseIdx, 4),
                    headBone,
                    neckBone,
                    spine3Bone,
                    spine2Bone,
                    y);
            }
        }

        /// <summary>
        /// Rest-pose limb segment and procedural counter-twist bone descriptors for a hero skeleton.
        /// </summary>
        public sealed class LimbTwistChain
        {
            public string Label { get; init; }
            public bool IsLeg { get; init; }
            public int UpperBone { get; init; }
            public int TwistFullBone { get; init; }
            public int TwistHalfBone { get; init; }
            public int LowerBone { get; init; }
            public int DistalBone { get; init; }
            public Vector3 UpperOrigin { get; init; }
            public Vector3 LowerOrigin { get; init; }
            public Vector3 DistalOrigin { get; init; }
            public Vector3 UpperDir { get; init; }
            public float UpperLen { get; init; }
            public Vector3 LowerDir { get; init; }
            public float LowerLen { get; init; }
            public float MaxRadius { get; init; }
        }

        /// <summary>
        /// Resolved thigh and upper-arm counter-twist chains on a Deadlock hero skeleton.
        /// </summary>
        public sealed class LimbTwistProfile
        {
            public int PelvisBone { get; init; }
            public LimbTwistChain[] Chains { get; init; } = Array.Empty<LimbTwistChain>();
        }

        private readonly struct LimbTwistChainSpec
        {
            public LimbTwistChainSpec(string label, bool isLeg, string[] upper, string[] twistFull, string[] twistHalf, string[] lower, string[] distal)
            {
                Label = label;
                IsLeg = isLeg;
                Upper = upper;
                TwistFull = twistFull;
                TwistHalf = twistHalf;
                Lower = lower;
                Distal = distal;
            }

            public string Label { get; }
            public bool IsLeg { get; }
            public string[] Upper { get; }
            public string[] TwistFull { get; }
            public string[] TwistHalf { get; }
            public string[] Lower { get; }
            public string[] Distal { get; }
        }

        private static readonly LimbTwistChainSpec[] DefaultLimbTwistSpecs =
        {
            new(
                "LeftThigh",
                true,
                new[] { "leg_upper_L", "leg_upper_l", "thigh_L", "thigh_l" },
                new[] { "leg_upper_L_TWIST", "leg_upper_l_twist", "leg_upper_twist_L" },
                new[] { "leg_upper_L_TWIST1", "leg_upper_l_twist1", "leg_upper_twist1_L" },
                new[] { "leg_lower_L", "leg_lower_l", "calf_L", "knee_L" },
                new[] { "ankle_L", "ankle_l", "foot_L", "foot_l" }),
            new(
                "RightThigh",
                true,
                new[] { "leg_upper_R", "leg_upper_r", "thigh_R", "thigh_r" },
                new[] { "leg_upper_R_TWIST", "leg_upper_r_twist", "leg_upper_twist_R" },
                new[] { "leg_upper_R_TWIST1", "leg_upper_r_twist1", "leg_upper_twist1_R" },
                new[] { "leg_lower_R", "leg_lower_r", "calf_R", "knee_R" },
                new[] { "ankle_R", "ankle_r", "foot_R", "foot_r" }),
            new(
                "LeftUpperArm",
                false,
                new[] { "arm_upper_L", "arm_upper_l", "upperarm_l", "leftarm" },
                new[] { "arm_upper_L_TWIST", "arm_upper_l_twist", "arm_upper_twist_L" },
                new[] { "arm_upper_L_TWIST1", "arm_upper_l_twist1", "arm_upper_twist1_L" },
                new[] { "arm_lower_L", "arm_lower_l", "forearm_l" },
                new[] { "hand_L", "hand_l", "wrist_l" }),
            new(
                "RightUpperArm",
                false,
                new[] { "arm_upper_R", "arm_upper_r", "upperarm_r", "rightarm" },
                new[] { "arm_upper_R_TWIST", "arm_upper_r_twist", "arm_upper_twist_R" },
                new[] { "arm_upper_R_TWIST1", "arm_upper_r_twist1", "arm_upper_twist1_R" },
                new[] { "arm_lower_R", "arm_lower_r", "forearm_r" },
                new[] { "hand_R", "hand_r", "wrist_r" })
        };

        /// <summary>
        /// Resolves upper-limb joints and existing procedural *_TWIST / *_TWIST1 bones on the hero skeleton.
        /// Works generically across any Deadlock hero skeleton without hardcoded bone indices.
        /// </summary>
        public static LimbTwistProfile BuildLimbTwistProfile(Skeleton3D skeleton, Transform3D[] restGlobalTransforms = null)
        {
            if (skeleton == null) return new LimbTwistProfile();

            int pelvisBone = FindBoneAny(skeleton, "pelvis", "hips", "bip01_pelvis");
            var chains = new List<LimbTwistChain>(DefaultLimbTwistSpecs.Length);

            Vector3 GetBoneOrigin(int boneIdx)
            {
                if (restGlobalTransforms != null && boneIdx >= 0 && boneIdx < restGlobalTransforms.Length)
                    return restGlobalTransforms[boneIdx].Origin;
                return skeleton.GetBoneGlobalRest(boneIdx).Origin;
            }

            foreach (var spec in DefaultLimbTwistSpecs)
            {
                int upper = FindBoneAny(skeleton, spec.Upper);
                int lower = FindBoneAny(skeleton, spec.Lower);
                if (upper < 0 || lower < 0) continue;

                int distal = FindBoneAny(skeleton, spec.Distal);
                int twistFull = FindBoneAny(skeleton, spec.TwistFull);
                int twistHalf = FindBoneAny(skeleton, spec.TwistHalf);

                Vector3 pUpper = GetBoneOrigin(upper);
                Vector3 pLower = GetBoneOrigin(lower);
                Vector3 pDistal = distal >= 0 ? GetBoneOrigin(distal) : pLower;

                Vector3 upperVec = pLower - pUpper;
                float upperLen = upperVec.Length();
                if (upperLen < 0.05f) continue;

                Vector3 lowerVec = pDistal - pLower;
                float lowerLen = lowerVec.Length();

                chains.Add(new LimbTwistChain
                {
                    Label = spec.Label,
                    IsLeg = spec.IsLeg,
                    UpperBone = upper,
                    TwistFullBone = twistFull,
                    TwistHalfBone = twistHalf,
                    LowerBone = lower,
                    DistalBone = distal,
                    UpperOrigin = pUpper,
                    LowerOrigin = pLower,
                    DistalOrigin = pDistal,
                    UpperDir = upperVec / upperLen,
                    UpperLen = upperLen,
                    LowerDir = lowerLen > 0.05f ? lowerVec / lowerLen : upperVec / upperLen,
                    LowerLen = lowerLen,
                    MaxRadius = spec.IsLeg ? MathF.Max(0.28f, upperLen * 0.95f) : MathF.Max(0.20f, upperLen * 0.85f)
                });
            }

            return new LimbTwistProfile
            {
                PelvisBone = pelvisBone,
                Chains = chains.ToArray()
            };
        }

        private static float SmoothStep01(float edge0, float edge1, float x)
        {
            if (edge1 <= edge0) return x >= edge1 ? 1.0f : 0.0f;
            float u = Mathf.Clamp((x - edge0) / (edge1 - edge0), 0.0f, 1.0f);
            return u * u * (3.0f - 2.0f * u);
        }

        /// <summary>
        /// Automatically redistributes proximal thigh and upper-arm weights onto existing Source 2
        /// counter-twist bones (*_TWIST / *_TWIST1), softens razor-sharp 1-bone hip seams for LBS volume
        /// preservation, and prunes stray distal ankle/hand bleeds above the knee/elbow.
        /// Operates strictly in Skeleton3D bone index space using hero rest-pose vertex coordinates.
        /// </summary>
        public static void AdjustLimbTwistAndHipWeights(
            Skeleton3D skeleton,
            int vCount,
            int[] boneIndices,
            float[] boneWeights,
            Vector3[] heroRestVerts,
            int[] triangleIndices = null,
            LimbTwistProfile profile = null)
        {
            if (skeleton == null || boneIndices == null || boneWeights == null || heroRestVerts == null || vCount <= 0)
                return;

            profile ??= BuildLimbTwistProfile(skeleton);
            if (profile.Chains == null || profile.Chains.Length == 0)
                return;

            bool[] creaseSeamMask = new bool[vCount];
            int seamCount = 0;

            System.Threading.Tasks.Parallel.For(0, vCount, v =>
            {
                int baseIdx = v * 4;
                if (AdjustSingleVertexLimbWeights(
                    heroRestVerts[v],
                    boneIndices.AsSpan(baseIdx, 4),
                    boneWeights.AsSpan(baseIdx, 4),
                    profile))
                {
                    creaseSeamMask[v] = true;
                    System.Threading.Interlocked.Increment(ref seamCount);
                }
            });

            // Feather hip & shoulder crease seams across neighboring triangles and weld coincident UV/outline vertices
            if (seamCount > 0 && triangleIndices != null && triangleIndices.Length >= 3)
            {
                SmoothWeightsLaplacian(triangleIndices, vCount, boneIndices, boneWeights, 2, heroRestVerts, creaseSeamMask);

                // Re-enforce cross-leg bleed & distal thigh pelvis caps on smoothed crease vertices
                // so Laplacian neighbor averaging across the crotch/hip seam does not re-introduce minor opposite-leg
                // weights or drag pelvis weight down below the hip crease.
                System.Threading.Tasks.Parallel.For(0, vCount, v =>
                {
                    if (!creaseSeamMask[v]) return;
                    int baseIdx = v * 4;
                    PostSmoothLegCleanup(
                        heroRestVerts[v],
                        boneIndices.AsSpan(baseIdx, 4),
                        boneWeights.AsSpan(baseIdx, 4),
                        profile);
                });
            }
        }

        private static bool CleanupLegPelvisAndCrossBleed(
            LimbTwistProfile profile,
            in LimbTwistChain chain,
            float tUpper,
            float rPerp,
            Span<int> uBones,
            Span<float> uWeights,
            ref int uCount,
            ref float upperW,
            int absorbBoneOverride = -1)
        {
            bool changed = false;
            int absorbBone = absorbBoneOverride >= 0 ? absorbBoneOverride : chain.UpperBone;

            if (profile.PelvisBone >= 0 && rPerp <= 0.32f)
            {
                float pelvisW = GetLimbWeight(uBones, uWeights, uCount, profile.PelvisBone);
                float maxPelvis = 0.42f * (1.0f - SmoothStep01(-0.02f, 0.14f, tUpper));
                if (tUpper >= 0.00f && tUpper <= 0.50f && (upperW + pelvisW) >= 0.40f && upperW >= 0.08f && pelvisW > maxPelvis)
                {
                    float cappedPelvis = maxPelvis > 0.005f ? maxPelvis : 0f;
                    float excess = pelvisW - cappedPelvis;
                    SubtractLimbWeight(uBones, uWeights, ref uCount, profile.PelvisBone);
                    if (cappedPelvis > 0f)
                        AccumulateLimbWeight(uBones, uWeights, ref uCount, profile.PelvisBone, cappedPelvis);
                    if (absorbBoneOverride >= 0)
                        AccumulateLimbWeight(uBones, uWeights, ref uCount, absorbBone, excess);
                    upperW += excess;
                    changed = true;
                }
                else if (absorbBoneOverride < 0 && tUpper >= -0.10f && tUpper <= 0.05f && upperW >= 0.45f && pelvisW < 0.24f)
                {
                    float uHip = 1.0f - SmoothStep01(-0.10f, 0.05f, tUpper);
                    float uRad = 1.0f - SmoothStep01(0.14f, 0.32f, rPerp);
                    float desiredPelvis = 0.24f * uHip * (0.4f + 0.6f * uRad);
                    float transfer = MathF.Min(MathF.Max(0f, desiredPelvis - pelvisW), upperW * 0.40f);
                    if (transfer > 0.005f)
                    {
                        AccumulateLimbWeight(uBones, uWeights, ref uCount, profile.PelvisBone, transfer);
                        upperW -= transfer;
                        changed = true;
                    }
                }
            }

            if (tUpper >= 0.01f && tUpper <= 1.05f && rPerp <= 0.18f && upperW >= 0.18f)
            {
                for (int ci = 0; ci < profile.Chains.Length; ci++)
                {
                    var other = profile.Chains[ci];
                    if (!other.IsLeg || other.UpperBone == chain.UpperBone)
                        continue;

                    float oppW = GetLimbWeight(uBones, uWeights, uCount, other.UpperBone)
                               + GetLimbWeight(uBones, uWeights, uCount, other.TwistFullBone)
                               + GetLimbWeight(uBones, uWeights, uCount, other.TwistHalfBone)
                               + GetLimbWeight(uBones, uWeights, uCount, other.LowerBone);

                    if (oppW > 0.001f && oppW < 0.24f && upperW >= oppW * 2.2f)
                    {
                        SubtractLimbWeight(uBones, uWeights, ref uCount, other.UpperBone);
                        SubtractLimbWeight(uBones, uWeights, ref uCount, other.TwistFullBone);
                        SubtractLimbWeight(uBones, uWeights, ref uCount, other.TwistHalfBone);
                        SubtractLimbWeight(uBones, uWeights, ref uCount, other.LowerBone);
                        if (absorbBoneOverride >= 0)
                            AccumulateLimbWeight(uBones, uWeights, ref uCount, absorbBone, oppW);
                        upperW += oppW;
                        changed = true;
                    }
                }
            }

            return changed;
        }

        private static void PostSmoothLegCleanup(
            Vector3 vPos,
            Span<int> bones,
            Span<float> weights,
            LimbTwistProfile profile)
        {
            Span<int> uBones = stackalloc int[12];
            Span<float> uWeights = stackalloc float[12];
            int uCount = 0;
            for (int k = 0; k < 4; k++)
            {
                if (weights[k] > 0.0001f)
                    AccumulateLimbWeight(uBones, uWeights, ref uCount, bones[k], weights[k]);
            }
            if (uCount == 0) return;

            bool touched = false;
            for (int c = 0; c < profile.Chains.Length; c++)
            {
                var chain = profile.Chains[c];
                if (!chain.IsLeg || chain.UpperLen <= 0.05f) continue;

                float wUpper = GetLimbWeight(uBones, uWeights, uCount, chain.UpperBone);
                float wFull = GetLimbWeight(uBones, uWeights, uCount, chain.TwistFullBone);
                float wHalf = GetLimbWeight(uBones, uWeights, uCount, chain.TwistHalfBone);
                float upperW = wUpper + wFull + wHalf;
                if (upperW <= 0.02f) continue;

                Vector3 relU = vPos - chain.UpperOrigin;
                float projU = relU.Dot(chain.UpperDir);
                float tUpper = projU / chain.UpperLen;
                float rPerp = (relU - chain.UpperDir * projU).Length();
                if (tUpper < 0.00f || tUpper > 1.05f || rPerp > chain.MaxRadius) continue;

                int dominantUpper = chain.UpperBone;
                float bestU = wUpper;
                if (wHalf > bestU && chain.TwistHalfBone >= 0) { bestU = wHalf; dominantUpper = chain.TwistHalfBone; }
                if (wFull > bestU && chain.TwistFullBone >= 0) { bestU = wFull; dominantUpper = chain.TwistFullBone; }

                if (CleanupLegPelvisAndCrossBleed(profile, in chain, tUpper, rPerp, uBones, uWeights, ref uCount, ref upperW, dominantUpper))
                    touched = true;
            }

            if (touched)
                WriteTop4NormalizedWeights(uBones, uWeights, uCount, bones, weights);
        }

        private static bool AdjustSingleVertexLimbWeights(
            Vector3 vPos,
            Span<int> bones,
            Span<float> weights,
            LimbTwistProfile profile)
        {
            Span<int> uBones = stackalloc int[12];
            Span<float> uWeights = stackalloc float[12];
            int uCount = 0;

            for (int k = 0; k < 4; k++)
            {
                float w = weights[k];
                if (w <= 0.0001f) continue;
                AccumulateLimbWeight(uBones, uWeights, ref uCount, bones[k], w);
            }

            if (uCount == 0) return false;

            bool touched = false;
            bool inCreaseSeam = false;

            foreach (var chain in profile.Chains)
            {
                // 1. Project vertex onto this limb's upper segment (hip->knee or shoulder->elbow)
                Vector3 dUpper = vPos - chain.UpperOrigin;
                float projUpper = dUpper.Dot(chain.UpperDir);
                float tUpper = projUpper / chain.UpperLen;
                float rPerp = (dUpper - chain.UpperDir * projUpper).Length();

                // 2. Gather existing weights belonging to this upper chain, lower bone, and distal bone
                float upperW = 0f;
                float lowerW = 0f;
                float distalW = 0f;
                for (int i = 0; i < uCount; i++)
                {
                    int b = uBones[i];
                    if (b == chain.UpperBone ||
                        (chain.TwistFullBone >= 0 && b == chain.TwistFullBone) ||
                        (chain.TwistHalfBone >= 0 && b == chain.TwistHalfBone))
                    {
                        upperW += uWeights[i];
                    }
                    else if (b == chain.LowerBone)
                    {
                        lowerW += uWeights[i];
                    }
                    else if (chain.DistalBone >= 0 && b == chain.DistalBone)
                    {
                        distalW += uWeights[i];
                    }
                }

                // 3. Prune stray distal ankle/hand bleeds appearing on the thigh, knee, or upper forearm
                if (distalW > 0.0001f && distalW < 0.25f && chain.DistalBone >= 0 && chain.LowerLen > 0.05f && (upperW + lowerW) >= 0.30f)
                {
                    float tLower = (vPos - chain.LowerOrigin).Dot(chain.LowerDir) / chain.LowerLen;
                    if (tLower < 0.55f)
                    {
                        SubtractLimbWeight(uBones, uWeights, ref uCount, chain.DistalBone);
                        int receiver = (tLower < 0.15f && upperW >= lowerW) ? chain.UpperBone : chain.LowerBone;
                        AccumulateLimbWeight(uBones, uWeights, ref uCount, receiver, distalW);
                        if (receiver == chain.UpperBone) upperW += distalW;
                        else lowerW += distalW;
                        touched = true;
                    }
                }

                // Skip twist / hip-seam reallocation if vertex has minimal upper-limb weight or sits outside this limb cylinder
                if (upperW <= 0.02f || tUpper < -0.20f || tUpper > 1.15f || rPerp > chain.MaxRadius)
                    continue;

                // 4. Leg hip-seam pelvis balancing & cross-leg bleed cleanup:
                //    - Cap excess Rigify/Mixamo pelvis bleed down the thigh (which collapses volume in Linear Blend Skinning under deep hip flexion)
                //    - Soften razor-sharp 1-bone hip sockets right at the crease (tUpper in [-0.10, +0.05])
                //    - Prune minor opposite-leg bleed on inner thighs (evaluated AFTER pelvis cap so raised upperW qualifies)
                if (chain.IsLeg)
                {
                    if (CleanupLegPelvisAndCrossBleed(profile, in chain, tUpper, rPerp, uBones, uWeights, ref uCount, ref upperW))
                        touched = true;

                    // 4b. Center knee crease envelope closer to Deadlock's native joint pivot (tUpper ~ 0.98)
                    //     so shin vertices below the knee don't hold heavy upper-thigh weight and fold inward.
                    if (chain.LowerBone >= 0 && tUpper >= 0.90f && tUpper <= 1.12f)
                    {
                        float kneeLowerW = lowerW;
                        float totalKnee = upperW + kneeLowerW;
                        if (kneeLowerW > 0.005f && totalKnee >= 0.65f)
                        {
                            float desiredUpperFrac = 1.0f - SmoothStep01(0.90f, 1.06f, tUpper);
                            float targetU = totalKnee * desiredUpperFrac;
                            float blendedU = upperW * 0.35f + targetU * 0.65f;
                            float blendedL = totalKnee - blendedU;
                            if (MathF.Abs(blendedU - upperW) > 0.01f)
                            {
                                SubtractLimbWeight(uBones, uWeights, ref uCount, chain.LowerBone);
                                if (blendedL > 0.005f)
                                {
                                    AccumulateLimbWeight(uBones, uWeights, ref uCount, chain.LowerBone, blendedL);
                                    upperW = blendedU;
                                    lowerW = blendedL;
                                }
                                else
                                {
                                    upperW = totalKnee;
                                    lowerW = 0f;
                                }
                                touched = true;
                            }
                        }
                    }
                }

                // Flag proximal hip / shoulder / knee crease vertices for targeted triangle-seam feathering
                if ((tUpper >= -0.10f && tUpper <= (chain.IsLeg ? 0.12f : 0.20f)) ||
                    (chain.IsLeg && tUpper >= 0.90f && tUpper <= 1.10f))
                {
                    inCreaseSeam = true;
                }

                // 5. Redistribute upper-limb weight onto procedural *_TWIST (-1.0) and *_TWIST1 (-0.5) bones
                bool hasTwistFull = chain.TwistFullBone >= 0;
                bool hasTwistHalf = chain.TwistHalfBone >= 0;
                if (!hasTwistFull && !hasTwistHalf)
                    continue;

                SubtractLimbWeight(uBones, uWeights, ref uCount, chain.UpperBone);
                if (hasTwistFull) SubtractLimbWeight(uBones, uWeights, ref uCount, chain.TwistFullBone);
                if (hasTwistHalf) SubtractLimbWeight(uBones, uWeights, ref uCount, chain.TwistHalfBone);

                float fFull, fHalf, fUpper;
                if (hasTwistFull && hasTwistHalf)
                {
                    // Smooth partition of unity along tUpper in [0, 1] matching Deadlock hero thighs:
                    // 0.00..0.14: ~100% TWIST (-1.0) -> ~0.52: ~95% TWIST1 (-0.5) -> >=0.92: 100% UpperBone (0.0)
                    fFull = 1.0f - SmoothStep01(0.14f, 0.52f, tUpper);
                    fUpper = SmoothStep01(0.52f, 0.92f, tUpper);
                    fHalf = MathF.Max(0f, 1.0f - fFull - fUpper);
                }
                else if (hasTwistFull)
                {
                    fFull = (1.0f - SmoothStep01(0.08f, 0.75f, tUpper)) * 0.90f;
                    fHalf = 0f;
                    fUpper = 1.0f - fFull;
                }
                else
                {
                    fFull = 0f;
                    fHalf = (1.0f - SmoothStep01(0.15f, 0.75f, tUpper)) * 0.90f;
                    fUpper = 1.0f - fHalf;
                }

                if (hasTwistFull && fFull * upperW > 0.005f)
                    AccumulateLimbWeight(uBones, uWeights, ref uCount, chain.TwistFullBone, upperW * fFull);
                if (hasTwistHalf && fHalf * upperW > 0.005f)
                    AccumulateLimbWeight(uBones, uWeights, ref uCount, chain.TwistHalfBone, upperW * fHalf);
                if (fUpper * upperW > 0.005f)
                    AccumulateLimbWeight(uBones, uWeights, ref uCount, chain.UpperBone, upperW * fUpper);
                else if ((!hasTwistFull || fFull * upperW <= 0.005f) && (!hasTwistHalf || fHalf * upperW <= 0.005f))
                    AccumulateLimbWeight(uBones, uWeights, ref uCount, chain.UpperBone, upperW);

                touched = true;
            }

            if (touched)
            {
                WriteTop4NormalizedWeights(uBones, uWeights, uCount, bones, weights);
            }

            return inCreaseSeam;
        }

        private static void AccumulateLimbWeight(Span<int> uBones, Span<float> uWeights, ref int uCount, int bone, float weight)
        {
            if (weight <= 0.0001f || bone < 0) return;
            for (int i = 0; i < uCount; i++)
            {
                if (uBones[i] == bone)
                {
                    uWeights[i] += weight;
                    return;
                }
            }
            if (uCount < uBones.Length)
            {
                uBones[uCount] = bone;
                uWeights[uCount] = weight;
                uCount++;
            }
        }

        private static float GetLimbWeight(Span<int> uBones, Span<float> uWeights, int uCount, int bone)
        {
            for (int i = 0; i < uCount; i++)
            {
                if (uBones[i] == bone) return uWeights[i];
            }
            return 0f;
        }

        private static void SubtractLimbWeight(Span<int> uBones, Span<float> uWeights, ref int uCount, int bone)
        {
            for (int i = 0; i < uCount; i++)
            {
                if (uBones[i] == bone)
                {
                    uWeights[i] = 0f;
                    return;
                }
            }
        }

        private static void WriteTop4NormalizedWeights(
            Span<int> uBones,
            Span<float> uWeights,
            int uCount,
            Span<int> outBones,
            Span<float> outWeights)
        {
            for (int k = 0; k < 4; k++)
            {
                int best = -1;
                float maxW = 0f;
                for (int u = 0; u < uCount; u++)
                {
                    if (uWeights[u] > maxW)
                    {
                        maxW = uWeights[u];
                        best = u;
                    }
                }
                if (best != -1 && maxW > 0.0001f)
                {
                    outBones[k] = uBones[best];
                    outWeights[k] = maxW;
                    uWeights[best] = -1f;
                }
                else
                {
                    outBones[k] = 0;
                    outWeights[k] = 0f;
                }
            }

            float keptSum = outWeights[0] + outWeights[1] + outWeights[2] + outWeights[3];
            if (keptSum > 0.0001f)
            {
                float invKept = 1.0f / keptSum;
                for (int k = 0; k < 4; k++)
                {
                    outWeights[k] *= invKept;
                }
            }
            else
            {
                outBones[0] = 0;
                outWeights[0] = 1.0f;
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

        public static (Dictionary<int, int> skelToBind, Dictionary<int, int> bindToSkel) BuildSkinIndexMappings(Skeleton3D skeleton, Skin skin)
        {
            var skelToBind = new Dictionary<int, int>();
            var bindToSkel = new Dictionary<int, int>();
            if (skeleton == null) return (skelToBind, bindToSkel);

            int boneCount = skeleton.GetBoneCount();

            if (skin != null)
            {
                for (int b = 0; b < boneCount; b++)
                {
                    int bindIdx = GetOrAddSkinBindIndex(skin, skeleton, b);
                    skelToBind[b] = bindIdx;
                    bindToSkel[bindIdx] = b;
                }
            }
            else
            {
                for (int b = 0; b < boneCount; b++)
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
            var usedTargets = new HashSet<int>();

            // 1. Apply user custom overrides if supplied
            if (userCustomRemap != null && userCustomRemap.Count > 0)
            {
                foreach (var (tName, sName) in userCustomRemap)
                {
                    if (string.IsNullOrWhiteSpace(sName) || string.IsNullOrWhiteSpace(tName)) continue;
                    int tIdx = targetSkeleton.FindBone(tName);
                    int sIdx = sourceSkeleton.FindBone(sName);
                    if (tIdx != -1 && sIdx != -1 && !map.ContainsKey(sIdx))
                    {
                        map[sIdx] = tIdx;
                        usedTargets.Add(tIdx);
                    }
                }
            }

            // 2. Fallback via HumanoidBoneMapper.AutoMap for any unmapped targets ONLY when no explicit user map was supplied,
            //    or for unmapped source bones matching unused secondary/twist slots cleanly.
            if (userCustomRemap == null || userCustomRemap.Count == 0)
            {
                var autoMapped = HumanoidBoneMapper.AutoMap(sourceSkeleton, targetSkeleton);
                foreach (var (tName, sName) in autoMapped)
                {
                    if (string.IsNullOrWhiteSpace(sName)) continue;
                    int tIdx = targetSkeleton.FindBone(tName);
                    int sIdx = sourceSkeleton.FindBone(sName);
                    if (tIdx != -1 && sIdx != -1 && !map.ContainsKey(sIdx) && !usedTargets.Contains(tIdx))
                    {
                        map[sIdx] = tIdx;
                        usedTargets.Add(tIdx);
                    }
                }
            }

            int srcCount = sourceSkeleton.GetBoneCount();
            string[] srcRawNames = new string[srcCount];
            string[] srcCleanExact = new string[srcCount];
            string[] srcCleanNoGltf = new string[srcCount];
            bool[] srcIsSecondary = new bool[srcCount];
            for (int sIdx = 0; sIdx < srcCount; sIdx++)
            {
                string raw = sourceSkeleton.GetBoneName(sIdx);
                srcRawNames[sIdx] = raw;
                srcCleanExact[sIdx] = HumanoidBoneMapper.CleanBoneName(raw, stripTrailingGltfIndex: false);
                srcCleanNoGltf[sIdx] = HumanoidBoneMapper.CleanBoneName(raw, stripTrailingGltfIndex: true);
                srcIsSecondary[sIdx] = HumanoidBoneMapper.IsSecondaryOrHelperBone(raw, allowTwist: false);
            }

            // Precompute definition target bone indices and cleaned aliases ONCE (eliminates O(B * D) C++ FindBone lookups)
            var defs = HumanoidBoneMapper.Definitions;
            int[] defTargetIndices = new int[defs.Count];
            string[][] defCleanAliases = new string[defs.Count][];
            string[] defCanonicalLower = new string[defs.Count];
            bool[] defRespectClear = new bool[defs.Count];
            for (int d = 0; d < defs.Count; d++)
            {
                var def = defs[d];
                defRespectClear[d] = userCustomRemap != null && userCustomRemap.ContainsKey(def.CanonicalName);
                string tgtResolved = HumanoidBoneMapper.ResolveTargetBone(targetSkeleton, def);
                defTargetIndices[d] = !string.IsNullOrEmpty(tgtResolved) ? targetSkeleton.FindBone(tgtResolved) : -1;
                defCanonicalLower[d] = def.CanonicalName.ToLowerInvariant();
                if (defTargetIndices[d] != -1 && !defRespectClear[d])
                {
                    var aliases = new string[def.SourceAliases.Length];
                    for (int a = 0; a < aliases.Length; a++)
                        aliases[a] = HumanoidBoneMapper.CleanBoneName(def.SourceAliases[a], stripTrailingGltfIndex: false);
                    defCleanAliases[d] = aliases;
                }
            }

            for (int sIdx = 0; sIdx < srcCount; sIdx++)
            {
                if (map.ContainsKey(sIdx)) continue;
                if (srcIsSecondary[sIdx]) continue;
                string rawName = srcRawNames[sIdx];

                // Check exact name match first (when target bone is not already taken)
                int exact = targetSkeleton.FindBone(rawName);
                if (exact != -1 && !usedTargets.Contains(exact))
                {
                    map[sIdx] = exact;
                    usedTargets.Add(exact);
                    continue;
                }

                // Match against HumanoidBoneMapper definitions using precomputed cleaned names (never colliding used targets)
                string cleanExact = srcCleanExact[sIdx];
                string cleanNoGltf = srcCleanNoGltf[sIdx];
                bool hasLateralTag = cleanNoGltf.EndsWith("_l") || cleanNoGltf.EndsWith("_r") || cleanNoGltf.Contains("_l_") || cleanNoGltf.Contains("_r_") || cleanNoGltf.Contains("left") || cleanNoGltf.Contains("right");
                bool disallowRight = (cleanNoGltf.EndsWith("_r") || cleanNoGltf.Contains("_r_") || cleanNoGltf.Contains("right")) && !cleanNoGltf.Contains("left");
                bool disallowLeft = (cleanNoGltf.EndsWith("_l") || cleanNoGltf.Contains("_l_") || cleanNoGltf.Contains("left")) && !cleanNoGltf.Contains("right");
                bool hasForehead = cleanNoGltf.Contains("forehead");

                for (int d = 0; d < defs.Count; d++)
                {
                    int tIdx = defTargetIndices[d];
                    if (tIdx == -1 || usedTargets.Contains(tIdx) || defRespectClear[d]) continue;
                    var def = defs[d];

                    bool isCentral = def.Group == HumanoidBoneMapper.BoneGroup.Pelvis || def.Group == HumanoidBoneMapper.BoneGroup.Spine || def.Group == HumanoidBoneMapper.BoneGroup.Head || def.Group == HumanoidBoneMapper.BoneGroup.Root;
                    if (isCentral && hasLateralTag) continue;
                    if (def.CanonicalName == "head" && hasForehead) continue;

                    bool isLeft = def.Group == HumanoidBoneMapper.BoneGroup.LeftArm || def.Group == HumanoidBoneMapper.BoneGroup.LeftLeg || def.Group == HumanoidBoneMapper.BoneGroup.LeftHandFingers;
                    bool isRight = def.Group == HumanoidBoneMapper.BoneGroup.RightArm || def.Group == HumanoidBoneMapper.BoneGroup.RightLeg || def.Group == HumanoidBoneMapper.BoneGroup.RightHandFingers;
                    if (isLeft && disallowRight) continue;
                    if (isRight && disallowLeft) continue;

                    bool sourceMatch = cleanNoGltf == defCanonicalLower[d];
                    if (!sourceMatch)
                    {
                        string[] cAliases = defCleanAliases[d];
                        for (int a = 0; a < cAliases.Length; a++)
                        {
                            string cAlias = cAliases[a];
                            if (cleanExact == cAlias || cleanNoGltf == cAlias ||
                                cleanNoGltf.StartsWith(cAlias + "_") || cleanNoGltf.EndsWith("_" + cAlias) || cleanNoGltf.Contains("_" + cAlias + "_"))
                            {
                                sourceMatch = true;
                                break;
                            }
                        }
                    }

                    if (sourceMatch)
                    {
                        map[sIdx] = tIdx;
                        usedTargets.Add(tIdx);
                        break;
                    }
                }
            }

            // Check for Deadlock Forearm Twist Bones (arm_lower_L_TWIST, arm_lower_R_TWIST, etc.)
            for (int sIdx = 0; sIdx < srcCount; sIdx++)
            {
                if (map.ContainsKey(sIdx)) continue;
                string sName = srcRawNames[sIdx];
                string clean = srcCleanNoGltf[sIdx];

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
            for (int sIdx = 0; sIdx < srcCount; sIdx++)
            {
                if (map.ContainsKey(sIdx)) continue;
                string clean = srcCleanNoGltf[sIdx];

                if (clean.EndsWith("_001") || clean.EndsWith(".001") || clean.Contains("twist"))
                {
                    string baseName = clean.Replace("_001", "").Replace(".001", "").Replace("twist", "");
                    for (int otherIdx = 0; otherIdx < srcCount; otherIdx++)
                    {
                        if (map.TryGetValue(otherIdx, out int tgtMapped))
                        {
                            string otherClean = srcCleanNoGltf[otherIdx];
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

        public const string SurfaceKeepSeparator = "::surf::";

        public static string MakeSurfaceKeepKey(string meshName, int surfaceIndex)
            => $"{meshName}{SurfaceKeepSeparator}{surfaceIndex}";

        public static bool IsOriginalSurfaceKept(IEnumerable<string> keptNames, string meshName, int surfaceIndex, string generatedSubName = null)
        {
            if (keptNames == null) return false;
            string surfKey = MakeSurfaceKeepKey(meshName, surfaceIndex);
            foreach (var entry in keptNames)
            {
                if (string.IsNullOrEmpty(entry)) continue;
                if (string.Equals(entry, meshName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry, surfKey, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(generatedSubName) && string.Equals(entry, generatedSubName, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            return false;
        }

        public static StandardMaterial3D CreateHiddenSurfaceMaterial()
        {
            return new StandardMaterial3D
            {
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = new Color(0f, 0f, 0f, 0f),
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                NoDepthTest = false,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
            };
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
                bool forceHiddenByReplacement = false;

                // If explicitly replaced by a custom submesh (e.g. custom gun replacing hero gun), force hidden
                if (options.SubmeshReplacementMap != null)
                {
                    foreach (var repTarget in options.SubmeshReplacementMap.Values)
                    {
                        if (string.IsNullOrEmpty(repTarget) || repTarget == "[ None ]") continue;
                        if (rawName.Equals(repTarget, StringComparison.OrdinalIgnoreCase))
                        {
                            forceHiddenByReplacement = true;
                            break;
                        }
                        // If replacing primary gun model (e.g. _CHRONO_GUN_MODEL), also hide corresponding hip/holstered version (_CHRONO_GUN_ON_HIP)
                        string repLower = repTarget.ToLowerInvariant();
                        string rawLower = rawName.ToLowerInvariant();
                        if (repLower.Contains("gun") && rawLower.Contains("gun"))
                        {
                            if (rawLower.Contains("hip") || rawLower.Contains("holster") || rawLower.Contains("back") || rawLower.Contains("sheath"))
                            {
                                forceHiddenByReplacement = true;
                                break;
                            }
                        }
                    }
                }

                int surfCount = mi.Mesh?.GetSurfaceCount() ?? 0;

                if (mi.HasMeta("GeneratedSubmeshes"))
                {
                    // Composite mesh has already been split into per-surface MeshInstance3D siblings (_surf_s)
                    // Keep the composite container hidden to avoid double rendering, and toggle each split surface individually.
                    mi.Visible = false;
                    bool anySubKept = false;
                    var genArr = mi.GetMeta("GeneratedSubmeshes").AsGodotArray();
                    for (int gIdx = 0; gIdx < genArr.Count; gIdx++)
                    {
                        if (genArr[gIdx].AsGodotObject() is MeshInstance3D subMi && GodotObject.IsInstanceValid(subMi))
                        {
                            int sIdx = subMi.HasMeta("SurfaceIndex") ? subMi.GetMeta("SurfaceIndex").AsInt32() : gIdx;
                            bool keepSub = !forceHiddenByReplacement &&
                                           IsOriginalSurfaceKept(options.KeptOriginalSubmeshNames, rawName, sIdx, subMi.Name.ToString());
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
                        bool keepSurf = !forceHiddenByReplacement &&
                                        IsOriginalSurfaceKept(options.KeptOriginalSubmeshNames, rawName, s);
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
                            bool keepSurf = !forceHiddenByReplacement &&
                                            IsOriginalSurfaceKept(options.KeptOriginalSubmeshNames, rawName, s);
                            mi.SetMeta($"SurfaceKept_{s}", keepSurf);
                            if (keepSurf)
                            {
                                Material origMat = mi.HasMeta($"OriginalMaterial_{s}") ? mi.GetMeta($"OriginalMaterial_{s}").As<Material>() : null;
                                mi.SetSurfaceOverrideMaterial(s, origMat);
                            }
                            else
                            {
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
                                mi.SetSurfaceOverrideMaterial(s, origMat);
                            }
                        }
                    }

                    if (anyKept) mi.RemoveMeta("UserHiddenByCustomModel");
                    else mi.SetMeta("UserHiddenByCustomModel", true);
                }
                else
                {
                    bool keep = !forceHiddenByReplacement && IsOriginalSurfaceKept(options.KeptOriginalSubmeshNames, rawName, 0);
                    mi.Visible = keep;
                    mi.SetMeta("UserVisibility", keep);
                    if (keep) mi.RemoveMeta("UserHiddenByCustomModel");
                    else mi.SetMeta("UserHiddenByCustomModel", true);
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
        private static void EvaluateMeshSurfaceInTargetSpace(
            MeshInstance3D mi,
            Node3D sourceRoot,
            Transform3D prefixTransform,
            Skeleton3D fallbackSkeleton,
            Vector3[] rawVerts,
            Vector3[] rawNorms,
            int[] rawBones,
            float[] rawWeights,
            out Vector3[] outVerts,
            out Vector3[] outNorms)
        {
            int vCount = rawVerts.Length;
            outVerts = new Vector3[vCount];
            outNorms = new Vector3[vCount];

            Transform3D fallbackXform = prefixTransform * ComputeTransformRelativeToRoot(mi, sourceRoot);

            Skeleton3D skel = null;
            if (!string.IsNullOrEmpty(mi.Skeleton.ToString()))
            {
                skel = mi.GetNodeOrNull<Skeleton3D>(mi.Skeleton);
            }
            if (skel == null && mi.GetParent() is Skeleton3D parentSkel) skel = parentSkel;
            if (skel == null) skel = fallbackSkeleton;

            int stride = (rawBones != null && rawWeights != null && vCount > 0 && rawBones.Length >= vCount * 8 && rawWeights.Length >= vCount * 8) ? 8 : 4;
            bool useSkinning = skel != null && mi.Skin != null && mi.Skin.GetBindCount() > 0 &&
                               rawBones != null && rawWeights != null &&
                               rawBones.Length >= vCount * stride && rawWeights.Length >= vCount * stride;

            if (!useSkinning)
            {
                for (int v = 0; v < vCount; v++)
                {
                    outVerts[v] = fallbackXform * rawVerts[v];
                    outNorms[v] = (rawNorms != null && rawNorms.Length == vCount)
                        ? (fallbackXform.Basis * rawNorms[v]).Normalized()
                        : Vector3.Up;
                }
                return;
            }

            Transform3D skelToHero = prefixTransform * ComputeTransformRelativeToRoot(skel, sourceRoot);
            int bindCount = mi.Skin.GetBindCount();
            int skelBoneCount = skel.GetBoneCount();
            var bindToHero = new Transform3D[bindCount];
            for (int bi = 0; bi < bindCount; bi++)
            {
                int boneIdx = ResolveSkinBindBone(mi.Skin, bi, skel);
                Transform3D gRest = (boneIdx >= 0 && boneIdx < skelBoneCount) ? skel.GetBoneGlobalRest(boneIdx) : Transform3D.Identity;
                bindToHero[bi] = skelToHero * gRest * mi.Skin.GetBindPose(bi);
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
                    accNorm += (bt.Basis * rn) * w;
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
        /// </summary>
        public static (Vector3 Min, Vector3 Max, bool Valid) ComputePristineModelBounds(Node3D root)
        {
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            bool found = false;

            Skeleton3D fallbackSkel = FindSkeleton(root);
            var meshes = CollectMeshes(root);
            foreach (var mi in meshes)
            {
                if (mi.Mesh == null) continue;

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
            else if (skeleton.GetBoneParent(idx) != lowerIdx)
            {
                skeleton.SetBoneParent(idx, lowerIdx);
            }

            return idx;
        }

        /// <summary>
        /// Ensures every mapped bone in heroSkeleton has a valid, non-zero InverseBindMatrix entry in skin.
        /// Searches existing binds by bone index or bone name, verifies/repairs the bind pose against
        /// skeleton.GetBoneGlobalRest(skeletonBoneIdx).AffineInverse() (within 1cm), or adds a new bind.
        /// </summary>
        public static int GetOrAddSkinBindIndex(Skin skin, Skeleton3D skeleton, int skeletonBoneIdx)
        {
            if (skin == null || skeleton == null || skeletonBoneIdx < 0 || skeletonBoneIdx >= skeleton.GetBoneCount())
                return 0;

            string boneName = skeleton.GetBoneName(skeletonBoneIdx);
            int bindCount = skin.GetBindCount();

            // 1. Search for existing bind matching either bone index or bone name
            for (int b = 0; b < bindCount; b++)
            {
                int bindBone = skin.GetBindBone(b);
                string bindName = skin.GetBindName(b);
                if (bindBone == skeletonBoneIdx || (!string.IsNullOrEmpty(bindName) && bindName == boneName))
                {
                    Transform3D restGlobal = skeleton.GetBoneGlobalRest(skeletonBoneIdx);
                    Transform3D expectedInv = restGlobal.AffineInverse();
                    Transform3D currentInv = skin.GetBindPose(b);
                    Transform3D currentPose = currentInv.AffineInverse();

                    if (currentPose.Origin.DistanceTo(restGlobal.Origin) > 0.01f ||
                        float.IsNaN(currentInv.Origin.X) || currentInv == Transform3D.Identity)
                    {
                        skin.SetBindPose(b, expectedInv);
                    }
                    if (skin.GetBindBone(b) != skeletonBoneIdx)
                    {
                        skin.SetBindBone(b, skeletonBoneIdx);
                    }
                    if (string.IsNullOrEmpty(bindName) || bindName != boneName)
                    {
                        skin.SetBindName(b, boneName);
                    }
                    return b;
                }
            }

            // 2. Not found in skin -> Add bind!
            Transform3D invBind = skeleton.GetBoneGlobalRest(skeletonBoneIdx).AffineInverse();
            skin.AddBind(skeletonBoneIdx, invBind);
            int newBindIdx = skin.GetBindCount() - 1;
            skin.SetBindName(newBindIdx, boneName);
            return newBindIdx;
        }

        public static void EnsureSkinHasBind(Skin skin, Skeleton3D skeleton, int boneIdx)
        {
            GetOrAddSkinBindIndex(skin, skeleton, boneIdx);
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
                // Skip procedural/IK/twist/cloth/cervical/shoulder bones whose authored or constraint-driven translations exceed 1.5 cm legitimately
                if (IsExcludedClothOrAccessoryBone(name) ||
                    name.Contains("pelvis") || name.Contains("twist") ||
                    name.Contains("neck") || name.Contains("head") ||
                    name.Contains("clavicle") || name.Contains("finger"))
                {
                    continue;
                }

                bool isAnatomicalLimb = name.Contains("arm_upper") || name.Contains("arm_lower") ||
                                       name == "hand_l" || name == "hand_r" ||
                                       name.Contains("leg_upper") || name.Contains("leg_lower") ||
                                       name.Contains("ankle") || name.Contains("ball") ||
                                       name.Contains("thigh") || name.Contains("shin") ||
                                       name.Contains("calf") || name == "foot_l" || name == "foot_r";

                if (!isAnatomicalLimb) continue;

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

        /// <summary>
        /// Loads a 3D model file (.glb, .gltf, .fbx), automatically repairing Blender KHR_materials_unlit / Emission
        /// materials (where baseColorFactor is [0,0,0,1] and textures/colors live in emissiveTexture/emissiveFactor),
        /// disambiguating generic material names, and tagging inverted-hull outline shells.
        /// </summary>
        public static Node3D LoadAndSanitize3DModelScene(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;

            Node importedScene = null;
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            string baseDir = Path.GetDirectoryName(filePath) ?? "";

            try
            {
                if (ext == ".fbx")
                {
                    try
                    {
                        var fbxDoc = new FbxDocument();
                        var fbxState = new FbxState();
                        if (fbxDoc.AppendFromFile(filePath, fbxState) == Error.Ok)
                        {
                            importedScene = fbxDoc.GenerateScene(fbxState);
                        }
                    }
                    catch (Exception fbxEx)
                    {
                        GD.Print($"[CustomModelAutoRigger] Direct FBX load warning: {fbxEx.Message}. Trying GltfDocument fallback...");
                    }
                }

                if (importedScene == null && ext == ".glb")
                {
                    try
                    {
                        byte[] rawGlb = File.ReadAllBytes(filePath);
                        byte[] patchedGlb = PreprocessGlbBytesForUnlitAndEmissiveMaterials(rawGlb);
                        var gltfDoc = new GltfDocument();
                        var gltfState = new GltfState();
                        if (gltfDoc.AppendFromBuffer(patchedGlb, baseDir, gltfState) == Error.Ok)
                        {
                            importedScene = gltfDoc.GenerateScene(gltfState);
                            if (importedScene != null)
                            {
                                var glbManifest = DeadlockPlayground.Materials.MaterialChannelDiscovery.ParseGlbManifest(rawGlb);
                                DeadlockPlayground.Materials.MaterialChannelDiscovery.RegisterGlbManifest(importedScene, glbManifest);
                            }
                        }
                    }
                    catch (Exception glbEx)
                    {
                        GD.Print($"[CustomModelAutoRigger] GLB buffer preprocess warning: {glbEx.Message}. Falling back to AppendFromFile.");
                    }
                }

                if (importedScene == null)
                {
                    var gltfDoc = new GltfDocument();
                    var gltfState = new GltfState();
                    if (gltfDoc.AppendFromFile(filePath, gltfState) == Error.Ok)
                    {
                        importedScene = gltfDoc.GenerateScene(gltfState);
                    }
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[CustomModelAutoRigger] Failed to load 3D file '{filePath}': {ex.Message}");
                return null;
            }

            if (importedScene == null) return null;

            var sceneRoot = importedScene as Node3D ?? new Node3D();
            if (sceneRoot != importedScene)
            {
                var childManifest = DeadlockPlayground.Materials.MaterialChannelDiscovery.TryGetGlbManifest(importedScene);
                if (childManifest != null)
                {
                    DeadlockPlayground.Materials.MaterialChannelDiscovery.RegisterGlbManifest(sceneRoot, childManifest);
                }
                sceneRoot.AddChild(importedScene);
            }

            if (!string.IsNullOrEmpty(filePath))
            {
                sceneRoot.SetMeta("ImportedSourceBaseDir", Path.GetDirectoryName(filePath) ?? "");
            }

            SanitizeImportedModelMaterials(sceneRoot);
            return sceneRoot;
        }

        /// <summary>
        /// Repairs binary glTF 2.0 (.glb) JSON chunk in-memory before Godot's GltfDocument parses it:
        /// 1. When a material uses KHR_materials_unlit or Emission nodes with pbrMetallicRoughness.baseColorFactor = [0,0,0,1]:
        ///    - Copies emissiveTexture to pbrMetallicRoughness.baseColorTexture if baseColorTexture is missing (e.g. Sasha's 'Iris' pupils!).
        ///    - Sets baseColorFactor RGB to [1,1,1] (if textured) or emissiveFactor (if untextured, e.g. Sasha's white eye sclera 'material'!).
        /// 2. Disambiguates generic Blender material names ("material", "Material.001", etc.) with the mesh name so distinct materials never collide.
        /// </summary>
        public static byte[] PreprocessGlbBytesForUnlitAndEmissiveMaterials(byte[] glbBytes)
        {
            if (glbBytes == null || glbBytes.Length < 20) return glbBytes;

            uint magic = BitConverter.ToUInt32(glbBytes, 0);
            uint version = BitConverter.ToUInt32(glbBytes, 4);
            if (magic != 0x46546C67u || version != 2u) return glbBytes;

            uint jsonLen = BitConverter.ToUInt32(glbBytes, 12);
            uint jsonType = BitConverter.ToUInt32(glbBytes, 16);
            if (jsonType != 0x4E4F534Au || 20 + jsonLen > glbBytes.Length) return glbBytes;

            string jsonStr = System.Text.Encoding.UTF8.GetString(glbBytes, 20, (int)jsonLen).TrimEnd('\0', ' ', '\t', '\r', '\n');
            var rootNode = System.Text.Json.Nodes.JsonNode.Parse(jsonStr) as System.Text.Json.Nodes.JsonObject;
            if (rootNode == null) return glbBytes;

            var materialsArr = rootNode["materials"] as System.Text.Json.Nodes.JsonArray;
            if (materialsArr == null || materialsArr.Count == 0) return glbBytes;

            // Map material index -> owning mesh name for disambiguating generic "material" / "Material.001" names
            var matIdxToOwnerMeshName = new Dictionary<int, string>();
            if (rootNode["meshes"] is System.Text.Json.Nodes.JsonArray meshesArr)
            {
                for (int m = 0; m < meshesArr.Count; m++)
                {
                    if (meshesArr[m] is not System.Text.Json.Nodes.JsonObject meshObj) continue;
                    string mName = meshObj["name"]?.GetValue<string>() ?? $"mesh_{m}";
                    if (meshObj["primitives"] is System.Text.Json.Nodes.JsonArray prims)
                    {
                        foreach (var prim in prims)
                        {
                            if (prim is System.Text.Json.Nodes.JsonObject pObj && pObj["material"] != null)
                            {
                                int matIdx = pObj["material"].GetValue<int>();
                                if (!matIdxToOwnerMeshName.ContainsKey(matIdx))
                                {
                                    matIdxToOwnerMeshName[matIdx] = mName;
                                }
                            }
                        }
                    }
                }
            }

            bool modified = false;
            for (int i = 0; i < materialsArr.Count; i++)
            {
                if (materialsArr[i] is not System.Text.Json.Nodes.JsonObject matObj) continue;

                string matName = matObj["name"]?.GetValue<string>() ?? $"mat_{i}";
                string lowerMat = matName.ToLowerInvariant();

                // Disambiguate generic Blender default material names ("material", "material.001", etc.)
                if (lowerMat == "material" || lowerMat.StartsWith("material.") || lowerMat.StartsWith("material_"))
                {
                    if (matIdxToOwnerMeshName.TryGetValue(i, out string ownerMesh) && !string.IsNullOrWhiteSpace(ownerMesh))
                    {
                        string suffix = lowerMat == "material" ? "mat" : matName.Replace('.', '_');
                        string cleanOwner = ownerMesh.Replace('.', '_').Replace(' ', '_');
                        matObj["name"] = $"{cleanOwner}_{suffix}";
                        modified = true;
                    }
                }

                var pbr = matObj["pbrMetallicRoughness"] as System.Text.Json.Nodes.JsonObject;
                if (pbr == null)
                {
                    pbr = new System.Text.Json.Nodes.JsonObject();
                    matObj["pbrMetallicRoughness"] = pbr;
                    modified = true;
                }

                // 1. If baseColorTexture is missing but emissiveTexture is present, promote emissiveTexture to baseColorTexture!
                if (pbr["baseColorTexture"] == null && matObj["emissiveTexture"] is System.Text.Json.Nodes.JsonObject emTex)
                {
                    var clonedTex = System.Text.Json.Nodes.JsonNode.Parse(emTex.ToJsonString()) as System.Text.Json.Nodes.JsonObject;
                    if (clonedTex != null)
                    {
                        pbr["baseColorTexture"] = clonedTex;
                        modified = true;
                    }
                }

                // 2. Check if baseColorFactor is near-black [0, 0, 0, a] while texture or emissiveFactor is present
                float r = 1f, g = 1f, b = 1f, a = 1f;
                if (pbr["baseColorFactor"] is System.Text.Json.Nodes.JsonArray bcf && bcf.Count >= 3)
                {
                    r = bcf[0]?.GetValue<float>() ?? 1f;
                    g = bcf[1]?.GetValue<float>() ?? 1f;
                    b = bcf[2]?.GetValue<float>() ?? 1f;
                    if (bcf.Count >= 4) a = bcf[3]?.GetValue<float>() ?? 1f;
                }

                float er = 0f, eg = 0f, eb = 0f;
                if (matObj["emissiveFactor"] is System.Text.Json.Nodes.JsonArray ef && ef.Count >= 3)
                {
                    er = ef[0]?.GetValue<float>() ?? 0f;
                    eg = ef[1]?.GetValue<float>() ?? 0f;
                    eb = ef[2]?.GetValue<float>() ?? 0f;
                }

                bool hasBaseTex = pbr["baseColorTexture"] != null;
                bool isBlackFactor = r < 0.02f && g < 0.02f && b < 0.02f;

                if (isBlackFactor)
                {
                    if (hasBaseTex)
                    {
                        // Do not multiply textured unlit/emissive materials (like Bodysuit or Iris) by black [0,0,0]!
                        pbr["baseColorFactor"] = new System.Text.Json.Nodes.JsonArray(1.0f, 1.0f, 1.0f, a);
                        modified = true;
                    }
                    else if (er > 0.02f || eg > 0.02f || eb > 0.02f)
                    {
                        // Promote non-zero emissiveFactor (e.g. eye sclera [1,1,1] or colored outline shell) to baseColorFactor!
                        pbr["baseColorFactor"] = new System.Text.Json.Nodes.JsonArray(
                            Math.Clamp(er, 0f, 1f),
                            Math.Clamp(eg, 0f, 1f),
                            Math.Clamp(eb, 0f, 1f),
                            a);
                        modified = true;
                    }
                }
            }

            if (!modified) return glbBytes;

            byte[] newJsonBytes = System.Text.Encoding.UTF8.GetBytes(rootNode.ToJsonString());
            int pad = (4 - (newJsonBytes.Length % 4)) % 4;
            int paddedJsonLen = newJsonBytes.Length + pad;

            int restOffset = 20 + (int)jsonLen;
            int restLen = glbBytes.Length - restOffset;
            uint totalLen = (uint)(20 + paddedJsonLen + restLen);

            byte[] outBytes = new byte[totalLen];
            using var ms = new MemoryStream(outBytes);
            using var bw = new BinaryWriter(ms);
            bw.Write(0x46546C67u);
            bw.Write(2u);
            bw.Write(totalLen);
            bw.Write((uint)paddedJsonLen);
            bw.Write(0x4E4F534Au);
            bw.Write(newJsonBytes);
            for (int p = 0; p < pad; p++) bw.Write((byte)0x20);
            if (restLen > 0)
            {
                bw.Write(glbBytes, restOffset, restLen);
            }
            return outBytes;
        }

        /// <summary>
        /// Inspects all MeshInstance3D nodes and their surfaces in an imported 3D scene:
        /// - Repairs any StandardMaterial3D where AlbedoTexture is null but EmissionTexture is present,
        ///   or where AlbedoColor is black (0,0,0) while a texture or Emission color is present.
        /// - Disables VertexColorUseAsAlbedo if the mesh has an all-black vertex color array.
        /// - Detects Inverted-Hull Outline Shells ("outline_*") and configures single-sided culling + unshaded mode.
        /// </summary>
        public static void SanitizeImportedModelMaterials(Node3D sceneRoot, Dictionary<string, bool> outlineOverrides = null)
        {
            if (sceneRoot == null || !GodotObject.IsInstanceValid(sceneRoot)) return;

            var assignedMatNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matToUniqueName = new Dictionary<Material, string>();
            var glbManifest = DeadlockPlayground.Materials.MaterialChannelDiscovery.TryGetGlbManifest(sceneRoot);
            var externalTexturePool = CollectSiblingExternalTextures(sceneRoot);

            var meshes = CollectMeshes(sceneRoot);
            foreach (var mi in meshes)
            {
                if (mi?.Mesh == null) continue;
                string rawMeshName = mi.Name.ToString();
                int surfCount = mi.Mesh.GetSurfaceCount();

                for (int s = 0; s < surfCount; s++)
                {
                    Material mat = mi.Mesh.SurfaceGetMaterial(s) ?? mi.GetSurfaceOverrideMaterial(s) ?? mi.MaterialOverride;
                    if (mat == null) continue;

                    // Disambiguate material name across distinct material instances so generic names
                    // (like "material", "Material", "default") never collide between meshes (e.g. eye vs pistol)
                    if (!matToUniqueName.TryGetValue(mat, out string uniqueMatName))
                    {
                        string rawMatName = mat.ResourceName?.Trim();
                        if (string.IsNullOrWhiteSpace(rawMatName) ||
                            rawMatName.Equals("material", StringComparison.OrdinalIgnoreCase) ||
                            rawMatName.Equals("default", StringComparison.OrdinalIgnoreCase) ||
                            rawMatName.Equals("custom_mat", StringComparison.OrdinalIgnoreCase))
                        {
                            rawMatName = $"{rawMeshName}_mat";
                        }

                        uniqueMatName = rawMatName;
                        int suffix = 1;
                        while (assignedMatNames.Contains(uniqueMatName))
                        {
                            uniqueMatName = $"{rawMatName}_{suffix++}";
                        }

                        assignedMatNames.Add(uniqueMatName);
                        matToUniqueName[mat] = uniqueMatName;
                        mat.ResourceName = uniqueMatName;
                    }

                    mi.SetMeta("SourceMaterialName", uniqueMatName);
                    mat.SetMeta("MaterialName", uniqueMatName);

                    // Check if vertex color array is all black/zero
                    bool hasAllBlackVertexColors = false;
                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays != null && arrays.Count > (int)Mesh.ArrayType.Color && arrays[(int)Mesh.ArrayType.Color].VariantType != Variant.Type.Nil)
                    {
                        var colors = arrays[(int)Mesh.ArrayType.Color].AsColorArray();
                        if (colors != null && colors.Length > 0)
                        {
                            bool anyNonBlack = false;
                            int step = Math.Max(1, colors.Length / 128);
                            for (int c = 0; c < colors.Length; c += step)
                            {
                                if (colors[c].R > 0.02f || colors[c].G > 0.02f || colors[c].B > 0.02f)
                                {
                                    anyNonBlack = true;
                                    break;
                                }
                            }
                            hasAllBlackVertexColors = !anyNonBlack;
                        }
                    }

                    bool isOutline = IsLikelyOutlineShell(mi, mat);
                    if (outlineOverrides != null && outlineOverrides.TryGetValue(rawMeshName, out bool userOutline))
                    {
                        isOutline = userOutline;
                    }

                    mi.SetMeta("IsOutlineShell", isOutline);

                    if (mat is StandardMaterial3D stdMat)
                    {
                        if (hasAllBlackVertexColors && stdMat.VertexColorUseAsAlbedo)
                        {
                            stdMat.VertexColorUseAsAlbedo = false;
                        }

                        // Repair unlit/emissive texture & color if not already patched at GLB level
                        if (stdMat.AlbedoTexture == null && stdMat.EmissionTexture != null)
                        {
                            stdMat.AlbedoTexture = stdMat.EmissionTexture;
                        }

                        // If material has no AlbedoTexture, bake its solid AlbedoColor into a 16x16 solid-color ImageTexture!
                        // This prevents untextured solid-color materials (e.g. mouth, eye whites, outline_black, outline_for_cube)
                        // from turning black in Painter or borrowing the pistol's texture!
                        if (stdMat.AlbedoTexture == null)
                        {
                            Color solidCol = stdMat.AlbedoColor;
                            if (stdMat.EmissionEnabled && (stdMat.Emission.R > 0.02f || stdMat.Emission.G > 0.02f || stdMat.Emission.B > 0.02f) &&
                                solidCol.R < 0.02f && solidCol.G < 0.02f && solidCol.B < 0.02f)
                            {
                                solidCol = stdMat.Emission;
                            }

                            var img = Image.CreateEmpty(16, 16, false, Image.Format.Rgba8);
                            img.Fill(solidCol);
                            var solidTex = ImageTexture.CreateFromImage(img);
                            solidTex.ResourceName = $"{uniqueMatName}_solid_tex";
                            stdMat.AlbedoTexture = solidTex;
                            stdMat.AlbedoColor = Colors.White;
                        }
                        else
                        {
                            bool isBlackAlbedo = stdMat.AlbedoColor.R < 0.02f && stdMat.AlbedoColor.G < 0.02f && stdMat.AlbedoColor.B < 0.02f;
                            if (isBlackAlbedo)
                            {
                                stdMat.AlbedoColor = new Color(1f, 1f, 1f, stdMat.AlbedoColor.A > 0f ? stdMat.AlbedoColor.A : 1f);
                            }
                        }

                        // Clear redundant unlit Emission so Toon Shader doesn't add (1,1,1) white emission on top of Albedo
                        if (stdMat.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded && stdMat.EmissionEnabled)
                        {
                            stdMat.EmissionEnabled = false;
                            stdMat.Emission = Colors.Black;
                        }

                        stdMat.SetMeta("IsOutlineShell", isOutline);

                        if (isOutline)
                        {
                            // Inverted-hull outline shells must NEVER be two-sided (CullModeEnum.Disabled),
                            // otherwise the front of the outer shell covers the character body underneath!
                            if (stdMat.CullMode == BaseMaterial3D.CullModeEnum.Disabled)
                            {
                                stdMat.CullMode = BaseMaterial3D.CullModeEnum.Back;
                            }
                            stdMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                        }
                    }

                    if (!isOutline && DeadlockPlayground.Materials.MaterialChannelDiscovery.TryGetChannelSet(mat) == null)
                    {
                        DeadlockPlayground.Materials.GlbMaterialSlotInfo matchedSlot = null;
                        if (glbManifest != null)
                        {
                            string rawMatName = mat.ResourceName ?? "";
                            matchedSlot = glbManifest.Materials.FirstOrDefault(gm =>
                                !string.IsNullOrEmpty(gm.MaterialName) &&
                                (string.Equals(gm.MaterialName, rawMatName, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(gm.MaterialName, uniqueMatName, StringComparison.OrdinalIgnoreCase)));
                        }
                        var discovered = DeadlockPlayground.Materials.MaterialChannelDiscovery.DiscoverCustomModelChannels(
                            mat,
                            uniqueMatName,
                            matchedSlot,
                            externalTexturePool);
                        DeadlockPlayground.Materials.MaterialChannelDiscovery.RegisterChannelSet(mat, discovered);
                    }
                }
            }
        }

        private static List<Texture2D> CollectSiblingExternalTextures(Node3D sceneRoot)
        {
            var pool = new List<Texture2D>();
            if (sceneRoot == null || !sceneRoot.HasMeta("ImportedSourceBaseDir")) return pool;
            string baseDir = sceneRoot.GetMeta("ImportedSourceBaseDir").AsString();
            if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) return pool;

            var searchDirs = new List<string> { baseDir };
            string texSub = Path.Combine(baseDir, "textures");
            if (Directory.Exists(texSub)) searchDirs.Add(texSub);
            string texCapSub = Path.Combine(baseDir, "Textures");
            if (Directory.Exists(texCapSub) && !string.Equals(texSub, texCapSub, StringComparison.OrdinalIgnoreCase))
                searchDirs.Add(texCapSub);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in searchDirs)
            {
                try
                {
                    foreach (string file in Directory.EnumerateFiles(dir))
                    {
                        if (!seen.Add(file)) continue;
                        string ext = Path.GetExtension(file).ToLowerInvariant();
                        if (ext != ".png" && ext != ".tga" && ext != ".jpg" && ext != ".jpeg" && ext != ".webp") continue;
                        if (pool.Count >= 48) break;
                        var img = new Image();
                        if (img.Load(file) == Error.Ok)
                        {
                            var tex = ImageTexture.CreateFromImage(img);
                            tex.ResourceName = Path.GetFileName(file);
                            pool.Add(tex);
                        }
                    }
                }
                catch { }
            }
            return pool;
        }

        /// <summary>
        /// Auto-detects whether a MeshInstance3D or its Material is an Inverted-Hull Outline Shell
        /// (e.g. "Outline_Black", "Outline for Cube_005", "Outline for pistol", "Sasha.Body.Outline").
        /// </summary>
        public static bool IsLikelyOutlineShell(MeshInstance3D mi, Material mat = null)
        {
            if (mi == null) return false;
            mat ??= mi.Mesh != null && mi.Mesh.GetSurfaceCount() > 0
                ? (mi.Mesh.SurfaceGetMaterial(0) ?? mi.GetSurfaceOverrideMaterial(0) ?? mi.MaterialOverride)
                : null;

            string meshName = mi.Name.ToString();
            string matName = mat?.ResourceName ?? "";
            if (string.IsNullOrEmpty(matName) && mi.HasMeta("SourceMaterialName"))
            {
                matName = mi.GetMeta("SourceMaterialName").AsString();
            }

            if (meshName.Contains("outline", StringComparison.OrdinalIgnoreCase) ||
                matName.Contains("outline", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static void ConfigureRiggedSubmeshMetadata(MeshInstance3D newMi, MeshInstance3D srcMi, Material surfMat, RiggingOptions options)
        {
            if (newMi == null || srcMi == null) return;

            string srcName = srcMi.Name.ToString();
            bool isOutline = srcMi.HasMeta("IsOutlineShell") && srcMi.GetMeta("IsOutlineShell").AsBool();
            if (!isOutline)
            {
                isOutline = IsLikelyOutlineShell(srcMi, surfMat);
            }
            if (options?.SubmeshIsOutlineShell != null && options.SubmeshIsOutlineShell.TryGetValue(srcName, out bool overrideOutline))
            {
                isOutline = overrideOutline;
            }

            newMi.SetMeta("IsOutlineShell", isOutline);
            newMi.SetMeta("SourceSubmeshName", srcName);

            string matName = surfMat?.ResourceName;
            if (string.IsNullOrWhiteSpace(matName) && srcMi.HasMeta("SourceMaterialName"))
            {
                matName = srcMi.GetMeta("SourceMaterialName").AsString();
            }
            if (!string.IsNullOrWhiteSpace(matName))
            {
                newMi.SetMeta("SourceMaterialName", matName);
            }

            if (options?.SubmeshReplacementMap != null &&
                options.SubmeshReplacementMap.TryGetValue(srcName, out string repTarget) &&
                !string.IsNullOrWhiteSpace(repTarget))
            {
                newMi.SetMeta("ReplacesHeroSubmesh", repTarget);
            }

            if (surfMat is StandardMaterial3D stdMat)
            {
                stdMat.SetMeta("IsOutlineShell", isOutline);
                if (isOutline)
                {
                    if (stdMat.CullMode == BaseMaterial3D.CullModeEnum.Disabled)
                    {
                        stdMat.CullMode = BaseMaterial3D.CullModeEnum.Back;
                    }
                    stdMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                }
            }
        }

        private readonly struct SocketBakeMeta
        {
            public readonly Transform3D BoneRest;
            public readonly Vector3 Pivot;
            public readonly Transform3D BakedInv;

            public SocketBakeMeta(Transform3D boneRest, Vector3 pivot, Transform3D bakedInv)
            {
                BoneRest = boneRest;
                Pivot = pivot;
                BakedInv = bakedInv;
            }
        }

        private readonly struct SocketBakeOutput
        {
            public readonly Skin PropSkin;
            public readonly SocketBakeMeta Meta;

            public SocketBakeOutput(Skin propSkin, SocketBakeMeta meta)
            {
                PropSkin = propSkin;
                Meta = meta;
            }
        }

        public static Transform3D ComputeSocketHeroMatrix(Transform3D boneRest, Vector3 pivot, Vector3 userPosOffset, Vector3 userRotOffset, float userScale)
        {
            float safeScale = userScale > 0.001f ? userScale : 1.0f;
            Basis rotBasis = Basis.FromEuler(new Vector3(
                Mathf.DegToRad(userRotOffset.X),
                Mathf.DegToRad(userRotOffset.Y),
                Mathf.DegToRad(userRotOffset.Z)
            ), EulerOrder.Yxz);

            Basis scaledBasis = (boneRest.Basis * rotBasis) * Basis.FromScale(new Vector3(safeScale, safeScale, safeScale));
            Vector3 origin = pivot + (boneRest.Basis * userPosOffset);
            return new Transform3D(scaledBasis, origin);
        }

        private static SocketBakeOutput BakeSocketPropSurface(
            Skeleton3D targetSkeleton,
            string socketBoneName,
            string meshKey,
            RiggingOptions options,
            Dictionary<string, SocketFrame> socketFrames,
            Vector3[] inputVerts,
            Vector3[] inputNorms,
            Vector3[] alignedVerts,
            Vector3[] alignedNorms,
            int[] boneIndices,
            float[] boneWeights,
            Transform3D? localToHeroSpace,
            bool? hasInputNormals = null)
        {
            int tgtBone = targetSkeleton.FindBone(socketBoneName);
            if (tgtBone == -1) tgtBone = FindBoneAny(targetSkeleton, "weapon_hand_R", "weapon_hand_L", "hand_R", "hand_L", "head");
            if (tgtBone == -1) tgtBone = 0;

            Transform3D boneRest = tgtBone >= 0 ? targetSkeleton.GetBoneGlobalRest(tgtBone) : Transform3D.Identity;
            var propSkin = new Skin();
            propSkin.AddBind(tgtBone, boneRest.AffineInverse());

            bool snapToBone = options.SubmeshSnapToSocket != null &&
                              options.SubmeshSnapToSocket.TryGetValue(meshKey, out bool snap) && snap;
            Vector3 userPosOffset = options.SubmeshSocketOffsetPos != null &&
                                    options.SubmeshSocketOffsetPos.TryGetValue(meshKey, out var uPos) ? uPos : Vector3.Zero;
            Vector3 userRotOffset = options.SubmeshSocketOffsetRot != null &&
                                    options.SubmeshSocketOffsetRot.TryGetValue(meshKey, out var uRot) ? uRot : Vector3.Zero;
            float userScale = options.SubmeshSocketScale != null &&
                              options.SubmeshSocketScale.TryGetValue(meshKey, out float uScale) && uScale > 0.001f ? uScale : 1.0f;

            SocketFrame frame = default;
            bool hasFrame = socketFrames != null && socketFrames.TryGetValue(socketBoneName, out frame);

            Vector3 socketPos = hasFrame ? frame.SocketCenter : boneRest.Origin;
            Vector3 centerPoint = hasFrame ? frame.Center : Vector3.Zero;
            Basis pcaBasis = hasFrame ? frame.PcaBasis : Basis.Identity;
            Basis targetOrient = hasFrame ? frame.TargetOrientation : Basis.Identity;

            Vector3 pivot = snapToBone ? socketPos : Vector3.Zero;
            Transform3D bakedHeroMatrix = ComputeSocketHeroMatrix(boneRest, pivot, userPosOffset, userRotOffset, userScale);

            int vCount = alignedVerts.Length;
            bool useNorms = hasInputNormals ?? (inputNorms != null && inputNorms.Length == vCount);

            for (int v = 0; v < vCount; v++)
            {
                Vector3 heroVert = localToHeroSpace.HasValue ? (localToHeroSpace.Value * inputVerts[v]) : inputVerts[v];
                Vector3 baseVert = snapToBone
                    ? targetOrient * (pcaBasis * (heroVert - centerPoint))
                    : heroVert;
                alignedVerts[v] = bakedHeroMatrix * baseVert;

                if (useNorms && inputNorms != null && v < inputNorms.Length)
                {
                    Vector3 heroNorm = localToHeroSpace.HasValue ? (localToHeroSpace.Value.Basis * inputNorms[v]).Normalized() : inputNorms[v];
                    Vector3 baseNorm = snapToBone ? (targetOrient * (pcaBasis * heroNorm)) : heroNorm;
                    alignedNorms[v] = (bakedHeroMatrix.Basis * baseNorm).Normalized();
                }
                else
                {
                    alignedNorms[v] = Vector3.Up;
                }

                boneIndices[v * 4 + 0] = 0; // Bind 0 in propSkin
                boneWeights[v * 4 + 0] = 1.0f;
                boneIndices[v * 4 + 1] = 0;
                boneWeights[v * 4 + 1] = 0f;
                boneIndices[v * 4 + 2] = 0;
                boneWeights[v * 4 + 2] = 0f;
                boneIndices[v * 4 + 3] = 0;
                boneWeights[v * 4 + 3] = 0f;
            }

            var meta = new SocketBakeMeta(boneRest, pivot, bakedHeroMatrix.AffineInverse());
            return new SocketBakeOutput(propSkin, meta);
        }

        private static void ApplySocketMetadata(MeshInstance3D newMi, string meshKey, SocketBakeMeta meta)
        {
            if (newMi == null) return;
            newMi.SetMeta("IsSocketProp", true);
            newMi.SetMeta("SocketMeshKey", meshKey);
            newMi.SetMeta("SocketBoneRest", meta.BoneRest);
            newMi.SetMeta("SocketPivot", meta.Pivot);
            newMi.SetMeta("SocketBakedInv", meta.BakedInv);
        }

        /// <summary>
        /// Updates a rigged socket prop's Skin bind pose in O(1) when socket Pos/Rot/Scale offsets change,
        /// avoiding vertex buffer rebuilds and full KNN/bind passes while matching animated playback identically.
        /// </summary>
        public static bool TryUpdateSocketSkinBindPose(MeshInstance3D mi, Vector3 userPosOffset, Vector3 userRotOffset, float userScale)
        {
            if (mi == null || !GodotObject.IsInstanceValid(mi) || mi.Skin == null || mi.Skin.GetBindCount() <= 0)
                return false;
            if (!mi.HasMeta("IsSocketProp") || !mi.GetMeta("IsSocketProp").AsBool())
                return false;
            if (!mi.HasMeta("SocketBoneRest") || !mi.HasMeta("SocketPivot") || !mi.HasMeta("SocketBakedInv"))
                return false;

            Transform3D boneRest = mi.GetMeta("SocketBoneRest").AsTransform3D();
            Vector3 pivot = mi.GetMeta("SocketPivot").AsVector3();
            Transform3D bakedInv = mi.GetMeta("SocketBakedInv").AsTransform3D();

            Transform3D liveHeroMatrix = ComputeSocketHeroMatrix(boneRest, pivot, userPosOffset, userRotOffset, userScale);
            mi.Skin.SetBindPose(0, boneRest.AffineInverse() * liveHeroMatrix * bakedInv);
            return true;
        }

        #endregion
    }
}
