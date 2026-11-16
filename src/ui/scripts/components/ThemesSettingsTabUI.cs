using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using DeadlockPlayground.Theming;

namespace DeadlockPlayground.UI;

/// <summary>
/// Live Theme Editor & Preset Selector tab inside SettingsDialog.
/// Allows switching between Default, Night, Light, and custom saved themes,
/// live editing of all semantic colors, 3-role typography + .ttf/.otf importing,
/// font scaling, and geometry tweaks with immediate real-time preview.
/// </summary>
public partial class ThemesSettingsTabUI : VBoxContainer
{
    private OptionButton _presetOption;
    private Button _btnApplyTheme;
    private Button _btnSaveCustom;
    private Button _btnExportTheme;
    private Button _btnImportTheme;
    private Button _btnResetTheme;
    private Label _statusLabel;
    private VBoxContainer _editorVBox;

    // Color pickers + hex inputs keyed by palette property name
    private readonly Dictionary<string, (ColorPickerButton Picker, LineEdit Hex)> _colorRows = new();

    // Font role OptionButtons
    private OptionButton _optHeadingFont;
    private OptionButton _optBodyFont;
    private OptionButton _optNumericFont;
    private string _pendingFontImportRole;

    // Font size controls
    private HSlider _sliderGlobalScale;
    private SpinBox _spinGlobalScale;
    private HSlider _sliderHeadingSize;
    private SpinBox _spinHeadingSize;
    private HSlider _sliderBodySize;
    private SpinBox _spinBodySize;
    private HSlider _sliderNumericSize;
    private SpinBox _spinNumericSize;
    private HSlider _sliderOutlineStrength;
    private SpinBox _spinOutlineStrength;

    // Geometry / Style controls
    private HSlider _sliderCornerRadius;
    private SpinBox _spinCornerRadius;
    private HSlider _sliderBorderWidth;
    private SpinBox _spinBorderWidth;
    private HSlider _sliderBorderOpacity;
    private Label _lblBorderOpacity;
    private HSlider _sliderBackdropOpacity;
    private Label _lblBackdropOpacity;
    private HSlider _sliderControlSpacing;
    private SpinBox _spinControlSpacing;

    // Dialogs
    private FileDialog _exportDialog;
    private FileDialog _importDialog;
    private FileDialog _importFontDialog;
    private ConfirmationDialog _saveNameDialog;
    private LineEdit _saveNameInput;

    // Preset option mapping
    private readonly List<(string Type, string Id, string Display)> _presetEntries = new();
    private readonly List<(string Id, string Display)> _fontEntries = new();

    private bool _isSyncing = false;
    private ThemeProfile _draftProfile;

    public override void _Ready()
    {
        _presetOption = GetNode<OptionButton>("%PresetOption");
        _btnApplyTheme = GetNodeOrNull<Button>("%BtnApplyTheme");
        _btnSaveCustom = GetNode<Button>("%BtnSaveCustom");
        _btnExportTheme = GetNode<Button>("%BtnExportTheme");
        _btnImportTheme = GetNode<Button>("%BtnImportTheme");
        _btnResetTheme = GetNode<Button>("%BtnResetTheme");
        _statusLabel = GetNode<Label>("%StatusLabel");
        _editorVBox = GetNode<VBoxContainer>("%EditorVBox");

        BuildEditorSections();
        SetupDialogs();
        ConnectEvents();
        RefreshPresetDropdown();
        RefreshFontDropdowns();
        SyncFromCurrentProfile();
    }

    private void BuildEditorSections()
    {
        // B-1: Color Palette Pickers
        var palettePanel = CreateSectionPanel("Color Palette", out var paletteContent);
        var colorGrid = new GridContainer
        {
            Columns = 2,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        colorGrid.AddThemeConstantOverride("h_separation", 16);
        colorGrid.AddThemeConstantOverride("v_separation", 8);
        paletteContent.AddChild(colorGrid);

        AddColorRow(colorGrid, nameof(ThemePaletteData.BackgroundPrimary), "Background Primary");
        AddColorRow(colorGrid, nameof(ThemePaletteData.BackgroundSecondary), "Background Secondary");
        AddColorRow(colorGrid, nameof(ThemePaletteData.BackgroundTertiary), "Input / Field Background");
        AddColorRow(colorGrid, nameof(ThemePaletteData.SurfaceHeader), "Header / Toolbar Surface");
        AddColorRow(colorGrid, nameof(ThemePaletteData.AccentPrimary), "Accent Primary");
        AddColorRow(colorGrid, nameof(ThemePaletteData.AccentSecondary), "Accent Secondary");
        AddColorRow(colorGrid, nameof(ThemePaletteData.TextPrimary), "Text Primary");
        AddColorRow(colorGrid, nameof(ThemePaletteData.TextSecondary), "Text Secondary / Muted");
        AddColorRow(colorGrid, nameof(ThemePaletteData.BorderColor), "Border Color");
        AddColorRow(colorGrid, nameof(ThemePaletteData.SemanticSuccess), "Semantic Success");
        AddColorRow(colorGrid, nameof(ThemePaletteData.SemanticWarning), "Semantic Warning");
        AddColorRow(colorGrid, nameof(ThemePaletteData.SemanticDanger), "Semantic Danger");
        _editorVBox.AddChild(palettePanel);

        // B-2: Typography & Font Role Assignment
        var typoPanel = CreateSectionPanel("Typography & Font Roles", out var typoContent);
        _optHeadingFont = AddFontRoleRow(typoContent, "Heading / Display Font", "heading");
        _optBodyFont = AddFontRoleRow(typoContent, "Body / UI Font", "body");
        _optNumericFont = AddFontRoleRow(typoContent, "Numeric / Parameter Font", "numeric");
        _editorVBox.AddChild(typoPanel);

        // B-3: Font Sizing & Safe Scaling
        var sizePanel = CreateSectionPanel("Font Sizing & Safe Scaling", out var sizeContent);
        (_sliderGlobalScale, _spinGlobalScale) = AddIntSliderSpinRow(sizeContent, "Global Font Scale Offset (px)", -2, 6, 0, val =>
        {
            UpdateActiveProfile(p => p.Typography.GlobalFontScaleOffset = val);
        });
        (_sliderHeadingSize, _spinHeadingSize) = AddIntSliderSpinRow(sizeContent, "Heading Base Size (px)", 14, 28, 18, val =>
        {
            UpdateActiveProfile(p => p.Typography.HeadingFontSize = val);
        });
        (_sliderBodySize, _spinBodySize) = AddIntSliderSpinRow(sizeContent, "Body Base Size (px)", 12, 22, 16, val =>
        {
            UpdateActiveProfile(p => p.Typography.BodyFontSize = val);
        });
        (_sliderNumericSize, _spinNumericSize) = AddIntSliderSpinRow(sizeContent, "Numeric Base Size (px)", 12, 22, 15, val =>
        {
            UpdateActiveProfile(p => p.Typography.NumericFontSize = val);
        });
        (_sliderOutlineStrength, _spinOutlineStrength) = AddIntSliderSpinRow(sizeContent, "Font Outline Strength (px)", 0, 4, 0, val =>
        {
            UpdateActiveProfile(p => p.Typography.FontOutlineStrength = val);
        });
        _editorVBox.AddChild(sizePanel);

        // B-4: Style & Geometry Tweaks
        var stylePanel = CreateSectionPanel("Style & Geometry", out var styleContent);
        (_sliderCornerRadius, _spinCornerRadius) = AddIntSliderSpinRow(styleContent, "Corner Radius (px)", 0, 16, 6, val =>
        {
            UpdateActiveProfile(p => p.Style.CornerRadius = val);
        });
        (_sliderBorderWidth, _spinBorderWidth) = AddIntSliderSpinRow(styleContent, "Border Thickness (px)", 0, 4, 1, val =>
        {
            UpdateActiveProfile(p => p.Style.BorderWidth = val);
        });
        (_sliderBorderOpacity, _lblBorderOpacity) = AddFloatSliderRow(styleContent, "Border Opacity", 0.0f, 1.0f, 0.05f, 0.85f,
            v => $"{Mathf.RoundToInt(v * 100)}%",
            val => UpdateActiveProfile(p => p.Style.BorderOpacity = val));
        (_sliderBackdropOpacity, _lblBackdropOpacity) = AddFloatSliderRow(styleContent, "Modal Backdrop Dimming", 0.30f, 0.95f, 0.05f, 0.72f,
            v => $"{Mathf.RoundToInt(v * 100)}%",
            val => UpdateActiveProfile(p => p.Style.ModalBackdropOpacity = val));
        (_sliderControlSpacing, _spinControlSpacing) = AddIntSliderSpinRow(styleContent, "Control Padding (Compact <-> Comfortable)", 4, 12, 6, val =>
        {
            UpdateActiveProfile(p => p.Style.ControlSpacing = val);
        });
        _editorVBox.AddChild(stylePanel);
    }

    private PanelContainer CreateSectionPanel(string title, out VBoxContainer content)
    {
        var panel = new PanelContainer
        {
            ThemeTypeVariation = "InspectorSectionPanel",
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        var vbox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vbox.AddThemeConstantOverride("separation", 8);

        var header = new Label
        {
            Text = title,
            ThemeTypeVariation = "SectionHeaderLabel"
        };
        vbox.AddChild(header);
        vbox.AddChild(new HSeparator());

        content = vbox;
        panel.AddChild(vbox);
        return panel;
    }

    private void AddColorRow(GridContainer grid, string propName, string labelText)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);

        var lbl = new Label
        {
            Text = labelText,
            ThemeTypeVariation = "BodyLabel",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText = true
        };
        var picker = new ColorPickerButton
        {
            CustomMinimumSize = new Vector2(44, 28),
            EditAlpha = true
        };
        var hex = new LineEdit
        {
            CustomMinimumSize = new Vector2(96, 28),
            ThemeTypeVariation = "NumericLineEdit",
            MaxLength = 9
        };

        picker.ColorChanged += col =>
        {
            if (_isSyncing) return;
            string hexStr = ThemeProfile.ToHexString(col);
            _isSyncing = true;
            hex.Text = hexStr;
            _isSyncing = false;
            SetPaletteProperty(propName, hexStr);
        };

        void CommitHex()
        {
            if (_isSyncing) return;
            if (ThemeProfile.TryParseColor(hex.Text, out var parsed))
            {
                string norm = ThemeProfile.ToHexString(parsed);
                _isSyncing = true;
                picker.Color = parsed;
                hex.Text = norm;
                _isSyncing = false;
                SetPaletteProperty(propName, norm);
            }
            else
            {
                // Revert invalid input
                _isSyncing = true;
                hex.Text = ThemeProfile.ToHexString(picker.Color);
                _isSyncing = false;
            }
        }

        hex.TextSubmitted += _ => CommitHex();
        hex.FocusExited += CommitHex;

        row.AddChild(lbl);
        row.AddChild(picker);
        row.AddChild(hex);
        grid.AddChild(row);
        _colorRows[propName] = (picker, hex);
    }

    private OptionButton AddFontRoleRow(VBoxContainer parent, string labelText, string roleKey)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);

        var lbl = new Label
        {
            Text = labelText,
            ThemeTypeVariation = "BodyLabel",
            CustomMinimumSize = new Vector2(210, 0)
        };
        var opt = new OptionButton
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 30),
            ClipText = true
        };
        var btnImport = new Button
        {
            Text = "+ Import Font...",
            ThemeTypeVariation = "ToolbarButton"
        };

        opt.ItemSelected += idx =>
        {
            if (_isSyncing) return;
            if (idx < 0 || idx >= _fontEntries.Count) return;
            string fontId = _fontEntries[(int)idx].Id;
            if (string.IsNullOrEmpty(fontId)) return; // separator
            UpdateActiveProfile(p =>
            {
                switch (roleKey)
                {
                    case "heading": p.Typography.HeadingFont = fontId; break;
                    case "body": p.Typography.BodyFont = fontId; break;
                    case "numeric": p.Typography.NumericFont = fontId; break;
                }
            });
        };

        btnImport.Pressed += () =>
        {
            _pendingFontImportRole = roleKey;
            _importFontDialog.PopupCentered();
        };

        row.AddChild(lbl);
        row.AddChild(opt);
        row.AddChild(btnImport);
        parent.AddChild(row);
        return opt;
    }

    private (HSlider Slider, SpinBox Spin) AddIntSliderSpinRow(VBoxContainer parent, string labelText, int min, int max, int defVal, Action<int> onChanged)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 10);

        var lbl = new Label
        {
            Text = labelText,
            ThemeTypeVariation = "BodyLabel",
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = 1,
            Value = defVal,
            CustomMinimumSize = new Vector2(150, 0),
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        var spin = new SpinBox
        {
            MinValue = min,
            MaxValue = max,
            Step = 1,
            Value = defVal,
            ThemeTypeVariation = "NumericSpinBox",
            CustomMinimumSize = new Vector2(76, 28)
        };

        slider.ValueChanged += v =>
        {
            if (_isSyncing) return;
            int iv = Mathf.RoundToInt(v);
            _isSyncing = true;
            spin.Value = iv;
            _isSyncing = false;
            onChanged(iv);
        };
        spin.ValueChanged += v =>
        {
            if (_isSyncing) return;
            int iv = Mathf.RoundToInt(v);
            _isSyncing = true;
            slider.Value = iv;
            _isSyncing = false;
            onChanged(iv);
        };

        row.AddChild(lbl);
        row.AddChild(slider);
        row.AddChild(spin);
        parent.AddChild(row);
        return (slider, spin);
    }

    private (HSlider Slider, Label ValueLabel) AddFloatSliderRow(VBoxContainer parent, string labelText, float min, float max, float step, float defVal, Func<float, string> fmt, Action<float> onChanged)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 10);

        var lbl = new Label
        {
            Text = labelText,
            ThemeTypeVariation = "BodyLabel",
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            Value = defVal,
            CustomMinimumSize = new Vector2(150, 0),
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        var valLbl = new Label
        {
            Text = fmt(defVal),
            ThemeTypeVariation = "NumericLabel",
            CustomMinimumSize = new Vector2(56, 0),
            HorizontalAlignment = HorizontalAlignment.Right
        };

        slider.ValueChanged += v =>
        {
            float fv = (float)v;
            valLbl.Text = fmt(fv);
            if (_isSyncing) return;
            onChanged(fv);
        };

        row.AddChild(lbl);
        row.AddChild(slider);
        row.AddChild(valLbl);
        parent.AddChild(row);
        return (slider, valLbl);
    }

    private void SetupDialogs()
    {
        _exportDialog = new FileDialog
        {
            Title = "Export Theme JSON",
            FileMode = FileDialog.FileModeEnum.SaveFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.dptheme.json, *.json ; Deadlock Playground Theme" },
            Size = new Vector2I(680, 480)
        };
        _exportDialog.FileSelected += path =>
        {
            if (ThemeManager.Instance != null && ThemeManager.Instance.ExportThemeToFile(path))
            {
                ShowStatus($"Exported theme to {path}", isWarning: false);
            }
            else
            {
                ShowStatus("Failed to export theme file.", isWarning: true);
            }
        };
        AddChild(_exportDialog);

        _importDialog = new FileDialog
        {
            Title = "Import Theme JSON",
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.json, *.dptheme.json ; Deadlock Playground Theme" },
            Size = new Vector2I(680, 480)
        };
        _importDialog.FileSelected += path =>
        {
            _ = ApplyWithLoadingOverlayAsync("Importing & applying theme...", () =>
            {
                if (ThemeManager.Instance != null && ThemeManager.Instance.ImportThemeFromFile(path, out var imported, out var warnings))
                {
                    RefreshPresetDropdown();
                    SyncFromCurrentProfile();
                    if (warnings.Count > 0)
                        ShowStatus($"Imported '{imported.Name}' with {warnings.Count} sanitized field(s).", isWarning: true);
                    else
                        ShowStatus($"Imported and applied '{imported.Name}'.", isWarning: false);
                }
                else
                {
                    ShowStatus("Failed to import theme JSON.", isWarning: true);
                }
            });
        };
        AddChild(_importDialog);

        _importFontDialog = new FileDialog
        {
            Title = "Import Custom Font (.ttf / .otf / .woff2)",
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.ttf, *.otf, *.woff2 ; Font Files" },
            Size = new Vector2I(680, 480)
        };
        _importFontDialog.FileSelected += path =>
        {
            if (ThemeManager.Instance == null) return;
            string fontId = ThemeManager.Instance.ImportCustomFont(path, out string err);
            if (string.IsNullOrEmpty(fontId))
            {
                ShowStatus(err ?? "Failed to import font.", isWarning: true);
                return;
            }

            RefreshFontDropdowns();
            UpdateActiveProfile(p =>
            {
                switch (_pendingFontImportRole)
                {
                    case "heading": p.Typography.HeadingFont = fontId; break;
                    case "body": p.Typography.BodyFont = fontId; break;
                    case "numeric": p.Typography.NumericFont = fontId; break;
                }
            });
            SyncFromCurrentProfile();
            ShowStatus($"Imported font '{fontId.Substring("custom:".Length)}'.", isWarning: false);
        };
        AddChild(_importFontDialog);

        _saveNameDialog = new ConfirmationDialog
        {
            Title = "Save Custom Theme",
            OkButtonText = "Save",
            CancelButtonText = "Cancel"
        };
        var nameVBox = new VBoxContainer();
        nameVBox.AddThemeConstantOverride("separation", 6);
        nameVBox.AddChild(new Label { Text = "Enter a name for this custom theme:", ThemeTypeVariation = "BodyLabel" });
        _saveNameInput = new LineEdit { PlaceholderText = "My Custom Theme", CustomMinimumSize = new Vector2(300, 32) };
        nameVBox.AddChild(_saveNameInput);
        _saveNameDialog.AddChild(nameVBox);
        _saveNameDialog.Confirmed += () =>
        {
            string name = string.IsNullOrWhiteSpace(_saveNameInput.Text) ? "Custom Theme" : _saveNameInput.Text.Trim();
            ThemeManager.Instance?.SaveCustomTheme(name, _draftProfile);
            RefreshPresetDropdown();
            SyncFromCurrentProfile();
            ShowStatus($"Saved custom theme '{name}'.", isWarning: false);
        };
        AddChild(_saveNameDialog);
    }

    private void ConnectEvents()
    {
        _presetOption.ItemSelected += idx =>
        {
            if (_isSyncing) return;
            int i = (int)idx;
            if (i < 0 || i >= _presetEntries.Count) return;
            var entry = _presetEntries[i];
            if (entry.Type == "preset")
            {
                _ = ApplyWithLoadingOverlayAsync($"Applying '{entry.Display}' theme...", () =>
                {
                    ThemeManager.Instance?.ApplyPreset(entry.Id);
                    SyncFromCurrentProfile();
                    ShowStatus($"Applied preset '{entry.Display}'.", isWarning: false);
                });
            }
            else if (entry.Type == "custom")
            {
                _ = ApplyWithLoadingOverlayAsync($"Loading '{entry.Display}' theme...", () =>
                {
                    if (ThemeManager.Instance != null && ThemeManager.Instance.LoadCustomThemeFile(entry.Id, out var warnings))
                    {
                        SyncFromCurrentProfile();
                        ShowStatus(warnings.Count > 0 ? $"Loaded '{entry.Display}' ({warnings.Count} sanitized)." : $"Loaded '{entry.Display}'.", warnings.Count > 0);
                    }
                });
            }
        };

        if (_btnApplyTheme != null)
        {
            _btnApplyTheme.Pressed += () =>
            {
                if (_draftProfile == null) return;
                _ = ApplyWithLoadingOverlayAsync("Applying theme changes...", () =>
                {
                    ThemeManager.Instance?.ApplyProfile(_draftProfile, saveAsActive: true);
                    SyncFromCurrentProfile();
                    ShowStatus("Applied theme changes.", isWarning: false);
                });
            };
        }

        _btnSaveCustom.Pressed += () =>
        {
            _saveNameInput.Text = ThemeManager.CurrentProfile.Name == "Default (Deadlock Studio)"
                ? "My Deadlock Theme"
                : ThemeManager.CurrentProfile.Name;
            _saveNameDialog.PopupCentered();
            _saveNameInput.GrabFocus();
            _saveNameInput.SelectAll();
        };

        _btnExportTheme.Pressed += () =>
        {
            _exportDialog.CurrentFile = "my_theme.dptheme.json";
            _exportDialog.PopupCentered();
        };

        _btnImportTheme.Pressed += () => _importDialog.PopupCentered();

        _btnResetTheme.Pressed += () =>
        {
            _ = ApplyWithLoadingOverlayAsync("Resetting theme to defaults...", () =>
            {
                string presetId = ThemeManager.CurrentProfile.PresetId;
                if (presetId != ThemeProfile.PresetNight && presetId != ThemeProfile.PresetLight)
                    presetId = ThemeProfile.PresetDefault;
                ThemeManager.Instance?.ApplyPreset(presetId);
                RefreshPresetDropdown();
                SyncFromCurrentProfile();
                ShowStatus("Reset theme to preset defaults.", isWarning: false);
            });
        };
    }

    private async System.Threading.Tasks.Task ApplyWithLoadingOverlayAsync(string message, Action applyAction)
    {
        var tree = GetTree();
        var uiMgr = tree?.Root?.FindChild("UIRoot", true, false) as StudioUIManager
                 ?? tree?.Root?.FindChild("StudioUIManager", true, false) as StudioUIManager;

        uiMgr?.ShowLoading(true, message);
        if (tree != null)
        {
            await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }

        try
        {
            applyAction?.Invoke();
        }
        finally
        {
            if (tree != null)
            {
                await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
            uiMgr?.ShowLoading(false);
        }
    }

    private void RefreshPresetDropdown()
    {
        _isSyncing = true;
        _presetOption.Clear();
        _presetEntries.Clear();

        void AddEntry(string type, string id, string label)
        {
            _presetEntries.Add((type, id, label));
            _presetOption.AddItem(label);
        }

        AddEntry("preset", ThemeProfile.PresetDefault, "Default (Deadlock Studio)");
        AddEntry("preset", ThemeProfile.PresetNight, "Night (OLED Deep Dark)");
        AddEntry("preset", ThemeProfile.PresetLight, "Light (Daylight Cream / Parchment)");

        var customThemes = ThemeManager.Instance?.ListSavedCustomThemes() ?? new();
        if (customThemes.Count > 0)
        {
            _presetOption.AddSeparator("Saved Custom Themes");
            _presetEntries.Add(("sep", "", ""));
            foreach (var ct in customThemes)
            {
                AddEntry("custom", ct.FullPath, ct.DisplayName);
            }
        }
        _isSyncing = false;
    }

    private void RefreshFontDropdowns()
    {
        _isSyncing = true;
        _fontEntries.Clear();

        _fontEntries.Add((ThemeTypographyData.BuiltinColus, "Colus (Built-in)"));
        _fontEntries.Add((ThemeTypographyData.BuiltinDahlia, "Dahlia-Bold (Built-in)"));
        _fontEntries.Add((ThemeTypographyData.BuiltinSupersonic, "Supersonic Rocketship (Built-in)"));
        _fontEntries.Add((ThemeTypographyData.BuiltinSans, "Clean Sans-Serif (System)"));

        var customFonts = ThemeManager.Instance?.GetAvailableCustomFontIds() ?? new();
        if (customFonts.Count > 0)
        {
            _fontEntries.Add(("", "--- Imported Custom Fonts ---"));
            foreach (var cf in customFonts)
            {
                _fontEntries.Add((cf, cf.Substring("custom:".Length) + " (Imported)"));
            }
        }

        _fontEntries.Add(("", "--- System Fonts ---"));
        var sysFonts = OS.GetSystemFonts()
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Take(80);
        foreach (var sf in sysFonts)
        {
            _fontEntries.Add(("system:" + sf, sf));
        }

        foreach (var opt in new[] { _optHeadingFont, _optBodyFont, _optNumericFont })
        {
            if (opt == null) continue;
            opt.Clear();
            foreach (var entry in _fontEntries)
            {
                if (string.IsNullOrEmpty(entry.Id))
                    opt.AddSeparator(entry.Display);
                else
                    opt.AddItem(entry.Display);
            }
        }
        _isSyncing = false;
    }

    private void SyncFromCurrentProfile()
    {
        var p = ThemeManager.CurrentProfile;
        if (p == null) return;
        _draftProfile = p.Clone();

        _isSyncing = true;
        try
        {
            // Preset dropdown selection
            int presetIdx = _presetEntries.FindIndex(e =>
                (e.Type == "preset" && e.Id == p.PresetId) ||
                (e.Type == "custom" && e.Display.Equals(p.Name, StringComparison.OrdinalIgnoreCase)));
            if (presetIdx >= 0) _presetOption.Selected = presetIdx;

            // Colors
            SyncColor(nameof(ThemePaletteData.BackgroundPrimary), p.Palette.BackgroundPrimary);
            SyncColor(nameof(ThemePaletteData.BackgroundSecondary), p.Palette.BackgroundSecondary);
            SyncColor(nameof(ThemePaletteData.BackgroundTertiary), p.Palette.BackgroundTertiary);
            SyncColor(nameof(ThemePaletteData.SurfaceHeader), p.Palette.SurfaceHeader);
            SyncColor(nameof(ThemePaletteData.AccentPrimary), p.Palette.AccentPrimary);
            SyncColor(nameof(ThemePaletteData.AccentSecondary), p.Palette.AccentSecondary);
            SyncColor(nameof(ThemePaletteData.TextPrimary), p.Palette.TextPrimary);
            SyncColor(nameof(ThemePaletteData.TextSecondary), p.Palette.TextSecondary);
            SyncColor(nameof(ThemePaletteData.BorderColor), p.Palette.BorderColor);
            SyncColor(nameof(ThemePaletteData.SemanticSuccess), p.Palette.SemanticSuccess);
            SyncColor(nameof(ThemePaletteData.SemanticWarning), p.Palette.SemanticWarning);
            SyncColor(nameof(ThemePaletteData.SemanticDanger), p.Palette.SemanticDanger);

            // Fonts
            SelectFontInOption(_optHeadingFont, p.Typography.HeadingFont);
            SelectFontInOption(_optBodyFont, p.Typography.BodyFont);
            SelectFontInOption(_optNumericFont, p.Typography.NumericFont);

            // Sizes
            _sliderGlobalScale.Value = p.Typography.GlobalFontScaleOffset;
            _spinGlobalScale.Value = p.Typography.GlobalFontScaleOffset;
            _sliderHeadingSize.Value = p.Typography.HeadingFontSize;
            _spinHeadingSize.Value = p.Typography.HeadingFontSize;
            _sliderBodySize.Value = p.Typography.BodyFontSize;
            _spinBodySize.Value = p.Typography.BodyFontSize;
            _sliderNumericSize.Value = p.Typography.NumericFontSize;
            _spinNumericSize.Value = p.Typography.NumericFontSize;
            _sliderOutlineStrength.Value = p.Typography.FontOutlineStrength;
            _spinOutlineStrength.Value = p.Typography.FontOutlineStrength;

            // Style
            _sliderCornerRadius.Value = p.Style.CornerRadius;
            _spinCornerRadius.Value = p.Style.CornerRadius;
            _sliderBorderWidth.Value = p.Style.BorderWidth;
            _spinBorderWidth.Value = p.Style.BorderWidth;
            _sliderBorderOpacity.Value = p.Style.BorderOpacity;
            _lblBorderOpacity.Text = $"{Mathf.RoundToInt(p.Style.BorderOpacity * 100)}%";
            _sliderBackdropOpacity.Value = p.Style.ModalBackdropOpacity;
            _lblBackdropOpacity.Text = $"{Mathf.RoundToInt(p.Style.ModalBackdropOpacity * 100)}%";
            _sliderControlSpacing.Value = p.Style.ControlSpacing;
            _spinControlSpacing.Value = p.Style.ControlSpacing;
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void SyncColor(string propName, string hex)
    {
        if (!_colorRows.TryGetValue(propName, out var row)) return;
        row.Picker.Color = ThemeProfile.ParseColorOrDefault(hex, Colors.White);
        row.Hex.Text = hex;
    }

    private void SelectFontInOption(OptionButton opt, string fontId)
    {
        if (opt == null) return;
        int idx = _fontEntries.FindIndex(f => f.Id.Equals(fontId, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) opt.Selected = idx;
    }

    private void SetPaletteProperty(string propName, string hexValue)
    {
        UpdateActiveProfile(p =>
        {
            switch (propName)
            {
                case nameof(ThemePaletteData.BackgroundPrimary): p.Palette.BackgroundPrimary = hexValue; break;
                case nameof(ThemePaletteData.BackgroundSecondary): p.Palette.BackgroundSecondary = hexValue; break;
                case nameof(ThemePaletteData.BackgroundTertiary): p.Palette.BackgroundTertiary = hexValue; break;
                case nameof(ThemePaletteData.SurfaceHeader): p.Palette.SurfaceHeader = hexValue; break;
                case nameof(ThemePaletteData.AccentPrimary): p.Palette.AccentPrimary = hexValue; break;
                case nameof(ThemePaletteData.AccentSecondary): p.Palette.AccentSecondary = hexValue; break;
                case nameof(ThemePaletteData.TextPrimary): p.Palette.TextPrimary = hexValue; break;
                case nameof(ThemePaletteData.TextSecondary): p.Palette.TextSecondary = hexValue; break;
                case nameof(ThemePaletteData.BorderColor): p.Palette.BorderColor = hexValue; break;
                case nameof(ThemePaletteData.SemanticSuccess): p.Palette.SemanticSuccess = hexValue; break;
                case nameof(ThemePaletteData.SemanticWarning): p.Palette.SemanticWarning = hexValue; break;
                case nameof(ThemePaletteData.SemanticDanger): p.Palette.SemanticDanger = hexValue; break;
            }
        });
    }

    private void UpdateActiveProfile(Action<ThemeProfile> mutator)
    {
        _draftProfile ??= ThemeManager.CurrentProfile.Clone();
        mutator(_draftProfile);
        ShowStatus("Pending changes — click 'Apply Theme' to apply.", isWarning: true);
    }

    private void ShowStatus(string message, bool isWarning)
    {
        if (_statusLabel == null) return;
        _statusLabel.Text = message;
        _statusLabel.ThemeTypeVariation = isWarning ? "BadgeLabelWarning" : "BadgeLabelSuccess";
        _statusLabel.Visible = true;
    }
}
