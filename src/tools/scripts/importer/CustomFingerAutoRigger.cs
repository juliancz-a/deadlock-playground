using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace DeadlockPlayground.Importer
{
    /// <summary>
    /// ARP-style Finger Auto-Rigger (Phase 4):
    /// - Auto-detects 5 fingertips + knuckle/PIP/DIP landmarks on Left &amp; Right hand meshes.
    /// - Generates synthetic 3-phalanx finger chains (+ optional metacarpals &amp; wrist twist) when requested.
    /// - Computes per-finger capsule/geodesic weights so weights do not bleed across adjacent fingers.
    /// - Provides instant finger pose presets (Rest, Fist, Open, Pinch, Point) for live preview verification.
    /// </summary>
    public static class CustomFingerAutoRigger
    {
        public static readonly string[] FingerNames = { "thumb", "index", "middle", "ring", "pinky" };
        public static readonly string[] JointTokens = { "0", "1", "2", "end" };

        public static IEnumerable<string> EnumerateLandmarkKeys(bool includeRight = true)
        {
            foreach (string side in includeRight ? new[] { "L", "R" } : new[] { "L" })
            {
                foreach (string f in FingerNames)
                {
                    foreach (string j in JointTokens)
                        yield return $"finger_{f}_{j}_{side}";
                }
            }
        }

        public static string MirrorLandmarkKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (key.EndsWith("_L", StringComparison.Ordinal)) return key[..^2] + "_R";
            if (key.EndsWith("_R", StringComparison.Ordinal)) return key[..^2] + "_L";
            return null;
        }

        /// <summary>
        /// Auto-estimates 20 finger landmarks per hand (5 fingers x [knuckle 0, PIP 1, DIP 2, tip end])
        /// in imported-scene space using the hand's wrist/elbow landmarks and distal hand geometry.
        /// </summary>
        public static Dictionary<string, Vector3> AutoDetectFingerLandmarks(
            Node3D importedScene,
            IReadOnlyList<MeshInstance3D> meshes,
            Dictionary<string, Vector3> bodyPins)
        {
            var result = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
            if (importedScene == null || meshes == null || bodyPins == null) return result;

            // Gather scene-space vertices once
            var allVerts = new List<Vector3>(8192);
            foreach (var mi in meshes)
            {
                if (mi?.Mesh == null) continue;
                Transform3D xf = CustomModelAutoRigger.ComputeTransformRelativeToRoot(mi, importedScene);
                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                {
                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.Vertex) continue;
                    var vVar = arrays[(int)Mesh.ArrayType.Vertex];
                    if (vVar.VariantType == Variant.Type.Nil) continue;
                    var verts = vVar.AsVector3Array();
                    if (verts == null) continue;
                    foreach (var v in verts) allVerts.Add(xf * v);
                }
            }

            foreach (bool isLeft in new[] { true, false })
            {
                string sfx = isLeft ? "L" : "R";
                if (!bodyPins.TryGetValue($"Wrist{sfx}", out Vector3 wrist) ||
                    !bodyPins.TryGetValue($"Elbow{sfx}", out Vector3 elbow))
                    continue;

                Vector3 armDir = wrist - elbow;
                float armLen = armDir.Length();
                if (armLen < 0.02f) continue;
                armDir /= armLen;

                // Estimate hand length from arm length (~0.62 * forearm)
                float handSpan = MathF.Max(0.08f, armLen * 0.68f);

                // Collect distal hand vertices beyond wrist along armDir
                var handVerts = new List<Vector3>();
                foreach (var v in allVerts)
                {
                    Vector3 rel = v - wrist;
                    float proj = rel.Dot(armDir);
                    if (proj >= -0.01f && proj <= handSpan * 1.35f)
                    {
                        float perp = (rel - armDir * proj).Length();
                        if (perp <= handSpan * 0.85f)
                            handVerts.Add(v);
                    }
                }

                // Determine outward/lateral basis of the palm
                Vector3 upHint = Vector3.Up;
                Vector3 palmNormal = armDir.Cross(upHint);
                if (palmNormal.LengthSquared() < 1e-4f) palmNormal = Vector3.Forward;
                palmNormal = palmNormal.Normalized();
                Vector3 palmSide = palmNormal.Cross(armDir).Normalized();
                // Orient palmSide so thumb side (+thumb) points forward/upward consistently
                if (palmSide.Y < -0.25f) palmSide = -palmSide;

                // Measure real max reach if we found hand vertices
                float realReach = handSpan * 0.88f;
                if (handVerts.Count >= 16)
                {
                    float maxProj = handVerts.Max(v => (v - wrist).Dot(armDir));
                    if (maxProj > 0.04f) realReach = maxProj;
                }

                // Cluster vertices into 5 finger rays by lateral coordinate across the palm width
                var lateralOrdered = handVerts.Count >= 20
                    ? handVerts.Where(v => (v - wrist).Dot(armDir) > realReach * 0.35f).ToList()
                    : new List<Vector3>();

                for (int fi = 0; fi < FingerNames.Length; fi++)
                {
                    string fName = FingerNames[fi];
                    bool isThumb = fi == 0;
                    // Fan spread from thumb (-0.42) to pinky (+0.38)
                    float latFrac = fi switch
                    {
                        0 => 0.42f,
                        1 => 0.22f,
                        2 => 0.04f,
                        3 => -0.14f,
                        _ => -0.32f
                    };
                    float lengthFrac = fi switch
                    {
                        0 => 0.56f,
                        1 => 0.90f,
                        2 => 1.00f,
                        3 => 0.92f,
                        _ => 0.78f
                    };

                    Vector3 dir = (armDir * (isThumb ? 0.78f : 1.0f) + palmSide * latFrac).Normalized();
                    float knuckleStart = isThumb ? realReach * 0.24f : realReach * 0.46f;
                    float fingerLen = realReach * lengthFrac - knuckleStart;
                    if (fingerLen < 0.02f) fingerLen = realReach * 0.45f;

                    // Snap tip toward local vertex cluster if geometry exists near that ray
                    Vector3 estTip = wrist + dir * (knuckleStart + fingerLen);
                    if (lateralOrdered.Count >= 10)
                    {
                        Vector3 bestVert = estTip;
                        float bestScore = float.MaxValue;
                        foreach (var hv in lateralOrdered)
                        {
                            float d = hv.DistanceTo(estTip);
                            if (d < bestScore && d < realReach * 0.28f)
                            {
                                bestScore = d;
                                bestVert = hv;
                            }
                        }
                        if (bestScore < float.MaxValue)
                        {
                            estTip = estTip.Lerp(bestVert, 0.55f);
                        }
                    }

                    Vector3 knucklePos = wrist + dir * knuckleStart;
                    Vector3 pipPos = knucklePos.Lerp(estTip, 0.38f);
                    Vector3 dipPos = knucklePos.Lerp(estTip, 0.72f);

                    result[$"finger_{fName}_0_{sfx}"] = knucklePos;
                    result[$"finger_{fName}_1_{sfx}"] = pipPos;
                    result[$"finger_{fName}_2_{sfx}"] = dipPos;
                    result[$"finger_{fName}_end_{sfx}"] = estTip;
                }
            }

            return result;
        }

        public static void MirrorFingerLandmarks(Dictionary<string, Vector3> pins, bool leftToRight, Vector3 modelCenterX)
        {
            if (pins == null) return;
            string srcSfx = leftToRight ? "_L" : "_R";
            string dstSfx = leftToRight ? "_R" : "_L";
            foreach (var f in FingerNames)
            {
                foreach (var j in JointTokens)
                {
                    string srcKey = $"finger_{f}_{j}{srcSfx}";
                    string dstKey = $"finger_{f}_{j}{dstSfx}";
                    if (pins.TryGetValue(srcKey, out Vector3 p))
                    {
                        pins[dstKey] = new Vector3(2f * modelCenterX.X - p.X, p.Y, p.Z);
                    }
                }
            }
        }

        /// <summary>
        /// Generates synthetic finger chain definitions &amp; blends per-finger capsule/geodesic weights onto
        /// distal hand vertices of the rigged custom meshes, parented to hand_L / hand_R (or hero finger bones).
        /// </summary>
        public static int ApplyGeneratedFingerChainsAndWeights(
            Skeleton3D targetSkeleton,
            Skin customSkin,
            List<MeshInstance3D> riggedCustomMeshes,
            Dictionary<string, Vector3> fingerLandmarksSceneSpace,
            Transform3D sceneToHero,
            bool generateMetacarpals,
            bool generateWristTwist,
            out List<string> createdBoneNames)
        {
            var createdList = new List<string>();
            createdBoneNames = createdList;
            if (targetSkeleton == null || customSkin == null || riggedCustomMeshes == null || riggedCustomMeshes.Count == 0)
                return 0;
            if (fingerLandmarksSceneSpace == null || fingerLandmarksSceneSpace.Count == 0)
                return 0;

            var fingerSegments = new List<(int BoneIdx, int HandBoneIdx, Vector3 A, Vector3 B, bool IsLeft, string FingerName)>();
            var boneToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int b = 0; b < targetSkeleton.GetBoneCount(); b++)
                boneToIndex[targetSkeleton.GetBoneName(b)] = b;

            int EnsureFingerBone(string boneName, int parentIdx, Vector3 globalPosHero)
            {
                if (boneToIndex.TryGetValue(boneName, out int existing))
                {
                    CustomModelAutoRigger.GetOrAddSkinBindIndex(customSkin, targetSkeleton, existing);
                    return existing;
                }
                int newIdx = targetSkeleton.GetBoneCount();
                targetSkeleton.AddBone(boneName);
                targetSkeleton.SetBoneParent(newIdx, parentIdx);
                Transform3D parentGlobal = parentIdx >= 0 ? targetSkeleton.GetBoneGlobalRest(parentIdx) : Transform3D.Identity;
                Vector3 localOrigin = parentGlobal.AffineInverse() * globalPosHero;
                targetSkeleton.SetBoneRest(newIdx, new Transform3D(Basis.Identity, localOrigin));
                targetSkeleton.SetBonePosePosition(newIdx, localOrigin);
                targetSkeleton.SetBonePoseRotation(newIdx, Quaternion.Identity);
                targetSkeleton.SetBonePoseScale(newIdx, Vector3.One);
                boneToIndex[boneName] = newIdx;
                createdList.Add(boneName);
                CustomModelAutoRigger.GetOrAddSkinBindIndex(customSkin, targetSkeleton, newIdx);
                return newIdx;
            }

            foreach (bool isLeft in new[] { true, false })
            {
                string sfx = isLeft ? "L" : "R";
                int handBone = targetSkeleton.FindBone($"hand_{sfx}");
                if (handBone < 0) continue;
                Vector3 handPos = targetSkeleton.GetBoneGlobalRest(handBone).Origin;

                if (generateWristTwist)
                {
                    int lowerArm = targetSkeleton.FindBone($"arm_lower_{sfx}");
                    if (lowerArm >= 0)
                    {
                        Vector3 elbowPos = targetSkeleton.GetBoneGlobalRest(lowerArm).Origin;
                        EnsureFingerBone($"arm_lower_{sfx}_WRIST_TWIST", lowerArm, elbowPos.Lerp(handPos, 0.72f));
                    }
                }

                foreach (string fName in FingerNames)
                {
                    if (!fingerLandmarksSceneSpace.TryGetValue($"finger_{fName}_0_{sfx}", out Vector3 p0s) ||
                        !fingerLandmarksSceneSpace.TryGetValue($"finger_{fName}_1_{sfx}", out Vector3 p1s) ||
                        !fingerLandmarksSceneSpace.TryGetValue($"finger_{fName}_2_{sfx}", out Vector3 p2s) ||
                        !fingerLandmarksSceneSpace.TryGetValue($"finger_{fName}_end_{sfx}", out Vector3 pEs))
                    {
                        continue;
                    }

                    Vector3 p0 = sceneToHero * p0s;
                    Vector3 p1 = sceneToHero * p1s;
                    Vector3 p2 = sceneToHero * p2s;
                    Vector3 pE = sceneToHero * pEs;

                    int parentBone = handBone;
                    if (generateMetacarpals)
                    {
                        Vector3 metaPos = handPos.Lerp(p0, 0.42f);
                        parentBone = EnsureFingerBone($"finger_{fName}_meta_{sfx}", handBone, metaPos);
                    }

                    int b0 = EnsureFingerBone($"finger_{fName}_0_{sfx}", parentBone, p0);
                    int b1 = EnsureFingerBone($"finger_{fName}_1_{sfx}", b0, p1);
                    int b2 = EnsureFingerBone($"finger_{fName}_2_{sfx}", b1, p2);

                    fingerSegments.Add((b0, handBone, p0, p1, isLeft, fName));
                    fingerSegments.Add((b1, handBone, p1, p2, isLeft, fName));
                    fingerSegments.Add((b2, handBone, p2, pE, isLeft, fName));
                }
            }

            if (fingerSegments.Count == 0) return 0;

            var (skelToBind, bindToSkel) = CustomModelAutoRigger.BuildSkinIndexMappings(targetSkeleton, customSkin);
            int affectedVertices = 0;

            foreach (var mi in riggedCustomMeshes)
            {
                if (mi?.Mesh is not ArrayMesh arrMesh) continue;
                if (mi.HasMeta("IsSocketProp") && mi.GetMeta("IsSocketProp").AsBool()) continue;

                var rebuilt = new ArrayMesh();
                for (int s = 0; s < arrMesh.GetSurfaceCount(); s++)
                {
                    var arrays = arrMesh.SurfaceGetArrays(s);
                    var verts = CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    var bones = CustomModelAutoRigger.GetInt32Array(arrays[(int)Mesh.ArrayType.Bones]);
                    var weights = CustomModelAutoRigger.GetFloat32Array(arrays[(int)Mesh.ArrayType.Weights]);
                    if (verts == null || bones == null || weights == null)
                    {
                        rebuilt.AddSurfaceFromArrays(arrMesh.SurfaceGetPrimitiveType(s), arrays);
                        continue;
                    }

                    int stride = Math.Max(4, bones.Length / verts.Length);
                    if (stride != 4 && stride != 8) stride = 4;

                    for (int v = 0; v < verts.Length; v++)
                    {
                        // Check if vertex is predominantly bound to hand_L / hand_R or existing finger bone
                        float handWeight = 0f;
                        int matchedHandBone = -1;
                        for (int k = 0; k < stride; k++)
                        {
                            float w = weights[v * stride + k];
                            if (w <= 0.01f) continue;
                            int bindIdx = bones[v * stride + k];
                            int skelIdx = bindToSkel.TryGetValue(bindIdx, out int si) ? si : bindIdx;
                            if (skelIdx >= 0 && skelIdx < targetSkeleton.GetBoneCount())
                            {
                                string bn = targetSkeleton.GetBoneName(skelIdx);
                                if (bn.Equals("hand_L", StringComparison.OrdinalIgnoreCase) ||
                                    bn.Equals("hand_R", StringComparison.OrdinalIgnoreCase) ||
                                    bn.StartsWith("finger_", StringComparison.OrdinalIgnoreCase))
                                {
                                    handWeight += w;
                                    if (matchedHandBone < 0)
                                    {
                                        matchedHandBone = bn.EndsWith("_L", StringComparison.OrdinalIgnoreCase)
                                            ? targetSkeleton.FindBone("hand_L")
                                            : targetSkeleton.FindBone("hand_R");
                                    }
                                }
                            }
                        }

                        if (handWeight < 0.35f || matchedHandBone < 0) continue;

                        Vector3 pos = verts[v];
                        // Pick the SINGLE closest finger (ray isolation prevents cross-finger weight bleeding!)
                        string bestFinger = null;
                        float bestFingerDist = float.MaxValue;
                        foreach (var seg in fingerSegments)
                        {
                            if (seg.HandBoneIdx != matchedHandBone) continue;
                            float d = DistancePointToSegment(pos, seg.A, seg.B);
                            if (d < bestFingerDist)
                            {
                                bestFingerDist = d;
                                bestFinger = seg.FingerName;
                            }
                        }

                        // Only bind to finger bones if vertex is reasonably past the knuckle & within finger radius
                        if (bestFinger == null || bestFingerDist > 0.045f) continue;

                        // Compute soft weights strictly among the 3 phalanges of the winning finger + hand root
                        var fingerWeights = new Dictionary<int, float>();
                        float sum = 0f;
                        foreach (var seg in fingerSegments)
                        {
                            if (seg.HandBoneIdx != matchedHandBone || seg.FingerName != bestFinger) continue;
                            float d = DistancePointToSegment(pos, seg.A, seg.B);
                            float w = 1.0f / MathF.Pow(d + 0.004f, 3.0f);
                            fingerWeights[seg.BoneIdx] = fingerWeights.GetValueOrDefault(seg.BoneIdx) + w;
                            sum += w;
                        }
                        if (sum <= 1e-6f) continue;

                        // Blend palm base near knuckle smoothly back to hand bone
                        var knuckleSeg = fingerSegments.First(seg => seg.HandBoneIdx == matchedHandBone && seg.FingerName == bestFinger);
                        float alongFinger = (pos - knuckleSeg.A).Dot((knuckleSeg.B - knuckleSeg.A).Normalized());
                        if (alongFinger < 0.01f)
                        {
                            float palmFrac = Mathf.Clamp(-alongFinger / 0.025f, 0f, 0.85f);
                            float palmAdd = sum * palmFrac / MathF.Max(0.1f, 1f - palmFrac);
                            fingerWeights[matchedHandBone] = palmAdd;
                            sum += palmAdd;
                        }

                        var top = fingerWeights.OrderByDescending(kv => kv.Value).Take(stride).ToList();
                        float topSum = top.Sum(kv => kv.Value);
                        for (int k = 0; k < stride; k++)
                        {
                            if (k < top.Count && topSum > 1e-6f)
                            {
                                int bind = CustomModelAutoRigger.GetOrAddSkinBindIndex(customSkin, targetSkeleton, top[k].Key);
                                bones[v * stride + k] = bind;
                                weights[v * stride + k] = top[k].Value / topSum;
                            }
                            else
                            {
                                bones[v * stride + k] = 0;
                                weights[v * stride + k] = 0f;
                            }
                        }
                        affectedVertices++;
                    }

                    arrays[(int)Mesh.ArrayType.Bones] = bones;
                    arrays[(int)Mesh.ArrayType.Weights] = weights;
                    rebuilt.AddSurfaceFromArrays(arrMesh.SurfaceGetPrimitiveType(s), arrays);
                    var mat = arrMesh.SurfaceGetMaterial(s);
                    if (mat != null) rebuilt.SurfaceSetMaterial(rebuilt.GetSurfaceCount() - 1, mat);
                    rebuilt.SurfaceSetName(rebuilt.GetSurfaceCount() - 1, arrMesh.SurfaceGetName(s));
                }

                mi.Mesh = rebuilt;
                mi.Skin = customSkin;
            }

            return affectedVertices;
        }

        /// <summary>
        /// Applies an interactive finger curl pose preset (Rest, Fist, Open, Pinch, Point) to a preview skeleton.
        /// </summary>
        public static void ApplyFingerPosePreset(Skeleton3D skeleton, FingerPosePreset preset)
        {
            if (skeleton == null || !GodotObject.IsInstanceValid(skeleton)) return;

            for (int b = 0; b < skeleton.GetBoneCount(); b++)
            {
                string name = skeleton.GetBoneName(b).ToLowerInvariant();
                if (!name.Contains("finger_") && !name.Contains("thumb") && !name.Contains("index") &&
                    !name.Contains("middle") && !name.Contains("ring") && !name.Contains("pinky"))
                    continue;

                if (preset == FingerPosePreset.Rest)
                {
                    skeleton.SetBonePoseRotation(b, skeleton.GetBoneRest(b).Basis.GetRotationQuaternion());
                    continue;
                }

                bool isThumb = name.Contains("thumb");
                bool isIndex = name.Contains("index");
                bool isMiddle = name.Contains("middle");
                bool isRing = name.Contains("ring");
                bool isPinky = name.Contains("pinky");
                bool isMeta = name.Contains("meta");
                if (isMeta) continue;

                float curlDeg = preset switch
                {
                    FingerPosePreset.Open => -8f,
                    FingerPosePreset.Fist => isThumb ? 42f : 72f,
                    FingerPosePreset.Pinch => (isThumb || isIndex) ? 45f : 12f,
                    FingerPosePreset.Point => isIndex ? -5f : (isThumb ? 35f : 75f),
                    _ => 0f
                };

                Quaternion restRot = skeleton.GetBoneRest(b).Basis.GetRotationQuaternion();
                Quaternion curlRot = Quaternion.FromEuler(new Vector3(Mathf.DegToRad(curlDeg), 0f, 0f));
                skeleton.SetBonePoseRotation(b, (restRot * curlRot).Normalized());
            }
        }

        private static float DistancePointToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float lenSq = ab.LengthSquared();
            if (lenSq < 1e-8f) return p.DistanceTo(a);
            float t = Mathf.Clamp((p - a).Dot(ab) / lenSq, 0f, 1f);
            return p.DistanceTo(a + ab * t);
        }
    }
}
