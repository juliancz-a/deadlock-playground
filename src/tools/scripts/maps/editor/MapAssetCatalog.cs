using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using SteamDatabase.ValvePak;

namespace DeadlockPlayground.Maps.Editor;

/// <summary>
/// Model representing a browsable 3D prop or architecture asset in the catalog.
/// </summary>
public class MapCatalogItem
{
    public string DisplayName { get; set; }
    public string VmdlPath { get; set; }
    public string Category { get; set; }
    public string ThumbnailPath { get; set; }

    public string GetCacheKey()
    {
        return VmdlPath.Replace('/', '_').Replace('\\', '_').Replace(".vmdl", "");
    }
}

/// <summary>
/// Fast indexing service for Deadlock 3D models and architectural props stored across pak01_dir.vpk and dl_hideout.vpk.
/// Automatically categorizes thousands of models and provides high-speed search and filtering.
/// </summary>
public static class MapAssetCatalog
{
    public const string CategoryAll = "All Props";
    public const string CategoryArchitecture = "Architecture & Walls";
    public const string CategoryFloors = "Floors & Stairs";
    public const string CategoryStreet = "Street & Urban Props";
    public const string CategoryFurniture = "Interior Furniture";
    public const string CategoryGameplay = "Gameplay & Targets";
    public const string CategoryFoliage = "Foliage & Nature";
    public const string CategoryLights = "Lights & Fixtures";
    public const string CategoryOccult = "Occult & Hideout";

    public static readonly string[] Categories = new[]
    {
        CategoryAll,
        CategoryArchitecture,
        CategoryFloors,
        CategoryStreet,
        CategoryFurniture,
        CategoryGameplay,
        CategoryFoliage,
        CategoryLights,
        CategoryOccult
    };

    private static readonly List<MapCatalogItem> _allItems = new();
    private static readonly Dictionary<string, List<MapCatalogItem>> _itemsByCategory = new(StringComparer.OrdinalIgnoreCase);
    private static bool _isIndexed = false;

    public static bool IsIndexed => _isIndexed;
    public static int TotalItemsCount => _allItems.Count;

    /// <summary>
    /// Scans the base game and hideout VPK packages for usable props and architecture models.
    /// </summary>
    public static void IndexAssets(Package baseGamePackage, Package hideoutPackage = null)
    {
        if (_isIndexed && _allItems.Count > 0) return;

        _allItems.Clear();
        _itemsByCategory.Clear();

        foreach (var cat in Categories)
        {
            _itemsByCategory[cat] = new List<MapCatalogItem>();
        }

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Scan Base Game pak01_dir.vpk
        if (baseGamePackage != null && baseGamePackage.Entries.TryGetValue("vmdl_c", out var baseList))
        {
            foreach (var entry in baseList)
            {
                ProcessEntry(entry.DirectoryName, entry.FileName, seenPaths);
            }
        }

        // 2. Scan Hideout dl_hideout.vpk if loaded
        if (hideoutPackage != null && hideoutPackage.Entries.TryGetValue("vmdl_c", out var hideoutList))
        {
            foreach (var entry in hideoutList)
            {
                ProcessEntry(entry.DirectoryName, entry.FileName, seenPaths);
            }
        }

        // Populate All category
        _itemsByCategory[CategoryAll].AddRange(_allItems);
        _isIndexed = true;

        GD.Print($"[MapAssetCatalog] Indexed {_allItems.Count} Deadlock level props across {_itemsByCategory.Count} categories.");
    }

    private static void ProcessEntry(string dirName, string fileName, HashSet<string> seenPaths)
    {
        string fullPath = $"{dirName}/{fileName}.vmdl".Replace('\\', '/');

        // Filter out character models, gibs, animations, and LODs
        if (fullPath.Contains("_lod", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains("_gib", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains("anims/", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains("/heroes/", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains("heroes_wip", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains("materials/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!seenPaths.Add(fullPath)) return;

        string category = ClassifyModel(fullPath);
        if (string.IsNullOrEmpty(category)) return;

        string displayName = Path.GetFileNameWithoutExtension(fullPath)
            .Replace("prop_", "")
            .Replace("hideout_", "")
            .Replace('_', ' ');

        // Capitalize words
        displayName = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(displayName);

        var item = new MapCatalogItem
        {
            DisplayName = displayName,
            VmdlPath = fullPath,
            Category = category
        };

        _allItems.Add(item);

        if (_itemsByCategory.TryGetValue(category, out var list))
        {
            list.Add(item);
        }
    }

    private static string ClassifyModel(string path)
    {
        string p = path.ToLowerInvariant();

        if (p.Contains("architecture/") || p.Contains("arch_") || p.Contains("wall_") || p.Contains("pillar_") || p.Contains("roof_") || p.Contains("window_"))
            return CategoryArchitecture;

        if (p.Contains("floors/") || p.Contains("floor_") || p.Contains("stairs/") || p.Contains("stair_") || p.Contains("steps_") || p.Contains("streets/") || p.Contains("sidewalk_") || p.Contains("pavement_"))
            return CategoryFloors;

        if (p.Contains("props_street/") || p.Contains("street_") || p.Contains("barrier_") || p.Contains("crate_") || p.Contains("trash_") || p.Contains("cart_") || p.Contains("sign_"))
            return CategoryStreet;

        if (p.Contains("props_interiors/") || p.Contains("furniture/") || p.Contains("chair_") || p.Contains("table_") || p.Contains("desk_") || p.Contains("bookshelf_") || p.Contains("cabinet_"))
            return CategoryFurniture;

        if (p.Contains("props_gameplay/") || p.Contains("gameplay/") || p.Contains("target_") || p.Contains("dummy_") || p.Contains("beacon_") || p.Contains("turret_") || p.Contains("pedestal_"))
            return CategoryGameplay;

        if (p.Contains("foliage/") || p.Contains("nature/") || p.Contains("tree_") || p.Contains("plant_") || p.Contains("bush_") || p.Contains("canopy_") || p.Contains("rock_"))
            return CategoryFoliage;

        if (p.Contains("props_lamps/") || p.Contains("lights/") || p.Contains("lantern_") || p.Contains("lamp_") || p.Contains("chandelier_") || p.Contains("sconce_"))
            return CategoryLights;

        if (p.Contains("hideout/") || p.Contains("ritual_") || p.Contains("altar_") || p.Contains("candle_") || p.Contains("carpet_") || p.Contains("rug_") || p.Contains("skull_"))
            return CategoryOccult;

        // If it's in a props folder, classify as street props fallback
        if (p.Contains("props_") || p.Contains("props/"))
            return CategoryStreet;

        return null;
    }

    /// <summary>
    /// Searches and filters catalog items by category and search keyword query.
    /// </summary>
    public static List<MapCatalogItem> FilterItems(string category, string query, int maxResults = 100)
    {
        IEnumerable<MapCatalogItem> source;

        if (string.IsNullOrEmpty(category) || category == CategoryAll)
        {
            source = _allItems;
        }
        else if (_itemsByCategory.TryGetValue(category, out var list))
        {
            source = list;
        }
        else
        {
            source = _allItems;
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            string q = query.Trim().ToLowerInvariant();
            source = source.Where(item =>
                item.DisplayName.ToLowerInvariant().Contains(q) ||
                item.VmdlPath.ToLowerInvariant().Contains(q)
            );
        }

        return source.Take(maxResults).ToList();
    }
}
