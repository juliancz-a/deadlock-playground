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
        public static Transform3D ComputeImportedMeshToHeroTransform(MeshInstance3D mi, Node3D importedScene, Transform3D alignedTransform)
        {
            if (mi != null && mi.HasMeta("IsHeroSpaceDonorMesh") && mi.GetMeta("IsHeroSpaceDonorMesh").AsBool())
            {
                return ComputeTransformRelativeToRoot(mi, importedScene);
            }
            return alignedTransform * ComputeTransformRelativeToRoot(mi, importedScene);
        }

        private static List<DonorVertex> ExtractHeroDonorVertices(
            Node3D heroNode,
            Skeleton3D skeleton,
            Transform3D[] restGlobal,
            Transform3D[] posedGlobal,
            Dictionary<int, int> bindToSkeletonMap,
            RiggingOptions options)
        {
            var result = new List<DonorVertex>(3500);
            bool isAccessorySlot = !string.IsNullOrWhiteSpace(options?.SlotId);
            List<MeshInstance3D> meshes = null;
            bool allowCustomMeshes = false;

            // When rigging an independent accessory onto a hero that has an active complete replacement model,
            // prefer the replacement model's skinned meshes as the KNN donor cloud so cloth/armor conforms to it.
            if (isAccessorySlot && heroNode != null)
            {
                var mainCustomContainer = heroNode.FindChild("Custom_Rigged_Character", true, false) as Node3D;
                if (mainCustomContainer != null && GodotObject.IsInstanceValid(mainCustomContainer) && mainCustomContainer.Visible)
                {
                    var customCandidates = CollectMeshes(mainCustomContainer)
                        .Where(m => m.Mesh != null && m.Visible && (m.Skin != null || HasSkinnedVertices(m)))
                        .ToList();
                    if (customCandidates.Count > 0)
                    {
                        meshes = customCandidates;
                        allowCustomMeshes = true;
                    }
                }
            }
            meshes ??= CollectOriginalHeroMeshes(heroNode);
            Skin fallbackHeroSkin = FindSkin(heroNode);

            int boneCount = Math.Min(restGlobal.Length, posedGlobal.Length);
            Transform3D[] skinMatrices = new Transform3D[boneCount];
            for (int b = 0; b < boneCount; b++)
            {
                skinMatrices[b] = posedGlobal[b] * restGlobal[b].AffineInverse();
            }

            float subGridSize = 0.045f; // 4.5 cm subsampling for dense limb coverage without spatial holes
            var seenCells = new HashSet<Vector3I>(3500);
            bool excludeClothHair = options?.ExcludeClothHairBones ?? true;
            Span<int> topBones = stackalloc int[4];
            Span<float> topWeights = stackalloc float[4];

            foreach (var mi in meshes)
            {
                if (mi.Mesh == null || mi.Mesh.GetSurfaceCount() == 0) continue;
                if (!allowCustomMeshes && mi.HasMeta("IsCustomImportedMesh")) continue;

                string rawName = mi.Name.ToString().ToLowerInvariant();
                if (rawName.Contains("weapon") || rawName.Contains("gun") || rawName.Contains("cloth") ||
                    rawName.Contains("cape") || rawName.Contains("stake") || rawName.Contains("drum") ||
                    rawName.Contains("cigar") || rawName.Contains("holster") || rawName.Contains("prop") ||
                    rawName.Contains("_ui_") || rawName.Contains("_lod"))
                {
                    // Skip weapon, prop, cloth, UI-head swap, and LOD meshes from donor cloud
                    continue;
                }

                Transform3D meshToHero = ComputeTransformRelativeToRoot(mi, heroNode);
                Skin meshSkin = mi.Skin ?? fallbackHeroSkin;

                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                {
                    Material surfMat = mi.GetActiveMaterial(s) ?? mi.Mesh.SurfaceGetMaterial(s);
                    string matLabel = ((surfMat?.ResourceName ?? "") + " " + (surfMat?.ResourcePath ?? "")).ToLowerInvariant();
                    if (matLabel.Contains("weapon") || matLabel.Contains("gun") || matLabel.Contains("shotgun") ||
                        matLabel.Contains("crossbow") || matLabel.Contains("stake") || matLabel.Contains("cigar") ||
                        matLabel.Contains("drum") || matLabel.Contains("holster") || matLabel.Contains("prop") ||
                        matLabel.Contains("sword") || matLabel.Contains("knife") || matLabel.Contains("rifle") ||
                        matLabel.Contains("pistol"))
                    {
                        continue;
                    }

                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count == 0) continue;

                    var verts = GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    var bones = arrays.Count > (int)Mesh.ArrayType.Bones ? GetInt32Array(arrays[(int)Mesh.ArrayType.Bones]) : null;
                    var weights = arrays.Count > (int)Mesh.ArrayType.Weights ? GetFloat32Array(arrays[(int)Mesh.ArrayType.Weights]) : null;

                    if (verts == null || bones == null || weights == null || verts.Length == 0) continue;

                    int stride = (bones.Length >= verts.Length * 8 && weights.Length >= verts.Length * 8) ? 8 : 4;
                    if (bones.Length < verts.Length * stride || weights.Length < verts.Length * stride) continue;

                    for (int v = 0; v < verts.Length; v++)
                    {
                        topBones.Clear();
                        topWeights.Clear();

                        int baseOff = v * stride;
                        for (int k = 0; k < stride; k++)
                        {
                            float bw = weights[baseOff + k];
                            if (bw <= 0.0001f) continue;

                            int rb = bones[baseOff + k];
                            int sk = meshSkin != null
                                ? ResolveSkinBindBone(meshSkin, rb, skeleton)
                                : (bindToSkeletonMap.TryGetValue(rb, out int sVal) ? sVal : rb);

                            if (sk < 0 || sk >= skeleton.GetBoneCount()) continue;

                            string bName = skeleton.GetBoneName(sk).ToLowerInvariant();
                            if (excludeClothHair ? IsExcludedClothOrAccessoryBone(bName) : IsAlwaysExcludedNonBodyBone(bName))
                            {
                                continue;
                            }
                            if (options?.Method == RiggingMethod.PartialBodyAutoRig && !IsBoneInPartialRegion(bName, options))
                            {
                                continue;
                            }

                            // Merge duplicate bone entries or insert into sorted top-4 slots
                            bool merged = false;
                            for (int t = 0; t < 4; t++)
                            {
                                if (topWeights[t] > 0f && topBones[t] == sk)
                                {
                                    topWeights[t] += bw;
                                    merged = true;
                                    break;
                                }
                            }
                            if (merged) continue;

                            int minSlot = 0;
                            for (int t = 1; t < 4; t++)
                            {
                                if (topWeights[t] < topWeights[minSlot]) minSlot = t;
                            }
                            if (bw > topWeights[minSlot])
                            {
                                topBones[minSlot] = sk;
                                topWeights[minSlot] = bw;
                            }
                        }

                        float sum = topWeights[0] + topWeights[1] + topWeights[2] + topWeights[3];
                        if (sum <= 0.0001f) continue;

                        int primarySkelBone = topBones[0];
                        float bestW = topWeights[0];
                        for (int t = 1; t < 4; t++)
                        {
                            if (topWeights[t] > bestW)
                            {
                                bestW = topWeights[t];
                                primarySkelBone = topBones[t];
                            }
                        }

                        bool isHandOrFinger = false;
                        if (primarySkelBone >= 0 && primarySkelBone < skeleton.GetBoneCount())
                        {
                            string bName = skeleton.GetBoneName(primarySkelBone).ToLowerInvariant();
                            isHandOrFinger = bName.Contains("hand") || bName.Contains("finger") || bName.Contains("thumb") ||
                                             bName.Contains("wrist") || bName.Contains("index") || bName.Contains("pinky") || bName.Contains("ring");
                        }

                        float effectiveSubGrid = isHandOrFinger ? 0.015f : subGridSize;
                        Vector3 restLocal = meshToHero * verts[v];
                        Vector3I cell = new Vector3I(
                            Mathf.FloorToInt(restLocal.X / effectiveSubGrid),
                            Mathf.FloorToInt(restLocal.Y / effectiveSubGrid),
                            Mathf.FloorToInt(restLocal.Z / effectiveSubGrid)
                        );
                        if (!seenCells.Add(cell)) continue;

                        int[] bIndices = new int[4];
                        float[] bWeights = new float[4];
                        for (int t = 0; t < 4; t++)
                        {
                            bIndices[t] = topBones[t];
                            bWeights[t] = topWeights[t] / sum;
                        }

                        // Compute deformed position via CPU LBS using precomputed skinMatrices
                        Vector3 posedPos = Vector3.Zero;
                        for (int t = 0; t < 4; t++)
                        {
                            float w = bWeights[t];
                            if (w <= 0.0001f) continue;

                            int b = bIndices[t];
                            if (b >= 0 && b < boneCount)
                            {
                                posedPos += w * (skinMatrices[b] * restLocal);
                            }
                            else
                            {
                                posedPos += w * restLocal;
                            }
                        }

                        result.Add(new DonorVertex
                        {
                            RestPosition = restLocal,
                            PosedPosition = posedPos,
                            BoneIndices = bIndices,
                            BoneWeights = bWeights
                        });
                    }
                }
            }

            // Fallback: If 0 donor vertices extracted, generate synthetic donor vertices along Anatomical Skeleton Bone Capsules so KNN NEVER fails!
            if (result.Count == 0)
            {
                GD.Print("[CustomModelAutoRigger] Fallback: Generating anatomical capsule donor cloud from skeleton...");
                var capsules = BuildCapsuleSegments(skeleton, posedGlobal, options);
                foreach (var seg in capsules)
                {
                    Vector3 delta = seg.EndPos - seg.StartPos;
                    float len = delta.Length();
                    Vector3 dir = len > 0.001f ? delta / len : Vector3.Up;
                    Vector3 side = MathF.Abs(dir.Y) < 0.9f ? dir.Cross(Vector3.Up).Normalized() : dir.Cross(Vector3.Right).Normalized();
                    Vector3 up = dir.Cross(side).Normalized();

                    for (int step = 0; step <= 8; step++)
                    {
                        float t = step / 8.0f;
                        Vector3 c = seg.StartPos.Lerp(seg.EndPos, t);
                        float r = seg.Radius * 0.8f;

                        result.Add(new DonorVertex { RestPosition = c, PosedPosition = c, BoneIndices = new[] { seg.BoneIndex, 0, 0, 0 }, BoneWeights = new[] { 1f, 0f, 0f, 0f } });
                        result.Add(new DonorVertex { RestPosition = c + side * r, PosedPosition = c + side * r, BoneIndices = new[] { seg.BoneIndex, 0, 0, 0 }, BoneWeights = new[] { 1f, 0f, 0f, 0f } });
                        result.Add(new DonorVertex { RestPosition = c - side * r, PosedPosition = c - side * r, BoneIndices = new[] { seg.BoneIndex, 0, 0, 0 }, BoneWeights = new[] { 1f, 0f, 0f, 0f } });
                        result.Add(new DonorVertex { RestPosition = c + up * r, PosedPosition = c + up * r, BoneIndices = new[] { seg.BoneIndex, 0, 0, 0 }, BoneWeights = new[] { 1f, 0f, 0f, 0f } });
                        result.Add(new DonorVertex { RestPosition = c - up * r, PosedPosition = c - up * r, BoneIndices = new[] { seg.BoneIndex, 0, 0, 0 }, BoneWeights = new[] { 1f, 0f, 0f, 0f } });
                    }
                }
            }

            return result;
        }

        private static Dictionary<Vector3I, List<int>> BuildSpatialHashGrid(List<DonorVertex> donors, float cellSize)
        {
            var grid = new Dictionary<Vector3I, List<int>>();
            for (int i = 0; i < donors.Count; i++)
            {
                Vector3 p = donors[i].PosedPosition;
                Vector3I cell = new Vector3I(
                    Mathf.FloorToInt(p.X / cellSize),
                    Mathf.FloorToInt(p.Y / cellSize),
                    Mathf.FloorToInt(p.Z / cellSize)
                );

                if (!grid.TryGetValue(cell, out var list))
                {
                    list = new List<int>();
                    grid[cell] = list;
                }
                list.Add(i);
            }
            return grid;
        }

        private static float QueryPointToSegDistSq(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float lenSq = ab.LengthSquared();
            if (lenSq < 1e-8f) return p.DistanceSquaredTo(a);
            float t = Mathf.Clamp((p - a).Dot(ab) / lenSq, 0f, 1f);
            return p.DistanceSquaredTo(a + ab * t);
        }

        private static AnatomicalRegion InferStrictLimbRegion(Vector3 queryPoint, List<BoneCapsuleSegment> capsules, AnatomicalRegion[] capsuleRegions)
        {
            if (capsules == null || capsules.Count == 0 || capsuleRegions == null || capsuleRegions.Length < capsules.Count)
                return AnatomicalRegion.Unassigned;

            float minTorsoSq = float.MaxValue;
            float minArmLSq = float.MaxValue;
            float minArmRSq = float.MaxValue;
            float minLegLSq = float.MaxValue;
            float minLegRSq = float.MaxValue;

            for (int c = 0; c < capsules.Count; c++)
            {
                var cap = capsules[c];
                float dSq = QueryPointToSegDistSq(queryPoint, cap.StartPos, cap.EndPos);
                switch (capsuleRegions[c])
                {
                    case AnatomicalRegion.TorsoHead: if (dSq < minTorsoSq) minTorsoSq = dSq; break;
                    case AnatomicalRegion.ArmL:      if (dSq < minArmLSq)  minArmLSq = dSq;  break;
                    case AnatomicalRegion.ArmR:      if (dSq < minArmRSq)  minArmRSq = dSq;  break;
                    case AnatomicalRegion.LegL:      if (dSq < minLegLSq)  minLegLSq = dSq;  break;
                    case AnatomicalRegion.LegR:      if (dSq < minLegRSq)  minLegRSq = dSq;  break;
                }
            }

            float minTorso = minTorsoSq < float.MaxValue ? MathF.Sqrt(minTorsoSq) : float.MaxValue;
            float minArmL  = minArmLSq  < float.MaxValue ? MathF.Sqrt(minArmLSq)  : float.MaxValue;
            float minArmR  = minArmRSq  < float.MaxValue ? MathF.Sqrt(minArmRSq)  : float.MaxValue;
            float minLegL  = minLegLSq  < float.MaxValue ? MathF.Sqrt(minLegLSq)  : float.MaxValue;
            float minLegR  = minLegRSq  < float.MaxValue ? MathF.Sqrt(minLegRSq)  : float.MaxValue;

            // Require clear limb proximity (< 30cm) and a 3cm safety lead over competing regions so
            // shoulder/hip junction vertices continue to blend freely while hanging forearms and bulky
            // gauntlets stay isolated from adjacent trenchcoats or thigh surfaces.
            const float margin = 0.03f;
            const float maxClearDist = 0.30f;
            if (minArmL < maxClearDist && minArmL + margin < minTorso && minArmL + margin < minLegL && minArmL + margin < minArmR)
                return AnatomicalRegion.ArmL;
            if (minArmR < maxClearDist && minArmR + margin < minTorso && minArmR + margin < minLegR && minArmR + margin < minArmL)
                return AnatomicalRegion.ArmR;
            if (minLegL < maxClearDist && minLegL + margin < minTorso && minLegL + margin < minArmL && minLegL + margin < minLegR)
                return AnatomicalRegion.LegL;
            if (minLegR < maxClearDist && minLegR + margin < minTorso && minLegR + margin < minArmR && minLegR + margin < minLegL)
                return AnatomicalRegion.LegR;

            return AnatomicalRegion.Unassigned;
        }

        private static void QueryWeightsWithCapsuleFallback(
            Vector3 queryPoint,
            Dictionary<Vector3I, List<int>> grid,
            List<DonorVertex> donors,
            List<BoneCapsuleSegment> capsules,
            AnatomicalRegion[] capsuleRegions,
            int kNearest,
            float searchRadius,
            float cellSize,
            int[] outIndices,
            float[] outWeights,
            int outOffset)
        {
            float r = searchRadius;
            AnatomicalRegion strictRegion = InferStrictLimbRegion(queryPoint, capsules, capsuleRegions);

            Vector3I centerCell = new Vector3I(
                Mathf.FloorToInt(queryPoint.X / cellSize),
                Mathf.FloorToInt(queryPoint.Y / cellSize),
                Mathf.FloorToInt(queryPoint.Z / cellSize)
            );

            Span<int> bestDonors = stackalloc int[8];
            Span<float> bestDistSq = stackalloc float[8];
            bestDistSq.Fill(float.MaxValue);
            int candidateCount = 0;

            float rSq = r * r;

            // 1. Query center cell first (closest potential candidates)
            if (grid.TryGetValue(centerCell, out var centerList))
            {
                int listCount = centerList.Count;
                for (int li = 0; li < listCount; li++)
                {
                    int dIdx = centerList[li];
                    var donor = donors[dIdx];
                    if (strictRegion != AnatomicalRegion.Unassigned && donor.Region != 0 && donor.Region != (byte)strictRegion)
                        continue;

                    var dPos = donor.PosedPosition;
                    if (queryPoint.X > 0.02f && dPos.X < -0.02f) continue;
                    if (queryPoint.X < -0.02f && dPos.X > 0.02f) continue;

                    float dSq = queryPoint.DistanceSquaredTo(dPos);
                    if (dSq <= rSq && dSq < bestDistSq[7])
                    {
                        int insertPos = 7;
                        while (insertPos > 0 && dSq < bestDistSq[insertPos - 1])
                        {
                            bestDistSq[insertPos] = bestDistSq[insertPos - 1];
                            bestDonors[insertPos] = bestDonors[insertPos - 1];
                            insertPos--;
                        }
                        bestDistSq[insertPos] = dSq;
                        bestDonors[insertPos] = dIdx;
                        candidateCount++;
                        if (candidateCount >= kNearest && bestDistSq[kNearest - 1] < rSq)
                        {
                            rSq = bestDistSq[kNearest - 1];
                        }
                    }
                }
            }

            // 2. Spatial boundary pruning: if closest candidates are nearer than cell boundaries, skip all 26 neighbor cells!
            float minXDist = queryPoint.X - centerCell.X * cellSize;
            float maxXDist = (centerCell.X + 1) * cellSize - queryPoint.X;
            float minYDist = queryPoint.Y - centerCell.Y * cellSize;
            float maxYDist = (centerCell.Y + 1) * cellSize - queryPoint.Y;
            float minZDist = queryPoint.Z - centerCell.Z * cellSize;
            float maxZDist = (centerCell.Z + 1) * cellSize - queryPoint.Z;
            float dMinBorder = MathF.Min(MathF.Min(MathF.Min(minXDist, maxXDist), MathF.Min(minYDist, maxYDist)), MathF.Min(minZDist, maxZDist));

            bool canPruneNeighbors = candidateCount >= kNearest && bestDistSq[kNearest - 1] <= dMinBorder * dMinBorder;

            if (!canPruneNeighbors)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (dx == 0 && dy == 0 && dz == 0) continue; // Already queried center

                            Vector3I c = new Vector3I(centerCell.X + dx, centerCell.Y + dy, centerCell.Z + dz);
                            if (grid.TryGetValue(c, out var list))
                            {
                                int listCount = list.Count;
                                for (int li = 0; li < listCount; li++)
                                {
                                    int dIdx = list[li];
                                    var donor = donors[dIdx];
                                    if (strictRegion != AnatomicalRegion.Unassigned && donor.Region != 0 && donor.Region != (byte)strictRegion)
                                        continue;

                                    var dPos = donor.PosedPosition;
                                    if (queryPoint.X > 0.02f && dPos.X < -0.02f) continue;
                                    if (queryPoint.X < -0.02f && dPos.X > 0.02f) continue;

                                    float dSq = queryPoint.DistanceSquaredTo(dPos);
                                    if (dSq <= rSq && dSq < bestDistSq[7])
                                    {
                                        int insertPos = 7;
                                        while (insertPos > 0 && dSq < bestDistSq[insertPos - 1])
                                        {
                                            bestDistSq[insertPos] = bestDistSq[insertPos - 1];
                                            bestDonors[insertPos] = bestDonors[insertPos - 1];
                                            insertPos--;
                                        }
                                        bestDistSq[insertPos] = dSq;
                                        bestDonors[insertPos] = dIdx;
                                        candidateCount++;
                                        if (candidateCount >= kNearest && bestDistSq[kNearest - 1] < rSq)
                                        {
                                            rSq = bestDistSq[kNearest - 1];
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            Span<int> accumBones = stackalloc int[16];
            Span<float> accumWeights = stackalloc float[16];
            int accumCount = 0;

            if (candidateCount > 0)
            {
                int kMax = Math.Min(Math.Min(candidateCount, kNearest), 8);
                for (int i = 0; i < kMax; i++)
                {
                    if (bestDistSq[i] == float.MaxValue) break;
                    float dist = Mathf.Sqrt(bestDistSq[i]);
                    float wNeighbor = 1.0f / ((dist + 0.002f) * (dist + 0.002f));

                    var donor = donors[bestDonors[i]];
                    for (int k = 0; k < 4; k++)
                    {
                        float bw = donor.BoneWeights[k];
                        if (bw <= 0.0001f) continue;
                        int b = donor.BoneIndices[k];

                        int found = -1;
                        for (int a = 0; a < accumCount; a++)
                        {
                            if (accumBones[a] == b) { found = a; break; }
                        }
                        if (found != -1)
                        {
                            accumWeights[found] += wNeighbor * bw;
                        }
                        else if (accumCount < 16)
                        {
                            accumBones[accumCount] = b;
                            accumWeights[accumCount] = wNeighbor * bw;
                            accumCount++;
                        }
                    }
                }
            }

            // Capsule fallback if few candidates or far from surface
            float closestDist = candidateCount > 0 ? Mathf.Sqrt(bestDistSq[0]) : float.MaxValue;
            if (candidateCount < 2 || closestDist > 0.12f)
            {
                float blendAlpha = Mathf.Clamp((closestDist - 0.08f) / 0.15f, 0f, 1f);
                int capCount = capsules.Count;
                bool hasStrictCapsules = false;
                if (strictRegion != AnatomicalRegion.Unassigned && capsuleRegions != null && capsuleRegions.Length >= capCount)
                {
                    for (int ci = 0; ci < capCount; ci++)
                    {
                        if (capsuleRegions[ci] == strictRegion)
                        {
                            hasStrictCapsules = true;
                            break;
                        }
                    }
                }
                for (int ci = 0; ci < capCount; ci++)
                {
                    var seg = capsules[ci];
                    if (hasStrictCapsules && capsuleRegions[ci] != strictRegion) continue;
                    if (queryPoint.X > 0.02f && (seg.BoneName.EndsWith("_r") || seg.BoneName.Contains("_r_") || seg.BoneName.Contains("right"))) continue;
                    if (queryPoint.X < -0.02f && (seg.BoneName.EndsWith("_l") || seg.BoneName.Contains("_l_") || seg.BoneName.Contains("left"))) continue;

                    float dCapsule = DistancePointToSegment(queryPoint, seg.StartPos, seg.EndPos);
                    float denom = dCapsule + seg.Radius + 0.02f;
                    float wCapsule = 1.0f / (denom * denom * denom * Mathf.Sqrt(denom));
                    float addW = wCapsule * blendAlpha * 10.0f;

                    int found = -1;
                    for (int a = 0; a < accumCount; a++)
                    {
                        if (accumBones[a] == seg.BoneIndex) { found = a; break; }
                    }
                    if (found != -1)
                    {
                        accumWeights[found] += addW;
                    }
                    else if (accumCount < 16)
                    {
                        accumBones[accumCount] = seg.BoneIndex;
                        accumWeights[accumCount] = addW;
                        accumCount++;
                    }
                }
            }

            for (int top = 0; top < 4; top++)
            {
                int bestIdx = -1;
                float maxVal = 0f;
                for (int a = 0; a < accumCount; a++)
                {
                    if (accumWeights[a] > maxVal)
                    {
                        maxVal = accumWeights[a];
                        bestIdx = a;
                    }
                }
                if (bestIdx != -1 && maxVal > 0.0001f)
                {
                    outIndices[outOffset + top] = accumBones[bestIdx];
                    outWeights[outOffset + top] = maxVal;
                    accumWeights[bestIdx] = -1f;
                }
                else
                {
                    outIndices[outOffset + top] = 0;
                    outWeights[outOffset + top] = 0f;
                }
            }

            float sumW = outWeights[outOffset + 0] + outWeights[outOffset + 1] + outWeights[outOffset + 2] + outWeights[outOffset + 3];
            if (sumW > 0.0001f)
            {
                outWeights[outOffset + 0] /= sumW;
                outWeights[outOffset + 1] /= sumW;
                outWeights[outOffset + 2] /= sumW;
                outWeights[outOffset + 3] /= sumW;
            }
            else
            {
                outIndices[outOffset + 0] = 0;
                outWeights[outOffset + 0] = 1.0f;
            }
        }

        private static List<BoneCapsuleSegment> BuildCapsuleSegments(Skeleton3D skeleton, Transform3D[] posedGlobal, RiggingOptions options = null)
        {
            var segments = new List<BoneCapsuleSegment>();
            int boneCount = skeleton.GetBoneCount();
            bool filterPartial = options?.Method == RiggingMethod.PartialBodyAutoRig;

            for (int b = 0; b < boneCount; b++)
            {
                string bName = skeleton.GetBoneName(b).ToLowerInvariant();
                if (IsExcludedClothOrAccessoryBone(bName) || bName.Contains("twist"))
                {
                    continue;
                }

                // Filter to core anatomical bones only (reduces from 270 bones to ~16, eliminating millions of distance checks)
                bool isCoreAnatomical = bName.Contains("pelvis") || bName.Contains("spine") || bName.Contains("chest") ||
                                       bName.Contains("neck") || bName.Contains("head") || bName.Contains("clavicle") ||
                                       bName.Contains("arm_upper") || bName.Contains("arm_lower") || bName == "hand_l" || bName == "hand_r" ||
                                       bName.Contains("leg_upper") || bName.Contains("leg_lower") || bName.Contains("ankle") ||
                                       bName.Contains("thigh") || bName.Contains("calf") || bName == "foot_l" || bName == "foot_r";

                if (!isCoreAnatomical) continue;
                if (filterPartial && !IsBoneInPartialRegion(bName, options)) continue;

                int parent = skeleton.GetBoneParent(b);

                Vector3 startPos = posedGlobal[b].Origin;
                Vector3 endPos;

                // Find first child bone or project forward
                int firstChild = -1;
                for (int c = 0; c < boneCount; c++)
                {
                    if (skeleton.GetBoneParent(c) == b)
                    {
                        firstChild = c;
                        break;
                    }
                }

                if (firstChild != -1)
                {
                    endPos = posedGlobal[firstChild].Origin;
                }
                else if (parent != -1)
                {
                    Vector3 fromParent = startPos - posedGlobal[parent].Origin;
                    endPos = startPos + fromParent.Normalized() * 0.10f;
                }
                else
                {
                    endPos = startPos + Vector3.Up * 0.15f;
                }

                float radius = 0.04f;
                if (bName.Contains("pelvis") || bName.Contains("spine") || bName.Contains("chest")) radius = 0.12f;
                else if (bName.Contains("head")) radius = 0.10f;
                else if (bName.Contains("leg_upper") || bName.Contains("thigh")) radius = 0.08f;
                else if (bName.Contains("leg_lower") || bName.Contains("calf")) radius = 0.06f;
                else if (bName.Contains("arm_upper")) radius = 0.05f;
                else if (bName.Contains("arm_lower")) radius = 0.04f;

                segments.Add(new BoneCapsuleSegment
                {
                    BoneIndex = b,
                    BoneName = bName,
                    StartPos = startPos,
                    EndPos = endPos,
                    Radius = radius
                });
            }

            return segments;
        }

        private static float DistancePointToSegment(Vector3 point, Vector3 start, Vector3 end)
        {
            Vector3 ab = end - start;
            float lenSq = ab.LengthSquared();
            if (lenSq <= 0.00001f) return point.DistanceTo(start);

            float t = Mathf.Clamp((point - start).Dot(ab) / lenSq, 0.0f, 1.0f);
            Vector3 projection = start + t * ab;
            return point.DistanceTo(projection);
        }

        private static bool ApplyWristTwistBlending(
            Vector3 vHero,
            Span<int> topBones,
            Span<float> topWeights,
            ArmSegmentRegistration leftArm,
            ArmSegmentRegistration rightArm,
            int twistL,
            int twistR)
        {
            bool isWrist = false;
            if (leftArm != null)
            {
                if (CheckAndBlendWrist(vHero, topBones, topWeights, leftArm.Bt, leftArm.Ct, leftArm.TargetLowerBone, leftArm.TargetHandBone, twistL))
                {
                    isWrist = true;
                }
            }
            if (rightArm != null)
            {
                if (CheckAndBlendWrist(vHero, topBones, topWeights, rightArm.Bt, rightArm.Ct, rightArm.TargetLowerBone, rightArm.TargetHandBone, twistR))
                {
                    isWrist = true;
                }
            }
            return isWrist;
        }

        private static bool CheckAndBlendWrist(
            Vector3 vHero,
            Span<int> topBones,
            Span<float> topWeights,
            Vector3 Bt,
            Vector3 Ct,
            int lowerBone,
            int handBone,
            int twistBone)
        {
            Vector3 armDir = Ct - Bt;
            float armLen = armDir.Length();
            if (armLen < 0.01f) return false;

            Vector3 uArm = armDir / armLen;
            Vector3 d = vHero - Bt;
            float proj = d.Dot(uArm);
            float t = proj / armLen;

            // Boundary: within final 35% of forearm approaching wrist [0.65, 1.05]
            if (t < 0.65f || t > 1.05f) return false;

            // Radial distance check (must be on forearm/wrist cylinder, r <= 18cm)
            float r = (d - uArm * proj).Length();
            if (r > 0.18f) return false;

            // Check if vertex has influence from forearm or forearm twist
            int lowerIdx = -1;
            int handIdx = -1;
            float forearmWeight = 0f;

            for (int k = 0; k < 4; k++)
            {
                if (topWeights[k] <= 0.0001f) continue;
                if (topBones[k] == lowerBone || (twistBone != -1 && topBones[k] == twistBone))
                {
                    lowerIdx = k;
                    forearmWeight += topWeights[k];
                }
                else if (topBones[k] == handBone)
                {
                    handIdx = k;
                }
            }

            if (forearmWeight <= 0.05f) return false;

            // Smoothstep gradient: alpha = smoothstep(0.65, 1.0, t) * 0.45
            float normT = Mathf.Clamp((t - 0.65f) / 0.35f, 0.0f, 1.0f);
            float alpha = normT * normT * (3.0f - 2.0f * normT) * 0.45f;
            float transferW = forearmWeight * alpha;

            if (transferW <= 0.001f) return false;

            // Deduct from forearm bone(s)
            for (int k = 0; k < 4; k++)
            {
                if (topBones[k] == lowerBone || (twistBone != -1 && topBones[k] == twistBone))
                {
                    topWeights[k] *= (1.0f - alpha);
                }
            }

            // Add to destination bone (prefer twistBone if present, so pronation roll is only 50% without 180 deg collapse!)
            int destBone = (twistBone != -1) ? twistBone : handBone;
            int destIdx = -1;
            for (int k = 0; k < 4; k++)
            {
                if (topBones[k] == destBone) { destIdx = k; break; }
            }

            if (destIdx != -1)
            {
                topWeights[destIdx] += transferW;
            }
            else
            {
                // Replace lowest weight slot
                int minIdx = 0;
                float minVal = topWeights[0];
                for (int k = 1; k < 4; k++)
                {
                    if (topWeights[k] < minVal) { minVal = topWeights[k]; minIdx = k; }
                }
                topBones[minIdx] = destBone;
                topWeights[minIdx] = transferW;
            }

            // Re-normalize
            float sum = topWeights[0] + topWeights[1] + topWeights[2] + topWeights[3];
            if (sum > 0.0001f)
            {
                topWeights[0] /= sum;
                topWeights[1] /= sum;
                topWeights[2] /= sum;
                topWeights[3] /= sum;
            }

            return true;
        }

        private static void SmoothWeightsLaplacian(int[] indices, int vCount, int[] boneIndices, float[] boneWeights, int iterations, Vector3[] verts = null, bool[] vertexMask = null)
        {
            if (vCount <= 0 || iterations <= 0) return;

            // 1. Build adjacency with 1mm spatial hash for coincident vertex welding
            List<int>[] adj = new List<int>[vCount];
            for (int i = 0; i < vCount; i++) adj[i] = new List<int>(6);

            Dictionary<Vector3I, List<int>> spatialClusters = null;
            if (verts != null && verts.Length == vCount)
            {
                spatialClusters = new Dictionary<Vector3I, List<int>>(vCount);
                for (int v = 0; v < vCount; v++)
                {
                    Vector3 p = verts[v];
                    Vector3I q = new Vector3I(
                        Mathf.RoundToInt(p.X * 1000f),
                        Mathf.RoundToInt(p.Y * 1000f),
                        Mathf.RoundToInt(p.Z * 1000f)
                    );
                    if (!spatialClusters.TryGetValue(q, out var list))
                    {
                        list = new List<int>(2);
                        spatialClusters[q] = list;
                    }
                    list.Add(v);
                }

                // Connect coincident vertices in adjacency graph
                foreach (var list in spatialClusters.Values)
                {
                    if (list.Count <= 1) continue;
                    for (int a = 0; a < list.Count; a++)
                    {
                        for (int b = a + 1; b < list.Count; b++)
                        {
                            int vA = list[a];
                            int vB = list[b];
                            adj[vA].Add(vB);
                            adj[vB].Add(vA);
                        }
                    }
                }
            }

            if (indices != null && indices.Length >= 3)
            {
                for (int i = 0; i < indices.Length; i += 3)
                {
                    int i0 = indices[i];
                    int i1 = indices[i + 1];
                    int i2 = indices[i + 2];

                    if (i0 < vCount && i1 < vCount && i2 < vCount)
                    {
                        adj[i0].Add(i1); adj[i0].Add(i2);
                        adj[i1].Add(i0); adj[i1].Add(i2);
                        adj[i2].Add(i0); adj[i2].Add(i1);
                    }
                }
            }

            float lambda = 0.35f;
            int[] newIndices = new int[vCount * 4];
            float[] newWeights = new float[vCount * 4];

            for (int it = 0; it < iterations; it++)
            {
                System.Threading.Tasks.Parallel.For(0, vCount, v =>
                {
                    if (vertexMask != null && !vertexMask[v])
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            newIndices[v * 4 + k] = boneIndices[v * 4 + k];
                            newWeights[v * 4 + k] = boneWeights[v * 4 + k];
                        }
                        return;
                    }
                    SmoothSingleVertex(v, adj, boneIndices, boneWeights, newIndices, newWeights, lambda);
                });

                Array.Copy(newIndices, boneIndices, vCount * 4);
                Array.Copy(newWeights, boneWeights, vCount * 4);
            }

            // Post-pass: Average weights across coincident 3D vertex positions (within 1mm)
            // Ensures inverted-hull black outline shells and split UV seams receive 100% identical weights to skin!
            if (spatialClusters != null)
            {
                var accumDict = new Dictionary<int, float>(8);
                Span<int> canonBones = stackalloc int[4];
                Span<float> canonWeights = stackalloc float[4];
                foreach (var list in spatialClusters.Values)
                {
                    if (list.Count <= 1) continue;

                    accumDict.Clear();
                    float totalWeight = 0f;
                    foreach (int v in list)
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            float w = boneWeights[v * 4 + k];
                            if (w <= 0.0001f) continue;
                            int b = boneIndices[v * 4 + k];
                            accumDict[b] = accumDict.GetValueOrDefault(b, 0f) + w;
                            totalWeight += w;
                        }
                    }

                    if (totalWeight <= 0.0001f) continue;

                    var topBones = accumDict.OrderByDescending(kv => kv.Value).Take(4).ToList();
                    float normSum = topBones.Sum(kv => kv.Value);

                    for (int k = 0; k < 4; k++)
                    {
                        if (k < topBones.Count && normSum > 0.0001f)
                        {
                            canonBones[k] = topBones[k].Key;
                            canonWeights[k] = topBones[k].Value / normSum;
                        }
                        else
                        {
                            canonBones[k] = 0;
                            canonWeights[k] = 0f;
                        }
                    }

                    foreach (int v in list)
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            boneIndices[v * 4 + k] = canonBones[k];
                            boneWeights[v * 4 + k] = canonWeights[k];
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Stabilizes neck and head bone weights against floating-head stretch (e.g. Paradox/Chrono)
        /// where neck_0 and head translate upward away from spine_3 during jumps and traversal animations.
        /// Anchors upper chest/collar to spine_3 and blends head/cervical rotation onto spine_3 (75%) and neck_0 (25%),
        /// keeping chin-to-collar stretch strictly <= 1.10x without preview-only position clamping.
        /// Operates strictly in Skeleton3D bone index space BEFORE bone indices are remapped to Skin bind indices.
        /// </summary>
        public static void StabilizeNeckFloatingHeadVertex(
            Span<int> bones,
            Span<float> weights,
            int headBone,
            int neckBone,
            int spine3Bone,
            int spine2Bone = -1,
            float heroY = 0f,
            float throatYThreshold = 1.95f)
        {
            if (headBone < 0 || spine3Bone < 0) return;

            float headW = 0f;
            float neckW = 0f;
            float spine2W = 0f;
            for (int k = 0; k < 4; k++)
            {
                if (weights[k] <= 0.0001f) continue;
                if (bones[k] == headBone) headW += weights[k];
                else if (neckBone >= 0 && bones[k] == neckBone) neckW += weights[k];
                else if (spine2Bone >= 0 && bones[k] == spine2Bone) spine2W += weights[k];
            }

            bool isCervicalOrHead = headW > 0.0001f || neckW > 0.0001f;
            bool isUpperThroatSpine2 = spine2W > 0.0001f && (heroY >= throatYThreshold || isCervicalOrHead);

            if (!isCervicalOrHead && !isUpperThroatSpine2) return;

            // Anchor upper neck/head: 92% spine_3, 8% neck_0 (or 100% spine_3 if neckBone < 0)
            // Anchor lower neck: 95% spine_3, 5% neck_0
            // Transfer spine_2 influence on throat/collarline to spine_3 to eliminate accordion expansion
            float spine3Add = (headW * 0.92f) + (neckW * 0.95f) + (isUpperThroatSpine2 ? spine2W : 0f);
            float neckAdd = (neckBone >= 0) ? ((headW * 0.08f) + (neckW * 0.05f)) : 0f;
            if (neckBone < 0) spine3Add += neckAdd;

            Span<int> uBones = stackalloc int[6];
            Span<float> uWeights = stackalloc float[6];
            int uCount = 0;

            // Collect other unaffected influences
            for (int k = 0; k < 4; k++)
            {
                int b = bones[k];
                float w = weights[k];
                if (w <= 0.0001f) continue;
                if (b == headBone || (neckBone >= 0 && b == neckBone)) continue;
                if (isUpperThroatSpine2 && b == spine2Bone) continue;

                int found = -1;
                for (int u = 0; u < uCount; u++)
                {
                    if (uBones[u] == b) { found = u; break; }
                }
                if (found != -1) uWeights[found] += w;
                else if (uCount < 6) { uBones[uCount] = b; uWeights[uCount] = w; uCount++; }
            }

            // Add spine_3 influence
            if (spine3Add > 0.0001f && spine3Bone >= 0)
            {
                int found = -1;
                for (int u = 0; u < uCount; u++)
                {
                    if (uBones[u] == spine3Bone) { found = u; break; }
                }
                if (found != -1) uWeights[found] += spine3Add;
                else if (uCount < 6) { uBones[uCount] = spine3Bone; uWeights[uCount] = spine3Add; uCount++; }
            }

            // Add neck_0 influence
            if (neckAdd > 0.0001f && neckBone >= 0)
            {
                int found = -1;
                for (int u = 0; u < uCount; u++)
                {
                    if (uBones[u] == neckBone) { found = u; break; }
                }
                if (found != -1) uWeights[found] += neckAdd;
                else if (uCount < 6) { uBones[uCount] = neckBone; uWeights[uCount] = neckAdd; uCount++; }
            }

            // Sort top 4 influences descending by weight and normalize kept 4 to sum to 1.00000
            for (int k = 0; k < 4; k++)
            {
                int best = -1;
                float maxW = 0f;
                for (int u = 0; u < uCount; u++)
                {
                    if (uWeights[u] > maxW)
                    {
                        maxW = uWeights[u];
                        best = u;
                    }
                }
                if (best != -1 && maxW > 0.0001f)
                {
                    bones[k] = uBones[best];
                    weights[k] = maxW;
                    uWeights[best] = -1f;
                }
                else
                {
                    bones[k] = 0;
                    weights[k] = 0f;
                }
            }

            float keptSum = weights[0] + weights[1] + weights[2] + weights[3];
            if (keptSum > 0.0001f)
            {
                float invKept = 1.0f / keptSum;
                weights[0] *= invKept;
                weights[1] *= invKept;
                weights[2] *= invKept;
                weights[3] *= invKept;
            }
            else if (spine3Bone >= 0)
            {
                bones[0] = spine3Bone;
                weights[0] = 1.0f;
            }
        }

        public static void StabilizeNeckFloatingHeadWeights(
            Skeleton3D skeleton,
            int vCount,
            int[] boneIndices,
            float[] boneWeights,
            Vector3[] verts = null)
        {
            if (skeleton == null || boneIndices == null || boneWeights == null || vCount <= 0) return;
            int headBone = skeleton.FindBone("head");
            int neckBone = skeleton.FindBone("neck_0");
            if (neckBone < 0) neckBone = skeleton.FindBone("neck");
            int spine3Bone = skeleton.FindBone("spine_3");
            if (spine3Bone < 0) spine3Bone = skeleton.FindBone("spine_2");
            int spine2Bone = skeleton.FindBone("spine_2");
            if (headBone < 0 || spine3Bone < 0) return;

            for (int v = 0; v < vCount; v++)
            {
                int baseIdx = v * 4;
                float y = verts != null && v < verts.Length ? verts[v].Y : 0f;
                StabilizeNeckFloatingHeadVertex(
                    boneIndices.AsSpan(baseIdx, 4),
                    boneWeights.AsSpan(baseIdx, 4),
                    headBone,
                    neckBone,
                    spine3Bone,
                    spine2Bone,
                    y);
            }
        }

        /// <summary>
        /// Rest-pose limb segment and procedural counter-twist bone descriptors for a hero skeleton.
        /// </summary>
        public sealed class LimbTwistChain
        {
            public string Label { get; init; }
            public bool IsLeg { get; init; }
            public bool IsDistalTwist { get; init; }
            public int UpperBone { get; init; }
            public int TwistFullBone { get; init; }
            public int TwistHalfBone { get; init; }
            public int LowerBone { get; init; }
            public int DistalBone { get; init; }
            public Vector3 UpperOrigin { get; init; }
            public Vector3 LowerOrigin { get; init; }
            public Vector3 DistalOrigin { get; init; }
            public Vector3 UpperDir { get; init; }
            public float UpperLen { get; init; }
            public Vector3 LowerDir { get; init; }
            public float LowerLen { get; init; }
            public float MaxRadius { get; init; }
        }

        /// <summary>
        /// Resolved thigh, upper-arm, and forearm twist chains on a Deadlock hero skeleton.
        /// </summary>
        public sealed class LimbTwistProfile
        {
            public int PelvisBone { get; init; }
            public LimbTwistChain[] Chains { get; init; } = Array.Empty<LimbTwistChain>();
        }

        private readonly struct LimbTwistChainSpec
        {
            public LimbTwistChainSpec(string label, bool isLeg, string[] upper, string[] twistFull, string[] twistHalf, string[] lower, string[] distal, bool isDistalTwist = false)
            {
                Label = label;
                IsLeg = isLeg;
                IsDistalTwist = isDistalTwist;
                Upper = upper;
                TwistFull = twistFull;
                TwistHalf = twistHalf;
                Lower = lower;
                Distal = distal;
            }

            public string Label { get; }
            public bool IsLeg { get; }
            public bool IsDistalTwist { get; }
            public string[] Upper { get; }
            public string[] TwistFull { get; }
            public string[] TwistHalf { get; }
            public string[] Lower { get; }
            public string[] Distal { get; }
        }

        private static readonly LimbTwistChainSpec[] DefaultLimbTwistSpecs =
        {
            new(
                "LeftThigh",
                true,
                new[] { "leg_upper_L", "leg_upper_l", "thigh_L", "thigh_l" },
                new[] { "leg_upper_L_TWIST", "leg_upper_l_twist", "leg_upper_twist_L" },
                new[] { "leg_upper_L_TWIST1", "leg_upper_l_twist1", "leg_upper_twist1_L" },
                new[] { "leg_lower_L", "leg_lower_l", "calf_L", "knee_L" },
                new[] { "ankle_L", "ankle_l", "foot_L", "foot_l" }),
            new(
                "RightThigh",
                true,
                new[] { "leg_upper_R", "leg_upper_r", "thigh_R", "thigh_r" },
                new[] { "leg_upper_R_TWIST", "leg_upper_r_twist", "leg_upper_twist_R" },
                new[] { "leg_upper_R_TWIST1", "leg_upper_r_twist1", "leg_upper_twist1_R" },
                new[] { "leg_lower_R", "leg_lower_r", "calf_R", "knee_R" },
                new[] { "ankle_R", "ankle_r", "foot_R", "foot_r" }),
            new(
                "LeftUpperArm",
                false,
                new[] { "arm_upper_L", "arm_upper_l", "upperarm_l", "leftarm" },
                new[] { "arm_upper_L_TWIST", "arm_upper_l_twist", "arm_upper_twist_L" },
                new[] { "arm_upper_L_TWIST1", "arm_upper_l_twist1", "arm_upper_twist1_L" },
                new[] { "arm_lower_L", "arm_lower_l", "forearm_l" },
                new[] { "hand_L", "hand_l", "wrist_l" }),
            new(
                "RightUpperArm",
                false,
                new[] { "arm_upper_R", "arm_upper_r", "upperarm_r", "rightarm" },
                new[] { "arm_upper_R_TWIST", "arm_upper_r_twist", "arm_upper_twist_R" },
                new[] { "arm_upper_R_TWIST1", "arm_upper_r_twist1", "arm_upper_twist1_R" },
                new[] { "arm_lower_R", "arm_lower_r", "forearm_r" },
                new[] { "hand_R", "hand_r", "wrist_r" }),
            // Forearms use distal wrist-follow twist: arm_lower_*_TWIST (+0.5 mid-forearm) -> arm_lower_*_TWIST1 (+1.0 near wrist)
            new(
                "LeftForearm",
                false,
                new[] { "arm_lower_L", "arm_lower_l", "forearm_l" },
                new[] { "arm_lower_L_TWIST1", "arm_lower_l_twist1", "arm_lower_twist1_L" },
                new[] { "arm_lower_L_TWIST", "arm_lower_l_twist", "arm_lower_twist_L" },
                new[] { "hand_L", "hand_l", "wrist_l" },
                Array.Empty<string>(),
                isDistalTwist: true),
            new(
                "RightForearm",
                false,
                new[] { "arm_lower_R", "arm_lower_r", "forearm_r" },
                new[] { "arm_lower_R_TWIST1", "arm_lower_r_twist1", "arm_lower_twist1_R" },
                new[] { "arm_lower_R_TWIST", "arm_lower_r_twist", "arm_lower_twist_R" },
                new[] { "hand_R", "hand_r", "wrist_r" },
                Array.Empty<string>(),
                isDistalTwist: true)
        };

        /// <summary>
        /// Resolves upper-limb and forearm joints and existing procedural *_TWIST / *_TWIST1 bones on the hero skeleton.
        /// Works generically across any Deadlock hero skeleton without hardcoded bone indices.
        /// </summary>
        public static LimbTwistProfile BuildLimbTwistProfile(Skeleton3D skeleton, Transform3D[] restGlobalTransforms = null, Transform3D? skelToHeroTransform = null)
        {
            if (skeleton == null) return new LimbTwistProfile();

            int pelvisBone = FindBoneAny(skeleton, "pelvis", "hips", "bip01_pelvis");
            var chains = new List<LimbTwistChain>(DefaultLimbTwistSpecs.Length);
            Transform3D toHero = skelToHeroTransform ?? Transform3D.Identity;

            Vector3 GetBoneOrigin(int boneIdx)
            {
                Vector3 localOrigin = (restGlobalTransforms != null && boneIdx >= 0 && boneIdx < restGlobalTransforms.Length)
                    ? restGlobalTransforms[boneIdx].Origin
                    : skeleton.GetBoneGlobalRest(boneIdx).Origin;
                return toHero * localOrigin;
            }

            foreach (var spec in DefaultLimbTwistSpecs)
            {
                int upper = FindBoneAny(skeleton, spec.Upper);
                int lower = FindBoneAny(skeleton, spec.Lower);
                if (upper < 0 || lower < 0) continue;

                int distal = spec.Distal.Length > 0 ? FindBoneAny(skeleton, spec.Distal) : -1;
                int twistFull = FindBoneAny(skeleton, spec.TwistFull);
                int twistHalf = FindBoneAny(skeleton, spec.TwistHalf);

                Vector3 pUpper = GetBoneOrigin(upper);
                Vector3 pLower = GetBoneOrigin(lower);
                Vector3 pDistal = distal >= 0 ? GetBoneOrigin(distal) : pLower;

                Vector3 upperVec = pLower - pUpper;
                float upperLen = upperVec.Length();
                if (upperLen < 0.05f) continue;

                Vector3 lowerVec = pDistal - pLower;
                float lowerLen = lowerVec.Length();

                chains.Add(new LimbTwistChain
                {
                    Label = spec.Label,
                    IsLeg = spec.IsLeg,
                    IsDistalTwist = spec.IsDistalTwist,
                    UpperBone = upper,
                    TwistFullBone = twistFull,
                    TwistHalfBone = twistHalf,
                    LowerBone = lower,
                    DistalBone = distal,
                    UpperOrigin = pUpper,
                    LowerOrigin = pLower,
                    DistalOrigin = pDistal,
                    UpperDir = upperVec / upperLen,
                    UpperLen = upperLen,
                    LowerDir = lowerLen > 0.05f ? lowerVec / lowerLen : upperVec / upperLen,
                    LowerLen = lowerLen,
                    MaxRadius = spec.IsLeg ? MathF.Max(0.28f, upperLen * 0.95f) : MathF.Max(0.20f, upperLen * 0.85f)
                });
            }

            return new LimbTwistProfile
            {
                PelvisBone = pelvisBone,
                Chains = chains.ToArray()
            };
        }

        private static float SmoothStep01(float edge0, float edge1, float x)
        {
            if (edge1 <= edge0) return x >= edge1 ? 1.0f : 0.0f;
            float u = Mathf.Clamp((x - edge0) / (edge1 - edge0), 0.0f, 1.0f);
            return u * u * (3.0f - 2.0f * u);
        }

        /// <summary>
        /// Runs <see cref="AdjustLimbTwistAndHipWeights"/> jointly across all rigged surfaces so
        /// <see cref="SmoothWeightsLaplacian"/> welds coincident seam vertices across separate meshes
        /// (e.g. upper-body shorts mesh + bare legs mesh) and guarantees 0.0000 weight divergence across seams.
        /// </summary>
        public static void AdjustLimbTwistAndHipWeightsMultiSurface(
            Skeleton3D skeleton,
            IReadOnlyList<(Vector3[] Verts, int[] Indices, int[] BoneIndices, float[] BoneWeights)> surfaces,
            LimbTwistProfile profile = null)
        {
            if (skeleton == null || surfaces == null || surfaces.Count == 0)
                return;
            if (surfaces.Count == 1)
            {
                var s0 = surfaces[0];
                AdjustLimbTwistAndHipWeights(skeleton, s0.Verts.Length, s0.BoneIndices, s0.BoneWeights, s0.Verts, s0.Indices, profile);
                return;
            }

            int totalV = 0;
            int totalI = 0;
            for (int s = 0; s < surfaces.Count; s++)
            {
                var surf = surfaces[s];
                if (surf.Verts == null || surf.Verts.Length == 0) continue;
                totalV += surf.Verts.Length;
                if (surf.Indices != null && surf.Indices.Length >= 3) totalI += surf.Indices.Length;
            }
            if (totalV == 0) return;

            var allVerts = new Vector3[totalV];
            var allBones = new int[totalV * 4];
            var allWeights = new float[totalV * 4];
            var allIndices = totalI >= 3 ? new int[totalI] : null;

            int vOff = 0;
            int iOff = 0;
            for (int s = 0; s < surfaces.Count; s++)
            {
                var surf = surfaces[s];
                int vc = surf.Verts?.Length ?? 0;
                if (vc == 0) continue;
                Array.Copy(surf.Verts, 0, allVerts, vOff, vc);
                Array.Copy(surf.BoneIndices, 0, allBones, vOff * 4, vc * 4);
                Array.Copy(surf.BoneWeights, 0, allWeights, vOff * 4, vc * 4);
                if (allIndices != null && surf.Indices != null && surf.Indices.Length >= 3)
                {
                    int ic = surf.Indices.Length;
                    for (int k = 0; k < ic; k++)
                        allIndices[iOff + k] = surf.Indices[k] + vOff;
                    iOff += ic;
                }
                vOff += vc;
            }

            AdjustLimbTwistAndHipWeights(skeleton, totalV, allBones, allWeights, allVerts, allIndices, profile);

            // Copy updated cross-seam-welded weights back to each surface
            vOff = 0;
            for (int s = 0; s < surfaces.Count; s++)
            {
                var surf = surfaces[s];
                int vc = surf.Verts?.Length ?? 0;
                if (vc == 0) continue;
                Array.Copy(allBones, vOff * 4, surf.BoneIndices, 0, vc * 4);
                Array.Copy(allWeights, vOff * 4, surf.BoneWeights, 0, vc * 4);
                vOff += vc;
            }
        }

        /// <summary>
        /// Automatically redistributes proximal thigh and upper-arm weights onto existing Source 2
        /// counter-twist bones (*_TWIST / *_TWIST1), softens razor-sharp 1-bone hip seams for LBS volume
        /// preservation, and prunes stray distal ankle/hand bleeds above the knee/elbow.
        /// Operates strictly in Skeleton3D bone index space using hero rest-pose vertex coordinates.
        /// </summary>
        public static void AdjustLimbTwistAndHipWeights(
            Skeleton3D skeleton,
            int vCount,
            int[] boneIndices,
            float[] boneWeights,
            Vector3[] heroRestVerts,
            int[] triangleIndices = null,
            LimbTwistProfile profile = null)
        {
            if (skeleton == null || boneIndices == null || boneWeights == null || heroRestVerts == null || vCount <= 0)
                return;

            profile ??= BuildLimbTwistProfile(skeleton);
            if (profile.Chains == null || profile.Chains.Length == 0)
                return;

            bool[] creaseSeamMask = new bool[vCount];
            int seamCount = 0;

            System.Threading.Tasks.Parallel.For(0, vCount, v =>
            {
                int baseIdx = v * 4;
                if (AdjustSingleVertexLimbWeights(
                    heroRestVerts[v],
                    boneIndices.AsSpan(baseIdx, 4),
                    boneWeights.AsSpan(baseIdx, 4),
                    profile))
                {
                    creaseSeamMask[v] = true;
                    System.Threading.Interlocked.Increment(ref seamCount);
                }
            });

            // Feather hip & shoulder crease seams across neighboring triangles and weld coincident UV/outline vertices
            if (seamCount > 0 && triangleIndices != null && triangleIndices.Length >= 3)
            {
                SmoothWeightsLaplacian(triangleIndices, vCount, boneIndices, boneWeights, 2, heroRestVerts, creaseSeamMask);

                // Re-enforce cross-leg bleed & distal thigh pelvis caps on smoothed crease vertices
                // so Laplacian neighbor averaging across the crotch/hip seam does not re-introduce minor opposite-leg
                // weights or drag pelvis weight down below the hip crease.
                System.Threading.Tasks.Parallel.For(0, vCount, v =>
                {
                    if (!creaseSeamMask[v]) return;
                    int baseIdx = v * 4;
                    PostSmoothLegCleanup(
                        heroRestVerts[v],
                        boneIndices.AsSpan(baseIdx, 4),
                        boneWeights.AsSpan(baseIdx, 4),
                        profile);
                });
            }
        }

        private static bool CleanupLegPelvisAndCrossBleed(
            LimbTwistProfile profile,
            in LimbTwistChain chain,
            float tUpper,
            float rPerp,
            Span<int> uBones,
            Span<float> uWeights,
            ref int uCount,
            ref float upperW,
            int absorbBoneOverride = -1)
        {
            bool changed = false;
            int absorbBone = absorbBoneOverride >= 0 ? absorbBoneOverride : chain.UpperBone;

            if (profile.PelvisBone >= 0 && rPerp <= 0.32f)
            {
                float pelvisW = GetLimbWeight(uBones, uWeights, uCount, profile.PelvisBone);
                // Keep hip/glute crease weights smooth (no hard step-cliff at tUpper=0) while capping stray
                // pelvis bleed below tUpper >= 0.16 down to <= 0.12 by tUpper = 0.20 for thigh LBS volume preservation.
                float maxPelvis = 0.12f * (1.0f - SmoothStep01(0.18f, 0.28f, tUpper));
                float thighGate = tUpper >= 0.20f ? 1.0f : SmoothStep01(0.12f, 0.28f, upperW);
                float effectiveMaxPelvis = Mathf.Lerp(pelvisW, maxPelvis, thighGate);

                if (tUpper >= 0.16f && tUpper <= 0.50f && (upperW + pelvisW) >= 0.40f && upperW >= 0.12f && pelvisW > effectiveMaxPelvis)
                {
                    float cappedPelvis = effectiveMaxPelvis > 0.005f ? effectiveMaxPelvis : 0f;
                    float excess = pelvisW - cappedPelvis;
                    if (excess > 0.003f)
                    {
                        SubtractLimbWeight(uBones, uWeights, ref uCount, profile.PelvisBone);
                        if (cappedPelvis > 0f)
                            AccumulateLimbWeight(uBones, uWeights, ref uCount, profile.PelvisBone, cappedPelvis);
                        if (absorbBoneOverride >= 0)
                            AccumulateLimbWeight(uBones, uWeights, ref uCount, absorbBone, excess);
                        upperW += excess;
                        changed = true;
                    }
                }
                else if (absorbBoneOverride < 0 && tUpper >= -0.10f && tUpper <= 0.05f && upperW >= 0.45f && pelvisW < 0.24f)
                {
                    float uHip = 1.0f - SmoothStep01(-0.10f, 0.05f, tUpper);
                    float uRad = 1.0f - SmoothStep01(0.14f, 0.32f, rPerp);
                    float desiredPelvis = 0.24f * uHip * (0.4f + 0.6f * uRad);
                    float transfer = MathF.Min(MathF.Max(0f, desiredPelvis - pelvisW), upperW * 0.40f);
                    if (transfer > 0.005f)
                    {
                        AccumulateLimbWeight(uBones, uWeights, ref uCount, profile.PelvisBone, transfer);
                        upperW -= transfer;
                        changed = true;
                    }
                }
            }

            if (tUpper >= 0.01f && tUpper <= 1.05f && rPerp <= 0.18f && upperW >= 0.18f)
            {
                for (int ci = 0; ci < profile.Chains.Length; ci++)
                {
                    var other = profile.Chains[ci];
                    if (!other.IsLeg || other.UpperBone == chain.UpperBone)
                        continue;

                    float oppW = GetLimbWeight(uBones, uWeights, uCount, other.UpperBone)
                               + GetLimbWeight(uBones, uWeights, uCount, other.TwistFullBone)
                               + GetLimbWeight(uBones, uWeights, uCount, other.TwistHalfBone)
                               + GetLimbWeight(uBones, uWeights, uCount, other.LowerBone);

                    if (oppW > 0.001f && oppW < 0.24f && upperW >= oppW * 2.2f)
                    {
                        SubtractLimbWeight(uBones, uWeights, ref uCount, other.UpperBone);
                        SubtractLimbWeight(uBones, uWeights, ref uCount, other.TwistFullBone);
                        SubtractLimbWeight(uBones, uWeights, ref uCount, other.TwistHalfBone);
                        SubtractLimbWeight(uBones, uWeights, ref uCount, other.LowerBone);
                        if (absorbBoneOverride >= 0)
                            AccumulateLimbWeight(uBones, uWeights, ref uCount, absorbBone, oppW);
                        upperW += oppW;
                        changed = true;
                    }
                }
            }

            return changed;
        }

        private static void PostSmoothLegCleanup(
            Vector3 vPos,
            Span<int> bones,
            Span<float> weights,
            LimbTwistProfile profile)
        {
            Span<int> uBones = stackalloc int[12];
            Span<float> uWeights = stackalloc float[12];
            int uCount = 0;
            for (int k = 0; k < 4; k++)
            {
                if (weights[k] > 0.0001f)
                    AccumulateLimbWeight(uBones, uWeights, ref uCount, bones[k], weights[k]);
            }
            if (uCount == 0) return;

            bool touched = false;
            for (int c = 0; c < profile.Chains.Length; c++)
            {
                var chain = profile.Chains[c];
                if (!chain.IsLeg || chain.UpperLen <= 0.05f) continue;

                float wUpper = GetLimbWeight(uBones, uWeights, uCount, chain.UpperBone);
                float wFull = GetLimbWeight(uBones, uWeights, uCount, chain.TwistFullBone);
                float wHalf = GetLimbWeight(uBones, uWeights, uCount, chain.TwistHalfBone);
                float upperW = wUpper + wFull + wHalf;
                if (upperW <= 0.02f) continue;

                Vector3 relU = vPos - chain.UpperOrigin;
                float projU = relU.Dot(chain.UpperDir);
                float tUpper = projU / chain.UpperLen;
                float rPerp = (relU - chain.UpperDir * projU).Length();
                if (tUpper < 0.00f || tUpper > 1.05f || rPerp > chain.MaxRadius) continue;

                int dominantUpper = chain.UpperBone;
                float bestU = wUpper;
                if (wHalf > bestU && chain.TwistHalfBone >= 0) { bestU = wHalf; dominantUpper = chain.TwistHalfBone; }
                if (wFull > bestU && chain.TwistFullBone >= 0) { bestU = wFull; dominantUpper = chain.TwistFullBone; }

                if (CleanupLegPelvisAndCrossBleed(profile, in chain, tUpper, rPerp, uBones, uWeights, ref uCount, ref upperW, dominantUpper))
                    touched = true;
            }

            if (touched)
                WriteTop4NormalizedWeights(uBones, uWeights, uCount, bones, weights);
        }

        private static bool AdjustSingleVertexLimbWeights(
            Vector3 vPos,
            Span<int> bones,
            Span<float> weights,
            LimbTwistProfile profile)
        {
            Span<int> uBones = stackalloc int[12];
            Span<float> uWeights = stackalloc float[12];
            int uCount = 0;

            for (int k = 0; k < 4; k++)
            {
                float w = weights[k];
                if (w <= 0.0001f) continue;
                AccumulateLimbWeight(uBones, uWeights, ref uCount, bones[k], w);
            }

            if (uCount == 0) return false;

            bool touched = false;
            bool inCreaseSeam = false;

            foreach (var chain in profile.Chains)
            {
                // 1. Project vertex onto this limb's upper segment (hip->knee or shoulder->elbow)
                Vector3 dUpper = vPos - chain.UpperOrigin;
                float projUpper = dUpper.Dot(chain.UpperDir);
                float tUpper = projUpper / chain.UpperLen;
                float rPerp = (dUpper - chain.UpperDir * projUpper).Length();

                // 2. Gather existing weights belonging to this upper chain, lower bone, and distal bone
                float upperW = 0f;
                float lowerW = 0f;
                float distalW = 0f;
                for (int i = 0; i < uCount; i++)
                {
                    int b = uBones[i];
                    if (b == chain.UpperBone ||
                        (chain.TwistFullBone >= 0 && b == chain.TwistFullBone) ||
                        (chain.TwistHalfBone >= 0 && b == chain.TwistHalfBone))
                    {
                        upperW += uWeights[i];
                    }
                    else if (b == chain.LowerBone)
                    {
                        lowerW += uWeights[i];
                    }
                    else if (chain.DistalBone >= 0 && b == chain.DistalBone)
                    {
                        distalW += uWeights[i];
                    }
                }

                // 3. Prune stray distal ankle/hand bleeds appearing on the thigh, knee, or upper forearm
                if (distalW > 0.0001f && distalW < 0.25f && chain.DistalBone >= 0 && chain.LowerLen > 0.05f && (upperW + lowerW) >= 0.30f)
                {
                    float tLower = (vPos - chain.LowerOrigin).Dot(chain.LowerDir) / chain.LowerLen;
                    if (tLower < 0.55f)
                    {
                        SubtractLimbWeight(uBones, uWeights, ref uCount, chain.DistalBone);
                        int receiver = (tLower < 0.15f && upperW >= lowerW) ? chain.UpperBone : chain.LowerBone;
                        AccumulateLimbWeight(uBones, uWeights, ref uCount, receiver, distalW);
                        if (receiver == chain.UpperBone) upperW += distalW;
                        else lowerW += distalW;
                        touched = true;
                    }
                }

                // Skip twist / hip-seam reallocation if vertex has minimal upper-limb weight or sits outside this limb cylinder
                if (upperW <= 0.02f || tUpper < -0.20f || tUpper > 1.15f || rPerp > chain.MaxRadius)
                    continue;

                // 4. Leg hip-seam pelvis balancing & cross-leg bleed cleanup:
                //    - Cap excess Rigify/Mixamo pelvis bleed down the thigh (which collapses volume in Linear Blend Skinning under deep hip flexion)
                //    - Soften razor-sharp 1-bone hip sockets right at the crease (tUpper in [-0.10, +0.05])
                //    - Prune minor opposite-leg bleed on inner thighs (evaluated AFTER pelvis cap so raised upperW qualifies)
                if (chain.IsLeg)
                {
                    if (CleanupLegPelvisAndCrossBleed(profile, in chain, tUpper, rPerp, uBones, uWeights, ref uCount, ref upperW))
                        touched = true;

                    // 4b. Center knee crease envelope closer to Deadlock's native joint pivot (tUpper ~ 0.98)
                    //     so shin vertices below the knee don't hold heavy upper-thigh weight and fold inward.
                    if (chain.LowerBone >= 0 && tUpper >= 0.90f && tUpper <= 1.12f)
                    {
                        float kneeLowerW = lowerW;
                        float totalKnee = upperW + kneeLowerW;
                        if (kneeLowerW > 0.005f && totalKnee >= 0.65f)
                        {
                            float desiredUpperFrac = 1.0f - SmoothStep01(0.90f, 1.06f, tUpper);
                            float targetU = totalKnee * desiredUpperFrac;
                            float blendedU = upperW * 0.35f + targetU * 0.65f;
                            float blendedL = totalKnee - blendedU;
                            if (MathF.Abs(blendedU - upperW) > 0.01f)
                            {
                                SubtractLimbWeight(uBones, uWeights, ref uCount, chain.LowerBone);
                                if (blendedL > 0.005f)
                                {
                                    AccumulateLimbWeight(uBones, uWeights, ref uCount, chain.LowerBone, blendedL);
                                    upperW = blendedU;
                                    lowerW = blendedL;
                                }
                                else
                                {
                                    upperW = totalKnee;
                                    lowerW = 0f;
                                }
                                touched = true;
                            }
                        }
                    }
                }

                // Flag proximal hip / glute / shoulder / knee crease vertices for targeted triangle-seam feathering
                if (!chain.IsDistalTwist &&
                    ((tUpper >= (chain.IsLeg ? -0.14f : -0.10f) && tUpper <= 0.20f) ||
                     (chain.IsLeg && tUpper >= 0.90f && tUpper <= 1.10f)))
                {
                    inCreaseSeam = true;
                }

                // 5. Redistribute limb weight onto procedural *_TWIST and *_TWIST1 bones
                bool hasTwistFull = chain.TwistFullBone >= 0;
                bool hasTwistHalf = chain.TwistHalfBone >= 0;
                if (!hasTwistFull && !hasTwistHalf)
                    continue;

                SubtractLimbWeight(uBones, uWeights, ref uCount, chain.UpperBone);
                if (hasTwistFull) SubtractLimbWeight(uBones, uWeights, ref uCount, chain.TwistFullBone);
                if (hasTwistHalf) SubtractLimbWeight(uBones, uWeights, ref uCount, chain.TwistHalfBone);

                float fFull, fHalf, fUpper;
                if (chain.IsDistalTwist)
                {
                    // Distal wrist-follow forearm twist (elbow t=0 -> wrist t=1):
                    // 0.00..0.14: ~100% arm_lower (0.0) -> ~0.52: ~95% arm_lower_*_TWIST (+0.5) -> >=0.90: ~100% arm_lower_*_TWIST1 (+1.0)
                    if (hasTwistFull && hasTwistHalf)
                    {
                        fUpper = 1.0f - SmoothStep01(0.14f, 0.52f, tUpper);
                        fFull = SmoothStep01(0.52f, 0.90f, tUpper);
                        fHalf = MathF.Max(0f, 1.0f - fUpper - fFull);
                    }
                    else if (hasTwistHalf)
                    {
                        fHalf = SmoothStep01(0.25f, 0.90f, tUpper) * 0.90f;
                        fFull = 0f;
                        fUpper = 1.0f - fHalf;
                    }
                    else
                    {
                        fFull = SmoothStep01(0.25f, 0.90f, tUpper) * 0.90f;
                        fHalf = 0f;
                        fUpper = 1.0f - fFull;
                    }
                }
                else if (hasTwistFull && hasTwistHalf)
                {
                    // Smooth partition of unity along tUpper in [0, 1] matching Deadlock hero thighs & upper arms:
                    // 0.00..0.14: ~100% TWIST (-1.0) -> ~0.52: ~95% TWIST1 (-0.5) -> >=0.92: 100% UpperBone (0.0)
                    fFull = 1.0f - SmoothStep01(0.14f, 0.52f, tUpper);
                    fUpper = SmoothStep01(0.52f, 0.92f, tUpper);
                    fHalf = MathF.Max(0f, 1.0f - fFull - fUpper);
                }
                else if (hasTwistFull)
                {
                    fFull = (1.0f - SmoothStep01(0.08f, 0.75f, tUpper)) * 0.90f;
                    fHalf = 0f;
                    fUpper = 1.0f - fFull;
                }
                else
                {
                    fFull = 0f;
                    fHalf = (1.0f - SmoothStep01(0.15f, 0.75f, tUpper)) * 0.90f;
                    fUpper = 1.0f - fHalf;
                }

                if (hasTwistFull && fFull * upperW > 0.005f)
                    AccumulateLimbWeight(uBones, uWeights, ref uCount, chain.TwistFullBone, upperW * fFull);
                if (hasTwistHalf && fHalf * upperW > 0.005f)
                    AccumulateLimbWeight(uBones, uWeights, ref uCount, chain.TwistHalfBone, upperW * fHalf);
                if (fUpper * upperW > 0.005f)
                    AccumulateLimbWeight(uBones, uWeights, ref uCount, chain.UpperBone, upperW * fUpper);
                else if ((!hasTwistFull || fFull * upperW <= 0.005f) && (!hasTwistHalf || fHalf * upperW <= 0.005f))
                    AccumulateLimbWeight(uBones, uWeights, ref uCount, chain.UpperBone, upperW);

                touched = true;
            }

            if (touched)
            {
                WriteTop4NormalizedWeights(uBones, uWeights, uCount, bones, weights);
            }

            return inCreaseSeam;
        }

        private static void AccumulateLimbWeight(Span<int> uBones, Span<float> uWeights, ref int uCount, int bone, float weight)
        {
            if (weight <= 0.0001f || bone < 0) return;
            for (int i = 0; i < uCount; i++)
            {
                if (uBones[i] == bone)
                {
                    uWeights[i] += weight;
                    return;
                }
            }
            if (uCount < uBones.Length)
            {
                uBones[uCount] = bone;
                uWeights[uCount] = weight;
                uCount++;
            }
        }

        private static float GetLimbWeight(Span<int> uBones, Span<float> uWeights, int uCount, int bone)
        {
            for (int i = 0; i < uCount; i++)
            {
                if (uBones[i] == bone) return uWeights[i];
            }
            return 0f;
        }

        private static void SubtractLimbWeight(Span<int> uBones, Span<float> uWeights, ref int uCount, int bone)
        {
            for (int i = 0; i < uCount; i++)
            {
                if (uBones[i] == bone)
                {
                    uWeights[i] = 0f;
                    return;
                }
            }
        }

        private static void WriteTop4NormalizedWeights(
            Span<int> uBones,
            Span<float> uWeights,
            int uCount,
            Span<int> outBones,
            Span<float> outWeights)
        {
            for (int k = 0; k < 4; k++)
            {
                int best = -1;
                float maxW = 0f;
                for (int u = 0; u < uCount; u++)
                {
                    if (uWeights[u] > maxW)
                    {
                        maxW = uWeights[u];
                        best = u;
                    }
                }
                if (best != -1 && maxW > 0.0001f)
                {
                    outBones[k] = uBones[best];
                    outWeights[k] = maxW;
                    uWeights[best] = -1f;
                }
                else
                {
                    outBones[k] = 0;
                    outWeights[k] = 0f;
                }
            }

            float keptSum = outWeights[0] + outWeights[1] + outWeights[2] + outWeights[3];
            if (keptSum > 0.0001f)
            {
                float invKept = 1.0f / keptSum;
                for (int k = 0; k < 4; k++)
                {
                    outWeights[k] *= invKept;
                }
            }
            else
            {
                outBones[0] = 0;
                outWeights[0] = 1.0f;
            }
        }

        private static void SmoothSingleVertex(
            int v,
            List<int>[] adj,
            int[] boneIndices,
            float[] boneWeights,
            int[] newIndices,
            float[] newWeights,
            float lambda)
        {
            var neighbors = adj[v];
            if (neighbors.Count == 0)
            {
                for (int k = 0; k < 4; k++)
                {
                    newIndices[v * 4 + k] = boneIndices[v * 4 + k];
                    newWeights[v * 4 + k] = boneWeights[v * 4 + k];
                }
                return;
            }

            Span<int> blendBones = stackalloc int[16];
            Span<float> blendWeights = stackalloc float[16];
            int blendCount = 0;

            float selfWeight = 1.0f - lambda;
            for (int k = 0; k < 4; k++)
            {
                int b = boneIndices[v * 4 + k];
                float w = boneWeights[v * 4 + k];
                if (w > 0.0001f)
                {
                    blendBones[blendCount] = b;
                    blendWeights[blendCount] = w * selfWeight;
                    blendCount++;
                }
            }

            float neighborScale = lambda / neighbors.Count;
            int nCount = neighbors.Count;
            for (int ni = 0; ni < nCount; ni++)
            {
                int n = neighbors[ni];
                for (int k = 0; k < 4; k++)
                {
                    int b = boneIndices[n * 4 + k];
                    float w = boneWeights[n * 4 + k];
                    if (w > 0.0001f)
                    {
                        float add = w * neighborScale;
                        int found = -1;
                        for (int bi = 0; bi < blendCount; bi++)
                        {
                            if (blendBones[bi] == b) { found = bi; break; }
                        }
                        if (found != -1)
                        {
                            blendWeights[found] += add;
                        }
                        else if (blendCount < 16)
                        {
                            blendBones[blendCount] = b;
                            blendWeights[blendCount] = add;
                            blendCount++;
                        }
                    }
                }
            }

            for (int top = 0; top < 4; top++)
            {
                int bestIdx = -1;
                float maxVal = 0f;
                for (int bi = 0; bi < blendCount; bi++)
                {
                    if (blendWeights[bi] > maxVal)
                    {
                        maxVal = blendWeights[bi];
                        bestIdx = bi;
                    }
                }
                if (bestIdx != -1 && maxVal > 0.0001f)
                {
                    newIndices[v * 4 + top] = blendBones[bestIdx];
                    newWeights[v * 4 + top] = maxVal;
                    blendWeights[bestIdx] = -1f;
                }
                else
                {
                    newIndices[v * 4 + top] = 0;
                    newWeights[v * 4 + top] = 0f;
                }
            }

            float sumW = newWeights[v * 4 + 0] + newWeights[v * 4 + 1] + newWeights[v * 4 + 2] + newWeights[v * 4 + 3];
            if (sumW > 0.0001f)
            {
                newWeights[v * 4 + 0] /= sumW;
                newWeights[v * 4 + 1] /= sumW;
                newWeights[v * 4 + 2] /= sumW;
                newWeights[v * 4 + 3] /= sumW;
            }
            else
            {
                newIndices[v * 4 + 0] = 0;
                newWeights[v * 4 + 0] = 1.0f;
            }
        }

        private static void BakeInversePose(
            Vector3[] posedVerts,
            Vector3[] posedNorms,
            int[] boneIndices,
            float[] boneWeights,
            Transform3D[] restGlobal,
            Transform3D[] posedGlobal,
            Vector3[] outRestVerts,
            Vector3[] outRestNorms)
        {
            int vCount = posedVerts.Length;
            int boneCount = Math.Min(restGlobal.Length, posedGlobal.Length);
            Transform3D[] invSkinMatrices = new Transform3D[boneCount];
            for (int b = 0; b < boneCount; b++)
            {
                invSkinMatrices[b] = restGlobal[b] * posedGlobal[b].AffineInverse();
            }

            System.Threading.Tasks.Parallel.For(0, vCount, v =>
            {
                Vector3 pPosed = posedVerts[v];
                Vector3 nPosed = posedNorms[v];

                Vector3 pRest = Vector3.Zero;
                Vector3 nRest = Vector3.Zero;

                for (int k = 0; k < 4; k++)
                {
                    float w = boneWeights[v * 4 + k];
                    if (w <= 0.0001f) continue;

                    int b = boneIndices[v * 4 + k];
                    if (b >= 0 && b < boneCount)
                    {
                        Transform3D invSkinMatrix = invSkinMatrices[b];
                        pRest += w * (invSkinMatrix * pPosed);
                        nRest += w * (invSkinMatrix.Basis * nPosed);
                    }
                    else
                    {
                        pRest += w * pPosed;
                        nRest += w * nPosed;
                    }
                }

                outRestVerts[v] = pRest;
                outRestNorms[v] = nRest.Normalized();
            });
        }

        public static (Dictionary<int, int> skelToBind, Dictionary<int, int> bindToSkel) BuildSkinIndexMappings(Skeleton3D skeleton, Skin skin)
        {
            var skelToBind = new Dictionary<int, int>();
            var bindToSkel = new Dictionary<int, int>();
            if (skeleton == null) return (skelToBind, bindToSkel);

            int boneCount = skeleton.GetBoneCount();

            if (skin != null)
            {
                for (int b = 0; b < boneCount; b++)
                {
                    int bindIdx = GetOrAddSkinBindIndex(skin, skeleton, b);
                    skelToBind[b] = bindIdx;
                    bindToSkel[bindIdx] = b;
                }
            }
            else
            {
                for (int b = 0; b < boneCount; b++)
                {
                    skelToBind[b] = b;
                    bindToSkel[b] = b;
                }
            }

            return (skelToBind, bindToSkel);
        }

        public static Dictionary<int, int> BuildHumanoidBoneMap(
            Skeleton3D sourceSkeleton,
            Skeleton3D targetSkeleton,
            Dictionary<string, string> userCustomRemap = null,
            RigAnalysis classifierAnalysis = null,
            HashSet<string> disabledSourceBones = null)
        {
            var map = new Dictionary<int, int>();
            var usedTargets = new HashSet<int>();

            bool IsDisabled(int sIdx)
            {
                if (disabledSourceBones == null || disabledSourceBones.Count == 0) return false;
                string name = sourceSkeleton.GetBoneName(sIdx);
                return disabledSourceBones.Contains(name);
            }

            // 1. Apply user custom overrides if supplied (authoritative: wins even over classifier/control tags)
            if (userCustomRemap != null && userCustomRemap.Count > 0)
            {
                foreach (var (tName, sName) in userCustomRemap)
                {
                    if (string.IsNullOrWhiteSpace(sName) || string.IsNullOrWhiteSpace(tName)) continue;
                    int tIdx = targetSkeleton.FindBone(tName);
                    if (tIdx == -1)
                    {
                        var def = HumanoidBoneMapper.Definitions.FirstOrDefault(d =>
                            d.CanonicalName.Equals(tName, StringComparison.OrdinalIgnoreCase));
                        if (def != null)
                        {
                            string resolvedTarget = HumanoidBoneMapper.ResolveTargetBone(targetSkeleton, def);
                            if (!string.IsNullOrEmpty(resolvedTarget))
                            {
                                tIdx = targetSkeleton.FindBone(resolvedTarget);
                            }
                        }
                    }
                    int sIdx = sourceSkeleton.FindBone(sName);
                    if (tIdx != -1 && sIdx != -1 && !IsDisabled(sIdx) && !map.ContainsKey(sIdx))
                    {
                        map[sIdx] = tIdx;
                        usedTargets.Add(tIdx);
                    }
                }
            }

            // 1b. Exact 1:1 canonical mapping for LandmarkSyntheticSkeleton (built by ReferenceRigDef with authentic Deadlock
            //     bone names) BEFORE heuristic HumanoidBoneMapper.AutoMap / AdjustThreeSpineToFourSpine / RigBoneClassifier,
            //     so removing spine_0 or reducing SpineCount is respected verbatim instead of remapping back onto spine_0!
            bool isSyntheticDeadlockRig = string.Equals(sourceSkeleton.Name.ToString(), "LandmarkSyntheticSkeleton", StringComparison.Ordinal);
            if (isSyntheticDeadlockRig && (userCustomRemap == null || userCustomRemap.Count == 0))
            {
                // Never hijack user-authored ReferenceRig cloth bones onto vanilla hero bones that happen to share
                // a name (e.g. cape_0, hair_0, coat_0, skirt_0 on Haze/Wraith/Vindicta/Kelvin).
                HashSet<string> authoredClothBones = null;
                if (sourceSkeleton.HasMeta("AuthoredClothBoneNames"))
                {
                    var metaArr = sourceSkeleton.GetMeta("AuthoredClothBoneNames").AsGodotArray();
                    if (metaArr != null && metaArr.Count > 0)
                    {
                        authoredClothBones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var item in metaArr)
                        {
                            string bn = item.AsString();
                            if (!string.IsNullOrEmpty(bn)) authoredClothBones.Add(bn);
                        }
                    }
                }

                int sTotal = sourceSkeleton.GetBoneCount();
                for (int sIdx = 0; sIdx < sTotal; sIdx++)
                {
                    if (IsDisabled(sIdx) || map.ContainsKey(sIdx)) continue;
                    string sName = sourceSkeleton.GetBoneName(sIdx);
                    if (authoredClothBones != null && authoredClothBones.Contains(sName)) continue;
                    int tIdx = FindBoneAny(targetSkeleton, sName, sName.ToLowerInvariant());
                    if (tIdx == -1 && sName.Equals("neck_0", StringComparison.OrdinalIgnoreCase))
                        tIdx = FindBoneAny(targetSkeleton, "neck", "neck_01");
                    if (tIdx == -1 && sName.StartsWith("toe_", StringComparison.OrdinalIgnoreCase))
                    {
                        string ballName = "ball_" + sName[4..];
                        tIdx = FindBoneAny(targetSkeleton, ballName, ballName.ToLowerInvariant());
                    }
                    if (tIdx != -1 && !usedTargets.Contains(tIdx))
                    {
                        map[sIdx] = tIdx;
                        usedTargets.Add(tIdx);
                    }
                }
                return map;
            }

            // 2. Fallback via HumanoidBoneMapper.AutoMap for any unmapped targets ONLY when no explicit user map was supplied,
            //    or for unmapped source bones matching unused secondary/twist slots cleanly.
            if (userCustomRemap == null || userCustomRemap.Count == 0)
            {
                var autoMapped = HumanoidBoneMapper.AutoMap(sourceSkeleton, targetSkeleton);
                foreach (var (tName, sName) in autoMapped)
                {
                    if (string.IsNullOrWhiteSpace(sName)) continue;
                    int tIdx = targetSkeleton.FindBone(tName);
                    int sIdx = sourceSkeleton.FindBone(sName);
                    if (tIdx != -1 && sIdx != -1 && !IsDisabled(sIdx) && !map.ContainsKey(sIdx) && !usedTargets.Contains(tIdx))
                    {
                        map[sIdx] = tIdx;
                        usedTargets.Add(tIdx);
                    }
                }
            }

            // 2b. RigBoneClassifier canonical suggestions for any still-unmapped non-cleared slots (handles ARP root.x, VRM, UE, etc.)
            if (classifierAnalysis != null && classifierAnalysis.CanonicalToSource.Count > 0)
            {
                foreach (var (canonical, sIdx) in classifierAnalysis.CanonicalToSource)
                {
                    if (sIdx < 0 || sIdx >= sourceSkeleton.GetBoneCount()) continue;
                    if (IsDisabled(sIdx) || map.ContainsKey(sIdx)) continue;
                    if (userCustomRemap != null && userCustomRemap.ContainsKey(canonical)) continue;
                    int tIdx = targetSkeleton.FindBone(canonical);
                    if (tIdx == -1)
                    {
                        var def = HumanoidBoneMapper.Definitions.FirstOrDefault(d => d.CanonicalName.Equals(canonical, StringComparison.OrdinalIgnoreCase));
                        if (def != null)
                        {
                            string resolved = HumanoidBoneMapper.ResolveTargetBone(targetSkeleton, def);
                            if (!string.IsNullOrEmpty(resolved)) tIdx = targetSkeleton.FindBone(resolved);
                        }
                    }
                    if (tIdx != -1 && !usedTargets.Contains(tIdx))
                    {
                        map[sIdx] = tIdx;
                        usedTargets.Add(tIdx);
                    }
                }
            }

            int srcCount = sourceSkeleton.GetBoneCount();
            string[] srcRawNames = new string[srcCount];
            string[] srcCleanExact = new string[srcCount];
            string[] srcCleanNoGltf = new string[srcCount];
            bool[] srcIsSecondary = new bool[srcCount];
            for (int sIdx = 0; sIdx < srcCount; sIdx++)
            {
                string raw = sourceSkeleton.GetBoneName(sIdx);
                srcRawNames[sIdx] = raw;
                srcCleanExact[sIdx] = HumanoidBoneMapper.CleanBoneName(raw, stripTrailingGltfIndex: false);
                srcCleanNoGltf[sIdx] = HumanoidBoneMapper.CleanBoneName(raw, stripTrailingGltfIndex: true);
                srcIsSecondary[sIdx] = HumanoidBoneMapper.IsSecondaryOrHelperBone(raw, allowTwist: false);
            }

            // Precompute definition target bone indices and cleaned aliases ONCE (eliminates O(B * D) C++ FindBone lookups)
            var defs = HumanoidBoneMapper.Definitions;
            int[] defTargetIndices = new int[defs.Count];
            string[][] defCleanAliases = new string[defs.Count][];
            string[] defCanonicalLower = new string[defs.Count];
            bool[] defRespectClear = new bool[defs.Count];
            for (int d = 0; d < defs.Count; d++)
            {
                var def = defs[d];
                defRespectClear[d] = userCustomRemap != null && userCustomRemap.ContainsKey(def.CanonicalName);
                string tgtResolved = HumanoidBoneMapper.ResolveTargetBone(targetSkeleton, def);
                defTargetIndices[d] = !string.IsNullOrEmpty(tgtResolved) ? targetSkeleton.FindBone(tgtResolved) : -1;
                defCanonicalLower[d] = def.CanonicalName.ToLowerInvariant();
                if (defTargetIndices[d] != -1 && !defRespectClear[d])
                {
                    var aliases = new string[def.SourceAliases.Length];
                    for (int a = 0; a < aliases.Length; a++)
                        aliases[a] = HumanoidBoneMapper.CleanBoneName(def.SourceAliases[a], stripTrailingGltfIndex: false);
                    defCleanAliases[d] = aliases;
                }
            }

            for (int sIdx = 0; sIdx < srcCount; sIdx++)
            {
                if (map.ContainsKey(sIdx) || IsDisabled(sIdx)) continue;
                if (classifierAnalysis != null && classifierAnalysis.IsControlOrHelper(sIdx)) continue;
                if (srcIsSecondary[sIdx]) continue;
                string rawName = srcRawNames[sIdx];

                // Check exact name match first (when target bone is not already taken)
                int exact = targetSkeleton.FindBone(rawName);
                if (exact != -1 && !usedTargets.Contains(exact))
                {
                    map[sIdx] = exact;
                    usedTargets.Add(exact);
                    continue;
                }

                // Match against HumanoidBoneMapper definitions using precomputed cleaned names (never colliding used targets)
                string cleanExact = srcCleanExact[sIdx];
                string cleanNoGltf = srcCleanNoGltf[sIdx];
                bool hasLateralTag = cleanNoGltf.EndsWith("_l") || cleanNoGltf.EndsWith("_r") || cleanNoGltf.Contains("_l_") || cleanNoGltf.Contains("_r_") || cleanNoGltf.Contains("left") || cleanNoGltf.Contains("right");
                bool disallowRight = (cleanNoGltf.EndsWith("_r") || cleanNoGltf.Contains("_r_") || cleanNoGltf.Contains("right")) && !cleanNoGltf.Contains("left");
                bool disallowLeft = (cleanNoGltf.EndsWith("_l") || cleanNoGltf.Contains("_l_") || cleanNoGltf.Contains("left")) && !cleanNoGltf.Contains("right");
                bool hasForehead = cleanNoGltf.Contains("forehead");

                for (int d = 0; d < defs.Count; d++)
                {
                    int tIdx = defTargetIndices[d];
                    if (tIdx == -1 || usedTargets.Contains(tIdx) || defRespectClear[d]) continue;
                    var def = defs[d];

                    bool isCentral = def.Group == HumanoidBoneMapper.BoneGroup.Pelvis || def.Group == HumanoidBoneMapper.BoneGroup.Spine || def.Group == HumanoidBoneMapper.BoneGroup.Head || def.Group == HumanoidBoneMapper.BoneGroup.Root;
                    if (isCentral && hasLateralTag) continue;
                    if (def.CanonicalName == "head" && hasForehead) continue;

                    bool isLeft = def.Group == HumanoidBoneMapper.BoneGroup.LeftArm || def.Group == HumanoidBoneMapper.BoneGroup.LeftLeg || def.Group == HumanoidBoneMapper.BoneGroup.LeftHandFingers;
                    bool isRight = def.Group == HumanoidBoneMapper.BoneGroup.RightArm || def.Group == HumanoidBoneMapper.BoneGroup.RightLeg || def.Group == HumanoidBoneMapper.BoneGroup.RightHandFingers;
                    if (isLeft && disallowRight) continue;
                    if (isRight && disallowLeft) continue;

                    // Anatomical hand-vs-foot isolation: articulated toes (e.g. C_TOES_INDEX2.R) must never match fingers/arms,
                    // and hand/finger bones must never match foot/toe/leg slots.
                    bool isUpperLimbOrFinger = def.Group == HumanoidBoneMapper.BoneGroup.LeftHandFingers || def.Group == HumanoidBoneMapper.BoneGroup.RightHandFingers ||
                                               def.Group == HumanoidBoneMapper.BoneGroup.LeftArm || def.Group == HumanoidBoneMapper.BoneGroup.RightArm;
                    bool isLowerLimb = def.Group == HumanoidBoneMapper.BoneGroup.LeftLeg || def.Group == HumanoidBoneMapper.BoneGroup.RightLeg;
                    if (isUpperLimbOrFinger && (cleanNoGltf.Contains("toe") || cleanNoGltf.Contains("foot") || cleanNoGltf.Contains("ankle") || cleanNoGltf.Contains("heel") || cleanNoGltf.Contains("sole") || cleanNoGltf.Contains("ball")))
                        continue;
                    if (isLowerLimb && (cleanNoGltf.Contains("hand") || cleanNoGltf.Contains("wrist") || cleanNoGltf.Contains("finger") || cleanNoGltf.Contains("palm") || cleanNoGltf.Contains("arm")))
                        continue;

                    bool sourceMatch = cleanNoGltf == defCanonicalLower[d];
                    if (!sourceMatch)
                    {
                        string[] cAliases = defCleanAliases[d];
                        for (int a = 0; a < cAliases.Length; a++)
                        {
                            string cAlias = cAliases[a];
                            if (cleanNoGltf.EndsWith("_end") && !cAlias.EndsWith("_end")) continue;
                            if (cleanExact == cAlias || cleanNoGltf == cAlias ||
                                cleanNoGltf.StartsWith(cAlias + "_") || cleanNoGltf.EndsWith("_" + cAlias) || cleanNoGltf.Contains("_" + cAlias + "_"))
                            {
                                sourceMatch = true;
                                break;
                            }
                        }
                    }

                    if (sourceMatch)
                    {
                        map[sIdx] = tIdx;
                        usedTargets.Add(tIdx);
                        break;
                    }
                }
            }

            // Check for Deadlock Forearm Twist Bones (arm_lower_L_TWIST, arm_lower_R_TWIST, etc.)
            for (int sIdx = 0; sIdx < srcCount; sIdx++)
            {
                if (map.ContainsKey(sIdx)) continue;
                string sName = srcRawNames[sIdx];
                string clean = srcCleanNoGltf[sIdx];

                if (clean.Contains("forearm") && (clean.EndsWith("_001") || clean.EndsWith(".001") || clean.Contains("twist")))
                {
                    bool isLeft = clean.EndsWith("_l") || clean.Contains("_l_") || clean.Contains("left") || sName.ToLowerInvariant().Contains(".l");
                    int tgtTwist = FindBoneAny(targetSkeleton,
                        isLeft ? "arm_lower_L_TWIST" : "arm_lower_R_TWIST",
                        isLeft ? "arm_lower_twist_L" : "arm_lower_twist_R",
                        isLeft ? "arm_lower_l_twist" : "arm_lower_r_twist");
                    if (tgtTwist == -1)
                    {
                        tgtTwist = EnsureForearmTwistBone(targetSkeleton, isLeft);
                    }
                    if (tgtTwist != -1)
                    {
                        map[sIdx] = tgtTwist;
                    }
                }
            }

            // Collapse unmapped .001 twist bones into their mapped parent limb bone
            for (int sIdx = 0; sIdx < srcCount; sIdx++)
            {
                if (map.ContainsKey(sIdx)) continue;
                string clean = srcCleanNoGltf[sIdx];

                if (clean.EndsWith("_001") || clean.EndsWith(".001") || clean.Contains("twist"))
                {
                    string baseName = clean.Replace("_001", "").Replace(".001", "").Replace("twist", "");
                    for (int otherIdx = 0; otherIdx < srcCount; otherIdx++)
                    {
                        if (map.TryGetValue(otherIdx, out int tgtMapped))
                        {
                            string otherClean = srcCleanNoGltf[otherIdx];
                            if (otherClean == baseName || otherClean.StartsWith(baseName + "_") || baseName.StartsWith(otherClean))
                            {
                                map[sIdx] = tgtMapped;
                                break;
                            }
                        }
                    }
                }
            }

            return map;
        }

        private static int ResolveTargetBoneWithAncestorFallback(
            int sourceBone,
            Skeleton3D sourceSkeleton,
            Dictionary<int, int> sourceToTargetMap,
            Skeleton3D targetSkeleton,
            bool keepFingersRigid = false,
            HashSet<int> disabledIndices = null)
        {
            string srcNameRaw = sourceSkeleton.GetBoneName(sourceBone).ToLowerInvariant();
            bool isDisabled = disabledIndices != null && disabledIndices.Contains(sourceBone);

            // Palm bones (e.g. Rigify DEF-palm.01.L..04.L) always collapse into hand_L / hand_R
            if (!isDisabled && srcNameRaw.Contains("palm"))
            {
                bool isLeft = srcNameRaw.EndsWith(".l") || srcNameRaw.EndsWith("_l") || srcNameRaw.Contains("left");
                int handIdx = FindBoneAny(targetSkeleton, isLeft ? "hand_L" : "hand_R");
                if (handIdx != -1) return handIdx;
            }

            int resolved = -1;
            if (!isDisabled && sourceToTargetMap.TryGetValue(sourceBone, out int tgt))
            {
                resolved = tgt;
            }
            else
            {
                // Climb up the source hierarchy to find mapped ancestor
                int cur = sourceBone;
                while (cur != -1)
                {
                    cur = sourceSkeleton.GetBoneParent(cur);
                    if (cur != -1 && (disabledIndices == null || !disabledIndices.Contains(cur)) &&
                        sourceToTargetMap.TryGetValue(cur, out int mappedAncestor))
                    {
                        resolved = mappedAncestor;
                        break;
                    }
                }
            }

            if (resolved == -1)
            {
                int pelvis = targetSkeleton.FindBone("pelvis");
                resolved = pelvis != -1 ? pelvis : 0;
            }

            if (keepFingersRigid)
            {
                string tgtName = targetSkeleton.GetBoneName(resolved).ToLowerInvariant();
                string srcName = sourceSkeleton.GetBoneName(sourceBone).ToLowerInvariant();
                if (tgtName.StartsWith("finger_") || tgtName.Contains("thumb") || tgtName.Contains("index") ||
                    tgtName.Contains("middle") || tgtName.Contains("ring") || tgtName.Contains("pinky") ||
                    srcName.Contains("finger") || srcName.Contains("thumb") || srcName.Contains("index") ||
                    srcName.Contains("middle") || srcName.Contains("ring") || srcName.Contains("pinky") ||
                    srcName.Contains("f_"))
                {
                    bool isLeft = tgtName.EndsWith("_l") || tgtName.Contains("_l_") || srcName.EndsWith(".l") || srcName.EndsWith("_l") || srcName.Contains("left");
                    int handIdx = targetSkeleton.FindBone(isLeft ? "hand_L" : "hand_R");
                    if (handIdx != -1) return handIdx;
                }
            }

            return resolved;
        }

        public const string SurfaceKeepSeparator = "::surf::";

        public static string MakeSurfaceKeepKey(string meshName, int surfaceIndex)
            => $"{meshName}{SurfaceKeepSeparator}{surfaceIndex}";

        public static bool IsOriginalSurfaceKept(IEnumerable<string> keptNames, string meshName, int surfaceIndex, string generatedSubName = null)
        {
            if (keptNames == null) return false;
            string surfKey = MakeSurfaceKeepKey(meshName, surfaceIndex);
            foreach (var entry in keptNames)
            {
                if (string.IsNullOrEmpty(entry)) continue;
                if (string.Equals(entry, meshName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry, surfKey, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(generatedSubName) && string.Equals(entry, generatedSubName, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            return false;
        }

        public static StandardMaterial3D CreateHiddenSurfaceMaterial()
        {
            return new StandardMaterial3D
            {
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = new Color(0f, 0f, 0f, 0f),
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                NoDepthTest = false,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
            };
        }

        public static string GetContainerNameForSlot(string slotId)
            => string.IsNullOrWhiteSpace(slotId) ? "Custom_Rigged_Character" : $"Custom_Accessory_{slotId}";

        public static List<Node3D> CollectCustomContainers(Node3D heroNode)
        {
            var list = new List<Node3D>();
            if (heroNode == null || !GodotObject.IsInstanceValid(heroNode)) return list;
            foreach (Node n in heroNode.FindChildren("*", "Node3D", recursive: true, owned: false))
            {
                if (n is not Node3D n3d) continue;
                string name = n3d.Name.ToString();
                if (name == "Custom_Rigged_Character" ||
                    name == "Custom_Rigid_Accessory" ||
                    name.StartsWith("Custom_Accessory_", StringComparison.Ordinal))
                {
                    list.Add(n3d);
                }
            }
            return list;
        }

        /// <summary>
        /// Computes an aligned Transform3D in hero space that normalizes an accessory's imported bounds
        /// and snaps its pivot directly onto a target skeleton bone in rest pose.
        /// </summary>
        public static Transform3D ComputeAutoFitTransformToBone(Node3D importedScene, Skeleton3D heroSkeleton, string boneName, HashSet<string> includedMeshNames = null)
        {
            if (importedScene == null || heroSkeleton == null) return Transform3D.Identity;

            int bIdx = !string.IsNullOrWhiteSpace(boneName) ? heroSkeleton.FindBone(boneName) : -1;
            if (bIdx < 0) bIdx = FindBoneAny(heroSkeleton, "head", "spine_3", "pelvis", "weapon_hand_R", "hand_R");
            if (bIdx < 0) bIdx = 0;

            var (minV, maxV, anyVert) = ComputePristineModelBounds(importedScene, includedMeshNames);
            if (!anyVert) return Transform3D.Identity;

            Vector3 size = maxV - minV;
            float maxDim = Mathf.Max(size.X, Mathf.Max(size.Y, size.Z));
            float scale = 1.0f;
            if (maxDim > 0.0001f)
            {
                string lowerBone = boneName?.ToLowerInvariant() ?? "";
                float desiredMax = lowerBone.Contains("head") ? 0.30f
                                 : (lowerBone.Contains("hand") || lowerBone.Contains("weapon")) ? 0.35f
                                 : (lowerBone.Contains("ankle") || lowerBone.Contains("foot") || lowerBone.Contains("ball") || lowerBone.Contains("toe")) ? 0.28f
                                 : (lowerBone.Contains("leg_lower") || lowerBone.Contains("arm_lower")) ? 0.32f
                                 : (lowerBone.Contains("spine") || lowerBone.Contains("pelvis")) ? 0.48f
                                 : 0.34f;
                if (maxDim > desiredMax * 1.8f || maxDim < desiredMax * 0.25f)
                {
                    scale = desiredMax / maxDim;
                }
            }

            string bLow = boneName?.ToLowerInvariant() ?? "";
            Vector3 pivotLocal = bLow.Contains("head")
                ? new Vector3((minV.X + maxV.X) * 0.5f, minV.Y + size.Y * 0.25f, (minV.Z + maxV.Z) * 0.5f)
                : (minV + maxV) * 0.5f;

            Transform3D boneRest = heroSkeleton.GetBoneGlobalRest(bIdx);
            Vector3 boneLocalOrigin = heroSkeleton.Transform * boneRest.Origin;
            if (bLow.Contains("head"))
            {
                boneLocalOrigin += new Vector3(0f, 0.14f, 0f);
            }

            Vector3 origin = boneLocalOrigin - (pivotLocal * scale);
            return new Transform3D(Basis.Identity.Scaled(new Vector3(scale, scale, scale)), origin);
        }

        /// <summary>
        /// Swaps a symmetrical bone or submesh identifier between Left and Right sides (e.g. ankle_L <-> ankle_R).
        /// Returns null when the identifier is midline or has no symmetric counterpart.
        /// </summary>
        public static string MirrorBoneNameLtoR(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (name.EndsWith("_L", StringComparison.Ordinal)) return name[..^2] + "_R";
            if (name.EndsWith("_R", StringComparison.Ordinal)) return name[..^2] + "_L";
            if (name.EndsWith(".L", StringComparison.Ordinal)) return name[..^2] + ".R";
            if (name.EndsWith(".R", StringComparison.Ordinal)) return name[..^2] + ".L";
            if (name.Contains("_L_", StringComparison.Ordinal)) return name.Replace("_L_", "_R_");
            if (name.Contains("_R_", StringComparison.Ordinal)) return name.Replace("_R_", "_L_");
            if (name.Contains("Left", StringComparison.Ordinal)) return name.Replace("Left", "Right");
            if (name.Contains("Right", StringComparison.Ordinal)) return name.Replace("Right", "Left");
            if (name.Contains("left", StringComparison.Ordinal)) return name.Replace("left", "right");
            if (name.Contains("right", StringComparison.Ordinal)) return name.Replace("right", "left");
            return null;
        }

    }
}
