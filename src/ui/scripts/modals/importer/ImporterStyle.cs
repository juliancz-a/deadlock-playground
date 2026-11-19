using Godot;
using DeadlockPlayground.Importer;

namespace DeadlockPlayground.UI
{
    /// <summary>
    /// Design tokens and runtime font-scale helpers for the Custom Model Importer UI.
    /// Enforces a minimum 14 px font size for legibility with Dahlia and supports
    /// user-selectable UI text scaling (100%, 115%, 130%) without widening the panel.
    /// </summary>
    public static class ImporterStyle
    {
        public const int FontHint = 14;
        public const int FontBody = 16;
        public const int FontSection = 20;
        public const int FontTitle = 22;

        public const int ButtonHeight = 44;
        public const int PrimaryButtonHeight = 48;
        public const int IconSize = 24;
        public const int CardPadding = 16;
        public const int Gap = 12;

        private static int _scalePercent = -1;

        public static int ScalePercent
        {
            get
            {
                if (_scalePercent < 0)
                {
                    _scalePercent = ImporterPresetStore.LoadUiScalePercent();
                }
                return _scalePercent;
            }
            set
            {
                int clamped = value is 100 or 115 or 130 ? value : 100;
                if (_scalePercent == clamped) return;
                _scalePercent = clamped;
                ImporterPresetStore.SaveUiScalePercent(clamped);
            }
        }

        public static int ScaledFont(int baseSize)
        {
            int clampedBase = Mathf.Max(FontHint, baseSize);
            return Mathf.RoundToInt(clampedBase * (ScalePercent / 100f));
        }

        /// <summary>
        /// High-contrast secondary color for 14 px Dahlia hint text so it remains crisp
        /// against dark inspector cards.
        /// </summary>
        public static Color HintColor
        {
            get
            {
                var p = PlaygroundThemeHelper.TextPrimary;
                var s = PlaygroundThemeHelper.TextSecondary;
                return new Color(
                    Mathf.Lerp(s.R, p.R, 0.45f),
                    Mathf.Lerp(s.G, p.G, 0.45f),
                    Mathf.Lerp(s.B, p.B, 0.45f),
                    1.0f);
            }
        }

        public static void ApplyFontSize(Control ctrl, int baseSize)
        {
            if (ctrl == null) return;
            int clamped = Mathf.Max(FontHint, baseSize);
            ctrl.SetMeta("importer_base_font", clamped);
            ctrl.AddThemeFontSizeOverride("font_size", ScaledFont(clamped));
        }

        public static void ApplyHintStyle(Label label, bool wrap = true)
        {
            if (label == null) return;
            label.ThemeTypeVariation = "ImporterHint";
            label.AutowrapMode = wrap ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off;
            label.AddThemeColorOverride("font_color", HintColor);
            ApplyFontSize(label, FontHint);
        }

        public static void ApplyBodyStyle(Label label)
        {
            if (label == null) return;
            ApplyFontSize(label, FontBody);
        }

        public static void ApplySectionTitleStyle(Label label)
        {
            if (label == null) return;
            label.ThemeTypeVariation = "ImporterSectionTitle";
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            ApplyFontSize(label, FontSection);
        }

        public static void ApplyStepTitleStyle(Label label)
        {
            if (label == null) return;
            label.ThemeTypeVariation = "ImporterSectionTitle";
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            ApplyFontSize(label, FontTitle);
        }

        /// <summary>
        /// Re-applies font scaling to all controls under <paramref name="root"/> that were
        /// tagged via <see cref="ApplyFontSize"/> or are standard text controls inside the inspector.
        /// </summary>
        public static void RefreshFontScaleRecursive(Node root)
        {
            if (root == null) return;
            if (root is Control ctrl)
            {
                if (ctrl.HasMeta("importer_base_font"))
                {
                    int baseSize = (int)ctrl.GetMeta("importer_base_font");
                    ctrl.AddThemeFontSizeOverride("font_size", ScaledFont(baseSize));
                }
                else if (ctrl is Label or Button or CheckBox or OptionButton or LineEdit or TextEdit or RichTextLabel)
                {
                    int baseSize = ctrl.ThemeTypeVariation.ToString() switch
                    {
                        "ImporterHint" or "ImporterStepChip" => FontHint,
                        "ImporterSectionTitle" or "SectionHeaderLabel" => FontSection,
                        "ImporterPrimaryButton" => 18,
                        "TitleLabel" or "ModalTitleLabel" => FontTitle,
                        _ => FontBody
                    };
                    ctrl.AddThemeFontSizeOverride("font_size", ScaledFont(baseSize));
                }
            }

            foreach (Node child in root.GetChildren())
            {
                RefreshFontScaleRecursive(child);
            }
        }
    }
}

