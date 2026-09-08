#pragma warning disable CS0618
using Gizmo3DPlugin;
using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Manages interactive Inverse Kinematics (IK) for character limbs (Left/Right Arms & Legs).
/// Supports SkeletonIK3D with interactive tip targets AND elbow/knee pole magnets,
/// opacity controls, seamless FK/IK switching with auto-snapping, and synchronized procedural cloth solving.
/// </summary>
public partial class CharacterIKManager : Node3D
{
    public enum LimbChainType
    {
        LeftArm,
        RightArm,
        LeftLeg,
        RightLeg
    }

    public enum IKHandleType
    {
        TipTarget,
        PoleTarget
    }

    public class IKChainInfo
    {
        public LimbChainType Type;
        public string Name;
        public int RootBoneIndex = -1;
        public int MidBoneIndex = -1;
        public int TipBoneIndex = -1;
        public string RootBoneName = string.Empty;
        public string MidBoneName = string.Empty;
        public string TipBoneName = string.Empty;

        public SkeletonIK3D SkeletonIK;
        public Node3D TargetHandle;
        public MeshInstance3D HandleMesh;
        public Area3D HandleArea;
        public StandardMaterial3D TipMaterial;

        public Node3D PoleHandle;
        public MeshInstance3D PoleMesh;
        public Area3D PoleArea;
        public StandardMaterial3D PoleMaterial;

        public bool IsActive = false;
    }

    [Export] public Skeleton3D TargetSkeleton { get; set; }
    [Export(PropertyHint.Layers3DPhysics)] public uint PickingCollisionLayer { get; set; } = 2147483648; // Layer 32

    private readonly Dictionary<LimbChainType, IKChainInfo> _chains = new();
    private Gizmo3D _ikGizmo;
    private Node3D _dummyGizmoTarget;
    private (LimbChainType Chain, IKHandleType Handle)? _selectedHandle = null;

    private bool _masterIKEnabled = false;
    private bool _armsIKEnabled = true;
    private bool _legsIKEnabled = true;
    private float _ikBlendWeight = 1.0f;
    private float _handlesOpacity = 0.85f;

    public bool MasterIKEnabled => _masterIKEnabled;
    public bool ArmsIKEnabled => _armsIKEnabled;
    public bool LegsIKEnabled => _legsIKEnabled;
    public float IKBlendWeight => _ikBlendWeight;
    public float HandlesOpacity => _handlesOpacity;

    public event Action<bool> OnIKModeChanged;
    public event Action<LimbChainType, bool> OnLimbIKChanged;

    public override void _Ready()
    {
        InitGizmo();
    }

    public override void _ExitTree()
    {
        ClearChains();
        if (_ikGizmo != null && GodotObject.IsInstanceValid(_ikGizmo))
        {
            _ikGizmo.QueueFree();
            _ikGizmo = null;
        }
    }

    private void InitGizmo()
    {
        _ikGizmo = new Gizmo3D
        {
            Name = "IKTargetGizmo",
            Mode = Gizmo3D.ToolMode.Move,
            Layers = 2
        };
        AddChild(_ikGizmo);

        _dummyGizmoTarget = new Node3D { Name = "IKDummyGizmoTarget" };
        AddChild(_dummyGizmoTarget);

        _ikGizmo.TransformChanged += (mode, val) =>
        {
            if (!_selectedHandle.HasValue) return;

            var (chainType, handleType) = _selectedHandle.Value;
            if (!_chains.TryGetValue(chainType, out var chain)) return;

            if (handleType == IKHandleType.TipTarget)
            {
                if (chain.TargetHandle != null && GodotObject.IsInstanceValid(chain.TargetHandle))
                {
                    chain.TargetHandle.GlobalTransform = _dummyGizmoTarget.GlobalTransform;
                    OnTargetHandleMoved(chain);
                }
            }
            else if (handleType == IKHandleType.PoleTarget)
            {
                if (chain.PoleHandle != null && GodotObject.IsInstanceValid(chain.PoleHandle))
                {
                    chain.PoleHandle.GlobalTransform = _dummyGizmoTarget.GlobalTransform;
                    OnPoleHandleMoved(chain);
                }
            }
        };

        _ikGizmo.Visible = false;
    }

    public void Setup(Skeleton3D skeleton)
    {
        ClearChains();
        TargetSkeleton = skeleton;
        if (TargetSkeleton == null) return;

        // Build 4 primary limb chains
        BuildChain(LimbChainType.LeftArm, "Left Arm",
            new[] { "arm_upper_L", "arm_upper_l", "upperarm_l", "bip_upperarm_l", "arm_l" },
            new[] { "elbow_L", "elbow_l", "arm_lower_L", "arm_lower_l", "bip_lowerarm_l", "forearm_l" },
            new[] { "hand_L", "hand_l", "bip_hand_l", "wrist_l" });

        BuildChain(LimbChainType.RightArm, "Right Arm",
            new[] { "arm_upper_R", "arm_upper_r", "upperarm_r", "bip_upperarm_r", "arm_r" },
            new[] { "elbow_R", "elbow_r", "arm_lower_R", "arm_lower_r", "bip_lowerarm_r", "forearm_r" },
            new[] { "hand_R", "hand_r", "bip_hand_r", "wrist_r" });

        BuildChain(LimbChainType.LeftLeg, "Left Leg",
            new[] { "leg_upper_L", "leg_upper_l", "thigh_L", "thigh_l", "bip_thigh_l", "leg_l" },
            new[] { "leg_lower_L", "leg_lower_l", "knee_L", "knee_l", "bip_lowerleg_l", "calf_l" },
            new[] { "ankle_L", "ankle_l", "foot_L", "foot_l", "bip_foot_l" });

        BuildChain(LimbChainType.RightLeg, "Right Leg",
            new[] { "leg_upper_R", "leg_upper_r", "thigh_R", "thigh_r", "bip_thigh_r", "leg_r" },
            new[] { "leg_lower_R", "leg_lower_r", "knee_R", "knee_r", "bip_lowerleg_r", "calf_r" },
            new[] { "ankle_R", "ankle_r", "foot_R", "foot_r", "bip_foot_r" });

        UpdateActiveChains();
    }

    private void BuildChain(LimbChainType type, string displayName, string[] rootCandidates, string[] midCandidates, string[] tipCandidates)
    {
        if (TargetSkeleton == null) return;

        int rootIdx = FindFirstBone(TargetSkeleton, rootCandidates);
        int midIdx = FindFirstBone(TargetSkeleton, midCandidates);
        int tipIdx = FindFirstBone(TargetSkeleton, tipCandidates);

        if (rootIdx == -1 || tipIdx == -1)
        {
            GD.Print($"[CharacterIKManager] Could not find bones for {displayName}. Chain skipped.");
            return;
        }

        string rootName = TargetSkeleton.GetBoneName(rootIdx);
        string midName = midIdx != -1 ? TargetSkeleton.GetBoneName(midIdx) : string.Empty;
        string tipName = TargetSkeleton.GetBoneName(tipIdx);

        // 1. Create Tip Target Handle Node3D in World Space
        var tipHandleNode = new Node3D { Name = $"IKTarget_{type}" };
        AddChild(tipHandleNode);

        var tipMesh = new MeshInstance3D
        {
            Name = "TipVisual",
            Mesh = new SphereMesh { Radius = 0.05f, Height = 0.1f },
            Layers = 3
        };

        Color tipBaseColor = (type == LimbChainType.LeftArm || type == LimbChainType.RightArm)
            ? PlaygroundThemeHelper.SageGreen
            : PlaygroundThemeHelper.Parchment;

        var tipMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(tipBaseColor.R, tipBaseColor.G, tipBaseColor.B, _handlesOpacity),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            NoDepthTest = true
        };
        tipMesh.MaterialOverride = tipMat;
        tipHandleNode.AddChild(tipMesh);

        var tipArea = new Area3D
        {
            Name = "TipArea",
            CollisionLayer = PickingCollisionLayer,
            CollisionMask = 0
        };
        tipArea.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.08f } });
        tipHandleNode.AddChild(tipArea);

        tipArea.InputEvent += (Node camera, InputEvent @event, Vector3 pos, Vector3 norm, long shapeIdx) =>
        {
            if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                SelectHandle(type, IKHandleType.TipTarget);
                GetViewport()?.SetInputAsHandled();
            }
        };

        // 2. Create Elbow / Knee Pole Target Handle
        var poleHandleNode = new Node3D { Name = $"IKPole_{type}" };
        AddChild(poleHandleNode);

        var poleMesh = new MeshInstance3D
        {
            Name = "PoleVisual",
            Mesh = new SphereMesh { Radius = 0.035f, Height = 0.07f },
            Layers = 3
        };

        Color poleBaseColor = new Color(0.92f, 0.72f, 0.28f); // Warm amber gold for poles
        var poleMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(poleBaseColor.R, poleBaseColor.G, poleBaseColor.B, _handlesOpacity),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            NoDepthTest = true
        };
        poleMesh.MaterialOverride = poleMat;
        poleHandleNode.AddChild(poleMesh);

        var poleArea = new Area3D
        {
            Name = "PoleArea",
            CollisionLayer = PickingCollisionLayer,
            CollisionMask = 0
        };
        poleArea.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.07f } });
        poleHandleNode.AddChild(poleArea);

        poleArea.InputEvent += (Node camera, InputEvent @event, Vector3 pos, Vector3 norm, long shapeIdx) =>
        {
            if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                SelectHandle(type, IKHandleType.PoleTarget);
                GetViewport()?.SetInputAsHandled();
            }
        };

        // 3. Instantiate SkeletonIK3D child on TargetSkeleton
        var skeletonIK = new SkeletonIK3D
        {
            Name = $"SkeletonIK_{type}",
            RootBone = rootName,
            TipBone = tipName,
            TargetNode = tipHandleNode.GetPath(),
            UseMagnet = true,
            Interpolation = _ikBlendWeight
        };

        TargetSkeleton.AddChild(skeletonIK);

        var chain = new IKChainInfo
        {
            Type = type,
            Name = displayName,
            RootBoneIndex = rootIdx,
            MidBoneIndex = midIdx,
            TipBoneIndex = tipIdx,
            RootBoneName = rootName,
            MidBoneName = midName,
            TipBoneName = tipName,
            SkeletonIK = skeletonIK,
            TargetHandle = tipHandleNode,
            HandleMesh = tipMesh,
            HandleArea = tipArea,
            TipMaterial = tipMat,
            PoleHandle = poleHandleNode,
            PoleMesh = poleMesh,
            PoleArea = poleArea,
            PoleMaterial = poleMat,
            IsActive = false
        };

        _chains[type] = chain;

        // Snap initial target and pole to FK pose
        SnapChainToFK(chain);
        tipHandleNode.Visible = false;
        poleHandleNode.Visible = false;
    }

    public void SetMasterIKEnabled(bool enabled)
    {
        _masterIKEnabled = enabled;
        if (_masterIKEnabled)
        {
            SnapAllToFK();
        }
        else
        {
            BakeIKPoseToFK();
            DeselectHandle();
        }

        UpdateActiveChains();
        OnIKModeChanged?.Invoke(_masterIKEnabled);
    }

    public void SetArmsIKEnabled(bool enabled)
    {
        _armsIKEnabled = enabled;
        if (_chains.TryGetValue(LimbChainType.LeftArm, out var la)) SetChainActive(la, _masterIKEnabled && _armsIKEnabled);
        if (_chains.TryGetValue(LimbChainType.RightArm, out var ra)) SetChainActive(ra, _masterIKEnabled && _armsIKEnabled);
        OnLimbIKChanged?.Invoke(LimbChainType.LeftArm, enabled);
    }

    public void SetLegsIKEnabled(bool enabled)
    {
        _legsIKEnabled = enabled;
        if (_chains.TryGetValue(LimbChainType.LeftLeg, out var ll)) SetChainActive(ll, _masterIKEnabled && _legsIKEnabled);
        if (_chains.TryGetValue(LimbChainType.RightLeg, out var rl)) SetChainActive(rl, _masterIKEnabled && _legsIKEnabled);
        OnLimbIKChanged?.Invoke(LimbChainType.LeftLeg, enabled);
    }

    public void SetIKBlendWeight(float weight)
    {
        _ikBlendWeight = Mathf.Clamp(weight, 0.0f, 1.0f);
        foreach (var chain in _chains.Values)
        {
            if (chain.SkeletonIK != null && GodotObject.IsInstanceValid(chain.SkeletonIK))
            {
                chain.SkeletonIK.Interpolation = _ikBlendWeight;
            }
        }
    }

    public void SetHandlesOpacity(float alpha)
    {
        _handlesOpacity = Mathf.Clamp(alpha, 0.0f, 1.0f);
        foreach (var chain in _chains.Values)
        {
            if (chain.TipMaterial != null)
            {
                Color c = chain.TipMaterial.AlbedoColor;
                chain.TipMaterial.AlbedoColor = new Color(c.R, c.G, c.B, _handlesOpacity);
            }
            if (chain.PoleMaterial != null)
            {
                Color c = chain.PoleMaterial.AlbedoColor;
                chain.PoleMaterial.AlbedoColor = new Color(c.R, c.G, c.B, _handlesOpacity);
            }

            bool visible = chain.IsActive && _handlesOpacity > 0.01f;
            if (chain.TargetHandle != null) chain.TargetHandle.Visible = visible;
            if (chain.PoleHandle != null) chain.PoleHandle.Visible = visible;
        }
    }

    private void UpdateActiveChains()
    {
        bool armsActive = _masterIKEnabled && _armsIKEnabled;
        bool legsActive = _masterIKEnabled && _legsIKEnabled;

        if (_chains.TryGetValue(LimbChainType.LeftArm, out var la)) SetChainActive(la, armsActive);
        if (_chains.TryGetValue(LimbChainType.RightArm, out var ra)) SetChainActive(ra, armsActive);
        if (_chains.TryGetValue(LimbChainType.LeftLeg, out var ll)) SetChainActive(ll, legsActive);
        if (_chains.TryGetValue(LimbChainType.RightLeg, out var rl)) SetChainActive(rl, legsActive);
    }

    private void SetChainActive(IKChainInfo chain, bool active)
    {
        if (chain == null || chain.SkeletonIK == null || !GodotObject.IsInstanceValid(chain.SkeletonIK)) return;

        chain.IsActive = active;
        if (active)
        {
            SnapChainToFK(chain);
            if (!chain.SkeletonIK.IsRunning())
            {
                chain.SkeletonIK.Start();
            }
            bool visible = _handlesOpacity > 0.01f;
            if (chain.TargetHandle != null) chain.TargetHandle.Visible = visible;
            if (chain.PoleHandle != null) chain.PoleHandle.Visible = visible;
        }
        else
        {
            if (chain.SkeletonIK.IsRunning())
            {
                chain.SkeletonIK.Stop();
            }
            if (chain.TargetHandle != null) chain.TargetHandle.Visible = false;
            if (chain.PoleHandle != null) chain.PoleHandle.Visible = false;

            if (_selectedHandle.HasValue && _selectedHandle.Value.Chain == chain.Type)
            {
                DeselectHandle();
            }
        }
    }

    public void SnapAllToFK()
    {
        foreach (var chain in _chains.Values)
        {
            SnapChainToFK(chain);
        }
    }

    public void SnapChainToFK(IKChainInfo chain)
    {
        if (chain == null || TargetSkeleton == null || chain.TipBoneIndex == -1 || chain.TargetHandle == null) return;

        Transform3D boneGlobal = TargetSkeleton.GlobalTransform * TargetSkeleton.GetBoneGlobalPose(chain.TipBoneIndex);
        chain.TargetHandle.GlobalTransform = boneGlobal;

        if (chain.SkeletonIK != null && GodotObject.IsInstanceValid(chain.SkeletonIK))
        {
            chain.SkeletonIK.Target = chain.TargetHandle.Transform;
        }

        // Compute natural pole placement for elbow / knee
        Vector3 rootGlobal = (TargetSkeleton.GlobalTransform * TargetSkeleton.GetBoneGlobalPose(chain.RootBoneIndex)).Origin;
        Vector3 tipGlobal = chain.TargetHandle.GlobalPosition;
        Vector3 midGlobal;

        if (chain.MidBoneIndex != -1)
        {
            midGlobal = (TargetSkeleton.GlobalTransform * TargetSkeleton.GetBoneGlobalPose(chain.MidBoneIndex)).Origin;
        }
        else
        {
            midGlobal = (rootGlobal + tipGlobal) * 0.5f;
        }

        Vector3 chordMid = (rootGlobal + tipGlobal) * 0.5f;
        Vector3 bendDir = (midGlobal - chordMid);

        if (bendDir.LengthSquared() < 0.002f)
        {
            bool isArm = (chain.Type == LimbChainType.LeftArm || chain.Type == LimbChainType.RightArm);
            bendDir = isArm ? -TargetSkeleton.GlobalTransform.Basis.Z : TargetSkeleton.GlobalTransform.Basis.Z;
        }

        Vector3 poleGlobal = midGlobal + bendDir.Normalized() * 0.35f;
        if (chain.PoleHandle != null)
        {
            chain.PoleHandle.GlobalPosition = poleGlobal;
        }

        if (chain.SkeletonIK != null && GodotObject.IsInstanceValid(chain.SkeletonIK))
        {
            chain.SkeletonIK.Magnet = TargetSkeleton.ToLocal(poleGlobal);
        }

        if (_selectedHandle.HasValue && _selectedHandle.Value.Chain == chain.Type && _dummyGizmoTarget != null)
        {
            Node3D activeNode = (_selectedHandle.Value.Handle == IKHandleType.TipTarget) ? chain.TargetHandle : chain.PoleHandle;
            if (activeNode != null) _dummyGizmoTarget.GlobalTransform = activeNode.GlobalTransform;
        }
    }

    public void BakeIKPoseToFK()
    {
        if (TargetSkeleton == null) return;

        foreach (var chain in _chains.Values)
        {
            if (chain.IsActive && chain.SkeletonIK != null && chain.SkeletonIK.IsRunning())
            {
                TargetSkeleton.ForceUpdateAllBoneTransforms();
            }
        }
    }

    public void SelectHandle(LimbChainType type, IKHandleType handleType)
    {
        if (!_chains.TryGetValue(type, out var chain) || !chain.IsActive)
        {
            DeselectHandle();
            return;
        }

        Node3D targetNode = (handleType == IKHandleType.TipTarget) ? chain.TargetHandle : chain.PoleHandle;
        if (targetNode == null)
        {
            DeselectHandle();
            return;
        }

        _selectedHandle = (type, handleType);
        if (_ikGizmo != null && _dummyGizmoTarget != null)
        {
            _dummyGizmoTarget.GlobalTransform = targetNode.GlobalTransform;
            _ikGizmo.ClearSelection();
            _ikGizmo.Select(_dummyGizmoTarget);
            _ikGizmo.Visible = true;
        }
    }

    public void DeselectHandle()
    {
        _selectedHandle = null;
        if (_ikGizmo != null)
        {
            _ikGizmo.ClearSelection();
            _ikGizmo.Visible = false;
        }
    }

    private void OnTargetHandleMoved(IKChainInfo chain)
    {
        if (chain == null || TargetSkeleton == null) return;

        if (chain.SkeletonIK != null && GodotObject.IsInstanceValid(chain.SkeletonIK))
        {
            chain.SkeletonIK.Target = chain.TargetHandle.Transform;
        }

        ProceduralClothSolver.Conform(TargetSkeleton);
    }

    private void OnPoleHandleMoved(IKChainInfo chain)
    {
        if (chain == null || TargetSkeleton == null || chain.PoleHandle == null) return;

        if (chain.SkeletonIK != null && GodotObject.IsInstanceValid(chain.SkeletonIK))
        {
            chain.SkeletonIK.Magnet = TargetSkeleton.ToLocal(chain.PoleHandle.GlobalPosition);
        }

        ProceduralClothSolver.Conform(TargetSkeleton);
    }

    private void ClearChains()
    {
        DeselectHandle();
        foreach (var chain in _chains.Values)
        {
            if (chain.SkeletonIK != null && GodotObject.IsInstanceValid(chain.SkeletonIK))
            {
                chain.SkeletonIK.Stop();
                chain.SkeletonIK.QueueFree();
            }
            if (chain.TargetHandle != null && GodotObject.IsInstanceValid(chain.TargetHandle))
            {
                chain.TargetHandle.QueueFree();
            }
            if (chain.PoleHandle != null && GodotObject.IsInstanceValid(chain.PoleHandle))
            {
                chain.PoleHandle.QueueFree();
            }
        }
        _chains.Clear();
    }

    private int FindFirstBone(Skeleton3D skeleton, params string[] names)
    {
        if (skeleton == null) return -1;
        foreach (var name in names)
        {
            int idx = skeleton.FindBone(name);
            if (idx != -1) return idx;
        }
        int total = skeleton.GetBoneCount();
        foreach (var name in names)
        {
            for (int i = 0; i < total; i++)
            {
                if (string.Equals(skeleton.GetBoneName(i), name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }
        return -1;
    }
}
