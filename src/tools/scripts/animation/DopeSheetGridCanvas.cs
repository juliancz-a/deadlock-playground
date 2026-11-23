using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using DeadlockPlayground.Tools;

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
        public const float DefaultRulerHeight = 28f;
        public const float SummaryRowHeight = 26f;
        public const float TrackRowHeight = 26f;
        public const float DiamondRadius = 5.5f;

        // Dynamic ruler height synced with BoneHeaderPanel so Row 0..N never drift vertically
        public float RulerHeight { get; private set; } = DefaultRulerHeight;

        public void SyncHeaderHeight(float totalHeaderHeight)
        {
            float clampedRuler = Math.Max(20f, totalHeaderHeight - SummaryRowHeight);
            if (Math.Abs(RulerHeight - clampedRuler) > 0.25f)
            {
                RulerHeight = clampedRuler;
                if (_playheadOverlay != null)
                {
                    _playheadOverlay.TopY = RulerHeight;
                }
                QueueRedraw();
            }
        }

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
        private bool _dragThresholdExceeded = false;
        private List<SelectedKeyInfo> _pendingSingleSelectKeys = null;
        private Rect2 _boxSelectRect = default;
        private readonly Dictionary<SelectedKeyInfo, double> _keysInitialTimes = new();
        private double _scaleCenterTime = 0.0;
        private float _scaleFactor = 1.0f;
        private bool _isSnappingEnabled = true;
        private const float DragThresholdPx = 3.0f;
        private const float KeyHitRadiusX = 10.0f;

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
            FocusMode = FocusModeEnum.Click;
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

        /// <summary>
        /// Rebuilds the internal keyframe cache from the active Godot.Animation, refreshes track rows,
        /// reconciles selected keys, and queues a redraw. Must be called after Undo/Redo or external edits.
        /// </summary>
        public void RefreshFromAnimation()
        {
            if (_activeAnimation == null || !GodotObject.IsInstanceValid(_activeAnimation))
            {
                _selectedKeys.Clear();
                UpdateSelectedKeySet();
                QueueRedraw();
                return;
            }

            _duration = Math.Max(0.01, _activeAnimation.Length);
            _keyCache.InvalidateAll();
            _keyCache.Rebuild(_skeleton);

            // Preserve selected bone name across RebuildTrackRows
            string prevSelectedBone = (_selectedTrackIdx >= 0 && _selectedTrackIdx < _trackRows.Count)
                ? _trackRows[_selectedTrackIdx].BoneName
                : null;

            RebuildTrackRows();

            if (!string.IsNullOrEmpty(prevSelectedBone))
            {
                SelectTrackByBone(prevSelectedBone);
            }

            // Reconcile _selectedKeys against the updated animation so stale keys don't linger after Undo/Redo
            if (_selectedKeys.Count > 0)
            {
                for (int i = _selectedKeys.Count - 1; i >= 0; i--)
                {
                    var sk = _selectedKeys[i];
                    if (sk.TrackIdx < 0 || sk.TrackIdx >= _activeAnimation.GetTrackCount())
                    {
                        _selectedKeys.RemoveAt(i);
                        continue;
                    }
                    int found = _activeAnimation.TrackFindKey(sk.TrackIdx, sk.Time, Godot.Animation.FindMode.Approx);
                    if (found == -1 || Math.Abs(_activeAnimation.TrackGetKeyTime(sk.TrackIdx, found) - sk.Time) > 1e-3)
                    {
                        _selectedKeys.RemoveAt(i);
                    }
                    else
                    {
                        sk.Time = _activeAnimation.TrackGetKeyTime(sk.TrackIdx, found);
                        sk.Value = _activeAnimation.TrackGetKeyValue(sk.TrackIdx, found);
                        sk.Transition = _activeAnimation.TrackGetKeyTransition(sk.TrackIdx, found);
                    }
                }
                UpdateSelectedKeySet();
            }

            QueueRedraw();
        }

        /// <summary>
        /// Lightweight O(1) cache invalidation for live Auto-Key updates during interactive gizmo/slider dragging.
        /// </summary>
        public void InvalidateTracksAndRedraw(int posTrack, int rotTrack, bool trackAdded = false)
        {
            if (trackAdded)
            {
                RefreshFromAnimation();
                return;
            }
            if (posTrack >= 0) _keyCache.InvalidateTrack(posTrack);
            if (rotTrack >= 0) _keyCache.InvalidateTrack(rotTrack);
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
            var commands = new List<IAnimationCommand>(_selectedKeys.Count);

            foreach (var k in _selectedKeys)
            {
                double newTime = Math.Clamp(k.Time + offset, 0.0, _duration);
                commands.Add(new InsertKeyframeCommand(_activeAnimation, k.TrackIdx, newTime, k.Value, k.Transition));

                newSelection.Add(new SelectedKeyInfo
                {
                    TrackIdx = k.TrackIdx,
                    Time = newTime,
                    Value = k.Value,
                    Transition = k.Transition
                });
            }

            var batch = new BatchAnimationCommand($"Duplicate {commands.Count} Keyframe(s)", commands);
            if (_undoRedo != null)
            {
                _selectedKeys.Clear();
                _selectedKeys.AddRange(newSelection);
                UpdateSelectedKeySet();
                _undoRedo.Execute(batch);
                EmitSignal(SignalName.KeyframeSelectionChanged);
            }
            else
            {
                batch.Execute();
                _selectedKeys.Clear();
                _selectedKeys.AddRange(newSelection);
                UpdateSelectedKeySet();
                _keyCache.InvalidateAll();
                _keyCache.Rebuild(_skeleton);
                EmitSignal(SignalName.KeyframesModified);
                EmitSignal(SignalName.KeyframeSelectionChanged);
                QueueRedraw();
            }
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

                // Draw unselected keys first, then selected keys on top so a selected key
                // is never hidden behind an unselected Pos/Rot diamond at the same timestamp.
                DrawTrackKeys(row.PosTrackIdx, centerY, visibleStartTime, visibleEndTime, drawSelectedPass: false);
                DrawTrackKeys(row.RotTrackIdx, centerY, visibleStartTime, visibleEndTime, drawSelectedPass: false);
                if (_selectedKeys.Count > 0)
                {
                    DrawTrackKeys(row.PosTrackIdx, centerY, visibleStartTime, visibleEndTime, drawSelectedPass: true);
                    DrawTrackKeys(row.RotTrackIdx, centerY, visibleStartTime, visibleEndTime, drawSelectedPass: true);
                }
            }
        }

        private void DrawTrackKeys(int trackIdx, float centerY, double minTime, double maxTime, bool drawSelectedPass)
        {
            if (trackIdx < 0) return;

            var (startIdx, endIdx) = _keyCache.GetVisibleKeyRange(trackIdx, minTime - 0.1, maxTime + 0.1);
            if (startIdx > endIdx) return;

            var times = _keyCache.GetTrackTimes(trackIdx);
            float pixelsPerFrame = _pixelsPerSecond / _fps;
            bool drawDensityStrip = pixelsPerFrame < 3.0f;

            if (drawDensityStrip)
            {
                if (!drawSelectedPass)
                {
                    float startX = XFromTime(times[startIdx]);
                    float endX = XFromTime(times[endIdx]);
                    DrawRect(new Rect2(startX, centerY - 2f, Math.Max(2f, endX - startX), 4f), ColorDiamondNormal * 0.6f);
                }
                return;
            }

            float lastDrawnKeyX = -9999f;
            for (int k = startIdx; k <= endIdx; k++)
            {
                double t = times[k];
                bool isSelected = _selectedKeys.Count > 0 && _selectedKeySet.Contains(MakeKeyId(trackIdx, t, _fps));
                if (isSelected != drawSelectedPass) continue;

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
                AcceptEvent();
                HandleMouseButton(mb);
            }
            else if (@event is InputEventMouseMotion mm)
            {
                AcceptEvent();
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
                    GrabFocus();
                    _pendingSingleSelectKeys = null;
                    _dragThresholdExceeded = false;

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
                        var hitKeys = HitTestKeys(pos, out int hitRowIdx);
                        if (hitKeys.Count > 0)
                        {
                            // Also highlight the clicked bone row
                            if (hitRowIdx >= 0 && hitRowIdx < _trackRows.Count && _selectedTrackIdx != hitRowIdx)
                            {
                                _selectedTrackIdx = hitRowIdx;
                                EmitSignal(SignalName.TrackSelected, hitRowIdx, _trackRows[hitRowIdx].BoneName);
                            }

                            bool allAlreadySelected = hitKeys.All(hk => _selectedKeySet.Contains(MakeKeyId(hk.TrackIdx, hk.Time, _fps)));

                            if (mb.ShiftPressed)
                            {
                                if (allAlreadySelected)
                                {
                                    foreach (var hk in hitKeys)
                                    {
                                        long hkId = MakeKeyId(hk.TrackIdx, hk.Time, _fps);
                                        _selectedKeys.RemoveAll(k => MakeKeyId(k.TrackIdx, k.Time, _fps) == hkId);
                                    }
                                }
                                else
                                {
                                    foreach (var hk in hitKeys)
                                    {
                                        long hkId = MakeKeyId(hk.TrackIdx, hk.Time, _fps);
                                        if (!_selectedKeySet.Contains(hkId))
                                        {
                                            _selectedKeys.Add(hk);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                if (!allAlreadySelected)
                                {
                                    _selectedKeys.Clear();
                                    _selectedKeys.AddRange(hitKeys);
                                }
                                else if (_selectedKeys.Count > hitKeys.Count)
                                {
                                    // Keep multi-selection for potential drag; narrow to clicked key on click-release without drag
                                    _pendingSingleSelectKeys = hitKeys;
                                }
                            }

                            UpdateSelectedKeySet();
                            EmitSignal(SignalName.KeyframeSelectionChanged);

                            if (_selectedKeys.Count > 0)
                            {
                                _currentDragMode = DragMode.DragKeys;
                                _dragStartMousePos = pos;
                                _dragStartTime = TimeFromX(pos.X);
                                _dragDeltaTime = 0.0;
                                _keysInitialTimes.Clear();
                                foreach (var k in _selectedKeys)
                                {
                                    _keysInitialTimes[k] = k.Time;
                                }
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
                        if (!_dragThresholdExceeded && _pendingSingleSelectKeys != null)
                        {
                            // User clicked an already-selected key within a multi-selection without dragging -> select just that key
                            _selectedKeys.Clear();
                            _selectedKeys.AddRange(_pendingSingleSelectKeys);
                            UpdateSelectedKeySet();
                            EmitSignal(SignalName.KeyframeSelectionChanged);
                        }
                        else
                        {
                            CommitKeyMoves();
                        }
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
                    _dragThresholdExceeded = false;
                    _pendingSingleSelectKeys = null;
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
                if (!_dragThresholdExceeded && pos.DistanceTo(_dragStartMousePos) < DragThresholdPx)
                {
                    return;
                }
                _dragThresholdExceeded = true;
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
            else if (_currentDragMode == DragMode.None)
            {
                if (pos.Y >= RulerHeight + SummaryRowHeight)
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

                    bool overKey = HasKeyNear(pos);
                    MouseDefaultCursorShape = overKey ? CursorShape.Move : CursorShape.Arrow;
                }
                else
                {
                    if (_hoveredTrackIdx != -1)
                    {
                        _hoveredTrackIdx = -1;
                        EmitSignal(SignalName.TrackHovered, -1, "");
                        QueueRedraw();
                    }
                    MouseDefaultCursorShape = pos.Y <= RulerHeight ? CursorShape.Hsize : CursorShape.Arrow;
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

            if (KeybindsManager.IsActionTriggered("anim_delete_keys", key))
            {
                DeleteSelectedKeys();
                GetViewport().SetInputAsHandled();
            }
            else if (KeybindsManager.IsActionTriggered("anim_duplicate_keys", key))
            {
                DuplicateSelectedKeys();
                GetViewport().SetInputAsHandled();
            }
            else if (KeybindsManager.IsActionTriggered("anim_select_all", key))
            {
                if (key.AltPressed || _selectedKeys.Count > 0) DeselectAllKeys();
                else SelectAllKeys();
                GetViewport().SetInputAsHandled();
            }
        }

        private bool HasKeyNear(Vector2 mousePos)
        {
            if (_activeAnimation == null) return false;
            float relY = mousePos.Y - (RulerHeight + SummaryRowHeight) + _scrollY;
            int rowIdx = (int)Math.Floor(relY / TrackRowHeight);
            if (rowIdx < 0 || rowIdx >= _trackRows.Count) return false;

            var row = _trackRows[rowIdx];
            double mouseTime = TimeFromX(mousePos.X);
            double hitTolerance = KeyHitRadiusX / _pixelsPerSecond;

            int[] tracks = [row.PosTrackIdx, row.RotTrackIdx];
            foreach (int t in tracks)
            {
                if (t < 0 || t >= _activeAnimation.GetTrackCount()) continue;
                var (startK, endK) = _keyCache.GetVisibleKeyRange(t, mouseTime - hitTolerance, mouseTime + hitTolerance);
                if (startK <= endK) return true;
            }
            return false;
        }

        /// <summary>
        /// Hit-tests a bone row and returns ALL keys (both Position and Rotation) at the closest frame
        /// under the cursor, so clicking an individual bone keyframe selects and moves the full bone key cleanly.
        /// </summary>
        private List<SelectedKeyInfo> HitTestKeys(Vector2 mousePos, out int hitRowIdx)
        {
            var results = new List<SelectedKeyInfo>();
            hitRowIdx = -1;
            if (_activeAnimation == null) return results;

            float relY = mousePos.Y - (RulerHeight + SummaryRowHeight) + _scrollY;
            int rowIdx = (int)Math.Floor(relY / TrackRowHeight);
            if (rowIdx < 0 || rowIdx >= _trackRows.Count) return results;

            var row = _trackRows[rowIdx];
            double mouseTime = TimeFromX(mousePos.X);
            double hitTolerance = KeyHitRadiusX / _pixelsPerSecond;

            // 1. Find the closest key timestamp across Pos & Rot tracks on this row
            double bestTime = double.NaN;
            float bestDistPx = float.MaxValue;
            int[] tracks = [row.PosTrackIdx, row.RotTrackIdx];

            foreach (int t in tracks)
            {
                if (t < 0 || t >= _activeAnimation.GetTrackCount()) continue;
                var (startK, endK) = _keyCache.GetVisibleKeyRange(t, mouseTime - hitTolerance, mouseTime + hitTolerance);
                if (startK > endK) continue;

                var times = _keyCache.GetTrackTimes(t);
                for (int k = startK; k <= endK; k++)
                {
                    float distPx = Math.Abs(mousePos.X - XFromTime(times[k]));
                    if (distPx <= KeyHitRadiusX && distPx < bestDistPx)
                    {
                        bestDistPx = distPx;
                        bestTime = times[k];
                    }
                }
            }

            if (double.IsNaN(bestTime)) return results;

            // 2. Collect all keys on this bone row at that same frame (Pos + Rot)
            hitRowIdx = rowIdx;
            double frameTol = 0.45 / _fps;
            foreach (int t in tracks)
            {
                if (t < 0 || t >= _activeAnimation.GetTrackCount()) continue;
                var (startK, endK) = _keyCache.GetVisibleKeyRange(t, bestTime - frameTol, bestTime + frameTol);
                if (startK > endK) continue;

                var times = _keyCache.GetTrackTimes(t);
                for (int k = startK; k <= endK; k++)
                {
                    results.Add(new SelectedKeyInfo
                    {
                        TrackIdx = t,
                        Time = times[k],
                        Value = _activeAnimation.TrackGetKeyValue(t, k),
                        Transition = _activeAnimation.TrackGetKeyTransition(t, k)
                    });
                }
            }

            return results;
        }

        private void HandleSummaryRowClick(Vector2 pos, bool isShift)
        {
            if (_activeAnimation == null) return;
            double clickT = TimeFromX(pos.X);
            double hitTol = KeyHitRadiusX / _pixelsPerSecond;

            // Find closest summary diamond within hit tolerance
            var (sumStart, sumEnd) = _keyCache.GetVisibleSummaryRange(clickT - hitTol, clickT + hitTol);
            double targetTime = double.NaN;
            float bestDistPx = float.MaxValue;
            var summaryTimes = _keyCache.SummaryTimes;
            for (int i = sumStart; i <= sumEnd; i++)
            {
                float distPx = Math.Abs(pos.X - XFromTime(summaryTimes[i]));
                if (distPx <= KeyHitRadiusX && distPx < bestDistPx)
                {
                    bestDistPx = distPx;
                    targetTime = summaryTimes[i];
                }
            }

            if (double.IsNaN(targetTime))
            {
                SetPlayheadFromMouseX(pos.X);
                return;
            }

            double frameTol = 0.45 / _fps;
            var keysAtFrame = new List<SelectedKeyInfo>();
            for (int t = 0; t < _activeAnimation.GetTrackCount(); t++)
            {
                var (startK, endK) = _keyCache.GetVisibleKeyRange(t, targetTime - frameTol, targetTime + frameTol);
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
                UpdateSelectedKeySet();

                foreach (var k in keysAtFrame)
                {
                    long id = MakeKeyId(k.TrackIdx, k.Time, _fps);
                    if (!_selectedKeySet.Contains(id))
                    {
                        _selectedKeys.Add(k);
                        _selectedKeySet.Add(id);
                    }
                }

                UpdateSelectedKeySet();
                _currentDragMode = DragMode.DragKeys;
                _dragStartMousePos = pos;
                _dragStartTime = targetTime;
                _dragDeltaTime = 0.0;
                _dragThresholdExceeded = false;
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

            _dragDeltaTime = 0.0;
            _keysInitialTimes.Clear();

            if (moves.Count > 0)
            {
                UpdateSelectedKeySet();
                var cmd = new MoveKeyframesCommand(_activeAnimation, moves);
                // _undoRedo.Execute fires HistoryChanged -> AnimationStudioDock.OnCanvasKeyframesModified -> RefreshFromAnimation()
                _undoRedo.Execute(cmd);
            }
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

            _scaleFactor = 1.0f;
            _keysInitialTimes.Clear();

            if (moves.Count > 0)
            {
                UpdateSelectedKeySet();
                var cmd = new MoveKeyframesCommand(_activeAnimation, moves);
                _undoRedo.Execute(cmd);
            }
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
