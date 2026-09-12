using System;
using System.IO;
using System.Collections.Generic;
using Godot;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using SkiaSharp;
using VrfMaterial = ValveResourceFormat.ResourceTypes.Material;

namespace DeadlockPlayground.Materials;

/// <summary>
/// Handles extraction, format conversion (BGRA -> RGBA), mipmap decoding,
/// and session caching for Valve Source 2 textures (.vtex_c).
/// </summary>
public static class Source2TextureLoader
{
    private static readonly Dictionary<string, ImageTexture> _textureCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Clears the session texture cache. Call between hero unloads to free GPU memory.
    /// </summary>
    public static void CleanCache()
    {
        _textureCache.Clear();
    }

    /// <summary>
    /// Retrieves a cached texture or extracts it from the VPK.
    /// </summary>
    public static ImageTexture GetOrLoadTexture(Package package, string internalPath, bool forceOpaque)
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
            tex.ResourceName = internalPath;
            _textureCache[cacheKey] = tex;
        }
        return tex;
    }

    /// <summary>
    /// Loads a texture defined by parameter name from a VMAT resource.
    /// </summary>
    public static ImageTexture LoadVtexTexture(Package package, VrfMaterial mat, string paramName, bool forceOpaque = false)
    {
        string texPath = GetTextureParam(mat, paramName);
        if (!string.IsNullOrEmpty(texPath))
        {
            return GetOrLoadTexture(package, texPath, forceOpaque: forceOpaque);
        }
        return null;
    }

    /// <summary>
    /// Loads a texture directly by path.
    /// </summary>
    public static ImageTexture LoadVtexTexture(Package package, string texPath, bool forceOpaque = false)
    {
        if (string.IsNullOrEmpty(texPath)) return null;
        return GetOrLoadTexture(package, texPath, forceOpaque: forceOpaque);
    }

    /// <summary>
    /// Helper to bind a texture uniform on a ShaderMaterial if the parameter exists in the VMAT.
    /// </summary>
    public static bool BindTextureIfPresent(Package package, VrfMaterial mat, string paramName, ShaderMaterial shaderMat, string uniformName, bool forceOpaque = false)
    {
        string texPath = GetTextureParam(mat, paramName);
        if (!string.IsNullOrEmpty(texPath))
        {
            var tex = GetOrLoadTexture(package, texPath, forceOpaque: forceOpaque);
            if (tex != null)
            {
                shaderMat.SetShaderParameter(uniformName, tex);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Resolves texture path from TextureParams dictionary or m_textureParams KV3 array.
    /// </summary>
    public static string GetTextureParam(VrfMaterial mat, string paramName)
    {
        if (mat == null) return null;

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

    /// <summary>
    /// Extracts a .vtex_c file from the VPK into a Godot ImageTexture.
    /// Converts Skia Bgra8888 -> Rgba8888 and handles forceOpaque vs alpha cutout preservation.
    /// </summary>
    public static ImageTexture ExtractVtexToGodot(Package package, string vtexInternalPath, int maxDimension = 1024, bool forceOpaque = true)
    {
        if (package == null || string.IsNullOrWhiteSpace(vtexInternalPath)) return null;

        if (vtexInternalPath.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
        {
            vtexInternalPath = vtexInternalPath.Substring(9);
        }
        vtexInternalPath = vtexInternalPath.Trim('"', '\'', ' ');

        var entry = FindVtexEntry(package, vtexInternalPath);
        if (entry == null)
        {
            GD.PrintErr($"[TextureLoader] Texture not found in VPK: {vtexInternalPath}");
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
            skBitmap = texture.GenerateBitmap(depth: 0, mipLevel: mipLevel);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[TextureLoader] Error decoding bitmap ({vtexInternalPath}): {ex.Message}");
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

        // In Source 2 (Deadlock), alpha in BC7 is often 0 on opaque textures (used for roughness/masks).
        // If forceOpaque is enabled, set alpha to 255 to prevent transparent black surfaces.
        // Alpha-card textures (fur, sparkles, eyelashes) must preserve their alpha channel!
        bool isSparkle = vtexInternalPath.Contains("sparkle", StringComparison.OrdinalIgnoreCase) ||
                         vtexInternalPath.Contains("lash_sparkles", StringComparison.OrdinalIgnoreCase);
        bool isAlphaCardTexture = !forceOpaque ||
                                  isSparkle ||
                                  vtexInternalPath.Contains("fur", StringComparison.OrdinalIgnoreCase) ||
                                  vtexInternalPath.Contains("card", StringComparison.OrdinalIgnoreCase) ||
                                  vtexInternalPath.Contains("eyelash", StringComparison.OrdinalIgnoreCase) ||
                                  vtexInternalPath.Contains("lashes", StringComparison.OrdinalIgnoreCase);

        // Encode to PNG via Skia. Skia handles internal bitmap format (BGRA8888) to standard PNG conversion reliably.
        using var skImage = SKImage.FromBitmap(skBitmap);
        if (skImage == null) return null;

        using var encodedData = skImage.Encode(SKEncodedImageFormat.Png, 100);
        if (encodedData == null) return null;

        byte[] pngBytes = encodedData.ToArray();
        var godotImage = new Image();
        Error err = godotImage.LoadPngFromBuffer(pngBytes);
        if (err != Error.Ok)
        {
            GD.PrintErr($"[TextureLoader] Error loading PNG buffer ({vtexInternalPath}): {err}");
            return null;
        }

        if (!isAlphaCardTexture)
        {
            // For standard non-card surfaces, remove BC7 roughness/specular masks by converting to Rgb8 and back to Rgba8
            godotImage.Convert(Image.Format.Rgb8);
            godotImage.Convert(Image.Format.Rgba8);
        }
        else
        {
            // For authentic alpha-card textures (fur, sparkles, eyelashes, cards), preserve the author-crafted cutout mask.
            // Safeguard: if the decoded alpha is completely blank (all zeros), convert to opaque so geometry doesn't vanish.
            bool hasVisiblePixels = false;
            int stepX = Math.Max(1, godotImage.GetWidth() / 16);
            int stepY = Math.Max(1, godotImage.GetHeight() / 16);
            for (int y = 0; y < godotImage.GetHeight(); y += stepY)
            {
                for (int x = 0; x < godotImage.GetWidth(); x += stepX)
                {
                    if (godotImage.GetPixel(x, y).A > 0.05f)
                    {
                        hasVisiblePixels = true;
                        break;
                    }
                }
                if (hasVisiblePixels) break;
            }

            if (!hasVisiblePixels)
            {
                godotImage.Convert(Image.Format.Rgb8);
                godotImage.Convert(Image.Format.Rgba8);
            }
        }

        godotImage.GenerateMipmaps();
        var imageTex = ImageTexture.CreateFromImage(godotImage);
        if (imageTex != null)
        {
            imageTex.ResourceName = vtexInternalPath;
        }
        return imageTex;
    }

    /// <summary>
    /// Extracts a representative dominant color from a texture by sampling its 1x1 mip level from the VPK.
    /// Returns null if the texture cannot be loaded or is neutral white/black/grey.
    /// </summary>
    public static Color? ExtractRepresentativeColor(Package package, string vtexInternalPath)
    {
        if (package == null || string.IsNullOrWhiteSpace(vtexInternalPath)) return null;

        if (vtexInternalPath.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
        {
            vtexInternalPath = vtexInternalPath.Substring(9);
        }
        vtexInternalPath = vtexInternalPath.Trim('"', '\'', ' ');

        var entry = FindVtexEntry(package, vtexInternalPath);
        if (entry == null) return null;

        try
        {
            package.ReadEntry(entry, out byte[] data);
            using var resource = new ValveResourceFormat.Resource();
            using var ms = new MemoryStream(data);
            resource.Read(ms);

            if (resource.ResourceType != ResourceType.Texture) return null;

            var texture = (ValveResourceFormat.ResourceTypes.Texture)resource.DataBlock;
            uint maxMip = texture.NumMipLevels > 0 ? (uint)(texture.NumMipLevels - 1) : 0;

            using var skBitmap = texture.GenerateBitmap(depth: 0, mipLevel: maxMip);
            if (skBitmap == null) return null;

            var pixel = skBitmap.GetPixel(0, 0);
            float r = pixel.Red / 255f;
            float g = pixel.Green / 255f;
            float b = pixel.Blue / 255f;

            if (skBitmap.ColorType == SKColorType.Bgra8888)
            {
                (r, b) = (b, r);
            }

            // Filter out pure black, white, or neutral grey
            if (Source2ColorMatrix.IsNeutralWhiteOrBlack(new System.Numerics.Vector4(r, g, b, 1.0f)))
            {
                return null;
            }

            // Also ensure not neutral grey (where R, G, B are virtually identical)
            float maxDiff = Math.Max(Math.Abs(r - g), Math.Max(Math.Abs(r - b), Math.Abs(g - b)));
            if (maxDiff < 0.05f)
            {
                return null;
            }

            return new Color(r, g, b, 1.0f);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[TextureLoader] Error extracting representative color ({vtexInternalPath}): {ex.Message}");
            return null;
        }
    }

    private static PackageEntry FindVtexEntry(Package package, string vtexPath)
    {
        var entry = package.FindEntry(vtexPath);
        if (entry != null) return entry;

        if (!vtexPath.EndsWith("_c"))
        {
            entry = package.FindEntry(vtexPath + "_c");
            if (entry != null) return entry;
        }

        if (!vtexPath.StartsWith("materials/", StringComparison.OrdinalIgnoreCase))
        {
            string matPrefixed = "materials/" + vtexPath;
            entry = package.FindEntry(matPrefixed) ?? package.FindEntry(matPrefixed + "_c");
            if (entry != null) return entry;
        }
        else
        {
            string stripped = vtexPath.Substring(10);
            entry = package.FindEntry(stripped) ?? package.FindEntry(stripped + "_c");
            if (entry != null) return entry;
        }

        // Secondary fallback: search by filename across all vtex_c entries in VPK
        string fileNameOnly = Path.GetFileNameWithoutExtension(vtexPath);
        if (fileNameOnly.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase))
        {
            fileNameOnly = Path.GetFileNameWithoutExtension(fileNameOnly);
        }

        if (package.Entries.TryGetValue("vtex_c", out var texEntries))
        {
            entry = texEntries.Find(e => e.FileName.Equals(fileNameOnly, StringComparison.OrdinalIgnoreCase))
                 ?? texEntries.Find(e => e.FileName.Contains(fileNameOnly, StringComparison.OrdinalIgnoreCase));
        }

        return entry;
    }
}
