using System;
using System.Collections.Generic;
using Godot;
using DeadlockPlayground.Importer;

namespace DeadlockPlayground.UI
{
    /// <summary>
    /// Top-level 2D screen-space overlay Control rendered over the Importer's preview SubViewport
    /// for interactive Lasso polygon and Box marquee selection during Mesh Cut Mode.
    /// Declared at namespace scope to obey Godot C# GD0002 rules.
    /// </summary>
    public partial class MeshCutOverlay2D : Control
    {
        private readonly List<Vector2> _lassoPoints = new();
        private bool _isDrawingLasso;
        private bool _isDrawingBox;
        private Vector2 _boxStart;
        private Vector2 _boxCurrent;
        private MeshCutSelectOp _selectOp = MeshCutSelectOp.Replace;
        private bool _restoreMode;

        public MeshCutOverlay2D()
        {
            MouseFilter = MouseFilterEnum.Ignore;
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
            Visible = false;
        }

        public void BeginLasso(Vector2 startPos, MeshCutSelectOp op, bool restoreMode)
        {
            _lassoPoints.Clear();
            _lassoPoints.Add(startPos);
            _isDrawingLasso = true;
            _isDrawingBox = false;
            _selectOp = op;
            _restoreMode = restoreMode;
            Visible = true;
            QueueRedraw();
        }

        public void AppendLassoPoint(Vector2 pos)
        {
            if (!_isDrawingLasso) return;
            if (_lassoPoints.Count == 0 || _lassoPoints[^1].DistanceSquaredTo(pos) >= 16f)
            {
                _lassoPoints.Add(pos);
                QueueRedraw();
            }
        }

        public Vector2[] EndLasso()
        {
            var pts = _lassoPoints.ToArray();
            _lassoPoints.Clear();
            _isDrawingLasso = false;
            Visible = false;
            QueueRedraw();
            return pts;
        }

        public void BeginBox(Vector2 startPos, MeshCutSelectOp op, bool restoreMode)
        {
            _boxStart = startPos;
            _boxCurrent = startPos;
            _isDrawingBox = true;
            _isDrawingLasso = false;
            _selectOp = op;
            _restoreMode = restoreMode;
            Visible = true;
            QueueRedraw();
        }

        public void UpdateBox(Vector2 currentPos)
        {
            if (!_isDrawingBox) return;
            _boxCurrent = currentPos;
            QueueRedraw();
        }

        public Rect2 EndBox()
        {
            Rect2 r = new Rect2(_boxStart, _boxCurrent - _boxStart).Abs();
            _isDrawingBox = false;
            Visible = false;
            QueueRedraw();
            return r;
        }

        public void Cancel()
        {
            _lassoPoints.Clear();
            _isDrawingLasso = false;
            _isDrawingBox = false;
            Visible = false;
            QueueRedraw();
        }

        public override void _Draw()
        {
            Color strokeCol = _selectOp switch
            {
                MeshCutSelectOp.Subtract => new Color(0.98f, 0.32f, 0.34f, 0.96f),
                MeshCutSelectOp.Add => _restoreMode
                    ? new Color(0.22f, 0.92f, 0.98f, 0.96f)
                    : new Color(0.32f, 0.92f, 0.56f, 0.96f),
                _ => _restoreMode
                    ? new Color(0.22f, 0.90f, 0.98f, 0.96f)
                    : new Color(1.00f, 0.78f, 0.22f, 0.96f)
            };
            Color fillCol = new Color(strokeCol.R, strokeCol.G, strokeCol.B, 0.16f);

            if (_isDrawingBox)
            {
                Rect2 rect = new Rect2(_boxStart, _boxCurrent - _boxStart).Abs();
                DrawRect(rect, fillCol, filled: true);
                DrawRect(rect, strokeCol, filled: false, width: 2.0f);
            }
            else if (_isDrawingLasso && _lassoPoints.Count >= 2)
            {
                var pts = _lassoPoints.ToArray();
                if (pts.Length >= 3)
                {
                    // Guard against self-intersecting or collinear lasso polygons so Godot's canvas triangulator never logs C++ errors
                    var tris = Geometry2D.TriangulatePolygon(pts);
                    if (tris != null && tris.Length >= 3)
                    {
                        DrawColoredPolygon(pts, fillCol);
                    }
                }
                DrawPolyline(pts, strokeCol, 2.0f, antialiased: true);
                if (pts.Length >= 3)
                {
                    DrawDashedLine(pts[^1], pts[0], new Color(strokeCol.R, strokeCol.G, strokeCol.B, 0.72f), 1.5f, 6.0f);
                }
            }
        }
    }
}

