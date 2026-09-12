using System;
using Godot;
using SteamDatabase.ValvePak;

namespace DeadlockPlayground.Materials.Heroes;

/// <summary>
/// Hero material configuration for Lady Geist (ghost.vmdl_c / geist.vmdl_c).
/// Manages spectral emerald crystal arm shading (lady_geist.gdshader), data-driven
/// crystal tint extraction, and shawl fur shell safeguards.
/// </summary>
public class LadyGeistMaterialConfig : IHeroMaterialConfig
{
    public string HeroKey => "ghost";
    public string DisplayName => "Lady Geist Arm";

    /// <summary>
    /// Authentic Source 2 Lady Geist emerald green crystal spectral color.
    /// Used as authentic fallback when VMAT attributes do not declare custom tints.
    /// </summary>
    public static readonly Color AuthenticCrystalColor = new Color(0.04f, 0.78f, 0.40f, 0.80f);

    private static Color? _dynamicArmColor = null;

    /// <summary>
    /// Sets the dynamic arm color extracted from VMAT vector properties or compiled texture mipmaps.
    /// </summary>
    public static void SetDynamicArmColor(Color col)
    {
        _dynamicArmColor = col;
    }

    /// <summary>
    /// Returns the dynamically extracted arm crystal tint, or the authentic emerald crystal fallback.
    /// </summary>
    public Color? SignatureGlowColor => _dynamicArmColor ?? AuthenticCrystalColor;

    public void ConfigureBaseMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D material)
    {
        string mLower = meshName?.ToLowerInvariant() ?? "";
        string vLower = vmatPath?.ToLowerInvariant() ?? "";

        // Shawl fur cards (ghost_shawl_fur01 to fur05, geist_fur, etc.)
        bool isBaseShawlCloth = mLower.Equals("ghost_shawl") || mLower.Equals("shawl") || mLower.Equals("geist_shawl") ||
                                mLower.EndsWith("_ghost_shawl") || mLower.EndsWith("_geist_shawl") || mLower.EndsWith("_shawl");
        if ((mLower.Contains("fur") || vLower.Contains("fur")) && !isBaseShawlCloth)
        {
            material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            material.AlphaScissorThreshold = 0.35f;
            material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            material.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always;
            material.VertexColorUseAsAlbedo = false;
        }
    }

    public ShaderMaterial GetSignatureMaterial(string meshName, int surfaceIndex, string vmatPath, StandardMaterial3D baseMat)
    {
        string mLower = meshName?.ToLowerInvariant() ?? "";
        string vLower = vmatPath?.ToLowerInvariant() ?? "";

        // Lady Geist's demonic arm crystal / energy aura
        if (mLower.Contains("arm") || vLower.Contains("arm") ||
            mLower.Contains("crystal") || vLower.Contains("crystal") ||
            vLower.Contains("armglow") || mLower.Contains("armglow"))
        {
            var shader = Source2ShaderRegistry.GetLadyGeistShader();
            if (shader != null)
            {
                var sm = new ShaderMaterial { Shader = shader };
                Color armCol = SignatureGlowColor ?? AuthenticCrystalColor;
                sm.SetShaderParameter("arm_color", armCol);
                sm.SetShaderParameter("core_color", new Color(0.12f, 0.95f, 0.52f, 1.0f));
                sm.SetShaderParameter("rim_color", new Color(0.35f, 1.35f, 0.75f, 1.0f));
                sm.SetShaderParameter("rim_power", 2.4f);
                sm.SetShaderParameter("emission_energy", 2.4f);
                sm.SetShaderParameter("roughness", 0.10f);

                if (baseMat?.AlbedoTexture != null)
                {
                    sm.SetShaderParameter("albedo_texture", baseMat.AlbedoTexture);
                }
                if (baseMat?.NormalTexture != null)
                {
                    sm.SetShaderParameter("normal_texture", baseMat.NormalTexture);
                }
                return sm;
            }
        }
        return null;
    }

    public Material TryCreateCustomMaterial(Package package, string vmatPath, string meshName)
    {
        return null;
    }

    public bool ShouldPreserveMaterial(string meshName, string vmatPath, Material material)
    {
        string mLower = meshName?.ToLowerInvariant() ?? "";
        string vLower = vmatPath?.ToLowerInvariant() ?? "";
        bool isBaseShawlCloth = mLower.Equals("ghost_shawl") || mLower.Equals("shawl") || mLower.Equals("geist_shawl") ||
                                mLower.EndsWith("_ghost_shawl") || mLower.EndsWith("_geist_shawl") || mLower.EndsWith("_shawl");
        return (mLower.Contains("fur") || vLower.Contains("fur")) && !isBaseShawlCloth;
    }

    public Control BuildUI(Action<string, Variant> onParameterChanged)
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);

        Color defaultCol = SignatureGlowColor ?? AuthenticCrystalColor;

        // Arm Color
        var rowColor = new HBoxContainer();
        var lblColor = new Label { Text = "Arm Crystal Tint:", CustomMinimumSize = new Vector2(130, 0) };
        var cpColor = new ColorPickerButton { Color = defaultCol, CustomMinimumSize = new Vector2(60, 26) };
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
            onParameterChanged?.Invoke("glow_color", col);
            onParameterChanged?.Invoke("self_illum_tint", col);
            onParameterChanged?.Invoke("color_tint", col);
            onParameterChanged?.Invoke("arm_color", col);
        };

        sliderInt.ValueChanged += v =>
        {
            valInt.Text = v.ToString("F2");
            onParameterChanged?.Invoke("self_illum_scale", (float)v);
            onParameterChanged?.Invoke("emission_energy", (float)v);
        };

        return vbox;
    }
}
