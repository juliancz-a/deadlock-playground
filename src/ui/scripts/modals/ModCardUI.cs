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
    private TextureRect _iconRect;
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
        MouseDefaultCursorShape = CursorShape.PointingHand;

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
                Color cyanAccent = new Color(0.40f, 0.75f, 1.0f, 1.0f);
                _lblTitle.Text = "Vanilla Character";
                _lblHero.Text = "Default Source 2 Assets";
                _lblFilename.Text = "pak01_dir.vpk";
                _lblTag.Text = "OFFICIAL";
                PlaygroundThemeHelper.ApplyCardBadgeStyle(_tagPanel, _lblTag, cyanAccent);
                ApplyCardIcon("res://assets/at-icons/checkmark.svg", cyanAccent);
            }
            else if (mod != null)
            {
                string heroName = !string.IsNullOrEmpty(mod.DetectedHeroDisplayName) ? mod.DetectedHeroDisplayName : "Custom Hero";
                var (typeLabel, badgeLabel, iconPath, accentColor) = GetModVisuals(mod.ModType);

                _lblTitle.Text = heroName;
                _lblHero.Text = typeLabel;
                _lblFilename.Text = mod.FileName;
                _lblTag.Text = badgeLabel;
                PlaygroundThemeHelper.ApplyCardBadgeStyle(_tagPanel, _lblTag, accentColor);
                ApplyCardIcon(iconPath, accentColor);
            }
        }
    }

    private void ApplyCardIcon(string iconPath, Color accentColor)
    {
        if (_iconRect == null) return;
        var tex = GD.Load<Texture2D>(iconPath) ?? GD.Load<Texture2D>("res://assets/at-icons/folder.svg");
        _iconRect.Texture = tex;
        _iconRect.Modulate = new Color(accentColor.R, accentColor.G, accentColor.B, 0.78f);
    }

    private static (string typeLabel, string badgeLabel, string iconPath, Color accentColor) GetModVisuals(AddonModType modType)
    {
        return modType switch
        {
            AddonModType.FullModel => (
                "Model Mod",
                "MODEL MOD",
                "res://assets/at-icons/person_body.svg",
                new Color(0.95f, 0.85f, 0.30f, 1.0f)
            ),
            AddonModType.TextureOnly => (
                "Texture Mod",
                "TEXTURE MOD",
                "res://assets/at-icons/palette.svg",
                new Color(0.60f, 0.85f, 0.50f, 1.0f)
            ),
            AddonModType.AnimationOnly => (
                "Animation Mod",
                "ANIMATION MOD",
                "res://assets/at-icons/film.svg",
                new Color(0.82f, 0.52f, 0.98f, 1.0f)
            ),
            AddonModType.ParticleOnly => (
                "VFX Mod",
                "VFX MOD",
                "res://assets/at-icons/sparkles.svg",
                new Color(1.0f, 0.62f, 0.25f, 1.0f)
            ),
            AddonModType.Mixed => (
                "Mixed Mod",
                "MIXED MOD",
                "res://assets/at-icons/layers.svg",
                new Color(0.35f, 0.88f, 0.85f, 1.0f)
            ),
            _ => (
                "Addon Mod",
                "MOD",
                "res://assets/at-icons/folder.svg",
                new Color(0.60f, 0.68f, 0.78f, 1.0f)
            )
        };
    }

    private void InitStyles()
    {
        (_styleNormal, _styleHover, _styleSelected) = PlaygroundThemeHelper.CreateCardStateStyles(cornerRadius: 8, padding: 6);
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

        var thumbPanel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(158, 170),
            SizeFlagsHorizontal = SizeFlags.Fill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            ClipContents = true,
            MouseFilter = MouseFilterEnum.Ignore
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
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        thumbPanel.AddChild(_thumbRect);

        // Fallback / category icon in thumbnail
        var iconCenter = new CenterContainer
        {
            SizeFlagsHorizontal = SizeFlags.Fill,
            SizeFlagsVertical = SizeFlags.Fill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _iconRect = new TextureRect
        {
            Texture = GD.Load<Texture2D>("res://assets/at-icons/folder.svg"),
            CustomMinimumSize = new Vector2(44, 44),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Modulate = new Color(0.40f, 0.75f, 1.0f, 0.78f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        iconCenter.AddChild(_iconRect);
        thumbPanel.AddChild(iconCenter);

        // Tag badge
        _tagPanel = new PanelContainer
        {
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            SizeFlagsVertical = SizeFlags.ShrinkBegin,
            MouseFilter = MouseFilterEnum.Ignore
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

        _lblTag = new Label { Text = "Mod", MouseFilter = MouseFilterEnum.Ignore, ThemeTypeVariation = "BadgeLabelInfo" };
        _tagPanel.AddChild(_lblTag);
        thumbPanel.AddChild(_tagPanel);

        _lblTitle = new Label
        {
            Text = "Mod Title",
            SizeFlagsHorizontal = SizeFlags.Fill,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = MouseFilterEnum.Ignore,
            ThemeTypeVariation = "SubheaderLabel"
        };
        vbox.AddChild(_lblTitle);

        _lblHero = new Label
        {
            Text = "Hero Target",
            SizeFlagsHorizontal = SizeFlags.Fill,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = MouseFilterEnum.Ignore,
            ThemeTypeVariation = "MutedLabel"
        };
        vbox.AddChild(_lblHero);

        _lblFilename = new Label
        {
            Text = "*.vpk",
            SizeFlagsHorizontal = SizeFlags.Fill,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = MouseFilterEnum.Ignore,
            ThemeTypeVariation = "MutedLabel"
        };
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
