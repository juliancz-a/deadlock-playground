using System;
using Godot;

namespace DeadlockPlayground.UI
{
    /// <summary>
    /// Reusable inspector card with optional collapsible header and chevron.
    /// Uses ImporterStyle tokens (min 14 px hint, 20 px section title, 16 px padding).
    /// </summary>
    public partial class CollapsibleCard : PanelContainer
    {
        public VBoxContainer Body { get; private set; }
        public Label TitleLabel { get; private set; }
        public Label SubtitleLabel { get; private set; }
        public bool IsExpanded { get; private set; } = true;

        private Button _headerBtn;
        private string _titleText = "";
        private VBoxContainer _contentBox;
        private bool _collapsible;

        public event Action<bool> ExpandedChanged;

        public CollapsibleCard() { }

        public CollapsibleCard(string title, string subtitle = null, bool collapsible = false, bool startExpanded = true)
        {
            Build(title, subtitle, collapsible, startExpanded);
        }

        public void Build(string title, string subtitle = null, bool collapsible = false, bool startExpanded = true)
        {
            _collapsible = collapsible;
            _titleText = title ?? "";
            IsExpanded = !collapsible || startExpanded;

            ThemeTypeVariation = "SubPanelContainer";
            SizeFlagsHorizontal = SizeFlags.ExpandFill;

            var pad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            pad.AddThemeConstantOverride("margin_left", ImporterStyle.CardPadding);
            pad.AddThemeConstantOverride("margin_top", collapsible ? 8 : ImporterStyle.Gap);
            pad.AddThemeConstantOverride("margin_right", ImporterStyle.CardPadding);
            pad.AddThemeConstantOverride("margin_bottom", collapsible ? 8 : ImporterStyle.CardPadding);
            AddChild(pad);

            var rootBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            rootBox.AddThemeConstantOverride("separation", collapsible ? 6 : ImporterStyle.Gap);
            pad.AddChild(rootBox);

            if (!string.IsNullOrEmpty(_titleText))
            {
                if (collapsible)
                {
                    _headerBtn = new Button
                    {
                        Text = $"{(IsExpanded ? "▾" : "▸")} {_titleText}",
                        Flat = true,
                        Alignment = HorizontalAlignment.Left,
                        AutowrapMode = TextServer.AutowrapMode.WordSmart,
                        SizeFlagsHorizontal = SizeFlags.ExpandFill,
                        CustomMinimumSize = new Vector2(0, 28),
                        MouseDefaultCursorShape = CursorShape.PointingHand
                    };
                    var colus = PlaygroundThemeHelper.GetFontColus();
                    if (colus != null) _headerBtn.AddThemeFontOverride("font", colus);
                    _headerBtn.AddThemeColorOverride("font_color", PlaygroundThemeHelper.Parchment);
                    _headerBtn.AddThemeColorOverride("font_hover_color", Colors.White);
                    _headerBtn.AddThemeColorOverride("font_pressed_color", PlaygroundThemeHelper.Parchment);
                    ImporterStyle.ApplyFontSize(_headerBtn, ImporterStyle.FontBody);
                    _headerBtn.Pressed += () => SetExpanded(!IsExpanded);
                    rootBox.AddChild(_headerBtn);
                }
                else
                {
                    TitleLabel = new Label
                    {
                        Text = _titleText,
                        AutowrapMode = TextServer.AutowrapMode.WordSmart,
                        SizeFlagsHorizontal = SizeFlags.ExpandFill
                    };
                    ImporterStyle.ApplySectionTitleStyle(TitleLabel);
                    rootBox.AddChild(TitleLabel);
                }
            }

            _contentBox = new VBoxContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                Visible = IsExpanded
            };
            _contentBox.AddThemeConstantOverride("separation", ImporterStyle.Gap);
            rootBox.AddChild(_contentBox);

            if (!string.IsNullOrEmpty(subtitle))
            {
                SubtitleLabel = new Label
                {
                    Text = subtitle,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill
                };
                ImporterStyle.ApplyHintStyle(SubtitleLabel);
                _contentBox.AddChild(SubtitleLabel);
            }

            Body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            Body.AddThemeConstantOverride("separation", ImporterStyle.Gap);
            _contentBox.AddChild(Body);
        }

        public void SetExpanded(bool expanded)
        {
            if (!_collapsible) return;
            IsExpanded = expanded;
            if (_contentBox != null) _contentBox.Visible = expanded;
            if (_headerBtn != null) _headerBtn.Text = $"{(expanded ? "▾" : "▸")} {_titleText}";
            ExpandedChanged?.Invoke(expanded);
        }
    }
}
