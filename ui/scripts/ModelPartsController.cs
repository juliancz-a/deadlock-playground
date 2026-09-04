using Godot;
using System;
using System.Collections.Generic;

public partial class ModelPartsController : MenuButton
{
    [ExportCategory("Top Bar & Navigation")]
    [Export] private BaseButton _topBarButton;
    [Export] private MenuButton _menuButton;

    [ExportCategory("Panel & Container")]
    [Export] private Control _panel;
    [Export] private Button _btnClose;
    [Export] private Button _btnShowAll;
    [Export] private Button _btnHideAccessories;
    [Export] private Container _checkboxContainer;
    [Export] private PackedScene _checkboxPrefab;

    [ExportCategory("Dependencies")]
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
    private const int MenuIdShowAll = 10000;
    private const int MenuIdHideAccessories = 10001;

    public override void _Ready()
    {
        LinkDependencies();
        ConnectEvents();
        PopulatePopupMenu();

        // If a hero is already present in the scene, populate right away
        if (_vpkLoader != null && _vpkLoader.CurrentHeroNode != null)
        {
            RefreshFromCharacter(_vpkLoader.CurrentHeroNode);
        }
    }

    public void TogglePanel()
    {
        if (_panel != null)
        {
            _panel.Visible = !_panel.Visible;
            if (_topBarButton != null && _topBarButton.ToggleMode)
            {
                _topBarButton.ButtonPressed = _panel.Visible;
            }
        }
    }

    public void ShowPanel()
    {
        if (_panel != null)
        {
            _panel.Visible = true;
            if (_topBarButton != null && _topBarButton.ToggleMode)
            {
                _topBarButton.ButtonPressed = true;
            }
        }
    }

    public void HidePanel()
    {
        if (_panel != null)
        {
            _panel.Visible = false;
            if (_topBarButton != null && _topBarButton.ToggleMode)
            {
                _topBarButton.ButtonPressed = false;
            }
        }
    }

    private void LinkDependencies()
    {
        _vpkLoader ??= GetNodeOrNull<VpkLoaderTest>("/root/Main/VpkLoaderTest");

        // Fallbacks
        _menuButton ??= this;
        _panel ??= GetNodeOrNull<Control>("ModelPartsPanel") ?? GetNodeOrNull<Control>("PanelContainer");
        _topBarButton ??= this;
        _btnShowAll ??= _panel?.GetNodeOrNull<Button>("ShowAllButton");
        _btnHideAccessories ??= _panel?.GetNodeOrNull<Button>("HideAccessoriesButton");
        _btnClose ??= _panel?.GetNodeOrNull<Button>("CloseButton");
        _checkboxContainer ??= _panel?.GetNodeOrNull<Container>("ScrollContainer/SubmeshListContainer")
                                ?? _panel?.GetNodeOrNull<Container>("SubmeshListContainer");
    }

    private void ConnectEvents()
    {
        if (_vpkLoader != null)
        {
            _vpkLoader.HeroLoaded += OnHeroLoaded;
            _vpkLoader.HeroUnloaded += OnHeroUnloaded;
        }

        if (_topBarButton != null && _topBarButton is not MenuButton)
        {
            _topBarButton.Pressed += TogglePanel;
        }

        if (_btnClose != null)
        {
            _btnClose.Pressed += HidePanel;
        }

        if (_btnShowAll != null)
        {
            _btnShowAll.Pressed += ShowAll;
        }

        if (_btnHideAccessories != null)
        {
            _btnHideAccessories.Pressed += HideAllAccessories;
        }

        if (_menuButton != null)
        {
            var popup = _menuButton.GetPopup();
            if (popup != null)
            {
                popup.HideOnCheckableItemSelection = false;
                popup.IdPressed += OnPopupIdPressed;
                popup.AboutToPopup += OnPopupAboutToPopup;
            }
        }
    }

    private void OnPopupAboutToPopup()
    {
        if (_submeshes.Count == 0 || _submeshes.Exists(s => !IsInstanceValid(s.Mesh)))
        {
            var hero = _vpkLoader?.CurrentHeroNode 
                    ?? GetTree()?.Root?.FindChild("Hero_*", true, false) as Node3D;
            if (hero != null)
            {
                RefreshFromCharacter(hero);
            }
            else
            {
                PopulatePopupMenu();
            }
        }
        else
        {
            SyncPopupMenuState();
        }
    }

    private void OnHeroLoaded(Node3D heroNode)
    {
        RefreshFromCharacter(heroNode);
    }

    private void OnHeroUnloaded()
    {
        Clear();
    }

    /// <summary>
    /// Scans the hero hierarchy for character MeshInstance3D nodes and populates UI widgets.
    /// </summary>
    public void RefreshFromCharacter(Node3D heroRoot)
    {
        Clear();

        if (heroRoot == null) return;

        var detectedMeshes = new List<MeshInstance3D>();
        FindMeshesRecursive(heroRoot, detectedMeshes);

        foreach (var mesh in detectedMeshes)
        {
            string rawName = mesh.Name.ToString();
            string displayName = FormatDisplayName(rawName);
            bool isAccessory = CheckIfAccessory(rawName);

            var item = new SubmeshItem
            {
                Mesh = mesh,
                RawName = rawName,
                DisplayName = displayName,
                IsAccessory = isAccessory
            };

            _submeshes.Add(item);
        }

        PopulatePopupMenu();
        PopulateCheckboxContainer();
    }

    public void Clear()
    {
        _submeshes.Clear();

        // Clear dynamic container children
        if (_checkboxContainer != null)
        {
            foreach (Node child in _checkboxContainer.GetChildren())
            {
                child.QueueFree();
            }
        }

        // Reset popup menu items
        PopulatePopupMenu();
    }

    private void FindMeshesRecursive(Node node, List<MeshInstance3D> results)
    {
        if (node == null) return;

        if (node is MeshInstance3D mi && mi.Mesh != null)
        {
            string name = mi.Name.ToString().ToLowerInvariant();
            // Skip skeleton gizmos, bone markers, or UI canvas layers
            if (!name.Contains("marker") && !name.Contains("gizmo") && !name.Contains("bone_"))
            {
                results.Add(mi);
            }
        }

        foreach (Node child in node.GetChildren())
        {
            if (child is CanvasLayer || child.Name.ToString().Contains("Gizmo")) continue;
            FindMeshesRecursive(child, results);
        }
    }

    private void PopulatePopupMenu()
    {
        if (_menuButton == null) return;
        var popup = _menuButton.GetPopup();
        if (popup == null) return;

        popup.Clear();

        if (_submeshes.Count == 0)
        {
            popup.AddItem("No character loaded", 9999);
            popup.SetItemDisabled(popup.GetItemIndex(9999), true);
            return;
        }

        // Quick convenience actions at top
        popup.AddItem("Show All Meshes", MenuIdShowAll);
        popup.AddItem("Hide All Accessories", MenuIdHideAccessories);
        popup.AddSeparator();

        // Submesh check items
        for (int i = 0; i < _submeshes.Count; i++)
        {
            var item = _submeshes[i];
            popup.AddCheckItem(item.DisplayName, i);
            int idx = popup.GetItemIndex(i);
            if (idx >= 0 && item.Mesh != null)
            {
                popup.SetItemChecked(idx, item.Mesh.Visible);
            }
        }
    }

    private void PopulateCheckboxContainer()
    {
        if (_checkboxContainer == null) return;

        foreach (var item in _submeshes)
        {
            CheckBox checkBox = null;

            if (_checkboxPrefab != null)
            {
                var instance = _checkboxPrefab.Instantiate();
                checkBox = instance as CheckBox ?? instance.GetNodeOrNull<CheckBox>(".");
                if (checkBox == null)
                {
                    checkBox = new CheckBox();
                    instance.AddChild(checkBox);
                }
                _checkboxContainer.AddChild(instance);
            }
            else
            {
                checkBox = new CheckBox();
                _checkboxContainer.AddChild(checkBox);
            }

            checkBox.Text = item.DisplayName;
            checkBox.ButtonPressed = item.Mesh != null && item.Mesh.Visible;
            item.CheckBoxWidget = checkBox;

            var localItem = item;
            checkBox.Toggled += (isToggled) =>
            {
                if (localItem.Mesh != null)
                {
                    localItem.Mesh.Visible = isToggled;
                }
                SyncPopupMenuState();
            };
        }
    }

    private void OnPopupIdPressed(long id)
    {
        if (id == MenuIdShowAll)
        {
            ShowAll();
        }
        else if (id == MenuIdHideAccessories)
        {
            HideAllAccessories();
        }
        else if (id >= 0 && id < _submeshes.Count)
        {
            ToggleSubmesh((int)id);
        }
    }

    public void ToggleSubmesh(int index)
    {
        if (index < 0 || index >= _submeshes.Count) return;
        var item = _submeshes[index];
        if (item.Mesh == null) return;

        bool newVisible = !item.Mesh.Visible;
        item.Mesh.Visible = newVisible;

        if (item.CheckBoxWidget != null)
        {
            item.CheckBoxWidget.ButtonPressed = newVisible;
        }

        SyncPopupMenuState();
    }

    public void ShowAll()
    {
        foreach (var item in _submeshes)
        {
            if (item.Mesh != null)
            {
                item.Mesh.Visible = true;
            }
            if (item.CheckBoxWidget != null)
            {
                item.CheckBoxWidget.ButtonPressed = true;
            }
        }
        SyncPopupMenuState();
    }

    public void HideAllAccessories()
    {
        foreach (var item in _submeshes)
        {
            if (item.Mesh != null)
            {
                item.Mesh.Visible = !item.IsAccessory;
            }
            if (item.CheckBoxWidget != null)
            {
                item.CheckBoxWidget.ButtonPressed = !item.IsAccessory;
            }
        }
        SyncPopupMenuState();
    }

    private void SyncPopupMenuState()
    {
        if (_menuButton == null) return;
        var popup = _menuButton.GetPopup();
        if (popup == null) return;

        for (int i = 0; i < _submeshes.Count; i++)
        {
            int idx = popup.GetItemIndex(i);
            if (idx >= 0 && _submeshes[i].Mesh != null)
            {
                popup.SetItemChecked(idx, _submeshes[i].Mesh.Visible);
            }
        }
    }

    private static string FormatDisplayName(string rawName)
    {
        string clean = rawName.TrimStart('.', '_').Replace('_', ' ').Replace('-', ' ');
        var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Length > 0)
            {
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i].Substring(1);
            }
        }
        return string.Join(" ", words);
    }

    private static bool CheckIfAccessory(string meshNodeName)
    {
        string lower = meshNodeName.ToLowerInvariant();

        // Exact or prefixed core keywords are base
        string[] coreKeywords = { "body", "head", "face", "skin", "eye", "torso", "base" };
        foreach (var kw in coreKeywords)
        {
            if (lower == kw || lower == $"_{kw}" || lower == $".{kw}" || lower.StartsWith($"{kw}_") || lower.EndsWith($"_{kw}"))
            {
                return false;
            }
        }

        string[] accessoryKeywords = {
            "armor", "coat", "hat", "hair", "weapon", "gun", "holster", 
            "cape", "skirt", "belt", "cloth", "jacket", "acc", "glasses", 
            "mask", "prop", "shoulder", "knee", "scarf", "backpack", 
            "gear", "strap", "vest", "quiver", "arrow", "sword", "pistol",
            "boots", "shoes", "gloves", "hands", "feet", "pads", "chain"
        };

        foreach (var kw in accessoryKeywords)
        {
            if (lower.Contains(kw)) return true;
        }

        foreach (var kw in coreKeywords)
        {
            if (lower.Contains(kw)) return false;
        }

        return true;
    }
}
