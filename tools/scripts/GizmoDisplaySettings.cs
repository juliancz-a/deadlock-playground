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
        _isLoading = false;

        NotifyChanged();
    }

    public static void LoadConfig()
    {
        var config = new ConfigFile();
        if (config.Load(ConfigPath) == Error.Ok)
        {
            _isLoading = true;
            if (config.HasSectionKey(SectionName, "XRayLineColor"))
                XRayLineColor = (Color)config.GetValue(SectionName, "XRayLineColor", XRayLineColor);

            if (config.HasSectionKey(SectionName, "XRayLineOpacity"))
                XRayLineOpacity = (float)config.GetValue(SectionName, "XRayLineOpacity", XRayLineOpacity);

            if (config.HasSectionKey(SectionName, "BonePrimaryColor"))
                BonePrimaryColor = (Color)config.GetValue(SectionName, "BonePrimaryColor", BonePrimaryColor);

            if (config.HasSectionKey(SectionName, "BoneClothingColor"))
                BoneClothingColor = (Color)config.GetValue(SectionName, "BoneClothingColor", BoneClothingColor);

            if (config.HasSectionKey(SectionName, "BoneMarkerOpacity"))
                BoneMarkerOpacity = (float)config.GetValue(SectionName, "BoneMarkerOpacity", BoneMarkerOpacity);

            if (config.HasSectionKey(SectionName, "BoneMarkerScale"))
                BoneMarkerScale = (float)config.GetValue(SectionName, "BoneMarkerScale", BoneMarkerScale);

            if (config.HasSectionKey(SectionName, "IKHandlesOpacity"))
                IKHandlesOpacity = (float)config.GetValue(SectionName, "IKHandlesOpacity", IKHandlesOpacity);
            _isLoading = false;
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

        config.Save(ConfigPath);
    }
}
