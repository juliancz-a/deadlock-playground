using System;
using System.Collections.Generic;
using Godot;

namespace DeadlockPlayground.Animation
{
    public interface IAnimationCommand
    {
        string Description { get; }
        void Execute();
        void Undo();
    }

    /// <summary>
    /// Command-pattern Undo/Redo manager for keyframe editing, retiming, and bone pose transforms.
    /// </summary>
    public class AnimationUndoRedoManager
    {
        private readonly List<IAnimationCommand> _undoStack = new();
        private readonly List<IAnimationCommand> _redoStack = new();
        private const int MaxHistory = 100;

        public event Action HistoryChanged;

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        public string UndoDescription => CanUndo ? _undoStack[^1].Description : "";
        public string RedoDescription => CanRedo ? _redoStack[^1].Description : "";

        public void Execute(IAnimationCommand command)
        {
            if (command == null) return;
            command.Execute();
            _undoStack.Add(command);
            if (_undoStack.Count > MaxHistory)
            {
                _undoStack.RemoveAt(0);
            }
            _redoStack.Clear();
            HistoryChanged?.Invoke();
        }

        /// <summary>
        /// Records an already-applied command onto the undo stack without re-executing it.
        /// Useful when an interactive drag updated the animation live and commits a single undo entry on release.
        /// </summary>
        public void RecordExecuted(IAnimationCommand command)
        {
            if (command == null) return;
            _undoStack.Add(command);
            if (_undoStack.Count > MaxHistory)
            {
                _undoStack.RemoveAt(0);
            }
            _redoStack.Clear();
            HistoryChanged?.Invoke();
        }

        public void Undo()
        {
            if (!CanUndo) return;
            var command = _undoStack[^1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            command.Undo();
            _redoStack.Add(command);
            HistoryChanged?.Invoke();
        }

        public void Redo()
        {
            if (!CanRedo) return;
            var command = _redoStack[^1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            command.Execute();
            _undoStack.Add(command);
            HistoryChanged?.Invoke();
        }

        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
            HistoryChanged?.Invoke();
        }
    }

    /// <summary>
    /// Groups multiple animation commands into a single atomic Undo/Redo entry.
    /// </summary>
    public class BatchAnimationCommand : IAnimationCommand
    {
        private readonly List<IAnimationCommand> _commands;
        public string Description { get; }

        public BatchAnimationCommand(string description, IEnumerable<IAnimationCommand> commands)
        {
            Description = description;
            _commands = new List<IAnimationCommand>(commands);
        }

        public void Execute()
        {
            for (int i = 0; i < _commands.Count; i++)
            {
                _commands[i].Execute();
            }
        }

        public void Undo()
        {
            for (int i = _commands.Count - 1; i >= 0; i--)
            {
                _commands[i].Undo();
            }
        }
    }

    public record KeyframeRecord(double Time, Variant Value, Godot.Animation.InterpolationType Interpolation, float Transition = 1.0f);

    /// <summary>
    /// Command that inserts or updates a keyframe on an Animation track.
    /// </summary>
    public class InsertKeyframeCommand : IAnimationCommand
    {
        private readonly Godot.Animation _anim;
        private readonly int _trackIdx;
        private readonly double _time;
        private readonly Variant _value;
        private readonly float _transition;
        private bool _hadExistingKey;
        private Variant _oldValue;
        private bool _stateCaptured;

        public string Description => $"Insert Keyframe at {_time:F2}s";

        public InsertKeyframeCommand(Godot.Animation anim, int trackIdx, double time, Variant value, float transition = 1.0f)
        {
            _anim = anim;
            _trackIdx = trackIdx;
            _time = time;
            _value = value;
            _transition = transition;
        }

        /// <summary>
        /// Constructor for a keyframe edit whose pre-edit state was already captured before an interactive drag.
        /// </summary>
        public InsertKeyframeCommand(Godot.Animation anim, int trackIdx, double time, Variant newValue, bool hadExistingKey, Variant oldValue, float transition = 1.0f)
        {
            _anim = anim;
            _trackIdx = trackIdx;
            _time = time;
            _value = newValue;
            _hadExistingKey = hadExistingKey;
            _oldValue = oldValue;
            _transition = transition;
            _stateCaptured = true;
        }

        public void Execute()
        {
            if (_anim == null || _trackIdx < 0 || _trackIdx >= _anim.GetTrackCount()) return;

            int existingIdx = _anim.TrackFindKey(_trackIdx, _time, Godot.Animation.FindMode.Approx);
            if (existingIdx != -1 && Math.Abs(_anim.TrackGetKeyTime(_trackIdx, existingIdx) - _time) > 1e-4)
            {
                existingIdx = -1;
            }

            if (!_stateCaptured)
            {
                _hadExistingKey = existingIdx != -1;
                if (_hadExistingKey)
                {
                    _oldValue = _anim.TrackGetKeyValue(_trackIdx, existingIdx);
                }
                _stateCaptured = true;
            }

            if (existingIdx != -1)
            {
                _anim.TrackSetKeyValue(_trackIdx, existingIdx, _value);
            }
            else
            {
                var trackType = _anim.TrackGetType(_trackIdx);
                if (trackType == Godot.Animation.TrackType.Position3D)
                {
                    _anim.PositionTrackInsertKey(_trackIdx, _time, (Vector3)_value);
                }
                else if (trackType == Godot.Animation.TrackType.Rotation3D)
                {
                    _anim.RotationTrackInsertKey(_trackIdx, _time, (Quaternion)_value);
                }
                else
                {
                    _anim.TrackInsertKey(_trackIdx, _time, _value, _transition);
                }
            }
        }

        public void Undo()
        {
            if (_anim == null || _trackIdx < 0 || _trackIdx >= _anim.GetTrackCount()) return;

            int keyIdx = _anim.TrackFindKey(_trackIdx, _time, Godot.Animation.FindMode.Approx);
            if (keyIdx != -1 && Math.Abs(_anim.TrackGetKeyTime(_trackIdx, keyIdx) - _time) > 1e-4)
            {
                keyIdx = -1;
            }

            if (_hadExistingKey)
            {
                if (keyIdx != -1)
                {
                    _anim.TrackSetKeyValue(_trackIdx, keyIdx, _oldValue);
                }
                else
                {
                    var trackType = _anim.TrackGetType(_trackIdx);
                    if (trackType == Godot.Animation.TrackType.Position3D)
                        _anim.PositionTrackInsertKey(_trackIdx, _time, (Vector3)_oldValue);
                    else if (trackType == Godot.Animation.TrackType.Rotation3D)
                        _anim.RotationTrackInsertKey(_trackIdx, _time, (Quaternion)_oldValue);
                    else
                        _anim.TrackInsertKey(_trackIdx, _time, _oldValue, _transition);
                }
            }
            else
            {
                if (keyIdx != -1)
                {
                    _anim.TrackRemoveKey(_trackIdx, keyIdx);
                }
            }
        }
    }

    /// <summary>
    /// Command that deletes one or more keyframes.
    /// </summary>
    public class DeleteKeyframesCommand : IAnimationCommand
    {
        private readonly Godot.Animation _anim;
        private readonly List<(int TrackIdx, double Time, Variant Value, float Transition)> _deletedKeys;

        public string Description => $"Delete {_deletedKeys.Count} Keyframe(s)";

        public DeleteKeyframesCommand(Godot.Animation anim, List<(int TrackIdx, double Time, Variant Value, float Transition)> keysToDelete)
        {
            _anim = anim;
            _deletedKeys = new List<(int, double, Variant, float)>(keysToDelete);
        }

        public void Execute()
        {
            if (_anim == null) return;
            foreach (var (trackIdx, time, _, _) in _deletedKeys)
            {
                if (trackIdx < 0 || trackIdx >= _anim.GetTrackCount()) continue;
                int keyIdx = _anim.TrackFindKey(trackIdx, time, Godot.Animation.FindMode.Exact);
                if (keyIdx != -1)
                {
                    _anim.TrackRemoveKey(trackIdx, keyIdx);
                }
            }
        }

        public void Undo()
        {
            if (_anim == null) return;
            foreach (var (trackIdx, time, val, trans) in _deletedKeys)
            {
                if (trackIdx < 0 || trackIdx >= _anim.GetTrackCount()) continue;
                var trackType = _anim.TrackGetType(trackIdx);
                if (trackType == Godot.Animation.TrackType.Position3D)
                {
                    _anim.PositionTrackInsertKey(trackIdx, time, (Vector3)val);
                }
                else if (trackType == Godot.Animation.TrackType.Rotation3D)
                {
                    _anim.RotationTrackInsertKey(trackIdx, time, (Quaternion)val);
                }
                else
                {
                    _anim.TrackInsertKey(trackIdx, time, val, trans);
                }
            }
        }
    }

    /// <summary>
    /// Command that moves/retimes keyframes from old times to new times using lossless per-track snapshots.
    /// Handles adjacent multi-key shifts, overlapping destination times, and floating-point precision safely.
    /// </summary>
    public class MoveKeyframesCommand : IAnimationCommand
    {
        private readonly struct TrackKeySnapshot
        {
            public readonly double Time;
            public readonly Variant Value;
            public readonly float Transition;

            public TrackKeySnapshot(double time, Variant value, float transition)
            {
                Time = time;
                Value = value;
                Transition = transition;
            }
        }

        private readonly Godot.Animation _anim;
        private readonly List<(int TrackIdx, double OldTime, double NewTime)> _moves;
        private readonly Dictionary<int, List<TrackKeySnapshot>> _beforeSnapshots = new();
        private readonly Dictionary<int, List<TrackKeySnapshot>> _afterSnapshots = new();
        private bool _builtSnapshots;

        public string Description => $"Move {_moves.Count} Keyframe(s)";

        public MoveKeyframesCommand(Godot.Animation anim, List<(int TrackIdx, double OldTime, double NewTime)> moves)
        {
            _anim = anim;
            _moves = new List<(int, double, double)>(moves);
        }

        public void Execute()
        {
            if (_anim == null) return;
            if (!_builtSnapshots)
            {
                BuildSnapshots();
                _builtSnapshots = true;
            }
            ApplySnapshots(_afterSnapshots);
        }

        public void Undo()
        {
            if (_anim == null || !_builtSnapshots) return;
            ApplySnapshots(_beforeSnapshots);
        }

        private void BuildSnapshots()
        {
            var movesByTrack = new Dictionary<int, List<(double OldTime, double NewTime)>>();
            foreach (var (trackIdx, oldTime, newTime) in _moves)
            {
                if (trackIdx < 0 || trackIdx >= _anim.GetTrackCount()) continue;
                if (!movesByTrack.TryGetValue(trackIdx, out var list))
                {
                    list = new List<(double, double)>();
                    movesByTrack[trackIdx] = list;
                }
                list.Add((oldTime, newTime));
            }

            foreach (var (trackIdx, trackMoves) in movesByTrack)
            {
                int keyCount = _anim.TrackGetKeyCount(trackIdx);
                var before = new List<TrackKeySnapshot>(keyCount);
                for (int k = 0; k < keyCount; k++)
                {
                    before.Add(new TrackKeySnapshot(
                        _anim.TrackGetKeyTime(trackIdx, k),
                        _anim.TrackGetKeyValue(trackIdx, k),
                        _anim.TrackGetKeyTransition(trackIdx, k)
                    ));
                }
                _beforeSnapshots[trackIdx] = before;

                // Match each move to its closest original key index (avoiding duplicate matches)
                var movedIndices = new HashSet<int>();
                var movedNewKeys = new List<TrackKeySnapshot>(trackMoves.Count);

                foreach (var (oldTime, newTime) in trackMoves)
                {
                    int bestIdx = -1;
                    double bestDiff = 1e-3;
                    for (int k = 0; k < before.Count; k++)
                    {
                        if (movedIndices.Contains(k)) continue;
                        double diff = Math.Abs(before[k].Time - oldTime);
                        if (diff < bestDiff)
                        {
                            bestDiff = diff;
                            bestIdx = k;
                        }
                    }

                    if (bestIdx != -1)
                    {
                        movedIndices.Add(bestIdx);
                        var orig = before[bestIdx];
                        movedNewKeys.Add(new TrackKeySnapshot(newTime, orig.Value, orig.Transition));
                    }
                }

                // Keep all unmoved keys that are not overwritten by a moved key landing on the exact same timestamp
                var after = new List<TrackKeySnapshot>(before.Count);
                for (int k = 0; k < before.Count; k++)
                {
                    if (movedIndices.Contains(k)) continue;
                    double t = before[k].Time;
                    bool overwritten = false;
                    for (int m = 0; m < movedNewKeys.Count; m++)
                    {
                        if (Math.Abs(movedNewKeys[m].Time - t) < 1e-4)
                        {
                            overwritten = true;
                            break;
                        }
                    }
                    if (!overwritten)
                    {
                        after.Add(before[k]);
                    }
                }

                // Deduplicate moved keys if multiple landed on the same clamped boundary
                foreach (var mk in movedNewKeys)
                {
                    int existing = after.FindIndex(a => Math.Abs(a.Time - mk.Time) < 1e-4);
                    if (existing >= 0)
                        after[existing] = mk;
                    else
                        after.Add(mk);
                }

                after.Sort((a, b) => a.Time.CompareTo(b.Time));
                _afterSnapshots[trackIdx] = after;
            }
        }

        private void ApplySnapshots(Dictionary<int, List<TrackKeySnapshot>> snapshots)
        {
            foreach (var (trackIdx, keys) in snapshots)
            {
                if (trackIdx < 0 || trackIdx >= _anim.GetTrackCount()) continue;

                // Clear track keys in reverse order
                for (int k = _anim.TrackGetKeyCount(trackIdx) - 1; k >= 0; k--)
                {
                    _anim.TrackRemoveKey(trackIdx, k);
                }

                var trackType = _anim.TrackGetType(trackIdx);
                for (int i = 0; i < keys.Count; i++)
                {
                    var snap = keys[i];
                    if (trackType == Godot.Animation.TrackType.Position3D)
                    {
                        _anim.PositionTrackInsertKey(trackIdx, snap.Time, (Vector3)snap.Value);
                    }
                    else if (trackType == Godot.Animation.TrackType.Rotation3D)
                    {
                        _anim.RotationTrackInsertKey(trackIdx, snap.Time, (Quaternion)snap.Value);
                    }
                    else
                    {
                        _anim.TrackInsertKey(trackIdx, snap.Time, snap.Value, snap.Transition);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Command for modifying a 3D bone transform (position and/or rotation).
    /// </summary>
    public class BoneTransformCommand : IAnimationCommand
    {
        private readonly Skeleton3D _skeleton;
        private readonly int _boneIdx;
        private readonly Vector3 _oldPos;
        private readonly Quaternion _oldRot;
        private readonly Vector3 _newPos;
        private readonly Quaternion _newRot;

        public string Description => $"Transform Bone #{_boneIdx}";

        public BoneTransformCommand(Skeleton3D skeleton, int boneIdx, Vector3 oldPos, Quaternion oldRot, Vector3 newPos, Quaternion newRot)
        {
            _skeleton = skeleton;
            _boneIdx = boneIdx;
            _oldPos = oldPos;
            _oldRot = oldRot;
            _newPos = newPos;
            _newRot = newRot;
        }

        public void Execute()
        {
            if (_skeleton == null || _boneIdx < 0 || _boneIdx >= _skeleton.GetBoneCount()) return;
            _skeleton.SetBonePosePosition(_boneIdx, _newPos);
            _skeleton.SetBonePoseRotation(_boneIdx, _newRot);
            _skeleton.ForceUpdateAllBoneTransforms();
        }

        public void Undo()
        {
            if (_skeleton == null || _boneIdx < 0 || _boneIdx >= _skeleton.GetBoneCount()) return;
            _skeleton.SetBonePosePosition(_boneIdx, _oldPos);
            _skeleton.SetBonePoseRotation(_boneIdx, _oldRot);
            _skeleton.ForceUpdateAllBoneTransforms();
        }
    }

    /// <summary>
    /// Command for applying an additive delta rotation and position offset across all keyframes of a bone track in an animation.
    /// </summary>
    public class ApplyAdditiveClipOffsetCommand : IAnimationCommand
    {
        private readonly Godot.Animation _anim;
        private readonly int _rotTrack;
        private readonly int _posTrack;
        private readonly string _boneName;

        public struct KeyBackup<T>
        {
            public double Time;
            public T Value;
        }

        private readonly List<KeyBackup<Quaternion>> _oldRotKeys = new();
        private readonly List<KeyBackup<Quaternion>> _newRotKeys = new();
        private readonly List<KeyBackup<Vector3>> _oldPosKeys = new();
        private readonly List<KeyBackup<Vector3>> _newPosKeys = new();

        public string Description => $"Apply Additive Clip Offset to {_boneName}";

        public ApplyAdditiveClipOffsetCommand(
            Godot.Animation anim,
            int rotTrack,
            int posTrack,
            string boneName,
            Quaternion deltaRot,
            Vector3 deltaPos)
        {
            _anim = anim;
            _rotTrack = rotTrack;
            _posTrack = posTrack;
            _boneName = boneName;

            if (_anim != null && _rotTrack >= 0 && _rotTrack < _anim.GetTrackCount())
            {
                int count = _anim.TrackGetKeyCount(_rotTrack);
                for (int k = 0; k < count; k++)
                {
                    double t = _anim.TrackGetKeyTime(_rotTrack, k);
                    Quaternion oldQ = _anim.TrackGetKeyValue(_rotTrack, k).AsQuaternion();
                    Quaternion newQ = (oldQ * deltaRot).Normalized();
                    if (newQ.W < 0)
                    {
                        newQ = new Quaternion(-newQ.X, -newQ.Y, -newQ.Z, -newQ.W);
                    }
                    _oldRotKeys.Add(new KeyBackup<Quaternion> { Time = t, Value = oldQ });
                    _newRotKeys.Add(new KeyBackup<Quaternion> { Time = t, Value = newQ });
                }
            }

            if (_anim != null && _posTrack >= 0 && _posTrack < _anim.GetTrackCount() && deltaPos.LengthSquared() > 1e-6f)
            {
                int count = _anim.TrackGetKeyCount(_posTrack);
                for (int k = 0; k < count; k++)
                {
                    double t = _anim.TrackGetKeyTime(_posTrack, k);
                    Vector3 oldP = _anim.TrackGetKeyValue(_posTrack, k).AsVector3();
                    Vector3 newP = oldP + deltaPos;
                    _oldPosKeys.Add(new KeyBackup<Vector3> { Time = t, Value = oldP });
                    _newPosKeys.Add(new KeyBackup<Vector3> { Time = t, Value = newP });
                }
            }
        }

        public void Execute()
        {
            if (_anim == null) return;
            if (_rotTrack >= 0 && _rotTrack < _anim.GetTrackCount())
            {
                for (int k = 0; k < _newRotKeys.Count; k++)
                {
                    _anim.TrackSetKeyValue(_rotTrack, k, _newRotKeys[k].Value);
                }
            }
            if (_posTrack >= 0 && _posTrack < _anim.GetTrackCount())
            {
                for (int k = 0; k < _newPosKeys.Count; k++)
                {
                    _anim.TrackSetKeyValue(_posTrack, k, _newPosKeys[k].Value);
                }
            }
        }

        public void Undo()
        {
            if (_anim == null) return;
            if (_rotTrack >= 0 && _rotTrack < _anim.GetTrackCount())
            {
                for (int k = 0; k < _oldRotKeys.Count; k++)
                {
                    _anim.TrackSetKeyValue(_rotTrack, k, _oldRotKeys[k].Value);
                }
            }
            if (_posTrack >= 0 && _posTrack < _anim.GetTrackCount())
            {
                for (int k = 0; k < _oldPosKeys.Count; k++)
                {
                    _anim.TrackSetKeyValue(_posTrack, k, _oldPosKeys[k].Value);
                }
            }
        }
    }
}
