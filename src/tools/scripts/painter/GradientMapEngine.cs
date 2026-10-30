using Godot;
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace DeadlockPlayground.Painter
{
    public enum GradientMapBlendMode
    {
        Normal = 0,
        Overlay = 1,
        SoftLight = 2,
        Multiply = 3,
        Color = 4,
        Screen = 5
    }

    public struct GradientMapParams
    {
        public bool Reverse;
        public bool Dither;
        public float Opacity; // 0.0f to 1.0f
        public GradientMapBlendMode BlendMode;

        public static GradientMapParams Default()
        {
            return new GradientMapParams
            {
                Reverse = false,
                Dither = true,
                Opacity = 1.0f,
                BlendMode = GradientMapBlendMode.Normal
            };
        }
    }

    public static class GradientMapEngine
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BlendPixels(float bR, float bG, float bB, float gR, float gG, float gB, GradientMapBlendMode mode, out float oR, out float oG, out float oB)
        {
            switch (mode)
            {
                case GradientMapBlendMode.Multiply:
                    oR = bR * gR;
                    oG = bG * gG;
                    oB = bB * gB;
                    break;

                case GradientMapBlendMode.Screen:
                    oR = 1.0f - (1.0f - bR) * (1.0f - gR);
                    oG = 1.0f - (1.0f - bG) * (1.0f - gG);
                    oB = 1.0f - (1.0f - bB) * (1.0f - gB);
                    break;

                case GradientMapBlendMode.Overlay:
                    oR = bR < 0.5f ? 2.0f * bR * gR : 1.0f - 2.0f * (1.0f - bR) * (1.0f - gR);
                    oG = bG < 0.5f ? 2.0f * bG * gG : 1.0f - 2.0f * (1.0f - bG) * (1.0f - gG);
                    oB = bB < 0.5f ? 2.0f * bB * gB : 1.0f - 2.0f * (1.0f - bB) * (1.0f - gB);
                    break;

                case GradientMapBlendMode.SoftLight:
                    float SoftLightChannel(float a, float b)
                    {
                        if (b <= 0.5f)
                            return a - (1.0f - 2.0f * b) * a * (1.0f - a);
                        float d = a <= 0.25f ? ((16.0f * a - 12.0f) * a + 4.0f) * a : MathF.Sqrt(a);
                        return a + (2.0f * b - 1.0f) * (d - a);
                    }
                    oR = SoftLightChannel(Math.Clamp(bR, 0f, 1f), Math.Clamp(gR, 0f, 1f));
                    oG = SoftLightChannel(Math.Clamp(bG, 0f, 1f), Math.Clamp(gG, 0f, 1f));
                    oB = SoftLightChannel(Math.Clamp(bB, 0f, 1f), Math.Clamp(gB, 0f, 1f));
                    break;

                case GradientMapBlendMode.Color:
                    // Preserve base luminance, take hue and saturation from gradient color
                    ColorAdjustmentEngine.RgbToHsl(gR, gG, gB, out float gH, out float gS, out _);
                    float baseLum = Math.Clamp(0.2126f * bR + 0.7152f * bG + 0.0722f * bB, 0f, 1f);
                    ColorAdjustmentEngine.HslToRgb(gH, gS, baseLum, out oR, out oG, out oB);
                    break;

                case GradientMapBlendMode.Normal:
                default:
                    oR = gR;
                    oG = gG;
                    oB = gB;
                    break;
            }
        }

        public static unsafe void ApplyGradientMap(
            byte[] src,
            byte[] dst,
            int w,
            int h,
            GradientDefinition gradient,
            GradientMapParams p,
            byte[] mask = null,
            int maskW = 0,
            int maskH = 0,
            bool isSamplingUnderlying = false)
        {
            if (src == null || dst == null || w <= 0 || h <= 0 || gradient == null) return;

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

                        // Perceptual luminance mapping
                        float lum = Math.Clamp(0.2126f * r + 0.7152f * g + 0.0722f * b, 0.0f, 1.0f);

                        // Sample gradient ramp
                        Color gradCol = gradient.Evaluate(lum, reverse: p.Reverse, dither: p.Dither, x: x, y: y);

                        // Blend mode calculation
                        BlendPixels(r, g, b, gradCol.R, gradCol.G, gradCol.B, p.BlendMode, out float blR, out float blG, out float blB);

                        // Opacity lerp
                        float finalR = Mathf.Lerp(r, blR, p.Opacity);
                        float finalG = Mathf.Lerp(g, blG, p.Opacity);
                        float finalB = Mathf.Lerp(b, blB, p.Opacity);

                        float m = SampleMask(mask, maskW, maskH, x, y, w, h);

                        if (isSamplingUnderlying)
                        {
                            // Writing directly onto an empty layer
                            if (m > 0.005f)
                            {
                                hDst[idx] = (Half)finalR;
                                hDst[idx + 1] = (Half)finalG;
                                hDst[idx + 2] = (Half)finalB;
                                hDst[idx + 3] = (Half)MathF.Max(0.01f, m);
                            }
                            else
                            {
                                hDst[idx] = (Half)0f;
                                hDst[idx + 1] = (Half)0f;
                                hDst[idx + 2] = (Half)0f;
                                hDst[idx + 3] = (Half)0f;
                            }
                        }
                        else
                        {
                            // Standard layer or Base Texture modification
                            hDst[idx] = (Half)Mathf.Lerp(r, finalR, m);
                            hDst[idx + 1] = (Half)Mathf.Lerp(g, finalG, m);
                            hDst[idx + 2] = (Half)Mathf.Lerp(b, finalB, m);
                            hDst[idx + 3] = (Half)a;
                        }
                    }
                });
            }
        }
    }
}
