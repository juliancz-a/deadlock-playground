using System;
using Godot;

namespace DeadlockPlayground.Materials.Heroes;

public class LadyGeistMaterialConfig : IHeroMaterialConfig
{
    public string HeroKey => "ghost";
    public string DisplayName => "Lady Geist Arm";

    private Shader _armShader;

    public LadyGeistMaterialConfig()
    {
        if (ResourceLoader.Exists("res://assets/shaders/lady_geist.gdshader"))
        {
            _armShader = GD.Load<Shader>("res://assets/shaders/lady_geist.gdshader");
        }
    }

    public void ConfigureBaseMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D material)
    {
        // Base materials are accurately parsed and configured directly from VPK attributes by Source2MaterialHelper
    }

    public ShaderMaterial GetSignatureMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D baseMat)
    {
        string vmatLower = vmatPath.ToLowerInvariant();

        // Custom crystalline arm shader is applied to Lady Geist's demon arm (excluding the additive armglow submesh)
        if ((vmatLower.Contains("geist_arm") || vmatLower.Contains("ghost2_arm") || vmatLower.Contains("demon_arm")) && !vmatLower.Contains("armglow"))
        {
            if (_armShader == null) return null;

            var mat = new ShaderMaterial { Shader = _armShader };
            mat.SetShaderParameter("arm_color", new Color(0.04f, 0.78f, 0.40f, 0.80f));
            mat.SetShaderParameter("core_color", new Color(0.12f, 0.95f, 0.52f, 1.0f));
            mat.SetShaderParameter("rim_color", new Color(0.35f, 1.35f, 0.75f, 1.0f));
            mat.SetShaderParameter("rim_power", 2.4f);
            mat.SetShaderParameter("emission_energy", 2.4f);
            mat.SetShaderParameter("roughness", 0.10f);

            if (baseMat?.AlbedoTexture != null) mat.SetShaderParameter("albedo_texture", baseMat.AlbedoTexture);
            if (baseMat?.NormalTexture != null) mat.SetShaderParameter("normal_texture", baseMat.NormalTexture);

            return mat;
        }

        return null;
    }

    public Control BuildUI(Action<string, Variant> onParameterChanged)
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);

        // Arm Color
        var rowColor = new HBoxContainer();
        var lblColor = new Label { Text = "Arm Crystal Tint:", CustomMinimumSize = new Vector2(130, 0) };
        var cpColor = new ColorPickerButton { Color = new Color(0.04f, 0.78f, 0.40f, 0.80f), CustomMinimumSize = new Vector2(60, 26) };
        rowColor.AddChild(lblColor);
        rowColor.AddChild(cpColor);
        vbox.AddChild(rowColor);

        // Emission Intensity
        var rowInt = new HBoxContainer();
        var lblInt = new Label { Text = "Crystal Glow Energy:", CustomMinimumSize = new Vector2(130, 0) };
        var sliderInt = new HSlider { MinValue = 0.5, MaxValue = 5.0, Step = 0.1, Value = 2.4, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var valInt = new Label { Text = "2.40", CustomMinimumSize = new Vector2(45, 0) };
        rowInt.AddChild(lblInt);
        rowInt.AddChild(sliderInt);
        rowInt.AddChild(valInt);
        vbox.AddChild(rowInt);

        // Events
        cpColor.ColorChanged += col =>
        {
            onParameterChanged?.Invoke("arm_color", col);
        };

        sliderInt.ValueChanged += v =>
        {
            valInt.Text = v.ToString("F2");
            onParameterChanged?.Invoke("emission_energy", (float)v);
        };

        return vbox;
    }
}
