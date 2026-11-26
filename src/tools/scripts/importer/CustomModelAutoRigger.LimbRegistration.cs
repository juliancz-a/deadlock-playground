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
            bool keepFingersRigid = false,
            bool autoAlignHandOrientation = true,
            Vector3 manualHandOffsetDeg = default,
            float manualHandScale = 1.0f,
            List<MeshInstance3D> importedMeshes = null,
            Node3D importedScene = null,
            Transform3D alignedTransform = default)
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
            // Parallel-transport the forearm rotation from R1 instead of an independent ShortestArc(uS2, uT2),
            // eliminating spherical-holonomy axial twist jumps ("candy-wrapper" pinch) at the elbow joint!
            Vector3 uS2AfterR1 = (R1 * uS2).Normalized();
            Quaternion R2 = (ShortestArc(uS2AfterR1, uT2) * R1).Normalized();
            float sigma2 = Mathf.Clamp(Lt2 / Ls2, 0.75f, 1.35f);

            float maxArmAngleDeg = Mathf.RadToDeg(Mathf.Max(R1.GetAngle(), R2.GetAngle()));
            if (maxArmAngleDeg > 115f)
            {
                GD.PushWarning($"[CustomModelAutoRigger] Skipping Auto-Snap Arm Registration for '{upperName}': extreme angle mismatch ({maxArmAngleDeg:F1}° > 115°), likely mismatched axis or side.");
                return null;
            }

            bool isLeft = upperName.EndsWith("_L") || upperName.Contains("_l");
            string side = isLeft ? "L" : "R";

            // Measure wrist-to-knuckle span & forward direction across mapped finger_0 joints so narrower/shorter
            // hands (e.g. VRChat / Captain rigs vs tall Deadlock heroes) scale proportionately to match target
            // knuckle spread instead of leaving palm/_meta_ narrow while snapping distal fingers wider.
            float autoPalmScale = 1.0f;
            Vector3 sumSrcPalmDir = Vector3.Zero;
            Vector3 sumTgtPalmDir = Vector3.Zero;
            int palmDirCount = 0;
            {
                string[] palmFingers = { "index", "middle", "ring", "pinky" };
                float sumSrcDist = 0f;
                float sumTgtDist = 0f;
                int knucklePairs = 0;
                foreach (string pf in palmFingers)
                {
                    int tKnuckle = FindBoneAny(targetSkeleton, $"finger_{pf}_0_{side}", $"finger_{pf}_0_{side}".ToLowerInvariant());
                    if (tKnuckle == -1) continue;
                    int sKnuckle = FindSourceBoneForTarget(sourceToTargetMap, tKnuckle);
                    if (sKnuckle == -1) continue;

                    Vector3 srcOff = (skelToHero * sourceSkeleton.GetBoneGlobalRest(sKnuckle).Origin) - Cs;
                    Vector3 tgtOff = targetSkeleton.GetBoneGlobalRest(tKnuckle).Origin - Ct;
                    float lsK = srcOff.Length();
                    float ltK = tgtOff.Length();
                    if (lsK > 0.01f && ltK > 0.01f)
                    {
                        sumSrcDist += lsK;
                        sumTgtDist += ltK;
                        knucklePairs++;
                        sumSrcPalmDir += srcOff / lsK;
                        sumTgtPalmDir += tgtOff / ltK;
                        palmDirCount++;
                    }
                }
                if (!keepFingersRigid && knucklePairs >= 2 && sumSrcDist > 0.01f)
                {
                    autoPalmScale = Mathf.Clamp(sumTgtDist / sumSrcDist, 0.82f, 1.25f);
                }
            }

            float handScale = Mathf.Clamp((manualHandScale > 0.001f ? manualHandScale : 1.0f) * autoPalmScale, 0.5f, 1.8f);

            // Compute palm lateral axes & automatic axial forearm roll around uT2
            Vector3 tgtPalmLat = TryGetTargetPalmLateralAxis(targetSkeleton, side, uT2, out _);
            Quaternion twistAuto = Quaternion.Identity;
            if (autoAlignHandOrientation)
            {
                twistAuto = ComputeAutoPalmRollRotation(
                    sourceSkeleton, targetSkeleton, sourceToTargetMap,
                    srcHand, side, Cs, uS2, uT2, R2, skelToHero, tgtPalmLat,
                    importedMeshes, importedScene, alignedTransform,
                    out float autoRollDeg, out string palmSignalSource);
                if (MathF.Abs(autoRollDeg) > 0.5f)
                {
                    GD.Print($"[CustomModelAutoRigger] Auto-Snap Hand {side} axial roll={autoRollDeg:F1}° (signal={palmSignalSource}).");
                }
            }

            Quaternion autoPalmBend = Quaternion.Identity;
            if (autoAlignHandOrientation && !keepFingersRigid && palmDirCount >= 2
                && sumSrcPalmDir.LengthSquared() > 0.01f && sumTgtPalmDir.LengthSquared() > 0.01f)
            {
                Vector3 rotatedSrcFwd = ((twistAuto * R2) * sumSrcPalmDir.Normalized()).Normalized();
                Vector3 tgtFwd = sumTgtPalmDir.Normalized();
                Quaternion rawBend = ShortestArc(rotatedSrcFwd, tgtFwd);
                float bendAngle = rawBend.GetAngle();
                float maxBendRad = Mathf.DegToRad(30f);
                if (bendAngle > Mathf.DegToRad(0.5f))
                {
                    autoPalmBend = bendAngle > maxBendRad
                        ? Quaternion.Identity.Slerp(rawBend, maxBendRad / bendAngle).Normalized()
                        : rawBend;
                }
            }

            // Orthonormal frame at target hand: axRoll = forearm axis, axPitch = palm knuckle width axis, axYaw = palm normal
            Vector3 axRoll = uT2;
            Vector3 axPitch = tgtPalmLat.LengthSquared() > 0.25f
                ? (tgtPalmLat - axRoll * tgtPalmLat.Dot(axRoll)).Normalized()
                : ComputeFallbackPerpAxis(axRoll);
            if (axPitch.LengthSquared() < 0.25f) axPitch = ComputeFallbackPerpAxis(axRoll);
            Vector3 axYaw = axRoll.Cross(axPitch).Normalized();

            Quaternion qRollManual = Mathf.Abs(manualHandOffsetDeg.Z) > 1e-4f
                ? new Quaternion(axRoll, Mathf.DegToRad(manualHandOffsetDeg.Z))
                : Quaternion.Identity;
            Quaternion qPitchManual = Mathf.Abs(manualHandOffsetDeg.X) > 1e-4f
                ? new Quaternion(axPitch, Mathf.DegToRad(manualHandOffsetDeg.X))
                : Quaternion.Identity;
            Quaternion qYawManual = Mathf.Abs(manualHandOffsetDeg.Y) > 1e-4f
                ? new Quaternion(axYaw, Mathf.DegToRad(manualHandOffsetDeg.Y))
                : Quaternion.Identity;

            Quaternion forearmTwistExtra = (qRollManual * twistAuto).Normalized();
            Quaternion handBend = (qPitchManual * qYawManual * autoPalmBend).Normalized();
            Quaternion handTotalRot = (handBend * forearmTwistExtra * R2).Normalized();

            GD.Print($"[CustomModelAutoRigger] Auto-Snap Arm Registration for '{upperName}': Upper L_src={Ls1:F3}m, L_tgt={Lt1:F3}m, sigma={sigma1:F2}; Forearm L_src={Ls2:F3}m, L_tgt={Lt2:F3}m, sigma={sigma2:F2}, HandOffset=({manualHandOffsetDeg.X:F1},{manualHandOffsetDeg.Y:F1},{manualHandOffsetDeg.Z:F1})°, HandScale={handScale:F2} (autoPalm={autoPalmScale:F2})");

            HandFingersRegistration fingersReg = null;
            if (!keepFingersRigid)
            {
                fingersReg = BuildFingerSegmentRegistration(sourceSkeleton, targetSkeleton, sourceToTargetMap, isLeft, tgtHand, Cs, Ct, handTotalRot, skelToHero, handScale);
            }

            // Collect upper-arm & forearm counter-twist helper bones so painted SkinWeightOverrides
            // (which store post-counter-twist *_TWIST bone weights) evaluate with their exact arm segment on rescale!
            var upperTwists = new HashSet<int>();
            var lowerTwists = new HashSet<int>();
            string sideSuffix = "_" + side.ToLowerInvariant();
            for (int b = 0; b < targetSkeleton.GetBoneCount(); b++)
            {
                string bn = targetSkeleton.GetBoneName(b).ToLowerInvariant();
                if (!bn.Contains("twist")) continue;
                int par = targetSkeleton.GetBoneParent(b);
                bool sameSide = bn.EndsWith(sideSuffix) || bn.Contains(sideSuffix + "_");
                if (par == tgtUpper || (sameSide && (bn.Contains("arm_upper") || bn.Contains("bicep") || bn.Contains("shoulder"))))
                    upperTwists.Add(b);
                else if (par == tgtLower || (sameSide && (bn.Contains("arm_lower") || bn.Contains("forearm") || bn.Contains("wrist"))))
                    lowerTwists.Add(b);
            }

            return new ArmSegmentRegistration
            {
                TargetUpperBone = tgtUpper,
                TargetLowerBone = tgtLower,
                TargetHandBone = tgtHand,
                UpperTwistBones = upperTwists,
                LowerTwistBones = lowerTwists,
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
                RotationLower = R2,
                RotationForearmTwistExtra = forearmTwistExtra,
                RotationHandBend = handBend,
                RotationHandTotal = handTotalRot,
                HandScale = handScale
            };
        }

        private static Vector3 ComputeFallbackPerpAxis(Vector3 unitAxis)
        {
            Vector3 up = Mathf.Abs(unitAxis.Dot(Vector3.Up)) < 0.9f ? Vector3.Up : Vector3.Forward;
            return (up - unitAxis * up.Dot(unitAxis)).Normalized();
        }

        private static Vector3 TryGetTargetPalmLateralAxis(Skeleton3D targetSkeleton, string side, Vector3 uT2, out bool hasFingerSpread)
        {
            hasFingerSpread = false;
            int idxBone = FindBoneAny(targetSkeleton, $"finger_index_0_{side}", $"finger_index_0_{side}".ToLowerInvariant());
            int pkyBone = FindBoneAny(targetSkeleton, $"finger_pinky_0_{side}", $"finger_pinky_0_{side}".ToLowerInvariant(),
                $"finger_ring_0_{side}", $"finger_ring_0_{side}".ToLowerInvariant());
            if (idxBone >= 0 && pkyBone >= 0)
            {
                Vector3 v = targetSkeleton.GetBoneGlobalRest(idxBone).Origin - targetSkeleton.GetBoneGlobalRest(pkyBone).Origin;
                v -= uT2 * v.Dot(uT2);
                if (v.LengthSquared() > 1e-6f)
                {
                    hasFingerSpread = true;
                    return v.Normalized();
                }
            }

            int thumbBone = FindBoneAny(targetSkeleton, $"finger_thumb_0_{side}", $"finger_thumb_0_{side}".ToLowerInvariant());
            int handBone = FindBoneAny(targetSkeleton, $"hand_{side}", $"hand_{side}".ToLowerInvariant());
            if (thumbBone >= 0 && handBone >= 0)
            {
                Vector3 v = targetSkeleton.GetBoneGlobalRest(thumbBone).Origin - targetSkeleton.GetBoneGlobalRest(handBone).Origin;
                v -= uT2 * v.Dot(uT2);
                if (v.LengthSquared() > 1e-6f)
                {
                    hasFingerSpread = true;
                    return v.Normalized();
                }
            }

            int wpnOrHand = FindBoneAny(targetSkeleton, $"weapon_hand_{side}", $"hand_{side}", $"hand_{side}".ToLowerInvariant());
            if (wpnOrHand >= 0)
            {
                Basis b = targetSkeleton.GetBoneGlobalRest(wpnOrHand).Basis;
                Vector3 v = b.X - uT2 * b.X.Dot(uT2);
                if (v.LengthSquared() < 0.04f) v = b.Y - uT2 * b.Y.Dot(uT2);
                if (v.LengthSquared() > 1e-5f) return v.Normalized();
            }

            return ComputeFallbackPerpAxis(uT2);
        }

        public static Quaternion ComputeAutoPalmRollRotation(
            Skeleton3D sourceSkeleton,
            Skeleton3D targetSkeleton,
            Dictionary<int, int> sourceToTargetMap,
            int srcHand,
            string side,
            Vector3 Cs,
            Vector3 uS2,
            Vector3 uT2,
            Quaternion R2,
            Transform3D skelToHero,
            Vector3 tgtPalmLat,
            List<MeshInstance3D> importedMeshes,
            Node3D importedScene,
            Transform3D alignedTransform,
            out float rollDegOut,
            out string signalSource)
        {
            rollDegOut = 0f;
            signalSource = "none";
            if (tgtPalmLat.LengthSquared() < 0.25f) return Quaternion.Identity;

            Vector3 srcPalmLat = Vector3.Zero;
            bool hasSignedDirection = false;

            // 1. Primary signal: mapped finger_index_0 -> finger_pinky_0 / ring / thumb on source rig (unambiguous sign!)
            int tgtIdx = FindBoneAny(targetSkeleton, $"finger_index_0_{side}", $"finger_index_0_{side}".ToLowerInvariant());
            int tgtPky = FindBoneAny(targetSkeleton, $"finger_pinky_0_{side}", $"finger_pinky_0_{side}".ToLowerInvariant(),
                $"finger_ring_0_{side}", $"finger_ring_0_{side}".ToLowerInvariant());
            int srcIdx = tgtIdx >= 0 ? FindSourceBoneForTarget(sourceToTargetMap, tgtIdx) : -1;
            int srcPky = tgtPky >= 0 ? FindSourceBoneForTarget(sourceToTargetMap, tgtPky) : -1;

            if (srcIdx >= 0 && srcPky >= 0 && srcIdx != srcPky)
            {
                Vector3 v = (skelToHero * sourceSkeleton.GetBoneGlobalRest(srcIdx).Origin)
                          - (skelToHero * sourceSkeleton.GetBoneGlobalRest(srcPky).Origin);
                v -= uS2 * v.Dot(uS2);
                if (v.LengthSquared() > 1e-6f)
                {
                    srcPalmLat = v.Normalized();
                    hasSignedDirection = true;
                    signalSource = "fingers";
                }
            }

            // 2. Fallback signal: PCA over distal hand vertices weighted to srcHand (works for Landmark Auto-Rig & fingerless rigs!)
            if (srcPalmLat.LengthSquared() < 0.25f && importedMeshes != null && srcHand >= 0)
            {
                var handOffsets = CollectDistalHandOffsetsHeroSpace(
                    importedMeshes, importedScene, sourceSkeleton, srcHand, alignedTransform, Cs, uS2);
                if (handOffsets.Count >= 6 && TryComputePcaPalmWidthAxis(handOffsets, uS2, out Vector3 pcaWidth))
                {
                    srcPalmLat = pcaWidth;
                    hasSignedDirection = false;
                    signalSource = $"pca({handOffsets.Count}v)";
                }
            }

            if (srcPalmLat.LengthSquared() < 0.25f) return Quaternion.Identity;

            Vector3 v0 = R2 * srcPalmLat;
            v0 -= uT2 * v0.Dot(uT2);
            if (v0.LengthSquared() < 1e-6f) return Quaternion.Identity;
            v0 = v0.Normalized();

            Vector3 v1 = tgtPalmLat - uT2 * tgtPalmLat.Dot(uT2);
            if (v1.LengthSquared() < 1e-6f) return Quaternion.Identity;
            v1 = v1.Normalized();

            // Resolve eigenvector sign ambiguity for PCA so palm aligns within [-90, +90] instead of flipping upside down
            if (!hasSignedDirection && v0.Dot(v1) < 0f)
            {
                v0 = -v0;
            }

            float rollRad = Mathf.Atan2(uT2.Dot(v0.Cross(v1)), v0.Dot(v1));
            float maxRad = Mathf.DegToRad(hasSignedDirection ? 110f : 75f);
            rollRad = Mathf.Clamp(rollRad, -maxRad, maxRad);
            rollDegOut = Mathf.RadToDeg(rollRad);

            if (MathF.Abs(rollRad) < 1e-4f) return Quaternion.Identity;
            return new Quaternion(uT2, rollRad);
        }

        private static List<Vector3> CollectDistalHandOffsetsHeroSpace(
            List<MeshInstance3D> importedMeshes,
            Node3D importedScene,
            Skeleton3D sourceSkeleton,
            int srcHandBone,
            Transform3D alignedTransform,
            Vector3 Cs,
            Vector3 uS2)
        {
            var result = new List<Vector3>();
            if (importedMeshes == null || sourceSkeleton == null || srcHandBone < 0) return result;

            var handTreeBones = new HashSet<int> { srcHandBone };
            int boneCount = sourceSkeleton.GetBoneCount();
            for (int b = 0; b < boneCount; b++)
            {
                int cur = b;
                while (cur >= 0)
                {
                    if (cur == srcHandBone) { handTreeBones.Add(b); break; }
                    cur = sourceSkeleton.GetBoneParent(cur);
                }
            }

            foreach (var mi in importedMeshes)
            {
                if (mi?.Mesh == null) continue;
                Transform3D miToHero = importedScene != null
                    ? alignedTransform * ComputeTransformRelativeToRoot(mi, importedScene)
                    : alignedTransform;

                bool[] bindIsHand = null;
                if (mi.Skin != null)
                {
                    int bc = mi.Skin.GetBindCount();
                    bindIsHand = new bool[bc];
                    for (int k = 0; k < bc; k++)
                    {
                        int skIdx = mi.Skin.GetBindBone(k);
                        if (skIdx < 0)
                        {
                            string bName = mi.Skin.GetBindName(k);
                            if (!string.IsNullOrEmpty(bName)) skIdx = sourceSkeleton.FindBone(bName);
                        }
                        if (skIdx >= 0 && handTreeBones.Contains(skIdx)) bindIsHand[k] = true;
                    }
                }

                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                {
                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    if (verts == null || verts.Length == 0) continue;
                    int[] bones = arrays.Count > (int)Mesh.ArrayType.Bones ? GetInt32Array(arrays[(int)Mesh.ArrayType.Bones]) : null;
                    float[] weights = arrays.Count > (int)Mesh.ArrayType.Weights ? GetFloat32Array(arrays[(int)Mesh.ArrayType.Weights]) : null;
                    if (bones == null || weights == null || bones.Length == 0) continue;

                    int bpv = weights.Length / verts.Length;
                    if (bpv <= 0) continue;

                    Vector3[] heroVerts = null;
                    if (importedScene != null)
                    {
                        EvaluateMeshSurfaceInTargetSpace(mi, importedScene, alignedTransform, sourceSkeleton, verts, null, bones, weights, out heroVerts, out _, useExplicitSkeleton: true);
                    }

                    for (int v = 0; v < verts.Length; v++)
                    {
                        float hw = 0f;
                        int baseOff = v * bpv;
                        for (int k = 0; k < bpv; k++)
                        {
                            float w = weights[baseOff + k];
                            if (w <= 0.001f) continue;
                            int bi = bones[baseOff + k];
                            bool match = bindIsHand != null
                                ? (bi >= 0 && bi < bindIsHand.Length && bindIsHand[bi])
                                : handTreeBones.Contains(bi);
                            if (match) hw += w;
                        }
                        if (hw < 0.35f) continue;

                        Vector3 vHeroPos = heroVerts != null ? heroVerts[v] : (miToHero * verts[v]);
                        Vector3 d = vHeroPos - Cs;
                        float axial = d.Dot(uS2);
                        if (axial >= 0.01f && axial <= 0.22f)
                        {
                            result.Add(d - uS2 * axial);
                        }
                    }
                }
            }
            return result;
        }

        public static bool TryComputePcaPalmWidthAxis(IReadOnlyList<Vector3> radialOffsets, Vector3 uForearm, out Vector3 widthAxisHero)
        {
            widthAxisHero = Vector3.Zero;
            if (radialOffsets == null || radialOffsets.Count < 6) return false;

            Vector3 e1 = ComputeFallbackPerpAxis(uForearm);
            Vector3 e2 = uForearm.Cross(e1).Normalized();

            float mx = 0f, my = 0f;
            int n = radialOffsets.Count;
            for (int i = 0; i < n; i++)
            {
                mx += radialOffsets[i].Dot(e1);
                my += radialOffsets[i].Dot(e2);
            }
            mx /= n;
            my /= n;

            float cxx = 0f, cxy = 0f, cyy = 0f;
            for (int i = 0; i < n; i++)
            {
                float x = radialOffsets[i].Dot(e1) - mx;
                float y = radialOffsets[i].Dot(e2) - my;
                cxx += x * x;
                cxy += x * y;
                cyy += y * y;
            }
            cxx /= n;
            cxy /= n;
            cyy /= n;

            float tr = cxx + cyy;
            float diff = cxx - cyy;
            float disc = MathF.Sqrt(diff * diff + 4f * cxy * cxy);
            float lMax = 0.5f * (tr + disc);
            float lMin = 0.5f * (tr - disc);
            if (lMax <= 1e-6f || lMax < lMin * 1.35f) return false;

            float vx, vy;
            if (MathF.Abs(cxy) > 1e-7f)
            {
                vx = lMax - cyy;
                vy = cxy;
            }
            else if (cxx >= cyy)
            {
                vx = 1f;
                vy = 0f;
            }
            else
            {
                vx = 0f;
                vy = 1f;
            }

            Vector3 res = e1 * vx + e2 * vy;
            if (res.LengthSquared() < 1e-8f) return false;
            widthAxisHero = res.Normalized();
            return true;
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
            Transform3D skelToHero,
            float handScale = 1.0f)
        {
            if (sourceSkeleton == null || targetSkeleton == null || sourceToTargetMap == null) return null;

            string side = isLeft ? "L" : "R";
            var fingersReg = new HandFingersRegistration
            {
                IsLeft = isLeft,
                TargetHandBone = tgtHandBone
            };

            string[] fingerNames = { "thumb", "index", "middle", "ring", "pinky" };

            // Detect whether mapped metacarpal (_meta_) source bones are co-located at mid-palm (e.g. VRChat *Knuckle
            // bones sitting at a shared mid-palm origin) vs spread across carpal roots near the wrist.
            var metaSourcePositions = new List<Vector3>();
            foreach (var fname in fingerNames)
            {
                int tgtMeta = FindBoneAny(targetSkeleton, $"finger_{fname}_meta_{side}", $"finger_{fname}_meta_{side}".ToLowerInvariant());
                if (tgtMeta == -1) continue;
                int srcMeta = FindSourceBoneForTarget(sourceToTargetMap, tgtMeta);
                if (srcMeta == -1) continue;
                Vector3 rawAs = skelToHero * sourceSkeleton.GetBoneGlobalRest(srcMeta).Origin;
                metaSourcePositions.Add(Ct + Rlower * ((rawAs - Cs) * handScale));
            }
            bool metaAreCoLocated = false;
            if (metaSourcePositions.Count >= 2)
            {
                float maxSpread = 0f;
                for (int a = 0; a < metaSourcePositions.Count; a++)
                    for (int b = a + 1; b < metaSourcePositions.Count; b++)
                        maxSpread = MathF.Max(maxSpread, metaSourcePositions[a].DistanceTo(metaSourcePositions[b]));
                metaAreCoLocated = maxSpread < 0.015f;
            }

            // Compute palm-normal reference frames (source in vHand space vs target Deadlock hand) so finger
            // alignment preserves each finger's anatomical curl plane instead of introducing arbitrary axial twist.
            Vector3 srcPalmNormal = Vector3.Zero, tgtPalmNormal = Vector3.Zero;
            {
                int tgtIdx0 = FindBoneAny(targetSkeleton, $"finger_index_0_{side}", $"finger_index_0_{side}".ToLowerInvariant());
                int tgtMid0 = FindBoneAny(targetSkeleton, $"finger_middle_0_{side}", $"finger_middle_0_{side}".ToLowerInvariant());
                int tgtPnk0 = FindBoneAny(targetSkeleton, $"finger_pinky_0_{side}", $"finger_pinky_0_{side}".ToLowerInvariant());
                int srcIdx0 = tgtIdx0 != -1 ? FindSourceBoneForTarget(sourceToTargetMap, tgtIdx0) : -1;
                int srcMid0 = tgtMid0 != -1 ? FindSourceBoneForTarget(sourceToTargetMap, tgtMid0) : -1;
                int srcPnk0 = tgtPnk0 != -1 ? FindSourceBoneForTarget(sourceToTargetMap, tgtPnk0) : -1;
                if (tgtIdx0 != -1 && tgtMid0 != -1 && tgtPnk0 != -1 && srcIdx0 != -1 && srcMid0 != -1 && srcPnk0 != -1)
                {
                    Vector3 tIdx = targetSkeleton.GetBoneGlobalRest(tgtIdx0).Origin;
                    Vector3 tMid = targetSkeleton.GetBoneGlobalRest(tgtMid0).Origin;
                    Vector3 tPnk = targetSkeleton.GetBoneGlobalRest(tgtPnk0).Origin;
                    Vector3 tN = (tMid - Ct).Cross(tIdx - tPnk);
                    if (tN.LengthSquared() > 1e-8f) tgtPalmNormal = tN.Normalized();

                    Vector3 sIdx = Ct + Rlower * (((skelToHero * sourceSkeleton.GetBoneGlobalRest(srcIdx0).Origin) - Cs) * handScale);
                    Vector3 sMid = Ct + Rlower * (((skelToHero * sourceSkeleton.GetBoneGlobalRest(srcMid0).Origin) - Cs) * handScale);
                    Vector3 sPnk = Ct + Rlower * (((skelToHero * sourceSkeleton.GetBoneGlobalRest(srcPnk0).Origin) - Cs) * handScale);
                    Vector3 sN = (sMid - Ct).Cross(sIdx - sPnk);
                    if (sN.LengthSquared() > 1e-8f) srcPalmNormal = sN.Normalized();
                }
            }
            bool hasPalmNormals = srcPalmNormal.LengthSquared() > 0.5f && tgtPalmNormal.LengthSquared() > 0.5f;

            foreach (var fname in fingerNames)
            {
                RegisterFingerMetacarpalSegment(
                    fingersReg, sourceSkeleton, targetSkeleton, sourceToTargetMap,
                    fname, side, Cs, Ct, Rlower, skelToHero, handScale, metaAreCoLocated);

                int tgtMetaForFinger = FindBoneAny(targetSkeleton, $"finger_{fname}_meta_{side}", $"finger_{fname}_meta_{side}".ToLowerInvariant());
                FingerPhalanxSegment metaSeg = null;
                bool hasMetaSeg = tgtMetaForFinger != -1 && fingersReg.PhalanxesByTargetBone.TryGetValue(tgtMetaForFinger, out metaSeg);
                Vector3 metaEndBt = hasMetaSeg ? metaSeg.Bt : Vector3.Zero;

                bool hasPrevEff = false;
                Vector3 prevEffBt = Vector3.Zero;
                Quaternion prevR = Quaternion.Identity;

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

                    Vector3 rawAt = targetSkeleton.GetBoneGlobalRest(tgtBoneA).Origin;
                    Vector3 Bt;

                    // When source is a 2-phalanx finger (i == 1 has no mapped finger_*_2 so srcBoneB == -1 and
                    // falls through to finger_*_end at the fingertip), resolve target Bt to Deadlock's actual
                    // fingertip (finger_*_end / extrapolated past finger_*_2) rather than stopping at DIP finger_*_2,
                    // preventing 50% axial squashing/bunching of the distal finger!
                    bool isTwoJointDistalTip = (i == 1 && srcBoneB == -1 && sourceSkeleton.GetBoneChildren(srcBoneA).Length > 0);
                    if (isTwoJointDistalTip)
                    {
                        int tgtTip = FindBoneAny(targetSkeleton,
                            $"finger_{fname}_end_{side}", $"finger_{fname}_leaf_{side}",
                            $"finger_{fname}_end_{side}".ToLowerInvariant(), $"finger_{fname}_leaf_{side}".ToLowerInvariant());
                        if (tgtTip != -1)
                        {
                            Bt = targetSkeleton.GetBoneGlobalRest(tgtTip).Origin;
                        }
                        else if (tgtBoneB != -1)
                        {
                            Vector3 dipPos = targetSkeleton.GetBoneGlobalRest(tgtBoneB).Origin;
                            Vector3 segDir = dipPos - rawAt;
                            Bt = dipPos + segDir * 0.75f;
                        }
                        else
                        {
                            Bt = rawAt + Vector3.Forward * 0.03f;
                        }
                    }
                    else if (tgtBoneB != -1)
                    {
                        Bt = targetSkeleton.GetBoneGlobalRest(tgtBoneB).Origin;
                    }
                    else if (i > 0)
                    {
                        string tgtPrevName = $"finger_{fname}_{i - 1}_{side}";
                        int tgtPrev = FindBoneAny(targetSkeleton, tgtPrevName, tgtPrevName.ToLowerInvariant());
                        Vector3 prevPos = tgtPrev != -1 ? targetSkeleton.GetBoneGlobalRest(tgtPrev).Origin : rawAt - Vector3.Forward * 0.02f;
                        Vector3 dir = (rawAt - prevPos);
                        Bt = rawAt + (dir.Length() > 0.001f ? dir.Normalized() * (dir.Length() * 0.8f) : Vector3.Forward * 0.02f);
                    }
                    else
                    {
                        Bt = rawAt + Vector3.Forward * 0.025f;
                    }

                    // C0-continuous chain anchor: phalanx 0 starts at the metacarpal's exact Bt (or Deadlock's knuckle pivot
                    // when no metacarpal exists), and each distal phalanx starts at prevEffBt so adjacent segments NEVER
                    // pull shared joint vertices to two different 3D points!
                    Vector3 At = (i > 0 && hasPrevEff) ? prevEffBt : (i == 0 && hasMetaSeg ? metaEndBt : rawAt);

                    Vector3 rawAs = skelToHero * sourceSkeleton.GetBoneGlobalRest(srcBoneA).Origin;
                    Vector3 As = Ct + Rlower * ((rawAs - Cs) * handScale);
                    Vector3 Bs;

                    if (srcBoneB != -1)
                    {
                        Vector3 rawBs = skelToHero * sourceSkeleton.GetBoneGlobalRest(srcBoneB).Origin;
                        Bs = Ct + Rlower * ((rawBs - Cs) * handScale);
                    }
                    else if (sourceSkeleton.GetBoneChildren(srcBoneA).Length > 0)
                    {
                        int childSrc = sourceSkeleton.GetBoneChildren(srcBoneA)[0];
                        Vector3 rawBs = skelToHero * sourceSkeleton.GetBoneGlobalRest(childSrc).Origin;
                        Bs = Ct + Rlower * ((rawBs - Cs) * handScale);
                    }
                    else if (i > 0)
                    {
                        string tgtPrevName = $"finger_{fname}_{i - 1}_{side}";
                        int tgtPrev = FindBoneAny(targetSkeleton, tgtPrevName, tgtPrevName.ToLowerInvariant());
                        int srcPrev = tgtPrev != -1 ? FindSourceBoneForTarget(sourceToTargetMap, tgtPrev) : -1;
                        Vector3 rawPrev = srcPrev != -1 ? (skelToHero * sourceSkeleton.GetBoneGlobalRest(srcPrev).Origin) : rawAs - Vector3.Forward * 0.02f;
                        Vector3 prevAs = Ct + Rlower * ((rawPrev - Cs) * handScale);
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

                    // Align phalanx 0 axis + palm-normal curl plane (for the 4 main fingers), then parallel-transport
                    // along 0 -> 1 -> 2 so fingers bend cleanly in Deadlock's curl plane without axial twist!
                    Quaternion R;
                    if (i == 0 || !hasPrevEff)
                    {
                        Quaternion qAxis = ShortestArc(uS, uT);
                        R = qAxis;
                        if (hasPalmNormals && fname != "thumb")
                        {
                            Vector3 pSrc = qAxis * srcPalmNormal;
                            pSrc -= uT * pSrc.Dot(uT);
                            Vector3 pTgt = tgtPalmNormal - uT * tgtPalmNormal.Dot(uT);
                            if (pSrc.LengthSquared() > 1e-4f && pTgt.LengthSquared() > 1e-4f)
                            {
                                Quaternion qTwist = ShortestArc(pSrc.Normalized(), pTgt.Normalized());
                                float twAngle = qTwist.GetAngle();
                                float maxTwist = Mathf.DegToRad(30f);
                                if (twAngle > maxTwist)
                                    qTwist = Quaternion.Identity.Slerp(qTwist, maxTwist / twAngle).Normalized();
                                R = (qTwist * qAxis).Normalized();
                            }
                        }
                    }
                    else
                    {
                        R = (ShortestArc((prevR * uS).Normalized(), uT) * prevR).Normalized();
                    }

                    // Keep phalanx axial stretch tightly bounded [0.75, 1.30] so fingers never bunch up or over-stretch
                    // when source finger proportions differ from the target Deadlock hero.
                    float sigma = Mathf.Clamp(Lt / Ls, 0.75f, 1.30f);
                    Vector3 effBt = At + uT * (Ls * sigma);

                    prevEffBt = effBt;
                    prevR = R;
                    hasPrevEff = true;

                    fingersReg.PhalanxesByTargetBone[tgtBoneA] = new FingerPhalanxSegment
                    {
                        TargetBone = tgtBoneA,
                        Name = tgtBoneAName,
                        As = As,
                        Bs = Bs,
                        At = At,
                        Bt = effBt,
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

        private static void RegisterFingerMetacarpalSegment(
            HandFingersRegistration fingersReg,
            Skeleton3D sourceSkeleton,
            Skeleton3D targetSkeleton,
            Dictionary<int, int> sourceToTargetMap,
            string fname,
            string side,
            Vector3 Cs,
            Vector3 Ct,
            Quaternion Rlower,
            Transform3D skelToHero,
            float handScale,
            bool metaAreCoLocated)
        {
            string tgtMetaName = $"finger_{fname}_meta_{side}";
            int tgtMeta = FindBoneAny(targetSkeleton, tgtMetaName, tgtMetaName.ToLowerInvariant());
            string tgt0Name = $"finger_{fname}_0_{side}";
            int tgt0 = FindBoneAny(targetSkeleton, tgt0Name, tgt0Name.ToLowerInvariant());
            if (tgtMeta == -1 || tgt0 == -1) return;

            int srcMeta = FindSourceBoneForTarget(sourceToTargetMap, tgtMeta);
            int src0 = FindSourceBoneForTarget(sourceToTargetMap, tgt0);
            if (srcMeta == -1 || src0 == -1 || srcMeta == src0) return;

            Vector3 rawAs = skelToHero * sourceSkeleton.GetBoneGlobalRest(srcMeta).Origin;
            Vector3 As = Ct + Rlower * ((rawAs - Cs) * handScale);
            Vector3 rawBs = skelToHero * sourceSkeleton.GetBoneGlobalRest(src0).Origin;
            Vector3 Bs = Ct + Rlower * ((rawBs - Cs) * handScale);

            float Ls = (Bs - As).Length();
            if (Ls < 0.001f) return;

            // Watertight C0 bridge: anchor the metacarpal start at exact As (in vHand space) so Eval_meta(As) == Eval_hand(As)
            // with 0.000mm palm seam, and end at Deadlock's exact knuckle Bt so Eval_meta(Bs) == Eval_finger0(As_0) with 0.000mm knuckle seam!
            Vector3 At = As;
            Vector3 Bt = targetSkeleton.GetBoneGlobalRest(tgt0).Origin;

            float Lt = (Bt - At).Length();
            if (Lt < 0.001f) return;

            Vector3 uS = (Bs - As) / Ls;
            Vector3 uT = (Bt - At) / Lt;
            Quaternion R = ShortestArc(uS, uT);
            float metaAngle = R.GetAngle();
            float maxMetaAngle = Mathf.DegToRad(25f);
            if (metaAngle > maxMetaAngle)
                R = Quaternion.Identity.Slerp(R, maxMetaAngle / metaAngle).Normalized();
            float sigma = Mathf.Clamp(Lt / Ls, 0.75f, 1.30f);
            Vector3 effUnitT = (R * uS).Normalized();

            fingersReg.PhalanxesByTargetBone[tgtMeta] = new FingerPhalanxSegment
            {
                TargetBone = tgtMeta,
                Name = tgtMetaName,
                As = As,
                Bs = Bs,
                At = At,
                Bt = At + effUnitT * (Ls * sigma),
                UnitSource = uS,
                UnitTarget = effUnitT,
                LengthSource = Ls,
                LengthTarget = Lt,
                Sigma = sigma,
                Rotation = R
            };
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
            // Parallel-transport the forearm rotation from R1 instead of an independent ShortestArc(uS2, uT2),
            // eliminating spherical-holonomy axial twist jumps ("candy-wrapper" pinch) at the elbow joint!
            Vector3 uS2AfterR1 = (R1 * uS2).Normalized();
            Quaternion R2 = (ShortestArc(uS2AfterR1, uT2) * R1).Normalized();
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

            var thighTwists = new HashSet<int>();
            var calfTwists = new HashSet<int>();
            string legSideSfx = thighName.EndsWith("_L", StringComparison.OrdinalIgnoreCase) ? "_l" : "_r";
            for (int b = 0; b < targetSkeleton.GetBoneCount(); b++)
            {
                string bn = targetSkeleton.GetBoneName(b).ToLowerInvariant();
                if (!bn.Contains("twist")) continue;
                int par = targetSkeleton.GetBoneParent(b);
                bool sameSide = bn.EndsWith(legSideSfx) || bn.Contains(legSideSfx + "_");
                if (par == tgtThigh || (sameSide && (bn.Contains("leg_upper") || bn.Contains("thigh"))))
                    thighTwists.Add(b);
                else if (par == tgtCalf || (sameSide && (bn.Contains("leg_lower") || bn.Contains("calf") || bn.Contains("shin"))))
                    calfTwists.Add(b);
            }

            return new LegSegmentRegistration
            {
                TargetThighBone = tgtThigh,
                TargetCalfBone = tgtCalf,
                TargetAnkleBone = tgtAnkle,
                ThighTwistBones = thighTwists,
                CalfTwistBones = calfTwists,
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

        #endregion
    }
}
