using System;
using System.IO;
using System.Collections.Generic;
using Godot;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using DeadlockPlayground.Materials;
using DeadlockPlayground.Materials.Archetypes;
using VrfMaterial = ValveResourceFormat.ResourceTypes.Material;

/// <summary>
/// High-level orchestrator and facade for Valve Source 2 material ingestion.
/// Delegates texture decoding to Source2TextureLoader, shader management to Source2ShaderRegistry,
/// hero-specific multi-pass logic to HeroMaterialManager, and PBR/FX construction to specialized
/// archetype builders (Glass, DynamicFX, VertexColorPbr, StandardPbr).
/// </summary>
public static class Source2MaterialHelper
{
    #region Shader Accessors (Delegated to Source2ShaderRegistry)

    public static Shader LoadValveShader(string shaderName) => Source2ShaderRegistry.LoadValveShader(shaderName);
    public static Shader GetHeroOutlineShader() => Source2ShaderRegistry.GetHeroOutlineShader();
    public static Shader GetVertexColorPbrShader() => Source2ShaderRegistry.GetVertexColorPbrShader();
    public static Shader GetPbrHeadShader() => Source2ShaderRegistry.GetVertexColorPbrShader();
    public static Shader GetFlameHairShader() => Source2ShaderRegistry.GetFlameHairShader();
    public static Shader GetDynamicFxShader(bool isAdditive) => Source2ShaderRegistry.GetDynamicFxShader(isAdditive);
    public static Shader GetDynamicJitterShader() => Source2ShaderRegistry.GetDynamicJitterShader();
    public static Shader GetDynamicGlowShader() => Source2ShaderRegistry.GetDynamicGlowShader();
    public static Shader GetGlassShader() => Source2ShaderRegistry.GetGlassShader();
    public static Shader GetCardsShader() => Source2ShaderRegistry.GetCardsShader();
    public static Shader GetWraithCardShader() => Source2ShaderRegistry.GetWraithCardShader();
    public static Shader GetLashSparklesShader() => Source2ShaderRegistry.GetLashSparklesShader();

    #endregion

    #region Texture Accessors (Delegated to Source2TextureLoader)

    public static ImageTexture ExtractVtexToGodot(Package package, string vtexInternalPath, int maxDimension = 1024, bool forceOpaque = true)
        => Source2TextureLoader.ExtractVtexToGodot(package, vtexInternalPath, maxDimension, forceOpaque);

    public static ImageTexture LoadVtexTexture(Package package, VrfMaterial mat, string paramName, bool forceOpaque = false)
        => Source2TextureLoader.LoadVtexTexture(package, mat, paramName, forceOpaque);

    public static ImageTexture LoadVtexTexture(Package package, string texPath, bool forceOpaque = false)
        => Source2TextureLoader.LoadVtexTexture(package, texPath, forceOpaque);

    public static void cleanCache()
    {
        Source2TextureLoader.CleanCache();
    }

    #endregion

    #region Color & Matrix Math (Delegated to Source2ColorMatrix)

    public static Projection CalculateAlbedoColorCorrectMatrix(System.Numerics.Vector3 csb, System.Numerics.Vector3 colorOffset)
        => Source2ColorMatrix.CalculateAlbedoColorCorrectMatrix(csb, colorOffset);

    public static bool IsNeutralWhiteOrBlack(System.Numerics.Vector4 v)
        => Source2ColorMatrix.IsNeutralWhiteOrBlack(v);

    #endregion

    /// <summary>
    /// Reads a .vmat_c file from the VPK and constructs the corresponding Godot Material.
    /// Checks HeroMaterialManager for bespoke hero overrides before falling back to generic
    /// Source 2 archetype builders (Glass, DynamicFX, VertexColorPbr, StandardPbr).
    /// </summary>
    public static Godot.Material CreateMaterialFromVmat(Package package, string vmatPath, string meshName = null)
    {
        if (package == null || string.IsNullOrWhiteSpace(vmatPath)) return null;
        if (!vmatPath.EndsWith("_c")) vmatPath += "_c";

        // 1. Check for bespoke hero material overrides (e.g. Lash sparkles, Wraith cards)
        var customHeroMat = HeroMaterialManager.TryCreateCustomMaterial(null, package, vmatPath, meshName);
        if (customHeroMat != null)
        {
            return customHeroMat;
        }

        // 2. Locate VMAT resource inside VPK
        var entry = FindVmatEntry(package, vmatPath);
        if (entry == null)
        {
            if (vmatPath.Contains("jitter02", StringComparison.OrdinalIgnoreCase))
            {
                string fallbackPath = vmatPath.Replace("jitter02", "jitter01", StringComparison.OrdinalIgnoreCase)
                                              .Replace("Jitter02", "Jitter01", StringComparison.OrdinalIgnoreCase);
                return CreateMaterialFromVmat(package, fallbackPath, meshName);
            }

            GD.PrintErr($"[Source2MaterialHelper] VMAT file not found: {vmatPath}");
            return null;
        }

        package.ReadEntry(entry, out byte[] data);
        using var resource = new ValveResourceFormat.Resource();
        using var ms = new MemoryStream(data);
        resource.Read(ms);

        if (resource.ResourceType != ResourceType.Material)
        {
            return null;
        }

        var matResource = (VrfMaterial)resource.DataBlock;
        string vmatLower = vmatPath.ToLowerInvariant();

        // 3. Archetype A: Translucent glass, domes, and lenses (Dynamo, Paradox, Paige, generic glass)
        var glassParams = VmatColorExtractor.ExtractGlass(matResource, vmatPath);
        if (GlassMaterialBuilder.IsApplicable(matResource, vmatPath, glassParams))
        {
            return GlassMaterialBuilder.Build(package, matResource, vmatPath, glassParams);
        }

        // 4. Archetype B: Dynamic procedural FX (Billy jitter, Infernus flame ribbons, Vindicta aura, scrolling UVs)
        if (DynamicFxMaterialBuilder.IsApplicable(matResource, vmatPath))
        {
            return DynamicFxMaterialBuilder.Build(package, matResource, vmatPath);
        }

        // 5. Archetype C: Vertex-colored head & hair PBR (Wraith head, Mirage turban/hair)
        var albedoParams = VmatColorExtractor.ExtractAlbedo(matResource, vmatPath, glassParams.IsGlass, glassParams.Opacity);
        bool isFur = vmatLower.Contains("fur") || vmatLower.Contains("shawl") || (meshName != null && (meshName.Contains("fur", StringComparison.OrdinalIgnoreCase) || meshName.Contains("shawl", StringComparison.OrdinalIgnoreCase)));
        if (VertexColorPbrMaterialBuilder.IsApplicable(matResource, vmatPath, albedoParams.ColorTexturePath, isFur))
        {
            return VertexColorPbrMaterialBuilder.Build(package, matResource, vmatPath);
        }

        // 6. Archetype D: Standard PBR surface (character clothing, body, skin, weapons)
        return StandardPbrMaterialBuilder.Build(package, matResource, vmatPath, meshName, glassParams);
    }

    /// <summary>
    /// Extracts the dynamic glow color for energy and self-illumination layers.
    /// Prioritizes self-illum tint, NPR transmissive color, explicit non-neutral tints,
    /// dynamic texture mipmap sampling, and falling back to the hero's registered signature color.
    /// </summary>
    public static Color ExtractDynamicGlowColor(VrfMaterial vmat, string vmatPath, Package package = null)
    {
        if (vmat == null) return Colors.White;
        string vLower = vmatPath?.ToLowerInvariant() ?? "";

        // Priority 1: Self-illumination tint holds the true emissive/fire color
        if (vmat.VectorParams.TryGetValue("g_vSelfIllumTint1", out var siTint1) && !IsNeutralWhiteOrBlack(siTint1))
            return OnColorResolved(new Color(siTint1.X, siTint1.Y, siTint1.Z, 1.0f), vLower);
        if (vmat.VectorParams.TryGetValue("g_vSelfIllumTint", out var siTint0) && !IsNeutralWhiteOrBlack(siTint0))
            return OnColorResolved(new Color(siTint0.X, siTint0.Y, siTint0.Z, 1.0f), vLower);
        if (vmat.VectorParams.TryGetValue("TextureSelfIllumTint1", out var siTintT1) && !IsNeutralWhiteOrBlack(siTintT1))
            return OnColorResolved(new Color(siTintT1.X, siTintT1.Y, siTintT1.Z, 1.0f), vLower);
        if (vmat.VectorParams.TryGetValue("TextureSelfIllumTint", out var siTintT0) && !IsNeutralWhiteOrBlack(siTintT0))
            return OnColorResolved(new Color(siTintT0.X, siTintT0.Y, siTintT0.Z, 1.0f), vLower);

        // Priority 2: NPR Transmissive color
        if (vmat.VectorParams.TryGetValue("TextureNprTramsissiveColor1", out var nprColor1) && !IsNeutralWhiteOrBlack(nprColor1))
            return OnColorResolved(new Color(nprColor1.X, nprColor1.Y, nprColor1.Z, 1.0f), vLower);
        if (vmat.VectorParams.TryGetValue("TextureNprTransmissiveColor1", out var nprColor1_alt) && !IsNeutralWhiteOrBlack(nprColor1_alt))
            return OnColorResolved(new Color(nprColor1_alt.X, nprColor1_alt.Y, nprColor1_alt.Z, 1.0f), vLower);
        if (vmat.VectorParams.TryGetValue("TextureNprTramsissiveColor", out var nprColor0) && !IsNeutralWhiteOrBlack(nprColor0))
            return OnColorResolved(new Color(nprColor0.X, nprColor0.Y, nprColor0.Z, 1.0f), vLower);
        if (vmat.VectorParams.TryGetValue("TextureNprTransmissiveColor", out var nprColor0_alt) && !IsNeutralWhiteOrBlack(nprColor0_alt))
            return OnColorResolved(new Color(nprColor0_alt.X, nprColor0_alt.Y, nprColor0_alt.Z, 1.0f), vLower);
        if (vmat.VectorParams.TryGetValue("TextureTransmissiveColor1", out var tcTrans1) && !IsNeutralWhiteOrBlack(tcTrans1))
            return OnColorResolved(new Color(tcTrans1.X, tcTrans1.Y, tcTrans1.Z, 1.0f), vLower);
        if (vmat.VectorParams.TryGetValue("TextureTransmissiveColor", out var tcTrans0) && !IsNeutralWhiteOrBlack(tcTrans0))
            return OnColorResolved(new Color(tcTrans0.X, tcTrans0.Y, tcTrans0.Z, 1.0f), vLower);

        // Priority 3: General color tint (if explicitly tinted away from pure white/black/grey)
        if (vmat.VectorParams.TryGetValue("g_vColorTint1", out var colTint1) && !IsNeutralWhiteOrBlack(colTint1))
            return OnColorResolved(new Color(colTint1.X, colTint1.Y, colTint1.Z, 1.0f), vLower);
        if (vmat.VectorParams.TryGetValue("g_vColorTint", out var colTint0) && !IsNeutralWhiteOrBlack(colTint0))
            return OnColorResolved(new Color(colTint0.X, colTint0.Y, colTint0.Z, 1.0f), vLower);
        if (vmat.VectorParams.TryGetValue("TextureColor1", out var tcVec1) && !IsNeutralWhiteOrBlack(tcVec1))
            return OnColorResolved(new Color(tcVec1.X, tcVec1.Y, tcVec1.Z, 1.0f), vLower);
        if (vmat.VectorParams.TryGetValue("TextureColor", out var tcVec0) && !IsNeutralWhiteOrBlack(tcVec0))
            return OnColorResolved(new Color(tcVec0.X, tcVec0.Y, tcVec0.Z, 1.0f), vLower);

        // Priority 4: Dynamic 1x1 mipmap extraction from compiled texture in VPK
        if (package != null)
        {
            string texPath = Source2TextureLoader.GetTextureParam(vmat, "g_tColor")
                          ?? Source2TextureLoader.GetTextureParam(vmat, "TextureColor")
                          ?? Source2TextureLoader.GetTextureParam(vmat, "g_tSelfIllumMask")
                          ?? Source2TextureLoader.GetTextureParam(vmat, "TextureTranslucency");
            if (!string.IsNullOrEmpty(texPath))
            {
                var repColor = Source2TextureLoader.ExtractRepresentativeColor(package, texPath);
                if (repColor.HasValue)
                {
                    return OnColorResolved(repColor.Value, vLower);
                }
            }
        }

        // Priority 5: Hero-specific signature fallback from HeroMaterialManager
        var signatureColor = HeroMaterialManager.GetSignatureGlowColor(null, vmatPath);
        if (signatureColor.HasValue)
        {
            return signatureColor.Value;
        }

        return Colors.White;
    }

    private static Color OnColorResolved(Color color, string vmatLower)
    {
        if (vmatLower.Contains("ghost") || vmatLower.Contains("geist"))
        {
            DeadlockPlayground.Materials.Heroes.LadyGeistMaterialConfig.SetDynamicArmColor(color);
        }
        return color;
    }

    private static PackageEntry FindVmatEntry(Package package, string vmatPath)
    {
        var entry = package.FindEntry(vmatPath);
        if (entry != null) return entry;

        if (!vmatPath.StartsWith("materials/", StringComparison.OrdinalIgnoreCase))
        {
            entry = package.FindEntry("materials/" + vmatPath);
            if (entry != null) return entry;
        }
        else
        {
            entry = package.FindEntry(vmatPath.Substring(10));
            if (entry != null) return entry;
        }

        string fileNameOnly = Path.GetFileNameWithoutExtension(vmatPath);
        if (fileNameOnly.EndsWith(".vmat", StringComparison.OrdinalIgnoreCase))
        {
            fileNameOnly = Path.GetFileNameWithoutExtension(fileNameOnly);
        }

        if (package.Entries.TryGetValue("vmat_c", out var matEntries))
        {
            entry = matEntries.Find(e => e.FileName.Equals(fileNameOnly, StringComparison.OrdinalIgnoreCase))
                 ?? matEntries.Find(e => e.FileName.Contains(fileNameOnly, StringComparison.OrdinalIgnoreCase));
        }

        return entry;
    }
}