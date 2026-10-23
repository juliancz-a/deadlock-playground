using Godot;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace DeadlockPlayground.Painter
{
    public enum FilterType
    {
        GaussianBlur = 0,
        BoxBlur = 1,
        MotionBlur = 2,
        Sharpen = 3,
        UnsharpMask = 4,
        AddNoise = 5,
        Median = 6,
        Mosaic = 7,
        ColorHalftone = 8,
        FindEdges = 9,
        Emboss = 10,
        OilPaint = 11,
        Solarize = 12,
        ChromaticAberration = 13,
        OutlineStroke = 14,
        HighPass = 15
    }

    public enum NoiseDistribution
    {
        Uniform = 0,
        Gaussian = 1
    }

    public enum OutlinePosition
    {
        Outside = 0,
        Inside = 1,
        Center = 2
    }

    public struct FilterParameters
    {
        public FilterType Type;

        // Blur
        public float BlurRadius;         // Gaussian (0.5 - 50), Box (1 - 50)
        public float MotionAngle;        // 0 - 360 deg
        public float MotionDistance;     // 1 - 60 px

        // Sharpen & Unsharp Mask
        public float UnsharpAmount;      // 0 - 300%
        public float UnsharpRadius;      // 1 - 20 px
        public float UnsharpThreshold;   // 0 - 50

        // Noise
        public float NoiseAmount;        // 0 - 100%
        public bool NoiseMonochromatic;
        public NoiseDistribution NoiseDist;

        // Median
        public int MedianRadius;         // 1 - 5 px

        // Pixelate
        public int MosaicCellSize;       // 2 - 64 px
        public int HalftoneDotSize;      // 2 - 16 px
        public int HalftoneLevels;       // 2 - 16

        // Emboss
        public float EmbossAngle;        // -180 to 180 deg
        public float EmbossHeight;       // 1 - 10 px
        public float EmbossAmount;       // 1 - 500%

        // Oil Paint (Kuwahara)
        public int OilPaintRadius;       // 1 - 8 px

        // Chromatic Aberration
        public int ShiftX;              // -20 to +20 px
        public int ShiftY;              // -20 to +20 px

        // Outline / Stroke
        public int OutlineThickness;     // 1 - 25 px
        public Color OutlineColor;
        public OutlinePosition OutlinePos;

        // High Pass
        public float HighPassRadius;     // 1 - 50 px

        public static FilterParameters Default(FilterType type)
        {
            return new FilterParameters
            {
                Type = type,
                BlurRadius = 4.0f,
                MotionAngle = 0.0f,
                MotionDistance = 12.0f,
                UnsharpAmount = 100.0f,
                UnsharpRadius = 2.0f,
                UnsharpThreshold = 0.0f,
                NoiseAmount = 15.0f,
                NoiseMonochromatic = true,
                NoiseDist = NoiseDistribution.Uniform,
                MedianRadius = 2,
                MosaicCellSize = 8,
                HalftoneDotSize = 6,
                HalftoneLevels = 4,
                EmbossAngle = 45.0f,
                EmbossHeight = 2.0f,
                EmbossAmount = 100.0f,
                OilPaintRadius = 3,
                ShiftX = 4,
                ShiftY = 0,
                OutlineThickness = 3,
                OutlineColor = new Color(0.85f, 0.68f, 0.32f, 1.0f),
                OutlinePos = OutlinePosition.Outside,
                HighPassRadius = 10.0f
            };
        }
    }

    public static class FilterEngine
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float SampleMask(byte[] mask, int maskW, int maskH, int x, int y, int w, int h)
        {
            if (mask == null || maskW <= 0 || maskH <= 0) return 1.0f;
            int mx = (int)((long)x * maskW / w);
            int my = (int)((long)y * maskH / h);
            int idx = my * maskW + mx;
            if (idx < 0 || idx >= mask.Length) return 0f;
            return mask[idx] / 255.0f;
        }

        public static unsafe void ApplyFilter(byte[] src, byte[] dst, int w, int h, FilterParameters p, byte[] mask = null, int maskW = 0, int maskH = 0, bool isSamplingUnderlying = false)
        {
            if (src == null || dst == null || w <= 0 || h <= 0) return;

            switch (p.Type)
            {
                case FilterType.GaussianBlur:
                    ApplyGaussianBlur(src, dst, w, h, p.BlurRadius, mask, maskW, maskH);
                    break;
                case FilterType.BoxBlur:
                    ApplyBoxBlur(src, dst, w, h, (int)MathF.Round(p.BlurRadius), mask, maskW, maskH);
                    break;
                case FilterType.MotionBlur:
                    ApplyMotionBlur(src, dst, w, h, p.MotionAngle, p.MotionDistance, mask, maskW, maskH);
                    break;
                case FilterType.Sharpen:
                    ApplySharpen(src, dst, w, h, mask, maskW, maskH);
                    break;
                case FilterType.UnsharpMask:
                    ApplyUnsharpMask(src, dst, w, h, p.UnsharpAmount, p.UnsharpRadius, p.UnsharpThreshold, mask, maskW, maskH);
                    break;
                case FilterType.AddNoise:
                    ApplyAddNoise(src, dst, w, h, p.NoiseAmount, p.NoiseMonochromatic, p.NoiseDist, mask, maskW, maskH);
                    break;
                case FilterType.Median:
                    ApplyMedian(src, dst, w, h, p.MedianRadius, mask, maskW, maskH);
                    break;
                case FilterType.Mosaic:
                    ApplyMosaic(src, dst, w, h, p.MosaicCellSize, mask, maskW, maskH);
                    break;
                case FilterType.ColorHalftone:
                    ApplyColorHalftone(src, dst, w, h, p.HalftoneDotSize, p.HalftoneLevels, mask, maskW, maskH);
                    break;
                case FilterType.FindEdges:
                    ApplyFindEdges(src, dst, w, h, mask, maskW, maskH);
                    break;
                case FilterType.Emboss:
                    ApplyEmboss(src, dst, w, h, p.EmbossAngle, p.EmbossHeight, p.EmbossAmount, mask, maskW, maskH);
                    break;
                case FilterType.OilPaint:
                    ApplyOilPaintKuwahara(src, dst, w, h, p.OilPaintRadius, mask, maskW, maskH);
                    break;
                case FilterType.Solarize:
                    ApplySolarize(src, dst, w, h, mask, maskW, maskH);
                    break;
                case FilterType.ChromaticAberration:
                    ApplyChromaticAberration(src, dst, w, h, p.ShiftX, p.ShiftY, mask, maskW, maskH);
                    break;
                case FilterType.OutlineStroke:
                    ApplyOutlineStroke(src, dst, w, h, p.OutlineThickness, p.OutlineColor, p.OutlinePos, mask, maskW, maskH);
                    break;
                case FilterType.HighPass:
                    ApplyHighPass(src, dst, w, h, p.HighPassRadius, mask, maskW, maskH);
                    break;
                default:
                    Buffer.BlockCopy(src, 0, dst, 0, src.Length);
                    break;
            }

            if (isSamplingUnderlying && mask != null && maskW > 0 && maskH > 0)
            {
                fixed (byte* pDst = dst)
                {
                    Half* hDst = (Half*)pDst;
                    Parallel.For(0, h, y =>
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int outIdx = (y * w + x) * 4;
                            float m = SampleMask(mask, maskW, maskH, x, y, w, h);
                            if (m <= 0.001f)
                            {
                                hDst[outIdx] = (Half)0f;
                                hDst[outIdx + 1] = (Half)0f;
                                hDst[outIdx + 2] = (Half)0f;
                                hDst[outIdx + 3] = (Half)0f;
                            }
                            else
                            {
                                hDst[outIdx + 3] = (Half)Math.Clamp((float)hDst[outIdx + 3] * m, 0f, 1f);
                            }
                        }
                    });
                }
            }
        }

        // --- Fast Separable 1D Gaussian Blur ---
        public static unsafe void ApplyGaussianBlur(byte[] src, byte[] dst, int w, int h, float radius, byte[] mask, int maskW, int maskH)
        {
            radius = MathF.Max(0.5f, radius);
            int r = (int)MathF.Ceiling(radius * 2.5f);
            if (r < 1) r = 1;
            int kSize = r * 2 + 1;

            float[] kernel = new float[kSize];
            float sigma = MathF.Max(0.1f, radius);
            float twoSigmaSq = 2.0f * sigma * sigma;
            float sum = 0f;

            for (int i = -r; i <= r; i++)
            {
                float val = MathF.Exp(-(i * i) / twoSigmaSq);
                kernel[i + r] = val;
                sum += val;
            }
            for (int i = 0; i < kSize; i++) kernel[i] /= sum;

            byte[] temp = ArrayPool<byte>.Shared.Rent(src.Length);

            try
            {
                fixed (byte* pSrc = src, pDst = dst, pTemp = temp)
                {
                    Half* hSrc = (Half*)pSrc;
                    Half* hDst = (Half*)pDst;
                    Half* hTemp = (Half*)pTemp;

                    // Pass 1: Horizontal 1D Blur (hSrc -> hTemp)
                    Parallel.For(0, h, y =>
                    {
                        int rowOffset = y * w;
                        for (int x = 0; x < w; x++)
                        {
                            float accR = 0f, accG = 0f, accB = 0f, accA = 0f;
                            for (int k = -r; k <= r; k++)
                            {
                                int sx = Math.Clamp(x + k, 0, w - 1);
                                float weight = kernel[k + r];
                                int idx = (rowOffset + sx) * 4;

                                accR += (float)hSrc[idx] * weight;
                                accG += (float)hSrc[idx + 1] * weight;
                                accB += (float)hSrc[idx + 2] * weight;
                                accA += (float)hSrc[idx + 3] * weight;
                            }

                            int outIdx = (rowOffset + x) * 4;
                            hTemp[outIdx] = (Half)accR;
                            hTemp[outIdx + 1] = (Half)accG;
                            hTemp[outIdx + 2] = (Half)accB;
                            hTemp[outIdx + 3] = (Half)accA;
                        }
                    });

                    // Pass 2: Vertical 1D Blur (hTemp -> hDst) with Mask Lerp
                    Parallel.For(0, h, y =>
                    {
                        for (int x = 0; x < w; x++)
                        {
                            float accR = 0f, accG = 0f, accB = 0f, accA = 0f;
                            for (int k = -r; k <= r; k++)
                            {
                                int sy = Math.Clamp(y + k, 0, h - 1);
                                float weight = kernel[k + r];
                                int idx = (sy * w + x) * 4;

                                accR += (float)hTemp[idx] * weight;
                                accG += (float)hTemp[idx + 1] * weight;
                                accB += (float)hTemp[idx + 2] * weight;
                                accA += (float)hTemp[idx + 3] * weight;
                            }

                            int outIdx = (y * w + x) * 4;
                            float m = SampleMask(mask, maskW, maskH, x, y, w, h);

                            if (m >= 0.999f)
                            {
                                hDst[outIdx] = (Half)accR;
                                hDst[outIdx + 1] = (Half)accG;
                                hDst[outIdx + 2] = (Half)accB;
                                hDst[outIdx + 3] = (Half)accA;
                            }
                            else if (m <= 0.001f)
                            {
                                hDst[outIdx] = hSrc[outIdx];
                                hDst[outIdx + 1] = hSrc[outIdx + 1];
                                hDst[outIdx + 2] = hSrc[outIdx + 2];
                                hDst[outIdx + 3] = hSrc[outIdx + 3];
                            }
                            else
                            {
                                hDst[outIdx] = (Half)Mathf.Lerp((float)hSrc[outIdx], accR, m);
                                hDst[outIdx + 1] = (Half)Mathf.Lerp((float)hSrc[outIdx + 1], accG, m);
                                hDst[outIdx + 2] = (Half)Mathf.Lerp((float)hSrc[outIdx + 2], accB, m);
                                hDst[outIdx + 3] = (Half)Mathf.Lerp((float)hSrc[outIdx + 3], accA, m);
                            }
                        }
                    });
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(temp);
            }
        }

        // --- Separable Box Blur ---
        public static unsafe void ApplyBoxBlur(byte[] src, byte[] dst, int w, int h, int radius, byte[] mask, int maskW, int maskH)
        {
            if (radius < 1) radius = 1;
            byte[] temp = ArrayPool<byte>.Shared.Rent(src.Length);

            try
            {
                fixed (byte* pSrc = src, pDst = dst, pTemp = temp)
                {
                    Half* hSrc = (Half*)pSrc;
                    Half* hDst = (Half*)pDst;
                    Half* hTemp = (Half*)pTemp;

                    int count = radius * 2 + 1;
                    float invCount = 1.0f / count;

                    // Pass 1: Horizontal
                    Parallel.For(0, h, y =>
                    {
                        int rowOffset = y * w;
                        for (int x = 0; x < w; x++)
                        {
                            float accR = 0f, accG = 0f, accB = 0f, accA = 0f;
                            for (int k = -radius; k <= radius; k++)
                            {
                                int sx = Math.Clamp(x + k, 0, w - 1);
                                int idx = (rowOffset + sx) * 4;
                                accR += (float)hSrc[idx];
                                accG += (float)hSrc[idx + 1];
                                accB += (float)hSrc[idx + 2];
                                accA += (float)hSrc[idx + 3];
                            }
                            int outIdx = (rowOffset + x) * 4;
                            hTemp[outIdx] = (Half)(accR * invCount);
                            hTemp[outIdx + 1] = (Half)(accG * invCount);
                            hTemp[outIdx + 2] = (Half)(accB * invCount);
                            hTemp[outIdx + 3] = (Half)(accA * invCount);
                        }
                    });

                    // Pass 2: Vertical
                    Parallel.For(0, h, y =>
                    {
                        for (int x = 0; x < w; x++)
                        {
                            float accR = 0f, accG = 0f, accB = 0f, accA = 0f;
                            for (int k = -radius; k <= radius; k++)
                            {
                                int sy = Math.Clamp(y + k, 0, h - 1);
                                int idx = (sy * w + x) * 4;
                                accR += (float)hTemp[idx];
                                accG += (float)hTemp[idx + 1];
                                accB += (float)hTemp[idx + 2];
                                accA += (float)hTemp[idx + 3];
                            }

                            int outIdx = (y * w + x) * 4;
                            float m = SampleMask(mask, maskW, maskH, x, y, w, h);
                            float rR = accR * invCount;
                            float rG = accG * invCount;
                            float rB = accB * invCount;
                            float rA = accA * invCount;

                            hDst[outIdx] = (Half)Mathf.Lerp((float)hSrc[outIdx], rR, m);
                            hDst[outIdx + 1] = (Half)Mathf.Lerp((float)hSrc[outIdx + 1], rG, m);
                            hDst[outIdx + 2] = (Half)Mathf.Lerp((float)hSrc[outIdx + 2], rB, m);
                            hDst[outIdx + 3] = (Half)Mathf.Lerp((float)hSrc[outIdx + 3], rA, m);
                        }
                    });
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(temp);
            }
        }

        // --- Motion Blur ---
        public static unsafe void ApplyMotionBlur(byte[] src, byte[] dst, int w, int h, float angleDeg, float distance, byte[] mask, int maskW, int maskH)
        {
            distance = MathF.Max(1.0f, distance);
            int steps = Math.Max(3, (int)MathF.Round(distance));
            float rad = angleDeg * (MathF.PI / 180.0f);
            float dirX = MathF.Cos(rad);
            float dirY = MathF.Sin(rad);

            fixed (byte* pSrc = src, pDst = dst)
            {
                Half* hSrc = (Half*)pSrc;
                Half* hDst = (Half*)pDst;

                Parallel.For(0, h, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        float accR = 0f, accG = 0f, accB = 0f, accA = 0f;
                        for (int s = 0; s < steps; s++)
                        {
                            float t = (s / (float)(steps - 1) - 0.5f) * distance;
                            int sx = Math.Clamp((int)MathF.Round(x + dirX * t), 0, w - 1);
                            int sy = Math.Clamp((int)MathF.Round(y + dirY * t), 0, h - 1);
                            int idx = (sy * w + sx) * 4;

                            accR += (float)hSrc[idx];
                            accG += (float)hSrc[idx + 1];
                            accB += (float)hSrc[idx + 2];
                            accA += (float)hSrc[idx + 3];
                        }

                        int outIdx = (y * w + x) * 4;
                        float invSteps = 1.0f / steps;
                        float m = SampleMask(mask, maskW, maskH, x, y, w, h);

                        hDst[outIdx] = (Half)Mathf.Lerp((float)hSrc[outIdx], accR * invSteps, m);
                        hDst[outIdx + 1] = (Half)Mathf.Lerp((float)hSrc[outIdx + 1], accG * invSteps, m);
                        hDst[outIdx + 2] = (Half)Mathf.Lerp((float)hSrc[outIdx + 2], accB * invSteps, m);
                        hDst[outIdx + 3] = (Half)Mathf.Lerp((float)hSrc[outIdx + 3], accA * invSteps, m);
                    }
                });
            }
        }

        // --- Immediate 3x3 Sharpen ---
        public static unsafe void ApplySharpen(byte[] src, byte[] dst, int w, int h, byte[] mask, int maskW, int maskH)
        {
            fixed (byte* pSrc = src, pDst = dst)
            {
                Half* hSrc = (Half*)pSrc;
                Half* hDst = (Half*)pDst;

                Parallel.For(0, h, y =>
                {
                    int topY = Math.Max(0, y - 1);
                    int botY = Math.Min(h - 1, y + 1);

                    for (int x = 0; x < w; x++)
                    {
                        int leftX = Math.Max(0, x - 1);
                        int rightX = Math.Min(w - 1, x + 1);

                        int cIdx = (y * w + x) * 4;
                        int tIdx = (topY * w + x) * 4;
                        int bIdx = (botY * w + x) * 4;
                        int lIdx = (y * w + leftX) * 4;
                        int rIdx = (y * w + rightX) * 4;

                        // 3x3 kernel: Center 5, Cross -1
                        float r = (float)hSrc[cIdx] * 5.0f - ((float)hSrc[tIdx] + (float)hSrc[bIdx] + (float)hSrc[lIdx] + (float)hSrc[rIdx]);
                        float g = (float)hSrc[cIdx + 1] * 5.0f - ((float)hSrc[tIdx + 1] + (float)hSrc[bIdx + 1] + (float)hSrc[lIdx + 1] + (float)hSrc[rIdx + 1]);
                        float b = (float)hSrc[cIdx + 2] * 5.0f - ((float)hSrc[tIdx + 2] + (float)hSrc[bIdx + 2] + (float)hSrc[lIdx + 2] + (float)hSrc[rIdx + 2]);
                        float a = (float)hSrc[cIdx + 3];

                        float m = SampleMask(mask, maskW, maskH, x, y, w, h);
                        hDst[cIdx] = (Half)Mathf.Lerp((float)hSrc[cIdx], Math.Clamp(r, 0f, 2f), m);
                        hDst[cIdx + 1] = (Half)Mathf.Lerp((float)hSrc[cIdx + 1], Math.Clamp(g, 0f, 2f), m);
                        hDst[cIdx + 2] = (Half)Mathf.Lerp((float)hSrc[cIdx + 2], Math.Clamp(b, 0f, 2f), m);
                        hDst[cIdx + 3] = (Half)a;
                    }
                });
            }
        }

        // --- Unsharp Mask ---
        public static unsafe void ApplyUnsharpMask(byte[] src, byte[] dst, int w, int h, float amountPercent, float radius, float threshold, byte[] mask, int maskW, int maskH)
        {
            byte[] blurred = ArrayPool<byte>.Shared.Rent(src.Length);

            try
            {
                ApplyGaussianBlur(src, blurred, w, h, radius, null, 0, 0);

                float amount = amountPercent / 100.0f;
                float thresh = threshold / 255.0f;

                fixed (byte* pSrc = src, pDst = dst, pBlur = blurred)
                {
                    Half* hSrc = (Half*)pSrc;
                    Half* hDst = (Half*)pDst;
                    Half* hBlur = (Half*)pBlur;

                    Parallel.For(0, h, y =>
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int idx = (y * w + x) * 4;
                            float oR = (float)hSrc[idx];
                            float oG = (float)hSrc[idx + 1];
                            float oB = (float)hSrc[idx + 2];
                            float oA = (float)hSrc[idx + 3];

                            float bR = (float)hBlur[idx];
                            float bG = (float)hBlur[idx + 1];
                            float bB = (float)hBlur[idx + 2];

                            float diffR = oR - bR;
                            float diffG = oG - bG;
                            float diffB = oB - bB;

                            float nR = MathF.Abs(diffR) >= thresh ? oR + diffR * amount : oR;
                            float nG = MathF.Abs(diffG) >= thresh ? oG + diffG * amount : oG;
                            float nB = MathF.Abs(diffB) >= thresh ? oB + diffB * amount : oB;

                            float m = SampleMask(mask, maskW, maskH, x, y, w, h);
                            hDst[idx] = (Half)Mathf.Lerp(oR, Math.Clamp(nR, 0f, 2f), m);
                            hDst[idx + 1] = (Half)Mathf.Lerp(oG, Math.Clamp(nG, 0f, 2f), m);
                            hDst[idx + 2] = (Half)Mathf.Lerp(oB, Math.Clamp(nB, 0f, 2f), m);
                            hDst[idx + 3] = (Half)oA;
                        }
                    });
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(blurred);
            }
        }

        // --- Add Noise ---
        public static unsafe void ApplyAddNoise(byte[] src, byte[] dst, int w, int h, float amountPercent, bool monochromatic, NoiseDistribution dist, byte[] mask, int maskW, int maskH)
        {
            float scale = (amountPercent / 100.0f) * 0.5f;

            fixed (byte* pSrc = src, pDst = dst)
            {
                Half* hSrc = (Half*)pSrc;
                Half* hDst = (Half*)pDst;

                Parallel.For(0, h, y =>
                {
                    // Thread-local pseudo random generator based on coordinates & salt
                    uint seed = (uint)(y * 1999 + 12345);

                    float NextFloat()
                    {
                        seed = (seed * 1664525u + 1013904223u);
                        return (seed >> 8) / 16777216.0f; // 0..1
                    }

                    float NextNoise()
                    {
                        if (dist == NoiseDistribution.Uniform)
                        {
                            return (NextFloat() * 2.0f - 1.0f) * scale;
                        }
                        else
                        {
                            // Box-Muller Gaussian
                            float u1 = MathF.Max(0.0001f, NextFloat());
                            float u2 = NextFloat();
                            return MathF.Sqrt(-2.0f * MathF.Log(u1)) * MathF.Cos(2.0f * MathF.PI * u2) * scale * 0.5f;
                        }
                    }

                    for (int x = 0; x < w; x++)
                    {
                        int idx = (y * w + x) * 4;
                        float oR = (float)hSrc[idx];
                        float oG = (float)hSrc[idx + 1];
                        float oB = (float)hSrc[idx + 2];
                        float oA = (float)hSrc[idx + 3];

                        float nR, nG, nB;
                        if (monochromatic)
                        {
                            float n = NextNoise();
                            nR = oR + n;
                            nG = oG + n;
                            nB = oB + n;
                        }
                        else
                        {
                            nR = oR + NextNoise();
                            nG = oG + NextNoise();
                            nB = oB + NextNoise();
                        }

                        float m = SampleMask(mask, maskW, maskH, x, y, w, h);
                        hDst[idx] = (Half)Mathf.Lerp(oR, Math.Clamp(nR, 0f, 2f), m);
                        hDst[idx + 1] = (Half)Mathf.Lerp(oG, Math.Clamp(nG, 0f, 2f), m);
                        hDst[idx + 2] = (Half)Mathf.Lerp(oB, Math.Clamp(nB, 0f, 2f), m);
                        hDst[idx + 3] = (Half)oA;
                    }
                });
            }
        }

        // --- Median Filter (Denoise) ---
        public static unsafe void ApplyMedian(byte[] src, byte[] dst, int w, int h, int radius, byte[] mask, int maskW, int maskH)
        {
            radius = Math.Clamp(radius, 1, 5);
            int windowSize = (radius * 2 + 1) * (radius * 2 + 1);

            fixed (byte* pSrc = src, pDst = dst)
            {
                Half* hSrc = (Half*)pSrc;
                Half* hDst = (Half*)pDst;

                Parallel.For(0, h, y =>
                {
                    float[] rVals = new float[windowSize];
                    float[] gVals = new float[windowSize];
                    float[] bVals = new float[windowSize];

                    for (int x = 0; x < w; x++)
                    {
                        int count = 0;
                        for (int dy = -radius; dy <= radius; dy++)
                        {
                            int sy = Math.Clamp(y + dy, 0, h - 1);
                            for (int dx = -radius; dx <= radius; dx++)
                            {
                                int sx = Math.Clamp(x + dx, 0, w - 1);
                                int sIdx = (sy * w + sx) * 4;

                                rVals[count] = (float)hSrc[sIdx];
                                gVals[count] = (float)hSrc[sIdx + 1];
                                bVals[count] = (float)hSrc[sIdx + 2];
                                count++;
                            }
                        }

                        Array.Sort(rVals, 0, count);
                        Array.Sort(gVals, 0, count);
                        Array.Sort(bVals, 0, count);

                        int mid = count / 2;
                        int outIdx = (y * w + x) * 4;
                        float m = SampleMask(mask, maskW, maskH, x, y, w, h);

                        hDst[outIdx] = (Half)Mathf.Lerp((float)hSrc[outIdx], rVals[mid], m);
                        hDst[outIdx + 1] = (Half)Mathf.Lerp((float)hSrc[outIdx + 1], gVals[mid], m);
                        hDst[outIdx + 2] = (Half)Mathf.Lerp((float)hSrc[outIdx + 2], bVals[mid], m);
                        hDst[outIdx + 3] = hSrc[outIdx + 3];
                    }
                });
            }
        }

        // --- Mosaic / Pixelate ---
        public static unsafe void ApplyMosaic(byte[] src, byte[] dst, int w, int h, int cellSize, byte[] mask, int maskW, int maskH)
        {
            cellSize = Math.Clamp(cellSize, 2, 64);

            fixed (byte* pSrc = src, pDst = dst)
            {
                Half* hSrc = (Half*)pSrc;
                Half* hDst = (Half*)pDst;

                int blockRows = (h + cellSize - 1) / cellSize;
                int blockCols = (w + cellSize - 1) / cellSize;

                Parallel.For(0, blockRows, by =>
                {
                    int startY = by * cellSize;
                    int endY = Math.Min(h, startY + cellSize);

                    for (int bx = 0; bx < blockCols; bx++)
                    {
                        int startX = bx * cellSize;
                        int endX = Math.Min(w, startX + cellSize);

                        // Compute average color in block
                        float accR = 0f, accG = 0f, accB = 0f, accA = 0f;
                        int count = 0;

                        for (int y = startY; y < endY; y++)
                        {
                            for (int x = startX; x < endX; x++)
                            {
                                int idx = (y * w + x) * 4;
                                accR += (float)hSrc[idx];
                                accG += (float)hSrc[idx + 1];
                                accB += (float)hSrc[idx + 2];
                                accA += (float)hSrc[idx + 3];
                                count++;
                            }
                        }

                        float inv = count > 0 ? 1.0f / count : 1.0f;
                        float avgR = accR * inv;
                        float avgG = accG * inv;
                        float avgB = accB * inv;
                        float avgA = accA * inv;

                        // Fill block with mask lerp
                        for (int y = startY; y < endY; y++)
                        {
                            for (int x = startX; x < endX; x++)
                            {
                                int idx = (y * w + x) * 4;
                                float m = SampleMask(mask, maskW, maskH, x, y, w, h);

                                hDst[idx] = (Half)Mathf.Lerp((float)hSrc[idx], avgR, m);
                                hDst[idx + 1] = (Half)Mathf.Lerp((float)hSrc[idx + 1], avgG, m);
                                hDst[idx + 2] = (Half)Mathf.Lerp((float)hSrc[idx + 2], avgB, m);
                                hDst[idx + 3] = (Half)Mathf.Lerp((float)hSrc[idx + 3], avgA, m);
                            }
                        }
                    }
                });
            }
        }

        // --- Color Halftone / Dither ---
        public static unsafe void ApplyColorHalftone(byte[] src, byte[] dst, int w, int h, int dotSize, int levels, byte[] mask, int maskW, int maskH)
        {
            dotSize = Math.Clamp(dotSize, 2, 16);
            levels = Math.Clamp(levels, 2, 16);
            float invLevels = 1.0f / (levels - 1);

            fixed (byte* pSrc = src, pDst = dst)
            {
                Half* hSrc = (Half*)pSrc;
                Half* hDst = (Half*)pDst;

                Parallel.For(0, h, y =>
                {
                    int py = y % dotSize;
                    for (int x = 0; x < w; x++)
                    {
                        int px = x % dotSize;
                        // Distance from cell center for radial halftone dot pattern
                        float cx = (dotSize - 1) * 0.5f;
                        float cy = (dotSize - 1) * 0.5f;
                        float dist = MathF.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy)) / (dotSize * 0.707f);
                        float threshold = (dist - 0.5f) * 0.35f;

                        int idx = (y * w + x) * 4;
                        float oR = (float)hSrc[idx];
                        float oG = (float)hSrc[idx + 1];
                        float oB = (float)hSrc[idx + 2];
                        float oA = (float)hSrc[idx + 3];

                        float rStep = MathF.Round(Math.Clamp(oR + threshold, 0f, 1f) * (levels - 1)) * invLevels;
                        float gStep = MathF.Round(Math.Clamp(oG + threshold, 0f, 1f) * (levels - 1)) * invLevels;
                        float bStep = MathF.Round(Math.Clamp(oB + threshold, 0f, 1f) * (levels - 1)) * invLevels;

                        float m = SampleMask(mask, maskW, maskH, x, y, w, h);
                        hDst[idx] = (Half)Mathf.Lerp(oR, rStep, m);
                        hDst[idx + 1] = (Half)Mathf.Lerp(oG, gStep, m);
                        hDst[idx + 2] = (Half)Mathf.Lerp(oB, bStep, m);
                        hDst[idx + 3] = (Half)oA;
                    }
                });
            }
        }

        // --- Find Edges (Sobel) ---
        public static unsafe void ApplyFindEdges(byte[] src, byte[] dst, int w, int h, byte[] mask, int maskW, int maskH)
        {
            fixed (byte* pSrc = src, pDst = dst)
            {
                Half* hSrc = (Half*)pSrc;
                Half* hDst = (Half*)pDst;

                Parallel.For(0, h, y =>
                {
                    int topY = Math.Max(0, y - 1);
                    int botY = Math.Min(h - 1, y + 1);

                    for (int x = 0; x < w; x++)
                    {
                        int leftX = Math.Max(0, x - 1);
                        int rightX = Math.Min(w - 1, x + 1);

                        float LumAt(int px, int py)
                        {
                            int sIdx = (py * w + px) * 4;
                            return (float)hSrc[sIdx] * 0.2126f + (float)hSrc[sIdx + 1] * 0.7152f + (float)hSrc[sIdx + 2] * 0.0722f;
                        }

                        float tl = LumAt(leftX, topY),  tc = LumAt(x, topY),  tr = LumAt(rightX, topY);
                        float ml = LumAt(leftX, y),                           mr = LumAt(rightX, y);
                        float bl = LumAt(leftX, botY),  bc = LumAt(x, botY),  br = LumAt(rightX, botY);

                        float gx = (tr + 2f * mr + br) - (tl + 2f * ml + bl);
                        float gy = (bl + 2f * bc + br) - (tl + 2f * tc + tr);
                        float edge = Math.Clamp(MathF.Sqrt(gx * gx + gy * gy) * 2.0f, 0f, 1f);

                        int outIdx = (y * w + x) * 4;
                        float m = SampleMask(mask, maskW, maskH, x, y, w, h);

                        hDst[outIdx] = (Half)Mathf.Lerp((float)hSrc[outIdx], edge, m);
                        hDst[outIdx + 1] = (Half)Mathf.Lerp((float)hSrc[outIdx + 1], edge, m);
                        hDst[outIdx + 2] = (Half)Mathf.Lerp((float)hSrc[outIdx + 2], edge, m);
                        hDst[outIdx + 3] = hSrc[outIdx + 3];
                    }
                });
            }
        }

        // --- Emboss ---
        public static unsafe void ApplyEmboss(byte[] src, byte[] dst, int w, int h, float angleDeg, float height, float amountPercent, byte[] mask, int maskW, int maskH)
        {
            float rad = angleDeg * (MathF.PI / 180.0f);
            float dx = MathF.Cos(rad) * height;
            float dy = MathF.Sin(rad) * height;
            float factor = (amountPercent / 100.0f) * 2.0f;

            fixed (byte* pSrc = src, pDst = dst)
            {
                Half* hSrc = (Half*)pSrc;
                Half* hDst = (Half*)pDst;

                Parallel.For(0, h, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        int x1 = Math.Clamp((int)MathF.Round(x + dx), 0, w - 1);
                        int y1 = Math.Clamp((int)MathF.Round(y + dy), 0, h - 1);
                        int x2 = Math.Clamp((int)MathF.Round(x - dx), 0, w - 1);
                        int y2 = Math.Clamp((int)MathF.Round(y - dy), 0, h - 1);

                        int idx1 = (y1 * w + x1) * 4;
                        int idx2 = (y2 * w + x2) * 4;

                        float lum1 = (float)hSrc[idx1] * 0.2126f + (float)hSrc[idx1 + 1] * 0.7152f + (float)hSrc[idx1 + 2] * 0.0722f;
                        float lum2 = (float)hSrc[idx2] * 0.2126f + (float)hSrc[idx2 + 1] * 0.7152f + (float)hSrc[idx2 + 2] * 0.0722f;

                        float emb = Math.Clamp(0.5f + (lum1 - lum2) * factor, 0f, 1f);

                        int outIdx = (y * w + x) * 4;
                        float m = SampleMask(mask, maskW, maskH, x, y, w, h);

                        hDst[outIdx] = (Half)Mathf.Lerp((float)hSrc[outIdx], emb, m);
                        hDst[outIdx + 1] = (Half)Mathf.Lerp((float)hSrc[outIdx + 1], emb, m);
                        hDst[outIdx + 2] = (Half)Mathf.Lerp((float)hSrc[outIdx + 2], emb, m);
                        hDst[outIdx + 3] = hSrc[outIdx + 3];
                    }
                });
            }
        }

        // --- Oil Paint / Kuwahara Stylized ---
        public static unsafe void ApplyOilPaintKuwahara(byte[] src, byte[] dst, int w, int h, int radius, byte[] mask, int maskW, int maskH)
        {
            radius = Math.Clamp(radius, 1, 8);

            fixed (byte* pSrc = src, pDst = dst)
            {
                Half* hSrc = (Half*)pSrc;
                Half* hDst = (Half*)pDst;

                Parallel.For(0, h, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        // 4 quadrants: Top-Left, Top-Right, Bottom-Left, Bottom-Right
                        float minVar = float.MaxValue;
                        float bestR = 0f, bestG = 0f, bestB = 0f;

                        void EvalQuadrant(int qx0, int qx1, int qy0, int qy1)
                        {
                            float sumR = 0f, sumG = 0f, sumB = 0f;
                            float sumSq = 0f;
                            int count = 0;

                            for (int qy = qy0; qy <= qy1; qy++)
                            {
                                int sy = Math.Clamp(y + qy, 0, h - 1);
                                for (int qx = qx0; qx <= qx1; qx++)
                                {
                                    int sx = Math.Clamp(x + qx, 0, w - 1);
                                    int idx = (sy * w + sx) * 4;

                                    float r = (float)hSrc[idx];
                                    float g = (float)hSrc[idx + 1];
                                    float b = (float)hSrc[idx + 2];
                                    float lum = r * 0.2126f + g * 0.7152f + b * 0.0722f;

                                    sumR += r;
                                    sumG += g;
                                    sumB += b;
                                    sumSq += lum * lum;
                                    count++;
                                }
                            }

                            if (count > 0)
                            {
                                float meanLum = (sumR * 0.2126f + sumG * 0.7152f + sumB * 0.0722f) / count;
                                float variance = (sumSq / count) - (meanLum * meanLum);

                                if (variance < minVar)
                                {
                                    minVar = variance;
                                    bestR = sumR / count;
                                    bestG = sumG / count;
                                    bestB = sumB / count;
                                }
                            }
                        }

                        // Q1: Top-Left
                        EvalQuadrant(-radius, 0, -radius, 0);
                        // Q2: Top-Right
                        EvalQuadrant(0, radius, -radius, 0);
                        // Q3: Bottom-Left
                        EvalQuadrant(-radius, 0, 0, radius);
                        // Q4: Bottom-Right
                        EvalQuadrant(0, radius, 0, radius);

                        int outIdx = (y * w + x) * 4;
                        float m = SampleMask(mask, maskW, maskH, x, y, w, h);

                        hDst[outIdx] = (Half)Mathf.Lerp((float)hSrc[outIdx], bestR, m);
                        hDst[outIdx + 1] = (Half)Mathf.Lerp((float)hSrc[outIdx + 1], bestG, m);
                        hDst[outIdx + 2] = (Half)Mathf.Lerp((float)hSrc[outIdx + 2], bestB, m);
                        hDst[outIdx + 3] = hSrc[outIdx + 3];
                    }
                });
            }
        }

        // --- Solarize ---
        public static unsafe void ApplySolarize(byte[] src, byte[] dst, int w, int h, byte[] mask, int maskW, int maskH)
        {
            fixed (byte* pSrc = src, pDst = dst)
            {
                Half* hSrc = (Half*)pSrc;
                Half* hDst = (Half*)pDst;

                Parallel.For(0, h, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        int idx = (y * w + x) * 4;
                        float r = (float)hSrc[idx];
                        float g = (float)hSrc[idx + 1];
                        float b = (float)hSrc[idx + 2];
                        float a = (float)hSrc[idx + 3];

                        float lum = r * 0.2126f + g * 0.7152f + b * 0.0722f;
                        float solR = lum > 0.5f ? 1.0f - r : r;
                        float solG = lum > 0.5f ? 1.0f - g : g;
                        float solB = lum > 0.5f ? 1.0f - b : b;

                        float m = SampleMask(mask, maskW, maskH, x, y, w, h);
                        hDst[idx] = (Half)Mathf.Lerp(r, Math.Clamp(solR, 0f, 1f), m);
                        hDst[idx + 1] = (Half)Mathf.Lerp(g, Math.Clamp(solG, 0f, 1f), m);
                        hDst[idx + 2] = (Half)Mathf.Lerp(b, Math.Clamp(solB, 0f, 1f), m);
                        hDst[idx + 3] = (Half)a;
                    }
                });
            }
        }

        // --- Chromatic Aberration ---
        public static unsafe void ApplyChromaticAberration(byte[] src, byte[] dst, int w, int h, int shiftX, int shiftY, byte[] mask, int maskW, int maskH)
        {
            fixed (byte* pSrc = src, pDst = dst)
            {
                Half* hSrc = (Half*)pSrc;
                Half* hDst = (Half*)pDst;

                Parallel.For(0, h, y =>
                {
                    int rx = Math.Clamp(shiftX, -w, w);
                    int ry = Math.Clamp(shiftY, -h, h);

                    for (int x = 0; x < w; x++)
                    {
                        // Red sampled with +shift
                        int redX = Math.Clamp(x + rx, 0, w - 1);
                        int redY = Math.Clamp(y + ry, 0, h - 1);
                        int redIdx = (redY * w + redX) * 4;

                        // Blue sampled with -shift
                        int blueX = Math.Clamp(x - rx, 0, w - 1);
                        int blueY = Math.Clamp(y - ry, 0, h - 1);
                        int blueIdx = (blueY * w + blueX) * 4;

                        int origIdx = (y * w + x) * 4;

                        float r = (float)hSrc[redIdx];
                        float g = (float)hSrc[origIdx + 1];
                        float b = (float)hSrc[blueIdx + 2];
                        float a = (float)hSrc[origIdx + 3];

                        float m = SampleMask(mask, maskW, maskH, x, y, w, h);
                        hDst[origIdx] = (Half)Mathf.Lerp((float)hSrc[origIdx], r, m);
                        hDst[origIdx + 1] = (Half)Mathf.Lerp((float)hSrc[origIdx + 1], g, m);
                        hDst[origIdx + 2] = (Half)Mathf.Lerp((float)hSrc[origIdx + 2], b, m);
                        hDst[origIdx + 3] = (Half)a;
                    }
                });
            }
        }

        // --- Outline / Stroke Alpha ---
        public static unsafe void ApplyOutlineStroke(byte[] src, byte[] dst, int w, int h, int thickness, Color strokeColor, OutlinePosition pos, byte[] mask, int maskW, int maskH)
        {
            thickness = Math.Clamp(thickness, 1, 25);
            int rSq = thickness * thickness;

            fixed (byte* pSrc = src, pDst = dst)
            {
                Half* hSrc = (Half*)pSrc;
                Half* hDst = (Half*)pDst;

                Parallel.For(0, h, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        int cIdx = (y * w + x) * 4;
                        float oA = (float)hSrc[cIdx + 3];
                        bool isOpaque = oA > 0.15f;

                        // Check neighborhood distance to opposite alpha state
                        bool isOutlinePixel = false;
                        for (int dy = -thickness; dy <= thickness && !isOutlinePixel; dy++)
                        {
                            int sy = Math.Clamp(y + dy, 0, h - 1);
                            for (int dx = -thickness; dx <= thickness && !isOutlinePixel; dx++)
                            {
                                if (dx * dx + dy * dy > rSq) continue;
                                int sx = Math.Clamp(x + dx, 0, w - 1);
                                int nIdx = (sy * w + sx) * 4;
                                float nA = (float)hSrc[nIdx + 3];
                                bool neighborOpaque = nA > 0.15f;

                                if (pos == OutlinePosition.Outside && !isOpaque && neighborOpaque)
                                {
                                    isOutlinePixel = true;
                                }
                                else if (pos == OutlinePosition.Inside && isOpaque && !neighborOpaque)
                                {
                                    isOutlinePixel = true;
                                }
                                else if (pos == OutlinePosition.Center && isOpaque != neighborOpaque)
                                {
                                    isOutlinePixel = true;
                                }
                            }
                        }

                        float m = SampleMask(mask, maskW, maskH, x, y, w, h);
                        if (isOutlinePixel && m > 0.01f)
                        {
                            hDst[cIdx] = (Half)Mathf.Lerp((float)hSrc[cIdx], strokeColor.R, m);
                            hDst[cIdx + 1] = (Half)Mathf.Lerp((float)hSrc[cIdx + 1], strokeColor.G, m);
                            hDst[cIdx + 2] = (Half)Mathf.Lerp((float)hSrc[cIdx + 2], strokeColor.B, m);
                            hDst[cIdx + 3] = (Half)Mathf.Lerp(oA, MathF.Max(oA, strokeColor.A), m);
                        }
                        else
                        {
                            hDst[cIdx] = hSrc[cIdx];
                            hDst[cIdx + 1] = hSrc[cIdx + 1];
                            hDst[cIdx + 2] = hSrc[cIdx + 2];
                            hDst[cIdx + 3] = hSrc[cIdx + 3];
                        }
                    }
                });
            }
        }

        // --- High Pass (0.5 + orig - gaussianBlur(orig, r)) ---
        public static unsafe void ApplyHighPass(byte[] src, byte[] dst, int w, int h, float radius, byte[] mask, int maskW, int maskH)
        {
            byte[] blurred = ArrayPool<byte>.Shared.Rent(src.Length);

            try
            {
                ApplyGaussianBlur(src, blurred, w, h, radius, null, 0, 0);

                fixed (byte* pSrc = src, pDst = dst, pBlur = blurred)
                {
                    Half* hSrc = (Half*)pSrc;
                    Half* hDst = (Half*)pDst;
                    Half* hBlur = (Half*)pBlur;

                    Parallel.For(0, h, y =>
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int idx = (y * w + x) * 4;
                            float oR = (float)hSrc[idx];
                            float oG = (float)hSrc[idx + 1];
                            float oB = (float)hSrc[idx + 2];
                            float oA = (float)hSrc[idx + 3];

                            float hpR = 0.5f + (oR - (float)hBlur[idx]);
                            float hpG = 0.5f + (oG - (float)hBlur[idx + 1]);
                            float hpB = 0.5f + (oB - (float)hBlur[idx + 2]);

                            float m = SampleMask(mask, maskW, maskH, x, y, w, h);
                            hDst[idx] = (Half)Mathf.Lerp(oR, Math.Clamp(hpR, 0f, 1f), m);
                            hDst[idx + 1] = (Half)Mathf.Lerp(oG, Math.Clamp(hpG, 0f, 1f), m);
                            hDst[idx + 2] = (Half)Mathf.Lerp(oB, Math.Clamp(hpB, 0f, 1f), m);
                            hDst[idx + 3] = (Half)oA;
                        }
                    });
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(blurred);
            }
        }
    }
}
