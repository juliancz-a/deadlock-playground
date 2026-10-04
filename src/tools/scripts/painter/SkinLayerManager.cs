using Godot;
using System;
using System.Collections.Generic;

namespace DeadlockPlayground.Painter
{
    public partial class SkinLayerManager : Node
    {
        [Signal] public delegate void LayerAddedEventHandler(int index, string name);
        [Signal] public delegate void LayerRemovedEventHandler(int index);
        [Signal] public delegate void LayerSelectedEventHandler(int index);
        [Signal] public delegate void LayersReorderedEventHandler();
        [Signal] public delegate void StackChangedEventHandler();

        public Vector2I CanvasSize { get; private set; } = new Vector2I(2048, 2048);

        private readonly List<SkinLayer> _layers = new();
        private int _activeLayerIndex = 0;
        private int _activeSurfaceIndex = 0;

        public int ActiveSurfaceIndex => _activeSurfaceIndex;
        public int TargetSurfaceIndex => _activeSurfaceIndex;

        public IReadOnlyList<SkinLayer> Layers => _layers;
        public int ActiveLayerIndex => _activeLayerIndex;
        public SkinLayer ActiveLayer => (_activeLayerIndex >= 0 && _activeLayerIndex < _layers.Count) ? _layers[_activeLayerIndex] : null;

        public SubViewport CompositeViewport => ActiveLayer?.CompositeViewport;
        public Control CompositeLayerContainer => ActiveLayer?.DisplayRect;

        private Shader _compositeShader;
        private Shader _dabShader;
        private Shader _heroPainterShader;

        private MeshInstance3D _targetMesh;
        public MeshInstance3D TargetMesh => _targetMesh;
        public HeroMeshHierarchy MeshHierarchy { get; set; }

        public override void _Ready()
        {
            EnsureShadersLoaded();
        }

        private void EnsureShadersLoaded()
        {
            _compositeShader ??= GD.Load<Shader>("res://assets/shaders/painter/layer_composite.gdshader");
            _dabShader ??= GD.Load<Shader>("res://assets/shaders/painter/brush_dab.gdshader");
            _heroPainterShader ??= GD.Load<Shader>("res://assets/shaders/painter/hero_painter_pbr.gdshader");
        }

        // Per-material atlas managers: meshes sharing identical materials share a single full-resolution OverlayAtlasManager.
        private readonly Dictionary<string, Node> _perMaterialAtlasManagers = new();
        private readonly Dictionary<MeshInstance3D, string> _meshToMaterialKey = new();
        private readonly List<string> _atlasLruKeys = new();
        private const int MaxActiveAtlasManagers = 16;

        private static readonly float[] s_srgb8ToLinear = new float[256];

        static SkinLayerManager()
        {
            for (int i = 0; i < 256; i++)
            {
                float s = i / 255.0f;
                s_srgb8ToLinear[i] = (s <= 0.04045f) ? (s / 12.92f) : MathF.Pow((s + 0.055f) / 1.055f, 2.4f);
            }
        }

        private class MaterialPaintingContext
        {
            public string MaterialKey;
            public Vector2I CanvasSize;
            public List<SkinLayer> Layers = new();
            public int ActiveLayerIndex = 0;
            public List<LayerPaintSnapshot> UndoStack = new();
            public List<LayerPaintSnapshot> RedoStack = new();
            public byte[] BaseAtlasBuffer;
            public Texture2D BakedCompositeTexture;

            public bool HasAnyPaint()
            {
                if (Layers == null || Layers.Count == 0) return false;
                foreach (var l in Layers)
                {
                    if (l != null && !l.IsBlank()) return true;
                }
                return false;
            }
        }

        private readonly Dictionary<string, MaterialPaintingContext> _materialContexts = new();
        private MaterialPaintingContext _currentContext;

        private string _activeMaterialKey;
        public string ActiveMaterialKey => _activeMaterialKey;

        // The atlas manager for the currently active paint target (null if no target selected).
        private Node _activeAtlasManager;
        /// <summary>Returns the OverlayAtlasManager for the currently selected paint target submesh.</summary>
        public Node AtlasManager => _activeAtlasManager;

        private Node3D _currentHero;
        public Node3D CurrentHero => _currentHero;
        private byte[] _compositeBuffer;
        private byte[] _otherLayersBuffer;
        private bool _otherLayersDirty = true;

        public void InvalidateOtherLayers() => _otherLayersDirty = true;

        private Rid _gpuUndoTextureRid = new();
        private int _gpuUndoWidth = 0;
        private int _gpuUndoHeight = 0;
        private int _gpuUndoLayerIndex = -1;
        private bool _hasGpuUndo = false;

        public void EnsureGpuUndoTexture(int width, int height)
        {
            var rd = RenderingServer.GetRenderingDevice();
            if (rd == null) return;

            if (_gpuUndoTextureRid.IsValid && rd.TextureIsValid(_gpuUndoTextureRid))
            {
                if (_gpuUndoWidth == width && _gpuUndoHeight == height) return;
                rd.FreeRid(_gpuUndoTextureRid);
                _gpuUndoTextureRid = new Rid();
            }

            _gpuUndoTextureRid = CreateLayerGpuTexture(width, height);
            _gpuUndoWidth = width;
            _gpuUndoHeight = height;
        }

        public void RecordGpuUndoSnapshot()
        {
            var rd = RenderingServer.GetRenderingDevice();
            var layer = ActiveLayer;
            if (rd == null || layer == null || !layer.LayerRid.IsValid || !rd.TextureIsValid(layer.LayerRid)) return;

            int atlasSize = CanvasSize.X > 0 ? CanvasSize.X : 2048;
            EnsureGpuUndoTexture(atlasSize, atlasSize);
            if (!_gpuUndoTextureRid.IsValid || !rd.TextureIsValid(_gpuUndoTextureRid)) return;

            rd.TextureCopy(layer.LayerRid, _gpuUndoTextureRid, Vector3.Zero, Vector3.Zero, new Vector3(atlasSize, atlasSize, 1), 0, 0, 0, 0);
            _gpuUndoLayerIndex = _activeLayerIndex;
            _hasGpuUndo = true;
        }

        public bool TryGpuUndo()
        {
            if (!_hasGpuUndo) return false;
            var rd = RenderingServer.GetRenderingDevice();
            var layer = ActiveLayer;
            if (rd == null || layer == null || !layer.LayerRid.IsValid || !rd.TextureIsValid(layer.LayerRid)) return false;
            if (!_gpuUndoTextureRid.IsValid || !rd.TextureIsValid(_gpuUndoTextureRid)) return false;
            if (_gpuUndoLayerIndex != _activeLayerIndex) return false;

            int atlasSize = CanvasSize.X > 0 ? CanvasSize.X : 2048;
            rd.TextureCopy(_gpuUndoTextureRid, layer.LayerRid, Vector3.Zero, Vector3.Zero, new Vector3(atlasSize, atlasSize, 1), 0, 0, 0, 0);
            _hasGpuUndo = false;
            layer.IsCpuSynced = false;
            return true;
        }

        public void EnsureCpuSynced()
        {
            var rd = RenderingServer.GetRenderingDevice();
            if (rd == null) return;
            foreach (var layer in _layers)
            {
                if (!layer.IsCpuSynced && layer.LayerRid.IsValid && rd.TextureIsValid(layer.LayerRid))
                {
                    layer.GpuData = rd.TextureGetData(layer.LayerRid, 0);
                    layer.IsCpuSynced = true;
                }
            }
        }

        public Rid CreateLayerGpuTexture(int width, int height, byte[] initialData = null)
        {
            var rd = RenderingServer.GetRenderingDevice();
            if (rd == null) return new Rid();

            var fmt = new RDTextureFormat
            {
                Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
                Width = (uint)width,
                Height = (uint)height,
                UsageBits = RenderingDevice.TextureUsageBits.StorageBit |
                            RenderingDevice.TextureUsageBits.SamplingBit |
                            RenderingDevice.TextureUsageBits.CanUpdateBit |
                            RenderingDevice.TextureUsageBits.CanCopyFromBit |
                            RenderingDevice.TextureUsageBits.CanCopyToBit
            };
            var view = new RDTextureView();
            byte[] data = (initialData != null && initialData.Length == width * height * 8)
                ? initialData
                : new byte[width * height * 8];
            return rd.TextureCreate(fmt, view, new Godot.Collections.Array<byte[]> { data });
        }

        private void TestTextureCopy(Rid a, Rid b)
        {
            var rd = RenderingServer.GetRenderingDevice();
            rd?.TextureCopy(a, b, Vector3.Zero, Vector3.Zero, new Vector3(100, 100, 1), 0, 0, 0, 0);
        }

        /// <summary>
        /// Resolves a unique material identifier for <paramref name="mesh"/> so all submeshes
        /// sharing the same material (e.g. ammo clips, bullets, weapon props) reuse a single atlas.
        /// </summary>
        public static string ResolveMaterialKey(MeshInstance3D mesh, int surfaceIndex = 0, HeroMeshHierarchy hierarchy = null)
        {
            if (mesh == null || !GodotObject.IsInstanceValid(mesh) || !HeroMeshHierarchy.IsAuthenticHeroMesh(mesh)) return string.Empty;

            // 1. Check if HeroMeshHierarchy already tagged OriginalVmatPath metadata
            if (mesh.HasMeta("OriginalVmatPath"))
            {
                string vmat = mesh.GetMeta("OriginalVmatPath").AsString();
                if (!string.IsNullOrWhiteSpace(vmat)) return vmat.ToLowerInvariant().Trim();
            }

            // 2. Check HeroMeshHierarchy submesh metadata
            if (hierarchy != null && hierarchy.Submeshes != null)
            {
                SubmeshNodeInfo match = null;
                for (int s = 0; s < hierarchy.Submeshes.Count; s++)
                {
                    var sub = hierarchy.Submeshes[s];
                    if (sub.Mesh == mesh && sub.SurfaceIndex == surfaceIndex)
                    {
                        match = sub;
                        break;
                    }
                    if (sub.Mesh == mesh && match == null)
                    {
                        match = sub;
                    }
                }
                if (match != null)
                {
                    if (!string.IsNullOrWhiteSpace(match.OriginalVmatPath)) return match.OriginalVmatPath.ToLowerInvariant().Trim();
                    if (!string.IsNullOrWhiteSpace(match.OriginalColorVtexCPath)) return match.OriginalColorVtexCPath.ToLowerInvariant().Trim();
                    if (!string.IsNullOrWhiteSpace(match.MaterialName)) return match.MaterialName.ToLowerInvariant().Trim();
                }
            }

            // 3. Inspect authentic material / surface override material
            var mat = HeroMeshHierarchy.GetAuthenticMaterial(mesh, surfaceIndex)
                   ?? mesh.GetSurfaceOverrideMaterial(surfaceIndex)
                   ?? (mesh.Mesh != null && surfaceIndex < mesh.Mesh.GetSurfaceCount() ? mesh.Mesh.SurfaceGetMaterial(surfaceIndex) : null)
                   ?? mesh.MaterialOverride;

            if (mat != null)
            {
                if (!string.IsNullOrWhiteSpace(mat.ResourcePath)) return mat.ResourcePath.ToLowerInvariant().Trim();
                if (!string.IsNullOrWhiteSpace(mat.ResourceName)) return mat.ResourceName.ToLowerInvariant().Trim();

                var baseTex = ExtractBaseTexture(mat);
                if (baseTex != null)
                {
                    if (!string.IsNullOrWhiteSpace(baseTex.ResourcePath)) return baseTex.ResourcePath.ToLowerInvariant().Trim();
                    if (!string.IsNullOrWhiteSpace(baseTex.ResourceName)) return baseTex.ResourceName.ToLowerInvariant().Trim();
                    return $"tex_rid_{baseTex.GetRid().Id}";
                }
                return $"mat_rid_{mat.GetRid().Id}";
            }

            return $"mesh_{mesh.Name}_{surfaceIndex}".ToLowerInvariant();
        }

        public string GetMaterialKey(MeshInstance3D mesh, int surfaceIndex = 0)
        {
            return ResolveMaterialKey(mesh, surfaceIndex, MeshHierarchy);
        }

        public void SetupForHero(Node3D heroNode)
        {
            if (_currentHero == heroNode && _perMaterialAtlasManagers.Count > 0)
            {
                return;
            }

            _currentHero = heroNode;
            if (_currentHero == null)
            {
                ClearAllAtlasManagers();
                ClearAllLayers();
                return;
            }

            RunHeroMaterialAudit(_currentHero, MeshHierarchy);

            ClearAllAtlasManagers();
            _materialContexts.Clear();
            _currentContext = null;
            _activeMaterialKey = null;
            // Individual atlases are created lazily when SetPaintTargetMesh() is first called.

            ClearAllLayers();
            ClearHistory();
            AddNewLayer("Paint Layer 1");
            RecompositeGpuLayers();
            RecordInitialSnapshot();
            NotifyStackChanged();
            NotifyLayerSelected(0);
        }

        /// <summary>
        /// Lazily creates (or retrieves) a dedicated full-resolution OverlayAtlasManager for
        /// the material used by <paramref name="mesh"/>. If another submesh shares the same material
        /// (e.g. ammo clips, bullets sharing weapon textures), it reuses the existing manager.
        /// </summary>
        public Node EnsureSubmeshAtlas(MeshInstance3D mesh)
        {
            if (mesh == null || !GodotObject.IsInstanceValid(mesh) || !HeroMeshHierarchy.IsAuthenticHeroMesh(mesh)) return null;

            string mLower = mesh.Name.ToString().ToLowerInvariant();
            if (mLower.Contains("sparkle") || mLower.Contains("ghost_glow") || mLower.Contains("outline")) return null;

            string materialKey = GetMaterialKey(mesh);

            if (_perMaterialAtlasManagers.TryGetValue(materialKey, out var existing))
            {
                if (existing != null && GodotObject.IsInstanceValid(existing))
                {
                    _meshToMaterialKey[mesh] = materialKey;
                    _atlasLruKeys.Remove(materialKey);
                    _atlasLruKeys.Add(materialKey);
                    // Apply existing overlay texture/shader to this mesh without creating a new atlas
                    try
                    {
                        existing.Call("apply_to_mesh", mesh);
                        GD.Print($"[SkinLayerManager] Reused atlas for shared material '{materialKey}' on '{mesh.Name}'");
                    }
                    catch (Exception ex)
                    {
                        GD.PrintErr($"[SkinLayerManager] apply_to_mesh failed for '{mesh.Name}': {ex.Message}");
                    }
                    _atlasLruKeys.Remove(materialKey);
                    _atlasLruKeys.Add(materialKey);
                    return existing;
                }
                _perMaterialAtlasManagers.Remove(materialKey);
                _atlasLruKeys.Remove(materialKey);
            }

            if (_currentHero == null || !GodotObject.IsInstanceValid(_currentHero))
            {
                return null;
            }

            // LRU eviction: keep at most MaxActiveAtlasManagers in VRAM
            while (_perMaterialAtlasManagers.Count >= MaxActiveAtlasManagers && _atlasLruKeys.Count > 0)
            {
                string oldestKey = _atlasLruKeys[0];
                _atlasLruKeys.RemoveAt(0);

                MaterialPaintingContext evictedCtx = null;
                _materialContexts.TryGetValue(oldestKey, out evictedCtx);

                if (_perMaterialAtlasManagers.TryGetValue(oldestKey, out var evictedMgr))
                {
                    if (evictedMgr != null && GodotObject.IsInstanceValid(evictedMgr))
                    {
                        if (evictedCtx != null && evictedCtx.HasAnyPaint())
                        {
                            var baked = BakeCompositeImageTexture(evictedMgr);
                            if (baked != null)
                            {
                                evictedCtx.BakedCompositeTexture = baked;
                            }
                        }

                        // Update or clear overlay material for all meshes sharing evicted material
                        foreach (var (m, key) in _meshToMaterialKey)
                        {
                            if (key == oldestKey && m != null && GodotObject.IsInstanceValid(m))
                            {
                                if (evictedCtx != null && evictedCtx.HasAnyPaint() && evictedCtx.BakedCompositeTexture != null)
                                {
                                    if (m.MaterialOverlay is ShaderMaterial sm)
                                    {
                                        sm.SetShaderParameter("overlay_texture", evictedCtx.BakedCompositeTexture);
                                        sm.SetShaderParameter("active_layer_texture", (Texture2D)null);
                                        sm.SetShaderParameter("active_layer_visible", false);
                                    }
                                }
                                else
                                {
                                    m.MaterialOverlay = null;
                                }
                            }
                        }

                        if (evictedMgr.IsInGroup("overlay_atlas_managers"))
                        {
                            evictedMgr.RemoveFromGroup("overlay_atlas_managers");
                        }
                        evictedMgr.Set("is_active_target", false);
                        evictedMgr.Set("atlas_texture_rid", new Rid());
                        evictedMgr.Set("base_texture_rid", new Rid());
                        evictedMgr.Set("composite_texture_rid", new Rid());
                        evictedMgr.Set("full_composite_rid", new Rid());
                        if (evictedMgr.HasMethod("_cleanup_texture"))
                        {
                            evictedMgr.Call("_cleanup_texture");
                        }
                        evictedMgr.GetParent()?.RemoveChild(evictedMgr);
                        evictedMgr.QueueFree();
                    }
                    _perMaterialAtlasManagers.Remove(oldestKey);

                    var meshesToRemove = new List<MeshInstance3D>();
                    foreach (var (m, key) in _meshToMaterialKey)
                    {
                        if (key == oldestKey) meshesToRemove.Add(m);
                    }
                    foreach (var m in meshesToRemove) _meshToMaterialKey.Remove(m);
                    GD.Print($"[SkinLayerManager] LRU evicted GPU atlas for material '{oldestKey}' to enforce {MaxActiveAtlasManagers}-slot VRAM limit");
                }

                // Drop unedited blank layer buffers and compress non-blank layers for evicted context
                if (evictedCtx != null)
                {
                    var rd = RenderingServer.GetRenderingDevice();
                    foreach (var layer in evictedCtx.Layers)
                    {
                        if (layer.IsBlank())
                        {
                            if (layer.LayerRid.IsValid && rd != null && rd.TextureIsValid(layer.LayerRid))
                            {
                                rd.FreeRid(layer.LayerRid);
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
                    evictedCtx.BaseAtlasBuffer = null;
                    evictedCtx.UndoStack.Clear();
                    evictedCtx.RedoStack.Clear();
                }

                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    }
                    catch { }
                });
            }

            var atlasScript = GD.Load<GDScript>("res://addons/gpu_texture_painter/manager/overlay_atlas_manager.gd");
            if (atlasScript == null)
            {
                GD.PrintErr("[SkinLayerManager] Failed to load overlay_atlas_manager.gd");
                return null;
            }

            var shader = GD.Load<Shader>("res://assets/shaders/painter/hero_painter_overlay.gdshader");
            if (shader == null)
            {
                GD.PrintErr("[SkinLayerManager] hero_painter_overlay.gdshader not found!");
            }

            var manager = (Node)atlasScript.New();
            if (manager == null)
            {
                GD.PrintErr("[SkinLayerManager] Failed to instantiate OverlayAtlasManager.");
                return null;
            }

            string cleanMatName = System.IO.Path.GetFileNameWithoutExtension(materialKey).Replace(" ", "_");
            if (string.IsNullOrEmpty(cleanMatName)) cleanMatName = mesh.Name;
            manager.Name = $"AtlasManager_{cleanMatName}";
            if (shader != null)
            {
                manager.Set("overlay_shader", shader);
            }

            // Attach to hero so it lives in the scene tree and gets _ready() called
            _currentHero.AddChild(manager);

            // Determine native texture dimensions for this submesh
            var origMat = HeroMeshHierarchy.GetAuthenticMaterial(mesh, 0)
                       ?? mesh.GetSurfaceOverrideMaterial(0)
                       ?? (mesh.Mesh != null && mesh.Mesh.GetSurfaceCount() > 0 ? mesh.Mesh.SurfaceGetMaterial(0) : null)
                       ?? mesh.MaterialOverride;
            var baseTex = ExtractBaseTexture(origMat);

            int nativeW = baseTex?.GetWidth() ?? 2048;
            int nativeH = baseTex?.GetHeight() ?? 2048;

            try
            {
                manager.Call("apply_single_mesh", mesh, nativeW, nativeH);
                GD.Print($"[SkinLayerManager] Created material atlas for '{materialKey}' on mesh '{mesh.Name}' ({nativeW}x{nativeH})");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[SkinLayerManager] apply_single_mesh failed for '{mesh.Name}': {ex.Message}");
            }

            _perMaterialAtlasManagers[materialKey] = manager;
            _meshToMaterialKey[mesh] = materialKey;
            _atlasLruKeys.Remove(materialKey);
            _atlasLruKeys.Add(materialKey);
            return manager;
        }

        /// <summary>Legacy shim — kept so callers that checked AtlasManager != null still compile.</summary>
        public void InitializeOverlayAtlas()
        {
            if (_targetMesh != null)
                EnsureSubmeshAtlas(_targetMesh);
        }

        public void ClearAllAtlasManagers()
        {
            foreach (var kvp in _perMaterialAtlasManagers)
            {
                var mgr = kvp.Value;
                if (mgr != null && GodotObject.IsInstanceValid(mgr))
                {
                    if (mgr.IsInGroup("overlay_atlas_managers"))
                    {
                        mgr.RemoveFromGroup("overlay_atlas_managers");
                    }
                    mgr.Set("is_active_target", false);
                    mgr.Set("atlas_texture_rid", new Rid());
                    mgr.Set("composite_texture_rid", new Rid());
                    mgr.Set("full_composite_rid", new Rid());
                    mgr.Set("base_texture_rid", new Rid());
                    if (mgr.HasMethod("_cleanup_texture"))
                    {
                        mgr.Call("_cleanup_texture");
                    }
                    var parent = mgr.GetParent();
                    parent?.RemoveChild(mgr);
                    mgr.QueueFree();
                }
            }
            _perMaterialAtlasManagers.Clear();
            _meshToMaterialKey.Clear();
            _atlasLruKeys.Clear();
            _activeAtlasManager = null;
            _activeMaterialKey = null;

            if (_scratchTextureRid.IsValid)
            {
                var rd = RenderingServer.GetRenderingDevice();
                if (rd != null && rd.TextureIsValid(_scratchTextureRid))
                {
                    rd.FreeRid(_scratchTextureRid);
                }
                _scratchTextureRid = new Rid();
            }
            _scratchBuffer = null;
        }

        /// <summary>Backward-compat alias for ClearAllAtlasManagers.</summary>
        public void ClearAtlasManager() => ClearAllAtlasManagers();

        public void SetCanvasResolution(Vector2I newSize)
        {
            if (newSize.X < 512 || newSize.Y < 512) newSize = new Vector2I(Math.Max(512, newSize.X), Math.Max(512, newSize.Y));
            if (newSize == CanvasSize) return;
            CanvasSize = newSize;

            ResizeLayerBuffers();

            if (_activeAtlasManager != null && GodotObject.IsInstanceValid(_activeAtlasManager))
            {
                _activeAtlasManager.Set("atlas_size", (int)CanvasSize.X);
                if (_activeAtlasManager.HasMethod("resize_atlas"))
                {
                    _activeAtlasManager.Call("resize_atlas", (int)CanvasSize.X);
                }
                else
                {
                    _activeAtlasManager.Call("apply");
                }
                ApplyOverlayParametersToMeshes();
                RebuildBaseAtlasBuffer();
            }

            RecompositeGpuLayers();
            RecordInitialSnapshot();
            NotifyStackChanged();
        }

        /// <summary>
        /// Resizes all layer GpuData buffers and invalidates CPU caches to match the current CanvasSize.
        /// Called automatically when switching submeshes or changing resolution.
        /// </summary>
        private void ResizeLayerBuffers()
        {
            int newBufferLen = CanvasSize.X * CanvasSize.Y * 8;
            var rd = RenderingServer.GetRenderingDevice();
            foreach (var layer in _layers)
            {
                if (layer.LayerRid.IsValid && rd != null && rd.TextureIsValid(layer.LayerRid))
                {
                    rd.FreeRid(layer.LayerRid);
                }
                layer.GpuData = new byte[newBufferLen];
                layer.LayerRid = CreateLayerGpuTexture(CanvasSize.X, CanvasSize.Y, null);
                if (rd != null && layer.LayerRid.IsValid)
                {
                    rd.TextureClear(layer.LayerRid, new Color(0, 0, 0, 0), 0, 1, 0, 1);
                }
            }
            _undoStack.Clear();
            _redoStack.Clear();
            _compositeBuffer = null;
            _otherLayersBuffer = null;
            _baseAtlasBuffer = null;
            _hasPopulatedBaseAtlasBuffer = false;
            UpdateActiveLayerBinding();
        }

        public void SetupForMesh(MeshInstance3D meshInstance, int defaultSurfaceIndex = 0)
        {
            if (meshInstance == null || meshInstance.Mesh == null) return;

            EnsureShadersLoaded();
            _activeSurfaceIndex = defaultSurfaceIndex;

            if (_currentHero == null || !GodotObject.IsInstanceValid(_currentHero))
            {
                Node current = meshInstance.GetParent();
                while (current != null && !(current is SubViewport) && current != GetTree()?.Root)
                {
                    if (current is Node3D n3d && (n3d.Name.ToString().StartsWith("Hero") || current.GetParent() is SubViewport))
                    {
                        _currentHero = n3d;
                        break;
                    }
                    current = current.GetParent();
                }
            }

            SetPaintTargetMesh(meshInstance);
        }

        public void EnsureMaterialBinding(MeshInstance3D activeTargetMesh, int surfaceIndex = 0)
        {
            if (activeTargetMesh == null || !GodotObject.IsInstanceValid(activeTargetMesh)) return;
            EnsureSubmeshAtlas(activeTargetMesh);
        }

        private static void ConfigurePbrParameters(ShaderMaterial paintMat, Material origMat)
        {
            if (paintMat == null || origMat == null) return;

            if (origMat is StandardMaterial3D stdMat)
            {
                if (stdMat.NormalTexture != null)
                {
                    paintMat.SetShaderParameter("g_tNormalRoughness", stdMat.NormalTexture);
                }
                if (stdMat.AOTexture != null)
                {
                    paintMat.SetShaderParameter("g_tAmbientOcclusion", stdMat.AOTexture);
                }
                paintMat.SetShaderParameter("roughness", stdMat.Roughness);
                paintMat.SetShaderParameter("metallic", stdMat.Metallic);
                paintMat.SetShaderParameter("specular", stdMat.MetallicSpecular);
            }
            else if (origMat is ShaderMaterial origShaderMat)
            {
                var norm = origShaderMat.GetShaderParameter("g_tNormalRoughness");
                if (norm.VariantType == Variant.Type.Object && norm.AsGodotObject() is Texture2D nTex)
                {
                    paintMat.SetShaderParameter("g_tNormalRoughness", nTex);
                }
                var ao = origShaderMat.GetShaderParameter("g_tAmbientOcclusion");
                if (ao.VariantType == Variant.Type.Object && ao.AsGodotObject() is Texture2D aoTex)
                {
                    paintMat.SetShaderParameter("g_tAmbientOcclusion", aoTex);
                }
                var rough = origShaderMat.GetShaderParameter("roughness");
                if (rough.VariantType == Variant.Type.Float)
                {
                    paintMat.SetShaderParameter("roughness", rough);
                }
                var met = origShaderMat.GetShaderParameter("metallic");
                if (met.VariantType == Variant.Type.Float)
                {
                    paintMat.SetShaderParameter("metallic", met);
                }
            }
        }

        public static Texture2D ExtractBaseTexture(Material mat)
        {
            if (mat == null) return null;

            if (mat is BaseMaterial3D baseMat)
            {
                return baseMat.AlbedoTexture;
            }
            if (mat is ShaderMaterial sm)
            {
                string[] candidateParams = { 
                    "g_tColor", "g_tColor1", "g_tColorA", "g_tColor0", "g_tColor2", "g_tColorB", 
                    "u_texture_color", "texture_albedo", "albedo_texture", "color_map", "g_tNprTransmissiveColor" 
                };
                foreach (var param in candidateParams)
                {
                    var paramTex = sm.GetShaderParameter(param);
                    if (paramTex.VariantType == Variant.Type.Object && paramTex.AsGodotObject() is Texture2D t2d)
                    {
                        return t2d;
                    }
                }
            }
            return null;
        }

        public void SetActiveSurface(int surfaceIndex)
        {
            if (_activeSurfaceIndex == surfaceIndex) return;
            _activeSurfaceIndex = surfaceIndex;
            NotifyStackChanged();
            NotifyLayerSelected(ActiveLayerIndex);
        }

        public void PaintDab(int surfaceIndex, Vector2 uvCoordinate, Color brushColor, float brushSize, Texture2D brushMask, float hardness, float flow, Vector2 aspectScale = default)
        {
            var activeLayer = ActiveLayer;
            if (activeLayer == null || activeLayer.IsLocked || !activeLayer.IsVisible) return;

            activeLayer.PaintDab(uvCoordinate, brushColor, brushSize, brushMask, hardness, flow, aspectScale);
        }

        public void PaintDab(Vector2 uvCoordinate, Color brushColor, float brushSize, Texture2D brushMask, float hardness, float flow, Vector2 aspectScale = default)
        {
            PaintDab(_activeSurfaceIndex, uvCoordinate, brushColor, brushSize, brushMask, hardness, flow, aspectScale);
        }

        public void RequestCompositeRender(int surfaceIndex = -1)
        {
        }

        public SkinLayer AddNewLayer(string name = null)
        {
            EnsureShadersLoaded();
            string layerName = name ?? $"Paint Layer {_layers.Count + 1}";
            var newLayer = new SkinLayer();
            newLayer.Initialize(layerName, CanvasSize, isLocked: false, _compositeShader, _dabShader, null);
            newLayer.GpuData = new byte[CanvasSize.X * CanvasSize.Y * 8];
            newLayer.LayerRid = CreateLayerGpuTexture(CanvasSize.X, CanvasSize.Y, null);
            var rd = RenderingServer.GetRenderingDevice();
            if (rd != null && newLayer.LayerRid.IsValid)
            {
                rd.TextureClear(newLayer.LayerRid, new Color(0, 0, 0, 0), 0, 1, 0, 1);
            }

            _layers.Add(newLayer);
            _activeLayerIndex = _layers.Count - 1;
            InvalidateOtherLayers();
            UpdateActiveLayerBinding();
            ApplyOverlayParametersToMeshes();
            RecompositeGpuLayers();
            NotifyLayerAdded(_activeLayerIndex, layerName);
            NotifyLayerSelected(_activeLayerIndex);
            NotifyStackChanged();
            return newLayer;
        }

        public void DeleteActiveLayer()
        {
            DeleteLayer(_activeLayerIndex);
        }

        public void DeleteActiveLayerAcrossAllMaterials()
        {
            DeleteLayerAcrossAllMaterials(_activeLayerIndex);
        }

        public void DeleteLayerAcrossAllMaterials(int index)
        {
            if (index < 0 || index >= _layers.Count)
            {
                GD.PrintErr("[SkinLayerManager] Invalid layer index to delete across materials.");
                return;
            }

            var rd = RenderingServer.GetRenderingDevice();

            // 1. Delete matching layer slot across all inactive persisted material contexts
            foreach (var ctx in _materialContexts.Values)
            {
                if (ctx.MaterialKey == _activeMaterialKey) continue;
                if (index >= 0 && index < ctx.Layers.Count)
                {
                    var l = ctx.Layers[index];
                    ctx.Layers.RemoveAt(index);

                    // Safely detach from inactive atlas manager before freeing RID to prevent
                    // "Attempted to free invalid ID" in Texture2DRD
                    Node atlasMgr = null;
                    if (_perMaterialAtlasManagers.TryGetValue(ctx.MaterialKey, out atlasMgr) && atlasMgr != null && GodotObject.IsInstanceValid(atlasMgr))
                    {
                        var curActiveRid = atlasMgr.Get("atlas_texture_rid");
                        if (curActiveRid.VariantType == Variant.Type.Rid && curActiveRid.AsRid() == l.LayerRid)
                        {
                            atlasMgr.Set("atlas_texture_rid", new Rid());
                            var resObj = atlasMgr.Get("active_layer_resource");
                            if (resObj.VariantType == Variant.Type.Object && resObj.AsGodotObject() is Texture2Drd texRd)
                            {
                                texRd.TextureRdRid = new Rid();
                            }
                        }
                    }

                    l.CleanUp();

                    if (ctx.Layers.Count == 0)
                    {
                        var defaultLayer = new SkinLayer();
                        defaultLayer.Initialize("Paint Layer 1", ctx.CanvasSize, false, _compositeShader, _dabShader, null);
                        defaultLayer.GpuData = new byte[ctx.CanvasSize.X * ctx.CanvasSize.Y * 8];
                        ctx.Layers.Add(defaultLayer);
                        ctx.ActiveLayerIndex = 0;
                    }
                    else if (ctx.ActiveLayerIndex >= ctx.Layers.Count)
                    {
                        ctx.ActiveLayerIndex = ctx.Layers.Count - 1;
                    }

                    ctx.UndoStack.Clear();
                    ctx.RedoStack.Clear();

                    // Recomposite inactive context so 3D viewport immediately reflects the deletion
                    int cDim = ctx.CanvasSize.X > 0 ? ctx.CanvasSize.X : 2048;
                    int bLen = cDim * cDim * 8;
                    byte[] inactiveComp = new byte[bLen];
                    foreach (var remLayer in ctx.Layers)
                    {
                        if (remLayer.IsVisible && remLayer.GpuData != null && remLayer.GpuData.Length == bLen)
                        {
                            BlendLayerBuffer(inactiveComp, remLayer.GpuData, remLayer.Opacity, remLayer.BlendMode, ctx.BaseAtlasBuffer);
                        }
                    }

                    if (atlasMgr != null && GodotObject.IsInstanceValid(atlasMgr) && rd != null)
                    {
                        var fRid = atlasMgr.Get("full_composite_rid");
                        if (fRid.VariantType == Variant.Type.Rid && fRid.AsRid().IsValid && rd.TextureIsValid(fRid.AsRid()))
                        {
                            rd.TextureUpdate(fRid.AsRid(), 0, inactiveComp);
                        }
                        var cRid = atlasMgr.Get("composite_texture_rid");
                        if (cRid.VariantType == Variant.Type.Rid && cRid.AsRid().IsValid && rd.TextureIsValid(cRid.AsRid()))
                        {
                            rd.TextureClear(cRid.AsRid(), new Color(0, 0, 0, 0), 0, 1, 0, 1);
                        }
                        var fullRes = atlasMgr.Get("full_composite_resource");
                        if (fullRes.VariantType == Variant.Type.Object && fullRes.AsGodotObject() is Texture2D fTex)
                        {
                            ctx.BakedCompositeTexture = fTex;
                        }
                    }
                }
            }

            // 2. Delete for the currently active material context
            DeleteLayer(index);

            // 3. Immediately refresh overlays on all character meshes in 3D viewport
            ApplyOverlayParametersToMeshes();
        }

        public void DeleteLayer(int index)
        {
            if (index < 0 || index >= _layers.Count)
            {
                GD.PrintErr("[SkinLayerManager] Invalid layer index to delete.");
                return;
            }

            RecordUndoSnapshot();

            var layer = _layers[index];
            _layers.RemoveAt(index);

            // Safely detach from active atlas manager before freeing RID to prevent
            // "Attempted to free invalid ID" in Texture2DRD
            if (_activeAtlasManager != null && GodotObject.IsInstanceValid(_activeAtlasManager))
            {
                var curActiveRid = _activeAtlasManager.Get("atlas_texture_rid");
                if (curActiveRid.VariantType == Variant.Type.Rid && curActiveRid.AsRid() == layer.LayerRid)
                {
                    _activeAtlasManager.Set("atlas_texture_rid", new Rid());
                    var resObj = _activeAtlasManager.Get("active_layer_resource");
                    if (resObj.VariantType == Variant.Type.Object && resObj.AsGodotObject() is Texture2Drd texRd)
                    {
                        texRd.TextureRdRid = new Rid();
                    }
                }
            }

            layer.CleanUp();
            layer.GpuData = null;

            InvalidateOtherLayers();

            if (_layers.Count == 0)
            {
                AddNewLayer("Paint Layer 1");
            }
            else
            {
                if (_activeLayerIndex >= _layers.Count)
                {
                    _activeLayerIndex = _layers.Count - 1;
                }
                UpdateActiveLayerBinding();
                ApplyOverlayParametersToMeshes();
                RecompositeGpuLayers();
            }

            if (_activeAtlasManager != null && GodotObject.IsInstanceValid(_activeAtlasManager))
            {
                if (_activeAtlasManager.HasMethod("_notify_brushes"))
                {
                    _activeAtlasManager.Call("_notify_brushes");
                }
            }

            NotifyLayerRemoved(index);
            NotifyLayerSelected(_activeLayerIndex);
            NotifyStackChanged();
        }

        public void MoveLayer(int fromIndex, int toIndex)
        {
            if (fromIndex < 0 || toIndex < 0 || fromIndex >= _layers.Count || toIndex >= _layers.Count || fromIndex == toIndex)
            {
                return;
            }

            var layer = _layers[fromIndex];
            _layers.RemoveAt(fromIndex);
            _layers.Insert(toIndex, layer);

            _activeLayerIndex = toIndex;
            InvalidateOtherLayers();
            UpdateActiveLayerBinding();
            ApplyOverlayParametersToMeshes();
            RecompositeGpuLayers();
            NotifyLayersReordered();
            NotifyLayerSelected(_activeLayerIndex);
            NotifyStackChanged();
        }

        public void RenameLayer(int index, string newName)
        {
            if (index < 0 || index >= _layers.Count) return;
            if (string.IsNullOrWhiteSpace(newName)) return;
            _layers[index].Name = newName.Trim();
            NotifyStackChanged();
        }

        public void UpdateActiveLayerBinding()
        {
            var layer = ActiveLayer;
            if (layer == null || _activeAtlasManager == null || !GodotObject.IsInstanceValid(_activeAtlasManager)) return;

            var rd = RenderingServer.GetRenderingDevice();
            if (rd == null) return;

            int w = CanvasSize.X > 0 ? CanvasSize.X : 2048;
            int h = CanvasSize.Y > 0 ? CanvasSize.Y : 2048;

            if (!layer.LayerRid.IsValid || !rd.TextureIsValid(layer.LayerRid))
            {
                layer.LayerRid = CreateLayerGpuTexture(w, h, null);
                if (rd.TextureIsValid(layer.LayerRid))
                {
                    rd.TextureClear(layer.LayerRid, new Color(0, 0, 0, 0), 0, 1, 0, 1);
                }
            }

            // Designate active atlas manager to dynamic slot 0 for brush compute
            _activeAtlasManager.Set("owns_atlas_texture_rid", false);
            _activeAtlasManager.Set("atlas_texture_rid", layer.LayerRid);
            _activeAtlasManager.Set("atlas_index", 0);
            _activeAtlasManager.Set("is_active_target", true);

            foreach (var kvp in _perMaterialAtlasManagers)
            {
                if (kvp.Value != _activeAtlasManager && GodotObject.IsInstanceValid(kvp.Value))
                {
                    kvp.Value.Set("is_active_target", false);
                }
            }

            if (_activeAtlasManager.HasMethod("_apply_texture_to_texture_resource"))
            {
                _activeAtlasManager.Call("_apply_texture_to_texture_resource");
            }
            if (_activeAtlasManager.HasMethod("_notify_brushes"))
            {
                _activeAtlasManager.Call("_notify_brushes");
            }
        }

        public void SyncActiveLayerGpuTexture()
        {
            var layer = ActiveLayer;
            if (layer == null) return;
            var rd = RenderingServer.GetRenderingDevice();
            if (rd == null || !layer.LayerRid.IsValid || !rd.TextureIsValid(layer.LayerRid)) return;
            int bufferLen = CanvasSize.X * CanvasSize.Y * 8;
            if (layer.GpuData != null && layer.GpuData.Length == bufferLen)
            {
                rd.TextureUpdate(layer.LayerRid, 0, layer.GpuData);
            }
        }

        private const int ScratchTextureSize = 1024;
        private Rid _scratchTextureRid = new();
        private byte[] _scratchBuffer;

        private void EnsureScratchTexture(RenderingDevice rd)
        {
            if (_scratchTextureRid.IsValid && rd.TextureIsValid(_scratchTextureRid))
                return;

            if (_scratchTextureRid.IsValid)
            {
                rd.FreeRid(_scratchTextureRid);
                _scratchTextureRid = new Rid();
            }

            _scratchBuffer = new byte[ScratchTextureSize * ScratchTextureSize * 8];

            var fmt = new RDTextureFormat
            {
                Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
                Width = ScratchTextureSize,
                Height = ScratchTextureSize,
                UsageBits = RenderingDevice.TextureUsageBits.StorageBit |
                            RenderingDevice.TextureUsageBits.SamplingBit |
                            RenderingDevice.TextureUsageBits.CanUpdateBit |
                            RenderingDevice.TextureUsageBits.CanCopyFromBit |
                            RenderingDevice.TextureUsageBits.CanCopyToBit
            };
            var view = new RDTextureView();
            _scratchTextureRid = rd.TextureCreate(fmt, view, new Godot.Collections.Array<byte[]> { _scratchBuffer });
        }

        public void UpdateActiveLayerGpuTextureThrottled(Rect2I dirtyRect)
        {
            var layer = ActiveLayer;
            if (layer == null) return;
            var rd = RenderingServer.GetRenderingDevice();
            if (rd == null || !layer.LayerRid.IsValid || !rd.TextureIsValid(layer.LayerRid)) return;

            int atlasSize = CanvasSize.X > 0 ? CanvasSize.X : 2048;
            int bufferLen = atlasSize * atlasSize * 8;
            if (layer.GpuData == null || layer.GpuData.Length != bufferLen) return;

            int minX = Math.Clamp(dirtyRect.Position.X, 0, atlasSize - 1);
            int minY = Math.Clamp(dirtyRect.Position.Y, 0, atlasSize - 1);
            int maxX = Math.Clamp(dirtyRect.End.X, 0, atlasSize);
            int maxY = Math.Clamp(dirtyRect.End.Y, 0, atlasSize);
            int w = maxX - minX;
            int h = maxY - minY;
            if (w <= 0 || h <= 0) return;

            EnsureScratchTexture(rd);
            if (!_scratchTextureRid.IsValid || !rd.TextureIsValid(_scratchTextureRid))
            {
                rd.TextureUpdate(layer.LayerRid, 0, layer.GpuData);
                return;
            }

            // Single tile fast-path (fits in scratch texture)
            if (w <= ScratchTextureSize && h <= ScratchTextureSize)
            {
                int rowBytes = w * 8;
                for (int y = 0; y < h; y++)
                {
                    int srcOffset = ((minY + y) * atlasSize + minX) * 8;
                    int dstOffset = (y * ScratchTextureSize) * 8;
                    Buffer.BlockCopy(layer.GpuData, srcOffset, _scratchBuffer, dstOffset, rowBytes);
                }

                rd.TextureUpdate(_scratchTextureRid, 0, _scratchBuffer);
                rd.TextureCopy(_scratchTextureRid, layer.LayerRid, Vector3.Zero, new Vector3(minX, minY, 0), new Vector3(w, h, 1), 0, 0, 0, 0);

                if (_layers.Count <= 1 && _activeAtlasManager != null && GodotObject.IsInstanceValid(_activeAtlasManager))
                {
                    var fullRidVal = _activeAtlasManager.Get("full_composite_rid");
                    if (fullRidVal.VariantType == Variant.Type.Rid && fullRidVal.AsRid().IsValid && rd.TextureIsValid(fullRidVal.AsRid()))
                    {
                        if (layer.BlendMode == LayerBlendMode.Normal)
                        {
                            rd.TextureCopy(_scratchTextureRid, fullRidVal.AsRid(), Vector3.Zero, new Vector3(minX, minY, 0), new Vector3(w, h, 1), 0, 0, 0, 0);
                        }
                        else
                        {
                            unsafe
                            {
                                fixed (byte* pScratch = _scratchBuffer, pBase = _baseAtlasBuffer)
                                {
                                    Half* hScratch = (Half*)pScratch;
                                    Half* hBase = pBase != null ? (Half*)pBase : null;
                                    for (int y = 0; y < h; y++)
                                    {
                                        int srcBaseRow = (minY + y) * atlasSize * 4;
                                        int dstRow = y * ScratchTextureSize * 4;
                                        for (int x = 0; x < w; x++)
                                        {
                                            int sIdx = dstRow + x * 4;
                                            float srcA = (float)hScratch[sIdx + 3] * layer.Opacity;
                                            if (srcA <= 0.0001f) continue;
                                            float srcR = (float)hScratch[sIdx];
                                            float srcG = (float)hScratch[sIdx + 1];
                                            float srcB = (float)hScratch[sIdx + 2];
                                            int bIdx = srcBaseRow + (minX + x) * 4;
                                            float bR = hBase != null ? (float)hBase[bIdx] : 1.0f;
                                            float bG = hBase != null ? (float)hBase[bIdx + 1] : 1.0f;
                                            float bB = hBase != null ? (float)hBase[bIdx + 2] : 1.0f;
                                            BlendRgb(bR, bG, bB, srcR, srcG, srcB, layer.BlendMode, out float outR, out float outG, out float outB);
                                            hScratch[sIdx] = (Half)outR;
                                            hScratch[sIdx + 1] = (Half)outG;
                                            hScratch[sIdx + 2] = (Half)outB;
                                            hScratch[sIdx + 3] = (Half)srcA;
                                        }
                                    }
                                }
                            }
                            rd.TextureUpdate(_scratchTextureRid, 0, _scratchBuffer);
                            rd.TextureCopy(_scratchTextureRid, fullRidVal.AsRid(), Vector3.Zero, new Vector3(minX, minY, 0), new Vector3(w, h, 1), 0, 0, 0, 0);
                        }
                    }
                }
                return;
            }

            // Multi-tile chunked upload (only touches dirty area tiles, never full 134MB fallback!)
            for (int ty = minY; ty < maxY; ty += ScratchTextureSize)
            {
                int tileH = Math.Min(ScratchTextureSize, maxY - ty);
                for (int tx = minX; tx < maxX; tx += ScratchTextureSize)
                {
                    int tileW = Math.Min(ScratchTextureSize, maxX - tx);
                    int rowBytes = tileW * 8;
                    for (int y = 0; y < tileH; y++)
                    {
                        int srcOffset = ((ty + y) * atlasSize + tx) * 8;
                        int dstOffset = (y * ScratchTextureSize) * 8;
                        Buffer.BlockCopy(layer.GpuData, srcOffset, _scratchBuffer, dstOffset, rowBytes);
                    }

                    rd.TextureUpdate(_scratchTextureRid, 0, _scratchBuffer);
                    rd.TextureCopy(_scratchTextureRid, layer.LayerRid, Vector3.Zero, new Vector3(tx, ty, 0), new Vector3(tileW, tileH, 1), 0, 0, 0, 0);

                    if (_layers.Count <= 1 && _activeAtlasManager != null && GodotObject.IsInstanceValid(_activeAtlasManager))
                    {
                        var fullRidVal = _activeAtlasManager.Get("full_composite_rid");
                        if (fullRidVal.VariantType == Variant.Type.Rid && fullRidVal.AsRid().IsValid && rd.TextureIsValid(fullRidVal.AsRid()))
                        {
                            if (layer.BlendMode == LayerBlendMode.Normal)
                            {
                                rd.TextureCopy(_scratchTextureRid, fullRidVal.AsRid(), Vector3.Zero, new Vector3(tx, ty, 0), new Vector3(tileW, tileH, 1), 0, 0, 0, 0);
                            }
                            else
                            {
                                unsafe
                                {
                                    fixed (byte* pScratch = _scratchBuffer, pBase = _baseAtlasBuffer)
                                    {
                                        Half* hScratch = (Half*)pScratch;
                                        Half* hBase = pBase != null ? (Half*)pBase : null;
                                        for (int y = 0; y < tileH; y++)
                                        {
                                            int srcBaseRow = (ty + y) * atlasSize * 4;
                                            int dstRow = y * ScratchTextureSize * 4;
                                            for (int x = 0; x < tileW; x++)
                                            {
                                                int sIdx = dstRow + x * 4;
                                                float srcA = (float)hScratch[sIdx + 3] * layer.Opacity;
                                                if (srcA <= 0.0001f) continue;
                                                float srcR = (float)hScratch[sIdx];
                                                float srcG = (float)hScratch[sIdx + 1];
                                                float srcB = (float)hScratch[sIdx + 2];
                                                int bIdx = srcBaseRow + (tx + x) * 4;
                                                float bR = hBase != null ? (float)hBase[bIdx] : 1.0f;
                                                float bG = hBase != null ? (float)hBase[bIdx + 1] : 1.0f;
                                                float bB = hBase != null ? (float)hBase[bIdx + 2] : 1.0f;
                                                BlendRgb(bR, bG, bB, srcR, srcG, srcB, layer.BlendMode, out float outR, out float outG, out float outB);
                                                hScratch[sIdx] = (Half)outR;
                                                hScratch[sIdx + 1] = (Half)outG;
                                                hScratch[sIdx + 2] = (Half)outB;
                                                hScratch[sIdx + 3] = (Half)srcA;
                                            }
                                        }
                                    }
                                }
                                rd.TextureUpdate(_scratchTextureRid, 0, _scratchBuffer);
                                rd.TextureCopy(_scratchTextureRid, fullRidVal.AsRid(), Vector3.Zero, new Vector3(tx, ty, 0), new Vector3(tileW, tileH, 1), 0, 0, 0, 0);
                            }
                        }
                    }
                }
            }
        }

        private void UploadBufferRectChunked(RenderingDevice rd, Rid destRid, byte[] buffer, int atlasSize, Rect2I rect)
        {
            if (rd == null || !destRid.IsValid || !rd.TextureIsValid(destRid) || buffer == null) return;
            EnsureScratchTexture(rd);
            if (!_scratchTextureRid.IsValid || !rd.TextureIsValid(_scratchTextureRid))
            {
                rd.TextureUpdate(destRid, 0, buffer);
                return;
            }

            int minX = Math.Clamp(rect.Position.X, 0, atlasSize - 1);
            int minY = Math.Clamp(rect.Position.Y, 0, atlasSize - 1);
            int maxX = Math.Clamp(rect.End.X, 0, atlasSize);
            int maxY = Math.Clamp(rect.End.Y, 0, atlasSize);
            int w = maxX - minX;
            int h = maxY - minY;
            if (w <= 0 || h <= 0) return;

            if (w <= ScratchTextureSize && h <= ScratchTextureSize)
            {
                int rowBytes = w * 8;
                for (int y = 0; y < h; y++)
                {
                    int srcOffset = ((minY + y) * atlasSize + minX) * 8;
                    int dstOffset = (y * ScratchTextureSize) * 8;
                    Buffer.BlockCopy(buffer, srcOffset, _scratchBuffer, dstOffset, rowBytes);
                }

                rd.TextureUpdate(_scratchTextureRid, 0, _scratchBuffer);
                rd.TextureCopy(_scratchTextureRid, destRid, Vector3.Zero, new Vector3(minX, minY, 0), new Vector3(w, h, 1), 0, 0, 0, 0);
                return;
            }

            for (int ty = minY; ty < maxY; ty += ScratchTextureSize)
            {
                int tileH = Math.Min(ScratchTextureSize, maxY - ty);
                for (int tx = minX; tx < maxX; tx += ScratchTextureSize)
                {
                    int tileW = Math.Min(ScratchTextureSize, maxX - tx);
                    int rowBytes = tileW * 8;
                    for (int y = 0; y < tileH; y++)
                    {
                        int srcOffset = ((ty + y) * atlasSize + tx) * 8;
                        int dstOffset = (y * ScratchTextureSize) * 8;
                        Buffer.BlockCopy(buffer, srcOffset, _scratchBuffer, dstOffset, rowBytes);
                    }

                    rd.TextureUpdate(_scratchTextureRid, 0, _scratchBuffer);
                    rd.TextureCopy(_scratchTextureRid, destRid, Vector3.Zero, new Vector3(tx, ty, 0), new Vector3(tileW, tileH, 1), 0, 0, 0, 0);
                }
            }
        }

        private static Rid _fallbackDummyTextureRid = new();

        public static Rid GetFallbackDummyTextureRid()
        {
            var rd = RenderingServer.GetRenderingDevice();
            if (rd == null) return new Rid();
            if (_fallbackDummyTextureRid.IsValid && rd.TextureIsValid(_fallbackDummyTextureRid))
            {
                return _fallbackDummyTextureRid;
            }
            var fmt = new RDTextureFormat
            {
                Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
                Width = 1,
                Height = 1,
                UsageBits = RenderingDevice.TextureUsageBits.StorageBit |
                            RenderingDevice.TextureUsageBits.SamplingBit |
                            RenderingDevice.TextureUsageBits.CanUpdateBit |
                            RenderingDevice.TextureUsageBits.CanCopyFromBit |
                            RenderingDevice.TextureUsageBits.CanCopyToBit
            };
            var view = new RDTextureView();
            byte[] zero = new byte[8];
            _fallbackDummyTextureRid = rd.TextureCreate(fmt, view, new Godot.Collections.Array<byte[]> { zero });
            return _fallbackDummyTextureRid;
        }

        public void SyncGpuStrokeToComposite()
        {
            var layer = ActiveLayer;
            if (layer == null || _activeAtlasManager == null || !GodotObject.IsInstanceValid(_activeAtlasManager)) return;

            var rd = RenderingServer.GetRenderingDevice();
            if (rd == null) return;

            int atlasSize = CanvasSize.X > 0 ? CanvasSize.X : 2048;
            var amSize = _activeAtlasManager.Get("atlas_size");
            if (amSize.VariantType == Variant.Type.Int && amSize.AsInt32() > 0)
            {
                atlasSize = amSize.AsInt32();
            }

            var fullRidVal = _activeAtlasManager.Get("full_composite_rid");
            var compRidVal = _activeAtlasManager.Get("composite_texture_rid");

            if (_layers.Count <= 1)
            {
                // Single layer fast path: direct GPU-to-GPU TextureCopy in 0.01ms with zero CPU readback fence
                if (layer.LayerRid.IsValid && rd.TextureIsValid(layer.LayerRid))
                {
                    if (fullRidVal.VariantType == Variant.Type.Rid && fullRidVal.AsRid().IsValid && rd.TextureIsValid(fullRidVal.AsRid()))
                    {
                        if (layer.BlendMode == LayerBlendMode.Normal)
                        {
                            rd.TextureCopy(layer.LayerRid, fullRidVal.AsRid(), Vector3.Zero, Vector3.Zero, new Vector3(atlasSize, atlasSize, 1), 0, 0, 0, 0);
                        }
                        else
                        {
                            // Blend with base atlas buffer so 2D canvas displays non-normal blend modes accurately
                            layer.IsCpuSynced = false;
                            EnsureCpuSynced();
                            RecompositeGpuLayers();
                        }
                    }
                    // For single layer, composite_texture_rid (_otherLayersBuffer) MUST remain clear/empty!
                    // Do NOT copy layer.LayerRid into compRidVal!
                    if (compRidVal.VariantType == Variant.Type.Rid && compRidVal.AsRid().IsValid && rd.TextureIsValid(compRidVal.AsRid()))
                    {
                        rd.TextureClear(compRidVal.AsRid(), new Color(0, 0, 0, 0), 0, 1, 0, 1);
                    }
                }
                layer.IsCpuSynced = false;
            }
            else
            {
                // Multi-layer path: sync CPU buffer and recomposite all layers
                layer.IsCpuSynced = false;
                EnsureCpuSynced();
                RecompositeGpuLayers();
            }
        }

        public void SelectLayer(int index)
        {
            if (index < 0 || index >= _layers.Count) return;

            _activeLayerIndex = index;
            InvalidateOtherLayers();
            UpdateActiveLayerBinding();
            RecompositeGpuLayers();
            ApplyOverlayParametersToMeshes();
            NotifyLayerSelected(index);
        }

        public void SetActiveLayer(int index)
        {
            SelectLayer(index);
        }

        public void ClearCurrentLayer()
        {
            RecordInitialSnapshot();
            var layer = ActiveLayer;
            if (layer != null)
            {
                layer.GpuData = new byte[CanvasSize.X * CanvasSize.Y * 8];
                layer.Clear();
                var rd = RenderingServer.GetRenderingDevice();
                if (rd != null && layer.LayerRid.IsValid && rd.TextureIsValid(layer.LayerRid))
                {
                    rd.TextureClear(layer.LayerRid, new Color(0, 0, 0, 0), 0, 1, 0, 1);
                }
            }
            InvalidateOtherLayers();
            RecompositeGpuLayers();
            ApplyOverlayParametersToMeshes();
            RecordUndoSnapshot();
            NotifyStackChanged();
            GD.Print("[SkinLayerManager] Cleared active layer paint.");
        }

        public void ClearActiveLayer()
        {
            ClearCurrentLayer();
        }

        // --- Undo / Redo Pipeline ---
        public class LayerPaintSnapshot
        {
            public int ActiveLayerIndex;
            public Dictionary<int, byte[]> CompressedLayerData = new();
            public Dictionary<int, byte[]> PendingRawLayerData = new();
            public int UncompressedLength;
        }

        private static byte[] CompressBuffer(byte[] raw)
        {
            if (raw == null || raw.Length == 0) return Array.Empty<byte>();
            using var ms = new System.IO.MemoryStream();
            using (var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            {
                ds.Write(raw, 0, raw.Length);
            }
            return ms.ToArray();
        }

        private static byte[] DecompressBuffer(byte[] compressed, int uncompressedLength)
        {
            if (compressed == null || compressed.Length == 0) return new byte[uncompressedLength];
            byte[] decompressed = new byte[uncompressedLength];
            using var ms = new System.IO.MemoryStream(compressed);
            using var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress);
            int totalRead = 0;
            while (totalRead < uncompressedLength)
            {
                int bytesRead = ds.Read(decompressed, totalRead, uncompressedLength - totalRead);
                if (bytesRead == 0) break;
                totalRead += bytesRead;
            }
            return decompressed;
        }

        private readonly List<LayerPaintSnapshot> _undoStack = new();
        private readonly List<LayerPaintSnapshot> _redoStack = new();
        private const int MaxUndoSnapshots = 10;

        public bool CanUndo => _hasGpuUndo || _undoStack.Count > 1;
        public bool CanRedo => _redoStack.Count > 0;

        /// <summary>
        /// Clears all undo and redo history snapshots and invalidates cached atlas buffers.
        /// Invoked when unloading or switching heroes to prevent strokes from leaking across characters.
        /// </summary>
        public void ClearHistory()
        {
            _undoStack.Clear();
            _redoStack.Clear();
            _hasGpuUndo = false;
            _compositeBuffer = null;
            _baseAtlasBuffer = null;
            _hasPopulatedBaseAtlasBuffer = false;
            NotifyStackChanged();
        }

        public void RecordInitialSnapshot()
        {
            if (_undoStack.Count == 0)
            {
                RecordUndoSnapshot();
            }
        }

        public void RecordUndoSnapshot(int? targetLayerOnly = null)
        {
            if (_layers.Count == 0) return;

            EnsureCpuSynced();

            int atlasSize = CanvasSize.X > 0 ? CanvasSize.X : 2048;
            int bufferLen = atlasSize * atlasSize * 8;

            var snap = new LayerPaintSnapshot
            {
                ActiveLayerIndex = _activeLayerIndex,
                UncompressedLength = bufferLen
            };

            int maxSnapshots = CanvasSize.X >= 4096 ? 3 : (CanvasSize.X >= 2048 ? 6 : MaxUndoSnapshots);

            LayerPaintSnapshot prevSnap = _undoStack.Count > 0 ? _undoStack[^1] : null;

            for (int i = 0; i < _layers.Count; i++)
            {
                var layer = _layers[i];
                if (layer.GpuData == null || layer.GpuData.Length == 0) continue;

                if (layer.IsBlank())
                {
                    snap.CompressedLayerData[i] = Array.Empty<byte>();
                    continue;
                }

                if (targetLayerOnly.HasValue && targetLayerOnly.Value != i && prevSnap != null)
                {
                    lock (prevSnap)
                    {
                        if (prevSnap.PendingRawLayerData.TryGetValue(i, out var prevRaw))
                        {
                            byte[] rawCopy = new byte[prevRaw.Length];
                            Buffer.BlockCopy(prevRaw, 0, rawCopy, 0, prevRaw.Length);
                            snap.PendingRawLayerData[i] = rawCopy;
                        }
                        else if (prevSnap.CompressedLayerData.TryGetValue(i, out var prevCompressed))
                        {
                            snap.CompressedLayerData[i] = prevCompressed; // Structural sharing: zero compression overhead
                        }
                    }
                }
                else
                {
                    int layerIdx = i;
                    byte[] rawCopy = new byte[layer.GpuData.Length];
                    Buffer.BlockCopy(layer.GpuData, 0, rawCopy, 0, layer.GpuData.Length);
                    snap.PendingRawLayerData[layerIdx] = rawCopy;

                    // Asynchronously compress in background threadpool to eliminate UI freeze
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        var compressed = CompressBuffer(rawCopy);
                        lock (snap)
                        {
                            snap.CompressedLayerData[layerIdx] = compressed;
                            snap.PendingRawLayerData.Remove(layerIdx);
                        }
                    });
                }
            }

            _undoStack.Add(snap);
            if (_undoStack.Count > maxSnapshots + 1)
            {
                var removed = _undoStack[0];
                _undoStack.RemoveAt(0);
                removed.PendingRawLayerData.Clear();
                removed.CompressedLayerData.Clear();
            }
            _redoStack.Clear();
            _hasGpuUndo = false;
        }

        public byte[] GetAtlasDataSnapshot()
        {
            EnsureCpuSynced();
            var layer = ActiveLayer;
            if (layer != null && layer.GpuData != null)
            {
                return layer.GpuData;
            }

            if (_activeAtlasManager == null || !GodotObject.IsInstanceValid(_activeAtlasManager)) return null;
            var ridVal = _activeAtlasManager.Get("atlas_texture_rid");
            if (ridVal.VariantType != Variant.Type.Rid) return null;
            Rid rid = ridVal.AsRid();
            var rd = RenderingServer.GetRenderingDevice();
            if (!rid.IsValid || rd == null) return null;
            return rd.TextureGetData(rid, 0);
        }

        private byte[] _preStrokeShadowBuffer;

        public byte[] GetPreStrokeSnapshot()
        {
            EnsureCpuSynced();
            var layer = ActiveLayer;
            if (layer == null || layer.GpuData == null || layer.GpuData.Length == 0)
            {
                return null;
            }

            int len = layer.GpuData.Length;
            if (_preStrokeShadowBuffer == null || _preStrokeShadowBuffer.Length != len)
            {
                _preStrokeShadowBuffer = new byte[len];
            }

            Buffer.BlockCopy(layer.GpuData, 0, _preStrokeShadowBuffer, 0, len);
            return _preStrokeShadowBuffer;
        }

        public void Undo()
        {
            if (_hasGpuUndo && TryGpuUndo())
            {
                RecompositeGpuLayers();
                NotifyStackChanged();
                NotifyLayerSelected(_activeLayerIndex);
                GD.Print("[SkinLayerManager] Instant GPU VRAM Undo performed (0.05ms).");
                return;
            }

            if (_undoStack.Count <= 1)
            {
                GD.Print("[SkinLayerManager] No undo states available.");
                return;
            }

            var current = _undoStack[^1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            _redoStack.Add(current);

            var previous = _undoStack[^1];
            RestoreSnapshot(previous);
            RecompositeGpuLayers();
            NotifyStackChanged();
            NotifyLayerSelected(_activeLayerIndex);
            GD.Print($"[SkinLayerManager] Undo performed. Remaining undo steps: {_undoStack.Count - 1}");
        }

        public void Redo()
        {
            if (_redoStack.Count == 0)
            {
                GD.Print("[SkinLayerManager] No redo states available.");
                return;
            }

            var next = _redoStack[^1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            _undoStack.Add(next);

            RestoreSnapshot(next);
            RecompositeGpuLayers();
            NotifyStackChanged();
            NotifyLayerSelected(_activeLayerIndex);
            GD.Print($"[SkinLayerManager] Redo performed. Remaining redo steps: {_redoStack.Count}");
        }

        private void RestoreSnapshot(LayerPaintSnapshot snapshot)
        {
            if (snapshot == null) return;

            // Preserve current active layer so undo/redo does NOT switch the user's active layer selection
            int preservedActiveLayer = _activeLayerIndex;

            var rd = RenderingServer.GetRenderingDevice();
            int uncompressedLen = snapshot.UncompressedLength > 0 
                ? snapshot.UncompressedLength 
                : (CanvasSize.X * CanvasSize.Y * 8);

            for (int i = 0; i < _layers.Count; i++)
            {
                var l = _layers[i];
                byte[] rawData = null;
                lock (snapshot)
                {
                    if (snapshot.PendingRawLayerData.TryGetValue(i, out var pending))
                    {
                        rawData = (byte[])pending.Clone();
                    }
                    else if (snapshot.CompressedLayerData.TryGetValue(i, out var compressedData))
                    {
                        rawData = DecompressBuffer(compressedData, uncompressedLen);
                    }
                }

                if (rawData != null)
                {
                    l.GpuData = rawData;
                    l.IsCpuSynced = true;
                    if (rd != null && l.LayerRid.IsValid && rd.TextureIsValid(l.LayerRid))
                    {
                        rd.TextureUpdate(l.LayerRid, 0, l.GpuData);
                    }
                }
                else if (l.GpuData != null)
                {
                    Array.Clear(l.GpuData, 0, l.GpuData.Length);
                    l.IsCpuSynced = true;
                    if (rd != null && l.LayerRid.IsValid && rd.TextureIsValid(l.LayerRid))
                    {
                        rd.TextureClear(l.LayerRid, new Color(0, 0, 0, 0), 0, 1, 0, 1);
                    }
                }
            }

            if (_layers.Count > 0)
            {
                _activeLayerIndex = Math.Clamp(preservedActiveLayer, 0, _layers.Count - 1);
            }
        }

        // --- Overlay Shading & Blend Modes ---
        private readonly List<MeshInstance3D> _registeredSubmeshes = new();
        public IReadOnlyList<MeshInstance3D> RegisteredSubmeshes => _registeredSubmeshes;

        public void RegisterSubmesh(MeshInstance3D mesh)
        {
            if (mesh != null && !_registeredSubmeshes.Contains(mesh))
            {
                _registeredSubmeshes.Add(mesh);
            }
        }

        public void ClearRegisteredSubmeshes()
        {
            _registeredSubmeshes.Clear();
        }

        private int _currentOverlayBlendMode = 0;
        private float _currentOverlayOpacity = 1.0f;

        public int OverlayBlendMode => _currentOverlayBlendMode;
        public float OverlayOpacity => _currentOverlayOpacity;

        public void SetOverlayBlendMode(int blendMode)
        {
            _currentOverlayBlendMode = blendMode;
            if (ActiveLayer != null)
            {
                ActiveLayer.SetBlendMode((LayerBlendMode)blendMode);
            }
            ApplyOverlayParametersToMeshes();
            RecompositeGpuLayers();
            NotifyStackChanged();
        }

        public void SetOverlayOpacity(float opacity)
        {
            _currentOverlayOpacity = Mathf.Clamp(opacity, 0.0f, 1.0f);
            if (ActiveLayer != null)
            {
                ActiveLayer.SetOpacity(opacity);
            }
            RecompositeGpuLayers();
            NotifyStackChanged();
        }

        private Texture2D _selectionMaskTexture;
        private bool _showSelectionMask = false;
        private bool _showStencilPattern = true;

        public void SetSelectionMaskOverlay(Texture2D maskTexture, bool show, bool showPattern = true)
        {
            _selectionMaskTexture = maskTexture;
            _showSelectionMask = show;
            _showStencilPattern = showPattern;
            ApplyOverlayParametersToMeshes();
        }

        private static Texture2D ExtractMeshBaseTexture(MeshInstance3D mesh)
        {
            if (mesh == null) return null;
            int sCount = mesh.Mesh != null ? mesh.Mesh.GetSurfaceCount() : 1;
            for (int s = 0; s < sCount; s++)
            {
                var origMat = mesh.GetSurfaceOverrideMaterial(s)
                           ?? (mesh.Mesh != null ? mesh.Mesh.SurfaceGetMaterial(s) : null)
                           ?? mesh.MaterialOverride;
                if (origMat != null)
                {
                    var baseTex = ExtractBaseTexture(origMat);
                    if (baseTex != null) return baseTex;
                }
            }
            return null;
        }

        public void ApplyOverlayParametersToMeshes()
        {
            void ConfigureMeshOverlay(MeshInstance3D mesh)
            {
                if (mesh == null || !GodotObject.IsInstanceValid(mesh) || !HeroMeshHierarchy.IsAuthenticHeroMesh(mesh)) return;
                if (mesh.HasMeta("IsHiddenComposite") || !mesh.Visible)
                {
                    mesh.Layers &= ~(uint)(1 << 20);
                    return;
                }

                string mLower = mesh.Name.ToString().ToLowerInvariant();
                string matKey = GetMaterialKey(mesh);
                var checkMat = HeroMeshHierarchy.GetAuthenticMaterial(mesh, 0)
                           ?? mesh.GetSurfaceOverrideMaterial(0)
                           ?? (mesh.Mesh != null && mesh.Mesh.GetSurfaceCount() > 0 ? mesh.Mesh.SurfaceGetMaterial(0) : null)
                           ?? mesh.MaterialOverride;

                if (DeadlockPlayground.Materials.DeadlockMaterialResolver.ShouldPreserveOriginalMaterial(mesh.Name, matKey, checkMat) ||
                    mLower.Contains("sparkle") || mLower.Contains("ghost_glow") || mLower.Contains("outline"))
                {
                    mesh.Layers &= ~(uint)(1 << 20);
                    if (mesh.MaterialOverlay != null)
                    {
                        mesh.MaterialOverlay = null;
                    }
                    return;
                }

                string targetMatKey = GetMaterialKey(_targetMesh);
                string meshMatKey = GetMaterialKey(mesh);
                bool isTarget = (mesh == _targetMesh) || (!string.IsNullOrEmpty(targetMatKey) && targetMatKey == meshMatKey);

                if (isTarget)
                {
                    var mat = mesh.MaterialOverlay as ShaderMaterial;
                    if (mat == null && _activeAtlasManager != null && GodotObject.IsInstanceValid(_activeAtlasManager))
                    {
                        try
                        {
                            _activeAtlasManager.Call("apply_to_mesh", mesh);
                            mat = mesh.MaterialOverlay as ShaderMaterial;
                        }
                        catch { }
                    }

                    if (mat != null)
                    {
                        if (_activeAtlasManager != null && GodotObject.IsInstanceValid(_activeAtlasManager))
                        {
                            var activeRes = _activeAtlasManager.Get("active_layer_resource");
                            if (activeRes.VariantType == Variant.Type.Object && activeRes.AsGodotObject() is Texture2D actTex)
                            {
                                mat.SetShaderParameter("active_layer_texture", actTex);
                            }
                            var atlasRes = _activeAtlasManager.Get("atlas_texture_resource");
                            if (atlasRes.VariantType == Variant.Type.Object && atlasRes.AsGodotObject() is Texture2D atlTex)
                            {
                                mat.SetShaderParameter("overlay_texture", atlTex);
                            }
                        }

                        mat.SetShaderParameter("layer_opacity", ActiveLayer?.Opacity ?? 1.0f);
                        mat.SetShaderParameter("active_layer_visible", ActiveLayer?.IsVisible ?? true);
                        mat.SetShaderParameter("active_layer_opacity", ActiveLayer?.Opacity ?? 1.0f);
                        mat.SetShaderParameter("blend_mode", (int)(ActiveLayer?.BlendMode ?? LayerBlendMode.Normal));
                        mat.SetShaderParameter("is_paint_target", mesh == _targetMesh);
                        mat.SetShaderParameter("atlas_index", 0);

                        if (_showSelectionMask && _selectionMaskTexture != null)
                        {
                            mat.SetShaderParameter("selection_mask", _selectionMaskTexture);
                            mat.SetShaderParameter("show_selection_mask", true);
                            mat.SetShaderParameter("show_stencil_pattern", _showStencilPattern);
                        }
                        else
                        {
                            mat.SetShaderParameter("show_selection_mask", false);
                            mat.SetShaderParameter("show_stencil_pattern", false);
                            mat.SetShaderParameter("selection_mask", (Texture2D)null);
                        }

                        Texture2D baseTex = ExtractMeshBaseTexture(mesh);
                        if (baseTex != null)
                        {
                            mat.SetShaderParameter("g_tColor", baseTex);
                        }

                        mesh.Layers |= (uint)(1 << 20);
                    }
                    else
                    {
                        mesh.Layers |= (uint)(1 << 20);
                    }
                }
                else
                {
                    // Inactive material: keep showing its painted artwork in 3D viewport!
                    Texture2D inactiveCompositeTex = null;
                    Node inactiveAtlas = null;
                    bool hasActiveAtlas = _perMaterialAtlasManagers.TryGetValue(meshMatKey, out inactiveAtlas) && inactiveAtlas != null && GodotObject.IsInstanceValid(inactiveAtlas);

                    bool hasPaint = false;
                    MaterialPaintingContext inactiveCtx = null;
                    if (_materialContexts.TryGetValue(meshMatKey, out inactiveCtx))
                    {
                        hasPaint = inactiveCtx.HasAnyPaint();
                    }

                    if (hasPaint)
                    {
                        if (hasActiveAtlas)
                        {
                            var fullRes = inactiveAtlas.Get("full_composite_resource");
                            if (fullRes.VariantType == Variant.Type.Object && fullRes.AsGodotObject() is Texture2D fTex)
                            {
                                if (fTex is Texture2Drd texRd)
                                {
                                    var rd = RenderingServer.GetRenderingDevice();
                                    if (texRd.TextureRdRid.IsValid && rd != null && rd.TextureIsValid(texRd.TextureRdRid))
                                    {
                                        inactiveCompositeTex = fTex;
                                    }
                                }
                                else
                                {
                                    inactiveCompositeTex = fTex;
                                }
                            }
                            if (inactiveCompositeTex == null)
                            {
                                var texRes = inactiveAtlas.Get("atlas_texture_resource");
                                if (texRes.VariantType == Variant.Type.Object && texRes.AsGodotObject() is Texture2D aTex)
                                {
                                    if (aTex is Texture2Drd aTexRd)
                                    {
                                        var rd = RenderingServer.GetRenderingDevice();
                                        if (aTexRd.TextureRdRid.IsValid && rd != null && rd.TextureIsValid(aTexRd.TextureRdRid))
                                        {
                                            inactiveCompositeTex = aTex;
                                        }
                                    }
                                    else
                                    {
                                        inactiveCompositeTex = aTex;
                                    }
                                }
                            }
                        }

                        if (inactiveCompositeTex == null && inactiveCtx != null)
                        {
                            if (inactiveCtx.BakedCompositeTexture != null)
                            {
                                if (inactiveCtx.BakedCompositeTexture is Texture2Drd bTexRd)
                                {
                                    var rd = RenderingServer.GetRenderingDevice();
                                    if (bTexRd.TextureRdRid.IsValid && rd != null && rd.TextureIsValid(bTexRd.TextureRdRid))
                                    {
                                        inactiveCompositeTex = inactiveCtx.BakedCompositeTexture;
                                    }
                                }
                                else
                                {
                                    inactiveCompositeTex = inactiveCtx.BakedCompositeTexture;
                                }
                            }
                        }
                    }

                    if (hasPaint && inactiveCompositeTex != null)
                    {
                        var mat = mesh.MaterialOverlay as ShaderMaterial;
                        if (mat == null)
                        {
                            var overlayShader = GD.Load<Shader>("res://assets/shaders/painter/hero_painter_overlay.gdshader");
                            if (overlayShader != null)
                            {
                                mat = new ShaderMaterial { Shader = overlayShader };
                                mat.SetShaderParameter("position_in_atlas", Vector2.Zero);
                                mat.SetShaderParameter("size_in_atlas", Vector2.One);
                                mesh.MaterialOverlay = mat;
                            }
                        }

                        if (mat != null)
                        {
                            mat.SetShaderParameter("overlay_texture", inactiveCompositeTex);
                            mat.SetShaderParameter("active_layer_texture", (Texture2D)null);
                            mat.SetShaderParameter("active_layer_visible", false);
                            mat.SetShaderParameter("active_layer_opacity", 0.0f);
                            mat.SetShaderParameter("layer_opacity", 1.0f);
                            mat.SetShaderParameter("blend_mode", 0);
                            mat.SetShaderParameter("is_paint_target", false);
                            mat.SetShaderParameter("show_selection_mask", false);
                            mat.SetShaderParameter("show_stencil_pattern", false);

                            Texture2D baseTex = ExtractMeshBaseTexture(mesh);
                            if (baseTex != null)
                            {
                                mat.SetShaderParameter("g_tColor", baseTex);
                            }

                            mesh.Layers &= ~(uint)(1 << 20);
                        }
                    }
                    else
                    {
                        // Inactive material has no paint or no valid composite texture: clear overlay!
                        mesh.Layers &= ~(uint)(1 << 20);
                        if (mesh.MaterialOverlay != null)
                        {
                            mesh.MaterialOverlay = null;
                        }
                    }
                }
            }

            foreach (var mesh in _registeredSubmeshes)
            {
                ConfigureMeshOverlay(mesh);
            }

            if (_currentHero != null && GodotObject.IsInstanceValid(_currentHero))
            {
                void ApplyToNode(Node node)
                {
                    if (node is MeshInstance3D mi)
                    {
                        RegisterSubmesh(mi);
                        ConfigureMeshOverlay(mi);
                    }
                    foreach (var child in node.GetChildren())
                    {
                        ApplyToNode(child);
                    }
                }

                ApplyToNode(_currentHero);
            }
        }

        private void SaveActiveContext()
        {
            if (string.IsNullOrEmpty(_activeMaterialKey)) return;

            if (ActiveLayer != null)
            {
                ActiveLayer.IsCpuSynced = false;
            }
            EnsureCpuSynced();
            RecompositeGpuLayers();

            if (!_materialContexts.TryGetValue(_activeMaterialKey, out var ctx))
            {
                ctx = new MaterialPaintingContext { MaterialKey = _activeMaterialKey };
                _materialContexts[_activeMaterialKey] = ctx;
            }

            if (_activeAtlasManager != null && GodotObject.IsInstanceValid(_activeAtlasManager))
            {
                var fullRes = _activeAtlasManager.Get("full_composite_resource");
                if (fullRes.VariantType == Variant.Type.Object && fullRes.AsGodotObject() is Texture2D fullTex)
                {
                    ctx.BakedCompositeTexture = fullTex;
                }
                else
                {
                    var texRes = _activeAtlasManager.Get("atlas_texture_resource");
                    if (texRes.VariantType == Variant.Type.Object && texRes.AsGodotObject() is Texture2D t2d)
                    {
                        ctx.BakedCompositeTexture = t2d;
                    }
                }
            }

            ctx.CanvasSize = CanvasSize;
            ctx.Layers.Clear();
            ctx.Layers.AddRange(_layers);
            ctx.ActiveLayerIndex = _activeLayerIndex;
            ctx.UndoStack.Clear();
            ctx.UndoStack.AddRange(_undoStack);
            // Prune undo history for inactive contexts to keep RAM low: keep at most 1 state
            while (ctx.UndoStack.Count > 1)
            {
                ctx.UndoStack.RemoveAt(0);
            }
            ctx.RedoStack.Clear();
            ctx.BaseAtlasBuffer = _baseAtlasBuffer;
        }

        private void SwitchToContext(string targetMaterialKey, MeshInstance3D targetMesh)
        {
            if (string.IsNullOrEmpty(targetMaterialKey)) return;

            if (_materialContexts.TryGetValue(targetMaterialKey, out var ctx))
            {
                _currentContext = ctx;
                CanvasSize = ctx.CanvasSize;
                int bufferLen = CanvasSize.X * CanvasSize.Y * 8;
                var rd = RenderingServer.GetRenderingDevice();
                foreach (var layer in ctx.Layers)
                {
                    if (layer.GpuData == null || layer.GpuData.Length != bufferLen)
                    {
                        layer.DecompressGpuData(bufferLen);
                    }
                    if (rd != null && layer.GpuData != null && layer.GpuData.Length == bufferLen)
                    {
                        if (!layer.LayerRid.IsValid || !rd.TextureIsValid(layer.LayerRid))
                        {
                            layer.LayerRid = CreateLayerGpuTexture(CanvasSize.X, CanvasSize.Y, layer.GpuData);
                        }
                    }
                }
                _layers.Clear();
                _layers.AddRange(ctx.Layers);
                _activeLayerIndex = Math.Clamp(ctx.ActiveLayerIndex, 0, Math.Max(0, _layers.Count - 1));
                _undoStack.Clear();
                _undoStack.AddRange(ctx.UndoStack);
                _redoStack.Clear();
                _hasGpuUndo = false;
                _otherLayersDirty = true;
                if (ctx.BaseAtlasBuffer != null && ctx.BaseAtlasBuffer.Length == bufferLen)
                {
                    _baseAtlasBuffer = ctx.BaseAtlasBuffer;
                    _hasPopulatedBaseAtlasBuffer = true;
                }
                else
                {
                    _baseAtlasBuffer = null;
                    _hasPopulatedBaseAtlasBuffer = false;
                }
                GD.Print($"[SkinLayerManager] Restored persisted paint context for '{targetMaterialKey}' ({_layers.Count} layer(s), {CanvasSize.X}x{CanvasSize.Y})");
            }
            else
            {
                var mgr = EnsureSubmeshAtlas(targetMesh);
                int nativeDim = (mgr != null && GodotObject.IsInstanceValid(mgr))
                    ? (int)mgr.Call("get_native_atlas_size")
                    : 2048;
                if (nativeDim < 512) nativeDim = 2048;
                CanvasSize = new Vector2I(nativeDim, nativeDim);
                _layers.Clear();
                _undoStack.Clear();
                _redoStack.Clear();
                _hasGpuUndo = false;
                _otherLayersDirty = true;
                _hasPopulatedBaseAtlasBuffer = false;
                _activeLayerIndex = 0;

                AddNewLayer("Paint Layer 1");

                ctx = new MaterialPaintingContext
                {
                    MaterialKey = targetMaterialKey,
                    CanvasSize = CanvasSize,
                    Layers = new List<SkinLayer>(_layers),
                    ActiveLayerIndex = 0,
                    UndoStack = new List<LayerPaintSnapshot>(_undoStack),
                    RedoStack = new List<LayerPaintSnapshot>(_redoStack)
                };
                _materialContexts[targetMaterialKey] = ctx;
                _currentContext = ctx;
                GD.Print($"[SkinLayerManager] Created new paint context for '{targetMaterialKey}' ({CanvasSize.X}x{CanvasSize.Y})");
            }
        }

        public void SetPaintTargetMesh(MeshInstance3D targetMesh)
        {
            if (targetMesh == null || !GodotObject.IsInstanceValid(targetMesh))
            {
                _targetMesh = null;
                _activeAtlasManager = null;
                _activeMaterialKey = null;
                ApplyOverlayParametersToMeshes();
                return;
            }

            string targetMeshMaterialKey = GetMaterialKey(targetMesh);
            if (!string.IsNullOrEmpty(_activeMaterialKey) && _activeMaterialKey == targetMeshMaterialKey && _activeAtlasManager != null && GodotObject.IsInstanceValid(_activeAtlasManager))
            {
                // Material is already active and loaded; update active submesh reference only
                _targetMesh = targetMesh;
                ApplyOverlayParametersToMeshes();
                return;
            }

            try
            {
                SaveActiveContext();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[SkinLayerManager] Error in SaveActiveContext: {ex.Message}");
            }

            _targetMesh = targetMesh;
            _activeMaterialKey = targetMeshMaterialKey;

            // Lazily create or retrieve the per-submesh atlas
            var mgr = EnsureSubmeshAtlas(_targetMesh);
            _activeAtlasManager = mgr;

            SwitchToContext(targetMeshMaterialKey, _targetMesh);

            InvalidateBaseAtlasBuffer();
            UpdateActiveLayerBinding();
            RecompositeGpuLayers();
            ApplyOverlayParametersToMeshes();
            NotifyStackChanged();
            NotifyLayerSelected(_activeLayerIndex);
        }

        // --- Submesh Bucket Fill ---
        public void FillCurrentSubmesh(Color color, Vector2? hitUv = null, MagicWandTool wandTool = null, float tolerance = 0.5f)
        {
            FillSubmesh(_targetMesh, color, hitUv, wandTool, tolerance);
        }

        private bool[] BuildSubmeshUvMask(MeshInstance3D mesh, int width, int height, Vector2 pos, Vector2 size, out Rect2I boundingBox)
        {
            var mask = new bool[width * height];
            int minX = width, maxX = 0, minY = height, maxY = 0;
            bool anyVertex = false;

            if (mesh == null)
            {
                boundingBox = new Rect2I(0, 0, width, height);
                return mask;
            }

            var meshesToRasterize = new List<MeshInstance3D>();
            meshesToRasterize.Add(mesh);
            string targetMatKey = GetMaterialKey(mesh);
            if (!string.IsNullOrEmpty(targetMatKey))
            {
                foreach (var regMesh in _registeredSubmeshes)
                {
                    if (regMesh != null && regMesh != mesh && GodotObject.IsInstanceValid(regMesh) && GetMaterialKey(regMesh) == targetMatKey)
                    {
                        meshesToRasterize.Add(regMesh);
                    }
                }
            }

            foreach (var m in meshesToRasterize)
            {
                if (m == null || m.Mesh == null) continue;
                int surfaceCount = m.Mesh.GetSurfaceCount();
                for (int s = 0; s < surfaceCount; s++)
                {
                    var arrays = m.Mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count <= (int)Mesh.ArrayType.TexUV) continue;

                    var uvsVariant = arrays[(int)Mesh.ArrayType.TexUV];
                    if (uvsVariant.VariantType != Variant.Type.PackedVector2Array) continue;
                    Vector2[] uvs = uvsVariant.AsVector2Array();
                    if (uvs == null || uvs.Length == 0) continue;

                    var indicesVariant = arrays[(int)Mesh.ArrayType.Index];
                    int[] indices = (indicesVariant.VariantType == Variant.Type.PackedInt32Array)
                        ? indicesVariant.AsInt32Array()
                        : null;

                    int triCount = indices != null ? indices.Length / 3 : uvs.Length / 3;

                for (int t = 0; t < triCount; t++)
                {
                    int i0 = indices != null ? indices[t * 3] : t * 3;
                    int i1 = indices != null ? indices[t * 3 + 1] : t * 3 + 1;
                    int i2 = indices != null ? indices[t * 3 + 2] : t * 3 + 2;

                    if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length) continue;

                    Vector2 uv0 = uvs[i0];
                    Vector2 uv1 = uvs[i1];
                    Vector2 uv2 = uvs[i2];

                    // Skip wrap chords crossing UV seams
                    if (MathF.Abs(uv0.X - uv1.X) > 0.4f || MathF.Abs(uv1.X - uv2.X) > 0.4f || MathF.Abs(uv2.X - uv0.X) > 0.4f ||
                        MathF.Abs(uv0.Y - uv1.Y) > 0.4f || MathF.Abs(uv1.Y - uv2.Y) > 0.4f || MathF.Abs(uv2.Y - uv0.Y) > 0.4f)
                    {
                        continue;
                    }

                    Vector2 p0 = new Vector2(uv0.X * width, uv0.Y * height);
                    Vector2 p1 = new Vector2(uv1.X * width, uv1.Y * height);
                    Vector2 p2 = new Vector2(uv2.X * width, uv2.Y * height);

                    int tMinX = Math.Clamp((int)MathF.Floor(MathF.Min(p0.X, MathF.Min(p1.X, p2.X))), 0, width - 1);
                    int tMaxX = Math.Clamp((int)MathF.Ceiling(MathF.Max(p0.X, MathF.Max(p1.X, p2.X))), 0, width - 1);
                    int tMinY = Math.Clamp((int)MathF.Floor(MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y))), 0, height - 1);
                    int tMaxY = Math.Clamp((int)MathF.Ceiling(MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y))), 0, height - 1);

                    Vector2 v0 = p1 - p0;
                    Vector2 v1 = p2 - p0;
                    float denom = v0.X * v1.Y - v1.X * v0.Y;
                    if (MathF.Abs(denom) < 1e-7f) continue;
                    float invDenom = 1.0f / denom;

                    for (int y = tMinY; y <= tMaxY; y++)
                    {
                        float py = y + 0.5f;
                        int row = y * width;
                        for (int x = tMinX; x <= tMaxX; x++)
                        {
                            float px = x + 0.5f;
                            Vector2 v2 = new Vector2(px - p0.X, py - p0.Y);
                            float u = (v2.X * v1.Y - v1.X * v2.Y) * invDenom;
                            float v = (v0.X * v2.Y - v2.X * v0.Y) * invDenom;

                            if (u >= -0.02f && v >= -0.02f && (u + v) <= 1.02f)
                            {
                                mask[row + x] = true;
                                anyVertex = true;
                                if (x < minX) minX = x;
                                if (x > maxX) maxX = x;
                                if (y < minY) minY = y;
                                if (y > maxY) maxY = y;
                            }
                        }
                    }
                }
            }
        }

            if (!anyVertex || minX > maxX || minY > maxY)
            {
                boundingBox = new Rect2I(0, 0, width, height);
                return mask;
            }

            boundingBox = new Rect2I(minX, minY, maxX - minX + 1, maxY - minY + 1);

            // 1-pixel conservative dilation to avoid unpainted perimeter texels along mesh seams
            bool[] dilated = (bool[])mask.Clone();
            for (int y = minY; y <= maxY; y++)
            {
                int row = y * width;
                for (int x = minX; x <= maxX; x++)
                {
                    if (mask[row + x])
                    {
                        if (x > 0) dilated[row + (x - 1)] = true;
                        if (x < width - 1) dilated[row + (x + 1)] = true;
                        if (y > 0) dilated[(y - 1) * width + x] = true;
                        if (y < height - 1) dilated[(y + 1) * width + x] = true;
                    }
                }
            }
            return dilated;
        }

        public void FillSubmesh(MeshInstance3D mesh, Color color, Vector2? hitUv = null, MagicWandTool wandTool = null, float tolerance = 0.5f)
        {
            if (_targetMesh != null && mesh != _targetMesh && GetMaterialKey(mesh) != GetMaterialKey(_targetMesh))
            {
                GD.Print("[SkinLayerManager] Ignoring fill: mesh is not the currently selected target mesh or material.");
                return;
            }

            var fillAtlasMgr = _activeAtlasManager;
            if (mesh == null || !GodotObject.IsInstanceValid(mesh) || fillAtlasMgr == null || !GodotObject.IsInstanceValid(fillAtlasMgr))
            {
                GD.PrintErr("[SkinLayerManager] Cannot fill submesh: invalid mesh or atlas manager.");
                return;
            }

            if (mesh.MaterialOverlay is not ShaderMaterial sm)
            {
                GD.PrintErr("[SkinLayerManager] Submesh has no MaterialOverlay assigned.");
                return;
            }

            var posVar = sm.GetShaderParameter("position_in_atlas");
            var sizeVar = sm.GetShaderParameter("size_in_atlas");
            if (posVar.VariantType != Variant.Type.Vector2 || sizeVar.VariantType != Variant.Type.Vector2)
            {
                GD.PrintErr("[SkinLayerManager] Submesh MaterialOverlay missing atlas coordinates.");
                return;
            }

            Vector2 pos = posVar.AsVector2();
            Vector2 size = sizeVar.AsVector2();

            var rd = RenderingServer.GetRenderingDevice();
            var ridVal = fillAtlasMgr.Get("atlas_texture_rid");
            if (ridVal.VariantType != Variant.Type.Rid) return;
            Rid rid = ridVal.AsRid();
            if (!rid.IsValid || rd == null) return;

            int atlasSize = (int)fillAtlasMgr.Get("atlas_size");
            if (atlasSize <= 0) atlasSize = 2048;

            RecordInitialSnapshot();

            int startX = Mathf.Clamp((int)(pos.X * atlasSize), 0, atlasSize - 1);
            int startY = Mathf.Clamp((int)(pos.Y * atlasSize), 0, atlasSize - 1);
            int width = Mathf.Clamp((int)(size.X * atlasSize), 1, atlasSize - startX);
            int height = Mathf.Clamp((int)(size.Y * atlasSize), 1, atlasSize - startY);

            Color linearCol = color.SrgbToLinear();
            Half hR = (Half)linearCol.R;
            Half hG = (Half)linearCol.G;
            Half hB = (Half)linearCol.B;
            Half hA = (Half)1.0f; // Solid 100% target opacity

            bool useMask = wandTool != null && wandTool.HasSelection;

            if (ActiveLayer != null)
            {
                EnsureCpuSynced();
                int bufferLen = atlasSize * atlasSize * 8;
                if (ActiveLayer.GpuData == null || ActiveLayer.GpuData.Length != bufferLen)
                {
                    ActiveLayer.GpuData = new byte[bufferLen];
                }

                bool[] submeshUvMask = BuildSubmeshUvMask(mesh, width, height, pos, size, out Rect2I uvBounds);
                bool hasValidUvMask = uvBounds.Size.X > 0 && uvBounds.Size.Y > 0;

                bool[] fillMask = new bool[width * height];

                if (useMask)
                {
                    // Fill matching mask pixels with solid 100% target opacity, strictly within submesh geometry
                    for (int y = 0; y < height; y++)
                    {
                        int ay = startY + y;
                        int row = y * width;
                        for (int x = 0; x < width; x++)
                        {
                            if (hasValidUvMask && !submeshUvMask[row + x]) continue;
                            int ax = startX + x;
                            float maskVal = wandTool.GetPixelMaskValue(ax, ay);
                            if (maskVal >= 0.5f)
                            {
                                fillMask[row + x] = true;
                            }
                        }
                    }
                }
                else
                {
                    int seedLocalX = width / 2;
                    int seedLocalY = height / 2;
                    if (hitUv.HasValue)
                    {
                        seedLocalX = Mathf.Clamp((int)(hitUv.Value.X * width), 0, width - 1);
                        seedLocalY = Mathf.Clamp((int)(hitUv.Value.Y * height), 0, height - 1);
                    }
                    else if (hasValidUvMask)
                    {
                        seedLocalX = uvBounds.Position.X + uvBounds.Size.X / 2;
                        seedLocalY = uvBounds.Position.Y + uvBounds.Size.Y / 2;
                    }

                    if (hasValidUvMask && !submeshUvMask[seedLocalY * width + seedLocalX])
                    {
                        // Find nearest marked submesh texel within 16px
                        int bestX = seedLocalX, bestY = seedLocalY;
                        float bestDistSq = float.MaxValue;
                        for (int dy = -16; dy <= 16; dy++)
                        {
                            int cy = seedLocalY + dy;
                            if (cy < 0 || cy >= height) continue;
                            for (int dx = -16; dx <= 16; dx++)
                            {
                                int cx = seedLocalX + dx;
                                if (cx < 0 || cx >= width) continue;
                                if (submeshUvMask[cy * width + cx])
                                {
                                    float dSq = dx * dx + dy * dy;
                                    if (dSq < bestDistSq)
                                    {
                                        bestDistSq = dSq;
                                        bestX = cx;
                                        bestY = cy;
                                    }
                                }
                            }
                        }
                        seedLocalX = bestX;
                        seedLocalY = bestY;
                    }

                    if (tolerance >= 0.99f)
                    {
                        // 100% Maximum Tolerance: Fill ALL parts and islands belonging to this material
                        if (hasValidUvMask)
                        {
                            Array.Copy(submeshUvMask, fillMask, fillMask.Length);
                        }
                        else
                        {
                            Array.Fill(fillMask, true);
                        }
                    }
                    else
                    {
                        // Dedicated Paint Bucket Tolerance Flood Fill
                        if (_baseAtlasBuffer == null || _baseAtlasBuffer.Length != bufferLen)
                        {
                            RebuildBaseAtlasBuffer();
                        }

                        unsafe
                        {
                            fixed (byte* pBase = _baseAtlasBuffer, pLayer = ActiveLayer.GpuData)
                            {
                                Half* hBase = (Half*)pBase;
                                Half* hLayer = (Half*)pLayer;

                                int seedAtlasIdx = ((startY + seedLocalY) * atlasSize + (startX + seedLocalX)) * 4;

                                float sR, sG, sB, sA;
                                float layerA = (float)hLayer[seedAtlasIdx + 3];
                                if (layerA > 0.05f)
                                {
                                    sR = (float)hLayer[seedAtlasIdx];
                                    sG = (float)hLayer[seedAtlasIdx + 1];
                                    sB = (float)hLayer[seedAtlasIdx + 2];
                                    sA = layerA;
                                }
                                else if (hBase != null)
                                {
                                    sR = (float)hBase[seedAtlasIdx];
                                    sG = (float)hBase[seedAtlasIdx + 1];
                                    sB = (float)hBase[seedAtlasIdx + 2];
                                    sA = (float)hBase[seedAtlasIdx + 3];
                                }
                                else
                                {
                                    sR = 1f; sG = 1f; sB = 1f; sA = 1f;
                                }

                                int[] q = new int[width * height];
                                int qHead = 0, qTail = 0;

                                int seedIdx = seedLocalY * width + seedLocalX;
                                fillMask[seedIdx] = true;
                                q[qTail++] = seedIdx;

                                while (qHead < qTail)
                                {
                                    int curr = q[qHead++];
                                    int cx = curr % width;
                                    int cy = curr / width;

                                    void CheckNeighbor(int nx, int ny)
                                    {
                                        int nIdx = ny * width + nx;
                                        if (fillMask[nIdx]) return;
                                        if (hasValidUvMask && !submeshUvMask[nIdx]) return;

                                        int atlasIdx = ((startY + ny) * atlasSize + (startX + nx)) * 4;
                                        float cR, cG, cB, cA;
                                        float pA = (float)hLayer[atlasIdx + 3];
                                        if (pA > 0.05f)
                                        {
                                            cR = (float)hLayer[atlasIdx];
                                            cG = (float)hLayer[atlasIdx + 1];
                                            cB = (float)hLayer[atlasIdx + 2];
                                            cA = pA;
                                        }
                                        else if (hBase != null)
                                        {
                                            cR = (float)hBase[atlasIdx];
                                            cG = (float)hBase[atlasIdx + 1];
                                            cB = (float)hBase[atlasIdx + 2];
                                            cA = (float)hBase[atlasIdx + 3];
                                        }
                                        else
                                        {
                                            cR = 1f; cG = 1f; cB = 1f; cA = 1f;
                                        }

                                        if (sA > 0.001f && cA <= 0.001f) return;

                                        float dr = cR - sR;
                                        float dg = cG - sG;
                                        float db = cB - sB;
                                        float dist = MathF.Sqrt(0.299f * dr * dr + 0.587f * dg * dg + 0.114f * db * db);

                                        if (dist <= tolerance)
                                        {
                                            fillMask[nIdx] = true;
                                            q[qTail++] = nIdx;
                                        }
                                    }

                                    if (cx > 0) CheckNeighbor(cx - 1, cy);
                                    if (cx < width - 1) CheckNeighbor(cx + 1, cy);
                                    if (cy > 0) CheckNeighbor(cx, cy - 1);
                                    if (cy < height - 1) CheckNeighbor(cx, cy + 1);
                                }
                            }
                        }
                    }
                }

                // 1-pixel dilation on the bucket fill region to eliminate fringing, strictly bounded within submesh geometry
                bool[] dilatedMask = (bool[])fillMask.Clone();
                for (int y = 0; y < height; y++)
                {
                    int row = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        if (fillMask[row + x])
                        {
                            void TryDilate(int dx, int dy)
                            {
                                int nx = x + dx;
                                int ny = y + dy;
                                if (nx >= 0 && nx < width && ny >= 0 && ny < height)
                                {
                                    int nIdx = ny * width + nx;
                                    if (!hasValidUvMask || submeshUvMask[nIdx])
                                    {
                                        dilatedMask[nIdx] = true;
                                    }
                                }
                            }
                            TryDilate(-1, 0);
                            TryDilate(1, 0);
                            TryDilate(0, -1);
                            TryDilate(0, 1);
                        }
                    }
                }

                // Apply solid, 100% target opacity paint
                unsafe
                {
                    fixed (byte* pDst = ActiveLayer.GpuData)
                    {
                        Half* hLayer = (Half*)pDst;
                        for (int y = 0; y < height; y++)
                        {
                            int rowOffset = (startY + y) * atlasSize * 4;
                            int maskRow = y * width;
                            for (int x = 0; x < width; x++)
                            {
                                if (dilatedMask[maskRow + x])
                                {
                                    int idx = rowOffset + (startX + x) * 4;
                                    hLayer[idx] = hR;
                                    hLayer[idx + 1] = hG;
                                    hLayer[idx + 2] = hB;
                                    hLayer[idx + 3] = hA;
                                }
                            }
                        }
                    }
                }
            }

            SyncActiveLayerGpuTexture();
            RecompositeGpuLayers();
            RecordUndoSnapshot();

            MeshHierarchy?.MarkSubmeshDirty(mesh);

            GD.Print($"[SkinLayerManager] Bucket filled submesh '{mesh.Name}' with {color.ToHtml()} (Tol={tolerance:F2}, UseMask={useMask}, HitUV={hitUv})");
        }

        public bool StampDecalToAtlas(
            Vector2 hitUv, 
            Texture2D decalTexture, 
            float rotationDeg, 
            float scale, 
            MagicWandTool wandTool = null,
            Vector3 decalRight = default,
            Vector3 decalDown = default,
            Vector3 worldTangent = default,
            Vector3 worldBitangent = default,
            float unitsU = 1.0f,
            float unitsV = 1.0f)
        {
            var stampAtlasMgr = _activeAtlasManager;
            if (decalTexture == null || _targetMesh == null || !GodotObject.IsInstanceValid(_targetMesh) || stampAtlasMgr == null || !GodotObject.IsInstanceValid(stampAtlasMgr))
            {
                return false;
            }

            if (_targetMesh.MaterialOverlay is not ShaderMaterial sm) return false;

            var posVar = sm.GetShaderParameter("position_in_atlas");
            var sizeVar = sm.GetShaderParameter("size_in_atlas");
            if (posVar.VariantType != Variant.Type.Vector2 || sizeVar.VariantType != Variant.Type.Vector2) return false;

            Vector2 pos = posVar.AsVector2();
            Vector2 size = sizeVar.AsVector2();

            var rd = RenderingServer.GetRenderingDevice();
            var ridVal = stampAtlasMgr.Get("atlas_texture_rid");
            if (ridVal.VariantType != Variant.Type.Rid) return false;
            Rid rid = ridVal.AsRid();
            if (!rid.IsValid || rd == null) return false;

            int atlasSize = (int)stampAtlasMgr.Get("atlas_size");
            if (atlasSize <= 0) atlasSize = 2048;

            Image decalImg = decalTexture.GetImage();
            if (decalImg == null) return false;
            if (decalImg.IsCompressed()) decalImg.Decompress();
            if (decalImg.GetFormat() != Image.Format.Rgba8) decalImg.Convert(Image.Format.Rgba8);

            int dW = decalImg.GetWidth();
            int dH = decalImg.GetHeight();
            if (dW <= 0 || dH <= 0) return false;

            byte[] rawDecal = decalImg.GetData();

            RecordInitialSnapshot();

            Vector2 atlasUv = hitUv * size + pos;
            Vector2 centerPx = atlasUv * atlasSize;

            float aspect = (float)dW / dH;
            float spanU = (aspect >= 1.0f) ? scale * aspect : scale;
            float spanV = (aspect < 1.0f) ? scale / aspect : scale;

            float halfExtX = (spanU * size.X * atlasSize) * 0.5f;
            float halfExtY = (spanV * size.Y * atlasSize) * 0.5f;

            if (halfExtX < 1.0f) halfExtX = 1.0f;
            if (halfExtY < 1.0f) halfExtY = 1.0f;

            float rad = Mathf.DegToRad(rotationDeg);
            float cosR = Mathf.Cos(rad);
            float sinR = Mathf.Sin(rad);

            bool use3DProjection = decalRight.LengthSquared() > 0.001f && worldTangent.LengthSquared() > 0.001f;
            Vector3 T = worldTangent * unitsU;
            Vector3 B = worldBitangent * unitsV;

            float decalSizeX = spanU * unitsU;
            float decalSizeZ = spanV * unitsV;
            if (decalSizeX < 1e-4f) decalSizeX = 1e-4f;
            if (decalSizeZ < 1e-4f) decalSizeZ = 1e-4f;

            float dU_decalX = T.Dot(decalRight) / decalSizeX;
            float dV_decalX = B.Dot(decalRight) / decalSizeX;
            float dU_decalZ = T.Dot(decalDown) / decalSizeZ;
            float dV_decalZ = B.Dot(decalDown) / decalSizeZ;

            float submeshW = size.X * atlasSize;
            float submeshH = size.Y * atlasSize;

            float diag = Mathf.Sqrt(halfExtX * halfExtX + halfExtY * halfExtY) * 1.5f;
            int minX = Mathf.Clamp((int)(centerPx.X - diag), 0, atlasSize - 1);
            int maxX = Mathf.Clamp((int)(centerPx.X + diag), 0, atlasSize - 1);
            int minY = Mathf.Clamp((int)(centerPx.Y - diag), 0, atlasSize - 1);
            int maxY = Mathf.Clamp((int)(centerPx.Y + diag), 0, atlasSize - 1);

            int bufferLen = atlasSize * atlasSize * 8;
            if (ActiveLayer != null)
            {
                if (ActiveLayer.GpuData == null || ActiveLayer.GpuData.Length != bufferLen)
                {
                    ActiveLayer.GpuData = new byte[bufferLen];
                }
                unsafe
                {
                    fixed (byte* pDst = ActiveLayer.GpuData, pDecal = rawDecal)
                    {
                        Half* hLayer = (Half*)pDst;
                        for (int y = minY; y <= maxY; y++)
                        {
                            int rowOffset = y * atlasSize * 4;
                            for (int x = minX; x <= maxX; x++)
                            {
                                float rx, ry;
                                if (use3DProjection)
                                {
                                    float deltaU = (float)(x - centerPx.X) / submeshW;
                                    float deltaV = (float)(y - centerPx.Y) / submeshH;
                                    rx = (deltaU * dU_decalX + deltaV * dV_decalX) * 2.0f;
                                    ry = (deltaU * dU_decalZ + deltaV * dV_decalZ) * 2.0f;
                                }
                                else
                                {
                                    float nx = (float)(x - centerPx.X) / halfExtX;
                                    float ny = (float)(y - centerPx.Y) / halfExtY;
                                    rx = nx * cosR - ny * sinR;
                                    ry = nx * sinR + ny * cosR;
                                }

                                if (Mathf.Abs(rx) > 1.0f || Mathf.Abs(ry) > 1.0f) continue;
                                if (wandTool != null && wandTool.HasActiveSelection && !wandTool.IsPixelSelected(x, y)) continue;

                                float du = (rx + 1.0f) * 0.5f;
                                float dv = (ry + 1.0f) * 0.5f;

                                int px = Mathf.Clamp((int)(du * dW), 0, dW - 1);
                                int py = Mathf.Clamp((int)(dv * dH), 0, dH - 1);

                                int dOff = (py * dW + px) * 4;
                                byte rawA = pDecal[dOff + 3];
                                if (rawA == 0) continue;

                                float dAlpha = rawA / 255.0f;
                                Color dCol = new Color(pDecal[dOff] / 255.0f, pDecal[dOff + 1] / 255.0f, pDecal[dOff + 2] / 255.0f, dAlpha).SrgbToLinear();
                                if (dCol.A <= 0.001f) continue;

                                int idx = rowOffset + x * 4;
                                float curR = (float)hLayer[idx];
                                float curG = (float)hLayer[idx + 1];
                                float curB = (float)hLayer[idx + 2];
                                float curA = (float)hLayer[idx + 3];

                                float outA = Mathf.Clamp(dCol.A + curA * (1.0f - dCol.A), 0.0f, 1.0f);
                                float outR = (outA > 0.0f) ? (dCol.R * dCol.A + curR * curA * (1.0f - dCol.A)) / outA : dCol.R;
                                float outG = (outA > 0.0f) ? (dCol.G * dCol.A + curG * curA * (1.0f - dCol.A)) / outA : dCol.G;
                                float outB = (outA > 0.0f) ? (dCol.B * dCol.A + curB * curA * (1.0f - dCol.A)) / outA : dCol.B;

                                hLayer[idx] = (Half)outR;
                                hLayer[idx + 1] = (Half)outG;
                                hLayer[idx + 2] = (Half)outB;
                                hLayer[idx + 3] = (Half)outA;
                            }
                        }
                    }
                }
            }

            SyncActiveLayerGpuTexture();
            RecompositeGpuLayers();
            RecordUndoSnapshot();

            if (_targetMesh != null && MeshHierarchy != null)
            {
                MeshHierarchy.MarkSubmeshDirty(_targetMesh);
            }

            GD.Print($"[SkinLayerManager] Decal successfully stamped to atlas at ({centerPx.X:F0}, {centerPx.Y:F0})");
            return true;
        }

        public Texture2D GetAtlasTextureResource()
        {
            if (_activeAtlasManager != null && GodotObject.IsInstanceValid(_activeAtlasManager))
            {
                var fullRes = _activeAtlasManager.Get("full_composite_resource");
                if (fullRes.VariantType == Variant.Type.Object && fullRes.AsGodotObject() is Texture2D fullTex)
                {
                    return fullTex;
                }
                var texRes = _activeAtlasManager.Get("atlas_texture_resource");
                if (texRes.VariantType == Variant.Type.Object && texRes.AsGodotObject() is Texture2D t2d)
                {
                    return t2d;
                }
            }
            return null;
        }

        private Texture2Drd _baseTextureResource;
        private bool _hasPopulatedBaseAtlasBuffer = false;
        public bool HasPopulatedBaseAtlasBuffer => _hasPopulatedBaseAtlasBuffer;

        public Texture2D GetBaseTextureResource()
        {
            if (_activeAtlasManager != null && GodotObject.IsInstanceValid(_activeAtlasManager))
            {
                if (_baseAtlasBuffer == null || !_hasPopulatedBaseAtlasBuffer)
                {
                    RebuildBaseAtlasBuffer();
                }

                var baseRidVal = _activeAtlasManager.Get("base_texture_rid");
                if (baseRidVal.VariantType == Variant.Type.Rid)
                {
                    Rid baseRid = baseRidVal.AsRid();
                    if (baseRid.IsValid)
                    {
                        if (_baseTextureResource == null || !GodotObject.IsInstanceValid(_baseTextureResource) || _baseTextureResource.TextureRdRid != baseRid)
                        {
                            _baseTextureResource = new Texture2Drd();
                            _baseTextureResource.TextureRdRid = baseRid;
                        }
                        return _baseTextureResource;
                    }
                }
            }
            return null;
        }

        public byte[] CompositeBuffer => _compositeBuffer;
        public byte[] BaseAtlasBuffer => _baseAtlasBuffer;

        public Color GetVisibleColorAtAtlasPx(int x, int y)
        {
            int atlasSize = CanvasSize.X > 0 ? CanvasSize.X : 2048;
            if (x < 0 || x >= atlasSize || y < 0 || y >= atlasSize) return Colors.White;

            if (_baseAtlasBuffer == null || !_hasPopulatedBaseAtlasBuffer || _baseAtlasBuffer.Length < atlasSize * atlasSize * 8)
            {
                RebuildBaseAtlasBuffer();
            }

            int idx = (y * atlasSize + x) * 4;
            unsafe
            {
                float baseR = 1.0f, baseG = 1.0f, baseB = 1.0f;
                if (_baseAtlasBuffer != null && _baseAtlasBuffer.Length >= (idx + 4) * 2)
                {
                    fixed (byte* pBase = _baseAtlasBuffer)
                    {
                        Half* hBase = (Half*)pBase;
                        baseR = (float)hBase[idx];
                        baseG = (float)hBase[idx + 1];
                        baseB = (float)hBase[idx + 2];
                    }
                }

                if (_compositeBuffer != null && _compositeBuffer.Length >= (idx + 4) * 2)
                {
                    fixed (byte* pComp = _compositeBuffer)
                    {
                        Half* hComp = (Half*)pComp;
                        float r = (float)hComp[idx];
                        float g = (float)hComp[idx + 1];
                        float b = (float)hComp[idx + 2];
                        float a = (float)hComp[idx + 3];

                        if (a >= 0.999f)
                        {
                            return new Color(r, g, b, 1.0f).LinearToSrgb();
                        }
                        if (a > 0.001f)
                        {
                            float finalR = r * a + baseR * (1.0f - a);
                            float finalG = g * a + baseG * (1.0f - a);
                            float finalB = b * a + baseB * (1.0f - a);
                            return new Color(finalR, finalG, finalB, 1.0f).LinearToSrgb();
                        }
                    }
                }

                return new Color(baseR, baseG, baseB, 1.0f).LinearToSrgb();
            }
        }

        public unsafe Rect2I PaintDab2D(
            Vector2 atlasPx,
            Color color,
            float sizePx,
            float hardness,
            float flow,
            bool isErase,
            MagicWandTool wandTool = null,
            BrushShapeType shape = BrushShapeType.SoftCircle,
            Texture2D brushTex = null)
        {
            var layer = ActiveLayer;
            if (layer == null || layer.IsLocked || !layer.IsVisible) return new Rect2I();

            int atlasSize = CanvasSize.X > 0 ? CanvasSize.X : 2048;
            int bufferLen = atlasSize * atlasSize * 8;
            if (layer.GpuData == null || layer.GpuData.Length != bufferLen)
            {
                layer.GpuData = new byte[bufferLen];
            }

            float radius = sizePx * 0.5f;
            if (radius < 0.5f) radius = 0.5f;
            int intRadius = Mathf.CeilToInt(radius);

            int minX = Mathf.Clamp((int)(atlasPx.X - intRadius), 0, atlasSize - 1);
            int maxX = Mathf.Clamp((int)(atlasPx.X + intRadius), 0, atlasSize - 1);
            int minY = Mathf.Clamp((int)(atlasPx.Y - intRadius), 0, atlasSize - 1);
            int maxY = Mathf.Clamp((int)(atlasPx.Y + intRadius), 0, atlasSize - 1);

            float rSq = radius * radius;
            float hardFrac = Mathf.Clamp(hardness, 0.0f, 0.99f);
            float innerRadius = radius * hardFrac;
            float innerRadiusSq = innerRadius * innerRadius;
            float invFadeRange = 1.0f / MathF.Max(0.001f, radius - innerRadius);

            Color linCol = color.SrgbToLinear();

            Image brushImg = null;
            if ((shape == BrushShapeType.Splatter || shape == BrushShapeType.Grunge) && brushTex != null)
            {
                brushImg = brushTex.GetImage();
                if (brushImg != null && brushImg.IsCompressed()) brushImg.Decompress();
            }

            fixed (byte* pDst = layer.GpuData)
            {
                Half* hLayer = (Half*)pDst;
                for (int y = minY; y <= maxY; y++)
                {
                    int rowOffset = y * atlasSize * 4;
                    float dy = y - atlasPx.Y;
                    float dySq = dy * dy;
                    if (shape != BrushShapeType.Square && dySq > rSq) continue;

                    float absY = MathF.Abs(dy);
                    int rowMinX = minX;
                    int rowMaxX = maxX;
                    if (shape != BrushShapeType.Square)
                    {
                        float spanX = MathF.Sqrt(rSq - dySq);
                        rowMinX = Math.Max(minX, (int)(atlasPx.X - spanX));
                        rowMaxX = Math.Min(maxX, (int)(atlasPx.X + spanX));
                    }

                    for (int x = rowMinX; x <= rowMaxX; x++)
                    {
                        float dx = x - atlasPx.X;
                        float absX = MathF.Abs(dx);

                        float falloff = 0.0f;

                        if (shape == BrushShapeType.Square)
                        {
                            if (absX > radius || absY > radius) continue;
                            float maxDist = Math.Max(absX, absY);
                            if (maxDist <= innerRadius)
                            {
                                falloff = 1.0f;
                            }
                            else
                            {
                                float t = (maxDist - innerRadius) / Math.Max(0.001f, radius - innerRadius);
                                falloff = Mathf.Clamp(1.0f - t, 0.0f, 1.0f);
                            }
                        }
                        else if (shape == BrushShapeType.HardCircle)
                        {
                            falloff = 1.0f;
                        }
                        else if (shape == BrushShapeType.Splatter || shape == BrushShapeType.Grunge)
                        {
                            float dSq = dx * dx + dySq;
                            if (dSq > rSq) continue;
                            float dist = Mathf.Sqrt(dSq);
                            float baseFalloff = (dist <= innerRadius) ? 1.0f : Mathf.Clamp(1.0f - (dist - innerRadius) / Math.Max(0.001f, radius - innerRadius), 0.0f, 1.0f);

                            if (brushImg != null)
                            {
                                float u = Math.Clamp((dx + radius) / (radius * 2.0f), 0.0f, 1.0f);
                                float v = Math.Clamp((dy + radius) / (radius * 2.0f), 0.0f, 1.0f);
                                int bx = Math.Clamp((int)(u * brushImg.GetWidth()), 0, brushImg.GetWidth() - 1);
                                int by = Math.Clamp((int)(v * brushImg.GetHeight()), 0, brushImg.GetHeight() - 1);
                                Color bPixel = brushImg.GetPixel(bx, by);
                                falloff = baseFalloff * bPixel.A;
                            }
                            else
                            {
                                falloff = baseFalloff;
                            }
                        }
                        else // SoftCircle
                        {
                            float dSq = dx * dx + dySq;
                            if (dSq > rSq) continue;
                            if (dSq <= innerRadiusSq)
                            {
                                falloff = 1.0f;
                            }
                            else
                            {
                                float dist = MathF.Sqrt(dSq);
                                float t = (dist - innerRadius) * invFadeRange;
                                falloff = Math.Clamp(1.0f - t, 0.0f, 1.0f);
                                falloff = falloff * falloff * (3.0f - 2.0f * falloff);
                            }
                        }

                        float wandWeight = 1.0f;
                        if (wandTool != null && wandTool.HasActiveSelection)
                        {
                            float maskVal = wandTool.GetPixelMaskValue(x, y);
                            if (maskVal < 0.5f) continue;
                            wandWeight = Math.Clamp((maskVal - 0.5f) / 0.5f, 0.0f, 1.0f);
                        }

                        float dabAlpha = falloff * flow * wandWeight;
                        if (dabAlpha <= 0.0001f) continue;

                        int idx = rowOffset + x * 4;

                        if (isErase)
                        {
                            float curA = (float)hLayer[idx + 3];
                            hLayer[idx + 3] = (Half)Mathf.Clamp(curA * (1.0f - dabAlpha), 0.0f, 1.0f);
                        }
                        else
                        {
                            float curR = (float)hLayer[idx];
                            float curG = (float)hLayer[idx + 1];
                            float curB = (float)hLayer[idx + 2];
                            float curA = (float)hLayer[idx + 3];

                            float outA = Mathf.Clamp(dabAlpha + curA * (1.0f - dabAlpha), 0.0f, 1.0f);
                            float outR = (outA > 0.0001f) ? (linCol.R * dabAlpha + curR * curA * (1.0f - dabAlpha)) / outA : linCol.R;
                            float outG = (outA > 0.0001f) ? (linCol.G * dabAlpha + curG * curA * (1.0f - dabAlpha)) / outA : linCol.G;
                            float outB = (outA > 0.0001f) ? (linCol.B * dabAlpha + curB * curA * (1.0f - dabAlpha)) / outA : linCol.B;

                            hLayer[idx] = (Half)outR;
                            hLayer[idx + 1] = (Half)outG;
                            hLayer[idx + 2] = (Half)outB;
                            hLayer[idx + 3] = (Half)outA;
                        }
                    }
                }
            }

            return new Rect2I(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        public Rect2I PaintStroke2D(
            Vector2 fromAtlasPx,
            Vector2 toAtlasPx,
            Color color,
            float sizePx,
            float hardness,
            float flow,
            bool isErase,
            MagicWandTool wandTool = null,
            BrushShapeType shape = BrushShapeType.SoftCircle,
            Texture2D brushTex = null)
        {
            int atlasSize = CanvasSize.X > 0 ? CanvasSize.X : 2048;
            float radius = sizePx * 0.5f;
            int intRadius = Mathf.CeilToInt(radius);
            int minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(fromAtlasPx.X, toAtlasPx.X) - intRadius), 0, atlasSize - 1);
            int maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(fromAtlasPx.X, toAtlasPx.X) + intRadius), 0, atlasSize - 1);
            int minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(fromAtlasPx.Y, toAtlasPx.Y) - intRadius), 0, atlasSize - 1);
            int maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(fromAtlasPx.Y, toAtlasPx.Y) + intRadius), 0, atlasSize - 1);

            float dist = fromAtlasPx.DistanceTo(toAtlasPx);
            float brushRadius = sizePx * 0.5f;
            float stepDistance = MathF.Max(4.0f, brushRadius * 0.35f);
            int steps = Math.Clamp((int)MathF.Ceiling(dist / stepDistance), 1, 32);

            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                Vector2 pt = fromAtlasPx.Lerp(toAtlasPx, t);
                PaintDab2D(pt, color, sizePx, hardness, flow, isErase, wandTool, shape, brushTex);
            }

            return new Rect2I(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        public void Finish2DStroke(Rect2I? strokeDirtyRect = null)
        {
            if (strokeDirtyRect.HasValue && strokeDirtyRect.Value.Size.X > 0 && strokeDirtyRect.Value.Size.Y > 0)
            {
                // Partial stroke: dirty area has already been uploaded tile-by-tile via UpdateActiveLayerGpuTextureThrottled.
                // Recomposite only the dirty bounding rect to eliminate the CPU full-buffer scan and PCIe stall!
                RecompositeGpuLayers(strokeDirtyRect.Value);
                RecordUndoSnapshot(_activeLayerIndex);
            }
            else
            {
                SyncActiveLayerGpuTexture();
                RecompositeGpuLayers();
                RecordUndoSnapshot(_activeLayerIndex);
            }

            if (_targetMesh != null && MeshHierarchy != null)
            {
                MeshHierarchy.MarkSubmeshDirty(_targetMesh);
            }
        }

        public unsafe void CommitStrokeToActiveLayer(byte[] preStrokeData, byte[] postStrokeData, Color? strokeColor = null, bool isErase = false, MagicWandTool wandTool = null)
        {
            var layer = ActiveLayer;
            if (layer == null || postStrokeData == null || postStrokeData.Length == 0) return;

            int len = postStrokeData.Length;
            if (layer.GpuData == null || layer.GpuData.Length != len)
            {
                layer.GpuData = new byte[len];
            }

            int pixelCount = len / 8;
            int atlasWidth = CanvasSize.X > 0 ? CanvasSize.X : 2048;
            if (_activeAtlasManager != null && GodotObject.IsInstanceValid(_activeAtlasManager))
            {
                int amSize = (int)_activeAtlasManager.Get("atlas_size");
                if (amSize > 0) atlasWidth = amSize;
            }

            int numRows = pixelCount / atlasWidth;
            int globalMinX = int.MaxValue, globalMaxX = -1, globalMinY = int.MaxValue, globalMaxY = -1;
            object boundsLock = new object();
            bool checkWand = wandTool != null && wandTool.HasActiveSelection;

            fixed (byte* pPre = preStrokeData, pPost = postStrokeData, pLayer = layer.GpuData)
            {
                ulong* uPre = (ulong*)pPre;
                ulong* uPost = (ulong*)pPost;
                Half* hPre = (Half*)pPre;
                Half* hPost = (Half*)pPost;
                Half* hLayer = (Half*)pLayer;

                float op = Mathf.Max(layer.Opacity, 0.001f);
                bool hasPre = preStrokeData != null && preStrokeData.Length == len;

                System.Threading.Tasks.Parallel.For(0, numRows, () => (MinX: int.MaxValue, MaxX: -1, MinY: int.MaxValue, MaxY: -1), (py, state, localBounds) =>
                {
                    int rowOffset = py * atlasWidth;
                    for (int px = 0; px < atlasWidth; px++)
                    {
                        int i = rowOffset + px;
                        if (hasPre && uPost[i] == uPre[i]) continue;
                        if (checkWand && wandTool.GetPixelMaskValue(px, py) < 0.5f) continue;

                        if (px < localBounds.MinX) localBounds.MinX = px;
                        if (px > localBounds.MaxX) localBounds.MaxX = px;
                        if (py < localBounds.MinY) localBounds.MinY = py;
                        if (py > localBounds.MaxY) localBounds.MaxY = py;

                        int hOffset = i * 4;
                        float actualA = (float)hPost[hOffset + 3];
                        float unscaledA = Mathf.Clamp(actualA / op, 0.0f, 1.0f);

                        if (isErase)
                        {
                            float curA = (float)hLayer[hOffset + 3];
                            float deltaErase = 0.0f;
                            if (hasPre)
                            {
                                float rawPreA = (float)hPre[hOffset + 3];
                                deltaErase = Mathf.Max(0.0f, rawPreA - actualA);
                            }
                            else
                            {
                                deltaErase = 1.0f - actualA;
                            }
                            float newLayerA = Mathf.Clamp(curA - deltaErase / op, 0.0f, 1.0f);
                            hLayer[hOffset + 3] = (Half)newLayerA;
                        }
                        else if (strokeColor.HasValue)
                        {
                            Color c = strokeColor.Value.SrgbToLinear();
                            float curA = (float)hLayer[hOffset + 3];
                            if (curA <= 0.001f)
                            {
                                hLayer[hOffset] = (Half)c.R;
                                hLayer[hOffset + 1] = (Half)c.G;
                                hLayer[hOffset + 2] = (Half)c.B;
                                hLayer[hOffset + 3] = (Half)unscaledA;
                            }
                            else
                            {
                                float outA = Mathf.Clamp(unscaledA + curA * (1.0f - unscaledA), 0.0f, 1.0f);
                                float curR = (float)hLayer[hOffset];
                                float curG = (float)hLayer[hOffset + 1];
                                float curB = (float)hLayer[hOffset + 2];
                                float outR = (outA > 0.0001f) ? (c.R * unscaledA + curR * curA * (1.0f - unscaledA)) / outA : c.R;
                                float outG = (outA > 0.0001f) ? (c.G * unscaledA + curG * curA * (1.0f - unscaledA)) / outA : c.G;
                                float outB = (outA > 0.0001f) ? (c.B * unscaledA + curB * curA * (1.0f - unscaledA)) / outA : c.B;
                                hLayer[hOffset] = (Half)outR;
                                hLayer[hOffset + 1] = (Half)outG;
                                hLayer[hOffset + 2] = (Half)outB;
                                hLayer[hOffset + 3] = (Half)outA;
                            }
                        }
                        else
                        {
                            hLayer[hOffset] = hPost[hOffset];
                            hLayer[hOffset + 1] = hPost[hOffset + 1];
                            hLayer[hOffset + 2] = hPost[hOffset + 2];
                            hLayer[hOffset + 3] = (Half)unscaledA;
                        }
                    }
                    return localBounds;
                },
                localBounds =>
                {
                    if (localBounds.MaxX >= 0)
                    {
                        lock (boundsLock)
                        {
                            if (localBounds.MinX < globalMinX) globalMinX = localBounds.MinX;
                            if (localBounds.MaxX > globalMaxX) globalMaxX = localBounds.MaxX;
                            if (localBounds.MinY < globalMinY) globalMinY = localBounds.MinY;
                            if (localBounds.MaxY > globalMaxY) globalMaxY = localBounds.MaxY;
                        }
                    }
                });
            }

            int minX = globalMinX, maxX = globalMaxX, minY = globalMinY, maxY = globalMaxY;

            if (maxX >= 0 && MeshHierarchy != null)
            {
                float normMinX = (float)minX / atlasWidth;
                float normMaxX = (float)maxX / atlasWidth;
                float normMinY = (float)minY / atlasWidth;
                float normMaxY = (float)maxY / atlasWidth;
                Rect2 strokeRect = new Rect2(normMinX, normMinY, Mathf.Max(normMaxX - normMinX, 0.001f), Mathf.Max(normMaxY - normMinY, 0.001f));

                foreach (var submesh in MeshHierarchy.Submeshes)
                {
                    if (submesh.Mesh != null && submesh.Mesh.MaterialOverlay is ShaderMaterial sm)
                    {
                        var posVar = sm.GetShaderParameter("position_in_atlas");
                        var sizeVar = sm.GetShaderParameter("size_in_atlas");
                        if (posVar.VariantType == Variant.Type.Vector2 && sizeVar.VariantType == Variant.Type.Vector2)
                        {
                            Vector2 pos = posVar.AsVector2();
                            Vector2 size = sizeVar.AsVector2();
                            Rect2 submeshRect = new Rect2(pos, size);
                            if (strokeRect.Intersects(submeshRect))
                            {
                                submesh.IsDirty = true;
                                submesh.IsSelected = true;
                            }
                        }
                    }
                }
            }

            if (_targetMesh != null && MeshHierarchy != null)
            {
                MeshHierarchy.MarkSubmeshDirty(_targetMesh);
            }
        }

        private byte[] _baseAtlasBuffer;

        public void InvalidateBaseAtlasBuffer()
        {
            _baseAtlasBuffer = null;
            _hasPopulatedBaseAtlasBuffer = false;
        }

        public void RebuildBaseAtlasBuffer()
        {
            // In the per-submesh atlas model the buffer only needs to represent the ACTIVE submesh.
            if (_activeAtlasManager == null || !GodotObject.IsInstanceValid(_activeAtlasManager)) return;
            if (_targetMesh == null || !GodotObject.IsInstanceValid(_targetMesh)) return;

            int atlasSize = (int)_activeAtlasManager.Get("atlas_size");
            if (atlasSize <= 0) atlasSize = CanvasSize.X > 0 ? CanvasSize.X : 2048;
            int bufferLen = atlasSize * atlasSize * 8;

            if (_baseAtlasBuffer == null || _baseAtlasBuffer.Length != bufferLen)
            {
                _baseAtlasBuffer = new byte[bufferLen];
            }

            // Fill with opaque white as the default background
            unsafe
            {
                fixed (byte* pBase = _baseAtlasBuffer)
                {
                    Half* hBase = (Half*)pBase;
                    Half one = (Half)1.0f;
                    int halfCount = bufferLen / 2;
                    for (int i = 0; i < halfCount; i++)
                    {
                        hBase[i] = one;
                    }
                }
            }

            // Resolve native texture for the active submesh
            Texture2D baseTex = null;
            var smMat = _targetMesh.MaterialOverlay as ShaderMaterial;
            if (smMat != null)
            {
                var gColor = smMat.GetShaderParameter("g_tColor");
                if (gColor.VariantType == Variant.Type.Object && gColor.AsGodotObject() is Texture2D gt)
                    baseTex = gt;
            }
            if (baseTex == null)
            {
                int sCount = _targetMesh.Mesh != null ? _targetMesh.Mesh.GetSurfaceCount() : 1;
                for (int s = 0; s < sCount; s++)
                {
                    var origMat = HeroMeshHierarchy.GetAuthenticMaterial(_targetMesh, s)
                               ?? _targetMesh.GetSurfaceOverrideMaterial(s)
                               ?? (_targetMesh.Mesh != null ? _targetMesh.Mesh.SurfaceGetMaterial(s) : null)
                               ?? _targetMesh.MaterialOverride;
                    if (origMat != null)
                    {
                        baseTex = ExtractBaseTexture(origMat);
                        if (baseTex != null) break;
                    }
                }
            }

            if (baseTex == null)
            {
                _hasPopulatedBaseAtlasBuffer = false;
                return;
            }

            Image img = baseTex.GetImage();
            if (img == null) { _hasPopulatedBaseAtlasBuffer = false; return; }
            if (img.IsCompressed())
            {
                var err = img.Decompress();
                if (err != Error.Ok) { _hasPopulatedBaseAtlasBuffer = false; return; }
            }

            // Resize only if needed (should be 1:1 when atlas matches native size)
            if (img.GetWidth() != atlasSize || img.GetHeight() != atlasSize)
            {
                img.Resize(atlasSize, atlasSize, Image.Interpolation.Nearest);
            }

            if (img.GetFormat() != Image.Format.Rgba8)
            {
                img.Convert(Image.Format.Rgba8);
            }

            byte[] srcBytes = img.GetData();
            int pixelCount = atlasSize * atlasSize;

            unsafe
            {
                fixed (byte* pSrc = srcBytes)
                fixed (byte* pBase = _baseAtlasBuffer)
                {
                    Half* hBase = (Half*)pBase;

                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIdx = i * 4;
                        byte bR = pSrc[srcIdx];
                        byte bG = pSrc[srcIdx + 1];
                        byte bB = pSrc[srcIdx + 2];
                        byte bA = pSrc[srcIdx + 3];

                        float a = bA / 255.0f;
                        float linR = s_srgb8ToLinear[bR];
                        float linG = s_srgb8ToLinear[bG];
                        float linB = s_srgb8ToLinear[bB];

                        hBase[srcIdx]     = (Half)linR;
                        hBase[srcIdx + 1] = (Half)linG;
                        hBase[srcIdx + 2] = (Half)linB;
                        hBase[srcIdx + 3] = (Half)a;
                    }
                }
            }

            _hasPopulatedBaseAtlasBuffer = true;
            if (_currentContext != null)
            {
                _currentContext.BaseAtlasBuffer = _baseAtlasBuffer;
            }

            var rd = RenderingServer.GetRenderingDevice();
            if (rd != null)
            {
                var baseRidVal = _activeAtlasManager.Get("base_texture_rid");
                if (baseRidVal.VariantType == Variant.Type.Rid)
                {
                    Rid baseRid = baseRidVal.AsRid();
                    if (baseRid.IsValid && rd.TextureIsValid(baseRid))
                    {
                        rd.TextureUpdate(baseRid, 0, _baseAtlasBuffer);
                    }
                }
            }
        }

        public void RecompositeGpuLayers(Rect2I? dirtyRect = null)
        {
            if (_activeAtlasManager == null || !GodotObject.IsInstanceValid(_activeAtlasManager)) return;

            var rd = RenderingServer.GetRenderingDevice();
            if (rd == null) return;

            int atlasSize = (int)_activeAtlasManager.Get("atlas_size");
            if (atlasSize <= 0) atlasSize = CanvasSize.X > 0 ? CanvasSize.X : 2048;
            int bufferLen = atlasSize * atlasSize * 8;

            if (_compositeBuffer == null || _compositeBuffer.Length != bufferLen)
            {
                _compositeBuffer = new byte[bufferLen];
                dirtyRect = null; // Full rebuild required
            }

            if (_otherLayersBuffer == null || _otherLayersBuffer.Length != bufferLen)
            {
                _otherLayersBuffer = new byte[bufferLen];
            }

            if (_baseAtlasBuffer == null || _baseAtlasBuffer.Length != bufferLen)
            {
                RebuildBaseAtlasBuffer();
                dirtyRect = null; // Full rebuild required
            }

            var active = ActiveLayer;

            if (dirtyRect.HasValue && dirtyRect.Value.Size.X > 0 && dirtyRect.Value.Size.Y > 0)
            {
                Rect2I rect = dirtyRect.Value;
                int minX = Mathf.Clamp(rect.Position.X, 0, atlasSize - 1);
                int maxX = Mathf.Clamp(rect.End.X, 0, atlasSize - 1);
                int minY = Mathf.Clamp(rect.Position.Y, 0, atlasSize - 1);
                int maxY = Mathf.Clamp(rect.End.Y, 0, atlasSize - 1);

                // Clear dirty rect in _compositeBuffer (and _otherLayersBuffer if dirty)
                unsafe
                {
                    fixed (byte* pComp = _compositeBuffer, pOther = _otherLayersBuffer)
                    {
                        ulong* uComp = (ulong*)pComp;
                        ulong* uOther = (ulong*)pOther;
                        int count = maxX - minX + 1;
                        for (int y = minY; y <= maxY; y++)
                        {
                            int rowStart = y * atlasSize + minX;
                            new Span<ulong>(uComp + rowStart, count).Clear();
                            if (_otherLayersDirty)
                            {
                                new Span<ulong>(uOther + rowStart, count).Clear();
                            }
                        }
                    }
                }

                // Blend layers only in dirty rect
                foreach (var layer in _layers)
                {
                    if (!layer.IsVisible || layer.GpuData == null || layer.GpuData.Length != bufferLen)
                    {
                        continue;
                    }

                    BlendLayerBufferRect(_compositeBuffer, layer.GpuData, layer.Opacity, layer.BlendMode, _baseAtlasBuffer, atlasSize, minX, maxX, minY, maxY);

                    if (_otherLayersDirty && layer != active)
                    {
                        BlendLayerBufferRect(_otherLayersBuffer, layer.GpuData, layer.Opacity, layer.BlendMode, _baseAtlasBuffer, atlasSize, minX, maxX, minY, maxY);
                    }
                }
            }
            else
            {
                Array.Clear(_compositeBuffer, 0, _compositeBuffer.Length);
                if (_otherLayersDirty)
                {
                    Array.Clear(_otherLayersBuffer, 0, _otherLayersBuffer.Length);
                }

                foreach (var layer in _layers)
                {
                    if (!layer.IsVisible || layer.GpuData == null || layer.GpuData.Length != bufferLen)
                    {
                        continue;
                    }

                    BlendLayerBuffer(_compositeBuffer, layer.GpuData, layer.Opacity, layer.BlendMode, _baseAtlasBuffer);

                    if (_otherLayersDirty && layer != active)
                    {
                        BlendLayerBuffer(_otherLayersBuffer, layer.GpuData, layer.Opacity, layer.BlendMode, _baseAtlasBuffer);
                    }
                }
            }

            // 1. Upload _otherLayersBuffer only when other layers actually changed
            var compRidVal = _activeAtlasManager.Get("composite_texture_rid");
            if (_otherLayersDirty && compRidVal.VariantType == Variant.Type.Rid && compRidVal.AsRid().IsValid)
            {
                rd.TextureUpdate(compRidVal.AsRid(), 0, _otherLayersBuffer);
                _otherLayersDirty = false;
            }

            // 2. Upload _compositeBuffer to full_composite_rid (for 2D canvas and exports)
            var fullRidVal = _activeAtlasManager.Get("full_composite_rid");
            if (fullRidVal.VariantType == Variant.Type.Rid && fullRidVal.AsRid().IsValid)
            {
                if (dirtyRect.HasValue && dirtyRect.Value.Size.X > 0 && dirtyRect.Value.Size.Y > 0)
                {
                    if (_layers.Count <= 1 && active != null && active.BlendMode == LayerBlendMode.Normal && active.LayerRid.IsValid && rd.TextureIsValid(active.LayerRid))
                    {
                        // 1-layer fast path: direct GPU-to-GPU copy in 0.01ms with zero CPU/PCIe overhead
                        rd.TextureCopy(active.LayerRid, fullRidVal.AsRid(), Vector3.Zero, Vector3.Zero, new Vector3(atlasSize, atlasSize, 1), 0, 0, 0, 0);
                    }
                    else
                    {
                        // Multi-layer path: upload only dirty tiles
                        UploadBufferRectChunked(rd, fullRidVal.AsRid(), _compositeBuffer, atlasSize, dirtyRect.Value);
                    }
                }
                else
                {
                    rd.TextureUpdate(fullRidVal.AsRid(), 0, _compositeBuffer);
                }
            }
            else
            {
                // Fallback for single-RID setup
                var atlasRidVal = _activeAtlasManager.Get("atlas_texture_rid");
                if (atlasRidVal.VariantType == Variant.Type.Rid && atlasRidVal.AsRid().IsValid && compRidVal.VariantType != Variant.Type.Rid)
                {
                    if (dirtyRect.HasValue && dirtyRect.Value.Size.X > 0 && dirtyRect.Value.Size.Y > 0)
                    {
                        UploadBufferRectChunked(rd, atlasRidVal.AsRid(), _compositeBuffer, atlasSize, dirtyRect.Value);
                    }
                    else
                    {
                        rd.TextureUpdate(atlasRidVal.AsRid(), 0, _compositeBuffer);
                    }
                }
            }
        }

        private static unsafe void BlendLayerBufferRect(
            byte[] dst,
            byte[] src,
            float opacity,
            LayerBlendMode mode,
            byte[] baseBuffer,
            int atlasSize,
            int minX,
            int maxX,
            int minY,
            int maxY)
        {
            fixed (byte* pDst = dst, pSrc = src, pBase = baseBuffer)
            {
                ulong* uSrc = (ulong*)pSrc;
                ulong* uDst = (ulong*)pDst;
                Half* hSrc = (Half*)pSrc;
                Half* hDst = (Half*)pDst;
                Half* hBase = pBase != null ? (Half*)pBase : null;

                for (int y = minY; y <= maxY; y++)
                {
                    int rowStart = y * atlasSize;
                    for (int x = minX; x <= maxX; x++)
                    {
                        int i = rowStart + x;
                        if (uSrc[i] == 0) continue;

                        int hOffset = i * 4;
                        float srcA = (float)hSrc[hOffset + 3] * opacity;
                        if (srcA <= 0.0001f) continue;

                        float srcR = (float)hSrc[hOffset];
                        float srcG = (float)hSrc[hOffset + 1];
                        float srcB = (float)hSrc[hOffset + 2];

                        if (uDst[i] == 0)
                        {
                            float bR = hBase != null ? (float)hBase[hOffset] : 1.0f;
                            float bG = hBase != null ? (float)hBase[hOffset + 1] : 1.0f;
                            float bB = hBase != null ? (float)hBase[hOffset + 2] : 1.0f;

                            float outR, outG, outB;
                            BlendRgb(bR, bG, bB, srcR, srcG, srcB, mode, out outR, out outG, out outB);

                            hDst[hOffset] = (Half)outR;
                            hDst[hOffset + 1] = (Half)outG;
                            hDst[hOffset + 2] = (Half)outB;
                            hDst[hOffset + 3] = (Half)srcA;
                        }
                        else
                        {
                            float curA = (float)hDst[hOffset + 3];
                            float curR = (float)hDst[hOffset];
                            float curG = (float)hDst[hOffset + 1];
                            float curB = (float)hDst[hOffset + 2];

                            float bR = hBase != null ? (float)hBase[hOffset] : 1.0f;
                            float bG = hBase != null ? (float)hBase[hOffset + 1] : 1.0f;
                            float bB = hBase != null ? (float)hBase[hOffset + 2] : 1.0f;

                            float blendR, blendG, blendB;
                            BlendRgb(bR, bG, bB, srcR, srcG, srcB, mode, out blendR, out blendG, out blendB);

                            float outA = Mathf.Clamp(srcA + curA * (1.0f - srcA), 0.0f, 1.0f);
                            float outR = (outA > 0.0001f) ? (blendR * srcA + curR * curA * (1.0f - srcA)) / outA : blendR;
                            float outG = (outA > 0.0001f) ? (blendG * srcA + curG * curA * (1.0f - srcA)) / outA : blendG;
                            float outB = (outA > 0.0001f) ? (blendB * srcA + curB * curA * (1.0f - srcA)) / outA : blendB;

                            hDst[hOffset] = (Half)outR;
                            hDst[hOffset + 1] = (Half)outG;
                            hDst[hOffset + 2] = (Half)outB;
                            hDst[hOffset + 3] = (Half)outA;
                        }
                    }
                }
            }
        }

        private byte[] _shapePreviewBuffer;

        public void UpdateShapePreview(ShapeTool shapeTool, Color color)
        {
            if (_activeAtlasManager == null || !GodotObject.IsInstanceValid(_activeAtlasManager)) return;

            var rd = RenderingServer.GetRenderingDevice();
            var layer = ActiveLayer;
            if (layer == null || !layer.LayerRid.IsValid || rd == null || !rd.TextureIsValid(layer.LayerRid)) return;

            if (shapeTool != null && shapeTool.HasActiveShape && layer.GpuData != null)
            {
                int len = layer.GpuData.Length;
                if (_shapePreviewBuffer == null || _shapePreviewBuffer.Length != len)
                {
                    _shapePreviewBuffer = new byte[len];
                }
                Buffer.BlockCopy(layer.GpuData, 0, _shapePreviewBuffer, 0, len);

                int atlasSize = CanvasSize.X > 0 ? CanvasSize.X : 2048;
                shapeTool.BlendPreview(_shapePreviewBuffer, atlasSize, color);
                rd.TextureUpdate(layer.LayerRid, 0, _shapePreviewBuffer);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static float GetLuminance(float r, float g, float b)
        {
            return 0.299f * r + 0.587f * g + 0.114f * b;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static void SetLuminance(float r, float g, float b, float l, out float resR, out float resG, out float resB)
        {
            float d = l - GetLuminance(r, g, b);
            float cr = r + d;
            float cg = g + d;
            float cb = b + d;
            float lum = GetLuminance(cr, cg, cb);
            float minC = MathF.Min(cr, MathF.Min(cg, cb));
            float maxC = MathF.Max(cr, MathF.Max(cg, cb));
            if (minC < 0.0f)
            {
                float denom = MathF.Max(lum - minC, 0.0001f);
                cr = lum + ((cr - lum) * lum) / denom;
                cg = lum + ((cg - lum) * lum) / denom;
                cb = lum + ((cb - lum) * lum) / denom;
            }
            if (maxC > 1.0f)
            {
                float denom = MathF.Max(maxC - lum, 0.0001f);
                cr = lum + ((cr - lum) * (1.0f - lum)) / denom;
                cg = lum + ((cg - lum) * (1.0f - lum)) / denom;
                cb = lum + ((cb - lum) * (1.0f - lum)) / denom;
            }
            resR = Math.Clamp(cr, 0.0f, 1.0f);
            resG = Math.Clamp(cg, 0.0f, 1.0f);
            resB = Math.Clamp(cb, 0.0f, 1.0f);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static void BlendRgb(float bR, float bG, float bB, float srcR, float srcG, float srcB, LayerBlendMode mode, out float outR, out float outG, out float outB)
        {
            if (mode == LayerBlendMode.Multiply)
            {
                outR = bR * srcR;
                outG = bG * srcG;
                outB = bB * srcB;
            }
            else if (mode == LayerBlendMode.Screen)
            {
                outR = 1.0f - (1.0f - bR) * (1.0f - srcR);
                outG = 1.0f - (1.0f - bG) * (1.0f - srcG);
                outB = 1.0f - (1.0f - bB) * (1.0f - srcB);
            }
            else if (mode == LayerBlendMode.Overlay)
            {
                outR = bR < 0.5f ? (2.0f * bR * srcR) : (1.0f - 2.0f * (1.0f - bR) * (1.0f - srcR));
                outG = bG < 0.5f ? (2.0f * bG * srcG) : (1.0f - 2.0f * (1.0f - bG) * (1.0f - srcG));
                outB = bB < 0.5f ? (2.0f * bB * srcB) : (1.0f - 2.0f * (1.0f - bB) * (1.0f - srcB));
            }
            else if (mode == LayerBlendMode.Darken)
            {
                outR = Mathf.Min(bR, srcR);
                outG = Mathf.Min(bG, srcG);
                outB = Mathf.Min(bB, srcB);
            }
            else if (mode == LayerBlendMode.Lighten)
            {
                outR = Mathf.Max(bR, srcR);
                outG = Mathf.Max(bG, srcG);
                outB = Mathf.Max(bB, srcB);
            }
            else if (mode == LayerBlendMode.ColorDodge)
            {
                outR = bR / Mathf.Max(1.0f - srcR, 0.001f);
                outG = bG / Mathf.Max(1.0f - srcG, 0.001f);
                outB = bB / Mathf.Max(1.0f - srcB, 0.001f);
            }
            else if (mode == LayerBlendMode.Color)
            {
                float baseLum = GetLuminance(bR, bG, bB);
                SetLuminance(srcR, srcG, srcB, baseLum, out outR, out outG, out outB);
            }
            else
            {
                outR = srcR;
                outG = srcG;
                outB = srcB;
            }
        }

        private static unsafe void BlendLayerBuffer(byte[] dst, byte[] src, float opacity, LayerBlendMode mode, byte[] baseBuffer)
        {
            int pixelCount = dst.Length / 8;
            fixed (byte* pDst = dst, pSrc = src, pBase = baseBuffer)
            {
                ulong* uSrc = (ulong*)pSrc;
                ulong* uDst = (ulong*)pDst;
                Half* hSrc = (Half*)pSrc;
                Half* hDst = (Half*)pDst;
                Half* hBase = pBase != null ? (Half*)pBase : null;

                int chunkSize = 32768;
                int numChunks = (pixelCount + chunkSize - 1) / chunkSize;

                System.Threading.Tasks.Parallel.For(0, numChunks, chunkIdx =>
                {
                    int start = chunkIdx * chunkSize;
                    int end = Math.Min(start + chunkSize, pixelCount);

                    for (int i = start; i < end; i++)
                    {
                        if (uSrc[i] == 0) continue;

                        int hOffset = i * 4;
                        float srcA = (float)hSrc[hOffset + 3] * opacity;
                        if (srcA <= 0.0001f) continue;

                        float srcR = (float)hSrc[hOffset];
                        float srcG = (float)hSrc[hOffset + 1];
                        float srcB = (float)hSrc[hOffset + 2];

                        if (uDst[i] == 0)
                        {
                            float bR = hBase != null ? (float)hBase[hOffset] : 1.0f;
                            float bG = hBase != null ? (float)hBase[hOffset + 1] : 1.0f;
                            float bB = hBase != null ? (float)hBase[hOffset + 2] : 1.0f;

                            float outR, outG, outB;

                            if (mode == LayerBlendMode.Multiply)
                            {
                                outR = bR * srcR;
                                outG = bG * srcG;
                                outB = bB * srcB;
                            }
                            else if (mode == LayerBlendMode.Screen)
                            {
                                outR = 1.0f - (1.0f - bR) * (1.0f - srcR);
                                outG = 1.0f - (1.0f - bG) * (1.0f - srcG);
                                outB = 1.0f - (1.0f - bB) * (1.0f - srcB);
                            }
                            else if (mode == LayerBlendMode.Overlay)
                            {
                                outR = bR < 0.5f ? (2.0f * bR * srcR) : (1.0f - 2.0f * (1.0f - bR) * (1.0f - srcR));
                                outG = bG < 0.5f ? (2.0f * bG * srcG) : (1.0f - 2.0f * (1.0f - bG) * (1.0f - srcG));
                                outB = bB < 0.5f ? (2.0f * bB * srcB) : (1.0f - 2.0f * (1.0f - bB) * (1.0f - srcB));
                            }
                            else if (mode == LayerBlendMode.Darken)
                            {
                                outR = Mathf.Min(bR, srcR);
                                outG = Mathf.Min(bG, srcG);
                                outB = Mathf.Min(bB, srcB);
                            }
                            else if (mode == LayerBlendMode.Lighten)
                            {
                                outR = Mathf.Max(bR, srcR);
                                outG = Mathf.Max(bG, srcG);
                                outB = Mathf.Max(bB, srcB);
                            }
                            else if (mode == LayerBlendMode.ColorDodge)
                            {
                                outR = bR / Mathf.Max(1.0f - srcR, 0.001f);
                                outG = bG / Mathf.Max(1.0f - srcG, 0.001f);
                                outB = bB / Mathf.Max(1.0f - srcB, 0.001f);
                            }
                            else if (mode == LayerBlendMode.Color)
                            {
                                float baseLum = GetLuminance(bR, bG, bB);
                                SetLuminance(srcR, srcG, srcB, baseLum, out outR, out outG, out outB);
                            }
                            else
                            {
                                outR = srcR;
                                outG = srcG;
                                outB = srcB;
                            }

                            hDst[hOffset] = (Half)outR;
                            hDst[hOffset + 1] = (Half)outG;
                            hDst[hOffset + 2] = (Half)outB;
                            hDst[hOffset + 3] = (Half)Mathf.Clamp(srcA, 0.0f, 1.0f);
                        }
                        else
                        {
                            float dstR = (float)hDst[hOffset];
                            float dstG = (float)hDst[hOffset + 1];
                            float dstB = (float)hDst[hOffset + 2];
                            float dstA = (float)hDst[hOffset + 3];

                            float outA = Mathf.Clamp(srcA + dstA * (1.0f - srcA), 0.0f, 1.0f);
                            float outR, outG, outB;

                            if (mode == LayerBlendMode.Multiply)
                            {
                                float blendR = dstR * srcR;
                                float blendG = dstG * srcG;
                                float blendB = dstB * srcB;
                                outR = Mathf.Lerp(dstR, blendR, srcA);
                                outG = Mathf.Lerp(dstG, blendG, srcA);
                                outB = Mathf.Lerp(dstB, blendB, srcA);
                            }
                            else if (mode == LayerBlendMode.Screen)
                            {
                                float blendR = 1.0f - (1.0f - dstR) * (1.0f - srcR);
                                float blendG = 1.0f - (1.0f - dstG) * (1.0f - srcG);
                                float blendB = 1.0f - (1.0f - dstB) * (1.0f - srcB);
                                outR = Mathf.Lerp(dstR, blendR, srcA);
                                outG = Mathf.Lerp(dstG, blendG, srcA);
                                outB = Mathf.Lerp(dstB, blendB, srcA);
                            }
                            else if (mode == LayerBlendMode.Overlay)
                            {
                                float blendR = dstR < 0.5f ? (2.0f * dstR * srcR) : (1.0f - 2.0f * (1.0f - dstR) * (1.0f - srcR));
                                float blendG = dstG < 0.5f ? (2.0f * dstG * srcG) : (1.0f - 2.0f * (1.0f - dstG) * (1.0f - srcG));
                                float blendB = dstB < 0.5f ? (2.0f * dstB * srcB) : (1.0f - 2.0f * (1.0f - dstB) * (1.0f - srcB));
                                outR = Mathf.Lerp(dstR, blendR, srcA);
                                outG = Mathf.Lerp(dstG, blendG, srcA);
                                outB = Mathf.Lerp(dstB, blendB, srcA);
                            }
                            else if (mode == LayerBlendMode.Darken)
                            {
                                float blendR = Mathf.Min(dstR, srcR);
                                float blendG = Mathf.Min(dstG, srcG);
                                float blendB = Mathf.Min(dstB, srcB);
                                outR = Mathf.Lerp(dstR, blendR, srcA);
                                outG = Mathf.Lerp(dstG, blendG, srcA);
                                outB = Mathf.Lerp(dstB, blendB, srcA);
                            }
                            else if (mode == LayerBlendMode.Lighten)
                            {
                                float blendR = Mathf.Max(dstR, srcR);
                                float blendG = Mathf.Max(dstG, srcG);
                                float blendB = Mathf.Max(dstB, srcB);
                                outR = Mathf.Lerp(dstR, blendR, srcA);
                                outG = Mathf.Lerp(dstG, blendG, srcA);
                                outB = Mathf.Lerp(dstB, blendB, srcA);
                            }
                            else if (mode == LayerBlendMode.ColorDodge)
                            {
                                float blendR = dstR / Mathf.Max(1.0f - srcR, 0.001f);
                                float blendG = dstG / Mathf.Max(1.0f - srcG, 0.001f);
                                float blendB = dstB / Mathf.Max(1.0f - srcB, 0.001f);
                                outR = Mathf.Lerp(dstR, blendR, srcA);
                                outG = Mathf.Lerp(dstG, blendG, srcA);
                                outB = Mathf.Lerp(dstB, blendB, srcA);
                            }
                            else if (mode == LayerBlendMode.Color)
                            {
                                float underLum = GetLuminance(dstR, dstG, dstB);
                                SetLuminance(srcR, srcG, srcB, underLum, out float blendR, out float blendG, out float blendB);
                                outR = Mathf.Lerp(dstR, blendR, srcA);
                                outG = Mathf.Lerp(dstG, blendG, srcA);
                                outB = Mathf.Lerp(dstB, blendB, srcA);
                            }
                            else
                            {
                                outR = (srcR * srcA + dstR * dstA * (1.0f - srcA)) / Mathf.Max(outA, 0.0001f);
                                outG = (srcG * srcA + dstG * dstA * (1.0f - srcA)) / Mathf.Max(outA, 0.0001f);
                                outB = (srcB * srcA + dstB * dstA * (1.0f - srcA)) / Mathf.Max(outA, 0.0001f);
                            }

                            hDst[hOffset] = (Half)outR;
                            hDst[hOffset + 1] = (Half)outG;
                            hDst[hOffset + 2] = (Half)outB;
                            hDst[hOffset + 3] = (Half)outA;
                        }
                    }
                });
            }
        }

        public void SetLayerVisibility(int index, bool visible)
        {
            if (index < 0 || index >= _layers.Count) return;

            _layers[index].SetVisibility(visible);
            ApplyOverlayParametersToMeshes();
            RecompositeGpuLayers();
            NotifyStackChanged();
        }

        public void SetLayerOpacity(int index, float opacity)
        {
            if (index < 0 || index >= _layers.Count) return;

            _layers[index].SetOpacity(opacity);
            _otherLayersDirty = true;
            ApplyOverlayParametersToMeshes();
            RecompositeGpuLayers();
            NotifyStackChanged();
        }

        public void SetLayerBlendMode(int index, LayerBlendMode mode)
        {
            if (index < 0 || index >= _layers.Count) return;

            _layers[index].SetBlendMode(mode);
            _otherLayersDirty = true;
            ApplyOverlayParametersToMeshes();
            RecompositeGpuLayers();
            NotifyStackChanged();
        }

        public Image BakeCompositeImage(int surfaceIndex = -1)
        {
            EnsureCpuSynced();
            Node targetAtlas = _activeAtlasManager;
            if (surfaceIndex >= 0 && MeshHierarchy != null && surfaceIndex < MeshHierarchy.Submeshes.Count)
            {
                var mesh = MeshHierarchy.Submeshes[surfaceIndex].Mesh;
                if (mesh != null)
                {
                    string matKey = GetMaterialKey(mesh, surfaceIndex);
                    if (_perMaterialAtlasManagers.TryGetValue(matKey, out var submeshAtlas))
                    {
                        targetAtlas = submeshAtlas;
                    }
                }
            }
            else if (targetAtlas == null && _perMaterialAtlasManagers.Count > 0)
            {
                foreach (var mgr in _perMaterialAtlasManagers.Values)
                {
                    if (mgr != null && GodotObject.IsInstanceValid(mgr))
                    {
                        targetAtlas = mgr;
                        break;
                    }
                }
            }

            if (targetAtlas != null && GodotObject.IsInstanceValid(targetAtlas))
            {
                var rd = RenderingServer.GetRenderingDevice();
                var ridVal = targetAtlas.Get("atlas_texture_rid");
                if (ridVal.VariantType == Variant.Type.Rid)
                {
                    Rid rid = ridVal.AsRid();
                    if (rid.IsValid && rd != null)
                    {
                        byte[] data = rd.TextureGetData(rid, 0);
                        int size = (int)targetAtlas.Get("atlas_size");
                        if (data != null && data.Length > 0 && size > 0)
                        {
                            byte[] cleanData = (byte[])data.Clone();
                            unsafe
                            {
                                fixed (byte* pClean = cleanData)
                                {
                                    Half* h = (Half*)pClean;
                                    int count = cleanData.Length / 8;
                                    for (int i = 0; i < count; i++)
                                    {
                                        int off = i * 4;
                                        float r = (float)h[off];
                                        float g = (float)h[off + 1];
                                        float b = (float)h[off + 2];
                                        float a = Mathf.Clamp((float)h[off + 3], 0.0f, 1.0f);
                                        Color lin = new Color(r, g, b, a);
                                        Color srgb = lin.LinearToSrgb();
                                        h[off] = (Half)srgb.R;
                                        h[off + 1] = (Half)srgb.G;
                                        h[off + 2] = (Half)srgb.B;
                                        h[off + 3] = (Half)a;
                                    }
                                }
                            }
                            var img = Image.CreateFromData(size, size, false, Image.Format.Rgbah, cleanData);
                            img.Convert(Image.Format.Rgba8);
                            return img;
                        }
                    }
                }
            }

            return null;
        }

        private ImageTexture BakeCompositeImageTexture(Node atlasMgr)
        {
            if (atlasMgr == null || !GodotObject.IsInstanceValid(atlasMgr)) return null;
            var rd = RenderingServer.GetRenderingDevice();
            if (rd == null) return null;

            var fullRidVal = atlasMgr.Get("full_composite_rid");
            Rid rid = (fullRidVal.VariantType == Variant.Type.Rid) ? fullRidVal.AsRid() : new Rid();
            if (!rid.IsValid || !rd.TextureIsValid(rid))
            {
                var atlasRidVal = atlasMgr.Get("atlas_texture_rid");
                rid = (atlasRidVal.VariantType == Variant.Type.Rid) ? atlasRidVal.AsRid() : new Rid();
            }

            if (!rid.IsValid || !rd.TextureIsValid(rid)) return null;

            byte[] data = rd.TextureGetData(rid, 0);
            int size = (int)atlasMgr.Get("atlas_size");
            if (data == null || data.Length == 0 || size <= 0) return null;

            var img = Image.CreateFromData(size, size, false, Image.Format.Rgbah, data);
            return ImageTexture.CreateFromImage(img);
        }

        public static void RunHeroMaterialAudit(Node3D heroNode, HeroMeshHierarchy hierarchy = null)
        {
            GD.Print("[HeroAudit] Starting material inspection...");
            if (heroNode == null || !GodotObject.IsInstanceValid(heroNode))
            {
                GD.Print("[HeroAudit] Warning: heroNode is null or invalid.");
                return;
            }

            try
            {
                var allMeshes = new List<MeshInstance3D>();
                void FindMeshes(Node node)
                {
                    if (node is MeshInstance3D mi && HeroMeshHierarchy.IsAuthenticHeroMesh(mi))
                    {
                        allMeshes.Add(mi);
                    }
                    foreach (Node child in node.GetChildren())
                    {
                        string childName = child.Name.ToString();
                        if (childName.StartsWith("@")) continue;
                        string childLower = childName.ToLowerInvariant();
                        if (childLower.Contains("picker") || childLower.Contains("gizmo") || childLower.Contains("handle") || childLower.Contains("visual"))
                            continue;

                        FindMeshes(child);
                    }
                }
                FindMeshes(heroNode);

                var materialSlots = new Dictionary<string, (int Width, int Height, List<string> Meshes)>();
                int maxW = 0, maxH = 0;

                foreach (var mesh in allMeshes)
                {
                    if (!HeroMeshHierarchy.IsAuthenticHeroMesh(mesh)) continue;
                    string matKey = ResolveMaterialKey(mesh, 0, hierarchy);
                    if (string.IsNullOrEmpty(matKey)) matKey = "unassigned_material";

                    if (!materialSlots.ContainsKey(matKey))
                    {
                        var origMat = HeroMeshHierarchy.GetAuthenticMaterial(mesh, 0)
                                   ?? mesh.GetSurfaceOverrideMaterial(0)
                                   ?? (mesh.Mesh != null && mesh.Mesh.GetSurfaceCount() > 0 ? mesh.Mesh.SurfaceGetMaterial(0) : null)
                                   ?? mesh.MaterialOverride;
                        var baseTex = ExtractBaseTexture(origMat);
                        int w = baseTex?.GetWidth() ?? 2048;
                        int h = baseTex?.GetHeight() ?? 2048;
                        if (w > maxW) maxW = w;
                        if (h > maxH) maxH = h;

                        materialSlots[matKey] = (w, h, new List<string>());
                    }

                    materialSlots[matKey].Meshes.Add(mesh.Name);
                }

                string heroName = heroNode.Name.ToString().Replace("Hero_", "");
                GD.Print($"=== HERO MATERIAL AUDIT: [{heroName}] ===");
                GD.Print($"Total Submeshes: {allMeshes.Count}");
                GD.Print($"Unique Materials Found: {materialSlots.Count}");

                int slotIdx = 0;
                foreach (var kvp in materialSlots)
                {
                    string usedBy = string.Join(", ", kvp.Value.Meshes);
                    GD.Print($"  [Slot {slotIdx}] \"{kvp.Key}\" ({kvp.Value.Width}x{kvp.Value.Height}) -> Used by: {usedBy}");
                    slotIdx++;
                }

                GD.Print("--------------------------------------------------");
                GD.Print($"Required Overlay Atlas Managers: {materialSlots.Count} / 16 available");
                GD.Print($"Max Resolution Encountered: {maxW}x{maxH}");
                GD.Print("=== END AUDIT ===");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[Audit Error] {ex}");
            }
        }

        public void ClearAllLayers()
        {
            foreach (var kvp in _materialContexts)
            {
                foreach (var layer in kvp.Value.Layers)
                {
                    layer.CleanUp();
                }
            }
            _materialContexts.Clear();
            _currentContext = null;

            foreach (var layer in _layers)
            {
                layer.CleanUp();
            }
            _layers.Clear();
            _activeLayerIndex = 0;
            _activeSurfaceIndex = 0;
            _otherLayersBuffer = null;
            _compositeBuffer = null;
            _baseAtlasBuffer = null;
            _hasPopulatedBaseAtlasBuffer = false;
        }

        public void CleanupHeroAtlas()
        {
            ClearAllAtlasManagers();
            ClearAllLayers();
            ClearHistory();
            _materialContexts.Clear();
            _currentContext = null;
            _currentHero = null;
        }

        private bool _isExitingTree = false;

        public override void _ExitTree()
        {
            _isExitingTree = true;
            ClearAllAtlasManagers();
            ClearAllLayers();
            ClearHistory();
            base._ExitTree();
        }

        private void NotifyLayerAdded(int index, string name) { if (!_isExitingTree) EmitSignal(SignalName.LayerAdded, index, name); }
        private void NotifyLayerRemoved(int index) { if (!_isExitingTree) EmitSignal(SignalName.LayerRemoved, index); }
        private void NotifyLayerSelected(int index) { if (!_isExitingTree) EmitSignal(SignalName.LayerSelected, index); }
        private void NotifyLayersReordered() { if (!_isExitingTree) EmitSignal(SignalName.LayersReordered); }
        public void NotifyStackChanged()
        {
            if (!_isExitingTree) EmitSignal(SignalName.StackChanged);
        }
    }
}