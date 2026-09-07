using Gizmo3DPlugin;
using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Handles interactive 3D gizmo manipulation, raycast bone picking, and wireframe visualization.
/// Integrates with BoneLayerManager to preserve 100% of the Skeleton3D hierarchy.
/// </summary>
public partial class SkeletonGizmoManager : Node3D
{
    [Export] public Skeleton3D TargetSkeleton { get; set; }
    
    private IBoneUIController _uiManager;
    public IBoneUIController UIManager
    {
        get => _uiManager;
        set => SetUIManager(value);
    }
    
    /// <summary>
    /// Physics layer for Area3D bone picking colliders (Default: Layer 32 = 2147483648).
    /// </summary>
    [Export(PropertyHint.Layers3DPhysics)] public uint CollisionLayer { get; set; } = 2147483648;

    [ExportCategory("Debug Wireframe")]
    [Export] public Material XRayMaterial { get; set; }

    private BoneLayerManager _boneLayerManager;
    public BoneLayerManager LayerManager => _boneLayerManager;

    private MeshInstance3D _skeletonMeshInstance;
    private ImmediateMesh _immediateMesh;

    private int _selectedBoneIdx = -1;
    private Gizmo3D _gizmo;
    private Node3D _dummyTarget;
    private bool _isGizmoDragging = false;
    private bool _areDotsGloballyEnabled = true;

    public void SetUIManager(IBoneUIController uiManager)
    {
        if (_uiManager != null)
        {
            _uiManager.OnXRayToggled -= SetupDots;
            _uiManager.OnLinesToggled -= SetupLines;
            _uiManager.OnBoneSelectedFromUI -= SelectBone;
        }

        _uiManager = uiManager;
        if (_uiManager != null)
        {
            if (_boneLayerManager != null)
            {
                _uiManager.SetBoneLayerManager(_boneLayerManager);
            }
            _uiManager.OnXRayToggled += SetupDots;
            _uiManager.OnLinesToggled += SetupLines;
            _uiManager.OnBoneSelectedFromUI += SelectBone;
            SetupDots(_uiManager.IsXRayEnabled);
            SetupLines(_uiManager.IsLinesEnabled);
        }
    }

    public override void _Ready()
    {
        if (TargetSkeleton == null)
        {
            GD.PrintErr("SkeletonGizmoManager: TargetSkeleton is not set!");
            return;
        }

        // 1. Initialize BoneLayerManager
        _boneLayerManager = new BoneLayerManager
        {
            Name = "BoneLayerManager"
        };
        AddChild(_boneLayerManager);
        _boneLayerManager.BuildBoneHierarchyOverlay(TargetSkeleton, CollisionLayer);
        _boneLayerManager.BoneClicked += OnBonePicked;

        // 2. Connect UI events safely without duplicate subscriptions
        if (_uiManager != null)
        {
            _uiManager.SetBoneLayerManager(_boneLayerManager);
            _uiManager.OnXRayToggled -= SetupDots;
            _uiManager.OnLinesToggled -= SetupLines;
            _uiManager.OnBoneSelectedFromUI -= SelectBone;

            _uiManager.OnXRayToggled += SetupDots;
            _uiManager.OnLinesToggled += SetupLines;
            _uiManager.OnBoneSelectedFromUI += SelectBone;
        }

        // 3. Initialize Skeleton line wireframe & 3D Gizmo
        InitSkeletonMesh();
        InitGizmo();
        
        GizmoDisplaySettings.OnSettingsChanged += ApplyDisplaySettings;
        ApplyDisplaySettings();

        if (UIManager != null)
        {
            SetupDots(UIManager.IsXRayEnabled);
            SetupLines(UIManager.IsLinesEnabled);
        }
    }

    public override void _ExitTree()
    {
        GizmoDisplaySettings.OnSettingsChanged -= ApplyDisplaySettings;
        if (_uiManager != null)
        {
            _uiManager.OnXRayToggled -= SetupDots;
            _uiManager.OnLinesToggled -= SetupLines;
            _uiManager.OnBoneSelectedFromUI -= SelectBone;
        }
        if (_boneLayerManager != null && GodotObject.IsInstanceValid(_boneLayerManager))
        {
            _boneLayerManager.BoneClicked -= OnBonePicked;
        }
    }

    private void ApplyDisplaySettings()
    {
        if (XRayMaterial is StandardMaterial3D stdMat)
        {
            Color col = GizmoDisplaySettings.XRayLineColor;
            col.A = GizmoDisplaySettings.XRayLineOpacity;
            stdMat.AlbedoColor = col;
        }
    }

    private void InitGizmo()
    {
        _gizmo = new Gizmo3D();
        _gizmo.Mode = Gizmo3D.ToolMode.Rotate;
        _gizmo.Layers = 2;
        AddChild(_gizmo);

        _dummyTarget = new Node3D { Name = "GizmoDummyTarget" };
        AddChild(_dummyTarget);

        _gizmo.TransformChanged += (mode, value) => {
            _isGizmoDragging = true;
            HandleGizmoTransformChanged(_dummyTarget.GlobalTransform);
        };
        _gizmo.TransformEnd += (mode) => {
            _isGizmoDragging = false;
        };
    }

    private void OnBonePicked(int boneIdx)
    {
        SelectBone(boneIdx);
        if (UIManager != null)
        {
            UIManager.SelectBone(boneIdx);
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseBtn && mouseBtn.Pressed && mouseBtn.ButtonIndex == MouseButton.Left)
        {
            // If user clicked in empty space, deselect current bone
            var camera = GetViewport()?.GetCamera3D();
            if (camera != null)
            {
                var spaceState = GetWorld3D()?.DirectSpaceState;
                if (spaceState == null) return;

                var from = camera.ProjectRayOrigin(mouseBtn.Position);
                var to = from + camera.ProjectRayNormal(mouseBtn.Position) * 1000f;
                var query = PhysicsRayQueryParameters3D.Create(from, to, CollisionLayer);
                query.CollideWithAreas = true;
                
                var result = spaceState.IntersectRay(query);
                if (result.Count == 0)
                {
                    SelectBone(-1);
                    if (UIManager != null)
                    {
                        UIManager.SelectBone(-1);
                    }
                }
            }
        }
    }

    public void SelectBone(int boneIdx)
    {
        _selectedBoneIdx = boneIdx;

        if (_boneLayerManager != null)
        {
            _boneLayerManager.SetSelectedBone(boneIdx);
        }

        SpawnOrMoveGizmo();
    }

    private void SpawnOrMoveGizmo()
    {
        if (_gizmo == null || _dummyTarget == null) return;

        if (_selectedBoneIdx == -1 || TargetSkeleton == null)
        {
            _gizmo.ClearSelection();
            _gizmo.Visible = false;
            return;
        }

        var controlItem = _boneLayerManager?.GetControl(_selectedBoneIdx);
        if (controlItem == null || controlItem.Attachment == null)
        {
            _gizmo.ClearSelection();
            _gizmo.Visible = false;
            return;
        }
       
        _gizmo.Visible = true;
        if (!_gizmo.IsSelected(_dummyTarget))
        {
            _gizmo.ClearSelection();
            _gizmo.Select(_dummyTarget);
        }

        _dummyTarget.GlobalTransform = controlItem.Attachment.GlobalTransform;
        
        string bName = TargetSkeleton.GetBoneName(_selectedBoneIdx)?.ToLowerInvariant() ?? "";
        bool isRootOrProp = bName.Contains("weapon") || bName.Contains("gun") || 
                           bName.Contains("prop") || TargetSkeleton.GetBoneParent(_selectedBoneIdx) == -1;
        
        if (isRootOrProp)
        {
            _gizmo.Mode = Gizmo3D.ToolMode.Rotate | Gizmo3D.ToolMode.Move;
        }
        else
        {
            _gizmo.Mode = Gizmo3D.ToolMode.Rotate;
        }
        
        if (!_gizmo.IsSelected(_dummyTarget))
        {
            _gizmo.Select(_dummyTarget);
        }
    }

    private void HandleGizmoTransformChanged(Transform3D globalTransform)
    {
        if (_selectedBoneIdx == -1 || TargetSkeleton == null) return;

        // Convert Gizmo's GlobalTransform to the Bone's Local Pose (relative to parent)
        Transform3D skelGlobal = TargetSkeleton.GlobalTransform;
        Transform3D boneGlobal = globalTransform;
        
        Transform3D boneInSkelSpace = skelGlobal.AffineInverse() * boneGlobal;

        int parentIdx = TargetSkeleton.GetBoneParent(_selectedBoneIdx);
        Transform3D parentSkelSpace = Transform3D.Identity;
        if (parentIdx != -1)
        {
            parentSkelSpace = TargetSkeleton.GetBoneGlobalPose(parentIdx);
        }

        Transform3D localPose = parentSkelSpace.AffineInverse() * boneInSkelSpace;

        TargetSkeleton.SetBonePoseRotation(_selectedBoneIdx, localPose.Basis.GetRotationQuaternion());
        
        if (_gizmo != null && _gizmo.Mode.HasFlag(Gizmo3D.ToolMode.Move))
        {
            TargetSkeleton.SetBonePosePosition(_selectedBoneIdx, localPose.Origin);
        }

        ProceduralClothSolver.Conform(TargetSkeleton);

        if (UIManager != null)
        {
            UIManager.UpdateSlidersFromBone();
        }
    }

    private void InitSkeletonMesh()
    {
        _skeletonMeshInstance = new MeshInstance3D();
        _skeletonMeshInstance.Layers = 2;
        _immediateMesh = new ImmediateMesh();
        _skeletonMeshInstance.Mesh = _immediateMesh;

        if (XRayMaterial == null)
        {
            Color col = GizmoDisplaySettings.XRayLineColor;
            col.A = GizmoDisplaySettings.XRayLineOpacity;
            var mat = new StandardMaterial3D
            {
                AlbedoColor = col,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                NoDepthTest = true
            };
            XRayMaterial = mat;
        }
        
        _skeletonMeshInstance.MaterialOverride = XRayMaterial;
        _skeletonMeshInstance.Visible = false;
        TargetSkeleton.AddChild(_skeletonMeshInstance);
    }

    private void SetupLines(bool enabled)
    {
        if (GodotObject.IsInstanceValid(_skeletonMeshInstance))
        {
            _skeletonMeshInstance.Visible = enabled;
        }
    }

    private void SetupDots(bool enabled)
    {
        _areDotsGloballyEnabled = enabled;
        if (_boneLayerManager == null || !GodotObject.IsInstanceValid(_boneLayerManager)) return;

        // If dots are globally disabled, hide all markers without altering layer configuration
        foreach (var item in _boneLayerManager.BoneControls.Values)
        {
            if (item != null && GodotObject.IsInstanceValid(item.MarkerMesh))
            {
                bool isLayerActive = _boneLayerManager.IsLayerEnabled(item.Category);
                item.MarkerMesh.Visible = enabled && isLayerActive;
            }
        }
    }

    public override void _Process(double delta)
    {
        if (GodotObject.IsInstanceValid(_skeletonMeshInstance) && _skeletonMeshInstance.Visible && GodotObject.IsInstanceValid(TargetSkeleton))
        {
            DrawSkeleton();
        }

        // Keep Gizmo attached to bone in case animation or slider moves it
        if (GodotObject.IsInstanceValid(_gizmo) && _gizmo.Visible && _selectedBoneIdx != -1 && !_isGizmoDragging)
        {
            var item = _boneLayerManager?.GetControl(_selectedBoneIdx);
            if (item != null && GodotObject.IsInstanceValid(item.Attachment) && GodotObject.IsInstanceValid(_dummyTarget))
            {
                _dummyTarget.GlobalTransform = item.Attachment.GlobalTransform;
            }
        }
    }

    private void DrawSkeleton()
    {
        if (!GodotObject.IsInstanceValid(TargetSkeleton) || !GodotObject.IsInstanceValid(_immediateMesh)) return;

        _immediateMesh.ClearSurfaces();
        _immediateMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);

        int boneCount = TargetSkeleton.GetBoneCount();
        for (int i = 0; i < boneCount; i++)
        {
            int parentIdx = TargetSkeleton.GetBoneParent(i);
            if (parentIdx != -1)
            {
                Vector3 p1 = TargetSkeleton.GetBoneGlobalPose(parentIdx).Origin;
                Vector3 p2 = TargetSkeleton.GetBoneGlobalPose(i).Origin;

                _immediateMesh.SurfaceAddVertex(p1);
                _immediateMesh.SurfaceAddVertex(p2);
            }
        }

        _immediateMesh.SurfaceEnd();
    }
}
