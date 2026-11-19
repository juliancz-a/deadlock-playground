using System;
using System.Collections.Generic;
using Godot;
using DeadlockPlayground.Importer;

namespace DeadlockPlayground.Animation
{
    /// <summary>
    /// Evaluates spring/jiggle physics for preserved custom clothing, hair, cape, and accessory bone chains
    /// appended to a hero Skeleton3D. Runs after AnimationPlayer and Source2BoneConstraintDriver.
    /// </summary>
    public partial class CustomJiggleBoneDriver : Node
    {
        private sealed class JiggleBoneState
        {
            public int BoneIdx;
            public int ParentIdx;
            public Transform3D RestLocal;
            public Vector3 RestScale = Vector3.One;
            public Vector3 AimAxisLocal;
            public float BoneLength;
            public float Stiffness;
            public float Damping;
            public float Gravity;
            public float SpringK = 40f;
            public float DampK = 6f;
            public float GravityMetersPerSecSq = 9.81f;
            public bool IsBackSideOfTorso;
            public float ColRadiusMeters = 0.024f;
            public float StrayRadiusMeters = -1f; // < 0 = disabled / unlimited
            public bool Simulate = true;
            public string BoneName = "";
            public string SourceBoneName = "";
            public float AngleLimitRad = Mathf.DegToRad(85f);
            public Vector3 TipWorldPos;
            public Vector3 Velocity;
            public Vector3 CurBoneHeadWorld;
            public float CurWorldBoneLength;
            public Transform3D CurParentGlobalPose;
            public Transform3D CurTargetLocal;
            public Vector3 CurRestTipWorld;
            public bool SkipPoseWrite;
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
        }

        private sealed class ColliderState
        {
            public int BoneIdx;
            public string BoneName = "";
            public Vector3 Point0Local;
            public float Radius0;
            public Vector3 Point1Local;
            public float Radius1;
        }

        private struct WorldCapsule
        {
            public int BoneIdx;
            public Vector3 A;
            public float Radius0;
            public Vector3 B;
            public float Radius1;
            public bool IsTorsoBarrier;
            public Vector3 BackWorld;
        }

        [Export] public bool EnableSimulation { get; set; } = true;

        private Skeleton3D _skeleton;
        private readonly List<ActiveChain> _chains = new();
        private readonly List<ProxyNeighborLink> _proxyLinks = new();
        private readonly List<ColliderState> _colliders = new();
        private const float HairCapsuleMarginMeters = 0.015f;

        public int ActiveChainCount => _chains.Count;

        /// <summary>
        /// Pauses live simulation and resets all custom physics bones to their clean rest pose
        /// (used during Weight Paint mode so the mesh holds completely still under the brush).
        /// </summary>
        public void SetPausedForEditing(bool paused)
        {
            EnableSimulation = !paused;
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
                    _skeleton.SetBonePoseRotation(st.BoneIdx, st.RestLocal.Basis.GetRotationQuaternion());
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
                for (int i = 0; i < count; i++)
                {
                    int bIdx = _skeleton.FindBone(names[i]);
                    if (bIdx < 0) continue;

                    float t = count > 1 ? i / (float)(count - 1) : 0f;
                    float stiff = Mathf.Lerp(def.StiffnessRoot, def.StiffnessTip, t);
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
                        // Leaf bone or single-bone tuft: continue along strand direction (+Y/parent->bone), never inverted -Y
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
                    var prevP = PhysicsParamResolver.ResolvePreviewBone(def, t, jov);

                    // Determine if this cloth bone rests behind the torso (-Z in hero space) for side-aware collision
                    bool isBackSide = def.PlanarizeBackBarrier;
                    if (!isBackSide && def.Simulation != SimulationKind.Jiggle)
                    {
                        int spineIdx = _skeleton.FindBone("spine_2");
                        if (spineIdx < 0) spineIdx = _skeleton.FindBone("spine_3");
                        if (spineIdx >= 0)
                        {
                            Vector3 spineOrigin = _skeleton.GetBoneGlobalRest(spineIdx).Origin;
                            Vector3 boneTipHero = _skeleton.GetBoneGlobalRest(bIdx) * (aimLocal * boneLen);
                            if (boneTipHero.Z < spineOrigin.Z - 0.03f && boneTipHero.Y >= spineOrigin.Y - 0.55f)
                                isBackSide = true;
                        }
                    }

                    active.Bones.Add(new JiggleBoneState
                    {
                        BoneIdx = bIdx,
                        BoneName = names[i],
                        SourceBoneName = srcName,
                        ParentIdx = _skeleton.GetBoneParent(bIdx),
                        RestLocal = restLocal,
                        RestScale = restScale,
                        AimAxisLocal = aimLocal,
                        BoneLength = boneLen,
                        Simulate = simJoint,
                        Stiffness = Mathf.Clamp(jov?.GoalStrength ?? stiff, 0f, 1f),
                        Damping = Mathf.Clamp(def.Damping, 0f, 1f),
                        Gravity = def.Gravity,
                        SpringK = prevP.SpringK,
                        DampK = prevP.DampK,
                        GravityMetersPerSecSq = prevP.GravityMetersPerSecSq,
                        IsBackSideOfTorso = isBackSide,
                        ColRadiusMeters = prevP.CollisionRadiusMeters,
                        StrayRadiusMeters = prevP.StrayRadiusMeters,
                        AngleLimitRad = def.Simulation == SimulationKind.Jiggle
                            ? Mathf.DegToRad(Mathf.Clamp(def.AngleLimitDeg, 6f, 85f))
                            : Mathf.DegToRad(140f),
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
        /// Hot-updates spring, damping, gravity, collision radius, stray radius, and simulation mode
        /// on active chains without re-rigging meshes or resetting velocities.
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

            bool simModeChanged = false;
            foreach (var active in _chains)
            {
                BoneChainDef match = null;
                if (!string.IsNullOrEmpty(active.ChainId)) byRootOrId.TryGetValue(active.ChainId, out match);
                if (match == null && !string.IsNullOrEmpty(active.Definition?.RootSourceBone))
                    byRootOrId.TryGetValue(active.Definition.RootSourceBone, out match);
                if (match == null) continue;

                if (active.Simulation != match.Simulation) simModeChanged = true;
                active.Simulation = match.Simulation;
                active.Definition = match.Clone();

                int count = active.Bones.Count;
                for (int i = 0; i < count; i++)
                {
                    float t = count > 1 ? i / (float)(count - 1) : 0f;
                    var st = active.Bones[i];
                    var jov = match.FindJointOverride(st.BoneName, st.SourceBoneName);
                    st.Simulate = jov?.Simulate ?? true;
                    var prevP = PhysicsParamResolver.ResolvePreviewBone(match, t, jov);
                    st.Stiffness = Mathf.Clamp(jov?.GoalStrength ?? Mathf.Lerp(match.StiffnessRoot, match.StiffnessTip, t), 0f, 1f);
                    st.Damping = Mathf.Clamp(match.Damping, 0f, 1f);
                    st.Gravity = match.Gravity;
                    st.SpringK = prevP.SpringK;
                    st.DampK = prevP.DampK;
                    st.GravityMetersPerSecSq = prevP.GravityMetersPerSecSq;
                    st.ColRadiusMeters = prevP.CollisionRadiusMeters;
                    st.StrayRadiusMeters = prevP.StrayRadiusMeters;
                    st.AngleLimitRad = match.Simulation == SimulationKind.Jiggle
                        ? Mathf.DegToRad(Mathf.Clamp(match.AngleLimitDeg, 6f, 85f))
                        : Mathf.DegToRad(140f);
                }
            }

            if (simModeChanged)
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

                Vector3 center = Vector3.Zero;
                int centerSamples = 0;
                foreach (var ch in grp)
                {
                    if (ch.AnchorBoneIdx >= 0 && ch.AnchorBoneIdx < _skeleton.GetBoneCount())
                    {
                        center = _skeleton.GetBoneGlobalRest(ch.AnchorBoneIdx).Origin;
                        centerSamples = 1;
                        break;
                    }
                    center += _skeleton.GetBoneGlobalRest(ch.Bones[0].BoneIdx).Origin;
                    centerSamples++;
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

                bool closeLoop = ordered.Count >= 4;
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
                        var stA = chA.Bones[k];
                        var stB = chB.Bones[k];
                        Vector3 restTipA = _skeleton.GetBoneGlobalRest(stA.BoneIdx) * (stA.AimAxisLocal * stA.BoneLength);
                        Vector3 restTipB = _skeleton.GetBoneGlobalRest(stB.BoneIdx) * (stB.AimAxisLocal * stB.BoneLength);
                        float restDist = restTipA.DistanceTo(restTipB);
                        if (restDist > 0.005f && restDist < 0.75f)
                        {
                            _proxyLinks.Add(new ProxyNeighborLink
                            {
                                A = stA,
                                B = stB,
                                RestDistWorld = restDist
                            });
                        }
                    }
                }
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
                    Point0Local = cap.EffectivePoint0,
                    Radius0 = cap.EffectiveRadius0,
                    Point1Local = cap.EffectivePoint1,
                    Radius1 = cap.EffectiveRadius1
                });
            }
        }

        public int ActiveColliderCount => _colliders.Count;

        private readonly List<WorldCapsule> _worldCapsulesCache = new();

        public override void _Process(double delta)
        {
            if (!EnableSimulation || _chains.Count == 0) return;
            if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton) || !_skeleton.IsInsideTree()) return;
            float dt = Math.Clamp((float)delta, 0.001f, 0.033f);
            StepSimulation(dt);
        }

        public void StepSimulation(float dt)
        {
            if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton) || _chains.Count == 0) return;

            Transform3D skelGlobal = _skeleton.IsInsideTree() ? _skeleton.GlobalTransform : Transform3D.Identity;
            int boneCount = _skeleton.GetBoneCount();

            _worldCapsulesCache.Clear();
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

                // Compute current world-space back direction (-Z in hero rest space rotated by bone's pose delta)
                Vector3 backWorld = (skelGlobal.Basis * new Vector3(0f, 0f, -1f)).Normalized();
                if (isTorsoBarrier)
                {
                    Basis restBasis = _skeleton.GetBoneGlobalRest(c.BoneIdx).Basis.Orthonormalized();
                    Basis poseBasis = poseSkel.Basis.Orthonormalized();
                    Vector3 backSkel = poseBasis * (restBasis.Inverse() * new Vector3(0f, 0f, -1f));
                    Vector3 bw = skelGlobal.Basis * backSkel;
                    if (bw.LengthSquared() > 1e-6f) backWorld = bw.Normalized();
                }

                _worldCapsulesCache.Add(new WorldCapsule
                {
                    BoneIdx = c.BoneIdx,
                    A = boneWorld * c.Point0Local,
                    Radius0 = c.Radius0 * worldScale,
                    B = boneWorld * c.Point1Local,
                    Radius1 = c.Radius1 * worldScale,
                    IsTorsoBarrier = isTorsoBarrier,
                    BackWorld = backWorld
                });
            }

            foreach (var chain in _chains)
            {
                foreach (var st in chain.Bones)
                {
                    if (st.BoneIdx < 0 || st.BoneIdx >= boneCount) continue;

                    Transform3D parentGlobalPose = (st.ParentIdx >= 0 && st.ParentIdx < boneCount)
                        ? _skeleton.GetBoneGlobalPose(st.ParentIdx)
                        : Transform3D.Identity;

                    Transform3D targetLocal = st.RestLocal;
                    Transform3D targetSkelPose = parentGlobalPose * targetLocal;
                    Vector3 boneHeadWorld = skelGlobal * targetSkelPose.Origin;
                    Vector3 restTipWorld = skelGlobal * (targetSkelPose * (st.AimAxisLocal * st.BoneLength));
                    float worldBoneLen = MathF.Max(0.02f, restTipWorld.DistanceTo(boneHeadWorld));

                    st.CurParentGlobalPose = parentGlobalPose;
                    st.CurTargetLocal = targetLocal;
                    st.CurBoneHeadWorld = boneHeadWorld;
                    st.CurRestTipWorld = restTipWorld;
                    st.CurWorldBoneLength = worldBoneLen;
                    st.SkipPoseWrite = false;

                    if (!st.Initialized || (restTipWorld - st.TipWorldPos).LengthSquared() > 2.25f)
                    {
                        st.TipWorldPos = restTipWorld;
                        st.Velocity = Vector3.Zero;
                        st.Initialized = true;
                        st.InitialAnchorDist = float.MaxValue;
                        if (chain.AnchorBoneIdx >= 0)
                        {
                            foreach (var wc in _worldCapsulesCache)
                            {
                                if (wc.BoneIdx != chain.AnchorBoneIdx) continue;
                                ComputeClosestTaperedCapsule(restTipWorld, wc.A, wc.B, wc.Radius0, wc.Radius1, out float dist, out _, out _);
                                if (dist < st.InitialAnchorDist) st.InitialAnchorDist = dist;
                            }
                        }
                        _skeleton.SetBonePosePosition(st.BoneIdx, st.RestLocal.Origin);
                        _skeleton.SetBonePoseRotation(st.BoneIdx, st.RestLocal.Basis.GetRotationQuaternion());
                        _skeleton.SetBonePoseScale(st.BoneIdx, st.RestScale);
                        st.SkipPoseWrite = true;
                        continue;
                    }

                    if (!st.Simulate)
                    {
                        st.TipWorldPos = restTipWorld;
                        st.Velocity = Vector3.Zero;
                        st.Initialized = true;
                        _skeleton.SetBonePosePosition(st.BoneIdx, st.RestLocal.Origin);
                        _skeleton.SetBonePoseRotation(st.BoneIdx, st.RestLocal.Basis.Orthonormalized().GetRotationQuaternion());
                        _skeleton.SetBonePoseScale(st.BoneIdx, st.RestScale);
                        st.SkipPoseWrite = true;
                        continue;
                    }

                    Vector3 displacement = restTipWorld - st.TipWorldPos;
                    float springK = st.SpringK;
                    float dampK = st.DampK;
                    // When a back-draping cloth bone's head-rotated rest target enters a torso capsule
                    // (e.g. during Crouch + Look Up), attenuate spring attraction so it doesn't yank hair into the chest.
                    if (st.IsBackSideOfTorso && chain.Simulation != SimulationKind.Jiggle && springK > 1f)
                    {
                        for (int c = 0; c < _worldCapsulesCache.Count; c++)
                        {
                            var wc = _worldCapsulesCache[c];
                            if (!wc.IsTorsoBarrier) continue;
                            ComputeClosestTaperedCapsule(restTipWorld, wc.A, wc.B, wc.Radius0, wc.Radius1, out float rDist, out float rCap, out _);
                            if (rDist < rCap + st.ColRadiusMeters)
                            {
                                springK *= 0.12f;
                                break;
                            }
                        }
                    }

                    // True gravity (9.81 * g m/s², not crushed by stiffness) so low-stiffness skirts & hair hang naturally.
                    Vector3 gravityAcc = new Vector3(0f, -st.GravityMetersPerSecSq, 0f);

                    Vector3 accel = displacement * springK + gravityAcc - st.Velocity * dampK;
                    // Clamp peak acceleration and apply micro-sleep damping so cloth settles calmly without jitter
                    float accelMagSq = accel.LengthSquared();
                    if (accelMagSq > 40000f) accel *= (200f / MathF.Sqrt(accelMagSq));
                    st.Velocity += accel * dt;
                    if (st.Velocity.LengthSquared() < 0.0004f) st.Velocity *= 0.65f;
                    st.TipWorldPos += st.Velocity * dt;

                    // Optional stray-radius leash around restTipWorld (< 0 = disabled / unlimited)
                    if (st.StrayRadiusMeters > 0f)
                    {
                        Vector3 strayVec = st.TipWorldPos - restTipWorld;
                        if (strayVec.LengthSquared() > st.StrayRadiusMeters * st.StrayRadiusMeters)
                        {
                            st.TipWorldPos = restTipWorld + strayVec.Normalized() * st.StrayRadiusMeters;
                        }
                    }

                    Vector3 deltaTip = st.TipWorldPos - boneHeadWorld;
                    if (deltaTip.LengthSquared() < 1e-8f) { st.SkipPoseWrite = true; continue; }
                    st.TipWorldPos = boneHeadWorld + deltaTip.Normalized() * worldBoneLen;

                    if (_worldCapsulesCache.Count > 0 && chain.Simulation != SimulationKind.Jiggle)
                    {
                        ResolveBodyCapsuleCollisions(boneHeadWorld, st, worldBoneLen, chain.AnchorBoneIdx);
                    }

                    ApplyBonePoseFromTip(skelGlobal, st, chain.Simulation == SimulationKind.Jiggle);
                }
            }

            // Coupled ProxyCloth PBD sheet relaxation pass linking adjacent skirt/coat/back-hair strands
            if (_proxyLinks.Count > 0)
            {
                for (int pbdIter = 0; pbdIter < 4; pbdIter++)
                {
                    foreach (var link in _proxyLinks)
                    {
                        if (link.A.SkipPoseWrite || link.B.SkipPoseWrite) continue;
                        Vector3 diff = link.B.TipWorldPos - link.A.TipWorldPos;
                        float dist = diff.Length();
                        if (dist < 1e-5f) continue;
                        float err = dist - link.RestDistWorld;
                        // Preserve skirt/sheet circumference cleanly against gravity collapse
                        float k = err > 0f ? 0.72f : 0.45f;
                        Vector3 corr = (diff / dist) * (err * 0.5f * k);
                        link.A.TipWorldPos += corr;
                        link.B.TipWorldPos -= corr;
                    }

                    foreach (var chain in _chains)
                    {
                        if (chain.Simulation != SimulationKind.ProxyCloth) continue;
                        foreach (var st in chain.Bones)
                        {
                            if (st.SkipPoseWrite) continue;
                            Vector3 d = st.TipWorldPos - st.CurBoneHeadWorld;
                            if (d.LengthSquared() > 1e-8f)
                                st.TipWorldPos = st.CurBoneHeadWorld + d.Normalized() * st.CurWorldBoneLength;
                            if (_worldCapsulesCache.Count > 0)
                                ResolveBodyCapsuleCollisions(st.CurBoneHeadWorld, st, st.CurWorldBoneLength, chain.AnchorBoneIdx);
                        }
                    }
                }

                // Commit coupled sheet rotations top-down along each ProxyCloth chain
                foreach (var chain in _chains)
                {
                    if (chain.Simulation != SimulationKind.ProxyCloth) continue;
                    foreach (var st in chain.Bones)
                    {
                        if (st.SkipPoseWrite) continue;
                        st.CurParentGlobalPose = (st.ParentIdx >= 0 && st.ParentIdx < boneCount)
                            ? _skeleton.GetBoneGlobalPose(st.ParentIdx)
                            : Transform3D.Identity;
                        st.CurTargetLocal = st.RestLocal;
                        Transform3D targetSkelPose = st.CurParentGlobalPose * st.CurTargetLocal;
                        st.CurBoneHeadWorld = skelGlobal * targetSkelPose.Origin;
                        st.CurRestTipWorld = skelGlobal * (targetSkelPose * (st.AimAxisLocal * st.BoneLength));
                        ApplyBonePoseFromTip(skelGlobal, st, chain.Simulation == SimulationKind.Jiggle);
                    }
                }
            }
        }

        private void ApplyBonePoseFromTip(Transform3D skelGlobal, JiggleBoneState st, bool enforceTightAngleLimit = true)
        {
            Vector3 restDirWorld = (st.CurRestTipWorld - st.CurBoneHeadWorld).Normalized();
            Vector3 curDirWorld = (st.TipWorldPos - st.CurBoneHeadWorld).Normalized();
            if (restDirWorld.LengthSquared() <= 0.5f || curDirWorld.LengthSquared() <= 0.5f) return;

            _skeleton.SetBonePosePosition(st.BoneIdx, st.CurTargetLocal.Origin);
            _skeleton.SetBonePoseScale(st.BoneIdx, st.RestScale);

            float dot = Mathf.Clamp(restDirWorld.Dot(curDirWorld), -1f, 1f);
            if (dot < 0.99995f)
            {
                // Convert world swing back into parent-local rotation delta with orthonormalized basis
                Basis skelToParent = (skelGlobal.Basis * st.CurParentGlobalPose.Basis).Orthonormalized().Inverse();
                Vector3 restDirLocal = (skelToParent * restDirWorld).Normalized();
                Vector3 curDirLocal = (skelToParent * curDirWorld).Normalized();
                float localDot = Mathf.Clamp(restDirLocal.Dot(curDirLocal), -1f, 1f);
                float angle = Mathf.Acos(localDot);
                // Cloth / ProxyCloth in Source 2 FeModel does not hard-lock to a 35 deg Jiggle cone
                // (otherwise a 100 deg crouch + look-up forces hair straight through the chest).
                float maxAllowedRad = enforceTightAngleLimit ? st.AngleLimitRad : Mathf.DegToRad(140f);
                float clampedAngle = Mathf.Min(angle, maxAllowedRad);

                Quaternion swingLocal = Quaternion.Identity;
                if (clampedAngle > 1e-4f)
                {
                    Vector3 axis = restDirLocal.Cross(curDirLocal);
                    float axisLenSq = axis.LengthSquared();
                    if (axisLenSq > 1e-8f)
                    {
                        axis /= Mathf.Sqrt(axisLenSq);
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
                Quaternion finalRot = (swingLocal * st.CurTargetLocal.Basis.Orthonormalized().GetRotationQuaternion()).Normalized();
                _skeleton.SetBonePoseRotation(st.BoneIdx, finalRot);
            }
            else
            {
                _skeleton.SetBonePoseRotation(st.BoneIdx, st.CurTargetLocal.Basis.Orthonormalized().GetRotationQuaternion());
            }
        }

        private void ResolveBodyCapsuleCollisions(Vector3 boneHeadWorld, JiggleBoneState st, float worldBoneLen, int anchorBoneIdx)
        {
            float effMargin = MathF.Max(HairCapsuleMarginMeters, st.ColRadiusMeters);
            for (int iter = 0; iter < 4; iter++)
            {
                bool pushed = false;
                for (int c = 0; c < _worldCapsulesCache.Count; c++)
                {
                    var wc = _worldCapsulesCache[c];
                    bool isAnchorCapsule = wc.BoneIdx == anchorBoneIdx;

                    // True segment-to-segment closest point between the bone span [boneHeadWorld, TipWorldPos]
                    // and the capsule axis [wc.A, wc.B], plus explicit tip check.
                    ClosestPointsBetweenSegments(boneHeadWorld, st.TipWorldPos, wc.A, wc.B, out float tBone, out float tCap);
                    // Keep effT >= 0.75 so near-root contact never amplifies pushout by 4x and causes self-oscillation!
                    float effT = MathF.Max(0.75f, tBone);
                    Vector3 ptOnBone = boneHeadWorld.Lerp(st.TipWorldPos, effT);
                    Vector3 ptOnCap = wc.A.Lerp(wc.B, tCap);
                    float capRad = Mathf.Lerp(wc.Radius0, wc.Radius1, tCap);
                    Vector3 diff = ptOnBone - ptOnCap;
                    float dist = diff.Length();
                    Vector3 normal = dist > 1e-5f ? (diff / dist) : (wc.IsTorsoBarrier ? wc.BackWorld : Vector3.Back);

                    // Side-aware torso barrier: if a back-draping strand ever approaches or crosses the
                    // centerline of spine_1..3 / neck_0, push strictly toward the back half-space (-Z)
                    // instead of letting the normal flip to +Z and eject the hair out the front of the chest!
                    if (st.IsBackSideOfTorso && wc.IsTorsoBarrier && normal.Dot(wc.BackWorld) < 0.20f)
                    {
                        normal = wc.BackWorld;
                        // Signed distance along BackWorld from capsule center so front-side penetration is fully reversed
                        float signedBack = diff.Dot(wc.BackWorld);
                        if (signedBack < capRad + effMargin)
                        {
                            float penBack = MathF.Min(0.15f, (capRad + effMargin) - signedBack);
                            st.TipWorldPos += wc.BackWorld * (penBack / effT);
                            float vn = st.Velocity.Dot(wc.BackWorld);
                            if (vn < 0f) st.Velocity -= wc.BackWorld * vn;
                            pushed = true;
                            continue;
                        }
                    }

                    float minAllowed = isAnchorCapsule
                        ? MathF.Min(capRad + effMargin * 0.5f, st.InitialAnchorDist - 0.002f)
                        : capRad + effMargin;

                    if (dist < minAllowed)
                    {
                        float pen = MathF.Min(0.15f, minAllowed - dist);
                        st.TipWorldPos += normal * (pen / effT);
                        float vn = st.Velocity.Dot(normal);
                        if (vn < 0f)
                            st.Velocity -= normal * vn;
                        pushed = true;
                    }
                }

                Vector3 d = st.TipWorldPos - boneHeadWorld;
                float lenSq = d.LengthSquared();
                if (lenSq > 1e-8f)
                    st.TipWorldPos = boneHeadWorld + (d / MathF.Sqrt(lenSq)) * worldBoneLen;
                if (!pushed) break;
            }
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

            if (a <= 1e-8f && e <= 1e-8f)
            {
                s = 0f; t = 0f; return;
            }
            if (a <= 1e-8f)
            {
                s = 0f;
                t = Mathf.Clamp(f / e, 0f, 1f);
                return;
            }

            float c = d1.Dot(r);
            if (e <= 1e-8f)
            {
                t = 0f;
                s = Mathf.Clamp(-c / a, 0f, 1f);
                return;
            }

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

