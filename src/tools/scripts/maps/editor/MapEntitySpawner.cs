using System;
using System.Collections.Generic;
using Godot;

namespace DeadlockPlayground.Maps.Editor;

/// <summary>
/// Spawns and manages 3D visual representations and live Godot components for native Source 2 gameplay entities
/// (info_player_start, light_omni2, light_barn, light_environment, target dummies, shop zones).
/// </summary>
public static class MapEntitySpawner
{
    public const string EntityPlayerStart = "info_player_start";
    public const string EntityLightOmni = "light_omni2";
    public const string EntityLightSpot = "light_barn";
    public const string EntityLightSun = "light_environment";
    public const string EntityTargetDummy = "npc_trooper";
    public const string EntityShopZone = "trigger_shop";
    public const string EntityCustom = "custom_kv3";

    /// <summary>
    /// Creates a 3D Node representing a Source 2 entity in the editor viewport.
    /// </summary>
    public static Node3D SpawnEntityNode(MapObjectData data)
    {
        var root = new Node3D
        {
            Name = $"{data.EntityType}_{data.Id[..6]}"
        };
        root.SetMeta("map_object_id", data.Id);
        root.Transform = data.GetTransform3D();

        switch (data.EntityType)
        {
            case EntityPlayerStart:
                BuildPlayerStartVisual(root);
                break;

            case EntityLightOmni:
                BuildOmniLight(root, data);
                break;

            case EntityLightSpot:
                BuildSpotLight(root, data);
                break;

            case EntityLightSun:
                BuildSunLight(root, data);
                break;

            case EntityTargetDummy:
                BuildTargetDummyVisual(root);
                break;

            case EntityShopZone:
                BuildShopZoneVisual(root);
                break;

            default:
                BuildGenericEntityVisual(root, data);
                break;
        }

        // Tag all created StaticBody3D with map_object_id for instant picking
        foreach (var b in root.FindChildren("*", "StaticBody3D", true, false))
        {
            if (b is Node nb) nb.SetMeta("map_object_id", data.Id);
        }

        return root;
    }

    private static void BuildPlayerStartVisual(Node3D root)
    {
        // 1. Base pedestal ring
        var ringMesh = new CylinderMesh
        {
            TopRadius = 0.45f,
            BottomRadius = 0.50f,
            Height = 0.08f
        };
        var ringMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.1f, 0.9f, 0.5f, 0.85f),
            EmissionEnabled = true,
            Emission = new Color(0.1f, 0.9f, 0.5f),
            EmissionEnergyMultiplier = 1.2f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha
        };
        var ringInstance = new MeshInstance3D
        {
            Mesh = ringMesh,
            MaterialOverride = ringMat,
            Position = new Vector3(0, 0.04f, 0)
        };
        // Clear layer 21 to respect painter rules
        ringInstance.Layers &= ~(1u << 20);
        root.AddChild(ringInstance);

        // 2. 3D Facing Arrow (Points toward -Z which is Godot Forward)
        var arrowMesh = new PrismMesh
        {
            Size = new Vector3(0.35f, 0.5f, 0.08f)
        };
        var arrowMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.2f, 1.0f, 0.6f),
            EmissionEnabled = true,
            Emission = new Color(0.2f, 1.0f, 0.6f),
            EmissionEnergyMultiplier = 1.5f
        };
        var arrowInstance = new MeshInstance3D
        {
            Mesh = arrowMesh,
            MaterialOverride = arrowMat,
            Position = new Vector3(0, 0.06f, -0.6f),
            RotationDegrees = new Vector3(-90, 0, 0)
        };
        arrowInstance.Layers &= ~(1u << 20);
        root.AddChild(arrowInstance);

        // 3. Player Silhouette Dummy Box
        var capsule = new CapsuleMesh
        {
            Radius = 0.28f,
            Height = 1.75f
        };
        var capsuleMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.2f, 0.85f, 0.6f, 0.4f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha
        };
        var bodyInstance = new MeshInstance3D
        {
            Mesh = capsule,
            MaterialOverride = capsuleMat,
            Position = new Vector3(0, 0.875f, 0)
        };
        bodyInstance.Layers &= ~(1u << 20);
        root.AddChild(bodyInstance);

        // 4. Raycast picking collision
        var staticBody = new StaticBody3D { Name = "PickingCollision" };
        staticBody.CollisionLayer = 1;
        staticBody.CollisionMask = 0;
        var colShape = new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = 0.35f, Height = 1.8f },
            Position = new Vector3(0, 0.9f, 0)
        };
        staticBody.AddChild(colShape);
        root.AddChild(staticBody);
    }

    private static void BuildOmniLight(Node3D root, MapObjectData data)
    {
        // Live Godot OmniLight3D
        var omni = new OmniLight3D
        {
            Name = "LiveOmniLight",
            LightColor = data.LightColor,
            LightEnergy = data.LightEnergy,
            OmniRange = data.LightRange,
            ShadowEnabled = true
        };
        omni.Layers &= ~(1u << 20);
        root.AddChild(omni);

        // Visual Bulb Gizmo
        var sphere = new SphereMesh { Radius = 0.15f, Height = 0.30f };
        var mat = new StandardMaterial3D
        {
            AlbedoColor = data.LightColor,
            EmissionEnabled = true,
            Emission = data.LightColor,
            EmissionEnergyMultiplier = 2.0f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
        var gizmo = new MeshInstance3D
        {
            Name = "LightGizmoMesh",
            Mesh = sphere,
            MaterialOverride = mat
        };
        gizmo.Layers &= ~(1u << 20);
        root.AddChild(gizmo);

        // Raycast picking collision
        var staticBody = new StaticBody3D { Name = "PickingCollision" };
        staticBody.CollisionLayer = 1;
        staticBody.CollisionMask = 0;
        var colShape = new CollisionShape3D
        {
            Shape = new SphereShape3D { Radius = 0.35f }
        };
        staticBody.AddChild(colShape);
        root.AddChild(staticBody);
    }

    private static void BuildSpotLight(Node3D root, MapObjectData data)
    {
        // Live Godot SpotLight3D
        var spot = new SpotLight3D
        {
            Name = "LiveSpotLight",
            LightColor = data.LightColor,
            LightEnergy = data.LightEnergy,
            SpotRange = data.LightRange,
            SpotAngle = data.SpotAngle,
            ShadowEnabled = true
        };
        spot.Layers &= ~(1u << 20);
        root.AddChild(spot);

        // Visual Cone Gizmo
        var cylinder = new CylinderMesh
        {
            TopRadius = 0.05f,
            BottomRadius = 0.20f,
            Height = 0.35f
        };
        var mat = new StandardMaterial3D
        {
            AlbedoColor = data.LightColor,
            EmissionEnabled = true,
            Emission = data.LightColor,
            EmissionEnergyMultiplier = 1.8f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
        var gizmo = new MeshInstance3D
        {
            Name = "LightGizmoMesh",
            Mesh = cylinder,
            MaterialOverride = mat,
            RotationDegrees = new Vector3(-90, 0, 0)
        };
        gizmo.Layers &= ~(1u << 20);
        root.AddChild(gizmo);

        // Raycast picking collision
        var staticBody = new StaticBody3D { Name = "PickingCollision" };
        staticBody.CollisionLayer = 1;
        staticBody.CollisionMask = 0;
        var colShape = new CollisionShape3D
        {
            Shape = new SphereShape3D { Radius = 0.35f }
        };
        staticBody.AddChild(colShape);
        root.AddChild(staticBody);
    }

    private static void BuildSunLight(Node3D root, MapObjectData data)
    {
        var sun = new DirectionalLight3D
        {
            Name = "LiveSunLight",
            LightColor = data.LightColor,
            LightEnergy = data.LightEnergy,
            ShadowEnabled = true
        };
        sun.Layers &= ~(1u << 20);
        root.AddChild(sun);

        var ring = new TorusMesh
        {
            InnerRadius = 0.35f,
            OuterRadius = 0.40f
        };
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(1.0f, 0.85f, 0.3f),
            EmissionEnabled = true,
            Emission = new Color(1.0f, 0.85f, 0.3f),
            EmissionEnergyMultiplier = 2.0f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
        var gizmo = new MeshInstance3D
        {
            Name = "SunGizmoMesh",
            Mesh = ring,
            MaterialOverride = mat
        };
        gizmo.Layers &= ~(1u << 20);
        root.AddChild(gizmo);

        // Raycast picking collision
        var staticBody = new StaticBody3D { Name = "PickingCollision" };
        staticBody.CollisionLayer = 1;
        staticBody.CollisionMask = 0;
        var colShape = new CollisionShape3D
        {
            Shape = new SphereShape3D { Radius = 0.5f }
        };
        staticBody.AddChild(colShape);
        root.AddChild(staticBody);
    }

    private static void BuildTargetDummyVisual(Node3D root)
    {
        var box = new BoxMesh { Size = new Vector3(0.5f, 1.8f, 0.3f) };
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.9f, 0.2f, 0.2f, 0.75f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha
        };
        var mi = new MeshInstance3D
        {
            Mesh = box,
            MaterialOverride = mat,
            Position = new Vector3(0, 0.9f, 0)
        };
        mi.Layers &= ~(1u << 20);
        root.AddChild(mi);

        // Raycast picking collision
        var staticBody = new StaticBody3D { Name = "PickingCollision" };
        staticBody.CollisionLayer = 1;
        staticBody.CollisionMask = 0;
        var colShape = new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = 0.35f, Height = 1.8f },
            Position = new Vector3(0, 0.9f, 0)
        };
        staticBody.AddChild(colShape);
        root.AddChild(staticBody);
    }

    private static void BuildShopZoneVisual(Node3D root)
    {
        var box = new BoxMesh { Size = new Vector3(4.0f, 2.5f, 4.0f) };
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(1.0f, 0.8f, 0.1f, 0.25f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        var mi = new MeshInstance3D
        {
            Mesh = box,
            MaterialOverride = mat,
            Position = new Vector3(0, 1.25f, 0)
        };
        mi.Layers &= ~(1u << 20);
        root.AddChild(mi);

        // Raycast picking collision
        var staticBody = new StaticBody3D { Name = "PickingCollision" };
        staticBody.CollisionLayer = 1;
        staticBody.CollisionMask = 0;
        var colShape = new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(4.0f, 2.5f, 4.0f) },
            Position = new Vector3(0, 1.25f, 0)
        };
        staticBody.AddChild(colShape);
        root.AddChild(staticBody);
    }

    private static void BuildGenericEntityVisual(Node3D root, MapObjectData data)
    {
        var sphere = new SphereMesh { Radius = 0.3f, Height = 0.6f };
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.8f, 0.4f, 1.0f, 0.65f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha
        };
        var mi = new MeshInstance3D
        {
            Mesh = sphere,
            MaterialOverride = mat,
            Position = new Vector3(0, 0.3f, 0)
        };
        mi.Layers &= ~(1u << 20);
        root.AddChild(mi);

        // Raycast picking collision
        var staticBody = new StaticBody3D { Name = "PickingCollision" };
        staticBody.CollisionLayer = 1;
        staticBody.CollisionMask = 0;
        var colShape = new CollisionShape3D
        {
            Shape = new SphereShape3D { Radius = 0.4f },
            Position = new Vector3(0, 0.3f, 0)
        };
        staticBody.AddChild(colShape);
        root.AddChild(staticBody);
    }

    /// <summary>
    /// Updates live light and visual parameters when user edits properties in the Inspector.
    /// </summary>
    public static void UpdateEntityProperties(Node3D root, MapObjectData data)
    {
        if (root == null || data == null) return;

        var omni = root.FindChild("LiveOmniLight", true, false) as OmniLight3D;
        if (omni != null)
        {
            omni.LightColor = data.LightColor;
            omni.LightEnergy = data.LightEnergy;
            omni.OmniRange = data.LightRange;
        }

        var spot = root.FindChild("LiveSpotLight", true, false) as SpotLight3D;
        if (spot != null)
        {
            spot.LightColor = data.LightColor;
            spot.LightEnergy = data.LightEnergy;
            spot.SpotRange = data.LightRange;
            spot.SpotAngle = data.SpotAngle;
        }

        var sun = root.FindChild("LiveSunLight", true, false) as DirectionalLight3D;
        if (sun != null)
        {
            sun.LightColor = data.LightColor;
            sun.LightEnergy = data.LightEnergy;
        }

        var gizmo = root.FindChild("LightGizmoMesh", true, false) as MeshInstance3D;
        if (gizmo != null && gizmo.MaterialOverride is StandardMaterial3D stdMat)
        {
            stdMat.AlbedoColor = data.LightColor;
            stdMat.Emission = data.LightColor;
        }
    }
}
