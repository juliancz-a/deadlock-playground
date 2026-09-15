using Godot;
using System;

/// <summary>
/// Global configuration and event bus for application-wide user preferences.
/// Persists settings to 'user://settings.cfg'.
/// </summary>
public static class UserSettings
{
    private const string ConfigPath = "user://settings.cfg";
    private const string SectionName = "UserSettings";

    private static bool _toonEnabled = false;

    public static bool ToonEnabled
    {
        get => _toonEnabled;
        set
        {
            if (_toonEnabled != value)
            {
                _toonEnabled = value;
                SaveSettings();
                ToonEnabledChanged?.Invoke(value);
            }
        }
    }

    public static event Action<bool> ToonEnabledChanged;

    static UserSettings()
    {
        LoadSettings();
    }

    public static void LoadSettings()
    {
        var config = new ConfigFile();
        if (config.Load(ConfigPath) == Error.Ok)
        {
            _toonEnabled = (bool)config.GetValue(SectionName, "ToonEnabled", false);
        }
    }

    public static void SaveSettings()
    {
        var config = new ConfigFile();
        config.Load(ConfigPath); // Retain other sections like GizmoDisplay or GamePaths
        config.SetValue(SectionName, "ToonEnabled", _toonEnabled);
        config.Save(ConfigPath);
    }
}
