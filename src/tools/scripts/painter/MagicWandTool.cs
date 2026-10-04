using Godot;
using System;
using System.Collections.Generic;

namespace DeadlockPlayground.Painter
{
    public enum MagicWandCombineMode
    {
        Replace,
        Add,
        Subtract
    }

    public partial class MagicWandTool : RefCounted
    {
        [Signal] public delegate void MaskUpdatedEventHandler(bool hasActiveMask);
        [Signal] public delegate void ColorSampledEventHandler(Color color);

        private float _tolerance = 0.15f;
        public float Tolerance
        {
            get => _tolerance;
            set => _tolerance = Mathf.Clamp(value, 0.005f, 1.0f);
        }

        /// <summary>
        /// Perceptual quadratic threshold mapping giving fine control in the 0.05 - 0.25 range without mesh flooding.
        /// </summary>
        public float EffectiveTolerance => MathF.Pow(_tolerance, 2.2f) * 0.5f;

        private readonly SelectionMask _selectionMask;
        public SelectionMask SelectionMask => _selectionMask;

        public Color TargetColor { get; private set; } = Colors.White;
        public bool HasSelection => _selectionMask?.HasSelection ?? false;
        public bool HasActiveSelection => (_selectionMask?.HasSelection ?? false) && (_selectionMask?.UseSelectionMask ?? false);

        public bool UseSelectionMask
        {
            get => _selectionMask?.UseSelectionMask ?? false;
            set
            {
                if (_selectionMask != null && _selectionMask.UseSelectionMask != value)
                {
                    _selectionMask.UseSelectionMask = value;
                    EmitSignal(SignalName.MaskUpdated, HasActiveSelection);
                }
            }
        }
        public bool Contiguous { get; set; } = true;
        public bool IsolateSubmesh { get; set; } = false;
        public bool AntiAliasing { get; set; } = true;

        public Rid SelectionMaskRid => _selectionMask?.MaskTextureRid ?? new Rid();
        public Texture2Drd MaskTextureResource => _selectionMask?.MaskTextureResource;

        public int CurrentAtlasSize => _selectionMask?.CanvasSize ?? 2048;

        // CPU-side active selection mask buffer (1 byte per texel in atlas: 0 = unselected, 255 = selected)
        public byte[] ActiveAtlasMask => _selectionMask?.Buffer;

        public class MagicWandSeedPoint
        {
            public Vector2 HitUV;
            public Image SubmeshImage;
            public Rect2 SubmeshRect;
            public Color TargetColor;
            public MagicWandCombineMode CombineMode;
            public bool Contiguous;
            public bool IsolateSubmesh;
            public bool AntiAliasing;
        }

        private readonly List<MagicWandSeedPoint> _seedPoints = new();
        public IReadOnlyList<MagicWandSeedPoint> SeedPoints => _seedPoints;

        public MagicWandTool(SelectionMask selectionMask = null)
        {
            _selectionMask = selectionMask ?? new SelectionMask(2048);
            _selectionMask.MaskUpdated += (hasMask) => EmitSignal(SignalName.MaskUpdated, hasMask);
        }

        public bool IsPixelSelected(int atlasX, int atlasY)
        {
            return _selectionMask?.IsPixelSelected(atlasX, atlasY) ?? true;
        }

        public float GetPixelMaskValue(int atlasX, int atlasY)
        {
            return _selectionMask?.GetPixelMaskValue(atlasX, atlasY) ?? 1.0f;
        }

        public bool IsUvSelected(Vector2 uv)
        {
            if (_selectionMask == null || !_selectionMask.HasSelection || _selectionMask.CanvasSize <= 0) return true;
            int x = Mathf.Clamp((int)(uv.X * _selectionMask.CanvasSize), 0, _selectionMask.CanvasSize - 1);
            int y = Mathf.Clamp((int)(uv.Y * _selectionMask.CanvasSize), 0, _selectionMask.CanvasSize - 1);
            return IsPixelSelected(x, y);
        }

        public float GetUvMaskValue(Vector2 uv)
        {
            if (_selectionMask == null || !_selectionMask.HasSelection || _selectionMask.CanvasSize <= 0) return 1.0f;
            int x = Mathf.Clamp((int)(uv.X * _selectionMask.CanvasSize), 0, _selectionMask.CanvasSize - 1);
            int y = Mathf.Clamp((int)(uv.Y * _selectionMask.CanvasSize), 0, _selectionMask.CanvasSize - 1);
            return GetPixelMaskValue(x, y);
        }

        private void EnsureMaskTexture(RenderingDevice rd, int atlasSize)
        {
            _selectionMask.EnsureMaskTexture(rd, atlasSize);
        }

        public static float GetColorDistance(Color c1, Color c2)
        {
            float dr = c1.R - c2.R;
            float dg = c1.G - c2.G;
            float db = c1.B - c2.B;
            // Perceptual luma-weighted color distance: sqrt(0.299 * dr^2 + 0.587 * dg^2 + 0.114 * db^2)
            float deltaE = MathF.Sqrt(0.299f * dr * dr + 0.587f * dg * dg + 0.114f * db * db);

            // Special handling for white / near-white outlines:
            // When target or candidate is white/light grey, anti-aliased perimeter pixels
            // blend with the dark background mesh, reducing luminance while remaining neutral.
            bool c1IsNearWhite = c1.R > 0.70f && c1.G > 0.70f && c1.B > 0.70f;
            bool c2IsNearWhite = c2.R > 0.70f && c2.G > 0.70f && c2.B > 0.70f;
            if (c1IsNearWhite || c2IsNearWhite)
            {
                Color whiteRef = c2IsNearWhite ? c2 : c1;
                Color cand = c2IsNearWhite ? c1 : c2;

                float maxVal = Mathf.Max(cand.R, Mathf.Max(cand.G, cand.B));
                float minVal = Mathf.Min(cand.R, Mathf.Min(cand.G, cand.B));
                float chroma = maxVal - minVal;

                // Neutral/low-chroma candidate (white/grey outline)
                if (chroma < 0.25f && maxVal > 0.30f)
                {
                    float lumDiff = Mathf.Abs(whiteRef.R - maxVal);
                    float outlineDist = lumDiff * 0.55f + chroma * 0.45f;
                    return Mathf.Min(deltaE, outlineDist);
                }
            }

            return deltaE;
        }

        public static bool IsColorMatch(Color c1, Color c2, float tolerance)
        {
            return GetColorDistance(c1, c2) <= tolerance;
        }

        public bool GenerateMask(
            Color sampledColor,
            Vector2 hitUV,
            Image submeshImg,
            Rect2 submeshRect,
            Rid baseTextureRid,
            int atlasSize,
            MagicWandCombineMode combineMode = MagicWandCombineMode.Replace)
        {
            var rd = RenderingServer.GetRenderingDevice();
            if (rd == null)
            {
                GD.PrintErr("[MagicWandTool] RenderingDevice is null.");
                return false;
            }

            if (atlasSize <= 0) atlasSize = 2048;
            EnsureMaskTexture(rd, atlasSize);
            if (!_selectionMask.MaskTextureRid.IsValid)
            {
                GD.PrintErr("[MagicWandTool] Mask texture is not valid.");
                return false;
            }

            _selectionMask.EnsureBuffer(atlasSize);

            if (combineMode == MagicWandCombineMode.Replace)
            {
                _seedPoints.Clear();
            }

            if (submeshImg != null)
            {
                int imgW = submeshImg.GetWidth();
                int imgH = submeshImg.GetHeight();
                int seedX = Mathf.Clamp((int)(hitUV.X * imgW), 0, imgW - 1);
                int seedY = Mathf.Clamp((int)(hitUV.Y * imgH), 0, imgH - 1);
                Color seedColor = submeshImg.GetPixel(seedX, seedY);
                TargetColor = seedColor;

                _seedPoints.Add(new MagicWandSeedPoint
                {
                    HitUV = hitUV,
                    SubmeshImage = submeshImg,
                    SubmeshRect = submeshRect,
                    TargetColor = seedColor,
                    CombineMode = combineMode,
                    Contiguous = Contiguous,
                    IsolateSubmesh = IsolateSubmesh,
                    AntiAliasing = AntiAliasing
                });
            }

            RebuildMaskFromSeedPoints(rd, atlasSize);

            UseSelectionMask = true;

            EmitSignal(SignalName.ColorSampled, TargetColor);
            EmitSignal(SignalName.MaskUpdated, true);

            GD.Print($"[MagicWandTool] Generated selection mask: Color=#{TargetColor.ToHtml(false)} Mode={combineMode} Contiguous={Contiguous} Tol={_tolerance:F2} (Eff={EffectiveTolerance:F4}) Seeds={_seedPoints.Count}");
            return true;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static float GetByteColorDistance(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
        {
            float dr = (r1 - r2) * (1.0f / 255.0f);
            float dg = (g1 - g2) * (1.0f / 255.0f);
            float db = (b1 - b2) * (1.0f / 255.0f);
            // Perceptual luma-weighted color distance: sqrt(0.299 * dr^2 + 0.587 * dg^2 + 0.114 * db^2)
            float deltaE = MathF.Sqrt(0.299f * dr * dr + 0.587f * dg * dg + 0.114f * db * db);

            bool c1IsNearWhite = r1 > 178 && g1 > 178 && b1 > 178;
            bool c2IsNearWhite = r2 > 178 && g2 > 178 && b2 > 178;
            if (c1IsNearWhite || c2IsNearWhite)
            {
                byte whiteR = c2IsNearWhite ? r2 : r1;
                byte candR = c2IsNearWhite ? r1 : r2;
                byte candG = c2IsNearWhite ? g1 : g2;
                byte candB = c2IsNearWhite ? b1 : b2;

                byte maxValB = Math.Max(candR, Math.Max(candG, candB));
                byte minValB = Math.Min(candR, Math.Min(candG, candB));
                float chroma = (maxValB - minValB) * (1.0f / 255.0f);

                if (chroma < 0.25f && maxValB > 76)
                {
                    float lumDiff = MathF.Abs(whiteR - maxValB) * (1.0f / 255.0f);
                    float outlineDist = lumDiff * 0.55f + chroma * 0.45f;
                    return Math.Min(deltaE, outlineDist);
                }
            }

            return deltaE;
        }

        private static readonly Half[] _halfLut = PrecomputeHalfLut();

        private static Half[] PrecomputeHalfLut()
        {
            var lut = new Half[256];
            for (int i = 0; i < 256; i++)
            {
                lut[i] = (Half)(i / 255.0f);
            }
            return lut;
        }

        private void RebuildMaskFromSeedPoints(RenderingDevice rd, int atlasSize)
        {
            _selectionMask.EnsureBuffer(atlasSize);
            byte[] activeMask = _selectionMask.Buffer;
            Array.Clear(activeMask, 0, activeMask.Length);

            foreach (var seed in _seedPoints)
            {
                if (seed.SubmeshImage == null) continue;

                var img = seed.SubmeshImage;
                if (img.GetFormat() != Image.Format.Rgba8)
                {
                    img.Convert(Image.Format.Rgba8);
                }

                int imgW = img.GetWidth();
                int imgH = img.GetHeight();
                int seedX = Math.Clamp((int)(seed.HitUV.X * imgW), 0, imgW - 1);
                int seedY = Math.Clamp((int)(seed.HitUV.Y * imgH), 0, imgH - 1);

                int rectX = Math.Clamp((int)(seed.SubmeshRect.Position.X * atlasSize), 0, atlasSize - 1);
                int rectY = Math.Clamp((int)(seed.SubmeshRect.Position.Y * atlasSize), 0, atlasSize - 1);
                int rectW = Math.Clamp((int)(seed.SubmeshRect.Size.X * atlasSize), 1, atlasSize - rectX);
                int rectH = Math.Clamp((int)(seed.SubmeshRect.Size.Y * atlasSize), 1, atlasSize - rectY);

                byte[] rawImg = img.GetData();
                byte[] submeshMask = new byte[imgW * imgH];

                float tol = EffectiveTolerance;
                float softTol = tol * 1.50f;
                float flowTol = tol * 1.25f;

                int minSelX = imgW, maxSelX = -1, minSelY = imgH, maxSelY = -1;

                int seedOffset = (seedY * imgW + seedX) * 4;
                byte targetR = rawImg[seedOffset];
                byte targetG = rawImg[seedOffset + 1];
                byte targetB = rawImg[seedOffset + 2];
                byte targetA = rawImg[seedOffset + 3];

                if (seed.Contiguous)
                {
                    int[] q = new int[imgW * imgH];
                    int qHead = 0, qTail = 0;

                    int seedIdx = seedY * imgW + seedX;
                    submeshMask[seedIdx] = 255;
                    q[qTail++] = seedIdx;

                    minSelX = seedX; maxSelX = seedX;
                    minSelY = seedY; maxSelY = seedY;

                    while (qHead < qTail)
                    {
                        int curr = q[qHead++];
                        int cx = curr % imgW;
                        int cy = curr / imgW;

                        if (cx < minSelX) minSelX = cx;
                        if (cx > maxSelX) maxSelX = cx;
                        if (cy < minSelY) minSelY = cy;
                        if (cy > maxSelY) maxSelY = cy;

                        // 4 directions
                        if (cx > 0) CheckAndEnqueue(cx - 1, cy);
                        if (cx < imgW - 1) CheckAndEnqueue(cx + 1, cy);
                        if (cy > 0) CheckAndEnqueue(cx, cy - 1);
                        if (cy < imgH - 1) CheckAndEnqueue(cx, cy + 1);

                        void CheckAndEnqueue(int nx, int ny)
                        {
                            int nIdx = ny * imgW + nx;
                            if (submeshMask[nIdx] != 0) return; // visited

                            int nOff = nIdx * 4;
                            byte a = rawImg[nOff + 3];
                            if (targetA > 0 && a == 0) return;

                            float dist = GetByteColorDistance(rawImg[nOff], rawImg[nOff + 1], rawImg[nOff + 2], targetR, targetG, targetB);
                            if (dist <= tol)
                            {
                                submeshMask[nIdx] = 255;
                                q[qTail++] = nIdx;
                            }
                            else if (seed.AntiAliasing && dist <= softTol)
                            {
                                float match = 1.0f - (dist - tol) / Math.Max(0.0001f, softTol - tol);
                                submeshMask[nIdx] = (byte)Math.Clamp((int)(match * 255f), 1, 255);
                                if (dist <= flowTol)
                                {
                                    q[qTail++] = nIdx;
                                }
                            }
                        }
                    }
                }
                else
                {
                    object bbLock = new object();
                    System.Threading.Tasks.Parallel.For(0, imgH, () => (MinX: imgW, MaxX: -1, MinY: imgH, MaxY: -1), (py, state, localBB) =>
                    {
                        int row = py * imgW;
                        int pRow = row * 4;
                        for (int px = 0; px < imgW; px++)
                        {
                            int pOff = pRow + px * 4;
                            byte a = rawImg[pOff + 3];
                            if (targetA > 0 && a == 0) continue;

                            float dist = GetByteColorDistance(rawImg[pOff], rawImg[pOff + 1], rawImg[pOff + 2], targetR, targetG, targetB);
                            if (dist <= tol)
                            {
                                submeshMask[row + px] = 255;
                                if (px < localBB.MinX) localBB.MinX = px;
                                if (px > localBB.MaxX) localBB.MaxX = px;
                                if (py < localBB.MinY) localBB.MinY = py;
                                if (py > localBB.MaxY) localBB.MaxY = py;
                            }
                            else if (seed.AntiAliasing && dist <= softTol)
                            {
                                float match = 1.0f - (dist - tol) / Math.Max(0.0001f, softTol - tol);
                                byte bMatch = (byte)Math.Clamp((int)(match * 255f), 0, 255);
                                submeshMask[row + px] = bMatch;
                                if (bMatch >= 128)
                                {
                                    if (px < localBB.MinX) localBB.MinX = px;
                                    if (px > localBB.MaxX) localBB.MaxX = px;
                                    if (py < localBB.MinY) localBB.MinY = py;
                                    if (py > localBB.MaxY) localBB.MaxY = py;
                                }
                            }
                        }
                        return localBB;
                    },
                    localBB =>
                    {
                        if (localBB.MaxX >= 0)
                        {
                            lock (bbLock)
                            {
                                if (localBB.MinX < minSelX) minSelX = localBB.MinX;
                                if (localBB.MaxX > maxSelX) maxSelX = localBB.MaxX;
                                if (localBB.MinY < minSelY) minSelY = localBB.MinY;
                                if (localBB.MaxY > maxSelY) maxSelY = localBB.MaxY;
                            }
                        }
                    });
                }

                // 1-pixel morphological expansion (grow) to bridge bilinear texture seams
                // + 1.0–1.5 px feathering filter along perimeter for smooth anti-aliased edge blending
                if (seed.AntiAliasing && maxSelX >= 0)
                {
                    int pad = 5;
                    int dMinX = Math.Max(0, minSelX - pad);
                    int dMaxX = Math.Min(imgW - 1, maxSelX + pad);
                    int dMinY = Math.Max(0, minSelY - pad);
                    int dMaxY = Math.Min(imgH - 1, maxSelY + pad);

                    // Step 1: 1-pixel dilation / expansion
                    byte[] dilated = new byte[imgW * imgH];
                    Array.Copy(submeshMask, dilated, submeshMask.Length);

                    for (int py = dMinY; py <= dMaxY; py++)
                    {
                        int row = py * imgW;
                        for (int px = dMinX; px <= dMaxX; px++)
                        {
                            int idx = row + px;
                            if (submeshMask[idx] >= 128) continue;

                            bool hasNeighbor = (px > 0 && submeshMask[idx - 1] >= 128) ||
                                               (px < imgW - 1 && submeshMask[idx + 1] >= 128) ||
                                               (py > 0 && submeshMask[idx - imgW] >= 128) ||
                                               (py < imgH - 1 && submeshMask[idx + imgW] >= 128);

                            if (hasNeighbor)
                            {
                                dilated[idx] = 255;
                            }
                        }
                    }

                    // Step 2: Separable 3-tap binomial / box-blur feathering pass [1, 2, 1]/4 (~1.2 px blur radius)
                    int bW = dMaxX - dMinX + 1;
                    int bH = dMaxY - dMinY + 1;
                    float[] hBlur = new float[bW * bH];

                    for (int py = dMinY; py <= dMaxY; py++)
                    {
                        int row = py * imgW;
                        int bRow = (py - dMinY) * bW;
                        for (int px = dMinX; px <= dMaxX; px++)
                        {
                            float left = (px > 0) ? dilated[row + px - 1] : dilated[row + px];
                            float center = dilated[row + px];
                            float right = (px < imgW - 1) ? dilated[row + px + 1] : dilated[row + px];
                            hBlur[bRow + (px - dMinX)] = (left + 2.0f * center + right) * 0.25f;
                        }
                    }

                    // Vertical pass writing back into submeshMask
                    for (int py = dMinY; py <= dMaxY; py++)
                    {
                        int row = py * imgW;
                        int bRow = (py - dMinY) * bW;
                        int bPrevRow = Math.Max(0, py - dMinY - 1) * bW;
                        int bNextRow = Math.Min(bH - 1, py - dMinY + 1) * bW;

                        for (int px = dMinX; px <= dMaxX; px++)
                        {
                            int xOff = px - dMinX;
                            float top = hBlur[bPrevRow + xOff];
                            float mid = hBlur[bRow + xOff];
                            float bot = hBlur[bNextRow + xOff];
                            float val = (top + 2.0f * mid + bot) * 0.25f;
                            submeshMask[row + px] = (byte)Math.Clamp((int)MathF.Round(val), 0, 255);
                        }
                    }
                }

                // Blit into active atlas mask
                var combineMode = seed.CombineMode;
                bool isAA = seed.AntiAliasing;
                System.Threading.Tasks.Parallel.For(0, rectH, y =>
                {
                    int atlasRow = (rectY + y) * atlasSize;

                    if (!isAA)
                    {
                        // Strict binary nearest-neighbor blit for pixel-perfect hard edges
                        int srcY = Math.Clamp((int)(((float)y + 0.5f) / rectH * imgH), 0, imgH - 1);
                        int srcRow = srcY * imgW;
                        for (int x = 0; x < rectW; x++)
                        {
                            int srcX = Math.Clamp((int)(((float)x + 0.5f) / rectW * imgW), 0, imgW - 1);
                            byte byteVal = submeshMask[srcRow + srcX] >= 128 ? (byte)255 : (byte)0;
                            int atlasIdx = atlasRow + (rectX + x);
                            if (combineMode == MagicWandCombineMode.Subtract)
                                activeMask[atlasIdx] = (byte)Math.Max(0, activeMask[atlasIdx] - byteVal);
                            else if (combineMode == MagicWandCombineMode.Add)
                                activeMask[atlasIdx] = (byte)Math.Min(255, activeMask[atlasIdx] + byteVal);
                            else
                                activeMask[atlasIdx] = byteVal;
                        }
                    }
                    else
                    {
                        // Parallel bilinear blit into active atlas mask for smooth antialiased boundaries
                        float v = ((float)y + 0.5f) / rectH * imgH - 0.5f;
                        int y0 = Math.Clamp((int)MathF.Floor(v), 0, imgH - 1);
                        int y1 = Math.Clamp(y0 + 1, 0, imgH - 1);
                        float fv = Math.Clamp(v - y0, 0.0f, 1.0f);

                        for (int x = 0; x < rectW; x++)
                        {
                            float u = ((float)x + 0.5f) / rectW * imgW - 0.5f;
                            int x0 = Math.Clamp((int)MathF.Floor(u), 0, imgW - 1);
                            int x1 = Math.Clamp(x0 + 1, 0, imgH - 1);
                            float fu = Math.Clamp(u - x0, 0.0f, 1.0f);

                            byte m00 = submeshMask[y0 * imgW + x0];
                            byte m10 = submeshMask[y0 * imgW + x1];
                            byte m01 = submeshMask[y1 * imgW + x0];
                            byte m11 = submeshMask[y1 * imgW + x1];

                            float top = m00 + (m10 - m00) * fu;
                            float bottom = m01 + (m11 - m01) * fu;
                            byte byteVal = (byte)Math.Clamp((int)MathF.Round(top + (bottom - top) * fv), 0, 255);

                            int atlasIdx = atlasRow + (rectX + x);
                            if (combineMode == MagicWandCombineMode.Subtract)
                                activeMask[atlasIdx] = (byte)Math.Max(0, activeMask[atlasIdx] - byteVal);
                            else if (combineMode == MagicWandCombineMode.Add)
                                activeMask[atlasIdx] = (byte)Math.Min(255, activeMask[atlasIdx] + byteVal);
                            else
                                activeMask[atlasIdx] = byteVal;
                        }
                    }
                });
            }

            _selectionMask.UpdateSelectionStateAndUpload();
        }

        private void UploadMaskToGpu(RenderingDevice rd, int atlasSize)
        {
            _selectionMask.UploadToGpu(rd);
        }

        public void InvertMask()
        {
            _selectionMask.Invert();
        }

        public void RecomputeWithTolerance(float newTolerance)
        {
            Tolerance = newTolerance;
            if (_seedPoints.Count > 0)
            {
                var rd = RenderingServer.GetRenderingDevice();
                if (rd != null)
                {
                    int size = CurrentAtlasSize > 0 ? CurrentAtlasSize : 2048;
                    RebuildMaskFromSeedPoints(rd, size);
                    EmitSignal(SignalName.MaskUpdated, true);
                }
            }
        }

        public void ClearMask()
        {
            _seedPoints.Clear();
            _selectionMask.Clear();
            GD.Print("[MagicWandTool] Selection mask cleared.");
        }

        public void Cleanup()
        {
            _seedPoints.Clear();
            _selectionMask.Cleanup();
        }
    }
}
