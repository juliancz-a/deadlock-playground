using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace DeadlockPlayground.Animation
{
    /// <summary>
    /// High-performance 2D Dope Sheet timeline canvas for viewing, retiming, and editing keyframes.
    /// Emulates Blender's Animation Dope Sheet workflow with cached key lookups, binary-search culling,
    /// hardware-batched diamond rendering, and decoupled playhead overlay.
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
                float clamped = Math.Max(0f, value);
                if (Math.Abs(_scrollY - clamped) > 0.5f)
                {
                    _scrollY = clamped;
                    QueueRedraw();
                }
            }
        }

        // Active Animation & Tracks
        private Godot.Animation _activeAnimation;
        private Skeleton3D _skeleton;
        private readonly List<TrackRowInfo> _trackRows = new();
        private readonly List<SelectedKeyInfo> _selectedKeys = new();
        private readonly HashSet<long> _selectedKeySet = new();
        private readonly HashSet<int> _selectedSummaryFrames = new();
        private readonly AnimationKeyCache _keyCache = new();

        // Drag & Tool States
        private enum DragMode { None, ScrubPlayhead, PanCanvas, BoxSelect, DragKeys, ScaleKeys }
        private DragMode _currentDragMode = DragMode.None;
        private Vector2 _dragStartMousePos = Vector2.Zero;
        private double _dragStartTime = 0.0;
        private double _dragDeltaTime = 0.0;
        private Rect2 _boxSelectRect = default;
        private readonly Dictionary<SelectedKeyInfo, double> _keysInitialTimes = new();
        private double _scaleCenterTime = 0.0;
        private float _scaleFactor = 1.0f;
        private bool _isSnappingEnabled = true;

        public bool IsSnappingEnabled { get => _isSnappingEnabled; set => _isSnappingEnabled = value; }

        // Playhead Overlay
        private PlayheadOverlay _playheadOverlay;

        // Diamond Texture Batching
        private static Texture2D _diamondTexture;

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

        public override void _Ready()
        {
            ClipContents = true;
            GetDiamondTexture(); // Ensure diamond texture is initialized

            _playheadOverlay = new PlayheadOverlay();
            _playheadOverlay.TopY = RulerHeight;
            AddChild(_playheadOverlay);
        }

        public override void _Notification(int what)
        {
            base._Notification(what);
            if (what == NotificationResized && _playheadOverlay != null)
            {
                _playheadOverlay.Size = Size;
                _playheadOverlay.PlayheadX = XFromTime(_currentTime);
            }
        }

        private static Texture2D GetDiamondTexture()
        {
            if (_diamondTexture != null) return _diamondTexture;

            int size = 16;
            var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
            float center = 7.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Math.Abs(x - center) + Math.Abs(y - center);
                    if (d <= 5.5f)
                    {
                        img.SetPixel(x, y, Colors.White);
                    }
                    else if (d <= 6.8f)
                    {
                        img.SetPixel(x, y, new Color(0.12f, 0.14f, 0.17f, 0.95f));
                    }
                }
            }

            _diamondTexture = ImageTexture.CreateFromImage(img);
            return _diamondTexture;
        }

        private static long MakeKeyId(int trackIdx, double time, float fps)
        {
            int frame = (int)Math.Round(time * fps);
            return ((long)trackIdx << 32) | (uint)frame;
        }

        private void UpdateSelectedKeySet()
        {
            _selectedKeySet.Clear();
            _selectedSummaryFrames.Clear();
            foreach (var k in _selectedKeys)
            {
                _selectedKeySet.Add(MakeKeyId(k.TrackIdx, k.Time, _fps));
                _selectedSummaryFrames.Add((int)Math.Round(k.Time * _fps));
            }
        }

        public void SetUndoRedoManager(AnimationUndoRedoManager undoRedo)
        {
            _undoRedo = undoRedo;
        }

        public void SetAnimation(Godot.Animation anim, Skeleton3D skeleton, float fps = 30f)
        {
            _activeAnimation = anim;
            _skeleton = (skeleton != null && GodotObject.IsInstanceValid(skeleton) && !skeleton.IsQueuedForDeletion()) ? skeleton : null;
            _fps = fps > 0 ? fps : 30f;
            _duration = anim != null ? Math.Max(0.01, anim.Length) : 1.0;
            _selectedKeys.Clear();
            UpdateSelectedKeySet();

            _keyCache.SetAnimation(anim, _skeleton, _fps);
            RebuildTrackRows();
            QueueRedraw();
        }

        public void SetPlayheadTime(double time)
        {
            _currentTime = Math.Clamp(time, 0.0, _duration);
            EnsurePlayheadVisible();
            if (_playheadOverlay != null)
            {
                _playheadOverlay.PlayheadX = XFromTime(_currentTime);
            }
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
            if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton) || _skeleton.IsQueuedForDeletion()) return;

            var boneTracks = _keyCache.BoneTracks;

            for (int b = 0; b < _skeleton.GetBoneCount(); b++)
            {
                string boneName = _skeleton.GetBoneName(b);
                if (!string.IsNullOrEmpty(_filter) && !boneName.ToLowerInvariant().Contains(_filter))
                {
                    continue;
                }

                int posTrack = -1;
                int rotTrack = -1;

                if (boneTracks.TryGetValue(boneName, out var pair))
                {
                    posTrack = pair.posTrack;
                    rotTrack = pair.rotTrack;
                }

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

        public IReadOnlyList<TrackRowInfo> TrackRows => _trackRows;

        public void SelectKeysForBones(ISet<string> boneNames)
        {
            _selectedKeys.Clear();
            if (_activeAnimation == null || boneNames == null || boneNames.Count == 0) return;

            for (int i = 0; i < _trackRows.Count; i++)
            {
                var row = _trackRows[i];
                bool matches = boneNames.Contains(row.BoneName);
                _trackRows[i] = row with { IsSelected = matches };
                if (matches)
                {
                    AddTrackKeysToSelection(row.PosTrackIdx);
                    AddTrackKeysToSelection(row.RotTrackIdx);
                }
            }

            UpdateSelectedKeySet();
            EmitSignal(SignalName.KeyframeSelectionChanged);
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
            UpdateSelectedKeySet();
            EmitSignal(SignalName.KeyframeSelectionChanged);
            QueueRedraw();
        }

        public void DeselectAllKeys()
        {
            _selectedKeys.Clear();
            UpdateSelectedKeySet();
            EmitSignal(SignalName.KeyframeSelectionChanged);
            QueueRedraw();
        }

        public void FrameAll()
        {
            if (_duration <= 0.0) return;
            float availWidth = Size.X;
            if (availWidth > 50f)
            {
                _pixelsPerSecond = Mathf.Clamp(availWidth / (float)_duration, 10f, 2000f);
                _scrollX = 0f;
                if (_playheadOverlay != null) _playheadOverlay.PlayheadX = XFromTime(_currentTime);
                QueueRedraw();
            }
        }

        public void FrameSelected()
        {
            if (_selectedKeys.Count == 0)
            {
                FrameAll();
                return;
            }
            double minT = _selectedKeys.Min(k => k.Time);
            double maxT = _selectedKeys.Max(k => k.Time);
            double span = Math.Max(maxT - minT, 0.2);
            float availWidth = Size.X;
            if (availWidth > 50f)
            {
                _pixelsPerSecond = Mathf.Clamp((availWidth * 0.8f) / (float)span, 10f, 2000f);
                _scrollX = Math.Max(0f, (float)minT * _pixelsPerSecond - availWidth * 0.1f);
                if (_playheadOverlay != null) _playheadOverlay.PlayheadX = XFromTime(_currentTime);
                QueueRedraw();
            }
        }

        public void StartGrabKeys()
        {
            if (_selectedKeys.Count == 0 || _activeAnimation == null) return;
            _currentDragMode = DragMode.DragKeys;
            _dragStartMousePos = GetLocalMousePosition();
            _dragStartTime = TimeFromX(_dragStartMousePos.X);
            _dragDeltaTime = 0.0;
            _keysInitialTimes.Clear();
            foreach (var k in _selectedKeys) _keysInitialTimes[k] = k.Time;
            QueueRedraw();
        }

        public void StartScaleKeys()
        {
            if (_selectedKeys.Count == 0 || _activeAnimation == null) return;
            _currentDragMode = DragMode.ScaleKeys;
            _dragStartMousePos = GetLocalMousePosition();
            _scaleCenterTime = _currentTime;
            _scaleFactor = 1.0f;
            _keysInitialTimes.Clear();
            foreach (var k in _selectedKeys) _keysInitialTimes[k] = k.Time;
            QueueRedraw();
        }

        public void StartBoxSelect()
        {
            _currentDragMode = DragMode.BoxSelect;
            _dragStartMousePos = GetLocalMousePosition();
            _boxSelectRect = new Rect2(_dragStartMousePos, Vector2.Zero);
            QueueRedraw();
        }

        private void AddTrackKeysToSelection(int trackIdx)
        {
            if (trackIdx < 0 || _activeAnimation == null || trackIdx >= _activeAnimation.GetTrackCount()) return;
            var times = _keyCache.GetTrackTimes(trackIdx);
            for (int k = 0; k < times.Length; k++)
            {
                double time = times[k];
                _selectedKeys.Add(new SelectedKeyInfo
                {
                    TrackIdx = trackIdx,
                    Time = time,
                    Value = _activeAnimation.TrackGetKeyValue(trackIdx, k),
                    Transition = _activeAnimation.TrackGetKeyTransition(trackIdx, k)
                });
            }
        }

        public void DeleteSelectedKeys()
        {
            if (_activeAnimation == null || _selectedKeys.Count == 0) return;

            var list = _selectedKeys.Select(k => (k.TrackIdx, k.Time, k.Value, k.Transition)).ToList();
            var cmd = new DeleteKeyframesCommand(_activeAnimation, list);
            if (_undoRedo != null) _undoRedo.Execute(cmd); else cmd.Execute();

            _selectedKeys.Clear();
            UpdateSelectedKeySet();
            _keyCache.InvalidateAll();
            _keyCache.Rebuild(_skeleton);

            EmitSignal(SignalName.KeyframesModified);
            EmitSignal(SignalName.KeyframeSelectionChanged);
            QueueRedraw();
        }

        public void DuplicateSelectedKeys()
        {
            if (_activeAnimation == null || _selectedKeys.Count == 0) return;

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
            UpdateSelectedKeySet();
            _keyCache.InvalidateAll();
            _keyCache.Rebuild(_skeleton);

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

            // 6. Marquee Selection Box
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

            float pixelsPerFrame = _pixelsPerSecond / _fps;
            int step = pixelsPerFrame < 4f ? (int)Math.Round(_fps) : 1;

            var gridLines = new List<Vector2>();
            var secondLines = new List<Vector2>();

            for (int f = startFrame; f <= endFrame; f++)
            {
                if (step > 1 && (f % step != 0)) continue;

                double t = f / (double)_fps;
                float x = XFromTime(t);
                if (x < 0 || x > size.X) continue;

                bool isSecond = (f % (int)Math.Round(_fps)) == 0;
                var p1 = new Vector2(x, RulerHeight);
                var p2 = new Vector2(x, contentHeight);

                if (isSecond)
                {
                    secondLines.Add(p1);
                    secondLines.Add(p2);
                }
                else
                {
                    gridLines.Add(p1);
                    gridLines.Add(p2);
                }
            }

            if (gridLines.Count > 0) DrawMultiline(gridLines.ToArray(), ColorGridLine, 1.0f);
            if (secondLines.Count > 0) DrawMultiline(secondLines.ToArray(), ColorSecondLine, 1.5f);
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

            double visibleStartTime = Math.Max(0.0, TimeFromX(0));
            double visibleEndTime = Math.Max(0.0, TimeFromX(size.X));

            var (startIdx, endIdx) = _keyCache.GetVisibleSummaryRange(visibleStartTime - 0.1, visibleEndTime + 0.1);
            if (startIdx > endIdx) return;

            float centerY = y + SummaryRowHeight * 0.5f;
            float lastDrawnSummaryX = -9999f;
            var summaryTimes = _keyCache.SummaryTimes;

            for (int i = startIdx; i <= endIdx; i++)
            {
                double t = summaryTimes[i];
                float x = XFromTime(t);

                int frame = (int)Math.Round(t * _fps);
                bool anySelected = _selectedSummaryFrames.Contains(frame);
                if (!anySelected && (x - lastDrawnSummaryX) < 3.5f) continue;

                Color col = anySelected ? ColorDiamondSummarySelected : ColorDiamondSummary;
                DrawDiamond(new Vector2(x, centerY), col);
                lastDrawnSummaryX = x;
            }
        }

        private void DrawTrackRows(Vector2 size)
        {
            float startY = RulerHeight + SummaryRowHeight - _scrollY;
            int firstRow = Math.Max(0, (int)Math.Floor(_scrollY / TrackRowHeight));
            int lastRow = Math.Min(_trackRows.Count - 1, (int)Math.Ceiling((_scrollY + size.Y - (RulerHeight + SummaryRowHeight)) / TrackRowHeight));

            double visibleStartTime = Math.Max(0.0, TimeFromX(0));
            double visibleEndTime = Math.Max(0.0, TimeFromX(size.X));

            for (int i = firstRow; i <= lastRow; i++)
            {
                float rowY = startY + i * TrackRowHeight;
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
                DrawTrackKeys(row.PosTrackIdx, centerY, visibleStartTime, visibleEndTime);
                // Draw Rotation keys
                DrawTrackKeys(row.RotTrackIdx, centerY, visibleStartTime, visibleEndTime);
            }
        }

        private void DrawTrackKeys(int trackIdx, float centerY, double minTime, double maxTime)
        {
            if (trackIdx < 0) return;

            var (startIdx, endIdx) = _keyCache.GetVisibleKeyRange(trackIdx, minTime - 0.1, maxTime + 0.1);
            if (startIdx > endIdx) return;

            var times = _keyCache.GetTrackTimes(trackIdx);
            float pixelsPerFrame = _pixelsPerSecond / _fps;
            bool drawDensityStrip = pixelsPerFrame < 3.0f;

            if (drawDensityStrip)
            {
                float startX = XFromTime(times[startIdx]);
                float endX = XFromTime(times[endIdx]);
                DrawRect(new Rect2(startX, centerY - 2f, Math.Max(2f, endX - startX), 4f), ColorDiamondNormal * 0.6f);
                return;
            }

            float lastDrawnKeyX = -9999f;
            for (int k = startIdx; k <= endIdx; k++)
            {
                double t = times[k];
                bool isSelected = _selectedKeySet.Contains(MakeKeyId(trackIdx, t, _fps));

                float drawT = (float)t;
                if (isSelected)
                {
                    if (_currentDragMode == DragMode.DragKeys)
                    {
                        drawT += (float)_dragDeltaTime;
                    }
                    else if (_currentDragMode == DragMode.ScaleKeys)
                    {
                        drawT = (float)(_scaleCenterTime + (t - _scaleCenterTime) * _scaleFactor);
                    }
                }

                float x = XFromTime(drawT);
                if (!isSelected && (x - lastDrawnKeyX) < 3.5f) continue;

                Color col = isSelected ? ColorDiamondSelected : ColorDiamondNormal;
                DrawDiamond(new Vector2(x, centerY), col);
                lastDrawnKeyX = x;
            }
        }

        private void DrawDiamond(Vector2 center, Color color)
        {
            var tex = GetDiamondTexture();
            DrawTexture(tex, center - new Vector2(8, 8), color);
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
            else if (@event is InputEventKey key && key.Pressed && !key.Echo)
            {
                HandleKeyboard(key);
            }
        }

        private void HandleMouseButton(InputEventMouseButton mb)
        {
            var pos = mb.Position;

            if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    if (pos.Y <= RulerHeight)
                    {
                        _currentDragMode = DragMode.ScrubPlayhead;
                        SetPlayheadFromMouseX(pos.X);
                    }
                    else if (pos.Y <= RulerHeight + SummaryRowHeight)
                    {
                        HandleSummaryRowClick(pos, mb.ShiftPressed);
                    }
                    else
                    {
                        var hitKey = HitTestKey(pos);
                        if (hitKey != null)
                        {
                            long hitId = MakeKeyId(hitKey.TrackIdx, hitKey.Time, _fps);
                            if (!mb.ShiftPressed && !_selectedKeySet.Contains(hitId))
                            {
                                _selectedKeys.Clear();
                            }

                            if (mb.ShiftPressed && _selectedKeySet.Contains(hitId))
                            {
                                _selectedKeys.RemoveAll(k => k.TrackIdx == hitKey.TrackIdx && Math.Abs(k.Time - hitKey.Time) < 0.001);
                            }
                            else
                            {
                                _selectedKeys.Add(hitKey);
                            }

                            UpdateSelectedKeySet();
                            EmitSignal(SignalName.KeyframeSelectionChanged);

                            _currentDragMode = DragMode.DragKeys;
                            _dragStartMousePos = pos;
                            _dragStartTime = TimeFromX(pos.X);
                            _dragDeltaTime = 0.0;
                            _keysInitialTimes.Clear();
                            foreach (var k in _selectedKeys)
                            {
                                _keysInitialTimes[k] = k.Time;
                            }
                            QueueRedraw();
                        }
                        else
                        {
                            if (!mb.ShiftPressed)
                            {
                                _selectedKeys.Clear();
                                UpdateSelectedKeySet();
                                EmitSignal(SignalName.KeyframeSelectionChanged);
                            }

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
                    if (_currentDragMode == DragMode.DragKeys)
                    {
                        CommitKeyMoves();
                    }
                    else if (_currentDragMode == DragMode.ScaleKeys)
                    {
                        CommitKeyScales();
                    }
                    else if (_currentDragMode == DragMode.BoxSelect)
                    {
                        CommitBoxSelection();
                    }
                    _currentDragMode = DragMode.None;
                    _dragDeltaTime = 0.0;
                    _scaleFactor = 1.0f;
                    QueueRedraw();
                }
            }
            else if (mb.ButtonIndex == MouseButton.Right && mb.Pressed)
            {
                if (_currentDragMode == DragMode.DragKeys || _currentDragMode == DragMode.ScaleKeys || _currentDragMode == DragMode.BoxSelect)
                {
                    _currentDragMode = DragMode.None;
                    _dragDeltaTime = 0.0;
                    _scaleFactor = 1.0f;
                    _keysInitialTimes.Clear();
                    QueueRedraw();
                    GetViewport().SetInputAsHandled();
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
                    float maxScroll = Math.Max(0f, _trackRows.Count * TrackRowHeight - (Size.Y - (RulerHeight + SummaryRowHeight)));
                    _scrollY = Math.Clamp(_scrollY - TrackRowHeight * 2f, 0f, maxScroll);
                    EmitSignal(SignalName.VerticalScrollChanged, _scrollY);
                    QueueRedraw();
                }
                GetViewport().SetInputAsHandled();
            }
            else if (mb.ButtonIndex == MouseButton.WheelDown)
            {
                if (mb.CtrlPressed)
                {
                    ZoomAt(pos.X, 1.0f / 1.15f);
                }
                else if (mb.ShiftPressed)
                {
                    _scrollX = Math.Max(0f, _scrollX + 40f);
                    QueueRedraw();
                }
                else
                {
                    float maxScroll = Math.Max(0f, _trackRows.Count * TrackRowHeight - (Size.Y - (RulerHeight + SummaryRowHeight)));
                    _scrollY = Math.Clamp(_scrollY + TrackRowHeight * 2f, 0f, maxScroll);
                    EmitSignal(SignalName.VerticalScrollChanged, _scrollY);
                    QueueRedraw();
                }
                GetViewport().SetInputAsHandled();
            }
        }

        private void HandleMouseMotion(InputEventMouseMotion mm)
        {
            var pos = mm.Position;

            if (_currentDragMode == DragMode.ScrubPlayhead)
            {
                SetPlayheadFromMouseX(pos.X);
            }
            else if (_currentDragMode == DragMode.PanCanvas)
            {
                Vector2 delta = mm.Relative;
                _scrollX = Math.Max(0f, _scrollX - delta.X);
                float maxScroll = Math.Max(0f, _trackRows.Count * TrackRowHeight - (Size.Y - (RulerHeight + SummaryRowHeight)));
                _scrollY = Math.Clamp(_scrollY - delta.Y, 0f, maxScroll);
                EmitSignal(SignalName.VerticalScrollChanged, _scrollY);
                QueueRedraw();
            }
            else if (_currentDragMode == DragMode.BoxSelect)
            {
                _boxSelectRect = new Rect2(
                    Mathf.Min(_dragStartMousePos.X, pos.X),
                    Mathf.Min(_dragStartMousePos.Y, pos.Y),
                    Mathf.Abs(pos.X - _dragStartMousePos.X),
                    Mathf.Abs(pos.Y - _dragStartMousePos.Y)
                );
                QueueRedraw();
            }
            else if (_currentDragMode == DragMode.DragKeys && _activeAnimation != null)
            {
                double currentMouseT = TimeFromX(pos.X);
                double deltaT = currentMouseT - _dragStartTime;
                if (_isSnappingEnabled)
                {
                    double frameSnap = 1.0 / _fps;
                    _dragDeltaTime = Math.Round(deltaT / frameSnap) * frameSnap;
                }
                else
                {
                    _dragDeltaTime = deltaT;
                }
                QueueRedraw();
            }
            else if (_currentDragMode == DragMode.ScaleKeys && _activeAnimation != null)
            {
                float pivotX = XFromTime(_scaleCenterTime);
                float initialDist = _dragStartMousePos.X - pivotX;
                float currentDist = pos.X - pivotX;
                float scale = Math.Abs(initialDist) > 10f ? currentDist / initialDist : 1.0f;
                _scaleFactor = Mathf.Clamp(scale, 0.05f, 20f);
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
            if (key.Keycode == Key.Escape)
            {
                if (_currentDragMode == DragMode.DragKeys || _currentDragMode == DragMode.ScaleKeys || _currentDragMode == DragMode.BoxSelect)
                {
                    _currentDragMode = DragMode.None;
                    _dragDeltaTime = 0.0;
                    _scaleFactor = 1.0f;
                    _keysInitialTimes.Clear();
                    QueueRedraw();
                    GetViewport().SetInputAsHandled();
                    return;
                }
            }

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
            float relY = mousePos.Y - (RulerHeight + SummaryRowHeight) + _scrollY;
            int rowIdx = (int)Math.Floor(relY / TrackRowHeight);
            if (rowIdx < 0 || rowIdx >= _trackRows.Count) return null;

            var row = _trackRows[rowIdx];
            float centerY = (RulerHeight + SummaryRowHeight - _scrollY) + rowIdx * TrackRowHeight + TrackRowHeight * 0.5f;
            if (Math.Abs(mousePos.Y - centerY) > DiamondRadius + 3f) return null;

            double mouseTime = TimeFromX(mousePos.X);
            double hitTolerance = (DiamondRadius + 3f) / _pixelsPerSecond;

            int[] tracks = [row.PosTrackIdx, row.RotTrackIdx];
            foreach (int t in tracks)
            {
                if (t < 0 || t >= _activeAnimation.GetTrackCount()) continue;
                var (startK, endK) = _keyCache.GetVisibleKeyRange(t, mouseTime - hitTolerance, mouseTime + hitTolerance);
                if (startK > endK) continue;

                var times = _keyCache.GetTrackTimes(t);
                for (int k = startK; k <= endK; k++)
                {
                    double time = times[k];
                    float x = XFromTime(time);
                    if (Math.Abs(mousePos.X - x) <= DiamondRadius + 3f)
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

            return null;
        }

        private void HandleSummaryRowClick(Vector2 pos, bool isShift)
        {
            if (_activeAnimation == null) return;
            double clickT = TimeFromX(pos.X);
            double frameSnap = 1.0 / _fps;
            double snappedT = Math.Round(clickT / frameSnap) * frameSnap;

            var keysAtFrame = new List<SelectedKeyInfo>();
            for (int t = 0; t < _activeAnimation.GetTrackCount(); t++)
            {
                var (startK, endK) = _keyCache.GetVisibleKeyRange(t, snappedT - frameSnap * 0.45, snappedT + frameSnap * 0.45);
                if (startK > endK) continue;
                var times = _keyCache.GetTrackTimes(t);
                for (int k = startK; k <= endK; k++)
                {
                    keysAtFrame.Add(new SelectedKeyInfo
                    {
                        TrackIdx = t,
                        Time = times[k],
                        Value = _activeAnimation.TrackGetKeyValue(t, k),
                        Transition = _activeAnimation.TrackGetKeyTransition(t, k)
                    });
                }
            }

            if (keysAtFrame.Count > 0)
            {
                if (!isShift) _selectedKeys.Clear();
                _selectedKeys.AddRange(keysAtFrame);
                UpdateSelectedKeySet();
                _currentDragMode = DragMode.DragKeys;
                _dragStartMousePos = pos;
                _dragStartTime = snappedT;
                _dragDeltaTime = 0.0;
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
            if (_activeAnimation == null || _undoRedo == null || _keysInitialTimes.Count == 0 || Math.Abs(_dragDeltaTime) < 0.0001)
            {
                _dragDeltaTime = 0.0;
                _keysInitialTimes.Clear();
                return;
            }

            var moves = new List<(int TrackIdx, double OldTime, double NewTime)>();
            foreach (var k in _selectedKeys)
            {
                if (_keysInitialTimes.TryGetValue(k, out double origT))
                {
                    double targetT = Math.Clamp(origT + _dragDeltaTime, 0.0, _duration);
                    if (Math.Abs(targetT - origT) > 0.0001)
                    {
                        moves.Add((k.TrackIdx, origT, targetT));
                        k.Time = targetT;
                    }
                }
            }

            if (moves.Count > 0)
            {
                var cmd = new MoveKeyframesCommand(_activeAnimation, moves);
                _undoRedo.Execute(cmd);
                _keyCache.InvalidateAll();
                _keyCache.Rebuild(_skeleton);
                UpdateSelectedKeySet();
                EmitSignal(SignalName.KeyframesModified);
            }

            _dragDeltaTime = 0.0;
            _keysInitialTimes.Clear();
        }

        private void CommitKeyScales()
        {
            if (_activeAnimation == null || _undoRedo == null || _keysInitialTimes.Count == 0 || Math.Abs(_scaleFactor - 1.0f) < 0.001f)
            {
                _scaleFactor = 1.0f;
                _keysInitialTimes.Clear();
                return;
            }

            var moves = new List<(int TrackIdx, double OldTime, double NewTime)>();
            double frameSnap = 1.0 / _fps;

            foreach (var k in _selectedKeys)
            {
                if (_keysInitialTimes.TryGetValue(k, out double origT))
                {
                    double scaledT = _scaleCenterTime + (origT - _scaleCenterTime) * _scaleFactor;
                    double targetT = _isSnappingEnabled ? Math.Round(scaledT / frameSnap) * frameSnap : scaledT;
                    targetT = Math.Clamp(targetT, 0.0, _duration);
                    if (Math.Abs(targetT - origT) > 0.0001)
                    {
                        moves.Add((k.TrackIdx, origT, targetT));
                        k.Time = targetT;
                    }
                }
            }

            if (moves.Count > 0)
            {
                var cmd = new MoveKeyframesCommand(_activeAnimation, moves);
                _undoRedo.Execute(cmd);
                _keyCache.InvalidateAll();
                _keyCache.Rebuild(_skeleton);
                UpdateSelectedKeySet();
                EmitSignal(SignalName.KeyframesModified);
            }

            _scaleFactor = 1.0f;
            _keysInitialTimes.Clear();
        }

        private void CommitBoxSelection()
        {
            if (_activeAnimation == null) return;
            float startY = RulerHeight + SummaryRowHeight - _scrollY;

            int firstRow = Math.Max(0, (int)Math.Floor((_boxSelectRect.Position.Y - (RulerHeight + SummaryRowHeight) + _scrollY) / TrackRowHeight));
            int lastRow = Math.Min(_trackRows.Count - 1, (int)Math.Ceiling((_boxSelectRect.End.Y - (RulerHeight + SummaryRowHeight) + _scrollY) / TrackRowHeight));

            double minT = TimeFromX(_boxSelectRect.Position.X);
            double maxT = TimeFromX(_boxSelectRect.End.X);
            if (minT > maxT) (minT, maxT) = (maxT, minT);

            for (int i = firstRow; i <= lastRow; i++)
            {
                var row = _trackRows[i];
                float centerY = startY + i * TrackRowHeight + TrackRowHeight * 0.5f;
                if (centerY < _boxSelectRect.Position.Y || centerY > _boxSelectRect.End.Y) continue;

                int[] tracks = [row.PosTrackIdx, row.RotTrackIdx];
                foreach (int t in tracks)
                {
                    if (t < 0 || t >= _activeAnimation.GetTrackCount()) continue;
                    var (startK, endK) = _keyCache.GetVisibleKeyRange(t, minT, maxT);
                    if (startK > endK) continue;

                    var times = _keyCache.GetTrackTimes(t);
                    for (int k = startK; k <= endK; k++)
                    {
                        double time = times[k];
                        long id = MakeKeyId(t, time, _fps);
                        if (!_selectedKeySet.Contains(id))
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

            UpdateSelectedKeySet();
            EmitSignal(SignalName.KeyframeSelectionChanged);
        }

        private void SetPlayheadFromMouseX(float mouseX)
        {
            double t = Math.Clamp(TimeFromX(mouseX), 0.0, _duration);
            double frameSnap = 1.0 / _fps;
            t = Math.Round(t / frameSnap) * frameSnap;
            _currentTime = t;
            if (_playheadOverlay != null)
            {
                _playheadOverlay.PlayheadX = XFromTime(_currentTime);
            }
            EmitSignal(SignalName.PlayheadScrubbed, _currentTime);
        }

        private void ZoomAt(float mouseX, float factor)
        {
            double timeAtMouse = TimeFromX(mouseX);
            _pixelsPerSecond = Mathf.Clamp(_pixelsPerSecond * factor, 30f, 1200f);
            float newXAtMouse = (float)(timeAtMouse * _pixelsPerSecond);
            _scrollX = Math.Max(0f, newXAtMouse - mouseX);
            if (_playheadOverlay != null)
            {
                _playheadOverlay.PlayheadX = XFromTime(_currentTime);
            }
            QueueRedraw();
        }

        private void EnsurePlayheadVisible()
        {
            float playheadX = XFromTime(_currentTime);
            bool scrolled = false;
            if (playheadX < 0)
            {
                _scrollX = (float)(_currentTime * _pixelsPerSecond) - 40f;
                scrolled = true;
            }
            else if (playheadX > Size.X)
            {
                _scrollX = (float)(_currentTime * _pixelsPerSecond) - Size.X + 80f;
                scrolled = true;
            }
            _scrollX = Math.Max(0f, _scrollX);
            if (scrolled)
            {
                QueueRedraw();
            }
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
