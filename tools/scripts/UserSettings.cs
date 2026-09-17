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

    public enum StudioEnvMode
    {
        DarkStudio = 0,
        GreyBackdrop = 1,
        TransparentViewport = 2
    }

    private static bool _toonEnabled = false;
    private static int _msaa3D = 1; // 0=Disabled, 1=2x, 2=4x, 3=8x
    private static int _shadowQuality = 2; // 0=Off, 1=Low, 2=High
    private static StudioEnvMode _studioEnv = StudioEnvMode.DarkStudio;
    private static bool _showStudioBackground = true;
    private static int _maxFps = 60; // 0 = uncapped
    private static bool _vsync = true;

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

    public static int Msaa3D
    {
        get => _msaa3D;
        set
        {
            if (_msaa3D != value)
            {
                _msaa3D = value;
                SaveSettings();
                Msaa3DChanged?.Invoke(value);
            }
        }
    }

    public static int ShadowQuality
    {
        get => _shadowQuality;
        set
        {
            if (_shadowQuality != value)
            {
                _shadowQuality = value;
                SaveSettings();
                ShadowQualityChanged?.Invoke(value);
            }
        }
    }

    public static StudioEnvMode StudioEnvironment
    {
        get => _studioEnv;
        set
        {
            if (_studioEnv != value)
            {
                _studioEnv = value;
                SaveSettings();
                StudioEnvironmentChanged?.Invoke(value);
            }
        }
    }

    public static bool ShowStudioBackground
    {
        get => _showStudioBackground;
        set
        {
            if (_showStudioBackground != value)
            {
                _showStudioBackground = value;
                SaveSettings();
                ShowStudioBackgroundChanged?.Invoke(value);
            }
        }
    }

    public static int MaxFps
    {
        get => _maxFps;
        set
        {
            if (_maxFps != value)
            {
                _maxFps = value;
                SaveSettings();
                PerformanceSettingsChanged?.Invoke();
            }
        }
    }

    public static bool VSync
    {
        get => _vsync;
        set
        {
            if (_vsync != value)
            {
                _vsync = value;
                SaveSettings();
                PerformanceSettingsChanged?.Invoke();
            }
        }
    }

    public static event Action<bool> ToonEnabledChanged;
    public static event Action<int> Msaa3DChanged;
    public static event Action<int> ShadowQualityChanged;
    public static event Action<StudioEnvMode> StudioEnvironmentChanged;
    public static event Action<bool> ShowStudioBackgroundChanged;
    public static event Action PerformanceSettingsChanged;

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
            _msaa3D = (int)config.GetValue(SectionName, "Msaa3D", 1);
            _shadowQuality = (int)config.GetValue(SectionName, "ShadowQuality", 2);
            _studioEnv = (StudioEnvMode)(int)config.GetValue(SectionName, "StudioEnvironment", 0);
            _showStudioBackground = (bool)config.GetValue(SectionName, "ShowStudioBackground", true);
            _maxFps = (int)config.GetValue(SectionName, "MaxFps", 60);
            _vsync = (bool)config.GetValue(SectionName, "VSync", true);
        }
    }

    public static void SaveSettings()
    {
        var config = new ConfigFile();
        config.Load(ConfigPath); // Retain other sections like GizmoDisplay or GamePaths
        config.SetValue(SectionName, "ToonEnabled", _toonEnabled);
        config.SetValue(SectionName, "Msaa3D", _msaa3D);
        config.SetValue(SectionName, "ShadowQuality", _shadowQuality);
        config.SetValue(SectionName, "StudioEnvironment", (int)_studioEnv);
        config.SetValue(SectionName, "ShowStudioBackground", _showStudioBackground);
        config.SetValue(SectionName, "MaxFps", _maxFps);
        config.SetValue(SectionName, "VSync", _vsync);
        config.Save(ConfigPath);
    }

    public static void ApplyPerformanceSettings()
    {
        Engine.MaxFps = _maxFps;
        DisplayServer.WindowSetVsyncMode(_vsync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
    }

    public static void ApplyGraphicsSettings(
        Viewport mainViewport,
        SubViewport worldViewport,
        DirectionalLight3D dirLight,
        ColorRect bgRect,
        WorldEnvironment worldEnv = null,
        TextureRect bgTextureRect = null,
        Node3D stagePlatform = null)
    {
        // 1. MSAA
        var msaaVal = _msaa3D switch
        {
            1 => Viewport.Msaa.Msaa2X,
            2 => Viewport.Msaa.Msaa4X,
            3 => Viewport.Msaa.Msaa8X,
            _ => Viewport.Msaa.Disabled
        };

        if (mainViewport != null) mainViewport.Msaa3D = msaaVal;
        if (worldViewport != null) worldViewport.Msaa3D = msaaVal;

        // 2. Shadows
        if (dirLight != null)
        {
            dirLight.ShadowEnabled = (_shadowQuality > 0);
        }

        var filterQuality = _shadowQuality switch
        {
            1 => RenderingServer.ShadowQuality.SoftLow,
            2 => RenderingServer.ShadowQuality.SoftHigh,
            _ => RenderingServer.ShadowQuality.Hard
        };
        RenderingServer.DirectionalSoftShadowFilterSetQuality(filterQuality);
        RenderingServer.PositionalSoftShadowFilterSetQuality(filterQuality);

        // 3. Studio Environment & Background
        SceneEnvironmentManager.ApplyEnvironment(mainViewport, worldViewport, worldEnv, bgRect, bgTextureRect, stagePlatform);

        // 4. Performance
        ApplyPerformanceSettings();
    }
}
