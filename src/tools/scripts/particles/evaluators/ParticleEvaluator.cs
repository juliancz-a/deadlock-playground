using System;
using System.Collections.Generic;
using Godot;

namespace DeadlockPlayground.Particles;

/// <summary>
/// A single spline control point supporting cubic Hermite interpolation.
/// </summary>
public struct SplinePoint
{
    public float X;
    public float Y;
    public float SlopeIncoming;
    public float SlopeOutgoing;

    public SplinePoint(float x, float y, float slopeIn, float slopeOut)
    {
        X = x;
        Y = y;
        SlopeIncoming = slopeIn;
        SlopeOutgoing = slopeOut;
    }
}

/// <summary>
/// Evaluates Source 2 piecewise cubic Hermite spline curves (m_Curve.m_spline).
/// </summary>
public class SplineCurve
{
    public List<SplinePoint> Points { get; } = new();
    public Vector2 DomainMins { get; set; } = Vector2.Zero;
    public Vector2 DomainMaxs { get; set; } = Vector2.One;

    public SplineCurve() { }

    public SplineCurve(IEnumerable<SplinePoint> points, Vector2 domainMins, Vector2 domainMaxs)
    {
        Points.AddRange(points);
        DomainMins = domainMins;
        DomainMaxs = domainMaxs;
        Points.Sort((a, b) => a.X.CompareTo(b.X));
    }

    public float Evaluate(float t)
    {
        if (Points.Count == 0) return 0.0f;
        if (Points.Count == 1) return Points[0].Y;

        // Clamp to domain extremes
        if (t <= Points[0].X) return Points[0].Y;
        if (t >= Points[^1].X) return Points[^1].Y;

        // Binary search or linear scan for segment
        int segIdx = 0;
        for (int i = 0; i < Points.Count - 1; i++)
        {
            if (t >= Points[i].X && t <= Points[i + 1].X)
            {
                segIdx = i;
                break;
            }
        }

        var p0 = Points[segIdx];
        var p1 = Points[segIdx + 1];

        float h = p1.X - p0.X;
        if (MathF.Abs(h) < 1e-6f) return p0.Y;

        // Normalized interval [0, 1]
        float u = (t - p0.X) / h;
        float u2 = u * u;
        float u3 = u2 * u;

        // Hermite basis functions
        float h00 = 2.0f * u3 - 3.0f * u2 + 1.0f;
        float h10 = u3 - 2.0f * u2 + u;
        float h01 = -2.0f * u3 + 3.0f * u2;
        float h11 = u3 - u2;

        return h00 * p0.Y + h10 * h * p0.SlopeOutgoing + h01 * p1.Y + h11 * h * p1.SlopeIncoming;
    }
}

/// <summary>
/// Runtime context supplied to particle evaluators during simulation ticks.
/// </summary>
public class ParticleEvaluationContext
{
    public float CollectionAge { get; set; } = 0.0f;
    public float ParticleAge { get; set; } = 0.0f;
    public float Lifetime { get; set; } = 1.0f;
    public float NormalizedAge => Lifetime > 1e-5f ? Mathf.Clamp(ParticleAge / Lifetime, 0.0f, 1.0f) : 1.0f;
    public int ParticleIndex { get; set; } = 0;
    public int MaxParticles { get; set; } = 1;
    public Vector3 ParticlePosition { get; set; } = Vector3.Zero;
    public Vector3 ParticleVelocity { get; set; } = Vector3.Zero;
    public ControlPointRegistry ControlPoints { get; set; }
    public Random Rng { get; set; } = new Random();

    // Per-particle scratch scalar attribute slots (0..63)
    private readonly float[] _attributes = new float[64];

    public float GetAttribute(int fieldId)
    {
        return (fieldId >= 0 && fieldId < _attributes.Length) ? _attributes[fieldId] : 0.0f;
    }

    public void SetAttribute(int fieldId, float value)
    {
        if (fieldId >= 0 && fieldId < _attributes.Length)
        {
            _attributes[fieldId] = value;
        }
    }
}

/// <summary>
/// Strongly typed Source 2 Float Input evaluator (CParticleCollectionFloatInput).
/// Evaluates PF_TYPE_* modes, PF_MAP_TYPE_* mappings, noise, and bias splines.
/// </summary>
public class ParticleFloatInput
{
    public string Type { get; set; } = "PF_TYPE_LITERAL";
    public string MapType { get; set; } = "PF_MAP_TYPE_DIRECT";
    public float LiteralValue { get; set; } = 0.0f;
    public int ControlPoint { get; set; } = 0;
    public int VectorComponent { get; set; } = 0; // 0=X, 1=Y, 2=Z
    public int ScalarAttribute { get; set; } = 3;
    public float RandomMin { get; set; } = 0.0f;
    public float RandomMax { get; set; } = 1.0f;
    public bool HasRandomSignFlip { get; set; } = false;
    public string RandomMode { get; set; } = "PF_RANDOM_MODE_CONSTANT";

    // Remap parameters
    public float Input0 { get; set; } = 0.0f;
    public float Input1 { get; set; } = 1.0f;
    public float Output0 { get; set; } = 0.0f;
    public float Output1 { get; set; } = 1.0f;
    public float MultFactor { get; set; } = 1.0f;
    public float BiasParameter { get; set; } = 0.0f;

    // Noise parameters
    public float NoiseOutputMin { get; set; } = 0.0f;
    public float NoiseOutputMax { get; set; } = 1.0f;
    public float NoiseScale { get; set; } = 0.1f;
    public Vector3 NoiseOffsetRate { get; set; } = Vector3.Zero;

    // Spline curve mapping
    public SplineCurve Curve { get; set; }

    public static ParticleFloatInput FromLiteral(float val) => new() { LiteralValue = val, Type = "PF_TYPE_LITERAL" };

    public float Evaluate(ParticleEvaluationContext ctx)
    {
        ctx ??= new ParticleEvaluationContext();

        // 1. Evaluate raw input value according to PF_TYPE
        float rawVal = Type switch
        {
            "PF_TYPE_LITERAL" => LiteralValue,
            "PF_TYPE_COLLECTION_AGE" => ctx.CollectionAge,
            "PF_TYPE_PARTICLE_AGE" => ctx.ParticleAge,
            "PF_TYPE_PARTICLE_AGE_NORMALIZED" => ctx.NormalizedAge,
            "PF_TYPE_PARTICLE_NUMBER" => (float)ctx.ParticleIndex,
            "PF_TYPE_PARTICLE_NUMBER_NORMALIZED" => ctx.MaxParticles > 1 ? (float)ctx.ParticleIndex / (float)(ctx.MaxParticles - 1) : 0.0f,
            "PF_TYPE_CONTROL_POINT_COMPONENT" => EvaluateControlPointComponent(ctx),
            "PF_TYPE_RANDOM_UNIFORM" => EvaluateRandomUniform(ctx),
            "PF_TYPE_RANDOM_BIASED" => EvaluateRandomBiased(ctx),
            "PF_TYPE_PARTICLE_NOISE" => EvaluateNoise(ctx),
            "PF_TYPE_PARTICLE_FLOAT" => ctx.GetAttribute(ScalarAttribute),
            _ => LiteralValue
        };

        // 2. Apply Mapping according to PF_MAP_TYPE
        float mappedVal = MapType switch
        {
            "PF_MAP_TYPE_DIRECT" => rawVal,
            "PF_MAP_TYPE_REMAP" => Remap(rawVal, Input0, Input1, Output0, Output1, clamp: true),
            "PF_MAP_TYPE_REMAP_BIASED" => ApplyBias(Remap(rawVal, Input0, Input1, Output0, Output1, clamp: true), BiasParameter),
            "PF_MAP_TYPE_CURVE" => Curve != null ? Curve.Evaluate(rawVal) : rawVal,
            _ => rawVal
        };

        return mappedVal * MultFactor;
    }

    private float EvaluateControlPointComponent(ParticleEvaluationContext ctx)
    {
        if (ctx.ControlPoints == null) return 0.0f;
        var cp = ctx.ControlPoints.GetOrCreate(ControlPoint);
        return VectorComponent switch
        {
            0 => cp.Position.X,
            1 => cp.Position.Y,
            2 => cp.Position.Z,
            3 => cp.Value,
            _ => cp.Position.X
        };
    }

    private float EvaluateRandomUniform(ParticleEvaluationContext ctx)
    {
        float t = (float)(ctx.Rng?.NextDouble() ?? 0.5);
        float val = Mathf.Lerp(RandomMin, RandomMax, t);
        if (HasRandomSignFlip && (ctx.Rng?.Next(2) == 1))
        {
            val = -val;
        }
        return val;
    }

    private float EvaluateRandomBiased(ParticleEvaluationContext ctx)
    {
        float t = (float)(ctx.Rng?.NextDouble() ?? 0.5);
        t = ApplyBias(t, BiasParameter);
        return Mathf.Lerp(RandomMin, RandomMax, t);
    }

    private float EvaluateNoise(ParticleEvaluationContext ctx)
    {
        // Simple fast deterministic sinusoidal noise approximation for CPU particles
        Vector3 pos = ctx.ParticlePosition * NoiseScale + NoiseOffsetRate * ctx.CollectionAge;
        float n = (MathF.Sin(pos.X * 12.9898f + pos.Y * 78.233f + pos.Z * 37.719f) * 43758.5453f) % 1.0f;
        if (n < 0f) n += 1.0f;
        return Mathf.Lerp(NoiseOutputMin, NoiseOutputMax, n);
    }

    private static float Remap(float val, float in0, float in1, float out0, float out1, bool clamp)
    {
        float rangeIn = in1 - in0;
        if (MathF.Abs(rangeIn) < 1e-6f) return out0;

        float t = (val - in0) / rangeIn;
        if (clamp) t = Mathf.Clamp(t, 0.0f, 1.0f);
        return Mathf.Lerp(out0, out1, t);
    }

    public static float ApplyBias(float x, float bias)
    {
        x = Mathf.Clamp(x, 0.0f, 1.0f);
        if (MathF.Abs(bias) < 1e-5f) return x;

        // Schlick Bias: f(x) = x / ((1/b - 2)*(1 - x) + 1)
        float b = Mathf.Clamp((bias + 1.0f) * 0.5f, 0.001f, 0.999f);
        return x / ((1.0f / b - 2.0f) * (1.0f - x) + 1.0f);
    }
}

public class GradientStopDef
{
    public float Position { get; set; } = 0.0f;
    public Vector3 Color { get; set; } = Vector3.Zero;
}

/// <summary>
/// Strongly typed Source 2 Vector Input evaluator (CParticleCollectionVecInput).
/// </summary>
public class ParticleVecInput
{
    public string Type { get; set; } = "PVEC_TYPE_LITERAL";
    public Vector3 LiteralValue { get; set; } = Vector3.Zero;
    public int ControlPoint { get; set; } = 0;
    public ParticleFloatInput ComponentX { get; set; }
    public ParticleFloatInput ComponentY { get; set; }
    public ParticleFloatInput ComponentZ { get; set; }
    public ParticleFloatInput FloatInterp { get; set; }
    public List<GradientStopDef> GradientStops { get; } = new();

    public bool IsPosition { get; set; } = false;

    public static ParticleVecInput FromLiteral(Vector3 val, bool isPosition = false) => new() { LiteralValue = val, Type = "PVEC_TYPE_LITERAL", IsPosition = isPosition };

    public Vector3 Evaluate(ParticleEvaluationContext ctx)
    {
        if (Type == "PVEC_TYPE_FLOAT_INTERP_GRADIENT")
        {
            if (GradientStops.Count == 0) return LiteralValue;
            float t = FloatInterp?.Evaluate(ctx) ?? ctx?.NormalizedAge ?? 0.0f;
            t = Mathf.Clamp(t, 0.0f, 1.0f);

            if (t <= GradientStops[0].Position) return GradientStops[0].Color;
            if (t >= GradientStops[^1].Position) return GradientStops[^1].Color;

            for (int i = 0; i < GradientStops.Count - 1; i++)
            {
                var s0 = GradientStops[i];
                var s1 = GradientStops[i + 1];
                if (t >= s0.Position && t <= s1.Position)
                {
                    float span = s1.Position - s0.Position;
                    float f = span > 1e-5f ? (t - s0.Position) / span : 0.0f;
                    return s0.Color.Lerp(s1.Color, f);
                }
            }
            return GradientStops[^1].Color;
        }

        return Type switch
        {
            "PVEC_TYPE_LITERAL" => LiteralValue,
            "PVEC_TYPE_CONTROL_POINT" => ctx?.ControlPoints?.GetPosition(ControlPoint) ?? Vector3.Zero,
            "PVEC_TYPE_FLOAT_COMPONENTS" => IsPosition
                ? new Vector3(
                    EvaluateDistance(ComponentX, ctx),
                    EvaluateDistance(ComponentZ, ctx), // Source 2 Z is Godot Y (up)
                    -EvaluateDistance(ComponentY, ctx) // Source 2 Y is Godot -Z
                )
                : new Vector3(
                    ComponentX?.Evaluate(ctx) ?? 0f,
                    ComponentY?.Evaluate(ctx) ?? 0f,
                    ComponentZ?.Evaluate(ctx) ?? 0f
                ),
            _ => LiteralValue
        };
    }

    private static float EvaluateDistance(ParticleFloatInput comp, ParticleEvaluationContext ctx)
    {
        if (comp == null) return 0f;
        float val = comp.Evaluate(ctx);
        if (comp.Type == "PF_TYPE_LITERAL")
        {
            return val * 0.0254f; // Inches to meters
        }
        return val;
    }
}
