using System;
using Godot;

namespace DeadlockPlayground.Maps;

/// <summary>
/// Mathematical and spatial utilities for converting Source 2 map coordinates,
/// bounding boxes, orientations, and scales to Godot 3D space.
/// </summary>
public static class MapCoordinateUtils
{
    /// <summary>
    /// Scale factor converting Source 2 units (inches) to Godot units (meters).
    /// 1 Source 2 Hammer unit = 1 inch = 0.0254 meters.
    /// </summary>
    public const float InchesToMeters = 0.0254f;

    /// <summary>
    /// Converts a Source 2 point (X=Forward/East, Y=Left/North, Z=Up, inches)
    /// to Godot 3D space (X=Right/East, Y=Up, Z=-Forward/South, meters).
    /// </summary>
    public static Vector3 SourceToGodotPoint(float x, float y, float z)
    {
        return new Vector3(x * InchesToMeters, z * InchesToMeters, -y * InchesToMeters);
    }

    public static Vector3 SourceToGodotPoint(System.Numerics.Vector3 src)
    {
        return new Vector3(src.X * InchesToMeters, src.Z * InchesToMeters, -src.Y * InchesToMeters);
    }

    /// <summary>
    /// Converts a Source 2 normal/direction vector to Godot 3D space (normalized, no distance scaling).
    /// </summary>
    public static Vector3 SourceToGodotNormal(float x, float y, float z)
    {
        var v = new Vector3(x, z, -y);
        return v.LengthSquared() > 1e-6f ? v.Normalized() : Vector3.Up;
    }

    public static Vector3 SourceToGodotNormal(System.Numerics.Vector3 src)
    {
        var v = new Vector3(src.X, src.Z, -src.Y);
        return v.LengthSquared() > 1e-6f ? v.Normalized() : Vector3.Up;
    }

    // Row-vector coordinate conversion matrix C such that Vector3.Transform(P_source, C) == SourceToGodotPoint(P_source)
    private static readonly System.Numerics.Matrix4x4 SourceToGodotMatrix = new(
        InchesToMeters, 0, 0, 0,
        0, 0, -InchesToMeters, 0,
        0, InchesToMeters, 0, 0,
        0, 0, 0, 1f
    );

    private static readonly System.Numerics.Matrix4x4 GodotToSourceMatrix = InvertMatrix(SourceToGodotMatrix);

    private static System.Numerics.Matrix4x4 InvertMatrix(System.Numerics.Matrix4x4 matrix)
    {
        System.Numerics.Matrix4x4.Invert(matrix, out var inverse);
        return inverse;
    }

    /// <summary>
    /// Converts a Source 2 entity transform (origin, pitch/yaw/roll angles in degrees, scale)
    /// into native Godot Transform3D using exact coordinate transformation conjugation (C^-1 * M_source * C).
    /// Eliminates bad prop orientation and pitch/yaw flipping.
    /// </summary>
    public static Transform3D SourceEntityToGodotTransform(
        System.Numerics.Vector3 origin,
        System.Numerics.Vector3 anglesDeg,
        System.Numerics.Vector3 scale)
    {
        // Source 2 QAngle composition: pitch turns about Y, yaw about Z, roll about X
        float pitchRad = anglesDeg.X * Mathf.Pi / 180f;
        float yawRad = anglesDeg.Y * Mathf.Pi / 180f;
        float rollRad = anglesDeg.Z * Mathf.Pi / 180f;

        var rollMatrix = System.Numerics.Matrix4x4.CreateRotationX(rollRad);
        var pitchMatrix = System.Numerics.Matrix4x4.CreateRotationY(pitchRad);
        var yawMatrix = System.Numerics.Matrix4x4.CreateRotationZ(yawRad);
        var rotMatrix = rollMatrix * pitchMatrix * yawMatrix;

        var scaleMatrix = System.Numerics.Matrix4x4.CreateScale(scale);
        var transMatrix = System.Numerics.Matrix4x4.CreateTranslation(origin);

        var s2Transform = scaleMatrix * rotMatrix * transMatrix;
        var godotTransform = GodotToSourceMatrix * s2Transform * SourceToGodotMatrix;

        if (System.Numerics.Matrix4x4.Decompose(godotTransform, out var s, out var r, out var t))
        {
            var gPos = new Vector3(t.X, t.Y, t.Z);
            var gRot = new Godot.Quaternion(r.X, r.Y, r.Z, r.W);
            var gScale = new Vector3(s.X, s.Y, s.Z);
            var basis = new Basis(gRot).Scaled(gScale);
            return new Transform3D(basis, gPos);
        }

        return new Transform3D(Basis.Identity, SourceToGodotPoint(origin));
    }

    /// <summary>
    /// Converts a Godot 3D point (meters) back to Source 2 space (inches).
    /// </summary>
    public static System.Numerics.Vector3 GodotToSourcePoint(Vector3 g)
    {
        return new System.Numerics.Vector3(g.X / InchesToMeters, -g.Z / InchesToMeters, g.Y / InchesToMeters);
    }

    /// <summary>
    /// Converts a Godot Transform3D into Source 2 origin (inches), angles (Pitch, Yaw, Roll in degrees), and scale.
    /// Uses exact coordinate transformation conjugation (C * M_godot * C^-1).
    /// </summary>
    public static (System.Numerics.Vector3 origin, System.Numerics.Vector3 anglesDeg, System.Numerics.Vector3 scale) GodotToSourceTransform(Transform3D gTransform)
    {
        var gPos = gTransform.Origin;
        var gRot = gTransform.Basis.GetRotationQuaternion();
        var gScale = gTransform.Basis.Scale;

        var numScale = new System.Numerics.Vector3(gScale.X, gScale.Y, gScale.Z);
        var numRot = new System.Numerics.Quaternion(gRot.X, gRot.Y, gRot.Z, gRot.W);
        var numTrans = new System.Numerics.Vector3(gPos.X, gPos.Y, gPos.Z);

        var gMatrix = System.Numerics.Matrix4x4.CreateScale(numScale) *
                      System.Numerics.Matrix4x4.CreateFromQuaternion(numRot) *
                      System.Numerics.Matrix4x4.CreateTranslation(numTrans);

        var s2Matrix = SourceToGodotMatrix * gMatrix * GodotToSourceMatrix;

        if (System.Numerics.Matrix4x4.Decompose(s2Matrix, out var s, out var r, out var t))
        {
            float x = r.X, y = r.Y, z = r.Z, w = r.W;

            // Pitch (around Y)
            float sinP = 2.0f * (w * y - z * x);
            float pitchRad;
            if (MathF.Abs(sinP) >= 1f)
                pitchRad = MathF.CopySign(MathF.PI / 2f, sinP);
            else
                pitchRad = MathF.Asin(sinP);

            // Yaw (around Z)
            float sinY = 2.0f * (w * z + x * y);
            float cosY = 1.0f - 2.0f * (y * y + z * z);
            float yawRad = MathF.Atan2(sinY, cosY);

            // Roll (around X)
            float sinR = 2.0f * (w * x + y * z);
            float cosR = 1.0f - 2.0f * (x * x + y * y);
            float rollRad = MathF.Atan2(sinR, cosR);

            var angles = new System.Numerics.Vector3(
                pitchRad * 180f / MathF.PI,
                yawRad * 180f / MathF.PI,
                rollRad * 180f / MathF.PI
            );

            return (t, angles, s);
        }

        return (
            GodotToSourcePoint(gPos),
            System.Numerics.Vector3.Zero,
            new System.Numerics.Vector3(gScale.X, gScale.Y, gScale.Z)
        );
    }

    /// <summary>
    /// Converts Source 2 Euler angles (Pitch, Yaw, Roll in degrees) to Godot Euler rotation (radians).
    /// </summary>
    public static Vector3 SourceAnglesToGodotEuler(float pitchDeg, float yawDeg, float rollDeg)
    {
        // Source 2: Pitch (rotation around Y-axis), Yaw (rotation around Z-axis Up), Roll (rotation around X-axis Forward).
        // Godot: Pitch around X-axis, Yaw around Y-axis Up, Roll around Z-axis Forward.
        float yawRad = Mathf.DegToRad(yawDeg - 90.0f);
        float pitchRad = Mathf.DegToRad(-pitchDeg);
        float rollRad = Mathf.DegToRad(rollDeg);
        return new Vector3(pitchRad, yawRad, rollRad);
    }

    /// <summary>
    /// Converts a Source 2 3x4 or 4x4 matrix row-major array into a Godot Transform3D using
    /// exact coordinate transformation conjugation (C^-1 * M_source * C), matching SourceEntityToGodotTransform.
    /// Eliminates tilted or floating props and preserves scale and orientation.
    /// </summary>
    public static Transform3D SourceMatrixToGodotTransform(float[] m)
    {
        if (m == null || m.Length < 12) return Transform3D.Identity;

        // In Source 2 row-major:
        // [ m0,  m1,  m2,  m3 ] (X row: x_axis.x, x_axis.y, x_axis.z, pos.x)
        // [ m4,  m5,  m6,  m7 ] (Y row: y_axis.x, y_axis.y, y_axis.z, pos.y)
        // [ m8,  m9,  m10, m11] (Z row: z_axis.x, z_axis.y, z_axis.z, pos.z)
        // In System.Numerics.Matrix4x4, row vectors: (x, y, z, 1) * M
        // M11=m[0], M12=m[4], M13=m[8],  M14=0
        // M21=m[1], M22=m[5], M23=m[9],  M24=0
        // M31=m[2], M32=m[6], M33=m[10], M34=0
        // M41=m[3], M42=m[7], M43=m[11], M44=1
        var s2Transform = new System.Numerics.Matrix4x4(
            m[0], m[4], m[8], 0,
            m[1], m[5], m[9], 0,
            m[2], m[6], m[10], 0,
            m[3], m[7], m[11], 1
        );

        var godotTransform = GodotToSourceMatrix * s2Transform * SourceToGodotMatrix;

        if (System.Numerics.Matrix4x4.Decompose(godotTransform, out var s, out var r, out var t))
        {
            var gPos = new Vector3(t.X, t.Y, t.Z);
            var gRot = new Godot.Quaternion(r.X, r.Y, r.Z, r.W);
            var gScale = new Vector3(s.X, s.Y, s.Z);
            var basis = new Basis(gRot).Scaled(gScale);
            return new Transform3D(basis, gPos);
        }

        return new Transform3D(Basis.Identity, SourceToGodotPoint(m[3], m[7], m[11]));
    }

    /// <summary>
    /// Converts a Source 2 AABB (min, max in inches) to a Godot Aabb (in meters).
    /// </summary>
    public static Aabb SourceBoundsToGodotAabb(System.Numerics.Vector3 min, System.Numerics.Vector3 max)
    {
        Vector3 p1 = SourceToGodotPoint(min.X, min.Y, min.Z);
        Vector3 p2 = SourceToGodotPoint(max.X, max.Y, max.Z);

        Vector3 gMin = new Vector3(Mathf.Min(p1.X, p2.X), Mathf.Min(p1.Y, p2.Y), Mathf.Min(p1.Z, p2.Z));
        Vector3 gMax = new Vector3(Mathf.Max(p1.X, p2.X), Mathf.Max(p1.Y, p2.Y), Mathf.Max(p1.Z, p2.Z));

        return new Aabb(gMin, gMax - gMin);
    }
}
