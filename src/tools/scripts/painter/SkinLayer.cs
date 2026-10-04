using Godot;
using System;

namespace DeadlockPlayground.Painter
{
    public enum LayerBlendMode
    {
        Normal = 0,
        Multiply = 1,
        Screen = 2,
        Overlay = 3,
        Darken = 4,
        Lighten = 5,
        ColorDodge = 6,
        Color = 7
    }

    public partial class SkinLayer : RefCounted
    {
        public string Name { get; set; } = "New Layer";
        public bool IsVisible { get; set; } = true;
        public bool IsLocked { get; set; } = false;
        public float Opacity { get; set; } = 1.0f;
        public LayerBlendMode BlendMode { get; set; } = LayerBlendMode.Normal;
        public byte[] GpuData { get; set; }
        public byte[] CompressedGpuData { get; set; }
        public ImageTexture Texture { get; set; }
        public Image BackingImage { get; set; }
        public Rid LayerRid { get; set; } = new Rid();
        public bool IsCpuSynced { get; set; } = true;

        public SubViewport Viewport { get; private set; }
        public SubViewport CompositeViewport { get; set; }
        public Node2D StrokeContainer { get; private set; }
        public TextureRect DisplayRect { get; private set; }
        public BackBufferCopy BufferCopy { get; private set; }
        public ShaderMaterial CompositeMaterial { get; private set; }

        private Vector2I _canvasSize;
        private Shader _dabShader;

        public SkinLayer() { }

        public bool IsBlank()
        {
            if (CompressedGpuData != null && CompressedGpuData.Length > 0)
            {
                return CompressedGpuData.Length < 250;
            }
            if (GpuData != null && GpuData.Length > 0)
            {
                unsafe
                {
                    fixed (byte* p = GpuData)
                    {
                        ulong* u = (ulong*)p;
                        int uCount = GpuData.Length / 8;
                        for (int i = 0; i < uCount; i++)
                        {
                            if (u[i] != 0) return false;
                        }
                    }
                }
                return true;
            }
            return true;
        }

        public void CompressGpuData()
        {
            if (GpuData != null && GpuData.Length > 0)
            {
                using var ms = new System.IO.MemoryStream();
                using (var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                {
                    ds.Write(GpuData, 0, GpuData.Length);
                }
                CompressedGpuData = ms.ToArray();
                GpuData = null;
            }
        }

        public void DecompressGpuData(int expectedLength)
        {
            if (GpuData == null || GpuData.Length != expectedLength)
            {
                if (CompressedGpuData != null && CompressedGpuData.Length > 0)
                {
                    byte[] decompressed = new byte[expectedLength];
                    using var ms = new System.IO.MemoryStream(CompressedGpuData);
                    using var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress);
                    int totalRead = 0;
                    while (totalRead < expectedLength)
                    {
                        int bytesRead = ds.Read(decompressed, totalRead, expectedLength - totalRead);
                        if (bytesRead == 0) break;
                        totalRead += bytesRead;
                    }
                    GpuData = decompressed;
                }
                else
                {
                    GpuData = new byte[expectedLength];
                }
            }
        }

        public void Initialize(string name, Vector2I canvasSize, bool isLocked, Shader compositeShader, Shader dabShader, Texture2D baseTexture = null)
        {
            Name = name;
            _canvasSize = canvasSize;
            IsLocked = isLocked;
            _dabShader = dabShader;
            if (compositeShader != null)
            {
                CompositeMaterial = new ShaderMaterial { Shader = compositeShader };
                UpdateMaterialParameters();
            }
        }

        public void PaintDab(Vector2 uvCoordinate, Color brushColor, float brushSize, Texture2D brushMask, float hardness, float flow, Vector2 aspectScale = default)
        {
            if (IsLocked || Viewport == null || StrokeContainer == null) return;

            if (aspectScale == default || aspectScale == Vector2.Zero || float.IsNaN(aspectScale.X) || float.IsNaN(aspectScale.Y))
            {
                aspectScale = Vector2.One;
            }

            // Clamp UV into 0..1
            Vector2 clampedUv = new Vector2(Mathf.Clamp(uvCoordinate.X, 0.0f, 1.0f), Mathf.Clamp(uvCoordinate.Y, 0.0f, 1.0f));

            var dabMat = new ShaderMaterial { Shader = _dabShader };
            dabMat.SetShaderParameter("u_brush_color", brushColor);
            dabMat.SetShaderParameter("u_hardness", hardness);
            dabMat.SetShaderParameter("u_flow", flow);
            if (brushMask != null)
            {
                dabMat.SetShaderParameter("u_brush_mask", brushMask);
            }

            float baseDim = 64.0f;
            Texture2D dabTex = brushMask;
            if (dabTex != null)
            {
                baseDim = Mathf.Max(dabTex.GetWidth(), dabTex.GetHeight());
            }
            else
            {
                dabTex = CreateFallbackDabTexture();
            }
            if (baseDim <= 0) baseDim = 64.0f;

            // Guard dab scale so it cannot collapse to zero or sub-pixel sizes
            float baseSize = Mathf.Max(16.0f, brushSize);
            Vector2 finalDabSize = aspectScale * baseSize;

            var dab = new Sprite2D
            {
                Texture = dabTex,
                Material = dabMat,
                Position = clampedUv * (Vector2)_canvasSize,
                Scale = finalDabSize / baseDim,
                Modulate = brushColor
            };

            StrokeContainer.AddChild(dab);
            Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
            if (CompositeViewport != null && GodotObject.IsInstanceValid(CompositeViewport))
            {
                CompositeViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
            }
        }

        public void PaintDecal(Vector2 uvCoordinate, Texture2D decalTexture, float rotationDegrees, float scale)
        {
            if (IsLocked || Viewport == null || StrokeContainer == null || decalTexture == null) return;

            Vector2 canvasPos = uvCoordinate * _canvasSize;
            var sprite = new Sprite2D
            {
                Texture = decalTexture,
                Position = canvasPos,
                RotationDegrees = rotationDegrees,
                Scale = Vector2.One * scale
            };

            StrokeContainer.AddChild(sprite);
            Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
            if (CompositeViewport != null && GodotObject.IsInstanceValid(CompositeViewport))
            {
                CompositeViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
            }
        }

        public void Clear()
        {
            if (IsLocked) return;

            if (StrokeContainer != null)
            {
                foreach (Node child in StrokeContainer.GetChildren())
                {
                    child.QueueFree();
                }
            }

            if (Viewport != null)
            {
                RenderingServer.ViewportSetTransparentBackground(Viewport.GetViewportRid(), true);
                Viewport.RenderTargetClearMode = SubViewport.ClearMode.Once;
                Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
            }
            if (CompositeViewport != null && GodotObject.IsInstanceValid(CompositeViewport))
            {
                CompositeViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
            }
        }

        public void SetVisibility(bool visible)
        {
            IsVisible = visible;
            if (DisplayRect != null) DisplayRect.Visible = visible;
            if (BufferCopy != null) BufferCopy.Visible = visible;
        }

        public void SetOpacity(float opacity)
        {
            Opacity = Mathf.Clamp(opacity, 0.0f, 1.0f);
            UpdateMaterialParameters();
        }

        public void SetBlendMode(LayerBlendMode mode)
        {
            BlendMode = mode;
            UpdateMaterialParameters();
        }

        public void UpdateMaterialParameters()
        {
            if (CompositeMaterial == null) return;
            CompositeMaterial.SetShaderParameter("u_blend_mode", (int)BlendMode);
            CompositeMaterial.SetShaderParameter("u_layer_opacity", Opacity);
        }

        public void CleanUp()
        {
            if (LayerRid.IsValid)
            {
                var rd = RenderingServer.GetRenderingDevice();
                if (rd != null && rd.TextureIsValid(LayerRid))
                {
                    rd.FreeRid(LayerRid);
                }
                LayerRid = new Rid();
            }
            if (BufferCopy != null && GodotObject.IsInstanceValid(BufferCopy))
            {
                BufferCopy.QueueFree();
            }
            if (DisplayRect != null && GodotObject.IsInstanceValid(DisplayRect))
            {
                DisplayRect.QueueFree();
            }
            if (Viewport != null && GodotObject.IsInstanceValid(Viewport))
            {
                Viewport.QueueFree();
            }
        }

        private static ImageTexture _fallbackDabTex;
        private static Texture2D CreateFallbackDabTexture()
        {
            if (_fallbackDabTex != null) return _fallbackDabTex;

            int size = 64;
            var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
            img.Fill(Colors.White);
            _fallbackDabTex = ImageTexture.CreateFromImage(img);
            return _fallbackDabTex;
        }
    }
}
