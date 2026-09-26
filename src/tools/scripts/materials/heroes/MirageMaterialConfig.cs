using System;
using Godot;

namespace DeadlockPlayground.Materials.Heroes;

/// <summary>
/// Hero material configuration for Mirage.
/// 
/// Mirage's turban, hair, and beard geometry is covered by "miragev3_vertcolor.vmat",
/// which declares F_PAINT_VERTEX_COLORS = 1 and contains a compiled 4×4 dark-brown
/// transmissive texture (g_tNprTransmissiveColor = ...ffc09775.vtex).
/// 
/// Like Wraith's head, the surface is rendered dynamically via source2_pbr.gdshader
/// (ShaderMaterial), where the vertex color stream is multiplied by the authentic
/// transmissive texture and blended via the Blue-channel ramp clamp(1.0 - vert.b, 0.0, 1.0).
/// Facial relief and roughness are decoded from g_tNormalRoughness (ebebd272).
/// 
/// This config is a thin guard; the actual ShaderMaterial assembly is handled in
/// Source2MaterialHelper.CreateMaterialFromVmat.
/// </summary>
public class MirageMaterialConfig : IHeroMaterialConfig
{
    public string HeroKey     => "mirage";
    public string DisplayName => "Mirage";

    public void ConfigureBaseMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D material)
    {
        // miragev3_vertcolor is rendered via source2_pbr.gdshader (ShaderMaterial),
        // so this callback only fires for remaining StandardMaterial3D surfaces (body, clothing, weapons).
    }

    public ShaderMaterial GetSignatureMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D baseMat)
        => null; // No signature shader override for Mirage.

    public Control BuildUI(Action<string, Variant> onParameterChanged)
    {
        return null;
    }
}
