using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace DeadlockPlayground.Importer
{
    public enum MeshCutTool
    {
        Lasso = 0,
        Box = 1,
        Brush = 2,
        Island = 3,
        BoneRegion = 4
    }

    public enum MeshCutSelectOp
    {
        Replace = 0,
        Add = 1,
        Subtract = 2
    }

    public enum MeshCutReferenceMode
    {
        FitCheck = 0,
        GhostOthers = 1,
        IsolateTarget = 2
    }

    /// <summary>
    /// Core geometry, topology, selection, undo/redo, and RLE mask engine for the
    /// interactive 3D Mesh Cut / Trim Editor inside the Custom 3D Model Importer.
    /// Operates purely on triangle index masks so original vertices, normals, UVs,
    /// tangents, blend shapes, and 4-bone / 8-bone skinning weights remain 100% intact.
    /// </summary>
    public static class MeshCutEditorCore
    {
        public sealed class SurfaceCutSessionData
        {
            public string SurfaceKey = "";
            public string DisplayName = "";
            public bool IsOriginalHeroMesh;
            public MeshInstance3D TargetMeshInstance;
            public int SurfaceIndex;
            public Node3D HeroRoot;
            public Skeleton3D Skeleton;
            public Skin Skin;

            public Vector3[] RestVerts = Array.Empty<Vector3>();
            public Vector3[] RestNorms = Array.Empty<Vector3>();
            public Vector3[] PosedVerts = Array.Empty<Vector3>();
            public Vector3[] PosedNorms = Array.Empty<Vector3>();
            public int[] RawTriIndices = Array.Empty<int>();
            public int TriCount => RawTriIndices.Length / 3;

            public int[] RawSkinBones;
            public float[] RawSkinWeights;
            public Mesh.ArrayFormat SourceFormatFlags;
            public int BoneStride = 4; // 4 or 8
            public int[] SkelBoneIndices; // vCount * BoneStride (resolved to Skeleton3D bone index)
            public float[] SkelBoneWeights; // vCount * BoneStride

            public int[][] TriNeighbors = Array.Empty<int[]>();
            public int[] TriIslandIds = Array.Empty<int>();
            public int IslandCount;
            public int[] TriDominantBone = Array.Empty<int>();

            public Vector3[] TriCentroidsPosed = Array.Empty<Vector3>();
            public Vector3[] TriCentroidsHero = Array.Empty<Vector3>();
            public Vector3[] TriNormalsPosed = Array.Empty<Vector3>();
            public Vector3[] TriNormalsHero = Array.Empty<Vector3>();
            public float[] TriRadius = Array.Empty<float>();

            public HashSet<int> CutTriangles = new();
            public HashSet<int> SelectedTriangles = new();
        }

        public sealed class HistorySnapshot
        {
            public string Label { get; init; } = "";
            public int[] CutRle { get; init; } = Array.Empty<int>();
            public int[] SelectedRle { get; init; } = Array.Empty<int>();
        }

        public sealed class MeshCutHistory
        {
            private readonly List<HistorySnapshot> _undo = new();
            private readonly List<HistorySnapshot> _redo = new();
            public int MaxDepth { get; set; } = 60;

            public bool CanUndo => _undo.Count > 0;
            public bool CanRedo => _redo.Count > 0;
            public string NextUndoLabel => _undo.Count > 0 ? _undo[^1].Label : "";
            public string NextRedoLabel => _redo.Count > 0 ? _redo[^1].Label : "";
            public int UndoCount => _undo.Count;
            public int RedoCount => _redo.Count;

            public void Clear()
            {
                _undo.Clear();
                _redo.Clear();
            }

            public void RecordBeforeMutation(string label, HashSet<int> currentCut, HashSet<int> currentSelected)
            {
                _undo.Add(new HistorySnapshot
                {
                    Label = label ?? "Edit",
                    CutRle = EncodeCutMask(currentCut),
                    SelectedRle = EncodeCutMask(currentSelected)
                });
                if (_undo.Count > MaxDepth)
                    _undo.RemoveAt(0);
                _redo.Clear();
            }

            public bool TryUndo(HashSet<int> cutTarget, HashSet<int> selectedTarget, int maxTriCount, out string undoneLabel)
            {
                undoneLabel = "";
                if (_undo.Count == 0) return false;

                var snap = _undo[^1];
                _undo.RemoveAt(_undo.Count - 1);

                _redo.Add(new HistorySnapshot
                {
                    Label = snap.Label,
                    CutRle = EncodeCutMask(cutTarget),
                    SelectedRle = EncodeCutMask(selectedTarget)
                });

                cutTarget.Clear();
                foreach (int t in DecodeCutMask(snap.CutRle, maxTriCount)) cutTarget.Add(t);
                selectedTarget.Clear();
                foreach (int t in DecodeCutMask(snap.SelectedRle, maxTriCount)) selectedTarget.Add(t);
                undoneLabel = snap.Label;
                return true;
            }

            public bool TryRedo(HashSet<int> cutTarget, HashSet<int> selectedTarget, int maxTriCount, out string redoneLabel)
            {
                redoneLabel = "";
                if (_redo.Count == 0) return false;

                var snap = _redo[^1];
                _redo.RemoveAt(_redo.Count - 1);

                _undo.Add(new HistorySnapshot
                {
                    Label = snap.Label,
                    CutRle = EncodeCutMask(cutTarget),
                    SelectedRle = EncodeCutMask(selectedTarget)
                });

                cutTarget.Clear();
                foreach (int t in DecodeCutMask(snap.CutRle, maxTriCount)) cutTarget.Add(t);
                selectedTarget.Clear();
                foreach (int t in DecodeCutMask(snap.SelectedRle, maxTriCount)) selectedTarget.Add(t);
                redoneLabel = snap.Label;
                return true;
            }
        }

        // =========================================================================
        // RLE Cut Mask Serialization & Index Filtering
        // =========================================================================

        /// <summary>Encodes sorted triangle indices into compact [start, runLen, ...] RLE pairs.</summary>
        public static int[] EncodeCutMask(IEnumerable<int> cutTriangles)
        {
            if (cutTriangles == null) return Array.Empty<int>();
            var sorted = cutTriangles.Where(t => t >= 0).Distinct().OrderBy(t => t).ToArray();
            if (sorted.Length == 0) return Array.Empty<int>();

            var rle = new List<int>(Math.Min(sorted.Length * 2, 128));
            int runStart = sorted[0];
            int runLen = 1;
            for (int i = 1; i < sorted.Length; i++)
            {
                if (sorted[i] == runStart + runLen)
                {
                    runLen++;
                }
                else
                {
                    rle.Add(runStart);
                    rle.Add(runLen);
                    runStart = sorted[i];
                    runLen = 1;
                }
            }
            rle.Add(runStart);
            rle.Add(runLen);
            return rle.ToArray();
        }

        public static HashSet<int> DecodeCutMask(int[] rle, int maxTriCount = int.MaxValue)
        {
            var set = new HashSet<int>();
            if (rle == null || rle.Length < 2) return set;
            for (int i = 0; i + 1 < rle.Length; i += 2)
            {
                int start = rle[i];
                int len = rle[i + 1];
                if (start < 0 || len <= 0) continue;
                int end = (int)Math.Min((long)start + len, maxTriCount);
                for (int t = start; t < end; t++)
                    set.Add(t);
            }
            return set;
        }

        public static int CountCutTriangles(int[] rle)
        {
            if (rle == null || rle.Length < 2) return 0;
            int total = 0;
            for (int i = 0; i + 1 < rle.Length; i += 2)
            {
                if (rle[i] >= 0 && rle[i + 1] > 0)
                    total += rle[i + 1];
            }
            return total;
        }

        public static int[] UnionRleMasks(IEnumerable<int[]> masks, int maxTriCount = int.MaxValue)
        {
            var merged = new HashSet<int>();
            if (masks != null)
            {
                foreach (var m in masks)
                {
                    if (m == null || m.Length < 2) continue;
                    for (int i = 0; i + 1 < m.Length; i += 2)
                    {
                        int start = m[i];
                        int len = m[i + 1];
                        if (start < 0 || len <= 0) continue;
                        int end = (int)Math.Min((long)start + len, maxTriCount);
                        for (int t = start; t < end; t++)
                            merged.Add(t);
                    }
                }
            }
            return EncodeCutMask(merged);
        }

        /// <summary>
        /// Filters triangle indices by excluding cut triangle IDs.
        /// If 100% of triangles are cut, emits a single degenerate [0, 0, 0] triangle so Godot
        /// ArrayMesh.AddSurfaceFromArrays never throws and multi-surface indices never shift.
        /// </summary>
        public static int[] FilterTriangleIndices(
            int[] rawIndices,
            int vertexCount,
            HashSet<int> cutTris,
            out int keptTriCount,
            out int totalTriCount)
        {
            if (rawIndices == null || rawIndices.Length < 3)
            {
                int triCnt = vertexCount / 3;
                rawIndices = new int[triCnt * 3];
                for (int i = 0; i < rawIndices.Length; i++) rawIndices[i] = i;
            }

            totalTriCount = rawIndices.Length / 3;
            if (cutTris == null || cutTris.Count == 0)
            {
                keptTriCount = totalTriCount;
                return rawIndices;
            }

            var kept = new List<int>(rawIndices.Length);
            int keptCnt = 0;
            for (int t = 0; t < totalTriCount; t++)
            {
                if (cutTris.Contains(t)) continue;
                int o = t * 3;
                kept.Add(rawIndices[o]);
                kept.Add(rawIndices[o + 1]);
                kept.Add(rawIndices[o + 2]);
                keptCnt++;
            }

            keptTriCount = keptCnt;
            if (keptCnt == 0)
            {
                return vertexCount > 0 ? new[] { 0, 0, 0 } : Array.Empty<int>();
            }
            return kept.ToArray();
        }

        /// <summary>
        /// Strips degenerate triangles (where two or more corners reference the same vertex index),
        /// used during Source 2 SMD export so 100%-cut placeholder surfaces emit 0 triangles.
        /// </summary>
        public static int[] StripDegenerateTriangles(int[] indices)
        {
            if (indices == null || indices.Length < 3) return Array.Empty<int>();
            bool anyDegen = false;
            int triCount = indices.Length / 3;
            for (int t = 0; t < triCount; t++)
            {
                int i0 = indices[t * 3];
                int i1 = indices[t * 3 + 1];
                int i2 = indices[t * 3 + 2];
                if (i0 == i1 || i1 == i2 || i2 == i0)
                {
                    anyDegen = true;
                    break;
                }
            }
            if (!anyDegen) return indices;

            var clean = new List<int>(indices.Length);
            for (int t = 0; t < triCount; t++)
            {
                int i0 = indices[t * 3];
                int i1 = indices[t * 3 + 1];
                int i2 = indices[t * 3 + 2];
                if (i0 == i1 || i1 == i2 || i2 == i0) continue;
                clean.Add(i0);
                clean.Add(i1);
                clean.Add(i2);
            }
            return clean.ToArray();
        }

        // =========================================================================
        // Surface Capture & Topology Construction
        // =========================================================================

        public static SurfaceCutSessionData CaptureSurface(
            MeshInstance3D mi,
            int surfaceIndex,
            string surfaceKey,
            string displayName,
            bool isOriginalHeroMesh,
            Node3D heroRoot,
            Skeleton3D skeleton,
            int[] existingCutMaskRle = null)
        {
            if (mi == null || !GodotObject.IsInstanceValid(mi) || mi.Mesh == null) return null;

            // Always read the pristine triangle index buffer from OriginalPreDeformMesh / OriginalPreCutMesh
            // so previously cut triangles are still selectable and restorable!
            Mesh currentMesh = mi.Mesh;
            Mesh pristineMesh = currentMesh;
            if (mi.HasMeta("OriginalPreDeformMesh"))
            {
                var orig = mi.GetMeta("OriginalPreDeformMesh").As<Mesh>();
                if (orig != null && surfaceIndex < orig.GetSurfaceCount())
                    pristineMesh = orig;
            }
            else if (mi.HasMeta("OriginalPreCutMesh"))
            {
                var orig = mi.GetMeta("OriginalPreCutMesh").As<Mesh>();
                if (orig != null && surfaceIndex < orig.GetSurfaceCount())
                    pristineMesh = orig;
            }

            if (surfaceIndex < 0 || surfaceIndex >= pristineMesh.GetSurfaceCount()) return null;

            var pristineArrays = pristineMesh.SurfaceGetArrays(surfaceIndex);
            if (pristineArrays == null || pristineArrays.Count <= (int)Mesh.ArrayType.Vertex) return null;

            var pristineVerts = CustomModelAutoRigger.GetVector3Array(pristineArrays[(int)Mesh.ArrayType.Vertex]);
            if (pristineVerts == null || pristineVerts.Length == 0) return null;

            // Use currentMesh vertices/normals if vertex count matches so existing deform/sculpt/reposition shows accurately
            Vector3[] verts = pristineVerts;
            Vector3[] norms = pristineArrays.Count > (int)Mesh.ArrayType.Normal
                ? CustomModelAutoRigger.GetVector3Array(pristineArrays[(int)Mesh.ArrayType.Normal])
                : null;

            if (!ReferenceEquals(currentMesh, pristineMesh) && surfaceIndex < currentMesh.GetSurfaceCount())
            {
                var curArrays = currentMesh.SurfaceGetArrays(surfaceIndex);
                if (curArrays != null && curArrays.Count > (int)Mesh.ArrayType.Vertex)
                {
                    var curVerts = CustomModelAutoRigger.GetVector3Array(curArrays[(int)Mesh.ArrayType.Vertex]);
                    if (curVerts != null && curVerts.Length == pristineVerts.Length)
                    {
                        verts = curVerts;
                        var curNorms = curArrays.Count > (int)Mesh.ArrayType.Normal
                            ? CustomModelAutoRigger.GetVector3Array(curArrays[(int)Mesh.ArrayType.Normal])
                            : null;
                        if (curNorms != null && curNorms.Length == verts.Length)
                            norms = curNorms;
                    }
                }
            }

            if (norms == null || norms.Length != verts.Length)
            {
                norms = new Vector3[verts.Length];
                Array.Fill(norms, Vector3.Up);
            }

            int[] rawIndices = pristineArrays.Count > (int)Mesh.ArrayType.Index
                ? CustomModelAutoRigger.GetInt32Array(pristineArrays[(int)Mesh.ArrayType.Index])
                : null;
            if (rawIndices == null || rawIndices.Length < 3)
            {
                int triCnt = verts.Length / 3;
                rawIndices = new int[triCnt * 3];
                for (int i = 0; i < rawIndices.Length; i++) rawIndices[i] = i;
            }

            // Capture skinning arrays (supports both 4-bone and 8-bone meshes)
            int[] rawBones = pristineArrays.Count > (int)Mesh.ArrayType.Bones
                ? CustomModelAutoRigger.GetInt32Array(pristineArrays[(int)Mesh.ArrayType.Bones])
                : null;
            float[] rawWeights = pristineArrays.Count > (int)Mesh.ArrayType.Weights
                ? CustomModelAutoRigger.GetFloat32Array(pristineArrays[(int)Mesh.ArrayType.Weights])
                : null;

            int boneStride = 4;
            Mesh.ArrayFormat fmt = (pristineMesh is ArrayMesh srcArr) ? srcArr.SurfaceGetFormat(surfaceIndex) : default;
            if (rawBones != null && rawBones.Length == verts.Length * 8)
            {
                boneStride = 8;
                fmt |= Mesh.ArrayFormat.FlagUse8BoneWeights;
            }

            // Resolve skin bind indices -> Skeleton3D bone indices
            int[] skelBones = null;
            float[] skelWeights = null;
            if (rawBones != null && rawWeights != null &&
                rawBones.Length >= verts.Length * boneStride &&
                rawWeights.Length >= verts.Length * boneStride)
            {
                skelBones = new int[verts.Length * boneStride];
                skelWeights = (float[])rawWeights.Clone();
                var bindToSkel = BuildSkinBindToSkeletonMap(skeleton, mi.Skin);
                int boneCount = skeleton != null && GodotObject.IsInstanceValid(skeleton) ? skeleton.GetBoneCount() : 0;

                for (int i = 0; i < skelBones.Length; i++)
                {
                    int bIdx = rawBones[i];
                    if (bindToSkel.Count > 0)
                    {
                        skelBones[i] = bindToSkel.TryGetValue(bIdx, out int mapped) ? mapped : -1;
                    }
                    else
                    {
                        skelBones[i] = (bIdx >= 0 && bIdx < boneCount) ? bIdx : -1;
                    }
                }
            }

            int triCount = rawIndices.Length / 3;
            BuildTriangleTopology(verts, rawIndices, out var triNeighbors, out var triIslands, out int islandCount);

            var triDomBone = new int[triCount];
            Array.Fill(triDomBone, -1);
            if (skelBones != null && skelWeights != null)
            {
                var boneAccum = new Dictionary<int, float>(12);
                for (int t = 0; t < triCount; t++)
                {
                    boneAccum.Clear();
                    int o = t * 3;
                    for (int c = 0; c < 3; c++)
                    {
                        int v = rawIndices[o + c];
                        if ((uint)v >= (uint)verts.Length) continue;
                        int vOff = v * boneStride;
                        for (int k = 0; k < boneStride; k++)
                        {
                            float w = skelWeights[vOff + k];
                            int sb = skelBones[vOff + k];
                            if (w > 0.001f && sb >= 0)
                                boneAccum[sb] = boneAccum.GetValueOrDefault(sb, 0f) + w;
                        }
                    }
                    int bestBone = -1;
                    float bestW = 0f;
                    foreach (var (b, w) in boneAccum)
                    {
                        if (w > bestW)
                        {
                            bestW = w;
                            bestBone = b;
                        }
                    }
                    triDomBone[t] = bestBone;
                }
            }

            var data = new SurfaceCutSessionData
            {
                SurfaceKey = surfaceKey ?? "",
                DisplayName = displayName ?? mi.Name.ToString(),
                IsOriginalHeroMesh = isOriginalHeroMesh,
                TargetMeshInstance = mi,
                SurfaceIndex = surfaceIndex,
                HeroRoot = heroRoot,
                Skeleton = skeleton,
                Skin = mi.Skin,
                RestVerts = (Vector3[])verts.Clone(),
                RestNorms = (Vector3[])norms.Clone(),
                PosedVerts = (Vector3[])verts.Clone(),
                PosedNorms = (Vector3[])norms.Clone(),
                RawTriIndices = (int[])rawIndices.Clone(),
                RawSkinBones = rawBones != null ? (int[])rawBones.Clone() : null,
                RawSkinWeights = rawWeights != null ? (float[])rawWeights.Clone() : null,
                SourceFormatFlags = fmt,
                BoneStride = boneStride,
                SkelBoneIndices = skelBones,
                SkelBoneWeights = skelWeights,
                TriNeighbors = triNeighbors,
                TriIslandIds = triIslands,
                IslandCount = islandCount,
                TriDominantBone = triDomBone,
                TriCentroidsPosed = new Vector3[triCount],
                TriCentroidsHero = new Vector3[triCount],
                TriNormalsPosed = new Vector3[triCount],
                TriNormalsHero = new Vector3[triCount],
                TriRadius = new float[triCount],
                CutTriangles = DecodeCutMask(existingCutMaskRle, triCount),
                SelectedTriangles = new HashSet<int>()
            };

            UpdatePosedGeometry(data);
            return data;
        }

        private static Dictionary<int, int> BuildSkinBindToSkeletonMap(Skeleton3D skeleton, Skin skin)
        {
            var map = new Dictionary<int, int>();
            if (skeleton == null || !GodotObject.IsInstanceValid(skeleton) ||
                skin == null || !GodotObject.IsInstanceValid(skin))
                return map;

            int boneCount = skeleton.GetBoneCount();
            int bindCount = skin.GetBindCount();
            for (int bi = 0; bi < bindCount; bi++)
            {
                int b = skin.GetBindBone(bi);
                if (b < 0 || b >= boneCount)
                {
                    string nm = skin.GetBindName(bi);
                    if (!string.IsNullOrEmpty(nm)) b = skeleton.FindBone(nm);
                }
                if (b >= 0 && b < boneCount)
                    map[bi] = b;
            }
            return map;
        }

        private static void BuildTriangleTopology(
            Vector3[] verts,
            int[] indices,
            out int[][] triNeighbors,
            out int[] triIslandIds,
            out int islandCount)
        {
            int vCount = verts.Length;
            int triCount = indices.Length / 3;

            // 1mm spatial welding so UV seams never artificially split topological islands or Grow/Shrink rings
            const float cell = 0.001f;
            var cellToClusterId = new Dictionary<(long, long, long), int>(vCount);
            var vertClusterId = new int[vCount];
            int nextCluster = 0;
            for (int v = 0; v < vCount; v++)
            {
                var key = (
                    (long)MathF.Round(verts[v].X / cell),
                    (long)MathF.Round(verts[v].Y / cell),
                    (long)MathF.Round(verts[v].Z / cell));
                if (!cellToClusterId.TryGetValue(key, out int cid))
                {
                    cid = nextCluster++;
                    cellToClusterId[key] = cid;
                }
                vertClusterId[v] = cid;
            }

            // Map each welded vertex cluster -> list of incident triangles
            var clusterTris = new List<int>[nextCluster];
            for (int t = 0; t < triCount; t++)
            {
                int o = t * 3;
                int i0 = indices[o], i1 = indices[o + 1], i2 = indices[o + 2];
                if ((uint)i0 >= (uint)vCount || (uint)i1 >= (uint)vCount || (uint)i2 >= (uint)vCount) continue;
                int c0 = vertClusterId[i0], c1 = vertClusterId[i1], c2 = vertClusterId[i2];
                (clusterTris[c0] ??= new List<int>(6)).Add(t);
                if (c1 != c0) (clusterTris[c1] ??= new List<int>(6)).Add(t);
                if (c2 != c0 && c2 != c1) (clusterTris[c2] ??= new List<int>(6)).Add(t);
            }

            triNeighbors = new int[triCount][];
            var nbSet = new HashSet<int>(16);
            for (int t = 0; t < triCount; t++)
            {
                nbSet.Clear();
                int o = t * 3;
                int i0 = indices[o], i1 = indices[o + 1], i2 = indices[o + 2];
                if ((uint)i0 < (uint)vCount && (uint)i1 < (uint)vCount && (uint)i2 < (uint)vCount)
                {
                    void AddFromCluster(int cid)
                    {
                        var list = clusterTris[cid];
                        if (list == null) return;
                        for (int j = 0; j < list.Count; j++)
                        {
                            int nt = list[j];
                            if (nt != t) nbSet.Add(nt);
                        }
                    }
                    AddFromCluster(vertClusterId[i0]);
                    AddFromCluster(vertClusterId[i1]);
                    AddFromCluster(vertClusterId[i2]);
                }
                triNeighbors[t] = nbSet.Count > 0 ? nbSet.ToArray() : Array.Empty<int>();
            }

            // Connected-component triangle islands via BFS
            triIslandIds = new int[triCount];
            Array.Fill(triIslandIds, -1);
            islandCount = 0;
            var q = new Queue<int>();
            for (int t = 0; t < triCount; t++)
            {
                if (triIslandIds[t] >= 0) continue;
                int id = islandCount++;
                triIslandIds[t] = id;
                q.Enqueue(t);
                while (q.Count > 0)
                {
                    int cur = q.Dequeue();
                    var nbs = triNeighbors[cur];
                    for (int j = 0; j < nbs.Length; j++)
                    {
                        int nt = nbs[j];
                        if (triIslandIds[nt] < 0)
                        {
                            triIslandIds[nt] = id;
                            q.Enqueue(nt);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Evaluates current world-space posed vertex &amp; triangle geometry via CPU LBS
        /// (supports both 4-bone and 8-bone skinned meshes, plus unrigged meshes).
        /// </summary>
        public static void UpdatePosedGeometry(SurfaceCutSessionData s)
        {
            if (s == null || s.RestVerts.Length == 0) return;
            int vc = s.RestVerts.Length;
            Transform3D miGlobal = (s.TargetMeshInstance != null && GodotObject.IsInstanceValid(s.TargetMeshInstance) && s.TargetMeshInstance.IsInsideTree())
                ? s.TargetMeshInstance.GlobalTransform
                : Transform3D.Identity;
            Transform3D heroInv = (s.HeroRoot != null && GodotObject.IsInstanceValid(s.HeroRoot) && s.HeroRoot.IsInsideTree())
                ? s.HeroRoot.GlobalTransform.AffineInverse()
                : Transform3D.Identity;

            bool isSkinned = s.Skeleton != null && GodotObject.IsInstanceValid(s.Skeleton) &&
                             s.Skin != null && GodotObject.IsInstanceValid(s.Skin) &&
                             s.RawSkinBones != null && s.RawSkinWeights != null &&
                             s.RawSkinBones.Length >= vc * s.BoneStride;

            if (isSkinned)
            {
                int bindCount = s.Skin.GetBindCount();
                int boneCount = s.Skeleton.GetBoneCount();
                Transform3D skelGlobal = s.Skeleton.IsInsideTree() ? s.Skeleton.GlobalTransform : Transform3D.Identity;
                var bindMats = new Transform3D[bindCount];
                for (int bi = 0; bi < bindCount; bi++)
                {
                    int b = s.Skin.GetBindBone(bi);
                    if (b < 0 || b >= boneCount)
                    {
                        string nm = s.Skin.GetBindName(bi);
                        if (!string.IsNullOrEmpty(nm)) b = s.Skeleton.FindBone(nm);
                    }
                    Transform3D pose = (b >= 0 && b < boneCount) ? s.Skeleton.GetBoneGlobalPose(b) : Transform3D.Identity;
                    bindMats[bi] = skelGlobal * pose * s.Skin.GetBindPose(bi);
                }

                int stride = s.BoneStride;
                for (int v = 0; v < vc; v++)
                {
                    Vector3 rv = s.RestVerts[v];
                    Vector3 rn = s.RestNorms[v];
                    Vector3 acc = Vector3.Zero;
                    Vector3 accN = Vector3.Zero;
                    float sumW = 0f;
                    int off = v * stride;
                    for (int k = 0; k < stride; k++)
                    {
                        float w = s.RawSkinWeights[off + k];
                        if (w <= 0.0001f) continue;
                        int bi = s.RawSkinBones[off + k];
                        if (bi < 0 || bi >= bindCount) continue;
                        acc += (bindMats[bi] * rv) * w;
                        accN += (bindMats[bi].Basis * rn) * w;
                        sumW += w;
                    }
                    s.PosedVerts[v] = sumW > 0.0001f ? acc / sumW : (skelGlobal * rv);
                    s.PosedNorms[v] = accN.LengthSquared() > 1e-8f ? accN.Normalized() : (skelGlobal.Basis * rn).Normalized();
                }
            }
            else
            {
                Basis normBasis = MathF.Abs(miGlobal.Basis.Determinant()) > 1e-8f
                    ? miGlobal.Basis.Inverse().Transposed()
                    : miGlobal.Basis;
                for (int v = 0; v < vc; v++)
                {
                    s.PosedVerts[v] = miGlobal * s.RestVerts[v];
                    Vector3 n = normBasis * s.RestNorms[v];
                    s.PosedNorms[v] = n.LengthSquared() > 1e-8f ? n.Normalized() : Vector3.Up;
                }
            }

            int triCount = s.TriCount;
            for (int t = 0; t < triCount; t++)
            {
                int o = t * 3;
                int i0 = s.RawTriIndices[o], i1 = s.RawTriIndices[o + 1], i2 = s.RawTriIndices[o + 2];
                if ((uint)i0 >= (uint)vc || (uint)i1 >= (uint)vc || (uint)i2 >= (uint)vc) continue;
                Vector3 a = s.PosedVerts[i0], b = s.PosedVerts[i1], c = s.PosedVerts[i2];
                Vector3 centroid = (a + b + c) * (1.0f / 3.0f);
                s.TriCentroidsPosed[t] = centroid;
                s.TriCentroidsHero[t] = heroInv * centroid;
                // Godot glTF ArrayMesh triangles use CW winding so (c - a).Cross(b - a) points outward;
                // align sign with average posed vertex normal so both CW and CCW meshes are strictly outward.
                Vector3 fn = (c - a).Cross(b - a);
                Vector3 avgVn = s.PosedNorms[i0] + s.PosedNorms[i1] + s.PosedNorms[i2];
                if (fn.LengthSquared() > 1e-10f)
                {
                    fn = fn.Normalized();
                    if (avgVn.LengthSquared() > 1e-8f && fn.Dot(avgVn) < 0f)
                        fn = -fn;
                }
                else
                {
                    fn = avgVn.LengthSquared() > 1e-8f ? avgVn.Normalized() : Vector3.Up;
                }
                s.TriNormalsPosed[t] = fn;
                Vector3 fnHero = heroInv.Basis * fn;
                s.TriNormalsHero[t] = fnHero.LengthSquared() > 1e-8f ? fnHero.Normalized() : fn;
                float r0 = (a - centroid).Length();
                float r1 = (b - centroid).Length();
                float r2 = (c - centroid).Length();
                s.TriRadius[t] = MathF.Max(r0, MathF.Max(r1, r2));
            }
        }

        // =========================================================================
        // Raycasting & Selection Tools
        // =========================================================================

        public static bool RaycastTriangle(
            SurfaceCutSessionData s,
            Vector3 rayOrigin,
            Vector3 rayDir,
            bool restoreMode,
            out int hitTri,
            out Vector3 hitPointWorld,
            out Vector3 hitNormalWorld)
        {
            hitTri = -1;
            hitPointWorld = Vector3.Zero;
            hitNormalWorld = Vector3.Up;
            if (s == null) return false;

            float bestT = float.MaxValue;
            int triCount = s.TriCount;
            var verts = s.PosedVerts;
            var idx = s.RawTriIndices;

            for (int t = 0; t < triCount; t++)
            {
                bool isCut = s.CutTriangles.Contains(t);
                if (restoreMode ? !isCut : isCut) continue;

                int o = t * 3;
                int i0 = idx[o], i1 = idx[o + 1], i2 = idx[o + 2];
                if ((uint)i0 >= (uint)verts.Length || (uint)i1 >= (uint)verts.Length || (uint)i2 >= (uint)verts.Length) continue;
                Vector3 a = verts[i0], b = verts[i1], c = verts[i2];
                if (CustomWeightPaintEditor.RayTriangle(rayOrigin, rayDir, a, b, c, out float hitT) && hitT < bestT)
                {
                    bestT = hitT;
                    hitTri = t;
                    hitPointWorld = rayOrigin + rayDir * hitT;
                    hitNormalWorld = s.TriNormalsPosed[t];
                }
            }
            return hitTri >= 0;
        }

        public static bool PointInPolygon2D(Vector2 pt, Vector2[] poly)
        {
            if (poly == null || poly.Length < 3) return false;
            bool inside = false;
            int j = poly.Length - 1;
            for (int i = 0; i < poly.Length; j = i++)
            {
                Vector2 pi = poly[i];
                Vector2 pj = poly[j];
                bool intersect = ((pi.Y > pt.Y) != (pj.Y > pt.Y)) &&
                                 (pt.X < (pj.X - pi.X) * (pt.Y - pi.Y) / ((pj.Y - pi.Y) + 1e-12f) + pi.X);
                if (intersect) inside = !inside;
            }
            return inside;
        }

        private static bool IsFrontFacing(SurfaceCutSessionData s, int triIdx, Camera3D cam)
        {
            if (cam == null) return true;
            Vector3 toCam = cam.GlobalPosition - s.TriCentroidsPosed[triIdx];
            if (toCam.LengthSquared() < 1e-8f) return true;
            // Accept slightly grazing angles (-0.05) so silhouette triangles remain easy to catch
            return s.TriNormalsPosed[triIdx].Dot(toCam.Normalized()) >= -0.05f;
        }

        private static Vector3 MirrorWorldPointAcrossHeroX(SurfaceCutSessionData s, Vector3 heroPt)
        {
            Transform3D heroGlobal = (s.HeroRoot != null && GodotObject.IsInstanceValid(s.HeroRoot) && s.HeroRoot.IsInsideTree())
                ? s.HeroRoot.GlobalTransform
                : Transform3D.Identity;
            return heroGlobal * new Vector3(-heroPt.X, heroPt.Y, heroPt.Z);
        }

        /// <summary>
        /// Camera-independent 3D Hero-Space X-symmetry mirroring: maps every primary selected triangle
        /// across the hero's X=0 symmetry plane onto its matching triangle(s) on the opposite side,
        /// preserving front-vs-back normal orientation so Mirror X works reliably from any camera angle
        /// (front, back, 3/4, side profile) for Lasso, Box, and Brush tools!
        /// </summary>
        private static void AddMirroredTriangles3D(
            SurfaceCutSessionData s,
            HashSet<int> primaryMatched,
            HashSet<int> outMatched,
            bool restoreMode)
        {
            if (s == null || primaryMatched == null || primaryMatched.Count == 0) return;

            int triCount = s.TriCount;
            const float cellSize = 0.035f;
            float invCell = 1.0f / cellSize;

            var grid = new Dictionary<(int, int, int), List<int>>(Math.Min(triCount, 4096));
            for (int t = 0; t < triCount; t++)
            {
                bool isCut = s.CutTriangles.Contains(t);
                if (restoreMode ? !isCut : isCut) continue;
                Vector3 c = s.TriCentroidsHero[t];
                var key = ((int)MathF.Floor(c.X * invCell), (int)MathF.Floor(c.Y * invCell), (int)MathF.Floor(c.Z * invCell));
                if (!grid.TryGetValue(key, out var bucket))
                {
                    bucket = new List<int>(6);
                    grid[key] = bucket;
                }
                bucket.Add(t);
            }

            foreach (int t in primaryMatched)
            {
                Vector3 srcPos = s.TriCentroidsHero[t];
                Vector3 mirPos = new Vector3(-srcPos.X, srcPos.Y, srcPos.Z);
                Vector3 srcNorm = t < s.TriNormalsHero.Length ? s.TriNormalsHero[t] : Vector3.Up;
                Vector3 mirNorm = new Vector3(-srcNorm.X, srcNorm.Y, srcNorm.Z);

                float triRad = (t < s.TriRadius.Length && s.TriRadius[t] > 0.001f) ? s.TriRadius[t] : 0.015f;
                float searchRad = MathF.Max(0.028f, triRad * 1.65f);
                float searchRadSq = searchRad * searchRad;
                float multiMatchRadSq = MathF.Pow(MathF.Max(0.010f, triRad * 0.85f), 2f);

                int cx = (int)MathF.Floor(mirPos.X * invCell);
                int cy = (int)MathF.Floor(mirPos.Y * invCell);
                int cz = (int)MathF.Floor(mirPos.Z * invCell);
                int span = Math.Clamp((int)MathF.Ceiling(searchRad * invCell), 1, 3);

                int bestTri = -1;
                float bestDSq = searchRadSq;
                for (int dx = -span; dx <= span; dx++)
                {
                    for (int dy = -span; dy <= span; dy++)
                    {
                        for (int dz = -span; dz <= span; dz++)
                        {
                            if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var bucket)) continue;
                            for (int bi = 0; bi < bucket.Count; bi++)
                            {
                                int u = bucket[bi];
                                float dSq = s.TriCentroidsHero[u].DistanceSquaredTo(mirPos);
                                if (dSq > searchRadSq) continue;
                                Vector3 uNorm = u < s.TriNormalsHero.Length ? s.TriNormalsHero[u] : Vector3.Up;
                                if (uNorm.Dot(mirNorm) < 0.20f) continue;

                                if (dSq <= multiMatchRadSq)
                                    outMatched.Add(u);
                                if (dSq < bestDSq)
                                {
                                    bestDSq = dSq;
                                    bestTri = u;
                                }
                            }
                        }
                    }
                }
                if (bestTri >= 0) outMatched.Add(bestTri);
            }
        }

        private static bool ApplyMatchedSet(HashSet<int> selected, HashSet<int> matched, MeshCutSelectOp op)
        {
            bool changed = false;
            switch (op)
            {
                case MeshCutSelectOp.Replace:
                    if (selected.Count != matched.Count || !selected.SetEquals(matched))
                    {
                        selected.Clear();
                        foreach (int t in matched) selected.Add(t);
                        changed = true;
                    }
                    break;
                case MeshCutSelectOp.Add:
                    foreach (int t in matched)
                        if (selected.Add(t)) changed = true;
                    break;
                case MeshCutSelectOp.Subtract:
                    foreach (int t in matched)
                        if (selected.Remove(t)) changed = true;
                    break;
            }
            return changed;
        }

        public static bool SelectByLasso(
            SurfaceCutSessionData s,
            Camera3D cam,
            Vector2[] polygon,
            MeshCutSelectOp op,
            bool xRay,
            bool mirrorX,
            bool restoreMode)
        {
            if (s == null || cam == null || polygon == null || polygon.Length < 3) return false;

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < polygon.Length; i++)
            {
                if (polygon[i].X < minX) minX = polygon[i].X;
                if (polygon[i].Y < minY) minY = polygon[i].Y;
                if (polygon[i].X > maxX) maxX = polygon[i].X;
                if (polygon[i].Y > maxY) maxY = polygon[i].Y;
            }

            var primaryMatched = new HashSet<int>();
            int triCount = s.TriCount;

            bool TestPoint(Vector3 worldPt)
            {
                if (cam.IsPositionBehind(worldPt)) return false;
                Vector2 sp = cam.UnprojectPosition(worldPt);
                if (sp.X < minX || sp.X > maxX || sp.Y < minY || sp.Y > maxY) return false;
                return PointInPolygon2D(sp, polygon);
            }

            for (int t = 0; t < triCount; t++)
            {
                bool isCut = s.CutTriangles.Contains(t);
                if (restoreMode ? !isCut : isCut) continue;

                if (!xRay && !IsFrontFacing(s, t, cam)) continue;
                if (TestPoint(s.TriCentroidsPosed[t]))
                    primaryMatched.Add(t);
            }

            var matched = new HashSet<int>(primaryMatched);
            if (mirrorX && primaryMatched.Count > 0)
            {
                AddMirroredTriangles3D(s, primaryMatched, matched, restoreMode);
            }

            return ApplyMatchedSet(s.SelectedTriangles, matched, op);
        }

        public static bool SelectByBox(
            SurfaceCutSessionData s,
            Camera3D cam,
            Rect2 rect,
            MeshCutSelectOp op,
            bool xRay,
            bool mirrorX,
            bool restoreMode)
        {
            if (s == null || cam == null) return false;
            Rect2 norm = rect.Abs();
            if (norm.Size.X < 2f && norm.Size.Y < 2f) return false;

            var primaryMatched = new HashSet<int>();
            int triCount = s.TriCount;

            bool TestPoint(Vector3 worldPt)
            {
                if (cam.IsPositionBehind(worldPt)) return false;
                Vector2 sp = cam.UnprojectPosition(worldPt);
                return norm.HasPoint(sp);
            }

            for (int t = 0; t < triCount; t++)
            {
                bool isCut = s.CutTriangles.Contains(t);
                if (restoreMode ? !isCut : isCut) continue;

                if (!xRay && !IsFrontFacing(s, t, cam)) continue;
                if (TestPoint(s.TriCentroidsPosed[t]))
                    primaryMatched.Add(t);
            }

            var matched = new HashSet<int>(primaryMatched);
            if (mirrorX && primaryMatched.Count > 0)
            {
                AddMirroredTriangles3D(s, primaryMatched, matched, restoreMode);
            }

            return ApplyMatchedSet(s.SelectedTriangles, matched, op);
        }

        public static bool SelectByBrush(
            SurfaceCutSessionData s,
            Camera3D cam,
            Vector3 hitPointWorld,
            Vector3 rayOrigin,
            Vector3 rayDir,
            float radius,
            MeshCutSelectOp op,
            bool xRay,
            bool mirrorX,
            bool restoreMode)
        {
            if (s == null || radius <= 0.001f) return false;

            float rSq = radius * radius;
            Vector3 rayDirNorm = rayDir.LengthSquared() > 1e-8f ? rayDir.Normalized() : Vector3.Forward;
            int triCount = s.TriCount;
            var primaryMatched = new HashSet<int>();

            for (int t = 0; t < triCount; t++)
            {
                bool isCut = s.CutTriangles.Contains(t);
                if (restoreMode ? !isCut : isCut) continue;

                if (xRay)
                {
                    Vector3 rel = s.TriCentroidsPosed[t] - rayOrigin;
                    float along = rel.Dot(rayDirNorm);
                    if (along > 0.01f)
                    {
                        Vector3 perp = rel - rayDirNorm * along;
                        if (perp.LengthSquared() <= rSq) primaryMatched.Add(t);
                    }
                }
                else
                {
                    if (s.TriCentroidsPosed[t].DistanceSquaredTo(hitPointWorld) <= rSq && IsFrontFacing(s, t, cam))
                        primaryMatched.Add(t);
                }
            }

            if (primaryMatched.Count == 0) return false;

            var matched = new HashSet<int>(primaryMatched);
            if (mirrorX)
            {
                AddMirroredTriangles3D(s, primaryMatched, matched, restoreMode);
            }

            bool changed = false;
            foreach (int t in matched)
            {
                if (op == MeshCutSelectOp.Subtract)
                {
                    if (s.SelectedTriangles.Remove(t)) changed = true;
                }
                else
                {
                    if (s.SelectedTriangles.Add(t)) changed = true;
                }
            }
            return changed;
        }

        public static bool SelectByIsland(
            SurfaceCutSessionData s,
            int hitTri,
            MeshCutSelectOp op,
            bool mirrorX,
            bool restoreMode)
        {
            if (s == null || hitTri < 0 || hitTri >= s.TriCount) return false;

            int targetIsland = s.TriIslandIds[hitTri];
            int mirrorIsland = -1;
            if (mirrorX)
            {
                Vector3 mirHero = new Vector3(-s.TriCentroidsHero[hitTri].X, s.TriCentroidsHero[hitTri].Y, s.TriCentroidsHero[hitTri].Z);
                int bestMirTri = -1;
                float bestDSq = 0.10f * 0.10f; // 10cm search threshold for symmetric island
                for (int t = 0; t < s.TriCount; t++)
                {
                    if (s.TriIslandIds[t] == targetIsland) continue;
                    bool isCut = s.CutTriangles.Contains(t);
                    if (restoreMode ? !isCut : isCut) continue;
                    float dSq = s.TriCentroidsHero[t].DistanceSquaredTo(mirHero);
                    if (dSq < bestDSq)
                    {
                        bestDSq = dSq;
                        bestMirTri = t;
                    }
                }
                if (bestMirTri >= 0) mirrorIsland = s.TriIslandIds[bestMirTri];
            }

            var matched = new HashSet<int>();
            for (int t = 0; t < s.TriCount; t++)
            {
                bool isCut = s.CutTriangles.Contains(t);
                if (restoreMode ? !isCut : isCut) continue;
                int isl = s.TriIslandIds[t];
                if (isl == targetIsland || (mirrorIsland >= 0 && isl == mirrorIsland))
                    matched.Add(t);
            }
            return ApplyMatchedSet(s.SelectedTriangles, matched, op);
        }

        public static HashSet<int> CollectBoneSubtree(Skeleton3D skel, int rootBoneIdx, bool includeChildren)
        {
            var set = new HashSet<int>();
            if (skel == null || !GodotObject.IsInstanceValid(skel) || rootBoneIdx < 0 || rootBoneIdx >= skel.GetBoneCount())
                return set;

            set.Add(rootBoneIdx);
            if (!includeChildren) return set;

            int boneCount = skel.GetBoneCount();
            bool added = true;
            while (added)
            {
                added = false;
                for (int b = 0; b < boneCount; b++)
                {
                    if (set.Contains(b)) continue;
                    int p = skel.GetBoneParent(b);
                    if (p >= 0 && set.Contains(p))
                    {
                        set.Add(b);
                        added = true;
                    }
                }
            }
            return set;
        }

        public static bool SelectByBoneRegion(
            SurfaceCutSessionData s,
            int rootBoneIdx,
            bool includeChildren,
            bool mirrorX,
            MeshCutSelectOp op,
            bool restoreMode,
            float weightThreshold = 0.35f)
        {
            if (s == null || s.Skeleton == null || !GodotObject.IsInstanceValid(s.Skeleton) || rootBoneIdx < 0)
                return false;

            var targetBones = CollectBoneSubtree(s.Skeleton, rootBoneIdx, includeChildren);
            if (mirrorX)
            {
                string bName = s.Skeleton.GetBoneName(rootBoneIdx);
                string mirName = CustomWeightPaintEditor.MirrorBoneName(bName);
                if (!string.IsNullOrEmpty(mirName))
                {
                    int mirIdx = s.Skeleton.FindBone(mirName);
                    if (mirIdx >= 0)
                    {
                        foreach (int mb in CollectBoneSubtree(s.Skeleton, mirIdx, includeChildren))
                            targetBones.Add(mb);
                    }
                }
            }

            var matched = new HashSet<int>();
            int triCount = s.TriCount;
            bool hasWeights = s.SkelBoneIndices != null && s.SkelBoneWeights != null;
            int stride = s.BoneStride;
            int vCount = s.RestVerts.Length;

            for (int t = 0; t < triCount; t++)
            {
                bool isCut = s.CutTriangles.Contains(t);
                if (restoreMode ? !isCut : isCut) continue;

                if (s.TriDominantBone[t] >= 0 && targetBones.Contains(s.TriDominantBone[t]))
                {
                    matched.Add(t);
                    continue;
                }

                if (hasWeights)
                {
                    float wSum = 0f;
                    int o = t * 3;
                    for (int c = 0; c < 3; c++)
                    {
                        int v = s.RawTriIndices[o + c];
                        if ((uint)v >= (uint)vCount) continue;
                        int off = v * stride;
                        for (int k = 0; k < stride; k++)
                        {
                            if (targetBones.Contains(s.SkelBoneIndices[off + k]))
                                wSum += s.SkelBoneWeights[off + k];
                        }
                    }
                    if (wSum >= weightThreshold * 3.0f)
                        matched.Add(t);
                }
            }

            return ApplyMatchedSet(s.SelectedTriangles, matched, op);
        }

        // =========================================================================
        // Selection Refinements & Cut Actions
        // =========================================================================

        public static bool GrowSelection(SurfaceCutSessionData s, bool restoreMode)
        {
            if (s == null || s.SelectedTriangles.Count == 0) return false;
            var toAdd = new List<int>();
            foreach (int t in s.SelectedTriangles)
            {
                if ((uint)t >= (uint)s.TriNeighbors.Length) continue;
                var nbs = s.TriNeighbors[t];
                for (int i = 0; i < nbs.Length; i++)
                {
                    int nt = nbs[i];
                    if (s.SelectedTriangles.Contains(nt)) continue;
                    bool isCut = s.CutTriangles.Contains(nt);
                    if (restoreMode ? !isCut : isCut) continue;
                    toAdd.Add(nt);
                }
            }
            if (toAdd.Count == 0) return false;
            foreach (int nt in toAdd) s.SelectedTriangles.Add(nt);
            return true;
        }

        public static bool ShrinkSelection(SurfaceCutSessionData s, bool restoreMode)
        {
            if (s == null || s.SelectedTriangles.Count == 0) return false;
            var toRemove = new List<int>();
            foreach (int t in s.SelectedTriangles)
            {
                if ((uint)t >= (uint)s.TriNeighbors.Length) continue;
                var nbs = s.TriNeighbors[t];
                bool isBoundary = nbs.Length == 0;
                for (int i = 0; i < nbs.Length; i++)
                {
                    int nt = nbs[i];
                    bool isCut = s.CutTriangles.Contains(nt);
                    if (restoreMode ? !isCut : isCut) continue;
                    if (!s.SelectedTriangles.Contains(nt))
                    {
                        isBoundary = true;
                        break;
                    }
                }
                if (isBoundary) toRemove.Add(t);
            }
            if (toRemove.Count == 0) return false;
            foreach (int t in toRemove) s.SelectedTriangles.Remove(t);
            return true;
        }

        public static bool InvertSelection(SurfaceCutSessionData s, bool restoreMode)
        {
            if (s == null) return false;
            var next = new HashSet<int>();
            int triCount = s.TriCount;
            for (int t = 0; t < triCount; t++)
            {
                bool isCut = s.CutTriangles.Contains(t);
                if (restoreMode ? !isCut : isCut) continue;
                if (!s.SelectedTriangles.Contains(t))
                    next.Add(t);
            }
            if (next.Count == s.SelectedTriangles.Count && next.SetEquals(s.SelectedTriangles))
                return false;
            s.SelectedTriangles = next;
            return true;
        }

        public static int CutSelected(SurfaceCutSessionData s)
        {
            if (s == null || s.SelectedTriangles.Count == 0) return 0;
            int added = 0;
            foreach (int t in s.SelectedTriangles)
            {
                if (s.CutTriangles.Add(t)) added++;
            }
            s.SelectedTriangles.Clear();
            return added;
        }

        public static int KeepOnlySelected(SurfaceCutSessionData s)
        {
            if (s == null || s.SelectedTriangles.Count == 0) return 0;
            int added = 0;
            int triCount = s.TriCount;
            for (int t = 0; t < triCount; t++)
            {
                if (!s.SelectedTriangles.Contains(t))
                {
                    if (s.CutTriangles.Add(t)) added++;
                }
                else
                {
                    s.CutTriangles.Remove(t);
                }
            }
            s.SelectedTriangles.Clear();
            return added;
        }

        public static int RestoreSelected(SurfaceCutSessionData s)
        {
            if (s == null || s.SelectedTriangles.Count == 0) return 0;
            int restored = 0;
            foreach (int t in s.SelectedTriangles)
            {
                if (s.CutTriangles.Remove(t)) restored++;
            }
            s.SelectedTriangles.Clear();
            return restored;
        }

        public static int RestoreAll(SurfaceCutSessionData s)
        {
            if (s == null || s.CutTriangles.Count == 0) return 0;
            int restored = s.CutTriangles.Count;
            s.CutTriangles.Clear();
            s.SelectedTriangles.Clear();
            return restored;
        }

        // =========================================================================
        // GPU-Skinned 3D Selection & Cut-Ghost Overlay Builders
        // =========================================================================

        /// <summary>
        /// Builds a GPU-skinned (or static) overlay ArrayMesh containing only the specified triangle indices,
        /// preserving 4-bone or 8-bone skinning arrays so the highlight tracks live animations automatically!
        /// </summary>
        public static ArrayMesh BuildTriangleSubsetOverlayMesh(
            SurfaceCutSessionData s,
            HashSet<int> triangleIds,
            Material overlayMaterial)
        {
            if (s == null || triangleIds == null || triangleIds.Count == 0 || s.RestVerts.Length == 0)
                return null;

            var triIdx = new List<int>(triangleIds.Count * 3);
            int triCount = s.TriCount;
            foreach (int t in triangleIds)
            {
                if ((uint)t >= (uint)triCount) continue;
                int o = t * 3;
                triIdx.Add(s.RawTriIndices[o]);
                triIdx.Add(s.RawTriIndices[o + 1]);
                triIdx.Add(s.RawTriIndices[o + 2]);
            }
            if (triIdx.Count < 3) return null;

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = s.RestVerts;
            if (s.RestNorms != null && s.RestNorms.Length == s.RestVerts.Length)
                arrays[(int)Mesh.ArrayType.Normal] = s.RestNorms;
            arrays[(int)Mesh.ArrayType.Index] = triIdx.ToArray();

            Mesh.ArrayFormat fmt = default;
            if (s.RawSkinBones != null && s.RawSkinWeights != null &&
                s.RawSkinBones.Length >= s.RestVerts.Length * s.BoneStride &&
                s.RawSkinWeights.Length >= s.RestVerts.Length * s.BoneStride)
            {
                arrays[(int)Mesh.ArrayType.Bones] = s.RawSkinBones;
                arrays[(int)Mesh.ArrayType.Weights] = s.RawSkinWeights;
                if (s.BoneStride == 8)
                    fmt |= Mesh.ArrayFormat.FlagUse8BoneWeights;
            }

            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, fmt);
            if (overlayMaterial != null)
                mesh.SurfaceSetMaterial(0, overlayMaterial);
            return mesh;
        }

        public static StandardMaterial3D CreateSelectionOverlayMaterial(bool restoreMode, bool xRay = false)
        {
            return new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = restoreMode
                    ? new Color(0.18f, 0.88f, 0.96f, xRay ? 0.38f : 0.54f) // Cyan for restore selection
                    : new Color(1.00f, 0.72f, 0.14f, xRay ? 0.40f : 0.56f), // Warm amber for cut selection
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
                NoDepthTest = xRay,
                Grow = true,
                GrowAmount = 0.0014f,
                RenderPriority = 60
            };
        }

        public static StandardMaterial3D CreateXRayTargetMaterial()
        {
            return new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel,
                AlbedoColor = new Color(0.76f, 0.82f, 0.88f, 0.44f),
                Roughness = 0.72f,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
                RenderPriority = 40
            };
        }

        public static StandardMaterial3D CreateCutGhostOverlayMaterial()
        {
            return new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(0.95f, 0.22f, 0.26f, 0.24f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
                Grow = true,
                GrowAmount = 0.0006f,
                RenderPriority = 55
            };
        }

        /// <summary>
        /// Lists all Skeleton3D bones that have non-zero influence on at least one triangle of the surface,
        /// sorted by triangle count descending so the Bone-Region picker immediately surfaces the most relevant bones.
        /// </summary>
        public static List<(int BoneIdx, string BoneName, int TriCount)> GetWeightedBonesForSurface(SurfaceCutSessionData s)
        {
            var result = new List<(int BoneIdx, string BoneName, int TriCount)>();
            if (s == null || s.Skeleton == null || !GodotObject.IsInstanceValid(s.Skeleton)) return result;

            var counts = new Dictionary<int, int>();
            int boneCount = s.Skeleton.GetBoneCount();
            for (int t = 0; t < s.TriCount; t++)
            {
                int b = s.TriDominantBone[t];
                if (b >= 0 && b < boneCount)
                    counts[b] = counts.GetValueOrDefault(b, 0) + 1;
            }
            foreach (var (b, cnt) in counts.OrderByDescending(kv => kv.Value))
            {
                result.Add((b, s.Skeleton.GetBoneName(b), cnt));
            }
            return result;
        }
    }
}

