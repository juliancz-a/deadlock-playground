using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using DeadlockPlayground.Importer;

namespace DeadlockPlayground.Tests
{
    /// <summary>
    /// Headless verification suite for <see cref="VoxelHeatWeightSolver"/>:
    /// 1. Multi-shell lockstep (pelvis + thigh + disconnected shorts shell + detached floating thigh strap).
    /// 2. High-res 2.5mm hand voxel heat (5 separated fingers + solid palm + disconnected fingerless glove shell).
    /// 3. Modular SpineCount 1..4 mapping (verifies spine_0 is omitted for SpineCount 1..3 and present for 4).
    /// 4. Performance benchmark (&lt; 500 ms).
    /// </summary>
    public partial class VoxelHeatVerificationTest : SceneTree
    {
        private int _passed;
        private int _failed;

        private void Check(bool cond, string label)
        {
            if (cond)
            {
                _passed++;
                GD.Print($"  [PASS] {label}");
            }
            else
            {
                _failed++;
                GD.PrintErr($"  [FAIL] {label}");
            }
        }

        public override void _Initialize()
        {
            GD.Print("=== VoxelHeatWeightSolver Verification Suite ===");
            try
            {
                TestMultiShellHipAndThighLockstep();
                TestHighResHandAndFingerIsolation();
                TestModularSpineCountAndExactMapping();
                TestClothChainIsolationFromFaceAndHoodie();
                DiagnoseKirikoModelIfPresent();
            }
            catch (Exception ex)
            {
                _failed++;
                GD.PrintErr($"[EXCEPTION] {ex}");
            }

            GD.Print($"SUMMARY: {_passed} passed, {_failed} failed.");
            Quit(_failed == 0 ? 0 : 1);
        }

        private static (Vector3[] Verts, int[] Indices) BuildCylinder(Vector3 a, Vector3 b, float radius, int radialSegs, int heightSegs, bool capEnds)
        {
            Vector3 ab = b - a;
            Vector3 axis = ab.Normalized();
            Vector3 refV = MathF.Abs(axis.Y) < 0.9f ? Vector3.Up : Vector3.Right;
            Vector3 u = axis.Cross(refV).Normalized();
            Vector3 v = axis.Cross(u).Normalized();

            var verts = new List<Vector3>();
            var indices = new List<int>();

            for (int hi = 0; hi <= heightSegs; hi++)
            {
                float t = hi / (float)heightSegs;
                Vector3 c = a.Lerp(b, t);
                for (int ri = 0; ri < radialSegs; ri++)
                {
                    float ang = (ri / (float)radialSegs) * Mathf.Tau;
                    verts.Add(c + (u * MathF.Cos(ang) + v * MathF.Sin(ang)) * radius);
                }
            }

            for (int hi = 0; hi < heightSegs; hi++)
            {
                int row0 = hi * radialSegs;
                int row1 = (hi + 1) * radialSegs;
                for (int ri = 0; ri < radialSegs; ri++)
                {
                    int rn = (ri + 1) % radialSegs;
                    indices.Add(row0 + ri); indices.Add(row1 + ri); indices.Add(row1 + rn);
                    indices.Add(row0 + ri); indices.Add(row1 + rn); indices.Add(row0 + rn);
                }
            }

            if (capEnds)
            {
                int c0 = verts.Count; verts.Add(a);
                int c1 = verts.Count; verts.Add(b);
                int topRow = heightSegs * radialSegs;
                for (int ri = 0; ri < radialSegs; ri++)
                {
                    int rn = (ri + 1) % radialSegs;
                    indices.Add(c0); indices.Add(rn); indices.Add(ri);
                    indices.Add(c1); indices.Add(topRow + ri); indices.Add(topRow + rn);
                }
            }

            return (verts.ToArray(), indices.ToArray());
        }

        private static (Vector3[] Verts, int[] Indices) BuildBox(Vector3 center, Vector3 halfExtents)
        {
            Vector3 h = halfExtents;
            var verts = new[]
            {
                center + new Vector3(-h.X, -h.Y, -h.Z), center + new Vector3( h.X, -h.Y, -h.Z),
                center + new Vector3( h.X,  h.Y, -h.Z), center + new Vector3(-h.X,  h.Y, -h.Z),
                center + new Vector3(-h.X, -h.Y,  h.Z), center + new Vector3( h.X, -h.Y,  h.Z),
                center + new Vector3( h.X,  h.Y,  h.Z), center + new Vector3(-h.X,  h.Y,  h.Z)
            };
            var idx = new[]
            {
                0,2,1, 0,3,2,  4,5,6, 4,6,7,
                0,1,5, 0,5,4,  2,3,7, 2,7,6,
                0,4,7, 0,7,3,  1,2,6, 1,6,5
            };
            return (verts, idx);
        }

        private static float GetBoneWeight(int[] bones, float[] weights, int v, int targetBone)
        {
            float sum = 0f;
            for (int k = 0; k < 4; k++)
                if (bones[v * 4 + k] == targetBone) sum += weights[v * 4 + k];
            return sum;
        }

        private void TestMultiShellHipAndThighLockstep()
        {
            GD.Print("--- Test 1: Multi-Shell Pelvis + Thigh + Shorts + Detached Holster ---");
            // Build pelvis + left thigh + right thigh + overlapping open shorts shell + detached floating thigh strap
            var torso = BuildCylinder(new Vector3(0f, 0.85f, 0f), new Vector3(0f, 1.50f, 0f), 0.16f, 20, 12, true);
            var thighL = BuildCylinder(new Vector3(0.11f, 0.92f, 0f), new Vector3(0.11f, 0.45f, 0f), 0.075f, 16, 12, true);
            var thighR = BuildCylinder(new Vector3(-0.11f, 0.92f, 0f), new Vector3(-0.11f, 0.45f, 0f), 0.075f, 16, 12, true);
            // Open shorts cuff wrapping right thigh at y=0.72..0.95 (6mm outside bare thigh, disconnected mesh!)
            var shortsR = BuildCylinder(new Vector3(-0.11f, 0.95f, 0f), new Vector3(-0.11f, 0.72f, 0f), 0.081f, 16, 6, false);
            // Detached floating kunai holster sitting 2.0cm outside left thigh at y=0.65..0.78
            var holsterL = BuildBox(new Vector3(0.205f, 0.72f, 0f), new Vector3(0.015f, 0.06f, 0.025f));

            var specs = new List<ReferenceRigDef.ResolvedBoneSpec>
            {
                new() { Index = 0, Name = "pelvis", ParentIndex = -1, Role = ReferenceBoneRole.Core, Origin = new(0, 0.95f, 0), SegStart = new(0, 0.88f, 0), SegEnd = new(0, 1.12f, 0), Radius = 0.14f, AllowBelow = true },
                new() { Index = 1, Name = "spine_1", ParentIndex = 0, Role = ReferenceBoneRole.Core, Origin = new(0, 1.12f, 0), SegStart = new(0, 1.12f, 0), SegEnd = new(0, 1.50f, 0), Radius = 0.14f, AllowBeyond = true },
                new() { Index = 2, Name = "leg_upper_L", ParentIndex = 0, Role = ReferenceBoneRole.Core, Origin = new(0.11f, 0.90f, 0), SegStart = new(0.11f, 0.90f, 0), SegEnd = new(0.11f, 0.45f, 0), Radius = 0.08f, AllowBeyond = true },
                new() { Index = 3, Name = "leg_upper_R", ParentIndex = 0, Role = ReferenceBoneRole.Core, Origin = new(-0.11f, 0.90f, 0), SegStart = new(-0.11f, 0.90f, 0), SegEnd = new(-0.11f, 0.45f, 0), Radius = 0.08f, AllowBeyond = true }
            };

            var verts = new List<Vector3[]> { torso.Verts, thighL.Verts, thighR.Verts, shortsR.Verts, holsterL.Verts };
            var idxs = new List<int[]> { torso.Indices, thighL.Indices, thighR.Indices, shortsR.Indices, holsterL.Indices };

            var sw = Stopwatch.StartNew();
            bool ok = VoxelHeatWeightSolver.SolveWeights(verts, idxs, specs, 1.7f, null, out var outBones, out var outWeights);
            sw.Stop();
            Check(ok, $"VoxelHeatWeightSolver.SolveWeights succeeded ({sw.ElapsedMilliseconds} ms)");
            Check(sw.ElapsedMilliseconds < 500, $"Body solve time {sw.ElapsedMilliseconds} ms < 500 ms");

            // Check weight sum normalization
            int badSum = 0;
            for (int s = 0; s < outWeights.Count; s++)
            {
                for (int v = 0; v < verts[s].Length; v++)
                {
                    float sum = outWeights[s][v * 4] + outWeights[s][v * 4 + 1] + outWeights[s][v * 4 + 2] + outWeights[s][v * 4 + 3];
                    if (MathF.Abs(sum - 1f) > 0.01f) badSum++;
                }
            }
            Check(badSum == 0, $"All multi-shell vertices sum to 1.0 (bad={badSum})");

            // Compare right shorts vs right bare thigh at the exact same Y (~0.80 +/- 0.015)
            float shortsLegR = 0f, thighLegR = 0f;
            int sc = 0, tc = 0;
            for (int v = 0; v < shortsR.Verts.Length; v++)
                if (MathF.Abs(shortsR.Verts[v].Y - 0.80f) < 0.015f) { shortsLegR += GetBoneWeight(outBones[3], outWeights[3], v, 3); sc++; }
            for (int v = 0; v < thighR.Verts.Length; v++)
                if (MathF.Abs(thighR.Verts[v].Y - 0.80f) < 0.015f) { thighLegR += GetBoneWeight(outBones[2], outWeights[2], v, 3); tc++; }
            shortsLegR /= Math.Max(1, sc);
            thighLegR /= Math.Max(1, tc);
            float shellDiff = MathF.Abs(shortsLegR - thighLegR);
            Check(shellDiff < 0.06f, $"Shorts & bare thigh lockstep at y=0.80 (shorts={shortsLegR:F3}, thigh={thighLegR:F3}, diff={shellDiff:F3} < 0.06)");

            // Check detached floating holster on left thigh inherits leg_upper_L cleanly with zero opposite-leg bleed
            float holsterLWeight = 0f, holsterRWeight = 0f;
            for (int v = 0; v < holsterL.Verts.Length; v++)
            {
                holsterLWeight += GetBoneWeight(outBones[4], outWeights[4], v, 2);
                holsterRWeight += GetBoneWeight(outBones[4], outWeights[4], v, 3);
            }
            holsterLWeight /= holsterL.Verts.Length;
            holsterRWeight /= holsterL.Verts.Length;
            Check(holsterLWeight > 0.80f && holsterRWeight < 0.01f, $"Detached left holster follows leg_upper_L ({holsterLWeight:F3}) with zero right-leg bleed ({holsterRWeight:F4})");

            // Check zero cross-leg bleed on mid-thighs
            float crossBleedMax = 0f;
            for (int v = 0; v < thighL.Verts.Length; v++)
                if (thighL.Verts[v].Y < 0.75f) crossBleedMax = MathF.Max(crossBleedMax, GetBoneWeight(outBones[1], outWeights[1], v, 3));
            Check(crossBleedMax < 0.02f, $"Zero cross-leg bleed on left thigh (max={crossBleedMax:F4} < 0.02)");
        }

        private void TestHighResHandAndFingerIsolation()
        {
            GD.Print("--- Test 2: High-Res Hand + 5 Separated Fingers + Fingerless Glove ---");
            // Forearm + palm box + 5 well-separated finger cylinders + fingerless glove shell around palm
            var forearm = BuildCylinder(new Vector3(0.30f, 1.0f, 0f), new Vector3(0.55f, 1.0f, 0f), 0.032f, 12, 6, true);
            var palm = BuildBox(new Vector3(0.595f, 1.0f, 0f), new Vector3(0.045f, 0.012f, 0.042f));
            var glove = BuildBox(new Vector3(0.595f, 1.0f, 0f), new Vector3(0.042f, 0.015f, 0.045f));

            // 4 main fingers along +X, spaced 2.0cm apart in Z (radius 0.7cm -> 0.6cm air gap between fingers)
            var idxCyl = BuildCylinder(new Vector3(0.64f, 1.0f,  0.030f), new Vector3(0.73f, 1.0f,  0.030f), 0.007f, 10, 9, true);
            var midCyl = BuildCylinder(new Vector3(0.64f, 1.0f,  0.010f), new Vector3(0.74f, 1.0f,  0.010f), 0.007f, 10, 9, true);
            var rngCyl = BuildCylinder(new Vector3(0.64f, 1.0f, -0.010f), new Vector3(0.73f, 1.0f, -0.010f), 0.007f, 10, 9, true);
            var pnkCyl = BuildCylinder(new Vector3(0.64f, 1.0f, -0.030f), new Vector3(0.71f, 1.0f, -0.030f), 0.006f, 10, 9, true);
            var thmCyl = BuildCylinder(new Vector3(0.57f, 1.0f,  0.042f), new Vector3(0.63f, 1.0f,  0.088f), 0.008f, 10, 6, true);

            var specs = new List<ReferenceRigDef.ResolvedBoneSpec>
            {
                new() { Index = 0, Name = "arm_lower_L", ParentIndex = -1, Role = ReferenceBoneRole.Core, Origin = new(0.30f, 1.0f, 0f), SegStart = new(0.30f, 1.0f, 0f), SegEnd = new(0.55f, 1.0f, 0f), Radius = 0.03f, AllowBelow = true },
                new() { Index = 1, Name = "hand_L", ParentIndex = 0, Role = ReferenceBoneRole.Core, Origin = new(0.55f, 1.0f, 0f), SegStart = new(0.55f, 1.0f, 0f), SegEnd = new(0.60f, 1.0f, 0f), Radius = 0.025f, AllowBelow = true }
            };

            void AddFinger(string fn, Vector3 k0, Vector3 tip, bool hasMeta)
            {
                int parent = 1;
                if (hasMeta)
                {
                    int mIdx = specs.Count;
                    Vector3 mOrig = new Vector3(0.55f, 1.0f, 0f).Lerp(k0, 0.35f);
                    specs.Add(new ReferenceRigDef.ResolvedBoneSpec
                    {
                        Index = mIdx, Name = $"finger_{fn}_meta_L", ParentIndex = 1, Role = ReferenceBoneRole.Finger,
                        Origin = mOrig, SegStart = mOrig, SegEnd = k0, Radius = 0.014f
                    });
                    parent = mIdx;
                }
                float[] fracs = { 0f, 0.46f, 0.76f, 1f };
                for (int j = 0; j < 3; j++)
                {
                    int bIdx = specs.Count;
                    Vector3 a = k0.Lerp(tip, fracs[j]);
                    Vector3 b = k0.Lerp(tip, fracs[j + 1]);
                    specs.Add(new ReferenceRigDef.ResolvedBoneSpec
                    {
                        Index = bIdx, Name = $"finger_{fn}_{j}_L", ParentIndex = parent, Role = ReferenceBoneRole.Finger,
                        Origin = a, SegStart = a, SegEnd = b, Radius = 0.008f, AllowBelow = j == 0, AllowBeyond = j == 2
                    });
                    parent = bIdx;
                }
            }

            AddFinger("thumb",  new(0.57f, 1.0f,  0.042f), new(0.63f, 1.0f,  0.088f), hasMeta: false);
            AddFinger("index",  new(0.64f, 1.0f,  0.030f), new(0.73f, 1.0f,  0.030f), hasMeta: true);
            AddFinger("middle", new(0.64f, 1.0f,  0.010f), new(0.74f, 1.0f,  0.010f), hasMeta: true);
            AddFinger("ring",   new(0.64f, 1.0f, -0.010f), new(0.73f, 1.0f, -0.010f), hasMeta: true);
            AddFinger("pinky",  new(0.64f, 1.0f, -0.030f), new(0.71f, 1.0f, -0.030f), hasMeta: true);

            var verts = new List<Vector3[]> { forearm.Verts, palm.Verts, glove.Verts, idxCyl.Verts, midCyl.Verts, rngCyl.Verts, pnkCyl.Verts, thmCyl.Verts };
            var idxs = new List<int[]> { forearm.Indices, palm.Indices, glove.Indices, idxCyl.Indices, midCyl.Indices, rngCyl.Indices, pnkCyl.Indices, thmCyl.Indices };

            var sw = Stopwatch.StartNew();
            bool ok = VoxelHeatWeightSolver.SolveWeights(verts, idxs, specs, 1.7f, null, out var outBones, out var outWeights);
            sw.Stop();
            Check(ok, $"Hand + 5-finger VoxelHeatWeightSolver succeeded ({sw.ElapsedMilliseconds} ms)");

            int FindSpec(string n) => specs.FindIndex(s => s.Name == n);
            int idx0 = FindSpec("finger_index_0_L"), idx1 = FindSpec("finger_index_1_L"), idx2 = FindSpec("finger_index_2_L");
            int mid0 = FindSpec("finger_middle_0_L"), mid1 = FindSpec("finger_middle_1_L"), mid2 = FindSpec("finger_middle_2_L");

            // Index finger distal vertices (x > 0.66) must have ~0% middle-finger bleed and active weights on phalanges 0, 1, 2
            float maxCrossFinger = 0f;
            float sumIdx0 = 0f, sumIdx1 = 0f, sumIdx2 = 0f;
            for (int v = 0; v < idxCyl.Verts.Length; v++)
            {
                if (idxCyl.Verts[v].X > 0.66f)
                {
                    float wMid = GetBoneWeight(outBones[3], outWeights[3], v, mid0) +
                                 GetBoneWeight(outBones[3], outWeights[3], v, mid1) +
                                 GetBoneWeight(outBones[3], outWeights[3], v, mid2);
                    if (wMid > maxCrossFinger) maxCrossFinger = wMid;
                }
                sumIdx0 += GetBoneWeight(outBones[3], outWeights[3], v, idx0);
                sumIdx1 += GetBoneWeight(outBones[3], outWeights[3], v, idx1);
                sumIdx2 += GetBoneWeight(outBones[3], outWeights[3], v, idx2);
            }
            Check(maxCrossFinger < 0.01f, $"Zero cross-finger bleed index -> middle (max={maxCrossFinger:F4} < 0.01)");
            Check(sumIdx0 > 5f && sumIdx1 > 5f && sumIdx2 > 5f, $"All 3 index phalanges weighted (p0={sumIdx0:F1}, p1={sumIdx1:F1}, p2={sumIdx2:F1})");

            // Fingerless glove must track palm + metacarpals (+ thumb_0 CMC base) cleanly and match inner palm box
            float gloveHandAndMeta = 0f, palmHandAndMeta = 0f;
            int thumb0 = FindSpec("finger_thumb_0_L");
            for (int v = 0; v < glove.Verts.Length; v++)
            {
                float wg = GetBoneWeight(outBones[2], outWeights[2], v, 1) + GetBoneWeight(outBones[2], outWeights[2], v, thumb0);
                float wp = GetBoneWeight(outBones[1], outWeights[1], v, 1) + GetBoneWeight(outBones[1], outWeights[1], v, thumb0);
                for (int b = 0; b < specs.Count; b++)
                {
                    if (specs[b].Name.Contains("_meta_"))
                    {
                        wg += GetBoneWeight(outBones[2], outWeights[2], v, b);
                        wp += GetBoneWeight(outBones[1], outWeights[1], v, b);
                    }
                }
                gloveHandAndMeta += wg;
                palmHandAndMeta += wp;
            }
            gloveHandAndMeta /= glove.Verts.Length;
            palmHandAndMeta /= palm.Verts.Length;
            float glovePalmDiff = MathF.Abs(gloveHandAndMeta - palmHandAndMeta);
            Check(gloveHandAndMeta > 0.70f && glovePalmDiff < 0.08f,
                $"Disconnected fingerless glove locked to palm+metacarpals (glove={gloveHandAndMeta:F3}, palm={palmHandAndMeta:F3}, diff={glovePalmDiff:F3})");
        }

        private void TestModularSpineCountAndExactMapping()
        {
            GD.Print("--- Test 3: Modular SpineCount 1..4 & Exact Synthetic Mapping ---");
            // Build target Deadlock-like skeleton with spine_0..3
            var targetSkel = new Skeleton3D();
            string[] tgtNames = { "pelvis", "spine_0", "spine_1", "spine_2", "spine_3", "neck_0", "head" };
            for (int i = 0; i < tgtNames.Length; i++)
            {
                targetSkel.AddBone(tgtNames[i]);
                if (i > 0) targetSkel.SetBoneParent(i, i - 1);
            }

            foreach (int sc in new[] { 1, 2, 3, 4 })
            {
                var srcSkel = new Skeleton3D { Name = "LandmarkSyntheticSkeleton" };
                srcSkel.AddBone("pelvis");
                string[] spNames = ReferenceRigDef.GetDeadlockSpineBoneNames(sc);
                int prev = 0;
                foreach (var sp in spNames)
                {
                    int idx = srcSkel.GetBoneCount();
                    srcSkel.AddBone(sp);
                    srcSkel.SetBoneParent(idx, prev);
                    prev = idx;
                }
                int nIdx = srcSkel.GetBoneCount(); srcSkel.AddBone("neck_0"); srcSkel.SetBoneParent(nIdx, prev);
                int hIdx = srcSkel.GetBoneCount(); srcSkel.AddBone("head"); srcSkel.SetBoneParent(hIdx, nIdx);

                var map = CustomModelAutoRigger.BuildHumanoidBoneMap(srcSkel, targetSkel);
                bool hasSpine0 = false;
                foreach (var kv in map)
                    if (targetSkel.GetBoneName(kv.Value) == "spine_0") hasSpine0 = true;

                bool expectSpine0 = sc == 4;
                Check(hasSpine0 == expectSpine0, $"SpineCount={sc} -> spine_0 mapped={hasSpine0} (expected {expectSpine0})");
                srcSkel.Free();
            }
            targetSkel.Free();
        }

        private void TestClothChainIsolationFromFaceAndHoodie()
        {
            GD.Print("--- Test 4: Hair Ponytail Cloth Chain Isolation (Zero Face/Hoodie Bleed) ---");
            // Head sphere/cylinder at y=1.55..1.75, hoodie/shoulder cylinder right below at y=1.25..1.52,
            // and a separate ponytail hanging behind the head at z=-0.12..-0.18, y=1.38..1.66 (close to hoodie!).
            var headMesh = BuildCylinder(new Vector3(0f, 1.54f, 0f), new Vector3(0f, 1.76f, 0f), 0.10f, 16, 8, true);
            var hoodieMesh = BuildCylinder(new Vector3(0f, 1.25f, 0f), new Vector3(0f, 1.52f, 0f), 0.16f, 16, 8, true);
            var ponytailMesh = BuildCylinder(new Vector3(0f, 1.66f, -0.11f), new Vector3(0f, 1.40f, -0.15f), 0.045f, 14, 10, true);

            var specs = new List<ReferenceRigDef.ResolvedBoneSpec>
            {
                new() { Index = 0, Name = "spine_3", ParentIndex = -1, Role = ReferenceBoneRole.Core, Origin = new(0, 1.32f, 0), SegStart = new(0, 1.25f, 0), SegEnd = new(0, 1.50f, 0), Radius = 0.14f, AllowBelow = true },
                new() { Index = 1, Name = "head", ParentIndex = 0, Role = ReferenceBoneRole.Core, Origin = new(0, 1.55f, 0), SegStart = new(0, 1.55f, 0), SegEnd = new(0, 1.75f, 0), Radius = 0.09f, AllowBeyond = true },
                new()
                {
                    Index = 2, Name = "hair_tail_0", ParentIndex = 1, Role = ReferenceBoneRole.Cloth,
                    Origin = new(0, 1.66f, -0.11f), SegStart = new(0, 1.66f, -0.11f), SegEnd = new(0, 1.40f, -0.15f),
                    Radius = 0.08f, AllowBelow = true, AllowBeyond = true, ClothChainId = "refcloth_test"
                }
            };

            var verts = new List<Vector3[]> { headMesh.Verts, hoodieMesh.Verts, ponytailMesh.Verts };
            var idxs = new List<int[]> { headMesh.Indices, hoodieMesh.Indices, ponytailMesh.Indices };
            var meshNames = new List<string> { "Head", "Hoodie", "Hair" };

            bool ok = VoxelHeatWeightSolver.SolveWeights(verts, idxs, specs, 1.7f, null, out var outBones, out var outWeights, meshNames);
            Check(ok, "Cloth isolation VoxelHeatWeightSolver succeeded");

            float maxFaceBleed = 0f;
            for (int v = 0; v < headMesh.Verts.Length; v++)
                maxFaceBleed = MathF.Max(maxFaceBleed, GetBoneWeight(outBones[0], outWeights[0], v, 2));

            float maxHoodieBleed = 0f;
            for (int v = 0; v < hoodieMesh.Verts.Length; v++)
                maxHoodieBleed = MathF.Max(maxHoodieBleed, GetBoneWeight(outBones[1], outWeights[1], v, 2));

            float avgPonytailW = 0f;
            int tailCnt = 0;
            for (int v = 0; v < ponytailMesh.Verts.Length; v++)
            {
                if (ponytailMesh.Verts[v].Y < 1.60f)
                {
                    avgPonytailW += GetBoneWeight(outBones[2], outWeights[2], v, 2);
                    tailCnt++;
                }
            }
            avgPonytailW /= Math.Max(1, tailCnt);

            Check(maxFaceBleed < 0.005f, $"Zero hair_tail bleed onto head/face (max={maxFaceBleed:F4} < 0.005)");
            Check(maxHoodieBleed < 0.005f, $"Zero hair_tail bleed onto hoodie/shoulder (max={maxHoodieBleed:F4} < 0.005)");
            Check(avgPonytailW > 0.75f, $"Ponytail body strongly weighted to hair_tail_0 (avg={avgPonytailW:F3} > 0.75)");
        }

        private void DiagnoseKirikoModelIfPresent()
        {
            string path = @"d:\GameDev\deadlock-playground\scratch_out\kirikonorig.glb";
            if (!System.IO.File.Exists(path)) return;
            GD.Print("--- Test 5: Real Model Diagnostic (kirikonorig.glb) ---");
            var gltf = new GltfDocument();
            var state = new GltfState();
            var err = gltf.AppendFromFile(path, state);
            if (err != Error.Ok) { GD.PrintErr($"Failed to load kirikonorig.glb: {err}"); return; }
            var scene = gltf.GenerateScene(state) as Node3D;
            if (scene == null) return;

            var meshes = CustomModelAutoRigger.CollectMeshes(scene);
            GD.Print($"  Loaded kirikonorig.glb: {meshes.Count} mesh(es)");
            var allVerts = new List<Vector3[]>();
            var allIdx = new List<int[]>();
            var meshNames = new List<string>();
            float minY = float.MaxValue, maxY = float.MinValue;

            foreach (var mi in meshes)
            {
                Transform3D xf = CustomModelAutoRigger.ComputeTransformRelativeToRoot(mi, scene);
                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                {
                    var arr = mi.Mesh.SurfaceGetArrays(s);
                    var v = CustomModelAutoRigger.GetVector3Array(arr[(int)Mesh.ArrayType.Vertex]);
                    var idx = arr.Count > (int)Mesh.ArrayType.Index ? CustomModelAutoRigger.GetInt32Array(arr[(int)Mesh.ArrayType.Index]) : null;
                    if (v == null || v.Length == 0) continue;
                    var wv = new Vector3[v.Length];
                    float sMinY = float.MaxValue, sMaxY = float.MinValue;
                    for (int i = 0; i < v.Length; i++)
                    {
                        wv[i] = xf * v[i];
                        if (wv[i].Y < sMinY) sMinY = wv[i].Y;
                        if (wv[i].Y > sMaxY) sMaxY = wv[i].Y;
                        if (wv[i].Y < minY) minY = wv[i].Y;
                        if (wv[i].Y > maxY) maxY = wv[i].Y;
                    }
                    if (idx == null || idx.Length == 0)
                    {
                        idx = new int[v.Length - v.Length % 3];
                        for (int i = 0; i < idx.Length; i++) idx[i] = i;
                    }
                    GD.Print($"    Surf[{allVerts.Count}]: {mi.Name}::s{s} verts={v.Length} Y=[{sMinY:F3}..{sMaxY:F3}]");
                    allVerts.Add(wv);
                    allIdx.Add(idx);
                    meshNames.Add(mi.Name.ToString());
                }
            }

            float H = MathF.Max(0.1f, maxY - minY);
            var pins = CustomLandmarkWeightSolver.EstimateLandmarks(scene, meshes);
            var refRig = new ReferenceRigDef { Joints = new Dictionary<string, Vector3>(pins, StringComparer.OrdinalIgnoreCase) };
            refRig.EnsureDefaultJoints(H);
            refRig.SnapFingersToMesh(scene, meshes, forceRegenerate: true);
            var specs = refRig.BuildResolvedBones(Transform3D.Identity, H, 1f);

            var sw = Stopwatch.StartNew();
            bool ok = VoxelHeatWeightSolver.SolveWeights(allVerts, allIdx, specs, H, pins.TryGetValue("Chin", out var ch) ? ch.Y : (float?)null, out var outBones, out var outWeights, meshNames);
            sw.Stop();
            GD.Print($"  VoxelHeat solved kirikonorig.glb in {sw.ElapsedMilliseconds} ms (ok={ok}, H={H:F3}m, pelvisY={pins["Pelvis"].Y:F3}, hipRY={pins["HipR"].Y:F3})");

            // Check coincident vertex pairs across DIFFERENT surfaces near the pelvis/hip band!
            int pelvisIdx = specs.FindIndex(sp => sp.Name == "pelvis");
            int legRIdx = specs.FindIndex(sp => sp.Name == "leg_upper_R");
            int legLIdx = specs.FindIndex(sp => sp.Name == "leg_upper_L");
            float hipY = pins["HipR"].Y;

            // Find closest cross-surface pairs near hipY +/- 0.15*H
            int crossPairsWithin2mm = 0, crossPairsWithin10mm = 0;
            float maxDiff2mm = 0f, maxDiff10mm = 0f;
            for (int sa = 0; sa < allVerts.Count; sa++)
            {
                for (int sb = sa + 1; sb < allVerts.Count; sb++)
                {
                    var va = allVerts[sa];
                    var vb = allVerts[sb];
                    // Sample hip band vertices on sa
                    int checkedA = 0;
                    for (int ia = 0; ia < va.Length && checkedA < 400; ia += Math.Max(1, va.Length / 400))
                    {
                        if (MathF.Abs(va[ia].Y - hipY) > 0.15f * H) continue;
                        checkedA++;
                        float bestDSq = 0.010f * 0.010f;
                        int bestB = -1;
                        for (int ib = 0; ib < vb.Length; ib++)
                        {
                            float dSq = va[ia].DistanceSquaredTo(vb[ib]);
                            if (dSq < bestDSq) { bestDSq = dSq; bestB = ib; }
                        }
                        if (bestB >= 0)
                        {
                            float d = MathF.Sqrt(bestDSq);
                            float waP = GetBoneWeight(outBones[sa], outWeights[sa], ia, pelvisIdx);
                            float wbP = GetBoneWeight(outBones[sb], outWeights[sb], bestB, pelvisIdx);
                            float waR = GetBoneWeight(outBones[sa], outWeights[sa], ia, legRIdx);
                            float wbR = GetBoneWeight(outBones[sb], outWeights[sb], bestB, legRIdx);
                            float diff = MathF.Max(MathF.Abs(waP - wbP), MathF.Abs(waR - wbR));
                            if (d <= 0.002f) { crossPairsWithin2mm++; if (diff > maxDiff2mm) maxDiff2mm = diff; }
                            if (d <= 0.010f) { crossPairsWithin10mm++; if (diff > maxDiff10mm) maxDiff10mm = diff; }
                        }
                    }
                }
            }
            GD.Print($"  After VoxelHeat (BEFORE AdjustLimbTwist): pairs<=2mm={crossPairsWithin2mm} (maxDiff={maxDiff2mm:F4}), pairs<=10mm={crossPairsWithin10mm} (maxDiff={maxDiff10mm:F4})");

            // Now simulate per-surface AdjustLimbTwistAndHipWeights (what RigPreRiggedModel currently does!)
            var skel = new Skeleton3D();
            for (int b = 0; b < specs.Count; b++)
            {
                skel.AddBone(specs[b].Name);
                if (specs[b].ParentIndex >= 0) skel.SetBoneParent(b, specs[b].ParentIndex);
                Transform3D pG = specs[b].ParentIndex >= 0 ? new Transform3D(Basis.Identity, specs[specs[b].ParentIndex].Origin) : Transform3D.Identity;
                skel.SetBoneRest(b, pG.AffineInverse() * new Transform3D(Basis.Identity, specs[b].Origin));
            }
            // Add twist bones like Deadlock heroes
            foreach (string side in new[] { "L", "R" })
            {
                int up = skel.FindBone($"leg_upper_{side}");
                int lo = skel.FindBone($"leg_lower_{side}");
                Vector3 pUp = skel.GetBoneGlobalRest(up).Origin;
                Vector3 pLo = skel.GetBoneGlobalRest(lo).Origin;
                int t0 = skel.GetBoneCount(); skel.AddBone($"leg_upper_{side}_TWIST"); skel.SetBoneParent(t0, up);
                skel.SetBoneRest(t0, new Transform3D(Basis.Identity, (pLo - pUp) * 0.33f));
                int t1 = skel.GetBoneCount(); skel.AddBone($"leg_upper_{side}_TWIST1"); skel.SetBoneParent(t1, up);
                skel.SetBoneRest(t1, new Transform3D(Basis.Identity, (pLo - pUp) * 0.66f));
            }
            skel.ResetBonePoses();
            var prof = CustomModelAutoRigger.BuildLimbTwistProfile(skel);
            var postBones = new List<int[]>();
            var postWeights = new List<float[]>();
            var multiSurfaces = new List<(Vector3[] Verts, int[] Indices, int[] BoneIndices, float[] BoneWeights)>();
            for (int s = 0; s < allVerts.Count; s++)
            {
                var bi = (int[])outBones[s].Clone();
                var bw = (float[])outWeights[s].Clone();
                postBones.Add(bi);
                postWeights.Add(bw);
                multiSurfaces.Add((allVerts[s], allIdx[s], bi, bw));
            }
            CustomModelAutoRigger.AdjustLimbTwistAndHipWeightsMultiSurface(skel, multiSurfaces, prof);

            int twistRIdx = skel.FindBone("leg_upper_R_TWIST");
            float postMaxDiff2mm = 0f, postMaxDiff10mm = 0f;
            for (int sa = 0; sa < allVerts.Count; sa++)
            {
                for (int sb = sa + 1; sb < allVerts.Count; sb++)
                {
                    var va = allVerts[sa];
                    var vb = allVerts[sb];
                    int checkedA = 0;
                    for (int ia = 0; ia < va.Length && checkedA < 400; ia += Math.Max(1, va.Length / 400))
                    {
                        if (MathF.Abs(va[ia].Y - hipY) > 0.15f * H) continue;
                        checkedA++;
                        float bestDSq = 0.010f * 0.010f;
                        int bestB = -1;
                        for (int ib = 0; ib < vb.Length; ib++)
                        {
                            float dSq = va[ia].DistanceSquaredTo(vb[ib]);
                            if (dSq < bestDSq) { bestDSq = dSq; bestB = ib; }
                        }
                        if (bestB >= 0)
                        {
                            float d = MathF.Sqrt(bestDSq);
                            float waP = GetBoneWeight(postBones[sa], postWeights[sa], ia, pelvisIdx);
                            float wbP = GetBoneWeight(postBones[sb], postWeights[sb], bestB, pelvisIdx);
                            float waT = GetBoneWeight(postBones[sa], postWeights[sa], ia, twistRIdx);
                            float wbT = GetBoneWeight(postBones[sb], postWeights[sb], bestB, twistRIdx);
                            float diff = MathF.Max(MathF.Abs(waP - wbP), MathF.Abs(waT - wbT));
                            if (d <= 0.002f && diff > postMaxDiff2mm) postMaxDiff2mm = diff;
                            if (d <= 0.010f && diff > postMaxDiff10mm) postMaxDiff10mm = diff;
                        }
                    }
                }
            }
            GD.Print($"  After cross-surface AdjustLimbTwistMultiSurface: maxDiff<=2mm={postMaxDiff2mm:F4}, maxDiff<=10mm={postMaxDiff10mm:F4}");
            Check(postMaxDiff2mm < 0.005f, $"kirikonorig.glb upper/legs seam coincident vertices welded with 0 gap (maxDiff<=2mm={postMaxDiff2mm:F4} < 0.005)");
            skel.Free();
            scene.Free();
        }
    }
}
