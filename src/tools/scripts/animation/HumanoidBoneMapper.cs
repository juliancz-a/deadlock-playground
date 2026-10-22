using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace DeadlockPlayground.Animation
{
    /// <summary>
    /// Smart humanoid bone mapper for retargeting external animation rigs (Mixamo, Mesh2Motion,
    /// Unreal Mannequin, Unity Humanoid, Blender Rigify) to Deadlock Source 2 hero skeletons.
    /// </summary>
    public static class HumanoidBoneMapper
    {
        public enum BoneGroup
        {
            Root,
            Pelvis,
            Spine,
            Head,
            LeftArm,
            RightArm,
            LeftLeg,
            RightLeg,
            LeftHandFingers,
            RightHandFingers,
            Secondary
        }

        public class BoneDefinition
        {
            public string CanonicalName { get; }
            public string DisplayLabel { get; }
            public BoneGroup Group { get; }
            public string[] TargetAliases { get; }
            public string[] SourceAliases { get; }

            public BoneDefinition(string canonicalName, string displayLabel, BoneGroup group, string[] targetAliases, string[] sourceAliases)
            {
                CanonicalName = canonicalName;
                DisplayLabel = displayLabel;
                Group = group;
                TargetAliases = targetAliases;
                SourceAliases = sourceAliases;
            }
        }

        public static readonly List<BoneDefinition> Definitions = new()
        {
            // --- Pelvis & Core ---
            new("pelvis", "Pelvis / Hips", BoneGroup.Pelvis,
                new[] { "pelvis", "hip", "hips" },
                new[] { "hips", "pelvis", "root_hips", "b_pelvis", "hip", "bip_pelvis", "c_pelvis" }),

            // --- Spine & Posture ---
            new("spine_0", "Spine 0 (Lower)", BoneGroup.Spine,
                new[] { "spine_0", "spine_01", "spine", "spine0" },
                new[] { "spine", "spine_01", "spine1", "spine.001", "b_spine1", "bip_spine0" }),
            new("spine_1", "Spine 1 (Mid)", BoneGroup.Spine,
                new[] { "spine_1", "spine_02", "spine2" },
                new[] { "spine1", "spine_02", "spine2", "spine.002", "b_spine2", "bip_spine1" }),
            new("spine_2", "Spine 2 (Chest)", BoneGroup.Spine,
                new[] { "spine_2", "spine_03", "spine3", "chest" },
                new[] { "spine2", "spine_03", "spine3", "chest", "spine.003", "b_spine3", "bip_spine2" }),
            new("spine_3", "Spine 3 (Upper Chest)", BoneGroup.Spine,
                new[] { "spine_3", "spine_04", "spine4", "upperchest", "upper_chest" },
                new[] { "spine3", "spine_04", "spine4", "upperchest", "upper_chest", "spine.004", "b_spine4" }),

            // --- Neck & Head ---
            new("neck_0", "Neck", BoneGroup.Head,
                new[] { "neck_0", "neck_01", "neck", "neck0" },
                new[] { "neck", "neck_01", "neck1", "neck.001", "b_neck", "bip_neck" }),
            new("head", "Head", BoneGroup.Head,
                new[] { "head", "head_01", "head0" },
                new[] { "head", "head_01", "head.001", "b_head", "bip_head" }),

            // --- Left Arm Chain ---
            new("clavicle_L", "Left Shoulder / Clavicle", BoneGroup.LeftArm,
                new[] { "clavicle_L", "clavicle_l", "shoulder_L", "shoulder_l" },
                new[] { "leftshoulder", "clavicle_l", "shoulder.l", "l_clavicle", "b_l_clavicle", "l_shoulder", "bip_clavicle_l" }),
            new("arm_upper_L", "Left Upper Arm", BoneGroup.LeftArm,
                new[] { "arm_upper_L", "arm_upper_l", "upperarm_l", "upper_arm_l", "arm_l" },
                new[] { "leftarm", "upperarm_l", "upper_arm.l", "l_upperarm", "b_l_arm", "left_arm", "arm_l", "bip_arm_l" }),
            new("arm_lower_L", "Left Forearm", BoneGroup.LeftArm,
                new[] { "arm_lower_L", "arm_lower_l", "lowerarm_l", "lower_arm_l", "forearm_l" },
                new[] { "leftforearm", "lowerarm_l", "forearm.l", "l_lowerarm", "b_l_forearm", "left_forearm", "forearm_l", "bip_forearm_l" }),
            new("hand_L", "Left Hand / Wrist", BoneGroup.LeftArm,
                new[] { "hand_L", "hand_l", "wrist_L", "wrist_l" },
                new[] { "lefthand", "hand_l", "hand.l", "l_hand", "b_l_hand", "left_hand", "wrist_l", "bip_hand_l" }),

            // --- Right Arm Chain ---
            new("clavicle_R", "Right Shoulder / Clavicle", BoneGroup.RightArm,
                new[] { "clavicle_R", "clavicle_r", "shoulder_R", "shoulder_r" },
                new[] { "rightshoulder", "clavicle_r", "shoulder.r", "r_clavicle", "b_r_clavicle", "r_shoulder", "bip_clavicle_r" }),
            new("arm_upper_R", "Right Upper Arm", BoneGroup.RightArm,
                new[] { "arm_upper_R", "arm_upper_r", "upperarm_r", "upper_arm_r", "arm_r" },
                new[] { "rightarm", "upperarm_r", "upper_arm.r", "r_upperarm", "b_r_arm", "right_arm", "arm_r", "bip_arm_r" }),
            new("arm_lower_R", "Right Forearm", BoneGroup.RightArm,
                new[] { "arm_lower_R", "arm_lower_r", "lowerarm_r", "lower_arm_r", "forearm_r" },
                new[] { "rightforearm", "lowerarm_r", "forearm.r", "r_lowerarm", "b_r_forearm", "right_forearm", "forearm_r", "bip_forearm_r" }),
            new("hand_R", "Right Hand / Wrist", BoneGroup.RightArm,
                new[] { "hand_R", "hand_r", "wrist_R", "wrist_r" },
                new[] { "righthand", "hand_r", "hand.r", "r_hand", "b_r_hand", "right_hand", "wrist_r", "bip_hand_r" }),

            // --- Left Leg Chain ---
            new("leg_upper_L", "Left Thigh / UpLeg", BoneGroup.LeftLeg,
                new[] { "leg_upper_L", "leg_upper_l", "thigh_L", "thigh_l", "leg_l" },
                new[] { "leftupleg", "thigh_l", "thigh.l", "l_thigh", "b_l_thigh", "left_thigh", "leg_l", "bip_leg_l" }),
            new("leg_lower_L", "Left Calf / Shin", BoneGroup.LeftLeg,
                new[] { "leg_lower_L", "leg_lower_l", "calf_L", "calf_l", "knee_l", "shin_l" },
                new[] { "leftleg", "calf_l", "shin.l", "l_calf", "b_l_calf", "left_calf", "bip_calf_l" }),
            new("ankle_L", "Left Foot / Ankle", BoneGroup.LeftLeg,
                new[] { "ankle_L", "ankle_l", "foot_L", "foot_l" },
                new[] { "leftfoot", "foot_l", "foot.l", "l_foot", "b_l_foot", "left_foot", "ankle_l", "bip_foot_l" }),
            new("ball_L", "Left Toe / Ball", BoneGroup.LeftLeg,
                new[] { "ball_L", "ball_l", "toe_L", "toe_l", "toes_l" },
                new[] { "lefttoebase", "ball_l", "toe.l", "l_toe", "b_l_toe", "left_toe", "bip_toe_l" }),

            // --- Right Leg Chain ---
            new("leg_upper_R", "Right Thigh / UpLeg", BoneGroup.RightLeg,
                new[] { "leg_upper_R", "leg_upper_r", "thigh_R", "thigh_r", "leg_r" },
                new[] { "rightupleg", "thigh_r", "thigh.r", "r_thigh", "b_r_thigh", "right_thigh", "leg_r", "bip_leg_r" }),
            new("leg_lower_R", "Right Calf / Shin", BoneGroup.RightLeg,
                new[] { "leg_lower_R", "leg_lower_r", "calf_R", "calf_r", "knee_r", "shin_r" },
                new[] { "rightleg", "calf_r", "shin.r", "r_calf", "b_r_calf", "right_calf", "bip_calf_r" }),
            new("ankle_R", "Right Foot / Ankle", BoneGroup.RightLeg,
                new[] { "ankle_R", "ankle_r", "foot_R", "foot_r" },
                new[] { "rightfoot", "foot_r", "foot.r", "r_foot", "b_r_foot", "right_foot", "ankle_r", "bip_foot_r" }),
            new("ball_R", "Right Toe / Ball", BoneGroup.RightLeg,
                new[] { "ball_R", "ball_r", "toe_R", "toe_r", "toes_r" },
                new[] { "righttoebase", "ball_r", "toe.r", "r_toe", "b_r_toe", "right_toe", "bip_toe_r" }),

            // --- Left Hand Fingers ---
            new("finger_thumb_0_L", "Left Thumb 1", BoneGroup.LeftHandFingers,
                new[] { "finger_thumb_0_L", "finger_thumb_0_l", "thumb_01_l", "thumb_0_l" },
                new[] { "lefthandthumb1", "thumb_01_l", "thumb1_l", "thumb.01.l", "f_thumb01_l" }),
            new("finger_thumb_1_L", "Left Thumb 2", BoneGroup.LeftHandFingers,
                new[] { "finger_thumb_1_L", "finger_thumb_1_l", "thumb_02_l", "thumb_1_l" },
                new[] { "lefthandthumb2", "thumb_02_l", "thumb2_l", "thumb.02.l", "f_thumb02_l" }),
            new("finger_thumb_2_L", "Left Thumb 3", BoneGroup.LeftHandFingers,
                new[] { "finger_thumb_2_L", "finger_thumb_2_l", "thumb_03_l", "thumb_2_l" },
                new[] { "lefthandthumb3", "thumb_03_l", "thumb3_l", "thumb.03.l", "f_thumb03_l" }),

            new("finger_index_0_L", "Left Index 1", BoneGroup.LeftHandFingers,
                new[] { "finger_index_0_L", "finger_index_0_l", "index_01_l", "index_0_l" },
                new[] { "lefthandindex1", "index_01_l", "index1_l", "index.01.l", "f_index01_l" }),
            new("finger_index_1_L", "Left Index 2", BoneGroup.LeftHandFingers,
                new[] { "finger_index_1_L", "finger_index_1_l", "index_02_l", "index_1_l" },
                new[] { "lefthandindex2", "index_02_l", "index2_l", "index.02.l", "f_index02_l" }),
            new("finger_index_2_L", "Left Index 3", BoneGroup.LeftHandFingers,
                new[] { "finger_index_2_L", "finger_index_2_l", "index_03_l", "index_2_l" },
                new[] { "lefthandindex3", "index_03_l", "index3_l", "index.03.l", "f_index03_l" }),

            new("finger_middle_0_L", "Left Middle 1", BoneGroup.LeftHandFingers,
                new[] { "finger_middle_0_L", "finger_middle_0_l", "middle_01_l", "middle_0_l" },
                new[] { "lefthandmiddle1", "middle_01_l", "middle1_l", "middle.01.l", "f_middle01_l" }),
            new("finger_middle_1_L", "Left Middle 2", BoneGroup.LeftHandFingers,
                new[] { "finger_middle_1_L", "finger_middle_1_l", "middle_02_l", "middle_1_l" },
                new[] { "lefthandmiddle2", "middle_02_l", "middle2_l", "middle.02.l", "f_middle02_l" }),
            new("finger_middle_2_L", "Left Middle 3", BoneGroup.LeftHandFingers,
                new[] { "finger_middle_2_L", "finger_middle_2_l", "middle_03_l", "middle_2_l" },
                new[] { "lefthandmiddle3", "middle_03_l", "middle3_l", "middle.03.l", "f_middle03_l" }),

            new("finger_ring_0_L", "Left Ring 1", BoneGroup.LeftHandFingers,
                new[] { "finger_ring_0_L", "finger_ring_0_l", "ring_01_l", "ring_0_l" },
                new[] { "lefthandring1", "ring_01_l", "ring1_l", "ring.01.l", "f_ring01_l" }),
            new("finger_ring_1_L", "Left Ring 2", BoneGroup.LeftHandFingers,
                new[] { "finger_ring_1_L", "finger_ring_1_l", "ring_02_l", "ring_1_l" },
                new[] { "lefthandring2", "ring_02_l", "ring2_l", "ring.02.l", "f_ring02_l" }),
            new("finger_ring_2_L", "Left Ring 3", BoneGroup.LeftHandFingers,
                new[] { "finger_ring_2_L", "finger_ring_2_l", "ring_03_l", "ring_2_l" },
                new[] { "lefthandring3", "ring_03_l", "ring3_l", "ring.03.l", "f_ring03_l" }),

            new("finger_pinky_0_L", "Left Pinky 1", BoneGroup.LeftHandFingers,
                new[] { "finger_pinky_0_L", "finger_pinky_0_l", "pinky_01_l", "pinky_0_l" },
                new[] { "lefthandpinky1", "pinky_01_l", "pinky1_l", "pinky.01.l", "f_pinky01_l", "little_01_l" }),
            new("finger_pinky_1_L", "Left Pinky 2", BoneGroup.LeftHandFingers,
                new[] { "finger_pinky_1_L", "finger_pinky_1_l", "pinky_02_l", "pinky_1_l" },
                new[] { "lefthandpinky2", "pinky_02_l", "pinky2_l", "pinky.02.l", "f_pinky02_l", "little_02_l" }),
            new("finger_pinky_2_L", "Left Pinky 3", BoneGroup.LeftHandFingers,
                new[] { "finger_pinky_2_L", "finger_pinky_2_l", "pinky_03_l", "pinky_2_l" },
                new[] { "lefthandpinky3", "pinky_03_l", "pinky3_l", "pinky.03.l", "f_pinky03_l", "little_03_l" }),

            // --- Right Hand Fingers ---
            new("finger_thumb_0_R", "Right Thumb 1", BoneGroup.RightHandFingers,
                new[] { "finger_thumb_0_R", "finger_thumb_0_r", "thumb_01_r", "thumb_0_r" },
                new[] { "righthandthumb1", "thumb_01_r", "thumb1_r", "thumb.01.r", "f_thumb01_r" }),
            new("finger_thumb_1_R", "Right Thumb 2", BoneGroup.RightHandFingers,
                new[] { "finger_thumb_1_R", "finger_thumb_1_r", "thumb_02_r", "thumb_1_r" },
                new[] { "righthandthumb2", "thumb_02_r", "thumb2_r", "thumb.02.r", "f_thumb02_r" }),
            new("finger_thumb_2_R", "Right Thumb 3", BoneGroup.RightHandFingers,
                new[] { "finger_thumb_2_R", "finger_thumb_2_r", "thumb_03_r", "thumb_2_r" },
                new[] { "righthandthumb3", "thumb_03_r", "thumb3_r", "thumb.03.r", "f_thumb03_r" }),

            new("finger_index_0_R", "Right Index 1", BoneGroup.RightHandFingers,
                new[] { "finger_index_0_R", "finger_index_0_r", "index_01_r", "index_0_r" },
                new[] { "righthandindex1", "index_01_r", "index1_r", "index.01.r", "f_index01_r" }),
            new("finger_index_1_R", "Right Index 2", BoneGroup.RightHandFingers,
                new[] { "finger_index_1_R", "finger_index_1_r", "index_02_r", "index_1_r" },
                new[] { "righthandindex2", "index_02_r", "index2_r", "index.02.r", "f_index02_r" }),
            new("finger_index_2_R", "Right Index 3", BoneGroup.RightHandFingers,
                new[] { "finger_index_2_R", "finger_index_2_r", "index_03_r", "index_2_r" },
                new[] { "righthandindex3", "index_03_r", "index3_r", "index.03.r", "f_index03_r" }),

            new("finger_middle_0_R", "Right Middle 1", BoneGroup.RightHandFingers,
                new[] { "finger_middle_0_R", "finger_middle_0_r", "middle_01_r", "middle_0_r" },
                new[] { "righthandmiddle1", "middle_01_r", "middle1_r", "middle.01.r", "f_middle01_r" }),
            new("finger_middle_1_R", "Right Middle 2", BoneGroup.RightHandFingers,
                new[] { "finger_middle_1_R", "finger_middle_1_r", "middle_02_r", "middle_1_r" },
                new[] { "righthandmiddle2", "middle_02_r", "middle2_r", "middle.02.r", "f_middle02_r" }),
            new("finger_middle_2_R", "Right Middle 3", BoneGroup.RightHandFingers,
                new[] { "finger_middle_2_R", "finger_middle_2_r", "middle_03_r", "middle_2_r" },
                new[] { "righthandmiddle3", "middle_03_r", "middle3_r", "middle.03.r", "f_middle03_r" }),

            new("finger_ring_0_R", "Right Ring 1", BoneGroup.RightHandFingers,
                new[] { "finger_ring_0_R", "finger_ring_0_r", "ring_01_r", "ring_0_r" },
                new[] { "righthandring1", "ring_01_r", "ring1_r", "ring.01.r", "f_ring01_r" }),
            new("finger_ring_1_R", "Right Ring 2", BoneGroup.RightHandFingers,
                new[] { "finger_ring_1_R", "finger_ring_1_r", "ring_02_r", "ring_1_r" },
                new[] { "righthandring2", "ring_02_r", "ring2_r", "ring.02.r", "f_ring02_r" }),
            new("finger_ring_2_R", "Right Ring 3", BoneGroup.RightHandFingers,
                new[] { "finger_ring_2_R", "finger_ring_2_r", "ring_03_r", "ring_2_r" },
                new[] { "righthandring3", "ring_03_r", "ring3_r", "ring.03.r", "f_ring03_r" }),

            new("finger_pinky_0_R", "Right Pinky 1", BoneGroup.RightHandFingers,
                new[] { "finger_pinky_0_R", "finger_pinky_0_r", "pinky_01_r", "pinky_0_r" },
                new[] { "righthandpinky1", "pinky_01_r", "pinky1_r", "pinky.01.r", "f_pinky01_r", "little_01_r" }),
            new("finger_pinky_1_R", "Right Pinky 2", BoneGroup.RightHandFingers,
                new[] { "finger_pinky_1_R", "finger_pinky_1_r", "pinky_02_r", "pinky_1_r" },
                new[] { "righthandpinky2", "pinky_02_r", "pinky2_r", "pinky.02.r", "f_pinky02_r", "little_02_r" }),
            new("finger_pinky_2_R", "Right Pinky 3", BoneGroup.RightHandFingers,
                new[] { "finger_pinky_2_R", "finger_pinky_2_r", "pinky_03_r", "pinky_2_r" },
                new[] { "righthandpinky3", "pinky_03_r", "pinky3_r", "pinky.03.r", "f_pinky03_r", "little_03_r" })
        };

        /// <summary>
        /// Cleans and sanitizes bone name for fuzzy token matching.
        /// Strips namespace prefixes like "mixamorig:", "b_", "def_", "j_", "root|", etc.
        /// </summary>
        public static string CleanBoneName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return "";

            string name = rawName.Trim();

            // Strip namespaces / prefixes
            int colonIdx = name.LastIndexOf(':');
            if (colonIdx != -1) name = name[(colonIdx + 1)..];

            int barIdx = name.LastIndexOf('|');
            if (barIdx != -1) name = name[(barIdx + 1)..];

            int slashIdx = name.LastIndexOf('/');
            if (slashIdx != -1) name = name[(slashIdx + 1)..];

            // Strip standard prefixes
            if (name.StartsWith("mixamorig_", StringComparison.OrdinalIgnoreCase)) name = name[10..];
            else if (name.StartsWith("mixamo_", StringComparison.OrdinalIgnoreCase)) name = name[7..];
            else if (name.StartsWith("skel_", StringComparison.OrdinalIgnoreCase)) name = name[5..];
            else if (name.StartsWith("rig_", StringComparison.OrdinalIgnoreCase)) name = name[4..];
            else if (name.StartsWith("b_", StringComparison.OrdinalIgnoreCase)) name = name[2..];
            else if (name.StartsWith("def_", StringComparison.OrdinalIgnoreCase)) name = name[4..];
            else if (name.StartsWith("j_", StringComparison.OrdinalIgnoreCase)) name = name[2..];
            else if (name.StartsWith("bip_", StringComparison.OrdinalIgnoreCase)) name = name[4..];
            else if (name.StartsWith("cc_", StringComparison.OrdinalIgnoreCase)) name = name[3..];
            else if (name.StartsWith("m_avg_", StringComparison.OrdinalIgnoreCase)) name = name[6..];
            else if (name.StartsWith("m_", StringComparison.OrdinalIgnoreCase)) name = name[2..];

            // Normalize delimiters: replace dots and hyphens with underscores
            name = name.Replace('.', '_').Replace('-', '_');

            return name.ToLowerInvariant();
        }

        /// <summary>
        /// Finds the best target bone on the Deadlock skeleton corresponding to a BoneDefinition.
        /// </summary>
        public static string ResolveTargetBone(Skeleton3D targetSkeleton, BoneDefinition def)
        {
            if (targetSkeleton == null) return null;

            // 1. Direct canonical name match
            if (targetSkeleton.FindBone(def.CanonicalName) != -1)
            {
                return def.CanonicalName;
            }

            // 2. Exact match against target aliases
            foreach (var alias in def.TargetAliases)
            {
                int idx = targetSkeleton.FindBone(alias);
                if (idx != -1) return targetSkeleton.GetBoneName(idx);
            }

            // 3. Case-insensitive / normalized match across all bones in target skeleton
            int totalBones = targetSkeleton.GetBoneCount();
            for (int b = 0; b < totalBones; b++)
            {
                string tName = targetSkeleton.GetBoneName(b);
                string cleanT = CleanBoneName(tName);

                foreach (var alias in def.TargetAliases)
                {
                    string cleanAlias = CleanBoneName(alias);
                    if (cleanT == cleanAlias) return tName;
                }
            }

            return null;
        }

        /// <summary>
        /// Finds the best source bone on the source skeleton corresponding to a BoneDefinition.
        /// </summary>
        public static string ResolveSourceBone(Skeleton3D sourceSkeleton, BoneDefinition def)
        {
            if (sourceSkeleton == null) return null;

            int totalSource = sourceSkeleton.GetBoneCount();
            var sourceBones = new List<string>(totalSource);
            for (int s = 0; s < totalSource; s++)
            {
                sourceBones.Add(sourceSkeleton.GetBoneName(s));
            }

            // 1. Exact match with any source alias
            foreach (var alias in def.SourceAliases)
            {
                var match = sourceBones.FirstOrDefault(b => string.Equals(b, alias, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }

            // 2. Normalized match (cleaning prefixes like mixamorig:, bip_, etc.)
            foreach (var alias in def.SourceAliases)
            {
                string cleanAlias = CleanBoneName(alias);
                var match = sourceBones.FirstOrDefault(b => CleanBoneName(b) == cleanAlias);
                if (match != null) return match;
            }

            // 3. Substring matching for compound names (e.g. "LeftHandIndex1", "thigh_l", "clavicle_l")
            foreach (var alias in def.SourceAliases)
            {
                string cleanAlias = CleanBoneName(alias);
                var match = sourceBones.FirstOrDefault(b =>
                {
                    string c = CleanBoneName(b);
                    return c.Contains(cleanAlias) || cleanAlias.Contains(c);
                });
                if (match != null) return match;
            }

            return null;
        }

        /// <summary>
        /// Generates a complete auto-mapping dictionary: [TargetDeadlockBoneName] => [SourceBoneName].
        /// </summary>
        public static Dictionary<string, string> AutoMap(Skeleton3D sourceSkeleton, Skeleton3D targetSkeleton)
        {
            var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (targetSkeleton == null || sourceSkeleton == null) return mapping;

            var usedSourceBones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var def in Definitions)
            {
                string targetBone = ResolveTargetBone(targetSkeleton, def);
                if (string.IsNullOrEmpty(targetBone)) continue;

                string sourceBone = ResolveSourceBone(sourceSkeleton, def);
                if (!string.IsNullOrEmpty(sourceBone) && !usedSourceBones.Contains(sourceBone))
                {
                    mapping[targetBone] = sourceBone;
                    usedSourceBones.Add(sourceBone);
                }
                else
                {
                    // Available on target skeleton but unmapped
                    mapping[targetBone] = "";
                }
            }

            // Adjust 3-spine rigs (Mixamo: Spine, Spine1, Spine2 / Mesh2Motion: spine_01, spine_02, spine_03)
            // mapped onto 4-spine target rigs (Deadlock: spine_0, spine_1, spine_2, spine_3).
            AdjustThreeSpineToFourSpine(mapping, sourceSkeleton, targetSkeleton);

            return mapping;
        }

        private static void AdjustThreeSpineToFourSpine(Dictionary<string, string> mapping, Skeleton3D sourceSkeleton, Skeleton3D targetSkeleton)
        {
            if (sourceSkeleton == null || targetSkeleton == null) return;

            // Target must have spine_3, and currently be unmapped
            int targetSpine3Idx = targetSkeleton.FindBone("spine_3");
            if (targetSpine3Idx == -1) return;

            string targetSpine3Name = targetSkeleton.GetBoneName(targetSpine3Idx);
            if (mapping.TryGetValue(targetSpine3Name, out string mapped3) && !string.IsNullOrEmpty(mapped3))
            {
                return; // Already mapped (e.g. 4+ spine rig like Unreal Mannequin)
            }

            // Check if target has spine_2 mapped to top spine
            int targetSpine2Idx = targetSkeleton.FindBone("spine_2");
            if (targetSpine2Idx == -1) return;
            string targetSpine2Name = targetSkeleton.GetBoneName(targetSpine2Idx);

            if (!mapping.TryGetValue(targetSpine2Name, out string mapped2) || string.IsNullOrEmpty(mapped2))
            {
                return;
            }

            // Top source spine (mapped to spine_2) is where clavicles and neck attach on 3-spine rigs.
            // Shift mapped2 to spine_3 (Upper Chest), and connect spine_2 to the mid spine.
            int targetSpine1Idx = targetSkeleton.FindBone("spine_1");
            string targetSpine1Name = targetSpine1Idx != -1 ? targetSkeleton.GetBoneName(targetSpine1Idx) : "spine_1";
            mapping.TryGetValue(targetSpine1Name, out string mapped1);

            int targetSpine0Idx = targetSkeleton.FindBone("spine_0");
            string targetSpine0Name = targetSpine0Idx != -1 ? targetSkeleton.GetBoneName(targetSpine0Idx) : "spine_0";
            mapping.TryGetValue(targetSpine0Name, out string mapped0);

            // Shift top spine to spine_3 (Upper Chest) so Clavicles and Neck curl forward with the chest
            mapping[targetSpine3Name] = mapped2;

            // Mid spine becomes spine_2 (Chest)
            mapping[targetSpine2Name] = !string.IsNullOrEmpty(mapped1) ? mapped1 : "";

            // Lower-mid spine_1 is left unmapped so it smoothly maintains its natural rest curvature relative to spine_0
            // rather than duplicating mapped1 (which causes 2x forward thoracic hunch, collapsing arms into jackets)
            mapping[targetSpine1Name] = "";
        }

        /// <summary>
        /// Returns UI color coding for each bone group to enhance table readability.
        /// </summary>
        public static Color GetGroupColor(BoneGroup group)
        {
            return group switch
            {
                BoneGroup.Pelvis => new Color(0.95f, 0.70f, 0.20f),      // Gold / Amber
                BoneGroup.Spine => new Color(0.35f, 0.75f, 0.95f),       // Cyan
                BoneGroup.Head => new Color(0.85f, 0.45f, 0.95f),        // Purple / Magenta
                BoneGroup.LeftArm => new Color(0.30f, 0.85f, 0.45f),     // Green
                BoneGroup.RightArm => new Color(0.30f, 0.60f, 0.95f),    // Blue
                BoneGroup.LeftLeg => new Color(0.85f, 0.60f, 0.30f),     // Orange
                BoneGroup.RightLeg => new Color(0.95f, 0.45f, 0.35f),    // Coral / Red
                BoneGroup.LeftHandFingers => new Color(0.50f, 0.85f, 0.70f), // Mint
                BoneGroup.RightHandFingers => new Color(0.60f, 0.70f, 0.95f),// Soft Blue
                _ => new Color(0.70f, 0.70f, 0.70f)
            };
        }
    }
}
