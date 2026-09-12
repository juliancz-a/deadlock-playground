using System;
using System.Collections.Generic;
using Godot;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using VrfMaterial = ValveResourceFormat.ResourceTypes.Material;

namespace DeadlockPlayground.Materials;

/// <summary>
/// Data-transfer record holding extracted dynamic glow parameters for source2_dynamic_glow.gdshader.
/// </summary>
public record DynamicGlowParams(
    Color GlowColor,
    Vector2 ScrollSpeed,
    float FresnelExponent,
    float OpacityScale,
    string MaskTexturePath,
    bool EnableVertexJitter,
    Vector3 JitterAmplitude,
    Vector3 JitterFrequency
);

/// <summary>
/// Data-transfer record holding extracted glass and translucency parameters.
/// </summary>
public record GlassParams(
    bool IsGlass,
    bool IsTranslucent,
    float Opacity,
    float Roughness,
    float Metallic,
    float Clearcoat,
    float ClearcoatRoughness,
    float RefractAmount
);

/// <summary>
/// Data-transfer record holding extracted self-illumination parameters.
/// </summary>
public record SelfIllumParams(
    bool IsEmissive,
    Color EmissionColor,
    float EnergyMultiplier,
    string MaskTexturePath
);

/// <summary>
/// Data-transfer record holding extracted albedo and vertex color parameters.
/// </summary>
public record AlbedoParams(
    Color BaseColor,
    bool UseVertexColorAsAlbedo,
    string ColorTexturePath,
    Vector3 UvScale,
    Vector3 UvOffset
);

/// <summary>
/// Universal parameter extractor for Valve Source 2 (.vmat_c) KV3 data blocks.
/// Parses vertex color flags, glass shaders, emission, and animated UV/jitter parameters.
/// </summary>
public static class VmatColorExtractor
{
    /// <summary>
    /// Evaluates if the material represents a dynamic flow, glow, or energy layer
    /// requiring the custom procedural source2_dynamic_glow.gdshader.
    /// </summary>
    public static bool IsDynamicGlow(VrfMaterial mat, string vmatPath)
    {
        if (mat == null) return false;
        string vmatLower = vmatPath?.ToLowerInvariant() ?? "";

        // 1. Explicit additive blend flag
        if (mat.IntParams.TryGetValue("F_ADDITIVE_BLEND", out long addVal) && addVal == 1)
        {
            return true;
        }

        // 2. Animated UV scrolling parameters
        if (mat.VectorParams.TryGetValue("g_vSelfIllumScrollSpeed1", out var scrollVec1) &&
            (MathF.Abs(scrollVec1.X) > 0.0001f || MathF.Abs(scrollVec1.Y) > 0.0001f))
        {
            return true;
        }
        if (mat.VectorParams.TryGetValue("g_vSelfIllumScrollSpeed", out var scrollVec0) &&
            (MathF.Abs(scrollVec0.X) > 0.0001f || MathF.Abs(scrollVec0.Y) > 0.0001f))
        {
            return true;
        }

        // 3. Vertex jitter flag
        if (mat.IntParams.TryGetValue("F_JITTER_VERTICES", out long jitterVal) && jitterVal == 1)
        {
            return true;
        }

        // 4. Dedicated dynamic energy layers in Deadlock
        if ((vmatLower.Contains("armglow") || vmatLower.Contains("cyclonewrap_glow") || vmatLower.Contains("vindicta_glow")) &&
            !vmatLower.Contains("arm_base"))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Extracts parameters for source2_dynamic_glow.gdshader from the material KV3 attributes.
    /// </summary>
    public static DynamicGlowParams ExtractDynamicGlow(VrfMaterial mat, string vmatPath)
    {
        // 1. Glow Color / Tint (Data-driven extraction hierarchy)
        Color glowColor = Source2MaterialHelper.ExtractDynamicGlowColor(mat, vmatPath);

        // 2. Scroll Speed
        Vector2 scrollSpeed = Vector2.Zero;
        if (mat.VectorParams.TryGetValue("g_vSelfIllumScrollSpeed1", out var sv1))
        {
            scrollSpeed = new Vector2(sv1.X, sv1.Y);
        }
        else if (mat.VectorParams.TryGetValue("g_vSelfIllumScrollSpeed", out var sv0))
        {
            scrollSpeed = new Vector2(sv0.X, sv0.Y);
        }

        // 3. Fresnel Exponent
        float fresnelExp = 2.0f;
        if (mat.FloatParams.TryGetValue("g_flSelfIllumFresnelMaskExponent", out float fe1) && fe1 > 0.01f)
        {
            fresnelExp = fe1;
        }
        else if (mat.FloatParams.TryGetValue("g_flAlphaAnglePower1", out float fe2) && fe2 > 0.01f)
        {
            fresnelExp = fe2;
        }
        else if (mat.FloatParams.TryGetValue("g_flFresnelExponent", out float fe0) && fe0 > 0.01f)
        {
            fresnelExp = fe0;
        }

        // 4. Opacity Scale
        float opacityScale = 1.0f;
        if (mat.FloatParams.TryGetValue("g_flOpacityScale1", out float op1) && op1 > 0.001f)
        {
            opacityScale = op1;
        }
        else if (mat.FloatParams.TryGetValue("g_flOpacityScale", out float op0) && op0 > 0.001f)
        {
            opacityScale = op0;
        }
        else if (mat.FloatParams.TryGetValue("g_flOpacity", out float opVal) && opVal > 0.001f)
        {
            opacityScale = opVal;
        }

        // 5. Mask Texture Path
        string maskPath = GetTextureParam(mat, "g_tSelfIllumMask") ??
                          GetTextureParam(mat, "TextureSelfIllumMask") ??
                          GetTextureParam(mat, "g_tColor") ??
                          GetTextureParam(mat, "TextureColor");

        // 6. Vertex Jitter
        bool enableJitter = (mat.IntParams.TryGetValue("F_JITTER_VERTICES", out long jVal) && jVal == 1) ||
                            mat.VectorParams.ContainsKey("g_vJitterAmplitude");

        Vector3 jitterAmp = new(0.015f, 0.015f, 0.015f);
        if (mat.VectorParams.TryGetValue("g_vJitterAmplitude1", out var ja1))
        {
            jitterAmp = new Vector3(ja1.X, ja1.Y, ja1.Z);
        }
        else if (mat.VectorParams.TryGetValue("g_vJitterAmplitude", out var ja0))
        {
            jitterAmp = new Vector3(ja0.X, ja0.Y, ja0.Z);
        }

        Vector3 jitterFreq = new(10.0f, 10.0f, 10.0f);
        if (mat.VectorParams.TryGetValue("g_vJitterFrequency1", out var jf1))
        {
            jitterFreq = new Vector3(jf1.X, jf1.Y, jf1.Z);
        }
        else if (mat.VectorParams.TryGetValue("g_vJitterFrequency", out var jf0))
        {
            jitterFreq = new Vector3(jf0.X, jf0.Y, jf0.Z);
        }

        return new DynamicGlowParams(
            glowColor,
            scrollSpeed,
            fresnelExp,
            opacityScale,
            maskPath,
            enableJitter,
            jitterAmp,
            jitterFreq
        );
    }

    /// <summary>
    /// Extracts glass, lens, and translucent surface parameters.
    /// Handles Dynamo head glass, Paradox visor, Paige specs, and generic glass.
    /// </summary>
    public static GlassParams ExtractGlass(VrfMaterial mat, string vmatPath)
    {
        string vmatLower = vmatPath?.ToLowerInvariant() ?? "";
        string shaderLower = mat.ShaderName?.ToLowerInvariant() ?? "";

        bool isGlass = (mat.IntParams.TryGetValue("F_GLASS", out long glassVal) && glassVal == 1) ||
                       shaderLower.Contains("glass") ||
                       vmatLower.Contains("glass") || vmatLower.Contains("lens") ||
                       vmatLower.Contains("specs") || vmatLower.Contains("spectacle") ||
                       (mat.IntParams.TryGetValue("F_WRITE_DEPTH_BEFORE_ALPHA_BLENDING", out long depthBlend) && depthBlend == 1 &&
                        (vmatLower.Contains("trans") || vmatLower.Contains("headglass")));

        bool isTranslucent = (mat.IntParams.TryGetValue("F_TRANSLUCENT", out long transVal) && transVal == 1) || isGlass;

        float opacity = 0.55f;
        if (mat.FloatParams.TryGetValue("g_flOpacityScale1", out float op1) && op1 > 0.001f)
        {
            opacity = op1;
        }
        else if (mat.FloatParams.TryGetValue("g_flOpacity", out float op2) && op2 > 0.001f)
        {
            opacity = op2;
        }
        else if (mat.FloatParams.TryGetValue("g_flOpacityScale", out float op3) && op3 > 0.001f)
        {
            opacity = op3;
        }
        else if (mat.FloatParams.TryGetValue("g_flCloakFactor1", out float cf1) && cf1 > 0.001f)
        {
            opacity = Mathf.Clamp(cf1 * 0.6f, 0.35f, 0.75f);
        }
        else if (mat.FloatParams.TryGetValue("g_flAlphaAnglePower1", out float op4) && Mathf.Abs(op4) > 0.001f)
        {
            opacity = Mathf.Clamp(Mathf.Abs(op4) * 0.25f, 0.25f, 0.8f);
        }

        float roughness = isGlass ? 0.067f : 0.20f;
        if (mat.VectorParams.TryGetValue("TextureRoughness1", out var rVec))
        {
            roughness = rVec.X;
        }
        else if (mat.FloatParams.TryGetValue("g_flRoughnessScale1", out float rScale))
        {
            roughness = rScale;
        }

        float metallic = 0.0f;
        if (mat.VectorParams.TryGetValue("TextureMetalness1", out var mVec))
        {
            metallic = mVec.X;
        }
        else if (mat.FloatParams.TryGetValue("g_flMetalnessScale1", out float mScale))
        {
            metallic = mScale;
        }
        else if (isGlass)
        {
            metallic = 0.05f;
        }

        // Use the raw g_flCloakRefractAmount directly: 0.0 for gun canister (crisp interior),
        // 0.075 for head dome (subtle optical warp). No clamping or scaling — preserve Source 2 intent.
        float refractAmount = 0.0f;
        if (mat.FloatParams.TryGetValue("g_flCloakRefractAmount", out float crf))
        {
            refractAmount = crf;
        }

        float clearcoat = isGlass ? 1.0f : 0.0f;
        float clearcoatRoughness = 0.05f;
        if (mat.FloatParams.TryGetValue("g_flClearcoatRoughness1", out float ccRough))
        {
            clearcoatRoughness = ccRough;
        }

        return new GlassParams(isGlass, isTranslucent, opacity, roughness, metallic, clearcoat, clearcoatRoughness, refractAmount);
    }

    /// <summary>
    /// Extracts albedo, color tint, and vertex color information.
    /// Prevents dark weapon blowouts, prevents grey vertex masks from desaturating hero faces,
    /// and correctly preserves glass textures.
    /// </summary>
    public static AlbedoParams ExtractAlbedo(VrfMaterial mat, string vmatPath, bool isGlass, float glassOpacity)
    {
        string vmatLower = vmatPath?.ToLowerInvariant() ?? "";
        string shaderLower = mat.ShaderName?.ToLowerInvariant() ?? "";

        // 1. Color texture resolution
        string colorPath = GetTextureParam(mat, "g_tColor") ??
                           GetTextureParam(mat, "TextureColor") ??
                           GetTextureParam(mat, "g_tColor1") ??
                           GetTextureParam(mat, "g_tColorA") ??
                           GetTextureParam(mat, "g_tColor0") ??
                           GetTextureParam(mat, "g_tColor2") ??
                           GetTextureParam(mat, "g_tColorB") ??
                           GetTextureParam(mat, "TextureColor0") ??
                           GetTextureParam(mat, "TextureColor1");

        bool hasDiffuseTexture = !string.IsNullOrEmpty(colorPath);

        bool isEyeSurface = vmatLower.Contains("eye") || vmatLower.Contains("pupil") ||
                            vmatLower.Contains("cornea") || vmatLower.Contains("iris") ||
                            vmatLower.Contains("sclera") || vmatLower.Contains("ivy_eyes") ||
                            vmatLower.Contains("tengu_eye") || vmatLower.Contains("gargoyle_eye");

        if (string.IsNullOrEmpty(colorPath) && isEyeSurface)
        {
            colorPath = GetTextureParam(mat, "g_tIris") ??
                        GetTextureParam(mat, "g_tSclera") ??
                        GetTextureParam(mat, "g_tEyeDiffuse") ??
                        GetTextureParam(mat, "g_tEyeColor") ??
                        GetTextureParam(mat, "g_tColorIris") ??
                        GetTextureParam(mat, "g_tColorSclera") ??
                        GetTextureParam(mat, "TextureColorIris") ??
                        GetTextureParam(mat, "TextureColorSclera");

            if (string.IsNullOrEmpty(colorPath) && (vmatLower.Contains("ivy") || vmatLower.Contains("tengu")))
            {
                colorPath = "models/heroes_wip/ivy/materials/ivy_bodyv3_color_png_9ca4a4c5.vtex";
            }

            hasDiffuseTexture = !string.IsNullOrEmpty(colorPath);
        }

        if (string.IsNullOrEmpty(colorPath) && (vmatLower.Contains("ivy_bodyv3") || vmatLower.Contains("ivy") || vmatLower.Contains("tengu")))
        {
            colorPath = "models/heroes_wip/ivy/materials/ivy_bodyv3_color_png_9ca4a4c5.vtex";
            hasDiffuseTexture = true;
        }

        // Check if diffuse fallback is black [0,0,0]
        bool isTextureColorBlack = mat.VectorParams.TryGetValue("TextureColor1", out var tcBlack) &&
                                   tcBlack.X <= 0.001f && tcBlack.Y <= 0.001f && tcBlack.Z <= 0.001f;

        bool isDiffuseMissingOrBlack = !hasDiffuseTexture || isTextureColorBlack;

        // 2. Vertex Color Gating:
        // In Deadlock, almost all materials have g_bMaskVertexColorTint1=1 for gameplay ability masks.
        // Vertex colors in Source 2 hero heads/bodies are grey AO/shadows, NOT skin colors.
        // Multiplying diffuse by vertex color desaturates and darkens faces (Wraith, Mirage).
        // VertexColorUseAsAlbedo should ONLY be enabled when explicitly a vertcolor shader/flag,
        // OR when diffuse is missing/black (e.g. hair cards, feet, Mirage hands).
        // Eyeballs and Ivy's unified face atlas must NEVER use vertex color as albedo to avoid deep socket AO blacking them out.
        bool explicitVertColor = (mat.IntParams.TryGetValue("F_VERTEX_COLOR", out long vcVal) && vcVal == 1) ||
                                 (mat.IntParams.TryGetValue("F_PAINT_VERTEX_COLORS", out long pvcVal) && pvcVal == 1) ||
                                 shaderLower.Contains("vertcolor") ||
                                 vmatLower.Contains("vertcolor") ||
                                 vmatLower.Contains("_vc");

        bool hasMaskTintFlag = (mat.IntParams.TryGetValue("g_bMaskVertexColorTint1", out long mvc1) && mvc1 == 1) ||
                               (mat.IntParams.TryGetValue("g_bMaskVertexColorTint", out long mvc0) && mvc0 == 1);

        bool useVertexColorAsAlbedo = explicitVertColor || (isDiffuseMissingOrBlack && (hasMaskTintFlag || !hasDiffuseTexture));
        if (isEyeSurface || vmatLower.Contains("ivy_bodyv3") || vmatLower.Contains("ivy") || vmatLower.Contains("tengu"))
        {
            useVertexColorAsAlbedo = false;
        }

        // 3. Vector Tint Extraction:
        // True tint parameters (g_vColorTint, g_vColorTint1, m_vColorTint, g_vTintColor)
        // TextureColor/TextureColor1 in VectorParams are texture fallbacks, NOT tints.
        // Using TextureColor1 as a tint darkens weapons (like Wraith's gun) to pitch black!
        System.Numerics.Vector4? tintVec = null;
        if (mat.VectorParams.TryGetValue("g_vColorTint", out var vt1) && (vt1.X < 0.999f || vt1.Y < 0.999f || vt1.Z < 0.999f)) tintVec = vt1;
        else if (mat.VectorParams.TryGetValue("g_vColorTint1", out var vt2) && (vt2.X < 0.999f || vt2.Y < 0.999f || vt2.Z < 0.999f)) tintVec = vt2;
        else if (mat.VectorParams.TryGetValue("m_vColorTint", out var vt6)) tintVec = vt6;
        else if (mat.VectorParams.TryGetValue("g_vTintColor", out var vt7)) tintVec = vt7;
        else if (mat.VectorParams.TryGetValue("Color", out var vt8)) tintVec = vt8;
        else if (!hasDiffuseTexture)
        {
            // Only use fallback TextureColor/TextureColor1 when there is NO diffuse texture
            if (mat.VectorParams.TryGetValue("TextureColor1", out var vt4) && (vt4.X > 0.001f || vt4.Y > 0.001f || vt4.Z > 0.001f)) tintVec = vt4;
            else if (mat.VectorParams.TryGetValue("TextureColor", out var vt5) && (vt5.X > 0.001f || vt5.Y > 0.001f || vt5.Z > 0.001f)) tintVec = vt5;
        }

        Color baseColor = Colors.White;

// Si la superficie usa colores de vértices (Wraith head, Dev vertcolor, etc.),
        // el albedo base DEBE ser blanco puro. De lo contrario, cualquier tinte vectorial
        // tiñe de marrón uniforme la piel, el pelo y los ojos de la malla.
        if (useVertexColorAsAlbedo)
        {
            baseColor = Colors.White;
        }
        else if (tintVec.HasValue)
        {
            var tv = tintVec.Value;
            if (tv.X > 0.001f || tv.Y > 0.001f || tv.Z > 0.001f)
            {
                float alpha = tv.W > 0.001f ? tv.W : (isGlass ? glassOpacity : 1.0f);
                baseColor = new Color(tv.X, tv.Y, tv.Z, alpha);
            }
        }
        else if (isGlass)
        {
            baseColor = new Color(0.06f, 0.07f, 0.09f, glassOpacity);
        }

        // Si es una cabeza o superficie facial completa, nunca permitir tintes oscuros residuales
        bool isHeadSurface = vmatLower.Contains("head") || isEyeSurface;
        if (isHeadSurface && !isGlass && (baseColor != Colors.White))
        {
            baseColor = Colors.White;
        }

        // 4. UV Scale and Offset
        Vector3 uvScale = Vector3.One;
        Vector3 uvOffset = Vector3.Zero;
        if (mat.VectorParams.TryGetValue("g_vAlbedoTexcoordScale1", out var us))
        {
            uvScale = new Vector3(us.X, us.Y, 1.0f);
        }
        if (mat.VectorParams.TryGetValue("g_vAlbedoTexcoordOffset1", out var uo))
        {
            uvOffset = new Vector3(uo.X, uo.Y, 0.0f);
        }

        return new AlbedoParams(baseColor, useVertexColorAsAlbedo, colorPath, uvScale, uvOffset);
    }

    /// <summary>
    /// Extracts static PBR self-illumination / emission.
    /// Preserves proxy guard for F_USE_STATUS_EFFECTS_PROXY=1 to eliminate white blowouts on Shiv, Bebop, and Paradox.
    /// </summary>
    public static SelfIllumParams ExtractSelfIllum(VrfMaterial mat, string vmatPath, bool isAdditive)
    {
        string vmatLower = vmatPath?.ToLowerInvariant() ?? "";

        bool isStatusProxy = mat.IntParams.TryGetValue("F_USE_STATUS_EFFECTS_PROXY", out long statusProxy) && statusProxy == 1;
        bool isBodyOrClothes = vmatLower.Contains("body") || vmatLower.Contains("clothes") ||
                               vmatLower.Contains("jacket") || vmatLower.Contains("coat") ||
                               vmatLower.Contains("pants") || vmatLower.Contains("headbase") ||
                               vmatLower.Contains("skin") || vmatLower.Contains("chrono_v2.vmat");
        bool isDedicatedGlow = vmatLower.Contains("glow") || vmatLower.Contains("hourglass") ||
                               vmatLower.Contains("portal") || vmatLower.Contains("flame") ||
                               vmatLower.Contains("light") || vmatLower.Contains("beam") || isAdditive;

        bool hasSelfIllumFlag = (mat.IntParams.TryGetValue("F_SELF_ILLUM", out long selfIllum) && selfIllum == 1) ||
                                (mat.IntParams.TryGetValue("F_EMISSIVE", out long emissive) && emissive == 1);

        string selfIllumMaskPath = GetTextureParam(mat, "g_tSelfIllumMask") ??
                                   GetTextureParam(mat, "TextureSelfIllumMask");

        bool hasValidIllumMask = !string.IsNullOrEmpty(selfIllumMaskPath) &&
                                 !selfIllumMaskPath.ToLowerInvariant().Contains("default_mask");

        bool maskIsBlack = mat.VectorParams.TryGetValue("TextureSelfIllumMask1", out var tcMask) &&
                           tcMask.X <= 0.001f && tcMask.Y <= 0.001f && tcMask.Z <= 0.001f;

        float illumScale = 0.0f;
        if (mat.FloatParams.TryGetValue("g_flSelfIllumScale1", out float flScale1)) illumScale = flScale1;
        else if (mat.FloatParams.TryGetValue("g_flSelfIllumScale", out float flScale0)) illumScale = flScale0;
        bool hasDedicatedEmissiveBodyMask = hasValidIllumMask &&
            (selfIllumMaskPath.ToLowerInvariant().Contains("emissive") || vmatLower.Contains("inferno_body"));

        bool shouldEmit = hasSelfIllumFlag && illumScale > 0.001f && !maskIsBlack &&
                          (!isStatusProxy || isDedicatedGlow || hasDedicatedEmissiveBodyMask) &&
                          (!isBodyOrClothes || hasDedicatedEmissiveBodyMask);

        Color emissionColor = Colors.White;
        if (shouldEmit)
        {
            if (mat.VectorParams.TryGetValue("g_vSelfIllumTint1", out var illumVec1))
            {
                emissionColor = new Color(illumVec1.X, illumVec1.Y, illumVec1.Z);
            }
            else if (mat.VectorParams.TryGetValue("g_vSelfIllumTint", out var illumVec0))
            {
                emissionColor = new Color(illumVec0.X, illumVec0.Y, illumVec0.Z);
            }
            else if (mat.VectorParams.TryGetValue("TextureSelfIllumTint1", out var illumVecT))
            {
                emissionColor = new Color(illumVecT.X, illumVecT.Y, illumVecT.Z);
            }
        }

        return new SelfIllumParams(
            shouldEmit,
            emissionColor,
            illumScale,
            hasValidIllumMask ? selfIllumMaskPath : null
        );
    }

    private static string GetTextureParam(VrfMaterial mat, string paramName)
    {
        string path = null;
        if (mat.TextureParams.TryGetValue(paramName, out var p))
        {
            path = p;
        }
        else if (mat.Data != null)
        {
            var texArray = mat.Data.GetArray("m_textureParams");
            if (texArray != null)
            {
                foreach (var item in texArray)
                {
                    if (item.GetStringProperty("m_name")?.Equals(paramName, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        path = item.GetStringProperty("m_pValue");
                        break;
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(path))
        {
            if (path.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(9);
            }
            path = path.Trim('"', '\'', ' ');
        }
        return path;
    }
}
