using System;
using Godot;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using VrfMaterial = ValveResourceFormat.ResourceTypes.Material;

namespace DeadlockPlayground.Materials.Heroes;

/// <summary>
/// Hero material configuration for Lash.
/// Manages signature dynamic sparkles FX around his shoulders and shoulder/body safeguards.
/// </summary>
public class LashMaterialConfig : IHeroMaterialConfig
{
    public string HeroKey => "lash";
    public string DisplayName => "Lash Sparkles";

    public Color? SignatureGlowColor => new Color(0.85098f, 0.776471f, 0.219608f, 1.0f);

    public void ConfigureBaseMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D material)
    {
        string mLower = meshName?.ToLowerInvariant() ?? "";
        string vLower = vmatPath?.ToLowerInvariant() ?? "";

        if (mLower.Contains("sparkle") || vLower.Contains("sparkle"))
        {
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            material.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
            material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            material.AlbedoColor = SignatureGlowColor.Value;
        }
    }

    public ShaderMaterial GetSignatureMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D baseMat)
    {
        return null;
    }

    public Godot.Material TryCreateCustomMaterial(Package package, string vmatPath, string meshName)
    {
        string vmatLower = vmatPath?.ToLowerInvariant() ?? "";
        if (!vmatLower.Contains("lash_sparkles") && !(vmatLower.Contains("sparkle") && vmatLower.Contains("lash")))
        {
            return null;
        }

        var sparkleShader = Source2ShaderRegistry.GetLashSparklesShader();
        if (sparkleShader == null) return null;

        var shaderMat = new ShaderMaterial { Shader = sparkleShader };
        shaderMat.RenderPriority = 3;
        shaderMat.SetMeta("PreserveShading", true);

        // Load sparkle mask with raw RGBA (forceOpaque: false)
        var maskTex = Source2TextureLoader.LoadVtexTexture(package, "models/heroes_wip/lash/materials/lash_sparkles_mask_psd_c38399ee.vtex", forceOpaque: false)
                   ?? Source2TextureLoader.LoadVtexTexture(package, "lash_sparkles_mask_psd_c38399ee.vtex", forceOpaque: false);

        if (maskTex != null)
        {
            shaderMat.SetShaderParameter("sparkle_mask", maskTex);
        }

        Color tint = SignatureGlowColor.Value;
        float scale = 7.154f;
        Vector2 scrollSpeed = new Vector2(0.5f, 0.0f);

        shaderMat.SetShaderParameter("sparkle_tint", tint);
        shaderMat.SetShaderParameter("emission_scale", scale);
        shaderMat.SetShaderParameter("scroll_speed", scrollSpeed);
        shaderMat.SetShaderParameter("sweep_speed", 0.5f);

        return shaderMat;
    }

    public bool ShouldPreserveMaterial(string meshName, string vmatPath, Godot.Material material)
    {
        string mLower = meshName?.ToLowerInvariant() ?? "";
        string vLower = vmatPath?.ToLowerInvariant() ?? "";
        return mLower.Contains("sparkle") || vLower.Contains("sparkle");
    }

    public Control BuildUI(Action<string, Variant> onParameterChanged)
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);

        // Sparkle Tint
        var rowColor = new HBoxContainer();
        var lblColor = new Label { Text = "Sparkles Tint:", CustomMinimumSize = new Vector2(130, 0) };
        var cpColor = new ColorPickerButton { Color = new Color(0.85098f, 0.776471f, 0.219608f, 1.0f), CustomMinimumSize = new Vector2(60, 26) };
        rowColor.AddChild(lblColor);
        rowColor.AddChild(cpColor);
        vbox.AddChild(rowColor);

        // Sparkle Radiant Scale
        var rowScale = new HBoxContainer();
        var lblScale = new Label { Text = "Radiant Brightness:", CustomMinimumSize = new Vector2(130, 0) };
        var sliderScale = new HSlider { MinValue = 1.0, MaxValue = 15.0, Step = 0.1, Value = 7.15, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var valScale = new Label { Text = "7.15", CustomMinimumSize = new Vector2(45, 0) };
        rowScale.AddChild(lblScale);
        rowScale.AddChild(sliderScale);
        rowScale.AddChild(valScale);
        vbox.AddChild(rowScale);

        // Sparkle Scroll Speed X (Source 2 Self-Illum Scroll Speed 1)
        var rowSpeed = new HBoxContainer();
        var lblSpeed = new Label { Text = "Scroll Speed X:", CustomMinimumSize = new Vector2(130, 0) };
        var sliderSpeed = new HSlider { MinValue = 0.0, MaxValue = 2.0, Step = 0.05, Value = 0.50, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var valSpeed = new Label { Text = "0.50", CustomMinimumSize = new Vector2(45, 0) };
        rowSpeed.AddChild(lblSpeed);
        rowSpeed.AddChild(sliderSpeed);
        rowSpeed.AddChild(valSpeed);
        vbox.AddChild(rowSpeed);

        // Events
        cpColor.ColorChanged += col =>
        {
            onParameterChanged?.Invoke("sparkle_tint", col);
            onParameterChanged?.Invoke("self_illum_tint", col);
            onParameterChanged?.Invoke("color_tint", col);
        };

        sliderScale.ValueChanged += v =>
        {
            valScale.Text = v.ToString("F2");
            onParameterChanged?.Invoke("emission_scale", (float)v);
            onParameterChanged?.Invoke("self_illum_scale", (float)v);
        };

        sliderSpeed.ValueChanged += v =>
        {
            valSpeed.Text = v.ToString("F2");
            onParameterChanged?.Invoke("sweep_speed", (float)v);
            onParameterChanged?.Invoke("scroll_speed", new Vector2((float)v, 0.0f));
            onParameterChanged?.Invoke("self_illum_scroll_speed", new Vector2((float)v, 0.0f));
        };

        return vbox;
    }
}
