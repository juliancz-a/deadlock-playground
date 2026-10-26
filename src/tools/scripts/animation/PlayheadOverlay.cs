using System;
using Godot;

namespace DeadlockPlayground.Animation
{
    /// <summary>
    /// Dedicated lightweight overlay Control for rendering the animation playhead.
    /// Prevents redrawing the entire timeline canvas and all its keyframes every frame during playback.
    /// </summary>
    public partial class PlayheadOverlay : Control
    {
        private float _playheadX = -1f;
        private float _topY = 0f;
        private Color _color = new(0.95f, 0.22f, 0.25f, 1f); // Vibrant red

        private readonly Vector2[] _handlePoly = new Vector2[5];

        public float PlayheadX
        {
            get => _playheadX;
            set
            {
                if (Math.Abs(_playheadX - value) > 0.25f)
                {
                    _playheadX = value;
                    QueueRedraw();
                }
            }
        }

        public float TopY
        {
            get => _topY;
            set
            {
                _topY = value;
                QueueRedraw();
            }
        }

        public Color PlayheadColor
        {
            get => _color;
            set
            {
                _color = value;
                QueueRedraw();
            }
        }

        public override void _Ready()
        {
            MouseFilter = MouseFilterEnum.Ignore;
            SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        }

        public override void _Draw()
        {
            var size = Size;
            if (size.X <= 0 || size.Y <= 0 || _playheadX < 0 || _playheadX > size.X) return;

            float x = _playheadX;

            // 1. Playhead Vertical Line
            DrawLine(new Vector2(x, _topY), new Vector2(x, size.Y), _color, 1.5f);

            // 2. Playhead Scrubber Top Handle (Blender-style polygon marker)
            float handleW = 6f;
            float handleH = 12f;
            _handlePoly[0] = new Vector2(x - handleW, _topY);
            _handlePoly[1] = new Vector2(x + handleW, _topY);
            _handlePoly[2] = new Vector2(x + handleW, _topY + handleH - 4f);
            _handlePoly[3] = new Vector2(x, _topY + handleH);
            _handlePoly[4] = new Vector2(x - handleW, _topY + handleH - 4f);

            DrawColoredPolygon(_handlePoly, _color);
            DrawPolyline(_handlePoly, Colors.White, 1.0f);
        }
    }
}
