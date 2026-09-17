using Godot;
using System;

/// <summary>
/// Manages 3D studio viewport environment, sky, lighting presets, and background modes.
/// </summary>
public static class SceneEnvironmentManager
{
    private static Sky _activeStudioSky;

    public static Sky ActiveStudioSky
    {
        get => _activeStudioSky;
        set => _activeStudioSky = value;
    }

    /// <summary>
    /// Ensures active sky is initialized from existing environment sky or creates a default studio sky.
    /// </summary>
    public static Sky EnsureStudioSky(WorldEnvironment worldEnv = null)
    {
        if (_activeStudioSky != null) return _activeStudioSky;

        if (worldEnv?.Environment?.Sky != null)
        {
            _activeStudioSky = worldEnv.Environment.Sky;
            return _activeStudioSky;
        }

        var skyMat = new ProceduralSkyMaterial
        {
            SkyHorizonColor = new Color(0.18f, 0.20f, 0.24f, 1f),
            SkyTopColor = new Color(0.08f, 0.09f, 0.12f, 1f),
            GroundBottomColor = new Color(0.04f, 0.05f, 0.06f, 1f),
            GroundHorizonColor = new Color(0.12f, 0.13f, 0.15f, 1f),
            SunAngleMax = 30.0f
        };

        _activeStudioSky = new Sky { SkyMaterial = skyMat };
        return _activeStudioSky;
    }

    /// <summary>
    /// Applies studio background and viewport settings based on UserSettings preferences.
    /// </summary>
    public static void ApplyEnvironment(
        Viewport mainViewport,
        SubViewport worldViewport,
        WorldEnvironment worldEnv,
        ColorRect bgRect = null,
        TextureRect bgTextureRect = null,
        Node3D stagePlatform = null)
    {
        // Root window viewport must NEVER have transparent_bg enabled
        if (mainViewport != null)
        {
            mainViewport.TransparentBg = false;
        }

        bool isTransparentMode = UserSettings.StudioEnvironment == UserSettings.StudioEnvMode.TransparentViewport;
        bool hasCustom2dBg = bgTextureRect != null && bgTextureRect.Texture != null && bgTextureRect.Visible;

        // If the user wants 2D background images visible, or transparent export mode,
        // the 3D SubViewport MUST be transparent so the layers underneath show through.
        bool need3dViewportTransparency = isTransparentMode || (UserSettings.ShowStudioBackground && (bgTextureRect != null && bgTextureRect.Visible));

        if (worldViewport != null)
        {
            worldViewport.TransparentBg = need3dViewportTransparency;
        }

        if (worldEnv?.Environment != null)
        {
            EnsureStudioSky(worldEnv);

            if (isTransparentMode)
            {
                worldEnv.Environment.BackgroundMode = Godot.Environment.BGMode.ClearColor;
                worldEnv.Environment.BackgroundColor = new Color(0f, 0f, 0f, 0f);
            }
            else if (need3dViewportTransparency)
            {
                // When showing a 2D Background texture/color behind the 3D viewport,
                // the 3D environment clear color must be transparent.
                worldEnv.Environment.BackgroundMode = Godot.Environment.BGMode.ClearColor;
                worldEnv.Environment.BackgroundColor = new Color(0f, 0f, 0f, 0f);
                worldEnv.Environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
                worldEnv.Environment.AmbientLightColor = new Color(0.35f, 0.36f, 0.38f, 1.0f);
                worldEnv.Environment.AmbientLightEnergy = 1.0f;
            }
            else if (UserSettings.ShowStudioBackground)
            {
                if (UserSettings.StudioEnvironment == UserSettings.StudioEnvMode.DarkStudio)
                {
                    worldEnv.Environment.BackgroundMode = Godot.Environment.BGMode.Sky;
                    worldEnv.Environment.Sky = _activeStudioSky;
                    worldEnv.Environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
                    worldEnv.Environment.AmbientLightColor = new Color(0.28f, 0.29f, 0.32f, 1.0f);
                    worldEnv.Environment.AmbientLightEnergy = 1.0f;
                }
                else // GreyBackdrop
                {
                    worldEnv.Environment.BackgroundMode = Godot.Environment.BGMode.ClearColor;
                    worldEnv.Environment.BackgroundColor = new Color(0.35f, 0.36f, 0.38f, 1.0f);
                    worldEnv.Environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
                    worldEnv.Environment.AmbientLightColor = new Color(0.35f, 0.35f, 0.38f, 1.0f);
                    worldEnv.Environment.AmbientLightEnergy = 1.0f;
                }
            }
            else
            {
                // Studio background disabled: neutral dark viewport
                worldEnv.Environment.BackgroundMode = Godot.Environment.BGMode.ClearColor;
                worldEnv.Environment.BackgroundColor = new Color(0.12f, 0.12f, 0.14f, 1.0f);
            }
        }

        // 2D Background Controls
        if (bgRect != null)
        {
            bool show2dBg = !isTransparentMode && UserSettings.ShowStudioBackground;
            bgRect.Visible = show2dBg;
            if (show2dBg)
            {
                bgRect.Color = UserSettings.StudioEnvironment switch
                {
                    UserSettings.StudioEnvMode.GreyBackdrop => new Color(0.35f, 0.36f, 0.38f, 1.0f),
                    _ => new Color(0.08f, 0.09f, 0.11f, 1.0f) // Dark Studio
                };
            }
        }

        // Texture and 2D background visibility
        if (bgTextureRect != null)
        {
            bgTextureRect.Visible = !isTransparentMode && UserSettings.ShowStudioBackground;
        }

        // 3D Floor/Stage Platform:
        if (stagePlatform != null)
        {
            bool hasActive2dTexture = bgTextureRect != null && bgTextureRect.Visible && bgTextureRect.Texture != null;
            stagePlatform.Visible = !isTransparentMode && UserSettings.ShowStudioBackground && !hasActive2dTexture;
        }
    }
}