using Godot;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

/// <summary>
/// Bitmask flags identifying the functional layer/category of a character bone.
/// </summary>
[Flags]
public enum BoneCategory
{
    None        = 0,
    Primary     = 1 << 0, // Core anatomy: Head, Neck, Spine, Chest, Pelvis, Arms, Legs
    Clothing    = 1 << 1, // Skirts, dresses, capes, flaps, coats, dynamic cloth bones
    Fingers     = 1 << 2, // Hand phalanges and thumb joints
    Face        = 1 << 3, // Eyes, brows, jaw, lips, tongue
    Props       = 1 << 4, // Gun, hammer, holster, backpack, attached items
    Helpers     = 1 << 5, // Twist, roll, helper, attachment locators, IK targets
    All         = Primary | Clothing | Fingers | Face | Props | Helpers
}

/// <summary>
/// Holds references to the visual and collision picking nodes generated for a specific bone.
/// </summary>
public class BoneControlItem
{
    public int BoneIndex { get; set; }
    public string BoneName { get; set; } = string.Empty;
    public BoneCategory Category { get; set; }
    public BoneAttachment3D Attachment { get; set; }
    public Area3D Area { get; set; }
    public CollisionShape3D Shape { get; set; }
    public MeshInstance3D MarkerMesh { get; set; }
}

/// <summary>
/// Manages bone classification, hitbox generation, non-destructive layer toggles,
/// and procedural cloth bone anchoring for Godot 4.
/// </summary>
public partial class BoneLayerManager : Node3D
{
    [Signal]
    public delegate void BoneClickedEventHandler(int boneIdx);

    [Signal]
    public delegate void LayerToggledEventHandler(int categoryBit, bool isEnabled);

    [Export]
    public Skeleton3D TargetSkeleton { get; set; }

    /// <summary>
    /// Default collision layer bit for bone picking (Default: Layer 32 = 2^31 = 2147483648).
    /// </summary>
    [Export(PropertyHint.Layers3DPhysics)]
    public uint PickingCollisionLayer { get; set; } = 2147483648;

    // Active layer mask (Primary and Props enabled by default)
    private BoneCategory _activeLayers = BoneCategory.Primary | BoneCategory.Props;

    private readonly Dictionary<int, BoneControlItem> _boneControls = new();
    private readonly Dictionary<BoneCategory, List<int>> _categoryIndices = new();

    // Shared unshaded materials for control markers
    private StandardMaterial3D _matPrimary;
    private StandardMaterial3D _matClothing;
    private StandardMaterial3D _matFingers;
    private StandardMaterial3D _matFace;
    private StandardMaterial3D _matProps;
    private StandardMaterial3D _matHelpers;
    private StandardMaterial3D _matSelected;

    private int _selectedBoneIdx = -1;

    public BoneCategory ActiveLayers => _activeLayers;
    public IReadOnlyDictionary<int, BoneControlItem> BoneControls => _boneControls;

    public override void _Ready()
    {
        GizmoDisplaySettings.LoadConfig();
        InitializeMaterials();
        GizmoDisplaySettings.OnSettingsChanged += ApplyDisplaySettings;
    }

    public override void _ExitTree()
    {
        GizmoDisplaySettings.OnSettingsChanged -= ApplyDisplaySettings;
    }

    /// <summary>
    /// Builds the entire bone control overlay across the skeleton without modifying the skeleton hierarchy.
    /// Automatically anchors procedural cloth root bones to their anatomical parents (pelvis, spine, chest).
    /// </summary>
    public void BuildBoneHierarchyOverlay(Skeleton3D skeleton, uint collisionLayer)
    {
        TargetSkeleton = skeleton;
        PickingCollisionLayer = collisionLayer;

        ClearControls();

        if (TargetSkeleton == null)
        {
            GD.PrintErr("[BoneLayerManager] TargetSkeleton is null! Cannot generate bone controls.");
            return;
        }

        // Category lists initialization
        foreach (BoneCategory cat in Enum.GetValues(typeof(BoneCategory)))
        {
            if (cat != BoneCategory.None && cat != BoneCategory.All)
            {
                _categoryIndices[cat] = new List<int>();
            }
        }

        int totalBones = TargetSkeleton.GetBoneCount();
        GD.Print($"[BoneLayerManager] Classifying and generating pickers for {totalBones} bones...");

        for (int i = 0; i < totalBones; i++)
        {
            string boneName = TargetSkeleton.GetBoneName(i) ?? string.Empty;
            BoneCategory category = ClassifyBone(boneName);

            _categoryIndices[category].Add(i);

            // 1. Create BoneAttachment3D as child of Skeleton3D
            var attachment = new BoneAttachment3D
            {
                Name = $"Pick_{boneName}_{i}",
                BoneName = boneName,
                BoneIdx = i
            };
            TargetSkeleton.AddChild(attachment);

            // 2. Create Area3D for raycast picking
            var area = new Area3D
            {
                Name = "PickerArea",
                CollisionLayer = PickingCollisionLayer,
                CollisionMask = 0,
                Monitorable = true,
                Monitoring = false
            };
            attachment.AddChild(area);

            int currentBoneIdx = i;
            area.InputEvent += (Node camera, InputEvent @event, Vector3 pos, Vector3 norm, long shapeIdx) =>
            {
                if (@event is InputEventMouseButton mouse && mouse.Pressed && mouse.ButtonIndex == MouseButton.Left)
                {
                    EmitSignal(SignalName.BoneClicked, currentBoneIdx);
                }
            };

            // 3. Create CollisionShape3D
            var colShape = new CollisionShape3D { Name = "PickerShape" };
            colShape.Shape = CreateCollisionShapeForBone(boneName, category);
            colShape.Scale = Vector3.One * GizmoDisplaySettings.BoneMarkerScale;
            area.AddChild(colShape);

            // 4. Create visual handle marker (MeshInstance3D)
            var markerMesh = new MeshInstance3D
            {
                Name = "PickerHandle",
                Mesh = CreateMarkerMeshForBone(boneName, category),
                MaterialOverride = GetMaterialForCategory(category),
                Scale = Vector3.One * GizmoDisplaySettings.BoneMarkerScale,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Layers = 2
            };
            attachment.AddChild(markerMesh);

            var controlItem = new BoneControlItem
            {
                BoneIndex = i,
                BoneName = boneName,
                Category = category,
                Attachment = attachment,
                Area = area,
                Shape = colShape,
                MarkerMesh = markerMesh
            };

            _boneControls[i] = controlItem;
        }

        // Apply initial layer visibility and collision state
        ApplyLayerStateToAll();

        GD.Print($"[BoneLayerManager] Overlay built successfully. Summary:" +
                 $" Primary: {_categoryIndices[BoneCategory.Primary].Count}," +
                 $" Clothing: {_categoryIndices[BoneCategory.Clothing].Count}," +
                 $" Fingers: {_categoryIndices[BoneCategory.Fingers].Count}," +
                 $" Face: {_categoryIndices[BoneCategory.Face].Count}," +
                 $" Props: {_categoryIndices[BoneCategory.Props].Count}," +
                 $" Helpers: {_categoryIndices[BoneCategory.Helpers].Count}");
    }


    /// <summary>
    /// Inspects a Source 2 / Deadlock bone name and categorizes it using prioritized matching.
    /// </summary>
    public static BoneCategory ClassifyBone(string rawBoneName)
    {
        if (string.IsNullOrWhiteSpace(rawBoneName))
            return BoneCategory.Helpers;

        string name = rawBoneName.ToLowerInvariant();

        // 1. HELPERS & INTERNAL
        if (name.Contains("twist") || name.Contains("roll") || name.Contains("helper") ||
            name.StartsWith("attach_") || name.Contains("_attach") || name.Contains("ik_") || 
            name.Contains("iktarget") || name.Contains("locator") || name.Contains("null_") || 
            name.Contains("socket") || name.Contains("target_") || name.Contains("dummy") ||
            name.Contains("vfx") || name.Contains("fx_") || name.Contains("camera") ||
            name.Contains("hose") || name.Contains("cocking") || name.Contains("jiggle_helper"))
        {
            return BoneCategory.Helpers;
        }

        // 2. FACE & EXPRESSIONS
        if (name.Contains("eye") || name.Contains("brow") || name.Contains("jaw") ||
            name.Contains("lip") || name.Contains("tongue") || name.Contains("eyelid") ||
            name.Contains("cheek") || name.Contains("nose") || name.Contains("chin") ||
            name.Contains("mouth") || name.Contains("teeth") || name.Contains("expression"))
        {
            return BoneCategory.Face;
        }

        // 3. FINGERS
        if (name.Contains("finger") || name.Contains("thumb") || name.Contains("index") ||
            name.Contains("middle") || name.Contains("ring") || name.Contains("pinky") || 
            name.Contains("pinkie") || Regex.IsMatch(name, @"hand_[lr]_\d+"))
        {
            return BoneCategory.Fingers;
        }

        // 4. CLOTHING & PHYSICS
        if (name.Contains("skirt") || name.Contains("cloth") || name.Contains("dress") ||
            name.Contains("coat") || name.Contains("flap") || name.Contains("cape") ||
            name.Contains("tassel") || name.Contains("ribbon") || name.Contains("strap") ||
            name.Contains("dangle") || name.Contains("ponytail") || name.Contains("fur") ||
            name.Contains("jiggle") || name.Contains("purse") || name.Contains("tabard") ||
            name.Contains("sleeve_dangle") || name.StartsWith("$cloth") || name.Contains("boa"))
        {
            return BoneCategory.Clothing;
        }

        // 5. PROPS & WEAPONS
        if (name.Contains("weapon") || name.Contains("gun") || name.Contains("hammer") ||
            name.Contains("holster") || name.Contains("backpack") || name.Contains("prop") ||
            name.Contains("item") || name.Contains("sword") || name.Contains("knife") ||
            name.Contains("shield") || name.Contains("magazine") || name.Contains("barrel") ||
            name.Contains("bullet") || name.Contains("scabbard") || name.Contains("quiver"))
        {
            return BoneCategory.Props;
        }

        // 6. PRIMARY / ANATOMY
        if (name.Contains("root") || name.Contains("pelvis") || name.Contains("hip") ||
            name.Contains("spine") || name.Contains("chest") || name.Contains("neck") ||
            name.Contains("head") || name.Contains("clavicle") || name.Contains("shoulder") ||
            name.Contains("arm") || name.Contains("bicep") || name.Contains("elbow") ||
            name.Contains("forearm") || name.Contains("wrist") || name.Contains("hand") ||
            name.Contains("leg") || name.Contains("thigh") || name.Contains("knee") ||
            name.Contains("calf") || name.Contains("ankle") || name.Contains("foot") ||
            name.Contains("toe") || name.Contains("butt") || name.Contains("groin"))
        {
            return BoneCategory.Primary;
        }

        return BoneCategory.Primary;
    }

    public void SetLayerEnabled(BoneCategory category, bool enabled)
    {
        if (enabled)
            _activeLayers |= category;
        else
            _activeLayers &= ~category;

        if (!_categoryIndices.TryGetValue(category, out var boneIndices))
            return;

        foreach (int boneIdx in boneIndices)
        {
            if (_boneControls.TryGetValue(boneIdx, out var item))
            {
                ApplyControlState(item, enabled);
            }
        }

        EmitSignal(SignalName.LayerToggled, (int)category, enabled);
    }

    public bool IsLayerEnabled(BoneCategory category)
    {
        return (_activeLayers & category) == category;
    }

    public List<int> GetBonesInCategory(BoneCategory category)
    {
        return _categoryIndices.TryGetValue(category, out var list) ? list : new List<int>();
    }

    public BoneControlItem GetControl(int boneIdx)
    {
        return _boneControls.TryGetValue(boneIdx, out var item) ? item : null;
    }

    public void SetSelectedBone(int boneIdx)
    {
        if (_selectedBoneIdx != -1 && _boneControls.TryGetValue(_selectedBoneIdx, out var oldItem))
        {
            if (oldItem.MarkerMesh != null)
            {
                oldItem.MarkerMesh.MaterialOverride = GetMaterialForCategory(oldItem.Category);
            }
        }

        _selectedBoneIdx = boneIdx;

        if (_selectedBoneIdx != -1 && _boneControls.TryGetValue(_selectedBoneIdx, out var newItem))
        {
            if (newItem.MarkerMesh != null)
            {
                newItem.MarkerMesh.MaterialOverride = _matSelected;
            }
        }
    }

    private void ApplyControlState(BoneControlItem item, bool enabled)
    {
        if (item == null) return;

        if (GodotObject.IsInstanceValid(item.MarkerMesh))
        {
            item.MarkerMesh.Visible = enabled;
        }

        if (GodotObject.IsInstanceValid(item.Area))
        {
            item.Area.CollisionLayer = enabled ? PickingCollisionLayer : 0;
            item.Area.Monitorable = enabled;
        }

        if (GodotObject.IsInstanceValid(item.Shape))
        {
            item.Shape.Disabled = !enabled;
        }
    }

    private void ApplyLayerStateToAll()
    {
        foreach (var kvp in _boneControls)
        {
            bool isLayerActive = (_activeLayers & kvp.Value.Category) != 0;
            ApplyControlState(kvp.Value, isLayerActive);
        }
    }

    private void ClearControls()
    {
        foreach (var item in _boneControls.Values)
        {
            if (item.Attachment != null && IsInstanceValid(item.Attachment))
            {
                item.Attachment.QueueFree();
            }
        }
        _boneControls.Clear();
        _categoryIndices.Clear();
        _selectedBoneIdx = -1;
    }

    private void InitializeMaterials()
    {
        Color primaryCol = GizmoDisplaySettings.BonePrimaryColor;
        primaryCol.A = GizmoDisplaySettings.BoneMarkerOpacity;
        _matPrimary = CreateMarkerMaterial(primaryCol);

        Color clothCol = GizmoDisplaySettings.BoneClothingColor;
        clothCol.A = GizmoDisplaySettings.BoneMarkerOpacity;
        _matClothing = CreateMarkerMaterial(clothCol);

        _matFingers = CreateMarkerMaterial(new Color(1.0f, 0.8f, 0.2f, GizmoDisplaySettings.BoneMarkerOpacity));
        _matFace = CreateMarkerMaterial(new Color(0.7f, 0.4f, 1.0f, GizmoDisplaySettings.BoneMarkerOpacity));
        _matProps = CreateMarkerMaterial(new Color(0.2f, 1.0f, 0.4f, GizmoDisplaySettings.BoneMarkerOpacity));
        _matHelpers = CreateMarkerMaterial(new Color(0.5f, 0.5f, 0.5f, 0.4f));
        _matSelected = CreateMarkerMaterial(new Color(1.0f, 1.0f, 0.0f, 1.0f));
    }

    public void ApplyDisplaySettings()
    {
        if (_matPrimary != null)
        {
            Color primaryCol = GizmoDisplaySettings.BonePrimaryColor;
            primaryCol.A = GizmoDisplaySettings.BoneMarkerOpacity;
            _matPrimary.AlbedoColor = primaryCol;
        }

        if (_matClothing != null)
        {
            Color clothCol = GizmoDisplaySettings.BoneClothingColor;
            clothCol.A = GizmoDisplaySettings.BoneMarkerOpacity;
            _matClothing.AlbedoColor = clothCol;
        }

        if (_matFingers != null)
        {
            Color col = new Color(1.0f, 0.8f, 0.2f, GizmoDisplaySettings.BoneMarkerOpacity);
            _matFingers.AlbedoColor = col;
        }

        if (_matFace != null)
        {
            Color col = new Color(0.7f, 0.4f, 1.0f, GizmoDisplaySettings.BoneMarkerOpacity);
            _matFace.AlbedoColor = col;
        }

        if (_matProps != null)
        {
            Color col = new Color(0.2f, 1.0f, 0.4f, GizmoDisplaySettings.BoneMarkerOpacity);
            _matProps.AlbedoColor = col;
        }

        // Update scale on existing markers
        Vector3 newScale = Vector3.One * GizmoDisplaySettings.BoneMarkerScale;
        foreach (var item in _boneControls.Values)
        {
            if (item.MarkerMesh != null) item.MarkerMesh.Scale = newScale;
            if (item.Shape != null) item.Shape.Scale = newScale;
        }
    }

    private static StandardMaterial3D CreateMarkerMaterial(Color color)
    {
        return new StandardMaterial3D
        {
            AlbedoColor = color,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            NoDepthTest = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
    }

    private StandardMaterial3D GetMaterialForCategory(BoneCategory category)
    {
        return category switch
        {
            BoneCategory.Primary => _matPrimary,
            BoneCategory.Clothing => _matClothing,
            BoneCategory.Fingers => _matFingers,
            BoneCategory.Face => _matFace,
            BoneCategory.Props => _matProps,
            BoneCategory.Helpers => _matHelpers,
            _ => _matPrimary
        };
    }

    private Shape3D CreateCollisionShapeForBone(string boneName, BoneCategory category)
    {
        string name = boneName.ToLowerInvariant();

        if (category == BoneCategory.Fingers)
            return new CapsuleShape3D { Radius = 0.012f, Height = 0.04f };

        if (category == BoneCategory.Clothing)
            return new SphereShape3D { Radius = 0.035f };

        if (category == BoneCategory.Face)
            return new SphereShape3D { Radius = 0.018f };

        if (category == BoneCategory.Props)
            return new BoxShape3D { Size = new Vector3(0.06f, 0.06f, 0.06f) };

        if (name.Contains("hand") || name.Contains("foot"))
            return new SphereShape3D { Radius = 0.04f };

        return new CapsuleShape3D { Radius = 0.03f, Height = 0.1f };
    }

    private Mesh CreateMarkerMeshForBone(string boneName, BoneCategory category)
    {
        string name = boneName.ToLowerInvariant();

        switch (category)
        {
            case BoneCategory.Clothing:
                return new TorusMesh
                {
                    InnerRadius = 0.02f,
                    OuterRadius = 0.035f
                };

            case BoneCategory.Props:
                return new BoxMesh
                {
                    Size = new Vector3(0.04f, 0.04f, 0.04f),
                    FlipFaces = true
                };

            case BoneCategory.Face:
                return new TorusMesh
                {
                    InnerRadius = 0.012f,
                    OuterRadius = 0.018f
                };

            case BoneCategory.Fingers:
                return new CapsuleMesh
                {
                    Radius = 0.006f,
                    Height = 0.03f
                };

            case BoneCategory.Helpers:
                return new SphereMesh
                {
                    Radius = 0.008f,
                    Height = 0.016f
                };

            case BoneCategory.Primary:
            default:
                if (name.Contains("hand") || name.Contains("wrist"))
                {
                    return new TorusMesh
                    {
                        InnerRadius = 0.045f,
                        OuterRadius = 0.055f
                    };
                }
                return new SphereMesh
                {
                    Radius = 0.02f,
                    Height = 0.04f
                };
        }
    }
}
