using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using DeadlockPlayground.Addons;

namespace DeadlockPlayground.UI;

/// <summary>
/// Modal dialog displaying installed user mods (.vpk) in a rich visual gallery.
/// Allows selecting full character replacement models, texture mods, or resetting to vanilla.
/// </summary>
public partial class ModPickerModalUI : PanelContainer
{
    [Signal] public delegate void ModSelectedEventHandler(string modFilePath, string modDisplayTitle, bool isVanilla);
    [Signal] public delegate void ModalClosedEventHandler();

    private LineEdit _searchBar;
    private Button _btnClose;
    private Button _btnSelect;
    private Button _btnCancel;
    private Label _lblSelectedInfo;
    private HFlowContainer _flowMods;

    private readonly List<ModCardUI> _allCards = new();
    private ModCardUI _selectedCard = null;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(880, 600);
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

        var titleLabel = new Label
        {
            Text = "MODDED CHARACTER MODELS",
            ThemeTypeVariation = "TitleLabel",
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin
        };
        titleLabel.AddThemeFontSizeOverride("font_size", 16);
        titleLabel.AddThemeColorOverride("font_color", new Color(0.40f, 0.75f, 1.0f, 1.0f));
        header.AddChild(titleLabel);

        _searchBar = new LineEdit
        {
            PlaceholderText = "Search modded models...",
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

        _flowMods = new HFlowContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        _flowMods.AddThemeConstantOverride("h_separation", 12);
        _flowMods.AddThemeConstantOverride("v_separation", 12);
        scroll.AddChild(_flowMods);

        rootVbox.AddChild(new HSeparator());

        // Footer
        var footer = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.Fill };
        footer.AddThemeConstantOverride("separation", 12);
        rootVbox.AddChild(footer);

        _lblSelectedInfo = new Label
        {
            Text = "Select a character mod to load...",
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
            Text = "Load Mod",
            CustomMinimumSize = new Vector2(130, 32),
            Disabled = true
        };
        _btnSelect.AddThemeColorOverride("font_color", new Color(0.40f, 0.75f, 1.0f, 1.0f));
        footer.AddChild(_btnSelect);
    }

    private void ConnectEvents()
    {
        _btnClose.Pressed += CloseModal;
        _btnCancel.Pressed += CloseModal;
        _btnSelect.Pressed += ConfirmSelection;
        _searchBar.TextChanged += OnSearchFilterChanged;
    }

    public void Open(IReadOnlyList<AddonModInfo> mods, string activeModPath = null)
    {
        Visible = true;
        CenterModal();
        PopulateMods(mods, activeModPath);
    }

    public void PopulateMods(IReadOnlyList<AddonModInfo> mods, string activeModPath = null)
    {
        _allCards.Clear();
        foreach (Node child in _flowMods.GetChildren())
        {
            child.QueueFree();
        }

        // 1. Vanilla Entry
        var vanillaCard = new ModCardUI();
        vanillaCard.Setup(null, isVanilla: true);
        vanillaCard.CardSelected += OnCardSelected;
        vanillaCard.CardDoubleClicked += OnCardDoubleClicked;
        _flowMods.AddChild(vanillaCard);
        _allCards.Add(vanillaCard);

        if (string.IsNullOrEmpty(activeModPath))
        {
            SelectCard(vanillaCard);
        }

        // 2. Mod Cards
        if (mods != null)
        {
            foreach (var mod in mods)
            {
                var card = new ModCardUI();
                card.Setup(mod, isVanilla: false);
                card.CardSelected += OnCardSelected;
                card.CardDoubleClicked += OnCardDoubleClicked;
                _flowMods.AddChild(card);
                _allCards.Add(card);

                if (!string.IsNullOrEmpty(activeModPath) && mod.FilePath.Equals(activeModPath, StringComparison.OrdinalIgnoreCase))
                {
                    SelectCard(card);
                }
            }
        }
    }

    private void OnCardSelected(ModCardUI card)
    {
        SelectCard(card);
    }

    private void OnCardDoubleClicked(ModCardUI card)
    {
        SelectCard(card);
        ConfirmSelection();
    }

    private void SelectCard(ModCardUI card)
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
            if (_selectedCard.IsVanilla)
            {
                _lblSelectedInfo.Text = "Selected: Vanilla Character Model (pak01_dir.vpk)";
            }
            else if (_selectedCard.ModInfo != null)
            {
                _lblSelectedInfo.Text = $"Selected: {_selectedCard.ModInfo.DisplayTitle} ({_selectedCard.ModInfo.FileName})";
            }
        }
        else
        {
            _btnSelect.Disabled = true;
            _lblSelectedInfo.Text = "Select a character mod to load...";
        }
    }

    private void OnSearchFilterChanged(string query)
    {
        string q = query?.Trim().ToLowerInvariant() ?? "";
        foreach (var card in _allCards)
        {
            if (card == null) continue;
            if (card.IsVanilla)
            {
                card.Visible = string.IsNullOrEmpty(q) || "vanilla".Contains(q) || "official".Contains(q);
                continue;
            }

            var mod = card.ModInfo;
            if (mod == null) continue;

            bool matches = string.IsNullOrEmpty(q) ||
                           mod.DisplayTitle.ToLowerInvariant().Contains(q) ||
                           mod.FileName.ToLowerInvariant().Contains(q) ||
                           (!string.IsNullOrEmpty(mod.DetectedHeroDisplayName) && mod.DetectedHeroDisplayName.ToLowerInvariant().Contains(q)) ||
                           (!string.IsNullOrEmpty(mod.DetectedHeroCodename) && mod.DetectedHeroCodename.ToLowerInvariant().Contains(q));

            card.Visible = matches;
        }
    }

    private void ConfirmSelection()
    {
        if (_selectedCard != null)
        {
            if (_selectedCard.IsVanilla)
            {
                EmitSignal(SignalName.ModSelected, "", "Vanilla", true);
            }
            else if (_selectedCard.ModInfo != null)
            {
                EmitSignal(SignalName.ModSelected, _selectedCard.ModInfo.FilePath, _selectedCard.ModInfo.DisplayTitle, false);
            }
            CloseModal();
        }
    }

    public void CloseModal()
    {
        Visible = false;
        EmitSignal(SignalName.ModalClosed);
    }
}
