using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace DeadlockPlayground.Animation
{
    public enum ModifierRemoveMode
    {
        RemoveAll,
        RemoveType,
        RemoveFirst
    }

    /// <summary>
    /// Base class for all Blender-style non-destructive F-Curve modifiers.
    /// Modifiers are evaluated top-to-bottom on a ChannelCurve.
    /// </summary>
    public abstract class FCurveModifier
    {
        public string Name { get; set; } = "";
        public bool IsMuted { get; set; } = false;
        public float Influence { get; set; } = 1.0f; // [0.0 - 1.0]

        // Restrict Frame Range
        public bool RestrictFrameRange { get; set; } = false;
        public float StartFrame { get; set; } = 0f;
        public float EndFrame { get; set; } = 100f;
        public float BlendIn { get; set; } = 0f; // frames to fade in
        public float BlendOut { get; set; } = 0f; // frames to fade out

        public virtual bool RequiresFirst => false;
        public abstract string TypeName { get; }

        public float GetEffectiveInfluence(double time, float fps)
        {
            if (IsMuted) return 0f;
            float inf = Mathf.Clamp(Influence, 0f, 1f);
            if (inf <= 0f) return 0f;
            if (!RestrictFrameRange) return inf;

            float frame = (float)(time * fps);
            if (frame < StartFrame || frame > EndFrame) return 0f;

            float rangeInf = 1.0f;
            if (BlendIn > 0f && frame < StartFrame + BlendIn)
            {
                rangeInf = Mathf.Clamp((frame - StartFrame) / BlendIn, 0f, 1f);
            }
            if (BlendOut > 0f && frame > EndFrame - BlendOut)
            {
                rangeInf = Mathf.Min(rangeInf, Mathf.Clamp((EndFrame - frame) / BlendOut, 0f, 1f));
            }
            return inf * rangeInf;
        }

        public abstract float Evaluate(float baseVal, double time, float fps, GraphEditorCanvas.ChannelCurve curve);
    }

    /// <summary>
    /// 1. Generator Modifier: Evaluates a polynomial formula y = A + Bx + Cx^2 + ... or factorized (Ax+B)(Cx+D).
    /// </summary>
    public class GeneratorModifier : FCurveModifier
    {
        public enum GeneratorMode { ExpandedPolynomial, FactorizedPolynomial }

        public GeneratorMode Mode { get; set; } = GeneratorMode.ExpandedPolynomial;
        public bool IsAdditive { get; set; } = false;
        public int Order { get; set; } = 1;
        public List<float> Coefficients { get; set; } = new() { 0f, 1f, 0f, 0f };

        public override string TypeName => "Generator";

        public GeneratorModifier()
        {
            Name = "Generator";
        }

        public override float Evaluate(float baseVal, double time, float fps, GraphEditorCanvas.ChannelCurve curve)
        {
            float effInf = GetEffectiveInfluence(time, fps);
            if (effInf <= 0f) return baseVal;

            float x = (float)(time * fps);
            float polyVal = 0f;

            if (Mode == GeneratorMode.ExpandedPolynomial)
            {
                float xPow = 1f;
                int maxOrder = Math.Min(Order, Math.Max(0, Coefficients.Count - 1));
                for (int i = 0; i <= maxOrder; i++)
                {
                    float coef = i < Coefficients.Count ? Coefficients[i] : 0f;
                    polyVal += coef * xPow;
                    xPow *= x;
                }
            }
            else // Factorized: (Ax + B)(Cx + D)
            {
                float a = Coefficients.Count > 0 ? Coefficients[0] : 1f;
                float b = Coefficients.Count > 1 ? Coefficients[1] : 0f;
                float c = Coefficients.Count > 2 ? Coefficients[2] : 1f;
                float d = Coefficients.Count > 3 ? Coefficients[3] : 0f;
                polyVal = (a * x + b) * (c * x + d);
            }

            float target = IsAdditive ? (baseVal + polyVal) : polyVal;
            return Mathf.Lerp(baseVal, target, effInf);
        }
    }

    /// <summary>
    /// 2. Built-in Function Modifier: Evaluates trigonometry/math functions (Sine, Cosine, Tangent, etc.).
    /// </summary>
    public class BuiltInFunctionModifier : FCurveModifier
    {
        public enum FunctionType
        {
            Sine,
            Cosine,
            Tangent,
            SquareRoot,
            NaturalLogarithm,
            NormalizedSine
        }

        public FunctionType FuncType { get; set; } = FunctionType.Sine;
        public bool IsAdditive { get; set; } = false;
        public float Amplitude { get; set; } = 1.0f;
        public float PhaseMultiplier { get; set; } = 1.0f;
        public float PhaseOffset { get; set; } = 0.0f;
        public float ValueOffset { get; set; } = 0.0f;

        public override string TypeName => "Built-in Function";

        public BuiltInFunctionModifier()
        {
            Name = "Built-in Function";
        }

        public override float Evaluate(float baseVal, double time, float fps, GraphEditorCanvas.ChannelCurve curve)
        {
            float effInf = GetEffectiveInfluence(time, fps);
            if (effInf <= 0f) return baseVal;

            float x = (float)(time * fps) * PhaseMultiplier + PhaseOffset;
            float rawY = 0f;

            switch (FuncType)
            {
                case FunctionType.Sine:
                    rawY = Mathf.Sin(x);
                    break;
                case FunctionType.Cosine:
                    rawY = Mathf.Cos(x);
                    break;
                case FunctionType.Tangent:
                    rawY = Mathf.Clamp(Mathf.Tan(x), -100f, 100f);
                    break;
                case FunctionType.SquareRoot:
                    rawY = x >= 0f ? Mathf.Sqrt(x) : 0f;
                    break;
                case FunctionType.NaturalLogarithm:
                    rawY = x > 1e-5f ? Mathf.Log(x) : 0f;
                    break;
                case FunctionType.NormalizedSine:
                    rawY = Math.Abs(x) < 1e-4f ? 1.0f : (Mathf.Sin(x) / x);
                    break;
            }

            float funcVal = rawY * Amplitude + ValueOffset;
            float target = IsAdditive ? (baseVal + funcVal) : funcVal;
            return Mathf.Lerp(baseVal, target, effInf);
        }
    }

    /// <summary>
    /// 3. Envelope Modifier: Reshapes curve around a reference value within lower/upper bounds.
    /// </summary>
    public class EnvelopeModifier : FCurveModifier
    {
        public class ControlPoint
        {
            public float Frame { get; set; }
            public float Min { get; set; } = -1.0f;
            public float Max { get; set; } = 1.0f;
        }

        public float ReferenceValue { get; set; } = 0.0f;
        public float DefaultMin { get; set; } = -1.0f;
        public float DefaultMax { get; set; } = 1.0f;
        public List<ControlPoint> ControlPoints { get; set; } = new();

        public override string TypeName => "Envelope";

        public EnvelopeModifier()
        {
            Name = "Envelope";
        }

        public override float Evaluate(float baseVal, double time, float fps, GraphEditorCanvas.ChannelCurve curve)
        {
            float effInf = GetEffectiveInfluence(time, fps);
            if (effInf <= 0f) return baseVal;

            float frame = (float)(time * fps);
            float curMin = DefaultMin;
            float curMax = DefaultMax;

            if (ControlPoints.Count > 0)
            {
                var sorted = ControlPoints.OrderBy(p => p.Frame).ToList();
                if (frame <= sorted[0].Frame)
                {
                    curMin = sorted[0].Min;
                    curMax = sorted[0].Max;
                }
                else if (frame >= sorted[^1].Frame)
                {
                    curMin = sorted[^1].Min;
                    curMax = sorted[^1].Max;
                }
                else
                {
                    for (int i = 0; i < sorted.Count - 1; i++)
                    {
                        if (frame >= sorted[i].Frame && frame <= sorted[i + 1].Frame)
                        {
                            float factor = (frame - sorted[i].Frame) / Math.Max(0.001f, sorted[i + 1].Frame - sorted[i].Frame);
                            curMin = Mathf.Lerp(sorted[i].Min, sorted[i + 1].Min, factor);
                            curMax = Mathf.Lerp(sorted[i].Max, sorted[i + 1].Max, factor);
                            break;
                        }
                    }
                }
            }

            float diff = baseVal - ReferenceValue;
            float target;
            if (diff >= 0f)
            {
                float baseSpan = Math.Max(1e-4f, DefaultMax);
                target = ReferenceValue + diff * (curMax / baseSpan);
            }
            else
            {
                float baseSpan = Math.Min(-1e-4f, DefaultMin);
                target = ReferenceValue + diff * (curMin / baseSpan);
            }

            return Mathf.Lerp(baseVal, target, effInf);
        }
    }

    /// <summary>
    /// 4. Cycles Modifier: Repeats the curve before and after the authored keyframes.
    /// Must be the first modifier in the stack and is incompatible with Smooth.
    /// </summary>
    public class CyclesModifier : FCurveModifier
    {
        public enum CycleMode
        {
            NoCycles,
            RepeatMotion,
            RepeatWithOffset,
            RepeatMirrored
        }

        public CycleMode BeforeMode { get; set; } = CycleMode.RepeatMotion;
        public CycleMode AfterMode { get; set; } = CycleMode.RepeatMotion;
        public int BeforeCycles { get; set; } = 0; // 0 = infinite
        public int AfterCycles { get; set; } = 0; // 0 = infinite

        public override bool RequiresFirst => true;
        public override string TypeName => "Cycles";

        public CyclesModifier()
        {
            Name = "Cycles";
        }

        public override float Evaluate(float baseVal, double time, float fps, GraphEditorCanvas.ChannelCurve curve)
        {
            float effInf = GetEffectiveInfluence(time, fps);
            if (effInf <= 0f || curve.Keys.Count < 2) return baseVal;

            double tMin = curve.Keys[0].Time;
            double tMax = curve.Keys[^1].Time;
            double span = tMax - tMin;
            if (span <= 1e-4) return baseVal;

            float target = baseVal;

            if (time > tMax && AfterMode != CycleMode.NoCycles)
            {
                double delta = time - tMin;
                int cycleIdx = (int)Math.Floor(delta / span);
                if (AfterCycles == 0 || cycleIdx <= AfterCycles)
                {
                    double localT = tMin + (delta - cycleIdx * span);
                    if (AfterMode == CycleMode.RepeatMirrored && (cycleIdx % 2 == 1))
                    {
                        localT = tMax - (localT - tMin);
                    }
                    target = curve.EvaluateBase(localT);
                    if (AfterMode == CycleMode.RepeatWithOffset)
                    {
                        float totalOffset = curve.EvaluateBase(tMax) - curve.EvaluateBase(tMin);
                        target += cycleIdx * totalOffset;
                    }
                }
            }
            else if (time < tMin && BeforeMode != CycleMode.NoCycles)
            {
                double delta = tMax - time;
                int cycleIdx = (int)Math.Floor(delta / span);
                if (BeforeCycles == 0 || cycleIdx <= BeforeCycles)
                {
                    double localT = tMax - (delta - cycleIdx * span);
                    if (BeforeMode == CycleMode.RepeatMirrored && (cycleIdx % 2 == 1))
                    {
                        localT = tMin + (tMax - localT);
                    }
                    target = curve.EvaluateBase(localT);
                    if (BeforeMode == CycleMode.RepeatWithOffset)
                    {
                        float totalOffset = curve.EvaluateBase(tMax) - curve.EvaluateBase(tMin);
                        target -= cycleIdx * totalOffset;
                    }
                }
            }

            return Mathf.Lerp(baseVal, target, effInf);
        }
    }

    /// <summary>
    /// 5. Noise Modifier: Modifies the curve with multi-octave coherent noise formula.
    /// </summary>
    public class NoiseModifier : FCurveModifier
    {
        public enum NoiseBlendType { Replace, Add, Subtract, Multiply }

        public NoiseBlendType BlendType { get; set; } = NoiseBlendType.Add;
        public float Scale { get; set; } = 10.0f;
        public float Strength { get; set; } = 1.0f;
        public float Offset { get; set; } = 0.0f;
        public float Phase { get; set; } = 0.0f;
        public int Depth { get; set; } = 0;
        public float Lacunarity { get; set; } = 2.0f;
        public float Roughness { get; set; } = 0.5f;

        public override string TypeName => "Noise";

        public NoiseModifier()
        {
            Name = "Noise";
        }

        public override float Evaluate(float baseVal, double time, float fps, GraphEditorCanvas.ChannelCurve curve)
        {
            float effInf = GetEffectiveInfluence(time, fps);
            if (effInf <= 0f) return baseVal;

            float x = (float)((time + Offset / Math.Max(1f, fps)) * fps) / Math.Max(0.1f, Scale) + Phase;

            // Multi-octave fractional Brownian motion (fBm)
            float totalNoise = 0f;
            float freq = 1f;
            float amp = 1f;
            float maxAmp = 0f;
            int octaves = Mathf.Clamp(Depth + 1, 1, 6);

            for (int i = 0; i < octaves; i++)
            {
                totalNoise += CoherentNoise1D(x * freq) * amp;
                maxAmp += amp;
                freq *= Math.Max(1.0f, Lacunarity);
                amp *= Math.Clamp(Roughness, 0f, 1f);
            }

            float n = maxAmp > 0f ? (totalNoise / maxAmp) : 0f; // [-0.5, 0.5]
            float target = baseVal;

            switch (BlendType)
            {
                case NoiseBlendType.Replace:
                    target = n * Strength;
                    break;
                case NoiseBlendType.Add:
                    target = baseVal + (n + 0.5f) * Strength;
                    break;
                case NoiseBlendType.Subtract:
                    target = baseVal - (n + 0.5f) * Strength;
                    break;
                case NoiseBlendType.Multiply:
                    target = baseVal * (1.0f + n * Strength);
                    break;
            }

            return Mathf.Lerp(baseVal, target, effInf);
        }

        private static float CoherentNoise1D(float x)
        {
            int i = Mathf.FloorToInt(x);
            float f = x - i;
            float u = f * f * (3f - 2f * f); // Smoothstep

            float g0 = PseudoRandomGrad(i);
            float g1 = PseudoRandomGrad(i + 1);

            float n0 = g0 * f;
            float n1 = g1 * (f - 1f);
            return Mathf.Lerp(n0, n1, u);
        }

        private static float PseudoRandomGrad(int n)
        {
            n = (n << 13) ^ n;
            int nn = (n * (n * n * 15731 + 789221) + 1376312589) & 0x7fffffff;
            return 1.0f - ((float)nn / 1073741824.0f);
        }
    }

    /// <summary>
    /// 6. Limits Modifier: Clamps curve values and/or frames to specific minimum/maximum bounds.
    /// </summary>
    public class LimitsModifier : FCurveModifier
    {
        public bool UseMinX { get; set; } = false;
        public float MinX { get; set; } = 0.0f; // frame
        public bool UseMaxX { get; set; } = false;
        public float MaxX { get; set; } = 100.0f; // frame

        public bool UseMinY { get; set; } = false;
        public float MinY { get; set; } = 0.0f;
        public bool UseMaxY { get; set; } = false;
        public float MaxY { get; set; } = 100.0f;

        public override string TypeName => "Limits";

        public LimitsModifier()
        {
            Name = "Limits";
        }

        public override float Evaluate(float baseVal, double time, float fps, GraphEditorCanvas.ChannelCurve curve)
        {
            float effInf = GetEffectiveInfluence(time, fps);
            if (effInf <= 0f) return baseVal;

            float frame = (float)(time * fps);
            float target = baseVal;

            // X limits: constant extrapolation outside frame bounds
            if (UseMinX && frame < MinX)
            {
                target = curve.EvaluateBase(MinX / fps);
            }
            else if (UseMaxX && frame > MaxX)
            {
                target = curve.EvaluateBase(MaxX / fps);
            }

            // Y limits: clamp value
            if (UseMinY && target < MinY)
            {
                target = MinY;
            }
            if (UseMaxY && target > MaxY)
            {
                target = MaxY;
            }

            return Mathf.Lerp(baseVal, target, effInf);
        }
    }

    /// <summary>
    /// 7. Stepped Interpolation Modifier: Samples curve every N frames producing choppy, stepped motion.
    /// </summary>
    public class SteppedModifier : FCurveModifier
    {
        public float StepSize { get; set; } = 2.0f;
        public float Offset { get; set; } = 0.0f;
        public bool UseFrameRange { get; set; } = false;
        public float RangeStart { get; set; } = 0.0f;
        public float RangeEnd { get; set; } = 100.0f;

        public override string TypeName => "Stepped";

        public SteppedModifier()
        {
            Name = "Stepped";
        }

        public override float Evaluate(float baseVal, double time, float fps, GraphEditorCanvas.ChannelCurve curve)
        {
            float effInf = GetEffectiveInfluence(time, fps);
            if (effInf <= 0f) return baseVal;

            float frame = (float)(time * fps);
            if (UseFrameRange && (frame < RangeStart || frame > RangeEnd))
            {
                return baseVal;
            }

            float step = Math.Max(1.0f, StepSize);
            float steppedFrame = Mathf.Floor((frame - Offset) / step) * step + Offset;
            double steppedTime = steppedFrame / Math.Max(1.0, fps);

            float target = curve.EvaluateBase(steppedTime);
            return Mathf.Lerp(baseVal, target, effInf);
        }
    }

    /// <summary>
    /// 8. Smooth (Gaussian) Modifier: Uses Gaussian smoothing to reduce detail and noise.
    /// Must be first in the stack and is incompatible with Cycles.
    /// </summary>
    public class GaussianSmoothModifier : FCurveModifier
    {
        public float Sigma { get; set; } = 3.0f;
        public int FilterWidth { get; set; } = 5;

        public override bool RequiresFirst => true;
        public override string TypeName => "Smooth (Gaussian)";

        public GaussianSmoothModifier()
        {
            Name = "Smooth (Gaussian)";
        }

        public override float Evaluate(float baseVal, double time, float fps, GraphEditorCanvas.ChannelCurve curve)
        {
            float effInf = GetEffectiveInfluence(time, fps);
            if (effInf <= 0f || Sigma <= 0.01f || FilterWidth <= 0) return baseVal;

            float weightSum = 0f;
            float valSum = 0f;
            float invTwoSigmaSq = 1.0f / (2.0f * Sigma * Sigma);

            for (int i = -FilterWidth; i <= FilterWidth; i++)
            {
                float w = Mathf.Exp(-(i * i) * invTwoSigmaSq);
                double sampleTime = time + (i / (double)Math.Max(1f, fps));
                float sampleVal = curve.EvaluateBase(sampleTime);
                valSum += sampleVal * w;
                weightSum += w;
            }

            float target = weightSum > 0f ? (valSum / weightSum) : baseVal;
            return Mathf.Lerp(baseVal, target, effInf);
        }
    }
}
