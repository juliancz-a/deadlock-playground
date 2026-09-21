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

    public static void ApplyEnvironment(
        Viewport mainViewport,
        SubViewport worldViewport,
        WorldEnvironment worldEnv,
        ColorRect bgRect = null,
        TextureRect bgTextureRect = null,
        Node3D stagePlatform = null)
    {
        if (mainViewport != null)
        {
            mainViewport.TransparentBg = false;
        }

        bool showBg = UserSettings.ShowStudioBackground;
        bool hasTexture = bgTextureRect != null && bgTextureRect.Texture != null;
        bool is3DStage = stagePlatform != null && stagePlatform.Visible;

        // Si el usuario quiere ver el fondo (2D canvas con imagen o color)
        if (showBg && !is3DStage)
        {
            // El SubViewport 3D debe ser transparente para mostrar el canvas detrás
            if (worldViewport != null)
            {
                worldViewport.TransparentBg = true;
            }

            if (worldEnv?.Environment != null)
            {
                worldEnv.Environment.BackgroundMode = Godot.Environment.BGMode.ClearColor;
                worldEnv.Environment.BackgroundColor = new Color(0f, 0f, 0f, 0f);
                worldEnv.Environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
                worldEnv.Environment.AmbientLightColor = new Color(0.35f, 0.36f, 0.38f, 1.0f);
                worldEnv.Environment.AmbientLightEnergy = 1.0f;
            }

            if (bgTextureRect != null) bgTextureRect.Visible = hasTexture;
            if (bgRect != null) bgRect.Visible = true;
            if (stagePlatform != null) stagePlatform.Visible = false;
        }
        else if (showBg && is3DStage)
        {
            // Modo estudio 3D completo con piso
            if (worldViewport != null)
            {
                worldViewport.TransparentBg = false;
            }

            if (worldEnv?.Environment != null)
            {
                worldEnv.Environment.BackgroundMode = Godot.Environment.BGMode.Sky;
                worldEnv.Environment.Sky = EnsureStudioSky(worldEnv);
                worldEnv.Environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
                worldEnv.Environment.AmbientLightColor = new Color(0.28f, 0.29f, 0.32f, 1.0f);
                worldEnv.Environment.AmbientLightEnergy = 1.0f;
            }

            if (bgTextureRect != null) bgTextureRect.Visible = false;
            if (bgRect != null) bgRect.Visible = false;
        }
        else
        {
            // Fondo desactivado: viewport oscuro neutral
            if (worldViewport != null)
            {
                worldViewport.TransparentBg = false;
            }

            if (worldEnv?.Environment != null)
            {
                worldEnv.Environment.BackgroundMode = Godot.Environment.BGMode.ClearColor;
                worldEnv.Environment.BackgroundColor = new Color(0.08f, 0.09f, 0.11f, 1.0f);
            }

            if (bgTextureRect != null) bgTextureRect.Visible = false;
            if (bgRect != null) bgRect.Visible = false;
            if (stagePlatform != null) stagePlatform.Visible = false;
        }
    }
}