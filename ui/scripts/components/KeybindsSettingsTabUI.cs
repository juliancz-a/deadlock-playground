using Godot;
using System;
using System.Collections.Generic;
using DeadlockPlayground.Tools;

public partial class KeybindsSettingsTabUI : VBoxContainer
{
    private readonly Dictionary<string, VBoxContainer> _categoryContainers = new();
    private Label _lblConflictWarning;
    private Button _btnResetDefaults;
    private ConfirmationDialog _confirmResetDialog;

    private string _listeningAction = null;
    private Button _listeningButton = null;
    private Key _pendingConflictKey = Key.None;
    private bool _pendingConflictCtrl = false;
    private bool _pendingConflictShift = false;
    private bool _pendingConflictAlt = false;

    private readonly Dictionary<string, Button> _actionButtons = new();

    public override void _Ready()
    {
        ThemeOverrideConstants();
        BuildUI();
        KeybindsManager.OnKeybindsChanged += RefreshAllButtons;
    }

    public override void _ExitTree()
    {
        KeybindsManager.OnKeybindsChanged -= RefreshAllButtons;
    }

    private void ThemeOverrideConstants()
    {
        AddThemeConstantOverride("separation", 10);
    }

    private void BuildUI()
    {
        // 1. Header description
        var descLabel = new Label
        {
            Text = "Customize editor shortcuts. Click any keybind button to assign a new key.\nSupports modifiers (Ctrl, Shift, Alt). Press Escape while listening to cancel.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        descLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.72f, 0.78f));
        descLabel.AddThemeFontSizeOverride("font_size", 12);
        AddChild(descLabel);

        // 2. Conflict Warning Label
        _lblConflictWarning = new Label
        {
            Visible = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        _lblConflictWarning.AddThemeColorOverride("font_color", new Color(1.0f, 0.4f, 0.35f));
        _lblConflictWarning.AddThemeFontSizeOverride("font_size", 12);
        AddChild(_lblConflictWarning);

        // 3. Category TabContainer
        var tabContainer = new TabContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 270)
        };

        string[] categories = { "Texture Paint", "Camera", "Bones" };
        foreach (var cat in categories)
        {
            var margin = new MarginContainer
            {
                Name = cat,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill
            };
            margin.AddThemeConstantOverride("margin_left", 6);
            margin.AddThemeConstantOverride("margin_top", 8);
            margin.AddThemeConstantOverride("margin_right", 6);
            margin.AddThemeConstantOverride("margin_bottom", 6);

            var scroll = new ScrollContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };

            var vbox = new VBoxContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill
            };
            vbox.AddThemeConstantOverride("separation", 6);

            scroll.AddChild(vbox);
            margin.AddChild(scroll);
            tabContainer.AddChild(margin);

            _categoryContainers[cat] = vbox;
        }

        AddChild(tabContainer);
        PopulateActionRows();

        // 4. Bottom action bar
        var bottomBar = new HBoxContainer();
        bottomBar.AddThemeConstantOverride("separation", 12);

        _btnResetDefaults = new Button
        {
            Text = "Reset to Defaults",
            CustomMinimumSize = new Vector2(140, 30)
        };
        _btnResetDefaults.Pressed += OnResetDefaultsPressed;
        bottomBar.AddChild(_btnResetDefaults);

        AddChild(bottomBar);

        // Confirmation Dialog
        _confirmResetDialog = new ConfirmationDialog
        {
            Title = "Reset Keybinds to Defaults?",
            DialogText = "Reset all keybind mappings across all categories back to their default shortcuts?",
            OkButtonText = "Reset",
            CancelButtonText = "Cancel"
        };
        _confirmResetDialog.Confirmed += () =>
        {
            KeybindsManager.ResetToDefaults();
            StopListening();
            RefreshAllButtons();
        };
        AddChild(_confirmResetDialog);
    }

    private void PopulateActionRows()
    {
        foreach (var vbox in _categoryContainers.Values)
        {
            foreach (Node child in vbox.GetChildren())
            {
                child.QueueFree();
            }
        }
        _actionButtons.Clear();

        foreach (var kvp in KeybindsManager.Bindings)
        {
            string action = kvp.Key;
            var binding = kvp.Value;
            string category = string.IsNullOrEmpty(binding.Category) ? "Texture Paint" : binding.Category;

            if (!_categoryContainers.TryGetValue(category, out var targetContainer))
            {
                if (!_categoryContainers.TryGetValue("Texture Paint", out targetContainer))
                    continue;
            }

            var row = new PanelContainer();
            var rowStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.11f, 0.12f, 0.15f, 0.8f),
                CornerRadiusTopLeft = 4,
                CornerRadiusTopRight = 4,
                CornerRadiusBottomRight = 4,
                CornerRadiusBottomLeft = 4
            };
            row.AddThemeStyleboxOverride("panel", rowStyle);

            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 12);
            margin.AddThemeConstantOverride("margin_top", 6);
            margin.AddThemeConstantOverride("margin_right", 12);
            margin.AddThemeConstantOverride("margin_bottom", 6);

            var hbox = new HBoxContainer();
            hbox.AddThemeConstantOverride("separation", 12);

            // Left text block (Title + Description)
            var vboxText = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var titleLbl = new Label
            {
                Text = binding.DisplayName,
                ThemeTypeVariation = "HeaderSmall"
            };
            titleLbl.AddThemeColorOverride("font_color", new Color(0.92f, 0.93f, 0.95f));
            titleLbl.AddThemeFontSizeOverride("font_size", 13);

            var descLbl = new Label
            {
                Text = binding.Description
            };
            descLbl.AddThemeColorOverride("font_color", new Color(0.6f, 0.62f, 0.68f));
            descLbl.AddThemeFontSizeOverride("font_size", 11);

            vboxText.AddChild(titleLbl);
            vboxText.AddChild(descLbl);
            hbox.AddChild(vboxText);

            // Right: Rebind button
            var btn = new Button
            {
                Text = KeybindsManager.GetShortcutText(action),
                CustomMinimumSize = new Vector2(110, 28)
            };
            btn.Pressed += () => StartListening(action, btn);

            _actionButtons[action] = btn;
            hbox.AddChild(btn);

            margin.AddChild(hbox);
            row.AddChild(margin);
            targetContainer.AddChild(row);
        }
    }

    private void StartListening(string action, Button btn)
    {
        if (_listeningAction != null && _listeningButton != null)
        {
            // Reset previous button
            _listeningButton.Text = KeybindsManager.GetShortcutText(_listeningAction);
            _listeningButton.Modulate = Colors.White;
        }

        _listeningAction = action;
        _listeningButton = btn;
        _pendingConflictKey = Key.None;

        btn.Text = "Press any key...";
        btn.Modulate = new Color(1.0f, 0.85f, 0.4f);
        _lblConflictWarning.Visible = false;
    }

    private void StopListening()
    {
        if (_listeningAction != null && _listeningButton != null)
        {
            _listeningButton.Text = KeybindsManager.GetShortcutText(_listeningAction);
            _listeningButton.Modulate = Colors.White;
        }
        _listeningAction = null;
        _listeningButton = null;
        _pendingConflictKey = Key.None;
        _lblConflictWarning.Visible = false;
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (_listeningAction == null || @event is not InputEventKey keyEvent || !keyEvent.Pressed || keyEvent.Echo)
            return;

        // Check for Escape to cancel
        if (keyEvent.Keycode == Key.Escape)
        {
            StopListening();
            GetViewport()?.SetInputAsHandled();
            return;
        }

        // Ignore pure modifier presses
        if (keyEvent.Keycode == Key.Ctrl || keyEvent.Keycode == Key.Shift || keyEvent.Keycode == Key.Alt || keyEvent.Keycode == Key.Meta)
        {
            return;
        }

        Key pressedKey = keyEvent.Keycode;
        bool ctrl = keyEvent.CtrlPressed || keyEvent.MetaPressed;
        bool shift = keyEvent.ShiftPressed;
        bool alt = keyEvent.AltPressed;

        // Check if this was a repeat press confirming a conflict overwrite
        if (_pendingConflictKey == pressedKey && _pendingConflictCtrl == ctrl && _pendingConflictShift == shift && _pendingConflictAlt == alt)
        {
            KeybindsManager.ForceRebind(_listeningAction, pressedKey, ctrl, shift, alt);
            StopListening();
            RefreshAllButtons();
            GetViewport()?.SetInputAsHandled();
            return;
        }

        // Check for conflicts
        if (KeybindsManager.CheckConflict(_listeningAction, pressedKey, ctrl, shift, alt, out string conflictActionName))
        {
            _pendingConflictKey = pressedKey;
            _pendingConflictCtrl = ctrl;
            _pendingConflictShift = shift;
            _pendingConflictAlt = alt;

            string keyStr = KeybindsManager.FormatKey(pressedKey, ctrl, shift, alt);
            _lblConflictWarning.Text = $"⚠ Conflict: '{keyStr}' is already assigned to '{conflictActionName}'. Press '{keyStr}' again to overwrite, or press Esc to cancel.";
            _lblConflictWarning.Visible = true;
            GetViewport()?.SetInputAsHandled();
            return;
        }

        // Clean rebind
        KeybindsManager.Rebind(_listeningAction, pressedKey, ctrl, shift, alt, out _);
        StopListening();
        RefreshAllButtons();
        GetViewport()?.SetInputAsHandled();
    }

    private void RefreshAllButtons()
    {
        foreach (var kvp in _actionButtons)
        {
            if (kvp.Key != _listeningAction)
            {
                kvp.Value.Text = KeybindsManager.GetShortcutText(kvp.Key);
            }
        }
    }

    private void OnResetDefaultsPressed()
    {
        _confirmResetDialog?.PopupCentered();
    }
}
