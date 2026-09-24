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

    public static ImageTexture ExtractVtexToGodot(Package package, string vtexInternalPath, int maxDimension = 1024, bool forceOpaque = true, Package addonPackage = null)
        => Source2TextureLoader.ExtractVtexToGodot(package, vtexInternalPath, maxDimension, forceOpaque, addonPackage);

    public static ImageTexture LoadVtexTexture(Package package, VrfMaterial mat, string paramName, bool forceOpaque = false, Package addonPackage = null)
        => Source2TextureLoader.LoadVtexTexture(package, mat, paramName, forceOpaque, addonPackage);

    public static ImageTexture LoadVtexTexture(Package package, string texPath, bool forceOpaque = false, Package addonPackage = null)
        => Source2TextureLoader.LoadVtexTexture(package, texPath, forceOpaque, addonPackage);

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
    /// Reads a .vmat_c file from the active Addon VPK (Priority 1) or Base Game VPK (Priority 2)
    /// and constructs the corresponding Godot Material.
    /// Checks HeroMaterialManager for bespoke hero overrides before falling back to generic
    /// Source 2 archetype builders (Glass, DynamicFX, VertexColorPbr, StandardPbr).
    /// </summary>
    public static Godot.Material CreateMaterialFromVmat(Package package, string vmatPath, string meshName = null, Package addonPackage = null, string heroName = null)
    {
        if ((package == null && addonPackage == null) || string.IsNullOrWhiteSpace(vmatPath)) return null;
        if (!vmatPath.EndsWith("_c")) vmatPath += "_c";

        // 1. Check for bespoke hero material overrides (e.g. Lash sparkles, Wraith cards, Lady Geist shawl)
        var customHeroMat = HeroMaterialManager.TryCreateCustomMaterial(heroName, package, vmatPath, meshName, addonPackage);
        if (customHeroMat != null)
        {
            return customHeroMat;
        }

        // 3. Locate VMAT resource: Priority 1 = Active Addon VPK, Priority 2 = Base Game VPK
        PackageEntry entry = null;
        Package targetPackage = null;

        if (addonPackage != null)
        {
            entry = FindVmatEntry(addonPackage, vmatPath);
            if (entry != null)
            {
                targetPackage = addonPackage;
                GD.Print($"  [AddonMaterial] Injected custom material from addon: {entry.DirectoryName}/{entry.FileName}.{entry.TypeName}");
            }
        }

        if (entry == null && package != null)
        {
            entry = FindVmatEntry(package, vmatPath);
            if (entry != null)
            {
                targetPackage = package;
            }
        }

        if (entry == null || targetPackage == null)
        {
            if (vmatPath.Contains("jitter02", StringComparison.OrdinalIgnoreCase))
            {
                string fallbackPath = vmatPath.Replace("jitter02", "jitter01", StringComparison.OrdinalIgnoreCase)
                                              .Replace("Jitter02", "Jitter01", StringComparison.OrdinalIgnoreCase);
                return CreateMaterialFromVmat(package, fallbackPath, meshName, addonPackage, heroName);
            }

            if (vmatPath.Contains("geist_shawl", StringComparison.OrdinalIgnoreCase) ||
                (meshName != null && meshName.Contains("geist_shawl", StringComparison.OrdinalIgnoreCase)))
            {
                var shawlMat = DeadlockPlayground.Materials.Heroes.LadyGeistMaterialConfig.CreateShawlMaterial(package, vmatPath, meshName, addonPackage);
                if (shawlMat != null) return shawlMat;
            }

            GD.PrintErr($"[Source2MaterialHelper] VMAT file not found in Addon or Base VPK: {vmatPath}");
            return null;
        }

        targetPackage.ReadEntry(entry, out byte[] data);
        using var resource = new ValveResourceFormat.Resource();
        using var ms = new MemoryStream(data);
        resource.Read(ms);

        if (resource.ResourceType != ResourceType.Material)
        {
            return null;
        }

        var matResource = (VrfMaterial)resource.DataBlock;
        string vmatLower = vmatPath.ToLowerInvariant();

        Godot.Material createdMat = null;
        var glassParams = VmatColorExtractor.ExtractGlass(matResource, vmatPath);

        // 3. Archetype 0: Inverted-hull solid outline meshes (Viscous bodyoutline)
        bool isSolidOutline = vmatLower.Contains("viscous_outline")
                           || (meshName != null && (meshName.Contains("body_outline", StringComparison.OrdinalIgnoreCase) || meshName.Contains("bodyoutline", StringComparison.OrdinalIgnoreCase)));
        if (isSolidOutline)
        {
            var outlineShader = Source2ShaderRegistry.GetViscousOutlineShader();
            if (outlineShader != null)
            {
                var outlineMat = new ShaderMaterial { Shader = outlineShader };
                Color tint = new Color(0.407843f, 0.94902f, 0.415686f, 1.0f);
                if (matResource.VectorParams.TryGetValue("TextureColor1", out var tc1) && (tc1.X > 0.001f || tc1.Y > 0.001f || tc1.Z > 0.001f) && !Source2ColorMatrix.IsNeutralWhiteOrBlack(tc1))
                    tint = new Color(tc1.X, tc1.Y, tc1.Z, 1.0f);
                else if (matResource.VectorParams.TryGetValue("TextureColor", out var tc0) && (tc0.X > 0.001f || tc0.Y > 0.001f || tc0.Z > 0.001f) && !Source2ColorMatrix.IsNeutralWhiteOrBlack(tc0))
                    tint = new Color(tc0.X, tc0.Y, tc0.Z, 1.0f);
                else if (matResource.VectorParams.TryGetValue("g_vColorTint1", out var ct1) && !Source2ColorMatrix.IsNeutralWhiteOrBlack(ct1))
                    tint = new Color(ct1.X, ct1.Y, ct1.Z, 1.0f);
                else if (matResource.VectorParams.TryGetValue("g_vColorTint", out var ct0) && !Source2ColorMatrix.IsNeutralWhiteOrBlack(ct0))
                    tint = new Color(ct0.X, ct0.Y, ct0.Z, 1.0f);
                else if (matResource.VectorParams.TryGetValue("g_vSolidOutlineTint1", out var ot1) && !Source2ColorMatrix.IsNeutralWhiteOrBlack(ot1))
                    tint = new Color(ot1.X, ot1.Y, ot1.Z, 1.0f);
                else if (matResource.VectorParams.TryGetValue("g_vSolidOutlineTint", out var ot0) && !Source2ColorMatrix.IsNeutralWhiteOrBlack(ot0))
                    tint = new Color(ot0.X, ot0.Y, ot0.Z, 1.0f);

                float thickness = 0.004f;
                if (matResource.FloatParams.TryGetValue("g_flOutlineThickness1", out var th1)) thickness = th1;
                else if (matResource.FloatParams.TryGetValue("g_flOutlineThickness", out var th0)) thickness = th0;
                else if (matResource.FloatParams.TryGetValue("g_flOutlineWidth1", out var ow1)) thickness = ow1;
                else if (matResource.FloatParams.TryGetValue("g_flOutlineWidth", out var ow0)) thickness = ow0;

                outlineMat.SetShaderParameter("g_vSolidOutlineTint", tint);
                outlineMat.SetShaderParameter("outline_color", tint);
                outlineMat.SetShaderParameter("outline_thickness", thickness);
                outlineMat.RenderPriority = 0;
                outlineMat.SetMeta("PreserveShading", true);
                createdMat = outlineMat;
            }
        }
        // 4. Archetype A: Translucent glass, domes, and lenses (Dynamo, Paradox, Paige, generic glass)
        else if (GlassMaterialBuilder.IsApplicable(matResource, vmatPath, glassParams))
        {
            createdMat = GlassMaterialBuilder.Build(package, matResource, vmatPath, glassParams, addonPackage);
        }
        else
        {
            var albedoParams = VmatColorExtractor.ExtractAlbedo(matResource, vmatPath, glassParams.IsGlass, glassParams.Opacity);
            bool isFur = vmatLower.Contains("fur") || vmatLower.Contains("shawl") || (meshName != null && (meshName.Contains("fur", StringComparison.OrdinalIgnoreCase) || meshName.Contains("shawl", StringComparison.OrdinalIgnoreCase)));

            // 4. Archetype B: Vertex-colored head & hair PBR (Wraith head, Mirage turban/hair, Vindicta limbs)
            if (VertexColorPbrMaterialBuilder.IsApplicable(matResource, vmatPath, albedoParams.ColorTexturePath, isFur))
            {
                createdMat = VertexColorPbrMaterialBuilder.Build(package, matResource, vmatPath, addonPackage);
            }
            // 5. Archetype C: Dynamic procedural FX (Billy jitter, Infernus flame ribbons, Vindicta aura, scrolling UVs)
            else if (DynamicFxMaterialBuilder.IsApplicable(matResource, vmatPath))
            {
                createdMat = DynamicFxMaterialBuilder.Build(package, matResource, vmatPath, addonPackage);
            }
            else
            {
                // 6. Archetype D: Standard PBR surface (character clothing, body, skin, weapons)
                createdMat = StandardPbrMaterialBuilder.Build(package, matResource, vmatPath, meshName, glassParams, addonPackage);
            }
        }

        if (createdMat != null)
        {
            createdMat.SetMeta("OriginalVmatPath", vmatPath);
            if (!string.IsNullOrEmpty(matResource.ShaderName))
            {
                createdMat.SetMeta("OriginalShader", matResource.ShaderName);
            }
            if (matResource.TextureParams != null)
            {
                foreach (var kvp in matResource.TextureParams)
                {
                    createdMat.SetMeta($"TextureParam_{kvp.Key}", kvp.Value);
                }
            }

            string colorTex = Source2TextureLoader.GetTextureParam(matResource, "g_tColor")
                           ?? Source2TextureLoader.GetTextureParam(matResource, "TextureColor")
                           ?? Source2TextureLoader.GetTextureParam(matResource, "g_tColor1");
            if (!string.IsNullOrEmpty(colorTex))
            {
                string norm = colorTex.Replace('\\', '/').Trim().TrimStart('/');
                if (norm.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase))
                {
                    norm += "_c";
                }
                else if (!norm.EndsWith(".vtex_c", StringComparison.OrdinalIgnoreCase))
                {
                    norm += ".vtex_c";
                }
                createdMat.SetMeta("OriginalColorVtexCPath", norm);
            }
        }

        return createdMat;
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

    public static PackageEntry FindVmatEntry(Package package, string vmatPath)
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