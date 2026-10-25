using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace DeadlockPlayground.Maps.Editor;

/// <summary>
/// Data container for a Deadlock custom map project (.dpmap JSON schema).
/// Stores the complete map layout, placed props, transforms, visual tints, and entity configurations.
/// </summary>
public class MapProject
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("mapName")]
    public string MapName { get; set; } = "custom_deadlock_arena";

    /// <summary>
    /// "blank" for Blank Canvas, or "dl_hideout" for Hideout modification.
    /// </summary>
    [JsonPropertyName("baseStageMode")]
    public string BaseStageMode { get; set; } = "blank";

    [JsonPropertyName("presetName")]
    public string PresetName { get; set; } = "Lobby Center Spawn (Default)";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("modifiedAt")]
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("objects")]
    public List<MapObjectData> Objects { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string SerializeToJson()
    {
        ModifiedAt = DateTime.UtcNow;
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    public static MapProject DeserializeFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<MapProject>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MapProject] Failed to deserialize .dpmap project: {ex.Message}");
            return null;
        }
    }

    public bool SaveToFile(string filePath)
    {
        try
        {
            string globalPath = ProjectSettings.GlobalizePath(filePath);
            string dir = Path.GetDirectoryName(globalPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = SerializeToJson();
            File.WriteAllText(globalPath, json);
            GD.Print($"[MapProject] Project saved successfully to: {globalPath} ({Objects.Count} objects)");
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MapProject] Failed to save project to {filePath}: {ex.Message}");
            return false;
        }
    }

    public static MapProject LoadFromFile(string filePath)
    {
        string globalPath = ProjectSettings.GlobalizePath(filePath);
        if (!File.Exists(globalPath))
        {
            GD.PrintErr($"[MapProject] File does not exist: {globalPath}");
            return null;
        }

        string json = File.ReadAllText(globalPath);
        var proj = DeserializeFromJson(json);
        if (proj != null)
        {
            GD.Print($"[MapProject] Project loaded successfully from: {globalPath} ({proj.Objects.Count} objects)");
        }
        return proj;
    }
}
