using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using SteamDatabase.ValvePak;
using DeadlockPlayground.Materials;

namespace DeadlockPlayground.Maps.Editor;

/// <summary>
/// Central manager for Deadlock Playground Forge Mode. Coordinates the 3D Stage environment,
/// ghost prop placement, interactive selection, entity spawning, and level serialization.
/// </summary>
public partial class MapEditorManager : Node
{
    public static MapEditorManager Instance { get; private set; }

    [Signal] public delegate void EditorModeChangedEventHandler(bool isForgeActive);
    [Signal] public delegate void ProjectUpdatedEventHandler();
    [Signal] public delegate void GhostPlacementStateChangedEventHandler(bool isPlacing, string vmdlPath);

    private Node _worldStageParent;
    private Node3D _hiddenStagePlatform;
    private Node3D _forgeStageRoot;
    private Node3D _placedObjectsRoot;
    private MeshInstance3D _gridFloorMesh;
    private Node3D _ghostMeshInstance;
    private string _ghostVmdlPath;
    private float _ghostRotationYDeg = 0.0f;
    private bool _isPlacingGhost = false;

    private MapProject _currentProject = new();
    private readonly Dictionary<string, Node3D> _objectNodesById = new();
    private Package _baseGamePackage;
    private Package _hideoutPackage;
    private Camera3D _viewportCamera;

    public MapProject CurrentProject => _currentProject;
    public Node3D ForgeStageRoot => _forgeStageRoot;
    public Node3D PlacedObjectsRoot => _placedObjectsRoot;
    public Node WorldStageParent => _worldStageParent;
    public bool IsForgeActive => _forgeStageRoot != null && GodotObject.IsInstanceValid(_forgeStageRoot) && _forgeStageRoot.IsInsideTree() && _forgeStageRoot.Visible;
    public bool IsPlacingGhost => _isPlacingGhost;
    public int PlacedCount => _currentProject.Objects.Count;

    public Node3D GetNodeForObject(string objectId)
    {
        if (string.IsNullOrEmpty(objectId)) return null;
        if (_objectNodesById.TryGetValue(objectId, out var node) && GodotObject.IsInstanceValid(node))
        {
            return node;
        }
        return null;
    }

    public override void _EnterTree()
    {
        Instance = this;
    }

    public void Initialize(Package baseGamePackage, Package hideoutPackage)
    {
        var (bPkg, hPkg) = Source2MapLoader.EnsureAllPackagesMounted(baseGamePackage);
        _baseGamePackage = bPkg;
        _hideoutPackage = hPkg ?? hideoutPackage ?? Source2MapLoader.LoadedHideoutPackage;

        // Ensure asset catalog is indexed
        if (!MapAssetCatalog.IsIndexed && _baseGamePackage != null)
        {
            MapAssetCatalog.IndexAssets(_baseGamePackage, _hideoutPackage);
        }
    }

    /// <summary>
    /// Guarantees that the Forge stage root and placed objects container are valid, instantiated,
    /// and attached to the scene tree. If null, disposed, or unattached, auto-initializes the Blank Canvas.
    /// </summary>
    public bool EnsureEditorRootValid()
    {
        if (_forgeStageRoot != null && GodotObject.IsInstanceValid(_forgeStageRoot) && _forgeStageRoot.IsInsideTree()
            && _placedObjectsRoot != null && GodotObject.IsInstanceValid(_placedObjectsRoot) && _placedObjectsRoot.IsInsideTree())
        {
            return true;
        }

        GD.Print("[MapEditorManager] Forge stage root invalid or unattached. Auto-initializing Blank Canvas...");
        Node parent = _worldStageParent;
        if (parent == null || !GodotObject.IsInstanceValid(parent) || !parent.IsInsideTree())
        {
            var tree = GetTree() ?? Engine.GetMainLoop() as SceneTree;
            parent = (tree?.Root?.FindChild("WorldViewport", true, false) as Node)
                  ?? (tree?.Root?.FindChild("SubViewport", true, false) as Node);
        }
        Camera3D cam = _viewportCamera;
        if (cam == null || !GodotObject.IsInstanceValid(cam))
        {
            var tree = GetTree() ?? Engine.GetMainLoop() as SceneTree;
            cam = parent?.FindChild("Camera3D", true, false) as Camera3D
               ?? tree?.Root?.FindChild("Camera3D", true, false) as Camera3D;
        }

        CreateBlankCanvas(parent, cam);
        return _forgeStageRoot != null && GodotObject.IsInstanceValid(_forgeStageRoot) && _forgeStageRoot.IsInsideTree();
    }

    /// <summary>
    /// Initializes a brand new Blank Map Canvas environment.
    /// Auto-mounts VPKs, activates 3D sky/environment, creates infinite reference grid,
    /// walkable architectural floor platform, directional sun, and player spawn.
    /// </summary>
    public void CreateBlankCanvas(Node worldStageParent = null, Camera3D camera = null)
    {
        // 1. Ensure all VPK packages are mounted
        var (bPkg, hPkg) = Source2MapLoader.EnsureAllPackagesMounted(_baseGamePackage);
        _baseGamePackage = bPkg;
        _hideoutPackage = hPkg ?? _hideoutPackage ?? Source2MapLoader.LoadedHideoutPackage;

        if (worldStageParent != null)
        {
            _worldStageParent = worldStageParent;
        }
        else if (_worldStageParent == null || !GodotObject.IsInstanceValid(_worldStageParent) || !_worldStageParent.IsInsideTree())
        {
            var tree = GetTree() ?? Engine.GetMainLoop() as SceneTree;
            _worldStageParent = (tree?.Root?.FindChild("WorldViewport", true, false) as Node)
                             ?? (tree?.Root?.FindChild("SubViewport", true, false) as Node);
        }

        if (camera != null)
        {
            _viewportCamera = camera;
        }

        ClearStage();

        if (_worldStageParent == null)
        {
            GD.PrintErr("[MapEditorManager] Cannot create blank canvas: worldStageParent is null.");
            return;
        }

        _currentProject = new MapProject
        {
            MapName = "custom_deadlock_arena",
            BaseStageMode = "blank"
        };

        _forgeStageRoot = new Node3D { Name = "ForgeStageRoot" };
        _worldStageParent.AddChild(_forgeStageRoot);

        // 2. Activate 3D Environment, Procedural Skybox & Ambient Lighting
        Activate3DStageEnvironment();

        // 3. Build Reference Grid Floor Plane
        BuildGridFloor();

        // 4. Container for placed props & entities
        _placedObjectsRoot = new Node3D { Name = "PlacedObjectsRoot" };
        _forgeStageRoot.AddChild(_placedObjectsRoot);

        // 5. Initialize Selection Manager
        MapSelectionManager.Instance?.Initialize(_forgeStageRoot);

        // 6. Directional Sun (light_environment)
        var sunData = new MapObjectData
        {
            Name = "Sun (light_environment)",
            EntityType = MapEntitySpawner.EntityLightSun,
            Position = new Vector3(0, 15, 0),
            RotationDegrees = new Vector3(-45, 30, 0),
            LightColor = new Color(1.0f, 0.96f, 0.90f),
            LightEnergy = 1.8f
        };
        SpawnObjectFromData(sunData, pushUndo: false);

        // 7. Default Walkable Arena Floor Platform (solid architecture with physics collision)
        var floorData = new MapObjectData
        {
            Name = "Floor_Platform_01",
            EntityType = "prop_dynamic",
            ModelPath = "models/architecture/streets/street_tile_concrete_01.vmdl",
            Position = new Vector3(0, 0, 0),
            RotationDegrees = Vector3.Zero,
            Scale = new Vector3(4.0f, 1.0f, 4.0f),
            SolidMode = 6
        };
        SpawnObjectFromData(floorData, pushUndo: false);

        // 8. Default Player Spawn Point (info_player_start)
        var spawnData = new MapObjectData
        {
            Name = "Player_Spawn_01",
            EntityType = MapEntitySpawner.EntityPlayerStart,
            Position = new Vector3(0, 0.1f, 0),
            RotationDegrees = Vector3.Zero
        };
        SpawnObjectFromData(spawnData, pushUndo: false);

        EmitSignal(SignalName.EditorModeChanged, true);
        EmitSignal(SignalName.ProjectUpdated);

        GD.Print("[MapEditorManager] Blank Map Canvas initialized with 3D skybox, reference grid, sun, floor, and player spawn!");
    }

    private void Activate3DStageEnvironment()
    {
        if (_worldStageParent == null) return;
        var tree = _worldStageParent.GetTree() ?? Engine.GetMainLoop() as SceneTree;
        var worldVp = _worldStageParent as SubViewport ?? tree?.Root?.FindChild("WorldViewport", true, false) as SubViewport;
        var worldEnv = tree?.Root?.FindChild("WorldEnvironment", true, false) as WorldEnvironment;
        var bgRect = tree?.Root?.FindChild("BgColorRect", true, false) as ColorRect;
        var bgTexRect = tree?.Root?.FindChild("BgTextureRect", true, false) as TextureRect;

        // Hide default studio disc platform while in 3D Map / Forge Mode
        var stagePlatform = _worldStageParent.FindChild("StagePlatform", true, false) as Node3D
                         ?? tree?.Root?.FindChild("StagePlatform", true, false) as Node3D;
        if (stagePlatform != null)
        {
            _hiddenStagePlatform = stagePlatform;
            _hiddenStagePlatform.Visible = false;
        }

        SceneEnvironmentManager.Is3DMapStageActive = true;
        SceneEnvironmentManager.ApplyEnvironment(tree?.Root, worldVp, worldEnv, bgRect, bgTexRect, _hiddenStagePlatform);
    }

    private void BuildGridFloor()
    {
        var planeMesh = new PlaneMesh
        {
            Size = new Vector2(200.0f, 200.0f)
        };

        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.12f, 0.13f, 0.16f, 0.85f),
            Roughness = 0.9f,
            Metallic = 0.1f
        };

        _gridFloorMesh = new MeshInstance3D
        {
            Name = "ForgeReferenceGrid",
            Mesh = planeMesh,
            MaterialOverride = mat,
            Position = new Vector3(0, -0.01f, 0)
        };
        _gridFloorMesh.Layers &= ~(1u << 20);

        // Create StaticBody3D collision for raycast picking
        var staticBody = new StaticBody3D { Name = "GridCollisionBody" };
        var shape = new CollisionShape3D
        {
            Shape = new WorldBoundaryShape3D
            {
                Plane = new Plane(Vector3.Up, 0)
            }
        };
        staticBody.AddChild(shape);
        _gridFloorMesh.AddChild(staticBody);

        _forgeStageRoot.AddChild(_gridFloorMesh);
    }

    public void ConvertLoadedHideoutToEditable(Node3D hideoutStageRoot, Camera3D camera = null)
    {
        _viewportCamera = camera;
        if (hideoutStageRoot == null || !GodotObject.IsInstanceValid(hideoutStageRoot)) return;

        _currentProject = new MapProject
        {
            MapName = "dl_hideout_custom",
            BaseStageMode = "dl_hideout"
        };

        _forgeStageRoot = hideoutStageRoot;
        _placedObjectsRoot = hideoutStageRoot.FindChild("PropsRoot", true, false) as Node3D;
        if (_placedObjectsRoot == null)
        {
            _placedObjectsRoot = new Node3D { Name = "PropsRoot" };
            _forgeStageRoot.AddChild(_placedObjectsRoot);
        }

        MapSelectionManager.Instance?.Initialize(_forgeStageRoot);

        EmitSignal(SignalName.EditorModeChanged, true);
        EmitSignal(SignalName.ProjectUpdated);
        GD.Print("[MapEditorManager] Hideout stage converted to editable Forge Mode scene!");
    }

    public Node3D SpawnObjectFromData(MapObjectData data, bool pushUndo = true)
    {
        if (data == null) return null;

        EnsureEditorRootValid();
        if (_placedObjectsRoot == null || !GodotObject.IsInstanceValid(_placedObjectsRoot) || !_placedObjectsRoot.IsInsideTree())
        {
            GD.PrintErr("[MapEditorManager] Failed to spawn object: _placedObjectsRoot is not valid in tree.");
            return null;
        }

        Node3D nodeInstance = null;

        if (data.EntityType == "prop_dynamic")
        {
            nodeInstance = Source2MeshLoader.InstantiateModel(
                data.ModelPath,
                _hideoutPackage,
                _baseGamePackage,
                MapTextureQuality.Medium,
                instanceTint: data.ColorTint,
                entityOrObjectName: data.Name);

            // If model wasn't found in VPK, generate a robust procedural floor platform or placeholder box
            if (nodeInstance == null)
            {
                if (data.Name == "Floor_Platform_01" || (data.ModelPath != null && data.ModelPath.Contains("street_tile")))
                {
                    nodeInstance = CreateProceduralFloorPlatform("Floor_Platform_01");
                }
                else
                {
                    nodeInstance = CreatePlaceholderBox(data.Name, data.ColorTint);
                }
            }

            // Add physics collision shape for raycasting and snapping
            EnsureObjectPickingCollision(nodeInstance, data.Id);
        }
        else
        {
            // Native Source 2 entity (player start, light, shop, target)
            nodeInstance = MapEntitySpawner.SpawnEntityNode(data);
        }

        if (nodeInstance != null)
        {
            nodeInstance.Transform = data.GetTransform3D();
            nodeInstance.SetMeta("map_object_id", data.Id);
            EnsureObjectPickingCollision(nodeInstance, data.Id);
            _placedObjectsRoot.AddChild(nodeInstance);
            _objectNodesById[data.Id] = nodeInstance;

            if (!_currentProject.Objects.Contains(data))
            {
                _currentProject.Objects.Add(data);
            }

            if (pushUndo)
            {
                var action = new PlaceObjectUndoAction(data, nodeInstance, this);
                MapSelectionManager.Instance?.PushUndoAction(action);
            }

            EmitSignal(SignalName.ProjectUpdated);
        }

        return nodeInstance;
    }

    private Node3D CreateProceduralFloorPlatform(string name)
    {
        var platformRoot = new Node3D { Name = name };
        var mi = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(32.0f, 0.5f, 32.0f) },
            Position = new Vector3(0, -0.25f, 0)
        };
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.18f, 0.20f, 0.24f),
            Roughness = 0.85f,
            Metallic = 0.05f
        };
        mi.MaterialOverride = mat;
        mi.Layers &= ~(1u << 20);
        platformRoot.AddChild(mi);

        var staticBody = new StaticBody3D { Name = "FloorCollision" };
        var shape = new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(32.0f, 0.5f, 32.0f) },
            Position = new Vector3(0, -0.25f, 0)
        };
        staticBody.AddChild(shape);
        platformRoot.AddChild(staticBody);

        return platformRoot;
    }

    private Node3D CreatePlaceholderBox(string name, Color tint)
    {
        var boxNode = new Node3D { Name = name };
        var mi = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.0f, 1.0f, 1.0f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = tint },
            Position = new Vector3(0, 0.5f, 0)
        };
        mi.Layers &= ~(1u << 20);
        boxNode.AddChild(mi);

        var staticBody = new StaticBody3D { Name = "PlaceholderCollision" };
        var shape = new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(1.0f, 1.0f, 1.0f) },
            Position = new Vector3(0, 0.5f, 0)
        };
        staticBody.AddChild(shape);
        boxNode.AddChild(staticBody);

        return boxNode;
    }

    /// <summary>
    /// Guarantees that every placed object has a StaticBody3D raycast target with metadata
    /// for instant, rock-solid left-click 3D viewport picking.
    /// </summary>
    public static void EnsureObjectPickingCollision(Node3D root, string objId)
    {
        if (root == null || !GodotObject.IsInstanceValid(root)) return;

        // If it already has StaticBody3D children, tag them with map_object_id
        var existingBodies = root.FindChildren("*", "StaticBody3D", true, false);
        if (existingBodies.Count > 0)
        {
            foreach (var b in existingBodies)
            {
                if (b is Node nb) nb.SetMeta("map_object_id", objId);
            }
            return;
        }

        // Calculate combined AABB of mesh children
        Aabb totalAabb = new Aabb();
        bool hasAabb = false;

        foreach (var child in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is MeshInstance3D mi && mi.Mesh != null)
            {
                var miAabb = mi.Mesh.GetAabb();
                if (!hasAabb)
                {
                    totalAabb = miAabb;
                    hasAabb = true;
                }
                else
                {
                    totalAabb = totalAabb.Merge(miAabb);
                }
            }
        }

        if (!hasAabb || totalAabb.Size.LengthSquared() < 1e-4f)
        {
            totalAabb = new Aabb(new Vector3(-0.5f, 0, -0.5f), Vector3.One);
        }

        var staticBody = new StaticBody3D { Name = "PickingCollision" };
        staticBody.SetMeta("map_object_id", objId);
        staticBody.CollisionLayer = 1;
        staticBody.CollisionMask = 0;

        var colShape = new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = totalAabb.Size * 1.05f },
            Position = totalAabb.GetCenter()
        };
        staticBody.AddChild(colShape);
        root.AddChild(staticBody);
    }

    // --- Ghost Placement Mode ---

    public void StartPlacement(string vmdlPath)
    {
        EnsureEditorRootValid();
        CancelPlacement();

        _ghostVmdlPath = vmdlPath;
        _ghostRotationYDeg = 0.0f;
        _isPlacingGhost = true;

        // Instantiate ghost model
        _ghostMeshInstance = Source2MeshLoader.InstantiateModel(
            vmdlPath,
            _hideoutPackage,
            _baseGamePackage,
            MapTextureQuality.Low,
            instanceTint: new Color(0.2f, 0.9f, 1.0f, 0.65f));

        if (_ghostMeshInstance == null)
        {
            _ghostMeshInstance = CreatePlaceholderBox("GhostPreview", new Color(0.2f, 0.9f, 1.0f, 0.65f));
        }

        // Apply semi-transparent ghost overlay material
        ApplyGhostMaterial(_ghostMeshInstance);

        if (_forgeStageRoot != null && GodotObject.IsInstanceValid(_forgeStageRoot) && _forgeStageRoot.IsInsideTree())
        {
            _forgeStageRoot.AddChild(_ghostMeshInstance);
        }

        EmitSignal(SignalName.GhostPlacementStateChanged, true, vmdlPath);
    }

    private void ApplyGhostMaterial(Node3D root)
    {
        var ghostMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.15f, 0.85f, 1.0f, 0.55f),
            EmissionEnabled = true,
            Emission = new Color(0.15f, 0.85f, 1.0f),
            EmissionEnergyMultiplier = 0.8f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };

        foreach (var child in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is MeshInstance3D mi)
            {
                mi.MaterialOverride = ghostMat;
                mi.Layers &= ~(1u << 20);
            }
        }
    }

    public void UpdateGhostRaycast(Vector2 mousePos, Camera3D camera)
    {
        if (!_isPlacingGhost || _ghostMeshInstance == null || camera == null) return;

        var rayOrigin = camera.ProjectRayOrigin(mousePos);
        var rayNormal = camera.ProjectRayNormal(mousePos);
        var spaceState = camera.GetWorld3D().DirectSpaceState;

        var query = PhysicsRayQueryParameters3D.Create(rayOrigin, rayOrigin + rayNormal * 1000.0f);
        var hit = spaceState.IntersectRay(query);

        Vector3 placePos;
        if (hit.Count > 0 && hit.TryGetValue("position", out var posVariant))
        {
            placePos = posVariant.AsVector3();
        }
        else
        {
            // Intersect with ground plane Y=0
            float t = -rayOrigin.Y / (rayNormal.Y != 0 ? rayNormal.Y : -0.001f);
            placePos = rayOrigin + rayNormal * Mathf.Max(0.1f, t);
        }

        // Snap position if snapping is active
        if (MapSelectionManager.Instance?.IsSnappingEnabled == true)
        {
            float snap = MapSelectionManager.Instance.TranslateSnap;
            if (snap > 0.001f)
            {
                placePos.X = Mathf.Round(placePos.X / snap) * snap;
                placePos.Z = Mathf.Round(placePos.Z / snap) * snap;
            }
        }

        var basis = Basis.FromEuler(new Vector3(0, Mathf.DegToRad(_ghostRotationYDeg), 0));
        _ghostMeshInstance.Transform = new Transform3D(basis, placePos);
    }

    public void RotateGhost(float degreesDelta)
    {
        _ghostRotationYDeg = (_ghostRotationYDeg + degreesDelta) % 360.0f;
        if (_ghostMeshInstance != null)
        {
            var b = Basis.FromEuler(new Vector3(0, Mathf.DegToRad(_ghostRotationYDeg), 0));
            _ghostMeshInstance.Basis = b;
        }
    }

    public void CommitGhostPlacement()
    {
        if (!_isPlacingGhost || _ghostMeshInstance == null) return;

        var data = new MapObjectData
        {
            Name = Path.GetFileNameWithoutExtension(_ghostVmdlPath),
            EntityType = "prop_dynamic",
            ModelPath = _ghostVmdlPath,
            Position = _ghostMeshInstance.Position,
            RotationDegrees = new Vector3(0, _ghostRotationYDeg, 0),
            Scale = Vector3.One,
            SolidMode = 6
        };

        var placedNode = SpawnObjectFromData(data, pushUndo: true);
        if (placedNode != null)
        {
            MapSelectionManager.Instance?.SelectObject(placedNode, data);
        }

        CancelPlacement();
    }

    public void CancelPlacement()
    {
        if (_ghostMeshInstance != null && GodotObject.IsInstanceValid(_ghostMeshInstance))
        {
            _ghostMeshInstance.QueueFree();
            _ghostMeshInstance = null;
        }
        _isPlacingGhost = false;
        _ghostVmdlPath = null;
        EmitSignal(SignalName.GhostPlacementStateChanged, false, "");
    }

    public void DuplicateSelected()
    {
        var selData = MapSelectionManager.Instance?.SelectedData;
        if (selData == null) return;

        var clone = new MapObjectData
        {
            Name = $"{selData.Name}_Copy",
            EntityType = selData.EntityType,
            ModelPath = selData.ModelPath,
            Position = selData.Position + new Vector3(0.5f, 0, 0.5f),
            RotationDegrees = selData.RotationDegrees,
            Scale = selData.Scale,
            ColorTint = selData.ColorTint,
            SolidMode = selData.SolidMode,
            LightColor = selData.LightColor,
            LightEnergy = selData.LightEnergy,
            LightRange = selData.LightRange,
            SpotAngle = selData.SpotAngle
        };

        var node = SpawnObjectFromData(clone, pushUndo: true);
        if (node != null)
        {
            MapSelectionManager.Instance?.SelectObject(node, clone);
        }
    }

    public void DeleteSelected()
    {
        var selData = MapSelectionManager.Instance?.SelectedData;
        var selNode = MapSelectionManager.Instance?.SelectedNode;
        if (selData == null || selNode == null) return;

        MapSelectionManager.Instance.ClearSelection();

        var action = new DeleteObjectUndoAction(selData, selNode, this);
        MapSelectionManager.Instance.PushUndoAction(action);

        RemoveObjectInternal(selData, selNode);
    }

    public void RemoveObjectInternal(MapObjectData data, Node3D node)
    {
        _currentProject.Objects.Remove(data);
        _objectNodesById.Remove(data.Id);

        if (GodotObject.IsInstanceValid(node))
        {
            node.GetParent()?.RemoveChild(node);
            node.QueueFree();
        }

        EmitSignal(SignalName.ProjectUpdated);
    }

    public void RestoreObjectInternal(MapObjectData data, Node3D node)
    {
        if (!_currentProject.Objects.Contains(data))
        {
            _currentProject.Objects.Add(data);
        }
        if (_placedObjectsRoot != null && !node.IsInsideTree())
        {
            _placedObjectsRoot.AddChild(node);
            _objectNodesById[data.Id] = node;
        }
        EmitSignal(SignalName.ProjectUpdated);
    }

    public void ClearStage()
    {
        CancelPlacement();
        MapSelectionManager.Instance?.ResetGizmo();

        if (_forgeStageRoot != null && GodotObject.IsInstanceValid(_forgeStageRoot))
        {
            _forgeStageRoot.GetParent()?.RemoveChild(_forgeStageRoot);
            _forgeStageRoot.QueueFree();
        }
        _forgeStageRoot = null;
        _placedObjectsRoot = null;
        _gridFloorMesh = null;
        _ghostMeshInstance = null;

        if (GodotObject.IsInstanceValid(_hiddenStagePlatform))
        {
            _hiddenStagePlatform.Visible = true;
            _hiddenStagePlatform = null;
        }

        _objectNodesById.Clear();
        _currentProject.Objects.Clear();
        EmitSignal(SignalName.EditorModeChanged, false);
        EmitSignal(SignalName.ProjectUpdated);
    }
}

public class PlaceObjectUndoAction : IMapUndoAction
{
    public string ActionName => "Place Object";
    private readonly MapObjectData _data;
    private readonly Node3D _node;
    private readonly MapEditorManager _manager;

    public PlaceObjectUndoAction(MapObjectData data, Node3D node, MapEditorManager manager)
    {
        _data = data;
        _node = node;
        _manager = manager;
    }

    public void Undo() => _manager.RemoveObjectInternal(_data, _node);
    public void Redo() => _manager.RestoreObjectInternal(_data, _node);
}

public class DeleteObjectUndoAction : IMapUndoAction
{
    public string ActionName => "Delete Object";
    private readonly MapObjectData _data;
    private readonly Node3D _node;
    private readonly MapEditorManager _manager;

    public DeleteObjectUndoAction(MapObjectData data, Node3D node, MapEditorManager manager)
    {
        _data = data;
        _node = node;
        _manager = manager;
    }

    public void Undo() => _manager.RestoreObjectInternal(_data, _node);
    public void Redo() => _manager.RemoveObjectInternal(_data, _node);
}
