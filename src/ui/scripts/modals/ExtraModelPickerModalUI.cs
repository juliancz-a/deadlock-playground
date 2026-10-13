using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeadlockPlayground.Catalog;
using SteamDatabase.ValvePak;

namespace DeadlockPlayground.UI;

/// <summary>
/// Modal dialog for browsing and selecting ability-specific and supplementary 3D hero models (.vmdl_c)
/// scoped dynamically to the active hero from pak01_dir.vpk.
/// </summary>
public partial class ExtraModelPickerModalUI : PanelContainer
{
    [Signal] public delegate void ExtraModelSelectedEventHandler(string vmdlPath, string displayName);
    [Signal] public delegate void ModalClosedEventHandler();

    private Label _lblTitle;
    private LineEdit _searchBar;
    private Button _btnClose;
    private Button _btnSelect;
    private Button _btnCancel;
    private Label _lblSelectedInfo;
    private HFlowContainer _flowCards;

    private readonly List<ExtraModelCardUI> _spawnedCards = new();
    private ExtraModelCardUI _selectedCard = null;
    private DeadlockHeroEntry _currentHero = null;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(900, 600);
        Theme = GD.Load<Theme>("res://assets/themes/PlaygroundTheme.tres");
        BuildModalLayout();
        CenterModal();
        ConnectEvents();
    }

    public void CenterModal()
    {
        SetAnchorsPreset(LayoutPreset.Center);
        AnchorLeft = 0.5f;
        AnchorTop = 0.5f;
        AnchorRight = 0.5f;
        AnchorBottom = 0.5f;
        GrowHorizontal = GrowDirection.Both;
        GrowVertical = GrowDirection.Both;
        float halfW = CustomMinimumSize.X > 0 ? CustomMinimumSize.X / 2f : 450f;
        float halfH = CustomMinimumSize.Y > 0 ? CustomMinimumSize.Y / 2f : 300f;
        OffsetLeft = -halfW;
        OffsetTop = -halfH;
        OffsetRight = halfW;
        OffsetBottom = halfH;
    }

    private void BuildModalLayout()
    {
        var mainStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.11f, 0.14f, 0.98f),
            CornerRadiusTopLeft = 10,
            CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10,
            CornerRadiusBottomRight = 10,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.26f, 0.32f, 0.42f, 0.8f),
            ContentMarginLeft = 20,
            ContentMarginRight = 20,
            ContentMarginTop = 16,
            ContentMarginBottom = 16
        };
        AddThemeStyleboxOverride("panel", mainStyle);

        var rootVbox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.Fill,
            SizeFlagsVertical = SizeFlags.Fill
        };
        rootVbox.AddThemeConstantOverride("separation", 12);
        AddChild(rootVbox);

        // Header
        var header = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.Fill };
        header.AddThemeConstantOverride("separation", 14);
        rootVbox.AddChild(header);

        _lblTitle = new Label
        {
            Text = "HERO ABILITY & EXTRA MODELS",
            ThemeTypeVariation = "TitleLabel",
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin
        };
        _lblTitle.AddThemeFontSizeOverride("font_size", 16);
        _lblTitle.AddThemeColorOverride("font_color", new Color(0.95f, 0.85f, 0.35f, 1.0f));
        header.AddChild(_lblTitle);

        _searchBar = new LineEdit
        {
            PlaceholderText = "Search models...",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(260, 32),
            ClearButtonEnabled = true
        };
        header.AddChild(_searchBar);

        _btnClose = new Button
        {
            Text = "✕",
            CustomMinimumSize = new Vector2(32, 32)
        };
        header.AddChild(_btnClose);

        rootVbox.AddChild(new HSeparator());

        // Body
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        rootVbox.AddChild(scroll);

        _flowCards = new HFlowContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        _flowCards.AddThemeConstantOverride("h_separation", 12);
        _flowCards.AddThemeConstantOverride("v_separation", 12);
        scroll.AddChild(_flowCards);

        rootVbox.AddChild(new HSeparator());

        // Footer
        var footer = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.Fill };
        footer.AddThemeConstantOverride("separation", 12);
        rootVbox.AddChild(footer);

        _lblSelectedInfo = new Label
        {
            Text = "Select an extra model to load...",
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _lblSelectedInfo.AddThemeFontSizeOverride("font_size", 12);
        _lblSelectedInfo.AddThemeColorOverride("font_color", new Color(0.75f, 0.78f, 0.84f, 1.0f));
        footer.AddChild(_lblSelectedInfo);

        _btnCancel = new Button
        {
            Text = "Cancel",
            CustomMinimumSize = new Vector2(90, 32)
        };
        footer.AddChild(_btnCancel);

        _btnSelect = new Button
        {
            Text = "Load Model",
            CustomMinimumSize = new Vector2(130, 32),
            Disabled = true
        };
        _btnSelect.AddThemeColorOverride("font_color", new Color(0.95f, 0.85f, 0.35f, 1.0f));
        footer.AddChild(_btnSelect);
    }

    private void ConnectEvents()
    {
        _btnClose.Pressed += CloseModal;
        _btnCancel.Pressed += CloseModal;
        _btnSelect.Pressed += ConfirmSelection;
        _searchBar.TextChanged += OnSearchFilterChanged;
    }

    public void OpenForHero(Package package, DeadlockHeroEntry hero, string currentLoadedVmdl = null)
    {
        _currentHero = hero;
        Visible = true;
        CenterModal();

        string heroName = hero?.DisplayName ?? "Hero";
        if (_lblTitle != null)
        {
            _lblTitle.Text = $"{heroName.ToUpperInvariant()} — ABILITY & EXTRA MODELS";
        }

        PopulateModels(package, hero, currentLoadedVmdl);
    }

    private void PopulateModels(Package package, DeadlockHeroEntry hero, string currentLoadedVmdl)
    {
        _spawnedCards.Clear();
        foreach (Node child in _flowCards.GetChildren())
        {
            child.QueueFree();
        }

        if (hero == null) return;

        var entries = DiscoverExtraModels(package, hero);

        foreach (var entry in entries)
        {
            var card = new ExtraModelCardUI();
            card.Setup(entry);
            card.CardSelected += OnCardSelected;
            card.CardDoubleClicked += OnCardDoubleClicked;
            _flowCards.AddChild(card);
            _spawnedCards.Add(card);

            if (!string.IsNullOrEmpty(currentLoadedVmdl) && entry.VmdlPath.Equals(currentLoadedVmdl, StringComparison.OrdinalIgnoreCase))
            {
                SelectCard(card);
            }
            else if (string.IsNullOrEmpty(currentLoadedVmdl) && entry.IsMainHeroModel)
            {
                SelectCard(card);
            }
        }
    }

    private static List<ExtraModelEntry> DiscoverExtraModels(Package package, DeadlockHeroEntry hero)
    {
        var result = new List<ExtraModelEntry>();
        if (hero == null) return result;

        // 1. Always guarantee Main Model as candidate #1
        string mainVmdl = hero.VmdlRelativePath.Replace('\\', '/');
        result.Add(new ExtraModelEntry
        {
            DisplayTitle = $"{hero.DisplayName} (Base Character)",
            VmdlPath = mainVmdl,
            FileName = Path.GetFileNameWithoutExtension(mainVmdl),
            IsMainHeroModel = true
        });

        if (package == null) return result;

        string heroDir = Path.GetDirectoryName(hero.VmdlRelativePath)?.Replace('\\', '/').Trim('/') ?? "";
        string codename = hero.InternalCodename.ToLowerInvariant().Trim();
        string alias = hero.WipAlias?.ToLowerInvariant().Trim();

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mainVmdl };

        foreach (var ext in new[] { "vmdl_c", "vmdl" })
        {
            if (!package.Entries.TryGetValue(ext, out var modelEntries)) continue;

            foreach (var e in modelEntries)
            {
                string fn = e.FileName.ToLowerInvariant();
                string dn = e.DirectoryName.ToLowerInvariant();
                string fullPath = $"{e.DirectoryName}/{e.FileName}.{ext}".Replace('\\', '/');

                // Filter out non-renderable auxiliary files
                if (fn.Contains("_physics") || fn.Contains("_ragdoll") || fn.Contains("_hitbox") ||
                    fn.Contains("_anim") || fn.Contains("_anims") || fn.Contains("_ik") ||
                    fn.Contains("_skeleton") || fn.Contains("_test") || fn.Contains("_lod"))
                {
                    continue;
                }

                // Check directory match or filename match
                bool matchesHero = false;
                if (!string.IsNullOrEmpty(heroDir) && (dn.Equals(heroDir.ToLowerInvariant()) || dn.StartsWith(heroDir.ToLowerInvariant() + "/")))
                {
                    matchesHero = true;
                }
                else if (dn.Contains($"/{codename}") || (!string.IsNullOrEmpty(alias) && dn.Contains($"/{alias}")))
                {
                    matchesHero = true;
                }
                else if (fn.StartsWith($"{codename}_") || (!string.IsNullOrEmpty(alias) && fn.StartsWith($"{alias}_")))
                {
                    matchesHero = true;
                }

                if (matchesHero && seenPaths.Add(fullPath))
                {
                    string cleanTitle = CleanModelTitle(e.FileName, codename, alias, hero.DisplayName);
                    result.Add(new ExtraModelEntry
                    {
                        DisplayTitle = cleanTitle,
                        VmdlPath = fullPath,
                        FileName = e.FileName,
                        IsMainHeroModel = false
                    });
                }
            }
        }

        return result;
    }

    private static string CleanModelTitle(string fileName, string codename, string alias, string heroDisplayName)
    {
        string raw = fileName;
        if (!string.IsNullOrEmpty(codename) && raw.StartsWith(codename + "_", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw.Substring(codename.Length + 1);
        }
        else if (!string.IsNullOrEmpty(alias) && raw.StartsWith(alias + "_", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw.Substring(alias.Length + 1);
        }

        string formatted = raw.Replace('_', ' ');
        formatted = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(formatted);

        // Friendly name replacements
        if (formatted.Equals("Dragon", StringComparison.OrdinalIgnoreCase)) return $"{heroDisplayName}'s Dragon";
        if (formatted.Equals("Horse", StringComparison.OrdinalIgnoreCase)) return $"{heroDisplayName}'s Horse";
        if (formatted.Equals("Turret", StringComparison.OrdinalIgnoreCase)) return $"{heroDisplayName}'s Turret";
        if (formatted.Equals("Wall", StringComparison.OrdinalIgnoreCase)) return $"{heroDisplayName}'s Wall";

        return formatted;
    }

    private void OnCardSelected(ExtraModelCardUI card)
    {
        SelectCard(card);
    }

    private void OnCardDoubleClicked(ExtraModelCardUI card)
    {
        SelectCard(card);
        ConfirmSelection();
    }

    private void SelectCard(ExtraModelCardUI card)
    {
        if (_selectedCard != null && GodotObject.IsInstanceValid(_selectedCard))
        {
            _selectedCard.IsSelected = false;
        }

        _selectedCard = card;
        if (_selectedCard != null && _selectedCard.Entry != null)
        {
            _selectedCard.IsSelected = true;
            _btnSelect.Disabled = false;
            _lblSelectedInfo.Text = $"Selected: {_selectedCard.Entry.DisplayTitle}\nPath: {_selectedCard.Entry.VmdlPath}";
        }
        else
        {
            _btnSelect.Disabled = true;
            _lblSelectedInfo.Text = "Select an extra model to load...";
        }
    }

    private void OnSearchFilterChanged(string query)
    {
        string q = query?.Trim().ToLowerInvariant() ?? "";
        foreach (var card in _spawnedCards)
        {
            if (card == null || card.Entry == null) continue;
            bool matches = string.IsNullOrEmpty(q) ||
                           card.Entry.DisplayTitle.ToLowerInvariant().Contains(q) ||
                           card.Entry.VmdlPath.ToLowerInvariant().Contains(q) ||
                           card.Entry.FileName.ToLowerInvariant().Contains(q);
            card.Visible = matches;
        }
    }

    private void ConfirmSelection()
    {
        if (_selectedCard != null && _selectedCard.Entry != null)
        {
            EmitSignal(SignalName.ExtraModelSelected, _selectedCard.Entry.VmdlPath, _selectedCard.Entry.DisplayTitle);
            CloseModal();
        }
    }

    public void CloseModal()
    {
        Visible = false;
        EmitSignal(SignalName.ModalClosed);
    }
}
