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
    private bool _isDisabled = false;

    private StyleBoxFlat _styleNormal;
    private StyleBoxFlat _styleHover;
    private StyleBoxFlat _styleSelected;

    public bool IsDisabled
    {
        get => _isDisabled;
        set
        {
            _isDisabled = value;
            MouseFilter = _isDisabled ? MouseFilterEnum.Stop : MouseFilterEnum.Stop;
            Modulate = _isDisabled ? new Color(0.7f, 0.7f, 0.75f, 0.75f) : Colors.White;
        }
    }

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
        MouseDefaultCursorShape = CursorShape.PointingHand;

        InitStyles();
        BuildUI();
        UpdateCardStyle();
    }

    public void Setup(ExtraModelEntry entry, bool isActiveModel = false)
    {
        Entry = entry;
        if (_lblTitle != null && entry != null)
        {
            _lblTitle.Text = entry.DisplayTitle;
            _lblPath.Text = entry.VmdlPath;

            if (entry.IsMainHeroModel)
            {
                _lblTag.Text = isActiveModel ? "Main Model (ACTIVE)" : "Main Model";
                _lblTag.Modulate = isActiveModel ? new Color(0.35f, 0.95f, 0.55f, 1.0f) : new Color(0.40f, 0.75f, 1.0f, 1.0f);
            }
            else
            {
                _lblTag.Text = isActiveModel ? "Extra Model (ACTIVE)" : "Ability / Extra Model";
                _lblTag.Modulate = isActiveModel ? new Color(0.35f, 0.95f, 0.55f, 1.0f) : new Color(0.95f, 0.85f, 0.35f, 1.0f);
            }

            if (isActiveModel)
            {
                IsDisabled = true;
                TooltipText = $"{entry.DisplayTitle} is already active.";
            }
        }
    }

    private void InitStyles()
    {
        (_styleNormal, _styleHover, _styleSelected) = PlaygroundThemeHelper.CreateCardStateStyles(cornerRadius: 8, padding: 10);
    }

    private void BuildUI()
    {
        var vbox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.Fill,
            SizeFlagsVertical = SizeFlags.Fill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        vbox.AddThemeConstantOverride("separation", 6);
        AddChild(vbox);

        // Header row with Icon + Tag
        var headerRow = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.Fill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        headerRow.AddThemeConstantOverride("separation", 6);
        vbox.AddChild(headerRow);

        var icon = new TextureRect
        {
            Texture = GD.Load<Texture2D>("res://assets/at-icons/mesh_select.svg"),
            CustomMinimumSize = new Vector2(16, 16),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Modulate = new Color(0.95f, 0.85f, 0.40f, 0.9f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        headerRow.AddChild(icon);

        _tagPanel = new PanelContainer
        {
            SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
            MouseFilter = MouseFilterEnum.Ignore
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

        var colusFont = PlaygroundThemeHelper.GetFontColus();

        _lblTag = new Label { Text = "Extra Model", MouseFilter = MouseFilterEnum.Ignore, ThemeTypeVariation = "BadgeLabelInfo" };
        _tagPanel.AddChild(_lblTag);
        headerRow.AddChild(_tagPanel);

        // Title
        _lblTitle = new Label
        {
            Text = "Model Name",
            SizeFlagsHorizontal = SizeFlags.Fill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore,
            ThemeTypeVariation = "SubheaderLabel"
        };
        vbox.AddChild(_lblTitle);

        // Path
        _lblPath = new Label
        {
            Text = "models/...",
            SizeFlagsHorizontal = SizeFlags.Fill,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = MouseFilterEnum.Ignore,
            ThemeTypeVariation = "MutedLabel"
        };
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
            if (mb.DoubleClick && !_isDisabled)
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
