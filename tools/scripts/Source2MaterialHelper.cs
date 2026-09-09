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

        // Secondary search path: fallback to filename match across all vtex_c entries in VPK
        if (entry == null)
        {
            string fileNameOnly = Path.GetFileNameWithoutExtension(vtexInternalPath);
            if (package.Entries.TryGetValue("vtex_c", out var texEntries))
            {
                entry = texEntries.Find(e => e.FileName.Equals(fileNameOnly, StringComparison.OrdinalIgnoreCase));
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
            string fileNameOnly = Path.GetFileNameWithoutExtension(vmatPath);
            if (package.Entries.TryGetValue("vmat_c", out var matEntries))
            {
                entry = matEntries.Find(e => e.FileName.Equals(fileNameOnly, StringComparison.OrdinalIgnoreCase));
            }
        }

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
        bool isGlass = (matResource.IntParams.TryGetValue("F_GLASS", out long glassVal) && glassVal == 1) ||
                       vmatLower.Contains("glass") || vmatLower.Contains("lens") ||
                       (matResource.IntParams.TryGetValue("F_WRITE_DEPTH_BEFORE_ALPHA_BLENDING", out long depthBlend) && depthBlend == 1 &&
                        (vmatLower.Contains("trans") || vmatLower.Contains("headglass")));
        bool isTranslucent = (matResource.IntParams.TryGetValue("F_TRANSLUCENT", out long transVal) && transVal == 1) || isGlass;
        bool isAlphaTest = matResource.IntParams.TryGetValue("F_ALPHA_TEST", out long alphaTestVal) && alphaTestVal == 1;

        if (isAdditive)
        {
            godotMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            godotMat.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
            godotMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            godotMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        }
        else if (isAlphaTest)
        {
            godotMat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            godotMat.AlphaScissorThreshold = 0.5f;
            godotMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        }
        else if (isTranslucent)
        {
            godotMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            godotMat.BlendMode = BaseMaterial3D.BlendModeEnum.Mix;
            // CRITICAL: OpaqueOnly prevents transparent glass shells from writing depth and occluding internal geometry (e.g. Paradox head hourglass).
            godotMat.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.OpaqueOnly;
            godotMat.RenderPriority = 1; // Render after internal opaque and emissive meshes (priority 0)
            godotMat.CullMode = isGlass ? BaseMaterial3D.CullModeEnum.Back : BaseMaterial3D.CullModeEnum.Disabled;
            godotMat.Roughness = isGlass ? 0.15f : 0.2f;
            godotMat.Metallic = 0.05f;
            godotMat.ClearcoatEnabled = true;
            godotMat.Clearcoat = 1.0f;
            godotMat.ClearcoatRoughness = 0.05f;
        }

        // 2. Textura de Color / Albedo
        string colorPath = getTexture(matResource, "g_tColor") ??
                           getTexture(matResource, "TextureColor") ??
                           getTexture(matResource, "g_tColor1") ??
                           getTexture(matResource, "g_tColorA") ??
                           getTexture(matResource, "g_tColor0") ??
                           getTexture(matResource, "g_tColor2") ??
                           getTexture(matResource, "g_tColorB") ??
                           getTexture(matResource, "TextureColor0") ??
                           getTexture(matResource, "TextureColor1");

        // CRITICAL FIX: In Source 2 BC7 color textures, alpha is 0 across all pixels unless explicitly an alpha-cutout texture.
        // For glass and translucent meshes, the diffuse texture color must be extracted with forceOpaque: true so Skia doesn't
        // wipe the alpha to 0, while transparency is controlled via godotMat.AlbedoColor.A (e.g. 0.35f-0.40f).
        // Only alpha-tested textures (hair cards, foliage) preserve the raw texture alpha.
        bool forceOpaqueColor = !isAdditive && !isAlphaTest;
        if (!string.IsNullOrEmpty(colorPath))
        {
            godotMat.AlbedoTexture = getOrLoadTexture(package, colorPath, forceOpaqueColor);
        }

        // Check vector tint parameters (multiplies texture or provides base albedo if no texture)
        // Prioritize non-default tints: in VPK, TextureColor1 often contains the specific tint when g_vColorTint1 is default white <1,1,1,0>
        System.Numerics.Vector4? tintVec = null;
        if (matResource.VectorParams.TryGetValue("g_vColorTint", out var vt1) && (vt1.X < 0.999f || vt1.Y < 0.999f || vt1.Z < 0.999f)) tintVec = vt1;
        else if (matResource.VectorParams.TryGetValue("g_vColorTint1", out var vt2) && (vt2.X < 0.999f || vt2.Y < 0.999f || vt2.Z < 0.999f)) tintVec = vt2;
        else if (matResource.VectorParams.TryGetValue("TextureColor1", out var vt4) && (vt4.X < 0.999f || vt4.Y < 0.999f || vt4.Z < 0.999f)) tintVec = vt4;
        else if (matResource.VectorParams.TryGetValue("TextureColor", out var vt5) && (vt5.X < 0.999f || vt5.Y < 0.999f || vt5.Z < 0.999f)) tintVec = vt5;
        else if (matResource.VectorParams.TryGetValue("g_vColorTint0", out var vt3)) tintVec = vt3;
        else if (matResource.VectorParams.TryGetValue("m_vColorTint", out var vt6)) tintVec = vt6;
        else if (matResource.VectorParams.TryGetValue("g_vTintColor", out var vt7)) tintVec = vt7;
        else if (matResource.VectorParams.TryGetValue("Color", out var vt8)) tintVec = vt8;
        else if (matResource.VectorParams.TryGetValue("g_vColorTint", out var vtDef1)) tintVec = vtDef1;
        else if (matResource.VectorParams.TryGetValue("g_vColorTint1", out var vtDef2)) tintVec = vtDef2;
        else if (matResource.VectorParams.TryGetValue("TextureColor1", out var vtDef4)) tintVec = vtDef4;

        if (tintVec.HasValue)
        {
            var tv = tintVec.Value;
            if (tv.X > 0.001f || tv.Y > 0.001f || tv.Z > 0.001f)
            {
                // In Source 2, dark/tinted glass (such as Paradox's head visor with TextureColor1 <0.043, 0.043, 0.043>)
                // is obscure smoked glass (~0.75f opacity) rather than high-transparency glass (~0.35f-0.40f).
                float defaultGlassAlpha = (tv.X < 0.15f && tv.Y < 0.15f && tv.Z < 0.15f) ? 0.75f : 0.40f;
                float alpha = tv.W > 0.001f ? tv.W : (isGlass ? defaultGlassAlpha : 1.0f);
                godotMat.AlbedoColor = new Color(tv.X, tv.Y, tv.Z, alpha);
            }
        }
        else if (isGlass)
        {
            godotMat.AlbedoColor = new Color(0.08f, 0.08f, 0.12f, 0.75f);
        }

        // For additive blending sprites / meshes, apply g_vSelfIllumTint1 directly to AlbedoColor so the glow renders with its intended color
        if (isAdditive && matResource.VectorParams.TryGetValue("g_vSelfIllumTint1", out var addTint))
        {
            godotMat.AlbedoColor = new Color(addTint.X, addTint.Y, addTint.Z, 1.0f);
        }

        // Vertex color support: F_VERTEX_COLOR=1, F_PAINT_VERTEX_COLORS=1, or explicit vertcolor materials
        bool hasVertexColor = (matResource.IntParams.TryGetValue("F_VERTEX_COLOR", out long vcVal) && vcVal == 1) ||
                              (matResource.IntParams.TryGetValue("F_PAINT_VERTEX_COLORS", out long pvcVal) && pvcVal == 1);
        if (hasVertexColor || vmatLower.Contains("vertcolor") || vmatLower.Contains("_vc") || string.IsNullOrEmpty(colorPath))
        {
            godotMat.VertexColorUseAsAlbedo = true;
        }

        // UV Tiling and Offset (e.g. Lady Geist shawl fur scale <4.0, 0.5>)
        if (matResource.VectorParams.TryGetValue("g_vAlbedoTexcoordScale1", out var uvScale))
        {
            godotMat.Uv1Scale = new Vector3(uvScale.X, uvScale.Y, 1.0f);
        }
        if (matResource.VectorParams.TryGetValue("g_vAlbedoTexcoordOffset1", out var uvOffset))
        {
            godotMat.Uv1Offset = new Vector3(uvOffset.X, uvOffset.Y, 0.0f);
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

        // 5. Emisión / Auto-iluminación PBR data-driven desde el VPK
        // En Source 2, materiales de ropa o cuerpo con F_USE_STATUS_EFFECTS_PROXY=1 contienen parámetros de auto-iluminación
        // utilizados únicamente en tiempo de ejecución para efectos de juego (invulnerabilidad, canalización de habilidades).
        // En estado neutral/estudio, la ropa y el cuerpo NO emiten luz (para no convertirse en bombillas blancas).
        // La emisión estática se reserva para submallas dedicadas de VFX/brillo (hourglass, keyglow, armglow, cards, etc.) o aditivas.
        bool isStatusProxy = matResource.IntParams.TryGetValue("F_USE_STATUS_EFFECTS_PROXY", out long statusProxy) && statusProxy == 1;
        bool isBodyOrClothes = vmatLower.Contains("body") || vmatLower.Contains("clothes") ||
                               vmatLower.Contains("jacket") || vmatLower.Contains("coat") ||
                               vmatLower.Contains("pants") || vmatLower.Contains("headbase") ||
                               vmatLower.Contains("skin") || vmatLower.Contains("chrono_v2.vmat");
        bool isDedicatedGlow = vmatLower.Contains("glow") || vmatLower.Contains("hourglass") ||
                               vmatLower.Contains("portal") ||
                               vmatLower.Contains("flame") || vmatLower.Contains("light") ||
                               vmatLower.Contains("beam") || isAdditive;

        bool hasSelfIllumFlag = (matResource.IntParams.TryGetValue("F_SELF_ILLUM", out long selfIllum) && selfIllum == 1) ||
                                (matResource.IntParams.TryGetValue("F_EMISSIVE", out long emissive) && emissive == 1);
        string selfIllumMaskPath = getTexture(matResource, "g_tSelfIllumMask") ?? getTexture(matResource, "TextureSelfIllumMask");
        bool hasValidIllumMask = !string.IsNullOrEmpty(selfIllumMaskPath) && !selfIllumMaskPath.ToLowerInvariant().Contains("default_mask");

        bool maskIsBlack = matResource.VectorParams.TryGetValue("TextureSelfIllumMask1", out var tcMask) &&
                           tcMask.X <= 0.001f && tcMask.Y <= 0.001f && tcMask.Z <= 0.001f;

        float illumScale = 0.0f;
        if (matResource.FloatParams.TryGetValue("g_flSelfIllumScale1", out float flScale1)) illumScale = flScale1;
        else if (matResource.FloatParams.TryGetValue("g_flSelfIllumScale", out float flScale0)) illumScale = flScale0;

        if (hasSelfIllumFlag && illumScale > 0.001f && !maskIsBlack && (!isStatusProxy || isDedicatedGlow) && !isBodyOrClothes)
        {
            godotMat.EmissionEnabled = true;
            godotMat.EmissionEnergyMultiplier = illumScale;

            if (matResource.VectorParams.TryGetValue("g_vSelfIllumTint1", out var illumVec))
            {
                godotMat.Emission = new Color(illumVec.X, illumVec.Y, illumVec.Z);
            }
            else
            {
                godotMat.Emission = Colors.White;
            }

            if (hasValidIllumMask)
            {
                var maskTex = getOrLoadTexture(package, selfIllumMaskPath, forceOpaque: true);
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

        if (isDedicatedGlow)
        {
            godotMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        }

        float roughness = isWeapon ? 0.38f : (isGlass ? 0.15f : 0.65f);
        if (matResource.VectorParams.TryGetValue("TextureRoughness1", out var rVec)) roughness = rVec.X;
        else if (matResource.VectorParams.TryGetValue("TextureRoughness", out var rVec0)) roughness = rVec0.X;
        else if (matResource.FloatParams.TryGetValue("g_flRoughnessScale1", out float rScale)) roughness = rScale;
        godotMat.Roughness = roughness;

        float metallic = isWeapon ? 0.85f : (isGlass ? 0.10f : (isDedicatedGlow && !isAdditive ? 0.80f : 0.0f));
        if (matResource.VectorParams.TryGetValue("TextureMetalness1", out var mVec)) metallic = mVec.X;
        else if (matResource.VectorParams.TryGetValue("TextureMetalness", out var mVec0)) metallic = mVec0.X;
        else if (matResource.FloatParams.TryGetValue("g_flMetalnessScale1", out float mScale)) metallic = mScale;
        godotMat.Metallic = metallic;

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