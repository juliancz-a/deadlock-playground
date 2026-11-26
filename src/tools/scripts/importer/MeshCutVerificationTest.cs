using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using DeadlockPlayground.Importer;
using DeadlockPlayground.Painter;
using DeadlockPlayground.Workspace;

namespace DeadlockPlayground.Tests
{
    /// <summary>
    /// Headless verification suite for the Interactive 3D Mesh Cut / Trim Editor:
    /// 1. RLE cut mask encoding, decoding, and multi-slot union.
    /// 2. 1mm UV-seam welded topology, Connected Island, Bone-Region (+ child bones + Mirror X),
    ///    Grow, Shrink, Invert, Lasso, Box, Brush, Cut Selected, and Keep Only Selected.
    /// 3. Full Undo / Redo / Reset stack parity.
    /// 4. 8-bone skinning (FlagUse8BoneWeights) preservation across RecomputeHeroDeformations &amp; HeroMeshHierarchy.
    /// 5. 100%-cut surface stability (surface count invariant in Godot + 0 degenerate tris in Source 2 SMD export).
    /// 6. Imported mesh cut masks, stacked accessory union/restore, and Workspace JSON round-trip.
    /// </summary>
    public partial class MeshCutVerificationTest : SceneTree
    {
        private bool _allPassed = true;

        private void Check(bool cond, string label)
        {
            if (cond)
            {
                GD.Print($"  [PASS] {label}");
            }
            else
            {
                GD.PrintErr($"  [FAIL] {label}");
                _allPassed = false;
            }
        }

        private static Skeleton3D CreateTestHumanoidSkeleton()
        {
            var skel = new Skeleton3D { Name = "Skeleton3D" };

            int AddBone(string name, int parent, Vector3 localPos)
            {
                int idx = skel.AddBone(name);
                if (parent >= 0) skel.SetBoneParent(idx, parent);
                skel.SetBoneRest(idx, new Transform3D(Basis.Identity, localPos));
                skel.SetBonePosePosition(idx, localPos);
                skel.SetBonePoseRotation(idx, Quaternion.Identity);
                skel.SetBonePoseScale(idx, Vector3.One);
                return idx;
            }

            int pelvis = AddBone("pelvis", -1, new Vector3(0f, 0.95f, 0f));
            int spine2 = AddBone("spine_2", pelvis, new Vector3(0f, 0.35f, 0f));
            int armUpperL = AddBone("arm_upper_L", spine2, new Vector3(0.22f, 0.15f, 0f));
            int handL = AddBone("hand_L", armUpperL, new Vector3(0.45f, -0.10f, 0f));
            AddBone("finger_index_0_L", handL, new Vector3(0.08f, 0f, 0f));
            int armUpperR = AddBone("arm_upper_R", spine2, new Vector3(-0.22f, 0.15f, 0f));
            int handR = AddBone("hand_R", armUpperR, new Vector3(-0.45f, -0.10f, 0f));
            AddBone("finger_index_0_R", handR, new Vector3(-0.08f, 0f, 0f));
            AddBone("head", spine2, new Vector3(0f, 0.35f, 0f));

            skel.ForceUpdateBoneChildTransform(pelvis);
            return skel;
        }

        /// <summary>
        /// Builds a synthetic 2-surface 8-bone skinned hero mesh mimicking Violet's upper body:
        /// Surface 0 ("upper_body"): Dress (2x2 grid = 8 tris with an explicit UV seam split on coincident vertices,
        ///   weighted to spine_2) + Left Hand (2 tris weighted to hand_L &amp; finger_index_0_L) +
        ///   Right Hand (2 tris weighted to hand_R &amp; finger_index_0_R). Total = 12 tris, 3 welded islands.
        /// Surface 1 ("head_cap"): 4 tris weighted to head.
        /// </summary>
        private static MeshInstance3D CreateSyntheticVioletMesh(Skeleton3D skel, out Skin skin)
        {
            skin = CustomModelAutoRigger.CreateFullSkeletonSkin(skel);

            int bSpine2 = skel.FindBone("spine_2");
            int bHandL = skel.FindBone("hand_L");
            int bFingerL = skel.FindBone("finger_index_0_L");
            int bHandR = skel.FindBone("hand_R");
            int bFingerR = skel.FindBone("finger_index_0_R");
            int bHead = skel.FindBone("head");

            var arrMesh = new ArrayMesh();

            // --- Surface 0: Dress (8 tris across UV seam) + Left Hand (2 tris) + Right Hand (2 tris) ---
            {
                var verts = new List<Vector3>();
                var norms = new List<Vector3>();
                var uvs = new List<Vector2>();
                var indices = new List<int>();
                var bones8 = new List<int>();
                var weights8 = new List<float>();

                int AddVert(Vector3 p, Vector2 uv, int b0, float w0, int b1 = 0, float w1 = 0f)
                {
                    int idx = verts.Count;
                    verts.Add(p);
                    norms.Add(Vector3.Back);
                    uvs.Add(uv);
                    bones8.Add(b0); bones8.Add(b1); bones8.Add(0); bones8.Add(0);
                    bones8.Add(0);  bones8.Add(0);  bones8.Add(0); bones8.Add(0);
                    weights8.Add(w0); weights8.Add(w1); weights8.Add(0f); weights8.Add(0f);
                    weights8.Add(0f); weights8.Add(0f); weights8.Add(0f); weights8.Add(0f);
                    return idx;
                }

                void AddQuad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, int b0, float w0, int b1 = 0, float w1 = 0f)
                {
                    int v0 = AddVert(p0, new Vector2(0, 0), b0, w0, b1, w1);
                    int v1 = AddVert(p1, new Vector2(1, 0), b0, w0, b1, w1);
                    int v2 = AddVert(p2, new Vector2(1, 1), b0, w0, b1, w1);
                    int v3 = AddVert(p3, new Vector2(0, 1), b0, w0, b1, w1);
                    indices.Add(v0); indices.Add(v1); indices.Add(v2);
                    indices.Add(v0); indices.Add(v2); indices.Add(v3);
                }

                // Dress: 4 quads arranged 2x2 around X=0 with separate UV-seam vertices along shared edges!
                // Left-top, Right-top, Left-bottom, Right-bottom (8 tris, all coincident at shared borders)
                AddQuad(new Vector3(-0.20f, 1.35f, 0.10f), new Vector3(0.00f, 1.35f, 0.10f), new Vector3(0.00f, 1.15f, 0.10f), new Vector3(-0.20f, 1.15f, 0.10f), bSpine2, 1f);
                AddQuad(new Vector3(0.00f, 1.35f, 0.10f), new Vector3(0.20f, 1.35f, 0.10f), new Vector3(0.20f, 1.15f, 0.10f), new Vector3(0.00f, 1.15f, 0.10f), bSpine2, 1f);
                AddQuad(new Vector3(-0.20f, 1.15f, 0.10f), new Vector3(0.00f, 1.15f, 0.10f), new Vector3(0.00f, 0.95f, 0.10f), new Vector3(-0.20f, 0.95f, 0.10f), bSpine2, 1f);
                AddQuad(new Vector3(0.00f, 1.15f, 0.10f), new Vector3(0.20f, 1.15f, 0.10f), new Vector3(0.20f, 0.95f, 0.10f), new Vector3(0.00f, 0.95f, 0.10f), bSpine2, 1f);

                // Left hand + finger (2 tris, separate spatial island at X = +0.67..+0.80)
                AddQuad(new Vector3(0.65f, 1.38f, 0.05f), new Vector3(0.78f, 1.38f, 0.05f), new Vector3(0.78f, 1.28f, 0.05f), new Vector3(0.65f, 1.28f, 0.05f), bFingerL, 0.7f, bHandL, 0.3f);

                // Right hand + finger (2 tris, mirrored spatial island at X = -0.78..-0.65)
                AddQuad(new Vector3(-0.78f, 1.38f, 0.05f), new Vector3(-0.65f, 1.38f, 0.05f), new Vector3(-0.65f, 1.28f, 0.05f), new Vector3(-0.78f, 1.28f, 0.05f), bFingerR, 0.7f, bHandR, 0.3f);

                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
                arrays[(int)Mesh.ArrayType.Normal] = norms.ToArray();
                arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
                arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
                arrays[(int)Mesh.ArrayType.Bones] = bones8.ToArray();
                arrays[(int)Mesh.ArrayType.Weights] = weights8.ToArray();

                arrMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, Mesh.ArrayFormat.FlagUse8BoneWeights);
                arrMesh.SurfaceSetName(0, "violet_upper_body");
                var mat0 = new StandardMaterial3D { ResourceName = "materials/models/heroes_staging/violet/violet_upper.vmat" };
                arrMesh.SurfaceSetMaterial(0, mat0);
            }

            // --- Surface 1: Head cap (4 tris weighted to head) ---
            {
                var verts = new Vector3[]
                {
                    new(-0.1f, 1.65f, 0.05f), new(0.1f, 1.65f, 0.05f), new(0.1f, 1.85f, 0.05f), new(-0.1f, 1.85f, 0.05f),
                    new(-0.1f, 1.65f, -0.05f), new(0.1f, 1.65f, -0.05f), new(0.1f, 1.85f, -0.05f), new(-0.1f, 1.85f, -0.05f)
                };
                var norms = Enumerable.Repeat(Vector3.Up, verts.Length).ToArray();
                var uvs = Enumerable.Repeat(Vector2.Zero, verts.Length).ToArray();
                var idx = new int[] { 0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6 };
                var bones8 = new int[verts.Length * 8];
                var weights8 = new float[verts.Length * 8];
                for (int v = 0; v < verts.Length; v++) { bones8[v * 8] = bHead; weights8[v * 8] = 1f; }

                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = verts;
                arrays[(int)Mesh.ArrayType.Normal] = norms;
                arrays[(int)Mesh.ArrayType.TexUV] = uvs;
                arrays[(int)Mesh.ArrayType.Index] = idx;
                arrays[(int)Mesh.ArrayType.Bones] = bones8;
                arrays[(int)Mesh.ArrayType.Weights] = weights8;

                arrMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, Mesh.ArrayFormat.FlagUse8BoneWeights);
                arrMesh.SurfaceSetName(1, "violet_head");
                var mat1 = new StandardMaterial3D { ResourceName = "materials/models/heroes_staging/violet/violet_head.vmat" };
                arrMesh.SurfaceSetMaterial(1, mat1);
            }

            var mi = new MeshInstance3D
            {
                Name = "_violet_body",
                Mesh = arrMesh,
                Skin = skin
            };
            return mi;
        }

        public override async void _Initialize()
        {
            GD.Print("=======================================================");
            GD.Print("        MESH CUT / TRIM EDITOR VERIFICATION TEST       ");
            GD.Print("=======================================================");

            try
            {
                // -----------------------------------------------------------------
                // 1. RLE Encoding, Decoding & Multi-Slot Union
                // -----------------------------------------------------------------
                var sampleSet = new HashSet<int> { 0, 1, 2, 5, 7, 8, 9, 10, 42 };
                int[] rle = MeshCutEditorCore.EncodeCutMask(sampleSet);
                var decoded = MeshCutEditorCore.DecodeCutMask(rle);
                Check(rle.Length == 8 && decoded.SetEquals(sampleSet) && MeshCutEditorCore.CountCutTriangles(rle) == sampleSet.Count,
                    "RLE encode/decode round-trip and CountCutTriangles are exact");

                int[] unionRle = MeshCutEditorCore.UnionRleMasks(new[]
                {
                    MeshCutEditorCore.EncodeCutMask(new[] { 0, 1, 2 }),
                    MeshCutEditorCore.EncodeCutMask(new[] { 2, 3, 4 })
                });
                Check(MeshCutEditorCore.DecodeCutMask(unionRle).SetEquals(new[] { 0, 1, 2, 3, 4 }),
                    "UnionRleMasks merges overlapping slot cut ranges cleanly");

                // -----------------------------------------------------------------
                // 2. Topology Welding, Bone-Region (+ Children + Mirror X),
                //    Island, Grow/Shrink/Invert, Keep Only & Undo/Redo
                // -----------------------------------------------------------------
                var heroRoot = new Node3D { Name = "TestHeroRoot" };
                Root.AddChild(heroRoot);
                var skel = CreateTestHumanoidSkeleton();
                heroRoot.AddChild(skel);
                var violetMi = CreateSyntheticVioletMesh(skel, out var skin);
                skel.AddChild(violetMi);
                violetMi.Skeleton = violetMi.GetPathTo(skel);

                string surf0Key = CustomModelAutoRigger.MakeOriginalSurfaceDeformKey(violetMi, 0);
                var session = MeshCutEditorCore.CaptureSurface(violetMi, 0, surf0Key, "Upper Body", true, heroRoot, skel);
                Check(session != null && session.TriCount == 12 && session.BoneStride == 8,
                    "CaptureSurface captured all 12 triangles and detected 8-bone skinning stride");
                Check(session.IslandCount == 3,
                    $"1mm UV-seam welding fused dress seam cleanly into exactly 3 islands (Dress, Hand_L, Hand_R), got {session.IslandCount}");

                // Bone-Region Select: pick hand_L with Include Children + Mirror X -> selects ONLY both hands (tris 8,9,10,11)!
                var history = new MeshCutEditorCore.MeshCutHistory();
                int bHandL = skel.FindBone("hand_L");
                history.RecordBeforeMutation("Bone Select Hands", session.CutTriangles, session.SelectedTriangles);
                bool boneSelOk = MeshCutEditorCore.SelectByBoneRegion(
                    session, bHandL, includeChildren: true, mirrorX: true, MeshCutSelectOp.Replace, restoreMode: false);
                Check(boneSelOk && session.SelectedTriangles.SetEquals(new[] { 8, 9, 10, 11 }),
                    "Bone-Region Select (hand_L + Include Children + Mirror X) isolated both hands (4 tris) with 0 dress tris");

                // Keep Only Selected -> cuts all 8 dress tris (0..7), keeps hands (8..11)!
                history.RecordBeforeMutation("Keep Only Hands", session.CutTriangles, session.SelectedTriangles);
                int cutDressCnt = MeshCutEditorCore.KeepOnlySelected(session);
                Check(cutDressCnt == 8 && session.CutTriangles.SetEquals(Enumerable.Range(0, 8)) && session.SelectedTriangles.Count == 0,
                    "KeepOnlySelected cut all 8 dress triangles and preserved both hands");

                // Restore Mode + Island Select on dress -> selects all 8 cut dress tris in 1 click -> Restore!
                history.RecordBeforeMutation("Island Select Cut Dress", session.CutTriangles, session.SelectedTriangles);
                bool islandRestoreSel = MeshCutEditorCore.SelectByIsland(session, 0, MeshCutSelectOp.Replace, mirrorX: false, restoreMode: true);
                Check(islandRestoreSel && session.SelectedTriangles.SetEquals(Enumerable.Range(0, 8)),
                    "Island Select in Restore Mode selected all 8 welded cut dress triangles across UV seam");

                history.RecordBeforeMutation("Restore Dress", session.CutTriangles, session.SelectedTriangles);
                int restoredCnt = MeshCutEditorCore.RestoreSelected(session);
                Check(restoredCnt == 8 && session.CutTriangles.Count == 0,
                    "RestoreSelected restored all 8 dress triangles back to original");

                // Undo x2 -> back to Keep Only Hands state! Then Redo x2 -> back to restored!
                Check(history.TryUndo(session.CutTriangles, session.SelectedTriangles, session.TriCount, out _) &&
                      history.TryUndo(session.CutTriangles, session.SelectedTriangles, session.TriCount, out _) &&
                      session.CutTriangles.SetEquals(Enumerable.Range(0, 8)),
                    "Undo x2 cleanly reverted Restore back to isolated-hands state");

                Check(history.TryRedo(session.CutTriangles, session.SelectedTriangles, session.TriCount, out _) &&
                      history.TryRedo(session.CutTriangles, session.SelectedTriangles, session.TriCount, out _) &&
                      session.CutTriangles.Count == 0,
                    "Redo x2 cleanly re-applied Restore");

                // Grow, Shrink, Invert on dress island
                session.SelectedTriangles.Clear();
                session.SelectedTriangles.Add(0);
                bool grew = MeshCutEditorCore.GrowSelection(session, restoreMode: false);
                int afterGrow = session.SelectedTriangles.Count;
                bool shrunk = MeshCutEditorCore.ShrinkSelection(session, restoreMode: false);
                bool inverted = MeshCutEditorCore.InvertSelection(session, restoreMode: false);
                Check(grew && afterGrow > 1 && shrunk && inverted && session.SelectedTriangles.Count > 0,
                    $"Grow ({afterGrow} tris), Shrink, and Invert selection operations succeeded");

                // Brush selection test (centered on left hand at X=+0.715, Y=1.33, Z=0.05 with Mirror X)
                session.SelectedTriangles.Clear();
                bool brushed = MeshCutEditorCore.SelectByBrush(
                    session, null,
                    new Vector3(0.715f, 1.33f, 0.05f),
                    new Vector3(0.715f, 1.33f, 1.0f),
                    Vector3.Forward,
                    radius: 0.12f,
                    MeshCutSelectOp.Add,
                    xRay: true,
                    mirrorX: true,
                    restoreMode: false);
                Check(brushed && session.SelectedTriangles.SetEquals(new[] { 8, 9, 10, 11 }),
                    "3D Brush Select with Mirror X selected both left and right hands symmetrically");

                // -----------------------------------------------------------------
                // 3. 8-Bone Skinning Deformation Preservation + HeroMeshHierarchy
                // -----------------------------------------------------------------
                var dressCutRle = MeshCutEditorCore.EncodeCutMask(Enumerable.Range(0, 8));
                var previewCutOpts = new CustomModelAutoRigger.RiggingOptions
                {
                    OriginalMeshCutMasks = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
                    {
                        [surf0Key] = dressCutRle
                    }
                };
                CustomModelAutoRigger.RecomputeHeroDeformations(heroRoot, previewCutOpts);

                var cutArrMesh = violetMi.Mesh as ArrayMesh;
                bool has8BoneFlag = cutArrMesh != null && (cutArrMesh.SurfaceGetFormat(0) & Mesh.ArrayFormat.FlagUse8BoneWeights) != 0;
                var cutSurf0Arrays = cutArrMesh?.SurfaceGetArrays(0);
                var cutVerts0 = cutSurf0Arrays != null ? cutSurf0Arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array() : null;
                var cutIdx0 = cutSurf0Arrays != null ? cutSurf0Arrays[(int)Mesh.ArrayType.Index].AsInt32Array() : null;
                var cutBones0 = cutSurf0Arrays != null ? cutSurf0Arrays[(int)Mesh.ArrayType.Bones].AsInt32Array() : null;
                Check(violetMi.HasMeta("IsDeformedOriginalMesh") && violetMi.GetMeta("IsDeformedOriginalMesh").AsBool() &&
                      has8BoneFlag && cutVerts0 != null && cutVerts0.Length == 24 &&
                      cutIdx0 != null && cutIdx0.Length == 12 &&
                      cutBones0 != null && cutBones0.Length == 24 * 8,
                    "RecomputeHeroDeformations preserved FlagUse8BoneWeights, exact 24 vertices, 4 kept hand tris, and stamped IsDeformedOriginalMesh=true");

                // Pose arm_upper_L + hand_L rotated 45 deg and verify UpdatePosedGeometry deforms surviving hand vertices cleanly!
                await ToSignal(this, SignalName.ProcessFrame);
                MeshCutEditorCore.UpdatePosedGeometry(session);
                Vector3 beforePoseHand = session.PosedVerts[16];
                skel.SetBonePoseRotation(bHandL, new Quaternion(Vector3.Forward, Mathf.DegToRad(45f)));
                await ToSignal(this, SignalName.ProcessFrame);
                await ToSignal(this, SignalName.ProcessFrame);
                MeshCutEditorCore.UpdatePosedGeometry(session);
                Vector3 afterPoseHand = session.PosedVerts[16];
                Check(beforePoseHand.DistanceTo(afterPoseHand) > 0.01f,
                    $"Posed hand vertex deformed smoothly under bone rotation (delta={beforePoseHand.DistanceTo(afterPoseHand):F4}m)");
                skel.ResetBonePoses();
                await ToSignal(this, SignalName.ProcessFrame);

                // Verify HeroMeshHierarchy splits the cut 8-bone multi-surface mesh while preserving FlagUse8BoneWeights!
                var hierarchy = new HeroMeshHierarchy { Name = "TestHierarchy" };
                Root.AddChild(hierarchy);
                hierarchy.ScanHero(heroRoot);
                var splitUpper = hierarchy.Submeshes.FirstOrDefault(s => s.Mesh != null && s.SurfaceIndex == 0);
                bool splitHas8Bone = splitUpper?.Mesh?.Mesh is ArrayMesh splitArr &&
                                     (splitArr.SurfaceGetFormat(0) & Mesh.ArrayFormat.FlagUse8BoneWeights) != 0;
                Check(splitUpper != null && splitHas8Bone &&
                      splitUpper.Mesh.HasMeta("IsDeformedOriginalMesh") && splitUpper.Mesh.GetMeta("IsDeformedOriginalMesh").AsBool(),
                    "HeroMeshHierarchy split preserved FlagUse8BoneWeights and IsDeformedOriginalMesh on cut vanilla surface");

                // -----------------------------------------------------------------
                // 4. 100%-Cut Surface Stability + Source 2 SMD Export
                // -----------------------------------------------------------------
                // Restore composite visibility for direct AddPreservedVanillaMeshes test (ScanHero hid parent composite)
                violetMi.Visible = true;
                string surf1Key = CustomModelAutoRigger.MakeOriginalSurfaceDeformKey(violetMi, 1);
                var fullCutOpts = new CustomModelAutoRigger.RiggingOptions
                {
                    OriginalMeshCutMasks = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
                    {
                        [surf0Key] = MeshCutEditorCore.EncodeCutMask(Enumerable.Range(0, 12)), // 100% cut!
                        [surf1Key] = MeshCutEditorCore.EncodeCutMask(new[] { 0, 1 })           // 50% cut (2 of 4 tris left)
                    }
                };
                CustomModelAutoRigger.RecomputeHeroDeformations(heroRoot, fullCutOpts);
                Check(violetMi.Mesh.GetSurfaceCount() == 2,
                    "100%-cut surface 0 kept GetSurfaceCount()==2 invariant in Godot ArrayMesh");

                var bundle = CustomModelExporter.BuildExportBundle(new List<MeshInstance3D>(), skel, "violet");
                CustomModelExporter.AddPreservedVanillaMeshes(bundle, new List<MeshInstance3D> { violetMi }, new List<string>(), skel);
                Check(bundle.PreservedVanillaSubmeshes.Count == 1 &&
                      bundle.PreservedVanillaSubmeshes[0].Indices.Length == 6,
                    $"AddPreservedVanillaMeshes skipped 100%-cut surface 0 and exported exact 2 remaining tris (6 indices) for surface 1");

                string tempSmd = Path.Combine(Path.GetTempPath(), $"meshcut_verify_{Guid.NewGuid():N}.smd");
                try
                {
                    CustomModelExporter.WriteSourceSpaceSmd(tempSmd, bundle, bundle.PreservedVanillaSubmeshes);
                    string smdText = File.ReadAllText(tempSmd);
                    int matLines = smdText.Split('\n').Count(l => l.Trim().Equals("violet_head", StringComparison.OrdinalIgnoreCase));
                    Check(matLines == 2 && !smdText.Contains("violet_upper", StringComparison.OrdinalIgnoreCase),
                        $"WriteSourceSpaceSmd emitted exactly 2 triangles for violet_head and 0 for 100%-cut violet_upper");
                }
                finally
                {
                    if (File.Exists(tempSmd)) File.Delete(tempSmd);
                }

                // -----------------------------------------------------------------
                // 5. Imported Mesh Cut Mask + Stacked Accessories + Workspace JSON Round-Trip
                // -----------------------------------------------------------------
                // Restore heroRoot clean before stacking 2 synthetic accessories
                CustomModelAutoRigger.RecomputeHeroDeformations(heroRoot, new CustomModelAutoRigger.RiggingOptions());

                var accScene1 = new Node3D { Name = "SweaterAcc" };
                var sweaterBox = new MeshInstance3D { Name = "SweaterMesh", Mesh = new BoxMesh { Size = new Vector3(0.4f, 0.4f, 0.3f) } };
                accScene1.AddChild(sweaterBox);

                string importedCutKey = CustomModelAutoRigger.MakeSurfaceKeepKey("SweaterMesh", 0);
                var sweaterOpts = new CustomModelAutoRigger.RiggingOptions
                {
                    SlotId = "acc_sweater",
                    AccessoryName = "Sweater",
                    Intent = CustomModelAutoRigger.AccessoryIntent.AddAccessory,
                    Mode = CustomModelAutoRigger.ReplaceMode.PartialAccessory,
                    Method = CustomModelAutoRigger.RiggingMethod.RigidSingleBone,
                    RigidBoneName = "spine_2",
                    OriginalMeshCutMasks = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
                    {
                        [surf0Key] = MeshCutEditorCore.EncodeCutMask(new[] { 0, 1, 2, 3 })
                    },
                    ImportedMeshCutMasks = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
                    {
                        [importedCutKey] = MeshCutEditorCore.EncodeCutMask(new[] { 0, 1 })
                    }
                };
                var rig1 = CustomModelAutoRigger.RigAndBind(accScene1, heroRoot, sweaterOpts);
                var riggedSweater = rig1.CustomSubmeshes.FirstOrDefault();
                int sweaterTrisAfterCut = riggedSweater?.Mesh?.SurfaceGetArrays(0)?[(int)Mesh.ArrayType.Index].AsInt32Array()?.Length / 3 ?? 0;
                Check(rig1.Success && riggedSweater != null && riggedSweater.HasMeta("OriginalPreCutMesh") && sweaterTrisAfterCut == 10,
                    $"RigAndBind applied ImportedMeshCutMasks to sweater (12 -> {sweaterTrisAfterCut} tris) and preserved OriginalPreCutMesh");

                // Stack second accessory cutting tris 4..7 on the same hero surface
                var accScene2 = new Node3D { Name = "BeltAcc" };
                accScene2.AddChild(new MeshInstance3D { Name = "BeltMesh", Mesh = new BoxMesh { Size = new Vector3(0.3f, 0.1f, 0.25f) } });
                var beltOpts = new CustomModelAutoRigger.RiggingOptions
                {
                    SlotId = "acc_belt",
                    AccessoryName = "Belt",
                    Intent = CustomModelAutoRigger.AccessoryIntent.AddAccessory,
                    Mode = CustomModelAutoRigger.ReplaceMode.PartialAccessory,
                    Method = CustomModelAutoRigger.RiggingMethod.RigidSingleBone,
                    RigidBoneName = "pelvis",
                    OriginalMeshCutMasks = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
                    {
                        [surf0Key] = MeshCutEditorCore.EncodeCutMask(new[] { 4, 5, 6, 7 })
                    }
                };
                var rig2 = CustomModelAutoRigger.RigAndBind(accScene2, heroRoot, beltOpts);
                accScene1.Free();
                accScene2.Free();
                int stackedKeptTris = violetMi.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Index].AsInt32Array().Length / 3;
                Check(rig2.Success && stackedKeptTris == 4,
                    $"Two stacked accessories unioned hero cut masks cleanly (12 - 4 - 4 = {stackedKeptTris} tris kept)");

                // Hide slot 1 -> its cut restores while slot 2's cut stays active!
                rig1.ContainerNode.Visible = false;
                CustomModelAutoRigger.RecomputeHeroDeformations(heroRoot);
                int afterHideTris = violetMi.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Index].AsInt32Array().Length / 3;
                Check(afterHideTris == 8,
                    $"Hiding accessory slot 1 restored its 4 cut tris while preserving slot 2's cut ({afterHideTris} tris kept)");

                // Workspace JSON Round-Trip
                var wsEntry = WorkspaceCustomModelEntry.FromRiggingOptions("sweater.glb", "Sweater", sweaterOpts);
                string json = JsonSerializer.Serialize(wsEntry);
                var deserialized = JsonSerializer.Deserialize<WorkspaceCustomModelEntry>(json);
                var roundTripped = deserialized?.ToRiggingOptions();
                Check(roundTripped?.OriginalMeshCutMasks != null &&
                      roundTripped.OriginalMeshCutMasks.TryGetValue(surf0Key, out var rtOrig) &&
                      MeshCutEditorCore.DecodeCutMask(rtOrig).SetEquals(new[] { 0, 1, 2, 3 }) &&
                      roundTripped.ImportedMeshCutMasks != null &&
                      roundTripped.ImportedMeshCutMasks.TryGetValue(importedCutKey, out var rtImp) &&
                      MeshCutEditorCore.DecodeCutMask(rtImp).SetEquals(new[] { 0, 1 }),
                    "WorkspaceCustomModelEntry JSON round-trip preserved OriginalMeshCutMasks and ImportedMeshCutMasks losslessly");

                // -----------------------------------------------------------------
                // 6. Phase 2 Item 1: Accessory Toggle OFF -> ON -> OFF & 8-Bone Weight Integrity
                // -----------------------------------------------------------------
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    bool vis = (cycle % 2) == 1;
                    hierarchy.CleanupGeneratedSubmeshes();
                    rig1.ContainerNode.Visible = vis;
                    foreach (var childMi in CustomModelAutoRigger.CollectMeshes(rig1.ContainerNode))
                        childMi.Visible = vis;
                    CustomModelAutoRigger.RecomputeHeroReplacementVisibility(heroRoot);
                    hierarchy.ScanHero(heroRoot);
                }
                var toggledUpper = hierarchy.Submeshes.FirstOrDefault(s => s.Mesh != null && s.SurfaceIndex == 0);
                bool toggle8BoneOk = false;
                if (toggledUpper?.Mesh?.Mesh is ArrayMesh tArr &&
                    (tArr.SurfaceGetFormat(0) & Mesh.ArrayFormat.FlagUse8BoneWeights) != 0)
                {
                    var ta = tArr.SurfaceGetArrays(0);
                    var tv = ta[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    var tb = ta[(int)Mesh.ArrayType.Bones].AsInt32Array();
                    var tw = ta[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                    if (tv.Length == 24 && tb.Length == 24 * 8 && tw.Length == 24 * 8)
                    {
                        float minSum = float.MaxValue, maxSum = float.MinValue;
                        for (int vi = 0; vi < tv.Length; vi++)
                        {
                            float wsum = 0f;
                            for (int k = 0; k < 8; k++) wsum += tw[vi * 8 + k];
                            if (wsum < minSum) minSum = wsum;
                            if (wsum > maxSum) maxSum = wsum;
                        }
                        toggle8BoneOk = minSum > 0.99f && maxSum < 1.01f;
                    }
                }
                Check(toggle8BoneOk,
                    "Repeated accessory OFF/ON/OFF toggles + CleanupGeneratedSubmeshes + ScanHero preserved 8-bone skinning weights without corruption");

                // -----------------------------------------------------------------
                // 7. Phase 2 Item 3: X-Ray CW Winding Front-Face vs Back-Face Selection & Overlay NoDepthTest
                // -----------------------------------------------------------------
                var xrayRoot = new Node3D { Name = "XRayTestRoot" };
                Root.AddChild(xrayRoot);
                var xrayMesh = new ArrayMesh();
                // Two quads: front quad at Z=+0.5 with outward normal +Z (Godot CW winding: a->b->c where (c-a)x(b-a) = +Z)
                //            back quad at Z=-0.5 with outward normal -Z
                var xVerts = new Vector3[]
                {
                    // Front quad (Z=+0.5, normal +Z): (0,0), (0,1), (1,1), (1,0) -> CW from +Z: (0,0)-(0,1)-(1,1)
                    new(-0.3f, -0.3f,  0.5f), new(-0.3f,  0.3f,  0.5f), new( 0.3f,  0.3f,  0.5f), new( 0.3f, -0.3f,  0.5f),
                    // Back quad (Z=-0.5, normal -Z): reversed CW from -Z
                    new(-0.3f, -0.3f, -0.5f), new( 0.3f, -0.3f, -0.5f), new( 0.3f,  0.3f, -0.5f), new(-0.3f,  0.3f, -0.5f)
                };
                var xNorms = new Vector3[]
                {
                    Vector3.Back, Vector3.Back, Vector3.Back, Vector3.Back,
                    Vector3.Forward, Vector3.Forward, Vector3.Forward, Vector3.Forward
                };
                var xIdx = new int[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
                var xArr = new Godot.Collections.Array();
                xArr.Resize((int)Mesh.ArrayType.Max);
                xArr[(int)Mesh.ArrayType.Vertex] = xVerts;
                xArr[(int)Mesh.ArrayType.Normal] = xNorms;
                xArr[(int)Mesh.ArrayType.Index] = xIdx;
                xrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, xArr);
                var xrayMi = new MeshInstance3D { Name = "XRayBox", Mesh = xrayMesh };
                xrayRoot.AddChild(xrayMi);

                var xraySession = MeshCutEditorCore.CaptureSurface(
                    xrayMi, 0, "XRayBox::surf::0", "XRayBox", false, xrayRoot, null);
                var xrayCam = new Camera3D { Position = new Vector3(0f, 0f, 3.0f) };
                xrayRoot.AddChild(xrayCam);
                xrayCam.LookAt(Vector3.Zero, Vector3.Up);
                await ToSignal(this, SignalName.ProcessFrame);

                // Use Brush from camera (+Z looking toward origin) to test front-face vs X-Ray culling (including with Mirror X!)
                xraySession.SelectedTriangles.Clear();
                MeshCutEditorCore.SelectByBrush(
                    xraySession, xrayCam, Vector3.Zero, xrayCam.GlobalPosition, Vector3.Forward,
                    radius: 1.5f, MeshCutSelectOp.Replace, xRay: false, mirrorX: true, restoreMode: false);
                bool frontOnlyOk = xraySession.SelectedTriangles.SetEquals(new[] { 0, 1 });

                xraySession.SelectedTriangles.Clear();
                MeshCutEditorCore.SelectByBrush(
                    xraySession, xrayCam, Vector3.Zero, xrayCam.GlobalPosition, Vector3.Forward,
                    radius: 1.5f, MeshCutSelectOp.Replace, xRay: true, mirrorX: false, restoreMode: false);
                bool xrayBothOk = xraySession.SelectedTriangles.SetEquals(new[] { 0, 1, 2, 3 });

                var matXrayOn = MeshCutEditorCore.CreateSelectionOverlayMaterial(false, xRay: true);
                var matXrayOff = MeshCutEditorCore.CreateSelectionOverlayMaterial(false, xRay: false);
                var matTargetXray = MeshCutEditorCore.CreateXRayTargetMaterial();
                Check(frontOnlyOk && xrayBothOk && matXrayOn.NoDepthTest && !matXrayOff.NoDepthTest &&
                      matTargetXray.Transparency == BaseMaterial3D.TransparencyEnum.Alpha,
                    "X-Ray OFF selected strictly front CW tris (0,1), X-Ray ON selected front+back (0..3), and overlay/target X-Ray materials configured properly");
                xrayRoot.QueueFree();

                // -----------------------------------------------------------------
                // 8. Phase 2 Item 4: Clothing / Partial-Body Auto-Rig (Semi-Body Pins + Quick Hero Transfer + JSON)
                // -----------------------------------------------------------------
                var (fullHero, fullSkel, fullSkin) = BuildFullHumanoidHeroForPartialRig();
                Root.AddChild(fullHero);
                await ToSignal(this, SignalName.ProcessFrame);

                // Build a static unrigged straight-down sweater (torso box + left/right hanging sleeves)
                Node3D MakeSweaterScene()
                {
                    var sRoot = new Node3D { Name = "SweaterScene" };
                    var sMi = new MeshInstance3D { Name = "female_sweater", Mesh = BuildStraightDownSweaterMesh() };
                    sRoot.AddChild(sMi);
                    return sRoot;
                }

                // Engine A: Semi-Body Pins (UpperBodySweater)
                var swSceneA = MakeSweaterScene();
                var optsPartialPins = new CustomModelAutoRigger.RiggingOptions
                {
                    SlotId = "acc_partial_pins",
                    AccessoryName = "PinkSweaterPins",
                    Intent = CustomModelAutoRigger.AccessoryIntent.AddAccessory,
                    Mode = CustomModelAutoRigger.ReplaceMode.PartialAccessory,
                    AccessoryBoneMode = AccessoryBoneMode.SkinToHero,
                    Method = CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig,
                    PartialRegion = CustomModelAutoRigger.PartialBodyRegion.UpperBodySweater,
                    PartialEngine = CustomModelAutoRigger.PartialBodyEngine.SemiBodyPins,
                    AutoSnapArmsToHero = true,
                    AutoSnapLegsToHero = true,
                    AutoSnapHeadToHero = true,
                    AlignedTransform = Transform3D.Identity
                };
                var estPins = CustomLandmarkWeightSolver.EstimatePartialRegionPins(
                    swSceneA, CustomModelAutoRigger.CollectMeshes(swSceneA), fullSkel, Transform3D.Identity, optsPartialPins);
                bool estDetectedHangingSleeves = estPins.TryGetValue("ShoulderL", out var pShL) &&
                                                 estPins.TryGetValue("WristL", out var pWrL) &&
                                                 pWrL.Y < pShL.Y - 0.25f;
                var resPins = CustomModelAutoRigger.RigAndBind(swSceneA, fullHero, optsPartialPins);
                swSceneA.Free();

                bool pinsWeightsClean = false;
                bool pinsLiftedSleeves = false;
                if (resPins.Success && resPins.CustomSubmeshes.Count > 0)
                {
                    var sm = resPins.CustomSubmeshes[0];
                    var sa = sm.Mesh.SurfaceGetArrays(0);
                    var sv = sa[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    var sb = sa[(int)Mesh.ArrayType.Bones].AsInt32Array();
                    var sw = sa[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                    var skinUsed = sm.Skin;
                    int armWeighted = 0, torsoWeighted = 0, forbiddenWeighted = 0;
                    float maxSleeveDistalY = float.MinValue;
                    for (int vi = 0; vi < sv.Length; vi++)
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            float w = sw[vi * 4 + k];
                            if (w < 0.05f) continue;
                            int bi = sb[vi * 4 + k];
                            int skIdx = (skinUsed != null && bi >= 0 && bi < skinUsed.GetBindCount())
                                ? fullSkel.FindBone(skinUsed.GetBindName(bi))
                                : bi;
                            if (skIdx < 0 || skIdx >= fullSkel.GetBoneCount()) continue;
                            string bn = fullSkel.GetBoneName(skIdx).ToLowerInvariant();
                            if (bn.Contains("leg") || bn.Contains("ankle") || bn.Contains("head") || bn.Contains("neck"))
                                forbiddenWeighted++;
                            if (bn.Contains("arm_") || bn.Contains("hand_") || bn.Contains("clavicle"))
                                armWeighted++;
                            if (bn.Contains("spine") || bn.Contains("pelvis"))
                                torsoWeighted++;
                        }
                        if (MathF.Abs(sv[vi].X) > 0.30f && sv[vi].Y > maxSleeveDistalY)
                            maxSleeveDistalY = sv[vi].Y;
                    }
                    pinsWeightsClean = forbiddenWeighted == 0 && armWeighted > 0 && torsoWeighted > 0;
                    // Original hanging cuffs were at X=+-0.26, Y=0.90; after AutoSnapArmsToHero lifts sleeves into A-Pose, distal sleeve vertices reach |X|>0.30 and Y>1.05!
                    pinsLiftedSleeves = maxSleeveDistalY > 1.05f;
                }
                Check(estDetectedHangingSleeves && resPins.Success && pinsWeightsClean && pinsLiftedSleeves,
                    $"PartialBodyAutoRig (SemiBodyPins) detected hanging sleeves, assigned 0 forbidden leg/head weights, and lifted sleeves cleanly into hero A-Pose");

                // Engine B: Quick Hero Transfer + Pose Angle Match (-35 deg arm drop)
                var swSceneB = MakeSweaterScene();
                var optsPartialTransfer = new CustomModelAutoRigger.RiggingOptions
                {
                    SlotId = "acc_partial_transfer",
                    AccessoryName = "PinkSweaterTransfer",
                    Intent = CustomModelAutoRigger.AccessoryIntent.AddAccessory,
                    Mode = CustomModelAutoRigger.ReplaceMode.PartialAccessory,
                    AccessoryBoneMode = AccessoryBoneMode.SkinToHero,
                    Method = CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig,
                    PartialRegion = CustomModelAutoRigger.PartialBodyRegion.UpperBodySweater,
                    PartialEngine = CustomModelAutoRigger.PartialBodyEngine.HeroBodyTransfer,
                    CustomArmAngleDeg = -35f,
                    SearchRadius = 0.25f,
                    SmoothingIterations = 2,
                    AlignedTransform = Transform3D.Identity
                };
                var resTransfer = CustomModelAutoRigger.RigAndBind(swSceneB, fullHero, optsPartialTransfer);
                swSceneB.Free();

                bool transferWeightsClean = false;
                if (resTransfer.Success && resTransfer.CustomSubmeshes.Count > 0)
                {
                    var sm = resTransfer.CustomSubmeshes[0];
                    var sa = sm.Mesh.SurfaceGetArrays(0);
                    var sb = sa[(int)Mesh.ArrayType.Bones].AsInt32Array();
                    var sw = sa[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                    var skinUsed = sm.Skin;
                    int armOrTorso = 0, forbidden = 0;
                    int vc = sa[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length;
                    for (int vi = 0; vi < vc; vi++)
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            float w = sw[vi * 4 + k];
                            if (w < 0.05f) continue;
                            int bi = sb[vi * 4 + k];
                            int skIdx = (skinUsed != null && bi >= 0 && bi < skinUsed.GetBindCount())
                                ? fullSkel.FindBone(skinUsed.GetBindName(bi))
                                : bi;
                            if (skIdx < 0 || skIdx >= fullSkel.GetBoneCount()) continue;
                            string bn = fullSkel.GetBoneName(skIdx).ToLowerInvariant();
                            if (bn.Contains("leg") || bn.Contains("ankle") || bn.Contains("head") || bn.Contains("neck"))
                                forbidden++;
                            else if (bn.Contains("arm_") || bn.Contains("hand_") || bn.Contains("clavicle") || bn.Contains("spine") || bn.Contains("pelvis"))
                                armOrTorso++;
                        }
                    }
                    transferWeightsClean = forbidden == 0 && armOrTorso > 0;
                }
                Check(resTransfer.Success && transferWeightsClean,
                    "PartialBodyAutoRig (HeroBodyTransfer + -35 deg Arm Drop) bound sweater with 0 forbidden leg/head weights");

                // Workspace JSON Round-Trip for PartialBodyAutoRig options
                var partialWs = WorkspaceCustomModelEntry.FromRiggingOptions("female_sweater.glb", "Sweater", new CustomModelAutoRigger.RiggingOptions
                {
                    Method = CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig,
                    PartialRegion = CustomModelAutoRigger.PartialBodyRegion.LowerBodyPants,
                    PartialEngine = CustomModelAutoRigger.PartialBodyEngine.HeroBodyTransfer,
                    PartialIncludeSpine = false,
                    PartialIncludeArms = false,
                    PartialIncludeHands = false,
                    PartialIncludeLegs = true,
                    PartialIncludeHead = false
                });
                var partialRt = JsonSerializer.Deserialize<WorkspaceCustomModelEntry>(JsonSerializer.Serialize(partialWs))?.ToRiggingOptions();
                Check(partialRt != null &&
                      partialRt.Method == CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig &&
                      partialRt.PartialRegion == CustomModelAutoRigger.PartialBodyRegion.LowerBodyPants &&
                      partialRt.PartialEngine == CustomModelAutoRigger.PartialBodyEngine.HeroBodyTransfer &&
                      !partialRt.PartialIncludeSpine && partialRt.PartialIncludeLegs,
                    "WorkspaceCustomModelEntry JSON round-trip preserved PartialBodyAutoRig region, engine, and custom toggles");

                // -----------------------------------------------------------------
                // 9. Phase 3 Follow-Up Checks: Overlay Isolation, Side-View 3D Mirror X,
                //    Imported Mesh Bottom Flare + Sculpt Deltas, and Cancel Skin Isolation
                // -----------------------------------------------------------------
                // Check A (Issue #1): CollectMeshes strictly skips IsMeshCutOverlay nodes
                var overlayProbeRoot = new Node3D { Name = "OverlayProbe" };
                var overlayMi = new MeshInstance3D { Name = "MeshCutSelectedOverlay3D", Mesh = new BoxMesh() };
                overlayMi.SetMeta("IsMeshCutOverlay", true);
                overlayProbeRoot.AddChild(overlayMi);
                var realChildMi = new MeshInstance3D { Name = "RealMesh", Mesh = new BoxMesh() };
                overlayProbeRoot.AddChild(realChildMi);
                var collectedMeshes = CustomModelAutoRigger.CollectMeshes(overlayProbeRoot);
                Check(collectedMeshes.Count == 1 && ReferenceEquals(collectedMeshes[0], realChildMi),
                    "CollectMeshes skipped IsMeshCutOverlay so Cut/Undo isolation never hides yellow selection overlays");
                overlayProbeRoot.Free();

                // Check B (Issue #3): Side-profile camera Mirror X mirrors +X selection onto -X side cleanly!
                var mirRoot = new Node3D { Name = "MirrorSideTestRoot" };
                Root.AddChild(mirRoot);
                var mirSweaterMi = new MeshInstance3D { Name = "SymSweater", Mesh = BuildStraightDownSweaterMesh() };
                mirRoot.AddChild(mirSweaterMi);
                var mirSideCam = new Camera3D { Position = new Vector3(2.5f, 1.15f, 0.0f) };
                mirRoot.AddChild(mirSideCam);
                mirSideCam.LookAt(new Vector3(0f, 1.15f, 0f), Vector3.Up);
                await ToSignal(this, SignalName.ProcessFrame);

                var mirSession = MeshCutEditorCore.CaptureSurface(mirSweaterMi, 0, "SymSweater::surf::0", "SymSweater", false, mirRoot, null);
                MeshCutEditorCore.UpdatePosedGeometry(mirSession);
                bool mirBrushed = MeshCutEditorCore.SelectByBrush(
                    mirSession, mirSideCam,
                    hitPointWorld: new Vector3(0.24f, 1.15f, 0.0f),
                    rayOrigin: mirSideCam.GlobalPosition,
                    rayDir: Vector3.Left,
                    radius: 0.12f,
                    MeshCutSelectOp.Replace,
                    xRay: true,
                    mirrorX: true,
                    restoreMode: false);
                bool hasPosX = mirSession.SelectedTriangles.Any(t => mirSession.TriCentroidsHero[t].X > 0.15f);
                bool hasNegX = mirSession.SelectedTriangles.Any(t => mirSession.TriCentroidsHero[t].X < -0.15f);
                Check(mirBrushed && hasPosX && hasNegX,
                    $"Camera-independent 3D Mirror X selected both +X and -X sleeves from side-profile view ({mirSession.SelectedTriangles.Count} tris)");
                mirRoot.QueueFree();

                // Check C (Issue #5): Imported Mesh Bottom Flare (+50%) + Sparse Sculpt Deltas + Restore + JSON Round-Trip
                var sculptScene = MakeSweaterScene();
                var sculptMi = CustomModelAutoRigger.CollectMeshes(sculptScene)[0];
                var origArr = sculptMi.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                float origBotMaxX = origArr.Where(v => v.Y < 0.98f && MathF.Abs(v.X) < 0.16f).Max(v => MathF.Abs(v.X));
                float origTopMaxX = origArr.Where(v => v.Y > 1.42f && MathF.Abs(v.X) < 0.16f).Max(v => MathF.Abs(v.X));

                string sculptSurfKey = CustomModelAutoRigger.MakeSurfaceKeepKey("female_sweater", 0);
                var sculptOpts = new CustomModelAutoRigger.RiggingOptions
                {
                    ImportedMeshBottomFlare = 0.50f,
                    ImportedMeshTopFlare = 0.0f,
                    ImportedMeshInflateMeters = 0.01f,
                    ImportedMeshSculptDeltas = new Dictionary<string, Dictionary<int, Vector3>>(StringComparer.OrdinalIgnoreCase)
                    {
                        [sculptSurfKey] = new Dictionary<int, Vector3> { [0] = new Vector3(0.05f, 0f, 0f) }
                    }
                };
                var restoreSculpt = CustomModelAutoRigger.ApplyImportedMeshSculptToScene(sculptScene, sculptOpts);
                var flaredArr = sculptMi.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                float flaredBotMaxX = flaredArr.Where(v => v.Y < 0.98f && MathF.Abs(v.X) < 0.30f).Max(v => MathF.Abs(v.X));
                float flaredTopNonV0MaxX = flaredArr.Skip(1).Where(v => v.Y > 1.42f && MathF.Abs(v.X) < 0.20f).Max(v => MathF.Abs(v.X));
                bool v0Moved = (flaredArr[0].X - origArr[0].X) > 0.05f;
                restoreSculpt?.Invoke();
                var restoredArr = sculptMi.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                bool restoredExact = restoredArr[0].DistanceTo(origArr[0]) < 1e-6f;
                sculptScene.Free();

                var scWs = WorkspaceCustomModelEntry.FromRiggingOptions("sweater.glb", "Sweater", sculptOpts);
                var scRt = JsonSerializer.Deserialize<WorkspaceCustomModelEntry>(JsonSerializer.Serialize(scWs))?.ToRiggingOptions();
                bool scJsonOk = scRt != null &&
                                MathF.Abs(scRt.ImportedMeshBottomFlare - 0.50f) < 1e-4f &&
                                MathF.Abs(scRt.ImportedMeshInflateMeters - 0.01f) < 1e-4f &&
                                scRt.ImportedMeshSculptDeltas != null &&
                                scRt.ImportedMeshSculptDeltas.TryGetValue(sculptSurfKey, out var rtMap) &&
                                rtMap.TryGetValue(0, out var rtV0) && rtV0.DistanceTo(new Vector3(0.05f, 0f, 0f)) < 1e-4f;
                Check(flaredBotMaxX > origBotMaxX * 1.40f && MathF.Abs(flaredTopNonV0MaxX - (origTopMaxX + 0.01f)) < 0.01f && v0Moved && restoredExact && scJsonOk,
                    $"ImportedMesh BottomFlare (+50% widened hem {origBotMaxX:F3}->{flaredBotMaxX:F3}m), sculpt delta, restore, and JSON round-trip verified");

                // Check D (Issue #6): Preview clone duplicates Skin & metadata so cancelling modal NEVER mutates live hero Skin!
                var liveHeroMi = CustomModelAutoRigger.CollectOriginalHeroMeshes(fullHero)[0];
                liveHeroMi.SetMeta("TestDictMeta", new Godot.Collections.Dictionary { ["k"] = 1 });
                var clonedHero = ImporterMemoryGuard.CloneHierarchySharingResources(fullHero);
                var clonedHeroMi = CustomModelAutoRigger.CollectOriginalHeroMeshes(clonedHero)[0];
                bool skinIsolated = liveHeroMi.Skin != null && clonedHeroMi.Skin != null && !ReferenceEquals(liveHeroMi.Skin, clonedHeroMi.Skin);
                var clonedDict = clonedHeroMi.GetMeta("TestDictMeta").AsGodotDictionary();
                clonedDict["k"] = 999;
                bool metaIsolated = liveHeroMi.GetMeta("TestDictMeta").AsGodotDictionary()["k"].AsInt32() == 1;
                clonedHero.Free();
                Check(skinIsolated && metaIsolated,
                    "CloneHierarchySharingResources isolated Skin and Dictionary metadata so modal preview/cancel never corrupts live vanilla hero");

                // Check E (Phase 4): Semi-Body Legs (wide-stance pants), CoatDress, Neck-only collar, Feet toggle & JSON round-trip
                var pantsScene = new Node3D { Name = "WideStancePants" };
                var pantsMi = new MeshInstance3D { Name = "pants_mesh", Mesh = BuildWideStancePantsMesh() };
                pantsScene.AddChild(pantsMi);
                var pantsOpts = new CustomModelAutoRigger.RiggingOptions
                {
                    SlotId = "acc_pants_verify",
                    Method = CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig,
                    PartialRegion = CustomModelAutoRigger.PartialBodyRegion.LowerBodyPants,
                    PartialEngine = CustomModelAutoRigger.PartialBodyEngine.SemiBodyPins,
                    PartialIncludeLegs = true,
                    PartialIncludeFeet = false,
                    PartialIncludeNeck = false,
                    Mode = CustomModelAutoRigger.ReplaceMode.PartialAccessory,
                    AccessoryBoneMode = AccessoryBoneMode.SkinToHero,
                    AutoSnapArmsToHero = false,
                    AutoSnapLegsToHero = true,
                    AutoSnapHeadToHero = false,
                    AlignedTransform = Transform3D.Identity
                };
                var pantsPins = CustomLandmarkWeightSolver.EstimatePartialRegionPins(
                    pantsScene, new[] { pantsMi }, fullSkel, Transform3D.Identity, pantsOpts);
                bool wideAnkleDetected = pantsPins.TryGetValue("AnkleL", out var pAnkL) &&
                                         pantsPins.TryGetValue("AnkleR", out var pAnkR) &&
                                         pAnkL.X > 0.22f && pAnkR.X < -0.22f;
                var pantsRes = CustomModelAutoRigger.RigAndBind(pantsScene, fullHero, pantsOpts);
                bool pantsClean = pantsRes.Success && pantsRes.ContainerNode != null;
                bool pantsZeroForbidden = true;
                bool pantsHasLegWeights = false;
                if (pantsClean)
                {
                    var pMeshes = CustomModelAutoRigger.CollectMeshes(pantsRes.ContainerNode);
                    foreach (var pm in pMeshes)
                    {
                        var pArr = pm.Mesh.SurfaceGetArrays(0);
                        var pB = CustomModelAutoRigger.GetInt32Array(pArr[(int)Mesh.ArrayType.Bones]);
                        var pW = CustomModelAutoRigger.GetFloat32Array(pArr[(int)Mesh.ArrayType.Weights]);
                        for (int k = 0; k < pB.Length; k++)
                        {
                            if (pW[k] <= 0.01f) continue;
                            int bi = pB[k];
                            string bn = pm.Skin != null && bi >= 0 && bi < pm.Skin.GetBindCount()
                                ? pm.Skin.GetBindName(bi).ToString()
                                : (bi >= 0 && bi < fullSkel.GetBoneCount() ? fullSkel.GetBoneName(bi) : "");
                            if (bn.Contains("arm", StringComparison.OrdinalIgnoreCase) ||
                                bn.Contains("hand", StringComparison.OrdinalIgnoreCase) ||
                                bn.Contains("head", StringComparison.OrdinalIgnoreCase) ||
                                bn.Contains("toe", StringComparison.OrdinalIgnoreCase))
                                pantsZeroForbidden = false;
                            if (bn.Contains("leg_upper", StringComparison.OrdinalIgnoreCase) ||
                                bn.Contains("leg_lower", StringComparison.OrdinalIgnoreCase))
                                pantsHasLegWeights = true;
                        }
                    }
                    pantsRes.ContainerNode.Free();
                }
                pantsScene.Free();

                // Verify CoatDress + Neck-only (without Head) mask & .dpp JSON round-trip for PartialIncludeFeet/Neck
                var coatOpts = new CustomModelAutoRigger.RiggingOptions
                {
                    Method = CustomModelAutoRigger.RiggingMethod.PartialBodyAutoRig,
                    PartialRegion = CustomModelAutoRigger.PartialBodyRegion.CoatDress,
                    PartialIncludeSpine = true,
                    PartialIncludeLegs = true,
                    PartialIncludeFeet = true,
                    PartialIncludeNeck = true,
                    PartialIncludeHead = false
                };
                var coatMask = CustomModelAutoRigger.GetPartialBodyRigMask(coatOpts);
                bool coatMaskOk = coatMask.IncludeSpine && coatMask.IncludeLegs && coatMask.IncludeFeet &&
                                  coatMask.IncludeNeck && !coatMask.IncludeHead &&
                                  CustomModelAutoRigger.IsBoneInPartialRegion("neck_0", coatOpts) &&
                                  !CustomModelAutoRigger.IsBoneInPartialRegion("head", coatOpts) &&
                                  CustomModelAutoRigger.IsBoneInPartialRegion("toe_L", coatOpts);
                var coatWs = WorkspaceCustomModelEntry.FromRiggingOptions("coat.glb", "Coat", coatOpts);
                var coatRt = JsonSerializer.Deserialize<WorkspaceCustomModelEntry>(JsonSerializer.Serialize(coatWs))?.ToRiggingOptions();
                bool coatJsonOk = coatRt != null &&
                                  coatRt.PartialRegion == CustomModelAutoRigger.PartialBodyRegion.CoatDress &&
                                  coatRt.PartialIncludeFeet && coatRt.PartialIncludeNeck && !coatRt.PartialIncludeHead;
                Check(wideAnkleDetected && pantsClean && pantsZeroForbidden && pantsHasLegWeights && coatMaskOk && coatJsonOk,
                    "Semi-Body Auto-Rig Legs (wide-stance pants pin detection, 0 forbidden arm/head/toe weights), CoatDress, Neck-only collar, and Feet/Neck JSON round-trip verified");

                // Check F (UX Polish): Hidden vanilla hero mesh on an accessory slot stays hidden after RigAndBind + RecomputeHeroReplacementVisibility!
                var hideAccScene = new Node3D { Name = "HideTestAcc" };
                hideAccScene.AddChild(new MeshInstance3D { Name = "acc_hat", Mesh = BuildStraightDownSweaterMesh() });
                var hideAccOpts = new CustomModelAutoRigger.RiggingOptions
                {
                    SlotId = "acc_hide_verify",
                    Method = CustomModelAutoRigger.RiggingMethod.RigidSingleBone,
                    RigidBoneName = "head",
                    Mode = CustomModelAutoRigger.ReplaceMode.PartialAccessory,
                    Intent = CustomModelAutoRigger.AccessoryIntent.ReplacePart,
                    ExplicitReplacedTargets = new List<string> { "hero_body" },
                    IsPreviewOnly = false,
                    AlignedTransform = Transform3D.Identity
                };
                var hideRes = CustomModelAutoRigger.RigAndBind(hideAccScene, fullHero, hideAccOpts);
                var vanillaBodyAfter = CustomModelAutoRigger.CollectOriginalHeroMeshes(fullHero)[0];
                bool stayedHiddenAfterConfirm = hideRes.Success && !vanillaBodyAfter.Visible &&
                                                vanillaBodyAfter.HasMeta("UserHiddenByCustomModel") &&
                                                vanillaBodyAfter.GetMeta("UserHiddenByCustomModel").AsBool();
                hideRes.ContainerNode?.Free();
                hideAccScene.Free();
                Check(stayedHiddenAfterConfirm,
                    "Hiding a vanilla hero mesh on an accessory slot persists through RigAndBind & RecomputeHeroReplacementVisibility (never reappears on Confirm)");

                fullHero.QueueFree();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[EXCEPTION] {ex}");
                _allPassed = false;
            }

            GD.Print("=======================================================");
            GD.Print(_allPassed ? "  ALL MESH CUT VERIFICATION CHECKS PASSED!" : "  SOME MESH CUT CHECKS FAILED!");
            GD.Print("=======================================================");
            Quit(_allPassed ? 0 : 1);
        }

        private static (Node3D Root, Skeleton3D Skel, Skin Skin) BuildFullHumanoidHeroForPartialRig()
        {
            var root = new Node3D { Name = "PartialRigHero" };
            var skel = new Skeleton3D { Name = "Skeleton3D" };
            root.AddChild(skel);

            var boneDefs = new (string Name, string Parent, Vector3 Pos)[]
            {
                ("pelvis", null, new(0f, 0.95f, 0f)),
                ("spine_1", "pelvis", new(0f, 1.08f, 0f)),
                ("spine_2", "spine_1", new(0f, 1.22f, 0f)),
                ("spine_3", "spine_2", new(0f, 1.36f, 0f)),
                ("neck_0", "spine_3", new(0f, 1.48f, 0f)),
                ("head", "neck_0", new(0f, 1.58f, 0f)),
                ("clavicle_L", "spine_3", new(0.06f, 1.42f, 0f)),
                ("arm_upper_L", "clavicle_L", new(0.18f, 1.40f, 0f)),
                ("arm_lower_L", "arm_upper_L", new(0.38f, 1.24f, 0f)),
                ("hand_L", "arm_lower_L", new(0.56f, 1.08f, 0f)),
                ("clavicle_R", "spine_3", new(-0.06f, 1.42f, 0f)),
                ("arm_upper_R", "clavicle_R", new(-0.18f, 1.40f, 0f)),
                ("arm_lower_R", "arm_upper_R", new(-0.38f, 1.24f, 0f)),
                ("hand_R", "arm_lower_R", new(-0.56f, 1.08f, 0f)),
                ("leg_upper_L", "pelvis", new(0.10f, 0.90f, 0f)),
                ("leg_lower_L", "leg_upper_L", new(0.10f, 0.48f, 0f)),
                ("ankle_L", "leg_lower_L", new(0.10f, 0.08f, 0f)),
                ("leg_upper_R", "pelvis", new(-0.10f, 0.90f, 0f)),
                ("leg_lower_R", "leg_upper_R", new(-0.10f, 0.48f, 0f)),
                ("ankle_R", "leg_lower_R", new(-0.10f, 0.08f, 0f))
            };

            var byName = new Dictionary<string, int>();
            var globalPos = new Vector3[boneDefs.Length];
            for (int i = 0; i < boneDefs.Length; i++)
            {
                skel.AddBone(boneDefs[i].Name);
                byName[boneDefs[i].Name] = i;
                globalPos[i] = boneDefs[i].Pos;
            }
            var skin = new Skin();
            for (int i = 0; i < boneDefs.Length; i++)
            {
                int p = boneDefs[i].Parent != null ? byName[boneDefs[i].Parent] : -1;
                if (p >= 0) skel.SetBoneParent(i, p);
                Vector3 local = p >= 0 ? globalPos[i] - globalPos[p] : globalPos[i];
                skel.SetBoneRest(i, new Transform3D(Basis.Identity, local));
                skin.AddNamedBind(boneDefs[i].Name, new Transform3D(Basis.Identity, -globalPos[i]));
            }
            skel.ResetBonePoses();

            // Build a minimal skinned body mesh (boxes along torso, arms, legs, head) as donor surface
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var bones = new List<int>();
            var weights = new List<float>();
            var indices = new List<int>();
            void AddBoneBox(int boneIdx, Vector3 center, Vector3 half)
            {
                int v0 = verts.Count;
                Vector3[] corners =
                {
                    center + new Vector3(-half.X, -half.Y,  half.Z), center + new Vector3(-half.X,  half.Y,  half.Z),
                    center + new Vector3( half.X,  half.Y,  half.Z), center + new Vector3( half.X, -half.Y,  half.Z),
                    center + new Vector3(-half.X, -half.Y, -half.Z), center + new Vector3( half.X, -half.Y, -half.Z),
                    center + new Vector3( half.X,  half.Y, -half.Z), center + new Vector3(-half.X,  half.Y, -half.Z)
                };
                foreach (var c in corners)
                {
                    verts.Add(c);
                    norms.Add((c - center).Normalized());
                    bones.Add(boneIdx); bones.Add(0); bones.Add(0); bones.Add(0);
                    weights.Add(1f); weights.Add(0f); weights.Add(0f); weights.Add(0f);
                }
                int[] boxTris = { 0,1,2, 0,2,3, 4,5,6, 4,6,7, 0,4,7, 0,7,1, 3,2,6, 3,6,5, 1,7,6, 1,6,2, 0,3,5, 0,5,4 };
                foreach (int ti in boxTris) indices.Add(v0 + ti);
            }
            AddBoneBox(byName["pelvis"], new(0f, 0.96f, 0f), new(0.14f, 0.08f, 0.10f));
            AddBoneBox(byName["spine_2"], new(0f, 1.24f, 0f), new(0.15f, 0.16f, 0.11f));
            AddBoneBox(byName["head"], new(0f, 1.62f, 0f), new(0.09f, 0.10f, 0.09f));
            AddBoneBox(byName["arm_upper_L"], new(0.28f, 1.32f, 0f), new(0.10f, 0.06f, 0.06f));
            AddBoneBox(byName["arm_lower_L"], new(0.47f, 1.16f, 0f), new(0.10f, 0.05f, 0.05f));
            AddBoneBox(byName["arm_upper_R"], new(-0.28f, 1.32f, 0f), new(0.10f, 0.06f, 0.06f));
            AddBoneBox(byName["arm_lower_R"], new(-0.47f, 1.16f, 0f), new(0.10f, 0.05f, 0.05f));
            AddBoneBox(byName["leg_upper_L"], new(0.10f, 0.69f, 0f), new(0.07f, 0.21f, 0.07f));
            AddBoneBox(byName["leg_upper_R"], new(-0.10f, 0.69f, 0f), new(0.07f, 0.21f, 0.07f));

            var arr = new Godot.Collections.Array();
            arr.Resize((int)Mesh.ArrayType.Max);
            arr[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
            arr[(int)Mesh.ArrayType.Normal] = norms.ToArray();
            arr[(int)Mesh.ArrayType.Bones] = bones.ToArray();
            arr[(int)Mesh.ArrayType.Weights] = weights.ToArray();
            arr[(int)Mesh.ArrayType.Index] = indices.ToArray();
            var am = new ArrayMesh();
            am.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
            var mi = new MeshInstance3D { Name = "hero_body", Mesh = am, Skin = skin };
            skel.AddChild(mi);
            mi.Skeleton = new NodePath("..");
            return (root, skel, skin);
        }

        private static ArrayMesh BuildStraightDownSweaterMesh()
        {
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var indices = new List<int>();

            void AddTube(Vector3 topCenter, Vector3 botCenter, float radX, float radZ, int segs, int rings)
            {
                int baseV = verts.Count;
                for (int r = 0; r <= rings; r++)
                {
                    float t = r / (float)rings;
                    Vector3 c = topCenter.Lerp(botCenter, t);
                    for (int s = 0; s < segs; s++)
                    {
                        float ang = (s / (float)segs) * MathF.Tau;
                        Vector3 d = new Vector3(MathF.Cos(ang) * radX, 0f, MathF.Sin(ang) * radZ);
                        verts.Add(c + d);
                        norms.Add(d.Normalized());
                        uvs.Add(new Vector2(s / (float)segs, t));
                    }
                }
                for (int r = 0; r < rings; r++)
                {
                    for (int s = 0; s < segs; s++)
                    {
                        int sNext = (s + 1) % segs;
                        int a = baseV + r * segs + s;
                        int b = baseV + r * segs + sNext;
                        int c = baseV + (r + 1) * segs + sNext;
                        int d = baseV + (r + 1) * segs + s;
                        indices.Add(a); indices.Add(b); indices.Add(c);
                        indices.Add(a); indices.Add(c); indices.Add(d);
                    }
                }
            }

            // Torso cylinder (waist Y=0.95 to collar Y=1.45, radius X=0.145, Z=0.11)
            AddTube(new(0f, 1.45f, 0f), new(0f, 0.95f, 0f), 0.145f, 0.11f, 16, 8);
            // Left straight-down hanging sleeve (shoulder X=+0.21, Y=1.40 down to cuff X=+0.26, Y=0.90)
            AddTube(new(0.21f, 1.40f, 0f), new(0.26f, 0.90f, 0f), 0.055f, 0.055f, 12, 8);
            // Right straight-down hanging sleeve (shoulder X=-0.21, Y=1.40 down to cuff X=-0.26, Y=0.90)
            AddTube(new(-0.21f, 1.40f, 0f), new(-0.26f, 0.90f, 0f), 0.055f, 0.055f, 12, 8);

            var arr = new Godot.Collections.Array();
            arr.Resize((int)Mesh.ArrayType.Max);
            arr[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
            arr[(int)Mesh.ArrayType.Normal] = norms.ToArray();
            arr[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
            arr[(int)Mesh.ArrayType.Index] = indices.ToArray();
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
            return mesh;
        }

        private static ArrayMesh BuildWideStancePantsMesh()
        {
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var indices = new List<int>();

            void AddTube(Vector3 topCenter, Vector3 botCenter, float radX, float radZ, int segs, int rings)
            {
                int baseV = verts.Count;
                for (int r = 0; r <= rings; r++)
                {
                    float t = r / (float)rings;
                    Vector3 c = topCenter.Lerp(botCenter, t);
                    for (int s = 0; s < segs; s++)
                    {
                        float ang = (s / (float)segs) * MathF.Tau;
                        Vector3 d = new Vector3(MathF.Cos(ang) * radX, 0f, MathF.Sin(ang) * radZ);
                        verts.Add(c + d);
                        norms.Add(d.Normalized());
                        uvs.Add(new Vector2(s / (float)segs, t));
                    }
                }
                for (int r = 0; r < rings; r++)
                {
                    for (int s = 0; s < segs; s++)
                    {
                        int sNext = (s + 1) % segs;
                        int a = baseV + r * segs + s;
                        int b = baseV + r * segs + sNext;
                        int c = baseV + (r + 1) * segs + sNext;
                        int d = baseV + (r + 1) * segs + s;
                        indices.Add(a); indices.Add(b); indices.Add(c);
                        indices.Add(a); indices.Add(c); indices.Add(d);
                    }
                }
            }

            // Waist/pelvis band (Y=0.98 down to 0.85)
            AddTube(new(0f, 0.98f, 0f), new(0f, 0.85f, 0f), 0.15f, 0.11f, 16, 4);
            // Wide-stance left leg tube (hip X=+0.11, Y=0.88 down to ankle X=+0.28, Y=0.10)
            AddTube(new(0.11f, 0.88f, 0f), new(0.28f, 0.10f, 0f), 0.065f, 0.065f, 12, 8);
            // Wide-stance right leg tube (hip X=-0.11, Y=0.88 down to ankle X=-0.28, Y=0.10)
            AddTube(new(-0.11f, 0.88f, 0f), new(-0.28f, 0.10f, 0f), 0.065f, 0.065f, 12, 8);

            var arr = new Godot.Collections.Array();
            arr.Resize((int)Mesh.ArrayType.Max);
            arr[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
            arr[(int)Mesh.ArrayType.Normal] = norms.ToArray();
            arr[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
            arr[(int)Mesh.ArrayType.Index] = indices.ToArray();
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
            return mesh;
        }
    }
}

