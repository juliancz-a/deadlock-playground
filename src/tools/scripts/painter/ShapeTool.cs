using Godot;
using System;
using System.Collections.Generic;

namespace DeadlockPlayground.Painter
{
    public enum CanvasShapeType
    {
        Square = 0,
        Circle = 1,
        Line = 2
    }

    public enum ShapeFillMode
    {
        FillOnly = 0,
        StrokeOnly = 1,
        FillAndStroke = 2
    }

    public enum ShapeHandleType
    {
        None = 0,
        Body = 1,
        TopLeft = 2,
        TopRight = 3,
        BottomRight = 4,
        BottomLeft = 5,
        Rotate = 6
    }

    public class ShapeTool
    {
        public event Action ShapeChanged;
        public event Action ShapeCommitted;
        public event Action ShapeCancelled;

        public CanvasShapeType ShapeType { get; set; } = CanvasShapeType.Square;
        public ShapeFillMode FillMode { get; set; } = ShapeFillMode.FillOnly;
        public float StrokeWidth { get; set; } = 6.0f;

        // Interactive transform in 2D atlas pixel coordinates
        public Vector2 Center { get; set; } = new Vector2(1024, 1024);
        public Vector2 Size { get; set; } = new Vector2(250, 250);

        public float Width
        {
            get => Size.X;
            set => Size = new Vector2(value, Size.Y);
        }

        public float Height
        {
            get => Size.Y;
            set => Size = new Vector2(Size.X, value);
        }

        public float RotationDegrees { get; set; } = 0.0f;

        // Line-specific endpoints (in atlas px)
        public Vector2 LineStart { get; set; } = Vector2.Zero;
        public Vector2 LineEnd { get; set; } = Vector2.Zero;

        public bool HasActiveShape { get; private set; } = false;

        // Drag interaction state
        private bool _isDragging = false;
        private ShapeHandleType _activeHandle = ShapeHandleType.None;
        private Vector2 _dragStartAtlasPos = Vector2.Zero;
        private Vector2 _initialCenter = Vector2.Zero;
        private Vector2 _initialSize = Vector2.Zero;
        private float _initialRotation = 0.0f;
        private Vector2 _initialLineStart = Vector2.Zero;
        private Vector2 _initialLineEnd = Vector2.Zero;

        private bool _isCreatingNew = false;

        public bool IsDragging => _isDragging;
        public ShapeHandleType ActiveHandle => _activeHandle;

        public void BeginNewShape(Vector2 startAtlasPx)
        {
            HasActiveShape = true;
            _isDragging = true;
            _isCreatingNew = true;
            _activeHandle = ShapeHandleType.BottomRight;

            _dragStartAtlasPos = startAtlasPx;
            Center = startAtlasPx;
            Size = new Vector2(10, 10);
            RotationDegrees = 0.0f;

            LineStart = startAtlasPx;
            LineEnd = startAtlasPx;

            _initialCenter = Center;
            _initialSize = Size;
            _initialRotation = 0.0f;
            _initialLineStart = LineStart;
            _initialLineEnd = LineEnd;

            ShapeChanged?.Invoke();
        }

        public void UpdateDrag(Vector2 currentAtlasPx, bool shiftKey = false)
        {
            if (!_isDragging) return;
            if (_isCreatingNew)
            {
                UpdateNewShapeDrag(currentAtlasPx, shiftKey);
            }
            else
            {
                UpdateHandleDrag(currentAtlasPx, shiftKey);
            }
        }

        public void UpdateNewShapeDrag(Vector2 currentAtlasPx, bool squareConstrain = false)
        {
            if (!_isDragging) return;

            if (ShapeType == CanvasShapeType.Line)
            {
                LineEnd = currentAtlasPx;
                Center = (LineStart + LineEnd) * 0.5f;
                Vector2 delta = LineEnd - LineStart;
                Size = new Vector2(delta.Length(), StrokeWidth);
                RotationDegrees = Mathf.RadToDeg(MathF.Atan2(delta.Y, delta.X));
            }
            else
            {
                Vector2 diff = currentAtlasPx - _dragStartAtlasPos;
                float w = MathF.Abs(diff.X);
                float h = MathF.Abs(diff.Y);

                if (squareConstrain)
                {
                    float maxDim = MathF.Max(w, h);
                    w = maxDim;
                    h = maxDim;
                }

                w = MathF.Max(w, 8.0f);
                h = MathF.Max(h, 8.0f);

                Size = new Vector2(w, h);
                Center = _dragStartAtlasPos + diff * 0.5f;
            }

            ShapeChanged?.Invoke();
        }

        public void EndNewShapeDrag()
        {
            _isDragging = false;
            _activeHandle = ShapeHandleType.None;
            ShapeChanged?.Invoke();
        }

        public ShapeHandleType HitTest(Vector2 atlasPx, float zoom)
        {
            if (!HasActiveShape) return ShapeHandleType.None;

            float hitRadius = 12.0f / MathF.Max(zoom, 0.001f);
            float hitRadiusSq = hitRadius * hitRadius;

            if (ShapeType == CanvasShapeType.Line)
            {
                if (atlasPx.DistanceSquaredTo(LineStart) <= hitRadiusSq) return ShapeHandleType.TopLeft;
                if (atlasPx.DistanceSquaredTo(LineEnd) <= hitRadiusSq) return ShapeHandleType.BottomRight;

                // Test line body
                float distToLine = DistancePointToSegment(atlasPx, LineStart, LineEnd);
                if (distToLine <= MathF.Max(StrokeWidth * 0.5f, 8.0f / zoom)) return ShapeHandleType.Body;
                return ShapeHandleType.None;
            }

            // Test rotation handle (top center extended)
            Vector2 rotHandlePos = GetRotationHandlePosition();
            if (atlasPx.DistanceSquaredTo(rotHandlePos) <= hitRadiusSq)
            {
                return ShapeHandleType.Rotate;
            }

            // Test 4 corner handles
            var (tl, tr, br, bl) = GetCorners();
            if (atlasPx.DistanceSquaredTo(tl) <= hitRadiusSq) return ShapeHandleType.TopLeft;
            if (atlasPx.DistanceSquaredTo(tr) <= hitRadiusSq) return ShapeHandleType.TopRight;
            if (atlasPx.DistanceSquaredTo(br) <= hitRadiusSq) return ShapeHandleType.BottomRight;
            if (atlasPx.DistanceSquaredTo(bl) <= hitRadiusSq) return ShapeHandleType.BottomLeft;

            // Test body (inside transformed box)
            Vector2 local = (atlasPx - Center).Rotated(-Mathf.DegToRad(RotationDegrees));
            if (MathF.Abs(local.X) <= Size.X * 0.5f && MathF.Abs(local.Y) <= Size.Y * 0.5f)
            {
                return ShapeHandleType.Body;
            }

            return ShapeHandleType.None;
        }

        public bool StartHandleDrag(ShapeHandleType handle, Vector2 startAtlasPx)
        {
            if (!HasActiveShape || handle == ShapeHandleType.None) return false;

            _isDragging = true;
            _isCreatingNew = false;
            _activeHandle = handle;
            _dragStartAtlasPos = startAtlasPx;
            _initialCenter = Center;
            _initialSize = Size;
            _initialRotation = RotationDegrees;
            _initialLineStart = LineStart;
            _initialLineEnd = LineEnd;
            return true;
        }

        public void UpdateHandleDrag(Vector2 currentAtlasPx, bool shiftKey = false)
        {
            if (!_isDragging || _activeHandle == ShapeHandleType.None) return;

            Vector2 totalDelta = currentAtlasPx - _dragStartAtlasPos;

            if (ShapeType == CanvasShapeType.Line)
            {
                if (_activeHandle == ShapeHandleType.TopLeft)
                {
                    LineStart = _initialLineStart + totalDelta;
                }
                else if (_activeHandle == ShapeHandleType.BottomRight)
                {
                    LineEnd = _initialLineEnd + totalDelta;
                }
                else if (_activeHandle == ShapeHandleType.Body)
                {
                    LineStart = _initialLineStart + totalDelta;
                    LineEnd = _initialLineEnd + totalDelta;
                }

                Center = (LineStart + LineEnd) * 0.5f;
                Vector2 diff = LineEnd - LineStart;
                Size = new Vector2(diff.Length(), StrokeWidth);
                RotationDegrees = Mathf.RadToDeg(MathF.Atan2(diff.Y, diff.X));
                ShapeChanged?.Invoke();
                return;
            }

            if (_activeHandle == ShapeHandleType.Body)
            {
                Center = _initialCenter + totalDelta;
                ShapeChanged?.Invoke();
                return;
            }

            if (_activeHandle == ShapeHandleType.Rotate)
            {
                Vector2 fromCenter = currentAtlasPx - Center;
                float angleRad = MathF.Atan2(fromCenter.Y, fromCenter.X) + MathF.PI * 0.5f;
                float deg = Mathf.RadToDeg(angleRad);
                if (shiftKey)
                {
                    // Snap to 15 degrees
                    deg = MathF.Round(deg / 15.0f) * 15.0f;
                }
                RotationDegrees = deg;
                ShapeChanged?.Invoke();
                return;
            }

            // Corner scale drag
            float rad = Mathf.DegToRad(_initialRotation);
            Vector2 localDelta = totalDelta.Rotated(-rad);

            float w = _initialSize.X;
            float h = _initialSize.Y;

            switch (_activeHandle)
            {
                case ShapeHandleType.BottomRight:
                    w += localDelta.X;
                    h += localDelta.Y;
                    break;
                case ShapeHandleType.BottomLeft:
                    w -= localDelta.X;
                    h += localDelta.Y;
                    break;
                case ShapeHandleType.TopRight:
                    w += localDelta.X;
                    h -= localDelta.Y;
                    break;
                case ShapeHandleType.TopLeft:
                    w -= localDelta.X;
                    h -= localDelta.Y;
                    break;
            }

            if (shiftKey)
            {
                float aspect = _initialSize.X / MathF.Max(_initialSize.Y, 0.001f);
                float maxDim = MathF.Max(w, h * aspect);
                w = maxDim;
                h = maxDim / aspect;
            }

            w = MathF.Max(w, 8.0f);
            h = MathF.Max(h, 8.0f);

            Size = new Vector2(w, h);
            ShapeChanged?.Invoke();
        }

        public void EndHandleDrag()
        {
            _isDragging = false;
            _isCreatingNew = false;
            _activeHandle = ShapeHandleType.None;
            ShapeChanged?.Invoke();
        }

        public void CancelShape()
        {
            HasActiveShape = false;
            _isDragging = false;
            _isCreatingNew = false;
            _activeHandle = ShapeHandleType.None;
            ShapeCancelled?.Invoke();
            ShapeChanged?.Invoke();
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

        public void DrawOverlay(CanvasItem canvas, Vector2 pan, float zoom, Color previewColor)
        {
            if (!HasActiveShape) return;

            Color accentBorder = new Color(0.95f, 0.82f, 0.35f, 0.95f);
            Color fillPreview = new Color(previewColor.R, previewColor.G, previewColor.B, previewColor.A * 0.35f);
            Color strokePreview = new Color(previewColor.R, previewColor.G, previewColor.B, MathF.Max(previewColor.A, 0.85f));

            if (ShapeType == CanvasShapeType.Line)
            {
                canvas.DrawLine(LineStart, LineEnd, strokePreview, StrokeWidth, antialiased: true);
                canvas.DrawLine(LineStart, LineEnd, accentBorder, 1.5f / zoom, antialiased: true);

                float hRadius = 5.0f / zoom;
                canvas.DrawCircle(LineStart, hRadius, Colors.White, filled: true);
                canvas.DrawCircle(LineStart, hRadius, Colors.Black, filled: false, width: 1.0f / zoom);
                canvas.DrawCircle(LineEnd, hRadius, Colors.White, filled: true);
                canvas.DrawCircle(LineEnd, hRadius, Colors.Black, filled: false, width: 1.0f / zoom);
                return;
            }

            var (tl, tr, br, bl) = GetCorners();

            // Draw shape silhouette
            if (ShapeType == CanvasShapeType.Square)
            {
                var pts = new Vector2[] { tl, tr, br, bl };
                if (FillMode == ShapeFillMode.FillOnly || FillMode == ShapeFillMode.FillAndStroke)
                {
                    canvas.DrawColoredPolygon(pts, fillPreview);
                }

                if (FillMode == ShapeFillMode.StrokeOnly || FillMode == ShapeFillMode.FillAndStroke)
                {
                    canvas.DrawPolyline(new Vector2[] { tl, tr, br, bl, tl }, strokePreview, StrokeWidth, antialiased: true);
                }
            }
            else if (ShapeType == CanvasShapeType.Circle)
            {
                // Draw ellipse segments
                int segs = 48;
                var poly = new Vector2[segs];
                float rad = Mathf.DegToRad(RotationDegrees);
                float rx = Size.X * 0.5f;
                float ry = Size.Y * 0.5f;

                for (int i = 0; i < segs; i++)
                {
                    float angle = (i / (float)segs) * MathF.Tau;
                    Vector2 localPt = new Vector2(MathF.Cos(angle) * rx, MathF.Sin(angle) * ry);
                    poly[i] = Center + localPt.Rotated(rad);
                }

                if (FillMode == ShapeFillMode.FillOnly || FillMode == ShapeFillMode.FillAndStroke)
                {
                    canvas.DrawColoredPolygon(poly, fillPreview);
                }

                if (FillMode == ShapeFillMode.StrokeOnly || FillMode == ShapeFillMode.FillAndStroke)
                {
                    var closed = new Vector2[segs + 1];
                    Array.Copy(poly, closed, segs);
                    closed[segs] = poly[0];
                    canvas.DrawPolyline(closed, strokePreview, StrokeWidth, antialiased: true);
                }
            }

            // Draw Bounding Wireframe & Transformation Gizmo
            canvas.DrawPolyline(new Vector2[] { tl, tr, br, bl, tl }, accentBorder, 1.2f / zoom, antialiased: true);

            // Rotation handle stem and circle
            Vector2 topCenter = (tl + tr) * 0.5f;
            Vector2 rotPos = GetRotationHandlePosition();
            canvas.DrawLine(topCenter, rotPos, accentBorder, 1.0f / zoom, antialiased: true);
            canvas.DrawCircle(rotPos, 5.0f / zoom, new Color(0.35f, 0.85f, 1.0f, 0.95f), filled: true);
            canvas.DrawCircle(rotPos, 5.0f / zoom, Colors.White, filled: false, width: 1.0f / zoom);

            // 4 Corner handles
            float handleSize = 7.0f / zoom;
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

        public unsafe bool CommitShape(SkinLayerManager layerManager, Color activeColor, LayerBlendMode blendMode, MagicWandTool wandTool)
        {
            if (!HasActiveShape || layerManager == null || layerManager.ActiveLayer == null) return false;

            var layer = layerManager.ActiveLayer;
            if (layer.IsLocked || !layer.IsVisible) return false;

            int atlasSize = layerManager.CanvasSize.X > 0 ? layerManager.CanvasSize.X : 2048;
            int bufferLen = atlasSize * atlasSize * 8;

            if (layer.GpuData == null || layer.GpuData.Length != bufferLen)
            {
                layer.GpuData = new byte[bufferLen];
            }

            layerManager.RecordInitialSnapshot();

            // Calculate conservative Axis-Aligned Bounding Box (AABB) in atlas pixels
            float pad = MathF.Max(StrokeWidth * 0.5f + 4.0f, 16.0f);
            float minX = atlasSize, maxX = 0, minY = atlasSize, maxY = 0;

            if (ShapeType == CanvasShapeType.Line)
            {
                minX = MathF.Min(LineStart.X, LineEnd.X) - pad;
                maxX = MathF.Max(LineStart.X, LineEnd.X) + pad;
                minY = MathF.Min(LineStart.Y, LineEnd.Y) - pad;
                maxY = MathF.Max(LineStart.Y, LineEnd.Y) + pad;
            }
            else
            {
                var (tl, tr, br, bl) = GetCorners();
                minX = MathF.Min(MathF.Min(tl.X, tr.X), MathF.Min(br.X, bl.X)) - pad;
                maxX = MathF.Max(MathF.Max(tl.X, tr.X), MathF.Max(br.X, bl.X)) + pad;
                minY = MathF.Min(MathF.Min(tl.Y, tr.Y), MathF.Min(br.Y, bl.Y)) - pad;
                maxY = MathF.Max(MathF.Max(tl.Y, tr.Y), MathF.Max(br.Y, bl.Y)) + pad;
            }

            int iMinX = Math.Clamp((int)MathF.Floor(minX), 0, atlasSize - 1);
            int iMaxX = Math.Clamp((int)MathF.Ceiling(maxX), 0, atlasSize - 1);
            int iMinY = Math.Clamp((int)MathF.Floor(minY), 0, atlasSize - 1);
            int iMaxY = Math.Clamp((int)MathF.Ceiling(maxY), 0, atlasSize - 1);

            int rectW = iMaxX - iMinX + 1;
            int rectH = iMaxY - iMinY + 1;
            if (rectW <= 0 || rectH <= 0) return false;

            Color linCol = activeColor.SrgbToLinear();
            float rad = Mathf.DegToRad(RotationDegrees);
            float cosR = MathF.Cos(-rad);
            float sinR = MathF.Sin(-rad);

            float halfW = Size.X * 0.5f;
            float halfH = Size.Y * 0.5f;
            float strokeHalf = StrokeWidth * 0.5f;

            bool checkWand = wandTool != null && wandTool.HasActiveSelection;

            fixed (byte* pDst = layer.GpuData)
            {
                Half* hLayer = (Half*)pDst;

                System.Threading.Tasks.Parallel.For(iMinY, iMaxY + 1, py =>
                {
                    int rowOffset = py * atlasSize * 4;

                    for (int px = iMinX; px <= iMaxX; px++)
                    {
                        float wandWeight = 1.0f;
                        if (checkWand)
                        {
                            float maskVal = wandTool.GetPixelMaskValue(px, py);
                            if (maskVal < 0.5f) continue;
                            wandWeight = Math.Clamp((maskVal - 0.5f) / 0.5f, 0.0f, 1.0f);
                        }

                        float coverage = 0.0f;

                        if (ShapeType == CanvasShapeType.Line)
                        {
                            float dist = DistancePointToSegment(new Vector2(px + 0.5f, py + 0.5f), LineStart, LineEnd);
                            float d = dist - strokeHalf;
                            coverage = Math.Clamp(0.5f - d, 0.0f, 1.0f);
                        }
                        else
                        {
                            // Transform pixel center to local shape coordinate space
                            float dx = (px + 0.5f) - Center.X;
                            float dy = (py + 0.5f) - Center.Y;
                            float lx = dx * cosR - dy * sinR;
                            float ly = dx * sinR + dy * cosR;

                            if (ShapeType == CanvasShapeType.Square)
                            {
                                float qx = MathF.Abs(lx) - halfW;
                                float qy = MathF.Abs(ly) - halfH;
                                float dOut = MathF.Sqrt(MathF.Max(qx, 0.0f) * MathF.Max(qx, 0.0f) + MathF.Max(qy, 0.0f) * MathF.Max(qy, 0.0f));
                                float dIn = MathF.Min(MathF.Max(qx, qy), 0.0f);
                                float boxSdf = dOut + dIn;

                                if (FillMode == ShapeFillMode.FillOnly)
                                {
                                    coverage = Math.Clamp(0.5f - boxSdf, 0.0f, 1.0f);
                                }
                                else if (FillMode == ShapeFillMode.StrokeOnly)
                                {
                                    float strokeD = MathF.Abs(boxSdf) - strokeHalf;
                                    coverage = Math.Clamp(0.5f - strokeD, 0.0f, 1.0f);
                                }
                                else if (FillMode == ShapeFillMode.FillAndStroke)
                                {
                                    float fillStrokeSdf = boxSdf - strokeHalf;
                                    coverage = Math.Clamp(0.5f - fillStrokeSdf, 0.0f, 1.0f);
                                }
                            }
                            else if (ShapeType == CanvasShapeType.Circle)
                            {
                                float nX = lx / MathF.Max(halfW, 0.001f);
                                float nY = ly / MathF.Max(halfH, 0.001f);
                                float normDist = MathF.Sqrt(nX * nX + nY * nY);
                                float minR = MathF.Min(halfW, halfH);
                                float ellipseSdf = (normDist - 1.0f) * minR;

                                if (FillMode == ShapeFillMode.FillOnly)
                                {
                                    coverage = Math.Clamp(0.5f - ellipseSdf, 0.0f, 1.0f);
                                }
                                else if (FillMode == ShapeFillMode.StrokeOnly)
                                {
                                    float strokeD = MathF.Abs(ellipseSdf) - strokeHalf;
                                    coverage = Math.Clamp(0.5f - strokeD, 0.0f, 1.0f);
                                }
                                else if (FillMode == ShapeFillMode.FillAndStroke)
                                {
                                    float fillStrokeSdf = ellipseSdf - strokeHalf;
                                    coverage = Math.Clamp(0.5f - fillStrokeSdf, 0.0f, 1.0f);
                                }
                            }
                        }

                        float finalAlpha = coverage * activeColor.A * layer.Opacity * wandWeight;
                        if (finalAlpha <= 0.0001f) continue;

                        int idx = rowOffset + px * 4;
                        float curR = (float)hLayer[idx];
                        float curG = (float)hLayer[idx + 1];
                        float curB = (float)hLayer[idx + 2];
                        float curA = (float)hLayer[idx + 3];

                        float outA = Math.Clamp(finalAlpha + curA * (1.0f - finalAlpha), 0.0f, 1.0f);
                        float outR = (outA > 0.0001f) ? (linCol.R * finalAlpha + curR * curA * (1.0f - finalAlpha)) / outA : linCol.R;
                        float outG = (outA > 0.0001f) ? (linCol.G * finalAlpha + curG * curA * (1.0f - finalAlpha)) / outA : linCol.G;
                        float outB = (outA > 0.0001f) ? (linCol.B * finalAlpha + curB * curA * (1.0f - finalAlpha)) / outA : linCol.B;

                        hLayer[idx] = (Half)outR;
                        hLayer[idx + 1] = (Half)outG;
                        hLayer[idx + 2] = (Half)outB;
                        hLayer[idx + 3] = (Half)outA;
                    }
                });
            }

            layerManager.SyncActiveLayerGpuTexture();
            layerManager.RecompositeGpuLayers();
            layerManager.RecordUndoSnapshot();
            layerManager.NotifyStackChanged();

            HasActiveShape = false;
            _isDragging = false;
            _activeHandle = ShapeHandleType.None;

            ShapeCommitted?.Invoke();
            ShapeChanged?.Invoke();
            return true;
        }

        public unsafe void BlendPreview(byte[] compositeBuffer, int atlasSize, Color activeColor)
        {
            if (!HasActiveShape || compositeBuffer == null || compositeBuffer.Length < atlasSize * atlasSize * 8) return;

            // Calculate conservative Axis-Aligned Bounding Box (AABB) in atlas pixels
            float pad = MathF.Max(StrokeWidth * 0.5f + 4.0f, 16.0f);
            float minX = atlasSize, maxX = 0, minY = atlasSize, maxY = 0;

            if (ShapeType == CanvasShapeType.Line)
            {
                minX = MathF.Min(LineStart.X, LineEnd.X) - pad;
                maxX = MathF.Max(LineStart.X, LineEnd.X) + pad;
                minY = MathF.Min(LineStart.Y, LineEnd.Y) - pad;
                maxY = MathF.Max(LineStart.Y, LineEnd.Y) + pad;
            }
            else
            {
                var (tl, tr, br, bl) = GetCorners();
                minX = MathF.Min(MathF.Min(tl.X, tr.X), MathF.Min(br.X, bl.X)) - pad;
                maxX = MathF.Max(MathF.Max(tl.X, tr.X), MathF.Max(br.X, bl.X)) + pad;
                minY = MathF.Min(MathF.Min(tl.Y, tr.Y), MathF.Min(br.Y, bl.Y)) - pad;
                maxY = MathF.Max(MathF.Max(tl.Y, tr.Y), MathF.Max(br.Y, bl.Y)) + pad;
            }

            int iMinX = Math.Clamp((int)MathF.Floor(minX), 0, atlasSize - 1);
            int iMaxX = Math.Clamp((int)MathF.Ceiling(maxX), 0, atlasSize - 1);
            int iMinY = Math.Clamp((int)MathF.Floor(minY), 0, atlasSize - 1);
            int iMaxY = Math.Clamp((int)MathF.Ceiling(maxY), 0, atlasSize - 1);

            int rectW = iMaxX - iMinX + 1;
            int rectH = iMaxY - iMinY + 1;
            if (rectW <= 0 || rectH <= 0) return;

            Color linCol = activeColor.SrgbToLinear();
            float rad = Mathf.DegToRad(RotationDegrees);
            float cosR = MathF.Cos(-rad);
            float sinR = MathF.Sin(-rad);

            float halfW = Size.X * 0.5f;
            float halfH = Size.Y * 0.5f;
            float strokeHalf = StrokeWidth * 0.5f;

            fixed (byte* pDst = compositeBuffer)
            {
                Half* hComp = (Half*)pDst;

                System.Threading.Tasks.Parallel.For(iMinY, iMaxY + 1, py =>
                {
                    int rowOffset = py * atlasSize * 4;

                    for (int px = iMinX; px <= iMaxX; px++)
                    {
                        float coverage = 0.0f;

                        if (ShapeType == CanvasShapeType.Line)
                        {
                            float dist = DistancePointToSegment(new Vector2(px + 0.5f, py + 0.5f), LineStart, LineEnd);
                            float d = dist - strokeHalf;
                            coverage = Math.Clamp(0.5f - d, 0.0f, 1.0f);
                        }
                        else
                        {
                            float dx = (px + 0.5f) - Center.X;
                            float dy = (py + 0.5f) - Center.Y;
                            float lx = dx * cosR - dy * sinR;
                            float ly = dx * sinR + dy * cosR;

                            if (ShapeType == CanvasShapeType.Square)
                            {
                                float qx = MathF.Abs(lx) - halfW;
                                float qy = MathF.Abs(ly) - halfH;
                                float dOut = MathF.Sqrt(MathF.Max(qx, 0.0f) * MathF.Max(qx, 0.0f) + MathF.Max(qy, 0.0f) * MathF.Max(qy, 0.0f));
                                float dIn = MathF.Min(MathF.Max(qx, qy), 0.0f);
                                float boxSdf = dOut + dIn;

                                if (FillMode == ShapeFillMode.FillOnly)
                                {
                                    coverage = Math.Clamp(0.5f - boxSdf, 0.0f, 1.0f);
                                }
                                else if (FillMode == ShapeFillMode.StrokeOnly)
                                {
                                    float strokeD = MathF.Abs(boxSdf) - strokeHalf;
                                    coverage = Math.Clamp(0.5f - strokeD, 0.0f, 1.0f);
                                }
                                else if (FillMode == ShapeFillMode.FillAndStroke)
                                {
                                    float fillStrokeSdf = boxSdf - strokeHalf;
                                    coverage = Math.Clamp(0.5f - fillStrokeSdf, 0.0f, 1.0f);
                                }
                            }
                            else if (ShapeType == CanvasShapeType.Circle)
                            {
                                float nX = lx / MathF.Max(halfW, 0.001f);
                                float nY = ly / MathF.Max(halfH, 0.001f);
                                float normDist = MathF.Sqrt(nX * nX + nY * nY);
                                float minR = MathF.Min(halfW, halfH);
                                float ellipseSdf = (normDist - 1.0f) * minR;

                                if (FillMode == ShapeFillMode.FillOnly)
                                {
                                    coverage = Math.Clamp(0.5f - ellipseSdf, 0.0f, 1.0f);
                                }
                                else if (FillMode == ShapeFillMode.StrokeOnly)
                                {
                                    float strokeD = MathF.Abs(ellipseSdf) - strokeHalf;
                                    coverage = Math.Clamp(0.5f - strokeD, 0.0f, 1.0f);
                                }
                                else if (FillMode == ShapeFillMode.FillAndStroke)
                                {
                                    float fillStrokeSdf = ellipseSdf - strokeHalf;
                                    coverage = Math.Clamp(0.5f - fillStrokeSdf, 0.0f, 1.0f);
                                }
                            }
                        }

                        float finalAlpha = coverage * activeColor.A;
                        if (finalAlpha <= 0.0001f) continue;

                        int idx = rowOffset + px * 4;
                        float curR = (float)hComp[idx];
                        float curG = (float)hComp[idx + 1];
                        float curB = (float)hComp[idx + 2];
                        float curA = (float)hComp[idx + 3];

                        float outA = Math.Clamp(finalAlpha + curA * (1.0f - finalAlpha), 0.0f, 1.0f);
                        float outR = (outA > 0.0001f) ? (linCol.R * finalAlpha + curR * curA * (1.0f - finalAlpha)) / outA : linCol.R;
                        float outG = (outA > 0.0001f) ? (linCol.G * finalAlpha + curG * curA * (1.0f - finalAlpha)) / outA : linCol.G;
                        float outB = (outA > 0.0001f) ? (linCol.B * finalAlpha + curB * curA * (1.0f - finalAlpha)) / outA : linCol.B;

                        hComp[idx] = (Half)outR;
                        hComp[idx + 1] = (Half)outG;
                        hComp[idx + 2] = (Half)outB;
                        hComp[idx + 3] = (Half)outA;
                    }
                });
            }
        }

        private static float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float l2 = ab.LengthSquared();
            if (l2 < 1e-6f) return p.DistanceTo(a);
            float t = Math.Clamp((p - a).Dot(ab) / l2, 0.0f, 1.0f);
            Vector2 proj = a + t * ab;
            return p.DistanceTo(proj);
        }
    }
}
