using System;
using System.Collections.Generic;
using Godot;

namespace DeadlockPlayground.Animation
{
    /// <summary>
    /// High-performance virtualized canvas for the Dope Sheet bone row list.
    /// Replaces hundreds of individual Button nodes with a single drawing pass.
    /// </summary>
    public partial class BoneListCanvas : Control
    {
        [Signal] public delegate void BoneSelectedEventHandler(string boneName);
        [Signal] public delegate void BoneHoveredEventHandler(string boneName);
        [Signal] public delegate void ScrollChangedEventHandler(float scrollY);

        public const float TrackRowHeight = 26f;

        private readonly List<string> _bones = new();
        private string _selectedBone = "";
        private string _hoveredBone = "";
        private float _scrollY = 0f;
        private Font _font;
        private int _fontSize = 13;

        private static readonly Color ColorNormalText = new(0.82f, 0.86f, 0.90f, 0.85f);
        private static readonly Color ColorSelectedText = Colors.White;
        private static readonly Color ColorHoverText = Colors.White;
        private static readonly Color ColorSelectedBg = new(0.20f, 0.40f, 0.70f, 0.90f);
        private static readonly Color ColorSelectedBorder = new(0.40f, 0.85f, 1.0f, 1.0f);
        private static readonly Color ColorHoverBg = new(1f, 1f, 1f, 0.08f);

        public float TotalContentHeight => _bones.Count * TrackRowHeight;
        public int BoneCount => _bones.Count;

        public float ScrollY
        {
            get => _scrollY;
            set
            {
                float clamped = Math.Max(0f, value);
                if (Math.Abs(_scrollY - clamped) > 0.5f)
                {
                    _scrollY = clamped;
                    QueueRedraw();
                }
            }
        }

        public string SelectedBone
        {
            get => _selectedBone;
            set
            {
                if (_selectedBone != value)
                {
                    _selectedBone = value ?? "";
                    QueueRedraw();
                }
            }
        }

        public string HoveredBone
        {
            get => _hoveredBone;
            set
            {
                if (_hoveredBone != value)
                {
                    _hoveredBone = value ?? "";
                    QueueRedraw();
                }
            }
        }

        public override void _Ready()
        {
            ClipContents = true;
            MouseFilter = MouseFilterEnum.Stop;
            _font = PlaygroundThemeHelper.GetFontColus() ?? ThemeDB.FallbackFont;
        }

        public void SetBones(IEnumerable<string> bones)
        {
            _bones.Clear();
            if (bones != null)
            {
                _bones.AddRange(bones);
            }
            float maxScroll = Math.Max(0f, _bones.Count * TrackRowHeight - Size.Y);
            if (_scrollY > maxScroll)
            {
                _scrollY = maxScroll;
            }
            QueueRedraw();
        }

        public override void _Draw()
        {
            var size = Size;
            if (size.X <= 0 || size.Y <= 0 || _bones.Count == 0) return;

            int firstRow = Math.Max(0, (int)Math.Floor(_scrollY / TrackRowHeight));
            int lastRow = Math.Min(_bones.Count - 1, (int)Math.Ceiling((_scrollY + size.Y) / TrackRowHeight));

            for (int i = firstRow; i <= lastRow; i++)
            {
                string boneName = _bones[i];
                float y = i * TrackRowHeight - _scrollY;
                var rect = new Rect2(0, y, size.X, TrackRowHeight);

                bool isSelected = !string.IsNullOrEmpty(_selectedBone) && _selectedBone.Equals(boneName, StringComparison.OrdinalIgnoreCase);
                bool isHovered = !string.IsNullOrEmpty(_hoveredBone) && _hoveredBone.Equals(boneName, StringComparison.OrdinalIgnoreCase);

                if (isSelected)
                {
                    DrawRect(rect, ColorSelectedBg);
                    DrawRect(new Rect2(0, y, 3, TrackRowHeight), ColorSelectedBorder);
                }
                else if (isHovered)
                {
                    DrawRect(rect, ColorHoverBg);
                }

                Color textCol = isSelected ? ColorSelectedText : (isHovered ? ColorHoverText : ColorNormalText);
                DrawString(_font, new Vector2(10, y + 17), boneName, HorizontalAlignment.Left, (int)size.X - 16, _fontSize, textCol);
            }
        }

        public override void _GuiInput(InputEvent @event)
        {
            if (@event is InputEventMouseMotion mm)
            {
                float totalY = mm.Position.Y + _scrollY;
                int row = (int)Math.Floor(totalY / TrackRowHeight);
                if (row >= 0 && row < _bones.Count)
                {
                    string target = _bones[row];
                    if (_hoveredBone != target)
                    {
                        _hoveredBone = target;
                        QueueRedraw();
                        EmitSignal(SignalName.BoneHovered, target);
                    }
                }
                else if (!string.IsNullOrEmpty(_hoveredBone))
                {
                    _hoveredBone = "";
                    QueueRedraw();
                    EmitSignal(SignalName.BoneHovered, "");
                }
            }
            else if (@event is InputEventMouseButton mb && mb.Pressed)
            {
                if (mb.ButtonIndex == MouseButton.WheelUp)
                {
                    float maxScroll = Math.Max(0f, _bones.Count * TrackRowHeight - Size.Y);
                    ScrollY = Math.Clamp(_scrollY - TrackRowHeight * 2f, 0f, maxScroll);
                    EmitSignal(SignalName.ScrollChanged, _scrollY);
                    GetViewport().SetInputAsHandled();
                }
                else if (mb.ButtonIndex == MouseButton.WheelDown)
                {
                    float maxScroll = Math.Max(0f, _bones.Count * TrackRowHeight - Size.Y);
                    ScrollY = Math.Clamp(_scrollY + TrackRowHeight * 2f, 0f, maxScroll);
                    EmitSignal(SignalName.ScrollChanged, _scrollY);
                    GetViewport().SetInputAsHandled();
                }
                else if (mb.ButtonIndex == MouseButton.Left)
                {
                    float totalY = mb.Position.Y + _scrollY;
                    int row = (int)Math.Floor(totalY / TrackRowHeight);
                    if (row >= 0 && row < _bones.Count)
                    {
                        string target = _bones[row];
                        _selectedBone = target;
                        QueueRedraw();
                        EmitSignal(SignalName.BoneSelected, target);
                    }
                    GetViewport().SetInputAsHandled();
                }
            }
        }

        public void ScrollToBone(string boneName)
        {
            if (string.IsNullOrEmpty(boneName) || _bones.Count == 0) return;
            for (int i = 0; i < _bones.Count; i++)
            {
                if (_bones[i].Equals(boneName, StringComparison.OrdinalIgnoreCase))
                {
                    float targetY = i * TrackRowHeight;
                    float visibleH = Size.Y > 0 ? Size.Y : 200f;
                    if (targetY < _scrollY || targetY + TrackRowHeight > _scrollY + visibleH)
                    {
                        float maxScroll = Math.Max(0f, _bones.Count * TrackRowHeight - visibleH);
                        ScrollY = Math.Clamp(targetY - (visibleH * 0.5f), 0f, maxScroll);
                        EmitSignal(SignalName.ScrollChanged, _scrollY);
                    }
                    break;
                }
            }
        }
    }
}
