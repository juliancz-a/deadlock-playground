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

        private Node _atlasManager;
        public Node AtlasManager => _atlasManager;
        private Node3D _currentHero;
        public Node3D CurrentHero => _currentHero;
        private byte[] _compositeBuffer;

        public void SetupForHero(Node3D heroNode)
        {
            if (_currentHero == heroNode && _atlasManager != null && GodotObject.IsInstanceValid(_atlasManager))
            {
                return;
            }

            _currentHero = heroNode;
            if (_currentHero == null)
            {
                ClearAtlasManager();
                ClearAllLayers();
                return;
            }

            ClearAtlasManager();
            InitializeOverlayAtlas();

            ClearAllLayers();
            AddNewLayer("Paint Layer 1");
            RecompositeGpuLayers();
            NotifyStackChanged();
            NotifyLayerSelected(0);
        }

        public void InitializeOverlayAtlas()
        {
            if (_atlasManager != null && GodotObject.IsInstanceValid(_atlasManager))
            {
                return;
            }

            if (_currentHero == null || !GodotObject.IsInstanceValid(_currentHero))
            {
                return;
            }

            var atlasScript = GD.Load<GDScript>("res://addons/gpu_texture_painter/manager/overlay_atlas_manager.gd");
            if (atlasScript == null)
            {
                GD.PrintErr("[SkinLayerManager] Failed to load overlay_atlas_manager.gd");
                return;
            }

            var shader = GD.Load<Shader>("res://shaders/hero_painter_overlay.gdshader")
                      ?? GD.Load<Shader>("res://assets/shaders/painter/hero_painter_overlay.gdshader");

            if (shader == null)
            {
                GD.PrintErr("[SkinLayerManager] Critical Error: hero_painter_overlay.gdshader not found!");
            }

            _atlasManager = (Node)atlasScript.New();
            if (_atlasManager == null)
            {
                GD.PrintErr("[SkinLayerManager] Failed to instantiate OverlayAtlasManager.");
                return;
            }

            _atlasManager.Name = "ActiveOverlayAtlasManager";
            _atlasManager.Set("atlas_size", (int)CanvasSize.X);
            if (shader != null)
            {
                _atlasManager.Set("overlay_shader", shader);
            }

            // Direct child of the hero node so it scopes only to hero submeshes
            _currentHero.AddChild(_atlasManager);

            // Execute atlas packing and material_overlay application
            try
            {
                _atlasManager.Call("apply");
                ApplyOverlayParametersToMeshes();
                GD.Print($"[SkinLayerManager] Initialized and applied OverlayAtlasManager on {_currentHero.Name}!");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[SkinLayerManager] Exception executing apply on OverlayAtlasManager: {ex.Message}");
            }
        }

        public void ClearAtlasManager()
        {
            if (_atlasManager != null && GodotObject.IsInstanceValid(_atlasManager))
            {
                if (_atlasManager.IsInGroup("overlay_atlas_managers"))
                {
                    _atlasManager.RemoveFromGroup("overlay_atlas_managers");
                }
                var parent = _atlasManager.GetParent();
                if (parent != null)
                {
                    parent.RemoveChild(_atlasManager);
                }
                _atlasManager.QueueFree();
                _atlasManager = null;
            }
        }

        public void SetCanvasResolution(Vector2I newSize)
        {
            if (newSize == CanvasSize) return;
            CanvasSize = newSize;

            int newBufferLen = CanvasSize.X * CanvasSize.Y * 8;
            foreach (var layer in _layers)
            {
                layer.GpuData = new byte[newBufferLen];
            }

            // Purge previous undo/redo snapshots
            _undoStack.Clear();
            _redoStack.Clear();
            _compositeBuffer = null;
            _baseAtlasBuffer = null;
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);

            if (_atlasManager != null && GodotObject.IsInstanceValid(_atlasManager))
            {
                _atlasManager.Set("atlas_size", (int)CanvasSize.X);
                _atlasManager.Call("apply");
            }

            RecompositeGpuLayers();
            RecordInitialSnapshot();
            NotifyStackChanged();
        }

        public void SetupForMesh(MeshInstance3D meshInstance, int defaultSurfaceIndex = 0)
        {
            if (meshInstance == null || meshInstance.Mesh == null) return;

            EnsureShadersLoaded();
            _targetMesh = meshInstance;
            _activeSurfaceIndex = defaultSurfaceIndex;

            if (_currentHero == null || !GodotObject.IsInstanceValid(_currentHero))
            {
                Node current = _targetMesh.GetParent();
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

            if (_atlasManager == null && _currentHero != null)
            {
                InitializeOverlayAtlas();
            }

            if (_layers.Count == 0)
            {
                AddNewLayer("Paint Layer 1");
            }

            SetPaintTargetMesh(_targetMesh);
            NotifyStackChanged();
            NotifyLayerSelected(_activeLayerIndex);
        }

        public void EnsureMaterialBinding(MeshInstance3D activeTargetMesh, int surfaceIndex = 0)
        {
            if (activeTargetMesh == null || !GodotObject.IsInstanceValid(activeTargetMesh)) return;

            if (_atlasManager == null && _currentHero != null)
            {
                InitializeOverlayAtlas();
            }
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

            if (mat is StandardMaterial3D stdMat)
            {
                return stdMat.AlbedoTexture;
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
            AddChild(newLayer.Viewport);

            _layers.Add(newLayer);
            _activeLayerIndex = _layers.Count - 1;
            NotifyLayerAdded(_activeLayerIndex, layerName);
            NotifyLayerSelected(_activeLayerIndex);
            NotifyStackChanged();
            return newLayer;
        }

        public void DeleteActiveLayer()
        {
            DeleteLayer(_activeLayerIndex);
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
            layer.CleanUp();

            if (_layers.Count == 0)
            {
                AddNewLayer("Paint Layer 1");
            }
            else if (_activeLayerIndex >= _layers.Count)
            {
                _activeLayerIndex = _layers.Count - 1;
            }

            RecompositeGpuLayers();

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

        public void SelectLayer(int index)
        {
            if (index < 0 || index >= _layers.Count) return;

            _activeLayerIndex = index;
            ApplyOverlayParametersToMeshes();
            NotifyLayerSelected(index);
        }

        public void ClearCurrentLayer()
        {
            RecordInitialSnapshot();
            var layer = ActiveLayer;
            if (layer != null)
            {
                layer.GpuData = new byte[CanvasSize.X * CanvasSize.Y * 8];
                layer.Clear();
            }
            RecompositeGpuLayers();
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
            public Dictionary<int, byte[]> LayerData = new();
        }

        private readonly List<LayerPaintSnapshot> _undoStack = new();
        private readonly List<LayerPaintSnapshot> _redoStack = new();
        private const int MaxUndoSnapshots = 10;

        public bool CanUndo => _undoStack.Count > 1;
        public bool CanRedo => _redoStack.Count > 0;

        public void RecordInitialSnapshot()
        {
            if (_undoStack.Count == 0)
            {
                RecordUndoSnapshot();
            }
        }

        public void RecordUndoSnapshot()
        {
            if (_layers.Count == 0) return;

            var snap = new LayerPaintSnapshot
            {
                ActiveLayerIndex = _activeLayerIndex
            };

            for (int i = 0; i < _layers.Count; i++)
            {
                if (_layers[i].GpuData != null && _layers[i].GpuData.Length > 0)
                {
                    snap.LayerData[i] = (byte[])_layers[i].GpuData.Clone();
                }
            }

            _undoStack.Add(snap);
            if (_undoStack.Count > MaxUndoSnapshots + 1)
            {
                _undoStack.RemoveAt(0);
            }
            _redoStack.Clear();
        }

        public byte[] GetAtlasDataSnapshot()
        {
            if (_atlasManager == null || !GodotObject.IsInstanceValid(_atlasManager)) return null;
            var rd = RenderingServer.GetRenderingDevice();
            var ridVal = _atlasManager.Get("atlas_texture_rid");
            if (ridVal.VariantType != Variant.Type.Rid) return null;
            Rid rid = ridVal.AsRid();
            if (!rid.IsValid || rd == null) return null;
            return rd.TextureGetData(rid, 0);
        }

        public void Undo()
        {
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
            GD.Print($"[SkinLayerManager] Redo performed. Remaining redo steps: {_redoStack.Count}");
        }

        private void RestoreSnapshot(LayerPaintSnapshot snapshot)
        {
            if (snapshot == null) return;
            if (snapshot.ActiveLayerIndex >= 0 && snapshot.ActiveLayerIndex < _layers.Count)
            {
                _activeLayerIndex = snapshot.ActiveLayerIndex;
            }

            for (int i = 0; i < _layers.Count; i++)
            {
                if (snapshot.LayerData.TryGetValue(i, out var rawData))
                {
                    _layers[i].GpuData = (byte[])rawData.Clone();
                }
                else if (_layers[i].GpuData != null)
                {
                    Array.Clear(_layers[i].GpuData, 0, _layers[i].GpuData.Length);
                }
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

        public void SetSelectionMaskOverlay(Texture2D maskTexture, bool show)
        {
            _selectionMaskTexture = maskTexture;
            _showSelectionMask = show;
            ApplyOverlayParametersToMeshes();
        }

        public void ApplyOverlayParametersToMeshes()
        {
            void ConfigureMeshOverlay(MeshInstance3D mesh)
            {
                if (mesh == null || !GodotObject.IsInstanceValid(mesh)) return;
                if (mesh.HasMeta("IsHiddenComposite") || !mesh.Visible)
                {
                    mesh.Layers &= ~(uint)(1 << 20);
                    return;
                }
                var mat = mesh.MaterialOverlay as ShaderMaterial;
                if (mat != null)
                {
                    mat.SetShaderParameter("layer_opacity", ActiveLayer?.Opacity ?? 1.0f);
                    if (_targetMesh != null)
                    {
                        mat.SetShaderParameter("is_paint_target", mesh == _targetMesh);
                    }

                    if (_showSelectionMask && _selectionMaskTexture != null)
                    {
                        mat.SetShaderParameter("selection_mask", _selectionMaskTexture);
                        mat.SetShaderParameter("show_selection_mask", true);
                    }
                    else
                    {
                        mat.SetShaderParameter("show_selection_mask", false);
                        mat.SetShaderParameter("selection_mask", (Texture2D)null);
                    }

                    Texture2D baseTex = null;
                    int sCount = mesh.Mesh != null ? mesh.Mesh.GetSurfaceCount() : 1;
                    for (int s = 0; s < sCount; s++)
                    {
                        var origMat = mesh.GetSurfaceOverrideMaterial(s)
                                   ?? (mesh.Mesh != null ? mesh.Mesh.SurfaceGetMaterial(s) : null)
                                   ?? mesh.MaterialOverride;
                        if (origMat != null)
                        {
                            baseTex = ExtractBaseTexture(origMat);
                            if (baseTex != null) break;
                        }
                    }
                    if (baseTex != null)
                    {
                        mat.SetShaderParameter("g_tColor", baseTex);
                    }

                    mesh.Layers |= (uint)(1 << 20);
                }
                else
                {
                    mesh.Layers &= ~(uint)(1 << 20);
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

        public void SetPaintTargetMesh(MeshInstance3D targetMesh)
        {
            _targetMesh = targetMesh;
            ApplyOverlayParametersToMeshes();
        }

        // --- Submesh Bucket Fill ---
        public void FillCurrentSubmesh(Color color, Vector2? hitUv = null, MagicWandTool wandTool = null)
        {
            FillSubmesh(_targetMesh, color, hitUv, wandTool);
        }

        public void FillSubmesh(MeshInstance3D mesh, Color color, Vector2? hitUv = null, MagicWandTool wandTool = null)
        {
            if (_targetMesh != null && mesh != _targetMesh)
            {
                GD.Print("[SkinLayerManager] Ignoring fill: mesh is not the currently selected target mesh.");
                return;
            }

            if (mesh == null || !GodotObject.IsInstanceValid(mesh) || _atlasManager == null || !GodotObject.IsInstanceValid(_atlasManager))
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
            var ridVal = _atlasManager.Get("atlas_texture_rid");
            if (ridVal.VariantType != Variant.Type.Rid) return;
            Rid rid = ridVal.AsRid();
            if (!rid.IsValid || rd == null) return;

            int atlasSize = (int)_atlasManager.Get("atlas_size");
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
            Half hA = (Half)1.0f;

            bool useMask = wandTool != null && wandTool.HasSelection && wandTool.UseSelectionMask;

            if (ActiveLayer != null)
            {
                int bufferLen = atlasSize * atlasSize * 8;
                if (ActiveLayer.GpuData == null || ActiveLayer.GpuData.Length != bufferLen)
                {
                    ActiveLayer.GpuData = new byte[bufferLen];
                }

                if (useMask)
                {
                    unsafe
                    {
                        fixed (byte* pDst = ActiveLayer.GpuData)
                        {
                            Half* hLayer = (Half*)pDst;
                            for (int y = startY; y < startY + height; y++)
                            {
                                int rowOffset = y * atlasSize * 4;
                                for (int x = startX; x < startX + width; x++)
                                {
                                    if (!wandTool.IsPixelSelected(x, y)) continue;
                                    int idx = rowOffset + x * 4;
                                    hLayer[idx] = hR;
                                    hLayer[idx + 1] = hG;
                                    hLayer[idx + 2] = hB;
                                    hLayer[idx + 3] = hA;
                                }
                            }
                        }
                    }
                }
                else
                {
                    unsafe
                    {
                        fixed (byte* pDst = ActiveLayer.GpuData)
                        {
                            Half* hLayer = (Half*)pDst;
                            for (int y = startY; y < startY + height; y++)
                            {
                                int rowOffset = y * atlasSize * 4;
                                for (int x = startX; x < startX + width; x++)
                                {
                                    int idx = rowOffset + x * 4;
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

            RecompositeGpuLayers();
            RecordUndoSnapshot();

            MeshHierarchy?.MarkSubmeshDirty(mesh);

            GD.Print($"[SkinLayerManager] Bucket filled submesh '{mesh.Name}' with {color.ToHtml()} (UseMask={useMask}, HitUV={hitUv})");
        }

        public bool StampDecalToAtlas(Vector2 hitUv, Texture2D decalTexture, float rotationDeg, float scale)
        {
            if (decalTexture == null || _targetMesh == null || !GodotObject.IsInstanceValid(_targetMesh) || _atlasManager == null || !GodotObject.IsInstanceValid(_atlasManager))
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
            var ridVal = _atlasManager.Get("atlas_texture_rid");
            if (ridVal.VariantType != Variant.Type.Rid) return false;
            Rid rid = ridVal.AsRid();
            if (!rid.IsValid || rd == null) return false;

            int atlasSize = (int)_atlasManager.Get("atlas_size");
            if (atlasSize <= 0) atlasSize = 2048;

            Image decalImg = decalTexture.GetImage();
            if (decalImg == null) return false;
            if (decalImg.IsCompressed()) decalImg.Decompress();

            int dW = decalImg.GetWidth();
            int dH = decalImg.GetHeight();
            if (dW <= 0 || dH <= 0) return false;

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

            float diag = Mathf.Sqrt(halfExtX * halfExtX + halfExtY * halfExtY);
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
                    fixed (byte* pDst = ActiveLayer.GpuData)
                    {
                        Half* hLayer = (Half*)pDst;
                        for (int y = minY; y <= maxY; y++)
                        {
                            int rowOffset = y * atlasSize * 4;
                            for (int x = minX; x <= maxX; x++)
                            {
                                float nx = (float)(x - centerPx.X) / halfExtX;
                                float ny = (float)(y - centerPx.Y) / halfExtY;

                                float rx = nx * cosR - ny * sinR;
                                float ry = nx * sinR + ny * cosR;

                                if (Mathf.Abs(rx) > 1.0f || Mathf.Abs(ry) > 1.0f) continue;

                                float du = (rx + 1.0f) * 0.5f;
                                float dv = (ry + 1.0f) * 0.5f;

                                int px = Mathf.Clamp((int)(du * dW), 0, dW - 1);
                                int py = Mathf.Clamp((int)(dv * dH), 0, dH - 1);

                                Color dCol = decalImg.GetPixel(px, py).SrgbToLinear();
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

            RecompositeGpuLayers();
            RecordUndoSnapshot();

            if (_targetMesh != null && MeshHierarchy != null)
            {
                MeshHierarchy.MarkSubmeshDirty(_targetMesh);
            }

            GD.Print($"[SkinLayerManager] Decal successfully stamped to atlas at ({centerPx.X:F0}, {centerPx.Y:F0})");
            return true;
        }

        public unsafe void CommitStrokeToActiveLayer(byte[] preStrokeData, byte[] postStrokeData, Color? strokeColor = null, bool isErase = false)
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
            if (_atlasManager != null && GodotObject.IsInstanceValid(_atlasManager))
            {
                int amSize = (int)_atlasManager.Get("atlas_size");
                if (amSize > 0) atlasWidth = amSize;
            }

            int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;

            fixed (byte* pPre = preStrokeData, pPost = postStrokeData, pLayer = layer.GpuData)
            {
                ulong* uPre = (ulong*)pPre;
                ulong* uPost = (ulong*)pPost;
                Half* hPre = (Half*)pPre;
                Half* hPost = (Half*)pPost;
                Half* hLayer = (Half*)pLayer;

                float op = Mathf.Max(layer.Opacity, 0.001f);

                for (int i = 0; i < pixelCount; i++)
                {
                    if (preStrokeData != null && preStrokeData.Length == len && uPost[i] == uPre[i]) continue;

                    int px = i % atlasWidth;
                    int py = i / atlasWidth;
                    if (px < minX) minX = px;
                    if (px > maxX) maxX = px;
                    if (py < minY) minY = py;
                    if (py > maxY) maxY = py;

                    int hOffset = i * 4;

                    float actualA = (float)hPost[hOffset + 3];
                    float unscaledA = Mathf.Clamp(actualA / op, 0.0f, 1.0f);

                    if (isErase)
                    {
                        float curA = (float)hLayer[hOffset + 3];
                        float deltaErase = 0.0f;
                        if (preStrokeData != null && preStrokeData.Length == len)
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
            }

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
        }

        public void RebuildBaseAtlasBuffer()
        {
            if (_atlasManager == null || !GodotObject.IsInstanceValid(_atlasManager)) return;

            int atlasSize = (int)_atlasManager.Get("atlas_size");
            if (atlasSize <= 0) atlasSize = CanvasSize.X > 0 ? CanvasSize.X : 2048;
            int bufferLen = atlasSize * atlasSize * 8;

            if (_baseAtlasBuffer == null || _baseAtlasBuffer.Length != bufferLen)
            {
                _baseAtlasBuffer = new byte[bufferLen];
            }

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

            var meshesToScan = new HashSet<MeshInstance3D>(_registeredSubmeshes);
            if (_targetMesh != null && GodotObject.IsInstanceValid(_targetMesh))
            {
                meshesToScan.Add(_targetMesh);
            }

            foreach (var mesh in meshesToScan)
            {
                if (mesh == null || !GodotObject.IsInstanceValid(mesh)) continue;
                if (mesh.MaterialOverlay is not ShaderMaterial sm) continue;

                var posVar = sm.GetShaderParameter("position_in_atlas");
                var sizeVar = sm.GetShaderParameter("size_in_atlas");
                if (posVar.VariantType != Variant.Type.Vector2 || sizeVar.VariantType != Variant.Type.Vector2) continue;

                Vector2 pos = posVar.AsVector2();
                Vector2 size = sizeVar.AsVector2();

                Texture2D baseTex = null;
                int sCount = mesh.Mesh != null ? mesh.Mesh.GetSurfaceCount() : 1;
                for (int s = 0; s < sCount; s++)
                {
                    var origMat = mesh.GetSurfaceOverrideMaterial(s)
                               ?? (mesh.Mesh != null ? mesh.Mesh.SurfaceGetMaterial(s) : null)
                               ?? mesh.MaterialOverride;
                    if (origMat != null)
                    {
                        baseTex = ExtractBaseTexture(origMat);
                        if (baseTex != null) break;
                    }
                }

                if (baseTex == null) continue;

                Image img = baseTex.GetImage();
                if (img == null) continue;
                if (img.IsCompressed())
                {
                    var err = img.Decompress();
                    if (err != Error.Ok) continue;
                }

                int rectX = Mathf.Clamp((int)(pos.X * atlasSize), 0, atlasSize - 1);
                int rectY = Mathf.Clamp((int)(pos.Y * atlasSize), 0, atlasSize - 1);
                int rectW = Mathf.Clamp((int)(size.X * atlasSize), 1, atlasSize - rectX);
                int rectH = Mathf.Clamp((int)(size.Y * atlasSize), 1, atlasSize - rectY);

                if (rectW <= 0 || rectH <= 0) continue;

                if (img.GetWidth() != rectW || img.GetHeight() != rectH)
                {
                    img.Resize(rectW, rectH, Image.Interpolation.Bilinear);
                }

                unsafe
                {
                    fixed (byte* pBase = _baseAtlasBuffer)
                    {
                        Half* hBase = (Half*)pBase;
                        for (int y = 0; y < rectH; y++)
                        {
                            int rowOffset = (rectY + y) * atlasSize * 4;
                            for (int x = 0; x < rectW; x++)
                            {
                                Color c = img.GetPixel(x, y).SrgbToLinear();
                                int idx = rowOffset + (rectX + x) * 4;
                                hBase[idx] = (Half)c.R;
                                hBase[idx + 1] = (Half)c.G;
                                hBase[idx + 2] = (Half)c.B;
                                hBase[idx + 3] = (Half)1.0f;
                            }
                        }
                    }
                }
            }

            var rd = RenderingServer.GetRenderingDevice();
            if (_atlasManager != null && GodotObject.IsInstanceValid(_atlasManager) && rd != null)
            {
                var baseRidVal = _atlasManager.Get("base_texture_rid");
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

        public void RecompositeGpuLayers()
        {
            if (_atlasManager == null || !GodotObject.IsInstanceValid(_atlasManager)) return;

            var rd = RenderingServer.GetRenderingDevice();
            var ridVal = _atlasManager.Get("atlas_texture_rid");
            if (ridVal.VariantType != Variant.Type.Rid) return;
            Rid rid = ridVal.AsRid();
            if (!rid.IsValid || rd == null) return;

            int atlasSize = (int)_atlasManager.Get("atlas_size");
            if (atlasSize <= 0) atlasSize = CanvasSize.X > 0 ? CanvasSize.X : 2048;
            int bufferLen = atlasSize * atlasSize * 8;

            if (_compositeBuffer == null || _compositeBuffer.Length != bufferLen)
            {
                _compositeBuffer = new byte[bufferLen];
            }
            else
            {
                Array.Clear(_compositeBuffer, 0, _compositeBuffer.Length);
            }

            if (_baseAtlasBuffer == null || _baseAtlasBuffer.Length != bufferLen)
            {
                RebuildBaseAtlasBuffer();
            }

            foreach (var layer in _layers)
            {
                if (!layer.IsVisible || layer.GpuData == null || layer.GpuData.Length != bufferLen)
                {
                    continue;
                }

                BlendLayerBuffer(_compositeBuffer, layer.GpuData, layer.Opacity, layer.BlendMode, _baseAtlasBuffer);
            }

            var err = rd.TextureUpdate(rid, 0, _compositeBuffer);
            if (err != Error.Ok)
            {
                GD.PushError($"[RecompositeGpuLayers] TextureUpdate failed: {err}");
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

                for (int i = 0; i < pixelCount; i++)
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
            }
        }

        public void SetLayerVisibility(int index, bool visible)
        {
            if (index < 0 || index >= _layers.Count) return;

            _layers[index].SetVisibility(visible);
            RecompositeGpuLayers();
            NotifyStackChanged();
        }

        public void SetLayerOpacity(int index, float opacity)
        {
            if (index < 0 || index >= _layers.Count) return;

            _layers[index].SetOpacity(opacity);
            RecompositeGpuLayers();
            NotifyStackChanged();
        }

        public void SetLayerBlendMode(int index, LayerBlendMode mode)
        {
            if (index < 0 || index >= _layers.Count) return;

            _layers[index].SetBlendMode(mode);
            ApplyOverlayParametersToMeshes();
            RecompositeGpuLayers();
            NotifyStackChanged();
        }

        public Image BakeCompositeImage(int surfaceIndex = -1)
        {
            if (_atlasManager != null && GodotObject.IsInstanceValid(_atlasManager))
            {
                var rd = RenderingServer.GetRenderingDevice();
                var ridVal = _atlasManager.Get("atlas_texture_rid");
                if (ridVal.VariantType == Variant.Type.Rid)
                {
                    Rid rid = ridVal.AsRid();
                    if (rid.IsValid && rd != null)
                    {
                        byte[] data = rd.TextureGetData(rid, 0);
                        int size = (int)_atlasManager.Get("atlas_size");
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

        public void ClearAllLayers()
        {
            foreach (var layer in _layers)
            {
                layer.CleanUp();
            }
            _layers.Clear();
            _activeLayerIndex = 0;
            _activeSurfaceIndex = 0;
            _targetMesh = null;
            _registeredSubmeshes.Clear();
        }

        public void CleanupHeroAtlas()
        {
            ClearAtlasManager();
            ClearAllLayers();
            _baseAtlasBuffer = null;
            _currentHero = null;
        }

        public override void _ExitTree()
        {
            ClearAtlasManager();
            ClearAllLayers();
            base._ExitTree();
        }

        private void NotifyLayerAdded(int index, string name) => EmitSignal(SignalName.LayerAdded, index, name);
        private void NotifyLayerRemoved(int index) => EmitSignal(SignalName.LayerRemoved, index);
        private void NotifyLayerSelected(int index) => EmitSignal(SignalName.LayerSelected, index);
        private void NotifyLayersReordered() => EmitSignal(SignalName.LayersReordered);
        private void NotifyStackChanged()
        {
            EmitSignal(SignalName.StackChanged);
        }
    }
}