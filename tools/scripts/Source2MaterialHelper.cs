using System;
using System.IO;
using System.Collections.Generic;
using Godot;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using SkiaSharp;

public static class Source2MaterialHelper
{
    // Diccionario caché para no decodificar dos veces la misma textura si se repite
    private static readonly Dictionary<string, ImageTexture> _textureCache = new();

    /// <summary>
    /// Extrae un vtex_c a un ImageTexture nativo de Godot rápidamente reduciendo su tamaño.
    /// Si forceOpaque es true (por defecto para texturas de albedo/normal/ao opacas),
    /// fuerza el canal Alpha a 255 para evitar que la compresión PNG de Skia anule el RGB (A=0 en BC7).
    /// </summary>
    public static ImageTexture ExtractVtexToGodot(Package package, string vtexInternalPath, int maxDimension = 1024, bool forceOpaque = true)
    {
        var entry = package.FindEntry(vtexInternalPath);
        if (entry == null)
        {
            if (!vtexInternalPath.EndsWith("_c"))
            {
                entry = package.FindEntry(vtexInternalPath + "_c");
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
        // Si no se fuerza a 255 en texturas opacas, Skia aplica premultiplicación a 0 al exportar a PNG,
        // borrando todos los canales RGB a negro/vacío.
        if (forceOpaque)
        {
            var span = skBitmap.GetPixelSpan();
            int totalPixels = skBitmap.Width * skBitmap.Height;
            for (int i = 0; i < totalPixels; i++)
            {
                span[i * 4 + 3] = 255;
            }
        }

        // Convertimos el bitmap a bytes PNG en memoria usando SkiaSharp
        using var skImage = SKImage.FromBitmap(skBitmap);
        using var encodedData = skImage.Encode(SKEncodedImageFormat.Png, 85);
        byte[] pngBytes = encodedData.ToArray();

        // Cargamos la imagen nativa en Godot
        var godotImage = new Image();
        Error err = godotImage.LoadPngFromBuffer(pngBytes);
        if (err != Error.Ok)
        {
            GD.PrintErr($"[MaterialHelper] Error al cargar buffer PNG en Godot: {err}");
            return null;
        }

        return ImageTexture.CreateFromImage(godotImage);
    }

    /// <summary>
    /// Lee un archivo .vmat_c y crea un StandardMaterial3D de Godot limpio, robusto y genérico.
    /// </summary>
    public static StandardMaterial3D CreateMaterialFromVmat(Package package, string vmatPath)
    {
        if (!vmatPath.EndsWith("_c")) vmatPath += "_c";

        var entry = package.FindEntry(vmatPath);
        if (entry == null)
        {
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
        var godotMat = new StandardMaterial3D();
        string vmatLower = vmatPath.ToLowerInvariant();

        // 1. Flags de renderizado, blend mode y transparencia
        bool isAdditive = matResource.IntParams.TryGetValue("F_ADDITIVE_BLEND", out long addVal) && addVal == 1;
        bool isTranslucent = (matResource.IntParams.TryGetValue("F_TRANSLUCENT", out long transVal) && transVal == 1) ||
                             (matResource.IntParams.TryGetValue("F_GLASS", out long glassVal) && glassVal == 1);

        if (isAdditive)
        {
            godotMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            godotMat.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
            godotMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            godotMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        }
        else if (isTranslucent)
        {
            godotMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            godotMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        }

        // 2. Textura de Color / Albedo
        string colorPath = getTexture(matResource, "g_tColor") ??
                           getTexture(matResource, "TextureColor") ??
                           getTexture(matResource, "g_tColor1") ??
                           getTexture(matResource, "g_tColorA");

        // En Source 2, las texturas BC7 diffuse suelen tener A=0 en su canal alfa (usado para máscaras internas).
        // Siempre forzamos opaco el diffuse a menos que sea aditivo para no perder los canales RGB al codificar PNG.
        bool forceOpaqueColor = !isAdditive;
        if (!string.IsNullOrEmpty(colorPath))
        {
            godotMat.AlbedoTexture = getOrLoadTexture(package, colorPath, forceOpaqueColor);
        }
        else
        {
            // Si no hay textura de color, verificar vectores de tinte
            if (matResource.VectorParams.TryGetValue("TextureColor1", out var tc))
            {
                godotMat.AlbedoColor = new Color(tc.X, tc.Y, tc.Z, 1.0f);
            }
            else if (matResource.VectorParams.TryGetValue("g_vColorTint1", out var ct))
            {
                godotMat.AlbedoColor = new Color(ct.X, ct.Y, ct.Z, 1.0f);
            }
        }

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

        // 5. Emisión / Auto-iluminación PBR
        // En Source 2 Deadlock, muchos materiales tienen F_SELF_ILLUM=1 pero también F_USE_STATUS_EFFECTS_PROXY=1.
        // F_USE_STATUS_EFFECTS_PROXY indica que la auto-iluminación es un proxy dinámico para efectos de juego
        // (como el láser de Bebop, la furia de Shiv, o habilidades de Paradox).
        // Habilitar emisión estática en estos materiales quema las texturas a blanco puro o causa coloraciones falsas (verde/rosa).
        // Por tanto, la emisión base solo se activa si F_USE_STATUS_EFFECTS_PROXY != 1 y tiene una máscara válida no-dummy.
        bool isStatusProxy = matResource.IntParams.TryGetValue("F_USE_STATUS_EFFECTS_PROXY", out long proxyVal) && proxyVal == 1;
        bool hasSelfIllumFlag = (matResource.IntParams.TryGetValue("F_SELF_ILLUM", out long selfIllum) && selfIllum == 1) ||
                                (matResource.IntParams.TryGetValue("F_EMISSIVE", out long emissive) && emissive == 1);
        string selfIllumMaskPath = getTexture(matResource, "g_tSelfIllumMask") ?? getTexture(matResource, "TextureSelfIllumMask");
        bool hasValidIllumMask = !string.IsNullOrEmpty(selfIllumMaskPath) && !selfIllumMaskPath.ToLowerInvariant().Contains("default_mask");

        bool maskIsBlack = matResource.VectorParams.TryGetValue("TextureSelfIllumMask1", out var tcMask) &&
                           tcMask.X <= 0.001f && tcMask.Y <= 0.001f && tcMask.Z <= 0.001f;

        if (hasSelfIllumFlag && hasValidIllumMask && !isStatusProxy && !maskIsBlack)
        {
            var maskTex = getOrLoadTexture(package, selfIllumMaskPath, forceOpaque: true);
            if (maskTex != null)
            {
                godotMat.EmissionEnabled = true;
                godotMat.EmissionTexture = maskTex;

                if (matResource.VectorParams.TryGetValue("g_vSelfIllumTint1", out var illumVec))
                {
                    godotMat.Emission = new Color(illumVec.X, illumVec.Y, illumVec.Z);
                }
                else
                {
                    godotMat.Emission = Colors.White;
                }
                godotMat.EmissionEnergyMultiplier = 1.0f;
            }
        }

        // 6. Configuración de parámetros PBR por defecto
        bool isWeapon = vmatLower.Contains("weapon") || vmatLower.Contains("gun") ||
                        vmatLower.Contains("sword") || vmatLower.Contains("katana") ||
                        vmatLower.Contains("bow") || vmatLower.Contains("shortsword");
        if (isWeapon)
        {
            godotMat.Metallic = 0.75f;
            godotMat.Roughness = 0.35f;
        }
        else
        {
            godotMat.Roughness = 0.6f;
            godotMat.Metallic = 0.1f;
        }

        return godotMat;
    }

    private static string getTexture(ValveResourceFormat.ResourceTypes.Material mat, string paramName)
    {
        if (mat.TextureParams.TryGetValue(paramName, out string path))
        {
            return path;
        }
        return null;
    }

    private static ImageTexture getOrLoadTexture(Package package, string internalPath, bool forceOpaque)
    {
        string cacheKey = internalPath + (forceOpaque ? "_opq" : "");
        if (_textureCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
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
    }
}