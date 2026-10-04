using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeadlockPlayground.UI;

namespace DeadlockPlayground.Painter
{
    public class DptexManifest
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("hero_id")]
        public string HeroId { get; set; } = "hero";

        [JsonPropertyName("target_mesh_slot")]
        public string TargetMeshSlot { get; set; } = "body";

        [JsonPropertyName("canvas_resolution")]
        public int[] CanvasResolution { get; set; } = new int[] { 2048, 2048 };

        [JsonPropertyName("palette")]
        public List<string> Palette { get; set; } = new();

        [JsonPropertyName("active_layer_index")]
        public int ActiveLayerIndex { get; set; } = 0;

        [JsonPropertyName("layers")]
        public List<DptexLayerInfo> Layers { get; set; } = new();
    }

    public class DptexLayerInfo
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("visible")]
        public bool Visible { get; set; } = true;

        [JsonPropertyName("opacity")]
        public float Opacity { get; set; } = 1.0f;

        [JsonPropertyName("blend_mode")]
        public string BlendMode { get; set; } = "Normal";

        [JsonPropertyName("file")]
        public string File { get; set; }
    }

    public class HeroContext
    {
        public string HeroId { get; set; } = "hero";
        public string TargetMeshSlot { get; set; } = "body";
        public QuickColorPaletteUI PaletteUI { get; set; }
    }

    public class ProjectSaveResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public string FilePath { get; set; }
    }

    public class ProjectLoadResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public DptexManifest Manifest { get; set; }
    }

    public static class ProjectFileManager
    {
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        public static ProjectSaveResult SaveProject(string filePath, SkinLayerManager layerManager, HeroContext context)
        {
            return SaveProject(filePath, layerManager, context?.HeroId ?? "hero", context?.TargetMeshSlot ?? "body", context?.PaletteUI);
        }

        public static ProjectSaveResult SaveProject(
            string filePath,
            SkinLayerManager layerManager,
            string heroId,
            string targetMeshSlot,
            QuickColorPaletteUI paletteUI = null)
        {
            var result = new ProjectSaveResult { FilePath = filePath };

            if (string.IsNullOrWhiteSpace(filePath))
            {
                result.Success = false;
                result.ErrorMessage = "File path cannot be empty.";
                return result;
            }

            if (layerManager == null)
            {
                result.Success = false;
                result.ErrorMessage = "SkinLayerManager reference is null.";
                return result;
            }

            try
            {
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                int width = layerManager.CanvasSize.X > 0 ? layerManager.CanvasSize.X : 2048;
                int height = layerManager.CanvasSize.Y > 0 ? layerManager.CanvasSize.Y : 2048;

                var manifest = new DptexManifest
                {
                    Version = 1,
                    HeroId = heroId ?? "hero",
                    TargetMeshSlot = targetMeshSlot ?? "body",
                    CanvasResolution = new int[] { width, height },
                    ActiveLayerIndex = layerManager.ActiveLayerIndex,
                    Palette = paletteUI?.GetPaletteHistory() ?? new List<string>()
                };

                var layerPngBytes = new List<(string FileName, byte[] Data)>();

                for (int i = 0; i < layerManager.Layers.Count; i++)
                {
                    var layer = layerManager.Layers[i];
                    string fileName = $"layer_{i}.png";
                    string layerId = $"layer_{i}";

                    manifest.Layers.Add(new DptexLayerInfo
                    {
                        Id = layerId,
                        Name = layer.Name,
                        Visible = layer.IsVisible,
                        Opacity = layer.Opacity,
                        BlendMode = layer.BlendMode.ToString(),
                        File = fileName
                    });

                    Image img = LayerGpuDataToImage(layer.GpuData, width, height);
                    byte[] png = img.SavePngToBuffer();
                    layerPngBytes.Add((fileName, png));
                }

                string manifestJson = JsonSerializer.Serialize(manifest, _jsonOptions);

                // Create ZIP archive
                using (var fileStream = System.IO.File.Create(filePath))
                using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
                {
                    // 1. Write manifest.json (strictly without BOM)
                    var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
                    using (var entryStream = manifestEntry.Open())
                    {
                        byte[] jsonBytes = Encoding.UTF8.GetBytes(manifestJson);
                        entryStream.Write(jsonBytes, 0, jsonBytes.Length);
                    }

                    // 2. Write layer images
                    foreach (var (fileName, data) in layerPngBytes)
                    {
                        var layerEntry = archive.CreateEntry(fileName, CompressionLevel.Fastest);
                        using (var entryStream = layerEntry.Open())
                        {
                            entryStream.Write(data, 0, data.Length);
                        }
                    }
                }

                result.Success = true;
                GD.Print($"[ProjectFileManager] Successfully saved project to: {filePath} ({manifest.Layers.Count} layers)");
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                GD.PrintErr($"[ProjectFileManager] Failed to save project: {ex.Message}");
            }

            return result;
        }

        public static DptexManifest PeekManifest(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
            {
                return null;
            }

            try
            {
                using var fileStream = System.IO.File.OpenRead(filePath);
                using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);
                var entry = archive.GetEntry("manifest.json");
                if (entry == null) return null;

                using var stream = entry.Open();
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                byte[] bytes = ms.ToArray();
                int offset = 0;
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    offset = 3;
                }
                string json = Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
                return JsonSerializer.Deserialize<DptexManifest>(json, _jsonOptions);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ProjectFileManager] Failed to peek manifest: {ex.Message}");
                return null;
            }
        }

        public static ProjectLoadResult LoadProject(
            string filePath,
            SkinLayerManager layerManager,
            QuickColorPaletteUI paletteUI = null)
        {
            var result = new ProjectLoadResult();

            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
            {
                result.Success = false;
                result.ErrorMessage = $"File does not exist: {filePath}";
                return result;
            }

            if (layerManager == null)
            {
                result.Success = false;
                result.ErrorMessage = "SkinLayerManager reference is null.";
                return result;
            }

            try
            {
                byte[] manifestBytes = null;
                var layerImages = new Dictionary<string, byte[]>();

                // 1. Read ZIP in memory
                using (var fileStream = System.IO.File.OpenRead(filePath))
                using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Read))
                {
                    var manifestEntry = archive.GetEntry("manifest.json");
                    if (manifestEntry == null)
                    {
                        result.Success = false;
                        result.ErrorMessage = "Corrupt project file: missing manifest.json";
                        return result;
                    }

                    using (var ms = new MemoryStream())
                    {
                        using (var s = manifestEntry.Open())
                        {
                            s.CopyTo(ms);
                        }
                        manifestBytes = ms.ToArray();
                    }

                    foreach (var entry in archive.Entries)
                    {
                        if (entry.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        {
                            using var ms = new MemoryStream();
                            using (var s = entry.Open())
                            {
                                s.CopyTo(ms);
                            }
                            byte[] bytes = ms.ToArray();
                            layerImages[entry.Name] = bytes;
                            layerImages[entry.FullName] = bytes;
                            string fn = Path.GetFileName(entry.FullName);
                            if (!string.IsNullOrEmpty(fn)) layerImages[fn] = bytes;
                        }
                    }
                }

                // 2. Parse manifest.json (strip UTF-8 BOM if present)
                int bomOffset = 0;
                if (manifestBytes != null && manifestBytes.Length >= 3 &&
                    manifestBytes[0] == 0xEF && manifestBytes[1] == 0xBB && manifestBytes[2] == 0xBF)
                {
                    bomOffset = 3;
                }
                string manifestJson = Encoding.UTF8.GetString(manifestBytes, bomOffset, manifestBytes.Length - bomOffset);
                manifestJson = manifestJson.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
                var manifest = JsonSerializer.Deserialize<DptexManifest>(manifestJson, _jsonOptions);
                if (manifest == null)
                {
                    result.Success = false;
                    result.ErrorMessage = "Failed to deserialize manifest.json";
                    return result;
                }
                result.Manifest = manifest;

                // 3. Ensure canvas resolution
                int targetW = (manifest.CanvasResolution != null && manifest.CanvasResolution.Length >= 2) ? manifest.CanvasResolution[0] : 2048;
                int targetH = (manifest.CanvasResolution != null && manifest.CanvasResolution.Length >= 2) ? manifest.CanvasResolution[1] : 2048;

                if (layerManager.CanvasSize.X != targetW || layerManager.CanvasSize.Y != targetH)
                {
                    layerManager.SetCanvasResolution(new Vector2I(targetW, targetH));
                }

                // 4. Reconstruct layer objects
                layerManager.ClearAllLayers();

                if (manifest.Layers == null || manifest.Layers.Count == 0)
                {
                    layerManager.AddNewLayer("Paint Layer 1");
                }
                else
                {
                    for (int i = 0; i < manifest.Layers.Count; i++)
                    {
                        var info = manifest.Layers[i];
                        var layer = layerManager.AddNewLayer(info.Name ?? $"Layer {i + 1}");

                        // Blend mode
                        if (Enum.TryParse<LayerBlendMode>(info.BlendMode, true, out var bm))
                        {
                            layer.SetBlendMode(bm);
                        }
                        else
                        {
                            layer.SetBlendMode(LayerBlendMode.Normal);
                        }

                        // Opacity & Visibility
                        layer.SetOpacity(info.Opacity);
                        layer.SetVisibility(info.Visible);

                        // Load layer image with robust filename fallbacks
                        byte[] pngData = null;
                        if (!string.IsNullOrEmpty(info.File))
                        {
                            if (!layerImages.TryGetValue(info.File, out pngData))
                            {
                                string baseName = Path.GetFileName(info.File);
                                layerImages.TryGetValue(baseName, out pngData);
                            }
                        }
                        if (pngData == null)
                        {
                            layerImages.TryGetValue($"layer_{i}.png", out pngData);
                        }

                        if (pngData != null && pngData.Length > 0)
                        {
                            var img = new Image();
                            var err = img.LoadPngFromBuffer(pngData);
                            if (err == Error.Ok)
                            {
                                layer.GpuData = ImageToLayerGpuData(img, targetW, targetH);
                            }
                            else
                            {
                                GD.PrintErr($"[ProjectFileManager] Failed to decode PNG for layer: {info.File}");
                                layer.GpuData = new byte[targetW * targetH * 8];
                            }
                        }
                        else
                        {
                            layer.GpuData = new byte[targetW * targetH * 8];
                        }
                    }
                }

                // 5. Select active layer
                int activeIndex = Math.Clamp(manifest.ActiveLayerIndex, 0, Math.Max(0, layerManager.Layers.Count - 1));
                layerManager.SelectLayer(activeIndex);

                // 6. Trigger composite redraw onto viewport and 3D preview model
                layerManager.RebuildBaseAtlasBuffer();
                layerManager.RecompositeGpuLayers();
                layerManager.RecordInitialSnapshot();
                layerManager.NotifyStackChanged();

                // 7. Restore color palette history array
                if (paletteUI != null && manifest.Palette != null && manifest.Palette.Count > 0)
                {
                    paletteUI.SetPaletteHistory(manifest.Palette);
                }

                result.Success = true;
                GD.Print($"[ProjectFileManager] Successfully loaded project from: {filePath} ({layerManager.Layers.Count} layers restored)");
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                GD.PrintErr($"[ProjectFileManager] Failed to load project: {ex.Message}");
            }

            return result;
        }

        public static Image LayerGpuDataToImage(byte[] gpuData, int width, int height)
        {
            if (gpuData == null || gpuData.Length == 0)
            {
                return Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
            }

            int actualPixelCount = gpuData.Length / 8;
            int srcWidth = (int)MathF.Round(MathF.Sqrt(actualPixelCount));
            int srcHeight = srcWidth > 0 ? (actualPixelCount / srcWidth) : 0;
            if (srcWidth <= 0 || srcHeight <= 0 || srcWidth * srcHeight * 8 != gpuData.Length)
            {
                srcWidth = width;
                srcHeight = height;
            }

            int pixelCount = srcWidth * srcHeight;
            byte[] dstData = new byte[pixelCount * 4];

            unsafe
            {
                fixed (byte* pSrc = gpuData, pDst = dstData)
                {
                    Half* hSrc = (Half*)pSrc;

                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcOff = i * 4;
                        float r = (float)hSrc[srcOff];
                        float g = (float)hSrc[srcOff + 1];
                        float b = (float)hSrc[srcOff + 2];
                        float a = Mathf.Clamp((float)hSrc[srcOff + 3], 0.0f, 1.0f);

                        // Convert from Linear HDR back to sRGB for PNG export
                        Color lin = new Color(r, g, b, a);
                        Color srgb = lin.LinearToSrgb();

                        int dstOff = i * 4;
                        pDst[dstOff] = (byte)Math.Clamp((int)MathF.Round(srgb.R * 255.0f), 0, 255);
                        pDst[dstOff + 1] = (byte)Math.Clamp((int)MathF.Round(srgb.G * 255.0f), 0, 255);
                        pDst[dstOff + 2] = (byte)Math.Clamp((int)MathF.Round(srgb.B * 255.0f), 0, 255);
                        pDst[dstOff + 3] = (byte)Math.Clamp((int)MathF.Round(a * 255.0f), 0, 255);
                    }
                }
            }

            var resultImg = Image.CreateFromData(srcWidth, srcHeight, false, Image.Format.Rgba8, dstData);
            if (srcWidth != width || srcHeight != height)
            {
                resultImg.Resize(width, height, Image.Interpolation.Bilinear);
            }
            return resultImg;
        }

        public static byte[] ImageToLayerGpuData(Image img, int width, int height)
        {
            byte[] gpuData = new byte[width * height * 8];
            if (img == null) return gpuData;

            if (img.GetWidth() != width || img.GetHeight() != height)
            {
                img = (Image)img.Duplicate();
                img.Resize(width, height, Image.Interpolation.Bilinear);
            }

            if (img.GetFormat() != Image.Format.Rgba8)
            {
                img = (Image)img.Duplicate();
                img.Convert(Image.Format.Rgba8);
            }

            byte[] srcData = img.GetData();
            int pixelCount = width * height;

            unsafe
            {
                fixed (byte* pSrc = srcData, pDst = gpuData)
                {
                    Half* hDst = (Half*)pDst;

                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcOff = i * 4;
                        float r = pSrc[srcOff] * (1.0f / 255.0f);
                        float g = pSrc[srcOff + 1] * (1.0f / 255.0f);
                        float b = pSrc[srcOff + 2] * (1.0f / 255.0f);
                        float a = pSrc[srcOff + 3] * (1.0f / 255.0f);

                        // Convert from sRGB PNG back to Linear HDR
                        Color srgb = new Color(r, g, b, a);
                        Color lin = srgb.SrgbToLinear();

                        int dstOff = i * 4;
                        hDst[dstOff] = (Half)lin.R;
                        hDst[dstOff + 1] = (Half)lin.G;
                        hDst[dstOff + 2] = (Half)lin.B;
                        hDst[dstOff + 3] = (Half)a;
                    }
                }
            }

            return gpuData;
        }
    }
}
