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
        new("Calico", "nano", "models/heroes_staging/nano/nano_v2/nano.vmdl_c", HeroCategory.LegacyStaging),
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
        if (string.IsNullOrEmpty(codename)) return null;
        string lower = codename.ToLowerInvariant();
        return Entries.FirstOrDefault(e => e.InternalCodename.Equals(lower, StringComparison.OrdinalIgnoreCase) ||
                                           e.DisplayName.Equals(lower, StringComparison.OrdinalIgnoreCase));
    }

    public static DeadlockHeroEntry GetByPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string normalized = path.Replace('\\', '/').ToLowerInvariant();
        return Entries.FirstOrDefault(e => e.VmdlRelativePath.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }
}
