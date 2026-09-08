using System;
using Godot;

namespace DeadlockPlayground.Materials.Heroes;

public class BebopMaterialConfig : IHeroMaterialConfig
{
    public string HeroKey => "bebop";
    public string DisplayName => "Bebop: Scrap Golem";

    public void ConfigureBaseMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D material)
    {
        string meshLower = meshName.ToLowerInvariant();
        string vmatLower = vmatPath.ToLowerInvariant();

        // 1. Head (disable green status effect emission, restore yellow painted metal)
        if (meshLower.Contains("head") || vmatLower.Contains("head"))
        {
            material.EmissionEnabled = false;
            material.Metallic = 0.35f;
            material.Roughness = 0.45f;
            return;
        }

        // 2. Weapon (bebop_weapon, gun_front, gun_base: disable pink status effect emission, restore heavy steel)
        if (meshLower.Contains("gun") || meshLower.Contains("weapon") || vmatLower.Contains("weapon"))
        {
            material.EmissionEnabled = false;
            material.Metallic = 0.85f;
            material.Roughness = 0.30f;
            return;
        }

        // 3. Arms & Hands
        if (meshLower.Contains("arm") || meshLower.Contains("hand") || meshLower.Contains("forearm") ||
            vmatLower.Contains("arm"))
        {
            material.EmissionEnabled = false;
            material.Metallic = 0.50f;
            material.Roughness = 0.45f;
            return;
        }

        // 4. Body (upper and lower chassis)
        material.EmissionEnabled = false;
        material.Metallic = 0.45f;
        material.Roughness = 0.50f;
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
