using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeadlockPlayground.Animation;
using DeadlockPlayground.Catalog;
using DeadlockPlayground.Importer;
using DeadlockPlayground.Particles;
using DeadlockPlayground.Tools;
using Godot;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.Particles;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.ModelAnimation;
using GameFileLoader = ValveResourceFormat.IO.GameFileLoader;

namespace DeadlockPlayground.Tests;

/// <summary>
/// Headless end-to-end QA smoke test suite for Phase 5 of the ValveResourceFormat 21.1 upgrade.
/// Run via:
/// &amp; "D:\GameDev\Godot v4.7.2 .NET\Godot_v4.7.2-stable_mono_win64_console.exe" --headless --path src --script res://tools/scripts/Vrf21SmokeTest.cs
/// </summary>
public partial class Vrf21SmokeTest : SceneTree
{
    private const uint VisualLayer21Bit = 1u << 20;

    private int _passedChecks;
    private int _failedChecks;

    public override void _Initialize()
    {
        _ = RunAllTestsAsync();
    }

    private void Check(bool condition, string description)
    {
        if (condition)
        {
            _passedChecks++;
            GD.Print($"  [PASS] {description}");
        }
        else
        {
            _failedChecks++;
            GD.PrintErr($"  [FAIL] {description}");
        }
    }

    private async Task RunAllTestsAsync()
    {
        await ToSignal(this, SignalName.ProcessFrame);

        GD.Print("=======================================================");
        GD.Print("   VRF 21.1 UPGRADE END-TO-END HEADLESS SMOKE SUITE    ");
        GD.Print("=======================================================");

        string vpkPath = HeroParticleService.ResolveVpkPath();
        Check(!string.IsNullOrEmpty(vpkPath) && File.Exists(vpkPath), $"Resolved Deadlock VPK: {vpkPath}");
        if (string.IsNullOrEmpty(vpkPath) || !File.Exists(vpkPath))
        {
            GD.PrintErr("[Abort] Deadlock pak01_dir.vpk not found. Exiting with failure.");
            Quit(1);
            return;
        }

        FileStream vpkStream = null;
        Package pkg = null;
        VpkLoaderTest vpkLoader = null;
        ParticleStage3D particleStage = null;
        Camera3D camera = null;

        try
        {
            vpkStream = new FileStream(vpkPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            pkg = new Package();
            pkg.SetFileName(vpkPath);
            pkg.Read(vpkStream);

            // Verify concurrent shared VPK open works while pkg holds FileShare.ReadWrite | FileShare.Delete
            using (var concurrentFs = new FileStream(vpkPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var concurrentPkg = new Package())
            {
                concurrentPkg.SetFileName(vpkPath);
                concurrentPkg.Read(concurrentFs);
                Check(concurrentPkg.Entries.Count > 0, $"Concurrent FileShare.ReadWrite|Delete VPK open succeeded ({concurrentPkg.Entries.Count} extensions).");
            }

            camera = new Camera3D
            {
                Name = "SmokeTestCamera",
                Position = new Vector3(0.0f, 1.5f, 3.5f)
            };
            Root.AddChild(camera);
            camera.Current = true;

            vpkLoader = new VpkLoaderTest
            {
                Name = "SmokeVpkLoader",
                VpkPath = vpkPath
            };
            Root.AddChild(vpkLoader);

            particleStage = new ParticleStage3D
            {
                Name = "ParticleStage3D"
            };
            Root.AddChild(particleStage);

            await ToSignal(this, SignalName.ProcessFrame);

            // Group A: Core VRF 21.1 & Hero Model/Constraints (leaves chrono loaded in vpkLoader)
            var (chronoNode, vrfChrono) = await RunCoreModelAndConstraintTestsAsync(pkg, vpkLoader);

            // Group B: ModelExtract Vanilla Hero Decompilation
            RunVanillaDecompileTests(vpkPath);

            // Group C: VRF 21.1 ParticleSystemSimulation & Godot Renderer Pipeline
            RunParticleTests(pkg, vpkLoader, particleStage, chronoNode, vrfChrono);

            // Group D: Shader & Animation Parity
            RunShaderAndAnimationTests(pkg, vpkLoader, chronoNode, vrfChrono);
        }
        catch (Exception ex)
        {
            _failedChecks++;
            GD.PrintErr($"[EXCEPTION] Unhandled error during Vrf21SmokeTest: {ex}");
        }
        finally
        {
            try
            {
                particleStage?.UnloadActiveParticle();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[Cleanup] ParticleStage3D cleanup warning: {ex.Message}");
            }

            if (vpkLoader != null && GodotObject.IsInstanceValid(vpkLoader))
            {
                vpkLoader.QueueFree();
            }
            if (particleStage != null && GodotObject.IsInstanceValid(particleStage))
            {
                particleStage.QueueFree();
            }
            if (camera != null && GodotObject.IsInstanceValid(camera))
            {
                camera.QueueFree();
            }

            pkg?.Dispose();
            vpkStream?.Dispose();

            GD.Print("=======================================================");
            GD.Print($"VRF 21.1 SMOKE SUMMARY: {_passedChecks} passed, {_failedChecks} failed");
            GD.Print("=======================================================");
        }

        Quit(_failedChecks == 0 ? 0 : 1);
    }

    private async Task<(Node3D ChronoNode, Model VrfChrono)> RunCoreModelAndConstraintTestsAsync(Package pkg, VpkLoaderTest vpkLoader)
    {
        GD.Print("\n[Group A] Core VRF 21.1 Model Parsing, MeshGroups, BoneConstraints & Hero Loading...");

        var chronoCatalog = DeadlockHeroCatalog.GetByCodename("chrono");
        var shivCatalog = DeadlockHeroCatalog.GetByCodename("shiv");
        Check(chronoCatalog != null && shivCatalog != null, "Catalog resolved both 'chrono' and 'shiv'.");

        Model vrfChrono = null;
        if (WithModelDeferred(pkg, chronoCatalog.VmdlRelativePath, m =>
        {
            vrfChrono = m;
            int embeddedCount = m.GetEmbeddedMeshes().Count();
            int meshGroupCount = m.MeshGroups.Names.Count;
            var constraints = BoneConstraintBase.ReadList(m);
            Check(embeddedCount > 0, $"chrono has {embeddedCount} embedded mesh(es).");
            Check(meshGroupCount > 0, $"chrono MeshGroups parsed ({meshGroupCount} group(s), {m.MeshGroups.BodyGroups.Count} bodygroup(s), defaultMask=0x{m.MeshGroups.DefaultMask:X}).");
            Check(constraints.Length > 0, $"BoneConstraintBase.ReadList(chrono) returned {constraints.Length} constraint(s).");
        }))
        {
            Check(vrfChrono != null, $"ReadMode.Deferred parsed chrono Model ({chronoCatalog.VmdlRelativePath}).");
        }
        else
        {
            Check(false, $"ReadMode.Deferred parsed chrono Model ({chronoCatalog.VmdlRelativePath}).");
        }

        // Scan hero .vmdl_c files in VPK using ReadMode.Deferred to verify broad parsing and find constraints + jiggle bones
        int scannedHeroes = 0;
        int parsedOk = 0;
        int heroesWithConstraints = 0;
        int heroesWithJiggleBones = 0;
        string jiggleHeroPath = null;
        DeadlockHeroEntry jiggleCatalogMatch = null;

        if (pkg.Entries.TryGetValue("vmdl_c", out var vmdlEntries))
        {
            var catalogPaths = DeadlockHeroCatalog.Entries
                .Select(h => h.VmdlRelativePath.Replace('\\', '/'))
                .Where(p => pkg.FindEntry(p) != null);

            var vpkHeroPaths = vmdlEntries
                .Select(e => e.GetFullPath().Replace('\\', '/'))
                .Where(p =>
                    (p.StartsWith("models/heroes_staging/", StringComparison.OrdinalIgnoreCase) ||
                     p.StartsWith("models/heroes_wip/", StringComparison.OrdinalIgnoreCase) ||
                     p.StartsWith("models/heroes/", StringComparison.OrdinalIgnoreCase)) &&
                    !p.Contains("_physics", StringComparison.OrdinalIgnoreCase) &&
                    !p.Contains("_agdoll", StringComparison.OrdinalIgnoreCase) &&
                    !p.Contains("_hitbox", StringComparison.OrdinalIgnoreCase) &&
                    !p.Contains("_anim", StringComparison.OrdinalIgnoreCase) &&
                    !p.Contains("_weapon", StringComparison.OrdinalIgnoreCase) &&
                    !p.Contains("_gun", StringComparison.OrdinalIgnoreCase));

            var heroCandidates = catalogPaths
                .Concat(vpkHeroPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(60)
                .ToList();

            foreach (string path in heroCandidates)
            {
                scannedHeroes++;
                if (WithModelDeferred(pkg, path, m =>
                {
                    parsedOk++;
                    var c = BoneConstraintBase.ReadList(m);
                    if (c.Length > 0) heroesWithConstraints++;

                    int jiggleCount = m.GetEmbeddedPhys()?.FeModel?.JiggleBones.Length ?? 0;
                    if (jiggleCount > 0)
                    {
                        heroesWithJiggleBones++;
                        jiggleHeroPath ??= path;
                        jiggleCatalogMatch ??= DeadlockHeroCatalog.Entries.FirstOrDefault(h =>
                            string.Equals(h.VmdlRelativePath, path, StringComparison.OrdinalIgnoreCase));
                    }
                }))
                {
                    // inspected cleanly under open deferred stream
                }
            }
        }

        Check(scannedHeroes > 0 && parsedOk == scannedHeroes, $"Hero VPK ReadMode.Deferred scan parsed {parsedOk}/{scannedHeroes} model(s) cleanly.");
        Check(heroesWithConstraints > 0, $"Found {heroesWithConstraints} scanned hero model(s) with BoneConstraints.");
        GD.Print($"  [Info] Scanned hero scan jiggle-bone models found: {heroesWithJiggleBones} (first: {jiggleHeroPath ?? "none"})");

        // Load shiv first
        Node3D shivNode = await vpkLoader.LoadHeroModelAsync(shivCatalog);
        Check(shivNode != null, "VpkLoaderTest loaded shiv.");
        if (shivNode != null)
        {
            var shivMeshes = shivNode.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().ToList();
            var shivSkel = shivNode.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>().FirstOrDefault();
            Check(shivMeshes.Count > 0, $"shiv spawned {shivMeshes.Count} MeshInstance3D node(s).");
            Check(shivSkel != null && shivSkel.GetBoneCount() > 0, $"shiv Skeleton3D has {shivSkel?.GetBoneCount() ?? 0} bone(s).");

            ModelAttachmentRegistry.TryGetHeroModel(shivNode, out var vrfShiv);
            if (shivSkel != null && vrfShiv != null)
            {
                var shivDriver = new Source2BoneConstraintDriver { Name = "SmokeShivConstraintDriver" };
                shivNode.AddChild(shivDriver);
                shivDriver.Initialize(vrfShiv, shivSkel, shivNode);
                shivDriver.EvaluateNow(1.0f / 60.0f);
                Check(shivDriver.IsInitialized, $"Source2BoneConstraintDriver initialized on shiv (constraints={shivDriver.ConstraintCount}, jiggle={shivDriver.JiggleBoneCount}).");
            }
            else if (shivSkel != null)
            {
                WithModelDeferred(pkg, shivCatalog.VmdlRelativePath, m =>
                {
                    var shivDriver = new Source2BoneConstraintDriver { Name = "SmokeShivConstraintDriver" };
                    shivNode.AddChild(shivDriver);
                    shivDriver.Initialize(m, shivSkel, shivNode);
                    shivDriver.EvaluateNow(1.0f / 60.0f);
                    Check(shivDriver.IsInitialized, $"Source2BoneConstraintDriver initialized on shiv (constraints={shivDriver.ConstraintCount}, jiggle={shivDriver.JiggleBoneCount}).");
                });
            }
        }

        // If any catalog hero has jiggle bones (e.g., Viscous/Kelvin/etc.), verify Source2BoneConstraintDriver jiggle evaluation too
        if (jiggleCatalogMatch != null &&
            !string.Equals(jiggleCatalogMatch.InternalCodename, "chrono", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(jiggleCatalogMatch.InternalCodename, "shiv", StringComparison.OrdinalIgnoreCase))
        {
            Node3D jiggleNode = await vpkLoader.LoadHeroModelAsync(jiggleCatalogMatch);
            var jiggleDriver = jiggleNode?.FindChildren("*", "Node", true, false)
                .OfType<Source2BoneConstraintDriver>()
                .FirstOrDefault();
            if (jiggleDriver != null)
            {
                jiggleDriver.EnableJiggleBones = true;
                for (int step = 0; step < 3; step++)
                {
                    jiggleDriver.EvaluateNow(1.0f / 60.0f);
                }
                Check(jiggleDriver.IsInitialized && jiggleDriver.JiggleBoneCount > 0,
                    $"Evaluated {jiggleDriver.JiggleBoneCount} jiggle bone(s) on '{jiggleCatalogMatch.DisplayName}' without errors.");
            }
        }

        // Load chrono last so it remains active in vpkLoader for Groups C & D
        Node3D chronoNode = await vpkLoader.LoadHeroModelAsync(chronoCatalog);
        Check(chronoNode != null, "VpkLoaderTest loaded chrono.");
        if (chronoNode != null)
        {
            var chronoMeshes = chronoNode.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().ToList();
            int surfaceCount = chronoMeshes.Sum(m => m.Mesh?.GetSurfaceCount() ?? 0);
            var chronoSkel = chronoNode.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>().FirstOrDefault();
            var chronoDriver = chronoNode.FindChildren("*", "Node", true, false)
                .OfType<Source2BoneConstraintDriver>()
                .FirstOrDefault();

            Check(chronoMeshes.Count > 0 && surfaceCount > 0, $"chrono has {chronoMeshes.Count} meshes and {surfaceCount} surfaces.");
            Check(chronoSkel != null && chronoSkel.GetBoneCount() > 0, $"chrono Skeleton3D has {chronoSkel?.GetBoneCount() ?? 0} bone(s).");
            Check(chronoDriver != null && chronoDriver.IsInitialized && chronoDriver.ConstraintCount > 0,
                $"chrono Source2BoneConstraintDriver initialized (constraints={chronoDriver?.ConstraintCount ?? 0}, jiggle={chronoDriver?.JiggleBoneCount ?? 0}).");
            chronoDriver?.EvaluateNow(1.0f / 60.0f);
        }

        Model registeredChrono = null;
        if (chronoNode != null)
        {
            ModelAttachmentRegistry.TryGetHeroModel(chronoNode, out registeredChrono);
        }
        return (chronoNode, registeredChrono ?? vrfChrono);
    }

    private void RunVanillaDecompileTests(string vpkPath)
    {
        GD.Print("\n[Group B] CustomModelExporter.DecompileVanillaHeroVmdl (with & without softbody)...");

        string repoRoot = Path.GetFullPath(ProjectSettings.GlobalizePath("res://.."));
        string tempRoot = Path.Combine(repoRoot, "scratch_out", "vrf21_smoke");
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
        Directory.CreateDirectory(tempRoot);

        try
        {
            var chronoCatalog = DeadlockHeroCatalog.GetByCodename("chrono");
            string gameRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(vpkPath)!, "..", ".."));
            string modelFolderRel = Path.GetDirectoryName(chronoCatalog.VmdlRelativePath)?.Replace('\\', '/') ?? "models/heroes_staging/chrono";

            var bundle = new CustomModelExporter.CustomModelExportBundle
            {
                HeroCodename = chronoCatalog.InternalCodename,
                HeroModelVpkPath = chronoCatalog.VmdlRelativePath,
                DeadlockGamePath = gameRoot
            };

            foreach (bool softbody in new[] { false, true })
            {
                string subDir = Path.Combine(tempRoot, softbody ? "with_softbody" : "no_softbody");
                Directory.CreateDirectory(subDir);

                var (vmdlText, subfiles, vrfBones) = CustomModelExporter.DecompileVanillaHeroVmdl(
                    bundle,
                    chronoCatalog.VmdlRelativePath,
                    modelFolderRel,
                    reconstructSoftbody: softbody);

                Check(!string.IsNullOrWhiteSpace(vmdlText), $"DecompileVanillaHeroVmdl(reconstructSoftbody={softbody}) produced non-empty text ({vmdlText?.Length ?? 0} chars).");
                bool looksValidKv3 = vmdlText != null &&
                                     vmdlText.Contains("<!-- kv3 encoding:text:", StringComparison.OrdinalIgnoreCase) &&
                                     vmdlText.Contains("rootNode", StringComparison.OrdinalIgnoreCase);
                Check(looksValidKv3, $"Decompiled VMDL (reconstructSoftbody={softbody}) contains valid KV3 header & rootNode ({vrfBones?.Count ?? 0} VRF bones, {subfiles?.Count ?? 0} subfiles).");

                string outVmdlPath = Path.Combine(subDir, $"{chronoCatalog.InternalCodename}.vmdl");
                File.WriteAllText(outVmdlPath, vmdlText ?? string.Empty);

                if (subfiles != null)
                {
                    foreach (var kv in subfiles)
                    {
                        string rel = kv.Key.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                        string target = Path.Combine(subDir, rel);
                        string parent = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                        File.WriteAllBytes(target, kv.Value);
                    }
                }

                Check(File.Exists(outVmdlPath) && new FileInfo(outVmdlPath).Length > 200,
                    $"Wrote decompiled VMDL to temporary disk path ({new FileInfo(outVmdlPath).Length} bytes, reconstructSoftbody={softbody}).");
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            Check(!Directory.Exists(tempRoot), $"Cleaned up temporary directory: {tempRoot}");
        }
    }

    private void RunParticleTests(Package pkg, VpkLoaderTest vpkLoader, ParticleStage3D stage, Node3D chronoNode, Model vrfChrono)
    {
        GD.Print("\n[Group C] VRF 21.1 ParticleSystemSimulation & Godot Renderer Pipeline...");

        IFileLoader fileLoader = vpkLoader.GetOrCreateFileLoader();
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (pkg.Entries.TryGetValue("vpcf_c", out var vpcfEntries))
        {
            var candidates = vpcfEntries
                .Select(e => e.GetFullPath().Replace('\\', '/'))
                .Where(p => p.StartsWith("particles/abilities/", StringComparison.OrdinalIgnoreCase) ||
                            p.StartsWith("particles/heroes/", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            int inspected = 0;
            foreach (string vpcfPath in candidates)
            {
                if (targets.Count == 5 || inspected >= 250) break;
                inspected++;

                var sim = ParticleHierarchyDefinitionLoader.CreateVrfSimulation(
                    vpcfPath, fileLoader, pkg, null, out var rootDef, out _);
                if (sim == null) continue;

                if (sim.Children.Count > 0 && !targets.ContainsKey("ChildSystems"))
                {
                    targets["ChildSystems"] = vpcfPath;
                }

                var flattened = ParticleHierarchyDefinitionLoader.FlattenVrfSimulation(sim, vpcfPath);
                foreach (var item in flattened)
                {
                    if (item.Def == null) continue;
                    foreach (var r in item.Def.Renderers)
                    {
                        if (r is RenderSpritesDef && !targets.ContainsKey("Sprites")) targets["Sprites"] = vpcfPath;
                        else if ((r is RenderTrailsDef || r is RenderRopesDef) && !targets.ContainsKey("TrailsOrRopes")) targets["TrailsOrRopes"] = vpcfPath;
                        else if (r is RenderModelsDef && !targets.ContainsKey("Models")) targets["Models"] = vpcfPath;
                        else if ((r is RenderLightDef || r is RenderLightBeamDef) && !targets.ContainsKey("Lights")) targets["Lights"] = vpcfPath;
                    }
                }
            }
        }

        Check(targets.Count >= 4, $"Discovered real hero .vpcf_c systems covering [{string.Join(", ", targets.Select(kv => $"{kv.Key}={kv.Value}"))}].");

        // Direct VRF 21.1 ParticleSystemSimulation API verification on first candidate
        string firstVpcf = targets.Values.FirstOrDefault();
        if (!string.IsNullOrEmpty(firstVpcf))
        {
            var directSim = ParticleHierarchyDefinitionLoader.CreateVrfSimulation(
                firstVpcf, fileLoader, pkg, null, out _, out _);
            if (directSim != null)
            {
                directSim.SetCameraPosition(new System.Numerics.Vector3(0f, 100f, 60f));
                int directPeak = 0;
                const float dt = 1.0f / 60.0f;
                float worldTime = 0.0f;
                for (int i = 0; i < 15; i++)
                {
                    worldTime += dt;
                    directSim.Update(dt, worldTime);
                    directPeak = Math.Max(directPeak, CountVrfParticlesRecursive(directSim));
                }
                directSim.PlayEndCap();
                for (int i = 0; i < 5; i++)
                {
                    worldTime += dt;
                    directSim.Update(dt, worldTime);
                }
                directSim.PlayEndCapOnly();
                worldTime += dt;
                directSim.Update(dt, worldTime);
                Check(true, $"Direct VRF ParticleSystemSimulation Update/PlayEndCap/PlayEndCapOnly succeeded on '{firstVpcf}' (peak VRF particles={directPeak}).");
            }
        }

        int totalPeakRenderedParticles = 0;
        var distinctPaths = targets.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (string vpcfPath in distinctPaths)
        {
            stage.LoadParticle(
                vpcfPath,
                variantName: "SmokeTest",
                abilityName: "Vrf21Smoke",
                heroNode: chronoNode,
                fileLoader: fileLoader,
                basePackage: pkg,
                addonPackage: null,
                ownerHeroId: "chrono",
                heroVrfModel: vrfChrono);

            Check(stage.HasActiveParticle && stage.Renderers.Count > 0,
                $"ParticleStage3D mounted '{vpcfPath}' ({stage.Renderers.Count} renderer(s)).");

            stage.SetCollideWithFloor(true);
            Check(stage.CollideWithFloor, $"SetCollideWithFloor(true) active on '{vpcfPath}'.");

            int peakParticles = 0;

            // Normal mode
            stage.SetVfxPlaybackMode(VfxPlaybackMode.Normal);
            stage.Play();
            for (int f = 0; f < 18; f++)
            {
                stage._Process(1.0 / 60.0);
                peakParticles = Math.Max(peakParticles, stage.Renderers.Sum(r => GodotObject.IsInstanceValid(r) ? r.Instance.ActiveParticleCount : 0));
            }
            stage.Seek(0.25f);

            // NormalWithEndCap + TriggerRelease
            stage.SetVfxPlaybackMode(VfxPlaybackMode.NormalWithEndCap);
            stage.Play();
            for (int f = 0; f < 6; f++)
            {
                stage._Process(1.0 / 60.0);
                peakParticles = Math.Max(peakParticles, stage.Renderers.Sum(r => GodotObject.IsInstanceValid(r) ? r.Instance.ActiveParticleCount : 0));
            }
            stage.TriggerRelease();
            for (int f = 0; f < 6; f++)
            {
                stage._Process(1.0 / 60.0);
            }

            // EndCapOnly
            stage.SetVfxPlaybackMode(VfxPlaybackMode.EndCapOnly);
            stage.Play();
            for (int f = 0; f < 6; f++)
            {
                stage._Process(1.0 / 60.0);
            }

            totalPeakRenderedParticles += peakParticles;

            // Rule B.1: Every VisualInstance3D generated under ParticleStage3D MUST have Layer 21 (bit 20) cleared
            var visualNodes = new List<VisualInstance3D>();
            CollectVisualInstances(stage, visualNodes);
            var badLayerNodes = visualNodes.Where(vi => (vi.Layers & VisualLayer21Bit) != 0).ToList();
            Check(visualNodes.Count > 0 && badLayerNodes.Count == 0,
                $"Layer 21 (bit 20) cleared on all {visualNodes.Count} VisualInstance3D node(s) for '{vpcfPath}' (peak active={peakParticles}).");

            stage.UnloadActiveParticle();
        }

        Check(totalPeakRenderedParticles > 0, $"Stepped real hero particle systems produced active particles across runs (summed peak={totalPeakRenderedParticles}).");
    }

    private void RunShaderAndAnimationTests(Package pkg, VpkLoaderTest vpkLoader, Node3D chronoNode, Model vrfChrono)
    {
        GD.Print("\n[Group D] Toon Shader NPR Metadata Parity & DeadlockAnimLoader Decoding...");

        // D.1 Shader parity
        var toonShader = GD.Load<Shader>("res://assets/shaders/toon_shader.gdshader");
        Check(toonShader != null && !string.IsNullOrEmpty(toonShader.Code), "Loaded res://assets/shaders/toon_shader.gdshader cleanly.");

        Godot.Material rimVmatMat = null;
        string rimVmatPath = null;
        if (pkg.Entries.TryGetValue("vmat_c", out var vmatEntries))
        {
            var chronoVmats = vmatEntries
                .Select(e => e.GetFullPath().Replace('\\', '/'))
                .Where(p => p.StartsWith("models/heroes_staging/chrono/", StringComparison.OrdinalIgnoreCase) ||
                            p.StartsWith("models/heroes_staging/", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(60)
                .ToList();

            foreach (string vmatPath in chronoVmats)
            {
                var mat = Source2MaterialHelper.CreateMaterialFromVmat(pkg, vmatPath, null, null, "chrono");
                if (mat == null) continue;

                rimVmatMat ??= mat;
                rimVmatPath ??= vmatPath;
                if (mat.HasMeta("RimMaskTexture"))
                {
                    rimVmatMat = mat;
                    rimVmatPath = vmatPath;
                    break;
                }
            }
        }

        Check(rimVmatMat != null, $"Created Source 2 material from VPK '{rimVmatPath}'.");
        if (toonShader != null && rimVmatMat != null)
        {
            var toonMat = new ShaderMaterial { Shader = toonShader };
            Source2MaterialHelper.ApplyToonNprMetadata(toonMat, rimVmatMat, pkg);

            float expectedSelfIllum = rimVmatMat.HasMeta("SelfIllumAlbedoFactor")
                ? (float)rimVmatMat.GetMeta("SelfIllumAlbedoFactor")
                : 1.0f;
            float actualSelfIllum = toonMat.GetShaderParameter("self_illum_albedo_factor").AsSingle();
            Check(Mathf.Abs(actualSelfIllum - expectedSelfIllum) < 0.001f,
                $"ApplyToonNprMetadata transferred self_illum_albedo_factor={actualSelfIllum:F3} (expected {expectedSelfIllum:F3}).");

            if (rimVmatMat.HasMeta("RimMaskTexture"))
            {
                var rimTex = toonMat.GetShaderParameter("rim_mask_texture").As<Texture2D>();
                bool greenChan = toonMat.GetShaderParameter("rim_mask_from_green_channel").AsBool();
                Check(rimTex != null && greenChan, $"ApplyToonNprMetadata transferred RimMaskTexture and rim_mask_from_green_channel={greenChan} for '{rimVmatPath}'.");

                // Also verify VPK path fallback branch (when RimMaskTexture meta is absent but OriginalRimMaskVtexCPath is set)
                var fallbackMetaMat = new StandardMaterial3D();
                fallbackMetaMat.SetMeta("SelfIllumAlbedoFactor", 0.37f);
                fallbackMetaMat.SetMeta("OriginalRimMaskVtexCPath", (string)rimVmatMat.GetMeta("OriginalRimMaskVtexCPath"));
                var toonMatFallback = new ShaderMaterial { Shader = toonShader };
                Source2MaterialHelper.ApplyToonNprMetadata(toonMatFallback, fallbackMetaMat, pkg);
                var fallbackTex = toonMatFallback.GetShaderParameter("rim_mask_texture").As<Texture2D>();
                float fallbackIllum = toonMatFallback.GetShaderParameter("self_illum_albedo_factor").AsSingle();
                Check(fallbackTex != null && Mathf.Abs(fallbackIllum - 0.37f) < 0.001f,
                    "ApplyToonNprMetadata VPK RimMask path fallback loaded Texture2D and factor cleanly.");
            }
        }

        // D.2 Animation decoding parity (absolute/secondary .vnmskel + additive)
        var anims = vpkLoader.CurrentHeroAnimations;
        var chronoSkel = chronoNode?.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>().FirstOrDefault();
        var chronoAnimPlayer = chronoNode?.FindChildren("*", "AnimationPlayer", true, false).OfType<AnimationPlayer>().FirstOrDefault();
        IFileLoader fileLoader = vpkLoader.GetOrCreateFileLoader();

        Check(anims != null && anims.Count > 0 && chronoSkel != null && chronoAnimPlayer != null && vrfChrono != null,
            $"chrono animation environment ready ({anims?.Count ?? 0} indexed sequences).");

        if (anims != null && chronoSkel != null && chronoAnimPlayer != null && vrfChrono != null)
        {
            var idleClip = anims.FirstOrDefault(a =>
                a.IsCompatible && !a.IsAdditive &&
                a.CleanName.Contains("stand_idle", StringComparison.OrdinalIgnoreCase))
                ?? anims.FirstOrDefault(a => a.IsCompatible && !a.IsAdditive);

            int modelBoneCount = vrfChrono.Skeleton?.Bones.Length ?? 0;
            var secondaryAbsClip = anims.FirstOrDefault(a =>
                a.IsCompatible && !a.IsAdditive &&
                a.VrfAnimation is ClipAnimation ca &&
                ca.Clip.TrackCompressionSettings.Length != modelBoneCount)
                ?? idleClip;

            var additiveClip = anims.FirstOrDefault(a => a.IsCompatible && a.IsAdditive);

            Check(idleClip != null, $"Selected idle clip: '{idleClip?.CleanName}'.");
            Check(secondaryAbsClip != null, $"Selected absolute/secondary .vnmskel clip: '{secondaryAbsClip?.CleanName}' (VrfSkeleton='{secondaryAbsClip?.VrfAnimation?.TargetSkeletonName}').");
            Check(additiveClip != null, $"Selected additive clip: '{additiveClip?.CleanName}' (IsAdditive={additiveClip?.IsAdditive}).");

            Godot.Animation decodedIdle = null;
            if (idleClip != null)
            {
                decodedIdle = DeadlockAnimLoader.DecodeAnimationToGodot(
                    idleClip, chronoSkel, chronoAnimPlayer, vrfChrono, fileLoader, null, "smoke_idle", inPlace: true);
                Check(decodedIdle != null && decodedIdle.GetTrackCount() > 0 && !DeadlockAnimLoader.IsAdditiveAnimation(decodedIdle),
                    $"Decoded idle clip '{idleClip.CleanName}' ({decodedIdle?.GetTrackCount() ?? 0} tracks, IsAdditive=False).");
            }

            if (secondaryAbsClip != null)
            {
                var decodedAbs = DeadlockAnimLoader.DecodeAnimationToGodot(
                    secondaryAbsClip, chronoSkel, chronoAnimPlayer, vrfChrono, fileLoader, idleClip, "smoke_abs_secondary", inPlace: true);
                bool absIsAdditiveMeta = decodedAbs != null && decodedAbs.HasMeta("is_additive") && decodedAbs.GetMeta("is_additive").AsBool();
                Check(decodedAbs != null && decodedAbs.GetTrackCount() > 0 && !absIsAdditiveMeta && !DeadlockAnimLoader.IsAdditiveAnimation(decodedAbs),
                    $"Decoded secondary .vnmskel clip '{secondaryAbsClip.CleanName}' ({decodedAbs?.GetTrackCount() ?? 0} tracks, is_additive={absIsAdditiveMeta}).");
            }

            if (additiveClip != null)
            {
                var decodedAddWithIdle = DeadlockAnimLoader.DecodeAnimationToGodot(
                    additiveClip, chronoSkel, chronoAnimPlayer, vrfChrono, fileLoader, idleClip, "smoke_add_underlay", inPlace: true);
                bool addMeta = decodedAddWithIdle != null && decodedAddWithIdle.HasMeta("is_additive") && decodedAddWithIdle.GetMeta("is_additive").AsBool();
                Check(decodedAddWithIdle != null && decodedAddWithIdle.GetTrackCount() > 0 && addMeta,
                    $"Decoded additive clip '{additiveClip.CleanName}' ({decodedAddWithIdle?.GetTrackCount() ?? 0} tracks, is_additive={addMeta}, Rule D.6 IsAdditiveAnimation={DeadlockAnimLoader.IsAdditiveAnimation(decodedAddWithIdle)}).");

                if (decodedIdle != null)
                {
                    var decodedAddRaw = DeadlockAnimLoader.DecodeAnimationToGodot(
                        additiveClip, chronoSkel, chronoAnimPlayer, vrfChrono, fileLoader, null, "smoke_add_raw", inPlace: true);
                    var composed = DeadlockAnimLoader.ComposeOverIdle(decodedAddRaw, decodedIdle, chronoSkel);
                    bool composedMeta = composed != null && composed.HasMeta("composed") && composed.GetMeta("composed").AsBool();
                    bool composedIsAdd = composed == null || DeadlockAnimLoader.IsAdditiveAnimation(composed);
                    Check(composed != null && composed.GetTrackCount() > 0 && composedMeta && !composedIsAdd,
                        $"ComposeOverIdle verified for '{additiveClip.CleanName}' ({composed?.GetTrackCount() ?? 0} tracks, composed={composedMeta}, IsAdditiveAnimation={composedIsAdd}).");
                }
            }
        }
    }

    private static bool WithModelDeferred(Package pkg, string vpkPath, Action<Model> inspect)
    {
        if (pkg == null || string.IsNullOrEmpty(vpkPath) || inspect == null) return false;
        string normalized = vpkPath.Replace('\\', '/').TrimStart('/');
        var entry = pkg.FindEntry(normalized);
        if (entry == null && !normalized.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
        {
            normalized += "_c";
            entry = pkg.FindEntry(normalized);
        }
        if (entry == null) return false;

        pkg.ReadEntry(entry, out byte[] data);
        if (data == null || data.Length == 0) return false;

        using var ms = new MemoryStream(data);
        using var res = new ValveResourceFormat.Resource { FileName = normalized };
        res.Read(ms, leaveOpen: true, mode: ReadMode.Deferred);
        if (res.DataBlock is not Model model) return false;
        inspect(model);
        return true;
    }

    private static int CountVrfParticlesRecursive(ParticleSystemSimulation sim)
    {
        if (sim == null) return 0;
        int total = sim.Particles.Current.Length;
        foreach (var child in sim.Children)
        {
            total += CountVrfParticlesRecursive(child);
        }
        return total;
    }

    private static void CollectVisualInstances(Node root, List<VisualInstance3D> results)
    {
        if (root == null) return;
        if (root is VisualInstance3D vi)
        {
            results.Add(vi);
        }
        foreach (Node child in root.GetChildren())
        {
            CollectVisualInstances(child, results);
        }
    }
}
