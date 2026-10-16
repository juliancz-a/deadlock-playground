using Godot;
using System;
using DeadlockPlayground.Particles.Models;

namespace DeadlockPlayground.UI;

/// <summary>
/// Visual gallery card displaying a particle variant with its interactive preview thumbnail.
/// </summary>
public partial class ParticleCardUI : PanelContainer
{
    [Signal] public delegate void CardSelectedEventHandler(ParticleCardUI card);
    [Signal] public delegate void CardDoubleClickedEventHandler(ParticleCardUI card);

    public ParticleEntry Entry { get; private set; }
    public string AbilityName { get; private set; }

    private TextureRect _thumbRect;
    private Label _lblVariant;
    private Label _lblAbility;
    private PanelContainer _thumbPanel;

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
        CustomMinimumSize = new Vector2(210, 220);
        MouseFilter = MouseFilterEnum.Stop;

        InitStyles();
        BuildUI();
        UpdateCardStyle();
    }

    private void InitStyles()
    {
        _styleNormal = new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.14f, 0.17f, 0.95f),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.22f, 0.26f, 0.32f, 0.8f),
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        };

        _styleHover = new StyleBoxFlat
        {
            BgColor = new Color(0.16f, 0.19f, 0.24f, 0.98f),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.35f, 0.55f, 0.85f, 0.9f),
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        };

        _styleSelected = new StyleBoxFlat
        {
            BgColor = new Color(0.18f, 0.24f, 0.32f, 1.0f),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
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

        // Thumbnail Container
        _thumbPanel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(198, 140),
            SizeFlagsHorizontal = SizeFlags.Fill,
            SizeFlagsVertical = SizeFlags.Fill
        };
        var thumbStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.07f, 0.09f, 1.0f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4
        };
        _thumbPanel.AddThemeStyleboxOverride("panel", thumbStyle);

        _thumbRect = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsHorizontal = SizeFlags.Fill,
            SizeFlagsVertical = SizeFlags.Fill
        };
        _thumbPanel.AddChild(_thumbRect);
        vbox.AddChild(_thumbPanel);

        // Variant Name Label (Primary)
        _lblVariant = new Label
        {
            Text = Entry?.VariantName ?? "Particle Variant",
            HorizontalAlignment = HorizontalAlignment.Center,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = SizeFlags.Fill
        };
        _lblVariant.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 0.95f, 1.0f));
        _lblVariant.AddThemeFontSizeOverride("font_size", 13);
        vbox.AddChild(_lblVariant);

        // Ability Name Label (Secondary)
        _lblAbility = new Label
        {
            Text = AbilityName ?? "Ability",
            HorizontalAlignment = HorizontalAlignment.Center,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = SizeFlags.Fill
        };
        _lblAbility.AddThemeColorOverride("font_color", new Color(0.60f, 0.65f, 0.72f, 0.9f));
        _lblAbility.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(_lblAbility);
    }

    public void Setup(ParticleEntry entry, string abilityName)
    {
        Entry = entry;
        AbilityName = abilityName;

        if (_lblVariant != null) _lblVariant.Text = entry?.VariantName ?? "Variant";
        if (_lblAbility != null) _lblAbility.Text = abilityName ?? "";

        // Default placeholder icon
        var sparkles = GD.Load<Texture2D>("res://assets/icons/sparkles.svg");
        if (_thumbRect != null)
        {
            _thumbRect.Texture = sparkles;
            _thumbRect.Modulate = new Color(0.45f, 0.7f, 0.95f, 0.5f);
        }
    }

    public void SetThumbnail(Texture2D thumbnail)
    {
        if (_thumbRect != null && thumbnail != null)
        {
            _thumbRect.Texture = thumbnail;
            _thumbRect.Modulate = Colors.White;
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
}
