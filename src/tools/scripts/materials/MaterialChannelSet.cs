using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Godot;
using ValvePak;
using ValveResourceFormat.ResourceTypes;
using VrfMaterial = ValveResourceFormat.ResourceTypes.Material;

namespace DeadlockPlayground.Materials;

/// <summary>
/// Canonical material texture channels supported across Deadlock VPK models,
/// Community Mod VPKs, Custom Imported Models (.glb/.fbx), 2D UV Canvas, and 3D Shaders.
/// </summary>
public enum MaterialTextureChannel
{
    Color = 0,
    AmbientOcclusion = 1,
    Normal = 2,
    Roughness = 3,
    Shadow = 4,
    SelfIllum = 5,
    TintMask = 6,
    Metalness = 7,
    RimMask = 8,
    OutlineMask = 9
}

/// <summary>
/// Single channel entry inside a <see cref="MaterialChannelSet"/>.
/// Tracks the live texture, original/fallback state, shader enable toggle, and variants.
/// </summary>
public sealed class ChannelMapEntry
{
    public MaterialTextureChannel Channel { get; init; }
    public string DisplayLabel { get; init; } = "";
    public string ShortBadge { get; init; } = "";

    public ImageTexture Texture { get; set; }
    public ImageTexture OriginalTexture { get; set; }
    public Image SourceImage { get; set; }

    /// <summary>Original VPK path (.vtex_c), glTF image name, or disk import path.</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>True when backed by an actual discovered/imported asset; false if procedural default.</summary>
    public bool IsPresent { get; set; }

    /// <summary>True when using a clean generated procedural default (e.g., white AO, flat Normal).</summary>
    public bool IsDefaultFallback { get; set; }

    /// <summary>Whether this channel contributes to the live 3D shader output.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>True when user painted or imported an external image into this channel.</summary>
    public bool IsDirty { get; set; }

    /// <summary>True when Normal/Roughness was decoded from a Source 2 g_tNormalRoughness pack.</summary>
    public bool IsPackedNormalRoughness { get; set; }

    /// <summary>Original .vtex_c path of the packed g_tNormalRoughness texture (for VPK re-packing).</summary>
    public string PackedSourceVtexPath { get; set; } = "";

    public bool HasBlueToAlphaRoughness { get; set; }

    /// <summary>Multiple texture variants discovered in VPK (e.g., alternate Color skins).</summary>
    public List<string> VariantPaths { get; } = new();
    public List<string> VariantLabels { get; } = new();
    public int ActiveVariantIndex { get; set; } = 0;

    public bool IsColorSpaceSrgb =>
        Channel == MaterialTextureChannel.Color ||
        Channel == MaterialTextureChannel.Shadow ||
        Channel == MaterialTextureChannel.SelfIllum;

    public bool HasValidTexture => Texture != null && GodotObject.IsInstanceValid(Texture);
}

/// <summary>
/// Complete multi-channel texture map descriptor attached to a hero/custom submesh or MaterialGroup.
/// </summary>
public sealed class MaterialChannelSet
{
    public string MaterialKey { get; set; } = "";
    public string OriginalVmatPath { get; set; } = "";
    public string MaterialStem { get; set; } = "";

    public Dictionary<MaterialTextureChannel, ChannelMapEntry> Channels { get; } = new();

    public MaterialTextureChannel ActiveChannel { get; set; } = MaterialTextureChannel.Color;
    public MaterialTextureChannel? SoloPreviewChannel { get; set; } = null;

    public ChannelMapEntry GetChannel(MaterialTextureChannel ch)
        => Channels.TryGetValue(ch, out var entry) ? entry : null;

    public bool HasRealMap(MaterialTextureChannel ch)
        => Channels.TryGetValue(ch, out var entry) && entry.IsPresent && !entry.IsDefaultFallback && entry.HasValidTexture;

    public void SetChannel(MaterialTextureChannel ch, ChannelMapEntry entry)
    {
        if (entry != null) Channels[ch] = entry;
    }

    public static string GetDisplayLabel(MaterialTextureChannel ch) => ch switch
    {
        MaterialTextureChannel.Color => "COLOR",
        MaterialTextureChannel.AmbientOcclusion => "AO",
        MaterialTextureChannel.Normal => "NORMAL",
        MaterialTextureChannel.Roughness => "ROUGHNESS",
        MaterialTextureChannel.Shadow => "SHADOW / NPR",
        MaterialTextureChannel.SelfIllum => "SELF-ILLUM",
        MaterialTextureChannel.TintMask => "TINT MASK",
        MaterialTextureChannel.Metalness => "METALNESS",
        MaterialTextureChannel.RimMask => "RIM MASK",
        MaterialTextureChannel.OutlineMask => "OUTLINE MASK",
        _ => ch.ToString().ToUpperInvariant()
    };

    public static string GetShortBadge(MaterialTextureChannel ch) => ch switch
    {
        MaterialTextureChannel.Color => "C",
        MaterialTextureChannel.AmbientOcclusion => "AO",
        MaterialTextureChannel.Normal => "N",
        MaterialTextureChannel.Roughness => "R",
        MaterialTextureChannel.Shadow => "SH",
        MaterialTextureChannel.SelfIllum => "EM",
        MaterialTextureChannel.TintMask => "TM",
        MaterialTextureChannel.Metalness => "M",
        MaterialTextureChannel.RimMask => "RM",
        MaterialTextureChannel.OutlineMask => "OL",
        _ => "?"
    };

    public static int ChannelToShaderDebugIndex(MaterialTextureChannel? ch) => ch switch
    {
        null => 0,
        MaterialTextureChannel.Color => 1,
        MaterialTextureChannel.AmbientOcclusion => 2,
        MaterialTextureChannel.Normal => 3,
        MaterialTextureChannel.Roughness => 4,
        MaterialTextureChannel.Shadow => 5,
        MaterialTextureChannel.SelfIllum => 6,
        MaterialTextureChannel.Metalness => 7,
        MaterialTextureChannel.TintMask => 8,
        _ => 0
    };

    /// <summary>
    /// Ensures all 5 core PBR/NPR channels exist (creating clean procedural fallbacks for any absent slots).
    /// </summary>
    public void EnsureCoreFallbacks(int fallbackWidth = 256, int fallbackHeight = 256)
    {
        MaterialTextureChannel[] core =
        {
            MaterialTextureChannel.Color,
            MaterialTextureChannel.AmbientOcclusion,
            MaterialTextureChannel.Normal,
            MaterialTextureChannel.Roughness,
            MaterialTextureChannel.Shadow
        };

        foreach (var ch in core)
        {
            if (Channels.TryGetValue(ch, out var existing) && existing.HasValidTexture)
            {
                continue;
            }

            var fbTex = MaterialChannelDiscovery.CreateDefaultFallbackTexture(ch, fallbackWidth, fallbackHeight);
            var fbImg = fbTex.GetImage();
            Channels[ch] = new ChannelMapEntry
            {
                Channel = ch,
                DisplayLabel = GetDisplayLabel(ch),
                ShortBadge = GetShortBadge(ch),
                Texture = fbTex,
                OriginalTexture = fbTex,
                SourceImage = fbImg,
                SourcePath = "(Default)",
                IsPresent = false,
                IsDefaultFallback = true,
                IsEnabled = true,
                IsDirty = false
            };
        }
    }

    public List<(MaterialTextureChannel Channel, string Badge, bool IsReal)> GetSummaryBadges()
    {
        var order = new[]
        {
            MaterialTextureChannel.Color,
            MaterialTextureChannel.Normal,
            MaterialTextureChannel.Roughness,
            MaterialTextureChannel.AmbientOcclusion,
            MaterialTextureChannel.Shadow,
            MaterialTextureChannel.SelfIllum,
            MaterialTextureChannel.Metalness,
            MaterialTextureChannel.TintMask,
            MaterialTextureChannel.RimMask,
            MaterialTextureChannel.OutlineMask
        };

        var result = new List<(MaterialTextureChannel, string, bool)>();
        foreach (var ch in order)
        {
            if (!Channels.TryGetValue(ch, out var entry)) continue;
            bool isCore = ch is MaterialTextureChannel.Color
                or MaterialTextureChannel.Normal
                or MaterialTextureChannel.Roughness
                or MaterialTextureChannel.AmbientOcclusion
                or MaterialTextureChannel.Shadow;
            bool isReal = entry.IsPresent && !entry.IsDefaultFallback;
            if (isCore || isReal)
            {
                result.Add((ch, GetShortBadge(ch), isReal));
            }
        }
        return result;
    }

    public bool SwitchColorVariant(Package package, Package addonPackage, int variantIndex)
    {
        if (!Channels.TryGetValue(MaterialTextureChannel.Color, out var colorEntry)) return false;
        if (variantIndex < 0 || variantIndex >= colorEntry.VariantPaths.Count) return false;

        string targetPath = colorEntry.VariantPaths[variantIndex];
        if (string.IsNullOrEmpty(targetPath)) return false;

        var tex = Source2TextureLoader.GetOrLoadTexture(package, targetPath, forceOpaque: true, addonPackage: addonPackage);
        if (tex == null) return false;

        colorEntry.ActiveVariantIndex = variantIndex;
        colorEntry.Texture = tex;
        colorEntry.OriginalTexture = tex;
        colorEntry.SourceImage = tex.GetImage();
        colorEntry.SourcePath = targetPath;
        colorEntry.IsPresent = true;
        colorEntry.IsDefaultFallback = false;
        colorEntry.IsDirty = false;
        return true;
    }

    public bool ResetChannelToOriginal(MaterialTextureChannel ch)
    {
        if (!Channels.TryGetValue(ch, out var entry)) return false;
        if (entry.OriginalTexture == null || !GodotObject.IsInstanceValid(entry.OriginalTexture)) return false;
        entry.Texture = entry.OriginalTexture;
        entry.SourceImage = entry.OriginalTexture.GetImage();
        entry.IsDirty = false;
        return true;
    }
}

/// <summary>
/// Parsed glTF 2.0 material texture descriptors extracted from raw .glb binary JSON chunk.
/// Enables Tier 1 exact PBR slot + ORM detection and Tier 2 image filename matching.
/// </summary>
public sealed class GlbMaterialSlotInfo
{
    public int MaterialIndex { get; init; }
    public string MaterialName { get; init; } = "";
    public string BaseColorImageName { get; init; } = "";
    public string NormalImageName { get; init; } = "";
    public string OcclusionImageName { get; init; } = "";
    public string MetallicRoughnessImageName { get; init; } = "";
    public string EmissiveImageName { get; init; } = "";

    public int BaseColorImageIndex { get; init; } = -1;
    public int NormalImageIndex { get; init; } = -1;
    public int OcclusionImageIndex { get; init; } = -1;
    public int MetallicRoughnessImageIndex { get; init; } = -1;
    public int EmissiveImageIndex { get; init; } = -1;

    public bool OcclusionSharesMetallicRoughness =>
        OcclusionImageIndex >= 0 && OcclusionImageIndex == MetallicRoughnessImageIndex;
}

public sealed class GlbModelTextureManifest
{
    public List<string> ImageNames { get; } = new();
    public List<GlbMaterialSlotInfo> Materials { get; } = new();
}

/// <summary>
/// Universal texture map discovery, Source 2 Normal+Roughness splitting/packing,
/// 3-tier Custom Model PBR/NPR detection, and live shader synchronization.
/// </summary>
public static class MaterialChannelDiscovery
{
    private static readonly ConditionalWeakTable<Godot.Material, MaterialChannelSet> _materialRegistry = new();
    private static readonly ConditionalWeakTable<Node, GlbModelTextureManifest> _glbManifestRegistry = new();

    public static void RegisterChannelSet(Godot.Material mat, MaterialChannelSet set)
    {
        if (mat == null || set == null) return;
        _materialRegistry.AddOrUpdate(mat, set);
    }

    public static MaterialChannelSet TryGetChannelSet(Godot.Material mat)
    {
        if (mat == null) return null;
        return _materialRegistry.TryGetValue(mat, out var set) ? set : null;
    }

    public static void RegisterGlbManifest(Node sceneRoot, GlbModelTextureManifest manifest)
    {
        if (sceneRoot == null || manifest == null) return;
        _glbManifestRegistry.AddOrUpdate(sceneRoot, manifest);
    }

    public static GlbModelTextureManifest TryGetGlbManifest(Node sceneRoot)
    {
        if (sceneRoot == null) return null;
        return _glbManifestRegistry.TryGetValue(sceneRoot, out var manifest) ? manifest : null;
    }
    /// <summary>
    /// Parses the JSON chunk of a binary .glb file to read glTF 2.0 material texture bindings
    /// and embedded image names without modifying the imported Godot scene.
    /// </summary>
    public static GlbModelTextureManifest ParseGlbManifest(byte[] glbBytes)
    {
        var manifest = new GlbModelTextureManifest();
        if (glbBytes == null || glbBytes.Length < 20) return manifest;

        // Verify glTF binary header: magic "glTF" (0x46546C67)
        uint magic = BitConverter.ToUInt32(glbBytes, 0);
        if (magic != 0x46546C67u) return manifest;

        uint chunkLength = BitConverter.ToUInt32(glbBytes, 12);
        uint chunkType = BitConverter.ToUInt32(glbBytes, 16);
        if (chunkType != 0x4E4F534Au || 20 + chunkLength > glbBytes.Length) return manifest;

        try
        {
            string jsonText = Encoding.UTF8.GetString(glbBytes, 20, (int)chunkLength);
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            var imageNames = new List<string>();
            if (root.TryGetProperty("images", out var imagesEl) && imagesEl.ValueKind == JsonValueKind.Array)
            {
                int idx = 0;
                foreach (var imgEl in imagesEl.EnumerateArray())
                {
                    string name = imgEl.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(name) && imgEl.TryGetProperty("uri", out var uEl))
                    {
                        string uri = uEl.GetString() ?? "";
                        if (!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        {
                            name = Path.GetFileNameWithoutExtension(uri);
                        }
                    }
                    if (string.IsNullOrWhiteSpace(name)) name = $"image_{idx}";
                    imageNames.Add(name);
                    manifest.ImageNames.Add(name);
                    idx++;
                }
            }

            var textureToImage = new List<int>();
            if (root.TryGetProperty("textures", out var texEl) && texEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in texEl.EnumerateArray())
                {
                    int srcIdx = t.TryGetProperty("source", out var sEl) && sEl.TryGetInt32(out int v) ? v : -1;
                    if (srcIdx < 0 && t.TryGetProperty("extensions", out var extEl))
                    {
                        // Handle EXT_texture_webp or KHR_texture_basisu source fallback
                        if (extEl.TryGetProperty("EXT_texture_webp", out var webp) &&
                            webp.TryGetProperty("source", out var ws) && ws.TryGetInt32(out int wv))
                        {
                            srcIdx = wv;
                        }
                    }
                    textureToImage.Add(srcIdx);
                }
            }

            int ResolveTexImageIndex(JsonElement parent, string prop)
            {
                if (!parent.TryGetProperty(prop, out var texRef)) return -1;
                if (!texRef.TryGetProperty("index", out var idxEl) || !idxEl.TryGetInt32(out int texIdx)) return -1;
                return (texIdx >= 0 && texIdx < textureToImage.Count) ? textureToImage[texIdx] : -1;
            }

            string ResolveImageName(int imgIdx)
                => (imgIdx >= 0 && imgIdx < imageNames.Count) ? imageNames[imgIdx] : "";

            if (root.TryGetProperty("materials", out var matsEl) && matsEl.ValueKind == JsonValueKind.Array)
            {
                int mIdx = 0;
                foreach (var matEl in matsEl.EnumerateArray())
                {
                    string mName = matEl.TryGetProperty("name", out var mn) ? mn.GetString() ?? "" : "";
                    int baseIdx = -1;
                    int mrIdx = -1;

                    if (matEl.TryGetProperty("pbrMetallicRoughness", out var pbrEl))
                    {
                        baseIdx = ResolveTexImageIndex(pbrEl, "baseColorTexture");
                        mrIdx = ResolveTexImageIndex(pbrEl, "metallicRoughnessTexture");
                    }

                    int normIdx = ResolveTexImageIndex(matEl, "normalTexture");
                    int occIdx = ResolveTexImageIndex(matEl, "occlusionTexture");
                    int emissIdx = ResolveTexImageIndex(matEl, "emissiveTexture");

                    manifest.Materials.Add(new GlbMaterialSlotInfo
                    {
                        MaterialIndex = mIdx,
                        MaterialName = mName,
                        BaseColorImageIndex = baseIdx,
                        NormalImageIndex = normIdx,
                        OcclusionImageIndex = occIdx,
                        MetallicRoughnessImageIndex = mrIdx,
                        EmissiveImageIndex = emissIdx,
                        BaseColorImageName = ResolveImageName(baseIdx),
                        NormalImageName = ResolveImageName(normIdx),
                        OcclusionImageName = ResolveImageName(occIdx),
                        MetallicRoughnessImageName = ResolveImageName(mrIdx),
                        EmissiveImageName = ResolveImageName(emissIdx)
                    });
                    mIdx++;
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MaterialChannelDiscovery] Failed to parse GLB JSON manifest: {ex.Message}");
        }

        return manifest;
    }

    public static bool IsPlaceholderOrDevTexture(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        string norm = path.Replace("\\", "/").ToLowerInvariant();
        if (norm.Contains("materials/default/") || norm.Contains("materials/dev/") ||
            norm.Contains("_default_color") || norm.Contains("dev_"))
        {
            return true;
        }

        string file = Path.GetFileNameWithoutExtension(norm);
        if (file.StartsWith("default_") || file == "black" || file == "white" || file == "grey")
        {
            return true;
        }
        return false;
    }

    public static string ExtractMaterialStem(string vmatPathOrKey)
    {
        if (string.IsNullOrWhiteSpace(vmatPathOrKey)) return "";
        string file = Path.GetFileNameWithoutExtension(vmatPathOrKey.Replace("\\", "/"));
        if (file.EndsWith(".vmat", StringComparison.OrdinalIgnoreCase))
        {
            file = Path.GetFileNameWithoutExtension(file);
        }
        // Trim shader/variant suffixes if any
        return file.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Decodes a Source 2 g_tNormalRoughness ImageTexture into separate tangent-space RGB Normal
    /// and grayscale Roughness ImageTextures.
    /// Supports both VRF BlueToAlpha mode (Normal XYZ in RGB, Roughness in A) and raw
    /// Hemi-Octahedron packed textures (Oct Normal in RG, Roughness in B).
    /// </summary>
    public static void SplitNormalRoughnessTexture(
        ImageTexture packedTex,
        string sourcePath,
        out ImageTexture normalTex,
        out ImageTexture roughnessTex)
    {
        normalTex = null;
        roughnessTex = null;
        if (packedTex == null || !GodotObject.IsInstanceValid(packedTex)) return;

        Image src = packedTex.GetImage();
        if (src == null || src.IsEmpty()) return;

        if (src.IsCompressed()) src.Decompress();
        if (src.GetFormat() != Image.Format.Rgba8) src.Convert(Image.Format.Rgba8);

        int w = src.GetWidth();
        int h = src.GetHeight();
        byte[] raw = src.GetData();
        if (raw == null || raw.Length < w * h * 4) return;

        bool hasBlueToAlpha = packedTex.HasMeta("HasBlueToAlphaRoughness") &&
                              packedTex.GetMeta("HasBlueToAlphaRoughness").AsBool();

        // Heuristic fallback if meta was stripped: inspect Blue vs Alpha variance
        if (!hasBlueToAlpha)
        {
            int sampleStep = Math.Max(1, (w * h) / 512);
            bool alphaVaries = false;
            bool blueLow = false;
            byte firstA = raw[3];
            for (int p = 0; p < w * h; p += sampleStep)
            {
                int i = p * 4;
                if (Math.Abs(raw[i + 3] - firstA) > 4) alphaVaries = true;
                if (raw[i + 2] < 160) blueLow = true;
            }
            if (alphaVaries && !blueLow)
            {
                hasBlueToAlpha = true;
            }
        }

        byte[] normBytes = new byte[w * h * 4];
        byte[] roughBytes = new byte[w * h * 4];

        for (int p = 0; p < w * h; p++)
        {
            int i = p * 4;
            byte r = raw[i];
            byte g = raw[i + 1];
            byte b = raw[i + 2];
            byte a = raw[i + 3];

            if (hasBlueToAlpha)
            {
                // Normal RGB already decoded/reconstructed by Source2TextureLoader; Roughness moved to A.
                byte nz = b;
                if (nz == 0)
                {
                    float nx = (r / 255f) * 2f - 1f;
                    float ny = (g / 255f) * 2f - 1f;
                    float nzSq = Math.Max(0f, 1f - nx * nx - ny * ny);
                    nz = (byte)Math.Clamp((MathF.Sqrt(nzSq) * 0.5f + 0.5f) * 255f, 0f, 255f);
                }
                normBytes[i] = r;
                normBytes[i + 1] = g;
                normBytes[i + 2] = nz;
                normBytes[i + 3] = 255;

                roughBytes[i] = a;
                roughBytes[i + 1] = a;
                roughBytes[i + 2] = a;
                roughBytes[i + 3] = 255;
            }
            else
            {
                // Hemi-Octahedron normal in RG, Roughness in B
                float ex = (r / 255f) * 2f - 1f;
                float ey = (g / 255f) * 2f - 1f;
                float nx = ex + ey;
                float ny = ex - ey;
                float nz = 1f - Math.Abs(ex) - Math.Abs(ey);
                float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 1e-5f)
                {
                    nx /= len;
                    ny /= len;
                    nz /= len;
                }
                else
                {
                    nx = 0f;
                    ny = 0f;
                    nz = 1f;
                }

                normBytes[i] = (byte)Math.Clamp((nx * 0.5f + 0.5f) * 255f, 0f, 255f);
                normBytes[i + 1] = (byte)Math.Clamp((ny * 0.5f + 0.5f) * 255f, 0f, 255f);
                normBytes[i + 2] = (byte)Math.Clamp((nz * 0.5f + 0.5f) * 255f, 0f, 255f);
                normBytes[i + 3] = 255;

                roughBytes[i] = b;
                roughBytes[i + 1] = b;
                roughBytes[i + 2] = b;
                roughBytes[i + 3] = 255;
            }
        }

        Image normImg = Image.CreateFromData(w, h, false, Image.Format.Rgba8, normBytes);
        normalTex = ImageTexture.CreateFromImage(normImg);
        normalTex.ResourceName = (sourcePath ?? "normal") + "#normal";

        Image roughImg = Image.CreateFromData(w, h, false, Image.Format.Rgba8, roughBytes);
        roughnessTex = ImageTexture.CreateFromImage(roughImg);
        roughnessTex.ResourceName = (sourcePath ?? "roughness") + "#roughness";
    }

    /// <summary>
    /// Packs a tangent-space RGB Normal image and a grayscale Roughness image back into
    /// Source 2 NormalRoughness RGBA layout (Normal XY(Z) in RGB, Roughness in Alpha/Blue).
    /// </summary>
    public static Image PackNormalRoughnessImage(Image normalImg, Image roughnessImg, bool packAsBlueToAlpha = true)
    {
        if (normalImg == null && roughnessImg == null) return null;

        int w = normalImg?.GetWidth() ?? roughnessImg.GetWidth();
        int h = normalImg?.GetHeight() ?? roughnessImg.GetHeight();

        Image nWork = normalImg != null ? (Image)normalImg.Duplicate() : CreateFlatNormalImage(w, h);
        Image rWork = roughnessImg != null ? (Image)roughnessImg.Duplicate() : CreateSolidGrayImage(w, h, 180);

        if (nWork.IsCompressed()) nWork.Decompress();
        if (rWork.IsCompressed()) rWork.Decompress();
        if (nWork.GetFormat() != Image.Format.Rgba8) nWork.Convert(Image.Format.Rgba8);
        if (rWork.GetFormat() != Image.Format.Rgba8) rWork.Convert(Image.Format.Rgba8);
        if (nWork.GetWidth() != w || nWork.GetHeight() != h) nWork.Resize(w, h, Image.Interpolation.Bilinear);
        if (rWork.GetWidth() != w || rWork.GetHeight() != h) rWork.Resize(w, h, Image.Interpolation.Bilinear);

        byte[] nBytes = nWork.GetData();
        byte[] rBytes = rWork.GetData();
        byte[] outBytes = new byte[w * h * 4];

        for (int p = 0; p < w * h; p++)
        {
            int i = p * 4;
            byte rough = rBytes[i]; // R channel of grayscale roughness
            if (packAsBlueToAlpha)
            {
                outBytes[i] = nBytes[i];
                outBytes[i + 1] = nBytes[i + 1];
                outBytes[i + 2] = nBytes[i + 2];
                outBytes[i + 3] = rough;
            }
            else
            {
                // Hemi-octahedron RG + Roughness in B
                float nx = (nBytes[i] / 255f) * 2f - 1f;
                float ny = (nBytes[i + 1] / 255f) * 2f - 1f;
                float nz = Math.Max(0.0001f, (nBytes[i + 2] / 255f) * 2f - 1f);
                float invL1 = 1f / (Math.Abs(nx) + Math.Abs(ny) + nz);
                float ox = nx * invL1;
                float oy = ny * invL1;
                float hx = (ox + oy) * 0.5f;
                float hy = (ox - oy) * 0.5f;
                outBytes[i] = (byte)Math.Clamp((hx * 0.5f + 0.5f) * 255f, 0f, 255f);
                outBytes[i + 1] = (byte)Math.Clamp((hy * 0.5f + 0.5f) * 255f, 0f, 255f);
                outBytes[i + 2] = rough;
                outBytes[i + 3] = 255;
            }
        }

        return Image.CreateFromData(w, h, false, Image.Format.Rgba8, outBytes);
    }

    public static ImageTexture CreateDefaultFallbackTexture(MaterialTextureChannel ch, int width = 256, int height = 256)
    {
        Image img = ch switch
        {
            MaterialTextureChannel.Normal => CreateFlatNormalImage(width, height),
            MaterialTextureChannel.Roughness => CreateSolidGrayImage(width, height, 180),
            MaterialTextureChannel.AmbientOcclusion => CreateSolidGrayImage(width, height, 255),
            MaterialTextureChannel.Shadow => CreateSolidGrayImage(width, height, 255),
            MaterialTextureChannel.Color => CreateSolidGrayImage(width, height, 230),
            MaterialTextureChannel.Metalness => CreateSolidGrayImage(width, height, 0),
            MaterialTextureChannel.SelfIllum => CreateSolidGrayImage(width, height, 0),
            _ => CreateSolidGrayImage(width, height, 255)
        };
        var tex = ImageTexture.CreateFromImage(img);
        tex.ResourceName = $"default_{ch.ToString().ToLowerInvariant()}";
        return tex;
    }

    private static Image CreateFlatNormalImage(int w, int h)
    {
        byte[] bytes = new byte[w * h * 4];
        for (int p = 0; p < w * h; p++)
        {
            int i = p * 4;
            bytes[i] = 128;
            bytes[i + 1] = 128;
            bytes[i + 2] = 255;
            bytes[i + 3] = 255;
        }
        return Image.CreateFromData(w, h, false, Image.Format.Rgba8, bytes);
    }

    private static Image CreateSolidGrayImage(int w, int h, byte val)
    {
        byte[] bytes = new byte[w * h * 4];
        for (int p = 0; p < w * h; p++)
        {
            int i = p * 4;
            bytes[i] = val;
            bytes[i + 1] = val;
            bytes[i + 2] = val;
            bytes[i + 3] = 255;
        }
        return Image.CreateFromData(w, h, false, Image.Format.Rgba8, bytes);
    }

    /// <summary>
    /// Extracts a single channel from an image (e.g. R=AO, G=Roughness, B=Metalness) as RGBA8 grayscale.
    /// Optionally inverts (e.g. Glossiness -> Roughness).
    /// </summary>
    public static ImageTexture ExtractSingleChannelTexture(
        Texture2D sourceTex,
        int channelIndex,
        bool invert = false,
        string resourceTag = "")
    {
        if (sourceTex == null || !GodotObject.IsInstanceValid(sourceTex)) return null;
        Image img = sourceTex.GetImage();
        if (img == null || img.IsEmpty()) return null;

        Image work = (Image)img.Duplicate();
        if (work.IsCompressed()) work.Decompress();
        if (work.GetFormat() != Image.Format.Rgba8) work.Convert(Image.Format.Rgba8);

        int w = work.GetWidth();
        int h = work.GetHeight();
        byte[] src = work.GetData();
        byte[] dst = new byte[w * h * 4];
        int chOffset = Math.Clamp(channelIndex, 0, 3);

        for (int p = 0; p < w * h; p++)
        {
            int i = p * 4;
            byte v = src[i + chOffset];
            if (invert) v = (byte)(255 - v);
            dst[i] = v;
            dst[i + 1] = v;
            dst[i + 2] = v;
            dst[i + 3] = 255;
        }

        Image outImg = Image.CreateFromData(w, h, false, Image.Format.Rgba8, dst);
        var outTex = ImageTexture.CreateFromImage(outImg);
        outTex.ResourceName = resourceTag;
        return outTex;
    }

    /// <summary>
    /// Checks whether a specific RGBA channel of a Texture2D has meaningful pixel variation
    /// (used to detect packed ORM/ARM Red-channel AO maps on custom .glb/.fbx imports).
    /// </summary>
    public static bool ChannelHasVariation(Texture2D tex, int channelIndex, int minDelta = 6)
    {
        if (tex == null || !GodotObject.IsInstanceValid(tex)) return false;
        Image img = tex.GetImage();
        if (img == null || img.IsEmpty()) return false;
        Image work = (Image)img.Duplicate();
        if (work.IsCompressed()) work.Decompress();
        if (work.GetFormat() != Image.Format.Rgba8) work.Convert(Image.Format.Rgba8);

        int w = work.GetWidth();
        int h = work.GetHeight();
        byte[] data = work.GetData();
        int step = Math.Max(1, (w * h) / 512);
        byte minV = 255, maxV = 0;
        int off = Math.Clamp(channelIndex, 0, 3);

        for (int p = 0; p < w * h; p += step)
        {
            byte v = data[p * 4 + off];
            if (v < minV) minV = v;
            if (v > maxV) maxV = v;
            if (maxV - minV >= minDelta) return true;
        }
        return false;
    }

    /// <summary>
    /// Discovers all material texture channels from a Source 2 material (.vmat_c) AND scans sibling
    /// .vtex_c entries in the same hero materials/ directory across base pak01 and loaded Mod VPKs.
    /// </summary>
    public static MaterialChannelSet DiscoverSource2Channels(
        Package package,
        VrfMaterial matResource,
        string vmatPath,
        Godot.Material builtMaterial = null,
        Package addonPackage = null)
    {
        var set = new MaterialChannelSet
        {
            MaterialKey = vmatPath ?? builtMaterial?.ResourceName ?? "material",
            OriginalVmatPath = vmatPath ?? "",
            MaterialStem = ExtractMaterialStem(vmatPath ?? builtMaterial?.ResourceName ?? "")
        };

        string colorPath = "";
        string aoPath = "";
        string normalRoughnessPath = "";
        string standaloneNormalPath = "";
        string standaloneRoughPath = "";
        string shadowPath = "";
        string selfIllumPath = "";
        string tintMaskPath = "";
        string metalnessPath = "";
        string rimMaskPath = "";
        string outlineMaskPath = "";

        var discoveredColorVariants = new List<string>();

        void ConsiderParam(string key, string val)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(val)) return;
            string k = key.ToLowerInvariant();
            string v = val.Replace("\\", "/").TrimStart('/');

            if (k is "g_tcolor" or "texturecolor" or "texturecolor1" or "g_tcolor0")
            {
                if (!IsPlaceholderOrDevTexture(v))
                {
                    if (string.IsNullOrEmpty(colorPath)) colorPath = v;
                    if (!discoveredColorVariants.Contains(v, StringComparer.OrdinalIgnoreCase))
                        discoveredColorVariants.Add(v);
                }
            }
            else if (k.StartsWith("g_tcolor", StringComparison.Ordinal) || k.StartsWith("texturecolor", StringComparison.Ordinal))
            {
                if (!IsPlaceholderOrDevTexture(v) && !discoveredColorVariants.Contains(v, StringComparer.OrdinalIgnoreCase))
                    discoveredColorVariants.Add(v);
            }
            else if (k is "g_tambientocclusion" or "g_tao" or "textureambientocclusion" or "textureambientocclusion1")
            {
                if (!IsPlaceholderOrDevTexture(v)) aoPath = v;
            }
            else if (k is "g_tnormalroughness" or "texturenormalroughness")
            {
                if (!IsPlaceholderOrDevTexture(v)) normalRoughnessPath = v;
            }
            else if (k is "g_tnormal" or "texturenormal" or "texturenormal1")
            {
                if (!IsPlaceholderOrDevTexture(v))
                {
                    if (v.Contains("normalroughness", StringComparison.OrdinalIgnoreCase))
                        normalRoughnessPath = v;
                    else
                        standaloneNormalPath = v;
                }
            }
            else if (k is "g_troughness" or "textureroughness" or "g_tanisogloss")
            {
                if (!IsPlaceholderOrDevTexture(v)) standaloneRoughPath = v;
            }
            else if (k.Contains("transmissivecolor") || k.Contains("tramsissivecolor") || k.Contains("shadowtint") || k.Contains("shadow"))
            {
                if (!IsPlaceholderOrDevTexture(v)) shadowPath = v;
            }
            else if (k.Contains("selfillum"))
            {
                if (!IsPlaceholderOrDevTexture(v)) selfIllumPath = v;
            }
            else if (k.Contains("tintmask"))
            {
                if (!IsPlaceholderOrDevTexture(v)) tintMaskPath = v;
            }
            else if (k.Contains("metal"))
            {
                if (!IsPlaceholderOrDevTexture(v)) metalnessPath = v;
            }
            else if (k.Contains("rimlightmask") || k.Contains("rimmask"))
            {
                if (!IsPlaceholderOrDevTexture(v)) rimMaskPath = v;
            }
            else if (k.Contains("outlinemask"))
            {
                if (!IsPlaceholderOrDevTexture(v)) outlineMaskPath = v;
            }
        }

        if (matResource != null)
        {
            foreach (var kv in matResource.TextureParams)
            {
                ConsiderParam(kv.Key, kv.Value);
            }
        }

        if (builtMaterial != null)
        {
            foreach (var metaName in builtMaterial.GetMetaList())
            {
                string mKey = metaName.ToString();
                if (mKey.StartsWith("TextureParam_", StringComparison.Ordinal))
                {
                    ConsiderParam(mKey.Substring("TextureParam_".Length), builtMaterial.GetMeta(metaName).AsString());
                }
                else if (mKey == "OriginalColorVtexCPath")
                {
                    ConsiderParam("g_tColor", builtMaterial.GetMeta(metaName).AsString());
                }
                else if (mKey == "OriginalRimMaskVtexCPath")
                {
                    ConsiderParam("g_tRimLightMask", builtMaterial.GetMeta(metaName).AsString());
                }
            }
        }

        // Scan sibling .vtex_c files in mounted VPKs sharing the hero materials directory & material stem
        if (!string.IsNullOrEmpty(vmatPath) && !string.IsNullOrEmpty(set.MaterialStem))
        {
            string dir = Path.GetDirectoryName(vmatPath.Replace("\\", "/"))?.Replace("\\", "/")?.Trim('/');
            if (!string.IsNullOrEmpty(dir))
            {
                var siblingFiles = ScanVpkDirectoryVtex(package, addonPackage, dir, set.MaterialStem);
                foreach (string sib in siblingFiles)
                {
                    string f = Path.GetFileName(sib).ToLowerInvariant();
                    if (f.Contains("_color") || f.Contains("g_tcolor"))
                    {
                        if (string.IsNullOrEmpty(colorPath)) colorPath = sib;
                        if (!discoveredColorVariants.Contains(sib, StringComparer.OrdinalIgnoreCase))
                            discoveredColorVariants.Add(sib);
                    }
                    else if (f.Contains("_ao_") || f.EndsWith("_ao.vtex_c") || f.Contains("ambientocclusion"))
                    {
                        if (string.IsNullOrEmpty(aoPath)) aoPath = sib;
                    }
                    else if (f.Contains("_shadow") || f.Contains("shadowtint") || f.Contains("transmissive"))
                    {
                        if (string.IsNullOrEmpty(shadowPath)) shadowPath = sib;
                    }
                    else if (f.Contains("normalroughness"))
                    {
                        if (string.IsNullOrEmpty(normalRoughnessPath)) normalRoughnessPath = sib;
                    }
                    else if (f.Contains("_normal") || f.EndsWith("_n.vtex_c"))
                    {
                        if (string.IsNullOrEmpty(standaloneNormalPath)) standaloneNormalPath = sib;
                    }
                    else if (f.Contains("_rough") || f.EndsWith("_r.vtex_c"))
                    {
                        if (string.IsNullOrEmpty(standaloneRoughPath)) standaloneRoughPath = sib;
                    }
                    else if (f.Contains("_selfillum"))
                    {
                        if (string.IsNullOrEmpty(selfIllumPath)) selfIllumPath = sib;
                    }
                    else if (f.Contains("_tint") || f.Contains("_mask"))
                    {
                        if (string.IsNullOrEmpty(tintMaskPath)) tintMaskPath = sib;
                    }
                    else if (f.Contains("_metal"))
                    {
                        if (string.IsNullOrEmpty(metalnessPath)) metalnessPath = sib;
                    }
                }
            }
        }

        // Load primary Color (plus any variants)
        ImageTexture loadedColor = null;
        if (builtMaterial is StandardMaterial3D std && std.AlbedoTexture is ImageTexture stdImgTex)
        {
            loadedColor = stdImgTex;
        }
        else if (builtMaterial is ShaderMaterial sm && sm.GetShaderParameter("albedo_texture").AsGodotObject() is ImageTexture smTex)
        {
            loadedColor = smTex;
        }
        if (loadedColor == null && !string.IsNullOrEmpty(colorPath))
        {
            loadedColor = Source2TextureLoader.GetOrLoadTexture(package, colorPath, forceOpaque: true, addonPackage: addonPackage);
        }

        if (loadedColor != null || discoveredColorVariants.Count > 0)
        {
            if (loadedColor == null && discoveredColorVariants.Count > 0)
            {
                colorPath = discoveredColorVariants[0];
                loadedColor = Source2TextureLoader.GetOrLoadTexture(package, colorPath, forceOpaque: true, addonPackage: addonPackage);
            }

            var cEntry = new ChannelMapEntry
            {
                Channel = MaterialTextureChannel.Color,
                DisplayLabel = MaterialChannelSet.GetDisplayLabel(MaterialTextureChannel.Color),
                ShortBadge = MaterialChannelSet.GetShortBadge(MaterialTextureChannel.Color),
                Texture = loadedColor,
                OriginalTexture = loadedColor,
                SourceImage = loadedColor?.GetImage(),
                SourcePath = colorPath,
                IsPresent = loadedColor != null,
                IsDefaultFallback = loadedColor == null
            };
            for (int i = 0; i < discoveredColorVariants.Count; i++)
            {
                string vp = discoveredColorVariants[i];
                cEntry.VariantPaths.Add(vp);
                string shortHash = Path.GetFileNameWithoutExtension(vp);
                int idx = shortHash.LastIndexOf('_');
                string suffix = idx > 0 ? shortHash.Substring(idx + 1) : (i + 1).ToString();
                cEntry.VariantLabels.Add($"Color {i + 1} ({suffix})");
            }
            if (!string.IsNullOrEmpty(colorPath))
            {
                int activeIdx = cEntry.VariantPaths.FindIndex(p => string.Equals(p, colorPath, StringComparison.OrdinalIgnoreCase));
                cEntry.ActiveVariantIndex = activeIdx >= 0 ? activeIdx : 0;
            }
            set.SetChannel(MaterialTextureChannel.Color, cEntry);
        }

        // Ambient Occlusion
        ImageTexture loadedAo = null;
        if (builtMaterial is StandardMaterial3D stdAo && stdAo.AOTexture is ImageTexture existingAo)
        {
            loadedAo = existingAo;
        }
        else if (!string.IsNullOrEmpty(aoPath))
        {
            loadedAo = Source2TextureLoader.GetOrLoadTexture(package, aoPath, forceOpaque: true, addonPackage: addonPackage);
        }
        if (loadedAo != null)
        {
            set.SetChannel(MaterialTextureChannel.AmbientOcclusion, new ChannelMapEntry
            {
                Channel = MaterialTextureChannel.AmbientOcclusion,
                DisplayLabel = MaterialChannelSet.GetDisplayLabel(MaterialTextureChannel.AmbientOcclusion),
                ShortBadge = MaterialChannelSet.GetShortBadge(MaterialTextureChannel.AmbientOcclusion),
                Texture = loadedAo,
                OriginalTexture = loadedAo,
                SourceImage = loadedAo.GetImage(),
                SourcePath = aoPath,
                IsPresent = true,
                IsDefaultFallback = false
            });
        }

        // Normal & Roughness (split g_tNormalRoughness if present, plus standalone fallback)
        if (!string.IsNullOrEmpty(normalRoughnessPath))
        {
            ImageTexture packedNr = null;
            if (builtMaterial is StandardMaterial3D stdNr && stdNr.NormalTexture is ImageTexture stdNorm &&
                stdNorm.HasMeta("HasBlueToAlphaRoughness"))
            {
                packedNr = stdNorm;
            }
            else if (builtMaterial is ShaderMaterial smNr &&
                     smNr.GetShaderParameter("g_tNormalRoughness").AsGodotObject() is ImageTexture shaderNr)
            {
                packedNr = shaderNr;
            }

            if (packedNr == null)
            {
                packedNr = Source2TextureLoader.GetOrLoadTexture(package, normalRoughnessPath, forceOpaque: false, addonPackage: addonPackage);
            }

            if (packedNr != null)
            {
                SplitNormalRoughnessTexture(packedNr, normalRoughnessPath, out var splitNorm, out var splitRough);
                bool hasBlueAlpha = packedNr.HasMeta("HasBlueToAlphaRoughness") && packedNr.GetMeta("HasBlueToAlphaRoughness").AsBool();
                if (splitNorm != null)
                {
                    set.SetChannel(MaterialTextureChannel.Normal, new ChannelMapEntry
                    {
                        Channel = MaterialTextureChannel.Normal,
                        DisplayLabel = MaterialChannelSet.GetDisplayLabel(MaterialTextureChannel.Normal),
                        ShortBadge = MaterialChannelSet.GetShortBadge(MaterialTextureChannel.Normal),
                        Texture = splitNorm,
                        OriginalTexture = splitNorm,
                        SourceImage = splitNorm.GetImage(),
                        SourcePath = normalRoughnessPath,
                        IsPresent = true,
                        IsDefaultFallback = false,
                        IsPackedNormalRoughness = true,
                        PackedSourceVtexPath = normalRoughnessPath,
                        HasBlueToAlphaRoughness = hasBlueAlpha
                    });
                }
                if (splitRough != null && string.IsNullOrEmpty(standaloneRoughPath))
                {
                    set.SetChannel(MaterialTextureChannel.Roughness, new ChannelMapEntry
                    {
                        Channel = MaterialTextureChannel.Roughness,
                        DisplayLabel = MaterialChannelSet.GetDisplayLabel(MaterialTextureChannel.Roughness),
                        ShortBadge = MaterialChannelSet.GetShortBadge(MaterialTextureChannel.Roughness),
                        Texture = splitRough,
                        OriginalTexture = splitRough,
                        SourceImage = splitRough.GetImage(),
                        SourcePath = normalRoughnessPath,
                        IsPresent = true,
                        IsDefaultFallback = false,
                        IsPackedNormalRoughness = true,
                        PackedSourceVtexPath = normalRoughnessPath,
                        HasBlueToAlphaRoughness = hasBlueAlpha
                    });
                }
            }
        }

        if (!set.HasRealMap(MaterialTextureChannel.Normal) && !string.IsNullOrEmpty(standaloneNormalPath))
        {
            var nTex = Source2TextureLoader.GetOrLoadTexture(package, standaloneNormalPath, forceOpaque: true, addonPackage: addonPackage);
            if (nTex != null)
            {
                set.SetChannel(MaterialTextureChannel.Normal, new ChannelMapEntry
                {
                    Channel = MaterialTextureChannel.Normal,
                    DisplayLabel = MaterialChannelSet.GetDisplayLabel(MaterialTextureChannel.Normal),
                    ShortBadge = MaterialChannelSet.GetShortBadge(MaterialTextureChannel.Normal),
                    Texture = nTex,
                    OriginalTexture = nTex,
                    SourceImage = nTex.GetImage(),
                    SourcePath = standaloneNormalPath,
                    IsPresent = true,
                    IsDefaultFallback = false
                });
            }
        }

        if (!set.HasRealMap(MaterialTextureChannel.Roughness) && !string.IsNullOrEmpty(standaloneRoughPath))
        {
            var rTex = Source2TextureLoader.GetOrLoadTexture(package, standaloneRoughPath, forceOpaque: true, addonPackage: addonPackage);
            if (rTex != null)
            {
                set.SetChannel(MaterialTextureChannel.Roughness, new ChannelMapEntry
                {
                    Channel = MaterialTextureChannel.Roughness,
                    DisplayLabel = MaterialChannelSet.GetDisplayLabel(MaterialTextureChannel.Roughness),
                    ShortBadge = MaterialChannelSet.GetShortBadge(MaterialTextureChannel.Roughness),
                    Texture = rTex,
                    OriginalTexture = rTex,
                    SourceImage = rTex.GetImage(),
                    SourcePath = standaloneRoughPath,
                    IsPresent = true,
                    IsDefaultFallback = false
                });
            }
        }

        // Shadow / NPR Transmissive
        if (!string.IsNullOrEmpty(shadowPath))
        {
            var shTex = Source2TextureLoader.GetOrLoadTexture(package, shadowPath, forceOpaque: true, addonPackage: addonPackage);
            if (shTex != null)
            {
                set.SetChannel(MaterialTextureChannel.Shadow, new ChannelMapEntry
                {
                    Channel = MaterialTextureChannel.Shadow,
                    DisplayLabel = MaterialChannelSet.GetDisplayLabel(MaterialTextureChannel.Shadow),
                    ShortBadge = MaterialChannelSet.GetShortBadge(MaterialTextureChannel.Shadow),
                    Texture = shTex,
                    OriginalTexture = shTex,
                    SourceImage = shTex.GetImage(),
                    SourcePath = shadowPath,
                    IsPresent = true,
                    IsDefaultFallback = false
                });
            }
        }

        // Extra / Mask maps (only added if actually discovered)
        TryAttachOptionalVtex(package, addonPackage, set, MaterialTextureChannel.SelfIllum, selfIllumPath);
        TryAttachOptionalVtex(package, addonPackage, set, MaterialTextureChannel.TintMask, tintMaskPath);
        TryAttachOptionalVtex(package, addonPackage, set, MaterialTextureChannel.Metalness, metalnessPath);
        TryAttachOptionalVtex(package, addonPackage, set, MaterialTextureChannel.RimMask, rimMaskPath);
        TryAttachOptionalVtex(package, addonPackage, set, MaterialTextureChannel.OutlineMask, outlineMaskPath);

        int fbW = Math.Max(64, loadedColor?.GetWidth() ?? 256);
        int fbH = Math.Max(64, loadedColor?.GetHeight() ?? 256);
        set.EnsureCoreFallbacks(Math.Min(fbW, 512), Math.Min(fbH, 512));
        return set;
    }

    private static void TryAttachOptionalVtex(
        Package package,
        Package addonPackage,
        MaterialChannelSet set,
        MaterialTextureChannel ch,
        string vtexPath)
    {
        if (string.IsNullOrEmpty(vtexPath)) return;
        var tex = Source2TextureLoader.GetOrLoadTexture(package, vtexPath, forceOpaque: false, addonPackage: addonPackage);
        if (tex == null) return;
        set.SetChannel(ch, new ChannelMapEntry
        {
            Channel = ch,
            DisplayLabel = MaterialChannelSet.GetDisplayLabel(ch),
            ShortBadge = MaterialChannelSet.GetShortBadge(ch),
            Texture = tex,
            OriginalTexture = tex,
            SourceImage = tex.GetImage(),
            SourcePath = vtexPath,
            IsPresent = true,
            IsDefaultFallback = false
        });
    }

    private static List<string> ScanVpkDirectoryVtex(Package package, Package addonPackage, string dirNorm, string stem)
    {
        var matches = new List<string>();
        string prefix = $"{dirNorm.ToLowerInvariant()}/";
        string stemLower = stem.ToLowerInvariant();

        void ScanPkg(Package pkg)
        {
            if (pkg == null) return;
            if (pkg.Entries == null || !pkg.Entries.TryGetValue("vtex_c", out var entries)) return;
            foreach (var e in entries)
            {
                string full = e.GetFullPath().Replace("\\", "/").TrimStart('/');
                string lower = full.ToLowerInvariant();
                if (!lower.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string fname = Path.GetFileNameWithoutExtension(lower);
                if (fname.StartsWith(stemLower + "_", StringComparison.Ordinal) ||
                    fname.StartsWith(stemLower + "_vmat", StringComparison.Ordinal) ||
                    fname.Equals(stemLower, StringComparison.Ordinal))
                {
                    if (!matches.Contains(full, StringComparer.OrdinalIgnoreCase))
                    {
                        matches.Add(full);
                    }
                }
            }
        }

        // Priority: addon mod VPK first, then base game VPK
        ScanPkg(addonPackage);
        ScanPkg(package);
        return matches;
    }

    /// <summary>
    /// 3-Tier PBR/NPR Channel Detector for Custom Imported 3D Models (.glb / .fbx).
    /// Tier 1: Native glTF 2.0 / StandardMaterial3D slots (including ORM Red->AO unpacking).
    /// Tier 2: Fuzzy filename/texture-name suffix matching (including _gloss inversion & _shadow).
    /// Tier 3: Clean procedural defaults marked IsDefaultFallback = true.
    /// </summary>
    public static MaterialChannelSet DiscoverCustomModelChannels(
        Godot.Material mat,
        string materialKey,
        GlbMaterialSlotInfo glbSlot = null,
        IReadOnlyList<Texture2D> externalPool = null)
    {
        var set = new MaterialChannelSet
        {
            MaterialKey = materialKey ?? mat?.ResourceName ?? "custom_material",
            MaterialStem = ExtractMaterialStem(materialKey ?? mat?.ResourceName ?? "custom_material")
        };

        // Already stored on material?
        if (mat != null && mat.HasMeta("DeadlockChannelSetAttached") && mat.GetMeta("DeadlockChannelSetAttached").AsBool())
        {
            // Rebuild only if needed
        }

        StandardMaterial3D std = mat as StandardMaterial3D;
        ShaderMaterial shaderMat = mat as ShaderMaterial;

        ImageTexture ToImageTex(Godot.Texture t, string fallbackName = "")
        {
            if (t == null) return null;
            if (t is ImageTexture it)
            {
                if (string.IsNullOrEmpty(it.ResourceName) && !string.IsNullOrEmpty(fallbackName))
                    it.ResourceName = fallbackName;
                return it;
            }
            if (t is Texture2D t2d)
            {
                var img = t2d.GetImage();
                if (img != null && !img.IsEmpty())
                {
                    var created = ImageTexture.CreateFromImage(img);
                    created.ResourceName = !string.IsNullOrEmpty(t2d.ResourceName) ? t2d.ResourceName : fallbackName;
                    return created;
                }
            }
            return null;
        }

        // --- TIER 1: Native glTF 2.0 / StandardMaterial3D Property Slots ---
        Texture2D rawBaseTex = std?.AlbedoTexture ?? shaderMat?.GetShaderParameter("albedo_texture").AsGodotObject() as Texture2D;
        Texture2D rawNormalTex = std?.NormalTexture ?? shaderMat?.GetShaderParameter("normal_texture").AsGodotObject() as Texture2D;
        Texture2D rawAoTex = std?.AOTexture ?? shaderMat?.GetShaderParameter("ao_texture").AsGodotObject() as Texture2D;
        Texture2D rawRoughTex = std?.RoughnessTexture ?? shaderMat?.GetShaderParameter("roughness_texture").AsGodotObject() as Texture2D;
        Texture2D rawMetalTex = std?.MetallicTexture;
        Texture2D rawEmissiveTex = std?.EmissionTexture ?? shaderMat?.GetShaderParameter("emission_texture").AsGodotObject() as Texture2D;

        if (rawBaseTex != null)
        {
            var cTex = ToImageTex(rawBaseTex, glbSlot?.BaseColorImageName ?? "albedo");
            if (cTex != null)
            {
                set.SetChannel(MaterialTextureChannel.Color, new ChannelMapEntry
                {
                    Channel = MaterialTextureChannel.Color,
                    DisplayLabel = MaterialChannelSet.GetDisplayLabel(MaterialTextureChannel.Color),
                    ShortBadge = MaterialChannelSet.GetShortBadge(MaterialTextureChannel.Color),
                    Texture = cTex,
                    OriginalTexture = cTex,
                    SourceImage = cTex.GetImage(),
                    SourcePath = glbSlot?.BaseColorImageName ?? cTex.ResourceName ?? "BaseColor",
                    IsPresent = true,
                    IsDefaultFallback = false
                });
            }
        }

        if (rawNormalTex != null)
        {
            var nTex = ToImageTex(rawNormalTex, glbSlot?.NormalImageName ?? "normal");
            if (nTex != null)
            {
                set.SetChannel(MaterialTextureChannel.Normal, new ChannelMapEntry
                {
                    Channel = MaterialTextureChannel.Normal,
                    DisplayLabel = MaterialChannelSet.GetDisplayLabel(MaterialTextureChannel.Normal),
                    ShortBadge = MaterialChannelSet.GetShortBadge(MaterialTextureChannel.Normal),
                    Texture = nTex,
                    OriginalTexture = nTex,
                    SourceImage = nTex.GetImage(),
                    SourcePath = glbSlot?.NormalImageName ?? nTex.ResourceName ?? "Normal",
                    IsPresent = true,
                    IsDefaultFallback = false
                });
            }
        }

        // Check Roughness + ORM/ARM packed AO + Metalness per glTF 2.0 specification (R=AO, G=Roughness, B=Metallic)
        if (rawRoughTex != null)
        {
            int rChan = std != null ? (int)std.RoughnessTextureChannel : 1; // Godot glTF importer uses Green (1)
            string rName = !string.IsNullOrEmpty(glbSlot?.MetallicRoughnessImageName)
                ? glbSlot.MetallicRoughnessImageName
                : (rawRoughTex.ResourceName ?? "roughness");

            bool isGloss = rName.Contains("gloss", StringComparison.OrdinalIgnoreCase);
            var roughExtracted = ExtractSingleChannelTexture(rawRoughTex, rChan, invert: isGloss, resourceTag: rName + "#roughness");
            if (roughExtracted != null)
            {
                set.SetChannel(MaterialTextureChannel.Roughness, new ChannelMapEntry
                {
                    Channel = MaterialTextureChannel.Roughness,
                    DisplayLabel = MaterialChannelSet.GetDisplayLabel(MaterialTextureChannel.Roughness),
                    ShortBadge = MaterialChannelSet.GetShortBadge(MaterialTextureChannel.Roughness),
                    Texture = roughExtracted,
                    OriginalTexture = roughExtracted,
                    SourceImage = roughExtracted.GetImage(),
                    SourcePath = isGloss ? $"{rName} (Inverted Gloss)" : rName,
                    IsPresent = true,
                    IsDefaultFallback = false
                });
            }

            // Metalness in Blue channel if non-zero/present
            if (rawMetalTex == null && (rName.Contains("orm", StringComparison.OrdinalIgnoreCase) ||
                                        rName.Contains("arm", StringComparison.OrdinalIgnoreCase) ||
                                        rName.Contains("metal", StringComparison.OrdinalIgnoreCase) ||
                                        ChannelHasVariation(rawRoughTex, 2, 16)))
            {
                var metalExtracted = ExtractSingleChannelTexture(rawRoughTex, 2, invert: false, resourceTag: rName + "#metal");
                if (metalExtracted != null)
                {
                    set.SetChannel(MaterialTextureChannel.Metalness, new ChannelMapEntry
                    {
                        Channel = MaterialTextureChannel.Metalness,
                        DisplayLabel = MaterialChannelSet.GetDisplayLabel(MaterialTextureChannel.Metalness),
                        ShortBadge = MaterialChannelSet.GetShortBadge(MaterialTextureChannel.Metalness),
                        Texture = metalExtracted,
                        OriginalTexture = metalExtracted,
                        SourceImage = metalExtracted.GetImage(),
                        SourcePath = $"{rName} (B Channel)",
                        IsPresent = true,
                        IsDefaultFallback = false
                    });
                }
            }

            // ORM/ARM Red-channel AO detection!
            bool sharedOcc = (glbSlot != null && glbSlot.OcclusionSharesMetallicRoughness) ||
                             ReferenceEquals(rawAoTex, rawRoughTex) ||
                             rName.Contains("orm", StringComparison.OrdinalIgnoreCase) ||
                             rName.Contains("arm", StringComparison.OrdinalIgnoreCase) ||
                             rName.Contains("mrao", StringComparison.OrdinalIgnoreCase) ||
                             (rawAoTex == null && ChannelHasVariation(rawRoughTex, 0, 10));
            if (sharedOcc && ChannelHasVariation(rawRoughTex, 0, 6))
            {
                var aoUnpacked = ExtractSingleChannelTexture(rawRoughTex, 0, invert: false, resourceTag: rName + "#ao_orm");
                if (aoUnpacked != null)
                {
                    set.SetChannel(MaterialTextureChannel.AmbientOcclusion, new ChannelMapEntry
                    {
                        Channel = MaterialTextureChannel.AmbientOcclusion,
                        DisplayLabel = MaterialChannelSet.GetDisplayLabel(MaterialTextureChannel.AmbientOcclusion),
                        ShortBadge = MaterialChannelSet.GetShortBadge(MaterialTextureChannel.AmbientOcclusion),
                        Texture = aoUnpacked,
                        OriginalTexture = aoUnpacked,
                        SourceImage = aoUnpacked.GetImage(),
                        SourcePath = $"{rName} (ORM Red)",
                        IsPresent = true,
                        IsDefaultFallback = false
                    });
                }
            }
        }

        if (!set.HasRealMap(MaterialTextureChannel.AmbientOcclusion) && rawAoTex != null)
        {
            int aoChan = std != null ? (int)std.AOTextureChannel : 0;
            string aoName = glbSlot?.OcclusionImageName ?? rawAoTex.ResourceName ?? "AO";
            var aoTex = ExtractSingleChannelTexture(rawAoTex, aoChan, invert: false, resourceTag: aoName + "#ao")
                        ?? ToImageTex(rawAoTex, aoName);
            if (aoTex != null)
            {
                set.SetChannel(MaterialTextureChannel.AmbientOcclusion, new ChannelMapEntry
                {
                    Channel = MaterialTextureChannel.AmbientOcclusion,
                    DisplayLabel = MaterialChannelSet.GetDisplayLabel(MaterialTextureChannel.AmbientOcclusion),
                    ShortBadge = MaterialChannelSet.GetShortBadge(MaterialTextureChannel.AmbientOcclusion),
                    Texture = aoTex,
                    OriginalTexture = aoTex,
                    SourceImage = aoTex.GetImage(),
                    SourcePath = aoName,
                    IsPresent = true,
                    IsDefaultFallback = false
                });
            }
        }

        if (!set.HasRealMap(MaterialTextureChannel.Metalness) && rawMetalTex != null)
        {
            var mTex = ExtractSingleChannelTexture(rawMetalTex, std != null ? (int)std.MetallicTextureChannel : 2, false, "metalness");
            if (mTex != null)
            {
                set.SetChannel(MaterialTextureChannel.Metalness, new ChannelMapEntry
                {
                    Channel = MaterialTextureChannel.Metalness,
                    DisplayLabel = MaterialChannelSet.GetDisplayLabel(MaterialTextureChannel.Metalness),
                    ShortBadge = MaterialChannelSet.GetShortBadge(MaterialTextureChannel.Metalness),
                    Texture = mTex,
                    OriginalTexture = mTex,
                    SourceImage = mTex.GetImage(),
                    SourcePath = rawMetalTex.ResourceName ?? "Metalness",
                    IsPresent = true,
                    IsDefaultFallback = false
                });
            }
        }

        if (rawEmissiveTex != null)
        {
            string eName = glbSlot?.EmissiveImageName ?? rawEmissiveTex.ResourceName ?? "Emissive";
            var eTex = ToImageTex(rawEmissiveTex, eName);
            if (eTex != null)
            {
                set.SetChannel(MaterialTextureChannel.SelfIllum, new ChannelMapEntry
                {
                    Channel = MaterialTextureChannel.SelfIllum,
                    DisplayLabel = MaterialChannelSet.GetDisplayLabel(MaterialTextureChannel.SelfIllum),
                    ShortBadge = MaterialChannelSet.GetShortBadge(MaterialTextureChannel.SelfIllum),
                    Texture = eTex,
                    OriginalTexture = eTex,
                    SourceImage = eTex.GetImage(),
                    SourcePath = eName,
                    IsPresent = true,
                    IsDefaultFallback = false
                });
            }
        }

        // --- TIER 2: Fuzzy Naming Convention Matching (external / sibling textures) ---
        if (externalPool != null && externalPool.Count > 0)
        {
            ApplyFuzzyTexturePool(set, externalPool, materialKey);
        }

        // --- TIER 3: Clean Procedural Fallbacks ---
        int fbW = Math.Max(64, set.GetChannel(MaterialTextureChannel.Color)?.Texture?.GetWidth() ?? 256);
        int fbH = Math.Max(64, set.GetChannel(MaterialTextureChannel.Color)?.Texture?.GetHeight() ?? 256);
        set.EnsureCoreFallbacks(Math.Min(fbW, 512), Math.Min(fbH, 512));
        return set;
    }

    private static void ApplyFuzzyTexturePool(MaterialChannelSet set, IReadOnlyList<Texture2D> pool, string matKey)
    {
        string matStem = ExtractMaterialStem(matKey);
        bool MatchesStem(string name)
        {
            if (string.IsNullOrEmpty(matStem) || matStem == "custom_material") return true;
            string n = Path.GetFileNameWithoutExtension(name ?? "").ToLowerInvariant();
            return n.Contains(matStem, StringComparison.Ordinal) || pool.Count <= 8;
        }

        foreach (var tex in pool)
        {
            if (tex == null) continue;
            string name = !string.IsNullOrEmpty(tex.ResourceName) ? tex.ResourceName : Path.GetFileName(tex.ResourcePath ?? "");
            if (string.IsNullOrEmpty(name) || !MatchesStem(name)) continue;
            string lower = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();

            if (!set.HasRealMap(MaterialTextureChannel.AmbientOcclusion) &&
                (HasSuffixToken(lower, "ao") || HasSuffixToken(lower, "ambientocclusion") ||
                 HasSuffixToken(lower, "occlusion") || HasSuffixToken(lower, "occ")))
            {
                var ao = ExtractSingleChannelTexture(tex, 0, false, name + "#ao");
                if (ao != null) set.SetChannel(MaterialTextureChannel.AmbientOcclusion, BuildDetectedEntry(MaterialTextureChannel.AmbientOcclusion, ao, name));
            }
            else if (!set.HasRealMap(MaterialTextureChannel.Normal) &&
                     (HasSuffixToken(lower, "n") || HasSuffixToken(lower, "norm") ||
                      HasSuffixToken(lower, "normal") || HasSuffixToken(lower, "nrm") || HasSuffixToken(lower, "nor")))
            {
                var img = tex.GetImage();
                if (img != null)
                {
                    var nTex = tex as ImageTexture ?? ImageTexture.CreateFromImage(img);
                    set.SetChannel(MaterialTextureChannel.Normal, BuildDetectedEntry(MaterialTextureChannel.Normal, nTex, name));
                }
            }
            else if (!set.HasRealMap(MaterialTextureChannel.Roughness) &&
                     (HasSuffixToken(lower, "r") || HasSuffixToken(lower, "rough") ||
                      HasSuffixToken(lower, "roughness") || HasSuffixToken(lower, "rgh") || HasSuffixToken(lower, "spec")))
            {
                var rTex = ExtractSingleChannelTexture(tex, 1, false, name + "#rough");
                if (rTex != null) set.SetChannel(MaterialTextureChannel.Roughness, BuildDetectedEntry(MaterialTextureChannel.Roughness, rTex, name));
            }
            else if (!set.HasRealMap(MaterialTextureChannel.Roughness) && HasSuffixToken(lower, "gloss"))
            {
                var gTex = ExtractSingleChannelTexture(tex, 1, true, name + "#rough_inv_gloss");
                if (gTex != null) set.SetChannel(MaterialTextureChannel.Roughness, BuildDetectedEntry(MaterialTextureChannel.Roughness, gTex, $"{name} (Inverted Gloss)"));
            }
            else if (!set.HasRealMap(MaterialTextureChannel.Shadow) &&
                     (HasSuffixToken(lower, "shadow") || HasSuffixToken(lower, "shd") ||
                      HasSuffixToken(lower, "shade") || HasSuffixToken(lower, "ilm") ||
                      HasSuffixToken(lower, "sss") || HasSuffixToken(lower, "tonemap")))
            {
                var img = tex.GetImage();
                if (img != null)
                {
                    var shTex = tex as ImageTexture ?? ImageTexture.CreateFromImage(img);
                    set.SetChannel(MaterialTextureChannel.Shadow, BuildDetectedEntry(MaterialTextureChannel.Shadow, shTex, name));
                }
            }
            else if (HasSuffixToken(lower, "orm") || HasSuffixToken(lower, "arm") ||
                     HasSuffixToken(lower, "mrao") || HasSuffixToken(lower, "comp"))
            {
                if (!set.HasRealMap(MaterialTextureChannel.AmbientOcclusion))
                {
                    var ao = ExtractSingleChannelTexture(tex, 0, false, name + "#orm_ao");
                    if (ao != null) set.SetChannel(MaterialTextureChannel.AmbientOcclusion, BuildDetectedEntry(MaterialTextureChannel.AmbientOcclusion, ao, $"{name} (R)"));
                }
                if (!set.HasRealMap(MaterialTextureChannel.Roughness))
                {
                    var r = ExtractSingleChannelTexture(tex, 1, false, name + "#orm_rough");
                    if (r != null) set.SetChannel(MaterialTextureChannel.Roughness, BuildDetectedEntry(MaterialTextureChannel.Roughness, r, $"{name} (G)"));
                }
                if (!set.HasRealMap(MaterialTextureChannel.Metalness))
                {
                    var m = ExtractSingleChannelTexture(tex, 2, false, name + "#orm_metal");
                    if (m != null) set.SetChannel(MaterialTextureChannel.Metalness, BuildDetectedEntry(MaterialTextureChannel.Metalness, m, $"{name} (B)"));
                }
            }
        }
    }

    private static ChannelMapEntry BuildDetectedEntry(MaterialTextureChannel ch, ImageTexture tex, string source) => new()
    {
        Channel = ch,
        DisplayLabel = MaterialChannelSet.GetDisplayLabel(ch),
        ShortBadge = MaterialChannelSet.GetShortBadge(ch),
        Texture = tex,
        OriginalTexture = tex,
        SourceImage = tex.GetImage(),
        SourcePath = source,
        IsPresent = true,
        IsDefaultFallback = false
    };

    private static bool HasSuffixToken(string baseNameLower, string token)
    {
        if (baseNameLower.EndsWith("_" + token, StringComparison.Ordinal) ||
            baseNameLower.EndsWith("-" + token, StringComparison.Ordinal) ||
            baseNameLower.Equals(token, StringComparison.Ordinal))
        {
            return true;
        }
        return baseNameLower.Contains("_" + token + "_", StringComparison.Ordinal) ||
               baseNameLower.Contains("-" + token + "-", StringComparison.Ordinal);
    }

    /// <summary>
    /// Synchronizes all channel toggles, solo preview mode, and updated textures to any
    /// live Godot 3D material (StandardMaterial3D, toon_shader, source2_pbr, hero_painter_overlay).
    /// </summary>
    public static void ApplyChannelsToMaterial(
        Godot.Material mat,
        MaterialChannelSet channelSet,
        bool isPainterOverlay = false)
    {
        if (mat == null || !GodotObject.IsInstanceValid(mat) || channelSet == null) return;

        var color = channelSet.GetChannel(MaterialTextureChannel.Color);
        var ao = channelSet.GetChannel(MaterialTextureChannel.AmbientOcclusion);
        var normal = channelSet.GetChannel(MaterialTextureChannel.Normal);
        var rough = channelSet.GetChannel(MaterialTextureChannel.Roughness);
        var shadow = channelSet.GetChannel(MaterialTextureChannel.Shadow);
        var illum = channelSet.GetChannel(MaterialTextureChannel.SelfIllum);
        var metal = channelSet.GetChannel(MaterialTextureChannel.Metalness);

        int debugIdx = MaterialChannelSet.ChannelToShaderDebugIndex(channelSet.SoloPreviewChannel);
        bool enColor = color?.IsEnabled ?? true;
        bool enAo = (ao?.IsEnabled ?? true) && (ao?.HasValidTexture ?? false);
        bool enNormal = (normal?.IsEnabled ?? true) && (normal?.HasValidTexture ?? false);
        bool enRough = (rough?.IsEnabled ?? true) && (rough?.HasValidTexture ?? false);
        bool enShadow = (shadow?.IsEnabled ?? true) && (shadow?.HasValidTexture ?? false) && !(shadow?.IsDefaultFallback ?? true);
        bool enIllum = (illum?.IsEnabled ?? true) && (illum?.HasValidTexture ?? false) && !(illum?.IsDefaultFallback ?? true);

        if (mat is ShaderMaterial sm)
        {
            // Toggle flags & solo view channel
            sm.SetShaderParameter("enable_ao", enAo);
            sm.SetShaderParameter("enable_normal", enNormal);
            sm.SetShaderParameter("enable_roughness", enRough);
            sm.SetShaderParameter("enable_shadow_map", enShadow);
            sm.SetShaderParameter("enable_color_map", enColor);
            sm.SetShaderParameter("debug_view_channel", debugIdx);

            // Detect shader family by shader code uniform declarations
            string shaderCode = sm.Shader?.Code ?? string.Empty;
            bool hasSource2NormalRoughness = shaderCode.Contains("g_tNormalRoughness", StringComparison.Ordinal);

            if (hasSource2NormalRoughness)
            {
                if (ao?.HasValidTexture == true) sm.SetShaderParameter("g_tAmbientOcclusion", ao.Texture);
                if (shadow?.HasValidTexture == true)
                {
                    sm.SetShaderParameter("g_tNprTransmissiveColor", shadow.Texture);
                    sm.SetShaderParameter("has_shadow_map", enShadow);
                }
                if (color?.HasValidTexture == true && !isPainterOverlay)
                {
                    if (shaderCode.Contains("g_tColor", StringComparison.Ordinal))
                    {
                        sm.SetShaderParameter("g_tColor", color.Texture);
                    }
                }
                if (normal?.HasValidTexture == true || rough?.HasValidTexture == true)
                {
                    if ((normal?.IsDirty == true || rough?.IsDirty == true) &&
                        (normal?.SourceImage != null || rough?.SourceImage != null))
                    {
                        var packedImg = PackNormalRoughnessImage(normal?.SourceImage, rough?.SourceImage, packAsBlueToAlpha: false);
                        if (packedImg != null)
                        {
                            sm.SetShaderParameter("g_tNormalRoughness", ImageTexture.CreateFromImage(packedImg));
                        }
                    }
                }
            }
            else
            {
                // toon_shader.gdshader or hero_painter_overlay.gdshader
                if (!isPainterOverlay && color?.HasValidTexture == true)
                {
                    sm.SetShaderParameter("albedo_texture", color.Texture);
                }
                if (ao?.HasValidTexture == true)
                {
                    sm.SetShaderParameter("ao_texture", ao.Texture);
                }
                if (normal?.HasValidTexture == true)
                {
                    sm.SetShaderParameter("normal_texture", normal.Texture);
                    sm.SetShaderParameter("normal_is_source2", false);
                }
                if (rough?.HasValidTexture == true)
                {
                    sm.SetShaderParameter("roughness_texture", rough.Texture);
                }
                if (shadow?.HasValidTexture == true)
                {
                    sm.SetShaderParameter("shadow_tint_texture", shadow.Texture);
                    sm.SetShaderParameter("has_shadow_tint_texture", enShadow);
                }
                if (illum?.HasValidTexture == true)
                {
                    sm.SetShaderParameter("emission_texture", illum.Texture);
                    sm.SetShaderParameter("enable_emission", enIllum);
                }
                if (metal?.HasValidTexture == true)
                {
                    sm.SetShaderParameter("metallic_texture", metal.Texture);
                }
            }
        }
        else if (mat is StandardMaterial3D std)
        {
            if (color?.HasValidTexture == true)
            {
                std.AlbedoTexture = enColor ? color.Texture : CreateDefaultFallbackTexture(MaterialTextureChannel.Color, 16, 16);
            }

            std.AOEnabled = enAo;
            if (enAo && ao?.HasValidTexture == true)
            {
                std.AOTexture = ao.Texture;
                std.AOTextureChannel = BaseMaterial3D.TextureChannel.Red;
            }
            else
            {
                std.AOTexture = null;
            }

            std.NormalEnabled = enNormal;
            if (enNormal && normal?.HasValidTexture == true)
            {
                std.NormalTexture = normal.Texture;
            }

            if (enRough && rough?.HasValidTexture == true)
            {
                std.RoughnessTexture = rough.Texture;
                std.RoughnessTextureChannel = BaseMaterial3D.TextureChannel.Red;
                std.Roughness = 1.0f;
            }
            else
            {
                std.RoughnessTexture = null;
                std.Roughness = 0.72f;
            }

            if (enIllum && illum?.HasValidTexture == true)
            {
                std.EmissionEnabled = true;
                std.EmissionTexture = illum.Texture;
            }
        }
    }
}
