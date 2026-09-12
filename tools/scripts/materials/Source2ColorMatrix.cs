using System;
using Godot;

namespace DeadlockPlayground.Materials;

/// <summary>
/// Mathematical utilities for Valve Source 2 color transformations, CSB matrices,
/// and luminance analysis.
/// </summary>
public static class Source2ColorMatrix
{
    public static readonly System.Numerics.Vector3 LumCoeffsNormalised =
        System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(0.2126f, 0.7152f, 0.0722f));

    /// <summary>
    /// Calculates the 4x4 color-correction matrix from Source 2's g_vAlbedoContrastSaturationBrightness1
    /// and color offset parameters matching Citadel's eval and Blender node graph.
    /// </summary>
    public static Projection CalculateAlbedoColorCorrectMatrix(System.Numerics.Vector3 csb, System.Numerics.Vector3 colorOffset)
    {
        var cross = System.Numerics.Vector3.Cross(LumCoeffsNormalised, System.Numerics.Vector3.UnitZ);
        var angle = MathF.Atan2(cross.Length(), System.Numerics.Vector3.Dot(LumCoeffsNormalised, System.Numerics.Vector3.UnitZ));
        var rotation = System.Numerics.Matrix4x4.CreateFromAxisAngle(System.Numerics.Vector3.Normalize(cross), angle);

        var result = System.Numerics.Matrix4x4.CreateTranslation(-colorOffset)
                   * System.Numerics.Matrix4x4.CreateScale(csb.X)
                   * System.Numerics.Matrix4x4.CreateTranslation(colorOffset);
        result *= System.Numerics.Matrix4x4.CreateScale(csb.Z);
        result *= System.Numerics.Matrix4x4.CreateScale(LumCoeffsNormalised);
        result *= rotation;
        result *= System.Numerics.Matrix4x4.CreateScale(csb.Y, csb.Y, 1f);
        result *= System.Numerics.Matrix4x4.Transpose(rotation);
        result *= System.Numerics.Matrix4x4.CreateScale(System.Numerics.Vector3.One / LumCoeffsNormalised);

        var finalMat = System.Numerics.Matrix4x4.Transpose(result);

        // Convert to Godot Projection (mat4 for shader uniform)
        return new Projection(
            new Vector4(finalMat.M11, finalMat.M12, finalMat.M13, finalMat.M14),
            new Vector4(finalMat.M21, finalMat.M22, finalMat.M23, finalMat.M24),
            new Vector4(finalMat.M31, finalMat.M32, finalMat.M33, finalMat.M34),
            new Vector4(finalMat.M41, finalMat.M42, finalMat.M43, finalMat.M44)
        );
    }

    /// <summary>
    /// Evaluates if a vector parameter represents neutral white, black, or monochrome grey scalar
    /// rather than an intentional chromatic color tint.
    /// </summary>
    public static bool IsNeutralWhiteOrBlack(System.Numerics.Vector4 v)
    {
        bool isZero = v.X < 0.05f && v.Y < 0.05f && v.Z < 0.05f;
        bool isWhite = v.X > 0.95f && v.Y > 0.95f && v.Z > 0.95f;
        if (isZero || isWhite) return true;

        // Grayscale check: if difference between max and min color channels is less than 0.08,
        // it is a monochromatic luminance scalar, NOT an authentic chromatic hue.
        float max = MathF.Max(v.X, MathF.Max(v.Y, v.Z));
        float min = MathF.Min(v.X, MathF.Min(v.Y, v.Z));
        return (max - min) < 0.08f;
    }
}
