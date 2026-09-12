using System;
using Godot;

namespace DeadlockPlayground.Materials.Heroes;

/// <summary>
/// Hero material configuration for Wraith.
/// wraith_head.vmat uses F_VERTEX_COLOR = 1 with a 1×1 dummy texture (498635a).
/// Her true skin/hair color lives in the mesh vertex buffer (COLOR_0) and in the
/// TextureNprTramsissiveColor1 VectorParam. The head is rendered via source2_pbr.gdshader
/// (ShaderMaterial) rather than StandardMaterial3D so the Hemi-Octahedral normal map
/// and AO can be decoded correctly.
/// This config is a thin guard; the actual ShaderMaterial assembly is handled in
/// Source2MaterialHelper.CreateMaterialFromVmat via the isDevDummyTexture → NPR head path.
/// </summary>
public class WraithMaterialConfig : IHeroMaterialConfig
{
    public string HeroKey => "wraith";
    public string DisplayName => "Wraith";

    public void ConfigureBaseMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D material)
    {
        // Wraith's head (source2_pbr) and cards (source2_cards) are ShaderMaterials.
        // StandardMaterial3D surfaces (body, clothing, weapons) are handled natively.
    }

    public ShaderMaterial GetSignatureMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D baseMat)
    {
        // No interactive signature effect for Wraith — head and cards use dedicated shaders.
        return null;
    }

    public Control BuildUI(Action<string, Variant> onParameterChanged)
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);

        // Card Glow Tint (Multiplies authentic texture color; default White preserves 100% natural lilac)
        var rowColor = new HBoxContainer();
        var lblColor = new Label { Text = "Card Glow Tint:", CustomMinimumSize = new Vector2(130, 0) };
        var cpColor = new ColorPickerButton { Color = Colors.White, CustomMinimumSize = new Vector2(60, 26) };
        rowColor.AddChild(lblColor);
        rowColor.AddChild(cpColor);
        vbox.AddChild(rowColor);

        // Card Emission Scale / Bloom (Balanced 2.0 glow, soft radiant bloom)
        var rowScale = new HBoxContainer();
        var lblScale = new Label { Text = "Card Bloom Energy:", CustomMinimumSize = new Vector2(130, 0) };
        var sliderScale = new HSlider { MinValue = 0.0, MaxValue = 6.0, Step = 0.1, Value = 2.0, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var valScale = new Label { Text = "2.00", CustomMinimumSize = new Vector2(45, 0) };
        rowScale.AddChild(lblScale);
        rowScale.AddChild(sliderScale);
        rowScale.AddChild(valScale);
        vbox.AddChild(rowScale);

        // Card Halo Opacity (Selective exterior envelope transparency, default 0.10)
        var rowHalo = new HBoxContainer();
        var lblHalo = new Label { Text = "Aura Halo Opacity:", CustomMinimumSize = new Vector2(130, 0) };
        var sliderHalo = new HSlider { MinValue = 0.0, MaxValue = 1.0, Step = 0.02, Value = 0.10, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var valHalo = new Label { Text = "0.10", CustomMinimumSize = new Vector2(45, 0) };
        rowHalo.AddChild(lblHalo);
        rowHalo.AddChild(sliderHalo);
        rowHalo.AddChild(valHalo);
        vbox.AddChild(rowHalo);

        // Events
        cpColor.ColorChanged += col =>
        {
            onParameterChanged?.Invoke("g_vSelfIllumTint", col);
            onParameterChanged?.Invoke("glow_color", col);
        };

        sliderScale.ValueChanged += val =>
        {
            valScale.Text = val.ToString("F2");
            onParameterChanged?.Invoke("g_flSelfIllumScale", (float)val);
            onParameterChanged?.Invoke("emission_scale", (float)val);
        };

        sliderHalo.ValueChanged += val =>
        {
            valHalo.Text = val.ToString("F2");
            onParameterChanged?.Invoke("g_flHaloAlpha", (float)val);
        };

        return vbox;
    }
}
