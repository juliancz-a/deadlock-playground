using System;
using Godot;

namespace DeadlockPlayground.Materials.Heroes;

public class YamatoMaterialConfig : IHeroMaterialConfig
{
    public string HeroKey => "yamato";
    public string DisplayName => "Yamato";

    public void ConfigureBaseMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D material)
    {
        string meshLower = meshName.ToLowerInvariant();
        string vmatLower = vmatPath.ToLowerInvariant();

        material.EmissionEnabled = false;

        // Weapons (katana, shortsword, scabbard, hilt)
        if (meshLower.Contains("weapon") || meshLower.Contains("scabbard") ||
            meshLower.Contains("sword") || meshLower.Contains("hilt") ||
            vmatLower.Contains("sword") || vmatLower.Contains("weapon"))
        {
            material.Metallic = 0.85f;
            material.Roughness = 0.28f;
        }
        else
        {
            // Robes, armor plates, hair
            material.Metallic = 0.05f;
            material.Roughness = 0.65f;
        }
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
