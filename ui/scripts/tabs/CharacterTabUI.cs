using Godot;
using System;
using System.Collections.Generic;

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

    private readonly List<(string DisplayName, string InternalId)> _heroes = new()
    {
        ("Abrams", "abrams"),
        ("Bebop", "bebop"),
        ("Dynamo", "dynamo"),
        ("Grey Talon", "archer"),
        ("Haze", "haze"),
        ("Infernus", "chronos"),
        ("Ivy", "tengu"),
        ("Kelvin", "kelvin"),
        ("Lady Geist", "ghost"),
        ("Lash", "lash_v2"),
        ("McGinnis", "engineer"),
        ("Mirage", "mirage"),
        ("Mo & Krill", "digger"),
        ("Paradox", "chrono"),
        ("Pocket", "pocket"),
        ("Seven", "wrecker"),
        ("Shiv", "shiv"),
        ("Vindicta", "hornet"),
        ("Viscous", "viscous"),
        ("Warden", "warden"),
        ("Wraith", "wraith"),
        ("Yamato", "yamato")
    };

    public override void _Ready()
    {
        PopulateHeroDropdown();
        ConnectEvents();

        if (_vpkLoader == null)
        {
            _vpkLoader = GetNodeOrNull<VpkLoaderTest>("/root/Main/VpkLoaderTest");
        }

        if (_vpkLoader != null)
        {
            _vpkLoader.HeroLoaded += SetHero;
            _vpkLoader.HeroUnloaded += ClearSubmeshes;
            if (_vpkLoader.CurrentHeroNode != null)
            {
                SetHero(_vpkLoader.CurrentHeroNode);
            }
        }
    }

    private void PopulateHeroDropdown()
    {
        if (_heroOptionButton == null) return;

        _heroOptionButton.Clear();
        _heroOptionButton.AddItem("Select a Hero...", -1);
        _heroOptionButton.SetItemDisabled(0, true);

        for (int i = 0; i < _heroes.Count; i++)
        {
            _heroOptionButton.AddItem(_heroes[i].DisplayName, i);
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
        if (id < 0 || id >= _heroes.Count) return;

        string internalId = _heroes[id].InternalId;
        GD.Print($"[CharacterTab] Loading hero: {_heroes[id].DisplayName} ({internalId})");
        EmitSignal(SignalName.CharacterRequested, internalId);

        if (_vpkLoader != null)
        {
            _ = _vpkLoader.LoadHeroAsync(internalId, internalId);
        }
    }

    public void SetHero(Node3D heroNode)
    {
        ClearSubmeshes();
        if (heroNode == null) return;

        CollectSubmeshesRecursive(heroNode);
        PopulateSubmeshList();
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
