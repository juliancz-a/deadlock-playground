using System;
using System.Collections.Generic;
using Godot;
using SteamDatabase.ValvePak;
using ValveResourceFormat.ResourceTypes;
using VrfMaterial = ValveResourceFormat.ResourceTypes.Material;

namespace DeadlockPlayground.Materials.Archetypes;

/// <summary>
/// Builds StandardMaterial3D instances for PBR clothing, body, skin, accessories, and weapons
/// from Source 2 VMAT attributes.
/// </summary>
public static class StandardPbrMaterialBuilder
{
    public static StandardMaterial3D Build(
        Package package,
        VrfMaterial matResource,
        string vmatPath,
        string meshName,
        GlassParams glassParams)
    {
        string vmatLower = vmatPath?.ToLowerInvariant() ?? "";
        var godotMat = new StandardMaterial3D();

        bool isAdditive = matResource.IntParams.TryGetValue("F_ADDITIVE_BLEND", out long addVal) && addVal == 1;
        bool isAlphaTest = matResource.IntParams.TryGetValue("F_ALPHA_TEST", out long alphaTestVal) && alphaTestVal == 1;
        bool isVertColorMat = matResource.IntParams.GetValueOrDefault("F_VERTEX_COLOR", 0) == 1 || vmatLower.Contains("vertcolor");
        bool isFur = vmatLower.Contains("fur") || (meshName != null && meshName.Contains("fur", StringComparison.OrdinalIgnoreCase));
        bool isEyelashOrShadow = vmatLower.Contains("eyelash") || vmatLower.Contains("lashes") || vmatLower.Contains("eyeshadow");

        // Solid hair/head volumes (Wraith, McGinnis, Mirage, etc.) use vertcolor_pbr_basic but are
        // fully opaque geometry — AlphaScissor on these meshes breaks depth writes and washes vertex colors.
        bool isSolidVertColorHair = isVertColorMat && !isFur;

        bool renderBackfaces = (matResource.IntParams.TryGetValue("F_RENDER_BACKFACES", out long rbfVal) && rbfVal == 1)
                            || isFur; // Fur shells need two-sided card rendering

        if (isAdditive)
        {
            godotMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            godotMat.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
            godotMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            godotMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        }
        else if (isAlphaTest || isEyelashOrShadow || isFur)
        {
            godotMat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            float alphaRef = isFur ? 0.35f : 0.5f;
            if (!isFur)
            {
                if (matResource.FloatParams.TryGetValue("g_flAlphaTestReference1", out var ar1)) alphaRef = ar1;
                else if (matResource.FloatParams.TryGetValue("g_flAlphaTestReference", out var ar0)) alphaRef = ar0;
            }
            godotMat.AlphaScissorThreshold = alphaRef;
            godotMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            godotMat.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always;
        }
        else if (isSolidVertColorHair)
        {
            godotMat.Transparency = BaseMaterial3D.TransparencyEnum.Disabled;
            godotMat.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.OpaqueOnly;
            godotMat.CullMode = BaseMaterial3D.CullModeEnum.Back;
            godotMat.VertexColorUseAsAlbedo = true;
        }

        if (renderBackfaces)
        {
            godotMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        }

        var albedoParams = VmatColorExtractor.ExtractAlbedo(matResource, vmatPath, glassParams?.IsGlass ?? false, glassParams?.Opacity ?? 1.0f);
        bool forceOpaqueColor = !isAdditive && !isAlphaTest && !isEyelashOrShadow && !isFur && !(glassParams?.IsTranslucent ?? false);
        string diffusePath = albedoParams.ColorTexturePath;

        if (!string.IsNullOrEmpty(diffusePath))
        {
            godotMat.AlbedoTexture = Source2TextureLoader.GetOrLoadTexture(package, diffusePath, forceOpaqueColor);
            godotMat.VertexColorUseAsAlbedo = false;
            godotMat.AlbedoColor = Colors.White;
        }
        else
        {
            godotMat.AlbedoColor = albedoParams.BaseColor;
            godotMat.VertexColorUseAsAlbedo = albedoParams.UseVertexColorAsAlbedo;
        }

        godotMat.Uv1Scale  = albedoParams.UvScale;
        godotMat.Uv1Offset = albedoParams.UvOffset;

        if (isAdditive && matResource.VectorParams.TryGetValue("g_vSelfIllumTint1", out var addTint))
        {
            godotMat.AlbedoColor = new Color(addTint.X, addTint.Y, addTint.Z, 1.0f);
        }

        if (godotMat.AlbedoTexture != null && !isAdditive)
        {
            godotMat.AlbedoColor = Colors.White;
        }

        // Normal Map
        string normalPath = Source2TextureLoader.GetTextureParam(matResource, "g_tNormal") 
                         ?? Source2TextureLoader.GetTextureParam(matResource, "g_tNormalRoughness");
        if (!string.IsNullOrEmpty(normalPath))
        {
            godotMat.NormalEnabled = true;
            godotMat.NormalTexture = Source2TextureLoader.GetOrLoadTexture(package, normalPath, forceOpaque: true);
        }

        // Ambient Occlusion
        string aoPath = Source2TextureLoader.GetTextureParam(matResource, "g_tAmbientOcclusion") 
                     ?? Source2TextureLoader.GetTextureParam(matResource, "g_tAO");
        if (!string.IsNullOrEmpty(aoPath))
        {
            godotMat.AOEnabled = true;
            godotMat.AOTexture = Source2TextureLoader.GetOrLoadTexture(package, aoPath, forceOpaque: true);
        }

        // Emissive / Self-illumination
        var selfIllumParams = VmatColorExtractor.ExtractSelfIllum(matResource, vmatPath, isAdditive);
        if (selfIllumParams.IsEmissive)
        {
            ImageTexture maskTex = null;
            if (!string.IsNullOrEmpty(selfIllumParams.MaskTexturePath))
            {
                maskTex = Source2TextureLoader.GetOrLoadTexture(package, selfIllumParams.MaskTexturePath, forceOpaque: true);
            }

            if (string.IsNullOrEmpty(selfIllumParams.MaskTexturePath) || maskTex != null)
            {
                godotMat.EmissionEnabled = true;
                godotMat.EmissionOperator = BaseMaterial3D.EmissionOperatorEnum.Multiply;
                godotMat.EmissionEnergyMultiplier = Math.Clamp(selfIllumParams.EnergyMultiplier * 0.25f, 1.0f, 2.5f);
                godotMat.Emission = selfIllumParams.EmissionColor;

                if (maskTex != null)
                {
                    godotMat.EmissionTexture = maskTex;
                }
            }
        }

        // Roughness & Metallic
        bool isWeapon = vmatLower.Contains("weapon") || vmatLower.Contains("gun") ||
                        vmatLower.Contains("sword") || vmatLower.Contains("katana") ||
                        vmatLower.Contains("bow") || vmatLower.Contains("shortsword");
        bool isDedicatedGlow = vmatLower.Contains("glow") || vmatLower.Contains("hourglass") ||
                               vmatLower.Contains("portal") || vmatLower.Contains("flame") ||
                               vmatLower.Contains("light") || vmatLower.Contains("beam") || isAdditive;

        if (isDedicatedGlow)
        {
            godotMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        }

        float roughness = isWeapon ? 0.45f : 0.65f;
        if (matResource.VectorParams.TryGetValue("TextureRoughness1", out var rVec)) roughness = rVec.X;
        else if (matResource.VectorParams.TryGetValue("TextureRoughness", out var rVec0)) roughness = rVec0.X;
        else if (matResource.FloatParams.TryGetValue("g_flRoughnessScale1", out float rScale)) roughness = rScale;
        godotMat.Roughness = roughness;

        float metallic = isWeapon ? 0.35f : (isDedicatedGlow && !isAdditive ? 0.80f : 0.0f);
        if (matResource.VectorParams.TryGetValue("TextureMetalness1", out var mVec)) metallic = mVec.X;
        else if (matResource.VectorParams.TryGetValue("TextureMetalness", out var mVec0)) metallic = mVec0.X;
        else if (matResource.FloatParams.TryGetValue("g_flMetalnessScale1", out float mScale)) metallic = mScale;
        godotMat.Metallic = metallic;

        // Matte head / hair surfaces specular clamp: Godot's default MetallicSpecular = 0.5 produces
        // blinding glare blowout on high-roughness surfaces. Clamp to 0.08 and roughness to 0.95.
        bool isMatteHeadOrHair = roughness > 0.85f
                              || vmatLower.Contains("hair")
                              || vmatLower.Contains("head")
                              || vmatLower.Contains("vertcolor")
                              || isSolidVertColorHair;
        if (isMatteHeadOrHair)
        {
            godotMat.Roughness = 0.95f;
            godotMat.MetallicSpecular = 0.08f;
        }

        // Eye surfaces
        bool isEyeSurface = vmatLower.Contains("eye") || vmatLower.Contains("pupil") ||
                            vmatLower.Contains("cornea") || vmatLower.Contains("iris") ||
                            vmatLower.Contains("sclera");
        if (isEyeSurface)
        {
            godotMat.VertexColorUseAsAlbedo = false;
            godotMat.Roughness = 0.15f;
            godotMat.Metallic = 0.0f;
            if (godotMat.AlbedoColor.R <= 0.05f && godotMat.AlbedoColor.G <= 0.05f && godotMat.AlbedoColor.B <= 0.05f)
            {
                godotMat.AlbedoColor = Colors.White;
            }
        }

        return godotMat;
    }
}
