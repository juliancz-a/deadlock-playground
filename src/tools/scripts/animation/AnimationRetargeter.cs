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
        public class RetargetOptions
        {
            /// <summary>Optional manual arm pitch tweak in degrees (-60° to +60°). Defaults to 0° (0° = 100% dynamic vector alignment from bone geometry).</summary>
            public float ArmPitchOffsetDeg { get; set; } = 0f;

            /// <summary>Optional manual arm clearance tweak in degrees (-20° to +20°). Defaults to 0° (0° = 100% dynamic mesh volumetric clearance).</summary>
            public float ArmClearanceOffsetDeg { get; set; } = 0f;

            /// <summary>Optional manual spine tilt tweak in degrees (-30° to +30°). Defaults to 0° (0° = 100% dynamic arc-length redistribution).</summary>
            public float SpinePitchOffsetDeg { get; set; } = 0f;

            /// <summary>Pelvis vertical displacement height scale (0.5x to 1.5x)</summary>
            public float PelvisHeightScale { get; set; } = 1.0f;

            /// <summary>Zeros out horizontal translation deltas (X, Z), keeping in-place root motion</summary>
            public bool LockHorizontalRootMotion { get; set; } = true;

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

            // 1. Compute Model-Space Rest Transforms for Source Skeleton
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
                Quaternion localRot = SafeNormalize(localRest.Basis.GetRotationQuaternion());
                Vector3 localPos = localRest.Origin;

                if (p == -1)
                {
                    sourceRestGlobalRot[s] = localRot;
                    sourceRestGlobalPos[s] = localPos;
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
                if (boneMapping != null && boneMapping.TryGetValue(targetName, out string sourceName) && !string.IsNullOrEmpty(sourceName))
                {
                    int s = sourceSkeleton.FindBone(sourceName);
                    if (s == -1)
                    {
                        string cleanS = HumanoidBoneMapper.CleanBoneName(sourceName);
                        for (int i = 0; i < sourceBoneCount; i++)
                        {
                            if (HumanoidBoneMapper.CleanBoneName(sourceSkeleton.GetBoneName(i)) == cleanS)
                            {
                                s = i;
                                break;
                            }
                        }
                    }

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
                int pIdx = targetSkeleton.FindBone("pelvis");
                if (pIdx != -1)
                {
                    mappedPelvisTargetIndex = pIdx;
                    for (int s = 0; s < sourceBoneCount; s++)
                    {
                        if (IsPelvisOrHips(sourceSkeleton.GetBoneName(s)))
                        {
                            mappedPelvisSourceIndex = s;
                            break;
                        }
                    }
                }
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

            void AlignLimbSegment(string targetParentName, string targetChildName, Quaternion userTweak)
            {
                int tP = targetSkeleton.FindBone(targetParentName);
                int tC = targetSkeleton.FindBone(targetChildName);
                if (tP == -1 || tC == -1) return;

                int sP = targetToSourceIndex[tP];
                int sC = targetToSourceIndex[tC];
                if (sP == -1 || sC == -1) return;

                Vector3 uS = (sourceRestGlobalPos[sC] - sourceRestGlobalPos[sP]).Normalized();
                Vector3 uT = (targetAlignedRestGlobalPos[tC] - targetAlignedRestGlobalPos[tP]).Normalized();

                Quaternion qAlign = ShortestArc(uT, uS);
                Quaternion qFinal = SafeNormalize(userTweak * qAlign);

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

            // Align limb chains hierarchically
            // Left Arm
            AlignLimbSegment("arm_upper_L", "arm_lower_L", leftArmTweak);
            AlignLimbSegment("arm_lower_L", "hand_L", Quaternion.Identity);

            // Right Arm
            AlignLimbSegment("arm_upper_R", "arm_lower_R", rightArmTweak);
            AlignLimbSegment("arm_lower_R", "hand_R", Quaternion.Identity);

            // Left Leg
            AlignLimbSegment("leg_upper_L", "leg_lower_L", Quaternion.Identity);
            AlignLimbSegment("leg_lower_L", "ankle_L", Quaternion.Identity);
            AlignLimbSegment("ankle_L", "ball_L", Quaternion.Identity);

            // Right Leg
            AlignLimbSegment("leg_upper_R", "leg_lower_R", Quaternion.Identity);
            AlignLimbSegment("leg_lower_R", "ankle_R", Quaternion.Identity);
            AlignLimbSegment("ankle_R", "ball_R", Quaternion.Identity);

            // Head
            AlignLimbSegment("neck_0", "head", Quaternion.Identity);

            // 7. Anatomical Ratio-Based Clavicle Scaling
            float clavicleWeightL = 1.0f;
            float clavicleWeightR = 1.0f;

            float ComputeClavicleWeight(string clavName, string upperName, string lowerName, string handName)
            {
                int tClav = targetSkeleton.FindBone(clavName);
                int tUpper = targetSkeleton.FindBone(upperName);
                int tLower = targetSkeleton.FindBone(lowerName);
                int tHand = targetSkeleton.FindBone(handName);

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

            // 8. Spine Chain Redistribution Parameterization
            var targetSpineIndices = new List<int>();
            for (int i = 0; i < 10; i++)
            {
                int idx = targetSkeleton.FindBone($"spine_{i}");
                if (idx != -1) targetSpineIndices.Add(idx);
            }
            if (targetSpineIndices.Count == 0)
            {
                for (int i = 0; i < targetBoneCount; i++)
                {
                    if (IsSpineBone(targetSkeleton.GetBoneName(i))) targetSpineIndices.Add(i);
                }
            }
            targetSpineIndices.Sort((a, b) => Array.IndexOf(targetOrder, a).CompareTo(Array.IndexOf(targetOrder, b)));

            int targetSpineCount = targetSpineIndices.Count;
            var targetSpineStartU = new float[targetSpineCount];
            var targetSpineEndU = new float[targetSpineCount];

            if (targetSpineCount > 0)
            {
                var targetLengths = new float[targetSpineCount];
                float totalTLen = 0f;
                int neckIdx = targetSkeleton.FindBone("neck_0");

                for (int j = 0; j < targetSpineCount; j++)
                {
                    int currBone = targetSpineIndices[j];
                    int nextBone = j < targetSpineCount - 1 ? targetSpineIndices[j + 1] : neckIdx;
                    float segLen = 0.12f;
                    if (nextBone != -1)
                    {
                        segLen = (targetRestGlobalPos[nextBone] - targetRestGlobalPos[currBone]).Length();
                    }
                    segLen = Math.Max(0.001f, segLen);
                    targetLengths[j] = segLen;
                    totalTLen += segLen;
                }

                float accumU = 0f;
                for (int j = 0; j < targetSpineCount; j++)
                {
                    targetSpineStartU[j] = accumU;
                    accumU += targetLengths[j] / totalTLen;
                    targetSpineEndU[j] = j == targetSpineCount - 1 ? 1.0f : accumU;
                }
            }

            var sourceSpineIndices = new List<int>();
            foreach (int t in targetSpineIndices)
            {
                int s = targetToSourceIndex[t];
                if (s != -1 && !sourceSpineIndices.Contains(s))
                {
                    sourceSpineIndices.Add(s);
                }
            }
            if (sourceSpineIndices.Count == 0)
            {
                for (int s = 0; s < sourceBoneCount; s++)
                {
                    if (IsSpineBone(sourceSkeleton.GetBoneName(s))) sourceSpineIndices.Add(s);
                }
            }
            sourceSpineIndices.Sort((a, b) => Array.IndexOf(sourceOrder, a).CompareTo(Array.IndexOf(sourceOrder, b)));

            int sourceSpineCount = sourceSpineIndices.Count;
            var sourceSpineEndU = new float[sourceSpineCount];
            if (sourceSpineCount > 0)
            {
                var sLengths = new float[sourceSpineCount];
                float totalSLen = 0f;
                int sNeck = FindBoneByNames(sourceSkeleton, "neck_0", "neck", "neck1", "head");

                for (int k = 0; k < sourceSpineCount; k++)
                {
                    int currBone = sourceSpineIndices[k];
                    int nextBone = k < sourceSpineCount - 1 ? sourceSpineIndices[k + 1] : sNeck;
                    float segLen = 0.12f;
                    if (nextBone != -1)
                    {
                        segLen = (sourceRestGlobalPos[nextBone] - sourceRestGlobalPos[currBone]).Length();
                    }
                    segLen = Math.Max(0.001f, segLen);
                    sLengths[k] = segLen;
                    totalSLen += segLen;
                }

                float accumSU = 0f;
                for (int k = 0; k < sourceSpineCount; k++)
                {
                    accumSU += sLengths[k] / totalSLen;
                    sourceSpineEndU[k] = k == sourceSpineCount - 1 ? 1.0f : accumSU;
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

            // 10. Index Source Animation Tracks
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

                int s = sourceSkeleton.FindBone(boneName);
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

            float pelvisScaleFactor = 1.0f;
            if (mappedPelvisSourceIndex != -1 && mappedPelvisTargetIndex != -1)
            {
                float srcH = Math.Abs(sourceRestGlobalPos[mappedPelvisSourceIndex].Y);
                float tgtH = Math.Abs(targetRestGlobalPos[mappedPelvisTargetIndex].Y);
                if (srcH > 0.05f && tgtH > 0.05f)
                {
                    pelvisScaleFactor = (tgtH / srcH) * options.PelvisHeightScale;
                }
                else
                {
                    pelvisScaleFactor = options.PelvisHeightScale;
                }
            }

            int tClavL = targetSkeleton.FindBone("clavicle_L");
            int tClavR = targetSkeleton.FindBone("clavicle_R");
            int sClavL = tClavL != -1 ? targetToSourceIndex[tClavL] : -1;
            int sClavR = tClavR != -1 ? targetToSourceIndex[tClavR] : -1;

            int tArmUpperL = targetSkeleton.FindBone("arm_upper_L");
            int tArmLowerL = targetSkeleton.FindBone("arm_lower_L");
            int tHandL = targetSkeleton.FindBone("hand_L");

            int tArmUpperR = targetSkeleton.FindBone("arm_upper_R");
            int tArmLowerR = targetSkeleton.FindBone("arm_lower_R");
            int tHandR = targetSkeleton.FindBone("hand_R");

            float spinePitchRad = Mathf.DegToRad(options.SpinePitchOffsetDeg);
            Quaternion spineTweak = SafeNormalize(new Quaternion(Vector3.Right, spinePitchRad));

            var sourceCumRotKnots = new Quaternion[sourceSpineCount + 1];

            for (int f = 0; f < frameCount; f++)
            {
                double time = Math.Min(animLength, f * (1.0 / fps));

                // A. Forward Kinematics on Source Skeleton
                foreach (int s in sourceOrder)
                {
                    int p = sourceSkeleton.GetBoneParent(s);
                    Quaternion localRot = SafeInterpolateRotation(sourceAnim, sourceRotTracks[s], time, sourceSkeleton.GetBoneRest(s));
                    Vector3 localPos = SafeInterpolatePosition(sourceAnim, sourcePosTracks[s], time, sourceSkeleton.GetBoneRest(s));

                    currentSourceLocalRot[s] = localRot;

                    if (p == -1)
                    {
                        currentSourceGlobalRot[s] = localRot;
                        currentSourceGlobalPos[s] = localPos;
                    }
                    else
                    {
                        currentSourceGlobalRot[s] = SafeNormalize(currentSourceGlobalRot[p] * localRot);
                        currentSourceGlobalPos[s] = currentSourceGlobalPos[p] + currentSourceGlobalRot[p] * localPos;
                    }
                }

                // B. Compute Cumulative Source Spine Rotations for Arc-Length Sampling
                if (sourceSpineCount > 0)
                {
                    sourceCumRotKnots[0] = Quaternion.Identity;
                    for (int k = 0; k < sourceSpineCount; k++)
                    {
                        int sBone = sourceSpineIndices[k];
                        Quaternion sRestLocal = SafeNormalize(sourceSkeleton.GetBoneRest(sBone).Basis.GetRotationQuaternion());
                        Quaternion sLocalDelta = SafeNormalize(sRestLocal.Inverse() * currentSourceLocalRot[sBone]);
                        sourceCumRotKnots[k + 1] = SafeNormalize(sourceCumRotKnots[k] * sLocalDelta);
                    }
                }

                Quaternion SampleCumulativeSourceRotation(float u)
                {
                    if (sourceSpineCount == 0 || u <= 0.0001f) return Quaternion.Identity;
                    if (u >= 0.9999f) return sourceCumRotKnots[sourceSpineCount];

                    float prevU = 0f;
                    for (int k = 0; k < sourceSpineCount; k++)
                    {
                        float currU = sourceSpineEndU[k];
                        if (u >= prevU && u <= currU)
                        {
                            float range = Math.Max(1e-4f, currU - prevU);
                            float alpha = Mathf.Clamp((u - prevU) / range, 0f, 1f);
                            return SafeNormalize(sourceCumRotKnots[k].Slerp(sourceCumRotKnots[k + 1], alpha));
                        }
                        prevU = currU;
                    }
                    return sourceCumRotKnots[sourceSpineCount];
                }

                // C. Target Skeleton Evaluation in Topological Order
                foreach (int t in targetOrder)
                {
                    int p = targetSkeleton.GetBoneParent(t);
                    int s = targetToSourceIndex[t];

                    if (targetSpineIndices.Contains(t) && targetSpineCount > 0)
                    {
                        // Spine bone redistribution along normalized arc length
                        int spineIdx = targetSpineIndices.IndexOf(t);
                        float uStart = targetSpineStartU[spineIdx];
                        float uEnd = targetSpineEndU[spineIdx];

                        Quaternion cStart = SampleCumulativeSourceRotation(uStart);
                        Quaternion cEnd = SampleCumulativeSourceRotation(uEnd);
                        Quaternion deltaL = SafeNormalize(cStart.Inverse() * cEnd);

                        Quaternion localRest = SafeNormalize(targetSkeleton.GetBoneRest(t).Basis.GetRotationQuaternion());
                        Quaternion localRot = SafeNormalize(localRest * deltaL);

                        if (spineIdx == 0 && Math.Abs(options.SpinePitchOffsetDeg) > 0.01f)
                        {
                            localRot = SafeNormalize(spineTweak * localRot);
                        }

                        currentTargetGlobalRot[t] = p == -1
                            ? localRot
                            : SafeNormalize(currentTargetGlobalRot[p] * localRot);
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
                        // Mapped limb / head / finger bone
                        currentTargetGlobalRot[t] = SafeNormalize(currentSourceGlobalRot[s] * qOffset[t]);
                    }
                    else
                    {
                        // Unmapped bone: preserve rest relative to parent
                        Quaternion localRestRot = SafeNormalize(targetSkeleton.GetBoneRest(t).Basis.GetRotationQuaternion());
                        currentTargetGlobalRot[t] = p == -1
                            ? localRestRot
                            : SafeNormalize(currentTargetGlobalRot[p] * localRestRot);
                    }

                    // Forward position computation
                    if (t == mappedPelvisTargetIndex && mappedPelvisSourceIndex != -1)
                    {
                        Vector3 srcDisp = currentSourceGlobalPos[mappedPelvisSourceIndex] - sourceRestGlobalPos[mappedPelvisSourceIndex];
                        srcDisp *= pelvisScaleFactor;

                        if (options.LockHorizontalRootMotion)
                        {
                            srcDisp.X = 0f;
                            srcDisp.Z = 0f;
                        }

                        currentTargetGlobalPos[t] = targetAlignedRestGlobalPos[t] + srcDisp;
                    }
                    else
                    {
                        Vector3 localRestPos = targetSkeleton.GetBoneRest(t).Origin;
                        currentTargetGlobalPos[t] = p == -1
                            ? localRestPos
                            : currentTargetGlobalPos[p] + currentTargetGlobalRot[p] * localRestPos;
                    }
                }

                // D. Per-Frame Volumetric Torso & Limb Anti-Clipping Solver
                SolveArmTorsoClearance(tArmUpperL, tArmLowerL, tHandL, torsoProfile, targetSkeleton, currentTargetGlobalRot, currentTargetGlobalPos);
                SolveArmTorsoClearance(tArmUpperR, tArmLowerR, tHandR, torsoProfile, targetSkeleton, currentTargetGlobalRot, currentTargetGlobalPos);

                // E. Store Final Parent-Local Rotations and Pelvis Positions
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

            // 13. Track Generation & RDP Curve Simplification
            int totalInsertedKeys = 0;
            var rdpKeyIndices = new List<int>(frameCount);
            const float RotToleranceRad = 0.0087f; // ~0.5° tolerance preserves exact 3D pose
            const float PosToleranceMeters = 0.0015f; // 1.5 mm tolerance

            for (int t = 0; t < targetBoneCount; t++)
            {
                if (targetToSourceIndex[t] == -1 && !targetSpineIndices.Contains(t)) continue;

                string bName = targetSkeleton.GetBoneName(t);
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

                if (isStatic && Math.Abs(bakedRotations[t, 0].Dot(restRot)) >= 0.9999f)
                {
                    continue;
                }

                int rotTr = retargetedAnim.AddTrack(Godot.Animation.TrackType.Rotation3D);
                retargetedAnim.TrackSetPath(rotTr, $"{skeletonPrefix}:{bName}");

                if (isStatic)
                {
                    retargetedAnim.RotationTrackInsertKey(rotTr, 0.0, bakedRotations[t, 0]);
                    retargetedAnim.RotationTrackInsertKey(rotTr, animLength, bakedRotations[t, 0]);
                    totalInsertedKeys += 2;
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

                rdpKeyIndices.Clear();
                rdpKeyIndices.Add(0);
                SimplifyPositionCurveRdp(bakedPelvisPos, 0, frameCount - 1, fps, animLength, PosToleranceMeters, rdpKeyIndices);
                if (frameCount > 1)
                {
                    rdpKeyIndices.Add(frameCount - 1);
                }

                foreach (int f in rdpKeyIndices)
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
    }
}
