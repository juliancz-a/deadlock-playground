using Godot;
using System;

/// <summary>
/// Production theme helper and styling utility for the Deadlock 3D Playground.
/// Provides access to the Deadlock dark-green & parchment palette, theme type variations,
/// and automated UI decoration methods.
/// </summary>
public static class PlaygroundThemeHelper
{
    public const string ThemePath = "res://assets/themes/PlaygroundTheme.tres";
    public const string FontColusPath = "res://assets/fonts/Colus-Regular.ttf";
    public const string FontDahliaPath = "res://assets/fonts/dahlia-bold.otf";
    public const string FontOrganicPath = "res://assets/fonts/Organic Relief.ttf";

    // --- Official Deadlock Palette Constants ---
    /// <summary> #efdebf - Parchment / Cream highlight (titles, active accents, headers) </summary>
    public static readonly Color Parchment = new Color(0.937255f, 0.870588f, 0.74902f, 1.0f);

    /// <summary> #72947f - Sage Green / Jade accent (sliders, active borders, toggles) </summary>
    public static readonly Color SageGreen = new Color(0.447059f, 0.580392f, 0.498039f, 1.0f);

    /// <summary> #3f5d4d - Pine / Moss Green (button hover, focused outlines, card borders) </summary>
    public static readonly Color PineGreen = new Color(0.247059f, 0.364706f, 0.301961f, 1.0f);

    /// <summary> #2f4442 - Deep Teal / Forest (cards, sub-panels, button normal surfaces) </summary>
    public static readonly Color DeepTeal = new Color(0.184314f, 0.266667f, 0.258824f, 1.0f);

    /// <summary> #222021 - Dark Charcoal Base (sidebar, dialog backdrops, line edits) </summary>
    public static readonly Color DarkCharcoal = new Color(0.133333f, 0.12549f, 0.129412f, 1.0f);

    // --- Text & Border Helpers ---
    public static readonly Color TextPrimary = new Color(0.92f, 0.94f, 0.92f, 1.0f);
    public static readonly Color TextSecondary = new Color(0.72f, 0.78f, 0.75f, 1.0f);
    public static readonly Color TextMuted = new Color(0.50f, 0.55f, 0.52f, 0.45f);
    public static readonly Color BorderSubtle = new Color(0.247059f, 0.364706f, 0.301961f, 0.65f);
    public static readonly Color DividerColor = new Color(0.447059f, 0.580392f, 0.498039f, 0.22f);

    // --- Theme Type Variation Names ---
    public const string VarAccentButton = "AccentButton";
    public const string VarTabButton = "TabButton";
    public const string VarSecondaryButton = "SecondaryButton";
    public const string VarHeaderLabel = "HeaderLabel";
    public const string VarTitleLabel = "TitleLabel";
    public const string VarMutedLabel = "MutedLabel";
    public const string VarNumericLabel = "NumericLabel";
    public const string VarCardPanel = "CardPanel";
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

        // 1. Buttons (MUST NOT match CheckBox, CheckButton, or OptionButton)
        if (control is Button btn && !(control is CheckBox) && !(control is CheckButton) && !(control is OptionButton))
        {
            // If already explicitly set, skip
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
