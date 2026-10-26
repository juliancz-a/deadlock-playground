using System;
using System.Collections.Generic;
using Godot;

namespace DeadlockPlayground.Animation
{
    /// <summary>
    /// High-performance keyframe cache layer for Godot.Animation tracks.
    /// Eliminates per-key C++ interop during canvas drawing, hit testing, and selection.
    /// </summary>
    public class AnimationKeyCache
    {
        private Godot.Animation _animation;
        private readonly Dictionary<int, double[]> _trackTimes = new();
        private readonly Dictionary<string, (int posTrack, int rotTrack)> _boneTracks = new(StringComparer.OrdinalIgnoreCase);
        private double[] _summaryTimes = Array.Empty<double>();
        private float _fps = 30f;
        private bool _isDirty = false;

        public bool IsDirty => _isDirty;
        public double[] SummaryTimes => _summaryTimes;
        public IReadOnlyDictionary<string, (int posTrack, int rotTrack)> BoneTracks => _boneTracks;

        public void SetAnimation(Godot.Animation anim, Skeleton3D skeleton, float fps = 30f)
        {
            _animation = anim;
            _fps = fps > 0 ? fps : 30f;
            InvalidateAll();
            Rebuild(skeleton);
        }

        public void InvalidateAll()
        {
            _trackTimes.Clear();
            _boneTracks.Clear();
            _summaryTimes = Array.Empty<double>();
        }

        public void InvalidateTrack(int trackIdx)
        {
            _trackTimes.Remove(trackIdx);
        }

        public void Rebuild(Skeleton3D skeleton)
        {
            _trackTimes.Clear();
            _boneTracks.Clear();

            if (_animation == null || !GodotObject.IsInstanceValid(_animation))
            {
                _summaryTimes = Array.Empty<double>();
                return;
            }

            int trackCount = _animation.GetTrackCount();

            // 1. Single-pass over all tracks to parse bone names and cache times
            var summarySet = new HashSet<int>(); // Keyed by frame index = Math.Round(t * fps)
            var summaryList = new List<double>();

            for (int t = 0; t < trackCount; t++)
            {
                int kCount = _animation.TrackGetKeyCount(t);
                double[] times = new double[kCount];
                for (int k = 0; k < kCount; k++)
                {
                    double time = _animation.TrackGetKeyTime(t, k);
                    times[k] = time;
                    int frame = (int)Math.Round(time * _fps);
                    if (summarySet.Add(frame))
                    {
                        summaryList.Add(time);
                    }
                }
                _trackTimes[t] = times;

                string path = _animation.TrackGetPath(t).ToString();
                int colonIdx = path.LastIndexOf(':');
                string boneName = colonIdx >= 0 ? path.Substring(colonIdx + 1) : path;

                var type = _animation.TrackGetType(t);
                if (_boneTracks.TryGetValue(boneName, out var pair))
                {
                    if (type == Godot.Animation.TrackType.Position3D) pair.posTrack = t;
                    else if (type == Godot.Animation.TrackType.Rotation3D) pair.rotTrack = t;
                    _boneTracks[boneName] = pair;
                }
                else
                {
                    int p = type == Godot.Animation.TrackType.Position3D ? t : -1;
                    int r = type == Godot.Animation.TrackType.Rotation3D ? t : -1;
                    _boneTracks[boneName] = (p, r);
                }
            }

            // 2. Sort summary times
            summaryList.Sort();
            _summaryTimes = summaryList.ToArray();
        }

        public double[] GetTrackTimes(int trackIdx)
        {
            if (trackIdx < 0 || _animation == null) return Array.Empty<double>();

            if (_trackTimes.TryGetValue(trackIdx, out var cached))
            {
                return cached;
            }

            if (trackIdx >= _animation.GetTrackCount()) return Array.Empty<double>();

            int kCount = _animation.TrackGetKeyCount(trackIdx);
            double[] times = new double[kCount];
            for (int k = 0; k < kCount; k++)
            {
                times[k] = _animation.TrackGetKeyTime(trackIdx, k);
            }
            _trackTimes[trackIdx] = times;
            return times;
        }

        /// <summary>
        /// Returns the index range [startIdx, endIdx] of keys falling within [minTime, maxTime] using binary search.
        /// If no keys in range, returns (0, -1).
        /// </summary>
        public (int startIdx, int endIdx) GetVisibleKeyRange(int trackIdx, double minTime, double maxTime)
        {
            var times = GetTrackTimes(trackIdx);
            if (times.Length == 0) return (0, -1);

            int start = Array.BinarySearch(times, minTime);
            if (start < 0) start = ~start;

            if (start >= times.Length) return (0, -1);

            int end = Array.BinarySearch(times, maxTime);
            if (end < 0) end = (~end) - 1;

            if (end < start) return (0, -1);

            return (start, Math.Min(end, times.Length - 1));
        }

        /// <summary>
        /// Returns the index range [startIdx, endIdx] of summary keys falling within [minTime, maxTime].
        /// </summary>
        public (int startIdx, int endIdx) GetVisibleSummaryRange(double minTime, double maxTime)
        {
            if (_summaryTimes.Length == 0) return (0, -1);

            int start = Array.BinarySearch(_summaryTimes, minTime);
            if (start < 0) start = ~start;

            if (start >= _summaryTimes.Length) return (0, -1);

            int end = Array.BinarySearch(_summaryTimes, maxTime);
            if (end < 0) end = (~end) - 1;

            if (end < start) return (0, -1);

            return (start, Math.Min(end, _summaryTimes.Length - 1));
        }
    }
}
