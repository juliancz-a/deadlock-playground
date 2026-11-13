using System;
using System.Numerics;
using ValveResourceFormat.Particles;

namespace DeadlockPlayground.Particles;

/// <summary>
/// Floor plane particle collision adapter implementing VRF's IParticleCollision.
/// Traces against the horizontal Source 2 ground plane (Z = FloorHeightSource2) when enabled.
/// </summary>
public sealed class GodotParticleCollision : IParticleCollision
{
    public bool CollideWithFloor { get; set; } = false;

    /// <summary>
    /// Floor elevation in Source 2 inches along +Z. Defaults to 0 (ground level).
    /// </summary>
    public float FloorHeightSource2 { get; set; } = 0f;

    public bool HasGeometry => CollideWithFloor;

    public bool TraceRay(Vector3 rayStart, Vector3 rayEnd, out Vector3 position, out Vector3 normal, out float fraction)
    {
        if (!CollideWithFloor)
        {
            position = rayEnd;
            normal = Vector3.UnitZ;
            fraction = 1f;
            return false;
        }

        float zPlane = FloorHeightSource2;
        float dStart = rayStart.Z - zPlane;
        float dEnd = rayEnd.Z - zPlane;

        // Moving downward from above/at the floor or crossing upward from below
        if ((dStart >= 0f && dEnd <= 0f) || (dStart <= 0f && dEnd >= 0f))
        {
            float denom = rayStart.Z - rayEnd.Z;
            if (MathF.Abs(denom) <= 1e-6f)
            {
                if (MathF.Abs(dStart) <= 1e-4f)
                {
                    fraction = 0f;
                    position = new Vector3(rayStart.X, rayStart.Y, zPlane);
                    normal = Vector3.UnitZ;
                    return true;
                }
            }
            else
            {
                float t = dStart / denom;
                if (t >= 0f && t <= 1f)
                {
                    fraction = t;
                    position = Vector3.Lerp(rayStart, rayEnd, t);
                    position.Z = zPlane;
                    normal = dStart >= 0f ? Vector3.UnitZ : -Vector3.UnitZ;
                    return true;
                }
            }
        }

        position = rayEnd;
        normal = Vector3.UnitZ;
        fraction = 1f;
        return false;
    }
}

