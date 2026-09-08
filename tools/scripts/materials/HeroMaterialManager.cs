using System;
using System.Collections.Generic;
using Godot;
using DeadlockPlayground.Materials.Heroes;

namespace DeadlockPlayground.Materials;

/// <summary>
/// Central registry and coordinator for hero-specific material configurations and signature shaders.
/// Keeps Source2MaterialHelper and UI components fully decoupled and modular.
/// </summary>
public static class HeroMaterialManager
{
    private static readonly Dictionary<string, IHeroMaterialConfig> _configs = new(StringComparer.OrdinalIgnoreCase);

    static HeroMaterialManager()
    {
        RegisterConfig(new ViscousMaterialConfig());
        RegisterConfig(new VindictaMaterialConfig());
        RegisterConfig(new LadyGeistMaterialConfig());
        RegisterConfig(new ParadoxMaterialConfig());
        RegisterConfig(new YamatoMaterialConfig());
        RegisterConfig(new ShivMaterialConfig());
        RegisterConfig(new BebopMaterialConfig());
    }

    public static void RegisterConfig(IHeroMaterialConfig config)
    {
        if (config == null) return;
        _configs[config.HeroKey] = config;
    }

    /// <summary>
    /// Resolves the matching hero config based on the hero name or internal model identifier.
    /// Supports aliases (e.g. "hornet" -> Vindicta, "ghost" -> Lady Geist, "chrono" -> Paradox).
    /// </summary>
    public static IHeroMaterialConfig GetConfigForHero(string heroName)
    {
        if (string.IsNullOrWhiteSpace(heroName)) return null;
        string lower = heroName.ToLowerInvariant();

        if (_configs.TryGetValue(lower, out var exact)) return exact;

        if (lower.Contains("viscous")) return _configs.GetValueOrDefault("viscous");
        if (lower.Contains("hornet") || lower.Contains("vindicta")) return _configs.GetValueOrDefault("vindicta");
        if (lower.Contains("ghost") || lower.Contains("lady") || lower.Contains("geist")) return _configs.GetValueOrDefault("ghost");
        if (lower.Contains("chrono") || lower.Contains("paradox")) return _configs.GetValueOrDefault("chrono");
        if (lower.Contains("yamato")) return _configs.GetValueOrDefault("yamato");
        if (lower.Contains("shiv")) return _configs.GetValueOrDefault("shiv");
        if (lower.Contains("bebop")) return _configs.GetValueOrDefault("bebop");

        return null;
    }

    /// <summary>
    /// Configures the base StandardMaterial3D during VPK model loading using the appropriate hero configuration.
    /// </summary>
    public static void ConfigureMaterial(string heroName, string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D material)
    {
        if (material == null) return;
        var config = GetConfigForHero(heroName);
        config?.ConfigureBaseMaterial(meshName, surfaceIndex, vmatPath, material);
    }

    /// <summary>
    /// Returns a signature ShaderMaterial override for the given surface if supported by the hero.
    /// </summary>
    public static ShaderMaterial GetSignatureMaterial(string heroName, string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D baseMat)
    {
        var config = GetConfigForHero(heroName);
        return config?.GetSignatureMaterial(meshName, surfaceIndex, vmatPath, baseMat);
    }
}
