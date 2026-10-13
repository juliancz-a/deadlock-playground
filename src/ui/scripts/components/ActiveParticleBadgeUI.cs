using Godot;
using System;

namespace DeadlockPlayground.UI;

/// <summary>
/// Themed card overlay displayed in the top-right export area when a particle effect is loaded.
/// Replaces the redundant screenshot exporter card and provides explicit controls:
/// particle metadata, "Browse Particles" modal trigger, and stylized unload button.
/// Fully adheres to PlaygroundTheme.tres.
/// </summary>
public partial class ActiveParticleBadgeUI : PanelContainer
{
    [Signal] public delegate void BrowseRequestedEventHandler();
    [Signal] public delegate void UnloadRequestedEventHandler();

    private TextureRect _iconRect;
    private Label _lblHeader;
    private Label _lblHeroAbility;
    private Label _lblVariant;
    private Button _btnBrowse;
    private Button _btnUnload;

    private Control _exportPanel;

    public override void _Ready()
    {
        Visible = false;
        Theme = GD.Load<Theme>("res://assets/themes/PlaygroundTheme.tres");
        BuildUI();
    }

    private void BuildUI()
    {
        CustomMinimumSize = new Vector2(280, 0);

        // Position in the top-right export area (matching ExportPanel)
        SetAnchorsPreset(LayoutPreset.TopRight);
        AnchorLeft = 1.0f;
        AnchorRight = 1.0f;
        AnchorTop = 0.0f;
        AnchorBottom = 0.0f;
        GrowHorizontal = GrowDirection.Begin;
        GrowVertical = GrowDirection.End;
        OffsetLeft = -296.0f;
        OffsetTop = 46.0f;
        OffsetRight = -16.0f;
        OffsetBottom = 160.0f;
        ZIndex = 50;

        // Card styling matching PlaygroundTheme.tres
        var cardStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.12f, 0.95f),
            CornerRadiusTopLeft = 10,
            CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10,
            CornerRadiusBottomRight = 10,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.28f, 0.32f, 0.42f, 0.7f),
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            ContentMarginTop = 12,
            ContentMarginBottom = 12
        };
        AddThemeStyleboxOverride("panel", cardStyle);

        var rootVbox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.Fill,
            SizeFlagsVertical = SizeFlags.Fill
        };
        rootVbox.AddThemeConstantOverride("separation", 8);
        AddChild(rootVbox);

        // 1. Header Row (Sparkle icon + ACTIVE VFX title)
        var headerRow = new HBoxContainer();
        headerRow.AddThemeConstantOverride("separation", 8);
        rootVbox.AddChild(headerRow);

        _iconRect = new TextureRect
        {
            Texture = GD.Load<Texture2D>("res://assets/icons/sparkles.svg"),
            CustomMinimumSize = new Vector2(18, 18),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            Modulate = new Color(0.95f, 0.85f, 0.40f, 1.0f)
        };
        headerRow.AddChild(_iconRect);

        _lblHeader = new Label
        {
            Text = "ACTIVE PARTICLE VFX",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        _lblHeader.AddThemeFontSizeOverride("font_size", 11);
        _lblHeader.AddThemeColorOverride("font_color", new Color(0.85f, 0.75f, 0.55f, 1.0f));
        headerRow.AddChild(_lblHeader);

        // Header separator
        var sep = new HSeparator();
        rootVbox.AddChild(sep);

        // 2. Metadata Section (Hero + Ability & Variant)
        _lblHeroAbility = new Label
        {
            Text = "Hero Particle",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _lblHeroAbility.AddThemeFontSizeOverride("font_size", 13);
        _lblHeroAbility.AddThemeColorOverride("font_color", Colors.White);
        rootVbox.AddChild(_lblHeroAbility);

        _lblVariant = new Label
        {
            Text = "Variant: Default",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _lblVariant.AddThemeFontSizeOverride("font_size", 10);
        _lblVariant.AddThemeColorOverride("font_color", new Color(0.65f, 0.70f, 0.78f, 1.0f));
        rootVbox.AddChild(_lblVariant);

        // 3. Explicit Button Layout: "Browse Particles" and "Unload"
        var buttonsRow = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.Fill
        };
        buttonsRow.AddThemeConstantOverride("separation", 8);
        rootVbox.AddChild(buttonsRow);

        _btnBrowse = new Button
        {
            Text = "Browse VFX",
            Icon = GD.Load<Texture2D>("res://assets/icons/sparkles.svg"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 28)
        };
        _btnBrowse.AddThemeFontSizeOverride("font_size", 11);
        _btnBrowse.Pressed += () => EmitSignal(SignalName.BrowseRequested);
        buttonsRow.AddChild(_btnBrowse);

        _btnUnload = new Button
        {
            Text = "Close Effect",
            TooltipText = "Unload currently active particle VFX",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 28)
        };
        _btnUnload.AddThemeFontSizeOverride("font_size", 11);
        _btnUnload.AddThemeColorOverride("font_hover_color", new Color(1.0f, 0.45f, 0.45f, 1.0f));
        _btnUnload.Pressed += () =>
        {
            HideBadge();
            EmitSignal(SignalName.UnloadRequested);
        };
        buttonsRow.AddChild(_btnUnload);
    }

    private void ResolveExportPanel()
    {
        if (_exportPanel == null || !GodotObject.IsInstanceValid(_exportPanel))
        {
            _exportPanel = GetParent()?.FindChild("ExportPanel", true, false) as Control
                        ?? GetTree()?.Root?.FindChild("ExportPanel", true, false) as Control;
        }
    }

    private bool _isPaintingMode = false;

    public void SetPaintingMode(bool isPainting)
    {
        _isPaintingMode = isPainting;
        UpdateBadgePosition();
    }

    private void UpdateBadgePosition()
    {
        if (_isPaintingMode)
        {
            // Position underneath the Source 2 Mod Export panel (46 to 250px)
            OffsetTop = 262.0f;
            OffsetBottom = 376.0f;
            OffsetLeft = -296.0f;
            OffsetRight = -16.0f;
        }
        else
        {
            // Standard top-right export area position
            OffsetTop = 46.0f;
            OffsetBottom = 160.0f;
            OffsetLeft = -296.0f;
            OffsetRight = -16.0f;
        }
    }

    public void ShowBadge(string heroName, string abilityName, string variantName)
    {
        ResolveExportPanel();
        if (_exportPanel != null && GodotObject.IsInstanceValid(_exportPanel))
        {
            _exportPanel.Visible = false;
        }

        UpdateBadgePosition();

        if (_lblHeroAbility != null)
        {
            string heroDisplay = string.IsNullOrEmpty(heroName) ? "Hero" : heroName;
            string abilityDisplay = string.IsNullOrEmpty(abilityName) ? "Ability VFX" : abilityName;
            _lblHeroAbility.Text = $"{heroDisplay} — {abilityDisplay}";
        }

        if (_lblVariant != null)
        {
            string varText = string.IsNullOrEmpty(variantName) ? "Standard" : variantName;
            _lblVariant.Text = $"Variant: {varText}";
        }

        Visible = true;
    }

    public void HideBadge()
    {
        Visible = false;
        ResolveExportPanel();
        if (_exportPanel != null && GodotObject.IsInstanceValid(_exportPanel))
        {
            // Restore screenshot export panel unless in paint tab
            _exportPanel.Visible = !_isPaintingMode;
        }
    }
}
