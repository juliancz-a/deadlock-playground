using System;
using System.Collections.Generic;
using System.Linq;

namespace DeadlockPlayground.Catalog;

public enum HeroCategory
{
    UpdatedAndNew,
    LegacyStaging
}

public record DeadlockHeroEntry(
    string DisplayName,
    string InternalCodename,
    string VmdlRelativePath,
    HeroCategory Category
);

public static class DeadlockHeroCatalog
{
    public static readonly IReadOnlyList<DeadlockHeroEntry> Entries = new List<DeadlockHeroEntry>
    {
        // ==========================================
        // Category: Updated & New Heroes
        // ==========================================
        new("Abrams", "abrams", "models/heroes_wip/abrams/abrams.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Apollo", "fencer", "models/heroes_wip/fencer/fencer.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Bebop", "bebop", "models/heroes_staging/bebop/bebop.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Billy", "punkgoat", "models/heroes_wip/punkgoat/punkgoat.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Calico", "nano", "models/heroes_staging/nano/nano_v2/nano.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Celeste", "unicorn", "models/heroes_wip/unicorn/unicorn.vmdl_c", HeroCategory.UpdatedAndNew),
        new("The Doorman", "doorman", "models/heroes_wip/doorman_v2/doorman.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Drifter", "drifter", "models/heroes_wip/drifter/drifter.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Dynamo", "dynamo", "models/heroes_wip/dynamo/dynamo.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Graves", "necro", "models/heroes_wip/necro/necro.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Grey Talon", "archer", "models/heroes_staging/archer/archer.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Haze", "haze", "models/heroes_staging/haze/haze.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Holliday", "astro", "models/heroes_staging/astro/astro.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Infernus", "inferno", "models/heroes_staging/inferno_v4/inferno.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Ivy", "ivy", "models/heroes_wip/ivy/ivy.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Kelvin", "kelvin", "models/heroes_staging/kelvin_v2/kelvin.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Lady Geist", "geist", "models/heroes_wip/geist/geist.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Lash", "lash", "models/heroes_wip/lash/lash.vmdl_c", HeroCategory.UpdatedAndNew),
        new("McGinnis", "mcginnis", "models/heroes_wip/mcginnis/mcginnis.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Mina", "vampirebat", "models/heroes_wip/vampirebat/vampirebat.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Mirage", "mirage", "models/heroes_staging/mirage_v2/mirage.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Mo & Krill", "digger", "models/heroes_staging/digger/digger.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Paige", "bookworm", "models/heroes_wip/bookworm/bookworm.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Paradox", "chrono", "models/heroes_staging/chrono/chrono.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Pocket", "pocket", "models/heroes_wip/pocket/pocket.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Rem", "familiar", "models/heroes_wip/familiar/familiar_wip.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Seven", "gigawatt_prisoner", "models/heroes_staging/gigawatt_prisoner/gigawatt_prisoner.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Shiv", "shiv", "models/heroes_staging/shiv/shiv.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Silver", "werewolf", "models/heroes_wip/werewolf/werewolf.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Silver (Transformed / Ult)", "werewolf_transform", "models/heroes_wip/werewolf/transform/werewolf_transform.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Sinclair", "magician", "models/heroes_staging/magician_v2/magician.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Venator", "priest", "models/heroes_wip/priest/priest.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Victor", "frank", "models/heroes_wip/frank/frank.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Vindicta", "hornet", "models/heroes_staging/hornet_v3/hornet.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Viscous", "viscous", "models/heroes_staging/viscous/viscous.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Viscous (Inflated / Ult)", "viscous_inflated", "models/heroes_staging/viscous/viscous_inflated.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Vyper", "viper", "models/heroes_staging/viper/viper.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Warden", "warden", "models/heroes_staging/warden/warden.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Wraith", "wraith", "models/heroes_wip/wraith/wraith.vmdl_c", HeroCategory.UpdatedAndNew),
        new("Yamato", "yamato", "models/heroes_staging/yamato_v2/yamato.vmdl_c", HeroCategory.UpdatedAndNew),

        // ==========================================
        // Category: Legacy / Prototype Heroes
        // ==========================================
        new("Abrams (Old)", "atlas_detective", "models/heroes_staging/atlas_detective_v2/atlas_detective.vmdl_c", HeroCategory.LegacyStaging),
        new("Dynamo (Old)", "prof_dynamo", "models/heroes_staging/prof_dynamo/prof_dynamo.vmdl_c", HeroCategory.LegacyStaging),
        new("Grey Talon (Old / Unused)", "archer_v2", "models/heroes_staging/archer_v2/archer_v2.vmdl_c", HeroCategory.LegacyStaging),
        new("Lady Geist (Old)", "ghost", "models/heroes_staging/ghost/ghost.vmdl_c", HeroCategory.LegacyStaging),
        new("McGinnis (Old)", "engineer", "models/heroes_staging/engineer/engineer.vmdl_c", HeroCategory.LegacyStaging),
        new("Pocket (Old)", "synth", "models/heroes_staging/synth/synth.vmdl_c", HeroCategory.LegacyStaging),
        new("Sinclair (Old)", "magician_old", "models/heroes_staging/magician/magician.vmdl_c", HeroCategory.LegacyStaging),
        new("Wraith (Old)", "wraith_old", "models/heroes_staging/wraith/wraith.vmdl_c", HeroCategory.LegacyStaging)
    };

    public static IEnumerable<DeadlockHeroEntry> UpdatedHeroes =>
        Entries.Where(e => e.Category == HeroCategory.UpdatedAndNew);

    public static IEnumerable<DeadlockHeroEntry> LegacyHeroes =>
        Entries.Where(e => e.Category == HeroCategory.LegacyStaging);

    public static DeadlockHeroEntry GetByCodename(string codename)
    {
        return ResolveHero(codename);
    }

    /// <summary>
    /// Resolves any directory name, internal codename, or model folder (e.g. "hornet_v3", "inferno_v4")
    /// to its authentic in-game hero display name and catalog entry.
    /// </summary>
    public static DeadlockHeroEntry ResolveHero(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;

        string clean = candidate.Trim().ToLowerInvariant();

        // 1. Direct match on InternalCodename or DisplayName
        var match = Entries.FirstOrDefault(e =>
            e.InternalCodename.Equals(clean, StringComparison.OrdinalIgnoreCase) ||
            e.DisplayName.Equals(clean, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        // 2. Check if any hero's VmdlRelativePath contains this directory (e.g. "/hornet_v3/", "/inferno_v4/")
        match = Entries.FirstOrDefault(e =>
            e.VmdlRelativePath.IndexOf($"/{clean}/", StringComparison.OrdinalIgnoreCase) >= 0 ||
            e.VmdlRelativePath.IndexOf($"/{clean}.", StringComparison.OrdinalIgnoreCase) >= 0);
        if (match != null) return match;

        // 3. Strip version suffixes like _v2, _v3, _v4
        string stripped = System.Text.RegularExpressions.Regex.Replace(clean, @"_v\d+$", "");
        if (!stripped.Equals(clean, StringComparison.OrdinalIgnoreCase))
        {
            match = ResolveHero(stripped);
            if (match != null) return match;
        }

        // 4. Strip _wip, _staging, _old suffixes
        string strippedExtra = System.Text.RegularExpressions.Regex.Replace(clean, @"_(?:wip|staging|old)$", "");
        if (!strippedExtra.Equals(clean, StringComparison.OrdinalIgnoreCase))
        {
            match = ResolveHero(strippedExtra);
            if (match != null) return match;
        }

        // 5. Special aliases dictionary for known Deadlock internal dev names
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "hornet", "hornet" },
            { "hornet_v3", "hornet" },
            { "inferno", "inferno" },
            { "inferno_v4", "inferno" },
            { "gigawatt", "gigawatt_prisoner" },
            { "gigawatt_prisoner", "gigawatt_prisoner" },
            { "seven", "gigawatt_prisoner" },
            { "fencer", "fencer" },
            { "apollo", "fencer" },
            { "punkgoat", "punkgoat" },
            { "billy", "punkgoat" },
            { "nano", "nano" },
            { "nano_v2", "nano" },
            { "calico", "nano" },
            { "unicorn", "unicorn" },
            { "celeste", "unicorn" },
            { "doorman", "doorman" },
            { "doorman_v2", "doorman" },
            { "necro", "necro" },
            { "graves", "necro" },
            { "archer", "archer" },
            { "archer_v2", "archer_v2" },
            { "grey talon", "archer" },
            { "astro", "astro" },
            { "holliday", "astro" },
            { "ghost", "ghost" },
            { "geist", "geist" },
            { "lady geist", "geist" },
            { "engineer", "engineer" },
            { "mcginnis", "mcginnis" },
            { "vampirebat", "vampirebat" },
            { "mina", "vampirebat" },
            { "digger", "digger" },
            { "mo & krill", "digger" },
            { "bookworm", "bookworm" },
            { "paige", "bookworm" },
            { "chrono", "chrono" },
            { "paradox", "chrono" },
            { "synth", "synth" },
            { "pocket", "pocket" },
            { "familiar", "familiar" },
            { "familiar_wip", "familiar" },
            { "rem", "familiar" },
            { "werewolf", "werewolf" },
            { "silver", "werewolf" },
            { "magician", "magician" },
            { "magician_v2", "magician" },
            { "sinclair", "magician" },
            { "priest", "priest" },
            { "venator", "priest" },
            { "frank", "frank" },
            { "victor", "frank" },
            { "vindicta", "hornet" },
            { "viper", "viper" },
            { "vyper", "viper" },
            { "atlas_detective", "atlas_detective" },
            { "atlas_detective_v2", "atlas_detective" },
            { "prof_dynamo", "prof_dynamo" }
        };

        if (aliases.TryGetValue(clean, out var aliasCode))
        {
            match = Entries.FirstOrDefault(e => e.InternalCodename.Equals(aliasCode, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        // 6. Substring check
        match = Entries.FirstOrDefault(e =>
            clean.Contains(e.InternalCodename, StringComparison.OrdinalIgnoreCase) ||
            clean.Contains(e.DisplayName.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        return null;
    }

    public static DeadlockHeroEntry GetByPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string normalized = path.Replace('\\', '/').ToLowerInvariant();
        return Entries.FirstOrDefault(e => e.VmdlRelativePath.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }
}
