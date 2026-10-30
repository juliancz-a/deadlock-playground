using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using DeadlockPlayground.Tools;

namespace DeadlockPlayground.Animation
{
    /// <summary>
    /// Interactive F-Curve / Graph Editor for viewing and tweaking keyframe Bezier tangents,
    /// interpolation modes, and channel curves in Euler degrees or positional meters.
    /// Emulates Blender's Graph Editor with modal G/S/R transforms and axis locking.
    /// </summary>
    public partial class GraphEditorCanvas : Control
    {
        [Signal] public delegate void PlayheadScrubbedEventHandler(double time);
        [Signal] public delegate void KeyframesModifiedEventHandler();
        [Signal] public delegate void KeySelectionChangedEventHandler(double time, float value, bool hasSelection);
        [Signal] public delegate void CurvesRebuiltEventHandler();
        [Signal] public delegate void ModalStatusChangedEventHandler(string statusText);
        [Signal] public delegate void ActiveCurveChangedEventHandler(string curveName);
        [Signal] public delegate void ModifiersModifiedEventHandler();
        [Signal] public delegate void ModifiersSidebarToggledEventHandler();
        [Signal] public delegate void ShortcutsRequestedEventHandler();

        public enum TangentMode { Free, Aligned, Vector, Auto }
        public enum InterpolationMode { Bezier, Linear, Constant }
        public enum ModalTransformMode { None, Grab, Scale, Rotate }
        public enum AxisLock { None, X, Y }

        public class CurveKey
        {
            public double Time { get; set; }
            public float Value { get; set; }
            public Vector2 InHandle { get; set; } = new(-0.05f, 0f); // Relative (dt, dval)
            public Vector2 OutHandle { get; set; } = new(0.05f, 0f); // Relative (dt, dval)
            public TangentMode Mode { get; set; } = TangentMode.Aligned;
            public InterpolationMode Interp { get; set; } = InterpolationMode.Bezier;
            public bool IsSelected { get; set; }
            public bool IsInHandleSelected { get; set; }
            public bool IsOutHandleSelected { get; set; }
        }

        public class ChannelCurve
        {
            public string Name { get; set; } = "";
            public Color ChannelColor { get; set; } = Colors.Red;
            public int TrackIdx { get; set; } = -1;
            public int SubChannel { get; set; } = 0; // 0=X, 1=Y, 2=Z, 3=EulerX, 4=EulerY, 5=EulerZ
            public bool IsVisible { get; set; } = true;
            public bool IsLocked { get; set; } = false;
            public List<CurveKey> Keys { get; } = new();
            public List<FCurveModifier> Modifiers { get; } = new();

            public float EvaluateBase(double time)
            {
                if (Keys.Count == 0) return 0f;
                if (Keys.Count == 1 || time <= Keys[0].Time) return Keys[0].Value;
                if (time >= Keys[^1].Time) return Keys[^1].Value;

                for (int i = 0; i < Keys.Count - 1; i++)
                {
                    if (time >= Keys[i].Time && time <= Keys[i + 1].Time)
                    {
                        double span = Keys[i + 1].Time - Keys[i].Time;
                        if (span <= 1e-6) return Keys[i].Value;
                        float factor = (float)((time - Keys[i].Time) / span);
                        return Mathf.Lerp(Keys[i].Value, Keys[i + 1].Value, factor);
                    }
                }
                return Keys[^1].Value;
            }

            public float Evaluate(double time, float fps)
            {
                float val = EvaluateBase(time);
                if (Modifiers.Count == 0) return val;

                for (int i = 0; i < Modifiers.Count; i++)
                {
                    var mod = Modifiers[i];
                    if (mod.IsMuted) continue;
                    val = mod.Evaluate(val, time, fps, this);
                }
                return val;
            }
        }

        private readonly List<ChannelCurve> _curves = new();
        private Godot.Animation _activeAnimation;
        private Skeleton3D _skeleton;
        private string _activeBoneName = "";
        private ChannelCurve _activeCurve = null;
        private AnimationUndoRedoManager _undoRedo;

        public ChannelCurve ActiveCurve
        {
            get => _activeCurve;
            set
            {
                if (_activeCurve != value)
                {
                    _activeCurve = value;
                    EmitSignal(SignalName.ActiveCurveChanged, _activeCurve?.Name ?? "");
                    QueueRedraw();
                }
            }
        }

        public double PlayheadTime => _currentTime;
        public float Fps => _fps;

        // Viewport Transform
        private double _currentTime = 0.0;
        private double _duration = 1.0;
        private float _fps = 30f;
        private float _pixelsPerSecond = 140f;
        private float _pixelsPerValueUnit = 50f;
        private float _scrollX = 0f;
        private float _scrollY = 0f; // in screen pixels, centered vertically
        private bool _isNormalized = false;

        // Selection & Drag State
        private enum DragMode { None, ScrubPlayhead, PanCanvas, DragKey, DragInHandle, DragOutHandle, BoxSelect }
        private DragMode _currentDragMode = DragMode.None;
        private Vector2 _dragStartMousePos = Vector2.Zero;
        private Rect2 _boxSelectRect = default;
        private CurveKey _draggedKey = null;
        private ChannelCurve _draggedCurve = null;

        // Modal Transforms (Blender G / S / R operators)
        private ModalTransformMode _modalMode = ModalTransformMode.None;
        private AxisLock _axisLock = AxisLock.None;
        private Vector2 _modalStartMousePos = Vector2.Zero;
        private Vector2 _modalPivotCanvas = Vector2.Zero;
        private double _modalPivotTime = 0.0;
        private float _modalPivotValue = 0.0f;
        private readonly Dictionary<CurveKey, (double Time, float Value, Vector2 InHandle, Vector2 OutHandle)> _modalInitialKeyStates = new();

        // Pre-allocated Bezier polyline buffers for zero-allocation drawing
        private readonly Vector2[] _bezierBuffer6 = new Vector2[7];
        private readonly Vector2[] _bezierBuffer18 = new Vector2[19];

        // Visual Colors
        private static readonly Color ColorBg = new(0.11f, 0.12f, 0.14f, 1f);
        private static readonly Color ColorGridLine = new(0.18f, 0.20f, 0.23f, 0.6f);
        private static readonly Color ColorZeroLine = new(0.35f, 0.38f, 0.44f, 0.9f);
        private static readonly Color ColorPlayhead = new(0.95f, 0.22f, 0.25f, 1f);
        private static readonly Color ColorHandleLine = new(0.7f, 0.7f, 0.7f, 0.6f);
        private static readonly Color ColorHandlePoint = new(1f, 0.85f, 0.2f, 1f);
        private static readonly Color ColorMarquee = new(0.3f, 0.6f, 1.0f, 0.25f);
        private static readonly Color ColorMarqueeBorder = new(0.4f, 0.7f, 1.0f, 0.85f);
        private static readonly Color ColorAxisLockX = new(0.9f, 0.25f, 0.25f, 0.8f);
        private static readonly Color ColorAxisLockY = new(0.25f, 0.85f, 0.3f, 0.8f);

        // Standard Channel Colors (Blender XYZ convention)
        public static readonly Color ColorPosX = new(0.95f, 0.25f, 0.25f, 1f); // Red
        public static readonly Color ColorPosY = new(0.30f, 0.85f, 0.30f, 1f); // Green
        public static readonly Color ColorPosZ = new(0.25f, 0.55f, 0.95f, 1f); // Blue
        public static readonly Color ColorRotX = new(0.95f, 0.50f, 0.45f, 1f); // Coral
        public static readonly Color ColorRotY = new(0.55f, 0.90f, 0.50f, 1f); // Mint
        public static readonly Color ColorRotZ = new(0.45f, 0.70f, 0.95f, 1f); // Sky Blue

        public IReadOnlyList<ChannelCurve> Curves => _curves;
        public string ActiveBoneName => _activeBoneName;
        public bool IsNormalized
        {
            get => _isNormalized;
            set
            {
                _isNormalized = value;
                QueueRedraw();
            }
        }

        private PlayheadOverlay _playheadOverlay;
        private readonly List<CurveKey> _selectionCache = new();
        private bool _selectionCacheDirty = true;

        public override void _Ready()
        {
            ClipContents = true;
            FocusMode = FocusModeEnum.Click;
            _playheadOverlay = new PlayheadOverlay();
            _playheadOverlay.TopY = 0f;
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

        public void SetUndoRedoManager(AnimationUndoRedoManager undoRedo)
        {
            _undoRedo = undoRedo;
        }

        public void SetAnimationAndBone(Godot.Animation anim, Skeleton3D skeleton, string boneName, float fps = 30f)
        {
            if (_activeAnimation == anim && _skeleton == skeleton && _activeBoneName == boneName && Math.Abs(_fps - fps) < 0.01)
            {
                return;
            }

            _activeAnimation = anim;
            _skeleton = (skeleton != null && GodotObject.IsInstanceValid(skeleton) && !skeleton.IsQueuedForDeletion()) ? skeleton : null;
            _activeBoneName = boneName;
            _fps = fps > 0 ? fps : 30f;
            _duration = anim != null ? Math.Max(0.01, anim.Length) : 1.0;
            _selectionCacheDirty = true;
            RebuildCurves();
            QueueRedraw();
        }

        public void SetPlayheadTime(double time)
        {
            _currentTime = Math.Clamp(time, 0.0, _duration);
            if (_playheadOverlay != null)
            {
                _playheadOverlay.PlayheadX = XFromTime(_currentTime);
            }
        }

        public void RebuildCurves()
        {
            var savedModifiers = new Dictionary<(int TrackIdx, int SubChannel), List<FCurveModifier>>();
            var savedSelectedKeys = new HashSet<(int TrackIdx, int SubChannel, double Time)>();
            foreach (var c in _curves)
            {
                if (c.Modifiers.Count > 0)
                {
                    savedModifiers[(c.TrackIdx, c.SubChannel)] = new List<FCurveModifier>(c.Modifiers);
                }
                foreach (var k in c.Keys)
                {
                    if (k.IsSelected)
                    {
                        savedSelectedKeys.Add((c.TrackIdx, c.SubChannel, k.Time));
                    }
                }
            }
            string prevActiveName = _activeCurve?.Name;

            _curves.Clear();
            if (_activeAnimation == null || _skeleton == null || !GodotObject.IsInstanceValid(_skeleton) || _skeleton.IsQueuedForDeletion() || string.IsNullOrEmpty(_activeBoneName))
            {
                _activeCurve = null;
                EmitSignal(SignalName.ActiveCurveChanged, "");
                EmitSignal(SignalName.CurvesRebuilt);
                return;
            }

            int posTrack = -1;
            int rotTrack = -1;

            for (int t = 0; t < _activeAnimation.GetTrackCount(); t++)
            {
                string path = _activeAnimation.TrackGetPath(t).ToString();
                if (!path.EndsWith($":{_activeBoneName}", StringComparison.OrdinalIgnoreCase)) continue;

                var type = _activeAnimation.TrackGetType(t);
                if (type == Godot.Animation.TrackType.Position3D) posTrack = t;
                else if (type == Godot.Animation.TrackType.Rotation3D) rotTrack = t;
            }

            // 1. Position Channels (X, Y, Z in meters)
            if (posTrack != -1)
            {
                var curveX = new ChannelCurve { Name = $"{_activeBoneName} Pos X", ChannelColor = ColorPosX, TrackIdx = posTrack, SubChannel = 0 };
                var curveY = new ChannelCurve { Name = $"{_activeBoneName} Pos Y", ChannelColor = ColorPosY, TrackIdx = posTrack, SubChannel = 1 };
                var curveZ = new ChannelCurve { Name = $"{_activeBoneName} Pos Z", ChannelColor = ColorPosZ, TrackIdx = posTrack, SubChannel = 2 };

                int kCount = _activeAnimation.TrackGetKeyCount(posTrack);
                for (int k = 0; k < kCount; k++)
                {
                    double time = _activeAnimation.TrackGetKeyTime(posTrack, k);
                    Vector3 val = (Vector3)_activeAnimation.TrackGetKeyValue(posTrack, k);
                    curveX.Keys.Add(new CurveKey { Time = time, Value = val.X });
                    curveY.Keys.Add(new CurveKey { Time = time, Value = val.Y });
                    curveZ.Keys.Add(new CurveKey { Time = time, Value = val.Z });
                }

                _curves.Add(curveX);
                _curves.Add(curveY);
                _curves.Add(curveZ);
            }

            // 2. Rotation Channels (Euler X, Y, Z in degrees)
            if (rotTrack != -1)
            {
                var curveRotX = new ChannelCurve { Name = $"{_activeBoneName} Rot X", ChannelColor = ColorRotX, TrackIdx = rotTrack, SubChannel = 3 };
                var curveRotY = new ChannelCurve { Name = $"{_activeBoneName} Rot Y", ChannelColor = ColorRotY, TrackIdx = rotTrack, SubChannel = 4 };
                var curveRotZ = new ChannelCurve { Name = $"{_activeBoneName} Rot Z", ChannelColor = ColorRotZ, TrackIdx = rotTrack, SubChannel = 5 };

                int kCount = _activeAnimation.TrackGetKeyCount(rotTrack);
                Vector3 prevEuler = Vector3.Zero;

                for (int k = 0; k < kCount; k++)
                {
                    double time = _activeAnimation.TrackGetKeyTime(rotTrack, k);
                    Quaternion q = ((Quaternion)_activeAnimation.TrackGetKeyValue(rotTrack, k)).Normalized();
                    Vector3 eulerRad = q.GetEuler();
                    Vector3 eulerDeg = new(
                        Mathf.RadToDeg(eulerRad.X),
                        Mathf.RadToDeg(eulerRad.Y),
                        Mathf.RadToDeg(eulerRad.Z)
                    );

                    // Continuous unwrapping to avoid 360-deg flips
                    if (k > 0)
                    {
                        eulerDeg.X += 360f * Mathf.Round((prevEuler.X - eulerDeg.X) / 360f);
                        eulerDeg.Y += 360f * Mathf.Round((prevEuler.Y - eulerDeg.Y) / 360f);
                        eulerDeg.Z += 360f * Mathf.Round((prevEuler.Z - eulerDeg.Z) / 360f);
                    }
                    prevEuler = eulerDeg;

                    curveRotX.Keys.Add(new CurveKey { Time = time, Value = eulerDeg.X });
                    curveRotY.Keys.Add(new CurveKey { Time = time, Value = eulerDeg.Y });
                    curveRotZ.Keys.Add(new CurveKey { Time = time, Value = eulerDeg.Z });
                }

                _curves.Add(curveRotX);
                _curves.Add(curveRotY);
                _curves.Add(curveRotZ);
            }

            // Restore saved modifiers
            foreach (var c in _curves)
            {
                if (savedModifiers.TryGetValue((c.TrackIdx, c.SubChannel), out var mods))
                {
                    c.Modifiers.AddRange(mods);
                }
            }

            // Restore active curve
            _activeCurve = _curves.FirstOrDefault(c => c.Name == prevActiveName) ?? _curves.FirstOrDefault();
            EmitSignal(SignalName.ActiveCurveChanged, _activeCurve?.Name ?? "");

            // Restore selected keys
            if (savedSelectedKeys.Count > 0)
            {
                foreach (var c in _curves)
                {
                    foreach (var k in c.Keys)
                    {
                        if (savedSelectedKeys.Any(s => s.TrackIdx == c.TrackIdx && s.SubChannel == c.SubChannel && Math.Abs(s.Time - k.Time) < 0.001))
                        {
                            k.IsSelected = true;
                        }
                    }
                }
            }

            // Compute default automatic Bezier handles for all keys
            foreach (var curve in _curves)
            {
                ComputeAutoTangents(curve);
            }

            EmitSignal(SignalName.CurvesRebuilt);
        }

        public void FitView()
        {
            var visibleKeys = _curves.Where(c => c.IsVisible).SelectMany(c => c.Keys).ToList();
            if (visibleKeys.Count == 0)
            {
                _scrollX = 0f;
                _scrollY = 0f;
                _pixelsPerSecond = 140f;
                _pixelsPerValueUnit = 50f;
                QueueRedraw();
                return;
            }

            double minT = visibleKeys.Min(k => k.Time);
            double maxT = visibleKeys.Max(k => k.Time);
            float minV = visibleKeys.Min(k => k.Value);
            float maxV = visibleKeys.Max(k => k.Value);

            double spanT = Math.Max(0.1, maxT - minT);
            float spanV = Math.Max(1.0f, maxV - minV);

            float canvasW = Math.Max(100f, Size.X - 80f);
            float canvasH = Math.Max(100f, Size.Y - 80f);

            _pixelsPerSecond = Mathf.Clamp((float)(canvasW / spanT), 30f, 1000f);
            _pixelsPerValueUnit = Mathf.Clamp(canvasH / spanV, 2f, 500f);

            _scrollX = (float)(minT * _pixelsPerSecond) - 40f;
            float centerV = (minV + maxV) * 0.5f;
            _scrollY = centerV * _pixelsPerValueUnit;

            QueueRedraw();
        }

        public void FitSelectedView()
        {
            var selected = GetSelectedKeys();
            if (selected.Count == 0)
            {
                FitView();
                return;
            }

            double minT = selected.Min(k => k.Time);
            double maxT = selected.Max(k => k.Time);
            float minV = selected.Min(k => k.Value);
            float maxV = selected.Max(k => k.Value);

            double spanT = Math.Max(0.1, maxT - minT);
            float spanV = Math.Max(1.0f, maxV - minV);

            float canvasW = Math.Max(100f, Size.X - 80f);
            float canvasH = Math.Max(100f, Size.Y - 80f);

            _pixelsPerSecond = Mathf.Clamp((float)(canvasW / spanT), 30f, 1000f);
            _pixelsPerValueUnit = Mathf.Clamp(canvasH / spanV, 2f, 500f);

            _scrollX = (float)(minT * _pixelsPerSecond) - 40f;
            float centerV = (minV + maxV) * 0.5f;
            _scrollY = centerV * _pixelsPerValueUnit;

            QueueRedraw();
        }

        public void SelectChannel(ChannelCurve targetCurve, bool addToSelection = false)
        {
            _selectionCacheDirty = true;
            ActiveCurve = targetCurve;
            if (!addToSelection)
            {
                foreach (var c in _curves)
                {
                    foreach (var k in c.Keys) k.IsSelected = false;
                }
            }
            if (targetCurve != null && targetCurve.IsVisible && !targetCurve.IsLocked)
            {
                foreach (var k in targetCurve.Keys) k.IsSelected = true;
            }
            NotifyKeySelectionChanged();
            QueueRedraw();
        }

        public void IsolateChannel(ChannelCurve targetCurve)
        {
            if (targetCurve == null) return;

            bool isOnlyVisible = targetCurve.IsVisible && _curves.All(c => c == targetCurve ? c.IsVisible : !c.IsVisible);

            if (isOnlyVisible)
            {
                foreach (var c in _curves) c.IsVisible = true;
            }
            else
            {
                foreach (var c in _curves) c.IsVisible = (c == targetCurve);
            }

            _selectionCacheDirty = true;
            QueueRedraw();
        }

        public enum EasePreset { Linear, EaseIn, EaseOut, EaseInOut, Constant }

        public void SetSelectedEasePreset(EasePreset preset)
        {
            var selected = GetSelectedKeys();
            if (selected.Count == 0) return;

            foreach (var k in selected)
            {
                switch (preset)
                {
                    case EasePreset.Linear:
                        k.Mode = TangentMode.Vector;
                        break;
                    case EasePreset.EaseIn:
                        k.Mode = TangentMode.Free;
                        k.InHandle = new Vector2(k.InHandle.X, 0f);
                        break;
                    case EasePreset.EaseOut:
                        k.Mode = TangentMode.Free;
                        k.OutHandle = new Vector2(k.OutHandle.X, 0f);
                        break;
                    case EasePreset.EaseInOut:
                        k.Mode = TangentMode.Aligned;
                        k.InHandle = new Vector2(k.InHandle.X, 0f);
                        k.OutHandle = new Vector2(k.OutHandle.X, 0f);
                        break;
                    case EasePreset.Constant:
                        k.Mode = TangentMode.Free;
                        k.OutHandle = new Vector2(0f, 0f);
                        break;
                }
            }

            foreach (var curve in _curves)
            {
                ComputeAutoTangents(curve);
            }

            BakeCurvesToGodotTracks();
            EmitSignal(SignalName.KeyframesModified);
            QueueRedraw();
        }

        public void SetSelectedKeyValue(float newValue)
        {
            var selected = GetSelectedKeys();
            if (selected.Count == 0) return;

            foreach (var k in selected)
            {
                k.Value = newValue;
            }

            foreach (var curve in _curves)
            {
                ComputeAutoTangents(curve);
            }

            BakeCurvesToGodotTracks();
            EmitSignal(SignalName.KeyframesModified);
            QueueRedraw();
        }

        public void SetSelectedKeyFrame(int frame)
        {
            var selected = GetSelectedKeys();
            if (selected.Count == 0) return;

            double newTime = Math.Clamp(frame / (double)_fps, 0.0, _duration);
            foreach (var k in selected)
            {
                k.Time = newTime;
            }

            foreach (var curve in _curves)
            {
                ComputeAutoTangents(curve);
            }

            BakeCurvesToGodotTracks();
            EmitSignal(SignalName.KeyframesModified);
            QueueRedraw();
        }

        public List<CurveKey> GetSelectedKeys()
        {
            if (_selectionCacheDirty)
            {
                _selectionCache.Clear();
                foreach (var curve in _curves)
                {
                    if (!curve.IsVisible || curve.IsLocked) continue;
                    foreach (var k in curve.Keys)
                    {
                        if (k.IsSelected) _selectionCache.Add(k);
                    }
                }
                _selectionCacheDirty = false;
            }
            return _selectionCache;
        }

        public void ToggleSelectAll()
        {
            _selectionCacheDirty = true;
            bool anySelected = _curves.Any(c => c.Keys.Any(k => k.IsSelected));
            foreach (var curve in _curves)
            {
                if (!curve.IsVisible || curve.IsLocked) continue;
                foreach (var k in curve.Keys)
                {
                    k.IsSelected = !anySelected;
                }
            }

            NotifyKeySelectionChanged();
            QueueRedraw();
        }

        public void DeleteSelectedKeys()
        {
            var selected = GetSelectedKeys();
            if (selected.Count == 0) return;

            foreach (var curve in _curves)
            {
                if (!curve.IsVisible || curve.IsLocked) continue;
                curve.Keys.RemoveAll(k => k.IsSelected);
                ComputeAutoTangents(curve);
            }

            _selectionCacheDirty = true;
            BakeCurvesToGodotTracks();
            EmitSignal(SignalName.KeyframesModified);
            NotifyKeySelectionChanged();
            QueueRedraw();
        }

        public void DuplicateSelectedKeys()
        {
            var selected = GetSelectedKeys();
            if (selected.Count == 0) return;

            double offset = 1.0 / _fps;
            foreach (var curve in _curves)
            {
                if (!curve.IsVisible || curve.IsLocked) continue;
                var newlyAdded = new List<CurveKey>();
                foreach (var k in curve.Keys.Where(k => k.IsSelected).ToList())
                {
                    k.IsSelected = false;
                    var dup = new CurveKey
                    {
                        Time = Math.Clamp(k.Time + offset, 0.0, _duration),
                        Value = k.Value,
                        Mode = k.Mode,
                        InHandle = k.InHandle,
                        OutHandle = k.OutHandle,
                        IsSelected = true
                    };
                    newlyAdded.Add(dup);
                }
                curve.Keys.AddRange(newlyAdded);
                curve.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
                ComputeAutoTangents(curve);
            }

            _selectionCacheDirty = true;
            BakeCurvesToGodotTracks();
            EmitSignal(SignalName.KeyframesModified);
            NotifyKeySelectionChanged();
            QueueRedraw();
        }

        public void InsertKeyAtPlayhead()
        {
            if (_activeAnimation == null || _curves.Count == 0) return;

            double snap = 1.0 / _fps;
            double time = Math.Clamp(Math.Round(_currentTime / snap) * snap, 0.0, _duration);

            var targetCurves = _curves.Where(c => c == _activeCurve).ToList();
            if (targetCurves.Count == 0)
            {
                targetCurves = _curves.Where(c => c.IsVisible && !c.IsLocked).ToList();
            }
            if (targetCurves.Count == 0) targetCurves = _curves.ToList();

            foreach (var curve in targetCurves)
            {
                float val = curve.Evaluate(time, _fps);
                var existing = curve.Keys.FirstOrDefault(k => Math.Abs(k.Time - time) < 0.0001);
                if (existing != null)
                {
                    existing.Value = val;
                    existing.IsSelected = true;
                }
                else
                {
                    var newKey = new CurveKey
                    {
                        Time = time,
                        Value = val,
                        Mode = TangentMode.Aligned,
                        IsSelected = true
                    };
                    curve.Keys.Add(newKey);
                    curve.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
                }
                ComputeAutoTangents(curve);
            }

            _selectionCacheDirty = true;
            BakeCurvesToGodotTracks();
            EmitSignal(SignalName.KeyframesModified);
            NotifyKeySelectionChanged();
            QueueRedraw();
        }

        private void NotifyKeySelectionChanged()
        {
            _selectionCacheDirty = true;
            var sel = GetSelectedKeys();
            if (sel.Count > 0)
            {
                var first = sel[0];
                EmitSignal(SignalName.KeySelectionChanged, first.Time, first.Value, true);
            }
            else
            {
                EmitSignal(SignalName.KeySelectionChanged, 0.0, 0f, false);
            }
        }

        public void StartBoxSelect()
        {
            _currentDragMode = DragMode.BoxSelect;
            _dragStartMousePos = GetLocalMousePosition();
            _boxSelectRect = new Rect2(_dragStartMousePos, Vector2.Zero);
            QueueRedraw();
        }

        #region Modal Transforms (G / S / R)
        public void StartModalTransform(ModalTransformMode mode)
        {
            var selected = GetSelectedKeys();
            if (selected.Count == 0)
            {
                // Auto-select key nearest playhead
                CurveKey nearest = null;
                double nearestDist = double.MaxValue;
                foreach (var c in _curves.Where(c => c.IsVisible && !c.IsLocked))
                {
                    foreach (var k in c.Keys)
                    {
                        double d = Math.Abs(k.Time - _currentTime);
                        if (d < nearestDist)
                        {
                            nearestDist = d;
                            nearest = k;
                        }
                    }
                }
                if (nearest != null)
                {
                    nearest.IsSelected = true;
                    selected.Add(nearest);
                }
            }

            if (selected.Count == 0) return;

            _modalMode = mode;
            _axisLock = AxisLock.None;
            _modalStartMousePos = GetLocalMousePosition();

            _modalInitialKeyStates.Clear();
            double sumTime = 0.0;
            float sumVal = 0.0f;

            foreach (var k in selected)
            {
                _modalInitialKeyStates[k] = (k.Time, k.Value, k.InHandle, k.OutHandle);
                sumTime += k.Time;
                sumVal += k.Value;
            }

            _modalPivotTime = sumTime / selected.Count;
            _modalPivotValue = sumVal / selected.Count;
            _modalPivotCanvas = CanvasPoint(_modalPivotTime, _modalPivotValue);

            EmitSignal(SignalName.ModalStatusChanged, GetModalStatusText());
            QueueRedraw();
        }

        public void ConfirmModalTransform()
        {
            if (_modalMode == ModalTransformMode.None) return;

            BakeCurvesToGodotTracks();
            EmitSignal(SignalName.KeyframesModified);

            _modalMode = ModalTransformMode.None;
            _axisLock = AxisLock.None;
            _modalInitialKeyStates.Clear();

            EmitSignal(SignalName.ModalStatusChanged, "");
            NotifyKeySelectionChanged();
            QueueRedraw();
        }

        public void CancelModalTransform()
        {
            if (_modalMode == ModalTransformMode.None) return;

            foreach (var (key, orig) in _modalInitialKeyStates)
            {
                key.Time = orig.Time;
                key.Value = orig.Value;
                key.InHandle = orig.InHandle;
                key.OutHandle = orig.OutHandle;
            }

            foreach (var curve in _curves)
            {
                ComputeAutoTangents(curve);
            }

            _modalMode = ModalTransformMode.None;
            _axisLock = AxisLock.None;
            _modalInitialKeyStates.Clear();

            EmitSignal(SignalName.ModalStatusChanged, "");
            NotifyKeySelectionChanged();
            QueueRedraw();
        }

        public void ToggleAxisLock(AxisLock axis)
        {
            _axisLock = (_axisLock == axis) ? AxisLock.None : axis;
            UpdateModalTransform(GetLocalMousePosition());
            EmitSignal(SignalName.ModalStatusChanged, GetModalStatusText());
            QueueRedraw();
        }

        private string GetModalStatusText()
        {
            if (_modalMode == ModalTransformMode.None) return "";

            string modeName = _modalMode switch
            {
                ModalTransformMode.Grab => "GRAB",
                ModalTransformMode.Scale => "SCALE",
                ModalTransformMode.Rotate => "ROTATE",
                _ => ""
            };

            string axisStr = _axisLock switch
            {
                AxisLock.X => " • Time (X) Locked",
                AxisLock.Y => " • Value (Y) Locked",
                _ => ""
            };

            return $"[ {modeName}{axisStr} ]  (X/Y: Lock Axis | ↵: Confirm | Esc: Cancel)";
        }

        private void UpdateModalTransform(Vector2 mousePos)
        {
            if (_modalMode == ModalTransformMode.None || _modalInitialKeyStates.Count == 0) return;

            Vector2 mouseDelta = mousePos - _modalStartMousePos;

            if (_modalMode == ModalTransformMode.Grab)
            {
                double dt = mouseDelta.X / _pixelsPerSecond;
                float dv = -mouseDelta.Y / _pixelsPerValueUnit;

                if (_axisLock == AxisLock.X) dv = 0f;
                if (_axisLock == AxisLock.Y) dt = 0.0;

                double frameSnap = 1.0 / _fps;

                foreach (var (key, orig) in _modalInitialKeyStates)
                {
                    double targetT = Math.Clamp(orig.Time + dt, 0.0, _duration);
                    targetT = Math.Round(targetT / frameSnap) * frameSnap;
                    key.Time = targetT;
                    key.Value = orig.Value + dv;
                }
            }
            else if (_modalMode == ModalTransformMode.Scale)
            {
                float factorX = 1f + (mouseDelta.X * 0.01f);
                float factorY = 1f - (mouseDelta.Y * 0.01f);

                if (_axisLock == AxisLock.X) factorY = 1.0f;
                if (_axisLock == AxisLock.Y) factorX = 1.0f;

                foreach (var (key, orig) in _modalInitialKeyStates)
                {
                    double targetT = Math.Clamp(_modalPivotTime + (orig.Time - _modalPivotTime) * factorX, 0.0, _duration);
                    key.Time = targetT;
                    key.Value = _modalPivotValue + (orig.Value - _modalPivotValue) * factorY;
                    key.InHandle = new Vector2(orig.InHandle.X * factorX, orig.InHandle.Y * factorY);
                    key.OutHandle = new Vector2(orig.OutHandle.X * factorX, orig.OutHandle.Y * factorY);
                }
            }
            else if (_modalMode == ModalTransformMode.Rotate)
            {
                Vector2 v0 = _modalStartMousePos - _modalPivotCanvas;
                Vector2 v1 = mousePos - _modalPivotCanvas;
                float angle = v1.Angle() - v0.Angle();

                float cosA = Mathf.Cos(angle);
                float sinA = Mathf.Sin(angle);

                foreach (var (key, orig) in _modalInitialKeyStates)
                {
                    Vector2 pOrig = CanvasPoint(orig.Time, orig.Value);
                    Vector2 rel = pOrig - _modalPivotCanvas;
                    Vector2 rotRel = new(rel.X * cosA - rel.Y * sinA, rel.X * sinA + rel.Y * cosA);
                    Vector2 newCanvas = _modalPivotCanvas + rotRel;

                    key.Time = Math.Clamp(TimeFromX(newCanvas.X), 0.0, _duration);
                    key.Value = ValueFromY(newCanvas.Y);

                    // Rotate handles
                    key.InHandle = new Vector2(orig.InHandle.X * cosA - orig.InHandle.Y * sinA, orig.InHandle.X * sinA + orig.InHandle.Y * cosA);
                    key.OutHandle = new Vector2(orig.OutHandle.X * cosA - orig.OutHandle.Y * sinA, orig.OutHandle.X * sinA + orig.OutHandle.Y * cosA);
                }
            }

            foreach (var curve in _curves)
            {
                ComputeAutoTangents(curve);
            }
            BakeCurvesToGodotTracks();
            EmitSignal(SignalName.KeyframesModified);
            QueueRedraw();
        }
        #endregion

        private static void ComputeAutoTangents(ChannelCurve curve)
        {
            var keys = curve.Keys;
            for (int i = 0; i < keys.Count; i++)
            {
                var cur = keys[i];
                if (cur.Mode != TangentMode.Aligned && cur.Mode != TangentMode.Auto) continue;

                if (i == 0 && keys.Count > 1)
                {
                    float dt = (float)(keys[1].Time - cur.Time) * 0.33f;
                    float dv = (keys[1].Value - cur.Value) * 0.33f;
                    cur.OutHandle = new Vector2(dt, dv);
                    cur.InHandle = new Vector2(-dt, -dv);
                }
                else if (i == keys.Count - 1 && keys.Count > 1)
                {
                    float dt = (float)(cur.Time - keys[i - 1].Time) * 0.33f;
                    float dv = (cur.Value - keys[i - 1].Value) * 0.33f;
                    cur.InHandle = new Vector2(-dt, -dv);
                    cur.OutHandle = new Vector2(dt, dv);
                }
                else if (keys.Count > 2)
                {
                    var prev = keys[i - 1];
                    var next = keys[i + 1];
                    float totalDt = (float)(next.Time - prev.Time);
                    float slope = totalDt > 0.0001f ? (next.Value - prev.Value) / totalDt : 0f;

                    float dtIn = (float)(cur.Time - prev.Time) * 0.33f;
                    float dtOut = (float)(next.Time - cur.Time) * 0.33f;

                    cur.InHandle = new Vector2(-dtIn, -dtIn * slope);
                    cur.OutHandle = new Vector2(dtOut, dtOut * slope);
                }
            }
        }

        public override void _Draw()
        {
            var size = Size;
            if (size.X <= 0 || size.Y <= 0) return;

            // 1. Background
            DrawRect(new Rect2(Vector2.Zero, size), ColorBg);

            // 2. Value & Time Grids
            DrawValueGrid(size);
            DrawTimeGrid(size);

            // 3. F-Curves & Tangents
            foreach (var curve in _curves)
            {
                if (!curve.IsVisible) continue;
                DrawChannelCurve(curve, size);
            }

            // 4. Playhead is drawn by _playheadOverlay

            // 5. Marquee Box Selection
            if (_currentDragMode == DragMode.BoxSelect && _boxSelectRect.Size.Length() > 2f)
            {
                DrawRect(_boxSelectRect, ColorMarquee);
                DrawRect(_boxSelectRect, ColorMarqueeBorder, filled: false, width: 1.5f);
            }

            // 6. Modal Transform Axis Constraints & Pivot Indicator
            if (_modalMode != ModalTransformMode.None)
            {
                if (_axisLock == AxisLock.X)
                {
                    float y = _modalPivotCanvas.Y;
                    DrawLine(new Vector2(0, y), new Vector2(size.X, y), ColorAxisLockX, 1.5f);
                }
                else if (_axisLock == AxisLock.Y)
                {
                    float x = _modalPivotCanvas.X;
                    DrawLine(new Vector2(x, 0), new Vector2(x, size.Y), ColorAxisLockY, 1.5f);
                }

                // Pivot marker
                DrawCircle(_modalPivotCanvas, 4f, Colors.Gold);
                DrawArc(_modalPivotCanvas, 8f, 0, Mathf.Tau, 16, Colors.Gold, 1.2f);

                // Floating On-Canvas Status HUD Pill
                string hudText = GetModalStatusText();
                if (!string.IsNullOrEmpty(hudText))
                {
                    var font = ThemeDB.FallbackFont;
                    int fSize = 12;
                    Vector2 strSize = font.GetStringSize(hudText, HorizontalAlignment.Left, -1, fSize);
                    float padH = 14f;
                    float padV = 6f;
                    Vector2 pillPos = new Vector2(Mathf.Max(20f, (size.X - strSize.X - padH * 2) * 0.5f), size.Y - 38f);
                    Rect2 pillRect = new Rect2(pillPos, new Vector2(strSize.X + padH * 2, strSize.Y + padV * 2));

                    DrawRect(pillRect, new Color(0.08f, 0.09f, 0.12f, 0.92f), filled: true);
                    DrawRect(pillRect, new Color(1.0f, 0.75f, 0.2f, 0.85f), filled: false, width: 1.5f);
                    DrawString(font, new Vector2(pillPos.X + padH, pillPos.Y + padV + strSize.Y - 2), hudText, HorizontalAlignment.Left, -1, fSize, new Color(1.0f, 0.88f, 0.35f));
                }
            }
        }

        private void DrawValueGrid(Vector2 size)
        {
            float zeroY = YFromValue(0f);
            if (zeroY >= 0 && zeroY <= size.Y)
            {
                DrawLine(new Vector2(0, zeroY), new Vector2(size.X, zeroY), ColorZeroLine, 1.5f);
            }

            var font = ThemeDB.FallbackFont;
            int fontSize = 10;

            float valueStep = (_pixelsPerValueUnit > 100f) ? 0.5f : ((_pixelsPerValueUnit > 20f) ? 5.0f : 20.0f);
            float topVal = ValueFromY(0);
            float botVal = ValueFromY(size.Y);
            float minVal = Math.Min(topVal, botVal);
            float maxVal = Math.Max(topVal, botVal);

            int startI = (int)Math.Floor(minVal / valueStep);
            int endI = (int)Math.Ceiling(maxVal / valueStep);

            for (int i = startI; i <= endI; i++)
            {
                float val = i * valueStep;
                float y = YFromValue(val);
                if (y < 0 || y > size.Y) continue;

                DrawLine(new Vector2(0, y), new Vector2(size.X, y), ColorGridLine, 1.0f);
                DrawString(font, new Vector2(6, y - 3), $"{val:F1}", HorizontalAlignment.Left, -1, fontSize, ColorZeroLine);
            }
        }

        private void DrawTimeGrid(Vector2 size)
        {
            double visibleStartTime = Math.Max(0.0, TimeFromX(0));
            double visibleEndTime = Math.Max(0.0, TimeFromX(size.X));

            int startFrame = (int)Math.Floor(visibleStartTime * _fps);
            int endFrame = (int)Math.Ceiling(visibleEndTime * _fps) + 1;

            int frameStep = (_pixelsPerSecond > 80f) ? 1 : ((_pixelsPerSecond > 30f) ? 5 : (int)Math.Max(1, Math.Round(_fps)));

            for (int f = startFrame; f <= endFrame; f += frameStep)
            {
                double t = f / (double)_fps;
                float x = XFromTime(t);
                if (x < 0 || x > size.X) continue;

                bool isSecond = (f % (int)Math.Round(_fps)) == 0;
                DrawLine(new Vector2(x, 0), new Vector2(x, size.Y), ColorGridLine * (isSecond ? 1.5f : 0.8f), 1.0f);
            }
        }

        private void DrawChannelCurve(ChannelCurve curve, Vector2 size)
        {
            var keys = curve.Keys;
            if (keys.Count == 0 && curve.Modifiers.Count == 0) return;

            // Frustum cull bounds for curve segments
            double tMin = TimeFromX(-30);
            double tMax = TimeFromX(size.X + 30);

            if (curve.Modifiers.Count > 0 && curve.Modifiers.Any(m => !m.IsMuted))
            {
                // Sample modified curve across the canvas viewport
                float stepPx = 4.0f;
                int count = Mathf.Clamp((int)(size.X / stepPx) + 2, 20, 800);
                var polyPoints = new Vector2[count];
                for (int s = 0; s < count; s++)
                {
                    float sx = s * stepPx;
                    double t = TimeFromX(sx);
                    float val = curve.Evaluate(t, _fps);
                    polyPoints[s] = CanvasPoint(t, val);
                }
                DrawPolyline(polyPoints, curve.ChannelColor, 2.0f, antialiased: true);
            }
            else
            {
                // Draw connecting Bezier spline segments
                for (int i = 0; i < keys.Count - 1; i++)
                {
                    var k0 = keys[i];
                    var k1 = keys[i + 1];

                    // Frustum culling: skip segments outside visible viewport
                    if (k1.Time < tMin || k0.Time > tMax) continue;

                    Vector2 p0 = CanvasPoint(k0.Time, k0.Value);
                    Vector2 p3 = CanvasPoint(k1.Time, k1.Value);

                    if (k0.Interp == InterpolationMode.Constant)
                    {
                        DrawLine(p0, new Vector2(p3.X, p0.Y), curve.ChannelColor, 2.0f, antialiased: true);
                        DrawLine(new Vector2(p3.X, p0.Y), p3, curve.ChannelColor, 2.0f, antialiased: true);
                    }
                    else if (k0.Interp == InterpolationMode.Linear)
                    {
                        DrawLine(p0, p3, curve.ChannelColor, 2.0f, antialiased: true);
                    }
                    else
                    {
                        // Adaptive Tessellation based on pixel width
                        float dx = Mathf.Abs(p3.X - p0.X);
                        if (dx < 6.0f)
                        {
                            // Sub-pixel or tiny span: straight line is visually identical
                            DrawLine(p0, p3, curve.ChannelColor, 2.0f, antialiased: true);
                        }
                        else
                        {
                            Vector2 p1 = CanvasPoint(k0.Time + k0.OutHandle.X, k0.Value + k0.OutHandle.Y);
                            Vector2 p2 = CanvasPoint(k1.Time + k1.InHandle.X, k1.Value + k1.InHandle.Y);

                            if (dx < 40.0f)
                            {
                                // Moderate span: 6 segments is smooth and low cost
                                for (int s = 0; s <= 6; s++)
                                {
                                    float t = s / 6.0f;
                                    _bezierBuffer6[s] = SampleCubicBezier(p0, p1, p2, p3, t);
                                }
                                DrawPolyline(_bezierBuffer6, curve.ChannelColor, 2.0f, antialiased: true);
                            }
                            else
                            {
                                // Wide span: 18 segments for smooth curvature
                                for (int s = 0; s <= 18; s++)
                                {
                                    float t = s / 18.0f;
                                    _bezierBuffer18[s] = SampleCubicBezier(p0, p1, p2, p3, t);
                                }
                                DrawPolyline(_bezierBuffer18, curve.ChannelColor, 2.0f, antialiased: true);
                            }
                        }
                    }
                }
            }

            // Draw keyframe points and handles
            float lastDrawnKeyX = -9999f;
            foreach (var k in keys)
            {
                Vector2 p = CanvasPoint(k.Time, k.Value);
                if (p.X < -20 || p.X > size.X + 20) continue;

                // Screen-space LOD: skip redundant unselected key dots that clump within 3 pixels
                if (!k.IsSelected && Mathf.Abs(p.X - lastDrawnKeyX) < 3.0f)
                {
                    continue;
                }

                // Handles (if selected)
                if (k.IsSelected)
                {
                    Vector2 inPt = CanvasPoint(k.Time + k.InHandle.X, k.Value + k.InHandle.Y);
                    Vector2 outPt = CanvasPoint(k.Time + k.OutHandle.X, k.Value + k.OutHandle.Y);

                    DrawLine(p, inPt, ColorHandleLine, 1.2f);
                    DrawCircle(inPt, 3.5f, k.IsInHandleSelected ? Colors.White : ColorHandlePoint);

                    DrawLine(p, outPt, ColorHandleLine, 1.2f);
                    DrawCircle(outPt, 3.5f, k.IsOutHandleSelected ? Colors.White : ColorHandlePoint);
                }

                // Main Key point
                Color ptCol = k.IsSelected ? Colors.Gold : curve.ChannelColor.Lightened(0.3f);
                DrawCircle(p, 4.5f, ptCol);
                DrawArc(p, 4.5f, 0, Mathf.Tau, 12, Colors.Black, 1.2f);
                lastDrawnKeyX = p.X;
            }
        }

        private static Vector2 SampleCubicBezier(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            float u = 1f - t;
            float tt = t * t;
            float uu = u * u;
            float uuu = uu * u;
            float ttt = tt * t;

            return uuu * p0 + 3f * uu * t * p1 + 3f * u * tt * p2 + ttt * p3;
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
                HandleKey(key);
            }
        }

        private void HandleKey(InputEventKey key)
        {
            if (_modalMode != ModalTransformMode.None)
            {
                if (key.Keycode == Key.X)
                {
                    ToggleAxisLock(AxisLock.X);
                    GetViewport().SetInputAsHandled();
                    return;
                }
                if (key.Keycode == Key.Y)
                {
                    ToggleAxisLock(AxisLock.Y);
                    GetViewport().SetInputAsHandled();
                    return;
                }
                if (key.Keycode == Key.Enter || key.Keycode == Key.KpEnter)
                {
                    ConfirmModalTransform();
                    GetViewport().SetInputAsHandled();
                    return;
                }
                if (key.Keycode == Key.Escape)
                {
                    CancelModalTransform();
                    GetViewport().SetInputAsHandled();
                    return;
                }
            }
            else
            {
                if (KeybindsManager.IsActionTriggered("anim_delete_keys", key))
                {
                    DeleteSelectedKeys();
                    GetViewport().SetInputAsHandled();
                    return;
                }
                if (KeybindsManager.IsActionTriggered("anim_insert_key", key))
                {
                    InsertKeyAtPlayhead();
                    GetViewport().SetInputAsHandled();
                    return;
                }
                if (KeybindsManager.IsActionTriggered("anim_duplicate_keys", key))
                {
                    DuplicateSelectedKeys();
                    GetViewport().SetInputAsHandled();
                    return;
                }
                if (KeybindsManager.IsActionTriggered("anim_toggle_modifiers", key))
                {
                    EmitSignal(SignalName.ModifiersSidebarToggled);
                    GetViewport().SetInputAsHandled();
                    return;
                }
                if (KeybindsManager.IsActionTriggered("anim_transform_grab", key))
                {
                    StartModalTransform(ModalTransformMode.Grab);
                    GetViewport().SetInputAsHandled();
                    return;
                }
                if (KeybindsManager.IsActionTriggered("anim_transform_scale", key))
                {
                    StartModalTransform(ModalTransformMode.Scale);
                    GetViewport().SetInputAsHandled();
                    return;
                }
                if (KeybindsManager.IsActionTriggered("anim_transform_rotate", key))
                {
                    StartModalTransform(ModalTransformMode.Rotate);
                    GetViewport().SetInputAsHandled();
                    return;
                }
                if (KeybindsManager.IsActionTriggered("anim_select_all", key))
                {
                    ToggleSelectAll();
                    GetViewport().SetInputAsHandled();
                    return;
                }
                if (KeybindsManager.IsActionTriggered("anim_box_select", key))
                {
                    _currentDragMode = DragMode.BoxSelect;
                    _dragStartMousePos = GetLocalMousePosition();
                    _boxSelectRect = new Rect2(_dragStartMousePos, Vector2.Zero);
                    GetViewport().SetInputAsHandled();
                    return;
                }
                if (KeybindsManager.IsActionTriggered("anim_fit_view", key))
                {
                    FitView();
                    GetViewport().SetInputAsHandled();
                    return;
                }
                if (KeybindsManager.IsActionTriggered("anim_fit_selected", key))
                {
                    FitSelectedView();
                    GetViewport().SetInputAsHandled();
                    return;
                }
                if (KeybindsManager.IsActionTriggered("anim_cheatsheet", key))
                {
                    EmitSignal(SignalName.ShortcutsRequested);
                    GetViewport().SetInputAsHandled();
                    return;
                }
            }
        }

        private void HandleMouseButton(InputEventMouseButton mb)
        {
            Vector2 pos = mb.Position;

            if (_modalMode != ModalTransformMode.None)
            {
                if (mb.ButtonIndex == MouseButton.Left && mb.Pressed)
                {
                    ConfirmModalTransform();
                }
                else if (mb.ButtonIndex == MouseButton.Right && mb.Pressed)
                {
                    CancelModalTransform();
                }
                return;
            }

            if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    if (HitTestHandles(pos)) return;
                    if (HitTestKeyPoints(pos, mb.ShiftPressed)) return;

                    // Empty space: Box select or scrub
                    if (mb.ShiftPressed)
                    {
                        _currentDragMode = DragMode.BoxSelect;
                        _dragStartMousePos = pos;
                        _boxSelectRect = new Rect2(pos, Vector2.Zero);
                    }
                    else
                    {
                        DeselectAll();
                        NotifyKeySelectionChanged();
                        _currentDragMode = DragMode.ScrubPlayhead;
                        SetPlayheadFromMouseX(pos.X);
                    }
                }
                else
                {
                    if (_currentDragMode == DragMode.DragKey || _currentDragMode == DragMode.DragInHandle || _currentDragMode == DragMode.DragOutHandle)
                    {
                        bool hasMoved = _dragStartMousePos.DistanceTo(pos) >= 3f;
                        if (hasMoved)
                        {
                            if (_draggedCurve != null)
                            {
                                _draggedCurve.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
                                ComputeAutoTangents(_draggedCurve);
                            }
                            BakeCurvesToGodotTracks();
                            EmitSignal(SignalName.KeyframesModified);
                        }
                    }
                    else if (_currentDragMode == DragMode.BoxSelect)
                    {
                        CommitBoxSelection();
                    }

                    _currentDragMode = DragMode.None;
                    _draggedKey = null;
                    _draggedCurve = null;
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
                    _pixelsPerSecond = Mathf.Clamp(_pixelsPerSecond * 1.15f, 30f, 1200f);
                }
                else if (mb.ShiftPressed)
                {
                    _pixelsPerValueUnit = Mathf.Clamp(_pixelsPerValueUnit * 1.15f, 5f, 500f);
                }
                else
                {
                    _scrollY += 20f;
                }
                QueueRedraw();
            }
            else if (mb.ButtonIndex == MouseButton.WheelDown)
            {
                if (mb.CtrlPressed)
                {
                    _pixelsPerSecond = Mathf.Clamp(_pixelsPerSecond / 1.15f, 30f, 1200f);
                }
                else if (mb.ShiftPressed)
                {
                    _pixelsPerValueUnit = Mathf.Clamp(_pixelsPerValueUnit / 1.15f, 5f, 500f);
                }
                else
                {
                    _scrollY -= 20f;
                }
                QueueRedraw();
            }
        }

        private void HandleMouseMotion(InputEventMouseMotion mm)
        {
            Vector2 pos = mm.Position;

            if (_modalMode != ModalTransformMode.None)
            {
                UpdateModalTransform(pos);
                return;
            }

            if (_currentDragMode == DragMode.ScrubPlayhead)
            {
                SetPlayheadFromMouseX(pos.X);
            }
            else if (_currentDragMode == DragMode.PanCanvas)
            {
                _scrollX = Math.Max(0f, _scrollX - mm.Relative.X);
                _scrollY += mm.Relative.Y;
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
            else if (_currentDragMode == DragMode.DragKey && _draggedKey != null)
            {
                _draggedKey.Time = Math.Clamp(TimeFromX(pos.X), 0.0, _duration);
                _draggedKey.Value = ValueFromY(pos.Y);
                if (_draggedCurve != null) ComputeAutoTangents(_draggedCurve);
                NotifyKeySelectionChanged();
                QueueRedraw();
            }
            else if (_currentDragMode == DragMode.DragInHandle && _draggedKey != null)
            {
                Vector2 handleCanvasPos = pos;
                float dt = (float)(TimeFromX(handleCanvasPos.X) - _draggedKey.Time);
                float dv = ValueFromY(handleCanvasPos.Y) - _draggedKey.Value;

                _draggedKey.InHandle = new Vector2(dt, dv);
                if (_draggedKey.Mode == TangentMode.Aligned)
                {
                    _draggedKey.OutHandle = new Vector2(-dt, -dv);
                }
                QueueRedraw();
            }
            else if (_currentDragMode == DragMode.DragOutHandle && _draggedKey != null)
            {
                Vector2 handleCanvasPos = pos;
                float dt = (float)(TimeFromX(handleCanvasPos.X) - _draggedKey.Time);
                float dv = ValueFromY(handleCanvasPos.Y) - _draggedKey.Value;

                _draggedKey.OutHandle = new Vector2(dt, dv);
                if (_draggedKey.Mode == TangentMode.Aligned)
                {
                    _draggedKey.InHandle = new Vector2(-dt, -dv);
                }
                QueueRedraw();
            }
        }

        private void CommitBoxSelection()
        {
            foreach (var curve in _curves)
            {
                if (!curve.IsVisible || curve.IsLocked) continue;
                foreach (var k in curve.Keys)
                {
                    Vector2 pt = CanvasPoint(k.Time, k.Value);
                    if (_boxSelectRect.HasPoint(pt))
                    {
                        k.IsSelected = true;
                    }
                }
            }
            NotifyKeySelectionChanged();
        }

        private bool HitTestHandles(Vector2 pos)
        {
            foreach (var curve in _curves)
            {
                if (!curve.IsVisible || curve.IsLocked) continue;
                foreach (var k in curve.Keys)
                {
                    if (!k.IsSelected) continue;

                    if (k.InHandle.LengthSquared() > 0.001f)
                    {
                        Vector2 inPt = CanvasPoint(k.Time + k.InHandle.X, k.Value + k.InHandle.Y);
                        if (pos.DistanceTo(inPt) <= 7f)
                        {
                            k.IsInHandleSelected = true;
                            _currentDragMode = DragMode.DragInHandle;
                            _draggedKey = k;
                            _draggedCurve = curve;
                            _dragStartMousePos = pos;
                            QueueRedraw();
                            return true;
                        }
                    }

                    if (k.OutHandle.LengthSquared() > 0.001f)
                    {
                        Vector2 outPt = CanvasPoint(k.Time + k.OutHandle.X, k.Value + k.OutHandle.Y);
                        if (pos.DistanceTo(outPt) <= 7f)
                        {
                            k.IsOutHandleSelected = true;
                            _currentDragMode = DragMode.DragOutHandle;
                            _draggedKey = k;
                            _draggedCurve = curve;
                            _dragStartMousePos = pos;
                            QueueRedraw();
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private bool HitTestKeyPoints(Vector2 pos, bool isShift)
        {
            foreach (var curve in _curves)
            {
                if (!curve.IsVisible || curve.IsLocked) continue;
                foreach (var k in curve.Keys)
                {
                    Vector2 pt = CanvasPoint(k.Time, k.Value);
                    if (pos.DistanceTo(pt) <= 10f)
                    {
                        if (!isShift)
                        {
                            DeselectAll();
                        }
                        k.IsSelected = true;
                        ActiveCurve = curve;
                        _currentDragMode = DragMode.DragKey;
                        _draggedKey = k;
                        _draggedCurve = curve;
                        _dragStartMousePos = pos;
                        NotifyKeySelectionChanged();
                        QueueRedraw();
                        return true;
                    }
                }
            }
            return false;
        }

        private void DeselectAll()
        {
            foreach (var curve in _curves)
            {
                foreach (var k in curve.Keys)
                {
                    k.IsSelected = false;
                    k.IsInHandleSelected = false;
                    k.IsOutHandleSelected = false;
                }
            }
        }

        private void BakeCurvesToGodotTracks()
        {
            if (_activeAnimation == null || _curves.Count == 0) return;

            var trackGroups = _curves.GroupBy(c => c.TrackIdx);
            foreach (var group in trackGroups)
            {
                int trackIdx = group.Key;
                if (trackIdx < 0 || trackIdx >= _activeAnimation.GetTrackCount()) continue;

                var trackType = _activeAnimation.TrackGetType(trackIdx);
                var curveList = group.ToList();

                var cX = curveList.FirstOrDefault(c => c.SubChannel == 0 || c.SubChannel == 3);
                var cY = curveList.FirstOrDefault(c => c.SubChannel == 1 || c.SubChannel == 4);
                var cZ = curveList.FirstOrDefault(c => c.SubChannel == 2 || c.SubChannel == 5);

                bool hasActiveModifiers = curveList.Any(c => c.Modifiers.Any(m => !m.IsMuted));

                // 1. Determine keyframe times to bake
                List<double> bakeTimes;
                if (hasActiveModifiers)
                {
                    // Dense baking for live viewport animation with modifiers
                    int totalFrames = Math.Max(1, (int)Math.Round(_duration * _fps) + 1);
                    bakeTimes = new List<double>(totalFrames);
                    for (int f = 0; f < totalFrames; f++)
                    {
                        bakeTimes.Add(f / (double)_fps);
                    }
                }
                else
                {
                    // Clean keyframe times from authored keys
                    bakeTimes = curveList
                        .SelectMany(c => c.Keys)
                        .Select(k => Math.Round(k.Time * _fps) / (double)_fps)
                        .Distinct()
                        .OrderBy(t => t)
                        .ToList();
                }

                // 2. Remove obsolete keys from Godot track (keys not in bakeTimes)
                for (int k = _activeAnimation.TrackGetKeyCount(trackIdx) - 1; k >= 0; k--)
                {
                    double t = _activeAnimation.TrackGetKeyTime(trackIdx, k);
                    if (!bakeTimes.Any(bt => Math.Abs(bt - t) < 0.0005))
                    {
                        _activeAnimation.TrackRemoveKey(trackIdx, k);
                    }
                }

                // 3. Insert or update keyframe values
                if (trackType == Godot.Animation.TrackType.Position3D)
                {
                    foreach (double t in bakeTimes)
                    {
                        float x = cX != null ? cX.Evaluate(t, _fps) : 0f;
                        float y = cY != null ? cY.Evaluate(t, _fps) : 0f;
                        float z = cZ != null ? cZ.Evaluate(t, _fps) : 0f;
                        Vector3 val = new(x, y, z);

                        int keyIdx = _activeAnimation.TrackFindKey(trackIdx, t, Godot.Animation.FindMode.Exact);
                        if (keyIdx != -1)
                        {
                            _activeAnimation.TrackSetKeyValue(trackIdx, keyIdx, val);
                        }
                        else
                        {
                            _activeAnimation.PositionTrackInsertKey(trackIdx, t, val);
                        }
                    }
                }
                else if (trackType == Godot.Animation.TrackType.Rotation3D)
                {
                    foreach (double t in bakeTimes)
                    {
                        float degX = cX != null ? cX.Evaluate(t, _fps) : 0f;
                        float degY = cY != null ? cY.Evaluate(t, _fps) : 0f;
                        float degZ = cZ != null ? cZ.Evaluate(t, _fps) : 0f;

                        Vector3 eulerRad = new(Mathf.DegToRad(degX), Mathf.DegToRad(degY), Mathf.DegToRad(degZ));
                        Quaternion q = Quaternion.FromEuler(eulerRad).Normalized();

                        int keyIdx = _activeAnimation.TrackFindKey(trackIdx, t, Godot.Animation.FindMode.Exact);
                        if (keyIdx != -1)
                        {
                            _activeAnimation.TrackSetKeyValue(trackIdx, keyIdx, q);
                        }
                        else
                        {
                            _activeAnimation.RotationTrackInsertKey(trackIdx, t, q);
                        }
                    }
                }
            }
        }

        public bool AddModifier(ChannelCurve curve, FCurveModifier modifier)
        {
            if (curve == null || modifier == null) return false;

            if (modifier is CyclesModifier)
            {
                if (curve.Modifiers.Any(m => m is GaussianSmoothModifier))
                {
                    GD.PrintErr("[GraphEditor] Cannot add Cycles Modifier: Incompatible with Gaussian Smooth Modifier.");
                    return false;
                }
                curve.Modifiers.Insert(0, modifier);
            }
            else if (modifier is GaussianSmoothModifier)
            {
                if (curve.Modifiers.Any(m => m is CyclesModifier))
                {
                    GD.PrintErr("[GraphEditor] Cannot add Gaussian Smooth Modifier: Incompatible with Cycles Modifier.");
                    return false;
                }
                curve.Modifiers.Insert(0, modifier);
            }
            else
            {
                curve.Modifiers.Add(modifier);
            }

            BakeCurvesToGodotTracks();
            EmitSignal(SignalName.KeyframesModified);
            EmitSignal(SignalName.ModifiersModified);
            QueueRedraw();
            return true;
        }

        public void RemoveModifier(ChannelCurve curve, FCurveModifier modifier)
        {
            if (curve == null || modifier == null) return;
            curve.Modifiers.Remove(modifier);
            BakeCurvesToGodotTracks();
            EmitSignal(SignalName.KeyframesModified);
            EmitSignal(SignalName.ModifiersModified);
            QueueRedraw();
        }

        public void RemoveModifiers(ChannelCurve curve, ModifierRemoveMode mode, string typeName = null)
        {
            if (curve == null || curve.Modifiers.Count == 0) return;

            switch (mode)
            {
                case ModifierRemoveMode.RemoveAll:
                    curve.Modifiers.Clear();
                    break;
                case ModifierRemoveMode.RemoveFirst:
                    curve.Modifiers.RemoveAt(0);
                    break;
                case ModifierRemoveMode.RemoveType:
                    if (!string.IsNullOrEmpty(typeName))
                    {
                        curve.Modifiers.RemoveAll(m => m.TypeName.Equals(typeName, StringComparison.OrdinalIgnoreCase));
                    }
                    break;
            }

            BakeCurvesToGodotTracks();
            EmitSignal(SignalName.KeyframesModified);
            EmitSignal(SignalName.ModifiersModified);
            QueueRedraw();
        }

        public void MoveModifier(ChannelCurve curve, int index, int direction)
        {
            if (curve == null || index < 0 || index >= curve.Modifiers.Count) return;
            int targetIdx = index + direction;
            if (targetIdx < 0 || targetIdx >= curve.Modifiers.Count) return;

            var mod = curve.Modifiers[index];
            var targetMod = curve.Modifiers[targetIdx];

            if (mod.RequiresFirst && targetIdx != 0) return;
            if (targetMod.RequiresFirst && index != 0) return;

            curve.Modifiers.RemoveAt(index);
            curve.Modifiers.Insert(targetIdx, mod);

            BakeCurvesToGodotTracks();
            EmitSignal(SignalName.KeyframesModified);
            EmitSignal(SignalName.ModifiersModified);
            QueueRedraw();
        }

        public void NotifyModifiersChanged()
        {
            BakeCurvesToGodotTracks();
            EmitSignal(SignalName.KeyframesModified);
            EmitSignal(SignalName.ModifiersModified);
            QueueRedraw();
        }

        public static float SampleCurveAt(ChannelCurve curve, double time)
        {
            var keys = curve.Keys;
            if (keys.Count == 0) return 0f;
            if (keys.Count == 1 || time <= keys[0].Time) return keys[0].Value;
            if (time >= keys[^1].Time) return keys[^1].Value;

            for (int i = 0; i < keys.Count - 1; i++)
            {
                if (time >= keys[i].Time && time <= keys[i + 1].Time)
                {
                    float factor = (float)((time - keys[i].Time) / (keys[i + 1].Time - keys[i].Time));
                    return Mathf.Lerp(keys[i].Value, keys[i + 1].Value, factor);
                }
            }
            return keys[^1].Value;
        }

        private void SetPlayheadFromMouseX(float mouseX)
        {
            double t = Math.Clamp(TimeFromX(mouseX), 0.0, _duration);
            double frameSnap = 1.0 / _fps;
            t = Math.Round(t / frameSnap) * frameSnap;
            _currentTime = t;
            EmitSignal(SignalName.PlayheadScrubbed, _currentTime);
            QueueRedraw();
        }

        private Vector2 CanvasPoint(double time, float val) => new(XFromTime(time), YFromValue(val));
        public float XFromTime(double time) => (float)(time * _pixelsPerSecond) - _scrollX;
        public double TimeFromX(float x) => (x + _scrollX) / _pixelsPerSecond;
        public float YFromValue(float val) => (Size.Y * 0.5f) - (val * _pixelsPerValueUnit) + _scrollY;
        public float ValueFromY(float y) => ((Size.Y * 0.5f) - y + _scrollY) / _pixelsPerValueUnit;
    }
}
