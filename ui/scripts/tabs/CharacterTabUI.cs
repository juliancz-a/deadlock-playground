using Godot;
using System;
using System.Collections.Generic;
using DeadlockPlayground.Catalog;

public partial class CharacterTabUI : VBoxContainer
{
    [Signal] public delegate void CharacterRequestedEventHandler(string internalId);

    [Export] private OptionButton _heroOptionButton;
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
    private readonly List<DeadlockHeroEntry> _selectableEntries = new();

    public override void _Ready()
    {
        PopulateHeroDropdown();
        ConnectEvents();

        if (_vpkLoader == null)
        {
            _vpkLoader = GetNodeOrNull<VpkLoaderTest>("/root/Main/UIRoot/MainHUD/VBoxContainer/MainSplit/ViewportArea/SubViewportContainer/WorldViewport/VpkLoaderTest")
                      ?? GetNodeOrNull<VpkLoaderTest>("/root/Main/VpkLoaderTest")
                      ?? GetTree().Root.FindChild("VpkLoaderTest", true, false) as VpkLoaderTest;
        }

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
        _selectableEntries.Clear();

        _heroOptionButton.AddItem("Select a Hero...", -1);
        _heroOptionButton.SetItemDisabled(0, true);

        // Header: -- Heroes --
        _heroOptionButton.AddItem("-- Heroes --", -1);
        int heroHeaderIndex = _heroOptionButton.ItemCount - 1;
        _heroOptionButton.SetItemDisabled(heroHeaderIndex, true);

        foreach (var hero in DeadlockHeroCatalog.UpdatedHeroes)
        {
            int currentId = _selectableEntries.Count;
            _selectableEntries.Add(hero);
            _heroOptionButton.AddItem($"  {hero.DisplayName}", currentId);
        }

        // Header: -- Legacy / Prototype Heroes --
        _heroOptionButton.AddItem("-- Legacy / Prototype Heroes --", -1);
        int legacyHeaderIndex = _heroOptionButton.ItemCount - 1;
        _heroOptionButton.SetItemDisabled(legacyHeaderIndex, true);

        foreach (var hero in DeadlockHeroCatalog.LegacyHeroes)
        {
            int currentId = _selectableEntries.Count;
            _selectableEntries.Add(hero);
            _heroOptionButton.AddItem($"  {hero.DisplayName}", currentId);
        }

        _heroOptionButton.Select(0);
    }

    private void ConnectEvents()
    {
        if (_heroOptionButton != null)
        {
            _heroOptionButton.ItemSelected += OnHeroSelected;
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

    private void OnHeroSelected(long index)
    {
        if (_heroOptionButton == null) return;
        int id = _heroOptionButton.GetItemId((int)index);
        if (id < 0 || id >= _selectableEntries.Count) return;

        var entry = _selectableEntries[id];
        GD.Print($"[CharacterTab] Loading hero: {entry.DisplayName} ({entry.InternalCodename}) from {entry.VmdlRelativePath}");
        EmitSignal(SignalName.CharacterRequested, entry.InternalCodename);

        if (_vpkLoader != null)
        {
            _ = _vpkLoader.LoadHeroModelAsync(entry);
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

            // Skip bone markers and gizmos
            if (!lowerName.Contains("marker") && !lowerName.Contains("gizmo"))
            {
                bool isAcc = lowerName.Contains("acc") ||
                             lowerName.Contains("prop") ||
                             lowerName.Contains("weapon") ||
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
            var cb = new CheckBox
            {
                Text = item.DisplayName,
                ButtonPressed = item.Mesh.Visible,
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };

            var localItem = item;
            cb.Toggled += (pressed) =>
            {
                if (localItem.Mesh != null && GodotObject.IsInstanceValid(localItem.Mesh))
                {
                    localItem.Mesh.Visible = pressed;
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
                item.Mesh.Visible = true;
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
                item.Mesh.Visible = shouldBeVisible;
                if (item.CheckBoxWidget != null) item.CheckBoxWidget.ButtonPressed = shouldBeVisible;
            }
        }
    }
}
