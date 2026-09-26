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
            AddDefault("Texture Paint", "paint_bucket", "Bucket Fill", "Switch to Submesh / Island Bucket Fill tool", Key.G);
            AddDefault("Texture Paint", "paint_wand", "Magic Wand", "Switch to Magic Wand color-range mask tool", Key.M);
            AddDefault("Texture Paint", "paint_decal", "Decal Stamper", "Switch to Decal projection stamper", Key.L);
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
            foreach (var kvp in _bindings)
            {
                if (kvp.Key == action) continue;
                var b = kvp.Value;
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

            // Clear conflicting binding on other actions
            foreach (var kvp in _bindings)
            {
                if (kvp.Key == action) continue;
                var b = kvp.Value;
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
            return $"[{GetShortcutText("paint_brush")}] Brush  [{GetShortcutText("paint_eraser")}] Erase  [{GetShortcutText("paint_bucket")}] Fill  [{GetShortcutText("paint_wand")}] Wand  [{GetShortcutText("paint_decal")}] Decal  [{GetShortcutText("paint_text")}] Text  [{GetShortcutText("paint_mirror")}] Mirror  [{GetShortcutText("brush_size_down")}/{GetShortcutText("brush_size_up")}] Size  [{GetShortcutText("paint_uv_toggle")}] 2D UV";
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
