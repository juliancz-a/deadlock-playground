using System;
using System.Collections.Generic;
using Godot;

namespace DeadlockPlayground.Animation
{
    /// <summary>
    /// Utility for pose copying, pasting, and bilateral L/R pose flipping for Deadlock hero skeletons.
    /// </summary>
    public static class AnimationMirrorHelper
    {
        public class PoseSnapshot
        {
            public Dictionary<string, (Vector3 Position, Quaternion Rotation)> BoneTransforms { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private static PoseSnapshot _poseClipboard;

        public static bool HasClipboardPose => _poseClipboard != null && _poseClipboard.BoneTransforms.Count > 0;

        /// <summary>
        /// Captures the current pose from a Skeleton3D into the pose clipboard.
        /// </summary>
        public static PoseSnapshot CopyPose(Skeleton3D skeleton)
        {
            if (skeleton == null) return null;
            var snapshot = new PoseSnapshot();

            for (int i = 0; i < skeleton.GetBoneCount(); i++)
            {
                string name = skeleton.GetBoneName(i);
                Vector3 pos = skeleton.GetBonePosePosition(i);
                Quaternion rot = skeleton.GetBonePoseRotation(i);
                snapshot.BoneTransforms[name] = (pos, rot);
            }

            _poseClipboard = snapshot;
            return snapshot;
        }

        /// <summary>
        /// Pastes the clipboard pose directly onto the target Skeleton3D.
        /// </summary>
        public static bool PastePose(Skeleton3D skeleton, PoseSnapshot snapshot = null)
        {
            snapshot ??= _poseClipboard;
            if (skeleton == null || snapshot == null) return false;

            foreach (var (boneName, transform) in snapshot.BoneTransforms)
            {
                int boneIdx = skeleton.FindBone(boneName);
                if (boneIdx != -1)
                {
                    skeleton.SetBonePosePosition(boneIdx, transform.Position);
                    skeleton.SetBonePoseRotation(boneIdx, transform.Rotation);
                }
            }

            skeleton.ForceUpdateAllBoneTransforms();
            return true;
        }

        /// <summary>
        /// Pastes the clipboard pose flipped across the sagittal axis (Left <-> Right bone swapping with mirrored transforms).
        /// </summary>
        public static bool PasteMirrorPose(Skeleton3D skeleton, PoseSnapshot snapshot = null)
        {
            snapshot ??= _poseClipboard;
            if (skeleton == null || snapshot == null) return false;

            foreach (var (sourceBoneName, transform) in snapshot.BoneTransforms)
            {
                string targetBoneName = GetOppositeBoneName(sourceBoneName);
                int targetBoneIdx = skeleton.FindBone(targetBoneName);
                if (targetBoneIdx == -1) continue;

                // Mirror position: invert X (sagittal plane in Godot/glTF)
                Vector3 mirroredPos = new Vector3(-transform.Position.X, transform.Position.Y, transform.Position.Z);

                // Mirror rotation quaternion: negate Y and Z axes (or X and W depending on coordinate frame)
                // In Godot (Y-up, X-right, Z-back): Reflection across Y-Z plane negates X axis.
                // For a quaternion q = (x, y, z, w), reflecting across X=0 produces q' = (x, -y, -z, w) or (-x, y, z, -w).
                Quaternion mirroredRot = new Quaternion(-transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, -transform.Rotation.W).Normalized();

                // If bone is a central bone (spine, head, root, pelvis) without L/R counterpart:
                if (sourceBoneName.Equals(targetBoneName, StringComparison.OrdinalIgnoreCase))
                {
                    mirroredPos = new Vector3(-transform.Position.X, transform.Position.Y, transform.Position.Z);
                    mirroredRot = new Quaternion(-transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, -transform.Rotation.W).Normalized();
                }

                skeleton.SetBonePosePosition(targetBoneIdx, mirroredPos);
                skeleton.SetBonePoseRotation(targetBoneIdx, mirroredRot);
            }

            skeleton.ForceUpdateAllBoneTransforms();
            return true;
        }

        /// <summary>
        /// Finds the opposite bilateral counterpart for a given bone name (e.g. arm_upper_L <-> arm_upper_R).
        /// </summary>
        public static string GetOppositeBoneName(string boneName)
        {
            if (string.IsNullOrEmpty(boneName)) return boneName;

            // Common Deadlock / Source 2 bone naming conventions:
            // 1. _L / _R suffix (e.g., arm_upper_L, leg_thigh_R, hand_L)
            if (boneName.EndsWith("_L", StringComparison.OrdinalIgnoreCase))
            {
                return boneName[..^2] + (char.IsLower(boneName[^1]) ? "_r" : "_R");
            }
            if (boneName.EndsWith("_R", StringComparison.OrdinalIgnoreCase))
            {
                return boneName[..^2] + (char.IsLower(boneName[^1]) ? "_l" : "_L");
            }

            // 2. .L / .R suffix
            if (boneName.EndsWith(".L", StringComparison.OrdinalIgnoreCase))
            {
                return boneName[..^2] + (char.IsLower(boneName[^1]) ? ".r" : ".R");
            }
            if (boneName.EndsWith(".R", StringComparison.OrdinalIgnoreCase))
            {
                return boneName[..^2] + (char.IsLower(boneName[^1]) ? ".l" : ".L");
            }

            // 3. _left / _right suffix or infix
            if (boneName.Contains("_left_", StringComparison.OrdinalIgnoreCase))
            {
                return ReplaceCaseInsensitive(boneName, "_left_", "_right_");
            }
            if (boneName.Contains("_right_", StringComparison.OrdinalIgnoreCase))
            {
                return ReplaceCaseInsensitive(boneName, "_right_", "_left_");
            }

            if (boneName.EndsWith("_left", StringComparison.OrdinalIgnoreCase))
            {
                return boneName[..^5] + (char.IsUpper(boneName[^4]) ? "_RIGHT" : "_right");
            }
            if (boneName.EndsWith("_right", StringComparison.OrdinalIgnoreCase))
            {
                return boneName[..^6] + (char.IsUpper(boneName[^5]) ? "_LEFT" : "_left");
            }

            // Central bone (root, pelvis, spine_0, neck_0, head, etc.)
            return boneName;
        }

        private static string ReplaceCaseInsensitive(string input, string search, string replacement)
        {
            int idx = input.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (idx == -1) return input;
            return input.Substring(0, idx) + replacement + input.Substring(idx + search.Length);
        }
    }
}
