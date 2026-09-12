using System;
using System.Collections.Generic;
using Godot;
using SteamDatabase.ValvePak;
using ValveResourceFormat.ResourceTypes;
using VrfMaterial = ValveResourceFormat.ResourceTypes.Material;

namespace DeadlockPlayground.Materials.Archetypes;

/// <summary>
/// Builds ShaderMaterial instances using source2_glass.gdshader for translucent glass,
/// domes, and lenses (e.g. Dynamo head dome & gun canister, Paradox visor, Paige glasses).
/// </summary>
public static class GlassMaterialBuilder
{
    public static bool IsApplicable(VrfMaterial mat, string vmatPath, GlassParams glassParams)
    {
        return glassParams != null && glassParams.IsGlass;
    }

    public static ShaderMaterial Build(Package package, VrfMaterial matResource, string vmatPath, GlassParams glassParams)
    {
        var glassShader = Source2ShaderRegistry.GetGlassShader();
        if (glassShader == null) return null;

        var shaderMat = new ShaderMaterial { Shader = glassShader };
        // Always render glass at priority 1 so internal geometry (reactor core, gears, neck)
        // depth-writes before the glass surface samples the screen texture.
        shaderMat.RenderPriority = 1;

        // 1. Generic base color from TextureColor1 or g_vColorTint
        Vector3 baseColor = new Vector3(0.06f, 0.07f, 0.09f); // Dark smoked glass base

        if (matResource.VectorParams.TryGetValue("TextureColor1", out var tc1) && (tc1.X > 0.001f || tc1.Y > 0.001f || tc1.Z > 0.001f))
            baseColor = new Vector3(tc1.X, tc1.Y, tc1.Z);
        else if (matResource.VectorParams.TryGetValue("TextureColor", out var tc0) && (tc0.X > 0.001f || tc0.Y > 0.001f || tc0.Z > 0.001f))
            baseColor = new Vector3(tc0.X, tc0.Y, tc0.Z);
        else if (matResource.VectorParams.TryGetValue("g_vColorTint1", out var tintVec1) && !Source2ColorMatrix.IsNeutralWhiteOrBlack(tintVec1))
            baseColor = new Vector3(tintVec1.X, tintVec1.Y, tintVec1.Z);
        else if (matResource.VectorParams.TryGetValue("g_vColorTint", out var tintVec0) && !Source2ColorMatrix.IsNeutralWhiteOrBlack(tintVec0))
            baseColor = new Vector3(tintVec0.X, tintVec0.Y, tintVec0.Z);

        Color glassTint = new Color(baseColor.X, baseColor.Y, baseColor.Z, glassParams.Opacity);

        shaderMat.SetShaderParameter("glass_tint", glassTint);
        shaderMat.SetShaderParameter("roughness", glassParams.Roughness);
        shaderMat.SetShaderParameter("metallic", glassParams.Metallic);
        shaderMat.SetShaderParameter("clearcoat", glassParams.Clearcoat);
        shaderMat.SetShaderParameter("clearcoat_roughness", glassParams.ClearcoatRoughness);
        shaderMat.SetShaderParameter("refract_amount", glassParams.RefractAmount);

        // 2. Generic Self-Illumination and Animated Liquid/Bubbles in Glass (e.g. Viscous head)
        bool hasGlassSelfIllum = matResource.IntParams.GetValueOrDefault("F_SELF_ILLUM", 0) == 1;
        shaderMat.SetShaderParameter("enable_self_illum", hasGlassSelfIllum);
        if (hasGlassSelfIllum)
        {
            System.Numerics.Vector4 siTint = System.Numerics.Vector4.One;
            if (matResource.VectorParams.TryGetValue("g_vSelfIllumTint1", out var sit1)) siTint = sit1;
            else if (matResource.VectorParams.TryGetValue("g_vSelfIllumTint", out var sit0)) siTint = sit0;
            shaderMat.SetShaderParameter("self_illum_tint", new Color(siTint.X, siTint.Y, siTint.Z, 1.0f));

            float siScale = 1.0f;
            if (matResource.FloatParams.TryGetValue("g_flSelfIllumScale1", out var sis1)) siScale = sis1;
            else if (matResource.FloatParams.TryGetValue("g_flSelfIllumScale", out var sis0)) siScale = sis0;
            shaderMat.SetShaderParameter("self_illum_scale", siScale);

            float siAlbedoFactor = 0.0f;
            if (matResource.FloatParams.TryGetValue("g_flSelfIllumAlbedoFactor1", out var flAf1)) siAlbedoFactor = flAf1;
            else if (matResource.FloatParams.TryGetValue("g_flSelfIllumAlbedoFactor", out var flAf0)) siAlbedoFactor = flAf0;
            shaderMat.SetShaderParameter("self_illum_albedo_factor", siAlbedoFactor);

            Vector2 glassSiScroll = Vector2.Zero;
            if (matResource.VectorParams.TryGetValue("g_vSelfIllumScrollSpeed1", out var siss1)) glassSiScroll = new Vector2(siss1.X, siss1.Y);
            else if (matResource.VectorParams.TryGetValue("g_vSelfIllumScrollSpeed", out var siss0)) glassSiScroll = new Vector2(siss0.X, siss0.Y);
            shaderMat.SetShaderParameter("self_illum_scroll_speed", glassSiScroll);

            Vector2 glassAlbScroll = Vector2.Zero;
            if (matResource.VectorParams.TryGetValue("g_vAlbedoScrollSpeed1", out var as1)) glassAlbScroll = new Vector2(as1.X, as1.Y);
            else if (matResource.VectorParams.TryGetValue("g_vAlbedoScrollSpeed", out var as0)) glassAlbScroll = new Vector2(as0.X, as0.Y);
            shaderMat.SetShaderParameter("albedo_scroll_speed", glassAlbScroll);

            bool hasSiMask = Source2TextureLoader.BindTextureIfPresent(package, matResource, "g_tSelfIllumMask", shaderMat, "self_illum_mask", true)
                          || Source2TextureLoader.BindTextureIfPresent(package, matResource, "TextureSelfIllumMask", shaderMat, "self_illum_mask", true);
            shaderMat.SetShaderParameter("use_self_illum_mask", hasSiMask);
        }

        string colorPath = Source2TextureLoader.GetTextureParam(matResource, "g_tColor") 
                        ?? Source2TextureLoader.GetTextureParam(matResource, "TextureColor");
        if (!string.IsNullOrEmpty(colorPath))
        {
            var colorTex = Source2TextureLoader.GetOrLoadTexture(package, colorPath, forceOpaque: true);
            if (colorTex != null) shaderMat.SetShaderParameter("albedo_texture", colorTex);
        }

        string glassNormalPath = Source2TextureLoader.GetTextureParam(matResource, "g_tNormal")
                              ?? Source2TextureLoader.GetTextureParam(matResource, "g_tNormalRoughness")
                              ?? Source2TextureLoader.GetTextureParam(matResource, "TextureNormalRoughness")
                              ?? Source2TextureLoader.GetTextureParam(matResource, "TextureNormal");
        if (!string.IsNullOrEmpty(glassNormalPath))
        {
            var normalTex = Source2TextureLoader.GetOrLoadTexture(package, glassNormalPath, forceOpaque: true);
            if (normalTex != null) shaderMat.SetShaderParameter("normal_texture", normalTex);
        }

        return shaderMat;
    }
}
