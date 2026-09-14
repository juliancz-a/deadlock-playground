using Godot;
using System;

namespace DeadlockPlayground.Painter
{
    public partial class DecalStamper : Node
    {
        [Signal] public delegate void DecalPlacedEventHandler(Vector3 worldPos, Vector2 hitUv);
        [Signal] public delegate void DecalBakedEventHandler();

        public Texture2D DecalTexture { get; private set; }
        public float RotationDegrees { get; set; } = 0.0f;
        public float DecalScale { get; set; } = 0.25f; // UV normalized scale

        private Decal _previewDecal;
        private Node _previewContainer;
        private SkinLayerManager _layerManager;
        private MeshPainter3D _painter;

        private Vector3 _currentWorldPos = Vector3.Zero;
        private Vector3 _currentNormal = Vector3.Up;
        private Vector2 _currentHitUv = Vector2.Zero;
        private bool _hasPlacement = false;

        public bool HasPlacement => _hasPlacement;

        public void Setup(Node parentContainer, SkinLayerManager layerManager, MeshPainter3D painter)
        {
            _layerManager = layerManager;
            _painter = painter;

            if (DecalTexture == null)
            {
                DecalTexture = CreateDefaultDecalTexture();
            }

            _previewContainer = parentContainer;
            _previewDecal = new Decal
            {
                Name = "PainterDecalPreview",
                Size = new Vector3(0.5f, 0.5f, 0.5f),
                TextureAlbedo = DecalTexture,
                CullMask = 1,
                Visible = false
            };

            if (_previewContainer != null)
            {
                _previewContainer.AddChild(_previewDecal);
            }
        }

        public static Texture2D CreateDefaultDecalTexture()
        {
            int size = 256;
            var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
            img.Fill(Colors.Transparent);

            Vector2 center = new Vector2(size * 0.5f, size * 0.5f);
            float outerRadius = size * 0.44f;
            float innerRadius = size * 0.19f;

            // Generate 5-point star polygon vertices
            int numPoints = 10;
            Vector2[] starPolygon = new Vector2[numPoints];
            for (int i = 0; i < numPoints; i++)
            {
                float angle = -Mathf.Pi * 0.5f + i * (Mathf.Pi / 5.0f);
                float r = (i % 2 == 0) ? outerRadius : innerRadius;
                starPolygon[i] = center + new Vector2(Mathf.Cos(angle) * r, Mathf.Sin(angle) * r);
            }

            float ringOuter = size * 0.48f;
            float ringInner = size * 0.44f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 pt = new Vector2(x + 0.5f, y + 0.5f);
                    float d = (pt - center).Length();

                    bool inStar = Geometry2D.IsPointInPolygon(pt, starPolygon);
                    bool inRing = d >= ringInner && d <= ringOuter;

                    if (inStar)
                    {
                        img.SetPixel(x, y, new Color(1.0f, 1.0f, 1.0f, 0.95f));
                    }
                    else if (inRing)
                    {
                        img.SetPixel(x, y, new Color(1.0f, 1.0f, 1.0f, 0.85f));
                    }
                }
            }

            return ImageTexture.CreateFromImage(img);
        }

        public bool LoadDecalFromFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath)) return false;

            var img = new Image();
            Error err = img.Load(filePath);
            if (err != Error.Ok)
            {
                GD.PrintErr($"[DecalStamper] Failed to load decal image from {filePath}: {err}");
                return false;
            }

            DecalTexture = ImageTexture.CreateFromImage(img);
            if (_previewDecal != null)
            {
                _previewDecal.TextureAlbedo = DecalTexture;
            }

            GD.Print($"[DecalStamper] Loaded decal texture: {img.GetWidth()}x{img.GetHeight()}");
            return true;
        }

        public void SetDecalTexture(Texture2D texture)
        {
            DecalTexture = texture;
            if (_previewDecal != null)
            {
                _previewDecal.TextureAlbedo = DecalTexture;
            }
        }

        public void PlaceAt(Vector3 worldPos, Vector3 normal, Vector2 uv)
        {
            _currentWorldPos = worldPos;
            _currentNormal = normal;
            _currentHitUv = uv;
            _hasPlacement = true;

            UpdatePreviewTransform();
            if (_previewDecal != null)
            {
                _previewDecal.Visible = true;
            }

            EmitSignal(SignalName.DecalPlaced, worldPos, uv);
        }

        public void UpdatePreviewTransform()
        {
            if (_previewDecal == null || !_hasPlacement) return;

            // Orient decal towards normal
            _previewDecal.GlobalPosition = _currentWorldPos + _currentNormal * 0.01f;

            // Construct basis oriented along normal
            Vector3 forward = _currentNormal.Normalized();
            Vector3 up = MathF.Abs(forward.Y) < 0.99f ? Vector3.Up : Vector3.Right;
            Vector3 right = up.Cross(forward).Normalized();
            up = forward.Cross(right).Normalized();

            Basis basis = new Basis(right, up, forward);
            // Apply rotation around the normal
            basis = basis.Rotated(forward, Mathf.DegToRad(RotationDegrees));

            float size3D = DecalScale * 2.0f;
            _previewDecal.Transform = new Transform3D(basis, _currentWorldPos);
            _previewDecal.Size = new Vector3(size3D, size3D, size3D);
        }

        public bool BakeToActiveLayer()
        {
            if (!_hasPlacement || DecalTexture == null || _layerManager == null)
            {
                GD.PrintErr("[DecalStamper] Cannot bake: no placement or decal texture loaded.");
                return false;
            }

            var activeLayer = _layerManager.ActiveLayer;
            if (activeLayer == null || activeLayer.IsLocked)
            {
                GD.PrintErr("[DecalStamper] Cannot bake: active layer is null or locked.");
                return false;
            }

            bool success = _layerManager.StampDecalToAtlas(_currentHitUv, DecalTexture, RotationDegrees, DecalScale);
            if (success)
            {
                EmitSignal(SignalName.DecalBaked);
            }

            // Hide 3D preview after baking
            if (_previewDecal != null)
            {
                _previewDecal.Visible = false;
            }
            _hasPlacement = false;

            GD.Print($"[DecalStamper] Baked decal to active layer at UV ({_currentHitUv.X:F3}, {_currentHitUv.Y:F3}) (Success: {success})");
            return success;
        }

        public void HidePreview()
        {
            if (_previewDecal != null)
            {
                _previewDecal.Visible = false;
            }
            _hasPlacement = false;
        }

        public override void _ExitTree()
        {
            if (_previewDecal != null && GodotObject.IsInstanceValid(_previewDecal))
            {
                _previewDecal.QueueFree();
            }
            base._ExitTree();
        }
    }
}
