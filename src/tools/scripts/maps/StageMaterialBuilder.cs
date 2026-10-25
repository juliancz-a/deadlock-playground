using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using DeadlockPlayground.Materials;
using VrfMaterial = ValveResourceFormat.ResourceTypes.Material;
using Material = Godot.Material;

namespace DeadlockPlayground.Maps;

public class MaterialDiagnosticInfo
{
    public string MaterialPath { get; set; } = "";
    public string ShaderName { get; set; } = "";
    public Color AlbedoTintA { get; set; } = Colors.White;
    public Color AlbedoTintB { get; set; } = Colors.White;
    public bool HasVertexColor { get; set; }
    public bool HasTintMask { get; set; }
    public Dictionary<string, string> TextureParams { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> VectorParams { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, float> FloatParams { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> IntParams { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Fast material builder and cache for Deadlock map environments.
/// Converts Source 2 .vmat_c materials into unified Godot StandardMaterial3D instances
/// using native block-compressed textures (BC7/BC1) without shader compilation stalls.
/// </summary>
public static class StageMaterialBuilder
{
    private static readonly Dictionary<string, Material> _materialCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _loadedTextures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<string>> _missingTextures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, MaterialDiagnosticInfo> _diagnosticsCache = new(StringComparer.OrdinalIgnoreCase);

    private static Shader _stagePbrShader;
    private static Shader _stageOverlayShader;

    private static void EnsureShadersLoaded()
    {
        if (_stagePbrShader == null)
        {
            _stagePbrShader = GD.Load<Shader>("res://assets/shaders/maps/source2_map_stage.gdshader");
        }
        if (_stageOverlayShader == null)
        {
            _stageOverlayShader = GD.Load<Shader>("res://assets/shaders/maps/source2_map_overlay.gdshader");
        }
    }

    /// <summary>
    /// Clears the material cache and frees material references. Call when unloading maps.
    /// </summary>
    public static void CleanCache()
    {
        _materialCache.Clear();
        _loadedTextures.Clear();
        _missingTextures.Clear();
        _diagnosticsCache.Clear();
    }

    /// <summary>
    /// Retrieves diagnostic metadata parsed from a .vmat_c file.
    /// </summary>
    public static MaterialDiagnosticInfo GetMaterialDiagnostic(string vmatPath)
    {
        if (string.IsNullOrWhiteSpace(vmatPath)) return null;
        if (vmatPath.StartsWith("resource:", StringComparison.OrdinalIgnoreCase)) vmatPath = vmatPath.Substring(9);
        vmatPath = vmatPath.Trim('"', '\'', ' ').Replace('\\', '/');

        if (_diagnosticsCache.TryGetValue(vmatPath, out var info)) return info;

        string cPath = vmatPath.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? vmatPath : vmatPath + "_c";
        if (_diagnosticsCache.TryGetValue(cPath, out info)) return info;

        if (vmatPath.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
        {
            string noC = vmatPath.Substring(0, vmatPath.Length - 2);
            if (_diagnosticsCache.TryGetValue(noC, out info)) return info;
        }

        return null;
    }

    /// <summary>
    /// Gets the count of materials currently cached.
    /// </summary>
    public static int CachedMaterialCount => _materialCache.Count;

    /// <summary>
    /// Gets the count of unique textures successfully loaded into GPU memory.
    /// </summary>
    public static int LoadedTextureCount => _loadedTextures.Count;

    /// <summary>
    /// Gets the count of unique textures that failed to resolve across all packages.
    /// </summary>
    public static int MissingTextureCount => _missingTextures.Count;

    /// <summary>
    /// Read-only dictionary of missing texture paths mapped to the materials that requested them.
    /// </summary>
    public static IReadOnlyDictionary<string, HashSet<string>> MissingTextures => _missingTextures;

    /// <summary>
    /// Resolves or builds a shared Godot Material for a Source 2 .vmat_c path.
    /// Uses custom stage PBR and overlay shaders with GPU HemiOct normal decoding, roughness extraction,
    /// and two-layer material blending for Source 2 environment blend surfaces.
    /// Searches in hideoutPackage first, then falls back to baseGamePackage (pak01_dir.vpk).
    /// </summary>
    public static Material GetOrBuildMaterial(
        string vmatPath,
        Package hideoutPackage,
        Package baseGamePackage,
        MapTextureQuality quality = MapTextureQuality.Medium,
        Package addonPackage = null)
    {
        if (string.IsNullOrWhiteSpace(vmatPath)) return GetFallbackMaterial();

        if (vmatPath.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
        {
            vmatPath = vmatPath.Substring(9);
        }
        vmatPath = vmatPath.Trim('"', '\'', ' ');

        string cacheKey = $"mat_{(int)quality}_{vmatPath}";
        if (_materialCache.TryGetValue(cacheKey, out var cachedMat) && GodotObject.IsInstanceValid(cachedMat))
        {
            return cachedMat;
        }

        // Locate .vmat_c in VPKs
        string cPath = vmatPath.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? vmatPath : vmatPath + "_c";
        Package targetPkg = null;
        PackageEntry entry = null;

        if (addonPackage != null)
        {
            entry = addonPackage.FindEntry(cPath);
            if (entry != null) targetPkg = addonPackage;
        }

        if (entry == null && hideoutPackage != null)
        {
            entry = hideoutPackage.FindEntry(cPath) ?? hideoutPackage.FindEntry("maps/dl_hideout/" + Path.GetFileName(cPath));
            if (entry != null) targetPkg = hideoutPackage;
        }

        if (entry == null && baseGamePackage != null)
        {
            entry = baseGamePackage.FindEntry(cPath);
            if (entry != null) targetPkg = baseGamePackage;
        }

        if (entry == null && Source2TextureLoader.CorePackage != null)
        {
            entry = Source2TextureLoader.CorePackage.FindEntry(cPath);
            if (entry != null) targetPkg = Source2TextureLoader.CorePackage;
        }

        if (entry == null || targetPkg == null)
        {
            var fallbackDiag = new MaterialDiagnosticInfo
            {
                MaterialPath = vmatPath,
                ShaderName = "NotFound / Fallback",
                AlbedoTintA = new Color(0.72f, 0.70f, 0.68f)
            };
            _diagnosticsCache[vmatPath] = fallbackDiag;
            var fallback = GetFallbackMaterial();
            _materialCache[cacheKey] = fallback;
            return fallback;
        }

        // Read VMAT resource
        targetPkg.ReadEntry(entry, out byte[] matBytes);
        using var res = new ValveResourceFormat.Resource();
        using var ms = new MemoryStream(matBytes);
        res.Read(ms);

        if (res.DataBlock is not VrfMaterial vrfMat)
        {
            var fallbackDiag = new MaterialDiagnosticInfo
            {
                MaterialPath = vmatPath,
                ShaderName = "InvalidDataBlock / Fallback",
                AlbedoTintA = new Color(0.72f, 0.70f, 0.68f)
            };
            _diagnosticsCache[vmatPath] = fallbackDiag;
            var fallback = GetFallbackMaterial();
            _materialCache[cacheKey] = fallback;
            return fallback;
        }

        EnsureShadersLoaded();

        // Check if material uses vertex color blending
        bool hasVertexColor = (vrfMat.FloatParams.TryGetValue("g_fVertexColorStrength1", out var vcs) && vcs > 0.05f)
                           || (vrfMat.IntParams.TryGetValue("F_ENABLE_VERTEX_COLOR", out var fvc) && fvc == 1)
                           || (vrfMat.IntParams.TryGetValue("F_PAINT_VERTEX_COLORS", out var fpvc) && fpvc == 1)
                           || (vrfMat.IntParams.TryGetValue("F_VERTEX_COLOR", out var fvc2) && fvc2 == 1)
                           || vrfMat.ShaderName.Equals("environment_blend.vfx", StringComparison.OrdinalIgnoreCase)
                           || vrfMat.ShaderName.Equals("citadel_environment_blend.vfx", StringComparison.OrdinalIgnoreCase);

        // Diagnostic metadata recording
        var diag = new MaterialDiagnosticInfo
        {
            MaterialPath = vmatPath,
            ShaderName = vrfMat.ShaderName,
            HasVertexColor = hasVertexColor
        };
        foreach (var (k, v) in vrfMat.TextureParams) diag.TextureParams[k] = v;
        foreach (var (k, v) in vrfMat.VectorParams) diag.VectorParams[k] = v.ToString();
        foreach (var (k, v) in vrfMat.FloatParams) diag.FloatParams[k] = v;
        foreach (var (k, v) in vrfMat.IntParams) diag.IntParams[k] = v;

        // Extract Material Color Tint (g_vColorTint, g_vColorTint1, TextureColor1, Color, etc.)
        Color tint = ExtractColorTint(vrfMat);
        Color tintB = ExtractColorTintB(vrfMat);
        diag.AlbedoTintA = tint;
        diag.AlbedoTintB = tintB;

        // Tint Mask detection (F_TINT_MASK, g_bMaskColorTint1, g_tTintMask)
        bool hasTintMask = (vrfMat.IntParams.TryGetValue("F_TINT_MASK", out var ftm) && ftm == 1)
                        || (vrfMat.IntParams.TryGetValue("g_bMaskColorTint1", out var bmct) && bmct == 1)
                        || vrfMat.TextureParams.ContainsKey("g_tTintMask")
                        || vrfMat.TextureParams.ContainsKey("TextureTintMask");
        diag.HasTintMask = hasTintMask;
        _diagnosticsCache[vmatPath] = diag;

        // Check Transparency, Translucency, Alpha Testing, and Overlays
        bool isGlass = vrfMat.ShaderName.Contains("glass", StringComparison.OrdinalIgnoreCase)
                    || vmatPath.Contains("glass", StringComparison.OrdinalIgnoreCase);

        // Strict Decal & Overlay detection:
        // Core architectural geometry (walls, floors, buildings, ceilings, tiles) must NEVER use the overlay shader!
        string normalizedVmat = vmatPath.Replace('\\', '/').ToLowerInvariant();
        bool isArchitectural = vrfMat.ShaderName.Contains("blend", StringComparison.OrdinalIgnoreCase)
                            || vrfMat.ShaderName.Contains("world", StringComparison.OrdinalIgnoreCase)
                            || normalizedVmat.Contains("/architecture/")
                            || normalizedVmat.Contains("/buildings/")
                            || normalizedVmat.Contains("/floors/")
                            || normalizedVmat.Contains("/walls/")
                            || normalizedVmat.Contains("/ceilings/")
                            || normalizedVmat.Contains("/tiles/")
                            || normalizedVmat.Contains("/wood_")
                            || normalizedVmat.Contains("/stone_");

        bool isDecalShader = vrfMat.ShaderName.Equals("citadel_overlay.vfx", StringComparison.OrdinalIgnoreCase)
                          || vrfMat.ShaderName.Equals("static_overlay.vfx", StringComparison.OrdinalIgnoreCase)
                          || vrfMat.ShaderName.Equals("projected_decals.vfx", StringComparison.OrdinalIgnoreCase)
                          || vrfMat.ShaderName.Contains("overlay.vfx", StringComparison.OrdinalIgnoreCase)
                          || vrfMat.ShaderName.Contains("decal.vfx", StringComparison.OrdinalIgnoreCase);

        bool isDecalPath = normalizedVmat.Contains("/decals/")
                        || normalizedVmat.Contains("/overlays/")
                        || normalizedVmat.Contains("/graffiti/")
                        || normalizedVmat.Contains("/sigils/")
                        || normalizedVmat.Contains("/stickers/")
                        || Path.GetFileName(normalizedVmat).StartsWith("decal_")
                        || Path.GetFileName(normalizedVmat).StartsWith("overlay_");

        bool isDecalOrOverlay = !isArchitectural && (isDecalShader || isDecalPath);

        bool isAlphaTested = (vrfMat.IntParams.TryGetValue("F_ALPHA_TEST", out var fat) && fat == 1)
                          || vrfMat.FloatParams.ContainsKey("g_flAlphaTestReference1")
                          || vrfMat.FloatParams.ContainsKey("g_flAlphaTestReference");

        // Special Glass Material
        if (isGlass)
        {
            diag.ShaderName = "Glass (StandardMaterial3D Translucent)";
            diag.AlbedoTintA = new Color(0.75f, 0.88f, 0.95f, 0.35f);
            var glassMat = new StandardMaterial3D
            {
                ResourceName = Path.GetFileNameWithoutExtension(vmatPath),
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                Transparency = BaseMaterial3D.TransparencyEnum.AlphaDepthPrePass,
                Roughness = 0.15f,
                Metallic = 0.15f,
                MetallicSpecular = 0.6f,
                AlbedoColor = new Color(0.75f, 0.88f, 0.95f, 0.35f)
            };
            _materialCache[cacheKey] = glassMat;
            return glassMat;
        }

        string tintMaskPath = Source2TextureLoader.GetTextureParam(vrfMat, "g_tTintMask")
                           ?? Source2TextureLoader.GetTextureParam(vrfMat, "TextureTintMask");
        Texture2D tintMaskTex = (!string.IsNullOrEmpty(tintMaskPath) && !IsDummyTexture(tintMaskPath))
            ? LoadTextureWithFallbacks(tintMaskPath, vmatPath, hideoutPackage, baseGamePackage, quality, addonPackage)
            : null;

        // Resolve Primary and Secondary Albedo Textures (for multi-blend architectural surfaces)
        string primaryTexPath = ResolvePrimaryAlbedoPath(vrfMat);
        string secondaryTexPath = ResolveSecondaryAlbedoPath(vrfMat);

        // If primary texture is dummy or missing, but secondary texture is valid, promote secondary to primary!
        if ((string.IsNullOrEmpty(primaryTexPath) || IsDummyTexture(primaryTexPath)) &&
            !string.IsNullOrEmpty(secondaryTexPath) && !IsDummyTexture(secondaryTexPath))
        {
            primaryTexPath = secondaryTexPath;
            secondaryTexPath = null;
        }

        bool isDummyAlbedo = string.IsNullOrEmpty(primaryTexPath) || IsDummyTexture(primaryTexPath);
        Texture2D albedoTex = (!string.IsNullOrEmpty(primaryTexPath) && !isDummyAlbedo)
            ? LoadTextureWithFallbacks(primaryTexPath, vmatPath, hideoutPackage, baseGamePackage, quality, addonPackage)
            : null;

        // If primary texture failed to load from VPKs, try secondary texture as fallback primary
        if (albedoTex == null && !string.IsNullOrEmpty(secondaryTexPath) && !IsDummyTexture(secondaryTexPath))
        {
            albedoTex = LoadTextureWithFallbacks(secondaryTexPath, vmatPath, hideoutPackage, baseGamePackage, quality, addonPackage);
            secondaryTexPath = null;
        }

        // Load secondary texture for Layer 1 blending if present
        Texture2D albedoTexB = (!string.IsNullOrEmpty(secondaryTexPath) && !IsDummyTexture(secondaryTexPath))
            ? LoadTextureWithFallbacks(secondaryTexPath, vmatPath, hideoutPackage, baseGamePackage, quality, addonPackage)
            : null;

        // Resolve Normal / Roughness Texture
        string normalTexPath = Source2TextureLoader.GetTextureParam(vrfMat, "g_tNormalRoughness")
                            ?? Source2TextureLoader.GetTextureParam(vrfMat, "g_tNormalRoughness1")
                            ?? Source2TextureLoader.GetTextureParam(vrfMat, "g_tNormalRoughnessA")
                            ?? Source2TextureLoader.GetTextureParam(vrfMat, "g_tNormal")
                            ?? Source2TextureLoader.GetTextureParam(vrfMat, "TextureNormal");
        Texture2D normTex = (!string.IsNullOrEmpty(normalTexPath) && !IsDummyTexture(normalTexPath))
            ? LoadTextureWithFallbacks(normalTexPath, vmatPath, hideoutPackage, baseGamePackage, quality, addonPackage)
            : null;

        // Case 1: Decal or Wall Overlay (RenderPriority = 2, DepthDrawNever, Vertex normal offset)
        if (isDecalOrOverlay && _stageOverlayShader != null)
        {
            var overlayMat = new ShaderMaterial
            {
                ResourceName = Path.GetFileNameWithoutExtension(vmatPath),
                Shader = _stageOverlayShader,
                RenderPriority = 2
            };

            if (albedoTex != null)
            {
                overlayMat.SetShaderParameter("texture_albedo", albedoTex);
                overlayMat.SetShaderParameter("has_albedo_texture", true);
            }
            else
            {
                overlayMat.SetShaderParameter("has_albedo_texture", false);
            }

            overlayMat.SetShaderParameter("albedo_tint", tint != Colors.White ? tint : Colors.White);

            if (normTex != null)
            {
                overlayMat.SetShaderParameter("texture_normal_roughness", normTex);
                overlayMat.SetShaderParameter("has_normal_texture", true);
            }
            else
            {
                overlayMat.SetShaderParameter("has_normal_texture", false);
            }

            if (hasVertexColor)
            {
                overlayMat.SetShaderParameter("use_vertex_color", true);
            }

            _materialCache[cacheKey] = overlayMat;
            return overlayMat;
        }

        // Case 2: Standard PBR Map Surface with GPU HemiOct Normal Decoding & Roughness
        // (100% Opaque Depth Pass with hardware Z-buffer occlusion)
        if (_stagePbrShader != null)
        {
            var pbrMat = new ShaderMaterial
            {
                ResourceName = Path.GetFileNameWithoutExtension(vmatPath),
                Shader = _stagePbrShader
            };

            if (albedoTex != null)
            {
                pbrMat.SetShaderParameter("texture_albedo", albedoTex);
                pbrMat.SetShaderParameter("has_albedo_texture", true);
            }
            else
            {
                pbrMat.SetShaderParameter("has_albedo_texture", false);
            }

            pbrMat.SetShaderParameter("albedo_tint", tint);
            pbrMat.SetShaderParameter("albedo_tint_b", tintB);

            if (hasTintMask)
            {
                pbrMat.SetShaderParameter("has_tint_mask", true);
                if (tintMaskTex != null)
                {
                    pbrMat.SetShaderParameter("has_dedicated_tint_mask", true);
                    pbrMat.SetShaderParameter("texture_tint_mask", tintMaskTex);
                }
            }

            if (hasVertexColor)
            {
                pbrMat.SetShaderParameter("use_vertex_color", true);
            }

            // Two-layer material blend (Layer B)
            if (albedoTexB != null)
            {
                pbrMat.SetShaderParameter("texture_albedo_b", albedoTexB);
                pbrMat.SetShaderParameter("has_albedo_b", true);
            }
            else
            {
                pbrMat.SetShaderParameter("has_albedo_b", false);
            }

            if (normTex != null)
            {
                pbrMat.SetShaderParameter("texture_normal_roughness", normTex);
                pbrMat.SetShaderParameter("has_normal_texture", true);
            }
            else
            {
                pbrMat.SetShaderParameter("has_normal_texture", false);
            }

            if (isAlphaTested)
            {
                pbrMat.SetShaderParameter("is_alpha_tested", true);
                float scissorRef = 0.5f;
                if (vrfMat.FloatParams.TryGetValue("g_flAlphaTestReference1", out var atr1)) scissorRef = atr1;
                else if (vrfMat.FloatParams.TryGetValue("g_flAlphaTestReference", out var atr)) scissorRef = atr;
                pbrMat.SetShaderParameter("alpha_scissor_threshold", Mathf.Clamp(scissorRef, 0.05f, 0.95f));
            }

            // Resolve Emissive / Self-Illum
            bool hasSelfIllumFeature = (vrfMat.IntParams.TryGetValue("F_SELF_ILLUM", out var fsi) && fsi == 1)
                                    || (vrfMat.IntParams.TryGetValue("F_EMISSIVE", out var fe) && fe == 1);

            float selfIllumScale = 0f;
            if (vrfMat.FloatParams.TryGetValue("g_flSelfIllumScale", out var sis)) selfIllumScale = Math.Max(selfIllumScale, sis);
            if (vrfMat.FloatParams.TryGetValue("g_flSelfIllumScale1", out var sis1)) selfIllumScale = Math.Max(selfIllumScale, sis1);
            if (vrfMat.FloatParams.TryGetValue("g_flEmissionScale", out var ems)) selfIllumScale = Math.Max(selfIllumScale, ems);

            string emissiveTexPath = Source2TextureLoader.GetTextureParam(vrfMat, "g_tSelfIllumMask")
                                  ?? Source2TextureLoader.GetTextureParam(vrfMat, "TextureSelfIllumMask")
                                  ?? Source2TextureLoader.GetTextureParam(vrfMat, "g_tEmissive")
                                  ?? Source2TextureLoader.GetTextureParam(vrfMat, "g_tSelfIllumTintMask");

            // Only enable self-illum if the texture mask successfully loaded!
            if (hasSelfIllumFeature && selfIllumScale > 0.01f && !string.IsNullOrEmpty(emissiveTexPath) && !IsDummyTexture(emissiveTexPath))
            {
                var emTex = LoadTextureWithFallbacks(emissiveTexPath, vmatPath, hideoutPackage, baseGamePackage, quality, addonPackage);
                if (emTex != null)
                {
                    pbrMat.SetShaderParameter("has_self_illum_texture", true);
                    pbrMat.SetShaderParameter("texture_self_illum", emTex);
                    Color emColor = ExtractEmissionTint(vrfMat);
                    pbrMat.SetShaderParameter("self_illum_tint", new Vector3(emColor.R, emColor.G, emColor.B));
                    pbrMat.SetShaderParameter("self_illum_scale", Mathf.Clamp(selfIllumScale, 0.05f, 1.0f));
                }
            }

            _materialCache[cacheKey] = pbrMat;
            return pbrMat;
        }

        // Fallback StandardMaterial3D if shader failed to load
        var mat = new StandardMaterial3D
        {
            ResourceName = Path.GetFileNameWithoutExtension(vmatPath),
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            Roughness = 0.85f,
            MetallicSpecular = 0.2f,
            VertexColorUseAsAlbedo = false // Never tint with vertex blend weights!
        };
        if (albedoTex != null) mat.AlbedoTexture = albedoTex;
        _materialCache[cacheKey] = mat;
        return mat;
    }

    private static bool IsDummyTexture(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        string p = path.ToLowerInvariant().Replace('\\', '/');
        return p.Contains("materials/default/") ||
               p.Contains("default_white") ||
               p.Contains("default_mask") ||
               p.Contains("default_color") ||
               p.Contains("default_selfillum") ||
               p.Contains("default_normal") ||
               p.Contains("white_color") ||
               p.Contains("error") ||
               p.Contains("dev/reflectivity") ||
               p.Contains("tools/") ||
               p.Contains("1x1") ||
               p.Contains("4x4") ||
               p.Contains("ef8a35db");
    }

    private static string ResolvePrimaryAlbedoPath(VrfMaterial vrfMat)
    {
        if (vrfMat == null) return null;

        string[] priorityKeys = {
            "g_tColorWorldOverlay",
            "g_tColor",
            "g_tColor1",
            "g_tColorA",
            "g_tColor0",
            "TextureColor",
            "TextureColor1",
            "TextureColorA",
            "TextureColor0",
            "g_tBaseTexture",
            "g_tOverlayColor",
            "g_tDecalColor"
        };

        // 1. Search prioritized keys for a non-dummy texture
        foreach (var key in priorityKeys)
        {
            string p = Source2TextureLoader.GetTextureParam(vrfMat, key);
            if (!string.IsNullOrEmpty(p) && !IsDummyTexture(p))
            {
                return p;
            }
        }

        // 2. Scan all texture params for any key containing "Color" or "Albedo" not dummy
        if (vrfMat.TextureParams != null)
        {
            foreach (var (k, v) in vrfMat.TextureParams)
            {
                if (string.IsNullOrEmpty(v)) continue;
                if (k.Contains("Color", StringComparison.OrdinalIgnoreCase) || k.Contains("Albedo", StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsDummyTexture(v) && !k.Contains("ColorB", StringComparison.OrdinalIgnoreCase) && !k.Contains("Color2", StringComparison.OrdinalIgnoreCase))
                    {
                        return v;
                    }
                }
            }
        }

        // 3. Fallback: return dummy texture if present (e.g. 1x1 white for tinted geometry)
        foreach (var key in priorityKeys)
        {
            string p = Source2TextureLoader.GetTextureParam(vrfMat, key);
            if (!string.IsNullOrEmpty(p))
            {
                return p;
            }
        }

        return null;
    }

    private static string ResolveSecondaryAlbedoPath(VrfMaterial vrfMat)
    {
        if (vrfMat == null) return null;

        string[] secondaryKeys = {
            "g_tColorB",
            "g_tColor1",
            "TextureColorB",
            "TextureColor1",
            "g_tColor2",
            "TextureColor2",
            "g_tLayer1Color",
            "g_tLayer2Color"
        };

        foreach (var key in secondaryKeys)
        {
            string p = Source2TextureLoader.GetTextureParam(vrfMat, key);
            if (!string.IsNullOrEmpty(p) && !IsDummyTexture(p))
            {
                return p;
            }
        }

        return null;
    }

    private static Color ExtractColorTint(VrfMaterial vrfMat)
    {
        if (vrfMat?.VectorParams == null) return Colors.White;

        string[] tintKeys = {
            "g_vColorTint",
            "g_vColorTint1",
            "TextureColor",
            "TextureColor1",
            "TextureColorA",
            "TextureColor0",
            "TextureColorB",
            "g_vColorTintA",
            "g_vColorTint0",
            "m_vColorTint",
            "g_vColor",
            "Color",
            "ColorTint",
            "g_vColorTint2",
            "g_vBorderTint1",
            "g_vBorderTint2",
            "g_vBorderTint3",
            "TextureNprTramsissiveColor1",
            "TextureNprTransmissiveColor",
            "g_vCloakColorTint",
            "g_vTexColorTint",
            "g_vDiffuseModulation"
        };

        Color? fallbackWhite = null;

        // Pass 1: Prefer any explicit NON-WHITE tint
        foreach (var key in tintKeys)
        {
            if (vrfMat.VectorParams.TryGetValue(key, out var v))
            {
                if (v.X <= 0.0001f && v.Y <= 0.0001f && v.Z <= 0.0001f)
                {
                    continue;
                }

                float maxComp = Math.Max(v.X, Math.Max(v.Y, v.Z));
                float scale = maxComp > 1.0f ? (1.0f / 255.0f) : 1.0f;

                float r = Mathf.Clamp(v.X * scale, 0f, 1f);
                float g = Mathf.Clamp(v.Y * scale, 0f, 1f);
                float b = Mathf.Clamp(v.Z * scale, 0f, 1f);
                float a = v.W > 0f ? (v.W > 1.0f ? Mathf.Clamp(v.W * scale, 0f, 1f) : Mathf.Clamp(v.W, 0f, 1f)) : 1f;

                var candidate = new Color(r, g, b, a);

                // If non-white (e.g. gold, bronze, green, black, etc.), use it immediately!
                if (r < 0.98f || g < 0.98f || b < 0.98f)
                {
                    return candidate;
                }

                fallbackWhite ??= candidate;
            }
        }

        return fallbackWhite ?? Colors.White;
    }

    private static Color ExtractColorTintB(VrfMaterial vrfMat)
    {
        if (vrfMat?.VectorParams == null) return Colors.White;

        string[] tintBKeys = {
            "g_vColorTint2",
            "TextureColor2",
            "g_vColorTintB",
            "TextureColorB",
            "g_vBorderTint2",
            "g_vBorderTint3",
            "g_vColorTint1"
        };

        Color? fallbackWhite = null;

        foreach (var key in tintBKeys)
        {
            if (vrfMat.VectorParams.TryGetValue(key, out var v))
            {
                if (v.X <= 0.0001f && v.Y <= 0.0001f && v.Z <= 0.0001f) continue;
                float maxComp = Math.Max(v.X, Math.Max(v.Y, v.Z));
                float scale = maxComp > 1.0f ? (1.0f / 255.0f) : 1.0f;
                float r = Mathf.Clamp(v.X * scale, 0f, 1f);
                float g = Mathf.Clamp(v.Y * scale, 0f, 1f);
                float b = Mathf.Clamp(v.Z * scale, 0f, 1f);
                float a = v.W > 0f ? (v.W > 1.0f ? Mathf.Clamp(v.W * scale, 0f, 1f) : Mathf.Clamp(v.W, 0f, 1f)) : 1f;

                var candidate = new Color(r, g, b, a);
                if (r < 0.98f || g < 0.98f || b < 0.98f)
                {
                    return candidate;
                }
                fallbackWhite ??= candidate;
            }
        }

        return fallbackWhite ?? Colors.White;
    }

    private static Color ExtractEmissionTint(VrfMaterial vrfMat)
    {
        if (vrfMat?.VectorParams == null) return Colors.White;

        string[] emTintKeys = { "g_vSelfIllumTint", "g_vSelfIllumTint1", "g_vSelfIllumFresnelMaskTint1" };
        foreach (var key in emTintKeys)
        {
            if (vrfMat.VectorParams.TryGetValue(key, out var v))
            {
                if (v.X <= 0.0001f && v.Y <= 0.0001f && v.Z <= 0.0001f) continue;
                float maxComp = Math.Max(v.X, Math.Max(v.Y, v.Z));
                float scale = maxComp > 1.0f ? (1.0f / 255.0f) : 1.0f;
                return new Color(
                    Mathf.Clamp(v.X * scale, 0f, 1f),
                    Mathf.Clamp(v.Y * scale, 0f, 1f),
                    Mathf.Clamp(v.Z * scale, 0f, 1f),
                    1.0f
                );
            }
        }
        return Colors.White;
    }

    private static Texture2D LoadTextureWithFallbacks(
        string path,
        string requestingVmat,
        Package hideoutPkg,
        Package basePkg,
        MapTextureQuality q,
        Package addonPkg)
    {
        if (string.IsNullOrWhiteSpace(path) || IsDummyTexture(path)) return null;

        string cPath = path.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? path : path + "_c";
        Texture2D loaded = null;

        // Priority 1: Check active Addon VPK
        if (addonPkg != null && (addonPkg.FindEntry(cPath) != null || addonPkg.FindEntry(path) != null))
        {
            loaded = Source2TextureLoader.GetOrLoadMapTexture(addonPkg, path, q, null);
        }

        // Priority 2: Check Hideout VPK (map-specific textures)
        if (loaded == null && hideoutPkg != null && (hideoutPkg.FindEntry(cPath) != null || hideoutPkg.FindEntry(path) != null))
        {
            loaded = Source2TextureLoader.GetOrLoadMapTexture(hideoutPkg, path, q, addonPkg);
        }

        // Priority 3: Check Base Game VPK (citadel/pak01_dir.vpk - shared models & materials)
        if (loaded == null && basePkg != null && (basePkg.FindEntry(cPath) != null || basePkg.FindEntry(path) != null))
        {
            loaded = Source2TextureLoader.GetOrLoadMapTexture(basePkg, path, q, addonPkg);
        }

        // Priority 4: Check Core Engine VPK (game/core/pak01_dir.vpk - default textures)
        if (loaded == null && Source2TextureLoader.CorePackage != null && (Source2TextureLoader.CorePackage.FindEntry(cPath) != null || Source2TextureLoader.CorePackage.FindEntry(path) != null))
        {
            loaded = Source2TextureLoader.GetOrLoadMapTexture(Source2TextureLoader.CorePackage, path, q, addonPkg);
        }

        if (loaded != null)
        {
            _loadedTextures.Add(path);
            return loaded;
        }

        // Track missing texture for diagnostics audit
        if (!_missingTextures.TryGetValue(path, out var referrers))
        {
            referrers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _missingTextures[path] = referrers;
        }
        if (!string.IsNullOrEmpty(requestingVmat))
        {
            referrers.Add(requestingVmat);
        }

        return null;
    }

    /// <summary>
    /// Generates a human-readable diagnostic report detailing material count, loaded textures, and missing textures.
    /// </summary>
    public static string GenerateDiagnosticSummary()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("==================================================");
        sb.AppendLine("   STAGE MATERIAL & TEXTURE DIAGNOSTIC REPORT");
        sb.AppendLine("==================================================");
        sb.AppendLine($"Cached Materials:     {_materialCache.Count}");
        sb.AppendLine($"Loaded Textures:      {_loadedTextures.Count}");
        sb.AppendLine($"Missing Textures:     {_missingTextures.Count}");
        sb.AppendLine("--------------------------------------------------");

        if (_missingTextures.Count == 0)
        {
            sb.AppendLine("All requested textures resolved successfully! Zero missing textures.");
        }
        else
        {
            sb.AppendLine("MISSING TEXTURES LIST:");
            int idx = 1;
            foreach (var (texPath, referrers) in _missingTextures)
            {
                sb.AppendLine($"[{idx++}] '{texPath}'");
                sb.AppendLine($"    Referenced by ({referrers.Count}): {string.Join(", ", referrers)}");
            }
        }
        sb.AppendLine("==================================================");
        return sb.ToString();
    }

    /// <summary>
    /// Prints the complete diagnostic summary to the Godot console and debugger.
    /// </summary>
    public static void LogDiagnosticReport()
    {
        GD.Print(GenerateDiagnosticSummary());
    }

    private static StandardMaterial3D _fallbackMaterial;

    private static StandardMaterial3D GetFallbackMaterial()
    {
        if (_fallbackMaterial == null || !GodotObject.IsInstanceValid(_fallbackMaterial))
        {
            _fallbackMaterial = new StandardMaterial3D
            {
                ResourceName = "Map_Fallback_Material",
                AlbedoColor = new Color(0.72f, 0.70f, 0.68f),
                Roughness = 0.9f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                VertexColorUseAsAlbedo = false
            };
        }
        return _fallbackMaterial;
    }
}
