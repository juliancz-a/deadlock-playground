using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using SteamDatabase.ValvePak;
using DeadlockPlayground.Maps.Editor;

namespace DeadlockPlayground.UI;

/// <summary>
/// Centered modal dialog for browsing Deadlock 3D props and architectural models with asynchronous
/// thumbnail generation and instant ghost placement in the 3D Viewport.
/// </summary>
public partial class MapAssetBrowserUI : PanelContainer
{
    [Export] private OptionButton _categoryOption;
    [Export] private LineEdit _searchBar;
    [Export] private GridContainer _cardsGrid;
    [Export] private Label _resultsCountLabel;
    [Export] private Button _btnClose;

    private Package _baseGamePackage;
    private Package _hideoutPackage;
    private string _currentCategory = MapAssetCatalog.CategoryAll;
    private string _currentQuery = "";

    private readonly Queue<(MapCatalogItem item, TextureRect texRect)> _thumbnailQueue = new();
    private bool _isProcessingQueue = false;
    private int _searchBatchToken = 0;
    private Texture2D _placeholderIcon;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(960, 640);
        ApplyModalStyle();
        CenterModal();
        LinkReferences();
        PopulateCategories();
        _placeholderIcon = GD.Load<Texture2D>("res://assets/at-icons/cube.svg");
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

        float halfW = CustomMinimumSize.X > 0 ? CustomMinimumSize.X / 2f : 480f;
        float halfH = CustomMinimumSize.Y > 0 ? CustomMinimumSize.Y / 2f : 320f;
        OffsetLeft = -halfW;
        OffsetTop = -halfH;
        OffsetRight = halfW;
        OffsetBottom = halfH;
    }

    private void ApplyModalStyle()
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
    }

    private void LinkReferences()
    {
        _categoryOption ??= GetNodeOrNull<OptionButton>("VBox/HeaderBox/FilterRow/CategoryOption")
                          ?? FindChild("CategoryOption", true, false) as OptionButton;

        _searchBar ??= GetNodeOrNull<LineEdit>("VBox/HeaderBox/FilterRow/SearchBar")
                    ?? FindChild("SearchBar", true, false) as LineEdit;

        _cardsGrid ??= GetNodeOrNull<GridContainer>("VBox/Scroll/CardsGrid")
                    ?? FindChild("CardsGrid", true, false) as GridContainer;

        _resultsCountLabel ??= GetNodeOrNull<Label>("VBox/FooterBox/ResultsCountLabel")
                            ?? FindChild("ResultsCountLabel", true, false) as Label;

        _btnClose ??= GetNodeOrNull<Button>("VBox/TitleRow/BtnClose")
                   ?? FindChild("BtnClose", true, false) as Button;

        if (_categoryOption != null)
        {
            _categoryOption.ItemSelected += OnCategorySelected;
        }

        if (_searchBar != null)
        {
            _searchBar.TextChanged += OnSearchTextChanged;
        }

        if (_btnClose != null)
        {
            _btnClose.Pressed += CloseModal;
        }
    }

    public void CloseModal()
    {
        Visible = false;
        _thumbnailQueue.Clear();
        var studioUI = GetTree()?.Root?.FindChild("UIRoot", true, false) as StudioUIManager
                    ?? GetTree()?.Root?.FindChild("StudioUIManager", true, false) as StudioUIManager;
        studioUI?.UpdateModalsState();
    }

    public void OpenModal()
    {
        Visible = true;
        if (GetParent() is Control parentControl && !parentControl.Visible)
        {
            parentControl.Visible = true;
        }
        CenterModal();
        RefreshCatalog();
        var studioUI = GetTree()?.Root?.FindChild("UIRoot", true, false) as StudioUIManager
                    ?? GetTree()?.Root?.FindChild("StudioUIManager", true, false) as StudioUIManager;
        studioUI?.UpdateModalsState();
    }

    public void Initialize(Package baseGamePackage, Package hideoutPackage)
    {
        _baseGamePackage = baseGamePackage;
        _hideoutPackage = hideoutPackage;

        if (!MapAssetCatalog.IsIndexed && _baseGamePackage != null)
        {
            MapAssetCatalog.IndexAssets(_baseGamePackage, _hideoutPackage);
        }

        PopulateCategories();
        RefreshCatalog();
    }

    private void PopulateCategories()
    {
        if (_categoryOption == null) return;
        _categoryOption.Clear();

        for (int i = 0; i < MapAssetCatalog.Categories.Length; i++)
        {
            _categoryOption.AddItem(MapAssetCatalog.Categories[i], i);
        }
    }

    private void OnCategorySelected(long index)
    {
        if (index >= 0 && index < MapAssetCatalog.Categories.Length)
        {
            _currentCategory = MapAssetCatalog.Categories[index];
            RefreshCatalog();
        }
    }

    private void OnSearchTextChanged(string newText)
    {
        _currentQuery = newText;
        RefreshCatalog();
    }

    public void RefreshCatalog()
    {
        if (_cardsGrid == null) return;

        _searchBatchToken++;
        _thumbnailQueue.Clear();

        // Clear existing card nodes
        foreach (var child in _cardsGrid.GetChildren())
        {
            _cardsGrid.RemoveChild(child);
            child.QueueFree();
        }

        var items = MapAssetCatalog.FilterItems(_currentCategory, _currentQuery, maxResults: 100);

        if (_resultsCountLabel != null)
        {
            _resultsCountLabel.Text = $"Showing {items.Count} items (Category: {_currentCategory})";
        }

        foreach (var item in items)
        {
            var (card, texRect) = CreatePropCard(item);
            _cardsGrid.AddChild(card);

            var cached = PropThumbnailRenderer.GetCachedThumbnail(item.VmdlPath);
            if (cached != null)
            {
                texRect.Texture = cached;
            }
            else
            {
                texRect.Texture = _placeholderIcon;
                texRect.Modulate = new Color(0.5f, 0.6f, 0.7f, 0.4f);
                _thumbnailQueue.Enqueue((item, texRect));
            }
        }

        if (!_isProcessingQueue && _thumbnailQueue.Count > 0)
        {
            _ = ProcessThumbnailQueueAsync(_searchBatchToken);
        }
    }

    private async Task ProcessThumbnailQueueAsync(int batchToken)
    {
        _isProcessingQueue = true;

        while (_thumbnailQueue.Count > 0)
        {
            if (batchToken != _searchBatchToken || !Visible || !IsInsideTree())
            {
                break;
            }

            var (item, texRect) = _thumbnailQueue.Dequeue();
            if (!GodotObject.IsInstanceValid(texRect) || !texRect.IsInsideTree())
            {
                continue;
            }

            var thumb = await PropThumbnailRenderer.RenderThumbnailAsync(
                item.VmdlPath,
                _baseGamePackage,
                _hideoutPackage,
                this);

            if (batchToken == _searchBatchToken && GodotObject.IsInstanceValid(texRect))
            {
                if (thumb != null)
                {
                    texRect.Texture = thumb;
                    texRect.Modulate = Colors.White;
                }
            }
        }

        _isProcessingQueue = false;
    }

    private (Control card, TextureRect texRect) CreatePropCard(MapCatalogItem item)
    {
        var cardPanel = new PanelContainer();
        cardPanel.CustomMinimumSize = new Vector2(160, 190);
        cardPanel.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var normalStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.14f, 0.18f, 0.95f),
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.20f, 0.25f, 0.35f, 0.6f),
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 8,
            ContentMarginBottom = 8
        };

        var hoverStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.15f, 0.19f, 0.25f, 0.98f),
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.35f, 0.75f, 1.0f, 0.9f),
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 8,
            ContentMarginBottom = 8
        };

        cardPanel.AddThemeStyleboxOverride("panel", normalStyle);
        cardPanel.MouseEntered += () => cardPanel.AddThemeStyleboxOverride("panel", hoverStyle);
        cardPanel.MouseExited += () => cardPanel.AddThemeStyleboxOverride("panel", normalStyle);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 6);
        cardPanel.AddChild(vbox);

        // Thumbnail Container & TextureRect
        var thumbContainer = new PanelContainer();
        thumbContainer.CustomMinimumSize = new Vector2(0, 100);
        var thumbBgStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.12f, 1.0f),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6
        };
        thumbContainer.AddThemeStyleboxOverride("panel", thumbBgStyle);

        var texRect = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        thumbContainer.AddChild(texRect);
        vbox.AddChild(thumbContainer);

        // Title Label
        var nameLabel = new Label
        {
            Text = item.DisplayName,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MaxLinesVisible = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        nameLabel.AddThemeFontSizeOverride("font_size", 11);
        nameLabel.AddThemeColorOverride("font_color", new Color(0.9f, 0.92f, 0.96f, 1.0f));
        vbox.AddChild(nameLabel);

        // Place Button
        var btnPlace = new Button
        {
            Text = "Place Prop",
            CustomMinimumSize = new Vector2(0, 26),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        btnPlace.AddThemeFontSizeOverride("font_size", 11);
        btnPlace.Pressed += () =>
        {
            CloseModal();
            MapEditorManager.Instance?.StartPlacement(item.VmdlPath);
        };
        vbox.AddChild(btnPlace);

        return (cardPanel, texRect);
    }
}
