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

        private float _tolerance = 0.08f;
        public float Tolerance
        {
            get => _tolerance;
            set => _tolerance = Mathf.Clamp(value, 0.002f, 0.50f);
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
            // Normalized Euclidean distance in sRGB space [0.0 .. 1.0]
            return Mathf.Sqrt((dr * dr + dg * dg + db * db) / 3.0f);
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
                int imgW = img.GetWidth();
                int imgH = img.GetHeight();
                int seedX = Mathf.Clamp((int)(seed.HitUV.X * imgW), 0, imgW - 1);
                int seedY = Mathf.Clamp((int)(seed.HitUV.Y * imgH), 0, imgH - 1);

                int rectX = Mathf.Clamp((int)(seed.SubmeshRect.Position.X * atlasSize), 0, atlasSize - 1);
                int rectY = Mathf.Clamp((int)(seed.SubmeshRect.Position.Y * atlasSize), 0, atlasSize - 1);
                int rectW = Mathf.Clamp((int)(seed.SubmeshRect.Size.X * atlasSize), 1, atlasSize - rectX);
                int rectH = Mathf.Clamp((int)(seed.SubmeshRect.Size.Y * atlasSize), 1, atlasSize - rectY);

                float[] submeshMask = new float[imgW * imgH];

                float tol = _tolerance;
                // Soft edge threshold: allows gentle boundary transition to capture beard & hair blends
                float softTol = tol * 1.35f;
                // Propagation cutoff in flood fill: slightly beyond hard tolerance so it flows into the fringe
                float flowTol = tol * 1.15f;

                if (seed.Contiguous)
                {
                    bool[] visited = new bool[imgW * imgH];
                    Queue<int> q = new Queue<int>();

                    int seedIdx = seedY * imgW + seedX;
                    visited[seedIdx] = true;
                    submeshMask[seedIdx] = 1.0f;
                    q.Enqueue(seedIdx);

                    int[] dxs = { 1, -1, 0, 0 };
                    int[] dys = { 0, 0, 1, -1 };

                    while (q.Count > 0)
                    {
                        int curr = q.Dequeue();
                        int cx = curr % imgW;
                        int cy = curr / imgW;

                        for (int dir = 0; dir < 4; dir++)
                        {
                            int nx = cx + dxs[dir];
                            int ny = cy + dys[dir];
                            if (nx < 0 || nx >= imgW || ny < 0 || ny >= imgH) continue;

                            int nIdx = ny * imgW + nx;
                            if (visited[nIdx]) continue;

                            visited[nIdx] = true;
                            Color col = img.GetPixel(nx, ny);
                            if (seed.TargetColor.A > 0.001f && col.A <= 0.001f) continue;

                            float dist = GetColorDistance(col, seed.TargetColor);
                            if (dist <= tol)
                            {
                                submeshMask[nIdx] = 1.0f;
                                q.Enqueue(nIdx);
                            }
                            else if (dist <= softTol)
                            {
                                float match = 1.0f - (dist - tol) / Math.Max(0.0001f, softTol - tol);
                                submeshMask[nIdx] = match;
                                if (dist <= flowTol)
                                {
                                    q.Enqueue(nIdx);
                                }
                            }
                        }
                    }
                }
                else
                {
                    for (int py = 0; py < imgH; py++)
                    {
                        int row = py * imgW;
                        for (int px = 0; px < imgW; px++)
                        {
                            Color col = img.GetPixel(px, py);
                            if (seed.TargetColor.A > 0.001f && col.A <= 0.001f) continue;

                            float dist = GetColorDistance(col, seed.TargetColor);
                            if (dist <= tol)
                            {
                                submeshMask[row + px] = 1.0f;
                            }
                            else if (dist <= softTol)
                            {
                                submeshMask[row + px] = 1.0f - (dist - tol) / Math.Max(0.0001f, softTol - tol);
                            }
                        }
                    }
                }

                // 1-pixel border dilation to capture blended hair/beard fringe pixels
                float[] dilatedMask = (float[])submeshMask.Clone();
                float dilationTol = tol * 1.05f;
                for (int py = 0; py < imgH; py++)
                {
                    for (int px = 0; px < imgW; px++)
                    {
                        int idx = py * imgW + px;
                        if (submeshMask[idx] > 0.35f) continue;

                        // Check if adjacent to high confidence selection
                        bool neighborSelected = false;
                        if (px > 0 && submeshMask[idx - 1] >= 0.5f) neighborSelected = true;
                        else if (px < imgW - 1 && submeshMask[idx + 1] >= 0.5f) neighborSelected = true;
                        else if (py > 0 && submeshMask[idx - imgW] >= 0.5f) neighborSelected = true;
                        else if (py < imgH - 1 && submeshMask[idx + imgW] >= 0.5f) neighborSelected = true;

                        if (neighborSelected)
                        {
                            Color col = img.GetPixel(px, py);
                            if (seed.TargetColor.A <= 0.001f || col.A > 0.001f)
                            {
                                float dist = GetColorDistance(col, seed.TargetColor);
                                if (dist <= dilationTol)
                                {
                                    float fringeStrength = Mathf.Clamp(1.0f - (dist - tol) / Math.Max(0.0001f, dilationTol - tol), 0.2f, 0.75f);
                                    dilatedMask[idx] = Mathf.Max(dilatedMask[idx], fringeStrength);
                                }
                            }
                        }
                    }
                }

                // 3x3 anti-aliasing smoothing filter on boundary pixels to eliminate pixelated stair-steps
                float[] filteredMask = (float[])dilatedMask.Clone();
                for (int py = 1; py < imgH - 1; py++)
                {
                    for (int px = 1; px < imgW - 1; px++)
                    {
                        int idx = py * imgW + px;
                        float center = dilatedMask[idx];
                        float l = dilatedMask[idx - 1];
                        float r = dilatedMask[idx + 1];
                        float u = dilatedMask[idx - imgW];
                        float d = dilatedMask[idx + imgW];

                        if ((center > 0.001f && center < 0.999f) ||
                            (center >= 0.999f && (l < 0.5f || r < 0.5f || u < 0.5f || d < 0.5f)) ||
                            (center <= 0.001f && (l > 0.5f || r > 0.5f || u > 0.5f || d > 0.5f)))
                        {
                            float filtered = center * 0.4f + (l + r + u + d) * 0.15f;
                            filteredMask[idx] = filtered;
                        }
                    }
                }

                // Map submeshMask into _activeAtlasMask with bilinear resampling and proper combine modes
                for (int y = 0; y < rectH; y++)
                {
                    float v = ((float)y + 0.5f) / rectH * imgH - 0.5f;
                    int y0 = Mathf.Clamp((int)MathF.Floor(v), 0, imgH - 1);
                    int y1 = Mathf.Clamp(y0 + 1, 0, imgH - 1);
                    float fv = Mathf.Clamp(v - y0, 0.0f, 1.0f);

                    int atlasRow = (rectY + y) * atlasSize;

                    for (int x = 0; x < rectW; x++)
                    {
                        float u = ((float)x + 0.5f) / rectW * imgW - 0.5f;
                        int x0 = Mathf.Clamp((int)MathF.Floor(u), 0, imgW - 1);
                        int x1 = Mathf.Clamp(x0 + 1, 0, imgH - 1);
                        float fu = Mathf.Clamp(u - x0, 0.0f, 1.0f);

                        // Bilinear interpolation
                        float m00 = filteredMask[y0 * imgW + x0];
                        float m10 = filteredMask[y0 * imgW + x1];
                        float m01 = filteredMask[y1 * imgW + x0];
                        float m11 = filteredMask[y1 * imgW + x1];

                        float top = m00 + (m10 - m00) * fu;
                        float bottom = m01 + (m11 - m01) * fu;
                        float val = top + (bottom - top) * fv;

                        byte byteVal = (byte)Mathf.Clamp((int)MathF.Round(val * 255.0f), 0, 255);
                        int atlasIdx = atlasRow + (rectX + x);

                        if (seed.CombineMode == MagicWandCombineMode.Subtract)
                        {
                            _activeAtlasMask[atlasIdx] = (byte)Math.Max(0, _activeAtlasMask[atlasIdx] - byteVal);
                        }
                        else if (seed.CombineMode == MagicWandCombineMode.Add)
                        {
                            _activeAtlasMask[atlasIdx] = (byte)Math.Min(255, _activeAtlasMask[atlasIdx] + byteVal);
                        }
                        else
                        {
                            // Replace
                            _activeAtlasMask[atlasIdx] = byteVal;
                        }
                    }
                }
            }

            UploadMaskToGpu(rd, atlasSize);
        }

        private void UploadMaskToGpu(RenderingDevice rd, int atlasSize)
        {
            if (_activeAtlasMask == null || !_maskTextureRid.IsValid || !rd.TextureIsValid(_maskTextureRid)) return;

            byte[] uploadBytes = new byte[atlasSize * atlasSize * 8];
            unsafe
            {
                fixed (byte* pMask = _activeAtlasMask)
                fixed (byte* pUpload = uploadBytes)
                {
                    Half* hDst = (Half*)pUpload;
                    Half one = (Half)1.0f;
                    int pixelCount = atlasSize * atlasSize;
                    for (int i = 0; i < pixelCount; i++)
                    {
                        Half v = (Half)(pMask[i] / 255.0f);
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
