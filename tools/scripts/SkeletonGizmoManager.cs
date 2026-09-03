using Gizmo3DPlugin;
using Godot;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

public partial class SkeletonGizmoManager : Node3D
{
    [Export] public Skeleton3D TargetSkeleton { get; set; }
    [Export] public PoseEditorUI UIManager { get; set; }
    
    // The layer to place our bone Area3D colliders on. Default: layer 32.
    [Export(PropertyHint.Layers3DPhysics)] public uint CollisionLayer { get; set; } = 2147483648; // Bit 32
    
    [ExportCategory("Bone Filtering")]
    private readonly string[] IgnoreBonePatterns = {
        "twist", "helper", "jiggle", "cloth", "attach_", "vfx", "dynamic", "fx_", "ik_",
        "fur", "$cloth", "dress", "purse", "hose", "cocking", "IKTARGET"
    };

    [ExportCategory("Debug Wireframe")]
    [Export] public Material XRayMaterial { get; set; }

    private List<int> _activeBones = new List<int>();
    private Dictionary<int, BoneAttachment3D> _boneAttachments = new Dictionary<int, BoneAttachment3D>();
    private StandardMaterial3D _controlMatDefault;
    private StandardMaterial3D _controlMatSelected;
    
    // Maps bone index to its custom control mesh instance
    private Dictionary<int, MeshInstance3D> _boneControls = new Dictionary<int, MeshInstance3D>();
    
    private MeshInstance3D _skeletonMeshInstance;
    private ImmediateMesh _immediateMesh;

    private int _selectedBoneIdx = -1;
    
    private Gizmo3D _gizmo;
    private Node3D _dummyTarget;
    private bool _isGizmoDragging = false;

    public override void _Ready()
    {
        if (TargetSkeleton == null)
        {
            GD.PrintErr("SkeletonGizmoManager: TargetSkeleton is not set!");
            return;
        }

        if (UIManager != null)
        {
            UIManager.OnXRayToggled += SetupDots;
            UIManager.OnLinesToggled += SetupLines;
            UIManager.OnBoneSelectedFromUI += SelectBone;
        }

        // Initialize control materials
        _controlMatDefault = new StandardMaterial3D();
        _controlMatDefault.AlbedoColor = new Color(0, 1, 1, 0.5f); // Cyan transparent
        _controlMatDefault.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        _controlMatDefault.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        _controlMatDefault.NoDepthTest = true;

        _controlMatSelected = new StandardMaterial3D();
        _controlMatSelected.AlbedoColor = new Color(1, 1, 0, 1.0f); // Yellow solid
        _controlMatSelected.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        _controlMatSelected.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        _controlMatSelected.NoDepthTest = true;

        SetupBones();
        InitSkeletonMesh();
        InitGizmo();
        
        if (UIManager != null)
        {
            SetupDots(UIManager.IsXRayEnabled);
            SetupLines(UIManager.IsLinesEnabled);
        }
    }

    private void InitGizmo()
    {
        _gizmo = new Gizmo3D();
        _gizmo.Mode = Gizmo3D.ToolMode.Rotate; // Will be set dynamically
        AddChild(_gizmo);

        _dummyTarget = new Node3D();
        AddChild(_dummyTarget);

        _gizmo.TransformChanged += (mode, value) => {
            _isGizmoDragging = true;
            HandleGizmoTransformChanged(_dummyTarget.GlobalTransform);
        };
        _gizmo.TransformEnd += (mode) => {
            _isGizmoDragging = false;
        };
    }

    private void SetupBones()
    {
        int boneCount = TargetSkeleton.GetBoneCount();
        for (int i = 0; i < boneCount; i++)
        {
            string boneName = TargetSkeleton.GetBoneName(i)?.ToLower() ?? "";
            
            bool ignore = false;
            foreach (var pattern in IgnoreBonePatterns)
            {
                if (boneName.Contains(pattern.ToLower()))
                {
                    ignore = true;
                    break;
                }
            }

            if (ignore) continue;

            _activeBones.Add(i);

            // Create BoneAttachment3D
            var attachment = new BoneAttachment3D();
            attachment.BoneName = TargetSkeleton.GetBoneName(i);
            attachment.BoneIdx = i;
            TargetSkeleton.AddChild(attachment);
            _boneAttachments[i] = attachment;

            // Create Area3D for picking
            var area = new Area3D();
            area.CollisionLayer = CollisionLayer;
            area.CollisionMask = 0; // Doesn't need to scan for things
            attachment.AddChild(area);

            // Bind click event
            int boneIdx = i; // local copy for closure
            area.InputEvent += (camera, @event, position, normal, shapeIdx) => 
            {
                OnBoneAreaInputEvent(camera, @event, position, normal, shapeIdx, boneIdx);
            };

            // Create CollisionShape3D
            var shape = new CollisionShape3D();
            var capsule = new CapsuleShape3D();
            capsule.Radius = 0.03f;
            capsule.Height = 0.1f;
            shape.Shape = capsule;
            
            // Orient the capsule along the Y axis usually (Godot's default)
            area.AddChild(shape);

            // Create Custom Bone Control Mesh
            var controlMeshInstance = new MeshInstance3D();
            
            if (boneName.Contains("weapon") || boneName.Contains("gun") || boneName.Contains("prop"))
            {
                var box = new BoxMesh();
                box.Size = new Vector3(0.025f, 0.025f, 0.025f);
                box.FlipFaces = true;
                controlMeshInstance.Mesh = box;
            }
            else if (boneName.Contains("eye"))
            {
                var torus = new TorusMesh();
                torus.InnerRadius = 0.015f;
                torus.OuterRadius = 0.02f;
                controlMeshInstance.Mesh = torus;
                controlMeshInstance.Scale = new Vector3(1, 0.5f, 1); // Oval shape
            }
            else if (boneName.Contains("hand"))
            {
                var torus = new TorusMesh();
                torus.InnerRadius = 0.05f;
                torus.OuterRadius = 0.06f;
                controlMeshInstance.Mesh = torus;
            }
            else if (boneName.Contains("finger"))
            {
                var cap = new CapsuleMesh();
                cap.Radius = 0.005f;
                cap.Height = 0.03f;
                controlMeshInstance.Mesh = cap;
            }
            else
            {
                // Default small dot
                var sphereMesh = new SphereMesh();
                sphereMesh.Radius = 0.015f;
                sphereMesh.Height = 0.03f;
                controlMeshInstance.Mesh = sphereMesh;
            }
            
            controlMeshInstance.MaterialOverride = _controlMatDefault;
            attachment.AddChild(controlMeshInstance);
            
            _boneControls[i] = controlMeshInstance;
        }
    }

    private void OnBoneAreaInputEvent(Node camera, InputEvent @event, Vector3 position, Vector3 normal, long shapeIdx, int boneIdx)
    {
        if (@event is InputEventMouseButton mouseButton && mouseButton.Pressed && mouseButton.ButtonIndex == MouseButton.Left)
        {
            SelectBone(boneIdx);
            
            // Update the UI if it's connected
            if (UIManager != null)
            {
                UIManager.SelectBone(boneIdx);
            }
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseBtn && mouseBtn.Pressed && mouseBtn.ButtonIndex == MouseButton.Left)
        {
            var camera = GetViewport().GetCamera3D();
            if (camera != null)
            {
                var spaceState = GetWorld3D().DirectSpaceState;
                var from = camera.ProjectRayOrigin(mouseBtn.Position);
                var to = from + camera.ProjectRayNormal(mouseBtn.Position) * 1000f;
                var query = PhysicsRayQueryParameters3D.Create(from, to, CollisionLayer);
                query.CollideWithAreas = true;
                
                var result = spaceState.IntersectRay(query);
                if (result.Count == 0)
                {
                    // Clicked on nothing! Deselect
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
        // Reset old highlight
        if (_selectedBoneIdx != -1 && _boneControls.ContainsKey(_selectedBoneIdx))
        {
            _boneControls[_selectedBoneIdx].MaterialOverride = _controlMatDefault;
        }

        _selectedBoneIdx = boneIdx;

        // Apply new highlight
        if (_selectedBoneIdx != -1 && _boneControls.ContainsKey(_selectedBoneIdx))
        {
            _boneControls[_selectedBoneIdx].MaterialOverride = _controlMatSelected;
        }

        SpawnOrMoveGizmo();
    }

    private void SpawnOrMoveGizmo()
    {
        if (_gizmo == null || _dummyTarget == null) 
        {
            GD.PrintErr("Gizmo or Dummy Target not found");
            return;
        }

        if (_selectedBoneIdx == -1 || !_boneAttachments.ContainsKey(_selectedBoneIdx))
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
        var attachment = _boneAttachments[_selectedBoneIdx];
        _dummyTarget.GlobalTransform = attachment.GlobalTransform;
        
        string bName = TargetSkeleton.GetBoneName(_selectedBoneIdx).ToLower();
        bool isWeapon = bName.Contains("weapon") || bName.Contains("gun") || bName.Contains("prop");
        
        if (isWeapon || TargetSkeleton.GetBoneParent(_selectedBoneIdx) == -1)
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

        // We need to convert the Gizmo's GlobalTransform to the Bone's Local Pose (relative to parent)
        Transform3D skelGlobal = TargetSkeleton.GlobalTransform;
        Transform3D boneGlobal = globalTransform;
        
        // Convert to Skeleton Space
        Transform3D boneInSkelSpace = skelGlobal.AffineInverse() * boneGlobal;

        int parentIdx = TargetSkeleton.GetBoneParent(_selectedBoneIdx);
        Transform3D parentSkelSpace = Transform3D.Identity;
        if (parentIdx != -1)
        {
            parentSkelSpace = TargetSkeleton.GetBoneGlobalPose(parentIdx);
        }

        // Convert to Parent Bone Space (which is the local pose)
        Transform3D localPose = parentSkelSpace.AffineInverse() * boneInSkelSpace;

        // Note: SetBonePoseRotation expects a Quaternion.
        TargetSkeleton.SetBonePoseRotation(_selectedBoneIdx, localPose.Basis.GetRotationQuaternion());
        
        // Only set position if translation is enabled (e.g. root or weapon)
        if (_gizmo != null && _gizmo.Mode.HasFlag(Gizmo3D.ToolMode.Move))
        {
            TargetSkeleton.SetBonePosePosition(_selectedBoneIdx, localPose.Origin);
        }

        if (UIManager != null)
        {
            UIManager.UpdateSlidersFromBone();
        }
    }

    private void InitSkeletonMesh()
    {
        _skeletonMeshInstance = new MeshInstance3D();
        _immediateMesh = new ImmediateMesh();
        _skeletonMeshInstance.Mesh = _immediateMesh;

        if (XRayMaterial == null)
        {
            var mat = new StandardMaterial3D();
            mat.AlbedoColor = new Color(0, 1, 1, 0.3f); // Cyan, lower opacity
            mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            mat.NoDepthTest = true;
            XRayMaterial = mat;
        }
        
        _skeletonMeshInstance.MaterialOverride = XRayMaterial;
        _skeletonMeshInstance.Visible = false;
        TargetSkeleton.AddChild(_skeletonMeshInstance);
    }

    private void SetupLines(bool enabled)
    {
        if (_skeletonMeshInstance != null)
        {
            _skeletonMeshInstance.Visible = enabled;
        }
    }

    private void SetupDots(bool enabled)
    {
        foreach (var control in _boneControls.Values)
        {
            control.Visible = enabled;
        }
    }

    public override void _Process(double delta)
    {
        if (_skeletonMeshInstance != null && _skeletonMeshInstance.Visible && TargetSkeleton != null)
        {
            DrawSkeleton();
        }

        // Keep Gizmo attached to bone in case animation or something else moves it
        if (_gizmo != null && _gizmo.Visible && _selectedBoneIdx != -1 && !_isGizmoDragging)
        {
            if (_boneAttachments.ContainsKey(_selectedBoneIdx))
            {
                _dummyTarget.GlobalTransform = _boneAttachments[_selectedBoneIdx].GlobalTransform;
            }
        }
    }

    private void DrawSkeleton()
    {
        _immediateMesh.ClearSurfaces();
        _immediateMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);

        int boneCount = TargetSkeleton.GetBoneCount();
        for (int i = 0; i < boneCount; i++)
        {
            int parentIdx = TargetSkeleton.GetBoneParent(i);
            if (parentIdx != -1)
            {
                // We draw in Skeleton Local Space, so we use GetBoneGlobalPose() which is relative to the Skeleton.
                Vector3 p1 = TargetSkeleton.GetBoneGlobalPose(parentIdx).Origin;
                Vector3 p2 = TargetSkeleton.GetBoneGlobalPose(i).Origin;

                _immediateMesh.SurfaceAddVertex(p1);
                _immediateMesh.SurfaceAddVertex(p2);
            }
        }

        _immediateMesh.SurfaceEnd();
    }
}
