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

	/// /// <summary>
    /// Extrae un vtex_c a un ImageTexture nativo de Godot rápidamente reduciendo su tamaño.
    /// </summary>
    public static ImageTexture ExtractVtexToGodot(Package package, string vtexInternalPath, int maxDimension = 1024)
    {
        var entry = package.FindEntry(vtexInternalPath);
        if (entry == null)
        {
            // A veces las referencias en Source 2 terminan en .vtex en vez de .vtex_c
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

        // Usamos la API de VRF para extraer el bitmap crudo de SkiaSharp
        var texture = (ValveResourceFormat.ResourceTypes.Texture)resource.DataBlock;
        
        // Mip 0 = 100% (4K), Mip 1 = 50% (2K), Mip 2 = 25% (1024px)
        // Pedir un mipmap inferior es instantáneo y ahorra RAM
        uint maxMip = texture.NumMipLevels > 0 ? (uint)(texture.NumMipLevels - 1) : 0;
        uint mipLevel = 0;
        if (texture.Width > maxDimension || texture.Height > maxDimension)
        {
            mipLevel = (uint)Math.Min(2, maxMip);
        }

        SKBitmap skBitmap;
        try
        {
            skBitmap = texture.GenerateBitmap(mipLevel);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Algunas texturas no soportan mips mayores a 0 (ej: texturas de profundidad)
            skBitmap = texture.GenerateBitmap(0);
        }

        if (skBitmap == null) return null;
        using var _ = skBitmap;

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
    /// Lee un archivo .vmat_c y crea un StandardMaterial3D de Godot configurado.
    /// </summary>
	/// 
    public static StandardMaterial3D CreateMaterialFromVmat(Package package, string vmatPath)
    {
        // Normalizamos extensión
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

        // 1. Textura de Color / Albedo (en Source 2 suele llamarse "g_tColor")
        string colorPath = getTexture(matResource, "g_tColor");
        if (!string.IsNullOrEmpty(colorPath))
        {
            godotMat.AlbedoTexture = getOrLoadTexture(package, colorPath);
        }

        // 2. Normal Map (suele llamarse "g_tNormal" o "g_tNormalRoughness")
        string normalPath = getTexture(matResource, "g_tNormal");
        if (!string.IsNullOrEmpty(normalPath))
        {
            godotMat.NormalEnabled = true;
            godotMat.NormalTexture = getOrLoadTexture(package, normalPath);
        }

        // 3. Ambient Occlusion ("g_tAmbientOcclusion" o "g_tAO")
        string aoPath = getTexture(matResource, "g_tAmbientOcclusion");
        if (!string.IsNullOrEmpty(aoPath))
        {
            godotMat.AOEnabled = true;
            godotMat.AOTexture = getOrLoadTexture(package, aoPath);
        }

        // Valores PBR base por defecto
        godotMat.Roughness = 0.6f;
        godotMat.Metallic = 0.1f;

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

    private static ImageTexture getOrLoadTexture(Package package, string internalPath)
    {
        if (_textureCache.TryGetValue(internalPath, out var cached))
        {
            return cached;
        }

        var tex = ExtractVtexToGodot(package, internalPath, maxDimension: 1024);
        if (tex != null)
        {
            _textureCache[internalPath] = tex;
        }
        return tex;
    }

    public static void cleanCache()
    {
        _textureCache.Clear();
    }
}