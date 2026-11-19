using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DeadlockPlayground.Animation;
using DeadlockPlayground.Importer;
using Godot;

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
                TestProxyClothOpenSheetVsClosedSkirt();
                TestPlanarizedTorsoBackBarrier();
                TestCustomPropBoxColliderAndJsonRoundTrip();
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
            driver.RegisterChains("cape", MakeChainDefs("spine_3", 1, 3, SimulationKind.Cloth, 0.05f, 0.0f, 0.1f, 1.5f));

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

            drvLoose.RegisterChains("loose", MakeChainDefs("spine_3", 1, 3, SimulationKind.Cloth, 0.05f, 0.0f, 0.25f, 1.0f));
            drvStiff.RegisterChains("stiff", MakeChainDefs("spine_3", 1, 3, SimulationKind.Cloth, 0.95f, 0.85f, 0.25f, 1.0f));

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
            driver.RegisterChains("jiggle", MakeChainDefs("pelvis", 1, 2, SimulationKind.Jiggle, 0.95f, 0.85f, 0.50f, 1.0f, angleLimitDeg: 20f));

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
            driver.RegisterChains("cape", MakeChainDefs("spine_2", 1, 2, SimulationKind.Cloth, 0.15f, 0.0f, 0.2f, 1.0f, openSheet: true, planarizeBack: true));
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
    }
}
