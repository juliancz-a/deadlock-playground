using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveKeyValue;
using DeadlockPlayground.Painter;

namespace DeadlockPlayground.Maps.Editor;

/// <summary>
/// Options controlling the compilation and export of a Deadlock map project into a playable Source 2 .vpk.
/// </summary>
public class MapCompileOptions
{
    /// <summary>
    /// If true, writes an addon mod VPK to game/citadel/addons/pakXX_dir.vpk.
    /// If false, writes a full standalone map to game/citadel/maps/{MapName}.vpk.
    /// </summary>
    public bool ExportAsAddon { get; set; } = true;

    /// <summary>
    /// Preferred addon VPK index (e.g. 2 for pak02_dir.vpk). If null, automatically resolves next free index.
    /// </summary>
    public int? TargetVpkIndex { get; set; } = null;

    /// <summary>
    /// Coordinate offset applied in Source 2 units (inches). For blank maps, offsets to clean coordinates.
    /// </summary>
    public System.Numerics.Vector3 SourceCoordinateOffset { get; set; } = System.Numerics.Vector3.Zero;
}

/// <summary>
/// Result report returned after compiling a map project into a Source 2 .vpk.
/// </summary>
public class MapCompileResult
{
    public bool Success { get; set; }
    public string OutputVpkPath { get; set; } = "";
    public string ConsoleLaunchCommand { get; set; } = "";
    public int PropsCount { get; set; }
    public int LightsCount { get; set; }
    public int SpawnsCount { get; set; }
    public string Message { get; set; } = "";
    public string ErrorDetails { get; set; } = "";
}

/// <summary>
/// Binary compiler that transforms a Deadlock Playground map project (.dpmap) into an authentic,
/// playable Source 2 .vpk map with Havok physics collision (solid=6), real-time lighting, and player spawns.
/// </summary>
public static class Source2MapCompiler
{
    public static MapCompileResult CompileMap(
        MapProject project,
        string deadlockGamePath,
        MapCompileOptions options = null)
    {
        options ??= new MapCompileOptions();
        var result = new MapCompileResult();

        if (project == null)
        {
            result.Message = "Project data is null.";
            return result;
        }

        if (string.IsNullOrEmpty(deadlockGamePath) || !Directory.Exists(deadlockGamePath))
        {
            result.Message = "Invalid Deadlock game installation path.";
            return result;
        }

        string donorVpkPath = Path.Combine(deadlockGamePath, "game", "citadel", "maps", "dl_hideout.vpk");
        if (!File.Exists(donorVpkPath))
        {
            result.Message = $"Donor map dl_hideout.vpk not found at: {donorVpkPath}";
            return result;
        }

        try
        {
            GD.Print($"[Source2MapCompiler] Beginning map compilation for project '{project.MapName}' ({project.Objects.Count} objects)...");

            using var donorPackage = new Package();
            donorPackage.Read(donorVpkPath);

            // 1. Ingest donor Entity Lump default_ents.vents_c
            var ventsEntry = donorPackage.FindEntry("maps/dl_hideout/entities/default_ents.vents_c");
            if (ventsEntry == null)
            {
                result.Message = "Donor map is missing maps/dl_hideout/entities/default_ents.vents_c";
                return result;
            }

            donorPackage.ReadEntry(ventsEntry, out byte[] ventBytes);
            using var ventRes = new ValveResourceFormat.Resource();
            using var ventMs = new MemoryStream(ventBytes);
            ventRes.Read(ventMs);

            if (ventRes.DataBlock is not EntityLump entityLump)
            {
                result.Message = "Could not parse EntityLump from donor map default_ents.vents_c";
                return result;
            }

            var ekv = entityLump.Data["m_entityKeyValues"];

            // Find a donor entity entry to borrow a valid m_connections array structure
            KVObject donorConnections = null;
            foreach (var item in ekv)
            {
                if (item.Value.ContainsKey("m_connections"))
                {
                    donorConnections = item.Value["m_connections"];
                    break;
                }
            }

            // If blank map mode: keep only worldspawn (entity 0) and remove all existing hideout entities
            bool isBlank = project.BaseStageMode.Equals("blank", StringComparison.OrdinalIgnoreCase);
            if (isBlank)
            {
                var worldspawnEntry = ekv.FirstOrDefault();
                ekv.Clear();
                if (worldspawnEntry.Value != null)
                {
                    ekv.Add(worldspawnEntry.Value);
                }
            }

            int propsCount = 0;
            int lightsCount = 0;
            int spawnsCount = 0;
            int hammerId = 700000;

            // 2. Inject User-Placed Objects into Entity Lump
            foreach (var obj in project.Objects)
            {
                var (origin, angles, scale) = MapCoordinateUtils.GodotToSourceTransform(obj.GetTransform3D());
                origin += options.SourceCoordinateOffset;
                hammerId++;

                var values = new KVObject();

                if (obj.EntityType == MapEntitySpawner.EntityPlayerStart)
                {
                    values.Add("classname", new KVObject("info_player_start"));
                    values.Add("targetname", new KVObject(obj.Name));
                    values.Add("origin", new KVObject($"{origin.X:F2} {origin.Y:F2} {origin.Z:F2}"));
                    values.Add("angles", new KVObject($"{angles.X:F2} {angles.Y:F2} {angles.Z:F2}"));
                    values.Add("hammeruniqueid", new KVObject(hammerId));
                    spawnsCount++;
                }
                else if (obj.EntityType is MapEntitySpawner.EntityLightOmni or MapEntitySpawner.EntityLightSpot or MapEntitySpawner.EntityLightSun)
                {
                    values.Add("classname", new KVObject(obj.EntityType));
                    values.Add("targetname", new KVObject(obj.Name));
                    values.Add("origin", new KVObject($"{origin.X:F2} {origin.Y:F2} {origin.Z:F2}"));
                    values.Add("angles", new KVObject($"{angles.X:F2} {angles.Y:F2} {angles.Z:F2}"));
                    values.Add("color", new KVObject($"{(int)(obj.LightColor.R * 255)} {(int)(obj.LightColor.G * 255)} {(int)(obj.LightColor.B * 255)}"));
                    values.Add("brightness", new KVObject((obj.LightEnergy * 100f).ToString("F0")));
                    values.Add("range", new KVObject((obj.LightRange / MapCoordinateUtils.InchesToMeters).ToString("F0")));
                    if (obj.EntityType == MapEntitySpawner.EntityLightSpot)
                    {
                        values.Add("spot_fov", new KVObject(obj.SpotAngle.ToString("F1")));
                    }
                    values.Add("hammeruniqueid", new KVObject(hammerId));
                    lightsCount++;
                }
                else
                {
                    // Default / prop_dynamic
                    string propModel = string.IsNullOrWhiteSpace(obj.ModelPath)
                        ? "models/props_gameplay/wooden_crate_destructible.vmdl"
                        : obj.ModelPath;

                    // Ensure .vmdl extension (strip _c if present)
                    if (propModel.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase))
                    {
                        propModel = propModel[..^2];
                    }

                    values.Add("classname", new KVObject("prop_dynamic"));
                    values.Add("targetname", new KVObject(obj.Name));
                    values.Add("model", new KVObject(propModel));
                    values.Add("origin", new KVObject($"{origin.X:F2} {origin.Y:F2} {origin.Z:F2}"));
                    values.Add("angles", new KVObject($"{angles.X:F2} {angles.Y:F2} {angles.Z:F2}"));
                    values.Add("scales", new KVObject($"{scale.X:F3} {scale.Y:F3} {scale.Z:F3}"));
                    values.Add("solid", new KVObject(obj.SolidMode.ToString()));
                    values.Add("rendercolor", new KVObject($"{(int)(obj.ColorTint.R * 255)} {(int)(obj.ColorTint.G * 255)} {(int)(obj.ColorTint.B * 255)} 255"));
                    values.Add("renderamt", new KVObject(255));
                    values.Add("fademindist", new KVObject(-1));
                    values.Add("fademaxdist", new KVObject(0));
                    values.Add("hammeruniqueid", new KVObject(hammerId));

                    // Add custom KV3 attributes if provided
                    if (obj.CustomKv != null)
                    {
                        foreach (var kvp in obj.CustomKv)
                        {
                            if (!values.ContainsKey(kvp.Key))
                            {
                                values.Add(kvp.Key, new KVObject(kvp.Value));
                            }
                        }
                    }

                    propsCount++;
                }

                var kv3Data = new KVObject();
                kv3Data.Add("version", new KVObject(1));
                kv3Data.Add("values", values);
                kv3Data.Add("attributes", new KVObject());

                var entityEntry = new KVObject();
                entityEntry.Add("m_connections", donorConnections ?? new KVObject());
                entityEntry.Add("keyValues3Data", kv3Data);

                ekv.Add(entityEntry);
            }

            // Ensure at least one spawn point exists so the player spawns reliably
            if (spawnsCount == 0)
            {
                hammerId++;
                var fallbackSpawnVals = new KVObject();
                fallbackSpawnVals.Add("classname", new KVObject("info_player_start"));
                fallbackSpawnVals.Add("targetname", new KVObject("default_forge_spawn"));
                fallbackSpawnVals.Add("origin", new KVObject("0 0 50"));
                fallbackSpawnVals.Add("angles", new KVObject("0 0 0"));
                fallbackSpawnVals.Add("hammeruniqueid", new KVObject(hammerId));

                var fbKv3 = new KVObject();
                fbKv3.Add("version", new KVObject(1));
                fbKv3.Add("values", fallbackSpawnVals);
                fbKv3.Add("attributes", new KVObject());

                var fbEntry = new KVObject();
                fbEntry.Add("m_connections", donorConnections ?? new KVObject());
                fbEntry.Add("keyValues3Data", fbKv3);
                ekv.Add(fbEntry);
                spawnsCount++;
            }

            // 3. Serialize modified EntityLump back into binary KV3
            using var compiledVentsMs = new MemoryStream();
            ventRes.Serialize(compiledVentsMs);
            byte[] compiledVentsBytes = compiledVentsMs.ToArray();

            // 4. Handle WorldNode n0.vwnod_c for Blank Canvas maps
            byte[] compiledWorldNodeBytes = null;
            if (isBlank)
            {
                var vwnodEntry = donorPackage.FindEntry("maps/dl_hideout/worldnodes/n0.vwnod_c");
                if (vwnodEntry != null)
                {
                    donorPackage.ReadEntry(vwnodEntry, out byte[] wnBytes);
                    using var wnRes = new ValveResourceFormat.Resource();
                    using var wnMs = new MemoryStream(wnBytes);
                    wnRes.Read(wnMs);

                    if (wnRes.DataBlock is WorldNode worldNode)
                    {
                        // Clear the baked hideout geometry hulls so only custom placed props and architecture are rendered
                        if (worldNode.Data.ContainsKey("m_sceneObjects"))
                        {
                            worldNode.Data["m_sceneObjects"] = new KVObject();
                        }
                        if (worldNode.Data.ContainsKey("m_aggregateSceneObjects"))
                        {
                            worldNode.Data["m_aggregateSceneObjects"] = new KVObject();
                        }

                        using var compiledWnMs = new MemoryStream();
                        wnRes.Serialize(compiledWnMs);
                        compiledWorldNodeBytes = compiledWnMs.ToArray();
                    }
                }
            }

            // 5. Package into Target VPK Archive
            string outVpkPath;
            string consoleCmd;

            if (options.ExportAsAddon)
            {
                string addonsDir = Path.Combine(deadlockGamePath, "game", "citadel", "addons");
                if (!Directory.Exists(addonsDir))
                {
                    Directory.CreateDirectory(addonsDir);
                }

                int vpkIdx = options.TargetVpkIndex ?? VpkIndexResolver.ResolveNextIndex(addonsDir);
                string vpkName = $"pak{vpkIdx:D2}_dir.vpk";
                outVpkPath = Path.Combine(addonsDir, vpkName);

                using var addonPkg = new Package();
                addonPkg.AddFile("maps/dl_hideout/entities/default_ents.vents_c", compiledVentsBytes);

                if (compiledWorldNodeBytes != null)
                {
                    addonPkg.AddFile("maps/dl_hideout/worldnodes/n0.vwnod_c", compiledWorldNodeBytes);
                }

                addonPkg.Write(outVpkPath);
                consoleCmd = "sv_cheats 1; map dl_hideout";
            }
            else
            {
                string mapsDir = Path.Combine(deadlockGamePath, "game", "citadel", "maps");
                if (!Directory.Exists(mapsDir))
                {
                    Directory.CreateDirectory(mapsDir);
                }

                string safeMapName = string.IsNullOrWhiteSpace(project.MapName) ? "custom_map" : project.MapName.Trim().ToLowerInvariant().Replace(' ', '_');
                outVpkPath = Path.Combine(mapsDir, $"{safeMapName}.vpk");

                using var mapPkg = new Package();

                // Copy all donor assets (skybox, cubemaps, sounds, materials)
                foreach (var group in donorPackage.Entries.Values)
                {
                    foreach (var entry in group)
                    {
                        string entryPath = $"{entry.DirectoryName}/{entry.FileName}.{entry.TypeName}";
                        if (entryPath.Equals("maps/dl_hideout/entities/default_ents.vents_c", StringComparison.OrdinalIgnoreCase))
                        {
                            mapPkg.AddFile(entryPath, compiledVentsBytes);
                        }
                        else if (compiledWorldNodeBytes != null && entryPath.Equals("maps/dl_hideout/worldnodes/n0.vwnod_c", StringComparison.OrdinalIgnoreCase))
                        {
                            mapPkg.AddFile(entryPath, compiledWorldNodeBytes);
                        }
                        else
                        {
                            donorPackage.ReadEntry(entry, out byte[] fileData);
                            mapPkg.AddFile(entryPath, fileData);
                        }
                    }
                }

                mapPkg.Write(outVpkPath);
                consoleCmd = $"sv_cheats 1; map {safeMapName}";
            }

            result.Success = true;
            result.OutputVpkPath = outVpkPath;
            result.ConsoleLaunchCommand = consoleCmd;
            result.PropsCount = propsCount;
            result.LightsCount = lightsCount;
            result.SpawnsCount = spawnsCount;
            result.Message = $"Successfully compiled map into: {Path.GetFileName(outVpkPath)}!";

            GD.Print($"[Source2MapCompiler] Compilation succeeded! Path: {outVpkPath} | Props: {propsCount}, Lights: {lightsCount}, Spawns: {spawnsCount}");
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Compilation failed: {ex.Message}";
            result.ErrorDetails = ex.ToString();
            GD.PrintErr($"[Source2MapCompiler] Compilation exception: {ex}");
            return result;
        }
    }
}
