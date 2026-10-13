using System;
using System.Collections.Generic;
using Godot;

namespace DeadlockPlayground.Particles;

public enum SystemSimulationState
{
    Inactive = 0,
    Running = 1,
    Ending = 2, // EndCap released
    Dead = 3
}

/// <summary>
/// Active runtime instance of a single ParticleSystemDefinition.
/// Executes deterministic CPU simulation over an unmanaged particle pool.
/// </summary>
public class ParticleSystemInstance
{
    public ParticleSystemDefinition Definition { get; }
    public SystemSimulationState State { get; private set; } = SystemSimulationState.Inactive;
    public float Delay { get; set; } = 0.0f;
    public bool IsEndCap { get; set; } = false;
    public float CollectionAge { get; private set; } = 0.0f;
    public float CurrentFrameTime { get; private set; } = 1.0f / 60.0f;
    public bool IsEmissionEnabled { get; set; } = true;
    public ControlPointRegistry ControlPoints { get; set; } = new();

    private readonly CpuParticle[] _particles;
    private readonly int _maxParticles;
    private float _spawnAccumulator = 0.0f;
    private bool _instantaneousEmitted = false;
    private uint _seed;
    private Transform3D _prevCpTransform = Transform3D.Identity;
    private bool _hasPrevCpTransform = false;
    private uint _ringWaveOrbitCount = 0;

    public ReadOnlySpan<CpuParticle> Particles => _particles;
    public int ActiveParticleCount
    {
        get
        {
            int c = 0;
            for (int i = 0; i < _maxParticles; i++)
            {
                if (_particles[i].IsAlive) c++;
            }
            return c;
        }
    }

    public ParticleSystemInstance(ParticleSystemDefinition definition, uint seed = 1337)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _seed = seed == 0 ? 1337 : seed;

        // Pure orchestrators like root chrono_time_wall_charge have MaxParticles = 0
        _maxParticles = Math.Clamp(definition.MaxParticles, 0, 1024);
        _particles = new CpuParticle[_maxParticles];
        for (int i = 0; i < _maxParticles; i++)
        {
            _particles[i].Clear();
        }
    }

    public void Reset()
    {
        State = (IsEndCap || Delay > 0.0001f) ? SystemSimulationState.Inactive : SystemSimulationState.Running;
        CollectionAge = 0.0f;
        _spawnAccumulator = 0.0f;
        _instantaneousEmitted = false;
        _hasPrevCpTransform = false;
        _ringWaveOrbitCount = 0;

        for (int i = 0; i < _maxParticles; i++)
        {
            _particles[i].Clear();
        }
    }

    public void TriggerEndCap()
    {
        if (IsEndCap)
        {
            // EndCap children activate when the release signal is received
            State = SystemSimulationState.Running;
            CollectionAge = 0.0f;
            _instantaneousEmitted = false;
        }
        else if (State == SystemSimulationState.Running)
        {
            // Running systems transition to Ending to execute EndCap operators
            State = SystemSimulationState.Ending;
        }
    }

    public void Step(float dt, float totalElapsed, ControlPointRegistry cpRegistry)
    {
        CurrentFrameTime = dt;
        var activeCp = cpRegistry ?? ControlPoints ?? new ControlPointRegistry();

        if (State == SystemSimulationState.Inactive)
        {
            // Delay check for non-endcap children
            if (!IsEndCap && totalElapsed >= Delay)
            {
                State = SystemSimulationState.Running;
            }
            else
            {
                return;
            }
        }

        if (State == SystemSimulationState.Dead) return;

        CollectionAge += dt;

        var evalCtx = new ParticleEvaluationContext
        {
            CollectionAge = CollectionAge,
            ParticleAge = 0.0f,
            Lifetime = Definition.ConstantLifetime > 0 ? Definition.ConstantLifetime : 1.0f,
            ControlPoints = activeCp
        };

        // 1. Evaluate Emitters (only while Running, not Ending, and IsEmissionEnabled)
        if (State == SystemSimulationState.Running && _maxParticles > 0 && IsEmissionEnabled)
        {
            foreach (var emitter in Definition.Emitters)
            {
                if (emitter is ContinuousEmitterDef cont)
                {
                    float duration = cont.EmissionDuration.Evaluate(evalCtx);
                    if (duration <= 0.001f || CollectionAge <= duration)
                    {
                        float rate = cont.EmitRate.Evaluate(evalCtx);
                        _spawnAccumulator += rate * dt;
                        while (_spawnAccumulator >= 1.0f)
                        {
                            SpawnParticle(activeCp, evalCtx, dt);
                            _spawnAccumulator -= 1.0f;
                        }
                    }
                }
                else if (emitter is InstantaneousEmitterDef inst)
                {
                    if (!_instantaneousEmitted && CollectionAge >= inst.Delay)
                    {
                        int toEmit = Mathf.RoundToInt(inst.ParticlesToEmit.Evaluate(evalCtx));
                        for (int i = 0; i < toEmit; i++)
                        {
                            SpawnParticle(activeCp, evalCtx, dt);
                        }
                        _instantaneousEmitted = true;
                    }
                }
            }
        }

        // 2. Step Active Particles & Execute Operators
        int aliveCount = 0;
        bool isEnding = (State == SystemSimulationState.Ending);

        // Precompute PositionLock delta once per frame for all particles
        PositionLockOperatorDef posLockDef = null;
        Transform3D posLockCurrent = Transform3D.Identity;
        Transform3D posLockDeltaXform = Transform3D.Identity;
        Vector3 posLockDeltaVec = Vector3.Zero;

        foreach (var op in Definition.Operators)
        {
            if (op.EndCapState == ParticleEndCapState.EndCapOn && !isEnding) continue;
            if (op.EndCapState == ParticleEndCapState.EndCapOff && isEnding) continue;

            if (op is PositionLockOperatorDef pl)
            {
                posLockDef = pl;
                posLockCurrent = activeCp != null ? activeCp.GetTransform(pl.ControlPointNumber) : Transform3D.Identity;
                if (!_hasPrevCpTransform)
                {
                    _prevCpTransform = posLockCurrent;
                    _hasPrevCpTransform = true;
                }
                posLockDeltaVec = posLockCurrent.Origin - _prevCpTransform.Origin;
                if (pl.LockRotation)
                {
                    posLockDeltaXform = posLockCurrent * _prevCpTransform.AffineInverse();
                }
                break;
            }
        }

        for (int i = 0; i < _maxParticles; i++)
        {
            if (!_particles[i].IsAlive) continue;

            _particles[i].Age += dt;
            evalCtx.ParticleAge = _particles[i].Age;
            evalCtx.Lifetime = _particles[i].Lifetime;

            // Execute Operators with EndCap gating
            foreach (var op in Definition.Operators)
            {
                if (op.EndCapState == ParticleEndCapState.EndCapOn && !isEnding) continue;
                if (op.EndCapState == ParticleEndCapState.EndCapOff && isEnding) continue;

                if (op is PositionLockOperatorDef pl)
                {
                    if (pl.LockRotation)
                    {
                        _particles[i].Position = posLockDeltaXform * _particles[i].Position;
                        _particles[i].PrevPosition = posLockDeltaXform * _particles[i].PrevPosition;
                    }
                    else if (posLockDeltaVec != Vector3.Zero)
                    {
                        _particles[i].Position += posLockDeltaVec;
                        _particles[i].PrevPosition += posLockDeltaVec;
                    }
                }
                else
                {
                    ExecuteOperator(op, ref _particles[i], dt, evalCtx, activeCp);
                }
            }

            if (_particles[i].IsAlive)
            {
                aliveCount++;
            }
        }

        if (posLockDef != null)
        {
            _prevCpTransform = posLockCurrent;
        }

        // Check if system is exhausted
        if (State == SystemSimulationState.Ending && aliveCount == 0)
        {
            State = SystemSimulationState.Dead;
        }
    }

    private void SpawnParticle(ControlPointRegistry cpRegistry, ParticleEvaluationContext evalCtx, float dt)
    {
        int slot = -1;
        for (int i = 0; i < _maxParticles; i++)
        {
            if (!_particles[i].IsAlive)
            {
                slot = i;
                break;
            }
        }

        if (slot < 0) return; // Pool full

        ref var p = ref _particles[slot];
        p.Clear();
        p.IsAlive = true;
        p.Age = 0.0f;
        p.Lifetime = Definition.ConstantLifetime > 0 ? Definition.ConstantLifetime : 1.0f;
        p.BaseRadius = Definition.ConstantRadius * 0.0254f; // Inches to meters
        p.Radius = p.BaseRadius;
        p.Color = Definition.ConstantColor;
        p.Alpha = Definition.ConstantColor.A;
        p.Rotation = 0.0f;
        p.RotationalVelocity = 0.0f;
        p.SequenceIndex = Definition.HasConstantSequenceNumber ? Definition.ConstantSequenceNumber : 0;

        Vector3 cp0Pos = cpRegistry != null ? cpRegistry.GetPosition(0) : Vector3.Zero;
        p.Position = cp0Pos;
        p.PrevPosition = cp0Pos;
        p.OffsetFromCP = Vector3.Zero;
        p.Velocity = Vector3.Zero;

        p.SetField(1, p.Lifetime);
        p.SetField(3, p.BaseRadius);
        p.SetField(7, p.Alpha);
        p.SetField(10, 1.0f); // Default trail length / scrollX

        // Run Initializers
        foreach (var init in Definition.Initializers)
        {
            ExecuteInitializer(init, ref p, evalCtx, cpRegistry);
        }

        // Section A1: PrevPosition = Position - Velocity * dt
        p.PrevPosition = p.Position - (p.Velocity * (dt > 0 ? dt : (1.0f / 60.0f)));

        // Snapshot initial state after all initializers have completed
        p.InitialColor = p.Color;
        p.SnapshotInitialFields();
    }

    private void ExecuteInitializer(
        IParticleInitializerDef init,
        ref CpuParticle p,
        ParticleEvaluationContext ctx,
        ControlPointRegistry cpRegistry)
    {
        switch (init)
        {
            case RingWaveInitializerDef rw:
            {
                float radiusM = rw.Radius.Evaluate(ctx) * 0.0254f;
                float thickM = rw.Thickness.Evaluate(ctx) * 0.0254f;
                float speedMinM = rw.SpeedMin.Evaluate(ctx) * 0.0254f;
                float speedMaxM = rw.SpeedMax.Evaluate(ctx) * 0.0254f;
                float speed = Mathf.Lerp(speedMinM, speedMaxM, NextRandomFloat());

                float theta;
                if (rw.EvenDistribution)
                {
                    float perOrbit = rw.ParticlesPerOrbit <= 0 ? 32 : rw.ParticlesPerOrbit;
                    _ringWaveOrbitCount++;
                    theta = _ringWaveOrbitCount * (Mathf.Tau / Mathf.Max(1f, perOrbit));
                }
                else
                {
                    theta = NextRandomFloat() * Mathf.Tau;
                }

                // VRF Ground Truth: In Source 2, ring lies in the horizontal XY plane (Z=0).
                // Convert Source (X fwd, Y left, Z up) to Godot (X right, Y up, Z back): (x, y, z) -> (x, z, -y)
                Vector3 radialDir = new Vector3(Mathf.Cos(theta), 0.0f, -Mathf.Sin(theta));
                Vector3 godotOffset = radialDir * radiusM;

                if (thickM > 0.0f)
                {
                    godotOffset += GetRandomUnitVector() * (NextRandomFloat() * thickM);
                }

                var cpBasis = cpRegistry != null ? new Basis(cpRegistry.GetOrientation(rw.ControlPointNumber)) : Basis.Identity;
                Vector3 worldOffset = cpBasis * godotOffset;

                Vector3 outward = worldOffset.Normalized();
                if (rw.XYVelocityOnly)
                {
                    Vector3 localOutward = radialDir;
                    outward = (cpBasis * localOutward).Normalized();
                }

                Vector3 worldVel = outward * speed;
                Vector3 center = cpRegistry != null ? cpRegistry.GetPosition(rw.ControlPointNumber) : Vector3.Zero;

                p.Position = center + worldOffset;
                p.Velocity = worldVel;
                p.OffsetFromCP = worldOffset;
                break;
            }

            case CreateWithinSphereTransformDef sp:
            {
                float rMin = sp.RadiusMin.Evaluate(ctx) * 0.0254f;
                float rMax = sp.RadiusMax.Evaluate(ctx) * 0.0254f;
                float sMin = sp.SpeedMin.Evaluate(ctx) * 0.0254f;
                float sMax = sp.SpeedMax.Evaluate(ctx) * 0.0254f;

                Vector3 dir = GetRandomUnitVector();
                float r = Mathf.Lerp(rMin, rMax, NextRandomFloat());
                float speed = Mathf.Lerp(sMin, sMax, NextRandomFloat());

                Vector3 center = cpRegistry != null ? cpRegistry.GetPosition(sp.ControlPointNumber) : Vector3.Zero;
                Vector3 offset = dir * r;
                Vector3 vel = dir * speed;

                p.Position = center + offset;
                p.OffsetFromCP = offset;
                p.Velocity = vel;
                break;
            }

            case InitFloatDef fl:
            {
                float val = fl.InputValue.Evaluate(ctx);
                float cur = p.GetField(fl.OutputField);
                float result = fl.SetMethod switch
                {
                    ParticleSetMethod.SetDirect => (fl.OutputField == 3 ? val * 0.0254f : val),
                    ParticleSetMethod.ScaleInitial => cur * val, // dimensionless multiplier
                    ParticleSetMethod.AddInitial => cur + (fl.OutputField == 3 ? val * 0.0254f : val),
                    _ => val
                };
                p.SetField(fl.OutputField, result);
                if (fl.OutputField == 1) p.Lifetime = result;
                else if (fl.OutputField == 3) { p.BaseRadius = result; p.Radius = result; }
                else if (fl.OutputField == 7) p.Alpha = result;
                else if (fl.OutputField == 9 || fl.OutputField == 13) p.SequenceIndex = (int)result;
                break;
            }

            case RandomColorInitializerDef rc:
            {
                float t = NextRandomFloat();
                p.Color = rc.ColorMin.Lerp(rc.ColorMax, t);
                break;
            }

            case RandomSequenceInitializerDef rseq:
            {
                int minSeq = rseq.SequenceMin;
                int maxSeq = rseq.SequenceMax;
                // In Source 2 assets, 4-sequence sheets (e.g. electrical_arc_white) author m_nSequenceMax = 4 as count
                if (maxSeq > minSeq && maxSeq == 4) maxSeq = 3;
                int count = Math.Max(1, maxSeq - minSeq + 1);
                p.SequenceIndex = minSeq + (int)(NextRandomFloat() * count);
                break;
            }

            case RandomYawFlipInitializerDef ryf:
            {
                if (NextRandomFloat() < ryf.Percent)
                {
                    p.Rotation += Mathf.Pi;
                }
                break;
            }

            case PositionOffsetInitializerDef pof:
            {
                Vector3 off = new Vector3(
                    Mathf.Lerp(pof.OffsetMin.X, pof.OffsetMax.X, NextRandomFloat()),
                    Mathf.Lerp(pof.OffsetMin.Y, pof.OffsetMax.Y, NextRandomFloat()),
                    Mathf.Lerp(pof.OffsetMin.Z, pof.OffsetMax.Z, NextRandomFloat())
                );
                p.Position += off;
                p.OffsetFromCP += off;
                break;
            }
        }
    }

    private void ExecuteOperator(
        IParticleOperatorDef op,
        ref CpuParticle p,
        float dt,
        ParticleEvaluationContext ctx,
        ControlPointRegistry cpRegistry)
    {
        switch (op)
        {
            case BasicMovementOperatorDef bm:
            {
                float dtSquared = dt * dt;
                Vector3 gravityMovement = bm.Gravity * dtSquared;
                float dragValue = Mathf.Clamp(bm.Drag, 0.0f, 0.9999999f);
                float dragFactor = Mathf.Exp(Mathf.Log(1.0f - dragValue) / (1.0f / 30.0f) * dt);

                Vector3 step = gravityMovement + dragFactor * (p.Position - p.PrevPosition);
                p.Velocity = step / dt;
                p.PrevPosition = p.Position;
                p.Position += step;
                p.OffsetFromCP += step;
                break;
            }

            case PositionLockOperatorDef pl:
            {
                // Handled in precomputed frame loop above
                break;
            }

            case DecayOperatorDef:
            {
                if (p.Age >= p.Lifetime)
                {
                    p.IsAlive = false;
                }
                break;
            }

            case InterpolateRadiusOperatorDef ir:
            {
                float normAge = Mathf.Clamp(p.Age / p.Lifetime, 0.0f, 1.0f);
                if (normAge >= ir.StartTime && normAge <= ir.EndTime)
                {
                    float span = Mathf.Max(1e-5f, ir.EndTime - ir.StartTime);
                    float u = Mathf.Clamp((normAge - ir.StartTime) / span, 0.0f, 1.0f);
                    if (Mathf.Abs(ir.Bias) > 1e-4f)
                    {
                        u = ParticleFloatInput.ApplyBias(u, ir.Bias);
                    }
                    float scale = Mathf.Lerp(ir.StartScale, ir.EndScale, u);
                    float initR = p.GetInitialField(3);
                    if (initR <= 0f) initR = p.BaseRadius;
                    p.Radius = initR * scale;
                    p.SetField(3, p.Radius);
                }
                break;
            }

            case ColorInterpolateOperatorDef ci:
            {
                float normAge = Mathf.Clamp(p.Age / p.Lifetime, 0.0f, 1.0f);
                if (normAge >= ci.FadeStartTime && normAge <= ci.FadeEndTime)
                {
                    float span = Mathf.Max(1e-5f, ci.FadeEndTime - ci.FadeStartTime);
                    float u = Mathf.Clamp((normAge - ci.FadeStartTime) / span, 0.0f, 1.0f);
                    if (ci.EaseInOut)
                    {
                        u = Mathf.SmoothStep(0.0f, 1.0f, u);
                    }
                    p.Color = p.InitialColor.Lerp(ci.ColorFade, u);
                }
                break;
            }

            case RampScalarLinearSimpleOperatorDef ramp:
            {
                if (p.Age >= ramp.StartTime && p.Age <= ramp.EndTime)
                {
                    float delta = ramp.Rate * dt;
                    if (ramp.Field == 3) // Radius
                    {
                        p.Radius = Mathf.Max(0.0f, p.Radius + (delta * 0.0254f));
                        p.SetField(3, p.Radius);
                    }
                    else
                    {
                        p.AddField(ramp.Field, delta);
                    }
                }
                break;
            }

            case FadeOutSimpleOperatorDef fo:
            {
                float remain = p.Lifetime - p.Age;
                if (fo.FadeOutTime > 0.001f && remain < fo.FadeOutTime)
                {
                    float initA = p.GetInitialField(7);
                    if (initA <= 0f) initA = 1.0f;
                    float a = Mathf.Clamp(remain / fo.FadeOutTime, 0.0f, 1.0f);
                    p.Alpha = initA * a;
                    p.SetField(7, p.Alpha);
                }
                break;
            }

            case FadeInSimpleOperatorDef fi:
            {
                if (fi.FadeInTime > 0.001f && p.Age < fi.FadeInTime)
                {
                    float initA = p.GetInitialField(7);
                    if (initA <= 0f) initA = 1.0f;
                    float a = Mathf.Clamp(p.Age / fi.FadeInTime, 0.0f, 1.0f);
                    p.Alpha = initA * a;
                    p.SetField(7, p.Alpha);
                }
                break;
            }

            case SetFloatOperatorDef sf:
            {
                float val = sf.InputValue.Evaluate(ctx);
                float initial = p.GetInitialField(sf.OutputField);
                float result = sf.SetMethod switch
                {
                    ParticleSetMethod.SetDirect => (sf.OutputField == 3 ? val * 0.0254f : val),
                    ParticleSetMethod.ScaleInitial => initial * val,
                    ParticleSetMethod.AddInitial => initial + (sf.OutputField == 3 ? val * 0.0254f : val),
                    _ => val
                };
                p.SetField(sf.OutputField, result);
                if (sf.OutputField == 1) p.Lifetime = result;
                else if (sf.OutputField == 3) p.Radius = result;
                else if (sf.OutputField == 7) p.Alpha = result;
                else if (sf.OutputField == 9 || sf.OutputField == 13) p.SequenceIndex = (int)result;
                break;
            }

            case SetVecOperatorDef sv:
            {
                Vector3 val = sv.InputValue.Evaluate(ctx);
                if (sv.OutputField == 6)
                {
                    p.Color = new Color(val.X, val.Y, val.Z, p.Alpha);
                }
                break;
            }

            case SpinUpdateOperatorDef:
            {
                p.Rotation += p.RotationalVelocity * dt;
                break;
            }
        }
    }



    private float NextRandomFloat()
    {
        _seed = (_seed * 1664525u) + 1013904223u;
        return (_seed & 0x00FFFFFF) / (float)0x01000000;
    }

    private Vector3 GetRandomUnitVector()
    {
        float z = (NextRandomFloat() * 2.0f) - 1.0f;
        float phi = NextRandomFloat() * Mathf.Tau;
        float r = Mathf.Sqrt(Mathf.Max(0.0f, 1.0f - (z * z)));
        return new Vector3(r * Mathf.Cos(phi), r * Mathf.Sin(phi), z);
    }
}
