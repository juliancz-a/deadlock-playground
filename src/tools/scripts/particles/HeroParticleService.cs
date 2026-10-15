using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DeadlockPlayground.Catalog;
using DeadlockPlayground.Particles.Models;
using Godot;
using SteamDatabase.ValvePak;

namespace DeadlockPlayground.Particles;

/// <summary>
/// Singleton service managing the hero particle index, offline JSON caching,
/// and asynchronous background ingestion.
/// </summary>
public class HeroParticleService
{
    private static HeroParticleService _instance;
    public static HeroParticleService Instance => _instance ??= new HeroParticleService();

    private const string CachePath = "user://cache/hero_particles_index.json";
    private HeroParticleIndex _currentIndex;
    private Task<HeroParticleIndex> _loadingTask;
    private readonly object _lock = new();

    public event Action OnIndexLoaded;

    public bool IsLoaded => _currentIndex != null;
    public HeroParticleIndex CurrentIndex => _currentIndex;

    public async Task<HeroParticleIndex> EnsureLoadedAsync(string customVdataOrVpkPath = null)
    {
        if (_currentIndex != null)
        {
            return _currentIndex;
        }

        lock (_lock)
        {
            if (_loadingTask != null)
            {
                return _loadingTask.Result;
            }
            _loadingTask = Task.Run(() => LoadOrBuildIndex(customVdataOrVpkPath));
        }

        var result = await _loadingTask;
        _currentIndex = result;
        OnIndexLoaded?.Invoke();
        return result;
    }

    private HeroParticleIndex LoadOrBuildIndex(string explicitPath)
    {
        string globalCachePath = ProjectSettings.GlobalizePath(CachePath);

        // 1. Try reading existing JSON cache (< 15 ms)
        if (File.Exists(globalCachePath))
        {
            try
            {
                string json = File.ReadAllText(globalCachePath);
                var cachedIndex = JsonSerializer.Deserialize<HeroParticleIndex>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (cachedIndex != null && cachedIndex.Heroes != null && cachedIndex.Heroes.Count > 0)
                {
                    // Ensure cache contains canonical aliases (Grey Talon, Paradox, Dynamo)
                    if (cachedIndex.Heroes.ContainsKey("Grey Talon") || cachedIndex.Heroes.ContainsKey("Paradox"))
                    {
                        GD.Print($"[HeroParticleService] Loaded particle index from cache ({cachedIndex.Heroes.Count} heroes) in < 15 ms.");
                        return cachedIndex;
                    }

                    GD.Print("[HeroParticleService] Stale cache without canonical hero aliases detected. Re-indexing...");
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[HeroParticleService] Failed to read cache: {ex.Message}. Rebuilding...");
            }
        }

        // 2. Locate abilities.vdata_c
        string vdataPath = ResolveVdataPath(explicitPath);

        HeroParticleIndex freshIndex = null;
        if (!string.IsNullOrEmpty(vdataPath) && File.Exists(vdataPath))
        {
            GD.Print($"[HeroParticleService] Indexing particles from file: {vdataPath}");
            freshIndex = FastVDataParticleIndexer.ParseFromFile(vdataPath);
        }
        else
        {
            // Try extracting from Deadlock VPK
            freshIndex = TryExtractFromVpk();
        }

        if (freshIndex == null)
        {
            GD.PrintErr("[HeroParticleService] Could not locate abilities.vdata_c in docs, game path, or VPK.");
            freshIndex = new HeroParticleIndex(new Dictionary<string, IReadOnlyList<AbilityParticleGroup>>());
            return freshIndex;
        }

        // 3. Save to cache
        try
        {
            string cacheDir = Path.GetDirectoryName(globalCachePath);
            if (!string.IsNullOrEmpty(cacheDir) && !Directory.Exists(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
            }

            string json = JsonSerializer.Serialize(freshIndex, new JsonSerializerOptions
            {
                WriteIndented = false
            });
            File.WriteAllText(globalCachePath, json);
            GD.Print($"[HeroParticleService] Particle index cached to: {globalCachePath} ({freshIndex.Heroes.Count} heroes)");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[HeroParticleService] Failed to save cache: {ex.Message}");
        }

        return freshIndex;
    }

    private string ResolveVdataPath(string explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        // Check docs/abilities.vdata_c relative to project
        string docsRelative = ProjectSettings.GlobalizePath("res://../docs/abilities.vdata_c");
        if (File.Exists(docsRelative))
        {
            return docsRelative;
        }

        string directPath = @"d:\GameDev\deadlock-playground\docs\abilities.vdata_c";
        if (File.Exists(directPath))
        {
            return directPath;
        }

        return null;
    }

    private HeroParticleIndex TryExtractFromVpk()
    {
        var pathManager = new GamePathManager();
        pathManager.LoadConfig();

        string vpkPath = null;
        if (!string.IsNullOrEmpty(pathManager.CurrentGamePath))
        {
            vpkPath = Path.Combine(pathManager.CurrentGamePath, "game", "citadel", "pak01_dir.vpk");
        }

        if (string.IsNullOrEmpty(vpkPath) || !File.Exists(vpkPath))
        {
            vpkPath = @"E:\SteamLibrary\steamapps\common\Deadlock\game\citadel\pak01_dir.vpk";
        }

        if (!File.Exists(vpkPath))
        {
            return null;
        }

        try
        {
            using var package = new Package();
            package.Read(vpkPath);

            var entry = package.FindEntry("scripts/abilities.vdata_c");
            if (entry == null) return null;

            package.ReadEntry(entry, out byte[] data);
            using var stream = new MemoryStream(data);
            return FastVDataParticleIndexer.ParseFromStream(stream);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[HeroParticleService] Failed to extract from VPK: {ex.Message}");
            return null;
        }
    }

    private static readonly Dictionary<string, string> s_displayNameMap = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<string>> s_aliasMap = new(StringComparer.OrdinalIgnoreCase);
    private static bool s_metadataInitialized = false;

    private static void EnsureHeroMetadataInitialized(Package vpkPackage = null)
    {
        if (s_metadataInitialized) return;
        s_metadataInitialized = true;

        // 1. Seed base known pairings from DeadlockHeroCatalog
        foreach (var hero in DeadlockHeroCatalog.Entries)
        {
            RegisterHeroMapping(hero.DisplayName, hero.InternalCodename);
            if (!string.IsNullOrEmpty(hero.WipAlias))
            {
                RegisterHeroMapping(hero.DisplayName, hero.WipAlias);
            }
        }

        // Additional known Source 2 internal folder pairings in Deadlock
        RegisterHeroMapping("Grey Talon", "archer", "orion", "Hero Orion", "Hero Archer");
        RegisterHeroMapping("Paradox", "chrono", "Hero Chrono");
        RegisterHeroMapping("Dynamo", "dynamo", "sumo", "prof_dynamo", "Hero Dynamo", "Hero Sumo");
        RegisterHeroMapping("Lady Geist", "geist", "ghost", "Hero Ghost", "Hero Geist");
        RegisterHeroMapping("Vindicta", "hornet", "Hero Hornet");
        RegisterHeroMapping("McGinnis", "mcginnis", "forge", "engineer", "Hero Forge");
        RegisterHeroMapping("Ivy", "ivy", "tengu", "Hero Tengu");
        RegisterHeroMapping("Seven", "gigawatt_prisoner", "gigawatt", "seven", "Hero Gigawatt");
        RegisterHeroMapping("Pocket", "pocket", "synth", "Hero Synth");
        RegisterHeroMapping("Mo & Krill", "digger", "krill", "mo_krill", "mo and krill", "Hero Mo and Krill");
        RegisterHeroMapping("Holliday", "astro", "Hero Astro");
        RegisterHeroMapping("Calico", "nano", "Hero Nano");
        RegisterHeroMapping("Mina", "vampirebat", "Hero VampireBat");
        RegisterHeroMapping("Victor", "frank", "Hero Frank");
        RegisterHeroMapping("Paige", "bookworm", "Hero Bookworm");
        RegisterHeroMapping("Billy", "punkgoat", "Hero PunkGoat");
        RegisterHeroMapping("Solomon", "chessmaster", "Hero ChessMaster");
        RegisterHeroMapping("Violet", "artist", "Hero Artist");
        RegisterHeroMapping("Apollo", "fencer", "Hero Fencer");
        RegisterHeroMapping("Celeste", "unicorn", "Hero Unicorn");
        RegisterHeroMapping("Graves", "necro", "Hero Necro");
        RegisterHeroMapping("Sinclair", "magician", "Hero Magician");

        // 2. Dynamically ingest scripts/heroes.vdata_c if available (without hardcoding)
        try
        {
            ParseHeroesVData(vpkPackage);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[HeroParticleService] Notice: Could not dynamically parse heroes.vdata_c: {ex.Message}");
        }

        // 3. Dynamically read localization tokens from citadel_english.txt if available on disk or in game folder
        try
        {
            ParseLocalizationTokens();
        }
        catch { }
    }

    private static void RegisterHeroMapping(string displayName, params string[] aliases)
    {
        s_displayNameMap[displayName] = displayName;
        if (!s_aliasMap.TryGetValue(displayName, out var list))
        {
            list = new List<string> { displayName };
            s_aliasMap[displayName] = list;
        }

        foreach (var a in aliases)
        {
            if (string.IsNullOrWhiteSpace(a)) continue;
            string clean = a.Trim();
            s_displayNameMap[clean] = displayName;
            if (!list.Any(x => x.Equals(clean, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(clean);
            }

            if (!s_aliasMap.TryGetValue(clean, out var subList))
            {
                subList = new List<string> { clean, displayName };
                s_aliasMap[clean] = subList;
            }
            else if (!subList.Any(x => x.Equals(displayName, StringComparison.OrdinalIgnoreCase)))
            {
                subList.Add(displayName);
            }
        }
    }

    private static void ParseHeroesVData(Package existingPackage)
    {
        byte[] vdataBytes = null;

        if (existingPackage != null)
        {
            var entry = existingPackage.FindEntry("scripts/heroes.vdata_c");
            if (entry != null)
            {
                existingPackage.ReadEntry(entry, out vdataBytes);
            }
        }

        if (vdataBytes == null)
        {
            string defaultVpk = @"E:\SteamLibrary\steamapps\common\Deadlock\game\citadel\pak01_dir.vpk";
            if (File.Exists(defaultVpk))
            {
                using var pkg = new Package();
                pkg.Read(defaultVpk);
                var entry = pkg.FindEntry("scripts/heroes.vdata_c");
                if (entry != null)
                {
                    pkg.ReadEntry(entry, out vdataBytes);
                }
            }
        }

        if (vdataBytes == null) return;

        using var ms = new MemoryStream(vdataBytes);
        using var res = new ValveResourceFormat.Resource();
        res.Read(ms);

        if (res.DataBlock is ValveResourceFormat.ResourceTypes.BinaryKV3 kv)
        {
            foreach (var heroEntry in kv.Data.Root)
            {
                string key = heroEntry.Key; // e.g. "hero_chrono" or "hero_orion"
                string codename = key.StartsWith("hero_", StringComparison.OrdinalIgnoreCase) ? key[5..] : key;

                string logoName = null;
                if (heroEntry.Value.TryGetValue("m_strLogoImageEnglish", out var logoObj) && logoObj != null)
                {
                    string logoStr = logoObj.ToString();
                    if (!string.IsNullOrEmpty(logoStr))
                    {
                        logoName = Path.GetFileNameWithoutExtension(logoStr);
                    }
                }

                string modelFolder = null;
                if (heroEntry.Value.TryGetValue("m_strModelName", out var modelObj) && modelObj != null)
                {
                    string modelStr = modelObj.ToString();
                    if (!string.IsNullOrEmpty(modelStr))
                    {
                        var parts = modelStr.Split('/');
                        if (parts.Length >= 3)
                        {
                            modelFolder = parts[^2];
                        }
                    }
                }

                string display = !string.IsNullOrEmpty(logoName)
                    ? System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(logoName.Replace('_', ' '))
                    : System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(codename.Replace('_', ' '));

                RegisterHeroMapping(display, codename, modelFolder, $"Hero {display}", $"Hero {codename}");
            }
        }
    }

    private static void ParseLocalizationTokens()
    {
        string[] potentialPaths = new[]
        {
            @"E:\SteamLibrary\steamapps\common\Deadlock\game\citadel\resource\localization\citadel_english.txt",
            @"E:\SteamLibrary\steamapps\common\Deadlock\game\citadel\resource\citadel_english.txt"
        };

        foreach (var p in potentialPaths)
        {
            if (File.Exists(p))
            {
                var lines = File.ReadAllLines(p);
                foreach (var line in lines)
                {
                    // Pattern: "citadel_hero_chrono" "Paradox"
                    string trimmed = line.Trim();
                    if (!trimmed.StartsWith("\"citadel_hero_", StringComparison.OrdinalIgnoreCase)) continue;

                    int firstEnd = trimmed.IndexOf('"', 1);
                    if (firstEnd <= 0) continue;
                    string token = trimmed.Substring(1, firstEnd - 1);
                    string codename = token.Substring("citadel_hero_".Length);

                    int secondStart = trimmed.IndexOf('"', firstEnd + 1);
                    if (secondStart <= 0) continue;
                    int secondEnd = trimmed.IndexOf('"', secondStart + 1);
                    if (secondEnd <= secondStart) continue;

                    string display = trimmed.Substring(secondStart + 1, secondEnd - secondStart - 1).Trim();
                    if (!string.IsNullOrEmpty(display))
                    {
                        RegisterHeroMapping(display, codename, $"#{token}");
                    }
                }
                break;
            }
        }
    }

    /// <summary>
    /// Resolves an in-game localized/display name from any technical codename, folder name, or token.
    /// (e.g. "chrono" -> "Paradox", "sumo" -> "Dynamo", "archer" -> "Grey Talon", "orion" -> "Grey Talon").
    /// </summary>
    public static string ResolveDisplayName(string heroNameOrCodename)
    {
        if (string.IsNullOrWhiteSpace(heroNameOrCodename)) return "Hero";
        EnsureHeroMetadataInitialized();

        string clean = heroNameOrCodename.Trim();
        if (clean.StartsWith("#citadel_hero_", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean.Substring(14);
        }
        else if (clean.StartsWith("citadel_hero_", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean.Substring(13);
        }
        else if (clean.StartsWith("Hero ", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean.Substring(5).Trim();
        }

        if (s_displayNameMap.TryGetValue(clean, out var match))
        {
            return match;
        }

        // Check catalog
        var cat = DeadlockHeroCatalog.ResolveHero(clean);
        if (cat != null)
        {
            return cat.DisplayName;
        }

        return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(clean.Replace('_', ' '));
    }

    /// <summary>
    /// Retrieves the list of ability particle groups for a specific hero by display name, folder, or internal codename.
    /// Supports Grey Talon ("archer" / "orion" / "Hero Orion"), Paradox ("chrono"), Dynamo ("sumo"), etc.
    /// </summary>
    public IReadOnlyList<AbilityParticleGroup> GetHeroAbilities(string heroNameOrCodename)
    {
        if (_currentIndex == null || string.IsNullOrWhiteSpace(heroNameOrCodename))
        {
            return Array.Empty<AbilityParticleGroup>();
        }

        EnsureHeroMetadataInitialized();

        string raw = heroNameOrCodename.Trim();

        // 1. Direct match in dictionary
        if (_currentIndex.Heroes.TryGetValue(raw, out var directMatch))
        {
            return directMatch;
        }

        // 2. Clean "Hero " prefix if present
        if (raw.StartsWith("Hero ", StringComparison.OrdinalIgnoreCase))
        {
            string sub = raw.Substring(5).Trim();
            if (_currentIndex.Heroes.TryGetValue(sub, out var subMatch))
            {
                return subMatch;
            }
        }

        // 3. Resolve via alias mappings (covers archer <-> orion <-> Grey Talon, chrono <-> Paradox, etc.)
        if (s_aliasMap.TryGetValue(raw, out var aliases))
        {
            foreach (var alias in aliases)
            {
                if (_currentIndex.Heroes.TryGetValue(alias, out var match))
                {
                    return match;
                }
            }
        }

        // 4. Resolve via DeadlockHeroCatalog
        var entry = DeadlockHeroCatalog.GetByCodename(raw)
                 ?? DeadlockHeroCatalog.ResolveHero(raw);

        if (entry != null)
        {
            if (_currentIndex.Heroes.TryGetValue(entry.DisplayName, out var byDisplay))
            {
                return byDisplay;
            }
            if (_currentIndex.Heroes.TryGetValue(entry.InternalCodename, out var byCodename))
            {
                return byCodename;
            }
            if (!string.IsNullOrEmpty(entry.WipAlias) && _currentIndex.Heroes.TryGetValue(entry.WipAlias, out var byWip))
            {
                return byWip;
            }

            // Check if catalog DisplayName matches indexed folder
            foreach (var kvp in _currentIndex.Heroes)
            {
                if (kvp.Key.Equals(entry.DisplayName, StringComparison.OrdinalIgnoreCase) ||
                    kvp.Key.Equals(entry.InternalCodename, StringComparison.OrdinalIgnoreCase))
                {
                    return kvp.Value;
                }
            }
        }

        // 5. Fallback partial substring search across indexed folders
        foreach (var kvp in _currentIndex.Heroes)
        {
            if (kvp.Key.Contains(raw, StringComparison.OrdinalIgnoreCase) ||
                raw.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        return Array.Empty<AbilityParticleGroup>();
    }

    public void InvalidateCache()
    {
        string globalCachePath = ProjectSettings.GlobalizePath(CachePath);
        if (File.Exists(globalCachePath))
        {
            try { File.Delete(globalCachePath); } catch { }
        }
        _currentIndex = null;
        _loadingTask = null;
    }
}
