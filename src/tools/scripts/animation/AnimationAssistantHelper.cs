using System;
using System.Collections.Generic;
using Godot;

namespace DeadlockPlayground.Animation
{
    /// <summary>
    /// Casual-friendly animation assistants for 1-click loop fixing, curve jitter smoothing,
    /// procedural breathing idle generation, motion amplification, and full clip mirroring.
    /// </summary>
    public static class AnimationAssistantHelper
    {
        /// <summary>
        /// Smoothly cross-blends the end frames of an animation back into Frame 0,
        /// ensuring a completely seamless loop without a jarring snap.
        /// </summary>
        public static void MakeLoopSeamless(Godot.Animation anim, Skeleton3D skeleton, int blendFrames = 6, float fps = 30f)
        {
            if (anim == null || anim.Length <= 0.01) return;

            float blendWindow = Math.Max(0.033f, blendFrames / Math.Max(1f, fps));
            double clipLength = anim.Length;
            double blendStartTime = Math.Max(0.0, clipLength - blendWindow);

            for (int t = 0; t < anim.GetTrackCount(); t++)
            {
                int kCount = anim.TrackGetKeyCount(t);
                if (kCount < 2) continue;

                var trackType = anim.TrackGetType(t);

                if (trackType == Godot.Animation.TrackType.Rotation3D)
                {
                    Quaternion rotStart = ((Quaternion)anim.TrackGetKeyValue(t, 0)).Normalized();

                    // Find or insert key at clipLength with rotStart
                    for (int k = 0; k < anim.TrackGetKeyCount(t); k++)
                    {
                        double kTime = anim.TrackGetKeyTime(t, k);
                        if (kTime >= blendStartTime && kTime < clipLength)
                        {
                            float factor = (float)((kTime - blendStartTime) / (clipLength - blendStartTime));
                            factor = Mathf.Clamp(factor, 0f, 1f);
                            factor = factor * factor * (3f - 2f * factor); // Smoothstep

                            Quaternion curRot = ((Quaternion)anim.TrackGetKeyValue(t, k)).Normalized();
                            Quaternion blended = curRot.Slerp(rotStart, factor).Normalized();
                            anim.TrackSetKeyValue(t, k, blended);
                        }
                    }

                    // Ensure final key matches start key exactly
                    int lastIdx = anim.TrackFindKey(t, clipLength, Godot.Animation.FindMode.Exact);
                    if (lastIdx != -1)
                    {
                        anim.TrackSetKeyValue(t, lastIdx, rotStart);
                    }
                    else
                    {
                        anim.TrackInsertKey(t, clipLength, rotStart);
                    }
                }
                else if (trackType == Godot.Animation.TrackType.Position3D)
                {
                    Vector3 posStart = (Vector3)anim.TrackGetKeyValue(t, 0);

                    for (int k = 0; k < anim.TrackGetKeyCount(t); k++)
                    {
                        double kTime = anim.TrackGetKeyTime(t, k);
                        if (kTime >= blendStartTime && kTime < clipLength)
                        {
                            float factor = (float)((kTime - blendStartTime) / (clipLength - blendStartTime));
                            factor = Mathf.Clamp(factor, 0f, 1f);
                            factor = factor * factor * (3f - 2f * factor);

                            Vector3 curPos = (Vector3)anim.TrackGetKeyValue(t, k);
                            Vector3 blended = curPos.Lerp(posStart, factor);
                            anim.TrackSetKeyValue(t, k, blended);
                        }
                    }

                    int lastIdx = anim.TrackFindKey(t, clipLength, Godot.Animation.FindMode.Exact);
                    if (lastIdx != -1)
                    {
                        anim.TrackSetKeyValue(t, lastIdx, posStart);
                    }
                    else
                    {
                        anim.TrackInsertKey(t, clipLength, posStart);
                    }
                }
            }
        }

        /// <summary>
        /// Applies a Gaussian / moving-average smoothing pass over keyframes to eliminate jitter and robotic motion.
        /// </summary>
        public static void SmoothCurves(Godot.Animation anim, string boneNameFilter, float strength = 0.5f)
        {
            if (anim == null) return;
            float w = Mathf.Clamp(strength * 0.4f, 0.05f, 0.45f);

            for (int t = 0; t < anim.GetTrackCount(); t++)
            {
                string path = anim.TrackGetPath(t).ToString();
                if (!string.IsNullOrEmpty(boneNameFilter) && !path.EndsWith($":{boneNameFilter}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int kCount = anim.TrackGetKeyCount(t);
                if (kCount < 3) continue;

                var trackType = anim.TrackGetType(t);

                if (trackType == Godot.Animation.TrackType.Rotation3D)
                {
                    var smoothed = new Quaternion[kCount];
                    for (int k = 0; k < kCount; k++)
                    {
                        smoothed[k] = ((Quaternion)anim.TrackGetKeyValue(t, k)).Normalized();
                    }

                    for (int k = 1; k < kCount - 1; k++)
                    {
                        Quaternion prev = smoothed[k - 1];
                        Quaternion cur = smoothed[k];
                        Quaternion next = smoothed[k + 1];

                        Quaternion blend1 = prev.Slerp(next, 0.5f).Normalized();
                        smoothed[k] = cur.Slerp(blend1, w).Normalized();
                    }

                    for (int k = 1; k < kCount - 1; k++)
                    {
                        anim.TrackSetKeyValue(t, k, smoothed[k]);
                    }
                }
                else if (trackType == Godot.Animation.TrackType.Position3D)
                {
                    var smoothed = new Vector3[kCount];
                    for (int k = 0; k < kCount; k++)
                    {
                        smoothed[k] = (Vector3)anim.TrackGetKeyValue(t, k);
                    }

                    for (int k = 1; k < kCount - 1; k++)
                    {
                        Vector3 prev = smoothed[k - 1];
                        Vector3 cur = smoothed[k];
                        Vector3 next = smoothed[k + 1];

                        Vector3 avg = (prev + next) * 0.5f;
                        smoothed[k] = cur.Lerp(avg, w);
                    }

                    for (int k = 1; k < kCount - 1; k++)
                    {
                        anim.TrackSetKeyValue(t, k, smoothed[k]);
                    }
                }
            }
        }

        /// <summary>
        /// Bakes subtle procedural breathing and idle sway into the character's spine, chest, and neck.
        /// </summary>
        public static void GenerateBreathingLayer(Godot.Animation anim, Skeleton3D skeleton, float intensity = 1.0f, float sway = 0.5f, float bpm = 14f, float fps = 30f)
        {
            if (anim == null || skeleton == null) return;

            // Target joints for breathing and body rhythm
            string[] spineJoints = { "spine_1", "spine_2", "spine_3", "chest" };
            string[] headJoints = { "neck_0", "head" };

            float breathFreq = (bpm / 60.0f) * Mathf.Tau;
            float duration = (float)Math.Max(1.0, anim.Length);
            int totalFrames = (int)Math.Ceiling(duration * fps);

            foreach (var bName in spineJoints)
            {
                int boneIdx = skeleton.FindBone(bName);
                if (boneIdx == -1) continue;

                int trackIdx = FindOrCreateRotationTrack(anim, skeleton, bName);
                if (trackIdx == -1) continue;

                Quaternion baseRot = skeleton.GetBoneRest(boneIdx).Basis.GetRotationQuaternion().Normalized();

                for (int f = 0; f <= totalFrames; f++)
                {
                    double time = Math.Min(duration, f / (double)fps);
                    float phase = (float)time * breathFreq;

                    // Subtle pitch expansion on chest/spine: +/- 1.8 deg * intensity
                    float pitchAngle = Mathf.Sin(phase) * Mathf.DegToRad(1.8f * intensity);
                    Quaternion breathDelta = new Quaternion(Vector3.Right, pitchAngle);

                    Quaternion existingRot = baseRot;
                    int kIdx = anim.TrackFindKey(trackIdx, time, Godot.Animation.FindMode.Exact);
                    if (kIdx != -1)
                    {
                        existingRot = ((Quaternion)anim.TrackGetKeyValue(trackIdx, kIdx)).Normalized();
                    }

                    Quaternion combined = (existingRot * breathDelta).Normalized();

                    if (kIdx != -1)
                    {
                        anim.TrackSetKeyValue(trackIdx, kIdx, combined);
                    }
                    else
                    {
                        anim.TrackInsertKey(trackIdx, time, combined);
                    }
                }
            }

            // Head and neck sway
            foreach (var bName in headJoints)
            {
                int boneIdx = skeleton.FindBone(bName);
                if (boneIdx == -1) continue;

                int trackIdx = FindOrCreateRotationTrack(anim, skeleton, bName);
                if (trackIdx == -1) continue;

                Quaternion baseRot = skeleton.GetBoneRest(boneIdx).Basis.GetRotationQuaternion().Normalized();

                for (int f = 0; f <= totalFrames; f++)
                {
                    double time = Math.Min(duration, f / (double)fps);
                    float phase = (float)time * (breathFreq * 0.5f); // Half frequency sway

                    float yawAngle = Mathf.Sin(phase) * Mathf.DegToRad(1.2f * sway);
                    float rollAngle = Mathf.Cos(phase) * Mathf.DegToRad(0.8f * sway);
                    Quaternion swayDelta = Quaternion.FromEuler(new Vector3(0, yawAngle, rollAngle));

                    Quaternion existingRot = baseRot;
                    int kIdx = anim.TrackFindKey(trackIdx, time, Godot.Animation.FindMode.Exact);
                    if (kIdx != -1)
                    {
                        existingRot = ((Quaternion)anim.TrackGetKeyValue(trackIdx, kIdx)).Normalized();
                    }

                    Quaternion combined = (existingRot * swayDelta).Normalized();

                    if (kIdx != -1)
                    {
                        anim.TrackSetKeyValue(trackIdx, kIdx, combined);
                    }
                    else
                    {
                        anim.TrackInsertKey(trackIdx, time, combined);
                    }
                }
            }
        }

        /// <summary>
        /// Scales the motion deltas of bone(s) relative to Frame 0 (e.g. 50% for subtle, 150% for exaggerated).
        /// </summary>
        public static void AmplifyMotion(Godot.Animation anim, string boneNameFilter, float scaleFactor)
        {
            if (anim == null) return;

            for (int t = 0; t < anim.GetTrackCount(); t++)
            {
                string path = anim.TrackGetPath(t).ToString();
                if (!string.IsNullOrEmpty(boneNameFilter) && !path.EndsWith($":{boneNameFilter}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int kCount = anim.TrackGetKeyCount(t);
                if (kCount < 2) continue;

                var trackType = anim.TrackGetType(t);

                if (trackType == Godot.Animation.TrackType.Rotation3D)
                {
                    Quaternion baseRot = ((Quaternion)anim.TrackGetKeyValue(t, 0)).Normalized();
                    Quaternion invBase = baseRot.Inverse();

                    for (int k = 1; k < kCount; k++)
                    {
                        Quaternion curRot = ((Quaternion)anim.TrackGetKeyValue(t, k)).Normalized();
                        Quaternion delta = invBase * curRot;

                        // Slerp from Identity towards or beyond delta
                        Quaternion scaledDelta;
                        if (scaleFactor >= 0f && scaleFactor <= 1f)
                        {
                            scaledDelta = Quaternion.Identity.Slerp(delta, scaleFactor);
                        }
                        else
                        {
                            // Extrapolation beyond 1.0: decompose rotation angle and axis
                            float angle = 2f * Mathf.Acos(Mathf.Clamp(delta.W, -1f, 1f));
                            float sinHalf = Mathf.Sqrt(Math.Max(0f, 1f - delta.W * delta.W));
                            Vector3 axis = (sinHalf > 0.0001f) ? new Vector3(delta.X, delta.Y, delta.Z) / sinHalf : Vector3.Up;
                            scaledDelta = new Quaternion(axis, angle * scaleFactor);
                        }

                        Quaternion newRot = (baseRot * scaledDelta).Normalized();
                        anim.TrackSetKeyValue(t, k, newRot);
                    }
                }
                else if (trackType == Godot.Animation.TrackType.Position3D)
                {
                    Vector3 basePos = (Vector3)anim.TrackGetKeyValue(t, 0);

                    for (int k = 1; k < kCount; k++)
                    {
                        Vector3 curPos = (Vector3)anim.TrackGetKeyValue(t, k);
                        Vector3 delta = curPos - basePos;
                        Vector3 newPos = basePos + delta * scaleFactor;
                        anim.TrackSetKeyValue(t, k, newPos);
                    }
                }
            }
        }

        /// <summary>
        /// Flips the entire animation across the character's sagittal (L/R) plane.
        /// </summary>
        public static void MirrorWholeAnimation(Godot.Animation anim, Skeleton3D skeleton)
        {
            if (anim == null || skeleton == null) return;

            // Cache all existing track data
            var trackData = new List<(string BoneName, Godot.Animation.TrackType Type, List<(double Time, Variant Value)> Keys)>();

            for (int t = 0; t < anim.GetTrackCount(); t++)
            {
                string path = anim.TrackGetPath(t).ToString();
                int colonIdx = path.LastIndexOf(':');
                if (colonIdx == -1) continue;

                string boneName = path[(colonIdx + 1)..];
                var type = anim.TrackGetType(t);

                var keys = new List<(double Time, Variant Value)>();
                int kCount = anim.TrackGetKeyCount(t);
                for (int k = 0; k < kCount; k++)
                {
                    keys.Add((anim.TrackGetKeyTime(t, k), anim.TrackGetKeyValue(t, k)));
                }

                trackData.Add((boneName, type, keys));
            }

            // Clear tracks and rebuild mirrored
            while (anim.GetTrackCount() > 0)
            {
                anim.RemoveTrack(0);
            }

            string skeletonPath = "%GeneralSkeleton";

            foreach (var (boneName, type, keys) in trackData)
            {
                string targetBoneName = AnimationMirrorHelper.GetOppositeBoneName(boneName);
                int t = anim.AddTrack(type);
                anim.TrackSetPath(t, $"{skeletonPath}:{targetBoneName}");

                foreach (var (time, value) in keys)
                {
                    if (type == Godot.Animation.TrackType.Rotation3D)
                    {
                        Quaternion rot = ((Quaternion)value).Normalized();
                        // Mirror rotation: negate X and W
                        Quaternion mirrored = new Quaternion(-rot.X, rot.Y, rot.Z, -rot.W).Normalized();
                        anim.TrackInsertKey(t, time, mirrored);
                    }
                    else if (type == Godot.Animation.TrackType.Position3D)
                    {
                        Vector3 pos = (Vector3)value;
                        // Invert X
                        Vector3 mirrored = new Vector3(-pos.X, pos.Y, pos.Z);
                        anim.TrackInsertKey(t, time, mirrored);
                    }
                }
            }
        }

        private static int FindOrCreateRotationTrack(Godot.Animation anim, Skeleton3D skeleton, string boneName)
        {
            for (int t = 0; t < anim.GetTrackCount(); t++)
            {
                if (anim.TrackGetType(t) == Godot.Animation.TrackType.Rotation3D &&
                    anim.TrackGetPath(t).ToString().EndsWith($":{boneName}", StringComparison.OrdinalIgnoreCase))
                {
                    return t;
                }
            }

            int track = anim.AddTrack(Godot.Animation.TrackType.Rotation3D);
            anim.TrackSetPath(track, $"%GeneralSkeleton:{boneName}");
            return track;
        }
    }
}
