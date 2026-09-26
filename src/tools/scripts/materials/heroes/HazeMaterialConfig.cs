using System;
using System.IO;
using System.Collections.Generic;
using Godot;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using VrfMaterial = ValveResourceFormat.ResourceTypes.Material;

namespace DeadlockPlayground.Materials.Heroes;

/// <summary>
/// Hero material configuration for Haze (haze.vmdl_c).
/// Coordinates the head smoke effect (hazev2_headsmoke) using authentic Source 2 parameters:
/// - Mesh vertex color tinting (g_bMaskVertexColorTint1 = 1)
/// - Material color tinting from VMAT (g_bMaskColorTint1 = 1, g_vColorTint1)
/// - Authentic alpha test reference and view angle power from VMAT
/// - Continuous wave jitter masked at scalp root (texture_jitter_mask)
/// - Upward UV scrolling smoke flow (g_vAlbedoScrollSpeed1)
/// </summary>
public class HazeMaterialConfig : IHeroMaterialConfig
{
    public string HeroKey => "haze";
    public string DisplayName => "Haze Smoke & Stealth";

    public Color? SignatureGlowColor => null;

    public void ConfigureBaseMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D material)
    {
        string mLower = meshName?.ToLowerInvariant() ?? "";
        string vLower = vmatPath?.ToLowerInvariant() ?? "";

        if (mLower.Contains("smoke") || vLower.Contains("smoke") ||
            mLower.Contains("headsmoke") || vLower.Contains("headsmoke"))
        {
            material.AlbedoColor = Colors.Black;
            material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            material.AlphaScissorThreshold = 0.10f;
            material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            material.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always;
            material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        }
    }

    public ShaderMaterial GetSignatureMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D baseMat)
    {
        string mLower = meshName?.ToLowerInvariant() ?? "";
        string vLower = vmatPath?.ToLowerInvariant() ?? "";

        if (mLower.Contains("smoke") || vLower.Contains("smoke") ||
            mLower.Contains("headsmoke") || vLower.Contains("headsmoke"))
        {
            return CreateHeadsmokeMaterial(null, vmatPath, meshName) as ShaderMaterial;
        }

        return null;
    }

    public Godot.Material TryCreateCustomMaterial(Package package, string vmatPath, string meshName)
    {
        return TryCreateCustomMaterial(package, vmatPath, meshName, null);
    }

    public Godot.Material TryCreateCustomMaterial(Package package, string vmatPath, string meshName, Package addonPackage)
    {
        string mLower = meshName?.ToLowerInvariant() ?? "";
        string vLower = vmatPath?.ToLowerInvariant() ?? "";

        if (mLower.Contains("smoke") || vLower.Contains("smoke") ||
            mLower.Contains("headsmoke") || vLower.Contains("headsmoke"))
        {
            return CreateHeadsmokeMaterial(package, vmatPath, meshName, addonPackage);
        }

        return null;
    }

    public static Godot.Material CreateHeadsmokeMaterial(Package package, string vmatPath, string meshName, Package addonPackage = null)
    {
        var shader = Source2ShaderRegistry.GetHazeHeadsmokeShader();
        if (shader == null)
        {
            GD.PrintErr("[HazeMaterialConfig] source2_haze_headsmoke.gdshader could not be loaded.");
            return null;
        }

        var shaderMat = new ShaderMaterial { Shader = shader };
        shaderMat.ResourceName = string.IsNullOrEmpty(vmatPath) ? "hazev2_headsmoke" : vmatPath;
        shaderMat.SetMeta("PreserveShading", true);
        if (!string.IsNullOrEmpty(vmatPath))
        {
            shaderMat.SetMeta("OriginalVmatPath", vmatPath);
        }

        VrfMaterial vrfMat = TryReadVmat(package, vmatPath, addonPackage);

        // 1. Color Parameters from VMAT (Authentic VMAT extraction - no hardcoded colors)
        Color colorTint = Colors.Black;
        bool maskColorTint = true;
        bool useVertexColor = true;

        if (vrfMat != null)
        {
            Color? texColor = null;
            if (vrfMat.VectorParams.TryGetValue("TextureColor1", out var tc1)) texColor = new Color(tc1.X, tc1.Y, tc1.Z, 1.0f);
            else if (vrfMat.VectorParams.TryGetValue("TextureColor", out var tc0)) texColor = new Color(tc0.X, tc0.Y, tc0.Z, 1.0f);

            Color? tintColor = null;
            if (vrfMat.VectorParams.TryGetValue("g_vColorTint1", out var ct1)) tintColor = new Color(ct1.X, ct1.Y, ct1.Z, 1.0f);
            else if (vrfMat.VectorParams.TryGetValue("g_vColorTint", out var ct0)) tintColor = new Color(ct0.X, ct0.Y, ct0.Z, 1.0f);
            else if (vrfMat.VectorParams.TryGetValue("m_vColorTint", out var ctM)) tintColor = new Color(ctM.X, ctM.Y, ctM.Z, 1.0f);
            else if (vrfMat.VectorParams.TryGetValue("g_vTintColor", out var ctG)) tintColor = new Color(ctG.X, ctG.Y, ctG.Z, 1.0f);
            else if (vrfMat.VectorParams.TryGetValue("Color", out var ctC)) tintColor = new Color(ctC.X, ctC.Y, ctC.Z, 1.0f);

            if (vrfMat.IntParams.TryGetValue("g_bMaskColorTint1", out long mct1)) maskColorTint = mct1 == 1;
            else if (vrfMat.IntParams.TryGetValue("g_bMaskColorTint", out long mct0)) maskColorTint = mct0 == 1;

            if (vrfMat.IntParams.TryGetValue("g_bMaskVertexColorTint1", out long mvct1)) useVertexColor = mvct1 == 1;
            else if (vrfMat.IntParams.TryGetValue("g_bMaskVertexColorTint", out long mvct0)) useVertexColor = mvct0 == 1;
            else if (vrfMat.IntParams.TryGetValue("F_VERTEX_COLOR", out long fvc)) useVertexColor = fvc == 1;

            // Valve Source 2 pbr.vfx: modulate TextureColor by ColorTint
            if (texColor.HasValue && tintColor.HasValue && maskColorTint)
            {
                var t = texColor.Value;
                var c = tintColor.Value;
                colorTint = new Color(t.R * c.R, t.G * c.G, t.B * c.B, 1.0f);
            }
            else if (texColor.HasValue)
            {
                colorTint = texColor.Value;
            }
            else if (tintColor.HasValue)
            {
                colorTint = tintColor.Value;
            }
        }

        shaderMat.SetShaderParameter("color_tint", colorTint);
        shaderMat.SetShaderParameter("mask_color_tint", maskColorTint);
        shaderMat.SetShaderParameter("use_vertex_color", useVertexColor);

        // 2. Alpha Testing & Opacity from VMAT
        float alphaRef = 0.10f;
        float alphaAnglePower = -0.176f;
        float opacityScale = 1.0f;

        if (vrfMat != null)
        {
            if (vrfMat.FloatParams.TryGetValue("g_flAlphaTestReference1", out var ar1)) alphaRef = ar1;
            else if (vrfMat.FloatParams.TryGetValue("g_flAlphaTestReference", out var ar0)) alphaRef = ar0;

            if (vrfMat.FloatParams.TryGetValue("g_flAlphaAnglePower1", out var aap1)) alphaAnglePower = aap1;
            else if (vrfMat.FloatParams.TryGetValue("g_flAlphaAnglePower", out var aap0)) alphaAnglePower = aap0;

            if (vrfMat.FloatParams.TryGetValue("g_flOpacityScale1", out var op1)) opacityScale = op1;
            else if (vrfMat.FloatParams.TryGetValue("g_flOpacityScale", out var op0)) opacityScale = op0;
        }

        shaderMat.SetShaderParameter("alpha_test_threshold", alphaRef);
        shaderMat.SetShaderParameter("alpha_angle_power", alphaAnglePower);
        shaderMat.SetShaderParameter("opacity_scale", opacityScale);

        // 3. UV Scrolling & Transforms from VMAT
        Vector2 albScrollSpeed = new Vector2(0.10f, 0.30f);
        Vector2 uvScale = Vector2.One;
        Vector2 uvOffset = Vector2.Zero;
        float scrollQuantize = 0.0f;

        if (vrfMat != null)
        {
            if (vrfMat.VectorParams.TryGetValue("g_vAlbedoScrollSpeed1", out var as1)) albScrollSpeed = new Vector2(as1.X, as1.Y);
            else if (vrfMat.VectorParams.TryGetValue("g_vAlbedoScrollSpeed", out var as0)) albScrollSpeed = new Vector2(as0.X, as0.Y);

            if (vrfMat.VectorParams.TryGetValue("g_vAlbedoTexcoordScale1", out var us1)) uvScale = new Vector2(us1.X, us1.Y);
            else if (vrfMat.VectorParams.TryGetValue("g_vAlbedoTexcoordScale", out var us0)) uvScale = new Vector2(us0.X, us0.Y);

            if (vrfMat.VectorParams.TryGetValue("g_vAlbedoTexcoordOffset1", out var uo1)) uvOffset = new Vector2(uo1.X, uo1.Y);
            else if (vrfMat.VectorParams.TryGetValue("g_vAlbedoTexcoordOffset", out var uo0)) uvOffset = new Vector2(uo0.X, uo0.Y);

            if (vrfMat.FloatParams.TryGetValue("g_flAlbedoScrollQuantize1", out var sq1)) scrollQuantize = sq1;
            else if (vrfMat.FloatParams.TryGetValue("g_flAlbedoScrollQuantize", out var sq0)) scrollQuantize = sq0;
        }

        shaderMat.SetShaderParameter("albedo_scroll_speed", albScrollSpeed);
        shaderMat.SetShaderParameter("albedo_uv_scale", uvScale);
        shaderMat.SetShaderParameter("albedo_uv_offset", uvOffset);
        shaderMat.SetShaderParameter("albedo_scroll_quantize", scrollQuantize);

        // 4. Jitter parameters from VMAT
        bool enableJitter = true;
        float jSpeedA = 0.2f;
        float jSpeedB = 0.5f;
        Vector3 jAmpY = new Vector3(0.0f, 1.0f, 0.0f);
        Vector3 jAmpZ = new Vector3(1.0f, 1.0f, 2.0f);
        Vector3 jFreqA = new Vector3(0.04f, 0.04f, 0.16f);
        Vector3 jFreqB = new Vector3(0.05f, 0.09f, 0.17f);

        if (vrfMat != null)
        {
            if (vrfMat.IntParams.TryGetValue("F_JITTER_VERTICES", out long jv)) enableJitter = jv == 1;
            if (vrfMat.FloatParams.TryGetValue("g_flJitterSpeedA1", out var jsa1)) jSpeedA = jsa1;
            if (vrfMat.FloatParams.TryGetValue("g_flJitterSpeedB1", out var jsb1)) jSpeedB = jsb1;

            if (vrfMat.VectorParams.TryGetValue("g_vJitterAmplitudesYA1", out var jya)) jAmpY = new Vector3(jya.X, jya.Y, jya.Z);
            if (vrfMat.VectorParams.TryGetValue("g_vJitterAmplitudesZA1", out var jza)) jAmpZ = new Vector3(jza.X, jza.Y, jza.Z);

            if (vrfMat.VectorParams.TryGetValue("g_vJitterFrequenciesA1", out var jfa)) jFreqA = new Vector3(jfa.X, jfa.Y, jfa.Z);
            if (vrfMat.VectorParams.TryGetValue("g_vJitterFrequenciesB1", out var jfb)) jFreqB = new Vector3(jfb.X, jfb.Y, jfb.Z);
        }

        shaderMat.SetShaderParameter("enable_jitter", enableJitter);
        shaderMat.SetShaderParameter("jitter_speed_a", jSpeedA);
        shaderMat.SetShaderParameter("jitter_speed_b", jSpeedB);
        shaderMat.SetShaderParameter("jitter_amp_y", jAmpY);
        shaderMat.SetShaderParameter("jitter_amp_z", jAmpZ);
        shaderMat.SetShaderParameter("jitter_freq_a", jFreqA);
        shaderMat.SetShaderParameter("jitter_freq_b", jFreqB);

        // 5. Textures from VMAT
        ImageTexture smokeTex = null;
        if (vrfMat != null)
        {
            string colParam = Source2TextureLoader.GetTextureParam(vrfMat, "g_tColor")
                           ?? Source2TextureLoader.GetTextureParam(vrfMat, "TextureTranslucency1")
                           ?? Source2TextureLoader.GetTextureParam(vrfMat, "TextureTranslucency")
                           ?? Source2TextureLoader.GetTextureParam(vrfMat, "TextureColor");
            if (!string.IsNullOrEmpty(colParam))
            {
                smokeTex = Source2TextureLoader.GetOrLoadTexture(package, colParam, forceOpaque: false, addonPackage: addonPackage);
            }
        }

        if (smokeTex == null)
        {
            smokeTex = Source2TextureLoader.GetOrLoadTexture(package, "models/heroes_staging/haze/materials/hazev2_headsmoke_opacity_psd_eb1a2365.vtex", forceOpaque: false, addonPackage: addonPackage)
                    ?? Source2TextureLoader.GetOrLoadTexture(package, "models/heroes_staging/haze/materials/hazev2_headsmoke_opacity_png_8de31f38.vtex", forceOpaque: false, addonPackage: addonPackage)
                    ?? Source2TextureLoader.GetOrLoadTexture(package, "hazev2_headsmoke_opacity.vtex", forceOpaque: false, addonPackage: addonPackage);
        }

        if (smokeTex != null)
        {
            shaderMat.SetShaderParameter("texture_albedo", smokeTex);
        }

        ImageTexture normalTex = null;
        if (vrfMat != null)
        {
            string nrParam = Source2TextureLoader.GetTextureParam(vrfMat, "g_tNormalRoughness")
                          ?? Source2TextureLoader.GetTextureParam(vrfMat, "TextureNormal1")
                          ?? Source2TextureLoader.GetTextureParam(vrfMat, "TextureNormal");
            if (!string.IsNullOrEmpty(nrParam))
            {
                normalTex = Source2TextureLoader.GetOrLoadTexture(package, nrParam, forceOpaque: false, addonPackage: addonPackage);
            }
        }

        if (normalTex == null)
        {
            normalTex = Source2TextureLoader.GetOrLoadTexture(package, "models/heroes_staging/haze/materials/hazev2_headsmoke_vmat_g_tnormalroughness_53b47c48.vtex", forceOpaque: false, addonPackage: addonPackage)
                     ?? Source2TextureLoader.GetOrLoadTexture(package, "models/heroes_staging/haze/materials/hazev2_headsmoke_invis_vmat_g_tnormalroughness_53b47c48.vtex", forceOpaque: false, addonPackage: addonPackage);
        }

        if (normalTex != null)
        {
            shaderMat.SetShaderParameter("texture_normal_roughness", normalTex);
        }

        ImageTexture jitterMaskTex = null;
        if (vrfMat != null)
        {
            string jmParam = Source2TextureLoader.GetTextureParam(vrfMat, "g_tJitterMask")
                          ?? Source2TextureLoader.GetTextureParam(vrfMat, "TextureJitterMask");
            if (!string.IsNullOrEmpty(jmParam))
            {
                jitterMaskTex = Source2TextureLoader.GetOrLoadTexture(package, jmParam, forceOpaque: true, addonPackage: addonPackage);
            }
        }

        if (jitterMaskTex == null)
        {
            jitterMaskTex = Source2TextureLoader.GetOrLoadTexture(package, "models/npc/ghosts_passive/materials/gradient_trans_psd_7d2ef6aa.vtex", forceOpaque: true, addonPackage: addonPackage);
        }

        if (jitterMaskTex != null)
        {
            shaderMat.SetShaderParameter("texture_jitter_mask", jitterMaskTex);
        }

        GD.Print($"  [HazeHeadsmoke] Built custom head smoke ShaderMaterial for '{meshName}' (smokeTex: {smokeTex != null}, normalTex: {normalTex != null}, jitterMask: {jitterMaskTex != null}, tint: {colorTint}, alphaRef: {alphaRef})");
        return shaderMat;
    }

    private static VrfMaterial TryReadVmat(Package package, string vmatPath, Package addonPackage = null)
        => Source2MaterialHelper.TryReadVmat(package, vmatPath, addonPackage);

    public bool ShouldPreserveMaterial(string meshName, string vmatPath, Godot.Material material)
    {
        string mLower = meshName?.ToLowerInvariant() ?? "";
        string vLower = vmatPath?.ToLowerInvariant() ?? "";

        if (mLower.Contains("smoke") || vLower.Contains("smoke") ||
            mLower.Contains("headsmoke") || vLower.Contains("headsmoke"))
        {
            return true;
        }

        return false;
    }

    public Control BuildUI(Action<string, Variant> onParameterChanged)
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);

        // 1. Smoke Color Tint
        var rowColor = new HBoxContainer();
        var lblColor = new Label { Text = "Smoke Color:", CustomMinimumSize = new Vector2(130, 0) };
        var cpColor = new ColorPickerButton { Color = Colors.Black, CustomMinimumSize = new Vector2(60, 26) };
        rowColor.AddChild(lblColor);
        rowColor.AddChild(cpColor);
        vbox.AddChild(rowColor);

        // 2. Smoke Opacity
        var rowDense = new HBoxContainer();
        var lblDense = new Label { Text = "Smoke Opacity:", CustomMinimumSize = new Vector2(130, 0) };
        var sliderDense = new HSlider { MinValue = 0.5, MaxValue = 3.5, Step = 0.1, Value = 1.0, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var valDense = new Label { Text = "1.00", CustomMinimumSize = new Vector2(45, 0) };
        rowDense.AddChild(lblDense);
        rowDense.AddChild(sliderDense);
        rowDense.AddChild(valDense);
        vbox.AddChild(rowDense);

        // 3. Smoke Flow Speed
        var rowSpeed = new HBoxContainer();
        var lblSpeed = new Label { Text = "Flow Speed:", CustomMinimumSize = new Vector2(130, 0) };
        var sliderSpeed = new HSlider { MinValue = 0.2, MaxValue = 3.0, Step = 0.1, Value = 1.0, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var valSpeed = new Label { Text = "1.00", CustomMinimumSize = new Vector2(45, 0) };
        rowSpeed.AddChild(lblSpeed);
        rowSpeed.AddChild(sliderSpeed);
        rowSpeed.AddChild(valSpeed);
        vbox.AddChild(rowSpeed);

        // Wire event handlers
        cpColor.ColorChanged += col =>
        {
            onParameterChanged?.Invoke("color_tint", col);
        };

        sliderDense.ValueChanged += v =>
        {
            valDense.Text = v.ToString("F2");
            onParameterChanged?.Invoke("opacity_scale", (float)v);
        };

        sliderSpeed.ValueChanged += v =>
        {
            valSpeed.Text = v.ToString("F2");
            float factor = (float)v;
            onParameterChanged?.Invoke("albedo_scroll_speed", new Vector2(0.10f * factor, 0.30f * factor));
        };

        return vbox;
    }
}
