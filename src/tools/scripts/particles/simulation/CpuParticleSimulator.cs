using System;
using System.Collections.Generic;
using Godot;

namespace DeadlockPlayground.Particles;

/// <summary>
/// Top-level deterministic CPU particle simulator orchestrating an entire .vpcf hierarchy.
/// Owns the control point registry (CP0 socket, CP1 charge, CP10 target) and handles
/// fixed-dt stepping, timeline seeking, EndCap state transitions, and auto-charge playback.
/// </summary>
public class CpuParticleSimulator
{
    private readonly List<ParticleSystemInstance> _instances = new();
    private readonly ControlPointRegistry _cpRegistry;
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
        _currentTime = 0.0f;
        _hasEnded = false;
    }

    public void SetDuration(float duration)
    {
        _duration = Mathf.Max(0.5f, duration);
    }

    public void SetCharge(float charge01)
    {
        charge01 = Mathf.Clamp(charge01, 0.0f, 1.0f);
        _cpRegistry.SetValue(1, charge01);
    }

    public float GetCharge()
    {
        return _cpRegistry.GetValue(1);
    }

    public void TriggerRelease()
    {
        _hasEnded = true;

        foreach (var inst in _instances)
        {
            inst.TriggerEndCap();
        }
    }

    public void Reset()
    {
        _currentTime = 0.0f;
        _hasEnded = false;
        if (AutoChargeLoop)
        {
            SetCharge(0.0f);
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

        // Step all system instances
        foreach (var inst in _instances)
        {
            inst.Step(dt, _currentTime, _cpRegistry);
        }
    }
}
