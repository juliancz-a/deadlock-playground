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

    public static Color XRayLineColor { get; set; } = new Color(0.15f, 0.85f, 1.0f);
    public static float XRayLineOpacity { get; set; } = 0.4f;

    public static Color BonePrimaryColor { get; set; } = new Color(0.15f, 0.85f, 1.0f);
    public static Color BoneClothingColor { get; set; } = new Color(1.0f, 0.35f, 0.75f);
    public static float BoneMarkerOpacity { get; set; } = 0.75f;
    public static float BoneMarkerScale { get; set; } = 1.0f;

    public static event Action OnSettingsChanged;

    public static void NotifyChanged()
    {
        OnSettingsChanged?.Invoke();
        SaveConfig();
    }

    public static void ResetToDefaults()
    {
        XRayLineColor = new Color(0.15f, 0.85f, 1.0f);
        XRayLineOpacity = 0.4f;

        BonePrimaryColor = new Color(0.15f, 0.85f, 1.0f);
        BoneClothingColor = new Color(1.0f, 0.35f, 0.75f);
        BoneMarkerOpacity = 0.75f;
        BoneMarkerScale = 1.0f;

        NotifyChanged();
    }

    public static void LoadConfig()
    {
        var config = new ConfigFile();
        if (config.Load(ConfigPath) == Error.Ok)
        {
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

        config.Save(ConfigPath);
    }
}
