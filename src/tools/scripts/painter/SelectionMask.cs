using Godot;
using System;
using System.Collections.Generic;

namespace DeadlockPlayground.Painter
{
    public enum SelectionToolType
    {
        Rectangular = 0,
        Lasso = 1,
        Polygonal = 2
    }

    public enum SelectionCombineMode
    {
        Replace = 0,
        Add = 1,
        Subtract = 2
    }

    public class SelectionMask
    {
        public event Action<bool> MaskUpdated;

        public int CanvasSize { get; private set; } = 2048;

        // CPU-side active selection mask buffer (1 byte per texel in atlas: 0 = unselected, 255 = selected)
        private byte[] _buffer;
        public byte[] Buffer => _buffer;

        public bool HasSelection { get; private set; } = false;

        private bool _useSelectionMask = true;
        public bool UseSelectionMask
        {
            get => _useSelectionMask;
            set
            {
                if (_useSelectionMask != value)
                {
                    _useSelectionMask = value;
                    MaskUpdated?.Invoke(HasSelection && _useSelectionMask);
                }
            }
        }

        public SelectionToolType CurrentToolType { get; set; } = SelectionToolType.Rectangular;

        private Rid _maskTextureRid = new();
        public Rid MaskTextureRid => _maskTextureRid;

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

        public SelectionMask(int canvasSize = 2048)
        {
            EnsureBuffer(canvasSize);
        }

        public void EnsureBuffer(int canvasSize)
        {
            if (canvasSize <= 0) canvasSize = 2048;
            if (_buffer == null || CanvasSize != canvasSize || _buffer.Length != canvasSize * canvasSize)
            {
                CanvasSize = canvasSize;
                _buffer = new byte[canvasSize * canvasSize];
                HasSelection = false;
            }
        }

        public void EnsureMaskTexture(RenderingDevice rd, int canvasSize)
        {
            if (canvasSize <= 0) canvasSize = 2048;
            EnsureBuffer(canvasSize);

            if (_maskTextureRid.IsValid && rd.TextureIsValid(_maskTextureRid) && CanvasSize == canvasSize)
            {
                return;
            }

            if (_maskTextureRid.IsValid && rd.TextureIsValid(_maskTextureRid))
            {
                rd.FreeRid(_maskTextureRid);
                _maskTextureRid = new Rid();
            }

            var fmt = new RDTextureFormat
            {
                Width = (uint)canvasSize,
                Height = (uint)canvasSize,
                Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
                TextureType = RenderingDevice.TextureType.Type2D,
                UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
                            RenderingDevice.TextureUsageBits.StorageBit |
                            RenderingDevice.TextureUsageBits.CanUpdateBit |
                            RenderingDevice.TextureUsageBits.CanCopyFromBit
            };

            var view = new RDTextureView();
            byte[] zeroBytes = new byte[canvasSize * canvasSize * 8];
            var dataArray = new Godot.Collections.Array<byte[]>();
            dataArray.Add(zeroBytes);
            _maskTextureRid = rd.TextureCreate(fmt, view, dataArray);
            if (_maskTextureResource != null)
            {
                _maskTextureResource.TextureRdRid = _maskTextureRid;
            }
        }

        public void UpdateSelectionState()
        {
            bool hasSel = false;
            byte[] buf = _buffer;
            if (buf != null)
            {
                for (int i = 0; i < buf.Length; i++)
                {
                    if (buf[i] > 0)
                    {
                        hasSel = true;
                        break;
                    }
                }
            }
            HasSelection = hasSel;
        }

        public void UploadToGpu(RenderingDevice rd = null)
        {
            UpdateSelectionState();
            rd ??= RenderingServer.GetRenderingDevice();
            if (rd == null) return;

            int size = CanvasSize;
            EnsureMaskTexture(rd, size);
            if (!_maskTextureRid.IsValid || !rd.TextureIsValid(_maskTextureRid)) return;

            byte[] uploadBytes = new byte[size * size * 8];
            Half one = (Half)1.0f;
            var lut = _halfLut;
            byte[] mask = _buffer;
            int totalPixels = size * size;

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

        public bool IsPixelSelected(int x, int y)
        {
            if (!HasSelection || _buffer == null) return true;
            if (x < 0 || y < 0 || x >= CanvasSize || y >= CanvasSize) return false;
            return _buffer[y * CanvasSize + x] >= 128;
        }

        public float GetPixelMaskValue(int x, int y)
        {
            if (!HasSelection || _buffer == null) return 1.0f;
            if (x < 0 || y < 0 || x >= CanvasSize || y >= CanvasSize) return 0.0f;
            return _buffer[y * CanvasSize + x] * (1.0f / 255.0f);
        }

        public void RasterizeRect(Rect2 pixelRect, SelectionCombineMode mode)
        {
            int size = CanvasSize;
            EnsureBuffer(size);

            int minX = Math.Clamp((int)MathF.Floor(pixelRect.Position.X), 0, size - 1);
            int minY = Math.Clamp((int)MathF.Floor(pixelRect.Position.Y), 0, size - 1);
            int maxX = Math.Clamp((int)MathF.Ceiling(pixelRect.End.X), 0, size - 1);
            int maxY = Math.Clamp((int)MathF.Ceiling(pixelRect.End.Y), 0, size - 1);

            if (minX > maxX) (minX, maxX) = (maxX, minX);
            if (minY > maxY) (minY, maxY) = (maxY, minY);

            byte[] buf = _buffer;
            if (mode == SelectionCombineMode.Replace)
            {
                Array.Clear(buf, 0, buf.Length);
            }

            for (int y = minY; y <= maxY; y++)
            {
                int row = y * size;
                for (int x = minX; x <= maxX; x++)
                {
                    int idx = row + x;
                    if (mode == SelectionCombineMode.Subtract)
                    {
                        buf[idx] = 0;
                    }
                    else
                    {
                        buf[idx] = 255;
                    }
                }
            }

            ApplyEdgeFeathering(minX, minY, maxX, maxY);
            UpdateSelectionStateAndUpload();
        }

        public void RasterizePolygon(IReadOnlyList<Vector2> points, SelectionCombineMode mode)
        {
            if (points == null || points.Count < 3) return;

            int size = CanvasSize;
            EnsureBuffer(size);

            // 1. Calculate bounding box
            float minXF = float.MaxValue, maxXF = float.MinValue;
            float minYF = float.MaxValue, maxYF = float.MinValue;
            for (int i = 0; i < points.Count; i++)
            {
                Vector2 pt = points[i];
                if (pt.X < minXF) minXF = pt.X;
                if (pt.X > maxXF) maxXF = pt.X;
                if (pt.Y < minYF) minYF = pt.Y;
                if (pt.Y > maxYF) maxYF = pt.Y;
            }

            int minX = Math.Clamp((int)MathF.Floor(minXF), 0, size - 1);
            int maxX = Math.Clamp((int)MathF.Ceiling(maxXF), 0, size - 1);
            int minY = Math.Clamp((int)MathF.Floor(minYF), 0, size - 1);
            int maxY = Math.Clamp((int)MathF.Ceiling(maxYF), 0, size - 1);

            if (minX > maxX || minY > maxY) return;

            byte[] buf = _buffer;
            if (mode == SelectionCombineMode.Replace)
            {
                Array.Clear(buf, 0, buf.Length);
            }

            int n = points.Count;
            List<float> xIntersects = new List<float>();

            // 2. Scanline polygon fill
            for (int y = minY; y <= maxY; y++)
            {
                float yCenter = y + 0.5f;
                xIntersects.Clear();

                for (int i = 0; i < n; i++)
                {
                    Vector2 p1 = points[i];
                    Vector2 p2 = points[(i + 1) % n];

                    if ((p1.Y <= yCenter && p2.Y > yCenter) || (p2.Y <= yCenter && p1.Y > yCenter))
                    {
                        float t = (yCenter - p1.Y) / (p2.Y - p1.Y);
                        float xInt = p1.X + t * (p2.X - p1.X);
                        xIntersects.Add(xInt);
                    }
                }

                if (xIntersects.Count < 2) continue;

                xIntersects.Sort();

                int rowOffset = y * size;
                for (int k = 0; k < xIntersects.Count - 1; k += 2)
                {
                    int startX = Math.Clamp((int)MathF.Round(xIntersects[k]), 0, size - 1);
                    int endX = Math.Clamp((int)MathF.Round(xIntersects[k + 1]) - 1, 0, size - 1);

                    for (int x = startX; x <= endX; x++)
                    {
                        int idx = rowOffset + x;
                        if (mode == SelectionCombineMode.Subtract)
                        {
                            buf[idx] = 0;
                        }
                        else
                        {
                            buf[idx] = 255;
                        }
                    }
                }
            }

            // 3. Bresenham outline on edges to guarantee closed, continuous boundary
            byte edgeVal = (mode == SelectionCombineMode.Subtract) ? (byte)0 : (byte)255;
            for (int i = 0; i < n; i++)
            {
                DrawBresenhamLine(points[i], points[(i + 1) % n], edgeVal);
            }

            ApplyEdgeFeathering(minX, minY, maxX, maxY);
            UpdateSelectionStateAndUpload();
        }

        private void DrawBresenhamLine(Vector2 p0, Vector2 p1, byte val)
        {
            int size = CanvasSize;
            int x0 = Math.Clamp((int)MathF.Round(p0.X), 0, size - 1);
            int y0 = Math.Clamp((int)MathF.Round(p0.Y), 0, size - 1);
            int x1 = Math.Clamp((int)MathF.Round(p1.X), 0, size - 1);
            int y1 = Math.Clamp((int)MathF.Round(p1.Y), 0, size - 1);

            int dx = Math.Abs(x1 - x0);
            int dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            while (true)
            {
                _buffer[y0 * size + x0] = val;
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 > -dy)
                {
                    err -= dy;
                    x0 += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    y0 += sy;
                }
            }
        }

        private void ApplyEdgeFeathering(int minX, int minY, int maxX, int maxY)
        {
            int size = CanvasSize;
            int pad = 3;
            int dMinX = Math.Max(0, minX - pad);
            int dMaxX = Math.Min(size - 1, maxX + pad);
            int dMinY = Math.Max(0, minY - pad);
            int dMaxY = Math.Min(size - 1, maxY + pad);

            int bW = dMaxX - dMinX + 1;
            int bH = dMaxY - dMinY + 1;
            if (bW <= 0 || bH <= 0) return;

            byte[] buf = _buffer;
            float[] hBlur = new float[bW * bH];

            // Horizontal pass
            for (int py = dMinY; py <= dMaxY; py++)
            {
                int row = py * size;
                int bRow = (py - dMinY) * bW;
                for (int px = dMinX; px <= dMaxX; px++)
                {
                    float left = (px > 0) ? buf[row + px - 1] : buf[row + px];
                    float center = buf[row + px];
                    float right = (px < size - 1) ? buf[row + px + 1] : buf[row + px];
                    hBlur[bRow + (px - dMinX)] = (left + 2.0f * center + right) * 0.25f;
                }
            }

            // Vertical pass
            for (int py = dMinY; py <= dMaxY; py++)
            {
                int row = py * size;
                int bPrevRow = Math.Max(0, py - dMinY - 1) * bW;
                int bRow = (py - dMinY) * bW;
                int bNextRow = Math.Min(bH - 1, py - dMinY + 1) * bW;

                for (int px = dMinX; px <= dMaxX; px++)
                {
                    int xOff = px - dMinX;
                    float top = hBlur[bPrevRow + xOff];
                    float mid = hBlur[bRow + xOff];
                    float bot = hBlur[bNextRow + xOff];
                    float val = (top + 2.0f * mid + bot) * 0.25f;

                    // Preserve solid core pixels (>= 250) or pure zero pixels (<= 5)
                    byte orig = buf[row + px];
                    if (orig > 0 && orig < 255)
                    {
                        buf[row + px] = (byte)Math.Clamp((int)MathF.Round(val), 0, 255);
                    }
                    else if (orig == 0 && val >= 64)
                    {
                        // Slight soft fringe along the outside edge
                        buf[row + px] = (byte)Math.Clamp((int)MathF.Round(val * 0.5f), 0, 127);
                    }
                }
            }
        }

        public void Invert()
        {
            int size = CanvasSize;
            EnsureBuffer(size);

            byte[] buf = _buffer;
            int len = buf.Length;
            for (int i = 0; i < len; i++)
            {
                buf[i] = (byte)(255 - buf[i]);
            }

            UpdateSelectionStateAndUpload();
            GD.Print("[SelectionMask] Selection inverted.");
        }

        public void Clear()
        {
            int size = CanvasSize;
            if (_buffer != null)
            {
                Array.Clear(_buffer, 0, _buffer.Length);
            }
            HasSelection = false;
            UploadToGpu();
            MaskUpdated?.Invoke(false);
            GD.Print("[SelectionMask] Selection cleared.");
        }

        public void SetBuffer(byte[] newBuffer, int size)
        {
            if (newBuffer == null) return;
            CanvasSize = size;
            _buffer = (byte[])newBuffer.Clone();
            UpdateSelectionStateAndUpload();
        }

        public void UpdateSelectionStateAndUpload()
        {
            UpdateSelectionState();
            UploadToGpu();
            MaskUpdated?.Invoke(HasSelection && _useSelectionMask);
        }

        public Image ToImage()
        {
            int size = CanvasSize;
            EnsureBuffer(size);
            var img = Image.CreateEmpty(size, size, false, Image.Format.R8);
            byte[] data = img.GetData();
            System.Buffer.BlockCopy(_buffer, 0, data, 0, _buffer.Length);
            return img;
        }

        public void FromImage(Image img)
        {
            if (img == null) return;
            int size = img.GetWidth();
            EnsureBuffer(size);

            if (img.GetFormat() != Image.Format.R8)
            {
                var copy = (Image)img.Duplicate();
                copy.Convert(Image.Format.R8);
                img = copy;
            }

            byte[] data = img.GetData();
            System.Buffer.BlockCopy(data, 0, _buffer, 0, Math.Min(_buffer.Length, data.Length));
            UpdateSelectionStateAndUpload();
        }

        public void Cleanup()
        {
            _buffer = null;
            if (_maskTextureResource != null)
            {
                _maskTextureResource.TextureRdRid = new Rid();
                _maskTextureResource = null;
            }

            var rd = RenderingServer.GetRenderingDevice();
            if (rd != null && _maskTextureRid.IsValid && rd.TextureIsValid(_maskTextureRid))
            {
                rd.FreeRid(_maskTextureRid);
                _maskTextureRid = new Rid();
            }
        }
    }
}
