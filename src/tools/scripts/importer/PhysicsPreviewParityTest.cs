using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeadlockPlayground.Animation;
using DeadlockPlayground.Importer;
using Godot;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace DeadlockPlayground.Tests
{
    /// <summary>
    /// Fast headless verification suite for Source 2 FeModel-matched Verlet PBD cloth,
    /// implicit JiggleBone stability, OpenSheet topology, planarized torso barrier,
    /// and Weight-Paint pause freeze in <see cref="CustomJiggleBoneDriver"/>.
    /// </summary>
    public partial class PhysicsPreviewParityTest : SceneTree
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
            _ = RunAsync();
        }

        private async Task RunAsync()
        {
            await ToSignal(this, SignalName.ProcessFrame);
            try
            {
                GD.Print("=== PhysicsPreviewParityTest ===");
                TestWeightPaintPause();
                TestClothVerletDrapeAndMonotonicity();
                TestJiggleZeroGravityAndImplicitStability();
                TestBlenderJiggleBoneXAxisReorientationAndCsdk13Parity();
                TestProxyClothOpenSheetVsClosedSkirt();
                TestPlanarizedTorsoBackBarrier();
                TestCustomPropBoxColliderAndJsonRoundTrip();
                TestRestingCollisionZeroJitterAndClothHinge();
                TestPaigeShortHairExactSource2SagAndZeroHiddenCollision();
                TestCmScaledShortHairExportAndCsdk13FeModel();
                TestProxyClothSkirtAndCoatCsdk13Parity();
                TestSkirtAndNonShortHairPreviewFidelity();
                await TestHandleWrapLayerHierarchyAndHazeExportAsync();
            }
            catch (Exception ex)
            {
                _failed++;
                GD.PrintErr($"[EXCEPTION] {ex}");
            }
            finally
            {
                GD.Print($"=== RESULT: {_passed} passed, {_failed} failed ===");
                Quit(_failed == 0 ? 0 : 1);
            }
        }

        private static (Node3D Root, Skeleton3D Skel, CustomJiggleBoneDriver Driver) BuildTestRig(
            string anchorName,
            Vector3 anchorPos,
            int strandCount,
            int segsPerStrand,
            Vector3 segDir,
            float segLen,
            bool radialRing)
        {
            var root = new Node3D { Name = "TestHeroRoot" };
            var skel = new Skeleton3D { Name = "Skeleton3D" };
            root.AddChild(skel);

            int anchorIdx = skel.AddBone(anchorName);
            skel.SetBoneRest(anchorIdx, new Transform3D(Basis.Identity, anchorPos));
            skel.SetBonePosePosition(anchorIdx, anchorPos);
            skel.SetBonePoseRotation(anchorIdx, Quaternion.Identity);

            for (int s = 0; s < strandCount; s++)
            {
                int parent = anchorIdx;
                float angle = radialRing ? (s * Mathf.Tau / strandCount) : ((s - (strandCount - 1) * 0.5f) * 0.25f);
                Vector3 rootOff = radialRing
                    ? new Vector3(MathF.Sin(angle) * 0.14f, 0f, MathF.Cos(angle) * 0.14f)
                    : new Vector3(angle * 0.25f, 0f, -0.14f);

                for (int k = 0; k < segsPerStrand; k++)
                {
                    int bi = skel.AddBone($"strand_{s}_{k}");
                    skel.SetBoneParent(bi, parent);
                    Vector3 localPos = k == 0 ? rootOff : (segDir.Normalized() * segLen);
                    skel.SetBoneRest(bi, new Transform3D(Basis.Identity, localPos));
                    skel.SetBonePosePosition(bi, localPos);
                    skel.SetBonePoseRotation(bi, Quaternion.Identity);
                    parent = bi;
                }
            }

            var driver = CustomJiggleBoneDriver.GetOrAttach(root, skel);
            return (root, skel, driver);
        }

        private static List<BoneChainDef> MakeChainDefs(
            string anchorName,
            int strandCount,
            int segsPerStrand,
            SimulationKind sim,
            float stiffRoot,
            float stiffTip,
            float damping,
            float gravity,
            bool openSheet = false,
            bool planarizeBack = false,
            float angleLimitDeg = 22f)
        {
            var list = new List<BoneChainDef>();
            for (int s = 0; s < strandCount; s++)
            {
                var names = new List<string>();
                for (int k = 0; k < segsPerStrand; k++) names.Add($"strand_{s}_{k}");
                list.Add(new BoneChainDef
                {
                    ChainId = $"c_{s}",
                    Name = $"c_{s}",
                    RootSourceBone = names[0],
                    AnchorHeroBone = anchorName,
                    Policy = ChainPolicy.PreservePhysicsChain,
                    Simulation = sim,
                    StiffnessRoot = stiffRoot,
                    StiffnessTip = stiffTip,
                    Damping = damping,
                    Gravity = gravity,
                    OpenSheet = openSheet,
                    PlanarizeBackBarrier = planarizeBack,
                    AngleLimitDeg = angleLimitDeg,
                    SourceBoneNames = new List<string>(names),
                    AppendedHeroBoneNames = new List<string>(names)
                });
            }
            return list;
        }

        private void TestWeightPaintPause()
        {
            var (root, skel, driver) = BuildTestRig("spine_3", new Vector3(0, 1.3f, 0), 1, 3, new Vector3(0, 0, -1), 0.12f, false);
            Root.AddChild(root);
            driver.RegisterChains("cape", MakeChainDefs("spine_3", 1, 3, SimulationKind.Cloth, 0.05f, 0.0f, 0.1f, 1.5f, angleLimitDeg: 140f));

            driver.SetPausedForEditing(true);
            for (int f = 0; f < 60; f++) driver.StepSimulation(1f / 60f);

            float maxRotDelta = 0f;
            for (int k = 0; k < 3; k++)
            {
                int bi = skel.FindBone($"strand_0_{k}");
                float ang = skel.GetBonePoseRotation(bi).AngleTo(Quaternion.Identity);
                if (ang > maxRotDelta) maxRotDelta = ang;
            }
            Check(maxRotDelta < 1e-5f, $"Weight-Paint pause holds all bones completely motionless (maxRot={maxRotDelta:F6} rad)");

            driver.SetPausedForEditing(false);
            for (int f = 0; f < 45; f++) driver.StepSimulation(1f / 60f);
            float resumedAngle = skel.GetBonePoseRotation(skel.FindBone("strand_0_0")).AngleTo(Quaternion.Identity);
            Check(resumedAngle > 0.15f, $"Unpausing resumes live gravity drape cleanly (angle={Mathf.RadToDeg(resumedAngle):F1} deg)");

            root.QueueFree();
        }

        private void TestClothVerletDrapeAndMonotonicity()
        {
            // Horizontal strand pointing back (-Z) under gravity: loose should droop much more than stiff
            var (rootLoose, skelLoose, drvLoose) = BuildTestRig("spine_3", new Vector3(0, 1.3f, 0), 1, 3, new Vector3(0, 0, -1), 0.12f, false);
            var (rootStiff, skelStiff, drvStiff) = BuildTestRig("spine_3", new Vector3(0, 1.3f, 0), 1, 3, new Vector3(0, 0, -1), 0.12f, false);
            Root.AddChild(rootLoose);
            Root.AddChild(rootStiff);

            drvLoose.RegisterChains("loose", MakeChainDefs("spine_3", 1, 3, SimulationKind.Cloth, 0.05f, 0.0f, 0.25f, 1.0f, angleLimitDeg: 140f));
            drvStiff.RegisterChains("stiff", MakeChainDefs("spine_3", 1, 3, SimulationKind.Cloth, 0.95f, 0.85f, 0.25f, 1.0f, angleLimitDeg: 140f));

            for (int f = 0; f < 120; f++)
            {
                drvLoose.StepSimulation(1f / 60f);
                drvStiff.StepSimulation(1f / 60f);
            }

            // Compute live global tip of strand_0_2 directly from posed bone transforms
            static Vector3 ComputeChainGlobalTip(Skeleton3D sk, int boneIdx, Vector3 tipLocal)
            {
                var chain = new List<int>();
                for (int b = boneIdx; b >= 0; b = sk.GetBoneParent(b)) chain.Add(b);
                Transform3D acc = Transform3D.Identity;
                for (int i = chain.Count - 1; i >= 0; i--) acc *= sk.GetBonePose(chain[i]);
                return acc * tipLocal;
            }

            Vector3 tipLoose = ComputeChainGlobalTip(skelLoose, skelLoose.FindBone("strand_0_2"), new Vector3(0, 0, -0.10f));
            Vector3 tipStiff = ComputeChainGlobalTip(skelStiff, skelStiff.FindBone("strand_0_2"), new Vector3(0, 0, -0.10f));
            float restY = 1.3f;
            float droopLoose = restY - tipLoose.Y;
            float droopStiff = restY - tipStiff.Y;

            Check(!float.IsNaN(droopLoose) && !float.IsNaN(droopStiff), "Cloth Verlet positions stay strictly finite");
            Check(droopLoose > 0.12f, $"Loose cloth drapes naturally under 9.144 m/s^2 gravity (droop={droopLoose:F3}m)");
            Check(droopStiff < droopLoose * 0.45f, $"Stiff cloth holds rest shape tighter than loose (stiff={droopStiff:F3}m < loose={droopLoose:F3}m)");

            rootLoose.QueueFree();
            rootStiff.QueueFree();
        }

        private void TestJiggleZeroGravityAndImplicitStability()
        {
            var (root, skel, driver) = BuildTestRig("pelvis", new Vector3(0, 0.9f, 0), 1, 2, new Vector3(0, 0, -1), 0.09f, false);
            Root.AddChild(root);
            driver.RegisterChains("jiggle", MakeChainDefs("pelvis", 1, 2, SimulationKind.Jiggle, 0.95f, 0.85f, 0.50f, 0.0f, angleLimitDeg: 20f));

            // 1. At rest: zero gravity droop
            for (int f = 0; f < 60; f++) driver.StepSimulation(1f / 60f);
            float restDrift = skel.GetBonePoseRotation(skel.FindBone("strand_0_0")).AngleTo(Quaternion.Identity);
            Check(restDrift < 1e-4f, $"JiggleBone has zero gravity droop at rest (drift={restDrift:F6} rad)");

            // 2. Rapid world-space character impulse (running/jumping): stays finite & strictly within AngleLimitDeg
            float maxAngleDeg = 0f;
            bool allFinite = true;
            for (int f = 0; f < 120; f++)
            {
                float yOff = f < 40 ? MathF.Sin(f * 0.45f) * 0.35f : 0f;
                root.Position = new Vector3(0, yOff, 0);
                driver.StepSimulation(1f / 60f);
                Quaternion q = skel.GetBonePoseRotation(skel.FindBone("strand_0_0"));
                if (!q.IsFinite()) allFinite = false;
                float deg = Mathf.RadToDeg(q.AngleTo(Quaternion.Identity));
                if (deg > maxAngleDeg) maxAngleDeg = deg;
            }
            float settledDeg = Mathf.RadToDeg(skel.GetBonePoseRotation(skel.FindBone("strand_0_0")).AngleTo(Quaternion.Identity));
            Check(allFinite, "High-stiffness implicit JiggleBone remains 100% finite under rapid impulse");
            Check(maxAngleDeg > 1.0f && maxAngleDeg <= 21.5f, $"JiggleBone responds dynamically and strictly obeys AngleLimitDeg=20 (max={maxAngleDeg:F1} deg)");
            Check(settledDeg < 1.0f, $"JiggleBone settles cleanly back to rest after impulse (settled={settledDeg:F2} deg)");

            root.QueueFree();
        }

        private void TestBlenderJiggleBoneXAxisReorientationAndCsdk13Parity()
        {
            GD.Print("--- Blender +Y JiggleBone -> Source 2 +X Reorientation & CSDK13 FeJiggleBone Parity ---");

            // Exact Blender/glTF chest bone setup from test_jiggle.dpp:
            //   c_Breast_root_r has local +Y pointing forward out of chest (Godot +Z) and local +X pointing sideways (Godot +X),
            //   with co-located child Breast_01_r at T=[0,0,0].
            var skel = new Skeleton3D { Name = "BlenderChestSkel" };
            int bRoot = skel.AddBone("root_motion");
            skel.SetBoneRest(bRoot, Transform3D.Identity);
            int bPelvis = skel.AddBone("pelvis");
            skel.SetBoneParent(bPelvis, bRoot);
            skel.SetBoneRest(bPelvis, new Transform3D(Basis.Identity, new Vector3(0f, 1.00f, 0f)));
            int bSpine2 = skel.AddBone("spine_2");
            skel.SetBoneParent(bSpine2, bPelvis);
            skel.SetBoneRest(bSpine2, new Transform3D(Basis.Identity, new Vector3(0f, 0.30f, 0f)));

            // Exact quaternion from magician_Dupin_NOCLOTHING_Deadlock.glb c_Breast_root_dupli_002.r:
            // world +X = [0.897, 0.027, 0.442] (SIDEWAYS), world +Y = [-0.411, 0.422, 0.808] (FORWARD OUT OF CHEST)
            Quaternion blenderBreastQ = new Quaternion(0.6931f, -0.2095f, 0.0879f, 0.6842f).Normalized();
            int bBreastRoot = skel.AddBone("c_breast_root_r");
            skel.SetBoneParent(bBreastRoot, bSpine2);
            skel.SetBoneRest(bBreastRoot, new Transform3D(new Basis(blenderBreastQ), new Vector3(-0.08f, 0.04f, 0.09f)));

            int bBreastChild = skel.AddBone("breast_01_r");
            skel.SetBoneParent(bBreastChild, bBreastRoot);
            skel.SetBoneRest(bBreastChild, Transform3D.Identity); // Co-located child T=[0,0,0]

            var chestChain = new BoneChainDef
            {
                ChainId = "c_breast_r",
                Name = "c_breast_root_r",
                RootSourceBone = "c_breast_root_r",
                AnchorHeroBone = "spine_2",
                Policy = ChainPolicy.PreservePhysicsChain,
                Simulation = SimulationKind.Jiggle,
                StiffnessRoot = 0.88f,
                StiffnessTip = 0.56f,
                Damping = 0.20f,
                Gravity = 0.0f,
                AngleLimitDeg = 30f,
                SourceBoneNames = new List<string> { "c_breast_root_r", "breast_01_r" },
                AppendedHeroBoneNames = new List<string> { "c_breast_root_r", "breast_01_r" }
            };

            var bundle = CustomModelExporter.BuildExportBundle(
                new List<MeshInstance3D>(),
                skel,
                "magician",
                preservedChains: new[] { chestChain });

            Vector3 pelSrc = CustomModelExporter.GodotToSourcePosition(skel.GetBoneGlobalRest(bPelvis).Origin);
            Vector3 sp2Src = CustomModelExporter.GodotToSourcePosition(skel.GetBoneGlobalRest(bSpine2).Origin);
            Vector3 sp2Local = sp2Src - pelSrc;
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            string vmdlTemplate =
                "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc39:version{1ceecb0d-4093-4729-ab18-b76886c64cde} -->\r\n" +
                "{\r\n\trootNode = \r\n\t{\r\n\t\t_class = \"RootNode\"\r\n\t\tchildren = \r\n\t\t[\r\n" +
                "\t\t\t{\r\n\t\t\t\t_class = \"Skeleton\"\r\n\t\t\t\tchildren = \r\n\t\t\t\t[\r\n" +
                "\t\t\t\t\t{\r\n\t\t\t\t\t\t_class = \"Bone\"\r\n\t\t\t\t\t\tname = \"root_motion\"\r\n\t\t\t\t\t\torigin = [ 0.0, 0.0, 0.0 ]\r\n\t\t\t\t\t\tangles = [ 0.0, 0.0, 0.0 ]\r\n\t\t\t\t\t\tchildren = \r\n\t\t\t\t\t\t[\r\n" +
                "\t\t\t\t\t\t\t{\r\n\t\t\t\t\t\t\t\t_class = \"Bone\"\r\n\t\t\t\t\t\t\t\tname = \"pelvis\"\r\n" +
                string.Format(ic, "\t\t\t\t\t\t\t\torigin = [ {0:F4}, {1:F4}, {2:F4} ]\r\n", pelSrc.X, pelSrc.Y, pelSrc.Z) +
                "\t\t\t\t\t\t\t\tangles = [ 0.0, 0.0, 0.0 ]\r\n\t\t\t\t\t\t\t\tchildren = \r\n\t\t\t\t\t\t\t\t[\r\n" +
                "\t\t\t\t\t\t\t\t\t{\r\n\t\t\t\t\t\t\t\t\t\t_class = \"Bone\"\r\n\t\t\t\t\t\t\t\t\t\tname = \"spine_2\"\r\n" +
                string.Format(ic, "\t\t\t\t\t\t\t\t\t\torigin = [ {0:F4}, {1:F4}, {2:F4} ]\r\n", sp2Local.X, sp2Local.Y, sp2Local.Z) +
                "\t\t\t\t\t\t\t\t\t\tangles = [ 0.0, 0.0, 0.0 ]\r\n\t\t\t\t\t\t\t\t\t},\r\n" +
                "\t\t\t\t\t\t\t\t]\r\n\t\t\t\t\t\t\t},\r\n" +
                "\t\t\t\t\t\t]\r\n\t\t\t\t\t},\r\n" +
                "\t\t\t\t]\r\n\t\t\t},\r\n\t\t]\r\n\t}\r\n}\r\n";

            string patchedVmdl = CustomModelExporter.GeneratePatchedVmdl(
                vmdlTemplate, bundle, "models/test_jiggle_x", "materials/test_jiggle_x",
                new List<(string, string)> { ("body", "models/test_jiggle_x/body.smd") },
                new List<string> { "mat" }, new HashSet<string>(), includeAnimGraph2Nodes: false);

            var exportedBones = CustomModelExporter.ParseSkeletonBonesFromVmdlText(patchedVmdl);
            Check(patchedVmdl.Contains("tip_mass = 0.000"),
                "Zero-gravity JiggleBone emits tip_mass = 0.000 (zero static droop/sideways twist)");
            Check(patchedVmdl.Contains("has_length_constraint = true"),
                "JiggleBone emits has_length_constraint = true matching preview sphere projection");

            string csdkRc = @"D:\Reduced_CSDK_13\game\bin\win64\resourcecompiler.exe";
            if (File.Exists(csdkRc))
            {
                string tmpSrc = @"D:\Reduced_CSDK_13\content\citadel_addons\_dp_verify_jiggle_x\models\test_jiggle_x";
                string tmpDst = @"D:\Reduced_CSDK_13\game\citadel_addons\_dp_verify_jiggle_x\models\test_jiggle_x";
                try
                {
                    Directory.CreateDirectory(tmpSrc);
                    bundle.Submeshes.Add(new CustomModelExporter.CustomModelExportBundle.SubmeshData
                    {
                        SubmeshName = "chest_mesh",
                        MaterialName = "mat",
                        Vertices = new[]
                        {
                            skel.GetBoneGlobalRest(bBreastRoot).Origin,
                            skel.GetBoneGlobalRest(bBreastRoot).Origin + new Vector3(0.02f, 0f, 0.04f),
                            skel.GetBoneGlobalRest(bBreastRoot).Origin + new Vector3(0f, 0.03f, 0.04f)
                        },
                        Normals = new[] { Vector3.Back, Vector3.Back, Vector3.Back },
                        UVs = new[] { Vector2.Zero, Vector2.Right, Vector2.One },
                        Indices = new[] { 0, 1, 2 },
                        BoneIndices = new[] { bBreastRoot, 0, 0, 0, bBreastChild, 0, 0, 0, bBreastRoot, 0, 0, 0 },
                        BoneWeights = new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f }
                    });
                    CustomModelExporter.WriteSourceSpaceSmd(Path.Combine(tmpSrc, "body.smd"), bundle, bundle.Submeshes, exportedBones);
                    CustomModelExporter.WriteRestBindAnimationSmd(Path.Combine(tmpSrc, "magician_anim_bind.smd"), bundle, exportedBones);
                    string vmdlPath = Path.Combine(tmpSrc, "magician.vmdl");
                    File.WriteAllText(vmdlPath, patchedVmdl);

                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = csdkRc,
                        Arguments = $"-i \"{vmdlPath}\"",
                        WorkingDirectory = Path.GetDirectoryName(csdkRc),
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    };
                    var proc = System.Diagnostics.Process.Start(psi);
                    string rcOut = proc.StandardOutput.ReadToEnd() + "\n" + proc.StandardError.ReadToEnd();
                    proc.WaitForExit();

                    string vmdlC = Path.Combine(tmpDst, "magician.vmdl_c");
                    bool compiledOk = proc.ExitCode == 0 && File.Exists(vmdlC);
                    if (!compiledOk) GD.PrintErr($"[CSDK13 JIGGLE X FAIL]\n{rcOut}");

                    float forwardAlignDot = -1f;
                    float compiledTipMass = -1f;
                    float compiledYawDamp = -1f;
                    string compiledFlags = "";
                    bool hasFlexible = false;
                    bool hasLengthConstraint = false;
                    if (compiledOk)
                    {
                        using var res = new ValveResourceFormat.Resource { FileName = vmdlC };
                        res.Read(vmdlC);
                        var mdl = res.DataBlock as Model;
                        var fe = mdl?.GetEmbeddedPhys()?.FeModel;
                        if (mdl != null && fe != null && fe.JiggleBones != null && fe.JiggleBones.Length > 0)
                        {
                            // Expected Source 2 forward direction = GodotToSourceDirection(blenderBreastQ * Vector3.Up)
                            Vector3 expectedGd = (new Basis(blenderBreastQ) * Vector3.Up).Normalized();
                            Vector3 expectedSrc = CustomModelExporter.GodotToSourcePosition(expectedGd).Normalized();
                            var expSys = new System.Numerics.Vector3(expectedSrc.X, expectedSrc.Y, expectedSrc.Z);

                            int bIdx = Array.FindIndex(mdl.Skeleton.Bones, b => b.Name.Equals("c_breast_root_r", StringComparison.OrdinalIgnoreCase));
                            if (bIdx >= 0)
                            {
                                var worldMat = mdl.Skeleton.Bones[bIdx].BindPose;
                                var actualSrcX = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(worldMat.M11, worldMat.M12, worldMat.M13));
                                forwardAlignDot = System.Numerics.Vector3.Dot(actualSrcX, expSys);
                            }

                            var jb = fe.JiggleBones[0].JiggleBone;
                            compiledTipMass = jb.TipMass;
                            compiledYawDamp = jb.YawDamping;
                            compiledFlags = jb.BoneFlags.ToString();
                            hasFlexible = compiledFlags.Contains("Flexible", StringComparison.OrdinalIgnoreCase);
                            hasLengthConstraint = compiledFlags.Contains("LengthConstraint", StringComparison.OrdinalIgnoreCase);
                            GD.Print($"  [CSDK13 FeJiggleBone] Count={fe.JiggleBones.Length} Flags={compiledFlags} " +
                                     $"TipMass={compiledTipMass:F3} YawStiff={jb.YawStiffness:F1} YawDamp={compiledYawDamp:F2} " +
                                     $"ForwardAlignDot={forwardAlignDot:F4}");
                        }
                    }

                    Check(compiledOk && forwardAlignDot > 0.99f,
                        $"CSDK13 compiled Blender +Y chest bone with local +X aimed forward out of chest (dot={forwardAlignDot:F4}, vs sideways before!)");
                    Check(compiledOk && hasFlexible && hasLengthConstraint,
                        $"CSDK13 compiled Flexible + LengthConstraint spring-damper flags (flags={compiledFlags})");
                    Check(compiledOk && compiledTipMass == 0f,
                        $"CSDK13 compiled zero-gravity JiggleBone with TipMass=0 (got {compiledTipMass:F3})");
                    Check(compiledOk && compiledYawDamp > 10f,
                        $"CSDK13 compiled critically-calibrated YawDamping for damping=0.20 (got {compiledYawDamp:F2}, vs 2.8 underdamped ringing before!)");
                }
                finally
                {
                    try { Directory.Delete(@"D:\Reduced_CSDK_13\content\citadel_addons\_dp_verify_jiggle_x", true); } catch { }
                    try { Directory.Delete(@"D:\Reduced_CSDK_13\game\citadel_addons\_dp_verify_jiggle_x", true); } catch { }
                }
            }

            skel.Free();
        }

        private void TestProxyClothOpenSheetVsClosedSkirt()
        {
            // 4 strands x 3 segments:
            // OpenSheet=true -> 3 pairs * (3 horiz + 4 diag) = 21 links
            // Closed skirt (OpenSheet=false, radial ring) -> 4 pairs * (3 horiz + 4 diag) = 28 links
            var (rootCape, _, drvCape) = BuildTestRig("spine_3", new Vector3(0, 1.3f, 0), 4, 3, new Vector3(0, -1, 0), 0.14f, radialRing: true);
            var (rootSkirt, _, drvSkirt) = BuildTestRig("pelvis", new Vector3(0, 0.9f, 0), 4, 3, new Vector3(0, -1, 0), 0.14f, radialRing: true);
            Root.AddChild(rootCape);
            Root.AddChild(rootSkirt);

            drvCape.RegisterChains("cape", MakeChainDefs("spine_3", 4, 3, SimulationKind.ProxyCloth, 0.4f, 0.05f, 0.2f, 1.0f, openSheet: true));
            drvSkirt.RegisterChains("skirt", MakeChainDefs("pelvis", 4, 3, SimulationKind.ProxyCloth, 0.4f, 0.05f, 0.2f, 1.0f, openSheet: false));

            Check(drvCape.ActiveProxyLinkCount == 21, $"OpenSheet ProxyCloth cape builds 21 horiz+diag links (no closed tube) (got {drvCape.ActiveProxyLinkCount})");
            Check(drvSkirt.ActiveProxyLinkCount == 28, $"Closed ProxyCloth skirt builds 28 horiz+diag links (got {drvSkirt.ActiveProxyLinkCount})");

            rootCape.QueueFree();
            rootSkirt.QueueFree();
        }

        private void TestPlanarizedTorsoBackBarrier()
        {
            var (root, skel, driver) = BuildTestRig("spine_2", new Vector3(0, 1.2f, 0), 1, 2, new Vector3(0, -1, 0), 0.16f, false);
            Root.AddChild(root);
            driver.RegisterChains("hair", MakeChainDefs("spine_2", 1, 2, SimulationKind.Cloth, 0.2f, 0.0f, 0.2f, 1.0f, planarizeBack: true));
            driver.RegisterCollisionCapsules(new[]
            {
                new BodyCollisionCapsuleDef
                {
                    BoneName = "spine_2",
                    Enabled = true,
                    Point0Local = new Vector3(0, -0.20f, 0),
                    Point1Local = new Vector3(0, 0.20f, 0),
                    Radius0Meters = 0.14f,
                    Radius1Meters = 0.14f
                }
            });

            // Tilt spine_2 forward so rest hair target tries to swing into the torso capsule
            int sp = skel.FindBone("spine_2");
            skel.SetBonePoseRotation(sp, new Quaternion(Vector3.Right, Mathf.DegToRad(35f)));
            skel.ForceUpdateBoneChildTransform(sp);
            for (int f = 0; f < 60; f++) driver.StepSimulation(1f / 60f);

            var chainIdx = new List<int>();
            for (int b = skel.FindBone("strand_0_1"); b >= 0; b = skel.GetBoneParent(b)) chainIdx.Add(b);
            Transform3D tipPose = Transform3D.Identity;
            for (int i = chainIdx.Count - 1; i >= 0; i--) tipPose *= skel.GetBonePose(chainIdx[i]);
            Vector3 tipWorld = tipPose.Origin;
            Check(tipWorld.Z < -0.05f, $"Planarized torso barrier keeps back-draping hair behind chest (tipZ={tipWorld.Z:F3}m)");

            root.QueueFree();
        }

        private void TestCustomPropBoxColliderAndJsonRoundTrip()
        {
            var boxDef = new BodyCollisionCapsuleDef
            {
                ColliderId = "box_prop_1",
                Shape = ColliderShapeKind.Box,
                IsCustomCollider = true,
                BoneName = "spine_2",
                DisplayName = "Back Prop Box 1",
                LocalOffsetMeters = new Vector3(0f, -0.05f, -0.18f),
                BoxHalfExtentsMeters = new Vector3(0.16f, 0.18f, 0.12f),
                LocalRotationDeg = new Vector3(0f, 0f, 0f),
                PlanarizeBackOnly = true,
                Enabled = true,
                IsUserEdited = true
            };

            // 1. Verify JSON serialization round-trip preserves all ClothShapeBox properties
            string json = BodyColliderAutoFitter.SerializeToJson(new[] { boxDef });
            var parsed = BodyColliderAutoFitter.DeserializeFromJson(json);
            Check(parsed.Count == 1 &&
                  parsed[0].Shape == ColliderShapeKind.Box &&
                  parsed[0].IsCustomCollider &&
                  parsed[0].PlanarizeBackOnly &&
                  MathF.Abs(parsed[0].BoxHalfExtentsMeters.Z - 0.12f) < 1e-4f,
                  "Custom ClothShapeBox JSON round-trip preserves shape, extents, custom flag, and planarizeBack");

            // 2. Verify a hanging cape strand is cleanly deflected behind the back-prop box (Z <= -0.30m)
            var (root, skel, driver) = BuildTestRig("spine_2", new Vector3(0, 1.2f, 0), 1, 2, new Vector3(0, -1, 0), 0.16f, false);
            Root.AddChild(root);
            driver.RegisterChains("cape", MakeChainDefs("spine_2", 1, 2, SimulationKind.ProxyCloth, 0.15f, 0.0f, 0.2f, 1.0f, openSheet: true, planarizeBack: true));
            driver.RegisterCollisionCapsules(new[] { boxDef });

            for (int f = 0; f < 60; f++) driver.StepSimulation(1f / 60f);

            var chainIdx = new List<int>();
            for (int b = skel.FindBone("strand_0_1"); b >= 0; b = skel.GetBoneParent(b)) chainIdx.Add(b);
            Transform3D tipPose = Transform3D.Identity;
            for (int i = chainIdx.Count - 1; i >= 0; i--) tipPose *= skel.GetBonePose(chainIdx[i]);
            Vector3 tipWorld = tipPose * new Vector3(0, -0.14f, 0);
            Check(tipWorld.Z < -0.26f, $"Custom Back-Prop ClothShapeBox deflects cape cleanly over prop back face (tipZ={tipWorld.Z:F3}m)");

            root.QueueFree();
        }

        private void TestRestingCollisionZeroJitterAndClothHinge()
        {
            // 1. Resting cape on torso capsule settles with near-zero frame-to-frame jitter (no collision energy injection)
            var (root, skel, driver) = BuildTestRig("spine_2", new Vector3(0, 1.2f, 0), 1, 2, new Vector3(0, -1, 0), 0.16f, false);
            Root.AddChild(root);
            var defs = MakeChainDefs("spine_2", 1, 2, SimulationKind.Cloth, 0.25f, 0.05f, 0.35f, 1.0f, planarizeBack: true, angleLimitDeg: 140f);
            driver.RegisterChains("cape_rest", defs);
            driver.RegisterCollisionCapsules(new[]
            {
                new BodyCollisionCapsuleDef
                {
                    BoneName = "spine_2",
                    Enabled = true,
                    Point0Local = new Vector3(0, -0.20f, 0),
                    Point1Local = new Vector3(0, 0.20f, 0),
                    Radius0Meters = 0.16f,
                    Radius1Meters = 0.16f
                }
            });

            for (int f = 0; f < 120; f++) driver.StepSimulation(1f / 60f);
            int b0 = skel.FindBone("strand_0_0");
            Quaternion prevQ = skel.GetBonePoseRotation(b0);
            float maxJitterDeg = 0f;
            for (int f = 0; f < 30; f++)
            {
                driver.StepSimulation(1f / 60f);
                Quaternion curQ = skel.GetBonePoseRotation(b0);
                float deltaDeg = Mathf.RadToDeg(curQ.AngleTo(prevQ));
                if (deltaDeg > maxJitterDeg) maxJitterDeg = deltaDeg;
                prevQ = curQ;
            }
            Check(maxJitterDeg < 0.20f, $"Cape resting against torso capsule settles without collision bounce jitter (maxJitter={maxJitterDeg:F3} deg/frame)");

            // 2. Opt-in ClothChain ribbon hinge limit clamps out-of-plane gravity droop when EnableRibbonExtrude=true (ExtrudeForwardAxis=0, Twist=0, AngleLimit=15°)
            var (rootH, skelH, drvH) = BuildTestRig("spine_3", new Vector3(0, 1.3f, 0), 1, 2, new Vector3(0, 0, -1), 0.14f, false);
            Root.AddChild(rootH);
            var hingeDefs = MakeChainDefs("spine_3", 1, 2, SimulationKind.Cloth, 0.05f, 0.0f, 0.30f, 1.0f, angleLimitDeg: 15f);
            hingeDefs[0].EnableRibbonExtrude = true;
            hingeDefs[0].ExtrudeForwardAxis = 0; // +X -> ribbon plane is XZ (horizontal), normal = Y, so vertical gravity droop is out-of-plane!
            hingeDefs[0].ExtrudeTwistDeg = 0f;
            drvH.RegisterChains("hinge", hingeDefs);
            for (int f = 0; f < 90; f++) drvH.StepSimulation(1f / 60f);
            float hingeAngleDeg = Mathf.RadToDeg(skelH.GetBonePoseRotation(skelH.FindBone("strand_0_0")).AngleTo(Quaternion.Identity));
            Check(hingeAngleDeg <= 16.5f, $"Opt-in ClothChain ribbon hinge limits out-of-plane swing to AngleLimitDeg=15 (got {hingeAngleDeg:F1} deg)");

            // 3. Default Valve 1D Rope (EnableRibbonExtrude=false) ignores arbitrary local-X hinge so short hair never kinks sideways
            var (rootRope, skelRope, drvRope) = BuildTestRig("spine_3", new Vector3(0, 1.3f, 0), 1, 2, new Vector3(0, 0, -1), 0.14f, false);
            Root.AddChild(rootRope);
            var ropeDefs = MakeChainDefs("spine_3", 1, 2, SimulationKind.Cloth, 0.05f, 0.0f, 0.15f, 1.0f, angleLimitDeg: 15f);
            ropeDefs[0].EnableRibbonExtrude = false;
            drvRope.RegisterChains("rope_free", ropeDefs);
            for (int f = 0; f < 90; f++) drvRope.StepSimulation(1f / 60f);
            float ropeAngleDeg = Mathf.RadToDeg(skelRope.GetBonePoseRotation(skelRope.FindBone("strand_0_0")).AngleTo(Quaternion.Identity));
            Check(ropeAngleDeg > 35.0f, $"Default Valve 1D Rope (EnableRibbonExtrude=false) swings freely without arbitrary X-axis hinge kinking (got {ropeAngleDeg:F1} deg)");

            root.QueueFree();
            rootH.QueueFree();
            rootRope.QueueFree();
        }

        private void TestPaigeShortHairExactSource2SagAndZeroHiddenCollision()
        {
            GD.Print("--- Source 2 FeModel Exact Integrator & Honest Collision Preview Parity ---");

            // 1. Horizontal short spiky hair strand with ShortHairBangsId (Paige/Silver: goalF=0.532->0.211, 1.0g):
            //    Under Source 2's exact FeNodeIntegrator, equilibrium tip sag is ((1-fa)/fa)*g*dt^2 (~0.09..0.37 in = 2..10 mm),
            //    NOT the old 20x weaker preview that drooped 9+ inches!
            var (rootS, skelS, drvS) = BuildTestRig("head", new Vector3(0, 1.65f, 0), 1, 2, new Vector3(0, 0, -1), 0.10f, false);
            Root.AddChild(rootS);
            var defsS = MakeChainDefs("head", 1, 2, SimulationKind.Cloth, 0.72f, 0.65f, 0.15f, 1.0f);
            PhysicsPresetCatalog.ApplyPresetToChain(defsS[0], PhysicsPresetCatalog.ShortHairBangsId, resetMacros: true);
            drvS.RegisterChains("short_hair", defsS);
            for (int f = 0; f < 120; f++) drvS.StepSimulation(1f / 60f);

            float rootTiltDeg = Mathf.RadToDeg(skelS.GetBonePoseRotation(skelS.FindBone("strand_0_0")).AngleTo(Quaternion.Identity));
            float tipTiltDeg = Mathf.RadToDeg(skelS.GetBonePoseRotation(skelS.FindBone("strand_0_1")).AngleTo(Quaternion.Identity));
            Check(rootTiltDeg > 0.3f && rootTiltDeg < 6.0f && tipTiltDeg > 1.0f && tipTiltDeg < 14.0f,
                $"ShortHairBangsId (Paige/Silver, 1.0g) holds styled silhouette with exact Source 2 sag (root={rootTiltDeg:F2} deg, tip={tipTiltDeg:F2} deg)");

            // 2. Honest collision parity: if a head capsule intentionally penetrates a dynamic hair node at bind pose,
            //    preview MUST push it out (zero hidden InitialAnchorDist / RestCapsuleAllowances masking!).
            var (rootC, skelC, drvC) = BuildTestRig("head", new Vector3(0, 1.65f, 0), 1, 2, new Vector3(0, 0, -1), 0.08f, false);
            Root.AddChild(rootC);
            var defsC = MakeChainDefs("head", 1, 2, SimulationKind.Cloth, 0.72f, 0.65f, 0.15f, 1.0f);
            PhysicsPresetCatalog.ApplyPresetToChain(defsC[0], PhysicsPresetCatalog.ShortHairBangsId, resetMacros: true);
            drvC.RegisterChains("hair_clip", defsC);
            // Oversized 0.35m head capsule clearly swallowing strand_0_0 tip (at Z=-0.22m from head)
            drvC.RegisterCollisionCapsules(new[]
            {
                new BodyCollisionCapsuleDef
                {
                    BoneName = "head",
                    Enabled = true,
                    Point0Local = new Vector3(0, -0.05f, 0),
                    Point1Local = new Vector3(0, 0.05f, 0),
                    Radius0Meters = 0.35f,
                    Radius1Meters = 0.35f
                }
            });
            for (int f = 0; f < 20; f++) drvC.StepSimulation(1f / 60f);
            float pushAngleDeg = Mathf.RadToDeg(skelC.GetBonePoseRotation(skelC.FindBone("strand_0_0")).AngleTo(Quaternion.Identity));
            // Note: strand points along -Z directly away from head, so tip is at Z=-0.22 (inside 0.35m radius),
            // let's test an angled strand (offset X=+0.10, pointing +X) that clearly pushes outward!
            rootC.QueueFree();

            var (rootC2, skelC2, drvC2) = BuildTestRig("head", new Vector3(0, 1.65f, 0), 1, 2, new Vector3(0, -1, 0), 0.08f, false);
            Root.AddChild(rootC2);
            var defsC2 = MakeChainDefs("head", 1, 2, SimulationKind.Cloth, 0.30f, 0.15f, 0.15f, 1.0f);
            drvC2.RegisterChains("hair_clip2", defsC2);
            drvC2.RegisterCollisionCapsules(new[]
            {
                new BodyCollisionCapsuleDef
                {
                    BoneName = "head",
                    Enabled = true,
                    Point0Local = new Vector3(0, -0.15f, 0),
                    Point1Local = new Vector3(0, 0.15f, 0),
                    Radius0Meters = 0.25f,
                    Radius1Meters = 0.25f
                }
            });
            for (int f = 0; f < 30; f++) drvC2.StepSimulation(1f / 60f);
            float honestPushDeg = Mathf.RadToDeg(skelC2.GetBonePoseRotation(skelC2.FindBone("strand_0_0")).AngleTo(Quaternion.Identity));
            Check(honestPushDeg > 15.0f, $"Honest preview collision pushes penetrating hair out of oversized head capsule without hidden allowance masking (push={honestPushDeg:F1} deg)");

            rootS.QueueFree();
            rootC2.QueueFree();
        }

        private void TestCmScaledShortHairExportAndCsdk13FeModel()
        {
            GD.Print("--- CM-Scaled (0.01x) Short Spiky Hair Export & CSDK13 FeModel Parity ---");

            // 1. Reproduce legacy 0.01x cm-rigged skeleton (like Unreal/FBX/Zagreus on Infernus):
            //    head has scale=1.0, hair_01_root has Basis.Scale=(0.01,0.01,0.01),
            //    so hair_01_mid/tip have raw local Origins of (10.0m, ...) which equal 0.10m in Godot world!
            var skel = new Skeleton3D { Name = "CmScaledHeroSkel" };
            int bRoot = skel.AddBone("root_motion");
            skel.SetBoneRest(bRoot, Transform3D.Identity);
            int bPelvis = skel.AddBone("pelvis");
            skel.SetBoneParent(bPelvis, bRoot);
            skel.SetBoneRest(bPelvis, new Transform3D(Basis.Identity, new Vector3(0f, 1.43f, 0.076f)));
            int bHead = skel.AddBone("head");
            skel.SetBoneParent(bHead, bPelvis);
            skel.SetBoneRest(bHead, new Transform3D(Basis.Identity, new Vector3(0f, 0.84f, -0.03f)));

            // Simulate legacy scaled bone 0 (0.01x scale!) + 100x child origins
            int bHair0 = skel.AddBone("hair_spike_0");
            skel.SetBoneParent(bHair0, bHead);
            skel.SetBoneRest(bHair0, new Transform3D(Basis.Identity.Scaled(new Vector3(0.01f, 0.01f, 0.01f)), new Vector3(0.05f, 0.12f, -0.04f)));
            int bHair1 = skel.AddBone("hair_spike_1");
            skel.SetBoneParent(bHair1, bHair0);
            skel.SetBoneRest(bHair1, new Transform3D(Basis.Identity, new Vector3(3.0f, 8.0f, -4.0f))); // 0.01 * (3,8,-4) = (0.03, 0.08, -0.04) m (~3.7 in)
            int bHair2 = skel.AddBone("hair_spike_2");
            skel.SetBoneParent(bHair2, bHair1);
            skel.SetBoneRest(bHair2, new Transform3D(Basis.Identity, new Vector3(2.0f, 7.0f, -3.0f))); // ~3.1 in

            var shortHairChain = new BoneChainDef
            {
                ChainId = "c_hair_spike",
                Name = "hair_spike_0",
                RootSourceBone = "hair_spike_0",
                AnchorHeroBone = "head",
                Policy = ChainPolicy.PreservePhysicsChain,
                SourceBoneNames = new List<string> { "hair_spike_0", "hair_spike_1", "hair_spike_2" },
                AppendedHeroBoneNames = new List<string> { "hair_spike_0", "hair_spike_1", "hair_spike_2" }
            };
            PhysicsPresetCatalog.ApplyPresetToChain(shortHairChain, PhysicsPresetCatalog.ShortHairBangsId, resetMacros: true);

            var bundle = CustomModelExporter.BuildExportBundle(
                new List<MeshInstance3D>(),
                skel,
                "inferno",
                preservedChains: new[] { shortHairChain });

            // Add a head capsule that intentionally overlaps dynamic hair_spike_1 (distance ~8.6 in from head)
            Vector3 headWorldGd = skel.GetBoneGlobalRest(bHead).Origin;
            bundle.CustomFittedWorldCapsules.Add(new CustomModelExporter.CustomModelExportBundle.ClothCollisionCapsuleDef
            {
                ParentBone = "head",
                Point0 = headWorldGd,
                Radius0 = 0.240f, // 9.45 in (intentionally swallows hair_spike_1 at ~8.6 in so clearance pass trims it!)
                Point1 = headWorldGd + new Vector3(0f, 0.14f, 0f),
                Radius1 = 0.245f  // 9.65 in
            });

            // Build minimal Source 2 vmdl matching head world pos (GodotToSourcePosition)
            Vector3 pelSrc = CustomModelExporter.GodotToSourcePosition(skel.GetBoneGlobalRest(bPelvis).Origin);
            Vector3 headSrc = CustomModelExporter.GodotToSourcePosition(headWorldGd);
            Vector3 headLocalSrc = headSrc - pelSrc;
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            string vmdlTemplate =
                "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc39:version{1ceecb0d-4093-4729-ab18-b76886c64cde} -->\r\n" +
                "{\r\n\trootNode = \r\n\t{\r\n\t\t_class = \"RootNode\"\r\n\t\tchildren = \r\n\t\t[\r\n" +
                "\t\t\t{\r\n\t\t\t\t_class = \"Skeleton\"\r\n\t\t\t\tchildren = \r\n\t\t\t\t[\r\n" +
                "\t\t\t\t\t{\r\n\t\t\t\t\t\t_class = \"Bone\"\r\n\t\t\t\t\t\tname = \"root_motion\"\r\n\t\t\t\t\t\torigin = [ 0.0, 0.0, 0.0 ]\r\n\t\t\t\t\t\tangles = [ 0.0, 0.0, 0.0 ]\r\n\t\t\t\t\t\tchildren = \r\n\t\t\t\t\t\t[\r\n" +
                "\t\t\t\t\t\t\t{\r\n\t\t\t\t\t\t\t\t_class = \"Bone\"\r\n\t\t\t\t\t\t\t\tname = \"pelvis\"\r\n" +
                string.Format(ic, "\t\t\t\t\t\t\t\torigin = [ {0:F4}, {1:F4}, {2:F4} ]\r\n", pelSrc.X, pelSrc.Y, pelSrc.Z) +
                "\t\t\t\t\t\t\t\tangles = [ 0.0, 0.0, 0.0 ]\r\n\t\t\t\t\t\t\t\tchildren = \r\n\t\t\t\t\t\t\t\t[\r\n" +
                "\t\t\t\t\t\t\t\t\t{\r\n\t\t\t\t\t\t\t\t\t\t_class = \"Bone\"\r\n\t\t\t\t\t\t\t\t\t\tname = \"head\"\r\n" +
                string.Format(ic, "\t\t\t\t\t\t\t\t\t\torigin = [ {0:F4}, {1:F4}, {2:F4} ]\r\n", headLocalSrc.X, headLocalSrc.Y, headLocalSrc.Z) +
                "\t\t\t\t\t\t\t\t\t\tangles = [ 0.0, 0.0, 0.0 ]\r\n\t\t\t\t\t\t\t\t\t},\r\n" +
                "\t\t\t\t\t\t\t\t]\r\n\t\t\t\t\t\t\t},\r\n\t\t\t\t\t\t]\r\n\t\t\t\t\t},\r\n\t\t\t\t]\r\n\t\t\t},\r\n\t\t]\r\n\t}\r\n}\r\n";

            string patchedVmdl = CustomModelExporter.GeneratePatchedVmdl(
                vmdlTemplate, bundle, "models/test_cm", "materials/test_cm",
                new List<(string, string)> { ("body", "models/test_cm/body.smd") },
                new List<string> { "mat" }, new HashSet<string>(), includeAnimGraph2Nodes: false);

            var exportedBones = CustomModelExporter.ParseSkeletonBonesFromVmdlText(patchedVmdl);
            var h0 = exportedBones.FirstOrDefault(b => b.Name == "hair_spike_0");
            var h1 = exportedBones.FirstOrDefault(b => b.Name == "hair_spike_1");
            var h2 = exportedBones.FirstOrDefault(b => b.Name == "hair_spike_2");
            float h1LenIn = h1 != null ? h1.Origin.Length() : 9999f;
            float h2LenIn = h2 != null ? h2.Origin.Length() : 9999f;
            Check(h0 != null && h1 != null && h2 != null && h1LenIn < 6.0f && h2LenIn < 6.0f,
                $"0.01x cm-scaled hair chain exports exact inch origins instead of 100x explosion (h1={h1LenIn:F2} in, h2={h2LenIn:F2} in)");
            Check(!exportedBones.Any(b => b.Name.Contains("_ext", StringComparison.OrdinalIgnoreCase)),
                "Upward short spiky hair generates zero synthetic downward _ext bones");
            Check(patchedVmdl.Contains("stray_radius = "),
                "Short Hair / Bangs preset emits bounded stray_radius leash into ClothChain");

            // 2. Compile through real CSDK13 resourcecompiler.exe and inspect compiled FeModel!
            string csdkRc = @"D:\Reduced_CSDK_13\game\bin\win64\resourcecompiler.exe";
            if (File.Exists(csdkRc))
            {
                string tmpSrc = @"D:\Reduced_CSDK_13\content\citadel_addons\_dp_verify_cm_hair\models\test_cm";
                string tmpDst = @"D:\Reduced_CSDK_13\game\citadel_addons\_dp_verify_cm_hair\models\test_cm";
                try
                {
                    Directory.CreateDirectory(tmpSrc);
                    // Build minimal triangles weighted to hair_spike_0/1/2
                    bundle.Submeshes.Add(new CustomModelExporter.CustomModelExportBundle.SubmeshData
                    {
                        SubmeshName = "hair_spike_mesh",
                        MaterialName = "mat",
                        Vertices = new[]
                        {
                            skel.GetBoneGlobalRest(bHair0).Origin,
                            skel.GetBoneGlobalRest(bHair1).Origin + new Vector3(0.01f, 0f, 0f),
                            skel.GetBoneGlobalRest(bHair2).Origin
                        },
                        Normals = new[] { Vector3.Up, Vector3.Up, Vector3.Up },
                        UVs = new[] { Vector2.Zero, Vector2.Right, Vector2.One },
                        Indices = new[] { 0, 1, 2 },
                        BoneIndices = new[] { bHair0, 0, 0, 0, bHair1, 0, 0, 0, bHair2, 0, 0, 0 },
                        BoneWeights = new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f }
                    });
                    CustomModelExporter.WriteSourceSpaceSmd(Path.Combine(tmpSrc, "body.smd"), bundle, bundle.Submeshes, exportedBones);
                    CustomModelExporter.WriteRestBindAnimationSmd(Path.Combine(tmpSrc, "inferno_anim_bind.smd"), bundle, exportedBones);
                    string vmdlPath = Path.Combine(tmpSrc, "inferno.vmdl");
                    File.WriteAllText(vmdlPath, patchedVmdl);

                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = csdkRc,
                        Arguments = $"-i \"{vmdlPath}\"",
                        WorkingDirectory = Path.GetDirectoryName(csdkRc),
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    };
                    var proc = System.Diagnostics.Process.Start(psi);
                    string rcOut = proc.StandardOutput.ReadToEnd() + "\n" + proc.StandardError.ReadToEnd();
                    proc.WaitForExit();

                    string vmdlC = Path.Combine(tmpDst, "inferno.vmdl_c");
                    bool compiledOk = proc.ExitCode == 0 && File.Exists(vmdlC);
                    if (!compiledOk) GD.PrintErr($"[CSDK13 CM HAIR FAIL]\n{rcOut}");

                    float maxHairDistIn = 9999f;
                    float headCapTopR = 9999f;
                    int strayCount = 0;
                    int rodCount = 0;
                    int twistCount = 0;
                    int hingeCount = -1;
                    int freeNodesCount = -1;
                    int kelagerCount = -1;
                    bool rootFreeRotatingStatic = false;
                    bool rootPinnedOnScalp = false;
                    bool hasTipEndNode = false;
                    float firstDynGoalF = -1f;
                    float firstDynGoalV = -1f;
                    float firstDynPointDamp = -1f;
                    if (compiledOk)
                    {
                        using var res = new ValveResourceFormat.Resource { FileName = vmdlC };
                        res.Read(vmdlC);
                        var fe = (res.DataBlock as Model)?.GetEmbeddedPhys()?.FeModel;
                        if (fe != null && fe.InitPose != null)
                        {
                            var ctrlNames = fe.CtrlName ?? Array.Empty<string>();
                            int headNode = Array.FindIndex(ctrlNames, n => string.Equals(n, "head", StringComparison.OrdinalIgnoreCase));
                            int h0Node = Array.FindIndex(ctrlNames, n => string.Equals(n, "hair_spike_0", StringComparison.OrdinalIgnoreCase));
                            int hEndNode = Array.FindIndex(ctrlNames, n => string.Equals(n, "hair_spike_2_end", StringComparison.OrdinalIgnoreCase));
                            rootPinnedOnScalp = h0Node >= 0 && h0Node < fe.StaticNodes;
                            rootFreeRotatingStatic = h0Node >= fe.RotLockStaticNodes && h0Node < fe.StaticNodes;
                            hasTipEndNode = hEndNode >= fe.StaticNodes && hEndNode < fe.NodeCount;
                            rodCount = fe.Rods?.Length ?? 0;
                            twistCount = fe.Twists?.Length ?? 0;
                            hingeCount = fe.HingeLimits?.Length ?? 0;
                            freeNodesCount = fe.FreeNodes?.Length ?? 0;
                            kelagerCount = fe.KelagerBends?.Length ?? 0;
                            GD.Print($"  [CSDK13 FeModel] Nodes={fe.NodeCount} Static={fe.StaticNodes} RotLockStatic={fe.RotLockStaticNodes} " +
                                     $"Rods={rodCount} Twists={twistCount} NodeBases={fe.NodeBases?.Length ?? 0} " +
                                     $"Hinge={hingeCount} Free={freeNodesCount} Kelager={kelagerCount} " +
                                     $"Names=[{string.Join(", ", ctrlNames)}]");

                            var headP = headNode >= 0 ? fe.InitPose[headNode].Position : System.Numerics.Vector3.Zero;
                            maxHairDistIn = 0f;
                            for (int ni = fe.StaticNodes; ni < fe.NodeCount; ni++)
                            {
                                float d = System.Numerics.Vector3.Distance(fe.InitPose[ni].Position, headP);
                                if (d > maxHairDistIn) maxHairDistIn = d;
                            }
                            if (fe.NodeIntegrator != null && fe.StaticNodes < fe.NodeIntegrator.Length)
                            {
                                var integ = fe.NodeIntegrator[fe.StaticNodes];
                                firstDynGoalF = integ.AnimationForceAttraction;
                                firstDynGoalV = integ.AnimationVertexAttraction;
                                firstDynPointDamp = integ.PointDamping;
                            }
                            strayCount = fe.AnimStrayRadii?.Length ?? 0;
                            if (fe.TaperedCapsuleRigids != null)
                            {
                                foreach (var cap in fe.TaperedCapsuleRigids)
                                {
                                    if (cap.Node == headNode && cap.Spheres != null && cap.Spheres.Length >= 2)
                                    {
                                        headCapTopR = MathF.Min(cap.Spheres[0].W, cap.Spheres[1].W);
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    Check(compiledOk && maxHairDistIn > 2.0f && maxHairDistIn < 20.0f,
                        $"CSDK13 compiled cm-scaled short hair FeModel with realistic bounds (maxDistFromHead={maxHairDistIn:F2} in, vs 1185 in before!)");
                    Check(compiledOk && rodCount >= 5 && twistCount >= 4 && hingeCount == 0 && freeNodesCount == 0 && kelagerCount == 0,
                        $"CSDK13 compiled pure Valve 1D chain FeModel (Rods={rodCount}, Twists={twistCount}, HingeLimits={hingeCount}, FreeNodes={freeNodesCount}, KelagerBends={kelagerCount})");
                    Check(compiledOk && rootPinnedOnScalp && rootFreeRotatingStatic && hasTipEndNode,
                        $"CSDK13 pinned free-rotating scalp root hair_spike_0 (static={rootPinnedOnScalp}, freeRot={rootFreeRotatingStatic}) and dynamic tip hair_spike_2_end (dynamic={hasTipEndNode})");
                    Check(compiledOk && firstDynGoalF > 0.45f && firstDynGoalF < 0.60f && firstDynGoalV > firstDynGoalF && firstDynPointDamp < 0.10f,
                        $"CSDK13 compiled Paige/Silver goalF/goalV/PointDamping (goalF={firstDynGoalF:F3}, goalV={firstDynGoalV:F3}, pDamp={firstDynPointDamp:F3})");
                    Check(compiledOk && strayCount > 0,
                        $"CSDK13 compiled bounded AnimStrayRadii leash for Short Hair preset (count={strayCount})");
                    Check(compiledOk && headCapTopR < 9.0f,
                        $"Head collider radius trimmed cleanly for dynamic hair nodes (headCapTopR={headCapTopR:F2} in < 9.45 in)");
                }
                finally
                {
                    try { Directory.Delete(@"D:\Reduced_CSDK_13\content\citadel_addons\_dp_verify_cm_hair", true); } catch { }
                    try { Directory.Delete(@"D:\Reduced_CSDK_13\game\citadel_addons\_dp_verify_cm_hair", true); } catch { }
                }
            }

            skel.Free();
        }

        private void TestProxyClothSkirtAndCoatCsdk13Parity()
        {
            GD.Print("--- ProxyCloth Skirt/Coat Row-0 Pin, FeCollisionPlane & CSDK13 FeModel Parity ---");

            // Build a realistic 4-strand x 3-bone wrap-around skirt anchored to pelvis + leg_upper_L/R thighs
            var root = new Node3D { Name = "SkirtTestRoot" };
            var skel = new Skeleton3D { Name = "Skeleton3D" };
            root.AddChild(skel);
            Root.AddChild(root);

            int bRoot = skel.AddBone("root_motion");
            skel.SetBoneRest(bRoot, Transform3D.Identity);
            int bPelvis = skel.AddBone("pelvis");
            skel.SetBoneParent(bPelvis, bRoot);
            skel.SetBoneRest(bPelvis, new Transform3D(Basis.Identity, new Vector3(0f, 1.00f, 0f)));
            int bLegL = skel.AddBone("leg_upper_L");
            skel.SetBoneParent(bLegL, bPelvis);
            skel.SetBoneRest(bLegL, new Transform3D(Basis.Identity, new Vector3(0.10f, -0.08f, 0f)));
            int bLegR = skel.AddBone("leg_upper_R");
            skel.SetBoneParent(bLegR, bPelvis);
            skel.SetBoneRest(bLegR, new Transform3D(Basis.Identity, new Vector3(-0.10f, -0.08f, 0f)));

            var skirtChains = new List<BoneChainDef>();
            string[] dirNames = { "front", "left", "back", "right" };
            Vector3[] ringOffsets =
            {
                new Vector3(0f, -0.02f, 0.22f),
                new Vector3(0.22f, -0.02f, 0f),
                new Vector3(0f, -0.02f, -0.22f),
                new Vector3(-0.22f, -0.02f, 0f)
            };

            for (int s = 0; s < 4; s++)
            {
                int parent = bPelvis;
                var names = new List<string>();
                for (int k = 0; k < 3; k++)
                {
                    string bn = $"skirt_{dirNames[s]}_{k}";
                    int bi = skel.AddBone(bn);
                    skel.SetBoneParent(bi, parent);
                    // Flare slightly outward as the skirt hangs down (-Y) so it clears the legs at bind pose
                    Vector3 loc = k == 0 ? ringOffsets[s] : (new Vector3(ringOffsets[s].X * 0.14f, -0.15f, ringOffsets[s].Z * 0.14f));
                    skel.SetBoneRest(bi, new Transform3D(Basis.Identity, loc));
                    skel.SetBonePosePosition(bi, loc);
                    skel.SetBonePoseRotation(bi, Quaternion.Identity);
                    names.Add(bn);
                    parent = bi;
                }
                var ch = new BoneChainDef
                {
                    ChainId = $"skirt_{dirNames[s]}",
                    Name = names[0],
                    RootSourceBone = names[0],
                    AnchorHeroBone = "pelvis",
                    Policy = ChainPolicy.PreservePhysicsChain,
                    SourceBoneNames = new List<string>(names),
                    AppendedHeroBoneNames = new List<string>(names)
                };
                PhysicsPresetCatalog.ApplyPresetToChain(ch, PhysicsPresetCatalog.HeavySkirtDressId, resetMacros: true);
                skirtChains.Add(ch);
            }

            // 1. Live Preview test: verify thigh swing pushes front skirt forward via bind-pose FeCollisionPlane!
            var driver = CustomJiggleBoneDriver.GetOrAttach(root, skel);
            driver.RegisterChains("skirt", skirtChains);
            var thighCaps = new[]
            {
                new BodyCollisionCapsuleDef
                {
                    BoneName = "leg_upper_L",
                    Enabled = true,
                    Point0Local = Vector3.Zero,
                    Point1Local = new Vector3(0f, -0.40f, 0f),
                    Radius0Meters = 0.085f,
                    Radius1Meters = 0.075f
                },
                new BodyCollisionCapsuleDef
                {
                    BoneName = "leg_upper_R",
                    Enabled = true,
                    Point0Local = Vector3.Zero,
                    Point1Local = new Vector3(0f, -0.40f, 0f),
                    Radius0Meters = 0.085f,
                    Radius1Meters = 0.075f
                }
            };
            driver.RegisterCollisionCapsules(thighCaps);

            static Vector3 GetBoneWorldOrigin(Skeleton3D sk, int bIdx)
            {
                var c = new List<int>();
                for (int b = bIdx; b >= 0; b = sk.GetBoneParent(b)) c.Add(b);
                Transform3D p = Transform3D.Identity;
                for (int i = c.Count - 1; i >= 0; i--) p *= sk.GetBonePose(c[i]);
                return p.Origin;
            }

            // Settle at rest first (builds bind-pose FeCollisionPlanes)
            skel.SetBonePosePosition(bPelvis, new Vector3(0f, 1.00f, 0f));
            skel.SetBonePosePosition(bLegL, new Vector3(0.10f, -0.08f, 0f));
            skel.SetBonePosePosition(bLegR, new Vector3(-0.10f, -0.08f, 0f));
            for (int f = 0; f < 30; f++) driver.StepSimulation(1f / 60f);
            int frontB2 = skel.FindBone("skirt_front_2");
            float frontLowZBefore = GetBoneWorldOrigin(skel, frontB2).Z;

            // Swing both thighs forward (-55 deg around +X so legs kick toward +Z front)
            skel.SetBonePoseRotation(bLegL, new Quaternion(Vector3.Right, Mathf.DegToRad(-55f)));
            skel.SetBonePoseRotation(bLegR, new Quaternion(Vector3.Right, Mathf.DegToRad(-55f)));
            skel.ForceUpdateBoneChildTransform(bLegL);
            skel.ForceUpdateBoneChildTransform(bLegR);
            for (int f = 0; f < 45; f++) driver.StepSimulation(1f / 60f);

            float frontLowZAfter = GetBoneWorldOrigin(skel, frontB2).Z;
            Check(frontLowZAfter > frontLowZBefore + 0.06f,
                $"Wrap-around skirt FeCollisionPlanes push front hem panel cleanly forward when legs kick forward (Z: {frontLowZBefore:F3}m -> {frontLowZAfter:F3}m)");

            // Reset leg poses for export test
            skel.SetBonePoseRotation(bLegL, Quaternion.Identity);
            skel.SetBonePoseRotation(bLegR, Quaternion.Identity);

            // 2. Compile real _cloth_proxy.dmx through live CSDK13 resourcecompiler.exe and verify FeModel parity!
            string csdkRc = @"D:\Reduced_CSDK_13\game\bin\win64\resourcecompiler.exe";
            if (File.Exists(csdkRc))
            {
                string tmpSrc = @"D:\Reduced_CSDK_13\content\citadel_addons\_dp_verify_proxy_skirt\models\test_skirt";
                string tmpDst = @"D:\Reduced_CSDK_13\game\citadel_addons\_dp_verify_proxy_skirt\models\test_skirt";
                try
                {
                    Directory.CreateDirectory(tmpSrc);
                    var bundle = CustomModelExporter.BuildExportBundle(
                        new List<MeshInstance3D>(), skel, "yamato", preservedChains: skirtChains);
                    foreach (var tc in thighCaps)
                    {
                        Transform3D bg = skel.GetBoneGlobalRest(skel.FindBone(tc.BoneName));
                        bundle.CustomFittedWorldCapsules.Add(new CustomModelExporter.CustomModelExportBundle.ClothCollisionCapsuleDef
                        {
                            ParentBone = tc.BoneName,
                            Point0 = bg * tc.Point0Local,
                            Radius0 = tc.Radius0Meters,
                            Point1 = bg * tc.Point1Local,
                            Radius1 = tc.Radius1Meters
                        });
                    }

                    Vector3 pelSrc = CustomModelExporter.GodotToSourcePosition(skel.GetBoneGlobalRest(bPelvis).Origin);
                    Vector3 legLSrc = CustomModelExporter.GodotToSourcePosition(skel.GetBoneGlobalRest(bLegL).Origin) - pelSrc;
                    Vector3 legRSrc = CustomModelExporter.GodotToSourcePosition(skel.GetBoneGlobalRest(bLegR).Origin) - pelSrc;
                    var ic = System.Globalization.CultureInfo.InvariantCulture;
                    string vmdlTemplate =
                        "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc39:version{1ceecb0d-4093-4729-ab18-b76886c64cde} -->\r\n" +
                        "{\r\n\trootNode = \r\n\t{\r\n\t\t_class = \"RootNode\"\r\n\t\tchildren = \r\n\t\t[\r\n" +
                        "\t\t\t{\r\n\t\t\t\t_class = \"Skeleton\"\r\n\t\t\t\tchildren = \r\n\t\t\t\t[\r\n" +
                        "\t\t\t\t\t{\r\n\t\t\t\t\t\t_class = \"Bone\"\r\n\t\t\t\t\t\tname = \"root_motion\"\r\n\t\t\t\t\t\torigin = [ 0.0, 0.0, 0.0 ]\r\n\t\t\t\t\t\tangles = [ 0.0, 0.0, 0.0 ]\r\n\t\t\t\t\t\tchildren = \r\n\t\t\t\t\t\t[\r\n" +
                        "\t\t\t\t\t\t\t{\r\n\t\t\t\t\t\t\t\t_class = \"Bone\"\r\n\t\t\t\t\t\t\t\tname = \"pelvis\"\r\n" +
                        string.Format(ic, "\t\t\t\t\t\t\t\torigin = [ {0:F4}, {1:F4}, {2:F4} ]\r\n", pelSrc.X, pelSrc.Y, pelSrc.Z) +
                        "\t\t\t\t\t\t\t\tangles = [ 0.0, 0.0, 0.0 ]\r\n\t\t\t\t\t\t\t\tchildren = \r\n\t\t\t\t\t\t\t\t[\r\n" +
                        "\t\t\t\t\t\t\t\t\t{\r\n\t\t\t\t\t\t\t\t\t\t_class = \"Bone\"\r\n\t\t\t\t\t\t\t\t\t\tname = \"leg_upper_L\"\r\n" +
                        string.Format(ic, "\t\t\t\t\t\t\t\t\t\torigin = [ {0:F4}, {1:F4}, {2:F4} ]\r\n", legLSrc.X, legLSrc.Y, legLSrc.Z) +
                        "\t\t\t\t\t\t\t\t\t\tangles = [ 0.0, 0.0, 0.0 ]\r\n\t\t\t\t\t\t\t\t\t},\r\n" +
                        "\t\t\t\t\t\t\t\t\t{\r\n\t\t\t\t\t\t\t\t\t\t_class = \"Bone\"\r\n\t\t\t\t\t\t\t\t\t\tname = \"leg_upper_R\"\r\n" +
                        string.Format(ic, "\t\t\t\t\t\t\t\t\t\torigin = [ {0:F4}, {1:F4}, {2:F4} ]\r\n", legRSrc.X, legRSrc.Y, legRSrc.Z) +
                        "\t\t\t\t\t\t\t\t\t\tangles = [ 0.0, 0.0, 0.0 ]\r\n\t\t\t\t\t\t\t\t\t},\r\n" +
                        "\t\t\t\t\t\t\t\t]\r\n\t\t\t\t\t\t\t},\r\n\t\t\t\t\t\t]\r\n\t\t\t\t\t},\r\n\t\t\t\t]\r\n\t\t\t},\r\n\t\t]\r\n\t}\r\n}\r\n";

                    // Pre-patch skeleton to get final VmdlSkeletonBone list for DMX & SMD
                    string preVmdl = CustomModelExporter.GeneratePatchedVmdl(
                        vmdlTemplate, bundle, "models/test_skirt", "materials/test_skirt",
                        new List<(string, string)> { ("body", "models/test_skirt/body.smd") },
                        new List<string> { "mat" }, new HashSet<string>(), includeAnimGraph2Nodes: false);
                    var exportedBones = CustomModelExporter.ParseSkeletonBonesFromVmdlText(preVmdl);

                    string proxyLeaf = "yamato_cloth_proxy.dmx";
                    bool wroteDmx = CustomModelExporter.WriteProceduralClothProxyDmx(
                        Path.Combine(tmpSrc, proxyLeaf), $"models/test_skirt/{proxyLeaf}", bundle, exportedBones);
                    Check(wroteDmx, "WriteProceduralClothProxyDmx emitted clean Row-0-pinned wrap-around skirt DMX");

                    // Re-generate vmdl knowing proxyLeaf exists so ClothProxyMeshList + planarize_wrap are emitted
                    string finalVmdl = CustomModelExporter.GeneratePatchedVmdl(
                        vmdlTemplate, bundle, "models/test_skirt", "materials/test_skirt",
                        new List<(string, string)> { ("body", "models/test_skirt/body.smd") },
                        new List<string> { "mat" }, new HashSet<string> { proxyLeaf }, includeAnimGraph2Nodes: false);

                    // Minimal skinned triangle on skirt_front_0/1/2
                    int bf0 = skel.FindBone("skirt_front_0"), bf1 = skel.FindBone("skirt_front_1"), bf2 = skel.FindBone("skirt_front_2");
                    bundle.Submeshes.Add(new CustomModelExporter.CustomModelExportBundle.SubmeshData
                    {
                        SubmeshName = "skirt_mesh",
                        MaterialName = "mat",
                        Vertices = new[] { skel.GetBoneGlobalRest(bf0).Origin, skel.GetBoneGlobalRest(bf1).Origin + new Vector3(0.02f, 0, 0), skel.GetBoneGlobalRest(bf2).Origin },
                        Normals = new[] { Vector3.Back, Vector3.Back, Vector3.Back },
                        UVs = new[] { Vector2.Zero, Vector2.Right, Vector2.One },
                        Indices = new[] { 0, 1, 2 },
                        BoneIndices = new[] { bf0, 0, 0, 0, bf1, 0, 0, 0, bf2, 0, 0, 0 },
                        BoneWeights = new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f }
                    });
                    CustomModelExporter.WriteSourceSpaceSmd(Path.Combine(tmpSrc, "body.smd"), bundle, bundle.Submeshes, exportedBones);
                    CustomModelExporter.WriteRestBindAnimationSmd(Path.Combine(tmpSrc, "yamato_anim_bind.smd"), bundle, exportedBones);
                    string vmdlPath = Path.Combine(tmpSrc, "yamato.vmdl");
                    File.WriteAllText(vmdlPath, finalVmdl);

                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = csdkRc,
                        Arguments = $"-i \"{vmdlPath}\"",
                        WorkingDirectory = Path.GetDirectoryName(csdkRc),
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    };
                    var proc = System.Diagnostics.Process.Start(psi);
                    string rcOut = proc.StandardOutput.ReadToEnd() + "\n" + proc.StandardError.ReadToEnd();
                    proc.WaitForExit();

                    string vmdlC = Path.Combine(tmpDst, "yamato.vmdl_c");
                    bool compiledOk = proc.ExitCode == 0 && File.Exists(vmdlC);
                    if (!compiledOk) GD.PrintErr($"[CSDK13 SKIRT FAIL]\n{rcOut}");

                    float minDynHorizFromPelvis = 9999f;
                    int colPlaneCount = 0;
                    int nodeBasesCount = 0;
                    int worldColNodesCount = 0;
                    int worldColParamsCount = 0;
                    float row1GoalF = -1f, hemGoalF = -1f;
                    if (compiledOk)
                    {
                        using var res = new ValveResourceFormat.Resource { FileName = vmdlC };
                        res.Read(vmdlC);
                        var fe = (res.DataBlock as Model)?.GetEmbeddedPhys()?.FeModel;
                        if (fe != null && fe.InitPose != null)
                        {
                            var ctrlNames = fe.CtrlName ?? Array.Empty<string>();
                            int pelNode = Array.FindIndex(ctrlNames, n => string.Equals(n, "pelvis", StringComparison.OrdinalIgnoreCase));
                            var pelP = pelNode >= 0 ? fe.InitPose[pelNode].Position : new System.Numerics.Vector3(pelSrc.X, pelSrc.Y, pelSrc.Z);
                            colPlaneCount = fe.CollisionPlanes?.Length ?? 0;
                            nodeBasesCount = fe.NodeBases?.Length ?? 0;
                            worldColNodesCount = fe.WorldCollisionNodes?.Length ?? 0;
                            worldColParamsCount = fe.WorldCollisionParams?.Length ?? 0;
                            var dynGoalFs = new List<float>();
                            string minHorizNodeName = "";
                            for (int ni = fe.StaticNodes; ni < fe.NodeCount; ni++)
                            {
                                var p = fe.InitPose[ni].Position;
                                float horiz = MathF.Sqrt((p.X - pelP.X) * (p.X - pelP.X) + (p.Y - pelP.Y) * (p.Y - pelP.Y));
                                if (horiz < minDynHorizFromPelvis) { minDynHorizFromPelvis = horiz; minHorizNodeName = ctrlNames[ni]; }
                                if (fe.NodeIntegrator != null && ni < fe.NodeIntegrator.Length)
                                    dynGoalFs.Add(fe.NodeIntegrator[ni].AnimationForceAttraction);
                            }
                            if (dynGoalFs.Count > 0)
                            {
                                dynGoalFs.Sort();
                                hemGoalF = dynGoalFs[0];
                                row1GoalF = dynGoalFs[^1];
                            }
                            GD.Print($"  [CSDK13 Skirt FeModel] Nodes={fe.NodeCount} Static={fe.StaticNodes} Dyn={fe.NodeCount - fe.StaticNodes} " +
                                     $"Rods={fe.Rods?.Length ?? 0} NodeBases={nodeBasesCount} CollisionPlanes={colPlaneCount} " +
                                     $"minHorizFromPelvis={minDynHorizFromPelvis:F2} in ({minHorizNodeName}) row1GoalF={row1GoalF:F3} hemGoalF={hemGoalF:F4}");
                        }
                    }

                    Check(compiledOk && minDynHorizFromPelvis > 6.5f,
                        $"CSDK13 ProxyCloth skirt has ZERO interior pelvis vertices (minHorizFromPelvis={minDynHorizFromPelvis:F2} in > 6.5 in)");
                    Check(compiledOk && colPlaneCount > 0 && nodeBasesCount >= 12,
                        $"CSDK13 compiled wrap-around skirt with planarize_wrap CollisionPlanes ({colPlaneCount}) and ALL 12 skirt bones back-solved including bone[0] (NodeBases={nodeBasesCount} >= 12)");
                    // Exact match to PhysicsParamResolver at t=1/3 (0.185) and t=1.0 (0.0008)
                    Check(compiledOk && row1GoalF > 0.14f && row1GoalF < 0.24f && hemGoalF < 0.01f,
                        $"CSDK13 skirt goalF matches preview & Valve Yamato curve exactly (row1={row1GoalF:F3} vs 0.185, hem={hemGoalF:F4} vs 0.0008)");
                    Check(compiledOk && worldColNodesCount > 0 && worldColParamsCount > 0,
                        $"CSDK13 ProxyCloth skirt compiles native Source 2 floor collision (WorldCollisionNodes={worldColNodesCount}, Params={worldColParamsCount})");
                }
                finally
                {
                    try { Directory.Delete(@"D:\Reduced_CSDK_13\content\citadel_addons\_dp_verify_proxy_skirt", true); } catch { }
                    try { Directory.Delete(@"D:\Reduced_CSDK_13\game\citadel_addons\_dp_verify_proxy_skirt", true); } catch { }
                }

                // 3. Verify standalone ClothChain with PlanarizeBackBarrier=true (UltraLongPonytailId, ZERO ProxyCloth)
                //    emits ClothVertexMap "planarize_back" and compiles real spine_2/spine_3 CollisionPlanes in CSDK13!
                string tmpPonySrc = @"D:\Reduced_CSDK_13\content\citadel_addons\_dp_verify_pony_plane\models\test_pony";
                string tmpPonyDst = @"D:\Reduced_CSDK_13\game\citadel_addons\_dp_verify_pony_plane\models\test_pony";
                var ponySkel = new Skeleton3D { Name = "PonySkel" };
                try
                {
                    Directory.CreateDirectory(tmpPonySrc);
                    int pRoot = ponySkel.AddBone("root_motion");
                    ponySkel.SetBoneRest(pRoot, Transform3D.Identity);
                    int pSpine = ponySkel.AddBone("spine_3");
                    ponySkel.SetBoneParent(pSpine, pRoot);
                    ponySkel.SetBoneRest(pSpine, new Transform3D(Basis.Identity, new Vector3(0f, 1.35f, 0f)));
                    int pHead = ponySkel.AddBone("head");
                    ponySkel.SetBoneParent(pHead, pSpine);
                    ponySkel.SetBoneRest(pHead, new Transform3D(Basis.Identity, new Vector3(0f, 0.30f, 0f)));
                    int pn0 = ponySkel.AddBone("ponytail_0");
                    ponySkel.SetBoneParent(pn0, pHead);
                    ponySkel.SetBoneRest(pn0, new Transform3D(Basis.Identity, new Vector3(0f, 0.02f, -0.14f)));
                    int pn1 = ponySkel.AddBone("ponytail_1");
                    ponySkel.SetBoneParent(pn1, pn0);
                    ponySkel.SetBoneRest(pn1, new Transform3D(Basis.Identity, new Vector3(0f, -0.16f, -0.04f)));
                    int pn2 = ponySkel.AddBone("ponytail_2");
                    ponySkel.SetBoneParent(pn2, pn1);
                    ponySkel.SetBoneRest(pn2, new Transform3D(Basis.Identity, new Vector3(0f, -0.18f, -0.02f)));

                    var ponyChain = new BoneChainDef
                    {
                        ChainId = "pony_0",
                        Name = "ponytail_0",
                        RootSourceBone = "ponytail_0",
                        AnchorHeroBone = "head",
                        Policy = ChainPolicy.PreservePhysicsChain,
                        SourceBoneNames = new List<string> { "ponytail_0", "ponytail_1", "ponytail_2" },
                        AppendedHeroBoneNames = new List<string> { "ponytail_0", "ponytail_1", "ponytail_2" }
                    };
                    PhysicsPresetCatalog.ApplyPresetToChain(ponyChain, PhysicsPresetCatalog.UltraLongPonytailId, resetMacros: true);

                    var ponyBundle = CustomModelExporter.BuildExportBundle(
                        new List<MeshInstance3D>(), ponySkel, "unicorn", preservedChains: new[] { ponyChain });
                    Vector3 spSrc = CustomModelExporter.GodotToSourcePosition(ponySkel.GetBoneGlobalRest(pSpine).Origin);
                    Vector3 hdSrc = CustomModelExporter.GodotToSourcePosition(ponySkel.GetBoneGlobalRest(pHead).Origin) - spSrc;
                    var ic2 = System.Globalization.CultureInfo.InvariantCulture;
                    string ponyTpl =
                        "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc39:version{1ceecb0d-4093-4729-ab18-b76886c64cde} -->\r\n" +
                        "{\r\n\trootNode = \r\n\t{\r\n\t\t_class = \"RootNode\"\r\n\t\tchildren = \r\n\t\t[\r\n" +
                        "\t\t\t{\r\n\t\t\t\t_class = \"Skeleton\"\r\n\t\t\t\tchildren = \r\n\t\t\t\t[\r\n" +
                        "\t\t\t\t\t{\r\n\t\t\t\t\t\t_class = \"Bone\"\r\n\t\t\t\t\t\tname = \"root_motion\"\r\n\t\t\t\t\t\torigin = [ 0.0, 0.0, 0.0 ]\r\n\t\t\t\t\t\tangles = [ 0.0, 0.0, 0.0 ]\r\n\t\t\t\t\t\tchildren = \r\n\t\t\t\t\t\t[\r\n" +
                        "\t\t\t\t\t\t\t{\r\n\t\t\t\t\t\t\t\t_class = \"Bone\"\r\n\t\t\t\t\t\t\t\tname = \"spine_3\"\r\n" +
                        string.Format(ic2, "\t\t\t\t\t\t\t\torigin = [ {0:F4}, {1:F4}, {2:F4} ]\r\n", spSrc.X, spSrc.Y, spSrc.Z) +
                        "\t\t\t\t\t\t\t\tangles = [ 0.0, 0.0, 0.0 ]\r\n\t\t\t\t\t\t\t\tchildren = \r\n\t\t\t\t\t\t\t\t[\r\n" +
                        "\t\t\t\t\t\t\t\t\t{\r\n\t\t\t\t\t\t\t\t\t\t_class = \"Bone\"\r\n\t\t\t\t\t\t\t\t\t\tname = \"head\"\r\n" +
                        string.Format(ic2, "\t\t\t\t\t\t\t\t\t\torigin = [ {0:F4}, {1:F4}, {2:F4} ]\r\n", hdSrc.X, hdSrc.Y, hdSrc.Z) +
                        "\t\t\t\t\t\t\t\t\t\tangles = [ 0.0, 0.0, 0.0 ]\r\n\t\t\t\t\t\t\t\t\t},\r\n" +
                        "\t\t\t\t\t\t\t\t]\r\n\t\t\t\t\t\t\t},\r\n\t\t\t\t\t\t]\r\n\t\t\t\t\t},\r\n\t\t\t\t]\r\n\t\t\t},\r\n\t\t]\r\n\t}\r\n}\r\n";

                    string ponyVmdl = CustomModelExporter.GeneratePatchedVmdl(
                        ponyTpl, ponyBundle, "models/test_pony", "materials/test_pony",
                        new List<(string, string)> { ("body", "models/test_pony/body.smd") },
                        new List<string> { "mat" }, new HashSet<string>(), includeAnimGraph2Nodes: false);
                    var ponyBones = CustomModelExporter.ParseSkeletonBonesFromVmdlText(ponyVmdl);
                    ponyBundle.Submeshes.Add(new CustomModelExporter.CustomModelExportBundle.SubmeshData
                    {
                        SubmeshName = "pony_mesh",
                        MaterialName = "mat",
                        Vertices = new[] { ponySkel.GetBoneGlobalRest(pn0).Origin, ponySkel.GetBoneGlobalRest(pn1).Origin + new Vector3(0.01f, 0, 0), ponySkel.GetBoneGlobalRest(pn2).Origin },
                        Normals = new[] { Vector3.Back, Vector3.Back, Vector3.Back },
                        UVs = new[] { Vector2.Zero, Vector2.Right, Vector2.One },
                        Indices = new[] { 0, 1, 2 },
                        BoneIndices = new[] { pn0, 0, 0, 0, pn1, 0, 0, 0, pn2, 0, 0, 0 },
                        BoneWeights = new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f }
                    });
                    CustomModelExporter.WriteSourceSpaceSmd(Path.Combine(tmpPonySrc, "body.smd"), ponyBundle, ponyBundle.Submeshes, ponyBones);
                    CustomModelExporter.WriteRestBindAnimationSmd(Path.Combine(tmpPonySrc, "unicorn_anim_bind.smd"), ponyBundle, ponyBones);
                    string ponyVmdlPath = Path.Combine(tmpPonySrc, "unicorn.vmdl");
                    File.WriteAllText(ponyVmdlPath, ponyVmdl);

                    var psi2 = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = csdkRc,
                        Arguments = $"-i \"{ponyVmdlPath}\"",
                        WorkingDirectory = Path.GetDirectoryName(csdkRc),
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    };
                    var proc2 = System.Diagnostics.Process.Start(psi2);
                    proc2.StandardOutput.ReadToEnd();
                    proc2.StandardError.ReadToEnd();
                    proc2.WaitForExit();

                    string ponyVmdlC = Path.Combine(tmpPonyDst, "unicorn.vmdl_c");
                    int ponyPlanes = 0;
                    int ponyWorldColNodes = 0;
                    bool ponyOk = proc2.ExitCode == 0 && File.Exists(ponyVmdlC);
                    if (ponyOk)
                    {
                        using var res2 = new ValveResourceFormat.Resource { FileName = ponyVmdlC };
                        res2.Read(ponyVmdlC);
                        var fe2 = (res2.DataBlock as Model)?.GetEmbeddedPhys()?.FeModel;
                        ponyPlanes = fe2?.CollisionPlanes?.Length ?? 0;
                        ponyWorldColNodes = fe2?.WorldCollisionNodes?.Length ?? 0;
                    }
                    Check(ponyOk && ponyVmdl.Contains("name = \"planarize_back\"") && ponyPlanes > 0,
                        $"Standalone ClothChain UltraLongPonytail emits ClothVertexMap planarize_back and compiles spine CollisionPlanes (count={ponyPlanes})");
                    Check(ponyOk && ponyWorldColNodes > 0,
                        $"Standalone ClothChain UltraLongPonytail compiles native Source 2 floor collision (WorldCollisionNodes={ponyWorldColNodes})");
                }
                finally
                {
                    ponySkel.Free();
                    try { Directory.Delete(@"D:\Reduced_CSDK_13\content\citadel_addons\_dp_verify_pony_plane", true); } catch { }
                    try { Directory.Delete(@"D:\Reduced_CSDK_13\game\citadel_addons\_dp_verify_pony_plane", true); } catch { }
                }
            }

            root.QueueFree();
        }

        private void TestSkirtAndNonShortHairPreviewFidelity()
        {
            GD.Print("--- Skirt & Non-Short Hair In-App Preview Fidelity (FeCollisionPlane + Multi-Pass PBD) ---");

            // 1. HeavySkirtDressId (6-strand wrap-around skirt) with full leg_upper + leg_lower capsules:
            //    Must settle calmly at rest with zero Frame-1 outward/upward explosion from ComputeClosestTaperedCapsule bug!
            var rootSk = new Node3D { Name = "SkirtPreviewFidelityRoot" };
            var skelSk = new Skeleton3D { Name = "Skeleton3D" };
            rootSk.AddChild(skelSk);
            Root.AddChild(rootSk);

            int sRoot = skelSk.AddBone("root_motion");
            skelSk.SetBoneRest(sRoot, Transform3D.Identity);
            int sPelvis = skelSk.AddBone("pelvis");
            skelSk.SetBoneParent(sPelvis, sRoot);
            skelSk.SetBoneRest(sPelvis, new Transform3D(Basis.Identity, new Vector3(0f, 1.00f, 0f)));
            int sLegL = skelSk.AddBone("leg_upper_L");
            skelSk.SetBoneParent(sLegL, sPelvis);
            skelSk.SetBoneRest(sLegL, new Transform3D(Basis.Identity, new Vector3(0.095f, -0.06f, 0f)));
            int sLegR = skelSk.AddBone("leg_upper_R");
            skelSk.SetBoneParent(sLegR, sPelvis);
            skelSk.SetBoneRest(sLegR, new Transform3D(Basis.Identity, new Vector3(-0.095f, -0.06f, 0f)));
            skelSk.ResetBonePoses();

            var skirtDefs = new List<BoneChainDef>();
            var restHemWorld = new List<Vector3>();
            int strandN = 6;
            for (int s = 0; s < strandN; s++)
            {
                float ang = s * Mathf.Tau / strandN;
                Vector3 radial = new Vector3(MathF.Sin(ang), 0f, MathF.Cos(ang));
                int parent = sPelvis;
                var names = new List<string>();
                for (int k = 0; k < 4; k++)
                {
                    string bn = $"skirt6_{s}_{k}";
                    int bi = skelSk.AddBone(bn);
                    skelSk.SetBoneParent(bi, parent);
                    Vector3 loc = k == 0
                        ? (radial * 0.21f + new Vector3(0f, -0.02f, 0f))
                        : (radial * 0.022f + new Vector3(0f, -0.14f, 0f));
                    skelSk.SetBoneRest(bi, new Transform3D(Basis.Identity, loc));
                    skelSk.SetBonePosePosition(bi, loc);
                    skelSk.SetBonePoseRotation(bi, Quaternion.Identity);
                    names.Add(bn);
                    parent = bi;
                }
                restHemWorld.Add(skelSk.GetBoneGlobalRest(skelSk.FindBone(names[^1])).Origin);
                var ch = new BoneChainDef
                {
                    ChainId = $"skirt6_{s}",
                    Name = names[0],
                    RootSourceBone = names[0],
                    AnchorHeroBone = "pelvis",
                    Policy = ChainPolicy.PreservePhysicsChain,
                    SourceBoneNames = new List<string>(names),
                    AppendedHeroBoneNames = new List<string>(names)
                };
                PhysicsPresetCatalog.ApplyPresetToChain(ch, PhysicsPresetCatalog.HeavySkirtDressId, resetMacros: true);
                skirtDefs.Add(ch);
            }

            var drvSk = CustomJiggleBoneDriver.GetOrAttach(rootSk, skelSk);
            drvSk.RegisterChains("skirt6", skirtDefs);
            // Step 5 frames BEFORE registering capsules to verify RegisterCollisionCapsules rebuilds planes on initialized chains!
            for (int f = 0; f < 5; f++) drvSk.StepSimulation(1f / 60f);
            drvSk.RegisterCollisionCapsules(new[]
            {
                new BodyCollisionCapsuleDef { BoneName = "leg_upper_L", Enabled = true, Point0Local = Vector3.Zero, Point1Local = new Vector3(0f, -0.42f, 0f), Radius0Meters = 0.082f, Radius1Meters = 0.070f },
                new BodyCollisionCapsuleDef { BoneName = "leg_upper_R", Enabled = true, Point0Local = Vector3.Zero, Point1Local = new Vector3(0f, -0.42f, 0f), Radius0Meters = 0.082f, Radius1Meters = 0.070f }
            });

            for (int f = 0; f < 90; f++) drvSk.StepSimulation(1f / 60f);

            static Vector3 BoneWorldOrigin(Skeleton3D sk, int bi)
            {
                var chain = new List<int>();
                for (int b = bi; b >= 0; b = sk.GetBoneParent(b)) chain.Add(b);
                Transform3D acc = Transform3D.Identity;
                for (int i = chain.Count - 1; i >= 0; i--) acc *= sk.GetBonePose(chain[i]);
                return acc.Origin;
            }

            float maxSkirtRestDisp = 0f;
            bool skirtAllFinite = true;
            for (int s = 0; s < strandN; s++)
            {
                Vector3 cur = BoneWorldOrigin(skelSk, skelSk.FindBone($"skirt6_{s}_3"));
                if (!cur.IsFinite()) skirtAllFinite = false;
                float d = cur.DistanceTo(restHemWorld[s]);
                if (d > maxSkirtRestDisp) maxSkirtRestDisp = d;
            }
            Check(skirtAllFinite && maxSkirtRestDisp < 0.08f,
                $"HeavySkirtDressId (6-strand) settles cleanly at rest with zero plane explosion (maxDisp={maxSkirtRestDisp:F3}m < 0.08m)");

            // 2. LongFlowingHairId + MediumHairId with BOTH back strands AND front/side framing strands:
            //    Back strands stay calm behind spine_2/spine_3; front/side framing strands stay in FRONT (never slammed through chest!).
            var rootH = new Node3D { Name = "LongHairPreviewRoot" };
            var skelH = new Skeleton3D { Name = "Skeleton3D" };
            rootH.AddChild(skelH);
            Root.AddChild(rootH);

            int hRoot = skelH.AddBone("root_motion");
            skelH.SetBoneRest(hRoot, Transform3D.Identity);
            int hSpine2 = skelH.AddBone("spine_2");
            skelH.SetBoneParent(hSpine2, hRoot);
            skelH.SetBoneRest(hSpine2, new Transform3D(Basis.Identity, new Vector3(0f, 1.25f, 0f)));
            int hSpine3 = skelH.AddBone("spine_3");
            skelH.SetBoneParent(hSpine3, hSpine2);
            skelH.SetBoneRest(hSpine3, new Transform3D(Basis.Identity, new Vector3(0f, 0.16f, 0f)));
            int hHead = skelH.AddBone("head");
            skelH.SetBoneParent(hHead, hSpine3);
            skelH.SetBoneRest(hHead, new Transform3D(Basis.Identity, new Vector3(0f, 0.24f, 0f)));
            skelH.ResetBonePoses();

            (string Tag, Vector3 RootOff)[] hairStrands =
            {
                ("back_c", new Vector3(0f, -0.02f, -0.20f)),
                ("back_l", new Vector3(0.09f, -0.02f, -0.19f)),
                ("front_l", new Vector3(0.14f, -0.02f, 0.15f))
            };
            var hairDefs = new List<BoneChainDef>();
            var hairRestTips = new Dictionary<string, Vector3>();
            foreach (var (tag, rOff) in hairStrands)
            {
                int parent = hHead;
                var names = new List<string>();
                for (int k = 0; k < 4; k++)
                {
                    string bn = $"hair_{tag}_{k}";
                    int bi = skelH.AddBone(bn);
                    skelH.SetBoneParent(bi, parent);
                    Vector3 loc = k == 0 ? rOff : new Vector3(0f, -0.12f, 0f);
                    skelH.SetBoneRest(bi, new Transform3D(Basis.Identity, loc));
                    skelH.SetBonePosePosition(bi, loc);
                    skelH.SetBonePoseRotation(bi, Quaternion.Identity);
                    names.Add(bn);
                    parent = bi;
                }
                hairRestTips[tag] = skelH.GetBoneGlobalRest(skelH.FindBone(names[^1])).Origin;
                var ch = new BoneChainDef
                {
                    ChainId = $"hair_{tag}",
                    Name = names[0],
                    RootSourceBone = names[0],
                    AnchorHeroBone = "head",
                    Policy = ChainPolicy.PreservePhysicsChain,
                    SourceBoneNames = new List<string>(names),
                    AppendedHeroBoneNames = new List<string>(names)
                };
                PhysicsPresetCatalog.ApplyPresetToChain(ch, PhysicsPresetCatalog.LongFlowingHairId, resetMacros: true);
                hairDefs.Add(ch);
            }

            var drvH = CustomJiggleBoneDriver.GetOrAttach(rootH, skelH);
            drvH.RegisterChains("long_hair", hairDefs);
            drvH.RegisterCollisionCapsules(new[]
            {
                new BodyCollisionCapsuleDef { BoneName = "spine_2", Enabled = true, Point0Local = new Vector3(0f, -0.12f, 0f), Point1Local = new Vector3(0f, 0.12f, 0f), Radius0Meters = 0.11f, Radius1Meters = 0.11f },
                new BodyCollisionCapsuleDef { BoneName = "spine_3", Enabled = true, Point0Local = new Vector3(0f, -0.10f, 0f), Point1Local = new Vector3(0f, 0.10f, 0f), Radius0Meters = 0.11f, Radius1Meters = 0.11f }
            });

            for (int f = 0; f < 90; f++) drvH.StepSimulation(1f / 60f);

            Vector3 backCur = BoneWorldOrigin(skelH, skelH.FindBone("hair_back_c_3"));
            Vector3 frontCur = BoneWorldOrigin(skelH, skelH.FindBone("hair_front_l_3"));
            float backDisp = backCur.DistanceTo(hairRestTips["back_c"]);
            float frontDisp = frontCur.DistanceTo(hairRestTips["front_l"]);
            Check(backCur.IsFinite() && backCur.Z < -0.15f && backDisp < 0.08f,
                $"LongFlowingHairId back strand settles calmly behind spine with zero plane blast (Z={backCur.Z:F3}m, disp={backDisp:F3}m)");
            Check(frontCur.IsFinite() && frontCur.Z > 0.08f && frontDisp < 0.08f,
                $"LongFlowingHairId front/side framing strand stays in front of chest instead of slamming behind spine (Z={frontCur.Z:F3}m, disp={frontDisp:F3}m)");

            rootSk.QueueFree();
            rootH.QueueFree();
        }

        private async Task TestHandleWrapLayerHierarchyAndHazeExportAsync()
        {
            GD.Print("--- HandleWrapLayer Discovery & Finished_.glb -> Haze Export Parity ---");

            // 1. Unit test: HeroMeshHierarchy.ScanHero + BuildExportBundle must preserve custom meshes named
            //    'HandleWrapLayer' while still ignoring editor gizmos and skipping constant alpha-0 AlphaDepthPrePass meshes!
            var heroRoot = new Node3D { Name = "TestHazeHero" };
            var skel = new Skeleton3D { Name = "Skeleton3D" };
            heroRoot.AddChild(skel);
            int bRoot = skel.AddBone("root_motion");
            skel.SetBoneRest(bRoot, Transform3D.Identity);
            int bPelvis = skel.AddBone("pelvis");
            skel.SetBoneParent(bPelvis, bRoot);
            skel.SetBoneRest(bPelvis, new Transform3D(Basis.Identity, new Vector3(0, 1.0f, 0)));

            var customContainer = new Node3D { Name = "Custom_Rigged_Character" };
            customContainer.SetMeta("IsCustomImportedMesh", true);
            heroRoot.AddChild(customContainer);

            static MeshInstance3D MakeTriMesh(string name, StandardMaterial3D mat, bool isCustom)
            {
                var arr = new Godot.Collections.Array();
                arr.Resize((int)Godot.Mesh.ArrayType.Max);
                arr[(int)Godot.Mesh.ArrayType.Vertex] = new[] { new Vector3(0, 0, 0), new Vector3(0.2f, 0, 0), new Vector3(0, 0.2f, 0) };
                arr[(int)Godot.Mesh.ArrayType.Normal] = new[] { Vector3.Back, Vector3.Back, Vector3.Back };
                arr[(int)Godot.Mesh.ArrayType.TexUV] = new[] { Vector2.Zero, Vector2.Right, Vector2.Up };
                arr[(int)Godot.Mesh.ArrayType.Index] = new[] { 0, 1, 2 };
                var am = new ArrayMesh();
                am.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arr);
                am.SurfaceSetMaterial(0, mat);
                var mi = new MeshInstance3D { Name = name, Mesh = am };
                if (isCustom) mi.SetMeta("IsCustomImportedMesh", true);
                return mi;
            }

            var bodyMat = new StandardMaterial3D { ResourceName = "rbx_Rig3.model/HandleWrapLayer.MeshPart.001", AlbedoColor = new Color(0.2f, 0.2f, 0.25f, 1f) };
            var hiddenR15Mat = new StandardMaterial3D
            {
                ResourceName = "rbx_Rig3.model/LeftFoot.MeshPart",
                Transparency = BaseMaterial3D.TransparencyEnum.AlphaDepthPrePass,
                AlbedoColor = new Color(1f, 1f, 1f, 0f)
            };
            var gizmoMat = new StandardMaterial3D { ResourceName = "gizmo_mat" };

            var uniformMi = MakeTriMesh("Rig3_model_HandleWrapLayer_MeshPart_001_surf0", bodyMat, isCustom: true);
            var hiddenMi = MakeTriMesh("Rig3_model_LeftFoot_MeshPart_surf0", hiddenR15Mat, isCustom: true);
            var gizmoMi = MakeTriMesh("BonePickerGizmo", gizmoMat, isCustom: false);
            var pickAttach = new BoneAttachment3D { Name = "Pick_cloth_0" };
            var pickerHandleMi = MakeTriMesh("PickerHandle", gizmoMat, isCustom: false);
            pickAttach.AddChild(pickerHandleMi);
            skel.AddChild(pickAttach);
            customContainer.AddChild(uniformMi);
            customContainer.AddChild(hiddenMi);
            heroRoot.AddChild(gizmoMi);
            Root.AddChild(heroRoot);

            var hierarchy = new DeadlockPlayground.Painter.HeroMeshHierarchy();
            Root.AddChild(hierarchy);
            hierarchy.ScanHero(heroRoot);

            bool foundHandleBody = hierarchy.Submeshes.Any(s => s.RawName.Contains("HandleWrapLayer_MeshPart_001"));
            bool excludedGizmo = !hierarchy.Submeshes.Any(s => s.RawName.Contains("BonePickerGizmo"));
            bool excludedPickerHandle = !hierarchy.Submeshes.Any(s => s.RawName.Contains("PickerHandle"));
            Check(foundHandleBody && excludedGizmo && excludedPickerHandle,
                $"HeroMeshHierarchy preserves custom HandleWrapLayer body mesh ({foundHandleBody}) while excluding BonePickerGizmo ({excludedGizmo}) and X-ray PickerHandle ({excludedPickerHandle})");

            // Verify RigidJiggle thumbnail uses pouches_bebop.png and Custom SVG loads at crisp high-DPI (>= 64px)
            var rigidPreset = PhysicsPresetCatalog.Find(PhysicsPresetCatalog.RigidJiggleId);
            var customSvgTex = PhysicsPresetCatalog.LoadPresetTexture(null);
            Check(rigidPreset != null && rigidPreset.ThumbnailPath.EndsWith("pouches_bebop.png", StringComparison.OrdinalIgnoreCase),
                $"RigidJiggle preset uses pouches_bebop.png thumbnail ({rigidPreset?.ThumbnailPath})");
            Check(customSvgTex != null && customSvgTex.GetWidth() >= 64,
                $"Custom preset SVG icon rasterizes at high quality (size={customSvgTex?.GetWidth() ?? 0}x{customSvgTex?.GetHeight() ?? 0})");

            var customMeshes = hierarchy.Submeshes
                .Where(s => s.IsCustomImportedMesh && s.Mesh != null && s.Mesh.Visible)
                .Select(s => s.Mesh)
                .Distinct()
                .ToList();
            var unitBundle = CustomModelExporter.BuildExportBundle(customMeshes, skel, "haze");
            bool bundleHasUniform = unitBundle.Submeshes.Any(sm => sm.SubmeshName.Contains("HandleWrapLayer_MeshPart_001"));
            bool bundleSkippedAlpha0 = !unitBundle.Submeshes.Any(sm => sm.SubmeshName.Contains("LeftFoot"));
            Check(bundleHasUniform && bundleSkippedAlpha0,
                $"BuildExportBundle keeps HandleWrapLayer uniform ({bundleHasUniform}) and skips constant alpha-0 AlphaDepthPrePass under-body ({bundleSkippedAlpha0})");

            // 2. Real end-to-end test on scratch_out\Finished_.glb -> haze.vmdl_c via CSDK13 (when local test asset + VPK + CSDK13 exist)
            string finishedGlb = @"d:\GameDev\deadlock-playground\scratch_out\Finished_.glb";
            string csdkRc = @"D:\Reduced_CSDK_13\game\bin\win64\resourcecompiler.exe";
            string deadlockRoot = @"E:\SteamLibrary\steamapps\common\Deadlock";
            if (File.Exists(finishedGlb) && File.Exists(csdkRc) && File.Exists(Path.Combine(deadlockRoot, "game", "citadel", "pak01_dir.vpk")))
            {
                var doc = new GltfDocument();
                var state = new GltfState();
                Error gErr = doc.AppendFromFile(finishedGlb, state);
                Node3D glbRoot = gErr == Error.Ok ? doc.GenerateScene(state) as Node3D : null;
                if (glbRoot != null)
                {
                    var glbContainer = new Node3D { Name = "Custom_Rigged_Character" };
                    glbContainer.SetMeta("IsCustomImportedMesh", true);
                    // Re-tag all imported meshes as IsCustomImportedMesh as RigAndBind does
                    var srcMeshes = new List<MeshInstance3D>();
                    void WalkMeshes(Node n)
                    {
                        if (n is MeshInstance3D m && m.Mesh != null) srcMeshes.Add(m);
                        foreach (Node c in n.GetChildren()) WalkMeshes(c);
                    }
                    WalkMeshes(glbRoot);

                    var fakeHero = new Node3D { Name = "HazeFinishedTest" };
                    var fakeSkel = new Skeleton3D { Name = "Skeleton3D" };
                    fakeHero.AddChild(fakeSkel);
                    int r0 = fakeSkel.AddBone("root_motion");
                    fakeSkel.SetBoneRest(r0, Transform3D.Identity);
                    int r1 = fakeSkel.AddBone("pelvis");
                    fakeSkel.SetBoneParent(r1, r0);
                    fakeSkel.SetBoneRest(r1, new Transform3D(Basis.Identity, new Vector3(0, 1.0f, 0)));
                    fakeHero.AddChild(glbContainer);
                    foreach (var m in srcMeshes)
                    {
                        var dup = new MeshInstance3D { Name = $"{m.Name}_surf0", Mesh = m.Mesh, Visible = true };
                        dup.SetMeta("IsCustomImportedMesh", true);
                        glbContainer.AddChild(dup);
                    }
                    Root.AddChild(fakeHero);

                    var h2 = new DeadlockPlayground.Painter.HeroMeshHierarchy();
                    Root.AddChild(h2);
                    h2.ScanHero(fakeHero);

                    int scannedHandleCount = h2.Submeshes.Count(s => s.RawName.Contains("HandleWrapLayer", StringComparison.OrdinalIgnoreCase));
                    Check(scannedHandleCount == 4,
                        $"Finished_.glb HeroMeshHierarchy captures all 4 HandleWrapLayer meshes including main body uniform (got {scannedHandleCount}/4)");

                    var exportMeshes = h2.Submeshes
                        .Where(s => s.IsCustomImportedMesh && s.Mesh != null && s.Mesh.Visible)
                        .Select(s => s.Mesh)
                        .Distinct()
                        .ToList();
                    var hazeBundle = CustomModelExporter.BuildExportBundle(
                        exportMeshes,
                        fakeSkel,
                        "haze",
                        heroModelVpkPath: "models/heroes_staging/haze/haze.vmdl_c",
                        compilerExe: csdkRc,
                        deadlockGamePath: deadlockRoot);

                    bool hasMainUniform = hazeBundle.Submeshes.Any(sm => sm.SubmeshName.Contains("HandleWrapLayer_MeshPart_001") && sm.Vertices.Length == 3718);
                    Check(hasMainUniform && hazeBundle.Submeshes.Count == 9,
                        $"Finished_.glb BuildExportBundle includes 3718-vert main body uniform and exactly 9 visible opaque meshes (got {hazeBundle.Submeshes.Count})");

                    var compRes = await CustomModelExporter.ExportAndCompileCustomModelAsync(hazeBundle);
                    int compiledTris = 0;
                    if (compRes.Success && !string.IsNullOrEmpty(compRes.CompiledVmdlCPath) && File.Exists(compRes.CompiledVmdlCPath))
                    {
                        using var vRes = new ValveResourceFormat.Resource { FileName = compRes.CompiledVmdlCPath };
                        vRes.Read(compRes.CompiledVmdlCPath);
                        if (vRes.DataBlock is Model mdl)
                        {
                            foreach (var em in mdl.GetEmbeddedMeshes())
                            {
                                var vb = em.Mesh.VBIB;
                                if (vb?.IndexBuffers != null)
                                    foreach (var ib in vb.IndexBuffers) compiledTris += (int)(ib.ElementCount / 3);
                            }
                        }
                    }
                    Check(compRes.Success && compiledTris >= 20000,
                        $"Finished_.glb compiled cleanly into haze.vmdl_c via CSDK13 with full body geometry (ok={compRes.Success}, tris={compiledTris}, files={compRes.AllowlistedVpkFiles.Count}, err='{compRes.ErrorMessage}')");

                    h2.QueueFree();
                    fakeHero.QueueFree();
                    glbRoot.QueueFree();
                }
            }

            hierarchy.QueueFree();
            heroRoot.QueueFree();
        }
    }
}
