using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ValveKeyValue;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;
using Godot;
using VrfResource = ValveResourceFormat.Resource;

namespace DeadlockPlayground.Particles;

/// <summary>
/// Represents a parsed .vpcf particle definition and its hierarchy of child systems.
/// </summary>
public class ParticleSystemNode
{
    public string VpcfPath { get; set; }
    public int MaxParticles { get; set; } = 64;
    public float ConstantRadius { get; set; } = 1.0f;
    public float Lifetime { get; set; } = 0.0f;
    public Color ConstantColor { get; set; } = Colors.White;
    public Vector3 CP0Offset { get; set; } = Vector3.Zero;
    public string CP0AttachmentName { get; set; } = string.Empty;
    public bool HasCP0Offset { get; set; } = false;

    // --- VRF Operator & Renderer Parameters ---
    public bool IsEndCap { get; set; } = false;
    public float Delay { get; set; } = 0.0f;
    public string RendererClass { get; set; } = "C_OP_RenderSprites";
    public string OutputBlendMode { get; set; } = "PARTICLE_OUTPUT_BLEND_MODE_ALPHA";
    public float TrailLengthScale { get; set; } = 1.0f;
    public float TrailMaxLength { get; set; } = 2000.0f;
    public float TrailMinLength { get; set; } = 0.0f;

    // --- RingWave Parameters (C_INIT_RingWave) ---
    public bool IsRingWave { get; set; } = false;
    public float RingRadius { get; set; } = 5.0f; // inches
    public float RingSpeedMin { get; set; } = 0.0f;
    public float RingSpeedMax { get; set; } = 0.0f;
    public float RingPitch { get; set; } = 0.0f;
    public bool RingXYVelocityOnly { get; set; } = false;

    // --- Texture Controls & UV Flipbook Parameters ---
    public Vector4 UVTransform { get; set; } = new Vector4(1f, 1f, 0f, 0f); // ScaleU, ScaleV, OffsetU, OffsetV
    public Vector4 UVRect { get; set; } = new Vector4(0f, 0f, 1f, 1f); // MinX, MinY, MaxX, MaxY
    public int ConstantSequenceNumber { get; set; } = 0;
    public bool HasSequenceNumber { get; set; } = false;

    // --- Model Renderer Parameters (C_OP_RenderModels) ---
    public bool IsModelRenderer { get; set; } = false;
    public string ModelPath { get; set; } = string.Empty;
    public string OverrideMaterialPath { get; set; } = string.Empty;

    public List<string> ChildVpcfPaths { get; } = new();
    public List<string> ReferencedTexturePaths { get; } = new();
    public List<string> ReferencedMaterialPaths { get; } = new();
    public List<ParticleSystemNode> Children { get; } = new();
}

/// <summary>
/// Recursively parses compiled Source 2 particle systems (.vpcf_c), resolving child emitters
/// and extracting referenced .vmat_c materials and .vtex_c textures.
/// </summary>
public static class VpcfHierarchyLoader
{
    public static ParticleSystemNode LoadHierarchy(string vpcfPath, IFileLoader fileLoader, int maxDepth = 3)
    {
        if (string.IsNullOrEmpty(vpcfPath) || fileLoader == null) return null;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return LoadNodeInternal(vpcfPath, fileLoader, visited, currentDepth: 0, maxDepth);
    }

    private static ParticleSystemNode LoadNodeInternal(string vpcfPath, IFileLoader fileLoader, HashSet<string> visited, int currentDepth, int maxDepth)
    {
        if (currentDepth > maxDepth) return null;
        if (!visited.Add(vpcfPath)) return null;

        string searchPath = vpcfPath.Replace('\\', '/');
        if (!searchPath.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
        {
            searchPath += "_c";
        }

        VrfResource res = null;
        try
        {
            res = fileLoader.LoadFile(searchPath) ?? fileLoader.LoadFileCompiled(searchPath);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[VpcfHierarchyLoader] Error loading {searchPath}: {ex.Message}");
        }

        if (res == null)
        {
            return new ParticleSystemNode { VpcfPath = vpcfPath };
        }

        using (res)
        {
            if (res.DataBlock is not ParticleSystem ps || ps.Data == null)
            {
                return new ParticleSystemNode { VpcfPath = vpcfPath };
            }

            var node = new ParticleSystemNode
            {
                VpcfPath = vpcfPath
            };

            var data = ps.Data;

            // Extract basic properties
            if (data.TryGetValue("m_nMaxParticles", out var maxPartObj) && maxPartObj != null)
            {
                try { node.MaxParticles = (int)Convert.ToInt64(maxPartObj.ToString()); } catch { }
            }
            if (data.TryGetValue("m_flConstantRadius", out var radObj) && radObj != null)
            {
                try { node.ConstantRadius = (float)Convert.ToDouble(radObj.ToString()); } catch { }
            }

            // Extract ConstantColor [r, g, b, a]
            if (data.TryGetValue("m_ConstantColor", out var colObj) && colObj != null && colObj.IsArray)
            {
                try
                {
                    var items = colObj.Children.ToArray();
                    if (items.Length >= 3)
                    {
                        float r = (float)items[0].Value / 255f;
                        float g = (float)items[1].Value / 255f;
                        float b = (float)items[2].Value / 255f;
                        float a = items.Length > 3 ? (float)items[3].Value / 255f : 1f;
                        node.ConstantColor = new Color(r, g, b, a);
                    }
                }
                catch { }
            }

            // Extract Control Point 0 (CP0) Attachment & Offset from root m_drivers or nested m_controlPointConfigurations
            if (data.TryGetValue("m_drivers", out var rootDriversObj) && rootDriversObj != null)
            {
                ParseDrivers(rootDriversObj, node);
            }

            if (!node.HasCP0Offset && string.IsNullOrEmpty(node.CP0AttachmentName))
            {
                if (data.TryGetValue("m_controlPointConfigurations", out var cpConfigsObj) && cpConfigsObj != null)
                {
                    foreach (var configItem in cpConfigsObj.Children)
                    {
                        if (configItem.Value.TryGetValue("m_drivers", out var driversObj) && driversObj != null)
                        {
                            ParseDrivers(driversObj, node);
                        }
                        if (node.HasCP0Offset || !string.IsNullOrEmpty(node.CP0AttachmentName)) break;
                    }
                }
            }

            // Extract Initializers (C_INIT_RingWave, sequences, etc.)
            if (data.TryGetValue("m_Initializers", out var initsObj) && initsObj != null)
            {
                foreach (var initItem in initsObj.Children)
                {
                    var initVal = initItem.Value;
                    if (initVal == null) continue;

                    string cls = initVal.TryGetValue("_class", out var clsObj) ? clsObj?.ToString() ?? "" : "";
                    if (cls.Equals("C_INIT_RingWave", StringComparison.OrdinalIgnoreCase))
                    {
                        node.IsRingWave = true;
                        node.RingRadius = ExtractFloat(initVal, "m_flInitialRadius", 5.0f);
                        node.RingSpeedMin = ExtractFloat(initVal, "m_flInitialSpeedMin", 0.0f);
                        node.RingSpeedMax = ExtractFloat(initVal, "m_flInitialSpeedMax", 0.0f);
                        node.RingPitch = ExtractFloat(initVal, "m_flPitch", 0.0f);
                        if (initVal.TryGetValue("m_bXYVelocityOnly", out var xyObj) && xyObj != null)
                        {
                            node.RingXYVelocityOnly = xyObj.ToString().Equals("1") || xyObj.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    else if (cls.Equals("C_INIT_InitFloat", StringComparison.OrdinalIgnoreCase))
                    {
                        if (initVal.TryGetValue("m_nOutputField", out var outFieldObj) && outFieldObj != null)
                        {
                            try
                            {
                                int fieldIdx = Convert.ToInt32(outFieldObj.ToString());
                                if (fieldIdx == 1) // Lifetime
                                {
                                    node.Lifetime = ExtractFloat(initVal, "m_InputValue", 1.0f);
                                }
                                else if (fieldIdx == 9 || fieldIdx == 13) // Sequence number field
                                {
                                    node.ConstantSequenceNumber = (int)ExtractFloat(initVal, "m_InputValue", 0f);
                                    node.HasSequenceNumber = true;
                                }
                            }
                            catch { }
                        }
                    }
                    else if (cls.Equals("C_INIT_RandomColor", StringComparison.OrdinalIgnoreCase))
                    {
                        if (initVal.TryGetValue("m_ColorMin", out var cMinObj) && cMinObj != null && cMinObj.IsArray)
                        {
                            try
                            {
                                var items = cMinObj.Children.ToArray();
                                if (items.Length >= 3)
                                {
                                    float r = (float)items[0].Value / 255f;
                                    float g = (float)items[1].Value / 255f;
                                    float b = (float)items[2].Value / 255f;
                                    node.ConstantColor = new Color(r, g, b, 1.0f);
                                }
                            }
                            catch { }
                        }
                    }
                }
            }

            // Extract Renderers, blend modes, trail parameters, models, and texture controls
            if (data.TryGetValue("m_Renderers", out var renderersObj) && renderersObj != null)
            {
                foreach (var rendererItem in renderersObj.Children)
                {
                    var rVal = rendererItem.Value;
                    if (rVal != null)
                    {
                        if (rVal.TryGetValue("_class", out var clsObj) && clsObj != null)
                        {
                            node.RendererClass = clsObj.ToString();
                        }
                        if (rVal.TryGetValue("m_nOutputBlendMode", out var blendObj) && blendObj != null)
                        {
                            node.OutputBlendMode = blendObj.ToString();
                        }
                        if (rVal.TryGetValue("m_flLengthScale", out var lenScaleObj) && lenScaleObj != null)
                        {
                            try { node.TrailLengthScale = Convert.ToSingle(lenScaleObj.ToString()); } catch { }
                        }
                        if (rVal.TryGetValue("m_flMaxLength", out var maxLenObj) && maxLenObj != null)
                        {
                            try { node.TrailMaxLength = Convert.ToSingle(maxLenObj.ToString()); } catch { }
                        }
                        if (rVal.TryGetValue("m_flMinLength", out var minLenObj) && minLenObj != null)
                        {
                            try { node.TrailMinLength = Convert.ToSingle(minLenObj.ToString()); } catch { }
                        }

                        // C_OP_RenderModels
                        if (node.RendererClass.Equals("C_OP_RenderModels", StringComparison.OrdinalIgnoreCase))
                        {
                            node.IsModelRenderer = true;
                            if (rVal.TryGetValue("m_ModelList", out var modelListObj) && modelListObj != null)
                            {
                                foreach (var mItem in modelListObj.Children)
                                {
                                    if (mItem.Value.TryGetValue("m_model", out var mObj) && mObj != null)
                                    {
                                        node.ModelPath = mObj.ToString();
                                        break;
                                    }
                                }
                            }
                            if (rVal.TryGetValue("m_hOverrideMaterial", out var overMatObj) && overMatObj != null)
                            {
                                node.OverrideMaterialPath = overMatObj.ToString();
                            }
                        }

                        // Texture controls in m_vecTexturesInput (m_flFinalTextureScaleV, m_flFinalTextureOffsetV, etc.)
                        if (rVal.TryGetValue("m_vecTexturesInput", out var texInObj) && texInObj != null)
                        {
                            foreach (var ti in texInObj.Children)
                            {
                                if (ti.Value.TryGetValue("m_TextureControls", out var tc) && tc != null)
                                {
                                    float scaleU = ExtractFloat(tc, "m_flFinalTextureScaleU", 1.0f);
                                    float scaleV = ExtractFloat(tc, "m_flFinalTextureScaleV", 1.0f);
                                    float offsetU = ExtractFloat(tc, "m_flFinalTextureOffsetU", 0.0f);
                                    float offsetV = ExtractFloat(tc, "m_flFinalTextureOffsetV", 0.0f);
                                    node.UVTransform = new Vector4(scaleU, scaleV, offsetU, offsetV);
                                }
                            }
                        }

                        // Renderer constant sequence number
                        if (rVal.TryGetValue("m_nConstantSequenceNumber", out var cSeqObj) && cSeqObj != null)
                        {
                            try
                            {
                                node.ConstantSequenceNumber = Convert.ToInt32(cSeqObj.ToString());
                                node.HasSequenceNumber = true;
                            }
                            catch { }
                        }

                        ScanForTexturesAndMaterials(rVal, node);
                    }
                }
            }

            // If textures weren't found directly but materials were, resolve materials via fileLoader to get their textures
            if (node.ReferencedTexturePaths.Count == 0 && node.ReferencedMaterialPaths.Count > 0 && fileLoader != null)
            {
                foreach (var matPath in node.ReferencedMaterialPaths)
                {
                    string searchMat = matPath.Replace('\\', '/');
                    if (!searchMat.EndsWith("_c", StringComparison.OrdinalIgnoreCase)) searchMat += "_c";

                    try
                    {
                        using var matRes = fileLoader.LoadFile(searchMat) ?? fileLoader.LoadFileCompiled(searchMat);
                        if (matRes != null)
                        {
                            if (matRes.DataBlock is ValveResourceFormat.ResourceTypes.Material vmat)
                            {
                                foreach (var tp in vmat.TextureParams)
                                {
                                    if (tp.Key.Contains("Color", StringComparison.OrdinalIgnoreCase) ||
                                        tp.Key.Equals("Texture", StringComparison.OrdinalIgnoreCase) ||
                                        node.ReferencedTexturePaths.Count == 0)
                                    {
                                        string tPath = tp.Value;
                                        if (tPath.EndsWith("_c", StringComparison.OrdinalIgnoreCase)) tPath = tPath[..^2];
                                        if (!node.ReferencedTexturePaths.Contains(tPath))
                                        {
                                            node.ReferencedTexturePaths.Add(tPath);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch { }

                    if (node.ReferencedTexturePaths.Count > 0) break;
                }
            }

            // Extract Spritesheet / Sequence sub-rect from .vtex_c if available
            if (node.ReferencedTexturePaths.Count > 0 && fileLoader != null)
            {
                string firstTex = node.ReferencedTexturePaths[0].Replace('\\', '/');
                if (!firstTex.EndsWith("_c", StringComparison.OrdinalIgnoreCase)) firstTex += "_c";
                try
                {
                    using var tRes = fileLoader.LoadFile(firstTex) ?? fileLoader.LoadFileCompiled(firstTex);
                    if (tRes?.DataBlock is ValveResourceFormat.ResourceTypes.Texture vTex)
                    {
                        var sheet = vTex.GetSpriteSheetData();
                        if (sheet != null && sheet.Sequences.Length > 0)
                        {
                            int seqIdx = Mathf.Clamp(node.ConstantSequenceNumber, 0, sheet.Sequences.Length - 1);
                            var seq = sheet.Sequences[seqIdx];
                            if (seq.Frames.Length > 0 && seq.Frames[0].Images.Length > 0)
                            {
                                var img = seq.Frames[0].Images[0];
                                node.UVRect = new Vector4(img.UncroppedMin.X, img.UncroppedMin.Y, img.UncroppedMax.X, img.UncroppedMax.Y);
                            }
                        }
                    }
                }
                catch { }
            }

            // Extract Children (m_Children) including EndCap and Delay evaluation
            if (data.TryGetValue("m_Children", out var childrenObj) && childrenObj != null)
            {
                foreach (var childItem in childrenObj.Children)
                {
                    var cVal = childItem.Value;
                    string childRef = null;
                    if (cVal.TryGetValue("m_ChildRef", out var refObj) && refObj != null)
                    {
                        childRef = refObj.ToString();
                    }

                    if (!string.IsNullOrEmpty(childRef))
                    {
                        node.ChildVpcfPaths.Add(childRef);

                        bool isEndCap = false;
                        if (cVal.TryGetValue("m_bEndCap", out var endCapObj) && endCapObj != null)
                        {
                            isEndCap = endCapObj.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
                        }

                        float delay = 0.0f;
                        if (cVal.TryGetValue("m_flDelay", out var delayObj) && delayObj != null)
                        {
                            try { delay = Convert.ToSingle(delayObj.ToString()); } catch { }
                        }

                        // Recurse to load child system
                        var childNode = LoadNodeInternal(childRef, fileLoader, visited, currentDepth + 1, maxDepth);
                        if (childNode != null)
                        {
                            childNode.IsEndCap = isEndCap;
                            childNode.Delay = delay;

                            // Inherit CP0 socket and offset from parent if not overridden
                            if (string.IsNullOrEmpty(childNode.CP0AttachmentName) && !string.IsNullOrEmpty(node.CP0AttachmentName))
                            {
                                childNode.CP0AttachmentName = node.CP0AttachmentName;
                            }
                            if (!childNode.HasCP0Offset && node.HasCP0Offset)
                            {
                                childNode.CP0Offset = node.CP0Offset;
                                childNode.HasCP0Offset = true;
                            }

                            node.Children.Add(childNode);
                        }
                    }
                }
            }

            return node;
        }
    }

    private static void ParseDrivers(KVObject driversObj, ParticleSystemNode node)
    {
        if (driversObj == null) return;

        foreach (var driverItem in driversObj.Children)
        {
            var driver = driverItem.Value;
            if (driver == null) continue;

            int cpIdx = 0;
            if (driver.TryGetValue("m_iControlPoint", out var cpIdxObj) && cpIdxObj != null)
            {
                try { cpIdx = Convert.ToInt32(cpIdxObj.ToString()); } catch { }
            }

            if (cpIdx == 0)
            {
                if (driver.TryGetValue("m_attachmentName", out var attachObj) && attachObj != null)
                {
                    node.CP0AttachmentName = attachObj.ToString()?.Trim() ?? string.Empty;
                }

                if (driver.TryGetValue("m_vecOffset", out var offsetObj) && offsetObj != null && offsetObj.IsArray)
                {
                    try
                    {
                        var vals = offsetObj.Children.ToArray();
                        if (vals.Length >= 3)
                        {
                            float sx = Convert.ToSingle(vals[0].Value.ToString());
                            float sy = Convert.ToSingle(vals[1].Value.ToString());
                            float sz = Convert.ToSingle(vals[2].Value.ToString());
                            // Source 2 (Z-up, inches) to Godot (Y-up, meters)
                            const float inchesToMeters = 0.0254f;
                            node.CP0Offset = new Vector3(sx * inchesToMeters, sz * inchesToMeters, -sy * inchesToMeters);
                            node.HasCP0Offset = true;
                        }
                    }
                    catch { }
                }
                break;
            }
        }
    }

    private static void ScanForTexturesAndMaterials(KVObject obj, ParticleSystemNode node)
    {
        if (obj == null || obj.IsNull) return;

        foreach (var c in obj.Children)
        {
            if (c.Value.Count > 0)
            {
                ScanForTexturesAndMaterials(c.Value, node);
            }
            else
            {
                string s = c.Value.ToString() ?? "";
                if (s.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".vtex_c", StringComparison.OrdinalIgnoreCase))
                {
                    string cleanVtex = s.EndsWith("_c") ? s.Substring(0, s.Length - 2) : s;
                    if (!node.ReferencedTexturePaths.Contains(cleanVtex))
                    {
                        node.ReferencedTexturePaths.Add(cleanVtex);
                    }
                }
                else if (s.EndsWith(".vmat", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".vmat_c", StringComparison.OrdinalIgnoreCase))
                {
                    string cleanVmat = s.EndsWith("_c") ? s.Substring(0, s.Length - 2) : s;
                    if (!node.ReferencedMaterialPaths.Contains(cleanVmat))
                    {
                        node.ReferencedMaterialPaths.Add(cleanVmat);
                    }
                }
            }
        }
    }

    private static float ExtractFloat(KVObject kv, string key, float fallback = 0f)
    {
        if (kv == null || !kv.TryGetValue(key, out var val) || val == null) return fallback;
        return ParseFloatValue(val, fallback);
    }

    private static float ParseFloatValue(KVObject val, float fallback = 0f)
    {
        if (val == null) return fallback;
        if (val.TryGetValue("m_flLiteralValue", out var lit) && lit != null)
        {
            try { return Convert.ToSingle(lit.ToString()); } catch { }
        }
        if (val.TryGetValue("m_flRandomMin", out var rMin) && rMin != null)
        {
            try { return Convert.ToSingle(rMin.ToString()); } catch { }
        }
        try { return Convert.ToSingle(val.ToString()); } catch { }
        return fallback;
    }

    /// <summary>
    /// Flattens all unique texture paths referenced across the entire system and its children.
    /// </summary>
    public static List<string> GetAllTextures(ParticleSystemNode root)
    {
        var result = new List<string>();
        if (root == null) return result;

        void Collect(ParticleSystemNode node)
        {
            foreach (var t in node.ReferencedTexturePaths)
            {
                if (!result.Contains(t)) result.Add(t);
            }
            foreach (var child in node.Children)
            {
                Collect(child);
            }
        }

        Collect(root);
        return result;
    }
}
