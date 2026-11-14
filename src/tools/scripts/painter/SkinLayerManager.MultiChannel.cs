using System;
using System.Collections.Generic;
using Godot;
using ValvePak;
using DeadlockPlayground.Materials;

namespace DeadlockPlayground.Painter;

public partial class SkinLayerManager : Node
{
    public MaterialTextureChannel ActiveTextureChannel { get; private set; } = MaterialTextureChannel.Color;
    public Package ActiveVpkPackage { get; set; }
    public Package ActiveAddonPackage { get; set; }

    public event Action<MaterialTextureChannel> ActiveChannelChanged;
    public event Action<MaterialChannelSet> ChannelSetUpdated;

    public string ActiveBaseMaterialKey => StripChannelSuffix(_activeMaterialKey);

    public static string StripChannelSuffix(string key)
    {
        if (string.IsNullOrEmpty(key)) return key;
        int idx = key.IndexOf("::ch:", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? key.Substring(0, idx) : key;
    }

    public static string GetChannelContextKey(string baseMatKey, MaterialTextureChannel channel)
    {
        string cleanBase = StripChannelSuffix(baseMatKey);
        if (string.IsNullOrEmpty(cleanBase) || channel == MaterialTextureChannel.Color)
        {
            return cleanBase;
        }
        return $"{cleanBase}::ch:{channel}";
    }

    public MaterialChannelSet GetActiveChannelSet()
    {
        return GetChannelSetForMesh(_targetMesh, ActiveBaseMaterialKey);
    }

    public MaterialChannelSet GetChannelSetForMesh(MeshInstance3D mesh, string baseMatKey = null)
    {
        if (MeshHierarchy != null)
        {
            if (mesh != null)
            {
                var sub = MeshHierarchy.FindSubmesh(mesh);
                if (sub?.Channels != null) return sub.Channels;
            }
            if (!string.IsNullOrEmpty(baseMatKey))
            {
                string cleanBase = StripChannelSuffix(baseMatKey);
                foreach (var g in MeshHierarchy.MaterialGroups)
                {
                    if (string.Equals(g.MaterialKey, cleanBase, StringComparison.OrdinalIgnoreCase))
                    {
                        if (g.Channels != null) return g.Channels;
                        foreach (var s in g.Submeshes)
                        {
                            if (s.Channels != null)
                            {
                                g.Channels = s.Channels;
                                return s.Channels;
                            }
                        }
                    }
                }
            }
        }

        if (mesh != null && GodotObject.IsInstanceValid(mesh))
        {
            var mat = HeroMeshHierarchy.GetAuthenticMaterial(mesh, 0)
                      ?? mesh.GetSurfaceOverrideMaterial(0)
                      ?? (mesh.Mesh != null && mesh.Mesh.GetSurfaceCount() > 0 ? mesh.Mesh.SurfaceGetMaterial(0) : null)
                      ?? mesh.MaterialOverride;
            var attached = MaterialChannelDiscovery.TryGetChannelSet(mat);
            if (attached != null) return attached;
        }
        return null;
    }

    private void SaveCurrentContextSnapshot(string ctxKey)
    {
        if (string.IsNullOrEmpty(ctxKey) || _currentContext == null) return;
        _currentContext.Layers = new List<SkinLayer>(_layers);
        _currentContext.ActiveLayerIndex = _activeLayerIndex;
        _currentContext.BaseAtlasBuffer = _baseAtlasBuffer;
        _currentContext.CanvasSize = CanvasSize;
        _currentContext.UndoStack = new List<LayerPaintSnapshot>(_undoStack);
        _currentContext.RedoStack = new List<LayerPaintSnapshot>(_redoStack);
        _materialContexts[ctxKey] = _currentContext;
    }

    /// <summary>
    /// Switches the active paintable / inspectable texture channel (Color, AO, Normal, Roughness, Shadow, etc.)
    /// for the currently active material group while preserving per-channel layer stacks.
    /// </summary>
    public void SetActiveTextureChannel(MaterialTextureChannel channel)
    {
        if (_targetMesh == null || !GodotObject.IsInstanceValid(_targetMesh)) return;
        string baseKey = !string.IsNullOrEmpty(_activeMaterialKey)
            ? StripChannelSuffix(_activeMaterialKey)
            : GetMaterialKey(_targetMesh);
        if (string.IsNullOrEmpty(baseKey)) return;

        var chSet = GetChannelSetForMesh(_targetMesh, baseKey);
        if (chSet == null) return;

        // Flush any unsaved non-color strokes OR cache color composite before switching away
        SyncActiveChannelCompositeToSet(forceBakeColorCache: ActiveTextureChannel == MaterialTextureChannel.Color);

        string prevCtxKey = GetChannelContextKey(baseKey, ActiveTextureChannel);
        SaveCurrentContextSnapshot(prevCtxKey);

        ActiveTextureChannel = channel;
        chSet.ActiveChannel = channel;
        if (chSet.SoloPreviewChannel.HasValue)
        {
            chSet.SoloPreviewChannel = channel;
        }

        string nextCtxKey = GetChannelContextKey(baseKey, channel);
        _activeMaterialKey = nextCtxKey;

        int w = CanvasSize.X > 0 ? CanvasSize.X : 2048;
        int h = CanvasSize.Y > 0 ? CanvasSize.Y : 2048;

        var chEntry = chSet.GetChannel(channel);
        Texture2D chBaseTex = chEntry?.Texture;

        if (_materialContexts.TryGetValue(nextCtxKey, out var nextCtx) && nextCtx != null && nextCtx.Layers.Count > 0)
        {
            _currentContext = nextCtx;
            _layers.Clear();
            _layers.AddRange(nextCtx.Layers);
            _activeLayerIndex = Mathf.Clamp(nextCtx.ActiveLayerIndex, 0, Math.Max(0, _layers.Count - 1));
            _undoStack.Clear();
            _undoStack.AddRange(nextCtx.UndoStack);
            _redoStack.Clear();
            _redoStack.AddRange(nextCtx.RedoStack);
            _baseAtlasBuffer = nextCtx.BaseAtlasBuffer;
            _hasPopulatedBaseAtlasBuffer = _baseAtlasBuffer != null && _baseAtlasBuffer.Length == w * h * 8;
        }
        else
        {
            _layers.Clear();
            _undoStack.Clear();
            _redoStack.Clear();
            _baseAtlasBuffer = null;
            _hasPopulatedBaseAtlasBuffer = false;

            EnsureShadersLoaded();
            var defaultLayer = new SkinLayer();
            defaultLayer.Initialize($"{MaterialChannelSet.GetDisplayLabel(channel)} Paint 1", CanvasSize, false, _compositeShader, _dabShader, null);
            _layers.Add(defaultLayer);
            _activeLayerIndex = 0;

            nextCtx = new MaterialPaintingContext
            {
                MaterialKey = nextCtxKey,
                CanvasSize = CanvasSize,
                Layers = new List<SkinLayer>(_layers),
                ActiveLayerIndex = 0,
                HasPaintStrokes = chEntry?.IsDirty ?? false
            };
            _materialContexts[nextCtxKey] = nextCtx;
            _currentContext = nextCtx;
        }

        // Update base texture layer & re-upload to active GPU atlas manager
        _standaloneBaseTexture = chBaseTex;
        if (_baseLayer != null)
        {
            _baseLayer.Texture = chBaseTex as ImageTexture ?? (chBaseTex != null ? ImageTexture.CreateFromImage(chBaseTex.GetImage()) : null);
            _baseLayer.Name = channel == MaterialTextureChannel.Color
                ? "Base Texture"
                : $"{MaterialChannelSet.GetDisplayLabel(channel)} Map";
        }
        if (!_hasPopulatedBaseAtlasBuffer)
        {
            RebuildBaseAtlasBuffer();
        }
        else
        {
            SyncBaseTextureToGpu();
        }

        UploadLayersToActiveAtlasManager();
        _otherLayersDirty = true;
        RecompositeGpuLayers();
        ApplyOverlayParametersToMeshes();
        SyncChannelsToTargetMaterials(baseKey);

        NotifyStackChanged();
        NotifyLayerSelected(_activeLayerIndex);
        ActiveChannelChanged?.Invoke(channel);
        ChannelSetUpdated?.Invoke(chSet);
    }

    private void UploadLayersToActiveAtlasManager()
    {
        if (_activeAtlasManager == null || !GodotObject.IsInstanceValid(_activeAtlasManager)) return;
        var rd = RenderingServer.GetRenderingDevice();
        if (rd == null) return;

        int bufferLen = Math.Max(1, CanvasSize.X) * Math.Max(1, CanvasSize.Y) * 8;
        var layerList = new Godot.Collections.Array<GodotObject>();
        foreach (var l in _layers)
        {
            if ((!l.LayerRid.IsValid || !rd.TextureIsValid(l.LayerRid)) && l.GpuData != null && l.GpuData.Length == bufferLen)
            {
                var fmt = new RDTextureFormat
                {
                    Width = (uint)CanvasSize.X,
                    Height = (uint)CanvasSize.Y,
                    Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
                    UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
                                RenderingDevice.TextureUsageBits.StorageBit |
                                RenderingDevice.TextureUsageBits.CanCopyFromBit |
                                RenderingDevice.TextureUsageBits.CanUpdateBit
                };
                l.LayerRid = rd.TextureCreate(fmt, new RDTextureView(), new Godot.Collections.Array<byte[]> { l.GpuData });
            }
            if (l.LayerRid.IsValid && rd.TextureIsValid(l.LayerRid) && l.GpuData != null && l.GpuData.Length == bufferLen && l.IsCpuSynced)
            {
                rd.TextureUpdate(l.LayerRid, 0, l.GpuData);
            }
            layerList.Add(l);
        }

        _activeAtlasManager.Set("layers", layerList);
        if (_activeLayerIndex >= 0 && _activeLayerIndex < _layers.Count)
        {
            var act = _layers[_activeLayerIndex];
            _activeAtlasManager.Set("atlas_texture_rid", act.LayerRid);
            var resObj = _activeAtlasManager.Get("active_layer_resource");
            if (resObj.VariantType == Variant.Type.Object && resObj.AsGodotObject() is Texture2Drd texRd && act.LayerRid.IsValid)
            {
                texRd.TextureRdRid = act.LayerRid;
            }
        }
    }

    public void SetChannelEnabled(MaterialTextureChannel channel, bool enabled)
    {
        var chSet = GetActiveChannelSet();
        var entry = chSet?.GetChannel(channel);
        if (entry == null) return;

        entry.IsEnabled = enabled;
        SyncChannelsToTargetMaterials(ActiveBaseMaterialKey);
        ApplyOverlayParametersToMeshes();
        ChannelSetUpdated?.Invoke(chSet);
    }

    public void SetSoloChannelPreview(bool solo)
    {
        var chSet = GetActiveChannelSet();
        if (chSet == null) return;

        chSet.SoloPreviewChannel = solo ? ActiveTextureChannel : null;
        SyncChannelsToTargetMaterials(ActiveBaseMaterialKey);
        ApplyOverlayParametersToMeshes();
        ChannelSetUpdated?.Invoke(chSet);
    }

    public bool SelectColorVariant(int variantIndex)
    {
        var chSet = GetActiveChannelSet();
        if (chSet == null) return false;

        if (!chSet.SwitchColorVariant(ActiveVpkPackage, ActiveAddonPackage, variantIndex))
            return false;

        var colorTex = chSet.GetChannel(MaterialTextureChannel.Color)?.Texture;
        if (ActiveTextureChannel == MaterialTextureChannel.Color)
        {
            _standaloneBaseTexture = colorTex;
            if (_baseLayer != null) _baseLayer.Texture = colorTex as ImageTexture;
            RebuildBaseAtlasBuffer();
            RecompositeGpuLayers();
        }
        else if (_materialContexts.TryGetValue(GetChannelContextKey(ActiveBaseMaterialKey, MaterialTextureChannel.Color), out var colorCtx))
        {
            if (colorTex != null)
            {
                int w = CanvasSize.X > 0 ? CanvasSize.X : 2048;
                int h = CanvasSize.Y > 0 ? CanvasSize.Y : 2048;
                colorCtx.BaseAtlasBuffer = ExtractTextureToLinearHalfBuffer(colorTex, w, h);
            }
        }

        SyncChannelsToTargetMaterials(ActiveBaseMaterialKey);
        ApplyOverlayParametersToMeshes();
        NotifyStackChanged();
        ChannelSetUpdated?.Invoke(chSet);
        return true;
    }

    /// <summary>
    /// Imports an external PNG/TGA image file directly into the currently selected texture channel
    /// (Color, AO, Normal, Roughness, Shadow, etc.) and immediately updates 2D & 3D viewports.
    /// </summary>
    public bool ImportExternalImageToActiveChannel(Image loadedImg, string sourcePathOrName)
    {
        if (loadedImg == null || loadedImg.IsEmpty()) return false;
        var chSet = GetActiveChannelSet();
        if (chSet == null) return false;

        var ch = ActiveTextureChannel;
        var entry = chSet.GetChannel(ch);
        if (entry == null)
        {
            entry = new ChannelMapEntry
            {
                Channel = ch,
                DisplayLabel = MaterialChannelSet.GetDisplayLabel(ch),
                ShortBadge = MaterialChannelSet.GetShortBadge(ch),
                IsEnabled = true
            };
            chSet.SetChannel(ch, entry);
        }

        var tex = ImageTexture.CreateFromImage(loadedImg);
        tex.ResourceName = System.IO.Path.GetFileName(sourcePathOrName ?? "imported.png");

        entry.Texture = tex;
        entry.SourceImage = loadedImg;
        entry.SourcePath = sourcePathOrName ?? "Imported PNG";
        entry.IsPresent = true;
        entry.IsDefaultFallback = false;
        entry.IsDirty = true;

        _standaloneBaseTexture = tex;
        if (_baseLayer != null)
        {
            _baseLayer.Texture = tex;
        }
        RebuildBaseAtlasBuffer();
        RecompositeGpuLayers();
        SyncChannelsToTargetMaterials(ActiveBaseMaterialKey);
        ApplyOverlayParametersToMeshes();
        SetActiveContextHasPaintStrokes(true);
        NotifyStackChanged();
        ChannelSetUpdated?.Invoke(chSet);
        return true;
    }

    public bool ResetActiveChannel()
    {
        var chSet = GetActiveChannelSet();
        if (chSet == null) return false;

        var ch = ActiveTextureChannel;
        bool resetOk = chSet.ResetChannelToOriginal(ch);
        if (!resetOk) return false;

        var entry = chSet.GetChannel(ch);
        _standaloneBaseTexture = entry?.Texture;
        if (_baseLayer != null)
        {
            _baseLayer.Texture = entry?.Texture as ImageTexture;
        }

        foreach (var l in _layers)
        {
            if (l.GpuData != null) Array.Clear(l.GpuData, 0, l.GpuData.Length);
            l.IsCpuSynced = true;
        }
        UploadLayersToActiveAtlasManager();
        RebuildBaseAtlasBuffer();
        _otherLayersDirty = true;
        RecompositeGpuLayers();
        SyncChannelsToTargetMaterials(ActiveBaseMaterialKey);
        ApplyOverlayParametersToMeshes();
        if (entry != null) entry.IsDirty = false;
        SetActiveContextHasPaintStrokes(false);
        NotifyStackChanged();
        ChannelSetUpdated?.Invoke(chSet);
        return true;
    }

    private void SyncActiveChannelCompositeToSet(bool forceBakeColorCache = false)
    {
        var chSet = GetActiveChannelSet();
        if (chSet == null) return;

        bool isColor = ActiveTextureChannel == MaterialTextureChannel.Color;
        if (isColor && !forceBakeColorCache) return;

        bool hasPaint = HasPaintStrokes();
        var entry = chSet.GetChannel(ActiveTextureChannel);
        if (entry == null) return;

        if (!hasPaint && !entry.IsDirty && !isColor) return;

        int w = CanvasSize.X > 0 ? CanvasSize.X : 2048;
        int h = CanvasSize.Y > 0 ? CanvasSize.Y : 2048;
        bool srgb = entry.IsColorSpaceSrgb;
        Image baked = BakeCompositeImageFromLayers(_layers, w, h, srgb);
        if (baked != null && !baked.IsEmpty())
        {
            var tex = ImageTexture.CreateFromImage(baked);
            entry.Texture = tex;
            entry.SourceImage = baked;
            if (hasPaint) entry.IsDirty = true;
            if (hasPaint) entry.IsDefaultFallback = false;
            if (hasPaint) entry.IsPresent = true;
        }
    }

    private void OnMultiChannelPaintCommitted()
    {
        if (ActiveTextureChannel == MaterialTextureChannel.Color) return;
        SyncActiveChannelCompositeToSet();
        SyncChannelsToTargetMaterials(ActiveBaseMaterialKey);
        ApplyOverlayParametersToMeshes();
        ChannelSetUpdated?.Invoke(GetActiveChannelSet());
    }

    public void SyncChannelsToTargetMaterials(string baseMatKey = null)
    {
        baseMatKey ??= ActiveBaseMaterialKey;
        if (string.IsNullOrEmpty(baseMatKey)) return;

        var chSet = GetChannelSetForMesh(_targetMesh, baseMatKey);
        if (chSet == null) return;

        if (MeshHierarchy != null)
        {
            foreach (var sub in MeshHierarchy.Submeshes)
            {
                if (sub?.Mesh == null || !GodotObject.IsInstanceValid(sub.Mesh)) continue;
                string subKey = GetMaterialKey(sub.Mesh, sub.SurfaceIndex);
                if (!string.Equals(subKey, baseMatKey, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(sub.OriginalVmatPath, baseMatKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (sub.OriginalMaterial != null)
                    MaterialChannelDiscovery.ApplyChannelsToMaterial(sub.OriginalMaterial, chSet);
                if (sub.ToonMaterial != null)
                    MaterialChannelDiscovery.ApplyChannelsToMaterial(sub.ToonMaterial, chSet);
                if (sub.Mesh.MaterialOverlay != null)
                    MaterialChannelDiscovery.ApplyChannelsToMaterial(sub.Mesh.MaterialOverlay, chSet, isPainterOverlay: true);
            }
        }
    }

    private void ApplyMultiChannelOverlayUniforms(ShaderMaterial mat, MeshInstance3D mesh, Material baseMat, string baseMatKey)
    {
        if (mat == null) return;
        var chSet = GetChannelSetForMesh(mesh, baseMatKey);
        if (chSet == null) return;

        MaterialChannelDiscovery.ApplyChannelsToMaterial(mat, chSet, isPainterOverlay: true);

        bool editingOtherChannel = ActiveTextureChannel != MaterialTextureChannel.Color &&
                                   string.Equals(StripChannelSuffix(_activeMaterialKey), StripChannelSuffix(baseMatKey), StringComparison.OrdinalIgnoreCase);

        if (editingOtherChannel)
        {
            var colorTex = chSet.GetChannel(MaterialTextureChannel.Color)?.Texture ?? ExtractMeshBaseTexture(mesh);
            mat.SetShaderParameter("use_channel_color_override", colorTex != null);
            if (colorTex != null)
                mat.SetShaderParameter("channel_color_override_texture", colorTex);
        }
        else
        {
            mat.SetShaderParameter("use_channel_color_override", false);
        }
    }

    public bool HasChannelEdits(SubmeshNodeInfo submesh, MaterialTextureChannel channel)
    {
        if (submesh == null) return false;
        if (channel == MaterialTextureChannel.Color)
        {
            string baseKey = !string.IsNullOrEmpty(submesh.OriginalVmatPath)
                ? submesh.OriginalVmatPath.ToLowerInvariant().Trim()
                : GetMaterialKey(submesh.Mesh, submesh.SurfaceIndex);
            if (HasPaintStrokes(baseKey)) return true;
        }

        var entry = submesh.Channels?.GetChannel(channel);
        if (entry != null && entry.IsDirty) return true;

        string matKey = !string.IsNullOrEmpty(submesh.OriginalVmatPath)
            ? submesh.OriginalVmatPath.ToLowerInvariant().Trim()
            : GetMaterialKey(submesh.Mesh, submesh.SurfaceIndex);
        string chKey = GetChannelContextKey(matKey, channel);
        if (_materialContexts.TryGetValue(chKey, out var ctx) && ctx != null && ctx.HasAnyPaint())
        {
            return true;
        }
        return string.Equals(_activeMaterialKey, chKey, StringComparison.OrdinalIgnoreCase) && HasPaintStrokes(chKey);
    }

    public Image BakeChannelImageForSubmesh(SubmeshNodeInfo submesh, MaterialTextureChannel channel)
    {
        if (submesh == null) return null;
        if (channel == MaterialTextureChannel.Color)
        {
            var entryC = submesh.Channels?.GetChannel(MaterialTextureChannel.Color);
            if (!HasChannelEdits(submesh, MaterialTextureChannel.Color) && entryC?.SourceImage != null)
                return entryC.SourceImage;
            return BakeCompositeImageForSubmesh(submesh);
        }

        string baseKey = !string.IsNullOrEmpty(submesh.OriginalVmatPath)
            ? submesh.OriginalVmatPath.ToLowerInvariant().Trim()
            : GetMaterialKey(submesh.Mesh, submesh.SurfaceIndex);
        string chKey = GetChannelContextKey(baseKey, channel);

        var entry = submesh.Channels?.GetChannel(channel);
        bool srgb = entry?.IsColorSpaceSrgb ?? (channel is MaterialTextureChannel.Shadow or MaterialTextureChannel.SelfIllum);

        if (string.Equals(_activeMaterialKey, chKey, StringComparison.OrdinalIgnoreCase))
        {
            int w = CanvasSize.X > 0 ? CanvasSize.X : 2048;
            int h = CanvasSize.Y > 0 ? CanvasSize.Y : 2048;
            var live = BakeCompositeImageFromLayers(_layers, w, h, srgb);
            if (live != null && !live.IsEmpty()) return live;
        }

        if (_materialContexts.TryGetValue(chKey, out var ctx) && ctx != null && ctx.Layers.Count > 0)
        {
            int w = ctx.CanvasSize.X > 0 ? ctx.CanvasSize.X : 2048;
            int h = ctx.CanvasSize.Y > 0 ? ctx.CanvasSize.Y : 2048;
            byte[] savedBuf = _baseAtlasBuffer;
            if (ctx.BaseAtlasBuffer != null) _baseAtlasBuffer = ctx.BaseAtlasBuffer;
            var img = BakeCompositeImageFromLayers(ctx.Layers, w, h, srgb);
            _baseAtlasBuffer = savedBuf;
            if (img != null && !img.IsEmpty()) return img;
        }

        return entry?.SourceImage ?? entry?.Texture?.GetImage();
    }
}
