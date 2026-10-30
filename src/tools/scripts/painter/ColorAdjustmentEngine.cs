using Godot;
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace DeadlockPlayground.Painter
{
    public enum AdjustmentType
    {
        BrightnessContrast = 0,
        HueSaturation = 1,
        ColorBalance = 2,
        Exposure = 3,
        Invert = 4,
        Desaturate = 5,
        Levels = 6,
        Posterize = 7,
        Threshold = 8
    }

    public struct ColorAdjustmentParams
    {
        public AdjustmentType Type;

        // Brightness / Contrast (-100 to +100)
        public float Brightness;
        public float Contrast;

        // Hue / Saturation (-180 to +180, -100 to +100, -100 to +100)
        public float Hue;
        public float Saturation;
        public float Lightness;
        public bool Colorize;

        // Color Balance (-100 to +100)
        public float CyanRed;
        public float MagentaGreen;
        public float YellowBlue;
        public bool PreserveLuminosity;

        // Exposure (-3.0 to +3.0, -0.5 to +0.5, 0.2 to 3.0)
        public float Exposure;
        public float Offset;
        public float Gamma;

        // Levels (inBlack: 0..253, gamma: 0.1..5.0, inWhite: 2..255, outBlack: 0..255, outWhite: 0..255)
        public float InBlack;
        public float LevelsGamma;
        public float InWhite;
        public float OutBlack;
        public float OutWhite;

        // Posterize / Threshold
        public int PosterizeLevels;
        public float ThresholdValue;

        public static ColorAdjustmentParams Default(AdjustmentType type)
        {
            return new ColorAdjustmentParams
            {
                Type = type,
                Brightness = 0.0f,
                Contrast = 0.0f,
                Hue = 0.0f,
                Saturation = 0.0f,
                Lightness = 0.0f,
                Colorize = false,
                CyanRed = 0.0f,
                MagentaGreen = 0.0f,
                YellowBlue = 0.0f,
                PreserveLuminosity = true,
                Exposure = 0.0f,
                Offset = 0.0f,
                Gamma = 1.0f,
                InBlack = 0.0f,
                LevelsGamma = 1.0f,
                InWhite = 255.0f,
                OutBlack = 0.0f,
                OutWhite = 255.0f,
                PosterizeLevels = 4,
                ThresholdValue = 128.0f
            };
        }
    }

    public static class ColorAdjustmentEngine
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float LinearToSrgb(float c)
        {
            if (c <= 0.0031308f)
                return MathF.Max(0.0f, c * 12.92f);
            return 1.055f * MathF.Pow(MathF.Max(0.0f, c), 1.0f / 2.4f) - 0.055f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float SrgbToLinear(float c)
        {
            if (c <= 0.04045f)
                return MathF.Max(0.0f, c / 12.92f);
            return MathF.Pow(MathF.Max(0.0f, (c + 0.055f) / 1.055f), 2.4f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RgbToHsl(float r, float g, float b, out float h, out float s, out float l)
        {
            float max = MathF.Max(r, MathF.Max(g, b));
            float min = MathF.Min(r, MathF.Min(g, b));
            float delta = max - min;
            l = (max + min) * 0.5f;

            if (delta <= 0.00001f)
            {
                h = 0.0f;
                s = 0.0f;
                return;
            }

            s = (l > 0.5f) ? (delta / (2.0f - max - min)) : (delta / (max + min));

            if (max == r)
            {
                h = ((g - b) / delta) + (g < b ? 6.0f : 0.0f);
            }
            else if (max == g)
            {
                h = ((b - r) / delta) + 2.0f;
            }
            else
            {
                h = ((r - g) / delta) + 4.0f;
            }
            h *= 60.0f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void HslToRgb(float h, float s, float l, out float r, out float g, out float b)
        {
            if (s <= 0.00001f)
            {
                r = g = b = l;
                return;
            }

            float q = (l < 0.5f) ? (l * (1.0f + s)) : (l + s - l * s);
            float p = 2.0f * l - q;
            float hk = h / 360.0f;

            r = HueToRgb(p, q, hk + 1.0f / 3.0f);
            g = HueToRgb(p, q, hk);
            b = HueToRgb(p, q, hk - 1.0f / 3.0f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float HueToRgb(float p, float q, float t)
        {
            if (t < 0.0f) t += 1.0f;
            if (t > 1.0f) t -= 1.0f;
            if (t < 1.0f / 6.0f) return p + (q - p) * 6.0f * t;
            if (t < 1.0f / 2.0f) return q;
            if (t < 2.0f / 3.0f) return p + (q - p) * (2.0f / 3.0f - t) * 6.0f;
            return p;
        }

        public static void ApplyAdjustment(
            byte[] sourceBuffer,
            byte[] targetBuffer,
            int width,
            int height,
            ColorAdjustmentParams parameters,
            byte[] selectionMask = null,
            int maskWidth = 0,
            int maskHeight = 0,
            bool isSamplingUnderlying = false)
        {
            if (sourceBuffer == null || targetBuffer == null || width <= 0 || height <= 0) return;
            int totalBytes = width * height * 8;
            if (sourceBuffer.Length < totalBytes || targetBuffer.Length < totalBytes) return;

            bool hasMask = selectionMask != null && maskWidth > 0 && maskHeight > 0 &&
                           selectionMask.Length >= (maskWidth * maskHeight);
            bool maskSameSize = hasMask && maskWidth == width && maskHeight == height;

            // Precompute adjustment constants
            float bBright = parameters.Brightness / 100.0f;
            float cContrast = parameters.Contrast / 100.0f;
            float contrastFactor = (cContrast >= 0.0f)
                ? ((1.0f + cContrast) / MathF.Max(0.001f, 1.0f - cContrast * 0.99f))
                : (1.0f + cContrast);

            float deltaHue = parameters.Hue;
            float deltaSat = parameters.Saturation / 100.0f;
            float deltaLight = parameters.Lightness / 100.0f;
            bool colorize = parameters.Colorize;
            float colorizeSat = Math.Clamp((parameters.Saturation + 100.0f) / 200.0f, 0.0f, 1.0f);

            float cr = parameters.CyanRed / 100.0f;
            float mg = parameters.MagentaGreen / 100.0f;
            float yb = parameters.YellowBlue / 100.0f;
            bool preserveLum = parameters.PreserveLuminosity;

            float expMult = MathF.Pow(2.0f, parameters.Exposure);
            float expOffset = parameters.Offset;
            float invGamma = (parameters.Gamma > 0.001f) ? (1.0f / parameters.Gamma) : 1.0f;

            // Levels precomputation
            float inB = parameters.InBlack / 255.0f;
            float inW = parameters.InWhite / 255.0f;
            float outB = parameters.OutBlack / 255.0f;
            float outW = parameters.OutWhite / 255.0f;
            float invLevelsRange = 1.0f / MathF.Max(0.001f, inW - inB);
            float invLevelsGamma = 1.0f / MathF.Max(0.01f, parameters.LevelsGamma);

            // Posterize / Threshold precomputation
            int postLevels = Math.Clamp(parameters.PosterizeLevels, 2, 32);
            float postStep = postLevels - 1;
            float threshVal = Math.Clamp(parameters.ThresholdValue / 255.0f, 0.0f, 1.0f);

            AdjustmentType type = parameters.Type;

            unsafe
            {
                fixed (byte* pSrc = sourceBuffer)
                fixed (byte* pDst = targetBuffer)
                fixed (byte* pMask = selectionMask)
                {
                    nint srcPtr = (nint)pSrc;
                    nint dstPtr = (nint)pDst;
                    nint maskPtr = (nint)pMask;

                    Parallel.For(0, height, y =>
                    {
                        Half* srcHalf = (Half*)srcPtr;
                        Half* dstHalf = (Half*)dstPtr;
                        ulong* srcUlong = (ulong*)srcPtr;
                        ulong* dstUlong = (ulong*)dstPtr;
                        byte* maskBytes = (byte*)maskPtr;

                        int rowOffset = y * width;
                        int maskRowOffset = (hasMask && !maskSameSize)
                            ? (int)(((long)y * maskHeight) / height) * maskWidth
                            : 0;

                        for (int x = 0; x < width; x++)
                        {
                            int idx = rowOffset + x;

                            float maskWeight = 1.0f;
                            if (hasMask)
                            {
                                byte maskByte;
                                if (maskSameSize)
                                {
                                    maskByte = maskBytes[idx];
                                }
                                else
                                {
                                    int mx = (int)(((long)x * maskWidth) / width);
                                    maskByte = maskBytes[maskRowOffset + mx];
                                }

                                if (maskByte == 0)
                                {
                                    // Completely outside selection mask: retain source pixel as-is
                                    dstUlong[idx] = srcUlong[idx];
                                    continue;
                                }
                                maskWeight = maskByte / 255.0f;
                            }

                            Half aHalf = srcHalf[idx * 4 + 3];
                            float aFloat = (float)aHalf;

                            // If not masked and completely transparent, no need to transform
                            if (!hasMask && aFloat <= 0.0f)
                            {
                                dstUlong[idx] = srcUlong[idx];
                                continue;
                            }

                            // Read source linear RGB
                            float linR = (float)srcHalf[idx * 4 + 0];
                            float linG = (float)srcHalf[idx * 4 + 1];
                            float linB = (float)srcHalf[idx * 4 + 2];

                            // Convert to perceptual sRGB for adjustments
                            float sR = LinearToSrgb(linR);
                            float sG = LinearToSrgb(linG);
                            float sB = LinearToSrgb(linB);

                            float adjR = sR;
                            float adjG = sG;
                            float adjB = sB;

                            switch (type)
                            {
                                case AdjustmentType.Invert:
                                    adjR = Math.Clamp(1.0f - sR, 0.0f, 1.0f);
                                    adjG = Math.Clamp(1.0f - sG, 0.0f, 1.0f);
                                    adjB = Math.Clamp(1.0f - sB, 0.0f, 1.0f);
                                    break;

                                case AdjustmentType.Desaturate:
                                    float lumDesat = Math.Clamp(0.2126f * sR + 0.7152f * sG + 0.0722f * sB, 0.0f, 1.0f);
                                    adjR = lumDesat;
                                    adjG = lumDesat;
                                    adjB = lumDesat;
                                    break;

                                case AdjustmentType.BrightnessContrast:
                                    float rB = Math.Clamp(sR + bBright, 0.0f, 1.0f);
                                    float gB = Math.Clamp(sG + bBright, 0.0f, 1.0f);
                                    float bB = Math.Clamp(sB + bBright, 0.0f, 1.0f);
                                    adjR = Math.Clamp((rB - 0.5f) * contrastFactor + 0.5f, 0.0f, 1.0f);
                                    adjG = Math.Clamp((gB - 0.5f) * contrastFactor + 0.5f, 0.0f, 1.0f);
                                    adjB = Math.Clamp((bB - 0.5f) * contrastFactor + 0.5f, 0.0f, 1.0f);
                                    break;

                                case AdjustmentType.HueSaturation:
                                    RgbToHsl(sR, sG, sB, out float h, out float s, out float l);
                                    if (!colorize)
                                    {
                                        h = (h + deltaHue) % 360.0f;
                                        if (h < 0.0f) h += 360.0f;

                                        if (deltaSat >= 0.0f) s += (1.0f - s) * deltaSat;
                                        else s += s * deltaSat;
                                        s = Math.Clamp(s, 0.0f, 1.0f);

                                        if (deltaLight >= 0.0f) l += (1.0f - l) * deltaLight;
                                        else l += l * deltaLight;
                                        l = Math.Clamp(l, 0.0f, 1.0f);
                                    }
                                    else
                                    {
                                        h = deltaHue % 360.0f;
                                        if (h < 0.0f) h += 360.0f;
                                        s = colorizeSat;
                                        if (deltaLight >= 0.0f) l += (1.0f - l) * deltaLight;
                                        else l += l * deltaLight;
                                        l = Math.Clamp(l, 0.0f, 1.0f);
                                    }
                                    HslToRgb(h, s, l, out adjR, out adjG, out adjB);
                                    break;

                                case AdjustmentType.ColorBalance:
                                    float origLum = 0.2126f * sR + 0.7152f * sG + 0.0722f * sB;
                                    float rBal = Math.Clamp(sR + cr * 0.35f, 0.0f, 1.0f);
                                    float gBal = Math.Clamp(sG + mg * 0.35f, 0.0f, 1.0f);
                                    float bBal = Math.Clamp(sB + yb * 0.35f, 0.0f, 1.0f);

                                    if (preserveLum)
                                    {
                                        float newLum = 0.2126f * rBal + 0.7152f * gBal + 0.0722f * bBal;
                                        if (newLum > 0.001f)
                                        {
                                            float scale = origLum / newLum;
                                            rBal = Math.Clamp(rBal * scale, 0.0f, 1.0f);
                                            gBal = Math.Clamp(gBal * scale, 0.0f, 1.0f);
                                            bBal = Math.Clamp(bBal * scale, 0.0f, 1.0f);
                                        }
                                    }
                                    adjR = rBal;
                                    adjG = gBal;
                                    adjB = bBal;
                                    break;

                                case AdjustmentType.Exposure:
                                    float rExp = MathF.Max(0.0f, sR * expMult + expOffset);
                                    float gExp = MathF.Max(0.0f, sG * expMult + expOffset);
                                    float bExp = MathF.Max(0.0f, sB * expMult + expOffset);
                                    adjR = Math.Clamp(MathF.Pow(rExp, invGamma), 0.0f, 1.0f);
                                    adjG = Math.Clamp(MathF.Pow(gExp, invGamma), 0.0f, 1.0f);
                                    adjB = Math.Clamp(MathF.Pow(bExp, invGamma), 0.0f, 1.0f);
                                    break;

                                case AdjustmentType.Levels:
                                    float rLev = Math.Clamp((sR - inB) * invLevelsRange, 0.0f, 1.0f);
                                    float gLev = Math.Clamp((sG - inB) * invLevelsRange, 0.0f, 1.0f);
                                    float bLev = Math.Clamp((sB - inB) * invLevelsRange, 0.0f, 1.0f);
                                    rLev = MathF.Pow(rLev, invLevelsGamma);
                                    gLev = MathF.Pow(gLev, invLevelsGamma);
                                    bLev = MathF.Pow(bLev, invLevelsGamma);
                                    adjR = Math.Clamp(outB + rLev * (outW - outB), 0.0f, 1.0f);
                                    adjG = Math.Clamp(outB + gLev * (outW - outB), 0.0f, 1.0f);
                                    adjB = Math.Clamp(outB + bLev * (outW - outB), 0.0f, 1.0f);
                                    break;

                                case AdjustmentType.Posterize:
                                    adjR = MathF.Round(sR * postStep) / postStep;
                                    adjG = MathF.Round(sG * postStep) / postStep;
                                    adjB = MathF.Round(sB * postStep) / postStep;
                                    break;

                                case AdjustmentType.Threshold:
                                    float lumThresh = 0.299f * sR + 0.587f * sG + 0.114f * sB;
                                    float binVal = (lumThresh >= threshVal) ? 1.0f : 0.0f;
                                    adjR = binVal;
                                    adjG = binVal;
                                    adjB = binVal;
                                    break;
                            }

                            // Convert adjusted sRGB back to linear space
                            float finalLinR = SrgbToLinear(adjR);
                            float finalLinG = SrgbToLinear(adjG);
                            float finalLinB = SrgbToLinear(adjB);

                            if (isSamplingUnderlying)
                            {
                                if (hasMask)
                                {
                                    if (maskWeight > 0.005f)
                                    {
                                        dstHalf[idx * 4 + 0] = (Half)finalLinR;
                                        dstHalf[idx * 4 + 1] = (Half)finalLinG;
                                        dstHalf[idx * 4 + 2] = (Half)finalLinB;
                                        dstHalf[idx * 4 + 3] = (Half)maskWeight;
                                    }
                                    else
                                    {
                                        dstHalf[idx * 4 + 0] = (Half)0f;
                                        dstHalf[idx * 4 + 1] = (Half)0f;
                                        dstHalf[idx * 4 + 2] = (Half)0f;
                                        dstHalf[idx * 4 + 3] = (Half)0f;
                                    }
                                }
                                else
                                {
                                    dstHalf[idx * 4 + 0] = (Half)finalLinR;
                                    dstHalf[idx * 4 + 1] = (Half)finalLinG;
                                    dstHalf[idx * 4 + 2] = (Half)finalLinB;
                                    dstHalf[idx * 4 + 3] = (Half)1.0f;
                                }
                            }
                            else
                            {
                                // Apply selection mask weighting if active
                                if (hasMask && maskWeight < 0.999f)
                                {
                                    finalLinR = linR + (finalLinR - linR) * maskWeight;
                                    finalLinG = linG + (finalLinG - linG) * maskWeight;
                                    finalLinB = linB + (finalLinB - linB) * maskWeight;
                                }

                                dstHalf[idx * 4 + 0] = (Half)finalLinR;
                                dstHalf[idx * 4 + 1] = (Half)finalLinG;
                                dstHalf[idx * 4 + 2] = (Half)finalLinB;
                                dstHalf[idx * 4 + 3] = aHalf; // Original Alpha ALWAYS preserved!
                            }
                        }
                    });
                }
            }
        }
    }
}
