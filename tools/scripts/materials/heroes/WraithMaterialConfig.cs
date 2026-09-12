using System;
using Godot;
using SteamDatabase.ValvePak;
using ValveResourceFormat.ResourceTypes;
using VrfMaterial = ValveResourceFormat.ResourceTypes.Material;

namespace DeadlockPlayground.Materials.Heroes;

/// <summary>
/// Hero material configuration for Wraith.
/// Manages Wraith's head NPR shader (source2_vertcolor_pbr), psychic lilac signature glow (#A967F5),
/// and the dual-card architecture:
///   - Fedora hat card (wraith_model Surface [3]): Solid, opaque StandardMaterial3D (prevents backface double-vision).
///   - Floating handcards (wraith_handcards_model): source2_cards.gdshader with selective translucent aura envelope.
/// </summary>
public class WraithMaterialConfig : IHeroMaterialConfig
{
    public string HeroKey => "wraith";
    public string DisplayName => "Wraith";

    public Color? SignatureGlowColor => new Color(0.663f, 0.404f, 0.961f, 1.0f); // #A967F5 lilac

    public void ConfigureBaseMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D material)
    {
    }

    public ShaderMaterial GetSignatureMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D baseMat)
    {
        return null;
    }

    public Godot.Material TryCreateCustomMaterial(Package package, string vmatPath, string meshName)
    {
        string vmatLower = vmatPath?.ToLowerInvariant() ?? "";
        if (!vmatLower.Contains("wraith_cards") && !vmatLower.Contains("handcards") && !(vmatLower.Contains("card") && vmatLower.Contains("wraith")))
        {
            return null;
        }

        // Case A: Fedora hat card on wraith_model (Surface [3])
        // Must be an opaque StandardMaterial3D with Backface Culling enabled and Transparency disabled.
        if (meshName != null && meshName.Contains("wraith_model", StringComparison.OrdinalIgnoreCase))
        {
            return CreateHatCardMaterial(package);
        }

        // Case B: Floating handcards with psychic aura skirt
        var cardShader = Source2ShaderRegistry.GetCardsShader();
        if (cardShader != null)
        {
            var shaderMat = new ShaderMaterial { Shader = cardShader };
            shaderMat.ResourceName = vmatPath;
            shaderMat.SetMeta("PreserveShading", true);

            var albedo = Source2TextureLoader.GetOrLoadTexture(package, "models/heroes_wip/wraith/materials/wraith_cards_color_psd_71101b24.vtex", forceOpaque: true)
                      ?? Source2TextureLoader.GetOrLoadTexture(package, "wraith_cards_color_psd_71101b24.vtex", forceOpaque: true);
            if (albedo != null) shaderMat.SetShaderParameter("g_tColor", albedo);

            var mask = Source2TextureLoader.GetOrLoadTexture(package, "models/heroes_wip/wraith/materials/wraith_cards_mask_psd_b2fc96c4.vtex", forceOpaque: false)
                    ?? Source2TextureLoader.GetOrLoadTexture(package, "wraith_cards_mask_psd_b2fc96c4.vtex", forceOpaque: false);
            if (mask != null) shaderMat.SetShaderParameter("g_tSelfIllumMask", mask);

            var nr = Source2TextureLoader.GetOrLoadTexture(package, "models/heroes_wip/wraith/materials/wraith_cards_vmat_g_tnormalroughness_ebebd272.vtex", forceOpaque: true)
                  ?? Source2TextureLoader.GetOrLoadTexture(package, "wraith_cards_vmat_g_tnormalroughness_ebebd272.vtex", forceOpaque: true);
            if (nr != null) shaderMat.SetShaderParameter("g_tNormalRoughness", nr);

            shaderMat.SetShaderParameter("g_vSelfIllumTint", Colors.White);
            shaderMat.SetShaderParameter("g_flSelfIllumScale", 2.0f);
            shaderMat.SetShaderParameter("g_flHaloAlpha", 0.10f);
            shaderMat.SetShaderParameter("g_flHaloVThreshold", 0.82f);
            shaderMat.SetShaderParameter("invert_v", false);

            return shaderMat;
        }

        return CreateHatCardMaterial(package);
    }

    public static StandardMaterial3D CreateHatCardMaterial(Package package)
    {
        var mat = new StandardMaterial3D
        {
            ResourceName = "wraith_hat_card",
            Transparency = BaseMaterial3D.TransparencyEnum.Disabled,
            CullMode = BaseMaterial3D.CullModeEnum.Back,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always,
            Roughness = 0.55f,
            Metallic = 0.0f,
            AlbedoColor = Colors.White
        };
        mat.SetMeta("PreserveShading", true);

        var albedo = Source2TextureLoader.GetOrLoadTexture(package, "models/heroes_wip/wraith/materials/wraith_cards_color_psd_71101b24.vtex", forceOpaque: true)
                  ?? Source2TextureLoader.GetOrLoadTexture(package, "wraith_cards_color_psd_71101b24.vtex", forceOpaque: true);
        if (albedo != null) mat.AlbedoTexture = albedo;

        var mask = Source2TextureLoader.GetOrLoadTexture(package, "models/heroes_wip/wraith/materials/wraith_cards_mask_psd_b2fc96c4.vtex", forceOpaque: false)
                ?? Source2TextureLoader.GetOrLoadTexture(package, "wraith_cards_mask_psd_b2fc96c4.vtex", forceOpaque: false);
        if (mask != null)
        {
            mat.EmissionEnabled = true;
            mat.EmissionTexture = mask;
            mat.EmissionOperator = BaseMaterial3D.EmissionOperatorEnum.Multiply;
            mat.Emission = new Color(0.663f, 0.404f, 0.961f, 1.0f); // Lilac #A967F5
            mat.EmissionEnergyMultiplier = 1.2f;
        }

        return mat;
    }

    public bool ShouldPreserveMaterial(string meshName, string vmatPath, Godot.Material material)
    {
        string mLower = meshName?.ToLowerInvariant() ?? "";
        string vLower = vmatPath?.ToLowerInvariant() ?? "";
        return mLower.Contains("card") || vLower.Contains("card") || (material?.ResourceName?.Contains("card") == true);
    }

    public Control BuildUI(Action<string, Variant> onParameterChanged)
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);

        // Card Glow Tint
        var rowColor = new HBoxContainer();
        var lblColor = new Label { Text = "Card Glow Tint:", CustomMinimumSize = new Vector2(130, 0) };
        var cpColor = new ColorPickerButton { Color = Colors.White, CustomMinimumSize = new Vector2(60, 26) };
        rowColor.AddChild(lblColor);
        rowColor.AddChild(cpColor);
        vbox.AddChild(rowColor);

        // Card Emission Scale / Bloom
        var rowScale = new HBoxContainer();
        var lblScale = new Label { Text = "Card Bloom Energy:", CustomMinimumSize = new Vector2(130, 0) };
        var sliderScale = new HSlider { MinValue = 0.0, MaxValue = 6.0, Step = 0.1, Value = 2.0, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var valScale = new Label { Text = "2.00", CustomMinimumSize = new Vector2(45, 0) };
        rowScale.AddChild(lblScale);
        rowScale.AddChild(sliderScale);
        rowScale.AddChild(valScale);
        vbox.AddChild(rowScale);

        // Card Halo Opacity
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
