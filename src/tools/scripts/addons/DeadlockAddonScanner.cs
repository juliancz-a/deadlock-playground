using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using SteamDatabase.ValvePak;
using DeadlockPlayground.Catalog;

namespace DeadlockPlayground.Addons
{
    public enum AddonModType
    {
        Vanilla,
        FullModel,
        TextureOnly,
        Unknown
    }

    public class AddonModInfo
    {
        public string FilePath { get; set; } = "";
        public string FileName { get; set; } = "";
        public AddonModType ModType { get; set; } = AddonModType.Unknown;
        public string DetectedHeroCodename { get; set; } = "";
        public string DetectedHeroDisplayName { get; set; } = "";
        public string DisplayTitle { get; set; } = "";

        public List<string> ModelEntries { get; } = new();
        public List<string> MaterialEntries { get; } = new();
        public List<string> TextureEntries { get; } = new();
        public List<string> AllEntries { get; } = new();

        public override string ToString() => DisplayTitle;
    }

    /// <summary>
    /// Scans game/citadel/addons for user-installed *_dir.vpk files, inspects package entries,
    /// and classifies them into Full Model Replacement Mods or Texture-Only Mods.
    /// </summary>
    public static class DeadlockAddonScanner
    {
        private static readonly Regex HeroPathRegex = new(
            @"heroes(?:_staging|_wip)?/([^/]+)/",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );

        /// <summary>
        /// Scans the specified addons directory for all *_dir.vpk archives and inspects their contents.
        /// </summary>
        public static List<AddonModInfo> ScanAddons(string addonsDirectory)
        {
            var results = new List<AddonModInfo>();

            if (string.IsNullOrWhiteSpace(addonsDirectory) || !Directory.Exists(addonsDirectory))
            {
                return results;
            }

            try
            {
                var vpkFiles = Directory.GetFiles(addonsDirectory, "*_dir.vpk");
                Array.Sort(vpkFiles, StringComparer.OrdinalIgnoreCase);

                foreach (var filePath in vpkFiles)
                {
                    try
                    {
                        var info = InspectAddonPackage(filePath);
                        if (info != null)
                        {
                            results.Add(info);
                        }
                    }
                    catch (Exception ex)
                    {
                        GD.PrintErr($"[AddonScanner] Error inspecting '{Path.GetFileName(filePath)}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[AddonScanner] Failed to enumerate addons in '{addonsDirectory}': {ex.Message}");
            }

            return results;
        }

        /// <summary>
        /// Opens a single VPK package and classifies it based on embedded files.
        /// </summary>
        public static AddonModInfo InspectAddonPackage(string vpkPath)
        {
            if (!File.Exists(vpkPath)) return null;

            string fileName = Path.GetFileName(vpkPath);
            var modInfo = new AddonModInfo
            {
                FilePath = vpkPath,
                FileName = fileName
            };

            using var package = new Package();
            package.Read(vpkPath);

            var heroFrequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            void RegisterHeroCandidate(string path)
            {
                var match = HeroPathRegex.Match(path.Replace('\\', '/'));
                if (match.Success)
                {
                    string candidate = match.Groups[1].Value.ToLowerInvariant();
                    heroFrequency[candidate] = heroFrequency.GetValueOrDefault(candidate, 0) + 1;
                }
            }

            foreach (var typeList in package.Entries.Values)
            {
                foreach (var entry in typeList)
                {
                    string dir = string.IsNullOrEmpty(entry.DirectoryName) ? "" : entry.DirectoryName.Trim('/');
                    string fullPath = string.IsNullOrEmpty(dir)
                        ? $"{entry.FileName}.{entry.TypeName}"
                        : $"{dir}/{entry.FileName}.{entry.TypeName}";
                    modInfo.AllEntries.Add(fullPath);

                    string lower = fullPath.Replace('\\', '/').ToLowerInvariant();

                    // Only inspect files modifying heroes_wip, heroes_staging, or heroes character directories
                    bool isHeroFile = lower.Contains("heroes_staging/") ||
                                      lower.Contains("heroes_wip/") ||
                                      lower.Contains("/heroes/") ||
                                      lower.StartsWith("heroes/") ||
                                      lower.Contains("models/heroes") ||
                                      lower.Contains("materials/models/heroes");

                    if (!isHeroFile)
                    {
                        continue;
                    }

                    RegisterHeroCandidate(lower);

                    if (lower.EndsWith(".vmdl_c") || lower.EndsWith(".vmdl"))
                    {
                        // Exclude collision/ragdoll/physics models that cannot be rendered as character meshes
                        if (!lower.Contains("_physics") && !lower.Contains("_agdoll") && !lower.Contains("_hitbox"))
                        {
                            modInfo.ModelEntries.Add(fullPath);
                        }
                    }
                    else if (lower.EndsWith(".vmat_c") || lower.EndsWith(".vmat"))
                    {
                        modInfo.MaterialEntries.Add(fullPath);
                    }
                    else if (lower.EndsWith(".vtex_c") || lower.EndsWith(".vtex"))
                    {
                        modInfo.TextureEntries.Add(fullPath);
                    }
                }
            }

            // Exclude packages that do not modify character heroes folders (e.g. map, UI, sound, prop mods)
            if (modInfo.ModelEntries.Count == 0 && modInfo.TextureEntries.Count == 0 && modInfo.MaterialEntries.Count == 0)
            {
                GD.Print($"[AddonScanner] Skipping '{fileName}': does not modify heroes_wip, heroes_staging, or heroes.");
                return null;
            }

            // Fallback hero detection: if regex didn't find heroes/ in paths, scan against catalog codenames
            if (heroFrequency.Count == 0)
            {
                foreach (var hero in DeadlockHeroCatalog.Entries)
                {
                    string code = hero.InternalCodename.ToLowerInvariant();
                    int matches = modInfo.AllEntries.Count(e => e.IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (matches > 0)
                    {
                        heroFrequency[code] = matches;
                    }
                }
            }

            // Resolve target hero from highest frequency candidate and parse into real game display name (e.g. hornet_v3 -> Vindicta)
            if (heroFrequency.Count > 0)
            {
                string bestCandidate = heroFrequency.OrderByDescending(kvp => kvp.Value).First().Key;
                var catalogEntry = DeadlockHeroCatalog.ResolveHero(bestCandidate);

                if (catalogEntry != null)
                {
                    modInfo.DetectedHeroCodename = catalogEntry.InternalCodename;
                    modInfo.DetectedHeroDisplayName = catalogEntry.DisplayName;
                }
                else
                {
                    modInfo.DetectedHeroCodename = bestCandidate;
                    modInfo.DetectedHeroDisplayName = Capitalize(bestCandidate);
                }
            }

            // Sort model entries so primary character models precede accessories/weapons
            if (modInfo.ModelEntries.Count > 1)
            {
                modInfo.ModelEntries.Sort((a, b) =>
                {
                    int Score(string path)
                    {
                        string fn = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                        if (fn.Contains("_weapon") || fn.Contains("weapon_") || fn.Contains("_gun") || fn.Contains("_prop")) return 100;
                        if (fn.Contains("_cloth") || fn.Contains("_armor") || fn.Contains("_attachment")) return 50;
                        if (!string.IsNullOrEmpty(modInfo.DetectedHeroCodename) && fn.Equals(modInfo.DetectedHeroCodename, StringComparison.OrdinalIgnoreCase)) return -10;
                        return 0;
                    }
                    return Score(a).CompareTo(Score(b));
                });
            }

            // Classify mod type:
            // 1. If it contains .vmdl_c -> Full Model Replacement Mod
            // 2. If it contains .vtex_c or .vmat_c without a base model -> Texture-Only Mod
            if (modInfo.ModelEntries.Count > 0)
            {
                modInfo.ModType = AddonModType.FullModel;
            }
            else if (modInfo.TextureEntries.Count > 0 || modInfo.MaterialEntries.Count > 0)
            {
                modInfo.ModType = AddonModType.TextureOnly;
            }
            else
            {
                modInfo.ModType = AddonModType.Unknown;
            }

            // Format readable DisplayTitle: "Vindicta Model - pak_10_dir.vpk"
            string heroName = !string.IsNullOrEmpty(modInfo.DetectedHeroDisplayName)
                ? modInfo.DetectedHeroDisplayName
                : "Custom Character";

            string typeTag = modInfo.ModType switch
            {
                AddonModType.FullModel => $"{heroName} Model",
                AddonModType.TextureOnly => $"{heroName} Texture Mod",
                _ => $"{heroName} Mod"
            };

            modInfo.DisplayTitle = $"{typeTag} - {fileName}";

            return modInfo;
        }

        private static string Capitalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return char.ToUpperInvariant(text[0]) + (text.Length > 1 ? text.Substring(1) : "");
        }
    }
}
