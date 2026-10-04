using System;
using Godot;

namespace DeadlockPlayground.Materials.Heroes;

public class VindictaMaterialConfig : IHeroMaterialConfig
{
    public string HeroKey => "vindicta";
    public string DisplayName => "Vindicta";

    public Color? SignatureGlowColor => new Color(0.392157f, 0.709804f, 0.964706f, 1.0f); // #64B5F6 cyan

    private Shader _auraShader;

    public VindictaMaterialConfig()
    {
        if (ResourceLoader.Exists("res://assets/shaders/valve/vindicta.gdshader"))
            _auraShader = GD.Load<Shader>("res://assets/shaders/valve/vindicta.gdshader");
        else if (ResourceLoader.Exists("res://assets/shaders/vindicta.gdshader"))
            _auraShader = GD.Load<Shader>("res://assets/shaders/vindicta.gdshader");
        else if (ResourceLoader.Exists("res://assets/shaders/valve/source2_dynamic_glow.gdshader"))
            _auraShader = GD.Load<Shader>("res://assets/shaders/valve/source2_dynamic_glow.gdshader");
        else if (ResourceLoader.Exists("res://assets/shaders/source2_dynamic_glow.gdshader"))
            _auraShader = GD.Load<Shader>("res://assets/shaders/source2_dynamic_glow.gdshader");
    }

    public void ConfigureBaseMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D material)
    {
        // Base materials are accurately parsed and configured directly from VPK attributes by Source2MaterialHelper
    }

    public ShaderMaterial GetSignatureMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D baseMat)
    {
        return null;
    }

    public Godot.Material TryCreateCustomMaterial(SteamDatabase.ValvePak.Package package, string vmatPath, string meshName)
        => TryCreateCustomMaterial(package, vmatPath, meshName, null);

    public Godot.Material TryCreateCustomMaterial(SteamDatabase.ValvePak.Package package, string vmatPath, string meshName, SteamDatabase.ValvePak.Package addonPackage)
    {
        string mLower = meshName?.ToLowerInvariant() ?? "";
        string vLower = vmatPath?.ToLowerInvariant() ?? "";

        if (!mLower.Contains("ghost_glow") && !mLower.Contains("vindicta_glow") &&
            !vLower.Contains("ghost_glow") && !vLower.Contains("vindicta_glow") && !vLower.Contains("hornet_glow"))
        {
            return null;
        }

        var shader = _auraShader
                  ?? GD.Load<Shader>("res://assets/shaders/valve/vindicta.gdshader")
                  ?? GD.Load<Shader>("res://assets/shaders/vindicta.gdshader")
                  ?? Source2ShaderRegistry.GetDynamicGlowShader();

        if (shader == null) return null;

        var shaderMat = new ShaderMaterial { Shader = shader };
        shaderMat.RenderPriority = 2;
        shaderMat.SetMeta("PreserveShading", true);
        if (!string.IsNullOrEmpty(vmatPath))
        {
            shaderMat.SetMeta("OriginalVmatPath", vmatPath);
        }

        // Set authentic parameters matching vindicta.gdshader
        shaderMat.SetShaderParameter("aura_color", new Color(0.18f, 0.78f, 1.0f, 0.90f));
        shaderMat.SetShaderParameter("inner_core_color", new Color(0.65f, 0.94f, 1.0f, 1.0f));
        shaderMat.SetShaderParameter("emission_energy", 2.4f);
        shaderMat.SetShaderParameter("fresnel_power", 2.0f);
        shaderMat.SetShaderParameter("noise_speed", 0.8f);

        // Also set fallback parameters in case source2_dynamic_glow.gdshader was loaded
        shaderMat.SetShaderParameter("glow_color", new Color(0.18f, 0.78f, 1.0f, 0.90f));
        shaderMat.SetShaderParameter("fresnel_exponent", 2.0f);
        shaderMat.SetShaderParameter("opacity_scale", 1.0f);

        return shaderMat;
    }

    public bool ShouldPreserveMaterial(string meshName, string vmatPath, Godot.Material material)
    {
        string mLower = meshName?.ToLowerInvariant() ?? "";
        string vLower = vmatPath?.ToLowerInvariant() ?? "";
        return mLower.Contains("ghost_glow") || mLower.Contains("vindicta_glow") || vLower.Contains("vindicta_glow") || vLower.Contains("hornet_glow");
    }

    public Control BuildUI(Action<string, Variant> onParameterChanged)
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);

        // Aura Color
        var rowColor = new HBoxContainer();
        var lblColor = new Label { Text = "Aura Color:", CustomMinimumSize = new Vector2(130, 0) };
        var cpColor = new ColorPickerButton { Color = new Color(0.18f, 0.78f, 1.0f, 0.90f), CustomMinimumSize = new Vector2(60, 26) };
        rowColor.AddChild(lblColor);
        rowColor.AddChild(cpColor);
        vbox.AddChild(rowColor);

        // Emission Intensity
        var rowInt = new HBoxContainer();
        var lblInt = new Label { Text = "Aura Glow Intensity:", CustomMinimumSize = new Vector2(130, 0) };
        var sliderInt = new HSlider { MinValue = 0.5, MaxValue = 5.0, Step = 0.1, Value = 2.4, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var valInt = new Label { Text = "2.40", CustomMinimumSize = new Vector2(45, 0) };
        rowInt.AddChild(lblInt);
        rowInt.AddChild(sliderInt);
        rowInt.AddChild(valInt);
        vbox.AddChild(rowInt);

        // Noise Speed
        var rowSpeed = new HBoxContainer();
        var lblSpeed = new Label { Text = "Ectoplasm Flow:", CustomMinimumSize = new Vector2(130, 0) };
        var sliderSpeed = new HSlider { MinValue = 0.1, MaxValue = 3.0, Step = 0.1, Value = 0.8, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var valSpeed = new Label { Text = "0.80", CustomMinimumSize = new Vector2(45, 0) };
        rowSpeed.AddChild(lblSpeed);
        rowSpeed.AddChild(sliderSpeed);
        rowSpeed.AddChild(valSpeed);
        vbox.AddChild(rowSpeed);

        // Events
        cpColor.ColorChanged += col =>
        {
            onParameterChanged?.Invoke("self_illum_tint", col);
            onParameterChanged?.Invoke("color_tint", col);
            onParameterChanged?.Invoke("glow_color", col);
            onParameterChanged?.Invoke("aura_color", col);
        };

        sliderInt.ValueChanged += v =>
        {
            valInt.Text = v.ToString("F2");
            onParameterChanged?.Invoke("opacity_scale", (float)v / 2.0f);
            onParameterChanged?.Invoke("self_illum_scale", (float)v * 4.0f);
            onParameterChanged?.Invoke("emission_energy", (float)v);
        };

        sliderSpeed.ValueChanged += v =>
        {
            valSpeed.Text = v.ToString("F2");
            onParameterChanged?.Invoke("self_illum_scroll_speed", new Vector2(0.0f, -(float)v * 0.35f));
            onParameterChanged?.Invoke("scroll_speed", new Vector2(0.0f, -(float)v * 0.35f));
            onParameterChanged?.Invoke("noise_speed", (float)v);
        };

        return vbox;
    }
}
