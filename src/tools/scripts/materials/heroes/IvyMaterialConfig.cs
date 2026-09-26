using System;
using Godot;
using SteamDatabase.ValvePak;

namespace DeadlockPlayground.Materials.Heroes;

/// <summary>
/// Hero material configuration for Ivy (Tengu).
/// Coordinates the unified 4096x4096 body atlas (ivy_bodyv3), eye socket two-sided culling,
/// facial AO protection (VertexColorUseAsAlbedo = false), and pupil alpha-card decal cutout.
/// </summary>
public class IvyMaterialConfig : IHeroMaterialConfig
{
    public string HeroKey => "ivy";
    public string DisplayName => "Ivy / Tengu";

    public Color? SignatureGlowColor => new Color(0.2f, 0.8f, 0.4f, 1.0f);

    public void ConfigureBaseMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D material)
    {
        string vLower = vmatPath?.ToLowerInvariant() ?? "";
        string mLower = meshName?.ToLowerInvariant() ?? "";

        bool isVertColor = vLower.Contains("vertcolor");
        bool isPupilSurface = isVertColor && (surfaceIndex == 3 || mLower.Contains("eye") || mLower.Contains("pupil"));

        if (isPupilSurface)
        {
            // Ivy Surface 3: Flat pupil alpha-card decal sitting directly in front of golden sclera
            material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            material.AlphaScissorThreshold = 0.1f;
            material.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always;
            material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            material.VertexColorUseAsAlbedo = true;
            material.AlbedoColor = Colors.White;
        }
        else if (vLower.Contains("ivy_bodyv3") || vLower.Contains("tengu"))
        {
            // Body, face, and eyes unified atlas:
            // Disable vertex color so facial socket cavity AO does not multiply the eyes into black voids.
            material.VertexColorUseAsAlbedo = false;
            // Two-sided rendering so eyeball cavity backfaces render without being culled
            material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        }
    }

    public ShaderMaterial GetSignatureMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D baseMat)
    {
        return null;
    }

    public Material TryCreateCustomMaterial(Package package, string vmatPath, string meshName)
    {
        return null;
    }

    public Control BuildUI(Action<string, Variant> onParameterChanged)
    {
        return null;
    }
}
