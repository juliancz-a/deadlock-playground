using System;
using System.Collections.Generic;
using Godot;

namespace DeadlockPlayground.Particles;

/// <summary>
/// Represents a single Source 2 Control Point (CP) state,
/// including 3D position, orientation, velocity, and scalar/vector custom channels.
/// </summary>
public class ControlPoint
{
    public int Index { get; set; }
    public Vector3 Position { get; set; } = Vector3.Zero;
    public Quaternion Orientation { get; set; } = Quaternion.Identity;
    public Vector3 Velocity { get; set; } = Vector3.Zero;
    public float Value { get; set; } = 0.0f;
    public string AttachmentName { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;

    public Transform3D Transform => new Transform3D(new Basis(Orientation), Position);

    public ControlPoint(int index)
    {
        Index = index;
    }

    public ControlPoint Clone()
    {
        return new ControlPoint(Index)
        {
            Position = Position,
            Orientation = Orientation,
            Velocity = Velocity,
            Value = Value,
            AttachmentName = AttachmentName,
            EntityName = EntityName
        };
    }
}

/// <summary>
/// Central registry managing all Control Points (CP0 - CP63) for active particle simulations.
/// Manages standard application-driven CPs (CP0 socket anchor, CP1 charge parameter, CP10 torso target).
/// </summary>
public class ControlPointRegistry
{
    private readonly Dictionary<int, ControlPoint> _controlPoints = new();

    /// <summary>
    /// Default torso target height in Godot coordinate space (Y-up, ~1.288 meters / 50.72 inches).
    /// </summary>
    public static readonly Vector3 DefaultTorsoTargetOffset = new(0.0f, 1.288f, 0.0f);

    public ControlPointRegistry()
    {
        // Pre-initialize standard Control Points
        GetOrCreate(0); // CP0: Socket / Emitter Root
        var cp1 = GetOrCreate(1); // CP1: Ability Charge Parameter (0.0 = idle, 1.0 = fully charged)
        cp1.Value = 0.0f;

        var cp10 = GetOrCreate(10); // CP10: Torso target / aim point
        cp10.Position = DefaultTorsoTargetOffset;
    }

    public ControlPoint GetOrCreate(int index)
    {
        if (!_controlPoints.TryGetValue(index, out var cp))
        {
            cp = new ControlPoint(index);
            _controlPoints[index] = cp;
        }
        return cp;
    }

    public bool HasControlPoint(int index) => _controlPoints.ContainsKey(index);

    public Vector3 GetPosition(int index)
    {
        return _controlPoints.TryGetValue(index, out var cp) ? cp.Position : Vector3.Zero;
    }

    public Quaternion GetOrientation(int index)
    {
        return _controlPoints.TryGetValue(index, out var cp) ? cp.Orientation : Quaternion.Identity;
    }

    public Transform3D GetTransform(int index)
    {
        return _controlPoints.TryGetValue(index, out var cp) ? cp.Transform : Transform3D.Identity;
    }

    public float GetValue(int index)
    {
        return _controlPoints.TryGetValue(index, out var cp) ? cp.Value : 0.0f;
    }

    public void SetPosition(int index, Vector3 pos)
    {
        GetOrCreate(index).Position = pos;
    }

    public void SetOrientation(int index, Quaternion rot)
    {
        GetOrCreate(index).Orientation = rot;
    }

    public void SetTransform(int index, Transform3D transform)
    {
        var cp = GetOrCreate(index);
        cp.Position = transform.Origin;
        cp.Orientation = transform.Basis.GetRotationQuaternion();
    }

    public void SetValue(int index, float val)
    {
        var cp = GetOrCreate(index);
        cp.Value = val;
    }

    public void CopyFrom(ControlPointRegistry source)
    {
        if (source == null) return;
        foreach (var (idx, cp) in source._controlPoints)
        {
            _controlPoints[idx] = cp.Clone();
        }
    }
}
