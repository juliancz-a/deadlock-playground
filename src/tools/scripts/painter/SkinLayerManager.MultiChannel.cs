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
    private bool _userMutedNonColorSolo = false;

    public event Action<MaterialTextureChannel> ActiveChannelChanged;
    public event Action<MaterialChannelSet> ChannelSetUpdated;
    public event Action<MaterialTextureChannel> ExternalChannelMapImported;

    public string ActiveBaseMaterialKey => StripChannelSuffix(_activeMaterialKey);

    public static string StripChannelSuffix(string key)
    {
        if (string.IsNullOrEmpty(key)) return key;
        int idx = key.IndexOf("::ch:", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? key.Substring(0, idx) : key;
    }

    public static string ExtractChannelSuffix(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        int idx = key.IndexOf("::ch:", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? key.Substring(idx) : string.Empty;
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
        string cleanBase = !string.IsNullOrEmpty(baseMatKey) ? StripChannelSuffix(baseMatKey) : null;

        if (MeshHierarchy != null)
        {
            if (mesh != null)
            {
                var sub = MeshHierarchy.FindSubmesh(mesh);
                if (sub?.Channels != null)
                {
                    if (string.IsNullOrEmpty(cleanBase))
                    {
                        return sub.Channels;
                    }
                    string subKey = GetMaterialKey(sub.Mesh, sub.SurfaceIndex);
                    string targetStem = ExtractMaterialStem(cleanBase);
                    if (string.Equals(subKey, cleanBase, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(sub.OriginalVmatPath, cleanBase, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(targetStem) &&
                         (string.Equals(ExtractMaterialStem(subKey), targetStem, StringComparison.Ordinal) ||
                          string.Equals(ExtractMaterialStem(sub.OriginalVmatPath), targetStem, StringComparison.Ordinal))))
                    {
                        return sub.Channels;
                    }
                }
            }
            if (!string.IsNullOrEmpty(cleanBase))
            {
                string targetStem = ExtractMaterialStem(cleanBase);
                foreach (var g in MeshHierarchy.MaterialGroups)
                {
                    if (string.Equals(g.MaterialKey, cleanBase, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(targetStem) && string.Equals(ExtractMaterialStem(g.MaterialKey), targetStem, StringComparison.Ordinal)))
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
                foreach (var s in MeshHierarchy.Submeshes)
                {
                    if (s?.Channels == null || s.Mesh == null) continue;
                    string sKey = GetMaterialKey(s.Mesh, s.SurfaceIndex);
                    if (string.Equals(sKey, cleanBase, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.OriginalVmatPath, cleanBase, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(targetStem) &&
                         (string.Equals(ExtractMaterialStem(sKey), targetStem, StringComparison.Ordinal) ||
                          string.Equals(ExtractMaterialStem(s.OriginalVmatPath), targetStem, StringComparison.Ordinal))))
                    {
                        return s.Channels;
                    }
                }
            }
        }

        if (mesh != null && GodotObject.IsInstanceValid(mesh) &&
            (string.IsNullOrEmpty(cleanBase) || string.Equals(GetMaterialKey(mesh, 0), cleanBase, StringComparison.OrdinalIgnoreCase)))
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
        EnsureCpuSynced();
        var rd = RenderingServer.GetRenderingDevice();
        foreach (var layer in _layers)
        {
            if (layer == null) continue;
            if (layer.IsBlank())
            {
                if (layer.LayerRid.IsValid)
                {
                    DetachAtlasManagerLayerRid(layer.LayerRid);
                    if (rd != null && rd.TextureIsValid(layer.LayerRid))
                    {
                        rd.FreeRid(layer.LayerRid);
                    }
                    layer.LayerRid = new Rid();
                }
                layer.GpuData = null;
                layer.CompressedGpuData = null;
            }
            else
            {
                layer.CompressGpuData();
            }
        }
        _currentContext.Layers = new List<SkinLayer>(_layers);
        _currentContext.ActiveLayerIndex = _activeLayerIndex;
        // Never pin 128 MiB base buffer in inactive channel contexts; rebuilt on demand.
        _currentContext.BaseAtlasBuffer = null;
        _currentContext.CanvasSize = CanvasSize;
        _currentContext.UndoStack = new List<LayerPaintSnapshot>(_undoStack);
        while (_currentContext.UndoStack.Count > 1)
        {
            _currentContext.UndoStack.RemoveAt(0);
        }
        _currentContext.RedoStack.Clear();
        _materialContexts[ctxKey] = _currentContext;
    }

    private void DetachAtlasManagerLayerRid(Rid targetRid)
    {
        if (!targetRid.IsValid || _activeAtlasManager == null || !GodotObject.IsInstanceValid(_activeAtlasManager)) return;
        var curActiveRid = _activeAtlasManager.Get("atlas_texture_rid");
        if (curActiveRid.VariantType == Variant.Type.Rid && curActiveRid.AsRid() == targetRid)
        {
            _activeAtlasManager.Set("atlas_texture_rid", new Rid());
            var resObj = _activeAtlasManager.Get("active_layer_resource");
            if (resObj.VariantType == Variant.Type.Object && resObj.AsGodotObject() is Texture2Drd texRd)
            {
                texRd.TextureRdRid = new Rid();
            }
        }
    }

    /// <summary>
    /// Switches the active paintable / inspectable texture channel (Color, AO, Normal, Roughness, Shadow, etc.)
    /// for the currently active material group while preserving per-channel layer stacks.
    /// </summary>
    public void SetActiveTextureChannel(MaterialTextureChannel channel)
    {
        if (_targetMesh == null || !GodotObject.IsInstanceValid(_targetMesh)) return;
        if (ActiveTextureChannel == channel) return;
        string baseKey = !string.IsNullOrEmpty(_activeMaterialKey)
            ? StripChannelSuffix(_activeMaterialKey)
            : GetMaterialKey(_targetMesh);
        if (string.IsNullOrEmpty(baseKey)) return;

        var chSet = GetChannelSetForMesh(_targetMesh, baseKey);
        if (chSet == null) return;

        // Flush any unsaved paint strokes on the outgoing channel before switching away
        SyncActiveChannelCompositeToSet();

        string prevCtxKey = GetChannelContextKey(baseKey, ActiveTextureChannel);
        SaveCurrentContextSnapshot(prevCtxKey);

        ActiveTextureChannel = channel;
        chSet.ActiveChannel = channel;
        if (channel != MaterialTextureChannel.Color)
        {
            if (!_userMutedNonColorSolo)
            {
                chSet.SoloPreviewChannel = channel;
            }
            else if (chSet.SoloPreviewChannel.HasValue)
            {
                chSet.SoloPreviewChannel = channel;
            }
        }
        else
        {
            chSet.SoloPreviewChannel = null;
        }

        string nextCtxKey = GetChannelContextKey(baseKey, channel);
        _activeMaterialKey = nextCtxKey;

        int w = CanvasSize.X > 0 ? CanvasSize.X : 2048;
        int h = CanvasSize.Y > 0 ? CanvasSize.Y : 2048;

        var chEntry = chSet.GetChannel(channel);
        chEntry?.EnsureMaterialized();
        Texture2D chBaseTex = chEntry?.Texture;

        if (_materialContexts.TryGetValue(nextCtxKey, out var nextCtx) && nextCtx != null && nextCtx.Layers.Count > 0)
        {
            var rd = RenderingServer.GetRenderingDevice();
            int bufLen = w * h * 8;
            int nextActiveIdx = Mathf.Clamp(nextCtx.ActiveLayerIndex, 0, Math.Max(0, nextCtx.Layers.Count - 1));
            for (int i = 0; i < nextCtx.Layers.Count; i++)
            {
                var layer = nextCtx.Layers[i];
                if (layer.GpuData == null || layer.GpuData.Length != bufLen)
                {
                    layer.DecompressGpuData(bufLen);
                }
                if (rd != null && (!layer.LayerRid.IsValid || !rd.TextureIsValid(layer.LayerRid)))
                {
                    if (layer.GpuData != null && layer.GpuData.Length == bufLen)
                    {
                        layer.LayerRid = CreateLayerGpuTexture(w, h, layer.GpuData);
                    }
                    else if (i == nextActiveIdx || nextCtx.Layers.Count == 1)
                    {
                        // Active layer must always own a valid cleared GPU texture (zero CPU allocation needed).
                        layer.LayerRid = CreateLayerGpuTexture(w, h, null);
                        layer.IsCpuSynced = false;
                    }
                }
            }
            _currentContext = nextCtx;
            _layers.Clear();
            _layers.AddRange(nextCtx.Layers);
            _activeLayerIndex = nextActiveIdx;
            _undoStack.Clear();
            _undoStack.AddRange(nextCtx.UndoStack);
            _redoStack.Clear();
            _redoStack.AddRange(nextCtx.RedoStack);
        }
        else
        {
            _layers.Clear();
            _undoStack.Clear();
            _redoStack.Clear();

            EnsureShadersLoaded();
            var defaultLayer = new SkinLayer();
            defaultLayer.Initialize($"{MaterialChannelSet.GetDisplayLabel(channel)} Paint 1", CanvasSize, false, _compositeShader, _dabShader, null);
            defaultLayer.LayerRid = CreateLayerGpuTexture(w, h, null);
            defaultLayer.IsCpuSynced = false;
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

        // Update base texture layer & rebuild active channel GPU base atlas buffer (never touch _standaloneBaseTexture!)
        if (_baseLayer != null)
        {
            _baseLayer.Texture = chBaseTex as ImageTexture ?? (chBaseTex != null ? ImageTexture.CreateFromImage(chBaseTex.GetImage()) : null);
            _baseLayer.Name = channel == MaterialTextureChannel.Color
                ? "Base Texture"
                : $"{MaterialChannelSet.GetDisplayLabel(channel)} Map";
        }
        InvalidateBaseAtlasBuffer();
        RebuildBaseAtlasBuffer();

        UploadLayersToActiveAtlasManager();
        _otherLayersDirty = true;
        UpdateActiveLayerBinding();
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

        int w = Math.Max(1, CanvasSize.X);
        int h = Math.Max(1, CanvasSize.Y);
        int bufferLen = w * h * 8;
        var layerList = new Godot.Collections.Array<GodotObject>();
        for (int i = 0; i < _layers.Count; i++)
        {
            var l = _layers[i];
            if (!l.LayerRid.IsValid || !rd.TextureIsValid(l.LayerRid))
            {
                if (l.GpuData != null && l.GpuData.Length == bufferLen)
                {
                    l.LayerRid = CreateLayerGpuTexture(w, h, l.GpuData);
                }
                else if (i == _activeLayerIndex)
                {
                    l.LayerRid = CreateLayerGpuTexture(w, h, null);
                    l.IsCpuSynced = false;
                }
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
            if (resObj.VariantType == Variant.Type.Object && resObj.AsGodotObject() is Texture2Drd texRd)
            {
                texRd.TextureRdRid = (act.LayerRid.IsValid && rd.TextureIsValid(act.LayerRid)) ? act.LayerRid : new Rid();
            }
        }
    }

    public void SetChannelEnabled(MaterialTextureChannel channel, bool enabled)
    {
        var chSet = GetActiveChannelSet();
        var entry = chSet?.GetChannel(channel);
        if (entry == null) return;

        if (channel == ActiveTextureChannel && channel != MaterialTextureChannel.Color && HasPaintStrokes())
        {
            SyncActiveChannelCompositeToSet();
        }
        entry.IsEnabled = enabled;
        SyncChannelsToTargetMaterials(ActiveBaseMaterialKey);
        ApplyOverlayParametersToMeshes();
        ChannelSetUpdated?.Invoke(chSet);
    }

    public void SetSoloChannelPreview(bool solo)
    {
        var chSet = GetActiveChannelSet();
        if (chSet == null) return;

        if (ActiveTextureChannel != MaterialTextureChannel.Color)
        {
            _userMutedNonColorSolo = !solo;
        }
        chSet.SoloPreviewChannel = solo ? ActiveTextureChannel : null;
        SyncChannelsToTargetMaterials(ActiveBaseMaterialKey);
        ApplyOverlayParametersToMeshes();
        ChannelSetUpdated?.Invoke(chSet);
    }

    public bool SelectColorVariant(int variantIndex)
    {
        var chSet = GetActiveChannelSet();
        if (chSet == null) return false;
        var existingColor = chSet.GetChannel(MaterialTextureChannel.Color);
        if (existingColor != null && existingColor.ActiveVariantIndex == variantIndex && existingColor.HasValidTexture)
            return false;

        if (!chSet.SwitchColorVariant(ActiveVpkPackage, ActiveAddonPackage, variantIndex))
            return false;

        var colorTex = chSet.GetChannel(MaterialTextureChannel.Color)?.Texture;
        SyncChannelsToTargetMaterials(ActiveBaseMaterialKey);
        ApplyOverlayParametersToMeshes();

        if (ActiveTextureChannel == MaterialTextureChannel.Color)
        {
            if (_baseLayer != null) _baseLayer.Texture = colorTex as ImageTexture;
            InvalidateBaseAtlasBuffer();
            RebuildBaseAtlasBuffer();
            RecompositeGpuLayers();
        }

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

        if (IsStandaloneTextureMode)
        {
            _standaloneBaseTexture = tex;
        }
        if (_baseLayer != null)
        {
            _baseLayer.Texture = tex;
        }
        SyncChannelsToTargetMaterials(ActiveBaseMaterialKey);
        ApplyOverlayParametersToMeshes();
        InvalidateBaseAtlasBuffer();
        RebuildBaseAtlasBuffer();
        RecompositeGpuLayers();
        SetActiveContextHasPaintStrokes(true);
        NotifyStackChanged();
        ChannelSetUpdated?.Invoke(chSet);
        ExternalChannelMapImported?.Invoke(ch);
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
        if (IsStandaloneTextureMode)
        {
            _standaloneBaseTexture = entry?.Texture;
        }
        if (_baseLayer != null)
        {
            _baseLayer.Texture = entry?.Texture as ImageTexture;
        }

        foreach (var l in _layers)
        {
            if (l.GpuData != null) Array.Clear(l.GpuData, 0, l.GpuData.Length);
            l.IsCpuSynced = true;
        }
        SyncChannelsToTargetMaterials(ActiveBaseMaterialKey);
        ApplyOverlayParametersToMeshes();
        UploadLayersToActiveAtlasManager();
        InvalidateBaseAtlasBuffer();
        RebuildBaseAtlasBuffer();
        _otherLayersDirty = true;
        RecompositeGpuLayers();
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

        // Never overwrite an unpainted original/variant texture with a CPU re-bake
        if (!hasPaint) return;

        int w = CanvasSize.X > 0 ? CanvasSize.X : 2048;
        int h = CanvasSize.Y > 0 ? CanvasSize.Y : 2048;
        // Atlas layers & _baseAtlasBuffer store sRGB-decoded linear floats; convert back to [0..1] encoded byte values cleanly.
        Image baked = BakeCompositeImageFromLayers(_layers, w, h, applyLinearToSrgb: true);
        if (baked != null && !baked.IsEmpty())
        {
            var tex = ImageTexture.CreateFromImage(baked);
            entry.Texture = tex;
            entry.SourceImage = baked;
            entry.IsDirty = true;
            entry.IsDefaultFallback = false;
            entry.IsPresent = true;
        }
    }

    private void OnMultiChannelPaintCommitted()
    {
        if (ActiveTextureChannel == MaterialTextureChannel.Color) return;
        // Defer expensive 4K CPU readback/bake to channel switch, SaveActiveContext, or export;
        // live 3D strokes render at 60 FPS directly on GPU via active_paint_channel in hero_painter_overlay.gdshader.
        var entry = GetActiveChannelSet()?.GetChannel(ActiveTextureChannel);
        if (entry != null)
        {
            entry.IsDirty = true;
            entry.IsDefaultFallback = false;
            entry.IsPresent = true;
        }
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
            string targetStem = ExtractMaterialStem(baseMatKey);
            foreach (var sub in MeshHierarchy.Submeshes)
            {
                if (sub?.Mesh == null || !GodotObject.IsInstanceValid(sub.Mesh)) continue;
                string subKey = GetMaterialKey(sub.Mesh, sub.SurfaceIndex);
                if (!string.Equals(subKey, baseMatKey, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(sub.OriginalVmatPath, baseMatKey, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrEmpty(targetStem) ||
                     (!string.Equals(ExtractMaterialStem(subKey), targetStem, StringComparison.Ordinal) &&
                      !string.Equals(ExtractMaterialStem(sub.OriginalVmatPath), targetStem, StringComparison.Ordinal))))
                {
                    continue;
                }

                if (sub.OriginalMaterial != null)
                    MaterialChannelDiscovery.ApplyChannelsToMaterial(sub.OriginalMaterial, chSet);
                if (sub.ToonMaterial != null)
                    MaterialChannelDiscovery.ApplyChannelsToMaterial(sub.ToonMaterial, chSet);
                var surfMat = sub.Mesh.GetSurfaceOverrideMaterial(sub.SurfaceIndex);
                if (surfMat != null && surfMat != sub.OriginalMaterial && surfMat != sub.ToonMaterial)
                    MaterialChannelDiscovery.ApplyChannelsToMaterial(surfMat, chSet);
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

        mat.SetShaderParameter("active_paint_channel", editingOtherChannel ? MaterialChannelSet.ChannelToShaderDebugIndex(ActiveTextureChannel) : 0);

        if (editingOtherChannel)
        {
            var colorTex = chSet.GetChannel(MaterialTextureChannel.Color)?.Texture ?? ExtractMeshBaseTexture(mesh);
            bool validColorTex = IsTextureRenderReady(colorTex);
            mat.SetShaderParameter("use_channel_color_override", validColorTex);
            mat.SetShaderParameter("channel_color_override_texture", validColorTex ? colorTex : null);
        }
        else
        {
            mat.SetShaderParameter("use_channel_color_override", false);
        }
    }

    public int GetContextActiveLayerIndex(string materialKey)
    {
        if (string.IsNullOrEmpty(materialKey)) return _activeLayerIndex;
        if (string.Equals(_activeMaterialKey, materialKey, StringComparison.OrdinalIgnoreCase))
            return _activeLayerIndex;
        return _materialContexts.TryGetValue(materialKey, out var ctx) && ctx != null
            ? ctx.ActiveLayerIndex
            : 0;
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

        if (string.Equals(_activeMaterialKey, chKey, StringComparison.OrdinalIgnoreCase))
        {
            int w = CanvasSize.X > 0 ? CanvasSize.X : 2048;
            int h = CanvasSize.Y > 0 ? CanvasSize.Y : 2048;
            var live = BakeCompositeImageFromLayers(_layers, w, h, applyLinearToSrgb: true);
            if (live != null && !live.IsEmpty()) return live;
        }

        if (_materialContexts.TryGetValue(chKey, out var ctx) && ctx != null && ctx.Layers.Count > 0)
        {
            int w = ctx.CanvasSize.X > 0 ? ctx.CanvasSize.X : 2048;
            int h = ctx.CanvasSize.Y > 0 ? ctx.CanvasSize.Y : 2048;
            byte[] savedBuf = _baseAtlasBuffer;
            if (ctx.BaseAtlasBuffer != null) _baseAtlasBuffer = ctx.BaseAtlasBuffer;
            var img = BakeCompositeImageFromLayers(ctx.Layers, w, h, applyLinearToSrgb: true);
            _baseAtlasBuffer = savedBuf;
            if (img != null && !img.IsEmpty()) return img;
        }

        return entry?.SourceImage ?? entry?.Texture?.GetImage();
    }
}
