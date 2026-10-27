using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeadlockPlayground.Tools
{
    public class ActionBinding
    {
        [JsonPropertyName("action")]
        public string ActionName { get; set; } = "";

        [JsonPropertyName("name")]
        public string DisplayName { get; set; } = "";

        [JsonPropertyName("desc")]
        public string Description { get; set; } = "";

        [JsonPropertyName("key")]
        public Key Keycode { get; set; }

        [JsonPropertyName("ctrl")]
        public bool Ctrl { get; set; }

        [JsonPropertyName("shift")]
        public bool Shift { get; set; }

        [JsonPropertyName("alt")]
        public bool Alt { get; set; }

        [JsonPropertyName("category")]
        public string Category { get; set; } = "Texture Paint";

        [JsonPropertyName("secondary_key")]
        public Key SecondaryKeycode { get; set; } = Key.None;

        [JsonPropertyName("secondary_ctrl")]
        public bool SecondaryCtrl { get; set; }

        [JsonPropertyName("secondary_shift")]
        public bool SecondaryShift { get; set; }

        [JsonPropertyName("secondary_alt")]
        public bool SecondaryAlt { get; set; }
    }

    public static class KeybindsManager
    {
        private const string ConfigPath = "user://keybinds.json";

        public static event Action OnKeybindsChanged;

        private static readonly Dictionary<string, ActionBinding> _bindings = new();
        private static bool _initialized = false;

        public static IReadOnlyDictionary<string, ActionBinding> Bindings
        {
            get
            {
                EnsureInitialized();
                return _bindings;
            }
        }

        static KeybindsManager()
        {
            EnsureInitialized();
        }

        public static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            SetupDefaults();
            LoadKeybinds();
            RegisterAllToInputMap();
        }

        private static void SetupDefaults()
        {
            _bindings.Clear();

            // 1. Texture Paint
            AddDefault("Texture Paint", "paint_brush", "Brush", "Switch to standard Brush painting tool", Key.B);
            AddDefault("Texture Paint", "paint_eraser", "Eraser", "Switch to Eraser tool", Key.E);
            AddDefault("Texture Paint", "paint_eyedropper", "Eyedropper", "Sample raw material color from surface", Key.I);
            AddDefault("Texture Paint", "paint_bucket", "Bucket Fill", "Switch to Submesh / Island Bucket Fill tool", Key.G);
            AddDefault("Texture Paint", "paint_wand", "Magic Wand", "Switch to Magic Wand color-range mask tool", Key.W);
            AddDefault("Texture Paint", "paint_select_rect", "Rectangular Selection", "Switch to Rectangular selection tool", Key.M);
            AddDefault("Texture Paint", "paint_select_lasso", "Lasso Selection", "Switch to Lasso freehand selection tool", Key.L);
            AddDefault("Texture Paint", "paint_select_poly", "Polygonal Selection", "Switch to Polygonal selection tool", Key.P);
            AddDefault("Texture Paint", "paint_selection_invert", "Invert Selection", "Invert active selection mask", Key.I, ctrl: true, shift: true);
            AddDefault("Texture Paint", "paint_selection_clear", "Clear Selection", "Clear active selection mask", Key.D, ctrl: true);
            AddDefault("Texture Paint", "paint_decal", "Decal Stamper", "Switch to Decal projection stamper", Key.K);
            AddDefault("Texture Paint", "paint_clone", "Clone Stamp", "Switch to Clone Stamp tool", Key.S);
            AddDefault("Texture Paint", "paint_text", "Text Projector", "Toggle 3D Text Projector panel", Key.T);
            AddDefault("Texture Paint", "paint_mirror", "Mirror Symmetry", "Toggle 3D Axis Symmetry painting", Key.N);
            AddDefault("Texture Paint", "paint_undo", "Undo", "Undo painter stroke", Key.Z, ctrl: true);
            AddDefault("Texture Paint", "paint_redo", "Redo", "Redo painter stroke", Key.Y, ctrl: true, 
                secKey: Key.Z, secCtrl: true, secShift: true);
            AddDefault("Texture Paint", "brush_size_up", "Brush Size Up", "Increase brush radius (+)", Key.Equal, 
                secKey: Key.KpAdd);
            AddDefault("Texture Paint", "brush_size_down", "Brush Size Down", "Decrease brush radius (-)", Key.Minus, 
                secKey: Key.KpSubtract);
            AddDefault("Texture Paint", "paint_uv_toggle", "Toggle 2D UV Canvas", "Toggle docked 2D UV Texture Canvas", Key.U);

            // 2. Camera
            AddDefault("Camera", "camera_pan_up", "Move Up", "Move camera viewport up (W)", Key.W);
            AddDefault("Camera", "camera_pan_down", "Move Down", "Move camera viewport down (S)", Key.S);
            AddDefault("Camera", "camera_pan_left", "Move Left", "Move camera viewport left (A)", Key.A);
            AddDefault("Camera", "camera_pan_right", "Move Right", "Move camera viewport right (D)", Key.D);
            AddDefault("Camera", "camera_elevate_up", "Elevate Up", "Raise camera elevation (E)", Key.E);
            AddDefault("Camera", "camera_elevate_down", "Elevate Down", "Lower camera elevation (Q)", Key.Q);
            AddDefault("Camera", "camera_freecam", "Freecam Toggle", "Toggle between Orbit and Freecam navigation", Key.F);
            AddDefault("Camera", "camera_reset", "Reset View", "Reset camera angle, zoom, and orientation", Key.Home, 
                secKey: Key.R, secCtrl: true);
            AddDefault("Camera", "camera_focus", "Focus Model", "Recenter and focus camera on character model", Key.F, ctrl: true);

            // 3. Bones / Rigging
            AddDefault("Bones", "view_xray", "Toggle X-Ray", "Toggle Submesh Bone & Wireframe X-Ray mode", Key.X);
            AddDefault("Bones", "bones_ik_toggle", "Toggle IK Handles", "Toggle Inverse Kinematics limb handles", Key.I);
            AddDefault("Bones", "bones_reset_pose", "Reset Skeleton Pose", "Reset character skeleton to default bind pose", Key.P, ctrl: true);

            // 4. Animation Studio Deck
            AddDefault("Animation", "anim_toggle_dock", "Toggle Animation Dock", "Open or collapse bottom Animation Studio Dock", Key.Space, ctrl: true);
            AddDefault("Animation", "anim_play_toggle", "Play / Pause", "Toggle animation playback transport", Key.Space);
            AddDefault("Animation", "anim_step_prev", "Previous Frame", "Step playhead backward by 1 frame", Key.Left);
            AddDefault("Animation", "anim_step_next", "Next Frame", "Step playhead forward by 1 frame", Key.Right);
            AddDefault("Animation", "anim_seek_start", "Jump to Start", "Seek playhead to frame 0", Key.Left, shift: true, secKey: Key.Home);
            AddDefault("Animation", "anim_seek_end", "Jump to End", "Seek playhead to end frame", Key.Right, shift: true, secKey: Key.End);
            AddDefault("Animation", "anim_tab_dopesheet", "Dope Sheet Editor", "Switch to Dope Sheet editor tab", Key.Key1);
            AddDefault("Animation", "anim_tab_grapheditor", "Graph Editor", "Switch to F-Curve Graph Editor tab", Key.Key2);
            AddDefault("Animation", "anim_tab_clippose", "Clip & Pose Settings", "Switch to Clip & Pose settings tab", Key.Key3);
            AddDefault("Animation", "anim_insert_key", "Insert Keyframe", "Insert keyframe at playhead for selected bone", Key.I);
            AddDefault("Animation", "anim_delete_keys", "Delete Keyframes", "Delete selected keyframes from active curves/tracks", Key.Delete, secKey: Key.X);
            AddDefault("Animation", "anim_duplicate_keys", "Duplicate Keyframes", "Duplicate selected keyframes offset by 1 frame", Key.D, shift: true);
            AddDefault("Animation", "anim_select_all", "Select / Deselect All", "Toggle select all or deselect all keyframes", Key.A);
            AddDefault("Animation", "anim_box_select", "Box Select Keys", "Activate rectangular marquee selection for keyframes", Key.B);
            AddDefault("Animation", "anim_fit_view", "Fit View to Canvas", "Frame and center all visible keyframes or curves", Key.Home);
            AddDefault("Animation", "anim_fit_selected", "Frame Selected Keys", "Frame and zoom in on selected keyframes", Key.Period, secKey: Key.KpPeriod);
            AddDefault("Animation", "anim_transform_grab", "Grab / Move Keys", "Start interactive keyframe translation / retiming", Key.G);
            AddDefault("Animation", "anim_transform_scale", "Scale Keys", "Start interactive keyframe scaling around playhead", Key.S);
            AddDefault("Animation", "anim_transform_rotate", "Rotate Keys (Graph)", "Start interactive curve rotation on selected F-Curve keys", Key.R);
            AddDefault("Animation", "anim_toggle_modifiers", "Toggle Modifiers Sidebar", "Toggle F-Curve Modifiers sidebar panel in Graph Editor", Key.N);
            AddDefault("Animation", "anim_copy_pose", "Copy Pose", "Copy selected bone pose to clipboard", Key.C, ctrl: true);
            AddDefault("Animation", "anim_paste_pose", "Paste Pose", "Paste bone pose from clipboard onto current frame", Key.V, ctrl: true);
            AddDefault("Animation", "anim_paste_mirror_pose", "Paste Mirrored Pose", "Paste opposite mirrored bone pose onto current frame", Key.V, ctrl: true, shift: true);
            AddDefault("Animation", "anim_undo", "Undo Animation", "Undo last animation or keyframe modification", Key.Z, ctrl: true);
            AddDefault("Animation", "anim_redo", "Redo Animation", "Redo previously undone animation action", Key.Y, ctrl: true, secKey: Key.Z, secCtrl: true, secShift: true);
            AddDefault("Animation", "anim_cheatsheet", "Shortcuts Cheatsheet", "Open Animation Editor Shortcuts Cheatsheet dialog", Key.F1, secKey: Key.Slash, secShift: true);
        }

        private static void AddDefault(string category, string action, string name, string desc, Key key, 
            bool ctrl = false, bool shift = false, bool alt = false,
            Key secKey = Key.None, bool secCtrl = false, bool secShift = false, bool secAlt = false)
        {
            _bindings[action] = new ActionBinding
            {
                Category = category,
                ActionName = action,
                DisplayName = name,
                Description = desc,
                Keycode = key,
                Ctrl = ctrl,
                Shift = shift,
                Alt = alt,
                SecondaryKeycode = secKey,
                SecondaryCtrl = secCtrl,
                SecondaryShift = secShift,
                SecondaryAlt = secAlt
            };
        }

        public static void LoadKeybinds()
        {
            try
            {
                if (!FileAccess.FileExists(ConfigPath)) return;

                using var file = FileAccess.Open(ConfigPath, FileAccess.ModeFlags.Read);
                if (file == null) return;

                string json = file.GetAsText();
                var saved = JsonSerializer.Deserialize<Dictionary<string, ActionBinding>>(json);
                if (saved != null)
                {
                    foreach (var kvp in saved)
                    {
                        if (_bindings.ContainsKey(kvp.Key))
                        {
                            var b = _bindings[kvp.Key];
                            b.Keycode = kvp.Value.Keycode;
                            b.Ctrl = kvp.Value.Ctrl;
                            b.Shift = kvp.Value.Shift;
                            b.Alt = kvp.Value.Alt;
                            if (!string.IsNullOrEmpty(kvp.Value.Category))
                            {
                                b.Category = kvp.Value.Category;
                            }
                            if (kvp.Value.SecondaryKeycode != Key.None)
                            {
                                b.SecondaryKeycode = kvp.Value.SecondaryKeycode;
                                b.SecondaryCtrl = kvp.Value.SecondaryCtrl;
                                b.SecondaryShift = kvp.Value.SecondaryShift;
                                b.SecondaryAlt = kvp.Value.SecondaryAlt;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[KeybindsManager] Failed to load keybinds: {ex.Message}");
            }
        }

        public static void SaveKeybinds()
        {
            try
            {
                using var file = FileAccess.Open(ConfigPath, FileAccess.ModeFlags.Write);
                if (file == null) return;

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_bindings, options);
                file.StoreString(json);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[KeybindsManager] Failed to save keybinds: {ex.Message}");
            }
        }

        public static void ResetToDefaults()
        {
            SetupDefaults();
            RegisterAllToInputMap();
            SaveKeybinds();
            OnKeybindsChanged?.Invoke();
        }

        public static bool CheckConflict(string action, Key key, bool ctrl, bool shift, bool alt, out string conflictingActionName)
        {
            conflictingActionName = null;
            string targetCat = _bindings.TryGetValue(action, out var cur) ? cur.Category : "";

            foreach (var kvp in _bindings)
            {
                if (kvp.Key == action) continue;
                var b = kvp.Value;

                // Scope conflicts to the same category so paint and animation shortcuts don't block each other
                if (!string.IsNullOrEmpty(targetCat) && !string.IsNullOrEmpty(b.Category) && b.Category != targetCat)
                    continue;

                if (b.Keycode == key && b.Ctrl == ctrl && b.Shift == shift && b.Alt == alt)
                {
                    conflictingActionName = b.DisplayName;
                    return true;
                }
            }
            return false;
        }

        public static bool Rebind(string action, Key key, bool ctrl, bool shift, bool alt, out string conflictFound)
        {
            EnsureInitialized();
            conflictFound = null;

            if (CheckConflict(action, key, ctrl, shift, alt, out conflictFound))
            {
                // Return conflict info so UI can display warning
                return false;
            }

            if (_bindings.TryGetValue(action, out var binding))
            {
                binding.Keycode = key;
                binding.Ctrl = ctrl;
                binding.Shift = shift;
                binding.Alt = alt;

                RegisterActionToInputMap(binding);
                SaveKeybinds();
                OnKeybindsChanged?.Invoke();
                return true;
            }

            return false;
        }

        public static void ForceRebind(string action, Key key, bool ctrl, bool shift, bool alt)
        {
            EnsureInitialized();
            string targetCat = _bindings.TryGetValue(action, out var cur) ? cur.Category : "";

            // Clear conflicting binding on other actions in the same category
            foreach (var kvp in _bindings)
            {
                if (kvp.Key == action) continue;
                var b = kvp.Value;

                if (!string.IsNullOrEmpty(targetCat) && !string.IsNullOrEmpty(b.Category) && b.Category != targetCat)
                    continue;

                if (b.Keycode == key && b.Ctrl == ctrl && b.Shift == shift && b.Alt == alt)
                {
                    b.Keycode = Key.None;
                    b.Ctrl = false;
                    b.Shift = false;
                    b.Alt = false;
                    RegisterActionToInputMap(b);
                }
            }

            if (_bindings.TryGetValue(action, out var binding))
            {
                binding.Keycode = key;
                binding.Ctrl = ctrl;
                binding.Shift = shift;
                binding.Alt = alt;

                RegisterActionToInputMap(binding);
                SaveKeybinds();
                OnKeybindsChanged?.Invoke();
            }
        }

        /// <summary>
        /// Fast, reliable shortcut tester that tests if an InputEventKey matches an action binding
        /// against both primary and secondary keys with exact modifiers.
        /// </summary>
        public static bool IsActionTriggered(string action, InputEventKey keyEvent)
        {
            EnsureInitialized();
            if (keyEvent == null || !keyEvent.Pressed || keyEvent.Echo) return false;
            if (!_bindings.TryGetValue(action, out var b)) return false;

            Key pressedKey = keyEvent.Keycode != Key.None ? keyEvent.Keycode : keyEvent.PhysicalKeycode;
            bool ctrl = keyEvent.CtrlPressed || keyEvent.MetaPressed;
            bool shift = keyEvent.ShiftPressed;
            bool alt = keyEvent.AltPressed;

            if (b.Keycode != Key.None && pressedKey == b.Keycode && ctrl == b.Ctrl && shift == b.Shift && alt == b.Alt)
                return true;

            if (b.SecondaryKeycode != Key.None && pressedKey == b.SecondaryKeycode && ctrl == b.SecondaryCtrl && shift == b.SecondaryShift && alt == b.SecondaryAlt)
                return true;

            return false;
        }

        public static void RegisterAllToInputMap()
        {
            foreach (var binding in _bindings.Values)
            {
                RegisterActionToInputMap(binding);
            }
        }

        private static void RegisterActionToInputMap(ActionBinding binding)
        {
            string action = binding.ActionName;
            if (!InputMap.HasAction(action))
            {
                InputMap.AddAction(action);
            }

            InputMap.ActionEraseEvents(action);

            if (binding.Keycode != Key.None)
            {
                var ev = new InputEventKey
                {
                    Keycode = binding.Keycode,
                    PhysicalKeycode = binding.Keycode,
                    CtrlPressed = binding.Ctrl,
                    ShiftPressed = binding.Shift,
                    AltPressed = binding.Alt
                };
                InputMap.ActionAddEvent(action, ev);
            }

            if (binding.SecondaryKeycode != Key.None)
            {
                var secEv = new InputEventKey
                {
                    Keycode = binding.SecondaryKeycode,
                    PhysicalKeycode = binding.SecondaryKeycode,
                    CtrlPressed = binding.SecondaryCtrl,
                    ShiftPressed = binding.SecondaryShift,
                    AltPressed = binding.SecondaryAlt
                };
                InputMap.ActionAddEvent(action, secEv);
            }
        }

        public static string GetShortcutText(string action)
        {
            EnsureInitialized();
            if (_bindings.TryGetValue(action, out var b))
            {
                return FormatKey(b.Keycode, b.Ctrl, b.Shift, b.Alt);
            }
            return "";
        }

        public static string FormatKey(Key key, bool ctrl, bool shift, bool alt)
        {
            if (key == Key.None) return "None";

            var parts = new List<string>();
            if (ctrl) parts.Add("Ctrl");
            if (alt) parts.Add("Alt");
            if (shift) parts.Add("Shift");

            string keyName = key switch
            {
                Key.Equal => "+",
                Key.KpAdd => "+",
                Key.Minus => "-",
                Key.KpSubtract => "-",
                Key.Bracketleft => "[",
                Key.Bracketright => "]",
                Key.Period => ".",
                Key.Comma => ",",
                Key.Up => "Up",
                Key.Down => "Down",
                Key.Left => "Left",
                Key.Right => "Right",
                Key.Pageup => "Page Up",
                Key.Pagedown => "Page Down",
                _ => key.ToString()
            };

            parts.Add(keyName);
            return string.Join(" + ", parts);
        }

        public static string GetFullPainterCheatsheet()
        {
            EnsureInitialized();
            return $"[{GetShortcutText("paint_brush")}] Brush  [{GetShortcutText("paint_eraser")}] Erase  [{GetShortcutText("paint_eyedropper")}] Pick  [{GetShortcutText("paint_bucket")}] Fill  [{GetShortcutText("paint_wand")}] Wand  [{GetShortcutText("paint_decal")}] Decal  [{GetShortcutText("paint_text")}] Text  [{GetShortcutText("paint_mirror")}] Mirror  [{GetShortcutText("brush_size_down")}/{GetShortcutText("brush_size_up")}] Size  [{GetShortcutText("paint_uv_toggle")}] 2D UV";
        }

        public static string GetNavigationCheatsheet()
        {
            EnsureInitialized();
            string up = GetShortcutText("camera_pan_up");
            string down = GetShortcutText("camera_pan_down");
            string left = GetShortcutText("camera_pan_left");
            string right = GetShortcutText("camera_pan_right");
            string eleUp = GetShortcutText("camera_elevate_up");
            string eleDown = GetShortcutText("camera_elevate_down");

            string moveStr = $"{up}{left}{down}{right}";
            if (!string.IsNullOrEmpty(eleDown) && !string.IsNullOrEmpty(eleUp) && eleDown != "None" && eleUp != "None")
            {
                moveStr += $" / {eleDown}{eleUp}";
            }

            return $"Rotate: MMB | Pan: Shift+MMB | Move: {moveStr} | Zoom: Scroll";
        }
    }
}
