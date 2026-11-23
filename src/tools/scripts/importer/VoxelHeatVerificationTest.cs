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
                TestLandmarkAutoRigOnPreviouslyRiggedModel();
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

        private void TestLandmarkAutoRigOnPreviouslyRiggedModel()
        {
            GD.Print("\n--- Test 5: LandmarkAutoRig on Previously Rigged Model (Old Skeleton + Non-Identity cm Skin) ---");

            // Build a synthetic humanoid mesh in upright 1.7m scene space first.
            var (torsoV, torsoI) = BuildCylinder(new Vector3(0, 0.90f, 0), new Vector3(0, 1.50f, 0), 0.14f, 16, 10, true);
            var (headV, headI) = BuildCylinder(new Vector3(0, 1.52f, 0), new Vector3(0, 1.74f, 0), 0.10f, 12, 6, true);
            var (legLV, legLI) = BuildCylinder(new Vector3(0.10f, 0.02f, 0), new Vector3(0.10f, 0.92f, 0), 0.065f, 12, 10, true);
            var (legRV, legRI) = BuildCylinder(new Vector3(-0.10f, 0.02f, 0), new Vector3(-0.10f, 0.92f, 0), 0.065f, 12, 10, true);
            var (armLV, armLI) = BuildCylinder(new Vector3(0.16f, 1.42f, 0), new Vector3(0.68f, 1.32f, 0), 0.045f, 10, 8, true);
            var (armRV, armRI) = BuildCylinder(new Vector3(-0.16f, 1.42f, 0), new Vector3(-0.68f, 1.32f, 0), 0.045f, 10, 8, true);

            var sceneVerts = new List<Vector3>();
            var allIdx = new List<int>();
            void AppendPart(Vector3[] v, int[] idx)
            {
                int off = sceneVerts.Count;
                sceneVerts.AddRange(v);
                foreach (int t in idx) allIdx.Add(off + t);
            }
            AppendPart(torsoV, torsoI);
            AppendPart(headV, headI);
            AppendPart(legLV, legLI);
            AppendPart(legRV, legRI);
            AppendPart(armLV, armLI);
            AppendPart(armRV, armRI);

            // Create a previously rigged imported scene whose OLD Skeleton3D has 0.01x cm scale,
            // 90° X rotation, and scrambled arbitrary bones in wildly different positions!
            var importedScene = new Node3D { Name = "PreRiggedImportedRoot" };
            var oldSkel = new Skeleton3D
            {
                Name = "OldArmature",
                Transform = new Transform3D(Basis.FromEuler(new Vector3(Mathf.DegToRad(90f), 0f, 0f)) * Basis.FromScale(Vector3.One * 0.01f), new Vector3(0f, 0f, 0f))
            };
            importedScene.AddChild(oldSkel);

            // Populate 30 arbitrary old bones with huge offsets in cm space so any bone-index mismatch explodes the mesh by meters!
            var oldGlobalRest = new Transform3D[30];
            for (int b = 0; b < 30; b++)
            {
                oldSkel.AddBone($"old_scrambled_bone_{b}");
                if (b > 0) oldSkel.SetBoneParent(b, b - 1);
                Vector3 posCm = new Vector3((b % 5 - 2) * 35f, (b / 5) * 45f, ((b * 7) % 5 - 2) * 30f);
                Basis rot = Basis.FromEuler(new Vector3(b * 0.4f, b * 0.7f, b * 0.3f));
                oldGlobalRest[b] = new Transform3D(rot, posCm);
                Transform3D local = b > 0 ? oldGlobalRest[b - 1].AffineInverse() * oldGlobalRest[b] : oldGlobalRest[b];
                oldSkel.SetBoneRest(b, local);
            }
            oldSkel.ResetBonePoses();

            // Encode the mesh's raw vertices in 100x cm Z-up space (different from scene space!),
            // with an old Skin whose inverse-bind matrices map rawVerts back to exact upright sceneVerts!
            Transform3D oldSkelToScene = oldSkel.Transform;
            Transform3D rawToScene = new Transform3D(Basis.FromEuler(new Vector3(Mathf.DegToRad(-90f), 0f, 0f)) * Basis.FromScale(Vector3.One * 0.01f), new Vector3(0.5f, -0.3f, 0.2f));
            Transform3D sceneToRaw = rawToScene.AffineInverse();

            var rawVerts = new Vector3[sceneVerts.Count];
            var rawNorms = new Vector3[sceneVerts.Count];
            var rawBones = new int[sceneVerts.Count * 4];
            var rawWeights = new float[sceneVerts.Count * 4];
            for (int i = 0; i < sceneVerts.Count; i++)
            {
                rawVerts[i] = sceneToRaw * sceneVerts[i];
                rawNorms[i] = Vector3.Up;
                // Skin each vertex to an old scrambled bone (0..29)
                int oldB = i % 30;
                rawBones[i * 4] = oldB;
                rawWeights[i * 4] = 1f;
            }

            var oldSkin = new Skin();
            for (int b = 0; b < 30; b++)
            {
                // Ensure: oldSkelToScene * oldGlobalRest[b] * oldBindPose[b] * rawVert == sceneVert
                // => oldBindPose[b] = (oldSkelToScene * oldGlobalRest[b]).AffineInverse() * rawToScene
                Transform3D oldBindPose = (oldSkelToScene * oldGlobalRest[b]).AffineInverse() * rawToScene;
                oldSkin.AddBind(b, oldBindPose);
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = rawVerts;
            arrays[(int)Mesh.ArrayType.Normal] = rawNorms;
            arrays[(int)Mesh.ArrayType.Index] = allIdx.ToArray();
            arrays[(int)Mesh.ArrayType.Bones] = rawBones;
            arrays[(int)Mesh.ArrayType.Weights] = rawWeights;
            var oldMesh = new ArrayMesh();
            oldMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

            // Parent MeshInstance3D directly under OldArmature with Skeleton = ".." (exact glTF/FBX layout!)
            var mi = new MeshInstance3D
            {
                Name = "CharacterBody",
                Mesh = oldMesh,
                Skin = oldSkin,
                Skeleton = new NodePath("..")
            };
            oldSkel.AddChild(mi);

            // Add importedScene to SceneTree so mi.GetNodeOrNull(mi.Skeleton) resolves OldArmature just like in the real app!
            Root.AddChild(importedScene);

            // 1. EstimateLandmarks must see the evaluated 1.7m upright humanoid (NOT the 100x raw buffer!)
            var estPins = CustomLandmarkWeightSolver.EstimateLandmarks(importedScene, new[] { mi });
            Check(estPins.ContainsKey("Head") && estPins.ContainsKey("AnkleL"), "EstimateLandmarks succeeded on pre-rigged cm-scaled mesh");
            float estH = estPins["Head"].Y - estPins["AnkleL"].Y;
            Check(estH > 1.2f && estH < 2.0f, $"EstimateLandmarks height matches evaluated 1.7m rest pose (estH={estH:F3}m)");

            // 2. Build a matching target Deadlock hero rig and run full CustomModelAutoRigger.RigAndBind with LandmarkAutoRig!
            var heroRoot = new Node3D { Name = "SyntheticHeroRoot" };
            var heroSkel = new Skeleton3D { Name = "HeroSkeleton" };
            heroRoot.AddChild(heroSkel);
            var refRigForHero = new ReferenceRigDef { SpineCount = 3, FingerMode = ReferenceFingerRigMode.RigidMitten };
            foreach (var kv in estPins) refRigForHero.Joints[kv.Key] = kv.Value;
            refRigForHero.EnsureDefaultJoints(1.74f);
            var heroSpecs = refRigForHero.BuildResolvedBones(Transform3D.Identity, 1.74f, 1f, false);
            var heroGlobalRest = new Transform3D[heroSpecs.Count];
            for (int b = 0; b < heroSpecs.Count; b++)
            {
                heroSkel.AddBone(heroSpecs[b].Name);
                heroGlobalRest[b] = new Transform3D(Basis.Identity, heroSpecs[b].Origin);
            }
            for (int b = 0; b < heroSpecs.Count; b++)
            {
                int p = heroSpecs[b].ParentIndex;
                if (p >= 0) heroSkel.SetBoneParent(b, p);
                heroSkel.SetBoneRest(b, p >= 0 ? heroGlobalRest[p].AffineInverse() * heroGlobalRest[b] : heroGlobalRest[b]);
            }
            // Add native Deadlock thigh, upper-arm, and forearm twist bones to verify AutoCounterTwistWeights on LandmarkAutoRig!
            void AddTwistPair(string upperName, string lowerName, string twist0Name, string twist1Name)
            {
                int u = heroSkel.FindBone(upperName);
                int l = heroSkel.FindBone(lowerName);
                if (u < 0 || l < 0) return;
                Vector3 pU = heroSkel.GetBoneGlobalRest(u).Origin;
                Vector3 pL = heroSkel.GetBoneGlobalRest(l).Origin;
                int t0 = heroSkel.GetBoneCount(); heroSkel.AddBone(twist0Name); heroSkel.SetBoneParent(t0, u);
                heroSkel.SetBoneRest(t0, new Transform3D(Basis.Identity, (pL - pU) * 0.33f));
                int t1 = heroSkel.GetBoneCount(); heroSkel.AddBone(twist1Name); heroSkel.SetBoneParent(t1, u);
                heroSkel.SetBoneRest(t1, new Transform3D(Basis.Identity, (pL - pU) * 0.66f));
            }
            foreach (string side in new[] { "L", "R" })
            {
                AddTwistPair($"leg_upper_{side}", $"leg_lower_{side}", $"leg_upper_{side}_TWIST", $"leg_upper_{side}_TWIST1");
                AddTwistPair($"arm_upper_{side}", $"arm_lower_{side}", $"arm_upper_{side}_TWIST", $"arm_upper_{side}_TWIST1");
                AddTwistPair($"arm_lower_{side}", $"hand_{side}", $"arm_lower_{side}_TWIST", $"arm_lower_{side}_TWIST1");
            }
            heroSkel.ResetBonePoses();
            Root.AddChild(heroRoot);

            var options = new CustomModelAutoRigger.RiggingOptions
            {
                Method = CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig,
                AlignedTransform = Transform3D.Identity,
                ReferenceRig = refRigForHero.Clone(),
                RigFamilyOverride = RigFamily.Mixamo, // Stale override from old rig should be ignored
                AutoSnapArmsToHero = true,
                AutoSnapLegsToHero = true,
                AutoSnapHeadToHero = true,
                AutoAlignHandOrientation = false
            };

            var rigRes = CustomModelAutoRigger.RigAndBind(importedScene, heroRoot, options);
            Check(rigRes.Success && rigRes.CustomSubmeshes.Count == 1, "LandmarkAutoRig RigAndBind succeeded on previously rigged model");

            // 3. Verify ZERO vertex explosion: every output vertex must stay tightly within the 1.74m humanoid bounds!
            Vector3 minOut = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 maxOut = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            float maxErr = 0f;
            if (rigRes.Success && rigRes.CustomSubmeshes.Count > 0)
            {
                var outArr = rigRes.CustomSubmeshes[0].Mesh.SurfaceGetArrays(0);
                var outVerts = outArr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                for (int i = 0; i < outVerts.Length; i++)
                {
                    minOut = minOut.Min(outVerts[i]);
                    maxOut = maxOut.Max(outVerts[i]);
                    if (i < sceneVerts.Count)
                    {
                        float err = outVerts[i].DistanceTo(sceneVerts[i]);
                        if (err > maxErr) maxErr = err;
                    }
                }
            }
            GD.Print($"  Rigged output bounds: min={minOut:F3}, max={maxOut:F3}, maxVertErr={maxErr:F4}m");
            Check(minOut.X > -0.95f && maxOut.X < 0.95f && minOut.Y > -0.15f && maxOut.Y < 1.95f && minOut.Z > -0.50f && maxOut.Z < 0.50f,
                $"Zero vertex explosion on pre-rigged model (bounds=[{minOut:F2}..{maxOut:F2}])");
            Check(maxErr < 0.005f, $"Output vertices match evaluated rest pose tightly (maxErr={maxErr:F4}m < 0.005m)");

            // 3b. Verify ALL 12 native Deadlock thigh/upper-arm/forearm twist bones receive weights on LandmarkAutoRig!
            var weightedTwistCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (rigRes.Success && rigRes.CustomSubmeshes.Count > 0)
            {
                var outMi = rigRes.CustomSubmeshes[0];
                var outArr = outMi.Mesh.SurfaceGetArrays(0);
                var outBones = outArr[(int)Mesh.ArrayType.Bones].AsInt32Array();
                var outWeights = outArr[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                var outSkin = outMi.Skin;
                for (int i = 0; i < outBones.Length && i < outWeights.Length; i++)
                {
                    if (outWeights[i] <= 0.01f) continue;
                    int bi = outBones[i];
                    int sb = (outSkin != null && bi >= 0 && bi < outSkin.GetBindCount()) ? outSkin.GetBindBone(bi) : bi;
                    if (sb >= 0 && sb < heroSkel.GetBoneCount())
                    {
                        string bn = heroSkel.GetBoneName(sb);
                        if (bn.Contains("TWIST", StringComparison.OrdinalIgnoreCase))
                            weightedTwistCounts[bn] = weightedTwistCounts.GetValueOrDefault(bn) + 1;
                    }
                }
            }
            string[] expectedTwists =
            {
                "leg_upper_L_TWIST", "leg_upper_L_TWIST1", "leg_upper_R_TWIST", "leg_upper_R_TWIST1",
                "arm_upper_L_TWIST", "arm_upper_L_TWIST1", "arm_upper_R_TWIST", "arm_upper_R_TWIST1",
                "arm_lower_L_TWIST", "arm_lower_L_TWIST1", "arm_lower_R_TWIST", "arm_lower_R_TWIST1"
            };
            var missingTwists = new List<string>();
            foreach (var tn in expectedTwists)
                if (!weightedTwistCounts.TryGetValue(tn, out int c) || c == 0) missingTwists.Add(tn);
            GD.Print($"  Weighted twist bones ({weightedTwistCounts.Count}/12): {string.Join(", ", weightedTwistCounts)}");
            Check(missingTwists.Count == 0, $"LandmarkAutoRig populates all 12 thigh/arm/forearm TWIST bones (missing=[{string.Join(", ", missingTwists)}])");

            // 4. Verify Restore() left original mesh/skin/skeleton intact
            Check(mi.Mesh == oldMesh && mi.Skin == oldSkin && mi.Skeleton == new NodePath(".."),
                "BuildSyntheticRig.Restore() cleanly restored original Mesh, Skin, and Skeleton NodePath");

            Root.RemoveChild(heroRoot);
            heroRoot.Free();
            Root.RemoveChild(importedScene);
            importedScene.Free();
        }
    }
}
