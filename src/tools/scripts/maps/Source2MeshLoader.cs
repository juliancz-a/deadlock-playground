using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveKeyValue;
using DeadlockPlayground.Materials;
using VrfMesh = ValveResourceFormat.ResourceTypes.Mesh;

namespace DeadlockPlayground.Maps;

/// <summary>
/// Loads and instantiates Source 2 .vmdl_c models (both embedded world geometry and shared props)
/// directly into Godot 3D node hierarchies with geometry caching and material binding.
/// </summary>
public static class Source2MeshLoader
{
    private static readonly Dictionary<string, List<(ArrayMesh Mesh, string MaterialPath)>> _modelMeshCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Clears the geometry cache. Call when unloading map stages.
    /// </summary>
    public static void CleanCache()
    {
        _modelMeshCache.Clear();
    }

    /// <summary>
    /// Instantiates a Source 2 .vmdl_c model into a Godot Node3D hierarchy.
    /// Searches in hideoutPackage first (for world geometry), then falls back to baseGamePackage (for props).
    /// </summary>
    public static Node3D InstantiateModel(
        string modelPath,
        Package hideoutPackage,
        Package baseGamePackage,
        MapTextureQuality quality = MapTextureQuality.Medium,
        Package addonPackage = null,
        Color? instanceTint = null,
        string entityOrObjectName = null)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return null;

        var surfaces = GetOrLoadModelSurfaces(modelPath, hideoutPackage, baseGamePackage);
        if (surfaces == null || surfaces.Count == 0) return null;

        var rootNode = new Node3D
        {
            Name = Path.GetFileNameWithoutExtension(modelPath)
        };

        Color effTint = instanceTint ?? Colors.White;

        for (int i = 0; i < surfaces.Count; i++)
        {
            var (arrayMesh, matPath) = surfaces[i];
            if (arrayMesh == null) continue;

            var mat = StageMaterialBuilder.GetOrBuildMaterial(matPath, hideoutPackage, baseGamePackage, quality, addonPackage);

            var mi = new MeshInstance3D
            {
                Name = $"Surface_{i}",
                Mesh = arrayMesh,
                MaterialOverride = mat,
                VisibilityRangeBegin = 0.0f,
                VisibilityRangeEnd = 0.0f,
                VisibilityRangeBeginMargin = 0.0f,
                VisibilityRangeEndMargin = 0.0f,
                VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled,
                ExtraCullMargin = 4.0f
            };

            // Set per-instance shader parameter for tinting
            mi.SetInstanceShaderParameter("instance_tint", effTint);

            // Store comprehensive diagnostics metadata for 3D picking & inspection
            mi.SetMeta("model_path", modelPath);
            mi.SetMeta("material_path", matPath ?? "");
            mi.SetMeta("instance_tint", effTint);
            mi.SetMeta("object_name", entityOrObjectName ?? "");
            mi.SetMeta("surface_index", i);

            var diag = StageMaterialBuilder.GetMaterialDiagnostic(matPath);
            if (diag != null)
            {
                mi.SetMeta("shader_name", diag.ShaderName);
                mi.SetMeta("albedo_tint", diag.AlbedoTintA);
            }

            rootNode.AddChild(mi);
        }

        return rootNode;
    }

    /// <summary>
    /// Loads or retrieves cached geometry surfaces for a .vmdl_c model or .vmesh_c direct mesh.
    /// Ingests both embedded geometry and external referenced meshes (.vmesh_c) for the highest detail LOD.
    /// </summary>
    public static List<(ArrayMesh Mesh, string MaterialPath)> GetOrLoadModelSurfaces(
        string modelPath,
        Package hideoutPackage,
        Package baseGamePackage)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return null;

        if (modelPath.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
        {
            modelPath = modelPath.Substring(9);
        }
        modelPath = modelPath.Trim('"', '\'', ' ');

        string cPath = modelPath.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? modelPath : modelPath + "_c";

        if (_modelMeshCache.TryGetValue(cPath, out var cached) && cached != null)
        {
            return cached;
        }

        PackageEntry entry = null;
        Package targetPkg = null;

        // Check hideout VPK first (world geometry)
        if (hideoutPackage != null)
        {
            entry = hideoutPackage.FindEntry(cPath) ?? hideoutPackage.FindEntry("maps/dl_hideout/" + Path.GetFileName(cPath));
            if (entry != null) targetPkg = hideoutPackage;
        }

        // Fallback to base game VPK (shared props)
        if (entry == null && baseGamePackage != null)
        {
            entry = baseGamePackage.FindEntry(cPath);
            if (entry != null) targetPkg = baseGamePackage;
        }

        if (entry == null || targetPkg == null)
        {
            return null;
        }

        targetPkg.ReadEntry(entry, out byte[] modelBytes);
        using var res = new ValveResourceFormat.Resource();
        using var ms = new MemoryStream(modelBytes);
        res.Read(ms);

        var surfaceList = new List<(ArrayMesh Mesh, string MaterialPath)>();

        // Case A: Direct .vmesh_c mesh resource
        if (res.DataBlock is VrfMesh directMesh)
        {
            DecodeMeshSurfaces(directMesh, surfaceList);
            _modelMeshCache[cPath] = surfaceList;
            return surfaceList;
        }

        // Case B: Composite .vmdl_c model resource
        if (res.DataBlock is Model model)
        {
            int targetLod = model.LodInfo.LowestLevel;

            // 1. Ingest embedded meshes for target LOD (fall back to all if none in LOD)
            var embeddedMeshes = model.GetEmbeddedMeshesForLod(targetLod);
            int embeddedCount = 0;
            foreach (var item in embeddedMeshes)
            {
                DecodeMeshSurfaces(item.Mesh, surfaceList);
                embeddedCount++;
            }
            if (embeddedCount == 0)
            {
                foreach (var item in model.GetEmbeddedMeshes())
                {
                    DecodeMeshSurfaces(item.Mesh, surfaceList);
                }
            }

            // 2. Ingest external referenced meshes (.vmesh_c) for target LOD
            // (e.g. stair rails, frames, and composite furniture)
            var refMeshes = model.GetReferenceMeshNamesForLod(targetLod);
            int refCount = 0;
            foreach (var refMesh in refMeshes)
            {
                var extMesh = LoadExternalMesh(refMesh.MeshName, hideoutPackage, baseGamePackage);
                if (extMesh != null)
                {
                    DecodeMeshSurfaces(extMesh, surfaceList);
                    refCount++;
                }
            }
            if (refCount == 0)
            {
                foreach (var refMesh in model.GetReferenceMeshNamesAndLoD())
                {
                    var extMesh = LoadExternalMesh(refMesh.MeshName, hideoutPackage, baseGamePackage);
                    if (extMesh != null)
                    {
                        DecodeMeshSurfaces(extMesh, surfaceList);
                    }
                }
            }

            _modelMeshCache[cPath] = surfaceList;
            return surfaceList;
        }

        return null;
    }

    private static void DecodeMeshSurfaces(VrfMesh vrfMesh, List<(ArrayMesh Mesh, string MaterialPath)> surfaceList)
    {
        if (vrfMesh?.Data == null) return;

        if (vrfMesh.Data.TryGetValue("m_sceneObjects", out var sos) && sos != null && sos.IsArray)
        {
            for (int s = 0; s < sos.Count; s++)
            {
                var sceneObj = sos[s];
                if (sceneObj.TryGetValue("m_drawCalls", out var dcs) && dcs != null && dcs.IsArray)
                {
                    for (int d = 0; d < dcs.Count; d++)
                    {
                        var drawCall = dcs[d];
                        var arrayMesh = VbibMeshDecoder.DecodeDrawCallToArrayMesh(vrfMesh, drawCall, out string matPath);
                        if (arrayMesh != null)
                        {
                            surfaceList.Add((arrayMesh, matPath));
                        }
                    }
                }
            }
        }
    }

    private static VrfMesh LoadExternalMesh(string meshPath, Package hideoutPackage, Package baseGamePackage)
    {
        if (string.IsNullOrWhiteSpace(meshPath)) return null;

        if (meshPath.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
        {
            meshPath = meshPath.Substring(9);
        }
        meshPath = meshPath.Trim('"', '\'', ' ');

        string cPath = meshPath.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? meshPath : meshPath + "_c";

        PackageEntry entry = null;
        Package targetPkg = null;

        if (hideoutPackage != null)
        {
            entry = hideoutPackage.FindEntry(cPath) ?? hideoutPackage.FindEntry("maps/dl_hideout/" + Path.GetFileName(cPath));
            if (entry != null) targetPkg = hideoutPackage;
        }

        if (entry == null && baseGamePackage != null)
        {
            entry = baseGamePackage.FindEntry(cPath);
            if (entry != null) targetPkg = baseGamePackage;
        }

        if (entry == null || targetPkg == null) return null;

        try
        {
            targetPkg.ReadEntry(entry, out byte[] bytes);
            using var res = new ValveResourceFormat.Resource();
            using var ms = new MemoryStream(bytes);
            res.Read(ms);
            return res.DataBlock as VrfMesh;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Source2MeshLoader] Failed to decode external mesh '{cPath}': {ex.Message}");
            return null;
        }
    }
}
