using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DeadlockPlayground.Theming;

namespace DeadlockPlayground.Tests;

/// <summary>
/// Headless verification test for ThemeManager & ThemeProfile:
/// 1. Preset switching (Light, Default, Night) in lockstep on PlaygroundTheme & ModalDialogTheme
/// 2. 3-role font swapping
/// 3. JSON export/import round-tripping
/// 4. Corrupted / out-of-range / zero-contrast JSON sanitization & clamping
/// </summary>
public partial class ThemeSystemVerificationTest : SceneTree
{
    public override void _Initialize()
    {
        GD.Print("=================================================");
        GD.Print("     THEME SYSTEM VERIFICATION TEST (HEADLESS)   ");
        GD.Print("=================================================");

        bool passed = true;
        string tempDir = Path.Combine(Path.GetTempPath(), "dl_theme_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        string activeThemeOsPath = ProjectSettings.GlobalizePath("user://active_theme.json");
        string activeThemeBackup = File.Exists(activeThemeOsPath) ? File.ReadAllText(activeThemeOsPath) : null;
        string leakedThemeOsPath = ProjectSettings.GlobalizePath("user://themes/roundtrip_test_theme.dptheme.json");

        try
        {
            var manager = ThemeManager.Instance ?? new ThemeManager();
            if (manager.GetParent() == null)
            {
                Root.AddChild(manager);
            }

            var pgTheme = manager.GetPlaygroundTheme();
            var modalTheme = manager.GetModalTheme();
            if (pgTheme == null || modalTheme == null)
            {
                throw new Exception("Failed to load PlaygroundTheme.tres or ModalDialogTheme.tres.");
            }

            // 1. Test Preset Switching (Default, Night, Light)
            GD.Print("[Test 1] Testing Preset Switching (Default -> Night -> Light)...");
            manager.ApplyPreset(ThemeProfile.PresetDefault, saveAsActive: false);
            Color defaultText = pgTheme.GetColor("font_color", "Label");
            Color modalDefaultText = modalTheme.GetColor("font_color", "Label");
            Assert(defaultText == modalDefaultText, "Default preset Label font_color mismatch between PlaygroundTheme and ModalDialogTheme.");
            Assert(pgTheme.GetFontSize("font_size", "Label") == 16, $"Expected Default Body size 16, got {pgTheme.GetFontSize("font_size", "Label")}");
            Assert(pgTheme.GetFontSize("font_size", "SectionHeaderLabel") == 18, $"Expected Default Heading size 18, got {pgTheme.GetFontSize("font_size", "SectionHeaderLabel")}");
            Assert(pgTheme.GetFontSize("font_size", "NumericLabel") == 15, $"Expected Default Numeric size 15, got {pgTheme.GetFontSize("font_size", "NumericLabel")}");

            manager.ApplyPreset(ThemeProfile.PresetNight, saveAsActive: false);
            Color nightAccent = pgTheme.GetColor("font_color", "HeaderLabel");
            Color modalNightAccent = modalTheme.GetColor("font_color", "HeaderLabel");
            Assert(nightAccent == modalNightAccent, "Night preset HeaderLabel accent mismatch between themes.");
            Assert(nightAccent != defaultText, "Night preset accent should differ from Default text.");

            manager.ApplyPreset(ThemeProfile.PresetLight, saveAsActive: false);
            Color lightText = pgTheme.GetColor("font_color", "Label");
            Color modalLightText = modalTheme.GetColor("font_color", "Label");
            Assert(lightText == modalLightText, "Light preset Label font_color mismatch between themes.");
            Assert(ThemeProfile.ComputeRelativeLuminance(lightText) < 0.1, "Light theme text should be dark for legibility.");
            Assert(ThemeManager.IsLightTheme(), "IsLightTheme() should return true for Light preset.");
            GD.Print("[Test 1] PASSED");

            // 2. Test 3-Role Font Swapping
            GD.Print("[Test 2] Testing 3-Role Font Swapping...");
            var swapped = ThemeProfile.CreateDefaultPreset();
            swapped.Typography.HeadingFont = ThemeTypographyData.BuiltinSupersonic;
            swapped.Typography.BodyFont = ThemeTypographyData.BuiltinSans;
            swapped.Typography.NumericFont = ThemeTypographyData.BuiltinColus;
            manager.ApplyProfile(swapped, saveAsActive: false);

            Font hFont = pgTheme.GetFont("font", "HeaderLabel");
            Font bFont = pgTheme.GetFont("font", "Label");
            Font nFont = pgTheme.GetFont("font", "NumericLabel");
            Assert(hFont != null && bFont != null && nFont != null, "Swapped fonts should all resolve non-null.");
            Assert(hFont == modalTheme.GetFont("font", "HeaderLabel"), "Heading font mismatch in ModalDialogTheme.");
            Assert(bFont == modalTheme.GetFont("font", "Label"), "Body font mismatch in ModalDialogTheme.");
            Assert(nFont == modalTheme.GetFont("font", "NumericLabel"), "Numeric font mismatch in ModalDialogTheme.");
            Assert(bFont is SystemFont, "BuiltinSans should resolve to SystemFont.");
            GD.Print("[Test 2] PASSED");

            // 3. Test JSON Export / Import Round-Tripping
            GD.Print("[Test 3] Testing JSON Export / Import Round-Tripping...");
            var custom = ThemeProfile.CreateNightPreset();
            custom.Name = "RoundTrip Test Theme";
            custom.Palette.AccentPrimary = "#FF8844";
            custom.Typography.BodyFontSize = 19;
            custom.Typography.GlobalFontScaleOffset = 2;
            custom.Style.CornerRadius = 11;
            custom.Style.BorderWidth = 2;

            string exportPath = Path.Combine(tempDir, "roundtrip.dptheme.json");
            Assert(manager.ExportThemeToFile(exportPath, custom), "ExportThemeToFile failed.");
            Assert(File.Exists(exportPath), "Exported JSON file does not exist.");

            Assert(manager.ImportThemeFromFile(exportPath, out var imported, out var importWarnings), "ImportThemeFromFile failed.");
            Assert(importWarnings.Count == 0, $"Expected 0 warnings on clean roundtrip, got {importWarnings.Count}");
            Assert(imported.Name == "RoundTrip Test Theme", $"Expected Name 'RoundTrip Test Theme', got '{imported.Name}'");
            Assert(imported.Palette.AccentPrimary == "#FF8844", $"Expected AccentPrimary '#FF8844', got '{imported.Palette.AccentPrimary}'");
            Assert(imported.Typography.BodyFontSize == 19, $"Expected BodyFontSize 19, got {imported.Typography.BodyFontSize}");
            Assert(imported.Style.CornerRadius == 11, $"Expected CornerRadius 11, got {imported.Style.CornerRadius}");
            Assert(pgTheme.GetFontSize("font_size", "Label") == 21, $"Expected scaled Body size 19+2=21, got {pgTheme.GetFontSize("font_size", "Label")}");
            GD.Print("[Test 3] PASSED");

            // 4. Test Corrupted / Out-of-Range / Zero-Contrast JSON Sanitization & Clamping
            GD.Print("[Test 4] Testing External JSON Tamper Protection & Clamping...");
            var corrupted = new ThemeProfile
            {
                Name = "",
                Palette = new ThemePaletteData
                {
                    BackgroundPrimary = "#222222",
                    BackgroundSecondary = "#222222",
                    TextPrimary = "#222222", // Zero contrast!
                    AccentPrimary = "NOT_A_HEX_COLOR"
                },
                Typography = new ThemeTypographyData
                {
                    HeadingFont = "custom:missing_font_99999.ttf",
                    BodyFontSize = 3,          // Too small -> clamp to 12
                    HeadingFontSize = 99,      // Too large -> clamp to 28
                    NumericFontSize = -10,     // Too small -> clamp to 12
                    GlobalFontScaleOffset = 50,// Too large -> clamp to 6
                    FontOutlineStrength = 99   // Too large -> clamp to 4
                },
                Style = new ThemeStyleData
                {
                    CornerRadius = 99,         // Clamp to 16
                    BorderWidth = -4,          // Clamp to 0
                    BorderOpacity = float.NaN, // Fallback
                    ModalBackdropOpacity = 0.01f, // Clamp to 0.30
                    ControlSpacing = 99        // Clamp to 12
                }
            };

            var sanitized = ThemeProfile.SanitizeAndValidateProfile(corrupted, out var warnings);
            Assert(warnings.Count >= 8, $"Expected >= 8 sanitization warnings, got {warnings.Count}");
            Assert(sanitized.Typography.BodyFontSize == 12, $"BodyFontSize should clamp to 12, got {sanitized.Typography.BodyFontSize}");
            Assert(sanitized.Typography.HeadingFontSize == 28, $"HeadingFontSize should clamp to 28, got {sanitized.Typography.HeadingFontSize}");
            Assert(sanitized.Typography.NumericFontSize == 12, $"NumericFontSize should clamp to 12, got {sanitized.Typography.NumericFontSize}");
            Assert(sanitized.Typography.GlobalFontScaleOffset == 6, $"GlobalFontScaleOffset should clamp to 6, got {sanitized.Typography.GlobalFontScaleOffset}");
            Assert(sanitized.Typography.FontOutlineStrength == 4, $"FontOutlineStrength should clamp to 4, got {sanitized.Typography.FontOutlineStrength}");
            Assert(sanitized.Style.CornerRadius == 16, $"CornerRadius should clamp to 16, got {sanitized.Style.CornerRadius}");
            Assert(sanitized.Style.BorderWidth == 0, $"BorderWidth should clamp to 0, got {sanitized.Style.BorderWidth}");
            Assert(!float.IsNaN(sanitized.Style.BorderOpacity), "BorderOpacity should not be NaN.");
            Assert(Math.Abs(sanitized.Style.ModalBackdropOpacity - 0.30f) < 0.001f, $"ModalBackdropOpacity should clamp to 0.30, got {sanitized.Style.ModalBackdropOpacity}");
            Assert(sanitized.Style.ControlSpacing == 12, $"ControlSpacing should clamp to 12, got {sanitized.Style.ControlSpacing}");
            Assert(sanitized.Typography.HeadingFont == ThemeTypographyData.BuiltinColus, "Missing custom font should fall back to BuiltinColus.");

            // Verify contrast guard reverted zero-contrast colors
            var sText = ThemeProfile.ParseColorOrDefault(sanitized.Palette.TextPrimary, Colors.Black);
            var sBg = ThemeProfile.ParseColorOrDefault(sanitized.Palette.BackgroundPrimary, Colors.Black);
            Assert(ThemeProfile.ComputeContrastRatio(sText, sBg) >= 2.2, "Contrast guard failed to restore readable contrast.");

            // Apply sanitized profile to ensure zero crashes
            manager.ApplyProfile(sanitized, saveAsActive: false);
            GD.Print($"[Test 4] PASSED ({warnings.Count} warnings cleanly handled)");

            // Restore default at the end
            manager.ApplyPreset(ThemeProfile.PresetDefault, saveAsActive: false);

            // 5. Live UI SceneTree Performance & Wrapping Verification
            GD.Print("[Test 5] Testing Live SettingsDialog + FeedbackDialog Theme Switch Performance & Wrapping...");
            var settingsScene = GD.Load<PackedScene>("res://ui/scenes/modals/SettingsDialog.tscn");
            var feedbackScene = GD.Load<PackedScene>("res://ui/scenes/modals/FeedbackDialog.tscn");
            Assert(settingsScene != null && feedbackScene != null, "Failed to load SettingsDialog.tscn or FeedbackDialog.tscn.");

            var settingsNode = settingsScene.Instantiate<Control>();
            var feedbackNode = feedbackScene.Instantiate<Control>();
            Root.AddChild(settingsNode);
            Root.AddChild(feedbackNode);
            if (!settingsNode.IsNodeReady()) settingsNode._Ready();
            if (!feedbackNode.IsNodeReady()) feedbackNode._Ready();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            string[] cycle = { ThemeProfile.PresetNight, ThemeProfile.PresetLight, ThemeProfile.PresetDefault };
            for (int i = 0; i < 9; i++)
            {
                manager.ApplyPreset(cycle[i % cycle.Length], saveAsActive: false);
            }
            sw.Stop();
            GD.Print($"[Test 5] 9 consecutive live theme switches with SettingsDialog + FeedbackDialog took {sw.ElapsedMilliseconds} ms");
            Assert(sw.ElapsedMilliseconds < 1500, $"Live theme switching took too long: {sw.ElapsedMilliseconds} ms");

            var btnGithub = feedbackNode.FindChild("BtnGithub", true, false) as Button;
            var lblSub = feedbackNode.FindChild("LabelSub", true, false) as Label;
            Assert(btnGithub != null && btnGithub.AutowrapMode == TextServer.AutowrapMode.Off, "FeedbackDialog BtnGithub should keep AutowrapMode.Off so buttons stay 1-line and never collapse.");
            Assert(lblSub != null && lblSub.AutowrapMode != TextServer.AutowrapMode.Off, "FeedbackDialog LabelSub should have wrapping enabled.");
            Assert(feedbackNode.CustomMinimumSize.X <= 600f, $"FeedbackDialog CustomMinimumSize.X should be <= 600, got {feedbackNode.CustomMinimumSize.X}");

            var themesTab = settingsNode.FindChild("ThemesSettingsTab", true, false) as Control;
            var btnApplyTheme = themesTab?.FindChild("BtnApplyTheme", true, false) as Button;
            var btnSaveCustom = themesTab?.FindChild("BtnSaveCustom", true, false) as Button;
            var editorScroll = themesTab?.FindChild("EditorScroll", true, false) as ScrollContainer;
            Assert(btnApplyTheme != null, "ThemesSettingsTab BtnApplyTheme should exist.");
            Assert(btnSaveCustom != null && btnSaveCustom.AutowrapMode == TextServer.AutowrapMode.Off, "ThemesSettingsTab BtnSaveCustom should keep AutowrapMode.Off.");
            Assert(editorScroll != null && editorScroll.CustomMinimumSize.Y >= 320f, "ThemesSettingsTab EditorScroll should have >= 320px minimum height.");
            Assert(pgTheme.GetColor("font_color", "MenuButton") == Colors.White, "MenuButton (File/Adjustments/Filter/Fill) should have white text.");

            // Verify BonesTab CheckLines, PaintTab, CharacterTab, FloatingBrushPalette
            var bonesScene = GD.Load<PackedScene>("res://ui/scenes/tabs/BonesTab.tscn");
            var paintScene = GD.Load<PackedScene>("res://ui/scenes/tabs/PaintTab.tscn");
            var charScene = GD.Load<PackedScene>("res://ui/scenes/tabs/CharacterTab.tscn");
            var brushPaletteScene = GD.Load<PackedScene>("res://ui/scenes/FloatingBrushPalette.tscn");
            Assert(bonesScene != null && paintScene != null && charScene != null && brushPaletteScene != null, "Failed to load tab/palette scenes.");

            var bonesNode = bonesScene.Instantiate<Control>();
            var paintNode = paintScene.Instantiate<Control>();
            var charNode = charScene.Instantiate<Control>();
            var brushNode = brushPaletteScene.Instantiate<Control>();
            Root.AddChild(bonesNode);
            Root.AddChild(paintNode);
            Root.AddChild(charNode);
            Root.AddChild(brushNode);
            if (!brushNode.IsNodeReady()) brushNode._Ready();
            PlaygroundThemeHelper.AutoDecorate(bonesNode);

            var checkLines = bonesNode.FindChild("CheckLines", true, false) as CheckBox;
            Assert(checkLines != null && checkLines.AutowrapMode == TextServer.AutowrapMode.Off && checkLines.Text == "Wireframe Lines", "BonesTab CheckLines must keep AutowrapMode.Off so its label never disappears.");
            var btnMoveUp = paintNode.FindChild("BtnMoveUp", true, false) as Button;
            var btnDeleteLayer = paintNode.FindChild("BtnDeleteLayer", true, false) as Button;
            Assert(btnMoveUp != null && btnMoveUp.Icon != null && btnMoveUp.ThemeTypeVariation == "IconButton", "PaintTab BtnMoveUp should use arrow_up.svg icon with IconButton variation.");
            Assert(btnDeleteLayer != null && btnDeleteLayer.Text == "Del", $"PaintTab BtnDeleteLayer should be 'Del' so layer toolbar fits on 1 row, got '{btnDeleteLayer?.Text}'.");
            var btnImportCustom = charNode.FindChild("BtnImportCustomModel", true, false) as Button;
            Assert(btnImportCustom != null && btnImportCustom.AutowrapMode == TextServer.AutowrapMode.Word && !btnImportCustom.ClipText, "CharacterTab BtnImportCustomModel should wrap onto 2 lines without clipping.");
            var stripPanel = brushNode.FindChild("ToolStripPanel", true, false) as PanelContainer;
            Assert(stripPanel != null && stripPanel.ThemeTypeVariation == "CardPanel", "FloatingBrushPalette ToolStripPanel should use CardPanel theme variation.");
            var flyoutParamLbl = brushNode.FindChild("FlyoutPanel", true, false)?.FindChild("LblTitle", true, false) as Label;
            Assert(flyoutParamLbl != null && flyoutParamLbl.ThemeTypeVariation == "CompactLabel", $"FloatingBrushPalette parameter LblTitle should use CompactLabel variation, got '{flyoutParamLbl?.ThemeTypeVariation}'.");

            var settingsTabs = settingsNode.FindChild("TabContainer", true, false) as TabContainer;
            int themesTabIdx = settingsTabs?.GetTabIdxFromControl(settingsTabs.FindChild("Themes", false, false) as Control) ?? -1;
            Assert(settingsTabs != null && themesTabIdx >= 0 && settingsTabs.GetTabIcon(themesTabIdx) != null, "SettingsDialog Themes tab icon should be set and tinted.");

            manager.ApplyPreset(ThemeProfile.PresetLight, saveAsActive: false);
            var (cardNormal, _, _) = PlaygroundThemeHelper.CreateCardStateStyles();
            Assert(cardNormal.BgColor.Luminance > 0.45f, $"Light Mode card BgColor should be light/greyish (luminance > 0.45), got {cardNormal.BgColor} (lum={cardNormal.BgColor.Luminance:F2}).");

            // Verify Light Mode card badge pill contrast
            var testBadgePanel = new PanelContainer();
            var testBadgeLabel = new Label { Text = "STANDARD" };
            PlaygroundThemeHelper.ApplyCardBadgeStyle(testBadgePanel, testBadgeLabel, new Color(0.40f, 0.75f, 1.0f, 1.0f));
            var badgeBgStyle = testBadgePanel.GetThemeStylebox("panel") as StyleBoxFlat;
            Assert(badgeBgStyle != null && badgeBgStyle.BgColor.Luminance > 0.70f, "Light Mode card badge pill should have a bright background.");
            Assert(testBadgeLabel.GetThemeColor("font_color").Luminance < 0.45f, "Light Mode card badge text should be dark/saturated for contrast.");
            testBadgePanel.QueueFree();
            testBadgeLabel.QueueFree();

            // Verify ExportPanel, PaintModExportPanel, DopeSheetView, ClipAndPoseView theme bindings
            var exportPanelScene = GD.Load<PackedScene>("res://ui/scenes/ExportPanel.tscn");
            var paintExportScene = GD.Load<PackedScene>("res://ui/scenes/components/PaintModExportPanel.tscn");
            var dopeScene = GD.Load<PackedScene>("res://scenes/ui/animation/DopeSheetView.tscn");
            var clipScene = GD.Load<PackedScene>("res://scenes/ui/animation/ClipAndPoseView.tscn");
            var exportPanelNode = exportPanelScene.Instantiate<PanelContainer>();
            var paintExportNode = paintExportScene.Instantiate<PanelContainer>();
            var dopeNode = dopeScene.Instantiate<Control>();
            var clipNode = clipScene.Instantiate<Control>();
            Assert(exportPanelNode.ThemeTypeVariation == "CardPanel", "ExportPanel should use CardPanel.");
            Assert(paintExportNode.ThemeTypeVariation == "CardPanel", "PaintModExportPanel should use CardPanel.");
            var boneHeader = dopeNode.FindChild("BoneHeaderPanel", true, false) as PanelContainer;
            Assert(boneHeader != null && boneHeader.ThemeTypeVariation == "InspectorSectionPanel", "DopeSheet BoneHeaderPanel should use InspectorSectionPanel.");
            var quickSub = clipNode.FindChild("LblSubtitle", true, false) as Label;
            Assert(quickSub != null && quickSub.ThemeTypeVariation == "MutedLabel", "ClipAndPoseView QuickAssistants LblSubtitle should use MutedLabel.");
            exportPanelNode.QueueFree();
            paintExportNode.QueueFree();
            dopeNode.QueueFree();
            clipNode.QueueFree();

            manager.ApplyPreset(ThemeProfile.PresetDefault, saveAsActive: false);

            bonesNode.QueueFree();
            paintNode.QueueFree();
            charNode.QueueFree();
            brushNode.QueueFree();

            settingsNode.QueueFree();
            feedbackNode.QueueFree();
            GD.Print("[Test 5] PASSED");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ThemeSystemVerificationTest] FAILED: {ex.Message}\n{ex.StackTrace}");
            passed = false;
        }
        finally
        {
            try
            {
                if (activeThemeBackup != null)
                    File.WriteAllText(activeThemeOsPath, activeThemeBackup);
                else if (File.Exists(activeThemeOsPath))
                    File.Delete(activeThemeOsPath);

                if (File.Exists(leakedThemeOsPath))
                    File.Delete(leakedThemeOsPath);
            }
            catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }

        GD.Print("=================================================");
        GD.Print(passed ? "ALL THEME SYSTEM TESTS PASSED!" : "THEME SYSTEM TESTS FAILED!");
        GD.Print("=================================================");
        Quit(passed ? 0 : 1);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("Assertion failed: " + message);
    }
}

