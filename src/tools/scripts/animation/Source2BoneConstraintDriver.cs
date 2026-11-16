using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Godot;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.ModelAnimation;
using ValveResourceFormat.ResourceTypes.ModelData.Attachments;
using ValveResourceFormat.ResourceTypes.ModelFlex;
using ValveResourceFormat.ResourceTypes.RubikonPhysics.Softbody;
using ValveResourceFormat.Utils;
using GdBasis = Godot.Basis;
using GdQuat = Godot.Quaternion;
using GdTransform3D = Godot.Transform3D;
using GdVec3 = Godot.Vector3;
using SysMat4 = System.Numerics.Matrix4x4;
using SysQuat = System.Numerics.Quaternion;
using SysVec3 = System.Numerics.Vector3;
using SysVec4 = System.Numerics.Vector4;

namespace DeadlockPlayground.Animation
{
    /// <summary>
    /// Evaluates Source 2 bone constraints (all 11 ModelDoc constraint types) and softbody jiggle bones
    /// against a Godot <see cref="Skeleton3D"/> in Source 2 Z-up inches space, writing modified slave bones
    /// and flex morph targets back cleanly after AnimationPlayer and gizmo edits.
    /// </summary>
    public partial class Source2BoneConstraintDriver : Node
    {
        private const float SourceToGltfScale = 0.0254f;
        private const float MetersToInches = 1f / SourceToGltfScale;

        // Matches ValveResourceFormat.IO.GltfModelExporter.Conversion.SourceToGltfRotation
        private static readonly SysQuat SourceToGltfRotation = new(-0.5f, -0.5f, -0.5f, 0.5f);
        private static readonly SysQuat GltfToSourceRotation = SysQuat.Conjugate(SourceToGltfRotation);
        private static readonly SysMat4 SourceToGltfMat = SysMat4.CreateFromQuaternion(SourceToGltfRotation);
        private static readonly SysMat4 GltfToSourceMat = SysMat4.CreateFromQuaternion(GltfToSourceRotation);

        /// <summary>Gets or sets whether Source 2 bone constraints are evaluated.</summary>
        [Export]
        public bool EnableConstraints { get; set; } = true;

        /// <summary>Gets or sets whether FeModel jiggle bone spring simulation is active (off by default for deterministic checks).</summary>
        [Export]
        public bool EnableJiggleBones { get; set; } = false;

        /// <summary>Gets whether <see cref="Initialize"/> succeeded with a valid VRF skeleton and Godot skeleton.</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>Gets the number of resolved Source 2 bone constraints.</summary>
        public int ConstraintCount => _constraintSolver?.Count ?? 0;

        /// <summary>Gets the number of resolved Source 2 jiggle bones.</summary>
        public int JiggleBoneCount => _jiggleSolver?.Count ?? 0;

        private Model _vrfModel;
        private Skeleton _vrfSkeleton;
        private Skeleton3D _godotSkeleton;

        private BoneConstraintSolverCore _constraintSolver;
        private JiggleBoneSolverCore _jiggleSolver;

        private int[] _vrfToGodotBone = [];
        private int[] _godotToVrfBone = [];
        private int[] _vrfParentIndices = [];
        private SysMat4[] _vrfLocalBindPose = [];
        private SysMat4[] _pose = [];

        // Sorted by skeleton depth so parent transforms always resolve before children on writeback.
        private int[] _allTrackedVrfBones = [];
        private bool[] _isConstraintDirtyVrfBone = [];
        private bool[] _isJiggleDirtyVrfBone = [];

        // Base-pose preservation prevents weight < 1 constraints from creeping and jiggle bones from drifting when static.
        private bool[] _hasWritten = [];
        private GdVec3[] _baseLocalPos = [];
        private GdQuat[] _baseLocalRot = [];
        private GdVec3[] _baseLocalScale = [];
        private GdVec3[] _lastWrittenPos = [];
        private GdQuat[] _lastWrittenRot = [];
        private GdVec3[] _lastWrittenScale = [];

        private int[] _vrfOrderByDepth = [];
        private float[] _morphDefaults = [];
        private float[] _morphBuffer = [];
        private List<(MeshInstance3D Mesh, int BlendShapeIdx)>[] _morphTargets = [];

        private bool _wasJiggleEnabled;
        private bool _isEvaluating;

        /// <summary>
        /// Initializes constraint and jiggle solvers for a VRF <see cref="Model"/> and Godot <see cref="Skeleton3D"/>.
        /// </summary>
        public void Initialize(Model vrfModel, Skeleton3D godotSkeleton)
        {
            Initialize(vrfModel, godotSkeleton, null);
        }

        /// <summary>
        /// Initializes constraint and jiggle solvers for a VRF <see cref="Model"/>, Godot <see cref="Skeleton3D"/>,
        /// and optional root node for discovering blend-shape meshes.
        /// </summary>
        public void Initialize(Model vrfModel, Skeleton3D godotSkeleton, Node morphSearchRoot)
        {
            ResetState();

            if (vrfModel?.Skeleton == null || vrfModel.Skeleton.Bones.Length == 0 ||
                godotSkeleton == null || !IsInstanceValid(godotSkeleton))
            {
                return;
            }

            _vrfModel = vrfModel;
            _vrfSkeleton = vrfModel.Skeleton;
            _godotSkeleton = godotSkeleton;

            // Run after AnimationPlayer and SkeletonGizmoManager (default priority 0)
            ProcessPriority = 50;

            int vrfBoneCount = _vrfSkeleton.Bones.Length;
            int godotBoneCount = godotSkeleton.GetBoneCount();

            _vrfToGodotBone = new int[vrfBoneCount];
            _godotToVrfBone = new int[godotBoneCount];
            Array.Fill(_godotToVrfBone, -1);

            _vrfParentIndices = new int[vrfBoneCount];
            _vrfLocalBindPose = new SysMat4[vrfBoneCount];
            _pose = new SysMat4[vrfBoneCount];

            for (int i = 0; i < vrfBoneCount; i++)
            {
                var bone = _vrfSkeleton.Bones[i];
                _vrfParentIndices[i] = bone.Parent != null ? bone.Parent.Index : -1;
                _vrfLocalBindPose[i] =
                    SysMat4.CreateScale(bone.Scale) *
                    SysMat4.CreateFromQuaternion(bone.Angle) *
                    SysMat4.CreateTranslation(bone.Position);

                int gIdx = godotSkeleton.FindBone(bone.Name);
                _vrfToGodotBone[i] = gIdx;
                if (gIdx >= 0 && gIdx < godotBoneCount)
                {
                    _godotToVrfBone[gIdx] = i;
                }
            }

            _constraintSolver = new BoneConstraintSolverCore(
                BoneConstraintBase.ReadList(vrfModel),
                _vrfSkeleton,
                vrfModel.FlexControllers ?? [],
                vrfModel.Attachments ?? new Dictionary<string, Attachment>());

            _jiggleSolver = JiggleBoneSolverCore.Create(vrfModel);

            _isConstraintDirtyVrfBone = new bool[vrfBoneCount];
            foreach (int b in _constraintSolver.ConstrainedBoneIndices)
            {
                if ((uint)b < (uint)vrfBoneCount)
                {
                    _isConstraintDirtyVrfBone[b] = true;
                }
            }

            _isJiggleDirtyVrfBone = new bool[vrfBoneCount];
            if (_jiggleSolver != null)
            {
                foreach (int b in _jiggleSolver.JiggleBoneIndices)
                {
                    if ((uint)b < (uint)vrfBoneCount)
                    {
                        _isJiggleDirtyVrfBone[b] = true;
                    }
                }
            }

            int[] depths = ComputeSkeletonDepths(_vrfParentIndices);
            _vrfOrderByDepth = Enumerable.Range(0, vrfBoneCount).ToArray();
            Array.Sort(_vrfOrderByDepth, (a, b) =>
            {
                int cmp = depths[a].CompareTo(depths[b]);
                return cmp != 0 ? cmp : a.CompareTo(b);
            });

            var tracked = new List<int>();
            for (int i = 0; i < vrfBoneCount; i++)
            {
                if (_isConstraintDirtyVrfBone[i] || _isJiggleDirtyVrfBone[i])
                {
                    tracked.Add(i);
                }
            }

            tracked.Sort((a, b) =>
            {
                int cmp = depths[a].CompareTo(depths[b]);
                return cmp != 0 ? cmp : a.CompareTo(b);
            });
            _allTrackedVrfBones = [.. tracked];

            _hasWritten = new bool[vrfBoneCount];
            _baseLocalPos = new GdVec3[vrfBoneCount];
            _baseLocalRot = new GdQuat[vrfBoneCount];
            _baseLocalScale = new GdVec3[vrfBoneCount];
            _lastWrittenPos = new GdVec3[vrfBoneCount];
            _lastWrittenRot = new GdQuat[vrfBoneCount];
            _lastWrittenScale = new GdVec3[vrfBoneCount];

            int flexCount = vrfModel.FlexControllers?.Length ?? 0;
            _morphDefaults = new float[flexCount];
            _morphBuffer = new float[flexCount];
            BuildMorphTargetLookup(vrfModel.FlexControllers ?? [], morphSearchRoot);

            _wasJiggleEnabled = EnableJiggleBones;
            IsInitialized = true;
        }

        /// <summary>
        /// Sets an optional flex controller input value before morph-reading constraints (such as MorphConstraint) evaluate.
        /// </summary>
        public void SetFlexControllerValue(string name, float value)
        {
            if (_vrfModel?.FlexControllers == null || string.IsNullOrEmpty(name)) return;

            for (int i = 0; i < _vrfModel.FlexControllers.Length; i++)
            {
                if (string.Equals(_vrfModel.FlexControllers[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    _morphDefaults[i] = value;
                    break;
                }
            }
        }

        /// <summary>
        /// Resets all external flex controller inputs back to zero.
        /// </summary>
        public void ClearFlexControllerOverrides()
        {
            Array.Clear(_morphDefaults);
        }

        public override void _Process(double delta)
        {
            if (!IsInitialized) return;
            if (!EnableConstraints && !EnableJiggleBones && !_wasJiggleEnabled) return;

            EvaluateNow((float)delta);
        }

        /// <summary>
        /// Synchronizes the current Godot <see cref="Skeleton3D"/> pose into Source 2 model space, evaluates active
        /// jiggle bones and bone constraints, and writes modified slave bones and morph blend shapes back.
        /// </summary>
        public void EvaluateNow(float deltaTime = 0f)
        {
            if (!IsInitialized || _isEvaluating) return;
            if (_godotSkeleton == null || !IsInstanceValid(_godotSkeleton) || _godotSkeleton.GetBoneCount() == 0) return;

            _isEvaluating = true;
            try
            {
                bool runJiggle = EnableJiggleBones && _jiggleSolver != null;
                bool runConstraints = EnableConstraints && _constraintSolver != null && _constraintSolver.Count > 0;

                if (_wasJiggleEnabled && !runJiggle)
                {
                    _jiggleSolver?.Reset();
                }
                _wasJiggleEnabled = runJiggle;

                // 1. Restore unmodified base local poses for any tracked slave/jiggle bones whose pose still equals
                //    what we wrote last frame (preventing iterative Slerp creep on weight < 1 or static jiggle drift).
                bool restoredAny = false;
                for (int t = 0; t < _allTrackedVrfBones.Length; t++)
                {
                    int b = _allTrackedVrfBones[t];
                    int gIdx = _vrfToGodotBone[b];
                    if (gIdx < 0) continue;

                    GdVec3 curPos = _godotSkeleton.GetBonePosePosition(gIdx);
                    GdQuat curRot = _godotSkeleton.GetBonePoseRotation(gIdx);
                    GdVec3 curScale = _godotSkeleton.GetBonePoseScale(gIdx);

                    if (_hasWritten[b] &&
                        ApproxEqualVec3(curPos, _lastWrittenPos[b]) &&
                        ApproxEqualQuat(curRot, _lastWrittenRot[b]) &&
                        ApproxEqualVec3(curScale, _lastWrittenScale[b]))
                    {
                        _godotSkeleton.SetBonePosePosition(gIdx, _baseLocalPos[b]);
                        _godotSkeleton.SetBonePoseRotation(gIdx, _baseLocalRot[b]);
                        _godotSkeleton.SetBonePoseScale(gIdx, _baseLocalScale[b]);
                        _hasWritten[b] = false;
                        restoredAny = true;
                    }
                    else
                    {
                        _baseLocalPos[b] = curPos;
                        _baseLocalRot[b] = curRot;
                        _baseLocalScale[b] = curScale;
                    }
                }

                if (restoredAny)
                {
                    _godotSkeleton.ForceUpdateAllBoneTransforms();
                }

                if (!runJiggle && !runConstraints)
                {
                    return;
                }

                // 2. Sample Godot skeleton global poses into Source 2 model-space Matrix4x4[] (Z-up inches) in depth order.
                for (int idx = 0; idx < _vrfOrderByDepth.Length; idx++)
                {
                    int b = _vrfOrderByDepth[idx];
                    int gIdx = _vrfToGodotBone[b];
                    if (gIdx >= 0)
                    {
                        _pose[b] = GodotGlobalToSourcePose(_godotSkeleton.GetBoneGlobalPose(gIdx));
                    }
                    else
                    {
                        int p = _vrfParentIndices[b];
                        _pose[b] = p >= 0 ? _vrfLocalBindPose[b] * _pose[p] : _vrfLocalBindPose[b];
                    }
                }

                // 3. Evaluate JiggleBones first (in Source 2 world space) followed by BoneConstraints, matching VRF AnimationController.
                if (_morphDefaults.Length > 0)
                {
                    Array.Copy(_morphDefaults, _morphBuffer, _morphDefaults.Length);
                }

                if (runJiggle && deltaTime > 0f)
                {
                    SysMat4 modelToWorld = GodotTransformToSourceWorld(_godotSkeleton.GlobalTransform);
                    _jiggleSolver!.Evaluate(_pose, modelToWorld, deltaTime);
                }

                if (runConstraints)
                {
                    _constraintSolver!.Evaluate(_pose, _morphBuffer);
                }

                // 4. Write back ONLY active modified slave / jiggle bones as local poses in depth order.
                bool wroteAny = false;
                for (int t = 0; t < _allTrackedVrfBones.Length; t++)
                {
                    int b = _allTrackedVrfBones[t];
                    bool shouldWrite = (runConstraints && _isConstraintDirtyVrfBone[b]) ||
                                       (runJiggle && _isJiggleDirtyVrfBone[b]);
                    if (!shouldWrite) continue;

                    int gIdx = _vrfToGodotBone[b];
                    if (gIdx < 0) continue;

                    GdTransform3D godotGlobal = SourcePoseToGodotGlobal(_pose[b]);
                    int gParent = _godotSkeleton.GetBoneParent(gIdx);

                    GdTransform3D godotParentGlobal;
                    if (gParent >= 0)
                    {
                        int vrfParent = gParent < _godotToVrfBone.Length ? _godotToVrfBone[gParent] : -1;
                        godotParentGlobal = vrfParent >= 0
                            ? SourcePoseToGodotGlobal(_pose[vrfParent])
                            : _godotSkeleton.GetBoneGlobalPose(gParent);
                    }
                    else
                    {
                        godotParentGlobal = GdTransform3D.Identity;
                    }

                    GdTransform3D godotLocal = godotParentGlobal.AffineInverse() * godotGlobal;
                    GdVec3 outPos = godotLocal.Origin;
                    GdVec3 outScale = godotLocal.Basis.Scale;
                    GdQuat outRot = godotLocal.Basis.Orthonormalized().GetRotationQuaternion().Normalized();

                    _godotSkeleton.SetBonePosePosition(gIdx, outPos);
                    _godotSkeleton.SetBonePoseRotation(gIdx, outRot);
                    _godotSkeleton.SetBonePoseScale(gIdx, outScale);

                    _lastWrittenPos[b] = outPos;
                    _lastWrittenRot[b] = outRot;
                    _lastWrittenScale[b] = outScale;
                    _hasWritten[b] = true;
                    wroteAny = true;
                }

                if (wroteAny)
                {
                    _godotSkeleton.ForceUpdateAllBoneTransforms();
                }

                if (runConstraints && _constraintSolver is { WritesMorphs: true })
                {
                    ApplyMorphOutputs();
                }
            }
            finally
            {
                _isEvaluating = false;
            }
        }

        private void ResetState()
        {
            IsInitialized = false;
            _vrfModel = null;
            _vrfSkeleton = null;
            _godotSkeleton = null;
            _constraintSolver = null;
            _jiggleSolver = null;
            _vrfToGodotBone = [];
            _godotToVrfBone = [];
            _vrfParentIndices = [];
            _vrfLocalBindPose = [];
            _pose = [];
            _vrfOrderByDepth = [];
            _allTrackedVrfBones = [];
            _isConstraintDirtyVrfBone = [];
            _isJiggleDirtyVrfBone = [];
            _hasWritten = [];
            _baseLocalPos = [];
            _baseLocalRot = [];
            _baseLocalScale = [];
            _lastWrittenPos = [];
            _lastWrittenRot = [];
            _lastWrittenScale = [];
            _morphDefaults = [];
            _morphBuffer = [];
            _morphTargets = [];
            _wasJiggleEnabled = false;
        }

        private void BuildMorphTargetLookup(FlexController[] flexControllers, Node morphSearchRoot)
        {
            _morphTargets = new List<(MeshInstance3D, int)>[flexControllers.Length];
            for (int i = 0; i < flexControllers.Length; i++)
            {
                _morphTargets[i] = new List<(MeshInstance3D, int)>();
            }

            if (flexControllers.Length == 0 || _godotSkeleton == null) return;

            var meshes = new List<MeshInstance3D>();
            Node searchRoot = morphSearchRoot ?? _godotSkeleton.GetParent() ?? _godotSkeleton;
            CollectMeshInstances(searchRoot, meshes);

            foreach (var mesh in meshes)
            {
                if (mesh.GetBlendShapeCount() <= 0) continue;

                for (int i = 0; i < flexControllers.Length; i++)
                {
                    int idx = mesh.FindBlendShapeByName(flexControllers[i].Name);
                    if (idx >= 0)
                    {
                        _morphTargets[i].Add((mesh, idx));
                    }
                }
            }
        }

        private void ApplyMorphOutputs()
        {
            for (int i = 0; i < _morphTargets.Length && i < _morphBuffer.Length; i++)
            {
                var targets = _morphTargets[i];
                if (targets == null || targets.Count == 0) continue;

                float value = _morphBuffer[i];
                for (int t = 0; t < targets.Count; t++)
                {
                    var (mesh, blendIdx) = targets[t];
                    if (IsInstanceValid(mesh))
                    {
                        mesh.SetBlendShapeValue(blendIdx, value);
                    }
                }
            }
        }

        private static void CollectMeshInstances(Node root, List<MeshInstance3D> results)
        {
            if (root is MeshInstance3D mesh)
            {
                results.Add(mesh);
            }

            int childCount = root.GetChildCount();
            for (int i = 0; i < childCount; i++)
            {
                CollectMeshInstances(root.GetChild(i), results);
            }
        }

        private static int[] ComputeSkeletonDepths(int[] parents)
        {
            int[] depths = new int[parents.Length];
            for (int i = 0; i < parents.Length; i++)
            {
                int depth = 0;
                int cur = parents[i];
                int guard = 0;
                while (cur >= 0 && cur < parents.Length && guard++ < parents.Length)
                {
                    depth++;
                    cur = parents[cur];
                }
                depths[i] = depth;
            }
            return depths;
        }

        private static SysMat4 GodotGlobalToSourcePose(in GdTransform3D t)
        {
            GdBasis b = t.Basis;
            GdVec3 o = t.Origin;
            var mGodotInches = new SysMat4(
                b.X.X, b.X.Y, b.X.Z, 0f,
                b.Y.X, b.Y.Y, b.Y.Z, 0f,
                b.Z.X, b.Z.Y, b.Z.Z, 0f,
                o.X * MetersToInches, o.Y * MetersToInches, o.Z * MetersToInches, 1f);
            return mGodotInches * GltfToSourceMat;
        }

        private static GdTransform3D SourcePoseToGodotGlobal(in SysMat4 sourcePose)
        {
            SysMat4 mGodotInches = sourcePose * SourceToGltfMat;
            var basis = new GdBasis(
                new GdVec3(mGodotInches.M11, mGodotInches.M12, mGodotInches.M13),
                new GdVec3(mGodotInches.M21, mGodotInches.M22, mGodotInches.M23),
                new GdVec3(mGodotInches.M31, mGodotInches.M32, mGodotInches.M33));
            var origin = new GdVec3(
                mGodotInches.M41 * SourceToGltfScale,
                mGodotInches.M42 * SourceToGltfScale,
                mGodotInches.M43 * SourceToGltfScale);
            return new GdTransform3D(basis, origin);
        }

        private static SysMat4 GodotTransformToSourceWorld(in GdTransform3D t)
        {
            return SourceToGltfMat * GodotGlobalToSourcePose(t);
        }

        private static bool ApproxEqualVec3(in GdVec3 a, in GdVec3 b)
        {
            return MathF.Abs(a.X - b.X) <= 1e-5f &&
                   MathF.Abs(a.Y - b.Y) <= 1e-5f &&
                   MathF.Abs(a.Z - b.Z) <= 1e-5f;
        }

        private static bool ApproxEqualQuat(in GdQuat a, in GdQuat b)
        {
            float dot = MathF.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W);
            return dot >= 1f - 1e-5f;
        }

        // =====================================================================
        // Self-contained Math & Attachment helpers (matching VRF MathUtils / ModelSceneNode)
        // =====================================================================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Saturate(float x) => Math.Clamp(x, 0f, 1f);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Lerp(float from, float to, float amount) => ((to - from) * amount) + from;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Remap(float x, float inputMin, float inputMax)
        {
            if (inputMin == inputMax) return inputMax;
            return (x - inputMin) / (inputMax - inputMin);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float RemapValClamped(float x, float inputMin, float inputMax, float outputMin, float outputMax)
        {
            if (inputMin == inputMax)
            {
                return x >= inputMax ? outputMax : outputMin;
            }
            return Lerp(outputMin, outputMax, Saturate(Remap(x, inputMin, inputMax)));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Wrap(float x, float lowBounds, float highBounds)
        {
            float range = highBounds - lowBounds;
            return x - range * MathF.Floor((x - lowBounds) / range);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float SafeAcos(float cosine) => MathF.Acos(Math.Clamp(cosine, -1f, 1f));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float AngleBetween(SysVec3 a, SysVec3 b) => SafeAcos(SysVec3.Dot(a, b));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SysVec3 SafeNormalize(SysVec3 value)
        {
            float lenSq = value.LengthSquared();
            return lenSq == 0f ? SysVec3.Zero : value / MathF.Sqrt(lenSq);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SysVec3 SafeNormalize(SysVec3 value, SysVec3 fallback, float minimumLengthSquared = 0f)
        {
            float lenSq = value.LengthSquared();
            return lenSq > minimumLengthSquared ? value / MathF.Sqrt(lenSq) : fallback;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SysVec3 ProjectOntoPlane(SysVec3 value, SysVec3 unitNormal)
        {
            return value - (unitNormal * SysVec3.Dot(value, unitNormal));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SysMat4 CreateBasis(SysVec3 x, SysVec3 y, SysVec3 z) => new(
            x.X, x.Y, x.Z, 0f,
            y.X, y.Y, y.Z, 0f,
            z.X, z.Y, z.Z, 0f,
            0f, 0f, 0f, 1f);

        private static float DotSpan(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        {
            float sum = 0f;
            int count = Math.Min(a.Length, b.Length);
            for (int i = 0; i < count; i++)
            {
                sum += a[i] * b[i];
            }
            return sum;
        }

        private static int[] GetDescendants(Bone bone)
        {
            var collected = new List<int>();
            var stack = new Stack<Bone>(bone.Children);
            while (stack.TryPop(out var child))
            {
                collected.Add(child.Index);
                foreach (var grandchild in child.Children)
                {
                    stack.Push(grandchild);
                }
            }

            return [.. collected.Order()];
        }

        private static SysMat4 GetAttachmentLocalTransform(Attachment attachment, Skeleton skeleton, SysMat4[] pose)
        {
            var transform = SysMat4.Identity;

            for (int i = 0; i < attachment.Length; i++)
            {
                var influence = attachment[i];
                int boneIndex = skeleton.GetBoneIndex(influence.Name);
                if (boneIndex != -1)
                {
                    var boneTransform = pose[boneIndex];
                    var influenceTransform =
                        SysMat4.CreateFromQuaternion(influence.Rotation) *
                        SysMat4.CreateTranslation(influence.Offset);
                    transform *= SysMat4.Lerp(SysMat4.Identity, influenceTransform * boneTransform, influence.Weight);
                }
            }

            if (attachment.IgnoreRotation)
            {
                float scale = transform.M22;
                var translation = transform.Translation;
                transform = SysMat4.CreateScale(scale) * SysMat4.CreateTranslation(translation);
            }

            return transform;
        }

        // =====================================================================
        // Ported VRF 21.1 BoneConstraintSolver (all 11 Source 2 constraint types)
        // =====================================================================

        private sealed class BoneConstraintSolverCore
        {
            private readonly Constraint[] _constraints;
            public int[] ConstrainedBoneIndices { get; }
            public bool WritesMorphs { get; }
            public int Count => _constraints.Length;

            public BoneConstraintSolverCore(
                BoneConstraintBase[] definitions,
                Skeleton skeleton,
                FlexController[] flexControllers,
                IReadOnlyDictionary<string, Attachment> attachments)
            {
                if (definitions.Length == 0)
                {
                    _constraints = [];
                    ConstrainedBoneIndices = [];
                    return;
                }

                var context = new BuildContext(skeleton, flexControllers, attachments);
                var built = new List<Constraint>();
                var boneSet = new HashSet<int>();

                foreach (var definition in definitions)
                {
                    Constraint constraint = definition switch
                    {
                        TiltTwistConstraint c => TiltTwist.Create(c, context),
                        TwistConstraint c => Twist.Create(c, context),
                        AimConstraint c => Aim.Create(c, context),
                        OrientConstraint c => Orient.Create(c, context),
                        PointConstraint c => Point.Create(c, context),
                        ParentConstraint c => Parent.Create(c, context),
                        MorphConstraint c => Morph.Create(c, context),
                        PoseSpaceBoneConstraint c => PoseSpaceBone.Create(c, context),
                        PoseSpaceMorphConstraint c => PoseSpaceMorph.Create(c, context),
                        DotToMorphConstraint c => DotToMorph.Create(c, context),
                        RbfConstraint c => Rbf.Create(c, context),
                        _ => null,
                    };

                    if (constraint != null)
                    {
                        built.Add(constraint);
                        WritesMorphs |= constraint.WritesMorphs;
                        constraint.CollectConstrainedBones(boneSet);
                    }
                }

                _constraints = [.. built];
                ConstrainedBoneIndices = [.. boneSet];
            }

            public void Evaluate(SysMat4[] pose, Span<float> morphs)
            {
                foreach (var constraint in _constraints)
                {
                    constraint.Evaluate(pose, morphs);
                }
            }

            private sealed class BuildContext(
                Skeleton skeleton,
                FlexController[] flexControllers,
                IReadOnlyDictionary<string, Attachment> attachments)
            {
                public Skeleton Skeleton { get; } = skeleton;

                private readonly Dictionary<uint, Attachment> _attachmentsByHash = attachments.Values
                    .DistinctBy(a => StringToken.Get(a.Name))
                    .ToDictionary(a => StringToken.Get(a.Name));

                private readonly Dictionary<Bone, int[]> _descendants = [];

                public int ParentOf(int bone) => bone >= 0 && Skeleton.Bones[bone].Parent is { } parent ? parent.Index : -1;

                public int FindMorph(string name) => Array.FindIndex(flexControllers, c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

                public FlexController GetMorph(int index) => index >= 0 ? flexControllers[index] : null;

                public Attachment FindAttachment(uint hash) => _attachmentsByHash.GetValueOrDefault(hash);

                public Attachment FindAttachment(string name) => attachments.GetValueOrDefault(name);

                public int[] DescendantsOf(int boneIndex)
                {
                    var bone = Skeleton.Bones[boneIndex];
                    if (!_descendants.TryGetValue(bone, out var list))
                    {
                        list = GetDescendants(bone);
                        _descendants[bone] = list;
                    }
                    return list;
                }

                public Constrained ResolveConstrainedBone(ConstrainedBone constrained)
                {
                    int bone = Skeleton.GetBoneIndex(constrained.BoneHash);
                    return bone < 0 ? null : new Constrained(bone, ParentOf(bone), constrained.Weight, DescendantsOf(bone), constrained);
                }

                public Constrained[] ResolveBones(uint[] hashes)
                    => ResolveConstrainedBones([.. hashes.Select(hash => new ConstrainedBone { BoneHash = hash, Weight = 1f })]);

                public Constrained[] ResolveConstrainedBones(BaseConstraint constraint)
                    => ResolveConstrainedBones(constraint.ConstrainedBones);

                private Constrained[] ResolveConstrainedBones(ConstrainedBone[] definitions)
                {
                    var constrainedBones = new Constrained[definitions.Length];
                    for (int i = 0; i < constrainedBones.Length; i++)
                    {
                        if (ResolveConstrainedBone(definitions[i]) is not { } constrained)
                        {
                            return null;
                        }
                        constrainedBones[i] = constrained;
                    }
                    return constrainedBones;
                }

                public Target ResolveTarget(ConstraintTarget target)
                {
                    var offset = new FrameBone(target.PositionOffset, 1f, target.Offset);

                    if (!target.IsAttachment)
                    {
                        int bone = Skeleton.GetBoneIndex(target.BoneHash);
                        return bone < 0 ? null : new Target(IsApproxIdentity(offset) ? TargetKind.Bone : TargetKind.BoneWithOffset, bone, offset, null, target);
                    }

                    if (FindAttachment(target.BoneHash) is not { Length: > 0 } attachment)
                    {
                        return null;
                    }

                    if (attachment.Length > 1)
                    {
                        return new Target(TargetKind.Attachment, -1, offset, attachment, target);
                    }

                    var influence = attachment[0];
                    offset = new FrameBone(influence.Offset, 1f, influence.Rotation).Concat(offset);

                    int influenceBone = Skeleton.GetBoneIndex(influence.Name);
                    if (influenceBone < 0)
                    {
                        return new Target(TargetKind.Fixed, -1, offset, null, target);
                    }

                    return new Target(IsApproxIdentity(offset) ? TargetKind.Bone : TargetKind.BoneWithOffset, influenceBone, offset, null, target);
                }

                public Target[] ResolveTargets(BaseConstraint constraint)
                {
                    var targets = new Target[constraint.Targets.Length];
                    for (int i = 0; i < targets.Length; i++)
                    {
                        if (ResolveTarget(constraint.Targets[i]) is not { } target)
                        {
                            return null;
                        }
                        targets[i] = target;
                    }
                    return targets;
                }
            }

            private sealed record Constrained(int Bone, int Parent, float Weight, int[] Descendants, ConstrainedBone Definition);

            private enum TargetKind
            {
                BoneWithOffset,
                Bone,
                Attachment,
                Fixed,
            }

            private sealed record Target(TargetKind Kind, int Bone, FrameBone Offset, Attachment Attachment, ConstraintTarget Definition);

            private abstract class Constraint
            {
                public virtual bool WritesMorphs => false;
                public abstract void Evaluate(SysMat4[] pose, Span<float> morphs);
                public virtual void CollectConstrainedBones(HashSet<int> bones) { }
            }

            private abstract class TargetConstraint(Constrained[] constrainedBones, Target[] targets, Skeleton skeleton) : Constraint
            {
                protected Constrained[] ConstrainedBones { get; } = constrainedBones;
                protected Target[] Targets { get; } = targets;

                public override void CollectConstrainedBones(HashSet<int> bones)
                {
                    foreach (var c in ConstrainedBones) bones.Add(c.Bone);
                }

                protected FrameBone GetTarget(Target target, SysMat4[] pose) => target.Kind switch
                {
                    TargetKind.BoneWithOffset => FrameBone.FromMatrix(pose[target.Bone]).Concat(target.Offset),
                    TargetKind.Bone => FrameBone.FromMatrix(pose[target.Bone]),
                    TargetKind.Attachment => FrameBone.FromMatrix(GetAttachmentLocalTransform(target.Attachment!, skeleton, pose)).Concat(target.Offset),
                    _ => target.Offset,
                };

                protected (SysVec3 Position, float TotalWeight) BlendPositions(int count, SysMat4[] pose)
                {
                    var position = SysVec3.Zero;
                    var totalWeight = 0f;

                    for (int i = 0; i < count; i++)
                    {
                        var target = Targets[i];
                        var targetPosition = target.Kind switch
                        {
                            TargetKind.BoneWithOffset => FrameBone.FromMatrix(pose[target.Bone]).Concat(target.Offset).Position,
                            TargetKind.Bone => pose[target.Bone].Translation,
                            TargetKind.Attachment => GetAttachmentLocalTransform(target.Attachment!, skeleton, pose).Translation + target.Definition.PositionOffset,
                            _ => target.Offset.Position,
                        };

                        position += targetPosition * target.Definition.Weight;
                        totalWeight += target.Definition.Weight;
                    }

                    if (totalWeight != 0f)
                    {
                        position /= totalWeight;
                    }

                    return (position, totalWeight);
                }

                protected (SysQuat Rotation, float TotalWeight) BlendRotations(SysMat4[] pose, Span<SysQuat> rotations, Span<float> weights)
                {
                    var totalWeight = 0f;

                    for (int i = 0; i < Targets.Length; i++)
                    {
                        rotations[i] = GetTarget(Targets[i], pose).Angle;
                        weights[i] = Targets[i].Definition.Weight;
                        totalWeight += weights[i];
                    }

                    return (WeightedAverage(rotations, weights), totalWeight);
                }
            }

            private sealed class TiltTwist(Constrained constrained, int targetBone, int targetParent, SysQuat inverseOffset, int targetAxis, int constrainedAxis) : Constraint
            {
                private bool _hasPreviousAngle;
                private float _previousAngle;

                public override void CollectConstrainedBones(HashSet<int> bones) => bones.Add(constrained.Bone);

                public static TiltTwist Create(TiltTwistConstraint definition, BuildContext context)
                {
                    if (definition.ConstrainedBones.Length == 0 || definition.Targets.Length == 0 ||
                        context.ResolveConstrainedBone(definition.ConstrainedBones[0]) is not { } constrained)
                    {
                        return null;
                    }

                    int targetBone = context.Skeleton.GetBoneIndex(definition.Targets[0].BoneHash);
                    if (targetBone < 0)
                    {
                        return null;
                    }

                    return new TiltTwist(
                        constrained,
                        targetBone,
                        context.ParentOf(targetBone),
                        SysQuat.Inverse(definition.Targets[0].Offset),
                        Math.Clamp(definition.TargetAxis, 0, 2),
                        Math.Clamp(definition.ConstrainedAxis, 0, 2));
                }

                public override void Evaluate(SysMat4[] pose, Span<float> morphs)
                {
                    var rotation = FrameBone.FromMatrix(pose[targetBone]).Angle;
                    if (targetParent >= 0)
                    {
                        rotation = SysQuat.Inverse(FrameBone.FromMatrix(pose[targetParent]).Angle) * rotation;
                    }

                    float angle = TwistAngle(inverseOffset * rotation, targetAxis) * constrained.Weight;

                    if (_hasPreviousAngle && _previousAngle != angle)
                    {
                        float previousWrapped = Wrap(_previousAngle, -MathF.Tau, MathF.Tau);
                        float wrapped = Wrap(angle, -MathF.Tau, MathF.Tau);
                        float delta = wrapped - previousWrapped;

                        if (MathF.Abs(delta) >= MathF.PI)
                        {
                            delta += wrapped > previousWrapped ? -MathF.Tau : MathF.Tau;
                        }

                        angle = _previousAngle + delta;
                    }

                    _hasPreviousAngle = true;
                    _previousAngle = angle;

                    var axis = SysVec3.Zero;
                    axis[constrainedAxis] = 1f;
                    SetLocalRotation(pose, constrained, SysQuat.CreateFromAxisAngle(axis, angle));
                }

                private static float TwistAngle(SysQuat rotation, int axis)
                {
                    rotation = rotation == default ? SysQuat.Identity : SysQuat.Normalize(rotation);

                    Span<SysVec3> basis =
                    [
                        SysVec3.Transform(SysVec3.UnitX, rotation),
                        SysVec3.Transform(SysVec3.UnitY, rotation),
                        SysVec3.Transform(SysVec3.UnitZ, rotation)
                    ];
                    int axis1 = (axis + 1) % 3;
                    int axis2 = (axis + 2) % 3;
                    const float Epsilon = 0.0001f;

                    float diagonal = Math.Clamp(MathF.Floor(basis[axis][axis] * 1e7f + 0.5f) / 1e7f, -1f, 1f);
                    float tilt = diagonal < 0f ? MathF.Acos(diagonal + 1f) : MathF.Acos(1f - diagonal);

                    if (MathF.Abs(tilt - MathF.PI / 2f) > Epsilon)
                    {
                        float a = basis[axis][axis1];
                        float b = basis[axis][axis2];
                        float invLength = 1f / MathF.Max(MathF.Sqrt(a * a + b * b), 1.17549435e-38f);
                        a *= invLength;
                        b *= invLength;

                        float m1 = basis[axis1][axis];
                        float m2 = basis[axis2][axis];
                        return MathF.Atan2(m2 * a - m1 * b, -m1 * a - m2 * b);
                    }

                    float diagonal1 = basis[axis1][axis1];
                    if (MathF.Abs(diagonal1 - 1f) <= Epsilon)
                    {
                        return 0f;
                    }

                    return MathF.Atan2(basis[axis1][axis2], diagonal1);
                }
            }

            private sealed class Twist(TwistConstraint definition, Constrained[] constrainedBones, int childBone, int parentBone, int grandparentBone) : Constraint
            {
                private readonly SysQuat[] _results = new SysQuat[constrainedBones.Length];
                private readonly SysQuat _baseInverse = SysQuat.Inverse(definition.Inverse ? definition.ParentBindRotation : definition.ChildBindRotation);

                public override void CollectConstrainedBones(HashSet<int> bones)
                {
                    foreach (var c in constrainedBones) bones.Add(c.Bone);
                }

                public static Twist Create(TwistConstraint definition, BuildContext context)
                {
                    if (context.ResolveConstrainedBones(definition) is not { } constrainedBones)
                    {
                        return null;
                    }

                    if (definition.Targets.Length < 2)
                    {
                        return new Twist(definition, constrainedBones, -1, -1, -1);
                    }

                    int childBone = context.Skeleton.GetBoneIndex(definition.Targets[1].BoneHash);
                    int parentBone = context.ParentOf(childBone);
                    return childBone < 0 || parentBone < 0
                        ? null
                        : new Twist(definition, constrainedBones, childBone, parentBone, context.ParentOf(parentBone));
                }

                public override void Evaluate(SysMat4[] pose, Span<float> morphs)
                {
                    var parentRotation = SysQuat.Identity;
                    var child = FrameBone.Identity;

                    if (childBone >= 0)
                    {
                        var parent = FrameBone.FromMatrix(pose[parentBone]);
                        child = parent.Inverse().Concat(FrameBone.FromMatrix(pose[childBone]));
                        parentRotation = grandparentBone >= 0
                            ? FrameBone.FromMatrix(pose[grandparentBone]).Inverse().Concat(parent).Angle
                            : parent.Angle;
                    }

                    Solve(parentRotation, child, _baseInverse);

                    for (int i = 0; i < constrainedBones.Length; i++)
                    {
                        SetLocalRotation(pose, constrainedBones[i], _results[i]);
                    }
                }

                private void Solve(SysQuat parentRotation, FrameBone child, SysQuat baseInverse)
                {
                    for (int i = 0; i < constrainedBones.Length; i++)
                    {
                        _results[i] = constrainedBones[i].Definition.BaseOrientation;
                    }

                    var up = definition.UpVector;
                    var childDirection = child.Position;
                    if (childDirection.LengthSquared() < 1.42109e-12f)
                    {
                        return;
                    }

                    childDirection = SysVec3.Normalize(childDirection);

                    SysVec3 rotatedUp, twistAxis;
                    if (definition.Inverse)
                    {
                        var rotation = baseInverse * Align(baseInverse, parentRotation);
                        rotatedUp = SysVec3.Transform(up, rotation);
                        twistAxis = SysVec3.Transform(childDirection, rotation);
                    }
                    else
                    {
                        var rotation = baseInverse * Align(baseInverse, child.Angle);
                        rotatedUp = SysVec3.Transform(up, rotation);
                        twistAxis = SysVec3.Transform(childDirection, baseInverse);
                    }

                    const float ParallelEpsilon = 1.19209e-6f;
                    if (1f - MathF.Abs(SysVec3.Dot(up, twistAxis)) < ParallelEpsilon ||
                        1f - MathF.Abs(SysVec3.Dot(rotatedUp, twistAxis)) < ParallelEpsilon)
                    {
                        return;
                    }

                    var from = SysVec3.Normalize(ProjectOntoPlane(up, twistAxis));
                    var to = SysVec3.Normalize(ProjectOntoPlane(rotatedUp, twistAxis));
                    float cosine = SysVec3.Dot(from, to);

                    if (MathF.Abs(MathF.Abs(cosine) - 1f) <= 0.001f)
                    {
                        return;
                    }

                    float angle = SafeAcos(cosine);
                    if (SysVec3.Dot(SysVec3.Normalize(SysVec3.Cross(from, to)), twistAxis) < 0f)
                    {
                        angle = -angle;
                    }

                    var twist = SysQuat.CreateFromAxisAngle(childDirection, angle);

                    for (int i = 0; i < constrainedBones.Length; i++)
                    {
                        float weight = constrainedBones[i].Weight;
                        var scaled = SysQuat.Slerp(SysQuat.Identity, twist, definition.Inverse ? weight - 1f : weight);
                        _results[i] = scaled * Align(scaled, constrainedBones[i].Definition.BaseOrientation);
                    }
                }
            }

            private sealed class Aim(AimConstraint definition, Constrained[] constrainedBones, Target[] targets, Skeleton skeleton)
                : TargetConstraint(constrainedBones, targets, skeleton)
            {
                public static Aim Create(AimConstraint definition, BuildContext context)
                    => context.ResolveConstrainedBones(definition) is { } constrainedBones && context.ResolveTargets(definition) is { } targets
                        ? new Aim(definition, constrainedBones, targets, context.Skeleton)
                        : null;

                public override void Evaluate(SysMat4[] pose, Span<float> morphs)
                {
                    var upTarget = FrameBone.Identity;
                    int upType = definition.UpType;

                    if (upType is 0 or 2)
                    {
                        if (Targets.Length <= 1) return;
                        upTarget = GetTarget(Targets[^1], pose);
                    }
                    else if (Targets.Length == 0)
                    {
                        return;
                    }

                    var (targetPosition, totalWeight) = BlendPositions(Targets.Length > 1 ? Targets.Length - 1 : Targets.Length, pose);

                    foreach (var constrained in ConstrainedBones)
                    {
                        float weight = totalWeight * constrained.Weight;
                        if (MathF.Abs(weight) <= 0.0001f) continue;

                        var current = FrameBone.FromMatrix(pose[constrained.Bone]);
                        var parent = constrained.Parent >= 0 ? FrameBone.FromMatrix(pose[constrained.Parent]) : FrameBone.Identity;
                        var parentInverse = parent.Inverse();

                        var localPosition = parentInverse.TransformPoint(current.Position);

                        var up = upType switch
                        {
                            1 => definition.UpVector,
                            3 => SysVec3.Transform(definition.UpVector, parent.Angle),
                            2 => SafeNormalize(upTarget.Position - parent.TransformPoint(localPosition)),
                            _ => SysVec3.Transform(definition.UpVector, upTarget.Angle),
                        };

                        up = SysVec3.Transform(up, SysQuat.Conjugate(parent.Angle));
                        var direction = SafeNormalize(parentInverse.TransformPoint(targetPosition) - localPosition);

                        var look = LookRotation(direction, up);
                        var local = look * Align(look, definition.AimOffset);
                        var rotation = parent.Angle * Align(parent.Angle, local);

                        var previous = current;
                        current.Angle = MathF.Abs(weight - 1f) <= 0.0001f ? rotation : SysQuat.Slerp(current.Angle, rotation, weight);
                        WriteBack(pose, constrained, current, previous);
                    }
                }

                private static SysQuat LookRotation(SysVec3 forward, SysVec3 up)
                {
                    forward = SafeNormalize(forward);
                    up = SafeNormalize(ProjectOntoPlane(up, forward));
                    var side = SafeNormalize(SysVec3.Cross(forward, up));

                    var basis = CreateBasis(side, forward, up);
                    return SysQuat.CreateFromRotationMatrix(basis);
                }
            }

            private sealed class Orient(Constrained[] constrainedBones, Target[] targets, Skeleton skeleton)
                : TargetConstraint(constrainedBones, targets, skeleton)
            {
                public static Orient Create(OrientConstraint definition, BuildContext context)
                    => context.ResolveConstrainedBones(definition) is { } constrainedBones && context.ResolveTargets(definition) is { } targets
                        ? new Orient(constrainedBones, targets, context.Skeleton)
                        : null;

                public override void Evaluate(SysMat4[] pose, Span<float> morphs)
                {
                    Span<SysQuat> rotations = stackalloc SysQuat[Targets.Length];
                    Span<float> weights = stackalloc float[Targets.Length];
                    var (rotation, totalWeight) = BlendRotations(pose, rotations, weights);

                    foreach (var constrained in ConstrainedBones)
                    {
                        float weight = totalWeight * constrained.Weight;
                        if (MathF.Abs(weight) <= 0.0001f) continue;

                        var current = FrameBone.FromMatrix(pose[constrained.Bone]);
                        var previous = current;
                        current.Angle = MathF.Abs(weight - 1f) <= 0.0001f ? rotation : SysQuat.Slerp(current.Angle, rotation, weight);
                        WriteBack(pose, constrained, current, previous);
                    }
                }
            }

            private sealed class Point(Constrained[] constrainedBones, Target[] targets, Skeleton skeleton)
                : TargetConstraint(constrainedBones, targets, skeleton)
            {
                public static Point Create(PointConstraint definition, BuildContext context)
                    => context.ResolveConstrainedBones(definition) is { } constrainedBones && context.ResolveTargets(definition) is { } targets
                        ? new Point(constrainedBones, targets, context.Skeleton)
                        : null;

                public override void Evaluate(SysMat4[] pose, Span<float> morphs)
                {
                    var (position, totalWeight) = BlendPositions(Targets.Length, pose);

                    foreach (var constrained in ConstrainedBones)
                    {
                        float weight = totalWeight * constrained.Weight;
                        if (MathF.Abs(weight) <= 0.0001f) continue;

                        var current = FrameBone.FromMatrix(pose[constrained.Bone]);
                        var previous = current;
                        current.Position = MathF.Abs(weight - 1f) <= 0.0001f ? position : SysVec3.Lerp(current.Position, position, weight);
                        WriteBack(pose, constrained, current, previous);
                    }
                }
            }

            private sealed class Parent(Constrained[] constrainedBones, Target[] targets, Skeleton skeleton)
                : TargetConstraint(constrainedBones, targets, skeleton)
            {
                public static Parent Create(ParentConstraint definition, BuildContext context)
                    => context.ResolveConstrainedBones(definition) is { } constrainedBones && context.ResolveTargets(definition) is { } targets
                        ? new Parent(constrainedBones, targets, context.Skeleton)
                        : null;

                public override void Evaluate(SysMat4[] pose, Span<float> morphs)
                {
                    Span<SysQuat> rotations = stackalloc SysQuat[Targets.Length];
                    Span<float> weights = stackalloc float[Targets.Length];
                    var position = SysVec4.Zero;
                    float totalWeight = 0f;

                    for (int i = 0; i < Targets.Length; i++)
                    {
                        var target = GetTarget(Targets[i], pose);
                        rotations[i] = target.Angle;
                        weights[i] = Targets[i].Definition.Weight;
                        position += new SysVec4(target.Position, target.Scale) * weights[i];
                        totalWeight += weights[i];
                    }

                    if (totalWeight > 0f)
                    {
                        position /= totalWeight;
                    }

                    var blended = new FrameBone(position, WeightedAverage(rotations, weights));

                    foreach (var constrained in ConstrainedBones)
                    {
                        float weight = totalWeight * constrained.Weight;
                        if (MathF.Abs(weight) <= 0.0001f) continue;

                        if (MathF.Abs(weight - 1f) <= 0.0001f)
                        {
                            WriteBack(pose, constrained, blended);
                            continue;
                        }

                        var current = FrameBone.FromMatrix(pose[constrained.Bone]);
                        var previous = current;
                        float t = constrained.Weight;
                        current.Position = SysVec3.Lerp(current.Position, blended.Position, t);
                        current.Scale = Lerp(current.Scale, blended.Scale, t);
                        current.Angle = SysQuat.Normalize(SysQuat.Lerp(current.Angle, Align(current.Angle, blended.Angle), t));
                        WriteBack(pose, constrained, current, previous);
                    }
                }
            }

            private sealed class Morph(MorphConstraint definition, Constrained[] constrainedBones, int morph, float morphMin, float morphMax) : Constraint
            {
                public override void CollectConstrainedBones(HashSet<int> bones)
                {
                    foreach (var c in constrainedBones) bones.Add(c.Bone);
                }

                public static Morph Create(MorphConstraint definition, BuildContext context)
                {
                    if (context.ResolveConstrainedBones(definition) is not { } constrainedBones || context.ResolveTargets(definition) is null)
                    {
                        return null;
                    }

                    int morph = context.FindMorph(definition.TargetMorph);
                    return context.GetMorph(morph) is { } controller
                        ? new Morph(definition, constrainedBones, morph, controller.Min, controller.Max)
                        : null;
                }

                public override void Evaluate(SysMat4[] pose, Span<float> morphs)
                {
                    if (morph >= morphs.Length) return;

                    float value = morphs[morph];
                    float amount = morphMin == morphMax
                        ? (value - morphMax >= 0f ? definition.Max : definition.Min)
                        : definition.Min + (definition.Max - definition.Min) * (value - morphMin) / (morphMax - morphMin);

                    int channel = definition.Channel;
                    if (channel is < 0 or > 5) return;

                    foreach (var constrained in constrainedBones)
                    {
                        float weight = constrained.Weight;
                        if (MathF.Abs(weight) <= 0.0001f) continue;

                        var parent = constrained.Parent >= 0 ? FrameBone.FromMatrix(pose[constrained.Parent]) : FrameBone.Identity;
                        var local = parent.Inverse().Concat(FrameBone.FromMatrix(pose[constrained.Bone]));

                        if (channel < 3)
                        {
                            var position = constrained.Definition.BasePosition;
                            position[channel] += amount * weight;
                            local.Position = position;
                        }
                        else
                        {
                            var axis = SysVec3.Zero;
                            axis[channel - 3] = 1f;
                            var baseRotation = constrained.Definition.BaseOrientation;
                            var rotated = SysQuat.CreateFromAxisAngle(axis, float.DegreesToRadians(amount));
                            rotated = baseRotation * Align(baseRotation, rotated);
                            local.Angle = MathF.Abs(weight - 1f) <= 0.0001f ? rotated : SysQuat.Slerp(baseRotation, rotated, weight);
                        }

                        WriteBack(pose, constrained, parent.Concat(local));
                    }
                }
            }

            private sealed class PoseSpaceBone(Constrained[] constrainedBones, int bone, int parent, SysVec3 offset, RadialBasis basis, PoseSpaceBoneConstraint definition) : Constraint
            {
                private readonly float[] _weights = new float[basis.SampleCount];

                public override void CollectConstrainedBones(HashSet<int> bones)
                {
                    foreach (var c in constrainedBones) bones.Add(c.Bone);
                }

                public static PoseSpaceBone Create(PoseSpaceBoneConstraint definition, BuildContext context)
                {
                    if (definition.Targets.Length != 1 || !definition.Targets[0].IsAttachment ||
                        definition.ConstrainedBones.Length == 0 || definition.Inputs.Length == 0 ||
                        context.ResolveConstrainedBones(definition) is not { } constrainedBones ||
                        context.ResolveTarget(definition.Targets[0]) is not { Kind: TargetKind.Bone or TargetKind.BoneWithOffset } target ||
                        definition.Inputs.Any(input => input.Outputs.Length < constrainedBones.Length))
                    {
                        return null;
                    }

                    int parent = context.ParentOf(target.Bone);
                    if (parent < 0) return null;

                    if (RadialBasis.Create(definition.Inputs.Select(i => i.Value), definition.Kernel, definition.Falloff) is not { } basis ||
                        basis.SampleCount != definition.Inputs.Length)
                    {
                        return null;
                    }

                    return new PoseSpaceBone(constrainedBones, target.Bone, parent, target.Offset.Position, basis, definition);
                }

                public override void Evaluate(SysMat4[] pose, Span<float> morphs)
                {
                    var local = FrameBone.FromMatrix(pose[parent]).Inverse().Concat(FrameBone.FromMatrix(pose[bone]));
                    basis.Evaluate(local.TransformPoint(offset), _weights);

                    var inputs = definition.Inputs;
                    Span<SysQuat> rotations = stackalloc SysQuat[inputs.Length];

                    for (int s = 0; s < constrainedBones.Length; s++)
                    {
                        var position = SysVec3.Zero;
                        for (int i = 0; i < inputs.Length; i++)
                        {
                            position += inputs[i].Outputs[s].Position * _weights[i];
                            rotations[i] = inputs[i].Outputs[s].Rotation;
                        }

                        var newLocal = new FrameBone(position, 1f, WeightedAverage(rotations, _weights));
                        var constrained = constrainedBones[s];
                        WriteBack(pose, constrained, constrained.Parent >= 0 ? FrameBone.FromMatrix(pose[constrained.Parent]).Concat(newLocal) : newLocal);
                    }
                }
            }

            private sealed class PoseSpaceMorph(PoseSpaceMorphConstraint definition, int bone, int parent, SysVec3 offset, int[] outputs, RadialBasis basis) : Constraint
            {
                private readonly float[] _weights = new float[basis.SampleCount];

                public override bool WritesMorphs => true;

                public static PoseSpaceMorph Create(PoseSpaceMorphConstraint definition, BuildContext context)
                {
                    int bone = context.Skeleton.GetBoneIndex(definition.BoneName);
                    int parent = context.ParentOf(bone);
                    int[] outputs = definition.OutputMorphs.Select(context.FindMorph).ToArray();

                    if (definition.Inputs.Length == 0 || outputs.Length == 0 || bone <= 0 || parent < 0 ||
                        context.FindAttachment(definition.AttachmentName) is not { Length: 1 } attachment ||
                        outputs.Any(o => o < 0) ||
                        RadialBasis.Create(definition.Inputs.Select(i => i.Value), definition.Kernel, definition.Falloff) is not { } basis ||
                        basis.SampleCount != definition.Inputs.Length)
                    {
                        return null;
                    }

                    return new PoseSpaceMorph(definition, bone, parent, attachment[0].Offset, outputs, basis);
                }

                public override void Evaluate(SysMat4[] pose, Span<float> morphs)
                {
                    var local = FrameBone.FromMatrix(pose[parent]).Inverse().Concat(FrameBone.FromMatrix(pose[bone]));
                    basis.Evaluate(local.TransformPoint(offset), _weights);

                    var inputs = definition.Inputs;
                    for (int j = 0; j < outputs.Length; j++)
                    {
                        if (outputs[j] >= morphs.Length) continue;

                        float value = 0f;
                        for (int i = 0; i < inputs.Length; i++)
                        {
                            value += j < inputs[i].Weights.Length ? inputs[i].Weights[j] * _weights[i] : 0f;
                        }

                        morphs[outputs[j]] = definition.Clamp ? Saturate(value) : value;
                    }
                }
            }

            private sealed class DotToMorph(DotToMorphConstraint definition, int bone, int target, int morph) : Constraint
            {
                public override bool WritesMorphs => true;

                public static DotToMorph Create(DotToMorphConstraint definition, BuildContext context)
                {
                    int bone = context.Skeleton.GetBoneIndex(definition.BoneName);
                    int target = context.Skeleton.GetBoneIndex(definition.TargetBoneName);
                    int morph = context.FindMorph(definition.MorphChannelName);
                    return bone >= 0 && target >= 0 && morph >= 0 ? new DotToMorph(definition, bone, target, morph) : null;
                }

                public override void Evaluate(SysMat4[] pose, Span<float> morphs)
                {
                    if (morph >= morphs.Length) return;

                    var boneRotation = FrameBone.FromMatrix(pose[bone]).Angle;
                    var direction = SafeNormalize(pose[target].Translation - pose[bone].Translation);

                    float angle = float.RadiansToDegrees(AngleBetween(direction, SysVec3.Transform(SysVec3.UnitZ, boneRotation)));
                    float value = RemapValClamped(angle, definition.InputMin, definition.InputMax, definition.OutputMin, definition.OutputMax);

                    value += MathF.Abs(SysVec3.Dot(direction, SysVec3.Transform(SysVec3.UnitY, boneRotation))) * (definition.OutputMin - value);
                    morphs[morph] = value;
                }
            }

            private sealed class Rbf(Constrained[] inputs, Constrained[] outputs, RbfParameters parameters) : Constraint
            {
                private readonly (SysVec3 Position, SysQuat Rotation)[] _inputPose = new (SysVec3, SysQuat)[inputs.Length];
                private readonly float[] _distances = new float[parameters.PoseCount];

                public override void CollectConstrainedBones(HashSet<int> bones)
                {
                    foreach (var c in outputs) bones.Add(c.Bone);
                }

                public static Rbf Create(RbfConstraint definition, BuildContext context)
                {
                    if (definition.InputBoneHashes.Length == 0 || definition.OutputBoneHashes.Length == 0 ||
                        RbfParameters.Read(definition.Parameters) is not { } parameters ||
                        parameters.InputCount != definition.InputBoneHashes.Length ||
                        parameters.OutputCount != definition.OutputBoneHashes.Length)
                    {
                        return null;
                    }

                    return context.ResolveBones(definition.InputBoneHashes) is { } inputs &&
                           context.ResolveBones(definition.OutputBoneHashes) is { } outputs
                        ? new Rbf(inputs, outputs, parameters)
                        : null;
                }

                public override void Evaluate(SysMat4[] pose, Span<float> morphs)
                {
                    for (int i = 0; i < inputs.Length; i++)
                    {
                        var input = inputs[i];
                        var local = FrameBone.FromMatrix(pose[input.Bone]);
                        if (input.Parent >= 0)
                        {
                            local = FrameBone.FromMatrix(pose[input.Parent]).Inverse().Concat(local);
                        }

                        _inputPose[i] = (local.Position, local.Angle);
                    }

                    parameters.Evaluate(_inputPose, _distances);

                    for (int o = 0; o < outputs.Length; o++)
                    {
                        var output = outputs[o];
                        var local = parameters.GetOutput(o, _distances);
                        WriteBack(pose, output, output.Parent >= 0 ? FrameBone.FromMatrix(pose[output.Parent]).Concat(local) : local);
                    }
                }
            }

            private sealed class RbfParameters
            {
                private float _translationThreshold;
                private float _rotationThreshold;
                private int _inputCount;
                private float[][] _translations = [];
                private float[][] _rotations = [];
                private float[] _translationScales = [];
                private float[] _rotationScales = [];
                private float[] _inputScales = [];
                private SysVec3[] _basePositions = [];
                private float[][] _outputWeights = [];

                public int PoseCount { get; private set; }
                public int InputCount => _inputCount;
                public int OutputCount => _basePositions.Length;

                public static RbfParameters Read(byte[] blob)
                {
                    if (blob == null || blob.Length < 0x20) return null;

                    var span = blob.AsSpan();
                    int poses = BinaryPrimitives.ReadInt32LittleEndian(span[0x14..]);
                    int inputs = BinaryPrimitives.ReadInt32LittleEndian(span[0x18..]);
                    int outputs = BinaryPrimitives.ReadInt32LittleEndian(span[0x1C..]);

                    if (poses <= 0 || inputs <= 0 || outputs <= 0 ||
                        0x20L + 4L * ((long)inputs * (7L * poses + 3) + (long)outputs * (3 + 7L * poses)) > blob.Length)
                    {
                        return null;
                    }

                    int offset = 0x20;
                    float[] ReadFloats(int count)
                    {
                        float[] values = MemoryMarshal.Cast<byte, float>(blob.AsSpan(offset, count * 4)).ToArray();
                        offset += count * 4;
                        return values;
                    }

                    var parameters = new RbfParameters
                    {
                        _translationThreshold = BinaryPrimitives.ReadSingleLittleEndian(span[0x04..]),
                        _rotationThreshold = BinaryPrimitives.ReadSingleLittleEndian(span[0x08..]),
                        PoseCount = poses,
                        _inputCount = inputs,
                    };

                    parameters._translations = [.. Enumerable.Range(0, inputs).Select(_ => ReadFloats(3 * poses))];
                    parameters._rotations = [.. Enumerable.Range(0, inputs).Select(_ => ReadFloats(4 * poses))];
                    parameters._translationScales = ReadFloats(inputs);
                    parameters._rotationScales = ReadFloats(inputs);
                    parameters._inputScales = ReadFloats(inputs);
                    parameters._basePositions = [.. Enumerable.Range(0, outputs).Select(_ => ReadFloats(3)).Select(v => new SysVec3(v))];
                    parameters._outputWeights = [.. Enumerable.Range(0, outputs).Select(_ => ReadFloats(7 * poses))];
                    return parameters;
                }

                public void Evaluate(ReadOnlySpan<(SysVec3 Position, SysQuat Rotation)> inputPose, Span<float> distances)
                {
                    distances.Clear();

                    if (PoseCount <= 1)
                    {
                        distances[0] = 1f;
                        return;
                    }

                    for (int i = 0; i < _inputCount; i++)
                    {
                        if (_inputScales[i] == 0f) continue;

                        for (int k = 0; k < PoseCount; k++)
                        {
                            float distance = 0f;

                            if (_translationScales[i] > 0f)
                            {
                                float[] t = _translations[i];
                                float d = SysVec3.Distance(inputPose[i].Position, new SysVec3(t.AsSpan(k * 3)));
                                distance += (d <= _translationThreshold ? 0f : d) / _translationScales[i];
                            }

                            if (_rotationScales[i] > 0f)
                            {
                                float[] r = _rotations[i];
                                float c = SysQuat.Dot(new SysQuat(r[k * 4], r[k * 4 + 1], r[k * 4 + 2], r[k * 4 + 3]), inputPose[i].Rotation);
                                float cosine = 2f * c * c - 1f;
                                float a = SafeAcos(cosine);
                                distance += (a <= _rotationThreshold ? 0f : a) / _rotationScales[i];
                            }

                            distances[k] += distance / _inputScales[i];
                        }
                    }
                }

                public FrameBone GetOutput(int output, ReadOnlySpan<float> distances)
                {
                    Span<float> channels = stackalloc float[7];
                    float[] weights = _outputWeights[output];

                    for (int c = 0; c < 7; c++)
                    {
                        channels[c] = DotSpan(weights.AsSpan(c * PoseCount, PoseCount), distances[..PoseCount]);
                    }

                    var rotation = new SysQuat(channels[3], channels[4], channels[5], channels[6]);
                    return new FrameBone(
                        _basePositions[output] + new SysVec3(channels),
                        1f,
                        rotation == default ? rotation : SysQuat.Normalize(rotation));
                }
            }

            private sealed class RadialBasis
            {
                private readonly SysVec3[] _samples;
                private readonly double[] _distances;
                private readonly double[,] _inverse;
                private readonly RbfKernel _kernel;
                private readonly float _scale;

                private RadialBasis(SysVec3[] samples, double[,] inverse, RbfKernel kernel, float scale)
                {
                    _samples = samples;
                    _inverse = inverse;
                    _distances = new double[samples.Length];
                    _kernel = kernel;
                    _scale = scale;
                }

                public int SampleCount => _samples.Length;

                public static RadialBasis Create(IEnumerable<SysVec3> values, RbfKernel kernel, float falloff)
                {
                    var samples = values.Distinct().ToArray();
                    if (samples.Length == 0) return null;

                    kernel = kernel is >= RbfKernel.Multiquadric and <= RbfKernel.ThinPlate ? kernel : RbfKernel.Multiquadric;
                    float scale = falloff > 0f ? 1f / falloff : 1f;

                    int n = samples.Length;
                    var matrix = new double[n, n];
                    for (int i = 0; i < n; i++)
                    {
                        matrix[i, i] = Kernel(kernel, 0f, scale);
                        for (int j = i + 1; j < n; j++)
                        {
                            matrix[i, j] = matrix[j, i] = Kernel(kernel, SysVec3.Distance(samples[i], samples[j]), scale);
                        }
                    }

                    return Invert(matrix) is { } inverse ? new RadialBasis(samples, inverse, kernel, scale) : null;
                }

                public void Evaluate(SysVec3 position, Span<float> weights)
                {
                    int n = _samples.Length;
                    for (int k = 0; k < n; k++)
                    {
                        _distances[k] = Kernel(_kernel, SysVec3.Distance(position, _samples[k]), _scale);
                    }

                    for (int i = 0; i < n; i++)
                    {
                        double sum = 0.0;
                        for (int k = 0; k < n; k++)
                        {
                            sum += _inverse[i, k] * _distances[k];
                        }
                        weights[i] = (float)sum;
                    }
                }

                private static float Kernel(RbfKernel kernel, float r, float scale) => kernel switch
                {
                    RbfKernel.Multiquadric => MathF.Sqrt(1f + (r * scale) * (r * scale)),
                    RbfKernel.InverseMultiquadric => 1f / MathF.Sqrt(1f + (r * scale) * (r * scale)),
                    RbfKernel.Gaussian => MathF.Exp(-(r * scale) * (r * scale)),
                    RbfKernel.Linear => r,
                    RbfKernel.Cubic => r * r * r,
                    RbfKernel.Quintic => r * r * r * r * r,
                    _ => r * r * MathF.Log(r),
                };

                private static double[,] Invert(double[,] matrix)
                {
                    int n = matrix.GetLength(0);
                    var lu = (double[,])matrix.Clone();
                    int[] pivots = Enumerable.Range(0, n).ToArray();

                    for (int column = 0; column < n; column++)
                    {
                        int pivot = column;
                        for (int row = column + 1; row < n; row++)
                        {
                            if (Math.Abs(lu[row, column]) > Math.Abs(lu[pivot, column]))
                            {
                                pivot = row;
                            }
                        }

                        if (pivot != column)
                        {
                            for (int k = 0; k < n; k++)
                            {
                                (lu[pivot, k], lu[column, k]) = (lu[column, k], lu[pivot, k]);
                            }
                            (pivots[pivot], pivots[column]) = (pivots[column], pivots[pivot]);
                        }

                        if (lu[column, column] == 0.0) return null;

                        for (int row = column + 1; row < n; row++)
                        {
                            lu[row, column] /= lu[column, column];
                            for (int k = column + 1; k < n; k++)
                            {
                                lu[row, k] -= lu[row, column] * lu[column, k];
                            }
                        }
                    }

                    var inverse = new double[n, n];
                    for (int column = 0; column < n; column++)
                    {
                        for (int row = 0; row < n; row++)
                        {
                            double sum = pivots[row] == column ? 1.0 : 0.0;
                            for (int k = 0; k < row; k++)
                            {
                                sum -= lu[row, k] * inverse[k, column];
                            }
                            inverse[row, column] = sum;
                        }

                        for (int row = n - 1; row >= 0; row--)
                        {
                            double sum = inverse[row, column];
                            for (int k = row + 1; k < n; k++)
                            {
                                sum -= lu[row, k] * inverse[k, column];
                            }
                            inverse[row, column] = sum / lu[row, row];
                        }
                    }

                    return inverse;
                }
            }

            private static void SetLocalRotation(SysMat4[] pose, Constrained constrained, SysQuat localRotation)
            {
                var current = FrameBone.FromMatrix(pose[constrained.Bone]);

                if (constrained.Parent < 0)
                {
                    var previous = current;
                    current.Angle = localRotation;
                    WriteBack(pose, constrained, current, previous);
                    return;
                }

                var parent = FrameBone.FromMatrix(pose[constrained.Parent]);
                var local = parent.Inverse().Concat(current);
                local.Angle = localRotation;
                WriteBack(pose, constrained, parent.Concat(local), current);
            }

            private static void WriteBack(SysMat4[] pose, Constrained constrained, FrameBone model, FrameBone? previousBone = null)
            {
                var previous = pose[constrained.Bone];
                var next = model.ToMatrix();
                pose[constrained.Bone] = next;

                if (constrained.Descendants.Length == 0) return;

                var previousDecomposed = previousBone ?? FrameBone.FromMatrix(previous);
                var delta = model.Concat(previousDecomposed.Inverse());
                if (IsApproxIdentity(delta) || !SysMat4.Invert(previous, out var previousInverse))
                {
                    return;
                }

                var deltaMatrix = previousInverse * next;
                foreach (int descendant in constrained.Descendants)
                {
                    pose[descendant] *= deltaMatrix;
                }
            }

            private static bool IsApproxIdentity(FrameBone transform)
            {
                const float PositionEpsilon = 0.0001f;
                const float RotationEpsilon = 0.1f;

                if (MathF.Abs(transform.Position.X) > PositionEpsilon ||
                    MathF.Abs(transform.Position.Y) > PositionEpsilon ||
                    MathF.Abs(transform.Position.Z) > PositionEpsilon ||
                    MathF.Abs(transform.Scale - 1f) > PositionEpsilon)
                {
                    return false;
                }

                var q = transform.Angle;
                bool matches = MathF.Abs(q.X) <= RotationEpsilon && MathF.Abs(q.Y) <= RotationEpsilon && MathF.Abs(q.Z) <= RotationEpsilon;
                return matches && (MathF.Abs(q.W - 1f) <= RotationEpsilon || MathF.Abs(q.W + 1f) <= RotationEpsilon);
            }

            private static SysQuat Align(SysQuat reference, SysQuat q) => SysQuat.Dot(reference, q) < 0f ? -q : q;

            private static SysQuat WeightedAverage(ReadOnlySpan<SysQuat> rotations, ReadOnlySpan<float> weights)
            {
                if (rotations.Length == 1)
                {
                    return rotations[0];
                }

                float totalWeight = 0f;
                foreach (float weight in weights)
                {
                    totalWeight += weight;
                }

                float inverseTotal = totalWeight > 0f ? 1f / totalWeight : 1f;
                var sum = SysVec4.Zero;

                for (int i = 0; i < rotations.Length; i++)
                {
                    var q = Align(rotations[0], rotations[i]);
                    float length = new SysVec3(q.X, q.Y, q.Z).Length();
                    float angleScale = length >= 1e-5f ? MathF.Atan2(length, q.W) / length : 0f;
                    sum += new SysVec4(q.X * angleScale, q.Y * angleScale, q.Z * angleScale, 0.5f * MathF.Log(q.LengthSquared())) * (weights[i] * inverseTotal);
                }

                float angle = new SysVec3(sum.X, sum.Y, sum.Z).Length();
                float magnitude = MathF.Exp(sum.W);
                var (sine, cosine) = MathF.SinCos(angle);
                float sineScale = angle >= 1e-5f ? sine * magnitude / angle : 0f;
                return new SysQuat(sum.X * sineScale, sum.Y * sineScale, sum.Z * sineScale, cosine * magnitude);
            }
        }

        // =====================================================================
        // Ported VRF 21.1 JiggleBoneSolver (FeModel softbody jiggle bones + collisions)
        // =====================================================================

        private sealed class JiggleBoneSolverCore
        {
            private const float MaxTimeStep = 1f / 20f;
            private const float MinimumLengthSquared = 1e-12f;
            private const float ResetDot = -0.9f;

            private readonly Entry[] _jiggleBones;
            private readonly int[][] _descendants;
            private readonly State[] _states;
            private readonly SysMat4[] _animated;
            private readonly SysMat4?[] _inverseAnimated;
            private readonly Collider[] _colliders;
            private readonly Collider[] _worldColliders;

            public int Count => _jiggleBones.Length;
            public IEnumerable<int> JiggleBoneIndices => _jiggleBones.Select(j => j.Bone);

            private readonly record struct Entry(int Bone, int Parent, FeModel.FeJiggleBone Jiggle);
            private readonly record struct Candidate(int SourceIndex, int ParentSourceIndex, int Bone, FeModel.FeJiggleBone Jiggle);
            private readonly record struct Collider(int Bone, SysVec4 Start, SysVec4 End, int CollisionMask);
            private readonly record struct SimulationContext(Collider[] WorldColliders, float DeltaTime);

            private readonly struct Goal
            {
                public Goal(SysMat4 matrix, float sign, float length)
                {
                    Sign = sign;
                    Position = matrix.Translation;
                    Forward = sign * new SysVec3(matrix.M11, matrix.M12, matrix.M13);
                    Left = sign * new SysVec3(matrix.M21, matrix.M22, matrix.M23);
                    Up = sign * new SysVec3(matrix.M31, matrix.M32, matrix.M33);
                    Tip = Position + (length * Forward);
                }

                public float Sign { get; }
                public SysVec3 Position { get; }
                public SysVec3 Forward { get; }
                public SysVec3 Left { get; }
                public SysVec3 Up { get; }
                public SysVec3 Tip { get; }
            }

            private struct State
            {
                public bool IsInitialized;
                public SysVec3 BasePosition;
                public SysVec3 BaseLastPosition;
                public SysVec3 BaseVelocity;
                public SysVec3 BaseAcceleration;
                public SysVec3 TipPosition;
                public SysVec3 TipVelocity;
                public SysVec3 TipAcceleration;
            }

            public static JiggleBoneSolverCore Create(Model model)
            {
                var feModel = model.GetEmbeddedPhys()?.FeModel;
                if (feModel is null || feModel.JiggleBones.Length == 0)
                {
                    return null;
                }

                var skeleton = model.Skeleton;
                int[] nodeBones = GetNodeBones(skeleton, feModel);
                var entries = GetEntries(feModel.JiggleBones, nodeBones);

                if (entries.Length == 0)
                {
                    return null;
                }

                var colliders = HasCollision(entries) ? GetColliders(feModel, nodeBones) : [];
                return new JiggleBoneSolverCore(skeleton, entries, colliders);
            }

            private JiggleBoneSolverCore(Skeleton skeleton, Entry[] entries, Collider[] colliders)
            {
                _jiggleBones = entries;
                _colliders = colliders;
                _worldColliders = new Collider[colliders.Length];
                _states = new State[entries.Length];
                _animated = new SysMat4[entries.Length];
                _inverseAnimated = new SysMat4?[entries.Length];
                _descendants = new int[entries.Length][];

                for (int i = 0; i < entries.Length; i++)
                {
                    _descendants[i] = GetDescendants(skeleton.Bones[entries[i].Bone]);
                }
            }

            public void Reset()
            {
                Array.Clear(_states);
            }

            public void Evaluate(SysMat4[] pose, SysMat4 modelToWorld, float timeStep)
            {
                if (timeStep <= 0f || timeStep > MaxTimeStep)
                {
                    Array.Clear(_states);
                    return;
                }

                if (!SysMat4.Decompose(modelToWorld, out _, out var rotation, out var translation))
                {
                    return;
                }

                var frame = SysMat4.CreateFromQuaternion(rotation);
                frame.Translation = translation;

                if (!SysMat4.Invert(frame, out var inverseFrame))
                {
                    return;
                }

                for (int i = 0; i < _jiggleBones.Length; i++)
                {
                    _animated[i] = pose[_jiggleBones[i].Bone];
                    _inverseAnimated[i] = SysMat4.Invert(_animated[i], out var inverse) ? inverse : null;
                }

                var context = new SimulationContext(_worldColliders, timeStep);

                for (int i = 0; i < _jiggleBones.Length; i++)
                {
                    var (bone, _, jiggle) = _jiggleBones[i];
                    var current = pose[bone];

                    if (!SysMat4.Invert(current, out var inverseCurrent))
                    {
                        continue;
                    }

                    if ((jiggle.BoneFlags & FeJiggleBoneFlags.Collision) != 0)
                    {
                        UpdateWorldColliders(pose, frame);
                    }

                    var goalModel = GetGoal(i, pose);
                    var simulated = Simulate(jiggle, ref _states[i], goalModel * frame, in context);
                    var simulatedModel = simulated * inverseFrame;
                    var delta = inverseCurrent * simulatedModel;

                    pose[bone] = simulatedModel;

                    foreach (int descendant in _descendants[i])
                    {
                        pose[descendant] *= delta;
                    }
                }
            }

            private void UpdateWorldColliders(SysMat4[] pose, SysMat4 modelToWorld)
            {
                for (int i = 0; i < _colliders.Length; i++)
                {
                    var collider = _colliders[i];
                    var colliderToWorld = pose[collider.Bone] * modelToWorld;
                    var start = SysVec3.Transform(new SysVec3(collider.Start.X, collider.Start.Y, collider.Start.Z), colliderToWorld);
                    var end = SysVec3.Transform(new SysVec3(collider.End.X, collider.End.Y, collider.End.Z), colliderToWorld);

                    _worldColliders[i] = collider with
                    {
                        Start = new SysVec4(start, collider.Start.W),
                        End = new SysVec4(end, collider.End.W),
                    };
                }
            }

            private SysMat4 GetGoal(int index, SysMat4[] pose)
            {
                var (bone, parent, jiggle) = _jiggleBones[index];

                if ((jiggle.BoneFlags & FeJiggleBoneFlags.GoalFromSimulatedPose) != 0)
                {
                    return pose[bone];
                }

                if (parent < 0)
                {
                    return _animated[index];
                }

                var inverseAnimatedParent = _inverseAnimated[parent];
                if (inverseAnimatedParent == null)
                {
                    return _animated[index];
                }

                var simulatedParent = pose[_jiggleBones[parent].Bone];
                return _animated[index] * inverseAnimatedParent.Value * simulatedParent;
            }

            private static SysMat4 Simulate(FeModel.FeJiggleBone jiggle, ref State state, SysMat4 goalMatrix, in SimulationContext context)
            {
                var flags = jiggle.BoneFlags;
                float sign = (flags & FeJiggleBoneFlags.InvertAxes) != 0 ? -1f : 1f;
                var goal = new Goal(goalMatrix, sign, jiggle.Length);

                if (!state.IsInitialized || IsFlipped(state, goal))
                {
                    state = new State
                    {
                        IsInitialized = true,
                        BasePosition = goal.Position,
                        BaseLastPosition = goal.Position,
                        TipPosition = goal.Tip,
                    };
                }

                var direction = goal.Forward;
                bool simulatesTip = (flags & (FeJiggleBoneFlags.Flexible | FeJiggleBoneFlags.Rigid)) != 0;

                if (simulatesTip)
                {
                    direction = SimulateTip(jiggle, ref state, in goal, in context);
                }

                var result = CreateFrame(sign * direction, in goal);

                if ((flags & FeJiggleBoneFlags.BaseSpring) != 0)
                {
                    result.Translation = SimulateBase(jiggle, ref state, in goal, in context);
                }

                return result;
            }

            private static bool IsFlipped(State state, in Goal goal)
            {
                var currentDirection = SafeNormalize(state.TipPosition - goal.Position, goal.Forward, MinimumLengthSquared);
                return SysVec3.Dot(currentDirection, goal.Forward) < ResetDot;
            }

            private static SysMat4 CreateFrame(SysVec3 forward, in Goal goal)
            {
                var goalUp = goal.Sign * goal.Up;
                var goalLeft = goal.Sign * goal.Left;
                var left = SafeNormalize(SysVec3.Cross(goalUp, forward), goalLeft, MinimumLengthSquared);
                var up = SysVec3.Cross(forward, left);

                var frame = CreateBasis(forward, left, up);
                frame.Translation = goal.Position;
                return frame;
            }

            private static SysVec3 SimulateTip(FeModel.FeJiggleBone jiggle, ref State state, in Goal goal, in SimulationContext context)
            {
                var flags = jiggle.BoneFlags;
                float deltaTime = context.DeltaTime;

                var acceleration = state.TipAcceleration + new SysVec3(0f, 0f, -jiggle.TipMass);
                state.TipAcceleration = SysVec3.Zero;

                if ((flags & FeJiggleBoneFlags.Flexible) != 0)
                {
                    var error = goal.Tip - state.TipPosition;
                    float yawAccel = (jiggle.YawStiffness * SysVec3.Dot(goal.Left, error)) - (jiggle.YawDamping * SysVec3.Dot(goal.Left, state.TipVelocity));
                    float pitchAccel = (jiggle.PitchStiffness * SysVec3.Dot(goal.Up, error)) - (jiggle.PitchDamping * SysVec3.Dot(goal.Up, state.TipVelocity));

                    acceleration += (yawAccel * goal.Left) + (pitchAccel * goal.Up);

                    if ((flags & FeJiggleBoneFlags.LengthConstraint) == 0)
                    {
                        float alongAccel = (jiggle.AlongStiffness * SysVec3.Dot(goal.Forward, error)) - (jiggle.AlongDamping * SysVec3.Dot(goal.Forward, state.TipVelocity));
                        acceleration += alongAccel * goal.Forward;
                    }
                }

                state.TipVelocity += acceleration * deltaTime;
                state.TipPosition += state.TipVelocity * deltaTime;

                if ((flags & FeJiggleBoneFlags.Collision) != 0)
                {
                    Collide(jiggle, ref state, in goal, in context);
                }

                if ((flags & FeJiggleBoneFlags.YawConstraint) != 0)
                {
                    ApplyAngleRange(ref state, goal.Position, goal.Forward, goal.Left, goal.Up,
                        jiggle.MinYaw, jiggle.MaxYaw, jiggle.YawFriction, jiggle.YawBounce);
                }

                if ((flags & FeJiggleBoneFlags.PitchConstraint) != 0)
                {
                    ApplyAngleRange(ref state, goal.Position, goal.Forward, goal.Up, goal.Left,
                        jiggle.MinPitch, jiggle.MaxPitch, jiggle.PitchFriction, jiggle.PitchBounce);
                }

                var direction = SafeNormalize(state.TipPosition - goal.Position, goal.Forward, MinimumLengthSquared);

                if ((flags & FeJiggleBoneFlags.AngleConstraint) != 0)
                {
                    float dot = SysVec3.Dot(direction, goal.Forward);
                    float angle = SafeAcos(dot);

                    if (dot < 0f)
                    {
                        angle = MathF.Tau - angle;
                    }

                    if (angle > jiggle.AngleLimit)
                    {
                        float maxDistance = 2f * jiggle.Length * MathF.Sin(jiggle.AngleLimit * 0.5f);
                        var awayFromGoal = SafeNormalize(state.TipPosition - goal.Tip, SysVec3.Zero, MinimumLengthSquared);

                        state.TipPosition = goal.Tip + (maxDistance * awayFromGoal);
                        direction = SafeNormalize(state.TipPosition - goal.Position, goal.Forward, MinimumLengthSquared);
                    }
                }

                if ((flags & FeJiggleBoneFlags.LengthConstraint) != 0)
                {
                    state.TipPosition = goal.Position + (jiggle.Length * direction);
                    state.TipVelocity = ProjectOntoPlane(state.TipVelocity, direction);
                }

                return direction;
            }

            private static void ApplyAngleRange(
                ref State state,
                SysVec3 goalPosition,
                SysVec3 forward,
                SysVec3 side,
                SysVec3 axis,
                float min,
                float max,
                float friction,
                float bounce)
            {
                var along = state.TipPosition - goalPosition;
                float angle = MathF.Atan2(SysVec3.Dot(side, along), SysVec3.Dot(forward, along));

                if (!TryClamp(ref angle, min, max))
                {
                    return;
                }

                var (sin, cos) = MathF.SinCos(angle);
                var limitForward = (cos * forward) + (sin * side);
                var limitNormal = (cos * side) - (sin * forward);

                state.TipPosition = goalPosition + (SysVec3.Dot(axis, along) * axis) + (SysVec3.Dot(limitForward, along) * limitForward);

                float normalVelocity = SysVec3.Dot(limitNormal, state.TipVelocity);
                var planeVelocity = (SysVec3.Dot(axis, state.TipVelocity) * axis) + (SysVec3.Dot(limitForward, state.TipVelocity) * limitForward);

                state.TipAcceleration -= friction * planeVelocity;
                state.TipVelocity = planeVelocity - (bounce * normalVelocity * limitNormal);
            }

            private static SysVec3 SimulateBase(FeModel.FeJiggleBone jiggle, ref State state, in Goal goal, in SimulationContext context)
            {
                float deltaTime = context.DeltaTime;
                var acceleration = state.BaseAcceleration + new SysVec3(0f, 0f, -jiggle.BaseMass);
                var error = goal.Position - state.BasePosition;

                acceleration += (jiggle.BaseStiffness * error) - (jiggle.BaseDamping * state.BaseVelocity);

                state.BaseVelocity += acceleration * deltaTime;
                state.BasePosition += state.BaseVelocity * deltaTime;
                state.BaseAcceleration = SysVec3.Zero;

                var offset = state.BasePosition - goal.Position;
                var local = new SysVec3(SysVec3.Dot(goal.Left, offset), SysVec3.Dot(goal.Up, offset), SysVec3.Dot(goal.Forward, offset));
                var localVelocity = new SysVec3(SysVec3.Dot(goal.Left, state.BaseVelocity), SysVec3.Dot(goal.Up, state.BaseVelocity), SysVec3.Dot(goal.Forward, state.BaseVelocity));

                if (TryClamp(ref local.X, jiggle.BaseMinLeft, jiggle.BaseMaxLeft))
                {
                    state.BaseAcceleration -= jiggle.BaseLeftFriction * ((localVelocity.Y * goal.Up) + (localVelocity.Z * goal.Forward));
                }

                if (TryClamp(ref local.Y, jiggle.BaseMinUp, jiggle.BaseMaxUp))
                {
                    state.BaseAcceleration -= jiggle.BaseUpFriction * ((localVelocity.X * goal.Left) + (localVelocity.Z * goal.Forward));
                }

                if (TryClamp(ref local.Z, jiggle.BaseMinForward, jiggle.BaseMaxForward))
                {
                    state.BaseAcceleration -= jiggle.BaseForwardFriction * ((localVelocity.X * goal.Left) + (localVelocity.Y * goal.Up));
                }

                state.BasePosition = goal.Position + (local.X * goal.Left) + (local.Y * goal.Up) + (local.Z * goal.Forward);
                state.BaseVelocity = (state.BasePosition - state.BaseLastPosition) / deltaTime;
                state.BaseLastPosition = state.BasePosition;

                return state.BasePosition;
            }

            private static void Collide(FeModel.FeJiggleBone jiggle, ref State state, in Goal goal, in SimulationContext context)
            {
                bool filtersByMask = (jiggle.BoneFlags & FeJiggleBoneFlags.CollisionMask) != 0;
                var direction = SafeNormalize(state.TipPosition - goal.Position, goal.Forward, MinimumLengthSquared);
                var bone = CreateFrame(goal.Sign * direction, in goal);
                var capsuleStart = SysVec3.Transform(jiggle.Point0, bone);
                var capsuleEnd = SysVec3.Transform(jiggle.Point1, bone);
                var tipBefore = state.TipPosition;

                foreach (var collider in context.WorldColliders)
                {
                    if (filtersByMask && (collider.CollisionMask & jiggle.CollisionMask) == 0)
                    {
                        continue;
                    }

                    var colliderStart = new SysVec3(collider.Start.X, collider.Start.Y, collider.Start.Z);
                    var colliderEnd = new SysVec3(collider.End.X, collider.End.Y, collider.End.Z);

                    var (capsuleT, colliderT) = GetClosestParameters(capsuleStart, capsuleEnd, colliderStart, colliderEnd);
                    var capsulePoint = SysVec3.Lerp(capsuleStart, capsuleEnd, capsuleT);
                    var colliderPoint = SysVec3.Lerp(colliderStart, colliderEnd, colliderT);
                    float capsuleRadius = Lerp(jiggle.Radius0, jiggle.Radius1, capsuleT);
                    float colliderRadius = Lerp(collider.Start.W, collider.End.W, colliderT);

                    float startReach = colliderRadius + jiggle.Radius0;
                    if (startReach * startReach >= SysVec3.DistanceSquared(colliderPoint, capsuleStart))
                    {
                        continue;
                    }

                    var separation = capsulePoint - colliderPoint;
                    float distanceSquared = separation.LengthSquared();
                    float radiusSum = capsuleRadius + colliderRadius;

                    if (radiusSum * radiusSum <= distanceSquared || distanceSquared < MinimumLengthSquared)
                    {
                        continue;
                    }

                    float distance = MathF.Sqrt(distanceSquared);
                    float leverArm = Lerp(jiggle.Point0.X, jiggle.Point1.X, capsuleT);

                    if (leverArm == 0f) continue;

                    var displacement = separation / distance * (radiusSum - distance);
                    state.TipPosition += displacement * (jiggle.Length / leverArm);
                }

                var pushed = state.TipPosition - tipBefore;
                if (pushed != SysVec3.Zero)
                {
                    state.TipVelocity -= SysVec3.Dot(state.TipVelocity, pushed) * pushed;
                }
            }

            private static (float S, float T) GetClosestParameters(SysVec3 p0, SysVec3 p1, SysVec3 q0, SysVec3 q1)
            {
                var d1 = q1 - q0;
                var d2 = p1 - p0;
                var plane = SysVec3.Cross(d1, d2);
                var toward = SysVec3.Cross(d1, plane);
                float denominator = SysVec3.Dot(d2, toward);
                float s = 0f;

                if (MathF.Abs(denominator) > MinimumLengthSquared)
                {
                    s = Saturate(SysVec3.Dot(q0 - p0, toward) / denominator);
                }

                float colliderLengthSquared = d1.LengthSquared();
                float t = 0f;

                if (colliderLengthSquared > MinimumLengthSquared)
                {
                    var point = p0 + (s * d2);
                    t = Saturate(SysVec3.Dot(point - q0, d1) / colliderLengthSquared);
                }

                return (s, t);
            }

            private static int[] GetNodeBones(Skeleton skeleton, FeModel feModel)
            {
                string[] nodeNames = feModel.CtrlName;
                int[] nodeBones = new int[nodeNames.Length];

                for (int i = 0; i < nodeNames.Length; i++)
                {
                    nodeBones[i] = skeleton.GetBoneIndex(nodeNames[i]);
                }

                return nodeBones;
            }

            private static Entry[] GetEntries(FeModel.FeIndexedJiggleBone[] source, int[] nodeBones)
            {
                var candidates = new List<Candidate>();

                for (int i = 0; i < source.Length; i++)
                {
                    var indexed = source[i];
                    if ((uint)indexed.Node >= (uint)nodeBones.Length || nodeBones[indexed.Node] < 0)
                    {
                        continue;
                    }

                    candidates.Add(new Candidate(i, indexed.JiggleParent, nodeBones[indexed.Node], indexed.JiggleBone));
                }

                candidates.Sort(static (a, b) => a.Bone.CompareTo(b.Bone));

                var sortedIndices = new Dictionary<int, int>(candidates.Count);
                for (int i = 0; i < candidates.Count; i++)
                {
                    sortedIndices[candidates[i].SourceIndex] = i;
                }

                var entries = new Entry[candidates.Count];
                for (int i = 0; i < candidates.Count; i++)
                {
                    var candidate = candidates[i];
                    int parent = sortedIndices.GetValueOrDefault(candidate.ParentSourceIndex, -1);
                    if (parent >= i) parent = -1;
                    entries[i] = new Entry(candidate.Bone, parent, candidate.Jiggle);
                }

                return entries;
            }

            private static bool HasCollision(Entry[] entries)
            {
                foreach (var entry in entries)
                {
                    if ((entry.Jiggle.BoneFlags & FeJiggleBoneFlags.Collision) != 0)
                    {
                        return true;
                    }
                }
                return false;
            }

            private static Collider[] GetColliders(FeModel feModel, int[] nodeBones)
            {
                var list = new List<Collider>();

                foreach (var sphere in feModel.SphereRigids)
                {
                    if (sphere.Sphere is { } shape && TryGetBone(nodeBones, sphere.Node, out int bone))
                    {
                        list.Add(new Collider(bone, shape, shape, sphere.CollisionMask));
                    }
                }

                foreach (var capsule in feModel.TaperedCapsuleRigids)
                {
                    if (capsule.Spheres.Length >= 2 && TryGetBone(nodeBones, capsule.Node, out int bone))
                    {
                        list.Add(new Collider(bone, capsule.Spheres[0], capsule.Spheres[1], capsule.CollisionMask));
                    }
                }

                return [.. list];
            }

            private static bool TryGetBone(int[] nodeBones, int node, out int bone)
            {
                bone = -1;
                if ((uint)node >= (uint)nodeBones.Length) return false;
                bone = nodeBones[node];
                return bone >= 0;
            }

            private static bool TryClamp(ref float value, float min, float max)
            {
                if (value < min)
                {
                    value = min;
                    return true;
                }
                if (value > max)
                {
                    value = max;
                    return true;
                }
                return false;
            }
        }
    }
}
