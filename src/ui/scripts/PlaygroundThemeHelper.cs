using Godot;
using System;
using DeadlockPlayground.Theming;

/// <summary>
/// Production theme helper and styling utility for the Deadlock 3D Playground.
/// Provides access to dynamic ThemeManager tokens, theme type variations,
/// and automated UI decoration methods.
/// </summary>
public static class PlaygroundThemeHelper
{
    public const string ThemePath = "res://assets/themes/PlaygroundTheme.tres";
    public const string FontColusPath = "res://assets/fonts/Colus-Regular.ttf";
    public const string FontDahliaPath = "res://assets/fonts/dahlia-bold.otf";
    public const string FontOrganicPath = "res://assets/fonts/Organic Relief.ttf";
    public const string FontSupersonicPath = "res://assets/fonts/Supersonic Rocketship.ttf";

    // --- Dynamic Palette Accessors (Backed by ThemeManager) ---
    public static Color Parchment => ThemeManager.GetColor(nameof(ThemePaletteData.AccentPrimary));
    public static Color SageGreen => ThemeManager.GetColor(nameof(ThemePaletteData.AccentSecondary));
    public static Color PineGreen => ThemeManager.GetColor(nameof(ThemePaletteData.BorderColor));
    public static Color DeepTeal => ThemeManager.GetColor(nameof(ThemePaletteData.SurfaceHeader));
    public static Color DarkCharcoal => ThemeManager.GetColor(nameof(ThemePaletteData.BackgroundSecondary));

    public static Color TextPrimary => ThemeManager.GetColor(nameof(ThemePaletteData.TextPrimary));
    public static Color TextSecondary => ThemeManager.GetColor(nameof(ThemePaletteData.TextSecondary));
    public static Color TextMuted => new Color(TextSecondary.R, TextSecondary.G, TextSecondary.B, 0.55f);
    public static Color BorderSubtle => new Color(PineGreen.R, PineGreen.G, PineGreen.B, 0.65f);
    public static Color DividerColor => new Color(SageGreen.R, SageGreen.G, SageGreen.B, 0.25f);

    // --- Theme Type Variation Names ---
    public const string VarAccentButton = "AccentButton";
    public const string VarPrimaryActionButton = "PrimaryActionButton";
    public const string VarDangerButton = "DangerButton";
    public const string VarToolbarButton = "ToolbarButton";
    public const string VarFilterPillButton = "FilterPillButton";
    public const string VarTabButton = "TabButton";
    public const string VarSecondaryButton = "SecondaryButton";
    public const string VarHeaderLabel = "HeaderLabel";
    public const string VarSubheaderLabel = "SubheaderLabel";
    public const string VarSectionHeaderLabel = "SectionHeaderLabel";
    public const string VarTitleLabel = "TitleLabel";
    public const string VarModalTitleLabel = "ModalTitleLabel";
    public const string VarBodyLabel = "BodyLabel";
    public const string VarMutedLabel = "MutedLabel";
    public const string VarNumericLabel = "NumericLabel";
    public const string VarNumericSpinBox = "NumericSpinBox";
    public const string VarNumericLineEdit = "NumericLineEdit";
    public const string VarBadgeSuccess = "BadgeLabelSuccess";
    public const string VarBadgeWarning = "BadgeLabelWarning";
    public const string VarBadgeInfo = "BadgeLabelInfo";
    public const string VarBadgeDanger = "BadgeLabelDanger";
    public const string VarCardPanel = "CardPanel";
    public const string VarInspectorSectionPanel = "InspectorSectionPanel";
    public const string VarModalHeaderPanel = "ModalHeaderPanel";
    public const string VarSubPanel = "SubPanelContainer";

    private static Theme _cachedTheme;
    private static FontFile _cachedFontColus;
    private static FontFile _cachedFontDahlia;
    private static FontFile _cachedFontOrganic;

    /// <summary>
    /// Loads and returns the cached PlaygroundTheme resource.
    /// </summary>
    public static Theme GetTheme()
    {
        if (_cachedTheme == null && ResourceLoader.Exists(ThemePath))
        {
            _cachedTheme = GD.Load<Theme>(ThemePath);
        }
        return _cachedTheme;
    }

    /// <summary>
    /// Loads and returns the Colus font resource.
    /// </summary>
    public static FontFile GetFontColus()
    {
        if (_cachedFontColus == null && ResourceLoader.Exists(FontColusPath))
        {
            _cachedFontColus = GD.Load<FontFile>(FontColusPath);
        }
        return _cachedFontColus;
    }

    /// <summary>
    /// Loads and returns the Dahlia font resource.
    /// </summary>
    public static FontFile GetFontDahlia()
    {
        if (_cachedFontDahlia == null && ResourceLoader.Exists(FontDahliaPath))
        {
            _cachedFontDahlia = GD.Load<FontFile>(FontDahliaPath);
        }
        return _cachedFontDahlia;
    }

    /// <summary>
    /// Loads and returns the Organic Relief font resource for numeric values.
    /// </summary>
    public static FontFile GetFontOrganic()
    {
        if (_cachedFontOrganic == null && ResourceLoader.Exists(FontOrganicPath))
        {
            _cachedFontOrganic = GD.Load<FontFile>(FontOrganicPath);
        }
        return _cachedFontOrganic;
    }

    /// <summary>
    /// Applies PlaygroundTheme to the target root control if not already applied.
    /// </summary>
    public static void ApplyTheme(Control root)
    {
        if (root == null) return;
        var theme = GetTheme();
        if (theme != null && root.Theme != theme)
        {
            root.Theme = theme;
        }
    }

    /// <summary>
    /// Styles a button as a primary action button (e.g. Export, Apply, Save)
    /// with vibrant Deadlock Sage Green fill and crisp Parchment trim.
    /// </summary>
    public static void MakeAccentButton(Button button)
    {
        if (button == null) return;
        button.ThemeTypeVariation = VarAccentButton;
    }

    /// <summary>
    /// Styles a button as a category navigation tab button.
    /// </summary>
    public static void MakeTabButton(Button button, bool active = false)
    {
        if (button == null) return;
        button.ThemeTypeVariation = VarTabButton;
        button.ToggleMode = true;
        UpdateTabButtonState(button, active);
    }

    /// <summary>
    /// Updates active visual state on a tab button cleanly.
    /// </summary>
    public static void UpdateTabButtonState(Button button, bool active)
    {
        if (button == null) return;
        button.ButtonPressed = active;
        // In case the button uses normal modulate, keep it clean and let theme styleboxes handle colors
        button.Modulate = active ? Colors.White : new Color(0.9f, 0.9f, 0.9f, 0.85f);
    }

    /// <summary>
    /// Styles a button as a secondary/utility button.
    /// </summary>
    public static void MakeSecondaryButton(Button button)
    {
        if (button == null) return;
        button.ThemeTypeVariation = VarSecondaryButton;
    }

    /// <summary>
    /// Creates theme-aware Normal, Hover, and Selected StyleBoxFlats for gallery/picker cards.
    /// </summary>
    public static (StyleBoxFlat Normal, StyleBoxFlat Hover, StyleBoxFlat Selected) CreateCardStateStyles(int cornerRadius = 8, int padding = 6)
    {
        Color bgNormal = ThemeManager.GetColor(nameof(ThemePaletteData.BackgroundSecondary));
        Color bgHover = ThemeManager.IsLightTheme()
            ? ThemeManager.GetColor(nameof(ThemePaletteData.SurfaceHeader))
            : bgNormal.Lightened(0.06f);
        Color bgSelected = ThemeManager.GetColor(nameof(ThemePaletteData.SurfaceHeader));
        Color borderNormal = ThemeManager.GetColor(nameof(ThemePaletteData.BorderColor));
        Color borderAccent = ThemeManager.GetColor(nameof(ThemePaletteData.AccentPrimary));

        StyleBoxFlat Make(Color bg, Color border, int bw) => new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = border,
            BorderWidthLeft = bw,
            BorderWidthRight = bw,
            BorderWidthTop = bw,
            BorderWidthBottom = bw,
            CornerRadiusTopLeft = cornerRadius,
            CornerRadiusTopRight = cornerRadius,
            CornerRadiusBottomLeft = cornerRadius,
            CornerRadiusBottomRight = cornerRadius,
            ContentMarginLeft = padding,
            ContentMarginRight = padding,
            ContentMarginTop = padding,
            ContentMarginBottom = padding
        };

        return (
            Make(bgNormal, new Color(borderNormal.R, borderNormal.G, borderNormal.B, 0.85f), 1),
            Make(bgHover, borderAccent, 1),
            Make(bgSelected, borderAccent, 2)
        );
    }

    /// <summary>
    /// Styles a card badge pill (<see cref="PanelContainer"/> + <see cref="Label"/>) so it remains
    /// crisp and high-contrast in both Light and Dark/Night themes.
    /// </summary>
    public static void ApplyCardBadgeStyle(PanelContainer badgePanel, Label badgeLabel, Color accentColor)
    {
        bool isLight = ThemeManager.IsLightTheme();
        Color pillBg;
        Color borderCol;
        Color textCol;

        if (isLight)
        {
            Color lightBase = new Color(0.95f, 0.96f, 0.98f, 0.95f);
            pillBg = lightBase.Lerp(new Color(accentColor.R, accentColor.G, accentColor.B, 0.95f), 0.16f);
            float lum = accentColor.Luminance;
            float darken = lum > 0.55f ? 0.42f : (lum > 0.35f ? 0.55f : 0.72f);
            textCol = new Color(accentColor.R * darken, accentColor.G * darken, accentColor.B * darken, 1.0f);
            borderCol = new Color(textCol.R, textCol.G, textCol.B, 0.35f);
        }
        else
        {
            pillBg = new Color(0.05f, 0.06f, 0.08f, 0.85f);
            borderCol = new Color(accentColor.R, accentColor.G, accentColor.B, 0.45f);
            textCol = accentColor;
        }

        if (badgePanel != null)
        {
            badgePanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = pillBg,
                CornerRadiusTopLeft = 4,
                CornerRadiusTopRight = 4,
                CornerRadiusBottomLeft = 4,
                CornerRadiusBottomRight = 4,
                BorderWidthLeft = 1,
                BorderWidthRight = 1,
                BorderWidthTop = 1,
                BorderWidthBottom = 1,
                BorderColor = borderCol,
                ContentMarginLeft = 6,
                ContentMarginRight = 6,
                ContentMarginTop = 2,
                ContentMarginBottom = 2
            });
        }

        if (badgeLabel != null)
        {
            badgeLabel.Modulate = Colors.White;
            badgeLabel.AddThemeColorOverride("font_color", textCol);
        }
    }

    /// <summary>
    /// Styles a label with Colus header typography in Deadlock Parchment.
    /// </summary>
    public static void MakeHeaderLabel(Label label)
    {
        if (label == null) return;
        label.ThemeTypeVariation = VarHeaderLabel;
    }

    /// <summary>
    /// Styles a label as a main application or modal title.
    /// </summary>
    public static void MakeTitleLabel(Label label)
    {
        if (label == null) return;
        label.ThemeTypeVariation = VarTitleLabel;
    }

    /// <summary>
    /// Styles a label with muted secondary readout typography.
    /// </summary>
    public static void MakeMutedLabel(Label label)
    {
        if (label == null) return;
        label.ThemeTypeVariation = VarMutedLabel;
    }

    /// <summary>
    /// Styles a label with Organic Relief font for numeric slider readouts and values.
    /// </summary>
    public static void MakeNumericLabel(Label label)
    {
        if (label == null) return;
        label.ThemeTypeVariation = VarNumericLabel;
    }

    /// <summary>
    /// Styles a PanelContainer as an elevated sub-card container.
    /// </summary>
    public static void MakeCard(PanelContainer panel)
    {
        if (panel == null) return;
        panel.ThemeTypeVariation = VarCardPanel;
    }

    /// <summary>
    /// Styles a PanelContainer as a dark inset sub-panel.
    /// </summary>
    public static void MakeSubPanel(PanelContainer panel)
    {
        if (panel == null) return;
        panel.ThemeTypeVariation = VarSubPanel;
    }

    /// <summary>
    /// Recursively decorates a node tree, automatically mapping common patterns
    /// to the standardized theme type variations.
    /// </summary>
    public static void AutoDecorate(Node root)
    {
        if (root == null) return;

        if (root is Control control)
        {
            ApplyTheme(control);
            DecorateControl(control);
        }

        foreach (Node child in root.GetChildren())
        {
            AutoDecorate(child);
        }
    }

    private static void DecorateControl(Control control)
    {
        string name = control.Name.ToString();

        // 1. Buttons (MUST NOT match CheckBox, CheckButton, or OptionButton for variation styling;
        //    keep Button AutowrapMode=Off so single-word buttons and HFlowContainer/HBoxContainer buttons never collapse or break mid-word.
        //    Only wrap CheckBox when inside a VBoxContainer so HBoxContainer checkboxes like BonesTab CheckLines never hide their label.)
        if (control is CheckBox chk)
        {
            if (chk.AutowrapMode == TextServer.AutowrapMode.Off && chk.GetParent() is VBoxContainer)
                chk.AutowrapMode = TextServer.AutowrapMode.Word;
        }

        if (control is Button btn && !(control is CheckBox) && !(control is CheckButton) && !(control is OptionButton))
        {
            // If already explicitly set, skip variation auto-assignment
            if (!string.IsNullOrEmpty(btn.ThemeTypeVariation))
                return;

            if (name.Contains("Export", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Apply", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Save", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("BtnPrimary", StringComparison.OrdinalIgnoreCase))
            {
                MakeAccentButton(btn);
            }
            else if (name.Contains("Tab", StringComparison.OrdinalIgnoreCase) ||
                     control.GetParent() is GridContainer grid && grid.Name.ToString().Contains("Tab", StringComparison.OrdinalIgnoreCase))
            {
                MakeTabButton(btn);
            }
        }
        // 2. Labels
        else if (control is Label lbl)
        {
            if (!string.IsNullOrEmpty(lbl.ThemeTypeVariation))
                return;

            // A. Numeric slider readouts (using Organic Relief font)
            if (IsSliderNumericLabel(lbl, name))
            {
                MakeNumericLabel(lbl);
                return;
            }

            // B. Section titles across all tabs (look like Camera Tab / Shading Tab)
            if (IsSectionTitle(lbl, name))
            {
                MakeTitleLabel(lbl);
                return;
            }

            // C. Sub-headers
            if (name.Contains("Header", StringComparison.OrdinalIgnoreCase) ||
                (name.EndsWith("Label") && lbl.Text.Length > 0 && IsLikelyHeader(lbl.Text)))
            {
                MakeHeaderLabel(lbl);
                return;
            }

            // D. Muted descriptions
            if (name.Contains("Desc", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Help", StringComparison.OrdinalIgnoreCase))
            {
                MakeMutedLabel(lbl);
                return;
            }
        }
        // 3. PanelContainers
        else if (control is PanelContainer pc)
        {
            if (!string.IsNullOrEmpty(pc.ThemeTypeVariation))
                return;

            if (name.Contains("Card", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("TabContentPanel", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("SubmeshPanel", StringComparison.OrdinalIgnoreCase))
            {
                MakeCard(pc);
            }
        }
    }

    private static bool IsSliderNumericLabel(Label lbl, string name)
    {
        // Known slider readout names
        if (name.StartsWith("LabelX") || name.StartsWith("LabelY") || name.StartsWith("LabelZ") ||
            name.Equals("LblDimensions", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("LabelCurr", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check sibling relationship: is this label positioned after an HSlider in the same container?
        if (lbl.GetParent() is BoxContainer parent)
        {
            int myIndex = lbl.GetIndex();
            foreach (Node child in parent.GetChildren())
            {
                if (child is HSlider or Slider)
                {
                    if (myIndex > child.GetIndex())
                    {
                        return true;
                    }
                }
            }
        }

        // Check if text has units or digits (e.g. "75°", "0.35", "1.00", "5.0m", "100%", "1.5x", "1920 × 1080 px")
        string text = lbl.Text;
        if (!string.IsNullOrWhiteSpace(text) && text.Length <= 18)
        {
            bool hasDigit = false;
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsDigit(text[i]))
                {
                    hasDigit = true;
                    break;
                }
            }
            if (hasDigit && (text.Contains('°') || text.Contains('%') || text.Contains('x') || 
                             text.Contains('m') || text.Contains('.') || text.Contains("px") || text.Contains('×')))
            {
                // Ensure it's not a title with numbers like "In-Game Background 1"
                if (!text.Contains("Background") && !text.Contains("Scene") && !text.Contains("Model") && !text.Contains("Preset"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsSectionTitle(Label lbl, string name)
    {
        // 1. Explicitly named Title or TitleGroupLabel (like in CameraTab & ShadingTab)
        if (name.Contains("Title", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AppLabel", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("TitleGroupLabel", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 2. Known section titles across all tabs
        if (name.Equals("HeroLabel", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PartsLabel", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("LayersLabel", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("XRayLabel", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("SearchLabel", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AnimLabel", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("ModeLabel", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("StageLabel", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("ColorLabel", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("GradLabel", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PatternLabel", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("DoFLabel", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("TonemapLabel", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("GlowLabel", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("FogLabel", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("SunLabel", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AmbLabel", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("TransformHeader", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 3. Structural header: first Label child of a *Group or *Header container
        Node parent = lbl.GetParent();
        if (parent != null)
        {
            string pName = parent.Name.ToString();
            if ((pName.EndsWith("Group", StringComparison.OrdinalIgnoreCase) ||
                 pName.EndsWith("Header", StringComparison.OrdinalIgnoreCase) ||
                 pName.StartsWith("Group", StringComparison.OrdinalIgnoreCase)) &&
                lbl.GetIndex() == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLikelyHeader(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string trimmed = text.Trim();
        return (trimmed.Length <= 35 && trimmed.ToUpperInvariant() == trimmed && char.IsLetter(trimmed[0]));
    }
}
