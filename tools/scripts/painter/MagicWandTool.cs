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

        public bool IsPixelSelected(int atlasX, int atlasY)
        {
            if (!HasSelection || !UseSelectionMask || _activeAtlasMask == null) return true;
            if (atlasX < 0 || atlasY < 0 || atlasX >= _currentAtlasSize || atlasY >= _currentAtlasSize) return false;
            return _activeAtlasMask[atlasY * _currentAtlasSize + atlasX] > 0;
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

        public static bool IsColorMatch(Color c1, Color c2, float tolerance)
        {
            float dr = c1.R - c2.R;
            float dg = c1.G - c2.G;
            float db = c1.B - c2.B;
            // Normalized Euclidean distance in sRGB space [0.0 .. 1.0]
            float dist = Mathf.Sqrt((dr * dr + dg * dg + db * db) / 3.0f);
            return dist <= tolerance;
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

                bool[] submeshMask = new bool[imgW * imgH];

                if (seed.Contiguous)
                {
                    bool[] visited = new bool[imgW * imgH];
                    Queue<int> q = new Queue<int>();

                    int seedIdx = seedY * imgW + seedX;
                    visited[seedIdx] = true;
                    submeshMask[seedIdx] = true;
                    q.Enqueue(seedIdx);

                    while (q.Count > 0)
                    {
                        int curr = q.Dequeue();
                        int cx = curr % imgW;
                        int cy = curr / imgW;

                        // 4-connected neighbors
                        int nx, ny, nIdx;

                        // Right
                        nx = cx + 1; ny = cy;
                        if (nx < imgW)
                        {
                            nIdx = ny * imgW + nx;
                            if (!visited[nIdx])
                            {
                                visited[nIdx] = true;
                                Color col = img.GetPixel(nx, ny);
                                if ((seed.TargetColor.A <= 0.001f || col.A > 0.001f) && IsColorMatch(col, seed.TargetColor, _tolerance))
                                {
                                    submeshMask[nIdx] = true;
                                    q.Enqueue(nIdx);
                                }
                            }
                        }

                        // Left
                        nx = cx - 1; ny = cy;
                        if (nx >= 0)
                        {
                            nIdx = ny * imgW + nx;
                            if (!visited[nIdx])
                            {
                                visited[nIdx] = true;
                                Color col = img.GetPixel(nx, ny);
                                if ((seed.TargetColor.A <= 0.001f || col.A > 0.001f) && IsColorMatch(col, seed.TargetColor, _tolerance))
                                {
                                    submeshMask[nIdx] = true;
                                    q.Enqueue(nIdx);
                                }
                            }
                        }

                        // Down
                        nx = cx; ny = cy + 1;
                        if (ny < imgH)
                        {
                            nIdx = ny * imgW + nx;
                            if (!visited[nIdx])
                            {
                                visited[nIdx] = true;
                                Color col = img.GetPixel(nx, ny);
                                if ((seed.TargetColor.A <= 0.001f || col.A > 0.001f) && IsColorMatch(col, seed.TargetColor, _tolerance))
                                {
                                    submeshMask[nIdx] = true;
                                    q.Enqueue(nIdx);
                                }
                            }
                        }

                        // Up
                        nx = cx; ny = cy - 1;
                        if (ny >= 0)
                        {
                            nIdx = ny * imgW + nx;
                            if (!visited[nIdx])
                            {
                                visited[nIdx] = true;
                                Color col = img.GetPixel(nx, ny);
                                if ((seed.TargetColor.A <= 0.001f || col.A > 0.001f) && IsColorMatch(col, seed.TargetColor, _tolerance))
                                {
                                    submeshMask[nIdx] = true;
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
                        for (int px = 0; px < imgW; px++)
                        {
                            Color col = img.GetPixel(px, py);
                            if ((seed.TargetColor.A <= 0.001f || col.A > 0.001f) && IsColorMatch(col, seed.TargetColor, _tolerance))
                            {
                                submeshMask[py * imgW + px] = true;
                            }
                        }
                    }
                }

                // Map submeshMask into _activeAtlasMask with boolean combining
                for (int y = 0; y < rectH; y++)
                {
                    int subY = Mathf.Clamp((int)((float)y / rectH * imgH), 0, imgH - 1);
                    int atlasRow = (rectY + y) * atlasSize;
                    for (int x = 0; x < rectW; x++)
                    {
                        int subX = Mathf.Clamp((int)((float)x / rectW * imgW), 0, imgW - 1);
                        bool isMatch = submeshMask[subY * imgW + subX];
                        if (!isMatch) continue;

                        int atlasIdx = atlasRow + (rectX + x);
                        if (seed.CombineMode == MagicWandCombineMode.Subtract)
                        {
                            // Subtraction: OldMask AND NOT(CurrentSelection)
                            _activeAtlasMask[atlasIdx] = 0;
                        }
                        else
                        {
                            // Replace / Add: OldMask OR CurrentSelection
                            _activeAtlasMask[atlasIdx] = 255;
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
                    Half zero = (Half)0.0f;
                    int pixelCount = atlasSize * atlasSize;
                    for (int i = 0; i < pixelCount; i++)
                    {
                        Half v = pMask[i] > 0 ? one : zero;
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
