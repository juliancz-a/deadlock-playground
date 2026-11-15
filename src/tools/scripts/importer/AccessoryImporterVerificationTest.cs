using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using DeadlockPlayground.Catalog;
using DeadlockPlayground.Importer;
using DeadlockPlayground.Materials;
using DeadlockPlayground.Painter;
using DeadlockPlayground.Workspace;

namespace DeadlockPlayground.Tests
{
    public partial class AccessoryImporterVerificationTest : SceneTree
    {
        private static Node3D CreateSyntheticHatScene(string name, Color tint, bool withDetailTextures)
        {
            var root = new Node3D { Name = name };
            var box = new BoxMesh { Size = new Vector3(0.24f, 0.16f, 0.24f) };

            var baseImg = Image.CreateEmpty(16, 16, false, Image.Format.Rgba8);
            baseImg.Fill(tint);
            var baseTex = ImageTexture.CreateFromImage(baseImg);

            var mat = new StandardMaterial3D
            {
                ResourceName = $"{name}_mat",
                AlbedoTexture = baseTex,
                Roughness = 0.55f
            };

            if (withDetailTextures)
            {
                var nrmImg = Image.CreateEmpty(16, 16, false, Image.Format.Rgba8);
                nrmImg.Fill(new Color(0.5f, 0.5f, 1.0f, 1.0f));
                mat.NormalEnabled = true;
                mat.NormalTexture = ImageTexture.CreateFromImage(nrmImg);

                var ormImg = Image.CreateEmpty(16, 16, false, Image.Format.Rgba8);
                ormImg.Fill(new Color(0.85f, 0.45f, 0.1f, 1.0f));
                var ormTex = ImageTexture.CreateFromImage(ormImg);
                mat.RoughnessTexture = ormTex;
                mat.RoughnessTextureChannel = BaseMaterial3D.TextureChannel.Green;
                mat.AOTexture = ormTex;
                mat.AOTextureChannel = BaseMaterial3D.TextureChannel.Red;
            }

            var mi = new MeshInstance3D
            {
                Name = $"{name}_mesh",
                Mesh = box
            };
            mi.SetSurfaceOverrideMaterial(0, mat);
            root.AddChild(mi);
            return root;
        }

        public override async void _Initialize()
        {
            GD.Print("=======================================================");
            GD.Print("   ACCESSORY & OUTFIT PART IMPORTER VERIFICATION TEST  ");
            GD.Print("=======================================================");

            bool allPassed = true;

            try
            {
                string vpkPath = DeadlockPlayground.Particles.HeroParticleService.ResolveVpkPath();
                var vpkLoader = new VpkLoaderTest { Name = "VpkLoaderTest", VpkPath = vpkPath };
                Root.AddChild(vpkLoader);

                var priestEntry = DeadlockHeroCatalog.GetByCodename("priest") ?? DeadlockHeroCatalog.Entries.FirstOrDefault();
                if (priestEntry == null)
                    throw new Exception("Could not find 'priest' or fallback hero in catalog.");

                Node3D heroNode = await vpkLoader.LoadHeroModelAsync(priestEntry);
                if (heroNode == null)
                    throw new Exception("Failed to load priest hero model from VPK.");

                var hierarchy = new HeroMeshHierarchy { Name = "HeroMeshHierarchy" };
                Root.AddChild(hierarchy);
                hierarchy.ScanHero(heroNode);

                int initialVanillaSubmeshCount = hierarchy.Submeshes.Count;
                GD.Print($"[Test] Loaded '{priestEntry.InternalCodename}' with {initialVanillaSubmeshCount} scanned submeshes.");

                // =========================================================================
                // SCENARIO A: Add New Standalone Rigid Hat Accessory + Channel Discovery + Export
                // =========================================================================
                var hatScene = CreateSyntheticHatScene("WizardHat", new Color(0.25f, 0.35f, 0.85f), withDetailTextures: true);
                var hatOpts = new CustomModelAutoRigger.RiggingOptions
                {
                    SlotId = "acc_hat",
                    AccessoryName = "Wizard Hat",
                    Intent = CustomModelAutoRigger.AccessoryIntent.AddAccessory,
                    Mode = CustomModelAutoRigger.ReplaceMode.PartialAccessory,
                    Method = CustomModelAutoRigger.RiggingMethod.RigidSingleBone,
                    RigidBoneName = "head",
                    AutoSnapHeadToHero = true,
                    RootPosition = new Vector3(0, 0.14f, 0),
                    RootScale = 1.0f,
                    IsPreviewOnly = false
                };

                var hatRes = CustomModelAutoRigger.RigAndBind(hatScene, heroNode, hatOpts);
                bool aRigOk = hatRes.Success && hatRes.CustomSubmeshes.Count == 1;
                var hatContainer = heroNode.FindChild("Custom_Accessory_acc_hat", true, false) as Node3D;
                bool aLayer21Clean = hatContainer != null && hatRes.CustomSubmeshes.All(m => (m.Layers & (1u << 20)) == 0);

                hierarchy.ScanHero(heroNode);
                var hatScanned = hierarchy.Submeshes.FirstOrDefault(s =>
                    s.IsCustomImportedMesh &&
                    s.Mesh != null && s.Mesh.HasMeta("AccessorySlotId") &&
                    s.Mesh.GetMeta("AccessorySlotId").AsString() == "acc_hat");
                bool aVanillaUnchanged = hierarchy.Submeshes.Count(s => !s.IsCustomImportedMesh) == initialVanillaSubmeshCount;

                var discoveredChannels = MaterialChannelDiscovery.DiscoverCustomModelChannels(
                    hatRes.CustomSubmeshes[0].GetSurfaceOverrideMaterial(0) ?? hatRes.CustomSubmeshes[0].GetActiveMaterial(0),
                    "wizard_hat_mat");
                bool aChannelsOk = discoveredChannels != null
                    && discoveredChannels.HasRealMap(MaterialTextureChannel.Color)
                    && discoveredChannels.HasRealMap(MaterialTextureChannel.Normal)
                    && discoveredChannels.HasRealMap(MaterialTextureChannel.Roughness)
                    && discoveredChannels.HasRealMap(MaterialTextureChannel.AmbientOcclusion);

                var session = ModWorkspaceSession.Instance;
                string tmpHatGlb = Path.Combine(Path.GetTempPath(), $"dl_acc_hat_{Guid.NewGuid():N}.glb");
                File.WriteAllBytes(tmpHatGlb, new byte[] { 0x67, 0x6C, 0x54, 0x46 });
                bool aWorkspaceOk = false;
                try
                {
                    session.StageAccessory("priest", "Venator", "acc_hat", "Wizard Hat", tmpHatGlb, hatOpts);
                    string manifestJson = JsonSerializer.Serialize(session.Manifest);
                    var roundTripped = JsonSerializer.Deserialize<WorkspaceManifest>(manifestJson);
                    aWorkspaceOk = roundTripped != null
                        && roundTripped.Heroes.TryGetValue("priest", out var rtHero)
                        && rtHero.Accessories.Count == 1
                        && rtHero.Accessories[0].SlotId == "acc_hat"
                        && rtHero.Accessories[0].ModelConfig.RigidBoneName == "head";
                }
                finally
                {
                    if (File.Exists(tmpHatGlb)) File.Delete(tmpHatGlb);
                }

                var skeleton = CustomModelAutoRigger.FindSkeleton(heroNode);
                var customMeshesA = hierarchy.Submeshes
                    .Where(s => s?.Mesh != null && GodotObject.IsInstanceValid(s.Mesh) && s.IsCustomImportedMesh && s.Mesh.Visible)
                    .Select(s => s.Mesh).Distinct().ToList();
                var keptStandaloneA = hierarchy.Submeshes
                    .Where(s => s?.Mesh != null && GodotObject.IsInstanceValid(s.Mesh) && !s.IsCustomImportedMesh && s.Mesh.Visible)
                    .Select(s => s.Mesh.HasMeta("ParentCompositeMesh")
                        ? s.Mesh.GetMeta("ParentCompositeMesh").As<MeshInstance3D>()?.Name.ToString()
                        : (!string.IsNullOrEmpty(s.RawName) ? s.RawName : s.Mesh.Name.ToString()))
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var pm = new GamePathManager();
                string compilerExe = pm.ResolveResourceCompilerExe();
                string gamePath = !string.IsNullOrEmpty(pm.CurrentGamePath) ? pm.CurrentGamePath : pm.AutoDetectDeadlockPath();

                var bundleA = CustomModelExporter.BuildExportBundle(
                    customMeshesA,
                    skeleton,
                    "priest",
                    heroModelVpkPath: priestEntry.VmdlRelativePath,
                    compilerExe: compilerExe,
                    deadlockGamePath: gamePath);
                CustomModelExporter.AddPreservedVanillaMeshes(bundleA, new List<MeshInstance3D>(), keptStandaloneA, skeleton);

                bool aVanillaDmxPreserved = bundleA != null
                    && bundleA.AllowedKeptVanillaDmxNames.Contains("priest")
                    && bundleA.PreservedVanillaSubmeshes.Count == 0;

                string aDetail = "";
                bool aCompileOk = true;
                if (bundleA != null)
                {
                    var (decompiledVmdlA, subfilesA, _) = CustomModelExporter.DecompileVanillaHeroVmdl(bundleA, priestEntry.VmdlRelativePath, "models/heroes_wip/priest", false);
                    var availLeafsA = new HashSet<string>(subfilesA.Keys.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
                    string vmdlA = CustomModelExporter.GeneratePatchedVmdl(
                        decompiledVmdlA,
                        bundleA,
                        "models/heroes_wip/priest",
                        "materials/models/heroes_wip/priest",
                        new List<(string MeshName, string SmdRelativePath)>
                        {
                            ("priest_custom_accessories", "models/heroes_wip/priest/priest_body_custom.smd")
                        },
                        bundleA.Submeshes.Select(s => CustomModelExporter.SanitizeMaterialName(s.MaterialName)).ToList(),
                        availLeafsA,
                        includeAnimGraph2Nodes: true);
                    bool hasAccRenderMesh = vmdlA.Contains("priest_custom_accessories", StringComparison.OrdinalIgnoreCase);
                    bool hasVanillaDmx = vmdlA.Contains("priest.dmx", StringComparison.OrdinalIgnoreCase) || vmdlA.Contains("_priest.dmx", StringComparison.OrdinalIgnoreCase);
                    aDetail = $"accMesh={hasAccRenderMesh}, vanillaDmx={hasVanillaDmx}";

                    if (!string.IsNullOrEmpty(compilerExe) && File.Exists(compilerExe))
                    {
                        string tmpVpk = Path.Combine(Path.GetTempPath(), $"dl_acc_test_a_{Guid.NewGuid():N}.vpk");
                        try
                        {
                            bundleA.ResourceCompilerExe = compilerExe;
                            bundleA.OutputVpkPath = tmpVpk;
                            var compRes = await CustomModelExporter.ExportAndCompileCustomModelAsync(bundleA);
                            aCompileOk = compRes.Success && File.Exists(tmpVpk);
                            aDetail += $", compile={aCompileOk} ({compRes.ErrorMessage})";
                        }
                        finally
                        {
                            if (File.Exists(tmpVpk)) File.Delete(tmpVpk);
                        }
                    }
                    else
                    {
                        aDetail += ", compileSkipped(no compiler)";
                    }

                    if (!hasAccRenderMesh || !hasVanillaDmx)
                        aVanillaDmxPreserved = false;
                }

                if (!aRigOk || !aLayer21Clean || hatScanned == null || !aVanillaUnchanged || !aChannelsOk || !aWorkspaceOk || !aVanillaDmxPreserved || !aCompileOk)
                {
                    GD.PrintErr($"FAIL [Scenario A]: rigOk={aRigOk}, layer21={aLayer21Clean}, scanned={hatScanned != null}, vanillaKept={aVanillaUnchanged}, channels={aChannelsOk}, workspace={aWorkspaceOk}, dmxKept={aVanillaDmxPreserved}, compile={aCompileOk} ({aDetail})");
                    allPassed = false;
                }
                else
                {
                    GD.Print($"PASS [Scenario A]: Hat accessory bound, Layer 21 clear, AO/Normal/Roughness discovered, workspace JSON intact, vanilla DMX kept ({aDetail}).");
                }

                // =========================================================================
                // SCENARIO B: Replace Specific Composite Surface ('priest_upper') + Stack Coexistence + Remove Restoration
                // =========================================================================
                var upperSubInfo = hierarchy.Submeshes.FirstOrDefault(s =>
                    (s.OriginalVmatPath ?? "").Contains("priest_upper", StringComparison.OrdinalIgnoreCase) &&
                    s.Mesh != null && s.Mesh.HasMeta("ParentCompositeMesh"));
                int upperSurfIdx = upperSubInfo?.Mesh != null && upperSubInfo.Mesh.HasMeta("SurfaceIndex")
                    ? upperSubInfo.Mesh.GetMeta("SurfaceIndex").AsInt32()
                    : 1;
                string upperTargetKey = upperSubInfo != null
                    ? CustomModelAutoRigger.MakeSurfaceKeepKey(
                        upperSubInfo.Mesh.GetMeta("ParentCompositeMesh").As<MeshInstance3D>().Name.ToString(),
                        upperSurfIdx)
                    : CustomModelAutoRigger.MakeSurfaceKeepKey("_priest", 1);

                var coatScene = CreateSyntheticHatScene("ArmorPlate", new Color(0.8f, 0.25f, 0.2f), withDetailTextures: false);
                var coatOpts = new CustomModelAutoRigger.RiggingOptions
                {
                    SlotId = "acc_coat",
                    AccessoryName = "Chest Armor",
                    Intent = CustomModelAutoRigger.AccessoryIntent.ReplacePart,
                    Mode = CustomModelAutoRigger.ReplaceMode.PartialAccessory,
                    Method = CustomModelAutoRigger.RiggingMethod.RigidSingleBone,
                    RigidBoneName = "spine_2",
                    ExplicitReplacedTargets = new List<string> { upperTargetKey },
                    IsPreviewOnly = false
                };

                var coatRes = CustomModelAutoRigger.RigAndBind(coatScene, heroNode, coatOpts);
                hierarchy.ScanHero(heroNode);

                bool bUpperHidden = !hierarchy.Submeshes.Any(s =>
                    s.Mesh != null && s.Mesh.HasMeta("ParentCompositeMesh") &&
                    (s.OriginalVmatPath ?? "").Contains("priest_upper", StringComparison.OrdinalIgnoreCase));
                bool bLowerAndWeaponsKept = hierarchy.Submeshes.Any(s => (s.OriginalVmatPath ?? "").Contains("priest_lower", StringComparison.OrdinalIgnoreCase))
                    && hierarchy.Submeshes.Any(s => (s.OriginalVmatPath ?? "").Contains("priest_shotgun", StringComparison.OrdinalIgnoreCase));
                int stackedCount = CustomModelAutoRigger.CollectCustomContainers(heroNode).Count;
                bool bCoexist = stackedCount == 2;

                var keptCompB = hierarchy.Submeshes
                    .Where(s => s?.Mesh != null && GodotObject.IsInstanceValid(s.Mesh) && !s.IsCustomImportedMesh && s.Mesh.Visible && s.Mesh.HasMeta("ParentCompositeMesh"))
                    .Select(s => s.Mesh).ToList();
                var keptStandaloneB = hierarchy.Submeshes
                    .Where(s => s?.Mesh != null && GodotObject.IsInstanceValid(s.Mesh) && !s.IsCustomImportedMesh && s.Mesh.Visible && !s.Mesh.HasMeta("ParentCompositeMesh"))
                    .Select(s => !string.IsNullOrEmpty(s.RawName) ? s.RawName : s.Mesh.Name.ToString()).ToList();

                var bundleB = CustomModelExporter.BuildExportBundle(
                    hierarchy.Submeshes.Where(s => s.IsCustomImportedMesh && s.Mesh.Visible).Select(s => s.Mesh).Distinct().ToList(),
                    skeleton,
                    "priest",
                    heroModelVpkPath: priestEntry.VmdlRelativePath,
                    compilerExe: compilerExe,
                    deadlockGamePath: gamePath);
                CustomModelExporter.AddPreservedVanillaMeshes(bundleB, keptCompB, keptStandaloneB, skeleton);

                bool bPartialSplitOk = !bundleB.AllowedKeptVanillaDmxNames.Contains("priest")
                    && bundleB.PreservedVanillaSubmeshes.Any(p => p.PreserveVanillaVmatPath.Contains("priest_lower", StringComparison.OrdinalIgnoreCase))
                    && bundleB.PreservedVanillaSubmeshes.All(p => !p.PreserveVanillaVmatPath.Contains("priest_upper", StringComparison.OrdinalIgnoreCase));

                // Remove 'acc_coat' and verify 'priest_upper' returns while 'acc_hat' remains!
                CustomModelAutoRigger.RemoveAccessorySlot(heroNode, "acc_coat");
                hierarchy.ScanHero(heroNode);

                bool bRestoredUpper = hierarchy.Submeshes.Any(s => (s.OriginalVmatPath ?? "").Contains("priest_upper", StringComparison.OrdinalIgnoreCase));
                int remainingCount = CustomModelAutoRigger.CollectCustomContainers(heroNode).Count;
                bool bHatStillPresent = remainingCount == 1 && heroNode.FindChild("Custom_Accessory_acc_hat", true, false) != null;

                session.DiscardAccessory("priest", "acc_hat");

                if (!coatRes.Success || !bUpperHidden || !bLowerAndWeaponsKept || !bCoexist || !bPartialSplitOk || !bRestoredUpper || !bHatStillPresent)
                {
                    GD.PrintErr($"FAIL [Scenario B]: coatRig={coatRes.Success}, upperHidden={bUpperHidden}, lowerWeaponsKept={bLowerAndWeaponsKept}, stacked={stackedCount}/2, partialSplit={bPartialSplitOk}, restoredUpper={bRestoredUpper}, hatLeft={bHatStillPresent}");
                    allPassed = false;
                }
                else
                {
                    GD.Print($"PASS [Scenario B]: Replaced only 'priest_upper' (kept lower & weapons), stacked 2 slots, partial SMD split verified, RemoveAccessorySlot cleanly restored 'priest_upper'.");
                }

                // =========================================================================
                // SCENARIO C: Non-Uniform XYZ Scale + Rename + Hero Fit-Deform & Export + Clean Restore
                // =========================================================================
                var upperMeshesBefore = hierarchy.Submeshes
                    .Where(s => s?.Mesh != null && !s.IsCustomImportedMesh &&
                        (s.OriginalVmatPath ?? "").Contains("priest_upper", StringComparison.OrdinalIgnoreCase))
                    .Select(s => (Sub: s, Verts: (Vector3[])s.Mesh.Mesh.SurfaceGetArrays(s.SurfaceIndex)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Clone()))
                    .ToList();
                var priestSkel = CustomModelAutoRigger.FindSkeleton(heroNode);
                int headIdx = priestSkel?.FindBone("head") ?? -1;
                Vector3 headRest = headIdx >= 0 ? priestSkel.GetBoneGlobalRest(headIdx).Origin : Vector3.Zero;
                float maxVanillaY = upperMeshesBefore.Count > 0
                    ? upperMeshesBefore.Max(x => x.Verts.Max(v => v.Y))
                    : 0f;

                var flatHatScene = CreateSyntheticHatScene("TopHatXyz", new Color(0.2f, 0.7f, 0.4f), withDetailTextures: false);
                Vector3 nonUniformS = new Vector3(1.6f, 0.55f, 1.15f);
                Transform3D fitXyz = Transform3D.Identity.Scaled(nonUniformS);
                fitXyz.Origin = new Vector3(0f, 1.55f, 0f);
                var hatXyzOpts = new CustomModelAutoRigger.RiggingOptions
                {
                    SlotId = "acc_tophat",
                    AccessoryName = "Initial TopHat",
                    Intent = CustomModelAutoRigger.AccessoryIntent.AddAccessory,
                    Mode = CustomModelAutoRigger.ReplaceMode.PartialAccessory,
                    Method = CustomModelAutoRigger.RiggingMethod.RigidSingleBone,
                    RigidBoneName = "head",
                    RootPosition = fitXyz.Origin,
                    RootScale = 1.0f,
                    RootScaleXYZ = nonUniformS,
                    AlignedTransform = fitXyz,
                    OriginalMeshDeformEnabled = true,
                    OriginalMeshDeformTargetFilter = "priest_upper",
                    OriginalMeshDeformBoneName = "head",
                    OriginalMeshDeformMode = CustomModelAutoRigger.HeroDeformMode.FlattenTopDown,
                    OriginalMeshDeformStrength = 0.85f,
                    OriginalMeshDeformRadius = 0.35f,
                    OriginalMeshDeformHeightOffset = -0.08f,
                    IsPreviewOnly = false
                };

                var topHatRes = CustomModelAutoRigger.RigAndBind(flatHatScene, heroNode, hatXyzOpts);
                hierarchy.ScanHero(heroNode);
                bool cNonUniformOk = false;
                if (topHatRes.Success && topHatRes.CustomSubmeshes.Count == 1)
                {
                    var verts = topHatRes.CustomSubmeshes[0].Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    float minX = verts.Min(v => v.X), maxX = verts.Max(v => v.X);
                    float minY = verts.Min(v => v.Y), maxY = verts.Max(v => v.Y);
                    float ratioXY = (maxX - minX) / MathF.Max(0.001f, maxY - minY);
                    cNonUniformOk = ratioXY > 2.2f; // BoxMesh was 0.24 x 0.16 (1.5:1); * (1.6 / 0.55) => ~4.36:1
                }

                // Rename slot in workspace & check JSON persistence for XYZ scale + deform
                string tmpXyzGlb = Path.Combine(Path.GetTempPath(), $"dl_acc_xyz_{Guid.NewGuid():N}.glb");
                File.WriteAllBytes(tmpXyzGlb, new byte[] { 0x67, 0x6C, 0x54, 0x46 });
                bool cWorkspaceRenameOk = false;
                try
                {
                    session.StageAccessory("priest", "Venator", "acc_tophat", "Initial TopHat", tmpXyzGlb, hatXyzOpts);
                    session.RenameAccessorySlot("priest", "acc_tophat", "Custom Renamed Top Hat");
                    string rtJson = JsonSerializer.Serialize(session.Manifest);
                    var rtManifest = JsonSerializer.Deserialize<WorkspaceManifest>(rtJson);
                    var rtAcc = rtManifest?.Heroes["priest"]?.Accessories?.FirstOrDefault(a => a.SlotId == "acc_tophat");
                    var rtOpts = rtAcc?.ModelConfig?.ToRiggingOptions();
                    cWorkspaceRenameOk = rtAcc != null
                        && rtAcc.DisplayName == "Custom Renamed Top Hat"
                        && rtOpts != null
                        && rtOpts.AccessoryName == "Custom Renamed Top Hat"
                        && Mathf.IsEqualApprox(rtOpts.RootScaleXYZ.X, 1.6f)
                        && Mathf.IsEqualApprox(rtOpts.RootScaleXYZ.Y, 0.55f)
                        && rtOpts.OriginalMeshDeformEnabled
                        && Mathf.IsEqualApprox(rtOpts.OriginalMeshDeformStrength, 0.85f);
                    session.DiscardAccessory("priest", "acc_tophat");
                }
                finally
                {
                    if (File.Exists(tmpXyzGlb)) File.Delete(tmpXyzGlb);
                }

                bool cDeformedMetaOk = hierarchy.Submeshes.Any(s =>
                    s?.Mesh != null && !s.IsCustomImportedMesh &&
                    s.Mesh.HasMeta("IsDeformedOriginalMesh") && s.Mesh.GetMeta("IsDeformedOriginalMesh").AsBool());
                var upperMeshesAfter = hierarchy.Submeshes
                    .Where(s => s?.Mesh != null && !s.IsCustomImportedMesh &&
                        (s.OriginalVmatPath ?? "").Contains("priest_upper", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                float maxDeformedY = upperMeshesAfter.Count > 0
                    ? upperMeshesAfter.Max(s => s.Mesh.Mesh.SurfaceGetArrays(s.SurfaceIndex)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Max(v => v.Y))
                    : 0f;
                bool cVertsFlattened = cDeformedMetaOk && (maxVanillaY - maxDeformedY) > 0.005f;

                // Verify deformed vanilla mesh routes through PreservedVanillaSubmeshes (SMD) rather than untouched DMX
                var customMeshesC = hierarchy.Submeshes
                    .Where(s => s?.Mesh != null && GodotObject.IsInstanceValid(s.Mesh) && s.IsCustomImportedMesh && s.Mesh.Visible)
                    .Select(s => s.Mesh).Distinct().ToList();
                var keptCompC = hierarchy.Submeshes
                    .Where(s => s?.Mesh != null && GodotObject.IsInstanceValid(s.Mesh) && !s.IsCustomImportedMesh && s.Mesh.Visible && s.Mesh.HasMeta("ParentCompositeMesh"))
                    .Select(s => s.Mesh).ToList();
                var bundleC = CustomModelExporter.BuildExportBundle(
                    customMeshesC, skeleton, "priest",
                    heroModelVpkPath: priestEntry.VmdlRelativePath,
                    compilerExe: compilerExe, deadlockGamePath: gamePath);
                CustomModelExporter.AddPreservedVanillaMeshes(bundleC, keptCompC, new List<string>(), skeleton);
                bool cExportDeformedSmdOk = bundleC != null
                    && !bundleC.AllowedKeptVanillaDmxNames.Contains("priest")
                    && bundleC.PreservedVanillaSubmeshes.Any(p =>
                        (p.PreserveVanillaVmatPath ?? "").Contains("priest_upper", StringComparison.OrdinalIgnoreCase) &&
                        p.Vertices.Max(v => v.Y) < maxVanillaY - 0.005f);

                // Remove accessory and confirm exact vertex restoration
                CustomModelAutoRigger.RemoveAccessorySlot(heroNode, "acc_tophat");
                hierarchy.ScanHero(heroNode);
                var upperMeshesRestored = hierarchy.Submeshes
                    .Where(s => s?.Mesh != null && !s.IsCustomImportedMesh &&
                        (s.OriginalVmatPath ?? "").Contains("priest_upper", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                float maxRestoredY = upperMeshesRestored.Count > 0
                    ? upperMeshesRestored.Max(s => s.Mesh.Mesh.SurfaceGetArrays(s.SurfaceIndex)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Max(v => v.Y))
                    : 0f;
                bool cRestoredCleanly = !hierarchy.Submeshes.Any(s =>
                    s?.Mesh != null && !s.IsCustomImportedMesh &&
                    s.Mesh.HasMeta("IsDeformedOriginalMesh") && s.Mesh.GetMeta("IsDeformedOriginalMesh").AsBool())
                    && Mathf.Abs(maxRestoredY - maxVanillaY) < 0.0005f;

                if (!cNonUniformOk || !cWorkspaceRenameOk || !cVertsFlattened || !cExportDeformedSmdOk || !cRestoredCleanly)
                {
                    GD.PrintErr($"FAIL [Scenario C]: nonUniform={cNonUniformOk}, workspaceRename={cWorkspaceRenameOk}, deformed={cDeformedMetaOk}, headRest={headRest}, flattened={cVertsFlattened} (vanillaY={maxVanillaY:F4}, defY={maxDeformedY:F4}), exportDeformedSmd={cExportDeformedSmdOk}, restored={cRestoredCleanly} (restY={maxRestoredY:F4})");
                    allPassed = false;
                }
                else
                {
                    GD.Print($"PASS [Scenario C]: Non-uniform XYZ scale, RenameAccessorySlot, Hero Fit-Deform ({maxVanillaY:F3}m -> {maxDeformedY:F3}m -> kept_props SMD), and clean vertex restoration verified.");
                }

                // =========================================================================
                // SCENARIO D: Viscous 'inflated' Visibility Persistence Across Character <-> Paint
                // =========================================================================
                var viscousEntry = DeadlockHeroCatalog.GetByCodename("viscous");
                bool dPassed = true;
                if (viscousEntry != null)
                {
                    Node3D viscousNode = await vpkLoader.LoadHeroModelAsync(viscousEntry);
                    if (viscousNode == null)
                    {
                        GD.PrintErr("FAIL [Scenario D]: Could not load viscous hero model.");
                        allPassed = false;
                    }
                    else
                    {
                        var allOrigViscous = CustomModelAutoRigger.CollectOriginalHeroMeshes(viscousNode);
                        var inflatedOrig = allOrigViscous.FirstOrDefault(m => m.Name.ToString().Contains("inflated", StringComparison.OrdinalIgnoreCase));
                        if (inflatedOrig == null)
                        {
                            GD.PrintErr("FAIL [Scenario D]: Could not locate 'inflated' mesh on viscous.");
                            allPassed = false;
                        }
                        else
                        {
                            // Hide inflated before Paint Tab scans/splits composites
                            inflatedOrig.Visible = false;
                            inflatedOrig.SetMeta("UserVisibility", false);

                            var vHierarchy = new HeroMeshHierarchy { Name = "ViscousHierarchy" };
                            Root.AddChild(vHierarchy);
                            vHierarchy.ScanHero(viscousNode);

                            var inflatedScanned = vHierarchy.Submeshes
                                .Where(s => s.RawName.Contains("inflated", StringComparison.OrdinalIgnoreCase))
                                .ToList();
                            bool staysHiddenOnScan = inflatedScanned.Count > 0 && inflatedScanned.All(s =>
                                !s.Mesh.Visible &&
                                (!s.Mesh.HasMeta("UserVisibility") || !s.Mesh.GetMeta("UserVisibility").AsBool()));

                            // Now show via Paint Tab SetMeshVisibility(true) then hide again SetMeshVisibility(false)
                            foreach (var s in inflatedScanned) vHierarchy.SetMeshVisibility(s, true);
                            bool showsWhenSetTrue = inflatedScanned.All(s => s.Mesh.Visible && s.Mesh.HasMeta("UserVisibility") && s.Mesh.GetMeta("UserVisibility").AsBool());
                            foreach (var s in inflatedScanned) vHierarchy.SetMeshVisibility(s, false);
                            bool staysHiddenAfterPaintToggle = inflatedScanned.All(s => !s.Mesh.Visible && s.Mesh.HasMeta("UserVisibility") && !s.Mesh.GetMeta("UserVisibility").AsBool());

                            dPassed = staysHiddenOnScan && showsWhenSetTrue && staysHiddenAfterPaintToggle;
                            if (!dPassed)
                            {
                                GD.PrintErr($"FAIL [Scenario D]: count={inflatedScanned.Count}, staysHiddenOnScan={staysHiddenOnScan}, showsWhenSetTrue={showsWhenSetTrue}, staysHiddenAfterPaintToggle={staysHiddenAfterPaintToggle}");
                                allPassed = false;
                            }
                            else
                            {
                                GD.Print($"PASS [Scenario D]: Viscous 'inflated' ({inflatedScanned.Count} scanned surface(s)) stays hidden & UserVisibility-synced across scans and Paint Tab toggles.");
                            }
                        }
                    }
                }
                else
                {
                    GD.Print("[Test] Skipping Scenario D (viscous catalog entry not found).");
                }

                // =========================================================================
                // SCENARIO E: Wrapped Negative/Tiled UVs + Modal Scale Pivot + Gizmo Euler + Toolbar
                // =========================================================================
                // 1) Tiled/negative UV wrapping (e.g. V in [-1, 0], U in [1, 2])
                Vector2 t0 = new Vector2(1.0f, -1.0f);
                Vector2 t1 = new Vector2(2.0f, -1.0f);
                Vector2 t2 = new Vector2(1.0f, 0.0f);
                MeshRaycaster.WrapTriangleUvsToUnitSquare(ref t0, ref t1, ref t2);
                bool triWrapOk = Mathf.IsEqualApprox(t0.X, 0.0f, 0.001f) && Mathf.IsEqualApprox(t0.Y, 0.0f, 0.001f)
                              && Mathf.IsEqualApprox(t1.X, 1.0f, 0.001f) && Mathf.IsEqualApprox(t1.Y, 0.0f, 0.001f)
                              && Mathf.IsEqualApprox(t2.X, 0.0f, 0.001f) && Mathf.IsEqualApprox(t2.Y, 1.0f, 0.001f);

                var quadArrays = new Godot.Collections.Array();
                quadArrays.Resize((int)Mesh.ArrayType.Max);
                quadArrays[(int)Mesh.ArrayType.Vertex] = new Vector3[]
                {
                    new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0),
                    new Vector3(-1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0)
                };
                quadArrays[(int)Mesh.ArrayType.Normal] = new Vector3[]
                {
                    Vector3.Back, Vector3.Back, Vector3.Back,
                    Vector3.Back, Vector3.Back, Vector3.Back
                };
                quadArrays[(int)Mesh.ArrayType.TexUV] = new Vector2[]
                {
                    new Vector2(1.0f, 0.0f), new Vector2(2.0f, 0.0f), new Vector2(2.0f, -1.0f),
                    new Vector2(1.0f, 0.0f), new Vector2(2.0f, -1.0f), new Vector2(1.0f, -1.0f)
                };
                var quadMesh = new ArrayMesh();
                quadMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, quadArrays);
                var quadMi = new MeshInstance3D { Name = "ShiftedUvQuad", Mesh = quadMesh };
                Root.AddChild(quadMi);

                var raycaster = new MeshRaycaster();
                raycaster.BuildFromMesh(quadMi);
                var rayHit = raycaster.RaycastMesh(new Vector3(0, 0, 2), Vector3.Forward, quadMi, cullBackfaces: false);
                var uvLookupHit = raycaster.FindPointFromUV(quadMi, new Vector2(0.5f, 0.5f));
                bool rayUvOk = rayHit.Hit
                            && Mathf.IsEqualApprox(rayHit.HitUV.X, 0.5f, 0.02f)
                            && Mathf.IsEqualApprox(rayHit.HitUV.Y, 0.5f, 0.02f);
                bool uvLookupOk = uvLookupHit.Hit && uvLookupHit.WorldPosition.DistanceTo(Vector3.Zero) < 0.05f;
                quadMi.QueueFree();

                // 2) Modal Scale Pivot Invariance + Euler Yxz Consistency + Clean Toolbar Intent
                var offcenterNode = new Node3D { Name = "OffcenterHat" };
                var offcenterBox = new ArrayMesh();
                var surfArrays = new BoxMesh { Size = new Vector3(0.3f, 0.2f, 0.3f) }.GetMeshArrays();
                var bVerts = surfArrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                Vector3 localOffset = new Vector3(0.0f, 1.25f, 0.0f);
                for (int vi = 0; vi < bVerts.Length; vi++) bVerts[vi] += localOffset;
                surfArrays[(int)Mesh.ArrayType.Vertex] = bVerts;
                offcenterBox.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, surfArrays);
                offcenterNode.AddChild(new MeshInstance3D { Name = "HatGeo", Mesh = offcenterBox });

                Node3D activeHero = GodotObject.IsInstanceValid(vpkLoader.CurrentHeroNode)
                    ? vpkLoader.CurrentHeroNode
                    : heroNode;
                var modal = new DeadlockPlayground.UI.CustomModelImporterModalUI();
                Root.AddChild(modal);
                modal.Open(
                    activeHero,
                    offcenterNode,
                    "test_hat.glb",
                    workflowIntent: DeadlockPlayground.UI.CustomModelImporterModalUI.ImportWorkflowIntent.AccessoryAddOrReplace);

                var allBtns = modal.FindChildren("*", "Button", true, false).OfType<Button>().ToList();
                var btnSnapGrip = allBtns.FirstOrDefault(b => b.Text == "Snap Grip");
                var btnStraighten = allBtns.FirstOrDefault(b => b.Text == "Straighten");
                bool toolbarIntentOk = btnSnapGrip != null && !btnSnapGrip.Visible
                                    && btnStraighten != null && !btnStraighten.Visible;

                var allSpins = modal.FindChildren("*", "SpinBox", true, false).OfType<SpinBox>().ToList();
                var spinSy = allSpins.FirstOrDefault(s => s.Prefix == "SY:");
                var spinPitch = allSpins.FirstOrDefault(s => s.Prefix == "Pitch:");
                var spinYaw = allSpins.FirstOrDefault(s => s.Prefix == "Yaw:");
                var spinRoll = allSpins.FirstOrDefault(s => s.Prefix == "Roll:");
                // Uniform scale spinbox has empty Prefix and Step == 0.001
                var spinUniform = allSpins.FirstOrDefault(s => string.IsNullOrEmpty(s.Prefix) && Math.Abs(s.Step - 0.001) < 1e-6);

                Transform3D xfBeforeScale = modal.GetAlignedTransform();
                Vector3 centerBeforeScale = xfBeforeScale * localOffset;
                if (spinUniform != null) spinUniform.Value = spinUniform.Value * 1.8;
                if (spinSy != null) spinSy.Value = 0.55;
                Transform3D xfAfterScale = modal.GetAlignedTransform();
                Vector3 centerAfterScale = xfAfterScale * localOffset;
                float pivotDrift = centerBeforeScale.DistanceTo(centerAfterScale);
                bool scalePivotOk = spinUniform != null && spinSy != null && pivotDrift < 0.002f;

                if (spinPitch != null) spinPitch.Value = 22.0;
                if (spinYaw != null) spinYaw.Value = -38.0;
                if (spinRoll != null) spinRoll.Value = 41.0;
                Basis rotCheckBasis = modal.GetAlignedTransform().Basis.Orthonormalized();
                Vector3 roundtripEuler = rotCheckBasis.GetEuler(EulerOrder.Yxz);
                Vector3 roundtripDeg = new Vector3(
                    Mathf.RadToDeg(roundtripEuler.X),
                    Mathf.RadToDeg(roundtripEuler.Y),
                    Mathf.RadToDeg(roundtripEuler.Z));
                bool eulerRoundtripOk = MathF.Abs(roundtripDeg.X - 22.0f) < 0.05f
                                     && MathF.Abs(roundtripDeg.Y - (-38.0f)) < 0.05f
                                     && MathF.Abs(roundtripDeg.Z - 41.0f) < 0.05f;

                modal.QueueFree();
                offcenterNode.QueueFree();

                if (!triWrapOk || !rayUvOk || !uvLookupOk || !toolbarIntentOk || !scalePivotOk || !eulerRoundtripOk)
                {
                    GD.PrintErr($"FAIL [Scenario E]: triWrapOk={triWrapOk}, rayUvOk={rayUvOk} ({rayHit.HitUV}), uvLookupOk={uvLookupOk}, toolbarIntentOk={toolbarIntentOk}, scalePivotOk={scalePivotOk} (drift={pivotDrift:F5}), eulerRoundtripOk={eulerRoundtripOk} (deg={roundtripDeg})");
                    allPassed = false;
                }
                else
                {
                    GD.Print($"PASS [Scenario E]: Tiled/negative UVs wrapped cleanly, Scale pivot drift={pivotDrift:F5}m, YXZ Euler round-trip exact ({roundtripDeg}), and Accessory toolbar hides weapon-only buttons.");
                }

                // =========================================================================
                // SCENARIO F: Disposed VPK Safety, 8-Bone Sculpt Deform + .dpp Round-Trip, Channel Visibility
                // =========================================================================
                // 1) Disposed ValvePak.Package safety in ExtractVtexToGodot
                bool fDisposedSafe = true;
                try
                {
                    var disposedPkg = new ValvePak.Package();
                    disposedPkg.Dispose();
                    var safeTex = Source2TextureLoader.ExtractVtexToGodot(disposedPkg, "materials/nonexistent.vtex_c", 256, true, disposedPkg);
                    fDisposedSafe = safeTex == null;
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"FAIL [Scenario F]: ExtractVtexToGodot threw on disposed package: {ex.GetType().Name}: {ex.Message}");
                    fDisposedSafe = false;
                }

                // 2) Sparse Hero Sculpt Deltas + 8-Bone Flag Preservation + Workspace Round-Trip
                Node3D targetSculptHero = GodotObject.IsInstanceValid(vpkLoader.CurrentHeroNode)
                    ? vpkLoader.CurrentHeroNode
                    : heroNode;
                var sculptSrcMesh = CustomModelAutoRigger.CollectOriginalHeroMeshes(targetSculptHero)
                    .FirstOrDefault(m => m?.Mesh != null && m.Mesh.GetSurfaceCount() > 0);
                bool fSculptApplied = false;
                bool fFlagPreserved = false;
                bool fSculptRoundtrip = false;
                bool fSculptRestored = false;
                if (sculptSrcMesh != null)
                {
                    string sculptKey = CustomModelAutoRigger.MakeOriginalSurfaceDeformKey(sculptSrcMesh, 0);
                    bool origHas8Bones = sculptSrcMesh.Mesh is ArrayMesh origAm &&
                        (origAm.SurfaceGetFormat(0) & Mesh.ArrayFormat.FlagUse8BoneWeights) != 0;
                    var preArrays = sculptSrcMesh.Mesh.SurfaceGetArrays(0);
                    Vector3[] preVerts = preArrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    Vector3 preV0Hero = CustomModelAutoRigger.ComputeTransformRelativeToRoot(sculptSrcMesh, targetSculptHero) * preVerts[0];

                    var sculptHat = CreateSyntheticHatScene("SculptHat", new Color(0.2f, 0.7f, 0.4f), false);
                    var sculptOpts = new CustomModelAutoRigger.RiggingOptions
                    {
                        SlotId = "acc_sculpt_test",
                        Intent = CustomModelAutoRigger.AccessoryIntent.AddAccessory,
                        Method = CustomModelAutoRigger.RiggingMethod.RigidSingleBone,
                        RigidBoneName = "head",
                        OriginalMeshDeformEnabled = true,
                        OriginalMeshDeformStrength = 0.0f, // pure sculpt brush delta without parametric flatten
                        OriginalMeshDeformSculpts = new Dictionary<string, Dictionary<int, Vector3>>(StringComparer.OrdinalIgnoreCase)
                        {
                            [sculptKey] = new Dictionary<int, Vector3> { [0] = new Vector3(0f, -0.025f, 0f) }
                        },
                        AlignedTransform = new Transform3D(Basis.Identity, new Vector3(0f, 1.78f, 0f))
                    };

                    var wsSculptEntry = WorkspaceCustomModelEntry.FromRiggingOptions("custom_models/test_hat.glb", "test_hat.glb", sculptOpts);
                    string jsonSculpt = JsonSerializer.Serialize(wsSculptEntry);
                    var rtSculptEntry = JsonSerializer.Deserialize<WorkspaceCustomModelEntry>(jsonSculpt);
                    var rtSculptOpts = rtSculptEntry.ToRiggingOptions();
                    fSculptRoundtrip = rtSculptOpts.OriginalMeshDeformSculpts.TryGetValue(sculptKey, out var rtVerts)
                                    && rtVerts.TryGetValue(0, out var rtD0)
                                    && rtD0.DistanceTo(new Vector3(0f, -0.025f, 0f)) < 0.0001f;

                    var sculptRes = CustomModelAutoRigger.RigAndBind(sculptHat, targetSculptHero, rtSculptOpts);
                    var postArrays = sculptSrcMesh.Mesh.SurfaceGetArrays(0);
                    Vector3[] postVerts = postArrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    Vector3 postV0Hero = CustomModelAutoRigger.ComputeTransformRelativeToRoot(sculptSrcMesh, targetSculptHero) * postVerts[0];
                    fSculptApplied = sculptRes.Success && (postV0Hero - preV0Hero).DistanceTo(new Vector3(0f, -0.025f, 0f)) < 0.001f;
                    bool postHas8Bones = sculptSrcMesh.Mesh is ArrayMesh postAm &&
                        (postAm.SurfaceGetFormat(0) & Mesh.ArrayFormat.FlagUse8BoneWeights) != 0;
                    fFlagPreserved = !origHas8Bones || postHas8Bones;

                    CustomModelAutoRigger.RemoveAccessorySlot(targetSculptHero, "acc_sculpt_test");
                    var restArrays = sculptSrcMesh.Mesh.SurfaceGetArrays(0);
                    Vector3 restV0Hero = CustomModelAutoRigger.ComputeTransformRelativeToRoot(sculptSrcMesh, targetSculptHero) *
                        restArrays[(int)Mesh.ArrayType.Vertex].AsVector3Array()[0];
                    fSculptRestored = restV0Hero.DistanceTo(preV0Hero) < 0.0005f;
                }

                // 3) Channel Map Discovery: Color-only hides AO/Normal/Rough, Detail-mapped reveals them
                bool IsPillVisibleInChannelBar(MaterialChannelSet cs, MaterialTextureChannel ch)
                {
                    if (ch == MaterialTextureChannel.Color) return true;
                    var en = cs?.GetChannel(ch);
                    bool isReal = en != null && en.IsPresent && !en.IsDefaultFallback;
                    bool isDirty = en != null && en.IsDirty;
                    return isReal || isDirty;
                }

                var simpleHat = CreateSyntheticHatScene("ChannelSimpleHat", new Color(0.8f, 0.2f, 0.2f), withDetailTextures: false);
                var simpleMat = ((MeshInstance3D)simpleHat.GetChild(0)).GetActiveMaterial(0);
                var simpleSet = MaterialChannelDiscovery.DiscoverCustomModelChannels(simpleMat, "simple_mat");
                simpleHat.QueueFree();
                bool fSimpleHidesExtras = IsPillVisibleInChannelBar(simpleSet, MaterialTextureChannel.Color)
                                       && !IsPillVisibleInChannelBar(simpleSet, MaterialTextureChannel.AmbientOcclusion)
                                       && !IsPillVisibleInChannelBar(simpleSet, MaterialTextureChannel.Normal)
                                       && !IsPillVisibleInChannelBar(simpleSet, MaterialTextureChannel.Roughness)
                                       && !IsPillVisibleInChannelBar(simpleSet, MaterialTextureChannel.Shadow);

                var richHat = CreateSyntheticHatScene("ChannelRichHat", new Color(0.2f, 0.2f, 0.8f), withDetailTextures: true);
                var richMat = ((MeshInstance3D)richHat.GetChild(0)).GetActiveMaterial(0);
                var richSet = MaterialChannelDiscovery.DiscoverCustomModelChannels(richMat, "rich_mat");
                richHat.QueueFree();
                bool fRichShowsExtras = IsPillVisibleInChannelBar(richSet, MaterialTextureChannel.Color)
                                     && IsPillVisibleInChannelBar(richSet, MaterialTextureChannel.Normal)
                                     && IsPillVisibleInChannelBar(richSet, MaterialTextureChannel.Roughness)
                                     && IsPillVisibleInChannelBar(richSet, MaterialTextureChannel.AmbientOcclusion);

                if (!fDisposedSafe || !fSculptRoundtrip || !fSculptApplied || !fFlagPreserved || !fSculptRestored || !fSimpleHidesExtras || !fRichShowsExtras)
                {
                    GD.PrintErr($"FAIL [Scenario F]: disposedSafe={fDisposedSafe}, roundtrip={fSculptRoundtrip}, sculptApplied={fSculptApplied}, flag8BonePreserved={fFlagPreserved}, restored={fSculptRestored}, simpleHides={fSimpleHidesExtras}, richShows={fRichShowsExtras}");
                    allPassed = false;
                }
                else
                {
                    GD.Print("PASS [Scenario F]: Disposed VPK safety, 8-bone sculpt deltas + .dpp round-trip + restoration, and material channel discovery visibility verified.");
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[Test] EXCEPTION: {ex.Message}\n{ex.StackTrace}");
                allPassed = false;
            }

            GD.Print("=======================================================");
            GD.Print(allPassed
                ? "   ALL ACCESSORY VERIFICATION CHECKS PASSED!           "
                : "   SOME ACCESSORY VERIFICATION CHECKS FAILED!          ");
            GD.Print("=======================================================");

            Quit(allPassed ? 0 : 1);
        }
    }
}
