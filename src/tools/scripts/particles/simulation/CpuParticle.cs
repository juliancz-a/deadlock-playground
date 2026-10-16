using System;
using Godot;

namespace DeadlockPlayground.Particles;

/// <summary>
/// Unmanaged, zero-GC particle representation for high-performance deterministic CPU simulation.
/// Supports standard physics attributes plus per-particle scratch float fields (0..31) for
/// Source 2 operator-driven kinematics and material var mappings.
/// </summary>
public unsafe struct CpuParticle
{
    public Vector3 Position;
    public Vector3 PrevPosition;
    public Vector3 Velocity;
    public Vector3 OffsetFromCP; // Local offset retained for PositionLock
    public float Radius;
    public float BaseRadius;
    public Color Color;
    public Color InitialColor;
    public float Alpha;
    public float Lifetime;
    public float Age;
    public float Rotation; // Radians
    public float RotationalVelocity;
    public int SequenceIndex;
    public bool IsAlive;

    // Per-particle attribute scratch slots (0..31):
    // 1: Lifetime
    // 3: Radius
    // 7: Alpha
    // 10: Trail Length / ScrollX
    // 17: Illum Color
    // 18: Scale Initial
    // 26: Alpha Test
    // 27: ScrollY
    private fixed float _fields[32];
    private fixed float _initialFields[32];

    public float GetField(int fieldId)
    {
        if (fieldId >= 0 && fieldId < 32)
        {
            fixed (float* p = _fields)
            {
                return p[fieldId];
            }
        }
        return 0f;
    }

    public void SetField(int fieldId, float val)
    {
        if (fieldId >= 0 && fieldId < 32)
        {
            fixed (float* p = _fields)
            {
                p[fieldId] = val;
            }
        }
    }

    public float GetInitialField(int fieldId)
    {
        if (fieldId >= 0 && fieldId < 32)
        {
            fixed (float* init = _initialFields)
            {
                return init[fieldId];
            }
        }
        return 0f;
    }

    public void SnapshotInitialFields()
    {
        fixed (float* p = _fields)
        fixed (float* init = _initialFields)
        {
            for (int i = 0; i < 32; i++)
            {
                init[i] = p[i];
            }
        }
    }

    public void ScaleField(int fieldId, float scale)
    {
        if (fieldId >= 0 && fieldId < 32)
        {
            fixed (float* p = _fields)
            {
                p[fieldId] *= scale;
            }
        }
    }

    public void AddField(int fieldId, float delta)
    {
        if (fieldId >= 0 && fieldId < 32)
        {
            fixed (float* p = _fields)
            {
                p[fieldId] += delta;
            }
        }
    }

    public void Clear()
    {
        Position = Vector3.Zero;
        PrevPosition = Vector3.Zero;
        Velocity = Vector3.Zero;
        OffsetFromCP = Vector3.Zero;
        Radius = 0f;
        BaseRadius = 0f;
        Color = Colors.White;
        Alpha = 1f;
        Lifetime = 1f;
        Age = 0f;
        Rotation = 0f;
        RotationalVelocity = 0f;
        SequenceIndex = 0;
        IsAlive = false;

        fixed (float* p = _fields)
        fixed (float* init = _initialFields)
        {
            for (int i = 0; i < 32; i++)
            {
                p[i] = 0f;
                init[i] = 0f;
            }
        }
    }
}
