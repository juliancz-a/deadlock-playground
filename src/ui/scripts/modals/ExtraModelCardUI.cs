using Godot;
using System;

namespace DeadlockPlayground.UI;

public class ExtraModelEntry
{
    public string DisplayTitle { get; set; } = "";
    public string VmdlPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public bool IsMainHeroModel { get; set; } = false;
}

/// <summary>
/// Gallery card representing a hero's supplementary or ability-specific 3D model (.vmdl_c).
/// </summary>
public partial class ExtraModelCardUI : PanelContainer
{
    [Signal] public delegate void CardSelectedEventHandler(ExtraModelCardUI card);
    [Signal] public delegate void CardDoubleClickedEventHandler(ExtraModelCardUI card);

    public ExtraModelEntry Entry { get; private set; }

    private Label _lblTitle;
    private Label _lblPath;
    private PanelContainer _tagPanel;
    private Label _lblTag;

    private bool _isSelected = false;
    private bool _isHovered = false;

    private StyleBoxFlat _styleNormal;
    private StyleBoxFlat _styleHover;
    private StyleBoxFlat _styleSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            UpdateCardStyle();
        }
    }

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(200, 160);
        MouseFilter = MouseFilterEnum.Stop;

        InitStyles();
        BuildUI();
        UpdateCardStyle();
    }

    public void Setup(ExtraModelEntry entry)
    {
        Entry = entry;
        if (_lblTitle != null && entry != null)
        {
            _lblTitle.Text = entry.DisplayTitle;
            _lblPath.Text = entry.VmdlPath;

            if (entry.IsMainHeroModel)
            {
                _lblTag.Text = "Main Model";
                _lblTag.Modulate = new Color(0.40f, 0.75f, 1.0f, 1.0f);
            }
            else
            {
                _lblTag.Text = "Ability / Extra Model";
                _lblTag.Modulate = new Color(0.95f, 0.85f, 0.35f, 1.0f);
            }
        }
    }

    private void InitStyles()
    {
        _styleNormal = new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.12f, 0.16f, 0.95f),
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.22f, 0.26f, 0.34f, 0.8f),
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 10,
            ContentMarginBottom = 10
        };

        _styleHover = new StyleBoxFlat
        {
            BgColor = new Color(0.14f, 0.17f, 0.22f, 0.98f),
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.95f, 0.85f, 0.40f, 0.9f),
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 10,
            ContentMarginBottom = 10
        };

        _styleSelected = new StyleBoxFlat
        {
            BgColor = new Color(0.16f, 0.22f, 0.30f, 1.0f),
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            BorderWidthLeft = 2,
            BorderWidthRight = 2,
            BorderWidthTop = 2,
            BorderWidthBottom = 2,
            BorderColor = new Color(0.95f, 0.85f, 0.30f, 1.0f),
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 10,
            ContentMarginBottom = 10
        };
    }

    private void BuildUI()
    {
        var vbox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.Fill,
            SizeFlagsVertical = SizeFlags.Fill
        };
        vbox.AddThemeConstantOverride("separation", 6);
        AddChild(vbox);

        // Header row with Icon + Tag
        var headerRow = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.Fill
        };
        headerRow.AddThemeConstantOverride("separation", 6);
        vbox.AddChild(headerRow);

        var icon = new TextureRect
        {
            Texture = GD.Load<Texture2D>("res://assets/at-icons/mesh_select.svg"),
            CustomMinimumSize = new Vector2(16, 16),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Modulate = new Color(0.95f, 0.85f, 0.40f, 0.9f)
        };
        headerRow.AddChild(icon);

        _tagPanel = new PanelContainer
        {
            SizeFlagsHorizontal = SizeFlags.ShrinkEnd
        };
        var tagStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.08f, 0.10f, 0.85f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 2,
            ContentMarginBottom = 2
        };
        _tagPanel.AddThemeStyleboxOverride("panel", tagStyle);

        _lblTag = new Label { Text = "Extra Model" };
        _lblTag.AddThemeFontSizeOverride("font_size", 10);
        _tagPanel.AddChild(_lblTag);
        headerRow.AddChild(_tagPanel);

        // Title
        _lblTitle = new Label
        {
            Text = "Model Name",
            SizeFlagsHorizontal = SizeFlags.Fill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        var colusFont = PlaygroundThemeHelper.GetFontColus();
        if (colusFont != null)
        {
            _lblTitle.AddThemeFontOverride("font", colusFont);
        }
        _lblTitle.AddThemeFontSizeOverride("font_size", 14);
        _lblTitle.AddThemeColorOverride("font_color", Colors.White);
        vbox.AddChild(_lblTitle);

        // Path
        _lblPath = new Label
        {
            Text = "models/...",
            SizeFlagsHorizontal = SizeFlags.Fill,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        _lblPath.AddThemeFontSizeOverride("font_size", 11);
        _lblPath.AddThemeColorOverride("font_color", new Color(0.60f, 0.65f, 0.72f, 1.0f));
        vbox.AddChild(_lblPath);

        if (Entry != null)
        {
            Setup(Entry);
        }
    }

    private void UpdateCardStyle()
    {
        if (_isSelected)
        {
            AddThemeStyleboxOverride("panel", _styleSelected);
        }
        else if (_isHovered)
        {
            AddThemeStyleboxOverride("panel", _styleHover);
        }
        else
        {
            AddThemeStyleboxOverride("panel", _styleNormal);
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseEnter)
        {
            _isHovered = true;
            UpdateCardStyle();
        }
        else if (what == NotificationMouseExit)
        {
            _isHovered = false;
            UpdateCardStyle();
        }
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
        {
            if (mb.DoubleClick)
            {
                EmitSignal(SignalName.CardDoubleClicked, this);
            }
            else
            {
                EmitSignal(SignalName.CardSelected, this);
            }
            AcceptEvent();
        }
    }
}
