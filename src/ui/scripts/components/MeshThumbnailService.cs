using System;
using System.Collections.Generic;
using Godot;

namespace DeadlockPlayground.UI
{
    /// <summary>
    /// Shared offscreen SubViewport service that renders static 3/4 front-facing
    /// 128x128 thumbnails for imported and original hero submeshes one per frame,
    /// caching results as ImageTextures keyed by submesh identifier.
    /// </summary>
    public partial class MeshThumbnailService : Node
    {
        private sealed class RenderJob
        {
            public string CacheKey;
            public Mesh Mesh;
            public int SurfaceIndex; // -1 = all surfaces
            public Color Tint;
            public Action<ImageTexture> Callback;
        }

        private SubViewport _viewport;
        private Camera3D _camera;
        private DirectionalLight3D _light;
        private MeshInstance3D _meshInstance;
        private ShaderMaterial _unshadedClayMat;

        private readonly Queue<RenderJob> _queue = new();
        private readonly Dictionary<string, ImageTexture> _cache = new(StringComparer.Ordinal);
        private bool _busy;

        public override void _Ready()
        {
            SetupViewport();
        }

        private void SetupViewport()
        {
            if (_viewport != null) return;

            _viewport = new SubViewport
            {
                Size = new Vector2I(128, 128),
                OwnWorld3D = true,
                World3D = new World3D(),
                TransparentBg = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
                Msaa3D = Viewport.Msaa.Msaa2X
            };
            AddChild(_viewport);

            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.ClearColor,
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.75f, 0.78f, 0.82f),
                AmbientLightEnergy = 1.0f
            };
            _viewport.AddChild(new WorldEnvironment { Environment = env });

            _camera = new Camera3D
            {
                Projection = Camera3D.ProjectionType.Orthogonal,
                Near = 0.01f,
                Far = 100.0f,
                Current = true
            };
            _viewport.AddChild(_camera);

            _light = new DirectionalLight3D
            {
                LightEnergy = 1.2f,
                ShadowEnabled = false
            };
            // Clear Visual Layer 21 (bit 20) per AGENTS.md
            _light.LightCullMask &= ~(1u << 20);
            _light.RotationDegrees = new Vector3(-35f, 30f, 0f);
            _viewport.AddChild(_light);

            _meshInstance = new MeshInstance3D();
            // Clear Visual Layer 21 (bit 20) per AGENTS.md
            _meshInstance.Layers &= ~(1u << 20);
            _viewport.AddChild(_meshInstance);

            // Simple unshaded normal-lit clay shader avoids Forward+ set-3 issues
            var shader = new Shader
            {
                Code = @"
shader_type spatial;
render_mode unshaded, cull_back;
uniform vec4 base_color : source_color = vec4(0.78, 0.82, 0.80, 1.0);
void fragment() {
    vec3 n = normalize(NORMAL);
    float rim = 1.0 - max(dot(n, vec3(0.0, 0.0, 1.0)), 0.0);
    float key = clamp(dot(n, normalize(vec3(0.45, 0.65, 0.60))) * 0.5 + 0.5, 0.22, 1.0);
    ALBEDO = base_color.rgb * key + vec3(0.12, 0.14, 0.16) * pow(rim, 2.0);
}
"
            };
            _unshadedClayMat = new ShaderMaterial { Shader = shader };
        }

        public void ClearCache()
        {
            _queue.Clear();
            _cache.Clear();
        }

        public void RequestThumbnail(string cacheKey, Mesh mesh, int surfaceIndex, Color tint, Action<ImageTexture> callback)
        {
            if (string.IsNullOrEmpty(cacheKey) || mesh == null || callback == null) return;
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                callback(cached);
                return;
            }
            _queue.Enqueue(new RenderJob
            {
                CacheKey = cacheKey,
                Mesh = mesh,
                SurfaceIndex = surfaceIndex,
                Tint = tint,
                Callback = callback
            });
            if (!_busy) _ = ProcessQueueAsync();
        }

        private async System.Threading.Tasks.Task ProcessQueueAsync()
        {
            _busy = true;
            try
            {
                SetupViewport();
                while (_queue.Count > 0 && IsInstanceValid(this) && IsInsideTree())
                {
                    var job = _queue.Dequeue();
                    if (job.Mesh == null || !IsInstanceValid(job.Mesh)) continue;
                    if (_cache.TryGetValue(job.CacheKey, out var existing))
                    {
                        job.Callback?.Invoke(existing);
                        continue;
                    }

                    Mesh renderMesh = job.Mesh;
                    if (job.SurfaceIndex >= 0 && job.SurfaceIndex < job.Mesh.GetSurfaceCount())
                    {
                        var single = new ArrayMesh();
                        var arrays = job.Mesh.SurfaceGetArrays(job.SurfaceIndex);
                        if (arrays != null && arrays.Count > 0)
                        {
                            single.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                            renderMesh = single;
                        }
                    }

                    _meshInstance.Mesh = renderMesh;
                    _unshadedClayMat.SetShaderParameter("base_color", job.Tint);
                    _meshInstance.MaterialOverride = _unshadedClayMat;

                    Aabb aabb = renderMesh.GetAabb();
                    Vector3 center = aabb.Position + aabb.Size * 0.5f;
                    float extent = Mathf.Max(aabb.Size.Length(), 0.05f);

                    // Static 3/4 front-facing camera angle
                    Vector3 dir = new Vector3(0.45f, 0.28f, 0.85f).Normalized();
                    _camera.Position = center + dir * (extent * 1.8f);
                    _camera.LookAt(center, Vector3.Up);
                    _camera.Size = Mathf.Max(aabb.Size.X, Mathf.Max(aabb.Size.Y, aabb.Size.Z)) * 1.35f;
                    if (_camera.Size < 0.05f) _camera.Size = 0.25f;

                    _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

                    if (!IsInstanceValid(this) || !IsInstanceValid(_viewport)) break;
                    var img = _viewport.GetTexture()?.GetImage();
                    if (img != null && !img.IsEmpty())
                    {
                        var tex = ImageTexture.CreateFromImage(img);
                        _cache[job.CacheKey] = tex;
                        job.Callback?.Invoke(tex);
                    }
                    _meshInstance.Mesh = null;
                }
            }
            catch
            {
                // Ignore if modal closed mid-render
            }
            finally
            {
                _busy = false;
            }
        }
    }
}

