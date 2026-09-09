using System;
using Godot;

namespace DeadlockPlayground.Materials.Heroes;

public class VindictaMaterialConfig : IHeroMaterialConfig
{
    public string HeroKey => "vindicta";
    public string DisplayName => "Vindicta";

    private Shader _auraShader;

    public VindictaMaterialConfig()
    {
        if (ResourceLoader.Exists("res://assets/shaders/vindicta.gdshader"))
        {
            _auraShader = GD.Load<Shader>("res://assets/shaders/vindicta.gdshader");
        }
    }

    public void ConfigureBaseMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D material)
    {
        // Base materials are accurately parsed and configured directly from VPK attributes by Source2MaterialHelper
    }

    public ShaderMaterial GetSignatureMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D baseMat)
    {
        string meshLower = meshName.ToLowerInvariant();
        string vmatLower = vmatPath.ToLowerInvariant();

        // The custom aura shader is applied ONLY to the outer ghost_glow mesh
        if (meshLower.Contains("glow") || vmatLower.Contains("vindicta_glow"))
        {
            if (_auraShader == null) return null;

            var mat = new ShaderMaterial { Shader = _auraShader };
            mat.SetShaderParameter("aura_color", new Color(0.18f, 0.78f, 1.0f, 0.90f));
            mat.SetShaderParameter("inner_core_color", new Color(0.65f, 0.94f, 1.0f, 1.0f));
            mat.SetShaderParameter("emission_energy", 2.4f);
            mat.SetShaderParameter("fresnel_power", 2.0f);
            mat.SetShaderParameter("noise_speed", 0.8f);
            return mat;
        }

        return null;
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
            onParameterChanged?.Invoke("aura_color", col);
        };

        sliderInt.ValueChanged += v =>
        {
            valInt.Text = v.ToString("F2");
            onParameterChanged?.Invoke("emission_energy", (float)v);
        };

        sliderSpeed.ValueChanged += v =>
        {
            valSpeed.Text = v.ToString("F2");
            onParameterChanged?.Invoke("noise_speed", (float)v);
        };

        return vbox;
    }
}
