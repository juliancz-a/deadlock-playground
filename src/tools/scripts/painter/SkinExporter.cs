using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using VrfMaterial = ValveResourceFormat.ResourceTypes.Material;
using DeadlockPlayground.Catalog;
using DeadlockPlayground.Workspace;

namespace DeadlockPlayground.Painter
{
    public enum ExportLogLevel
    {
        Info,
        Success,
        Warning,
        Error
    }

    public class ExportProgressReport
    {
        public float Progress { get; set; } // 0.0 to 1.0
        public string Message { get; set; }
        public ExportLogLevel Level { get; set; } = ExportLogLevel.Info;

        public ExportProgressReport(float progress, string message, ExportLogLevel level = ExportLogLevel.Info)
        {
            Progress = progress;
            Message = message;
            Level = level;
        }
    }

    public partial class ModExportConfig : RefCounted
    {
        public string ModName { get; set; } = "CustomSkin";
        public string HeroCodename { get; set; } = "hero";
        public string HeroDisplayName { get; set; } = "Hero";
        public List<SubmeshNodeInfo> TargetSubmeshes { get; set; } = new();
        public int Resolution { get; set; } = 2048; // 1024, 2048, 4096
        public bool InstallDirectlyToGame { get; set; } = true;
        public string CustomExportFolder { get; set; } = "";
        public string DeadlockGamePath { get; set; } = "";
        public string CsdkPath { get; set; } = "";
        public string ResourceCompilerPath { get; set; } = "";
        public Image PreBakedAtlas { get; set; } // Pre-extracted on Godot main thread
        public Dictionary<int, Image> PreBakedAtlasesBySurface { get; set; } = new();
        public Dictionary<int, Image> PreExtractedBaseImages { get; set; } = new();
        public Dictionary<int, (Vector2 pos, Vector2 size)> SubmeshAtlasRects { get; set; } = new();
        public Dictionary<string, Image> PreBakedAtlasesBySubmesh { get; set; } = new();
        public Dictionary<string, Image> PreExtractedBaseImagesBySubmesh { get; set; } = new();
        public Dictionary<string, (Vector2 pos, Vector2 size)> SubmeshAtlasRectsBySubmesh { get; set; } = new();
        public HashSet<string> ExcludedAssetVpkPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public class SkinExportResult
    {
        public bool Success { get; set; }
        public string PngPath { get; set; }
        public string VmatPath { get; set; }
        public string VpkPath { get; set; }
        public string ModDirectory { get; set; }
        public string ErrorMessage { get; set; }
    }

    public partial class SkinExporter : RefCounted
    {
        private readonly SkinLayerManager _layerManager;

        public SkinExporter(SkinLayerManager layerManager)
        {
            _layerManager = layerManager;
        }

        /// <summary>
        /// Captures the active GPU composite atlas image on the main thread before starting background compilation.
        /// </summary>
        public Image CaptureAtlasImage()
        {
            return _layerManager?.BakeCompositeImage();
        }

        /// <summary>
        /// Executes the end-to-end automated mod export pipeline in a background thread.
        /// Step A: Bake composite textures to PNG.
        /// Step B: Generate KV3 .vmat files.
        /// Step C: Compile via headless resourcecompiler.exe CLI.
        /// Step D: Package into VPK archive with ValvePak and clean up staging.
        /// </summary>
        public async Task<SkinExportResult> ExportModAsync(
            ModExportConfig config,
            IProgress<ExportProgressReport> progress,
            CancellationToken ct)
        {
            var result = new SkinExportResult();

            if (config == null)
            {
                result.Success = false;
                result.ErrorMessage = "Export configuration is null.";
                return result;
            }

            // Pre-extract all textures, materials, and atlas rects on the Godot MAIN THREAD before entering worker thread
            var candidateSubmeshes = config.TargetSubmeshes?.Where(s => s.IsSelected && s.IsDirty).ToList();
            if (candidateSubmeshes == null || candidateSubmeshes.Count == 0)
            {
                candidateSubmeshes = config.TargetSubmeshes ?? new List<SubmeshNodeInfo>();
            }

            foreach (var submesh in candidateSubmeshes)
            {
                if (submesh == null) continue;
                int sIdx = submesh.SurfaceIndex;
                string subKey = ResolveSubmeshKey(submesh);

                if (!config.PreBakedAtlasesBySubmesh.ContainsKey(subKey))
                {
                    var baked = _layerManager?.BakeCompositeImageForSubmesh(submesh);
                    if (baked != null && !baked.IsEmpty())
                    {
                        config.PreBakedAtlasesBySubmesh[subKey] = baked;
                        config.PreBakedAtlasesBySurface[sIdx] = baked;
                    }
                    else if (config.PreBakedAtlas != null && !config.PreBakedAtlas.IsEmpty())
                    {
                        config.PreBakedAtlasesBySubmesh[subKey] = config.PreBakedAtlas;
                        config.PreBakedAtlasesBySurface[sIdx] = config.PreBakedAtlas;
                    }
                }

                if (!config.PreExtractedBaseImagesBySubmesh.ContainsKey(subKey) && submesh.Mesh != null)
                {
                    var mat = HeroMeshHierarchy.GetAuthenticMaterial(submesh.Mesh, submesh.SurfaceIndex)
                           ?? submesh.Mesh.GetSurfaceOverrideMaterial(submesh.SurfaceIndex) 
                           ?? submesh.Mesh.Mesh?.SurfaceGetMaterial(submesh.SurfaceIndex)
                           ?? submesh.Mesh.MaterialOverride;

                    if (mat != null)
                    {
                        var baseTex = SkinLayerManager.ExtractBaseTexture(mat);
                        if (baseTex != null)
                        {
                            var bImg = baseTex.GetImage();
                            if (bImg != null && !bImg.IsEmpty())
                            {
                                if (bImg.IsCompressed()) bImg.Decompress();
                                var dup = (Image)bImg.Duplicate();
                                config.PreExtractedBaseImagesBySubmesh[subKey] = dup;
                                config.PreExtractedBaseImages[sIdx] = dup;
                            }
                        }
                    }
                }

                if (!config.SubmeshAtlasRectsBySubmesh.ContainsKey(subKey) && submesh.Mesh != null && submesh.Mesh.MaterialOverlay is ShaderMaterial sm)
                {
                    var posVar = sm.GetShaderParameter("position_in_atlas");
                    var sizeVar = sm.GetShaderParameter("size_in_atlas");
                    if (posVar.VariantType == Variant.Type.Vector2 && sizeVar.VariantType == Variant.Type.Vector2)
                    {
                        var rect = (posVar.AsVector2(), sizeVar.AsVector2());
                        config.SubmeshAtlasRectsBySubmesh[subKey] = rect;
                        config.SubmeshAtlasRects[sIdx] = rect;
                    }
                }
            }

            return await Task.Run(() =>
            {
                string csdkPath = ResolveEffectiveCsdkPath(config.CsdkPath, config.DeadlockGamePath);
                string deadlockPath = config.DeadlockGamePath;

                progress?.Report(new ExportProgressReport(0.05f, $"[INFO] Starting export for '{config.HeroDisplayName}' ({config.ModName})..."));

                // Validate CSDK / ResourceCompiler
                string compilerExe = ResolveCompilerExecutable(csdkPath, config.ResourceCompilerPath);
                if (string.IsNullOrEmpty(compilerExe) || !File.Exists(compilerExe))
                {
                    // Check configured path manager
                    var pm = new GamePathManager();
                    pm.LoadConfig();
                    compilerExe = pm.ResolveResourceCompilerExe();
                }

                if (string.IsNullOrEmpty(compilerExe) || !File.Exists(compilerExe))
                {
                    string err = $"resourcecompiler.exe not found. Please specify the path to resourcecompiler.exe in the Export dialog or Settings.";
                    progress?.Report(new ExportProgressReport(0.05f, $"[ERROR] {err}", ExportLogLevel.Error));
                    result.Success = false;
                    result.ErrorMessage = err;
                    return result;
                }

                // Auto-derive csdkPath if not set or invalid
                if (string.IsNullOrEmpty(csdkPath) || !Directory.Exists(csdkPath))
                {
                    string norm = compilerExe.Replace('\\', '/');
                    int binIdx = norm.IndexOf("/game/bin/win64", StringComparison.OrdinalIgnoreCase);
                    if (binIdx > 0)
                    {
                        csdkPath = norm.Substring(0, binIdx);
                    }
                    else
                    {
                        csdkPath = Path.GetDirectoryName(compilerExe);
                    }
                }

                progress?.Report(new ExportProgressReport(0.10f, $"[INFO] Compiler located: {compilerExe}"));
                progress?.Report(new ExportProgressReport(0.11f, $"[INFO] Staging root: {csdkPath}"));

                // Resolve Target Destination Directory
                string targetDir = config.InstallDirectlyToGame
                    ? Path.Combine(deadlockPath, "game", "citadel", "addons")
                    : config.CustomExportFolder;

                if (!Directory.Exists(targetDir))
                {
                    try { Directory.CreateDirectory(targetDir); }
                    catch (Exception ex)
                    {
                        string err = $"Failed to create target directory '{targetDir}': {ex.Message}";
                        progress?.Report(new ExportProgressReport(0.10f, $"[ERROR] {err}", ExportLogLevel.Error));
                        result.Success = false;
                        result.ErrorMessage = err;
                        return result;
                    }
                }

                // Resolve VPK filename via VpkIndexResolver
                string vpkFileName;
                if (config.InstallDirectlyToGame)
                {
                    vpkFileName = VpkIndexResolver.ResolveNextFileName(targetDir);
                    int highest = VpkIndexResolver.GetExistingIndices(targetDir).LastOrDefault();
                    progress?.Report(new ExportProgressReport(0.12f, $"[INFO] Detected highest addon: pak{highest:D2}_dir.vpk. Assigned: {vpkFileName}"));
                }
                else
                {
                    vpkFileName = $"{config.ModName}.vpk";
                    progress?.Report(new ExportProgressReport(0.12f, $"[INFO] Target custom output file: {vpkFileName}"));
                }

                string finalVpkPath = Path.Combine(targetDir, vpkFileName);

                // Determine internal hero folder: models/heroes_staging/<hero> or models/heroes_wip/<hero>
                string heroFolder = ResolveHeroInternalFolder(config.HeroCodename, config.TargetSubmeshes);
                progress?.Report(new ExportProgressReport(0.14f, $"[INFO] Resolved internal hero path: {heroFolder}"));

                // Setup Staging Paths in CSDK:
                // {csdkPath}/content/citadel_addons/_temp_export/{heroFolder}/materials/
                // {csdkPath}/game/citadel_addons/_temp_export/{heroFolder}/materials/
                string contentStagingRoot = Path.Combine(csdkPath, "content", "citadel_addons", "_temp_export");
                string gameStagingRoot = Path.Combine(csdkPath, "game", "citadel_addons", "_temp_export");
                string contentMaterialsDir = Path.Combine(contentStagingRoot, heroFolder, "materials");
                string gameMaterialsDir = Path.Combine(gameStagingRoot, heroFolder, "materials");

                try
                {
                    if (Directory.Exists(contentStagingRoot)) Directory.Delete(contentStagingRoot, true);
                    if (Directory.Exists(gameStagingRoot)) Directory.Delete(gameStagingRoot, true);
                    Directory.CreateDirectory(contentMaterialsDir);
                }
                catch (Exception ex)
                {
                    string err = $"Failed to initialize staging folders: {ex.Message}";
                    progress?.Report(new ExportProgressReport(0.14f, $"[ERROR] {err}", ExportLogLevel.Error));
                    result.Success = false;
                    result.ErrorMessage = err;
                    return result;
                }

                var submeshesToExport = config.TargetSubmeshes != null && config.TargetSubmeshes.Count > 0
                    ? config.TargetSubmeshes.Where(s => s.IsSelected && s.IsDirty).ToList()
                    : new List<SubmeshNodeInfo>();

                // Fallback: If no submesh is marked dirty but user explicitly selected submeshes
                if (submeshesToExport.Count == 0 && config.TargetSubmeshes != null && config.TargetSubmeshes.Count > 0)
                {
                    submeshesToExport = config.TargetSubmeshes.Where(s => s.IsSelected).ToList();
                }

                var workspaceSession = ModWorkspaceSession.Instance;
                var workspaceAssets = new List<DeadlockPlayground.Workspace.WorkspaceAssetEntry>();
                if (workspaceSession != null && workspaceSession.Manifest.Heroes.TryGetValue(config.HeroCodename, out var targetHeroEntry) && targetHeroEntry != null)
                {
                    workspaceAssets.AddRange(targetHeroEntry.Assets);
                }                
                int workspaceTexCount = workspaceAssets.Sum(a => a.ModifiedTextures.Count);
                int workspaceVpcfCount = workspaceAssets.Count(a => a.Category == "ParticleVFX" && !string.IsNullOrEmpty(a.ParticleOverridesFile));

                if (submeshesToExport.Count == 0 && workspaceTexCount == 0 && workspaceVpcfCount == 0)
                {
                    string err = "No modified submeshes or workspace assets selected for compilation. Base game submeshes were left untouched.";
                    progress?.Report(new ExportProgressReport(0.15f, $"[WARNING] {err}", ExportLogLevel.Warning));
                    result.Success = false;
                    result.ErrorMessage = err;
                    return result;
                }

                // Open base pak01_dir.vpk if available to extract authentic normal/AO/mask textures
                Package baseVpkPackage = null;
                try
                {
                    string baseVpkPath = !string.IsNullOrEmpty(config.DeadlockGamePath)
                        ? Path.Combine(config.DeadlockGamePath, "game", "citadel", "pak01_dir.vpk")
                        : null;

                    if (!string.IsNullOrEmpty(baseVpkPath) && File.Exists(baseVpkPath))
                    {
                        try
                        {
                            baseVpkPackage = new Package();
                            baseVpkPackage.Read(baseVpkPath);
                        }
                        catch (Exception ex)
                        {
                            GD.Print($"[SkinExporter] Notice: Could not pre-read base pak01_dir.vpk: {ex.Message}");
                            baseVpkPackage = null;
                        }
                    }

                    // =========================================================================
                    // STEP A: Bake Composite Texture to High-Fidelity PNG & DMX VTEX (20%)
                    // =========================================================================
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new ExportProgressReport(0.15f, $"[INFO] Baking {submeshesToExport.Count} composite texture(s) ({config.Resolution}x{config.Resolution})..."));
                    var stagedTextures = new List<(string materialName, string pngPath, string vtexPath, string originalVtexCPath)>();
                    var processedHeroVtexCPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var submesh in submeshesToExport)
                    {
                        ct.ThrowIfCancellationRequested();

                        string matName = ResolveSubmeshMaterialName(submesh, config.HeroCodename);
                        string originalColorVtexC = ResolveOriginalColorVtexCPath(submesh, config.HeroCodename, baseVpkPackage);

                        if (!processedHeroVtexCPaths.Add(originalColorVtexC))
                        {
                            progress?.Report(new ExportProgressReport(0.18f, $"[INFO] Skipping duplicate material export for submesh '{submesh.DisplayName}' (shares {Path.GetFileName(originalColorVtexC)})"));
                            continue;
                        }
                        progress?.Report(new ExportProgressReport(0.18f, $"[INFO] Submesh '{submesh.DisplayName}' intercepts: {originalColorVtexC}"));

                        string pngFileName = $"{matName}_color.png";
                        string fullPngPath = Path.Combine(contentMaterialsDir, pngFileName);

                        progress?.Report(new ExportProgressReport(0.20f, $"[INFO] Baking {submesh.DisplayName} composite -> {pngFileName}..."));

                        Image submeshImage = ExtractAndBlendSubmeshImage(config, submesh, config.Resolution);
                        if (submeshImage == null)
                        {
                            string err = $"Failed to extract image for submesh '{submesh.DisplayName}'.";
                            progress?.Report(new ExportProgressReport(0.20f, $"[ERROR] {err}", ExportLogLevel.Error));
                            result.Success = false;
                            result.ErrorMessage = err;
                            return result;
                        }

                        Error saveErr = submeshImage.SavePng(fullPngPath);
                        if (saveErr != Error.Ok)
                        {
                            string err = $"Failed to write PNG '{fullPngPath}' (Code: {saveErr}).";
                            progress?.Report(new ExportProgressReport(0.25f, $"[ERROR] {err}", ExportLogLevel.Error));
                            result.Success = false;
                            result.ErrorMessage = err;
                            return result;
                        }

                        // Generate companion DMX .vtex descriptor with addon-relative PNG path
                        string vtexFileName = $"{matName}_color.vtex";
                        string fullVtexPath = Path.Combine(contentMaterialsDir, vtexFileName);
                        string relativePngPath = $"{heroFolder}/materials/{pngFileName}".Replace('\\', '/').TrimStart('/');
                        string vtexContent = GenerateSource2DmxVtex(relativePngPath);
                        File.WriteAllText(fullVtexPath, vtexContent);
                        progress?.Report(new ExportProgressReport(0.28f, $"[INFO] Generated DMX VTEX descriptor: {vtexFileName} (m_fileName: {relativePngPath})"));

                        stagedTextures.Add((matName, fullPngPath, fullVtexPath, originalColorVtexC));
                    }

                    // =========================================================================
                    // STEP B.2: Incorporate Multi-Asset Workspace Staged Textures
                    // =========================================================================
                    try
                    {
                        var existingVtexPaths = new HashSet<string>(stagedTextures.Select(t => t.originalVtexCPath), StringComparer.OrdinalIgnoreCase);

                        foreach (var asset in workspaceAssets)
                        {
                            if (config.ExcludedAssetVpkPaths != null &&
                                (config.ExcludedAssetVpkPaths.Contains(asset.VpkPath) || (!string.IsNullOrEmpty(asset.OwnerAssetPath) && config.ExcludedAssetVpkPaths.Contains(asset.OwnerAssetPath))))
                            {
                                continue;
                            }

                            foreach (var (vtexKey, relPath) in asset.ModifiedTextures)
                            {
                                if (existingVtexPaths.Contains(vtexKey)) continue;

                                string stagedPngFull = Path.Combine(workspaceSession.SessionDir, relPath);
                                var inMemImg = workspaceSession.GetStagedTexture(vtexKey);
                                if (!File.Exists(stagedPngFull) && (inMemImg == null || inMemImg.IsEmpty())) continue;

                                string normVtex = vtexKey.Replace('\\', '/').TrimStart('/');
                                if (normVtex.StartsWith("particle:", StringComparison.OrdinalIgnoreCase))
                                {
                                    normVtex = normVtex.Substring("particle:".Length).TrimStart('/');
                                }
                                string assetFolder = Path.GetDirectoryName(normVtex)?.Replace('\\', '/');
                                if (string.IsNullOrEmpty(assetFolder)) assetFolder = "materials/particle";

                                string assetContentDir = Path.Combine(contentStagingRoot, assetFolder);
                                Directory.CreateDirectory(assetContentDir);

                                string baseName = Path.GetFileNameWithoutExtension(normVtex);
                                if (baseName.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
                                {
                                    baseName = baseName.Substring(0, baseName.Length - 2);
                                }

                                string targetPngFileName = $"{baseName}.png";
                                string targetPngFull = Path.Combine(assetContentDir, targetPngFileName);
                                if (File.Exists(stagedPngFull))
                                {
                                    File.Copy(stagedPngFull, targetPngFull, true);
                                }
                                else if (inMemImg != null)
                                {
                                    if (inMemImg.HasMipmaps()) inMemImg.ClearMipmaps();
                                    inMemImg.SavePng(targetPngFull);
                                }

                                string targetVtexFileName = $"{baseName}.vtex";
                                string targetVtexFull = Path.Combine(assetContentDir, targetVtexFileName);
                                string relPng = $"{assetFolder}/{targetPngFileName}".Replace('\\', '/').TrimStart('/');
                                string vtexDesc = GenerateSource2DmxVtex(relPng);
                                File.WriteAllText(targetVtexFull, vtexDesc);

                                string targetCompiledVtexCPath = normVtex;
                                if (!targetCompiledVtexCPath.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
                                {
                                    targetCompiledVtexCPath += "_c";
                                }

                                stagedTextures.Add((baseName, targetPngFull, targetVtexFull, targetCompiledVtexCPath));
                                existingVtexPaths.Add(vtexKey);
                                existingVtexPaths.Add(targetCompiledVtexCPath);

                                progress?.Report(new ExportProgressReport(0.36f, $"[INFO] Included workspace staged texture: {vtexKey}"));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        GD.PrintErr($"[SkinExporter] Notice: Non-fatal error collecting workspace textures: {ex.Message}");
                    }

                    progress?.Report(new ExportProgressReport(0.38f, $"[SUCCESS] All {stagedTextures.Count} custom texture(s) staged successfully across workspace.", ExportLogLevel.Success));

                    // =========================================================================
                    // STEP C: Compile Source 2 Textures via Headless resourcecompiler.exe (40% -> 85%)
                    // =========================================================================
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new ExportProgressReport(0.40f, $"[INFO] Beginning headless texture compilation via: {Path.GetFileName(compilerExe)}..."));

                    string compilerWorkingDir = Path.GetDirectoryName(compilerExe);
                    var compiledOutputFiles = new List<(string vtexC, string originalVtexCPath)>();

                    int totalTextures = stagedTextures.Count;
                    float texSlice = 0.45f / Math.Max(totalTextures, 1);

                    for (int tIdx = 0; tIdx < totalTextures; tIdx++)
                    {
                        var (matName, pngPath, vtexPath, originalVtexCPath) = stagedTextures[tIdx];
                        ct.ThrowIfCancellationRequested();

                        float texBase = 0.40f + (tIdx * texSlice);
                        float vtexProgress = texBase + (texSlice * 0.10f);
                        float vtexDoneProgress = texBase + (texSlice * 0.90f);

                        // Compile Texture (.vtex -> .vtex_c)
                        progress?.Report(new ExportProgressReport(vtexProgress, $"[INFO] Compiling texture ({tIdx + 1}/{totalTextures}): {Path.GetFileName(vtexPath)}..."));

                        var vtexArgs = new List<string>
                        {
                            "-i", $"\"{vtexPath.Replace('\\', '/')}\"",
                            "-nop4"
                        };

                        int vtexExitCode = RunCompilerProcess(compilerExe, compilerWorkingDir, string.Join(" ", vtexArgs), progress, ct, vtexProgress + 0.05f);

                        // Exact file match without wildcards to avoid prefix collisions
                        string expectedVtexFileName = $"{Path.GetFileNameWithoutExtension(vtexPath)}.vtex_c";
                        string expectedVtexC = LocateEmittedFile(gameMaterialsDir, contentMaterialsDir, expectedVtexFileName, gameStagingRoot, contentStagingRoot);
                        bool vtexEmitted = !string.IsNullOrEmpty(expectedVtexC) && File.Exists(expectedVtexC);

                        if (vtexExitCode != 0 && !vtexEmitted)
                        {
                            string err = $"resourcecompiler.exe failed (exit code {vtexExitCode}) while compiling {Path.GetFileName(vtexPath)}";
                            progress?.Report(new ExportProgressReport(vtexDoneProgress, $"[ERROR] {err}", ExportLogLevel.Error));
                            result.Success = false;
                            result.ErrorMessage = err;
                            return result;
                        }

                        if (!vtexEmitted)
                        {
                            string err = $"Compilation completed, but expected texture binary '{expectedVtexFileName}' was not found.";
                            progress?.Report(new ExportProgressReport(vtexDoneProgress, $"[ERROR] {err}", ExportLogLevel.Error));
                            result.Success = false;
                            result.ErrorMessage = err;
                            return result;
                        }

                        progress?.Report(new ExportProgressReport(vtexDoneProgress, $"[SUCCESS] Compiled texture ({tIdx + 1}/{totalTextures}): {Path.GetFileName(expectedVtexC)}", ExportLogLevel.Success));

                        compiledOutputFiles.Add((expectedVtexC, originalVtexCPath));
                    }

                    progress?.Report(new ExportProgressReport(0.85f, $"[SUCCESS] All {totalTextures} texture(s) compiled successfully to .vtex_c.", ExportLogLevel.Success));

                    // =========================================================================
                    // STEP C.2: Stage & Compile Modified Particle Systems (.vpcf -> .vpcf_c)
                    // =========================================================================
                    if (baseVpkPackage != null)
                    {
                        foreach (var asset in workspaceAssets)
                        {
                            if (asset.Category != "ParticleVFX" || string.IsNullOrEmpty(asset.ParticleOverridesFile)) continue;
                            if (config.ExcludedAssetVpkPaths != null &&
                                (config.ExcludedAssetVpkPaths.Contains(asset.VpkPath) || (!string.IsNullOrEmpty(asset.OwnerAssetPath) && config.ExcludedAssetVpkPaths.Contains(asset.OwnerAssetPath))))
                            {
                                continue;
                            }

                            var pOverrides = workspaceSession.GetStagedParticleOverrides(asset.VpkPath);
                            if (pOverrides == null || pOverrides.Emitters == null || pOverrides.Emitters.Count == 0) continue;

                            // Agrupar overrides por cada archivo .vpcf hijo (ej. chrono_time_wall_charge_beams.vpcf)
                            var vpcfTargets = new Dictionary<string, EmitterOverrideData>(StringComparer.OrdinalIgnoreCase);
                            foreach (var em in pOverrides.Emitters.Values)
                            {
                                string targetVpcf = !string.IsNullOrEmpty(em.ChildVpcfPath) ? em.ChildVpcfPath : asset.VpkPath;
                                if (!string.IsNullOrEmpty(targetVpcf))
                                {
                                    vpcfTargets[targetVpcf] = em;
                                }
                            }

                            foreach (var (rawVpcfPath, emOverride) in vpcfTargets)
                            {
                                ct.ThrowIfCancellationRequested();
                                string normVpcf = rawVpcfPath.Replace('\\', '/').TrimStart('/');
                                if (normVpcf.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
                                    normVpcf = normVpcf[..^2];
                                string compiledVpcfPath = normVpcf + "_c";

                                var vpkEntry = baseVpkPackage.FindEntry(compiledVpcfPath);
                                if (vpkEntry == null) continue;

                                baseVpkPackage.ReadEntry(vpkEntry, out byte[] rawVpcfBytes);
                                using var vrfRes = new ValveResourceFormat.Resource();
                                using var ms = new MemoryStream(rawVpcfBytes);
                                vrfRes.Read(ms);

                                string kv3Text = vrfRes.DataBlock?.ToString();
                                if (string.IsNullOrEmpty(kv3Text)) continue;

                                // Check if the emitter override actually introduces meaningful modifications compared to vanilla KV3
                                if (!HasMeaningfulVpcfOverrides(kv3Text, emOverride))
                                {
                                    GD.Print($"[SkinExporter] Skipping untouched particle definition: {normVpcf}");
                                    continue;
                                }

                                // Aplicar modificaciones al texto KV3 del .vpcf
                                kv3Text = ApplyOverridesToVpcfKv3Text(kv3Text, emOverride);

                                string vpcfFolder = Path.GetDirectoryName(normVpcf)?.Replace('\\', '/') ?? "particles";
                                string vpcfContentDir = Path.Combine(contentStagingRoot, vpcfFolder);
                                string vpcfGameDir = Path.Combine(gameStagingRoot, vpcfFolder);
                                Directory.CreateDirectory(vpcfContentDir);

                                string vpcfFileName = Path.GetFileName(normVpcf);
                                string fullVpcfContentPath = Path.Combine(vpcfContentDir, vpcfFileName);
                                // Asegurar que todas las texturas referenciadas en el .vpcf existan en gameStagingRoot para que resourcecompiler.exe no falle
                                foreach (System.Text.RegularExpressions.Match resMatch in System.Text.RegularExpressions.Regex.Matches(kv3Text, @"resource:""([^""]+)"""))
                                {
                                    string rawDep = resMatch.Groups[1].Value.Replace('\\', '/').Trim().TrimStart('/');
                                    string compiledDep = rawDep.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? rawDep : rawDep + "_c";
                                    EnsureTextureStagedInGameTree(gameStagingRoot, compiledDep, baseVpkPackage);
                                }
                                File.WriteAllText(fullVpcfContentPath, kv3Text);

                                string expectedVpcfCName = vpcfFileName + "_c";
                                string stagingGameVpcfC = Path.Combine(vpcfGameDir, expectedVpcfCName);
                                if (File.Exists(stagingGameVpcfC))
                                {
                                    try { File.Delete(stagingGameVpcfC); } catch { }
                                }

                                progress?.Report(new ExportProgressReport(0.82f, $"[INFO] Compiling modified particle definition: {vpcfFileName}..."));

                                var vpcfArgs = new List<string>
                                {
                                    "-i", $"\"{fullVpcfContentPath.Replace('\\', '/')}\"",
                                    "-nop4",
                                    "-v"
                                };
                                int exitCode = RunCompilerProcess(compilerExe, compilerWorkingDir, string.Join(" ", vpcfArgs), progress, ct, 0.83f);

                                string emittedVpcfC = LocateEmittedFile(vpcfGameDir, vpcfContentDir, expectedVpcfCName, gameStagingRoot, contentStagingRoot);
                                if (exitCode == 0 && !string.IsNullOrEmpty(emittedVpcfC) && File.Exists(emittedVpcfC))
                                {
                                    compiledOutputFiles.Add((emittedVpcfC, compiledVpcfPath));
                                    progress?.Report(new ExportProgressReport(0.84f, $"[SUCCESS] Compiled particle: {expectedVpcfCName}", ExportLogLevel.Success));
                                }
                                else
                                {
                                    string err = $"resourcecompiler.exe failed (exit code {exitCode}) while compiling particle '{vpcfFileName}'. Check compiler log for details.";
                                    progress?.Report(new ExportProgressReport(0.84f, $"[ERROR] {err}", ExportLogLevel.Error));
                                    result.Success = false;
                                    result.ErrorMessage = err;
                                    return result;
                                }
                            }
                        }
                    }
                    // =========================================================================
                    // STEP D: Package Intercepted VTEX Binaries into VPK Archive (85% -> 100%)
                    // =========================================================================
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new ExportProgressReport(0.85f, $"[INFO] Packing intercepted textures into VPK archive '{vpkFileName}'..."));

                    try
                    {
                        using (var package = new Package())
                        {
                            foreach (var (vtexC, originalVtexCPath) in compiledOutputFiles)
                            {
                                if (File.Exists(vtexC))
                                {
                                    byte[] vtexData = File.ReadAllBytes(vtexC);
                                    string internalVtexPath = originalVtexCPath.Replace('\\', '/').TrimStart('/');
                                    package.AddFile(internalVtexPath, vtexData);
                                    progress?.Report(new ExportProgressReport(0.90f, $"[INFO] Added intercepted texture to VPK: {internalVtexPath} ({vtexData.Length / 1024} KB)"));
                                }
                            }

                            package.Write(finalVpkPath);
                        }

                        if (!File.Exists(finalVpkPath))
                        {
                            throw new FileNotFoundException($"VPK package write failed, file not found at: {finalVpkPath}");
                        }

                        long vpkSize = new FileInfo(finalVpkPath).Length;
                        progress?.Report(new ExportProgressReport(0.95f, $"[SUCCESS] VPK archive successfully created ({vpkSize / 1024} KB).", ExportLogLevel.Success));
                    }
                    catch (Exception ex)
                    {
                        string err = $"Failed to package VPK: {ex.Message}";
                        progress?.Report(new ExportProgressReport(0.90f, $"[ERROR] {err}", ExportLogLevel.Error));
                        result.Success = false;
                        result.ErrorMessage = err;
                        return result;
                    }

                // Clean up temporary staging folders in CSDK
                try
                {
                    if (Directory.Exists(contentStagingRoot)) Directory.Delete(contentStagingRoot, true);
                    if (Directory.Exists(gameStagingRoot)) Directory.Delete(gameStagingRoot, true);
                    progress?.Report(new ExportProgressReport(0.98f, $"[INFO] Cleaned up temporary staging files."));
                }
                catch (Exception ex)
                {
                    GD.Print($"[SkinExporter] Non-fatal staging cleanup notice: {ex.Message}");
                }

                // Final 100% confirmation
                string installNote = config.InstallDirectlyToGame
                    ? $"Mod successfully installed to citadel/addons/{vpkFileName}"
                    : $"Mod successfully exported to {finalVpkPath}";

                progress?.Report(new ExportProgressReport(1.0f, $"[SUCCESS] {installNote}", ExportLogLevel.Success));

                result.Success = true;
                result.VpkPath = finalVpkPath;
                result.ModDirectory = targetDir;
                return result;
            }
            finally
            {
                baseVpkPackage?.Dispose();
            }
        }, ct);
        }

        private static string ResolveEffectiveCsdkPath(string userCsdkPath, string deadlockGamePath)
        {
            if (!string.IsNullOrEmpty(userCsdkPath) && Directory.Exists(userCsdkPath))
            {
                return userCsdkPath;
            }

            // Check if deadlock path has compiler
            if (!string.IsNullOrEmpty(deadlockGamePath))
            {
                string p1 = Path.Combine(deadlockGamePath, "game", "bin", "win64", "resourcecompiler.exe");
                if (File.Exists(p1)) return deadlockGamePath;

                string siblingCsdk = Path.GetFullPath(Path.Combine(deadlockGamePath, "..", "CSDK12"));
                if (Directory.Exists(siblingCsdk)) return siblingCsdk;
            }

            string[] defaults = {
                @"C:\CSDK12",
                @"D:\CSDK12",
                @"E:\CSDK12",
                @"E:\SteamLibrary\steamapps\common\CSDK12"
            };

            foreach (var d in defaults)
            {
                if (Directory.Exists(d)) return d;
            }

            return !string.IsNullOrEmpty(deadlockGamePath) ? deadlockGamePath : "";
        }

        private static string ResolveCompilerExecutable(string csdkPath, string explicitPath = null)
        {
            if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
            {
                // If user selected bin\win64\resourcecompiler.exe, prioritize bin_cs2 sibling!
                string norm = explicitPath.Replace('\\', '/');
                if (norm.Contains("/game/bin/win64/resourcecompiler.exe", StringComparison.OrdinalIgnoreCase))
                {
                    string binCs2Sibling = norm.Replace("/game/bin/win64/resourcecompiler.exe", "/game/bin_cs2/win64/resourcecompiler.exe", StringComparison.OrdinalIgnoreCase);
                    if (File.Exists(binCs2Sibling))
                    {
                        return binCs2Sibling;
                    }
                }
                return explicitPath;
            }

            if (!string.IsNullOrEmpty(csdkPath))
            {
                // 1. Isolated bin_cs2 compiler (prevents particleslib schema mismatch aborts)
                string p1 = Path.Combine(csdkPath, "game", "bin_cs2", "win64", "resourcecompiler.exe");
                if (File.Exists(p1)) return p1;

                // 2. Reduced_CSDK_12 nested
                string p2 = Path.Combine(csdkPath, "Reduced_CSDK_12", "game", "bin_cs2", "win64", "resourcecompiler.exe");
                if (File.Exists(p2)) return p2;

                // 3. Fallbacks
                string p3 = Path.Combine(csdkPath, "game", "bin", "win64", "resourcecompiler.exe");
                if (File.Exists(p3)) return p3;

                string p4 = Path.Combine(csdkPath, "resourcecompiler.exe");
                if (File.Exists(p4)) return p4;
            }

            return null;
        }

        public static string ResolveHeroInternalFolder(string heroCodename, List<SubmeshNodeInfo> submeshes = null)
        {
            string cleanHero = string.IsNullOrEmpty(heroCodename) ? "custom_hero" : heroCodename.ToLowerInvariant();

            // 1. Check if any submesh has an authentic OriginalVmatPath with exact folder structure
            if (submeshes != null)
            {
                foreach (var sm in submeshes)
                {
                    if (!string.IsNullOrEmpty(sm.OriginalVmatPath))
                    {
                        string norm = sm.OriginalVmatPath.Replace('\\', '/').ToLowerInvariant();
                        int matIdx = norm.IndexOf("/materials/", StringComparison.OrdinalIgnoreCase);
                        if (matIdx > 0)
                        {
                            return norm.Substring(0, matIdx);
                        }
                    }
                }
            }

            // 2. Query DeadlockHeroCatalog
            var entry = DeadlockHeroCatalog.ResolveHero(cleanHero) ?? DeadlockHeroCatalog.GetByPath(cleanHero);
            if (entry != null && !string.IsNullOrEmpty(entry.VmdlRelativePath))
            {
                string vmdlDir = Path.GetDirectoryName(entry.VmdlRelativePath)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(vmdlDir))
                {
                    return vmdlDir;
                }
            }

            // 3. If cleanHero is already a full or relative path starting with models/
            if (cleanHero.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
            {
                return cleanHero.TrimEnd('/');
            }

            // 4. Fallback default
            return $"models/heroes_staging/{cleanHero}";
        }

        private static string ResolveSubmeshMaterialName(SubmeshNodeInfo submesh, string heroCodename)
        {
            if (!string.IsNullOrEmpty(submesh.MaterialName))
            {
                return submesh.MaterialName.ToLowerInvariant();
            }

            if (!string.IsNullOrEmpty(submesh.OriginalVmatPath))
            {
                return Path.GetFileNameWithoutExtension(submesh.OriginalVmatPath).ToLowerInvariant();
            }

            string cleanMesh = string.IsNullOrEmpty(submesh.RawName) ? "body" : submesh.RawName.ToLowerInvariant().Replace(" ", "_");
            return $"{heroCodename.ToLowerInvariant()}_{cleanMesh}";
        }

        public static string ResolveSubmeshKey(SubmeshNodeInfo sm)
        {
            if (sm == null) return string.Empty;
            if (!string.IsNullOrEmpty(sm.DisplayName)) return sm.DisplayName;
            if (!string.IsNullOrEmpty(sm.RawName)) return sm.RawName;
            if (sm.Mesh != null) return $"{sm.Mesh.Name}_{sm.SurfaceIndex}";
            return sm.SurfaceIndex.ToString();
        }

        private static Image ExtractAndBlendSubmeshImage(ModExportConfig config, SubmeshNodeInfo submesh, int targetRes)
        {
            if (config == null || submesh == null) return null;

            string subKey = ResolveSubmeshKey(submesh);
            Image atlasImage = null;
            if (config.PreBakedAtlasesBySubmesh.TryGetValue(subKey, out var subAtlas) && subAtlas != null && !subAtlas.IsEmpty())
            {
                atlasImage = subAtlas;
            }
            else if (config.PreBakedAtlasesBySurface.TryGetValue(submesh.SurfaceIndex, out var surfaceAtlas) && surfaceAtlas != null && !surfaceAtlas.IsEmpty())
            {
                atlasImage = surfaceAtlas;
            }
            else if (config.PreBakedAtlas != null && !config.PreBakedAtlas.IsEmpty())
            {
                atlasImage = config.PreBakedAtlas;
            }

            if (atlasImage == null || atlasImage.IsEmpty()) return null;

            Image isolatedImage = atlasImage;

            // Check if submesh is assigned an atlas rectangle
            Vector2 pos = Vector2.Zero;
            Vector2 size = Vector2.One;
            if (config.SubmeshAtlasRectsBySubmesh.TryGetValue(subKey, out var subRect))
            {
                pos = subRect.pos;
                size = subRect.size;
            }
            else if (config.SubmeshAtlasRects.TryGetValue(submesh.SurfaceIndex, out var atlasRect))
            {
                pos = atlasRect.pos;
                size = atlasRect.size;
            }
            else if (submesh.Mesh != null && submesh.Mesh.MaterialOverlay is ShaderMaterial sm)
            {
                var posVar = sm.GetShaderParameter("position_in_atlas");
                var sizeVar = sm.GetShaderParameter("size_in_atlas");
                if (posVar.VariantType == Variant.Type.Vector2 && sizeVar.VariantType == Variant.Type.Vector2)
                {
                    pos = posVar.AsVector2();
                    size = sizeVar.AsVector2();
                }
            }

            if (size.X > 0 && size.Y > 0 && (size.X < 0.999f || size.Y < 0.999f))
            {
                int px = Mathf.Clamp(Mathf.RoundToInt(pos.X * atlasImage.GetWidth()), 0, atlasImage.GetWidth() - 1);
                int py = Mathf.Clamp(Mathf.RoundToInt(pos.Y * atlasImage.GetHeight()), 0, atlasImage.GetHeight() - 1);
                int pw = Mathf.Clamp(Mathf.RoundToInt(size.X * atlasImage.GetWidth()), 1, atlasImage.GetWidth() - px);
                int ph = Mathf.Clamp(Mathf.RoundToInt(size.Y * atlasImage.GetHeight()), 1, atlasImage.GetHeight() - py);

                isolatedImage = atlasImage.GetRegion(new Rect2I(px, py, pw, ph));
            }

            // Retrieve pre-extracted base texture image
            Image baseImg = null;
            if (config.PreExtractedBaseImagesBySubmesh.TryGetValue(subKey, out var subBase) && subBase != null && !subBase.IsEmpty())
            {
                baseImg = subBase;
            }
            else if (config.PreExtractedBaseImages.TryGetValue(submesh.SurfaceIndex, out var preBase) && preBase != null && !preBase.IsEmpty())
            {
                baseImg = preBase;
            }

            int exportWidth = 2048;
            int exportHeight = 2048;

            if (baseImg != null && !baseImg.IsEmpty())
            {
                int baseW = baseImg.GetWidth();
                int baseH = baseImg.GetHeight();
                if (targetRes > 0)
                {
                    if (baseW >= baseH)
                    {
                        exportWidth = targetRes;
                        exportHeight = Mathf.Max(1, Mathf.RoundToInt((float)targetRes * baseH / baseW));
                    }
                    else
                    {
                        exportHeight = targetRes;
                        exportWidth = Mathf.Max(1, Mathf.RoundToInt((float)targetRes * baseW / baseH));
                    }
                }
                else
                {
                    exportWidth = baseW;
                    exportHeight = baseH;
                }
            }
            else if (isolatedImage != null && !isolatedImage.IsEmpty())
            {
                int isoW = isolatedImage.GetWidth();
                int isoH = isolatedImage.GetHeight();
                if (targetRes > 0)
                {
                    if (isoW >= isoH)
                    {
                        exportWidth = targetRes;
                        exportHeight = Mathf.Max(1, Mathf.RoundToInt((float)targetRes * isoH / isoW));
                    }
                    else
                    {
                        exportHeight = targetRes;
                        exportWidth = Mathf.Max(1, Mathf.RoundToInt((float)targetRes * isoW / isoH));
                    }
                }
                else
                {
                    exportWidth = isoW;
                    exportHeight = isoH;
                }
            }
            else
            {
                exportWidth = targetRes > 0 ? targetRes : 2048;
                exportHeight = targetRes > 0 ? targetRes : 2048;
            }

            if (isolatedImage == null || isolatedImage.IsEmpty()) return null;

            // Ensure isolated painted region matches target dimensions without blur
            var finalImage = (Image)isolatedImage.Duplicate();
            if (finalImage.IsCompressed())
            {
                finalImage.Decompress();
            }
            if (finalImage.GetWidth() != exportWidth || finalImage.GetHeight() != exportHeight)
            {
                finalImage.Resize(exportWidth, exportHeight, Image.Interpolation.Nearest);
            }
            finalImage.Convert(Image.Format.Rgba8);

            // Composite over base texture if present
            if (baseImg != null && !baseImg.IsEmpty())
            {
                var baseUnscaled = (Image)baseImg.Duplicate();
                baseUnscaled.Convert(Image.Format.Rgba8);
                if (baseUnscaled.GetWidth() != exportWidth || baseUnscaled.GetHeight() != exportHeight)
                {
                    baseUnscaled.Resize(exportWidth, exportHeight, Image.Interpolation.Nearest);
                }

                // Alpha composite: final = paint.rgb * paint.a + base.rgb * (1 - paint.a)
                byte[] paintData = finalImage.GetData();
                byte[] baseData = baseUnscaled.GetData();

                if (paintData != null && baseData != null && paintData.Length >= exportWidth * exportHeight * 4 && baseData.Length >= exportWidth * exportHeight * 4)
                {
                    int pixelCount = exportWidth * exportHeight;
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int idx = i * 4;
                        float pA = paintData[idx + 3] / 255.0f;
                        if (pA <= 0.001f)
                        {
                            // 1:1 unpainted: exact raw base texels byte-for-byte!
                            paintData[idx] = baseData[idx];
                            paintData[idx + 1] = baseData[idx + 1];
                            paintData[idx + 2] = baseData[idx + 2];
                            // Preserve Valve's original Alpha channel (cavity AO, tint, fabric spec) byte-for-byte
                            paintData[idx + 3] = baseData[idx + 3];
                        }
                        else if (pA < 0.999f)
                        {
                            float bR = baseData[idx];
                            float bG = baseData[idx + 1];
                            float bB = baseData[idx + 2];

                            float pR = paintData[idx];
                            float pG = paintData[idx + 1];
                            float pB = paintData[idx + 2];

                            paintData[idx] = (byte)Mathf.Clamp(pR * pA + bR * (1.0f - pA), 0, 255);
                            paintData[idx + 1] = (byte)Mathf.Clamp(pG * pA + bG * (1.0f - pA), 0, 255);
                            paintData[idx + 2] = (byte)Mathf.Clamp(pB * pA + bB * (1.0f - pA), 0, 255);
                            // Preserve Valve's original Alpha channel byte-for-byte
                            paintData[idx + 3] = baseData[idx + 3];
                        }
                        else
                        {
                            // Full paint opacity replaces RGB but retains authentic base alpha channel
                            paintData[idx + 3] = baseData[idx + 3];
                        }
                    }

                    finalImage = Image.CreateFromData(exportWidth, exportHeight, false, Image.Format.Rgba8, paintData);
                }
            }

            return finalImage;
        }

        public static string ResolveOriginalColorVtexCPath(SubmeshNodeInfo submesh, string heroCodename, Package baseVpkPackage)
        {
            if (!string.IsNullOrEmpty(submesh?.OriginalColorVtexCPath))
            {
                return submesh.OriginalColorVtexCPath.Replace('\\', '/').TrimStart('/');
            }

            if (baseVpkPackage != null && submesh != null)
            {
                var candidates = new List<string>();
                if (!string.IsNullOrEmpty(submesh.OriginalVmatPath)) candidates.Add(submesh.OriginalVmatPath);
                if (!string.IsNullOrEmpty(submesh.MaterialName))
                {
                    candidates.Add($"materials/{submesh.MaterialName}.vmat_c");
                    candidates.Add($"models/{submesh.MaterialName}.vmat_c");
                }

                PackageEntry foundEntry = null;
                foreach (var c in candidates)
                {
                    string norm = c.Replace('\\', '/').TrimStart('/');
                    if (!norm.EndsWith("_c")) norm += "_c";
                    foundEntry = baseVpkPackage.FindEntry(norm);
                    if (foundEntry != null) break;
                }

                if (foundEntry == null && !string.IsNullOrEmpty(submesh.MaterialName))
                {
                    string targetFile = $"{submesh.MaterialName}.vmat_c";
                    if (baseVpkPackage.Entries.TryGetValue("vmat_c", out var vmatEntries))
                    {
                        foundEntry = vmatEntries.FirstOrDefault(e => string.Equals(e.FileName + "." + e.TypeName, targetFile, StringComparison.OrdinalIgnoreCase))
                                  ?? vmatEntries.FirstOrDefault(e => e.FileName.Equals(submesh.MaterialName, StringComparison.OrdinalIgnoreCase));
                    }
                }

                if (foundEntry != null)
                {
                    try
                    {
                        baseVpkPackage.ReadEntry(foundEntry, out byte[] data);
                        using var resource = new ValveResourceFormat.Resource();
                        using var ms = new MemoryStream(data);
                        resource.Read(ms);

                        if (resource.ResourceType == ResourceType.Material && resource.DataBlock is VrfMaterial vmat)
                        {
                            string colorTex = DeadlockPlayground.Materials.Source2TextureLoader.GetTextureParam(vmat, "g_tColor")
                                           ?? DeadlockPlayground.Materials.Source2TextureLoader.GetTextureParam(vmat, "TextureColor")
                                           ?? DeadlockPlayground.Materials.Source2TextureLoader.GetTextureParam(vmat, "g_tColor1");

                            if (!string.IsNullOrEmpty(colorTex))
                            {
                                string norm = colorTex.Replace('\\', '/').Trim().TrimStart('/');
                                if (norm.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase)) norm += "_c";
                                else if (!norm.EndsWith(".vtex_c", StringComparison.OrdinalIgnoreCase)) norm += ".vtex_c";
                                submesh.OriginalColorVtexCPath = norm;
                                return norm;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        GD.Print($"[SkinExporter] Notice: Could not extract OriginalColorVtexCPath from VPK: {ex.Message}");
                    }
                }
            }

            // Default fallback
            string matName = ResolveSubmeshMaterialName(submesh, heroCodename);
            string fallbackFolder = ResolveHeroInternalFolder(heroCodename, null);
            string defaultPath = $"{fallbackFolder}/materials/{matName}_color.vtex_c".Replace('\\', '/').TrimStart('/');
            if (submesh != null) submesh.OriginalColorVtexCPath = defaultPath;
            return defaultPath;
        }

        public static string GenerateSource2Kv3Material(string relativeColorTexturePath, string materialName)
        {
            return GenerateSource2Kv3Material(relativeColorTexturePath, materialName, null, null, null, null);
        }

        public static string GenerateSource2Kv3Material(
            string relativeColorTexturePath,
            string materialName,
            SubmeshNodeInfo submesh,
            string deadlockGamePath,
            string gameStagingRoot = null,
            Package baseVpkPackage = null)
        {
            string texPath = FormatVtexCResource(relativeColorTexturePath);

            // Attempt to enrich submesh with original base textures from VPK if missing
            if (submesh != null)
            {
                TryExtractOriginalVmatTexturesFromVpk(deadlockGamePath, submesh, baseVpkPackage);
            }

            string normalVtex = !string.IsNullOrEmpty(submesh?.OriginalNormalVtex)
                ? FormatVtexCResource(submesh.OriginalNormalVtex)
                : null;

            string aoVtex = !string.IsNullOrEmpty(submesh?.OriginalAoVtex)
                ? FormatVtexCResource(submesh.OriginalAoVtex)
                : null;

            string nprOutlineVtex = !string.IsNullOrEmpty(submesh?.OriginalNprOutlineVtex)
                ? FormatVtexCResource(submesh.OriginalNprOutlineVtex)
                : null;

            string maskVtex = !string.IsNullOrEmpty(submesh?.OriginalMaskVtex)
                ? FormatVtexCResource(submesh.OriginalMaskVtex)
                : null;

            // Ensure referenced textures exist in game tree so resourcecompiler doesn't fail
            if (!string.IsNullOrEmpty(gameStagingRoot))
            {
                if (!string.IsNullOrEmpty(normalVtex)) EnsureTextureStagedInGameTree(gameStagingRoot, normalVtex, baseVpkPackage);
                if (!string.IsNullOrEmpty(aoVtex)) EnsureTextureStagedInGameTree(gameStagingRoot, aoVtex, baseVpkPackage);
                if (!string.IsNullOrEmpty(nprOutlineVtex)) EnsureTextureStagedInGameTree(gameStagingRoot, nprOutlineVtex, baseVpkPackage);
                if (!string.IsNullOrEmpty(maskVtex)) EnsureTextureStagedInGameTree(gameStagingRoot, maskVtex, baseVpkPackage);
            }

            var sb = new StringBuilder();
            sb.AppendLine("<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->");
            sb.AppendLine("{");
            sb.AppendLine("\tshader = \"pbr.vfx\"");
            sb.AppendLine("\tF_SPECULAR = 1");
            sb.AppendLine($"\tg_tColor = resource:\"{texPath}\"");

            if (!string.IsNullOrEmpty(normalVtex))
            {
                sb.AppendLine($"\tg_tNormalRoughness = resource:\"{normalVtex}\"");
            }
            if (!string.IsNullOrEmpty(aoVtex))
            {
                sb.AppendLine($"\tg_tAmbientOcclusion = resource:\"{aoVtex}\"");
            }
            if (!string.IsNullOrEmpty(nprOutlineVtex))
            {
                sb.AppendLine($"\tg_tNPROutlineMask = resource:\"{nprOutlineVtex}\"");
            }
            if (!string.IsNullOrEmpty(maskVtex))
            {
                sb.AppendLine($"\tg_tSelfIllumMask = resource:\"{maskVtex}\"");
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        public static string FormatVtexCResource(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            string clean = path.Replace('\\', '/').Trim().TrimStart('/');
            if (clean.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean.Substring(0, clean.Length - 4) + ".vtex_c";
            }
            else if (clean.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase))
            {
                clean += "_c";
            }
            else if (!clean.EndsWith(".vtex_c", StringComparison.OrdinalIgnoreCase))
            {
                clean += ".vtex_c";
            }
            return clean;
        }

        private static void EnsureTextureStagedInGameTree(string gameStagingRoot, string vtexCInternalPath, Package baseVpkPackage)
        {
            if (string.IsNullOrEmpty(gameStagingRoot) || string.IsNullOrEmpty(vtexCInternalPath)) return;
            try
            {
                string normPath = vtexCInternalPath.Replace('\\', '/').TrimStart('/');
                string fullPath = Path.Combine(gameStagingRoot, normPath);
                if (File.Exists(fullPath)) return;

                string dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (baseVpkPackage != null)
                {
                    var entry = baseVpkPackage.FindEntry(normPath);
                    if (entry != null)
                    {
                        baseVpkPackage.ReadEntry(entry, out byte[] bytes);
                        File.WriteAllBytes(fullPath, bytes);
                        return;
                    }
                }

                // Place empty placeholder so resourcecompiler validates file existence in game tree
                File.WriteAllBytes(fullPath, Array.Empty<byte>());
            }
            catch (Exception ex)
            {
                GD.Print($"[SkinExporter] Notice: Could not stage texture '{vtexCInternalPath}' into game tree: {ex.Message}");
            }
        }

        private static void TryExtractOriginalVmatTexturesFromVpk(
            string deadlockGamePath,
            SubmeshNodeInfo submesh,
            Package baseVpkPackage)
        {
            if (submesh == null) return;
            if (!string.IsNullOrEmpty(submesh.OriginalNormalVtex) && !string.IsNullOrEmpty(submesh.OriginalAoVtex))
            {
                return; // Already populated from Godot material metadata
            }

            Package pkg = baseVpkPackage;
            bool disposePkg = false;

            try
            {
                if (pkg == null && !string.IsNullOrEmpty(deadlockGamePath))
                {
                    string vpkPath = Path.Combine(deadlockGamePath, "game", "citadel", "pak01_dir.vpk");
                    if (File.Exists(vpkPath))
                    {
                        pkg = new Package();
                        pkg.Read(vpkPath);
                        disposePkg = true;
                    }
                }

                if (pkg == null) return;

                var candidates = new List<string>();
                if (!string.IsNullOrEmpty(submesh.OriginalVmatPath)) candidates.Add(submesh.OriginalVmatPath);
                if (!string.IsNullOrEmpty(submesh.MaterialName))
                {
                    candidates.Add($"materials/{submesh.MaterialName}.vmat_c");
                    candidates.Add($"models/{submesh.MaterialName}.vmat_c");
                }

                PackageEntry foundEntry = null;
                foreach (var c in candidates)
                {
                    string norm = c.Replace('\\', '/').TrimStart('/');
                    if (!norm.EndsWith("_c")) norm += "_c";
                    foundEntry = pkg.FindEntry(norm);
                    if (foundEntry != null) break;
                }

                if (foundEntry == null && !string.IsNullOrEmpty(submesh.MaterialName))
                {
                    string targetFile = $"{submesh.MaterialName}.vmat_c";
                    foreach (var extGroup in pkg.Entries.Values)
                    {
                        foreach (var entry in extGroup)
                        {
                            if (string.Equals(entry.FileName + "." + entry.TypeName, targetFile, StringComparison.OrdinalIgnoreCase))
                            {
                                foundEntry = entry;
                                break;
                            }
                        }
                        if (foundEntry != null) break;
                    }
                }

                if (foundEntry != null)
                {
                    pkg.ReadEntry(foundEntry, out byte[] data);
                    using var resource = new ValveResourceFormat.Resource();
                    using var ms = new MemoryStream(data);
                    resource.Read(ms);

                    if (resource.ResourceType == ResourceType.Material && resource.DataBlock is VrfMaterial vmat)
                    {
                        if (string.IsNullOrEmpty(submesh.OriginalShader) && !string.IsNullOrEmpty(vmat.ShaderName))
                        {
                            submesh.OriginalShader = vmat.ShaderName;
                        }

                        if (vmat.TextureParams != null)
                        {
                            foreach (var kvp in vmat.TextureParams)
                            {
                                submesh.OriginalTextureParams[kvp.Key] = kvp.Value;
                                if (string.IsNullOrEmpty(submesh.OriginalNormalVtex) &&
                                    (kvp.Key.Equals("g_tNormalRoughness", StringComparison.OrdinalIgnoreCase) ||
                                     kvp.Key.Equals("g_tNormal", StringComparison.OrdinalIgnoreCase)))
                                {
                                    submesh.OriginalNormalVtex = kvp.Value;
                                }
                                else if (string.IsNullOrEmpty(submesh.OriginalAoVtex) &&
                                         (kvp.Key.Equals("g_tAmbientOcclusion", StringComparison.OrdinalIgnoreCase) ||
                                          kvp.Key.Equals("g_tAO", StringComparison.OrdinalIgnoreCase)))
                                {
                                    submesh.OriginalAoVtex = kvp.Value;
                                }
                                else if (string.IsNullOrEmpty(submesh.OriginalNprOutlineVtex) &&
                                         kvp.Key.Equals("g_tNPROutlineMask", StringComparison.OrdinalIgnoreCase))
                                {
                                    submesh.OriginalNprOutlineVtex = kvp.Value;
                                }
                                else if (string.IsNullOrEmpty(submesh.OriginalMaskVtex) &&
                                         (kvp.Key.Equals("g_tSelfIllumMask", StringComparison.OrdinalIgnoreCase) ||
                                          kvp.Key.Equals("g_tMask", StringComparison.OrdinalIgnoreCase)))
                                {
                                    submesh.OriginalMaskVtex = kvp.Value;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                GD.Print($"[SkinExporter] Notice: Could not extract original VMAT from VPK: {ex.Message}");
            }
            finally
            {
                if (disposePkg)
                {
                    pkg?.Dispose();
                }
            }
        }

        public static string GenerateSource2DmxVtex(string relativePathToPng)
        {
            string cleanPngPath = (relativePathToPng ?? "").Replace('\\', '/').TrimStart('/');
            return 
@"<!-- dmx encoding keyvalues2_noids 1 format vtex 1 -->
""CDmeVtex""
{
	""m_inputTextureArray"" ""element_array""
	[
		""CDmeInputTexture""
		{
			""m_name"" ""string"" ""InputTexture0""
			""m_fileName"" ""string"" """ + cleanPngPath + @"""
			""m_colorSpace"" ""string"" ""srgb""
			""m_typeString"" ""string"" ""2D""
			""m_imageProcessorArray"" ""element_array""
			[
				""CDmeImageProcessor""
				{
					""m_algorithm"" ""string"" ""None""
					""m_stringArg"" ""string"" """"
					""m_vFloat4Arg"" ""vector4"" ""0 0 0 0""
				}
			]
		}
	]
	""m_outputTypeString"" ""string"" ""2D""
	""m_outputFormat"" ""string"" ""BC7""
	""m_outputClearColor"" ""vector4"" ""0 0 0 0""
	""m_nOutputMinDimension"" ""int"" ""4""
	""m_nOutputMaxDimension"" ""int"" ""0""
	""m_textureOutputChannelArray"" ""element_array""
	[
		""CDmeTextureOutputChannel""
		{
			""m_inputTextureArray"" ""string_array"" [ ""InputTexture0"" ]
			""m_srcChannels"" ""string"" ""rgba""
			""m_dstChannels"" ""string"" ""rgba""
			""m_mipAlgorithm"" ""CDmeImageProcessor""
			{
				""m_algorithm"" ""string"" ""Box""
				""m_stringArg"" ""string"" """"
				""m_vFloat4Arg"" ""vector4"" ""0 0 0 0""
			}
			""m_outputColorSpace"" ""string"" ""srgb""
		}
	]
	""m_vClamp"" ""vector3"" ""0 0 0""
	""m_bNoLod"" ""bool"" ""0""
}
";
        }

        private static int FindRootOpeningBrace(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            int headerEnd = text.IndexOf("-->", StringComparison.Ordinal);
            return headerEnd >= 0 ? text.IndexOf('{', headerEnd) : text.IndexOf('{');
        }

        private static bool HasMeaningfulVpcfOverrides(string kv3Text, EmitterOverrideData ov)
        {
            if (string.IsNullOrEmpty(kv3Text) || ov == null) return false;

            // 1. Visibility toggle
            if (!ov.Visible) return true;

            // 2. Custom Size scale (vanilla is 1.0f)
            if (Math.Abs(ov.SizeScale - 1.0f) > 0.01f) return true;

            // 3. Custom Alpha scale (vanilla is 1.0f)
            if (Math.Abs(ov.AlphaScale - 1.0f) > 0.01f) return true;

            // 4. Constant Color / Tint
            if (ov.TintColorRgba != null && ov.TintColorRgba.Length >= 3)
            {
                int r = Math.Clamp((int)MathF.Round(ov.TintColorRgba[0] * 255f), 0, 255);
                int g = Math.Clamp((int)MathF.Round(ov.TintColorRgba[1] * 255f), 0, 255);
                int b = Math.Clamp((int)MathF.Round(ov.TintColorRgba[2] * 255f), 0, 255);

                var colMatch = System.Text.RegularExpressions.Regex.Match(
                    kv3Text,
                    @"m_ConstantColor\s*=\s*\[\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)(?:\s*,\s*(\d+))?\s*\]");
                if (colMatch.Success)
                {
                    int baseR = int.Parse(colMatch.Groups[1].Value);
                    int baseG = int.Parse(colMatch.Groups[2].Value);
                    int baseB = int.Parse(colMatch.Groups[3].Value);
                    if (Math.Abs(r - baseR) > 1 || Math.Abs(g - baseG) > 1 || Math.Abs(b - baseB) > 1)
                    {
                        return true;
                    }
                }
                else
                {
                    // If the particle has no m_ConstantColor in vanilla, it defaults to pure white [255, 255, 255].
                    // If the user picked a different tint color, it's a modification!
                    if (r < 250 || g < 250 || b < 250)
                    {
                        return true;
                    }
                }
            }

            // 5. C_INIT_RandomColor
            if (ov.HasRandomColor && kv3Text.Contains("C_INIT_RandomColor"))
            {
                if (ov.ColorMinRgb != null && ov.ColorMinRgb.Length >= 3)
                {
                    int rMin = Math.Clamp((int)MathF.Round(ov.ColorMinRgb[0] * 255f), 0, 255);
                    int gMin = Math.Clamp((int)MathF.Round(ov.ColorMinRgb[1] * 255f), 0, 255);
                    int bMin = Math.Clamp((int)MathF.Round(ov.ColorMinRgb[2] * 255f), 0, 255);
                    var minMatch = System.Text.RegularExpressions.Regex.Match(
                        kv3Text,
                        @"m_ColorMin\s*=\s*\[\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\]");
                    if (minMatch.Success)
                    {
                        int baseR = int.Parse(minMatch.Groups[1].Value);
                        int baseG = int.Parse(minMatch.Groups[2].Value);
                        int baseB = int.Parse(minMatch.Groups[3].Value);
                        if (Math.Abs(rMin - baseR) > 1 || Math.Abs(gMin - baseG) > 1 || Math.Abs(bMin - baseB) > 1)
                        {
                            return true;
                        }
                    }
                }

                if (ov.ColorMaxRgb != null && ov.ColorMaxRgb.Length >= 3)
                {
                    int rMax = Math.Clamp((int)MathF.Round(ov.ColorMaxRgb[0] * 255f), 0, 255);
                    int gMax = Math.Clamp((int)MathF.Round(ov.ColorMaxRgb[1] * 255f), 0, 255);
                    int bMax = Math.Clamp((int)MathF.Round(ov.ColorMaxRgb[2] * 255f), 0, 255);
                    var maxMatch = System.Text.RegularExpressions.Regex.Match(
                        kv3Text,
                        @"m_ColorMax\s*=\s*\[\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\]");
                    if (maxMatch.Success)
                    {
                        int baseR = int.Parse(maxMatch.Groups[1].Value);
                        int baseG = int.Parse(maxMatch.Groups[2].Value);
                        int baseB = int.Parse(maxMatch.Groups[3].Value);
                        if (Math.Abs(rMax - baseR) > 1 || Math.Abs(gMax - baseG) > 1 || Math.Abs(bMax - baseB) > 1)
                        {
                            return true;
                        }
                    }
                }
            }

            // 6. C_OP_ColorInterpolate
            if (ov.HasColorFade && ov.ColorFadeRgb != null && ov.ColorFadeRgb.Length >= 3 && kv3Text.Contains("C_OP_ColorInterpolate"))
            {
                int rFade = Math.Clamp((int)MathF.Round(ov.ColorFadeRgb[0] * 255f), 0, 255);
                int gFade = Math.Clamp((int)MathF.Round(ov.ColorFadeRgb[1] * 255f), 0, 255);
                int bFade = Math.Clamp((int)MathF.Round(ov.ColorFadeRgb[2] * 255f), 0, 255);
                var fadeMatch = System.Text.RegularExpressions.Regex.Match(
                    kv3Text,
                    @"m_ColorFade\s*=\s*\[\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\]");
                if (fadeMatch.Success)
                {
                    int baseR = int.Parse(fadeMatch.Groups[1].Value);
                    int baseG = int.Parse(fadeMatch.Groups[2].Value);
                    int baseB = int.Parse(fadeMatch.Groups[3].Value);
                    if (Math.Abs(rFade - baseR) > 1 || Math.Abs(gFade - baseG) > 1 || Math.Abs(bFade - baseB) > 1)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static string ApplyOverridesToVpcfKv3Text(string kv3Text, EmitterOverrideData ov)
        {

            if (string.IsNullOrEmpty(kv3Text) || ov == null) return kv3Text;

            // 0. Sanear propiedades exclusivas de Deadlock vpcf61 que causan [FAIL] en bin_cs2/resourcecompiler.exe (ej. explode_warp, explode_elec)
            string[] incompatibleCs2Keys = new[]
            {
                @"^\s*m_NamedValue\s*=\s*""""\s*\r?\n",
                @"^\s*m_nRandomSeed\s*=\s*-?\d+\s*\r?\n",
                @"^\s*m_nNoiseInputVectorAttribute\s*=\s*\d+\s*\r?\n",
                @"^\s*m_flNoCameraFallback\s*=\s*[-0-9\.]+\s*\r?\n",
                @"^\s*m_bUseBoundsCenter\s*=\s*(?:true|false)\s*\r?\n",
                @"^\s*m_flNotchedRangeMin\s*=\s*[-0-9\.]+\s*\r?\n",
                @"^\s*m_flNotchedRangeMax\s*=\s*[-0-9\.]+\s*\r?\n",
                @"^\s*m_flNotchedOutputOutside\s*=\s*[-0-9\.]+\s*\r?\n",
                @"^\s*m_flNotchedOutputInside\s*=\s*[-0-9\.]+\s*\r?\n",
                @"^\s*m_nRoundType\s*=\s*""[^""]*""\s*\r?\n",
                @"^\s*m_nRefractBlurType\s*=\s*""[^""]*""\s*\r?\n"
            };

            foreach (var pattern in incompatibleCs2Keys)
            {
                kv3Text = System.Text.RegularExpressions.Regex.Replace(
                    kv3Text,
                    pattern,
                    "",
                    System.Text.RegularExpressions.RegexOptions.Multiline);
            }

            if (string.IsNullOrEmpty(kv3Text) || ov == null) return kv3Text;

            // 1. Asegurar que m_ForceGenerators = [  ] esté presente (como en los .vpcf_c vanilla)
            if (!kv3Text.Contains("m_ForceGenerators"))
            {
                int renderersIdx = kv3Text.IndexOf("m_Renderers", StringComparison.Ordinal);
                if (renderersIdx > 0)
                {
                    kv3Text = kv3Text.Insert(renderersIdx, "m_ForceGenerators = [  ]\n\t");
                }
            }

            // 2. Si el usuario ocultó este emisor (Visible = false), ponemos m_nMaxParticles = 0
            if (!ov.Visible)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(kv3Text, @"m_nMaxParticles\s*=\s*\d+"))
                {
                    kv3Text = System.Text.RegularExpressions.Regex.Replace(kv3Text, @"m_nMaxParticles\s*=\s*\d+", "m_nMaxParticles = 0");
                }
                else
                {
                    int braceIdx = FindRootOpeningBrace(kv3Text);
                    if (braceIdx >= 0)
                        kv3Text = kv3Text.Insert(braceIdx + 1, "\n\tm_nMaxParticles = 0");
                }
            }

           // 3a. m_ConstantColor + AlphaScale
            if (ov.TintColorRgba != null && ov.TintColorRgba.Length >= 3)
            {
                int r = Math.Clamp((int)MathF.Round(ov.TintColorRgba[0] * 255f), 0, 255);
                int g = Math.Clamp((int)MathF.Round(ov.TintColorRgba[1] * 255f), 0, 255);
                int b = Math.Clamp((int)MathF.Round(ov.TintColorRgba[2] * 255f), 0, 255);
                int a = Math.Clamp((int)MathF.Round(ov.AlphaScale * 255f), 0, 255);
                string rgbaBlock = $"[ {r}, {g}, {b}, {a} ]";

                if (System.Text.RegularExpressions.Regex.IsMatch(kv3Text, @"m_ConstantColor\s*=\s*\[[^\]]*\]"))
                {
                    kv3Text = System.Text.RegularExpressions.Regex.Replace(kv3Text, @"m_ConstantColor\s*=\s*\[[^\]]*\]", $"m_ConstantColor = {rgbaBlock}");
                }
                else
                {
                    int braceIdx = FindRootOpeningBrace(kv3Text);
                    if (braceIdx >= 0)
                        kv3Text = kv3Text.Insert(braceIdx + 1, $"\n\tm_ConstantColor = {rgbaBlock}");
                }
            }

            // 3b. C_INIT_RandomColor: m_ColorMin y m_ColorMax independientes!
            if (ov.HasRandomColor && kv3Text.Contains("C_INIT_RandomColor"))
            {
                if (ov.ColorMinRgb != null && ov.ColorMinRgb.Length >= 3)
                {
                    int rMin = Math.Clamp((int)MathF.Round(ov.ColorMinRgb[0] * 255f), 0, 255);
                    int gMin = Math.Clamp((int)MathF.Round(ov.ColorMinRgb[1] * 255f), 0, 255);
                    int bMin = Math.Clamp((int)MathF.Round(ov.ColorMinRgb[2] * 255f), 0, 255);
                    kv3Text = System.Text.RegularExpressions.Regex.Replace(kv3Text, @"m_ColorMin\s*=\s*\[[^\]]*\]", $"m_ColorMin = [ {rMin}, {gMin}, {bMin} ]");
                }
                if (ov.ColorMaxRgb != null && ov.ColorMaxRgb.Length >= 3)
                {
                    int rMax = Math.Clamp((int)MathF.Round(ov.ColorMaxRgb[0] * 255f), 0, 255);
                    int gMax = Math.Clamp((int)MathF.Round(ov.ColorMaxRgb[1] * 255f), 0, 255);
                    int bMax = Math.Clamp((int)MathF.Round(ov.ColorMaxRgb[2] * 255f), 0, 255);
                    kv3Text = System.Text.RegularExpressions.Regex.Replace(kv3Text, @"m_ColorMax\s*=\s*\[[^\]]*\]", $"m_ColorMax = [ {rMax}, {gMax}, {bMax} ]");
                }
            }

            // 3c. C_OP_ColorInterpolate: m_ColorFade independiente!
            if (ov.HasColorFade && ov.ColorFadeRgb != null && ov.ColorFadeRgb.Length >= 3 && kv3Text.Contains("C_OP_ColorInterpolate"))
            {
                int rFade = Math.Clamp((int)MathF.Round(ov.ColorFadeRgb[0] * 255f), 0, 255);
                int gFade = Math.Clamp((int)MathF.Round(ov.ColorFadeRgb[1] * 255f), 0, 255);
                int bFade = Math.Clamp((int)MathF.Round(ov.ColorFadeRgb[2] * 255f), 0, 255);
                kv3Text = System.Text.RegularExpressions.Regex.Replace(kv3Text, @"m_ColorFade\s*=\s*\[[^\]]*\]", $"m_ColorFade = [ {rFade}, {gFade}, {bFade} ]");
            }

            // 4. Si cambió SizeScale, escalamos m_flConstantRadius e inyectamos/escalamos m_flRadiusScale en el Renderer
            if (Math.Abs(ov.SizeScale - 1.0f) > 0.01f)
            {
                string scaleStr = ov.SizeScale.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);

                var match = System.Text.RegularExpressions.Regex.Match(kv3Text, @"m_flConstantRadius\s*=\s*([0-9\.]+)");
                if (match.Success && float.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float baseRad))
                {
                    float newRad = baseRad * ov.SizeScale;
                    kv3Text = kv3Text.Substring(0, match.Index) +
                              $"m_flConstantRadius = {newRad.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}" +
                              kv3Text.Substring(match.Index + match.Length);
                }
                else
                {
                    int braceIdx = FindRootOpeningBrace(kv3Text);
                    if (braceIdx >= 0)
                        kv3Text = kv3Text.Insert(braceIdx + 1, $"\n\tm_flConstantRadius = {(5.0f * ov.SizeScale).ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}");
                }

                // Inyectar m_flRadiusScale en el primer Renderer si no lo tiene, para que escale incluso cuando C_INIT_InitFloat pisa el radio
                if (!kv3Text.Contains("m_flRadiusScale"))
                {
                    kv3Text = System.Text.RegularExpressions.Regex.Replace(
                        kv3Text,
                        @"(_class\s*=\s*""C_OP_Render(?:Sprites|Trails)"")",
                        $"$1\n\t\t\tm_flRadiusScale = {scaleStr}");
                }
            }

            return kv3Text;
        }

        private static int RunCompilerProcess(
            string compilerExe,
            string workingDir,
            string arguments,
            IProgress<ExportProgressReport> progress,
            CancellationToken ct,
            float activeProgress = 0.65f)
        {
            var psi = new ProcessStartInfo
            {
                FileName = compilerExe,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            int exitCode = 0;
            using (var process = new Process { StartInfo = psi })
            {
                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        progress?.Report(new ExportProgressReport(activeProgress, $"[COMPILER] {e.Data.Trim()}"));
                    }
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        progress?.Report(new ExportProgressReport(activeProgress, $"[COMPILER ERR] {e.Data.Trim()}", ExportLogLevel.Warning));
                    }
                };

                try
                {
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    using (ct.Register(() =>
                    {
                        try { if (!process.HasExited) process.Kill(true); } catch { }
                    }))
                    {
                        process.WaitForExit();
                    }

                    exitCode = process.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    progress?.Report(new ExportProgressReport(activeProgress, "[WARNING] Compilation canceled by user.", ExportLogLevel.Warning));
                    throw;
                }
            }

            return exitCode;
        }

        private static string LocateEmittedFile(string primaryDir, string fallbackDir, string pattern, string rootGameDir = null, string rootContentDir = null)
        {
            if (Directory.Exists(primaryDir))
            {
                var files = Directory.GetFiles(primaryDir, pattern, SearchOption.TopDirectoryOnly);
                if (files.Length > 0) return files[0];
                var subFiles = Directory.GetFiles(primaryDir, pattern, SearchOption.AllDirectories);
                if (subFiles.Length > 0) return subFiles[0];
            }
            if (Directory.Exists(fallbackDir))
            {
                var files = Directory.GetFiles(fallbackDir, pattern, SearchOption.TopDirectoryOnly);
                if (files.Length > 0) return files[0];
                var subFiles = Directory.GetFiles(fallbackDir, pattern, SearchOption.AllDirectories);
                if (subFiles.Length > 0) return subFiles[0];
            }
            if (!string.IsNullOrEmpty(rootGameDir) && Directory.Exists(rootGameDir))
            {
                var files = Directory.GetFiles(rootGameDir, pattern, SearchOption.AllDirectories);
                if (files.Length > 0) return files[0];
            }
            if (!string.IsNullOrEmpty(rootContentDir) && Directory.Exists(rootContentDir))
            {
                var files = Directory.GetFiles(rootContentDir, pattern, SearchOption.AllDirectories);
                if (files.Length > 0) return files[0];
            }
            return null;
        }

        // =========================================================================
        // Legacy Compatibility Methods
        // =========================================================================
        public SkinExportResult ExportFlattenedPng(string filePath)
        {
            var result = new SkinExportResult();
            if (_layerManager == null)
            {
                result.Success = false;
                result.ErrorMessage = "SkinLayerManager reference is null.";
                return result;
            }

            var image = _layerManager.BakeCompositeImage();
            if (image == null)
            {
                result.Success = false;
                result.ErrorMessage = "Failed to capture composite image from viewport.";
                return result;
            }

            try
            {
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                Error err = image.SavePng(filePath);
                if (err != Error.Ok)
                {
                    result.Success = false;
                    result.ErrorMessage = $"Godot SavePng failed with error code: {err}";
                    return result;
                }

                result.Success = true;
                result.PngPath = filePath;
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        public SkinExportResult ExportVmat(string vmatPath, string relativeTexturePath)
        {
            var result = new SkinExportResult();
            try
            {
                string dir = Path.GetDirectoryName(vmatPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string vmatContent = GenerateSource2Kv3Material(relativeTexturePath, Path.GetFileNameWithoutExtension(vmatPath));
                File.WriteAllText(vmatPath, vmatContent);

                result.Success = true;
                result.VmatPath = vmatPath;
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        public SkinExportResult InstallModToCitadel(string deadlockGamePath, string heroCodename, string submeshName)
        {
            var config = new ModExportConfig
            {
                DeadlockGamePath = deadlockGamePath,
                HeroCodename = heroCodename,
                PreBakedAtlas = _layerManager?.BakeCompositeImage(),
                InstallDirectlyToGame = true
            };
            return ExportModAsync(config, null, CancellationToken.None).GetAwaiter().GetResult();
        }
    }
}
