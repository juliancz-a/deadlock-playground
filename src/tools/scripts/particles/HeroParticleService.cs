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

    private const string CachePath = "user://cache/hero_particles_index_v2.json";
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

        Task<HeroParticleIndex> task;
        bool isOwner = false;
        lock (_lock)
        {
            if (_loadingTask == null)
            {
                _loadingTask = Task.Run(() => LoadOrBuildIndex(customVdataOrVpkPath));
                isOwner = true;
            }
            task = _loadingTask;
        }

        // Never block the calling (possibly main) thread: every caller awaits the same task.
        var result = await task;
        if (isOwner)
        {
            _currentIndex = result;
            OnIndexLoaded?.Invoke();
        }
        return result;
    }

    private HeroParticleIndex LoadOrBuildIndex(string explicitPath)
    {
        DeadlockPlayground.Core.StartupDiagnosticTracker.Log("HeroParticleService: index load/build start");
        string globalCachePath = ProjectSettings.GlobalizePath(CachePath);

        // Delete legacy v1 cache if present
        try
        {
            string oldCache = ProjectSettings.GlobalizePath("user://cache/hero_particles_index.json");
            if (File.Exists(oldCache)) File.Delete(oldCache);
        }
        catch { }

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
                    // Ensure cache contains canonical aliases AND updated heroes (Violet, Deadman Danny, Paradox)
                    bool hasViolet = cachedIndex.Heroes.ContainsKey("Violet") || cachedIndex.Heroes.ContainsKey("artist");
                    bool hasDeadman = cachedIndex.Heroes.ContainsKey("Deadman Danny") || cachedIndex.Heroes.ContainsKey("airheart");
                    bool hasParadox = cachedIndex.Heroes.ContainsKey("Paradox") || cachedIndex.Heroes.ContainsKey("chrono");

                    if ((cachedIndex.Heroes.ContainsKey("Grey Talon") || cachedIndex.Heroes.ContainsKey("Dynamo"))
                        && hasViolet && hasDeadman && hasParadox)
                    {
                        GD.Print($"[HeroParticleService] Loaded particle index from cache ({cachedIndex.Heroes.Count} heroes) in < 15 ms.");
                        return cachedIndex;
                    }

                    GD.Print("[HeroParticleService] Stale cache detected. Re-indexing...");
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[HeroParticleService] Failed to read cache: {ex.Message}. Rebuilding...");
            }
        }

        // 2. Open VPK if available
        string vpkPath = ResolveVpkPath();
        Package package = null;
        if (!string.IsNullOrEmpty(vpkPath) && File.Exists(vpkPath))
        {
            try
            {
                package = new Package();
                package.Read(vpkPath);
                EnsureHeroMetadataInitialized(package);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[HeroParticleService] Failed to open VPK package at {vpkPath}: {ex.Message}");
                package?.Dispose();
                package = null;
            }
        }
        else
        {
            EnsureHeroMetadataInitialized();
        }

        // 3. Locate abilities.vdata_c
        HeroParticleIndex baseIndex = null;
        if (package != null)
        {
            try
            {
                var entry = package.FindEntry("scripts/abilities.vdata_c");
                if (entry != null)
                {
                    package.ReadEntry(entry, out byte[] data);
                    using var stream = new MemoryStream(data);
                    baseIndex = FastVDataParticleIndexer.ParseFromStream(stream);
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[HeroParticleService] Could not parse abilities.vdata_c from VPK: {ex.Message}");
            }
        }

        if (baseIndex == null)
        {
            string vdataPath = ResolveVdataPath(explicitPath);
            if (!string.IsNullOrEmpty(vdataPath) && File.Exists(vdataPath))
            {
                GD.Print($"[HeroParticleService] Indexing particles from file: {vdataPath}");
                baseIndex = FastVDataParticleIndexer.ParseFromFile(vdataPath);
            }
        }

        // 4. Supplement index with VPK loose particle discovery (particles/abilities/...)
        // This ensures heroes without vdata ability entries (like Violet / artist and Deadman Danny / airheart)
        // have all their particles cleanly mapped into ability groups.
        var heroesMap = new Dictionary<string, List<AbilityParticleGroup>>(StringComparer.OrdinalIgnoreCase);

        if (baseIndex != null && baseIndex.Heroes != null)
        {
            foreach (var kvp in baseIndex.Heroes)
            {
                heroesMap[kvp.Key] = new List<AbilityParticleGroup>(kvp.Value);
            }
        }

        if (package != null)
        {
            try
            {
                SupplementFromVpkEntries(package, heroesMap);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[HeroParticleService] Failed to supplement particles from VPK entries: {ex.Message}");
            }
            finally
            {
                package.Dispose();
            }
        }

        var resultHeroes = new Dictionary<string, IReadOnlyList<AbilityParticleGroup>>(heroesMap.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in heroesMap)
        {
            resultHeroes[kvp.Key] = kvp.Value;
        }

        var freshIndex = new HeroParticleIndex(resultHeroes);

        // 5. Save to cache
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

    public static string ResolveGameRootPath()
    {
        var pathManager = new GamePathManager();
        pathManager.LoadConfig();

        if (!string.IsNullOrEmpty(pathManager.CurrentGamePath) && Directory.Exists(pathManager.CurrentGamePath))
        {
            return pathManager.CurrentGamePath;
        }

        try
        {
            string auto = pathManager.AutoDetectDeadlockPath();
            if (!string.IsNullOrEmpty(auto) && Directory.Exists(auto))
            {
                return auto;
            }
        }
        catch { }

        return null;
    }

    public static string ResolveVpkPath()
    {
        string gameRoot = ResolveGameRootPath();
        if (!string.IsNullOrEmpty(gameRoot))
        {
            string p = Path.Combine(gameRoot, "game", "citadel", "pak01_dir.vpk");
            if (File.Exists(p)) return p;
        }

        return null;
    }

    private static void SupplementFromVpkEntries(Package package, Dictionary<string, List<AbilityParticleGroup>> heroesMap)
    {
        if (package == null || !package.Entries.TryGetValue("vpcf_c", out var vpcfEntries))
        {
            return;
        }

        // Track already known vpcf paths for each hero to avoid duplicates
        var knownPathsByHero = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (heroKey, groups) in heroesMap)
        {
            if (!knownPathsByHero.TryGetValue(heroKey, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                knownPathsByHero[heroKey] = set;
            }
            foreach (var g in groups)
            {
                foreach (var p in g.Particles)
                {
                    set.Add(p.VpcfPath);
                }
            }
        }

        // Temporary accumulator: heroKey -> abilityKey -> list of ParticleEntry
        var newHeroAbilities = new Dictionary<string, Dictionary<string, List<ParticleEntry>>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in vpcfEntries)
        {
            string dir = entry.DirectoryName.Replace('\\', '/').Trim('/');
            if (!dir.StartsWith("particles/abilities/", StringComparison.OrdinalIgnoreCase))
                continue;

            string rel = dir.Substring("particles/abilities/".Length).TrimStart('/');
            string[] parts = rel.Split('/');
            if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
                continue;

            string heroFolder = parts[0].ToLowerInvariant();
            if (!TryResolveHeroCanonicalName(heroFolder, out string resolvedDisplay))
            {
                continue; // Skip non-hero or shared folders (e.g. melee, generic)
            }
            string heroKey = resolvedDisplay;

            string vpcfPath = $"{dir}/{entry.FileName}.vpcf";

            if (knownPathsByHero.TryGetValue(heroKey, out var heroKnown) && heroKnown.Contains(vpcfPath))
            {
                continue;
            }

            string abilityKey = DeriveAbilityKey(entry.FileName, heroFolder);
            string variantName = DeriveVariantName(entry.FileName, heroFolder);

            if (!newHeroAbilities.TryGetValue(heroKey, out var abilityDict))
            {
                abilityDict = new Dictionary<string, List<ParticleEntry>>(StringComparer.OrdinalIgnoreCase);
                newHeroAbilities[heroKey] = abilityDict;
            }

            if (!abilityDict.TryGetValue(abilityKey, out var particleList))
            {
                particleList = new List<ParticleEntry>();
                abilityDict[abilityKey] = particleList;
            }

            particleList.Add(new ParticleEntry(variantName, vpcfPath));
        }

        // Merge accumulated particles into heroesMap
        foreach (var (heroKey, abilityDict) in newHeroAbilities)
        {
            if (!heroesMap.TryGetValue(heroKey, out var existingGroups))
            {
                existingGroups = new List<AbilityParticleGroup>();
                heroesMap[heroKey] = existingGroups;
            }

            foreach (var (abilityKey, particles) in abilityDict)
            {
                var matchGroup = existingGroups.FirstOrDefault(g => g.AbilityKey.Equals(abilityKey, StringComparison.OrdinalIgnoreCase));
                if (matchGroup != null)
                {
                    var merged = new List<ParticleEntry>(matchGroup.Particles);
                    merged.AddRange(particles);
                    existingGroups.Remove(matchGroup);
                    existingGroups.Add(new AbilityParticleGroup(matchGroup.AbilityKey, matchGroup.LocalizationToken, merged));
                }
                else
                {
                    existingGroups.Add(new AbilityParticleGroup(abilityKey, $"#{abilityKey}", particles));
                }
            }

            // Also mirror under technical/internal aliases if missing (e.g. "artist", "airheart")
            if (s_aliasMap.TryGetValue(heroKey, out var aliases))
            {
                foreach (var a in aliases)
                {
                    if (!heroesMap.ContainsKey(a))
                    {
                        heroesMap[a] = existingGroups;
                    }
                }
            }
        }
    }

    private static string DeriveAbilityKey(string filename, string heroFolder)
    {
        string name = filename;
        if (name.StartsWith(heroFolder + "_", StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(heroFolder.Length + 1);
        }

        string[] tokens = name.Split('_');
        if (tokens.Length >= 2)
        {
            string two = $"{tokens[0]}_{tokens[1]}".ToLowerInvariant();
            if (two is "air_dash" or "backblast_cone" or "chimera_mark" or "still_life" or "heavy_melee" or "flow_state" or "buff_flow" or "rocket_explo" or "rocket_explosion")
            {
                return $"{heroFolder}_{two}";
            }
        }

        string primary = tokens.Length > 0 ? tokens[0].ToLowerInvariant() : "general";
        return $"{heroFolder}_{primary}";
    }

    private static string DeriveVariantName(string filename, string heroFolder)
    {
        string name = filename;
        if (name.StartsWith(heroFolder + "_", StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(heroFolder.Length + 1);
        }

        string[] tokens = name.Replace('_', ' ').Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return "Default";

        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Length > 0)
            {
                tokens[i] = char.ToUpperInvariant(tokens[i][0]) + tokens[i][1..].ToLowerInvariant();
            }
        }

        return string.Join(" ", tokens);
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

        return null;
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
            if (hero.DisplayName.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
            {
                RegisterHeroMapping(hero.DisplayName, hero.DisplayName[4..]);
            }
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
        RegisterHeroMapping("Violet", "artist", "Hero Artist", "Hero Violet");
        RegisterHeroMapping("Deadman Danny", "deadman_danny", "deadpack", "airheart", "Hero Deadman Danny", "Hero Airheart", "Hero Deadpack");
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

            // Guard: Do not overwrite an existing mapping with a different hero
            if (s_displayNameMap.TryGetValue(clean, out var existingHero) &&
                !existingHero.Equals(displayName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Also check catalog to ensure we do not map an existing catalog hero's codename/alias to another hero
            var cat = DeadlockHeroCatalog.ResolveHero(clean);
            if (cat != null && !cat.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

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
            string vpkPath = ResolveVpkPath();
            if (!string.IsNullOrEmpty(vpkPath) && File.Exists(vpkPath))
            {
                using var pkg = new Package();
                pkg.Read(vpkPath);
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

                // If modelFolder already belongs to another hero, don't register it as an alias for this hero
                if (!string.IsNullOrEmpty(modelFolder))
                {
                    if (s_displayNameMap.TryGetValue(modelFolder, out var owner) &&
                        !owner.Equals(display, StringComparison.OrdinalIgnoreCase))
                    {
                        modelFolder = null;
                    }
                    else
                    {
                        var cat = DeadlockHeroCatalog.ResolveHero(modelFolder);
                        if (cat != null && !cat.DisplayName.Equals(display, StringComparison.OrdinalIgnoreCase))
                        {
                            modelFolder = null;
                        }
                    }
                }

                RegisterHeroMapping(display, codename, modelFolder, $"Hero {display}", $"Hero {codename}");
            }
        }
    }

    private static void ParseLocalizationTokens()
    {
        string gameRoot = ResolveGameRootPath();
        if (string.IsNullOrEmpty(gameRoot)) return;

        string[] potentialPaths = new[]
        {
            Path.Combine(gameRoot, "game", "citadel", "resource", "localization", "citadel_english.txt"),
            Path.Combine(gameRoot, "game", "citadel", "resource", "citadel_english.txt")
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
    /// Checks whether the given name, codename, folder, or alias corresponds to a known hero,
    /// and returns their canonical display name if found.
    /// Returns false for non-hero directories (e.g. "generic", "items", "weapons", "melee").
    /// </summary>
    public static bool TryResolveHeroCanonicalName(string nameOrFolder, out string canonicalName)
    {
        canonicalName = null;
        if (string.IsNullOrWhiteSpace(nameOrFolder)) return false;

        EnsureHeroMetadataInitialized();

        string clean = nameOrFolder.Trim();
        if (clean.StartsWith("#citadel_hero_", StringComparison.OrdinalIgnoreCase))
            clean = clean[14..];
        else if (clean.StartsWith("citadel_hero_", StringComparison.OrdinalIgnoreCase))
            clean = clean[13..];
        else if (clean.StartsWith("Hero ", StringComparison.OrdinalIgnoreCase))
            clean = clean[5..].Trim();

        // 1. Direct check in known display name mapping
        if (s_displayNameMap.TryGetValue(clean, out var match))
        {
            canonicalName = match;
            return true;
        }

        // 2. Check catalog
        var cat = DeadlockHeroCatalog.ResolveHero(clean);
        if (cat != null)
        {
            canonicalName = cat.DisplayName;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks whether a particle at the given VPCF path is owned by (or compatible with) the specified hero.
    /// If the particle is located in another hero's folder (e.g. particles/abilities/chrono/... when indexing Doorman),
    /// this returns false to prevent cross-hero particle leaks.
    /// Non-hero generic particles (e.g. particles/generic/..., particles/impact_fx/...) return true.
    /// </summary>
    public static bool IsParticleOwnedByHero(string vpcfPath, string targetHeroName)
    {
        if (string.IsNullOrWhiteSpace(vpcfPath)) return false;
        if (string.IsNullOrWhiteSpace(targetHeroName)) return true;

        string normPath = vpcfPath.Replace('\\', '/').TrimStart('/');

        string heroSubFolder = null;
        if (normPath.StartsWith("particles/abilities/", StringComparison.OrdinalIgnoreCase))
        {
            string rel = normPath.Substring("particles/abilities/".Length);
            int slashIdx = rel.IndexOf('/');
            if (slashIdx > 0)
            {
                heroSubFolder = rel.Substring(0, slashIdx);
            }
        }
        else if (normPath.StartsWith("particles/heroes/", StringComparison.OrdinalIgnoreCase))
        {
            string rel = normPath.Substring("particles/heroes/".Length);
            int slashIdx = rel.IndexOf('/');
            if (slashIdx > 0)
            {
                heroSubFolder = rel.Substring(0, slashIdx);
            }
        }

        // If not under particles/abilities/{folder}/ or particles/heroes/{folder}/, it's not a hero-exclusive particle path
        if (string.IsNullOrEmpty(heroSubFolder))
        {
            return true;
        }

        // If the folder doesn't resolve to any known hero, it is generic/shared (e.g. melee, generic)
        if (!TryResolveHeroCanonicalName(heroSubFolder, out string particleOwner))
        {
            return true;
        }

        // The folder belongs to a known hero. Resolve the target hero's canonical name.
        if (!TryResolveHeroCanonicalName(targetHeroName, out string targetCanonical))
        {
            targetCanonical = ResolveDisplayName(targetHeroName);
        }

        // Direct equality check between canonical names (e.g. "Paradox" vs "The Doorman")
        if (particleOwner.Equals(targetCanonical, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check if aliases intersect (e.g. WIP aliases, old names, codenames)
        if (s_aliasMap.TryGetValue(targetCanonical, out var targetAliases) &&
            targetAliases.Any(a => a.Equals(heroSubFolder, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Particle belongs to another hero! Prevent leak.
        return false;
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
        try
        {
            string oldCache = ProjectSettings.GlobalizePath("user://cache/hero_particles_index.json");
            if (File.Exists(oldCache)) File.Delete(oldCache);
        }
        catch { }
        _currentIndex = null;
        _loadingTask = null;
    }
}
