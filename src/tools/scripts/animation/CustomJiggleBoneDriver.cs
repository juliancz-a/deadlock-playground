using System;
using System.Collections.Generic;
using Godot;
using DeadlockPlayground.Importer;

namespace DeadlockPlayground.Animation
{
    /// <summary>
    /// Evaluates Source 2 <c>FeModel</c>-matched physics for custom clothing, hair, cape, and accessory
    /// bone chains appended to a hero <see cref="Skeleton3D"/>. Runs after <c>AnimationPlayer</c> and
    /// <c>Source2BoneConstraintDriver</c> (<c>ProcessPriority = 55</c>).
    ///
    /// Uses a 60 Hz fixed-substep Position-Based Verlet (PBD) solver for <c>Cloth</c> / <c>ProxyCloth</c>
    /// (consuming exact <see cref="PhysicsParamResolver"/> <c>GoalForce</c>, <c>PointDamping</c>,
    /// <c>BendStiffness</c>, <c>Friction</c>, <c>OpenSheet</c> horizontal+diagonal rods, and planarized
    /// torso/thigh barriers) and an unconditionally stable implicit spring solver for <c>Jiggle</c>.
    /// </summary>
    public partial class CustomJiggleBoneDriver : Node
    {
        private const float FixedDt = 1.0f / 60.0f;
        private const int MaxSubstepsPerFrame = 4;
        private const int PbdConstraintIterations = 4;
        private const float HairCapsuleMarginMeters = 0.015f;

        private sealed class JiggleBoneState
        {
            public int BoneIdx;
            public int ParentIdx;
            public int ChainParentLocalIdx = -1; // index into ActiveChain.Bones if parent is in same chain
            public Transform3D RestLocal;
            public Vector3 RestScale = Vector3.One;
            public Vector3 AimAxisLocal;
            public float BoneLength;

            // Source 2 resolved parameters (directly from PhysicsParamResolver)
            public float GoalForce;
            public float GoalDamping;
            public float Damping01 = 0.25f;
            public float PointDamping;
            public float BendStiffness = 0.35f;
            public float Friction = 0.15f;
            public float GravityMetersPerSecSq = PhysicsParamResolver.StandardGravityMetersPerSecSq;
            public float JiggleOmega = 14f;
            public float JiggleDampRatio = 0.55f;
            public float AngleLimitRad = Mathf.DegToRad(22f);
            public float ClothHingeLimitRad = Mathf.DegToRad(35f);
            public int ExtrudeForwardAxis;
            public float ExtrudeTwistDeg;
            public float ColRadiusMeters = 0.024f;
            public float StrayRadiusMeters = -1f; // < 0 = disabled / unlimited
            public bool IsBackSideOfTorso;
            public bool Simulate = true;
            public string BoneName = "";
            public string SourceBoneName = "";

            // Verlet / Jiggle simulation state (world space)
            public Vector3 TipWorldPos;
            public Vector3 PrevTipWorldPos;
            public Vector3 RenderPrevTipWorldPos; // for smooth sub-frame interpolation on >60Hz monitors
            public Vector3 Velocity; // used by JiggleBone semi-implicit spring

            // Pure un-simulated animated pose captured once per frame (zero compounding parent curl)
            public Transform3D AnimGlobalPose;
            public Transform3D CommittedGlobalPose;
            public Vector3 AnimHeadWorld;
            public Vector3 AnimTipWorld;
            public float CurWorldBoneLength;
            public bool Initialized;
            public float InitialAnchorDist;
        }

        private sealed class ActiveChain
        {
            public string SlotId = "";
            public string ChainId = "";
            public string AnchorBoneName = "";
            public int AnchorBoneIdx = -1;
            public SimulationKind Simulation = SimulationKind.Cloth;
            public BoneChainDef Definition;
            public readonly List<JiggleBoneState> Bones = new();
        }

        private sealed class ProxyNeighborLink
        {
            public JiggleBoneState A;
            public JiggleBoneState B;
            public float RestDistWorld;
            public float Stiffness;
        }

        private sealed class ColliderState
        {
            public int BoneIdx;
            public string BoneName = "";
            public ColliderShapeKind Shape = ColliderShapeKind.Capsule;
            public Vector3 Point0Local;
            public float Radius0;
            public Vector3 Point1Local;
            public float Radius1;
            public Vector3 BoxCenterLocal;
            public Vector3 BoxHalfExtents;
            public Vector3 LocalRotationDeg;
            public bool PlanarizeBackOnly;
        }

        private struct WorldCapsule
        {
            public int BoneIdx;
            public Vector3 A;
            public float Radius0;
            public Vector3 B;
            public float Radius1;
            public bool IsTorsoBarrier;
            public bool IsThighBarrier;
            public Vector3 BackWorld;
            public Vector3 OutwardWorld;
        }

        private struct WorldBox
        {
            public int BoneIdx;
            public Transform3D BoxWorld;
            public Transform3D BoxWorldInv;
            public Vector3 HalfExtents;
            public bool PlanarizeBackOnly;
            public Vector3 BackWorld;
        }

        [Export] public bool EnableSimulation { get; set; } = true;

        private Skeleton3D _skeleton;
        private readonly List<ActiveChain> _chains = new();
        private readonly List<ProxyNeighborLink> _proxyLinks = new();
        private readonly List<ColliderState> _colliders = new();
        private readonly List<WorldCapsule> _worldCapsulesCache = new();
        private readonly List<WorldBox> _worldBoxesCache = new();
        private float _simAccumulator;

        public int ActiveChainCount => _chains.Count;
        public int ActiveProxyLinkCount => _proxyLinks.Count;
        public int ActiveColliderCount => _colliders.Count;

        /// <summary>
        /// Pauses live simulation and resets all custom physics bones to their clean rest pose
        /// (used during Weight Paint mode so the mesh holds completely still under the brush).
        /// </summary>
        public void SetPausedForEditing(bool paused)
        {
            EnableSimulation = !paused;
            _simAccumulator = 0f;
            if (!paused || _skeleton == null || !GodotObject.IsInstanceValid(_skeleton))
                return;

            int boneCount = _skeleton.GetBoneCount();
            foreach (var ch in _chains)
            {
                foreach (var st in ch.Bones)
                {
                    st.Velocity = Vector3.Zero;
                    st.Initialized = false;
                    if (st.BoneIdx >= 0 && st.BoneIdx < boneCount)
                    {
                        _skeleton.SetBonePosePosition(st.BoneIdx, st.RestLocal.Origin);
                        _skeleton.SetBonePoseRotation(st.BoneIdx, st.RestLocal.Basis.Orthonormalized().GetRotationQuaternion());
                        _skeleton.SetBonePoseScale(st.BoneIdx, st.RestScale);
                    }
                }
            }
        }

        /// <summary>
        /// Populates <paramref name="dest"/> with every skeleton bone index currently owned by this driver
        /// so legacy <c>ProceduralClothSolver</c> never overwrites custom chains.
        /// </summary>
        public void CollectOwnedBoneIndices(HashSet<int> dest)
        {
            if (dest == null) return;
            foreach (var ch in _chains)
            {
                foreach (var st in ch.Bones)
                {
                    if (st.BoneIdx >= 0) dest.Add(st.BoneIdx);
                }
            }
        }

        public List<BoneChainDef> GetActiveChainDefinitions()
        {
            var list = new List<BoneChainDef>();
            if (!EnableSimulation) return list;
            foreach (var ch in _chains)
            {
                if (ch?.Definition != null && ch.Definition.Policy == ChainPolicy.PreservePhysicsChain)
                    list.Add(ch.Definition.Clone());
            }
            return list;
        }

        public override void _Ready()
        {
            ProcessPriority = 55;
        }

        public static CustomJiggleBoneDriver GetOrAttach(Node3D heroRoot, Skeleton3D skeleton)
        {
            if (heroRoot == null || !GodotObject.IsInstanceValid(heroRoot)) return null;
            var existing = heroRoot.FindChild("CustomJiggleBoneDriver", false, false) as CustomJiggleBoneDriver;
            if (existing != null && GodotObject.IsInstanceValid(existing))
            {
                existing._skeleton = skeleton;
                return existing;
            }
            var driver = new CustomJiggleBoneDriver { Name = "CustomJiggleBoneDriver", _skeleton = skeleton };
            heroRoot.AddChild(driver);
            return driver;
        }

        public void SetSkeleton(Skeleton3D skeleton)
        {
            _skeleton = skeleton;
        }

        public void SetSimulationEnabled(bool enabled)
        {
            EnableSimulation = enabled;
            _simAccumulator = 0f;
            if (!enabled)
            {
                ResetChainsToRestPose();
            }
        }

        public void ResetChainsToRestPose()
        {
            if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton)) return;
            int boneCount = _skeleton.GetBoneCount();
            foreach (var chain in _chains)
            {
                foreach (var st in chain.Bones)
                {
                    if (st.BoneIdx < 0 || st.BoneIdx >= boneCount) continue;
                    _skeleton.SetBonePosePosition(st.BoneIdx, st.RestLocal.Origin);
                    _skeleton.SetBonePoseRotation(st.BoneIdx, st.RestLocal.Basis.Orthonormalized().GetRotationQuaternion());
                    _skeleton.SetBonePoseScale(st.BoneIdx, st.RestScale);
                    st.Initialized = false;
                    st.Velocity = Vector3.Zero;
                }
            }
        }

        public void RegisterChains(string slotId, IEnumerable<BoneChainDef> defs)
        {
            string normSlot = slotId ?? "";
            RemoveSlot(normSlot);
            if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton) || defs == null) return;

            foreach (var def in defs)
            {
                if (def == null || def.Policy != ChainPolicy.PreservePhysicsChain) continue;
                var names = def.AppendedHeroBoneNames != null && def.AppendedHeroBoneNames.Count > 0
                    ? def.AppendedHeroBoneNames
                    : def.SourceBoneNames;
                if (names == null || names.Count == 0) continue;

                string anchorName = def.AnchorHeroBone ?? "";
                int anchorIdx = !string.IsNullOrEmpty(anchorName) ? _skeleton.FindBone(anchorName) : -1;
                if (anchorIdx < 0 && names.Count > 0)
                {
                    int b0 = _skeleton.FindBone(names[0]);
                    if (b0 >= 0) anchorIdx = _skeleton.GetBoneParent(b0);
                }

                var active = new ActiveChain
                {
                    SlotId = normSlot,
                    ChainId = def.ChainId ?? def.Name ?? "",
                    AnchorBoneName = anchorName,
                    AnchorBoneIdx = anchorIdx,
                    Simulation = def.Simulation,
                    Definition = def.Clone()
                };

                int count = names.Count;
                Vector3 prevAimLocal = new Vector3(0, 1, 0);
                float prevBoneLen = 0.08f;
                var boneIdxToLocal = new Dictionary<int, int>();

                for (int i = 0; i < count; i++)
                {
                    int bIdx = _skeleton.FindBone(names[i]);
                    if (bIdx < 0) continue;

                    float t = count > 1 ? i / (float)(count - 1) : 0f;
                    Transform3D restLocal = _skeleton.GetBoneRest(bIdx);
                    Vector3 restScale = restLocal.Basis.Scale;

                    Vector3 aimLocal;
                    float boneLen;
                    if (i + 1 < count)
                    {
                        int nextIdx = _skeleton.FindBone(names[i + 1]);
                        if (nextIdx >= 0 && _skeleton.GetBoneParent(nextIdx) == bIdx && _skeleton.GetBoneRest(nextIdx).Origin.Length() > 0.005f)
                        {
                            Vector3 childOff = _skeleton.GetBoneRest(nextIdx).Origin;
                            boneLen = MathF.Max(0.03f, childOff.Length());
                            aimLocal = childOff.Normalized();
                            prevAimLocal = aimLocal;
                            prevBoneLen = boneLen;
                        }
                        else
                        {
                            Vector3 inLocal = i > 0 ? (restLocal.Basis.Inverse() * prevAimLocal) : Vector3.Zero;
                            aimLocal = i > 0
                                ? (inLocal.LengthSquared() > 1e-6f ? inLocal.Normalized() : new Vector3(0, 1, 0))
                                : (restLocal.Origin.Length() > 0.005f ? restLocal.Origin.Normalized() : new Vector3(0, 1, 0));
                            boneLen = i > 0 ? MathF.Max(0.04f, prevBoneLen * 0.85f) : 0.08f;
                            prevAimLocal = aimLocal;
                            prevBoneLen = boneLen;
                        }
                    }
                    else
                    {
                        Vector3 inLocal = i > 0 ? (restLocal.Basis.Inverse() * prevAimLocal) : Vector3.Zero;
                        aimLocal = i > 0
                            ? (inLocal.LengthSquared() > 1e-6f ? inLocal.Normalized() : new Vector3(0, 1, 0))
                            : (restLocal.Origin.Length() > 0.005f ? restLocal.Origin.Normalized() : new Vector3(0, 1, 0));
                        boneLen = i > 0
                            ? MathF.Max(0.04f, prevBoneLen * 0.85f)
                            : Mathf.Clamp(restLocal.Origin.Length(), 0.05f, 0.14f);
                    }

                    string srcName = (def.SourceBoneNames != null && i < def.SourceBoneNames.Count) ? def.SourceBoneNames[i] : names[i];
                    var jov = def.FindJointOverride(names[i], srcName);
                    bool simJoint = jov?.Simulate ?? true;
                    var prevP = PhysicsParamResolver.ResolvePreviewBone(def, t, jov, count);

                    // 1:1 parity with Source 2 export: strictly follow def.PlanarizeBackBarrier (never silently force on).
                    bool isBackSide = def.PlanarizeBackBarrier;

                    int parentSkelIdx = _skeleton.GetBoneParent(bIdx);
                    int chainParentLocal = boneIdxToLocal.TryGetValue(parentSkelIdx, out int cpIdx) ? cpIdx : -1;
                    int myLocalIdx = active.Bones.Count;
                    boneIdxToLocal[bIdx] = myLocalIdx;

                    active.Bones.Add(new JiggleBoneState
                    {
                        BoneIdx = bIdx,
                        BoneName = names[i],
                        SourceBoneName = srcName,
                        ParentIdx = parentSkelIdx,
                        ChainParentLocalIdx = chainParentLocal,
                        RestLocal = restLocal,
                        RestScale = restScale,
                        AimAxisLocal = aimLocal,
                        BoneLength = boneLen,
                        Simulate = simJoint,
                        GoalForce = prevP.GoalForce,
                        GoalDamping = prevP.GoalDamping,
                        Damping01 = prevP.Damping01,
                        PointDamping = prevP.PointDamping,
                        BendStiffness = prevP.BendStiffness,
                        Friction = prevP.Friction,
                        GravityMetersPerSecSq = prevP.GravityMetersPerSecSq,
                        JiggleOmega = prevP.JiggleOmega,
                        JiggleDampRatio = prevP.JiggleDampRatio,
                        AngleLimitRad = def.Simulation == SimulationKind.Jiggle
                            ? Mathf.DegToRad(Mathf.Clamp(prevP.JiggleAngleLimitDeg, 6f, 85f))
                            : Mathf.DegToRad(140f),
                        ClothHingeLimitRad = Mathf.DegToRad(Mathf.Clamp(def.AngleLimitDeg > 1f ? def.AngleLimitDeg : 35f, 5f, 140f)),
                        ExtrudeForwardAxis = def.ExtrudeForwardAxis,
                        ExtrudeTwistDeg = def.ExtrudeTwistDeg,
                        ColRadiusMeters = prevP.CollisionRadiusMeters,
                        StrayRadiusMeters = prevP.StrayRadiusMeters,
                        IsBackSideOfTorso = isBackSide,
                        Initialized = false
                    });
                }

                if (active.Bones.Count > 0)
                {
                    _chains.Add(active);
                }
            }

            RebuildProxyNeighborLinks();
        }

        /// <summary>
        /// Hot-updates Source 2 resolved parameters on active chains without re-rigging meshes or resetting state.
        /// </summary>
        public void UpdateChainParametersInPlace(IEnumerable<BoneChainDef> defs)
        {
            if (defs == null || _skeleton == null || !GodotObject.IsInstanceValid(_skeleton)) return;
            var byRootOrId = new Dictionary<string, BoneChainDef>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in defs)
            {
                if (d == null) continue;
                if (!string.IsNullOrEmpty(d.ChainId)) byRootOrId[d.ChainId] = d;
                if (!string.IsNullOrEmpty(d.RootSourceBone)) byRootOrId[d.RootSourceBone] = d;
            }

            bool topologyChanged = false;
            foreach (var active in _chains)
            {
                BoneChainDef match = null;
                if (!string.IsNullOrEmpty(active.ChainId)) byRootOrId.TryGetValue(active.ChainId, out match);
                if (match == null && !string.IsNullOrEmpty(active.Definition?.RootSourceBone))
                    byRootOrId.TryGetValue(active.Definition.RootSourceBone, out match);
                if (match == null) continue;

                if (active.Simulation != match.Simulation ||
                    (active.Definition?.OpenSheet ?? false) != match.OpenSheet)
                {
                    topologyChanged = true;
                }
                active.Simulation = match.Simulation;
                active.Definition = match.Clone();

                int count = active.Bones.Count;
                for (int i = 0; i < count; i++)
                {
                    float t = count > 1 ? i / (float)(count - 1) : 0f;
                    var st = active.Bones[i];
                    var jov = match.FindJointOverride(st.BoneName, st.SourceBoneName);
                    st.Simulate = jov?.Simulate ?? true;
                    var prevP = PhysicsParamResolver.ResolvePreviewBone(match, t, jov, count);
                    st.GoalForce = prevP.GoalForce;
                    st.GoalDamping = prevP.GoalDamping;
                    st.Damping01 = prevP.Damping01;
                    st.PointDamping = prevP.PointDamping;
                    st.BendStiffness = prevP.BendStiffness;
                    st.Friction = prevP.Friction;
                    st.GravityMetersPerSecSq = prevP.GravityMetersPerSecSq;
                    st.JiggleOmega = prevP.JiggleOmega;
                    st.JiggleDampRatio = prevP.JiggleDampRatio;
                    st.ColRadiusMeters = prevP.CollisionRadiusMeters;
                    st.StrayRadiusMeters = prevP.StrayRadiusMeters;
                    // 1:1 parity: unchecking PlanarizeBackBarrier turns it off immediately!
                    st.IsBackSideOfTorso = match.PlanarizeBackBarrier;
                    st.ClothHingeLimitRad = Mathf.DegToRad(Mathf.Clamp(match.AngleLimitDeg > 1f ? match.AngleLimitDeg : 35f, 5f, 140f));
                    st.ExtrudeForwardAxis = match.ExtrudeForwardAxis;
                    st.ExtrudeTwistDeg = match.ExtrudeTwistDeg;
                    st.AngleLimitRad = match.Simulation == SimulationKind.Jiggle
                        ? Mathf.DegToRad(Mathf.Clamp(prevP.JiggleAngleLimitDeg, 6f, 85f))
                        : Mathf.DegToRad(140f);
                }
            }

            if (topologyChanged)
            {
                RebuildProxyNeighborLinks();
            }
        }

        private void RebuildProxyNeighborLinks()
        {
            _proxyLinks.Clear();
            if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton)) return;

            var byGroup = new Dictionary<string, List<ActiveChain>>(StringComparer.OrdinalIgnoreCase);
            foreach (var ch in _chains)
            {
                if (ch.Simulation != SimulationKind.ProxyCloth || ch.Bones.Count == 0) continue;
                string key = $"{ch.SlotId}::{ch.AnchorBoneName}";
                if (!byGroup.TryGetValue(key, out var list))
                {
                    list = new List<ActiveChain>();
                    byGroup[key] = list;
                }
                list.Add(ch);
            }

            foreach (var grp in byGroup.Values)
            {
                if (grp.Count < 2) continue;

                bool forceOpenSheet = false;
                Vector3 center = Vector3.Zero;
                int centerSamples = 0;
                foreach (var ch in grp)
                {
                    if (ch.Definition?.OpenSheet == true) forceOpenSheet = true;
                    if (ch.AnchorBoneIdx >= 0 && ch.AnchorBoneIdx < _skeleton.GetBoneCount())
                    {
                        center = _skeleton.GetBoneGlobalRest(ch.AnchorBoneIdx).Origin;
                        centerSamples = 1;
                    }
                    else if (centerSamples == 0 || centerSamples > 1)
                    {
                        center += _skeleton.GetBoneGlobalRest(ch.Bones[0].BoneIdx).Origin;
                        centerSamples++;
                    }
                }
                if (centerSamples > 1) center /= centerSamples;

                var ordered = new List<(ActiveChain Chain, float Angle)>(grp.Count);
                foreach (var ch in grp)
                {
                    Vector3 rp = _skeleton.GetBoneGlobalRest(ch.Bones[0].BoneIdx).Origin;
                    Vector3 d = rp - center;
                    ordered.Add((ch, MathF.Atan2(d.X, d.Z)));
                }
                ordered.Sort((a, b) => a.Angle.CompareTo(b.Angle));

                bool closeLoop = !forceOpenSheet && ordered.Count >= 4;
                if (closeLoop)
                {
                    float maxGap = 0f;
                    for (int i = 0; i < ordered.Count; i++)
                    {
                        float gap = ordered[(i + 1) % ordered.Count].Angle - ordered[i].Angle;
                        if (gap < 0f) gap += Mathf.Tau;
                        if (gap > maxGap) maxGap = gap;
                    }
                    if (maxGap > Mathf.DegToRad(135f)) closeLoop = false;
                }

                int pairCount = closeLoop ? ordered.Count : ordered.Count - 1;
                for (int i = 0; i < pairCount; i++)
                {
                    var chA = ordered[i].Chain;
                    var chB = ordered[(i + 1) % ordered.Count].Chain;
                    int segs = Math.Min(chA.Bones.Count, chB.Bones.Count);
                    for (int k = 0; k < segs; k++)
                    {
                        // Horizontal structural rod (matches FeModel StretchRods across adjacent strands)
                        AddProxyLinkIfValid(chA.Bones[k], chB.Bones[k], stiffness: 0.65f);

                        // Diagonal shear rods (matches FeModel quad shear stiffness so skirts/capes hold woven sheet shape)
                        if (k + 1 < segs)
                        {
                            AddProxyLinkIfValid(chA.Bones[k], chB.Bones[k + 1], stiffness: 0.32f);
                            AddProxyLinkIfValid(chA.Bones[k + 1], chB.Bones[k], stiffness: 0.32f);
                        }
                    }
                }
            }
        }

        private void AddProxyLinkIfValid(JiggleBoneState stA, JiggleBoneState stB, float stiffness)
        {
            Vector3 restTipA = _skeleton.GetBoneGlobalRest(stA.BoneIdx) * (stA.AimAxisLocal * stA.BoneLength);
            Vector3 restTipB = _skeleton.GetBoneGlobalRest(stB.BoneIdx) * (stB.AimAxisLocal * stB.BoneLength);
            float restDist = restTipA.DistanceTo(restTipB);
            if (restDist > 0.005f && restDist < 0.85f)
            {
                _proxyLinks.Add(new ProxyNeighborLink
                {
                    A = stA,
                    B = stB,
                    RestDistWorld = restDist,
                    Stiffness = stiffness
                });
            }
        }

        public void RemoveSlot(string slotId)
        {
            string normSlot = slotId ?? "";
            _chains.RemoveAll(c => string.Equals(c.SlotId, normSlot, StringComparison.OrdinalIgnoreCase));
            RebuildProxyNeighborLinks();
        }

        public void ClearAll()
        {
            _chains.Clear();
            _proxyLinks.Clear();
            _colliders.Clear();
            _simAccumulator = 0f;
        }

        public void RegisterCollisionCapsules(IEnumerable<BodyCollisionCapsuleDef> capsules)
        {
            _colliders.Clear();
            if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton) || capsules == null)
                return;

            foreach (var cap in capsules)
            {
                if (cap == null || !cap.Enabled || string.IsNullOrWhiteSpace(cap.BoneName))
                    continue;
                int bIdx = _skeleton.FindBone(cap.BoneName);
                if (bIdx < 0)
                {
                    int bCount = _skeleton.GetBoneCount();
                    for (int i = 0; i < bCount; i++)
                    {
                        if (_skeleton.GetBoneName(i).Equals(cap.BoneName, StringComparison.OrdinalIgnoreCase))
                        {
                            bIdx = i;
                            break;
                        }
                    }
                }
                if (bIdx < 0) continue;

                _colliders.Add(new ColliderState
                {
                    BoneIdx = bIdx,
                    BoneName = cap.BoneName,
                    Shape = cap.Shape,
                    Point0Local = cap.EffectivePoint0,
                    Radius0 = cap.EffectiveRadius0,
                    Point1Local = cap.EffectivePoint1,
                    Radius1 = cap.EffectiveRadius1,
                    BoxCenterLocal = cap.LocalOffsetMeters,
                    BoxHalfExtents = cap.EffectiveBoxHalfExtents,
                    LocalRotationDeg = cap.LocalRotationDeg,
                    PlanarizeBackOnly = cap.PlanarizeBackOnly
                });
            }
        }

        public override void _Process(double delta)
        {
            if (!EnableSimulation || _chains.Count == 0) return;
            if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton) || !_skeleton.IsInsideTree()) return;
            float dt = Math.Clamp((float)delta, 0.001f, 0.05f);
            StepSimulation(dt);
        }

        public void StepSimulation(float dt)
        {
            if (!EnableSimulation || _skeleton == null || !GodotObject.IsInstanceValid(_skeleton) || _chains.Count == 0)
                return;

            Transform3D skelGlobal = _skeleton.IsInsideTree() ? _skeleton.GlobalTransform : Transform3D.Identity;
            int boneCount = _skeleton.GetBoneCount();

            BuildWorldCapsules(skelGlobal, boneCount);
            CaptureAnimatedTargetsAndInitialize(skelGlobal, boneCount);

            // 60 Hz fixed-substep accumulator with sub-frame render interpolation (zero stutter on >60Hz monitors)
            _simAccumulator = MathF.Min(_simAccumulator + Math.Clamp(dt, 0f, 0.066f), FixedDt * MaxSubstepsPerFrame);
            int substeps = 0;
            while (_simAccumulator >= FixedDt - 1e-5f && substeps < MaxSubstepsPerFrame)
            {
                foreach (var ch in _chains)
                    foreach (var st in ch.Bones)
                        st.RenderPrevTipWorldPos = st.TipWorldPos;

                _simAccumulator = MathF.Max(0f, _simAccumulator - FixedDt);
                StepFixedSubstep();
                substeps++;
            }
            float renderAlpha = _simAccumulator <= 1e-4f ? 1.0f : Mathf.Clamp(_simAccumulator / FixedDt, 0f, 1f);
            CommitFinalBonePoses(skelGlobal, boneCount, renderAlpha);
        }

        private void BuildWorldCapsules(Transform3D skelGlobal, int boneCount)
        {
            _worldCapsulesCache.Clear();
            _worldBoxesCache.Clear();
            Vector3 heroCenterWorld = skelGlobal.Origin;
            int pelvisIdx = _skeleton.FindBone("pelvis");
            if (pelvisIdx >= 0 && pelvisIdx < boneCount)
            {
                heroCenterWorld = skelGlobal * _skeleton.GetBoneGlobalPose(pelvisIdx).Origin;
            }

            foreach (var c in _colliders)
            {
                if (c.BoneIdx < 0 || c.BoneIdx >= boneCount) continue;
                Transform3D poseSkel = _skeleton.GetBoneGlobalPose(c.BoneIdx);
                Transform3D boneWorld = skelGlobal * poseSkel;
                float worldScale = (boneWorld.Basis.X.Length() + boneWorld.Basis.Y.Length() + boneWorld.Basis.Z.Length()) / 3f;
                if (worldScale < 0.01f) worldScale = 1.0f;

                bool isTorsoBarrier = c.BoneName.Equals("spine_1", StringComparison.OrdinalIgnoreCase) ||
                                      c.BoneName.Equals("spine_2", StringComparison.OrdinalIgnoreCase) ||
                                      c.BoneName.Equals("spine_3", StringComparison.OrdinalIgnoreCase) ||
                                      c.BoneName.Equals("neck_0", StringComparison.OrdinalIgnoreCase);

                bool isThighBarrier = c.BoneName.Equals("leg_upper_L", StringComparison.OrdinalIgnoreCase) ||
                                      c.BoneName.Equals("leg_upper_R", StringComparison.OrdinalIgnoreCase);

                Vector3 backWorld = (skelGlobal.Basis * new Vector3(0f, 0f, -1f)).Normalized();
                if (isTorsoBarrier || c.PlanarizeBackOnly)
                {
                    Basis restBasis = _skeleton.GetBoneGlobalRest(c.BoneIdx).Basis.Orthonormalized();
                    Basis poseBasis = poseSkel.Basis.Orthonormalized();
                    Vector3 backSkel = poseBasis * (restBasis.Inverse() * new Vector3(0f, 0f, -1f));
                    Vector3 bw = skelGlobal.Basis * backSkel;
                    if (bw.LengthSquared() > 1e-6f) backWorld = bw.Normalized();
                }

                if (c.Shape == ColliderShapeKind.Box)
                {
                    Basis rotLocal = Basis.FromEuler(new Vector3(
                        Mathf.DegToRad(c.LocalRotationDeg.X),
                        Mathf.DegToRad(c.LocalRotationDeg.Y),
                        Mathf.DegToRad(c.LocalRotationDeg.Z)));
                    Basis boxBasis = (boneWorld.Basis.Orthonormalized() * rotLocal).Orthonormalized();
                    Vector3 boxOrigin = boneWorld * c.BoxCenterLocal;
                    Transform3D boxWorld = new Transform3D(boxBasis, boxOrigin);
                    _worldBoxesCache.Add(new WorldBox
                    {
                        BoneIdx = c.BoneIdx,
                        BoxWorld = boxWorld,
                        BoxWorldInv = boxWorld.AffineInverse(),
                        HalfExtents = c.BoxHalfExtents * worldScale,
                        PlanarizeBackOnly = c.PlanarizeBackOnly,
                        BackWorld = backWorld
                    });
                    continue;
                }

                Vector3 capA = boneWorld * c.Point0Local;
                Vector3 capB = boneWorld * c.Point1Local;
                Vector3 capMid = (capA + capB) * 0.5f;
                Vector3 outDir = new Vector3(capMid.X - heroCenterWorld.X, 0f, capMid.Z - heroCenterWorld.Z);
                outDir = outDir.LengthSquared() > 1e-5f ? outDir.Normalized() : Vector3.Right;

                _worldCapsulesCache.Add(new WorldCapsule
                {
                    BoneIdx = c.BoneIdx,
                    A = capA,
                    Radius0 = c.Radius0 * worldScale,
                    B = capB,
                    Radius1 = c.Radius1 * worldScale,
                    IsTorsoBarrier = isTorsoBarrier,
                    IsThighBarrier = isThighBarrier,
                    BackWorld = backWorld,
                    OutwardWorld = outDir
                });
            }
        }

        private void CaptureAnimatedTargetsAndInitialize(Transform3D skelGlobal, int boneCount)
        {
            foreach (var chain in _chains)
            {
                for (int i = 0; i < chain.Bones.Count; i++)
                {
                    var st = chain.Bones[i];
                    if (st.BoneIdx < 0 || st.BoneIdx >= boneCount) continue;

                    // Pure animated parent pose: never inherit simulated rotation from an upstream physics bone,
                    // eliminating compounding curl on multi-bone capes, skirts, and hair strands.
                    Transform3D animParentPose;
                    if (st.ChainParentLocalIdx >= 0 && st.ChainParentLocalIdx < i)
                    {
                        animParentPose = chain.Bones[st.ChainParentLocalIdx].AnimGlobalPose;
                    }
                    else
                    {
                        animParentPose = (st.ParentIdx >= 0 && st.ParentIdx < boneCount)
                            ? _skeleton.GetBoneGlobalPose(st.ParentIdx)
                            : Transform3D.Identity;
                    }

                    st.AnimGlobalPose = animParentPose * st.RestLocal;
                    st.AnimHeadWorld = skelGlobal * st.AnimGlobalPose.Origin;
                    st.AnimTipWorld = skelGlobal * (st.AnimGlobalPose * (st.AimAxisLocal * st.BoneLength));
                    st.CurWorldBoneLength = MathF.Max(0.02f, st.AnimTipWorld.DistanceTo(st.AnimHeadWorld));

                    if (!st.Initialized || (st.AnimTipWorld - st.TipWorldPos).LengthSquared() > 2.25f)
                    {
                        st.TipWorldPos = st.AnimTipWorld;
                        st.PrevTipWorldPos = st.AnimTipWorld;
                        st.RenderPrevTipWorldPos = st.AnimTipWorld;
                        st.Velocity = Vector3.Zero;
                        st.Initialized = true;
                        st.InitialAnchorDist = float.MaxValue;
                        if (chain.AnchorBoneIdx >= 0)
                        {
                            foreach (var wc in _worldCapsulesCache)
                            {
                                if (wc.BoneIdx != chain.AnchorBoneIdx) continue;
                                ComputeClosestTaperedCapsule(st.AnimTipWorld, wc.A, wc.B, wc.Radius0, wc.Radius1, out float dist, out _, out _);
                                if (dist < st.InitialAnchorDist) st.InitialAnchorDist = dist;
                            }
                        }
                    }
                }
            }
        }

        private void StepFixedSubstep()
        {
            // 1. Integrate all particles (Verlet PBD for Cloth/ProxyCloth; Implicit Spring for Jiggle)
            foreach (var chain in _chains)
            {
                bool isJiggle = chain.Simulation == SimulationKind.Jiggle;
                for (int i = 0; i < chain.Bones.Count; i++)
                {
                    var st = chain.Bones[i];
                    if (!st.Simulate)
                    {
                        st.TipWorldPos = st.AnimTipWorld;
                        st.PrevTipWorldPos = st.AnimTipWorld;
                        st.Velocity = Vector3.Zero;
                        continue;
                    }

                    if (isJiggle)
                    {
                        StepJiggleSubstep(st);
                    }
                    else
                    {
                        StepClothVerletSubstep(st);
                    }
                }
            }

            // 2. PBD Constraint Projection Iterations (Rod + Bend + ProxyCloth Sheet + Stray + Collisions)
            for (int iter = 0; iter < PbdConstraintIterations; iter++)
            {
                // 2a. Chain structural rods + 2-hop bend rods
                foreach (var chain in _chains)
                {
                    if (chain.Simulation == SimulationKind.Jiggle) continue;
                    int n = chain.Bones.Count;

                    for (int i = 0; i < n; i++)
                    {
                        var st = chain.Bones[i];
                        if (!st.Simulate)
                        {
                            st.TipWorldPos = st.AnimTipWorld;
                            continue;
                        }

                        if (st.ChainParentLocalIdx >= 0 && st.ChainParentLocalIdx < i)
                        {
                            var parent = chain.Bones[st.ChainParentLocalIdx];
                            float wA = parent.Simulate ? 0.25f : 0.0f;
                            float wB = 1.0f;
                            SolveDistanceConstraint(ref parent.TipWorldPos, ref st.TipWorldPos, st.CurWorldBoneLength, wA, wB, stiffness: 0.95f);
                        }
                        else
                        {
                            Vector3 d = st.TipWorldPos - st.AnimHeadWorld;
                            float lenSq = d.LengthSquared();
                            if (lenSq > 1e-8f)
                                st.TipWorldPos = st.AnimHeadWorld + (d / MathF.Sqrt(lenSq)) * st.CurWorldBoneLength;
                        }
                    }

                    // 2-hop bend constraint (matches FeModel BendRods: prevents accordion kinks)
                    for (int i = 0; i + 2 < n; i++)
                    {
                        var a = chain.Bones[i];
                        var c = chain.Bones[i + 2];
                        if (!a.Simulate && !c.Simulate) continue;
                        float restBendDist = a.AnimTipWorld.DistanceTo(c.AnimTipWorld);
                        if (restBendDist < 0.005f) continue;
                        float bendK = 0.06f + c.BendStiffness * 0.34f;
                        SolveDistanceConstraint(
                            ref a.TipWorldPos,
                            ref c.TipWorldPos,
                            restBendDist,
                            a.Simulate ? 0.35f : 0.0f,
                            c.Simulate ? 0.65f : 0.0f,
                            bendK);
                    }
                }

                // 2b. ProxyCloth woven sheet constraints (horizontal + diagonal shear rods)
                for (int li = 0; li < _proxyLinks.Count; li++)
                {
                    var link = _proxyLinks[li];
                    if (!link.A.Simulate && !link.B.Simulate) continue;
                    SolveDistanceConstraint(
                        ref link.A.TipWorldPos,
                        ref link.B.TipWorldPos,
                        link.RestDistWorld,
                        link.A.Simulate ? 0.5f : 0.0f,
                        link.B.Simulate ? 0.5f : 0.0f,
                        link.Stiffness);
                }

                // 2c. Stray radius leash + Body capsule & planarized barrier collisions
                foreach (var chain in _chains)
                {
                    if (chain.Simulation == SimulationKind.Jiggle) continue;
                    bool isSkirtChain = chain.AnchorBoneName.Equals("pelvis", StringComparison.OrdinalIgnoreCase) &&
                                        chain.Definition?.OpenSheet != true;
                    for (int i = 0; i < chain.Bones.Count; i++)
                    {
                        var st = chain.Bones[i];
                        if (!st.Simulate) continue;

                        if (st.StrayRadiusMeters > 0f)
                        {
                            Vector3 stray = st.TipWorldPos - st.AnimTipWorld;
                            if (stray.LengthSquared() > st.StrayRadiusMeters * st.StrayRadiusMeters)
                            {
                                st.TipWorldPos = st.AnimTipWorld + stray.Normalized() * st.StrayRadiusMeters;
                            }
                        }

                        Vector3 headWorld = (st.ChainParentLocalIdx >= 0 && st.ChainParentLocalIdx < i)
                            ? chain.Bones[st.ChainParentLocalIdx].TipWorldPos
                            : st.AnimHeadWorld;

                        if (_worldCapsulesCache.Count > 0 || _worldBoxesCache.Count > 0)
                        {
                            ResolveBodyCapsuleCollisions(headWorld, st, st.CurWorldBoneLength, chain.AnchorBoneIdx, isSkirtChain);
                        }

                        // Re-enforce exact bone length from current head
                        Vector3 d = st.TipWorldPos - headWorld;
                        float lenSq = d.LengthSquared();
                        if (lenSq > 1e-8f)
                            st.TipWorldPos = headWorld + (d / MathF.Sqrt(lenSq)) * st.CurWorldBoneLength;
                    }
                }
            }
        }

        private void StepClothVerletSubstep(JiggleBoneState st)
        {
            // Responsive, perceptible damping in preview honoring both UI Damping (0..1) and per-bone GoalDamping:
            // Damping=0 -> 2.2/s (lively drape), Damping=0.35 -> 5.5/s (calm settle ~1s), Damping=1.0 -> 11.7/s.
            float totalDamp = 2.2f + st.Damping01 * 9.5f + st.GoalDamping * 6.0f;
            float dampFactor = MathF.Exp(-totalDamp * FixedDt);
            Vector3 vel = (st.TipWorldPos - st.PrevTipWorldPos) * dampFactor;

            // Cap extreme velocities (> 12 m/s) to prevent teleport bursts; sleep micro-jitter cleanly
            float maxStep = 12.0f * FixedDt;
            float velSq = vel.LengthSquared();
            if (velSq > maxStep * maxStep)
                vel *= maxStep / MathF.Sqrt(velSq);
            else if (velSq < 2e-7f)
                vel = Vector3.Zero;

            st.PrevTipWorldPos = st.TipWorldPos;
            st.TipWorldPos += vel + new Vector3(0f, -st.GravityMetersPerSecSq, 0f) * (FixedDt * FixedDt);

            // Source 2 AnimationForceAttraction (GoalForce): damped position pull toward pure AnimTipWorld.
            // Shift PrevTipWorldPos by 78% of the pull so pose-return glides calmly into rest instead of overshooting!
            float effGoal = st.GoalForce;
            if (effGoal > 1e-5f)
            {
                if (st.IsBackSideOfTorso && IsPointInsideTorsoBarrier(st.AnimTipWorld, st.ColRadiusMeters))
                {
                    effGoal *= 0.12f;
                }
                float pullAlpha = Mathf.Clamp(effGoal * 0.35f, 0f, 0.85f);
                Vector3 nextPos = st.TipWorldPos.Lerp(st.AnimTipWorld, pullAlpha);
                Vector3 pullDelta = nextPos - st.TipWorldPos;
                st.TipWorldPos = nextPos;
                float dampCarry = Mathf.Clamp(0.78f + st.GoalDamping * 0.40f, 0.78f, 0.98f);
                st.PrevTipWorldPos += pullDelta * dampCarry;
            }
        }

        private void StepJiggleSubstep(JiggleBoneState st)
        {
            // Unconditionally stable implicit damped harmonic oscillator (ZERO gravity — exact FeJiggleBone)
            float w = st.JiggleOmega;
            float w2 = w * w;
            float denom = 1.0f + 2.0f * st.JiggleDampRatio * w * FixedDt + w2 * FixedDt * FixedDt;
            Vector3 disp = st.AnimTipWorld - st.TipWorldPos;
            st.Velocity = (st.Velocity + disp * (w2 * FixedDt)) / denom;
            if (st.Velocity.LengthSquared() < 1e-6f && disp.LengthSquared() < 1e-6f)
                st.Velocity = Vector3.Zero;

            st.TipWorldPos += st.Velocity * FixedDt;

            // Re-project onto bone length sphere around AnimHeadWorld
            Vector3 curVec = st.TipWorldPos - st.AnimHeadWorld;
            if (curVec.LengthSquared() < 1e-8f)
            {
                st.TipWorldPos = st.AnimTipWorld;
                return;
            }
            Vector3 curDir = curVec.Normalized();
            Vector3 restDir = (st.AnimTipWorld - st.AnimHeadWorld).Normalized();

            // Hard cone angle clamp matching Source 2 FeJiggleBone.AngleLimitDeg
            float dot = Mathf.Clamp(restDir.Dot(curDir), -1f, 1f);
            float angle = MathF.Acos(dot);
            if (angle > st.AngleLimitRad)
            {
                Vector3 axis = restDir.Cross(curDir);
                if (axis.LengthSquared() > 1e-8f)
                {
                    Quaternion clampedRot = new Quaternion(axis.Normalized(), st.AngleLimitRad);
                    curDir = (clampedRot * restDir).Normalized();
                    // Damp outward radial velocity at the cone boundary
                    Vector3 boundaryNormal = (curDir - restDir * MathF.Cos(st.AngleLimitRad)).Normalized();
                    float vn = st.Velocity.Dot(boundaryNormal);
                    if (vn > 0f) st.Velocity -= boundaryNormal * vn;
                }
            }

            st.TipWorldPos = st.AnimHeadWorld + curDir * st.CurWorldBoneLength;
            st.PrevTipWorldPos = st.TipWorldPos;
        }

        private static void SolveDistanceConstraint(
            ref Vector3 pA,
            ref Vector3 pB,
            float restDist,
            float invMassA,
            float invMassB,
            float stiffness)
        {
            float wSum = invMassA + invMassB;
            if (wSum <= 1e-6f) return;
            Vector3 diff = pB - pA;
            float dist = diff.Length();
            if (dist < 1e-5f) return;
            float err = (dist - restDist) * stiffness / wSum;
            Vector3 n = diff / dist;
            if (invMassA > 0f) pA += n * (err * invMassA);
            if (invMassB > 0f) pB -= n * (err * invMassB);
        }

        private bool IsPointInsideTorsoBarrier(Vector3 worldPos, float margin)
        {
            for (int c = 0; c < _worldCapsulesCache.Count; c++)
            {
                var wc = _worldCapsulesCache[c];
                if (!wc.IsTorsoBarrier) continue;
                ComputeClosestTaperedCapsule(worldPos, wc.A, wc.B, wc.Radius0, wc.Radius1, out float rDist, out float rCap, out _);
                if (rDist < rCap + margin) return true;
            }
            return false;
        }

        private void CommitFinalBonePoses(Transform3D skelGlobal, int boneCount, float renderAlpha)
        {
            foreach (var chain in _chains)
            {
                bool isJiggle = chain.Simulation == SimulationKind.Jiggle;
                bool isClothHinge = chain.Simulation == SimulationKind.Cloth;
                for (int i = 0; i < chain.Bones.Count; i++)
                {
                    var st = chain.Bones[i];
                    if (st.BoneIdx < 0 || st.BoneIdx >= boneCount) continue;

                    // Track parent's committed global pose in C# top-down so child bones never read
                    // a stale Godot Skeleton3D global-pose cache from before bone i-1 was rotated this frame.
                    Transform3D parentGlobalPose;
                    if (st.ChainParentLocalIdx >= 0 && st.ChainParentLocalIdx < i)
                    {
                        parentGlobalPose = chain.Bones[st.ChainParentLocalIdx].CommittedGlobalPose;
                    }
                    else
                    {
                        parentGlobalPose = (st.ParentIdx >= 0 && st.ParentIdx < boneCount)
                            ? _skeleton.GetBoneGlobalPose(st.ParentIdx)
                            : Transform3D.Identity;
                    }

                    if (!st.Simulate)
                    {
                        Quaternion restQ = st.RestLocal.Basis.Orthonormalized().GetRotationQuaternion();
                        _skeleton.SetBonePosePosition(st.BoneIdx, st.RestLocal.Origin);
                        _skeleton.SetBonePoseRotation(st.BoneIdx, restQ);
                        _skeleton.SetBonePoseScale(st.BoneIdx, st.RestScale);
                        st.CommittedGlobalPose = parentGlobalPose * st.RestLocal;
                        continue;
                    }

                    Transform3D targetSkelPose = parentGlobalPose * st.RestLocal;
                    Vector3 curHeadWorld = skelGlobal * targetSkelPose.Origin;
                    Vector3 curRestTipWorld = skelGlobal * (targetSkelPose * (st.AimAxisLocal * st.BoneLength));
                    Vector3 interpTipWorld = renderAlpha >= 0.999f
                        ? st.TipWorldPos
                        : st.RenderPrevTipWorldPos.Lerp(st.TipWorldPos, renderAlpha);

                    Quaternion finalRot = ApplyBonePoseFromTip(skelGlobal, parentGlobalPose, curHeadWorld, curRestTipWorld, interpTipWorld, st, isJiggle, isClothHinge);
                    Transform3D localCommitted = new Transform3D(new Basis(finalRot).Scaled(st.RestScale), st.RestLocal.Origin);
                    st.CommittedGlobalPose = parentGlobalPose * localCommitted;
                }
            }
        }

        private static Vector3 GetExtrudeForwardAxisLocal(int axisIndex) => axisIndex switch
        {
            1 => Vector3.Up,
            2 => Vector3.Back,
            3 => Vector3.Left,
            4 => Vector3.Down,
            5 => Vector3.Forward,
            _ => Vector3.Right
        };

        private Quaternion ApplyBonePoseFromTip(
            Transform3D skelGlobal,
            Transform3D parentGlobalPose,
            Vector3 curHeadWorld,
            Vector3 curRestTipWorld,
            Vector3 targetTipWorld,
            JiggleBoneState st,
            bool enforceTightAngleLimit,
            bool enforceClothHinge)
        {
            Quaternion restRot = st.RestLocal.Basis.Orthonormalized().GetRotationQuaternion();
            Vector3 restDirWorld = (curRestTipWorld - curHeadWorld).Normalized();
            Vector3 curDirWorld = (targetTipWorld - curHeadWorld).Normalized();
            if (restDirWorld.LengthSquared() <= 0.5f || curDirWorld.LengthSquared() <= 0.5f) return restRot;

            _skeleton.SetBonePosePosition(st.BoneIdx, st.RestLocal.Origin);
            _skeleton.SetBonePoseScale(st.BoneIdx, st.RestScale);

            float dot = Mathf.Clamp(restDirWorld.Dot(curDirWorld), -1f, 1f);
            if (dot < 0.99995f)
            {
                Basis skelToParent = (skelGlobal.Basis * parentGlobalPose.Basis).Orthonormalized().Inverse();
                Vector3 restDirLocal = (skelToParent * restDirWorld).Normalized();
                Vector3 curDirLocal = (skelToParent * curDirWorld).Normalized();

                // ClothChain hinge constraint: when Simulation == Cloth and ClothHingeLimitRad < 135°,
                // limit out-of-plane swing perpendicular to the extruded ribbon plane (ExtrudeForwardAxis + ExtrudeTwistDeg).
                if (enforceClothHinge && st.ClothHingeLimitRad < Mathf.DegToRad(135f))
                {
                    Vector3 fwdLocal = (st.RestLocal.Basis.Orthonormalized() * GetExtrudeForwardAxisLocal(st.ExtrudeForwardAxis)).Normalized();
                    if (MathF.Abs(fwdLocal.Dot(restDirLocal)) > 0.95f)
                    {
                        fwdLocal = MathF.Abs(restDirLocal.Y) < 0.9f ? Vector3.Up : Vector3.Right;
                    }
                    // Ribbon plane normal = cross(restDir, forwardAxis), rotated around restDir by ExtrudeTwistDeg
                    Vector3 planeNormal = restDirLocal.Cross(fwdLocal).Normalized();
                    if (MathF.Abs(st.ExtrudeTwistDeg) > 0.01f)
                    {
                        planeNormal = (new Quaternion(restDirLocal, Mathf.DegToRad(st.ExtrudeTwistDeg)) * planeNormal).Normalized();
                    }
                    // Decompose curDirLocal into in-plane component and out-of-plane elevation angle
                    float outComp = curDirLocal.Dot(planeNormal);
                    Vector3 inPlane = curDirLocal - planeNormal * outComp;
                    float inPlaneLen = inPlane.Length();
                    float outAngle = MathF.Asin(Mathf.Clamp(MathF.Abs(outComp), 0f, 1f));
                    if (outAngle > st.ClothHingeLimitRad && inPlaneLen > 1e-5f)
                    {
                        float sinMax = MathF.Sin(st.ClothHingeLimitRad);
                        float cosMax = MathF.Cos(st.ClothHingeLimitRad);
                        curDirLocal = ((inPlane / inPlaneLen) * cosMax + planeNormal * (MathF.Sign(outComp) * sinMax)).Normalized();
                    }
                }

                float localDot = Mathf.Clamp(restDirLocal.Dot(curDirLocal), -1f, 1f);
                float angle = MathF.Acos(localDot);
                float maxAllowedRad = enforceTightAngleLimit ? st.AngleLimitRad : Mathf.DegToRad(140f);
                float clampedAngle = MathF.Min(angle, maxAllowedRad);

                Quaternion swingLocal = Quaternion.Identity;
                if (clampedAngle > 1e-4f)
                {
                    Vector3 axis = restDirLocal.Cross(curDirLocal);
                    float axisLenSq = axis.LengthSquared();
                    if (axisLenSq > 1e-8f)
                    {
                        axis /= MathF.Sqrt(axisLenSq);
                        swingLocal = new Quaternion(axis, clampedAngle);
                    }
                    else if (localDot < 0f)
                    {
                        Vector3 ortho = MathF.Abs(restDirLocal.Y) < 0.9f ? Vector3.Up : Vector3.Right;
                        axis = restDirLocal.Cross(ortho).Normalized();
                        if (axis.IsNormalized())
                            swingLocal = new Quaternion(axis, clampedAngle);
                    }
                }
                Quaternion finalRot = (swingLocal * restRot).Normalized();
                _skeleton.SetBonePoseRotation(st.BoneIdx, finalRot);
                return finalRot;
            }
            else
            {
                _skeleton.SetBonePoseRotation(st.BoneIdx, restRot);
                return restRot;
            }
        }

        /// <summary>
        /// Pushes <paramref name="st"/> out of a collider WITHOUT injecting outward bounce velocity:
        /// shifts both <c>TipWorldPos</c> and <c>PrevTipWorldPos</c> along the outward normal, zeroes
        /// inward normal velocity, and applies tangential Coulomb friction.
        /// </summary>
        private static void PushTipOutOfCollider(JiggleBoneState st, Vector3 normal, float pushDistance)
        {
            Vector3 corr = normal * pushDistance;
            st.TipWorldPos += corr;
            // Shift PrevTipWorldPos by the same normal push so resting on a back/prop collider NEVER injects bounce velocity!
            st.PrevTipWorldPos += corr;
            // Cancel any remaining inward normal velocity (inelastic resting contact, bounciness = 0.0)
            Vector3 vel = st.TipWorldPos - st.PrevTipWorldPos;
            float vn = vel.Dot(normal);
            if (vn < 0f)
            {
                st.PrevTipWorldPos += normal * vn;
            }
            ApplyContactFriction(st, normal);
        }

        private void ResolveBodyCapsuleCollisions(
            Vector3 boneHeadWorld,
            JiggleBoneState st,
            float worldBoneLen,
            int anchorBoneIdx,
            bool isSkirtChain)
        {
            float effMargin = MathF.Max(HairCapsuleMarginMeters, st.ColRadiusMeters);
            for (int c = 0; c < _worldCapsulesCache.Count; c++)
            {
                var wc = _worldCapsulesCache[c];
                bool isAnchorCapsule = wc.BoneIdx == anchorBoneIdx;

                ClosestPointsBetweenSegments(boneHeadWorld, st.TipWorldPos, wc.A, wc.B, out float tBone, out float tCap);
                float effT = MathF.Max(0.75f, tBone);
                Vector3 ptOnBone = boneHeadWorld.Lerp(st.TipWorldPos, effT);
                Vector3 ptOnCap = wc.A.Lerp(wc.B, tCap);
                float capRad = Mathf.Lerp(wc.Radius0, wc.Radius1, tCap);
                Vector3 diff = ptOnBone - ptOnCap;
                float dist = diff.Length();
                Vector3 normal = dist > 1e-5f ? (diff / dist) : (wc.IsTorsoBarrier ? wc.BackWorld : Vector3.Back);

                // Planarized torso back-barrier (matches CSDK13 planarize=true on spine_2/spine_3)
                if (st.IsBackSideOfTorso && wc.IsTorsoBarrier && normal.Dot(wc.BackWorld) < 0.20f)
                {
                    float signedBack = diff.Dot(wc.BackWorld);
                    if (signedBack < capRad + effMargin)
                    {
                        float penBack = MathF.Min(0.15f, (capRad + effMargin) - signedBack);
                        PushTipOutOfCollider(st, wc.BackWorld, penBack / effT);
                        continue;
                    }
                }

                // Planarized thigh skirt-barrier (matches Yamato planarize=true on leg_upper_L/R)
                if (isSkirtChain && wc.IsThighBarrier && normal.Dot(wc.OutwardWorld) < 0.15f && dist < capRad + effMargin)
                {
                    float signedOut = diff.Dot(wc.OutwardWorld);
                    if (signedOut < capRad + effMargin)
                    {
                        float penOut = MathF.Min(0.15f, (capRad + effMargin) - signedOut);
                        PushTipOutOfCollider(st, wc.OutwardWorld, penOut / effT);
                        continue;
                    }
                }

                float minAllowed = isAnchorCapsule
                    ? MathF.Min(capRad + effMargin * 0.5f, st.InitialAnchorDist - 0.002f)
                    : capRad + effMargin;

                if (dist < minAllowed)
                {
                    float pen = MathF.Min(0.15f, minAllowed - dist);
                    PushTipOutOfCollider(st, normal, pen / effT);
                }
            }

            // Custom Oriented Box Colliders (ClothShapeBox — e.g. backpacks, shields, quivers on spine_2/spine_3)
            for (int b = 0; b < _worldBoxesCache.Count; b++)
            {
                var wb = _worldBoxesCache[b];
                Vector3 ext = wb.HalfExtents + new Vector3(effMargin, effMargin, effMargin);
                // Sample bone midpoint (t=0.50), lower span (t=0.75), and tip (t=1.00) so long bones cannot tunnel through thin prop boxes
                for (int si = 0; si < 3; si++)
                {
                    float sampleT = si == 0 ? 1.0f : (si == 1 ? 0.75f : 0.50f);
                    Vector3 sampleWorld = boneHeadWorld.Lerp(st.TipWorldPos, sampleT);
                    Vector3 lp = wb.BoxWorldInv * sampleWorld;
                    if (MathF.Abs(lp.X) >= ext.X || MathF.Abs(lp.Y) >= ext.Y || MathF.Abs(lp.Z) >= ext.Z)
                        continue;

                    if (wb.PlanarizeBackOnly)
                    {
                        // One-sided back ramp: push strictly toward BackWorld so capes slide over the prop even without chain Back Barrier
                        Vector3 backLocal = (wb.BoxWorldInv.Basis * wb.BackWorld).Normalized();
                        float projHalf = MathF.Abs(backLocal.X) * ext.X + MathF.Abs(backLocal.Y) * ext.Y + MathF.Abs(backLocal.Z) * ext.Z;
                        float signedBack = lp.Dot(backLocal);
                        float penBack = MathF.Min(0.18f, projHalf - signedBack);
                        if (penBack > 0f)
                        {
                            PushTipOutOfCollider(st, wb.BackWorld, penBack / sampleT);
                        }
                        continue;
                    }

                    // Push out along the shallowest face normal of the oriented box
                    float px = ext.X - MathF.Abs(lp.X);
                    float py = ext.Y - MathF.Abs(lp.Y);
                    float pz = ext.Z - MathF.Abs(lp.Z);
                    Vector3 localNormal;
                    float pen;
                    if (px <= py && px <= pz)
                    {
                        pen = px;
                        localNormal = new Vector3(lp.X >= 0f ? 1f : -1f, 0f, 0f);
                    }
                    else if (py <= pz)
                    {
                        pen = py;
                        localNormal = new Vector3(0f, lp.Y >= 0f ? 1f : -1f, 0f);
                    }
                    else
                    {
                        pen = pz;
                        localNormal = new Vector3(0f, 0f, lp.Z >= 0f ? 1f : -1f);
                    }
                    Vector3 worldNormal = (wb.BoxWorld.Basis * localNormal).Normalized();
                    PushTipOutOfCollider(st, worldNormal, MathF.Min(0.18f, pen) / sampleT);
                }
            }
        }

        private static void ApplyContactFriction(JiggleBoneState st, Vector3 normal)
        {
            if (st.Friction <= 1e-4f) return;
            Vector3 vel = st.TipWorldPos - st.PrevTipWorldPos;
            Vector3 tang = vel - normal * vel.Dot(normal);
            float f = Mathf.Clamp(st.Friction * 0.45f, 0f, 0.85f);
            st.PrevTipWorldPos += tang * f;
        }

        private static void ClosestPointsBetweenSegments(
            Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2,
            out float s, out float t)
        {
            Vector3 d1 = q1 - p1;
            Vector3 d2 = q2 - p2;
            Vector3 r = p1 - p2;
            float a = d1.Dot(d1);
            float e = d2.Dot(d2);
            float f = d2.Dot(r);

            if (a <= 1e-8f && e <= 1e-8f) { s = 0f; t = 0f; return; }
            if (a <= 1e-8f) { s = 0f; t = Mathf.Clamp(f / e, 0f, 1f); return; }

            float c = d1.Dot(r);
            if (e <= 1e-8f) { t = 0f; s = Mathf.Clamp(-c / a, 0f, 1f); return; }

            float b = d1.Dot(d2);
            float denom = a * e - b * b;
            s = denom != 0f ? Mathf.Clamp((b * f - c * e) / denom, 0f, 1f) : 0f;
            t = (b * s + f) / e;

            if (t < 0f)
            {
                t = 0f;
                s = Mathf.Clamp(-c / a, 0f, 1f);
            }
            else if (t > 1f)
            {
                t = 1f;
                s = Mathf.Clamp((b - c) / a, 0f, 1f);
            }
        }

        private static void ComputeClosestTaperedCapsule(
            Vector3 p,
            Vector3 a,
            Vector3 b,
            float r0,
            float r1,
            out float dist,
            out float interpolatedRadius,
            out Vector3 outwardNormal)
        {
            Vector3 ab = b - a;
            float abSq = ab.LengthSquared();
            float t = abSq > 1e-8f ? Mathf.Clamp((p - a).Dot(ab) / abSq, 0f, 1f) : 0f;
            Vector3 closest = a + ab * t;
            interpolatedRadius = Mathf.Lerp(r0, r1, t);
            Vector3 diff = p - closest;
            float dSq = diff.LengthSquared();
            if (dSq > 1e-8f)
            {
                dist = MathF.Sqrt(dSq);
                outwardNormal = diff / dist;
            }
            else
            {
                dist = 0f;
                outwardNormal = Vector3.Back;
            }
        }
    }
}
