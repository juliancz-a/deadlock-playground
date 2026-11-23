using System;
using System.Collections.Generic;
using Godot;

namespace DeadlockPlayground.UI
{
    /// <summary>
    /// A group of large selectable option cards with icon, title, description,
    /// and an optional "Recommended" badge.
    /// </summary>
    public partial class OptionCardGroup : VBoxContainer
    {
        public sealed class OptionDef
        {
            public int Id;
            public string Title;
            public string Description;
            public string IconPath;
            public bool Recommended;
        }

        private sealed class CardEntry
        {
            public OptionDef Def;
            public PanelContainer Panel;
            public Button HitButton;
            public Label TitleLabel;
            public Label DescLabel;
            public PanelContainer BadgePanel;
            public StyleBoxFlat NormalStyle;
            public StyleBoxFlat SelectedStyle;
        }

        private readonly List<CardEntry> _entries = new();
        private GridContainer _grid;
        private int _selectedId = -1;

        public int SelectedId => _selectedId;
        public event Action<int> OptionSelected;

        public OptionCardGroup(int columns = 1)
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            AddThemeConstantOverride("separation", ImporterStyle.Gap);

            _grid = new GridContainer
            {
                Columns = Mathf.Max(1, columns),
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            _grid.AddThemeConstantOverride("h_separation", ImporterStyle.Gap);
            _grid.AddThemeConstantOverride("v_separation", ImporterStyle.Gap);
            AddChild(_grid);
        }

        public void SetOptions(IEnumerable<OptionDef> options, int selectedId)
        {
            foreach (Node c in _grid.GetChildren()) c.QueueFree();
            _entries.Clear();
            _selectedId = selectedId;

            foreach (var opt in options)
            {
                var panel = new PanelContainer
                {
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    MouseFilter = MouseFilterEnum.Pass
                };

                var normalStyle = new StyleBoxFlat
                {
                    BgColor = new Color(0.14f, 0.16f, 0.18f, 0.92f),
                    BorderColor = new Color(0.28f, 0.34f, 0.32f, 0.85f),
                    BorderWidthLeft = 1,
                    BorderWidthTop = 1,
                    BorderWidthRight = 1,
                    BorderWidthBottom = 1,
                    CornerRadiusTopLeft = 8,
                    CornerRadiusTopRight = 8,
                    CornerRadiusBottomLeft = 8,
                    CornerRadiusBottomRight = 8
                };

                var selectedStyle = new StyleBoxFlat
                {
                    BgColor = new Color(0.18f, 0.24f, 0.22f, 0.98f),
                    BorderColor = PlaygroundThemeHelper.Parchment,
                    BorderWidthLeft = 2,
                    BorderWidthTop = 2,
                    BorderWidthRight = 2,
                    BorderWidthBottom = 2,
                    CornerRadiusTopLeft = 8,
                    CornerRadiusTopRight = 8,
                    CornerRadiusBottomLeft = 8,
                    CornerRadiusBottomRight = 8
                };

                var hitBtn = new Button
                {
                    Flat = true,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    SizeFlagsVertical = SizeFlags.ExpandFill,
                    MouseDefaultCursorShape = CursorShape.PointingHand
                };

                var pad = new MarginContainer
                {
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    MouseFilter = MouseFilterEnum.Ignore
                };
                pad.AddThemeConstantOverride("margin_left", 12);
                pad.AddThemeConstantOverride("margin_top", 10);
                pad.AddThemeConstantOverride("margin_right", 12);
                pad.AddThemeConstantOverride("margin_bottom", 10);

                var row = new HBoxContainer
                {
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    MouseFilter = MouseFilterEnum.Ignore
                };
                row.AddThemeConstantOverride("separation", 12);

                if (!string.IsNullOrEmpty(opt.IconPath) && ResourceLoader.Exists(opt.IconPath))
                {
                    var tex = GD.Load<Texture2D>(opt.IconPath);
                    if (tex != null)
                    {
                        var icon = new TextureRect
                        {
                            Texture = tex,
                            CustomMinimumSize = new Vector2(ImporterStyle.IconSize, ImporterStyle.IconSize),
                            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                            SizeFlagsVertical = SizeFlags.ShrinkCenter,
                            Modulate = PlaygroundThemeHelper.Parchment,
                            MouseFilter = MouseFilterEnum.Ignore
                        };
                        row.AddChild(icon);
                    }
                }

                var textCol = new VBoxContainer
                {
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    MouseFilter = MouseFilterEnum.Ignore
                };
                textCol.AddThemeConstantOverride("separation", 3);

                var topRow = new HBoxContainer
                {
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    MouseFilter = MouseFilterEnum.Ignore
                };
                topRow.AddThemeConstantOverride("separation", 8);

                var lblTitle = new Label
                {
                    Text = opt.Title,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    MouseFilter = MouseFilterEnum.Ignore
                };
                ImporterStyle.ApplyFontSize(lblTitle, ImporterStyle.FontBody);
                topRow.AddChild(lblTitle);

                PanelContainer badgePanel = null;
                if (opt.Recommended)
                {
                    badgePanel = new PanelContainer
                    {
                        SizeFlagsVertical = SizeFlags.ShrinkCenter,
                        MouseFilter = MouseFilterEnum.Ignore
                    };
                    var badgeStyle = new StyleBoxFlat
                    {
                        BgColor = new Color(0.20f, 0.42f, 0.28f, 0.95f),
                        CornerRadiusTopLeft = 6,
                        CornerRadiusTopRight = 6,
                        CornerRadiusBottomLeft = 6,
                        CornerRadiusBottomRight = 6,
                        ContentMarginLeft = 8,
                        ContentMarginRight = 8,
                        ContentMarginTop = 2,
                        ContentMarginBottom = 2
                    };
                    badgePanel.AddThemeStyleboxOverride("panel", badgeStyle);
                    var lblBadge = new Label
                    {
                        Text = "Recommended",
                        MouseFilter = MouseFilterEnum.Ignore
                    };
                    ImporterStyle.ApplyFontSize(lblBadge, ImporterStyle.FontHint);
                    lblBadge.AddThemeColorOverride("font_color", new Color(0.85f, 0.98f, 0.88f));
                    badgePanel.AddChild(lblBadge);
                    topRow.AddChild(badgePanel);
                }

                textCol.AddChild(topRow);

                var lblDesc = new Label
                {
                    Text = opt.Description ?? "",
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    MouseFilter = MouseFilterEnum.Ignore
                };
                ImporterStyle.ApplyHintStyle(lblDesc);
                textCol.AddChild(lblDesc);

                row.AddChild(textCol);
                pad.AddChild(row);

                // Stack pad and hitBtn inside panel so pad sizes the panel and hitBtn covers it
                panel.AddChild(pad);
                panel.AddChild(hitBtn);

                int capturedId = opt.Id;
                hitBtn.Pressed += () =>
                {
                    Select(capturedId);
                    OptionSelected?.Invoke(capturedId);
                };

                _entries.Add(new CardEntry
                {
                    Def = opt,
                    Panel = panel,
                    HitButton = hitBtn,
                    TitleLabel = lblTitle,
                    DescLabel = lblDesc,
                    BadgePanel = badgePanel,
                    NormalStyle = normalStyle,
                    SelectedStyle = selectedStyle
                });

                _grid.AddChild(panel);
            }

            RefreshSelectionVisuals();
        }

        public void Select(int id)
        {
            _selectedId = id;
            RefreshSelectionVisuals();
        }

        private void RefreshSelectionVisuals()
        {
            foreach (var e in _entries)
            {
                bool isSelected = e.Def.Id == _selectedId;
                e.Panel.AddThemeStyleboxOverride("panel", isSelected ? e.SelectedStyle : e.NormalStyle);
                e.TitleLabel?.AddThemeColorOverride("font_color",
                    isSelected ? PlaygroundThemeHelper.Parchment : PlaygroundThemeHelper.TextPrimary);
            }
        }
    }
}

