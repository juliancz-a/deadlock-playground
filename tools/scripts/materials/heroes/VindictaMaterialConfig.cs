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
        string meshLower = meshName.ToLowerInvariant();
        string vmatLower = vmatPath.ToLowerInvariant();

        // 1. Ghost glow aura mesh: Additive, transparent, does not write depth
        if (meshLower.Contains("glow") || vmatLower.Contains("vindicta_glow"))
        {
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            material.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
            material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            material.AlbedoColor = new Color(0.18f, 0.78f, 1.0f, 0.40f);
            material.EmissionEnabled = true;
            material.Emission = new Color(0.18f, 0.78f, 1.0f);
            material.EmissionEnergyMultiplier = 1.2f;
            material.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled;
            return;
        }

        // 2. Ghostly cyan extremities (Hornet hands and feet submesh: vindicta_head drawcall 4)
        if (vmatLower.Contains("vindicta_head") && !vmatLower.Contains("headv2"))
        {
            material.AlbedoColor = new Color(0.40f, 0.85f, 1.0f, 1.0f);
            material.Roughness = 0.5f;
            material.EmissionEnabled = false;
            return;
        }

        // 3. Ethereal hair
        if (vmatLower.Contains("vindicta_hair"))
        {
            material.AlbedoColor = new Color(0.60f, 0.88f, 1.0f, 1.0f);
            material.Roughness = 0.55f;
            material.EmissionEnabled = false;
            return;
        }

        // 4. Dress and props: Ensure no white emission blowout, let diffuse texture shine
        if (vmatLower.Contains("vindicta_dress") || vmatLower.Contains("vindicta_props") || vmatLower.Contains("headv2"))
        {
            material.EmissionEnabled = false;
            material.Roughness = 0.65f;
            material.Metallic = 0.10f;
            return;
        }

        // 5. Sniper rifle
        if (vmatLower.Contains("gun"))
        {
            material.EmissionEnabled = false;
            material.Metallic = 0.70f;
            material.Roughness = 0.35f;
            return;
        }
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
