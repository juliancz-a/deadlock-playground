using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace DeadlockPlayground.Importer
{
    /// <summary>
    /// Phase 2 "Add-clothing-bones tool": generates synthetic bone chains anchored to a hero bone
    /// and computes smooth geodesic/capsule weights over vertices inside the target mesh/region.
    /// </summary>
    public static class AddClothingChainTool
    {
        public static int ApplySyntheticChains(
            Skeleton3D targetSkeleton,
            Skin customSkin,
            List<MeshInstance3D> riggedMeshes,
            IEnumerable<BoneChainDef> syntheticChains,
            List<string> warnings = null)
        {
            if (targetSkeleton == null || customSkin == null || riggedMeshes == null || syntheticChains == null)
                return 0;

            var activeSynthetic = syntheticChains
                .Where(c => c != null && c.IsSynthetic && c.Policy == ChainPolicy.PreservePhysicsChain)
                .ToList();
            if (activeSynthetic.Count == 0) return 0;

            int totalAddedBones = 0;

            foreach (var chain in activeSynthetic)
            {
                chain.AppendedHeroBoneNames.Clear();
                int anchorIdx = targetSkeleton.FindBone(string.IsNullOrWhiteSpace(chain.AnchorHeroBone) ? "spine_3" : chain.AnchorHeroBone);
                if (anchorIdx < 0) anchorIdx = targetSkeleton.FindBone("pelvis");
                if (anchorIdx < 0) anchorIdx = 0;

                Vector3 dir = chain.SyntheticDirectionHeroSpace.LengthSquared() > 1e-5f
                    ? chain.SyntheticDirectionHeroSpace.Normalized()
                    : new Vector3(0f, -1f, 0f);
                int segCount = Math.Clamp(chain.SyntheticSegmentCount, 2, 12);
                float totalLen = Math.Clamp(chain.SyntheticLengthMeters, 0.08f, 2.5f);
                float stepLen = totalLen / segCount;

                Vector3 startWorld = chain.SyntheticOriginHeroSpace.LengthSquared() > 1e-5f
                    ? chain.SyntheticOriginHeroSpace
                    : targetSkeleton.GetBoneGlobalRest(anchorIdx).Origin;

                var segPoints = new Vector3[segCount + 1];
                var chainBoneIndices = new int[segCount];
                int parentIdx = anchorIdx;

                string safeBase = SanitizeBoneBase(string.IsNullOrWhiteSpace(chain.Name) ? "cloth_chain" : chain.Name);
                for (int i = 0; i < segCount; i++)
                {
                    segPoints[i] = startWorld + dir * (stepLen * i);
                    string candidateName = $"{safeBase}_{i}";
                    string uniqueName = DeduplicateBoneName(targetSkeleton, candidateName);

                    int boneIdx = targetSkeleton.GetBoneCount();
                    targetSkeleton.AddBone(uniqueName);
                    targetSkeleton.SetBoneParent(boneIdx, parentIdx);

                    Transform3D parentGlobalRest = parentIdx >= 0 ? targetSkeleton.GetBoneGlobalRest(parentIdx) : Transform3D.Identity;
                    Vector3 localPos = parentGlobalRest.AffineInverse() * segPoints[i];
                    targetSkeleton.SetBoneRest(boneIdx, new Transform3D(Basis.Identity, localPos));
                    targetSkeleton.SetBonePosePosition(boneIdx, localPos);
                    targetSkeleton.SetBonePoseRotation(boneIdx, Quaternion.Identity);
                    targetSkeleton.SetBonePoseScale(boneIdx, Vector3.One);

                    CustomModelAutoRigger.GetOrAddSkinBindIndex(customSkin, targetSkeleton, boneIdx);
                    chain.AppendedHeroBoneNames.Add(uniqueName);
                    chainBoneIndices[i] = boneIdx;
                    parentIdx = boneIdx;
                    totalAddedBones++;
                }
                segPoints[segCount] = startWorld + dir * totalLen;

                // Blend weights onto target submesh vertices within SyntheticInfluenceRadius
                float radius = Math.Clamp(chain.SyntheticInfluenceRadius, 0.04f, 1.5f);
                var (skelToBind, bindToSkel) = CustomModelAutoRigger.BuildSkinIndexMappings(targetSkeleton, customSkin);

                foreach (var mi in riggedMeshes)
                {
                    if (mi?.Mesh is not ArrayMesh arrMesh) continue;
                    if (!string.IsNullOrWhiteSpace(chain.TargetMeshName) &&
                        !mi.Name.ToString().Contains(chain.TargetMeshName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

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
                            Vector3 p = verts[v];
                            float minDist = float.MaxValue;
                            var segWeights = new float[segCount];
                            float segSum = 0f;

                            for (int i = 0; i < segCount; i++)
                            {
                                float d = DistancePointToSegment(p, segPoints[i], segPoints[i + 1]);
                                if (d < minDist) minDist = d;
                                if (d < radius)
                                {
                                    float falloff = 1.0f - (d / radius);
                                    float w = falloff * falloff;
                                    segWeights[i] = w;
                                    segSum += w;
                                }
                            }

                            if (minDist >= radius || segSum <= 1e-6f) continue;

                            float chainBlend = Mathf.Clamp(1.0f - (minDist / radius), 0.1f, 0.95f);
                            var merged = new Dictionary<int, float>();
                            for (int k = 0; k < stride; k++)
                            {
                                float w = weights[v * stride + k];
                                if (w <= 1e-5f) continue;
                                int bind = bones[v * stride + k];
                                int skel = bindToSkel.TryGetValue(bind, out int si) ? si : bind;
                                merged[skel] = merged.GetValueOrDefault(skel) + w * (1.0f - chainBlend);
                            }
                            for (int i = 0; i < segCount; i++)
                            {
                                if (segWeights[i] > 1e-5f)
                                {
                                    merged[chainBoneIndices[i]] = merged.GetValueOrDefault(chainBoneIndices[i]) + (segWeights[i] / segSum) * chainBlend;
                                }
                            }

                            var top = merged.OrderByDescending(kv => kv.Value).Take(stride).ToList();
                            float sum = top.Sum(kv => kv.Value);
                            for (int k = 0; k < stride; k++)
                            {
                                if (k < top.Count && sum > 1e-6f)
                                {
                                    int bIdx = CustomModelAutoRigger.GetOrAddSkinBindIndex(customSkin, targetSkeleton, top[k].Key);
                                    bones[v * stride + k] = bIdx;
                                    weights[v * stride + k] = top[k].Value / sum;
                                }
                                else
                                {
                                    bones[v * stride + k] = 0;
                                    weights[v * stride + k] = 0f;
                                }
                            }
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
            }

            if (targetSkeleton.GetBoneCount() > 256)
            {
                warnings?.Add($"Total hero skeleton bone count ({targetSkeleton.GetBoneCount()}) is nearing Source 2 limits after adding synthetic chains.");
            }
            return totalAddedBones;
        }

        private static string SanitizeBoneBase(string raw)
        {
            var chars = raw.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
            string s = new string(chars).Trim('_');
            return string.IsNullOrEmpty(s) ? "cloth_chain" : s;
        }

        private static string DeduplicateBoneName(Skeleton3D skel, string baseName)
        {
            if (skel.FindBone(baseName) < 0) return baseName;
            for (int n = 1; n < 1000; n++)
            {
                string cand = $"{baseName}_ext{n}";
                if (skel.FindBone(cand) < 0) return cand;
            }
            return $"{baseName}_{Guid.NewGuid().ToString("N")[..4]}";
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

