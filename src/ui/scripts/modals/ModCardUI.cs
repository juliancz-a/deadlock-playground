using Godot;
using System;
using DeadlockPlayground.Addons;

namespace DeadlockPlayground.UI;

/// <summary>
/// Gallery card representing a user-installed VPK mod package (.vpk) or vanilla state.
/// </summary>
public partial class ModCardUI : PanelContainer
{
    [Signal] public delegate void CardSelectedEventHandler(ModCardUI card);
    [Signal] public delegate void CardDoubleClickedEventHandler(ModCardUI card);

    public AddonModInfo ModInfo { get; private set; }
    public bool IsVanilla { get; private set; }

    private TextureRect _thumbRect;
    private Label _lblTitle;
    private Label _lblHero;
    private Label _lblFilename;
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
        CustomMinimumSize = new Vector2(170, 230);
        MouseFilter = MouseFilterEnum.Stop;

        InitStyles();
        BuildUI();
        UpdateCardStyle();
    }

    public void Setup(AddonModInfo mod, bool isVanilla = false)
    {
        ModInfo = mod;
        IsVanilla = isVanilla;

        if (_lblTitle != null)
        {
            if (isVanilla)
            {
                _lblTitle.Text = "Vanilla Character";
                _lblHero.Text = "Default Source 2 Assets";
                _lblFilename.Text = "pak01_dir.vpk";
                _lblTag.Text = "Official";
                _lblTag.Modulate = new Color(0.40f, 0.75f, 1.0f, 1.0f);
            }
            else if (mod != null)
            {
                _lblTitle.Text = mod.DisplayTitle;
                string heroName = !string.IsNullOrEmpty(mod.DetectedHeroDisplayName) ? mod.DetectedHeroDisplayName : "Custom Hero";
                string typeName = mod.ModType == AddonModType.FullModel ? "Model Mod" : "Texture Mod";
                _lblHero.Text = $"{heroName} • {typeName}";
                _lblFilename.Text = mod.FileName;

                _lblTag.Text = typeName;
                _lblTag.Modulate = mod.ModType == AddonModType.FullModel
                    ? new Color(0.95f, 0.85f, 0.30f, 1.0f)
                    : new Color(0.60f, 0.85f, 0.50f, 1.0f);
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
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
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
            BorderColor = new Color(0.40f, 0.75f, 1.0f, 0.9f),
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
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
            BorderColor = new Color(0.40f, 0.75f, 1.0f, 1.0f),
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
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

        var thumbPanel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(158, 170),
            SizeFlagsHorizontal = SizeFlags.Fill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            ClipContents = true
        };
        var thumbStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.07f, 0.09f, 1.0f),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6
        };
        thumbPanel.AddThemeStyleboxOverride("panel", thumbStyle);
        vbox.AddChild(thumbPanel);

        _thumbRect = new TextureRect
        {
            SizeFlagsHorizontal = SizeFlags.Fill,
            SizeFlagsVertical = SizeFlags.Fill,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered
        };
        thumbPanel.AddChild(_thumbRect);

        // Fallback default icon in thumbnail
        var iconCenter = new CenterContainer
        {
            SizeFlagsHorizontal = SizeFlags.Fill,
            SizeFlagsVertical = SizeFlags.Fill
        };
        var iconRect = new TextureRect
        {
            Texture = GD.Load<Texture2D>("res://assets/at-icons/folder.svg"),
            CustomMinimumSize = new Vector2(40, 40),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Modulate = new Color(0.40f, 0.75f, 1.0f, 0.6f)
        };
        iconCenter.AddChild(iconRect);
        thumbPanel.AddChild(iconCenter);

        // Tag badge
        _tagPanel = new PanelContainer
        {
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            SizeFlagsVertical = SizeFlags.ShrinkBegin
        };
        _tagPanel.SetAnchorsPreset(LayoutPreset.TopLeft);
        var tagStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.06f, 0.08f, 0.85f),
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

        _lblTag = new Label { Text = "Mod" };
        _lblTag.AddThemeFontSizeOverride("font_size", 9);
        _tagPanel.AddChild(_lblTag);
        thumbPanel.AddChild(_tagPanel);

        _lblTitle = new Label
        {
            Text = "Mod Title",
            SizeFlagsHorizontal = SizeFlags.Fill,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        _lblTitle.AddThemeFontSizeOverride("font_size", 12);
        _lblTitle.AddThemeColorOverride("font_color", Colors.White);
        vbox.AddChild(_lblTitle);

        _lblHero = new Label
        {
            Text = "Hero Target",
            SizeFlagsHorizontal = SizeFlags.Fill,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        _lblHero.AddThemeFontSizeOverride("font_size", 10);
        _lblHero.AddThemeColorOverride("font_color", new Color(0.60f, 0.65f, 0.72f, 1.0f));
        vbox.AddChild(_lblHero);

        _lblFilename = new Label
        {
            Text = "*.vpk",
            SizeFlagsHorizontal = SizeFlags.Fill,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        _lblFilename.AddThemeFontSizeOverride("font_size", 9);
        _lblFilename.AddThemeColorOverride("font_color", new Color(0.45f, 0.50f, 0.58f, 1.0f));
        vbox.AddChild(_lblFilename);

        if (ModInfo != null || IsVanilla)
        {
            Setup(ModInfo, IsVanilla);
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
