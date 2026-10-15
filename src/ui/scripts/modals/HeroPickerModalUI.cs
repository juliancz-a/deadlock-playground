using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using DeadlockPlayground.Catalog;
using ValvePak;

namespace DeadlockPlayground.UI;

/// <summary>
/// Rich visual gallery modal for selecting hero models, categorized into 3 distinct sections:
/// New Heroes (City Never Sleeps Update), Character Models (In-Game / Standard), and Legacy Heroes.
/// Adheres strictly to PlaygroundTheme.tres.
/// </summary>
public partial class HeroPickerModalUI : PanelContainer
{
    [Signal] public delegate void HeroSelectedEventHandler(string heroCodename, string vmdlPath, string displayName);
    [Signal] public delegate void ModalClosedEventHandler();

    private LineEdit _searchBar;
    private Button _btnClose;
    private Button _btnSelect;
    private Button _btnCancel;
    private Label _lblSelectedInfo;

    private HFlowContainer _flowNewUpdate;
    private HFlowContainer _flowStandard;
    private HFlowContainer _flowLegacy;

    private VBoxContainer _sectionNewUpdate;
    private VBoxContainer _sectionStandard;
    private VBoxContainer _sectionLegacy;

    private readonly List<HeroCardUI> _allCards = new();
    private HeroCardUI _selectedCard = null;
    private Package _currentPackage = null;
    private bool _hasInitializedCards = false;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(1040, 700);
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
        float halfW = CustomMinimumSize.X > 0 ? CustomMinimumSize.X / 2f : 520f;
        float halfH = CustomMinimumSize.Y > 0 ? CustomMinimumSize.Y / 2f : 350f;
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

        // --- 1. Header Bar ---
        var header = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.Fill
        };
        header.AddThemeConstantOverride("separation", 14);
        rootVbox.AddChild(header);

        var titleLabel = new Label
        {
            Text = "SELECT A CHARACTER MODEL",
            ThemeTypeVariation = "TitleLabel",
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin
        };
        titleLabel.AddThemeFontSizeOverride("font_size", 16);
        titleLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.85f, 0.40f, 1.0f));
        header.AddChild(titleLabel);

        _searchBar = new LineEdit
        {
            PlaceholderText = "Search heroes by name or codename...",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(300, 32),
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

        // --- 2. Scrollable Body with 3 Sections ---
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        rootVbox.AddChild(scroll);

        var contentVbox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        contentVbox.AddThemeConstantOverride("separation", 20);
        scroll.AddChild(contentVbox);

        // Section 1: New Heroes (City Never Sleeps Update)
        _sectionNewUpdate = CreateSection(contentVbox, "★ NEW HEROES (CITY NEVER SLEEPS UPDATE)", new Color(0.95f, 0.85f, 0.25f, 1.0f), out _flowNewUpdate);

        // Section 2: Character Models (In-Game / Standard)
        _sectionStandard = CreateSection(contentVbox, "CHARACTER MODELS (IN-GAME / STANDARD)", new Color(0.40f, 0.75f, 1.0f, 1.0f), out _flowStandard);

        // Section 3: Legacy Heroes
        _sectionLegacy = CreateSection(contentVbox, "LEGACY & PROTOTYPE HEROES", new Color(0.70f, 0.50f, 0.85f, 1.0f), out _flowLegacy);

        rootVbox.AddChild(new HSeparator());

        // --- 3. Footer Bar ---
        var footer = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.Fill
        };
        footer.AddThemeConstantOverride("separation", 12);
        rootVbox.AddChild(footer);

        _lblSelectedInfo = new Label
        {
            Text = "Select a character model to load...",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MaxLinesVisible = 2
        };
        _lblSelectedInfo.AddThemeFontSizeOverride("font_size", 13);
        _lblSelectedInfo.AddThemeColorOverride("font_color", new Color(0.82f, 0.85f, 0.90f, 1.0f));
        footer.AddChild(_lblSelectedInfo);

        _btnCancel = new Button
        {
            Text = "Cancel",
            CustomMinimumSize = new Vector2(90, 32)
        };
        footer.AddChild(_btnCancel);

        _btnSelect = new Button
        {
            Text = "Select Character",
            CustomMinimumSize = new Vector2(140, 32),
            Disabled = true
        };
        _btnSelect.AddThemeColorOverride("font_color", new Color(0.95f, 0.85f, 0.30f, 1.0f));
        footer.AddChild(_btnSelect);
    }

    private static VBoxContainer CreateSection(VBoxContainer parent, string titleText, Color headerColor, out HFlowContainer flowContainer)
    {
        var section = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        section.AddThemeConstantOverride("separation", 10);
        parent.AddChild(section);

        var lbl = new Label
        {
            Text = titleText
        };
        lbl.AddThemeFontSizeOverride("font_size", 16);
        lbl.AddThemeColorOverride("font_color", headerColor);
        section.AddChild(lbl);

        flowContainer = new HFlowContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        flowContainer.AddThemeConstantOverride("h_separation", 12);
        flowContainer.AddThemeConstantOverride("v_separation", 12);
        section.AddChild(flowContainer);

        return section;
    }

    private void ConnectEvents()
    {
        _btnClose.Pressed += CloseModal;
        _btnCancel.Pressed += CloseModal;
        _btnSelect.Pressed += ConfirmSelection;
        _searchBar.TextChanged += OnSearchFilterChanged;
    }

    public void Open(Package package, string currentHeroCodename = null)
    {
        _currentPackage = package;
        Visible = true;
        CenterModal();

        if (!_hasInitializedCards)
        {
            PopulateAllCards();
            _hasInitializedCards = true;
        }

        // Highlight currently loaded hero if matching
        if (!string.IsNullOrEmpty(currentHeroCodename))
        {
            var match = _allCards.FirstOrDefault(c => c.Hero != null &&
                (c.Hero.InternalCodename.Equals(currentHeroCodename, StringComparison.OrdinalIgnoreCase) ||
                 c.Hero.DisplayName.Equals(currentHeroCodename, StringComparison.OrdinalIgnoreCase)));
            if (match != null)
            {
                SelectCard(match);
            }
        }
    }

    private void PopulateAllCards()
    {
        _allCards.Clear();

        // 1. New Heroes
        foreach (var hero in DeadlockHeroCatalog.NewUpdateHeroes)
        {
            var card = CreateCard(hero);
            _flowNewUpdate.AddChild(card);
            _allCards.Add(card);
        }

        // 2. Standard Heroes
        foreach (var hero in DeadlockHeroCatalog.UpdatedHeroes)
        {
            var card = CreateCard(hero);
            _flowStandard.AddChild(card);
            _allCards.Add(card);
        }

        // 3. Legacy Heroes
        foreach (var hero in DeadlockHeroCatalog.LegacyHeroes)
        {
            var card = CreateCard(hero);
            _flowLegacy.AddChild(card);
            _allCards.Add(card);
        }

        // Asynchronously load thumbnails for all cards
        if (_currentPackage != null)
        {
            foreach (var card in _allCards)
            {
                _ = card.LoadThumbnailAsync(_currentPackage);
            }
        }
    }

    private HeroCardUI CreateCard(DeadlockHeroEntry hero)
    {
        var card = new HeroCardUI();
        card.Setup(hero);
        card.CardSelected += OnCardSelected;
        card.CardDoubleClicked += OnCardDoubleClicked;
        return card;
    }

    private void OnCardSelected(HeroCardUI card)
    {
        SelectCard(card);
    }

    private void OnCardDoubleClicked(HeroCardUI card)
    {
        SelectCard(card);
        ConfirmSelection();
    }

    private void SelectCard(HeroCardUI card)
    {
        if (_selectedCard != null && GodotObject.IsInstanceValid(_selectedCard))
        {
            _selectedCard.IsSelected = false;
        }

        _selectedCard = card;
        if (_selectedCard != null)
        {
            _selectedCard.IsSelected = true;
            _btnSelect.Disabled = false;
            _lblSelectedInfo.Text = $"Selected: {_selectedCard.Hero.DisplayName} ({_selectedCard.Hero.InternalCodename})\nModel: {_selectedCard.Hero.VmdlRelativePath}";
        }
        else
        {
            _btnSelect.Disabled = true;
            _lblSelectedInfo.Text = "Select a character model to load...";
        }
    }

    private void OnSearchFilterChanged(string query)
    {
        string q = query?.Trim().ToLowerInvariant() ?? "";

        int visNew = 0, visStd = 0, visLeg = 0;

        foreach (var card in _allCards)
        {
            if (card == null || card.Hero == null) continue;

            bool matches = string.IsNullOrEmpty(q) ||
                           card.Hero.DisplayName.ToLowerInvariant().Contains(q) ||
                           card.Hero.InternalCodename.ToLowerInvariant().Contains(q) ||
                           (!string.IsNullOrEmpty(card.Hero.WipAlias) && card.Hero.WipAlias.ToLowerInvariant().Contains(q));

            card.Visible = matches;

            if (matches)
            {
                if (card.Hero.Category == HeroCategory.NewUpdate) visNew++;
                else if (card.Hero.Category == HeroCategory.UpdatedAndNew) visStd++;
                else if (card.Hero.Category == HeroCategory.LegacyStaging) visLeg++;
            }
        }

        _sectionNewUpdate.Visible = (visNew > 0);
        _sectionStandard.Visible = (visStd > 0);
        _sectionLegacy.Visible = (visLeg > 0);
    }

    private void ConfirmSelection()
    {
        if (_selectedCard != null && _selectedCard.Hero != null)
        {
            var hero = _selectedCard.Hero;
            EmitSignal(SignalName.HeroSelected, hero.InternalCodename, hero.VmdlRelativePath, hero.DisplayName);
            CloseModal();
        }
    }

    public void CloseModal()
    {
        Visible = false;
        EmitSignal(SignalName.ModalClosed);
    }
}
