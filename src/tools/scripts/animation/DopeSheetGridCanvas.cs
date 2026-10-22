using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace DeadlockPlayground.Animation
{
    /// <summary>
    /// High-performance 2D Dope Sheet timeline canvas for viewing, retiming, and editing keyframes.
    /// Emulates Blender's Animation Dope Sheet workflow.
    /// </summary>
    public partial class DopeSheetGridCanvas : Control
    {
        [Signal] public delegate void PlayheadScrubbedEventHandler(double time);
        [Signal] public delegate void KeyframesModifiedEventHandler();
        [Signal] public delegate void KeyframeSelectionChangedEventHandler();
        [Signal] public delegate void VerticalScrollChangedEventHandler(float scrollY);
        [Signal] public delegate void TrackHoveredEventHandler(int trackIdx, string boneName);
        [Signal] public delegate void TrackSelectedEventHandler(int trackIdx, string boneName);

        public record TrackRowInfo(string BoneName, int PosTrackIdx, int RotTrackIdx, bool IsSelected, bool IsMuted, bool IsLocked);

        public class SelectedKeyInfo
        {
            public int TrackIdx { get; set; }
            public double Time { get; set; }
            public Variant Value { get; set; }
            public float Transition { get; set; }
        }

        // Layout Constants
        public const float RulerHeight = 28f;
        public const float SummaryRowHeight = 26f;
        public const float TrackRowHeight = 26f;
        public const float DiamondRadius = 5.5f;

        // View State
        private double _currentTime = 0.0;
        private double _duration = 1.0;
        private float _fps = 30f;
        private float _pixelsPerSecond = 140f;
        private float _scrollX = 0f;
        private float _scrollY = 0f;
        private int _hoveredTrackIdx = -1;
        private int _selectedTrackIdx = -1;

        public float ScrollY
        {
            get => _scrollY;
            set
            {
                _scrollY = Math.Max(0f, value);
                QueueRedraw();
            }
        }

        public override void _Ready()
        {
            ClipContents = true;
        }

        // Active Animation & Tracks
        private Godot.Animation _activeAnimation;
        private Skeleton3D _skeleton;
        private readonly List<TrackRowInfo> _trackRows = new();
        private readonly List<SelectedKeyInfo> _selectedKeys = new();

        // Drag & Tool States
        private enum DragMode { None, ScrubPlayhead, PanCanvas, BoxSelect, DragKeys, ScaleKeys }
        private DragMode _currentDragMode = DragMode.None;
        private Vector2 _dragStartMousePos = Vector2.Zero;
        private double _dragStartTime = 0.0;
        private Rect2 _boxSelectRect = default;
        private readonly Dictionary<SelectedKeyInfo, double> _keysInitialTimes = new();
        private double _scaleCenterTime = 0.0;

        // Undo/Redo Reference
        private AnimationUndoRedoManager _undoRedo;

        // Visual Colors (Blender Theme Palette)
        private static readonly Color ColorBg = new(0.12f, 0.13f, 0.15f, 1f);
        private static readonly Color ColorBgAlt = new(0.14f, 0.15f, 0.17f, 1f);
        private static readonly Color ColorRulerBg = new(0.16f, 0.17f, 0.20f, 1f);
        private static readonly Color ColorSummaryBg = new(0.18f, 0.19f, 0.22f, 1f);
        private static readonly Color ColorGridLine = new(0.20f, 0.22f, 0.25f, 0.7f);
        private static readonly Color ColorSecondLine = new(0.30f, 0.33f, 0.38f, 0.9f);
        private static readonly Color ColorPlayhead = new(0.95f, 0.22f, 0.25f, 1f);
        private static readonly Color ColorDiamondNormal = new(0.85f, 0.88f, 0.92f, 1f);
        private static readonly Color ColorDiamondSelected = new(1.0f, 0.72f, 0.08f, 1f); // Blender Gold
        private static readonly Color ColorDiamondSummary = new(0.40f, 0.85f, 0.95f, 1f); // Cyan
        private static readonly Color ColorDiamondSummarySelected = new(1.0f, 0.85f, 0.25f, 1f);
        private static readonly Color ColorMarquee = new(0.3f, 0.6f, 1.0f, 0.25f);
        private static readonly Color ColorMarqueeBorder = new(0.4f, 0.7f, 1.0f, 0.85f);

        public double CurrentTime => _currentTime;
        public float Fps => _fps;
        public double Duration => _duration;
        public int SelectedKeyCount => _selectedKeys.Count;

        public void SetUndoRedoManager(AnimationUndoRedoManager undoRedo)
        {
            _undoRedo = undoRedo;
        }

        public void SetAnimation(Godot.Animation anim, Skeleton3D skeleton, float fps = 30f)
        {
            _activeAnimation = anim;
            _skeleton = skeleton;
            _fps = fps > 0 ? fps : 30f;
            _duration = anim != null ? Math.Max(0.01, anim.Length) : 1.0;
            _selectedKeys.Clear();
            RebuildTrackRows();
            QueueRedraw();
        }

        public void SetPlayheadTime(double time)
        {
            _currentTime = Math.Clamp(time, 0.0, _duration);
            EnsurePlayheadVisible();
            QueueRedraw();
        }

        private string _filter = "";

        public void SetFilter(string filter)
        {
            _filter = filter?.Trim().ToLowerInvariant() ?? "";
            RebuildTrackRows();
            QueueRedraw();
        }

        public void RebuildTrackRows()
        {
            _trackRows.Clear();
            if (_skeleton == null) return;

            // Map skeleton bones to Animation tracks (1:1 with bone list)
            for (int b = 0; b < _skeleton.GetBoneCount(); b++)
            {
                string boneName = _skeleton.GetBoneName(b);
                if (!string.IsNullOrEmpty(_filter) && !boneName.ToLowerInvariant().Contains(_filter))
                {
                    continue;
                }

                int posTrack = -1;
                int rotTrack = -1;

                if (_activeAnimation != null)
                {
                    for (int t = 0; t < _activeAnimation.GetTrackCount(); t++)
                    {
                        string path = _activeAnimation.TrackGetPath(t).ToString();
                        if (!path.EndsWith($":{boneName}", StringComparison.OrdinalIgnoreCase)) continue;

                        var type = _activeAnimation.TrackGetType(t);
                        if (type == Godot.Animation.TrackType.Position3D) posTrack = t;
                        else if (type == Godot.Animation.TrackType.Rotation3D) rotTrack = t;
                    }
                }

                // Add row for every bone so timeline rows match bone buttons 1-to-1
                _trackRows.Add(new TrackRowInfo(boneName, posTrack, rotTrack, false, false, false));
            }
        }

        public void SetBoneSelection(string boneName, bool isSelected)
        {
            for (int i = 0; i < _trackRows.Count; i++)
            {
                if (_trackRows[i].BoneName.Equals(boneName, StringComparison.OrdinalIgnoreCase))
                {
                    _trackRows[i] = _trackRows[i] with { IsSelected = isSelected };
                }
            }
            QueueRedraw();
        }

        public void SelectAllKeys()
        {
            _selectedKeys.Clear();
            if (_activeAnimation == null) return;

            foreach (var row in _trackRows)
            {
                AddTrackKeysToSelection(row.PosTrackIdx);
                AddTrackKeysToSelection(row.RotTrackIdx);
            }
            EmitSignal(SignalName.KeyframeSelectionChanged);
            QueueRedraw();
        }

        public void DeselectAllKeys()
        {
            _selectedKeys.Clear();
            EmitSignal(SignalName.KeyframeSelectionChanged);
            QueueRedraw();
        }

        private void AddTrackKeysToSelection(int trackIdx)
        {
            if (trackIdx < 0 || trackIdx >= _activeAnimation.GetTrackCount()) return;
            int keyCount = _activeAnimation.TrackGetKeyCount(trackIdx);
            for (int k = 0; k < keyCount; k++)
            {
                double time = _activeAnimation.TrackGetKeyTime(trackIdx, k);
                var val = _activeAnimation.TrackGetKeyValue(trackIdx, k);
                float trans = _activeAnimation.TrackGetKeyTransition(trackIdx, k);
                _selectedKeys.Add(new SelectedKeyInfo
                {
                    TrackIdx = trackIdx,
                    Time = time,
                    Value = val,
                    Transition = trans
                });
            }
        }

        public void DeleteSelectedKeys()
        {
            if (_activeAnimation == null || _selectedKeys.Count == 0) return;

            var list = _selectedKeys.Select(k => (k.TrackIdx, k.Time, k.Value, k.Transition)).ToList();
            var cmd = new DeleteKeyframesCommand(_activeAnimation, list);
            if (_undoRedo != null)
            {
                _undoRedo.Execute(cmd);
            }
            else
            {
                cmd.Execute();
            }

            _selectedKeys.Clear();
            EmitSignal(SignalName.KeyframesModified);
            EmitSignal(SignalName.KeyframeSelectionChanged);
            QueueRedraw();
        }

        public void DuplicateSelectedKeys()
        {
            if (_activeAnimation == null || _selectedKeys.Count == 0) return;

            // Clones selected keys with a 1-frame offset
            double offset = 1.0 / _fps;
            var newSelection = new List<SelectedKeyInfo>();

            foreach (var k in _selectedKeys)
            {
                double newTime = Math.Clamp(k.Time + offset, 0.0, _duration);
                var cmd = new InsertKeyframeCommand(_activeAnimation, k.TrackIdx, newTime, k.Value, k.Transition);
                if (_undoRedo != null) _undoRedo.Execute(cmd); else cmd.Execute();

                newSelection.Add(new SelectedKeyInfo
                {
                    TrackIdx = k.TrackIdx,
                    Time = newTime,
                    Value = k.Value,
                    Transition = k.Transition
                });
            }

            _selectedKeys.Clear();
            _selectedKeys.AddRange(newSelection);

            EmitSignal(SignalName.KeyframesModified);
            EmitSignal(SignalName.KeyframeSelectionChanged);
            QueueRedraw();
        }

        public override void _Draw()
        {
            var size = Size;
            if (size.X <= 0 || size.Y <= 0) return;

            // 1. Background
            DrawRect(new Rect2(Vector2.Zero, size), ColorBg);

            // 2. Timeline Grid & Frame Markers
            DrawTimelineGrid(size);

            // 3. Track Rows & Diamonds
            DrawTrackRows(size);

            // 4. Master Summary Row
            DrawMasterSummaryRow(size);

            // 5. Top Ruler (Header)
            DrawRuler(size);

            // 6. Playhead (Vertical Red Line)
            DrawPlayhead(size);

            // 7. Marquee Selection Box
            if (_currentDragMode == DragMode.BoxSelect && _boxSelectRect.Size.Length() > 2f)
            {
                DrawRect(_boxSelectRect, ColorMarquee);
                DrawRect(_boxSelectRect, ColorMarqueeBorder, filled: false, width: 1.5f);
            }
        }

        private void DrawTimelineGrid(Vector2 size)
        {
            float contentHeight = size.Y;
            double visibleStartTime = Math.Max(0.0, TimeFromX(0));
            double visibleEndTime = Math.Max(0.0, TimeFromX(size.X));

            int startFrame = (int)Math.Floor(visibleStartTime * _fps);
            int endFrame = (int)Math.Ceiling(visibleEndTime * _fps) + 1;

            for (int f = startFrame; f <= endFrame; f++)
            {
                double t = f / (double)_fps;
                float x = XFromTime(t);
                if (x < 0 || x > size.X) continue;

                bool isSecond = (f % (int)Math.Round(_fps)) == 0;
                Color col = isSecond ? ColorSecondLine : ColorGridLine;
                float width = isSecond ? 1.5f : 1.0f;

                DrawLine(new Vector2(x, RulerHeight), new Vector2(x, contentHeight), col, width);
            }
        }

        private void DrawRuler(Vector2 size)
        {
            DrawRect(new Rect2(0, 0, size.X, RulerHeight), ColorRulerBg);
            DrawLine(new Vector2(0, RulerHeight), new Vector2(size.X, RulerHeight), ColorSecondLine, 1.0f);

            var font = ThemeDB.FallbackFont;
            int fontSize = 11;

            double visibleStartTime = Math.Max(0.0, TimeFromX(0));
            double visibleEndTime = Math.Max(0.0, TimeFromX(size.X));

            int startFrame = (int)Math.Floor(visibleStartTime * _fps);
            int endFrame = (int)Math.Ceiling(visibleEndTime * _fps) + 1;

            int step = (_pixelsPerSecond > 200f) ? 5 : ((_pixelsPerSecond > 80f) ? 10 : 30);

            for (int f = startFrame; f <= endFrame; f++)
            {
                if (f < 0) continue;
                double t = f / (double)_fps;
                float x = XFromTime(t);
                if (x < 0 || x > size.X) continue;

                bool isMajor = (f % step == 0);
                float tickH = isMajor ? 10f : 5f;
                DrawLine(new Vector2(x, RulerHeight - tickH), new Vector2(x, RulerHeight), ColorDiamondNormal, 1.0f);

                if (isMajor)
                {
                    string label = f.ToString();
                    DrawString(font, new Vector2(x + 3, RulerHeight - 12), label, HorizontalAlignment.Left, -1, fontSize, ColorDiamondNormal);
                }
            }
        }

        private void DrawMasterSummaryRow(Vector2 size)
        {
            float y = RulerHeight;
            DrawRect(new Rect2(0, y, size.X, SummaryRowHeight), ColorSummaryBg);
            DrawLine(new Vector2(0, y + SummaryRowHeight), new Vector2(size.X, y + SummaryRowHeight), ColorGridLine, 1.0f);

            if (_activeAnimation == null) return;

            // Collect all unique frame times across all tracks
            var summaryTimes = new HashSet<double>();
            for (int t = 0; t < _activeAnimation.GetTrackCount(); t++)
            {
                int kCount = _activeAnimation.TrackGetKeyCount(t);
                for (int k = 0; k < kCount; k++)
                {
                    summaryTimes.Add(Math.Round(_activeAnimation.TrackGetKeyTime(t, k), 4));
                }
            }

            float centerY = y + SummaryRowHeight * 0.5f;
            foreach (double t in summaryTimes)
            {
                float x = XFromTime(t);
                if (x < -10 || x > size.X + 10) continue;

                bool anySelected = _selectedKeys.Any(k => Math.Abs(k.Time - t) < 0.001);
                Color col = anySelected ? ColorDiamondSummarySelected : ColorDiamondSummary;
                DrawDiamond(new Vector2(x, centerY), DiamondRadius + 1f, col);
            }
        }

        private void DrawTrackRows(Vector2 size)
        {
            float startY = RulerHeight + SummaryRowHeight - _scrollY;

            for (int i = 0; i < _trackRows.Count; i++)
            {
                float rowY = startY + i * TrackRowHeight;
                if (rowY + TrackRowHeight < RulerHeight + SummaryRowHeight || rowY > size.Y) continue;

                var row = _trackRows[i];
                Color bgCol = (i % 2 == 0) ? ColorBg : ColorBgAlt;
                if (row.IsSelected || i == _selectedTrackIdx)
                {
                    bgCol = bgCol.Lerp(new Color(0.25f, 0.45f, 0.75f, 1f), 0.40f);
                }
                else if (i == _hoveredTrackIdx)
                {
                    bgCol = bgCol.Lerp(new Color(1f, 1f, 1f, 1f), 0.12f);
                }

                DrawRect(new Rect2(0, rowY, size.X, TrackRowHeight), bgCol);
                DrawLine(new Vector2(0, rowY + TrackRowHeight), new Vector2(size.X, rowY + TrackRowHeight), ColorGridLine * 0.5f, 1.0f);

                float centerY = rowY + TrackRowHeight * 0.5f;

                // Draw Position keys
                DrawTrackKeys(row.PosTrackIdx, centerY, size.X);
                // Draw Rotation keys
                DrawTrackKeys(row.RotTrackIdx, centerY, size.X);
            }
        }

        private void DrawTrackKeys(int trackIdx, float centerY, float width)
        {
            if (_activeAnimation == null || trackIdx < 0 || trackIdx >= _activeAnimation.GetTrackCount()) return;

            int kCount = _activeAnimation.TrackGetKeyCount(trackIdx);
            for (int k = 0; k < kCount; k++)
            {
                double t = _activeAnimation.TrackGetKeyTime(trackIdx, k);
                float x = XFromTime(t);
                if (x < -10 || x > width + 10) continue;

                bool isSelected = _selectedKeys.Any(sel => sel.TrackIdx == trackIdx && Math.Abs(sel.Time - t) < 0.001);
                Color col = isSelected ? ColorDiamondSelected : ColorDiamondNormal;
                DrawDiamond(new Vector2(x, centerY), DiamondRadius, col);
            }
        }

        private void DrawDiamond(Vector2 center, float radius, Color color)
        {
            Vector2[] points = [
                center + new Vector2(0, -radius),
                center + new Vector2(radius, 0),
                center + new Vector2(0, radius),
                center + new Vector2(-radius, 0)
            ];
            DrawColoredPolygon(points, color);
            DrawPolyline(points.Concat([points[0]]).ToArray(), color.Darkened(0.4f), 1.2f);
        }

        private void DrawPlayhead(Vector2 size)
        {
            float x = XFromTime(_currentTime);
            if (x < 0 || x > size.X) return;

            // Top Scrubber Arrow / Handle
            Vector2[] arrow = [
                new Vector2(x - 6, 0),
                new Vector2(x + 6, 0),
                new Vector2(x + 6, RulerHeight - 8),
                new Vector2(x, RulerHeight),
                new Vector2(x - 6, RulerHeight - 8)
            ];
            DrawColoredPolygon(arrow, ColorPlayhead);

            // Vertical Line down through entire timeline
            DrawLine(new Vector2(x, RulerHeight), new Vector2(x, size.Y), ColorPlayhead, 1.8f);
        }

        public override void _GuiInput(InputEvent @event)
        {
            if (@event is InputEventMouseButton mb)
            {
                HandleMouseButton(mb);
            }
            else if (@event is InputEventMouseMotion mm)
            {
                HandleMouseMotion(mm);
            }
            else if (@event is InputEventKey key && key.Pressed)
            {
                HandleKeyboard(key);
            }
        }

        private void HandleMouseButton(InputEventMouseButton mb)
        {
            Vector2 pos = mb.Position;

            if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    if (pos.Y <= RulerHeight)
                    {
                        // Scrubbing in ruler
                        _currentDragMode = DragMode.ScrubPlayhead;
                        SetPlayheadFromMouseX(pos.X);
                    }
                    else if (pos.Y <= RulerHeight + SummaryRowHeight)
                    {
                        // Click in master summary row
                        HandleSummaryRowClick(pos, mb.ShiftPressed);
                    }
                    else
                    {
                        // Click in track rows: test for diamond hit
                        var hitKey = HitTestKey(pos);
                        if (hitKey != null)
                        {
                            if (!mb.ShiftPressed && !_selectedKeys.Any(k => k.TrackIdx == hitKey.TrackIdx && Math.Abs(k.Time - hitKey.Time) < 0.001))
                            {
                                _selectedKeys.Clear();
                            }

                            if (mb.ShiftPressed && _selectedKeys.Any(k => k.TrackIdx == hitKey.TrackIdx && Math.Abs(k.Time - hitKey.Time) < 0.001))
                            {
                                _selectedKeys.RemoveAll(k => k.TrackIdx == hitKey.TrackIdx && Math.Abs(k.Time - hitKey.Time) < 0.001);
                            }
                            else
                            {
                                _selectedKeys.Add(hitKey);
                            }

                            EmitSignal(SignalName.KeyframeSelectionChanged);

                            // Start dragging keys
                            _currentDragMode = DragMode.DragKeys;
                            _dragStartMousePos = pos;
                            _dragStartTime = TimeFromX(pos.X);
                            _keysInitialTimes.Clear();
                            foreach (var k in _selectedKeys)
                            {
                                _keysInitialTimes[k] = k.Time;
                            }
                            QueueRedraw();
                        }
                        else
                        {
                            // Empty space: Box / Marquee selection
                            if (!mb.ShiftPressed) _selectedKeys.Clear();
                            if (pos.Y >= RulerHeight + SummaryRowHeight)
                            {
                                float relY = pos.Y - (RulerHeight + SummaryRowHeight) + _scrollY;
                                int trackIdx = (int)Math.Floor(relY / TrackRowHeight);
                                if (trackIdx >= 0 && trackIdx < _trackRows.Count)
                                {
                                    _selectedTrackIdx = trackIdx;
                                    EmitSignal(SignalName.TrackSelected, trackIdx, _trackRows[trackIdx].BoneName);
                                }
                            }
                            _currentDragMode = DragMode.BoxSelect;
                            _dragStartMousePos = pos;
                            _boxSelectRect = new Rect2(pos, Vector2.Zero);
                            QueueRedraw();
                        }
                    }
                }
                else
                {
                    // Left Release: commit operations
                    if (_currentDragMode == DragMode.DragKeys)
                    {
                        CommitKeyMoves();
                    }
                    else if (_currentDragMode == DragMode.BoxSelect)
                    {
                        CommitBoxSelection();
                    }
                    _currentDragMode = DragMode.None;
                    QueueRedraw();
                }
            }
            else if (mb.ButtonIndex == MouseButton.Middle)
            {
                if (mb.Pressed)
                {
                    _currentDragMode = DragMode.PanCanvas;
                    _dragStartMousePos = pos;
                }
                else
                {
                    _currentDragMode = DragMode.None;
                }
            }
            else if (mb.ButtonIndex == MouseButton.WheelUp)
            {
                if (mb.CtrlPressed)
                {
                    ZoomAt(pos.X, 1.15f);
                }
                else if (mb.ShiftPressed)
                {
                    _scrollX = Math.Max(0f, _scrollX - 40f);
                    QueueRedraw();
                }
                else
                {
                    _scrollY = Math.Max(0f, _scrollY - TrackRowHeight * 2f);
                    EmitSignal(SignalName.VerticalScrollChanged, _scrollY);
                    QueueRedraw();
                }
            }
            else if (mb.ButtonIndex == MouseButton.WheelDown)
            {
                if (mb.CtrlPressed)
                {
                    ZoomAt(pos.X, 1.0f / 1.15f);
                }
                else if (mb.ShiftPressed)
                {
                    _scrollX += 40f;
                    QueueRedraw();
                }
                else
                {
                    _scrollY += TrackRowHeight * 2f;
                    EmitSignal(SignalName.VerticalScrollChanged, _scrollY);
                    QueueRedraw();
                }
            }
        }

        private void HandleMouseMotion(InputEventMouseMotion mm)
        {
            Vector2 pos = mm.Position;

            if (_currentDragMode == DragMode.ScrubPlayhead)
            {
                SetPlayheadFromMouseX(pos.X);
            }
            else if (_currentDragMode == DragMode.PanCanvas)
            {
                _scrollX = Math.Max(0f, _scrollX - mm.Relative.X);
                _scrollY = Math.Max(0f, _scrollY - mm.Relative.Y);
                EmitSignal(SignalName.VerticalScrollChanged, _scrollY);
                QueueRedraw();
            }
            else if (_currentDragMode == DragMode.BoxSelect)
            {
                _boxSelectRect = new Rect2(
                    Math.Min(_dragStartMousePos.X, pos.X),
                    Math.Min(_dragStartMousePos.Y, pos.Y),
                    Math.Abs(pos.X - _dragStartMousePos.X),
                    Math.Abs(pos.Y - _dragStartMousePos.Y)
                );
                QueueRedraw();
            }
            else if (_currentDragMode == DragMode.DragKeys && _activeAnimation != null)
            {
                double currentMouseT = TimeFromX(pos.X);
                double deltaT = currentMouseT - _dragStartTime;

                // Frame snapping
                double frameSnap = 1.0 / _fps;
                deltaT = Math.Round(deltaT / frameSnap) * frameSnap;

                foreach (var k in _selectedKeys)
                {
                    if (_keysInitialTimes.TryGetValue(k, out double origT))
                    {
                        double targetT = Math.Clamp(origT + deltaT, 0.0, _duration);
                        int curIdx = _activeAnimation.TrackFindKey(k.TrackIdx, k.Time, Godot.Animation.FindMode.Exact);
                        if (curIdx != -1)
                        {
                            _activeAnimation.TrackSetKeyTime(k.TrackIdx, curIdx, targetT);
                            k.Time = targetT;
                        }
                    }
                }
                QueueRedraw();
            }
            else if (_currentDragMode == DragMode.None && pos.Y >= RulerHeight + SummaryRowHeight)
            {
                float relY = pos.Y - (RulerHeight + SummaryRowHeight) + _scrollY;
                int trackIdx = (int)Math.Floor(relY / TrackRowHeight);
                if (trackIdx >= 0 && trackIdx < _trackRows.Count)
                {
                    if (_hoveredTrackIdx != trackIdx)
                    {
                        _hoveredTrackIdx = trackIdx;
                        EmitSignal(SignalName.TrackHovered, trackIdx, _trackRows[trackIdx].BoneName);
                        QueueRedraw();
                    }
                }
                else if (_hoveredTrackIdx != -1)
                {
                    _hoveredTrackIdx = -1;
                    EmitSignal(SignalName.TrackHovered, -1, "");
                    QueueRedraw();
                }
            }
        }

        private void HandleKeyboard(InputEventKey key)
        {
            if (key.Keycode == Key.Delete || key.Keycode == Key.X)
            {
                DeleteSelectedKeys();
                GetViewport().SetInputAsHandled();
            }
            else if (key.Keycode == Key.D && key.ShiftPressed)
            {
                DuplicateSelectedKeys();
                GetViewport().SetInputAsHandled();
            }
            else if (key.Keycode == Key.A)
            {
                if (key.CtrlPressed || _selectedKeys.Count == 0) SelectAllKeys();
                else DeselectAllKeys();
                GetViewport().SetInputAsHandled();
            }
        }

        private SelectedKeyInfo HitTestKey(Vector2 mousePos)
        {
            if (_activeAnimation == null) return null;
            float startY = RulerHeight + SummaryRowHeight - _scrollY;

            for (int i = 0; i < _trackRows.Count; i++)
            {
                float rowY = startY + i * TrackRowHeight;
                if (mousePos.Y < rowY || mousePos.Y > rowY + TrackRowHeight) continue;

                var row = _trackRows[i];
                float centerY = rowY + TrackRowHeight * 0.5f;

                int[] tracks = [row.PosTrackIdx, row.RotTrackIdx];
                foreach (int t in tracks)
                {
                    if (t < 0 || t >= _activeAnimation.GetTrackCount()) continue;
                    int kCount = _activeAnimation.TrackGetKeyCount(t);
                    for (int k = 0; k < kCount; k++)
                    {
                        double time = _activeAnimation.TrackGetKeyTime(t, k);
                        float x = XFromTime(time);
                        if (mousePos.DistanceTo(new Vector2(x, centerY)) <= DiamondRadius + 3f)
                        {
                            return new SelectedKeyInfo
                            {
                                TrackIdx = t,
                                Time = time,
                                Value = _activeAnimation.TrackGetKeyValue(t, k),
                                Transition = _activeAnimation.TrackGetKeyTransition(t, k)
                            };
                        }
                    }
                }
            }
            return null;
        }

        private void HandleSummaryRowClick(Vector2 pos, bool isShift)
        {
            if (_activeAnimation == null) return;
            double clickT = TimeFromX(pos.X);
            double frameSnap = 1.0 / _fps;
            double snappedT = Math.Round(clickT / frameSnap) * frameSnap;

            // Find all keys across all tracks at this frame
            var keysAtFrame = new List<SelectedKeyInfo>();
            for (int t = 0; t < _activeAnimation.GetTrackCount(); t++)
            {
                int kIdx = _activeAnimation.TrackFindKey(t, snappedT, Godot.Animation.FindMode.Approx);
                if (kIdx != -1)
                {
                    double actualT = _activeAnimation.TrackGetKeyTime(t, kIdx);
                    if (Math.Abs(actualT - snappedT) < frameSnap * 0.5)
                    {
                        keysAtFrame.Add(new SelectedKeyInfo
                        {
                            TrackIdx = t,
                            Time = actualT,
                            Value = _activeAnimation.TrackGetKeyValue(t, kIdx),
                            Transition = _activeAnimation.TrackGetKeyTransition(t, kIdx)
                        });
                    }
                }
            }

            if (keysAtFrame.Count > 0)
            {
                if (!isShift) _selectedKeys.Clear();
                _selectedKeys.AddRange(keysAtFrame);
                _currentDragMode = DragMode.DragKeys;
                _dragStartMousePos = pos;
                _dragStartTime = snappedT;
                _keysInitialTimes.Clear();
                foreach (var k in _selectedKeys) _keysInitialTimes[k] = k.Time;
                EmitSignal(SignalName.KeyframeSelectionChanged);
                QueueRedraw();
            }
            else
            {
                SetPlayheadFromMouseX(pos.X);
            }
        }

        private void CommitKeyMoves()
        {
            if (_activeAnimation == null || _undoRedo == null || _keysInitialTimes.Count == 0) return;

            var moves = new List<(int TrackIdx, double OldTime, double NewTime)>();
            foreach (var k in _selectedKeys)
            {
                if (_keysInitialTimes.TryGetValue(k, out double origT) && Math.Abs(k.Time - origT) > 0.0001)
                {
                    moves.Add((k.TrackIdx, origT, k.Time));
                }
            }

            if (moves.Count > 0)
            {
                var cmd = new MoveKeyframesCommand(_activeAnimation, moves);
                _undoRedo.Execute(cmd);
                EmitSignal(SignalName.KeyframesModified);
            }
            _keysInitialTimes.Clear();
        }

        private void CommitBoxSelection()
        {
            if (_activeAnimation == null) return;
            float startY = RulerHeight + SummaryRowHeight - _scrollY;

            for (int i = 0; i < _trackRows.Count; i++)
            {
                float rowY = startY + i * TrackRowHeight;
                var row = _trackRows[i];
                float centerY = rowY + TrackRowHeight * 0.5f;

                int[] tracks = [row.PosTrackIdx, row.RotTrackIdx];
                foreach (int t in tracks)
                {
                    if (t < 0 || t >= _activeAnimation.GetTrackCount()) continue;
                    int kCount = _activeAnimation.TrackGetKeyCount(t);
                    for (int k = 0; k < kCount; k++)
                    {
                        double time = _activeAnimation.TrackGetKeyTime(t, k);
                        float x = XFromTime(time);
                        if (_boxSelectRect.HasPoint(new Vector2(x, centerY)))
                        {
                            if (!_selectedKeys.Any(s => s.TrackIdx == t && Math.Abs(s.Time - time) < 0.001))
                            {
                                _selectedKeys.Add(new SelectedKeyInfo
                                {
                                    TrackIdx = t,
                                    Time = time,
                                    Value = _activeAnimation.TrackGetKeyValue(t, k),
                                    Transition = _activeAnimation.TrackGetKeyTransition(t, k)
                                });
                            }
                        }
                    }
                }
            }
            EmitSignal(SignalName.KeyframeSelectionChanged);
        }

        private void SetPlayheadFromMouseX(float mouseX)
        {
            double t = Math.Clamp(TimeFromX(mouseX), 0.0, _duration);
            // Snap to frame
            double frameSnap = 1.0 / _fps;
            t = Math.Round(t / frameSnap) * frameSnap;
            _currentTime = t;
            EmitSignal(SignalName.PlayheadScrubbed, _currentTime);
            QueueRedraw();
        }

        private void ZoomAt(float mouseX, float factor)
        {
            double timeAtMouse = TimeFromX(mouseX);
            _pixelsPerSecond = Mathf.Clamp(_pixelsPerSecond * factor, 30f, 1200f);
            float newXAtMouse = (float)(timeAtMouse * _pixelsPerSecond);
            _scrollX = Math.Max(0f, newXAtMouse - mouseX);
            QueueRedraw();
        }

        private void EnsurePlayheadVisible()
        {
            float playheadX = XFromTime(_currentTime);
            if (playheadX < 0)
            {
                _scrollX = (float)(_currentTime * _pixelsPerSecond) - 40f;
            }
            else if (playheadX > Size.X)
            {
                _scrollX = (float)(_currentTime * _pixelsPerSecond) - Size.X + 80f;
            }
            _scrollX = Math.Max(0f, _scrollX);
        }

        public float XFromTime(double time) => (float)(time * _pixelsPerSecond) - _scrollX;
        public double TimeFromX(float x) => (x + _scrollX) / _pixelsPerSecond;

        public void SelectTrackByBone(string boneName)
        {
            _selectedTrackIdx = -1;
            if (!string.IsNullOrEmpty(boneName))
            {
                for (int i = 0; i < _trackRows.Count; i++)
                {
                    if (_trackRows[i].BoneName.Equals(boneName, StringComparison.OrdinalIgnoreCase))
                    {
                        _selectedTrackIdx = i;
                        break;
                    }
                }
            }
            QueueRedraw();
        }

        public void SetHoveredTrackByBone(string boneName)
        {
            _hoveredTrackIdx = -1;
            if (!string.IsNullOrEmpty(boneName))
            {
                for (int i = 0; i < _trackRows.Count; i++)
                {
                    if (_trackRows[i].BoneName.Equals(boneName, StringComparison.OrdinalIgnoreCase))
                    {
                        _hoveredTrackIdx = i;
                        break;
                    }
                }
            }
            QueueRedraw();
        }
    }
}
