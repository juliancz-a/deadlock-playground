using Godot;
using System;
using System.Collections.Generic;
using DeadlockPlayground.UI;

namespace DeadlockPlayground.Painter
{
    public enum BrushToolMode
    {
        Paint = 0,
        Erase = 1,
        Eyedropper = 2,
        BucketFill = 3,
        Decal = 4,
        Text = 5,
        SelectSubmesh = 6,
        MagicWand = 7,
        Selection = 8,
        Shape = 9
    }

    public enum BrushShapeType
    {
        SoftCircle = 0,
        HardCircle = 1,
        Splatter = 2,
        Grunge = 3,
        Square = 4
    }

    public partial class MeshPainter3D : Node
    {
        [Signal] public delegate void ColorSampledEventHandler(Color color);
        [Signal] public delegate void BrushColorChangedEventHandler(Color color);
        [Signal] public delegate void StrokeStartedEventHandler();
        [Signal] public delegate void StrokeFinishedEventHandler();

        [Export] public bool IsPaintingActive { get; set; } = true;

        // Shape Painting Subsystem
        private readonly ShapeTool _shapeTool = new();
        public ShapeTool ShapeTool => _shapeTool;

        private bool _isDraggingShape3D = false;
        private Vector2 _shapeStartScreenPos = Vector2.Zero;
        private Vector2 _shapeCurrentScreenPos = Vector2.Zero;
        private Vector2 _shapeStartUV = Vector2.Zero;
        private Vector2 _shapeCurrentUV = Vector2.Zero;

        // Front Faces Only / Backface Occlusion
        private bool _frontFacesOnly = true;
        public bool FrontFacesOnly
        {
            get => _frontFacesOnly;
            set
            {
                _frontFacesOnly = value;
                ApplyFrontFacesOnlyToMaterials();
            }
        }

        public void ApplyFrontFacesOnlyToMaterials()
        {
            if (MeshHierarchy == null || MeshHierarchy.Submeshes == null) return;
            foreach (var sub in MeshHierarchy.Submeshes)
            {
                if (sub.Mesh != null && sub.Mesh.MaterialOverlay is ShaderMaterial sm)
                {
                    sm.SetShaderParameter("front_faces_only", _frontFacesOnly);
                }
            }
        }

        // Brush parameters
        private Color _brushColor = new Color(0.85f, 0.15f, 0.2f, 1.0f);
        public Color BrushColor
        {
            get => _brushColor;
            set
            {
                if (_brushColor != value)
                {
                    _brushColor = value;
                    EmitSignal(SignalName.BrushColorChanged, value);
                }
            }
        }
        public float BrushSize { get; set; } = 32.0f;

        private float _brushHardness = 0.5f;
        public float BrushHardness
        {
            get => _brushHardness;
            set
            {
                if (Math.Abs(_brushHardness - value) > 0.005f)
                {
                    _brushHardness = value;
                    GenerateProceduralBrushTextures();
                    _lastShapeType = (BrushShapeType)(-1);
                    SyncCameraBrushProperties();
                }
            }
        }

        public float BrushFlow { get; set; } = 1.0f;
        public BrushToolMode ToolMode { get; set; } = BrushToolMode.Paint;
        public BrushShapeType BrushShape { get; set; } = BrushShapeType.SoftCircle;
        public BrushShapeType EraserShape { get; set; } = BrushShapeType.HardCircle;
        public BrushShapeType ShapeType
        {
            get => (ToolMode == BrushToolMode.Erase) ? EraserShape : BrushShape;
            set
            {
                if (ToolMode == BrushToolMode.Erase) EraserShape = value;
                else BrushShape = value;
            }
        }
        public int BlendMode { get; set; } = 0;

        // Tolerance parameters
        private float _magicWandTolerance = 0.15f;
        public float MagicWandTolerance
        {
            get => _magicWandTolerance;
            set
            {
                _magicWandTolerance = Mathf.Clamp(value, 0.005f, 1.0f);
                if (_magicWandTool != null) _magicWandTool.Tolerance = _magicWandTolerance;
            }
        }

        private float _bucketFillTolerance = 0.50f;
        public float BucketFillTolerance
        {
            get => _bucketFillTolerance;
            set => _bucketFillTolerance = Mathf.Clamp(value, 0.01f, 1.0f);
        }

        // Submesh Selection Outline Gizmo
        private MeshInstance3D _selectionOutlineMesh;
        private ShaderMaterial _outlineMaskMat;
        private ShaderMaterial _outlineEdgeMat;

        private Camera3D _camera;
        private SubViewport _worldViewport;
        private SubViewportContainer _worldViewportContainer;
        private SkinLayerManager _layerManager;
        public SkinLayerManager LayerManager => _layerManager;
        private FloatingBrushPaletteUI _brushPalette;
        private QuickColorPaletteUI _quickColorPalette;
        public QuickColorPaletteUI QuickColorPalette
        {
            get => _quickColorPalette;
            set => _quickColorPalette = value;
        }
        private Control _cameraControlPad;
        private bool _clickOriginatedOnUI = false;
        private readonly MeshRaycaster _raycaster = new();
        private MeshInstance3D _currentMesh;
        public MeshInstance3D CurrentMesh => _currentMesh;
        private int _currentSurfaceIndex = 0;

        // GPU Texture Painter interop
        private Node3D _cameraBrush;
        public Node3D CameraBrush => _cameraBrush;

        private bool _isMouseDown = false;
        private bool _strokeInProgress = false;
        private int _debugFrameCount = 0;

        // 3D Oriented Ring Cursor Gizmo
        private MeshInstance3D _cursorGizmo;
        private StandardMaterial3D _cursorMaterial;
        private Sprite3D _eyedropperSprite;
        private TorusMesh _cursorTorusMesh;
        private ArrayMesh _cursorSquareMesh;
        private ArrayMesh _cursorSplatterMesh;
        private ArrayMesh _cursorGrungeMesh;
        private MeshInstance3D _decalPreviewQuad;
        private StandardMaterial3D _decalMaterial;
        private Decal _previewDecalNode;
        private DecalStamper _decalStamper;
        public DecalStamper DecalStamper
        {
            get => _decalStamper;
            set => _decalStamper = value;
        }

        private MagicWandTool _magicWandTool = new();
        public MagicWandTool MagicWandTool => _magicWandTool;
        public SelectionMask SelectionMask => _magicWandTool.SelectionMask;

        // 3D Viewport Selection Toolkit state
        private bool _isSelecting3D = false;
        private bool _isBuildingPoly3D = false;
        private Vector2 _selectionStartScreenPos;
        private Vector2 _selectionCurrentScreenPos;
        private readonly List<Vector2> _lassoPoints3D = new();
        private readonly List<Vector2> _polyPoints3D = new();
        private Vector2 _polyCurrentScreenPos;
        private Control _selectionOverlay3D;

        private SubmeshNodeInfo _lastHitSubmesh;
        public SubmeshNodeInfo LastHitSubmesh => _lastHitSubmesh;

        // 3D Shape Projection State
        private Vector3 _shapeStartWorldPos;
        private Vector3 _shapeStartWorldNormal;
        private Vector3 _shapeStartWorldTangent;
        private Vector3 _shapeStartWorldBitangent;
        private float _shapeStartUnitsPerU = 0.5f;
        private float _shapeStartUnitsPerV = 0.5f;
        private ulong _lastShapePreviewMs = 0;

        private TextProjector _textProjector;
        public TextProjector TextProjector
        {
            get => _textProjector;
            set
            {
                if (_textProjector != null && GodotObject.IsInstanceValid(_textProjector))
                {
                    _textProjector.TextureChanged -= OnTextProjectorTextureChanged;
                }
                _textProjector = value;
                if (_textProjector != null && GodotObject.IsInstanceValid(_textProjector))
                {
                    _textProjector.TextureChanged += OnTextProjectorTextureChanged;
                }
            }
        }

        private void OnTextProjectorTextureChanged(Texture2D newTexture)
        {
            if (ToolMode == BrushToolMode.Text && _previewDecalNode != null && GodotObject.IsInstanceValid(_previewDecalNode))
            {
                _previewDecalNode.SetTexture(Decal.DecalTexture.Albedo, newTexture);
            }
        }

        private bool _isMirroringEnabled = false;
        public bool IsMirroringEnabled
        {
            get => _isMirroringEnabled;
            set
            {
                _isMirroringEnabled = value;
                if (_isMirroringEnabled)
                {
                    EnsureMirrorCameraBrush();
                }
                else if (_mirrorCameraBrush != null && GodotObject.IsInstanceValid(_mirrorCameraBrush))
                {
                    _mirrorCameraBrush.Set("drawing", false);
                }
            }
        }
        private Node3D _mirrorCameraBrush;

        public HeroMeshHierarchy MeshHierarchy { get; set; }

        public bool IsMouseOverUI()
        {
            if (StudioUIManager.Instance != null && StudioUIManager.Instance.IsAnyModalOpen())
            {
                return true;
            }

            if (PaintTabUI.IsRenameDialogOpen)
            {
                return true;
            }

            var tree = GetTree();
            if (tree != null)
            {
                var root = tree.Root;
                if (root != null)
                {
                    var windows = root.FindChildren("*", "Window", recursive: true, owned: false);
                    if (windows != null)
                    {
                        for (int i = 0; i < windows.Count; i++)
                        {
                            if (windows[i] is Window win && win != root && win.Visible)
                            {
                                return true;
                            }
                        }
                    }
                }

                var paintTab = GetParent() as PaintTabUI ?? root?.FindChild("PaintTab", true, false) as PaintTabUI;
                if (paintTab != null && paintTab.IsAnyDialogOpen())
                {
                    return true;
                }

                var exportPanel = root?.FindChild("PaintModExportPanel", true, false) as PaintModExportPanelUI;
                if (exportPanel != null && GodotObject.IsInstanceValid(exportPanel))
                {
                    if (exportPanel.IsAnyDialogOpen()) return true;
                    if (exportPanel.Visible && exportPanel.GetGlobalRect().HasPoint(GetViewport().GetMousePosition())) return true;
                }

                var modDialog = root?.FindChild("ExportModDialog", true, false) as CanvasLayer;
                if (modDialog != null && GodotObject.IsInstanceValid(modDialog) && modDialog.Visible) return true;

                var progDialog = root?.FindChild("ExportProgressDialog", true, false) as CanvasLayer;
                if (progDialog != null && GodotObject.IsInstanceValid(progDialog) && progDialog.Visible) return true;
            }

            var hovered = GetTree()?.Root?.GuiGetHoveredControl();
            if (hovered != null)
            {
                bool isMainViewport = (hovered.Name == "SubViewportContainer" && hovered.GetParent()?.Name == "ViewportArea");
                if (!isMainViewport)
                {
                    return true;
                }
            }

            Vector2 globalMouse = GetViewport().GetMousePosition();

            if (_brushPalette != null && GodotObject.IsInstanceValid(_brushPalette) && _brushPalette.Visible)
            {
                if (_brushPalette.GetGlobalRect().HasPoint(globalMouse))
                {
                    return true;
                }
            }

            if (_quickColorPalette == null || !GodotObject.IsInstanceValid(_quickColorPalette))
            {
                _quickColorPalette = GetTree()?.Root?.FindChild("QuickColorPalette", true, false) as QuickColorPaletteUI;
            }

            if (_quickColorPalette != null && GodotObject.IsInstanceValid(_quickColorPalette) && _quickColorPalette.Visible)
            {
                if (_quickColorPalette.IsMouseOverPalette(globalMouse))
                {
                    return true;
                }
            }

            if (_cameraControlPad == null || !GodotObject.IsInstanceValid(_cameraControlPad))
            {
                _cameraControlPad = GetTree()?.Root?.FindChild("CameraControlPad", true, false) as Control;
            }

            if (_cameraControlPad != null && GodotObject.IsInstanceValid(_cameraControlPad) && _cameraControlPad.Visible)
            {
                if (_cameraControlPad.GetGlobalRect().HasPoint(globalMouse))
                {
                    return true;
                }
            }

            return false;
        }

        public bool RaycastAllSubmeshes(Vector3 rayOrigin, Vector3 rayDir, out SubmeshNodeInfo hitSubmesh, out RaycastHitResult hitResult, bool? cullBackfaces = null)
        {
            hitSubmesh = null;
            hitResult = new RaycastHitResult { Hit = false, Distance = float.MaxValue };

            if (MeshHierarchy == null || MeshHierarchy.Submeshes == null) return false;

            float closestDist = float.MaxValue;
            bool doCull = cullBackfaces ?? FrontFacesOnly;

            foreach (var sub in MeshHierarchy.Submeshes)
            {
                if (sub.Mesh == null || !sub.Mesh.Visible || !GodotObject.IsInstanceValid(sub.Mesh)) continue;

                // Lazily build and cache the raycaster for each mesh across all surfaces (-1)
                if (sub.Raycaster == null)
                {
                    sub.Raycaster = new MeshRaycaster();
                    sub.Raycaster.BuildFromMesh(sub.Mesh, -1);
                }

                if (!sub.Raycaster.IsInitialized) continue;

                // IntersectRay performs an early-exit AABB bound check first;
                // submeshes not under the ray return instantly in ~0.001ms without iterating triangles.
                var hit = sub.Raycaster.IntersectRay(sub.Mesh, rayOrigin, rayDir, cullBackfaces: doCull);
                if (hit.Hit && hit.Distance < closestDist)
                {
                    closestDist = hit.Distance;
                    hitResult = hit;
                    hitSubmesh = MeshHierarchy?.FindSubmesh(sub.Mesh, hit.HitSurfaceIndex) ?? sub;
                }
            }

            return hitSubmesh != null;
        }

        private int _depthPeelIndex = 0;
        private Vector2 _lastPeelMousePos = new Vector2(-9999, -9999);

        public List<(SubmeshNodeInfo Submesh, RaycastHitResult Hit)> RaycastAllSubmeshesSorted(Vector3 rayOrigin, Vector3 rayDir)
        {
            var hits = new List<(SubmeshNodeInfo, RaycastHitResult)>();
            if (MeshHierarchy == null || MeshHierarchy.Submeshes == null) return hits;

            foreach (var sub in MeshHierarchy.Submeshes)
            {
                if (sub.Mesh == null || !sub.Mesh.Visible || !GodotObject.IsInstanceValid(sub.Mesh)) continue;

                if (sub.Raycaster == null)
                {
                    sub.Raycaster = new MeshRaycaster();
                    sub.Raycaster.BuildFromMesh(sub.Mesh, -1);
                }

                if (!sub.Raycaster.IsInitialized) continue;

                var hit = sub.Raycaster.IntersectRay(sub.Mesh, rayOrigin, rayDir, cullBackfaces: FrontFacesOnly);
                if (hit.Hit)
                {
                    var resolvedSub = MeshHierarchy?.FindSubmesh(sub.Mesh, hit.HitSurfaceIndex) ?? sub;
                    hits.Add((resolvedSub, hit));
                }
            }

            hits.Sort((a, b) => a.Item2.Distance.CompareTo(b.Item2.Distance));
            return hits;
        }

        private byte[] _preStrokeAtlasData;
        private bool _isActionClickDown = false;

        // Cached procedural brush textures
        private readonly Dictionary<BrushShapeType, Texture2D> _brushTextures = new();

        private BrushShapeType _lastShapeType = (BrushShapeType)(-1);
        private float _lastBrushSize = -1f;
        private Color _lastBrushColor = Colors.Transparent;
        private float _lastBrushFlow = -1f;
        private BrushToolMode _lastToolMode = (BrushToolMode)(-1);
        private int _lastBlendMode = -1;
        private RaycastHitResult _lastHit;

        public override void _Ready()
        {
            GenerateProceduralBrushTextures();
            EnsureCursorGizmo();
            EnsureCameraBrush();
            GizmoDisplaySettings.OnSettingsChanged += ApplyOutlineSettings;
            ApplyOutlineSettings();

            if (_magicWandTool != null)
            {
                _magicWandTool.MaskUpdated += (hasMask) => SyncSelectionMaskState();
            }
        }

        public void EnsureCameraBrush()
        {
            if (_worldViewport == null)
            {
                _worldViewport = _currentMesh?.GetViewport() as SubViewport
                              ?? GetNodeOrNull<SubViewport>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport")
                              ?? GetTree()?.Root?.FindChild("WorldViewport", true, false) as SubViewport;
            }

            if (_cameraBrush != null && GodotObject.IsInstanceValid(_cameraBrush))
            {
                if (_worldViewport != null && _cameraBrush.GetParent() != _worldViewport)
                {
                    _cameraBrush.GetParent()?.RemoveChild(_cameraBrush);
                    _worldViewport.AddChild(_cameraBrush);
                    AttachCameraBrushWorld();
                }
                if (IsMirroringEnabled)
                {
                    EnsureMirrorCameraBrush();
                }
                return;
            }

            var brushScript = GD.Load<GDScript>("res://addons/gpu_texture_painter/brush/camera_brush.gd");
            if (brushScript == null)
            {
                GD.PrintErr("[MeshPainter3D] Failed to load camera_brush.gd");
                return;
            }

            _cameraBrush = (Node3D)brushScript.New();
            _cameraBrush.Name = "ActiveCameraBrush";
            _cameraBrush.Set("projection", 1); // 1 = PROJECTION_ORTHOGONAL (constant radius pencil)
            _cameraBrush.Set("resolution", new Vector2I(256, 256));
            _cameraBrush.Set("max_distance", 0.35f);
            _cameraBrush.Set("draw_speed", 35.0f);
            _cameraBrush.Set("drawing", false);

            var targetParent = _worldViewport as Node ?? _camera as Node ?? this;
            targetParent.AddChild(_cameraBrush);
            AttachCameraBrushWorld();

            if (IsMirroringEnabled)
            {
                EnsureMirrorCameraBrush();
            }

            SyncCameraBrushProperties(force: true);
            GD.Print("[MeshPainter3D] Initialized and added CameraBrush to scene!");
        }

        private void EnsureMirrorCameraBrush()
        {
            if (_mirrorCameraBrush != null && GodotObject.IsInstanceValid(_mirrorCameraBrush))
            {
                AttachMirrorCameraBrushWorld();
                SyncCameraBrushProperties(force: true);
                return;
            }

            var brushScript = GD.Load<GDScript>("res://addons/gpu_texture_painter/brush/camera_brush.gd");
            if (brushScript == null)
            {
                GD.PrintErr("[MeshPainter3D] Failed to load camera_brush.gd for mirror brush");
                return;
            }

            var targetParent = _worldViewport as Node ?? _camera as Node ?? this;
            _mirrorCameraBrush = (Node3D)brushScript.New();
            _mirrorCameraBrush.Name = "MirrorCameraBrush";
            _mirrorCameraBrush.Set("projection", 1);
            _mirrorCameraBrush.Set("resolution", new Vector2I(256, 256));
            _mirrorCameraBrush.Set("max_distance", 0.35f);
            _mirrorCameraBrush.Set("draw_speed", 35.0f);
            _mirrorCameraBrush.Set("drawing", false);
            targetParent.AddChild(_mirrorCameraBrush);
            AttachMirrorCameraBrushWorld();
            SyncCameraBrushProperties(force: true);
            GD.Print("[MeshPainter3D] Initialized MirrorCameraBrush!");
        }

        private void AttachCameraBrushWorld()
        {
            if (_cameraBrush == null || !GodotObject.IsInstanceValid(_cameraBrush) || _worldViewport == null) return;

            var cbViewport = _cameraBrush.GetNodeOrNull<SubViewport>("CameraBrushViewport")
                          ?? _cameraBrush.FindChild("CameraBrushViewport", true, false) as SubViewport;
            if (cbViewport != null)
            {
                var w3d = _worldViewport.FindWorld3D();
                if (w3d != null)
                {
                    cbViewport.World3D = w3d;
                }
            }
        }

        private void AttachMirrorCameraBrushWorld()
        {
            if (_mirrorCameraBrush == null || !GodotObject.IsInstanceValid(_mirrorCameraBrush) || _worldViewport == null) return;

            var cbViewport = _mirrorCameraBrush.GetNodeOrNull<SubViewport>("CameraBrushViewport")
                          ?? _mirrorCameraBrush.FindChild("CameraBrushViewport", true, false) as SubViewport;
            if (cbViewport != null)
            {
                var w3d = _worldViewport.FindWorld3D();
                if (w3d != null)
                {
                    cbViewport.World3D = w3d;
                }
            }

            if (_mirrorCameraBrush.HasMethod("get_atlas_textures"))
            {
                _mirrorCameraBrush.Call("get_atlas_textures");
            }
        }

        private (Vector3 MirroredPos, Vector3 MirroredNormal) ComputeMirroredPointAndNormal(Vector3 worldPos, Vector3 worldNormal)
        {
            Node3D heroRoot = MeshHierarchy?.HeroRoot;
            if (heroRoot != null && GodotObject.IsInstanceValid(heroRoot))
            {
                Vector3 localPos = heroRoot.ToLocal(worldPos);
                localPos.X = -localPos.X;
                Vector3 mirWorldPos = heroRoot.ToGlobal(localPos);

                Vector3 localNorm = heroRoot.GlobalBasis.Inverse() * worldNormal;
                localNorm.X = -localNorm.X;
                Vector3 mirWorldNorm = (heroRoot.GlobalBasis * localNorm).Normalized();

                return (mirWorldPos, mirWorldNorm);
            }
            return (new Vector3(-worldPos.X, worldPos.Y, worldPos.Z), new Vector3(-worldNormal.X, worldNormal.Y, worldNormal.Z));
        }

        private (Vector3 MirroredPos, Vector3 MirroredDir) ComputeMirroredPointAndDir(Vector3 worldPos, Vector3 worldDir)
        {
            Node3D heroRoot = MeshHierarchy?.HeroRoot;
            if (heroRoot != null && GodotObject.IsInstanceValid(heroRoot))
            {
                Vector3 localPos = heroRoot.ToLocal(worldPos);
                localPos.X = -localPos.X;
                Vector3 mirWorldPos = heroRoot.ToGlobal(localPos);

                Vector3 localDir = heroRoot.GlobalBasis.Inverse() * worldDir;
                localDir.X = -localDir.X;
                Vector3 mirWorldDir = (heroRoot.GlobalBasis * localDir).Normalized();

                return (mirWorldPos, mirWorldDir);
            }
            return (new Vector3(-worldPos.X, worldPos.Y, worldPos.Z), new Vector3(-worldDir.X, worldDir.Y, worldDir.Z).Normalized());
        }

        public void SyncCameraBrushProperties(bool force = false)
        {
            if (_cameraBrush == null || !GodotObject.IsInstanceValid(_cameraBrush)) return;

            int atlasRes = _layerManager?.CanvasSize.X ?? 2048;

            // Map pixel brush size to orthogonal camera world size (meters)
            if (force || Math.Abs(_lastBrushSize - BrushSize) > 0.01f)
            {
                float worldPixelSize = 1.6f / atlasRes;
                float worldSize = Mathf.Max(worldPixelSize * 0.5f, BrushSize * worldPixelSize);
                _cameraBrush.Set("size", worldSize);
                if (_mirrorCameraBrush != null && GodotObject.IsInstanceValid(_mirrorCameraBrush))
                {
                    _mirrorCameraBrush.Set("size", worldSize);
                }
                _lastBrushSize = BrushSize;
            }

            bool isErase = ToolMode == BrushToolMode.Erase;
            _cameraBrush.Set("is_erase", isErase);
            if (_mirrorCameraBrush != null && GodotObject.IsInstanceValid(_mirrorCameraBrush))
            {
                _mirrorCameraBrush.Set("is_erase", isErase);
            }

            float layerOpacity = _layerManager?.ActiveLayer?.Opacity ?? 1.0f;
            int activeBlendMode = _layerManager?.ActiveLayer != null 
                ? (int)_layerManager.ActiveLayer.BlendMode 
                : BlendMode;

            Color paintColor = isErase 
                ? new Color(1, 1, 1, BrushFlow) 
                : new Color(BrushColor.R, BrushColor.G, BrushColor.B, Mathf.Clamp(BrushColor.A * layerOpacity, 0.01f, 1.0f));

            if (force || _lastBrushColor != paintColor || _lastToolMode != ToolMode)
            {
                _cameraBrush.Set("color", paintColor);
                if (_mirrorCameraBrush != null && GodotObject.IsInstanceValid(_mirrorCameraBrush))
                {
                    _mirrorCameraBrush.Set("color", paintColor);
                }
                _lastBrushColor = paintColor;
                _lastToolMode = ToolMode;
            }

            if (force || Math.Abs(_lastBrushFlow - BrushFlow) > 0.01f)
            {
                float speed = Mathf.Max(5.0f, BrushFlow * 35.0f);
                _cameraBrush.Set("draw_speed", speed);
                if (_mirrorCameraBrush != null && GodotObject.IsInstanceValid(_mirrorCameraBrush))
                {
                    _mirrorCameraBrush.Set("draw_speed", speed);
                }
                _lastBrushFlow = BrushFlow;
            }

            BrushShapeType activeShape = (ToolMode == BrushToolMode.Erase) ? EraserShape : BrushShape;
            if (force || _lastShapeType != activeShape)
            {
                Texture2D brushTex = GetBrushTexture(activeShape);
                if (brushTex != null)
                {
                    var img = brushTex.GetImage();
                    if (img != null)
                    {
                        _cameraBrush.Set("brush_shape", img);
                        if (_mirrorCameraBrush != null && GodotObject.IsInstanceValid(_mirrorCameraBrush))
                        {
                            _mirrorCameraBrush.Set("brush_shape", img);
                        }
                    }
                }
                _lastShapeType = activeShape;
            }

            if (force || _lastBlendMode != activeBlendMode)
            {
                _cameraBrush.Set("blend_mode", activeBlendMode);
                if (_mirrorCameraBrush != null && GodotObject.IsInstanceValid(_mirrorCameraBrush))
                {
                    _mirrorCameraBrush.Set("blend_mode", activeBlendMode);
                }
                _lastBlendMode = activeBlendMode;
                BlendMode = activeBlendMode;
            }

            int minBleed = 0;
            int maxBleed = 0;
            Vector2I brushRes = new Vector2I(256, 256);
            Vector2 brushCenter = new Vector2(128.0f, 128.0f);
            float brushRadius = 128.0f;

            _cameraBrush.Set("min_bleed", minBleed);
            _cameraBrush.Set("max_bleed", maxBleed);
            if (!((Vector2I)_cameraBrush.Get("resolution")).Equals(brushRes))
            {
                _cameraBrush.Set("resolution", brushRes);
            }
            _cameraBrush.Set("brush_center", brushCenter);
            _cameraBrush.Set("brush_radius", brushRadius);

            bool useMask = _magicWandTool != null && _magicWandTool.HasSelection;
            _cameraBrush.Set("use_selection_mask", useMask);
            Rid maskRid = (useMask && _magicWandTool.SelectionMaskRid.IsValid) ? _magicWandTool.SelectionMaskRid : new Rid();
            var rd = RenderingServer.GetRenderingDevice();
            if (maskRid.IsValid && rd != null && !rd.TextureIsValid(maskRid))
            {
                maskRid = new Rid();
            }
            _cameraBrush.Set("selection_mask_rid", maskRid);

            if (_mirrorCameraBrush != null && GodotObject.IsInstanceValid(_mirrorCameraBrush))
            {
                _mirrorCameraBrush.Set("min_bleed", minBleed);
                _mirrorCameraBrush.Set("max_bleed", maxBleed);
                if (!((Vector2I)_mirrorCameraBrush.Get("resolution")).Equals(brushRes))
                {
                    _mirrorCameraBrush.Set("resolution", brushRes);
                }
                _mirrorCameraBrush.Set("brush_center", brushCenter);
                _mirrorCameraBrush.Set("brush_radius", brushRadius);
                _mirrorCameraBrush.Set("use_selection_mask", useMask);
                _mirrorCameraBrush.Set("selection_mask_rid", maskRid);
            }
        }

        public override void _Process(double delta)
        {
            if (!IsPaintingActive)
            {
                if (_cursorGizmo != null && _cursorGizmo.Visible) _cursorGizmo.Visible = false;
                if (_selectionOutlineMesh != null && _selectionOutlineMesh.Visible) _selectionOutlineMesh.Visible = false;
                if (_previewDecalNode != null && _previewDecalNode.Visible) _previewDecalNode.Visible = false;
                return;
            }

            EnsureCameraBrush();

            bool isLeftDown = Input.IsMouseButtonPressed(MouseButton.Left);
            if (!isLeftDown)
            {
                _clickOriginatedOnUI = false;
            }
            else if (IsMouseOverUI() && !_isMouseDown)
            {
                _clickOriginatedOnUI = true;
            }

            if (IsMouseOverUI() || _clickOriginatedOnUI)
            {
                if (_cursorGizmo != null) _cursorGizmo.Visible = false;
                if (_previewDecalNode != null) _previewDecalNode.Visible = false;
                if (_cameraBrush != null && GodotObject.IsInstanceValid(_cameraBrush))
                {
                    _cameraBrush.Set("drawing", false);
                }
                if (_mirrorCameraBrush != null && GodotObject.IsInstanceValid(_mirrorCameraBrush))
                {
                    _mirrorCameraBrush.Set("drawing", false);
                }
                if (_isMouseDown) FinishStroke();
                return;
            }

            if (ToolMode == BrushToolMode.Selection || ToolMode == BrushToolMode.Shape)
            {
                if (_cursorGizmo != null) _cursorGizmo.Visible = false;
                if (_previewDecalNode != null) _previewDecalNode.Visible = false;
                if (_cameraBrush != null && GodotObject.IsInstanceValid(_cameraBrush))
                {
                    _cameraBrush.Set("drawing", false);
                }
                if (_mirrorCameraBrush != null && GodotObject.IsInstanceValid(_mirrorCameraBrush))
                {
                    _mirrorCameraBrush.Set("drawing", false);
                }
            }

            UpdateBrushCursor();
            UpdateSelectionOutline();

            // Auto-select target submesh on click if clicking a different authentic hero submesh BEFORE evaluating painting permissions
            if (isLeftDown && !_isMouseDown && _lastHitSubmesh != null && _lastHitSubmesh.Mesh != _currentMesh && !IsMouseOverUI())
            {
                MeshHierarchy?.SelectTarget(_lastHitSubmesh, _lastHitSubmesh.SurfaceIndex);
            }

            var camera = _worldViewport?.GetCamera3D() ?? _camera;

            EnsureSelectionOverlay3D();

            bool canPaintLayer = _layerManager?.ActiveLayer != null && !_layerManager.ActiveLayer.IsLocked && _layerManager.ActiveLayer.IsVisible;
            bool isPainting = IsPaintingActive && canPaintLayer && isLeftDown && _lastHit.Hit 
                && ToolMode != BrushToolMode.Eyedropper 
                && ToolMode != BrushToolMode.BucketFill 
                && ToolMode != BrushToolMode.Decal 
                && ToolMode != BrushToolMode.Text 
                && ToolMode != BrushToolMode.SelectSubmesh
                && ToolMode != BrushToolMode.MagicWand
                && ToolMode != BrushToolMode.Selection
                && ToolMode != BrushToolMode.Shape;

            if (isLeftDown)
            {
                _brushPalette?.CloseFlyoutIfUnpinned();
            }

            // Click actions for SelectSubmesh, BucketFill, Decal, and Text
            if (isLeftDown && !_isActionClickDown && !IsMouseOverUI())
            {
                _isActionClickDown = true;
                if (ToolMode == BrushToolMode.SelectSubmesh)
                {
                    if (camera != null)
                    {
                        Vector2 mousePos = GetViewportMousePosition();
                        Vector3 origin = camera.ProjectRayOrigin(mousePos);
                        Vector3 dir = camera.ProjectRayNormal(mousePos).Normalized();
                        if (RaycastAllSubmeshes(origin, dir, out var clickedSub, out _))
                        {
                            MeshHierarchy?.SelectTarget(clickedSub, clickedSub.SurfaceIndex);
                        }
                    }
                }
                else if (ToolMode == BrushToolMode.BucketFill)
                {
                    if (camera != null)
                    {
                        Vector2 mousePos = GetViewportMousePosition();
                        Vector3 origin = camera.ProjectRayOrigin(mousePos);
                        Vector3 dir = camera.ProjectRayNormal(mousePos).Normalized();
                        var hit = (_currentMesh != null) ? _raycaster.IntersectRay(_currentMesh, origin, dir, cullBackfaces: FrontFacesOnly) : default;
                        if (hit.Hit)
                        {
                            _layerManager?.FillSubmesh(_currentMesh, BrushColor, hit.HitUV, _magicWandTool, BucketFillTolerance);
                        }
                        else if (RaycastAllSubmeshes(origin, dir, out var otherSub, out var otherHit))
                        {
                            MeshHierarchy?.SelectTarget(otherSub, otherSub.SurfaceIndex);
                            _layerManager?.FillSubmesh(otherSub.Mesh, BrushColor, otherHit.HitUV, _magicWandTool, BucketFillTolerance);
                        }
                    }
                }
                else if (ToolMode == BrushToolMode.MagicWand)
                {
                    if (camera != null)
                    {
                        Vector2 mousePos = GetViewportMousePosition();
                        Vector3 origin = camera.ProjectRayOrigin(mousePos);
                        Vector3 dir = camera.ProjectRayNormal(mousePos).Normalized();

                        MagicWandCombineMode combineMode = MagicWandCombineMode.Replace;
                        if (Input.IsKeyPressed(Key.Shift)) combineMode = MagicWandCombineMode.Add;
                        else if (Input.IsKeyPressed(Key.Alt)) combineMode = MagicWandCombineMode.Subtract;

                        var hit = (_currentMesh != null) ? _raycaster.IntersectRay(_currentMesh, origin, dir, cullBackfaces: FrontFacesOnly) : default;
                        if (hit.Hit)
                        {
                            ExecuteMagicWandSelection(hit, combineMode);
                        }
                        else if (RaycastAllSubmeshes(origin, dir, out var otherSub, out var otherHit))
                        {
                            MeshHierarchy?.SelectTarget(otherSub, otherSub.SurfaceIndex);
                            ExecuteMagicWandSelection(otherHit, combineMode);
                        }
                    }
                }
                else if (ToolMode == BrushToolMode.Eyedropper)
                {
                    if (camera != null)
                    {
                        Vector2 mousePos = GetViewportMousePosition();
                        Vector3 origin = camera.ProjectRayOrigin(mousePos);
                        Vector3 dir = camera.ProjectRayNormal(mousePos).Normalized();
                        var hit = (_currentMesh != null) ? _raycaster.IntersectRay(_currentMesh, origin, dir, cullBackfaces: FrontFacesOnly) : default;
                        if (hit.Hit)
                        {
                            SampleColorAtUV(hit.HitUV, hit.HitSurfaceIndex);
                        }
                        else if (RaycastAllSubmeshes(origin, dir, out var otherSub, out var otherHit))
                        {
                            MeshHierarchy?.SelectTarget(otherSub, otherSub.SurfaceIndex);
                            SampleColorAtUV(otherHit.HitUV, otherHit.HitSurfaceIndex);
                        }
                    }
                }
                else if (ToolMode == BrushToolMode.Decal && _lastHit.Hit)
                {
                    if (_decalStamper != null && _decalStamper.DecalTexture != null)
                    {
                        Basis decalBasis = _previewDecalNode != null ? _previewDecalNode.Transform.Basis : Basis.Identity;
                        Vector3 decalRight = decalBasis.Column0.Normalized();
                        Vector3 decalDown = decalBasis.Column2.Normalized();
                        float unitsU = (_lastHit.WorldUnitsPerU > 1e-4f && _lastHit.WorldUnitsPerU < 20.0f) ? _lastHit.WorldUnitsPerU : 0.5f;
                        float unitsV = (_lastHit.WorldUnitsPerV > 1e-4f && _lastHit.WorldUnitsPerV < 20.0f) ? _lastHit.WorldUnitsPerV : 0.5f;

                        _decalStamper.PlaceAt(_lastHit.HitPositionWorld, _lastHit.HitNormal, _lastHit.HitUV, decalRight, decalDown, _lastHit.WorldTangent, _lastHit.WorldBitangent, unitsU, unitsV);
                        _decalStamper.BakeToActiveLayer();

                        if (IsMirroringEnabled)
                        {
                            var (mirrorWorldPos, mirrorNormal) = ComputeMirroredPointAndNormal(_lastHit.HitPositionWorld, _lastHit.HitNormal);
                            RaycastHitResult mirrorHit = default;
                            if (_currentMesh != null && _raycaster != null && _raycaster.IsInitialized)
                            {
                                mirrorHit = _raycaster.IntersectRay(_currentMesh, mirrorWorldPos + mirrorNormal * 0.1f, -mirrorNormal, cullBackfaces: FrontFacesOnly);
                            }
                            if (!mirrorHit.Hit)
                            {
                                RaycastAllSubmeshes(mirrorWorldPos + mirrorNormal * 0.1f, -mirrorNormal, out _, out mirrorHit);
                            }

                            if (mirrorHit.Hit)
                            {
                                Vector3 mRight = new Vector3(-decalRight.X, decalRight.Y, decalRight.Z).Normalized();
                                Vector3 mDown = new Vector3(-decalDown.X, decalDown.Y, decalDown.Z).Normalized();
                                float mUnitsU = (mirrorHit.WorldUnitsPerU > 1e-4f && mirrorHit.WorldUnitsPerU < 20.0f) ? mirrorHit.WorldUnitsPerU : 0.5f;
                                float mUnitsV = (mirrorHit.WorldUnitsPerV > 1e-4f && mirrorHit.WorldUnitsPerV < 20.0f) ? mirrorHit.WorldUnitsPerV : 0.5f;

                                _decalStamper.PlaceAt(mirrorHit.HitPositionWorld, mirrorHit.HitNormal, mirrorHit.HitUV, mRight, mDown, mirrorHit.WorldTangent, mirrorHit.WorldBitangent, mUnitsU, mUnitsV);
                                _decalStamper.BakeToActiveLayer();
                                _decalStamper.PlaceAt(_lastHit.HitPositionWorld, _lastHit.HitNormal, _lastHit.HitUV, decalRight, decalDown, _lastHit.WorldTangent, _lastHit.WorldBitangent, unitsU, unitsV);
                            }
                        }
                    }
                }
                else if (ToolMode == BrushToolMode.Text && _lastHit.Hit)
                {
                    if (_textProjector != null)
                    {
                        Basis decalBasis = _previewDecalNode != null ? _previewDecalNode.Transform.Basis : Basis.Identity;
                        Vector3 decalRight = decalBasis.Column0.Normalized();
                        Vector3 decalDown = decalBasis.Column2.Normalized();
                        float unitsU = (_lastHit.WorldUnitsPerU > 1e-4f && _lastHit.WorldUnitsPerU < 20.0f) ? _lastHit.WorldUnitsPerU : 0.5f;
                        float unitsV = (_lastHit.WorldUnitsPerV > 1e-4f && _lastHit.WorldUnitsPerV < 20.0f) ? _lastHit.WorldUnitsPerV : 0.5f;

                        _textProjector.PlaceAt(_lastHit.HitPositionWorld, _lastHit.HitNormal, _lastHit.HitUV, decalRight, decalDown, _lastHit.WorldTangent, _lastHit.WorldBitangent, unitsU, unitsV);
                        _textProjector.BakeToActiveLayer();

                        if (IsMirroringEnabled)
                        {
                            var (mirrorWorldPos, mirrorNormal) = ComputeMirroredPointAndNormal(_lastHit.HitPositionWorld, _lastHit.HitNormal);
                            RaycastHitResult mirrorHit = default;
                            if (_currentMesh != null && _raycaster != null && _raycaster.IsInitialized)
                            {
                                mirrorHit = _raycaster.IntersectRay(_currentMesh, mirrorWorldPos + mirrorNormal * 0.1f, -mirrorNormal, cullBackfaces: FrontFacesOnly);
                            }
                            if (!mirrorHit.Hit)
                            {
                                RaycastAllSubmeshes(mirrorWorldPos + mirrorNormal * 0.1f, -mirrorNormal, out _, out mirrorHit);
                            }

                            if (mirrorHit.Hit)
                            {
                                Vector3 mRight = new Vector3(-decalRight.X, decalRight.Y, decalRight.Z).Normalized();
                                Vector3 mDown = new Vector3(-decalDown.X, decalDown.Y, decalDown.Z).Normalized();
                                float mUnitsU = (mirrorHit.WorldUnitsPerU > 1e-4f && mirrorHit.WorldUnitsPerU < 20.0f) ? mirrorHit.WorldUnitsPerU : 0.5f;
                                float mUnitsV = (mirrorHit.WorldUnitsPerV > 1e-4f && mirrorHit.WorldUnitsPerV < 20.0f) ? mirrorHit.WorldUnitsPerV : 0.5f;

                                _textProjector.PlaceAt(mirrorHit.HitPositionWorld, mirrorHit.HitNormal, mirrorHit.HitUV, mRight, mDown, mirrorHit.WorldTangent, mirrorHit.WorldBitangent, mUnitsU, mUnitsV);
                                _textProjector.BakeToActiveLayer();
                                _textProjector.PlaceAt(_lastHit.HitPositionWorld, _lastHit.HitNormal, _lastHit.HitUV, decalRight, decalDown, _lastHit.WorldTangent, _lastHit.WorldBitangent, unitsU, unitsV);
                            }
                        }
                    }
                }
            }
            else if (!isLeftDown)
            {
                _isActionClickDown = false;
            }

            if (camera != null && _cameraBrush != null && GodotObject.IsInstanceValid(_cameraBrush))
            {
                Vector2 mousePos = GetViewportMousePosition();
                Vector3 rayOrigin = camera.ProjectRayOrigin(mousePos);
                Vector3 rayDir = camera.ProjectRayNormal(mousePos).Normalized();

                if (_lastHit.Hit)
                {
                    Vector3 surfaceNormal = _lastHit.HitNormal.Normalized();
                    if (surfaceNormal.LengthSquared() < 0.001f) surfaceNormal = -rayDir;
                    if (surfaceNormal.Dot(-rayDir) < 0f) surfaceNormal = -surfaceNormal; // Ensure CameraBrush stays in front of surface
                    Vector3 brushPos = _lastHit.HitPositionWorld + surfaceNormal * 0.15f;
                    _cameraBrush.GlobalPosition = brushPos;
                    Vector3 up = Mathf.Abs(surfaceNormal.Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
                    _cameraBrush.LookAt(_lastHit.HitPositionWorld, up);
                    _cameraBrush.Set("max_distance", 0.35f);
                }
                else
                {
                    _cameraBrush.GlobalPosition = rayOrigin;
                    Vector3 up = Mathf.Abs(rayDir.Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
                    _cameraBrush.LookAt(rayOrigin + rayDir, up);
                    _cameraBrush.Set("max_distance", 0.35f);
                }

                SyncCameraBrushProperties();
                _cameraBrush.Set("drawing", isPainting && _lastHit.Hit);

                if (IsMirroringEnabled && _mirrorCameraBrush != null && GodotObject.IsInstanceValid(_mirrorCameraBrush))
                {
                    if (_lastHit.Hit)
                    {
                        Vector3 surfaceNormal = _lastHit.HitNormal.Normalized();
                        if (surfaceNormal.LengthSquared() < 0.001f) surfaceNormal = -rayDir;
                        if (surfaceNormal.Dot(-rayDir) < 0f) surfaceNormal = -surfaceNormal;
                        var (mirrorPos, mirrorNormal) = ComputeMirroredPointAndDir(_lastHit.HitPositionWorld, surfaceNormal);
                        mirrorNormal = mirrorNormal.Normalized();
                        if (mirrorNormal.Dot(-rayDir) < 0f) mirrorNormal = -mirrorNormal;
                        Vector3 mirrorBrushPos = mirrorPos + mirrorNormal * 0.15f;
                        _mirrorCameraBrush.GlobalPosition = mirrorBrushPos;
                        Vector3 up = Mathf.Abs(mirrorNormal.Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
                        _mirrorCameraBrush.LookAt(mirrorPos, up);
                        _mirrorCameraBrush.Set("max_distance", 0.35f);
                    }
                    _mirrorCameraBrush.Set("drawing", isPainting && _lastHit.Hit);
                }
                else if (_mirrorCameraBrush != null && GodotObject.IsInstanceValid(_mirrorCameraBrush))
                {
                    _mirrorCameraBrush.Set("drawing", false);
                }
            }

            if (isPainting)
            {
                if (!_isMouseDown)
                {
                    _isMouseDown = true;
                    _strokeInProgress = true;
                    _layerManager?.RecordGpuUndoSnapshot();
                    if (_layerManager?.ActiveLayer != null)
                    {
                        _layerManager.ActiveLayer.IsCpuSynced = false;
                    }
                    EmitSignal(SignalName.StrokeStarted);
                }
            }
            else if (_isMouseDown && !isLeftDown)
            {
                FinishStroke();
            }
        }

        private void EnsureCursorGizmo()
        {
            if (_worldViewport == null)
            {
                _worldViewport = _currentMesh?.GetViewport() as SubViewport
                              ?? GetNodeOrNull<SubViewport>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport")
                              ?? GetTree()?.Root?.FindChild("WorldViewport", true, false) as SubViewport;
            }

            if (_cursorGizmo == null)
            {
                _cursorTorusMesh = new TorusMesh
                {
                    InnerRadius = 0.94f,
                    OuterRadius = 1.0f,
                    Rings = 48,
                    RingSegments = 8
                };
                _cursorSquareMesh = CreateSquareGizmoMesh();
                _cursorSplatterMesh = CreateSplatterGizmoMesh();
                _cursorGrungeMesh = CreateGrungeGizmoMesh();

                _cursorGizmo = new MeshInstance3D
                {
                    Name = "BrushCursorRingGizmo",
                    Mesh = _cursorTorusMesh,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    Layers = 1 | 2,
                    Visible = false
                };

                _cursorMaterial = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor = new Color(1.0f, 0.95f, 0.65f, 0.95f), // Glowing warm white/yellow
                    NoDepthTest = true,
                    RenderPriority = 126,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled
                };
                _cursorGizmo.MaterialOverride = _cursorMaterial;

                _eyedropperSprite = new Sprite3D
                {
                    Name = "EyedropperCursorIcon",
                    Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                    NoDepthTest = true,
                    RenderPriority = 127,
                    PixelSize = 0.002f,
                    Layers = 1 | 2,
                    Visible = false
                };
                _cursorGizmo.AddChild(_eyedropperSprite);
                CreateEyedropperSpriteTexture();

                _decalPreviewQuad = new MeshInstance3D
                {
                    Name = "DecalPreviewQuad",
                    Mesh = new QuadMesh { Size = new Vector2(2.0f, 2.0f), Orientation = PlaneMesh.OrientationEnum.Y },
                    Position = new Vector3(0, 0.005f, 0),
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    Layers = 1 | 2,
                    Visible = false
                };
                _decalMaterial = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    AlbedoColor = new Color(1.0f, 1.0f, 1.0f, 0.5f),
                    NoDepthTest = true,
                    RenderPriority = 127,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled
                };
                _decalPreviewQuad.MaterialOverride = _decalMaterial;
                _cursorGizmo.AddChild(_decalPreviewQuad);
            }

            // Ensure any light source attached to the cursor gizmo or camera illuminates strictly Layer 2 (Gizmos only)
            foreach (Node child in _cursorGizmo.GetChildren())
            {
                if (child is Light3D l) l.LightCullMask = 2;
            }
            if (_camera != null)
            {
                foreach (Node child in _camera.GetChildren())
                {
                    if (child is Light3D l) l.LightCullMask = 2;
                }
            }

            if (!_cursorGizmo.IsInsideTree())
            {
                if (_worldViewport != null && GodotObject.IsInstanceValid(_worldViewport))
                {
                    _worldViewport.AddChild(_cursorGizmo);
                }
                else if (_currentMesh != null && _currentMesh.IsInsideTree())
                {
                    _currentMesh.GetParent()?.AddChild(_cursorGizmo);
                }
            }

            if (_previewDecalNode == null)
            {
                _previewDecalNode = new Decal
                {
                    Name = "PainterLiveDecalPreview",
                    Size = new Vector3(0.5f, 1.2f, 0.5f),
                    AlbedoMix = 0.95f,
                    CullMask = 1,
                    NormalFade = 0.0f,
                    UpperFade = 0.0f,
                    LowerFade = 0.0f,
                    Visible = false
                };
            }

            if (!_previewDecalNode.IsInsideTree())
            {
                if (_worldViewport != null && GodotObject.IsInstanceValid(_worldViewport))
                {
                    _worldViewport.AddChild(_previewDecalNode);
                }
                else if (_currentMesh != null && _currentMesh.IsInsideTree())
                {
                    _currentMesh.GetParent()?.AddChild(_previewDecalNode);
                }
            }
        }

        private void CreateEyedropperSpriteTexture()
        {
            if (_eyedropperSprite == null) return;
            int size = 24;
            var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
            Color tip = new Color(0.95f, 0.85f, 0.3f, 1.0f);
            Color body = new Color(0.9f, 0.95f, 1.0f, 1.0f);
            Color bulb = new Color(1.0f, 0.3f, 0.25f, 1.0f);

            for (int i = 0; i < 10; i++)
            {
                int x = 4 + i;
                int y = 4 + i;
                Color col = i < 3 ? tip : body;
                if (x < size && y < size) img.SetPixel(x, y, col);
                if (x + 1 < size && y < size) img.SetPixel(x + 1, y, col);
                if (x < size && y + 1 < size) img.SetPixel(x, y + 1, col);
            }
            // Bulb at top right
            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    if (dx * dx + dy * dy <= 4)
                    {
                        int bx = size - 6 + dx;
                        int by = size - 6 + dy;
                        if (bx >= 0 && bx < size && by >= 0 && by < size)
                        {
                            img.SetPixel(bx, by, bulb);
                        }
                    }
                }
            }
            _eyedropperSprite.Texture = ImageTexture.CreateFromImage(img);
        }

        private Mesh GetGizmoMeshForShape(BrushShapeType shape)
        {
            return shape switch
            {
                BrushShapeType.Square => (Mesh)_cursorSquareMesh ?? _cursorTorusMesh,
                BrushShapeType.Splatter => (Mesh)_cursorSplatterMesh ?? _cursorTorusMesh,
                BrushShapeType.Grunge => (Mesh)_cursorGrungeMesh ?? _cursorTorusMesh,
                _ => _cursorTorusMesh
            };
        }

        private ArrayMesh CreateSquareGizmoMesh()
        {
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            float o = 1.0f;
            float i = 0.92f;
            Vector3 o1 = new Vector3(-o, 0, -o), o2 = new Vector3(o, 0, -o), o3 = new Vector3(o, 0, o), o4 = new Vector3(-o, 0, o);
            Vector3 i1 = new Vector3(-i, 0, -i), i2 = new Vector3(i, 0, -i), i3 = new Vector3(i, 0, i), i4 = new Vector3(-i, 0, i);

            void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                st.SetNormal(Vector3.Up);
                st.AddVertex(a); st.AddVertex(b); st.AddVertex(c);
                st.AddVertex(a); st.AddVertex(c); st.AddVertex(d);
            }
            AddQuad(o1, o2, i2, i1);
            AddQuad(o2, o3, i3, i2);
            AddQuad(o3, o4, i4, i3);
            AddQuad(o4, o1, i1, i4);

            return st.Commit();
        }

        private ArrayMesh CreateSplatterGizmoMesh()
        {
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            int segs = 32;
            float rOut = 1.0f;
            float rIn = 0.92f;

            void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                st.SetNormal(Vector3.Up);
                st.AddVertex(a); st.AddVertex(b); st.AddVertex(c);
                st.AddVertex(a); st.AddVertex(c); st.AddVertex(d);
            }

            for (int s = 0; s < segs; s++)
            {
                float a0 = s * Mathf.Tau / segs;
                float a1 = (s + 1) * Mathf.Tau / segs;
                Vector3 o0 = new Vector3(Mathf.Cos(a0) * rOut, 0, Mathf.Sin(a0) * rOut);
                Vector3 o1 = new Vector3(Mathf.Cos(a1) * rOut, 0, Mathf.Sin(a1) * rOut);
                Vector3 i0 = new Vector3(Mathf.Cos(a0) * rIn, 0, Mathf.Sin(a0) * rIn);
                Vector3 i1 = new Vector3(Mathf.Cos(a1) * rIn, 0, Mathf.Sin(a1) * rIn);
                AddQuad(o0, o1, i1, i0);

                if (s % 4 == 0)
                {
                    float aMid = (a0 + a1) * 0.5f;
                    Vector3 tip = new Vector3(Mathf.Cos(aMid) * 1.25f, 0, Mathf.Sin(aMid) * 1.25f);
                    st.SetNormal(Vector3.Up);
                    st.AddVertex(o0); st.AddVertex(tip); st.AddVertex(o1);
                }
            }

            return st.Commit();
        }

        private ArrayMesh CreateGrungeGizmoMesh()
        {
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            int segs = 24;

            void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                st.SetNormal(Vector3.Up);
                st.AddVertex(a); st.AddVertex(b); st.AddVertex(c);
                st.AddVertex(a); st.AddVertex(c); st.AddVertex(d);
            }

            for (int s = 0; s < segs; s++)
            {
                float a0 = s * Mathf.Tau / segs;
                float a1 = (s + 1) * Mathf.Tau / segs;
                float jag0 = (s % 2 == 0) ? 1.0f : 0.88f;
                float jag1 = ((s + 1) % 2 == 0) ? 1.0f : 0.88f;
                Vector3 o0 = new Vector3(Mathf.Cos(a0) * jag0, 0, Mathf.Sin(a0) * jag0);
                Vector3 o1 = new Vector3(Mathf.Cos(a1) * jag1, 0, Mathf.Sin(a1) * jag1);
                Vector3 i0 = new Vector3(Mathf.Cos(a0) * (jag0 - 0.08f), 0, Mathf.Sin(a0) * (jag0 - 0.08f));
                Vector3 i1 = new Vector3(Mathf.Cos(a1) * (jag1 - 0.08f), 0, Mathf.Sin(a1) * (jag1 - 0.08f));
                AddQuad(o0, o1, i1, i0);
            }

            return st.Commit();
        }

        public void ApplyOutlineSettings()
        {
            Color baseCol = GizmoDisplaySettings.PainterOutlineColor;
            Color outlineCol = new Color(baseCol.R, baseCol.G, baseCol.B, GizmoDisplaySettings.PainterOutlineOpacity);
            float width = GizmoDisplaySettings.PainterOutlineWidth;

            _outlineEdgeMat?.SetShaderParameter("outline_color", outlineCol);
            _outlineEdgeMat?.SetShaderParameter("outline_width", width);
            _outlineMaskMat?.SetShaderParameter("outline_color", outlineCol);
        }

        public Vector2 GetViewportMousePosition()
        {
            if (_worldViewportContainer != null && GodotObject.IsInstanceValid(_worldViewportContainer))
            {
                return _worldViewportContainer.GetLocalMousePosition();
            }
            if (_worldViewport != null && GodotObject.IsInstanceValid(_worldViewport))
            {
                return _worldViewport.GetMousePosition();
            }
            return GetViewport().GetMousePosition();
        }

        private void UpdateBrushCursor()
        {
            EnsureCursorGizmo();

            if (_cursorGizmo == null || !_cursorGizmo.IsInsideTree()) return;

            if (!IsPaintingActive || (_currentMesh == null && ToolMode != BrushToolMode.SelectSubmesh))
            {
                _cursorGizmo.Visible = false;
                return;
            }

            var camera = _worldViewport?.GetCamera3D() ?? _camera;
            if (camera == null)
            {
                _cursorGizmo.Visible = false;
                return;
            }

            Vector2 localMouse = GetViewportMousePosition();
            Vector3 rayOrigin = camera.ProjectRayOrigin(localMouse);
            Vector3 rayDir = camera.ProjectRayNormal(localMouse);

            RaycastHitResult hitResult = default;
            SubmeshNodeInfo hitSub = null;

            // 1. First test current active mesh with FrontFacesOnly
            if (_currentMesh != null)
            {
                hitResult = _raycaster.IntersectRay(_currentMesh, rayOrigin, rayDir, cullBackfaces: FrontFacesOnly);
                if (hitResult.Hit)
                {
                    hitSub = MeshHierarchy?.FindSubmesh(_currentMesh, hitResult.HitSurfaceIndex);
                }
                else if (FrontFacesOnly)
                {
                    // 1b. Fallback: try current mesh with cullBackfaces = false to hit double-sided / back-facing surfaces (skirts, hair, ribbons)
                    hitResult = _raycaster.IntersectRay(_currentMesh, rayOrigin, rayDir, cullBackfaces: false);
                    if (hitResult.Hit)
                    {
                        hitSub = MeshHierarchy?.FindSubmesh(_currentMesh, hitResult.HitSurfaceIndex);
                    }
                }
            }

            // 2. If no hit on current mesh, search across all submeshes of the character
            if (!hitResult.Hit)
            {
                if (RaycastAllSubmeshes(rayOrigin, rayDir, out var anySub, out var anyHit, cullBackfaces: FrontFacesOnly))
                {
                    hitResult = anyHit;
                    hitSub = anySub;
                }
                else if (FrontFacesOnly && RaycastAllSubmeshes(rayOrigin, rayDir, out anySub, out anyHit, cullBackfaces: false))
                {
                    hitResult = anyHit;
                    hitSub = anySub;
                }
            }

            _lastHit = hitResult;
            _lastHitSubmesh = hitSub;

            var hit = _lastHit;

            if (_debugFrameCount++ % 30 == 0)
            {
                GD.Print($"[BrushDebug] Active: {IsPaintingActive} | InsideTree: {_cursorGizmo.IsInsideTree()} | Target: {_currentMesh?.Name} | MousePos: {localMouse} | Hit: {hit.Hit} | HitPos: {hit.HitPositionWorld}");
            }

            if (hit.Hit)
            {
                if (ToolMode == BrushToolMode.MagicWand || ToolMode == BrushToolMode.Selection || ToolMode == BrushToolMode.Shape)
                {
                    _cursorGizmo.Visible = false;
                    _cursorGizmo.Mesh = null;
                }
                else
                {
                    _cursorGizmo.Visible = true;
                }

                // Scale ring diameter to match CameraBrush size in world units
                float radius;
                if (ToolMode == BrushToolMode.SelectSubmesh)
                {
                    radius = 0.035f; // Compact, accurate reticle for submesh selection
                }
                else
                {
                    int atlasRes = _layerManager?.CanvasSize.X ?? 2048;
                    float worldPixelSize = 1.6f / atlasRes;
                    float worldDiameter = Mathf.Max(worldPixelSize * 0.5f, BrushSize * worldPixelSize);
                    radius = worldDiameter * 0.5f;
                }

                // Align ring normal to hit.HitNormal using UV tangents when available
                Vector3 normal = hit.HitNormal.Normalized();
                Vector3 tangent = Vector3.Right;
                Vector3 bitangent = Vector3.Forward;

                if (normal.LengthSquared() > 0.001f)
                {
                    if (hit.WorldTangent.LengthSquared() > 0.001f)
                    {
                        tangent = (hit.WorldTangent - normal * normal.Dot(hit.WorldTangent)).Normalized();
                        bitangent = hit.WorldBitangent.LengthSquared() > 0.001f
                            ? (hit.WorldBitangent - normal * normal.Dot(hit.WorldBitangent)).Normalized()
                            : tangent.Cross(normal).Normalized();
                        if (tangent.Cross(normal).Dot(bitangent) < 0)
                        {
                            bitangent = tangent.Cross(normal).Normalized();
                        }
                    }
                    else
                    {
                        Vector3 upGuide = Mathf.Abs(normal.Dot(Vector3.Up)) > 0.95f ? Vector3.Forward : Vector3.Up;
                        tangent = normal.Cross(upGuide).Normalized();
                        bitangent = tangent.Cross(normal).Normalized();
                    }
                    // Basis: X = tangent (UV +U), Y = normal, Z = bitangent (UV +V)
                    _cursorGizmo.Basis = new Basis(tangent, normal, bitangent).Scaled(new Vector3(radius, radius, radius));
                }
                else
                {
                    _cursorGizmo.Scale = new Vector3(radius, radius, radius);
                }

                _cursorGizmo.GlobalPosition = hit.HitPositionWorld + normal * 0.002f;

                if (ToolMode == BrushToolMode.Eyedropper)
                {
                    _cursorGizmo.Mesh = null;
                    if (_eyedropperSprite != null) _eyedropperSprite.Visible = true;
                    if (_decalPreviewQuad != null) _decalPreviewQuad.Visible = false;
                    if (_previewDecalNode != null) _previewDecalNode.Visible = false;
                }
                else if (ToolMode == BrushToolMode.SelectSubmesh)
                {
                    _cursorGizmo.Mesh = _cursorTorusMesh;
                    if (_eyedropperSprite != null) _eyedropperSprite.Visible = false;
                    if (_decalPreviewQuad != null) _decalPreviewQuad.Visible = false;
                    if (_previewDecalNode != null) _previewDecalNode.Visible = false;
                    if (_cursorMaterial != null) _cursorMaterial.AlbedoColor = new Color(0.3f, 0.8f, 1.0f, 0.95f);
                }
                else if (ToolMode == BrushToolMode.MagicWand)
                {
                    _cursorGizmo.Mesh = null;
                    _cursorGizmo.Visible = false;
                    if (_eyedropperSprite != null) _eyedropperSprite.Visible = false;
                    if (_decalPreviewQuad != null) _decalPreviewQuad.Visible = false;
                    if (_previewDecalNode != null) _previewDecalNode.Visible = false;
                }
                else if (ToolMode == BrushToolMode.Decal)
                {
                    _cursorGizmo.Mesh = _cursorTorusMesh;
                    if (_eyedropperSprite != null) _eyedropperSprite.Visible = false;
                    if (_decalPreviewQuad != null) _decalPreviewQuad.Visible = false;
                    if (_cursorMaterial != null) _cursorMaterial.AlbedoColor = new Color(0.35f, 0.85f, 1.0f, 0.95f);

                    var tex = _decalStamper?.DecalTexture;
                    if (tex != null && _previewDecalNode != null)
                    {
                        _previewDecalNode.Visible = true;
                        _previewDecalNode.SetTexture(Decal.DecalTexture.Albedo, tex);
                        float scale = _decalStamper?.DecalScale ?? 0.25f;
                        float rotDeg = _decalStamper?.RotationDegrees ?? 0.0f;
                        float aspect = (tex.GetHeight() > 0) ? (float)tex.GetWidth() / tex.GetHeight() : 1.0f;

                        float unitsU = (hit.WorldUnitsPerU > 1e-4f && hit.WorldUnitsPerU < 20.0f) ? hit.WorldUnitsPerU : 0.5f;
                        float unitsV = (hit.WorldUnitsPerV > 1e-4f && hit.WorldUnitsPerV < 20.0f) ? hit.WorldUnitsPerV : 0.5f;
                        float uvSpanU = (aspect >= 1.0f) ? scale * aspect : scale;
                        float uvSpanV = (aspect < 1.0f) ? scale / aspect : scale;
                        float sizeX = uvSpanU * unitsU;
                        float sizeZ = uvSpanV * unitsV;
                        float depthY = Mathf.Max(1.0f, Mathf.Max(sizeX, sizeZ) * 2.0f);
                        _previewDecalNode.NormalFade = 0.0f;
                        _previewDecalNode.UpperFade = 0.0f;
                        _previewDecalNode.LowerFade = 0.0f;
                        _previewDecalNode.Size = new Vector3(sizeX, depthY, sizeZ);

                        Basis basis = new Basis(tangent, normal, bitangent);
                        basis = basis.Rotated(normal, Mathf.DegToRad(rotDeg));
                        _previewDecalNode.Transform = new Transform3D(basis, hit.HitPositionWorld);
                    }
                    else if (_previewDecalNode != null)
                    {
                        _previewDecalNode.Visible = false;
                    }
                }
                else if (ToolMode == BrushToolMode.Text)
                {
                    _cursorGizmo.Mesh = _cursorTorusMesh;
                    if (_eyedropperSprite != null) _eyedropperSprite.Visible = false;
                    if (_decalPreviewQuad != null) _decalPreviewQuad.Visible = false;
                    if (_cursorMaterial != null) _cursorMaterial.AlbedoColor = new Color(0.85f, 0.45f, 1.0f, 0.95f);

                    var tex = _textProjector?.CurrentTexture ?? _textProjector?.TextTexture;
                    if (tex != null && _previewDecalNode != null)
                    {
                        _previewDecalNode.Visible = true;
                        _previewDecalNode.SetTexture(Decal.DecalTexture.Albedo, tex);
                        float scale = _textProjector?.TextScale ?? 0.25f;
                        float rotDeg = _textProjector?.RotationDegrees ?? 0.0f;
                        float aspect = (tex.GetHeight() > 0) ? (float)tex.GetWidth() / tex.GetHeight() : 1.0f;

                        float unitsU = (hit.WorldUnitsPerU > 1e-4f && hit.WorldUnitsPerU < 20.0f) ? hit.WorldUnitsPerU : 0.5f;
                        float unitsV = (hit.WorldUnitsPerV > 1e-4f && hit.WorldUnitsPerV < 20.0f) ? hit.WorldUnitsPerV : 0.5f;
                        float uvSpanU = (aspect >= 1.0f) ? scale * aspect : scale;
                        float uvSpanV = (aspect < 1.0f) ? scale / aspect : scale;
                        float sizeX = uvSpanU * unitsU;
                        float sizeZ = uvSpanV * unitsV;
                        float depthY = Mathf.Max(1.0f, Mathf.Max(sizeX, sizeZ) * 2.0f);
                        _previewDecalNode.NormalFade = 0.0f;
                        _previewDecalNode.UpperFade = 0.0f;
                        _previewDecalNode.LowerFade = 0.0f;
                        _previewDecalNode.Size = new Vector3(sizeX, depthY, sizeZ);

                        Basis basis = new Basis(tangent, normal, bitangent);
                        basis = basis.Rotated(normal, Mathf.DegToRad(rotDeg));
                        _previewDecalNode.Transform = new Transform3D(basis, hit.HitPositionWorld);
                    }
                    else if (_previewDecalNode != null)
                    {
                        _previewDecalNode.Visible = false;
                    }
                }
                else
                {
                    BrushShapeType activeShape = (ToolMode == BrushToolMode.Erase) ? EraserShape : BrushShape;
                    _cursorGizmo.Mesh = GetGizmoMeshForShape(activeShape);
                    if (_eyedropperSprite != null) _eyedropperSprite.Visible = false;
                    if (_decalPreviewQuad != null) _decalPreviewQuad.Visible = false;
                    if (_previewDecalNode != null) _previewDecalNode.Visible = false;

                    if (ToolMode == BrushToolMode.Erase)
                    {
                        // Tint ring red for eraser
                        if (_cursorMaterial != null) _cursorMaterial.AlbedoColor = new Color(1.0f, 0.25f, 0.2f, 0.95f);
                    }
                    else
                    {
                        // Glowing warm white/yellow for brush / fill
                        if (_cursorMaterial != null) _cursorMaterial.AlbedoColor = new Color(1.0f, 0.95f, 0.65f, 0.95f);
                    }
                }
            }
            else
            {
                _cursorGizmo.Visible = false;
                if (_previewDecalNode != null) _previewDecalNode.Visible = false;
            }
        }

        public void Setup(Camera3D camera, SubViewport worldViewport, SkinLayerManager layerManager, SubViewportContainer viewportContainer = null)
        {
            _camera = camera;
            _worldViewport = worldViewport;
            _layerManager = layerManager;
            _worldViewportContainer = viewportContainer 
                                   ?? (_worldViewport?.GetParent() as SubViewportContainer)
                                   ?? (GetTree()?.Root?.FindChild("SubViewportContainer", true, false) as SubViewportContainer);

            _brushPalette ??= GetTree()?.Root?.FindChild("FloatingBrushPalette", true, false) as FloatingBrushPaletteUI;

            EnsureCursorGizmo();
            EnsureCameraBrush();
            AttachCameraBrushWorld();

            if (_layerManager != null)
            {
                _layerManager.LayerSelected -= OnLayerManagerAtlasChanged;
                _layerManager.LayerRemoved -= OnLayerManagerAtlasChanged;
                _layerManager.LayerAdded -= OnLayerManagerAtlasAdded;
                _layerManager.LayersReordered -= OnLayerManagerAtlasReordered;
                _layerManager.StackChanged -= OnLayerManagerAtlasReordered;

                _layerManager.LayerSelected += OnLayerManagerAtlasChanged;
                _layerManager.LayerRemoved += OnLayerManagerAtlasChanged;
                _layerManager.LayerAdded += OnLayerManagerAtlasAdded;
                _layerManager.LayersReordered += OnLayerManagerAtlasReordered;
                _layerManager.StackChanged += OnLayerManagerAtlasReordered;
            }
        }

        private void OnLayerManagerAtlasChanged(int index)
        {
            if (_cameraBrush != null && GodotObject.IsInstanceValid(_cameraBrush))
            {
                _cameraBrush.Call("get_atlas_textures");
            }
            if (_mirrorCameraBrush != null && GodotObject.IsInstanceValid(_mirrorCameraBrush))
            {
                _mirrorCameraBrush.Call("get_atlas_textures");
            }
        }

        private void OnLayerManagerAtlasAdded(int index, string name)
        {
            OnLayerManagerAtlasChanged(index);
        }

        private void OnLayerManagerAtlasReordered()
        {
            OnLayerManagerAtlasChanged(0);
        }

        public void SetTargetMesh(MeshInstance3D mesh, int surfaceIndex = 0)
        {
            _currentMesh = mesh;
            _currentSurfaceIndex = surfaceIndex;

            if (_currentMesh != null)
            {
                _raycaster.BuildFromMesh(_currentMesh, -1);
                _layerManager?.SetupForMesh(_currentMesh, surfaceIndex);
                int atlasDim = _layerManager?.CanvasSize.X ?? 2048;
                _magicWandTool?.SelectionMask?.EnsureSize(atlasDim);
                ApplyFrontFacesOnlyToMaterials();
            }

            UpdateSelectionOutline();

            if (_cameraBrush != null && GodotObject.IsInstanceValid(_cameraBrush))
            {
                _cameraBrush.Call("get_atlas_textures");
            }
        }

        private void EnsureSelectionOutline()
        {
            if (_worldViewport == null)
            {
                _worldViewport = _currentMesh?.GetViewport() as SubViewport
                              ?? GetNodeOrNull<SubViewport>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport")
                              ?? GetTree()?.Root?.FindChild("WorldViewport", true, false) as SubViewport;
            }

            if (_selectionOutlineMesh == null)
            {
                var maskShader = GD.Load<Shader>("res://assets/shaders/painter/selection_mask.gdshader");
                var outlineShader = GD.Load<Shader>("res://assets/shaders/painter/selection_outline.gdshader");

                _outlineEdgeMat = new ShaderMaterial { Shader = outlineShader };
                _outlineMaskMat = new ShaderMaterial { Shader = maskShader };
                _outlineMaskMat.SetShaderParameter("is_mesh_hidden", false);
                _outlineMaskMat.NextPass = _outlineEdgeMat;
                ApplyOutlineSettings();

                _selectionOutlineMesh = new MeshInstance3D
                {
                    Name = "SelectedMeshOutlineGizmo",
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    Layers = 1,
                    Visible = false,
                    MaterialOverride = _outlineMaskMat
                };
            }

            if (!_selectionOutlineMesh.IsInsideTree())
            {
                if (_worldViewport != null && GodotObject.IsInstanceValid(_worldViewport))
                {
                    _worldViewport.AddChild(_selectionOutlineMesh);
                }
                else if (_currentMesh != null && _currentMesh.IsInsideTree())
                {
                    _currentMesh.GetParent()?.AddChild(_selectionOutlineMesh);
                }
            }
        }

        private void UpdateSelectionOutline()
        {
            EnsureSelectionOutline();
            if (_selectionOutlineMesh == null || !GodotObject.IsInstanceValid(_selectionOutlineMesh)) return;

            if (IsPaintingActive && _currentMesh != null && GodotObject.IsInstanceValid(_currentMesh) && _currentMesh.IsInsideTree())
            {
                _selectionOutlineMesh.Visible = true;
                _selectionOutlineMesh.Mesh = _currentMesh.Mesh;
                _selectionOutlineMesh.Skin = _currentMesh.Skin;
                _selectionOutlineMesh.Skeleton = _currentMesh.Skeleton;
                _selectionOutlineMesh.GlobalTransform = _currentMesh.GlobalTransform;

                bool isHidden = !_currentMesh.Visible;
                _outlineMaskMat?.SetShaderParameter("is_mesh_hidden", isHidden);
                _outlineEdgeMat?.SetShaderParameter("is_mesh_hidden", isHidden);
            }
            else
            {
                _selectionOutlineMesh.Visible = false;
            }
        }

        public override void _UnhandledInput(InputEvent @event)
        {
            if (!IsPaintingActive) return;

            if (@event is InputEventMouseButton mbRelease && !mbRelease.Pressed && mbRelease.ButtonIndex == MouseButton.Left)
            {
                _clickOriginatedOnUI = false;
                _isActionClickDown = false;
            }

            if (IsMouseOverUI() || _clickOriginatedOnUI) return;

            if (@event is InputEventMouseButton mouseBtn && mouseBtn.ButtonIndex == MouseButton.Left && mouseBtn.Pressed)
            {
                if (_isActionClickDown) return;
                _isActionClickDown = true;

                if ((mouseBtn.AltPressed && ToolMode != BrushToolMode.MagicWand) || ToolMode == BrushToolMode.SelectSubmesh)
                {
                    Vector2 pos = GetViewportMousePosition();
                    var camera = _worldViewport?.GetCamera3D() ?? _camera;
                    if (camera != null)
                    {
                        Vector3 origin = camera.ProjectRayOrigin(pos);
                        Vector3 dir = camera.ProjectRayNormal(pos).Normalized();
                        var hits = RaycastAllSubmeshesSorted(origin, dir);
                        if (hits.Count > 0)
                        {
                            if (mouseBtn.AltPressed)
                            {
                                if (pos.DistanceTo(_lastPeelMousePos) < 20.0f)
                                {
                                    _depthPeelIndex = (_depthPeelIndex + 1) % hits.Count;
                                }
                                else
                                {
                                    _depthPeelIndex = 0;
                                }
                            }
                            else
                            {
                                _depthPeelIndex = 0;
                            }
                            _lastPeelMousePos = pos;

                            var chosen = hits[_depthPeelIndex];
                            GD.Print($"[MeshPainter3D] Depth peel select: layer {_depthPeelIndex + 1}/{hits.Count} -> {chosen.Submesh.DisplayName}");
                            MeshHierarchy?.SelectTarget(chosen.Submesh, chosen.Submesh.SurfaceIndex);
                            GetViewport()?.SetInputAsHandled();
                            return;
                        }
                    }
                }
                else if (ToolMode == BrushToolMode.Eyedropper)
                {
                    Vector2 pos = GetViewportMousePosition();
                    var camera = _worldViewport?.GetCamera3D() ?? _camera;
                    if (camera != null)
                    {
                        var hit = _raycaster.IntersectRay(_currentMesh, camera.ProjectRayOrigin(pos), camera.ProjectRayNormal(pos), cullBackfaces: FrontFacesOnly);
                        if (hit.Hit)
                        {
                            SampleColorAtUV(hit.HitUV, hit.HitSurfaceIndex);
                            GetViewport()?.SetInputAsHandled();
                        }
                    }
                }
                else if (ToolMode == BrushToolMode.BucketFill)
                {
                    Vector2 pos = GetViewportMousePosition();
                    var camera = _worldViewport?.GetCamera3D() ?? _camera;
                    if (camera != null)
                    {
                        Vector3 origin = camera.ProjectRayOrigin(pos);
                        Vector3 dir = camera.ProjectRayNormal(pos).Normalized();
                        var hit = _currentMesh != null ? _raycaster.IntersectRay(_currentMesh, origin, dir, cullBackfaces: FrontFacesOnly) : default;
                        if (hit.Hit)
                        {
                            _layerManager?.FillSubmesh(_currentMesh, BrushColor, hit.HitUV, _magicWandTool, BucketFillTolerance);
                            GetViewport()?.SetInputAsHandled();
                        }
                        else if (RaycastAllSubmeshes(origin, dir, out var otherSub, out var otherHit))
                        {
                            MeshHierarchy?.SelectTarget(otherSub, otherSub.SurfaceIndex);
                            _layerManager?.FillSubmesh(otherSub.Mesh, BrushColor, otherHit.HitUV, _magicWandTool, BucketFillTolerance);
                            GetViewport()?.SetInputAsHandled();
                        }
                    }
                }
                else if (ToolMode == BrushToolMode.MagicWand)
                {
                    Vector2 pos = GetViewportMousePosition();
                    var camera = _worldViewport?.GetCamera3D() ?? _camera;
                    if (camera != null)
                    {
                        Vector3 origin = camera.ProjectRayOrigin(pos);
                        Vector3 dir = camera.ProjectRayNormal(pos).Normalized();

                        MagicWandCombineMode combineMode = MagicWandCombineMode.Replace;
                        if (mouseBtn.ShiftPressed || Input.IsKeyPressed(Key.Shift)) combineMode = MagicWandCombineMode.Add;
                        else if (mouseBtn.AltPressed || Input.IsKeyPressed(Key.Alt)) combineMode = MagicWandCombineMode.Subtract;

                        var hit = _raycaster.IntersectRay(_currentMesh, origin, dir, cullBackfaces: FrontFacesOnly);
                        if (hit.Hit)
                        {
                            ExecuteMagicWandSelection(hit, combineMode);
                            GetViewport()?.SetInputAsHandled();
                        }
                        else if (RaycastAllSubmeshes(origin, dir, out var otherSub, out var otherHit))
                        {
                            MeshHierarchy?.SelectTarget(otherSub, otherSub.SurfaceIndex);
                            ExecuteMagicWandSelection(otherHit, combineMode);
                            GetViewport()?.SetInputAsHandled();
                        }
                    }
                }
            }
        }

        public override void _Input(InputEvent @event)
        {
            if (!IsPaintingActive) return;

            // 0. Dedicated 3D Shape Tool Handling
            if (ToolMode == BrushToolMode.Shape)
            {
                if (@event is InputEventKey shapeKeyEv && shapeKeyEv.Pressed)
                {
                    if (shapeKeyEv.Keycode == Key.Escape)
                    {
                        if (_isDraggingShape3D || _shapeTool.HasActiveShape)
                        {
                            _isDraggingShape3D = false;
                            _shapeTool.CancelShape();
                            _layerManager?.RecompositeGpuLayers();
                            _selectionOverlay3D?.QueueRedraw();
                            GetViewport()?.SetInputAsHandled();
                            return;
                        }
                    }
                    else if (shapeKeyEv.Keycode == Key.Enter || shapeKeyEv.Keycode == Key.KpEnter)
                    {
                        if (_shapeTool.HasActiveShape)
                        {
                            _isDraggingShape3D = false;
                            _shapeTool.EndHandleDrag();
                            _shapeTool.CommitShape(_layerManager, BrushColor, (LayerBlendMode)BlendMode, _magicWandTool);
                            _selectionOverlay3D?.QueueRedraw();
                            GetViewport()?.SetInputAsHandled();
                            return;
                        }
                    }
                }

                if (@event is InputEventMouseButton shapeMb)
                {
                    if (shapeMb.ButtonIndex == MouseButton.Right && shapeMb.Pressed)
                    {
                        if (_isDraggingShape3D || _shapeTool.HasActiveShape)
                        {
                            _isDraggingShape3D = false;
                            _shapeTool.CancelShape();
                            _layerManager?.RecompositeGpuLayers();
                            _selectionOverlay3D?.QueueRedraw();
                            GetViewport()?.SetInputAsHandled();
                            return;
                        }
                    }

                    if (shapeMb.ButtonIndex == MouseButton.Left)
                    {
                        if (shapeMb.Pressed)
                        {
                            if (IsMouseOverUI() || _clickOriginatedOnUI) return;
                            var vpContainer = _worldViewportContainer;
                            if (vpContainer != null && !vpContainer.GetGlobalRect().HasPoint(shapeMb.GlobalPosition)) return;

                            var camera = _worldViewport?.GetCamera3D() ?? _camera;
                            if (camera == null) return;

                            Vector2 mousePos = GetViewportMousePosition();
                            Vector3 origin = camera.ProjectRayOrigin(mousePos);
                            Vector3 dir = camera.ProjectRayNormal(mousePos).Normalized();

                            RaycastHitResult hit = default;
                            if (_currentMesh != null)
                            {
                                hit = _raycaster.IntersectRay(_currentMesh, origin, dir, cullBackfaces: FrontFacesOnly);
                            }
                            if (!hit.Hit)
                            {
                                if (RaycastAllSubmeshes(origin, dir, out var otherSub, out var otherHit))
                                {
                                    MeshHierarchy?.SelectTarget(otherSub, otherSub.SurfaceIndex);
                                    hit = otherHit;
                                }
                            }

                            if (hit.Hit)
                            {
                                EnsureSelectionOverlay3D();
                                _isDraggingShape3D = true;
                                _shapeStartScreenPos = mousePos;
                                _shapeCurrentScreenPos = mousePos;
                                _shapeStartUV = hit.HitUV;
                                _shapeCurrentUV = hit.HitUV;
                                _shapeStartWorldPos = hit.HitPositionWorld;
                                _shapeStartWorldNormal = hit.HitNormal.Normalized();
                                _shapeStartWorldTangent = hit.WorldTangent.LengthSquared() > 0.001f ? hit.WorldTangent.Normalized() : Vector3.Right;
                                _shapeStartWorldBitangent = hit.WorldBitangent.LengthSquared() > 0.001f ? hit.WorldBitangent.Normalized() : Vector3.Forward;
                                _shapeStartUnitsPerU = (hit.WorldUnitsPerU > 1e-4f && hit.WorldUnitsPerU < 20.0f) ? hit.WorldUnitsPerU : 0.5f;
                                _shapeStartUnitsPerV = (hit.WorldUnitsPerV > 1e-4f && hit.WorldUnitsPerV < 20.0f) ? hit.WorldUnitsPerV : 0.5f;

                                int atlasSize = _layerManager?.CanvasSize.X ?? 2048;
                                Vector2 startAtlasPx = _shapeStartUV * atlasSize;
                                _shapeTool.BeginNewShape(startAtlasPx);
                                _lastShapePreviewMs = Time.GetTicksMsec();
                                _brushPalette?.SyncShapeControls();
                                _selectionOverlay3D?.QueueRedraw();
                                GetViewport()?.SetInputAsHandled();
                                return;
                            }
                        }
                        else // Left release
                        {
                            if (_isDraggingShape3D)
                            {
                                _isDraggingShape3D = false;
                                _shapeTool.EndHandleDrag();
                                if (_shapeStartScreenPos.DistanceTo(_shapeCurrentScreenPos) >= 3.0f)
                                {
                                    _layerManager?.EnsureCpuSynced();
                                    _shapeTool.CommitShape(_layerManager, BrushColor, (LayerBlendMode)BlendMode, _magicWandTool);
                                }
                                else
                                {
                                    _shapeTool.CancelShape();
                                    _layerManager?.RecompositeGpuLayers();
                                }
                                _selectionOverlay3D?.QueueRedraw();
                                GetViewport()?.SetInputAsHandled();
                                return;
                            }
                        }
                    }
                }

                if (@event is InputEventMouseMotion shapeMm)
                {
                    if (_isDraggingShape3D)
                    {
                        var camera = _worldViewport?.GetCamera3D() ?? _camera;
                        if (camera != null)
                        {
                            Vector2 mousePos = GetViewportMousePosition();
                            _shapeCurrentScreenPos = mousePos;
                            Vector3 origin = camera.ProjectRayOrigin(mousePos);
                            Vector3 dir = camera.ProjectRayNormal(mousePos).Normalized();

                            // Accurate curved surface raycast tracking with tangent plane fallback
                            RaycastHitResult currentHit = default;
                            if (_currentMesh != null && _raycaster != null && _raycaster.IsInitialized)
                            {
                                currentHit = _raycaster.IntersectRay(_currentMesh, origin, dir, cullBackfaces: FrontFacesOnly);
                            }
                            if (!currentHit.Hit)
                            {
                                RaycastAllSubmeshes(origin, dir, out _, out currentHit, cullBackfaces: FrontFacesOnly);
                            }

                            if (currentHit.Hit)
                            {
                                _shapeCurrentUV = currentHit.HitUV;
                            }
                            else
                            {
                                float denom = dir.Dot(_shapeStartWorldNormal);
                                if (Mathf.Abs(denom) > 1e-5f)
                                {
                                    float t = (_shapeStartWorldPos - origin).Dot(_shapeStartWorldNormal) / denom;
                                    if (t > 0)
                                    {
                                        Vector3 planePt = origin + dir * t;
                                        Vector3 deltaWorld = planePt - _shapeStartWorldPos;
                                        float deltaU = deltaWorld.Dot(_shapeStartWorldTangent) / _shapeStartUnitsPerU;
                                        float deltaV = deltaWorld.Dot(_shapeStartWorldBitangent) / _shapeStartUnitsPerV;
                                        _shapeCurrentUV = new Vector2(
                                            Mathf.Clamp(_shapeStartUV.X + deltaU, 0.0f, 1.0f),
                                            Mathf.Clamp(_shapeStartUV.Y + deltaV, 0.0f, 1.0f)
                                        );
                                    }
                                }
                                else
                                {
                                    Vector2 screenDelta = _shapeCurrentScreenPos - _shapeStartScreenPos;
                                    float approxUvScale = 1.0f / 400.0f;
                                    _shapeCurrentUV = new Vector2(
                                        Mathf.Clamp(_shapeStartUV.X + screenDelta.X * approxUvScale, 0.0f, 1.0f),
                                        Mathf.Clamp(_shapeStartUV.Y + screenDelta.Y * approxUvScale, 0.0f, 1.0f)
                                    );
                                }
                            }

                            int atlasSize = _layerManager?.CanvasSize.X ?? 2048;
                            Vector2 currentAtlasPx = _shapeCurrentUV * atlasSize;
                            _shapeTool.UpdateNewShapeDrag(currentAtlasPx, Input.IsKeyPressed(Key.Shift));

                            _brushPalette?.SyncShapeControls();
                            _selectionOverlay3D?.QueueRedraw();
                            GetViewport()?.SetInputAsHandled();
                            return;
                        }
                    }
                }

                return;
            }

            // 1. Dedicated 3D Selection Tool Handling
            if (ToolMode == BrushToolMode.Selection)
            {
                // Escape key cancels active selection
                if (@event is InputEventKey keyEv && keyEv.Pressed && keyEv.Keycode == Key.Escape)
                {
                    if (_isSelecting3D || _isBuildingPoly3D)
                    {
                        Cancel3DSelection();
                        GetViewport()?.SetInputAsHandled();
                        return;
                    }
                }

                // Mouse buttons (press, release, cancel)
                if (@event is InputEventMouseButton mb)
                {
                    if (mb.ButtonIndex == MouseButton.Right && mb.Pressed)
                    {
                        if (_isSelecting3D || _isBuildingPoly3D)
                        {
                            Cancel3DSelection();
                            GetViewport()?.SetInputAsHandled();
                            return;
                        }
                    }

                    if (mb.ButtonIndex == MouseButton.Left)
                    {
                        if (mb.Pressed)
                        {
                            if (IsMouseOverUI() || _clickOriginatedOnUI) return;

                            var vpContainer = _worldViewportContainer;
                            if (vpContainer != null && !vpContainer.GetGlobalRect().HasPoint(mb.GlobalPosition)) return;

                            EnsureSelectionOverlay3D();
                            Vector2 pos = GetViewportMousePosition();
                            var selType = SelectionMask?.CurrentToolType ?? _brushPalette?.CurrentSelectionType ?? SelectionToolType.Rectangular;

                            if (selType == SelectionToolType.Rectangular)
                            {
                                _isSelecting3D = true;
                                _selectionStartScreenPos = pos;
                                _selectionCurrentScreenPos = pos;
                                _selectionOverlay3D?.QueueRedraw();
                                GetViewport()?.SetInputAsHandled();
                                return;
                            }
                            else if (selType == SelectionToolType.Lasso)
                            {
                                _isSelecting3D = true;
                                _lassoPoints3D.Clear();
                                _lassoPoints3D.Add(pos);
                                _selectionOverlay3D?.QueueRedraw();
                                GetViewport()?.SetInputAsHandled();
                                return;
                            }
                            else if (selType == SelectionToolType.Polygonal)
                            {
                                if (!_isBuildingPoly3D)
                                {
                                    _isBuildingPoly3D = true;
                                    _polyPoints3D.Clear();
                                    _polyPoints3D.Add(pos);
                                    _polyCurrentScreenPos = pos;
                                }
                                else
                                {
                                    bool isNearStart = _polyPoints3D.Count >= 3 && pos.DistanceTo(_polyPoints3D[0]) <= 10.0f;
                                    if (mb.DoubleClick || isNearStart)
                                    {
                                        CloseAndApply3DPolygonSelection();
                                    }
                                    else
                                    {
                                        _polyPoints3D.Add(pos);
                                    }
                                }
                                _selectionOverlay3D?.QueueRedraw();
                                GetViewport()?.SetInputAsHandled();
                                return;
                            }
                        }
                        else
                        {
                            if (_isSelecting3D)
                            {
                                var selType = SelectionMask?.CurrentToolType ?? _brushPalette?.CurrentSelectionType ?? SelectionToolType.Rectangular;
                                if (selType == SelectionToolType.Rectangular)
                                {
                                    _isSelecting3D = false;
                                    Apply3DRectSelection();
                                    _selectionOverlay3D?.QueueRedraw();
                                    GetViewport()?.SetInputAsHandled();
                                    return;
                                }
                                else if (selType == SelectionToolType.Lasso)
                                {
                                    _isSelecting3D = false;
                                    Apply3DLassoSelection();
                                    _selectionOverlay3D?.QueueRedraw();
                                    GetViewport()?.SetInputAsHandled();
                                    return;
                                }
                            }
                        }
                    }
                }

                // Mouse motion for updating 3D marquee
                if (@event is InputEventMouseMotion)
                {
                    if (_isSelecting3D || _isBuildingPoly3D)
                    {
                        Vector2 localMouse = GetViewportMousePosition();
                        var selType = SelectionMask?.CurrentToolType ?? _brushPalette?.CurrentSelectionType ?? SelectionToolType.Rectangular;
                        if (_isSelecting3D && selType == SelectionToolType.Rectangular)
                        {
                            _selectionCurrentScreenPos = localMouse;
                            _selectionOverlay3D?.QueueRedraw();
                        }
                        else if (_isSelecting3D && selType == SelectionToolType.Lasso)
                        {
                            if (_lassoPoints3D.Count == 0 || _lassoPoints3D[_lassoPoints3D.Count - 1].DistanceTo(localMouse) > 4.0f)
                            {
                                _lassoPoints3D.Add(localMouse);
                                _selectionOverlay3D?.QueueRedraw();
                            }
                        }
                        else if (_isBuildingPoly3D && selType == SelectionToolType.Polygonal)
                        {
                            _polyCurrentScreenPos = localMouse;
                            _selectionOverlay3D?.QueueRedraw();
                        }
                    }
                }

                return;
            }

            // 2. Stroke finish for standard paint/erase
            if (@event is InputEventMouseButton mouseBtn && mouseBtn.ButtonIndex == MouseButton.Left && !mouseBtn.Pressed)
            {
                _isActionClickDown = false;
                FinishStroke();
            }
        }

        public void FinishStroke()
        {
            if (!_isMouseDown && !_strokeInProgress) return;
            _isMouseDown = false;
            _strokeInProgress = false;
            _isActionClickDown = false;

            if (_layerManager != null)
            {
                _layerManager.SyncGpuStrokeToComposite();
            }

            if (_currentMesh != null && MeshHierarchy != null)
            {
                MeshHierarchy.MarkSubmeshDirty(_currentMesh);
            }

            EmitSignal(SignalName.StrokeFinished);
        }

        public void OnTabDeactivated()
        {
            IsPaintingActive = false;
            if (_isMouseDown || _strokeInProgress)
            {
                FinishStroke();
            }
            _isMouseDown = false;
            _strokeInProgress = false;
            _isActionClickDown = false;
            if (_isDraggingShape3D)
            {
                _isDraggingShape3D = false;
                _shapeTool.CancelShape();
                _layerManager?.RecompositeGpuLayers();
                _selectionOverlay3D?.QueueRedraw();
            }

            if (_cursorGizmo != null)
            {
                _cursorGizmo.Visible = false;
            }
            if (_selectionOutlineMesh != null)
            {
                _selectionOutlineMesh.Visible = false;
            }
            if (_previewDecalNode != null)
            {
                _previewDecalNode.Visible = false;
            }
            if (_cameraBrush != null && GodotObject.IsInstanceValid(_cameraBrush))
            {
                _cameraBrush.Set("drawing", false);
            }
            if (_mirrorCameraBrush != null && GodotObject.IsInstanceValid(_mirrorCameraBrush))
            {
                _mirrorCameraBrush.Set("drawing", false);
            }

            _magicWandTool?.ClearMask();
        }

        /// <summary>
        /// Cancels any in-progress stroke and invalidates cached stroke data and selection masks.
        /// Invoked on hero unload/switch to prevent paint artifacts or masks from leaking across characters.
        /// </summary>
        public void ResetSession()
        {
            _isMouseDown = false;
            _strokeInProgress = false;
            _isActionClickDown = false;
            _preStrokeAtlasData = null;
            if (_isDraggingShape3D)
            {
                _isDraggingShape3D = false;
                _shapeTool.CancelShape();
                _layerManager?.RecompositeGpuLayers();
                _selectionOverlay3D?.QueueRedraw();
            }
            if (_cameraBrush != null && GodotObject.IsInstanceValid(_cameraBrush))
            {
                _cameraBrush.Set("drawing", false);
            }
            if (_mirrorCameraBrush != null && GodotObject.IsInstanceValid(_mirrorCameraBrush))
            {
                _mirrorCameraBrush.Set("drawing", false);
            }
            _magicWandTool?.ClearMask();
            SyncSelectionMaskState();
        }

        private void EnsureSelectionOverlay3D()
        {
            if (_selectionOverlay3D != null && GodotObject.IsInstanceValid(_selectionOverlay3D)) return;
            var container = _worldViewportContainer;
            if (container == null)
            {
                container = GetTree()?.Root?.FindChild("SubViewportContainer", true, false) as SubViewportContainer;
            }
            if (container == null) return;

            Control host = container.GetParent() as Control ?? container;
            _selectionOverlay3D = host.GetNodeOrNull<Control>("SelectionOverlay3D");
            if (_selectionOverlay3D == null)
            {
                _selectionOverlay3D = new Control
                {
                    Name = "SelectionOverlay3D",
                    MouseFilter = Control.MouseFilterEnum.Ignore
                };
                _selectionOverlay3D.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                _selectionOverlay3D.Position = container.Position;
                _selectionOverlay3D.Size = container.Size;
                _selectionOverlay3D.Draw += OnDrawSelectionOverlay3D;
                host.AddChild(_selectionOverlay3D);
                host.MoveChild(_selectionOverlay3D, container.GetIndex() + 1);

                container.Connect(Control.SignalName.Resized, Callable.From(() =>
                {
                    if (_selectionOverlay3D != null && GodotObject.IsInstanceValid(_selectionOverlay3D))
                    {
                        _selectionOverlay3D.Position = container.Position;
                        _selectionOverlay3D.Size = container.Size;
                    }
                }));
            }
        }

        private void OnDrawSelectionOverlay3D()
        {
            if (_selectionOverlay3D == null) return;

            if (ToolMode == BrushToolMode.Shape && _isDraggingShape3D)
            {
                Color shapeWireColor = new Color(0.96f, 0.82f, 0.35f, 0.95f);
                Color shapeFillColor = new Color(BrushColor.R, BrushColor.G, BrushColor.B, 0.20f);

                if (_shapeTool.ShapeType == CanvasShapeType.Square)
                {
                    Rect2 selRect = new Rect2(_shapeStartScreenPos, Vector2.Zero).Expand(_shapeCurrentScreenPos);
                    _selectionOverlay3D.DrawRect(selRect, shapeFillColor, filled: true);
                    _selectionOverlay3D.DrawRect(selRect, shapeWireColor, filled: false, width: 2.0f);
                }
                else if (_shapeTool.ShapeType == CanvasShapeType.Circle)
                {
                    Vector2 center = (_shapeStartScreenPos + _shapeCurrentScreenPos) * 0.5f;
                    float radius = _shapeStartScreenPos.DistanceTo(_shapeCurrentScreenPos) * 0.5f;
                    _selectionOverlay3D.DrawCircle(center, radius, shapeFillColor, filled: true);
                    _selectionOverlay3D.DrawCircle(center, radius, shapeWireColor, filled: false, width: 2.0f, antialiased: true);
                }
                else if (_shapeTool.ShapeType == CanvasShapeType.Line)
                {
                    _selectionOverlay3D.DrawLine(_shapeStartScreenPos, _shapeCurrentScreenPos, BrushColor, Mathf.Max(2.0f, _shapeTool.StrokeWidth * 0.5f), antialiased: true);
                    _selectionOverlay3D.DrawCircle(_shapeStartScreenPos, 4.0f, shapeWireColor);
                    _selectionOverlay3D.DrawCircle(_shapeCurrentScreenPos, 4.0f, shapeWireColor);
                }
                return;
            }

            if (ToolMode != BrushToolMode.Selection) return;

            var selType = SelectionMask?.CurrentToolType ?? _brushPalette?.CurrentSelectionType ?? SelectionToolType.Rectangular;
            Color marqueeColor = new Color(0.96f, 0.82f, 0.35f, 0.95f);
            Color marqueeFill = new Color(0.96f, 0.82f, 0.35f, 0.15f);

            if (_isSelecting3D && selType == SelectionToolType.Rectangular)
            {
                Rect2 selRect = new Rect2(_selectionStartScreenPos, Vector2.Zero).Expand(_selectionCurrentScreenPos);
                _selectionOverlay3D.DrawRect(selRect, marqueeFill, filled: true);
                _selectionOverlay3D.DrawRect(selRect, marqueeColor, filled: false, width: 1.5f);
            }
            else if (_isSelecting3D && selType == SelectionToolType.Lasso && _lassoPoints3D.Count >= 2)
            {
                for (int i = 0; i < _lassoPoints3D.Count - 1; i++)
                {
                    _selectionOverlay3D.DrawLine(_lassoPoints3D[i], _lassoPoints3D[i + 1], marqueeColor, 1.5f);
                }
                _selectionOverlay3D.DrawLine(_lassoPoints3D[_lassoPoints3D.Count - 1], _lassoPoints3D[0], new Color(0.96f, 0.82f, 0.35f, 0.5f), 1.0f);
            }
            else if (_isBuildingPoly3D && selType == SelectionToolType.Polygonal && _polyPoints3D.Count >= 1)
            {
                for (int i = 0; i < _polyPoints3D.Count - 1; i++)
                {
                    _selectionOverlay3D.DrawLine(_polyPoints3D[i], _polyPoints3D[i + 1], marqueeColor, 1.5f);
                    _selectionOverlay3D.DrawCircle(_polyPoints3D[i], 3.0f, marqueeColor);
                }
                _selectionOverlay3D.DrawCircle(_polyPoints3D[_polyPoints3D.Count - 1], 3.0f, marqueeColor);
                _selectionOverlay3D.DrawLine(_polyPoints3D[_polyPoints3D.Count - 1], _polyCurrentScreenPos, marqueeColor, 1.0f);

                bool nearStart = _polyCurrentScreenPos.DistanceTo(_polyPoints3D[0]) <= 10.0f;
                _selectionOverlay3D.DrawCircle(_polyPoints3D[0], nearStart ? 6.0f : 4.0f, nearStart ? Colors.White : marqueeColor);
            }
        }

        private SelectionCombineMode Get3DSelectionCombineMode()
        {
            if (Input.IsKeyPressed(Key.Shift)) return SelectionCombineMode.Add;
            if (Input.IsKeyPressed(Key.Alt)) return SelectionCombineMode.Subtract;
            return SelectionCombineMode.Replace;
        }

        private void Apply3DRectSelection()
        {
            float minX = Mathf.Min(_selectionStartScreenPos.X, _selectionCurrentScreenPos.X);
            float minY = Mathf.Min(_selectionStartScreenPos.Y, _selectionCurrentScreenPos.Y);
            float maxX = Mathf.Max(_selectionStartScreenPos.X, _selectionCurrentScreenPos.X);
            float maxY = Mathf.Max(_selectionStartScreenPos.Y, _selectionCurrentScreenPos.Y);
            Rect2 rect = new Rect2(minX, minY, Mathf.Max(maxX - minX, 1.0f), Mathf.Max(maxY - minY, 1.0f));
            if (rect.Size.X < 2f && rect.Size.Y < 2f) return;
            Rasterize3DScreenSelection(rect, null, Get3DSelectionCombineMode());
        }

        private void Apply3DLassoSelection()
        {
            if (_lassoPoints3D.Count < 3)
            {
                _lassoPoints3D.Clear();
                return;
            }
            Rasterize3DScreenSelection(new Rect2(), _lassoPoints3D, Get3DSelectionCombineMode());
            _lassoPoints3D.Clear();
        }

        private void CloseAndApply3DPolygonSelection()
        {
            if (_polyPoints3D.Count >= 3)
            {
                Rasterize3DScreenSelection(new Rect2(), _polyPoints3D, Get3DSelectionCombineMode());
            }
            _isBuildingPoly3D = false;
            _polyPoints3D.Clear();
            _selectionOverlay3D?.QueueRedraw();
        }

        public void Cancel3DSelection()
        {
            _isSelecting3D = false;
            _isBuildingPoly3D = false;
            _lassoPoints3D.Clear();
            _polyPoints3D.Clear();
            _selectionOverlay3D?.QueueRedraw();
        }

        private void Rasterize3DScreenSelection(Rect2 screenRect, IReadOnlyList<Vector2> screenPoly, SelectionCombineMode mode)
        {
            var camera = _worldViewport?.GetCamera3D() ?? _camera;
            if (camera == null || _currentMesh == null) return;

            if (!_raycaster.IsInitialized)
            {
                _raycaster.BuildFromMesh(_currentMesh, -1);
            }
            if (!_raycaster.IsInitialized || _raycaster.TriangleCount == 0) return;

            var selMask = _magicWandTool?.SelectionMask;
            if (selMask == null) return;
            int atlasSize = _layerManager?.CanvasSize.X ?? (selMask.CanvasSize > 0 ? selMask.CanvasSize : 2048);
            selMask.EnsureSize(atlasSize);

            byte[] maskBuf = selMask.Buffer;
            if (maskBuf == null || maskBuf.Length != atlasSize * atlasSize)
            {
                selMask.EnsureBuffer(atlasSize);
                maskBuf = selMask.Buffer;
            }

            if (mode == SelectionCombineMode.Replace)
            {
                Array.Clear(maskBuf, 0, maskBuf.Length);
            }

            bool isPoly = (screenPoly != null && screenPoly.Count >= 3);
            Vector2[] polyArr = isPoly ? new List<Vector2>(screenPoly).ToArray() : null;

            Rect2 marqueeBounds;
            if (isPoly)
            {
                float pMinX = float.MaxValue, pMaxX = float.MinValue, pMinY = float.MaxValue, pMaxY = float.MinValue;
                for (int i = 0; i < polyArr.Length; i++)
                {
                    if (polyArr[i].X < pMinX) pMinX = polyArr[i].X;
                    if (polyArr[i].X > pMaxX) pMaxX = polyArr[i].X;
                    if (polyArr[i].Y < pMinY) pMinY = polyArr[i].Y;
                    if (polyArr[i].Y > pMaxY) pMaxY = polyArr[i].Y;
                }
                marqueeBounds = new Rect2(pMinX, pMinY, Mathf.Max(pMaxX - pMinX, 1.0f), Mathf.Max(pMaxY - pMinY, 1.0f));
            }
            else
            {
                marqueeBounds = screenRect;
            }

            Vector2 submeshPos = Vector2.Zero;
            Vector2 submeshSize = Vector2.One;
            if (_currentMesh.MaterialOverlay is ShaderMaterial sm)
            {
                var pVar = sm.GetShaderParameter("position_in_atlas");
                var sVar = sm.GetShaderParameter("size_in_atlas");
                if (pVar.VariantType == Variant.Type.Vector2 && sVar.VariantType == Variant.Type.Vector2)
                {
                    submeshPos = pVar.AsVector2();
                    submeshSize = sVar.AsVector2();
                }
            }

            Transform3D globalTransform = _currentMesh.GlobalTransform;
            int triCount = _raycaster.TriangleCount;

            var candidateTris = new List<(Vector2 s0, Vector2 s1, Vector2 s2, Vector2 u0, Vector2 u1, Vector2 u2, int uvMinX, int uvMaxX, int uvMinY, int uvMaxY, float invDenom, bool allVerticesInside)>();

            for (int t = 0; t < triCount; t++)
            {
                var tri = _raycaster.GetTriangle(t);

                // Scope to currently active surface index
                if (_currentSurfaceIndex >= 0 && tri.SurfaceIndex != _currentSurfaceIndex)
                    continue;

                // 1. Transform to world space
                Vector3 w0 = globalTransform * tri.V0;
                Vector3 w1 = globalTransform * tri.V1;
                Vector3 w2 = globalTransform * tri.V2;

                // 2. Behind camera check
                if (camera.IsPositionBehind(w0) && camera.IsPositionBehind(w1) && camera.IsPositionBehind(w2)) continue;

                // 3. Project to screen
                Vector2 s0 = camera.UnprojectPosition(w0);
                Vector2 s1 = camera.UnprojectPosition(w1);
                Vector2 s2 = camera.UnprojectPosition(w2);

                float sMinX = Mathf.Min(s0.X, Mathf.Min(s1.X, s2.X));
                float sMaxX = Mathf.Max(s0.X, Mathf.Max(s1.X, s2.X));
                float sMinY = Mathf.Min(s0.Y, Mathf.Min(s1.Y, s2.Y));
                float sMaxY = Mathf.Max(s0.Y, Mathf.Max(s1.Y, s2.Y));
                Rect2 triScreenBounds = new Rect2(sMinX, sMinY, Mathf.Max(sMaxX - sMinX, 0.001f), Mathf.Max(sMaxY - sMinY, 0.001f));

                // EARLY REJECTION: Discard triangles outside screen marquee immediately
                if (!marqueeBounds.Intersects(triScreenBounds)) continue;

                // 4. Front Faces Only & Depth Occlusion (only on triangles touching marquee)
                if (FrontFacesOnly)
                {
                    Vector3 worldNormal = (globalTransform.Basis * tri.Normal).Normalized();
                    Vector3 triCenter = (w0 + w1 + w2) / 3.0f;
                    Vector3 viewDir = (triCenter - camera.GlobalPosition).Normalized();
                    if (worldNormal.Dot(viewDir) >= 0.0f)
                    {
                        continue; // Back-facing
                    }

                    Vector3 rayOrigin = camera.GlobalPosition;
                    Vector3 toTri = triCenter - rayOrigin;
                    float distToTri = toTri.Length();
                    if (distToTri > 1e-4f)
                    {
                        Vector3 rayDir = toTri / distToTri;
                        var occHit = _raycaster.IntersectRay(_currentMesh, rayOrigin, rayDir, cullBackfaces: true);
                        if (occHit.Hit && occHit.Distance < distToTri - 0.02f)
                        {
                            continue; // Occluded
                        }
                    }
                }

                // 5. UV triangle in atlas pixels
                Vector2 u0 = (submeshPos + tri.UV0 * submeshSize) * atlasSize;
                Vector2 u1 = (submeshPos + tri.UV1 * submeshSize) * atlasSize;
                Vector2 u2 = (submeshPos + tri.UV2 * submeshSize) * atlasSize;

                int uvMinX = Math.Clamp((int)MathF.Floor(Mathf.Min(u0.X, Mathf.Min(u1.X, u2.X))), 0, atlasSize - 1);
                int uvMaxX = Math.Clamp((int)MathF.Ceiling(Mathf.Max(u0.X, Mathf.Max(u1.X, u2.X))), 0, atlasSize - 1);
                int uvMinY = Math.Clamp((int)MathF.Floor(Mathf.Min(u0.Y, Mathf.Min(u1.Y, u2.Y))), 0, atlasSize - 1);
                int uvMaxY = Math.Clamp((int)MathF.Ceiling(Mathf.Max(u0.Y, Mathf.Max(u1.Y, u2.Y))), 0, atlasSize - 1);

                float denom = (u1.Y - u2.Y) * (u0.X - u2.X) + (u2.X - u1.X) * (u0.Y - u2.Y);
                if (MathF.Abs(denom) < 1e-6f) continue;
                float invDenom = 1.0f / denom;

                bool allVerticesInside = isPoly
                    ? (Geometry2D.IsPointInPolygon(s0, polyArr) && Geometry2D.IsPointInPolygon(s1, polyArr) && Geometry2D.IsPointInPolygon(s2, polyArr))
                    : (marqueeBounds.HasPoint(s0) && marqueeBounds.HasPoint(s1) && marqueeBounds.HasPoint(s2));

                candidateTris.Add((s0, s1, s2, u0, u1, u2, uvMinX, uvMaxX, uvMinY, uvMaxY, invDenom, allVerticesInside));
            }

            byte fillByte = (mode == SelectionCombineMode.Subtract) ? (byte)0 : (byte)255;
            System.Threading.Tasks.Parallel.ForEach(candidateTris, cTri =>
            {
                for (int py = cTri.uvMinY; py <= cTri.uvMaxY; py++)
                {
                    float uvY = py + 0.5f;
                    int row = py * atlasSize;
                    for (int px = cTri.uvMinX; px <= cTri.uvMaxX; px++)
                    {
                        float uvX = px + 0.5f;
                        float wA = ((cTri.u1.Y - cTri.u2.Y) * (uvX - cTri.u2.X) + (cTri.u2.X - cTri.u1.X) * (uvY - cTri.u2.Y)) * cTri.invDenom;
                        float wB = ((cTri.u2.Y - cTri.u0.Y) * (uvX - cTri.u2.X) + (cTri.u0.X - cTri.u2.X) * (uvY - cTri.u2.Y)) * cTri.invDenom;
                        float wC = 1.0f - wA - wB;

                        if (wA >= -0.02f && wB >= -0.02f && wC >= -0.02f)
                        {
                            bool inside = cTri.allVerticesInside;
                            if (!inside)
                            {
                                Vector2 screenPos = wA * cTri.s0 + wB * cTri.s1 + wC * cTri.s2;
                                inside = isPoly
                                    ? Geometry2D.IsPointInPolygon(screenPos, polyArr)
                                    : marqueeBounds.HasPoint(screenPos);
                            }

                            if (inside)
                            {
                                maskBuf[row + px] = fillByte;
                            }
                        }
                    }
                }
            });

            selMask.UseSelectionMask = true;
            selMask.UpdateSelectionStateAndUpload();
            SyncSelectionMaskState();
            _brushPalette?.UpdateWandUI();
            GD.Print($"[MeshPainter3D] Rasterized 3D selection marquee (mode={mode}, candidateTriangles={candidateTris.Count})");
        }

        [Obsolete("CPU raycasting paint strokes have been deprecated in favor of CameraBrush GPU compute projection.")]
        public bool ExecutePaintStroke(RaycastHitResult hit)
        {
            if (ToolMode == BrushToolMode.Eyedropper)
            {
                SampleColorAtUV(hit.HitUV, hit.HitSurfaceIndex);
                return true;
            }
            return true;
        }

        [Obsolete("CPU raycasting paint strokes have been deprecated in favor of CameraBrush GPU compute projection.")]
        public bool ProcessStrokeAtScreenPosition(Vector2 localPos)
        {
            return true;
        }

        public void ExecuteMagicWandSelection(RaycastHitResult hit, MagicWandCombineMode combineMode = MagicWandCombineMode.Replace)
        {
            if (_currentMesh == null || _layerManager == null || _magicWandTool == null) return;
            int atlasSize = _layerManager.CanvasSize.X > 0 ? _layerManager.CanvasSize.X : 2048;
            int seedX = Math.Clamp((int)(hit.HitUV.X * atlasSize), 0, atlasSize - 1);
            int seedY = Math.Clamp((int)(hit.HitUV.Y * atlasSize), 0, atlasSize - 1);
            ExecuteMagicWandAtPixel(seedX, seedY, combineMode);
        }

        public void ExecuteMagicWandSelectionAtlasPx(Vector2 atlasPx, MagicWandCombineMode combineMode = MagicWandCombineMode.Replace)
        {
            if (_layerManager == null || _magicWandTool == null) return;
            int atlasSize = _layerManager.CanvasSize.X > 0 ? _layerManager.CanvasSize.X : 2048;
            int seedX = Math.Clamp((int)atlasPx.X, 0, atlasSize - 1);
            int seedY = Math.Clamp((int)atlasPx.Y, 0, atlasSize - 1);
            ExecuteMagicWandAtPixel(seedX, seedY, combineMode);
        }

        private void ExecuteMagicWandAtPixel(int seedX, int seedY, MagicWandCombineMode combineMode)
        {
            if (_layerManager == null || _magicWandTool == null) return;

            _layerManager.EnsureCpuSynced();
            if (_layerManager.BaseAtlasBuffer == null)
            {
                _layerManager.RebuildBaseAtlasBuffer();
            }

            if (_magicWandTool.GenerateMaskFromAtlas(seedX, seedY, _layerManager, combineMode))
            {
                SyncSelectionMaskState();
                _brushPalette?.UpdateWandUI();
            }
        }

        public void SyncSelectionMaskState()
        {
            if (_magicWandTool == null) return;

            bool hasSelection = _magicWandTool.HasSelection;
            bool showPattern = _magicWandTool.UseSelectionMask;
            Rid maskRid = new Rid();

            if (hasSelection)
            {
                var rd = RenderingServer.GetRenderingDevice();
                if (rd != null)
                {
                    _magicWandTool.SelectionMask?.EnsureMaskTexture(rd, _magicWandTool.CurrentAtlasSize);
                }
                var candRid = _magicWandTool.SelectionMaskRid;
                if (candRid.IsValid && rd != null && rd.TextureIsValid(candRid))
                {
                    maskRid = candRid;
                }
            }

            if (_cameraBrush != null && GodotObject.IsInstanceValid(_cameraBrush))
            {
                _cameraBrush.Set("selection_mask_rid", maskRid);
                _cameraBrush.Set("use_selection_mask", hasSelection);
                _cameraBrush.Call("get_atlas_textures");
            }
            if (_mirrorCameraBrush != null && GodotObject.IsInstanceValid(_mirrorCameraBrush))
            {
                _mirrorCameraBrush.Set("selection_mask_rid", maskRid);
                _mirrorCameraBrush.Set("use_selection_mask", hasSelection);
                _mirrorCameraBrush.Call("get_atlas_textures");
            }

            if (_layerManager != null)
            {
                _layerManager.SetSelectionMaskOverlay(hasSelection ? _magicWandTool.MaskTextureResource : null, hasSelection, showPattern);
            }

            _brushPalette?.UpdateWandUI();

            var uvCanvas = GetTree()?.Root?.FindChild("UVCanvas2D", true, false) as UVCanvas2DUI
                        ?? GetTree()?.Root?.FindChild("UVCanvasPanel", true, false) as UVCanvas2DUI;
            uvCanvas?.QueueCanvasRedraw();
        }

        private void SampleColorAtUV(Vector2 uv, int surfaceIndex)
        {
            var img = _layerManager.BakeCompositeImage(surfaceIndex);
            if (img == null) return;

            int px = Mathf.Clamp((int)(uv.X * img.GetWidth()), 0, img.GetWidth() - 1);
            int py = Mathf.Clamp((int)(uv.Y * img.GetHeight()), 0, img.GetHeight() - 1);

            Color sampled = img.GetPixel(px, py);
            BrushColor = sampled;
            EmitSignal(SignalName.ColorSampled, sampled);
            GD.Print($"[MeshPainter3D] Sampled color at ({px}, {py}): {sampled.ToHtml()}");
        }

        public Texture2D GetBrushTexture(BrushShapeType type)
        {
            if (_brushTextures.TryGetValue(type, out var tex)) return tex;
            return null;
        }

        private void GenerateProceduralBrushTextures()
        {
            int texSize = 256;

            // 1. Soft Circle (Respects BrushHardness with smooth Hermite falloff)
            {
                var img = Image.CreateEmpty(texSize, texSize, false, Image.Format.Rgba8);
                Vector2 center = new Vector2(texSize * 0.5f, texSize * 0.5f);
                float radius = texSize * 0.5f - 1.0f;
                float hClamped = Mathf.Clamp(BrushHardness, 0.0f, 0.99f);
                float inner = radius * hClamped;

                for (int y = 0; y < texSize; y++)
                {
                    for (int x = 0; x < texSize; x++)
                    {
                        float d = (new Vector2(x + 0.5f, y + 0.5f) - center).Length();
                        float alpha;
                        if (d <= inner)
                        {
                            alpha = 1.0f;
                        }
                        else if (d < radius)
                        {
                            float t = (d - inner) / (radius - inner);
                            float s = Mathf.Clamp(1.0f - t, 0.0f, 1.0f);
                            alpha = s * s * (3.0f - 2.0f * s);
                        }
                        else
                        {
                            alpha = 0.0f;
                        }
                        img.SetPixel(x, y, new Color(1, 1, 1, alpha));
                    }
                }
                _brushTextures[BrushShapeType.SoftCircle] = ImageTexture.CreateFromImage(img);
            }

            // 2. Hard Circle (Strict 1px anti-aliased edge)
            {
                var img = Image.CreateEmpty(texSize, texSize, false, Image.Format.Rgba8);
                Vector2 center = new Vector2(texSize * 0.5f, texSize * 0.5f);
                float radius = texSize * 0.5f - 1.5f;

                for (int y = 0; y < texSize; y++)
                {
                    for (int x = 0; x < texSize; x++)
                    {
                        float d = (new Vector2(x + 0.5f, y + 0.5f) - center).Length();
                        float alpha = d <= radius ? 1.0f : Mathf.Clamp(1.0f - (d - radius), 0.0f, 1.0f);
                        img.SetPixel(x, y, new Color(1, 1, 1, alpha));
                    }
                }
                _brushTextures[BrushShapeType.HardCircle] = ImageTexture.CreateFromImage(img);
            }

            // 3. Splatter (Multi-dot distribution)
            {
                var img = Image.CreateEmpty(texSize, texSize, false, Image.Format.Rgba8);
                img.Fill(Colors.Transparent);
                var rng = new RandomNumberGenerator();
                rng.Seed = 1337;

                int dotCount = 85;
                for (int i = 0; i < dotCount; i++)
                {
                    float angle = rng.RandfRange(0, Mathf.Tau);
                    float dist = rng.RandfRange(0, texSize * 0.44f);
                    int cx = (int)(texSize * 0.5f + Mathf.Cos(angle) * dist);
                    int cy = (int)(texSize * 0.5f + Mathf.Sin(angle) * dist);
                    int r = rng.RandiRange(3, 11);

                    for (int dy = -r; dy <= r; dy++)
                    {
                        for (int dx = -r; dx <= r; dx++)
                        {
                            int px = cx + dx;
                            int py = cy + dy;
                            if (px >= 0 && px < texSize && py >= 0 && py < texSize)
                            {
                                if (dx * dx + dy * dy <= r * r)
                                {
                                    img.SetPixel(px, py, Colors.White);
                                }
                            }
                        }
                    }
                }
                _brushTextures[BrushShapeType.Splatter] = ImageTexture.CreateFromImage(img);
            }

            // 4. Grunge / Rough (Procedural Noise)
            {
                var img = Image.CreateEmpty(texSize, texSize, false, Image.Format.Rgba8);
                var rng = new RandomNumberGenerator();
                rng.Seed = 42;
                Vector2 center = new Vector2(texSize * 0.5f, texSize * 0.5f);
                float radius = texSize * 0.5f;

                for (int y = 0; y < texSize; y++)
                {
                    for (int x = 0; x < texSize; x++)
                    {
                        float d = (new Vector2(x + 0.5f, y + 0.5f) - center).Length() / radius;
                        float falloff = Mathf.Clamp(1.0f - d, 0.0f, 1.0f);
                        float noise = rng.RandfRange(0.2f, 1.0f);
                        float alpha = falloff * noise;
                        img.SetPixel(x, y, new Color(1, 1, 1, alpha));
                    }
                }
                _brushTextures[BrushShapeType.Grunge] = ImageTexture.CreateFromImage(img);
            }

            // 5. Square (Crisp white box)
            {
                var img = Image.CreateEmpty(texSize, texSize, false, Image.Format.Rgba8);
                img.Fill(Colors.White);
                _brushTextures[BrushShapeType.Square] = ImageTexture.CreateFromImage(img);
            }
        }

        public override void _ExitTree()
        {
            GizmoDisplaySettings.OnSettingsChanged -= ApplyOutlineSettings;
            if (_cursorGizmo != null && GodotObject.IsInstanceValid(_cursorGizmo))
            {
                _cursorGizmo.QueueFree();
                _cursorGizmo = null;
            }
            if (_selectionOutlineMesh != null && GodotObject.IsInstanceValid(_selectionOutlineMesh))
            {
                _selectionOutlineMesh.QueueFree();
                _selectionOutlineMesh = null;
            }
            if (_mirrorCameraBrush != null && GodotObject.IsInstanceValid(_mirrorCameraBrush))
            {
                _mirrorCameraBrush.QueueFree();
                _mirrorCameraBrush = null;
            }
            base._ExitTree();
        }
    }
}
