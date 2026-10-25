using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using Godot;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveKeyValue;
using DeadlockPlayground.Materials;

namespace DeadlockPlayground.Maps;

/// <summary>
/// High-level map orchestrator responsible for ingesting Deadlock map packages (e.g. dl_hideout.vpk),
/// managing spatial culling, instantiating architecture and props, synthesizing lights,
/// and maintaining the active 3D stage lifecycle.
/// </summary>
public static class Source2MapLoader
{
    private static Package _hideoutPackage;
    private static Node3D _activeStageRoot;

    public static Package LoadedHideoutPackage => _hideoutPackage;
    public static Node3D ActiveStageRoot => _activeStageRoot;
    public static bool IsStageLoaded => GodotObject.IsInstanceValid(_activeStageRoot);

    // Diagnostics
    public static int LastMeshesCount { get; private set; }
    public static int LastMaterialsCount { get; private set; }
    public static int LastLightsCount { get; private set; }
    public static long LastLoadTimeMs { get; private set; }
    public static string LastLoadedPresetId { get; private set; } = string.Empty;

    private static Node3D _hiddenStagePlatform;

    /// <summary>
    /// Guarantees that pak01_dir.vpk, dl_hideout.vpk, and core/pak01_dir.vpk are opened and mounted.
    /// Can be called on cold start before initializing Forge blank canvas or map viewing.
    /// </summary>
    public static (Package baseGamePackage, Package hideoutPackage) EnsureAllPackagesMounted(Package baseGamePackage = null)
    {
        // 1. Mount Hideout VPK
        if (_hideoutPackage == null)
        {
            string mapVpkPath = ResolveHideoutVpkPath();
            if (!string.IsNullOrEmpty(mapVpkPath) && File.Exists(mapVpkPath))
            {
                try
                {
                    _hideoutPackage = new Package();
                    _hideoutPackage.Read(mapVpkPath);
                    GD.Print($"[Source2MapLoader] Successfully mounted hideout VPK: {mapVpkPath}");
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[Source2MapLoader] Failed to open hideout VPK: {ex.Message}");
                }
            }
        }

        // 2. Mount Base Game VPK (pak01_dir.vpk)
        if (baseGamePackage == null)
        {
            string baseVpk = DeadlockPlayground.Particles.HeroParticleService.ResolveVpkPath();
            if (!string.IsNullOrEmpty(baseVpk) && File.Exists(baseVpk))
            {
                try
                {
                    baseGamePackage = new Package();
                    baseGamePackage.Read(baseVpk);
                    GD.Print($"[Source2MapLoader] Successfully mounted base game VPK: {baseVpk}");
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[Source2MapLoader] Failed to open base game VPK: {ex.Message}");
                }
            }
        }

        // 3. Mount Core VPK (core/pak01_dir.vpk)
        if (DeadlockPlayground.Materials.Source2TextureLoader.CorePackage == null)
        {
            string gameRoot = DeadlockPlayground.Particles.HeroParticleService.ResolveGameRootPath();
            if (!string.IsNullOrEmpty(gameRoot))
            {
                string coreVpk = Path.Combine(gameRoot, "game", "core", "pak01_dir.vpk");
                if (File.Exists(coreVpk))
                {
                    try
                    {
                        var corePkg = new Package();
                        corePkg.Read(coreVpk);
                        DeadlockPlayground.Materials.Source2TextureLoader.CorePackage = corePkg;
                        GD.Print($"[Source2MapLoader] Successfully mounted core VPK: {coreVpk}");
                    }
                    catch (Exception ex)
                    {
                        GD.PrintErr($"[Source2MapLoader] Failed to mount core VPK: {ex.Message}");
                    }
                }
            }
        }

        return (baseGamePackage, _hideoutPackage);
    }

    /// <summary>
    /// Loads a Deadlock map stage and mounts it under the specified parent node.
    /// </summary>
    public static Node3D LoadMapStage(
        Node parentNode,
        Package baseGamePackage,
        StagePresetDefinition preset,
        float radiusMeters = 35.0f,
        MapTextureQuality quality = MapTextureQuality.Medium,
        bool centerOnHero = true,
        string customMapVpkPath = null)
    {
        if (parentNode == null)
        {
            GD.PrintErr("[Source2MapLoader] Parent node cannot be null.");
            return null;
        }

        // 1. Teardown existing stage if active
        UnloadCurrentStage();

        var stopwatch = Stopwatch.StartNew();
        LastLoadedPresetId = preset?.Id ?? string.Empty;

        // 2. Ensure all packages mounted
        var (resolvedBase, resolvedHideout) = EnsureAllPackagesMounted(baseGamePackage);
        baseGamePackage = resolvedBase;

        if (_hideoutPackage == null)
        {
            GD.PrintErr("[Source2MapLoader] Hideout VPK package could not be mounted.");
            return null;
        }

        preset ??= StagePresets.HideoutPresets[0];

        // 4. Create root stage node
        _activeStageRoot = new Node3D
        {
            Name = "MapStageRoot"
        };
        parentNode.AddChild(_activeStageRoot);

        // Hide default studio disc platform while map stage is active
        var stagePlatform = parentNode.FindChild("StagePlatform", true, false) as Node3D
                         ?? parentNode.GetTree()?.Root?.FindChild("StagePlatform", true, false) as Node3D;
        if (stagePlatform != null)
        {
            _hiddenStagePlatform = stagePlatform;
            _hiddenStagePlatform.Visible = false;
        }

        int meshesLoaded = 0;
        int lightsCreated = 0;

        var centerSource = preset.SourcePosition;
        float radiusSourceUnits = radiusMeters > 0f ? (radiusMeters / MapCoordinateUtils.InchesToMeters) : -1f;

        // 5. Ingest WorldNode n0.vwnod_c
        var vwnodEntry = _hideoutPackage.FindEntry("maps/dl_hideout/worldnodes/n0.vwnod_c");
        if (vwnodEntry != null)
        {
            _hideoutPackage.ReadEntry(vwnodEntry, out byte[] nodeBytes);
            using var nodeRes = new ValveResourceFormat.Resource();
            using var nodeMs = new MemoryStream(nodeBytes);
            nodeRes.Read(nodeMs);

            if (nodeRes.DataBlock is WorldNode worldNode)
            {
                var architectureRoot = new Node3D { Name = "ArchitectureRoot" };
                _activeStageRoot.AddChild(architectureRoot);

                // A. SceneObjects (Foundations, walls, ceilings, corridors)
                // In Source 2 map nodes, SceneObjects represent core architectural hulls and do not contain separate AABBs.
                // We instantiate all of them so the full hideout building is rendered.
                foreach (var obj in worldNode.SceneObjects)
                {
                    string modelPath = GetKvString(obj, "m_renderableModel") ?? GetKvString(obj, "m_model") ?? GetKvString(obj, "m_renderable");
                    if (string.IsNullOrEmpty(modelPath)) continue;

                    Color objTint = ExtractInstanceTint(obj);
                    string objName = GetKvString(obj, "m_targetname") ?? GetKvString(obj, "m_name") ?? Path.GetFileNameWithoutExtension(modelPath);

                    var modelNode = Source2MeshLoader.InstantiateModel(
                        modelPath,
                        _hideoutPackage,
                        baseGamePackage,
                        quality,
                        instanceTint: objTint,
                        entityOrObjectName: objName);

                    if (modelNode != null)
                    {
                        float[] transformMatrix = GetKvFloatArray(obj, "m_vTransform");
                        if (transformMatrix != null)
                        {
                            modelNode.Transform = MapCoordinateUtils.SourceMatrixToGodotTransform(transformMatrix);
                        }
                        architectureRoot.AddChild(modelNode);
                        meshesLoaded++;
                    }
                }

                // B. AggregateSceneObjects (Pre-merged architectural trim, bricks, mouldings)
                foreach (var agg in worldNode.AggregateSceneObjects)
                {
                    string modelPath = GetKvString(agg, "m_renderableModel") ?? GetKvString(agg, "m_model") ?? GetKvString(agg, "m_renderable");
                    if (string.IsNullOrEmpty(modelPath)) continue;

                    Color aggTint = ExtractInstanceTint(agg);
                    string aggName = Path.GetFileNameWithoutExtension(modelPath);

                    var modelNode = Source2MeshLoader.InstantiateModel(
                        modelPath,
                        _hideoutPackage,
                        baseGamePackage,
                        quality,
                        instanceTint: aggTint,
                        entityOrObjectName: aggName);

                    if (modelNode != null)
                    {
                        architectureRoot.AddChild(modelNode);
                        meshesLoaded++;
                    }
                }
            }
        }

        // 6. Ingest Entity Lump (Props & Lights)
        var ventsEntry = _hideoutPackage.FindEntry("maps/dl_hideout/entities/default_ents.vents_c");
        if (ventsEntry != null)
        {
            _hideoutPackage.ReadEntry(ventsEntry, out byte[] ventBytes);
            using var ventRes = new ValveResourceFormat.Resource();
            using var ventMs = new MemoryStream(ventBytes);
            ventRes.Read(ventMs);

            if (ventRes.DataBlock is EntityLump entityLump)
            {
                var entities = entityLump.GetEntities();

                // A. Props (Furniture, tables, candles, decorations)
                var propsRoot = new Node3D { Name = "PropsRoot" };
                _activeStageRoot.AddChild(propsRoot);

                foreach (var ent in entities)
                {
                    string cls = ent.TryGetValue("classname", out var cVal) && cVal != null ? cVal.ToString() : "";
                    if (!cls.StartsWith("prop_", StringComparison.OrdinalIgnoreCase) &&
                        !cls.StartsWith("citadel_prop", StringComparison.OrdinalIgnoreCase) &&
                        !cls.Contains("button", StringComparison.OrdinalIgnoreCase) &&
                        !cls.Contains("badge", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string propModel = GetKvString(ent, "model");
                    if (string.IsNullOrEmpty(propModel)) continue;

                    var origin = ent.GetVector3Property("origin", System.Numerics.Vector3.Zero);

                    // Spatial distance check
                    if (radiusMeters > 0f && !StageChunkManager.IsWithinRadiusMeters(origin, centerSource, radiusMeters))
                    {
                        continue;
                    }

                    Color entTint = ExtractEntityTint(ent);
                    string entName = ent.TryGetValue("targetname", out var tnVal) && tnVal != null ? tnVal.ToString() : cls;

                    var propNode = Source2MeshLoader.InstantiateModel(
                        propModel,
                        _hideoutPackage,
                        baseGamePackage,
                        quality,
                        instanceTint: entTint,
                        entityOrObjectName: entName);

                    if (propNode != null)
                    {
                        var angles = ent.GetVector3Property("angles", System.Numerics.Vector3.Zero);
                        var scales = ent.GetVector3Property("scales", System.Numerics.Vector3.One);
                        propNode.Transform = MapCoordinateUtils.SourceEntityToGodotTransform(origin, angles, scales);
                        propsRoot.AddChild(propNode);
                        meshesLoaded++;
                    }
                }

                // B. Lights (OmniLight3D, SpotLight3D, DirectionalLight3D)
                var lightsNode = Source2LightSynthesizer.SynthesizeLights(
                    entities,
                    filterCenterGodot: preset.GodotPosition,
                    filterRadiusMeters: radiusMeters > 0f ? radiusMeters + 10f : -1f);

                if (lightsNode != null)
                {
                    _activeStageRoot.AddChild(lightsNode);
                    lightsCreated = lightsNode.GetChildCount();
                }
            }
        }

        // 7. Center Stage on Hero Spot
        if (centerOnHero)
        {
            _activeStageRoot.Position = -preset.GodotPosition;
        }

        // 8. Configure Scene Environment for 3D Stage Mode
        SceneEnvironmentManager.Is3DMapStageActive = true;
        var tree = parentNode.GetTree();
        var worldVp = parentNode as SubViewport ?? tree?.Root?.FindChild("WorldViewport", true, false) as SubViewport;
        var worldEnv = tree?.Root?.FindChild("WorldEnvironment", true, false) as WorldEnvironment;
        var bgRect = tree?.Root?.FindChild("BgColorRect", true, false) as ColorRect;
        var bgTexRect = tree?.Root?.FindChild("BgTextureRect", true, false) as TextureRect;
        SceneEnvironmentManager.ApplyEnvironment(tree?.Root, worldVp, worldEnv, bgRect, bgTexRect, _hiddenStagePlatform);

        stopwatch.Stop();
        LastMeshesCount = meshesLoaded;
        LastMaterialsCount = StageMaterialBuilder.CachedMaterialCount;
        LastLightsCount = lightsCreated;
        LastLoadTimeMs = stopwatch.ElapsedMilliseconds;

        GD.Print($"[Source2MapLoader] Stage loaded successfully in {LastLoadTimeMs} ms! (Meshes: {LastMeshesCount}, Materials: {LastMaterialsCount}, Lights: {LastLightsCount})");

        return _activeStageRoot;
    }

    /// <summary>
    /// Unloads the active map stage and frees all VRAM and mesh resources immediately.
    /// </summary>
    public static void UnloadCurrentStage()
    {
        if (GodotObject.IsInstanceValid(_activeStageRoot))
        {
            _activeStageRoot.GetParent()?.RemoveChild(_activeStageRoot);
            _activeStageRoot.QueueFree();
            _activeStageRoot = null;
        }

        if (GodotObject.IsInstanceValid(_hiddenStagePlatform))
        {
            _hiddenStagePlatform.Visible = true;
            _hiddenStagePlatform = null;
        }

        SceneEnvironmentManager.Is3DMapStageActive = false;
        var tree = Engine.GetMainLoop() as SceneTree;
        var worldVp = tree?.Root?.FindChild("WorldViewport", true, false) as SubViewport;
        var worldEnv = tree?.Root?.FindChild("WorldEnvironment", true, false) as WorldEnvironment;
        var bgRect = tree?.Root?.FindChild("BgColorRect", true, false) as ColorRect;
        var bgTexRect = tree?.Root?.FindChild("BgTextureRect", true, false) as TextureRect;
        SceneEnvironmentManager.ApplyEnvironment(tree?.Root, worldVp, worldEnv, bgRect, bgTexRect, _hiddenStagePlatform);

        Source2MeshLoader.CleanCache();
        StageMaterialBuilder.CleanCache();
        Source2TextureLoader.CleanMapCache();

        LastMeshesCount = 0;
        LastMaterialsCount = 0;
        LastLightsCount = 0;
    }

    /// <summary>
    /// Locates dl_hideout.vpk by searching known Steam and Citadel game paths.
    /// </summary>
    public static string ResolveHideoutVpkPath()
    {
        // 1. Check user://settings.cfg if user set custom game path
        var config = new ConfigFile();
        if (config.Load("user://settings.cfg") == Error.Ok)
        {
            string gamePath = (string)config.GetValue("Paths", "GamePath", "");
            if (!string.IsNullOrEmpty(gamePath))
            {
                string p1 = Path.Combine(gamePath, "game", "citadel", "maps", "dl_hideout.vpk");
                if (File.Exists(p1)) return p1;
                string p2 = Path.Combine(gamePath, "citadel", "maps", "dl_hideout.vpk");
                if (File.Exists(p2)) return p2;
                string p3 = Path.Combine(gamePath, "maps", "dl_hideout.vpk");
                if (File.Exists(p3)) return p3;
            }
        }

        // 2. Check HeroParticleService resolved VPK path
        string baseVpk = DeadlockPlayground.Particles.HeroParticleService.ResolveVpkPath();
        if (!string.IsNullOrEmpty(baseVpk) && File.Exists(baseVpk))
        {
            string candidate = Path.Combine(Path.GetDirectoryName(baseVpk), "maps", "dl_hideout.vpk");
            if (File.Exists(candidate)) return candidate;
        }

        // 3. Fallback to common Steam libraries
        string[] searchPaths = new[]
        {
            @"E:\SteamLibrary\steamapps\common\Deadlock\game\citadel\maps\dl_hideout.vpk",
            @"C:\Program Files (x86)\Steam\steamapps\common\Deadlock\game\citadel\maps\dl_hideout.vpk",
            @"D:\SteamLibrary\steamapps\common\Deadlock\game\citadel\maps\dl_hideout.vpk"
        };

        foreach (var p in searchPaths)
        {
            if (File.Exists(p)) return p;
        }

        return string.Empty;
    }

    private static string GetKvString(KVObject obj, string key)
    {
        if (obj != null && obj.TryGetValue(key, out var val) && val != null)
        {
            return val.ToString();
        }
        return null;
    }

    private static System.Numerics.Vector3 GetKvVector3(KVObject obj, string key)
    {
        if (obj != null && obj.TryGetValue(key, out var val) && val != null)
        {
            if (val.IsArray && val.Count >= 3)
            {
                if (float.TryParse(val[0].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(val[1].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                    float.TryParse(val[2].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                {
                    return new System.Numerics.Vector3(x, y, z);
                }
            }
            var parts = val.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 &&
                float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float px) &&
                float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float py) &&
                float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float pz))
            {
                return new System.Numerics.Vector3(px, py, pz);
            }
        }
        return System.Numerics.Vector3.Zero;
    }

    private static float[] GetKvFloatArray(KVObject obj, string key)
    {
        if (obj != null && obj.TryGetValue(key, out var val) && val != null)
        {
            var list = new List<float>();
            FlattenKvArray(val, list);
            return list.Count > 0 ? list.ToArray() : null;
        }
        return null;
    }

    private static void FlattenKvArray(KVObject val, List<float> list)
    {
        if (val == null) return;
        if (val.IsArray)
        {
            for (int i = 0; i < val.Count; i++)
            {
                var item = val[i];
                if (item != null && item.IsArray)
                {
                    FlattenKvArray(item, list);
                }
                else if (item != null && float.TryParse(item.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                {
                    list.Add(f);
                }
            }
        }
        else if (float.TryParse(val.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
        {
            list.Add(f);
        }
    }

    /// <summary>
    /// Extracts a per-instance color tint from a SceneObject or AggregateSceneObject.
    /// Handles byte 0..255 arrays, normalized 0..1 vectors, and string formats.
    /// </summary>
    public static Color ExtractInstanceTint(KVObject obj)
    {
        if (obj == null) return Colors.White;

        string[] tintKeys = { "m_vTintColor", "m_vColorTint", "rendercolor", "m_cRenderColor" };
        foreach (var key in tintKeys)
        {
            if (!obj.ContainsKey(key)) continue;

            try
            {
                var arr = GetKvFloatArray(obj, key);
                if (arr != null && arr.Length >= 3)
                {
                    float r = arr[0];
                    float g = arr[1];
                    float b = arr[2];
                    float a = arr.Length >= 4 ? arr[3] : 1.0f;

                    if (r > 0.001f || g > 0.001f || b > 0.001f)
                    {
                        float maxComp = Math.Max(r, Math.Max(g, b));
                        float scale = maxComp > 1.0f ? (1.0f / 255.0f) : 1.0f;
                        return new Color(
                            Mathf.Clamp(r * scale, 0f, 1f),
                            Mathf.Clamp(g * scale, 0f, 1f),
                            Mathf.Clamp(b * scale, 0f, 1f),
                            a > 0.001f ? (a > 1.0f ? Mathf.Clamp(a * scale, 0f, 1f) : Mathf.Clamp(a, 0f, 1f)) : 1.0f
                        );
                    }
                }
                else
                {
                    string s = GetKvString(obj, key);
                    if (!string.IsNullOrEmpty(s))
                    {
                        var parts = s.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3 &&
                            float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r) &&
                            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float g) &&
                            float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
                        {
                            float maxComp = Math.Max(r, Math.Max(g, b));
                            float scale = maxComp > 1.0f ? (1.0f / 255.0f) : 1.0f;
                            float a = 1.0f;
                            if (parts.Length >= 4 && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedA))
                            {
                                a = parsedA > 1.0f ? parsedA * scale : parsedA;
                            }
                            return new Color(Mathf.Clamp(r * scale, 0f, 1f), Mathf.Clamp(g * scale, 0f, 1f), Mathf.Clamp(b * scale, 0f, 1f), a);
                        }
                    }
                }
            }
            catch { }
        }

        return Colors.White;
    }

    /// <summary>
    /// Extracts a color tint from an EntityLump.Entity using VRF's GetRenderTint() and fallback keys.
    /// </summary>
    public static Color ExtractEntityTint(EntityLump.Entity ent)
    {
        if (ent == null) return Colors.White;

        try
        {
            if (ent.ContainsKey("rendercolor") || ent.ContainsKey("m_vTintColor") || ent.ContainsKey("m_vColorTint"))
            {
                var v4 = ent.GetRenderTint();
                if (v4.X > 0.001f || v4.Y > 0.001f || v4.Z > 0.001f)
                {
                    float a = v4.W > 0.001f ? v4.W : 1.0f;
                    return new Color(v4.X, v4.Y, v4.Z, a);
                }
            }
        }
        catch { }

        return ExtractInstanceTint(ent);
    }
}
