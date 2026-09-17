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

        // Cached dispatch parameters for live tolerance slider adjustments
        private Image _lastSubmeshImage;
        private Vector2 _lastHitUV = Vector2.Zero;
        private Rect2 _lastSubmeshRect = new(0, 0, 1, 1);
        private Rid _lastBaseTextureRid = new();
        private MagicWandCombineMode _lastCombineMode = MagicWandCombineMode.Replace;

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

            // Cache for live recomputation
            TargetColor = sampledColor;
            _lastSubmeshImage = submeshImg;
            _lastHitUV = hitUV;
            _lastSubmeshRect = submeshRect;
            _lastBaseTextureRid = baseTextureRid;
            _lastCombineMode = combineMode;

            if (combineMode == MagicWandCombineMode.Replace)
            {
                Array.Clear(_activeAtlasMask, 0, _activeAtlasMask.Length);
            }

            if (submeshImg != null)
            {
                int imgW = submeshImg.GetWidth();
                int imgH = submeshImg.GetHeight();
                int seedX = Mathf.Clamp((int)(hitUV.X * imgW), 0, imgW - 1);
                int seedY = Mathf.Clamp((int)(hitUV.Y * imgH), 0, imgH - 1);
                Color seedColor = submeshImg.GetPixel(seedX, seedY);
                TargetColor = seedColor;

                int rectX = Mathf.Clamp((int)(submeshRect.Position.X * atlasSize), 0, atlasSize - 1);
                int rectY = Mathf.Clamp((int)(submeshRect.Position.Y * atlasSize), 0, atlasSize - 1);
                int rectW = Mathf.Clamp((int)(submeshRect.Size.X * atlasSize), 1, atlasSize - rectX);
                int rectH = Mathf.Clamp((int)(submeshRect.Size.Y * atlasSize), 1, atlasSize - rectY);

                if (Contiguous)
                {
                    // Contiguous BFS flood fill starting from clicked seed pixel
                    bool[] visited = new bool[imgW * imgH];
                    bool[] submeshMask = new bool[imgW * imgH];
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
                                Color col = submeshImg.GetPixel(nx, ny);
                                if ((seedColor.A <= 0.001f || col.A > 0.001f) && IsColorMatch(col, seedColor, _tolerance))
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
                                Color col = submeshImg.GetPixel(nx, ny);
                                if ((seedColor.A <= 0.001f || col.A > 0.001f) && IsColorMatch(col, seedColor, _tolerance))
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
                                Color col = submeshImg.GetPixel(nx, ny);
                                if ((seedColor.A <= 0.001f || col.A > 0.001f) && IsColorMatch(col, seedColor, _tolerance))
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
                                Color col = submeshImg.GetPixel(nx, ny);
                                if ((seedColor.A <= 0.001f || col.A > 0.001f) && IsColorMatch(col, seedColor, _tolerance))
                                {
                                    submeshMask[nIdx] = true;
                                    q.Enqueue(nIdx);
                                }
                            }
                        }
                    }

                    // Map submeshMask into _activeAtlasMask
                    for (int y = 0; y < rectH; y++)
                    {
                        int subY = Mathf.Clamp((int)((float)y / rectH * imgH), 0, imgH - 1);
                        int atlasRow = (rectY + y) * atlasSize;
                        for (int x = 0; x < rectW; x++)
                        {
                            int subX = Mathf.Clamp((int)((float)x / rectW * imgW), 0, imgW - 1);
                            bool isMatch = submeshMask[subY * imgW + subX];
                            int atlasIdx = atlasRow + (rectX + x);

                            if (combineMode == MagicWandCombineMode.Subtract)
                            {
                                if (isMatch) _activeAtlasMask[atlasIdx] = 0;
                            }
                            else
                            {
                                if (isMatch) _activeAtlasMask[atlasIdx] = 255;
                            }
                        }
                    }
                }
                else
                {
                    // Non-contiguous (Global color range across target submesh)
                    for (int y = 0; y < rectH; y++)
                    {
                        int subY = Mathf.Clamp((int)((float)y / rectH * imgH), 0, imgH - 1);
                        int atlasRow = (rectY + y) * atlasSize;
                        for (int x = 0; x < rectW; x++)
                        {
                            int subX = Mathf.Clamp((int)((float)x / rectW * imgW), 0, imgW - 1);
                            Color col = submeshImg.GetPixel(subX, subY);
                            bool isMatch = (seedColor.A <= 0.001f || col.A > 0.001f) && IsColorMatch(col, seedColor, _tolerance);
                            int atlasIdx = atlasRow + (rectX + x);

                            if (combineMode == MagicWandCombineMode.Subtract)
                            {
                                if (isMatch) _activeAtlasMask[atlasIdx] = 0;
                            }
                            else
                            {
                                if (isMatch) _activeAtlasMask[atlasIdx] = 255;
                            }
                        }
                    }
                }
            }

            // Upload CPU mask to GPU texture (_maskTextureRid)
            UploadMaskToGpu(rd, atlasSize);

            HasSelection = true;
            UseSelectionMask = true;

            EmitSignal(SignalName.ColorSampled, TargetColor);
            EmitSignal(SignalName.MaskUpdated, true);

            GD.Print($"[MagicWandTool] Generated selection mask: Color=#{TargetColor.ToHtml(false)} Mode={combineMode} Contiguous={Contiguous} Tol={_tolerance:F2}");
            return true;
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
            if (HasSelection && _lastSubmeshImage != null)
            {
                GenerateMask(
                    TargetColor,
                    _lastHitUV,
                    _lastSubmeshImage,
                    _lastSubmeshRect,
                    _lastBaseTextureRid,
                    _currentAtlasSize,
                    _lastCombineMode
                );
            }
        }

        public void ClearMask()
        {
            HasSelection = false;
            UseSelectionMask = false;
            _lastSubmeshImage = null;

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
            _activeAtlasMask = null;
            _lastSubmeshImage = null;

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
