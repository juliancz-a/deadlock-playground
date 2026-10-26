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

    public record KeyframeRecord(double Time, Variant Value, Godot.Animation.InterpolationType Interpolation, float Transition = 1.0f);

    /// <summary>
    /// Command that inserts keyframes into an Animation track.
    /// </summary>
    public class InsertKeyframeCommand : IAnimationCommand
    {
        private readonly Godot.Animation _anim;
        private readonly int _trackIdx;
        private readonly double _time;
        private readonly Variant _value;
        private readonly float _transition;
        private int _existingKeyIdx = -1;
        private Variant _oldValue;

        public string Description => $"Insert Keyframe at {_time:F2}s";

        public InsertKeyframeCommand(Godot.Animation anim, int trackIdx, double time, Variant value, float transition = 1.0f)
        {
            _anim = anim;
            _trackIdx = trackIdx;
            _time = time;
            _value = value;
            _transition = transition;
        }

        public void Execute()
        {
            if (_anim == null || _trackIdx < 0 || _trackIdx >= _anim.GetTrackCount()) return;

            // Check if key already exists at this exact time
            _existingKeyIdx = _anim.TrackFindKey(_trackIdx, _time, Godot.Animation.FindMode.Exact);
            if (_existingKeyIdx != -1)
            {
                _oldValue = _anim.TrackGetKeyValue(_trackIdx, _existingKeyIdx);
                _anim.TrackSetKeyValue(_trackIdx, _existingKeyIdx, _value);
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

            if (_existingKeyIdx != -1)
            {
                // Restore previous value
                int keyIdx = _anim.TrackFindKey(_trackIdx, _time, Godot.Animation.FindMode.Exact);
                if (keyIdx != -1)
                {
                    _anim.TrackSetKeyValue(_trackIdx, keyIdx, _oldValue);
                }
            }
            else
            {
                int keyIdx = _anim.TrackFindKey(_trackIdx, _time, Godot.Animation.FindMode.Exact);
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
    /// Command that moves/retimes keyframes from old times to new times.
    /// </summary>
    public class MoveKeyframesCommand : IAnimationCommand
    {
        private readonly Godot.Animation _anim;
        private readonly List<(int TrackIdx, double OldTime, double NewTime)> _moves;

        public string Description => $"Move {_moves.Count} Keyframe(s)";

        public MoveKeyframesCommand(Godot.Animation anim, List<(int TrackIdx, double OldTime, double NewTime)> moves)
        {
            _anim = anim;
            _moves = new List<(int, double, double)>(moves);
        }

        public void Execute()
        {
            ApplyTimes(isUndo: false);
        }

        public void Undo()
        {
            ApplyTimes(isUndo: true);
        }

        private void ApplyTimes(bool isUndo)
        {
            if (_anim == null) return;
            foreach (var (trackIdx, oldTime, newTime) in _moves)
            {
                if (trackIdx < 0 || trackIdx >= _anim.GetTrackCount()) continue;
                double from = isUndo ? newTime : oldTime;
                double to = isUndo ? oldTime : newTime;
                int keyIdx = _anim.TrackFindKey(trackIdx, from, Godot.Animation.FindMode.Exact);
                if (keyIdx != -1)
                {
                    _anim.TrackSetKeyTime(trackIdx, keyIdx, to);
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
