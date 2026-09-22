using Godot;
using System;

/// <summary>
/// Global configuration and event bus for 3D Viewport X-Ray lines and bone gizmo appearance.
/// Persists user preferences to 'user://settings.cfg'.
/// </summary>
public static class GizmoDisplaySettings
{
    private const string ConfigPath = "user://settings.cfg";
    private const string SectionName = "GizmoDisplay";

    private static bool _isLoading = false;

    private static Color _xRayLineColor = new Color(0.15f, 0.85f, 1.0f);
    public static Color XRayLineColor
    {
        get => _xRayLineColor;
        set
        {
            _xRayLineColor = value;
            if (!_isLoading) NotifyChanged();
        }
    }

    private static float _xRayLineOpacity = 0.75f;
    public static float XRayLineOpacity
    {
        get => _xRayLineOpacity;
        set
        {
            _xRayLineOpacity = Mathf.Clamp(value, 0.05f, 1.0f);
            if (!_isLoading) NotifyChanged();
        }
    }

    private static Color _bonePrimaryColor = new Color(0.15f, 0.85f, 1.0f);
    public static Color BonePrimaryColor
    {
        get => _bonePrimaryColor;
        set
        {
            _bonePrimaryColor = value;
            if (!_isLoading) NotifyChanged();
        }
    }

    private static Color _boneClothingColor = new Color(1.0f, 0.35f, 0.75f);
    public static Color BoneClothingColor
    {
        get => _boneClothingColor;
        set
        {
            _boneClothingColor = value;
            if (!_isLoading) NotifyChanged();
        }
    }

    private static float _boneMarkerOpacity = 0.75f;
    public static float BoneMarkerOpacity
    {
        get => _boneMarkerOpacity;
        set
        {
            _boneMarkerOpacity = Mathf.Clamp(value, 0.05f, 1.0f);
            if (!_isLoading) NotifyChanged();
        }
    }

    private static float _boneMarkerScale = 1.0f;
    public static float BoneMarkerScale
    {
        get => _boneMarkerScale;
        set
        {
            _boneMarkerScale = Mathf.Clamp(value, 0.2f, 3.0f);
            if (!_isLoading) NotifyChanged();
        }
    }

    private static float _ikHandlesOpacity = 0.70f;
    public static float IKHandlesOpacity
    {
        get => _ikHandlesOpacity;
        set
        {
            _ikHandlesOpacity = Mathf.Clamp(value, 0.05f, 1.0f);
            if (!_isLoading) NotifyChanged();
        }
    }

    private static Color _painterOutlineColor = new Color(1.0f, 0.85f, 0.35f, 1.0f);
    public static Color PainterOutlineColor
    {
        get => _painterOutlineColor;
        set
        {
            _painterOutlineColor = value;
            if (!_isLoading) NotifyChanged();
        }
    }

    private static float _painterOutlineOpacity = 0.40f;
    public static float PainterOutlineOpacity
    {
        get => _painterOutlineOpacity;
        set
        {
            _painterOutlineOpacity = Mathf.Clamp(value, 0.05f, 1.0f);
            if (!_isLoading) NotifyChanged();
        }
    }

    private static float _painterOutlineWidth = 0.7f;
    public static float PainterOutlineWidth
    {
        get => _painterOutlineWidth;
        set
        {
            _painterOutlineWidth = Mathf.Clamp(value, 0.1f, 3.0f);
            if (!_isLoading) NotifyChanged();
        }
    }

    /// <summary>
    /// Converts linear opacity [0.0, 1.0] to a perceptual quadratic/cubic curve alpha = pow(x, 1.8).
    /// Ensures 5% setting renders as a faint hairline, and 50% remains translucent.
    /// </summary>
    public static float ToCurvedAlpha(float opacityValue)
    {
        return Mathf.Pow(Mathf.Clamp(opacityValue, 0.0f, 1.0f), 1.8f);
    }

    public static float XRayLineAlpha => ToCurvedAlpha(_xRayLineOpacity);
    public static float BoneMarkerAlpha => ToCurvedAlpha(_boneMarkerOpacity);
    public static float IKHandlesAlpha => ToCurvedAlpha(_ikHandlesOpacity);
    public static float PainterOutlineAlpha => ToCurvedAlpha(_painterOutlineOpacity);

    public static event Action OnSettingsChanged;

    public static void NotifyChanged()
    {
        OnSettingsChanged?.Invoke();
        SaveConfig();
    }

    public static void ResetToDefaults()
    {
        _isLoading = true;
        XRayLineColor = new Color(0.15f, 0.85f, 1.0f);
        XRayLineOpacity = 0.75f;
        BonePrimaryColor = new Color(0.15f, 0.85f, 1.0f);
        BoneClothingColor = new Color(1.0f, 0.35f, 0.75f);
        BoneMarkerOpacity = 0.75f;
        BoneMarkerScale = 1.0f;
        IKHandlesOpacity = 0.70f;
        PainterOutlineColor = new Color(1.0f, 0.85f, 0.35f, 1.0f);
        PainterOutlineOpacity = 0.40f;
        PainterOutlineWidth = 0.7f;
        _isLoading = false;

        NotifyChanged();
    }

    static GizmoDisplaySettings()
    {
        LoadConfig();
    }

    public static void LoadConfig()
    {
        var config = new ConfigFile();
        if (config.Load(ConfigPath) == Error.Ok)
        {
            _isLoading = true;
            try
            {
                if (config.HasSectionKey(SectionName, "XRayLineColor"))
                    _xRayLineColor = (Color)config.GetValue(SectionName, "XRayLineColor", _xRayLineColor);

                if (config.HasSectionKey(SectionName, "XRayLineOpacity"))
                    _xRayLineOpacity = Mathf.Clamp((float)config.GetValue(SectionName, "XRayLineOpacity", _xRayLineOpacity), 0.05f, 1.0f);

                if (config.HasSectionKey(SectionName, "BonePrimaryColor"))
                    _bonePrimaryColor = (Color)config.GetValue(SectionName, "BonePrimaryColor", _bonePrimaryColor);

                if (config.HasSectionKey(SectionName, "BoneClothingColor"))
                    _boneClothingColor = (Color)config.GetValue(SectionName, "BoneClothingColor", _boneClothingColor);

                if (config.HasSectionKey(SectionName, "BoneMarkerOpacity"))
                    _boneMarkerOpacity = Mathf.Clamp((float)config.GetValue(SectionName, "BoneMarkerOpacity", _boneMarkerOpacity), 0.05f, 1.0f);

                if (config.HasSectionKey(SectionName, "BoneMarkerScale"))
                    _boneMarkerScale = Mathf.Clamp((float)config.GetValue(SectionName, "BoneMarkerScale", _boneMarkerScale), 0.2f, 3.0f);

                if (config.HasSectionKey(SectionName, "IKHandlesOpacity"))
                    _ikHandlesOpacity = Mathf.Clamp((float)config.GetValue(SectionName, "IKHandlesOpacity", _ikHandlesOpacity), 0.05f, 1.0f);

                if (config.HasSectionKey(SectionName, "PainterOutlineColor"))
                    _painterOutlineColor = (Color)config.GetValue(SectionName, "PainterOutlineColor", _painterOutlineColor);

                if (config.HasSectionKey(SectionName, "PainterOutlineOpacity"))
                    _painterOutlineOpacity = Mathf.Clamp((float)config.GetValue(SectionName, "PainterOutlineOpacity", _painterOutlineOpacity), 0.05f, 1.0f);

                if (config.HasSectionKey(SectionName, "PainterOutlineWidth"))
                    _painterOutlineWidth = Mathf.Clamp((float)config.GetValue(SectionName, "PainterOutlineWidth", _painterOutlineWidth), 0.1f, 3.0f);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[GizmoDisplaySettings] Error loading config from {ConfigPath}: {ex.Message}");
            }
            finally
            {
                _isLoading = false;
            }

            OnSettingsChanged?.Invoke();
        }
    }

    public static void SaveConfig()
    {
        var config = new ConfigFile();
        config.Load(ConfigPath); // preserve other sections

        config.SetValue(SectionName, "XRayLineColor", XRayLineColor);
        config.SetValue(SectionName, "XRayLineOpacity", XRayLineOpacity);
        config.SetValue(SectionName, "BonePrimaryColor", BonePrimaryColor);
        config.SetValue(SectionName, "BoneClothingColor", BoneClothingColor);
        config.SetValue(SectionName, "BoneMarkerOpacity", BoneMarkerOpacity);
        config.SetValue(SectionName, "BoneMarkerScale", BoneMarkerScale);
        config.SetValue(SectionName, "IKHandlesOpacity", IKHandlesOpacity);
        config.SetValue(SectionName, "PainterOutlineColor", PainterOutlineColor);
        config.SetValue(SectionName, "PainterOutlineOpacity", PainterOutlineOpacity);
        config.SetValue(SectionName, "PainterOutlineWidth", PainterOutlineWidth);

        Error err = config.Save(ConfigPath);
        if (err != Error.Ok)
        {
            GD.PrintErr($"[GizmoDisplaySettings] Failed to save config to {ConfigPath}: {err}");
        }
    }
}
