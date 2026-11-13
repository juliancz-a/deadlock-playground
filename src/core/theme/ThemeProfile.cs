using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;

namespace DeadlockPlayground.Theming;

/// <summary>
/// Semantic color tokens driving all StyleBoxFlat backgrounds, borders, and font colors
/// across PlaygroundTheme.tres and ModalDialogTheme.tres.
/// </summary>
public class ThemePaletteData
{
    public string BackgroundPrimary { get; set; } = "#171616F5";
    public string BackgroundSecondary { get; set; } = "#222021F5";
    public string BackgroundTertiary { get; set; } = "#141314FA";
    public string SurfaceHeader { get; set; } = "#2F4442FA";
    public string AccentPrimary { get; set; } = "#EFDEBF";
    public string AccentSecondary { get; set; } = "#72947F";
    public string TextPrimary { get; set; } = "#EBF0EB";
    public string TextSecondary { get; set; } = "#B8C7BF";
    public string BorderColor { get; set; } = "#3F5D4D";
    public string SemanticSuccess { get; set; } = "#52B788";
    public string SemanticWarning { get; set; } = "#E9C46A";
    public string SemanticDanger { get; set; } = "#E76F51";

    public ThemePaletteData Clone()
    {
        return (ThemePaletteData)MemberwiseClone();
    }
}

/// <summary>
/// 3-role typography configuration and safe font size scaling bounds.
/// </summary>
public class ThemeTypographyData
{
    public const string BuiltinColus = "builtin:colus";
    public const string BuiltinDahlia = "builtin:dahlia";
    public const string BuiltinSupersonic = "builtin:supersonic";
    public const string BuiltinSans = "builtin:sans";

    public string HeadingFont { get; set; } = BuiltinColus;
    public string BodyFont { get; set; } = BuiltinDahlia;
    public string NumericFont { get; set; } = BuiltinSupersonic;

    public int GlobalFontScaleOffset { get; set; } = 0; // [-2, 6]
    public int HeadingFontSize { get; set; } = 18;      // [14, 28]
    public int BodyFontSize { get; set; } = 16;         // [12, 22]
    public int NumericFontSize { get; set; } = 15;      // [12, 22]
    public int FontOutlineStrength { get; set; } = 0;   // [0, 4]

    public ThemeTypographyData Clone()
    {
        return (ThemeTypographyData)MemberwiseClone();
    }
}

/// <summary>
/// Geometry, border, backdrop, and spacing parameters for live stylebox generation.
/// </summary>
public class ThemeStyleData
{
    public int CornerRadius { get; set; } = 6;              // [0, 16]
    public int BorderWidth { get; set; } = 1;               // [0, 4]
    public float BorderOpacity { get; set; } = 0.85f;       // [0.0, 1.0]
    public float ModalBackdropOpacity { get; set; } = 0.72f;// [0.30, 0.95]
    public int ControlSpacing { get; set; } = 6;            // [4, 12]

    public ThemeStyleData Clone()
    {
        return (ThemeStyleData)MemberwiseClone();
    }
}

/// <summary>
/// Complete serializable theme profile with built-in presets and tamper-proof sanitization.
/// </summary>
public class ThemeProfile
{
    public const string PresetDefault = "default";
    public const string PresetNight = "night";
    public const string PresetLight = "light";
    public const string PresetCustom = "custom";

    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Default (Deadlock Studio)";
    public string PresetId { get; set; } = PresetDefault;
    public ThemePaletteData Palette { get; set; } = new();
    public ThemeTypographyData Typography { get; set; } = new();
    public ThemeStyleData Style { get; set; } = new();

    public ThemeProfile Clone()
    {
        return new ThemeProfile
        {
            Version = Version,
            Name = Name,
            PresetId = PresetId,
            Palette = Palette?.Clone() ?? new ThemePaletteData(),
            Typography = Typography?.Clone() ?? new ThemeTypographyData(),
            Style = Style?.Clone() ?? new ThemeStyleData()
        };
    }

    public static ThemeProfile CreateDefaultPreset()
    {
        return new ThemeProfile
        {
            Version = 1,
            Name = "Default (Deadlock Studio)",
            PresetId = PresetDefault,
            Palette = new ThemePaletteData
            {
                BackgroundPrimary = "#171616F5",
                BackgroundSecondary = "#222021F5",
                BackgroundTertiary = "#141314FA",
                SurfaceHeader = "#2F4442FA",
                AccentPrimary = "#EFDEBF",
                AccentSecondary = "#72947F",
                TextPrimary = "#EBF0EB",
                TextSecondary = "#B8C7BF",
                BorderColor = "#3F5D4D",
                SemanticSuccess = "#52B788",
                SemanticWarning = "#E9C46A",
                SemanticDanger = "#E76F51"
            },
            Typography = new ThemeTypographyData
            {
                HeadingFont = ThemeTypographyData.BuiltinColus,
                BodyFont = ThemeTypographyData.BuiltinDahlia,
                NumericFont = ThemeTypographyData.BuiltinSupersonic,
                GlobalFontScaleOffset = 0,
                HeadingFontSize = 18,
                BodyFontSize = 16,
                NumericFontSize = 15,
                FontOutlineStrength = 0
            },
            Style = new ThemeStyleData
            {
                CornerRadius = 6,
                BorderWidth = 1,
                BorderOpacity = 0.85f,
                ModalBackdropOpacity = 0.72f,
                ControlSpacing = 6
            }
        };
    }

    public static ThemeProfile CreateNightPreset()
    {
        return new ThemeProfile
        {
            Version = 1,
            Name = "Night (OLED Deep Dark)",
            PresetId = PresetNight,
            Palette = new ThemePaletteData
            {
                BackgroundPrimary = "#0B0C10FA",
                BackgroundSecondary = "#12151CFA",
                BackgroundTertiary = "#08090DFC",
                SurfaceHeader = "#181C26FA",
                AccentPrimary = "#5CE1E6",
                AccentSecondary = "#A78BFA",
                TextPrimary = "#F1F5F9",
                TextSecondary = "#94A3B8",
                BorderColor = "#273043",
                SemanticSuccess = "#34D399",
                SemanticWarning = "#FBBF24",
                SemanticDanger = "#F87171"
            },
            Typography = new ThemeTypographyData
            {
                HeadingFont = ThemeTypographyData.BuiltinColus,
                BodyFont = ThemeTypographyData.BuiltinDahlia,
                NumericFont = ThemeTypographyData.BuiltinSupersonic,
                GlobalFontScaleOffset = 0,
                HeadingFontSize = 18,
                BodyFontSize = 16,
                NumericFontSize = 15,
                FontOutlineStrength = 0
            },
            Style = new ThemeStyleData
            {
                CornerRadius = 6,
                BorderWidth = 1,
                BorderOpacity = 0.85f,
                ModalBackdropOpacity = 0.82f,
                ControlSpacing = 6
            }
        };
    }

    public static ThemeProfile CreateLightPreset()
    {
        return new ThemeProfile
        {
            Version = 1,
            Name = "Light (Daylight Cream / Parchment)",
            PresetId = PresetLight,
            Palette = new ThemePaletteData
            {
                BackgroundPrimary = "#F6F7FAFC",
                BackgroundSecondary = "#EBEEF3FA",
                BackgroundTertiary = "#FFFFFFFC",
                SurfaceHeader = "#DDE3ECFA",
                AccentPrimary = "#145A52",
                AccentSecondary = "#9E5A14",
                TextPrimary = "#161A20",
                TextSecondary = "#47525E",
                BorderColor = "#A8B3C0",
                SemanticSuccess = "#157A4C",
                SemanticWarning = "#A86208",
                SemanticDanger = "#B91C1C"
            },
            Typography = new ThemeTypographyData
            {
                HeadingFont = ThemeTypographyData.BuiltinColus,
                BodyFont = ThemeTypographyData.BuiltinDahlia,
                NumericFont = ThemeTypographyData.BuiltinSupersonic,
                GlobalFontScaleOffset = 0,
                HeadingFontSize = 18,
                BodyFontSize = 16,
                NumericFontSize = 15,
                FontOutlineStrength = 0
            },
            Style = new ThemeStyleData
            {
                CornerRadius = 6,
                BorderWidth = 1,
                BorderOpacity = 0.85f,
                ModalBackdropOpacity = 0.55f,
                ControlSpacing = 6
            }
        };
    }

    public static ThemeProfile GetPreset(string presetId)
    {
        return presetId?.ToLowerInvariant() switch
        {
            PresetNight => CreateNightPreset(),
            PresetLight => CreateLightPreset(),
            _ => CreateDefaultPreset()
        };
    }

    /// <summary>
    /// Validates, clamps, and sanitizes an external or user-edited ThemeProfile so that
    /// corrupted JSON, NaN/Infinity values, missing custom fonts, or zero-contrast colors
    /// can never break or brick the UI.
    /// </summary>
    public static ThemeProfile SanitizeAndValidateProfile(ThemeProfile input, out List<string> warnings, Func<string, bool> customFontExists = null)
    {
        warnings = new List<string>();
        var defaults = CreateDefaultPreset();

        if (input == null)
        {
            warnings.Add("Theme profile was null; fell back to Default preset.");
            return defaults;
        }

        var result = input.Clone();

        if (string.IsNullOrWhiteSpace(result.Name))
        {
            result.Name = "Custom Theme";
            warnings.Add("Theme name was empty; assigned 'Custom Theme'.");
        }
        if (string.IsNullOrWhiteSpace(result.PresetId))
        {
            result.PresetId = PresetCustom;
        }

        result.Palette ??= defaults.Palette.Clone();
        result.Typography ??= defaults.Typography.Clone();
        result.Style ??= defaults.Style.Clone();

        // 1. Validate Palette Colors
        var p = result.Palette;
        var dp = defaults.Palette;
        p.BackgroundPrimary = SanitizeHex(p.BackgroundPrimary, dp.BackgroundPrimary, nameof(p.BackgroundPrimary), warnings);
        p.BackgroundSecondary = SanitizeHex(p.BackgroundSecondary, dp.BackgroundSecondary, nameof(p.BackgroundSecondary), warnings);
        p.BackgroundTertiary = SanitizeHex(p.BackgroundTertiary, dp.BackgroundTertiary, nameof(p.BackgroundTertiary), warnings);
        p.SurfaceHeader = SanitizeHex(p.SurfaceHeader, dp.SurfaceHeader, nameof(p.SurfaceHeader), warnings);
        p.AccentPrimary = SanitizeHex(p.AccentPrimary, dp.AccentPrimary, nameof(p.AccentPrimary), warnings);
        p.AccentSecondary = SanitizeHex(p.AccentSecondary, dp.AccentSecondary, nameof(p.AccentSecondary), warnings);
        p.TextPrimary = SanitizeHex(p.TextPrimary, dp.TextPrimary, nameof(p.TextPrimary), warnings);
        p.TextSecondary = SanitizeHex(p.TextSecondary, dp.TextSecondary, nameof(p.TextSecondary), warnings);
        p.BorderColor = SanitizeHex(p.BorderColor, dp.BorderColor, nameof(p.BorderColor), warnings);
        p.SemanticSuccess = SanitizeHex(p.SemanticSuccess, dp.SemanticSuccess, nameof(p.SemanticSuccess), warnings);
        p.SemanticWarning = SanitizeHex(p.SemanticWarning, dp.SemanticWarning, nameof(p.SemanticWarning), warnings);
        p.SemanticDanger = SanitizeHex(p.SemanticDanger, dp.SemanticDanger, nameof(p.SemanticDanger), warnings);

        // 2. Contrast / Legibility Guard (TextPrimary vs BackgroundPrimary & BackgroundSecondary)
        if (TryParseColor(p.TextPrimary, out var textCol) &&
            TryParseColor(p.BackgroundPrimary, out var bg1Col) &&
            TryParseColor(p.BackgroundSecondary, out var bg2Col))
        {
            double contrast1 = ComputeContrastRatio(textCol, bg1Col);
            double contrast2 = ComputeContrastRatio(textCol, bg2Col);
            if (contrast1 < 2.2 || contrast2 < 2.2)
            {
                warnings.Add($"Zero/low contrast detected between TextPrimary ({p.TextPrimary}) and backgrounds ({p.BackgroundPrimary} / {p.BackgroundSecondary}); reverted TextPrimary & BackgroundPrimary to Default for legibility.");
                p.TextPrimary = dp.TextPrimary;
                p.BackgroundPrimary = dp.BackgroundPrimary;
                p.BackgroundSecondary = dp.BackgroundSecondary;
            }
        }

        // 3. Clamp Typography Sizes & Fonts
        var t = result.Typography;
        t.GlobalFontScaleOffset = ClampWithWarn(t.GlobalFontScaleOffset, -2, 6, nameof(t.GlobalFontScaleOffset), warnings);
        t.HeadingFontSize = ClampWithWarn(t.HeadingFontSize, 14, 28, nameof(t.HeadingFontSize), warnings);
        t.BodyFontSize = ClampWithWarn(t.BodyFontSize, 12, 22, nameof(t.BodyFontSize), warnings);
        t.NumericFontSize = ClampWithWarn(t.NumericFontSize, 12, 22, nameof(t.NumericFontSize), warnings);
        t.FontOutlineStrength = ClampWithWarn(t.FontOutlineStrength, 0, 4, nameof(t.FontOutlineStrength), warnings);

        t.HeadingFont = SanitizeFontId(t.HeadingFont, ThemeTypographyData.BuiltinColus, nameof(t.HeadingFont), warnings, customFontExists);
        t.BodyFont = SanitizeFontId(t.BodyFont, ThemeTypographyData.BuiltinDahlia, nameof(t.BodyFont), warnings, customFontExists);
        t.NumericFont = SanitizeFontId(t.NumericFont, ThemeTypographyData.BuiltinSupersonic, nameof(t.NumericFont), warnings, customFontExists);

        // 4. Clamp Style Geometry & Guard NaN/Infinity
        var s = result.Style;
        s.CornerRadius = ClampWithWarn(s.CornerRadius, 0, 16, nameof(s.CornerRadius), warnings);
        s.BorderWidth = ClampWithWarn(s.BorderWidth, 0, 4, nameof(s.BorderWidth), warnings);
        s.ControlSpacing = ClampWithWarn(s.ControlSpacing, 4, 12, nameof(s.ControlSpacing), warnings);
        s.BorderOpacity = ClampFloatWithWarn(s.BorderOpacity, 0.0f, 1.0f, defaults.Style.BorderOpacity, nameof(s.BorderOpacity), warnings);
        s.ModalBackdropOpacity = ClampFloatWithWarn(s.ModalBackdropOpacity, 0.30f, 0.95f, defaults.Style.ModalBackdropOpacity, nameof(s.ModalBackdropOpacity), warnings);

        return result;
    }

    private static string SanitizeHex(string hex, string fallback, string fieldName, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(hex) || !TryParseColor(hex, out var parsed))
        {
            warnings.Add($"Invalid color '{hex ?? "null"}' for {fieldName}; fell back to {fallback}.");
            return fallback;
        }
        // Normalize to #RRGGBBAA or #RRGGBB
        return ToHexString(parsed);
    }

    private static string SanitizeFontId(string fontId, string fallback, string fieldName, List<string> warnings, Func<string, bool> customFontExists)
    {
        if (string.IsNullOrWhiteSpace(fontId))
        {
            warnings.Add($"Empty font ID for {fieldName}; fell back to {fallback}.");
            return fallback;
        }

        if (fontId.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
        {
            string fileName = fontId.Substring("custom:".Length).Trim();
            bool exists = customFontExists != null
                ? customFontExists(fileName)
                : File.Exists(ProjectSettings.GlobalizePath($"user://custom_fonts/{fileName}"));
            if (!exists)
            {
                warnings.Add($"Custom font '{fileName}' for {fieldName} was not found; fell back to {fallback}.");
                return fallback;
            }
        }

        return fontId;
    }

    private static int ClampWithWarn(int value, int min, int max, string fieldName, List<string> warnings)
    {
        if (value < min || value > max)
        {
            int clamped = Math.Clamp(value, min, max);
            warnings.Add($"{fieldName} ({value}) was out of range [{min}, {max}]; clamped to {clamped}.");
            return clamped;
        }
        return value;
    }

    private static float ClampFloatWithWarn(float value, float min, float max, float fallback, string fieldName, List<string> warnings)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            warnings.Add($"{fieldName} was NaN/Infinity; fell back to {fallback:F2}.");
            return fallback;
        }
        if (value < min || value > max)
        {
            float clamped = Math.Clamp(value, min, max);
            warnings.Add($"{fieldName} ({value:F2}) was out of range [{min:F2}, {max:F2}]; clamped to {clamped:F2}.");
            return clamped;
        }
        return value;
    }

    public static bool TryParseColor(string hex, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        string s = hex.Trim();
        if (!s.StartsWith('#')) s = "#" + s;
        if (s.Length != 4 && s.Length != 5 && s.Length != 7 && s.Length != 9)
            return false;

        try
        {
            color = Color.FromHtml(s);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static Color ParseColorOrDefault(string hex, Color fallback)
    {
        return TryParseColor(hex, out var c) ? c : fallback;
    }

    public static string ToHexString(Color c)
    {
        return "#" + c.ToHtml(includeAlpha: c.A < 0.999f).ToUpperInvariant();
    }

    public static double ComputeRelativeLuminance(Color c)
    {
        static double Linearize(double ch) => ch <= 0.03928 ? ch / 12.92 : Math.Pow((ch + 0.055) / 1.055, 2.4);
        return 0.2126 * Linearize(c.R) + 0.7152 * Linearize(c.G) + 0.0722 * Linearize(c.B);
    }

    public static double ComputeContrastRatio(Color a, Color b)
    {
        double la = ComputeRelativeLuminance(a);
        double lb = ComputeRelativeLuminance(b);
        double lighter = Math.Max(la, lb);
        double darker = Math.Min(la, lb);
        return (lighter + 0.05) / (darker + 0.05);
    }
}
