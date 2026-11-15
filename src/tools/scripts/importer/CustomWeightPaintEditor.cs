using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DeadlockPlayground.Importer
{
    /// <summary>
    /// Interactive 3D skin-weight paint and heatmap helper for the Importer Modal's preview viewport.
    /// Reads/writes ArrayMesh Bones, Weights, and vertex heatmap Colors in place, maintains 1mm seam-weld
    /// consistency, and persists edits as SkinWeightOverride entries.
    /// </summary>
    public static class CustomWeightPaintEditor
    {
        public enum BrushMode
        {
            Add = 0,
            Subtract = 1,
            Smooth = 2,
            SetOne = 3
        }

        public enum ViewportShadingMode
        {
            Textured = 0,
            SolidWire = 1,
            Wireframe = 2
        }

        public sealed class SurfaceState
        {
            public string OverrideKey;
            public MeshInstance3D MeshInstance;
            public MeshInstance3D WireframeOverlay;
            public int SurfaceIndex;
            public Material OriginalMaterial;
            public Vector3[] RestVerts;
            public Vector3[] RestNorms;
            public Vector3[] PosedVerts;
            public Vector2[] Uvs;
            public int[] Indices;
            public int[] EdgeIndices;
            public int[] SkinBones;   // vCount * 4 (Skin bind indices)
            public float[] Weights;   // vCount * 4
            public int[][] WeldClusters;
            public int[][] Neighbors;
        }

        public sealed class WeightHistoryEntry
        {
            public string Label;
            public Dictionary<string, (int[] Bones, float[] Weights)> Surfaces = new(StringComparer.OrdinalIgnoreCase);
        }

        public static string MirrorBoneName(string boneName)
        {
            if (string.IsNullOrEmpty(boneName)) return null;
            if (boneName.EndsWith("_L")) return boneName.Substring(0, boneName.Length - 2) + "_R";
            if (boneName.EndsWith("_R")) return boneName.Substring(0, boneName.Length - 2) + "_L";
            return null;
        }

        /// <summary>Builds editable surface state caches from _previewRiggedContainer meshes.</summary>
        public static List<SurfaceState> CaptureSurfaces(Node3D riggedContainer)
        {
            var list = new List<SurfaceState>();
            if (riggedContainer == null || !GodotObject.IsInstanceValid(riggedContainer)) return list;

            foreach (var mi in CustomModelAutoRigger.CollectMeshes(riggedContainer))
            {
                if (mi?.Mesh == null) continue;
                // Rigid socket attachments shouldn't be weight painted.
                if (mi.HasMeta("SocketBone")) continue;

                string srcName = mi.HasMeta("SourceMeshName") ? mi.GetMeta("SourceMeshName").AsString() : mi.Name.ToString();
                int srcSurf = mi.HasMeta("SourceSurfaceIndex") ? mi.GetMeta("SourceSurfaceIndex").AsInt32() : 0;

                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                {
                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count == 0) continue;

                    var verts = CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    if (verts == null || verts.Length == 0) continue;

                    var norms = arrays.Count > (int)Mesh.ArrayType.Normal ? CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Normal]) : null;
                    var uvs = arrays.Count > (int)Mesh.ArrayType.TexUV ? CustomModelAutoRigger.GetVector2Array(arrays[(int)Mesh.ArrayType.TexUV]) : null;
                    var indices = arrays.Count > (int)Mesh.ArrayType.Index ? CustomModelAutoRigger.GetInt32Array(arrays[(int)Mesh.ArrayType.Index]) : null;
                    var bones = arrays.Count > (int)Mesh.ArrayType.Bones ? CustomModelAutoRigger.GetInt32Array(arrays[(int)Mesh.ArrayType.Bones]) : null;
                    var weights = arrays.Count > (int)Mesh.ArrayType.Weights ? CustomModelAutoRigger.GetFloat32Array(arrays[(int)Mesh.ArrayType.Weights]) : null;
                    if (bones == null || weights == null || bones.Length < verts.Length * 4 || weights.Length < verts.Length * 4)
                        continue;

                    if (indices == null || indices.Length == 0)
                    {
                        indices = new int[verts.Length - verts.Length % 3];
                        for (int i = 0; i < indices.Length; i++) indices[i] = i;
                    }

                    BuildTopology(verts, indices, out var clusters, out var neighbors);
                    Material origMat = mi.HasMeta($"OriginalMaterial_{s}")
                        ? mi.GetMeta($"OriginalMaterial_{s}").As<Material>()
                        : (mi.Mesh.SurfaceGetMaterial(s) ?? mi.GetSurfaceOverrideMaterial(s));

                    list.Add(new SurfaceState
                    {
                        OverrideKey = CustomModelAutoRigger.MakeSurfaceKeepKey(srcName, srcSurf + s),
                        MeshInstance = mi,
                        WireframeOverlay = FindExistingWireframeOverlay(mi, s),
                        SurfaceIndex = s,
                        OriginalMaterial = origMat,
                        RestVerts = verts,
                        RestNorms = norms,
                        PosedVerts = (Vector3[])verts.Clone(),
                        Uvs = uvs,
                        Indices = indices,
                        EdgeIndices = BuildUniqueEdgeIndices(indices),
                        SkinBones = (int[])bones.Clone(),
                        Weights = (float[])weights.Clone(),
                        WeldClusters = clusters,
                        Neighbors = neighbors
                    });
                }
            }
            return list;
        }

        private static void BuildTopology(Vector3[] verts, int[] indices, out int[][] clustersPerVert, out int[][] neighbors)
        {
            int n = verts.Length;
            float cell = 0.001f;
            var map = new Dictionary<(long, long, long), List<int>>();
            for (int i = 0; i < n; i++)
            {
                var k = ((long)MathF.Round(verts[i].X / cell), (long)MathF.Round(verts[i].Y / cell), (long)MathF.Round(verts[i].Z / cell));
                if (!map.TryGetValue(k, out var grp)) { grp = new List<int>(2); map[k] = grp; }
                grp.Add(i);
            }
            clustersPerVert = new int[n][];
            foreach (var grp in map.Values)
            {
                var arr = grp.ToArray();
                foreach (int v in arr) clustersPerVert[v] = arr;
            }

            var adj = new HashSet<int>[n];
            for (int i = 0; i < n; i++) adj[i] = new HashSet<int>();
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                if (a >= n || b >= n || c >= n) continue;
                adj[a].Add(b); adj[a].Add(c);
                adj[b].Add(a); adj[b].Add(c);
                adj[c].Add(a); adj[c].Add(b);
            }
            // Share topological neighbors across coincident weld clusters so smoothing never tears UV seams.
            neighbors = new int[n][];
            for (int i = 0; i < n; i++)
            {
                var merged = new HashSet<int>(adj[i]);
                foreach (int w in clustersPerVert[i])
                    if (w != i) merged.UnionWith(adj[w]);
                merged.Remove(i);
                neighbors[i] = merged.ToArray();
            }
        }

        public static void UpdatePosedVertices(List<SurfaceState> surfaces, Skeleton3D skel, Skin skin)
        {
            if (skel == null || skin == null) return;
            int bindCount = skin.GetBindCount();
            int boneCount = skel.GetBoneCount();
            Transform3D skelXform = skel.GlobalTransform;
            var bindMats = new Transform3D[bindCount];
            for (int bi = 0; bi < bindCount; bi++)
            {
                int b = skin.GetBindBone(bi);
                if (b < 0 || b >= boneCount)
                {
                    string nm = skin.GetBindName(bi);
                    if (!string.IsNullOrEmpty(nm)) b = skel.FindBone(nm);
                }
                Transform3D pose = (b >= 0 && b < boneCount) ? skel.GetBoneGlobalPose(b) : Transform3D.Identity;
                bindMats[bi] = skelXform * pose * skin.GetBindPose(bi);
            }

            foreach (var s in surfaces)
            {
                int vc = s.RestVerts.Length;
                for (int v = 0; v < vc; v++)
                {
                    Vector3 rv = s.RestVerts[v];
                    Vector3 acc = Vector3.Zero;
                    float sumW = 0f;
                    int baseOff = v * 4;
                    for (int k = 0; k < 4; k++)
                    {
                        float w = s.Weights[baseOff + k];
                        if (w <= 0.0001f) continue;
                        int bi = s.SkinBones[baseOff + k];
                        if (bi < 0 || bi >= bindCount) continue;
                        acc += (bindMats[bi] * rv) * w;
                        sumW += w;
                    }
                    s.PosedVerts[v] = sumW > 0.0001f ? acc / sumW : (skelXform * rv);
                }
            }
        }

        public static bool RaycastSurfaces(
            Vector3 rayOrigin,
            Vector3 rayDir,
            List<SurfaceState> surfaces,
            out SurfaceState hitSurface,
            out Vector3 hitPoint,
            out Vector3 hitNormal)
        {
            hitSurface = null;
            hitPoint = Vector3.Zero;
            hitNormal = Vector3.Up;
            float bestT = float.MaxValue;

            foreach (var s in surfaces)
            {
                if (s.MeshInstance == null || !GodotObject.IsInstanceValid(s.MeshInstance) || !s.MeshInstance.Visible) continue;
                var verts = s.PosedVerts;
                var idx = s.Indices;
                for (int i = 0; i + 2 < idx.Length; i += 3)
                {
                    Vector3 a = verts[idx[i]], b = verts[idx[i + 1]], c = verts[idx[i + 2]];
                    if (RayTriangle(rayOrigin, rayDir, a, b, c, out float t) && t < bestT)
                    {
                        bestT = t;
                        hitSurface = s;
                        hitPoint = rayOrigin + rayDir * t;
                        Vector3 n = (b - a).Cross(c - a);
                        hitNormal = n.LengthSquared() > 1e-8f ? n.Normalized() : Vector3.Up;
                    }
                }
            }
            return hitSurface != null;
        }

        private static bool RayTriangle(Vector3 orig, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2, out float t)
        {
            t = 0f;
            Vector3 e1 = v1 - v0;
            Vector3 e2 = v2 - v0;
            Vector3 pvec = dir.Cross(e2);
            float det = e1.Dot(pvec);
            if (MathF.Abs(det) < 1e-7f) return false;
            float invDet = 1f / det;
            Vector3 tvec = orig - v0;
            float u = tvec.Dot(pvec) * invDet;
            if (u < 0f || u > 1f) return false;
            Vector3 qvec = tvec.Cross(e1);
            float v = dir.Dot(qvec) * invDet;
            if (v < 0f || u + v > 1f) return false;
            t = e2.Dot(qvec) * invDet;
            return t > 0.001f;
        }

        public static Color HeatColor(float w)
        {
            w = Mathf.Clamp(w, 0f, 1f);
            if (w <= 0.001f) return new Color(0.12f, 0.16f, 0.35f);
            if (w < 0.25f) return new Color(0.12f, 0.16f, 0.35f).Lerp(new Color(0.10f, 0.72f, 0.92f), w / 0.25f);
            if (w < 0.50f) return new Color(0.10f, 0.72f, 0.92f).Lerp(new Color(0.20f, 0.85f, 0.28f), (w - 0.25f) / 0.25f);
            if (w < 0.75f) return new Color(0.20f, 0.85f, 0.28f).Lerp(new Color(0.98f, 0.85f, 0.15f), (w - 0.50f) / 0.25f);
            return new Color(0.98f, 0.85f, 0.15f).Lerp(new Color(1.00f, 0.15f, 0.12f), (w - 0.75f) / 0.25f);
        }

        /// <summary>Rebuilds vertex Colors + Bones + Weights on a single preview MeshInstance3D surface.</summary>
        public static void RefreshSurfaceHeatmap(SurfaceState s, int targetBindIdx, int mirrorBindIdx, bool heatmapActive, Material heatmapMat, Material clayWireSolidMat = null)
        {
            if (s?.MeshInstance == null || !GodotObject.IsInstanceValid(s.MeshInstance)) return;
            if (s.MeshInstance.Mesh is not ArrayMesh arrMesh) return;

            int vc = s.RestVerts.Length;
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = s.RestVerts;
            if (s.RestNorms != null && s.RestNorms.Length == vc) arrays[(int)Mesh.ArrayType.Normal] = s.RestNorms;
            if (s.Uvs != null && s.Uvs.Length == vc) arrays[(int)Mesh.ArrayType.TexUV] = s.Uvs;
            if (s.Indices != null && s.Indices.Length > 0) arrays[(int)Mesh.ArrayType.Index] = s.Indices;
            arrays[(int)Mesh.ArrayType.Bones] = s.SkinBones;
            arrays[(int)Mesh.ArrayType.Weights] = s.Weights;

            if (heatmapActive)
            {
                var cols = new Color[vc];
                for (int v = 0; v < vc; v++)
                {
                    float w = 0f;
                    int baseOff = v * 4;
                    for (int k = 0; k < 4; k++)
                    {
                        int bi = s.SkinBones[baseOff + k];
                        if (bi == targetBindIdx || (mirrorBindIdx >= 0 && bi == mirrorBindIdx))
                            w += s.Weights[baseOff + k];
                    }
                    cols[v] = HeatColor(w);
                }
                arrays[(int)Mesh.ArrayType.Color] = cols;
            }

            arrMesh.ClearSurfaces();
            arrMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            Material surfaceMat = heatmapActive ? heatmapMat : (clayWireSolidMat ?? s.OriginalMaterial);
            arrMesh.SurfaceSetMaterial(0, surfaceMat);
            if (heatmapActive) s.MeshInstance.SetSurfaceOverrideMaterial(0, null);

            if (s.WireframeOverlay != null && GodotObject.IsInstanceValid(s.WireframeOverlay) && s.WireframeOverlay.Mesh is ArrayMesh wireMesh)
            {
                RebuildWireframeLinesSurface(wireMesh, s.RestVerts, s.RestNorms, s.EdgeIndices, s.SkinBones, s.Weights, 4, s.WireframeOverlay.MaterialOverride);
            }
        }

        /// <summary>
        /// Paints a single spherical brush stroke on matching surfaces (and optional mirrored side),
        /// returns true if any vertex changed.
        /// </summary>
        public static bool ApplyBrush(
            List<SurfaceState> surfaces,
            Vector3 hitPointWorld,
            BrushMode mode,
            float radius,
            float strength,
            int targetBindIdx,
            int fallbackBindIdx,
            bool mirrorLR,
            int mirrorBindIdx,
            int mirrorFallbackBindIdx)
        {
            bool anyChanged = ApplyHalfBrush(surfaces, hitPointWorld, mode, radius, strength, targetBindIdx, fallbackBindIdx);
            if (mirrorLR && mirrorBindIdx >= 0)
            {
                Vector3 mirroredPt = new Vector3(-hitPointWorld.X, hitPointWorld.Y, hitPointWorld.Z);
                if (ApplyHalfBrush(surfaces, mirroredPt, mode, radius, strength, mirrorBindIdx, mirrorFallbackBindIdx >= 0 ? mirrorFallbackBindIdx : fallbackBindIdx))
                    anyChanged = true;
            }
            return anyChanged;
        }

        private static bool ApplyHalfBrush(
            List<SurfaceState> surfaces,
            Vector3 center,
            BrushMode mode,
            float radius,
            float strength,
            int targetBindIdx,
            int fallbackBindIdx)
        {
            if (targetBindIdx < 0 || radius <= 0.001f || strength <= 0.0001f) return false;
            float rSq = radius * radius;
            bool any = false;

            foreach (var s in surfaces)
            {
                int vc = s.PosedVerts.Length;
                // Collect primary representative vertex per weld cluster inside brush radius.
                var visitedCluster = new bool[vc];
                float[] smoothSnapshots = mode == BrushMode.Smooth ? (float[])s.Weights.Clone() : null;
                int[] smoothBoneSnapshots = mode == BrushMode.Smooth ? (int[])s.SkinBones.Clone() : null;

                for (int v = 0; v < vc; v++)
                {
                    if (visitedCluster[v]) continue;
                    float dSq = (s.PosedVerts[v] - center).LengthSquared();
                    if (dSq > rSq) continue;

                    float dist = MathF.Sqrt(dSq);
                    float normT = 1f - dist / radius;
                    float falloff = normT * normT * (3f - 2f * normT);
                    float alpha = Mathf.Clamp(falloff * strength, 0f, 1f);
                    if (alpha <= 0.0001f) continue;

                    any = true;
                    var cluster = s.WeldClusters[v];
                    foreach (int c in cluster) visitedCluster[c] = true;

                    var dict = new Dictionary<int, float>(8);
                    int baseOff = v * 4;

                    if (mode == BrushMode.Smooth)
                    {
                        for (int k = 0; k < 4; k++)
                            if (smoothBoneSnapshots[baseOff + k] >= 0 && smoothSnapshots[baseOff + k] > 0.0001f)
                                dict[smoothBoneSnapshots[baseOff + k]] = dict.GetValueOrDefault(smoothBoneSnapshots[baseOff + k], 0f) + smoothSnapshots[baseOff + k] * (1f - alpha);

                        var nbrs = s.Neighbors[v];
                        if (nbrs.Length > 0)
                        {
                            float wShare = alpha / nbrs.Length;
                            foreach (int nb in nbrs)
                            {
                                int nOff = nb * 4;
                                for (int k = 0; k < 4; k++)
                                    if (smoothBoneSnapshots[nOff + k] >= 0 && smoothSnapshots[nOff + k] > 0.0001f)
                                        dict[smoothBoneSnapshots[nOff + k]] = dict.GetValueOrDefault(smoothBoneSnapshots[nOff + k], 0f) + smoothSnapshots[nOff + k] * wShare;
                            }
                        }
                    }
                    else
                    {
                        for (int k = 0; k < 4; k++)
                            if (s.SkinBones[baseOff + k] >= 0 && s.Weights[baseOff + k] > 0.0001f)
                                dict[s.SkinBones[baseOff + k]] = dict.GetValueOrDefault(s.SkinBones[baseOff + k], 0f) + s.Weights[baseOff + k];

                        float cur = dict.GetValueOrDefault(targetBindIdx, 0f);
                        if (mode == BrushMode.SetOne)
                        {
                            float next = Mathf.Lerp(cur, 1f, falloff > 0.35f ? 1f : alpha);
                            ScaleOthers(dict, targetBindIdx, next, fallbackBindIdx);
                        }
                        else if (mode == BrushMode.Add)
                        {
                            float next = Mathf.Clamp(cur + (1f - cur) * alpha, 0f, 1f);
                            ScaleOthers(dict, targetBindIdx, next, fallbackBindIdx);
                        }
                        else if (mode == BrushMode.Subtract)
                        {
                            float remove = cur * alpha;
                            float next = MathF.Max(0f, cur - remove);
                            ScaleOthers(dict, targetBindIdx, next, fallbackBindIdx);
                        }
                    }

                    // Top 4 normalized
                    var top = dict.Where(kv => kv.Value > 0.0001f).OrderByDescending(kv => kv.Value).Take(4).ToList();
                    float sum = top.Sum(kv => kv.Value);
                    int[] outB = new int[4];
                    float[] outW = new float[4];
                    if (sum <= 1e-6f)
                    {
                        outB[0] = fallbackBindIdx >= 0 ? fallbackBindIdx : 0;
                        outW[0] = 1f;
                    }
                    else
                    {
                        for (int k = 0; k < top.Count; k++) { outB[k] = top[k].Key; outW[k] = top[k].Value / sum; }
                    }

                    foreach (int c in cluster)
                    {
                        int cOff = c * 4;
                        for (int k = 0; k < 4; k++) { s.SkinBones[cOff + k] = outB[k]; s.Weights[cOff + k] = outW[k]; }
                    }
                }
            }
            return any;
        }

        private static void ScaleOthers(Dictionary<int, float> dict, int targetBone, float newTargetW, int fallbackBone)
        {
            float othersSum = 0f;
            foreach (var kv in dict)
                if (kv.Key != targetBone) othersSum += kv.Value;

            float desiredOthers = MathF.Max(0f, 1f - newTargetW);
            var keys = dict.Keys.Where(k => k != targetBone).ToList();
            if (othersSum > 1e-5f)
            {
                float ratio = desiredOthers / othersSum;
                foreach (int k in keys) dict[k] *= ratio;
            }
            else if (desiredOthers > 1e-5f)
            {
                int receiver = fallbackBone >= 0 && fallbackBone != targetBone ? fallbackBone : 0;
                dict[receiver] = desiredOthers;
            }

            if (newTargetW > 0.0001f) dict[targetBone] = newTargetW;
            else dict.Remove(targetBone);
        }

        /// <summary>Converts current surface bind weights into portable Skeleton bone-name overrides.</summary>
        public static Dictionary<string, CustomModelAutoRigger.SkinWeightOverride> ExportOverrides(
            List<SurfaceState> surfaces, Skeleton3D heroSkeleton, Skin heroSkin)
        {
            var result = new Dictionary<string, CustomModelAutoRigger.SkinWeightOverride>(StringComparer.OrdinalIgnoreCase);
            if (surfaces == null || heroSkeleton == null || heroSkin == null) return result;

            int bindCount = heroSkin.GetBindCount();
            var bindToBoneName = new string[bindCount];
            for (int bi = 0; bi < bindCount; bi++)
            {
                int b = heroSkin.GetBindBone(bi);
                if (b >= 0 && b < heroSkeleton.GetBoneCount()) bindToBoneName[bi] = heroSkeleton.GetBoneName(b);
                else bindToBoneName[bi] = heroSkin.GetBindName(bi);
            }

            foreach (var s in surfaces)
            {
                int vc = s.RestVerts.Length;
                var palette = new List<string>();
                var paletteMap = new Dictionary<string, int>(StringComparer.Ordinal);
                var palIndices = new int[vc * 4];
                for (int i = 0; i < vc * 4; i++)
                {
                    int bi = s.SkinBones[i];
                    string bName = (bi >= 0 && bi < bindCount && !string.IsNullOrEmpty(bindToBoneName[bi])) ? bindToBoneName[bi] : "pelvis";
                    if (!paletteMap.TryGetValue(bName, out int pi))
                    {
                        pi = palette.Count;
                        paletteMap[bName] = pi;
                        palette.Add(bName);
                    }
                    palIndices[i] = pi;
                }

                result[s.OverrideKey] = new CustomModelAutoRigger.SkinWeightOverride
                {
                    VertexCount = vc,
                    BonePalette = palette.ToArray(),
                    PaletteIndices = palIndices,
                    Weights = (float[])s.Weights.Clone()
                };
            }
            return result;
        }

        // ------------------------------------------------------------------
        // Undo / Redo Weight History Snapshots
        // ------------------------------------------------------------------

        public static WeightHistoryEntry CaptureHistoryEntry(List<SurfaceState> surfaces, string label)
        {
            var entry = new WeightHistoryEntry { Label = label ?? "Edit Weights" };
            if (surfaces == null) return entry;
            foreach (var s in surfaces)
            {
                if (s == null || string.IsNullOrEmpty(s.OverrideKey)) continue;
                entry.Surfaces[s.OverrideKey] = ((int[])s.SkinBones.Clone(), (float[])s.Weights.Clone());
            }
            return entry;
        }

        public static bool HasSurfaceWeightDifference(WeightHistoryEntry a, List<SurfaceState> current)
        {
            if (a == null || current == null) return false;
            foreach (var s in current)
            {
                if (s == null || string.IsNullOrEmpty(s.OverrideKey)) continue;
                if (!a.Surfaces.TryGetValue(s.OverrideKey, out var prev)) return true;
                if (prev.Bones.Length != s.SkinBones.Length || prev.Weights.Length != s.Weights.Length) return true;
                for (int i = 0; i < prev.Bones.Length; i++)
                    if (prev.Bones[i] != s.SkinBones[i]) return true;
                for (int i = 0; i < prev.Weights.Length; i++)
                    if (MathF.Abs(prev.Weights[i] - s.Weights[i]) > 1e-5f) return true;
            }
            return false;
        }

        public static bool RestoreHistoryEntry(List<SurfaceState> surfaces, WeightHistoryEntry entry)
        {
            if (surfaces == null || entry == null) return false;
            bool restoredAny = false;
            foreach (var s in surfaces)
            {
                if (s == null || string.IsNullOrEmpty(s.OverrideKey)) continue;
                if (!entry.Surfaces.TryGetValue(s.OverrideKey, out var saved)) continue;
                if (saved.Bones.Length != s.SkinBones.Length || saved.Weights.Length != s.Weights.Length) continue;
                Array.Copy(saved.Bones, s.SkinBones, saved.Bones.Length);
                Array.Copy(saved.Weights, s.Weights, saved.Weights.Length);
                restoredAny = true;
            }
            return restoredAny;
        }

        // ------------------------------------------------------------------
        // Per-Bone Surgical Weight Reset
        // ------------------------------------------------------------------

        /// <summary>
        /// Resets weights belonging to targetBoneName (and its L/R mirror when includeMirror is true) back
        /// to baselineOverrides on each SurfaceState while preserving user edits on unrelated bones.
        /// </summary>
        public static bool ResetBoneToBaseline(
            List<SurfaceState> surfaces,
            Dictionary<string, CustomModelAutoRigger.SkinWeightOverride> baselineOverrides,
            Skeleton3D heroSkeleton,
            Skin heroSkin,
            string targetBoneName,
            bool includeMirror)
        {
            if (surfaces == null || surfaces.Count == 0 || baselineOverrides == null || heroSkeleton == null || heroSkin == null)
                return false;

            int tgtBi = ResolveBindIdx(heroSkin, heroSkeleton, targetBoneName);
            if (tgtBi < 0) return false;

            string mirName = includeMirror ? MirrorBoneName(targetBoneName) : null;
            int mirBi = mirName != null ? ResolveBindIdx(heroSkin, heroSkeleton, mirName) : -1;

            int fallbackBi = ResolveBindIdx(heroSkin, heroSkeleton, "pelvis");
            if (fallbackBi < 0) fallbackBi = 0;

            bool changedAny = false;
            var baseDict = new Dictionary<int, float>(8);
            var curDict = new Dictionary<int, float>(8);

            foreach (var s in surfaces)
            {
                if (s == null || string.IsNullOrEmpty(s.OverrideKey)) continue;
                if (!baselineOverrides.TryGetValue(s.OverrideKey, out var ov) || ov == null) continue;
                int vc = s.RestVerts.Length;
                if (ov.VertexCount != vc || ov.PaletteIndices == null || ov.Weights == null || ov.BonePalette == null) continue;

                var palToBind = new int[ov.BonePalette.Length];
                for (int p = 0; p < ov.BonePalette.Length; p++)
                {
                    int bi = ResolveBindIdx(heroSkin, heroSkeleton, ov.BonePalette[p]);
                    palToBind[p] = bi >= 0 ? bi : fallbackBi;
                }

                var visited = new bool[vc];
                for (int v = 0; v < vc; v++)
                {
                    if (visited[v]) continue;

                    int baseOff = v * 4;
                    bool curHasResetBone = false;
                    for (int k = 0; k < 4; k++)
                    {
                        int bi = s.SkinBones[baseOff + k];
                        float w = s.Weights[baseOff + k];
                        if (w > 0.0001f && (bi == tgtBi || (mirBi >= 0 && bi == mirBi)))
                        {
                            curHasResetBone = true;
                            break;
                        }
                    }

                    bool baseHasResetBone = false;
                    baseDict.Clear();
                    for (int k = 0; k < 4; k++)
                    {
                        int pi = ov.PaletteIndices[baseOff + k];
                        float w = ov.Weights[baseOff + k];
                        if (w <= 0.0001f || pi < 0 || pi >= palToBind.Length) continue;
                        int bi = palToBind[pi];
                        baseDict[bi] = baseDict.GetValueOrDefault(bi, 0f) + w;
                        if (bi == tgtBi || (mirBi >= 0 && bi == mirBi)) baseHasResetBone = true;
                    }

                    if (!curHasResetBone && !baseHasResetBone) continue;

                    var cluster = s.WeldClusters != null && v < s.WeldClusters.Length ? s.WeldClusters[v] : new[] { v };
                    foreach (int c in cluster) visited[c] = true;

                    curDict.Clear();
                    for (int k = 0; k < 4; k++)
                    {
                        int bi = s.SkinBones[baseOff + k];
                        float w = s.Weights[baseOff + k];
                        if (bi >= 0 && w > 0.0001f) curDict[bi] = curDict.GetValueOrDefault(bi, 0f) + w;
                    }

                    float resetSum = 0f;
                    curDict.Remove(tgtBi);
                    if (baseDict.TryGetValue(tgtBi, out float bTgt) && bTgt > 0.0001f)
                    {
                        curDict[tgtBi] = bTgt;
                        resetSum += bTgt;
                    }
                    if (mirBi >= 0 && mirBi != tgtBi)
                    {
                        curDict.Remove(mirBi);
                        if (baseDict.TryGetValue(mirBi, out float bMir) && bMir > 0.0001f)
                        {
                            curDict[mirBi] = bMir;
                            resetSum += bMir;
                        }
                    }

                    float budget = MathF.Max(0f, 1f - resetSum);
                    float nonResetSum = 0f;
                    foreach (var kv in curDict)
                    {
                        if (kv.Key != tgtBi && kv.Key != mirBi) nonResetSum += kv.Value;
                    }

                    if (nonResetSum > 0.0001f)
                    {
                        float ratio = budget / nonResetSum;
                        var nonKeys = curDict.Keys.Where(k => k != tgtBi && k != mirBi).ToList();
                        foreach (int k in nonKeys) curDict[k] *= ratio;
                    }
                    else if (budget > 0.0001f)
                    {
                        // Current vertex was 100% locked onto the reset bone (e.g., via Set 1.0);
                        // restore remaining non-reset influences from baseline so it doesn't snap to pelvis.
                        float baseOtherSum = 0f;
                        foreach (var kv in baseDict)
                            if (kv.Key != tgtBi && kv.Key != mirBi) baseOtherSum += kv.Value;
                        if (baseOtherSum > 0.0001f)
                        {
                            float ratio = budget / baseOtherSum;
                            foreach (var kv in baseDict)
                                if (kv.Key != tgtBi && kv.Key != mirBi) curDict[kv.Key] = kv.Value * ratio;
                        }
                        else
                        {
                            curDict[fallbackBi] = budget;
                        }
                    }

                    var top = curDict.Where(kv => kv.Value > 0.0001f).OrderByDescending(kv => kv.Value).Take(4).ToList();
                    float sum = top.Sum(kv => kv.Value);
                    int[] outB = new int[4];
                    float[] outW = new float[4];
                    if (sum <= 1e-6f)
                    {
                        outB[0] = fallbackBi;
                        outW[0] = 1f;
                    }
                    else
                    {
                        for (int k = 0; k < top.Count; k++) { outB[k] = top[k].Key; outW[k] = top[k].Value / sum; }
                    }

                    foreach (int c in cluster)
                    {
                        int cOff = c * 4;
                        for (int k = 0; k < 4; k++)
                        {
                            if (s.SkinBones[cOff + k] != outB[k] || MathF.Abs(s.Weights[cOff + k] - outW[k]) > 1e-5f)
                                changedAny = true;
                            s.SkinBones[cOff + k] = outB[k];
                            s.Weights[cOff + k] = outW[k];
                        }
                    }
                }
            }
            return changedAny;
        }

        /// <summary>Restores all surfaces in-place from baselineOverrides (preserving Undo stack).</summary>
        public static bool ResetAllToBaseline(
            List<SurfaceState> surfaces,
            Dictionary<string, CustomModelAutoRigger.SkinWeightOverride> baselineOverrides,
            Skeleton3D heroSkeleton,
            Skin heroSkin)
        {
            if (surfaces == null || surfaces.Count == 0 || baselineOverrides == null || heroSkeleton == null || heroSkin == null)
                return false;

            int fallbackBi = ResolveBindIdx(heroSkin, heroSkeleton, "pelvis");
            if (fallbackBi < 0) fallbackBi = 0;

            bool changedAny = false;
            foreach (var s in surfaces)
            {
                if (s == null || string.IsNullOrEmpty(s.OverrideKey)) continue;
                if (!baselineOverrides.TryGetValue(s.OverrideKey, out var ov) || ov == null) continue;
                int vc = s.RestVerts.Length;
                if (ov.VertexCount != vc || ov.PaletteIndices == null || ov.Weights == null || ov.BonePalette == null) continue;

                var palToBind = new int[ov.BonePalette.Length];
                for (int p = 0; p < ov.BonePalette.Length; p++)
                {
                    int bi = ResolveBindIdx(heroSkin, heroSkeleton, ov.BonePalette[p]);
                    palToBind[p] = bi >= 0 ? bi : fallbackBi;
                }

                int total = vc * 4;
                for (int i = 0; i < total; i++)
                {
                    int pi = ov.PaletteIndices[i];
                    int nb = (pi >= 0 && pi < palToBind.Length) ? palToBind[pi] : fallbackBi;
                    float nw = ov.Weights[i];
                    if (s.SkinBones[i] != nb || MathF.Abs(s.Weights[i] - nw) > 1e-5f)
                        changedAny = true;
                    s.SkinBones[i] = nb;
                    s.Weights[i] = nw;
                }
            }
            return changedAny;
        }

        private static int ResolveBindIdx(Skin skin, Skeleton3D skel, string boneName)
        {
            if (skin == null || skel == null || string.IsNullOrEmpty(boneName)) return -1;
            int sb = skel.FindBone(boneName);
            for (int bi = 0; bi < skin.GetBindCount(); bi++)
            {
                if (sb >= 0 && skin.GetBindBone(bi) == sb) return bi;
                if (boneName.Equals(skin.GetBindName(bi), StringComparison.Ordinal)) return bi;
            }
            return -1;
        }

        // ------------------------------------------------------------------
        // Blender-Style Viewport Shading & Skinned Wireframe Overlays
        // ------------------------------------------------------------------

        public static int[] BuildUniqueEdgeIndices(int[] triIndices)
        {
            if (triIndices == null || triIndices.Length < 3) return Array.Empty<int>();
            var seen = new HashSet<ulong>(triIndices.Length);
            var edges = new List<int>(triIndices.Length * 2);
            void AddEdge(int a, int b)
            {
                if (a == b) return;
                uint lo = (uint)Math.Min(a, b);
                uint hi = (uint)Math.Max(a, b);
                ulong key = ((ulong)hi << 32) | lo;
                if (seen.Add(key))
                {
                    edges.Add(a);
                    edges.Add(b);
                }
            }
            for (int i = 0; i + 2 < triIndices.Length; i += 3)
            {
                int a = triIndices[i], b = triIndices[i + 1], c = triIndices[i + 2];
                AddEdge(a, b);
                AddEdge(b, c);
                AddEdge(c, a);
            }
            return edges.ToArray();
        }

        public static ShaderMaterial CreateWireframeLineMaterial(Color wireColor, float normalBias = 0.0012f, bool xRayNoDepth = false)
        {
            string renderMode = xRayNoDepth
                ? "render_mode unshaded, cull_disabled, depth_test_disabled, depth_draw_never;"
                : "render_mode unshaded, cull_disabled;";
            var shader = new Shader
            {
                Code = $@"shader_type spatial;
{renderMode}
uniform vec4 wire_color : source_color = vec4(0.06, 0.09, 0.14, 1.0);
uniform float normal_bias = 0.0012;
void vertex() {{
    VERTEX += NORMAL * normal_bias;
}}
void fragment() {{
    ALBEDO = wire_color.rgb;
    ALPHA = wire_color.a;
}}"
            };
            var mat = new ShaderMaterial { Shader = shader };
            if (xRayNoDepth) mat.RenderPriority = 20;
            mat.SetShaderParameter("wire_color", wireColor);
            mat.SetShaderParameter("normal_bias", normalBias);
            return mat;
        }

        public static StandardMaterial3D CreateWireframeClayMaterial(bool xRay = false)
        {
            if (xRay)
            {
                return new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
                    AlbedoColor = new Color(0.11f, 0.13f, 0.17f, 0.08f),
                    CullMode = BaseMaterial3D.CullModeEnum.Back
                };
            }
            return new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(0.11f, 0.13f, 0.17f),
                CullMode = BaseMaterial3D.CullModeEnum.Back
            };
        }

        private static StandardMaterial3D CreateXRaySurfaceMaterial(Material baseMat)
        {
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                AlbedoColor = new Color(0.75f, 0.85f, 0.95f, 0.42f)
            };
            if (baseMat is BaseMaterial3D std && std.AlbedoTexture != null)
            {
                mat.AlbedoTexture = std.AlbedoTexture;
                mat.AlbedoColor = new Color(std.AlbedoColor.R, std.AlbedoColor.G, std.AlbedoColor.B, 0.42f);
            }
            else if (baseMat is ShaderMaterial sm)
            {
                var texVar = sm.GetShaderParameter("albedo_tex");
                if (texVar.VariantType == Variant.Type.Object && texVar.AsGodotObject() is Texture2D tex)
                    mat.AlbedoTexture = tex;
            }
            return mat;
        }

        private static MeshInstance3D FindExistingWireframeOverlay(MeshInstance3D mi, int surfaceIdx)
        {
            var parent = mi?.GetParent();
            if (parent == null) return null;
            string expectedName = $"WireframeOverlay_{mi.Name}_s{surfaceIdx}";
            return parent.GetNodeOrNull<MeshInstance3D>(expectedName);
        }

        private static void RebuildWireframeLinesSurface(
            ArrayMesh wireMesh,
            Vector3[] verts,
            Vector3[] norms,
            int[] edgeIndices,
            int[] bones,
            float[] weights,
            int stride,
            Material lineMat)
        {
            if (wireMesh == null || verts == null || verts.Length == 0 || edgeIndices == null || edgeIndices.Length < 2) return;
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = verts;
            if (norms != null && norms.Length == verts.Length)
                arrays[(int)Mesh.ArrayType.Normal] = norms;
            arrays[(int)Mesh.ArrayType.Index] = edgeIndices;
            Mesh.ArrayFormat formatFlags = 0;
            if (bones != null && weights != null && (bones.Length == verts.Length * 4 || bones.Length == verts.Length * 8) && weights.Length == bones.Length)
            {
                arrays[(int)Mesh.ArrayType.Bones] = bones;
                arrays[(int)Mesh.ArrayType.Weights] = weights;
                if (bones.Length == verts.Length * 8) formatFlags |= Mesh.ArrayFormat.FlagUse8BoneWeights;
            }
            wireMesh.ClearSurfaces();
            wireMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays, null, null, formatFlags);
            if (lineMat != null) wireMesh.SurfaceSetMaterial(0, lineMat);
        }

        /// <summary>
        /// Syncs Blender-style solid/wireframe materials and skinned Lines overlays on every mesh in container.
        /// Note: never enables Visual Layer 21 (bit 20).
        /// </summary>
        public static void SyncContainerShadingAndWireframes(
            Node3D container,
            bool showWireframe,
            bool wireOnlyClay,
            bool heatmapActive,
            Material clayMat,
            Material wireLineMat,
            List<SurfaceState> paintSurfaces = null,
            bool xRayMode = false)
        {
            if (container == null || !GodotObject.IsInstanceValid(container)) return;

            var stateByKey = new Dictionary<(MeshInstance3D, int), SurfaceState>();
            if (paintSurfaces != null)
            {
                foreach (var ps in paintSurfaces)
                    if (ps?.MeshInstance != null) stateByKey[(ps.MeshInstance, ps.SurfaceIndex)] = ps;
            }

            Material xRayClayMat = xRayMode && wireOnlyClay ? CreateWireframeClayMaterial(xRay: true) : null;
            bool effectiveWireframe = showWireframe || xRayMode;

            foreach (var mi in CustomModelAutoRigger.CollectMeshes(container))
            {
                if (mi?.Mesh == null || !GodotObject.IsInstanceValid(mi)) continue;
                var parent = mi.GetParent();
                int surfCount = mi.Mesh.GetSurfaceCount();

                for (int s = 0; s < surfCount; s++)
                {
                    if (!heatmapActive)
                    {
                        if (wireOnlyClay)
                        {
                            mi.SetSurfaceOverrideMaterial(s, xRayMode ? xRayClayMat : clayMat);
                        }
                        else if (xRayMode)
                        {
                            Material baseMat = mi.Mesh.SurfaceGetMaterial(s);
                            mi.SetSurfaceOverrideMaterial(s, CreateXRaySurfaceMaterial(baseMat));
                        }
                        else
                        {
                            mi.SetSurfaceOverrideMaterial(s, null);
                        }
                    }

                    string wireName = $"WireframeOverlay_{mi.Name}_s{s}";
                    var overlay = parent?.GetNodeOrNull<MeshInstance3D>(wireName);

                    if (!effectiveWireframe)
                    {
                        if (overlay != null && GodotObject.IsInstanceValid(overlay)) overlay.Visible = false;
                        continue;
                    }

                    if (overlay == null || !GodotObject.IsInstanceValid(overlay))
                    {
                        var arrays = mi.Mesh.SurfaceGetArrays(s);
                        if (arrays == null || arrays.Count == 0) continue;
                        var verts = CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                        if (verts == null || verts.Length == 0) continue;
                        var norms = arrays.Count > (int)Mesh.ArrayType.Normal ? CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Normal]) : null;
                        var indices = arrays.Count > (int)Mesh.ArrayType.Index ? CustomModelAutoRigger.GetInt32Array(arrays[(int)Mesh.ArrayType.Index]) : null;
                        if (indices == null || indices.Length < 3)
                        {
                            indices = new int[verts.Length - verts.Length % 3];
                            for (int i = 0; i < indices.Length; i++) indices[i] = i;
                        }
                        var bones = arrays.Count > (int)Mesh.ArrayType.Bones ? CustomModelAutoRigger.GetInt32Array(arrays[(int)Mesh.ArrayType.Bones]) : null;
                        var weights = arrays.Count > (int)Mesh.ArrayType.Weights ? CustomModelAutoRigger.GetFloat32Array(arrays[(int)Mesh.ArrayType.Weights]) : null;
                        var edgeIndices = BuildUniqueEdgeIndices(indices);
                        if (edgeIndices.Length < 2) continue;

                        var wireMesh = new ArrayMesh();
                        int stride = (bones != null && bones.Length >= verts.Length * 8) ? 8 : 4;
                        RebuildWireframeLinesSurface(wireMesh, verts, norms, edgeIndices, bones, weights, stride, wireLineMat);

                        overlay = new MeshInstance3D
                        {
                            Name = wireName,
                            Mesh = wireMesh,
                            Skin = mi.Skin,
                            Transform = mi.Transform,
                            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                            MaterialOverride = wireLineMat,
                            Layers = 1u // isolated from Visual Layer 21
                        };
                        overlay.SetMeta("IsWireframeOverlay", true);
                        parent?.AddChild(overlay);
                        if (mi.Skeleton != null && !mi.Skeleton.IsEmpty)
                        {
                            overlay.Skeleton = overlay.GetPathTo(mi.GetNodeOrNull(mi.Skeleton));
                        }
                    }

                    if (stateByKey.TryGetValue((mi, s), out var ps))
                        ps.WireframeOverlay = overlay;

                    overlay.Transform = mi.Transform;
                    overlay.Layers &= ~(1u << 20);
                    overlay.MaterialOverride = wireLineMat;
                    overlay.Visible = mi.Visible && effectiveWireframe;
                }
            }
        }

        // ------------------------------------------------------------------
        // Fast Weight-Editing Helpers: Mirror Weights & Smooth Boundary
        // ------------------------------------------------------------------

        /// <summary>
        /// Mirrors skin weights across centerX from the source side to the opposite side of every surface
        /// (remapping _L &lt;-&gt; _R bone bind indices via heroSkeleton/heroSkin).
        /// </summary>
        public static bool MirrorWeightsSide(
            List<SurfaceState> surfaces,
            Skeleton3D heroSkeleton,
            Skin heroSkin,
            float centerX,
            bool leftToRight)
        {
            if (surfaces == null || surfaces.Count == 0 || heroSkeleton == null || heroSkin == null)
                return false;

            int bindCount = heroSkin.GetBindCount();
            if (bindCount <= 0) return false;

            var bindNames = new string[bindCount];
            for (int bi = 0; bi < bindCount; bi++)
            {
                int sb = heroSkin.GetBindBone(bi);
                bindNames[bi] = (sb >= 0 && sb < heroSkeleton.GetBoneCount())
                    ? heroSkeleton.GetBoneName(sb)
                    : heroSkin.GetBindName(bi);
            }

            var mirrorBindMap = new int[bindCount];
            for (int bi = 0; bi < bindCount; bi++)
            {
                string mir = MirrorBoneName(bindNames[bi]);
                int mirBi = mir != null ? ResolveBindIdx(heroSkin, heroSkeleton, mir) : -1;
                mirrorBindMap[bi] = mirBi >= 0 ? mirBi : bi;
            }

            // Detect which X sign corresponds to "_L" in heroSkeleton rest space.
            bool leftIsPositiveX = true;
            foreach (string probe in new[] { "arm_upper_L", "hand_L", "leg_upper_L", "ankle_L" })
            {
                int bL = heroSkeleton.FindBone(probe);
                string probeR = MirrorBoneName(probe);
                int bR = probeR != null ? heroSkeleton.FindBone(probeR) : -1;
                if (bL >= 0 && bR >= 0)
                {
                    float xL = heroSkeleton.GetBoneGlobalRest(bL).Origin.X;
                    float xR = heroSkeleton.GetBoneGlobalRest(bR).Origin.X;
                    if (MathF.Abs(xL - xR) > 1e-4f)
                    {
                        leftIsPositiveX = xL > xR;
                        break;
                    }
                }
            }

            // Source side sign relative to centerX.
            bool srcIsPositiveX = leftToRight ? leftIsPositiveX : !leftIsPositiveX;
            bool changedAny = false;
            const float midlineTol = 0.002f;
            const float cellSize = 0.03f;

            foreach (var s in surfaces)
            {
                if (s?.RestVerts == null || s.RestVerts.Length == 0) continue;
                int vc = s.RestVerts.Length;

                // Collect source-side vertices and index into a spatial grid.
                var srcVerts = new List<int>();
                var grid = new Dictionary<(int, int, int), List<int>>();
                for (int v = 0; v < vc; v++)
                {
                    float dx = s.RestVerts[v].X - centerX;
                    if (MathF.Abs(dx) < midlineTol) continue;
                    bool isPos = dx > 0f;
                    if (isPos != srcIsPositiveX) continue;
                    srcVerts.Add(v);
                    var key = (
                        (int)MathF.Floor(s.RestVerts[v].X / cellSize),
                        (int)MathF.Floor(s.RestVerts[v].Y / cellSize),
                        (int)MathF.Floor(s.RestVerts[v].Z / cellSize));
                    if (!grid.TryGetValue(key, out var bucket))
                    {
                        bucket = new List<int>(4);
                        grid[key] = bucket;
                    }
                    bucket.Add(v);
                }
                if (srcVerts.Count == 0) continue;

                var srcBonesSnap = (int[])s.SkinBones.Clone();
                var srcWeightsSnap = (float[])s.Weights.Clone();
                var accumDict = new Dictionary<int, float>(4);
                var visited = new bool[vc];

                for (int v = 0; v < vc; v++)
                {
                    if (visited[v]) continue;
                    float dx = s.RestVerts[v].X - centerX;
                    if (MathF.Abs(dx) < midlineTol) continue;
                    bool isPos = dx > 0f;
                    if (isPos == srcIsPositiveX) continue; // Only overwrite destination side.

                    int[] cluster = (s.WeldClusters != null && v < s.WeldClusters.Length && s.WeldClusters[v] != null)
                        ? s.WeldClusters[v]
                        : new[] { v };
                    foreach (int c in cluster) visited[c] = true;

                    Vector3 targetPos = new Vector3(2f * centerX - s.RestVerts[v].X, s.RestVerts[v].Y, s.RestVerts[v].Z);
                    int gx = (int)MathF.Floor(targetPos.X / cellSize);
                    int gy = (int)MathF.Floor(targetPos.Y / cellSize);
                    int gz = (int)MathF.Floor(targetPos.Z / cellSize);

                    int bestSrc = -1;
                    float bestD2 = float.MaxValue;
                    for (int ox = -2; ox <= 2; ox++)
                        for (int oy = -2; oy <= 2; oy++)
                            for (int oz = -2; oz <= 2; oz++)
                            {
                                if (!grid.TryGetValue((gx + ox, gy + oy, gz + oz), out var bucket)) continue;
                                foreach (int cand in bucket)
                                {
                                    float d2 = (s.RestVerts[cand] - targetPos).LengthSquared();
                                    if (d2 < bestD2) { bestD2 = d2; bestSrc = cand; }
                                }
                            }

                    if (bestSrc < 0)
                    {
                        foreach (int cand in srcVerts)
                        {
                            float d2 = (s.RestVerts[cand] - targetPos).LengthSquared();
                            if (d2 < bestD2) { bestD2 = d2; bestSrc = cand; }
                        }
                    }
                    if (bestSrc < 0) continue;

                    accumDict.Clear();
                    int srcOff = bestSrc * 4;
                    for (int k = 0; k < 4; k++)
                    {
                        float w = srcWeightsSnap[srcOff + k];
                        if (w <= 1e-5f) continue;
                        int bi = srcBonesSnap[srcOff + k];
                        int mBi = (bi >= 0 && bi < mirrorBindMap.Length) ? mirrorBindMap[bi] : bi;
                        accumDict.TryGetValue(mBi, out float existing);
                        accumDict[mBi] = existing + w;
                    }

                    var top = accumDict.Where(kv => kv.Value > 1e-4f).OrderByDescending(kv => kv.Value).Take(4).ToList();
                    if (top.Count == 0) continue;
                    float sum = top.Sum(kv => kv.Value);
                    var outB = new int[4];
                    var outW = new float[4];
                    for (int k = 0; k < top.Count; k++)
                    {
                        outB[k] = top[k].Key;
                        outW[k] = top[k].Value / sum;
                    }

                    foreach (int c in cluster)
                    {
                        int cOff = c * 4;
                        for (int k = 0; k < 4; k++)
                        {
                            if (s.SkinBones[cOff + k] != outB[k] || MathF.Abs(s.Weights[cOff + k] - outW[k]) > 1e-5f)
                                changedAny = true;
                            s.SkinBones[cOff + k] = outB[k];
                            s.Weights[cOff + k] = outW[k];
                        }
                    }
                }
            }
            return changedAny;
        }

        /// <summary>
        /// Smooths the weight falloff around targetBindIdx (and optional mirrorBindIdx) across topological neighbors,
        /// leaving unaffected areas of the mesh untouched.
        /// </summary>
        public static bool SmoothBoneBoundary(
            List<SurfaceState> surfaces,
            int targetBindIdx,
            int mirrorBindIdx = -1,
            int iterations = 2)
        {
            if (surfaces == null || surfaces.Count == 0 || targetBindIdx < 0) return false;
            bool changedAny = false;
            var curDict = new Dictionary<int, float>(6);

            for (int iter = 0; iter < Math.Max(1, iterations); iter++)
            {
                foreach (var s in surfaces)
                {
                    if (s?.RestVerts == null || s.Neighbors == null) continue;
                    int vc = s.RestVerts.Length;
                    var bonesSnap = (int[])s.SkinBones.Clone();
                    var weightsSnap = (float[])s.Weights.Clone();

                    float ReadWeight(int vIdx, int bindIdx)
                    {
                        if (bindIdx < 0) return 0f;
                        int off = vIdx * 4;
                        float total = 0f;
                        for (int k = 0; k < 4; k++)
                            if (bonesSnap[off + k] == bindIdx) total += weightsSnap[off + k];
                        return total;
                    }

                    var visited = new bool[vc];
                    for (int v = 0; v < vc; v++)
                    {
                        if (visited[v]) continue;
                        int[] nbrs = s.Neighbors[v];
                        if (nbrs == null || nbrs.Length == 0) continue;

                        int[] cluster = (s.WeldClusters != null && v < s.WeldClusters.Length && s.WeldClusters[v] != null)
                            ? s.WeldClusters[v]
                            : new[] { v };
                        foreach (int c in cluster) visited[c] = true;

                        foreach (int activeBi in new[] { targetBindIdx, mirrorBindIdx })
                        {
                            if (activeBi < 0) continue;
                            float curW = ReadWeight(v, activeBi);
                            float avgNbrW = 0f;
                            bool anyNbrHas = false;
                            int bestFallback = -1;
                            float bestFallbackW = -1f;

                            foreach (int nb in nbrs)
                            {
                                float nw = ReadWeight(nb, activeBi);
                                avgNbrW += nw;
                                if (nw > 0.001f) anyNbrHas = true;

                                int nbOff = nb * 4;
                                for (int k = 0; k < 4; k++)
                                {
                                    int bi = bonesSnap[nbOff + k];
                                    float w = weightsSnap[nbOff + k];
                                    if (bi != activeBi && w > bestFallbackW)
                                    {
                                        bestFallbackW = w;
                                        bestFallback = bi;
                                    }
                                }
                            }
                            if (curW <= 0.001f && !anyNbrHas) continue;
                            avgNbrW /= nbrs.Length;
                            float blended = Mathf.Clamp(curW * 0.55f + avgNbrW * 0.45f, 0f, 1f);
                            if (MathF.Abs(blended - curW) <= 1e-4f) continue;

                            curDict.Clear();
                            int cOff0 = v * 4;
                            for (int k = 0; k < 4; k++)
                            {
                                float w = s.Weights[cOff0 + k];
                                if (w <= 1e-5f) continue;
                                int bi = s.SkinBones[cOff0 + k];
                                curDict.TryGetValue(bi, out float prev);
                                curDict[bi] = prev + w;
                            }
                            int fallback = bestFallback >= 0 ? bestFallback : 0;
                            ScaleOthers(curDict, activeBi, blended, fallback);

                            var top = curDict.Where(kv => kv.Value > 1e-4f).OrderByDescending(kv => kv.Value).Take(4).ToList();
                            if (top.Count == 0) continue;
                            float sum = top.Sum(kv => kv.Value);
                            var outB = new int[4];
                            var outW = new float[4];
                            for (int k = 0; k < top.Count; k++)
                            {
                                outB[k] = top[k].Key;
                                outW[k] = top[k].Value / sum;
                            }

                            foreach (int c in cluster)
                            {
                                int cOff = c * 4;
                                for (int k = 0; k < 4; k++)
                                {
                                    if (s.SkinBones[cOff + k] != outB[k] || MathF.Abs(s.Weights[cOff + k] - outW[k]) > 1e-5f)
                                        changedAny = true;
                                    s.SkinBones[cOff + k] = outB[k];
                                    s.Weights[cOff + k] = outW[k];
                                }
                            }
                        }
                    }
                }
            }
            return changedAny;
        }
    }
}

