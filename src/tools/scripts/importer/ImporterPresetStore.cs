using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using DeadlockPlayground.Workspace;

namespace DeadlockPlayground.Importer
{
    /// <summary>
    /// Phase 5: Saves and loads importer RiggingOptions presets per model or per hero
    /// under user://importer_presets/ using WorkspaceCustomModelEntry JSON serialization.
    /// </summary>
    public static class ImporterPresetStore
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static string GetPresetsDirectory()
        {
            string dir = ProjectSettings.GlobalizePath("user://importer_presets");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        public static string SanitizePresetName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "default";
            var chars = raw.Trim().ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_')
                .ToArray();
            string clean = new string(chars).Trim('_');
            return string.IsNullOrEmpty(clean) ? "default" : clean;
        }

        public static string MakeModelPresetKey(string filePath)
            => "model_" + SanitizePresetName(Path.GetFileNameWithoutExtension(filePath ?? "model"));

        public static string MakeHeroPresetKey(string heroCodename)
            => "hero_" + SanitizePresetName(string.IsNullOrWhiteSpace(heroCodename) ? "hero" : heroCodename);

        public static bool SavePreset(string presetKey, CustomModelAutoRigger.RiggingOptions options, string sourceFilePath = "")
        {
            if (string.IsNullOrWhiteSpace(presetKey) || options == null) return false;
            try
            {
                string safe = SanitizePresetName(presetKey);
                string path = Path.Combine(GetPresetsDirectory(), safe + ".json");
                var entry = WorkspaceCustomModelEntry.FromRiggingOptions(sourceFilePath ?? "", Path.GetFileName(sourceFilePath ?? ""), options);
                string json = JsonSerializer.Serialize(entry, JsonOpts);
                File.WriteAllText(path, json);
                GD.Print($"[ImporterPresetStore] Saved preset '{safe}' -> {path}");
                return true;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ImporterPresetStore] Failed saving preset '{presetKey}': {ex.Message}");
                return false;
            }
        }

        public static CustomModelAutoRigger.RiggingOptions LoadPreset(string presetKey)
        {
            if (string.IsNullOrWhiteSpace(presetKey)) return null;
            try
            {
                string safe = SanitizePresetName(presetKey);
                string path = Path.Combine(GetPresetsDirectory(), safe + ".json");
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path);
                var entry = JsonSerializer.Deserialize<WorkspaceCustomModelEntry>(json, JsonOpts);
                return entry?.ToRiggingOptions();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ImporterPresetStore] Failed loading preset '{presetKey}': {ex.Message}");
                return null;
            }
        }

        public static List<string> ListPresetKeys()
        {
            var keys = new List<string>();
            try
            {
                string dir = GetPresetsDirectory();
                if (!Directory.Exists(dir)) return keys;
                foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    keys.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
            catch { /* ignore */ }
            return keys;
        }

        private sealed class ImporterUiSettingsDto
        {
            public int UiScalePercent { get; set; } = 100;
        }

        private static string GetUiSettingsPath()
            => ProjectSettings.GlobalizePath("user://importer_ui_settings.json");

        public static int LoadUiScalePercent()
        {
            try
            {
                string path = GetUiSettingsPath();
                if (!File.Exists(path)) return 100;
                var dto = JsonSerializer.Deserialize<ImporterUiSettingsDto>(File.ReadAllText(path), JsonOpts);
                int pct = dto?.UiScalePercent ?? 100;
                return pct is 100 or 115 or 130 ? pct : 100;
            }
            catch
            {
                return 100;
            }
        }

        public static void SaveUiScalePercent(int percent)
        {
            int clamped = percent is 100 or 115 or 130 ? percent : 100;
            try
            {
                string path = GetUiSettingsPath();
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonSerializer.Serialize(new ImporterUiSettingsDto { UiScalePercent = clamped }, JsonOpts));
            }
            catch { /* ignore */ }
        }
    }
}

