using System.Collections.Generic;
using Godot;

namespace DeadlockPlayground.Animation
{
    /// <summary>
    /// Centralized loader and applier for Animation Studio at-icons.
    /// Eliminates unicode emojis, text arrows, and provides theme-aligned icons.
    /// </summary>
    public static class DockIcons
    {
        private static readonly Dictionary<string, Texture2D> _cache = new();

        public static Texture2D Load(string iconName)
        {
            if (string.IsNullOrEmpty(iconName)) return null;

            if (_cache.TryGetValue(iconName, out var tex) && tex != null && GodotObject.IsInstanceValid(tex))
            {
                return tex;
            }

            string path = $"res://assets/at-icons/{iconName}.svg";
            if (!ResourceLoader.Exists(path))
            {
                path = $"res://assets/icons/{iconName}.svg";
                if (!ResourceLoader.Exists(path))
                {
                    return null;
                }
            }

            var loaded = GD.Load<Texture2D>(path);
            _cache[iconName] = loaded;
            return loaded;
        }

        private static readonly StyleBoxFlat IconBtnNormal = new()
        {
            ContentMarginLeft = 4f,
            ContentMarginRight = 4f,
            ContentMarginTop = 3f,
            ContentMarginBottom = 3f,
            BgColor = new Color(0.133333f, 0.12549f, 0.129412f, 0.75f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.247059f, 0.364706f, 0.301961f, 0.6f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusBottomLeft = 4
        };

        private static readonly StyleBoxFlat IconBtnHover = new()
        {
            ContentMarginLeft = 4f,
            ContentMarginRight = 4f,
            ContentMarginTop = 3f,
            ContentMarginBottom = 3f,
            BgColor = new Color(0.247059f, 0.364706f, 0.301961f, 0.95f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.447059f, 0.580392f, 0.498039f, 1f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusBottomLeft = 4
        };

        private static readonly StyleBoxFlat IconBtnPressed = new()
        {
            ContentMarginLeft = 4f,
            ContentMarginRight = 4f,
            ContentMarginTop = 3f,
            ContentMarginBottom = 3f,
            BgColor = new Color(0.447059f, 0.580392f, 0.498039f, 1f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            BorderColor = new Color(0.937255f, 0.870588f, 0.74902f, 1f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusBottomLeft = 4
        };

        public static void Apply(Button btn, string iconName, string tooltip = null, bool iconOnly = true)
        {
            if (btn == null) return;

            var icon = Load(iconName);
            if (icon != null)
            {
                btn.Icon = icon;
                btn.ExpandIcon = true;
                if (iconOnly)
                {
                    btn.Text = "";
                    btn.IconAlignment = HorizontalAlignment.Center;
                    btn.CustomMinimumSize = new Vector2(Mathf.Max(btn.CustomMinimumSize.X, 30f), 28f);

                    btn.AddThemeStyleboxOverride("normal", IconBtnNormal);
                    btn.AddThemeStyleboxOverride("hover", IconBtnHover);
                    btn.AddThemeStyleboxOverride("pressed", IconBtnPressed);
                    btn.AddThemeStyleboxOverride("hover_pressed", IconBtnPressed);
                    btn.AddThemeStyleboxOverride("focus", IconBtnHover);
                }
            }

            if (!string.IsNullOrEmpty(tooltip))
            {
                btn.TooltipText = tooltip;
            }
        }
    }
}
