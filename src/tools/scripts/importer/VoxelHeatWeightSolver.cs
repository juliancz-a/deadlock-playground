using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DeadlockPlayground.Importer
{
    /// <summary>
    /// Two-level sparse 3D Voxel Heat Diffusion skin weight solver (Baranâ€“PopoviÄ‡ / Auto-Rig Proâ€“style).
    /// <list type="bullet">
    ///   <item><b>Pass 1 (Body Volume Grid, ~96 voxels along longest axis)</b>: Conservatively voxelizes all active
    ///   mesh surfaces, performs exterior air BFS flood-fill (robust on open/non-watertight game meshes), seeds
    ///   bone cores with 3D Amanatidesâ€“Woo DDA interior visibility, solves an independent SPD heat equilibrium PDE
    ///   per bone via parallel Red-Black SOR (<c>Ï‰ = 1.65</c>), propagates weights onto detached floating shells
    ///   (holsters, straps, armor) via multi-source 3D BFS, and trilinearly samples onto all vertices.</item>
    ///   <item><b>Pass 2 (Local High-Res Hand Grids, ~64 voxels / ~2.5mm per hand)</b>: When articulated fingers are
    ///   enabled, runs the same sparse volumetric heat diffusion at ~2.5mm resolution per hand so empty air voxels
    ///   strictly separate adjacent fingers while the palm diffuses smoothly across <c>hand_*</c> and metacarpals,
    ///   then sub-partitions Pass 1's <c>hand_L/R</c> weight seamlessly.</item>
    /// </list>
    /// </summary>
    public static class VoxelHeatWeightSolver
    {
        private const byte CellEmpty = 0;
        private const byte CellSurface = 1;
        private const byte CellExteriorAir = 2;

        private struct SolverBone
        {
            public int OriginalSpecIndex;
            public string Name;
            public Vector3 SegStart;
            public Vector3 SegEnd;
            public bool AllowBelow;
            public bool AllowBeyond;
            public bool IsAuxiliaryBoundary; // e.g. proximal forearm anchor in local hand grid
        }

        private sealed class VoxelVolume
        {
            public int Nx, Ny, Nz;
            public Vector3 Origin;
            public float VoxelSize;
            public float InvVoxelSize;
            public byte[] State;           // Nx*Ny*Nz: CellEmpty, CellSurface, CellExteriorAir
            public int[] GridToActive;     // Nx*Ny*Nz: -1 if air, 0..ActiveCount-1 if solid
            public int[] ActiveToGrid;     // ActiveCount: flat grid index
            public int[] ActiveNeighbors;  // ActiveCount * 6: -1 if air/OOB, else active neighbor index
            public byte[] ActiveParity;    // ActiveCount: (x+y+z) & 1 for Red-Black SOR
            public int ActiveCount;

            public int Flat(int x, int y, int z) => (z * Ny + y) * Nx + x;
            public Vector3 CellCenter(int x, int y, int z) => new Vector3(
                Origin.X + (x + 0.5f) * VoxelSize,
                Origin.Y + (y + 0.5f) * VoxelSize,
                Origin.Z + (z + 0.5f) * VoxelSize);
        }

        /// <summary>
        /// Computes smooth volumetric 4-influence skin weights for every vertex across all surfaces in hero/aligned space.
        /// Returns false only on degenerate 0-volume geometry so the caller can fall back safely.
        /// </summary>
        public static bool SolveWeights(
            List<Vector3[]> alignedVerts,
            List<int[]> indices,
            List<ReferenceRigDef.ResolvedBoneSpec> specs,
            float H,
            float? chinPlaneY,
            out List<int[]> outBones,
            out List<float[]> outWeights,
            List<string> surfaceMeshNames = null)
        {
            outBones = null;
            outWeights = null;
            if (alignedVerts == null || alignedVerts.Count == 0 || specs == null || specs.Count == 0)
                return false;

            int nb = specs.Count;

            // 1. Weld coincident vertices (1mm) so UV/material seams get 100% identical weights and smooth topology
            float weldCell = 0.001f;
            var weldMap = new Dictionary<(long, long, long), int>();
            var weldPos = new List<Vector3>(8192);
            var weldMeshNames = new List<HashSet<string>>(8192);
            var vertToWeld = new List<int[]>(alignedVerts.Count);
            Vector3 bMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 bMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (int s = 0; s < alignedVerts.Count; s++)
            {
                var v = alignedVerts[s];
                string meshName = (surfaceMeshNames != null && s < surfaceMeshNames.Count) ? (surfaceMeshNames[s] ?? "") : "";
                var map = new int[v.Length];
                for (int i = 0; i < v.Length; i++)
                {
                    Vector3 p = v[i];
                    if (p.X < bMin.X) bMin.X = p.X; if (p.Y < bMin.Y) bMin.Y = p.Y; if (p.Z < bMin.Z) bMin.Z = p.Z;
                    if (p.X > bMax.X) bMax.X = p.X; if (p.Y > bMax.Y) bMax.Y = p.Y; if (p.Z > bMax.Z) bMax.Z = p.Z;

                    var key = ((long)MathF.Round(p.X / weldCell), (long)MathF.Round(p.Y / weldCell), (long)MathF.Round(p.Z / weldCell));
                    if (!weldMap.TryGetValue(key, out int id))
                    {
                        id = weldPos.Count;
                        weldMap[key] = id;
                        weldPos.Add(p);
                        weldMeshNames.Add(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    }
                    if (!string.IsNullOrEmpty(meshName)) weldMeshNames[id].Add(meshName);
                    map[i] = id;
                }
                vertToWeld.Add(map);
            }

            int n = weldPos.Count;
            Vector3 ext = bMax - bMin;
            float maxExtent = MathF.Max(ext.X, MathF.Max(ext.Y, ext.Z));
            if (n == 0 || maxExtent < 1e-4f) return false;

            // Gather triangles in weld-index space for fast voxel rasterization and 1 light post-smooth pass
            var triVerts = new List<(Vector3 A, Vector3 B, Vector3 C)>(16384);
            var adj = new List<int>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<int>(6);
            var seenEdges = new HashSet<long>();
            void AddEdge(int a, int b)
            {
                if (a == b) return;
                long k = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (!seenEdges.Add(k)) return;
                adj[a].Add(b);
                adj[b].Add(a);
            }
            for (int s = 0; s < indices.Count; s++)
            {
                var idx = indices[s];
                var map = vertToWeld[s];
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int a = map[idx[t]], b = map[idx[t + 1]], c = map[idx[t + 2]];
                    if (a == b || b == c || c == a) continue;
                    triVerts.Add((weldPos[a], weldPos[b], weldPos[c]));
                    AddEdge(a, b); AddEdge(b, c); AddEdge(c, a);
                }
            }
            if (triVerts.Count == 0) return false;

            // Identify articulated fingers per side (L/R) so Pass 1 solves Body (Core + Cloth) cleanly with full-hand capsules,
            // and Pass 2 refines each hand at ~2.5mm resolution!
            var fingerSpecIndicesL = new List<int>();
            var fingerSpecIndicesR = new List<int>();
            int idxHandL = -1, idxHandR = -1, idxArmLowerL = -1, idxArmLowerR = -1;
            for (int b = 0; b < nb; b++)
            {
                string bn = specs[b].Name;
                if (bn.Equals("hand_L", StringComparison.OrdinalIgnoreCase)) idxHandL = b;
                else if (bn.Equals("hand_R", StringComparison.OrdinalIgnoreCase)) idxHandR = b;
                else if (bn.Equals("arm_lower_L", StringComparison.OrdinalIgnoreCase)) idxArmLowerL = b;
                else if (bn.Equals("arm_lower_R", StringComparison.OrdinalIgnoreCase)) idxArmLowerR = b;
                else if (specs[b].Role == ReferenceBoneRole.Finger)
                {
                    if (bn.EndsWith("_L", StringComparison.OrdinalIgnoreCase)) fingerSpecIndicesL.Add(b);
                    else if (bn.EndsWith("_R", StringComparison.OrdinalIgnoreCase)) fingerSpecIndicesR.Add(b);
                }
            }

            // Build Pass 1 (Body) bone list: strictly Core bones ONLY (Cloth chains are solved in dedicated Pass 3
            // with island + geodesic + radius isolation so hair/cape bones NEVER bleed into face, ear, or hoodie!).
            // Extend hand_L/R segment to distal fingertip average when articulated fingers exist so the entire hand
            // volume receives 100% hand_L/R heat in Pass 1 before Pass 2 sub-partitions it.
            var bodyBones = new List<SolverBone>(nb);
            bool hasClothBones = false;
            for (int b = 0; b < nb; b++)
            {
                if (specs[b].Role == ReferenceBoneRole.Finger) continue;
                if (specs[b].Role == ReferenceBoneRole.Cloth) { hasClothBones = true; continue; }
                Vector3 segA = specs[b].SegStart;
                Vector3 segB = specs[b].SegEnd;
                bool allowBeyond = specs[b].AllowBeyond;

                if (b == idxHandL && fingerSpecIndicesL.Count > 0)
                {
                    segB = ComputeFarthestFingerEnd(specs, fingerSpecIndicesL, segA, segB);
                    allowBeyond = true;
                }
                else if (b == idxHandR && fingerSpecIndicesR.Count > 0)
                {
                    segB = ComputeFarthestFingerEnd(specs, fingerSpecIndicesR, segA, segB);
                    allowBeyond = true;
                }

                bodyBones.Add(new SolverBone
                {
                    OriginalSpecIndex = b,
                    Name = specs[b].Name,
                    SegStart = segA,
                    SegEnd = segB,
                    AllowBelow = specs[b].AllowBelow,
                    AllowBeyond = allowBeyond,
                    IsAuxiliaryBoundary = false
                });
            }

            // Include bone endpoints in body AABB so no bone ever falls outside the grid
            foreach (var bb in bodyBones)
            {
                ExpandBounds(ref bMin, ref bMax, bb.SegStart);
                ExpandBounds(ref bMin, ref bMax, bb.SegEnd);
            }

            // =====================================================================
            // PASS 1: BODY VOLUME HEAT DIFFUSION (longest axis = 96 voxels)
            // =====================================================================
            var bodyVol = BuildVoxelVolume(triVerts, bMin, bMax, longestAxisResolution: 96, padCells: 2);
            if (bodyVol == null || bodyVol.ActiveCount == 0)
                return false;

            float[][] bodyHeat = SolveVoxelHeatPde(bodyVol, bodyBones, sorIterations: 64, omega: 1.65f, robinAlpha: 2.8f, out int[] bodyNearestSolved);

            var dense = new float[n * nb];
            SampleHeatOntoVertices(bodyVol, bodyBones, bodyHeat, bodyNearestSolved, weldPos, dense, nb);

            // =====================================================================
            // PASS 2: LOCAL HIGH-RES HAND HEAT DIFFUSION (~64 voxels / ~2.5mm)
            // =====================================================================
            if (idxHandL >= 0 && fingerSpecIndicesL.Count > 0)
            {
                RefineHandWithLocalVoxelHeat(
                    triVerts, weldPos, specs, idxHandL, idxArmLowerL, fingerSpecIndicesL, dense, nb);
            }
            if (idxHandR >= 0 && fingerSpecIndicesR.Count > 0)
            {
                RefineHandWithLocalVoxelHeat(
                    triVerts, weldPos, specs, idxHandR, idxArmLowerR, fingerSpecIndicesR, dense, nb);
            }

            // =====================================================================
            // PASS 3: LOCALIZED ISLAND & GEODESIC-BOUNDED CLOTH-CHAIN WEIGHTING
            // =====================================================================
            if (hasClothBones)
            {
                ApplyLocalizedClothChainWeights(weldPos, weldMeshNames, adj, specs, dense, nb);
            }

            // =====================================================================
            // POST-PASS: 1 light weld-safe Laplacian pass (sub-voxel polish) + Chin Plane Gate
            // =====================================================================
            var smoothed = new float[dense.Length];
            var isFingerBone = new bool[nb];
            var isMetaBone = new bool[nb];
            for (int b = 0; b < nb; b++)
            {
                isFingerBone[b] = specs[b].Role == ReferenceBoneRole.Finger;
                isMetaBone[b] = specs[b].Name.Contains("_meta_", StringComparison.OrdinalIgnoreCase);
            }

            Parallel.For(0, n, i =>
            {
                int rowOff = i * nb;
                var neighbors = adj[i];
                if (neighbors.Count == 0)
                {
                    Array.Copy(dense, rowOff, smoothed, rowOff, nb);
                    return;
                }
                // Check if vertex is predominantly on a distal finger phalanx (keep distal fingers crisp)
                float distalFingerW = 0f;
                for (int b = 0; b < nb; b++)
                    if (isFingerBone[b] && !isMetaBone[b]) distalFingerW += dense[rowOff + b];
                float selfKeep = distalFingerW > 0.45f ? 0.88f : 0.78f;
                float neighMix = 1f - selfKeep;
                float invDeg = 1f / neighbors.Count;
                float rowSum = 0f;
                for (int b = 0; b < nb; b++)
                {
                    float avg = 0f;
                    for (int ni = 0; ni < neighbors.Count; ni++)
                        avg += dense[neighbors[ni] * nb + b];
                    float val = dense[rowOff + b] * selfKeep + (avg * invDeg) * neighMix;
                    smoothed[rowOff + b] = val;
                    rowSum += val;
                }
                if (rowSum > 1e-6f)
                {
                    float invS = 1f / rowSum;
                    for (int b = 0; b < nb; b++) smoothed[rowOff + b] *= invS;
                }
            });
            dense = smoothed;

            // Chin Plane Gate: prevent neck_0 from pulling on chin/jaw/head vertices
            int idxNeck = specs.FindIndex(b => b.Name.Equals("neck_0", StringComparison.OrdinalIgnoreCase));
            int idxHead = specs.FindIndex(b => b.Name.Equals("head", StringComparison.OrdinalIgnoreCase));
            if (chinPlaneY.HasValue && idxNeck >= 0 && idxHead >= 0)
            {
                float cy = chinPlaneY.Value;
                float band = MathF.Max(0.008f, 0.012f * H);
                for (int i = 0; i < n; i++)
                {
                    float vy = weldPos[i].Y;
                    if (vy <= cy - band) continue;
                    int rowOff = i * nb;
                    float wNeck = dense[rowOff + idxNeck];
                    if (wNeck <= 1e-5f) continue;
                    if (vy >= cy)
                    {
                        dense[rowOff + idxHead] += wNeck;
                        dense[rowOff + idxNeck] = 0f;
                    }
                    else
                    {
                        float t = (vy - (cy - band)) / band;
                        float shift = wNeck * t;
                        dense[rowOff + idxHead] += shift;
                        dense[rowOff + idxNeck] = wNeck - shift;
                    }
                }
            }

            // Zero-allocation Top-4 extraction & normalization per surface
            outBones = new List<int[]>(alignedVerts.Count);
            outWeights = new List<float[]>(alignedVerts.Count);
            Span<int> topIdx = stackalloc int[4];
            Span<float> topVal = stackalloc float[4];

            for (int s = 0; s < alignedVerts.Count; s++)
            {
                int vc = alignedVerts[s].Length;
                var bones = new int[vc * 4];
                var weights = new float[vc * 4];
                var map = vertToWeld[s];
                for (int v = 0; v < vc; v++)
                {
                    int baseIdx = map[v] * nb;
                    topIdx[0] = topIdx[1] = topIdx[2] = topIdx[3] = 0;
                    topVal[0] = topVal[1] = topVal[2] = topVal[3] = -1f;
                    for (int b = 0; b < nb; b++)
                    {
                        float w = dense[baseIdx + b];
                        if (w <= topVal[3]) continue;
                        int insertPos = 3;
                        while (insertPos > 0 && w > topVal[insertPos - 1])
                        {
                            topVal[insertPos] = topVal[insertPos - 1];
                            topIdx[insertPos] = topIdx[insertPos - 1];
                            insertPos--;
                        }
                        topVal[insertPos] = w;
                        topIdx[insertPos] = b;
                    }
                    float tot = 0f;
                    for (int k = 0; k < 4; k++)
                        if (topVal[k] > 0f) tot += topVal[k];
                    if (tot <= 1e-6f)
                    {
                        bones[v * 4] = 0;
                        weights[v * 4] = 1f;
                        continue;
                    }
                    float invTot = 1f / tot;
                    for (int k = 0; k < 4; k++)
                    {
                        bones[v * 4 + k] = topVal[k] > 0f ? topIdx[k] : 0;
                        weights[v * 4 + k] = topVal[k] > 0f ? topVal[k] * invTot : 0f;
                    }
                }
                outBones.Add(bones);
                outWeights.Add(weights);
            }

            return true;
        }

        /// <summary>
        /// Dedicated Pass 3 cloth-chain weight solver: isolates each authored cloth chain strictly to its target submesh
        /// (or auto-detected connected mesh island(s) pierced by the chain), bounds propagation by both geodesic surface
        /// distance and 3D capsule <c>InfluenceRadius</c>, and feathers smoothly from <c>0.0 -> 1.0</c> past the chain
        /// root so hair/ponytail/cape bones NEVER bleed into adjacent face, ear, or hoodie geometry!
        /// </summary>
        private static void ApplyLocalizedClothChainWeights(
            List<Vector3> weldPos,
            List<HashSet<string>> weldMeshNames,
            List<int>[] adj,
            List<ReferenceRigDef.ResolvedBoneSpec> specs,
            float[] dense,
            int nb)
        {
            int n = weldPos.Count;
            if (n == 0) return;

            // Group cloth bone specs by ClothChainId in root -> tip order
            var chainsById = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int b = 0; b < nb; b++)
            {
                if (specs[b].Role != ReferenceBoneRole.Cloth) continue;
                string cid = string.IsNullOrEmpty(specs[b].ClothChainId) ? $"__cloth_{b}" : specs[b].ClothChainId;
                if (!chainsById.TryGetValue(cid, out var list))
                {
                    list = new List<int>();
                    chainsById[cid] = list;
                }
                list.Add(b);
            }
            if (chainsById.Count == 0) return;

            // Compute connected mesh island IDs once over welded triangle adjacency
            var islandId = new int[n];
            Array.Fill(islandId, -1);
            int nextIsland = 0;
            var bfsQ = new Queue<int>();
            for (int i = 0; i < n; i++)
            {
                if (islandId[i] >= 0) continue;
                int curId = nextIsland++;
                islandId[i] = curId;
                bfsQ.Enqueue(i);
                while (bfsQ.Count > 0)
                {
                    int u = bfsQ.Dequeue();
                    var nbrs = adj[u];
                    for (int k = 0; k < nbrs.Count; k++)
                    {
                        int v = nbrs[k];
                        if (islandId[v] < 0) { islandId[v] = curId; bfsQ.Enqueue(v); }
                    }
                }
            }

            // Accumulate cloth contributions per vertex across all chains (so multiple chains blend cleanly if overlapping)
            var clothContrib = new float[n * nb];
            var totalClothW = new float[n];

            foreach (var kv in chainsById)
            {
                var boneList = kv.Value;
                if (boneList.Count == 0) continue;

                var firstSpec = specs[boneList[0]];
                float radius = Math.Clamp(firstSpec.Radius > 0.005f ? firstSpec.Radius : 0.08f, 0.02f, 0.35f);
                string targetMesh = firstSpec.ClothTargetMeshName ?? "";
                bool hasTargetMesh = !string.IsNullOrWhiteSpace(targetMesh);

                int segCount = boneList.Count;
                Vector3 rootPos = firstSpec.SegStart;
                Vector3 firstSegAxis = firstSpec.SegEnd - firstSpec.SegStart;
                float firstSegLen = firstSegAxis.Length();
                Vector3 rootDir = firstSegLen > 1e-4f
                    ? firstSegAxis / firstSegLen
                    : (specs[boneList[^1]].SegEnd - rootPos).LengthSquared() > 1e-8f
                        ? (specs[boneList[^1]].SegEnd - rootPos).Normalized()
                        : Vector3.Down;

                // Precompute per-segment lengths & cumulative arc length so curved chains evaluate cleanly
                var segLens = new float[segCount];
                var segCumStart = new float[segCount];
                float totalChainLen = 0f;
                for (int bi = 0; bi < segCount; bi++)
                {
                    segCumStart[bi] = totalChainLen;
                    float sl = specs[boneList[bi]].SegStart.DistanceTo(specs[boneList[bi]].SegEnd);
                    segLens[bi] = sl;
                    totalChainLen += sl;
                }

                // 1. Project eligible vertices onto the chain & identify which mesh island(s) belong to this cloth chain.
                //    - Dedicated cloth/hair shells (ponytails, overlapping anime hair cards, detached capes/skirts) are isolated
                //      from adjacent head/face/body shells.
                //    - Tiny nearby props/buttons/rivets are guarded from hijacking a large pierced cloth mesh.
                //    - Welded/wide cloth (capes, long hair, skirts) is NOT blocked by parent-bone weight gates, so hanging
                //      segments passing over spine/pelvis/legs receive full, smooth cloth weights all the way to the tip!
                var allowedIslands = new HashSet<int>();
                float radiusSq = radius * radius;
                float coreDist = radius * 0.70f;
                float coreDistSq = coreDist * coreDist;
                int closestAnyVert = -1;
                float closestAnyDistSq = float.MaxValue;

                var islandTotalVerts = new Dictionary<int, int>();
                var islandNearVerts = new Dictionary<int, int>();
                var islandCoreVerts = new Dictionary<int, int>();
                var islandDistalCoreVerts = new Dictionary<int, int>();

                for (int i = 0; i < n; i++)
                {
                    if (hasTargetMesh && !weldMeshNames[i].Contains(targetMesh)) continue;
                    int isl = islandId[i];
                    islandTotalVerts[isl] = islandTotalVerts.GetValueOrDefault(isl) + 1;

                    Vector3 p = weldPos[i];
                    float minDSq = float.MaxValue;
                    int bestSeg = 0;
                    float bestT = 0f;
                    for (int bi = 0; bi < segCount; bi++)
                    {
                        var sp = specs[boneList[bi]];
                        Vector3 ab = sp.SegEnd - sp.SegStart;
                        float len2 = ab.LengthSquared();
                        float t = len2 < 1e-10f ? 0f : Mathf.Clamp((p - sp.SegStart).Dot(ab) / len2, 0f, 1f);
                        float dSq = p.DistanceSquaredTo(sp.SegStart + ab * t);
                        if (dSq < minDSq) { minDSq = dSq; bestSeg = bi; bestT = t; }
                    }
                    if (minDSq < closestAnyDistSq) { closestAnyDistSq = minDSq; closestAnyVert = i; }

                    bool pastRoot = bestSeg > 0 || (p - rootPos).Dot(rootDir) >= -0.015f;
                    if (minDSq <= radiusSq && pastRoot)
                    {
                        islandNearVerts[isl] = islandNearVerts.GetValueOrDefault(isl) + 1;
                        if (minDSq <= coreDistSq)
                        {
                            islandCoreVerts[isl] = islandCoreVerts.GetValueOrDefault(isl) + 1;
                            float arcAlong = segCumStart[bestSeg] + bestT * segLens[bestSeg];
                            if (arcAlong >= totalChainLen * 0.15f)
                                islandDistalCoreVerts[isl] = islandDistalCoreVerts.GetValueOrDefault(isl) + 1;
                        }
                    }
                }

                if (hasTargetMesh)
                {
                    foreach (int isl in islandTotalVerts.Keys)
                        allowedIslands.Add(isl);
                }
                else
                {
                    int maxNear = 0;
                    foreach (var ikv in islandNearVerts)
                        if (ikv.Value > maxNear) maxNear = ikv.Value;

                    var dedicated = new List<int>();
                    int dedicatedNearSum = 0;
                    foreach (var ikv in islandNearVerts)
                    {
                        int isl = ikv.Key;
                        int tot = Math.Max(1, islandTotalVerts.GetValueOrDefault(isl));
                        float frac = ikv.Value / (float)tot;
                        if (frac >= 0.40f && islandCoreVerts.GetValueOrDefault(isl) > 0)
                        {
                            dedicated.Add(isl);
                            dedicatedNearSum += ikv.Value;
                        }
                    }

                    // Accept dedicated cloth/hair island(s) only when they account for a meaningful share of the
                    // chain's nearby geometry (prevents a tiny 6-vert button/rivet from locking out a 3000-vert skirt/cape).
                    bool useDedicated = dedicated.Count > 0 && dedicatedNearSum >= Math.Max(4, (int)(maxNear * 0.25f));
                    if (useDedicated)
                    {
                        foreach (int isl in dedicated) allowedIslands.Add(isl);
                    }
                    else
                    {
                        // Cloth is welded into a larger mesh (body or wide cape/skirt): pick the island most strongly
                        // pierced along the chain core (never biased toward tiny unrelated islands).
                        int bestIsl = -1;
                        float bestScore = -1f;
                        foreach (var ikv in islandNearVerts)
                        {
                            int isl = ikv.Key;
                            float score = islandDistalCoreVerts.GetValueOrDefault(isl) * 2.0f
                                        + islandCoreVerts.GetValueOrDefault(isl)
                                        + ikv.Value * 0.5f;
                            if (score > bestScore) { bestScore = score; bestIsl = isl; }
                        }
                        if (bestIsl >= 0) allowedIslands.Add(bestIsl);
                        else if (closestAnyVert >= 0) allowedIslands.Add(islandId[closestAnyVert]);
                    }
                }

                // Measure closest distance on allowed islands; if the user placed the chain slightly outside the cloth
                // surface (> 90% of radius), expand effRadius just enough to bind that surface strip instead of leaving 0 weight.
                float closestAllowedDistSq = float.MaxValue;
                int closestAllowedVert = -1;
                for (int i = 0; i < n; i++)
                {
                    if (!allowedIslands.Contains(islandId[i])) continue;
                    if (hasTargetMesh && !weldMeshNames[i].Contains(targetMesh)) continue;
                    Vector3 p = weldPos[i];
                    float minDSq = float.MaxValue;
                    for (int bi = 0; bi < segCount; bi++)
                    {
                        var sp = specs[boneList[bi]];
                        float dSq = p.DistanceSquaredTo(ClosestPointOnSegment(p, sp.SegStart, sp.SegEnd));
                        if (dSq < minDSq) minDSq = dSq;
                    }
                    if (minDSq < closestAllowedDistSq) { closestAllowedDistSq = minDSq; closestAllowedVert = i; }
                }

                float effRadius = radius;
                if (closestAllowedVert >= 0)
                {
                    float closestAllowedDist = MathF.Sqrt(closestAllowedDistSq);
                    if (closestAllowedDist > radius * 0.90f)
                        effRadius = MathF.Min(0.45f, MathF.Max(radius, closestAllowedDist + MathF.Max(0.035f, radius * 0.50f)));
                }

                // 2. Collect geodesic Dijkstra seeds inside the chain core on allowed islands
                var geoDist = new float[n];
                Array.Fill(geoDist, float.PositiveInfinity);
                var pq = new PriorityQueue<int, float>();
                float seedDist = effRadius * 0.82f;
                float seedDistSq = seedDist * seedDist;
                float maxGeo = effRadius * 1.65f;

                for (int i = 0; i < n; i++)
                {
                    if (!allowedIslands.Contains(islandId[i])) continue;
                    if (hasTargetMesh && !weldMeshNames[i].Contains(targetMesh)) continue;

                    Vector3 p = weldPos[i];
                    float minDSq = float.MaxValue;
                    int bestSeg = 0;
                    for (int bi = 0; bi < segCount; bi++)
                    {
                        var sp = specs[boneList[bi]];
                        float dSq = p.DistanceSquaredTo(ClosestPointOnSegment(p, sp.SegStart, sp.SegEnd));
                        if (dSq < minDSq) { minDSq = dSq; bestSeg = bi; }
                    }
                    if (bestSeg == 0 && (p - rootPos).Dot(rootDir) < -0.012f) continue;

                    if (minDSq <= seedDistSq)
                    {
                        geoDist[i] = 0f;
                        pq.Enqueue(i, 0f);
                    }
                }
                if (pq.Count == 0 && closestAllowedVert >= 0)
                {
                    geoDist[closestAllowedVert] = 0f;
                    pq.Enqueue(closestAllowedVert, 0f);
                }

                // Bounded Dijkstra along mesh edges (stops at maxGeo so it never crawls across the face/scalp!)
                while (pq.TryDequeue(out int u, out float du))
                {
                    if (du > geoDist[u] || du >= maxGeo) continue;
                    var nbrs = adj[u];
                    for (int k = 0; k < nbrs.Count; k++)
                    {
                        int v = nbrs[k];
                        if (hasTargetMesh && !weldMeshNames[v].Contains(targetMesh)) continue;
                        float nd = du + weldPos[u].DistanceTo(weldPos[v]);
                        if (nd < geoDist[v] && nd <= maxGeo)
                        {
                            geoDist[v] = nd;
                            pq.Enqueue(v, nd);
                        }
                    }
                }

                // 3. Assign crisp localized cloth weights with root-seam feathering + 3D radius + geodesic cutoff
                float radInner = effRadius * 0.65f;
                float geoInner = effRadius * 0.90f;
                float rootFeatherLen = MathF.Min(0.040f, MathF.Max(0.015f, firstSegLen * 0.28f));
                var segW = new float[segCount];

                for (int i = 0; i < n; i++)
                {
                    float gd = geoDist[i];
                    if (float.IsPositiveInfinity(gd) || gd >= maxGeo) continue;

                    Vector3 p = weldPos[i];
                    // Strict 3D distance to the chain
                    float minDist = float.MaxValue;
                    int bestSeg = 0;
                    float bestSegT = 0f;
                    for (int bi = 0; bi < segCount; bi++)
                    {
                        var sp = specs[boneList[bi]];
                        Vector3 ab = sp.SegEnd - sp.SegStart;
                        float len2 = ab.LengthSquared();
                        float t = len2 < 1e-10f ? 0f : Mathf.Clamp((p - sp.SegStart).Dot(ab) / len2, 0f, 1f);
                        float d = p.DistanceTo(sp.SegStart + ab * t);
                        if (d < minDist) { minDist = d; bestSeg = bi; bestSegT = t; }
                    }
                    if (minDist >= effRadius) continue;

                    // Smooth root-seam ramp on the root segment only (never zeros out distal segments of curved chains)
                    float rootRamp = 1.0f;
                    if (bestSeg == 0)
                    {
                        float alongRoot = (p - rootPos).Dot(rootDir);
                        rootRamp = SmoothStep01(-0.010f, rootFeatherLen, alongRoot);
                        if (rootRamp <= 0.001f) continue;
                    }

                    float radialFalloff = 1.0f - SmoothStep01(radInner, effRadius, minDist);
                    float geoFalloff = 1.0f - SmoothStep01(geoInner, maxGeo, gd);
                    float wChain = rootRamp * radialFalloff * geoFalloff;
                    if (wChain <= 0.005f) continue;

                    // Distribute wChain cleanly across the chain's segments (1 bone -> 100%, multi-bone -> smooth axial blend)
                    int rowOff = i * nb;
                    if (segCount == 1)
                    {
                        clothContrib[rowOff + boneList[0]] += wChain;
                    }
                    else
                    {
                        float chainCoord = bestSeg + bestSegT; // in [0, segCount]
                        float sumSeg = 0f;
                        for (int bi = 0; bi < segCount; bi++)
                        {
                            float center = bi + 0.5f;
                            float delta = chainCoord - center;
                            float sw = MathF.Exp(-delta * delta * 2.2f);
                            segW[bi] = sw;
                            sumSeg += sw;
                        }
                        float invSeg = sumSeg > 1e-6f ? (wChain / sumSeg) : 0f;
                        for (int bi = 0; bi < segCount; bi++)
                            clothContrib[rowOff + boneList[bi]] += segW[bi] * invSeg;
                    }
                    totalClothW[i] += wChain;
                }
            }

            // Blend cloth contributions cleanly over Pass 1+2 body weights (preserving exact partition of unity)
            for (int i = 0; i < n; i++)
            {
                float cw = totalClothW[i];
                if (cw <= 0.001f) continue;
                int rowOff = i * nb;
                float clampedCw = MathF.Min(1.0f, cw);
                float bodyKeep = 1.0f - clampedCw;
                float clothScale = clampedCw / cw;
                for (int b = 0; b < nb; b++)
                {
                    if (specs[b].Role == ReferenceBoneRole.Cloth)
                        dense[rowOff + b] = clothContrib[rowOff + b] * clothScale;
                    else
                        dense[rowOff + b] *= bodyKeep;
                }
            }
        }

        private static float SmoothStep01(float edge0, float edge1, float x)
        {
            if (edge1 <= edge0) return x >= edge1 ? 1f : 0f;
            float u = Mathf.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
            return u * u * (3f - 2f * u);
        }

        private static Vector3 ComputeFarthestFingerEnd(
            List<ReferenceRigDef.ResolvedBoneSpec> specs,
            List<int> fingerIndices,
            Vector3 wrist,
            Vector3 fallbackEnd)
        {
            Vector3 avgTip = Vector3.Zero;
            int cnt = 0;
            float maxDist = 0f;
            Vector3 farthest = fallbackEnd;
            foreach (int fi in fingerIndices)
            {
                Vector3 end = specs[fi].SegEnd;
                float d = wrist.DistanceTo(end);
                if (d > maxDist)
                {
                    maxDist = d;
                    farthest = end;
                }
                avgTip += end;
                cnt++;
            }
            if (cnt == 0) return fallbackEnd;
            Vector3 dir = ((avgTip / cnt) - wrist);
            if (dir.LengthSquared() < 1e-8f) return farthest;
            return wrist + dir.Normalized() * (maxDist * 0.90f);
        }

        private static void RefineHandWithLocalVoxelHeat(
            List<(Vector3 A, Vector3 B, Vector3 C)> allTris,
            List<Vector3> weldPos,
            List<ReferenceRigDef.ResolvedBoneSpec> specs,
            int idxHand,
            int idxArmLower,
            List<int> fingerSpecIndices,
            float[] dense,
            int nb)
        {
            Vector3 wrist = specs[idxHand].SegStart;
            Vector3 hMin = wrist;
            Vector3 hMax = wrist;
            ExpandBounds(ref hMin, ref hMax, specs[idxHand].SegEnd);
            foreach (int fi in fingerSpecIndices)
            {
                ExpandBounds(ref hMin, ref hMax, specs[fi].SegStart);
                ExpandBounds(ref hMin, ref hMax, specs[fi].SegEnd);
            }

            Vector3 handSpan = hMax - hMin;
            float handMaxDim = MathF.Max(handSpan.X, MathF.Max(handSpan.Y, handSpan.Z));
            if (handMaxDim < 0.01f) return;

            float margin = MathF.Max(0.018f, handMaxDim * 0.16f);
            Vector3 padVec = new Vector3(margin, margin, margin);
            Vector3 boxMin = hMin - padVec;
            Vector3 boxMax = hMax + padVec;

            // Pre-filter triangles overlapping the local hand AABB
            var handTris = new List<(Vector3 A, Vector3 B, Vector3 C)>(1024);
            for (int i = 0; i < allTris.Count; i++)
            {
                var tri = allTris[i];
                float tMinX = MathF.Min(tri.A.X, MathF.Min(tri.B.X, tri.C.X));
                float tMaxX = MathF.Max(tri.A.X, MathF.Max(tri.B.X, tri.C.X));
                if (tMaxX < boxMin.X || tMinX > boxMax.X) continue;
                float tMinY = MathF.Min(tri.A.Y, MathF.Min(tri.B.Y, tri.C.Y));
                float tMaxY = MathF.Max(tri.A.Y, MathF.Max(tri.B.Y, tri.C.Y));
                if (tMaxY < boxMin.Y || tMinY > boxMax.Y) continue;
                float tMinZ = MathF.Min(tri.A.Z, MathF.Min(tri.B.Z, tri.C.Z));
                float tMaxZ = MathF.Max(tri.A.Z, MathF.Max(tri.B.Z, tri.C.Z));
                if (tMaxZ < boxMin.Z || tMinZ > boxMax.Z) continue;
                handTris.Add(tri);
            }
            if (handTris.Count < 4) return;

            // Local hand bones: hand_L/R + all finger_*_meta_* + finger_*_0..2_* + optional proximal forearm boundary
            var localBones = new List<SolverBone>(fingerSpecIndices.Count + 2);
            localBones.Add(new SolverBone
            {
                OriginalSpecIndex = idxHand,
                Name = specs[idxHand].Name,
                SegStart = specs[idxHand].SegStart,
                SegEnd = specs[idxHand].SegEnd,
                AllowBelow = true,
                AllowBeyond = false,
                IsAuxiliaryBoundary = false
            });
            foreach (int fi in fingerSpecIndices)
            {
                localBones.Add(new SolverBone
                {
                    OriginalSpecIndex = fi,
                    Name = specs[fi].Name,
                    SegStart = specs[fi].SegStart,
                    SegEnd = specs[fi].SegEnd,
                    AllowBelow = specs[fi].AllowBelow,
                    AllowBeyond = specs[fi].AllowBeyond,
                    IsAuxiliaryBoundary = false
                });
            }
            if (idxArmLower >= 0)
            {
                // Proximal forearm anchor just behind wrist so forearm voxels entering the hand box absorb wrist bleed
                Vector3 armDir = (specs[idxArmLower].SegStart - wrist);
                if (armDir.LengthSquared() > 1e-6f)
                {
                    Vector3 armDirN = armDir.Normalized();
                    Vector3 proxStart = wrist + armDirN * MathF.Min(0.012f, handMaxDim * 0.08f);
                    Vector3 proxEnd = wrist + armDirN * MathF.Min(0.045f, margin * 0.90f);
                    localBones.Add(new SolverBone
                    {
                        OriginalSpecIndex = -1,
                        Name = "__wrist_boundary__",
                        SegStart = proxStart,
                        SegEnd = proxEnd,
                        AllowBelow = true,
                        AllowBeyond = true,
                        IsAuxiliaryBoundary = true
                    });
                }
            }

            var handVol = BuildVoxelVolume(handTris, boxMin, boxMax, longestAxisResolution: 64, padCells: 2);
            if (handVol == null || handVol.ActiveCount == 0) return;

            float[][] handHeat = SolveVoxelHeatPde(handVol, localBones, sorIterations: 64, omega: 1.65f, robinAlpha: 3.2f, out int[] handNearestSolved);

            int localCount = localBones.Count;
            int nVerts = weldPos.Count;
            Parallel.For(0, nVerts, i =>
            {
                int rowOff = i * nb;
                float wHand = dense[rowOff + idxHand];
                if (wHand <= 0.001f) return;

                Vector3 p = weldPos[i];
                Span<float> localW = stackalloc float[localCount];
                SampleSinglePointHeat(handVol, handHeat, handNearestSolved, p, localCount, localW);

                // Normalize strictly over non-auxiliary hand + finger bones
                float sum = 0f;
                for (int lb = 0; lb < localCount; lb++)
                {
                    if (localBones[lb].IsAuxiliaryBoundary) { localW[lb] = 0f; continue; }
                    sum += localW[lb];
                }
                if (sum <= 1e-5f) return; // keep Pass 1's hand weight intact if outside hand volume

                // Replace Pass 1's hand weight with sub-partitioned hand + metacarpal + finger weights
                dense[rowOff + idxHand] = 0f;
                float scale = wHand / sum;
                for (int lb = 0; lb < localCount; lb++)
                {
                    if (localBones[lb].IsAuxiliaryBoundary || localW[lb] <= 0f) continue;
                    dense[rowOff + localBones[lb].OriginalSpecIndex] += localW[lb] * scale;
                }
            });
        }

        private static VoxelVolume BuildVoxelVolume(
            List<(Vector3 A, Vector3 B, Vector3 C)> tris,
            Vector3 bMin,
            Vector3 bMax,
            int longestAxisResolution,
            int padCells)
        {
            Vector3 size = bMax - bMin;
            float maxDim = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
            if (maxDim < 1e-4f) return null;

            float voxelSize = maxDim / Math.Max(8, longestAxisResolution);
            float invVoxel = 1f / voxelSize;

            int nx = Math.Clamp((int)MathF.Ceiling(size.X * invVoxel) + padCells * 2, 6, longestAxisResolution + padCells * 2 + 2);
            int ny = Math.Clamp((int)MathF.Ceiling(size.Y * invVoxel) + padCells * 2, 6, longestAxisResolution + padCells * 2 + 2);
            int nz = Math.Clamp((int)MathF.Ceiling(size.Z * invVoxel) + padCells * 2, 6, longestAxisResolution + padCells * 2 + 2);
            Vector3 origin = bMin - new Vector3(padCells * voxelSize, padCells * voxelSize, padCells * voxelSize);

            int total = nx * ny * nz;
            var state = new byte[total];

            // 1. Conservative triangle surface rasterization (point-to-triangle distance <= 0.60 * voxelSize)
            float surfThreshSq = (voxelSize * 0.60f) * (voxelSize * 0.60f);
            for (int ti = 0; ti < tris.Count; ti++)
            {
                var (a, b, c) = tris[ti];
                float minX = MathF.Min(a.X, MathF.Min(b.X, c.X)) - voxelSize * 0.6f;
                float maxX = MathF.Max(a.X, MathF.Max(b.X, c.X)) + voxelSize * 0.6f;
                float minY = MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) - voxelSize * 0.6f;
                float maxY = MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) + voxelSize * 0.6f;
                float minZ = MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) - voxelSize * 0.6f;
                float maxZ = MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) + voxelSize * 0.6f;

                int ix0 = Math.Clamp((int)MathF.Floor((minX - origin.X) * invVoxel), 1, nx - 2);
                int ix1 = Math.Clamp((int)MathF.Floor((maxX - origin.X) * invVoxel), 1, nx - 2);
                int iy0 = Math.Clamp((int)MathF.Floor((minY - origin.Y) * invVoxel), 1, ny - 2);
                int iy1 = Math.Clamp((int)MathF.Floor((maxY - origin.Y) * invVoxel), 1, ny - 2);
                int iz0 = Math.Clamp((int)MathF.Floor((minZ - origin.Z) * invVoxel), 1, nz - 2);
                int iz1 = Math.Clamp((int)MathF.Floor((maxZ - origin.Z) * invVoxel), 1, nz - 2);

                for (int z = iz0; z <= iz1; z++)
                {
                    float cz = origin.Z + (z + 0.5f) * voxelSize;
                    int zOff = z * ny;
                    for (int y = iy0; y <= iy1; y++)
                    {
                        float cy = origin.Y + (y + 0.5f) * voxelSize;
                        int yzOff = (zOff + y) * nx;
                        for (int x = ix0; x <= ix1; x++)
                        {
                            int idx = yzOff + x;
                            if (state[idx] == CellSurface) continue;
                            float cx = origin.X + (x + 0.5f) * voxelSize;
                            if (PointTriangleDistSq(new Vector3(cx, cy, cz), a, b, c) <= surfThreshSq)
                                state[idx] = CellSurface;
                        }
                    }
                }
            }

            // 2. Exterior air 6-connected BFS from outer padded border (0, 0, 0)
            var queue = new int[total];
            int qHead = 0, qTail = 0;
            state[0] = CellExteriorAir;
            queue[qTail++] = 0;

            int strideY = nx;
            int strideZ = nx * ny;
            while (qHead < qTail)
            {
                int cur = queue[qHead++];
                int z = cur / strideZ;
                int rem = cur - z * strideZ;
                int y = rem / strideY;
                int x = rem - y * strideY;

                if (x > 0 && state[cur - 1] == CellEmpty) { state[cur - 1] = CellExteriorAir; queue[qTail++] = cur - 1; }
                if (x + 1 < nx && state[cur + 1] == CellEmpty) { state[cur + 1] = CellExteriorAir; queue[qTail++] = cur + 1; }
                if (y > 0 && state[cur - strideY] == CellEmpty) { state[cur - strideY] = CellExteriorAir; queue[qTail++] = cur - strideY; }
                if (y + 1 < ny && state[cur + strideY] == CellEmpty) { state[cur + strideY] = CellExteriorAir; queue[qTail++] = cur + strideY; }
                if (z > 0 && state[cur - strideZ] == CellEmpty) { state[cur - strideZ] = CellExteriorAir; queue[qTail++] = cur - strideZ; }
                if (z + 1 < nz && state[cur + strideZ] == CellEmpty) { state[cur + strideZ] = CellExteriorAir; queue[qTail++] = cur + strideZ; }
            }

            // 3. Compact active solid voxels (CellSurface + unvisited interior CellEmpty)
            var gridToActive = new int[total];
            Array.Fill(gridToActive, -1);
            int activeCount = 0;
            for (int i = 0; i < total; i++)
            {
                if (state[i] != CellExteriorAir)
                    gridToActive[i] = activeCount++;
            }
            if (activeCount == 0) return null;

            var activeToGrid = new int[activeCount];
            var activeNeighbors = new int[activeCount * 6];
            var activeParity = new byte[activeCount];

            for (int z = 0; z < nz; z++)
            {
                int zOff = z * ny;
                for (int y = 0; y < ny; y++)
                {
                    int yzOff = (zOff + y) * nx;
                    for (int x = 0; x < nx; x++)
                    {
                        int g = yzOff + x;
                        int a = gridToActive[g];
                        if (a < 0) continue;
                        activeToGrid[a] = g;
                        activeParity[a] = (byte)((x + y + z) & 1);
                        int nBase = a * 6;
                        activeNeighbors[nBase + 0] = x > 0 ? gridToActive[g - 1] : -1;
                        activeNeighbors[nBase + 1] = x + 1 < nx ? gridToActive[g + 1] : -1;
                        activeNeighbors[nBase + 2] = y > 0 ? gridToActive[g - strideY] : -1;
                        activeNeighbors[nBase + 3] = y + 1 < ny ? gridToActive[g + strideY] : -1;
                        activeNeighbors[nBase + 4] = z > 0 ? gridToActive[g - strideZ] : -1;
                        activeNeighbors[nBase + 5] = z + 1 < nz ? gridToActive[g + strideZ] : -1;
                    }
                }
            }

            return new VoxelVolume
            {
                Nx = nx,
                Ny = ny,
                Nz = nz,
                Origin = origin,
                VoxelSize = voxelSize,
                InvVoxelSize = invVoxel,
                State = state,
                GridToActive = gridToActive,
                ActiveToGrid = activeToGrid,
                ActiveNeighbors = activeNeighbors,
                ActiveParity = activeParity,
                ActiveCount = activeCount
            };
        }

        private static float[][] SolveVoxelHeatPde(
            VoxelVolume vol,
            List<SolverBone> bones,
            int sorIterations,
            float omega,
            float robinAlpha,
            out int[] nearestSolvedGrid)
        {
            int M = vol.ActiveCount;
            int nb = bones.Count;
            float vs = vol.VoxelSize;
            float invVs = vol.InvVoxelSize;
            int nx = vol.Nx, ny = vol.Ny, nz = vol.Nz;
            int strideZ = nx * ny;

            // Precompute trimmed bone segments (leave 8% free at non-endpoint joints so heat diffuses smoothly across creases)
            var trimStart = new Vector3[nb];
            var trimEnd = new Vector3[nb];
            for (int b = 0; b < nb; b++)
            {
                Vector3 a = bones[b].SegStart;
                Vector3 e = bones[b].SegEnd;
                Vector3 ab = e - a;
                float t0 = bones[b].AllowBelow ? 0.0f : 0.08f;
                float t1 = bones[b].AllowBeyond ? 1.0f : 0.92f;
                trimStart[b] = a + ab * t0;
                trimEnd[b] = a + ab * t1;
            }

            // Per-active-voxel Baranâ€“PopoviÄ‡ conductance c[i] and nearest visible bone owner[i]
            var conductance = new float[M];
            var ownerBone = new int[M];
            Array.Fill(ownerBone, -1);

            float coreDist = vs * 1.10f;
            float coreDistSq = coreDist * coreDist;

            Parallel.For(0, M, i =>
            {
                int g = vol.ActiveToGrid[i];
                int vz = g / strideZ;
                int rem = g - vz * strideZ;
                int vy = rem / nx;
                int vx = rem - vy * nx;
                Vector3 center = vol.CellCenter(vx, vy, vz);

                float bestVisDistSq = float.MaxValue;
                int bestVisBone = -1;
                float secondVisDistSq = float.MaxValue;

                for (int b = 0; b < nb; b++)
                {
                    Vector3 closest = ClosestPointOnSegment(center, trimStart[b], trimEnd[b]);
                    float dSq = center.DistanceSquaredTo(closest);
                    if (dSq >= secondVisDistSq && dSq > coreDistSq) continue;

                    // Test interior 3D DDA line-of-sight through active solid voxels (skip DDA if inside bone core)
                    bool visible = dSq <= coreDistSq || IsVoxelRayUnobstructed(vol, vx, vy, vz, closest);
                    if (!visible) continue;

                    if (dSq < bestVisDistSq)
                    {
                        secondVisDistSq = bestVisDistSq;
                        bestVisDistSq = dSq;
                        bestVisBone = b;
                    }
                    else if (dSq < secondVisDistSq)
                    {
                        secondVisDistSq = dSq;
                    }
                }

                if (bestVisBone >= 0)
                {
                    ownerBone[i] = bestVisBone;
                    if (bestVisDistSq <= coreDistSq)
                    {
                        // Strong bone-core heat source
                        conductance[i] = 64.0f;
                    }
                    else
                    {
                        // Smooth Baranâ€“PopoviÄ‡ Robin conductance; attenuate if nearly equidistant to a second visible bone
                        float dNormSq = bestVisDistSq * invVs * invVs;
                        float marginFactor = 1.0f;
                        if (secondVisDistSq < float.MaxValue && bestVisDistSq > 1e-8f)
                        {
                            float ratio = MathF.Sqrt(bestVisDistSq / secondVisDistSq); // in (0, 1]
                            if (ratio > 0.88f)
                                marginFactor = Mathf.Clamp((1.0f - ratio) / 0.12f, 0.15f, 1.0f);
                        }
                        conductance[i] = (robinAlpha * marginFactor) / (dNormSq + 0.25f);
                    }
                }
            });

            // Safety guarantee: ensure every bone has at least 1 strong seeded active voxel (prevents 0-weight short bones)
            var boneHasSeed = new bool[nb];
            for (int i = 0; i < M; i++)
                if (ownerBone[i] >= 0 && conductance[i] >= 1.0f) boneHasSeed[ownerBone[i]] = true;

            for (int b = 0; b < nb; b++)
            {
                if (boneHasSeed[b]) continue;
                Vector3 mid = (trimStart[b] + trimEnd[b]) * 0.5f;
                int bestActive = -1;
                float bestDSq = float.MaxValue;
                for (int i = 0; i < M; i++)
                {
                    int g = vol.ActiveToGrid[i];
                    int vz = g / strideZ;
                    int rem = g - vz * strideZ;
                    int vy = rem / nx;
                    int vx = rem - vy * nx;
                    float dSq = vol.CellCenter(vx, vy, vz).DistanceSquaredTo(mid);
                    if (dSq < bestDSq) { bestDSq = dSq; bestActive = i; }
                }
                if (bestActive >= 0)
                {
                    ownerBone[bestActive] = b;
                    conductance[bestActive] = 64.0f;
                    boneHasSeed[b] = true;
                }
            }

            // Precompute neighbor degree + diagonal inverse 1 / (deg + c_i) once for all bones
            var invDiag = new float[M];
            for (int i = 0; i < M; i++)
            {
                int deg = 0;
                int nBase = i * 6;
                for (int k = 0; k < 6; k++)
                    if (vol.ActiveNeighbors[nBase + k] >= 0) deg++;
                float diag = deg + conductance[i];
                invDiag[i] = diag > 1e-6f ? (1f / diag) : 0f;
            }

            // Solve independent SPD PDE per bone in parallel using Red-Black SOR
            var heat = new float[nb][];
            Parallel.For(0, nb, b =>
            {
                var u = new float[M];
                for (int i = 0; i < M; i++)
                    if (ownerBone[i] == b) u[i] = 1f;

                var neighbors = vol.ActiveNeighbors;
                var parity = vol.ActiveParity;

                for (int iter = 0; iter < sorIterations; iter++)
                {
                    for (byte color = 0; color < 2; color++)
                    {
                        for (int i = 0; i < M; i++)
                        {
                            if (parity[i] != color) continue;
                            float invD = invDiag[i];
                            if (invD <= 0f) continue;

                            int nBase = i * 6;
                            float sumN = 0f;
                            int n0 = neighbors[nBase + 0]; if (n0 >= 0) sumN += u[n0];
                            int n1 = neighbors[nBase + 1]; if (n1 >= 0) sumN += u[n1];
                            int n2 = neighbors[nBase + 2]; if (n2 >= 0) sumN += u[n2];
                            int n3 = neighbors[nBase + 3]; if (n3 >= 0) sumN += u[n3];
                            int n4 = neighbors[nBase + 4]; if (n4 >= 0) sumN += u[n4];
                            int n5 = neighbors[nBase + 5]; if (n5 >= 0) sumN += u[n5];

                            float rhs = ownerBone[i] == b ? conductance[i] : 0f;
                            float gs = (sumN + rhs) * invD;
                            float val = u[i] + omega * (gs - u[i]);
                            u[i] = val < 0f ? 0f : (val > 1f ? 1f : val);
                        }
                    }
                }
                heat[b] = u;
            });

            // Normalize solved active voxels and identify any unsolved detached voxel components
            int totalGrid = nx * ny * nz;
            nearestSolvedGrid = new int[totalGrid];
            Array.Fill(nearestSolvedGrid, -1);

            var bfsQueue = new int[totalGrid];
            int qHead = 0, qTail = 0;

            for (int i = 0; i < M; i++)
            {
                float sum = 0f;
                for (int b = 0; b < nb; b++) sum += heat[b][i];
                if (sum > 1e-4f)
                {
                    float invSum = 1f / sum;
                    for (int b = 0; b < nb; b++) heat[b][i] *= invSum;
                    int g = vol.ActiveToGrid[i];
                    nearestSolvedGrid[g] = i;
                    bfsQueue[qTail++] = g;
                }
            }

            // Multi-source 3D BFS through the grid: propagates nearest solved active voxel index to
            // detached floating shells (holsters, straps, hair clips) and air boundary cells!
            int strideY = nx;
            while (qHead < qTail)
            {
                int cur = bfsQueue[qHead++];
                int srcActive = nearestSolvedGrid[cur];
                int z = cur / strideZ;
                int rem = cur - z * strideZ;
                int y = rem / strideY;
                int x = rem - y * strideY;

                if (x > 0 && nearestSolvedGrid[cur - 1] < 0) { nearestSolvedGrid[cur - 1] = srcActive; bfsQueue[qTail++] = cur - 1; }
                if (x + 1 < nx && nearestSolvedGrid[cur + 1] < 0) { nearestSolvedGrid[cur + 1] = srcActive; bfsQueue[qTail++] = cur + 1; }
                if (y > 0 && nearestSolvedGrid[cur - strideY] < 0) { nearestSolvedGrid[cur - strideY] = srcActive; bfsQueue[qTail++] = cur - strideY; }
                if (y + 1 < ny && nearestSolvedGrid[cur + strideY] < 0) { nearestSolvedGrid[cur + strideY] = srcActive; bfsQueue[qTail++] = cur + strideY; }
                if (z > 0 && nearestSolvedGrid[cur - strideZ] < 0) { nearestSolvedGrid[cur - strideZ] = srcActive; bfsQueue[qTail++] = cur - strideZ; }
                if (z + 1 < nz && nearestSolvedGrid[cur + strideZ] < 0) { nearestSolvedGrid[cur + strideZ] = srcActive; bfsQueue[qTail++] = cur + strideZ; }
            }

            // Fill any unsolved active voxels (detached islands) from their nearest solved body voxel
            for (int i = 0; i < M; i++)
            {
                int g = vol.ActiveToGrid[i];
                int donor = nearestSolvedGrid[g];
                if (donor >= 0 && donor != i)
                {
                    for (int b = 0; b < nb; b++) heat[b][i] = heat[b][donor];
                }
            }

            return heat;
        }

        private static void SampleHeatOntoVertices(
            VoxelVolume vol,
            List<SolverBone> bones,
            float[][] heat,
            int[] nearestSolvedGrid,
            List<Vector3> weldPos,
            float[] dense,
            int nbFull)
        {
            int localCount = bones.Count;
            int nVerts = weldPos.Count;
            Parallel.For(0, nVerts, i =>
            {
                Span<float> sample = stackalloc float[localCount];
                SampleSinglePointHeat(vol, heat, nearestSolvedGrid, weldPos[i], localCount, sample);
                int rowOff = i * nbFull;
                for (int b = 0; b < localCount; b++)
                {
                    int origIdx = bones[b].OriginalSpecIndex;
                    if (origIdx >= 0 && origIdx < nbFull)
                        dense[rowOff + origIdx] = sample[b];
                }
            });
        }

        private static void SampleSinglePointHeat(
            VoxelVolume vol,
            float[][] heat,
            int[] nearestSolvedGrid,
            Vector3 p,
            int nb,
            Span<float> outWeights)
        {
            outWeights.Clear();
            float gx = (p.X - vol.Origin.X) * vol.InvVoxelSize - 0.5f;
            float gy = (p.Y - vol.Origin.Y) * vol.InvVoxelSize - 0.5f;
            float gz = (p.Z - vol.Origin.Z) * vol.InvVoxelSize - 0.5f;

            int x0 = (int)MathF.Floor(gx);
            int y0 = (int)MathF.Floor(gy);
            int z0 = (int)MathF.Floor(gz);
            float fx = gx - x0;
            float fy = gy - y0;
            float fz = gz - z0;

            float wTotal = 0f;
            for (int dz = 0; dz <= 1; dz++)
            {
                int z = z0 + dz;
                if (z < 0 || z >= vol.Nz) continue;
                float wz = dz == 0 ? (1f - fz) : fz;
                for (int dy = 0; dy <= 1; dy++)
                {
                    int y = y0 + dy;
                    if (y < 0 || y >= vol.Ny) continue;
                    float wyz = wz * (dy == 0 ? (1f - fy) : fy);
                    int yzOff = (z * vol.Ny + y) * vol.Nx;
                    for (int dx = 0; dx <= 1; dx++)
                    {
                        int x = x0 + dx;
                        if (x < 0 || x >= vol.Nx) continue;
                        int a = vol.GridToActive[yzOff + x];
                        if (a < 0) continue;
                        float w = wyz * (dx == 0 ? (1f - fx) : fx);
                        if (w <= 1e-6f) continue;
                        wTotal += w;
                        for (int b = 0; b < nb; b++)
                            outWeights[b] += w * heat[b][a];
                    }
                }
            }

            if (wTotal > 1e-5f)
            {
                float invW = 1f / wTotal;
                for (int b = 0; b < nb; b++) outWeights[b] *= invW;
                return;
            }

            // Fallback: nearest solved voxel in the 3D grid (handles vertices slightly outside solid cells)
            int cx = Math.Clamp((int)MathF.Floor((p.X - vol.Origin.X) * vol.InvVoxelSize), 0, vol.Nx - 1);
            int cy = Math.Clamp((int)MathF.Floor((p.Y - vol.Origin.Y) * vol.InvVoxelSize), 0, vol.Ny - 1);
            int cz = Math.Clamp((int)MathF.Floor((p.Z - vol.Origin.Z) * vol.InvVoxelSize), 0, vol.Nz - 1);
            int donor = nearestSolvedGrid[vol.Flat(cx, cy, cz)];
            if (donor >= 0)
            {
                for (int b = 0; b < nb; b++) outWeights[b] = heat[b][donor];
            }
        }

        /// <summary>
        /// Integer 3D Amanatidesâ€“Woo DDA ray march from solid voxel (vx,vy,vz) to target point.
        /// Returns true if the ray never crosses an exterior air cell (unobstructed interior visibility).
        /// </summary>
        private static bool IsVoxelRayUnobstructed(VoxelVolume vol, int vx, int vy, int vz, Vector3 targetWorld)
        {
            int tx = Math.Clamp((int)MathF.Floor((targetWorld.X - vol.Origin.X) * vol.InvVoxelSize), 0, vol.Nx - 1);
            int ty = Math.Clamp((int)MathF.Floor((targetWorld.Y - vol.Origin.Y) * vol.InvVoxelSize), 0, vol.Ny - 1);
            int tz = Math.Clamp((int)MathF.Floor((targetWorld.Z - vol.Origin.Z) * vol.InvVoxelSize), 0, vol.Nz - 1);

            if (vx == tx && vy == ty && vz == tz) return true;

            int dx = tx - vx, dy = ty - vy, dz = tz - vz;
            int stepX = dx > 0 ? 1 : (dx < 0 ? -1 : 0);
            int stepY = dy > 0 ? 1 : (dy < 0 ? -1 : 0);
            int stepZ = dz > 0 ? 1 : (dz < 0 ? -1 : 0);

            float tDeltaX = dx != 0 ? MathF.Abs(1f / dx) : float.PositiveInfinity;
            float tDeltaY = dy != 0 ? MathF.Abs(1f / dy) : float.PositiveInfinity;
            float tDeltaZ = dz != 0 ? MathF.Abs(1f / dz) : float.PositiveInfinity;

            float tMaxX = dx != 0 ? 0.5f * tDeltaX : float.PositiveInfinity;
            float tMaxY = dy != 0 ? 0.5f * tDeltaY : float.PositiveInfinity;
            float tMaxZ = dz != 0 ? 0.5f * tDeltaZ : float.PositiveInfinity;

            int cx = vx, cy = vy, cz = vz;
            int maxSteps = Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz) + 1;
            int nx = vol.Nx, ny = vol.Ny;
            var state = vol.State;

            for (int step = 0; step < maxSteps; step++)
            {
                if (cx == tx && cy == ty && cz == tz) return true;

                if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
                {
                    cx += stepX;
                    tMaxX += tDeltaX;
                }
                else if (tMaxY <= tMaxZ)
                {
                    cy += stepY;
                    tMaxY += tDeltaY;
                }
                else
                {
                    cz += stepZ;
                    tMaxZ += tDeltaZ;
                }

                // Allow the final target voxel itself to sit on the surface/edge if the bone is slightly outside,
                // but reject any intermediate exterior air cell!
                if (cx == tx && cy == ty && cz == tz) return true;
                if (state[(cz * ny + cy) * nx + cx] == CellExteriorAir)
                    return false;
            }

            return true;
        }

        private static void ExpandBounds(ref Vector3 min, ref Vector3 max, Vector3 p)
        {
            if (p.X < min.X) min.X = p.X; if (p.Y < min.Y) min.Y = p.Y; if (p.Z < min.Z) min.Z = p.Z;
            if (p.X > max.X) max.X = p.X; if (p.Y > max.Y) max.Y = p.Y; if (p.Z > max.Z) max.Z = p.Z;
        }

        private static Vector3 ClosestPointOnSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float lenSq = ab.LengthSquared();
            if (lenSq < 1e-10f) return a;
            float t = Mathf.Clamp((p - a).Dot(ab) / lenSq, 0f, 1f);
            return a + ab * t;
        }

        /// <summary>Exact squared Euclidean distance from point P to triangle (A, B, C) (Ericson, Real-Time Collision Detection).</summary>
        private static float PointTriangleDistSq(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a;
            Vector3 ac = c - a;
            Vector3 ap = p - a;
            float d1 = ab.Dot(ap);
            float d2 = ac.Dot(ap);
            if (d1 <= 0f && d2 <= 0f) return ap.LengthSquared();

            Vector3 bp = p - b;
            float d3 = ab.Dot(bp);
            float d4 = ac.Dot(bp);
            if (d3 >= 0f && d4 <= d3) return bp.LengthSquared();

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 / (d1 - d3);
                return (p - (a + ab * v)).LengthSquared();
            }

            Vector3 cp = p - c;
            float d5 = ab.Dot(cp);
            float d6 = ac.Dot(cp);
            if (d6 >= 0f && d5 <= d6) return cp.LengthSquared();

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 / (d2 - d6);
                return (p - (a + ac * w)).LengthSquared();
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                return (p - (b + (c - b) * w)).LengthSquared();
            }

            float denom = 1f / (va + vb + vc);
            float v2 = vb * denom;
            float w2 = vc * denom;
            return (p - (a + ab * v2 + ac * w2)).LengthSquared();
        }
    }
}
