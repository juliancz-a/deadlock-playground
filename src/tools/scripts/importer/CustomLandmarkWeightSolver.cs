using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DeadlockPlayground.Importer
{
    /// <summary>
    /// Landmark-Pin Auto-Rigger backend: estimates 16 humanoid joint pins from a static mesh, computes
    /// connected-island + mesh-geodesic skin weights against a synthetic Deadlock-named skeleton, and builds
    /// that skeleton + skinned surfaces so the result can be fed to <c>CustomModelAutoRigger.RigPreRiggedModel</c>.
    /// All pins are stored in the imported scene's own space ("model space"); the solver maps them to hero
    /// space via <c>RiggingOptions.AlignedTransform</c>.
    /// </summary>
    public static class CustomLandmarkWeightSolver
    {
        public static readonly string[] PinNames =
        {
            "Head", "Neck", "Chest", "Pelvis",
            "ShoulderL", "ElbowL", "WristL", "HipL", "KneeL", "AnkleL",
            "ShoulderR", "ElbowR", "WristR", "HipR", "KneeR", "AnkleR"
        };

        // Synthetic bone table (index == skin bind index).
        public static readonly string[] DeadlockBoneNames =
        {
            "pelvis", "spine_0", "spine_1", "spine_2", "spine_3", "neck_0", "head",
            "clavicle_L", "arm_upper_L", "arm_lower_L", "hand_L",
            "clavicle_R", "arm_upper_R", "arm_lower_R", "hand_R",
            "leg_upper_L", "leg_lower_L", "ankle_L",
            "leg_upper_R", "leg_lower_R", "ankle_R"
        };
        private static string[] BoneNames => DeadlockBoneNames;

        private static readonly int[] BoneParents =
        {
            -1, 0, 1, 2, 3, 4, 5,
            4, 7, 8, 9,
            4, 11, 12, 13,
            0, 15, 16,
            0, 18, 19
        };

        // Precomputed tree hop distances between the 21 bones for anatomical weight gating.
        private static readonly int[,] BoneHopDist = BuildBoneHopDist();

        private static int[,] BuildBoneHopDist()
        {
            int n = BoneParents.Length;
            var dist = new int[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    dist[i, j] = i == j ? 0 : 99;
            for (int i = 0; i < n; i++)
                if (BoneParents[i] >= 0) { dist[i, BoneParents[i]] = 1; dist[BoneParents[i], i] = 1; }
            for (int k = 0; k < n; k++)
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                        dist[i, j] = Math.Min(dist[i, j], dist[i, k] + dist[k, j]);
            return dist;
        }

        private static bool IsBoneAllowedForPrimary(int primary, int candidate)
        {
            if (primary == candidate) return true;
            // Distal extremities (hands, ankles/feet, head) only blend with immediate parent/child (hop <= 1).
            bool isDistal = primary == 6 || primary == 10 || primary == 14 || primary == 17 || primary == 20;
            int maxHop = isDistal ? 1 : 2;
            return BoneHopDist[primary, candidate] <= maxHop;
        }

        public static string MirrorName(string pin)
        {
            if (pin.EndsWith("L")) return pin.Substring(0, pin.Length - 1) + "R";
            if (pin.EndsWith("R")) return pin.Substring(0, pin.Length - 1) + "L";
            return null;
        }

        // ------------------------------------------------------------------
        // Mesh gathering
        // ------------------------------------------------------------------

        private sealed class SurfaceData
        {
            public MeshInstance3D Mi;
            public int Surface;
            public Godot.Collections.Array Arrays;
            public Vector3[] SceneVerts;   // evaluated rest-pose vertices in scene space (respecting existing Skin/Skeleton3D)
            public Vector3[] SceneNorms;   // evaluated rest-pose normals in scene space (or null if absent)
            public int[] Indices;
        }

        private static List<SurfaceData> GatherSurfaces(Node3D scene, IEnumerable<MeshInstance3D> meshes)
        {
            var list = new List<SurfaceData>();
            var fallbackSkel = CustomModelAutoRigger.FindSkeleton(scene);
            foreach (var mi in meshes)
            {
                if (mi?.Mesh == null) continue;
                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                {
                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count == 0) continue;
                    var verts = CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    if (verts == null || verts.Length == 0) continue;
                    var rawNorms = arrays.Count > (int)Mesh.ArrayType.Normal ? CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Normal]) : null;
                    var rawBones = arrays.Count > (int)Mesh.ArrayType.Bones ? CustomModelAutoRigger.GetInt32Array(arrays[(int)Mesh.ArrayType.Bones]) : null;
                    var rawWeights = arrays.Count > (int)Mesh.ArrayType.Weights ? CustomModelAutoRigger.GetFloat32Array(arrays[(int)Mesh.ArrayType.Weights]) : null;
                    CustomModelAutoRigger.EvaluateMeshSurfaceInTargetSpace(
                        mi, scene, Transform3D.Identity, fallbackSkel,
                        verts, rawNorms, rawBones, rawWeights,
                        out var sv, out var sn);
                    int[] idx = arrays.Count > (int)Mesh.ArrayType.Index ? CustomModelAutoRigger.GetInt32Array(arrays[(int)Mesh.ArrayType.Index]) : null;
                    if (idx == null || idx.Length == 0)
                    {
                        idx = new int[verts.Length - verts.Length % 3];
                        for (int i = 0; i < idx.Length; i++) idx[i] = i;
                    }
                    bool hasNorms = rawNorms != null && rawNorms.Length == verts.Length;
                    list.Add(new SurfaceData
                    {
                        Mi = mi,
                        Surface = s,
                        Arrays = arrays,
                        SceneVerts = sv,
                        SceneNorms = hasNorms ? sn : null,
                        Indices = idx
                    });
                }
            }
            return list;
        }

        // ------------------------------------------------------------------
        // Landmark estimation (model space, upright Y-up humanoid)
        // ------------------------------------------------------------------

        public static Dictionary<string, Vector3> EstimateLandmarks(Node3D scene, IEnumerable<MeshInstance3D> meshes)
        {
            var pts = new List<Vector3>();
            foreach (var sd in GatherSurfaces(scene, meshes)) pts.AddRange(sd.SceneVerts);
            return EstimateLandmarksFromPoints(pts);
        }

        public static Dictionary<string, Vector3> EstimateLandmarksFromPoints(List<Vector3> pts)
        {
            var pins = new Dictionary<string, Vector3>();
            if (pts.Count == 0) return pins;

            float minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
            float H = Math.Max(0.01f, maxY - minY);

            List<Vector3> Slice(float yAbs, float halfThick) =>
                pts.Where(p => Math.Abs(p.Y - yAbs) <= halfThick).ToList();
            float Yf(float f) => minY + f * H;

            // Body centre X from the leg band.
            var legBand = pts.Where(p => p.Y < Yf(0.30f)).ToList();
            if (legBand.Count == 0) legBand = pts;
            float cx = (legBand.Min(p => p.X) + legBand.Max(p => p.X)) * 0.5f;

            float ZAt(float yAbs, float half = 0.03f)
            {
                var sl = Slice(yAbs, half * H).Where(p => Math.Abs(p.X - cx) < 0.2f * H).ToList();
                if (sl.Count == 0) sl = Slice(yAbs, half * 3 * H);
                return sl.Count == 0 ? pts.Average(p => p.Z) : sl.Average(p => p.Z);
            }

            // Crotch: first Y from the top at which the leg band splits into two separate columns.
            float crotchY = Yf(0.47f);
            int stable = 0;
            for (float f = 0.55f; f >= 0.30f; f -= 0.01f)
            {
                var sl = Slice(Yf(f), 0.01f * H).Where(p => Math.Abs(p.X - cx) < 0.25f * H).Select(p => p.X).OrderBy(x => x).ToList();
                bool split = false;
                if (sl.Count >= 2 && sl.First() < cx && sl.Last() > cx)
                {
                    for (int i = 1; i < sl.Count; i++)
                        if (sl[i] - sl[i - 1] > 0.02f * H && sl[i - 1] < cx + 0.02f * H && sl[i] > cx - 0.02f * H) { split = true; break; }
                }
                stable = split ? stable + 1 : 0;
                if (stable >= 3) { crotchY = Yf(f + 0.02f); break; }
            }

            float hipY = crotchY + 0.05f * H;
            float ankleY = minY + 0.05f * H;

            float SideMeanX(float yAbs, float half, int sign, float fallback)
            {
                var sl = Slice(yAbs, half * H).Where(p => (p.X - cx) * sign > 0.01f * H).ToList();
                return sl.Count == 0 ? fallback : sl.Average(p => p.X);
            }

            float shoulderY = Yf(0.82f);
            float neckY = Yf(0.855f);
            float headY = Yf(0.93f);

            var sliceShoulder = Slice(shoulderY, 0.02f * H);
            float shoulderHalf = sliceShoulder.Count == 0 ? 0.12f * H : sliceShoulder.Max(p => Math.Abs(p.X - cx));
            var sliceChest = Slice(Yf(0.70f), 0.02f * H);
            float torsoHalf = sliceChest.Count == 0 ? 0.12f * H : sliceChest.Max(p => Math.Abs(p.X - cx));
            bool armsOut = shoulderHalf > 0.38f * H;

            float zMid = ZAt(Yf(0.55f));
            float pelvisY = hipY + 0.02f * H;
            float chinY = Yf(0.885f);
            var head = new Vector3(cx, headY, ZAt(headY, 0.05f));
            var chin = new Vector3(cx, chinY, ZAt(chinY, 0.03f));
            var neck = new Vector3(cx, neckY, ZAt(neckY));
            var pelvis = new Vector3(cx, pelvisY, zMid);
            var chest = new Vector3(cx, Mathf.Lerp(pelvisY, neckY, 0.6f), ZAt(Mathf.Lerp(pelvisY, neckY, 0.6f)));
            pins["Chin"] = chin; pins["Head"] = head; pins["Neck"] = neck; pins["Chest"] = chest; pins["Pelvis"] = pelvis;

            // +X side is labelled "L" initially; the rig builder auto-swaps against the hero skeleton.
            foreach (int sign in new[] { 1, -1 })
            {
                string sfx = sign > 0 ? "L" : "R";
                float hipX = SideMeanX(crotchY - 0.08f * H, 0.02f, sign, cx + sign * 0.06f * H);
                float ankleX = SideMeanX(ankleY, 0.03f, sign, hipX);
                var hip = new Vector3(hipX, hipY, ZAt(hipY));
                var ankle = new Vector3(ankleX, ankleY, ZAt(ankleY, 0.03f));
                var knee = hip.Lerp(ankle, 0.5f);
                knee.Z = hip.Z + (ankle.Z - hip.Z) * 0.5f;
                pins["Hip" + sfx] = hip; pins["Knee" + sfx] = knee; pins["Ankle" + sfx] = ankle;

                float shX = armsOut ? cx + sign * torsoHalf * 1.05f : cx + sign * shoulderHalf * 0.82f;
                var shoulder = new Vector3(shX, shoulderY, ZAt(shoulderY));
                Vector3 elbow, wrist;
                if (armsOut)
                {
                    var armPts = sliceShoulder.Where(p => (p.X - cx) * sign > 0).ToList();
                    float outer = armPts.Count == 0 ? shoulderHalf : armPts.Max(p => Math.Abs(p.X - cx));
                    wrist = new Vector3(cx + sign * (outer - 0.09f * H), shoulderY, shoulder.Z);
                    elbow = shoulder.Lerp(wrist, 0.5f);
                }
                else
                {
                    float OuterX(float yAbs, float fallback)
                    {
                        var sl = Slice(yAbs, 0.02f * H).Where(p => (p.X - cx) * sign > 0.04f * H).ToList();
                        return sl.Count == 0 ? fallback : sl.Max(p => Math.Abs(p.X - cx));
                    }
                    float eo = OuterX(Yf(0.63f), Math.Abs(shX - cx) + 0.03f * H);
                    float wo = OuterX(Yf(0.47f), Math.Abs(shX - cx) + 0.03f * H);
                    elbow = new Vector3(cx + sign * Math.Max(0.02f * H, eo - 0.035f * H), Yf(0.63f), ZAt(Yf(0.63f)));
                    wrist = new Vector3(cx + sign * Math.Max(0.02f * H, wo - 0.03f * H), Yf(0.47f), ZAt(Yf(0.47f)));
                }
                pins["Shoulder" + sfx] = shoulder; pins["Elbow" + sfx] = elbow; pins["Wrist" + sfx] = wrist;
                Vector3 armDir = (wrist - elbow).LengthSquared() > 1e-6f ? (wrist - elbow).Normalized() : new Vector3(sign, 0f, 0f);
                pins["HandTip" + sfx] = wrist + armDir * (0.095f * H);
            }
            return pins;
        }

        // ------------------------------------------------------------------
        // Bone geometry from pins
        // ------------------------------------------------------------------

        private sealed class BoneGeom
        {
            public Vector3[] Origin = new Vector3[BoneNames.Length];
            public Vector3[] SegStart = new Vector3[BoneNames.Length];
            public Vector3[] SegEnd = new Vector3[BoneNames.Length];
            public float[] Radius = new float[BoneNames.Length];
            public bool[] AllowBelow = new bool[BoneNames.Length];
            public bool[] AllowBeyond = new bool[BoneNames.Length];
        }

        private static BoneGeom BuildBoneGeom(Dictionary<string, Vector3> p, float H, float footFwdZ)
        {
            var g = new BoneGeom();
            Vector3 pelvis = p["Pelvis"], chest = p["Chest"], neck = p["Neck"], headPin = p["Head"];
            g.Origin[0] = pelvis;
            g.Origin[1] = pelvis.Lerp(chest, 0.33f);
            g.Origin[2] = pelvis.Lerp(chest, 0.66f);
            g.Origin[3] = chest;
            g.Origin[4] = chest.Lerp(neck, 0.6f);
            g.Origin[5] = neck;
            g.Origin[6] = neck.Lerp(headPin, 0.45f);

            Vector3 midHip = p["HipL"].Lerp(p["HipR"], 0.5f);
            g.SegStart[0] = midHip - Vector3.Up * 0.03f * H; g.SegEnd[0] = g.Origin[1]; g.AllowBelow[0] = true;
            for (int i = 1; i <= 3; i++) { g.SegStart[i] = g.Origin[i]; g.SegEnd[i] = g.Origin[i + 1]; }
            g.SegStart[4] = g.Origin[4]; g.SegEnd[4] = neck;
            g.SegStart[5] = neck; g.SegEnd[5] = g.Origin[6];
            g.SegStart[6] = g.Origin[6]; g.SegEnd[6] = headPin + (headPin - neck); g.AllowBeyond[6] = true;

            float[] radiusF = new float[BoneNames.Length];
            radiusF[0] = 0.11f; radiusF[1] = 0.10f; radiusF[2] = 0.10f; radiusF[3] = 0.11f; radiusF[4] = 0.11f;
            radiusF[5] = 0.04f; radiusF[6] = 0.07f;

            foreach (bool left in new[] { true, false })
            {
                string s = left ? "L" : "R";
                int c = left ? 7 : 11, au = c + 1, al = c + 2, hd = c + 3;
                int lu = left ? 15 : 18, ll = lu + 1, an = lu + 2;
                Vector3 sh = p["Shoulder" + s], el = p["Elbow" + s], wr = p["Wrist" + s];
                Vector3 hip = p["Hip" + s], kn = p["Knee" + s], ak = p["Ankle" + s];

                g.Origin[c] = neck + (sh - neck) * 0.2f;
                g.Origin[au] = sh; g.Origin[al] = el; g.Origin[hd] = wr;
                g.Origin[lu] = hip; g.Origin[ll] = kn; g.Origin[an] = ak;

                g.SegStart[c] = g.Origin[c]; g.SegEnd[c] = sh; radiusF[c] = 0.035f;
                g.SegStart[au] = sh; g.SegEnd[au] = el; radiusF[au] = 0.035f;
                g.SegStart[al] = el; g.SegEnd[al] = wr; radiusF[al] = 0.03f;
                Vector3 handDir = (wr - el).LengthSquared() > 1e-6f ? (wr - el).Normalized() : -Vector3.Up;
                g.SegStart[hd] = wr; g.SegEnd[hd] = wr + handDir * 0.11f * H; radiusF[hd] = 0.042f; g.AllowBeyond[hd] = true; g.AllowBelow[hd] = true;
                g.SegStart[lu] = hip; g.SegEnd[lu] = kn; radiusF[lu] = 0.055f;
                g.SegStart[ll] = kn; g.SegEnd[ll] = ak; radiusF[ll] = 0.04f;
                g.SegStart[an] = ak; g.SegEnd[an] = new Vector3(ak.X, ak.Y - 0.04f * H, ak.Z + footFwdZ * 0.145f * H); radiusF[an] = 0.058f; g.AllowBeyond[an] = true; g.AllowBelow[an] = true;
            }
            for (int i = 0; i < radiusF.Length; i++) g.Radius[i] = Math.Max(0.005f, radiusF[i] * H);
            return g;
        }

        private static float CapsuleScore(Vector3 pt, Vector3 a, Vector3 b, float radius, out float rawT)
        {
            Vector3 ab = b - a;
            float len2 = ab.LengthSquared();
            rawT = len2 < 1e-10f ? 0f : (pt - a).Dot(ab) / len2;
            float ct = Mathf.Clamp(rawT, 0f, 1f);
            return (pt - (a + ab * ct)).Length() / radius;
        }

        // ------------------------------------------------------------------
        // Geodesic weight solve
        // ------------------------------------------------------------------

        /// <summary>
        /// Computes 4-influence skin weights for every vertex of every surface. Vertices are in hero/aligned space.
        /// Returns per-surface bone index and weight arrays (stride 4).
        /// </summary>
        private static void SolveWeights(List<Vector3[]> alignedVerts, List<int[]> indices, BoneGeom g, float H,
            out List<int[]> outBones, out List<float[]> outWeights)
        {
            int nb = BoneNames.Length;
            float weldCell = 0.001f;
            var weldMap = new Dictionary<(long, long, long), int>();
            var weldPos = new List<Vector3>();
            var vertToWeld = new List<int[]>();
            for (int s = 0; s < alignedVerts.Count; s++)
            {
                var v = alignedVerts[s];
                var map = new int[v.Length];
                for (int i = 0; i < v.Length; i++)
                {
                    var key = ((long)MathF.Round(v[i].X / weldCell), (long)MathF.Round(v[i].Y / weldCell), (long)MathF.Round(v[i].Z / weldCell));
                    if (!weldMap.TryGetValue(key, out int id))
                    {
                        id = weldPos.Count;
                        weldMap[key] = id;
                        weldPos.Add(v[i]);
                    }
                    map[i] = id;
                }
                vertToWeld.Add(map);
            }

            int n = weldPos.Count;
            var adj = new List<(int, float)>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<(int, float)>(6);
            var seenEdges = new HashSet<long>();
            void AddEdge(int a, int b)
            {
                if (a == b) return;
                long k = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (!seenEdges.Add(k)) return;
                float d = (weldPos[a] - weldPos[b]).Length();
                adj[a].Add((b, d)); adj[b].Add((a, d));
            }
            for (int s = 0; s < indices.Count; s++)
            {
                var idx = indices[s]; var map = vertToWeld[s];
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int a = map[idx[t]], b = map[idx[t + 1]], c = map[idx[t + 2]];
                    AddEdge(a, b); AddEdge(b, c); AddEdge(c, a);
                }
            }

            // Label every weld node by its nearest bone capsule (with lateral armpit plane gating) and seed interior segments.
            float chestX = g.Origin[3].X;
            float shLX = g.Origin[8].X, shRX = g.Origin[12].X;
            float armSpanL = MathF.Abs(shLX - chestX), armSpanR = MathF.Abs(shRX - chestX);
            float armDirL = shLX >= chestX ? 1f : -1f, armDirR = shRX >= chestX ? 1f : -1f;
            float torsoArmTopY = MathF.Max(g.Origin[8].Y, g.Origin[12].Y) + 0.04f * H;
            float torsoPelvisY = g.Origin[0].Y;

            var label = new int[n];
            var seedsPerBone = new List<int>[nb];
            for (int b = 0; b < nb; b++) seedsPerBone[b] = new List<int>();
            for (int i = 0; i < n; i++)
            {
                Vector3 wp = weldPos[i];
                float latL = armSpanL > 1e-4f ? (wp.X - chestX) * armDirL / armSpanL : 0f;
                float latR = armSpanR > 1e-4f ? (wp.X - chestX) * armDirR / armSpanR : 0f;
                bool inArmHeight = wp.Y <= torsoArmTopY && wp.Y >= torsoPelvisY - 0.03f * H;

                float best = float.MaxValue; int bi = 0; float bt = 0.5f;
                for (int b = 0; b < nb; b++)
                {
                    float sc = CapsuleScore(wp, g.SegStart[b], g.SegEnd[b], g.Radius[b], out float t);
                    if (inArmHeight)
                    {
                        if (b == 8 && latL < 0.68f) sc *= 2.2f;
                        else if ((b == 9 || b == 10) && latL < 0.60f) sc *= 1.45f;
                        else if (b == 12 && latR < 0.68f) sc *= 2.2f;
                        else if ((b == 13 || b == 14) && latR < 0.60f) sc *= 1.45f;
                        else if (b >= 0 && b <= 4 && (latL > 0.85f || latR > 0.85f)) sc *= 2.0f;
                    }
                    if (sc < best) { best = sc; bi = b; bt = t; }
                }
                label[i] = bi;
                bool okLow = bt >= 0.12f || g.AllowBelow[bi];
                bool okHigh = bt <= 0.88f || g.AllowBeyond[bi];
                if (okLow && okHigh) seedsPerBone[bi].Add(i);
            }

            // Multi-source Dijkstra geodesic distance per bone (21 independent passes run in parallel).
            var G = new float[nb][];
            System.Threading.Tasks.Parallel.For(0, nb, b =>
            {
                var dist = new float[n];
                Array.Fill(dist, float.PositiveInfinity);
                if (seedsPerBone[b].Count == 0) { G[b] = dist; return; }
                var pq = new PriorityQueue<int, float>(seedsPerBone[b].Count);
                foreach (int sIdx in seedsPerBone[b]) { dist[sIdx] = 0f; pq.Enqueue(sIdx, 0f); }
                while (pq.TryDequeue(out int u, out float du))
                {
                    if (du > dist[u]) continue;
                    var neighbors = adj[u];
                    for (int ei = 0; ei < neighbors.Count; ei++)
                    {
                        var (v, w) = neighbors[ei];
                        float nd = du + w;
                        if (nd < dist[v]) { dist[v] = nd; pq.Enqueue(v, nd); }
                    }
                }
                G[b] = dist;
            });

            // Dense IDW weights per weld node (strictly gated by anatomical bone-chain hops to prevent coat/torso/leg bleed into hands/feet).
            float eps = 0.02f * H;
            var dense = new float[n * nb];
            for (int i = 0; i < n; i++)
            {
                int primary = label[i];
                float sum = 0f;
                int rowOff = i * nb;
                for (int b = 0; b < nb; b++)
                {
                    if (!IsBoneAllowedForPrimary(primary, b)) continue;
                    float gd = G[b][i];
                    if (float.IsPositiveInfinity(gd)) continue;
                    float w = 1f / ((gd + eps) * (gd + eps));
                    dense[rowOff + b] = w; sum += w;
                }
                if (sum <= 0f)
                {
                    // Detached island or isolated node: fall back to capsule IDW within allowed neighborhood.
                    for (int b = 0; b < nb; b++)
                    {
                        if (!IsBoneAllowedForPrimary(primary, b)) continue;
                        float sc = CapsuleScore(weldPos[i], g.SegStart[b], g.SegEnd[b], g.Radius[b], out _);
                        float w = 1f / ((sc + 0.1f) * (sc + 0.1f) * (sc + 0.1f) * (sc + 0.1f));
                        dense[rowOff + b] = w; sum += w;
                    }
                }
                if (sum <= 0f) { dense[rowOff + primary] = 1f; sum = 1f; }
                for (int b = 0; b < nb; b++) dense[rowOff + b] /= sum;
            }

            // 1 light global Laplacian pass + 4 targeted joint-transition smoothing passes (maxWeight < 0.85), double-buffered.
            var next = new float[dense.Length];
            for (int iter = 0; iter < 5; iter++)
            {
                bool transitionOnly = iter >= 1;
                float selfKeep = transitionOnly ? 0.65f : 0.75f;
                float neighborMix = 1f - selfKeep;
                for (int i = 0; i < n; i++)
                {
                    int rowOff = i * nb;
                    int primary = label[i];
                    var neighbors = adj[i];
                    if (neighbors.Count == 0)
                    {
                        Array.Copy(dense, rowOff, next, rowOff, nb);
                        continue;
                    }
                    if (transitionOnly)
                    {
                        float maxW = 0f;
                        for (int b = 0; b < nb; b++)
                            if (dense[rowOff + b] > maxW) maxW = dense[rowOff + b];
                        if (maxW >= 0.85f)
                        {
                            Array.Copy(dense, rowOff, next, rowOff, nb);
                            continue;
                        }
                    }
                    float rowSum = 0f;
                    float invDeg = 1f / neighbors.Count;
                    for (int b = 0; b < nb; b++)
                    {
                        if (!IsBoneAllowedForPrimary(primary, b)) { next[rowOff + b] = 0f; continue; }
                        float avg = 0f;
                        for (int ni = 0; ni < neighbors.Count; ni++)
                            avg += dense[neighbors[ni].Item1 * nb + b];
                        avg *= invDeg;
                        float val = dense[rowOff + b] * selfKeep + avg * neighborMix;
                        next[rowOff + b] = val;
                        rowSum += val;
                    }
                    if (rowSum > 1e-6f)
                    {
                        float invSum = 1f / rowSum;
                        for (int b = 0; b < nb; b++) next[rowOff + b] *= invSum;
                    }
                    else
                    {
                        next[rowOff + primary] = 1f;
                    }
                }
                (dense, next) = (next, dense);
            }

            // Zero-allocation Top-4 normalisation.
            outBones = new List<int[]>();
            outWeights = new List<float[]>();
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
                    if (tot <= 1e-6f) { bones[v * 4] = 0; weights[v * 4] = 1f; continue; }
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
        }

        private static int[,] BuildHopDistForSpecs(List<ReferenceRigDef.ResolvedBoneSpec> specs)
        {
            int n = specs.Count;
            var dist = new int[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    dist[i, j] = i == j ? 0 : 99;
            for (int i = 0; i < n; i++)
            {
                int p = specs[i].ParentIndex;
                if (p >= 0 && p < n) { dist[i, p] = 1; dist[p, i] = 1; }
            }
            for (int k = 0; k < n; k++)
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                        if (dist[i, k] + dist[k, j] < dist[i, j])
                            dist[i, j] = dist[i, k] + dist[k, j];
            return dist;
        }

        private static bool IsBoneAllowedForSpec(List<ReferenceRigDef.ResolvedBoneSpec> specs, int[,] hopDist, int primary, int candidate)
        {
            if (primary == candidate) return true;
            var pSpec = specs[primary];
            var cSpec = specs[candidate];

            // Never allow Left-side bones to blend with Right-side bones (e.g. leg_upper_L <-> leg_upper_R through pelvis)
            bool pL = pSpec.Name.EndsWith("_L", StringComparison.OrdinalIgnoreCase);
            bool pR = pSpec.Name.EndsWith("_R", StringComparison.OrdinalIgnoreCase);
            bool cL = cSpec.Name.EndsWith("_L", StringComparison.OrdinalIgnoreCase);
            bool cR = cSpec.Name.EndsWith("_R", StringComparison.OrdinalIgnoreCase);
            if ((pL && cR) || (pR && cL)) return false;

            // Fingers only blend with their own immediate parent/child (hop <= 1) so adjacent fingers never bleed;
            // allow hand <-> finger_*_0 (hop == 2 through metacarpal) so the knuckle seam never forms a hard cliff,
            // AND allow sibling metacarpals on the same hand to blend smoothly across the palm!
            if (pSpec.Role == ReferenceBoneRole.Finger || cSpec.Role == ReferenceBoneRole.Finger)
            {
                if (hopDist[primary, candidate] <= 1) return true;
                if (hopDist[primary, candidate] == 2)
                {
                    bool pIsHand = pSpec.Name.StartsWith("hand_", StringComparison.OrdinalIgnoreCase);
                    bool cIsHand = cSpec.Name.StartsWith("hand_", StringComparison.OrdinalIgnoreCase);
                    if (pIsHand || cIsHand) return true;

                    bool pIsMeta = pSpec.Name.Contains("_meta_", StringComparison.OrdinalIgnoreCase);
                    bool cIsMeta = cSpec.Name.Contains("_meta_", StringComparison.OrdinalIgnoreCase);
                    if (pIsMeta && cIsMeta && pSpec.ParentIndex == cSpec.ParentIndex) return true;
                }
                return false;
            }
            // Cloth chains only blend along their own chain + parent bone (hop <= 1)
            if (pSpec.Role == ReferenceBoneRole.Cloth || cSpec.Role == ReferenceBoneRole.Cloth)
                return hopDist[primary, candidate] <= 1;
            bool isDistal = pSpec.Name.Equals("head", StringComparison.OrdinalIgnoreCase) ||
                            pSpec.Name.StartsWith("hand_", StringComparison.OrdinalIgnoreCase) ||
                            pSpec.Name.StartsWith("ankle_", StringComparison.OrdinalIgnoreCase) ||
                            pSpec.Name.StartsWith("toe_", StringComparison.OrdinalIgnoreCase);
            int maxHop = isDistal ? 1 : 2;
            return hopDist[primary, candidate] <= maxHop;
        }

        private static void SolveWeightsModular(
            List<Vector3[]> alignedVerts,
            List<int[]> indices,
            List<ReferenceRigDef.ResolvedBoneSpec> specs,
            float H,
            float? chinPlaneY,
            out List<int[]> outBones,
            out List<float[]> outWeights)
        {
            int nb = specs.Count;
            var hopDist = BuildHopDistForSpecs(specs);
            float weldCell = 0.001f;
            var weldMap = new Dictionary<(long, long, long), int>();
            var weldPos = new List<Vector3>();
            var vertToWeld = new List<int[]>();
            for (int s = 0; s < alignedVerts.Count; s++)
            {
                var v = alignedVerts[s];
                var map = new int[v.Length];
                for (int i = 0; i < v.Length; i++)
                {
                    var key = ((long)MathF.Round(v[i].X / weldCell), (long)MathF.Round(v[i].Y / weldCell), (long)MathF.Round(v[i].Z / weldCell));
                    if (!weldMap.TryGetValue(key, out int id))
                    {
                        id = weldPos.Count;
                        weldMap[key] = id;
                        weldPos.Add(v[i]);
                    }
                    map[i] = id;
                }
                vertToWeld.Add(map);
            }

            int n = weldPos.Count;
            var adj = new List<(int, float)>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<(int, float)>(6);
            var seenEdges = new HashSet<long>();
            void AddEdge(int a, int b)
            {
                if (a == b) return;
                long k = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (!seenEdges.Add(k)) return;
                float d = (weldPos[a] - weldPos[b]).Length();
                adj[a].Add((b, d)); adj[b].Add((a, d));
            }
            for (int s = 0; s < indices.Count; s++)
            {
                var idx = indices[s]; var map = vertToWeld[s];
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int a = map[idx[t]], b = map[idx[t + 1]], c = map[idx[t + 2]];
                    AddEdge(a, b); AddEdge(b, c); AddEdge(c, a);
                }
            }

            // Locate key torso/arm indices dynamically for armpit lateral plane gating
            int idxPelvis = specs.FindIndex(b => b.Name.Equals("pelvis", StringComparison.OrdinalIgnoreCase));
            int idxArmUpL = specs.FindIndex(b => b.Name.Equals("arm_upper_L", StringComparison.OrdinalIgnoreCase));
            int idxArmLoL = specs.FindIndex(b => b.Name.Equals("arm_lower_L", StringComparison.OrdinalIgnoreCase));
            int idxHandL = specs.FindIndex(b => b.Name.Equals("hand_L", StringComparison.OrdinalIgnoreCase));
            int idxArmUpR = specs.FindIndex(b => b.Name.Equals("arm_upper_R", StringComparison.OrdinalIgnoreCase));
            int idxArmLoR = specs.FindIndex(b => b.Name.Equals("arm_lower_R", StringComparison.OrdinalIgnoreCase));
            int idxHandR = specs.FindIndex(b => b.Name.Equals("hand_R", StringComparison.OrdinalIgnoreCase));
            var torsoSet = new HashSet<int>();
            for (int b = 0; b < nb; b++)
                if (specs[b].Name.Equals("pelvis", StringComparison.OrdinalIgnoreCase) || specs[b].Name.StartsWith("spine_", StringComparison.OrdinalIgnoreCase))
                    torsoSet.Add(b);

            float chestX = idxPelvis >= 0 ? specs[idxPelvis].Origin.X : 0f;
            float shLX = idxArmUpL >= 0 ? specs[idxArmUpL].Origin.X : chestX + 0.15f * H;
            float shRX = idxArmUpR >= 0 ? specs[idxArmUpR].Origin.X : chestX - 0.15f * H;
            float armSpanL = MathF.Abs(shLX - chestX), armSpanR = MathF.Abs(shRX - chestX);
            float armDirL = shLX >= chestX ? 1f : -1f, armDirR = shRX >= chestX ? 1f : -1f;
            float torsoArmTopY = MathF.Max(idxArmUpL >= 0 ? specs[idxArmUpL].Origin.Y : H, idxArmUpR >= 0 ? specs[idxArmUpR].Origin.Y : H) + 0.04f * H;
            float torsoPelvisY = idxPelvis >= 0 ? specs[idxPelvis].Origin.Y : 0.45f * H;

            var label = new int[n];
            var seedsPerBone = new List<int>[nb];
            for (int b = 0; b < nb; b++) seedsPerBone[b] = new List<int>();
            for (int i = 0; i < n; i++)
            {
                Vector3 wp = weldPos[i];
                float latL = armSpanL > 1e-4f ? (wp.X - chestX) * armDirL / armSpanL : 0f;
                float latR = armSpanR > 1e-4f ? (wp.X - chestX) * armDirR / armSpanR : 0f;
                bool inArmHeight = wp.Y <= torsoArmTopY && wp.Y >= torsoPelvisY - 0.03f * H;

                float best = float.MaxValue; int bi = 0; float bt = 0.5f;
                for (int b = 0; b < nb; b++)
                {
                    var sp = specs[b];
                    float sc = CapsuleScore(wp, sp.SegStart, sp.SegEnd, sp.Radius, out float t);
                    if (inArmHeight)
                    {
                        if (b == idxArmUpL && latL < 0.68f) sc *= 2.2f;
                        else if ((b == idxArmLoL || b == idxHandL) && latL < 0.60f) sc *= 1.45f;
                        else if (b == idxArmUpR && latR < 0.68f) sc *= 2.2f;
                        else if ((b == idxArmLoR || b == idxHandR) && latR < 0.60f) sc *= 1.45f;
                        else if (torsoSet.Contains(b) && (latL > 0.85f || latR > 0.85f)) sc *= 2.0f;
                    }
                    if (sc < best) { best = sc; bi = b; bt = t; }
                }
                label[i] = bi;
                bool okLow = bt >= 0.12f || specs[bi].AllowBelow;
                bool okHigh = bt <= 0.88f || specs[bi].AllowBeyond;
                if (okLow && okHigh) seedsPerBone[bi].Add(i);
            }

            // Safety guarantee: ensure no short finger phalanx or metacarpal ends up with 0 Dijkstra seeds
            // (which would set G[b] = +Infinity and collapse the entire phalanx onto its neighbor on low-poly hands!).
            for (int b = 0; b < nb; b++)
            {
                if (seedsPerBone[b].Count > 0) continue;
                var sp = specs[b];
                // First try any vertex already labeled for this bone
                for (int i = 0; i < n; i++)
                    if (label[i] == b) seedsPerBone[b].Add(i);
                if (seedsPerBone[b].Count > 0) continue;
                // Fallback: seed with the single closest vertex to this bone segment
                float bestDist = float.MaxValue;
                int bestIdx = -1;
                for (int i = 0; i < n; i++)
                {
                    float sc = CapsuleScore(weldPos[i], sp.SegStart, sp.SegEnd, sp.Radius, out _);
                    if (sc < bestDist) { bestDist = sc; bestIdx = i; }
                }
                if (bestIdx >= 0) seedsPerBone[b].Add(bestIdx);
            }

            var G = new float[nb][];
            System.Threading.Tasks.Parallel.For(0, nb, b =>
            {
                var dist = new float[n];
                Array.Fill(dist, float.PositiveInfinity);
                if (seedsPerBone[b].Count == 0) { G[b] = dist; return; }
                var pq = new PriorityQueue<int, float>(seedsPerBone[b].Count);
                foreach (int sIdx in seedsPerBone[b]) { dist[sIdx] = 0f; pq.Enqueue(sIdx, 0f); }
                while (pq.TryDequeue(out int u, out float du))
                {
                    if (du > dist[u]) continue;
                    var neighbors = adj[u];
                    for (int ei = 0; ei < neighbors.Count; ei++)
                    {
                        var (v, w) = neighbors[ei];
                        float nd = du + w;
                        if (nd < dist[v]) { dist[v] = nd; pq.Enqueue(v, nd); }
                    }
                }
                G[b] = dist;
            });

            float eps = 0.02f * H;
            var dense = new float[n * nb];
            for (int i = 0; i < n; i++)
            {
                int primary = label[i];
                bool primaryIsMeta = specs[primary].Name.Contains("_meta_", StringComparison.OrdinalIgnoreCase);
                float localEps = (specs[primary].Role == ReferenceBoneRole.Finger && !primaryIsMeta) ? 0.004f * H : eps;
                float sum = 0f;
                int rowOff = i * nb;

                // Detect disconnected overlapping shells (e.g. shorts/underwear mesh over thigh skin):
                // if ANY allowed Core neighbor has no geodesic path onto this shell, evaluate ALL allowed Core bones
                // using a consistent Euclidean segment distance rather than mixing shorter Euclidean distances with
                // longer surface geodesic paths — ensuring overlapping shells receive identical pelvis<->thigh weights!
                bool useConsistentCoreEuclidean = false;
                if (specs[primary].Role == ReferenceBoneRole.Core)
                {
                    for (int b = 0; b < nb; b++)
                    {
                        if (specs[b].Role != ReferenceBoneRole.Core) continue;
                        if (!IsBoneAllowedForSpec(specs, hopDist, primary, b)) continue;
                        if (float.IsPositiveInfinity(G[b][i]))
                        {
                            useConsistentCoreEuclidean = true;
                            break;
                        }
                    }
                }

                for (int b = 0; b < nb; b++)
                {
                    if (!IsBoneAllowedForSpec(specs, hopDist, primary, b)) continue;
                    float gd;
                    if (useConsistentCoreEuclidean && specs[b].Role == ReferenceBoneRole.Core)
                    {
                        Vector3 ab = specs[b].SegEnd - specs[b].SegStart;
                        float len2 = ab.LengthSquared();
                        float ct = len2 < 1e-10f ? 0f : Mathf.Clamp((weldPos[i] - specs[b].SegStart).Dot(ab) / len2, 0f, 1f);
                        gd = (weldPos[i] - (specs[b].SegStart + ab * ct)).Length();
                    }
                    else
                    {
                        gd = G[b][i];
                        if (float.IsPositiveInfinity(gd)) continue;
                    }
                    float w = 1f / ((gd + localEps) * (gd + localEps));
                    dense[rowOff + b] = w; sum += w;
                }
                if (sum <= 0f)
                {
                    for (int b = 0; b < nb; b++)
                    {
                        if (!IsBoneAllowedForSpec(specs, hopDist, primary, b)) continue;
                        float sc = CapsuleScore(weldPos[i], specs[b].SegStart, specs[b].SegEnd, specs[b].Radius, out _);
                        float w = 1f / ((sc + 0.1f) * (sc + 0.1f) * (sc + 0.1f) * (sc + 0.1f));
                        dense[rowOff + b] = w; sum += w;
                    }
                }
                if (sum <= 0f) { dense[rowOff + primary] = 1f; sum = 1f; }
                for (int b = 0; b < nb; b++) dense[rowOff + b] /= sum;
            }

            var next = new float[dense.Length];
            for (int iter = 0; iter < 5; iter++)
            {
                bool transitionOnly = iter >= 1;
                float selfKeep = transitionOnly ? 0.65f : 0.75f;
                float neighborMix = 1f - selfKeep;
                for (int i = 0; i < n; i++)
                {
                    int rowOff = i * nb;
                    int primary = label[i];
                    // Keep distal finger phalanges crisp (stop after 2 passes so adjacent fingers never web),
                    // while letting palm metacarpals (_meta_) smooth smoothly across all 5 passes!
                    bool isDistalFinger = specs[primary].Role == ReferenceBoneRole.Finger &&
                                          !specs[primary].Name.Contains("_meta_", StringComparison.OrdinalIgnoreCase);
                    if (iter >= 2 && isDistalFinger)
                    {
                        Array.Copy(dense, rowOff, next, rowOff, nb);
                        continue;
                    }
                    var neighbors = adj[i];
                    if (neighbors.Count == 0)
                    {
                        Array.Copy(dense, rowOff, next, rowOff, nb);
                        continue;
                    }
                    if (transitionOnly)
                    {
                        float maxW = 0f;
                        for (int b = 0; b < nb; b++)
                            if (dense[rowOff + b] > maxW) maxW = dense[rowOff + b];
                        if (maxW >= 0.85f)
                        {
                            Array.Copy(dense, rowOff, next, rowOff, nb);
                            continue;
                        }
                    }
                    float rowSum = 0f;
                    float invDeg = 1f / neighbors.Count;
                    for (int b = 0; b < nb; b++)
                    {
                        if (!IsBoneAllowedForSpec(specs, hopDist, primary, b)) { next[rowOff + b] = 0f; continue; }
                        float avg = 0f;
                        for (int ni = 0; ni < neighbors.Count; ni++)
                            avg += dense[neighbors[ni].Item1 * nb + b];
                        avg *= invDeg;
                        float val = dense[rowOff + b] * selfKeep + avg * neighborMix;
                        next[rowOff + b] = val;
                        rowSum += val;
                    }
                    if (rowSum > 1e-6f)
                    {
                        float invSum = 1f / rowSum;
                        for (int b = 0; b < nb; b++) next[rowOff + b] *= invSum;
                    }
                    else
                    {
                        next[rowOff + primary] = 1f;
                    }
                }
                (dense, next) = (next, dense);
            }

            // Chin Plane Gate: prevent neck_0 from deforming or pulling any vertices on the chin, jaw, or head!
            // Everything at/above chinPlaneY transfers 100% of neck_0 weight cleanly to head; a 1.2cm band strictly
            // below chinPlaneY transitions smoothly into the throat so neck_0 bends naturally underneath the jaw.
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
                        float t = (vy - (cy - band)) / band; // 0 at bottom of band -> 1 at chinY
                        float shift = wNeck * t;
                        dense[rowOff + idxHead] += shift;
                        dense[rowOff + idxNeck] = wNeck - shift;
                    }
                }
            }

            outBones = new List<int[]>();
            outWeights = new List<float[]>();
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
                    if (tot <= 1e-6f) { bones[v * 4] = 0; weights[v * 4] = 1f; continue; }
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
        }

        /// <summary>
        /// Builds a clean, Godot 4.7-valid surface array keeping only standard mesh attributes and
        /// stripping incompatible <c>Custom0..3</c> slots that trigger
        /// <c>Condition "p_arrays[ai].get_type() != Variant::PACKED_BYTE_ARRAY" is true</c>.
        /// </summary>
        private static Godot.Collections.Array BuildSanitizedSurfaceArrays(
            Godot.Collections.Array srcArrays,
            int[] bonesOverride = null,
            float[] weightsOverride = null,
            Vector3[] vertsOverride = null,
            Vector3[] normsOverride = null)
        {
            var clean = new Godot.Collections.Array();
            clean.Resize((int)Mesh.ArrayType.Max);

            void CopySlot(Mesh.ArrayType slot)
            {
                int idx = (int)slot;
                if (srcArrays != null && idx < srcArrays.Count && srcArrays[idx].VariantType != Variant.Type.Nil)
                    clean[idx] = srcArrays[idx];
            }

            if (vertsOverride != null)
                clean[(int)Mesh.ArrayType.Vertex] = vertsOverride;
            else
                CopySlot(Mesh.ArrayType.Vertex);

            if (normsOverride != null)
                clean[(int)Mesh.ArrayType.Normal] = normsOverride;
            else
                CopySlot(Mesh.ArrayType.Normal);

            if (vertsOverride == null)
                CopySlot(Mesh.ArrayType.Tangent);

            CopySlot(Mesh.ArrayType.Color);
            CopySlot(Mesh.ArrayType.TexUV);
            CopySlot(Mesh.ArrayType.TexUV2);
            CopySlot(Mesh.ArrayType.Index);

            if (bonesOverride != null && weightsOverride != null)
            {
                clean[(int)Mesh.ArrayType.Bones] = bonesOverride;
                clean[(int)Mesh.ArrayType.Weights] = weightsOverride;
            }
            else
            {
                CopySlot(Mesh.ArrayType.Bones);
                CopySlot(Mesh.ArrayType.Weights);
            }

            return clean;
        }

        // ------------------------------------------------------------------
        // Synthetic rig construction (temporarily mutates the imported scene)
        // ------------------------------------------------------------------

        public sealed class SyntheticRig
        {
            public Skeleton3D Skeleton;
            public Action Restore;
            public int VertexCount;
            public Dictionary<string, Vector3> PinsUsedAligned;
            public List<BoneChainDef> AuthoredClothChainDefs = new();
        }

        public static SyntheticRig BuildSyntheticRig(
            Node3D scene,
            List<MeshInstance3D> meshes,
            Skeleton3D heroSkeleton,
            CustomModelAutoRigger.RiggingOptions options)
        {
            var surfaces = GatherSurfaces(scene, meshes);
            if (surfaces.Count == 0) throw new InvalidOperationException("Landmark rig: no mesh surfaces to bind.");

            ReferenceRigDef refRig = options.ReferenceRig?.Clone();
            if (refRig == null)
            {
                var pinsModel = options.LandmarkPins != null && PinNames.All(options.LandmarkPins.ContainsKey)
                    ? new Dictionary<string, Vector3>(options.LandmarkPins)
                    : EstimateLandmarks(scene, meshes);
                if (!PinNames.All(pinsModel.ContainsKey)) throw new InvalidOperationException("Landmark rig: could not estimate landmark pins.");
                refRig = new ReferenceRigDef { Joints = new Dictionary<string, Vector3>(pinsModel, StringComparer.OrdinalIgnoreCase) };
            }
            else if (!PinNames.All(refRig.Joints.ContainsKey))
            {
                var est = EstimateLandmarks(scene, meshes);
                foreach (var kv in est)
                    if (!refRig.Joints.ContainsKey(kv.Key)) refRig.Joints[kv.Key] = kv.Value;
            }

            Transform3D A = options.AlignedTransform;
            Transform3D Ainv = A.AffineInverse();

            // Body metrics in aligned space.
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var sd in surfaces)
                foreach (var v in sd.SceneVerts)
                {
                    float y = (A * v).Y;
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
            float H = Math.Max(0.1f, maxY - minY);
            refRig.EnsureDefaultJoints(H);

            // Auto-correct left/right labelling against the hero skeleton.
            bool swapLR = false;
            int heroArmL = heroSkeleton.FindBone("arm_upper_L");
            if (heroArmL >= 0 && refRig.Joints.TryGetValue("ShoulderL", out var shLm) && refRig.Joints.TryGetValue("ShoulderR", out var shRm))
            {
                float heroX = heroSkeleton.GetBoneGlobalRest(heroArmL).Origin.X;
                float myX = (A * shLm).X - (A * shRm).X;
                if (heroX * myX < 0f) swapLR = true;
            }

            // Foot forward direction from the lowest vertices relative to the ankles.
            float ankleZ = ((A * refRig.Joints["AnkleL"]).Z + (A * refRig.Joints["AnkleR"]).Z) * 0.5f;
            double sumZ = 0; int cntZ = 0;
            foreach (var sd in surfaces)
                foreach (var v in sd.SceneVerts)
                {
                    var w = A * v;
                    if (w.Y < minY + 0.06f * H) { sumZ += w.Z; cntZ++; }
                }
            float footFwd = cntZ > 0 && (sumZ / cntZ) < ankleZ ? -1f : 1f;

            var specs = refRig.BuildResolvedBones(A, H, footFwd, swapLeftRight: swapLR);
            var pinsAligned = refRig.Joints.ToDictionary(kv => kv.Key, kv => A * kv.Value, StringComparer.OrdinalIgnoreCase);

            var alignedVerts = new List<Vector3[]>();
            var indices = new List<int[]>();
            var surfaceMeshNames = new List<string>(surfaces.Count);
            foreach (var sd in surfaces)
            {
                var av = new Vector3[sd.SceneVerts.Length];
                for (int i = 0; i < av.Length; i++) av[i] = A * sd.SceneVerts[i];
                alignedVerts.Add(av);
                indices.Add(sd.Indices);
                surfaceMeshNames.Add(sd.Mi?.Name.ToString() ?? "");
            }
            float? chinPlaneY = pinsAligned.TryGetValue("Chin", out var chinAligned) ? chinAligned.Y : (float?)null;
            var swHeat = System.Diagnostics.Stopwatch.StartNew();
            List<int[]> bonesPer;
            List<float[]> weightsPer;
            if (VoxelHeatWeightSolver.SolveWeights(alignedVerts, indices, specs, H, chinPlaneY, out bonesPer, out weightsPer, surfaceMeshNames))
            {
                swHeat.Stop();
                GD.Print($"[VoxelHeatWeightSolver] Solved volumetric heat weights for {specs.Count} bones in {swHeat.ElapsedMilliseconds} ms.");
            }
            else
            {
                SolveWeightsModular(alignedVerts, indices, specs, H, chinPlaneY, out bonesPer, out weightsPer);
            }

            // Build modular skeleton in scene space (identity transform under the scene root).
            var skel = new Skeleton3D { Name = "LandmarkSyntheticSkeleton" };
            scene.AddChild(skel);
            var globalRest = new List<Transform3D>(specs.Count + 12);
            for (int b = 0; b < specs.Count; b++)
            {
                skel.AddBone(specs[b].Name);
                globalRest.Add(new Transform3D(Basis.Identity, Ainv * specs[b].Origin));
            }
            for (int b = 0; b < specs.Count; b++)
            {
                int parent = specs[b].ParentIndex;
                if (parent >= 0) skel.SetBoneParent(b, parent);
                Transform3D local = parent >= 0 ? globalRest[parent].AffineInverse() * globalRest[b] : globalRest[b];
                skel.SetBoneRest(b, local);
            }
            // Append zero-weight fingertip leaf bones (finger_{name}_end_{L|R}) so BuildFingerSegmentRegistration
            // computes exact distal phalanx orientation & snaps fingers cleanly into Deadlock's grip pose!
            var hasChildBone = new bool[specs.Count];
            for (int b = 0; b < specs.Count; b++)
                if (specs[b].ParentIndex >= 0 && specs[b].ParentIndex < specs.Count)
                    hasChildBone[specs[b].ParentIndex] = true;
            for (int b = 0; b < specs.Count; b++)
            {
                if (specs[b].Role != ReferenceBoneRole.Finger || hasChildBone[b]) continue;
                // e.g. "finger_index_2_L" -> "finger_index_end_L"
                var parts = specs[b].Name.Split('_');
                if (parts.Length >= 4)
                {
                    string leafName = $"{parts[0]}_{parts[1]}_end_{parts[^1]}";
                    int leafIdx = skel.GetBoneCount();
                    skel.AddBone(leafName);
                    skel.SetBoneParent(leafIdx, b);
                    var leafGlobal = new Transform3D(Basis.Identity, Ainv * specs[b].SegEnd);
                    globalRest.Add(leafGlobal);
                    skel.SetBoneRest(leafIdx, globalRest[b].AffineInverse() * leafGlobal);
                }
            }
            skel.ResetBonePoses();

            // Build BoneChainDefs for authored cloth chains so Tab 3 Physics & CSDK13 export pick them up seamlessly.
            var authoredChains = new List<BoneChainDef>();
            foreach (var grp in specs.Where(sp => sp.Role == ReferenceBoneRole.Cloth && !string.IsNullOrEmpty(sp.ClothChainId)).GroupBy(sp => sp.ClothChainId))
            {
                var ordered = grp.OrderBy(sp => sp.Index).ToList();
                if (ordered.Count == 0) continue;
                var srcChain = refRig.ClothChains.FirstOrDefault(c => string.Equals(c.ChainId, grp.Key, StringComparison.OrdinalIgnoreCase));
                var chainDef = new BoneChainDef
                {
                    ChainId = grp.Key,
                    Name = srcChain?.Name ?? ordered[0].Name,
                    Category = "Clothing & Hair",
                    RootSourceBone = ordered[0].Name,
                    SourceBoneNames = ordered.Select(sp => sp.Name).ToList(),
                    AnchorHeroBone = srcChain?.ParentBoneName ?? "spine_3",
                    Policy = ChainPolicy.PreservePhysicsChain
                };
                string anchorLower = (chainDef.AnchorHeroBone ?? "").ToLowerInvariant();
                string recPreset = ordered.Count == 1
                    ? PhysicsPresetCatalog.RigidJiggleId
                    : (anchorLower.Contains("head") || anchorLower.Contains("neck")
                        ? PhysicsPresetCatalog.LongFlowingHairId
                        : (anchorLower.Contains("pelvis") || anchorLower.Contains("leg")
                            ? PhysicsPresetCatalog.HeavySkirtDressId
                            : PhysicsPresetCatalog.CapeCloakId));
                PhysicsPresetCatalog.ApplyPresetToChain(chainDef, recPreset, resetMacros: true);
                authoredChains.Add(chainDef);
            }

            // Swap each mesh's surfaces for sanitized skinned copies baked in scene space
            // (completely decoupling previously rigged models from their old Skeleton3D / Skin inverse-bind transforms).
            var originals = new List<(MeshInstance3D mi, Mesh mesh, Skin skin, NodePath skeletonPath)>();
            var byMesh = surfaces.Select((sd, i) => (sd, i)).GroupBy(x => x.sd.Mi);
            int totalVerts = 0;
            foreach (var grp in byMesh)
            {
                var mi = grp.Key;
                var srcMesh = mi.Mesh;
                originals.Add((mi, srcMesh, mi.Skin, mi.Skeleton));
                var newMesh = new ArrayMesh();
                var surfaceMap = grp.ToDictionary(x => x.sd.Surface, x => x.i);
                int addedSurfIdx = 0;
                for (int s = 0; s < srcMesh.GetSurfaceCount(); s++)
                {
                    Godot.Collections.Array cleanArrays;
                    if (surfaceMap.TryGetValue(s, out int gi))
                    {
                        cleanArrays = BuildSanitizedSurfaceArrays(
                            surfaces[gi].Arrays,
                            bonesPer[gi],
                            weightsPer[gi],
                            surfaces[gi].SceneVerts,
                            surfaces[gi].SceneNorms);
                        totalVerts += surfaces[gi].SceneVerts.Length;
                    }
                    else
                    {
                        cleanArrays = BuildSanitizedSurfaceArrays(srcMesh.SurfaceGetArrays(s));
                    }
                    var prim = (srcMesh as ArrayMesh)?.SurfaceGetPrimitiveType(s) ?? Mesh.PrimitiveType.Triangles;
                    newMesh.AddSurfaceFromArrays(prim, cleanArrays);
                    if (addedSurfIdx < newMesh.GetSurfaceCount())
                    {
                        newMesh.SurfaceSetMaterial(addedSurfIdx, srcMesh.SurfaceGetMaterial(s));
                    }
                    addedSurfIdx++;
                }

                // Vertices in newMesh are already baked into scene space (= skel's local space),
                // so each bind pose is simply globalRest[b].AffineInverse() with no old-mesh transform dependency.
                var skin = new Skin();
                for (int b = 0; b < specs.Count; b++)
                    skin.AddBind(b, globalRest[b].AffineInverse());
                mi.Mesh = newMesh;
                mi.Skin = skin;
                mi.Skeleton = (mi.IsInsideTree() && skel.IsInsideTree()) ? mi.GetPathTo(skel) : new NodePath();
            }

            return new SyntheticRig
            {
                Skeleton = skel,
                VertexCount = totalVerts,
                PinsUsedAligned = pinsAligned,
                AuthoredClothChainDefs = authoredChains,
                Restore = () =>
                {
                    foreach (var (mi, mesh, skin, skelPath) in originals)
                    {
                        if (!GodotObject.IsInstanceValid(mi)) continue;
                        mi.Mesh = mesh;
                        mi.Skin = skin;
                        mi.Skeleton = skelPath;
                    }
                    if (GodotObject.IsInstanceValid(skel))
                    {
                        skel.GetParent()?.RemoveChild(skel);
                        skel.QueueFree();
                    }
                }
            };
        }
    }
}
