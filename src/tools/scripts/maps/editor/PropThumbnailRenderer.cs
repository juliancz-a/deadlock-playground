using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;
using SteamDatabase.ValvePak;
using DeadlockPlayground.Materials;

namespace DeadlockPlayground.Maps.Editor;

/// <summary>
/// On-demand asynchronous 3D thumbnail renderer for Deadlock map catalog props.
/// Lazily renders 128x128 preview icons using an isolated offscreen SubViewport (OwnWorld3D = true)
/// and caches them in user://cache/prop_thumbs/ for fast, silky-smooth prop browsing.
/// </summary>
public static class PropThumbnailRenderer
{
    private const int ThumbnailSize = 128;
    private const string CacheDir = "user://cache/prop_thumbs";

    private static readonly Dictionary<string, Texture2D> _memoryCache = new(StringComparer.OrdinalIgnoreCase);
    private static SubViewport _offscreenViewport;
    private static Camera3D _camera;
    private static Node3D _stageRoot;
    private static DirectionalLight3D _keyLight;
    private static DirectionalLight3D _fillLight;
    private static DirectionalLight3D _backLight;
    private static bool _isInitialized = false;

    private static void EnsureInitialized(Node sceneRoot)
    {
        if (_isInitialized && GodotObject.IsInstanceValid(_offscreenViewport) && _offscreenViewport.IsInsideTree()) return;

        // Ensure disk cache dir exists
        string globalCachePath = ProjectSettings.GlobalizePath(CacheDir);
        if (!Directory.Exists(globalCachePath))
        {
            Directory.CreateDirectory(globalCachePath);
        }

        // Create isolated offscreen SubViewport
        _offscreenViewport = new SubViewport
        {
            Name = "PropThumbnailViewport",
            Size = new Vector2I(ThumbnailSize, ThumbnailSize),
            TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            OwnWorld3D = true
        };

        var world = new World3D();
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1.0f),
            AmbientLightColor = new Color(0.75f, 0.80f, 0.90f),
            AmbientLightEnergy = 0.85f
        };
        world.Environment = env;
        _offscreenViewport.World3D = world;

        _stageRoot = new Node3D { Name = "PropStageRoot" };
        _offscreenViewport.AddChild(_stageRoot);

        // 3-Point Studio Lighting
        _keyLight = new DirectionalLight3D
        {
            Name = "KeyLight",
            LightColor = new Color(1.0f, 0.98f, 0.95f),
            LightEnergy = 1.6f,
            RotationDegrees = new Vector3(-35, 45, 0)
        };
        _stageRoot.AddChild(_keyLight);

        _fillLight = new DirectionalLight3D
        {
            Name = "FillLight",
            LightColor = new Color(0.85f, 0.90f, 1.0f),
            LightEnergy = 0.9f,
            RotationDegrees = new Vector3(-20, -135, 0)
        };
        _stageRoot.AddChild(_fillLight);

        _backLight = new DirectionalLight3D
        {
            Name = "BackLight",
            LightColor = new Color(1.0f, 1.0f, 1.0f),
            LightEnergy = 1.1f,
            RotationDegrees = new Vector3(30, 0, 0)
        };
        _stageRoot.AddChild(_backLight);

        // Offscreen Camera3D
        _camera = new Camera3D
        {
            Name = "ThumbnailCamera",
            Fov = 40.0f,
            Current = true
        };
        _stageRoot.AddChild(_camera);

        // Always attach viewport directly to tree root Window so its rendering is NEVER disabled by UI visibility
        var tree = sceneRoot?.GetTree() ?? Engine.GetMainLoop() as SceneTree;
        var rootWindow = tree?.Root;
        if (rootWindow != null)
        {
            rootWindow.AddChild(_offscreenViewport);
        }
        else if (sceneRoot != null)
        {
            sceneRoot.AddChild(_offscreenViewport);
        }

        _isInitialized = true;
    }

    /// <summary>
    /// Checks RAM and disk cache for an already-rendered thumbnail. Instant return with 0 lag.
    /// </summary>
    public static Texture2D GetCachedThumbnail(string vmdlPath)
    {
        if (string.IsNullOrWhiteSpace(vmdlPath)) return null;

        string cacheKey = vmdlPath.Replace('/', '_').Replace('\\', '_').Replace(".vmdl", "");

        // 1. Check RAM Cache
        if (_memoryCache.TryGetValue(cacheKey, out var memTex) && memTex != null)
        {
            return memTex;
        }

        // 2. Check Disk Cache
        string diskPath = $"{CacheDir}/{cacheKey}.png";
        string globalDiskPath = ProjectSettings.GlobalizePath(diskPath);
        if (File.Exists(globalDiskPath))
        {
            var img = new Image();
            if (img.Load(globalDiskPath) == Error.Ok)
            {
                var tex = ImageTexture.CreateFromImage(img);
                _memoryCache[cacheKey] = tex;
                return tex;
            }
        }

        return null;
    }

    /// <summary>
    /// Asynchronously renders a 3D model snapshot using the offscreen SubViewport,
    /// waiting for the rendering pipeline to draw, and caches the result to disk and memory.
    /// </summary>
    public static async Task<Texture2D> RenderThumbnailAsync(
        string vmdlPath,
        Package baseGamePackage,
        Package hideoutPackage,
        Node sceneRoot)
    {
        if (string.IsNullOrWhiteSpace(vmdlPath) || sceneRoot == null || !GodotObject.IsInstanceValid(sceneRoot)) return null;

        // Check cache first
        var cached = GetCachedThumbnail(vmdlPath);
        if (cached != null) return cached;

        string cacheKey = vmdlPath.Replace('/', '_').Replace('\\', '_').Replace(".vmdl", "");
        string diskPath = $"{CacheDir}/{cacheKey}.png";
        string globalDiskPath = ProjectSettings.GlobalizePath(diskPath);

        EnsureInitialized(sceneRoot);

        if (_offscreenViewport == null || !GodotObject.IsInstanceValid(_offscreenViewport) || !_offscreenViewport.IsInsideTree())
        {
            return null;
        }

        Node3D modelNode = null;
        try
        {
            if (baseGamePackage == null || hideoutPackage == null)
            {
                var (bPkg, hPkg) = Source2MapLoader.EnsureAllPackagesMounted(baseGamePackage);
                baseGamePackage ??= bPkg;
                hideoutPackage ??= hPkg ?? Source2MapLoader.LoadedHideoutPackage;
            }

            modelNode = Source2MeshLoader.InstantiateModel(
                vmdlPath,
                hideoutPackage,
                baseGamePackage,
                MapTextureQuality.Low);

            if (modelNode == null) return null;

            _stageRoot.AddChild(modelNode);

            // Calculate combined AABB across all mesh instances
            Aabb totalAabb = new Aabb();
            bool hasAabb = false;

            foreach (var child in modelNode.FindChildren("*", "MeshInstance3D", true, false))
            {
                if (child is MeshInstance3D mi && mi.Mesh != null)
                {
                    var miAabb = mi.Mesh.GetAabb();
                    if (!hasAabb)
                    {
                        totalAabb = miAabb;
                        hasAabb = true;
                    }
                    else
                    {
                        totalAabb = totalAabb.Merge(miAabb);
                    }
                }
            }

            if (!hasAabb || totalAabb.Size.LengthSquared() < 1e-4f)
            {
                totalAabb = new Aabb(new Vector3(-0.5f, 0, -0.5f), Vector3.One);
            }

            Vector3 center = totalAabb.GetCenter();
            float maxDim = MathF.Max(totalAabb.Size.X, MathF.Max(totalAabb.Size.Y, totalAabb.Size.Z));
            maxDim = MathF.Max(maxDim, 0.2f);
            float radius = (maxDim * 0.5f) * 1.35f;

            float fovRad = Mathf.DegToRad(_camera.Fov);
            float dist = radius / MathF.Sin(fovRad * 0.5f);
            dist = Mathf.Clamp(dist, 0.4f, 40.0f);

            // Elevated 3/4 isometric preview angle
            Vector3 camDir = new Vector3(0.55f, 0.40f, 0.70f).Normalized();
            Vector3 camPos = center + camDir * dist;

            _camera.Position = camPos;
            _camera.LookAt(center, Vector3.Up);

            // Enable viewport rendering and await 2 full engine frame passes
            _offscreenViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;

            var tree = sceneRoot.GetTree() ?? Engine.GetMainLoop() as SceneTree;
            if (tree != null)
            {
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                await tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                await tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            }

            var vpTex = _offscreenViewport.GetTexture();
            Image img = vpTex?.GetImage();
            _offscreenViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;

            if (img != null && !img.IsEmpty())
            {
                img.SavePng(globalDiskPath);
                var tex = ImageTexture.CreateFromImage(img);
                _memoryCache[cacheKey] = tex;
                return tex;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[PropThumbnailRenderer] Error rendering snapshot for {vmdlPath}: {ex.Message}");
        }
        finally
        {
            if (modelNode != null && GodotObject.IsInstanceValid(modelNode))
            {
                if (modelNode.GetParent() != null)
                {
                    modelNode.GetParent().RemoveChild(modelNode);
                }
                modelNode.QueueFree();
            }
        }

        return null;
    }

    /// <summary>
    /// Synchronous thumbnail fetcher with immediate fallback if not yet rendered.
    /// </summary>
    public static Texture2D GetOrRenderThumbnail(
        string vmdlPath,
        Package baseGamePackage,
        Package hideoutPackage,
        Node sceneRoot)
    {
        return GetCachedThumbnail(vmdlPath);
    }

    /// <summary>
    /// Clears memory cache of thumbnails.
    /// </summary>
    public static void ClearMemoryCache()
    {
        _memoryCache.Clear();
    }
}
