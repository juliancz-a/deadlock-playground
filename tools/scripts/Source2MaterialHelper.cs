using System;
using System.IO;
using System.Collections.Generic;
using Godot;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using SkiaSharp;
using DeadlockPlayground.Materials;

public static class Source2MaterialHelper
{
    // Diccionario caché para no decodificar dos veces la misma textura si se repite
    private static readonly Dictionary<string, ImageTexture> _textureCache = new();
    private static Shader _dynamicGlowShader;
    private static Shader _glassShader;
    private static Shader _dynamicFxShader;
    private static Shader _dynamicFxAddShader;
    private static Shader _flameHairShader;
    private static Shader _dynamicJitterShader;
    private static Shader _heroOutlineShader;
    private static Shader _lashSparklesShader;
    private static Shader _wraithCardShader;

private static Shader _pbrShader;

private static readonly System.Numerics.Vector3 LumCoeffsNormalised = 
    System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(0.2126f, 0.7152f, 0.0722f));

    private static Shader LoadValveShader(string shaderName)
    {
        string valvePath = $"res://assets/shaders/valve/{shaderName}";
        if (ResourceLoader.Exists(valvePath)) return GD.Load<Shader>(valvePath);
        string fallbackPath = $"res://assets/shaders/{shaderName}";
        if (ResourceLoader.Exists(fallbackPath)) return GD.Load<Shader>(fallbackPath);
        return null;
    }

    private static Shader GetHeroOutlineShader()
    {
        _heroOutlineShader ??= LoadValveShader("source2_hero_outline.gdshader");
        return _heroOutlineShader;
    }

private static Shader GetPbrHeadShader()
    {
        _pbrShader ??= LoadValveShader("source2_pbr.gdshader");
        return _pbrShader;
    }

    private static Shader GetFlameHairShader()
    {
        _flameHairShader ??= LoadValveShader("source2_flame_hair.gdshader");
        return _flameHairShader ?? GetDynamicFxShader(false);
    }

    private static Shader GetDynamicFxShader(bool isAdditive)
    {
        if (isAdditive)
        {
            _dynamicFxAddShader ??= LoadValveShader("source2_dynamic_fx_add.gdshader");
            if (_dynamicFxAddShader != null) return _dynamicFxAddShader;
        }

        _dynamicFxShader ??= LoadValveShader("source2_dynamic_fx.gdshader");
        return _dynamicFxShader;
    }

    private static Shader GetDynamicJitterShader()
    {
        _dynamicJitterShader ??= LoadValveShader("source2_dynamic_jitter.gdshader");
        return _dynamicJitterShader ?? GetDynamicFxShader(false);
    }

    private static Shader GetDynamicGlowShader()
    {
        _dynamicGlowShader ??= LoadValveShader("source2_dynamic_glow.gdshader");
        return _dynamicGlowShader;
    }

    private static Shader GetGlassShader()
    {
        _glassShader ??= LoadValveShader("source2_glass.gdshader");
        return _glassShader;
    }

    public static Shader GetLashSparklesShader()
    {
        _lashSparklesShader ??= LoadValveShader("lash_sparkles.gdshader");
        return _lashSparklesShader;
    }

    private static Shader _cardsShader;

    public static Shader GetCardsShader()
    {
        _cardsShader ??= LoadValveShader("source2_cards.gdshader") ?? LoadValveShader("source2_wraith_card.gdshader");
        return _cardsShader;
    }

    public static Shader GetWraithCardShader()
    {
        return GetCardsShader();
    }

    /// <summary>
    /// Extrae un vtex_c a un ImageTexture nativo de Godot rápidamente reduciendo su tamaño.
    /// Si forceOpaque es true (por defecto para texturas de albedo/normal/ao opacas),
    /// fuerza el canal Alpha a 255 para evitar que la compresión PNG de Skia anule el RGB (A=0 en BC7).
    /// </summary>
    public static ImageTexture ExtractVtexToGodot(Package package, string vtexInternalPath, int maxDimension = 1024, bool forceOpaque = true)
    {
        if (string.IsNullOrWhiteSpace(vtexInternalPath)) return null;

        if (vtexInternalPath.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
        {
            vtexInternalPath = vtexInternalPath.Substring(9);
        }
        vtexInternalPath = vtexInternalPath.Trim('"', '\'', ' ');

        var entry = package.FindEntry(vtexInternalPath);
        if (entry == null)
        {
            if (!vtexInternalPath.EndsWith("_c"))
            {
                entry = package.FindEntry(vtexInternalPath + "_c");
            }
        }
        if (entry == null && !vtexInternalPath.StartsWith("materials/", StringComparison.OrdinalIgnoreCase))
        {
            string matPrefixed = "materials/" + vtexInternalPath;
            entry = package.FindEntry(matPrefixed) ?? package.FindEntry(matPrefixed + "_c");
        }
        if (entry == null && vtexInternalPath.StartsWith("materials/", StringComparison.OrdinalIgnoreCase))
        {
            string stripped = vtexInternalPath.Substring(10);
            entry = package.FindEntry(stripped) ?? package.FindEntry(stripped + "_c");
        }

        // Secondary search path: fallback to filename match across all vtex_c entries in VPK
        if (entry == null)
        {
            string fileNameOnly = Path.GetFileNameWithoutExtension(vtexInternalPath);
            if (fileNameOnly.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase))
            {
                fileNameOnly = Path.GetFileNameWithoutExtension(fileNameOnly);
            }
            if (package.Entries.TryGetValue("vtex_c", out var texEntries))
            {
                entry = texEntries.Find(e => e.FileName.Equals(fileNameOnly, StringComparison.OrdinalIgnoreCase))
                     ?? texEntries.Find(e => e.FileName.Contains(fileNameOnly, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (entry == null)
        {
            GD.PrintErr($"[MaterialHelper] No se encontró la textura: {vtexInternalPath}");
            return null;
        }

        package.ReadEntry(entry, out byte[] data);
        using var resource = new ValveResourceFormat.Resource();
        using var ms = new MemoryStream(data);
        resource.Read(ms);

        if (resource.ResourceType != ResourceType.Texture)
        {
            return null;
        }

        var texture = (ValveResourceFormat.ResourceTypes.Texture)resource.DataBlock;

        uint maxMip = texture.NumMipLevels > 0 ? (uint)(texture.NumMipLevels - 1) : 0;
        uint mipLevel = 0;
        if (texture.Width > maxDimension || texture.Height > maxDimension)
        {
            mipLevel = (uint)Math.Min(2, maxMip);
        }

        SKBitmap skBitmap;
        try
        {
            // Especificar depth: 0 y mipLevel: mipLevel para evitar ArgumentOutOfRangeException
            skBitmap = texture.GenerateBitmap(depth: 0, mipLevel: mipLevel);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MaterialHelper] Error al decodificar bitmap ({vtexInternalPath}): {ex.Message}");
            try
            {
                skBitmap = texture.GenerateBitmap(0);
            }
            catch
            {
                return null;
            }
        }

        if (skBitmap == null) return null;
        using var _ = skBitmap;

        // En Source 2 (Deadlock), el canal Alpha de las texturas BC7 suele ser 0 (usado para rugosidad/máscaras).
        // Si no se fuerza a 255 en texturas opacas, el canal RGB puede multiplicarse a 0 o quedar transparente.
        bool isSparkle = vtexInternalPath.Contains("sparkle", StringComparison.OrdinalIgnoreCase) ||
                         vtexInternalPath.Contains("lash_sparkles", StringComparison.OrdinalIgnoreCase);
        bool isAlphaCardTexture = isSparkle ||
                                  vtexInternalPath.Contains("fur", StringComparison.OrdinalIgnoreCase) ||
                                  vtexInternalPath.Contains("eyelash", StringComparison.OrdinalIgnoreCase) ||
                                  vtexInternalPath.Contains("lashes", StringComparison.OrdinalIgnoreCase);

        bool isBgra = skBitmap.ColorType == SKColorType.Bgra8888;
        int bytesPerPixel = skBitmap.BytesPerPixel;
        int totalPixels = skBitmap.Width * skBitmap.Height;

        // Cargamos la imagen nativa en Godot pasando los bytes RGBA directos sin compresión ni clobbering
        Image godotImage = null;
        if (bytesPerPixel == 4)
        {
            try
            {
                var span = skBitmap.GetPixelSpan();
                byte[] pixelBytes = span.ToArray();

                for (int i = 0; i < totalPixels; i++)
                {
                    int idx = i * 4;
                    if (isBgra)
                    {
                        // Swap Blue (byte 0) and Red (byte 2) to convert BGRA8888 -> RGBA8888
                        byte blue = pixelBytes[idx];
                        pixelBytes[idx] = pixelBytes[idx + 2];     // R
                        pixelBytes[idx + 2] = blue;                // B
                    }

                    if (forceOpaque && !isAlphaCardTexture)
                    {
                        pixelBytes[idx + 3] = 255;
                    }
                }

                godotImage = Image.CreateFromData(skBitmap.Width, skBitmap.Height, false, Image.Format.Rgba8, pixelBytes);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[MaterialHelper] Error en CreateFromData, recurriendo a PNG: {ex.Message}");
            }
        }

        if (godotImage == null)
        {
            using var skImage = SKImage.FromBitmap(skBitmap);
            using var encodedData = skImage.Encode(SKEncodedImageFormat.Png, 100);
            byte[] pngBytes = encodedData.ToArray();
            godotImage = new Image();
            Error err = godotImage.LoadPngFromBuffer(pngBytes);
            if (err != Error.Ok)
            {
                GD.PrintErr($"[MaterialHelper] Error al cargar buffer PNG en Godot: {err}");
                return null;
            }
        }

        godotImage.GenerateMipmaps();
        return ImageTexture.CreateFromImage(godotImage);
    }

    /// <summary>
    /// Creates a dedicated, opaque StandardMaterial3D for Wraith's fedora hat card (wraith_model Surface [3]).
    /// Uses CullMode = Back and Transparency = Disabled to prevent double-sided geometry and transparent queue
    /// sorting artifacts from bleeding the mirrored backface UVs into the front face.
    /// Provides soft, balanced lilac emission (#A967F5) via the self-illum mask without blinding blowout.
    /// </summary>
    public static StandardMaterial3D CreateHatCardMaterial(Package package, ValveResourceFormat.ResourceTypes.Material matResource = null)
    {
        var hatCardMat = new StandardMaterial3D
        {
            ResourceName = "wraith_hat_card",
            CullMode = BaseMaterial3D.CullModeEnum.Back,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel,
            Transparency = BaseMaterial3D.TransparencyEnum.Disabled,
            VertexColorUseAsAlbedo = false,
            AlbedoColor = Colors.White,
            Roughness = 0.65f,
            Metallic = 0.0f
        };

        string colorPath = null;
        if (matResource != null)
        {
            if (matResource.TextureParams.TryGetValue("g_tColor", out var cp1)) colorPath = cp1;
            else if (matResource.TextureParams.TryGetValue("TextureColor", out var cp2)) colorPath = cp2;
            else if (matResource.TextureParams.TryGetValue("TextureColor1", out var cp3)) colorPath = cp3;
        }
        colorPath ??= "models/heroes_wip/wraith/materials/wraith_cards_color_psd_71101b24.vtex";

        var cardTex = ExtractVtexToGodot(package, colorPath, forceOpaque: true)
                   ?? ExtractVtexToGodot(package, "models/heroes_wip/wraith/materials/wraith_cards_color_psd_71101b24.vtex", forceOpaque: true)
                   ?? ExtractVtexToGodot(package, "wraith_cards_color_psd_71101b24.vtex", forceOpaque: true);
        if (cardTex != null)
        {
            hatCardMat.AlbedoTexture = cardTex;
        }

        string maskPath = null;
        if (matResource != null)
        {
            if (matResource.TextureParams.TryGetValue("g_tSelfIllumMask", out var mp1)) maskPath = mp1;
            else if (matResource.TextureParams.TryGetValue("TextureSelfIllumMask", out var mp2)) maskPath = mp2;
            else if (matResource.TextureParams.TryGetValue("g_tSelfIllumMask1", out var mp3)) maskPath = mp3;
        }
        maskPath ??= "models/heroes_wip/wraith/materials/wraith_cards_mask_psd_b2fc96c4.vtex";

        var cardMask = ExtractVtexToGodot(package, maskPath, forceOpaque: false)
                    ?? ExtractVtexToGodot(package, "models/heroes_wip/wraith/materials/wraith_cards_mask_psd_b2fc96c4.vtex", forceOpaque: false)
                    ?? ExtractVtexToGodot(package, "wraith_cards_mask_psd_b2fc96c4.vtex", forceOpaque: false);
        if (cardMask != null)
        {
            hatCardMat.EmissionEnabled = true;
            hatCardMat.EmissionTexture = cardMask;
            hatCardMat.EmissionOperator = BaseMaterial3D.EmissionOperatorEnum.Multiply;
            hatCardMat.Emission = new Color(0.663f, 0.404f, 0.961f, 1.0f); // Lilac #A967F5
            hatCardMat.EmissionEnergyMultiplier = 1.2f; // Soft, authentic psychic glow
        }

        return hatCardMat;
    }

    /// <summary>
    /// Lee un archivo .vmat_c y crea un Material de Godot limpio, robusto y genérico.
    /// Asigna automáticamente source2_dynamic_glow.gdshader a capas de energía / flujo / aditivas
    /// o un StandardMaterial3D configurado con VmatColorExtractor para superficies PBR.
    /// </summary>
    public static Godot.Material CreateMaterialFromVmat(Package package, string vmatPath, string meshName = null)
    {
        if (!vmatPath.EndsWith("_c")) vmatPath += "_c";

        string fileNameOnly = Path.GetFileNameWithoutExtension(vmatPath);
        if (fileNameOnly.EndsWith(".vmat", StringComparison.OrdinalIgnoreCase))
        {
            fileNameOnly = Path.GetFileNameWithoutExtension(fileNameOnly);
        }

        var entry = package.FindEntry(vmatPath);
        if (entry == null && !vmatPath.StartsWith("materials/", StringComparison.OrdinalIgnoreCase))
        {
            entry = package.FindEntry("materials/" + vmatPath);
        }
        if (entry == null && vmatPath.StartsWith("materials/", StringComparison.OrdinalIgnoreCase))
        {
            entry = package.FindEntry(vmatPath.Substring(10));
        }
        if (entry == null)
        {
            if (package.Entries.TryGetValue("vmat_c", out var matEntries))
            {
                entry = matEntries.Find(e => e.FileName.Equals(fileNameOnly, StringComparison.OrdinalIgnoreCase))
                     ?? matEntries.Find(e => e.FileName.Contains(fileNameOnly, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (entry == null)
        {
            if (vmatPath.Contains("jitter02", StringComparison.OrdinalIgnoreCase))
            {
                string fallbackPath = vmatPath.Replace("jitter02", "jitter01", StringComparison.OrdinalIgnoreCase)
                                              .Replace("Jitter02", "Jitter01", StringComparison.OrdinalIgnoreCase);
                GD.Print($"[VMAT] jitter02 no encontrado, recurriendo a {fallbackPath}");
                return CreateMaterialFromVmat(package, fallbackPath);
            }

            GD.PrintErr($"[VMAT] No se encontró el archivo: {vmatPath}");
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

        var matResource = (ValveResourceFormat.ResourceTypes.Material)resource.DataBlock;
        string vmatLower = vmatPath.ToLowerInvariant();

        // Dedicated Lash Sparkles additive billboard pass
        if (vmatLower.Contains("lash_sparkles") || (vmatLower.Contains("sparkle") && vmatLower.Contains("lash")))
        {
            var sparkleShader = GetLashSparklesShader();
            if (sparkleShader != null)
            {
                var shaderMat = new ShaderMaterial { Shader = sparkleShader };
                shaderMat.RenderPriority = 3;

                // Load sparkle mask with raw RGBA (forceOpaque: false)
                var maskTex = LoadVtexTexture(package, matResource, "g_tColor", forceOpaque: false)
                           ?? LoadVtexTexture(package, matResource, "TextureColor", forceOpaque: false)
                           ?? LoadVtexTexture(package, matResource, "TextureTranslucency", forceOpaque: false)
                           ?? LoadVtexTexture(package, matResource, "TextureTranslucency1", forceOpaque: false)
                           ?? LoadVtexTexture(package, "models/heroes_wip/lash/materials/lash_sparkles_mask_psd_c38399ee.vtex", forceOpaque: false)
                           ?? LoadVtexTexture(package, "lash_sparkles_mask_psd_c38399ee.vtex", forceOpaque: false);

                if (maskTex != null)
                {
                    shaderMat.SetShaderParameter("sparkle_mask", maskTex);
                    GD.Print($"[Lash Sparkles] Successfully bound mask texture to 'sparkle_mask' ({maskTex.GetWidth()}x{maskTex.GetHeight()})");
                }
                else
                {
                    GD.PrintErr($"[Lash Sparkles] Failed to load sparkle mask texture for: {vmatPath}");
                }

                // Pale gold tint from VMAT or default [0.85098, 0.776471, 0.219608]
                Color tint = new Color(0.85098f, 0.776471f, 0.219608f, 1.0f);
                if (matResource.VectorParams.TryGetValue("g_vSelfIllumTint1", out var sit1) && (sit1.X > 0.01f || sit1.Y > 0.01f || sit1.Z > 0.01f))
                    tint = new Color(sit1.X, sit1.Y, sit1.Z, 1.0f);
                else if (matResource.VectorParams.TryGetValue("g_vSelfIllumTint", out var sit0) && (sit0.X > 0.01f || sit0.Y > 0.01f || sit0.Z > 0.01f))
                    tint = new Color(sit0.X, sit0.Y, sit0.Z, 1.0f);
                shaderMat.SetShaderParameter("sparkle_tint", tint);

                float scale = 7.154f;
                if (matResource.FloatParams.TryGetValue("g_flSelfIllumScale1", out var sc1) && sc1 > 0.01f) scale = sc1;
                else if (matResource.FloatParams.TryGetValue("g_flSelfIllumScale", out var sc0) && sc0 > 0.01f) scale = sc0;
                shaderMat.SetShaderParameter("emission_scale", scale);

                Vector2 scrollSpeed = new Vector2(0.5f, 0.0f);
                if (matResource.VectorParams.TryGetValue("g_vSelfIllumScrollSpeed1", out var ss1) && (Mathf.Abs(ss1.X) > 0.001f || Mathf.Abs(ss1.Y) > 0.001f))
                    scrollSpeed = new Vector2(ss1.X, ss1.Y);
                else if (matResource.VectorParams.TryGetValue("g_vSelfIllumScrollSpeed", out var ss0) && (Mathf.Abs(ss0.X) > 0.001f || Mathf.Abs(ss0.Y) > 0.001f))
                    scrollSpeed = new Vector2(ss0.X, ss0.Y);
                shaderMat.SetShaderParameter("scroll_speed", scrollSpeed);
                shaderMat.SetShaderParameter("sweep_speed", scrollSpeed.X != 0 ? Mathf.Abs(scrollSpeed.X) : 0.5f);

                return shaderMat;
            }
        }

        // Punkgoat jitter02 is an unlit black variant; reuse jitter01 to ensure proper comic-book paper visuals
        if (vmatLower.Contains("punkgoat_border_jitter02"))
        {
            return CreateMaterialFromVmat(package, "models/heroes_wip/punkgoat/materials/punkgoat_border_jitter01.vmat");
        }

        // 0.4. Wraith's playing cards / magical handcards:
        // Case A: Hat card (wraith_model Surface [3]). The card on her fedora is a static, solid physical card polygon.
        // It MUST be an opaque StandardMaterial3D with CullMode = Back and Transparency = Disabled.
        // This ensures it renders in the opaque pass with Z-buffer depth rejection, preventing the back-face UVs
        // from bleeding through the front face and creating mirrored/doubled "A" glyphs and a split spade.
        // Case B: Floating handcards (wraith_handcards_model). These have an outer extruded halo skirt geometry
        // that requires selective alpha transparency and psychic neon bloom via source2_cards.gdshader.
        if (vmatLower.Contains("wraith_cards") || vmatLower.Contains("handcards") || (vmatLower.Contains("card") && vmatLower.Contains("wraith")))
        {
            if (meshName != null && meshName.Contains("wraith_model", StringComparison.OrdinalIgnoreCase))
            {
                return CreateHatCardMaterial(package, matResource);
            }

            var cardShader = GetCardsShader();
            if (cardShader != null)
            {
                var shaderMat = new ShaderMaterial { Shader = cardShader };
                shaderMat.ResourceName = vmatPath;

                // ── Color / Albedo texture (g_tColor / TextureColor) ───────────────────────────
                bool hasCardAlbedo = BindTextureIfPresent(package, matResource, "g_tColor", shaderMat, "g_tColor", forceOpaque: true)
                                  || BindTextureIfPresent(package, matResource, "TextureColor", shaderMat, "g_tColor", forceOpaque: true)
                                  || BindTextureIfPresent(package, matResource, "TextureColor1", shaderMat, "g_tColor", forceOpaque: true);

                if (!hasCardAlbedo)
                {
                    var albedoFallback = getOrLoadTexture(package, "models/heroes_wip/wraith/materials/wraith_cards_color_psd_71101b24.vtex", forceOpaque: true)
                                     ?? getOrLoadTexture(package, "wraith_cards_color_psd_71101b24.vtex", forceOpaque: true);
                    if (albedoFallback != null)
                    {
                        shaderMat.SetShaderParameter("g_tColor", albedoFallback);
                        hasCardAlbedo = true;
                    }
                }

                // ── Self-Illum mask (g_tSelfIllumMask / TextureSelfIllumMask) ────────────────────
                bool hasCardSIMask = BindTextureIfPresent(package, matResource, "g_tSelfIllumMask", shaderMat, "g_tSelfIllumMask", forceOpaque: true)
                                  || BindTextureIfPresent(package, matResource, "TextureSelfIllumMask", shaderMat, "g_tSelfIllumMask", forceOpaque: true)
                                  || BindTextureIfPresent(package, matResource, "g_tSelfIllumMask1", shaderMat, "g_tSelfIllumMask", forceOpaque: true);
                if (!hasCardSIMask)
                {
                    var maskFallback = getOrLoadTexture(package, "models/heroes_wip/wraith/materials/wraith_cards_mask_psd_b2fc96c4.vtex", forceOpaque: false)
                                    ?? getOrLoadTexture(package, "wraith_cards_mask_psd_b2fc96c4.vtex", forceOpaque: false);
                    if (maskFallback != null)
                    {
                        shaderMat.SetShaderParameter("g_tSelfIllumMask", maskFallback);
                        hasCardSIMask = true;
                    }
                }

                // ── Normal / Roughness texture (g_tNormalRoughness / TextureNormal) ───────────────
                bool hasCardNR = BindTextureIfPresent(package, matResource, "g_tNormalRoughness", shaderMat, "g_tNormalRoughness", forceOpaque: true)
                              || BindTextureIfPresent(package, matResource, "TextureNormalRoughness", shaderMat, "g_tNormalRoughness", forceOpaque: true)
                              || BindTextureIfPresent(package, matResource, "g_tNormal", shaderMat, "g_tNormalRoughness", forceOpaque: true)
                              || BindTextureIfPresent(package, matResource, "TextureNormal", shaderMat, "g_tNormalRoughness", forceOpaque: true);
                if (!hasCardNR)
                {
                    var nrFallback = getOrLoadTexture(package, "models/heroes_wip/wraith/materials/wraith_cards_vmat_g_tnormalroughness_ebebd272.vtex", forceOpaque: true)
                                  ?? getOrLoadTexture(package, "wraith_cards_vmat_g_tnormalroughness_ebebd272.vtex", forceOpaque: true);
                    if (nrFallback != null)
                    {
                        shaderMat.SetShaderParameter("g_tNormalRoughness", nrFallback);
                        hasCardNR = true;
                    }
                }

                // ── Card glow tint & emission scale ──────────────────────────────────────────
                // Tint multiplier: default White preserves 100% of the artist-painted lilac color from g_tColor
                shaderMat.SetShaderParameter("g_vSelfIllumTint", Colors.White);

                // Balanced emission scale (default 2.0 for soft radiant psychic glow without blowing out)
                float selfIllumScale = 2.0f;
                if (matResource != null)
                {
                    if (matResource.FloatParams.TryGetValue("g_flSelfIllumScale1", out var sis1) && sis1 > 0.01f && sis1 <= 4.0f)
                        selfIllumScale = sis1;
                    else if (matResource.FloatParams.TryGetValue("g_flSelfIllumScale", out var sis) && sis > 0.01f && sis <= 4.0f)
                        selfIllumScale = sis;
                }
                shaderMat.SetShaderParameter("g_flSelfIllumScale", selfIllumScale);

                // ── Halo envelope selective transparency parameters ──────────────────────────
                // Card faces occupy V in [0.0, 0.79], exterior aura skirt occupies V in [0.85, 1.0].
                // Threshold 0.82 keeps cards solid while halo envelope is softly translucent.
                shaderMat.SetShaderParameter("g_flHaloAlpha", 0.10f);
                shaderMat.SetShaderParameter("g_flHaloVThreshold", 0.82f);
                shaderMat.SetShaderParameter("invert_v", false);

                return shaderMat;
            }

            // ── Fallback: shader file not found, use StandardMaterial3D ────────
            return CreateHatCardMaterial(package, matResource);
        }
        

        // 0.5. Superficies de vidrio y lentes translúcidos PBR (Dynamo glass dome, Paradox visor/hourglass, etc.)
        var glassParams = VmatColorExtractor.ExtractGlass(matResource, vmatPath);
        if (glassParams.IsGlass)
        {
            var glassShader = GetGlassShader();
            if (glassShader != null)
            {
                var shaderMat = new ShaderMaterial { Shader = glassShader };
                // Always render glass at priority 1 so all internal geometry (reactor core, gears,
                // neck mechanisms) depth-writes before the glass surface samples the screen texture.
                shaderMat.RenderPriority = 1;

                // 1. Generic base color from TextureColor1 or g_vColorTint
              Vector3 baseColor = new Vector3(0.06f, 0.07f, 0.09f); // Dark smoked glass base

                if (matResource.VectorParams.TryGetValue("TextureColor1", out var tc1) && (tc1.X > 0.001f || tc1.Y > 0.001f || tc1.Z > 0.001f))
                    baseColor = new Vector3(tc1.X, tc1.Y, tc1.Z);
                else if (matResource.VectorParams.TryGetValue("TextureColor", out var tc0) && (tc0.X > 0.001f || tc0.Y > 0.001f || tc0.Z > 0.001f))
                    baseColor = new Vector3(tc0.X, tc0.Y, tc0.Z);
                else if (matResource.VectorParams.TryGetValue("g_vColorTint1", out var tintVec1) && !IsNeutralWhiteOrBlack(tintVec1))
                    baseColor = new Vector3(tintVec1.X, tintVec1.Y, tintVec1.Z);
                else if (matResource.VectorParams.TryGetValue("g_vColorTint", out var tintVec0) && !IsNeutralWhiteOrBlack(tintVec0))
                    baseColor = new Vector3(tintVec0.X, tintVec0.Y, tintVec0.Z);

                Color glassTint = new Color(baseColor.X, baseColor.Y, baseColor.Z, glassParams.Opacity);
         
                shaderMat.SetShaderParameter("glass_tint", glassTint);
                shaderMat.SetShaderParameter("roughness", glassParams.Roughness);
                shaderMat.SetShaderParameter("metallic", glassParams.Metallic);
                shaderMat.SetShaderParameter("clearcoat", glassParams.Clearcoat);
                shaderMat.SetShaderParameter("clearcoat_roughness", glassParams.ClearcoatRoughness);
                shaderMat.SetShaderParameter("refract_amount", glassParams.RefractAmount);

                // 2. Generic Self-Illumination and Animated Liquid/Bubbles in Glass (e.g. Viscous head)
                bool hasGlassSelfIllum = matResource.IntParams.GetValueOrDefault("F_SELF_ILLUM", 0) == 1;
                shaderMat.SetShaderParameter("enable_self_illum", hasGlassSelfIllum);
                if (hasGlassSelfIllum)
                {
                    System.Numerics.Vector4 siTint = System.Numerics.Vector4.One;
                    if (matResource.VectorParams.TryGetValue("g_vSelfIllumTint1", out var sit1)) siTint = sit1;
                    else if (matResource.VectorParams.TryGetValue("g_vSelfIllumTint", out var sit0)) siTint = sit0;
                    shaderMat.SetShaderParameter("self_illum_tint", new Color(siTint.X, siTint.Y, siTint.Z, 1.0f));

                    float siScale = 1.0f;
                    if (matResource.FloatParams.TryGetValue("g_flSelfIllumScale1", out var sis1)) siScale = sis1;
                    else if (matResource.FloatParams.TryGetValue("g_flSelfIllumScale", out var sis0)) siScale = sis0;
                    shaderMat.SetShaderParameter("self_illum_scale", siScale);

                    float siAlbedoFactor = 0.0f;
                    if (matResource.FloatParams.TryGetValue("g_flSelfIllumAlbedoFactor1", out var flAf1)) siAlbedoFactor = flAf1;
                    else if (matResource.FloatParams.TryGetValue("g_flSelfIllumAlbedoFactor", out var flAf0)) siAlbedoFactor = flAf0;
                    shaderMat.SetShaderParameter("self_illum_albedo_factor", siAlbedoFactor);

                    Vector2 glassSiScroll = Vector2.Zero;
                    if (matResource.VectorParams.TryGetValue("g_vSelfIllumScrollSpeed1", out var siss1)) glassSiScroll = new Vector2(siss1.X, siss1.Y);
                    else if (matResource.VectorParams.TryGetValue("g_vSelfIllumScrollSpeed", out var siss0)) glassSiScroll = new Vector2(siss0.X, siss0.Y);
                    shaderMat.SetShaderParameter("self_illum_scroll_speed", glassSiScroll);

                    Vector2 glassAlbScroll = Vector2.Zero;
                    if (matResource.VectorParams.TryGetValue("g_vAlbedoScrollSpeed1", out var as1)) glassAlbScroll = new Vector2(as1.X, as1.Y);
                    else if (matResource.VectorParams.TryGetValue("g_vAlbedoScrollSpeed", out var as0)) glassAlbScroll = new Vector2(as0.X, as0.Y);
                    shaderMat.SetShaderParameter("albedo_scroll_speed", glassAlbScroll);

                    bool hasSiMask = BindTextureIfPresent(package, matResource, "g_tSelfIllumMask", shaderMat, "self_illum_mask", true)
                                  || BindTextureIfPresent(package, matResource, "TextureSelfIllumMask", shaderMat, "self_illum_mask", true);
                    shaderMat.SetShaderParameter("use_self_illum_mask", hasSiMask);
                }

                string colorPath = getTexture(matResource, "g_tColor") ?? getTexture(matResource, "TextureColor");
                if (!string.IsNullOrEmpty(colorPath))
                {
                    var colorTex = getOrLoadTexture(package, colorPath, forceOpaque: true);
                    if (colorTex != null) shaderMat.SetShaderParameter("albedo_texture", colorTex);
                }

                string glassNormalPath = getTexture(matResource, "g_tNormal")
                    ?? getTexture(matResource, "g_tNormalRoughness")
                    ?? getTexture(matResource, "TextureNormalRoughness")
                    ?? getTexture(matResource, "TextureNormal");
                if (!string.IsNullOrEmpty(glassNormalPath))
                {
                    var normalTex = getOrLoadTexture(package, glassNormalPath, forceOpaque: true);
                    if (normalTex != null) shaderMat.SetShaderParameter("normal_texture", normalTex);
                }

                return shaderMat;
            }
        }

        // 1. Efectos visuales procedurales dinámicos universales (Billy jitter, Infernus fire, Vindicta glow, Lady Geist demonic arm)
        bool hasJitter = matResource.IntParams.GetValueOrDefault("F_JITTER_VERTICES", 0) == 1;
        bool hasTexTransform = matResource.IntParams.GetValueOrDefault("F_ENABLE_TEXTURE_TRANSFORMS", 0) == 1;
        bool isFxAdditive = matResource.IntParams.GetValueOrDefault("F_ADDITIVE_BLEND", 0) == 1;
        bool isFxAlphaTest = matResource.IntParams.GetValueOrDefault("F_ALPHA_TEST", 0) == 1;

        bool hasScroll = (matResource.VectorParams.TryGetValue("g_vAlbedoScrollSpeed1", out var albScroll) && (albScroll.X != 0 || albScroll.Y != 0))
                      || (matResource.VectorParams.TryGetValue("g_vAlbedoScrollSpeed", out var albScroll0) && (albScroll0.X != 0 || albScroll0.Y != 0))
                      || (matResource.VectorParams.TryGetValue("g_vSelfIllumScrollSpeed1", out var siScroll) && (siScroll.X != 0 || siScroll.Y != 0))
                      || (matResource.VectorParams.TryGetValue("g_vSelfIllumScrollSpeed", out var siScroll0) && (siScroll0.X != 0 || siScroll0.Y != 0))
                      || (matResource.VectorParams.TryGetValue("g_vTranslucentScrollSpeed0_1", out var trScroll) && (trScroll.X != 0 || trScroll.Y != 0))
                      || (matResource.VectorParams.TryGetValue("g_vTranslucentScrollSpeed1", out var trScroll1) && (trScroll1.X != 0 || trScroll1.Y != 0));

        bool isDedicatedFxName = vmatLower.Contains("jitter") || vmatLower.Contains("armglow") ||
                                 vmatLower.Contains("vindicta_glow") || vmatLower.Contains("hornet_glow") ||
                                 vmatLower.Contains("inferno_armglow") || vmatLower.Contains("flame") ||
                                 vmatLower.Contains("headglow") ||
                                 vmatLower.Contains("sparkle") || vmatLower.Contains("sparkles") ||
                                 vmatLower.Contains("lash_sparkles") ||
                                 vmatLower.Contains("geist_arm") || vmatLower.Contains("ghost2_arm");

        bool isSparkle = vmatLower.Contains("sparkle") || vmatLower.Contains("lash_sparkles");

        if (hasJitter || hasScroll || (hasTexTransform && (hasScroll || hasJitter)) || (isFxAdditive && (hasJitter || hasScroll || isDedicatedFxName)) || isDedicatedFxName || isSparkle || VmatColorExtractor.IsDynamicGlow(matResource, vmatPath))
        {
            Shader fxShader;
            bool isHeadGlow = vmatLower.Contains("headglow") || vmatLower.Contains("flame_hair");
            bool isArmGlow = vmatLower.Contains("armglow") || (vmatLower.Contains("inferno_flames") && !isHeadGlow);

            if (isHeadGlow)
            {
                fxShader = GetFlameHairShader();
            }
            else if (isArmGlow || isFxAdditive || isSparkle)
            {
                fxShader = GetDynamicFxShader(true);
            }
            else if (hasJitter || vmatLower.Contains("jitter"))
            {
                fxShader = GetDynamicJitterShader();
            }
            else
            {
                fxShader = GetDynamicFxShader(false);
            }

            if (fxShader != null)
            {
                var shaderMat = new ShaderMaterial { Shader = fxShader };

                // 1. Pass blend modes & alpha test
                shaderMat.SetShaderParameter("is_additive", isArmGlow || isFxAdditive || isSparkle);
                shaderMat.SetShaderParameter("is_sparkle", isSparkle);
                shaderMat.SetShaderParameter("is_alpha_test", isFxAlphaTest);
                bool isTranslucent = matResource.IntParams.GetValueOrDefault("F_TRANSLUCENT", 0) == 1;
                shaderMat.SetShaderParameter("is_translucent", isTranslucent);

                float alphaRef = 0.5f;
                if (matResource.FloatParams.TryGetValue("g_flAlphaTestReference1", out var aRef1)) alphaRef = aRef1;
                else if (matResource.FloatParams.TryGetValue("g_flAlphaTestReference", out var aRef0)) alphaRef = aRef0;
                shaderMat.SetShaderParameter("alpha_test_threshold", alphaRef);

                // 2. Pass Jitter parameters
                shaderMat.SetShaderParameter("enable_jitter", hasJitter);
                if (hasJitter)
                {
                    float jitterSpeedA = 1.0f;
                    if (matResource.FloatParams.TryGetValue("g_flJitterSpeedA1", out var jsa1)) jitterSpeedA = jsa1;
                    else if (matResource.FloatParams.TryGetValue("g_flJitterSpeedA", out var jsa0)) jitterSpeedA = jsa0;
                    else if (matResource.FloatParams.TryGetValue("g_flJitterSpeed1", out var js1)) jitterSpeedA = js1;
                    else if (matResource.FloatParams.TryGetValue("g_flJitterSpeed", out var js0)) jitterSpeedA = js0;
                    shaderMat.SetShaderParameter("jitter_speed_a", jitterSpeedA);

                    float jitterSpeedB = 0.0f;
                    if (matResource.FloatParams.TryGetValue("g_flJitterSpeedB1", out var jsb1)) jitterSpeedB = jsb1;
                    else if (matResource.FloatParams.TryGetValue("g_flJitterSpeedB", out var jsb0)) jitterSpeedB = jsb0;
                    shaderMat.SetShaderParameter("jitter_speed_b", jitterSpeedB);

                    float quantTime = 0.0f;
                    if (matResource.FloatParams.TryGetValue("g_flJitterQuantizeTime1", out var qt1)) quantTime = qt1;
                    else if (matResource.FloatParams.TryGetValue("g_flJitterQuantizeTime", out var qt0)) quantTime = qt0;
                    shaderMat.SetShaderParameter("jitter_quantize_time", quantTime);

                    float quantDisp = 0.0f;
                    if (matResource.FloatParams.TryGetValue("g_flJitterQuantizeDisplacement1", out var qd1)) quantDisp = qd1;
                    else if (matResource.FloatParams.TryGetValue("g_flJitterQuantizeDisplacement", out var qd0)) quantDisp = qd0;
                    shaderMat.SetShaderParameter("jitter_quantize_disp", quantDisp);

                    Vector3? ampX = null;
                    if (matResource.VectorParams.TryGetValue("g_vJitterAmplitudesXA1", out var ax1)) ampX = new Vector3(ax1.X, ax1.Y, ax1.Z);
                    else if (matResource.VectorParams.TryGetValue("g_vJitterAmplitudesXA", out var ax0)) ampX = new Vector3(ax0.X, ax0.Y, ax0.Z);
                    else if (matResource.VectorParams.TryGetValue("g_vJitterAmplitudesA1", out var a1)) ampX = new Vector3(a1.X, a1.Y, a1.Z);
                    else if (matResource.VectorParams.TryGetValue("g_vJitterAmplitudesA", out var a0)) ampX = new Vector3(a0.X, a0.Y, a0.Z);

                    if (ampX.HasValue)
                        shaderMat.SetShaderParameter("jitter_amp_x", ampX.Value);

                    Vector3? ampY = null;
                    if (matResource.VectorParams.TryGetValue("g_vJitterAmplitudesYA1", out var ay1)) ampY = new Vector3(ay1.X, ay1.Y, ay1.Z);
                    else if (matResource.VectorParams.TryGetValue("g_vJitterAmplitudesYA", out var ay0)) ampY = new Vector3(ay0.X, ay0.Y, ay0.Z);
                    else ampY = ampX;

                    if (ampY.HasValue)
                        shaderMat.SetShaderParameter("jitter_amp_y", ampY.Value);

                    Vector3? ampZ = null;
                    if (matResource.VectorParams.TryGetValue("g_vJitterAmplitudesZA1", out var az1)) ampZ = new Vector3(az1.X, az1.Y, az1.Z);
                    else if (matResource.VectorParams.TryGetValue("g_vJitterAmplitudesZA", out var az0)) ampZ = new Vector3(az0.X, az0.Y, az0.Z);
                    else ampZ = ampX;

                    if (ampZ.HasValue)
                        shaderMat.SetShaderParameter("jitter_amp_z", ampZ.Value);

                    Vector3? freqA = null;
                    if (matResource.VectorParams.TryGetValue("g_vJitterFrequenciesA1", out var fa1)) freqA = new Vector3(fa1.X, fa1.Y, fa1.Z);
                    else if (matResource.VectorParams.TryGetValue("g_vJitterFrequenciesA", out var fa0)) freqA = new Vector3(fa0.X, fa0.Y, fa0.Z);
                    else if (matResource.VectorParams.TryGetValue("g_vJitterFrequencies1", out var f1)) freqA = new Vector3(f1.X, f1.Y, f1.Z);

                    if (freqA.HasValue)
                        shaderMat.SetShaderParameter("jitter_freq_a", freqA.Value);

                    Vector3? freqB = null;
                    if (matResource.VectorParams.TryGetValue("g_vJitterFrequenciesB1", out var fb1)) freqB = new Vector3(fb1.X, fb1.Y, fb1.Z);
                    else if (matResource.VectorParams.TryGetValue("g_vJitterFrequenciesB", out var fb0)) freqB = new Vector3(fb0.X, fb0.Y, fb0.Z);
                    else freqB = freqA;

                    if (freqB.HasValue)
                        shaderMat.SetShaderParameter("jitter_freq_b", freqB.Value);
                }

                if (isHeadGlow)
                {
                    shaderMat.SetShaderParameter("enable_jitter", true);
                    shaderMat.SetShaderParameter("jitter_speed_a", 1.746f);
                    shaderMat.SetShaderParameter("jitter_amp_x", new Vector3(0.015f, 0.015f, 0.015f));
                    shaderMat.SetShaderParameter("jitter_amp_y", new Vector3(0.015f, 0.015f, 0.015f));
                    shaderMat.SetShaderParameter("jitter_amp_z", new Vector3(0.015f, 0.015f, 0.015f));
                    shaderMat.SetShaderParameter("jitter_freq_a", new Vector3(10.0f, 10.0f, 10.0f));
                }

                // 3. Pass UV transforms and scrolls
                Vector2 albSpd = Vector2.Zero;
                if (matResource.VectorParams.TryGetValue("g_vAlbedoScrollSpeed1", out var vAlb1)) albSpd = new Vector2(vAlb1.X, vAlb1.Y);
                else if (matResource.VectorParams.TryGetValue("g_vAlbedoScrollSpeed", out var vAlb0)) albSpd = new Vector2(vAlb0.X, vAlb0.Y);
                shaderMat.SetShaderParameter("albedo_scroll_speed", albSpd);

                Vector2 albScale = Vector2.One;
                if (matResource.VectorParams.TryGetValue("g_vAlbedoTexcoordScale1", out var vAsc1)) albScale = new Vector2(vAsc1.X, vAsc1.Y);
                else if (matResource.VectorParams.TryGetValue("g_vAlbedoTexcoordScale", out var vAsc0)) albScale = new Vector2(vAsc0.X, vAsc0.Y);
                else if (matResource.VectorParams.TryGetValue("g_vTexCoordScale", out var vTcSc)) albScale = new Vector2(vTcSc.X, vTcSc.Y);
                shaderMat.SetShaderParameter("albedo_uv_scale", albScale);

                Vector2 albOff = Vector2.Zero;
                if (matResource.VectorParams.TryGetValue("g_vAlbedoTexcoordOffset1", out var vAoff1)) albOff = new Vector2(vAoff1.X, vAoff1.Y);
                else if (matResource.VectorParams.TryGetValue("g_vAlbedoTexcoordOffset", out var vAoff0)) albOff = new Vector2(vAoff0.X, vAoff0.Y);
                else if (matResource.VectorParams.TryGetValue("g_vTexCoordOffset", out var vTcOff)) albOff = new Vector2(vTcOff.X, vTcOff.Y);
                shaderMat.SetShaderParameter("albedo_uv_offset", albOff);

                float albQuant = 0.0f;
                if (matResource.FloatParams.TryGetValue("g_flAlbedoScrollQuantize1", out var flAq1)) albQuant = flAq1;
                else if (matResource.FloatParams.TryGetValue("g_flAlbedoScrollQuantize", out var flAq0)) albQuant = flAq0;
                shaderMat.SetShaderParameter("albedo_scroll_quantize", albQuant);

                Vector2 siSpd = Vector2.Zero;
                if (matResource.VectorParams.TryGetValue("g_vSelfIllumScrollSpeed1", out var vSi1)) siSpd = new Vector2(vSi1.X, vSi1.Y);
                else if (matResource.VectorParams.TryGetValue("g_vSelfIllumScrollSpeed", out var vSi0)) siSpd = new Vector2(vSi0.X, vSi0.Y);
                shaderMat.SetShaderParameter("self_illum_scroll_speed", siSpd);

                Vector2 siScale = Vector2.One;
                if (matResource.VectorParams.TryGetValue("g_vSelfIllumTexcoordScale1", out var vSisc1)) siScale = new Vector2(vSisc1.X, vSisc1.Y);
                else if (matResource.VectorParams.TryGetValue("g_vSelfIllumTexcoordScale", out var vSisc0)) siScale = new Vector2(vSisc0.X, vSisc0.Y);
                shaderMat.SetShaderParameter("self_illum_uv_scale", siScale);

                Vector2 siOff = Vector2.Zero;
                if (matResource.VectorParams.TryGetValue("g_vSelfIllumTexcoordOffset1", out var vSioff1)) siOff = new Vector2(vSioff1.X, vSioff1.Y);
                else if (matResource.VectorParams.TryGetValue("g_vSelfIllumTexcoordOffset", out var vSioff0)) siOff = new Vector2(vSioff0.X, vSioff0.Y);
                shaderMat.SetShaderParameter("self_illum_uv_offset", siOff);

                Vector2 trSpd = Vector2.Zero;
                if (matResource.VectorParams.TryGetValue("g_vTranslucentScrollSpeed0_1", out var vTr1)) trSpd = new Vector2(vTr1.X, vTr1.Y);
                else if (matResource.VectorParams.TryGetValue("g_vTranslucentScrollSpeed1", out var vTr1_alt)) trSpd = new Vector2(vTr1_alt.X, vTr1_alt.Y);
                else if (matResource.VectorParams.TryGetValue("g_vTranslucentScrollSpeed", out var vTr0)) trSpd = new Vector2(vTr0.X, vTr0.Y);
                shaderMat.SetShaderParameter("translucent_scroll_speed", trSpd);

                if (siSpd == Vector2.Zero && albSpd == Vector2.Zero && (vmatLower.Contains("flame") || vmatLower.Contains("armglow") || vmatLower.Contains("headglow")))
                {
                    if (isHeadGlow)
                    {
                        albSpd = new Vector2(-0.6f, 0.2f);
                        siSpd = new Vector2(-0.6f, 0.2f);
                    }
                    else
                    {
                        siSpd = new Vector2(0.0f, -0.45f);
                        albSpd = new Vector2(0.0f, -0.3f);
                    }
                    shaderMat.SetShaderParameter("self_illum_scroll_speed", siSpd);
                    shaderMat.SetShaderParameter("albedo_scroll_speed", albSpd);
                }
                else if (isSparkle && siSpd == Vector2.Zero)
                {
                    siSpd = new Vector2(0.5f, 0.0f);
                    shaderMat.SetShaderParameter("self_illum_scroll_speed", siSpd);
                }

                // 4. Pass Colors & Emission
                Vector3 basePaperColor = Vector3.One;
                if (matResource.VectorParams.TryGetValue("TextureColor1", out var vTexCol1))
                    basePaperColor = new Vector3(vTexCol1.X, vTexCol1.Y, vTexCol1.Z);
                else if (matResource.VectorParams.TryGetValue("TextureColor", out var vTexCol0))
                    basePaperColor = new Vector3(vTexCol0.X, vTexCol0.Y, vTexCol0.Z);
                else if (vmatLower.Contains("jitter") || vmatLower.Contains("punkgoat"))
                    basePaperColor = new Vector3(0.462745f, 0.392157f, 0.247059f);

                shaderMat.SetShaderParameter("base_paper_color", basePaperColor);

                bool maskColorTint = matResource.IntParams.GetValueOrDefault("g_bMaskColorTint1", 0) == 1
                                  || matResource.IntParams.GetValueOrDefault("g_bMaskColorTint", 0) == 1;
                shaderMat.SetShaderParameter("mask_color_tint", maskColorTint);

                if (matResource.VectorParams.TryGetValue("g_vColorTint1", out var colTint1) && !IsNeutralWhiteOrBlack(colTint1))
                {
                    float colA1 = (colTint1.W > 0.001f) ? colTint1.W : 1.0f;
                    shaderMat.SetShaderParameter("color_tint", new Color(colTint1.X, colTint1.Y, colTint1.Z, colA1));
                }
                else if (matResource.VectorParams.TryGetValue("g_vColorTint", out var colTint0) && !IsNeutralWhiteOrBlack(colTint0))
                {
                    float colA0 = (colTint0.W > 0.001f) ? colTint0.W : 1.0f;
                    shaderMat.SetShaderParameter("color_tint", new Color(colTint0.X, colTint0.Y, colTint0.Z, colA0));
                }
                else
                {
                    shaderMat.SetShaderParameter("color_tint", Colors.White);
                }

                Color authenticGlowColor = ExtractDynamicGlowColor(matResource, vmatPath);
                shaderMat.SetShaderParameter("glow_color", authenticGlowColor);
                shaderMat.SetShaderParameter("self_illum_tint", authenticGlowColor);
                GD.Print($"[Source2MaterialHelper] Bound glow_color & self_illum_tint = #{authenticGlowColor.ToHtml()} to '{vmatPath}'");

                float siScaleVal = 1.0f;
                if (matResource.FloatParams.TryGetValue("g_flSelfIllumScale1", out var flSi1)) siScaleVal = flSi1;
                else if (matResource.FloatParams.TryGetValue("g_flSelfIllumScale", out var flSi0)) siScaleVal = flSi0;
                else if (matResource.FloatParams.TryGetValue("g_flSelfIllumBrightness", out var flSiB)) siScaleVal = flSiB;

                if ((vmatLower.Contains("headglow") || vmatLower.Contains("flame_hair")) && siScaleVal <= 1.0f)
                {
                    siScaleVal = 5.16f; // Valve inferno_headglow.vmat reference value
                }
                else if ((vmatLower.Contains("inferno") || vmatLower.Contains("flame") || vmatLower.Contains("armglow")) && siScaleVal <= 1.0f)
                {
                    siScaleVal = 3.5f;
                }
                else if (isSparkle && siScaleVal <= 1.0f)
                {
                    siScaleVal = 7.154f; // Valve lash_sparkles.vmat reference value
                }
                shaderMat.SetShaderParameter("self_illum_scale", siScaleVal);

                float siFresnelExp = 0.0f;
                if (matResource.FloatParams.TryGetValue("g_flSelfIllumFresnelMaskExponent", out var flFe1)) siFresnelExp = flFe1;
                else if (matResource.FloatParams.TryGetValue("g_flSelfIllumFresnelExponent", out var flFe0)) siFresnelExp = flFe0;
                shaderMat.SetShaderParameter("self_illum_fresnel_exponent", siFresnelExp);

                float siAlbedoFactor = 0.0f;
                if (matResource.FloatParams.TryGetValue("g_flSelfIllumAlbedoFactor1", out var flAf1)) siAlbedoFactor = flAf1;
                else if (matResource.FloatParams.TryGetValue("g_flSelfIllumAlbedoFactor", out var flAf0)) siAlbedoFactor = flAf0;
                shaderMat.SetShaderParameter("self_illum_albedo_factor", siAlbedoFactor);

                float opScale = 1.0f;
                if (matResource.FloatParams.TryGetValue("g_flOpacityScale1", out var flOp1)) opScale = flOp1;
                else if (matResource.FloatParams.TryGetValue("g_flOpacityScale", out var flOp0)) opScale = flOp0;

                shaderMat.SetShaderParameter("opacity_scale", opScale);

                float alphaAnglePower = 0.0f;
                if (matResource.FloatParams.TryGetValue("g_flAlphaAnglePower1", out var flAap1)) alphaAnglePower = flAap1;
                else if (matResource.FloatParams.TryGetValue("g_flAlphaAnglePower", out var flAap0)) alphaAnglePower = flAap0;
                shaderMat.SetShaderParameter("alpha_angle_power", alphaAnglePower);
                shaderMat.SetShaderParameter("is_arm_glow", isArmGlow);

                bool useVertexColor = matResource.IntParams.GetValueOrDefault("F_VERTEX_COLOR", 0) == 1;
                shaderMat.SetShaderParameter("use_vertex_color", useVertexColor);

                float fxRoughness = 0.5f;
                if (matResource.VectorParams.TryGetValue("TextureRoughness1", out var fxRVec)) fxRoughness = fxRVec.X;
                else if (matResource.VectorParams.TryGetValue("TextureRoughness", out var fxRVec0)) fxRoughness = fxRVec0.X;
                else if (matResource.FloatParams.TryGetValue("g_flRoughnessScale1", out float fxRScale)) fxRoughness = fxRScale;
                shaderMat.SetShaderParameter("roughness", fxRoughness);

                float fxMetallic = 0.0f;
                if (matResource.VectorParams.TryGetValue("TextureMetalness1", out var fxMVec)) fxMetallic = fxMVec.X;
                else if (matResource.VectorParams.TryGetValue("TextureMetalness", out var fxMVec0)) fxMetallic = fxMVec0.X;
                else if (matResource.FloatParams.TryGetValue("g_flMetalnessScale1", out float fxMScale)) fxMetallic = fxMScale;
                shaderMat.SetShaderParameter("metallic", fxMetallic);

                // 5. Bind Textures from VRF (preserve real alpha channel for flame cutout masks)
                bool fxForceOpaqueColor = false;
                bool hasAlbedo = BindTextureIfPresent(package, matResource, "g_tColor", shaderMat, "texture_albedo", fxForceOpaqueColor)
                              || BindTextureIfPresent(package, matResource, "TextureColor", shaderMat, "texture_albedo", fxForceOpaqueColor);

                if (isSparkle)
                {
                    // Explicitly load and bind mask texture g_tColor (lash_sparkles_mask_psd_c38399ee.vtex) with forceOpaque: false
                    var maskTex = LoadVtexTexture(package, matResource, "g_tColor", forceOpaque: false)
                               ?? LoadVtexTexture(package, matResource, "TextureColor", forceOpaque: false)
                               ?? LoadVtexTexture(package, matResource, "g_tSelfIllumMask", forceOpaque: false)
                               ?? LoadVtexTexture(package, matResource, "g_tColor1", forceOpaque: false)
                               ?? getOrLoadTexture(package, "models/heroes_wip/lash/materials/lash_sparkles_mask_psd_c38399ee.vtex", forceOpaque: false)
                               ?? getOrLoadTexture(package, "lash_sparkles_mask_psd_c38399ee.vtex", forceOpaque: false);

                    if (maskTex != null)
                    {
                        shaderMat.SetShaderParameter("texture_albedo", maskTex);
                        hasAlbedo = true;
                        GD.Print($"[Lash Sparkles] Successfully bound mask texture to 'texture_albedo'");
                    }
                    else
                    {
                        GD.PrintErr($"[Lash Sparkles] Failed to load sparkle mask texture for material: {vmatPath}");
                    }
                }

                bool hasNormal = BindTextureIfPresent(package, matResource, "g_tNormal", shaderMat, "texture_normal_roughness", true)
                              || BindTextureIfPresent(package, matResource, "g_tNormalRoughness", shaderMat, "texture_normal_roughness", true);
                if (hasNormal) shaderMat.SetShaderParameter("use_normal_map", true);

                bool hasSelfIllum = BindTextureIfPresent(package, matResource, "g_tSelfIllumMask", shaderMat, "texture_self_illum_mask", true)
                                 || BindTextureIfPresent(package, matResource, "g_tSelfIllum", shaderMat, "texture_self_illum_mask", true)
                                 || BindTextureIfPresent(package, matResource, "TextureSelfIllumMask", shaderMat, "texture_self_illum_mask", true);

                bool hasTrans = BindTextureIfPresent(package, matResource, "g_tAltTranslucency", shaderMat, "texture_translucent_mask", true)
                             || BindTextureIfPresent(package, matResource, "g_tTranslucent", shaderMat, "texture_translucent_mask", true)
                             || BindTextureIfPresent(package, matResource, "g_tTranslucentFilter", shaderMat, "texture_translucent_mask", true)
                             || BindTextureIfPresent(package, matResource, "TextureTranslucency", shaderMat, "texture_translucent_mask", true)
                             || BindTextureIfPresent(package, matResource, "TextureTranslucency1", shaderMat, "texture_translucent_mask", true);
                if (hasTrans) shaderMat.SetShaderParameter("use_translucent_mask", true);

                // Bind compatibility parameters for source2_dynamic_glow.gdshader
                BindTextureIfPresent(package, matResource, "g_tSelfIllumMask", shaderMat, "noise_mask", true);
                BindTextureIfPresent(package, matResource, "g_tColor", shaderMat, "noise_mask", true);
                BindTextureIfPresent(package, matResource, "TextureColor", shaderMat, "noise_mask", true);
                shaderMat.SetShaderParameter("scroll_speed", siSpd);
                shaderMat.SetShaderParameter("fresnel_exponent", siFresnelExp > 0.01f ? siFresnelExp : 2.0f);

                // 6. Set render priority
                if (isArmGlow || isFxAdditive || isSparkle)
                {
                    shaderMat.RenderPriority = 2;
                }
                else if (isHeadGlow)
                {
                    shaderMat.RenderPriority = 0; // Solid depth-tested geometry
                }
                else if (hasJitter || vmatLower.Contains("jitter"))
                {
                    // Render behind the main character mesh (RenderPriority = -1 vs 0) for full foreground clarity
                    shaderMat.RenderPriority = -1;
                }
                else if (isFxAlphaTest)
                {
                    shaderMat.RenderPriority = 0;
                }

                return shaderMat;
            }
        }

        // 2. Superficie PBR estándar
        var godotMat = new StandardMaterial3D();

        // Flags de renderizado, blend mode, vidrio y transparencia con VmatColorExtractor
        bool isAdditive = matResource.IntParams.TryGetValue("F_ADDITIVE_BLEND", out long addVal) && addVal == 1;
        bool isAlphaTest = matResource.IntParams.TryGetValue("F_ALPHA_TEST", out long alphaTestVal) && alphaTestVal == 1;
        bool isVertColorMat = matResource.IntParams.GetValueOrDefault("F_VERTEX_COLOR", 0) == 1 || vmatLower.Contains("vertcolor");
        bool isFur = vmatLower.Contains("fur") || (meshName != null && meshName.Contains("fur", StringComparison.OrdinalIgnoreCase));
        // Ivy/Tengu pupils: flat alpha-card decals that need scissor cutout over the sclera.
        // Detected by path containing both a vertcolor marker AND an Ivy/Tengu hero path segment.
        bool isIvyPupil = isVertColorMat &&
                          (vmatLower.Contains("ivy") || vmatLower.Contains("tengu"));
        // Solid hair/head volumes (Wraith, McGinnis, Mirage, etc.) use vertcolor_pbr_basic but are
        // fully opaque geometry — AlphaScissor on these meshes breaks depth writes and washes vertex colors.
        // Exclude thin fur/hair alpha cards from solid opaque treatment.
        bool isSolidVertColorHair = isVertColorMat && !isIvyPupil && !isFur;

        bool renderBackfaces = (matResource.IntParams.TryGetValue("F_RENDER_BACKFACES", out long rbfVal) && rbfVal == 1)
                            || vmatLower.Contains("ivy_bodyv3") || vmatLower.Contains("ivy") || vmatLower.Contains("tengu")
                            || isIvyPupil
                            || isFur; // Fur shells need two-sided card rendering
        bool isEyelashOrShadow = vmatLower.Contains("eyelash") || vmatLower.Contains("lashes") || vmatLower.Contains("eyeshadow");

        if (isAdditive)
        {
            godotMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            godotMat.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
            godotMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            godotMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        }
        else if (isAlphaTest || isEyelashOrShadow || isFur)
        {
            godotMat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            float alphaRef = 0.5f;
            if (matResource.FloatParams.TryGetValue("g_flAlphaTestReference1", out var ar1)) alphaRef = ar1;
            else if (matResource.FloatParams.TryGetValue("g_flAlphaTestReference", out var ar0)) alphaRef = ar0;
            else if (isFur) alphaRef = 0.35f;
            godotMat.AlphaScissorThreshold = alphaRef;
            godotMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            godotMat.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always;
            if (isFur && isVertColorMat)
            {
                godotMat.VertexColorUseAsAlbedo = true;
            }
        }
        else if (isIvyPupil)
        {
            // Ivy's pupils are thin alpha-card decals: they need scissor cutout to punch through the sclera.
            godotMat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            godotMat.AlphaScissorThreshold = 0.1f;
            godotMat.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always;
            godotMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            godotMat.VertexColorUseAsAlbedo = true;
        }
        else if (isSolidVertColorHair)
        {
            // Solid opaque hair/head geometry (McGinnis, Wraith, Mirage) — keep proper depth writes.
            // DO NOT apply AlphaScissor: it breaks depth-buffer writes and desaturates vertex colors.
            godotMat.Transparency = BaseMaterial3D.TransparencyEnum.Disabled;
            godotMat.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.OpaqueOnly;
            godotMat.CullMode = BaseMaterial3D.CullModeEnum.Back;
            godotMat.VertexColorUseAsAlbedo = true;
        }
        else if (glassParams.IsTranslucent)
        {
            godotMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            godotMat.BlendMode = BaseMaterial3D.BlendModeEnum.Mix;
            godotMat.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always;
            godotMat.RenderPriority = 1;
            godotMat.CullMode = glassParams.IsGlass ? BaseMaterial3D.CullModeEnum.Back : BaseMaterial3D.CullModeEnum.Disabled;
            godotMat.Roughness = glassParams.Roughness;
            godotMat.Metallic = glassParams.Metallic;
            if (glassParams.Clearcoat > 0.01f)
            {
                godotMat.ClearcoatEnabled = true;
                godotMat.Clearcoat = glassParams.Clearcoat;
                godotMat.ClearcoatRoughness = glassParams.ClearcoatRoughness;
            }
            godotMat.RefractionEnabled = true;
            godotMat.RefractionScale = glassParams.RefractAmount;
        }

        if (renderBackfaces)
        {
            godotMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        }

        var albedoParams = VmatColorExtractor.ExtractAlbedo(matResource, vmatPath, glassParams.IsGlass, glassParams.Opacity);

        bool forceOpaqueColor = !isAdditive && !isAlphaTest && !isEyelashOrShadow && !isFur && !glassParams.IsTranslucent;
        string diffusePath = albedoParams.ColorTexturePath;
        string nprTransTexturePath = getTexture(matResource, "g_tNprTransmissiveColor")
                                  ?? getTexture(matResource, "TextureNprTransmissiveColor")
                                  ?? getTexture(matResource, "TextureNprTramsissiveColor");

        bool hasNprTransmissiveVector = matResource.VectorParams.ContainsKey("TextureNprTramsissiveColor1")
                                     || matResource.VectorParams.ContainsKey("TextureNprTransmissiveColor1")
                                     || matResource.VectorParams.ContainsKey("TextureNprTramsissiveColor")
                                     || matResource.VectorParams.ContainsKey("TextureNprTransmissiveColor");

        bool isMirageVertColor = vmatLower.Contains("miragev3_vertcolor");
        bool isPaintVertexColors = matResource.IntParams.GetValueOrDefault("F_PAINT_VERTEX_COLORS", 0) == 1;

        bool isDevDummyTexture = string.IsNullOrEmpty(diffusePath) || diffusePath.Contains("498635a");
        bool isNprTransmissive = !string.IsNullOrEmpty(diffusePath) && diffusePath.Contains("nprtransmissive", StringComparison.OrdinalIgnoreCase);

        bool isNprVertColorSurface = !isFur && (isDevDummyTexture 
                                  || isNprTransmissive 
                                  || isMirageVertColor
                                  || ((isVertColorMat || isPaintVertexColors) && (!string.IsNullOrEmpty(nprTransTexturePath) || hasNprTransmissiveVector)));

        bool hasRealTexture = !string.IsNullOrEmpty(diffusePath) && !isNprVertColorSurface;

        if (hasRealTexture)
        {
            godotMat.AlbedoTexture = getOrLoadTexture(package, diffusePath, forceOpaqueColor);
            if (!isFur || !isVertColorMat)
            {
                godotMat.VertexColorUseAsAlbedo = false;
            }
            godotMat.AlbedoColor = Colors.White;
        }
        else if (isNprVertColorSurface)
        {
            // === NPR VERTEX-COLOR HEAD & HAIR (Wraith, Mirage, etc.) ===
            // F_VERTEX_COLOR = 1 or F_PAINT_VERTEX_COLORS = 1 with dummy albedo tile or transmissive texture.
            // Per pbr.frag.slang: the vertex color stream IS the baked diffuse albedo.
            var pbrHeadShader = GetPbrHeadShader();
            if (pbrHeadShader != null)
            {
                var pbrMat = new ShaderMaterial { Shader = pbrHeadShader };

                // 1. Base skin / hair transmissive tint
                // Priority A: compiled texture (e.g. Mirage g_tNprTransmissiveColor: ffc09775.vtex)
                ImageTexture transTex = null;
                if (!string.IsNullOrEmpty(nprTransTexturePath))
                {
                    transTex = getOrLoadTexture(package, nprTransTexturePath, forceOpaque: true);
                }

                if (transTex != null)
                {
                    pbrMat.SetShaderParameter("g_tNprTransmissiveColor", transTex);
                    pbrMat.SetShaderParameter("has_transmissive_texture", true);
                }
                else
                {
                    pbrMat.SetShaderParameter("has_transmissive_texture", false);
                }

                // Priority B: Vector parameter (e.g. Wraith TextureNprTramsissiveColor1 = [0.556, 0.361, 0.274, 1.0])
                Color skinColor = new Color(0.556f, 0.361f, 0.274f, 1.0f);
                System.Numerics.Vector4 nprVec = default;
                if (matResource.VectorParams.TryGetValue("TextureNprTramsissiveColor1", out nprVec)
                    || matResource.VectorParams.TryGetValue("TextureNprTransmissiveColor1", out nprVec)
                    || matResource.VectorParams.TryGetValue("TextureNprTramsissiveColor", out nprVec)
                    || matResource.VectorParams.TryGetValue("TextureNprTransmissiveColor", out nprVec))
                {
                    if (nprVec.X > 0.001f || nprVec.Y > 0.001f || nprVec.Z > 0.001f)
                    {
                        skinColor = new Color(nprVec.X, nprVec.Y, nprVec.Z, 1.0f);
                    }
                }
                pbrMat.SetShaderParameter("g_vSkinColor", skinColor);

                // CSB colour-correction matrix from g_vAlbedoContrastSaturationBrightness1
                Godot.Projection csbMatrix = Godot.Projection.Identity;
                System.Numerics.Vector4 csbVec = default;
                bool hasCsb = matResource.VectorParams.TryGetValue("g_vAlbedoContrastSaturationBrightness1", out csbVec)
                           || matResource.VectorParams.TryGetValue("g_vAlbedoContrastSaturationBrightness", out csbVec);
                if (hasCsb && (csbVec.X != 1.0f || csbVec.Y != 1.0f || csbVec.Z != 1.0f))
                {
                    System.Numerics.Vector4 colorOffset = default;
                    matResource.VectorParams.TryGetValue("g_vAlbedoColorOffset1", out colorOffset);
                    csbMatrix = CalculateAlbedoColorCorrectMatrix(
                        new System.Numerics.Vector3(csbVec.X, csbVec.Y, csbVec.Z),
                        new System.Numerics.Vector3(colorOffset.X, colorOffset.Y, colorOffset.Z)
                    );
                }
                pbrMat.SetShaderParameter("g_mAlbedoColorCorrect", csbMatrix);

                // 2. Normal & Roughness map (ebebd272)
                string nrPath = getTexture(matResource, "g_tNormalRoughness")
                             ?? getTexture(matResource, "TextureNormalRoughness")
                             ?? getTexture(matResource, "g_tNormal")
                             ?? getTexture(matResource, "TextureNormal");
                bool hasNormal = false;
                if (!string.IsNullOrEmpty(nrPath))
                {
                    var nrTex = getOrLoadTexture(package, nrPath, forceOpaque: true);
                    if (nrTex != null)
                    {
                        pbrMat.SetShaderParameter("g_tNormalRoughness", nrTex);
                        hasNormal = true;
                    }
                }
                pbrMat.SetShaderParameter("normal_map_depth", hasNormal ? 1.0f : 0.0f);

                // 3. Roughness scalar (in Blender, Blue channel drives roughness directly, roughness scale = 1.0)
                float pbrRoughness = 1.0f;
                pbrMat.SetShaderParameter("roughness", pbrRoughness);
                pbrMat.SetShaderParameter("metallic", 0.0f);

                // 4. Ambient Occlusion map
                string aoPbrPath = getTexture(matResource, "g_tAmbientOcclusion")
                                ?? getTexture(matResource, "TextureAmbientOcclusion");
                if (!string.IsNullOrEmpty(aoPbrPath))
                {
                    var aoTex = getOrLoadTexture(package, aoPbrPath, forceOpaque: false);
                    if (aoTex != null)
                    {
                        pbrMat.SetShaderParameter("g_tAmbientOcclusion", aoTex);
                    }
                }

                GD.Print($"[PbrHead] source2_pbr ShaderMaterial for '{vmatPath}' — " +
                         $"hasTransTex={(transTex != null)}, skinColor={skinColor.ToHtml()}, normalMap={hasNormal}, ao={!string.IsNullOrEmpty(aoPbrPath)}");
                return pbrMat;
            }

            // Fallback: shader not available — use StandardMaterial3D with vertex colors as albedo.
            // No skin color tint applied: vertex stream drives pigmentation per pbr.frag.slang.
            godotMat.AlbedoTexture = null;
            godotMat.AlbedoColor = Colors.White;  // neutral — vertex color provides the actual tone
            godotMat.VertexColorUseAsAlbedo = true;
            godotMat.NormalEnabled = false;

            string aoFbPath = getTexture(matResource, "g_tAmbientOcclusion")
                           ?? getTexture(matResource, "TextureAmbientOcclusion");
            if (!string.IsNullOrEmpty(aoFbPath))
            {
                var aoTex = getOrLoadTexture(package, aoFbPath, forceOpaque: false);
                if (aoTex != null)
                {
                    godotMat.AOEnabled = true;
                    godotMat.AOTexture = aoTex;
                    godotMat.AOLightAffect = 0.7f;
                }
            }

            godotMat.Roughness = 0.94f;
            godotMat.Metallic = 0.0f;
            godotMat.MetallicSpecular = 0.05f;
        }
        else
        {
            // Resto de fallbacks genéricos
            godotMat.AlbedoColor = albedoParams.BaseColor;
            godotMat.VertexColorUseAsAlbedo = albedoParams.UseVertexColorAsAlbedo;
        }

        godotMat.Uv1Scale  = albedoParams.UvScale;
        godotMat.Uv1Offset = albedoParams.UvOffset;

        if (isAdditive && matResource.VectorParams.TryGetValue("g_vSelfIllumTint1", out var addTint))
        {
            godotMat.AlbedoColor = new Color(addTint.X, addTint.Y, addTint.Z, 1.0f);
        }

        if (godotMat.AlbedoTexture != null && !isAdditive)
        {
            godotMat.AlbedoColor = Colors.White;
        }

        bool isMirageTexturedSurface = vmatLower.Contains("miragev3_head");
        if (isMirageTexturedSurface && godotMat.AlbedoTexture != null)
        {
            godotMat.VertexColorUseAsAlbedo = false;
        }

        // (Debug block moved — see below, after PBR parameters are fully resolved.)

        // 3. Normal Map
        string normalPath = getTexture(matResource, "g_tNormal") ?? getTexture(matResource, "g_tNormalRoughness");
        if (!string.IsNullOrEmpty(normalPath))
        {
            godotMat.NormalEnabled = true;
            godotMat.NormalTexture = getOrLoadTexture(package, normalPath, forceOpaque: true);
        }

        // 4. Ambient Occlusion
        string aoPath = getTexture(matResource, "g_tAmbientOcclusion") ?? getTexture(matResource, "g_tAO");
        if (!string.IsNullOrEmpty(aoPath))
        {
            godotMat.AOEnabled = true;
            godotMat.AOTexture = getOrLoadTexture(package, aoPath, forceOpaque: true);
        }

        // 5. Emisión / Auto-iluminación PBR data-driven desde el VPK con VmatColorExtractor
        var selfIllumParams = VmatColorExtractor.ExtractSelfIllum(matResource, vmatPath, isAdditive);
        if (selfIllumParams.IsEmissive)
        {
            ImageTexture maskTex = null;
            if (!string.IsNullOrEmpty(selfIllumParams.MaskTexturePath))
            {
                maskTex = getOrLoadTexture(package, selfIllumParams.MaskTexturePath, forceOpaque: true);
            }

            // CRITICAL: If a mask texture was expected but failed to load, do not enable emission
            // to avoid flooding the surface with flat unmasked emission color.
            if (string.IsNullOrEmpty(selfIllumParams.MaskTexturePath) || maskTex != null)
            {
                godotMat.EmissionEnabled = true;
                godotMat.EmissionOperator = BaseMaterial3D.EmissionOperatorEnum.Multiply;
                godotMat.EmissionEnergyMultiplier = Math.Clamp(selfIllumParams.EnergyMultiplier * 0.25f, 1.0f, 2.5f);
                godotMat.Emission = selfIllumParams.EmissionColor;

                if (maskTex != null)
                {
                    godotMat.EmissionTexture = maskTex;
                }
            }
        }

        // 6. Configuración de parámetros PBR con datos reales del VPK
        bool isWeapon = vmatLower.Contains("weapon") || vmatLower.Contains("gun") ||
                        vmatLower.Contains("sword") || vmatLower.Contains("katana") ||
                        vmatLower.Contains("bow") || vmatLower.Contains("shortsword");
        bool isDedicatedGlow = vmatLower.Contains("glow") || vmatLower.Contains("hourglass") ||
                               vmatLower.Contains("portal") || vmatLower.Contains("flame") ||
                               vmatLower.Contains("light") || vmatLower.Contains("beam") || isAdditive;

        if (isDedicatedGlow)
        {
            godotMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        }

        float roughness = isWeapon ? 0.45f : (glassParams.IsGlass ? glassParams.Roughness : 0.65f);
        if (matResource.VectorParams.TryGetValue("TextureRoughness1", out var rVec)) roughness = rVec.X;
        else if (matResource.VectorParams.TryGetValue("TextureRoughness", out var rVec0)) roughness = rVec0.X;
        else if (matResource.FloatParams.TryGetValue("g_flRoughnessScale1", out float rScale)) roughness = rScale;
        godotMat.Roughness = roughness;

        float metallic = isWeapon ? 0.35f : (glassParams.IsGlass ? glassParams.Metallic : (isDedicatedGlow && !isAdditive ? 0.80f : 0.0f));
        if (matResource.VectorParams.TryGetValue("TextureMetalness1", out var mVec)) metallic = mVec.X;
        else if (matResource.VectorParams.TryGetValue("TextureMetalness", out var mVec0)) metallic = mVec0.X;
        else if (matResource.FloatParams.TryGetValue("g_flMetalnessScale1", out float mScale)) metallic = mScale;
        godotMat.Metallic = metallic;

        // Matte head / hair / cloth surfaces — eliminate white glare blowout.
        // Godot's default MetallicSpecular = 0.5 produces blinding reflective patches on near-matte
        // surfaces even at roughness 0.95. Valve's VMATs confirm roughness 0.93–0.97 on all three heroes.
        // Clamp specular down to 0.08 (physical felt/skin equivalent) and pin roughness to 0.95 so
        // the VMAT-read value cannot be accidentally overridden by any earlier weapon/glow branch.
        bool isMatteHeadOrHair = roughness > 0.85f
                              || vmatLower.Contains("hair")
                              || vmatLower.Contains("head")
                              || vmatLower.Contains("vertcolor")
                              || isSolidVertColorHair;
        if (isMatteHeadOrHair)
        {
            godotMat.Roughness = 0.95f;
            godotMat.MetallicSpecular = 0.08f;
        }

        // ── Texture Debug (post-PBR) ───────────────────────────────────────────────────────────────────
        // Placed here so Roughness and Specular reflect the final values actually written to the material.
        // TODO: Remove once the grey/white head issue is confirmed resolved.
        if (vmatLower.Contains("mirage") || vmatLower.Contains("wraith") || vmatLower.Contains("mcginnis"))
        {
            string texDim = godotMat.AlbedoTexture != null
                ? $"{godotMat.AlbedoTexture.GetWidth()}x{godotMat.AlbedoTexture.GetHeight()}"
                : "NULL";
            GD.Print($"[Texture Debug] Material: '{vmatPath}'");
            GD.Print($"  -> diffusePath    : '{diffusePath ?? "NULL"}'");
            GD.Print($"  -> AlbedoTexture  : {(godotMat.AlbedoTexture != null ? "Loaded" : "MISSING")} ({texDim})");
            GD.Print($"  -> VertexColorUseAsAlbedo: {godotMat.VertexColorUseAsAlbedo}");
            GD.Print($"  -> AlbedoColor    : #{godotMat.AlbedoColor.ToHtml()}");
            GD.Print($"  -> Roughness      : {godotMat.Roughness:F3}  Specular: {godotMat.MetallicSpecular:F3}");
            GD.Print($"  -> isDevDummy={isDevDummyTexture}  hasRealTex={hasRealTexture}  isPaletteTile={godotMat.AlbedoTexture == null && hasRealTexture}");
        }
        // ──────────────────────────────────────────────────────────────────────────────────────────────

        // Ivy (Tengu): Entire body, face, and eyes are embedded on a single unified 4096x4096 atlas: ivy_bodyv3.
        // Disable vertex color multiplication so face/eye socket baked AO does not black out the eyes/face.
        if ((vmatLower.Contains("ivy_bodyv3") || vmatLower.Contains("ivy") || vmatLower.Contains("tengu")) && !isVertColorMat)
        {
            godotMat.VertexColorUseAsAlbedo = false;
            godotMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            if (godotMat.AlbedoTexture == null)
            {
                godotMat.AlbedoTexture = getOrLoadTexture(package, "models/heroes_wip/ivy/materials/ivy_bodyv3_color_png_9ca4a4c5.vtex", forceOpaque: true)
                                      ?? getOrLoadTexture(package, "ivy_bodyv3_color_png_9ca4a4c5.vtex", forceOpaque: true);
            }
        }

        bool isEyeSurface = vmatLower.Contains("eye") || vmatLower.Contains("pupil") ||
                            vmatLower.Contains("cornea") || vmatLower.Contains("iris") ||
                            vmatLower.Contains("sclera");

        if (isEyeSurface)
        {
            godotMat.VertexColorUseAsAlbedo = false;
            godotMat.Roughness = 0.15f;
            godotMat.Metallic = 0.0f;
            if (godotMat.AlbedoColor.R <= 0.05f && godotMat.AlbedoColor.G <= 0.05f && godotMat.AlbedoColor.B <= 0.05f)
            {
                godotMat.AlbedoColor = Colors.White;
            }
        }

        return godotMat;
    }

    /// <summary>
    /// Carga una textura .vtex_c desde el VPK dado el material y nombre de parámetro,
    /// o ruta directa, con control explícito de forceOpaque.
    /// </summary>
    public static ImageTexture LoadVtexTexture(Package package, ValveResourceFormat.ResourceTypes.Material mat, string paramName, bool forceOpaque = false)
    {
        string texPath = getTexture(mat, paramName);
        if (!string.IsNullOrEmpty(texPath))
        {
            return getOrLoadTexture(package, texPath, forceOpaque: forceOpaque);
        }
        return null;
    }

    public static ImageTexture LoadVtexTexture(Package package, string texPath, bool forceOpaque = false)
    {
        if (string.IsNullOrEmpty(texPath)) return null;
        return getOrLoadTexture(package, texPath, forceOpaque: forceOpaque);
    }

    private static bool BindTextureIfPresent(Package package, ValveResourceFormat.ResourceTypes.Material mat, string paramName, ShaderMaterial shaderMat, string uniformName, bool forceOpaque = false)
    {
        string texPath = getTexture(mat, paramName);
        if (!string.IsNullOrEmpty(texPath))
        {
            var tex = getOrLoadTexture(package, texPath, forceOpaque: forceOpaque);
            if (tex != null)
            {
                shaderMat.SetShaderParameter(uniformName, tex);
                return true;
            }
        }
        return false;
    }

    private static string getTexture(ValveResourceFormat.ResourceTypes.Material mat, string paramName)
    {
        string path = null;
        if (mat.TextureParams.TryGetValue(paramName, out var p))
        {
            path = p;
        }
        else if (mat.Data != null)
        {
            var texArray = mat.Data.GetArray("m_textureParams");
            if (texArray != null)
            {
                foreach (var item in texArray)
                {
                    if (item.GetStringProperty("m_name")?.Equals(paramName, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        path = item.GetStringProperty("m_pValue");
                        break;
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(path))
        {
            if (path.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(9);
            }
            path = path.Trim('"', '\'', ' ');
        }
        return path;
    }

    private static ImageTexture getOrLoadTexture(Package package, string internalPath, bool forceOpaque)
    {
        if (string.IsNullOrWhiteSpace(internalPath)) return null;

        if (internalPath.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
        {
            internalPath = internalPath.Substring(9);
        }
        internalPath = internalPath.Trim('"', '\'', ' ');

        string cacheKey = internalPath + (forceOpaque ? "_opq" : "");
        if (_textureCache.TryGetValue(cacheKey, out var cached))
        {
            if (GodotObject.IsInstanceValid(cached))
            {
                return cached;
            }
            _textureCache.Remove(cacheKey);
        }

        var tex = ExtractVtexToGodot(package, internalPath, maxDimension: 1024, forceOpaque: forceOpaque);
        if (tex != null)
        {
            _textureCache[cacheKey] = tex;
        }
        return tex;
    }

    public static void cleanCache()
    {
        _textureCache.Clear();
        _dynamicFxShader = null;
        _dynamicFxAddShader = null;
        _flameHairShader = null;
        _dynamicGlowShader = null;
        _glassShader = null;
        _heroOutlineShader = null;
        _lashSparklesShader = null;
        _wraithCardShader = null;
        _pbrShader = null;
    }

    /// <summary>
    /// Data-driven extraction hierarchy pulling authentic emissive/fire/aura glow color from Valve VMAT parameters.
    /// Eliminates white/grey fallback caused by g_vColorTint1 diffuse multipliers.
    /// </summary>
    public static Color ExtractDynamicGlowColor(ValveResourceFormat.ResourceTypes.Material vmat, string vmatPath)
    {
        if (vmat == null) return Colors.White;

        // Priority 1: Self-illumination tint holds the true emissive/fire color
        if (vmat.VectorParams.TryGetValue("g_vSelfIllumTint1", out var siTint1) && !IsNeutralWhiteOrBlack(siTint1))
        {
            var c = new Color(siTint1.X, siTint1.Y, siTint1.Z, 1.0f);
            GD.Print($"[ExtractDynamicGlowColor] {vmatPath} -> Priority 1 (g_vSelfIllumTint1): #{c.ToHtml()}");
            return c;
        }
        if (vmat.VectorParams.TryGetValue("g_vSelfIllumTint", out var siTint0) && !IsNeutralWhiteOrBlack(siTint0))
        {
            var c = new Color(siTint0.X, siTint0.Y, siTint0.Z, 1.0f);
            GD.Print($"[ExtractDynamicGlowColor] {vmatPath} -> Priority 1 (g_vSelfIllumTint): #{c.ToHtml()}");
            return c;
        }

        // Priority 2: NPR Transmissive color (used in Deadlock stylized energy shaders)
        if (vmat.VectorParams.TryGetValue("TextureNprTramsissiveColor1", out var nprColor1) && !IsNeutralWhiteOrBlack(nprColor1))
        {
            var c = new Color(nprColor1.X, nprColor1.Y, nprColor1.Z, 1.0f);
            GD.Print($"[ExtractDynamicGlowColor] {vmatPath} -> Priority 2 (TextureNprTramsissiveColor1): #{c.ToHtml()}");
            return c;
        }
        if (vmat.VectorParams.TryGetValue("TextureNprTransmissiveColor1", out var nprColor1_alt) && !IsNeutralWhiteOrBlack(nprColor1_alt))
        {
            var c = new Color(nprColor1_alt.X, nprColor1_alt.Y, nprColor1_alt.Z, 1.0f);
            GD.Print($"[ExtractDynamicGlowColor] {vmatPath} -> Priority 2 (TextureNprTransmissiveColor1): #{c.ToHtml()}");
            return c;
        }
        if (vmat.VectorParams.TryGetValue("TextureNprTramsissiveColor", out var nprColor0) && !IsNeutralWhiteOrBlack(nprColor0))
        {
            var c = new Color(nprColor0.X, nprColor0.Y, nprColor0.Z, 1.0f);
            GD.Print($"[ExtractDynamicGlowColor] {vmatPath} -> Priority 2 (TextureNprTramsissiveColor): #{c.ToHtml()}");
            return c;
        }
        if (vmat.VectorParams.TryGetValue("TextureNprTransmissiveColor", out var nprColor0_alt) && !IsNeutralWhiteOrBlack(nprColor0_alt))
        {
            var c = new Color(nprColor0_alt.X, nprColor0_alt.Y, nprColor0_alt.Z, 1.0f);
            GD.Print($"[ExtractDynamicGlowColor] {vmatPath} -> Priority 2 (TextureNprTransmissiveColor): #{c.ToHtml()}");
            return c;
        }

        // Priority 3: General color tint (only if explicitly tinted away from pure white/black/grey)
        if (vmat.VectorParams.TryGetValue("g_vColorTint1", out var colTint1) && !IsNeutralWhiteOrBlack(colTint1))
        {
            var c = new Color(colTint1.X, colTint1.Y, colTint1.Z, 1.0f);
            GD.Print($"[ExtractDynamicGlowColor] {vmatPath} -> Priority 3 (g_vColorTint1): #{c.ToHtml()}");
            return c;
        }
        if (vmat.VectorParams.TryGetValue("g_vColorTint", out var colTint0) && !IsNeutralWhiteOrBlack(colTint0))
        {
            var c = new Color(colTint0.X, colTint0.Y, colTint0.Z, 1.0f);
            GD.Print($"[ExtractDynamicGlowColor] {vmatPath} -> Priority 3 (g_vColorTint): #{c.ToHtml()}");
            return c;
        }

        // Safe signature fallbacks if the VMAT parameters are missing or uncompiled:
        string vmatLower = vmatPath?.ToLowerInvariant() ?? "";
        if (vmatLower.Contains("inferno") || vmatLower.Contains("armglow") || vmatLower.Contains("flame"))
        {
            var c = new Color(0.835294f, 0.431373f, 0.0f, 1.0f);      // Infernus fire #D56E00
            GD.Print($"[ExtractDynamicGlowColor] {vmatPath} -> Infernus fallback: #{c.ToHtml()}");
            return c;
        }
        if (vmatLower.Contains("geist") || vmatLower.Contains("ghost"))
        {
            var c = new Color(0.717647f, 0.109804f, 0.109804f, 1.0f); // Geist blood #B71C1C
            GD.Print($"[ExtractDynamicGlowColor] {vmatPath} -> Lady Geist fallback: #{c.ToHtml()}");
            return c;
        }
        if (vmatLower.Contains("hornet") || vmatLower.Contains("vindicta"))
        {
            var c = new Color(0.392157f, 0.709804f, 0.964706f, 1.0f); // Vindicta cyan #64B5F6
            GD.Print($"[ExtractDynamicGlowColor] {vmatPath} -> Vindicta fallback: #{c.ToHtml()}");
            return c;
        }
        if (vmatLower.Contains("wraith") || vmatLower.Contains("card"))
        {
            var c = new Color(0.663f, 0.404f, 0.961f, 1.0f); // Wraith psychic bright lilac #A967F5
            GD.Print($"[ExtractDynamicGlowColor] {vmatPath} -> Wraith lilac fallback: #{c.ToHtml()}");
            return c;
        }

        GD.Print($"[ExtractDynamicGlowColor] {vmatPath} -> Default White fallback");
        return Colors.White;
    }
public static Godot.Projection CalculateAlbedoColorCorrectMatrix(System.Numerics.Vector3 csb, System.Numerics.Vector3 colorOffset)
{
    var cross = System.Numerics.Vector3.Cross(LumCoeffsNormalised, System.Numerics.Vector3.UnitZ);
    var angle = MathF.Atan2(cross.Length(), System.Numerics.Vector3.Dot(LumCoeffsNormalised, System.Numerics.Vector3.UnitZ));
    var rotation = System.Numerics.Matrix4x4.CreateFromAxisAngle(System.Numerics.Vector3.Normalize(cross), angle);

    var result = System.Numerics.Matrix4x4.CreateTranslation(-colorOffset) 
               * System.Numerics.Matrix4x4.CreateScale(csb.X) 
               * System.Numerics.Matrix4x4.CreateTranslation(colorOffset);
    result *= System.Numerics.Matrix4x4.CreateScale(csb.Z);
    result *= System.Numerics.Matrix4x4.CreateScale(LumCoeffsNormalised);
    result *= rotation;
    result *= System.Numerics.Matrix4x4.CreateScale(csb.Y, csb.Y, 1f);
    result *= System.Numerics.Matrix4x4.Transpose(rotation);
    result *= System.Numerics.Matrix4x4.CreateScale(System.Numerics.Vector3.One / LumCoeffsNormalised);

    var finalMat = System.Numerics.Matrix4x4.Transpose(result);

    // Convertir a Projection de Godot (mat4 de shader)
    return new Godot.Projection(
        new Godot.Vector4(finalMat.M11, finalMat.M12, finalMat.M13, finalMat.M14),
        new Godot.Vector4(finalMat.M21, finalMat.M22, finalMat.M23, finalMat.M24),
        new Godot.Vector4(finalMat.M31, finalMat.M32, finalMat.M33, finalMat.M34),
        new Godot.Vector4(finalMat.M41, finalMat.M42, finalMat.M43, finalMat.M44)
    );
}
    public static bool IsNeutralWhiteOrBlack(System.Numerics.Vector4 v)
    {
        bool isZero = v.X < 0.05f && v.Y < 0.05f && v.Z < 0.05f;
        bool isWhite = v.X > 0.95f && v.Y > 0.95f && v.Z > 0.95f;
        if (isZero || isWhite) return true;

        // Grayscale check: if difference between max and min color channels is less than 0.08,
        // it is a monochromatic luminance scalar, NOT an authentic chromatic hue.
        float max = MathF.Max(v.X, MathF.Max(v.Y, v.Z));
        float min = MathF.Min(v.X, MathF.Min(v.Y, v.Z));
        return (max - min) < 0.08f;
    }
}