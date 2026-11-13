using System;
using System.Collections.Generic;
using Godot;
using ValveResourceFormat.Particles;

namespace DeadlockPlayground.Particles;

public enum VfxPlaybackMode
{
    Normal,
    NormalWithEndCap,
    EndCapOnly
}

/// <summary>
/// Top-level deterministic CPU particle simulator orchestrating an entire .vpcf hierarchy.
/// Owns the control point registry (CP0 socket, CP1 charge, CP10 target) and handles
/// fixed-dt stepping, timeline seeking, EndCap state transitions, and auto-charge playback.
/// </summary>
public class CpuParticleSimulator
{
    private readonly List<ParticleSystemInstance> _instances = new();
    private readonly ControlPointRegistry _cpRegistry;
    private readonly Dictionary<int, (Vector3 Pos, Quaternion Rot, float Val, bool HasVal)> _lastSyncedCps = new();

    private ParticleSystemSimulation _rootVrfSimulation;
    private GodotParticleCollision _collision;
    private System.Numerics.Vector3 _cameraPosSource2 = System.Numerics.Vector3.Zero;
    private bool _hasCameraPos = false;
    private VfxPlaybackMode _playbackMode = VfxPlaybackMode.Normal;
    private bool _collideWithFloor = false;

    private float _currentTime = 0.0f;
    private float _duration = 3.0f;
    private float _speedScale = 1.0f;
    private bool _isPlaying = true;
    private bool _hasEnded = false;

    // Automated loop parameters (ramp CP1 0->1 over 2.5s, release EndCap, loop at 3.0s)
    public bool AutoChargeLoop { get; set; } = false;
    public float AutoChargeDuration { get; set; } = 2.5f;
    public bool IsLooping { get; set; } = true;

    public ControlPointRegistry ControlPoints => _cpRegistry;
    public IReadOnlyList<ParticleSystemInstance> Instances => _instances;
    public ParticleSystemSimulation RootVrfSimulation => _rootVrfSimulation;
    public VfxPlaybackMode PlaybackMode => _playbackMode;
    public bool CollideWithFloor => _collideWithFloor;
    public float CurrentTime => _currentTime;
    public float Duration => _duration;
    public bool IsPlaying => _isPlaying;
    public float SpeedScale
    {
        get => _speedScale;
        set => _speedScale = Mathf.Max(0.01f, value);
    }

    public CpuParticleSimulator(ControlPointRegistry cpRegistry = null)
    {
        _cpRegistry = cpRegistry ?? new ControlPointRegistry();
        // Default CP10 torso preview target (50.72 inches = 1.288 m)
        if (!_cpRegistry.HasControlPoint(10))
        {
            _cpRegistry.SetPosition(10, new Vector3(0.0f, 1.288f, 0.0f));
        }
    }

    public void AddInstance(ParticleSystemInstance instance)
    {
        if (instance != null && !_instances.Contains(instance))
        {
            _instances.Add(instance);
        }
    }

    public void ClearInstances()
    {
        _instances.Clear();
        _rootVrfSimulation = null;
        _collision = null;
        _lastSyncedCps.Clear();
        _currentTime = 0.0f;
        _hasEnded = false;
    }

    public void SetRootVrfSimulation(ParticleSystemSimulation rootSimulation, GodotParticleCollision collision = null)
    {
        _rootVrfSimulation = rootSimulation;
        _collision = collision;
        _lastSyncedCps.Clear();

        if (_collision != null)
        {
            _collision.CollideWithFloor = _collideWithFloor;
        }

        if (_rootVrfSimulation != null)
        {
            if (_hasCameraPos)
            {
                _rootVrfSimulation.SetCameraPosition(_cameraPosSource2);
            }

            SyncControlPointsToVrf(force: true);

            if (_playbackMode == VfxPlaybackMode.EndCapOnly)
            {
                _rootVrfSimulation.PlayEndCapOnly();
                _hasEnded = true;
            }
        }
    }

    public void SetCameraPosition(Vector3 cameraWorldPosGodot)
    {
        _cameraPosSource2 = ParticleCoordUtils.GodotToSource2Position(cameraWorldPosGodot);
        _hasCameraPos = true;
        _rootVrfSimulation?.SetCameraPosition(_cameraPosSource2);
    }

    public void SetVfxPlaybackMode(VfxPlaybackMode mode)
    {
        _playbackMode = mode;
        Reset();
    }

    public void SetCollideWithFloor(bool enabled)
    {
        _collideWithFloor = enabled;
        if (_collision != null)
        {
            _collision.CollideWithFloor = enabled;
        }
    }

    public void SetDuration(float duration)
    {
        _duration = Mathf.Max(0.5f, duration);
    }

    public void SetCharge(float charge01)
    {
        charge01 = Mathf.Clamp(charge01, 0.0f, 1.0f);
        _cpRegistry.SetValue(1, charge01);
        if (_rootVrfSimulation != null)
        {
            _rootVrfSimulation.GetControlPoint(1).SetComponent(0, charge01);
            var cp1 = _cpRegistry.GetOrCreate(1);
            _lastSyncedCps[1] = (cp1.Position, cp1.Orientation, charge01, true);
        }
    }

    public float GetCharge()
    {
        return _cpRegistry.GetValue(1);
    }

    public void TriggerRelease()
    {
        _hasEnded = true;

        if (_rootVrfSimulation != null)
        {
            _rootVrfSimulation.PlayEndCap();
            return;
        }

        foreach (var inst in _instances)
        {
            inst.TriggerEndCap();
        }
    }

    public void Reset()
    {
        _currentTime = 0.0f;
        _hasEnded = false;
        _lastSyncedCps.Clear();

        if (AutoChargeLoop)
        {
            SetCharge(0.0f);
        }

        if (_rootVrfSimulation != null)
        {
            SyncControlPointsToVrf(force: true);
            if (_playbackMode == VfxPlaybackMode.EndCapOnly)
            {
                _rootVrfSimulation.PlayEndCapOnly();
                _hasEnded = true;
            }
            else
            {
                _rootVrfSimulation.Replay();
            }
            SyncControlPointsToVrf(force: true);
        }

        foreach (var inst in _instances)
        {
            inst.Reset();
        }
    }

    public void Play()
    {
        _isPlaying = true;
    }

    public void Pause()
    {
        _isPlaying = false;
    }

    public void Stop()
    {
        _isPlaying = false;
        Reset();
    }

    public void Seek(float targetTime)
    {
        targetTime = Mathf.Clamp(targetTime, 0.0f, _duration);
        Reset();

        if (targetTime <= 1e-4f)
        {
            return;
        }

        // Deterministic forward re-simulation with fixed step size
        const float fixedDt = 1.0f / 60.0f;
        float simT = 0.0f;

        while (simT + fixedDt <= targetTime)
        {
            StepInternal(fixedDt);
            simT += fixedDt;
        }

        float rem = targetTime - simT;
        if (rem > 1e-4f)
        {
            StepInternal(rem);
        }
    }

    public void Update(float delta)
    {
        if (!_isPlaying || _instances.Count == 0) return;

        float dt = delta * _speedScale;
        StepInternal(dt);

        // Loop handling: loop when IsLooping is active OR AutoChargeLoop is active
        if (_currentTime >= _duration)
        {
            if (IsLooping || AutoChargeLoop)
            {
                Reset();
            }
            else
            {
                _currentTime = _duration;
                _isPlaying = false;
            }
        }
    }

    public void SyncControlPointsToVrf(bool force = false)
    {
        if (_rootVrfSimulation == null) return;

        foreach (var kvp in _cpRegistry.AllControlPoints)
        {
            int idx = kvp.Key;
            var cp = kvp.Value;
            if (cp == null) continue;

            bool hasPrev = _lastSyncedCps.TryGetValue(idx, out var prev);
            bool posChanged = force || !hasPrev || prev.Pos.DistanceSquaredTo(cp.Position) > 1e-8f;
            bool rotChanged = force || !hasPrev || !prev.Rot.IsEqualApprox(cp.Orientation);
            bool valChanged = cp.HasValue && (force || !hasPrev || !prev.HasVal || MathF.Abs(prev.Val - cp.Value) > 1e-5f);

            // Preserve driver-configured CPs unless modified or explicitly host-driven (CP0 / CP10)
            bool isHostAnchored = idx == 0 || idx == 10;
            bool hasNonDefaultTransform = cp.Position != Vector3.Zero || !cp.Orientation.IsEqualApprox(Quaternion.Identity);

            if ((posChanged || rotChanged) && (isHostAnchored || hasNonDefaultTransform))
            {
                var vrfCp = _rootVrfSimulation.GetControlPoint(idx);
                if (posChanged)
                {
                    vrfCp.Position = ParticleCoordUtils.GodotToSource2Position(cp.Position);
                }
                if (rotChanged)
                {
                    vrfCp.Rotation = ParticleCoordUtils.GodotToSource2Rotation(cp.Orientation);
                }
            }

            if (valChanged)
            {
                _rootVrfSimulation.GetControlPoint(idx).SetComponent(0, cp.Value);
            }

            if (posChanged || rotChanged || valChanged)
            {
                _lastSyncedCps[idx] = (cp.Position, cp.Orientation, cp.Value, cp.HasValue);
            }
        }
    }

    private void StepInternal(float dt)
    {
        _currentTime += dt;

        // Auto charge loop logic: ramp CP1 from 0 to 1, then trigger release at AutoChargeDuration
        if (AutoChargeLoop)
        {
            if (_currentTime < AutoChargeDuration)
            {
                float charge = Mathf.Clamp(_currentTime / AutoChargeDuration, 0.0f, 1.0f);
                SetCharge(charge);
            }
            else
            {
                SetCharge(1.0f);
                if (!_hasEnded)
                {
                    TriggerRelease();
                }
            }
        }

        if (_rootVrfSimulation != null)
        {
            SyncControlPointsToVrf();
            if (_hasCameraPos)
            {
                _rootVrfSimulation.SetCameraPosition(_cameraPosSource2);
            }

            _rootVrfSimulation.Update(dt, _currentTime);

            if (_playbackMode == VfxPlaybackMode.NormalWithEndCap &&
                !_hasEnded &&
                _currentTime > 0.02f &&
                _rootVrfSimulation.HasFinishedEmitting(emissionEndIsEnough: true, includeChildren: true))
            {
                TriggerRelease();
            }
        }

        // Step all system instances (VrfSimulation-backed instances sync their ParticleSpan without re-stepping VRF)
        foreach (var inst in _instances)
        {
            inst.Step(dt, _currentTime, _cpRegistry);
        }
    }
}
