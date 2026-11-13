using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DeadlockPlayground.Theming;

/// <summary>
/// Central Theme Engine & Autoload service managing PlaygroundTheme.tres and ModalDialogTheme.tres
/// in lockstep at runtime. Supports 3 built-in presets, 3-role swappable typography, custom font
/// importing (.ttf/.otf/.woff2), live color/geometry editing, and tamper-safe JSON persistence.
/// </summary>
public partial class ThemeManager : Node
{
    public const string PlaygroundThemePath = "res://assets/themes/PlaygroundTheme.tres";
    public const string ModalDialogThemePath = "res://assets/themes/ModalDialogTheme.tres";

    public const string ActiveThemeUserPath = "user://active_theme.json";
    public const string CustomThemesDir = "user://themes";
    public const string CustomFontsDir = "user://custom_fonts";

    public static ThemeManager Instance { get; private set; }

    public static event Action<ThemeProfile> ThemeChanged;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static ThemeProfile _currentProfile = ThemeProfile.CreateDefaultPreset();
    public static ThemeProfile CurrentProfile => _currentProfile;

    private Godot.Theme _playgroundTheme;
    private Godot.Theme _modalTheme;

    private readonly Dictionary<string, Font> _fontCache = new(StringComparer.OrdinalIgnoreCase);

    public override void _EnterTree()
    {
        Instance = this;
        EnsureUserDirectories();
        LoadStartupTheme();
    }

    public override void _Ready()
    {
        // Re-apply on _Ready in case themes weren't loaded yet or scene tree just initialized
        ApplyProfile(_currentProfile, saveAsActive: false);
    }

    private static void EnsureUserDirectories()
    {
        try
        {
            Directory.CreateDirectory(ProjectSettings.GlobalizePath(CustomThemesDir));
            Directory.CreateDirectory(ProjectSettings.GlobalizePath(CustomFontsDir));
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[ThemeManager] Failed to create user theme directories: {ex.Message}");
        }
    }

    private void LoadStartupTheme()
    {
        string activePath = ProjectSettings.GlobalizePath(ActiveThemeUserPath);
        if (File.Exists(activePath))
        {
            try
            {
                string json = File.ReadAllText(activePath);
                var loaded = JsonSerializer.Deserialize<ThemeProfile>(json, JsonOptions);
                var sanitized = ThemeProfile.SanitizeAndValidateProfile(loaded, out var warnings);
                foreach (var w in warnings)
                {
                    GD.PushWarning($"[ThemeManager] Startup theme sanitization: {w}");
                }
                _currentProfile = sanitized;
                ApplyProfile(_currentProfile, saveAsActive: false);
                return;
            }
            catch (Exception ex)
            {
                GD.PushWarning($"[ThemeManager] Failed to load active theme from '{activePath}': {ex.Message}. Falling back to Default.");
            }
        }

        _currentProfile = ThemeProfile.CreateDefaultPreset();
        ApplyProfile(_currentProfile, saveAsActive: false);
    }

    public void EnsureThemesLoaded()
    {
        if (_playgroundTheme == null && ResourceLoader.Exists(PlaygroundThemePath))
        {
            _playgroundTheme = GD.Load<Godot.Theme>(PlaygroundThemePath);
        }
        if (_modalTheme == null && ResourceLoader.Exists(ModalDialogThemePath))
        {
            _modalTheme = GD.Load<Godot.Theme>(ModalDialogThemePath);
        }
    }

    public Godot.Theme GetPlaygroundTheme()
    {
        EnsureThemesLoaded();
        return _playgroundTheme;
    }

    public Godot.Theme GetModalTheme()
    {
        EnsureThemesLoaded();
        return _modalTheme;
    }

    /// <summary>
    /// Applies a ThemeProfile to both PlaygroundTheme.tres and ModalDialogTheme.tres in real time.
    /// </summary>
    public void ApplyProfile(ThemeProfile profile, bool saveAsActive = true)
    {
        var sanitized = ThemeProfile.SanitizeAndValidateProfile(profile, out var warnings);
        foreach (var w in warnings)
        {
            GD.PushWarning($"[ThemeManager] {w}");
        }

        _currentProfile = sanitized.Clone();
        EnsureThemesLoaded();

        if (_playgroundTheme != null)
        {
            _playgroundTheme.SetBlockSignals(true);
            try
            {
                ApplyProfileToGodotTheme(_playgroundTheme, _currentProfile, isModalTheme: false);
            }
            finally
            {
                _playgroundTheme.SetBlockSignals(false);
            }
            _playgroundTheme.EmitChanged();
        }
        if (_modalTheme != null)
        {
            _modalTheme.SetBlockSignals(true);
            try
            {
                ApplyProfileToGodotTheme(_modalTheme, _currentProfile, isModalTheme: true);
            }
            finally
            {
                _modalTheme.SetBlockSignals(false);
            }
            _modalTheme.EmitChanged();
        }

        if (saveAsActive)
        {
            SaveActiveTheme();
        }

        ThemeChanged?.Invoke(_currentProfile);
    }

    public void ApplyPreset(string presetId, bool saveAsActive = true)
    {
        var preset = ThemeProfile.GetPreset(presetId);
        ApplyProfile(preset, saveAsActive);
    }

    public void SaveActiveTheme()
    {
        try
        {
            EnsureUserDirectories();
            string path = ProjectSettings.GlobalizePath(ActiveThemeUserPath);
            string json = JsonSerializer.Serialize(_currentProfile, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[ThemeManager] Failed to save active theme: {ex.Message}");
        }
    }

    public string SaveCustomTheme(string themeName, ThemeProfile profile = null)
    {
        EnsureUserDirectories();
        var toSave = (profile ?? _currentProfile).Clone();
        toSave.Name = string.IsNullOrWhiteSpace(themeName) ? "Custom Theme" : themeName.Trim();
        toSave.PresetId = ThemeProfile.PresetCustom;

        string safeFile = SanitizeFileName(toSave.Name) + ".dptheme.json";
        string fullPath = Path.Combine(ProjectSettings.GlobalizePath(CustomThemesDir), safeFile);
        string json = JsonSerializer.Serialize(toSave, JsonOptions);
        File.WriteAllText(fullPath, json);

        ApplyProfile(toSave, saveAsActive: true);
        return fullPath;
    }

    public List<(string FileName, string DisplayName, string FullPath)> ListSavedCustomThemes()
    {
        var list = new List<(string, string, string)>();
        try
        {
            EnsureUserDirectories();
            string dir = ProjectSettings.GlobalizePath(CustomThemesDir);
            if (!Directory.Exists(dir)) return list;

            foreach (string file in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
            {
                try
                {
                    string json = File.ReadAllText(file);
                    var p = JsonSerializer.Deserialize<ThemeProfile>(json, JsonOptions);
                    string name = !string.IsNullOrWhiteSpace(p?.Name) ? p.Name : Path.GetFileNameWithoutExtension(file);
                    list.Add((Path.GetFileName(file), name, file));
                }
                catch
                {
                    // Skip unreadable file
                }
            }
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[ThemeManager] Error listing custom themes: {ex.Message}");
        }
        return list;
    }

    public bool LoadCustomThemeFile(string fullPath, out List<string> warnings)
    {
        warnings = new List<string>();
        try
        {
            if (!File.Exists(fullPath))
            {
                warnings.Add($"Theme file not found: {fullPath}");
                return false;
            }
            string json = File.ReadAllText(fullPath);
            var loaded = JsonSerializer.Deserialize<ThemeProfile>(json, JsonOptions);
            var sanitized = ThemeProfile.SanitizeAndValidateProfile(loaded, out warnings);
            ApplyProfile(sanitized, saveAsActive: true);
            return true;
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to parse theme JSON: {ex.Message}");
            return false;
        }
    }

    public bool ExportThemeToFile(string targetPath, ThemeProfile profile = null)
    {
        try
        {
            var p = ThemeProfile.SanitizeAndValidateProfile(profile ?? _currentProfile, out _);
            string json = JsonSerializer.Serialize(p, JsonOptions);
            File.WriteAllText(targetPath, json);
            return true;
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[ThemeManager] ExportThemeToFile failed: {ex.Message}");
            return false;
        }
    }

    public bool ImportThemeFromFile(string sourcePath, out ThemeProfile imported, out List<string> warnings)
    {
        imported = null;
        warnings = new List<string>();
        try
        {
            string json = File.ReadAllText(sourcePath);
            var raw = JsonSerializer.Deserialize<ThemeProfile>(json, JsonOptions);
            imported = ThemeProfile.SanitizeAndValidateProfile(raw, out warnings);
            if (string.IsNullOrWhiteSpace(imported.Name) || imported.Name == "Default (Deadlock Studio)")
            {
                imported.Name = Path.GetFileNameWithoutExtension(sourcePath);
            }
            imported.PresetId = ThemeProfile.PresetCustom;
            SaveCustomTheme(imported.Name, imported);
            return true;
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to import theme: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Imports an external .ttf / .otf / .woff2 font into user://custom_fonts/ and returns its font ID.
    /// </summary>
    public string ImportCustomFont(string sourcePath, out string error)
    {
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                error = "Selected font file does not exist.";
                return null;
            }

            string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (ext != ".ttf" && ext != ".otf" && ext != ".woff2")
            {
                error = "Unsupported font format. Please select a .ttf, .otf, or .woff2 file.";
                return null;
            }

            EnsureUserDirectories();
            string fileName = Path.GetFileName(sourcePath);
            string destPath = Path.Combine(ProjectSettings.GlobalizePath(CustomFontsDir), fileName);
            File.Copy(sourcePath, destPath, overwrite: true);

            // Verify loadable
            var fontFile = new FontFile();
            var loadErr = fontFile.LoadDynamicFont(destPath);
            if (loadErr != Error.Ok)
            {
                error = $"Godot failed to load font '{fileName}' (Error: {loadErr}).";
                return null;
            }

            string fontId = "custom:" + fileName;
            _fontCache[fontId] = fontFile;
            return fontId;
        }
        catch (Exception ex)
        {
            error = $"Failed to import font: {ex.Message}";
            return null;
        }
    }

    public List<string> GetAvailableCustomFontIds()
    {
        var ids = new List<string>();
        try
        {
            EnsureUserDirectories();
            string dir = ProjectSettings.GlobalizePath(CustomFontsDir);
            if (!Directory.Exists(dir)) return ids;

            foreach (string f in Directory.GetFiles(dir).OrderBy(x => x))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".ttf" || ext == ".otf" || ext == ".woff2")
                {
                    ids.Add("custom:" + Path.GetFileName(f));
                }
            }
        }
        catch { }
        return ids;
    }

    public Font ResolveFont(string fontId)
    {
        if (string.IsNullOrWhiteSpace(fontId))
            fontId = ThemeTypographyData.BuiltinDahlia;

        if (_fontCache.TryGetValue(fontId, out var cached) && cached != null)
            return cached;

        Font resolved = null;

        if (fontId.Equals(ThemeTypographyData.BuiltinColus, StringComparison.OrdinalIgnoreCase))
        {
            resolved = ResourceLoader.Exists("res://assets/fonts/Colus-Regular.ttf")
                ? GD.Load<FontFile>("res://assets/fonts/Colus-Regular.ttf")
                : null;
        }
        else if (fontId.Equals(ThemeTypographyData.BuiltinDahlia, StringComparison.OrdinalIgnoreCase))
        {
            resolved = ResourceLoader.Exists("res://assets/fonts/dahlia-bold.otf")
                ? GD.Load<FontFile>("res://assets/fonts/dahlia-bold.otf")
                : null;
        }
        else if (fontId.Equals(ThemeTypographyData.BuiltinSupersonic, StringComparison.OrdinalIgnoreCase))
        {
            resolved = ResourceLoader.Exists("res://assets/fonts/Supersonic Rocketship.ttf")
                ? GD.Load<FontFile>("res://assets/fonts/Supersonic Rocketship.ttf")
                : null;
        }
        else if (fontId.Equals(ThemeTypographyData.BuiltinSans, StringComparison.OrdinalIgnoreCase))
        {
            resolved = new SystemFont
            {
                FontNames = new[] { "Segoe UI", "Inter", "Roboto", "Liberation Sans", "Arial", "sans-serif" },
                Antialiasing = TextServer.FontAntialiasing.Gray
            };
        }
        else if (fontId.StartsWith("system:", StringComparison.OrdinalIgnoreCase))
        {
            string sysName = fontId.Substring("system:".Length).Trim();
            resolved = new SystemFont
            {
                FontNames = new[] { sysName },
                Antialiasing = TextServer.FontAntialiasing.Gray
            };
        }
        else if (fontId.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
        {
            string fileName = fontId.Substring("custom:".Length).Trim();
            string fullPath = Path.Combine(ProjectSettings.GlobalizePath(CustomFontsDir), fileName);
            if (File.Exists(fullPath))
            {
                var ff = new FontFile();
                if (ff.LoadDynamicFont(fullPath) == Error.Ok)
                {
                    resolved = ff;
                }
            }
        }

        resolved ??= ThemeDB.FallbackFont;
        _fontCache[fontId] = resolved;
        return resolved;
    }

    public static Color GetColor(string tokenName)
    {
        var p = _currentProfile.Palette;
        return tokenName switch
        {
            nameof(ThemePaletteData.BackgroundPrimary) => ThemeProfile.ParseColorOrDefault(p.BackgroundPrimary, new Color(0.09f, 0.086f, 0.088f, 0.96f)),
            nameof(ThemePaletteData.BackgroundSecondary) => ThemeProfile.ParseColorOrDefault(p.BackgroundSecondary, new Color(0.133f, 0.125f, 0.129f, 0.96f)),
            nameof(ThemePaletteData.BackgroundTertiary) => ThemeProfile.ParseColorOrDefault(p.BackgroundTertiary, new Color(0.08f, 0.075f, 0.08f, 0.98f)),
            nameof(ThemePaletteData.SurfaceHeader) => ThemeProfile.ParseColorOrDefault(p.SurfaceHeader, new Color(0.184f, 0.267f, 0.259f, 0.98f)),
            nameof(ThemePaletteData.AccentPrimary) => ThemeProfile.ParseColorOrDefault(p.AccentPrimary, new Color(0.937f, 0.871f, 0.749f, 1f)),
            nameof(ThemePaletteData.AccentSecondary) => ThemeProfile.ParseColorOrDefault(p.AccentSecondary, new Color(0.447f, 0.58f, 0.498f, 1f)),
            nameof(ThemePaletteData.TextPrimary) => ThemeProfile.ParseColorOrDefault(p.TextPrimary, new Color(0.92f, 0.94f, 0.92f, 1f)),
            nameof(ThemePaletteData.TextSecondary) => ThemeProfile.ParseColorOrDefault(p.TextSecondary, new Color(0.72f, 0.78f, 0.75f, 1f)),
            nameof(ThemePaletteData.BorderColor) => ThemeProfile.ParseColorOrDefault(p.BorderColor, new Color(0.247f, 0.365f, 0.302f, 1f)),
            nameof(ThemePaletteData.SemanticSuccess) => ThemeProfile.ParseColorOrDefault(p.SemanticSuccess, new Color(0.32f, 0.72f, 0.53f, 1f)),
            nameof(ThemePaletteData.SemanticWarning) => ThemeProfile.ParseColorOrDefault(p.SemanticWarning, new Color(0.91f, 0.77f, 0.42f, 1f)),
            nameof(ThemePaletteData.SemanticDanger) => ThemeProfile.ParseColorOrDefault(p.SemanticDanger, new Color(0.91f, 0.44f, 0.32f, 1f)),
            _ => Colors.White
        };
    }

    public static Color GetModalBackdropColor()
    {
        float opacity = _currentProfile?.Style?.ModalBackdropOpacity ?? 0.72f;
        var bg = GetColor(nameof(ThemePaletteData.BackgroundPrimary));
        bool isLight = ThemeProfile.ComputeRelativeLuminance(bg) > 0.45;
        return isLight
            ? new Color(0.12f, 0.11f, 0.10f, opacity)
            : new Color(0.02f, 0.02f, 0.03f, opacity);
    }

    public static bool IsLightTheme()
    {
        var bg = GetColor(nameof(ThemePaletteData.BackgroundPrimary));
        return ThemeProfile.ComputeRelativeLuminance(bg) > 0.45;
    }

    private void ApplyProfileToGodotTheme(Godot.Theme theme, ThemeProfile profile, bool isModalTheme)
    {
        var p = profile.Palette;
        var t = profile.Typography;
        var s = profile.Style;

        Color bgPrimary = ThemeProfile.ParseColorOrDefault(p.BackgroundPrimary, new Color(0.09f, 0.086f, 0.088f, 0.96f));
        Color bgSecondary = ThemeProfile.ParseColorOrDefault(p.BackgroundSecondary, new Color(0.133f, 0.125f, 0.129f, 0.96f));
        Color bgTertiary = ThemeProfile.ParseColorOrDefault(p.BackgroundTertiary, new Color(0.08f, 0.075f, 0.08f, 0.98f));
        Color surfaceHeader = ThemeProfile.ParseColorOrDefault(p.SurfaceHeader, new Color(0.184f, 0.267f, 0.259f, 0.98f));
        Color accentPrimary = ThemeProfile.ParseColorOrDefault(p.AccentPrimary, new Color(0.937f, 0.871f, 0.749f, 1f));
        Color accentSecondary = ThemeProfile.ParseColorOrDefault(p.AccentSecondary, new Color(0.447f, 0.58f, 0.498f, 1f));
        Color textPrimary = ThemeProfile.ParseColorOrDefault(p.TextPrimary, new Color(0.92f, 0.94f, 0.92f, 1f));
        Color textSecondary = ThemeProfile.ParseColorOrDefault(p.TextSecondary, new Color(0.72f, 0.78f, 0.75f, 1f));
        Color borderBase = ThemeProfile.ParseColorOrDefault(p.BorderColor, new Color(0.247f, 0.365f, 0.302f, 1f));
        Color semSuccess = ThemeProfile.ParseColorOrDefault(p.SemanticSuccess, new Color(0.32f, 0.72f, 0.53f, 1f));
        Color semWarning = ThemeProfile.ParseColorOrDefault(p.SemanticWarning, new Color(0.91f, 0.77f, 0.42f, 1f));
        Color semDanger = ThemeProfile.ParseColorOrDefault(p.SemanticDanger, new Color(0.91f, 0.44f, 0.32f, 1f));

        Color borderCol = new Color(borderBase.R, borderBase.G, borderBase.B, s.BorderOpacity);
        Color borderHover = new Color(accentSecondary.R, accentSecondary.G, accentSecondary.B, Math.Min(1f, s.BorderOpacity + 0.15f));
        Color borderFocus = new Color(accentPrimary.R, accentPrimary.G, accentPrimary.B, 0.95f);
        Color textMutedDisabled = new Color(textSecondary.R, textSecondary.G, textSecondary.B, 0.48f);

        bool isLightBg = ThemeProfile.ComputeRelativeLuminance(bgPrimary) > 0.45;
        Color outlineColor = isLightBg ? new Color(1f, 1f, 1f, 0.85f) : new Color(0f, 0f, 0f, 0.80f);
        Color shadowColor = isLightBg ? new Color(0f, 0f, 0f, 0.10f) : new Color(0f, 0f, 0f, 0.45f);
        Color iconDisabledCol = new Color(textSecondary.R, textSecondary.G, textSecondary.B, 0.65f);

        // Contrast-aware text on AccentPrimary fill
        Color onAccentPrimary = ThemeProfile.ComputeRelativeLuminance(accentPrimary) > 0.45
            ? new Color(0.10f, 0.10f, 0.11f, 1f)
            : new Color(0.97f, 0.97f, 0.98f, 1f);
        Color onAccentSecondary = ThemeProfile.ComputeRelativeLuminance(accentSecondary) > 0.45
            ? new Color(0.10f, 0.10f, 0.11f, 1f)
            : new Color(0.97f, 0.97f, 0.98f, 1f);
        Color onDanger = ThemeProfile.ComputeRelativeLuminance(semDanger) > 0.45
            ? new Color(0.10f, 0.10f, 0.11f, 1f)
            : new Color(0.98f, 0.98f, 0.98f, 1f);

        Font headingFont = ResolveFont(t.HeadingFont);
        Font bodyFont = ResolveFont(t.BodyFont);
        Font numericFont = ResolveFont(t.NumericFont);

        int headingSize = Math.Clamp(t.HeadingFontSize + t.GlobalFontScaleOffset, 14, 34);
        int bodySize = Math.Clamp(t.BodyFontSize + t.GlobalFontScaleOffset, 12, 28);
        int numericSize = Math.Clamp(t.NumericFontSize + t.GlobalFontScaleOffset, 12, 28);
        int titleSize = Math.Clamp(headingSize + 3, 18, 36);
        int smallHeaderSize = Math.Clamp(headingSize - 1, 14, 30);
        int badgeSize = Math.Clamp(bodySize - 1, 13, 24);

        int r = s.CornerRadius;
        int bw = s.BorderWidth;
        int padV = s.ControlSpacing + 1;
        int padH = s.ControlSpacing + 8;

        theme.DefaultFont = bodyFont;
        theme.DefaultFontSize = bodySize;

        // Helper for StyleBoxFlat
        StyleBoxFlat MakeBox(Color bg, Color border, int radius, int borderW, int ml, int mt, int mr, int mb)
        {
            return new StyleBoxFlat
            {
                BgColor = bg,
                BorderColor = border,
                BorderWidthLeft = borderW,
                BorderWidthTop = borderW,
                BorderWidthRight = borderW,
                BorderWidthBottom = borderW,
                CornerRadiusTopLeft = radius,
                CornerRadiusTopRight = radius,
                CornerRadiusBottomRight = radius,
                CornerRadiusBottomLeft = radius,
                ContentMarginLeft = ml,
                ContentMarginTop = mt,
                ContentMarginRight = mr,
                ContentMarginBottom = mb
            };
        }

        var emptyBox = new StyleBoxEmpty();

        // --- Standard Panels ---
        var panelBase = MakeBox(bgSecondary, borderCol, Math.Min(16, r + 2), bw, 10, 10, 10, 10);
        var modalPanel = MakeBox(bgPrimary, borderCol, Math.Min(16, r + 2), bw, 16, 14, 16, 16);
        modalPanel.ShadowColor = new Color(0, 0, 0, isLightBg ? 0.25f : 0.55f);
        modalPanel.ShadowSize = 12;

        var cardPanel = MakeBox(bgSecondary, borderCol, r, bw, 10, 10, 10, 10);
        var inspectorPanel = MakeBox(bgPrimary, new Color(borderCol.R, borderCol.G, borderCol.B, borderCol.A * 0.8f), r, bw, 10, 8, 10, 8);
        var subPanel = MakeBox(bgTertiary, new Color(borderCol.R, borderCol.G, borderCol.B, borderCol.A * 0.65f), r, bw, 8, 8, 8, 8);
        var headerPanel = MakeBox(surfaceHeader, borderCol, r, bw, 12, 8, 12, 8);

        theme.SetStylebox("panel", "Panel", isModalTheme ? modalPanel : panelBase);
        theme.SetStylebox("panel", "PanelContainer", isModalTheme ? modalPanel : panelBase);
        theme.SetStylebox("panel", "ScrollContainer", emptyBox);

        theme.SetTypeVariation("CardPanel", "PanelContainer");
        theme.SetStylebox("panel", "CardPanel", cardPanel);

        theme.SetTypeVariation("InspectorSectionPanel", "PanelContainer");
        theme.SetStylebox("panel", "InspectorSectionPanel", inspectorPanel);

        theme.SetTypeVariation("SubPanelContainer", "PanelContainer");
        theme.SetStylebox("panel", "SubPanelContainer", subPanel);

        theme.SetTypeVariation("ModalHeaderPanel", "PanelContainer");
        theme.SetStylebox("panel", "ModalHeaderPanel", headerPanel);

        theme.SetTypeVariation("ModalPanel", "PanelContainer");
        theme.SetStylebox("panel", "ModalPanel", modalPanel);

        // --- Buttons ---
        Color btnNormalBg = isLightBg ? bgSecondary.Darkened(0.04f) : surfaceHeader.Darkened(0.15f);
        Color btnHoverBg = isLightBg ? surfaceHeader.Darkened(0.06f) : surfaceHeader.Lightened(0.08f);
        Color btnPressedBg = isLightBg ? surfaceHeader.Darkened(0.14f) : bgPrimary;
        Color btnDisabledBg = new Color(bgSecondary.R, bgSecondary.G, bgSecondary.B, 0.45f);

        var btnNormal = MakeBox(btnNormalBg, borderCol, r, bw, padH, padV, padH, padV);
        var btnHover = MakeBox(btnHoverBg, borderHover, r, bw, padH, padV, padH, padV);
        var btnPressed = MakeBox(btnPressedBg, borderFocus, r, bw, padH, padV, padH, padV);
        var btnDisabled = MakeBox(btnDisabledBg, new Color(borderCol.R, borderCol.G, borderCol.B, 0.3f), r, bw, padH, padV, padH, padV);
        var btnFocus = MakeBox(Colors.Transparent, borderFocus, r, Math.Max(1, bw), 0, 0, 0, 0);
        btnFocus.DrawCenter = false;

        void ApplyButtonStyles(string typeName, Font font, int size, Color fg, Color fgHover, Color fgPressed, Color fgDisabled,
            StyleBoxFlat sNormal, StyleBoxFlat sHover, StyleBoxFlat sPressed)
        {
            theme.SetFont("font", typeName, font);
            theme.SetFontSize("font_size", typeName, size);
            theme.SetColor("font_color", typeName, fg);
            theme.SetColor("font_hover_color", typeName, fgHover);
            theme.SetColor("font_focus_color", typeName, fgHover);
            theme.SetColor("font_pressed_color", typeName, fgPressed);
            theme.SetColor("font_disabled_color", typeName, fgDisabled);
            theme.SetColor("icon_normal_color", typeName, fg);
            theme.SetColor("icon_hover_color", typeName, fgHover);
            theme.SetColor("icon_focus_color", typeName, fgHover);
            theme.SetColor("icon_pressed_color", typeName, fgPressed);
            theme.SetColor("icon_disabled_color", typeName, iconDisabledCol);
            theme.SetConstant("outline_size", typeName, t.FontOutlineStrength);
            theme.SetColor("font_outline_color", typeName, outlineColor);
            theme.SetStylebox("normal", typeName, sNormal);
            theme.SetStylebox("hover", typeName, sHover);
            theme.SetStylebox("pressed", typeName, sPressed);
            theme.SetStylebox("hover_pressed", typeName, sPressed);
            theme.SetStylebox("disabled", typeName, btnDisabled);
            theme.SetStylebox("focus", typeName, btnFocus);
        }

        ApplyButtonStyles("Button", headingFont, bodySize, textPrimary, accentPrimary, accentPrimary, textMutedDisabled, btnNormal, btnHover, btnPressed);
        ApplyButtonStyles("OptionButton", headingFont, badgeSize, textPrimary, accentPrimary, accentPrimary, textMutedDisabled, btnNormal, btnHover, btnPressed);

        theme.SetTypeVariation("SecondaryButton", "Button");
        ApplyButtonStyles("SecondaryButton", bodyFont, bodySize, textPrimary, accentPrimary, accentPrimary, textMutedDisabled, btnNormal, btnHover, btnPressed);

        // Compact square IconButton (4px margins so 28-32px icon buttons don't shrink SVG icons)
        var iconNormal = MakeBox(btnNormalBg, borderCol, r, bw, 4, 4, 4, 4);
        var iconHover = MakeBox(btnHoverBg, borderHover, r, bw, 4, 4, 4, 4);
        var iconPressed = MakeBox(btnPressedBg, borderFocus, r, bw, 4, 4, 4, 4);
        theme.SetTypeVariation("IconButton", "Button");
        ApplyButtonStyles("IconButton", bodyFont, badgeSize, textPrimary, accentPrimary, accentPrimary, textMutedDisabled, iconNormal, iconHover, iconPressed);

        // Primary / Accent Button
        var accNormal = MakeBox(accentSecondary, borderFocus, r, bw, padH + 2, padV + 1, padH + 2, padV + 1);
        var accHover = MakeBox(accentPrimary, borderFocus, r, bw, padH + 2, padV + 1, padH + 2, padV + 1);
        var accPressed = MakeBox(accentSecondary.Darkened(0.2f), borderFocus, r, bw, padH + 2, padV + 1, padH + 2, padV + 1);

        theme.SetTypeVariation("AccentButton", "Button");
        ApplyButtonStyles("AccentButton", headingFont, bodySize, onAccentSecondary, onAccentPrimary, onAccentSecondary, textMutedDisabled, accNormal, accHover, accPressed);

        theme.SetTypeVariation("PrimaryActionButton", "Button");
        ApplyButtonStyles("PrimaryActionButton", headingFont, bodySize, onAccentSecondary, onAccentPrimary, onAccentSecondary, textMutedDisabled, accNormal, accHover, accPressed);

        // Top-bar MenuButtons (File, Adjustments, Filter, Fill) - crisp white text on saturated accent surface
        ApplyButtonStyles("MenuButton", headingFont, badgeSize, Colors.White, Colors.White, Colors.White, textMutedDisabled, accNormal, accHover, accPressed);

        // Danger Button
        var dangerNormal = MakeBox(new Color(semDanger.R, semDanger.G, semDanger.B, 0.85f), semDanger, r, bw, padH, padV, padH, padV);
        var dangerHover = MakeBox(semDanger.Lightened(0.1f), Colors.White, r, bw, padH, padV, padH, padV);
        var dangerPressed = MakeBox(semDanger.Darkened(0.2f), semDanger, r, bw, padH, padV, padH, padV);
        theme.SetTypeVariation("DangerButton", "Button");
        ApplyButtonStyles("DangerButton", headingFont, bodySize, onDanger, onDanger, onDanger, textMutedDisabled, dangerNormal, dangerHover, dangerPressed);

        // Toolbar & FilterPill & Tab Buttons
        var tbNormal = MakeBox(bgSecondary, borderCol, r, bw, Math.Max(6, padH - 4), Math.Max(4, padV - 2), Math.Max(6, padH - 4), Math.Max(4, padV - 2));
        var tbHover = MakeBox(btnHoverBg, borderHover, r, bw, Math.Max(6, padH - 4), Math.Max(4, padV - 2), Math.Max(6, padH - 4), Math.Max(4, padV - 2));
        var tbPressed = MakeBox(surfaceHeader, borderFocus, r, bw, Math.Max(6, padH - 4), Math.Max(4, padV - 2), Math.Max(6, padH - 4), Math.Max(4, padV - 2));

        theme.SetTypeVariation("ToolbarButton", "Button");
        ApplyButtonStyles("ToolbarButton", bodyFont, badgeSize, textPrimary, accentPrimary, accentPrimary, textMutedDisabled, tbNormal, tbHover, tbPressed);

        var pillRadius = Math.Min(16, r + 6);
        var pillNormal = MakeBox(bgSecondary, borderCol, pillRadius, bw, 10, 4, 10, 4);
        var pillHover = MakeBox(btnHoverBg, borderHover, pillRadius, bw, 10, 4, 10, 4);
        var pillPressed = MakeBox(accentSecondary, borderFocus, pillRadius, bw, 10, 4, 10, 4);
        theme.SetTypeVariation("FilterPillButton", "Button");
        ApplyButtonStyles("FilterPillButton", bodyFont, badgeSize, textPrimary, accentPrimary, onAccentSecondary, textMutedDisabled, pillNormal, pillHover, pillPressed);

        var tabBtnNormal = MakeBox(bgPrimary, new Color(borderCol.R, borderCol.G, borderCol.B, 0.5f), r, bw, 8, 5, 8, 5);
        var tabBtnHover = MakeBox(btnHoverBg, borderHover, r, bw, 8, 5, 8, 5);
        var tabBtnPressed = MakeBox(surfaceHeader, borderFocus, r, bw, 8, 5, 8, 5);
        tabBtnPressed.BorderWidthBottom = Math.Max(2, bw + 1);
        theme.SetTypeVariation("TabButton", "Button");
        ApplyButtonStyles("TabButton", headingFont, badgeSize, textSecondary, textPrimary, accentPrimary, textMutedDisabled, tabBtnNormal, tabBtnHover, tabBtnPressed);

        // --- CheckBox & CheckButton ---
        foreach (string chk in new[] { "CheckBox", "CheckButton" })
        {
            theme.SetFont("font", chk, bodyFont);
            theme.SetFontSize("font_size", chk, bodySize);
            theme.SetColor("font_color", chk, textPrimary);
            theme.SetColor("font_hover_color", chk, accentPrimary);
            theme.SetColor("font_hover_pressed_color", chk, accentPrimary);
            theme.SetColor("font_pressed_color", chk, accentPrimary);
            theme.SetColor("font_focus_color", chk, accentPrimary);
            theme.SetColor("font_disabled_color", chk, textMutedDisabled);
            theme.SetConstant("h_separation", chk, 8);
            theme.SetStylebox("normal", chk, emptyBox);
            theme.SetStylebox("hover", chk, emptyBox);
            theme.SetStylebox("pressed", chk, emptyBox);
            theme.SetStylebox("hover_pressed", chk, emptyBox);
            theme.SetStylebox("disabled", chk, emptyBox);
            theme.SetStylebox("focus", chk, emptyBox);
        }

        // --- Labels & Typography Variations ---
        void ApplyLabelVariation(string typeName, string baseType, Font font, int size, Color color)
        {
            if (!string.IsNullOrEmpty(baseType))
                theme.SetTypeVariation(typeName, baseType);
            theme.SetFont("font", typeName, font);
            theme.SetFontSize("font_size", typeName, size);
            theme.SetColor("font_color", typeName, color);
            theme.SetColor("font_shadow_color", typeName, shadowColor);
            theme.SetConstant("shadow_offset_x", typeName, 1);
            theme.SetConstant("shadow_offset_y", typeName, 1);
            theme.SetConstant("outline_size", typeName, t.FontOutlineStrength);
            theme.SetColor("font_outline_color", typeName, outlineColor);
        }

        ApplyLabelVariation("Label", null, bodyFont, bodySize, textPrimary);
        ApplyLabelVariation("BodyLabel", "Label", bodyFont, bodySize, textPrimary);
        ApplyLabelVariation("CompactLabel", "Label", bodyFont, Math.Clamp(bodySize - 2, 12, 20), textPrimary);
        ApplyLabelVariation("MutedLabel", "Label", bodyFont, badgeSize, textSecondary);
        ApplyLabelVariation("HeaderLabel", "Label", headingFont, smallHeaderSize, accentPrimary);
        ApplyLabelVariation("SubheaderLabel", "Label", headingFont, smallHeaderSize, accentPrimary);
        ApplyLabelVariation("SectionHeaderLabel", "Label", headingFont, headingSize, accentPrimary);
        ApplyLabelVariation("TitleLabel", "Label", headingFont, titleSize, accentPrimary);
        ApplyLabelVariation("ModalTitleLabel", "Label", headingFont, titleSize, accentPrimary);
        ApplyLabelVariation("HeaderLarge", "Label", headingFont, titleSize, accentPrimary);
        ApplyLabelVariation("HeaderMedium", "Label", headingFont, headingSize, accentPrimary);
        ApplyLabelVariation("HeaderSmall", "Label", headingFont, smallHeaderSize, accentPrimary);
        ApplyLabelVariation("NumericLabel", "Label", numericFont, numericSize, accentPrimary);
        ApplyLabelVariation("BadgeLabelSuccess", "Label", headingFont, badgeSize, semSuccess);
        ApplyLabelVariation("BadgeLabelWarning", "Label", headingFont, badgeSize, semWarning);
        ApplyLabelVariation("BadgeLabelInfo", "Label", headingFont, badgeSize, accentSecondary);
        ApplyLabelVariation("BadgeLabelDanger", "Label", headingFont, badgeSize, semDanger);

        // RichTextLabel
        theme.SetFont("normal_font", "RichTextLabel", bodyFont);
        theme.SetFont("bold_font", "RichTextLabel", headingFont);
        theme.SetFontSize("normal_font_size", "RichTextLabel", bodySize);
        theme.SetFontSize("bold_font_size", "RichTextLabel", bodySize);
        theme.SetColor("default_color", "RichTextLabel", textPrimary);

        // --- Inputs: LineEdit, TextEdit, SpinBox, NumericSpinBox, NumericLineEdit ---
        var inputNormal = MakeBox(bgTertiary, borderCol, r, bw, 10, padV, 10, padV);
        var inputFocus = MakeBox(bgTertiary, borderFocus, r, bw, 10, padV, 10, padV);
        var inputReadOnly = MakeBox(new Color(bgTertiary.R, bgTertiary.G, bgTertiary.B, 0.65f), new Color(borderCol.R, borderCol.G, borderCol.B, 0.35f), r, bw, 10, padV, 10, padV);

        void ApplyInputStyles(string typeName, Font font, int size)
        {
            theme.SetFont("font", typeName, font);
            theme.SetFontSize("font_size", typeName, size);
            theme.SetColor("font_color", typeName, textPrimary);
            theme.SetColor("font_uneditable_color", typeName, textSecondary);
            theme.SetColor("font_placeholder_color", typeName, textMutedDisabled);
            theme.SetColor("font_selected_color", typeName, onAccentSecondary);
            theme.SetColor("selection_color", typeName, new Color(accentSecondary.R, accentSecondary.G, accentSecondary.B, 0.45f));
            theme.SetColor("caret_color", typeName, accentPrimary);
            theme.SetColor("clear_button_color", typeName, textSecondary);
            theme.SetStylebox("normal", typeName, inputNormal);
            theme.SetStylebox("focus", typeName, inputFocus);
            theme.SetStylebox("read_only", typeName, inputReadOnly);
        }

        ApplyInputStyles("LineEdit", bodyFont, bodySize);
        ApplyInputStyles("TextEdit", bodyFont, bodySize);
        theme.SetTypeVariation("NumericLineEdit", "LineEdit");
        ApplyInputStyles("NumericLineEdit", numericFont, numericSize);
        theme.SetTypeVariation("NumericSpinBox", "SpinBox");

        // --- TabContainer & TabBar ---
        var tcUnselected = MakeBox(bgPrimary, borderCol, r, 0, 12, 6, 12, 6);
        tcUnselected.CornerRadiusBottomLeft = 0;
        tcUnselected.CornerRadiusBottomRight = 0;
        var tcHovered = MakeBox(btnHoverBg, borderHover, r, 0, 12, 6, 12, 6);
        tcHovered.CornerRadiusBottomLeft = 0;
        tcHovered.CornerRadiusBottomRight = 0;
        var tcSelected = MakeBox(surfaceHeader, accentPrimary, r, 0, 14, 8, 14, 8);
        tcSelected.CornerRadiusBottomLeft = 0;
        tcSelected.CornerRadiusBottomRight = 0;
        tcSelected.BorderWidthBottom = 2;

        foreach (string tc in new[] { "TabContainer", "TabBar" })
        {
            theme.SetFont("font", tc, headingFont);
            theme.SetFontSize("font_size", tc, badgeSize);
            theme.SetColor("font_selected_color", tc, accentPrimary);
            theme.SetColor("font_hovered_color", tc, textPrimary);
            theme.SetColor("font_unselected_color", tc, textSecondary);
            theme.SetColor("font_disabled_color", tc, textMutedDisabled);
            theme.SetStylebox("panel", tc, cardPanel);
            theme.SetStylebox("tab_selected", tc, tcSelected);
            theme.SetStylebox("tab_hovered", tc, tcHovered);
            theme.SetStylebox("tab_unselected", tc, tcUnselected);
            theme.SetStylebox("tab_disabled", tc, tcUnselected);
            theme.SetStylebox("tab_focus", tc, emptyBox);
        }

        // --- PopupMenu ---
        var popupPanel = MakeBox(bgSecondary, borderHover, r, bw, 6, 6, 6, 6);
        var popupHover = MakeBox(surfaceHeader, borderHover, Math.Max(2, r - 2), bw, 8, 4, 8, 4);
        var hSep = new StyleBoxLine { Color = new Color(accentSecondary.R, accentSecondary.G, accentSecondary.B, 0.25f) };
        var vSep = new StyleBoxLine { Color = new Color(accentSecondary.R, accentSecondary.G, accentSecondary.B, 0.25f), Vertical = true };

        theme.SetFont("font", "PopupMenu", bodyFont);
        theme.SetFontSize("font_size", "PopupMenu", bodySize);
        theme.SetColor("font_color", "PopupMenu", textPrimary);
        theme.SetColor("font_hover_color", "PopupMenu", accentPrimary);
        theme.SetColor("font_disabled_color", "PopupMenu", textMutedDisabled);
        theme.SetColor("font_separator_color", "PopupMenu", accentSecondary);
        theme.SetStylebox("panel", "PopupMenu", popupPanel);
        theme.SetStylebox("hover", "PopupMenu", popupHover);
        theme.SetStylebox("separator", "PopupMenu", hSep);

        // --- Separators ---
        theme.SetConstant("separation", "HSeparator", 8);
        theme.SetStylebox("separator", "HSeparator", hSep);
        theme.SetConstant("separation", "VSeparator", 8);
        theme.SetStylebox("separator", "VSeparator", vSep);

        // --- Sliders & Scrollbars ---
        var grabberArea = MakeBox(accentSecondary, Colors.Transparent, 3, 0, 0, 3, 0, 3);
        var grabberAreaHl = MakeBox(accentPrimary, Colors.Transparent, 3, 0, 0, 3, 0, 3);
        var sliderGroove = MakeBox(bgTertiary, borderCol, 3, 1, 0, 3, 0, 3);

        theme.SetStylebox("grabber_area", "HSlider", grabberArea);
        theme.SetStylebox("grabber_area_highlight", "HSlider", grabberAreaHl);
        theme.SetStylebox("slider", "HSlider", sliderGroove);

        var sbGrabber = MakeBox(new Color(borderBase.R, borderBase.G, borderBase.B, 0.75f), Colors.Transparent, 3, 0, 0, 0, 0, 0);
        var sbGrabberHl = MakeBox(accentSecondary, Colors.Transparent, 3, 0, 0, 0, 0, 0);
        var sbGrabberPr = MakeBox(accentPrimary, Colors.Transparent, 3, 0, 0, 0, 0, 0);

        foreach (string sb in new[] { "HScrollBar", "VScrollBar" })
        {
            theme.SetStylebox("grabber", sb, sbGrabber);
            theme.SetStylebox("grabber_highlight", sb, sbGrabberHl);
            theme.SetStylebox("grabber_pressed", sb, sbGrabberPr);
            theme.SetStylebox("scroll", sb, emptyBox);
            theme.SetStylebox("scroll_focus", sb, emptyBox);
        }

        // --- Tree & ItemList ---
        foreach (string listType in new[] { "Tree", "ItemList" })
        {
            theme.SetFont("font", listType, headingFont);
            theme.SetFontSize("font_size", listType, bodySize);
            theme.SetColor("font_color", listType, textPrimary);
            theme.SetColor("font_selected_color", listType, onAccentSecondary);
            theme.SetConstant("outline_size", listType, t.FontOutlineStrength);
            theme.SetColor("font_outline_color", listType, outlineColor);
            theme.SetStylebox("panel", listType, subPanel);
        }
        foreach (string chkType in new[] { "CheckBox", "CheckButton" })
        {
            theme.SetConstant("outline_size", chkType, t.FontOutlineStrength);
            theme.SetColor("font_outline_color", chkType, outlineColor);
        }
        theme.SetFont("title_button_font", "Tree", headingFont);
        theme.SetFontSize("title_button_font_size", "Tree", smallHeaderSize);

        // --- Tooltips ---
        var tooltipBox = MakeBox(bgTertiary, borderHover, r, bw, 8, 5, 8, 5);
        theme.SetFont("font", "TooltipLabel", bodyFont);
        theme.SetFontSize("font_size", "TooltipLabel", badgeSize);
        theme.SetColor("font_color", "TooltipLabel", accentPrimary);
        theme.SetStylebox("panel", "TooltipPanel", tooltipBox);
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new string(name.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "custom_theme" : cleaned.ToLowerInvariant();
    }
}
