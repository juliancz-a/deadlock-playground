using Godot;
using System;

namespace DeadlockPlayground.UI;

/// <summary>
/// Floating pill overlay displayed in the 3D viewport corner when a particle effect is loaded.
/// Allows viewing active particle metadata and unloading with a single click.
/// </summary>
public partial class ActiveParticleBadgeUI : PanelContainer
{
    [Signal] public delegate void UnloadRequestedEventHandler();

    private TextureRect _iconRect;
    private Label _lblParticleName;
    private Button _btnClose;

    public override void _Ready()
    {
        Visible = false;
        BuildUI();
    }

    private void BuildUI()
    {
        // Custom floating pill style
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.13f, 0.18f, 0.90f),
            CornerRadiusTopLeft = 14,
            CornerRadiusTopRight = 14,
            CornerRadiusBottomLeft = 14,
            CornerRadiusBottomRight = 14,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.40f, 0.75f, 1.0f, 0.8f),
            ContentMarginLeft = 10,
            ContentMarginRight = 8,
            ContentMarginTop = 4,
            ContentMarginBottom = 4
        };
        AddThemeStyleboxOverride("panel", style);

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 8);
        AddChild(hbox);

        // Left Icon: sparkles.svg (16x16)
        _iconRect = new TextureRect
        {
            Texture = GD.Load<Texture2D>("res://assets/icons/sparkles.svg"),
            CustomMinimumSize = new Vector2(16, 16),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            Modulate = new Color(0.95f, 0.85f, 0.40f, 1.0f)
        };
        hbox.AddChild(_iconRect);

        // Active Particle Label
        _lblParticleName = new Label
        {
            Text = "Active Particle",
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        _lblParticleName.AddThemeFontSizeOverride("font_size", 12);
        _lblParticleName.AddThemeColorOverride("font_color", Colors.White);
        hbox.AddChild(_lblParticleName);

        // Close [X] Button
        _btnClose = new Button
        {
            Text = "✕",
            Flat = true,
            CustomMinimumSize = new Vector2(20, 20),
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        _btnClose.AddThemeFontSizeOverride("font_size", 11);
        _btnClose.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f, 1.0f));
        _btnClose.AddThemeColorOverride("font_hover_color", Colors.Red);
        _btnClose.Pressed += () => EmitSignal(SignalName.UnloadRequested);
        hbox.AddChild(_btnClose);
    }

    public void ShowBadge(string heroName, string abilityName, string variantName)
    {
        string display = $"{heroName} — {abilityName}";
        if (!string.IsNullOrEmpty(variantName))
        {
            display += $" ({variantName})";
        }

        if (_lblParticleName != null)
        {
            _lblParticleName.Text = display;
        }

        Visible = true;
    }

    public void HideBadge()
    {
        Visible = false;
    }
}
