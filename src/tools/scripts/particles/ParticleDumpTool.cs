using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SteamDatabase.ValvePak;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using VrfResource = ValveResourceFormat.Resource;

namespace DeadlockPlayground.Particles;

/// <summary>
/// Summary results returned by a particle dump execution.
/// </summary>
public class ParticleDumpResult
{
    public string RootVpcfPath { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public int VpcfCount => DumpedVpcfs.Count;
    public int MaterialCount => DumpedMaterials.Count;
    public int TextureCount => DumpedTextures.Count;
    public int ModelCount => DumpedModels.Count;
    public List<string> DumpedVpcfs { get; } = new();
    public List<string> DumpedMaterials { get; } = new();
    public List<string> DumpedTextures { get; } = new();
    public List<string> DumpedModels { get; } = new();
    public List<string> MissingAssets { get; } = new();
    public List<string> HierarchyTreeLines { get; } = new();
}

/// <summary>
/// Instrumentation tool for Phase 0 of the Particle Pipeline Overhaul.
/// Recursively inspects and dumps Source 2 particle system hierarchies (.vpcf_c),
/// along with referenced materials (.vmat_c), textures (.vtex_c), sprite sheet sequences,
/// and models (.vmdl_c) from mounted VPKs to disk using VRF DataBlock.ToString().
/// </summary>
public static class ParticleDumpTool
{
    public static Action<string> LogCallback { get; set; } = Console.WriteLine;
    public static Action<string> LogErrCallback { get; set; } = Console.Error.WriteLine;

    private static readonly string[] DefaultVpkSearchPaths =
    {
        @"E:\SteamLibrary\steamapps\common\Deadlock\game\citadel\pak01_dir.vpk",
        @"C:\Program Files (x86)\Steam\steamapps\common\Deadlock\game\citadel\pak01_dir.vpk",
        @"D:\SteamLibrary\steamapps\common\Deadlock\game\citadel\pak01_dir.vpk"
    };

    /// <summary>
    /// Resolves the absolute path to the target dump directory, defaulting to Godot user://particle_dump.
    /// </summary>
    public static string ResolveDefaultDumpDirectory()
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                return Path.Combine(appData, "Godot", "app_userdata", "Deadlock_Playground", "particle_dump");
            }
        }
        catch { }

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "particle_dump");
    }

    /// <summary>
    /// Dumps a particle hierarchy from a VPK file directly.
    /// </summary>
    public static ParticleDumpResult DumpFromVpk(string rootVpcfPath, string vpkPath = null, string targetOutputDir = null)
    {
        string resolvedVpkPath = ResolveVpkPath(vpkPath);
        if (string.IsNullOrEmpty(resolvedVpkPath) || !File.Exists(resolvedVpkPath))
        {
            throw new FileNotFoundException($"Cannot locate Deadlock VPK archive at: {vpkPath ?? "(default search paths)"}");
        }

        Log($"[ParticleDumpTool] Opening VPK: {resolvedVpkPath}");
        var package = new Package();
        package.Read(resolvedVpkPath);

        IFileLoader fileLoader = new GameFileLoader(package, string.Empty);
        return DumpHierarchy(rootVpcfPath, fileLoader, targetOutputDir);
    }

    /// <summary>
    /// Recursively dumps a particle hierarchy and all referenced Source 2 assets to disk.
    /// </summary>
    public static ParticleDumpResult DumpHierarchy(string rootVpcfPath, IFileLoader fileLoader, string targetOutputDir = null)
    {
        if (string.IsNullOrEmpty(rootVpcfPath))
            throw new ArgumentException("rootVpcfPath must not be null or empty", nameof(rootVpcfPath));
        if (fileLoader == null)
            throw new ArgumentNullException(nameof(fileLoader));

        string outDir = string.IsNullOrWhiteSpace(targetOutputDir) ? ResolveDefaultDumpDirectory() : targetOutputDir;
        Directory.CreateDirectory(outDir);

        Log($"[ParticleDumpTool] Starting hierarchy dump for: '{rootVpcfPath}' -> '{outDir}'");

        var result = new ParticleDumpResult
        {
            RootVpcfPath = rootVpcfPath,
            OutputDirectory = outDir
        };

        var visitedVpcfs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Recursive VPCF traversal
        TraverseAndDumpVpcf(
            rootVpcfPath,
            fileLoader,
            outDir,
            visitedVpcfs,
            referencedMaterials,
            referencedTextures,
            referencedModels,
            result,
            depth: 0,
            maxDepth: 6
        );

        // 2. Dump all referenced materials
        foreach (var matPath in referencedMaterials)
        {
            DumpMaterial(matPath, fileLoader, outDir, referencedTextures, result);
        }

        // 3. Dump all referenced textures (including sprite sheets)
        foreach (var texPath in referencedTextures)
        {
            DumpTexture(texPath, fileLoader, outDir, result);
        }

        // 4. Dump all referenced models
        foreach (var mdlPath in referencedModels)
        {
            DumpModel(mdlPath, fileLoader, outDir, result);
        }

        // 5. Generate human-readable manifest & JSON summary
        WriteManifest(result, outDir);

        Log($"[ParticleDumpTool] Dump completed successfully! Summary: " +
            $"{result.VpcfCount} VPCF, {result.MaterialCount} VMAT, {result.TextureCount} VTEX, {result.ModelCount} VMDL. " +
            $"Missing: {result.MissingAssets.Count}");

        return result;
    }

    private static void TraverseAndDumpVpcf(
        string vpcfPath,
        IFileLoader fileLoader,
        string outDir,
        HashSet<string> visitedVpcfs,
        HashSet<string> referencedMaterials,
        HashSet<string> referencedTextures,
        HashSet<string> referencedModels,
        ParticleDumpResult result,
        int depth,
        int maxDepth)
    {
        string normVpcf = NormalizePath(vpcfPath);
        if (string.IsNullOrEmpty(normVpcf)) return;
        if (depth > maxDepth) return;
        if (!visitedVpcfs.Add(normVpcf)) return;

        string indent = new string(' ', depth * 2);
        result.HierarchyTreeLines.Add($"{indent}- {normVpcf}");

        using var res = LoadResource(fileLoader, normVpcf);
        if (res == null)
        {
            result.MissingAssets.Add($"[VPCF] {normVpcf}");
            LogErr($"[ParticleDumpTool] Could not load VPCF: {normVpcf}");
            return;
        }

        result.DumpedVpcfs.Add(normVpcf);

        // Dump raw KV3 decompilation
        string safeFileName = MakeSafeFileName(normVpcf);
        string vpcfDumpPath = Path.Combine(outDir, $"{safeFileName}.txt");

        try
        {
            string kv3Text = res.DataBlock?.ToString() ?? "// Empty DataBlock";
            File.WriteAllText(vpcfDumpPath, kv3Text, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LogErr($"[ParticleDumpTool] Failed writing {vpcfDumpPath}: {ex.Message}");
        }

        // Scan KV3 data for child systems, materials, textures, and models
        KVObject data = null;
        if (res.DataBlock is ParticleSystem ps)
        {
            data = ps.Data;
        }
        else if (res.DataBlock is BinaryKV3 bkv)
        {
            data = bkv.Data;
        }

        if (data == null) return;

        var childrenToRecurse = new List<string>();

        // Check m_Children explicitly
        if (data.TryGetValue("m_Children", out var childrenObj) && childrenObj != null)
        {
            foreach (var childItem in childrenObj.Children)
            {
                var cVal = childItem.Value;
                if (cVal != null && cVal.TryGetValue("m_ChildRef", out var refObj) && refObj != null)
                {
                    string childRef = refObj.ToString();
                    if (!string.IsNullOrEmpty(childRef))
                    {
                        childrenToRecurse.Add(childRef);
                    }
                }
            }
        }

        // Deep-scan KV3 tree for asset references
        ScanKvForAssetReferences(data, referencedMaterials, referencedTextures, referencedModels, childrenToRecurse);

        // Recurse into children
        foreach (var childPath in childrenToRecurse)
        {
            TraverseAndDumpVpcf(
                childPath,
                fileLoader,
                outDir,
                visitedVpcfs,
                referencedMaterials,
                referencedTextures,
                referencedModels,
                result,
                depth + 1,
                maxDepth
            );
        }
    }

    private static void ScanKvForAssetReferences(
        KVObject obj,
        HashSet<string> referencedMaterials,
        HashSet<string> referencedTextures,
        HashSet<string> referencedModels,
        List<string> childVpcfs)
    {
        if (obj == null || obj.IsNull) return;

        foreach (var child in obj.Children)
        {
            if (child.Value.Count > 0)
            {
                ScanKvForAssetReferences(child.Value, referencedMaterials, referencedTextures, referencedModels, childVpcfs);
            }
            else
            {
                string s = child.Value.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(s)) continue;

                if (s.EndsWith(".vmat", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".vmat_c", StringComparison.OrdinalIgnoreCase))
                {
                    referencedMaterials.Add(NormalizePath(s));
                }
                else if (s.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".vtex_c", StringComparison.OrdinalIgnoreCase))
                {
                    referencedTextures.Add(NormalizePath(s));
                }
                else if (s.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase))
                {
                    referencedModels.Add(NormalizePath(s));
                }
                else if (s.EndsWith(".vpcf", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".vpcf_c", StringComparison.OrdinalIgnoreCase))
                {
                    string norm = NormalizePath(s);
                    if (!childVpcfs.Contains(norm, StringComparer.OrdinalIgnoreCase))
                    {
                        childVpcfs.Add(norm);
                    }
                }
            }
        }
    }

    private static void DumpMaterial(
        string matPath,
        IFileLoader fileLoader,
        string outDir,
        HashSet<string> referencedTextures,
        ParticleDumpResult result)
    {
        string normMat = NormalizePath(matPath);
        using var res = LoadResource(fileLoader, normMat);
        if (res == null)
        {
            result.MissingAssets.Add($"[VMAT] {normMat}");
            LogErr($"[ParticleDumpTool] Could not load material: {normMat}");
            return;
        }

        result.DumpedMaterials.Add(normMat);
        string safeFileName = MakeSafeFileName(normMat);
        string matDumpPath = Path.Combine(outDir, $"{safeFileName}.txt");

        var sb = new StringBuilder();
        sb.AppendLine($"// Material Dump: {normMat}");

        if (res.DataBlock is ValveResourceFormat.ResourceTypes.Material mat)
        {
            sb.AppendLine($"Shader: {mat.ShaderName}");
            sb.AppendLine();

            sb.AppendLine("=== Int Params ===");
            foreach (var kv in mat.IntParams.OrderBy(k => k.Key))
            {
                sb.AppendLine($"  {kv.Key} = {kv.Value}");
            }
            sb.AppendLine();

            sb.AppendLine("=== Float Params ===");
            foreach (var kv in mat.FloatParams.OrderBy(k => k.Key))
            {
                sb.AppendLine($"  {kv.Key} = {kv.Value:F6}");
            }
            sb.AppendLine();

            sb.AppendLine("=== Vector Params ===");
            foreach (var kv in mat.VectorParams.OrderBy(k => k.Key))
            {
                sb.AppendLine($"  {kv.Key} = [{kv.Value.X}, {kv.Value.Y}, {kv.Value.Z}, {kv.Value.W}]");
            }
            sb.AppendLine();

            sb.AppendLine("=== Texture Params ===");
            foreach (var kv in mat.TextureParams.OrderBy(k => k.Key))
            {
                sb.AppendLine($"  {kv.Key} = {kv.Value}");
                referencedTextures.Add(NormalizePath(kv.Value));
            }
            sb.AppendLine();

            sb.AppendLine("=== Raw KV3 Block ===");
            try
            {
                sb.AppendLine(res.DataBlock.ToString());
            }
            catch (Exception ex)
            {
                sb.AppendLine($"// Error calling ToString(): {ex.Message}");
            }
        }
        else
        {
            sb.AppendLine("// DataBlock is not Material: " + res.DataBlock?.GetType().FullName);
            sb.AppendLine(res.DataBlock?.ToString() ?? "");
        }

        try
        {
            File.WriteAllText(matDumpPath, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LogErr($"[ParticleDumpTool] Failed writing material {matDumpPath}: {ex.Message}");
        }
    }

    private static void DumpTexture(string texPath, IFileLoader fileLoader, string outDir, ParticleDumpResult result)
    {
        string normTex = NormalizePath(texPath);
        using var res = LoadResource(fileLoader, normTex);
        if (res == null)
        {
            result.MissingAssets.Add($"[VTEX] {normTex}");
            LogErr($"[ParticleDumpTool] Could not load texture: {normTex}");
            return;
        }

        result.DumpedTextures.Add(normTex);
        string safeFileName = MakeSafeFileName(normTex);
        string texDumpPath = Path.Combine(outDir, $"{safeFileName}.txt");

        var sb = new StringBuilder();
        sb.AppendLine($"// Texture Dump: {normTex}");

        if (res.DataBlock is ValveResourceFormat.ResourceTypes.Texture tex)
        {
            sb.AppendLine($"Dimensions: {tex.Width} x {tex.Height} x {tex.Depth}");
            sb.AppendLine($"Format: {tex.Format}");
            sb.AppendLine($"Flags: {tex.Flags}");
            sb.AppendLine($"NumMipLevels: {tex.NumMipLevels}");
            sb.AppendLine();

            var sheet = tex.GetSpriteSheetData();
            if (sheet != null && sheet.Sequences != null && sheet.Sequences.Length > 0)
            {
                sb.AppendLine($"=== SpriteSheet Metadata ({sheet.Sequences.Length} Sequences) ===");
                for (int sIdx = 0; sIdx < sheet.Sequences.Length; sIdx++)
                {
                    var seq = sheet.Sequences[sIdx];
                    sb.AppendLine($"  Sequence [{sIdx}]: Name='{seq.Name}', Clamp={seq.Clamp}, Frames={seq.Frames?.Length ?? 0}");
                    if (seq.Frames != null)
                    {
                        for (int fIdx = 0; fIdx < seq.Frames.Length; fIdx++)
                        {
                            var frame = seq.Frames[fIdx];
                            sb.AppendLine($"    Frame [{fIdx}]: DisplayTime={frame.DisplayTime:F4}, Images={frame.Images?.Length ?? 0}");
                            if (frame.Images != null)
                            {
                                for (int iIdx = 0; iIdx < frame.Images.Length; iIdx++)
                                {
                                    var img = frame.Images[iIdx];
                                    sb.AppendLine($"      Image [{iIdx}]: UncroppedMin=({img.UncroppedMin.X:F4}, {img.UncroppedMin.Y:F4}), UncroppedMax=({img.UncroppedMax.X:F4}, {img.UncroppedMax.Y:F4})");
                                    sb.AppendLine($"                   CroppedMin=({img.CroppedMin.X:F4}, {img.CroppedMin.Y:F4}), CroppedMax=({img.CroppedMax.X:F4}, {img.CroppedMax.Y:F4})");
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                sb.AppendLine("=== SpriteSheet Metadata: None ===");
            }
        }
        else
        {
            sb.AppendLine("// DataBlock is not Texture: " + res.DataBlock?.GetType().FullName);
        }

        try
        {
            File.WriteAllText(texDumpPath, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LogErr($"[ParticleDumpTool] Failed writing texture {texDumpPath}: {ex.Message}");
        }
    }

    private static void DumpModel(string mdlPath, IFileLoader fileLoader, string outDir, ParticleDumpResult result)
    {
        string normMdl = NormalizePath(mdlPath);
        using var res = LoadResource(fileLoader, normMdl);
        if (res == null)
        {
            result.MissingAssets.Add($"[VMDL] {normMdl}");
            LogErr($"[ParticleDumpTool] Could not load model: {normMdl}");
            return;
        }

        result.DumpedModels.Add(normMdl);
        string safeFileName = MakeSafeFileName(normMdl);
        string mdlDumpPath = Path.Combine(outDir, $"{safeFileName}.txt");

        var sb = new StringBuilder();
        sb.AppendLine($"// Model Dump: {normMdl}");

        if (res.DataBlock is ValveResourceFormat.ResourceTypes.Model mdl)
        {
            sb.AppendLine($"Model Name: {mdl.Name}");
            sb.AppendLine();

            sb.AppendLine("=== Embedded Meshes ===");
            try
            {
                foreach (var (mesh, meshIndex, meshName) in mdl.GetEmbeddedMeshes())
                {
                    sb.AppendLine($"  Mesh [{meshIndex}]: {meshName}");
                    var sceneObjects = mesh.Data.GetArray("m_sceneObjects");
                    if (sceneObjects != null)
                    {
                        foreach (var sceneObj in sceneObjects)
                        {
                            var drawCalls = sceneObj.GetArray("m_drawCalls");
                            if (drawCalls != null)
                            {
                                for (int d = 0; d < drawCalls.Count; d++)
                                {
                                    string mPath = drawCalls[d].GetStringProperty("m_material");
                                    sb.AppendLine($"    Surface [{d}] Material: {mPath}");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  Error getting embedded meshes: {ex.Message}");
            }
            sb.AppendLine();

            sb.AppendLine("=== Raw KV3 Block ===");
            try
            {
                sb.AppendLine(res.DataBlock.ToString());
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  Error in ToString(): {ex.Message}");
            }
        }
        else
        {
            sb.AppendLine("// DataBlock is not Model: " + res.DataBlock?.GetType().FullName);
            sb.AppendLine(res.DataBlock?.ToString() ?? "");
        }

        try
        {
            File.WriteAllText(mdlDumpPath, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LogErr($"[ParticleDumpTool] Failed writing model {mdlDumpPath}: {ex.Message}");
        }
    }

    private static void WriteManifest(ParticleDumpResult result, string outDir)
    {
        string manifestTextPath = Path.Combine(outDir, "dump_manifest.txt");
        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine("                  SOURCE 2 PARTICLE PIPELINE DUMP MANIFEST                     ");
        sb.AppendLine("================================================================================");
        sb.AppendLine($"Date: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Root System: {result.RootVpcfPath}");
        sb.AppendLine($"Output Directory: {outDir}");
        sb.AppendLine();

        sb.AppendLine($"--- System Hierarchy ({result.VpcfCount} systems) ---");
        foreach (var line in result.HierarchyTreeLines)
        {
            sb.AppendLine(line);
        }
        sb.AppendLine();

        sb.AppendLine($"--- Referenced Materials ({result.MaterialCount} items) ---");
        foreach (var m in result.DumpedMaterials)
        {
            sb.AppendLine($"  {m}");
        }
        sb.AppendLine();

        sb.AppendLine($"--- Referenced Textures ({result.TextureCount} items) ---");
        foreach (var t in result.DumpedTextures)
        {
            sb.AppendLine($"  {t}");
        }
        sb.AppendLine();

        sb.AppendLine($"--- Referenced Models ({result.ModelCount} items) ---");
        foreach (var mdl in result.DumpedModels)
        {
            sb.AppendLine($"  {mdl}");
        }
        sb.AppendLine();

        if (result.MissingAssets.Count > 0)
        {
            sb.AppendLine($"--- Missing Assets ({result.MissingAssets.Count} items) ---");
            foreach (var missing in result.MissingAssets)
            {
                sb.AppendLine($"  MISSING: {missing}");
            }
            sb.AppendLine();
        }

        try
        {
            File.WriteAllText(manifestTextPath, sb.ToString(), Encoding.UTF8);

            // Also output JSON manifest
            string manifestJsonPath = Path.Combine(outDir, "dump_manifest.json");
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(result, jsonOptions);
            File.WriteAllText(manifestJsonPath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LogErr($"[ParticleDumpTool] Failed writing manifest: {ex.Message}");
        }
    }

    private static VrfResource LoadResource(IFileLoader fileLoader, string resourcePath)
    {
        string search = resourcePath.Replace('\\', '/');
        if (!search.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
        {
            search += "_c";
        }

        try
        {
            return fileLoader.LoadFile(search) ?? fileLoader.LoadFileCompiled(search);
        }
        catch (Exception ex)
        {
            LogErr($"[ParticleDumpTool] Error loading '{search}': {ex.Message}");
            return null;
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        string clean = path.Trim().Replace('\\', '/');
        if (clean.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean.Substring("resource:".Length).Trim();
        }
        if (clean.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean.Substring(0, clean.Length - 2);
        }
        return clean;
    }

    private static string MakeSafeFileName(string path)
    {
        string clean = NormalizePath(path);
        // Replace directory separators with underscores
        return clean.Replace('/', '_').Replace('\\', '_');
    }

    private static string ResolveVpkPath(string userSuppliedPath)
    {
        if (!string.IsNullOrEmpty(userSuppliedPath) && File.Exists(userSuppliedPath))
        {
            return userSuppliedPath;
        }

        foreach (var candidate in DefaultVpkSearchPaths)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static void Log(string msg)
    {
        try { LogCallback?.Invoke(msg); } catch { Console.WriteLine(msg); }
    }

    private static void LogErr(string msg)
    {
        try { LogErrCallback?.Invoke(msg); } catch { Console.Error.WriteLine(msg); }
    }
}
