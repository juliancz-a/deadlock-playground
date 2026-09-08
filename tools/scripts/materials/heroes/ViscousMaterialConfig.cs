using System;
using Godot;

namespace DeadlockPlayground.Materials.Heroes;

public class ViscousMaterialConfig : IHeroMaterialConfig
{
    public string HeroKey => "viscous";
    public string DisplayName => "Viscous";

    private Shader _slimeShader;

    public ViscousMaterialConfig()
    {
        if (ResourceLoader.Exists("res://assets/shaders/viscous.gdshader"))
        {
            _slimeShader = GD.Load<Shader>("res://assets/shaders/viscous.gdshader");
        }
    }

    public void ConfigureBaseMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D material)
    {
        string meshLower = meshName.ToLowerInvariant();
        string vmatLower = vmatPath.ToLowerInvariant();

        // 1. Body outline: Deadlock inverted-hull NPR shell mesh
        if (meshLower.Contains("bodyoutline") || vmatLower.Contains("viscous_outline"))
        {
            material.CullMode = BaseMaterial3D.CullModeEnum.Front; // Cull frontfaces so only backfaces form the outline rim
            material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            material.AlbedoColor = new Color(0.40f, 0.95f, 0.42f, 1.0f);
            material.Transparency = BaseMaterial3D.TransparencyEnum.Disabled;
            material.EmissionEnabled = false;
            return;
        }

        // 2. Viscous helmet glass
        if (vmatLower.Contains("viscous_glass"))
        {
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            material.AlbedoColor = new Color(0.30f, 0.85f, 0.55f, 0.35f);
            material.Roughness = 0.05f;
            material.Metallic = 0.1f;
            material.ClearcoatEnabled = true;
            material.Clearcoat = 1.0f;
            material.ClearcoatRoughness = 0.05f;
            material.EmissionEnabled = false;
            return;
        }

        // 3. Slime ball and slime body base PBR
        if (vmatLower.Contains("viscous_ball") || vmatLower.Contains("viscous_body"))
        {
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            material.AlbedoColor = new Color(0.18f, 0.75f, 0.35f, 0.70f);
            material.Roughness = 0.08f;
            material.Metallic = 0.0f;
            material.ClearcoatEnabled = true;
            material.Clearcoat = 1.0f;
            material.ClearcoatRoughness = 0.05f;
            material.EmissionEnabled = false;
            return;
        }

        // 4. Viscous boots, gloves, collar details, gun (black.vmat)
        if (vmatLower.Contains("black"))
        {
            material.AlbedoColor = new Color(0.15f, 0.18f, 0.15f, 1.0f);
            material.Roughness = 0.50f;
            material.Metallic = 0.20f;
            material.EmissionEnabled = false;
            return;
        }

        // 5. Core organs (viscous_swatches)
        if (vmatLower.Contains("viscous_swatches"))
        {
            material.AlbedoColor = new Color(0.20f, 0.70f, 0.30f, 1.0f);
            material.Roughness = 0.35f;
            material.EmissionEnabled = false;
            return;
        }

        // 6. Head sphere
        if (vmatLower.Contains("viscous_head"))
        {
            material.AlbedoColor = new Color(0.20f, 0.82f, 0.32f, 1.0f);
            material.Roughness = 0.12f;
            material.ClearcoatEnabled = true;
            material.Clearcoat = 1.0f;
            material.EmissionEnabled = false;
            return;
        }
    }

    public ShaderMaterial GetSignatureMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D baseMat)
    {
        string meshLower = meshName.ToLowerInvariant();
        string vmatLower = vmatPath.ToLowerInvariant();

        // Guns, details, boots, outline and glass do NOT get the slime shader
        if (meshLower.Contains("gun") || vmatLower.Contains("black") ||
            meshLower.Contains("bodyoutline") || vmatLower.Contains("viscous_outline") ||
            vmatLower.Contains("viscous_glass") || vmatLower.Contains("viscous_swatches"))
        {
            return null;
        }

        // Apply slime shader ONLY to slime body parts:
        // inflated[2] (viscous_ball) and body[1] (viscous_body)
        if (vmatLower.Contains("viscous_ball") || vmatLower.Contains("viscous_body"))
        {
            if (_slimeShader == null) return null;

            var mat = new ShaderMaterial { Shader = _slimeShader };
            mat.SetShaderParameter("slime_color", new Color(0.12f, 0.72f, 0.32f, 0.70f));
            mat.SetShaderParameter("core_color", new Color(0.04f, 0.35f, 0.14f, 1.0f));
            mat.SetShaderParameter("rim_glow_color", new Color(0.40f, 1.0f, 0.55f, 1.0f));
            mat.SetShaderParameter("subsurface_color", new Color(0.15f, 0.85f, 0.40f, 1.0f));
            mat.SetShaderParameter("roughness", 0.06f);
            mat.SetShaderParameter("rim_power", 2.8f);
            mat.SetShaderParameter("rim_intensity", 1.2f);
            return mat;
        }

        return null;
    }

    public Control BuildUI(Action<string, Variant> onParameterChanged)
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);

        // Slime Color
        var rowColor = new HBoxContainer();
        var lblColor = new Label { Text = "Slime Base Color:", CustomMinimumSize = new Vector2(130, 0) };
        var cpColor = new ColorPickerButton { Color = new Color(0.12f, 0.72f, 0.32f, 0.70f), CustomMinimumSize = new Vector2(60, 26) };
        rowColor.AddChild(lblColor);
        rowColor.AddChild(cpColor);
        vbox.AddChild(rowColor);

        // Slime Opacity
        var rowOp = new HBoxContainer();
        var lblOp = new Label { Text = "Slime Opacity:", CustomMinimumSize = new Vector2(130, 0) };
        var sliderOp = new HSlider { MinValue = 0.2, MaxValue = 1.0, Step = 0.05, Value = 0.70, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var valOp = new Label { Text = "0.70", CustomMinimumSize = new Vector2(45, 0) };
        rowOp.AddChild(lblOp);
        rowOp.AddChild(sliderOp);
        rowOp.AddChild(valOp);
        vbox.AddChild(rowOp);

        // Roughness
        var rowRough = new HBoxContainer();
        var lblRough = new Label { Text = "Gloss / Roughness:", CustomMinimumSize = new Vector2(130, 0) };
        var sliderRough = new HSlider { MinValue = 0.0, MaxValue = 0.5, Step = 0.01, Value = 0.06, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var valRough = new Label { Text = "0.06", CustomMinimumSize = new Vector2(45, 0) };
        rowRough.AddChild(lblRough);
        rowRough.AddChild(sliderRough);
        rowRough.AddChild(valRough);
        vbox.AddChild(rowRough);

        // Rim Glow Color
        var rowRimCol = new HBoxContainer();
        var lblRimCol = new Label { Text = "Rim Glow Color:", CustomMinimumSize = new Vector2(130, 0) };
        var cpRimCol = new ColorPickerButton { Color = new Color(0.40f, 1.0f, 0.55f, 1.0f), CustomMinimumSize = new Vector2(60, 26) };
        rowRimCol.AddChild(lblRimCol);
        rowRimCol.AddChild(cpRimCol);
        vbox.AddChild(rowRimCol);

        // Rim Glow Intensity
        var rowRimInt = new HBoxContainer();
        var lblRimInt = new Label { Text = "Rim Glow Intensity:", CustomMinimumSize = new Vector2(130, 0) };
        var sliderRimInt = new HSlider { MinValue = 0.0, MaxValue = 3.0, Step = 0.1, Value = 1.2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var valRimInt = new Label { Text = "1.20", CustomMinimumSize = new Vector2(45, 0) };
        rowRimInt.AddChild(lblRimInt);
        rowRimInt.AddChild(sliderRimInt);
        rowRimInt.AddChild(valRimInt);
        vbox.AddChild(rowRimInt);

        // Wire events
        cpColor.ColorChanged += col =>
        {
            float op = (float)sliderOp.Value;
            onParameterChanged?.Invoke("slime_color", new Color(col.R, col.G, col.B, op));
        };

        sliderOp.ValueChanged += v =>
        {
            valOp.Text = v.ToString("F2");
            Color col = cpColor.Color;
            onParameterChanged?.Invoke("slime_color", new Color(col.R, col.G, col.B, (float)v));
        };

        sliderRough.ValueChanged += v =>
        {
            valRough.Text = v.ToString("F2");
            onParameterChanged?.Invoke("roughness", (float)v);
        };

        cpRimCol.ColorChanged += col =>
        {
            onParameterChanged?.Invoke("rim_glow_color", col);
        };

        sliderRimInt.ValueChanged += v =>
        {
            valRimInt.Text = v.ToString("F2");
            onParameterChanged?.Invoke("rim_intensity", (float)v);
        };

        return vbox;
    }
}
