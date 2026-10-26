using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using DeadlockPlayground.Painter;

namespace DeadlockPlayground.Animation
{
    /// <summary>
    /// Geometry-driven hierarchical model-space retargeter converting external animation clips
    /// (Mixamo, Mesh2Motion, Unreal, etc.) onto Deadlock Source 2 hero skeletons.
    /// All rest-pose alignments, anatomical proportions, and torso clearances are computed
    /// dynamically and mathematically from each hero's actual Skeleton3D and MeshInstance3D geometry.
    /// </summary>
    public static class AnimationRetargeter
    {
        public enum SourcePoseMode
        {
            AutoDetect,    // Dynamic vector alignment
            StrictTPose,   // Mixamo / Mesh2Motion standard T-Pose
            APose,         // A-Pose (30° / 45°)
            FrameZero      // Use Frame 0 as reference pose
        }

        public enum SpineRetargetMode
        {
            SmoothSpline4Bone, // Recommended: Smoothly resamples 3 source spines across Deadlock's 4 spines with zero kinks
            DirectBoneMap      // Direct 1:1 bone mapping
        }

        public class RetargetOptions
        {
            /// <summary>Source skeleton reference pose calibration mode.</summary>
            public SourcePoseMode PoseMode { get; set; } = SourcePoseMode.AutoDetect;

            /// <summary>Spine chain distribution mode.</summary>
            public SpineRetargetMode SpineMode { get; set; } = SpineRetargetMode.SmoothSpline4Bone;

            /// <summary>Optional manual arm pitch tweak in degrees (-60° to +60°). Defaults to 0° (0° = 100% dynamic vector alignment from bone geometry).</summary>
            public float ArmPitchOffsetDeg { get; set; } = 0f;

            /// <summary>Optional manual arm clearance tweak in degrees (-20° to +20°). Defaults to 0° (0° = 100% dynamic mesh volumetric clearance).</summary>
            public float ArmClearanceOffsetDeg { get; set; } = 0f;

            /// <summary>Optional manual spine tilt tweak in degrees (-30° to +30°). Defaults to 0° (0° = 100% dynamic arc-length redistribution).</summary>
            public float SpinePitchOffsetDeg { get; set; } = 0f;

            /// <summary>Pelvis vertical displacement height scale (0.5x to 1.5x)</summary>
            public float PelvisHeightScale { get; set; } = 1.0f;

            /// <summary>Manual vertical ground / floor offset in meters (-0.50m to +0.50m). Defaults to 0.00m.</summary>
            public float FloorOffsetMeters { get; set; } = 0.0f;

            /// <summary>Subtracts linear travel drift so locomotion clips play in-place while preserving local pelvic sway and weight shift.</summary>
            public bool LockHorizontalRootMotion { get; set; } = false;

            /// <summary>Dynamically adjusts pelvis height each frame so character feet plant firmly on the floor (Y = 0) without mid-air floating.</summary>
            public bool AutoGroundFeet { get; set; } = true;

            /// <summary>Applies per-leg analytic two-bone inverse kinematics to guarantee both feet plant firmly on the floor simultaneously without floating.</summary>
            public bool EnableFootIK { get; set; } = true;

            /// <summary>Soft-IK reach curve starting at 92% of leg length to prevent knee popping at full extension.</summary>
            public bool SoftIK { get; set; } = true;

            /// <summary>Temporal smoothing filter over baked knee flexion and pole vector trajectory to eliminate frame-to-frame snaps.</summary>
            public bool SmoothKnees { get; set; } = true;

            /// <summary>Derives knee bend-plane pole vector from authentic rest anatomy instead of FK cross-product noise.</summary>
            public bool AnatomicalKneePole { get; set; } = true;

            /// <summary>Target evaluation frame rate (defaults to 30 FPS)</summary>
            public float TargetFps { get; set; } = 30f;

            /// <summary>Name of the retargeted animation clip</summary>
            public string TargetAnimName { get; set; } = "retargeted_anim";

            /// <summary>Root skeleton path in the target scene (e.g. "GeneralSkeleton")</summary>
            public string SkeletonNodePath { get; set; } = "GeneralSkeleton";
        }

        /// <summary>
        /// Volumetric capsule profile measuring the hero's actual 3D mesh volume
        /// across vertical torso bands (including jackets, lapels, scarves, armor).
        /// </summary>
        public class TorsoVolumeProfile
        {
            public struct TorsoSlice
            {
                public float YMin;
                public float YMax;
                public float YCenter;
                public Vector3 Center;
                public float FrontRadius; // Chest depth (Z)
                public float SideRadius;  // Torso lateral width (X)
            }

            public List<TorsoSlice> Slices { get; } = new();
            public float ForearmRadius { get; set; } = 0.055f;
            public bool HasMeshData { get; set; } = false;

            public void GetSliceRadiiAtHeight(float y, out Vector3 center, out float frontR, out float sideR)
            {
                if (Slices.Count == 0)
                {
                    center = Vector3.Zero;
                    frontR = 0.16f;
                    sideR = 0.20f;
                    return;
                }

                if (y <= Slices[0].YCenter)
                {
                    center = Slices[0].Center;
                    frontR = Slices[0].FrontRadius;
                    sideR = Slices[0].SideRadius;
                    return;
                }

                if (y >= Slices[^1].YCenter)
                {
                    center = Slices[^1].Center;
                    frontR = Slices[^1].FrontRadius;
                    sideR = Slices[^1].SideRadius;
                    return;
                }

                for (int i = 0; i < Slices.Count - 1; i++)
                {
                    if (y >= Slices[i].YCenter && y <= Slices[i + 1].YCenter)
                    {
                        float span = Math.Max(0.0001f, Slices[i + 1].YCenter - Slices[i].YCenter);
                        float t = (y - Slices[i].YCenter) / span;
                        center = Slices[i].Center.Lerp(Slices[i + 1].Center, t);
                        frontR = Mathf.Lerp(Slices[i].FrontRadius, Slices[i + 1].FrontRadius, t);
                        sideR = Mathf.Lerp(Slices[i].SideRadius, Slices[i + 1].SideRadius, t);
                        return;
                    }
                }

                center = Slices[^1].Center;
                frontR = Slices[^1].FrontRadius;
                sideR = Slices[^1].SideRadius;
            }
        }

        /// <summary>
        /// Computes shortest-arc quaternion rotating from vector 'from' to vector 'to'.
        /// Robust against parallel, anti-parallel (180°), and degenerate zero vectors.
        /// </summary>
        public static Quaternion ShortestArc(Vector3 from, Vector3 to)
        {
            float fromLenSq = from.LengthSquared();
            float toLenSq = to.LengthSquared();
            if (fromLenSq < 1e-8f || toLenSq < 1e-8f)
            {
                return Quaternion.Identity;
            }

            Vector3 uFrom = from / Mathf.Sqrt(fromLenSq);
            Vector3 uTo = to / Mathf.Sqrt(toLenSq);
            float dot = Mathf.Clamp(uFrom.Dot(uTo), -1.0f, 1.0f);

            if (dot >= 0.999999f)
            {
                return Quaternion.Identity;
            }

            if (dot <= -0.999999f)
            {
                Vector3 axis = Vector3.Up.Cross(uFrom);
                if (axis.LengthSquared() < 0.001f)
                {
                    axis = Vector3.Right.Cross(uFrom);
                }
                return new Quaternion(axis.Normalized(), Mathf.Pi);
            }

            Vector3 cross = uFrom.Cross(uTo);
            float w = 1.0f + dot;
            return SafeNormalize(new Quaternion(cross.X, cross.Y, cross.Z, w));
        }

        /// <summary>
        /// Sanitizes and normalizes quaternions, falling back to Identity if NaN, infinite, or zero length.
        /// </summary>
        public static Quaternion SafeNormalize(Quaternion q)
        {
            if (float.IsNaN(q.X) || float.IsNaN(q.Y) || float.IsNaN(q.Z) || float.IsNaN(q.W) ||
                float.IsInfinity(q.X) || float.IsInfinity(q.Y) || float.IsInfinity(q.Z) || float.IsInfinity(q.W))
            {
                return Quaternion.Identity;
            }
            float lenSq = q.LengthSquared();
            if (lenSq < 1e-8f)
            {
                return Quaternion.Identity;
            }
            return q.Normalized();
        }

        /// <summary>
        /// Scales a rotation delta quaternion by a scalar factor using shortest-arc Slerp.
        /// </summary>
        public static Quaternion ScaleRotationDelta(Quaternion delta, float factor)
        {
            if (delta.W < 0)
            {
                delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);
            }
            return SafeNormalize(Quaternion.Identity.Slerp(delta, factor));
        }

        /// <summary>
        /// Computes topological order of bones so every parent joint is guaranteed to appear
        /// before any of its child joints. Eliminates GLTF arbitrary node ordering bugs and NaNs.
        /// </summary>
        public static int[] GetTopologicalOrder(Skeleton3D skeleton)
        {
            if (skeleton == null) return Array.Empty<int>();
            int count = skeleton.GetBoneCount();
            var order = new List<int>(count);
            var visited = new bool[count];

            void Visit(int b)
            {
                if (b < 0 || b >= count || visited[b]) return;
                int p = skeleton.GetBoneParent(b);
                if (p != -1 && p < count && !visited[p])
                {
                    Visit(p);
                }
                visited[b] = true;
                order.Add(b);
            }

            for (int i = 0; i < count; i++)
            {
                Visit(i);
            }

            return order.ToArray();
        }

        /// <summary>
        /// Auto-detects the character forward facing horizontal axis in model space.
        /// Evaluates toe-to-heel vectors (ball - ankle) or lateral hip cross-product.
        /// </summary>
        public static Vector3 DetectForwardFacingAxis(Skeleton3D skeleton, Vector3[] globalRestPos)
        {
            if (skeleton == null || globalRestPos == null || globalRestPos.Length == 0)
            {
                return new Vector3(0, 0, 1);
            }

            // 1. Toe-to-heel vectors: ball - ankle for left and right feet
            int ballL = FindBoneByNames(skeleton, "ball_l", "toe_l", "toes_l", "lefttoebase");
            int ankleL = FindBoneByNames(skeleton, "ankle_l", "foot_l", "leftfoot");
            int ballR = FindBoneByNames(skeleton, "ball_r", "toe_r", "toes_r", "righttoebase");
            int ankleR = FindBoneByNames(skeleton, "ankle_r", "foot_r", "rightfoot");

            Vector3 footDirSum = Vector3.Zero;
            int footCount = 0;

            if (ballL != -1 && ankleL != -1 && ballL < globalRestPos.Length && ankleL < globalRestPos.Length)
            {
                Vector3 vL = globalRestPos[ballL] - globalRestPos[ankleL];
                vL.Y = 0f;
                if (vL.LengthSquared() > 0.0009f)
                {
                    footDirSum += vL.Normalized();
                    footCount++;
                }
            }

            if (ballR != -1 && ankleR != -1 && ballR < globalRestPos.Length && ankleR < globalRestPos.Length)
            {
                Vector3 vR = globalRestPos[ballR] - globalRestPos[ankleR];
                vR.Y = 0f;
                if (vR.LengthSquared() > 0.0009f)
                {
                    footDirSum += vR.Normalized();
                    footCount++;
                }
            }

            if (footCount > 0 && footDirSum.LengthSquared() > 0.01f)
            {
                return footDirSum.Normalized();
            }

            // 2. Fallback: Lateral hip axis cross Up (Left Hip - Right Hip points to character's LEFT)
            int hipL = FindBoneByNames(skeleton, "leg_upper_l", "thigh_l", "leftupleg");
            int hipR = FindBoneByNames(skeleton, "leg_upper_r", "thigh_r", "rightupleg");

            if (hipL != -1 && hipR != -1 && hipL < globalRestPos.Length && hipR < globalRestPos.Length)
            {
                Vector3 lat = globalRestPos[hipL] - globalRestPos[hipR];
                lat.Y = 0f;
                if (lat.LengthSquared() > 0.0009f)
                {
                    Vector3 fwd = lat.Normalized().Cross(Vector3.Up);
                    if (fwd.LengthSquared() > 0.01f)
                    {
                        return fwd.Normalized();
                    }
                }
            }

            // 3. Fallback: Lateral shoulder axis cross Up
            int armL = FindBoneByNames(skeleton, "arm_upper_l", "upperarm_l", "leftarm");
            int armR = FindBoneByNames(skeleton, "arm_upper_r", "upperarm_r", "rightarm");
            if (armL != -1 && armR != -1 && armL < globalRestPos.Length && armR < globalRestPos.Length)
            {
                Vector3 lat = globalRestPos[armL] - globalRestPos[armR];
                lat.Y = 0f;
                if (lat.LengthSquared() > 0.0009f)
                {
                    Vector3 fwd = lat.Normalized().Cross(Vector3.Up);
                    if (fwd.LengthSquared() > 0.01f)
                    {
                        return fwd.Normalized();
                    }
                }
            }

            return new Vector3(0, 0, 1);
        }

        /// <summary>
        /// Measures hero's authentic 3D mesh volume around torso slices (pelvis through neck),
        /// computing frontal chest depth radius (including jackets/armor) and forearm radius.
        /// </summary>
        public static TorsoVolumeProfile BuildTorsoVolumeProfile(Skeleton3D skeleton, Vector3[] targetRestGlobalPos)
        {
            var profile = new TorsoVolumeProfile();
            if (skeleton == null || targetRestGlobalPos == null || targetRestGlobalPos.Length == 0)
            {
                return profile;
            }

            int pelvisIdx = skeleton.FindBone("pelvis");
            int spine0Idx = skeleton.FindBone("spine_0");
            int spine1Idx = skeleton.FindBone("spine_1");
            int spine2Idx = skeleton.FindBone("spine_2");
            int spine3Idx = skeleton.FindBone("spine_3");
            int neckIdx = skeleton.FindBone("neck_0");

            var spineIndices = new List<int>();
            if (pelvisIdx != -1) spineIndices.Add(pelvisIdx);
            if (spine0Idx != -1) spineIndices.Add(spine0Idx);
            if (spine1Idx != -1) spineIndices.Add(spine1Idx);
            if (spine2Idx != -1) spineIndices.Add(spine2Idx);
            if (spine3Idx != -1) spineIndices.Add(spine3Idx);
            if (neckIdx != -1) spineIndices.Add(neckIdx);

            if (spineIndices.Count < 2)
            {
                for (int i = 0; i < skeleton.GetBoneCount(); i++)
                {
                    string name = skeleton.GetBoneName(i);
                    if (IsPelvisOrHips(name) || IsSpineBone(name) || name.Contains("neck"))
                    {
                        if (!spineIndices.Contains(i)) spineIndices.Add(i);
                    }
                }
                spineIndices.Sort((a, b) => targetRestGlobalPos[a].Y.CompareTo(targetRestGlobalPos[b].Y));
            }

            if (spineIndices.Count < 2)
            {
                profile.Slices.Add(new TorsoVolumeProfile.TorsoSlice
                {
                    YMin = 0.5f,
                    YMax = 1.6f,
                    YCenter = 1.05f,
                    Center = Vector3.Up * 1.05f,
                    FrontRadius = 0.16f,
                    SideRadius = 0.20f
                });
                return profile;
            }

            for (int i = 0; i < spineIndices.Count - 1; i++)
            {
                int bCurr = spineIndices[i];
                int bNext = spineIndices[i + 1];

                float y0 = targetRestGlobalPos[bCurr].Y;
                float y1 = targetRestGlobalPos[bNext].Y;
                float yMin = Math.Min(y0, y1);
                float yMax = Math.Max(y0, y1);
                Vector3 c0 = targetRestGlobalPos[bCurr];
                Vector3 c1 = targetRestGlobalPos[bNext];

                profile.Slices.Add(new TorsoVolumeProfile.TorsoSlice
                {
                    YMin = yMin,
                    YMax = yMax,
                    YCenter = (yMin + yMax) * 0.5f,
                    Center = (c0 + c1) * 0.5f,
                    FrontRadius = 0.16f,
                    SideRadius = 0.20f
                });
            }

            var candidateMeshes = new List<MeshInstance3D>();
            void CollectMeshes(Node node)
            {
                if (node == null) return;
                if (node is MeshInstance3D mi && mi.Visible && HeroMeshHierarchy.IsAuthenticHeroMesh(mi))
                {
                    candidateMeshes.Add(mi);
                }
                foreach (Node child in node.GetChildren())
                {
                    CollectMeshes(child);
                }
            }
            CollectMeshes(skeleton.GetParent() ?? skeleton);

            int upperL = FindBoneByNames(skeleton, "arm_upper_L", "upperarm_l", "leftarm");
            int upperR = FindBoneByNames(skeleton, "arm_upper_R", "upperarm_r", "rightarm");
            float xCore = 0.28f;
            if (upperL != -1 && upperR != -1 && upperL < targetRestGlobalPos.Length && upperR < targetRestGlobalPos.Length)
            {
                xCore = Math.Max(xCore, Math.Max(Math.Abs(targetRestGlobalPos[upperL].X), Math.Abs(targetRestGlobalPos[upperR].X)) + 0.04f);
            }

            int lowerArmL = skeleton.FindBone("arm_lower_L");
            int handL = skeleton.FindBone("hand_L");
            Vector3 elbowPos = lowerArmL != -1 && lowerArmL < targetRestGlobalPos.Length ? targetRestGlobalPos[lowerArmL] : Vector3.Zero;
            Vector3 wristPos = handL != -1 && handL < targetRestGlobalPos.Length ? targetRestGlobalPos[handL] : Vector3.Zero;
            Vector3 armAxis = (wristPos - elbowPos);
            float armLen = armAxis.Length();
            if (armLen > 0.001f) armAxis /= armLen;

            float maxForearmDist = 0.055f;
            int measuredVertsCount = 0;

            var sliceMaxFront = new float[profile.Slices.Count];
            var sliceMaxSide = new float[profile.Slices.Count];

            foreach (var mi in candidateMeshes)
            {
                if (mi.Mesh == null) continue;

                Transform3D toSkeleton;
                if (skeleton.IsInsideTree() && mi.IsInsideTree())
                {
                    toSkeleton = skeleton.GlobalTransform.AffineInverse() * mi.GlobalTransform;
                }
                else if (skeleton.GetParent() == mi.GetParent() && skeleton.GetParent() != null)
                {
                    toSkeleton = skeleton.Transform.AffineInverse() * mi.Transform;
                }
                else
                {
                    toSkeleton = mi.Transform;
                }

                int surfCount = mi.Mesh.GetSurfaceCount();
                for (int s = 0; s < surfCount; s++)
                {
                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count == 0) continue;
                    var verts = (Vector3[])arrays[(int)Mesh.ArrayType.Vertex];
                    if (verts == null) continue;

                    for (int vIdx = 0; vIdx < verts.Length; vIdx++)
                    {
                        Vector3 p = toSkeleton * verts[vIdx];

                        if (armLen > 0.05f)
                        {
                            Vector3 vElbowToP = p - elbowPos;
                            float proj = vElbowToP.Dot(armAxis);
                            if (proj >= 0.1f * armLen && proj <= 0.9f * armLen)
                            {
                                float perpDist = (vElbowToP - proj * armAxis).Length();
                                if (perpDist > 0.02f && perpDist < 0.12f)
                                {
                                    maxForearmDist = Math.Max(maxForearmDist, perpDist);
                                }
                            }
                        }

                        if (Math.Abs(p.X) > xCore) continue;

                        for (int sl = 0; sl < profile.Slices.Count; sl++)
                        {
                            var slice = profile.Slices[sl];
                            if (p.Y >= slice.YMin - 0.02f && p.Y <= slice.YMax + 0.02f)
                            {
                                float dz = Math.Abs(p.Z - slice.Center.Z);
                                float dx = Math.Abs(p.X - slice.Center.X);

                                if (dz < 0.40f && dz > sliceMaxFront[sl]) sliceMaxFront[sl] = dz;
                                if (dx < 0.40f && dx > sliceMaxSide[sl]) sliceMaxSide[sl] = dx;
                                measuredVertsCount++;
                                break;
                            }
                        }
                    }
                }
            }

            if (measuredVertsCount > 20)
            {
                profile.HasMeshData = true;
                profile.ForearmRadius = Mathf.Clamp(maxForearmDist, 0.045f, 0.09f);

                for (int sl = 0; sl < profile.Slices.Count; sl++)
                {
                    var slice = profile.Slices[sl];
                    slice.FrontRadius = Mathf.Clamp(sliceMaxFront[sl], 0.13f, 0.35f);
                    slice.SideRadius = Mathf.Clamp(sliceMaxSide[sl], 0.16f, 0.35f);
                    profile.Slices[sl] = slice;
                }

                int midSlice = Math.Min(2, profile.Slices.Count - 1);
                GD.Print($"[AnimationRetargeter] Measured hero mesh volume ({measuredVertsCount} verts): Chest Depth=[{profile.Slices[midSlice].FrontRadius:F3}m], Torso Width=[{profile.Slices[midSlice].SideRadius:F3}m], Forearm=[{profile.ForearmRadius:F3}m]");
            }
            else
            {
                GD.Print("[AnimationRetargeter] Using procedural anatomical volume bounds (no hero submeshes detected).");
            }

            return profile;
        }

        /// <summary>
        /// Retargets sourceAnim on sourceSkeleton onto targetSkeleton using geometry-driven
        /// vector-to-vector matching, ratio-based clavicle scaling, arc-length spine redistribution,
        /// and mesh-aware volumetric torso clearance.
        /// </summary>
        public static Godot.Animation Retarget(
            Skeleton3D sourceSkeleton,
            Godot.Animation sourceAnim,
            Skeleton3D targetSkeleton,
            Dictionary<string, string> boneMapping,
            RetargetOptions options)
        {
            if (sourceSkeleton == null || sourceAnim == null || targetSkeleton == null)
            {
                GD.PrintErr("[AnimationRetargeter] Invalid arguments: skeletons or source animation is null.");
                return null;
            }

            options ??= new RetargetOptions();
            float fps = options.TargetFps > 0 ? options.TargetFps : 30f;
            double animLength = sourceAnim.Length > 0 ? sourceAnim.Length : 1.0;
            int frameCount = Math.Max(2, (int)Math.Ceiling(animLength * fps) + 1);

            int sourceBoneCount = sourceSkeleton.GetBoneCount();
            int targetBoneCount = targetSkeleton.GetBoneCount();

            // Compute topological traversal order (guarantees parents are computed before children)
            int[] sourceOrder = GetTopologicalOrder(sourceSkeleton);
            int[] targetOrder = GetTopologicalOrder(targetSkeleton);

            // 0. Index Source Animation Tracks (indexed upfront so Frame 0 pose can be sampled if requested)
            var sourceRotTracks = new int[sourceBoneCount];
            var sourcePosTracks = new int[sourceBoneCount];
            Array.Fill(sourceRotTracks, -1);
            Array.Fill(sourcePosTracks, -1);

            int srcTrackCount = sourceAnim.GetTrackCount();
            for (int tr = 0; tr < srcTrackCount; tr++)
            {
                string path = sourceAnim.TrackGetPath(tr).ToString();
                string boneName = path;
                int colonIdx = boneName.LastIndexOf(':');
                if (colonIdx != -1) boneName = boneName[(colonIdx + 1)..];
                int slashIdx = boneName.LastIndexOf('/');
                if (slashIdx != -1) boneName = boneName[(slashIdx + 1)..];

                int s = FindBoneCaseInsensitive(sourceSkeleton, boneName);
                if (s == -1)
                {
                    string clean = HumanoidBoneMapper.CleanBoneName(boneName);
                    for (int i = 0; i < sourceBoneCount; i++)
                    {
                        if (HumanoidBoneMapper.CleanBoneName(sourceSkeleton.GetBoneName(i)) == clean)
                        {
                            s = i;
                            break;
                        }
                    }
                }

                if (s != -1)
                {
                    var type = sourceAnim.TrackGetType(tr);
                    if (type == Godot.Animation.TrackType.Rotation3D)
                    {
                        sourceRotTracks[s] = tr;
                    }
                    else if (type == Godot.Animation.TrackType.Position3D)
                    {
                        sourcePosTracks[s] = tr;
                    }
                    else if (type == Godot.Animation.TrackType.Value)
                    {
                        string prop = path.Contains(':') ? path[(path.LastIndexOf(':') + 1)..].ToLowerInvariant() : "";
                        if (prop.Contains("rot") || prop.Contains("quaternion")) sourceRotTracks[s] = tr;
                        else if (prop.Contains("pos") || prop.Contains("loc")) sourcePosTracks[s] = tr;
                    }
                }
            }

            // 1. Compute Model-Space Rest Transforms for Source Skeleton
            // If sourceSkeleton is nested under an Armature or parent Node3D, evaluate full model space (+Y Up)
            Transform3D sourceToModel = Transform3D.Identity;
            Node currSource = sourceSkeleton;
            while (currSource != null && currSource.GetParent() != null)
            {
                if (currSource.GetParent() is Node3D parent3D)
                {
                    sourceToModel = parent3D.Transform * sourceToModel;
                }
                currSource = currSource.GetParent();
            }

            var sourceRestGlobalRot = new Quaternion[sourceBoneCount];
            var sourceRestGlobalPos = new Vector3[sourceBoneCount];
            for (int i = 0; i < sourceBoneCount; i++)
            {
                sourceRestGlobalRot[i] = Quaternion.Identity;
                sourceRestGlobalPos[i] = Vector3.Zero;
            }

            foreach (int s in sourceOrder)
            {
                int p = sourceSkeleton.GetBoneParent(s);
                Transform3D localRest = sourceSkeleton.GetBoneRest(s);
                Quaternion localRot;
                Vector3 localPos;

                if (options.PoseMode == SourcePoseMode.FrameZero && sourceRotTracks[s] != -1)
                {
                    localRot = SafeInterpolateRotation(sourceAnim, sourceRotTracks[s], 0.0, localRest);
                    localPos = sourcePosTracks[s] != -1
                        ? SafeInterpolatePosition(sourceAnim, sourcePosTracks[s], 0.0, localRest)
                        : localRest.Origin;
                }
                else
                {
                    localRot = SafeNormalize(localRest.Basis.GetRotationQuaternion());
                    localPos = localRest.Origin;
                }

                if (p == -1)
                {
                    sourceRestGlobalRot[s] = SafeNormalize(sourceToModel.Basis.GetRotationQuaternion() * localRot);
                    sourceRestGlobalPos[s] = sourceToModel * localPos;
                }
                else
                {
                    sourceRestGlobalRot[s] = SafeNormalize(sourceRestGlobalRot[p] * localRot);
                    sourceRestGlobalPos[s] = sourceRestGlobalPos[p] + sourceRestGlobalRot[p] * localPos;
                }
            }

            // 2. Compute Initial Model-Space Rest Transforms for Target Skeleton
            var targetRestGlobalRot = new Quaternion[targetBoneCount];
            var targetRestGlobalPos = new Vector3[targetBoneCount];
            for (int i = 0; i < targetBoneCount; i++)
            {
                targetRestGlobalRot[i] = Quaternion.Identity;
                targetRestGlobalPos[i] = Vector3.Zero;
            }

            foreach (int t in targetOrder)
            {
                int p = targetSkeleton.GetBoneParent(t);
                Transform3D localRest = targetSkeleton.GetBoneRest(t);
                Quaternion localRot = SafeNormalize(localRest.Basis.GetRotationQuaternion());
                Vector3 localPos = localRest.Origin;

                if (p == -1)
                {
                    targetRestGlobalRot[t] = localRot;
                    targetRestGlobalPos[t] = localPos;
                }
                else
                {
                    targetRestGlobalRot[t] = SafeNormalize(targetRestGlobalRot[p] * localRot);
                    targetRestGlobalPos[t] = targetRestGlobalPos[p] + targetRestGlobalRot[p] * localPos;
                }
            }

            // 3. Auto-Detect Character Forward Facing Axis & Horizontal Yaw Alignment
            Vector3 fwdSource = DetectForwardFacingAxis(sourceSkeleton, sourceRestGlobalPos);
            Vector3 fwdTarget = DetectForwardFacingAxis(targetSkeleton, targetRestGlobalPos);
            Quaternion qYawAlign = ShortestArc(fwdTarget, fwdSource);

            if (Math.Abs(qYawAlign.W) < 0.99999f)
            {
                for (int t = 0; t < targetBoneCount; t++)
                {
                    targetRestGlobalPos[t] = qYawAlign * targetRestGlobalPos[t];
                    targetRestGlobalRot[t] = SafeNormalize(qYawAlign * targetRestGlobalRot[t]);
                }
            }

            // 4. Build Torso Volume Profile from authentic hero mesh geometry
            TorsoVolumeProfile torsoProfile = BuildTorsoVolumeProfile(targetSkeleton, targetRestGlobalPos);

            // 5. Index Bone Mappings (target -> source)
            var targetToSourceIndex = new int[targetBoneCount];
            Array.Fill(targetToSourceIndex, -1);
            int mappedPelvisTargetIndex = -1;
            int mappedPelvisSourceIndex = -1;

            for (int t = 0; t < targetBoneCount; t++)
            {
                string targetName = targetSkeleton.GetBoneName(t);
                if (boneMapping != null && boneMapping.TryGetValue(targetName, out string sourceName) && !string.IsNullOrWhiteSpace(sourceName))
                {
                    int s = FindBoneCaseInsensitive(sourceSkeleton, sourceName);
                    if (s != -1)
                    {
                        targetToSourceIndex[t] = s;
                        if (IsPelvisOrHips(targetName) || IsPelvisOrHips(sourceName))
                        {
                            mappedPelvisTargetIndex = t;
                            mappedPelvisSourceIndex = s;
                        }
                    }
                }
            }

            if (mappedPelvisTargetIndex == -1)
            {
                mappedPelvisTargetIndex = FindBoneCaseInsensitive(targetSkeleton, "pelvis");
            }
            if (mappedPelvisSourceIndex == -1)
            {
                mappedPelvisSourceIndex = FindBoneByNames(sourceSkeleton, "pelvis", "hips", "hip");
            }

            // 6. Zero-Hardcode Rest Pose Alignment (Hierarchical Vector-to-Vector Matching)
            var targetAlignedRestGlobalRot = (Quaternion[])targetRestGlobalRot.Clone();
            var targetAlignedRestGlobalPos = (Vector3[])targetRestGlobalPos.Clone();

            // Precompute descendant lookup table
            var boneDescendants = new List<int>[targetBoneCount];
            for (int t = 0; t < targetBoneCount; t++)
            {
                boneDescendants[t] = new List<int>();
            }
            for (int t = 0; t < targetBoneCount; t++)
            {
                int curr = targetSkeleton.GetBoneParent(t);
                while (curr != -1)
                {
                    boneDescendants[curr].Add(t);
                    curr = targetSkeleton.GetBoneParent(curr);
                }
            }

            // Manual artistic tweak overrides (default 0°)
            float armPitchRad = Mathf.DegToRad(options.ArmPitchOffsetDeg);
            float armClearRad = Mathf.DegToRad(options.ArmClearanceOffsetDeg);
            Quaternion leftArmTweak = SafeNormalize(new Quaternion(Vector3.Up, -armClearRad) * new Quaternion(Vector3.Back, armPitchRad));
            Quaternion rightArmTweak = SafeNormalize(new Quaternion(Vector3.Up, armClearRad) * new Quaternion(Vector3.Back, -armPitchRad));

            void AlignLimbSegment(string targetParentName, string targetChildName, Quaternion userTweak, Vector3? targetHinge = null, Vector3? sourceHinge = null)
            {
                int tP = FindBoneCaseInsensitive(targetSkeleton, targetParentName);
                int tC = FindBoneCaseInsensitive(targetSkeleton, targetChildName);
                if (tP == -1 || tC == -1) return;

                int sP = targetToSourceIndex[tP];
                int sC = targetToSourceIndex[tC];
                if (sP == -1 || sC == -1) return;

                Vector3 uS = (sourceRestGlobalPos[sC] - sourceRestGlobalPos[sP]).Normalized();
                Vector3 uT = (targetAlignedRestGlobalPos[tC] - targetAlignedRestGlobalPos[tP]).Normalized();

                Quaternion qAlign = ShortestArc(uT, uS);

                // If hinge vectors are supplied (e.g. finger curl plane), align the hinge axis around uS
                Quaternion qRoll = Quaternion.Identity;
                if (targetHinge.HasValue && sourceHinge.HasValue &&
                    targetHinge.Value.LengthSquared() > 1e-6f && sourceHinge.Value.LengthSquared() > 1e-6f)
                {
                    Vector3 hT_rot = qAlign * targetHinge.Value;
                    Vector3 hT_perp = hT_rot - uS * hT_rot.Dot(uS);
                    Vector3 hS_perp = sourceHinge.Value - uS * sourceHinge.Value.Dot(uS);

                    if (hT_perp.LengthSquared() > 1e-6f && hS_perp.LengthSquared() > 1e-6f)
                    {
                        hT_perp = hT_perp.Normalized();
                        hS_perp = hS_perp.Normalized();

                        float cosTheta = Math.Clamp(hT_perp.Dot(hS_perp), -1f, 1f);
                        float sinTheta = (hT_perp.Cross(hS_perp)).Dot(uS);
                        float rollAngle = Mathf.Atan2(sinTheta, cosTheta);
                        qRoll = new Quaternion(uS, rollAngle);
                    }
                }

                Quaternion qFinal = SafeNormalize(userTweak * qRoll * qAlign);

                if (Math.Abs(qFinal.W) < 0.999999f)
                {
                    targetAlignedRestGlobalRot[tP] = SafeNormalize(qFinal * targetAlignedRestGlobalRot[tP]);
                    Vector3 pivot = targetAlignedRestGlobalPos[tP];

                    foreach (int d in boneDescendants[tP])
                    {
                        targetAlignedRestGlobalPos[d] = pivot + qFinal * (targetAlignedRestGlobalPos[d] - pivot);
                        targetAlignedRestGlobalRot[d] = SafeNormalize(qFinal * targetAlignedRestGlobalRot[d]);
                    }
                }
            }

            // Align limb chains hierarchically (Arms & Legs ONLY)
            // Left Arm
            AlignLimbSegment("arm_upper_L", "arm_lower_L", leftArmTweak);
            AlignLimbSegment("arm_lower_L", "hand_L", Quaternion.Identity);

            // Right Arm
            AlignLimbSegment("arm_upper_R", "arm_lower_R", rightArmTweak);
            AlignLimbSegment("arm_lower_R", "hand_R", Quaternion.Identity);

            // Hand & Finger alignments in rest pose
            // Aligns hand model-space rest direction with middle finger knuckle
            AlignLimbSegment("hand_L", "finger_middle_0_L", Quaternion.Identity);
            AlignLimbSegment("hand_R", "finger_middle_0_R", Quaternion.Identity);

            Vector3 ComputePalmNormal(Skeleton3D sk, Vector3[] restPos, string handName, string indexName, string pinkyName, string middleName, bool isLeft)
            {
                int tHand = FindBoneCaseInsensitive(sk, handName);
                int tIndex = FindBoneCaseInsensitive(sk, indexName);
                int tPinky = FindBoneCaseInsensitive(sk, pinkyName);
                int tMiddle = FindBoneCaseInsensitive(sk, middleName);
                if (tHand == -1 || tIndex == -1 || tPinky == -1)
                {
                    return isLeft ? Vector3.Forward : Vector3.Back;
                }
                Vector3 pHand = restPos[tHand];
                Vector3 pIndex = restPos[tIndex];
                Vector3 pPinky = restPos[tPinky];
                Vector3 pMiddle = tMiddle != -1 ? restPos[tMiddle] : 0.5f * (pIndex + pPinky);

                Vector3 vAcross = (pIndex - pPinky).Normalized();
                Vector3 vAlong = (pMiddle - pHand).Normalized();
                Vector3 nPalm = vAcross.Cross(vAlong);
                if (nPalm.LengthSquared() < 1e-4f)
                {
                    return isLeft ? Vector3.Forward : Vector3.Back;
                }
                nPalm = nPalm.Normalized();
                if (isLeft && nPalm.Z < 0f && Math.Abs(nPalm.Z) > 0.3f) nPalm = -nPalm;
                if (!isLeft && nPalm.Z > 0f && Math.Abs(nPalm.Z) > 0.3f) nPalm = -nPalm;
                return nPalm;
            }

            Vector3 palmNormTL = ComputePalmNormal(targetSkeleton, targetAlignedRestGlobalPos, "hand_L", "finger_index_0_L", "finger_pinky_0_L", "finger_middle_0_L", true);
            Vector3 palmNormTR = ComputePalmNormal(targetSkeleton, targetAlignedRestGlobalPos, "hand_R", "finger_index_0_R", "finger_pinky_0_R", "finger_middle_0_R", false);

            int tHandL_bone = FindBoneCaseInsensitive(targetSkeleton, "hand_L");
            int tHandR_bone = FindBoneCaseInsensitive(targetSkeleton, "hand_R");
            int sHandL_bone = tHandL_bone != -1 ? targetToSourceIndex[tHandL_bone] : -1;
            int sHandR_bone = tHandR_bone != -1 ? targetToSourceIndex[tHandR_bone] : -1;

            int sIndexL_bone = targetToSourceIndex[FindBoneCaseInsensitive(targetSkeleton, "finger_index_0_L")];
            int sPinkyL_bone = targetToSourceIndex[FindBoneCaseInsensitive(targetSkeleton, "finger_pinky_0_L")];
            int sMiddleL_bone = targetToSourceIndex[FindBoneCaseInsensitive(targetSkeleton, "finger_middle_0_L")];

            int sIndexR_bone = targetToSourceIndex[FindBoneCaseInsensitive(targetSkeleton, "finger_index_0_R")];
            int sPinkyR_bone = targetToSourceIndex[FindBoneCaseInsensitive(targetSkeleton, "finger_pinky_0_R")];
            int sMiddleR_bone = targetToSourceIndex[FindBoneCaseInsensitive(targetSkeleton, "finger_middle_0_R")];

            Vector3 palmNormSL = (sHandL_bone != -1 && sIndexL_bone != -1 && sPinkyL_bone != -1)
                ? ComputePalmNormal(sourceSkeleton, sourceRestGlobalPos, sourceSkeleton.GetBoneName(sHandL_bone), sourceSkeleton.GetBoneName(sIndexL_bone), sourceSkeleton.GetBoneName(sPinkyL_bone), sMiddleL_bone != -1 ? sourceSkeleton.GetBoneName(sMiddleL_bone) : "", true)
                : palmNormTL;

            Vector3 palmNormSR = (sHandR_bone != -1 && sIndexR_bone != -1 && sPinkyR_bone != -1)
                ? ComputePalmNormal(sourceSkeleton, sourceRestGlobalPos, sourceSkeleton.GetBoneName(sHandR_bone), sourceSkeleton.GetBoneName(sIndexR_bone), sourceSkeleton.GetBoneName(sPinkyR_bone), sMiddleR_bone != -1 ? sourceSkeleton.GetBoneName(sMiddleR_bone) : "", false)
                : palmNormTR;

            void AlignFingerChainFull(string fingerPrefix, bool isLeft)
            {
                string suffix = isLeft ? "_L" : "_R";
                string p0 = $"finger_{fingerPrefix}_0{suffix}";
                string p1 = $"finger_{fingerPrefix}_1{suffix}";
                string p2 = $"finger_{fingerPrefix}_2{suffix}";
                string pEnd = $"finger_{fingerPrefix}_end{suffix}";

                int t0 = FindBoneCaseInsensitive(targetSkeleton, p0);
                int t1 = FindBoneCaseInsensitive(targetSkeleton, p1);
                int t2 = FindBoneCaseInsensitive(targetSkeleton, p2);
                int tEnd = FindBoneCaseInsensitive(targetSkeleton, pEnd);
                if (t0 == -1 || t1 == -1) return;

                int s0 = targetToSourceIndex[t0];
                int s1 = targetToSourceIndex[t1];
                int s2 = t2 != -1 ? targetToSourceIndex[t2] : -1;
                int sEnd = tEnd != -1 ? targetToSourceIndex[tEnd] : -1;

                Vector3 palmNormT = isLeft ? palmNormTL : palmNormTR;
                Vector3 palmNormS = isLeft ? palmNormSL : palmNormSR;

                // 1. Phalanx 0 -> 1
                Vector3 dirT0 = (targetAlignedRestGlobalPos[t1] - targetAlignedRestGlobalPos[t0]).Normalized();
                Vector3 hT0 = palmNormT.Cross(dirT0).Normalized();
                Vector3 hS0 = Vector3.Zero;
                if (s0 != -1 && s1 != -1)
                {
                    Vector3 dirS0 = (sourceRestGlobalPos[s1] - sourceRestGlobalPos[s0]).Normalized();
                    hS0 = palmNormS.Cross(dirS0).Normalized();
                }
                AlignLimbSegment(p0, p1, Quaternion.Identity, hT0, hS0);

                // 2. Phalanx 1 -> 2
                if (t2 != -1)
                {
                    Vector3 dirT1 = (targetAlignedRestGlobalPos[t2] - targetAlignedRestGlobalPos[t1]).Normalized();
                    Vector3 hT1 = palmNormT.Cross(dirT1).Normalized();
                    Vector3 hS1 = Vector3.Zero;
                    if (s1 != -1 && s2 != -1)
                    {
                        Vector3 dirS1 = (sourceRestGlobalPos[s2] - sourceRestGlobalPos[s1]).Normalized();
                        hS1 = palmNormS.Cross(dirS1).Normalized();
                    }
                    AlignLimbSegment(p1, p2, Quaternion.Identity, hT1, hS1);
                }

                // 3. Phalanx 2 -> end
                if (t2 != -1 && tEnd != -1)
                {
                    Vector3 dirT2 = (targetAlignedRestGlobalPos[tEnd] - targetAlignedRestGlobalPos[t2]).Normalized();
                    Vector3 hT2 = palmNormT.Cross(dirT2).Normalized();
                    Vector3 hS2 = Vector3.Zero;
                    if (s2 != -1 && sEnd != -1)
                    {
                        Vector3 dirS2 = (sourceRestGlobalPos[sEnd] - sourceRestGlobalPos[s2]).Normalized();
                        hS2 = palmNormS.Cross(dirS2).Normalized();
                    }
                    AlignLimbSegment(p2, pEnd, Quaternion.Identity, hT2, hS2);
                }
            }

            // Left Hand Finger phalanges (with palm-normal derived curl hinges)
            AlignFingerChainFull("thumb", true);
            AlignFingerChainFull("index", true);
            AlignFingerChainFull("middle", true);
            AlignFingerChainFull("ring", true);
            AlignFingerChainFull("pinky", true);

            // Right Hand Finger phalanges (with palm-normal derived curl hinges)
            AlignFingerChainFull("thumb", false);
            AlignFingerChainFull("index", false);
            AlignFingerChainFull("middle", false);
            AlignFingerChainFull("ring", false);
            AlignFingerChainFull("pinky", false);

            // Note: Single-vector ShortestArc alignment is strictly OMITTED on:
            // 1. leg_upper_L/R -> leg_lower_L/R -> ankle_L/R (humanoid legs stand vertically on the floor; full 3D Two-Bone IK solves leg poses directly onto G_T,ankle(t))
            // 2. ankle_L/R -> ball_L/R (feet in rest pose are already flat on the floor; ShortestArc pitches feet when heel/boot heights differ)
            // 3. Axial core bones (pelvis, spine_0..3, neck_0, head; to prevent S-curve sagittal angle distortion and axial twist)
            // Both feet and axial core bones preserve direct upright rest-pose alignment: Q_offset = (Q_yawAlign * G_S,rest)^-1 * G_T,rest.

            // Toe ball to toe end tip (if ball_end is mapped)
            AlignLimbSegment("ball_L", "ball_end_L", Quaternion.Identity);
            AlignLimbSegment("ball_R", "ball_end_R", Quaternion.Identity);

            // 7. Anatomical Ratio-Based Clavicle Scaling
            float clavicleWeightL = 1.0f;
            float clavicleWeightR = 1.0f;

            float ComputeClavicleWeight(string clavName, string upperName, string lowerName, string handName)
            {
                int tClav = FindBoneCaseInsensitive(targetSkeleton, clavName);
                int tUpper = FindBoneCaseInsensitive(targetSkeleton, upperName);
                int tLower = FindBoneCaseInsensitive(targetSkeleton, lowerName);
                int tHand = FindBoneCaseInsensitive(targetSkeleton, handName);

                if (tClav == -1 || tUpper == -1 || tLower == -1 || tHand == -1) return 1.0f;

                int sClav = targetToSourceIndex[tClav];
                int sUpper = targetToSourceIndex[tUpper];
                int sLower = targetToSourceIndex[tLower];
                int sHand = targetToSourceIndex[tHand];

                if (sClav == -1 || sUpper == -1 || sLower == -1 || sHand == -1) return 1.0f;

                float lSClav = (sourceRestGlobalPos[sUpper] - sourceRestGlobalPos[sClav]).Length();
                float lSArm = lSClav +
                              (sourceRestGlobalPos[sLower] - sourceRestGlobalPos[sUpper]).Length() +
                              (sourceRestGlobalPos[sHand] - sourceRestGlobalPos[sLower]).Length();
                float rS = lSClav / Math.Max(1e-4f, lSArm);

                float lTClav = (targetRestGlobalPos[tUpper] - targetRestGlobalPos[tClav]).Length();
                float lTArm = lTClav +
                              (targetRestGlobalPos[tLower] - targetRestGlobalPos[tUpper]).Length() +
                              (targetRestGlobalPos[tHand] - targetRestGlobalPos[tLower]).Length();
                float rT = lTClav / Math.Max(1e-4f, lTArm);

                return Mathf.Clamp(rS / Math.Max(1e-4f, rT), 0.2f, 1.0f);
            }

            clavicleWeightL = ComputeClavicleWeight("clavicle_L", "arm_upper_L", "arm_lower_L", "hand_L");
            clavicleWeightR = ComputeClavicleWeight("clavicle_R", "arm_upper_R", "arm_lower_R", "hand_R");

            // 8. Spine Posture Pitch Tweak
            // Axial core bones (pelvis, spine_0..3, neck_0, head) preserve direct upright rest pose alignment
            // Q_offset = G_S,rest^-1 * G_T,rest. Optional spine posture pitch is applied to the lowest mapped spine joint.
            if (Math.Abs(options.SpinePitchOffsetDeg) > 0.01f)
            {
                float spinePitchRad = Mathf.DegToRad(options.SpinePitchOffsetDeg);
                Quaternion spineTweak = SafeNormalize(new Quaternion(Vector3.Right, spinePitchRad));

                int spinePitchBone = FindBoneCaseInsensitive(targetSkeleton, "spine_0");
                if (spinePitchBone == -1 || targetToSourceIndex[spinePitchBone] == -1)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        int sp = FindBoneCaseInsensitive(targetSkeleton, $"spine_{i}");
                        if (sp != -1 && targetToSourceIndex[sp] != -1)
                        {
                            spinePitchBone = sp;
                            break;
                        }
                    }
                }

                if (spinePitchBone != -1)
                {
                    targetAlignedRestGlobalRot[spinePitchBone] = SafeNormalize(spineTweak * targetAlignedRestGlobalRot[spinePitchBone]);
                }
            }

            // 9. Compute Alignment Offset Quaternions for Mapped Bones
            // Q_offset = G_S,rest^-1 * G_T,alignedRest
            var qOffset = new Quaternion[targetBoneCount];
            for (int t = 0; t < targetBoneCount; t++)
            {
                int s = targetToSourceIndex[t];
                if (s != -1)
                {
                    qOffset[t] = SafeNormalize(sourceRestGlobalRot[s].Inverse() * targetAlignedRestGlobalRot[t]);
                }
                else
                {
                    qOffset[t] = Quaternion.Identity;
                }
            }

            // Setup Foot Grounding rest measurements (lowest foot bone relative to floor Y = 0)
            int tAnkleL = FindBoneByNames(targetSkeleton, "ankle_L", "ankle_l", "foot_L", "foot_l");
            int tAnkleR = FindBoneByNames(targetSkeleton, "ankle_R", "ankle_r", "foot_R", "foot_r");
            int sAnkleL = tAnkleL != -1 && targetToSourceIndex[tAnkleL] != -1 ? targetToSourceIndex[tAnkleL] : FindBoneByNames(sourceSkeleton, "ankle_l", "foot_l", "leftfoot", "ankle_L");
            int sAnkleR = tAnkleR != -1 && targetToSourceIndex[tAnkleR] != -1 ? targetToSourceIndex[tAnkleR] : FindBoneByNames(sourceSkeleton, "ankle_r", "foot_r", "rightfoot", "ankle_R");

            // Recursive Foot Subtree collection (including attachfoot, sole, ball, ball_end, heels)
            var tFootSubtreeL = CollectFootSubtreeBones(targetSkeleton, tAnkleL, "_l");
            var tFootSubtreeR = CollectFootSubtreeBones(targetSkeleton, tAnkleR, "_r");
            var sFootSubtreeL = CollectFootSubtreeBones(sourceSkeleton, sAnkleL, "_l");
            var sFootSubtreeR = CollectFootSubtreeBones(sourceSkeleton, sAnkleR, "_r");

            float targetSoleRestYL = float.MaxValue;
            foreach (int b in tFootSubtreeL) targetSoleRestYL = Math.Min(targetSoleRestYL, targetRestGlobalPos[b].Y);
            if (targetSoleRestYL == float.MaxValue) targetSoleRestYL = 0f;

            float targetSoleRestYR = float.MaxValue;
            foreach (int b in tFootSubtreeR) targetSoleRestYR = Math.Min(targetSoleRestYR, targetRestGlobalPos[b].Y);
            if (targetSoleRestYR == float.MaxValue) targetSoleRestYR = 0f;

            float sourceSoleRestYL = float.MaxValue;
            foreach (int b in sFootSubtreeL) sourceSoleRestYL = Math.Min(sourceSoleRestYL, sourceRestGlobalPos[b].Y);
            if (sourceSoleRestYL == float.MaxValue) sourceSoleRestYL = 0f;

            float sourceSoleRestYR = float.MaxValue;
            foreach (int b in sFootSubtreeR) sourceSoleRestYR = Math.Min(sourceSoleRestYR, sourceRestGlobalPos[b].Y);
            if (sourceSoleRestYR == float.MaxValue) sourceSoleRestYR = 0f;

            // Leg IK Chain Discovery
            int tLegLowerL = tAnkleL != -1 ? targetSkeleton.GetBoneParent(tAnkleL) : -1;
            int tLegUpperL = tLegLowerL != -1 ? targetSkeleton.GetBoneParent(tLegLowerL) : -1;
            if (tLegUpperL == -1) tLegUpperL = FindBoneByNames(targetSkeleton, "leg_upper_L", "leg_upper_l", "thigh_L", "thigh_l");
            if (tLegLowerL == -1) tLegLowerL = FindBoneByNames(targetSkeleton, "leg_lower_L", "leg_lower_l", "calf_L", "calf_l", "shin_l");

            int tLegLowerR = tAnkleR != -1 ? targetSkeleton.GetBoneParent(tAnkleR) : -1;
            int tLegUpperR = tLegLowerR != -1 ? targetSkeleton.GetBoneParent(tLegLowerR) : -1;
            if (tLegUpperR == -1) tLegUpperR = FindBoneByNames(targetSkeleton, "leg_upper_R", "leg_upper_r", "thigh_R", "thigh_r");
            if (tLegLowerR == -1) tLegLowerR = FindBoneByNames(targetSkeleton, "leg_lower_R", "leg_lower_r", "calf_R", "calf_r", "shin_r");

            int tBallL = FindBoneByNames(targetSkeleton, "ball_L", "ball_l", "toe_L", "toe_l", "toes_l");
            int tBallR = FindBoneByNames(targetSkeleton, "ball_R", "ball_r", "toe_R", "toe_r", "toes_r");

            Vector3 hingeRestL = ComputeRestKneeHingeAxis(targetSkeleton, targetRestGlobalPos, targetRestGlobalRot, tLegUpperL, tLegLowerL, tAnkleL, tBallL, mappedPelvisTargetIndex);
            Vector3 hingeRestR = ComputeRestKneeHingeAxis(targetSkeleton, targetRestGlobalPos, targetRestGlobalRot, tLegUpperR, tLegLowerR, tAnkleR, tBallR, mappedPelvisTargetIndex);

            Vector3 localHingeL = tLegUpperL != -1 ? (targetRestGlobalRot[tLegUpperL].Inverse() * hingeRestL) : Vector3.Right;
            Vector3 localHingeR = tLegUpperR != -1 ? (targetRestGlobalRot[tLegUpperR].Inverse() * hingeRestR) : Vector3.Right;

            Vector3 restCharFwd = Vector3.Zero;
            if (tBallL != -1 && tAnkleL != -1)
            {
                Vector3 toeDir = targetRestGlobalPos[tBallL] - targetRestGlobalPos[tAnkleL];
                toeDir.Y = 0f;
                if (toeDir.LengthSquared() > 1e-4f) restCharFwd = toeDir.Normalized();
            }
            if (restCharFwd.LengthSquared() < 1e-4f && mappedPelvisTargetIndex != -1)
            {
                Vector3 pelvFwd = targetRestGlobalRot[mappedPelvisTargetIndex] * new Vector3(0f, 0f, 1f);
                pelvFwd.Y = 0f;
                if (pelvFwd.LengthSquared() > 1e-4f) restCharFwd = pelvFwd.Normalized();
            }
            if (restCharFwd.LengthSquared() < 1e-4f)
            {
                restCharFwd = new Vector3(0f, 0f, 1f);
            }

            Vector3 pelvisLocalFwd = mappedPelvisTargetIndex != -1
                ? (targetRestGlobalRot[mappedPelvisTargetIndex].Inverse() * restCharFwd).Normalized()
                : new Vector3(0f, 0f, 1f);

            Vector3 downRestL = (tLegUpperL != -1 && tAnkleL != -1)
                ? (targetRestGlobalPos[tAnkleL] - targetRestGlobalPos[tLegUpperL]).Normalized()
                : new Vector3(0f, -1f, 0f);
            Vector3 restKneeFwdL = downRestL.Cross(hingeRestL).Normalized();
            if (restKneeFwdL.Dot(restCharFwd) < 0f) restKneeFwdL = -restKneeFwdL;
            Vector3 restKneeFwdLocalL = tLegUpperL != -1 ? (targetRestGlobalRot[tLegUpperL].Inverse() * restKneeFwdL) : new Vector3(0f, 0f, 1f);

            Vector3 downRestR = (tLegUpperR != -1 && tAnkleR != -1)
                ? (targetRestGlobalPos[tAnkleR] - targetRestGlobalPos[tLegUpperR]).Normalized()
                : new Vector3(0f, -1f, 0f);
            Vector3 restKneeFwdR = downRestR.Cross(hingeRestR).Normalized();
            if (restKneeFwdR.Dot(restCharFwd) < 0f) restKneeFwdR = -restKneeFwdR;
            Vector3 restKneeFwdLocalR = tLegUpperR != -1 ? (targetRestGlobalRot[tLegUpperR].Inverse() * restKneeFwdR) : new Vector3(0f, 0f, 1f);

            List<int> leftLegDescendants = tLegUpperL != -1 ? CollectDescendantsInTopologicalOrder(targetSkeleton, targetOrder, tLegUpperL) : new List<int>();
            List<int> rightLegDescendants = tLegUpperR != -1 ? CollectDescendantsInTopologicalOrder(targetSkeleton, targetOrder, tLegUpperR) : new List<int>();

            // Setup Continuous 4-Bone Spine Resampling (Spline Slerp)
            int tSpine0 = FindBoneCaseInsensitive(targetSkeleton, "spine_0");
            int tSpine1 = FindBoneCaseInsensitive(targetSkeleton, "spine_1");
            int tSpine2 = FindBoneCaseInsensitive(targetSkeleton, "spine_2");
            int tSpine3 = FindBoneCaseInsensitive(targetSkeleton, "spine_3");

            bool is4SpineTarget = tSpine0 != -1 && tSpine1 != -1 && tSpine2 != -1 && tSpine3 != -1;
            int sLowerSpine = tSpine0 != -1 ? targetToSourceIndex[tSpine0] : -1;
            int sMidSpine = tSpine2 != -1 && targetToSourceIndex[tSpine2] != -1
                ? targetToSourceIndex[tSpine2]
                : (tSpine1 != -1 ? targetToSourceIndex[tSpine1] : -1);
            int sChestSpine = tSpine3 != -1 ? targetToSourceIndex[tSpine3] : -1;
            bool canUseSmoothSpline = is4SpineTarget && sLowerSpine != -1 && sMidSpine != -1 && sChestSpine != -1 &&
                                      targetToSourceIndex[tSpine1] != -1 &&
                                      options.SpineMode == SpineRetargetMode.SmoothSpline4Bone;

            // 11. Initialize Destination Animation
            var retargetedAnim = new Godot.Animation
            {
                Length = (float)animLength,
                Step = 1.0f / fps,
                LoopMode = sourceAnim.LoopMode != Godot.Animation.LoopModeEnum.None
                    ? Godot.Animation.LoopModeEnum.Linear
                    : Godot.Animation.LoopModeEnum.None
            };

            string skeletonPrefix = !string.IsNullOrEmpty(options.SkeletonNodePath) ? options.SkeletonNodePath : "GeneralSkeleton";

            // 12. Frame-by-Frame Evaluation
            var currentSourceGlobalRot = new Quaternion[sourceBoneCount];
            var currentSourceGlobalPos = new Vector3[sourceBoneCount];
            var currentSourceLocalRot = new Quaternion[sourceBoneCount];

            var currentTargetGlobalRot = new Quaternion[targetBoneCount];
            var currentTargetGlobalPos = new Vector3[targetBoneCount];

            var bakedRotations = new Quaternion[targetBoneCount, frameCount];
            var bakedPelvisPos = new Vector3[frameCount];
            var frameTargetGlobalRot = new Quaternion[targetBoneCount, frameCount];
            var frameTargetGlobalPos = new Vector3[targetBoneCount, frameCount];
            var goalAnklePosL = new Vector3[frameCount];
            var goalAnklePosR = new Vector3[frameCount];
            var rawPelvisDrop = new float[frameCount];

            // Measure Target Leg Segment Lengths & Maximum Straight-Leg Reach from Rest Pose
            float lThighL = (tLegUpperL != -1 && tLegLowerL != -1)
                ? (targetRestGlobalPos[tLegLowerL] - targetRestGlobalPos[tLegUpperL]).Length()
                : 0.42f;
            float lCalfL = (tLegLowerL != -1 && tAnkleL != -1)
                ? (targetRestGlobalPos[tAnkleL] - targetRestGlobalPos[tLegLowerL]).Length()
                : 0.40f;
            float rMaxL = (lThighL + lCalfL) * 0.992f;

            float lThighR = (tLegUpperR != -1 && tLegLowerR != -1)
                ? (targetRestGlobalPos[tLegLowerR] - targetRestGlobalPos[tLegUpperR]).Length()
                : 0.42f;
            float lCalfR = (tLegLowerR != -1 && tAnkleR != -1)
                ? (targetRestGlobalPos[tAnkleR] - targetRestGlobalPos[tLegLowerR]).Length()
                : 0.40f;
            float rMaxR = (lThighR + lCalfR) * 0.992f;

            // Leg Length Scaling s_leg = (L_T,leg / L_S,leg) * PelvisHeightScale
            float lS_LegL = (mappedPelvisSourceIndex != -1 && sAnkleL != -1)
                ? (sourceRestGlobalPos[mappedPelvisSourceIndex] - sourceRestGlobalPos[sAnkleL]).Length()
                : 0.90f;
            float lS_LegR = (mappedPelvisSourceIndex != -1 && sAnkleR != -1)
                ? (sourceRestGlobalPos[mappedPelvisSourceIndex] - sourceRestGlobalPos[sAnkleR]).Length()
                : 0.90f;
            float lS_Leg = (sAnkleL != -1 && sAnkleR != -1) ? 0.5f * (lS_LegL + lS_LegR) : (sAnkleL != -1 ? lS_LegL : (sAnkleR != -1 ? lS_LegR : 0.90f));

            float lT_LegL = (mappedPelvisTargetIndex != -1 && tAnkleL != -1)
                ? (targetRestGlobalPos[mappedPelvisTargetIndex] - targetRestGlobalPos[tAnkleL]).Length()
                : 0.90f;
            float lT_LegR = (mappedPelvisTargetIndex != -1 && tAnkleR != -1)
                ? (targetRestGlobalPos[mappedPelvisTargetIndex] - targetRestGlobalPos[tAnkleR]).Length()
                : 0.90f;
            float lT_Leg = (tAnkleL != -1 && tAnkleR != -1) ? 0.5f * (lT_LegL + lT_LegR) : (tAnkleL != -1 ? lT_LegL : (tAnkleR != -1 ? lT_LegR : 0.90f));

            float sLeg = (lT_Leg / Math.Max(0.01f, lS_Leg)) * options.PelvisHeightScale;
            float motionScale = sLeg;

            // Pre-sample source hips at t=0 and t=animLength to compute linear root drift velocity
            Vector3 srcHipsStart = mappedPelvisSourceIndex != -1
                ? SampleSourceBoneModelPos(sourceSkeleton, sourceAnim, sourceRotTracks, sourcePosTracks, sourceToModel, mappedPelvisSourceIndex, 0.0)
                : Vector3.Zero;
            Vector3 srcHipsEnd = mappedPelvisSourceIndex != -1
                ? SampleSourceBoneModelPos(sourceSkeleton, sourceAnim, sourceRotTracks, sourcePosTracks, sourceToModel, mappedPelvisSourceIndex, animLength)
                : Vector3.Zero;
            Vector3 totalDriftXZ = new Vector3(srcHipsEnd.X - srcHipsStart.X, 0f, srcHipsEnd.Z - srcHipsStart.Z);

            int tClavL = FindBoneCaseInsensitive(targetSkeleton, "clavicle_L");
            int tClavR = FindBoneCaseInsensitive(targetSkeleton, "clavicle_R");
            int sClavL = tClavL != -1 ? targetToSourceIndex[tClavL] : -1;
            int sClavR = tClavR != -1 ? targetToSourceIndex[tClavR] : -1;

            int tArmUpperL = FindBoneCaseInsensitive(targetSkeleton, "arm_upper_L");
            int tArmLowerL = FindBoneCaseInsensitive(targetSkeleton, "arm_lower_L");
            int tHandL = FindBoneCaseInsensitive(targetSkeleton, "hand_L");

            int tArmUpperR = FindBoneCaseInsensitive(targetSkeleton, "arm_upper_R");
            int tArmLowerR = FindBoneCaseInsensitive(targetSkeleton, "arm_lower_R");
            int tHandR = FindBoneCaseInsensitive(targetSkeleton, "hand_R");

            int tTwistL0 = FindBoneByNames(targetSkeleton, "arm_lower_L_TWIST", "arm_lower_twist_L", "arm_lower_l_twist", "wrist_helper_L");
            int tTwistL1 = FindBoneByNames(targetSkeleton, "arm_lower_L_TWIST1", "arm_lower_twist_1_L", "arm_lower_l_twist1");
            int tTwistR0 = FindBoneByNames(targetSkeleton, "arm_lower_R_TWIST", "arm_lower_twist_R", "arm_lower_r_twist", "wrist_helper_R");
            int tTwistR1 = FindBoneByNames(targetSkeleton, "arm_lower_R_TWIST1", "arm_lower_twist_1_R", "arm_lower_r_twist1");

            for (int f = 0; f < frameCount; f++)
            {
                double time = Math.Min(animLength, f * (1.0 / fps));

                // A. Forward Kinematics on Source Skeleton (including root & Armature transforms)
                foreach (int s in sourceOrder)
                {
                    int p = sourceSkeleton.GetBoneParent(s);
                    Quaternion localRot = SafeInterpolateRotation(sourceAnim, sourceRotTracks[s], time, sourceSkeleton.GetBoneRest(s));
                    Vector3 localPos = SafeInterpolatePosition(sourceAnim, sourcePosTracks[s], time, sourceSkeleton.GetBoneRest(s));

                    currentSourceLocalRot[s] = localRot;

                    if (p == -1)
                    {
                        currentSourceGlobalRot[s] = SafeNormalize(sourceToModel.Basis.GetRotationQuaternion() * localRot);
                        currentSourceGlobalPos[s] = sourceToModel * localPos;
                    }
                    else
                    {
                        currentSourceGlobalRot[s] = SafeNormalize(currentSourceGlobalRot[p] * localRot);
                        currentSourceGlobalPos[s] = currentSourceGlobalPos[p] + currentSourceGlobalRot[p] * localPos;
                    }
                }

                // B. Target Skeleton Evaluation in Strict Topological Parent-to-Child Order
                foreach (int t in targetOrder)
                {
                    int p = targetSkeleton.GetBoneParent(t);
                    int s = targetToSourceIndex[t];

                    if (canUseSmoothSpline && (t == tSpine0 || t == tSpine1 || t == tSpine2 || t == tSpine3))
                    {
                        // Continuous Spine Chain Resampling (Spline Slerp) across all 4 vertebrae
                        if (t == tSpine0)
                        {
                            Quaternion dLower = SafeNormalize(currentSourceGlobalRot[sLowerSpine] * sourceRestGlobalRot[sLowerSpine].Inverse());
                            Quaternion d0 = ScaleRotationDelta(dLower, 0.75f);
                            currentTargetGlobalRot[t] = SafeNormalize(d0 * targetAlignedRestGlobalRot[t]);
                        }
                        else if (t == tSpine1)
                        {
                            Quaternion dLower = SafeNormalize(currentSourceGlobalRot[sLowerSpine] * sourceRestGlobalRot[sLowerSpine].Inverse());
                            Quaternion dMid = SafeNormalize(currentSourceGlobalRot[sMidSpine] * sourceRestGlobalRot[sMidSpine].Inverse());
                            Quaternion dBlend = SafeNormalize(dLower.Slerp(dMid, 0.5f));
                            Quaternion d1 = ScaleRotationDelta(dBlend, 0.75f);
                            currentTargetGlobalRot[t] = SafeNormalize(d1 * targetAlignedRestGlobalRot[t]);
                        }
                        else if (t == tSpine2)
                        {
                            Quaternion dMid = SafeNormalize(currentSourceGlobalRot[sMidSpine] * sourceRestGlobalRot[sMidSpine].Inverse());
                            Quaternion d2 = ScaleRotationDelta(dMid, 0.75f);
                            currentTargetGlobalRot[t] = SafeNormalize(d2 * targetAlignedRestGlobalRot[t]);
                        }
                        else if (t == tSpine3)
                        {
                            // Upper chest reaches the exact target chest orientation for clavicle/neck integrity
                            currentTargetGlobalRot[t] = SafeNormalize(currentSourceGlobalRot[sChestSpine] * qOffset[t]);
                        }
                    }
                    else if (t == tClavL && sClavL != -1)
                    {
                        // Dynamic Left Clavicle Scaling
                        Quaternion sDelta = SafeNormalize(currentSourceGlobalRot[sClavL] * sourceRestGlobalRot[sClavL].Inverse());
                        Quaternion scaledDelta = SafeNormalize(Quaternion.Identity.Slerp(sDelta, clavicleWeightL));
                        currentTargetGlobalRot[t] = SafeNormalize(scaledDelta * targetAlignedRestGlobalRot[t]);
                    }
                    else if (t == tClavR && sClavR != -1)
                    {
                        // Dynamic Right Clavicle Scaling
                        Quaternion sDelta = SafeNormalize(currentSourceGlobalRot[sClavR] * sourceRestGlobalRot[sClavR].Inverse());
                        Quaternion scaledDelta = SafeNormalize(Quaternion.Identity.Slerp(sDelta, clavicleWeightR));
                        currentTargetGlobalRot[t] = SafeNormalize(scaledDelta * targetAlignedRestGlobalRot[t]);
                    }
                    else if (s != -1)
                    {
                        // Mapped bone: hierarchical model-space alignment
                        currentTargetGlobalRot[t] = SafeNormalize(currentSourceGlobalRot[s] * qOffset[t]);
                    }
                    else
                    {
                        // Unmapped bone (e.g. [NONE / SKIP] intermediate spine_1): strictly propagate from animated parent
                        Quaternion localRestRot = SafeNormalize(targetSkeleton.GetBoneRest(t).Basis.GetRotationQuaternion());
                        currentTargetGlobalRot[t] = p == -1
                            ? localRestRot
                            : SafeNormalize(currentTargetGlobalRot[p] * localRestRot);
                    }

                    // Forward position computation
                    if (t == mappedPelvisTargetIndex && mappedPelvisSourceIndex != -1)
                    {
                        Vector3 srcDisp = currentSourceGlobalPos[mappedPelvisSourceIndex] - sourceRestGlobalPos[mappedPelvisSourceIndex];
                        srcDisp *= motionScale;

                        if (options.LockHorizontalRootMotion)
                        {
                            // Subtract linear drift velocity line across the duration, preserving local pelvic weight-shift sway!
                            float driftAlpha = animLength > 0.0001 ? (float)(time / animLength) : 0f;
                            Vector3 currentDriftXZ = totalDriftXZ * driftAlpha * motionScale;
                            srcDisp.X -= currentDriftXZ.X;
                            srcDisp.Z -= currentDriftXZ.Z;
                        }

                        Vector3 targetPos = targetRestGlobalPos[t] + srcDisp;
                        targetPos.Y += options.FloorOffsetMeters;
                        currentTargetGlobalPos[t] = targetPos;
                    }
                    else
                    {
                        Vector3 localRestPos = targetSkeleton.GetBoneRest(t).Origin;
                        currentTargetGlobalPos[t] = p == -1
                            ? localRestPos
                            : currentTargetGlobalPos[p] + currentTargetGlobalRot[p] * localRestPos;
                    }
                }

                // C. Volumetric Torso & Limb Anti-Clipping Solver (Opt-in via ArmClearanceOffsetDeg slider)
                // Gated off by default so face-touching poses (like Crying.fbx) do not have their hands repelled from the face!
                if (options.ArmClearanceOffsetDeg > 0.01f)
                {
                    SolveArmTorsoClearance(tArmUpperL, tArmLowerL, tHandL, torsoProfile, targetSkeleton, currentTargetGlobalRot, currentTargetGlobalPos);
                    SolveArmTorsoClearance(tArmUpperR, tArmLowerR, tHandR, torsoProfile, targetSkeleton, currentTargetGlobalRot, currentTargetGlobalPos);
                }

                // Distribute forearm pronation/supination twist across Deadlock wrist helper bones
                DistributeForearmTwist(targetSkeleton, tArmLowerL, tHandL, tTwistL0, tTwistL1, currentTargetGlobalRot);
                DistributeForearmTwist(targetSkeleton, tArmLowerR, tHandR, tTwistR0, tTwistR1, currentTargetGlobalRot);

                // Step 1: Compute 3D Model-Space Target Ankle Goals G_T,ankle(t)
                Vector3 deltaAnkleS_L = (sAnkleL != -1)
                    ? (currentSourceGlobalPos[sAnkleL] - sourceRestGlobalPos[sAnkleL])
                    : Vector3.Zero;
                Vector3 deltaAnkleS_R = (sAnkleR != -1)
                    ? (currentSourceGlobalPos[sAnkleR] - sourceRestGlobalPos[sAnkleR])
                    : Vector3.Zero;

                Vector3 goalL = (tAnkleL != -1 ? targetRestGlobalPos[tAnkleL] : Vector3.Zero)
                    + deltaAnkleS_L * motionScale
                    + new Vector3(0f, options.FloorOffsetMeters, 0f);

                Vector3 goalR = (tAnkleR != -1 ? targetRestGlobalPos[tAnkleR] : Vector3.Zero)
                    + deltaAnkleS_R * motionScale
                    + new Vector3(0f, options.FloorOffsetMeters, 0f);

                if (options.LockHorizontalRootMotion)
                {
                    float driftAlpha = animLength > 0.0001 ? (float)(time / animLength) : 0f;
                    Vector3 currentDriftXZ = totalDriftXZ * driftAlpha * motionScale;
                    goalL.X -= currentDriftXZ.X;
                    goalL.Z -= currentDriftXZ.Z;
                    goalR.X -= currentDriftXZ.X;
                    goalR.Z -= currentDriftXZ.Z;
                }

                if (options.AutoGroundFeet)
                {
                    float minAnkleYL = (tAnkleL != -1 ? targetRestGlobalPos[tAnkleL].Y : 0.08f) + options.FloorOffsetMeters;
                    float minAnkleYR = (tAnkleR != -1 ? targetRestGlobalPos[tAnkleR].Y : 0.08f) + options.FloorOffsetMeters;
                    if (goalL.Y < minAnkleYL) goalL.Y = minAnkleYL;
                    if (goalR.Y < minAnkleYR) goalR.Y = minAnkleYR;
                }

                goalAnklePosL[f] = goalL;
                goalAnklePosR[f] = goalR;

                // Step 2: Measure straight-leg reach and compute vertical drop deltaY needed so legs can reach ankle goals
                float deltaYL = 0f;
                float rLimitL = rMaxL * 0.995f;
                if (tLegUpperL != -1 && tAnkleL != -1)
                {
                    Vector3 hipPosL = currentTargetGlobalPos[tLegUpperL];
                    Vector3 vL = hipPosL - goalL;
                    float dL = vL.Length();
                    if (dL > rLimitL && hipPosL.Y > goalL.Y)
                    {
                        float dXZ2 = vL.X * vL.X + vL.Z * vL.Z;
                        if (rLimitL * rLimitL <= dXZ2)
                        {
                            deltaYL = vL.Y;
                        }
                        else
                        {
                            deltaYL = vL.Y - Mathf.Sqrt(rLimitL * rLimitL - dXZ2);
                        }
                    }
                }

                float deltaYR = 0f;
                float rLimitR = rMaxR * 0.995f;
                if (tLegUpperR != -1 && tAnkleR != -1)
                {
                    Vector3 hipPosR = currentTargetGlobalPos[tLegUpperR];
                    Vector3 vR = hipPosR - goalR;
                    float dR = vR.Length();
                    if (dR > rLimitR && hipPosR.Y > goalR.Y)
                    {
                        float dXZ2 = vR.X * vR.X + vR.Z * vR.Z;
                        if (rLimitR * rLimitR <= dXZ2)
                        {
                            deltaYR = vR.Y;
                        }
                        else
                        {
                            deltaYR = vR.Y - Mathf.Sqrt(rLimitR * rLimitR - dXZ2);
                        }
                    }
                }

                rawPelvisDrop[f] = Math.Max(0f, Math.Max(deltaYL, deltaYR));

                // Cache frame FK poses before post-processing pass
                for (int t = 0; t < targetBoneCount; t++)
                {
                    frameTargetGlobalRot[t, f] = currentTargetGlobalRot[t];
                    frameTargetGlobalPos[t, f] = currentTargetGlobalPos[t];
                }
            }

            // D2. Smooth Pelvis Reach Clamp (3-frame Gaussian kernel [0.25, 0.50, 0.25])
            var smoothedPelvisDrop = new float[frameCount];
            for (int f = 0; f < frameCount; f++)
            {
                int fPrev = Math.Max(0, f - 1);
                int fNext = Math.Min(frameCount - 1, f + 1);
                smoothedPelvisDrop[f] = 0.25f * rawPelvisDrop[fPrev] + 0.5f * rawPelvisDrop[f] + 0.25f * rawPelvisDrop[fNext];
            }

            // D3. Final Baking & Two-Bone Foot IK Pass
            // Applies smoothed pelvis reach clamping and 3D Two-Bone IK onto G_T,ankle(t)
            for (int f = 0; f < frameCount; f++)
            {
                for (int t = 0; t < targetBoneCount; t++)
                {
                    currentTargetGlobalRot[t] = frameTargetGlobalRot[t, f];
                    currentTargetGlobalPos[t] = frameTargetGlobalPos[t, f];
                }

                float dropY = smoothedPelvisDrop[f];
                if (dropY > 1e-5f)
                {
                    if (mappedPelvisTargetIndex != -1)
                    {
                        currentTargetGlobalPos[mappedPelvisTargetIndex].Y -= dropY;
                    }
                    for (int t = 0; t < targetBoneCount; t++)
                    {
                        if (t != mappedPelvisTargetIndex && targetSkeleton.GetBoneParent(t) != -1)
                        {
                            currentTargetGlobalPos[t].Y -= dropY;
                        }
                    }
                }

                // Step 3: Solve Full 3D Twist-Free Analytic Two-Bone IK Onto G_T,ankle(t)
                if (options.EnableFootIK)
                {
                    Vector3 charFwdCurrent = restCharFwd;
                    if (mappedPelvisTargetIndex != -1)
                    {
                        Vector3 curPelvFwd = currentTargetGlobalRot[mappedPelvisTargetIndex] * pelvisLocalFwd;
                        curPelvFwd.Y = 0f;
                        if (curPelvFwd.LengthSquared() > 1e-4f) charFwdCurrent = curPelvFwd.Normalized();
                    }

                    // Left Leg Two-Bone IK
                    if (tLegUpperL != -1 && tLegLowerL != -1 && tAnkleL != -1)
                    {
                        Vector3 hipPos = currentTargetGlobalPos[tLegUpperL];
                        Vector3 kneePos = currentTargetGlobalPos[tLegLowerL];
                        Vector3 anklePos = currentTargetGlobalPos[tAnkleL];
                        Vector3 goalAnklePos = goalAnklePosL[f];
                        Vector3 frameHingeL = currentTargetGlobalRot[tLegUpperL] * localHingeL;

                        // Cache pre-IK local rotations and local positions for all descendants of leg_upper_L
                        var localRotBeforeIK = new Quaternion[targetBoneCount];
                        var localPosBeforeIK = new Vector3[targetBoneCount];
                        foreach (int b in leftLegDescendants)
                        {
                            int p = targetSkeleton.GetBoneParent(b);
                            if (p != -1)
                            {
                                localRotBeforeIK[b] = SafeNormalize(currentTargetGlobalRot[p].Inverse() * currentTargetGlobalRot[b]);
                                localPosBeforeIK[b] = currentTargetGlobalRot[p].Inverse() * (currentTargetGlobalPos[b] - currentTargetGlobalPos[p]);
                            }
                        }

                        SolveTwoBoneIK(
                            hipPos, kneePos, anklePos, goalAnklePos,
                            currentTargetGlobalRot[tLegUpperL],
                            currentTargetGlobalRot[tLegLowerL],
                            currentTargetGlobalRot[tAnkleL],
                            frameHingeL,
                            restKneeFwdLocalL,
                            charFwdCurrent,
                            options.SoftIK,
                            options.AnatomicalKneePole,
                            out Quaternion newHipRotL,
                            out Quaternion newKneeRotL,
                            out Quaternion newAnkleRotL,
                            out Vector3 solvedKneeL,
                            out Vector3 solvedAnkleL);

                        currentTargetGlobalRot[tLegUpperL] = newHipRotL;
                        currentTargetGlobalRot[tLegLowerL] = newKneeRotL;
                        currentTargetGlobalRot[tAnkleL] = newAnkleRotL;

                        currentTargetGlobalPos[tLegLowerL] = solvedKneeL;
                        currentTargetGlobalPos[tAnkleL] = solvedAnkleL;
                        Vector3 newAnklePos = solvedAnkleL;

                        // Re-propagate model-space transforms to all descendant and helper bones in topological order
                        foreach (int b in leftLegDescendants)
                        {
                            if (b == tLegLowerL || b == tAnkleL) continue;
                            int p = targetSkeleton.GetBoneParent(b);
                            if (p != -1)
                            {
                                currentTargetGlobalRot[b] = SafeNormalize(currentTargetGlobalRot[p] * localRotBeforeIK[b]);
                                currentTargetGlobalPos[b] = currentTargetGlobalPos[p] + currentTargetGlobalRot[p] * localPosBeforeIK[b];
                            }
                        }

                        Vector3 footDispL = newAnklePos - anklePos;
                        foreach (int b in tFootSubtreeL)
                        {
                            if (b != tAnkleL && !leftLegDescendants.Contains(b))
                            {
                                currentTargetGlobalPos[b] += footDispL;
                            }
                        }
                    }

                    // Right Leg Two-Bone IK
                    if (tLegUpperR != -1 && tLegLowerR != -1 && tAnkleR != -1)
                    {
                        Vector3 hipPos = currentTargetGlobalPos[tLegUpperR];
                        Vector3 kneePos = currentTargetGlobalPos[tLegLowerR];
                        Vector3 anklePos = currentTargetGlobalPos[tAnkleR];
                        Vector3 goalAnklePos = goalAnklePosR[f];
                        Vector3 frameHingeR = currentTargetGlobalRot[tLegUpperR] * localHingeR;

                        // Cache pre-IK local rotations and local positions for all descendants of leg_upper_R
                        var localRotBeforeIK = new Quaternion[targetBoneCount];
                        var localPosBeforeIK = new Vector3[targetBoneCount];
                        foreach (int b in rightLegDescendants)
                        {
                            int p = targetSkeleton.GetBoneParent(b);
                            if (p != -1)
                            {
                                localRotBeforeIK[b] = SafeNormalize(currentTargetGlobalRot[p].Inverse() * currentTargetGlobalRot[b]);
                                localPosBeforeIK[b] = currentTargetGlobalRot[p].Inverse() * (currentTargetGlobalPos[b] - currentTargetGlobalPos[p]);
                            }
                        }

                        SolveTwoBoneIK(
                            hipPos, kneePos, anklePos, goalAnklePos,
                            currentTargetGlobalRot[tLegUpperR],
                            currentTargetGlobalRot[tLegLowerR],
                            currentTargetGlobalRot[tAnkleR],
                            frameHingeR,
                            restKneeFwdLocalR,
                            charFwdCurrent,
                            options.SoftIK,
                            options.AnatomicalKneePole,
                            out Quaternion newHipRotR,
                            out Quaternion newKneeRotR,
                            out Quaternion newAnkleRotR,
                            out Vector3 solvedKneeR,
                            out Vector3 solvedAnkleR);

                        currentTargetGlobalRot[tLegUpperR] = newHipRotR;
                        currentTargetGlobalRot[tLegLowerR] = newKneeRotR;
                        currentTargetGlobalRot[tAnkleR] = newAnkleRotR;

                        currentTargetGlobalPos[tLegLowerR] = solvedKneeR;
                        currentTargetGlobalPos[tAnkleR] = solvedAnkleR;
                        Vector3 newAnklePos = solvedAnkleR;

                        // Re-propagate model-space transforms to all descendant and helper bones in topological order
                        foreach (int b in rightLegDescendants)
                        {
                            if (b == tLegLowerR || b == tAnkleR) continue;
                            int p = targetSkeleton.GetBoneParent(b);
                            if (p != -1)
                            {
                                currentTargetGlobalRot[b] = SafeNormalize(currentTargetGlobalRot[p] * localRotBeforeIK[b]);
                                currentTargetGlobalPos[b] = currentTargetGlobalPos[p] + currentTargetGlobalRot[p] * localPosBeforeIK[b];
                            }
                        }

                        Vector3 footDispR = newAnklePos - anklePos;
                        foreach (int b in tFootSubtreeR)
                        {
                            if (b != tAnkleR && !rightLegDescendants.Contains(b))
                            {
                                currentTargetGlobalPos[b] += footDispR;
                            }
                        }
                    }
                }

                // Bake parent-local rotations from final currentTargetGlobalRot
                for (int t = 0; t < targetBoneCount; t++)
                {
                    int p = targetSkeleton.GetBoneParent(t);
                    Quaternion localPoseRot;
                    if (p == -1)
                    {
                        localPoseRot = currentTargetGlobalRot[t];
                    }
                    else
                    {
                        localPoseRot = SafeNormalize(currentTargetGlobalRot[p].Inverse() * currentTargetGlobalRot[t]);
                    }

                    if (localPoseRot.W < 0)
                    {
                        localPoseRot = new Quaternion(-localPoseRot.X, -localPoseRot.Y, -localPoseRot.Z, -localPoseRot.W);
                    }

                    bakedRotations[t, f] = localPoseRot;
                }

                if (mappedPelvisTargetIndex != -1)
                {
                    int pBone = mappedPelvisTargetIndex;
                    int pParent = targetSkeleton.GetBoneParent(pBone);
                    Vector3 localPos = pParent == -1
                        ? currentTargetGlobalPos[pBone]
                        : currentTargetGlobalRot[pParent].Inverse() * (currentTargetGlobalPos[pBone] - currentTargetGlobalPos[pParent]);
                    bakedPelvisPos[f] = localPos;
                }
            }

            // Optional: Gaussian Temporal Smoothing on Leg Joints to Eliminate Micro-Jitter
            if (options.SmoothKnees && frameCount > 2)
            {
                int[] legBonesToSmooth = new int[] { tLegUpperL, tLegLowerL, tLegUpperR, tLegLowerR };
                foreach (int b in legBonesToSmooth)
                {
                    if (b == -1) continue;
                    var tempSmoothed = new Quaternion[frameCount];
                    for (int f = 0; f < frameCount; f++)
                    {
                        int fPrev = retargetedAnim.LoopMode != Godot.Animation.LoopModeEnum.None
                            ? (f - 1 + frameCount) % frameCount
                            : Math.Max(0, f - 1);
                        int fNext = retargetedAnim.LoopMode != Godot.Animation.LoopModeEnum.None
                            ? (f + 1) % frameCount
                            : Math.Min(frameCount - 1, f + 1);

                        Quaternion q0 = bakedRotations[b, fPrev];
                        Quaternion q1 = bakedRotations[b, f];
                        Quaternion q2 = bakedRotations[b, fNext];

                        if (q0.Dot(q1) < 0f) q0 = -q0;
                        if (q2.Dot(q1) < 0f) q2 = -q2;

                        tempSmoothed[f] = new Quaternion(
                            0.2f * q0.X + 0.6f * q1.X + 0.2f * q2.X,
                            0.2f * q0.Y + 0.6f * q1.Y + 0.2f * q2.Y,
                            0.2f * q0.Z + 0.6f * q1.Z + 0.2f * q2.Z,
                            0.2f * q0.W + 0.6f * q1.W + 0.2f * q2.W
                        ).Normalized();
                    }
                    for (int f = 0; f < frameCount; f++)
                    {
                        bakedRotations[b, f] = tempSmoothed[f];
                    }
                }
            }

            // 13. Track Generation & Selective Lossless Baking vs. RDP Simplification
            int totalInsertedKeys = 0;
            var rdpKeyIndices = new List<int>(frameCount);
            const float RotToleranceRad = 0.0087f; // ~0.5° tolerance preserves exact 3D pose

            for (int t = 0; t < targetBoneCount; t++)
            {
                string bName = targetSkeleton.GetBoneName(t);
                bool isForearmTwist = (t == tTwistL0 || t == tTwistL1 || t == tTwistR0 || t == tTwistR1);
                if (targetToSourceIndex[t] == -1 && !isForearmTwist) continue;

                Quaternion restRot = SafeNormalize(targetSkeleton.GetBoneRest(t).Basis.GetRotationQuaternion());

                bool isStatic = true;
                for (int f = 1; f < frameCount; f++)
                {
                    float dot = Math.Abs(bakedRotations[t, f].Dot(bakedRotations[t, 0]));
                    if (dot < 0.9999f)
                    {
                        isStatic = false;
                        break;
                    }
                }

                if (isStatic && !isForearmTwist && Math.Abs(bakedRotations[t, 0].Dot(restRot)) >= 0.9999f)
                {
                    continue;
                }

                int rotTr = retargetedAnim.AddTrack(Godot.Animation.TrackType.Rotation3D);
                retargetedAnim.TrackSetPath(rotTr, $"{skeletonPrefix}:{bName}");

                bool isPelvisOrLeg = IsPelvisOrLegOrFoot(bName);

                if (isStatic && !isForearmTwist)
                {
                    retargetedAnim.RotationTrackInsertKey(rotTr, 0.0, bakedRotations[t, 0]);
                    retargetedAnim.RotationTrackInsertKey(rotTr, animLength, bakedRotations[t, 0]);
                    totalInsertedKeys += 2;
                }
                else if (isPelvisOrLeg || isForearmTwist)
                {
                    // Lossless frame baking on Pelvis, Leg/Foot, and Forearm Twist joints:
                    // preserves exact per-frame FK cancellation between hip translation and knee/ankle rotation so planted feet NEVER wobble or slide!
                    for (int f = 0; f < frameCount; f++)
                    {
                        double currTime = Math.Min(animLength, f * (1.0 / fps));
                        retargetedAnim.RotationTrackInsertKey(rotTr, currTime, bakedRotations[t, f]);
                        totalInsertedKeys++;
                    }
                }
                else
                {
                    rdpKeyIndices.Clear();
                    rdpKeyIndices.Add(0);
                    SimplifyRotationCurveRdp(bakedRotations, t, 0, frameCount - 1, fps, animLength, RotToleranceRad, rdpKeyIndices);
                    if (frameCount > 1)
                    {
                        rdpKeyIndices.Add(frameCount - 1);
                    }

                    foreach (int f in rdpKeyIndices)
                    {
                        double currTime = Math.Min(animLength, f * (1.0 / fps));
                        retargetedAnim.RotationTrackInsertKey(rotTr, currTime, bakedRotations[t, f]);
                        totalInsertedKeys++;
                    }
                }
            }

            if (mappedPelvisTargetIndex != -1 && mappedPelvisSourceIndex != -1)
            {
                string pName = targetSkeleton.GetBoneName(mappedPelvisTargetIndex);
                int posTr = retargetedAnim.AddTrack(Godot.Animation.TrackType.Position3D);
                retargetedAnim.TrackSetPath(posTr, $"{skeletonPrefix}:{pName}");

                // Lossless frame baking on Pelvis translation: keeps hip displacement synchronized 1:1 with knee/ankle rotations
                for (int f = 0; f < frameCount; f++)
                {
                    double currTime = Math.Min(animLength, f * (1.0 / fps));
                    retargetedAnim.PositionTrackInsertKey(posTr, currTime, bakedPelvisPos[f]);
                    totalInsertedKeys++;
                }
            }

            GD.Print($"[AnimationRetargeter] Retargeted '{options.TargetAnimName}': {frameCount} frames @ {fps} FPS -> {retargetedAnim.GetTrackCount()} active tracks, {totalInsertedKeys} keys (optimized).");
            return retargetedAnim;
        }

        /// <summary>
        /// Analytic volumetric clearance solver: checks elbow and wrist against the hero's torso ellipsoid
        /// at that height slice, computing the minimal outward push vector and shortest-arc rotation.
        /// </summary>
        private static void SolveArmTorsoClearance(
            int tUpper, int tLower, int tHand,
            TorsoVolumeProfile profile,
            Skeleton3D targetSkeleton,
            Quaternion[] currentGlobalRot,
            Vector3[] currentGlobalPos)
        {
            if (tUpper == -1 || tLower == -1 || profile == null || profile.Slices.Count == 0) return;

            Vector3 pUpper = currentGlobalPos[tUpper];
            Vector3 pLower = currentGlobalPos[tLower];

            // 1. Check Elbow (arm_lower)
            profile.GetSliceRadiiAtHeight(pLower.Y, out Vector3 centerElbow, out float frontRElbow, out float sideRElbow);
            float aElbow = sideRElbow + profile.ForearmRadius;
            float bElbow = frontRElbow + profile.ForearmRadius;
            float dxElbow = pLower.X - centerElbow.X;
            float dzElbow = pLower.Z - centerElbow.Z;
            float rhoSqElbow = (dxElbow / Math.Max(0.001f, aElbow)) * (dxElbow / Math.Max(0.001f, aElbow)) +
                               (dzElbow / Math.Max(0.001f, bElbow)) * (dzElbow / Math.Max(0.001f, bElbow));

            if (rhoSqElbow < 1.0f)
            {
                float rho = Mathf.Sqrt(Math.Max(1e-6f, rhoSqElbow));
                Vector3 targetElbow = new Vector3(
                    centerElbow.X + (dxElbow / rho),
                    pLower.Y,
                    centerElbow.Z + (dzElbow / rho)
                );

                Vector3 vCurr = pLower - pUpper;
                Vector3 vTarget = targetElbow - pUpper;
                Quaternion push = ShortestArc(vCurr, vTarget);

                currentGlobalRot[tUpper] = SafeNormalize(push * currentGlobalRot[tUpper]);
                Vector3 upperToElbow = currentGlobalRot[tUpper] * targetSkeleton.GetBoneRest(tLower).Origin;
                currentGlobalPos[tLower] = pUpper + upperToElbow;
                pLower = currentGlobalPos[tLower];

                currentGlobalRot[tLower] = SafeNormalize(push * currentGlobalRot[tLower]);
                if (tHand != -1)
                {
                    Vector3 elbowToHand = currentGlobalRot[tLower] * targetSkeleton.GetBoneRest(tHand).Origin;
                    currentGlobalPos[tHand] = pLower + elbowToHand;
                }
            }

            // 2. Check Wrist (hand)
            if (tHand != -1)
            {
                Vector3 pHand = currentGlobalPos[tHand];
                profile.GetSliceRadiiAtHeight(pHand.Y, out Vector3 centerHand, out float frontRHand, out float sideRHand);
                float aHand = sideRHand + profile.ForearmRadius;
                float bHand = frontRHand + profile.ForearmRadius;
                float dxHand = pHand.X - centerHand.X;
                float dzHand = pHand.Z - centerHand.Z;
                float rhoSqHand = (dxHand / Math.Max(0.001f, aHand)) * (dxHand / Math.Max(0.001f, aHand)) +
                                  (dzHand / Math.Max(0.001f, bHand)) * (dzHand / Math.Max(0.001f, bHand));

                if (rhoSqHand < 1.0f)
                {
                    float rho = Mathf.Sqrt(Math.Max(1e-6f, rhoSqHand));
                    Vector3 targetHand = new Vector3(
                        centerHand.X + (dxHand / rho),
                        pHand.Y,
                        centerHand.Z + (dzHand / rho)
                    );

                    Vector3 vCurrHand = pHand - pLower;
                    Vector3 vTargetHand = targetHand - pLower;
                    Quaternion pushLower = ShortestArc(vCurrHand, vTargetHand);

                    currentGlobalRot[tLower] = SafeNormalize(pushLower * currentGlobalRot[tLower]);
                    Vector3 elbowToHand = currentGlobalRot[tLower] * targetSkeleton.GetBoneRest(tHand).Origin;
                    currentGlobalPos[tHand] = pLower + elbowToHand;
                }
            }
        }

        /// <summary>
        /// Recursively collects all bones in the foot subtree rooted at rootBone (including rootBone itself and all descendants),
        /// plus any bones on the same side matching foot/heel/sole/ball/attach keywords.
        /// </summary>
        public static List<int> CollectFootSubtreeBones(Skeleton3D skeleton, int rootBone, string sideSuffix = "")
        {
            var result = new List<int>();
            if (skeleton == null || rootBone < 0 || rootBone >= skeleton.GetBoneCount()) return result;

            var inSubtree = new HashSet<int>();
            inSubtree.Add(rootBone);

            int boneCount = skeleton.GetBoneCount();
            for (int i = 0; i < boneCount; i++)
            {
                if (i == rootBone) continue;
                int curr = i;
                while (curr != -1)
                {
                    int p = skeleton.GetBoneParent(curr);
                    if (p == rootBone || inSubtree.Contains(p))
                    {
                        inSubtree.Add(i);
                        break;
                    }
                    curr = p;
                }
            }

            // Also check for hero-specific foot attachment bones on the same side (e.g. attachfoot_l, heel_l, sole_l)
            if (!string.IsNullOrEmpty(sideSuffix))
            {
                string normSide = sideSuffix.ToLowerInvariant();
                for (int i = 0; i < boneCount; i++)
                {
                    if (inSubtree.Contains(i)) continue;
                    string name = skeleton.GetBoneName(i).ToLowerInvariant();
                    bool matchSide = name.EndsWith(normSide) || name.Contains(normSide + "_") || name.Contains("_" + normSide);
                    if (matchSide && (name.Contains("foot") || name.Contains("sole") || name.Contains("heel") || name.Contains("toe") || name.Contains("ball") || name.Contains("attach")))
                    {
                        inSubtree.Add(i);
                    }
                }
            }

            result.AddRange(inSubtree);
            return result;
        }

        /// <summary>
        /// Collects all descendant bones of a given root bone in strict topological (parent-before-child) order.
        /// </summary>
        public static List<int> CollectDescendantsInTopologicalOrder(Skeleton3D skeleton, int[] topologicalOrder, int rootBone)
        {
            var result = new List<int>();
            if (skeleton == null || rootBone < 0 || rootBone >= skeleton.GetBoneCount()) return result;
            var descendants = new HashSet<int> { rootBone };

            foreach (int b in topologicalOrder)
            {
                if (b == rootBone) continue;
                int p = skeleton.GetBoneParent(b);
                if (p != -1 && descendants.Contains(p))
                {
                    descendants.Add(b);
                    result.Add(b);
                }
            }
            return result;
        }

        /// <summary>
        /// Computes the true anatomical knee hinge axis in rest pose.
        /// Rotations around this axis bend the knee forward in the direction of the toes / character forward,
        /// pointing along the character's Right (+X in character model space).
        /// </summary>
        public static Vector3 ComputeRestKneeHingeAxis(
            Skeleton3D skeleton,
            Vector3[] restGlobalPos,
            Quaternion[] restGlobalRot,
            int tLegUpper,
            int tLegLower,
            int tAnkle,
            int tBall,
            int tPelvis)
        {
            // Character anatomical forward and down directions
            Vector3 fwd = Vector3.Zero;
            if (tAnkle != -1 && tBall != -1)
            {
                Vector3 toeDir = restGlobalPos[tBall] - restGlobalPos[tAnkle];
                toeDir.Y = 0f;
                if (toeDir.LengthSquared() > 1e-4f)
                {
                    fwd = toeDir.Normalized();
                }
            }
            if (fwd.LengthSquared() < 1e-4f && tPelvis != -1)
            {
                Vector3 pelvFwd = restGlobalRot[tPelvis] * new Vector3(0f, 0f, 1f);
                pelvFwd.Y = 0f;
                if (pelvFwd.LengthSquared() > 1e-4f) fwd = pelvFwd.Normalized();
            }
            if (fwd.LengthSquared() < 1e-4f)
            {
                fwd = new Vector3(0f, 0f, 1f); // Deadlock character forward (+Z)
            }

            Vector3 down = (tLegUpper != -1 && tAnkle != -1)
                ? (restGlobalPos[tAnkle] - restGlobalPos[tLegUpper]).Normalized()
                : new Vector3(0f, -1f, 0f);

            // Anatomical knee flexion rotates around Right = Forward x Down
            Vector3 charRight = fwd.Cross(down);
            if (charRight.LengthSquared() > 1e-4f)
            {
                charRight = charRight.Normalized();
            }
            else
            {
                charRight = Vector3.Right;
            }

            if (tLegUpper != -1 && tLegLower != -1 && tAnkle != -1)
            {
                Vector3 u1 = restGlobalPos[tLegLower] - restGlobalPos[tLegUpper];
                Vector3 u2 = restGlobalPos[tAnkle] - restGlobalPos[tLegLower];
                Vector3 nRest = u1.Cross(u2);
                if (nRest.LengthSquared() > 1e-6f)
                {
                    Vector3 hinge = nRest.Normalized();
                    if (hinge.Dot(charRight) < 0f) hinge = -hinge;
                    return hinge;
                }
            }

            return charRight;
        }

        /// <summary>
        /// Analytic closed-form Twist-Free Two-Bone Inverse Kinematics solver.
        /// Rotates the upper joint (hip/shoulder) and middle joint (knee/elbow) using pure
        /// Delta-Angle Law of Cosines flexions in the anatomical bend plane, followed by a minimal-twist swing
        /// to reach the goal effector position, strictly preserving 100% of the FK axial roll and knee turnout.
        /// Features soft-IK ease-in, anatomical knee pole projection, hyperextension prevention, and forward-bend clamping.
        /// </summary>
        public static void SolveTwoBoneIK(
            Vector3 hipPos,
            Vector3 kneePos,
            Vector3 anklePos,
            Vector3 goalEffectorPos,
            Quaternion hipRot,
            Quaternion kneeRot,
            Quaternion ankleRot,
            Vector3 anatomicalHingeRef,
            Vector3 restKneeFwdLocal,
            Vector3 charFwd,
            bool softIK,
            bool anatomicalKneePole,
            out Quaternion newHipRot,
            out Quaternion newKneeRot,
            out Quaternion newAnkleRot,
            out Vector3 solvedKneePos,
            out Vector3 solvedAnklePos)
        {
            Vector3 u1 = kneePos - hipPos;
            float L1 = u1.Length();
            Vector3 u2 = anklePos - kneePos;
            float L2 = u2.Length();
            float L_total = L1 + L2;

            if (L1 < 1e-4f || L2 < 1e-4f)
            {
                newHipRot = hipRot;
                newKneeRot = kneeRot;
                newAnkleRot = ankleRot;
                solvedKneePos = kneePos;
                solvedAnklePos = anklePos;
                return;
            }

            Vector3 d0 = anklePos - hipPos;
            float D_fk = d0.Length();
            if (D_fk < 1e-4f) D_fk = 1e-4f;
            Vector3 d0_hat = d0 / D_fk;

            Vector3 d = goalEffectorPos - hipPos;
            float dLen = d.Length();
            float minReach = Math.Abs(L1 - L2) + 0.005f;

            // Soft-IK reach ease-in: only eases in the final 1.5% of leg extension to prevent popping
            // preserving natural straight legs without compressing into a permanent squat
            float maxReach = L_total * 0.998f;
            float D_goal;
            if (softIK)
            {
                float dSoft = 0.985f * L_total;
                if (dLen > dSoft)
                {
                    float diff = maxReach - dSoft;
                    D_goal = maxReach - diff * Mathf.Exp(-(dLen - dSoft) / Math.Max(1e-4f, diff));
                }
                else
                {
                    D_goal = dLen;
                }
                D_goal = Math.Clamp(D_goal, minReach, maxReach);
            }
            else
            {
                D_goal = Math.Clamp(dLen, minReach, maxReach);
            }

            Vector3 u_goal = dLen > 1e-4f ? (d / dLen) : d0_hat;

            // 1. Calculate raw FK knee bend direction directly from perpendicular projection
            Vector3 u1_perp = u1 - d0_hat * u1.Dot(d0_hat);
            float h_knee = u1_perp.Length();

            Vector3 fwdRef = charFwd.LengthSquared() > 1e-4f ? charFwd.Normalized() : new Vector3(0f, 0f, 1f);

            // 2. Anatomical pole vector in current frame
            Vector3 anatomicalPole = (anatomicalKneePole && restKneeFwdLocal.LengthSquared() > 1e-4f)
                ? (hipRot * restKneeFwdLocal).Normalized()
                : fwdRef;

            Vector3 poleProj = anatomicalPole - d0_hat * anatomicalPole.Dot(d0_hat);
            Vector3 p_anat = poleProj.LengthSquared() > 1e-4f ? poleProj.Normalized() : fwdRef;

            Vector3 p_hat;
            if (h_knee > 0.003f)
            {
                Vector3 p_fk = u1_perp / h_knee;

                // Check if knee is trying to hyperextend backwards against anatomical forward
                if (p_fk.Dot(p_anat) < -0.05f)
                {
                    p_hat = p_anat;
                }
                else if (anatomicalKneePole && h_knee < 0.020f)
                {
                    float weight = Math.Clamp((h_knee - 0.003f) / 0.017f, 0f, 1f);
                    p_hat = (p_fk * weight + p_anat * (1f - weight)).Normalized();
                }
                else
                {
                    p_hat = p_fk;
                }
            }
            else
            {
                p_hat = p_anat;
            }

            if (p_hat.Dot(p_anat) < 0f)
            {
                p_hat = p_anat;
            }

            // 3. Law of Cosines target angle in bend plane with hyperextension clamp
            float cosBeta = Math.Clamp((L1 * L1 + L2 * L2 - D_goal * D_goal) / (2.0f * L1 * L2), -1.0f, 1.0f);
            float beta_ik = Mathf.Acos(cosBeta);

            // Knee flexion angle: theta = pi - beta. Clamp theta in [1.5 deg, 160 deg]
            float minFlex = 1.5f * (Mathf.Pi / 180f);
            float maxFlex = 160f * (Mathf.Pi / 180f);
            float flex = Math.Clamp(Mathf.Pi - beta_ik, minFlex, maxFlex);
            beta_ik = Mathf.Pi - flex;

            D_goal = Mathf.Sqrt(Math.Max(1e-4f, L1 * L1 + L2 * L2 - 2.0f * L1 * L2 * Mathf.Cos(beta_ik)));

            float cosAlpha_ik = Math.Clamp((L1 * L1 + D_goal * D_goal - L2 * L2) / (2.0f * L1 * D_goal), -1.0f, 1.0f);
            float alpha_ik = Mathf.Acos(cosAlpha_ik);

            // 4. Target bone vectors in the FK bend plane
            Vector3 u1_prime = (L1 * Mathf.Cos(alpha_ik)) * d0_hat + (L1 * Mathf.Sin(alpha_ik)) * p_hat;
            Vector3 u2_prime = (D_goal * d0_hat) - u1_prime;

            // 5. Swing the whole leg to match goal direction
            Quaternion qSwing = ShortestArc(d0_hat, u_goal);

            Vector3 u1_target = qSwing * u1_prime;
            Vector3 u2_target = qSwing * u2_prime;

            solvedKneePos = hipPos + u1_target;
            solvedAnklePos = solvedKneePos + u2_target;

            Quaternion qHipDelta = ShortestArc(u1, u1_target);
            Quaternion qCalfDelta = ShortestArc(u2, u2_target);

            newHipRot = SafeNormalize(qHipDelta * hipRot);
            newKneeRot = SafeNormalize(qCalfDelta * kneeRot);
            newAnkleRot = ankleRot;
        }

        public static void SolveTwoBoneIK(
            Vector3 hipPos,
            Vector3 kneePos,
            Vector3 anklePos,
            Vector3 goalEffectorPos,
            Quaternion hipRot,
            Quaternion kneeRot,
            Quaternion ankleRot,
            Vector3 anatomicalHingeRef,
            Vector3 restKneeFwdLocal,
            Vector3 charFwd,
            bool softIK,
            bool anatomicalKneePole,
            out Quaternion newHipRot,
            out Quaternion newKneeRot,
            out Quaternion newAnkleRot)
        {
            SolveTwoBoneIK(
                hipPos, kneePos, anklePos, goalEffectorPos,
                hipRot, kneeRot, ankleRot,
                anatomicalHingeRef, restKneeFwdLocal, charFwd,
                softIK, anatomicalKneePole,
                out newHipRot, out newKneeRot, out newAnkleRot,
                out _, out _);
        }

        public static void SolveTwoBoneIK(
            Vector3 hipPos,
            Vector3 kneePos,
            Vector3 anklePos,
            Vector3 goalEffectorPos,
            Quaternion hipRot,
            Quaternion kneeRot,
            Quaternion ankleRot,
            Vector3 anatomicalHingeRef,
            out Quaternion newHipRot,
            out Quaternion newKneeRot,
            out Quaternion newAnkleRot)
        {
            SolveTwoBoneIK(
                hipPos, kneePos, anklePos, goalEffectorPos,
                hipRot, kneeRot, ankleRot,
                anatomicalHingeRef,
                Vector3.Zero,
                new Vector3(0f, 0f, 1f),
                false,
                false,
                out newHipRot, out newKneeRot, out newAnkleRot);
        }

        public static void SolveTwoBoneIK(
            Vector3 hipPos,
            Vector3 kneePos,
            Vector3 anklePos,
            Vector3 goalEffectorPos,
            Quaternion hipRot,
            Quaternion kneeRot,
            Quaternion ankleRot,
            out Quaternion newHipRot,
            out Quaternion newKneeRot,
            out Quaternion newAnkleRot)
        {
            SolveTwoBoneIK(
                hipPos, kneePos, anklePos, goalEffectorPos,
                hipRot, kneeRot, ankleRot,
                Vector3.Zero,
                out newHipRot, out newKneeRot, out newAnkleRot);
        }

        public static int FindBoneCaseInsensitive(Skeleton3D skeleton, string boneName)
        {
            if (skeleton == null || string.IsNullOrWhiteSpace(boneName)) return -1;
            int idx = skeleton.FindBone(boneName);
            if (idx != -1) return idx;

            int count = skeleton.GetBoneCount();
            for (int i = 0; i < count; i++)
            {
                if (string.Equals(skeleton.GetBoneName(i), boneName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            string clean = HumanoidBoneMapper.CleanBoneName(boneName);
            for (int i = 0; i < count; i++)
            {
                if (HumanoidBoneMapper.CleanBoneName(skeleton.GetBoneName(i)) == clean)
                    return i;
            }

            return -1;
        }

        private static int FindBoneByNames(Skeleton3D skeleton, params string[] names)
        {
            if (skeleton == null || names == null) return -1;
            int count = skeleton.GetBoneCount();

            foreach (string name in names)
            {
                int idx = skeleton.FindBone(name);
                if (idx != -1) return idx;
            }

            for (int i = 0; i < count; i++)
            {
                string bName = skeleton.GetBoneName(i);
                foreach (string name in names)
                {
                    if (string.Equals(bName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
            }

            for (int i = 0; i < count; i++)
            {
                string bClean = HumanoidBoneMapper.CleanBoneName(skeleton.GetBoneName(i));
                foreach (string name in names)
                {
                    if (bClean == HumanoidBoneMapper.CleanBoneName(name))
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private static Quaternion SafeInterpolateRotation(Godot.Animation anim, int trackIdx, double time, Transform3D defaultRest)
        {
            if (anim == null || trackIdx < 0 || trackIdx >= anim.GetTrackCount())
            {
                return SafeNormalize(defaultRest.Basis.GetRotationQuaternion());
            }

            try
            {
                var trType = anim.TrackGetType(trackIdx);
                if (trType == Godot.Animation.TrackType.Rotation3D)
                {
                    return SafeNormalize(anim.RotationTrackInterpolate(trackIdx, time));
                }
                if (trType == Godot.Animation.TrackType.Value)
                {
                    Variant v = anim.ValueTrackInterpolate(trackIdx, time);
                    if (v.VariantType == Variant.Type.Quaternion)
                    {
                        return SafeNormalize(v.AsQuaternion());
                    }
                    if (v.VariantType == Variant.Type.Vector3)
                    {
                        return SafeNormalize(Quaternion.FromEuler(v.AsVector3()));
                    }
                }
            }
            catch
            {
            }

            return SafeNormalize(defaultRest.Basis.GetRotationQuaternion());
        }

        private static Vector3 SafeInterpolatePosition(Godot.Animation anim, int trackIdx, double time, Transform3D defaultRest)
        {
            if (anim == null || trackIdx < 0 || trackIdx >= anim.GetTrackCount())
            {
                return defaultRest.Origin;
            }

            try
            {
                var trType = anim.TrackGetType(trackIdx);
                if (trType == Godot.Animation.TrackType.Position3D)
                {
                    Vector3 pos = anim.PositionTrackInterpolate(trackIdx, time);
                    if (!float.IsNaN(pos.X) && !float.IsNaN(pos.Y) && !float.IsNaN(pos.Z))
                    {
                        return pos;
                    }
                }
                if (trType == Godot.Animation.TrackType.Value)
                {
                    Vector3 pos = anim.ValueTrackInterpolate(trackIdx, time).AsVector3();
                    if (!float.IsNaN(pos.X) && !float.IsNaN(pos.Y) && !float.IsNaN(pos.Z))
                    {
                        return pos;
                    }
                }
            }
            catch
            {
            }

            return defaultRest.Origin;
        }

        private static void SimplifyRotationCurveRdp(
            Quaternion[,] frames,
            int boneIdx,
            int startIdx,
            int endIdx,
            float fps,
            double animLength,
            float maxAngleToleranceRad,
            List<int> outKeyIndices)
        {
            if (endIdx <= startIdx + 1) return;

            double startTime = Math.Min(animLength, startIdx * (1.0 / fps));
            double endTime = Math.Min(animLength, endIdx * (1.0 / fps));
            double duration = endTime - startTime;
            if (duration <= 0.0001) return;

            Quaternion qStart = frames[boneIdx, startIdx];
            Quaternion qEnd = frames[boneIdx, endIdx];

            float maxError = 0f;
            int maxErrorIdx = -1;

            for (int i = startIdx + 1; i < endIdx; i++)
            {
                double currTime = Math.Min(animLength, i * (1.0 / fps));
                float alpha = (float)((currTime - startTime) / duration);
                Quaternion expected = qStart.Slerp(qEnd, alpha).Normalized();

                float dot = Math.Abs(frames[boneIdx, i].Dot(expected));
                float angleDiff = 2f * Mathf.Acos(Math.Clamp(dot, -1f, 1f));

                if (angleDiff > maxError)
                {
                    maxError = angleDiff;
                    maxErrorIdx = i;
                }
            }

            if (maxError > maxAngleToleranceRad && maxErrorIdx != -1)
            {
                SimplifyRotationCurveRdp(frames, boneIdx, startIdx, maxErrorIdx, fps, animLength, maxAngleToleranceRad, outKeyIndices);
                outKeyIndices.Add(maxErrorIdx);
                SimplifyRotationCurveRdp(frames, boneIdx, maxErrorIdx, endIdx, fps, animLength, maxAngleToleranceRad, outKeyIndices);
            }
        }

        private static void SimplifyPositionCurveRdp(
            Vector3[] frames,
            int startIdx,
            int endIdx,
            float fps,
            double animLength,
            float maxDistTolerance,
            List<int> outKeyIndices)
        {
            if (endIdx <= startIdx + 1) return;

            double startTime = Math.Min(animLength, startIdx * (1.0 / fps));
            double endTime = Math.Min(animLength, endIdx * (1.0 / fps));
            double duration = endTime - startTime;
            if (duration <= 0.0001) return;

            Vector3 pStart = frames[startIdx];
            Vector3 pEnd = frames[endIdx];

            float maxError = 0f;
            int maxErrorIdx = -1;

            for (int i = startIdx + 1; i < endIdx; i++)
            {
                double currTime = Math.Min(animLength, i * (1.0 / fps));
                float alpha = (float)((currTime - startTime) / duration);
                Vector3 expected = pStart.Lerp(pEnd, alpha);

                float dist = frames[i].DistanceTo(expected);
                if (dist > maxError)
                {
                    maxError = dist;
                    maxErrorIdx = i;
                }
            }

            if (maxError > maxDistTolerance && maxErrorIdx != -1)
            {
                SimplifyPositionCurveRdp(frames, startIdx, maxErrorIdx, fps, animLength, maxDistTolerance, outKeyIndices);
                outKeyIndices.Add(maxErrorIdx);
                SimplifyPositionCurveRdp(frames, maxErrorIdx, endIdx, fps, animLength, maxDistTolerance, outKeyIndices);
            }
        }

        /// <summary>
        /// Resolves the exact node track prefix (e.g. "Skeleton3D", "../GeneralSkeleton")
        /// matching how AnimationPlayer evaluates tracks relative to its RootNode.
        /// </summary>
        public static string ResolveSkeletonTrackPrefix(AnimationPlayer animPlayer, Skeleton3D skeleton)
        {
            if (animPlayer != null)
            {
                var libNames = animPlayer.GetAnimationLibraryList();
                foreach (var libName in libNames)
                {
                    var lib = animPlayer.GetAnimationLibrary(libName);
                    if (lib == null) continue;
                    var animList = lib.GetAnimationList();
                    foreach (var aName in animList)
                    {
                        var anim = lib.GetAnimation(aName);
                        if (anim == null || anim.GetTrackCount() == 0) continue;
                        for (int t = 0; t < anim.GetTrackCount(); t++)
                        {
                            string p = anim.TrackGetPath(t).ToString();
                            int cIdx = p.LastIndexOf(':');
                            if (cIdx != -1)
                            {
                                string prefix = p.Substring(0, cIdx);
                                if (!string.IsNullOrEmpty(prefix))
                                {
                                    return prefix;
                                }
                            }
                        }
                    }
                }

                if (skeleton != null)
                {
                    Node rootNode = animPlayer.GetNodeOrNull(animPlayer.RootNode);
                    if (rootNode != null)
                    {
                        string relPath = rootNode.GetPathTo(skeleton).ToString();
                        if (!string.IsNullOrEmpty(relPath))
                            return relPath;
                    }
                    return animPlayer.GetPathTo(skeleton).ToString();
                }
            }

            if (skeleton != null)
            {
                return skeleton.Name.ToString();
            }

            return "GeneralSkeleton";
        }

        public enum SourceRigPoseType
        {
            TPose,
            APose
        }

        /// <summary>
        /// Detects whether the source skeleton rest pose is in T-Pose or A-Pose.
        /// </summary>
        public static SourceRigPoseType DetectSourceRigPoseType(Skeleton3D sourceSkeleton)
        {
            if (sourceSkeleton == null) return SourceRigPoseType.TPose;

            int upperArmIdx = -1;
            int lowerArmIdx = -1;

            for (int i = 0; i < sourceSkeleton.GetBoneCount(); i++)
            {
                string bName = sourceSkeleton.GetBoneName(i);
                if (IsUpperArmLeft(bName) || IsUpperArmRight(bName))
                {
                    upperArmIdx = i;
                    for (int c = 0; c < sourceSkeleton.GetBoneCount(); c++)
                    {
                        if (sourceSkeleton.GetBoneParent(c) == upperArmIdx)
                        {
                            lowerArmIdx = c;
                            break;
                        }
                    }
                    if (lowerArmIdx != -1) break;
                }
            }

            if (upperArmIdx == -1 || lowerArmIdx == -1)
            {
                return SourceRigPoseType.TPose;
            }

            Vector3 upperArmPos = GetModelSpaceRestPos(sourceSkeleton, upperArmIdx);
            Vector3 lowerArmPos = GetModelSpaceRestPos(sourceSkeleton, lowerArmIdx);

            Vector3 armDir = (lowerArmPos - upperArmPos).Normalized();
            float verticalDrop = Math.Abs(armDir.Y);
            float angleDeg = Mathf.RadToDeg(Mathf.Asin(Math.Clamp(verticalDrop, 0f, 1f)));

            return angleDeg >= 18.0f ? SourceRigPoseType.APose : SourceRigPoseType.TPose;
        }

        private static Vector3 GetModelSpaceRestPos(Skeleton3D skeleton, int boneIdx)
        {
            var ancestors = new List<int>();
            int curr = skeleton.GetBoneParent(boneIdx);
            while (curr != -1)
            {
                ancestors.Add(curr);
                curr = skeleton.GetBoneParent(curr);
            }
            ancestors.Reverse();

            Transform3D global = Transform3D.Identity;
            foreach (int a in ancestors)
            {
                global *= skeleton.GetBoneRest(a);
            }
            global *= skeleton.GetBoneRest(boneIdx);
            return global.Origin;
        }

        public static bool IsUpperArmLeft(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string clean = HumanoidBoneMapper.CleanBoneName(name);
            if (clean == "leftarm" || clean == "arm_l") return true;
            return (clean.Contains("arm_upper_l") || clean.Contains("upperarm_l") || clean.Contains("upper_arm_l")) &&
                   !clean.Contains("lower") && !clean.Contains("clavicle") && !clean.Contains("hand");
        }

        public static bool IsUpperArmRight(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string clean = HumanoidBoneMapper.CleanBoneName(name);
            if (clean == "rightarm" || clean == "arm_r") return true;
            return (clean.Contains("arm_upper_r") || clean.Contains("upperarm_r") || clean.Contains("upper_arm_r")) &&
                   !clean.Contains("lower") && !clean.Contains("clavicle") && !clean.Contains("hand");
        }

        private static bool IsSpineBone(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            return lower.Contains("spine_") || lower.Contains("spine") || lower.Contains("chest");
        }

        private static bool IsPelvisOrHips(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            return lower.Contains("pelvis") || lower.Contains("hip") || lower.Contains("hips");
        }

        private static bool IsPelvisOrLegOrFoot(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string lower = name.ToLowerInvariant();
            return lower.Contains("pelvis") || lower.Contains("hip") ||
                   lower.Contains("leg_") || lower.Contains("thigh") || lower.Contains("calf") ||
                   lower.Contains("ankle") || lower.Contains("foot") || lower.Contains("ball") || lower.Contains("toe") ||
                   lower.Contains("sole");
        }

        private static void DistributeForearmTwist(
            Skeleton3D targetSkeleton,
            int tArmLower,
            int tHand,
            int tTwist0,
            int tTwist1,
            Quaternion[] currentGlobalRot)
        {
            if (tArmLower == -1 || tHand == -1 || (tTwist0 == -1 && tTwist1 == -1)) return;

            // Local rotation of hand relative to arm_lower
            Quaternion qHandLocal = SafeNormalize(currentGlobalRot[tArmLower].Inverse() * currentGlobalRot[tHand]);
            Quaternion qHandRestLocal = SafeNormalize(targetSkeleton.GetBoneRest(tHand).Basis.GetRotationQuaternion());
            Quaternion qHandDelta = SafeNormalize(qHandLocal * qHandRestLocal.Inverse());

            // Forearm axis in arm_lower local space (direction towards hand)
            Vector3 boneAxis = targetSkeleton.GetBoneRest(tHand).Origin;
            if (boneAxis.LengthSquared() < 1e-6f) boneAxis = Vector3.Right;
            else boneAxis = boneAxis.Normalized();

            // Swing-twist decomposition around boneAxis
            Vector3 qVec = new Vector3(qHandDelta.X, qHandDelta.Y, qHandDelta.Z);
            Vector3 proj = boneAxis * qVec.Dot(boneAxis);
            Quaternion qTwist = SafeNormalize(new Quaternion(proj.X, proj.Y, proj.Z, qHandDelta.W));
            if (qTwist.W < 0)
            {
                qTwist = new Quaternion(-qTwist.X, -qTwist.Y, -qTwist.Z, -qTwist.W);
            }

            if (tTwist0 != -1 && tTwist1 != -1)
            {
                Quaternion qT0 = SafeNormalize(Quaternion.Identity.Slerp(qTwist, 0.35f));
                Quaternion qT1 = SafeNormalize(Quaternion.Identity.Slerp(qTwist, 0.70f));
                currentGlobalRot[tTwist0] = SafeNormalize(currentGlobalRot[tArmLower] * qT0);
                currentGlobalRot[tTwist1] = SafeNormalize(currentGlobalRot[tArmLower] * qT1);
            }
            else if (tTwist0 != -1)
            {
                Quaternion qT0 = SafeNormalize(Quaternion.Identity.Slerp(qTwist, 0.50f));
                currentGlobalRot[tTwist0] = SafeNormalize(currentGlobalRot[tArmLower] * qT0);
            }
        }

        private static Vector3 SampleSourceBoneModelPos(
            Skeleton3D skeleton,
            Godot.Animation anim,
            int[] rotTracks,
            int[] posTracks,
            Transform3D toModel,
            int boneIdx,
            double time)
        {
            if (skeleton == null || boneIdx < 0 || boneIdx >= skeleton.GetBoneCount()) return Vector3.Zero;

            var chain = new List<int>();
            int curr = boneIdx;
            while (curr != -1)
            {
                chain.Add(curr);
                curr = skeleton.GetBoneParent(curr);
            }
            chain.Reverse();

            Quaternion gRot = Quaternion.Identity;
            Vector3 gPos = Vector3.Zero;

            foreach (int s in chain)
            {
                int p = skeleton.GetBoneParent(s);
                Quaternion lRot = SafeInterpolateRotation(anim, rotTracks[s], time, skeleton.GetBoneRest(s));
                Vector3 lPos = SafeInterpolatePosition(anim, posTracks[s], time, skeleton.GetBoneRest(s));

                if (p == -1)
                {
                    gRot = SafeNormalize(toModel.Basis.GetRotationQuaternion() * lRot);
                    gPos = toModel * lPos;
                }
                else
                {
                    gPos = gPos + gRot * lPos;
                    gRot = SafeNormalize(gRot * lRot);
                }
            }
            return gPos;
        }
    }
}
