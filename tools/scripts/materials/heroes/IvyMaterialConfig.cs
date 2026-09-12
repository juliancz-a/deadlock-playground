using System;
using Godot;

namespace DeadlockPlayground.Materials.Heroes;

/// <summary>
/// Hero material configuration for Ivy (Tengu).
/// Her entire body, head, face, and eyes are embedded into a single unified 4096x4096 atlas (ivy_bodyv3).
/// Ensures vertex color multiplication is disabled on ivy_bodyv3 so face/eye socket baked AO doesn't black out the eyes/face.
/// </summary>
public class IvyMaterialConfig : IHeroMaterialConfig
{
    public string HeroKey => "ivy";
    public string DisplayName => "Ivy (Tengu)";

    public void ConfigureBaseMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D material)
    {
        string vLower = vmatPath?.ToLowerInvariant() ?? "";
        string mLower = meshName?.ToLowerInvariant() ?? "";

        if (vLower.Contains("ivy_bodyv3") || ((vLower.Contains("ivy") || vLower.Contains("tengu")) && !vLower.Contains("vertcolor")))
        {
            // Disable vertex color multiplication so face/socket baked AO does not black out eyes and facial skin
            material.VertexColorUseAsAlbedo = false;
            // ivy_bodyv3 has F_RENDER_BACKFACES = 1 to render eyeball cavity geometry without backface culling
            material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        }

        if (vLower.Contains("vertcolor") || vLower.Contains("vertcolor_pbr_basic"))
        {
            material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            material.AlphaScissorThreshold = 0.1f;
            material.VertexColorUseAsAlbedo = true;
            material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            material.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always;
            material.AlbedoColor = Colors.White;
        }

        if (vLower.Contains("eyelash") || vLower.Contains("lashes") || vLower.Contains("eyeshadow") ||
            mLower.Contains("eyelash") || mLower.Contains("lashes") || mLower.Contains("eyeshadow"))
        {
            material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            material.AlphaScissorThreshold = 0.5f;
            material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
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
