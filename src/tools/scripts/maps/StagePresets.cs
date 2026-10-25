using System;
using System.Collections.Generic;
using Godot;

namespace DeadlockPlayground.Maps;

/// <summary>
/// Data definition for a selectable Hero Stage Spot / Room inside a Deadlock map.
/// </summary>
public class StagePresetDefinition
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public System.Numerics.Vector3 SourcePosition { get; set; }
    public System.Numerics.Vector3 SourceAngles { get; set; }
    public float DefaultRadiusMeters { get; set; } = 35.0f;

    /// <summary>
    /// Calculated Godot 3D world position in meters.
    /// </summary>
    public Vector3 GodotPosition => MapCoordinateUtils.SourceToGodotPoint(SourcePosition);

    /// <summary>
    /// Calculated Godot Euler rotation in radians.
    /// </summary>
    public Vector3 GodotRotation => MapCoordinateUtils.SourceAnglesToGodotEuler(SourceAngles.X, SourceAngles.Y, SourceAngles.Z);
}

/// <summary>
/// Authoritative registry of Deadlock Hideout stage presets with Source 2 coordinates.
/// </summary>
public static class StagePresets
{
    public static readonly IReadOnlyList<StagePresetDefinition> HideoutPresets = new List<StagePresetDefinition>
    {
        new()
        {
            Id = "lobby_spawn",
            DisplayName = "Lobby Spawn (Grand Hall)",
            Description = "Iconic hideout spawn. Grand chandelier overhead, rich wood paneling, and arched doorways.",
            SourcePosition = new(11598f, -6144f, -350f),
            SourceAngles = new(0f, 180f, 0f),
            DefaultRadiusMeters = 35.0f
        },
        new()
        {
            Id = "fortune_teller",
            DisplayName = "Fortune Teller Table",
            Description = "Occult table with crystal ball, skull candles, and moody green atmospheric lighting.",
            SourcePosition = new(11296f, -6094f, -286f),
            SourceAngles = new(0f, 0f, 0f),
            DefaultRadiusMeters = 25.0f
        },
        new()
        {
            Id = "fireplace_lounge",
            DisplayName = "Fireplace Lounge",
            Description = "Cozy seating area with upholstered armchairs, patterned rug, and warm hearth lighting.",
            SourcePosition = new(11492f, -6681f, -186f),
            SourceAngles = new(-5f, 0f, 0f),
            DefaultRadiusMeters = 25.0f
        },
        new()
        {
            Id = "item_shop",
            DisplayName = "Curio Item Shop",
            Description = "Hideout merchant counter with warm lantern fixtures and equipment shelves.",
            SourcePosition = new(11323f, -7100f, -286f),
            SourceAngles = new(0f, 270f, 0f),
            DefaultRadiusMeters = 28.0f
        },
        new()
        {
            Id = "testing_range",
            DisplayName = "Testing Range (Corridor)",
            Description = "Wide industrial testing lane with high ceilings, pipes, and target practice line.",
            SourcePosition = new(11878f, -8048f, -335f),
            SourceAngles = new(0f, 180f, 0f),
            DefaultRadiusMeters = 40.0f
        },
        new()
        {
            Id = "shooting_gallery",
            DisplayName = "Sandbox Shooting Gallery",
            Description = "Extended firing lane with hanging targets, distant bot platforms, and soul orb spawner.",
            SourcePosition = new(11686f, -8528f, -315f),
            SourceAngles = new(0f, 180f, 0f),
            DefaultRadiusMeters = 45.0f
        },
        new()
        {
            Id = "doorman_hotel",
            DisplayName = "Doorman Hotel Room",
            Description = "Secret isolated vintage hotel interior with dramatic mood lighting.",
            SourcePosition = new(16068f, -7303f, -2880f),
            SourceAngles = new(0f, 180f, 0f),
            DefaultRadiusMeters = 22.0f
        }
    };

    public static StagePresetDefinition GetPresetById(string id)
    {
        foreach (var p in HideoutPresets)
        {
            if (p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return p;
        }
        return HideoutPresets[0];
    }
}
