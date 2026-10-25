using System;
using System.Collections.Generic;
using Godot;

namespace DeadlockPlayground.Maps.Editor;

/// <summary>
/// Serializable model representing an individual placed prop, architecture tile, light, or Source 2 entity.
/// </summary>
public class MapObjectData
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Prop";
    
    /// <summary>
    /// Type of entity: "prop_dynamic", "info_player_start", "light_omni2", "light_barn", "light_environment", "npc_trooper", "trigger_shop", "custom_kv3"
    /// </summary>
    public string EntityType { get; set; } = "prop_dynamic";
    
    public string ModelPath { get; set; } = "";
    
    // Transform in Godot space
    public Vector3 Position { get; set; } = Vector3.Zero;
    public Vector3 RotationDegrees { get; set; } = Vector3.Zero;
    public Vector3 Scale { get; set; } = Vector3.One;
    
    // Visual & Material properties
    public Color ColorTint { get; set; } = Colors.White;
    public string MaterialOverride { get; set; } = "";
    
    /// <summary>
    /// In-game collision mode: 6 = Solid (VPhysics / Walkable Havok), 2 = Bounding Box Solid, 0 = Non-Solid Decorative
    /// </summary>
    public int SolidMode { get; set; } = 6;
    
    // Lighting parameters (for light entities)
    public Color LightColor { get; set; } = Colors.White;
    public float LightEnergy { get; set; } = 1.0f;
    public float LightRange { get; set; } = 8.0f;
    public float SpotAngle { get; set; } = 45.0f;
    
    // Custom KV3 attributes for advanced entity tuning
    public Dictionary<string, string> CustomKv { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Transform3D GetTransform3D()
    {
        var basis = Basis.FromEuler(new Vector3(
            Mathf.DegToRad(RotationDegrees.X),
            Mathf.DegToRad(RotationDegrees.Y),
            Mathf.DegToRad(RotationDegrees.Z)
        )).Scaled(Scale);

        return new Transform3D(basis, Position);
    }

    public void SetFromTransform3D(Transform3D t)
    {
        Position = t.Origin;
        Scale = t.Basis.Scale;
        
        // Extract Euler rotation in degrees
        var rotEuler = t.Basis.GetEuler();
        RotationDegrees = new Vector3(
            Mathf.RadToDeg(rotEuler.X),
            Mathf.RadToDeg(rotEuler.Y),
            Mathf.RadToDeg(rotEuler.Z)
        );
    }
}
