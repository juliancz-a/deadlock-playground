using System;
using System.Numerics;
using Godot;
using Quaternion = Godot.Quaternion;
using Vector3 = Godot.Vector3;

namespace DeadlockPlayground.Particles;

/// <summary>
/// Exact coordinate-system conversions between Source 2 (inches, Z-up: +X fwd, +Y left, +Z up)
/// and Godot / glTF (meters, Y-up: +X left, +Y up, +Z fwd), matching VRF's GltfModelExporter.
/// </summary>
public static class ParticleCoordUtils
{
    public const float InchesToMeters = 0.0254f;
    public const float MetersToInches = 1.0f / 0.0254f;

    /// <summary>
    /// Exact Source 2 -> glTF rotation quaternion used by VRF's GltfModelExporter (-90 deg pitch &amp; roll).
    /// Maps Source (+X, +Y, +Z) -> glTF (+Z, +X, +Y).
    /// </summary>
    public static readonly System.Numerics.Quaternion SourceToGltfRotation = new(-0.5f, -0.5f, -0.5f, 0.5f);

    public static Vector3 Source2ToGodotPosition(System.Numerics.Vector3 s)
        => new(s.Y * InchesToMeters, s.Z * InchesToMeters, s.X * InchesToMeters);

    public static System.Numerics.Vector3 GodotToSource2Position(Vector3 g)
        => new(g.Z * MetersToInches, g.X * MetersToInches, g.Y * MetersToInches);

    public static Vector3 Source2ToGodotDirection(System.Numerics.Vector3 s)
        => new(s.Y, s.Z, s.X);

    public static System.Numerics.Vector3 GodotToSource2Direction(Vector3 g)
        => new(g.Z, g.X, g.Y);

    public static System.Numerics.Quaternion GodotToSource2Rotation(Quaternion qG)
    {
        var q = new System.Numerics.Quaternion(qG.X, qG.Y, qG.Z, qG.W);
        var s = System.Numerics.Quaternion.Conjugate(SourceToGltfRotation) * q * SourceToGltfRotation;
        return System.Numerics.Quaternion.Normalize(s);
    }

    public static Quaternion Source2ToGodotRotation(System.Numerics.Quaternion qS)
    {
        var g = SourceToGltfRotation * qS * System.Numerics.Quaternion.Conjugate(SourceToGltfRotation);
        return new Quaternion(g.X, g.Y, g.Z, g.W).Normalized();
    }

    /// <summary>
    /// Converts a Godot world/placement Transform3D (meters, Y-up, +Z fwd) into a Source 2
    /// row-vector Matrix4x4 (inches, Z-up, +X fwd) compatible with VRF's IParticleModelObject.Transform.
    /// </summary>
    public static Matrix4x4 GodotWorldTransformToSource2Matrix(Transform3D t)
    {
        var r0 = GodotToSource2Direction(t.Basis.Z);
        var r1 = GodotToSource2Direction(t.Basis.X);
        var r2 = GodotToSource2Direction(t.Basis.Y);
        var pos = GodotToSource2Position(t.Origin);

        return new Matrix4x4(
            r0.X, r0.Y, r0.Z, 0f,
            r1.X, r1.Y, r1.Z, 0f,
            r2.X, r2.Y, r2.Z, 0f,
            pos.X, pos.Y, pos.Z, 1f);
    }

    /// <summary>
    /// Converts a Godot Skeleton3D global bone pose (where GltfModelExporter baked SourceToGltfRotation
    /// on skeleton roots without conjugation) into a Source 2 model-space bone Matrix4x4 in inches.
    /// </summary>
    public static Matrix4x4 GodotBonePoseToSource2Matrix(Transform3D bonePose)
    {
        var r0 = GodotToSource2Direction(bonePose.Basis.X);
        var r1 = GodotToSource2Direction(bonePose.Basis.Y);
        var r2 = GodotToSource2Direction(bonePose.Basis.Z);
        var pos = GodotToSource2Position(bonePose.Origin);

        return new Matrix4x4(
            r0.X, r0.Y, r0.Z, 0f,
            r1.X, r1.Y, r1.Z, 0f,
            r2.X, r2.Y, r2.Z, 0f,
            pos.X, pos.Y, pos.Z, 1f);
    }

    /// <summary>
    /// Builds a Godot Basis to place a glTF-exported (.vmdl_c via GltfModelExporter) MeshInstance3D
    /// from a Source 2 row-vector Matrix4x4.
    /// </summary>
    public static Basis Source2MatrixToGodotModelBasis(Matrix4x4 mS)
    {
        Vector3 r0 = Source2ToGodotDirection(new System.Numerics.Vector3(mS.M11, mS.M12, mS.M13));
        Vector3 r1 = Source2ToGodotDirection(new System.Numerics.Vector3(mS.M21, mS.M22, mS.M23));
        Vector3 r2 = Source2ToGodotDirection(new System.Numerics.Vector3(mS.M31, mS.M32, mS.M33));
        // glTF (+X, +Y, +Z) corresponds to Source (+Y, +Z, +X)
        return new Basis(r1, r2, r0);
    }

    /// <summary>
    /// Builds a Source 2 row-vector rotation matrix from Euler angles in radians (Pitch, Yaw, Roll),
    /// matching ValveResourceFormat.ResourceTypes.EntityTransformHelper.EulerAnglesToRotationMatrixRadians.
    /// </summary>
    public static Matrix4x4 Source2EulerRadiansToMatrix(float pitchRad, float yawRad, float rollRad)
    {
        var rollMatrix = Matrix4x4.CreateRotationX(rollRad);
        var pitchMatrix = Matrix4x4.CreateRotationY(pitchRad);
        var yawMatrix = Matrix4x4.CreateRotationZ(yawRad);
        return rollMatrix * pitchMatrix * yawMatrix;
    }

    /// <summary>
    /// Builds a Godot Basis from Source 2 particle Euler angles (Pitch, Yaw, Roll in radians)
    /// for glTF-exported model geometry or oriented quads.
    /// </summary>
    public static Basis Source2EulerRadiansToGodotModelBasis(float pitchRad, float yawRad, float rollRad)
        => Source2MatrixToGodotModelBasis(Source2EulerRadiansToMatrix(pitchRad, yawRad, rollRad));
}

