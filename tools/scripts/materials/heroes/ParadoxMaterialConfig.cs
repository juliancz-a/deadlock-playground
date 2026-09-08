using System;
using Godot;

namespace DeadlockPlayground.Materials.Heroes;

public class ParadoxMaterialConfig : IHeroMaterialConfig
{
    public string HeroKey => "chrono";
    public string DisplayName => "Paradox";

    public void ConfigureBaseMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D material)
    {
        string meshLower = meshName.ToLowerInvariant();
        string vmatLower = vmatPath.ToLowerInvariant();

        // Head glass helmet
        if (meshLower.Contains("head") && vmatLower.Contains("headglass"))
        {
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            material.AlbedoColor = new Color(0.40f, 0.70f, 0.85f, 0.35f);
            material.Roughness = 0.05f;
            material.Metallic = 0.10f;
            material.ClearcoatEnabled = true;
            material.Clearcoat = 1.0f;
            material.ClearcoatRoughness = 0.05f;
            material.EmissionEnabled = false;
            return;
        }

        // Weapon
        if (meshLower.Contains("gun") || vmatLower.Contains("gun"))
        {
            material.EmissionEnabled = false;
            material.Metallic = 0.75f;
            material.Roughness = 0.35f;
            return;
        }

        // Head hourglass
        if (vmatLower.Contains("hourglass"))
        {
            material.Roughness = 0.20f;
            material.ClearcoatEnabled = true;
            material.Clearcoat = 1.0f;
            return;
        }

        // Main body (chrono_v2): maintains diffuse and normal maps
        material.Roughness = 0.55f;
        material.Metallic = 0.25f;
    }

    public ShaderMaterial GetSignatureMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D baseMat)
    {
        return null;
    }

    public Control BuildUI(Action<string, Variant> onParameterChanged)
    {
        return null;
    }
}
