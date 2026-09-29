using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using DeadlockPlayground.Catalog;

namespace DeadlockPlayground.UI;

/// <summary>
/// Handles dedicated hero selection grouping, visual accent styling for WIP update heroes,
/// and alias-aware search and filtering across the playground catalog.
/// </summary>
public static class HeroSelectionUI
{
    public static readonly Color NewHeroAccentColor = new Color(1.0f, 0.82f, 0.28f); // Gold/Amber accent
    public static readonly Color HeaderAccentColor = new Color(0.95f, 0.70f, 0.20f);

    public static string FormatNewHeroItem(DeadlockHeroEntry hero)
    {
        string aliasPart = !string.IsNullOrEmpty(hero.WipAlias) &&
                           !hero.WipAlias.Equals(hero.DisplayName, StringComparison.OrdinalIgnoreCase) &&
                           !hero.WipAlias.Equals(hero.InternalCodename, StringComparison.OrdinalIgnoreCase)
            ? $" ({hero.WipAlias})"
            : "";
        return $"  {hero.DisplayName} [NEW]";
    }

    public static void PopulateUpdateHeroesDropdown(OptionButton optionButton, List<DeadlockHeroEntry> selectableHeroes)
    {
        if (optionButton == null || selectableHeroes == null) return;

        optionButton.Clear();
        selectableHeroes.Clear();

        // Placeholder item (index 0)
        optionButton.AddItem("Select New Hero (Update)...", -1);
        optionButton.SetItemDisabled(0, true);

        foreach (var hero in DeadlockHeroCatalog.NewUpdateHeroes)
        {
            int currentId = selectableHeroes.Count;
            selectableHeroes.Add(hero);
            string formattedText = FormatNewHeroItem(hero);
            optionButton.AddItem(formattedText, currentId);
        }
    }

    public static void PopulateStandardHeroesDropdown(OptionButton optionButton, List<DeadlockHeroEntry> selectableHeroes)
    {
        if (optionButton == null || selectableHeroes == null) return;

        optionButton.Clear();
        selectableHeroes.Clear();

        // Placeholder item (index 0)
        optionButton.AddItem("Select a Hero...", -1);
        optionButton.SetItemDisabled(0, true);

        // 1. SECTION: STANDARD HEROES
        optionButton.AddItem("── Heroes ──", -1);
        int heroHeaderIndex = optionButton.ItemCount - 1;
        optionButton.SetItemDisabled(heroHeaderIndex, true);

        foreach (var hero in DeadlockHeroCatalog.UpdatedHeroes)
        {
            int currentId = selectableHeroes.Count;
            selectableHeroes.Add(hero);
            optionButton.AddItem($"  {hero.DisplayName}", currentId);
        }

        // 2. SECTION: LEGACY / PROTOTYPE HEROES
        optionButton.AddItem("── Legacy / Prototype Heroes ──", -1);
        int legacyHeaderIndex = optionButton.ItemCount - 1;
        optionButton.SetItemDisabled(legacyHeaderIndex, true);

        foreach (var hero in DeadlockHeroCatalog.LegacyHeroes)
        {
            int currentId = selectableHeroes.Count;
            selectableHeroes.Add(hero);
            optionButton.AddItem($"  {hero.DisplayName}", currentId);
        }
    }

    public static void PopulateDropdown(OptionButton optionButton, List<DeadlockHeroEntry> selectableHeroes)
    {
        if (optionButton == null || selectableHeroes == null) return;

        optionButton.Clear();
        selectableHeroes.Clear();

        var popup = optionButton.GetPopup();

        // Placeholder item (index 0)
        optionButton.AddItem("Select a Hero...", -1);
        optionButton.SetItemDisabled(0, true);

        // 1. SECTION: NEW HEROES (UPDATE)
        optionButton.AddItem("── NEW HEROES (UPDATE) ──", -1);
        int newHeaderIndex = optionButton.ItemCount - 1;
        optionButton.SetItemDisabled(newHeaderIndex, true);
        foreach (var hero in DeadlockHeroCatalog.NewUpdateHeroes)
        {
            int currentId = selectableHeroes.Count;
            selectableHeroes.Add(hero);
            string formattedText = FormatNewHeroItem(hero);
            optionButton.AddItem(formattedText, currentId);
        }

        // 2. SECTION: STANDARD HEROES
        optionButton.AddItem("── Heroes ──", -1);
        int heroHeaderIndex = optionButton.ItemCount - 1;
        optionButton.SetItemDisabled(heroHeaderIndex, true);

        foreach (var hero in DeadlockHeroCatalog.UpdatedHeroes)
        {
            int currentId = selectableHeroes.Count;
            selectableHeroes.Add(hero);
            optionButton.AddItem($"  {hero.DisplayName}", currentId);
        }

        // 3. SECTION: LEGACY / PROTOTYPE HEROES
        optionButton.AddItem("── Legacy / Prototype Heroes ──", -1);
        int legacyHeaderIndex = optionButton.ItemCount - 1;
        optionButton.SetItemDisabled(legacyHeaderIndex, true);

        foreach (var hero in DeadlockHeroCatalog.LegacyHeroes)
        {
            int currentId = selectableHeroes.Count;
            selectableHeroes.Add(hero);
            optionButton.AddItem($"  {hero.DisplayName}", currentId);
        }
    }

    public static IEnumerable<DeadlockHeroEntry> FilterHeroes(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return DeadlockHeroCatalog.Entries;
        }

        string clean = query.Trim().ToLowerInvariant();

        return DeadlockHeroCatalog.Entries.Where(e =>
            e.DisplayName.Contains(clean, StringComparison.OrdinalIgnoreCase) ||
            e.InternalCodename.Contains(clean, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(e.WipAlias) && e.WipAlias.Contains(clean, StringComparison.OrdinalIgnoreCase)));
    }
}
