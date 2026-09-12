using System;
using System.Collections.Generic;
using Godot;
using SteamDatabase.ValvePak;
using DeadlockPlayground.Materials.Heroes;

namespace DeadlockPlayground.Materials;

/// <summary>
/// Central registry and coordinator for hero-specific material configurations and signature shaders.
/// Keeps Source2MaterialHelper, loaders, and UI components fully decoupled and modular.
/// </summary>
public static class HeroMaterialManager
{
    private static readonly Dictionary<string, IHeroMaterialConfig> _configs = new(StringComparer.OrdinalIgnoreCase);

    static HeroMaterialManager()
    {
        // Core heroes with interactive signature shaders and custom material hooks
        RegisterConfig(new InfernusMaterialConfig());
        RegisterConfig(new ViscousMaterialConfig());
        RegisterConfig(new VindictaMaterialConfig());
        RegisterConfig(new LadyGeistMaterialConfig());
        RegisterConfig(new IvyMaterialConfig());
        RegisterConfig(new LashMaterialConfig());
        RegisterConfig(new MirageMaterialConfig());
        RegisterConfig(new WraithMaterialConfig());
    }

    public static void RegisterConfig(IHeroMaterialConfig config)
    {
        if (config == null) return;
        _configs[config.HeroKey] = config;
    }

    /// <summary>
    /// Resolves the matching hero config based on the hero name or internal model identifier.
    /// Supports aliases (e.g. "hornet" -> Vindicta, "ghost" -> Lady Geist, "tengu" -> Ivy, "inferno" -> Infernus).
    /// </summary>
    public static IHeroMaterialConfig GetConfigForHero(string heroName)
    {
        if (string.IsNullOrWhiteSpace(heroName)) return null;
        string lower = heroName.ToLowerInvariant();

        if (_configs.TryGetValue(lower, out var exact)) return exact;

        if (lower.Contains("inferno") || lower.Contains("infernus")) return _configs.GetValueOrDefault("inferno");
        if (lower.Contains("viscous")) return _configs.GetValueOrDefault("viscous");
        if (lower.Contains("hornet") || lower.Contains("vindicta")) return _configs.GetValueOrDefault("vindicta");
        if (lower.Contains("ghost") || lower.Contains("lady") || lower.Contains("geist")) return _configs.GetValueOrDefault("ghost");
        if (lower.Contains("ivy") || lower.Contains("tengu")) return _configs.GetValueOrDefault("ivy");
        if (lower.Contains("lash")) return _configs.GetValueOrDefault("lash");
        if (lower.Contains("mirage")) return _configs.GetValueOrDefault("mirage");
        if (lower.Contains("wraith")) return _configs.GetValueOrDefault("wraith");

        return null;
    }

    /// <summary>
    /// Attempts to create a hero-specific bespoke material (e.g. Lash sparkles, Wraith cards).
    /// Returns null if standard archetype builders should process the material.
    /// </summary>
    public static Godot.Material TryCreateCustomMaterial(string heroName, Package package, string vmatPath, string meshName)
    {
        var config = GetConfigForHero(heroName);
        if (config != null)
        {
            var customMat = config.TryCreateCustomMaterial(package, vmatPath, meshName);
            if (customMat != null) return customMat;
        }

        // If heroName is unspecified, check if any registered hero matches vmatPath or meshName
        string vLower = vmatPath?.ToLowerInvariant() ?? "";
        string mLower = meshName?.ToLowerInvariant() ?? "";

        foreach (var kvp in _configs)
        {
            string key = kvp.Key;
            if (vLower.Contains(key) || mLower.Contains(key))
            {
                var customMat = kvp.Value.TryCreateCustomMaterial(package, vmatPath, meshName);
                if (customMat != null) return customMat;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the signature glow color for a hero layer.
    /// </summary>
    public static Color? GetSignatureGlowColor(string heroName, string vmatPath)
    {
        var config = GetConfigForHero(heroName);
        if (config?.SignatureGlowColor != null) return config.SignatureGlowColor;

        string vLower = vmatPath?.ToLowerInvariant() ?? "";
        if (vLower.Contains("ghost") || vLower.Contains("geist"))
        {
            var ghostConfig = _configs.GetValueOrDefault("ghost");
            if (ghostConfig?.SignatureGlowColor != null) return ghostConfig.SignatureGlowColor;
        }

        foreach (var kvp in _configs)
        {
            if (vLower.Contains(kvp.Key) && kvp.Value.SignatureGlowColor.HasValue)
            {
                return kvp.Value.SignatureGlowColor;
            }
        }

        return null;
    }

    /// <summary>
    /// Checks if a surface must strictly preserve its original material and avoid Toon swapping.
    /// </summary>
    public static bool ShouldPreserveMaterial(string heroName, string meshName, string vmatPath, Godot.Material material)
    {
        if (material != null && material.HasMeta("PreserveShading") && (bool)material.GetMeta("PreserveShading"))
        {
            return true;
        }

        var config = GetConfigForHero(heroName);
        if (config != null && config.ShouldPreserveMaterial(meshName, vmatPath, material))
        {
            return true;
        }

        string vLower = vmatPath?.ToLowerInvariant() ?? "";
        string mLower = meshName?.ToLowerInvariant() ?? "";

        if (vLower.Contains("ghost") || vLower.Contains("geist") || mLower.Contains("ghost") || mLower.Contains("geist") || vLower.Contains("shawl") || mLower.Contains("shawl"))
        {
            var ghostConfig = _configs.GetValueOrDefault("ghost");
            if (ghostConfig != null && ghostConfig.ShouldPreserveMaterial(meshName, vmatPath, material))
            {
                return true;
            }
        }

        foreach (var kvp in _configs)
        {
            if ((vLower.Contains(kvp.Key) || mLower.Contains(kvp.Key)) &&
                kvp.Value.ShouldPreserveMaterial(meshName, vmatPath, material))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Configures the base StandardMaterial3D during VPK model loading using the appropriate hero configuration.
    /// </summary>
    public static void ConfigureMaterial(string heroName, string meshName, int surfaceIndex, string vmatPath, Godot.Material material, Package package = null)
    {
        if (material is StandardMaterial3D stdMat)
        {
            var config = GetConfigForHero(heroName);
            config?.ConfigureBaseMaterial(meshName, surfaceIndex, vmatPath, stdMat);
        }
    }

    /// <summary>
    /// Returns a signature ShaderMaterial override for the given surface if supported by the hero.
    /// </summary>
    public static ShaderMaterial GetSignatureMaterial(string heroName, string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D baseMat)
    {
        var config = GetConfigForHero(heroName);
        if (config == null)
        {
            string vLower = vmatPath?.ToLowerInvariant() ?? "";
            string mLower = meshName?.ToLowerInvariant() ?? "";
            if (vLower.Contains("ghost") || vLower.Contains("geist") || mLower.Contains("ghost") || mLower.Contains("geist") || vLower.Contains("shawl") || mLower.Contains("shawl"))
            {
                config = _configs.GetValueOrDefault("ghost");
            }
        }
        return config?.GetSignatureMaterial(meshName, surfaceIndex, vmatPath, baseMat);
    }
}
