using Godot;
using System;

namespace DeadlockPlayground.Painter
{
    public enum ProjectionGizmoHandle
    {
        None = 0,
        Body = 1,
        TopLeft = 2,
        TopRight = 3,
        BottomRight = 4,
        BottomLeft = 5,
        Rotate = 6
    }

    public class ProjectionTransformGizmo
    {
        public event Action GizmoChanged;
        public event Action GizmoCommitted;
        public event Action GizmoCancelled;

        public bool IsActive { get; set; } = false;
        public bool IsText { get; set; } = false;
        public bool Is3DProjection { get; set; } = false;

        // Position & dimensions in atlas pixel coordinates
        private Vector2 _center = new Vector2(1024, 1024);
        private Vector2 _size = new Vector2(256, 256);
        private float _rotationDegrees = 0.0f;

        public Vector2 Center
        {
            get => _center;
            set
            {
                if (_center != value)
                {
                    _center = value;
                    GizmoChanged?.Invoke();
                }
            }
        }

        public Vector2 Size
        {
            get => _size;
            set
            {
                if (_size != value)
                {
                    _size = value;
                    GizmoChanged?.Invoke();
                }
            }
        }

        public float RotationDegrees
        {
            get => _rotationDegrees;
            set
            {
                if (MathF.Abs(_rotationDegrees - value) > 0.001f)
                {
                    _rotationDegrees = value;
                    GizmoChanged?.Invoke();
                }
            }
        }

        public Texture2D Texture { get; set; }

        // 3D Placement cache
        public Vector3 WorldPos { get; set; } = Vector3.Zero;
        public Vector3 WorldNormal { get; set; } = Vector3.Up;
        public Vector3 DecalRight { get; set; } = Vector3.Right;
        public Vector3 DecalDown { get; set; } = Vector3.Back;
        public Vector3 WorldTangent { get; set; } = Vector3.Zero;
        public Vector3 WorldBitangent { get; set; } = Vector3.Zero;
        public float UnitsU { get; set; } = 1.0f;
        public float UnitsV { get; set; } = 1.0f;
        public bool Has3DPlacement { get; set; } = false;
        public MeshInstance3D HitMesh { get; set; } = null;
        public Vector2 HitUV { get; set; } = Vector2.Zero;
        public int HitSurfaceIndex { get; set; } = 0;

        // Interaction state
        private bool _isDragging = false;
        private ProjectionGizmoHandle _activeHandle = ProjectionGizmoHandle.None;
        private Vector2 _dragStartAtlasPx = Vector2.Zero;
        private Vector2 _initialCenter = Vector2.Zero;
        private Vector2 _initialSize = Vector2.Zero;
        private float _initialRotation = 0.0f;

        public bool IsDragging => _isDragging;
        public ProjectionGizmoHandle ActiveHandle => _activeHandle;

        public float NormalizedScale(int atlasSize)
        {
            if (atlasSize <= 0) atlasSize = 2048;
            return MathF.Max(Size.X, Size.Y) / atlasSize;
        }

        public void Activate(Vector2 centerAtlasPx, Texture2D texture, float initialScale, float initialRotationDeg, bool isText, int atlasSize = 2048)
        {
            IsActive = true;
            IsText = isText;
            Texture = texture;
            Center = centerAtlasPx;
            RotationDegrees = initialRotationDeg;

            float basePixelDim = initialScale * atlasSize;
            float aspect = 1.0f;
            if (texture != null && texture.GetHeight() > 0)
            {
                aspect = (float)texture.GetWidth() / texture.GetHeight();
            }

            if (aspect >= 1.0f)
            {
                Size = new Vector2(basePixelDim, basePixelDim / aspect);
            }
            else
            {
                Size = new Vector2(basePixelDim * aspect, basePixelDim);
            }

            GizmoChanged?.Invoke();
        }

        public void Deactivate()
        {
            IsActive = false;
            _isDragging = false;
            _activeHandle = ProjectionGizmoHandle.None;
            Has3DPlacement = false;
            Is3DProjection = false;
            GizmoCancelled?.Invoke();
            GizmoChanged?.Invoke();
        }

        public (Vector2 TopLeft, Vector2 TopRight, Vector2 BottomRight, Vector2 BottomLeft) GetCorners()
        {
            float hw = Size.X * 0.5f;
            float hh = Size.Y * 0.5f;
            float rad = Mathf.DegToRad(RotationDegrees);

            Vector2 tl = Center + new Vector2(-hw, -hh).Rotated(rad);
            Vector2 tr = Center + new Vector2(hw, -hh).Rotated(rad);
            Vector2 br = Center + new Vector2(hw, hh).Rotated(rad);
            Vector2 bl = Center + new Vector2(-hw, hh).Rotated(rad);

            return (tl, tr, br, bl);
        }

        public Vector2 GetRotationHandlePosition()
        {
            float hh = Size.Y * 0.5f;
            float rad = Mathf.DegToRad(RotationDegrees);
            float stemLength = 26.0f;
            return Center + new Vector2(0.0f, -hh - stemLength).Rotated(rad);
        }

        public ProjectionGizmoHandle HitTest(Vector2 atlasPx, float zoom)
        {
            if (!IsActive) return ProjectionGizmoHandle.None;

            float hitRadius = MathF.Max(14.0f / MathF.Max(zoom, 0.001f), 8.0f);
            float hitRadiusSq = hitRadius * hitRadius;

            // Rotation handle
            Vector2 rotHandlePos = GetRotationHandlePosition();
            if (atlasPx.DistanceSquaredTo(rotHandlePos) <= hitRadiusSq)
            {
                return ProjectionGizmoHandle.Rotate;
            }

            // 4 Corner handles
            var (tl, tr, br, bl) = GetCorners();
            if (atlasPx.DistanceSquaredTo(tl) <= hitRadiusSq) return ProjectionGizmoHandle.TopLeft;
            if (atlasPx.DistanceSquaredTo(tr) <= hitRadiusSq) return ProjectionGizmoHandle.TopRight;
            if (atlasPx.DistanceSquaredTo(br) <= hitRadiusSq) return ProjectionGizmoHandle.BottomRight;
            if (atlasPx.DistanceSquaredTo(bl) <= hitRadiusSq) return ProjectionGizmoHandle.BottomLeft;

            // Body test
            Vector2 local = (atlasPx - Center).Rotated(-Mathf.DegToRad(RotationDegrees));
            if (MathF.Abs(local.X) <= Size.X * 0.5f && MathF.Abs(local.Y) <= Size.Y * 0.5f)
            {
                return ProjectionGizmoHandle.Body;
            }

            return ProjectionGizmoHandle.None;
        }

        public bool StartHandleDrag(ProjectionGizmoHandle handle, Vector2 startAtlasPx)
        {
            if (!IsActive || handle == ProjectionGizmoHandle.None) return false;

            _isDragging = true;
            _activeHandle = handle;
            _dragStartAtlasPx = startAtlasPx;
            _initialCenter = Center;
            _initialSize = Size;
            _initialRotation = RotationDegrees;
            return true;
        }

        public void UpdateHandleDrag(Vector2 currentAtlasPx, bool shiftKey = false)
        {
            if (!_isDragging || _activeHandle == ProjectionGizmoHandle.None) return;

            Vector2 totalDelta = currentAtlasPx - _dragStartAtlasPx;

            if (_activeHandle == ProjectionGizmoHandle.Body)
            {
                Center = _initialCenter + totalDelta;
                GizmoChanged?.Invoke();
                return;
            }

            if (_activeHandle == ProjectionGizmoHandle.Rotate)
            {
                Vector2 fromCenter = currentAtlasPx - Center;
                float angleRad = MathF.Atan2(fromCenter.Y, fromCenter.X) + MathF.PI * 0.5f;
                float deg = Mathf.RadToDeg(angleRad);
                if (shiftKey)
                {
                    deg = MathF.Round(deg / 15.0f) * 15.0f;
                }
                RotationDegrees = deg;
                GizmoChanged?.Invoke();
                return;
            }

            // Corner scale drag
            float rad = Mathf.DegToRad(_initialRotation);
            Vector2 localDelta = totalDelta.Rotated(-rad);

            float w = _initialSize.X;
            float h = _initialSize.Y;

            switch (_activeHandle)
            {
                case ProjectionGizmoHandle.BottomRight:
                    w += localDelta.X * 2.0f;
                    h += localDelta.Y * 2.0f;
                    break;
                case ProjectionGizmoHandle.BottomLeft:
                    w -= localDelta.X * 2.0f;
                    h += localDelta.Y * 2.0f;
                    break;
                case ProjectionGizmoHandle.TopRight:
                    w += localDelta.X * 2.0f;
                    h -= localDelta.Y * 2.0f;
                    break;
                case ProjectionGizmoHandle.TopLeft:
                    w -= localDelta.X * 2.0f;
                    h -= localDelta.Y * 2.0f;
                    break;
            }

            float initialAspect = _initialSize.X / MathF.Max(_initialSize.Y, 0.001f);
            if (!shiftKey)
            {
                // Default: preserve aspect ratio
                float maxDim = MathF.Max(w, h * initialAspect);
                w = maxDim;
                h = maxDim / initialAspect;
            }

            w = MathF.Max(w, 16.0f);
            h = MathF.Max(h, 16.0f);

            Size = new Vector2(w, h);
            GizmoChanged?.Invoke();
        }

        public void EndHandleDrag()
        {
            _isDragging = false;
            _activeHandle = ProjectionGizmoHandle.None;
            GizmoChanged?.Invoke();
        }

        public void DrawOverlay(CanvasItem canvas, Vector2 pan, float zoom)
        {
            if (!IsActive) return;

            var (tl, tr, br, bl) = GetCorners();
            Color accentBorder = new Color(0.25f, 0.75f, 1.0f, 0.95f); // Cyan-blue outline

            // Draw Texture Preview transformed in 2D
            if (Texture != null)
            {
                var pts = new Vector2[] { tl, tr, br, bl };
                var uvs = new Vector2[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
                var colors = new Color[] { Colors.White, Colors.White, Colors.White, Colors.White };

                canvas.DrawPolygon(pts, colors, uvs, Texture);
            }

            // Bounding wireframe
            canvas.DrawPolyline(new Vector2[] { tl, tr, br, bl, tl }, accentBorder, 1.5f / zoom, antialiased: true);

            // Center crosshair
            float crossSize = 8.0f / zoom;
            canvas.DrawLine(Center - new Vector2(crossSize, 0), Center + new Vector2(crossSize, 0), accentBorder, 1.2f / zoom);
            canvas.DrawLine(Center - new Vector2(0, crossSize), Center + new Vector2(0, crossSize), accentBorder, 1.2f / zoom);

            // Rotation handle stem and grabber
            Vector2 topCenter = (tl + tr) * 0.5f;
            Vector2 rotPos = GetRotationHandlePosition();
            canvas.DrawLine(topCenter, rotPos, accentBorder, 1.2f / zoom, antialiased: true);
            canvas.DrawCircle(rotPos, 6.0f / zoom, new Color(0.95f, 0.82f, 0.35f, 0.95f), filled: true); // Gold grabber
            canvas.DrawCircle(rotPos, 6.0f / zoom, Colors.White, filled: false, width: 1.2f / zoom);

            // 4 Corner handles
            float handleSize = 8.0f / zoom;
            DrawHandleSquare(canvas, tl, handleSize);
            DrawHandleSquare(canvas, tr, handleSize);
            DrawHandleSquare(canvas, br, handleSize);
            DrawHandleSquare(canvas, bl, handleSize);
        }

        private static void DrawHandleSquare(CanvasItem canvas, Vector2 pos, float size)
        {
            Rect2 rect = new Rect2(pos - new Vector2(size * 0.5f, size * 0.5f), new Vector2(size, size));
            canvas.DrawRect(rect, Colors.White, filled: true);
            canvas.DrawRect(rect, new Color(0.12f, 0.14f, 0.18f, 1.0f), filled: false, width: 1.2f);
        }

        public bool Commit(SkinLayerManager layerManager, MagicWandTool wandTool, int atlasSize = 2048)
        {
            if (!IsActive || Texture == null || layerManager == null) return false;

            int atlasW = layerManager.CanvasSize.X > 0 ? layerManager.CanvasSize.X : (atlasSize > 0 ? atlasSize : 2048);
            int atlasH = layerManager.CanvasSize.Y > 0 ? layerManager.CanvasSize.Y : (atlasSize > 0 ? atlasSize : 2048);
            Vector2 uv = new Vector2(Center.X / atlasW, Center.Y / atlasH);
            float scale = NormalizedScale(Math.Max(atlasW, atlasH));

            bool success;
            if (Has3DPlacement && Is3DProjection)
            {
                success = layerManager.StampDecalToAtlas(
                    uv,
                    Texture,
                    RotationDegrees,
                    scale,
                    wandTool,
                    DecalRight,
                    DecalDown,
                    WorldTangent,
                    WorldBitangent,
                    UnitsU,
                    UnitsV,
                    explicitPixelSize: Size);
            }
            else
            {
                success = layerManager.StampDecalToAtlas(
                    uv,
                    Texture,
                    RotationDegrees,
                    scale,
                    wandTool,
                    explicitPixelSize: Size);
            }

            if (success)
            {
                GizmoCommitted?.Invoke();
                Deactivate();
            }

            return success;
        }
    }
}
