using System;
using Godot;

namespace DeadlockPlayground.Materials.Heroes;

public class LadyGeistMaterialConfig : IHeroMaterialConfig
{
    public string HeroKey => "ghost";
    public string DisplayName => "Lady Geist: Crystalline Demon Arm";

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
        string vmatLower = vmatPath.ToLowerInvariant();

        // Lady Geist arm base material (when realistic PBR is active)
        if (vmatLower.Contains("ghost2_arm") || vmatLower.Contains("demon_arm"))
        {
            material.AlbedoColor = new Color(0.10f, 0.85f, 0.45f, 0.90f);
            material.Roughness = 0.12f;
            material.Metallic = 0.05f;
            material.ClearcoatEnabled = true;
            material.Clearcoat = 1.0f;
            material.ClearcoatRoughness = 0.08f;
            material.EmissionEnabled = true;
            material.Emission = new Color(0.12f, 0.95f, 0.52f);
            material.EmissionEnergyMultiplier = 1.4f;
            return;
        }

        // Clothes, hair, body, shawl, gun
        material.EmissionEnabled = false;

        if (vmatLower.Contains("gun"))
        {
            material.Metallic = 0.75f;
            material.Roughness = 0.35f;
        }
        else
        {
            material.Roughness = 0.65f;
            material.Metallic = 0.05f;
        }
    }

    public ShaderMaterial GetSignatureMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D baseMat)
    {
        string vmatLower = vmatPath.ToLowerInvariant();

        // Custom arm shader is applied ONLY to Lady Geist's demon arm
        if (vmatLower.Contains("ghost2_arm") || vmatLower.Contains("demon_arm"))
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
