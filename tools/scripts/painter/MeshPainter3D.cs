using Godot;
using System;
using System.Collections.Generic;

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
        MagicWand = 7
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
        [Signal] public delegate void StrokeStartedEventHandler();
        [Signal] public delegate void StrokeFinishedEventHandler();

        [Export] public bool IsPaintingActive { get; set; } = true;

        // Brush parameters
        public Color BrushColor { get; set; } = new Color(0.85f, 0.15f, 0.2f, 1.0f);
        public float BrushSize { get; set; } = 32.0f;
        public float BrushHardness { get; set; } = 0.5f;
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

        // Submesh Selection Outline Gizmo
        private MeshInstance3D _selectionOutlineMesh;
        private ShaderMaterial _outlineMaskMat;
        private ShaderMaterial _outlineEdgeMat;

        private Camera3D _camera;
        private SubViewport _worldViewport;
        private SubViewportContainer _worldViewportContainer;
        private SkinLayerManager _layerManager;
        private FloatingBrushPaletteUI _brushPalette;
        private readonly MeshRaycaster _raycaster = new();
        private MeshInstance3D _currentMesh;
        public MeshInstance3D CurrentMesh => _currentMesh;

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

        public bool IsMirroringEnabled { get; set; } = false;
        private Node3D _mirrorCameraBrush;

        public HeroMeshHierarchy MeshHierarchy { get; set; }

        public bool IsMouseOverUI()
        {
            if (_brushPalette != null && GodotObject.IsInstanceValid(_brushPalette) && _brushPalette.Visible)
            {
                Vector2 globalMouse = GetViewport().GetMousePosition();
                if (_brushPalette.GetGlobalRect().HasPoint(globalMouse))
                {
                    return true;
                }
            }
            return false;
        }

        public bool RaycastAllSubmeshes(Vector3 rayOrigin, Vector3 rayDir, out SubmeshNodeInfo hitSubmesh, out RaycastHitResult hitResult)
        {
            hitSubmesh = null;
            hitResult = new RaycastHitResult { Hit = false, Distance = float.MaxValue };

            if (MeshHierarchy == null || MeshHierarchy.Submeshes == null) return false;

            float closestDist = float.MaxValue;

            foreach (var sub in MeshHierarchy.Submeshes)
            {
                if (sub.Mesh == null || !sub.Mesh.Visible || !GodotObject.IsInstanceValid(sub.Mesh)) continue;

                // Lazily build and cache the raycaster for each submesh once
                if (sub.Raycaster == null)
                {
                    sub.Raycaster = new MeshRaycaster();
                    sub.Raycaster.BuildFromMesh(sub.Mesh, sub.SurfaceIndex);
                }

                if (!sub.Raycaster.IsInitialized) continue;

                // IntersectRay performs an early-exit AABB bound check first;
                // submeshes not under the ray return instantly in ~0.001ms without iterating triangles.
                var hit = sub.Raycaster.IntersectRay(sub.Mesh, rayOrigin, rayDir, cullBackfaces: false);
                if (hit.Hit && hit.Distance < closestDist)
                {
                    closestDist = hit.Distance;
                    hitResult = hit;
                    hitSubmesh = sub;
                }
            }

            return hitSubmesh != null;
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
            _cameraBrush.Set("resolution", new Vector2I(2048, 2048));
            _cameraBrush.Set("max_distance", 0.35f);
            _cameraBrush.Set("draw_speed", 100.0f);
            _cameraBrush.Set("drawing", false);

            var targetParent = _worldViewport as Node ?? _camera as Node ?? this;
            targetParent.AddChild(_cameraBrush);
            AttachCameraBrushWorld();

            if (IsMirroringEnabled && (_mirrorCameraBrush == null || !GodotObject.IsInstanceValid(_mirrorCameraBrush)))
            {
                _mirrorCameraBrush = (Node3D)brushScript.New();
                _mirrorCameraBrush.Name = "MirrorCameraBrush";
                _mirrorCameraBrush.Set("projection", 1);
                _mirrorCameraBrush.Set("resolution", new Vector2I(2048, 2048));
                _mirrorCameraBrush.Set("max_distance", 0.35f);
                _mirrorCameraBrush.Set("draw_speed", 100.0f);
                _mirrorCameraBrush.Set("drawing", false);
                targetParent.AddChild(_mirrorCameraBrush);
                AttachMirrorCameraBrushWorld();
            }

            SyncCameraBrushProperties(force: true);
            GD.Print("[MeshPainter3D] Initialized and added CameraBrush to scene!");
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
                float speed = Mathf.Max(10.0f, BrushFlow * 100.0f);
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

            int minBleed = 1;
            int maxBleed = 1;
            Vector2I brushRes = (atlasRes >= 2048) ? new Vector2I(512, 512) : new Vector2I(256, 256);

            _cameraBrush.Set("min_bleed", minBleed);
            _cameraBrush.Set("max_bleed", maxBleed);
            _cameraBrush.Set("resolution", brushRes);

            bool useMask = _magicWandTool != null && _magicWandTool.HasSelection && _magicWandTool.UseSelectionMask;
            _cameraBrush.Set("use_selection_mask", useMask);
            if (useMask && _magicWandTool.SelectionMaskRid.IsValid)
            {
                _cameraBrush.Set("selection_mask_rid", _magicWandTool.SelectionMaskRid);
            }

            if (_mirrorCameraBrush != null && GodotObject.IsInstanceValid(_mirrorCameraBrush))
            {
                _mirrorCameraBrush.Set("min_bleed", minBleed);
                _mirrorCameraBrush.Set("max_bleed", maxBleed);
                _mirrorCameraBrush.Set("resolution", brushRes);
                _mirrorCameraBrush.Set("use_selection_mask", useMask);
                if (useMask && _magicWandTool.SelectionMaskRid.IsValid)
                {
                    _mirrorCameraBrush.Set("selection_mask_rid", _magicWandTool.SelectionMaskRid);
                }
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

            if (IsMouseOverUI())
            {
                if (_cursorGizmo != null) _cursorGizmo.Visible = false;
                if (_previewDecalNode != null) _previewDecalNode.Visible = false;
                if (_isMouseDown) FinishStroke();
                return;
            }

            UpdateBrushCursor();
            UpdateSelectionOutline();

            var camera = _worldViewport?.GetCamera3D() ?? _camera;
            bool isLeftDown = Input.IsMouseButtonPressed(MouseButton.Left);

            bool canPaintLayer = _layerManager?.ActiveLayer != null && !_layerManager.ActiveLayer.IsLocked && _layerManager.ActiveLayer.IsVisible;
            bool isPainting = IsPaintingActive && canPaintLayer && isLeftDown && _lastHit.Hit 
                && ToolMode != BrushToolMode.Eyedropper 
                && ToolMode != BrushToolMode.BucketFill 
                && ToolMode != BrushToolMode.Decal 
                && ToolMode != BrushToolMode.Text 
                && ToolMode != BrushToolMode.SelectSubmesh
                && ToolMode != BrushToolMode.MagicWand;

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
                        var hit = (_currentMesh != null) ? _raycaster.IntersectRay(_currentMesh, origin, dir, cullBackfaces: false) : default;
                        if (hit.Hit)
                        {
                            _layerManager?.FillSubmesh(_currentMesh, BrushColor, hit.HitUV, _magicWandTool);
                        }
                        else if (RaycastAllSubmeshes(origin, dir, out var otherSub, out var otherHit))
                        {
                            MeshHierarchy?.SelectTarget(otherSub, otherSub.SurfaceIndex);
                            _layerManager?.FillSubmesh(otherSub.Mesh, BrushColor, otherHit.HitUV, _magicWandTool);
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

                        var hit = (_currentMesh != null) ? _raycaster.IntersectRay(_currentMesh, origin, dir, cullBackfaces: false) : default;
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
                        var hit = (_currentMesh != null) ? _raycaster.IntersectRay(_currentMesh, origin, dir, cullBackfaces: false) : default;
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
                        _decalStamper.PlaceAt(_lastHit.HitPositionWorld, _lastHit.HitNormal, _lastHit.HitUV);
                        _decalStamper.BakeToActiveLayer();

                        if (IsMirroringEnabled)
                        {
                            Vector3 mirrorWorldPos = new Vector3(-_lastHit.HitPositionWorld.X, _lastHit.HitPositionWorld.Y, _lastHit.HitPositionWorld.Z);
                            Vector3 mirrorNormal = new Vector3(-_lastHit.HitNormal.X, _lastHit.HitNormal.Y, _lastHit.HitNormal.Z);
                            var mirrorHit = _raycaster.IntersectRay(_currentMesh, mirrorWorldPos + mirrorNormal * 0.1f, -mirrorNormal, cullBackfaces: false);
                            if (mirrorHit.Hit)
                            {
                                _decalStamper.PlaceAt(mirrorHit.HitPositionWorld, mirrorHit.HitNormal, mirrorHit.HitUV);
                                _decalStamper.BakeToActiveLayer();
                            }
                        }
                    }
                }
                else if (ToolMode == BrushToolMode.Text && _lastHit.Hit)
                {
                    if (_textProjector != null)
                    {
                        _textProjector.PlaceAt(_lastHit.HitPositionWorld, _lastHit.HitNormal, _lastHit.HitUV);
                        _textProjector.BakeToActiveLayer();

                        if (IsMirroringEnabled)
                        {
                            Vector3 mirrorWorldPos = new Vector3(-_lastHit.HitPositionWorld.X, _lastHit.HitPositionWorld.Y, _lastHit.HitPositionWorld.Z);
                            Vector3 mirrorNormal = new Vector3(-_lastHit.HitNormal.X, _lastHit.HitNormal.Y, _lastHit.HitNormal.Z);
                            var mirrorHit = _raycaster.IntersectRay(_currentMesh, mirrorWorldPos + mirrorNormal * 0.1f, -mirrorNormal, cullBackfaces: false);
                            if (mirrorHit.Hit)
                            {
                                _textProjector.PlaceAt(mirrorHit.HitPositionWorld, mirrorHit.HitNormal, mirrorHit.HitUV);
                                _textProjector.BakeToActiveLayer();
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
                    Vector3 brushPos = _lastHit.HitPositionWorld - rayDir * 0.15f;
                    _cameraBrush.GlobalPosition = brushPos;
                    Vector3 up = Mathf.Abs(rayDir.Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
                    _cameraBrush.LookAt(brushPos + rayDir, up);
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
                        Vector3 mirrorPos = new Vector3(-_lastHit.HitPositionWorld.X, _lastHit.HitPositionWorld.Y, _lastHit.HitPositionWorld.Z);
                        Vector3 mirrorRayDir = new Vector3(-rayDir.X, rayDir.Y, rayDir.Z).Normalized();
                        Vector3 mirrorBrushPos = mirrorPos - mirrorRayDir * 0.15f;
                        _mirrorCameraBrush.GlobalPosition = mirrorBrushPos;
                        Vector3 up = Mathf.Abs(mirrorRayDir.Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
                        _mirrorCameraBrush.LookAt(mirrorBrushPos + mirrorRayDir, up);
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
                    _layerManager?.RecordInitialSnapshot();
                    _preStrokeAtlasData = _layerManager?.GetAtlasDataSnapshot();
                    _cameraBrush?.Call("get_atlas_textures");
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
                    Size = new Vector3(0.5f, 0.6f, 0.5f),
                    AlbedoMix = 0.85f,
                    CullMask = 1,
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

            if (ToolMode == BrushToolMode.SelectSubmesh || ToolMode == BrushToolMode.MagicWand || ToolMode == BrushToolMode.BucketFill || ToolMode == BrushToolMode.Eyedropper)
            {
                var curHit = (_currentMesh != null)
                    ? _raycaster.IntersectRay(_currentMesh, rayOrigin, rayDir, cullBackfaces: false)
                    : default;
                if (curHit.Hit)
                {
                    _lastHit = curHit;
                }
                else if (RaycastAllSubmeshes(rayOrigin, rayDir, out var hoverSub, out var allHit))
                {
                    _lastHit = allHit;
                }
                else
                {
                    _lastHit = new RaycastHitResult { Hit = false };
                }
            }
            else
            {
                _lastHit = (_currentMesh != null)
                    ? _raycaster.IntersectRay(_currentMesh, rayOrigin, rayDir, cullBackfaces: false)
                    : new RaycastHitResult { Hit = false };
            }

            var hit = _lastHit;

            if (_debugFrameCount++ % 30 == 0)
            {
                GD.Print($"[BrushDebug] Active: {IsPaintingActive} | InsideTree: {_cursorGizmo.IsInsideTree()} | Target: {_currentMesh?.Name} | MousePos: {localMouse} | Hit: {hit.Hit} | HitPos: {hit.HitPositionWorld}");
            }

            if (hit.Hit)
            {
                if (ToolMode == BrushToolMode.MagicWand)
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
                        _previewDecalNode.Size = new Vector3(sizeX, 0.6f, sizeZ);

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
                        _previewDecalNode.Size = new Vector3(sizeX, 0.6f, sizeZ);

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
        }

        public void SetTargetMesh(MeshInstance3D mesh, int surfaceIndex = 0)
        {
            _currentMesh = mesh;

            if (_currentMesh != null)
            {
                _raycaster.BuildFromMesh(_currentMesh, surfaceIndex);
                _layerManager?.SetupForMesh(_currentMesh, surfaceIndex);
                _layerManager?.SetPaintTargetMesh(_currentMesh);
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
            if (_currentMesh == null && ToolMode != BrushToolMode.SelectSubmesh && !(@event is InputEventMouseButton mb && mb.AltPressed)) return;

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
                        if (RaycastAllSubmeshes(origin, dir, out var clickedSub, out _))
                        {
                            MeshHierarchy?.SelectTarget(clickedSub, clickedSub.SurfaceIndex);
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
                        var hit = _raycaster.IntersectRay(_currentMesh, camera.ProjectRayOrigin(pos), camera.ProjectRayNormal(pos), cullBackfaces: false);
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
                        var hit = _currentMesh != null ? _raycaster.IntersectRay(_currentMesh, origin, dir, cullBackfaces: false) : default;
                        if (hit.Hit)
                        {
                            _layerManager?.FillSubmesh(_currentMesh, BrushColor, hit.HitUV, _magicWandTool);
                            GetViewport()?.SetInputAsHandled();
                        }
                        else if (RaycastAllSubmeshes(origin, dir, out var otherSub, out var otherHit))
                        {
                            MeshHierarchy?.SelectTarget(otherSub, otherSub.SurfaceIndex);
                            _layerManager?.FillSubmesh(otherSub.Mesh, BrushColor, otherHit.HitUV, _magicWandTool);
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

                        var hit = _raycaster.IntersectRay(_currentMesh, origin, dir, cullBackfaces: false);
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
            if (@event is InputEventMouseButton mouseBtn && mouseBtn.ButtonIndex == MouseButton.Left && !mouseBtn.Pressed)
            {
                FinishStroke();
            }
        }

        public void FinishStroke()
        {
            if (!_isMouseDown && !_strokeInProgress) return;
            _isMouseDown = false;
            _strokeInProgress = false;
            _isActionClickDown = false;
            byte[] postData = _layerManager?.GetAtlasDataSnapshot();
            bool isErase = ToolMode == BrushToolMode.Erase;
            _layerManager?.CommitStrokeToActiveLayer(_preStrokeAtlasData, postData, BrushColor, isErase);
            _layerManager?.RecompositeGpuLayers();
            _layerManager?.RecordUndoSnapshot();
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

            // Ensure base atlas buffer is populated with submesh textures
            _layerManager.RebuildBaseAtlasBuffer();

            Texture2D baseTex = null;
            int sCount = _currentMesh.Mesh != null ? _currentMesh.Mesh.GetSurfaceCount() : 1;
            int surfaceIdx = Mathf.Clamp(hit.HitSurfaceIndex, 0, sCount - 1);

            var origMat = HeroMeshHierarchy.GetAuthenticMaterial(_currentMesh, surfaceIdx)
                       ?? _currentMesh.GetSurfaceOverrideMaterial(surfaceIdx)
                       ?? (_currentMesh.Mesh != null ? _currentMesh.Mesh.SurfaceGetMaterial(surfaceIdx) : null)
                       ?? _currentMesh.MaterialOverride;

            if (origMat != null)
            {
                baseTex = SkinLayerManager.ExtractBaseTexture(origMat);
            }

            Rect2 submeshRect = new Rect2(0, 0, 1, 1);
            var overlayMat = _currentMesh.MaterialOverlay as ShaderMaterial;
            if (overlayMat != null)
            {
                var posVal = overlayMat.GetShaderParameter("position_in_atlas");
                var szVal = overlayMat.GetShaderParameter("size_in_atlas");
                if (posVal.VariantType == Variant.Type.Vector2 && szVal.VariantType == Variant.Type.Vector2)
                {
                    submeshRect = new Rect2(posVal.AsVector2(), szVal.AsVector2());
                }
                if (baseTex == null)
                {
                    var gCol = overlayMat.GetShaderParameter("g_tColor");
                    if (gCol.VariantType == Variant.Type.Object && gCol.AsGodotObject() is Texture2D t2d)
                    {
                        baseTex = t2d;
                    }
                }
            }

            Image img = null;
            if (baseTex != null)
            {
                img = baseTex.GetImage();
                if (img != null)
                {
                    if (img.IsCompressed()) img.Decompress();
                    if (img.GetFormat() != Image.Format.Rgba8) img.Convert(Image.Format.Rgba8);
                }
            }

            if (img == null)
            {
                img = Image.CreateEmpty(512, 512, false, Image.Format.Rgba8);
                img.Fill(Colors.White);
            }

            // Composite live painted layers over the submesh base texture so wand samples the true visible surface
            if (_layerManager != null)
            {
                var fullAtlas = _layerManager.BakeCompositeImage(surfaceIdx);
                if (fullAtlas != null)
                {
                    int rX = Mathf.Clamp((int)(submeshRect.Position.X * fullAtlas.GetWidth()), 0, fullAtlas.GetWidth() - 1);
                    int rY = Mathf.Clamp((int)(submeshRect.Position.Y * fullAtlas.GetHeight()), 0, fullAtlas.GetHeight() - 1);
                    int rW = Mathf.Clamp((int)(submeshRect.Size.X * fullAtlas.GetWidth()), 1, fullAtlas.GetWidth() - rX);
                    int rH = Mathf.Clamp((int)(submeshRect.Size.Y * fullAtlas.GetHeight()), 1, fullAtlas.GetHeight() - rY);
                    var paintRegion = fullAtlas.GetRegion(new Rect2I(rX, rY, rW, rH));
                    if (paintRegion != null)
                    {
                        if (paintRegion.GetWidth() != img.GetWidth() || paintRegion.GetHeight() != img.GetHeight())
                        {
                            paintRegion.Resize(img.GetWidth(), img.GetHeight());
                        }
                        int w = img.GetWidth();
                        int h = img.GetHeight();
                        for (int y = 0; y < h; y++)
                        {
                            for (int x = 0; x < w; x++)
                            {
                                Color pCol = paintRegion.GetPixel(x, y);
                                if (pCol.A > 0.001f)
                                {
                                    Color bCol = img.GetPixel(x, y);
                                    float a = pCol.A;
                                    Color comp = new Color(
                                        pCol.R * a + bCol.R * (1.0f - a),
                                        pCol.G * a + bCol.G * (1.0f - a),
                                        pCol.B * a + bCol.B * (1.0f - a),
                                        1.0f
                                    );
                                    img.SetPixel(x, y, comp);
                                }
                            }
                        }
                    }
                }
            }

            int px = Mathf.Clamp((int)(hit.HitUV.X * img.GetWidth()), 0, img.GetWidth() - 1);
            int py = Mathf.Clamp((int)(hit.HitUV.Y * img.GetHeight()), 0, img.GetHeight() - 1);
            Color sampledColor = img.GetPixel(px, py);

            Rid baseTextureRid = new();
            int atlasSize = _layerManager.CanvasSize.X;
            if (_layerManager.AtlasManager != null && GodotObject.IsInstanceValid(_layerManager.AtlasManager))
            {
                var ridVal = _layerManager.AtlasManager.Get("base_texture_rid");
                if (ridVal.VariantType == Variant.Type.Rid)
                {
                    baseTextureRid = ridVal.AsRid();
                }
                var sizeVal = _layerManager.AtlasManager.Get("atlas_size");
                if (sizeVal.VariantType == Variant.Type.Int)
                {
                    atlasSize = (int)sizeVal;
                }
            }

            if (_magicWandTool.GenerateMask(sampledColor, hit.HitUV, img, submeshRect, baseTextureRid, atlasSize, combineMode))
            {
                SyncSelectionMaskState();
            }
        }

        public void SyncSelectionMaskState()
        {
            if (_magicWandTool == null) return;

            bool hasMask = _magicWandTool.HasSelection && _magicWandTool.UseSelectionMask;
            Rid maskRid = hasMask ? _magicWandTool.SelectionMaskRid : new Rid();

            if (_cameraBrush != null && GodotObject.IsInstanceValid(_cameraBrush))
            {
                _cameraBrush.Set("selection_mask_rid", maskRid);
                _cameraBrush.Set("use_selection_mask", hasMask);
                _cameraBrush.Call("get_atlas_textures");
            }
            if (_mirrorCameraBrush != null && GodotObject.IsInstanceValid(_mirrorCameraBrush))
            {
                _mirrorCameraBrush.Set("selection_mask_rid", maskRid);
                _mirrorCameraBrush.Set("use_selection_mask", hasMask);
                _mirrorCameraBrush.Call("get_atlas_textures");
            }

            if (_layerManager != null)
            {
                _layerManager.SetSelectionMaskOverlay(hasMask ? _magicWandTool.MaskTextureResource : null, hasMask);
            }

            _brushPalette?.UpdateWandUI();
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
            int texSize = 64;

            // 1. Soft Circle (Cosine / Gaussian)
            {
                var img = Image.CreateEmpty(texSize, texSize, false, Image.Format.Rgba8);
                Vector2 center = new Vector2(texSize * 0.5f, texSize * 0.5f);
                float radius = texSize * 0.5f;

                for (int y = 0; y < texSize; y++)
                {
                    for (int x = 0; x < texSize; x++)
                    {
                        float d = (new Vector2(x, y) - center).Length() / radius;
                        float alpha = Mathf.Clamp(1.0f - Mathf.SmoothStep(0.0f, 1.0f, d), 0.0f, 1.0f);
                        img.SetPixel(x, y, new Color(1, 1, 1, alpha));
                    }
                }
                _brushTextures[BrushShapeType.SoftCircle] = ImageTexture.CreateFromImage(img);
            }

            // 2. Hard Circle
            {
                var img = Image.CreateEmpty(texSize, texSize, false, Image.Format.Rgba8);
                Vector2 center = new Vector2(texSize * 0.5f, texSize * 0.5f);
                float radius = texSize * 0.5f - 1.0f;

                for (int y = 0; y < texSize; y++)
                {
                    for (int x = 0; x < texSize; x++)
                    {
                        float d = (new Vector2(x, y) - center).Length();
                        float alpha = d <= radius ? 1.0f : Mathf.Clamp(1.0f - (d - radius), 0.0f, 1.0f);
                        img.SetPixel(x, y, new Color(1, 1, 1, alpha));
                    }
                }
                _brushTextures[BrushShapeType.HardCircle] = ImageTexture.CreateFromImage(img);
            }

            // 3. Splatter (Random multi-dot distribution)
            {
                var img = Image.CreateEmpty(texSize, texSize, false, Image.Format.Rgba8);
                img.Fill(Colors.Transparent);
                var rng = new RandomNumberGenerator();
                rng.Seed = 1337;

                int dotCount = 35;
                for (int i = 0; i < dotCount; i++)
                {
                    float angle = rng.RandfRange(0, Mathf.Tau);
                    float dist = rng.RandfRange(0, texSize * 0.45f);
                    int cx = (int)(texSize * 0.5f + Mathf.Cos(angle) * dist);
                    int cy = (int)(texSize * 0.5f + Mathf.Sin(angle) * dist);
                    int r = rng.RandiRange(1, 4);

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
                        float d = (new Vector2(x, y) - center).Length() / radius;
                        float falloff = Mathf.Clamp(1.0f - d, 0.0f, 1.0f);
                        float noise = rng.RandfRange(0.2f, 1.0f);
                        float alpha = falloff * noise;
                        img.SetPixel(x, y, new Color(1, 1, 1, alpha));
                    }
                }
                _brushTextures[BrushShapeType.Grunge] = ImageTexture.CreateFromImage(img);
            }

            // 5. Square
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
            base._ExitTree();
        }
    }
}
