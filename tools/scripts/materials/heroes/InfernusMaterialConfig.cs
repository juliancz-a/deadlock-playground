using System;
using Godot;
using SteamDatabase.ValvePak;
using DeadlockPlayground.Materials.Archetypes;

namespace DeadlockPlayground.Materials.Heroes;

/// <summary>
/// Hero material configuration for Infernus (inferno.vmdl_c).
/// Coordinates solid incandescent volumetric hair flame, additive arm glow contours,
/// and signature fiery amber glow color (#D56E00).
/// </summary>
public class InfernusMaterialConfig : IHeroMaterialConfig
{
    public string HeroKey => "inferno";
    public string DisplayName => "Infernus Fire";

    public Color? SignatureGlowColor => new Color(0.835294f, 0.431373f, 0.0f, 1.0f); // #D56E00 fiery amber

    public void ConfigureBaseMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D material)
    {
        // inferno_body represents his entire skin (neck, face, chin, torso, arm).
        // It retains the standard comic black outline (#141414).
    }

    public ShaderMaterial GetSignatureMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D baseMat)
    {
        return null;
    }

    public Material TryCreateCustomMaterial(Package package, string vmatPath, string meshName)
    {
        // Infernus hair flame and arm glow are processed via DynamicFxMaterialBuilder
        // using the authentic Valve parameters from VMAT.
        return null;
    }

    public Control BuildUI(Action<string, Variant> onParameterChanged)
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);

        // Fire Tint
        var rowColor = new HBoxContainer();
        var lblColor = new Label { Text = "Flame Color Tint:", CustomMinimumSize = new Vector2(130, 0) };
        var cpColor = new ColorPickerButton { Color = new Color(0.835294f, 0.431373f, 0.0f, 1.0f), CustomMinimumSize = new Vector2(60, 26) };
        rowColor.AddChild(lblColor);
        rowColor.AddChild(cpColor);
        vbox.AddChild(rowColor);

        // Flame Intensity
        var rowInt = new HBoxContainer();
        var lblInt = new Label { Text = "Flame Brightness:", CustomMinimumSize = new Vector2(130, 0) };
        var sliderInt = new HSlider { MinValue = 1.0, MaxValue = 8.0, Step = 0.1, Value = 4.0, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var valInt = new Label { Text = "4.00", CustomMinimumSize = new Vector2(45, 0) };
        rowInt.AddChild(lblInt);
        rowInt.AddChild(sliderInt);
        rowInt.AddChild(valInt);
        vbox.AddChild(rowInt);

        // Events
        cpColor.ColorChanged += col =>
        {
            onParameterChanged?.Invoke("self_illum_tint", col);
            onParameterChanged?.Invoke("glow_color", col);
        };

        sliderInt.ValueChanged += v =>
        {
            valInt.Text = v.ToString("F2");
            onParameterChanged?.Invoke("self_illum_scale", (float)v);
            onParameterChanged?.Invoke("emission_boost", (float)v);
        };

        return vbox;
    }
}
