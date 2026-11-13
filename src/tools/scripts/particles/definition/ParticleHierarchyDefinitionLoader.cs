using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using ValveKeyValue;
using ValvePak;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.IO;
using ValveResourceFormat.Particles;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using VrfResource = ValveResourceFormat.Resource;

namespace DeadlockPlayground.Particles;

/// <summary>
/// Composite IFileLoader wrapping an optional primary VRF IFileLoader and falling back
/// to active addon, base, and core VPK packages for ParticleSystemSimulation child resolution.
/// </summary>
internal sealed class CompositeParticleFileLoader : IFileLoader
{
    private readonly IFileLoader _primaryLoader;
    private readonly Package _basePackage;
    private readonly Package _addonPackage;
    private readonly Package _corePackage;

    public CompositeParticleFileLoader(IFileLoader primaryLoader, Package basePackage = null, Package addonPackage = null)
    {
        _primaryLoader = primaryLoader;
        _basePackage = basePackage;
        _addonPackage = addonPackage;
        _corePackage = DeadlockPlayground.Materials.Source2TextureLoader.CorePackage;
    }

    public GameInfo GameInfo => _primaryLoader?.GameInfo ?? GameInfo.Empty;

    public VrfResource LoadFile(string file)
    {
        if (string.IsNullOrEmpty(file)) return null;

        if (_primaryLoader != null)
        {
            var res = _primaryLoader.LoadFile(file);
            if (res != null)
            {
                if (string.IsNullOrEmpty(res.FileName)) res.FileName = file;
                return res;
            }
        }

        string normalized = NormalizePath(file);
        return TryReadResourceFromPackages(normalized)
            ?? (!normalized.EndsWith("_c", StringComparison.OrdinalIgnoreCase)
                ? TryReadResourceFromPackages(normalized + "_c")
                : null);
    }

    public VrfResource LoadFileCompiled(string file)
    {
        if (string.IsNullOrEmpty(file)) return null;

        if (_primaryLoader != null)
        {
            var res = _primaryLoader.LoadFileCompiled(file);
            if (res != null)
            {
                if (string.IsNullOrEmpty(res.FileName)) res.FileName = EnsureCompiledPath(NormalizePath(file));
                return res;
            }
        }

        string compiledPath = EnsureCompiledPath(NormalizePath(file));
        return TryReadResourceFromPackages(compiledPath);
    }

    public ShaderCollection LoadShader(string shaderName)
    {
        return _primaryLoader?.LoadShader(shaderName);
    }

    public Stream GetFileStream(string file)
    {
        if (string.IsNullOrEmpty(file)) return null;

        var stream = _primaryLoader?.GetFileStream(file);
        if (stream != null) return stream;

        string normalized = NormalizePath(file);
        byte[] bytes = TryReadBytesFromPackages(normalized);
        return bytes != null ? new MemoryStream(bytes) : null;
    }

    private static string NormalizePath(string path)
    {
        string norm = path.Replace('\\', '/').Trim();
        while (norm.StartsWith("./", StringComparison.Ordinal)) norm = norm.Substring(2);
        return norm.TrimStart('/');
    }

    private static string EnsureCompiledPath(string normalized)
    {
        return normalized.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? normalized : normalized + "_c";
    }

    private VrfResource TryReadResourceFromPackages(string path)
    {
        byte[] bytes = TryReadBytesFromPackages(path);
        if (bytes == null) return null;

        var res = new VrfResource
        {
            FileName = path
        };
        res.Read(new MemoryStream(bytes));
        return res;
    }

    private byte[] TryReadBytesFromPackages(string path)
    {
        if (_addonPackage != null)
        {
            var entry = _addonPackage.FindEntry(path);
            if (entry != null)
            {
                _addonPackage.ReadEntry(entry, out var bytes);
                return bytes;
            }
        }

        if (_basePackage != null)
        {
            lock (_basePackage)
            {
                var entry = _basePackage.FindEntry(path);
                if (entry != null)
                {
                    _basePackage.ReadEntry(entry, out var bytes);
                    return bytes;
                }
            }
        }

        if (_corePackage != null)
        {
            lock (_corePackage)
            {
                var entry = _corePackage.FindEntry(path);
                if (entry != null)
                {
                    _corePackage.ReadEntry(entry, out var bytes);
                    return bytes;
                }
            }
        }

        return null;
    }
}

public static class ParticleHierarchyDefinitionLoader
{
    public static Action<string> LogCallback { get; set; } = GD.Print;

    public static ParticleSystemSimulation CreateVrfSimulation(
        string vpcfPath,
        IFileLoader fileLoader,
        Package basePackage,
        Package addonPackage,
        out ParticleSystemDefinition rootDefinition,
        out IFileLoader compositeLoader)
    {
        rootDefinition = null;
        compositeLoader = new CompositeParticleFileLoader(fileLoader, basePackage, addonPackage);
        if (string.IsNullOrEmpty(vpcfPath)) return null;

        try
        {
            string normalized = vpcfPath.Replace('\\', '/').TrimStart('/');
            using var res = compositeLoader.LoadFileCompiled(normalized);
            if (res?.DataBlock is not ParticleSystem particleSystem)
            {
                return null;
            }

            var rootSim = new ParticleSystemSimulation(particleSystem, compositeLoader);
            rootDefinition = VpcfDefinitionParser.ParseFromUpgradedData(normalized, rootSim.Definition);
            return rootSim;
        }
        catch (Exception ex)
        {
            LogCallback?.Invoke($"[ParticleHierarchyDefinitionLoader] Failed to create VRF ParticleSystemSimulation for {vpcfPath}: {ex.Message}");
            return null;
        }
    }

    public static List<(ParticleSystemSimulation Sim, ParticleSystemDefinition Def, float Delay, bool IsEndCap)> FlattenVrfSimulation(
        ParticleSystemSimulation rootSim,
        string rootVpcfPath)
    {
        var list = new List<(ParticleSystemSimulation Sim, ParticleSystemDefinition Def, float Delay, bool IsEndCap)>();
        if (rootSim == null) return list;

        void Traverse(ParticleSystemSimulation current, string vpcfPath, float accumulatedDelay, bool isEndCap)
        {
            string rawPath = !string.IsNullOrEmpty(current.Name) && current.Name != "<unnamed>" ? current.Name : vpcfPath;
            string resolvedPath = StripCompiledSuffix(rawPath);
            var def = VpcfDefinitionParser.ParseFromUpgradedData(resolvedPath, current.Definition);
            list.Add((current, def, accumulatedDelay, isEndCap));

            var childrenArr = current.Definition?.GetArray("m_Children");
            int metaCursor = 0;

            foreach (var childSim in current.Children)
            {
                float childDelay = 0.0f;
                bool childEndCap = false;
                string childRefPath = childSim.Name ?? string.Empty;

                if (childrenArr != null)
                {
                    string childBase = StripCompiledSuffix(childSim.Name);
                    for (int i = metaCursor; i < childrenArr.Count; i++)
                    {
                        var childKv = childrenArr[i];
                        if (childKv == null) continue;
                        if (childKv.GetBooleanProperty("m_bDisableChild")) continue;

                        string refPath = childKv.GetStringProperty("m_ChildRef") ?? string.Empty;
                        if (string.Equals(StripCompiledSuffix(refPath), childBase, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(childBase))
                        {
                            childDelay = childKv.GetFloatProperty("m_flDelay");
                            childEndCap = childKv.GetBooleanProperty("m_bEndCap");
                            if (!string.IsNullOrEmpty(refPath)) childRefPath = refPath;
                            metaCursor = i + 1;
                            break;
                        }
                    }
                }

                Traverse(childSim, childRefPath, accumulatedDelay + childDelay, isEndCap || childEndCap);
            }
        }

        Traverse(rootSim, rootVpcfPath, 0.0f, false);
        return list;
    }

    private static string StripCompiledSuffix(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        string norm = path.Replace('\\', '/').Trim().TrimStart('/');
        return norm.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? norm.Substring(0, norm.Length - 2) : norm;
    }

    public static float[] ExtractSpriteSheetDurations(
        string vtexPath,
        IFileLoader fileLoader,
        Package basePackage = null,
        Package addonPackage = null)
    {
        if (string.IsNullOrEmpty(vtexPath)) return null;

        try
        {
            var loader = fileLoader as CompositeParticleFileLoader ?? new CompositeParticleFileLoader(fileLoader, basePackage, addonPackage);
            using var res = loader.LoadFileCompiled(vtexPath);
            if (res?.DataBlock is not ValveResourceFormat.ResourceTypes.Texture vTex) return null;

            var sheet = vTex.GetSpriteSheetData();
            if (sheet == null || sheet.SequenceIdCount <= 0) return null;

            var durations = new float[sheet.SequenceIdCount];
            for (int id = 0; id < durations.Length; id++)
            {
                int index = sheet.GetSequenceIndex(id);
                if (index >= 0 && index < sheet.Sequences.Length)
                {
                    durations[id] = sheet.Sequences[index].TotalTime;
                }
            }
            return durations;
        }
        catch
        {
            return null;
        }
    }

    public static void ApplyRuntimeControlPointDefaults(ParticleSystemSimulation rootSim, ControlPointRegistry registry)
    {
        if (rootSim == null) return;

        var configuredCps = new HashSet<int>();

        // 1. Apply runtime/preview m_controlPointConfigurations drivers (matching ParticleSceneNode pattern)
        var configs = rootSim.Definition?.GetArray("m_controlPointConfigurations");
        if (configs != null && configs.Count > 0)
        {
            KVObject chosenConfig = configs[0];
            foreach (var cfg in configs)
            {
                if (string.Equals(cfg.GetStringProperty("m_name"), "preview", StringComparison.OrdinalIgnoreCase))
                {
                    chosenConfig = cfg;
                    break;
                }
            }

            var drivers = chosenConfig?.GetArray("m_drivers");
            if (drivers != null)
            {
                foreach (var driver in drivers)
                {
                    int cpIdx = driver.GetInt32Property("m_iControlPoint");
                    var offsetArr = driver.GetFloatArray("m_vecOffset");
                    var offsetSrc2 = offsetArr is { Length: >= 3 }
                        ? new System.Numerics.Vector3(offsetArr[0], offsetArr[1], offsetArr[2])
                        : System.Numerics.Vector3.Zero;

                    var angArr = driver.GetFloatArray("m_angOffset");
                    var angSrc2 = angArr is { Length: >= 3 }
                        ? new System.Numerics.Vector3(angArr[0], angArr[1], angArr[2])
                        : System.Numerics.Vector3.Zero;

                    if (cpIdx == 0)
                    {
                        // CP0 placement comes from the active stage/hero socket; record orientation offset if any
                        if (angSrc2 != System.Numerics.Vector3.Zero)
                        {
                            var q = Source2AnglesDegreesToQuaternion(angSrc2);
                            rootSim.GetControlPoint(0).SetOrientation(
                                System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitX, q),
                                System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY, q),
                                System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitZ, q));
                        }
                        continue;
                    }

                    configuredCps.Add(cpIdx);
                    var cp = rootSim.GetControlPoint(cpIdx);
                    cp.Position = offsetSrc2;

                    if (angSrc2 != System.Numerics.Vector3.Zero)
                    {
                        var q = Source2AnglesDegreesToQuaternion(angSrc2);
                        cp.SetOrientation(
                            System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitX, q),
                            System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY, q),
                            System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitZ, q));
                    }

                    if (registry != null && (offsetSrc2 != System.Numerics.Vector3.Zero || angSrc2 != System.Numerics.Vector3.Zero))
                    {
                        registry.SetPosition(cpIdx, ParticleCoordUtils.Source2ToGodotPosition(offsetSrc2));
                        if (angSrc2 != System.Numerics.Vector3.Zero)
                        {
                            registry.SetOrientation(cpIdx, ParticleCoordUtils.Source2ToGodotRotation(Source2AnglesDegreesToQuaternion(angSrc2)));
                        }
                    }
                }
            }
        }

        // 2. Apply preview non-zero color defaults for renderer m_nTintCP if unset
        void ApplyTintCpDefaults(ParticleSystemSimulation sim)
        {
            var renderers = sim.Definition?.GetArray("m_Renderers");
            if (renderers != null)
            {
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    int tintCp = r.ContainsKey("m_nTintCP") ? r.GetInt32Property("m_nTintCP") : -1;
                    if (tintCp > 0 && tintCp != 1 && tintCp != 10 && !configuredCps.Contains(tintCp))
                    {
                        var vrfCp = rootSim.GetControlPoint(tintCp);
                        if (vrfCp.Position == System.Numerics.Vector3.Zero)
                        {
                            vrfCp.Position = new System.Numerics.Vector3(255.0f, 255.0f, 255.0f);
                        }
                    }
                }
            }

            foreach (var child in sim.Children)
            {
                ApplyTintCpDefaults(child);
            }
        }

        ApplyTintCpDefaults(rootSim);
    }

    private static System.Numerics.Quaternion Source2AnglesDegreesToQuaternion(System.Numerics.Vector3 pitchYawRollDeg)
    {
        const float deg2Rad = MathF.PI / 180.0f;
        var mat = ParticleCoordUtils.Source2EulerRadiansToMatrix(
            pitchYawRollDeg.X * deg2Rad,
            pitchYawRollDeg.Y * deg2Rad,
            pitchYawRollDeg.Z * deg2Rad);
        return System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.CreateFromRotationMatrix(mat));
    }

    public static ParticleSystemDefinition LoadHierarchy(
        string vpcfPath,
        IFileLoader fileLoader,
        Package basePackage = null,
        Package addonPackage = null,
        int maxDepth = 4)
    {
        if (string.IsNullOrEmpty(vpcfPath)) return null;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return LoadRecursive(vpcfPath, fileLoader, basePackage, addonPackage, visited, 0, maxDepth);
    }

    private static ParticleSystemDefinition LoadRecursive(
        string vpcfPath,
        IFileLoader fileLoader,
        Package basePackage,
        Package addonPackage,
        HashSet<string> visited,
        int currentDepth,
        int maxDepth)
    {
        if (currentDepth > maxDepth) return null;
        if (!visited.Add(vpcfPath)) return null;

        string normalized = vpcfPath.Replace('\\', '/');
        string compiledPath = normalized.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? normalized : normalized + "_c";

        VrfResource res = null;
        try
        {
            if (fileLoader != null)
            {
                res = fileLoader.LoadFile(compiledPath) ?? fileLoader.LoadFileCompiled(compiledPath);
            }

            if (res == null && addonPackage != null)
            {
                var entry = addonPackage.FindEntry(compiledPath);
                if (entry != null)
                {
                    addonPackage.ReadEntry(entry, out var bytes);
                    res = new VrfResource();
                    res.Read(new MemoryStream(bytes));
                }
            }

            if (res == null && basePackage != null)
            {
                lock (basePackage)
                {
                    var entry = basePackage.FindEntry(compiledPath);
                    if (entry != null)
                    {
                        basePackage.ReadEntry(entry, out var bytes);
                        res = new VrfResource();
                        res.Read(new MemoryStream(bytes));
                    }
                }
            }

            if (res == null && DeadlockPlayground.Materials.Source2TextureLoader.CorePackage != null)
            {
                var corePkg = DeadlockPlayground.Materials.Source2TextureLoader.CorePackage;
                lock (corePkg)
                {
                    var entry = corePkg.FindEntry(compiledPath);
                    if (entry != null)
                    {
                        corePkg.ReadEntry(entry, out var bytes);
                        res = new VrfResource();
                        res.Read(new MemoryStream(bytes));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogCallback?.Invoke($"[ParticleHierarchyDefinitionLoader] Error reading {compiledPath}: {ex.Message}");
        }

        if (res == null)
        {
            LogCallback?.Invoke($"[ParticleHierarchyDefinitionLoader] Could not resolve resource: {compiledPath}");
            return null;
        }

        ParticleSystemDefinition def;
        using (res)
        {
            def = VpcfDefinitionParser.Parse(normalized, res);
        }

        if (def == null) return null;

        // Recursively load children
        foreach (var childRef in def.Children)
        {
            if (!string.IsNullOrEmpty(childRef.ChildVpcfPath))
            {
                childRef.ChildDefinition = LoadRecursive(
                    childRef.ChildVpcfPath,
                    fileLoader,
                    basePackage,
                    addonPackage,
                    visited,
                    currentDepth + 1,
                    maxDepth);
            }
        }

        return def;
    }

    /// <summary>
    /// Flattens a recursive hierarchy of particle definitions into an ordered list with resolved delays and EndCap flags.
    /// </summary>
    public static List<(ParticleSystemDefinition Def, float Delay, bool IsEndCap)> FlattenHierarchy(ParticleSystemDefinition root)
    {
        var list = new List<(ParticleSystemDefinition Def, float Delay, bool IsEndCap)>();
        if (root == null) return list;

        void Traverse(ParticleSystemDefinition current, float accumulatedDelay, bool isEndCap)
        {
            list.Add((current, accumulatedDelay, isEndCap));
            foreach (var childRef in current.Children)
            {
                if (childRef.ChildDefinition != null)
                {
                    Traverse(
                        childRef.ChildDefinition,
                        accumulatedDelay + childRef.Delay,
                        isEndCap || childRef.IsEndCap);
                }
            }
        }

        Traverse(root, 0.0f, false);
        return list;
    }

    /// <summary>
    /// Computes the dynamic duration of a particle system hierarchy across all children and emitters.
    /// Does not hardcode duration: dynamically evaluates finite emission delays and lifetimes.
    /// </summary>
    public static float CalculateDuration(ParticleSystemDefinition root)
    {
        if (root == null) return 3.0f;

        var flattened = FlattenHierarchy(root);
        float maxFiniteDuration = 0.0f;
        bool hasContinuousInfinite = false;

        var dummyCtx = new ParticleEvaluationContext
        {
            CollectionAge = 0.0f,
            ParticleAge = 0.0f,
            Lifetime = 1.0f,
            ControlPoints = new ControlPointRegistry()
        };

        foreach (var item in flattened)
        {
            var def = item.Def;
            float systemDelay = item.Delay;

            // Determine nominal particle lifetime
            float lifetime = def.ConstantLifetime > 0.001f ? def.ConstantLifetime : 1.0f;
            foreach (var init in def.Initializers)
            {
                if (init is InitFloatDef ifd && ifd.OutputField == 1) // Lifetime
                {
                    float evalLifetime = ifd.InputValue.Evaluate(dummyCtx);
                    if (evalLifetime > 0.001f)
                    {
                        lifetime = MathF.Max(lifetime, evalLifetime);
                    }
                }
            }

            if (def.Emitters.Count == 0)
            {
                continue;
            }

            foreach (var emitter in def.Emitters)
            {
                if (emitter is ContinuousEmitterDef cont)
                {
                    float emitDur = cont.EmissionDuration.Evaluate(dummyCtx);
                    if (emitDur > 0.001f)
                    {
                        float total = systemDelay + emitDur + lifetime;
                        maxFiniteDuration = MathF.Max(maxFiniteDuration, total);
                    }
                    else
                    {
                        hasContinuousInfinite = true;
                    }
                }
                else if (emitter is InstantaneousEmitterDef inst)
                {
                    float total = systemDelay + inst.Delay + lifetime;
                    maxFiniteDuration = MathF.Max(maxFiniteDuration, total);
                }
            }
        }

        if (maxFiniteDuration > 0.05f)
        {
            // Add a small 0.1s padding so that final particles cleanly fade out
            return MathF.Max(1.0f, MathF.Ceiling((maxFiniteDuration + 0.1f) * 10f) / 10f);
        }

        return hasContinuousInfinite ? 3.0f : 2.0f;
    }
}
