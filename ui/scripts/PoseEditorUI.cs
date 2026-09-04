#pragma warning disable CS0618
using Godot;
using System;
using System.Collections.Generic;

public partial class PoseEditorUI : CanvasLayer
{
    [ExportCategory("Skeleton & Controls")]
    [Export] private Skeleton3D _skeleton;
    [Export] private MenuButton _boneMenuButton;
    [Export] private HSlider _sliderX, _sliderY, _sliderZ;
    [Export] private Label _labelX, _labelY, _labelZ;
    [Export] private Label _selectedBoneLabel;
    [Export] private Button _resetButton;
    [Export] private Button _toggleXRayButton;
    [Export] private CheckBox _toggleLinesButton;
    [Export] private Button _deselectButton;
    [Export] private GridContainer _fastBoneGrid;
    [Export] private OptionButton _poseOptionButton;
    [Export] private OptionButton _allBonesOptionButton;

    [ExportCategory("Bone Layers UI")]
    [Export] private CheckBox _checkPrimary;
    [Export] private CheckBox _checkClothing;
    [Export] private CheckBox _checkFingers;
    [Export] private CheckBox _checkFace;
    [Export] private CheckBox _checkProps;
    [Export] private CheckBox _checkHelpers;

    private AnimationPlayer _animPlayer;
    private BoneLayerManager _layerManager;
    
    private int _selectedBoneIdx = -1;
    private bool _updatingSliders = false;
    
    // An event to notify other scripts (like SkeletonGizmoManager) that X-Ray toggled
    public event Action<bool> OnXRayToggled;
    public bool IsXRayEnabled => _toggleXRayButton != null && _toggleXRayButton.ButtonPressed;

    public event Action<bool> OnLinesToggled;
    public bool IsLinesEnabled => _toggleLinesButton == null || _toggleLinesButton.ButtonPressed;

    // An event to notify other scripts that a bone was selected via UI
    public event Action<int> OnBoneSelectedFromUI;
    
    public override void _Ready()
    {
        if (_sliderX != null) _sliderX.ValueChanged += (v) => OnSliderValueChanged(v, 0);
        if (_sliderY != null) _sliderY.ValueChanged += (v) => OnSliderValueChanged(v, 1);
        if (_sliderZ != null) _sliderZ.ValueChanged += (v) => OnSliderValueChanged(v, 2);

        if (_resetButton != null) _resetButton.Pressed += OnResetPressed;
        
        if (_toggleXRayButton != null)
        {
            _toggleXRayButton.ToggleMode = true;
            _toggleXRayButton.Toggled += OnToggleXRayPressed;
        }
        
        if (_toggleLinesButton != null)
        {
            _toggleLinesButton.Toggled += (v) => OnLinesToggled?.Invoke(v);
        }

        if (_deselectButton != null)
        {
            _deselectButton.Pressed += () => {
                SelectBone(-1);
                OnBoneSelectedFromUI?.Invoke(-1);
            };
        }

        InitLayerCheckboxes();

        if (_skeleton != null)
        {
            PopulateFastBones();
            PopulateAllBones();
        }
    }

    /// <summary>
    /// Connects the BoneLayerManager instance and configures UI checkbox events and default layer states.
    /// </summary>
    public void SetBoneLayerManager(BoneLayerManager layerManager)
    {
        _layerManager = layerManager;
        HookLayerCheckboxEvents();
        PopulateAllBones();
    }

    private void InitLayerCheckboxes()
    {
        // Set default checkbox visual states per design specification
        if (_checkPrimary != null) _checkPrimary.ButtonPressed = true;   // Body / Primary: Default ON
        if (_checkClothing != null) _checkClothing.ButtonPressed = false; // Clothing & Skirts: Default OFF
        if (_checkFingers != null) _checkFingers.ButtonPressed = false;   // Fingers: Default OFF
        if (_checkFace != null) _checkFace.ButtonPressed = false;         // Face & Eyes: Default OFF
        if (_checkProps != null) _checkProps.ButtonPressed = true;       // Weapons & Props: Default ON
        if (_checkHelpers != null) _checkHelpers.ButtonPressed = false;   // Helpers & Internal: Default OFF

        HookLayerCheckboxEvents();
    }

    private bool _checkboxEventsHooked = false;

    private void HookLayerCheckboxEvents()
    {
        if (_checkboxEventsHooked) return;
        _checkboxEventsHooked = true;

        if (_checkPrimary != null) _checkPrimary.Toggled += OnPrimaryToggled;
        if (_checkClothing != null) _checkClothing.Toggled += OnClothingToggled;
        if (_checkFingers != null) _checkFingers.Toggled += OnFingersToggled;
        if (_checkFace != null) _checkFace.Toggled += OnFaceToggled;
        if (_checkProps != null) _checkProps.Toggled += OnPropsToggled;
        if (_checkHelpers != null) _checkHelpers.Toggled += OnHelpersToggled;
    }

    private void OnPrimaryToggled(bool toggled)  => _layerManager?.SetLayerEnabled(BoneCategory.Primary, toggled);
    private void OnClothingToggled(bool toggled) => _layerManager?.SetLayerEnabled(BoneCategory.Clothing, toggled);
    private void OnFingersToggled(bool toggled)  => _layerManager?.SetLayerEnabled(BoneCategory.Fingers, toggled);
    private void OnFaceToggled(bool toggled)     => _layerManager?.SetLayerEnabled(BoneCategory.Face, toggled);
    private void OnPropsToggled(bool toggled)    => _layerManager?.SetLayerEnabled(BoneCategory.Props, toggled);
    private void OnHelpersToggled(bool toggled)  => _layerManager?.SetLayerEnabled(BoneCategory.Helpers, toggled);
    
    public void SetSkeleton(Skeleton3D skeleton)
    {
        _skeleton = skeleton;
        EnforceClothTwoSidedMaterials(_skeleton);
        PopulateFastBones();
        PopulateAllBones();
    }
    
    private void PopulateAllBones()
    {
        if (_allBonesOptionButton == null || _skeleton == null) return;
        
        _allBonesOptionButton.Clear();
        _allBonesOptionButton.AddItem("Select any bone...", 0);
        _allBonesOptionButton.SetItemDisabled(0, true);
        
        int boneCount = _skeleton.GetBoneCount();
        int idx = 1;
        for (int i = 0; i < boneCount; i++)
        {
            string bName = _skeleton.GetBoneName(i);
            BoneCategory category = BoneLayerManager.ClassifyBone(bName);

            string displayName = $"[{category}] {bName}";
            _allBonesOptionButton.AddItem(displayName, idx);
            _allBonesOptionButton.SetItemMetadata(idx, i); // store actual bone index
            idx++;
        }
        
        _allBonesOptionButton.ItemSelected -= OnAllBonesItemSelected;
        _allBonesOptionButton.ItemSelected += OnAllBonesItemSelected;
    }
    
    private void OnAllBonesItemSelected(long index)
    {
        if (index == 0) return;
        int boneIdx = _allBonesOptionButton.GetItemMetadata((int)index).AsInt32();
        SelectBone(boneIdx);
        OnBoneSelectedFromUI?.Invoke(boneIdx);
        
        _allBonesOptionButton.Select(0); 
    }

    public void SetAnimationPlayer(AnimationPlayer player)
    {
        _animPlayer = player;
        if (_poseOptionButton == null)
        {
            GD.PrintErr("Pose OptionButton is NOT assigned in the Godot Inspector! Cannot populate poses.");
            return;
        }
        if (_animPlayer == null) return;

        _poseOptionButton.Clear();
        _poseOptionButton.AddItem("Select a Pose...", -1);
        _poseOptionButton.SetItemDisabled(0, true);

        var animList = _animPlayer.GetAnimationList();
        int idx = 1;
        foreach (string animName in animList)
        {
            _poseOptionButton.AddItem(animName, idx);
            _poseOptionButton.SetItemMetadata(idx, animName);
            idx++;
        }
        
        _poseOptionButton.ItemSelected -= OnPoseItemSelected;
        _poseOptionButton.ItemSelected += OnPoseItemSelected;

        _poseOptionButton.Select(0);
    }

    private void OnPoseItemSelected(long index)
    {
        if (index == 0) return;
        string animName = _poseOptionButton.GetItemMetadata((int)index).AsString();
        ApplyPoseFromAnimation(animName);
    }

    /// <summary>
    /// Applies the keyframes from the first frame of an animation into the skeleton's bone poses.
    /// Preserves 100% of unkeyed bones (such as cloth / skirt / coat bones) in their rest pose relative
    /// to their parent, preventing mesh deformation tearing or lost skinning.
    /// </summary>
    private void ApplyPoseFromAnimation(string animName)
    {
        if (_animPlayer == null || _skeleton == null) return;

        var anim = _animPlayer.GetAnimation(animName);
        if (anim == null) return;

        // Reset all bone poses cleanly to rest
        for (int i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            _skeleton.ResetBonePose(i);
        }

        int trackCount = anim.GetTrackCount();
        for (int i = 0; i < trackCount; i++)
        {
            var trackType = anim.TrackGetType(i);
            if (trackType == Animation.TrackType.Position3D ||
                trackType == Animation.TrackType.Rotation3D ||
                trackType == Animation.TrackType.Scale3D)
            {
                string trackPath = anim.TrackGetPath(i).ToString();
                string[] parts = trackPath.Split(':');
                if (parts.Length > 1)
                {
                    string boneName = parts[parts.Length - 1];
                    int boneIdx = _skeleton.FindBone(boneName);
                    if (boneIdx != -1)
                    {
                        if (trackType == Animation.TrackType.Position3D && anim.TrackGetKeyCount(i) > 0)
                        {
                            Vector3 pos = (Vector3)anim.PositionTrackInterpolate(i, 0.0);
                            _skeleton.SetBonePosePosition(boneIdx, pos);
                        }
                        else if (trackType == Animation.TrackType.Rotation3D && anim.TrackGetKeyCount(i) > 0)
                        {
                            Quaternion rot = (Quaternion)anim.RotationTrackInterpolate(i, 0.0);
                            _skeleton.SetBonePoseRotation(boneIdx, rot);
                        }
                    }
                }
            }
        }
        
        _skeleton.ForceUpdateAllBoneTransforms();

        // Conform unkeyed procedural cloth root bones to follow pelvis / chest rigidly without reparenting
        ConformProceduralClothPoses(_skeleton);

        _skeleton.ForceUpdateAllBoneTransforms();

        UpdateSlidersFromBone();
    }

    private const float SkirtFlapTracking = 0.94f;
    private const float SkirtTwistBlend = 0.50f;

    /// <summary>
    /// Finds the first bone matching any of the candidate names, with case-insensitive fallback.
    /// </summary>
    private static int FindFirstBone(Skeleton3D skeleton, params string[] names)
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

    #region Lightweight Position-Based Dynamics (PBD) Cloth Solver
    private class ClothParticle
    {
        public int BoneIndex;
        public Vector3 RestPosition;
        public Quaternion RestRotation;
        public bool IsAnchor;
    }

    private struct ClothConstraint
    {
        public int ParticleA;
        public int ParticleB;
        public float RestLength;
    }

    private class ClothSolverRig
    {
        public Skeleton3D Skeleton;
        public int BoneCount;
        public ClothParticle[] Particles;
        public Vector3[] CurrentPositions;
        public ClothConstraint[] Constraints;
    }

    private static ClothSolverRig _clothSolverRig;

    /// <summary>
    /// Initializes and caches particle rest state and local structural distance constraints (3 nearest neighbors <= 0.10m).
    /// Anchor particles are classified when rest Y is within 5cm of rest pelvis Y.
    /// </summary>
    private static ClothSolverRig BuildClothSolverRig(Skeleton3D skeleton, int pelvisIdx, Vector3 restPelvisPos, Vector3 restChestPos)
    {
        var rig = new ClothSolverRig
        {
            Skeleton = skeleton,
            BoneCount = skeleton.GetBoneCount()
        };

        var particleList = new List<ClothParticle>();
        int totalBones = skeleton.GetBoneCount();

        for (int i = 0; i < totalBones; i++)
        {
            string bName = skeleton.GetBoneName(i);
            if (string.IsNullOrEmpty(bName)) continue;

            int parentIdx = skeleton.GetBoneParent(i);
            if (parentIdx != -1) continue;

            bool isClothParticle = bName.StartsWith("$cloth_m0p", StringComparison.OrdinalIgnoreCase);
            if (!isClothParticle) continue;

            Transform3D restGlobal = skeleton.GetBoneGlobalRest(i);
            Vector3 restPos = restGlobal.Origin;
            Quaternion restRot = restGlobal.Basis.GetRotationQuaternion();

            // Ignore upper particles belonging to chest/collar/fur
            if (restPos.Y > restChestPos.Y - 0.12f) continue;

            bool isAnchor = Mathf.Abs(restPos.Y - restPelvisPos.Y) <= 0.05f;

            particleList.Add(new ClothParticle
            {
                BoneIndex = i,
                RestPosition = restPos,
                RestRotation = restRot,
                IsAnchor = isAnchor
            });
        }

        rig.Particles = particleList.ToArray();
        rig.CurrentPositions = new Vector3[rig.Particles.Length];

        // Build structural distance constraints: connect each particle to its 3 nearest neighbors within 0.10m
        var constraintSet = new HashSet<(int, int)>();
        var constraintsList = new List<ClothConstraint>();
        int pCount = rig.Particles.Length;

        for (int i = 0; i < pCount; i++)
        {
            Vector3 pi = rig.Particles[i].RestPosition;
            var neighbors = new List<(int idx, float distSq)>();

            for (int j = 0; j < pCount; j++)
            {
                if (i == j) continue;
                float dSq = pi.DistanceSquaredTo(rig.Particles[j].RestPosition);
                if (dSq <= 0.10f * 0.10f && dSq > 1e-6f)
                {
                    neighbors.Add((j, dSq));
                }
            }

            neighbors.Sort((a, b) => a.distSq.CompareTo(b.distSq));
            int countToTake = Mathf.Min(3, neighbors.Count);

            for (int k = 0; k < countToTake; k++)
            {
                int j = neighbors[k].idx;
                int minIdx = Mathf.Min(i, j);
                int maxIdx = Mathf.Max(i, j);
                if (constraintSet.Add((minIdx, maxIdx)))
                {
                    constraintsList.Add(new ClothConstraint
                    {
                        ParticleA = minIdx,
                        ParticleB = maxIdx,
                        RestLength = Mathf.Sqrt(neighbors[k].distSq)
                    });
                }
            }
        }

        rig.Constraints = constraintsList.ToArray();
        return rig;
    }

    /// <summary>
    /// Projects a penetrating particle outside the thigh capsule along its radial displacement vector.
    /// Incorporates the active UP/FORWARD wedge solver: anterior particles are strictly projected onto
    /// the positive (UP/FORWARD/OUTWARD) hemisphere so they cannot be trapped behind or beneath the bone axis.
    /// </summary>
    private static void ResolveThighCapsuleCollisionRadial(
        ref Vector3 point,
        Vector3 pRest,
        Vector3 restPelvisPos,
        Vector3 segA, Vector3 segB,
        Quaternion rotThigh,
        bool isRight,
        float radius, float thickness,
        Transform3D deltaPelvis,
        float modelFwdSign)
    {
        Vector3 ab = segB - segA;
        float abLenSq = ab.LengthSquared();
        if (abLenSq < 1e-6f) return;

        float abLen = Mathf.Sqrt(abLenSq);
        Vector3 uNorm = ab / abLen;

        float t = Mathf.Clamp((point - segA).Dot(ab) / abLenSq, 0.0f, 1.0f);
        Vector3 closest = segA + ab * t;

        Vector3 diff = point - closest;
        float distSq = diff.LengthSquared();
        float effectiveRadius = radius * Mathf.Lerp(1.12f, 1.0f, t) + thickness;

        // Active forward normal perpendicular to the thigh segment
        Vector3 fwdThigh = rotThigh * new Vector3(0, 0, modelFwdSign);
        Vector3 nFwd = fwdThigh - uNorm * uNorm.Dot(fwdThigh);
        if (nFwd.LengthSquared() > 1e-4f) nFwd = nFwd.Normalized();
        else nFwd = (deltaPelvis.Basis * new Vector3(0, 0, modelFwdSign)).Normalized();

        // Active UP normal perpendicular to the thigh segment
        Vector3 upThigh = rotThigh * Vector3.Up;
        Vector3 nUp = upThigh - uNorm * uNorm.Dot(upThigh);
        if (nUp.LengthSquared() > 1e-4f) nUp = nUp.Normalized();
        else nUp = (deltaPelvis.Basis * Vector3.Up).Normalized();

        // Active lateral (outward) normal perpendicular to the thigh segment:
        // In Deadlock GLTF coordinates (+Z forward, +Y up), Left side is +X (Vector3.Right), Right side is -X (Vector3.Left)
        Vector3 sideVec = isRight ? (deltaPelvis.Basis * Vector3.Left) : (deltaPelvis.Basis * Vector3.Right);
        Vector3 nSide = sideVec - uNorm * uNorm.Dot(sideVec);
        if (nSide.LengthSquared() > 1e-4f) nSide = nSide.Normalized();
        else nSide = isRight ? (deltaPelvis.Basis * Vector3.Left).Normalized() : (deltaPelvis.Basis * Vector3.Right).Normalized();

        float diffZ = (pRest.Z - restPelvisPos.Z) * modelFwdSign;
        bool isAnterior = diffZ > -0.02f;

        if (distSq < effectiveRadius * effectiveRadius)
        {
            float dist = Mathf.Sqrt(distSq);
            Vector3 radialDir;

            if (dist > 1e-4f)
            {
                radialDir = diff / dist;
            }
            else
            {
                radialDir = isAnterior ? (nFwd * 0.7f + nSide * 0.7f).Normalized() : nSide;
            }

            if (isAnterior)
            {
                // Thigh flexion angle away from vertical down
                float thighFlex = Mathf.Clamp(1.0f - Mathf.Max(0.0f, -uNorm.Y), 0.0f, 1.0f);
                float fwdComp = radialDir.Dot(nFwd);
                float sideComp = radialDir.Dot(nSide);

                if (thighFlex > 0.15f)
                {
                    // Raised/horizontal thigh: enforce top-half forward/upward projection
                    float upComp = radialDir.Dot(nUp);
                    if (fwdComp < 0.12f || upComp < -0.10f)
                    {
                        float targetFwd = Mathf.Max(0.20f, fwdComp > 0 ? fwdComp : -fwdComp * 0.5f + 0.35f);
                        float targetUp = Mathf.Max(0.15f, upComp > 0 ? upComp : -upComp * 0.5f + 0.25f);
                        radialDir = (nFwd * targetFwd + nUp * targetUp + nSide * sideComp).Normalized();
                    }
                }
                else
                {
                    // Standing/vertical thigh: project strictly on forward/lateral hemisphere (zero negative-Z pull)
                    if (fwdComp < 0.15f)
                    {
                        float targetFwd = Mathf.Max(0.25f, -fwdComp + 0.30f);
                        radialDir = (nFwd * targetFwd + nSide * sideComp).Normalized();
                    }
                }
            }

            point = closest + radialDir * effectiveRadius;
        }
    }

    /// <summary>
    /// Projects a particle outside the calf line segment capsule primitive using radial displacement.
    /// </summary>
    private static void ResolveCalfCapsuleCollisionRadial(
        ref Vector3 point,
        Vector3 segA, Vector3 segB,
        float radius, float thickness,
        Vector3 fallbackDir,
        bool isAnterior = false)
    {
        Vector3 ab = segB - segA;
        float abLenSq = ab.LengthSquared();
        if (abLenSq < 1e-6f) return;

        float t = Mathf.Clamp((point - segA).Dot(ab) / abLenSq, 0.0f, 1.0f);
        Vector3 closest = segA + ab * t;

        Vector3 diff = point - closest;
        float distSq = diff.LengthSquared();
        float effectiveRadius = radius + thickness;

        if (distSq < effectiveRadius * effectiveRadius)
        {
            float dist = Mathf.Sqrt(distSq);
            Vector3 radialDir = (dist > 1e-4f) ? (diff / dist) : fallbackDir;
            if (isAnterior && radialDir.Dot(fallbackDir) < 0.15f)
            {
                radialDir = (radialDir + fallbackDir * (0.35f - radialDir.Dot(fallbackDir))).Normalized();
            }
            point = closest + radialDir.Normalized() * effectiveRadius;
        }
    }

    /// <summary>
    /// Computes unified knee flexion ratio: 0.0 = straight leg (~0.95), 1.0 = deep crouch (<= -0.5).
    /// Uses consistent thresholds across warm-start seeding, collision projection, and skinning orientation.
    /// </summary>
    private static float ComputeKneeFlexRatio(Vector3 hip, Vector3 knee, Vector3 ankle)
    {
        Vector3 vThigh = knee - hip;
        Vector3 vCalf = ankle - knee;
        float lenThighSq = vThigh.LengthSquared();
        float lenCalfSq = vCalf.LengthSquared();
        if (lenThighSq < 1e-8f || lenCalfSq < 1e-8f) return 0.0f;

        Vector3 uThigh = vThigh / Mathf.Sqrt(lenThighSq);
        Vector3 uCalf = vCalf / Mathf.Sqrt(lenCalfSq);
        float dotStraight = uThigh.Dot(uCalf);

        // flexRatio: 0.0 = straight leg (~0.95), 1.0 = deep crouch (<= -0.5)
        return Mathf.Clamp((0.95f - dotStraight) / 1.45f, 0.0f, 1.0f);
    }

    /// <summary>
    /// Projects a particle outside the dynamic knee apex collision sphere and applies the Patella Tent elevation.
    /// In crouch / flex poses, treats the knee as a continuous convex apex connecting thigh to calf,
    /// strictly projecting anterior particles onto the positive UP/FORWARD hemisphere and lifting them
    /// above the knee joint center so fabric cannot be trapped behind the flexed knee joint.
    /// </summary>
    private static void ResolveKneeApexSphereCollision(
        ref Vector3 point,
        Vector3 pRest,
        Vector3 restPelvisPos,
        Vector3 currHip, Vector3 currKnee, Vector3 currAnkle,
        Quaternion rotThigh,
        bool isRight,
        float clothThickness,
        Transform3D deltaPelvis,
        float modelFwdSign)
    {
        Vector3 vThigh = currKnee - currHip;
        Vector3 vCalf = currAnkle - currKnee;
        float lenThighSq = vThigh.LengthSquared();
        float lenCalfSq = vCalf.LengthSquared();
        if (lenThighSq < 1e-6f || lenCalfSq < 1e-6f) return;

        float flexRatio = ComputeKneeFlexRatio(currHip, currKnee, currAnkle);
        float rKnee = Mathf.Lerp(0.11f, 0.15f, flexRatio);
        float effRadius = rKnee + clothThickness;

        Vector3 uThigh = vThigh / Mathf.Sqrt(lenThighSq);
        Vector3 uCalf = vCalf / Mathf.Sqrt(lenCalfSq);

        // Knee apex forward direction (patella)
        Vector3 ha = currAnkle - currHip;
        float haLenSq = ha.LengthSquared();
        Vector3 bendFwd = Vector3.Zero;
        if (haLenSq > 1e-4f)
        {
            float t = Mathf.Clamp((currKnee - currHip).Dot(ha) / haLenSq, 0.0f, 1.0f);
            bendFwd = currKnee - (currHip + ha * t);
        }
        if (bendFwd.LengthSquared() < 1e-4f)
            bendFwd = uThigh - uCalf;

        Vector3 kneeFwd;
        if (bendFwd.LengthSquared() > 1e-4f)
            kneeFwd = bendFwd.Normalized();
        else
            kneeFwd = (rotThigh * new Vector3(0, 0, modelFwdSign)).Normalized();

        // Lateral normal perpendicular to thigh axis: Left is +X, Right is -X
        Vector3 sideVec = isRight ? (deltaPelvis.Basis * Vector3.Left) : (deltaPelvis.Basis * Vector3.Right);
        Vector3 nSide = sideVec - uThigh * uThigh.Dot(sideVec);
        if (nSide.LengthSquared() > 1e-4f) nSide = nSide.Normalized();
        else nSide = isRight ? (deltaPelvis.Basis * Vector3.Left).Normalized() : (deltaPelvis.Basis * Vector3.Right).Normalized();

        // In deep flex, patella apex protrudes forward and slightly upward
        Vector3 kneeCenter = currKnee + kneeFwd * (flexRatio * 0.07f) + Vector3.Up * (flexRatio * 0.02f);

        Vector3 diff = point - kneeCenter;
        float dist = diff.Length();

        float diffZ = (pRest.Z - restPelvisPos.Z) * modelFwdSign;
        bool isAnterior = diffZ > -0.02f;

        if (dist < effRadius)
        {
            Vector3 pushDir;
            if (dist > 1e-4f)
            {
                pushDir = diff / dist;
            }
            else
            {
                pushDir = (kneeFwd * 0.7f + Vector3.Up * 0.7f).Normalized();
            }

            if (isAnterior)
            {
                float fwdDot = pushDir.Dot(kneeFwd);
                float upDot = pushDir.Dot(Vector3.Up);
                float sideDot = pushDir.Dot(nSide);

                if (flexRatio > 0.20f)
                {
                    // Strict forward & outward projection along patella apex normal for bent knee
                    float targetFwd = Mathf.Max(0.55f, fwdDot > 0 ? fwdDot : 0.55f);
                    float targetUp = Mathf.Max(0.15f, upDot > 0 ? upDot : 0.15f);
                    pushDir = (kneeFwd * targetFwd + Vector3.Up * targetUp + nSide * sideDot).Normalized();
                }
                else if (fwdDot < 0.20f || upDot < -0.10f)
                {
                    float targetFwd = Mathf.Max(0.35f, fwdDot > 0 ? fwdDot : -fwdDot + 0.40f);
                    float targetUp = Mathf.Max(0.25f, upDot > 0 ? upDot : -upDot + 0.30f);
                    pushDir = (kneeFwd * targetFwd + Vector3.Up * targetUp + nSide * sideDot).Normalized();
                }
            }

            point = kneeCenter + pushDir * effRadius;
        }

        // Robust Convex Knee Apex Projection: smoothly push anterior particles forward outside patella dome
        if (isAnterior && flexRatio > 0.15f)
        {
            Vector3 dKnee = point - currKnee;
            float latDist = dKnee.Dot(nSide);
            float fwdDist = dKnee.Dot(kneeFwd);
            float yDist = dKnee.Y;

            // Smooth bell-curve attenuation around knee apex (continuous dome, zero flat horizontal shelf clamping)
            float latFactor = Mathf.Clamp(1.0f - (latDist * latDist) / ((effRadius * 1.35f) * (effRadius * 1.35f)), 0.0f, 1.0f);
            float yFactor = Mathf.Clamp(1.0f - (yDist * yDist) / (0.22f * 0.22f), 0.0f, 1.0f);
            float domeFactor = latFactor * yFactor * flexRatio;

            if (domeFactor > 0.01f)
            {
                if (fwdDist < effRadius * 0.55f)
                {
                    float pushFwd = (effRadius * 0.55f - fwdDist) * domeFactor;
                    point += kneeFwd * pushFwd;
                }
                // Smooth patella crest elevation (continuous dome, zero flat clamping)
                float targetY = currKnee.Y + 0.05f * domeFactor;
                if (point.Y < targetY && fwdDist > -0.02f)
                {
                    point.Y = Mathf.Lerp(point.Y, targetY, 0.65f * domeFactor);
                }
            }
        }
    }

    /// <summary>
    /// Executes a lightweight Position-Based Dynamics (PBD) relaxation loop.
    /// Incorporates hierarchical single-pivot kinematic seeding (zero position lerping),
    /// active top-half wedge projection, robust patella apex elevation, collision priority over
    /// distance constraints, gravity suppression on supported surfaces, dynamic floor pooling, and bilateral leg blending.
    /// </summary>
    private static void SolveClothPBD(
        Skeleton3D skeleton,
        Transform3D deltaPelvis,
        Quaternion rotPelvis,
        Vector3 currPelvisPos,
        Vector3 restPelvisPos,
        Vector3 restHipR, Vector3 currHipR, Quaternion rotThighR, Vector3 restKneeR, Vector3 currKneeR, Quaternion rotKneeR, Vector3 currAnkleR,
        Vector3 restHipL, Vector3 currHipL, Quaternion rotThighL, Vector3 restKneeL, Vector3 currKneeL, Quaternion rotKneeL, Vector3 currAnkleL,
        float modelFwdSign,
        float groundY)
    {
        var rig = _clothSolverRig;
        int pCount = rig.Particles.Length;
        Vector3[] pos = rig.CurrentPositions;

        float legGap = (currHipL - currHipR).Length();
        float rThigh = Mathf.Clamp(legGap * 0.38f, 0.09f, 0.115f);
        float rCalf = Mathf.Clamp(rThigh * 0.95f, 0.095f, 0.110f);
        const float clothThickness = 0.012f;

        float hipMidX = (restHipL.X + restHipR.X) * 0.5f;
        float halfSpan = Mathf.Max(0.04f, Mathf.Abs(restHipL.X - restHipR.X) * 0.22f);

        // Unified knee flexion ratio across warm start, collision, and skinning orientation
        float flexRatioR = ComputeKneeFlexRatio(currHipR, currKneeR, currAnkleR);
        float flexRatioL = ComputeKneeFlexRatio(currHipL, currKneeL, currAnkleL);

        // Precompute patella forward directions for apex support checks
        Vector3 vThighR = currKneeR - currHipR;
        Vector3 vCalfR = currAnkleR - currKneeR;
        Vector3 uThighR = vThighR.LengthSquared() > 1e-6f ? vThighR.Normalized() : Vector3.Down;
        Vector3 uCalfR = vCalfR.LengthSquared() > 1e-6f ? vCalfR.Normalized() : Vector3.Down;
        Vector3 haR = currAnkleR - currHipR;
        float haLenSqR = haR.LengthSquared();
        Vector3 bendFwdR = Vector3.Zero;
        if (haLenSqR > 1e-4f)
        {
            float tR = Mathf.Clamp((currKneeR - currHipR).Dot(haR) / haLenSqR, 0.0f, 1.0f);
            bendFwdR = currKneeR - (currHipR + haR * tR);
        }
        if (bendFwdR.LengthSquared() < 1e-4f)
            bendFwdR = uThighR - uCalfR;

        Vector3 kneeFwdR = bendFwdR.LengthSquared() > 1e-4f
            ? bendFwdR.Normalized()
            : (rotThighR * new Vector3(0, 0, modelFwdSign)).Normalized();

        Vector3 vThighL = currKneeL - currHipL;
        Vector3 vCalfL = currAnkleL - currKneeL;
        Vector3 uThighL = vThighL.LengthSquared() > 1e-6f ? vThighL.Normalized() : Vector3.Down;
        Vector3 uCalfL = vCalfL.LengthSquared() > 1e-6f ? vCalfL.Normalized() : Vector3.Down;
        Vector3 haL = currAnkleL - currHipL;
        float haLenSqL = haL.LengthSquared();
        Vector3 bendFwdL = Vector3.Zero;
        if (haLenSqL > 1e-4f)
        {
            float tL = Mathf.Clamp((currKneeL - currHipL).Dot(haL) / haLenSqL, 0.0f, 1.0f);
            bendFwdL = currKneeL - (currHipL + haL * tL);
        }
        if (bendFwdL.LengthSquared() < 1e-4f)
            bendFwdL = uThighL - uCalfL;

        Vector3 kneeFwdL = bendFwdL.LengthSquared() > 1e-4f
            ? bendFwdL.Normalized()
            : (rotThighL * new Vector3(0, 0, modelFwdSign)).Normalized();

        // 1. Kinematic Warm Start (Single-Pivot Rotational Articulation with Lateral Bilateral Weighting - ZERO Position Lerping)
        for (int k = 0; k < pCount; k++)
        {
            var p = rig.Particles[k];
            if (p.IsAnchor)
            {
                pos[k] = currPelvisPos + (deltaPelvis.Basis * (p.RestPosition - restPelvisPos));
                continue;
            }

            Vector3 pRest = p.RestPosition;

            // Lateral bilateral weighting (X axis):
            // Left leg is +X, Right leg is -X. Ensures particles over each leg track that leg 100%.
            float uLeft = Mathf.Clamp((pRest.X - (hipMidX - halfSpan)) / (2.0f * halfSpan), 0.0f, 1.0f);
            float weightL = uLeft * uLeft * (3.0f - 2.0f * uLeft);
            float weightR = 1.0f - weightL;

            const float kneeZoneHalf = 0.15f;

            // Single-pivot hierarchical kinematic placement for Right Leg:
            Vector3 pLegR;
            if (pRest.Y >= restKneeR.Y + kneeZoneHalf)
            {
                pLegR = currHipR + (new Basis(rotThighR) * (pRest - restHipR));
            }
            else if (pRest.Y <= restKneeR.Y - kneeZoneHalf)
            {
                pLegR = currKneeR + (new Basis(rotKneeR) * (pRest - restKneeR));
            }
            else
            {
                float uR = Mathf.Clamp((restKneeR.Y + kneeZoneHalf - pRest.Y) / (2.0f * kneeZoneHalf), 0.0f, 1.0f);
                float smoothUR = uR * uR * (3.0f - 2.0f * uR);
                Quaternion rotBlendR = rotThighR.Slerp(rotKneeR, smoothUR);
                pLegR = currKneeR + (new Basis(rotBlendR) * (pRest - restKneeR));
            }

            // Single-pivot hierarchical kinematic placement for Left Leg:
            Vector3 pLegL;
            if (pRest.Y >= restKneeL.Y + kneeZoneHalf)
            {
                pLegL = currHipL + (new Basis(rotThighL) * (pRest - restHipL));
            }
            else if (pRest.Y <= restKneeL.Y - kneeZoneHalf)
            {
                pLegL = currKneeL + (new Basis(rotKneeL) * (pRest - restKneeL));
            }
            else
            {
                float uL = Mathf.Clamp((restKneeL.Y + kneeZoneHalf - pRest.Y) / (2.0f * kneeZoneHalf), 0.0f, 1.0f);
                float smoothUL = uL * uL * (3.0f - 2.0f * uL);
                Quaternion rotBlendL = rotThighL.Slerp(rotKneeL, smoothUL);
                pLegL = currKneeL + (new Basis(rotBlendL) * (pRest - restKneeL));
            }

            Vector3 pLeg = pLegR * weightR + pLegL * weightL;

            float diffZ = (pRest.Z - restPelvisPos.Z) * modelFwdSign;
            float fwdFactor = Mathf.Clamp((diffZ + 0.03f) / 0.10f, 0.0f, 1.0f);
            float flexRatioBilateral = Mathf.Lerp(flexRatioR, flexRatioL, weightL);
            float rearDrape = Mathf.Clamp(flexRatioBilateral * 1.25f, 0.0f, 1.0f);
            float wRear = (1.0f - rearDrape) * 0.85f;
            float wLeg = Mathf.Lerp(wRear, 1.0f, fwdFactor);

            Vector3 pBasePelvis = currPelvisPos + (deltaPelvis.Basis * (pRest - restPelvisPos));
            Vector3 seed = pBasePelvis.Lerp(pLeg, wLeg);

            if (seed.Y < groundY) seed.Y = groundY;
            pos[k] = seed;
        }


        // 2. Relaxation Iterations (18 iterations)
        const float stiffnessFront = 0.55f;
        const float stiffnessRear = 0.30f;
        Vector3 fallbackCalfDir = (deltaPelvis.Basis * new Vector3(0, 0, modelFwdSign)).Normalized();

        for (int iter = 0; iter < 18; iter++)
        {
            // A. Gravity / Drape on non-anchor particles:
            // Zero-out gravity forces on particles that are currently resting on the upper surface of a thigh or knee capsule.
            // Avoid premature lock: only flag supported if the particle is on the outer surface shell, NOT inside the volume.
            for (int k = 0; k < pCount; k++)
            {
                if (rig.Particles[k].IsAnchor) continue;

                float diffZ = (rig.Particles[k].RestPosition.Z - restPelvisPos.Z) * modelFwdSign;
                bool isAnterior = diffZ > -0.02f;

                bool isSupportedOnLimb = false;
                if (isAnterior)
                {
                    // Check right thigh upper surface
                    float abLenSqR = vThighR.LengthSquared();
                    if (abLenSqR > 1e-4f)
                    {
                        float tR = Mathf.Clamp((pos[k] - currHipR).Dot(vThighR) / abLenSqR, 0f, 1f);
                        Vector3 closestR = currHipR + vThighR * tR;
                        float distSqR = (pos[k] - closestR).LengthSquared();
                        float rThighEff = rThigh + clothThickness;
                        if (distSqR >= (rThighEff - 0.02f) * (rThighEff - 0.02f) &&
                            distSqR <= (rThighEff + 0.03f) * (rThighEff + 0.03f) &&
                            pos[k].Y >= closestR.Y - 0.01f)
                        {
                            isSupportedOnLimb = true;
                        }
                    }

                    // Check left thigh upper surface
                    if (!isSupportedOnLimb)
                    {
                        float abLenSqL = vThighL.LengthSquared();
                        if (abLenSqL > 1e-4f)
                        {
                            float tL = Mathf.Clamp((pos[k] - currHipL).Dot(vThighL) / abLenSqL, 0f, 1f);
                            Vector3 closestL = currHipL + vThighL * tL;
                            float distSqL = (pos[k] - closestL).LengthSquared();
                            float rThighEff = rThigh + clothThickness;
                            if (distSqL >= (rThighEff - 0.02f) * (rThighEff - 0.02f) &&
                                distSqL <= (rThighEff + 0.03f) * (rThighEff + 0.03f) &&
                                pos[k].Y >= closestL.Y - 0.01f)
                            {
                                isSupportedOnLimb = true;
                            }
                        }
                    }

                    // Check knee apex upper surface (must be near outer surface shell, NOT penetrating inside)
                    if (!isSupportedOnLimb)
                    {
                        float rKneeR = Mathf.Lerp(0.11f, 0.15f, flexRatioR) + clothThickness;
                        Vector3 kneeCenterR = currKneeR + kneeFwdR * (flexRatioR * 0.07f) + Vector3.Up * (flexRatioR * 0.02f);
                        float distSqKneeR = (pos[k] - kneeCenterR).LengthSquared();
                        if (distSqKneeR >= (rKneeR - 0.02f) * (rKneeR - 0.02f) &&
                            distSqKneeR <= (rKneeR + 0.03f) * (rKneeR + 0.03f) &&
                            pos[k].Y >= kneeCenterR.Y - 0.01f)
                        {
                            isSupportedOnLimb = true;
                        }
                        else
                        {
                            float rKneeL = Mathf.Lerp(0.11f, 0.15f, flexRatioL) + clothThickness;
                            Vector3 kneeCenterL = currKneeL + kneeFwdL * (flexRatioL * 0.07f) + Vector3.Up * (flexRatioL * 0.02f);
                            float distSqKneeL = (pos[k] - kneeCenterL).LengthSquared();
                            if (distSqKneeL >= (rKneeL - 0.02f) * (rKneeL - 0.02f) &&
                                distSqKneeL <= (rKneeL + 0.03f) * (rKneeL + 0.03f) &&
                                pos[k].Y >= kneeCenterL.Y - 0.01f)
                            {
                                isSupportedOnLimb = true;
                            }
                        }
                    }
                }

                if (!isSupportedOnLimb)
                {
                    float postFactor = Mathf.Clamp(-diffZ / 0.12f, 0.0f, 1.0f);
                    float grav = 0.0022f + postFactor * 0.0068f;
                    pos[k].Y -= grav;
                }
            }

            // B. Unilateral Structural Constraints (Stretch Limiting) - EXECUTED BEFORE COLLISIONS
            // Allows up to 1.30x resting length on front and 1.45x on rear so fabric expands around bent joints without pulling particles inside
            int cCount = rig.Constraints.Length;
            for (int c = 0; c < cCount; c++)
            {
                ref var constraint = ref rig.Constraints[c];
                int idxA = constraint.ParticleA;
                int idxB = constraint.ParticleB;

                Vector3 delta = pos[idxA] - pos[idxB];
                float dist = delta.Length();

                float diffZA = (rig.Particles[idxA].RestPosition.Z - restPelvisPos.Z) * modelFwdSign;
                float diffZB = (rig.Particles[idxB].RestPosition.Z - restPelvisPos.Z) * modelFwdSign;
                bool isPosteriorConstraint = (diffZA < -0.01f && diffZB < -0.01f);

                float maxStretch = isPosteriorConstraint ? 1.45f : 1.30f;
                float constraintStiffness = isPosteriorConstraint ? stiffnessRear : stiffnessFront;
                float maxDist = constraint.RestLength * maxStretch;

                if (dist > maxDist)
                {
                    float diff = (dist - maxDist) / dist;
                    Vector3 correction = delta * (diff * constraintStiffness);

                    bool anchorA = rig.Particles[idxA].IsAnchor;
                    bool anchorB = rig.Particles[idxB].IsAnchor;

                    if (!anchorA && !anchorB)
                    {
                        pos[idxA] -= correction * 0.5f;
                        pos[idxB] += correction * 0.5f;
                    }
                    else if (!anchorA && anchorB)
                    {
                        pos[idxA] -= correction;
                    }
                    else if (anchorA && !anchorB)
                    {
                        pos[idxB] += correction;
                    }
                }
            }

            // C. 3D Radial Capsule & Knee Apex Wedge/Tent Collisions (EXECUTED AFTER CONSTRAINTS TO GUARANTEE CLEARANCE)
            for (int k = 0; k < pCount; k++)
            {
                if (rig.Particles[k].IsAnchor) continue;

                ref Vector3 pt = ref pos[k];
                Vector3 pRest = rig.Particles[k].RestPosition;

                // Thigh capsules (Wedge Top-Half projection)
                ResolveThighCapsuleCollisionRadial(ref pt, pRest, restPelvisPos, currHipR, currKneeR, rotThighR, true, rThigh, clothThickness, deltaPelvis, modelFwdSign);
                ResolveThighCapsuleCollisionRadial(ref pt, pRest, restPelvisPos, currHipL, currKneeL, rotThighL, false, rThigh, clothThickness, deltaPelvis, modelFwdSign);

                // Knee apex sphere & Patella Tent elevation
                ResolveKneeApexSphereCollision(ref pt, pRest, restPelvisPos, currHipR, currKneeR, currAnkleR, rotThighR, true, clothThickness, deltaPelvis, modelFwdSign);
                ResolveKneeApexSphereCollision(ref pt, pRest, restPelvisPos, currHipL, currKneeL, currAnkleL, rotThighL, false, clothThickness, deltaPelvis, modelFwdSign);

                // Calf capsules
                float diffZ = (pRest.Z - restPelvisPos.Z) * modelFwdSign;
                bool isAnterior = diffZ > -0.02f;
                ResolveCalfCapsuleCollisionRadial(ref pt, currKneeR, currAnkleR, rCalf, clothThickness, fallbackCalfDir, isAnterior);
                ResolveCalfCapsuleCollisionRadial(ref pt, currKneeL, currAnkleL, rCalf, clothThickness, fallbackCalfDir, isAnterior);
            }

            // D. Dynamic Ground Plane Collision & Horizontal Pooling
            for (int k = 0; k < pCount; k++)
            {
                if (rig.Particles[k].IsAnchor) continue;

                if (pos[k].Y < groundY)
                {
                    float penetration = groundY - pos[k].Y;
                    pos[k].Y = groundY;

                    // Slide outward / pool horizontally on the floor
                    Vector3 poolDir = new Vector3(pos[k].X - hipMidX, 0, (pos[k].Z - restPelvisPos.Z));
                    if (poolDir.LengthSquared() > 1e-4f)
                    {
                        Vector3 nPool = poolDir.Normalized();
                        pos[k].X += nPool.X * penetration * 0.50f;
                        pos[k].Z += nPool.Z * penetration * 0.50f;
                    }
                }
            }

            // E. Pelvis Pinning: Re-enforce rigid anchor positions
            for (int k = 0; k < pCount; k++)
            {
                if (rig.Particles[k].IsAnchor)
                {
                    pos[k] = currPelvisPos + (deltaPelvis.Basis * (rig.Particles[k].RestPosition - restPelvisPos));
                }
            }
        }

        // 3. Dynamic Skinning Orientation with Lateral Bilateral Blending
        for (int k = 0; k < pCount; k++)
        {
            var p = rig.Particles[k];
            if (p.IsAnchor)
            {
                skeleton.SetBonePosePosition(p.BoneIndex, pos[k]);
                skeleton.SetBonePoseRotation(p.BoneIndex, rotPelvis * p.RestRotation);
                continue;
            }

            Vector3 pRest = p.RestPosition;

            // Lateral bilateral weighting (X axis):
            float uLeft = Mathf.Clamp((pRest.X - (hipMidX - halfSpan)) / (2.0f * halfSpan), 0.0f, 1.0f);
            float weightL = uLeft * uLeft * (3.0f - 2.0f * uLeft);
            float weightR = 1.0f - weightL;

            Quaternion rotThighBilateral = rotThighR.Slerp(rotThighL, weightL);
            Quaternion rotCalfBilateral = rotKneeR.Slerp(rotKneeL, weightL);

            // Relative offset from pelvis center in forward direction
            float diffZ = (pRest.Z - restPelvisPos.Z) * modelFwdSign;
            float fwdFactor = Mathf.Clamp((diffZ + 0.03f) / 0.10f, 0.0f, 1.0f);

            // Displacement from pure pelvis drape
            Vector3 pBasePelvis = currPelvisPos + (deltaPelvis.Basis * (pRest - restPelvisPos));
            float dispFromPelvis = (pos[k] - pBasePelvis).Length();
            float dispFactor = Mathf.Clamp(dispFromPelvis / 0.15f, 0.0f, 1.0f);

            // Dynamic rotation blend factor (up to 85% thigh tracking for forward and displaced particles)
            float rotBlend = Mathf.Clamp(Mathf.Max(fwdFactor * 0.80f, dispFactor * 0.80f), 0.0f, 0.85f);

            // Calf rotation blend factor: smooth Hermite curve over 30cm knee envelope
            float restKneeY = Mathf.Lerp(restKneeR.Y, restKneeL.Y, weightL);
            float flexRatioBilateral = Mathf.Lerp(flexRatioR, flexRatioL, weightL);
            float belowKnee = restKneeY - pRest.Y;
            const float skinZoneHalf = 0.15f;
            float uKnee = Mathf.Clamp((belowKnee + skinZoneHalf) / (2.0f * skinZoneHalf), 0.0f, 1.0f);
            float heightFactor = uKnee * uKnee * (3.0f - 2.0f * uKnee);
            float calfBlend = Mathf.Clamp(heightFactor * flexRatioBilateral, 0.0f, 1.0f);

            // If tail particle is resting on or pooled on the ground, keep it aligned upright with pelvis so fabric lies flat
            bool isOnGround = pos[k].Y <= groundY + 0.015f;
            if (isOnGround && diffZ < 0)
            {
                rotBlend = 0.0f;
                calfBlend = 0.0f;
            }

            Quaternion particleRot = rotPelvis
                .Slerp(rotThighBilateral, rotBlend)
                .Slerp(rotCalfBilateral, calfBlend);

            skeleton.SetBonePosePosition(p.BoneIndex, pos[k]);
            skeleton.SetBonePoseRotation(p.BoneIndex, particleRot * p.RestRotation);
        }
    }
    #endregion

    /// <summary>
    /// Enforces two-sided rendering (CullMode = Disabled) on character clothing materials
    /// so inner fabric and backfaces remain fully visible from all camera angles.
    /// </summary>
    public static void EnforceClothTwoSidedMaterials(Node characterRoot)
    {
        if (characterRoot == null) return;

        Node root = characterRoot;
        while (root.GetParent() != null && !(root.GetParent() is SubViewport) && !(root.GetParent() is Window))
        {
            if (root is Skeleton3D && root.GetParent() != null)
            {
                root = root.GetParent();
                break;
            }
            root = root.GetParent();
        }

        ApplyTwoSidedRecursive(root);
    }

    private static void ApplyTwoSidedRecursive(Node node)
    {
        if (node == null) return;

        if (node is MeshInstance3D mi && mi.Mesh != null)
        {
            string name = mi.Name.ToString().ToLowerInvariant();
            if (!name.Contains("marker") && !name.Contains("gizmo"))
            {
                int surfaceCount = mi.Mesh.GetSurfaceCount();
                for (int s = 0; s < surfaceCount; s++)
                {
                    Material mat = mi.GetSurfaceOverrideMaterial(s) ?? mi.Mesh.SurfaceGetMaterial(s);
                    if (mat is BaseMaterial3D baseMat)
                    {
                        baseMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
                    }
                }
            }
        }

        foreach (Node child in node.GetChildren())
        {
            ApplyTwoSidedRecursive(child);
        }
    }

    /// <summary>
    /// Conforms procedural cloth particle bones ($cloth_m0p*) using a Lightweight Position-Based Dynamics (PBD)
    /// cloth relaxation loop with true 3D radial capsule collisions against thighs and calves, dynamic skinning orientation,
    /// dynamic ground plane floor pooling, and bilateral leg collision resolution.
    /// Also aligns parented anterior/posterior slit flaps (dress_cut_front_*, dress_out_front_*).
    /// </summary>
    public static void ConformProceduralClothPoses(Skeleton3D skeleton)
    {
        if (skeleton == null) return;
        skeleton.ForceUpdateAllBoneTransforms();

        int boneCount = skeleton.GetBoneCount();
        if (boneCount == 0) return;

        // Ensure two-sided rendering on character clothing so inner faces are never culled
        EnforceClothTwoSidedMaterials(skeleton);

        // 1. Localizar los huesos clave anatómicos y twist
        int pelvisIdx = FindFirstBone(skeleton, "pelvis", "Pelvis");
        int chestIdx = FindFirstBone(skeleton, "chest", "spine_3", "spine_2");

        int legLIdx = FindFirstBone(skeleton, "leg_upper_L", "thigh_L");
        int legRIdx = FindFirstBone(skeleton, "leg_upper_R", "thigh_R");

        int twistLIdx = FindFirstBone(skeleton, "leg_upper_L_TWIST", "leg_upper_L_twist", "thigh_L_twist", "thigh_L_TWIST", "leg_upper_twist_L");
        int twistRIdx = FindFirstBone(skeleton, "leg_upper_R_TWIST", "leg_upper_R_twist", "thigh_R_twist", "thigh_R_TWIST", "leg_upper_twist_R");

        int kneeLIdx = FindFirstBone(skeleton, "leg_lower_L", "knee_L", "calf_L");
        int kneeRIdx = FindFirstBone(skeleton, "leg_lower_R", "knee_R", "calf_R");

        int ankleLIdx = FindFirstBone(skeleton, "ankle_L", "foot_L", "leg_foot_L");
        int ankleRIdx = FindFirstBone(skeleton, "ankle_R", "foot_R", "leg_foot_R");

        int ballLIdx = FindFirstBone(skeleton, "ball_L", "toe_L", "foot_toe_L");
        int ballRIdx = FindFirstBone(skeleton, "ball_R", "toe_R", "foot_toe_R");

        if (pelvisIdx == -1) return;

        // 2. Matrices y transformaciones globales de Pelvis y Torso
        Transform3D restPelvis = skeleton.GetBoneGlobalRest(pelvisIdx);
        Transform3D currPelvis = skeleton.GetBoneGlobalPose(pelvisIdx);
        Transform3D deltaPelvis = currPelvis * restPelvis.AffineInverse();
        Quaternion rotPelvis = deltaPelvis.Basis.GetRotationQuaternion();

        Vector3 restPelvisPos = restPelvis.Origin;
        Vector3 currPelvisPos = currPelvis.Origin;

        // Detect model forward orientation (+Z vs -Z) dynamically:
        // Deadlock models exported into Godot via VRF GLTF exporter have +Z as forward.
        // Confirm from foot anatomy (toes in front of ankles) or dress flaps if present.
        float modelFwdSign = 1.0f; // Default Godot / Deadlock GLTF convention: +Z is forward
        if ((ballLIdx != -1 && ankleLIdx != -1) || (ballRIdx != -1 && ankleRIdx != -1))
        {
            float dz = 0.0f;
            if (ballLIdx != -1 && ankleLIdx != -1)
                dz = skeleton.GetBoneGlobalRest(ballLIdx).Origin.Z - skeleton.GetBoneGlobalRest(ankleLIdx).Origin.Z;
            else if (ballRIdx != -1 && ankleRIdx != -1)
                dz = skeleton.GetBoneGlobalRest(ballRIdx).Origin.Z - skeleton.GetBoneGlobalRest(ankleRIdx).Origin.Z;

            if (Mathf.Abs(dz) > 0.02f)
            {
                modelFwdSign = dz > 0.0f ? 1.0f : -1.0f;
            }
        }
        else
        {
            for (int i = 0; i < boneCount; i++)
            {
                string bName = skeleton.GetBoneName(i);
                if (bName.StartsWith("dress_cut_front", StringComparison.OrdinalIgnoreCase) ||
                    bName.StartsWith("dress_out_front", StringComparison.OrdinalIgnoreCase))
                {
                    float fZ = skeleton.GetBoneGlobalRest(i).Origin.Z - restPelvisPos.Z;
                    if (Mathf.Abs(fZ) > 0.01f)
                    {
                        modelFwdSign = fZ > 0.0f ? 1.0f : -1.0f;
                        break;
                    }
                }
            }
        }

        Transform3D deltaChest = deltaPelvis;
        Vector3 restChestPos = restPelvisPos;
        Vector3 currChestPos = currPelvisPos;
        if (chestIdx != -1)
        {
            Transform3D restChest = skeleton.GetBoneGlobalRest(chestIdx);
            Transform3D currChest = skeleton.GetBoneGlobalPose(chestIdx);
            deltaChest = currChest * restChest.AffineInverse();
            restChestPos = restChest.Origin;
            currChestPos = currChest.Origin;
        }
        Quaternion rotChest = deltaChest.Basis.GetRotationQuaternion();

        // 3. Pierna Izquierda
        Transform3D restLegL = (legLIdx != -1) 
            ? skeleton.GetBoneGlobalRest(legLIdx) 
            : new Transform3D(Basis.Identity, restPelvisPos + new Vector3(0.12f, -0.05f, 0));
        Transform3D currLegL = (legLIdx != -1) 
            ? skeleton.GetBoneGlobalPose(legLIdx) 
            : new Transform3D(new Basis(rotPelvis), currPelvisPos + rotPelvis * (restLegL.Origin - restPelvisPos));
        Transform3D deltaLegL = currLegL * restLegL.AffineInverse();
        Quaternion rotLegL = deltaLegL.Basis.GetRotationQuaternion();

        Quaternion rotThighL = rotLegL;
        if (twistLIdx != -1)
        {
            Transform3D restTwistL = skeleton.GetBoneGlobalRest(twistLIdx);
            Transform3D currTwistL = skeleton.GetBoneGlobalPose(twistLIdx);
            Transform3D deltaTwistL = currTwistL * restTwistL.AffineInverse();
            Quaternion rotTwistL = deltaTwistL.Basis.GetRotationQuaternion();
            rotThighL = rotLegL.Slerp(rotTwistL, SkirtTwistBlend);
        }

        Vector3 restHipL = restLegL.Origin;
        Vector3 currHipL = currLegL.Origin;

        Vector3 restKneeL = (kneeLIdx != -1) 
            ? skeleton.GetBoneGlobalRest(kneeLIdx).Origin 
            : restHipL + new Vector3(0, -0.42f, 0);
        Vector3 currKneeL = (kneeLIdx != -1) 
            ? skeleton.GetBoneGlobalPose(kneeLIdx).Origin 
            : currHipL + rotLegL * (restKneeL - restHipL);

        Quaternion rotKneeL = rotThighL;
        if (kneeLIdx != -1)
        {
            Transform3D deltaKneeL = skeleton.GetBoneGlobalPose(kneeLIdx) * skeleton.GetBoneGlobalRest(kneeLIdx).AffineInverse();
            rotKneeL = deltaKneeL.Basis.GetRotationQuaternion();
        }

        Vector3 restAnkleL = (ankleLIdx != -1) 
            ? skeleton.GetBoneGlobalRest(ankleLIdx).Origin 
            : restKneeL + new Vector3(0, -0.42f, 0);
        Vector3 currAnkleL = (ankleLIdx != -1) 
            ? skeleton.GetBoneGlobalPose(ankleLIdx).Origin 
            : currKneeL + rotKneeL * (restAnkleL - restKneeL);

        // 4. Pierna Derecha
        Transform3D restLegR = (legRIdx != -1) 
            ? skeleton.GetBoneGlobalRest(legRIdx) 
            : new Transform3D(Basis.Identity, restPelvisPos + new Vector3(-0.12f, -0.05f, 0));
        Transform3D currLegR = (legRIdx != -1) 
            ? skeleton.GetBoneGlobalPose(legRIdx) 
            : new Transform3D(new Basis(rotPelvis), currPelvisPos + rotPelvis * (restLegR.Origin - restPelvisPos));
        Transform3D deltaLegR = currLegR * restLegR.AffineInverse();
        Quaternion rotLegR = deltaLegR.Basis.GetRotationQuaternion();

        Quaternion rotThighR = rotLegR;
        if (twistRIdx != -1)
        {
            Transform3D restTwistR = skeleton.GetBoneGlobalRest(twistRIdx);
            Transform3D currTwistR = skeleton.GetBoneGlobalPose(twistRIdx);
            Transform3D deltaTwistR = currTwistR * restTwistR.AffineInverse();
            Quaternion rotTwistR = deltaTwistR.Basis.GetRotationQuaternion();
            rotThighR = rotLegR.Slerp(rotTwistR, SkirtTwistBlend);
        }

        Vector3 restHipR = restLegR.Origin;
        Vector3 currHipR = currLegR.Origin;

        Vector3 restKneeR = (kneeRIdx != -1) 
            ? skeleton.GetBoneGlobalRest(kneeRIdx).Origin 
            : restHipR + new Vector3(0, -0.42f, 0);
        Vector3 currKneeR = (kneeRIdx != -1) 
            ? skeleton.GetBoneGlobalPose(kneeRIdx).Origin 
            : currHipR + rotLegR * (restKneeR - restHipR);

        Quaternion rotKneeR = rotThighR;
        if (kneeRIdx != -1)
        {
            Transform3D deltaKneeR = skeleton.GetBoneGlobalPose(kneeRIdx) * skeleton.GetBoneGlobalRest(kneeRIdx).AffineInverse();
            rotKneeR = deltaKneeR.Basis.GetRotationQuaternion();
        }

        Vector3 restAnkleR = (ankleRIdx != -1) 
            ? skeleton.GetBoneGlobalRest(ankleRIdx).Origin 
            : restKneeR + new Vector3(0, -0.42f, 0);
        Vector3 currAnkleR = (ankleRIdx != -1) 
            ? skeleton.GetBoneGlobalPose(ankleRIdx).Origin 
            : currKneeR + rotKneeR * (restAnkleR - restKneeR);

        // Determine dynamic ground plane height from ball/toe bones if available, or ankle offset (-0.105m)
        float groundY = Mathf.Min(currAnkleL.Y, currAnkleR.Y) - 0.105f;
        if (ballLIdx != -1 || ballRIdx != -1)
        {
            float minBallY = float.MaxValue;
            if (ballLIdx != -1) minBallY = Mathf.Min(minBallY, skeleton.GetBoneGlobalPose(ballLIdx).Origin.Y);
            if (ballRIdx != -1) minBallY = Mathf.Min(minBallY, skeleton.GetBoneGlobalPose(ballRIdx).Origin.Y);
            if (minBallY < float.MaxValue)
            {
                groundY = Mathf.Min(groundY, minBallY - 0.02f);
            }
        }

        // 5. Ajustar solapas emparentadas y ropa superior (cuello, boa, fur, coat)
        for (int i = 0; i < boneCount; i++)
        {
            string bName = skeleton.GetBoneName(i);
            if (string.IsNullOrEmpty(bName)) continue;

            int parentIdx = skeleton.GetBoneParent(i);
            if (parentIdx != -1)
            {
                // Solapas parentadas: dress_cut_front_*, dress_out_front_*, dress_cut_back_*
                bool isFlapFront = bName.StartsWith("dress_cut_front", StringComparison.OrdinalIgnoreCase) ||
                                   bName.StartsWith("dress_out_front", StringComparison.OrdinalIgnoreCase);
                bool isFlapBack = bName.StartsWith("dress_cut_back", StringComparison.OrdinalIgnoreCase) ||
                                  bName.StartsWith("dress_out_back", StringComparison.OrdinalIgnoreCase);

                if (isFlapFront || isFlapBack)
                {
                    bool isRight = true;
                    if (bName.Contains("_L", StringComparison.OrdinalIgnoreCase) || bName.EndsWith("L", StringComparison.OrdinalIgnoreCase))
                        isRight = false;
                    else if (bName.Contains("_R", StringComparison.OrdinalIgnoreCase) || bName.EndsWith("R", StringComparison.OrdinalIgnoreCase))
                        isRight = true;
                    else
                        isRight = skeleton.GetBoneGlobalRest(i).Origin.X <= 0.0f; // -X is Right, +X is Left

                    Quaternion targetLegRot = isRight ? rotThighR : rotThighL;

                    string parentName = skeleton.GetBoneName(parentIdx) ?? "";
                    bool parentIsFlap = parentName.StartsWith("dress_cut", StringComparison.OrdinalIgnoreCase) ||
                                        parentName.StartsWith("dress_out", StringComparison.OrdinalIgnoreCase);

                    if (parentIsFlap)
                    {
                        skeleton.SetBonePoseRotation(i, skeleton.GetBoneRest(i).Basis.GetRotationQuaternion());
                    }
                    else
                    {
                        float trackWeight = isFlapFront ? SkirtFlapTracking : (SkirtFlapTracking * 0.45f);
                        Quaternion flapDeltaRot = rotPelvis.Slerp(targetLegRot, trackWeight);

                        Transform3D restFlapGlobal = skeleton.GetBoneGlobalRest(i);
                        Transform3D currParent = skeleton.GetBoneGlobalPose(parentIdx);

                        Quaternion targetGlobalRot = flapDeltaRot * restFlapGlobal.Basis.GetRotationQuaternion();
                        Quaternion localPoseRot = currParent.Basis.GetRotationQuaternion().Inverse() * targetGlobalRot;

                        skeleton.SetBonePoseRotation(i, localPoseRot);
                    }
                }
                continue;
            }

            // Unparented procedural cloth particles ($cloth_m0p*) are solved by the PBD solver below
            bool isParticle = bName.StartsWith("$cloth_m0p", StringComparison.OrdinalIgnoreCase);
            if (isParticle) continue;

            bool isCloth = bName.StartsWith("$cloth", StringComparison.OrdinalIgnoreCase) ||
                           bName.Contains("skirt", StringComparison.OrdinalIgnoreCase) ||
                           bName.Contains("dress", StringComparison.OrdinalIgnoreCase) ||
                           bName.Contains("coat", StringComparison.OrdinalIgnoreCase) ||
                           bName.Contains("boa", StringComparison.OrdinalIgnoreCase) ||
                           bName.Contains("fur", StringComparison.OrdinalIgnoreCase);

            if (!isCloth) continue;

            Transform3D restCloth = skeleton.GetBoneGlobalRest(i);
            Vector3 pRest = restCloth.Origin;
            Quaternion restClothRot = restCloth.Basis.GetRotationQuaternion();

            bool isUpper = bName.Contains("coat", StringComparison.OrdinalIgnoreCase) ||
                           bName.Contains("fur", StringComparison.OrdinalIgnoreCase) ||
                           bName.Contains("boa", StringComparison.OrdinalIgnoreCase) ||
                           bName.Contains("collar", StringComparison.OrdinalIgnoreCase) ||
                           (pRest.Y > restChestPos.Y - 0.12f);

            if (isUpper)
            {
                Vector3 localOffsetChest = pRest - restChestPos;
                Vector3 pFinal = currChestPos + (deltaChest.Basis * localOffsetChest);
                skeleton.SetBonePosePosition(i, pFinal);
                skeleton.SetBonePoseRotation(i, rotChest * restClothRot);
            }
            else
            {
                Vector3 localOffsetPelvis = pRest - restPelvisPos;
                Vector3 pFinal = currPelvisPos + (deltaPelvis.Basis * localOffsetPelvis);
                skeleton.SetBonePosePosition(i, pFinal);
                skeleton.SetBonePoseRotation(i, rotPelvis * restClothRot);
            }
        }

        // 6. Ejecutar el Solver PBD para todas las partículas de falda/vestido ($cloth_m0p*)
        if (_clothSolverRig == null || _clothSolverRig.Skeleton != skeleton || _clothSolverRig.BoneCount != skeleton.GetBoneCount())
        {
            _clothSolverRig = BuildClothSolverRig(skeleton, pelvisIdx, restPelvisPos, restChestPos);
        }

        if (_clothSolverRig != null && _clothSolverRig.Particles.Length > 0)
        {
            skeleton.ForceUpdateAllBoneTransforms();
            SolveClothPBD(skeleton, deltaPelvis, rotPelvis, currPelvisPos, restPelvisPos,
                          restHipR, currHipR, rotThighR, restKneeR, currKneeR, rotKneeR, currAnkleR,
                          restHipL, currHipL, rotThighL, restKneeL, currKneeL, rotKneeL, currAnkleL,
                          modelFwdSign, groundY);
        }

        skeleton.ForceUpdateAllBoneTransforms();
    }
    
    private void PopulateFastBones()
    {
        if (_fastBoneGrid == null || _skeleton == null) return;
        
        foreach (Node child in _fastBoneGrid.GetChildren())
        {
            child.QueueFree();
        }

        var fastBones = new Dictionary<string, string>
        {
            { "Weapon R", "weapon_hand_R" },
            { "Weapon L", "weapon_hand_L" },
            { "Head", "head" },
            { "Spine", "spine" },
            { "Arm R", "arm_upper_R" },
            { "Arm L", "arm_upper_L" },
            { "Leg R", "leg_upper_R" },
            { "Leg L", "leg_upper_L" }
        };

        foreach (var kvp in fastBones)
        {
            int foundIdx = -1;
            for (int i = 0; i < _skeleton.GetBoneCount(); i++)
            {
                string bName = _skeleton.GetBoneName(i)?.ToLowerInvariant() ?? "";
                if (bName.Contains(kvp.Value.ToLowerInvariant()))
                {
                    foundIdx = i;
                    break;
                }
            }

            if (foundIdx != -1)
            {
                var btn = new Button
                {
                    Text = kvp.Key
                };
                btn.Pressed += () => {
                    SelectBone(foundIdx);
                    OnBoneSelectedFromUI?.Invoke(foundIdx);
                };
                _fastBoneGrid.AddChild(btn);
            }
        }
    }
    
    public void SelectBone(int boneIdx)
    {
        _selectedBoneIdx = boneIdx;
        
        if (_selectedBoneIdx == -1)
        {
            if (_selectedBoneLabel != null) _selectedBoneLabel.Text = "Selected Bone: None";
            
            _updatingSliders = true;
            if (_sliderX != null) _sliderX.Value = 0;
            if (_sliderY != null) _sliderY.Value = 0;
            if (_sliderZ != null) _sliderZ.Value = 0;
            UpdateLabels();
            _updatingSliders = false;
            return;
        }

        if (_skeleton != null)
        {
            string bName = _skeleton.GetBoneName(_selectedBoneIdx);
            BoneCategory cat = BoneLayerManager.ClassifyBone(bName);
            if (_selectedBoneLabel != null)
            {
                _selectedBoneLabel.Text = $"[{cat}] {bName}";
            }
            if (_boneMenuButton != null)
            {
                _boneMenuButton.Text = bName;
            }
        }
        
        UpdateSlidersFromBone();
    }
    
    public void UpdateSlidersFromBone()
    {
        if (_selectedBoneIdx < 0 || _skeleton == null) return;
        
        _updatingSliders = true;
        
        Quaternion rot = _skeleton.GetBonePoseRotation(_selectedBoneIdx);
        Vector3 eulerRads = rot.GetEuler();
        
        if (_sliderX != null) _sliderX.Value = Mathf.RadToDeg(eulerRads.X);
        if (_sliderY != null) _sliderY.Value = Mathf.RadToDeg(eulerRads.Y);
        if (_sliderZ != null) _sliderZ.Value = Mathf.RadToDeg(eulerRads.Z);
        
        UpdateLabels();
        
        _updatingSliders = false;
    }
    
    private void OnSliderValueChanged(double value, int axisIndex)
    {
        if (_updatingSliders || _selectedBoneIdx < 0 || _skeleton == null) return;
        
        UpdateLabels();
        
        if (_sliderX == null || _sliderY == null || _sliderZ == null) return;

        Vector3 eulerRads = new Vector3(
            Mathf.DegToRad((float)_sliderX.Value),
            Mathf.DegToRad((float)_sliderY.Value),
            Mathf.DegToRad((float)_sliderZ.Value)
        );
        
        _skeleton.SetBonePoseRotation(_selectedBoneIdx, Quaternion.FromEuler(eulerRads));
        ConformProceduralClothPoses(_skeleton);
    }
    
    private void UpdateLabels()
    {
        if (_labelX != null && _sliderX != null) _labelX.Text = $"{_sliderX.Value:F1}°";
        if (_labelY != null && _sliderY != null) _labelY.Text = $"{_sliderY.Value:F1}°";
        if (_labelZ != null && _sliderZ != null) _labelZ.Text = $"{_sliderZ.Value:F1}°";
    }
    
    private void OnResetPressed()
    {
        if (_skeleton == null) return;
        
        for (int i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            _skeleton.ResetBonePose(i);
        }
        
        ConformProceduralClothPoses(_skeleton);
        UpdateSlidersFromBone();
    }

    private void OnToggleXRayPressed(bool toggledOn)
    {
        OnXRayToggled?.Invoke(toggledOn);
    }
}
