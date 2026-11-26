using System;
using Godot;

namespace DeadlockPlayground.UI
{
    /// <summary>
    /// Visual gallery card for an imported submesh or an original hero part,
    /// displaying a static 3D thumbnail, name, stats, role badge, toggle, and Isolate button.
    /// </summary>
    public partial class MeshThumbCard : PanelContainer
    {
        private TextureRect _thumbRect;
        private Label _lblName;
        private Label _lblStats;
        private Label _lblRoleBadge;
        private PanelContainer _roleBadgePanel;
        private Button _btnToggle;
        private Button _btnIsolate;
        private Button _btnCut;
        private Button _btnSculpt;
        private StyleBoxFlat _normalStyle;
        private StyleBoxFlat _activeStyle;
        private bool _isActive;
        private bool _isHeroKeepMode;

        public VBoxContainer ExtraContent { get; private set; }
        public event Action<bool> Toggled;
        public event Action IsolatePressed;
        public event Action CutPressed;
        public event Action SculptPressed;
        public event Action<bool> HoveredChanged;

        public MeshThumbCard() { }

        public void Build(
            string displayName,
            string statsText,
            string roleText,
            Color roleColor,
            bool initialActive,
            bool isHeroKeepMode = false)
        {
            _isActive = initialActive;
            _isHeroKeepMode = isHeroKeepMode;
            SizeFlagsHorizontal = SizeFlags.ExpandFill;

            _normalStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.13f, 0.15f, 0.17f, 0.92f),
                BorderColor = new Color(0.28f, 0.33f, 0.31f, 0.8f),
                BorderWidthLeft = 1,
                BorderWidthTop = 1,
                BorderWidthRight = 1,
                BorderWidthBottom = 1,
                CornerRadiusTopLeft = 8,
                CornerRadiusTopRight = 8,
                CornerRadiusBottomLeft = 8,
                CornerRadiusBottomRight = 8
            };

            _activeStyle = new StyleBoxFlat
            {
                BgColor = isHeroKeepMode ? new Color(0.14f, 0.24f, 0.18f, 0.96f) : new Color(0.15f, 0.19f, 0.21f, 0.96f),
                BorderColor = isHeroKeepMode ? new Color(0.32f, 0.86f, 0.48f, 1.0f) : PlaygroundThemeHelper.Parchment,
                BorderWidthLeft = 2,
                BorderWidthTop = 2,
                BorderWidthRight = 2,
                BorderWidthBottom = 2,
                CornerRadiusTopLeft = 8,
                CornerRadiusTopRight = 8,
                CornerRadiusBottomLeft = 8,
                CornerRadiusBottomRight = 8
            };

            MouseEntered += () => HoveredChanged?.Invoke(true);
            MouseExited += () => HoveredChanged?.Invoke(false);

            var pad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            pad.AddThemeConstantOverride("margin_left", 10);
            pad.AddThemeConstantOverride("margin_top", 10);
            pad.AddThemeConstantOverride("margin_right", 10);
            pad.AddThemeConstantOverride("margin_bottom", 10);
            AddChild(pad);

            var vBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            vBox.AddThemeConstantOverride("separation", 6);
            pad.AddChild(vBox);

            // Thumbnail + Role badge
            var thumbWrap = new PanelContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 96)
            };
            var thumbBg = new StyleBoxFlat
            {
                BgColor = new Color(0.09f, 0.10f, 0.12f, 0.95f),
                CornerRadiusTopLeft = 6,
                CornerRadiusTopRight = 6,
                CornerRadiusBottomLeft = 6,
                CornerRadiusBottomRight = 6
            };
            thumbWrap.AddThemeStyleboxOverride("panel", thumbBg);

            _thumbRect = new TextureRect
            {
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(88, 88),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill
            };
            thumbWrap.AddChild(_thumbRect);

            if (!string.IsNullOrEmpty(roleText))
            {
                var badgeAlign = new MarginContainer
                {
                    SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
                    SizeFlagsVertical = SizeFlags.ShrinkBegin,
                    MouseFilter = MouseFilterEnum.Ignore
                };
                badgeAlign.AddThemeConstantOverride("margin_top", 4);
                badgeAlign.AddThemeConstantOverride("margin_right", 4);

                _roleBadgePanel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
                var bStyle = new StyleBoxFlat
                {
                    BgColor = new Color(roleColor.R * 0.35f, roleColor.G * 0.35f, roleColor.B * 0.35f, 0.92f),
                    BorderColor = roleColor,
                    BorderWidthLeft = 1,
                    BorderWidthTop = 1,
                    BorderWidthRight = 1,
                    BorderWidthBottom = 1,
                    CornerRadiusTopLeft = 4,
                    CornerRadiusTopRight = 4,
                    CornerRadiusBottomLeft = 4,
                    CornerRadiusBottomRight = 4,
                    ContentMarginLeft = 6,
                    ContentMarginRight = 6,
                    ContentMarginTop = 2,
                    ContentMarginBottom = 2
                };
                _roleBadgePanel.AddThemeStyleboxOverride("panel", bStyle);
                _lblRoleBadge = new Label { Text = roleText, MouseFilter = MouseFilterEnum.Ignore };
                ImporterStyle.ApplyFontSize(_lblRoleBadge, ImporterStyle.FontHint);
                _lblRoleBadge.AddThemeColorOverride("font_color", Colors.White);
                _roleBadgePanel.AddChild(_lblRoleBadge);
                badgeAlign.AddChild(_roleBadgePanel);
                thumbWrap.AddChild(badgeAlign);
            }

            vBox.AddChild(thumbWrap);

            _lblName = new Label
            {
                Text = displayName,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                TooltipText = displayName,
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            ImporterStyle.ApplyFontSize(_lblName, ImporterStyle.FontBody);
            vBox.AddChild(_lblName);

            _lblStats = new Label
            {
                Text = statsText,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            ImporterStyle.ApplyHintStyle(_lblStats);
            vBox.AddChild(_lblStats);

            var btnRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            btnRow.AddThemeConstantOverride("separation", 6);

            _btnToggle = new Button
            {
                Text = isHeroKeepMode ? (initialActive ? "Kept ✓" : "Keep") : (initialActive ? "On" : "Off"),
                ThemeTypeVariation = "ToolbarButton",
                ToggleMode = true,
                ButtonPressed = initialActive,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 34)
            };
            ImporterStyle.ApplyFontSize(_btnToggle, ImporterStyle.FontHint);
            _btnToggle.Toggled += (on) =>
            {
                SetActive(on);
                Toggled?.Invoke(on);
            };
            btnRow.AddChild(_btnToggle);

            _btnIsolate = new Button
            {
                Text = "3D",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(40, 34),
                TooltipText = "Isolate / highlight this mesh in the 3D preview"
            };
            ImporterStyle.ApplyFontSize(_btnIsolate, ImporterStyle.FontHint);
            _btnIsolate.Pressed += () => IsolatePressed?.Invoke();
            btnRow.AddChild(_btnIsolate);

            _btnCut = new Button
            {
                Text = "Cut",
                ThemeTypeVariation = "ToolbarButton",
                CustomMinimumSize = new Vector2(54, 34),
                TooltipText = "Cut / trim triangles away from this mesh in the 3D viewport (non-destructive, undoable)"
            };
            var cutIcon = GD.Load<Texture2D>("res://assets/at-icons/scissors.svg");
            if (cutIcon != null)
            {
                _btnCut.Icon = cutIcon;
                _btnCut.ExpandIcon = true;
                _btnCut.AddThemeConstantOverride("icon_max_width", 14);
                _btnCut.AddThemeConstantOverride("h_separation", 4);
            }
            ImporterStyle.ApplyFontSize(_btnCut, ImporterStyle.FontHint);
            _btnCut.Pressed += () => CutPressed?.Invoke();
            btnRow.AddChild(_btnCut);

            if (!isHeroKeepMode)
            {
                _btnSculpt = new Button
                {
                    Text = "Sculpt",
                    ThemeTypeVariation = "ToolbarButton",
                    CustomMinimumSize = new Vector2(76, 34),
                    TooltipText = "Reshape, non-uniform scale, brush sculpt, or edit faces on this specific mesh in 3D"
                };
                var scIcon = GD.Load<Texture2D>("res://assets/at-icons/mesh_select.svg");
                if (scIcon != null)
                {
                    _btnSculpt.Icon = scIcon;
                    _btnSculpt.ExpandIcon = true;
                    _btnSculpt.AddThemeConstantOverride("icon_max_width", 16);
                    _btnSculpt.AddThemeConstantOverride("h_separation", 5);
                }
                ImporterStyle.ApplyFontSize(_btnSculpt, ImporterStyle.FontHint);
                _btnSculpt.Pressed += () => SculptPressed?.Invoke();
                btnRow.AddChild(_btnSculpt);
            }

            vBox.AddChild(btnRow);

            ExtraContent = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            ExtraContent.AddThemeConstantOverride("separation", 6);
            vBox.AddChild(ExtraContent);

            RefreshBorder();
        }

        public void SetThumbnail(Texture2D tex)
        {
            if (_thumbRect != null && tex != null)
                _thumbRect.Texture = tex;
        }

        public void SetRoleBadge(string roleText, Color roleColor)
        {
            if (_lblRoleBadge != null) _lblRoleBadge.Text = roleText;
            if (_roleBadgePanel != null)
            {
                var bStyle = new StyleBoxFlat
                {
                    BgColor = new Color(roleColor.R * 0.35f, roleColor.G * 0.35f, roleColor.B * 0.35f, 0.92f),
                    BorderColor = roleColor,
                    BorderWidthLeft = 1,
                    BorderWidthTop = 1,
                    BorderWidthRight = 1,
                    BorderWidthBottom = 1,
                    CornerRadiusTopLeft = 4,
                    CornerRadiusTopRight = 4,
                    CornerRadiusBottomLeft = 4,
                    CornerRadiusBottomRight = 4,
                    ContentMarginLeft = 6,
                    ContentMarginRight = 6,
                    ContentMarginTop = 2,
                    ContentMarginBottom = 2
                };
                _roleBadgePanel.AddThemeStyleboxOverride("panel", bStyle);
            }
        }

        public void SetCutBadge(int cutTriCount, int totalTriCount)
        {
            if (_btnCut == null || !GodotObject.IsInstanceValid(_btnCut)) return;
            if (cutTriCount > 0 && totalTriCount > 0)
            {
                int pct = Mathf.Clamp(Mathf.RoundToInt((float)cutTriCount * 100f / totalTriCount), 1, 100);
                _btnCut.Text = $"Cut {pct}%";
                _btnCut.TooltipText = $"Trimmed {cutTriCount:N0} / {totalTriCount:N0} triangles ({pct}%). Click to edit or restore.";
                _btnCut.AddThemeColorOverride("font_color", new Color(1.0f, 0.78f, 0.30f));
                _btnCut.AddThemeColorOverride("icon_normal_color", new Color(1.0f, 0.78f, 0.30f));
            }
            else
            {
                _btnCut.Text = "Cut";
                _btnCut.TooltipText = "Cut / trim triangles away from this mesh in the 3D viewport (non-destructive, undoable)";
                _btnCut.RemoveThemeColorOverride("font_color");
                _btnCut.RemoveThemeColorOverride("icon_normal_color");
            }
        }

        public void SetCutButtonVisible(bool visible)
        {
            if (_btnCut != null && GodotObject.IsInstanceValid(_btnCut))
                _btnCut.Visible = visible;
        }

        public void SetSculptBadge(bool hasEdits, string tooltipSummary = "")
        {
            if (_btnSculpt == null || !GodotObject.IsInstanceValid(_btnSculpt)) return;
            if (hasEdits)
            {
                _btnSculpt.Text = "Sculpt •";
                _btnSculpt.TooltipText = string.IsNullOrEmpty(tooltipSummary)
                    ? "Custom reshape / non-uniform scale active on this mesh. Click to edit in 3D."
                    : tooltipSummary;
                _btnSculpt.AddThemeColorOverride("font_color", new Color(0.45f, 0.88f, 1.0f));
                _btnSculpt.AddThemeColorOverride("icon_normal_color", new Color(0.45f, 0.88f, 1.0f));
            }
            else
            {
                _btnSculpt.Text = "Sculpt";
                _btnSculpt.TooltipText = "Reshape, non-uniform scale, brush sculpt, or edit faces on this specific mesh in 3D";
                _btnSculpt.RemoveThemeColorOverride("font_color");
                _btnSculpt.RemoveThemeColorOverride("icon_normal_color");
            }
        }

        public void SetActive(bool active)
        {
            _isActive = active;
            if (_btnToggle != null)
            {
                _btnToggle.SetPressedNoSignal(active);
                _btnToggle.Text = _isHeroKeepMode ? (active ? "Kept ✓" : "Keep") : (active ? "On" : "Off");
            }
            RefreshBorder();
        }

        private void RefreshBorder()
        {
            AddThemeStyleboxOverride("panel", _isActive ? _activeStyle : _normalStyle);
        }
    }
}

