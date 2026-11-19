using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DeadlockPlayground.Animation;
using DeadlockPlayground.Catalog;
using DeadlockPlayground.Importer;
using DeadlockPlayground.Particles;
using DeadlockPlayground.Tools;
using DeadlockPlayground.UI;
using Godot;

namespace DeadlockPlayground.Tests
{
    /// <summary>
    /// End-to-end headless verification suite for Importer Overhaul Phases 0-6:
    /// - Phase 0 & 1: ImportReport, ImporterMemoryGuard commit/free check, resource-sharing hierarchy clone.
    /// - Phase 2: RigBoneClassifier (ARP/Rigify/Cape), unweighted pruning, authoritative CustomBoneRemap
    ///            across all methods, BoneChainDef preservation, CustomJiggleBoneDriver, AddClothingChainTool,
    ///            and .vmdl bone/jiggle export.
    /// - Phase 3: AccessoryBoneMode.KeepOwnBones attachment & clean RemoveAccessorySlot bone cleanup.
    /// - Phase 4: CustomFingerAutoRigger fingertip detection, Mirror L->R, chain generation & pose presets.
    /// - Phase 5 & 6: 5-tab CustomModelImporterModalUI layout, diff summary, ImporterPresetStore round-trip.
    /// </summary>
    public partial class ImporterOverhaulVerificationTest : SceneTree
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

        private static string ResolveSamplePath(string fileName)
        {
            string[] candidates =
            {
                Path.GetFullPath(Path.Combine("..", "tests", "importer_samples", fileName)),
                Path.GetFullPath(Path.Combine("tests", "importer_samples", fileName)),
                Path.GetFullPath(Path.Combine(@"d:\GameDev\deadlock-playground\tests\importer_samples", fileName))
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            return candidates[^1];
        }

        private static Node3D LoadGlb(string path)
        {
            var doc = new GltfDocument();
            var state = new GltfState();
            var err = doc.AppendFromFile(path, state);
            if (err != Error.Ok) return null;
            return doc.GenerateScene(state) as Node3D;
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
                string arpPath = ResolveSamplePath("arp_magician_sample.glb");
                string rigifyPath = ResolveSamplePath("rigify_sample.glb");
                string capePath = ResolveSamplePath("cape_chain_sample.glb");
                string staticPath = ResolveSamplePath("static_humanoid_sample.glb");

                Check(File.Exists(arpPath) && File.Exists(rigifyPath) && File.Exists(capePath) && File.Exists(staticPath),
                    "All 4 golden .glb samples exist in tests/importer_samples/");

                var vpkPath = HeroParticleService.ResolveVpkPath();
                var vpkLoader = new VpkLoaderTest { Name = "VpkLoaderTest", VpkPath = vpkPath };
                Root.AddChild(vpkLoader);

                var heroEntry = DeadlockHeroCatalog.GetByCodename("bebop");
                Check(heroEntry != null, "Resolved Bebop entry from DeadlockHeroCatalog");

                Node3D heroNode = await vpkLoader.LoadHeroModelAsync(heroEntry);
                Check(heroNode != null, "Loaded Bebop model from VPK");
                var heroSkel = CustomModelAutoRigger.FindSkeleton(heroNode);
                Check(heroSkel != null && heroSkel.GetBoneCount() > 0, $"Found hero skeleton ({heroSkel?.GetBoneCount() ?? 0} bones)");

                // === Phase 0 & 1: ImporterMemoryGuard & Resource-Sharing Clone ===
                GD.Print("=== Phase 0 & 1: Memory Guard & Resource-Sharing Clone ===");
                var memReport = ImporterMemoryGuard.Evaluate(arpPath, heroNode);
                Check(memReport.EstimateBytes > 0 && !string.IsNullOrEmpty(ImporterMemoryGuard.FormatFooterMemoryText()),
                    $"ImporterMemoryGuard estimated {memReport.EstimateBytes / (1024 * 1024)} MB and formatted footer telemetry");

                Node3D sharedClone = ImporterMemoryGuard.CloneHierarchySharingResources(heroNode);
                Root.AddChild(sharedClone);
                var origMeshes = CustomModelAutoRigger.CollectMeshes(heroNode);
                var clonedMeshes = CustomModelAutoRigger.CollectMeshes(sharedClone);
                Check(origMeshes.Count == clonedMeshes.Count && clonedMeshes.Count > 0,
                    $"CloneHierarchySharingResources preserved {clonedMeshes.Count} meshes");
                bool sharedSameMeshRef = origMeshes.Count > 0 && clonedMeshes.Count > 0 &&
                    ReferenceEquals(origMeshes[0].Mesh, clonedMeshes[0].Mesh);
                Check(sharedSameMeshRef, "CloneHierarchySharingResources shares heavy Mesh resource instances zero-copy");
                sharedClone.QueueFree();

                // === Phase 2: RigBoneClassifier (ARP, Rigify, Cape) ===
                GD.Print("=== Phase 2: RigBoneClassifier & Clothing Chains ===");
                Node3D arpRoot = LoadGlb(arpPath);
                Node3D rigifyRoot = LoadGlb(rigifyPath);
                Node3D capeRoot = LoadGlb(capePath);
                Check(arpRoot != null && rigifyRoot != null && capeRoot != null, "Loaded ARP, Rigify, and Cape GLB scenes cleanly");

                var arpSkel = CustomModelAutoRigger.FindSkeleton(arpRoot);
                var arpMeshes = CustomModelAutoRigger.CollectMeshes(arpRoot);
                var arpWeighted = RigBoneClassifier.ComputeWeightedBoneIndices(arpSkel, arpMeshes);
                var arpAnalysis = AnalyzeSkeleton(arpSkel, arpWeighted);
                Check(arpAnalysis.Family == RigFamily.AutoRigPro, $"ARP sample detected as {arpAnalysis.Family} (conf={arpAnalysis.Confidence:P0})");

                int idxCpos = arpSkel.FindBone("c_pos");
                int idxCape1 = arpSkel.FindBone("cape_01.x");
                Check(idxCpos >= 0 && arpAnalysis.IsControlOrHelper(idxCpos), "ARP 'c_pos' classified as Control/Helper");
                Check(!arpWeighted.Contains(idxCpos) && arpWeighted.Contains(idxCape1), "Weighted indices exclude 'c_pos' and include 'cape_01.x'");

                var arpExtra = RigBoneClassifier.FindExtraChains(arpAnalysis, new HashSet<int>(arpAnalysis.CanonicalToSource.Values), arpWeighted);
                Check(arpExtra.Any(c => c.RootName.StartsWith("cape_01", StringComparison.OrdinalIgnoreCase)),
                    $"ARP extra chain extractor found cape chain ({arpExtra.Count} extra chains)");

                var rigifySkel = CustomModelAutoRigger.FindSkeleton(rigifyRoot);
                var rigifyAnalysis = AnalyzeSkeleton(rigifySkel, RigBoneClassifier.ComputeWeightedBoneIndices(rigifySkel, CustomModelAutoRigger.CollectMeshes(rigifyRoot)));
                Check(rigifyAnalysis.Family == RigFamily.Rigify, $"Rigify sample detected as {rigifyAnalysis.Family}");

                // Authoritative CustomBoneRemap across PreRigged, KnnSurfaceProjection, LandmarkAutoRig + Preserved Chain + Jiggle + VMDL export
                var capeHeroClone = ImporterMemoryGuard.CloneHierarchySharingResources(heroNode);
                Root.AddChild(capeHeroClone);
                var capeSkel = CustomModelAutoRigger.FindSkeleton(capeRoot);
                var capeMeshes = CustomModelAutoRigger.CollectMeshes(capeRoot);
                var capeWeighted = RigBoneClassifier.ComputeWeightedBoneIndices(capeSkel, capeMeshes);
                var capeAnalysis = AnalyzeSkeleton(capeSkel, capeWeighted);
                var capeMappedSet = new HashSet<int>(capeAnalysis.CanonicalToSource.Values);
                var capeChains = RigBoneClassifier.FindExtraChains(capeAnalysis, capeMappedSet, capeWeighted);
                var capeHeroSkel = CustomModelAutoRigger.FindSkeleton(capeHeroClone);
                int heroBonesBeforeCape = capeHeroSkel.GetBoneCount();

                var chainDefs = capeChains.Select(ec => new BoneChainDef
                {
                    ChainId = ec.RootName,
                    Name = ec.RootName,
                    Category = ec.Category,
                    RootSourceBone = ec.RootName,
                    SourceBoneNames = ec.BoneIndices.Select(i => capeSkel.GetBoneName(i)).ToList(),
                    AnchorHeroBone = RigBoneClassifier.ResolveAnchorHeroBoneName(capeAnalysis, ec, new Dictionary<int, int>(), capeHeroSkel),
                    Policy = ChainPolicy.PreservePhysicsChain,
                    Simulation = SimulationKind.Jiggle,
                    StiffnessRoot = 0.85f,
                    StiffnessTip = 0.20f,
                    Damping = 0.35f,
                    Gravity = 1.0f
                }).ToList();

                var customRemap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["head"] = "head",
                    ["neck"] = "neck",
                    ["spine_upper"] = "spine_3",
                    ["spine_lower"] = "spine_1",
                    ["pelvis"] = "pelvis"
                };

                var capeOpts = new CustomModelAutoRigger.RiggingOptions
                {
                    Method = CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap,
                    Mode = CustomModelAutoRigger.ReplaceMode.FullReplacement,
                    CustomBoneRemap = customRemap,
                    PreserveSourceClothingChains = true,
                    KeepUnweightedBones = false,
                    BoneChains = chainDefs,
                    DisabledSourceBones = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cs_grp" },
                    BindSymmetry = true
                };

                var capeResult = CustomModelAutoRigger.RigAndBind(capeRoot, capeHeroClone, capeOpts);
                Check(capeResult.Success, $"PreRigged RigAndBind with preserved cape chain succeeded: {capeResult.Report?.BuildSummaryLine()}");
                Check(capeHeroSkel.GetBoneCount() > heroBonesBeforeCape,
                    $"Preserved clothing chain appended bones to hero skeleton ({heroBonesBeforeCape} -> {capeHeroSkel.GetBoneCount()})");
                Check(capeResult.Report != null && capeResult.Report.Stats.ClothBoneCount >= 3,
                    $"ImportReport logged ClothBoneCount={capeResult.Report?.Stats.ClothBoneCount ?? 0}");

                var jiggleDriver = capeHeroClone.FindChildren("*", "Node", true, false).OfType<CustomJiggleBoneDriver>().FirstOrDefault();
                Check(jiggleDriver != null && jiggleDriver.EnableSimulation, "CustomJiggleBoneDriver attached & enabled on preserved cape chain");
                if (jiggleDriver != null)
                {
                    jiggleDriver.StepSimulation(0.016f);
                    var activeDefs = jiggleDriver.GetActiveChainDefinitions();
                    Check(activeDefs.Count > 0, $"CustomJiggleBoneDriver.GetActiveChainDefinitions() returned {activeDefs.Count} cloned chain(s)");
                }

                // Verify Bundle + VMDL patch preserves custom bones & jiggle physics (both explicit & fallback metadata)
                var riggedMeshes = CustomModelAutoRigger.CollectMeshes(capeResult.ContainerNode);
                var exportBundle = CustomModelExporter.BuildExportBundle(
                    riggedMeshes, capeHeroSkel, "bebop", preservedChains: chainDefs);
                var fallbackBundle = CustomModelExporter.BuildExportBundle(
                    riggedMeshes, capeHeroSkel, "bebop", preservedChains: null);
                Check(exportBundle.PreservedCustomBoneNames.Count >= 3 && fallbackBundle.PreservedChains.Count >= 1,
                    $"CustomModelExportBundle preserved {exportBundle.PreservedCustomBoneNames.Count} custom cape bone(s) and fallback recovered {fallbackBundle.PreservedChains.Count} chain(s) when preservedChains == null");

                string minimalVmdl = "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc39:version{123} -->\n" +
                    "{\n\trootNode = \n\t{\n\t\t_class = \"RootNode\"\n\t\tchildren = \n\t\t[\n" +
                    "\t\t\t{\n\t\t\t\t_class = \"RenderMeshList\"\n\t\t\t\tchildren = []\n\t\t\t},\n" +
                    "\t\t\t{\n\t\t\t\t_class = \"Skeleton\"\n\t\t\t\tchildren = [\n" +
                    "\t\t\t\t\t{\n\t\t\t\t\t\t_class = \"Bone\"\n\t\t\t\t\t\tname = \"pelvis\"\n\t\t\t\t\t\torigin = [0.0, 0.0, 0.0]\n\t\t\t\t\t\tangles = [0.0, 0.0, 0.0]\n\t\t\t\t\t\tchildren = [\n" +
                    "\t\t\t\t\t\t\t{\n\t\t\t\t\t\t\t\t_class = \"Bone\"\n\t\t\t\t\t\t\t\tname = \"spine_3\"\n\t\t\t\t\t\t\t\torigin = [0.0, 0.0, 10.0]\n\t\t\t\t\t\t\t\tangles = [0.0, 0.0, 0.0]\n\t\t\t\t\t\t\t}\n" +
                    "\t\t\t\t\t\t]\n\t\t\t\t\t}\n\t\t\t\t]\n\t\t\t}\n" +
                    "\t\t]\n\t}\n}\n";
                string patchedVmdl = CustomModelExporter.GeneratePatchedVmdl(
                    minimalVmdl, exportBundle, "models/custom", "materials/custom",
                    new List<(string, string)> { ("custom_body", "models/custom/body.smd") },
                    new List<string> { "mat0" },
                    new HashSet<string>(),
                    includeAnimGraph2Nodes: false);
                Check(patchedVmdl.Contains("cape_center_01", StringComparison.OrdinalIgnoreCase) &&
                      patchedVmdl.Contains("JiggleBoneList", StringComparison.OrdinalIgnoreCase) &&
                      patchedVmdl.Contains("JiggleBone", StringComparison.OrdinalIgnoreCase),
                    "GeneratePatchedVmdl preserved custom cape bones and emitted JiggleBoneList physics block");
                capeHeroClone.QueueFree();

                // Verify authoritative CustomBoneRemap on KNN mode too
                var knnHeroClone = ImporterMemoryGuard.CloneHierarchySharingResources(heroNode);
                Root.AddChild(knnHeroClone);
                Node3D knnSrc = LoadGlb(arpPath);
                var knnOpts = new CustomModelAutoRigger.RiggingOptions
                {
                    Method = CustomModelAutoRigger.RiggingMethod.KnnSurfaceProjection,
                    CustomBoneRemap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["head"] = "head" },
                    BindSymmetry = true
                };
                var knnRes = CustomModelAutoRigger.RigAndBind(knnSrc, knnHeroClone, knnOpts);
                Check(knnRes.Success && knnRes.Report?.Stats.VertexCount > 0,
                    $"KNN fallback with BindSymmetry succeeded (verts={knnRes.Report?.Stats.VertexCount ?? 0})");
                knnHeroClone.QueueFree();

                // === Phase 3: Accessory KeepOwnBones & Slot Removal Cleanup ===
                GD.Print("=== Phase 3: Accessory KeepOwnBones & Slot Bone Cleanup ===");
                var accHeroClone = ImporterMemoryGuard.CloneHierarchySharingResources(heroNode);
                Root.AddChild(accHeroClone);
                var accHeroSkel = CustomModelAutoRigger.FindSkeleton(accHeroClone);
                int bonesBeforeAcc = accHeroSkel.GetBoneCount();

                Node3D accSrc = LoadGlb(capePath);
                string slotId = "slot_test_cape";
                var accOpts = new CustomModelAutoRigger.RiggingOptions
                {
                    SlotId = slotId,
                    AccessoryName = "TestCape",
                    Intent = CustomModelAutoRigger.AccessoryIntent.AddAccessory,
                    Mode = CustomModelAutoRigger.ReplaceMode.PartialAccessory,
                    AccessoryBoneMode = AccessoryBoneMode.KeepOwnBones,
                    RigidBoneName = "spine_3",
                    KeepUnweightedBones = false
                };
                var accRes = CustomModelAutoRigger.RigAndBind(accSrc, accHeroClone, accOpts);
                int bonesWithAcc = accHeroSkel.GetBoneCount();
                Check(accRes.Success && bonesWithAcc > bonesBeforeAcc,
                    $"Accessory KeepOwnBones appended bones ({bonesBeforeAcc} -> {bonesWithAcc})");

                CustomModelAutoRigger.RemoveAccessorySlot(accHeroClone, slotId);
                int bonesAfterRemove = accHeroSkel.GetBoneCount();
                Check(bonesAfterRemove == bonesBeforeAcc,
                    $"RemoveAccessorySlot cleanly removed slot bones ({bonesWithAcc} -> {bonesAfterRemove})");
                accHeroClone.QueueFree();

                // === Phase 4: Finger Auto-Rigging & AddClothingChainTool on Static Humanoid ===
                GD.Print("=== Phase 4: Finger Auto-Rigger & Synthetic Clothing Chain ===");
                var finHeroClone = ImporterMemoryGuard.CloneHierarchySharingResources(heroNode);
                Root.AddChild(finHeroClone);
                var finHeroSkel = CustomModelAutoRigger.FindSkeleton(finHeroClone);
                int bonesBeforeSyn = finHeroSkel.GetBoneCount();

                Node3D staticSrc = LoadGlb(staticPath);
                var staticMeshes = CustomModelAutoRigger.CollectMeshes(staticSrc);
                var bodyPins = CustomLandmarkWeightSolver.EstimateLandmarks(staticSrc, staticMeshes);
                var fingerPins = CustomFingerAutoRigger.AutoDetectFingerLandmarks(staticSrc, staticMeshes, bodyPins);
                Check(fingerPins.Count >= 10, $"AutoDetectFingerLandmarks detected {fingerPins.Count} finger tip/joint pins on static humanoid");

                CustomFingerAutoRigger.MirrorFingerLandmarks(fingerPins, leftToRight: true, Vector3.Zero);
                Check(fingerPins.Keys.Any(k => k.EndsWith("_L")) && fingerPins.Keys.Any(k => k.EndsWith("_R")),
                    "MirrorFingerLandmarks synchronized L/R finger landmarks");

                var synChain = new BoneChainDef
                {
                    ChainId = "syn_cape",
                    Name = "SyntheticCape",
                    AnchorHeroBone = "spine_3",
                    Policy = ChainPolicy.PreservePhysicsChain,
                    Simulation = SimulationKind.Jiggle,
                    IsSynthetic = true,
                    SyntheticLengthMeters = 0.55f,
                    SyntheticSegmentCount = 4,
                    SyntheticInfluenceRadius = 0.30f,
                    SyntheticOriginHeroSpace = new Vector3(0f, 1.30f, -0.18f),
                    SyntheticDirectionHeroSpace = new Vector3(0f, -1f, 0f)
                };

                var finOpts = new CustomModelAutoRigger.RiggingOptions
                {
                    Method = CustomModelAutoRigger.RiggingMethod.LandmarkAutoRig,
                    LandmarkPins = bodyPins,
                    FingerMode = FingerRigMode.GenerateFingerChains,
                    FingerLandmarks = fingerPins,
                    GenerateFingerMetacarpals = true,
                    GenerateWristTwist = true,
                    BoneChains = new List<BoneChainDef> { synChain },
                    PreserveSourceClothingChains = true
                };

                var finRes = CustomModelAutoRigger.RigAndBind(staticSrc, finHeroClone, finOpts);
                Check(finRes.Success, $"LandmarkAutoRig + GenerateFingerChains + SyntheticChain succeeded ({finRes.Report?.BuildSummaryLine()})");
                Check(finRes.Report != null && finRes.Report.Stats.FingerBoneCount > 0,
                    $"CustomFingerAutoRigger applied weights across {finRes.Report?.Stats.FingerBoneCount ?? 0} finger vertices");
                Check(finHeroSkel.GetBoneCount() > bonesBeforeSyn,
                    $"AddClothingChainTool appended synthetic chain bones ({bonesBeforeSyn} -> {finHeroSkel.GetBoneCount()})");

                int idxFingerBone = finHeroSkel.FindBone("finger_index_0_L");
                if (idxFingerBone >= 0)
                {
                    Quaternion beforePose = finHeroSkel.GetBonePoseRotation(idxFingerBone);
                    CustomFingerAutoRigger.ApplyFingerPosePreset(finHeroSkel, FingerPosePreset.Fist);
                    Quaternion afterFist = finHeroSkel.GetBonePoseRotation(idxFingerBone);
                    Check(!beforePose.IsEqualApprox(afterFist), "ApplyFingerPosePreset(Fist) articulated finger bones");
                }
                else
                {
                    Check(false, "Expected finger_index_0_L on hero skeleton");
                }
                finHeroClone.QueueFree();

                // === Phase 5 & 6: 5-Tab Modal UI, Presets & Bone Disable ===
                GD.Print("=== Phase 5 & 6: 5-Tab Modal UI & ImporterPresetStore ===");
                var modal = new CustomModelImporterModalUI();
                Root.AddChild(modal);
                Node3D modalSrc = LoadGlb(arpPath);
                modal.Open(heroNode, modalSrc, arpPath);
                await ToSignal(this, SignalName.ProcessFrame);

                var fiTabs = typeof(CustomModelImporterModalUI).GetField("_rightTabs", BindingFlags.NonPublic | BindingFlags.Instance);
                var tabs = fiTabs?.GetValue(modal) as TabContainer;
                Check(tabs != null && tabs.GetTabCount() == 5, $"Modal TabContainer has {tabs?.GetTabCount() ?? 0} tabs (expected 5)");
                if (tabs != null && tabs.GetTabCount() == 5)
                {
                    Check(tabs.GetTabTitle(0).Contains("Import") &&
                          tabs.GetTabTitle(1).Contains("Rig") &&
                          tabs.GetTabTitle(2).Contains("Bones") &&
                          tabs.GetTabTitle(3).Contains("Meshes") &&
                          tabs.GetTabTitle(4).Contains("Export") &&
                          !tabs.GetTabTitle(0).Contains("_") &&
                          tabs.GetTabIcon(0) != null && tabs.GetTabIcon(4) != null,
                        $"5-tab titles & icons verified: [{tabs.GetTabTitle(0)}, {tabs.GetTabTitle(1)}, {tabs.GetTabTitle(2)}, {tabs.GetTabTitle(3)}, {tabs.GetTabTitle(4)}]");
                }

                // Verify native .glb scene export
                string tempGlbOut = Path.Combine(Path.GetTempPath(), $"dl_verify_export_{Guid.NewGuid():N}.glb");
                var exportSample = LoadGlb(arpPath);
                bool glbOk = CustomModelExporter.ExportSceneToGlb(exportSample, tempGlbOut, out string glbErr);
                Check(glbOk && File.Exists(tempGlbOut) && new FileInfo(tempGlbOut).Length > 200,
                    $"CustomModelExporter.ExportSceneToGlb wrote valid GLB ({(File.Exists(tempGlbOut) ? new FileInfo(tempGlbOut).Length : 0)} bytes, err={glbErr})");
                if (File.Exists(tempGlbOut)) File.Delete(tempGlbOut);
                exportSample?.QueueFree();

                // Optional Kiriko Rainy Day hybrid ARP+Rigify test (if present in scratch_out/models)
                string kirikoPath = Path.GetFullPath(Path.Combine(@"d:\GameDev\deadlock-playground\scratch_out\models", "Kiriko Rainy Day.glb"));
                if (File.Exists(kirikoPath))
                {
                    GD.Print("=== Phase 7: Kiriko Hybrid ARP+Rigify & Hair Physics Alignment ===");
                    Node3D kirikoRoot = LoadGlb(kirikoPath);
                    var kirikoSkel = CustomModelAutoRigger.FindSkeleton(kirikoRoot);
                    var kirikoMeshes = CustomModelAutoRigger.CollectMeshes(kirikoRoot);
                    var kirikoWeighted = RigBoneClassifier.ComputeWeightedBoneIndices(kirikoSkel, kirikoMeshes);
                    var kirikoAnalysis = AnalyzeSkeleton(kirikoSkel, kirikoWeighted);
                    Check(kirikoAnalysis.Family == RigFamily.AutoRigPro,
                        $"Kiriko hybrid body detected as {kirikoAnalysis.Family} despite Rigify facial bones");

                    var kirikoExtra = RigBoneClassifier.FindExtraChains(
                        kirikoAnalysis, new HashSet<int>(kirikoAnalysis.CanonicalToSource.Values), kirikoWeighted);
                    Check(!kirikoExtra.Any(c => c.RootName.StartsWith("ORG-face", StringComparison.OrdinalIgnoreCase)),
                        $"Kiriko extra chains excluded ORG-face ({kirikoExtra.Count} chains total)");

                    var kirikoHeroClone = ImporterMemoryGuard.CloneHierarchySharingResources(heroNode);
                    Root.AddChild(kirikoHeroClone);
                    var kirikoHeroSkel = CustomModelAutoRigger.FindSkeleton(kirikoHeroClone);
                    var kBounds = CustomModelAutoRigger.ComputePristineModelBounds(kirikoRoot);
                    var hBounds = CustomModelAutoRigger.ComputeAuthenticHeroBounds(kirikoHeroClone);
                    float scale = (hBounds.Max.Y - hBounds.Min.Y) / MathF.Max(0.2f, kBounds.Max.Y - kBounds.Min.Y);
                    var kirikoOpts = new CustomModelAutoRigger.RiggingOptions
                    {
                        Method = CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap,
                        AlignedTransform = new Transform3D(Basis.Identity.Scaled(new Vector3(scale, scale, scale)), Vector3.Zero),
                        AutoSnapHeadToHero = true,
                        ConformToSkeleton = true,
                        PreserveSourceClothingChains = true,
                        EnableCustomPhysics = true
                    };
                    var kRes = CustomModelAutoRigger.RigAndBind(kirikoRoot, kirikoHeroClone, kirikoOpts);
                    Check(kRes.Success, $"Kiriko PreRigged bind succeeded ({kRes.Report?.BuildSummaryLine()})");

                    int headIdx = kirikoHeroSkel.FindBone("head");
                    int hairTail1 = kirikoHeroSkel.FindBone("hair tail 1");
                    int hairFront = kirikoHeroSkel.FindBone("hair front");
                    Check(hairFront < 0, "Single-bone 'hair front' defaulted to Collapse (locked rigid to head)");
                    if (headIdx >= 0 && hairTail1 >= 0)
                    {
                        float headToHairRest = (kirikoHeroSkel.GetBoneGlobalRest(hairTail1).Origin - kirikoHeroSkel.GetBoneGlobalRest(headIdx).Origin).Length();
                        Check(headToHairRest < 0.25f, $"Preserved 'hair tail 1' aligned tightly to snapped head pivot (dist={headToHairRest:F3}m < 0.25m)");
                    }
                    else
                    {
                        Check(false, "Expected preserved 'hair tail 1' on Kiriko hero skeleton");
                    }

                    var kDriver = kirikoHeroClone.FindChildren("*", "Node", true, false).OfType<CustomJiggleBoneDriver>().FirstOrDefault();
                    Check(kDriver != null && kDriver.EnableSimulation, "CustomJiggleBoneDriver active on Kiriko multi-bone hair/cloth");
                    if (kDriver != null)
                    {
                        Vector3 beforeOrigin = hairTail1 >= 0 ? kirikoHeroSkel.GetBoneGlobalPose(hairTail1).Origin : Vector3.Zero;
                        for (int f = 0; f < 5; f++) kDriver.StepSimulation(0.016f);
                        Vector3 afterOrigin = hairTail1 >= 0 ? kirikoHeroSkel.GetBoneGlobalPose(hairTail1).Origin : Vector3.Zero;
                        Check(beforeOrigin.DistanceTo(afterOrigin) < 0.08f, $"Kiriko hair tail rest pivot stayed stable during jiggle simulation (drift={beforeOrigin.DistanceTo(afterOrigin):F4}m)");
                    }

                    // Toggle EnableCustomPhysics OFF and verify zero appended jiggle chains
                    kirikoOpts.EnableCustomPhysics = false;
                    var kOffRes = CustomModelAutoRigger.RigAndBind(kirikoRoot, kirikoHeroClone, kirikoOpts);
                    Check(kOffRes.Success && kirikoHeroSkel.FindBone("hair tail 1") < 0,
                        "EnableCustomPhysics = false cleanly froze & un-appended jiggle chains");
                    kirikoHeroClone.QueueFree();
                    kirikoRoot.QueueFree();
                }

                // === Phase 8: Yaw=0°, Captain.glb, Wraith Ghost Anims, Slider Scrubbing & Jiggle Axis Guard ===
                GD.Print("=== Phase 8: Yaw=0°, Captain.glb, Wraith Ghost Anims, Slider Scrubbing & Jiggle Axis Guard ===");
                var fiYawSpin = typeof(CustomModelImporterModalUI).GetField("_spinRotYaw", BindingFlags.NonPublic | BindingFlags.Instance);
                var yawSpin = fiYawSpin?.GetValue(modal) as SpinBox;
                var miAutoFit = typeof(CustomModelImporterModalUI).GetMethod("AutoFitToHero", BindingFlags.NonPublic | BindingFlags.Instance);
                miAutoFit?.Invoke(modal, null);
                Check(yawSpin != null && Math.Abs(yawSpin.Value) < 0.01, $"AutoFitToHero defaults Yaw to 0° (actual={yawSpin?.Value ?? -999:F1}°)");

                string captainPath = Path.GetFullPath(Path.Combine(@"d:\GameDev\deadlock-playground\scratch_out\models", "captain.glb"));
                if (File.Exists(captainPath))
                {
                    Node3D capRoot = LoadGlb(captainPath);
                    var capSkel = CustomModelAutoRigger.FindSkeleton(capRoot);
                    var capMeshes = CustomModelAutoRigger.CollectMeshes(capRoot);
                    var capWeighted = RigBoneClassifier.ComputeWeightedBoneIndices(capSkel, capMeshes);
                    var capAnalysis = AnalyzeSkeleton(capSkel, capWeighted);
                    var capAutoMap = HumanoidBoneMapper.AutoMap(capSkel, heroSkel);

                    Check(capAutoMap.TryGetValue("leg_upper_L", out string luL) && luL == "Left leg",
                        $"Captain Left leg auto-mapped -> '{luL}'");
                    Check(capAutoMap.TryGetValue("leg_lower_L", out string llL) && llL == "Left knee",
                        $"Captain Left knee auto-mapped -> '{llL}'");
                    Check(capAutoMap.TryGetValue("ankle_L", out string anL) && anL == "Left ankle",
                        $"Captain Left ankle auto-mapped -> '{anL}'");
                    Check(capAutoMap.TryGetValue("ball_L", out string blL) && blL == "Left toe",
                        $"Captain Left toe auto-mapped -> '{blL}'");
                    Check(capAutoMap.TryGetValue("arm_lower_L", out string alL) && alL == "Left elbow",
                        $"Captain Left elbow auto-mapped -> '{alL}'");
                    Check(capAnalysis.CanonicalToSource.TryGetValue("finger_middle_0_L", out int fmIdx) && capSkel.GetBoneName(fmIdx) == "MiddleFinger0_L",
                        $"Captain MiddleFinger0_L classified without HairMiddle collision -> '{(capAnalysis.CanonicalToSource.ContainsKey("finger_middle_0_L") ? capSkel.GetBoneName(fmIdx) : "")}'");

                    var capExtra = RigBoneClassifier.FindExtraChains(
                        capAnalysis, new HashSet<int>(capAnalysis.CanonicalToSource.Values), capWeighted);
                    var capHub = capExtra.FirstOrDefault(c => c.RootName == "HairRoot");
                    var capHairCenter = capExtra.FirstOrDefault(c => c.RootName == "HairCenter.001");
                    var capHairMidL = capExtra.FirstOrDefault(c => c.RootName == "HairMiddle.L.001");
                    var capHairMidR = capExtra.FirstOrDefault(c => c.RootName == "HairMiddle.R.001");
                    var capBreastL = capExtra.FirstOrDefault(c => c.RootName == "Breast.L");
                    var capCoatTailL = capExtra.FirstOrDefault(c => c.RootName == "CoatTail.L");

                    Check(capHub != null && capHub.IsMultiBranchHub && capHub.BoneIndices.Count == 1,
                        $"Captain HairRoot isolated as 1-bone rigid scalp hub (IsMultiBranchHub={capHub?.IsMultiBranchHub})");
                    Check(capHairCenter != null && capHairCenter.BoneIndices.Count == 3 &&
                          capHairMidL != null && capHairMidL.BoneIndices.Count == 3 &&
                          capHairMidR != null && capHairMidR.BoneIndices.Count == 3,
                        $"Captain hair strands split cleanly (HairCenter={capHairCenter?.BoneIndices.Count}, HairMiddle.L={capHairMidL?.BoneIndices.Count}, HairMiddle.R={capHairMidR?.BoneIndices.Count})");
                    Check(!capExtra.Any(c => c.RootName == "BreastRoot" || c.RootName.StartsWith("Coat TailsRoot")),
                        $"Unweighted hubs BreastRoot / Coat TailsRoot omitted ({capBreastL?.RootName}, {capCoatTailL?.RootName} promoted)");

                    var capHeroClone = ImporterMemoryGuard.CloneHierarchySharingResources(heroNode);
                    Root.AddChild(capHeroClone);
                    var capHeroSkel = CustomModelAutoRigger.FindSkeleton(capHeroClone);
                    var capBounds = CustomModelAutoRigger.ComputePristineModelBounds(capRoot);
                    var hBoundsCap = CustomModelAutoRigger.ComputeAuthenticHeroBounds(capHeroClone);
                    float capScale = (hBoundsCap.Max.Y - hBoundsCap.Min.Y) / MathF.Max(0.2f, capBounds.Max.Y - capBounds.Min.Y);
                    var capOpts = new CustomModelAutoRigger.RiggingOptions
                    {
                        Method = CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap,
                        AlignedTransform = new Transform3D(Basis.Identity.Scaled(new Vector3(capScale, capScale, capScale)), Vector3.Zero),
                        AutoSnapArmsToHero = true,
                        AutoSnapLegsToHero = true,
                        AutoSnapHeadToHero = true,
                        ConformToSkeleton = true,
                        PreserveSourceClothingChains = true,
                        EnableCustomPhysics = true
                    };
                    var capRes = CustomModelAutoRigger.RigAndBind(capRoot, capHeroClone, capOpts);
                    Check(capRes.Success, $"Captain PreRigged bind succeeded ({capRes.Report?.BuildSummaryLine()})");
                    Check(capHeroSkel.FindBone("HairRoot") < 0 && capHeroSkel.FindBone("Hairpoof.L") < 0,
                        "Captain HairRoot scalp & single-bone Hairpoof.L defaulted to Rigid (collapsed onto head)");
                    Check(capHeroSkel.FindBone("HairCenter.001") >= 0 && capHeroSkel.FindBone("HairMiddle.L.001") >= 0,
                        "Captain split hair tails preserved with physics on hero skeleton");

                    var capDriver = capHeroClone.FindChildren("*", "Node", true, false).OfType<CustomJiggleBoneDriver>().FirstOrDefault();
                    Check(capDriver != null && capDriver.EnableSimulation, "CustomJiggleBoneDriver active on Captain split hair tails");
                    if (capDriver != null)
                    {
                        // Also test extreme bone pose perturbations to guarantee zero Quaternion(axis, angle) exceptions
                        bool noCrash = true;
                        try
                        {
                            for (int f = 0; f < 10; f++) capDriver.StepSimulation(0.033f);
                            int headB = capHeroSkel.FindBone("head");
                            if (headB >= 0)
                            {
                                capHeroSkel.SetBonePoseRotation(headB, new Quaternion(Vector3.Up, Mathf.Pi * 0.999f));
                                for (int f = 0; f < 15; f++) capDriver.StepSimulation(0.033f);
                            }
                        }
                        catch (Exception jEx)
                        {
                            noCrash = false;
                            GD.PrintErr($"Captain jiggle stress threw: {jEx}");
                        }
                        Check(noCrash, "CustomJiggleBoneDriver survived extreme ~180° swings with zero Quaternion normalization exceptions");
                    }

                    capHeroClone.QueueFree();
                    capRoot.QueueFree();
                }

                // Verify AnimationPlayer clone preserves RootNode & Wraith ghost animates + timeline slider scrubs cleanly
                var wraithEntry = DeadlockHeroCatalog.GetByCodename("wraith");
                Check(wraithEntry != null, "Resolved Wraith entry from DeadlockHeroCatalog");
                if (wraithEntry != null)
                {
                    Node3D wraithHero = await vpkLoader.LoadHeroModelAsync(wraithEntry);
                    Check(wraithHero != null, "Loaded Wraith model from VPK");
                    if (wraithHero != null)
                    {
                        // Simulate main stage state: scan split surfaces + decode primary_stand_idle onto live hero before modal.Open
                        var wraithHierarchy = new DeadlockPlayground.Painter.HeroMeshHierarchy();
                        wraithHero.AddChild(wraithHierarchy);
                        wraithHierarchy.ScanHero(wraithHero);
                        var liveWraithAp = wraithHero.FindChildren("*", "AnimationPlayer", true, false).OfType<AnimationPlayer>().FirstOrDefault();
                        var liveWraithSkel = CustomModelAutoRigger.FindSkeleton(wraithHero);
                        var wIdle = vpkLoader.CurrentHeroAnimations.FirstOrDefault(a => a.CleanName.Equals("primary_stand_idle", StringComparison.OrdinalIgnoreCase));
                        if (liveWraithAp != null && liveWraithSkel != null && wIdle != null)
                        {
                            vpkLoader.DecodeAnimation(wIdle, liveWraithSkel, liveWraithAp, null, wIdle.CleanName, true);
                            liveWraithAp.AssignedAnimation = wIdle.CleanName;
                        }

                        var wraithModal = new CustomModelImporterModalUI();
                        Root.AddChild(wraithModal);
                        Node3D wraithSrc = LoadGlb(arpPath);
                        wraithModal.Open(wraithHero, wraithSrc, arpPath);
                        await ToSignal(this, SignalName.ProcessFrame);

                        var fiWraithAp = typeof(CustomModelImporterModalUI).GetField("_previewAnimPlayer", BindingFlags.NonPublic | BindingFlags.Instance);
                        var fiWraithSkel = typeof(CustomModelImporterModalUI).GetField("_previewHeroSkeleton", BindingFlags.NonPublic | BindingFlags.Instance);
                        var fiOptTestAnim = typeof(CustomModelImporterModalUI).GetField("_optTestAnim", BindingFlags.NonPublic | BindingFlags.Instance);
                        var fiSliderTime = typeof(CustomModelImporterModalUI).GetField("_sliderAnimTimeline", BindingFlags.NonPublic | BindingFlags.Instance);
                        var miOnAnimSel = typeof(CustomModelImporterModalUI).GetMethod("OnTestAnimSelected", BindingFlags.NonPublic | BindingFlags.Instance);

                        var wAp = fiWraithAp?.GetValue(wraithModal) as AnimationPlayer;
                        var wSkel = fiWraithSkel?.GetValue(wraithModal) as Skeleton3D;
                        var wOptAnim = fiOptTestAnim?.GetValue(wraithModal) as OptionButton;
                        var wSlider = fiSliderTime?.GetValue(wraithModal) as HSlider;

                        Check(wAp != null && wSkel != null, $"Wraith modal bound primary AnimationPlayer={wAp?.Name} & Skeleton={wSkel?.Name} (isolated from live AssignedAnimation)");
                        Check(wAp != null && (liveWraithAp == null || wAp.GetAnimationLibrary("") != liveWraithAp.GetAnimationLibrary("")),
                            "Preview AnimationPlayer owns an isolated AnimationLibrary (never mutates live hero)");
                        if (wAp != null && wSkel != null && wOptAnim != null && wOptAnim.ItemCount > 1)
                        {
                            int chosenIdx = 1;
                            for (int ai = 1; ai < wOptAnim.ItemCount; ai++)
                            {
                                string nm = wOptAnim.GetItemText(ai).ToLowerInvariant();
                                if (nm.Contains("item_run_n") || nm.Contains("run")) { chosenIdx = ai; break; }
                            }
                            wOptAnim.Select(chosenIdx);
                            miOnAnimSel?.Invoke(wraithModal, new object[] { (long)chosenIdx });
                            await ToSignal(this, SignalName.ProcessFrame);

                            string activeAnim = !string.IsNullOrEmpty(wAp.CurrentAnimation) ? wAp.CurrentAnimation : wAp.AssignedAnimation;
                            int armU = wSkel.FindBone("arm_upper_L");
                            int legU = wSkel.FindBone("leg_upper_L");
                            Quaternion restArm = armU >= 0 ? wSkel.GetBoneRest(armU).Basis.GetRotationQuaternion() : Quaternion.Identity;
                            Quaternion pose0Arm = armU >= 0 ? wSkel.GetBonePoseRotation(armU) : Quaternion.Identity;
                            Quaternion pose0Leg = legU >= 0 ? wSkel.GetBonePoseRotation(legU) : Quaternion.Identity;

                            // Simulate clip ending (CurrentAnimation cleared while AssignedAnimation retained), then scrub slider
                            wAp.Stop();
                            double scrubT = Math.Min(0.28, wSlider != null ? Math.Max(0.05, wSlider.MaxValue * 0.5) : 0.25);
                            if (wSlider != null) wSlider.Value = scrubT;
                            await ToSignal(this, SignalName.ProcessFrame);

                            Quaternion pose1Arm = armU >= 0 ? wSkel.GetBonePoseRotation(armU) : Quaternion.Identity;
                            Quaternion pose1Leg = legU >= 0 ? wSkel.GetBonePoseRotation(legU) : Quaternion.Identity;
                            bool wraithMoved = !pose0Arm.IsEqualApprox(pose1Arm) || !pose0Leg.IsEqualApprox(pose1Leg) ||
                                               !pose1Arm.IsEqualApprox(restArm) ||
                                               Math.Abs(wAp.CurrentAnimationPosition - scrubT) < 0.08;
                            Check(!string.IsNullOrEmpty(activeAnim) && wraithMoved,
                                $"Wraith ghost anim '{activeAnim}' & timeline slider scrub moved skeleton to t={wAp.CurrentAnimationPosition:F2}s (armMoved={!pose1Arm.IsEqualApprox(restArm)})");
                        }

                        wraithModal.QueueFree();
                        wraithHero.QueueFree();
                    }
                }

                // Verify Physics Tree grouping UI + Viewport Bone Selection Badge
                {
                    var fiChainList = typeof(CustomModelImporterModalUI).GetField("_boxClothingChainsList", BindingFlags.NonPublic | BindingFlags.Instance);
                    var chainList = fiChainList?.GetValue(modal) as VBoxContainer;
                    var groupPanels = chainList?.GetChildren().OfType<PanelContainer>().ToList() ?? new List<PanelContainer>();
                    Check(groupPanels.Count > 0, $"Physics tree UI grouped chains into {groupPanels.Count} collapsible PanelContainer group(s)");

                    // Verify viewport bone selection & badge quick actions
                    var fiSelName = typeof(CustomModelImporterModalUI).GetField("_selectedViewportBoneName", BindingFlags.NonPublic | BindingFlags.Instance);
                    var fiBadge = typeof(CustomModelImporterModalUI).GetField("_boxSelectedBoneBadge", BindingFlags.NonPublic | BindingFlags.Instance);
                    var fiBadgeLbl = typeof(CustomModelImporterModalUI).GetField("_lblSelectedBoneName", BindingFlags.NonPublic | BindingFlags.Instance);
                    var miUpdBadge = typeof(CustomModelImporterModalUI).GetMethod("UpdateSelectedBoneBadge", BindingFlags.NonPublic | BindingFlags.Instance);
                    var miOnDis = typeof(CustomModelImporterModalUI).GetMethod("OnSelBoneDisablePressed", BindingFlags.NonPublic | BindingFlags.Instance);
                    var fiDisSet = typeof(CustomModelImporterModalUI).GetField("_disabledSourceBones", BindingFlags.NonPublic | BindingFlags.Instance);

                    fiSelName?.SetValue(modal, "head");
                    miUpdBadge?.Invoke(modal, null);
                    var badgeCtrl = fiBadge?.GetValue(modal) as PanelContainer;
                    var badgeLbl = fiBadgeLbl?.GetValue(modal) as Label;
                    Check(badgeCtrl != null && badgeCtrl.Visible && badgeLbl != null && badgeLbl.Text.Contains("head"),
                        $"Viewport bone selection badge surfaced for 'head' ('{badgeLbl?.Text}')");

                    miOnDis?.Invoke(modal, null);
                    var disSet = fiDisSet?.GetValue(modal) as HashSet<string>;
                    Check(disSet != null && disSet.Contains("head"), "Selected bone badge 'Disable' action toggled 'head' disabled state");
                    if (disSet != null && disSet.Contains("head")) miOnDis?.Invoke(modal, null); // restore
                    fiSelName?.SetValue(modal, "");
                    miUpdBadge?.Invoke(modal, null);
                }

                // Verify CustomModelExporter nested Skeleton bone hierarchy, JiggleBone schema & Procedural ClothProxy DMX + CSDK13 FeModel
                {
                    var testBundle = new CustomModelExporter.CustomModelExportBundle
                    {
                        HeroCodename = "haze",
                        BoneNames = { "root_motion", "pelvis", "head", "HairRoot", "HairCenter.001", "HairCenter.002" },
                        BoneHierarchy =
                        {
                            ["root_motion"] = null,
                            ["pelvis"] = "root_motion",
                            ["head"] = "pelvis",
                            ["HairRoot"] = "head",
                            ["HairCenter.001"] = "HairRoot",
                            ["HairCenter.002"] = "HairCenter.001"
                        },
                        PreservedCustomBoneNames = { "HairRoot", "HairCenter.001", "HairCenter.002" },
                        PreservedChains =
                        {
                            new BoneChainDef
                            {
                                Name = "HairCenter",
                                Category = "Clothing & Hair",
                                AnchorHeroBone = "head",
                                RootSourceBone = "HairCenter.001",
                                SourceBoneNames = { "HairCenter.001", "HairCenter.002" },
                                Policy = ChainPolicy.PreservePhysicsChain,
                                Simulation = SimulationKind.Cloth,
                                StiffnessRoot = 0.8f,
                                StiffnessTip = 0.25f,
                                Damping = 0.35f
                            }
                        }
                    };
                    testBundle.BoneLocalRestTransforms["root_motion"] = Transform3D.Identity;
                    testBundle.BoneLocalRestTransforms["pelvis"] = new Transform3D(Basis.Identity, new Vector3(0, 0, 1.0f));
                    testBundle.BoneLocalRestTransforms["head"] = new Transform3D(Basis.Identity, new Vector3(0, 0, 0.55f));
                    testBundle.BoneLocalRestTransforms["HairRoot"] = new Transform3D(Basis.Identity, new Vector3(0, -0.05f, 0.1f));
                    testBundle.BoneLocalRestTransforms["HairCenter.001"] = new Transform3D(Basis.Identity, new Vector3(0, -0.08f, -0.05f));
                    testBundle.BoneLocalRestTransforms["HairCenter.002"] = new Transform3D(Basis.Identity, new Vector3(0, -0.02f, -0.15f));

                    string minimalTemplate =
                        "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc39:version{1ceecb0d-4093-4729-ab18-b76886c64cde} -->\r\n" +
                        "{\r\n\trootNode = \r\n\t{\r\n\t\t_class = \"RootNode\"\r\n\t\tchildren = \r\n\t\t[\r\n" +
                        "\t\t\t{\r\n\t\t\t\t_class = \"Skeleton\"\r\n\t\t\t\tchildren = \r\n\t\t\t\t[\r\n" +
                        "\t\t\t\t\t{\r\n\t\t\t\t\t\t_class = \"Bone\"\r\n\t\t\t\t\t\tname = \"root_motion\"\r\n\t\t\t\t\t\torigin = [ 0.0, 0.0, 0.0 ]\r\n\t\t\t\t\t\tangles = [ 0.0, 0.0, 0.0 ]\r\n\t\t\t\t\t\tchildren = \r\n\t\t\t\t\t\t[\r\n" +
                        "\t\t\t\t\t\t\t{\r\n\t\t\t\t\t\t\t\t_class = \"Bone\"\r\n\t\t\t\t\t\t\t\tname = \"pelvis\"\r\n\t\t\t\t\t\t\t\torigin = [ 0.0, 0.0, 39.37 ]\r\n\t\t\t\t\t\t\t\tangles = [ 0.0, 0.0, 0.0 ]\r\n\t\t\t\t\t\t\t\tchildren = \r\n\t\t\t\t\t\t\t\t[\r\n" +
                        "\t\t\t\t\t\t\t\t\t{\r\n\t\t\t\t\t\t\t\t\t\t_class = \"Bone\"\r\n\t\t\t\t\t\t\t\t\t\tname = \"head\"\r\n\t\t\t\t\t\t\t\t\t\torigin = [ 0.0, 0.0, 21.65 ]\r\n\t\t\t\t\t\t\t\t\t\tangles = [ 0.0, 0.0, 0.0 ]\r\n\t\t\t\t\t\t\t\t\t},\r\n" +
                        "\t\t\t\t\t\t\t\t]\r\n\t\t\t\t\t\t\t},\r\n" +
                        "\t\t\t\t\t\t]\r\n\t\t\t\t\t},\r\n" +
                        "\t\t\t\t]\r\n\t\t\t},\r\n" +
                        "\t\t]\r\n\t}\r\n}\r\n";

                    // Normalize dotted preserved custom bones (HairCenter.001 -> haircenter_001) identically across
                    // BoneNames, PreservedChains, SMD, Skeleton, and _cloth_proxy.dmx
                    CustomModelExporter.NormalizePreservedCustomBoneNamesForSource2(testBundle);
                    Check(testBundle.PreservedCustomBoneNames.Contains("haircenter_001") &&
                          !testBundle.PreservedCustomBoneNames.Contains("HairCenter.001") &&
                          testBundle.PreservedChains[0].SourceBoneNames.Contains("haircenter_001"),
                        "NormalizePreservedCustomBoneNamesForSource2 normalized HairCenter.001 -> haircenter_001 across bundle & chains");

                    // Fallback / JiggleBone schema check
                    string patchedNoDmx = CustomModelExporter.GeneratePatchedVmdl(
                        minimalTemplate, testBundle, "models/test", "materials/test",
                        new List<(string, string)> { ("test_mesh", "models/test/test.smd") },
                        new List<string> { "mat" }, new HashSet<string>(), includeAnimGraph2Nodes: false);

                    bool hasNestedHeadChild = patchedNoDmx.Contains("name = \"hairroot\"") &&
                        patchedNoDmx.Contains("name = \"haircenter_001\"") &&
                        patchedNoDmx.Contains("name = \"haircenter_002\"") &&
                        !patchedNoDmx.Contains("note = \"parent=");
                    bool hasNoDoubleJiggle = !patchedNoDmx.Contains("_class = \"JiggleBoneList\"") &&
                        !patchedNoDmx.Contains("jiggle_root_bone = \"haircenter_001\"");
                    bool hasNativeClothChain = patchedNoDmx.Contains("_class = \"ClothChain\"") &&
                        patchedNoDmx.Contains("joint_name = \"haircenter_001\"") &&
                        patchedNoDmx.Contains("goal_damping = 0.049") &&
                        patchedNoDmx.Contains("_class = \"ClothShapeCapsule\"") &&
                        patchedNoDmx.Contains("local_force = 0.58");
                    Check(hasNestedHeadChild, "PatchSkeletonBlockWithCustomBones nests normalized Bone children under vanilla parent ('head' -> 'hairroot' -> 'haircenter_001' -> 'haircenter_002')");
                    Check(hasNoDoubleJiggle && hasNativeClothChain, "GeneratePatchedVmdl emits Softbody ClothChain + calibrated goal_damping + ClothParams + ClothShapeCapsule WITHOUT double-solving JiggleBoneList");

                    // Procedural DMX cloth proxy + Native ClothChain CSDK13 compile tests
                    string csdkRc = @"D:\Reduced_CSDK_13\game\bin\win64\resourcecompiler.exe";
                    if (File.Exists(csdkRc))
                    {
                        string tmpAddonSrc = @"D:\Reduced_CSDK_13\content\citadel_addons\_dp_verify_cloth\models\test";
                        string tmpAddonDst = @"D:\Reduced_CSDK_13\game\citadel_addons\_dp_verify_cloth\models\test";
                        try
                        {
                            Directory.CreateDirectory(tmpAddonSrc);
                            var smdBones = new List<CustomModelExporter.VmdlSkeletonBone>
                            {
                                new() { Index = 0, Name = "root_motion", ParentIndex = -1, Origin = Vector3.Zero, ParentRot = System.Numerics.Quaternion.Identity, HasParentRot = true },
                                new() { Index = 1, Name = "pelvis", ParentIndex = 0, Origin = new Vector3(0, 0, 39.37f), ParentRot = System.Numerics.Quaternion.Identity, HasParentRot = true },
                                new() { Index = 2, Name = "head", ParentIndex = 1, Origin = new Vector3(0, 0, 21.65f), ParentRot = System.Numerics.Quaternion.Identity, HasParentRot = true },
                                new() { Index = 3, Name = "hairroot", ParentIndex = 2, Origin = new Vector3(0, -2.0f, 3.0f), ParentRot = System.Numerics.Quaternion.Identity, HasParentRot = true },
                                new() { Index = 4, Name = "haircenter_001", ParentIndex = 3, Origin = new Vector3(0, -3.5f, -2.0f), ParentRot = System.Numerics.Quaternion.Identity, HasParentRot = true },
                                new() { Index = 5, Name = "haircenter_002", ParentIndex = 4, Origin = new Vector3(0, -1.0f, -6.0f), ParentRot = System.Numerics.Quaternion.Identity, HasParentRot = true },
                            };
                            File.WriteAllText(Path.Combine(tmpAddonSrc, "haze_anim_bind.smd"),
                                "version 1\nnodes\n0 \"root_motion\" -1\n1 \"pelvis\" 0\n2 \"head\" 1\n3 \"hairroot\" 2\n4 \"haircenter_001\" 3\n5 \"haircenter_002\" 4\nend\n" +
                                "skeleton\ntime 0\n0 0 0 0 0 0 0\n1 0 0 39.37 0 0 0\n2 0 0 21.65 0 0 0\n3 0 -2 3 0 0 0\n4 0 -3.5 -2 0 0 0\n5 0 -1 -6 0 0 0\nend\n");
                            File.WriteAllText(Path.Combine(tmpAddonSrc, "test.smd"),
                                "version 1\nnodes\n0 \"root_motion\" -1\n1 \"pelvis\" 0\n2 \"head\" 1\n3 \"hairroot\" 2\n4 \"haircenter_001\" 3\n5 \"haircenter_002\" 4\nend\n" +
                                "skeleton\ntime 0\n0 0 0 0 0 0 0\n1 0 0 39.37 0 0 0\n2 0 0 21.65 0 0 0\n3 0 -2 3 0 0 0\n4 0 -3.5 -2 0 0 0\n5 0 -1 -6 0 0 0\nend\n" +
                                "triangles\nmat\n3 0 -2 64 0 0 1 0 0 1 3 1.0\n4 1 -5.5 62 0 0 1 0.5 0.5 1 4 1.0\n4 -1 -5.5 62 0 0 1 1 1 1 4 1.0\n" +
                                "mat\n4 0 -5.5 62 0 0 1 0 0 1 4 1.0\n5 1 -6.5 56 0 0 1 0.5 0.5 1 5 1.0\n5 -1 -6.5 56 0 0 1 1 1 1 5 1.0\nend\n");

                            string dmxPath = Path.Combine(tmpAddonSrc, "haze_cloth_proxy.dmx");
                            bool dmxOk = CustomModelExporter.WriteProceduralClothProxyDmx(dmxPath, "models/test/haze_cloth_proxy.dmx", testBundle, smdBones);
                            string dmxText = dmxOk && File.Exists(dmxPath) ? File.ReadAllText(dmxPath) : "";
                            Check(dmxOk && dmxText.Contains("cloth_enable$0") && dmxText.Contains("cloth_stray_radius$0") && dmxText.Contains("cloth_suspenders$0"),
                                "WriteProceduralClothProxyDmx emitted valid ASCII DMX with cloth_enable$0, goal, stray_radius & suspenders");

                            string patchedWithDmx = CustomModelExporter.GeneratePatchedVmdl(
                                minimalTemplate, testBundle, "models/test", "materials/test",
                                new List<(string, string)> { ("test_mesh", "models/test/test.smd") },
                                new List<string> { "mat" },
                                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "haze_cloth_proxy.dmx" },
                                includeAnimGraph2Nodes: false);
                            Check(patchedWithDmx.Contains("ClothProxyMeshList") && patchedWithDmx.Contains("back_solve_joints = true") && patchedWithDmx.Contains("Softbody"),
                                "GeneratePatchedVmdl emits ClothProxyMeshList (back_solve_joints = true) + Softbody ClothParams");

                            string vmdlFile = Path.Combine(tmpAddonSrc, "haze.vmdl");
                            File.WriteAllText(vmdlFile, patchedWithDmx);
                            var psi = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = csdkRc,
                                Arguments = $"-i \"{vmdlFile}\"",
                                WorkingDirectory = Path.GetDirectoryName(csdkRc),
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                UseShellExecute = false
                            };
                            var proc = System.Diagnostics.Process.Start(psi);
                            string rcOut = proc.StandardOutput.ReadToEnd() + "\n" + proc.StandardError.ReadToEnd();
                            proc.WaitForExit();

                            string compiledVmdlC = Path.Combine(tmpAddonDst, "haze.vmdl_c");
                            bool rcSuccess = proc.ExitCode == 0 && File.Exists(compiledVmdlC);
                            if (!rcSuccess) GD.PrintErr($"[CSDK13 FAIL OUTPUT]\n{rcOut}");
                            var dmxStats = rcSuccess ? ReadCompiledFeModelAndBones(compiledVmdlC) : new CompiledFeStats();
                            int hair001Count = dmxStats.BoneNames.Count(b => b.Equals("haircenter_001", StringComparison.OrdinalIgnoreCase));
                            bool hasExactSingleNormalizedHair = hair001Count == 1 && !dmxStats.BoneNames.Any(b => b.Equals("HairCenter.001", StringComparison.Ordinal));
                            bool hasCalibratedDmxPhysics = dmxStats.DynGravity >= 359.0f && dmxStats.DynDamping >= 0f && dmxStats.DynDamping <= 0.01f &&
                                dmxStats.Capsules >= 2 && dmxStats.LocalForce <= 0.62f && dmxStats.MaxDynForce >= 0.15f && dmxStats.MaxDynForce <= 0.65f;
                            Check(rcSuccess && dmxStats.Rods > 0 && dmxStats.FreeNodes > 0 && dmxStats.NodeBases > 0 && hasExactSingleNormalizedHair && hasCalibratedDmxPhysics,
                                $"CSDK13 compiled normalized ClothProxy DMX into FeModel (rcExit={proc.ExitCode}, Rods={dmxStats.Rods}, Free={dmxStats.FreeNodes}, NB={dmxStats.NodeBases}, exactHair={hasExactSingleNormalizedHair}, Caps={dmxStats.Capsules}, LF={dmxStats.LocalForce:F2}, maxForce={dmxStats.MaxDynForce:F2}, damp={dmxStats.DynDamping:F3}, grav={dmxStats.DynGravity:F1})");

                            // Now compile the native ClothChain path (patchedNoDmx) and verify capsules, stray radii, and cubic-compensated Force
                            string vmdlChainFile = Path.Combine(tmpAddonSrc, "haze_chain.vmdl");
                            File.WriteAllText(vmdlChainFile, patchedNoDmx);
                            var psiChain = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = csdkRc,
                                Arguments = $"-i \"{vmdlChainFile}\"",
                                WorkingDirectory = Path.GetDirectoryName(csdkRc),
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                UseShellExecute = false
                            };
                            var procChain = System.Diagnostics.Process.Start(psiChain);
                            string rcChainOut = procChain.StandardOutput.ReadToEnd() + "\n" + procChain.StandardError.ReadToEnd();
                            procChain.WaitForExit();

                            string compiledChainVmdlC = Path.Combine(tmpAddonDst, "haze_chain.vmdl_c");
                            bool rcChainSuccess = procChain.ExitCode == 0 && File.Exists(compiledChainVmdlC);
                            if (!rcChainSuccess) GD.PrintErr($"[CSDK13 CHAIN FAIL OUTPUT]\n{rcChainOut}");
                            var chainStats = rcChainSuccess ? ReadCompiledFeModelAndBones(compiledChainVmdlC) : new CompiledFeStats();
                            int chainHair001 = chainStats.BoneNames.Count(b => b.Equals("haircenter_001", StringComparison.OrdinalIgnoreCase));
                            bool hasChainCalibrated = chainStats.DynGravity >= 359.0f && chainStats.DynDamping >= 0f && chainStats.DynDamping <= 0.01f &&
                                chainStats.Capsules >= 2 && chainStats.StrayRadii >= 2 && chainStats.LocalForce <= 0.62f && chainStats.MaxDynForce >= 0.30f && chainHair001 == 1;
                            Check(rcChainSuccess && chainStats.Rods > 0 && chainStats.NodeBases > 0 && hasChainCalibrated,
                                $"CSDK13 compiled native ClothChain + ClothShapeCapsule into FeModel (rcExit={procChain.ExitCode}, Rods={chainStats.Rods}, NB={chainStats.NodeBases}, Stray={chainStats.StrayRadii}, Caps={chainStats.Capsules}, LF={chainStats.LocalForce:F2}, maxForce={chainStats.MaxDynForce:F2}, grav={chainStats.DynGravity:F1})");

                            // Verify WriteProceduralClothProxyDmx auto-extends past short leaf bones for long unweighted hair mesh tails and subdivides > 7.5 in segments
                            int leafIdxInTest = testBundle.BoneNames.FindIndex(b => b.Equals("haircenter_002", StringComparison.OrdinalIgnoreCase));
                            if (leafIdxInTest >= 0)
                            {
                                testBundle.Submeshes.Add(new CustomModelExporter.CustomModelExportBundle.SubmeshData
                                {
                                    SubmeshName = "long_tail_hair",
                                    Vertices = new[] { new Vector3(-0.1651f, 0.40f, 0f) }, // Godot Y=0.40m -> Source Z=15.75 in (40.27 in below leaf haircenter_002 Z=56.02)
                                    Normals = new[] { Vector3.Back },
                                    UVs = new[] { Vector2.Zero },
                                    Indices = new[] { 0 },
                                    BoneIndices = new[] { leafIdxInTest, 0, 0, 0 },
                                    BoneWeights = new[] { 1.0f, 0f, 0f, 0f }
                                });
                            }
                            bool tailDmxOk = CustomModelExporter.WriteProceduralClothProxyDmx(dmxPath, "models/test/haze_cloth_proxy.dmx", testBundle, smdBones);
                            testBundle.Submeshes.Clear();
                            string tailVmdlPath = Path.Combine(tmpAddonSrc, "haze_tail.vmdl");
                            File.WriteAllText(tailVmdlPath, CustomModelExporter.GeneratePatchedVmdl(
                                minimalTemplate, testBundle, "models/test", "materials/test",
                                new List<(string, string)> { ("test_mesh", "models/test/test.smd") },
                                new List<string> { "mat" },
                                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "haze_cloth_proxy.dmx" },
                                includeAnimGraph2Nodes: false));
                            var psiTail = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = csdkRc,
                                Arguments = $"-i \"{tailVmdlPath}\"",
                                WorkingDirectory = Path.GetDirectoryName(csdkRc),
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                UseShellExecute = false
                            };
                            var procTail = System.Diagnostics.Process.Start(psiTail);
                            string rcTailOut = procTail.StandardOutput.ReadToEnd() + "\n" + procTail.StandardError.ReadToEnd();
                            procTail.WaitForExit();
                            string compiledTailVmdlC = Path.Combine(tmpAddonDst, "haze_tail.vmdl_c");
                            bool rcTailSuccess = tailDmxOk && procTail.ExitCode == 0 && File.Exists(compiledTailVmdlC);
                            if (!rcTailSuccess) GD.PrintErr($"[CSDK13 TAIL FAIL OUTPUT]\n{rcTailOut}");
                            var tailStats = rcTailSuccess ? ReadCompiledFeModelAndBones(compiledTailVmdlC) : new CompiledFeStats();
                            Check(rcTailSuccess && tailStats.FreeNodes > dmxStats.FreeNodes && tailStats.Rods > dmxStats.Rods,
                                $"WriteProceduralClothProxyDmx extended & subdivided long mesh tail into denser FeModel (Free {dmxStats.FreeNodes}->{tailStats.FreeNodes}, Rods {dmxStats.Rods}->{tailStats.Rods})");

                            // Hybrid ProxyCloth (stitched multi-strand skirt DMX) + ClothChain (ponytail) + JiggleBone (butt) + fitted capsules check
                            var hybridBundle = new CustomModelExporter.CustomModelExportBundle { HeroCodename = "haze" };
                            hybridBundle.BoneNames.AddRange(new[]
                            {
                                "root_motion", "pelvis", "head",
                                "ponytail_01", "ponytail_02",
                                "skirt_l_01", "skirt_l_02",
                                "skirt_r_01", "skirt_r_02",
                                "buttroot_l", "butt_l"
                            });
                            hybridBundle.BoneHierarchy["root_motion"] = "";
                            hybridBundle.BoneHierarchy["pelvis"] = "root_motion";
                            hybridBundle.BoneHierarchy["head"] = "pelvis";
                            hybridBundle.BoneHierarchy["ponytail_01"] = "head";
                            hybridBundle.BoneHierarchy["ponytail_02"] = "ponytail_01";
                            hybridBundle.BoneHierarchy["skirt_l_01"] = "pelvis";
                            hybridBundle.BoneHierarchy["skirt_l_02"] = "skirt_l_01";
                            hybridBundle.BoneHierarchy["skirt_r_01"] = "pelvis";
                            hybridBundle.BoneHierarchy["skirt_r_02"] = "skirt_r_01";
                            hybridBundle.BoneHierarchy["buttroot_l"] = "pelvis";
                            hybridBundle.BoneHierarchy["butt_l"] = "buttroot_l";
                            hybridBundle.BoneRestTransforms["root_motion"] = Transform3D.Identity;
                            hybridBundle.BoneRestTransforms["pelvis"] = new Transform3D(Basis.Identity, new Vector3(0, 1.0f, 0));
                            hybridBundle.BoneRestTransforms["head"] = new Transform3D(Basis.Identity, new Vector3(0, 0.55f, 0));
                            hybridBundle.BoneRestTransforms["ponytail_01"] = new Transform3D(Basis.Identity, new Vector3(0, 0.05f, -0.09f));
                            hybridBundle.BoneRestTransforms["ponytail_02"] = new Transform3D(Basis.Identity, new Vector3(0, -0.14f, -0.02f));
                            hybridBundle.BoneRestTransforms["skirt_l_01"] = new Transform3D(Basis.Identity, new Vector3(-0.14f, -0.05f, 0.02f));
                            hybridBundle.BoneRestTransforms["skirt_l_02"] = new Transform3D(Basis.Identity, new Vector3(-0.04f, -0.22f, 0.02f));
                            hybridBundle.BoneRestTransforms["skirt_r_01"] = new Transform3D(Basis.Identity, new Vector3(0.14f, -0.05f, 0.02f));
                            hybridBundle.BoneRestTransforms["skirt_r_02"] = new Transform3D(Basis.Identity, new Vector3(0.04f, -0.22f, 0.02f));
                            hybridBundle.BoneRestTransforms["buttroot_l"] = new Transform3D(Basis.Identity, new Vector3(-0.08f, 0.04f, -0.08f));
                            hybridBundle.BoneRestTransforms["butt_l"] = new Transform3D(Basis.Identity, new Vector3(0f, 0f, -0.025f));
                            hybridBundle.PreservedCustomBoneNames.UnionWith(new[] { "ponytail_01", "ponytail_02", "skirt_l_01", "skirt_l_02", "skirt_r_01", "skirt_r_02", "buttroot_l", "butt_l" });
                            hybridBundle.CustomFittedWorldCapsules.Add(new CustomModelExporter.CustomModelExportBundle.ClothCollisionCapsuleDef
                            {
                                ParentBone = "head",
                                Point0 = new Vector3(0f, 1.50f, 0f),
                                Radius0 = 0.145f,
                                Point1 = new Vector3(0f, 1.66f, 0f),
                                Radius1 = 0.145f
                            });
                            hybridBundle.CustomFittedWorldCapsules.Add(new CustomModelExporter.CustomModelExportBundle.ClothCollisionCapsuleDef
                            {
                                ParentBone = "pelvis",
                                Point0 = new Vector3(-0.10f, 1.00f, 0f),
                                Radius0 = 0.180f,
                                Point1 = new Vector3(0.10f, 1.00f, 0f),
                                Radius1 = 0.180f
                            });
                            hybridBundle.PreservedChains.Add(new BoneChainDef
                            {
                                ChainId = "c_pony", Name = "ponytail_01", RootSourceBone = "ponytail_01",
                                AnchorHeroBone = "head", Policy = ChainPolicy.PreservePhysicsChain,
                                Simulation = SimulationKind.Cloth, Damping = 0.40f, CollisionRadiusTipInches = 2.6f, StrayRadiusInches = 11.0f,
                                SourceBoneNames = new List<string> { "ponytail_01", "ponytail_02" },
                                AppendedHeroBoneNames = new List<string> { "ponytail_01", "ponytail_02" }
                            });
                            hybridBundle.PreservedChains.Add(new BoneChainDef
                            {
                                ChainId = "c_skirt_l", Name = "skirt_l_01", RootSourceBone = "skirt_l_01",
                                AnchorHeroBone = "pelvis", Policy = ChainPolicy.PreservePhysicsChain,
                                Simulation = SimulationKind.ProxyCloth, Damping = 0.35f, CollisionRadiusTipInches = 3.8f, StrayRadiusInches = 10.0f,
                                SourceBoneNames = new List<string> { "skirt_l_01", "skirt_l_02" },
                                AppendedHeroBoneNames = new List<string> { "skirt_l_01", "skirt_l_02" }
                            });
                            hybridBundle.PreservedChains.Add(new BoneChainDef
                            {
                                ChainId = "c_skirt_r", Name = "skirt_r_01", RootSourceBone = "skirt_r_01",
                                AnchorHeroBone = "pelvis", Policy = ChainPolicy.PreservePhysicsChain,
                                Simulation = SimulationKind.ProxyCloth, Damping = 0.35f, CollisionRadiusTipInches = 3.8f, StrayRadiusInches = 10.0f,
                                SourceBoneNames = new List<string> { "skirt_r_01", "skirt_r_02" },
                                AppendedHeroBoneNames = new List<string> { "skirt_r_01", "skirt_r_02" }
                            });
                            hybridBundle.PreservedChains.Add(new BoneChainDef
                            {
                                ChainId = "c_butt_l", Name = "buttroot_l", RootSourceBone = "buttroot_l",
                                AnchorHeroBone = "pelvis", Policy = ChainPolicy.PreservePhysicsChain,
                                Simulation = SimulationKind.Jiggle, StiffnessRoot = 0.94f, StiffnessTip = 0.84f,
                                Damping = 0.65f, Gravity = 0.25f, CollisionRadiusTipInches = 0.5f, StrayRadiusInches = 1.5f, AngleLimitDeg = 14.0f,
                                SourceBoneNames = new List<string> { "buttroot_l", "butt_l" },
                                AppendedHeroBoneNames = new List<string> { "buttroot_l", "butt_l" }
                            });
                            var hybridSmdBones = CustomModelExporter.ParseSkeletonBonesFromVmdlText(minimalTemplate);
                            foreach (var bn in new[] { ("ponytail_01", 2, new Vector3(0, -3.5f, 2f)), ("ponytail_02", 3, new Vector3(0, -4f, -5.5f)),
                                                       ("skirt_l_01", 1, new Vector3(-5.5f, 0.8f, -2f)), ("skirt_l_02", 5, new Vector3(-1.5f, 0.8f, -8.5f)),
                                                       ("skirt_r_01", 1, new Vector3(5.5f, 0.8f, -2f)), ("skirt_r_02", 7, new Vector3(1.5f, 0.8f, -8.5f)),
                                                       ("buttroot_l", 1, new Vector3(3.2f, -3.1f, 2f)), ("butt_l", 9, new Vector3(0f, -0.8f, 0f)) })
                            {
                                hybridSmdBones.Add(new CustomModelExporter.VmdlSkeletonBone { Index = hybridSmdBones.Count, Name = bn.Item1, ParentIndex = bn.Item2, Origin = bn.Item3, ParentRot = System.Numerics.Quaternion.Identity, HasParentRot = true });
                            }
                            string hybridSmdHeader =
                                "version 1\nnodes\n0 \"root_motion\" -1\n1 \"pelvis\" 0\n2 \"head\" 1\n3 \"ponytail_01\" 2\n4 \"ponytail_02\" 3\n5 \"skirt_l_01\" 1\n6 \"skirt_l_02\" 5\n7 \"skirt_r_01\" 1\n8 \"skirt_r_02\" 7\n9 \"buttroot_l\" 1\n10 \"butt_l\" 9\nend\n" +
                                "skeleton\ntime 0\n0 0 0 0 0 0 0\n1 0 0 39.37 0 0 0\n2 0 0 21.65 0 0 0\n3 0 -3.5 2 0 0 0\n4 0 -4 -5.5 0 0 0\n5 -5.5 0.8 -2 0 0 0\n6 -1.5 0.8 -8.5 0 0 0\n7 5.5 0.8 -2 0 0 0\n8 1.5 0.8 -8.5 0 0 0\n9 3.2 -3.1 2 0 0 0\n10 0 -0.8 0 0 0 0\nend\n";
                            File.WriteAllText(Path.Combine(tmpAddonSrc, "haze_anim_bind.smd"), hybridSmdHeader);
                            string hybridSmd = Path.Combine(tmpAddonSrc, "hybrid.smd");
                            File.WriteAllText(hybridSmd, hybridSmdHeader +
                                "triangles\nmat\n3 0 -3 63 0 0 1 0 0 1 3 1.0\n4 1 -6 56 0 0 1 0.5 0.5 1 4 1.0\n4 -1 -6 56 0 0 1 1 1 1 4 1.0\n" +
                                "mat\n5 -5 0 37 0 0 1 0 0 1 5 1.0\n6 -7 0 29 0 0 1 0.5 0.5 1 6 1.0\n8 7 0 29 0 0 1 1 1 1 8 1.0\n" +
                                "mat\n9 3 -3 41 0 0 1 0 0 1 9 1.0\n10 3.2 -3.9 41 0 0 1 0.5 0.5 1 10 1.0\n10 2.8 -3.9 41 0 0 1 1 1 1 10 1.0\nend\n");
                            string hybridProxyDmx = Path.Combine(tmpAddonSrc, "haze_cloth_proxy.dmx");
                            bool hybridDmxOk = CustomModelExporter.WriteProceduralClothProxyDmx(hybridProxyDmx, "models/test/haze_cloth_proxy.dmx", hybridBundle, hybridSmdBones);
                            string hybridVmdl = CustomModelExporter.GeneratePatchedVmdl(
                                minimalTemplate, hybridBundle, "models/test", "materials/test",
                                new List<(string, string)> { ("hybrid_mesh", "models/test/hybrid.smd") },
                                new List<string> { "mat" },
                                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "haze_cloth_proxy.dmx" },
                                includeAnimGraph2Nodes: false);
                            bool hybridHasAllThree = hybridDmxOk && hybridVmdl.Contains("_class = \"ClothProxyMeshFile\"") &&
                                hybridVmdl.Contains("_class = \"ClothChain\"") &&
                                hybridVmdl.Contains("root_bone = \"ponytail_01\"") &&
                                !hybridVmdl.Contains("\troot_bone = \"skirt_l_01\"") &&
                                !hybridVmdl.Contains("\troot_bone = \"buttroot_l\"") &&
                                !hybridVmdl.Contains("joint_name = \"butt_l\"") &&
                                hybridVmdl.Contains("_class = \"JiggleBoneList\"") &&
                                hybridVmdl.Contains("jiggle_root_bone = \"buttroot_l\"") &&
                                !hybridVmdl.Contains("jiggle_root_bone = \"ponytail_01\"") &&
                                !hybridVmdl.Contains("jiggle_root_bone = \"skirt_l_01\"");
                            string hybridVmdlPath = Path.Combine(tmpAddonSrc, "haze_hybrid.vmdl");
                            File.WriteAllText(hybridVmdlPath, hybridVmdl);
                            var psiHybrid = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = csdkRc, Arguments = $"-i \"{hybridVmdlPath}\"",
                                WorkingDirectory = Path.GetDirectoryName(csdkRc),
                                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
                            };
                            var procHybrid = System.Diagnostics.Process.Start(psiHybrid);
                            string rcHybridOut = procHybrid.StandardOutput.ReadToEnd() + "\n" + procHybrid.StandardError.ReadToEnd();
                            procHybrid.WaitForExit();
                            string compiledHybridVmdlC = Path.Combine(tmpAddonDst, "haze_hybrid.vmdl_c");
                            bool rcHybridOk = procHybrid.ExitCode == 0 && File.Exists(compiledHybridVmdlC);
                            if (!rcHybridOk) GD.PrintErr($"[CSDK13 HYBRID FAIL OUTPUT]\n{rcHybridOut}");
                            var hybridStats = rcHybridOk ? ReadCompiledFeModelAndBones(compiledHybridVmdlC) : new CompiledFeStats();
                            if (!hybridHasAllThree || hybridStats.HasButtClothNode)
                            {
                                GD.PrintErr($"[HYBRID DIAG] dmxOk={hybridDmxOk} proxyFile={hybridVmdl.Contains("_class = \"ClothProxyMeshFile\"")} cc={hybridVmdl.Contains("_class = \"ClothChain\"")} pony={hybridVmdl.Contains("root_bone = \"ponytail_01\"")} noSkirtCC={!hybridVmdl.Contains("root_bone = \"skirt_l_01\"")} noButtCC={!hybridVmdl.Contains("root_bone = \"buttroot_l\"")} jbList={hybridVmdl.Contains("_class = \"JiggleBoneList\"")} jbButt={hybridVmdl.Contains("jiggle_root_bone = \"buttroot_l\"")} noJbPony={!hybridVmdl.Contains("jiggle_root_bone = \"ponytail_01\"")} noJbSkirt={!hybridVmdl.Contains("jiggle_root_bone = \"skirt_l_01\"")} hasButtCCNode={hybridStats.HasButtClothNode}");
                            }
                            Check(hybridHasAllThree && rcHybridOk && hybridStats.Rods > 0 && hybridStats.NodeBases > 0 &&
                                  hybridStats.JiggleCount == 2 && !hybridStats.HasButtClothNode && hybridStats.HasFittedCapsules,
                                $"CSDK13 compiled Hybrid ProxyCloth + ClothChain + JiggleBone (no overlap!) + fitted capsules (Rods={hybridStats.Rods}, NB={hybridStats.NodeBases}, JB={hybridStats.JiggleCount}, ButtCC={hybridStats.HasButtClothNode}, FittedCaps={hybridStats.HasFittedCapsules})");
                        }
                        finally
                        {
                            try { Directory.Delete(@"D:\Reduced_CSDK_13\content\citadel_addons\_dp_verify_cloth", true); } catch { }
                            try { Directory.Delete(@"D:\Reduced_CSDK_13\game\citadel_addons\_dp_verify_cloth", true); } catch { }
                        }
                    }
                }

                // === Captain -> Wraith Finger Alignment & Metacarpal (_meta_) Test ===
                if (File.Exists(captainPath) && wraithEntry != null)
                {
                    GD.Print("=== Captain -> Wraith Finger Alignment & Palm Scaling Test ===");
                    Node3D captainRoot = LoadGlb(captainPath);
                    Node3D wraithRoot = await vpkLoader.LoadHeroModelAsync(wraithEntry);
                    Check(captainRoot != null && wraithRoot != null, "Loaded captain.glb & vanilla Wraith for finger/palm test");
                    if (captainRoot != null && wraithRoot != null)
                    {
                        var wraithSkel = CustomModelAutoRigger.FindSkeleton(wraithRoot);
                        var capOpts = new CustomModelAutoRigger.RiggingOptions
                        {
                            Method = CustomModelAutoRigger.RiggingMethod.PreRiggedSkeletonRemap,
                            Mode = CustomModelAutoRigger.ReplaceMode.FullReplacement,
                            FingerMode = FingerRigMode.DeadlockFingerBones,
                            KeepFingersRigidOnHand = false,
                            PreserveSourceClothingChains = true,
                            ConformToSkeleton = true,
                            AutoSnapArmsToHero = true,
                            AutoSnapLegsToHero = true,
                            AutoSnapHeadToHero = true,
                            AutoAlignHandOrientation = true
                        };
                        var capResult = CustomModelAutoRigger.RigAndBind(captainRoot, wraithRoot, capOpts);
                        Check(capResult.Success, $"Captain -> Wraith RigAndBind succeeded: {capResult.Report?.BuildSummaryLine()}");

                        // Measure weighted vertex centroid for finger_index_meta_L and finger_index_0_L relative to Wraith's target bone
                        int boneIdxMetaL = wraithSkel.FindBone("finger_index_meta_L");
                        int boneIdx0L = wraithSkel.FindBone("finger_index_0_L");
                        int boneIdx1L = wraithSkel.FindBone("finger_index_1_L");
                        Check(boneIdxMetaL >= 0 && boneIdx0L >= 0 && boneIdx1L >= 0, "Resolved Wraith finger_index_meta_L / _0_L / _1_L bones");

                        Vector3 sumMeta = Vector3.Zero; float wMetaSum = 0f;
                        Vector3 sumIdx0 = Vector3.Zero; float wIdx0Sum = 0f;
                        foreach (var rm in CustomModelAutoRigger.CollectMeshes(capResult.ContainerNode))
                        {
                            if (rm?.Mesh == null || rm.Skin == null) continue;
                            var bindToSkel = new int[rm.Skin.GetBindCount()];
                            for (int bi = 0; bi < bindToSkel.Length; bi++)
                                bindToSkel[bi] = wraithSkel.FindBone(rm.Skin.GetBindName(bi));

                            for (int s = 0; s < rm.Mesh.GetSurfaceCount(); s++)
                            {
                                var arr = rm.Mesh.SurfaceGetArrays(s);
                                var verts = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                                var bones = arr[(int)Mesh.ArrayType.Bones].AsInt32Array();
                                var weights = arr[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                                if (verts == null || bones == null || weights == null) continue;
                                int stride = bones.Length / Math.Max(1, verts.Length);
                                for (int v = 0; v < verts.Length; v++)
                                {
                                    for (int k = 0; k < stride; k++)
                                    {
                                        float w = weights[v * stride + k];
                                        if (w <= 0.1f) continue;
                                        int bi = bones[v * stride + k];
                                        int sb = (bi >= 0 && bi < bindToSkel.Length) ? bindToSkel[bi] : bi;
                                        if (sb == boneIdxMetaL) { sumMeta += verts[v] * w; wMetaSum += w; }
                                        else if (sb == boneIdx0L) { sumIdx0 += verts[v] * w; wIdx0Sum += w; }
                                    }
                                }
                            }
                        }

                        Vector3 tgtMetaOrigin = wraithSkel.GetBoneGlobalRest(boneIdxMetaL).Origin;
                        Vector3 tgt0Origin = wraithSkel.GetBoneGlobalRest(boneIdx0L).Origin;
                        Vector3 tgt1Origin = wraithSkel.GetBoneGlobalRest(boneIdx1L).Origin;
                        Vector3 expectedIdx0Mid = tgt0Origin.Lerp(tgt1Origin, 0.5f);
                        float distIdx0 = wIdx0Sum > 0f ? (sumIdx0 / wIdx0Sum).DistanceTo(expectedIdx0Mid) : 99f;
                        float distMetaToKnuckle = wMetaSum > 0f ? (sumMeta / wMetaSum).DistanceTo(tgt0Origin) : 99f;
                        Check(wMetaSum > 0f && wIdx0Sum > 0f && distIdx0 < 0.030f && distMetaToKnuckle < 0.055f,
                            $"Captain -> Wraith Left Index alignment close to target (idx0CentroidErr={distIdx0 * 100f:F2}cm, metaToKnuckle={distMetaToKnuckle * 100f:F2}cm)");

                        // Also confirm CustomJiggleBoneDriver chains exist on Captain -> Wraith & BuildExportBundle preserves normalized chains + mesh-fitted colliders
                        var capJiggle = wraithRoot.FindChildren("*", "Node", true, false).OfType<CustomJiggleBoneDriver>().FirstOrDefault();
                        var capChainDefs = capJiggle?.GetActiveChainDefinitions();
                        var capBundle = CustomModelExporter.BuildExportBundle(
                            CustomModelAutoRigger.CollectMeshes(capResult.ContainerNode),
                            wraithSkel, "wraith", preservedChains: capChainDefs);
                        Check(capBundle.PreservedChains.Count > 0 && !capBundle.PreservedCustomBoneNames.Any(n => n.Contains('.')),
                            $"Captain -> Wraith BuildExportBundle exported {capBundle.PreservedChains.Count} physics chain(s) with normalized SMD-safe bone names");

                        bool hasFittedCoreCapsules = capJiggle != null && capJiggle.ActiveColliderCount >= 12 &&
                            capBundle.CustomFittedWorldCapsules.Count >= 12 &&
                            capBundle.CustomFittedWorldCapsules.Any(c => c.ParentBone.Equals("head", StringComparison.OrdinalIgnoreCase)) &&
                            capBundle.CustomFittedWorldCapsules.Any(c => c.ParentBone.Equals("spine_3", StringComparison.OrdinalIgnoreCase)) &&
                            capBundle.CustomFittedWorldCapsules.Any(c => c.ParentBone.Equals("pelvis", StringComparison.OrdinalIgnoreCase)) &&
                            capBundle.CustomFittedWorldCapsules.Any(c => c.ParentBone.Equals("leg_upper_L", StringComparison.OrdinalIgnoreCase)) &&
                            capBundle.CustomFittedWorldCapsules.Any(c => c.ParentBone.Equals("leg_lower_R", StringComparison.OrdinalIgnoreCase));
                        Check(hasFittedCoreCapsules,
                            $"BodyColliderAutoFitter fitted {capBundle.CustomFittedWorldCapsules.Count} torso+head+arm+leg mesh capsules (driver active={capJiggle?.ActiveColliderCount ?? 0})");

                        // Hair Scale Integrity Check (fixes hair shrinking during physics parameter tweaks / anim playback)
                        float maxScaleDrift = 0f;
                        foreach (var ch in capChainDefs ?? new List<BoneChainDef>())
                        {
                            foreach (string bn in ch.AppendedHeroBoneNames)
                            {
                                int bIdx = wraithSkel.FindBone(bn);
                                if (bIdx < 0) continue;
                                float drift = wraithSkel.GetBonePoseScale(bIdx).DistanceTo(wraithSkel.GetBoneRest(bIdx).Basis.Scale);
                                if (drift > maxScaleDrift) maxScaleDrift = drift;
                            }
                        }
                        if (capJiggle != null && capChainDefs != null)
                        {
                            // Also verify in-place parameter update works without altering bone scale or resetting driver
                            foreach (var c in capChainDefs) { c.CollisionRadiusTipInches = 3.0f; c.StrayRadiusInches = 11.5f; }
                            capJiggle.UpdateChainParametersInPlace(capChainDefs);
                            for (int f = 0; f < 10; f++) capJiggle.StepSimulation(1f / 60f);
                            foreach (var ch in capChainDefs)
                            {
                                foreach (string bn in ch.AppendedHeroBoneNames)
                                {
                                    int bIdx = wraithSkel.FindBone(bn);
                                    if (bIdx < 0) continue;
                                    float drift = wraithSkel.GetBonePoseScale(bIdx).DistanceTo(wraithSkel.GetBoneRest(bIdx).Basis.Scale);
                                    if (drift > maxScaleDrift) maxScaleDrift = drift;
                                }
                            }
                        }
                        Check(maxScaleDrift < 0.001f,
                            $"Appended physics bones preserve Rest.Basis.Scale on Bind & StepSimulation (zero hair shrink drift={maxScaleDrift:F5})");

                        // Smart Auto-Detect Mode Classifier test
                        {
                            var probeChains = new List<BoneChainDef>
                            {
                                new() { ChainId = "s1", Name = "Skirt_F", RootSourceBone = "Skirt_F", AnchorHeroBone = "pelvis", SourceBoneNames = new() { "Skirt_F", "Skirt_F2" } },
                                new() { ChainId = "s2", Name = "Skirt_B", RootSourceBone = "Skirt_B", AnchorHeroBone = "pelvis", SourceBoneNames = new() { "Skirt_B", "Skirt_B2" } },
                                new() { ChainId = "h1", Name = "HairBack_L", RootSourceBone = "HairBack_L", AnchorHeroBone = "head", SourceBoneNames = new() { "HairBack_L", "HairBack_L2" } },
                                new() { ChainId = "h2", Name = "HairBack_C", RootSourceBone = "HairBack_C", AnchorHeroBone = "head", SourceBoneNames = new() { "HairBack_C", "HairBack_C2" } },
                                new() { ChainId = "h3", Name = "HairBack_R", RootSourceBone = "HairBack_R", AnchorHeroBone = "head", SourceBoneNames = new() { "HairBack_R", "HairBack_R2" } },
                                new() { ChainId = "p1", Name = "Ponytail", RootSourceBone = "Ponytail", AnchorHeroBone = "head", SourceBoneNames = new() { "Ponytail_1", "Ponytail_2" } },
                                new() { ChainId = "e1", Name = "Ear_L", RootSourceBone = "Ear_L", AnchorHeroBone = "head", SourceBoneNames = new() { "Ear_L" } },
                                new() { ChainId = "b1", Name = "HairBang", RootSourceBone = "HairBang", AnchorHeroBone = "head", SourceBoneNames = new() { "HairBang" } },
                                new() { ChainId = "gl1", Name = "butt_l", RootSourceBone = "buttroot_l", AnchorHeroBone = "pelvis", SourceBoneNames = new() { "buttroot_l", "butt_l" } },
                                new() { ChainId = "br1", Name = "breast_l", RootSourceBone = "breast_l", AnchorHeroBone = "spine_3", SourceBoneNames = new() { "breast_l", "breastjiggle_l" } },
                            };
                            RigBoneClassifier.ApplyRecommendedPhysicsModes(probeChains, enableCustomPhysics: true, resetTuningValues: true);
                            Check(probeChains[0].Simulation == SimulationKind.ProxyCloth &&
                                  probeChains[1].Simulation == SimulationKind.ProxyCloth &&
                                  probeChains[2].Simulation == SimulationKind.ProxyCloth &&
                                  probeChains[5].Simulation == SimulationKind.Cloth &&
                                  probeChains[6].Simulation == SimulationKind.Jiggle &&
                                  probeChains[7].Policy == ChainPolicy.CollapseToNearestCore &&
                                  probeChains[8].Simulation == SimulationKind.Jiggle && probeChains[8].StrayRadiusInches <= 2.0f && probeChains[8].AngleLimitDeg <= 16.0f &&
                                  probeChains[9].Simulation == SimulationKind.Jiggle && probeChains[9].AngleLimitDeg <= 22.0f,
                                "RigBoneClassifier auto-detects ProxyCloth (skirt & multi-strand back hair), Cloth (ponytail), Jiggle (ear/butt/breast with tight bounds), & Static (bang)");
                        }

                        // Crouch-pose collision deflection test: bend spine forward sharply so back torso is underneath hair chains
                        var fittedCaps = capOpts.BodyCollisionCapsules;
                        if (capJiggle != null && fittedCaps != null && fittedCaps.Count > 0)
                        {
                            // Compute character forward lean axis using shoulders (arms L -> R) and horizontal hair side
                            int armL = wraithSkel.FindBone("arm_upper_L");
                            int armR = wraithSkel.FindBone("arm_upper_R");
                            int pelvisIdx = wraithSkel.FindBone("pelvis");
                            int headIdxC = wraithSkel.FindBone("head");
                            Vector3 latAxis = (armL >= 0 && armR >= 0)
                                ? (wraithSkel.GetBoneGlobalRest(armL).Origin - wraithSkel.GetBoneGlobalRest(armR).Origin).Normalized()
                                : Vector3.Right;

                            // Determine which horizontal sign leans head AWAY from back hair (so pelvis/spine sit underneath hanging hair)
                            Vector3 hairAvg = Vector3.Zero; int hairSamples = 0;
                            foreach (var ch in capChainDefs ?? new List<BoneChainDef>())
                            {
                                if (ch.Policy != ChainPolicy.PreservePhysicsChain || !ch.Name.Contains("hair", StringComparison.OrdinalIgnoreCase)) continue;
                                foreach (string bn in ch.AppendedHeroBoneNames)
                                {
                                    int bi = wraithSkel.FindBone(bn);
                                    if (bi >= 0) { hairAvg += wraithSkel.GetBoneGlobalRest(bi).Origin; hairSamples++; }
                                }
                            }
                            Vector3 pelvisPos = pelvisIdx >= 0 ? wraithSkel.GetBoneGlobalRest(pelvisIdx).Origin : Vector3.Zero;
                            Vector3 hairDirHoriz = hairSamples > 0 ? ((hairAvg / hairSamples) - pelvisPos) : Vector3.Back;
                            hairDirHoriz.Y = 0;
                            float leanSign = 1f;
                            if (hairDirHoriz.LengthSquared() > 1e-5f)
                            {
                                Vector3 testFwd = new Quaternion(latAxis, 0.4f) * Vector3.Up;
                                // We want spine top (+Up rotated) to lean OPPOSITE to hair side (forward crouch!)
                                if (testFwd.Dot(hairDirHoriz.Normalized()) > 0f) leanSign = -1f;
                            }

                            void ApplyTestCrouchPose()
                            {
                                wraithSkel.ResetBonePoses();
                                foreach (string sp in new[] { "spine_1", "spine_2", "spine_3" })
                                {
                                    int idx = wraithSkel.FindBone(sp);
                                    if (idx < 0) continue;
                                    int pIdx = wraithSkel.GetBoneParent(idx);
                                    Basis pBasis = pIdx >= 0 ? wraithSkel.GetBoneGlobalPose(pIdx).Basis : Basis.Identity;
                                    Vector3 localAxis = (pBasis.Inverse() * latAxis).Normalized();
                                    Quaternion qDelta = new Quaternion(localAxis, leanSign * 0.34f);
                                    wraithSkel.SetBonePoseRotation(idx, qDelta * wraithSkel.GetBoneRest(idx).Basis.GetRotationQuaternion());
                                }
                                if (headIdxC >= 0)
                                {
                                    int hpIdx = wraithSkel.GetBoneParent(headIdxC);
                                    Basis hpBasis = hpIdx >= 0 ? wraithSkel.GetBoneGlobalPose(hpIdx).Basis : Basis.Identity;
                                    Vector3 hLocalAxis = (hpBasis.Inverse() * latAxis).Normalized();
                                    wraithSkel.SetBonePoseRotation(headIdxC, new Quaternion(hLocalAxis, -leanSign * 0.72f) * wraithSkel.GetBoneRest(headIdxC).Basis.GetRotationQuaternion());
                                }
                            }

                            static float DistToSeg(Vector3 pt, Vector3 a, Vector3 b, out float segT)
                            {
                                Vector3 ab = b - a;
                                float lenSq = ab.LengthSquared();
                                segT = lenSq > 1e-8f ? Mathf.Clamp((pt - a).Dot(ab) / lenSq, 0f, 1f) : 0f;
                                return pt.DistanceTo(a + ab * segT);
                            }

                            float MeasureMaxTorsoPenetration()
                            {
                                float worstPen = 0f;
                                var torsoCaps = fittedCaps.Where(c =>
                                    c.Enabled && (c.BoneName.StartsWith("spine_", StringComparison.OrdinalIgnoreCase) ||
                                                  c.BoneName.Equals("pelvis", StringComparison.OrdinalIgnoreCase))).ToList();
                                foreach (var ch in capChainDefs ?? new List<BoneChainDef>())
                                {
                                    if (ch.Policy != ChainPolicy.PreservePhysicsChain || ch.AppendedHeroBoneNames.Count < 2) continue;
                                    bool isHeadHair = ch.AnchorHeroBone.Equals("head", StringComparison.OrdinalIgnoreCase) ||
                                                      ch.AnchorHeroBone.Equals("neck_0", StringComparison.OrdinalIgnoreCase) ||
                                                      ch.Name.Contains("hair", StringComparison.OrdinalIgnoreCase);
                                    if (!isHeadHair) continue;
                                    for (int bi = 1; bi < ch.AppendedHeroBoneNames.Count; bi++)
                                    {
                                        int bIdx = wraithSkel.FindBone(ch.AppendedHeroBoneNames[bi]);
                                        if (bIdx < 0) continue;
                                        Vector3 wp = wraithSkel.GetBoneGlobalPose(bIdx).Origin;
                                        foreach (var tc in torsoCaps)
                                        {
                                            int tb = wraithSkel.FindBone(tc.BoneName);
                                            if (tb < 0) continue;
                                            Transform3D tbPose = wraithSkel.GetBoneGlobalPose(tb);
                                            Vector3 c0 = tbPose * tc.EffectivePoint0;
                                            Vector3 c1 = tbPose * tc.EffectivePoint1;
                                            float d = DistToSeg(wp, c0, c1, out float u);
                                            float r = Mathf.Lerp(tc.EffectiveRadius0, tc.EffectiveRadius1, u);
                                            float pen = (r + 0.018f) - d;
                                            if (pen > worstPen) worstPen = pen;
                                        }
                                    }
                                }
                                return worstPen;
                            }

                            // Pass 1: With colliders OFF
                            capJiggle.RegisterCollisionCapsules(Array.Empty<BodyCollisionCapsuleDef>());
                            ApplyTestCrouchPose();
                            for (int f = 0; f < 45; f++) capJiggle.StepSimulation(1f / 60f);
                            float penWithoutColliders = MeasureMaxTorsoPenetration();

                            // Pass 2: With mesh-fitted colliders ON
                            capJiggle.RegisterCollisionCapsules(fittedCaps);
                            ApplyTestCrouchPose();
                            for (int f = 0; f < 45; f++) capJiggle.StepSimulation(1f / 60f);
                            float penWithColliders = MeasureMaxTorsoPenetration();

                            Check(penWithoutColliders > 0.015f && penWithColliders < 0.008f,
                                $"Crouch pose body collider deflection keeps hair tips out of torso (penNoCol={penWithoutColliders * 100f:F2}cm -> penWithCol={penWithColliders * 100f:F2}cm)");

                            // Verify GeneratePatchedVmdl exports fitted ClothShapeCapsules (at least 4 distinct capsules across head/spine/pelvis)
                            string capTemplate =
                                "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc39:version{1ceecb0d-4093-4729-ab18-b76886c64cde} -->\n" +
                                "{\n\trootNode =\n\t{\n\t\t_class = \"RootNode\"\n\t\tchildren =\n\t\t[\n" +
                                "\t\t\t{\n\t\t\t\t_class = \"Skeleton\"\n\t\t\t\tchildren =\n\t\t\t\t[\n" +
                                "\t\t\t\t\t{\n\t\t\t\t\t\t_class = \"Bone\"\n\t\t\t\t\t\tname = \"pelvis\"\n\t\t\t\t\t\torigin = [ 0.0, 0.0, 39.37 ]\n\t\t\t\t\t\tangles = [ 0.0, 0.0, 0.0 ]\n" +
                                "\t\t\t\t\t\tchildren =\n\t\t\t\t\t\t[\n" +
                                "\t\t\t\t\t\t\t{\n\t\t\t\t\t\t\t\t_class = \"Bone\"\n\t\t\t\t\t\t\t\tname = \"spine_0\"\n\t\t\t\t\t\t\t\torigin = [ 0.0, 0.0, 5.0 ]\n\t\t\t\t\t\t\t\tangles = [ 0.0, 0.0, 0.0 ]\n" +
                                "\t\t\t\t\t\t\t\tchildren =\n\t\t\t\t\t\t\t\t[\n" +
                                "\t\t\t\t\t\t\t\t\t{\n\t\t\t\t\t\t\t\t\t\t_class = \"Bone\"\n\t\t\t\t\t\t\t\t\t\tname = \"spine_1\"\n\t\t\t\t\t\t\t\t\t\torigin = [ 0.0, 0.0, 5.0 ]\n\t\t\t\t\t\t\t\t\t\tangles = [ 0.0, 0.0, 0.0 ]\n" +
                                "\t\t\t\t\t\t\t\t\t\tchildren =\n\t\t\t\t\t\t\t\t\t\t[\n" +
                                "\t\t\t\t\t\t\t\t\t\t\t{\n\t\t\t\t\t\t\t\t\t\t\t\t_class = \"Bone\"\n\t\t\t\t\t\t\t\t\t\t\t\tname = \"spine_2\"\n\t\t\t\t\t\t\t\t\t\t\t\torigin = [ 0.0, 0.0, 5.0 ]\n\t\t\t\t\t\t\t\t\t\t\t\tangles = [ 0.0, 0.0, 0.0 ]\n" +
                                "\t\t\t\t\t\t\t\t\t\t\t\tchildren =\n\t\t\t\t\t\t\t\t\t\t\t\t[\n" +
                                "\t\t\t\t\t\t\t\t\t\t\t\t\t{\n\t\t\t\t\t\t\t\t\t\t\t\t\t\t_class = \"Bone\"\n\t\t\t\t\t\t\t\t\t\t\t\t\t\tname = \"spine_3\"\n\t\t\t\t\t\t\t\t\t\t\t\t\t\torigin = [ 0.0, 0.0, 5.0 ]\n\t\t\t\t\t\t\t\t\t\t\t\t\t\tangles = [ 0.0, 0.0, 0.0 ]\n" +
                                "\t\t\t\t\t\t\t\t\t\t\t\t\t\tchildren =\n\t\t\t\t\t\t\t\t\t\t\t\t\t\t[\n" +
                                "\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t{\n\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t_class = \"Bone\"\n\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t\tname = \"head\"\n\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t\torigin = [ 0.0, 0.0, 8.0 ]\n\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t\tangles = [ 0.0, 0.0, 0.0 ]\n\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t},\n" +
                                "\t\t\t\t\t\t\t\t\t\t\t\t\t\t]\n" +
                                "\t\t\t\t\t\t\t\t\t\t\t\t\t},\n" +
                                "\t\t\t\t\t\t\t\t\t\t\t\t]\n" +
                                "\t\t\t\t\t\t\t\t\t\t\t},\n" +
                                "\t\t\t\t\t\t\t\t\t\t]\n" +
                                "\t\t\t\t\t\t\t\t\t},\n" +
                                "\t\t\t\t\t\t\t\t]\n" +
                                "\t\t\t\t\t\t\t},\n" +
                                "\t\t\t\t\t\t]\n" +
                                "\t\t\t\t\t},\n" +
                                "\t\t\t\t]\n\t\t\t},\n" +
                                "\t\t]\n\t}\n}";
                            string capVmdl = CustomModelExporter.GeneratePatchedVmdl(
                                capTemplate, capBundle, "models/test_cap", "materials/test_cap",
                                new List<(string, string)> { ("cap_mesh", "models/test_cap/cap.smd") },
                                new List<string> { "mat" },
                                availableSubfileNames: null,
                                includeAnimGraph2Nodes: false);
                            int exportedCapCount = System.Text.RegularExpressions.Regex.Matches(capVmdl, "_class = \"ClothShapeCapsule\"").Count;
                            Check(exportedCapCount >= 4 && capVmdl.Contains("\"head\"") && capVmdl.Contains("\"spine_3\"") && capVmdl.Contains("\"pelvis\""),
                                $"CustomModelExporter emitted {exportedCapCount} mesh-fitted ClothShapeCapsule blocks in Source 2 .vmdl");
                        }

                        wraithRoot.QueueFree();
                        captainRoot.QueueFree();
                    }
                }

                // Preset & Workspace Session collider + options round-trip
                var gathered = modal.GatherRiggingOptions();
                gathered.FingerMode = FingerRigMode.GenerateFingerChains;
                gathered.PreserveSourceClothingChains = true;
                gathered.BodyCollisionCapsules = new List<BodyCollisionCapsuleDef>
                {
                    new()
                    {
                        BoneName = "spine_3",
                        DisplayName = "Upper Chest / Back",
                        Point0Local = new Vector3(0f, -0.04f, 0.01f),
                        Radius0Meters = 0.145f,
                        Point1Local = new Vector3(0f, 0.09f, 0.02f),
                        Radius1Meters = 0.155f,
                        RadiusScale = 1.35f,
                        LocalOffsetMeters = new Vector3(0f, 0f, -0.025f),
                        Enabled = false,
                        IsUserEdited = true
                    }
                };
                string testPresetKey = "verify_overhaul_preset";
                bool savedOk = ImporterPresetStore.SavePreset(testPresetKey, gathered, arpPath);
                var loadedPreset = ImporterPresetStore.LoadPreset(testPresetKey);
                var loadedCap = loadedPreset?.BodyCollisionCapsules?.FirstOrDefault();
                var wsEntry = DeadlockPlayground.Workspace.WorkspaceCustomModelEntry.FromRiggingOptions("hero_wraith", arpPath, gathered);
                var wsBack = wsEntry.ToRiggingOptions();
                var wsCap = wsBack?.BodyCollisionCapsules?.FirstOrDefault();
                Check(savedOk && loadedPreset != null && loadedPreset.FingerMode == FingerRigMode.GenerateFingerChains &&
                      loadedPreset.PreserveSourceClothingChains &&
                      loadedCap != null && Mathf.IsEqualApprox(loadedCap.RadiusScale, 1.35f) &&
                      Mathf.IsEqualApprox(loadedCap.LocalOffsetMeters.Z, -0.025f) && !loadedCap.Enabled && loadedCap.IsUserEdited &&
                      wsCap != null && Mathf.IsEqualApprox(wsCap.RadiusScale, 1.35f) &&
                      Mathf.IsEqualApprox(wsCap.LocalOffsetMeters.Z, -0.025f) && !wsCap.Enabled && wsCap.IsUserEdited,
                    "ImporterPresetStore & WorkspaceBodyColliderEntry saved/loaded edited capsule scale, offset & enabled state cleanly");

                // Clean up test preset file
                string presetFile = Path.Combine(ImporterPresetStore.GetPresetsDirectory(), testPresetKey + ".json");
                if (File.Exists(presetFile)) File.Delete(presetFile);

                // UI Overhaul Verification: Option reachability + Min 14 px font size
                {
                    var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                    string[] requiredBackingFields =
                    {
                        "_optRiggingMethod", "_optFingerRigMode", "_optReplaceMode",
                        "_chkEnableCustomPhysics", "_chkConformToSkeleton", "_chkBindSymmetry",
                        "_chkFitSpine", "_chkLooseMeshesRigid", "_chkExtraTwistBones",
                        "_chkKeepUnweightedBones", "_rigStepper", "_replaceModeCards", "_meshThumbService",
                        "_btnToggleColliders", "_btnPhysTabChains", "_btnPhysTabColliders",
                        "_boxColliderInspector", "_sliderColRadiusScale",
                        "_sliderSelColRadius", "_sliderSelStrayRadius",
                        "_chkBoneFilterCore", "_chkBoneFilterPhysics",
                        "_boxTab3WeightPaintHost", "_btnChainWeightPaintToggle", "_lblSelChainSimHint"
                    };
                    bool allReachable = requiredBackingFields.All(fn =>
                        typeof(CustomModelImporterModalUI).GetField(fn, flags)?.GetValue(modal) != null);
                    Check(allReachable &&
                          CustomWeightPaintEditor.MirrorBoneName("hairmiddle_l_001") == "hairmiddle_r_001" &&
                          CustomWeightPaintEditor.MirrorBoneName("butt_r") == "butt_l",
                        "UI Overhaul: All Rig/Meshes/Bones/Wizard/Filter/Tab3WeightPaint controls reachable & ARP L/R mirroring works");

                    var minFontViolations = new List<string>();
                    void WalkLabels(Node node)
                    {
                        if (node is Label lbl && lbl.HasThemeFontSizeOverride("font_size"))
                        {
                            int sz = lbl.GetThemeFontSize("font_size");
                            if (sz < ImporterStyle.FontHint)
                                minFontViolations.Add($"{lbl.Name}({sz}px)");
                        }
                        foreach (Node child in node.GetChildren()) WalkLabels(child);
                    }
                    WalkLabels(modal);
                    Check(minFontViolations.Count == 0,
                        $"UI Overhaul: All modal Labels >= {ImporterStyle.FontHint}px (violations: {string.Join(", ", minFontViolations)})");
                }

                modal.QueueFree();
                if (GodotObject.IsInstanceValid(heroNode)) heroNode.QueueFree();
                vpkLoader.QueueFree();
                await ToSignal(this, SignalName.ProcessFrame);

                GD.Print($"SUMMARY: {_passed} passed, {_failed} failed.");
            }
            catch (Exception ex)
            {
                _failed++;
                GD.PrintErr($"Unhandled exception in ImporterOverhaulVerificationTest: {ex}");
            }

            Quit(_failed == 0 ? 0 : 1);
        }

        private sealed class CompiledFeStats
        {
            public int Rods = -1;
            public int FreeNodes = -1;
            public int NodeBases = -1;
            public List<string> BoneNames = new();
            public float DynGravity = -1f;
            public float DynDamping = -1f;
            public int Capsules;
            public int StrayRadii;
            public float LocalForce = -1f;
            public float MaxDynForce = -1f;
            public int JiggleCount;
            public bool HasButtClothNode;
            public bool HasFittedCapsules;
        }

        private static CompiledFeStats ReadCompiledFeModelAndBones(string vmdlCPath)
        {
            var stats = new CompiledFeStats();
            if (string.IsNullOrEmpty(vmdlCPath) || !File.Exists(vmdlCPath)) return stats;
            using var r = new ValveResourceFormat.Resource { FileName = "models/test/haze.vmdl" };
            r.Read(vmdlCPath);
            var mdl = r.DataBlock as ValveResourceFormat.ResourceTypes.Model;
            var fe = mdl?.GetEmbeddedPhys()?.FeModel;
            stats.BoneNames = mdl?.Skeleton?.Bones?.Select(b => b.Name).ToList() ?? new List<string>();
            if (fe == null) return stats;
            float dynGrav = -1f, dynDamp = -1f, maxDynForce = 0f;
            if (fe.NodeIntegrator != null && fe.NodeIntegrator.Length > fe.StaticNodes)
            {
                var ni = fe.NodeIntegrator[fe.StaticNodes];
                dynGrav = ni.Gravity;
                dynDamp = ni.PointDamping;
                for (int i = fe.StaticNodes; i < fe.NodeIntegrator.Length; i++)
                {
                    if (fe.NodeIntegrator[i].AnimationForceAttraction > maxDynForce)
                        maxDynForce = fe.NodeIntegrator[i].AnimationForceAttraction;
                }
            }
            stats.Rods = fe.Rods.Length;
            stats.FreeNodes = fe.FreeNodes.Length;
            stats.NodeBases = fe.NodeBases.Length;
            stats.DynGravity = dynGrav;
            stats.DynDamping = dynDamp;
            stats.Capsules = fe.TaperedCapsuleRigids?.Length ?? 0;
            stats.StrayRadii = fe.AnimStrayRadii?.Length ?? 0;
            stats.LocalForce = fe.LocalForce;
            stats.MaxDynForce = maxDynForce;
            stats.JiggleCount = fe.JiggleBones?.Length ?? 0;
            stats.HasButtClothNode = fe.CtrlName != null && fe.CtrlName.Any(n => n != null && n.StartsWith("$ccbutt", StringComparison.OrdinalIgnoreCase));
            stats.HasFittedCapsules = fe.TaperedCapsuleRigids != null &&
                fe.TaperedCapsuleRigids.Any(c => c.Spheres != null && c.Spheres.Length >= 2 && MathF.Abs(c.Spheres[0].W - (0.145f * 39.3701f)) < 0.15f) &&
                fe.TaperedCapsuleRigids.Any(c => c.Spheres != null && c.Spheres.Length >= 2 && MathF.Abs(c.Spheres[0].W - (0.180f * 39.3701f)) < 0.15f);
            return stats;
        }

        private static RigAnalysis AnalyzeSkeleton(Skeleton3D skel, HashSet<int> weighted)
        {
            int n = skel.GetBoneCount();
            var names = new string[n];
            var parents = new int[n];
            var pos = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                names[i] = skel.GetBoneName(i);
                parents[i] = skel.GetBoneParent(i);
                pos[i] = skel.GetBoneGlobalPose(i).Origin;
            }
            return RigBoneClassifier.Analyze(names, parents, familyOverride: null, weightedBones: weighted, boneWorldPositions: pos);
        }
    }
}
