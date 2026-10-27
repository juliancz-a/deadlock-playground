#pragma warning disable CS0618
using System;
using Godot;

namespace DeadlockPlayground.Animation
{
    /// <summary>
    /// Centralized single source of truth for transport & animation playback controls.
    /// Synchronizes PlayerTabUI, AnimationStudioDock, and hotkeys.
    /// </summary>
    public class AnimationTransportService
    {
        public static AnimationTransportService Instance { get; } = new();

        public event Action<bool> PlaybackStateChanged;
        public event Action<double> TimeChanged;
        public event Action<bool> LoopChanged;
        public event Action<string> AnimationChanged;
        public event Action<float> SpeedChanged;

        private AnimationPlayer _animPlayer;
        private Skeleton3D _skeleton;
        private string _activeAnimName = "";
        private Godot.Animation _activeAnimation;
        private float _fps = 30f;
        private float _speed = 1.0f;
        private bool _isLooping = true;
        private bool _isPlaying = false;
        private double _currentTime = 0.0;
        private ulong _lastTickedFrame = ulong.MaxValue;
        private bool _isParticleMode = false;

        public AnimationPlayer Player => _animPlayer;
        public Skeleton3D Skeleton => _skeleton;
        public string ActiveAnimName => _activeAnimName;
        public Godot.Animation ActiveAnimation => _activeAnimation;
        public float Fps { get => _fps; set => _fps = value > 0 ? value : 30f; }
        public float Speed => _speed;
        public bool IsLooping => _isLooping;
        public bool IsPlaying => _isPlaying;
        public double CurrentTime => _currentTime;
        public double Length => _activeAnimation != null ? Math.Max(0.01, _activeAnimation.Length) : 0.0;
        public bool IsParticleMode => _isParticleMode;

        public void SetParticleMode(bool isParticle)
        {
            _isParticleMode = isParticle;
            if (isParticle && _isPlaying)
            {
                Pause();
            }
        }

        public void Bind(AnimationPlayer player, Skeleton3D skeleton, string animName, float fps = 30f)
        {
            if (_animPlayer != null && GodotObject.IsInstanceValid(_animPlayer))
            {
                try
                {
                    if (_animPlayer.IsConnected(AnimationMixer.SignalName.AnimationFinished, Callable.From<StringName>(OnAnimationFinishedInternal)))
                    {
                        _animPlayer.Disconnect(AnimationMixer.SignalName.AnimationFinished, Callable.From<StringName>(OnAnimationFinishedInternal));
                    }
                }
                catch { }
            }

            _animPlayer = player;
            _skeleton = skeleton;
            _activeAnimName = animName ?? "";
            _fps = fps > 0 ? fps : 30f;

            if (_animPlayer != null && GodotObject.IsInstanceValid(_animPlayer) && !string.IsNullOrEmpty(_activeAnimName) && _animPlayer.HasAnimation(_activeAnimName))
            {
                _activeAnimation = _animPlayer.GetAnimation(_activeAnimName);
                if (_activeAnimation != null)
                {
                    _activeAnimation.LoopMode = _isLooping ? Godot.Animation.LoopModeEnum.Linear : Godot.Animation.LoopModeEnum.None;
                }
                _speed = (float)_animPlayer.SpeedScale;
                _currentTime = !string.IsNullOrEmpty(_animPlayer.CurrentAnimation) ? _animPlayer.CurrentAnimationPosition : 0.0;
                _isPlaying = _animPlayer.IsPlaying();

                var callable = Callable.From<StringName>(OnAnimationFinishedInternal);
                if (!_animPlayer.IsConnected(AnimationMixer.SignalName.AnimationFinished, callable))
                {
                    _animPlayer.Connect(AnimationMixer.SignalName.AnimationFinished, callable);
                }
            }
            else
            {
                _activeAnimation = null;
                _isPlaying = false;
                _currentTime = 0.0;
            }

            AnimationChanged?.Invoke(_activeAnimName);
            LoopChanged?.Invoke(_isLooping);
            SpeedChanged?.Invoke(_speed);
            PlaybackStateChanged?.Invoke(_isPlaying);
            TimeChanged?.Invoke(_currentTime);
        }

        public void Unbind()
        {
            Bind(null, null, "", _fps);
        }

        public void Play()
        {
            if (_isParticleMode) return;
            if (_animPlayer == null || !GodotObject.IsInstanceValid(_animPlayer) || string.IsNullOrEmpty(_activeAnimName)) return;

            // Restart at end rule
            if (_currentTime >= Length - 0.05)
            {
                Seek(0.0, update: true, conformCloth: false);
            }

            _animPlayer.Play(_activeAnimName);
            _isPlaying = true;
            PlaybackStateChanged?.Invoke(true);
        }

        public void Pause()
        {
            if (_animPlayer != null && GodotObject.IsInstanceValid(_animPlayer))
            {
                _animPlayer.Pause();
            }
            if (_isPlaying)
            {
                _isPlaying = false;
                PlaybackStateChanged?.Invoke(false);
            }

            UpdateSkeletonCloth(force: true);
        }

        public void Toggle()
        {
            if (_isPlaying)
            {
                Pause();
            }
            else
            {
                Play();
            }
        }

        public void TogglePlay() => Toggle();

        public void Stop()
        {
            if (_animPlayer != null && GodotObject.IsInstanceValid(_animPlayer))
            {
                _animPlayer.Stop();
            }
            _isPlaying = false;
            PlaybackStateChanged?.Invoke(false);
            Seek(0.0, update: true, conformCloth: true);
        }

        public void Seek(double time, bool update = true, bool conformCloth = true)
        {
            double dur = Length;
            _currentTime = Math.Clamp(time, 0.0, dur);

            if (_animPlayer != null && GodotObject.IsInstanceValid(_animPlayer) && !string.IsNullOrEmpty(_activeAnimName) && _animPlayer.HasAnimation(_activeAnimName))
            {
                if (_animPlayer.CurrentAnimation != _activeAnimName)
                {
                    _animPlayer.Play(_activeAnimName);
                    if (!_isPlaying)
                    {
                        _animPlayer.Pause();
                    }
                }
                _animPlayer.Seek(_currentTime, update: update);
                if (!_isPlaying)
                {
                    _animPlayer.Advance(0.0001);
                    _animPlayer.Seek(_currentTime, update: true);
                    _skeleton?.ForceUpdateAllBoneTransforms();
                }
            }

            TimeChanged?.Invoke(_currentTime);

            if (conformCloth)
            {
                UpdateSkeletonCloth(force: true);
            }
        }

        public void Step(int frames)
        {
            int currentFrame = (int)Math.Round(_currentTime * _fps);
            int targetFrame = currentFrame + frames;
            int totalFrames = (int)Math.Round(Length * _fps);
            targetFrame = Math.Clamp(targetFrame, 0, Math.Max(0, totalFrames));
            Seek(targetFrame / (double)_fps, update: true, conformCloth: true);
        }

        public void SetLoop(bool looping)
        {
            _isLooping = looping;
            if (_activeAnimation != null)
            {
                _activeAnimation.LoopMode = looping ? Godot.Animation.LoopModeEnum.Linear : Godot.Animation.LoopModeEnum.None;
            }
            LoopChanged?.Invoke(_isLooping);
        }

        public void SetSpeed(float speed)
        {
            _speed = speed > 0 ? speed : 1.0f;
            if (_animPlayer != null && GodotObject.IsInstanceValid(_animPlayer))
            {
                _animPlayer.SpeedScale = _speed;
            }
            SpeedChanged?.Invoke(_speed);
        }

        public void Tick(double delta)
        {
            ulong currentFrame = Engine.GetProcessFrames();
            if (currentFrame == _lastTickedFrame) return;
            _lastTickedFrame = currentFrame;

            if (_isParticleMode) return;
            if (_animPlayer == null || !GodotObject.IsInstanceValid(_animPlayer) || string.IsNullOrEmpty(_activeAnimName)) return;

            bool playerIsPlaying = _animPlayer.IsPlaying();
            if (_isPlaying != playerIsPlaying)
            {
                _isPlaying = playerIsPlaying;
                PlaybackStateChanged?.Invoke(_isPlaying);
            }

            if (_isPlaying)
            {
                double pos = _animPlayer.CurrentAnimationPosition;
                if (Math.Abs(pos - _currentTime) > 0.0001)
                {
                    _currentTime = pos;
                    TimeChanged?.Invoke(_currentTime);
                }

                // Handle non-looping end
                if (!_isLooping && _currentTime >= Length - 0.03)
                {
                    Pause();
                }

                UpdateSkeletonCloth(force: false);
            }
        }

        public void UpdateSkeletonCloth(bool force)
        {
            if (_skeleton != null && GodotObject.IsInstanceValid(_skeleton) && !_skeleton.IsQueuedForDeletion())
            {
                _skeleton.ForceUpdateAllBoneTransforms();
                ProceduralClothSolver.MarkDirty();
                ProceduralClothSolver.Conform(_skeleton, force: force, _animPlayer);
            }
        }

        private void OnAnimationFinishedInternal(StringName animName)
        {
            if (animName.ToString() == _activeAnimName && !_isLooping)
            {
                _isPlaying = false;
                PlaybackStateChanged?.Invoke(false);
                UpdateSkeletonCloth(force: true);
            }
        }
    }
}
