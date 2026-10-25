using System;
using System.Collections.Generic;
using Godot;
using ValveResourceFormat.ResourceTypes;
using ValveKeyValue;

namespace DeadlockPlayground.Maps;

/// <summary>
/// Synthesizes real-time Godot 3D lights (OmniLight3D, SpotLight3D, DirectionalLight3D)
/// directly from Source 2 entity lump definitions (light_omni2, light_barn, light_environment).
/// </summary>
public static class Source2LightSynthesizer
{
    /// <summary>
    /// Builds a Godot Node3D containing all synthesized lights from the map entity lump,
    /// optionally filtered within a spatial radius around a center point.
    /// </summary>
    public static Node3D SynthesizeLights(
        IEnumerable<EntityLump.Entity> entities,
        Vector3? filterCenterGodot = null,
        float filterRadiusMeters = -1f)
    {
        var lightsRoot = new Node3D { Name = "StageLightsRoot" };
        if (entities == null) return lightsRoot;

        int omniCount = 0;
        int spotCount = 0;
        int envCount = 0;

        foreach (var ent in entities)
        {
            string cls = ent.TryGetValue("classname", out var cVal) && cVal != null ? cVal.ToString() : "";
            if (string.IsNullOrEmpty(cls)) continue;

            var sOrigin = ent.GetVector3Property("origin", System.Numerics.Vector3.Zero);
            var gOrigin = MapCoordinateUtils.SourceToGodotPoint(sOrigin);

            bool isGlobalLight = cls.Equals("light_environment", StringComparison.OrdinalIgnoreCase);

            // Spatial radius check if filtering is active (directional environment light is global)
            if (!isGlobalLight && filterCenterGodot.HasValue && filterRadiusMeters > 0f)
            {
                if (gOrigin.DistanceTo(filterCenterGodot.Value) > filterRadiusMeters)
                {
                    continue;
                }
            }

            var sAngles = ent.GetVector3Property("angles", System.Numerics.Vector3.Zero);
            var gEuler = MapCoordinateUtils.SourceAnglesToGodotEuler(sAngles.X, sAngles.Y, sAngles.Z);

            var sColor = ent.GetColor32Property("color");
            var gColor = new Color(sColor.X, sColor.Y, sColor.Z);

            float brightnessVal = ParseFloat(ent, "brightness", ParseFloat(ent, "intensity", 1.0f));
            float rangeVal = ParseFloat(ent, "range", 300.0f);
            float gRange = Mathf.Max(1.0f, rangeVal * MapCoordinateUtils.InchesToMeters);

            // 1. Point / Omni Lights (light_omni2)
            if (cls.Equals("light_omni2", StringComparison.OrdinalIgnoreCase) ||
                cls.Equals("light_omni", StringComparison.OrdinalIgnoreCase))
            {
                float energy = ConvertSourceBrightness(brightnessVal);

                var omni = new OmniLight3D
                {
                    Name = $"OmniLight_{omniCount++}",
                    Position = gOrigin,
                    LightColor = gColor,
                    LightEnergy = energy,
                    OmniRange = gRange,
                    OmniAttenuation = 1.0f,
                    ShadowEnabled = (energy > 1.5f && gRange > 8.0f && omniCount <= 8) // Limit shadow casters for performance
                };
                lightsRoot.AddChild(omni);
            }
            // 2. Spot / Barn-door Lights (light_barn)
            else if (cls.Equals("light_barn", StringComparison.OrdinalIgnoreCase) ||
                     cls.Equals("light_spot", StringComparison.OrdinalIgnoreCase))
            {
                float energy = ConvertSourceBrightness(brightnessVal);

                var spot = new SpotLight3D
                {
                    Name = $"SpotLight_{spotCount++}",
                    Position = gOrigin,
                    Rotation = gEuler,
                    LightColor = gColor,
                    LightEnergy = energy,
                    SpotRange = gRange,
                    SpotAngle = 55.0f,
                    SpotAttenuation = 1.0f,
                    ShadowEnabled = (energy > 2.0f && spotCount <= 6)
                };
                lightsRoot.AddChild(spot);
            }
            // 3. Directional Sun/Sky Light (light_environment)
            else if (cls.Equals("light_environment", StringComparison.OrdinalIgnoreCase) && envCount == 0)
            {
                envCount++;
                var dirLight = new DirectionalLight3D
                {
                    Name = "EnvironmentSunLight",
                    Rotation = gEuler,
                    LightColor = gColor,
                    LightEnergy = Mathf.Clamp(ConvertSourceBrightness(brightnessVal), 0.5f, 3.0f),
                    ShadowEnabled = true,
                    ShadowBias = 0.05f
                };
                lightsRoot.AddChild(dirLight);
            }
        }

        // 4. Synthesize Default Exterior Sun / Atmosphere if map entity lump had no light_environment
        if (envCount == 0)
        {
            var defaultSun = new DirectionalLight3D
            {
                Name = "EnvironmentSunLight",
                RotationDegrees = new Vector3(-45.0f, 35.0f, 0.0f),
                LightColor = new Color(0.98f, 0.95f, 0.88f),
                LightEnergy = 1.6f,
                ShadowEnabled = true,
                ShadowBias = 0.05f
            };
            lightsRoot.AddChild(defaultSun);
        }

        return lightsRoot;
    }

    private static float ConvertSourceBrightness(float b)
    {
        // Source 2 brightness values can be EV stops or linear scale
        if (b > 0f)
        {
            return Mathf.Clamp(Mathf.Pow(1.8f, b) * 0.35f, 0.2f, 6.0f);
        }
        else
        {
            return Mathf.Clamp(1.0f / Mathf.Max(0.1f, 1.0f - b * 0.4f), 0.1f, 1.0f);
        }
    }

    private static float ParseFloat(EntityLump.Entity ent, string key, float fallback)
    {
        if (ent.TryGetValue(key, out var val) && val != null)
        {
            if (float.TryParse(val.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f))
            {
                return f;
            }
        }
        return fallback;
    }
}
