using System;
using System.Collections.Generic;
using System.IO;
using DeadlockPlayground.Particles.Models;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace DeadlockPlayground.Particles;

/// <summary>
/// High-speed single-pass streaming / DOM parser for abilities.vdata_c that extracts
/// all hero abilities and referenced .vpcf particle paths without hardcoded hero dictionaries.
/// </summary>
public static class FastVDataParticleIndexer
{
    private const string VpcfExtension = ".vpcf";

    public static HeroParticleIndex ParseFromFile(string vdataFilePath)
    {
        if (!File.Exists(vdataFilePath))
        {
            throw new FileNotFoundException($"Cannot find vdata file at {vdataFilePath}", vdataFilePath);
        }

        using var resource = new Resource();
        resource.Read(vdataFilePath);
        return ParseFromResource(resource);
    }

    public static HeroParticleIndex ParseFromStream(Stream stream)
    {
        using var resource = new Resource();
        resource.Read(stream);
        return ParseFromResource(resource);
    }

    public static HeroParticleIndex ParseFromResource(Resource resource)
    {
        if (resource?.DataBlock is not BinaryKV3 kv3)
        {
            throw new InvalidDataException("Target resource is not a valid BinaryKV3 DataBlock.");
        }

        var root = kv3.Data.Root;
        var heroesMap = new Dictionary<string, List<AbilityParticleGroup>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in root.Children)
        {
            var abilityObj = entry.Value;
            if (abilityObj == null || abilityObj.IsNull) continue;

            string heroName = ExtractHeroCategory(abilityObj, entry.Key);
            if (string.IsNullOrEmpty(heroName))
            {
                continue;
            }

            // Normalize to canonical display name if known
            if (HeroParticleService.TryResolveHeroCanonicalName(heroName, out string canonicalHero))
            {
                heroName = canonicalHero;
            }

            string abilityKey = entry.Key;
            string locToken = $"#{abilityKey}";

            var particles = new List<ParticleEntry>(8);
            ExtractParticlesRecursive(abilityObj, string.Empty, particles, heroName);

            if (particles.Count > 0)
            {
                if (!heroesMap.TryGetValue(heroName, out var groupList))
                {
                    groupList = new List<AbilityParticleGroup>();
                    heroesMap[heroName] = groupList;
                }

                groupList.Add(new AbilityParticleGroup(abilityKey, locToken, particles));
            }
        }

        var result = new Dictionary<string, IReadOnlyList<AbilityParticleGroup>>(heroesMap.Count * 2, StringComparer.OrdinalIgnoreCase);
        foreach (var (hero, abilities) in heroesMap)
        {
            result[hero] = abilities;

            // Also index under canonical localized display name so direct queries by "Grey Talon", "Paradox", etc. succeed
            string display = HeroParticleService.ResolveDisplayName(hero);
            if (!string.IsNullOrEmpty(display) && !result.ContainsKey(display))
            {
                result[display] = abilities;
            }
        }

        return new HeroParticleIndex(result);
    }

    private static string ExtractHeroCategory(KVObject abilityObj, string abilityKey)
    {
        // 1. Preferred source: _editor.folder_name (e.g. "Hero Dynamo" -> "Dynamo", "Hero Orion" -> "Orion")
        if (abilityObj.TryGetValue("_editor", out var editorObj) && editorObj != null && !editorObj.IsNull)
        {
            if (editorObj.TryGetValue("folder_name", out var folderObj) && folderObj != null && !folderObj.IsNull)
            {
                string folder = folderObj.ToString()?.Trim();
                if (!string.IsNullOrEmpty(folder))
                {
                    if (folder.StartsWith("Hero ", StringComparison.OrdinalIgnoreCase))
                    {
                        return folder.Substring(5).Trim();
                    }
                }
            }
        }

        // 2. Secondary source: infer from ability key (e.g. "citadel_ability_archer_..." -> "Archer")
        if (!string.IsNullOrEmpty(abilityKey))
        {
            var parts = abilityKey.Split('_');
            if (parts.Length >= 3 && parts[0].Equals("citadel", StringComparison.OrdinalIgnoreCase) &&
                (parts[1].Equals("ability", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("weapon", StringComparison.OrdinalIgnoreCase)))
            {
                string candidate = parts[2];
                string display = HeroParticleService.ResolveDisplayName(candidate);
                if (!string.IsNullOrEmpty(display) && !display.Equals("Hero", StringComparison.OrdinalIgnoreCase))
                {
                    return display;
                }
            }
        }

        return null;
    }

    private static void ExtractParticlesRecursive(KVObject obj, string prefix, List<ParticleEntry> output, string heroName)
    {
        if (obj == null || obj.IsNull) return;

        foreach (var child in obj.Children)
        {
            var value = child.Value;
            if (value == null || value.IsNull) continue;

            if (value.Count > 0)
            {
                // Skip upgrades array as it only has numeric delta stats
                if (child.Key == "m_vecAbilityUpgrades" || child.Key == "m_vecPropertyUpgrades") continue;

                string cleanSubName = CleanSubBlockName(child.Key);
                string nextPrefix = string.IsNullOrEmpty(prefix) ? cleanSubName : $"{prefix} / {cleanSubName}";
                ExtractParticlesRecursive(value, nextPrefix, output, heroName);
            }
            else
            {
                try
                {
                    string valStr = value.ToString();
                    if (!string.IsNullOrEmpty(valStr) && valStr.EndsWith(VpcfExtension, StringComparison.OrdinalIgnoreCase))
                    {
                        // Filter out particles owned by another hero (prevents cross-hero leaks)
                        if (!string.IsNullOrEmpty(heroName) && !HeroParticleService.IsParticleOwnedByHero(valStr, heroName))
                        {
                            continue;
                        }

                        string cleanProp = CleanPropertyName(child.Key);
                        string variantName = string.IsNullOrEmpty(prefix) ? cleanProp : $"{prefix} / {cleanProp}";

                        // De-duplicate if same path already recorded in this ability
                        bool exists = false;
                        for (int i = 0; i < output.Count; i++)
                        {
                            if (output[i].VpcfPath.Equals(valStr, StringComparison.OrdinalIgnoreCase))
                            {
                                exists = true;
                                break;
                            }
                        }

                        if (!exists)
                        {
                            output.Add(new ParticleEntry(variantName, valStr));
                        }
                    }
                }
                catch
                {
                    // Ignore conversion exceptions on raw binary values
                }
            }
        }
    }

    public static string CleanPropertyName(string key)
    {
        if (string.IsNullOrEmpty(key)) return "Default";
        ReadOnlySpan<char> span = key.AsSpan();

        if (span.StartsWith("m_str", StringComparison.OrdinalIgnoreCase)) span = span[5..];
        else if (span.StartsWith("m_sz", StringComparison.OrdinalIgnoreCase)) span = span[4..];
        else if (span.StartsWith("m_s", StringComparison.OrdinalIgnoreCase)) span = span[3..];
        else if (span.StartsWith("m_", StringComparison.OrdinalIgnoreCase)) span = span[2..];

        if (span.EndsWith("Particle", StringComparison.OrdinalIgnoreCase)) span = span[..^8];
        else if (span.EndsWith("Effect", StringComparison.OrdinalIgnoreCase)) span = span[..^6];

        if (span.Length == 0) return "Default";
        return span.ToString();
    }

    public static string CleanSubBlockName(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        ReadOnlySpan<char> span = key.AsSpan();

        if (span.StartsWith("m_map", StringComparison.OrdinalIgnoreCase)) span = span[5..];
        else if (span.StartsWith("m_vec", StringComparison.OrdinalIgnoreCase)) span = span[5..];
        else if (span.StartsWith("m_", StringComparison.OrdinalIgnoreCase)) span = span[2..];

        if (span.EndsWith("Modifier", StringComparison.OrdinalIgnoreCase)) span = span[..^8];

        if (span.Length == 0) return key;
        return span.ToString();
    }
}
