using System;
using System.IO;
using Godot;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using VrfMaterial = ValveResourceFormat.ResourceTypes.Material;

namespace DeadlockPlayground.Materials.Heroes;

/// <summary>
/// Hero material configuration for Lady Geist (ghost.vmdl_c / geist.vmdl_c).
/// Manages spectral emerald crystal arm shading (lady_geist.gdshader), data-driven
/// crystal tint extraction, and custom fur/shawl material setup matching Blender node pipeline (geist_shawl).
/// </summary>
public class LadyGeistMaterialConfig : IHeroMaterialConfig
{
    public string HeroKey => "ghost";
    public string DisplayName => "Lady Geist Arm & Shawl";

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
        else if (isBaseShawlCloth || mLower.Contains("shawl") || vLower.Contains("shawl"))
        {
            // Base shawl cloth is strictly opaque geometry
            material.Transparency = BaseMaterial3D.TransparencyEnum.Disabled;
            material.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.OpaqueOnly;
            material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            material.Uv1Scale = new Vector3(4.0f, 0.5f, 1.0f);
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

        // Lady Geist fur shawl (base cloth and fur shells)
        if (mLower.Contains("shawl") || vLower.Contains("shawl") ||
            mLower.Contains("fur") || vLower.Contains("fur"))
        {
            return CreateShawlMaterial(null, vmatPath, meshName) as ShaderMaterial;
        }

        return null;
    }

    public Godot.Material TryCreateCustomMaterial(Package package, string vmatPath, string meshName)
    {
        return TryCreateCustomMaterial(package, vmatPath, meshName, null);
    }

    public Godot.Material TryCreateCustomMaterial(Package package, string vmatPath, string meshName, Package addonPackage)
    {
        string mLower = meshName?.ToLowerInvariant() ?? "";
        string vLower = vmatPath?.ToLowerInvariant() ?? "";

        bool isShawlOrFur = mLower.Contains("shawl") || vLower.Contains("shawl") ||
                            mLower.Contains("geist_fur") || vLower.Contains("geist_fur") ||
                            (mLower.Contains("fur") && (mLower.Contains("ghost") || mLower.Contains("geist")));

        if (isShawlOrFur)
        {
            return CreateShawlMaterial(package, vmatPath, meshName, addonPackage);
        }

        return null;
    }

    /// <summary>
    /// Creates the custom Lady Geist fur shawl material matching the Blender node pipeline:
    /// - UV Tile scale: Vector2(4.0f, 0.5f)
    /// - Base texture (geist_shawl_test_vmat_g_tcolor) multiplied by vertex color (COLOR.rgb)
    /// - Normal map (RGB) and Roughness (Alpha) unpacked from geist_shawl_test_vmat_g_tnormalroughness
    /// - Two-sided rendering with cull_mode = CULL_DISABLED (cull_disabled in lady_geist_shawl.gdshader)
    /// - Sets is_fur_shell = true and alpha_scissor_threshold = 0.35f for fur shells (ghost_shawl_fur01-05),
    ///   or is_fur_shell = false (opaque ALPHA = 1.0) for the base shawl cloth (ghost_shawl).
    /// </summary>
    public static Godot.Material CreateShawlMaterial(Package package, string vmatPath, string meshName, Package addonPackage = null)
    {
        var shader = Source2ShaderRegistry.GetLadyGeistShawlShader();
        if (shader == null)
        {
            GD.PrintErr("[LadyGeistMaterialConfig] lady_geist_shawl.gdshader could not be loaded.");
            return null;
        }

        string mLower = meshName?.ToLowerInvariant() ?? "";
        string vLower = vmatPath?.ToLowerInvariant() ?? "";

        bool isBaseShawlCloth = mLower.Equals("ghost_shawl") || mLower.Equals("shawl") || mLower.Equals("geist_shawl") ||
                                mLower.EndsWith("_ghost_shawl") || mLower.EndsWith("_geist_shawl") || mLower.EndsWith("_shawl");
        bool isFurShell = (mLower.Contains("fur") || vLower.Contains("fur")) && !isBaseShawlCloth;

        var shaderMat = new ShaderMaterial { Shader = shader };
        shaderMat.ResourceName = string.IsNullOrEmpty(vmatPath) ? (isFurShell ? "ghost_shawl_fur" : "ghost_shawl") : vmatPath;
        shaderMat.SetMeta("PreserveShading", true);
        if (!string.IsNullOrEmpty(vmatPath))
        {
            shaderMat.SetMeta("OriginalVmatPath", vmatPath);
        }

        // Set UV scale Vector2(4.0, 0.5) matching Blender Mapping node
        shaderMat.SetShaderParameter("uv_scale", new Vector2(4.0f, 0.5f));
        shaderMat.SetShaderParameter("is_fur_shell", isFurShell);
        shaderMat.SetShaderParameter("alpha_scissor_threshold", 0.35f);

        VrfMaterial vrfMat = TryReadVmat(package, vmatPath, addonPackage);

        // 1. Resolve base detail albedo texture (geist_shawl_test_vmat_g_tcolor)
        ImageTexture albedoTex = null;
        if (vrfMat != null)
        {
            string colParam = Source2TextureLoader.GetTextureParam(vrfMat, "g_tColor")
                           ?? Source2TextureLoader.GetTextureParam(vrfMat, "TextureColor")
                           ?? Source2TextureLoader.GetTextureParam(vrfMat, "g_tColor1");
            if (!string.IsNullOrEmpty(colParam))
            {
                // If this is a fur shell, only use VMAT's color if it actually represents fur
                bool paramIsFurOrCard = colParam.Contains("fur", StringComparison.OrdinalIgnoreCase) ||
                                        colParam.Contains("test", StringComparison.OrdinalIgnoreCase);
                if (!isFurShell || paramIsFurOrCard)
                {
                    albedoTex = Source2TextureLoader.GetOrLoadTexture(package, colParam, forceOpaque: !isFurShell, addonPackage: addonPackage);
                }
            }
        }

        if (albedoTex == null)
        {
            // For fur shells, load the authentic fur detail texture with author-crafted cutout alpha channel
            albedoTex = Source2TextureLoader.GetOrLoadTexture(package, "models/heroes_wip/geist/materials/geist_shawl_test_vmat_g_tcolor.vtex", forceOpaque: false, addonPackage: addonPackage)
                     ?? Source2TextureLoader.GetOrLoadTexture(package, "geist_shawl_test_vmat_g_tcolor.vtex", forceOpaque: false, addonPackage: addonPackage)
                     ?? Source2TextureLoader.GetOrLoadTexture(package, "geist_shawl_test_vmat_g_tcolor", forceOpaque: false, addonPackage: addonPackage)
                     ?? Source2TextureLoader.GetOrLoadTexture(package, "models/heroes_staging/ghost/materials/ghost_shawl_color.vtex", forceOpaque: !isFurShell, addonPackage: addonPackage)
                     ?? Source2TextureLoader.GetOrLoadTexture(package, "ghost_shawl_color.vtex", forceOpaque: !isFurShell, addonPackage: addonPackage)
                     ?? Source2TextureLoader.GetOrLoadTexture(package, "models/heroes_wip/geist/materials/geist_shawl_color.vtex", forceOpaque: !isFurShell, addonPackage: addonPackage)
                     ?? Source2TextureLoader.GetOrLoadTexture(package, "geist_shawl_color", forceOpaque: !isFurShell, addonPackage: addonPackage);
        }

        if (albedoTex != null)
        {
            shaderMat.SetShaderParameter("texture_albedo", albedoTex);
        }

        // 2. Resolve normal & roughness texture (geist_shawl_test_vmat_g_tnormalroughness)
        ImageTexture nrTex = null;
        if (vrfMat != null)
        {
            string nrParam = Source2TextureLoader.GetTextureParam(vrfMat, "g_tNormalRoughness")
                          ?? Source2TextureLoader.GetTextureParam(vrfMat, "TextureNormalRoughness")
                          ?? Source2TextureLoader.GetTextureParam(vrfMat, "g_tNormal")
                          ?? Source2TextureLoader.GetTextureParam(vrfMat, "TextureNormal");
            if (!string.IsNullOrEmpty(nrParam))
            {
                nrTex = Source2TextureLoader.GetOrLoadTexture(package, nrParam, forceOpaque: false, addonPackage: addonPackage);
            }
        }

        if (nrTex == null)
        {
            nrTex = Source2TextureLoader.GetOrLoadTexture(package, "models/heroes_wip/geist/materials/geist_shawl_test_vmat_g_tnormalroughness.vtex", forceOpaque: false, addonPackage: addonPackage)
                 ?? Source2TextureLoader.GetOrLoadTexture(package, "geist_shawl_test_vmat_g_tnormalroughness.vtex", forceOpaque: false, addonPackage: addonPackage)
                 ?? Source2TextureLoader.GetOrLoadTexture(package, "geist_shawl_test_vmat_g_tnormalroughness", forceOpaque: false, addonPackage: addonPackage)
                 ?? Source2TextureLoader.GetOrLoadTexture(package, "models/heroes_staging/ghost/materials/ghost_shawl_normal.vtex", forceOpaque: false, addonPackage: addonPackage)
                 ?? Source2TextureLoader.GetOrLoadTexture(package, "ghost_shawl_normal", forceOpaque: false, addonPackage: addonPackage)
                 ?? Source2TextureLoader.GetOrLoadTexture(package, "models/heroes_wip/geist/materials/geist_shawl_normal.vtex", forceOpaque: false, addonPackage: addonPackage)
                 ?? Source2TextureLoader.GetOrLoadTexture(package, "geist_shawl_normal", forceOpaque: false, addonPackage: addonPackage);
        }

        if (nrTex != null)
        {
            shaderMat.SetShaderParameter("texture_normal_roughness", nrTex);
        }

        shaderMat.SetShaderParameter("normal_map_depth", 1.0f);
        shaderMat.SetShaderParameter("roughness_mult", 1.0f);

        GD.Print($"  [LadyGeistShawl] Built custom fur shawl ShaderMaterial for '{meshName}' (furShell: {isFurShell}, albedo: {albedoTex != null}, normal/roughness: {nrTex != null})");
        return shaderMat;
    }

    private static VrfMaterial TryReadVmat(Package package, string vmatPath, Package addonPackage = null)
    {
        if (string.IsNullOrWhiteSpace(vmatPath)) return null;
        PackageEntry entry = null;
        Package targetPkg = null;

        if (addonPackage != null)
        {
            entry = Source2MaterialHelper.FindVmatEntry(addonPackage, vmatPath);
            if (entry != null) targetPkg = addonPackage;
        }
        if (entry == null && package != null)
        {
            entry = Source2MaterialHelper.FindVmatEntry(package, vmatPath);
            if (entry != null) targetPkg = package;
        }
        if (entry == null || targetPkg == null) return null;

        try
        {
            targetPkg.ReadEntry(entry, out byte[] data);
            using var resource = new ValveResourceFormat.Resource();
            using var ms = new MemoryStream(data);
            resource.Read(ms);
            if (resource.ResourceType == ResourceType.Material)
            {
                return (VrfMaterial)resource.DataBlock;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[LadyGeistMaterialConfig] Error parsing VMAT ({vmatPath}): {ex.Message}");
        }
        return null;
    }

    public bool ShouldPreserveMaterial(string meshName, string vmatPath, Godot.Material material)
    {
        string mLower = meshName?.ToLowerInvariant() ?? "";
        string vLower = vmatPath?.ToLowerInvariant() ?? "";

        // Shawl fur material must always be preserved across shading modes
        if (mLower.Contains("shawl") || vLower.Contains("shawl") || (material?.ResourceName?.Contains("shawl") == true))
        {
            return true;
        }

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

        // Shawl Fur Roughness Multiplier
        var rowRough = new HBoxContainer();
        var lblRough = new Label { Text = "Shawl Fur Roughness:", CustomMinimumSize = new Vector2(130, 0) };
        var sliderRough = new HSlider { MinValue = 0.1, MaxValue = 2.0, Step = 0.05, Value = 1.0, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var valRough = new Label { Text = "1.00", CustomMinimumSize = new Vector2(45, 0) };
        rowRough.AddChild(lblRough);
        rowRough.AddChild(sliderRough);
        rowRough.AddChild(valRough);
        vbox.AddChild(rowRough);

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

        sliderRough.ValueChanged += v =>
        {
            valRough.Text = v.ToString("F2");
            onParameterChanged?.Invoke("roughness_mult", (float)v);
        };

        return vbox;
    }
}

