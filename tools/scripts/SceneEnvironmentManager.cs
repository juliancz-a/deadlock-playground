using Godot;
using System;

/// <summary>
/// Manages 3D studio viewport environment, sky, lighting presets, and background modes.
/// </summary>
public static class SceneEnvironmentManager
{
    private static Sky _activeStudioSky;

    /// <summary>
    /// When true, ApplyEnvironment() will configure the world viewport for a fully transparent
    /// background (alpha=0) rather than showing any 2D canvas rect or solid color.
    /// Set by SceneTabUI when the user selects BgMode.Transparent.
    /// </summary>
    public static bool IsTransparentMode { get; set; } = false;

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

        // Transparent mode: user wants a true alpha-transparent PNG background.
        // Override everything and ensure the world viewport is fully transparent.
        if (IsTransparentMode)
        {
            if (worldViewport != null)
            {
                worldViewport.TransparentBg = true;
            }

            if (worldEnv?.Environment != null)
            {
                worldEnv.Environment.BackgroundMode = Godot.Environment.BGMode.ClearColor;
                worldEnv.Environment.BackgroundColor = new Color(0f, 0f, 0f, 0f);
                EnsureAmbientLighting(worldEnv.Environment);
            }

            // Explicitly hide both background rects so no image bleeds through
            if (bgTextureRect != null) bgTextureRect.Visible = false;
            if (bgRect != null) bgRect.Visible = false;
            if (stagePlatform != null) stagePlatform.Visible = false;
            return;
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
                EnsureAmbientLighting(worldEnv.Environment);
            }

            // Only show bgTextureRect if it has actual texture content;
            // bgRect is intentionally NOT forced visible here — SceneTabUI manages its own visibility.
            if (bgTextureRect != null) bgTextureRect.Visible = hasTexture;
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
                EnsureAmbientLighting(worldEnv.Environment);
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
                EnsureAmbientLighting(worldEnv.Environment);
            }

            if (bgTextureRect != null) bgTextureRect.Visible = false;
            if (bgRect != null) bgRect.Visible = false;
            if (stagePlatform != null) stagePlatform.Visible = false;
        }
    }

    private static void EnsureAmbientLighting(Godot.Environment env)
    {
        if (env == null) return;

        if (env.AmbientLightSource != Godot.Environment.AmbientSource.Color)
        {
            env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        }

        // Only supply an initial fallback if ambient light color is completely uninitialized (0,0,0,0)
        if (env.AmbientLightColor.A == 0f &&
            env.AmbientLightColor.R == 0f &&
            env.AmbientLightColor.G == 0f &&
            env.AmbientLightColor.B == 0f)
        {
            env.AmbientLightColor = new Color(0.35f, 0.36f, 0.38f, 1.0f);
        }

        if (env.AmbientLightEnergy <= 0f)
        {
            env.AmbientLightEnergy = 1.0f;
        }
    }
}