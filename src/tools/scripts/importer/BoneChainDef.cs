using System;
using System.Collections.Generic;

namespace DeadlockPlayground.Importer
{
    public enum ChainPolicy
    {
        PreservePhysicsChain = 0,
        CollapseToNearestCore = 1,
        Drop = 2
    }

    public enum SimulationKind
    {
        Jiggle = 0,
        Cloth = 1,
        ProxyCloth = 2
    }

    public enum AccessoryBoneMode
    {
        RigidToOneBone = 0,
        KeepOwnBones = 1,
        SkinToHero = 2
    }

    public enum FingerRigMode
    {
        RigidHand = 0,
        DeadlockFingerBones = 1,
        GenerateFingerChains = 2
    }

    public enum FingerPosePreset
    {
        Rest = 0,
        Fist = 1,
        Open = 2,
        Pinch = 3,
        Point = 4
    }

    /// <summary>
    /// Optional per-joint override for an individual bone (root or child) inside a <see cref="BoneChainDef"/>.
    /// Null properties inherit the parent chain's interpolated curve.
    /// </summary>
    public sealed class BoneJointOverride
    {
        public string BoneName { get; set; } = "";
        public bool? Simulate { get; set; }
        public float? GoalStrength { get; set; }
        public float? GoalDamping { get; set; }
        public float? CollisionRadiusInches { get; set; }
        /// <summary>Null = inherit chain curve, &lt; 0 = disabled/unlimited (-1), &gt;= 0 = explicit radius in inches.</summary>
        public float? StrayRadiusInches { get; set; }

        public bool HasAnyOverride =>
            Simulate.HasValue || GoalStrength.HasValue || GoalDamping.HasValue ||
            CollisionRadiusInches.HasValue || StrayRadiusInches.HasValue;

        public BoneJointOverride Clone() => new BoneJointOverride
        {
            BoneName = BoneName,
            Simulate = Simulate,
            GoalStrength = GoalStrength,
            GoalDamping = GoalDamping,
            CollisionRadiusInches = CollisionRadiusInches,
            StrayRadiusInches = StrayRadiusInches
        };
    }

    /// <summary>
    /// Unified representation of an extra secondary bone chain (clothing, cape, hair, tail, accessory)
    /// preserved from a source rig, kept on an accessory, or synthetically generated.
    /// Stores SimulationKind so full Cloth simulation can be added later without changing the importer.
    /// </summary>
    public sealed class BoneChainDef
    {
        public string ChainId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Category { get; set; } = "Clothing & Hair";
        public string RootSourceBone { get; set; } = "";
        public List<string> SourceBoneNames { get; set; } = new();
        public string AnchorHeroBone { get; set; } = "spine_3";
        public ChainPolicy Policy { get; set; } = ChainPolicy.PreservePhysicsChain;
        public SimulationKind Simulation { get; set; } = SimulationKind.Jiggle;
        public float StiffnessRoot { get; set; } = 0.80f;
        public float StiffnessTip { get; set; } = 0.15f;
        public float Damping { get; set; } = 0.35f;
        public float Gravity { get; set; } = 1.0f;
        public float CollisionRadiusTipInches { get; set; } = 2.4f;
        /// <summary>&lt; 0 (-1.0) disables AnimStrayRadius (matching Yamato/Wraith); &gt; 0 sets max stray leash in inches.</summary>
        public float StrayRadiusInches { get; set; } = -1.0f;
        public float TwistRelax { get; set; } = 1.0f;
        public float AngleLimitDeg { get; set; } = 35.0f;
        /// <summary>
        /// When true, emits a CSDK13 <c>ClothVertexMap</c> + planarized back-barrier capsule (<c>FeCollisionPlane</c>)
        /// on <c>spine_2</c>/<c>spine_3</c> (matching Valve's <c>unicorn.vmdl_c</c>) so long hair/capes cannot flip into the chest.
        /// </summary>
        public bool PlanarizeBackBarrier { get; set; } = false;
        /// <summary>When true, writes <c>cloth_anchor_free_rotate$0 = 1.0</c> so head/neck hair hinges freely at the nape.</summary>
        public bool AnchorFreeRotate { get; set; } = false;
        /// <summary>When true, multi-strand <c>ProxyCloth</c> sheets stay open (curtain/cape) instead of closing a 360° ring (skirt).</summary>
        public bool OpenSheet { get; set; } = true;
        /// <summary>Sliding friction (<c>cloth_friction$0</c>, 0.0..1.0; Valve uses 0.10..0.20).</summary>
        public float Friction { get; set; } = 0.15f;
        /// <summary>Source 2 <c>ClothChain</c> ribbon half-width in inches (<c>extrude_radius</c>, 0.2..6.0 in; default 1.0).</summary>
        public float ExtrudeRadiusInches { get; set; } = 1.0f;
        /// <summary>Source 2 <c>ClothChain</c> hinge/extrude forward axis (<c>extrude_forward_axis</c>): 0=+X (Default), 1=+Y, 2=+Z, 3=-X, 4=-Y, 5=-Z.</summary>
        public int ExtrudeForwardAxis { get; set; } = 0;
        /// <summary>Source 2 <c>ClothChain</c> hinge/extrude plane twist in degrees (<c>extrude_twist</c>, -180..+180; e.g. 90° like Blender).</summary>
        public float ExtrudeTwistDeg { get; set; } = 0.0f;
        public bool IsSynthetic { get; set; } = false;
        public string SlotId { get; set; } = "";
        /// <summary>Optional per-joint overrides keyed by source or appended bone name.</summary>
        public List<BoneJointOverride> JointOverrides { get; set; } = new();

        public BoneJointOverride FindJointOverride(string boneName, string fallbackSourceName = null)
        {
            if (JointOverrides == null || JointOverrides.Count == 0) return null;
            if (!string.IsNullOrEmpty(boneName))
            {
                for (int i = 0; i < JointOverrides.Count; i++)
                {
                    var ov = JointOverrides[i];
                    if (ov != null && string.Equals(ov.BoneName, boneName, StringComparison.OrdinalIgnoreCase))
                        return ov;
                }
            }
            if (!string.IsNullOrEmpty(fallbackSourceName))
            {
                for (int i = 0; i < JointOverrides.Count; i++)
                {
                    var ov = JointOverrides[i];
                    if (ov != null && string.Equals(ov.BoneName, fallbackSourceName, StringComparison.OrdinalIgnoreCase))
                        return ov;
                }
            }
            return null;
        }

        public BoneJointOverride GetOrCreateJointOverride(string boneName)
        {
            var existing = FindJointOverride(boneName);
            if (existing != null) return existing;
            JointOverrides ??= new List<BoneJointOverride>();
            var created = new BoneJointOverride { BoneName = boneName ?? "" };
            JointOverrides.Add(created);
            return created;
        }

        /// <summary>Synthetic chain authoring parameters (for Add-Clothing-Bones tool).</summary>
        public string TargetMeshName { get; set; } = "";
        public Godot.Vector3 SyntheticOriginHeroSpace { get; set; } = Godot.Vector3.Zero;
        public Godot.Vector3 SyntheticDirectionHeroSpace { get; set; } = new Godot.Vector3(0, -1, 0);
        public float SyntheticLengthMeters { get; set; } = 0.45f;
        public float SyntheticInfluenceRadius { get; set; } = 0.22f;
        public int SyntheticSegmentCount { get; set; } = 4;

        /// <summary>Resolved hero skeleton bone names after appending (parents always before children).</summary>
        public List<string> AppendedHeroBoneNames { get; set; } = new();

        public BoneChainDef Clone()
        {
            return new BoneChainDef
            {
                ChainId = ChainId,
                Name = Name,
                Category = Category,
                RootSourceBone = RootSourceBone,
                SourceBoneNames = new List<string>(SourceBoneNames),
                AnchorHeroBone = AnchorHeroBone,
                Policy = Policy,
                Simulation = Simulation,
                StiffnessRoot = StiffnessRoot,
                StiffnessTip = StiffnessTip,
                Damping = Damping,
                Gravity = Gravity,
                CollisionRadiusTipInches = CollisionRadiusTipInches,
                StrayRadiusInches = StrayRadiusInches,
                TwistRelax = TwistRelax,
                AngleLimitDeg = AngleLimitDeg,
                PlanarizeBackBarrier = PlanarizeBackBarrier,
                AnchorFreeRotate = AnchorFreeRotate,
                OpenSheet = OpenSheet,
                Friction = Friction,
                ExtrudeRadiusInches = ExtrudeRadiusInches,
                ExtrudeForwardAxis = ExtrudeForwardAxis,
                ExtrudeTwistDeg = ExtrudeTwistDeg,
                IsSynthetic = IsSynthetic,
                SlotId = SlotId,
                TargetMeshName = TargetMeshName,
                SyntheticOriginHeroSpace = SyntheticOriginHeroSpace,
                SyntheticDirectionHeroSpace = SyntheticDirectionHeroSpace,
                SyntheticLengthMeters = SyntheticLengthMeters,
                SyntheticInfluenceRadius = SyntheticInfluenceRadius,
                SyntheticSegmentCount = SyntheticSegmentCount,
                AppendedHeroBoneNames = new List<string>(AppendedHeroBoneNames),
                JointOverrides = JointOverrides != null ? JointOverrides.ConvertAll(j => j.Clone()) : new List<BoneJointOverride>()
            };
        }
    }

    /// <summary>
    /// Geometry primitive for a cloth body/prop collider (maps to Source 2 <c>ClothShapeCapsule</c> or <c>ClothShapeBox</c>).
    /// </summary>
    public enum ColliderShapeKind
    {
        Capsule = 0,
        Box = 1
    }

    /// <summary>
    /// Mesh-fitted body or user-added custom prop collision shape shared identically by the Godot preview
    /// cloth solver (<c>CustomJiggleBoneDriver</c>) and the Source 2 exporter (<c>CustomModelExporter</c>
    /// <c>ClothShapeCapsule</c> / <c>ClothShapeBox</c>). Stored in the owning bone's Godot rest-pose local space in meters.
    /// </summary>
    public sealed class BodyCollisionCapsuleDef
    {
        public string ColliderId { get; set; } = "";
        public ColliderShapeKind Shape { get; set; } = ColliderShapeKind.Capsule;
        public bool IsCustomCollider { get; set; } = false;
        public string BoneName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public Godot.Vector3 Point0Local { get; set; } = Godot.Vector3.Zero;
        public float Radius0Meters { get; set; } = 0.11f;
        public Godot.Vector3 Point1Local { get; set; } = new Godot.Vector3(0.10f, 0f, 0f);
        public float Radius1Meters { get; set; } = 0.11f;
        public float RadiusScale { get; set; } = 1.0f;
        public Godot.Vector3 LocalOffsetMeters { get; set; } = Godot.Vector3.Zero;
        /// <summary>Half-extents (X=Width/2, Y=Height/2, Z=Depth/2) in bone-local meters when <see cref="Shape"/> is <see cref="ColliderShapeKind.Box"/>.</summary>
        public Godot.Vector3 BoxHalfExtentsMeters { get; set; } = new Godot.Vector3(0.13f, 0.15f, 0.08f);
        /// <summary>Bone-local Euler rotation in degrees (Pitch X, Yaw Y, Roll Z) when <see cref="Shape"/> is <see cref="ColliderShapeKind.Box"/>.</summary>
        public Godot.Vector3 LocalRotationDeg { get; set; } = Godot.Vector3.Zero;
        /// <summary>When true, acts as a one-sided back barrier (<c>planarize = true</c> in Source 2) so capes slide over the back of a prop without snapping inside.</summary>
        public bool PlanarizeBackOnly { get; set; } = false;
        public bool Enabled { get; set; } = true;
        public bool IsUserEdited { get; set; } = false;

        public Godot.Vector3 EffectivePoint0 => Point0Local + LocalOffsetMeters;
        public Godot.Vector3 EffectivePoint1 => Point1Local + LocalOffsetMeters;
        public float EffectiveRadius0 => MathF.Max(0.02f, Radius0Meters * RadiusScale);
        public float EffectiveRadius1 => MathF.Max(0.02f, Radius1Meters * RadiusScale);
        public Godot.Vector3 EffectiveBoxHalfExtents => new Godot.Vector3(
            MathF.Max(0.015f, BoxHalfExtentsMeters.X * RadiusScale),
            MathF.Max(0.015f, BoxHalfExtentsMeters.Y * RadiusScale),
            MathF.Max(0.015f, BoxHalfExtentsMeters.Z * RadiusScale));

        public BodyCollisionCapsuleDef Clone()
        {
            return new BodyCollisionCapsuleDef
            {
                ColliderId = ColliderId,
                Shape = Shape,
                IsCustomCollider = IsCustomCollider,
                BoneName = BoneName,
                DisplayName = DisplayName,
                Point0Local = Point0Local,
                Radius0Meters = Radius0Meters,
                Point1Local = Point1Local,
                Radius1Meters = Radius1Meters,
                RadiusScale = RadiusScale,
                LocalOffsetMeters = LocalOffsetMeters,
                BoxHalfExtentsMeters = BoxHalfExtentsMeters,
                LocalRotationDeg = LocalRotationDeg,
                PlanarizeBackOnly = PlanarizeBackOnly,
                Enabled = Enabled,
                IsUserEdited = IsUserEdited
            };
        }
    }
}

