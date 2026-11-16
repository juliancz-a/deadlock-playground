using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using DeadlockPlayground.Addons;

namespace DeadlockPlayground.UI;

public enum ModFilterCategory
{
    All,
    Models,
    Textures,
    Animations,
    VfxOther
}

/// <summary>
/// Modal dialog displaying installed user mods (.vpk) in a rich visual gallery.
/// Allows selecting full character replacement models, texture mods, animation mods, VFX mods, or resetting to vanilla.
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
    private HFlowContainer _filterFlow;
    private CheckBox _chkShowNonStandard;
    private HFlowContainer _flowMods;

    private IReadOnlyList<AddonModInfo> _currentMods;

    private readonly Dictionary<ModFilterCategory, Button> _filterButtons = new();
    private ModFilterCategory _activeFilter = ModFilterCategory.All;

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
        ThemeTypeVariation = "ModalPanel";

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
            ThemeTypeVariation = "ModalTitleLabel",
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin
        };
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

        // Filter Pill / Badge Bar
        _filterFlow = new HFlowContainer
        {
            SizeFlagsHorizontal = SizeFlags.Fill
        };
        _filterFlow.AddThemeConstantOverride("h_separation", 8);
        _filterFlow.AddThemeConstantOverride("v_separation", 6);
        rootVbox.AddChild(_filterFlow);

        CreateFilterPill(ModFilterCategory.All, "All", new Color(0.40f, 0.75f, 1.0f, 1.0f));
        CreateFilterPill(ModFilterCategory.Models, "Model Mods", new Color(0.95f, 0.85f, 0.30f, 1.0f));
        CreateFilterPill(ModFilterCategory.Textures, "Texture Mods", new Color(0.60f, 0.85f, 0.50f, 1.0f));
        CreateFilterPill(ModFilterCategory.Animations, "Animation Mods", new Color(0.82f, 0.52f, 0.98f, 1.0f));
        CreateFilterPill(ModFilterCategory.VfxOther, "VFX / Other", new Color(1.0f, 0.62f, 0.25f, 1.0f));
        SetActiveFilter(ModFilterCategory.All, applyFilter: false);

        _chkShowNonStandard = new CheckBox
        {
            Text = "Show Non-Standard VPKs",
            ButtonPressed = true,
            FocusMode = FocusModeEnum.None,
            MouseDefaultCursorShape = CursorShape.PointingHand,
            TooltipText = "Show .vpk mods that do not follow the standard top-level *_dir.vpk structure (e.g., single-file .vpk or subfolder mods)."
        };
        _chkShowNonStandard.Toggled += (_) =>
        {
            UpdateFilterCounts(_currentMods);
            ApplyFilters();
        };
        _filterFlow.AddChild(_chkShowNonStandard);

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
            MaxLinesVisible = 2,
            ThemeTypeVariation = "BodyLabel"
        };
        footer.AddChild(_lblSelectedInfo);

        _btnCancel = new Button
        {
            Text = "Cancel",
            CustomMinimumSize = new Vector2(90, 32),
            ThemeTypeVariation = "SecondaryButton"
        };
        footer.AddChild(_btnCancel);

        _btnSelect = new Button
        {
            Text = "Load Mod",
            CustomMinimumSize = new Vector2(130, 32),
            Disabled = true,
            ThemeTypeVariation = "PrimaryActionButton"
        };
        footer.AddChild(_btnSelect);
    }

    private void CreateFilterPill(ModFilterCategory category, string label, Color accentColor)
    {
        var btn = new Button
        {
            Text = label,
            ToggleMode = true,
            FocusMode = FocusModeEnum.None,
            MouseDefaultCursorShape = CursorShape.PointingHand,
            CustomMinimumSize = new Vector2(0, 30),
            ThemeTypeVariation = "FilterPillButton"
        };

        btn.Pressed += () => SetActiveFilter(category, applyFilter: true);

        _filterFlow.AddChild(btn);
        _filterButtons[category] = btn;
    }

    private void SetActiveFilter(ModFilterCategory category, bool applyFilter)
    {
        _activeFilter = category;
        foreach (var kvp in _filterButtons)
        {
            kvp.Value.SetPressedNoSignal(kvp.Key == category);
        }

        if (applyFilter)
        {
            ApplyFilters();
        }
    }

    private void UpdateFilterCounts(IReadOnlyList<AddonModInfo> mods)
    {
        bool showNonStandard = _chkShowNonStandard?.ButtonPressed ?? true;
        int totalCount = 1; // Includes Vanilla card
        int modelCount = 0;
        int textureCount = 0;
        int animCount = 0;
        int vfxOtherCount = 0;

        if (mods != null)
        {
            foreach (var mod in mods)
            {
                if (mod == null) continue;
                if (!showNonStandard && !mod.IsStandardPakFormat) continue;
                totalCount++;
                switch (mod.ModType)
                {
                    case AddonModType.FullModel:
                        modelCount++;
                        break;
                    case AddonModType.TextureOnly:
                        textureCount++;
                        break;
                    case AddonModType.AnimationOnly:
                        animCount++;
                        break;
                    case AddonModType.ParticleOnly:
                    case AddonModType.Mixed:
                    case AddonModType.Unknown:
                    default:
                        vfxOtherCount++;
                        break;
                }
            }
        }

        if (_filterButtons.TryGetValue(ModFilterCategory.All, out var btnAll))
            btnAll.Text = $"All ({totalCount})";
        if (_filterButtons.TryGetValue(ModFilterCategory.Models, out var btnModels))
            btnModels.Text = $"Model Mods ({modelCount})";
        if (_filterButtons.TryGetValue(ModFilterCategory.Textures, out var btnTextures))
            btnTextures.Text = $"Texture Mods ({textureCount})";
        if (_filterButtons.TryGetValue(ModFilterCategory.Animations, out var btnAnims))
            btnAnims.Text = $"Animation Mods ({animCount})";
        if (_filterButtons.TryGetValue(ModFilterCategory.VfxOther, out var btnVfx))
            btnVfx.Text = $"VFX / Other ({vfxOtherCount})";
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
        _currentMods = mods;
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

        UpdateFilterCounts(mods);
        ApplyFilters();
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
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        string q = _searchBar?.Text?.Trim().ToLowerInvariant() ?? "";
        foreach (var card in _allCards)
        {
            if (card == null) continue;
            if (card.IsVanilla)
            {
                bool matchesCat = _activeFilter == ModFilterCategory.All;
                bool matchesSearch = string.IsNullOrEmpty(q) || "vanilla".Contains(q) || "official".Contains(q);
                card.Visible = matchesCat && matchesSearch;
                continue;
            }

            var mod = card.ModInfo;
            if (mod == null) continue;

            bool showNonStandard = _chkShowNonStandard?.ButtonPressed ?? true;
            if (!showNonStandard && !mod.IsStandardPakFormat)
            {
                card.Visible = false;
                continue;
            }

            bool matchesCategory = _activeFilter switch
            {
                ModFilterCategory.All => true,
                ModFilterCategory.Models => mod.ModType == AddonModType.FullModel,
                ModFilterCategory.Textures => mod.ModType == AddonModType.TextureOnly,
                ModFilterCategory.Animations => mod.ModType == AddonModType.AnimationOnly,
                ModFilterCategory.VfxOther => mod.ModType == AddonModType.ParticleOnly ||
                                              mod.ModType == AddonModType.Mixed ||
                                              mod.ModType == AddonModType.Unknown,
                _ => true
            };

            bool matchesQuery = string.IsNullOrEmpty(q) ||
                                mod.DisplayTitle.ToLowerInvariant().Contains(q) ||
                                mod.FileName.ToLowerInvariant().Contains(q) ||
                                (!string.IsNullOrEmpty(mod.DetectedHeroDisplayName) && mod.DetectedHeroDisplayName.ToLowerInvariant().Contains(q)) ||
                                (!string.IsNullOrEmpty(mod.DetectedHeroCodename) && mod.DetectedHeroCodename.ToLowerInvariant().Contains(q));

            card.Visible = matchesCategory && matchesQuery;
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
