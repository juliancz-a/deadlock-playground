using System;
using Godot;

namespace DeadlockPlayground.Materials;

/// <summary>
/// Interface for hero-specific material and shader configurations.
/// Keeps Source2MaterialHelper clean, generic, and modular.
/// </summary>
public interface IHeroMaterialConfig
{
    /// <summary>
    /// Unique key for hero lookup (e.g. "viscous", "vindicta", "ghost", "chrono", "yamato").
    /// </summary>
    string HeroKey { get; }

    /// <summary>
    /// Human-readable hero display name.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Adjusts or configures the base StandardMaterial3D during VPK model loading (e.g. outline cull mode, transparency).
    /// </summary>
    void ConfigureBaseMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D material);

    /// <summary>
    /// Returns a signature ShaderMaterial override for a specific mesh surface, or null if original should be used.
    /// </summary>
    ShaderMaterial GetSignatureMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D baseMat);

    /// <summary>
    /// Constructs the hero-specific controls dynamically in C# for ShadingTabUI, avoiding .tscn node bloat.
    /// Returns null if no custom parameters are needed.
    /// </summary>
    Control BuildUI(Action<string, Variant> onParameterChanged);
}
