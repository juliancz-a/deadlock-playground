using System;
using System.Collections.Generic;
using Godot;

namespace DeadlockPlayground.Maps;

/// <summary>
/// Manages spatial radius culling, room chunking, stage centering, and lifecycle teardown
/// for 3D map stage environments.
/// </summary>
public static class StageChunkManager
{
    /// <summary>
    /// Evaluates whether a Source 2 AABB bounding box intersects a sphere defined in Source 2 coordinates.
    /// </summary>
    public static bool IntersectsSphere(
        System.Numerics.Vector3 boxMin,
        System.Numerics.Vector3 boxMax,
        System.Numerics.Vector3 sphereCenter,
        float sphereRadiusSourceUnits)
    {
        if (sphereRadiusSourceUnits <= 0f) return true; // Full mode, no culling

        // Find the point on or inside the box closest to the sphere center
        float closestX = Math.Clamp(sphereCenter.X, Math.Min(boxMin.X, boxMax.X), Math.Max(boxMin.X, boxMax.X));
        float closestY = Math.Clamp(sphereCenter.Y, Math.Min(boxMin.Y, boxMax.Y), Math.Max(boxMin.Y, boxMax.Y));
        float closestZ = Math.Clamp(sphereCenter.Z, Math.Min(boxMin.Z, boxMax.Z), Math.Max(boxMin.Z, boxMax.Z));

        float dx = sphereCenter.X - closestX;
        float dy = sphereCenter.Y - closestY;
        float dz = sphereCenter.Z - closestZ;

        float distSq = dx * dx + dy * dy + dz * dz;
        float radiusSq = sphereRadiusSourceUnits * sphereRadiusSourceUnits;

        return distSq <= radiusSq;
    }

    /// <summary>
    /// Checks distance between two Source 2 points in inches against a radius in meters.
    /// </summary>
    public static bool IsWithinRadiusMeters(
        System.Numerics.Vector3 p1,
        System.Numerics.Vector3 p2,
        float radiusMeters)
    {
        if (radiusMeters <= 0f) return true;

        float radiusInches = radiusMeters / MapCoordinateUtils.InchesToMeters;
        float dx = p1.X - p2.X;
        float dy = p1.Y - p2.Y;
        float dz = p1.Z - p2.Z;

        return (dx * dx + dy * dy + dz * dz) <= (radiusInches * radiusInches);
    }
}
