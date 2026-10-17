using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using SteamDatabase.ValvePak;
using ValveResourceFormat.IO;
using VrfResource = ValveResourceFormat.Resource;

namespace DeadlockPlayground.Particles;

public static class ParticleHierarchyDefinitionLoader
{
    public static Action<string> LogCallback { get; set; } = GD.Print;

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
