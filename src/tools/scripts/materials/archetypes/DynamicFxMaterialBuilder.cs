using System;
using System.Collections.Generic;
using Godot;
using SteamDatabase.ValvePak;
using ValveResourceFormat.ResourceTypes;
using VrfMaterial = ValveResourceFormat.ResourceTypes.Material;

namespace DeadlockPlayground.Materials.Archetypes;

/// <summary>
/// Builds ShaderMaterial instances using Source 2 procedural dynamic effect shaders:
/// source2_flame_hair, source2_dynamic_fx_add, source2_dynamic_jitter, and source2_dynamic_fx.
/// </summary>
public static class DynamicFxMaterialBuilder
{
    public static bool IsApplicable(VrfMaterial matResource, string vmatPath)
    {
        if (matResource == null) return false;
        string vmatLower = vmatPath?.ToLowerInvariant() ?? "";

        // Never hijack solid NPR vertex-colored character surfaces (e.g. vindicta_head arms/legs, wraith_head).
        // Additive FX layers (vindicta_glow) declare F_ADDITIVE_BLEND = 1 and will still be processed.
        if (matResource.IntParams.GetValueOrDefault("F_VERTEX_COLOR", 0) == 1 &&
            matResource.IntParams.GetValueOrDefault("F_ADDITIVE_BLEND", 0) == 0 &&
            !vmatLower.Contains("jitter") && !vmatLower.Contains("glow") && !vmatLower.Contains("flame") && !vmatLower.Contains("sparkle"))
        {
            return false;
        }

        bool hasJitter = matResource.IntParams.GetValueOrDefault("F_JITTER_VERTICES", 0) == 1;
        bool hasTexTransform = matResource.IntParams.GetValueOrDefault("F_ENABLE_TEXTURE_TRANSFORMS", 0) == 1;
        bool isFxAdditive = matResource.IntParams.GetValueOrDefault("F_ADDITIVE_BLEND", 0) == 1;

        bool hasScroll = (matResource.VectorParams.TryGetValue("g_vAlbedoScrollSpeed1", out var albScroll) && (albScroll.X != 0 || albScroll.Y != 0))
                      || (matResource.VectorParams.TryGetValue("g_vAlbedoScrollSpeed", out var albScroll0) && (albScroll0.X != 0 || albScroll0.Y != 0))
                      || (matResource.VectorParams.TryGetValue("g_vSelfIllumScrollSpeed1", out var siScroll) && (siScroll.X != 0 || siScroll.Y != 0))
                      || (matResource.VectorParams.TryGetValue("g_vSelfIllumScrollSpeed", out var siScroll0) && (siScroll0.X != 0 || siScroll0.Y != 0))
                      || (matResource.VectorParams.TryGetValue("g_vTranslucentScrollSpeed0_1", out var trScroll) && (trScroll.X != 0 || trScroll.Y != 0))
                      || (matResource.VectorParams.TryGetValue("g_vTranslucentScrollSpeed1", out var trScroll1) && (trScroll1.X != 0 || trScroll1.Y != 0));

        bool isDedicatedFxName = vmatLower.Contains("jitter") || vmatLower.Contains("armglow") ||
                                 vmatLower.Contains("vindicta_glow") || vmatLower.Contains("hornet_glow") ||
                                 vmatLower.Contains("inferno_armglow") || vmatLower.Contains("flame") ||
                                 vmatLower.Contains("headglow") || vmatLower.Contains("headsmoke") || vmatLower.Contains("smoke") ||
                                 vmatLower.Contains("sparkle") || vmatLower.Contains("sparkles") ||
                                 vmatLower.Contains("lash_sparkles") ||
                                 vmatLower.Contains("geist_arm") || vmatLower.Contains("ghost2_arm");

        return hasJitter || hasScroll || (hasTexTransform && (hasScroll || hasJitter)) ||
               (isFxAdditive && (hasJitter || hasScroll || isDedicatedFxName)) ||
               isDedicatedFxName || VmatColorExtractor.IsDynamicGlow(matResource, vmatPath);
    }

    public static ShaderMaterial Build(Package package, VrfMaterial matResource, string vmatPath, Package addonPackage = null)
    {
        string vmatLower = vmatPath?.ToLowerInvariant() ?? "";

        bool hasJitter = matResource.IntParams.GetValueOrDefault("F_JITTER_VERTICES", 0) == 1;
        bool isFxAdditive = matResource.IntParams.GetValueOrDefault("F_ADDITIVE_BLEND", 0) == 1;
        bool isFxAlphaTest = matResource.IntParams.GetValueOrDefault("F_ALPHA_TEST", 0) == 1;

        bool isHeadGlow = vmatLower.Contains("headglow") || vmatLower.Contains("flame_hair");
        bool isHeadSmoke = vmatLower.Contains("headsmoke") || vmatLower.Contains("smoke");
        bool isArmGlow = vmatLower.Contains("armglow") || vmatLower.Contains("flame_arm") || (vmatLower.Contains("inferno_flames") && !isHeadGlow);
        bool isSparkle = vmatLower.Contains("sparkle") || vmatLower.Contains("lash_sparkles");

        Shader fxShader;
        if (isHeadSmoke)
        {
            fxShader = Source2ShaderRegistry.GetHazeHeadsmokeShader();
        }
        else if (isHeadGlow)
        {
            fxShader = Source2ShaderRegistry.GetFlameHairShader();
        }
        else if (isArmGlow || isFxAdditive || isSparkle)
        {
            fxShader = Source2ShaderRegistry.GetDynamicFxShader(true);
        }
        else if (hasJitter || vmatLower.Contains("jitter"))
        {
            fxShader = Source2ShaderRegistry.GetDynamicJitterShader();
        }
        else
        {
            fxShader = Source2ShaderRegistry.GetDynamicFxShader(false);
        }

        if (fxShader == null) return null;

        var shaderMat = new ShaderMaterial { Shader = fxShader };

        // 1. Pass blend modes & alpha test
        shaderMat.SetShaderParameter("is_additive", isArmGlow || isFxAdditive || isSparkle);
        shaderMat.SetShaderParameter("is_sparkle", isSparkle);
        shaderMat.SetShaderParameter("is_alpha_test", isFxAlphaTest);
        shaderMat.SetShaderParameter("is_arm_glow", isArmGlow);
        bool isTranslucent = matResource.IntParams.GetValueOrDefault("F_TRANSLUCENT", 0) == 1;
        shaderMat.SetShaderParameter("is_translucent", isTranslucent);
        bool useVertexColor = matResource.IntParams.GetValueOrDefault("g_bMaskVertexColorTint1", 0) == 1
                           || matResource.IntParams.GetValueOrDefault("g_bMaskVertexColorTint", 0) == 1
                           || matResource.IntParams.GetValueOrDefault("F_VERTEX_COLOR", 0) == 1;
        shaderMat.SetShaderParameter("use_vertex_color", useVertexColor);

        float alphaRef = 0.1f;
        if (matResource.FloatParams.TryGetValue("g_flAlphaTestReference1", out var aRef1)) alphaRef = aRef1;
        else if (matResource.FloatParams.TryGetValue("g_flAlphaTestReference", out var aRef0)) alphaRef = aRef0;
        shaderMat.SetShaderParameter("alpha_test_threshold", alphaRef);

        // 2. Pass Jitter parameters
        shaderMat.SetShaderParameter("enable_jitter", hasJitter);
        if (hasJitter)
        {
            float jitterSpeedA = 1.0f;
            if (matResource.FloatParams.TryGetValue("g_flJitterSpeedA1", out var jsa1)) jitterSpeedA = jsa1;
            else if (matResource.FloatParams.TryGetValue("g_flJitterSpeedA", out var jsa0)) jitterSpeedA = jsa0;
            shaderMat.SetShaderParameter("jitter_speed_a", jitterSpeedA);

            float jitterSpeedB = 1.0f;
            if (matResource.FloatParams.TryGetValue("g_flJitterSpeedB1", out var jsb1)) jitterSpeedB = jsb1;
            else if (matResource.FloatParams.TryGetValue("g_flJitterSpeedB", out var jsb0)) jitterSpeedB = jsb0;
            shaderMat.SetShaderParameter("jitter_speed_b", jitterSpeedB);

            float qTime = 0.0f;
            if (matResource.FloatParams.TryGetValue("g_flJitterQuantizeTime1", out var qt1)) qTime = qt1;
            else if (matResource.FloatParams.TryGetValue("g_flJitterQuantizeTime", out var qt0)) qTime = qt0;
            shaderMat.SetShaderParameter("jitter_quantize_time_1", qTime);

            float qDisp = 0.0f;
            if (matResource.FloatParams.TryGetValue("g_flJitterQuantizeDisplacement1", out var qd1)) qDisp = qd1;
            else if (matResource.FloatParams.TryGetValue("g_flJitterQuantizeDisplacement", out var qd0)) qDisp = qd0;
            shaderMat.SetShaderParameter("jitter_quantize_disp_1", qDisp);

            if (matResource.VectorParams.TryGetValue("g_vJitterExpression1", out var je1))
                shaderMat.SetShaderParameter("jitter_expression", new Vector3(je1.X, je1.Y, je1.Z));
            else if (matResource.VectorParams.TryGetValue("g_vJitterExpression", out var je0))
                shaderMat.SetShaderParameter("jitter_expression", new Vector3(je0.X, je0.Y, je0.Z));

            System.Numerics.Vector4 jxa = default;
            if (matResource.VectorParams.TryGetValue("g_vJitterAmplitudesXA1", out jxa) || matResource.VectorParams.TryGetValue("g_vJitterAmplitudesXA", out jxa))
                shaderMat.SetShaderParameter("jitter_amp_x_a", new Vector3(jxa.X, jxa.Y, jxa.Z));
            System.Numerics.Vector4 jya = default;
            if (matResource.VectorParams.TryGetValue("g_vJitterAmplitudesYA1", out jya) || matResource.VectorParams.TryGetValue("g_vJitterAmplitudesYA", out jya))
                shaderMat.SetShaderParameter("jitter_amp_y_a", new Vector3(jya.X, jya.Y, jya.Z));
            System.Numerics.Vector4 jza = default;
            if (matResource.VectorParams.TryGetValue("g_vJitterAmplitudesZA1", out jza) || matResource.VectorParams.TryGetValue("g_vJitterAmplitudesZA", out jza))
                shaderMat.SetShaderParameter("jitter_amp_z_a", new Vector3(jza.X, jza.Y, jza.Z));

            System.Numerics.Vector4 jxb = default;
            if (matResource.VectorParams.TryGetValue("g_vJitterAmplitudesXB1", out jxb) || matResource.VectorParams.TryGetValue("g_vJitterAmplitudesXB", out jxb))
                shaderMat.SetShaderParameter("jitter_amp_x_b", new Vector3(jxb.X, jxb.Y, jxb.Z));
            System.Numerics.Vector4 jyb = default;
            if (matResource.VectorParams.TryGetValue("g_vJitterAmplitudesYB1", out jyb) || matResource.VectorParams.TryGetValue("g_vJitterAmplitudesYB", out jyb))
                shaderMat.SetShaderParameter("jitter_amp_y_b", new Vector3(jyb.X, jyb.Y, jyb.Z));
            System.Numerics.Vector4 jzb = default;
            if (matResource.VectorParams.TryGetValue("g_vJitterAmplitudesZB1", out jzb) || matResource.VectorParams.TryGetValue("g_vJitterAmplitudesZB", out jzb))
                shaderMat.SetShaderParameter("jitter_amp_z_b", new Vector3(jzb.X, jzb.Y, jzb.Z));

            System.Numerics.Vector4 jfa = default;
            if (matResource.VectorParams.TryGetValue("g_vJitterFrequenciesA1", out jfa) || matResource.VectorParams.TryGetValue("g_vJitterFrequenciesA", out jfa))
                shaderMat.SetShaderParameter("jitter_freq_a", new Vector3(jfa.X, jfa.Y, jfa.Z));
            System.Numerics.Vector4 jfb = default;
            if (matResource.VectorParams.TryGetValue("g_vJitterFrequenciesB1", out jfb) || matResource.VectorParams.TryGetValue("g_vJitterFrequenciesB", out jfb))
                shaderMat.SetShaderParameter("jitter_freq_b", new Vector3(jfb.X, jfb.Y, jfb.Z));

            // Legacy fallbacks
            shaderMat.SetShaderParameter("jitter_amp_x", new Vector3(jxa.X, jxa.Y, jxb.Z));
            shaderMat.SetShaderParameter("jitter_amp_y", new Vector3(jya.X, jya.Y, jyb.Z));
            shaderMat.SetShaderParameter("jitter_amp_z", new Vector3(jza.X, jza.Y, jzb.Z));
        }

        // 3. Pass UV Scrolling & Transforms
        Vector2 albScrollSpeed = Vector2.Zero;
        if (matResource.VectorParams.TryGetValue("g_vAlbedoScrollSpeed1", out var as1)) albScrollSpeed = new Vector2(as1.X, as1.Y);
        else if (matResource.VectorParams.TryGetValue("g_vAlbedoScrollSpeed", out var as0)) albScrollSpeed = new Vector2(as0.X, as0.Y);
        shaderMat.SetShaderParameter("albedo_scroll_speed", albScrollSpeed);

        Vector2 siScrollSpeed = Vector2.Zero;
        if (matResource.VectorParams.TryGetValue("g_vSelfIllumScrollSpeed1", out var ss1)) siScrollSpeed = new Vector2(ss1.X, ss1.Y);
        else if (matResource.VectorParams.TryGetValue("g_vSelfIllumScrollSpeed", out var ss0)) siScrollSpeed = new Vector2(ss0.X, ss0.Y);
        shaderMat.SetShaderParameter("self_illum_scroll_speed", siScrollSpeed);

        Vector2 tr0ScrollSpeed = Vector2.Zero;
        if (matResource.VectorParams.TryGetValue("g_vTranslucentScrollSpeed0_1", out var ts0_1)) tr0ScrollSpeed = new Vector2(ts0_1.X, ts0_1.Y);
        else if (matResource.VectorParams.TryGetValue("g_vTranslucentScrollSpeed0", out var ts0_0)) tr0ScrollSpeed = new Vector2(ts0_0.X, ts0_0.Y);
        shaderMat.SetShaderParameter("translucent_scroll_speed_0", tr0ScrollSpeed);

        Vector2 tr1ScrollSpeed = Vector2.Zero;
        if (matResource.VectorParams.TryGetValue("g_vTranslucentScrollSpeed1", out var ts1_1)) tr1ScrollSpeed = new Vector2(ts1_1.X, ts1_1.Y);
        else if (matResource.VectorParams.TryGetValue("g_vTranslucentScrollSpeed", out var ts1_0)) tr1ScrollSpeed = new Vector2(ts1_0.X, ts1_0.Y);
        shaderMat.SetShaderParameter("translucent_scroll_speed_1", tr1ScrollSpeed);

        Vector3 uvScale = Vector3.One;
        if (matResource.VectorParams.TryGetValue("g_vAlbedoTexcoordScale1", out var us1)) uvScale = new Vector3(us1.X, us1.Y, 1.0f);
        else if (matResource.VectorParams.TryGetValue("g_vAlbedoTexcoordScale", out var us0)) uvScale = new Vector3(us0.X, us0.Y, 1.0f);
        shaderMat.SetShaderParameter("uv_scale", uvScale);

        Vector3 uvOffset = Vector3.Zero;
        if (matResource.VectorParams.TryGetValue("g_vAlbedoTexcoordOffset1", out var uo1)) uvOffset = new Vector3(uo1.X, uo1.Y, 0.0f);
        else if (matResource.VectorParams.TryGetValue("g_vAlbedoTexcoordOffset", out var uo0)) uvOffset = new Vector3(uo0.X, uo0.Y, 0.0f);
        shaderMat.SetShaderParameter("uv_offset", uvOffset);

        // 4. Pass Self-Illum & Color
        bool hasSelfIllum = matResource.IntParams.GetValueOrDefault("F_SELF_ILLUM", 0) == 1
                         || isArmGlow || isHeadGlow || isSparkle;
        shaderMat.SetShaderParameter("enable_self_illum", hasSelfIllum);

        Color glowColor = Source2MaterialHelper.ExtractDynamicGlowColor(matResource, vmatPath, package);

        shaderMat.SetShaderParameter("self_illum_tint", glowColor);
        shaderMat.SetShaderParameter("glow_color", glowColor);

        float siScaleVal = 1.0f;
        if (matResource.FloatParams.TryGetValue("g_flSelfIllumScale1", out var sis1)) siScaleVal = sis1;
        else if (matResource.FloatParams.TryGetValue("g_flSelfIllumScale", out var sis0)) siScaleVal = sis0;
        else if (isHeadGlow)
            siScaleVal = 5.16f; // Valve inferno_headglow.vmat reference value
        else if ((vmatLower.Contains("inferno") || vmatLower.Contains("flame") || vmatLower.Contains("armglow")) && siScaleVal <= 1.0f)
            siScaleVal = 3.2f;
        else if (isSparkle && siScaleVal <= 1.0f)
            siScaleVal = 7.154f;

        shaderMat.SetShaderParameter("self_illum_scale", siScaleVal);

        float opacityScale = 1.0f;
        if (matResource.FloatParams.TryGetValue("g_flOpacityScale1", out var op1)) opacityScale = op1;
        else if (matResource.FloatParams.TryGetValue("g_flOpacityScale", out var op0)) opacityScale = op0;
        shaderMat.SetShaderParameter("opacity_scale", opacityScale);

        float fresnelExp = 0.0f;
        if (matResource.FloatParams.TryGetValue("g_flSelfIllumFresnelMaskExponent", out var fe1)) fresnelExp = fe1;
        else if (matResource.FloatParams.TryGetValue("g_flFresnelExponent", out var fe0)) fresnelExp = fe0;
        shaderMat.SetShaderParameter("self_illum_fresnel_exponent", fresnelExp);

        float alphaAnglePower = 0.0f;
        if (matResource.FloatParams.TryGetValue("g_flAlphaAnglePower1", out var aap1)) alphaAnglePower = aap1;
        else if (matResource.FloatParams.TryGetValue("g_flAlphaAnglePower", out var aap0)) alphaAnglePower = aap0;
        shaderMat.SetShaderParameter("alpha_angle_power", alphaAnglePower);

        float emissionBoost = isArmGlow ? 1.6f : (isHeadGlow ? 1.0f : (isSparkle ? 2.5f : 1.0f));
        shaderMat.SetShaderParameter("emission_boost", emissionBoost);

        // 5. Pass Color Tint
        Color baseTint = isHeadSmoke ? Colors.Black : Colors.White;
        Color? fxTexCol = null;
        if (matResource.VectorParams.TryGetValue("TextureColor1", out var tc1)) fxTexCol = new Color(tc1.X, tc1.Y, tc1.Z, 1.0f);
        else if (matResource.VectorParams.TryGetValue("TextureColor", out var tc0)) fxTexCol = new Color(tc0.X, tc0.Y, tc0.Z, 1.0f);

        Color? fxTintCol = null;
        if (matResource.VectorParams.TryGetValue("g_vColorTint1", out var ct1)) fxTintCol = new Color(ct1.X, ct1.Y, ct1.Z, 1.0f);
        else if (matResource.VectorParams.TryGetValue("g_vColorTint", out var ct0)) fxTintCol = new Color(ct0.X, ct0.Y, ct0.Z, 1.0f);
        else if (matResource.VectorParams.TryGetValue("m_vColorTint", out var ctM)) fxTintCol = new Color(ctM.X, ctM.Y, ctM.Z, 1.0f);
        else if (matResource.VectorParams.TryGetValue("g_vTintColor", out var ctG)) fxTintCol = new Color(ctG.X, ctG.Y, ctG.Z, 1.0f);
        else if (matResource.VectorParams.TryGetValue("Color", out var ctC)) fxTintCol = new Color(ctC.X, ctC.Y, ctC.Z, 1.0f);

        bool maskTint = matResource.IntParams.GetValueOrDefault("g_bMaskColorTint1", 0) == 1
                     || matResource.IntParams.GetValueOrDefault("g_bMaskColorTint", 0) == 1;

        if (fxTexCol.HasValue && fxTintCol.HasValue && maskTint)
        {
            var t = fxTexCol.Value;
            var c = fxTintCol.Value;
            baseTint = new Color(t.R * c.R, t.G * c.G, t.B * c.B, 1.0f);
        }
        else if (fxTexCol.HasValue)
        {
            baseTint = fxTexCol.Value;
        }
        else if (fxTintCol.HasValue)
        {
            baseTint = fxTintCol.Value;
        }

        shaderMat.SetShaderParameter("color_tint", baseTint);
        shaderMat.SetShaderParameter("mask_color_tint", maskTint);

        int tintMode = (int)matResource.IntParams.GetValueOrDefault("g_nTextureColorTintMode1",
                       matResource.IntParams.GetValueOrDefault("g_nTextureColorTintMode", 0));
        shaderMat.SetShaderParameter("texture_color_tint_mode", tintMode);

        // 6. Bind Textures
        string colorTexPath = Source2TextureLoader.GetTextureParam(matResource, "g_tColor")
                           ?? Source2TextureLoader.GetTextureParam(matResource, "TextureColor")
                           ?? Source2TextureLoader.GetTextureParam(matResource, "g_tColor1")
                           ?? Source2TextureLoader.GetTextureParam(matResource, "TextureTranslucency")
                           ?? Source2TextureLoader.GetTextureParam(matResource, "TextureTranslucency1");

        bool forceOpaque = !isArmGlow && !isFxAdditive && !isSparkle && !isHeadSmoke;
        if (!string.IsNullOrEmpty(colorTexPath))
        {
            var colorTex = Source2TextureLoader.GetOrLoadTexture(package, colorTexPath, forceOpaque: forceOpaque, addonPackage: addonPackage);
            if (colorTex != null)
            {
                shaderMat.SetShaderParameter("albedo_texture", colorTex);
                shaderMat.SetShaderParameter("texture_albedo", colorTex);
            }
        }

        Source2TextureLoader.BindTextureIfPresent(package, matResource, "g_tSelfIllumMask", shaderMat, "self_illum_mask", forceOpaque: true, addonPackage: addonPackage);
        Source2TextureLoader.BindTextureIfPresent(package, matResource, "g_tSelfIllumMask", shaderMat, "texture_self_illum_mask", forceOpaque: true, addonPackage: addonPackage);
        Source2TextureLoader.BindTextureIfPresent(package, matResource, "g_tJitterMask", shaderMat, "jitter_mask", forceOpaque: true, addonPackage: addonPackage);
        Source2TextureLoader.BindTextureIfPresent(package, matResource, "g_tJitterMask", shaderMat, "texture_jitter_mask", forceOpaque: true, addonPackage: addonPackage);
        Source2TextureLoader.BindTextureIfPresent(package, matResource, "g_tNormalRoughness", shaderMat, "texture_normal_roughness", forceOpaque: false, addonPackage: addonPackage);
        Source2TextureLoader.BindTextureIfPresent(package, matResource, "TextureNormal1", shaderMat, "texture_normal_roughness", forceOpaque: false, addonPackage: addonPackage);
        Source2TextureLoader.BindTextureIfPresent(package, matResource, "TextureNormal", shaderMat, "texture_normal_roughness", forceOpaque: false, addonPackage: addonPackage);
        Source2TextureLoader.BindTextureIfPresent(package, matResource, "g_tTintMaskRimLightMask", shaderMat, "tint_mask_rim_mask", forceOpaque: true, addonPackage: addonPackage);

        // 7. Render Priorities
        if (isSparkle)
        {
            shaderMat.RenderPriority = 3;
        }
        else if (isArmGlow || isFxAdditive)
        {
            shaderMat.RenderPriority = 2;
        }
        else if (isHeadGlow || isHeadSmoke)
        {
            shaderMat.RenderPriority = 0; // Solid depth-tested geometry
        }
        else if (hasJitter || vmatLower.Contains("jitter"))
        {
            shaderMat.RenderPriority = -1; // Behind character for foreground clarity
        }
        else if (isFxAlphaTest)
        {
            shaderMat.RenderPriority = 0;
        }

        return shaderMat;
    }
}
