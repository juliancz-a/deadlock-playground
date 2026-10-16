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
        public int Version { get; set; } = 2;

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

        // Legacy / primary active layer list (retained for Version 1 backwards compatibility)
        [JsonPropertyName("layers")]
        public List<DptexLayerInfo> Layers { get; set; } = new();

        // Multi-material support (Version 2)
        [JsonPropertyName("materials")]
        public Dictionary<string, DptexMaterialInfo> Materials { get; set; } = new();
    }

    public class DptexMaterialInfo
    {
        [JsonPropertyName("material_key")]
        public string MaterialKey { get; set; }

        [JsonPropertyName("slot_name")]
        public string SlotName { get; set; }

        [JsonPropertyName("canvas_resolution")]
        public int[] CanvasResolution { get; set; } = new int[] { 2048, 2048 };

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

                // 1. Synchronize CPU buffers from GPU VRAM and save active context
                layerManager.SaveActiveContext();

                int activeWidth = layerManager.CanvasSize.X > 0 ? layerManager.CanvasSize.X : 2048;
                int activeHeight = layerManager.CanvasSize.Y > 0 ? layerManager.CanvasSize.Y : 2048;

                var manifest = new DptexManifest
                {
                    Version = 2,
                    HeroId = heroId ?? "hero",
                    TargetMeshSlot = targetMeshSlot ?? "body",
                    CanvasResolution = new int[] { activeWidth, activeHeight },
                    ActiveLayerIndex = layerManager.ActiveLayerIndex,
                    Palette = paletteUI?.GetPaletteHistory() ?? new List<string>()
                };

                var allContexts = layerManager.GetAllMaterialContextsForExport(targetMeshSlot);
                var layerPngBytes = new List<(string FileName, byte[] Data)>();

                int matIndex = 0;
                foreach (var ctxData in allContexts)
                {
                    string safeSlot = !string.IsNullOrEmpty(ctxData.SlotName) ? ctxData.SlotName : $"mat_{matIndex}";
                    safeSlot = safeSlot.Replace("/", "_").Replace("\\", "_").Replace(":", "_");

                    var matInfo = new DptexMaterialInfo
                    {
                        MaterialKey = ctxData.MaterialKey,
                        SlotName = ctxData.SlotName,
                        CanvasResolution = new int[] { ctxData.CanvasSize.X, ctxData.CanvasSize.Y },
                        ActiveLayerIndex = ctxData.ActiveLayerIndex
                    };

                    for (int i = 0; i < ctxData.Layers.Count; i++)
                    {
                        var layer = ctxData.Layers[i];
                        string fileName = $"materials/{safeSlot}_layer_{i}.png";
                        string layerId = $"{safeSlot}_layer_{i}";

                        matInfo.Layers.Add(new DptexLayerInfo
                        {
                            Id = layerId,
                            Name = layer.Name,
                            Visible = layer.IsVisible,
                            Opacity = layer.Opacity,
                            BlendMode = layer.BlendMode.ToString(),
                            File = fileName
                        });

                        Image img = LayerGpuDataToImage(layer.GpuData, ctxData.CanvasSize.X, ctxData.CanvasSize.Y);
                        byte[] png = img.SavePngToBuffer();
                        layerPngBytes.Add((fileName, png));

                        // For backwards compatibility: mirror the active context to root layers
                        if (ctxData.IsActive)
                        {
                            string rootFileName = $"layer_{i}.png";
                            manifest.Layers.Add(new DptexLayerInfo
                            {
                                Id = $"layer_{i}",
                                Name = layer.Name,
                                Visible = layer.IsVisible,
                                Opacity = layer.Opacity,
                                BlendMode = layer.BlendMode.ToString(),
                                File = rootFileName
                            });
                            layerPngBytes.Add((rootFileName, png));
                        }
                    }

                    manifest.Materials[ctxData.MaterialKey] = matInfo;
                    matIndex++;
                }

                // If no context was marked active, populate root layers from layerManager.Layers
                if (manifest.Layers.Count == 0 && layerManager.Layers.Count > 0)
                {
                    for (int i = 0; i < layerManager.Layers.Count; i++)
                    {
                        var layer = layerManager.Layers[i];
                        string fileName = $"layer_{i}.png";
                        manifest.Layers.Add(new DptexLayerInfo
                        {
                            Id = $"layer_{i}",
                            Name = layer.Name,
                            Visible = layer.IsVisible,
                            Opacity = layer.Opacity,
                            BlendMode = layer.BlendMode.ToString(),
                            File = fileName
                        });
                        Image img = LayerGpuDataToImage(layer.GpuData, activeWidth, activeHeight);
                        byte[] png = img.SavePngToBuffer();
                        layerPngBytes.Add((fileName, png));
                    }
                }

                string manifestJson = JsonSerializer.Serialize(manifest, _jsonOptions);

                // 2. Create ZIP archive
                using (var fileStream = System.IO.File.Create(filePath))
                using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
                {
                    // 2a. Write manifest.json (strictly without BOM)
                    var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
                    using (var entryStream = manifestEntry.Open())
                    {
                        byte[] jsonBytes = Encoding.UTF8.GetBytes(manifestJson);
                        entryStream.Write(jsonBytes, 0, jsonBytes.Length);
                    }

                    // 2b. Write layer images
                    var writtenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (fileName, data) in layerPngBytes)
                    {
                        if (writtenFiles.Contains(fileName)) continue;
                        writtenFiles.Add(fileName);

                        var layerEntry = archive.CreateEntry(fileName, CompressionLevel.Fastest);
                        using (var entryStream = layerEntry.Open())
                        {
                            entryStream.Write(data, 0, data.Length);
                        }
                    }
                }

                result.Success = true;
                GD.Print($"[ProjectFileManager] Successfully saved project to: {filePath} ({manifest.Materials.Count} materials, {layerPngBytes.Count} textures)");
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
            HeroMeshHierarchy meshHierarchy = null,
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
                var layerImages = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

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

                // 3. Clear existing layers cleanly
                layerManager.ClearAllLayers();

                // Ensure atlas managers exist for all submeshes on the hero
                if (meshHierarchy != null)
                {
                    layerManager.EnsureAllSubmeshAtlases(meshHierarchy);
                }

                // 4. Restore materials
                bool hasMultiMaterials = manifest.Materials != null && manifest.Materials.Count > 0;
                string activeTargetSlot = manifest.TargetMeshSlot ?? "body";

                if (hasMultiMaterials)
                {
                    string primaryActiveKey = null;

                    // First pass: identify active key
                    foreach (var kvp in manifest.Materials)
                    {
                        var matInfo = kvp.Value;
                        if (string.Equals(matInfo.SlotName, activeTargetSlot, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(matInfo.MaterialKey, activeTargetSlot, StringComparison.OrdinalIgnoreCase))
                        {
                            primaryActiveKey = matInfo.MaterialKey;
                            break;
                        }
                    }

                    // Fallback to first material if no match
                    if (string.IsNullOrEmpty(primaryActiveKey))
                    {
                        foreach (var kvp in manifest.Materials)
                        {
                            primaryActiveKey = kvp.Key;
                            break;
                        }
                    }

                    // Second pass: restore each material context
                    foreach (var kvp in manifest.Materials)
                    {
                        var matInfo = kvp.Value;
                        int mW = (matInfo.CanvasResolution != null && matInfo.CanvasResolution.Length >= 2) ? matInfo.CanvasResolution[0] : 2048;
                        int mH = (matInfo.CanvasResolution != null && matInfo.CanvasResolution.Length >= 2) ? matInfo.CanvasResolution[1] : 2048;
                        Vector2I res = new Vector2I(mW, mH);

                        var restoredLayers = new List<SkinLayer>();
                        for (int i = 0; i < matInfo.Layers.Count; i++)
                        {
                            var info = matInfo.Layers[i];
                            var layer = new SkinLayer();
                            string lName = info.Name ?? $"Layer {i + 1}";
                            layer.Initialize(lName, res, isLocked: false, null, null, null);

                            if (Enum.TryParse<LayerBlendMode>(info.BlendMode, true, out var bm)) layer.SetBlendMode(bm);
                            else layer.SetBlendMode(LayerBlendMode.Normal);
                            layer.SetOpacity(info.Opacity);
                            layer.SetVisibility(info.Visible);

                            byte[] pngData = null;
                            if (!string.IsNullOrEmpty(info.File))
                            {
                                if (!layerImages.TryGetValue(info.File, out pngData))
                                {
                                    string baseName = Path.GetFileName(info.File);
                                    layerImages.TryGetValue(baseName, out pngData);
                                }
                            }

                            if (pngData != null && pngData.Length > 0)
                            {
                                var img = new Image();
                                if (img.LoadPngFromBuffer(pngData) == Error.Ok)
                                {
                                    layer.GpuData = ImageToLayerGpuData(img, mW, mH);
                                }
                                else
                                {
                                    layer.GpuData = new byte[mW * mH * 8];
                                }
                            }
                            else
                            {
                                layer.GpuData = new byte[mW * mH * 8];
                            }

                            restoredLayers.Add(layer);
                        }

                        bool isActive = string.Equals(matInfo.MaterialKey, primaryActiveKey, StringComparison.OrdinalIgnoreCase);
                        layerManager.RestoreMaterialContext(matInfo.MaterialKey, res, restoredLayers, matInfo.ActiveLayerIndex, isActive);
                    }
                }
                else
                {
                    // Version 1 backwards compatibility: load root layers
                    int targetW = (manifest.CanvasResolution != null && manifest.CanvasResolution.Length >= 2) ? manifest.CanvasResolution[0] : 2048;
                    int targetH = (manifest.CanvasResolution != null && manifest.CanvasResolution.Length >= 2) ? manifest.CanvasResolution[1] : 2048;
                    Vector2I res = new Vector2I(targetW, targetH);

                    var restoredLayers = new List<SkinLayer>();
                    if (manifest.Layers == null || manifest.Layers.Count == 0)
                    {
                        var defaultLayer = new SkinLayer();
                        defaultLayer.Initialize("Paint Layer 1", res, isLocked: false, null, null, null);
                        defaultLayer.GpuData = new byte[targetW * targetH * 8];
                        restoredLayers.Add(defaultLayer);
                    }
                    else
                    {
                        for (int i = 0; i < manifest.Layers.Count; i++)
                        {
                            var info = manifest.Layers[i];
                            var layer = new SkinLayer();
                            string lName = info.Name ?? $"Layer {i + 1}";
                            layer.Initialize(lName, res, isLocked: false, null, null, null);

                            if (Enum.TryParse<LayerBlendMode>(info.BlendMode, true, out var bm)) layer.SetBlendMode(bm);
                            else layer.SetBlendMode(LayerBlendMode.Normal);
                            layer.SetOpacity(info.Opacity);
                            layer.SetVisibility(info.Visible);

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
                                if (img.LoadPngFromBuffer(pngData) == Error.Ok)
                                {
                                    layer.GpuData = ImageToLayerGpuData(img, targetW, targetH);
                                }
                                else
                                {
                                    layer.GpuData = new byte[targetW * targetH * 8];
                                }
                            }
                            else
                            {
                                layer.GpuData = new byte[targetW * targetH * 8];
                            }

                            restoredLayers.Add(layer);
                        }
                    }

                    string primaryKey = layerManager.ActiveMaterialKey ?? activeTargetSlot;
                    layerManager.RestoreMaterialContext(primaryKey, res, restoredLayers, manifest.ActiveLayerIndex, true);
                }

                // 5. Select active layer and apply overlay parameters
                layerManager.ApplyOverlayParametersToMeshes();
                layerManager.RecordInitialSnapshot();
                layerManager.NotifyStackChanged();

                // 6. Restore color palette history array
                if (paletteUI != null && manifest.Palette != null && manifest.Palette.Count > 0)
                {
                    paletteUI.SetPaletteHistory(manifest.Palette);
                }

                result.Success = true;
                GD.Print($"[ProjectFileManager] Successfully loaded project from: {filePath} ({manifest.Materials?.Count ?? 1} materials restored)");
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
            if (gpuData == null || gpuData.Length == 0 || width <= 0 || height <= 0)
            {
                return Image.CreateEmpty(Math.Max(1, width), Math.Max(1, height), false, Image.Format.Rgba8);
            }

            int pixelCount = width * height;
            byte[] dstData = new byte[pixelCount * 4];

            unsafe
            {
                fixed (byte* pSrc = gpuData, pDst = dstData)
                {
                    Half* hSrc = (Half*)pSrc;
                    int maxPixels = Math.Min(pixelCount, gpuData.Length / 8);

                    for (int i = 0; i < maxPixels; i++)
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

            return Image.CreateFromData(width, height, false, Image.Format.Rgba8, dstData);
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
