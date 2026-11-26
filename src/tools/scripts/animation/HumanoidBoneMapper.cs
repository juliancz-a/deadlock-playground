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
                new[] { "hips", "pelvis", "humanoidrootpart", "root_hips", "b_pelvis", "hip", "bip_pelvis", "c_pelvis", "def_spine", "def-spine", "def-pelvis.l", "def-pelvis.r", "def_pelvis_l", "def_pelvis_r", "pelvis.l", "pelvis.r" }),

            // --- Spine & Posture ---
            new("spine_0", "Spine 0 (Lower)", BoneGroup.Spine,
                new[] { "spine_0", "spine_01", "spine", "spine0" },
                new[] { "spine.001", "spine_01", "spine1", "lowertorso", "lower_torso", "def-spine.001", "def_spine_001", "b_spine1", "bip_spine0", "spine" }),
            new("spine_1", "Spine 1 (Mid)", BoneGroup.Spine,
                new[] { "spine_1", "spine_02", "spine2" },
                new[] { "spine.002", "spine_02", "spine2", "def-spine.002", "def_spine_002", "b_spine2", "bip_spine1" }),
            new("spine_2", "Spine 2 (Chest)", BoneGroup.Spine,
                new[] { "spine_2", "spine_03", "spine3", "chest" },
                new[] { "spine.003", "spine_03", "spine3", "chest", "uppertorso", "upper_torso", "def-spine.003", "def_spine_003", "b_spine3", "bip_spine2" }),
            new("spine_3", "Spine 3 (Upper Chest)", BoneGroup.Spine,
                new[] { "spine_3", "spine_04", "spine4", "upperchest", "upper_chest" },
                new[] { "spine.004", "spine_04", "spine4", "upperchest", "upper_chest", "def-spine.004", "def_spine_004", "b_spine4" }),

            // --- Neck & Head ---
            new("neck_0", "Neck", BoneGroup.Head,
                new[] { "neck_0", "neck_01", "neck", "neck0" },
                new[] { "neck", "neck_01", "neck1", "neck.001", "spine.005", "def-spine.005", "def_spine_005", "b_neck", "bip_neck" }),
            new("head", "Head", BoneGroup.Head,
                new[] { "head", "head_01", "head0" },
                new[] { "head", "head_01", "head.001", "head1", "head_1", "head_0", "bip01_head1", "spine.006", "def-spine.006", "def_spine_006", "b_head", "bip_head" }),

            // --- Left Arm Chain ---
            new("clavicle_L", "Left Shoulder / Clavicle", BoneGroup.LeftArm,
                new[] { "clavicle_L", "clavicle_l", "shoulder_L", "shoulder_l" },
                new[] { "leftshoulder", "clavicle_l", "shoulder.l", "shoulder_l", "l_clavicle", "b_l_clavicle", "l_shoulder", "bip_clavicle_l", "def-shoulder.l", "def_shoulder_l" }),
            new("arm_upper_L", "Left Upper Arm", BoneGroup.LeftArm,
                new[] { "arm_upper_L", "arm_upper_l", "upperarm_l", "upper_arm_l", "arm_l" },
                new[] { "leftarm", "leftupperarm", "left_upper_arm", "upperarm_l", "upper_arm.l", "upper_arm_l", "l_upperarm", "b_l_arm", "left_arm", "arm_l", "bip_arm_l", "bip_upperarm_l", "def-upper_arm.l", "def_upper_arm_l", "def-upper_arm.l.001", "def_upper_arm_l_001", "upper_arm.l.001" }),
            new("arm_lower_L", "Left Forearm", BoneGroup.LeftArm,
                new[] { "arm_lower_L", "arm_lower_l", "lowerarm_l", "lower_arm_l", "forearm_l" },
                new[] { "leftforearm", "leftlowerarm", "left_lower_arm", "lowerarm_l", "forearm.l", "forearm_l", "l_lowerarm", "l_forearm", "b_l_forearm", "left_forearm", "leftelbow", "left_elbow", "elbow_l", "elbow.l", "l_elbow", "bip_forearm_l", "def-forearm.l", "def_forearm_l", "def-forearm.l.001", "def_forearm_l_001", "forearm.l.001" }),
            new("hand_L", "Left Hand / Wrist", BoneGroup.LeftArm,
                new[] { "hand_L", "hand_l", "wrist_L", "wrist_l" },
                new[] { "lefthand", "hand_l", "hand.l", "l_hand", "b_l_hand", "left_hand", "leftwrist", "wrist_l", "bip_hand_l", "def-hand.l", "def_hand_l" }),

            // --- Right Arm Chain ---
            new("clavicle_R", "Right Shoulder / Clavicle", BoneGroup.RightArm,
                new[] { "clavicle_R", "clavicle_r", "shoulder_R", "shoulder_r" },
                new[] { "rightshoulder", "clavicle_r", "shoulder.r", "shoulder_r", "r_clavicle", "b_r_clavicle", "r_shoulder", "bip_clavicle_r", "def-shoulder.r", "def_shoulder_r" }),
            new("arm_upper_R", "Right Upper Arm", BoneGroup.RightArm,
                new[] { "arm_upper_R", "arm_upper_r", "upperarm_r", "upper_arm_r", "arm_r" },
                new[] { "rightarm", "rightupperarm", "right_upper_arm", "upperarm_r", "upper_arm.r", "upper_arm_r", "r_upperarm", "b_r_arm", "right_arm", "arm_r", "bip_arm_r", "bip_upperarm_r", "def-upper_arm.r", "def_upper_arm_r", "def-upper_arm.r.001", "def_upper_arm_r_001", "upper_arm.r.001" }),
            new("arm_lower_R", "Right Forearm", BoneGroup.RightArm,
                new[] { "arm_lower_R", "arm_lower_r", "lowerarm_r", "lower_arm_r", "forearm_r" },
                new[] { "rightforearm", "rightlowerarm", "right_lower_arm", "lowerarm_r", "forearm.r", "forearm_r", "r_lowerarm", "r_forearm", "b_r_forearm", "right_forearm", "rightelbow", "right_elbow", "elbow_r", "elbow.r", "r_elbow", "bip_forearm_r", "def-forearm.r", "def_forearm_r", "def-forearm.r.001", "def_forearm_r_001", "forearm.r.001" }),
            new("hand_R", "Right Hand / Wrist", BoneGroup.RightArm,
                new[] { "hand_R", "hand_r", "wrist_R", "wrist_r" },
                new[] { "righthand", "hand_r", "hand.r", "r_hand", "b_r_hand", "right_hand", "rightwrist", "wrist_r", "bip_hand_r", "def-hand.r", "def_hand_r" }),

            // --- Left Leg Chain ---
            new("leg_upper_L", "Left Thigh / UpLeg", BoneGroup.LeftLeg,
                new[] { "leg_upper_L", "leg_upper_l", "thigh_L", "thigh_l", "leg_l" },
                new[] { "leftupleg", "leftupperleg", "left_upper_leg", "thigh_l", "thigh.l", "l_thigh", "b_l_thigh", "left_thigh", "leg_l", "bip_leg_l", "def-thigh.l", "def_thigh_l", "def-thigh.l.001", "def_thigh_l_001", "thigh.l.001" }),
            new("leg_lower_L", "Left Calf / Shin", BoneGroup.LeftLeg,
                new[] { "leg_lower_L", "leg_lower_l", "calf_L", "calf_l", "knee_l", "shin_l" },
                new[] { "leftleg", "leftlowerleg", "left_lower_leg", "calf_l", "shin.l", "shin_l", "l_calf", "b_l_calf", "left_calf", "leftknee", "left_knee", "knee_l", "knee.l", "l_knee", "bip_calf_l", "def-shin.l", "def_shin_l", "def-shin.l.001", "def_shin_l_001", "shin.l.001" }),
            new("ankle_L", "Left Foot / Ankle", BoneGroup.LeftLeg,
                new[] { "ankle_L", "ankle_l", "foot_L", "foot_l" },
                new[] { "leftfoot", "foot_l", "foot.l", "l_foot", "b_l_foot", "left_foot", "leftankle", "left_ankle", "ankle_l", "ankle.l", "bip_foot_l", "def-foot.l", "def_foot_l" }),
            new("ball_L", "Left Toe / Ball", BoneGroup.LeftLeg,
                new[] { "ball_L", "ball_l", "toe_L", "toe_l", "toes_l" },
                new[] { "lefttoebase", "ball_l", "toe.l", "toe_l", "l_toe", "l_toe0", "toe0_l", "b_l_toe", "left_toe", "bip_toe_l", "def-toe.l", "def_toe_l" }),
            new("ball_end_L", "Left Toe End / Tip", BoneGroup.LeftLeg,
                new[] { "ball_end_L", "ball_end_l", "toe_end_L", "toe_end_l" },
                new[] { "lefttoe_end", "lefttoeend", "ball_leaf_l", "ball_left_end", "ball_end_l", "toe_end_l", "l_toe0_end", "toe0_end_l", "l_toe0nub" }),

            // --- Right Leg Chain ---
            new("leg_upper_R", "Right Thigh / UpLeg", BoneGroup.RightLeg,
                new[] { "leg_upper_R", "leg_upper_r", "thigh_R", "thigh_r", "leg_r" },
                new[] { "rightupleg", "rightupperleg", "right_upper_leg", "thigh_r", "thigh.r", "r_thigh", "b_r_thigh", "right_thigh", "leg_r", "bip_leg_r", "def-thigh.r", "def_thigh_r", "def-thigh.r.001", "def_thigh_r_001", "thigh.r.001" }),
            new("leg_lower_R", "Right Calf / Shin", BoneGroup.RightLeg,
                new[] { "leg_lower_R", "leg_lower_r", "calf_R", "calf_r", "knee_r", "shin_r" },
                new[] { "rightleg", "rightlowerleg", "right_lower_leg", "calf_r", "shin.r", "shin_r", "r_calf", "b_r_calf", "right_calf", "rightknee", "right_knee", "knee_r", "knee.r", "r_knee", "bip_calf_r", "def-shin.r", "def_shin_r", "def-shin.r.001", "def_shin_r_001", "shin.r.001" }),
            new("ankle_R", "Right Foot / Ankle", BoneGroup.RightLeg,
                new[] { "ankle_R", "ankle_r", "foot_R", "foot_r" },
                new[] { "rightfoot", "foot_r", "foot.r", "r_foot", "b_r_foot", "right_foot", "rightankle", "right_ankle", "ankle_r", "ankle.r", "bip_foot_r", "def-foot.r", "def_foot_r" }),
            new("ball_R", "Right Toe / Ball", BoneGroup.RightLeg,
                new[] { "ball_R", "ball_r", "toe_R", "toe_r", "toes_r" },
                new[] { "righttoebase", "ball_r", "toe.r", "toe_r", "r_toe", "r_toe0", "toe0_r", "b_r_toe", "right_toe", "bip_toe_r", "def-toe.r", "def_toe_r" }),
            new("ball_end_R", "Right Toe End / Tip", BoneGroup.RightLeg,
                new[] { "ball_end_R", "ball_end_r", "toe_end_R", "toe_end_r" },
                new[] { "righttoe_end", "righttoeend", "ball_leaf_r", "ball_right_end", "ball_end_r", "toe_end_r", "r_toe0_end", "toe0_end_r", "r_toe0nub" }),

            // --- Left Hand Fingers ---
            new("finger_thumb_meta_L", "Left Thumb Metacarpal", BoneGroup.LeftHandFingers,
                new[] { "finger_thumb_meta_L", "finger_thumb_meta_l" },
                Array.Empty<string>()),
            new("finger_thumb_0_L", "Left Thumb 1", BoneGroup.LeftHandFingers,
                new[] { "finger_thumb_0_L", "finger_thumb_0_l", "thumb_01_l", "thumb_0_l" },
                new[] { "lefthandthumb1", "thumb_01_l", "thumb1_l", "thumb0_l", "thumb.01.l", "f_thumb01_l", "def-thumb.01.l", "def_thumb_01_l", "f_thumb.01.l", "l_finger0", "finger0_l" }),
            new("finger_thumb_1_L", "Left Thumb 2", BoneGroup.LeftHandFingers,
                new[] { "finger_thumb_1_L", "finger_thumb_1_l", "thumb_02_l", "thumb_1_l" },
                new[] { "lefthandthumb2", "thumb_02_l", "thumb2_l", "thumb1_l", "thumb.02.l", "f_thumb02_l", "def-thumb.02.l", "def_thumb_02_l", "f_thumb.02.l", "l_finger01", "finger01_l" }),
            new("finger_thumb_2_L", "Left Thumb 3", BoneGroup.LeftHandFingers,
                new[] { "finger_thumb_2_L", "finger_thumb_2_l", "thumb_03_l", "thumb_2_l" },
                new[] { "lefthandthumb3", "thumb_03_l", "thumb3_l", "thumb2_l", "thumb.03.l", "f_thumb03_l", "def-thumb.03.l", "def_thumb_03_l", "f_thumb.03.l", "l_finger02", "finger02_l" }),
            new("finger_thumb_end_L", "Left Thumb End / Tip", BoneGroup.LeftHandFingers,
                new[] { "finger_thumb_end_L", "finger_thumb_end_l", "finger_thumb_leaf_l" },
                new[] { "thumb_04_leaf_l", "lefthandthumb4", "thumb_04_l", "thumb_leaf_l", "thumb_end_l", "thumb.004.l", "l_finger02_end", "l_finger0_end", "finger02_end_l", "l_finger0nub" }),

            new("finger_index_meta_L", "Left Index Metacarpal", BoneGroup.LeftHandFingers,
                new[] { "finger_index_meta_L", "finger_index_meta_l" },
                Array.Empty<string>()),
            new("finger_index_0_L", "Left Index 1", BoneGroup.LeftHandFingers,
                new[] { "finger_index_0_L", "finger_index_0_l", "index_01_l", "index_0_l" },
                new[] { "lefthandindex1", "index_01_l", "index1_l", "indexfinger0_l", "index.01.l", "f_index01_l", "def-f_index.01.l", "def_f_index_01_l", "f_index.01.l", "l_finger1", "finger1_l" }),
            new("finger_index_1_L", "Left Index 2", BoneGroup.LeftHandFingers,
                new[] { "finger_index_1_L", "finger_index_1_l", "index_02_l", "index_1_l" },
                new[] { "lefthandindex2", "index_02_l", "index2_l", "indexfinger1_l", "index.02.l", "f_index02_l", "def-f_index.02.l", "def_f_index_02_l", "f_index.02.l", "l_finger11", "finger11_l" }),
            new("finger_index_2_L", "Left Index 3", BoneGroup.LeftHandFingers,
                new[] { "finger_index_2_L", "finger_index_2_l", "index_03_l", "index_2_l" },
                new[] { "lefthandindex3", "index_03_l", "index3_l", "indexfinger2_l", "index.03.l", "f_index03_l", "def-f_index.03.l", "def_f_index_03_l", "f_index.03.l", "l_finger12", "finger12_l" }),
            new("finger_index_end_L", "Left Index End / Tip", BoneGroup.LeftHandFingers,
                new[] { "finger_index_end_L", "finger_index_end_l", "finger_index_leaf_l" },
                new[] { "index_04_leaf_l", "lefthandindex4", "index_04_l", "index_leaf_l", "index_end_l", "index.004.l", "l_finger12_end", "l_finger1_end", "finger12_end_l", "l_finger1nub" }),

            new("finger_middle_meta_L", "Left Middle Metacarpal", BoneGroup.LeftHandFingers,
                new[] { "finger_middle_meta_L", "finger_middle_meta_l" },
                Array.Empty<string>()),
            new("finger_middle_0_L", "Left Middle 1", BoneGroup.LeftHandFingers,
                new[] { "finger_middle_0_L", "finger_middle_0_l", "middle_01_l", "middle_0_l" },
                new[] { "lefthandmiddle1", "middle_01_l", "middle1_l", "middlefinger0_l", "middle.01.l", "f_middle01_l", "def-f_middle.01.l", "def_f_middle_01_l", "f_middle.01.l", "l_finger2", "finger2_l" }),
            new("finger_middle_1_L", "Left Middle 2", BoneGroup.LeftHandFingers,
                new[] { "finger_middle_1_L", "finger_middle_1_l", "middle_02_l", "middle_1_l" },
                new[] { "lefthandmiddle2", "middle_02_l", "middle2_l", "middlefinger1_l", "middle.02.l", "f_middle02_l", "def-f_middle.02.l", "def_f_middle_02_l", "f_middle.02.l", "l_finger21", "finger21_l" }),
            new("finger_middle_2_L", "Left Middle 3", BoneGroup.LeftHandFingers,
                new[] { "finger_middle_2_L", "finger_middle_2_l", "middle_03_l", "middle_2_l" },
                new[] { "lefthandmiddle3", "middle_03_l", "middle3_l", "middlefinger2_l", "middle.03.l", "f_middle03_l", "def-f_middle.03.l", "def_f_middle_03_l", "f_middle.03.l", "l_finger22", "finger22_l" }),
            new("finger_middle_end_L", "Left Middle End / Tip", BoneGroup.LeftHandFingers,
                new[] { "finger_middle_end_L", "finger_middle_end_l", "finger_middle_leaf_l" },
                new[] { "middle_04_leaf_l", "lefthandmiddle4", "middle_04_l", "middle_leaf_l", "middle_end_l", "middle.004.l", "l_finger22_end", "l_finger2_end", "finger22_end_l", "l_finger2nub" }),

            new("finger_ring_meta_L", "Left Ring Metacarpal", BoneGroup.LeftHandFingers,
                new[] { "finger_ring_meta_L", "finger_ring_meta_l" },
                Array.Empty<string>()),
            new("finger_ring_0_L", "Left Ring 1", BoneGroup.LeftHandFingers,
                new[] { "finger_ring_0_L", "finger_ring_0_l", "ring_01_l", "ring_0_l" },
                new[] { "lefthandring1", "ring_01_l", "ring1_l", "ringfinger0_l", "ring.01.l", "f_ring01_l", "def-f_ring.01.l", "def_f_ring_01_l", "f_ring.01.l", "l_finger3", "finger3_l" }),
            new("finger_ring_1_L", "Left Ring 2", BoneGroup.LeftHandFingers,
                new[] { "finger_ring_1_L", "finger_ring_1_l", "ring_02_l", "ring_1_l" },
                new[] { "lefthandring2", "ring_02_l", "ring2_l", "ringfinger1_l", "ring.02.l", "f_ring02_l", "def-f_ring.02.l", "def_f_ring_02_l", "f_ring.02.l", "l_finger31", "finger31_l" }),
            new("finger_ring_2_L", "Left Ring 3", BoneGroup.LeftHandFingers,
                new[] { "finger_ring_2_L", "finger_ring_2_l", "ring_03_l", "ring_2_l" },
                new[] { "lefthandring3", "ring_03_l", "ring3_l", "ringfinger2_l", "ring.03.l", "f_ring03_l", "def-f_ring.03.l", "def_f_ring_03_l", "f_ring.03.l", "l_finger32", "finger32_l" }),
            new("finger_ring_end_L", "Left Ring End / Tip", BoneGroup.LeftHandFingers,
                new[] { "finger_ring_end_L", "finger_ring_end_l", "finger_ring_leaf_l" },
                new[] { "ring_04_leaf_l", "lefthandring4", "ring_04_l", "ring_leaf_l", "ring_end_l", "ring.004.l", "l_finger32_end", "l_finger3_end", "finger32_end_r_unused", "l_finger3nub" }),

            new("finger_pinky_meta_L", "Left Pinky Metacarpal", BoneGroup.LeftHandFingers,
                new[] { "finger_pinky_meta_L", "finger_pinky_meta_l" },
                Array.Empty<string>()),
            new("finger_pinky_0_L", "Left Pinky 1", BoneGroup.LeftHandFingers,
                new[] { "finger_pinky_0_L", "finger_pinky_0_l", "pinky_01_l", "pinky_0_l" },
                new[] { "lefthandpinky1", "pinky_01_l", "pinky1_l", "littlefinger0_l", "pinky.01.l", "f_pinky01_l", "def-f_pinky.01.l", "def_f_pinky_01_l", "f_pinky.01.l", "little_01_l", "l_finger4", "finger4_l" }),
            new("finger_pinky_1_L", "Left Pinky 2", BoneGroup.LeftHandFingers,
                new[] { "finger_pinky_1_L", "finger_pinky_1_l", "pinky_02_l", "pinky_1_l" },
                new[] { "lefthandpinky2", "pinky_02_l", "pinky2_l", "littlefinger1_l", "pinky.02.l", "f_pinky02_l", "def-f_pinky.02.l", "def_f_pinky_02_l", "f_pinky.02.l", "little_02_l", "l_finger41", "finger41_l" }),
            new("finger_pinky_2_L", "Left Pinky 3", BoneGroup.LeftHandFingers,
                new[] { "finger_pinky_2_L", "finger_pinky_2_l", "pinky_03_l", "pinky_2_l" },
                new[] { "lefthandpinky3", "pinky_03_l", "pinky3_l", "littlefinger2_l", "pinky.03.l", "f_pinky03_l", "def-f_pinky.03.l", "def_f_pinky_03_l", "f_pinky.03.l", "little_03_l", "l_finger42", "finger42_l" }),
            new("finger_pinky_end_L", "Left Pinky End / Tip", BoneGroup.LeftHandFingers,
                new[] { "finger_pinky_end_L", "finger_pinky_end_l", "finger_pinky_leaf_l" },
                new[] { "pinky_04_leaf_l", "lefthandpinky4", "pinky_04_l", "pinky_leaf_l", "pinky_end_l", "pinky.004.l", "little_04_leaf_l", "l_finger42_end", "l_finger4_end", "finger42_end_l", "l_finger4nub" }),

            // --- Right Hand Fingers ---
            new("finger_thumb_meta_R", "Right Thumb Metacarpal", BoneGroup.RightHandFingers,
                new[] { "finger_thumb_meta_R", "finger_thumb_meta_r" },
                Array.Empty<string>()),
            new("finger_thumb_0_R", "Right Thumb 1", BoneGroup.RightHandFingers,
                new[] { "finger_thumb_0_R", "finger_thumb_0_r", "thumb_01_r", "thumb_0_r" },
                new[] { "righthandthumb1", "thumb_01_r", "thumb1_r", "thumb0_r", "thumb.01.r", "f_thumb01_r", "def-thumb.01.r", "def_thumb_01_r", "f_thumb.01.r", "r_finger0", "finger0_r" }),
            new("finger_thumb_1_R", "Right Thumb 2", BoneGroup.RightHandFingers,
                new[] { "finger_thumb_1_R", "finger_thumb_1_r", "thumb_02_r", "thumb_1_r" },
                new[] { "righthandthumb2", "thumb_02_r", "thumb2_r", "thumb1_r", "thumb.02.r", "f_thumb02_r", "def-thumb.02.r", "def_thumb_02_r", "f_thumb.02.r", "r_finger01", "finger01_r" }),
            new("finger_thumb_2_R", "Right Thumb 3", BoneGroup.RightHandFingers,
                new[] { "finger_thumb_2_R", "finger_thumb_2_r", "thumb_03_r", "thumb_2_r" },
                new[] { "righthandthumb3", "thumb_03_r", "thumb3_r", "thumb2_r", "thumb.03.r", "f_thumb03_r", "def-thumb.03.r", "def_thumb_03_r", "f_thumb.03.r", "r_finger02", "finger02_r" }),
            new("finger_thumb_end_R", "Right Thumb End / Tip", BoneGroup.RightHandFingers,
                new[] { "finger_thumb_end_R", "finger_thumb_end_r", "finger_thumb_leaf_r" },
                new[] { "thumb_04_leaf_r", "righthandthumb4", "thumb_04_r", "thumb_leaf_r", "thumb_end_r", "thumb.004.r", "r_finger02_end", "r_finger0_end", "finger02_end_r", "r_finger0nub" }),

            new("finger_index_meta_R", "Right Index Metacarpal", BoneGroup.RightHandFingers,
                new[] { "finger_index_meta_R", "finger_index_meta_r" },
                Array.Empty<string>()),
            new("finger_index_0_R", "Right Index 1", BoneGroup.RightHandFingers,
                new[] { "finger_index_0_R", "finger_index_0_r", "index_01_r", "index_0_r" },
                new[] { "righthandindex1", "index_01_r", "index1_r", "indexfinger0_r", "index.01.r", "f_index01_r", "def-f_index.01.r", "def_f_index_01_r", "f_index.01.r", "r_finger1", "finger1_r" }),
            new("finger_index_1_R", "Right Index 2", BoneGroup.RightHandFingers,
                new[] { "finger_index_1_R", "finger_index_1_r", "index_02_r", "index_1_r" },
                new[] { "righthandindex2", "index_02_r", "index2_r", "indexfinger1_r", "index.02.r", "f_index02_r", "def-f_index.02.r", "def_f_index_02_r", "f_index.02.r", "r_finger11", "finger11_r" }),
            new("finger_index_2_R", "Right Index 3", BoneGroup.RightHandFingers,
                new[] { "finger_index_2_R", "finger_index_2_r", "index_03_r", "index_2_r" },
                new[] { "righthandindex3", "index_03_r", "index3_r", "indexfinger2_r", "index.03.r", "f_index03_r", "def-f_index.03.r", "def_f_index_03_r", "f_index.03.r", "r_finger12", "finger12_r" }),
            new("finger_index_end_R", "Right Index End / Tip", BoneGroup.RightHandFingers,
                new[] { "finger_index_end_R", "finger_index_end_r", "finger_index_leaf_r" },
                new[] { "index_04_leaf_r", "righthandindex4", "index_04_r", "index_leaf_r", "index_end_r", "index.004.r", "r_finger12_end", "r_finger1_end", "finger12_end_r", "r_finger1nub" }),

            new("finger_middle_meta_R", "Right Middle Metacarpal", BoneGroup.RightHandFingers,
                new[] { "finger_middle_meta_R", "finger_middle_meta_r" },
                Array.Empty<string>()),
            new("finger_middle_0_R", "Right Middle 1", BoneGroup.RightHandFingers,
                new[] { "finger_middle_0_R", "finger_middle_0_r", "middle_01_r", "middle_0_r" },
                new[] { "righthandmiddle1", "middle_01_r", "middle1_r", "middlefinger0_r", "middle.01.r", "f_middle01_r", "def-f_middle.01.r", "def_f_middle_01_r", "f_middle.01.r", "r_finger2", "finger2_r" }),
            new("finger_middle_1_R", "Right Middle 2", BoneGroup.RightHandFingers,
                new[] { "finger_middle_1_R", "finger_middle_1_r", "middle_02_r", "middle_1_r" },
                new[] { "righthandmiddle2", "middle_02_r", "middle2_r", "middlefinger1_r", "middle.02.r", "f_middle02_r", "def-f_middle.02.r", "def_f_middle_02_r", "f_middle.02.r", "r_finger21", "finger21_r" }),
            new("finger_middle_2_R", "Right Middle 3", BoneGroup.RightHandFingers,
                new[] { "finger_middle_2_R", "finger_middle_2_r", "middle_03_r", "middle_2_r" },
                new[] { "righthandmiddle3", "middle_03_r", "middle3_r", "middlefinger2_r", "middle.03.r", "f_middle03_r", "def-f_middle.03.r", "def_f_middle_03_r", "f_middle.03.r", "r_finger22", "finger22_r" }),
            new("finger_middle_end_R", "Right Middle End / Tip", BoneGroup.RightHandFingers,
                new[] { "finger_middle_end_R", "finger_middle_end_r", "finger_middle_leaf_r" },
                new[] { "middle_04_leaf_r", "righthandmiddle4", "middle_04_r", "middle_leaf_r", "middle_end_r", "middle.004.r", "r_finger22_end", "r_finger2_end", "finger22_end_r", "r_finger2nub" }),

            new("finger_ring_meta_R", "Right Ring Metacarpal", BoneGroup.RightHandFingers,
                new[] { "finger_ring_meta_R", "finger_ring_meta_r" },
                Array.Empty<string>()),
            new("finger_ring_0_R", "Right Ring 1", BoneGroup.RightHandFingers,
                new[] { "finger_ring_0_R", "finger_ring_0_r", "ring_01_r", "ring_0_r" },
                new[] { "righthandring1", "ring_01_r", "ring1_r", "ringfinger0_r", "ring.01.r", "f_ring01_r", "def-f_ring.01.r", "def_f_ring_01_r", "f_ring.01.r", "r_finger3", "finger3_r" }),
            new("finger_ring_1_R", "Right Ring 2", BoneGroup.RightHandFingers,
                new[] { "finger_ring_1_R", "finger_ring_1_r", "ring_02_r", "ring_1_r" },
                new[] { "righthandring2", "ring_02_r", "ring2_r", "ringfinger1_r", "ring.02.r", "f_ring02_r", "def-f_ring.02.r", "def_f_ring_02_r", "f_ring.02.r", "r_finger31", "finger31_r" }),
            new("finger_ring_2_R", "Right Ring 3", BoneGroup.RightHandFingers,
                new[] { "finger_ring_2_R", "finger_ring_2_r", "ring_03_r", "ring_2_r" },
                new[] { "righthandring3", "ring_03_r", "ring3_r", "ringfinger2_r", "ring.03.r", "f_ring03_r", "def-f_ring.03.r", "def_f_ring_03_r", "f_ring.03.r", "r_finger32", "finger32_r" }),
            new("finger_ring_end_R", "Right Ring End / Tip", BoneGroup.RightHandFingers,
                new[] { "finger_ring_end_R", "finger_ring_end_r", "finger_ring_leaf_r" },
                new[] { "ring_04_leaf_r", "righthandring4", "ring_04_r", "ring_leaf_r", "ring_end_r", "ring.004.r", "r_finger32_end", "r_finger3_end", "finger32_end_r", "r_finger3nub" }),

            new("finger_pinky_meta_R", "Right Pinky Metacarpal", BoneGroup.RightHandFingers,
                new[] { "finger_pinky_meta_R", "finger_pinky_meta_r" },
                Array.Empty<string>()),
            new("finger_pinky_0_R", "Right Pinky 1", BoneGroup.RightHandFingers,
                new[] { "finger_pinky_0_R", "finger_pinky_0_r", "pinky_01_r", "pinky_0_r" },
                new[] { "righthandpinky1", "pinky_01_r", "pinky1_r", "littlefinger0_r", "pinky.01.r", "f_pinky01_r", "def-f_pinky.01.r", "def_f_pinky_01_r", "f_pinky.01.r", "little_01_r", "r_finger4", "finger4_r" }),
            new("finger_pinky_1_R", "Right Pinky 2", BoneGroup.RightHandFingers,
                new[] { "finger_pinky_1_R", "finger_pinky_1_r", "pinky_02_r", "pinky_1_r" },
                new[] { "righthandpinky2", "pinky_02_r", "pinky2_r", "littlefinger1_r", "pinky.02.r", "f_pinky02_r", "def-f_pinky.02.r", "def_f_pinky_02_r", "f_pinky.02.r", "little_02_r", "r_finger41", "finger41_r" }),
            new("finger_pinky_2_R", "Right Pinky 3", BoneGroup.RightHandFingers,
                new[] { "finger_pinky_2_R", "finger_pinky_2_r", "pinky_03_r", "pinky_2_r" },
                new[] { "righthandpinky3", "pinky_03_r", "pinky3_r", "littlefinger2_r", "pinky.03.r", "f_pinky03_r", "def-f_pinky.03.r", "def_f_pinky_03_r", "f_pinky.03.r", "little_03_r", "r_finger42", "finger42_r" }),
            new("finger_pinky_end_R", "Right Pinky End / Tip", BoneGroup.RightHandFingers,
                new[] { "finger_pinky_end_R", "finger_pinky_end_r", "finger_pinky_leaf_r" },
                new[] { "pinky_04_leaf_r", "righthandpinky4", "pinky_04_r", "pinky_leaf_r", "pinky_end_r", "pinky.004.r", "little_04_leaf_r", "r_finger42_end", "r_finger4_end", "finger42_end_r", "r_finger4nub" }),

            // --- Secondary & Helper Bones (Arm & Thigh Counter-Twists) ---
            new("arm_upper_L_TWIST", "Left Upper Arm Twist", BoneGroup.Secondary,
                new[] { "arm_upper_L_TWIST", "arm_upper_twist_L", "arm_upper_l_twist" },
                new[] { "arm_upper_l_twist", "arm_upper_twist_l", "upperarm_twist_l" }),
            new("arm_upper_R_TWIST", "Right Upper Arm Twist", BoneGroup.Secondary,
                new[] { "arm_upper_R_TWIST", "arm_upper_twist_R", "arm_upper_r_twist" },
                new[] { "arm_upper_r_twist", "arm_upper_twist_r", "upperarm_twist_r" }),
            new("arm_lower_L_TWIST", "Left Forearm Twist", BoneGroup.Secondary,
                new[] { "arm_lower_L_TWIST", "arm_lower_twist_L", "arm_lower_l_twist", "wrist_helper_L" },
                new[] { "def-forearm.l.001", "def_forearm_l_001", "forearm.l.001", "forearm_twist_l", "arm_lower_twist_l" }),
            new("arm_lower_R_TWIST", "Right Forearm Twist", BoneGroup.Secondary,
                new[] { "arm_lower_R_TWIST", "arm_lower_twist_R", "arm_lower_r_twist", "wrist_helper_R" },
                new[] { "def-forearm.r.001", "def_forearm_r_001", "forearm.r.001", "forearm_twist_r", "arm_lower_twist_r" }),
            new("leg_upper_L_TWIST", "Left Thigh Twist (Full)", BoneGroup.Secondary,
                new[] { "leg_upper_L_TWIST", "leg_upper_twist_L", "leg_upper_l_twist" },
                new[] { "leg_upper_l_twist", "leg_upper_twist_l", "thigh_twist_l" }),
            new("leg_upper_L_TWIST1", "Left Thigh Twist (Mid)", BoneGroup.Secondary,
                new[] { "leg_upper_L_TWIST1", "leg_upper_twist1_L", "leg_upper_l_twist1" },
                new[] { "leg_upper_l_twist1", "leg_upper_twist1_l", "thigh_twist1_l" }),
            new("leg_upper_R_TWIST", "Right Thigh Twist (Full)", BoneGroup.Secondary,
                new[] { "leg_upper_R_TWIST", "leg_upper_twist_R", "leg_upper_r_twist" },
                new[] { "leg_upper_r_twist", "leg_upper_twist_r", "thigh_twist_r" }),
            new("leg_upper_R_TWIST1", "Right Thigh Twist (Mid)", BoneGroup.Secondary,
                new[] { "leg_upper_R_TWIST1", "leg_upper_twist1_R", "leg_upper_r_twist1" },
                new[] { "leg_upper_r_twist1", "leg_upper_twist1_r", "thigh_twist1_r" })
        };

        /// <summary>
        /// Cleans and sanitizes bone name for fuzzy token matching.
        /// Strips namespace prefixes like "mixamorig:", "bip01_", "bip_", "b_", "def_", "def-", "j_", "root|", etc.
        /// Optionally strips trailing glTF node export indices like "_0108", "_03", "_019".
        /// </summary>
        public static string CleanBoneName(string rawName, bool stripTrailingGltfIndex = true)
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

            // Strip wrapper prefixes such as "ValveBiped." before skeletal rig prefixes ("Bip01_", etc.)
            if (name.StartsWith("valvebiped.", StringComparison.OrdinalIgnoreCase)) name = name[11..];
            else if (name.StartsWith("valvebiped_", StringComparison.OrdinalIgnoreCase)) name = name[11..];
            else if (name.StartsWith("valvebiped ", StringComparison.OrdinalIgnoreCase)) name = name[11..];

            // Strip standard prefixes (case-insensitive)
            if (name.StartsWith("mixamorig:", StringComparison.OrdinalIgnoreCase)) name = name[10..];
            else if (name.StartsWith("mixamorig_", StringComparison.OrdinalIgnoreCase)) name = name[10..];
            else if (name.StartsWith("mixamo_", StringComparison.OrdinalIgnoreCase)) name = name[7..];
            else if (name.StartsWith("skel_", StringComparison.OrdinalIgnoreCase)) name = name[5..];
            else if (name.StartsWith("rig_", StringComparison.OrdinalIgnoreCase)) name = name[4..];
            else if (name.StartsWith("b_", StringComparison.OrdinalIgnoreCase)) name = name[2..];
            else if (name.StartsWith("def_", StringComparison.OrdinalIgnoreCase)) name = name[4..];
            else if (name.StartsWith("def-", StringComparison.OrdinalIgnoreCase)) name = name[4..];
            else if (name.StartsWith("j_", StringComparison.OrdinalIgnoreCase)) name = name[2..];
            else if (name.StartsWith("bip01_", StringComparison.OrdinalIgnoreCase)) name = name[6..];
            else if (name.StartsWith("bip02_", StringComparison.OrdinalIgnoreCase)) name = name[6..];
            else if (name.StartsWith("bip01 ", StringComparison.OrdinalIgnoreCase)) name = name[6..];
            else if (name.StartsWith("bip02 ", StringComparison.OrdinalIgnoreCase)) name = name[6..];
            else if (name.StartsWith("bip01.", StringComparison.OrdinalIgnoreCase)) name = name[6..];
            else if (name.StartsWith("bip02.", StringComparison.OrdinalIgnoreCase)) name = name[6..];
            else if (name.StartsWith("bip_", StringComparison.OrdinalIgnoreCase)) name = name[4..];
            else if (name.StartsWith("bip ", StringComparison.OrdinalIgnoreCase)) name = name[4..];
            else if (name.StartsWith("cc_", StringComparison.OrdinalIgnoreCase)) name = name[3..];
            else if (name.StartsWith("m_avg_", StringComparison.OrdinalIgnoreCase)) name = name[6..];
            else if (name.StartsWith("m_", StringComparison.OrdinalIgnoreCase)) name = name[2..];

            if (stripTrailingGltfIndex)
            {
                // Strip trailing glTF node export indices (e.g. "_0108", "_03", "_019", "_0127")
                int lastUnderscore = name.LastIndexOf('_');
                if (lastUnderscore > 0 && lastUnderscore < name.Length - 1)
                {
                    string suffix = name[(lastUnderscore + 1)..];
                    if (suffix.Length >= 2 && suffix.All(char.IsDigit))
                    {
                        name = name[..lastUnderscore];
                    }
                }
            }

            // Normalize delimiters: replace dots, hyphens, and whitespace with underscores
            name = name.Replace('.', '_').Replace('-', '_').Replace(' ', '_');

            // Strip Blender Rigify finger prefix "f_" (e.g. f_index_01 -> index_01)
            string lowerName = name.ToLowerInvariant();
            if (lowerName.StartsWith("f_index_") || lowerName.StartsWith("f_middle_") || lowerName.StartsWith("f_ring_") || lowerName.StartsWith("f_pinky_") || lowerName.StartsWith("f_thumb_"))
            {
                name = name[2..];
            }

            return name.ToLowerInvariant();
        }

        /// <summary>
        /// Splits a raw bone name into a cleaner display base and an optional glTF numeric export suffix (e.g. "BIP01_SPINE", "_011").
        /// </summary>
        public static string SplitGltfNumericSuffix(string rawName, out string gltfSuffix)
        {
            gltfSuffix = "";
            if (string.IsNullOrWhiteSpace(rawName)) return rawName ?? "";

            int lastUnderscore = rawName.LastIndexOf('_');
            if (lastUnderscore > 0 && lastUnderscore < rawName.Length - 1)
            {
                string suffix = rawName[(lastUnderscore + 1)..];
                if (suffix.Length >= 2 && suffix.StartsWith('0') && suffix.All(char.IsDigit))
                {
                    gltfSuffix = rawName[lastUnderscore..];
                    return rawName[..lastUnderscore];
                }
            }
            return rawName;
        }

        /// <summary>
        /// Identifies non-primary bones (sub-foot bones, procedural helpers, twist bones, cloth/attachments)
        /// that must never be auto-mapped as primary humanoid limb joints.
        /// </summary>
        /// <summary>
        /// Identifies non-primary bones (sub-foot bones, procedural helpers, twist bones, cloth/attachments)
        /// that must never be auto-mapped as primary humanoid limb joints.
        /// </summary>
        public static bool IsSecondaryOrHelperBone(string rawName, bool allowTwist = false)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return false;
            string clean = CleanBoneName(rawName);

            // Sub-foot secondary bones (sole, foot_end, attachFoot, etc.)
            // Note: ball_end and toe_end are valid mapped toe tips (e.g. ball_end_L/R, mixamorig:LeftToe_End)
            if (clean.Contains("sole") || clean.Contains("foot_end"))
                return true;

            // Twist & procedural helper bones
            if (!allowTwist && (clean.Contains("twist") || clean.Contains("helper") || clean.Contains("proc") || clean.Contains("notwist")))
                return true;

            // IK targets, offset handles, and cloth/attachment bones
            if (clean.Contains("iktarget") || clean.Contains("ik_target") || clean.Contains("offset") ||
                clean.Contains("cloth") || clean.Contains("strap") || clean.Contains("flap") || clean.Contains("attach"))
                return true;

            return false;
        }

        /// <summary>
        /// Finds the best target bone on the Deadlock skeleton corresponding to a BoneDefinition.
        /// </summary>
        public static string ResolveTargetBone(Skeleton3D targetSkeleton, BoneDefinition def)
        {
            if (targetSkeleton == null || !GodotObject.IsInstanceValid(targetSkeleton) || targetSkeleton.IsQueuedForDeletion()) return null;

            bool allowTwist = def.Group == BoneGroup.Secondary || def.CanonicalName.Contains("twist", StringComparison.OrdinalIgnoreCase);

            // 1. Direct canonical name match
            if (targetSkeleton.FindBone(def.CanonicalName) != -1)
            {
                return def.CanonicalName;
            }

            // 2. Exact match against target aliases
            foreach (var alias in def.TargetAliases)
            {
                int idx = targetSkeleton.FindBone(alias);
                if (idx != -1)
                {
                    string candidate = targetSkeleton.GetBoneName(idx);
                    if (!IsSecondaryOrHelperBone(candidate, allowTwist)) return candidate;
                }
            }

            // 3. Case-insensitive / normalized match across all bones in target skeleton
            int totalBones = targetSkeleton.GetBoneCount();
            for (int b = 0; b < totalBones; b++)
            {
                string tName = targetSkeleton.GetBoneName(b);
                if (IsSecondaryOrHelperBone(tName, allowTwist)) continue;

                    string cleanT = CleanBoneName(tName, stripTrailingGltfIndex: false);

                    foreach (var alias in def.TargetAliases)
                    {
                        string cleanAlias = CleanBoneName(alias, stripTrailingGltfIndex: false);
                        if (cleanT == cleanAlias) return tName;
                    }
                }

                return null;
            }

        /// <summary>
        /// Finds the best source bone on the source skeleton corresponding to a BoneDefinition.
        /// </summary>
        public static string ResolveSourceBone(Skeleton3D sourceSkeleton, BoneDefinition def, IReadOnlyCollection<string> excludeSourceBones = null)
        {
            if (sourceSkeleton == null) return null;

            bool allowTwist = def.Group == BoneGroup.Secondary || def.CanonicalName.Contains("twist", StringComparison.OrdinalIgnoreCase);

            int totalSource = sourceSkeleton.GetBoneCount();
            var allSourceBones = new List<string>(totalSource);
            var sourceBones = new List<string>(totalSource);
            for (int s = 0; s < totalSource; s++)
            {
                string sName = sourceSkeleton.GetBoneName(s);
                if (!IsSecondaryOrHelperBone(sName, allowTwist))
                {
                    allSourceBones.Add(sName);
                    if (excludeSourceBones == null || !excludeSourceBones.Contains(sName))
                    {
                        sourceBones.Add(sName);
                    }
                }
            }

            // Rigify Special Case: In Blender Rigify, DEF-spine is the Pelvis root when spine.001 exists!
            bool isRigifySpine = allSourceBones.Any(b =>
            {
                string cFalse = CleanBoneName(b, stripTrailingGltfIndex: false);
                string cTrue = CleanBoneName(b, stripTrailingGltfIndex: true);
                return cFalse == "spine_001" || cTrue == "spine_001" || cFalse.EndsWith("_spine_001");
            });
            if (def.CanonicalName == "pelvis" && isRigifySpine)
            {
                var rigifyPelvis = sourceBones.FirstOrDefault(b =>
                {
                    string c = CleanBoneName(b, stripTrailingGltfIndex: true);
                    return c == "spine" || c == "def_spine";
                });
                if (rigifyPelvis != null) return rigifyPelvis;
            }

            // Detect 0-based spine numbering (Mixamo: Spine/Spine1/Spine2; 3ds Max Biped: Bip01_Spine/Bip01_Spine1/Bip01_Spine2)
            // vs 1-based spine numbering (Rigify: DEF-spine.001..004; Mesh2Motion: spine_01..03).
            IReadOnlyList<string> aliasesToSearch = def.SourceAliases;
            if (def.Group == BoneGroup.Spine && !isRigifySpine)
            {
                bool hasZeroBaseSpine = allSourceBones.Any(b =>
                {
                    string c = CleanBoneName(b, stripTrailingGltfIndex: true);
                    return c == "spine" || c == "spine0" || c == "spine_0";
                });
                if (hasZeroBaseSpine)
                {
                    aliasesToSearch = def.CanonicalName switch
                    {
                        "spine_0" => new[] { "spine_0", "spine0", "spine", "bip_spine0", "bip_spine", "b_spine0", "b_spine" },
                        "spine_1" => new[] { "spine_1", "spine1", "bip_spine1", "b_spine1", "spine_01" },
                        "spine_2" => new[] { "spine_2", "spine2", "bip_spine2", "b_spine2", "chest", "spine_02" },
                        "spine_3" => new[] { "spine_3", "spine3", "bip_spine3", "b_spine3", "upperchest", "upper_chest", "spine_03" },
                        _ => def.SourceAliases
                    };
                }
            }

            // 0. Exact canonical match for Deadlock-named spine bones (e.g. spine_0..3) when spine_0 is intentionally omitted
            if (def.Group == BoneGroup.Spine)
            {
                var exactCanonical = sourceBones.FirstOrDefault(b => string.Equals(b, def.CanonicalName, StringComparison.OrdinalIgnoreCase));
                if (exactCanonical != null) return exactCanonical;
            }

            // 1. Exact match with any source alias
            foreach (var alias in aliasesToSearch)
            {
                var match = sourceBones.FirstOrDefault(b => string.Equals(b, alias, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }

            // 2. Cleaned match preserving numeric bone indices (e.g. spine_01, spine_02, DEF-spine.001)
            foreach (var alias in aliasesToSearch)
            {
                string cleanAlias = CleanBoneName(alias, stripTrailingGltfIndex: false);
                var match = sourceBones.FirstOrDefault(b => CleanBoneName(b, stripTrailingGltfIndex: false) == cleanAlias);
                if (match != null) return match;
            }

            // 3. Normalized match stripping trailing glTF export node indices (e.g. BIP01_SPINE1_012 -> spine1)
            foreach (var alias in aliasesToSearch)
            {
                string cleanAlias = CleanBoneName(alias, stripTrailingGltfIndex: false);
                var match = sourceBones.FirstOrDefault(b => CleanBoneName(b, stripTrailingGltfIndex: true) == cleanAlias);
                if (match != null) return match;
            }

            // 4. Token-based matching for compound names (e.g. "LeftHandIndex1", "thigh_l", "clavicle_l")
            bool isCentral = def.Group == BoneGroup.Pelvis || def.Group == BoneGroup.Spine || def.Group == BoneGroup.Head || def.Group == BoneGroup.Root;
            bool isLeft = def.Group == BoneGroup.LeftArm || def.Group == BoneGroup.LeftLeg || def.Group == BoneGroup.LeftHandFingers;
            bool isRight = def.Group == BoneGroup.RightArm || def.Group == BoneGroup.RightLeg || def.Group == BoneGroup.RightHandFingers;

            foreach (var alias in aliasesToSearch)
            {
                string cleanAlias = CleanBoneName(alias, stripTrailingGltfIndex: false);
                var match = sourceBones.FirstOrDefault(b =>
                {
                    string c = CleanBoneName(b, stripTrailingGltfIndex: true);

                    // Central bones (pelvis, spine, head) must NEVER match lateral bones (ending in _l/_r or containing left/right)
                    bool hasLateralTag = c.EndsWith("_l") || c.EndsWith("_r") || c.Contains("_l_") || c.Contains("_r_") || c.Contains("left") || c.Contains("right");
                    if (isCentral && hasLateralTag) return false;

                    // Lateral sides must strictly match
                    if (isLeft && (c.EndsWith("_r") || c.Contains("_r_") || c.Contains("right")) && !c.Contains("left")) return false;
                    if (isRight && (c.EndsWith("_l") || c.Contains("_l_") || c.Contains("left")) && !c.Contains("right")) return false;

                    // "head" must not match "forehead"
                    if (def.CanonicalName == "head" && c.Contains("forehead")) return false;

                    // Anatomical hand-vs-foot isolation: articulated toes (e.g. C_TOES_INDEX2.R) must NEVER match hand fingers/arms,
                    // and hand/finger bones must NEVER match foot/toe/leg slots.
                    bool isUpperLimbOrFinger = def.Group == BoneGroup.LeftHandFingers || def.Group == BoneGroup.RightHandFingers ||
                                               def.Group == BoneGroup.LeftArm || def.Group == BoneGroup.RightArm;
                    bool isLowerLimb = def.Group == BoneGroup.LeftLeg || def.Group == BoneGroup.RightLeg;
                    if (isUpperLimbOrFinger && (c.Contains("toe") || c.Contains("foot") || c.Contains("ankle") || c.Contains("heel") || c.Contains("sole") || c.Contains("ball")))
                        return false;
                    if (isLowerLimb && (c.Contains("hand") || c.Contains("wrist") || c.Contains("finger") || c.Contains("palm") || c.Contains("arm")))
                        return false;

                    // Leaf "_end" nubs (e.g. head1_end, hand_end) must not steal primary joint slots
                    if (c.EndsWith("_end") && !cleanAlias.EndsWith("_end")) return false;

                    // Prevent bare "spine" alias on spine_0 from prefix-hijacking numbered spine bones (e.g. spine_1, spine_2, spine_3)
                    if (cleanAlias == "spine" && c.StartsWith("spine_", StringComparison.Ordinal))
                    {
                        string tail = c["spine_".Length..];
                        if (tail.Length > 0 && char.IsDigit(tail[0]) && tail != "0" && tail != "00")
                            return false;
                    }

                    // Match tokens or exact compound word
                    return c == cleanAlias || c.StartsWith(cleanAlias + "_") || c.EndsWith("_" + cleanAlias) || c.Contains("_" + cleanAlias + "_");
                });
                if (match != null) return match;
            }

            return null;
        }

        private readonly struct SourceBoneEntry
        {
            public readonly string Raw;
            public readonly string CleanExact;
            public readonly string CleanNoGltf;
            public readonly bool IsHelper;
            public readonly bool IsTwist;

            public SourceBoneEntry(string raw)
            {
                Raw = raw;
                CleanExact = CleanBoneName(raw, stripTrailingGltfIndex: false);
                CleanNoGltf = CleanBoneName(raw, stripTrailingGltfIndex: true);
                IsHelper = IsSecondaryOrHelperBone(raw, allowTwist: true);
                IsTwist = IsSecondaryOrHelperBone(raw, allowTwist: false) && !IsHelper;
            }
        }

        private static string ResolveSourceBoneFast(
            SourceBoneEntry[] entries,
            bool isRigifySpine,
            bool hasZeroBaseSpine,
            BoneDefinition def,
            HashSet<string> excludeSourceBones)
        {
            bool allowTwist = def.Group == BoneGroup.Secondary || def.CanonicalName.Contains("twist", StringComparison.OrdinalIgnoreCase);

            if (def.CanonicalName == "pelvis" && isRigifySpine)
            {
                for (int i = 0; i < entries.Length; i++)
                {
                    ref readonly var e = ref entries[i];
                    if (e.IsHelper || (!allowTwist && e.IsTwist)) continue;
                    if (excludeSourceBones != null && excludeSourceBones.Contains(e.Raw)) continue;
                    if (e.CleanNoGltf == "spine" || e.CleanNoGltf == "def_spine") return e.Raw;
                }
            }

            IReadOnlyList<string> aliasesToSearch = def.SourceAliases;
            if (def.Group == BoneGroup.Spine && !isRigifySpine && hasZeroBaseSpine)
            {
                aliasesToSearch = def.CanonicalName switch
                {
                    "spine_0" => new[] { "spine_0", "spine0", "spine", "bip_spine0", "bip_spine", "b_spine0", "b_spine" },
                    "spine_1" => new[] { "spine_1", "spine1", "bip_spine1", "b_spine1", "spine_01" },
                    "spine_2" => new[] { "spine_2", "spine2", "bip_spine2", "b_spine2", "chest", "spine_02" },
                    "spine_3" => new[] { "spine_3", "spine3", "bip_spine3", "b_spine3", "upperchest", "upper_chest", "spine_03" },
                    _ => def.SourceAliases
                };
            }

            // 0. Exact canonical match for Deadlock-named spine bones (e.g. spine_0..3) when spine_0 is intentionally omitted
            if (def.Group == BoneGroup.Spine)
            {
                for (int i = 0; i < entries.Length; i++)
                {
                    ref readonly var e = ref entries[i];
                    if (e.IsHelper || (!allowTwist && e.IsTwist)) continue;
                    if (excludeSourceBones != null && excludeSourceBones.Contains(e.Raw)) continue;
                    if (string.Equals(e.Raw, def.CanonicalName, StringComparison.OrdinalIgnoreCase)) return e.Raw;
                }
            }

            // 1. Exact match
            for (int a = 0; a < aliasesToSearch.Count; a++)
            {
                string alias = aliasesToSearch[a];
                for (int i = 0; i < entries.Length; i++)
                {
                    ref readonly var e = ref entries[i];
                    if (e.IsHelper || (!allowTwist && e.IsTwist)) continue;
                    if (excludeSourceBones != null && excludeSourceBones.Contains(e.Raw)) continue;
                    if (string.Equals(e.Raw, alias, StringComparison.OrdinalIgnoreCase)) return e.Raw;
                }
            }

            var cleanedAliases = new string[aliasesToSearch.Count];
            for (int a = 0; a < aliasesToSearch.Count; a++)
                cleanedAliases[a] = CleanBoneName(aliasesToSearch[a], stripTrailingGltfIndex: false);

            // 2. Cleaned match preserving numeric indices
            for (int a = 0; a < cleanedAliases.Length; a++)
            {
                string cleanAlias = cleanedAliases[a];
                for (int i = 0; i < entries.Length; i++)
                {
                    ref readonly var e = ref entries[i];
                    if (e.IsHelper || (!allowTwist && e.IsTwist)) continue;
                    if (excludeSourceBones != null && excludeSourceBones.Contains(e.Raw)) continue;
                    if (e.CleanExact == cleanAlias) return e.Raw;
                }
            }

            // 3. Normalized match stripping trailing glTF export node indices
            for (int a = 0; a < cleanedAliases.Length; a++)
            {
                string cleanAlias = cleanedAliases[a];
                for (int i = 0; i < entries.Length; i++)
                {
                    ref readonly var e = ref entries[i];
                    if (e.IsHelper || (!allowTwist && e.IsTwist)) continue;
                    if (excludeSourceBones != null && excludeSourceBones.Contains(e.Raw)) continue;
                    if (e.CleanNoGltf == cleanAlias) return e.Raw;
                }
            }

            // 4. Token-based matching
            bool isCentral = def.Group == BoneGroup.Pelvis || def.Group == BoneGroup.Spine || def.Group == BoneGroup.Head || def.Group == BoneGroup.Root;
            bool isLeft = def.Group == BoneGroup.LeftArm || def.Group == BoneGroup.LeftLeg || def.Group == BoneGroup.LeftHandFingers;
            bool isRight = def.Group == BoneGroup.RightArm || def.Group == BoneGroup.RightLeg || def.Group == BoneGroup.RightHandFingers;

            for (int a = 0; a < cleanedAliases.Length; a++)
            {
                string cleanAlias = cleanedAliases[a];
                for (int i = 0; i < entries.Length; i++)
                {
                    ref readonly var e = ref entries[i];
                    if (e.IsHelper || (!allowTwist && e.IsTwist)) continue;
                    if (excludeSourceBones != null && excludeSourceBones.Contains(e.Raw)) continue;

                    string c = e.CleanNoGltf;
                    bool hasLateralTag = c.EndsWith("_l") || c.EndsWith("_r") || c.Contains("_l_") || c.Contains("_r_") || c.Contains("left") || c.Contains("right");
                    if (isCentral && hasLateralTag) continue;
                    if (isLeft && (c.EndsWith("_r") || c.Contains("_r_") || c.Contains("right")) && !c.Contains("left")) continue;
                    if (isRight && (c.EndsWith("_l") || c.Contains("_l_") || c.Contains("left")) && !c.Contains("right")) continue;
                    if (def.CanonicalName == "head" && c.Contains("forehead")) continue;
                    bool isUpperLimbOrFinger = def.Group == BoneGroup.LeftHandFingers || def.Group == BoneGroup.RightHandFingers ||
                                               def.Group == BoneGroup.LeftArm || def.Group == BoneGroup.RightArm;
                    bool isLowerLimb = def.Group == BoneGroup.LeftLeg || def.Group == BoneGroup.RightLeg;
                    if (isUpperLimbOrFinger && (c.Contains("toe") || c.Contains("foot") || c.Contains("ankle") || c.Contains("heel") || c.Contains("sole") || c.Contains("ball")))
                        continue;
                    if (isLowerLimb && (c.Contains("hand") || c.Contains("wrist") || c.Contains("finger") || c.Contains("palm") || c.Contains("arm")))
                        continue;
                    if (c.EndsWith("_end") && !cleanAlias.EndsWith("_end")) continue;

                    // Prevent bare "spine" alias on spine_0 from prefix-hijacking numbered spine bones (e.g. spine_1, spine_2, spine_3)
                    if (cleanAlias == "spine" && c.StartsWith("spine_", StringComparison.Ordinal))
                    {
                        string tail = c["spine_".Length..];
                        if (tail.Length > 0 && char.IsDigit(tail[0]) && tail != "0" && tail != "00")
                            continue;
                    }

                    if (c == cleanAlias || c.StartsWith(cleanAlias + "_") || c.EndsWith("_" + cleanAlias) || c.Contains("_" + cleanAlias + "_"))
                        return e.Raw;
                }
            }

            return null;
        }

        /// <summary>
        /// Generates a complete auto-mapping dictionary: [TargetDeadlockBoneName] => [SourceBoneName].
        /// </summary>
        public static Dictionary<string, string> AutoMap(Skeleton3D sourceSkeleton, Skeleton3D targetSkeleton)
        {
            var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (targetSkeleton == null || !GodotObject.IsInstanceValid(targetSkeleton) || targetSkeleton.IsQueuedForDeletion()) return mapping;
            if (sourceSkeleton == null || !GodotObject.IsInstanceValid(sourceSkeleton) || sourceSkeleton.IsQueuedForDeletion()) return mapping;

            int srcCount = sourceSkeleton.GetBoneCount();
            var entries = new SourceBoneEntry[srcCount];
            bool isRigifySpine = false;
            bool hasZeroBaseSpine = false;
            for (int s = 0; s < srcCount; s++)
            {
                var entry = new SourceBoneEntry(sourceSkeleton.GetBoneName(s));
                entries[s] = entry;
                if (!entry.IsHelper)
                {
                    if (entry.CleanExact == "spine_001" || entry.CleanNoGltf == "spine_001" || entry.CleanExact.EndsWith("_spine_001"))
                        isRigifySpine = true;
                    if (entry.CleanNoGltf == "spine" || entry.CleanNoGltf == "spine0" || entry.CleanNoGltf == "spine_0")
                        hasZeroBaseSpine = true;
                }
            }

            var usedSourceBones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var def in Definitions)
            {
                string targetBone = ResolveTargetBone(targetSkeleton, def);
                if (string.IsNullOrEmpty(targetBone)) continue;

                string sourceBone = ResolveSourceBoneFast(entries, isRigifySpine, hasZeroBaseSpine, def, usedSourceBones);
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

            // Adjust 3-spine rigs (Mixamo: Spine, Spine1, Spine2 / 3ds Max Biped: Bip01_Spine..2 / Mesh2Motion: spine_01..03)
            // mapped onto 4-spine target rigs (Deadlock: spine_0, spine_1, spine_2, spine_3).
            AdjustThreeSpineToFourSpine(mapping, sourceSkeleton, targetSkeleton);

            // Adjust "Left leg -> Left knee -> Left ankle" rigs (Cats / VRChat / MMD) where "Left leg" was matched as leg_lower
            // and a childless "Thigh.L" helper stole leg_upper or left it out of hierarchy.
            AdjustArticulatedLegChains(mapping, sourceSkeleton, targetSkeleton);

            return mapping;
        }

        private static void AdjustArticulatedLegChains(Dictionary<string, string> mapping, Skeleton3D sourceSkeleton, Skeleton3D targetSkeleton)
        {
            if (sourceSkeleton == null || targetSkeleton == null) return;
            int srcCount = sourceSkeleton.GetBoneCount();
            if (srcCount == 0) return;

            var children = new List<int>[srcCount];
            for (int i = 0; i < srcCount; i++) children[i] = new List<int>();
            for (int i = 0; i < srcCount; i++)
            {
                int p = sourceSkeleton.GetBoneParent(i);
                if (p >= 0 && p < srcCount) children[p].Add(i);
            }

            foreach (string side in new[] { "L", "R" })
            {
                var upperDef = Definitions.FirstOrDefault(d => d.CanonicalName == $"leg_upper_{side}");
                var lowerDef = Definitions.FirstOrDefault(d => d.CanonicalName == $"leg_lower_{side}");
                if (upperDef == null || lowerDef == null) continue;

                string tgtUpper = ResolveTargetBone(targetSkeleton, upperDef);
                string tgtLower = ResolveTargetBone(targetSkeleton, lowerDef);
                if (string.IsNullOrEmpty(tgtUpper) || string.IsNullOrEmpty(tgtLower)) continue;

                string legToken = side == "L" ? "left_leg" : "right_leg";
                string kneeToken = side == "L" ? "left_knee" : "right_knee";
                string calfToken = side == "L" ? "knee_l" : "knee_r";

                for (int legIdx = 0; legIdx < srcCount; legIdx++)
                {
                    string cLeg = CleanBoneName(sourceSkeleton.GetBoneName(legIdx), stripTrailingGltfIndex: true);
                    if (cLeg != legToken && cLeg != legToken.Replace("_", "")) continue;

                    // Look for a direct or nested child matching calf/shin/lowerleg (preferred) or knee (fallback)
                    // under this bare "Left/Right leg"
                    int preferredIdx = -1;
                    int kneeIdx = -1;
                    var q = new Queue<int>();
                    foreach (int ch in children[legIdx]) q.Enqueue(ch);
                    while (q.Count > 0)
                    {
                        int cur = q.Dequeue();
                        string cc = CleanBoneName(sourceSkeleton.GetBoneName(cur), stripTrailingGltfIndex: true);
                        if (cc.Contains("shin") || cc.Contains("calf") || cc.Contains("lowerleg") || cc.Contains("lower_leg"))
                        {
                            preferredIdx = cur;
                            break;
                        }
                        if (kneeIdx < 0 && (cc == kneeToken || cc == kneeToken.Replace("_", "") || cc == calfToken || cc.Contains("knee")))
                        {
                            kneeIdx = cur;
                        }
                        foreach (int ch in children[cur]) q.Enqueue(ch);
                    }

                    int chosenLower = preferredIdx >= 0 ? preferredIdx : kneeIdx;
                    if (chosenLower >= 0)
                    {
                        mapping[tgtUpper] = sourceSkeleton.GetBoneName(legIdx);
                        mapping[tgtLower] = sourceSkeleton.GetBoneName(chosenLower);
                        break;
                    }
                }
            }
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
            // Shift mapped2 -> spine_3 (Upper Chest), keep mid spine -> spine_1 (or spine_2) strictly 1-to-1 with zero duplicate source bone collisions.
            int targetSpine1Idx = targetSkeleton.FindBone("spine_1");
            string targetSpine1Name = targetSpine1Idx != -1 ? targetSkeleton.GetBoneName(targetSpine1Idx) : "spine_1";
            mapping.TryGetValue(targetSpine1Name, out string mapped1);

            // Shift top spine to spine_3 (Upper Chest) so Clavicles and Neck curl forward with the chest
            mapping[targetSpine3Name] = mapped2;

            // Keep spine_1 = mapped1 (Mid) and leave spine_2 empty (auto-collapsed) so no source bone is duplicated across two target slots
            mapping[targetSpine2Name] = "";
            if (!string.IsNullOrEmpty(mapped1))
            {
                mapping[targetSpine1Name] = mapped1;
            }
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
