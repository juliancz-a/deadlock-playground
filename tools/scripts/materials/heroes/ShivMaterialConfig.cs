using System;
using Godot;

namespace DeadlockPlayground.Materials.Heroes;

public class ShivMaterialConfig : IHeroMaterialConfig
{
    public string HeroKey => "shiv";
    public string DisplayName => "Shiv: The Enforcer";

    public void ConfigureBaseMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D material)
    {
        string meshLower = meshName.ToLowerInvariant();
        string vmatLower = vmatPath.ToLowerInvariant();

        // 1. Sunglasses
        if (meshLower.Contains("glasses") || vmatLower.Contains("glasses"))
        {
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            material.AlbedoColor = new Color(0.10f, 0.10f, 0.12f, 0.60f);
            material.Roughness = 0.05f;
            material.Metallic = 0.20f;
            material.ClearcoatEnabled = true;
            material.Clearcoat = 1.0f;
            material.ClearcoatRoughness = 0.05f;
            material.EmissionEnabled = false;
            return;
        }

        // 2. Weapon / Gun
        if (meshLower.Contains("gun") || vmatLower.Contains("gun"))
        {
            material.Roughness = 0.35f;
            material.Metallic = 0.80f;
            material.EmissionEnabled = false;
            return;
        }

        // 3. Hair (disable white blowout emission, natural dark hair shading)
        if (vmatLower.Contains("shiv_hair") || meshLower.Contains("hair"))
        {
            material.Roughness = 0.85f;
            material.Metallic = 0.0f;
            material.EmissionEnabled = false;
            return;
        }

        // 4. Pants / Legs (disable white blowout emission)
        if (vmatLower.Contains("shiv_legs"))
        {
            material.Roughness = 0.75f;
            material.Metallic = 0.05f;
            material.EmissionEnabled = false;
            return;
        }

        // 5. Details / Mods (belts, bucklers, metallic gear)
        if (vmatLower.Contains("shiv_mods"))
        {
            material.Roughness = 0.35f;
            material.Metallic = 0.65f;
            material.EmissionEnabled = false;
            return;
        }

        // 6. Jacket (leather)
        if (vmatLower.Contains("shiv_jacket") || meshLower.Contains("jacket"))
        {
            material.Roughness = 0.50f;
            material.Metallic = 0.10f;
            material.EmissionEnabled = false;
            return;
        }

        // 7. Head & skin
        if (vmatLower.Contains("shiv_head") || vmatLower.Contains("shiv_skin"))
        {
            material.Roughness = 0.65f;
            material.Metallic = 0.0f;
            material.EmissionEnabled = false;
            return;
        }

        // 8. Reliquary gem
        if (vmatLower.Contains("reliquary"))
        {
            material.Roughness = 0.15f;
            material.Metallic = 0.20f;
            material.ClearcoatEnabled = true;
            material.Clearcoat = 1.0f;
            material.EmissionEnabled = false;
            return;
        }

        // Default shiv surfaces: disable fake emission
        material.EmissionEnabled = false;
        material.Roughness = 0.60f;
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
