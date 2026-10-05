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

        public int Width { get; private set; } = 2048;
        public int Height { get; private set; } = 2048;
        public int CanvasSize => Math.Max(Width, Height);

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
        private int _maskTextureWidth = 0;
        private int _maskTextureHeight = 0;

        public Rid MaskTextureRid
        {
            get
            {
                var rd = RenderingServer.GetRenderingDevice();
                if (rd != null && (!_maskTextureRid.IsValid || !rd.TextureIsValid(_maskTextureRid) || _maskTextureWidth != Width || _maskTextureHeight != Height))
                {
                    EnsureMaskTexture(rd, Width, Height);
                }
                return _maskTextureRid;
            }
        }

        private Texture2Drd _maskTextureResource;
        public Texture2Drd MaskTextureResource
        {
            get
            {
                var rd = RenderingServer.GetRenderingDevice();
                if (rd != null && (!_maskTextureRid.IsValid || !rd.TextureIsValid(_maskTextureRid) || _maskTextureWidth != Width || _maskTextureHeight != Height))
                {
                    EnsureMaskTexture(rd, Width, Height);
                }
                if (_maskTextureResource == null)
                {
                    _maskTextureResource = new Texture2Drd();
                }
                var currentRid = MaskTextureRid;
                if (currentRid.IsValid && _maskTextureResource.TextureRdRid != currentRid)
                {
                    _maskTextureResource.TextureRdRid = currentRid;
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

        public SelectionMask(int width = 2048, int height = 2048)
        {
            EnsureBuffer(width, height);
        }

        public void EnsureBuffer(int width, int height)
        {
            if (width < 256) width = 256;
            if (height < 256) height = 256;
            if (_buffer == null || Width != width || Height != height || _buffer.Length != width * height)
            {
                Width = width;
                Height = height;
                _buffer = new byte[width * height];
                HasSelection = false;
            }
        }

        public void EnsureBuffer(int canvasSize)
        {
            if (Width > 0 && Height > 0 && (Width != Height || Width == canvasSize || Height == canvasSize))
            {
                EnsureBuffer(Width, Height);
            }
            else
            {
                EnsureBuffer(canvasSize, canvasSize);
            }
        }

        public void EnsureMaskTexture(RenderingDevice rd, int width, int height)
        {
            if (width < 256) width = 256;
            if (height < 256) height = 256;
            EnsureBuffer(width, height);

            if (_maskTextureRid.IsValid && rd.TextureIsValid(_maskTextureRid) && _maskTextureWidth == width && _maskTextureHeight == height)
            {
                return;
            }

            if (_maskTextureResource != null && _maskTextureResource.TextureRdRid.IsValid)
            {
                _maskTextureResource.TextureRdRid = new Rid();
                _maskTextureRid = new Rid();
                _maskTextureWidth = 0;
                _maskTextureHeight = 0;
            }
            else if (_maskTextureRid.IsValid && rd.TextureIsValid(_maskTextureRid))
            {
                rd.FreeRid(_maskTextureRid);
                _maskTextureRid = new Rid();
                _maskTextureWidth = 0;
                _maskTextureHeight = 0;
            }

            var fmt = new RDTextureFormat
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
                TextureType = RenderingDevice.TextureType.Type2D,
                UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
                            RenderingDevice.TextureUsageBits.StorageBit |
                            RenderingDevice.TextureUsageBits.CanUpdateBit |
                            RenderingDevice.TextureUsageBits.CanCopyFromBit |
                            RenderingDevice.TextureUsageBits.CanCopyToBit
            };

            var view = new RDTextureView();
            byte[] zeroBytes = new byte[width * height * 8];
            var dataArray = new Godot.Collections.Array<byte[]> { zeroBytes };
            _maskTextureRid = rd.TextureCreate(fmt, view, dataArray);
            _maskTextureWidth = width;
            _maskTextureHeight = height;
            if (_maskTextureResource != null)
            {
                _maskTextureResource.TextureRdRid = _maskTextureRid;
            }
        }

        public void EnsureMaskTexture(RenderingDevice rd, int canvasSize)
        {
            if (Width > 0 && Height > 0 && (Width != Height || Width == canvasSize || Height == canvasSize))
            {
                EnsureMaskTexture(rd, Width, Height);
            }
            else
            {
                EnsureMaskTexture(rd, canvasSize, canvasSize);
            }
        }

        public void EnsureSize(int width, int height)
        {
            if (width < 256) width = 256;
            if (height < 256) height = 256;
            if (Width != width || Height != height || _buffer == null || _buffer.Length != width * height || _maskTextureWidth != width || _maskTextureHeight != height)
            {
                EnsureBuffer(width, height);
                var rd = RenderingServer.GetRenderingDevice();
                if (rd != null)
                {
                    EnsureMaskTexture(rd, width, height);
                }
            }
        }

        public void EnsureSize(int canvasSize)
        {
            if (Width > 0 && Height > 0 && (Width != Height || Width == canvasSize || Height == canvasSize))
            {
                EnsureSize(Width, Height);
            }
            else
            {
                EnsureSize(canvasSize, canvasSize);
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

            int w = Width;
            int h = Height;
            EnsureMaskTexture(rd, w, h);
            if (!_maskTextureRid.IsValid || !rd.TextureIsValid(_maskTextureRid)) return;

            byte[] uploadBytes = new byte[w * h * 8];
            Half one = (Half)1.0f;
            var lut = _halfLut;
            byte[] mask = _buffer;
            int totalPixels = w * h;

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
            if (x < 0 || y < 0 || x >= Width || y >= Height) return false;
            return _buffer[y * Width + x] >= 128;
        }

        public float GetPixelMaskValue(int x, int y)
        {
            if (!HasSelection || _buffer == null) return 1.0f;
            if (x < 0 || y < 0 || x >= Width || y >= Height) return 0.0f;
            return _buffer[y * Width + x] * (1.0f / 255.0f);
        }

        public void RasterizeRect(Vector2 p1, Vector2 p2, SelectionCombineMode mode)
        {
            int w = Width;
            int h = Height;
            EnsureBuffer(w, h);

            int xStart = (int)MathF.Round(MathF.Min(p1.X, p2.X));
            int xEnd   = (int)MathF.Round(MathF.Max(p1.X, p2.X));
            int yStart = (int)MathF.Round(MathF.Min(p1.Y, p2.Y));
            int yEnd   = (int)MathF.Round(MathF.Max(p1.Y, p2.Y));

            int minX = Math.Clamp(xStart, 0, w - 1);
            int maxX = Math.Clamp(xEnd, 0, w - 1);
            int minY = Math.Clamp(yStart, 0, h - 1);
            int maxY = Math.Clamp(yEnd, 0, h - 1);

            if (minX > maxX) (minX, maxX) = (maxX, minX);
            if (minY > maxY) (minY, maxY) = (maxY, minY);

            byte[] buf = _buffer;
            if (mode == SelectionCombineMode.Replace)
            {
                Array.Clear(buf, 0, buf.Length);
            }

            for (int y = minY; y <= maxY; y++)
            {
                int row = y * w;
                for (int x = minX; x <= maxX; x++)
                {
                    int idx = row + x;
                    buf[idx] = (mode == SelectionCombineMode.Subtract) ? (byte)0 : (byte)255;
                }
            }

            UpdateSelectionStateAndUpload();
        }

        public void RasterizeRect(Rect2 pixelRect, SelectionCombineMode mode)
        {
            RasterizeRect(pixelRect.Position, pixelRect.End, mode);
        }

        public void RasterizePolygon(IReadOnlyList<Vector2> points, SelectionCombineMode mode)
        {
            if (points == null || points.Count < 3) return;

            int w = Width;
            int h = Height;
            EnsureBuffer(w, h);

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

            int minX = Math.Clamp((int)MathF.Floor(minXF), 0, w - 1);
            int maxX = Math.Clamp((int)MathF.Ceiling(maxXF), 0, w - 1);
            int minY = Math.Clamp((int)MathF.Floor(minYF), 0, h - 1);
            int maxY = Math.Clamp((int)MathF.Ceiling(maxYF), 0, h - 1);

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

                int rowOffset = y * w;
                for (int k = 0; k < xIntersects.Count - 1; k += 2)
                {
                    int startX = Math.Clamp((int)MathF.Round(xIntersects[k]), 0, w - 1);
                    int endX = Math.Clamp((int)MathF.Round(xIntersects[k + 1]) - 1, 0, w - 1);

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
            int w = Width;
            int h = Height;
            int x0 = Math.Clamp((int)MathF.Round(p0.X), 0, w - 1);
            int y0 = Math.Clamp((int)MathF.Round(p0.Y), 0, h - 1);
            int x1 = Math.Clamp((int)MathF.Round(p1.X), 0, w - 1);
            int y1 = Math.Clamp((int)MathF.Round(p1.Y), 0, h - 1);

            int dx = Math.Abs(x1 - x0);
            int dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            while (true)
            {
                _buffer[y0 * w + x0] = val;
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
            int w = Width;
            int h = Height;
            int pad = 3;
            int dMinX = Math.Max(0, minX - pad);
            int dMaxX = Math.Min(w - 1, maxX + pad);
            int dMinY = Math.Max(0, minY - pad);
            int dMaxY = Math.Min(h - 1, maxY + pad);

            int bW = dMaxX - dMinX + 1;
            int bH = dMaxY - dMinY + 1;
            if (bW <= 0 || bH <= 0) return;

            byte[] buf = _buffer;
            float[] hBlur = new float[bW * bH];

            // Horizontal pass
            for (int py = dMinY; py <= dMaxY; py++)
            {
                int row = py * w;
                int bRow = (py - dMinY) * bW;
                for (int px = dMinX; px <= dMaxX; px++)
                {
                    float left = (px > 0) ? buf[row + px - 1] : buf[row + px];
                    float center = buf[row + px];
                    float right = (px < w - 1) ? buf[row + px + 1] : buf[row + px];
                    hBlur[bRow + (px - dMinX)] = (left + 2.0f * center + right) * 0.25f;
                }
            }

            // Vertical pass
            for (int py = dMinY; py <= dMaxY; py++)
            {
                int row = py * w;
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
            int w = Width;
            int h = Height;
            EnsureBuffer(w, h);

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
            int w = Width;
            int h = Height;
            if (_buffer != null)
            {
                Array.Clear(_buffer, 0, _buffer.Length);
            }
            HasSelection = false;
            UploadToGpu();
            MaskUpdated?.Invoke(false);
            GD.Print("[SelectionMask] Selection cleared.");
        }

        public void SetBuffer(byte[] newBuffer, int width, int height)
        {
            if (newBuffer == null) return;
            Width = width;
            Height = height;
            _buffer = (byte[])newBuffer.Clone();
            UpdateSelectionStateAndUpload();
        }

        public void SetBuffer(byte[] newBuffer, int size)
        {
            SetBuffer(newBuffer, size, size);
        }

        public void UpdateSelectionStateAndUpload()
        {
            UpdateSelectionState();
            UploadToGpu();
            MaskUpdated?.Invoke(HasSelection && _useSelectionMask);
        }

        public Image ToImage()
        {
            int w = Width;
            int h = Height;
            EnsureBuffer(w, h);
            var img = Image.CreateEmpty(w, h, false, Image.Format.R8);
            byte[] data = img.GetData();
            System.Buffer.BlockCopy(_buffer, 0, data, 0, _buffer.Length);
            return img;
        }

        public void FromImage(Image img)
        {
            if (img == null) return;
            int w = img.GetWidth();
            int h = img.GetHeight();
            EnsureBuffer(w, h);

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
            _maskTextureWidth = 0;
            _maskTextureHeight = 0;
            if (_maskTextureResource != null)
            {
                if (_maskTextureResource.TextureRdRid.IsValid)
                {
                    _maskTextureResource.TextureRdRid = new Rid();
                }
                _maskTextureResource = null;
                _maskTextureRid = new Rid();
            }
            else
            {
                var rd = RenderingServer.GetRenderingDevice();
                if (rd != null && _maskTextureRid.IsValid && rd.TextureIsValid(_maskTextureRid))
                {
                    rd.FreeRid(_maskTextureRid);
                    _maskTextureRid = new Rid();
                }
            }
        }
    }
}
