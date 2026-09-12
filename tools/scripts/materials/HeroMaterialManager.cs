using System;
using System.Collections.Generic;
using Godot;
using SteamDatabase.ValvePak;
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
        // Core heroes with interactive signature shaders
        RegisterConfig(new ViscousMaterialConfig());
        RegisterConfig(new VindictaMaterialConfig());
        RegisterConfig(new LadyGeistMaterialConfig());
        RegisterConfig(new IvyMaterialConfig());
        RegisterConfig(new LashMaterialConfig());
        // Heroes with runtime-palette colour overrides (colour not baked in VMAT)
        RegisterConfig(new MirageMaterialConfig());
        // Wraith: head NPR shader (source2_pbr.gdshader) wired via Source2MaterialHelper
        RegisterConfig(new WraithMaterialConfig());
    }

    public static void RegisterConfig(IHeroMaterialConfig config)
    {
        if (config == null) return;
        _configs[config.HeroKey] = config;
    }

    /// <summary>
    /// Resolves the matching hero config based on the hero name or internal model identifier.
    /// Supports aliases (e.g. "hornet" -> Vindicta, "ghost" -> Lady Geist, "tengu" -> Ivy).
    /// </summary>
    public static IHeroMaterialConfig GetConfigForHero(string heroName)
    {
        if (string.IsNullOrWhiteSpace(heroName)) return null;
        string lower = heroName.ToLowerInvariant();

        if (_configs.TryGetValue(lower, out var exact)) return exact;

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
    /// Configures the base StandardMaterial3D during VPK model loading using the appropriate hero configuration.
    /// Safely ignores non-PBR materials (e.g. dynamic glow ShaderMaterial).
    /// </summary>
    public static void ConfigureMaterial(string heroName, string meshName, int surfaceIndex, string vmatPath, Material material, Package package = null)
    {
        if (material is StandardMaterial3D stdMat)
        {
            var config = GetConfigForHero(heroName);
            config?.ConfigureBaseMaterial(meshName, surfaceIndex, vmatPath, stdMat);

            // Wraith diagnostic fallback: VRF's glTF exporter may strip vertex color arrays from the head
            // mesh, leaving F_VERTEX_COLOR surfaces entirely white (all vertices = 1,1,1,1).
            // If her head material has VertexColorUseAsAlbedo enabled but only the generic white dummy
            // texture bound (498635a), apply a canonical warm pale skin tone so she does not render
            // as a porcelain mannequin.  This fires only when vertex color data is provably absent.
        //     if (heroName.Contains("wraith", StringComparison.OrdinalIgnoreCase) &&
        //         vmatPath.Contains("wraith_head", StringComparison.OrdinalIgnoreCase) &&
        //         stdMat.VertexColorUseAsAlbedo &&
        //         (stdMat.AlbedoTexture == null ||
        //          (stdMat.AlbedoTexture.ResourcePath?.Contains("498635a") == true)))
        //     {
        //         // Warm pale skin tone — matches Wraith's canonical face/hair tone from Valve reference art.
        //         stdMat.AlbedoColor = new Color(0.82f, 0.72f, 0.68f, 1.0f);
        //     }
        }
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
