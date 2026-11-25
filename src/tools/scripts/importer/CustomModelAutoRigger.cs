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
        public enum RiggingMethod
        {
            KnnSurfaceProjection = 0,    // Case A: Transfer weights from hero body
            PreRiggedSkeletonRemap = 1,  // Case B: Remap existing bones
            RigidSingleBone = 2,         // Case C: Rigid attachment to single bone
            LandmarkAutoRig = 3,         // Case D: Full-body landmark-pin auto-rig (synthetic humanoid skeleton + geodesic weights)
            PartialBodyAutoRig = 4       // Case E: Clothing / partial-body auto-rig (semi-body pins OR region-scoped hero transfer)
        }

        public enum PartialBodyRegion
        {
            UpperBodySweater = 0, // Pelvis anchor + Spine + Clavicles + Arms (ideal for sweaters, jackets, hoodies)
            TorsoVest = 1,        // Pelvis + Spine + Clavicles (ideal for vests, corsets, chest armor)
            LowerBodyPants = 2,   // Pelvis + Upper/Lower Legs + Ankles (ideal for pants, shorts, skirts)
            GlovesSleeves = 3,    // Upper/Lower Arms + Hands + optional fingers
            BootsShoes = 4,       // Lower Legs + Ankles + Toes
            HeadHood = 5,         // Upper Spine collar + Neck + Head
            Custom = 6,           // Custom combination of Spine, Arms, Hands, Legs, Feet, Neck, Head
            CoatDress = 7,        // Pelvis + Spine + Clavicles + Legs (ideal for coats, dresses, tunics, long skirts)
            FullOutfit = 8        // Pelvis + Spine + Clavicles + Arms + Legs (ideal for jumpsuits, bodysuits, full armor)
        }

        public enum PartialBodyEngine
        {
            SemiBodyPins = 0,     // Recommended: Region-scoped 3D reference pins + Voxel Heat + limb pose conformation
            HeroBodyTransfer = 1  // Quick: Region-scoped Hero KNN weight transfer + Pose Angle Match + inverse-pose bake
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

        public enum AccessoryIntent
        {
            Auto = 0,
            FullCharacter = 1,
            AddAccessory = 2,
            ReplacePart = 3
        }

        public const int MaxAccessorySlots = 6;

        public sealed class SkinWeightOverride
        {
            public int VertexCount { get; set; }
            public string[] BonePalette { get; set; } = Array.Empty<string>();
            public int[] PaletteIndices { get; set; } = Array.Empty<int>(); // VertexCount * 4
            public float[] Weights { get; set; } = Array.Empty<float>();      // VertexCount * 4
        }

        public enum HeroDeformMode
        {
            FlattenTopDown = 0,
            RadialShrink = 1
        }

        public enum HeroSculptBrushMode
        {
            PushIn = 0,
            PullOut = 1,
            Flatten = 2,
            Smooth = 3,
            Erase = 4
        }

        public class RiggingOptions
        {
            public string SlotId { get; set; } = "";
            public string AccessoryName { get; set; } = "";
            public AccessoryIntent Intent { get; set; } = AccessoryIntent.Auto;
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
            public List<string> ExplicitReplacedTargets { get; set; } = new();
            /// <summary>Optional hero mesh/surface keys explicitly turned back ON by the user, clearing them from sibling accessory SlotHiddenTargets.</summary>
            public List<string> ExplicitRestoredTargets { get; set; } = new();
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
            public bool StabilizeNeckFloatingHead { get; set; } = false;
            public bool AutoCounterTwistWeights { get; set; } = true;
            public bool IncludeBakedOutlineShells { get; set; } = true;
            public Dictionary<string, bool> SubmeshIsOutlineShell { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public Vector3 RootPosition { get; set; } = Vector3.Zero;
            public Vector3 RootRotationDeg { get; set; } = Vector3.Zero;
            public float RootScale { get; set; } = 1.0f;
            public Vector3 RootScaleXYZ { get; set; } = Vector3.One;
            public bool FlipX { get; set; } = false;
            public bool FlipY { get; set; } = false;
            public bool FlipZ { get; set; } = false;
            public bool OriginalMeshDeformEnabled { get; set; } = false;
            public string OriginalMeshDeformTargetFilter { get; set; } = "hair";
            public string OriginalMeshDeformBoneName { get; set; } = "head";
            public HeroDeformMode OriginalMeshDeformMode { get; set; } = HeroDeformMode.FlattenTopDown;
            public float OriginalMeshDeformStrength { get; set; } = 0.55f;
            public float OriginalMeshDeformRadius { get; set; } = 0.24f;
            public float OriginalMeshDeformHeightOffset { get; set; } = 0.06f;
            /// <summary>Optional sparse per-surface vertex deltas (hero-space) from the 3D Hero Deform Sculpt Brush, keyed by MakeOriginalSurfaceDeformKey(mi, surfIdx).</summary>
            public Dictionary<string, Dictionary<int, Vector3>> OriginalMeshDeformSculpts { get; set; } = null;
            /// <summary>16 landmark pins (see CustomLandmarkWeightSolver.PinNames) in imported-scene space. Null = auto-estimate.</summary>
            public Dictionary<string, Vector3> LandmarkPins { get; set; } = null;
            /// <summary>Modular Auto-Rig Pro–style reference skeleton definition (spine 1–4, fingers, cloth chains). Supersedes LandmarkPins when present.</summary>
            public ReferenceRigDef ReferenceRig { get; set; } = null;
            /// <summary>Optional user-painted per-surface weight overrides keyed by MakeSurfaceKeepKey(meshName, surfaceIdx).</summary>
            public Dictionary<string, SkinWeightOverride> CustomSkinWeightOverrides { get; set; } = null;

            /// <summary>Auto-align axial forearm/palm roll during Auto-Snap Arms &amp; Wrists using palm/finger orientation.</summary>
            public bool AutoAlignHandOrientation { get; set; } = true;
            /// <summary>Manual left-hand rest-pose Euler offset (Pitch X, Yaw Y, Roll Z in degrees) around wrist pivot when Auto-Snap Arms is enabled.</summary>
            public Vector3 LeftHandSnapOffsetDeg { get; set; } = Vector3.Zero;
            /// <summary>Manual right-hand rest-pose Euler offset (Pitch X, Yaw Y, Roll Z in degrees) around wrist pivot when Auto-Snap Arms is enabled.</summary>
            public Vector3 RightHandSnapOffsetDeg { get; set; } = Vector3.Zero;
            /// <summary>Manual uniform scale around wrist pivot for left hand when Auto-Snap Arms is enabled.</summary>
            public float LeftHandSnapScale { get; set; } = 1.0f;
            /// <summary>Manual uniform scale around wrist pivot for right hand when Auto-Snap Arms is enabled.</summary>
            public float RightHandSnapScale { get; set; } = 1.0f;
            /// <summary>Optional rest-pose transforms for kept original hero parts/weapons, keyed by surface keep key (e.g. "_sword_model::surf::0").</summary>
            public Dictionary<string, KeptHeroPartTransform> KeptOriginalPartTransforms { get; set; } = null;
            /// <summary>Optional non-destructive RLE triangle cut masks for original hero surfaces, keyed by MakeOriginalSurfaceDeformKey(mi, surfIdx).</summary>
            public Dictionary<string, int[]> OriginalMeshCutMasks { get; set; } = null;
            /// <summary>Optional non-destructive RLE triangle cut masks for imported / vanilla donor meshes, keyed by MakeSurfaceKeepKey(sourceMeshName, surfIdx).</summary>
            public Dictionary<string, int[]> ImportedMeshCutMasks { get; set; } = null;

            // --- Overhaul Phases 2-6 Options ---
            public RigFamily? RigFamilyOverride { get; set; } = null;
            public bool KeepUnweightedBones { get; set; } = false;
            public bool EnableCustomPhysics { get; set; } = true;
            public bool PreserveSourceClothingChains { get; set; } = true;
            public HashSet<string> DisabledSourceBones { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public List<BoneChainDef> BoneChains { get; set; } = new();
            /// <summary>
            /// Optional explicit set of synthetic ChainIds that are newly added or geometry-dirty and should run
            /// procedural weighting on surfaces that already carry <see cref="CustomSkinWeightOverrides"/>.
            /// When non-null (even if empty), any other chain on a painted surface is left untouched so erased or
            /// sibling-painted chains are never overwritten on re-bind.
            /// </summary>
            public HashSet<string> SyntheticChainsToRebind { get; set; } = null;
            public List<BodyCollisionCapsuleDef> BodyCollisionCapsules { get; set; } = new();
            public AccessoryBoneMode AccessoryBoneMode { get; set; } = AccessoryBoneMode.RigidToOneBone;
            public FingerRigMode FingerMode { get; set; } = FingerRigMode.DeadlockFingerBones;
            public Dictionary<string, Vector3> FingerLandmarks { get; set; } = null;
            public bool GenerateFingerMetacarpals { get; set; } = false;
            public bool GenerateWristTwist { get; set; } = false;
            public bool BindSymmetry { get; set; } = true;
            public bool FitSpine { get; set; } = true;
            public bool ConformToSkeleton { get; set; } = true;
            public bool LooseMeshesRigid { get; set; } = false;
            public bool ExtraTwistBones { get; set; } = false;

            // --- Clothing / Partial-Body Auto-Rig Options ---
            public PartialBodyRegion PartialRegion { get; set; } = PartialBodyRegion.UpperBodySweater;
            public PartialBodyEngine PartialEngine { get; set; } = PartialBodyEngine.SemiBodyPins;
            public bool PartialIncludeSpine { get; set; } = true;
            public bool PartialIncludeArms { get; set; } = true;
            public bool PartialIncludeHands { get; set; } = false;
            public bool PartialIncludeLegs { get; set; } = false;
            public bool PartialIncludeFeet { get; set; } = false;
            public bool PartialIncludeNeck { get; set; } = false;
            public bool PartialIncludeHead { get; set; } = false;

            // --- Imported Mesh Reshape & 3D Fit Sculpt (Sweaters / Clothing / Accessories) ---
            public float ImportedMeshBottomFlare { get; set; } = 0f;
            public float ImportedMeshTopFlare { get; set; } = 0f;
            public float ImportedMeshInflateMeters { get; set; } = 0f;
            public Dictionary<string, Dictionary<int, Vector3>> ImportedMeshSculptDeltas { get; set; } = null;
        }

        public static float ComputeHeroHeight(Skeleton3D heroSkeleton)
        {
            if (heroSkeleton == null || heroSkeleton.GetBoneCount() == 0) return 1.70f;
            int headIdx = heroSkeleton.FindBone("head");
            if (headIdx < 0) headIdx = heroSkeleton.FindBone("neck_0");
            int ankleL = heroSkeleton.FindBone("ankle_L");
            int ankleR = heroSkeleton.FindBone("ankle_R");
            if (headIdx >= 0 && (ankleL >= 0 || ankleR >= 0))
            {
                float headY = heroSkeleton.GetBoneGlobalRest(headIdx).Origin.Y;
                float ankleY = (ankleL >= 0 && ankleR >= 0)
                    ? 0.5f * (heroSkeleton.GetBoneGlobalRest(ankleL).Origin.Y + heroSkeleton.GetBoneGlobalRest(ankleR).Origin.Y)
                    : heroSkeleton.GetBoneGlobalRest(ankleL >= 0 ? ankleL : ankleR).Origin.Y;
                float span = MathF.Abs(headY - ankleY);
                if (span > 0.35f) return Math.Clamp(span / 0.88f, 0.8f, 3.0f);
            }
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int b = 0; b < heroSkeleton.GetBoneCount(); b++)
            {
                float y = heroSkeleton.GetBoneGlobalRest(b).Origin.Y;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            return (maxY > minY + 0.3f) ? Math.Clamp((maxY - minY) / 0.90f, 0.8f, 3.0f) : 1.70f;
        }

        public static ReferenceRigDef.PartialBodyRigMask GetPartialBodyRigMask(RiggingOptions options)
        {
            var region = options?.PartialRegion ?? PartialBodyRegion.UpperBodySweater;
            bool optFeet = options?.PartialIncludeFeet ?? false;
            bool optNeck = options?.PartialIncludeNeck ?? false;
            bool optHands = options?.PartialIncludeHands ?? false;
            bool optHead = options?.PartialIncludeHead ?? false;
            return region switch
            {
                PartialBodyRegion.UpperBodySweater => new ReferenceRigDef.PartialBodyRigMask
                {
                    IncludePelvisAnchor = true,
                    IncludeSpine = true,
                    IncludeClavicles = true,
                    IncludeArms = true,
                    IncludeHands = optHands,
                    IncludeLegs = false,
                    LowerLegsOnly = false,
                    IncludeFeet = false,
                    IncludeNeck = optNeck,
                    IncludeHead = optHead
                },
                PartialBodyRegion.TorsoVest => new ReferenceRigDef.PartialBodyRigMask
                {
                    IncludePelvisAnchor = true,
                    IncludeSpine = true,
                    IncludeClavicles = true,
                    IncludeArms = false,
                    IncludeHands = false,
                    IncludeLegs = false,
                    LowerLegsOnly = false,
                    IncludeFeet = false,
                    IncludeNeck = optNeck,
                    IncludeHead = optHead
                },
                PartialBodyRegion.LowerBodyPants => new ReferenceRigDef.PartialBodyRigMask
                {
                    IncludePelvisAnchor = true,
                    IncludeSpine = false,
                    IncludeClavicles = false,
                    IncludeArms = false,
                    IncludeHands = false,
                    IncludeLegs = true,
                    LowerLegsOnly = false,
                    IncludeFeet = optFeet,
                    IncludeNeck = false,
                    IncludeHead = false
                },
                PartialBodyRegion.GlovesSleeves => new ReferenceRigDef.PartialBodyRigMask
                {
                    IncludePelvisAnchor = false,
                    IncludeSpine = false,
                    IncludeClavicles = false,
                    IncludeArms = true,
                    IncludeHands = true,
                    IncludeLegs = false,
                    LowerLegsOnly = false,
                    IncludeFeet = false,
                    IncludeNeck = false,
                    IncludeHead = false
                },
                PartialBodyRegion.BootsShoes => new ReferenceRigDef.PartialBodyRigMask
                {
                    IncludePelvisAnchor = false,
                    IncludeSpine = false,
                    IncludeClavicles = false,
                    IncludeArms = false,
                    IncludeHands = false,
                    IncludeLegs = true,
                    LowerLegsOnly = true,
                    IncludeFeet = true,
                    IncludeNeck = false,
                    IncludeHead = false
                },
                PartialBodyRegion.HeadHood => new ReferenceRigDef.PartialBodyRigMask
                {
                    IncludePelvisAnchor = false,
                    IncludeSpine = false,
                    IncludeCollarSpineAnchor = true,
                    IncludeClavicles = false,
                    IncludeArms = false,
                    IncludeHands = false,
                    IncludeLegs = false,
                    LowerLegsOnly = false,
                    IncludeFeet = false,
                    IncludeNeck = true,
                    IncludeHead = true
                },
                PartialBodyRegion.CoatDress => new ReferenceRigDef.PartialBodyRigMask
                {
                    IncludePelvisAnchor = true,
                    IncludeSpine = true,
                    IncludeClavicles = true,
                    IncludeArms = options?.PartialIncludeArms ?? false,
                    IncludeHands = optHands,
                    IncludeLegs = true,
                    LowerLegsOnly = false,
                    IncludeFeet = optFeet,
                    IncludeNeck = optNeck,
                    IncludeHead = optHead
                },
                PartialBodyRegion.FullOutfit => new ReferenceRigDef.PartialBodyRigMask
                {
                    IncludePelvisAnchor = true,
                    IncludeSpine = true,
                    IncludeClavicles = true,
                    IncludeArms = true,
                    IncludeHands = optHands,
                    IncludeLegs = true,
                    LowerLegsOnly = false,
                    IncludeFeet = optFeet,
                    IncludeNeck = optNeck,
                    IncludeHead = optHead
                },
                _ => new ReferenceRigDef.PartialBodyRigMask
                {
                    IncludePelvisAnchor = (options?.PartialIncludeSpine ?? true) || (options?.PartialIncludeLegs ?? false),
                    IncludeSpine = options?.PartialIncludeSpine ?? true,
                    IncludeCollarSpineAnchor = ((options?.PartialIncludeHead ?? false) || optNeck) && !(options?.PartialIncludeSpine ?? true),
                    IncludeClavicles = (options?.PartialIncludeSpine ?? true) || (options?.PartialIncludeArms ?? true),
                    IncludeArms = (options?.PartialIncludeArms ?? true) || optHands,
                    IncludeHands = optHands,
                    IncludeLegs = (options?.PartialIncludeLegs ?? false) || optFeet,
                    LowerLegsOnly = !(options?.PartialIncludeLegs ?? false) && optFeet,
                    IncludeFeet = optFeet,
                    IncludeNeck = optNeck || optHead,
                    IncludeHead = optHead
                }
            };
        }

        public static HashSet<string> GetAllowedPartialRegionPins(RiggingOptions options)
        {
            var m = GetPartialBodyRigMask(options);
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (m.IncludePelvisAnchor) set.Add("Pelvis");
            if (m.IncludeSpine || m.IncludeCollarSpineAnchor)
            {
                set.Add("Chest");
                set.Add("Neck");
            }
            if (m.IncludeNeck)
            {
                set.Add("Neck");
            }
            if (m.IncludeHead)
            {
                set.Add("Neck");
                set.Add("Chin");
                set.Add("Head");
            }
            if (m.IncludeClavicles && !m.IncludeArms)
            {
                set.Add("ShoulderL");
                set.Add("ShoulderR");
            }
            if (m.IncludeArms || m.IncludeHands)
            {
                set.Add("ShoulderL"); set.Add("ElbowL"); set.Add("WristL");
                set.Add("ShoulderR"); set.Add("ElbowR"); set.Add("WristR");
                if (m.IncludeHands)
                {
                    set.Add("HandTipL"); set.Add("HandTipR");
                }
            }
            if (m.IncludeLegs)
            {
                if (!m.LowerLegsOnly) { set.Add("HipL"); set.Add("HipR"); }
                set.Add("KneeL"); set.Add("AnkleL");
                set.Add("KneeR"); set.Add("AnkleR");
                if (m.IncludeFeet)
                {
                    set.Add("ToeL"); set.Add("ToeR");
                }
            }
            if (set.Count == 0)
            {
                set.Add("Pelvis"); set.Add("Chest"); set.Add("Neck");
            }
            return set;
        }

        public static bool IsBoneInPartialRegion(string boneName, RiggingOptions options)
        {
            if (string.IsNullOrEmpty(boneName)) return false;
            var m = GetPartialBodyRigMask(options);
            string b = boneName.ToLowerInvariant();

            if (b.Contains("finger") || b.Contains("thumb") || b.Contains("index") ||
                b.Contains("middle") || b.Contains("ring") || b.Contains("pinky"))
            {
                return m.IncludeHands;
            }
            if (b == "hand_l" || b == "hand_r" || b.StartsWith("hand_") || b.Contains("wrist"))
            {
                return m.IncludeHands || m.IncludeArms;
            }
            if (b.Contains("arm_upper") || b.Contains("arm_lower") || b.Contains("forearm") ||
                b.Contains("upperarm") || b.Contains("elbow") || b.Contains("shoulder") || b.Contains("bicep"))
            {
                return m.IncludeArms || m.IncludeHands;
            }
            if (b.Contains("clavicle") || b.Contains("collar"))
            {
                return m.IncludeClavicles || m.IncludeSpine || m.IncludeArms;
            }
            if (b.Contains("neck"))
            {
                return m.IncludeNeck || m.IncludeHead;
            }
            if (b.Contains("head") || b.Contains("jaw") || b.Contains("face") || b.Contains("eye"))
            {
                return m.IncludeHead;
            }
            if (b.Contains("leg_upper") || b.Contains("thigh") || b.Contains("hip"))
            {
                return m.IncludeLegs && !m.LowerLegsOnly;
            }
            if (b.Contains("toe") || b.Contains("ball") || b.Contains("foot"))
            {
                return m.IncludeLegs && m.IncludeFeet;
            }
            if (b.Contains("leg_lower") || b.Contains("calf") || b.Contains("shin") || b.Contains("knee") ||
                b.Contains("ankle"))
            {
                return m.IncludeLegs;
            }
            if (b.Contains("spine") || b.Contains("chest") || b.Contains("torso") || b.Contains("rib"))
            {
                if (m.IncludeSpine) return true;
                if (m.IncludeCollarSpineAnchor && (b.Contains("spine_3") || b.Contains("spine_2") || b.Contains("chest")))
                    return true;
                return false;
            }
            if (b.Contains("pelvis"))
            {
                return m.IncludePelvisAnchor;
            }
            return false;
        }

        /// <summary>Artist rest-pose offset applied to a kept original hero part/weapon (e.g. Fencer's sword) around its dominant bone pivot.</summary>
        public sealed class KeptHeroPartTransform
        {
            public Vector3 PositionOffset { get; set; } = Vector3.Zero;
            public Vector3 RotationDeg { get; set; } = Vector3.Zero;
            public float Scale { get; set; } = 1.0f;

            public bool IsIdentity =>
                PositionOffset.LengthSquared() < 1e-8f &&
                RotationDeg.LengthSquared() < 1e-6f &&
                MathF.Abs(Scale - 1.0f) < 1e-4f;
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
                    // Axial-only scaling clamped to [0.75, 1.30] - zero lateral thickness scaling!
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

            /// <summary>Axial twist component (auto palm roll + manual Roll Z) around UnitTargetLower, blended smoothly across distal forearm.</summary>
            public Quaternion RotationForearmTwistExtra { get; set; } = Quaternion.Identity;
            /// <summary>Manual wrist bend (Pitch X / Yaw Y in target palm frame) applied at Ct for hand and fingers.</summary>
            public Quaternion RotationHandBend { get; set; } = Quaternion.Identity;
            /// <summary>Total hand orientation = (RotationHandBend * RotationForearmTwistExtra * RotationLower).</summary>
            public Quaternion RotationHandTotal { get; set; } = Quaternion.Identity;
            /// <summary>Uniform scale around wrist pivot Ct applied to hand + fingers.</summary>
            public float HandScale { get; set; } = 1.0f;
            /// <summary>Counter-twist helper bones (e.g. arm_upper_L_TWIST, arm_lower_L_TWIST) that must evaluate with upper/lower arm segments instead of falling through to hand.</summary>
            public HashSet<int> UpperTwistBones { get; set; }
            public HashSet<int> LowerTwistBones { get; set; }

            public bool AppliesTo(int targetBoneIdx, Skeleton3D targetSkeleton)
            {
                if (targetBoneIdx == TargetUpperBone || targetBoneIdx == TargetLowerBone || targetBoneIdx == TargetHandBone)
                    return true;
                if ((UpperTwistBones != null && UpperTwistBones.Contains(targetBoneIdx)) ||
                    (LowerTwistBones != null && LowerTwistBones.Contains(targetBoneIdx)))
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
                if (targetBoneIdx == TargetUpperBone || (UpperTwistBones != null && UpperTwistBones.Contains(targetBoneIdx)))
                {
                    Vector3 d = vHero - As;
                    float proj = d.Dot(UnitSourceUpper);
                    Vector3 dScaled = UnitSourceUpper * (proj * SigmaUpper) + (d - UnitSourceUpper * proj);
                    return (At + RotationUpper * dScaled, RotationUpper);
                }

                if (targetBoneIdx == TargetLowerBone || (LowerTwistBones != null && LowerTwistBones.Contains(targetBoneIdx)))
                {
                    Vector3 d = vHero - Bs;
                    float proj = d.Dot(UnitSourceLower);
                    Vector3 perp = d - UnitSourceLower * proj;
                    float tArm = LengthSourceLower > 1e-4f ? Mathf.Clamp(proj / LengthSourceLower, 0f, 1f) : 0f;
                    float twistWeight = 0f;
                    if (tArm > 0.35f)
                    {
                        float u = (tArm - 0.35f) / 0.65f;
                        twistWeight = u * u * (3f - 2f * u);
                    }
                    float perpScale = Mathf.Lerp(1.0f, HandScale, twistWeight);
                    Vector3 dScaled = UnitSourceLower * (proj * SigmaLower) + (perp * perpScale);
                    if (twistWeight > 1e-4f && !RotationForearmTwistExtra.IsEqualApprox(Quaternion.Identity))
                    {
                        Quaternion qStep = Quaternion.Identity.Slerp(RotationForearmTwistExtra, twistWeight);
                        return (Bt + qStep * (RotationLower * dScaled), (qStep * RotationLower).Normalized());
                    }
                    return (Bt + RotationLower * dScaled, RotationLower);
                }

                // TargetHandBone or finger descendants
                Quaternion rotHand = !RotationHandTotal.IsEqualApprox(default) && RotationHandTotal.LengthSquared() > 0.5f
                    ? RotationHandTotal
                    : RotationLower;
                Vector3 vHand = Ct + rotHand * ((vHero - Cs) * HandScale);

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
            public HashSet<int> ThighTwistBones { get; set; }
            public HashSet<int> CalfTwistBones { get; set; }

            public bool AppliesTo(int targetBoneIdx, Skeleton3D targetSkeleton)
            {
                if (targetBoneIdx == TargetThighBone || targetBoneIdx == TargetCalfBone || targetBoneIdx == TargetAnkleBone)
                    return true;
                if ((ThighTwistBones != null && ThighTwistBones.Contains(targetBoneIdx)) ||
                    (CalfTwistBones != null && CalfTwistBones.Contains(targetBoneIdx)))
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
                if (targetBoneIdx == TargetThighBone || (ThighTwistBones != null && ThighTwistBones.Contains(targetBoneIdx)))
                {
                    Vector3 d = vHero - As;
                    float proj = d.Dot(UnitSourceThigh);
                    Vector3 dScaled = UnitSourceThigh * (proj * SigmaThigh) + (d - UnitSourceThigh * proj);
                    return (At + RotationThigh * dScaled, RotationThigh);
                }

                if (targetBoneIdx == TargetCalfBone || (CalfTwistBones != null && CalfTwistBones.Contains(targetBoneIdx)))
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
            public ImportReport Report { get; set; } = new();
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
            HashSet<string> includedSet = (includedSubmeshNames != null && includedSubmeshNames.Count > 0)
                ? new HashSet<string>(includedSubmeshNames, StringComparer.OrdinalIgnoreCase)
                : null;
            var targetMeshes = includedSet != null
                ? allMeshes.Where(m => includedSet.Contains(m.Name.ToString())).ToList()
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

            // Clean up only the matching slot container (never deleting other accessory slots or full character base)
            string targetContainerName = GetContainerNameForSlot(options?.SlotId);
            var existingCustom = heroNode.FindChild(targetContainerName, true, false);
            if (existingCustom != null && GodotObject.IsInstanceValid(existingCustom))
            {
                existingCustom.GetParent()?.RemoveChild(existingCustom);
                existingCustom.QueueFree();
            }

            bool isAccessorySlot = !string.IsNullOrWhiteSpace(options.SlotId) ||
                                   options.Intent == AccessoryIntent.AddAccessory ||
                                   options.Intent == AccessoryIntent.ReplacePart;

            // Clean up any bones previously appended by this slot before re-binding.
            // CRITICAL: Do NOT re-play AnimationPlayer poses mid-RigAndBind (restoreAnimationPose: false),
            // otherwise re-binding with any prior appended/synthetic bones poses heroSkeleton into the active
            // animation before RigPreRiggedModel/RigStaticModel runs, severely scrambling mesh weights!
            string activeSlotKey = string.IsNullOrWhiteSpace(options?.SlotId) ? "__main__" : options.SlotId;
            RemoveAppendedSlotBonesFromSkeleton(heroNode, heroSkeleton, activeSlotKey, restoreAnimationPose: false);
            heroSkeleton.ResetBonePoses();
            if (!heroSkeleton.HasMeta("OriginalHeroBoneCount"))
            {
                heroSkeleton.SetMeta("OriginalHeroBoneCount", heroSkeleton.GetBoneCount());
            }

            // Ensure forearm twist bones exist for full character skeletal rigs when requested
            if (!isAccessorySlot && options.ExtraTwistBones && options.Method != RiggingMethod.RigidSingleBone)
            {
                EnsureForearmTwistBone(heroSkeleton, isLeft: true);
                EnsureForearmTwistBone(heroSkeleton, isLeft: false);
            }

            var heroSkin = FindSkin(heroNode);
            // Create a dedicated Skin instance duplicated from heroSkin (or freshly created)
            // so we never mutate the vanilla hero model's skin and guarantee every bone in heroSkeleton
            // has a valid, non-zero InverseBindMatrix entry!
            Skin customSkin = heroSkin != null ? (Skin)heroSkin.Duplicate() : CreateFullSkeletonSkin(heroSkeleton);
            for (int b = 0; b < heroSkeleton.GetBoneCount(); b++)
            {
                GetOrAddSkinBindIndex(customSkin, heroSkeleton, b);
            }

            // Stage 1 (Synthetic Clothing Chains): Create synthetic bones on heroSkeleton + customSkin BEFORE
            // RigPreRiggedModel / RigUnriggedModel / RigRigidBoneModel runs so any user-painted CustomSkinWeightOverrides
            // referencing synthetic bones resolve to their exact bone indices instead of falling back to bone 0 (root_motion)!
            List<AddClothingChainTool.PreparedSyntheticChain> preparedSyntheticChains = null;
            int addedSyntheticBoneCount = 0;
            if (options.BoneChains != null && options.BoneChains.Any(c => c != null && c.IsSynthetic && c.Policy == ChainPolicy.PreservePhysicsChain))
            {
                preparedSyntheticChains = AddClothingChainTool.CreateSyntheticChainBones(
                    heroSkeleton, customSkin, options.BoneChains, out addedSyntheticBoneCount);
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

            int requestedFlipMask = (options.FlipX ? 1 : 0) | (options.FlipY ? 2 : 0) | (options.FlipZ ? 4 : 0);
            int alreadyBakedMask = importedScene.HasMeta("BakedFlipMask") ? importedScene.GetMeta("BakedFlipMask").AsInt32() : 0;
            Action restoreFlip = null;
            if (requestedFlipMask != 0 && alreadyBakedMask != requestedFlipMask)
            {
                bool flattenSkinned = options.Method == RiggingMethod.RigidSingleBone ||
                                      options.Preset == PosePreset.RigidSingleBone ||
                                      options.Method == RiggingMethod.KnnSurfaceProjection ||
                                      options.Method == RiggingMethod.LandmarkAutoRig ||
                                      options.Method == RiggingMethod.PartialBodyAutoRig;
                restoreFlip = ApplyGeometryFlipToScene(
                    importedScene,
                    options.FlipX,
                    options.FlipY,
                    options.FlipZ,
                    options.IncludedImportedSubmeshNames != null && options.IncludedImportedSubmeshNames.Count > 0
                        ? new HashSet<string>(options.IncludedImportedSubmeshNames, StringComparer.OrdinalIgnoreCase)
                        : null,
                    flattenSkinned);
            }

            Action restoreImportedSculpt = ApplyImportedMeshSculptToScene(importedScene, options);

            var importedSkeleton = FindSkeleton(importedScene);
            bool hasImportedRig = importedSkeleton != null && importedMeshes.Any(m => m.Skin != null || HasSkinnedVertices(m));
            bool useSyntheticPinRig =
                options.Method == RiggingMethod.LandmarkAutoRig ||
                (options.Method == RiggingMethod.PartialBodyAutoRig && options.PartialEngine == PartialBodyEngine.SemiBodyPins);

            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            GD.Print($"[CustomModelAutoRigger] Processing imported model: {importedMeshes.Count} submesh(es), HasRig={hasImportedRig}, Method={options.Method}, PartialRegion={options.PartialRegion}/{options.PartialEngine}, Preset={options.Preset}, Mode={options.Mode}, AccBoneMode={options.AccessoryBoneMode}");

            try
            {
                // Separate lazy-loaded vanilla Deadlock donor meshes (already in Deadlock Hero Space,
                // unskinned) when the primary model uses a foreign skeleton/landmark/own-bone pipeline,
                // so donor meshes are rigged via KNN/socket without interfering with the primary skeleton.
                bool needsSeparateDonorPass =
                    (isAccessorySlot && options.AccessoryBoneMode == AccessoryBoneMode.KeepOwnBones && hasImportedRig) ||
                    useSyntheticPinRig ||
                    (options.Method == RiggingMethod.PreRiggedSkeletonRemap && hasImportedRig);

                List<MeshInstance3D> primaryMeshes = importedMeshes;
                List<MeshInstance3D> heroSpaceDonorMeshes = null;
                if (needsSeparateDonorPass)
                {
                    heroSpaceDonorMeshes = importedMeshes
                        .Where(m => m.HasMeta("IsHeroSpaceDonorMesh") && m.GetMeta("IsHeroSpaceDonorMesh").AsBool())
                        .ToList();
                    if (heroSpaceDonorMeshes.Count > 0)
                    {
                        primaryMeshes = importedMeshes
                            .Where(m => !(m.HasMeta("IsHeroSpaceDonorMesh") && m.GetMeta("IsHeroSpaceDonorMesh").AsBool()))
                            .ToList();
                    }
                    else
                    {
                        heroSpaceDonorMeshes = null;
                    }
                }

                bool forceRigidAccessory = isAccessorySlot && options.AccessoryBoneMode == AccessoryBoneMode.RigidToOneBone &&
                                           options.Method == RiggingMethod.RigidSingleBone;
                bool isRigidNonSkin = (options.Method == RiggingMethod.RigidSingleBone || options.Preset == PosePreset.RigidSingleBone) &&
                                      !(isAccessorySlot && options.AccessoryBoneMode == AccessoryBoneMode.SkinToHero);

                if (primaryMeshes.Count == 0 && heroSpaceDonorMeshes != null && heroSpaceDonorMeshes.Count > 0)
                {
                    result = RigUnriggedModel(importedScene, heroSpaceDonorMeshes, heroNode, heroSkeleton, customSkin, options);
                    heroSpaceDonorMeshes = null;
                }
                else if (isAccessorySlot && options.AccessoryBoneMode == AccessoryBoneMode.KeepOwnBones && hasImportedRig)
                {
                    result = RigAccessoryKeepOwnBones(importedScene, primaryMeshes, importedSkeleton, heroNode, heroSkeleton, customSkin, options);
                }
                else if (forceRigidAccessory || isRigidNonSkin)
                {
                    result = RigRigidBoneModel(importedScene, primaryMeshes, heroNode, heroSkeleton, customSkin, options);
                }
                else if (useSyntheticPinRig)
                {
                    var synth = CustomLandmarkWeightSolver.BuildSyntheticRig(importedScene, primaryMeshes, heroSkeleton, options);
                    try
                    {
                        // Merge & refresh authored cloth chains from ReferenceRig into options.BoneChains
                        // (preserving existing user physics presets while removing deleted refcloth_* entries).
                        options.BoneChains ??= new List<BoneChainDef>();
                        var activeAuthoredIds = new HashSet<string>(
                            (synth.AuthoredClothChainDefs ?? new List<BoneChainDef>())
                                .Where(c => c != null && !string.IsNullOrEmpty(c.ChainId))
                                .Select(c => c.ChainId),
                            StringComparer.OrdinalIgnoreCase);
                        options.BoneChains.RemoveAll(c =>
                            c != null && !c.IsSynthetic &&
                            !string.IsNullOrEmpty(c.ChainId) &&
                            c.ChainId.StartsWith("refcloth_", StringComparison.OrdinalIgnoreCase) &&
                            !activeAuthoredIds.Contains(c.ChainId));
                        if (synth.AuthoredClothChainDefs != null && synth.AuthoredClothChainDefs.Count > 0)
                        {
                            var existingById = new Dictionary<string, BoneChainDef>(StringComparer.OrdinalIgnoreCase);
                            foreach (var c in options.BoneChains)
                                if (c != null && !string.IsNullOrEmpty(c.ChainId))
                                    existingById[c.ChainId] = c;

                            foreach (var ac in synth.AuthoredClothChainDefs)
                            {
                                if (ac == null || string.IsNullOrEmpty(ac.ChainId)) continue;
                                if (existingById.TryGetValue(ac.ChainId, out var prev))
                                {
                                    prev.Name = ac.Name;
                                    prev.RootSourceBone = ac.RootSourceBone;
                                    prev.SourceBoneNames = new List<string>(ac.SourceBoneNames);
                                    prev.AnchorHeroBone = ac.AnchorHeroBone;
                                }
                                else
                                {
                                    // Avoid stale non-refcloth chain with same RootSourceBone stealing the authored chain
                                    options.BoneChains.RemoveAll(c =>
                                        c != null && !c.IsSynthetic &&
                                        !string.Equals(c.ChainId, ac.ChainId, StringComparison.OrdinalIgnoreCase) &&
                                        string.Equals(c.RootSourceBone, ac.RootSourceBone, StringComparison.OrdinalIgnoreCase));
                                    options.BoneChains.Add(ac);
                                    existingById[ac.ChainId] = ac;
                                }
                            }
                        }
                        GD.Print($"[CustomModelAutoRigger] {(options.Method == RiggingMethod.PartialBodyAutoRig ? $"Partial-Body Auto-Rig ({options.PartialRegion})" : "Modular Auto-Rig")}: synthetic skeleton with {synth.Skeleton.GetBoneCount()} bones, {synth.VertexCount} vertices weighted.");
                        // Synthetic skeleton already uses exact Deadlock canonical bone names (including articulated finger_*_0..2_{L|R}).
                        // Temporarily clear stale pre-rigged CustomBoneRemap / DisabledSourceBones and sync FingerMode/AutoSnap from active rig/region
                        // so 100% of synthetic core + finger bones map 1:1 onto the Deadlock hero skeleton!
                        var prevRemap = options.CustomBoneRemap;
                        var prevDisabled = options.DisabledSourceBones;
                        var prevKeepRigid = options.KeepFingersRigidOnHand;
                        var prevFingerMode = options.FingerMode;
                        var prevFamilyOverride = options.RigFamilyOverride;
                        var prevSnapArms = options.AutoSnapArmsToHero;
                        var prevSnapLegs = options.AutoSnapLegsToHero;
                        var prevSnapHead = options.AutoSnapHeadToHero;
                        try
                        {
                            options.CustomBoneRemap = null;
                            options.DisabledSourceBones = null;
                            options.RigFamilyOverride = null;
                            if (options.Method == RiggingMethod.PartialBodyAutoRig)
                            {
                                var pMask = GetPartialBodyRigMask(options);
                                options.AutoSnapArmsToHero = prevSnapArms && (pMask.IncludeArms || pMask.IncludeHands);
                                options.AutoSnapLegsToHero = prevSnapLegs && pMask.IncludeLegs && !pMask.LowerLegsOnly;
                                options.AutoSnapHeadToHero = prevSnapHead && pMask.IncludeHead;
                                bool articulated = pMask.IncludeHands &&
                                                   options.ReferenceRig != null &&
                                                   options.ReferenceRig.FingerMode == ReferenceFingerRigMode.ArticulatedFingers;
                                options.KeepFingersRigidOnHand = !articulated;
                                options.FingerMode = articulated ? FingerRigMode.DeadlockFingerBones : FingerRigMode.RigidHand;
                            }
                            else if (options.ReferenceRig != null)
                            {
                                bool articulated = options.ReferenceRig.FingerMode == ReferenceFingerRigMode.ArticulatedFingers;
                                options.KeepFingersRigidOnHand = !articulated;
                                options.FingerMode = articulated ? FingerRigMode.DeadlockFingerBones : FingerRigMode.RigidHand;
                            }
                            result = RigPreRiggedModel(importedScene, primaryMeshes, synth.Skeleton, heroNode, heroSkeleton, customSkin, options);
                        }
                        finally
                        {
                            options.CustomBoneRemap = prevRemap;
                            options.DisabledSourceBones = prevDisabled;
                            options.KeepFingersRigidOnHand = prevKeepRigid;
                            options.FingerMode = prevFingerMode;
                            options.RigFamilyOverride = prevFamilyOverride;
                            options.AutoSnapArmsToHero = prevSnapArms;
                            options.AutoSnapLegsToHero = prevSnapLegs;
                            options.AutoSnapHeadToHero = prevSnapHead;
                        }
                    }
                    finally
                    {
                        synth.Restore();
                    }
                }
                else if (options.Method == RiggingMethod.PreRiggedSkeletonRemap && hasImportedRig)
                {
                    result = RigPreRiggedModel(importedScene, primaryMeshes, importedSkeleton, heroNode, heroSkeleton, customSkin, options);
                }
                else
                {
                    // Surface Weight Transfer (KNN or Partial-Body Hero Transfer)!
                    result = RigUnriggedModel(importedScene, primaryMeshes, heroNode, heroSkeleton, customSkin, options);
                }

                // Merge lazy-loaded vanilla Deadlock donor meshes into the primary container
                if (result.Success && heroSpaceDonorMeshes != null && heroSpaceDonorMeshes.Count > 0)
                {
                    var donorRes = RigUnriggedModel(importedScene, heroSpaceDonorMeshes, heroNode, heroSkeleton, customSkin, options);
                    if (donorRes.Success && donorRes.ContainerNode != null && GodotObject.IsInstanceValid(donorRes.ContainerNode))
                    {
                        foreach (var donorMi in donorRes.CustomSubmeshes)
                        {
                            if (donorMi == null || !GodotObject.IsInstanceValid(donorMi)) continue;
                            donorMi.GetParent()?.RemoveChild(donorMi);
                            result.ContainerNode.AddChild(donorMi);
                            if (donorMi.Skeleton != null && !donorMi.Skeleton.IsEmpty && heroSkeleton != null)
                            {
                                donorMi.Skeleton = donorMi.GetPathTo(heroSkeleton);
                            }
                            result.CustomSubmeshes.Add(donorMi);
                        }
                        result.TotalVertices += donorRes.TotalVertices;
                        result.TotalTriangles += donorRes.TotalTriangles;
                        donorRes.ContainerNode.GetParent()?.RemoveChild(donorRes.ContainerNode);
                        donorRes.ContainerNode.QueueFree();
                    }
                }

                swTotal.Stop();
                if (result.Success)
                {
                    result.Report ??= new ImportReport();
                    result.Report.MethodName = options.Method.ToString();
                    result.Report.Stats.VertexCount = result.TotalVertices;
                    result.Report.Stats.TriangleCount = result.TotalTriangles;
                    result.Report.Stats.ElapsedMs = swTotal.Elapsed.TotalMilliseconds;

                    // Phase 4: Generate synthetic finger chains if FingerRigMode.GenerateFingerChains is active on non-rigid character rigs
                    if (options.FingerMode == FingerRigMode.GenerateFingerChains &&
                        options.Method != RiggingMethod.RigidSingleBone &&
                        options.FingerLandmarks != null && options.FingerLandmarks.Count > 0)
                    {
                        int fingerVerts = CustomFingerAutoRigger.ApplyGeneratedFingerChainsAndWeights(
                            heroSkeleton, customSkin, result.CustomSubmeshes,
                            options.FingerLandmarks, options.AlignedTransform,
                            options.GenerateFingerMetacarpals, options.GenerateWristTwist,
                            out var createdFingerBones);
                        if (createdFingerBones.Count > 0)
                        {
                            TagAppendedBonesOnSkeleton(heroSkeleton, activeSlotKey, "finger_chains", createdFingerBones);
                            result.Report.Stats.FingerBoneCount += createdFingerBones.Count;
                            result.Report.Logs.Add($"Generated {createdFingerBones.Count} finger bone(s) affecting {fingerVerts} hand vertices.");
                        }
                    }

                    // Phase 2 (Stage 2): Blend distance weights for any synthetic clothing chains not already hand-painted
                    if (preparedSyntheticChains != null && preparedSyntheticChains.Count > 0)
                    {
                        AddClothingChainTool.ApplySyntheticChainWeights(
                            heroSkeleton, customSkin, result.CustomSubmeshes, preparedSyntheticChains,
                            options.CustomSkinWeightOverrides, result.Report.Warnings, options.SyntheticChainsToRebind);
                        if (addedSyntheticBoneCount > 0)
                        {
                            foreach (var sc in options.BoneChains.Where(c => c != null && c.IsSynthetic && c.AppendedHeroBoneNames.Count > 0))
                            {
                                TagAppendedBonesOnSkeleton(heroSkeleton, activeSlotKey, sc.ChainId, sc.AppendedHeroBoneNames);
                                result.Report.AppendedChains.Add(sc.Name);
                            }
                            result.Report.Stats.ClothBoneCount += addedSyntheticBoneCount;
                        }
                    }

                    // Apply non-destructive triangle cut masks to imported / donor submeshes (preserving vertices & skin weights)
                    if (options.ImportedMeshCutMasks != null && options.ImportedMeshCutMasks.Count > 0 && result.CustomSubmeshes.Count > 0)
                    {
                        ApplyImportedMeshCutMasks(result.CustomSubmeshes, options.ImportedMeshCutMasks);
                    }

                    // Fit or preserve mesh-matched body collision capsules (shared by Godot jiggle preview & Source 2 export)
                    List<BodyCollisionCapsuleDef> activeCapsules = options.BodyCollisionCapsules;
                    if (heroSkeleton != null && GodotObject.IsInstanceValid(heroSkeleton))
                    {
                        if (!isAccessorySlot)
                        {
                            var capsuleMeshes = (options.Method == RiggingMethod.RigidSingleBone)
                                ? CollectMeshes(heroNode)
                                : (result.CustomSubmeshes.Count > 0 ? result.CustomSubmeshes : CollectMeshes(heroNode));
                            activeCapsules = BodyColliderAutoFitter.FitFromRiggedMeshes(
                                heroSkeleton, capsuleMeshes, options.BodyCollisionCapsules,
                                physicsChains: options.BoneChains);
                            options.BodyCollisionCapsules = activeCapsules;
                            heroSkeleton.SetMeta(BodyColliderAutoFitter.SkeletonMetaKey,
                                BodyColliderAutoFitter.SerializeToJson(activeCapsules));
                        }
                        else if (activeCapsules != null && activeCapsules.Count > 0)
                        {
                            // Persist user-adjusted hero body colliders edited while configuring an accessory (e.g. cape)
                            heroSkeleton.SetMeta(BodyColliderAutoFitter.SkeletonMetaKey,
                                BodyColliderAutoFitter.SerializeToJson(activeCapsules));
                        }
                        else
                        {
                            // Preserve existing hero body colliders when adding/editing an accessory with physics,
                            // or auto-fit them from hero geometry if none have been cached yet.
                            if (heroSkeleton.HasMeta(BodyColliderAutoFitter.SkeletonMetaKey))
                            {
                                activeCapsules = BodyColliderAutoFitter.DeserializeFromJson(
                                    heroSkeleton.GetMeta(BodyColliderAutoFitter.SkeletonMetaKey).AsString());
                            }
                            if (activeCapsules == null || activeCapsules.Count == 0)
                            {
                                var heroMeshes = CollectMeshes(heroNode);
                                activeCapsules = BodyColliderAutoFitter.FitFromRiggedMeshes(
                                    heroSkeleton, heroMeshes, physicsChains: options.BoneChains);
                                if (activeCapsules.Count > 0)
                                {
                                    heroSkeleton.SetMeta(BodyColliderAutoFitter.SkeletonMetaKey,
                                        BodyColliderAutoFitter.SerializeToJson(activeCapsules));
                                }
                            }
                            if (activeCapsules != null && activeCapsules.Count > 0)
                            {
                                options.BodyCollisionCapsules = activeCapsules;
                            }
                        }
                    }

                    // Register active Jiggle chains with CustomJiggleBoneDriver only when master physics switch is ON
                    if (heroSkeleton != null && GodotObject.IsInstanceValid(heroSkeleton))
                    {
                        heroSkeleton.SetMeta("CustomPhysicsEnabled", options.EnableCustomPhysics);
                    }
                    var jiggleChains = options.EnableCustomPhysics
                        ? options.BoneChains?
                            .Where(c => c != null && c.Policy == ChainPolicy.PreservePhysicsChain && c.AppendedHeroBoneNames.Count > 0)
                            .ToList()
                        : null;
                    if (jiggleChains != null && jiggleChains.Count > 0)
                    {
                        var driver = CustomJiggleBoneDriver.GetOrAttach(heroNode, heroSkeleton);
                        driver?.SetSimulationEnabled(true);
                        driver?.RegisterChains(activeSlotKey, jiggleChains);
                        if (activeCapsules != null && activeCapsules.Count > 0)
                            driver?.RegisterCollisionCapsules(activeCapsules);
                    }
                    else
                    {
                        var driver = heroNode.FindChild("CustomJiggleBoneDriver", false, false) as CustomJiggleBoneDriver;
                        driver?.RemoveSlot(activeSlotKey);
                        if (driver != null && activeCapsules != null && activeCapsules.Count > 0)
                            driver.RegisterCollisionCapsules(activeCapsules);
                    }

                    FinalizeImportReportStats(result, heroSkeleton);

                    if (result.ContainerNode != null && GodotObject.IsInstanceValid(result.ContainerNode))
                    {
                        result.ContainerNode.SetMeta("IsCustomImportedMesh", true);
                        result.ContainerNode.SetMeta("AccessorySlotId", options.SlotId ?? "");
                        result.ContainerNode.SetMeta("AccessorySlotName", options.AccessoryName ?? "");
                    }
                    ApplyReplacementMode(heroNode, result.CustomSubmeshes, options, result.ContainerNode);
                    GD.Print($"[ImportReport] {result.Report.BuildSummaryLine()}");
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
            finally
            {
                restoreImportedSculpt?.Invoke();
                restoreFlip?.Invoke();
            }
        }

        /// <summary>
        /// Non-destructively applies parametric clothing reshape (Bottom Flare, Top Width, Normal Inflate)
        /// and sparse per-vertex 3D fit sculpt deltas to imported scene meshes before rigging/export.
        /// Returns an <see cref="Action"/> that restores original pristine meshes in <c>finally</c>.
        /// </summary>
        public static Action ApplyImportedMeshSculptToScene(Node3D importedScene, RiggingOptions options)
        {
            if (importedScene == null || !GodotObject.IsInstanceValid(importedScene) || options == null)
                return null;

            bool hasParam = MathF.Abs(options.ImportedMeshBottomFlare) > 1e-4f ||
                            MathF.Abs(options.ImportedMeshTopFlare) > 1e-4f ||
                            MathF.Abs(options.ImportedMeshInflateMeters) > 1e-5f;
            bool hasDeltas = options.ImportedMeshSculptDeltas != null && options.ImportedMeshSculptDeltas.Count > 0;
            if (!hasParam && !hasDeltas) return null;

            var meshes = CollectMeshes(importedScene);
            if (meshes.Count == 0) return null;

            HashSet<string> includedFilter = options.IncludedImportedSubmeshNames != null && options.IncludedImportedSubmeshNames.Count > 0
                ? new HashSet<string>(options.IncludedImportedSubmeshNames, StringComparer.OrdinalIgnoreCase)
                : null;

            // Compute garment scene-space bounding box for parametric Bottom/Top Flare
            float minY = float.MaxValue, maxY = float.MinValue;
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            if (hasParam)
            {
                foreach (var mi in meshes)
                {
                    if (mi?.Mesh == null) continue;
                    if (includedFilter != null && !includedFilter.Contains(mi.Name.ToString())) continue;
                    Transform3D toScene = ComputeTransformRelativeToRoot(mi, importedScene);
                    for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                    {
                        var arr = mi.Mesh.SurfaceGetArrays(s);
                        var verts = arr != null && arr.Count > (int)Mesh.ArrayType.Vertex ? GetVector3Array(arr[(int)Mesh.ArrayType.Vertex]) : null;
                        if (verts == null) continue;
                        for (int i = 0; i < verts.Length; i++)
                        {
                            Vector3 p = toScene * verts[i];
                            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
                            if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
                        }
                    }
                }
            }
            float spanY = MathF.Max(1e-4f, maxY - minY);
            float centerX = minX < maxX ? 0.5f * (minX + maxX) : 0f;
            float centerZ = minZ < maxZ ? 0.5f * (minZ + maxZ) : 0f;
            float rootScaleApprox = MathF.Max(1e-4f, options.RootScale > 1e-4f ? options.RootScale : options.AlignedTransform.Basis.Scale.Length() / MathF.Sqrt(3f));
            float inflateScene = options.ImportedMeshInflateMeters / rootScaleApprox;

            var originals = new List<(MeshInstance3D Mi, Mesh OrigMesh)>();
            foreach (var mi in meshes)
            {
                if (mi?.Mesh == null) continue;
                string mName = mi.Name.ToString();
                if (includedFilter != null && !includedFilter.Contains(mName)) continue;

                Mesh srcMesh = mi.Mesh;
                int surfCount = srcMesh.GetSurfaceCount();
                bool anySurfChanged = hasParam;
                if (!anySurfChanged && hasDeltas)
                {
                    for (int s = 0; s < surfCount; s++)
                    {
                        string sk = MakeSurfaceKeepKey(mName, s);
                        if (options.ImportedMeshSculptDeltas.TryGetValue(sk, out var vm) && vm != null && vm.Count > 0)
                        {
                            anySurfChanged = true;
                            break;
                        }
                    }
                }
                if (!anySurfChanged) continue;

                Transform3D toScene = ComputeTransformRelativeToRoot(mi, importedScene);
                Transform3D sceneToLocal = toScene.AffineInverse();
                Basis normToScene = MathF.Abs(toScene.Basis.Determinant()) > 1e-8f ? toScene.Basis.Inverse().Transposed() : toScene.Basis;

                var rebuilt = new ArrayMesh();
                for (int s = 0; s < surfCount; s++)
                {
                    var arrays = srcMesh.SurfaceGetArrays(s);
                    Material surfMat = srcMesh.SurfaceGetMaterial(s);
                    if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.Vertex) continue;
                    var verts = GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    if (verts == null || verts.Length == 0) continue;

                    var norms = arrays.Count > (int)Mesh.ArrayType.Normal ? GetVector3Array(arrays[(int)Mesh.ArrayType.Normal]) : null;
                    string surfKey = MakeSurfaceKeepKey(mName, s);
                    Dictionary<int, Vector3> surfDeltas = null;
                    if (hasDeltas) options.ImportedMeshSculptDeltas.TryGetValue(surfKey, out surfDeltas);

                    var outVerts = (Vector3[])verts.Clone();
                    for (int v = 0; v < outVerts.Length; v++)
                    {
                        Vector3 localP = outVerts[v];
                        if (hasParam)
                        {
                            Vector3 sp = toScene * localP;
                            float t = Math.Clamp((sp.Y - minY) / spanY, 0f, 1f);
                            float wBot = (1f - t) * (1f - t);
                            float wTop = t * t;
                            float scaleXZ = MathF.Max(0.25f, 1f + options.ImportedMeshBottomFlare * wBot + options.ImportedMeshTopFlare * wTop);
                            sp.X = centerX + (sp.X - centerX) * scaleXZ;
                            sp.Z = centerZ + (sp.Z - centerZ) * scaleXZ;

                            if (MathF.Abs(inflateScene) > 1e-6f && norms != null && v < norms.Length)
                            {
                                Vector3 sn = normToScene * norms[v];
                                if (sn.LengthSquared() > 1e-8f)
                                    sp += sn.Normalized() * inflateScene;
                            }
                            localP = sceneToLocal * sp;
                        }
                        if (surfDeltas != null && surfDeltas.TryGetValue(v, out var dLocal))
                        {
                            localP += dLocal;
                        }
                        outVerts[v] = localP;
                    }

                    arrays[(int)Mesh.ArrayType.Vertex] = outVerts;
                    Mesh.ArrayFormat fmt = default;
                    if (arrays.Count > (int)Mesh.ArrayType.Bones && arrays[(int)Mesh.ArrayType.Bones].VariantType != Variant.Type.Nil)
                    {
                        var bArr = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                        if (bArr != null && bArr.Length == outVerts.Length * 8)
                            fmt |= Mesh.ArrayFormat.FlagUse8BoneWeights;
                    }
                    rebuilt.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, fmt);
                    if (surfMat != null && rebuilt.GetSurfaceCount() > 0)
                        rebuilt.SurfaceSetMaterial(rebuilt.GetSurfaceCount() - 1, surfMat);
                }

                if (rebuilt.GetSurfaceCount() == surfCount)
                {
                    originals.Add((mi, srcMesh));
                    mi.Mesh = rebuilt;
                }
            }

            if (originals.Count == 0) return null;
            return () =>
            {
                foreach (var (mi, orig) in originals)
                {
                    if (mi != null && GodotObject.IsInstanceValid(mi))
                        mi.Mesh = orig;
                }
            };
        }

    }
}
