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
            set => _tolerance = Mathf.Clamp(value, 0.005f, 0.80f);
        }

        public Color TargetColor { get; private set; } = Colors.White;
        public bool HasSelection { get; private set; } = false;
        public bool UseSelectionMask { get; set; } = true;
        public bool Contiguous { get; set; } = true;
        public bool IsolateSubmesh { get; set; } = false;

        private Rid _maskTextureRid = new();
        public Rid SelectionMaskRid => _maskTextureRid;

        private Texture2Drd _maskTextureResource;
        public Texture2Drd MaskTextureResource
        {
            get
            {
                if (_maskTextureResource == null)
                {
                    _maskTextureResource = new Texture2Drd();
                }
                if (_maskTextureRid.IsValid && _maskTextureResource.TextureRdRid != _maskTextureRid)
                {
                    _maskTextureResource.TextureRdRid = _maskTextureRid;
                }
                return _maskTextureResource;
            }
        }

        private int _currentAtlasSize = 0;
        public int CurrentAtlasSize => _currentAtlasSize;

        // CPU-side active selection mask buffer (1 byte per texel in atlas: 0 = unselected, 255 = selected)
        private byte[] _activeAtlasMask;
        public byte[] ActiveAtlasMask => _activeAtlasMask;

        public class MagicWandSeedPoint
        {
            public Vector2 HitUV;
            public Image SubmeshImage;
            public Rect2 SubmeshRect;
            public Color TargetColor;
            public MagicWandCombineMode CombineMode;
            public bool Contiguous;
            public bool IsolateSubmesh;
        }

        private readonly List<MagicWandSeedPoint> _seedPoints = new();
        public IReadOnlyList<MagicWandSeedPoint> SeedPoints => _seedPoints;

        public MagicWandTool()
        {
        }

        public bool HasActiveSelection => HasSelection && UseSelectionMask;

        public bool IsPixelSelected(int atlasX, int atlasY)
        {
            if (!HasSelection || !UseSelectionMask || _activeAtlasMask == null) return true;
            if (atlasX < 0 || atlasY < 0 || atlasX >= _currentAtlasSize || atlasY >= _currentAtlasSize) return false;
            return _activeAtlasMask[atlasY * _currentAtlasSize + atlasX] >= 128;
        }

        public bool IsUvSelected(Vector2 uv)
        {
            if (!HasSelection || !UseSelectionMask || _activeAtlasMask == null || _currentAtlasSize <= 0) return true;
            int x = Mathf.Clamp((int)(uv.X * _currentAtlasSize), 0, _currentAtlasSize - 1);
            int y = Mathf.Clamp((int)(uv.Y * _currentAtlasSize), 0, _currentAtlasSize - 1);
            return IsPixelSelected(x, y);
        }

        private void EnsureMaskTexture(RenderingDevice rd, int atlasSize)
        {
            if (atlasSize <= 0) atlasSize = 2048;

            if (_maskTextureRid.IsValid && rd.TextureIsValid(_maskTextureRid) && _currentAtlasSize == atlasSize)
            {
                return;
            }

            if (_maskTextureRid.IsValid && rd.TextureIsValid(_maskTextureRid))
            {
                rd.FreeRid(_maskTextureRid);
                _maskTextureRid = new Rid();
            }

            _currentAtlasSize = atlasSize;
            _activeAtlasMask = new byte[atlasSize * atlasSize];

            var fmt = new RDTextureFormat
            {
                Width = (uint)atlasSize,
                Height = (uint)atlasSize,
                Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
                TextureType = RenderingDevice.TextureType.Type2D,
                UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
                            RenderingDevice.TextureUsageBits.StorageBit |
                            RenderingDevice.TextureUsageBits.CanUpdateBit |
                            RenderingDevice.TextureUsageBits.CanCopyFromBit
            };

            var view = new RDTextureView();
            byte[] zeroBytes = new byte[atlasSize * atlasSize * 8];
            _maskTextureRid = rd.TextureCreate(fmt, view, new Godot.Collections.Array<byte[]> { zeroBytes });
            if (_maskTextureResource != null)
            {
                _maskTextureResource.TextureRdRid = _maskTextureRid;
            }
            GD.Print($"[MagicWandTool] Created selection mask GPU texture ({atlasSize}x{atlasSize}) RID: {_maskTextureRid.Id}");
        }

        public static float GetColorDistance(Color c1, Color c2)
        {
            float dr = c1.R - c2.R;
            float dg = c1.G - c2.G;
            float db = c1.B - c2.B;
            // Perceptually weighted Euclidean distance (Rec. 709 luminance weights: 0.2126 R, 0.7152 G, 0.0722 B)
            float deltaE = Mathf.Sqrt(0.2126f * dr * dr + 0.7152f * dg * dg + 0.0722f * db * db);

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
            if (!_maskTextureRid.IsValid)
            {
                GD.PrintErr("[MagicWandTool] Mask texture is not valid.");
                return false;
            }

            if (_activeAtlasMask == null || _activeAtlasMask.Length != atlasSize * atlasSize)
            {
                _activeAtlasMask = new byte[atlasSize * atlasSize];
            }

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
                    IsolateSubmesh = IsolateSubmesh
                });
            }

            RebuildMaskFromSeedPoints(rd, atlasSize);

            HasSelection = _seedPoints.Count > 0;
            UseSelectionMask = true;

            EmitSignal(SignalName.ColorSampled, TargetColor);
            EmitSignal(SignalName.MaskUpdated, true);

            GD.Print($"[MagicWandTool] Generated selection mask: Color=#{TargetColor.ToHtml(false)} Mode={combineMode} Contiguous={Contiguous} Tol={_tolerance:F2} Seeds={_seedPoints.Count}");
            return true;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static float GetByteColorDistance(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
        {
            float dr = (r1 - r2) * (1.0f / 255.0f);
            float dg = (g1 - g2) * (1.0f / 255.0f);
            float db = (b1 - b2) * (1.0f / 255.0f);
            float deltaE = MathF.Sqrt(0.2126f * dr * dr + 0.7152f * dg * dg + 0.0722f * db * db);

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
            if (_activeAtlasMask == null || _activeAtlasMask.Length != atlasSize * atlasSize)
            {
                _activeAtlasMask = new byte[atlasSize * atlasSize];
            }
            Array.Clear(_activeAtlasMask, 0, _activeAtlasMask.Length);

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

                float tol = _tolerance;
                float softTol = tol * 1.60f;
                float flowTol = tol * 1.35f;

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
                            else if (dist <= softTol)
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
                            else if (dist <= softTol)
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

                // Border dilation & 3x3 smoothing bounded strictly to active area
                if (maxSelX >= 0)
                {
                    int dMinX = Math.Max(0, minSelX - 4);
                    int dMaxX = Math.Min(imgW - 1, maxSelX + 4);
                    int dMinY = Math.Max(0, minSelY - 4);
                    int dMaxY = Math.Min(imgH - 1, maxSelY + 4);

                    byte[] passMask = (byte[])submeshMask.Clone();
                    float dilationTol = tol * 1.35f;
                    for (int py = dMinY; py <= dMaxY; py++)
                    {
                        int row = py * imgW;
                        for (int px = dMinX; px <= dMaxX; px++)
                        {
                            int idx = row + px;
                            if (passMask[idx] >= 128) continue;

                            bool neighborSelected = (px > 0 && passMask[idx - 1] >= 128) ||
                                                   (px < imgW - 1 && passMask[idx + 1] >= 128) ||
                                                   (py > 0 && passMask[idx - imgW] >= 128) ||
                                                   (py < imgH - 1 && passMask[idx + imgW] >= 128);

                            if (neighborSelected)
                            {
                                int pOff = idx * 4;
                                float dist = GetByteColorDistance(rawImg[pOff], rawImg[pOff + 1], rawImg[pOff + 2], targetR, targetG, targetB);
                                if (dist <= dilationTol)
                                {
                                    float fringe = Math.Clamp(1.0f - (dist - tol) / Math.Max(0.0001f, dilationTol - tol), 0.55f, 0.95f);
                                    submeshMask[idx] = Math.Max(submeshMask[idx], (byte)(fringe * 255f));
                                }
                            }
                        }
                    }
                }

                // Parallel bilinear blit into active atlas mask
                var combineMode = seed.CombineMode;
                byte[] activeMask = _activeAtlasMask;
                System.Threading.Tasks.Parallel.For(0, rectH, y =>
                {
                    float v = ((float)y + 0.5f) / rectH * imgH - 0.5f;
                    int y0 = Math.Clamp((int)MathF.Floor(v), 0, imgH - 1);
                    int y1 = Math.Clamp(y0 + 1, 0, imgH - 1);
                    float fv = Math.Clamp(v - y0, 0.0f, 1.0f);

                    int atlasRow = (rectY + y) * atlasSize;

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
                });
            }

            UploadMaskToGpu(rd, atlasSize);
        }

        private void UploadMaskToGpu(RenderingDevice rd, int atlasSize)
        {
            if (_activeAtlasMask == null || !_maskTextureRid.IsValid || !rd.TextureIsValid(_maskTextureRid)) return;

            byte[] uploadBytes = new byte[atlasSize * atlasSize * 8];
            Half one = (Half)1.0f;
            var lut = _halfLut;
            byte[] mask = _activeAtlasMask;
            int totalPixels = atlasSize * atlasSize;

            unsafe
            {
                fixed (byte* pMask = mask, pUpload = uploadBytes)
                {
                    Half* hDst = (Half*)pUpload;
                    for (int i = 0; i < totalPixels; i++)
                    {
                        Half v = lut[pMask[i]];
                        int off = i * 4;
                        hDst[off] = v;
                        hDst[off + 1] = v;
                        hDst[off + 2] = v;
                        hDst[off + 3] = one;
                    }
                }
            }

            rd.TextureUpdate(_maskTextureRid, 0, uploadBytes);
        }

        public void RecomputeWithTolerance(float newTolerance)
        {
            Tolerance = newTolerance;
            if (_seedPoints.Count > 0)
            {
                var rd = RenderingServer.GetRenderingDevice();
                if (rd != null)
                {
                    int size = _currentAtlasSize > 0 ? _currentAtlasSize : 2048;
                    RebuildMaskFromSeedPoints(rd, size);
                    EmitSignal(SignalName.MaskUpdated, true);
                }
            }
        }

        public void ClearMask()
        {
            HasSelection = false;
            UseSelectionMask = false;
            _seedPoints.Clear();

            if (_activeAtlasMask != null)
            {
                Array.Clear(_activeAtlasMask, 0, _activeAtlasMask.Length);
            }

            var rd = RenderingServer.GetRenderingDevice();
            if (rd != null && _maskTextureRid.IsValid && rd.TextureIsValid(_maskTextureRid))
            {
                int size = _currentAtlasSize > 0 ? _currentAtlasSize : 2048;
                byte[] zeroData = new byte[size * size * 8];
                rd.TextureUpdate(_maskTextureRid, 0, zeroData);
            }

            if (_maskTextureResource != null && _maskTextureRid.IsValid)
            {
                _maskTextureResource.TextureRdRid = _maskTextureRid;
            }

            EmitSignal(SignalName.MaskUpdated, false);
            GD.Print("[MagicWandTool] Selection mask cleared.");
        }

        public void Cleanup()
        {
            _seedPoints.Clear();
            _activeAtlasMask = null;

            if (_maskTextureResource != null)
            {
                _maskTextureResource.TextureRdRid = new Rid();
                _maskTextureResource = null;
            }

            var rd = RenderingServer.GetRenderingDevice();
            if (rd != null)
            {
                if (_maskTextureRid.IsValid && rd.TextureIsValid(_maskTextureRid))
                {
                    rd.FreeRid(_maskTextureRid);
                    _maskTextureRid = new Rid();
                }
            }
        }
    }
}
