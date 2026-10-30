using Godot;
using System;
using System.Collections.Generic;
using DeadlockPlayground.Tools;
using DeadlockPlayground.Animation;

namespace DeadlockPlayground.UI
{
    /// <summary>
    /// Modal dialog displaying a comprehensive cheatsheet of all Animation Studio Dock shortcuts and keymaps.
    /// Values are dynamically read from KeybindsManager, allowing users to see their custom key bindings.
    /// Provides direct navigation to Settings > Keybinds > Animation.
    /// Centered reliably using the standard ModalsLayer architecture.
    /// </summary>
    public partial class AnimationShortcutsModalUI : PanelContainer
    {
        [Signal] public delegate void ModalClosedEventHandler();

        private Button _btnClose;
        private Button _btnFooterClose;
        private Button _btnOpenSettings;
        private HBoxContainer _columnsHBox;

        public override void _Ready()
        {
            Name = "AnimationShortcutsModal";
            CustomMinimumSize = new Vector2(840, 620);
            Theme = GD.Load<Theme>("res://assets/themes/PlaygroundTheme.tres");
            MouseFilter = MouseFilterEnum.Stop;

            BuildUI();
            CenterModal();
        }

        public override void _Notification(int what)
        {
            base._Notification(what);
            if (what == NotificationResized && IsInsideTree())
            {
                CenterModal();
            }
        }

        public void CenterModal()
        {
            SetAnchorsPreset(LayoutPreset.Center);
            AnchorLeft = 0.5f;
            AnchorTop = 0.5f;
            AnchorRight = 0.5f;
            AnchorBottom = 0.5f;
            GrowHorizontal = GrowDirection.Both;
            GrowVertical = GrowDirection.Both;
            float halfW = (CustomMinimumSize.X > 0 ? CustomMinimumSize.X : 840f) / 2f;
            float halfH = (CustomMinimumSize.Y > 0 ? CustomMinimumSize.Y : 620f) / 2f;
            OffsetLeft = -halfW;
            OffsetTop = -halfH;
            OffsetRight = halfW;
            OffsetBottom = halfH;
        }

        public void Open()
        {
            Visible = true;
            CenterModal();
            RebuildCards();
        }

        private void BuildUI()
        {
            var cardStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.086f, 0.094f, 0.118f, 0.98f),
                BorderWidthLeft = 1,
                BorderWidthTop = 1,
                BorderWidthRight = 1,
                BorderWidthBottom = 1,
                BorderColor = new Color(0.247f, 0.365f, 0.302f, 0.9f),
                CornerRadiusTopLeft = 8,
                CornerRadiusTopRight = 8,
                CornerRadiusBottomRight = 8,
                CornerRadiusBottomLeft = 8,
                ShadowColor = new Color(0, 0, 0, 0.65f),
                ShadowSize = 16
            };
            AddThemeStyleboxOverride("panel", cardStyle);

            var rootVBox = new VBoxContainer();
            rootVBox.AddThemeConstantOverride("separation", 10);
            AddChild(rootVBox);

            // 1. Header Bar
            var headerMargin = new MarginContainer();
            headerMargin.AddThemeConstantOverride("margin_left", 16);
            headerMargin.AddThemeConstantOverride("margin_top", 12);
            headerMargin.AddThemeConstantOverride("margin_right", 16);
            headerMargin.AddThemeConstantOverride("margin_bottom", 4);

            var headerHBox = new HBoxContainer();
            headerHBox.AddThemeConstantOverride("separation", 12);

            // Icon + Title block
            var titleVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var titleHBox = new HBoxContainer();
            titleHBox.AddThemeConstantOverride("separation", 8);

            var iconTex = DockIcons.Load("key");
            if (iconTex != null)
            {
                var iconRect = new TextureRect
                {
                    Texture = iconTex,
                    CustomMinimumSize = new Vector2(20, 20),
                    ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    Modulate = new Color(0.95f, 0.82f, 0.45f)
                };
                titleHBox.AddChild(iconRect);
            }

            var titleLbl = new Label
            {
                Text = "ANIMATION STUDIO SHORTCUTS & KEYMAP",
                ThemeTypeVariation = "HeaderSmall"
            };
            titleLbl.AddThemeColorOverride("font_color", new Color(0.95f, 0.88f, 0.72f));
            titleLbl.AddThemeFontSizeOverride("font_size", 15);
            titleHBox.AddChild(titleLbl);
            titleVBox.AddChild(titleHBox);

            var subLbl = new Label
            {
                Text = "Blender-style shortcuts for timeline scrubbing, keyframe transforms, and F-Curve editing."
            };
            subLbl.AddThemeColorOverride("font_color", new Color(0.70f, 0.73f, 0.80f));
            subLbl.AddThemeFontSizeOverride("font_size", 12);
            titleVBox.AddChild(subLbl);
            headerHBox.AddChild(titleVBox);

            // Configure in Settings button
            _btnOpenSettings = new Button
            {
                Text = "Configure in Settings",
                Icon = DockIcons.Load("cog"),
                ExpandIcon = true,
                TooltipText = "Open Application Settings to customize any animation shortcut"
            };
            _btnOpenSettings.CustomMinimumSize = new Vector2(170, 28);
            _btnOpenSettings.Pressed += OnOpenSettingsPressed;
            headerHBox.AddChild(_btnOpenSettings);

            // Close button
            _btnClose = new Button
            {
                Text = "✕",
                CustomMinimumSize = new Vector2(30, 28),
                Flat = true,
                TooltipText = "Close (Esc)"
            };
            _btnClose.Pressed += Close;
            headerHBox.AddChild(_btnClose);

            headerMargin.AddChild(headerHBox);
            rootVBox.AddChild(headerMargin);

            // Separator
            var sep = new HSeparator();
            rootVBox.AddChild(sep);

            // 2. Scrollable 2-Column Category Grid
            var bodyMargin = new MarginContainer
            {
                SizeFlagsVertical = SizeFlags.ExpandFill
            };
            bodyMargin.AddThemeConstantOverride("margin_left", 16);
            bodyMargin.AddThemeConstantOverride("margin_top", 4);
            bodyMargin.AddThemeConstantOverride("margin_right", 16);
            bodyMargin.AddThemeConstantOverride("margin_bottom", 6);

            var scroll = new ScrollContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };

            _columnsHBox = new HBoxContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill
            };
            _columnsHBox.AddThemeConstantOverride("separation", 16);
            scroll.AddChild(_columnsHBox);
            bodyMargin.AddChild(scroll);
            rootVBox.AddChild(bodyMargin);

            RebuildCards();

            // 3. Footer Bar
            var footerMargin = new MarginContainer();
            footerMargin.AddThemeConstantOverride("margin_left", 16);
            footerMargin.AddThemeConstantOverride("margin_top", 4);
            footerMargin.AddThemeConstantOverride("margin_right", 16);
            footerMargin.AddThemeConstantOverride("margin_bottom", 12);

            var footerHBox = new HBoxContainer();
            footerHBox.AddThemeConstantOverride("separation", 12);

            var tipHBox = new HBoxContainer();
            tipHBox.AddThemeConstantOverride("separation", 8);
            tipHBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;

            var tipIconTex = DockIcons.Load("info");
            if (tipIconTex != null)
            {
                var tipIcon = new TextureRect
                {
                    Texture = tipIconTex,
                    CustomMinimumSize = new Vector2(16, 16),
                    ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    Modulate = new Color(0.95f, 0.82f, 0.45f)
                };
                tipHBox.AddChild(tipIcon);
            }

            var tipLbl = new Label
            {
                Text = "All shortcuts can be customized in Application Settings > Keybinds > Animation.",
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            tipLbl.AddThemeColorOverride("font_color", new Color(0.65f, 0.70f, 0.78f));
            tipLbl.AddThemeFontSizeOverride("font_size", 12);
            tipHBox.AddChild(tipLbl);
            footerHBox.AddChild(tipHBox);

            _btnFooterClose = new Button
            {
                Text = "Close (Esc)",
                CustomMinimumSize = new Vector2(100, 30)
            };
            _btnFooterClose.Pressed += Close;
            footerHBox.AddChild(_btnFooterClose);

            footerMargin.AddChild(footerHBox);
            rootVBox.AddChild(footerMargin);
        }

        private void RebuildCards()
        {
            if (_columnsHBox == null) return;

            foreach (Node child in _columnsHBox.GetChildren())
            {
                child.QueueFree();
            }

            var col1 = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            col1.AddThemeConstantOverride("separation", 12);

            var col2 = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            col2.AddThemeConstantOverride("separation", 12);

            // Column 1 Cards:
            // 1. Playback & Transport
            col1.AddChild(CreateCategoryCard("play", "Playback & Transport", new (string label, string action, string overrideKey)[]
            {
                ("Play / Pause Transport", "anim_play_toggle", null),
                ("Next Frame (+1)", "anim_step_next", null),
                ("Previous Frame (-1)", "anim_step_prev", null),
                ("Jump to Start (Frame 0)", "anim_seek_start", null),
                ("Jump to End Frame", "anim_seek_end", null),
                ("Toggle Animation Dock", "anim_toggle_dock", null)
            }));

            // 2. Workspace Tabs & Navigation
            col1.AddChild(CreateCategoryCard("layers", "Workspace Tabs & Navigation", new (string label, string action, string overrideKey)[]
            {
                ("Dope Sheet Editor", "anim_tab_dopesheet", null),
                ("Graph Editor", "anim_tab_grapheditor", null),
                ("Clip & Pose Settings", "anim_tab_clippose", null),
                ("F-Curve Modifiers Sidebar", "anim_toggle_modifiers", null),
                ("Shortcuts Cheatsheet", "anim_cheatsheet", null)
            }));

            // 3. Canvas View & Navigation
            col1.AddChild(CreateCategoryCard("magnifying_glass", "Canvas View & Zooming", new (string label, string action, string overrideKey)[]
            {
                ("Fit View to All Keys", "anim_fit_view", null),
                ("Frame Selected Keys", "anim_fit_selected", null),
                ("Pan Canvas Viewport", null, "MMB Drag / Scroll"),
                ("Zoom Canvas Viewport", null, "Ctrl + Scroll")
            }));

            // Column 2 Cards:
            // 4. Keyframe Editing & Selection
            col2.AddChild(CreateCategoryCard("diamond", "Keyframe Authoring & Selection", new (string label, string action, string overrideKey)[]
            {
                ("Insert Key at Playhead", "anim_insert_key", null),
                ("Delete Selected Keys", "anim_delete_keys", null),
                ("Duplicate Selected Keys", "anim_duplicate_keys", null),
                ("Select / Deselect All", "anim_select_all", null),
                ("Box Marquee Select", "anim_box_select", null)
            }));

            // 5. Modal Transforms (Interactive)
            col2.AddChild(CreateCategoryCard("move", "Modal Transforms (G / S / R)", new (string label, string action, string overrideKey)[]
            {
                ("Grab / Translate Keys", "anim_transform_grab", null),
                ("Scale around Playhead", "anim_transform_scale", null),
                ("Rotate Curves (Graph)", "anim_transform_rotate", null),
                ("Lock Time Axis (X)", null, "X (during modal)"),
                ("Lock Value Axis (Y)", null, "Y (during modal)"),
                ("Confirm Transform", null, "Enter / LMB"),
                ("Cancel Transform", null, "Escape / RMB")
            }));

            // 6. Posing & History
            col2.AddChild(CreateCategoryCard("bone", "Posing & History", new (string label, string action, string overrideKey)[]
            {
                ("Copy Bone Pose", "anim_copy_pose", null),
                ("Paste Bone Pose", "anim_paste_pose", null),
                ("Paste Mirrored Pose", "anim_paste_mirror_pose", null),
                ("Undo Action", "anim_undo", null),
                ("Redo Action", "anim_redo", null)
            }));

            _columnsHBox.AddChild(col1);
            _columnsHBox.AddChild(col2);
        }

        private Control CreateCategoryCard(string iconName, string title, (string label, string action, string overrideKey)[] items)
        {
            var cardPanel = new PanelContainer();
            var cardStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.11f, 0.12f, 0.15f, 0.85f),
                BorderWidthLeft = 1,
                BorderWidthTop = 1,
                BorderWidthRight = 1,
                BorderWidthBottom = 1,
                BorderColor = new Color(0.20f, 0.22f, 0.27f, 0.8f),
                CornerRadiusTopLeft = 6,
                CornerRadiusTopRight = 6,
                CornerRadiusBottomRight = 6,
                CornerRadiusBottomLeft = 6
            };
            cardPanel.AddThemeStyleboxOverride("panel", cardStyle);

            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 12);
            margin.AddThemeConstantOverride("margin_top", 8);
            margin.AddThemeConstantOverride("margin_right", 12);
            margin.AddThemeConstantOverride("margin_bottom", 8);

            var vbox = new VBoxContainer();
            vbox.AddThemeConstantOverride("separation", 6);

            // Card Header HBox: Icon + Title
            var headerHBox = new HBoxContainer();
            headerHBox.AddThemeConstantOverride("separation", 8);

            if (!string.IsNullOrEmpty(iconName))
            {
                var catIconTex = DockIcons.Load(iconName);
                if (catIconTex != null)
                {
                    var catIconRect = new TextureRect
                    {
                        Texture = catIconTex,
                        CustomMinimumSize = new Vector2(16, 16),
                        ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
                        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                        Modulate = new Color(0.95f, 0.82f, 0.45f)
                    };
                    headerHBox.AddChild(catIconRect);
                }
            }

            var titleLbl = new Label
            {
                Text = title
            };
            titleLbl.AddThemeColorOverride("font_color", new Color(0.95f, 0.82f, 0.45f));
            titleLbl.AddThemeFontSizeOverride("font_size", 13);
            headerHBox.AddChild(titleLbl);
            vbox.AddChild(headerHBox);

            var divider = new HSeparator();
            divider.AddThemeConstantOverride("separation", 2);
            vbox.AddChild(divider);

            // Rows
            foreach (var item in items)
            {
                var rowHBox = new HBoxContainer();
                rowHBox.AddThemeConstantOverride("separation", 8);

                var actionLbl = new Label
                {
                    Text = item.label,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill
                };
                actionLbl.AddThemeColorOverride("font_color", new Color(0.85f, 0.87f, 0.90f));
                actionLbl.AddThemeFontSizeOverride("font_size", 12);
                rowHBox.AddChild(actionLbl);

                string keyText = item.overrideKey;
                if (string.IsNullOrEmpty(keyText) && !string.IsNullOrEmpty(item.action))
                {
                    keyText = KeybindsManager.GetShortcutText(item.action);
                    if (string.IsNullOrEmpty(keyText)) keyText = "None";
                }

                // Keyboard keycap badge
                var badgePanel = new PanelContainer();
                var badgeStyle = new StyleBoxFlat
                {
                    BgColor = new Color(0.18f, 0.20f, 0.26f, 0.95f),
                    BorderWidthLeft = 1,
                    BorderWidthTop = 1,
                    BorderWidthRight = 1,
                    BorderWidthBottom = 2,
                    BorderColor = new Color(0.32f, 0.36f, 0.45f, 0.95f),
                    CornerRadiusTopLeft = 4,
                    CornerRadiusTopRight = 4,
                    CornerRadiusBottomRight = 4,
                    CornerRadiusBottomLeft = 4
                };
                badgePanel.AddThemeStyleboxOverride("panel", badgeStyle);

                var badgeMargin = new MarginContainer();
                badgeMargin.AddThemeConstantOverride("margin_left", 6);
                badgeMargin.AddThemeConstantOverride("margin_top", 2);
                badgeMargin.AddThemeConstantOverride("margin_right", 6);
                badgeMargin.AddThemeConstantOverride("margin_bottom", 2);

                var badgeLbl = new Label
                {
                    Text = keyText
                };
                badgeLbl.AddThemeColorOverride("font_color", new Color(1.0f, 0.88f, 0.45f));
                badgeLbl.AddThemeFontSizeOverride("font_size", 11);
                badgeMargin.AddChild(badgeLbl);
                badgePanel.AddChild(badgeMargin);

                rowHBox.AddChild(badgePanel);
                vbox.AddChild(rowHBox);
            }

            margin.AddChild(vbox);
            cardPanel.AddChild(margin);
            return cardPanel;
        }

        private void OnOpenSettingsPressed()
        {
            Close();

            // Locate SettingsModal in tree and navigate directly to KEYBINDS -> ANIMATION
            var settingsModal = GetTree()?.Root?.FindChild("SettingsModal", true, false) as SettingsDialog
                             ?? GetTree()?.Root?.FindChild("SettingsDialog", true, false) as SettingsDialog;

            if (settingsModal != null)
            {
                var modalsLayer = GetTree()?.Root?.FindChild("ModalsLayer", true, false) as Control;
                if (modalsLayer != null) modalsLayer.Visible = true;

                settingsModal.Open(tabIndex: 3, keybindCategory: "Animation");
            }
        }

        public void Close()
        {
            Visible = false;
            EmitSignal(SignalName.ModalClosed);
        }

        public override void _Input(InputEvent @event)
        {
            if (!IsVisibleInTree()) return;

            if (@event is InputEventKey key && key.Pressed && !key.Echo)
            {
                if (key.Keycode == Key.Escape)
                {
                    GetViewport()?.SetInputAsHandled();
                    Close();
                }
            }
        }
    }
}
