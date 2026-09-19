using Godot;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using DeadlockPlayground.Catalog;
using DeadlockPlayground.Addons;

public partial class CharacterTabUI : VBoxContainer
{
    [Signal] public delegate void CharacterRequestedEventHandler(string internalId);

    [Export] private OptionButton _heroOptionButton;
    [Export] private OptionButton _optAddonMods;
    [Export] private RichTextLabel _lblAddonDetails;
    [Export] private Container _submeshContainer;
    [Export] private Button _btnShowAll;
    [Export] private Button _btnHideAccessories;
    [Export] private VpkLoaderTest _vpkLoader;

    public class SubmeshItem
    {
        public MeshInstance3D Mesh { get; set; }
        public string RawName { get; set; }
        public string DisplayName { get; set; }
        public bool IsAccessory { get; set; }
        public CheckBox CheckBoxWidget { get; set; }
    }

    private readonly List<SubmeshItem> _submeshes = new();
    private readonly List<DeadlockHeroEntry> _selectableHeroes = new();
    private readonly List<AddonModInfo> _addonMods = new();

    public override void _Ready()
    {
        ConnectEvents();

        if (_vpkLoader == null)
        {
            _vpkLoader = GetNodeOrNull<VpkLoaderTest>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/VpkLoaderTest")
                      ?? GetNodeOrNull<VpkLoaderTest>("/root/Main/VpkLoaderTest")
                      ?? GetTree().Root.FindChild("VpkLoaderTest", true, false) as VpkLoaderTest;
        }

        PopulateAddonDropdown();
        PopulateHeroDropdown();

        if (_vpkLoader != null)
        {
            _vpkLoader.HeroLoaded += SetHero;
            _vpkLoader.HeroUnloaded += ClearSubmeshes;
            if (_vpkLoader.CurrentHeroNode != null)
            {
                SetHero(_vpkLoader.CurrentHeroNode);
            }
            else
            {
                SetControlsEnabled(false);
            }
        }
        else
        {
            SetControlsEnabled(false);
        }
    }

    public void SetControlsEnabled(bool enabled)
    {
        if (_btnShowAll != null) _btnShowAll.Disabled = !enabled;
        if (_btnHideAccessories != null) _btnHideAccessories.Disabled = !enabled;
        if (_submeshContainer != null)
        {
            _submeshContainer.Modulate = enabled ? Colors.White : new Color(1, 1, 1, 0.4f);
        }
    }

    private void PopulateHeroDropdown()
    {
        if (_heroOptionButton == null) return;

        _heroOptionButton.Clear();
        _selectableHeroes.Clear();

        _heroOptionButton.AddItem("Select a Hero...", -1);
        _heroOptionButton.SetItemDisabled(0, true);

        // Header: -- Heroes --
        _heroOptionButton.AddItem("-- Heroes --", -1);
        int heroHeaderIndex = _heroOptionButton.ItemCount - 1;
        _heroOptionButton.SetItemDisabled(heroHeaderIndex, true);

        foreach (var hero in DeadlockHeroCatalog.UpdatedHeroes)
        {
            int currentId = _selectableHeroes.Count;
            _selectableHeroes.Add(hero);
            _heroOptionButton.AddItem($"  {hero.DisplayName}", currentId);
        }

        // Header: -- Legacy / Prototype Heroes --
        _heroOptionButton.AddItem("-- Legacy / Prototype Heroes --", -1);
        int legacyHeaderIndex = _heroOptionButton.ItemCount - 1;
        _heroOptionButton.SetItemDisabled(legacyHeaderIndex, true);

        foreach (var hero in DeadlockHeroCatalog.LegacyHeroes)
        {
            int currentId = _selectableHeroes.Count;
            _selectableHeroes.Add(hero);
            _heroOptionButton.AddItem($"  {hero.DisplayName}", currentId);
        }

        if (_vpkLoader?.LastLoadedHeroEntry != null)
        {
            SyncHeroDropdownToHero(_vpkLoader.LastLoadedHeroEntry);
        }
        else
        {
            _heroOptionButton.Select(0);
        }
    }

    private void ConnectEvents()
    {
        if (_heroOptionButton != null)
        {
            _heroOptionButton.ItemSelected += OnHeroSelected;
        }

        if (_optAddonMods != null)
        {
            _optAddonMods.ItemSelected += OnAddonSelected;
        }

        if (_btnShowAll != null)
        {
            _btnShowAll.Pressed += ShowAll;
        }

        if (_btnHideAccessories != null)
        {
            _btnHideAccessories.Pressed += HideAccessories;
        }
    }

    public void PopulateAddonDropdown()
    {
        if (_optAddonMods == null) return;

        _optAddonMods.Clear();
        _addonMods.Clear();

        string addonsDir = null;
        if (_vpkLoader != null && !string.IsNullOrEmpty(_vpkLoader.VpkPath))
        {
            string citadelDir = Path.GetDirectoryName(_vpkLoader.VpkPath);
            if (!string.IsNullOrEmpty(citadelDir))
            {
                addonsDir = Path.Combine(citadelDir, "addons");
            }
        }

        if (string.IsNullOrEmpty(addonsDir) || !Directory.Exists(addonsDir))
        {
            var pathManager = new GamePathManager();
            pathManager.LoadConfig();
            if (!string.IsNullOrEmpty(pathManager.CurrentGamePath))
            {
                addonsDir = Path.Combine(pathManager.CurrentGamePath, "game", "citadel", "addons");
            }
        }

        if (!string.IsNullOrEmpty(addonsDir))
        {
            ScanAndPopulateAddons(addonsDir);
        }
        else
        {
            SetNoAddonsAvailableUI();
        }
    }

    private void SetNoAddonsAvailableUI()
    {
        if (_optAddonMods == null) return;

        _optAddonMods.Clear();
        _addonMods.Clear();
        _optAddonMods.AddItem("No modded character models available", -1);
        _optAddonMods.SetItemDisabled(0, true);
        _optAddonMods.Disabled = true;

        if (_lblAddonDetails != null)
        {
            _lblAddonDetails.Text = "[color=#777777]No modded character packages found in citadel/addons/[/color]";
        }
    }

    private void ScanAndPopulateAddons(string addonsDir)
    {
        var detected = DeadlockAddonScanner.ScanAddons(addonsDir);

        if (detected.Count == 0)
        {
            SetNoAddonsAvailableUI();
            GD.Print($"[CharacterTab] Scanned addons in '{addonsDir}': no modded character models found.");
            return;
        }

        _optAddonMods.Disabled = false;
        _optAddonMods.Clear();
        _addonMods.Clear();

        // Default entry: "None (Vanilla)" (Index 0)
        _optAddonMods.AddItem("None (Vanilla)", 0);

        foreach (var mod in detected)
        {
            int currentId = _addonMods.Count + 1;
            _addonMods.Add(mod);
            _optAddonMods.AddItem(mod.DisplayTitle, currentId);
        }

        _optAddonMods.Select(0);
        if (_lblAddonDetails != null)
        {
            _lblAddonDetails.Text = "[color=#888888]Vanilla Assets (pak01_dir.vpk)[/color]";
        }

        GD.Print($"[CharacterTab] Scanned addons in '{addonsDir}': {detected.Count} modded character models found.");
    }

    private async void OnAddonSelected(long index)
    {
        if (_optAddonMods == null) return;
        int id = _optAddonMods.GetItemId((int)index);

        if (id <= 0 || id - 1 >= _addonMods.Count)
        {
            GD.Print("[CharacterTab] Addon selected: None (Vanilla)");
            _vpkLoader?.SetActiveAddon(null, null);

            if (_lblAddonDetails != null)
            {
                _lblAddonDetails.Text = "[color=#888888]Vanilla Assets (pak01_dir.vpk)[/color]";
            }

            // If a character was loaded, restore its vanilla model/textures
            if (_vpkLoader != null)
            {
                GD.Print("[CharacterTab] Restoring vanilla model/textures...");
                await _vpkLoader.ReloadCurrentHeroAsync();
            }

            SyncHeroDropdownToCurrent();
            return;
        }

        var mod = _addonMods[id - 1];
        GD.Print($"[CharacterTab] Addon selected: {mod.DisplayTitle} ({mod.FilePath})");
        _vpkLoader?.SetActiveAddon(mod.FilePath, mod);

        // Update details label with subtle grey VPK filename
        string heroName = !string.IsNullOrEmpty(mod.DetectedHeroDisplayName) ? mod.DetectedHeroDisplayName : "Custom Character";
        string typeText = mod.ModType == AddonModType.FullModel ? $"{heroName} Model" : $"{heroName} Texture Mod";
        if (_lblAddonDetails != null)
        {
            _lblAddonDetails.Text = $"[color=#dcdcdc]{typeText}[/color]  [color=#777777]—[/color]  [color=#8a8a8a]{mod.FileName}[/color]";
        }

        // Case 1: Full Model Mod -> Load addon model directly without needing prior vanilla model
        if (mod.ModType == AddonModType.FullModel || mod.ModelEntries.Count > 0)
        {
            GD.Print($"[CharacterTab] Loading Full Model addon directly: {mod.DisplayTitle}...");
            if (!string.IsNullOrEmpty(mod.DetectedHeroCodename))
            {
                SyncHeroDropdownToHeroCodename(mod.DetectedHeroCodename);
            }

            if (_vpkLoader != null)
            {
                await _vpkLoader.LoadAddonModelAsync(mod);
            }
            return;
        }

        // Case 2: Texture-Only Mod -> Load base hero model with injected addon textures
        if (mod.ModType == AddonModType.TextureOnly)
        {
            if (!string.IsNullOrEmpty(mod.DetectedHeroCodename))
            {
                var catalogEntry = DeadlockHeroCatalog.ResolveHero(mod.DetectedHeroCodename);
                if (catalogEntry != null)
                {
                    GD.Print($"[CharacterTab] Loading target hero '{catalogEntry.DisplayName}' with textures from '{mod.FileName}'...");
                    SyncHeroDropdownToHero(catalogEntry);

                    if (_vpkLoader != null)
                    {
                        await _vpkLoader.LoadHeroModelAsync(catalogEntry);
                    }
                    return;
                }
            }

            // Fallback: If a character is currently loaded, reload it with updated textures
            if (_vpkLoader != null && (_vpkLoader.LastLoadedHeroEntry != null || !string.IsNullOrEmpty(_vpkLoader.LastLoadedVmdlPath)))
            {
                GD.Print("[CharacterTab] Reloading current character with texture mod...");
                await _vpkLoader.ReloadCurrentHeroAsync();
            }
        }
    }

    private async void OnHeroSelected(long index)
    {
        if (_heroOptionButton == null) return;
        int id = _heroOptionButton.GetItemId((int)index);
        if (id < 0 || id >= _selectableHeroes.Count) return;

        var entry = _selectableHeroes[id];
        GD.Print($"[CharacterTab] Loading vanilla hero: {entry.DisplayName} ({entry.InternalCodename}) from {entry.VmdlRelativePath}");
        EmitSignal(SignalName.CharacterRequested, entry.InternalCodename);

        // If active addon is a full model replacement, reset addon selector to Vanilla so it doesn't conflict
        if (_vpkLoader != null && _vpkLoader.ActiveAddonInfo != null && _vpkLoader.ActiveAddonInfo.ModType == AddonModType.FullModel)
        {
            _vpkLoader.SetActiveAddon(null, null);
            if (_optAddonMods != null && !_optAddonMods.Disabled)
            {
                _optAddonMods.Select(0);
            }
            if (_lblAddonDetails != null)
            {
                _lblAddonDetails.Text = "[color=#888888]Vanilla Assets (pak01_dir.vpk)[/color]";
            }
        }

        if (_vpkLoader != null)
        {
            await _vpkLoader.LoadHeroModelAsync(entry);
        }
    }

    private void SelectOptionByItemId(OptionButton optionButton, int targetId)
    {
        if (optionButton == null) return;
        for (int i = 0; i < optionButton.ItemCount; i++)
        {
            if (optionButton.GetItemId(i) == targetId)
            {
                optionButton.Select(i);
                return;
            }
        }
    }

    private void SyncHeroDropdownToHero(DeadlockHeroEntry hero)
    {
        if (_heroOptionButton == null || hero == null) return;
        for (int i = 0; i < _selectableHeroes.Count; i++)
        {
            var entry = _selectableHeroes[i];
            if (entry.InternalCodename.Equals(hero.InternalCodename, StringComparison.OrdinalIgnoreCase))
            {
                SelectOptionByItemId(_heroOptionButton, i);
                return;
            }
        }
    }

    private void SyncHeroDropdownToHeroCodename(string codename)
    {
        if (string.IsNullOrEmpty(codename)) return;
        var hero = DeadlockHeroCatalog.ResolveHero(codename);
        if (hero != null)
        {
            SyncHeroDropdownToHero(hero);
        }
    }

    private void SyncHeroDropdownToCurrent()
    {
        if (_vpkLoader?.LastLoadedHeroEntry != null)
        {
            SyncHeroDropdownToHero(_vpkLoader.LastLoadedHeroEntry);
        }
    }

    public void SetHero(Node3D heroNode)
    {
        ClearSubmeshes();
        if (heroNode == null)
        {
            SetControlsEnabled(false);
            return;
        }

        CollectSubmeshesRecursive(heroNode);
        PopulateSubmeshList();
        SetControlsEnabled(true);
    }

    public void ClearSubmeshes()
    {
        if (_submeshContainer != null)
        {
            foreach (Node child in _submeshContainer.GetChildren())
            {
                child.QueueFree();
            }
        }
        _submeshes.Clear();
        SetControlsEnabled(false);
    }

    private void CollectSubmeshesRecursive(Node node)
    {
        if (node is MeshInstance3D mi && mi.Mesh != null)
        {
            string rawName = mi.Name.ToString();
            string lowerName = rawName.ToLowerInvariant();

            // Skip internal split surfaces created by HeroMeshHierarchy so only high-level submeshes appear
            if (mi.HasMeta("ParentCompositeMesh") || lowerName.Contains("_surf_"))
            {
                return;
            }

            // Skip bone markers and gizmos
            if (!lowerName.Contains("marker") && !lowerName.Contains("gizmo") && !lowerName.Contains("preview"))
            {
                bool isAcc = lowerName.Contains("acc") ||
                             lowerName.Contains("prop") ||
                             lowerName.Contains("weapon") ||
                             lowerName.Contains("gun") ||
                             lowerName.Contains("hat") ||
                             lowerName.Contains("gear") ||
                             lowerName.Contains("glass") ||
                             lowerName.Contains("mask") ||
                             lowerName.Contains("cape") ||
                             lowerName.Contains("coat");

                string displayName = CleanMeshName(rawName);

                _submeshes.Add(new SubmeshItem
                {
                    Mesh = mi,
                    RawName = rawName,
                    DisplayName = displayName,
                    IsAccessory = isAcc
                });
            }
        }

        foreach (Node child in node.GetChildren())
        {
            CollectSubmeshesRecursive(child);
        }
    }

    private string CleanMeshName(string raw)
    {
        string clean = raw.TrimStart('.', '_');
        if (string.IsNullOrEmpty(clean)) return "Mesh Part";
        return char.ToUpperInvariant(clean[0]) + clean.Substring(1).Replace('_', ' ');
    }

    private void PopulateSubmeshList()
    {
        if (_submeshContainer == null) return;

        foreach (var item in _submeshes)
        {
            bool isInitiallyVisible = item.Mesh.Visible;
            if (item.Mesh.HasMeta("UserVisibility"))
            {
                isInitiallyVisible = item.Mesh.GetMeta("UserVisibility").AsBool();
            }
            else if (item.Mesh.HasMeta("GeneratedSubmeshes"))
            {
                var gen = item.Mesh.GetMeta("GeneratedSubmeshes").AsGodotArray<MeshInstance3D>();
                if (gen.Count > 0 && gen[0] != null && GodotObject.IsInstanceValid(gen[0]))
                {
                    isInitiallyVisible = gen[0].Visible;
                }
            }

            item.Mesh.SetMeta("UserVisibility", isInitiallyVisible);

            var cb = new CheckBox
            {
                Text = item.DisplayName,
                ButtonPressed = isInitiallyVisible,
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };

            var localItem = item;
            cb.Toggled += (pressed) =>
            {
                if (localItem.Mesh != null && GodotObject.IsInstanceValid(localItem.Mesh))
                {
                    if (!localItem.Mesh.HasMeta("IsHiddenComposite"))
                    {
                        localItem.Mesh.Visible = pressed;
                    }
                    else
                    {
                        localItem.Mesh.Visible = false;
                    }
                    localItem.Mesh.SetMeta("UserVisibility", pressed);

                    if (localItem.Mesh.HasMeta("GeneratedSubmeshes"))
                    {
                        var genList = localItem.Mesh.GetMeta("GeneratedSubmeshes").AsGodotArray<MeshInstance3D>();
                        foreach (var sub in genList)
                        {
                            if (sub != null && GodotObject.IsInstanceValid(sub))
                            {
                                sub.Visible = pressed;
                            }
                        }
                    }
                }
            };

            item.CheckBoxWidget = cb;
            _submeshContainer.AddChild(cb);
        }
    }

    public void ShowAll()
    {
        foreach (var item in _submeshes)
        {
            if (item.Mesh != null && GodotObject.IsInstanceValid(item.Mesh))
            {
                if (!item.Mesh.HasMeta("IsHiddenComposite"))
                {
                    item.Mesh.Visible = true;
                }
                else
                {
                    item.Mesh.Visible = false;
                }
                item.Mesh.SetMeta("UserVisibility", true);

                if (item.Mesh.HasMeta("GeneratedSubmeshes"))
                {
                    var genList = item.Mesh.GetMeta("GeneratedSubmeshes").AsGodotArray<MeshInstance3D>();
                    foreach (var sub in genList)
                    {
                        if (sub != null && GodotObject.IsInstanceValid(sub))
                        {
                            sub.Visible = true;
                        }
                    }
                }

                if (item.CheckBoxWidget != null) item.CheckBoxWidget.ButtonPressed = true;
            }
        }
    }

    public void HideAccessories()
    {
        foreach (var item in _submeshes)
        {
            if (item.Mesh != null && GodotObject.IsInstanceValid(item.Mesh))
            {
                bool shouldBeVisible = !item.IsAccessory;
                if (!item.Mesh.HasMeta("IsHiddenComposite"))
                {
                    item.Mesh.Visible = shouldBeVisible;
                }
                else
                {
                    item.Mesh.Visible = false;
                }
                item.Mesh.SetMeta("UserVisibility", shouldBeVisible);

                if (item.Mesh.HasMeta("GeneratedSubmeshes"))
                {
                    var genList = item.Mesh.GetMeta("GeneratedSubmeshes").AsGodotArray<MeshInstance3D>();
                    foreach (var sub in genList)
                    {
                        if (sub != null && GodotObject.IsInstanceValid(sub))
                        {
                            sub.Visible = shouldBeVisible;
                        }
                    }
                }

                if (item.CheckBoxWidget != null) item.CheckBoxWidget.ButtonPressed = shouldBeVisible;
            }
        }
    }
}
