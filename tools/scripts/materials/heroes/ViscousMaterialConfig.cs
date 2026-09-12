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
        if (ResourceLoader.Exists("res://assets/shaders/valve/viscous.gdshader"))
            _slimeShader = GD.Load<Shader>("res://assets/shaders/valve/viscous.gdshader");
        else if (ResourceLoader.Exists("res://assets/shaders/viscous.gdshader"))
            _slimeShader = GD.Load<Shader>("res://assets/shaders/viscous.gdshader");
    }

    public void ConfigureBaseMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D material)
    {
        // Base materials are accurately parsed and configured directly from VPK attributes by Source2MaterialHelper
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
