#pragma warning disable CS0618
using Godot;
using System;
using System.Collections.Generic;
using DeadlockPlayground.Tools;

/// <summary>
/// Standalone Position-Based Dynamics (PBD) cloth solver and conforming service for Deadlock character models.
/// Extracted directly from the verified reference implementation.
/// </summary>
public static class ProceduralClothSolver
{
    private static bool _isDirty = true;
    private static ulong _lastSolvedFrame = 0;

    public static void MarkDirty()
    {
        _isDirty = true;
    }

    public static void Conform(Skeleton3D skeleton, bool force = false, AnimationPlayer animPlayer = null)
    {
        if (skeleton == null) return;
        ulong currentFrame = Engine.GetProcessFrames();
        if (!force && !_isDirty && currentFrame == _lastSolvedFrame)
        {
            return;
        }

        ConformProceduralClothPoses(skeleton, animPlayer);
        _isDirty = false;
        _lastSolvedFrame = currentFrame;
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

        // Torso vs Leg cloth classification
        public bool IsTorso;
        public float TorsoT; // 0.0 at pelvis, 1.0 at chest
        public Vector3 RestSpineOffset; // Offset from rest spine center at height TorsoT

        // Precalculated geometric parameters (invariant per hero model)
        public float WeightL;
        public float WeightR;
        public int ZoneR; // 0 = above knee, 1 = below knee, 2 = knee zone
        public float SmoothUR;
        public int ZoneL; // 0 = above knee, 1 = below knee, 2 = knee zone
        public float SmoothUL;
        public float FwdFactor;
        public float DiffZ;
        public bool IsAnterior;
        public float Grav;
        public float RestKneeY;
    }

    private struct ClothConstraint
    {
        public int ParticleA;
        public int ParticleB;
        public float RestLength;
        public float MaxDist;
        public float MaxDistSq;
        public float Stiffness;
    }

    private struct FlapBoneData
    {
        public int BoneIndex;
        public int ParentIndex;
        public bool IsFlapFront;
        public bool IsRight;
        public bool ParentIsFlap;
        public Transform3D RestFlapGlobal;
    }

    private struct KinematicClothingData
    {
        public int BoneIndex;
        public string BoneName;
        public bool IsUpper;
        public Transform3D RestCloth;
    }

    private class ClothSolverRig
    {
        public Skeleton3D Skeleton;
        public int BoneCount;
        public ClothParticle[] Particles;
        public Vector3[] CurrentPositions;
        public ClothConstraint[] Constraints;
        public int[] AnchorIndices;
        public int[] NonAnchorIndices;

        // Cached skeleton topology and key bones
        public int PelvisIdx;
        public int ChestIdx;
        public int LegLIdx;
        public int LegRIdx;
        public int TwistLIdx;
        public int TwistRIdx;
        public int KneeLIdx;
        public int KneeRIdx;
        public int AnkleLIdx;
        public int AnkleRIdx;
        public int BallLIdx;
        public int BallRIdx;
        public float ModelFwdSign;

        // Cached flap and kinematic clothing lists
        public FlapBoneData[] FlapBones;
        public KinematicClothingData[] KinematicClothingBones;
    }

    private static ClothSolverRig _clothSolverRig;

    /// <summary>
    /// Initializes and caches particle rest state, local structural distance constraints, key bones,
    /// and preclassified flaps / kinematic clothing bones.
    /// Anchor particles are classified when rest Y is within 5cm of rest pelvis Y.
    /// Only bones matching BoneLayerManager.IsProceduralClothBone are included in the PBD particle rig.
    /// </summary>
    private static ClothSolverRig BuildClothSolverRig(Skeleton3D skeleton)
    {
        int totalBones = skeleton.GetBoneCount();
        var rig = new ClothSolverRig
        {
            Skeleton = skeleton,
            BoneCount = totalBones
        };

        // 1. Locate key anatomical bones
        rig.PelvisIdx = FindFirstBone(skeleton, "pelvis", "Pelvis");
        rig.ChestIdx = FindFirstBone(skeleton, "chest", "spine_3", "spine_2");
        rig.LegLIdx = FindFirstBone(skeleton, "leg_upper_L", "thigh_L");
        rig.LegRIdx = FindFirstBone(skeleton, "leg_upper_R", "thigh_R");
        rig.TwistLIdx = FindFirstBone(skeleton, "leg_upper_L_TWIST", "leg_upper_L_twist", "thigh_L_twist", "thigh_L_TWIST", "leg_upper_twist_L");
        rig.TwistRIdx = FindFirstBone(skeleton, "leg_upper_R_TWIST", "leg_upper_R_twist", "thigh_R_twist", "thigh_R_TWIST", "leg_upper_twist_R");
        rig.KneeLIdx = FindFirstBone(skeleton, "leg_lower_L", "knee_L", "calf_L");
        rig.KneeRIdx = FindFirstBone(skeleton, "leg_lower_R", "knee_R", "calf_R");
        rig.AnkleLIdx = FindFirstBone(skeleton, "ankle_L", "foot_L", "leg_foot_L");
        rig.AnkleRIdx = FindFirstBone(skeleton, "ankle_R", "foot_R", "leg_foot_R");
        rig.BallLIdx = FindFirstBone(skeleton, "ball_L", "toe_L", "foot_toe_L");
        rig.BallRIdx = FindFirstBone(skeleton, "ball_R", "toe_R", "foot_toe_R");

        if (rig.PelvisIdx == -1) return rig;

        Vector3 restPelvisPos = skeleton.GetBoneGlobalRest(rig.PelvisIdx).Origin;
        Vector3 restChestPos = (rig.ChestIdx != -1) ? skeleton.GetBoneGlobalRest(rig.ChestIdx).Origin : restPelvisPos;

        // 2. Detect forward orientation (+Z vs -Z)
        float modelFwdSign = 1.0f;
        if ((rig.BallLIdx != -1 && rig.AnkleLIdx != -1) || (rig.BallRIdx != -1 && rig.AnkleRIdx != -1))
        {
            float dz = 0.0f;
            if (rig.BallLIdx != -1 && rig.AnkleLIdx != -1)
                dz = skeleton.GetBoneGlobalRest(rig.BallLIdx).Origin.Z - skeleton.GetBoneGlobalRest(rig.AnkleLIdx).Origin.Z;
            else if (rig.BallRIdx != -1 && rig.AnkleRIdx != -1)
                dz = skeleton.GetBoneGlobalRest(rig.BallRIdx).Origin.Z - skeleton.GetBoneGlobalRest(rig.AnkleRIdx).Origin.Z;

            if (Mathf.Abs(dz) > 0.02f)
            {
                modelFwdSign = dz > 0.0f ? 1.0f : -1.0f;
            }
        }
        else
        {
            for (int i = 0; i < totalBones; i++)
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
        rig.ModelFwdSign = modelFwdSign;

        // 3. Precompute rest leg span and knee references for fast particle weight evaluation
        Vector3 restHipL = (rig.LegLIdx != -1) ? skeleton.GetBoneGlobalRest(rig.LegLIdx).Origin : restPelvisPos + new Vector3(0.12f, -0.05f, 0);
        Vector3 restHipR = (rig.LegRIdx != -1) ? skeleton.GetBoneGlobalRest(rig.LegRIdx).Origin : restPelvisPos + new Vector3(-0.12f, -0.05f, 0);
        float hipMidX = (restHipL.X + restHipR.X) * 0.5f;
        float halfSpan = Mathf.Max(0.04f, Mathf.Abs(restHipL.X - restHipR.X) * 0.22f);

        float restKneeRY = (rig.KneeRIdx != -1) ? skeleton.GetBoneGlobalRest(rig.KneeRIdx).Origin.Y : restHipR.Y - 0.42f;
        float restKneeLY = (rig.KneeLIdx != -1) ? skeleton.GetBoneGlobalRest(rig.KneeLIdx).Origin.Y : restHipL.Y - 0.42f;
        const float kneeZoneHalf = 0.15f;

        float spineSpan = Mathf.Max(0.20f, restChestPos.Y - restPelvisPos.Y);
        float maxClothY = float.MinValue;
        for (int i = 0; i < totalBones; i++)
        {
            string bName = skeleton.GetBoneName(i);
            if (string.IsNullOrEmpty(bName)) continue;
            int parentIdx = skeleton.GetBoneParent(i);
            if (parentIdx == -1 && BoneLayerManager.IsProceduralClothBone(bName))
            {
                float y = skeleton.GetBoneGlobalRest(i).Origin.Y;
                if (y > maxClothY) maxClothY = y;
            }
        }
        bool hasUpperGarment = maxClothY > restPelvisPos.Y + 0.10f;

        var particleList = new List<ClothParticle>();
        var flapList = new List<FlapBoneData>();
        var kinematicList = new List<KinematicClothingData>();

        for (int i = 0; i < totalBones; i++)
        {
            string bName = skeleton.GetBoneName(i);
            if (string.IsNullOrEmpty(bName)) continue;

            int parentIdx = skeleton.GetBoneParent(i);

            // Flap detection (dress_cut_front_*, dress_out_front_*, dress_cut_back_*, dress_out_back_*)
            if (parentIdx != -1)
            {
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
                        isRight = skeleton.GetBoneGlobalRest(i).Origin.X <= 0.0f;

                    string parentName = skeleton.GetBoneName(parentIdx) ?? "";
                    bool parentIsFlap = parentName.StartsWith("dress_cut", StringComparison.OrdinalIgnoreCase) ||
                                        parentName.StartsWith("dress_out", StringComparison.OrdinalIgnoreCase);

                    flapList.Add(new FlapBoneData
                    {
                        BoneIndex = i,
                        ParentIndex = parentIdx,
                        IsFlapFront = isFlapFront,
                        IsRight = isRight,
                        ParentIsFlap = parentIsFlap,
                        RestFlapGlobal = skeleton.GetBoneGlobalRest(i)
                    });
                    continue;
                }
            }

            // Procedural PBD cloth particles ($cloth* / cloth_*) across skirts, coats, robes
            if (parentIdx == -1 && BoneLayerManager.IsProceduralClothBone(bName))
            {
                Transform3D restGlobal = skeleton.GetBoneGlobalRest(i);
                Vector3 restPos = restGlobal.Origin;
                Quaternion restRot = restGlobal.Basis.GetRotationQuaternion();

                float yDiff = restPos.Y - restPelvisPos.Y;
                bool isTorso = yDiff >= -0.05f;
                float torsoT = Mathf.Clamp(yDiff / spineSpan, 0.0f, 1.0f);
                Vector3 restSpineCenter = restPelvisPos.Lerp(restChestPos, torsoT);
                Vector3 restSpineOffset = restPos - restSpineCenter;

                bool isAnchor;
                if (hasUpperGarment)
                {
                    // Upper torso garment (coat, jacket, upper robe): anchored at top attachment seam near maxClothY
                    isAnchor = restPos.Y >= maxClothY - 0.045f;
                }
                else
                {
                    // Skirt / lower garment: anchored at pelvis waistband
                    isAnchor = Mathf.Abs(yDiff) <= 0.05f || (restPos.Y >= maxClothY - 0.04f);
                }

                // Precompute static weights (used for leg collision & skirt simulation)
                float uLeft = Mathf.Clamp((restPos.X - (hipMidX - halfSpan)) / (2.0f * halfSpan), 0.0f, 1.0f);
                float weightL = uLeft * uLeft * (3.0f - 2.0f * uLeft);
                float weightR = 1.0f - weightL;

                int zoneR = 2;
                float smoothUR = 0f;
                if (restPos.Y >= restKneeRY + kneeZoneHalf) zoneR = 0;
                else if (restPos.Y <= restKneeRY - kneeZoneHalf) zoneR = 1;
                else
                {
                    float uR = Mathf.Clamp((restKneeRY + kneeZoneHalf - restPos.Y) / (2.0f * kneeZoneHalf), 0.0f, 1.0f);
                    smoothUR = uR * uR * (3.0f - 2.0f * uR);
                }

                int zoneL = 2;
                float smoothUL = 0f;
                if (restPos.Y >= restKneeLY + kneeZoneHalf) zoneL = 0;
                else if (restPos.Y <= restKneeLY - kneeZoneHalf) zoneL = 1;
                else
                {
                    float uL = Mathf.Clamp((restKneeLY + kneeZoneHalf - restPos.Y) / (2.0f * kneeZoneHalf), 0.0f, 1.0f);
                    smoothUL = uL * uL * (3.0f - 2.0f * uL);
                }

                float diffZ = (restPos.Z - restPelvisPos.Z) * modelFwdSign;
                float fwdFactor = Mathf.Clamp((diffZ + 0.03f) / 0.10f, 0.0f, 1.0f);
                bool isAnterior = diffZ > -0.02f;
                float postFactor = Mathf.Clamp(-diffZ / 0.12f, 0.0f, 1.0f);
                float grav = 0.0022f + postFactor * 0.0068f;
                float restKneeY = Mathf.Lerp(restKneeRY, restKneeLY, weightL);

                particleList.Add(new ClothParticle
                {
                    BoneIndex = i,
                    RestPosition = restPos,
                    RestRotation = restRot,
                    IsAnchor = isAnchor,
                    IsTorso = isTorso,
                    TorsoT = torsoT,
                    RestSpineOffset = restSpineOffset,
                    WeightL = weightL,
                    WeightR = weightR,
                    ZoneR = zoneR,
                    SmoothUR = smoothUR,
                    ZoneL = zoneL,
                    SmoothUL = smoothUL,
                    FwdFactor = fwdFactor,
                    DiffZ = diffZ,
                    IsAnterior = isAnterior,
                    Grav = grav,
                    RestKneeY = restKneeY
                });
                continue;
            }

            // Kinematic clothing bones (coat_*, bag_*, jacket_*, fur, boa, collar, strap)
            if (BoneLayerManager.IsKinematicClothingBone(bName))
            {
                Transform3D restCloth = skeleton.GetBoneGlobalRest(i);
                Vector3 pRest = restCloth.Origin;
                bool isUpper = bName.Contains("coat", StringComparison.OrdinalIgnoreCase) ||
                               bName.Contains("fur", StringComparison.OrdinalIgnoreCase) ||
                               bName.Contains("boa", StringComparison.OrdinalIgnoreCase) ||
                               bName.Contains("collar", StringComparison.OrdinalIgnoreCase) ||
                               bName.Contains("jacket", StringComparison.OrdinalIgnoreCase) ||
                               bName.Contains("strap", StringComparison.OrdinalIgnoreCase) ||
                               (pRest.Y > restChestPos.Y - 0.12f);

                kinematicList.Add(new KinematicClothingData
                {
                    BoneIndex = i,
                    BoneName = bName,
                    IsUpper = isUpper,
                    RestCloth = restCloth
                });
            }
        }

        rig.Particles = particleList.ToArray();
        rig.CurrentPositions = new Vector3[rig.Particles.Length];
        rig.FlapBones = flapList.ToArray();
        rig.KinematicClothingBones = kinematicList.ToArray();

        // 4. Build structural distance constraints: connect each particle to its nearest neighbors within 0.12m
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
                if (dSq <= 0.12f * 0.12f && dSq > 1e-6f)
                {
                    neighbors.Add((j, dSq));
                }
            }

            neighbors.Sort((a, b) => a.distSq.CompareTo(b.distSq));
            int countToTake = Mathf.Min(4, neighbors.Count);

            for (int k = 0; k < countToTake; k++)
            {
                int j = neighbors[k].idx;
                int minIdx = Mathf.Min(i, j);
                int maxIdx = Mathf.Max(i, j);
                if (constraintSet.Add((minIdx, maxIdx)))
                {
                    float restLen = Mathf.Sqrt(neighbors[k].distSq);
                    bool bothTorso = rig.Particles[minIdx].IsTorso && rig.Particles[maxIdx].IsTorso;

                    float diffZA = (rig.Particles[minIdx].RestPosition.Z - restPelvisPos.Z) * modelFwdSign;
                    float diffZB = (rig.Particles[maxIdx].RestPosition.Z - restPelvisPos.Z) * modelFwdSign;
                    bool isPosterior = (diffZA < -0.01f && diffZB < -0.01f);

                    float maxStretch;
                    float stiffness;
                    if (bothTorso)
                    {
                        // Heavy coat/jacket fabric: tight stretch threshold to prevent rubbery stretching
                        maxStretch = 1.12f;
                        stiffness = 0.65f;
                    }
                    else
                    {
                        maxStretch = isPosterior ? 1.45f : 1.30f;
                        stiffness = isPosterior ? 0.30f : 0.55f;
                    }

                    float maxDist = restLen * maxStretch;

                    constraintsList.Add(new ClothConstraint
                    {
                        ParticleA = minIdx,
                        ParticleB = maxIdx,
                        RestLength = restLen,
                        MaxDist = maxDist,
                        MaxDistSq = maxDist * maxDist,
                        Stiffness = stiffness
                    });
                }
            }
        }

        rig.Constraints = constraintsList.ToArray();

        var anchorList = new List<int>();
        var nonAnchorList = new List<int>();
        for (int i = 0; i < rig.Particles.Length; i++)
        {
            if (rig.Particles[i].IsAnchor) anchorList.Add(i);
            else nonAnchorList.Add(i);
        }
        rig.AnchorIndices = anchorList.ToArray();
        rig.NonAnchorIndices = nonAnchorList.ToArray();

        return rig;
    }

    #region Optimized Collision Primitives & Precomputed Data
    private struct TorsoCapsuleData
    {
        public Vector3 SegA;
        public Vector3 SegB;
        public Vector3 Ab;
        public float AbLenSq;
        public Vector3 UNorm;
        public Vector3 NFwd;
        public Vector3 NSide;
        public float RadiusSide;
        public float RadiusFwd;
        public float ClothThickness;

        public float MinX;
        public float MaxX;
        public float MinY;
        public float MaxY;
        public float MinZ;
        public float MaxZ;
    }

    private static TorsoCapsuleData ComputeTorsoCapsuleData(
        Vector3 currPelvisPos,
        Vector3 currChestPos,
        Transform3D deltaPelvis,
        Transform3D deltaChest,
        float hipSpan,
        float modelFwdSign,
        float clothThickness = 0.015f)
    {
        Vector3 ab = currChestPos - currPelvisPos;
        float abLenSq = ab.LengthSquared();
        float abLen = abLenSq > 1e-6f ? Mathf.Sqrt(abLenSq) : 1e-3f;
        Vector3 uNorm = ab / abLen;

        Vector3 fwdPelvis = (deltaPelvis.Basis * new Vector3(0, 0, modelFwdSign)).Normalized();
        Vector3 fwdChest = (deltaChest.Basis * new Vector3(0, 0, modelFwdSign)).Normalized();
        Vector3 fwdAvg = (fwdPelvis + fwdChest).Normalized();

        Vector3 nFwd = fwdAvg - uNorm * uNorm.Dot(fwdAvg);
        if (nFwd.LengthSquared() > 1e-4f) nFwd = nFwd.Normalized();
        else nFwd = fwdPelvis;

        Vector3 sidePelvis = (deltaPelvis.Basis * Vector3.Right).Normalized();
        Vector3 sideChest = (deltaChest.Basis * Vector3.Right).Normalized();
        Vector3 sideAvg = (sidePelvis + sideChest).Normalized();

        Vector3 nSide = sideAvg - uNorm * uNorm.Dot(sideAvg);
        if (nSide.LengthSquared() > 1e-4f) nSide = nSide.Normalized();
        else nSide = sidePelvis;

        float rSide = Mathf.Clamp(hipSpan * 0.52f, 0.16f, 0.32f);
        float rFwd = Mathf.Clamp(rSide * 0.88f, 0.14f, 0.28f);

        float maxR = Mathf.Max(rSide, rFwd) + clothThickness + 0.05f;
        float minX = Mathf.Min(currPelvisPos.X, currChestPos.X) - maxR;
        float maxX = Mathf.Max(currPelvisPos.X, currChestPos.X) + maxR;
        float minY = Mathf.Min(currPelvisPos.Y, currChestPos.Y) - maxR;
        float maxY = Mathf.Max(currPelvisPos.Y, currChestPos.Y) + maxR;
        float minZ = Mathf.Min(currPelvisPos.Z, currChestPos.Z) - maxR;
        float maxZ = Mathf.Max(currPelvisPos.Z, currChestPos.Z) + maxR;

        return new TorsoCapsuleData
        {
            SegA = currPelvisPos,
            SegB = currChestPos,
            Ab = ab,
            AbLenSq = abLenSq,
            UNorm = uNorm,
            NFwd = nFwd,
            NSide = nSide,
            RadiusSide = rSide,
            RadiusFwd = rFwd,
            ClothThickness = clothThickness,
            MinX = minX,
            MaxX = maxX,
            MinY = minY,
            MaxY = maxY,
            MinZ = minZ,
            MaxZ = maxZ
        };
    }

    private static void ResolveTorsoCollision(ref Vector3 point, in TorsoCapsuleData data)
    {
        if (data.AbLenSq < 1e-6f) return;

        if (point.Y < data.MinY || point.Y > data.MaxY ||
            point.X < data.MinX || point.X > data.MaxX ||
            point.Z < data.MinZ || point.Z > data.MaxZ)
        {
            return;
        }

        float t = Mathf.Clamp((point - data.SegA).Dot(data.Ab) / data.AbLenSq, 0.0f, 1.0f);
        Vector3 center = data.SegA + data.Ab * t;
        Vector3 diff = point - center;

        float sideComp = diff.Dot(data.NSide);
        float fwdComp = diff.Dot(data.NFwd);

        float taper = Mathf.Lerp(1.05f, 0.95f, Mathf.Sin(t * Mathf.Pi));
        float rSideEff = (data.RadiusSide * taper) + data.ClothThickness;
        float rFwdEff = (data.RadiusFwd * taper) + data.ClothThickness;

        float normDistSq = (sideComp * sideComp) / (rSideEff * rSideEff) + (fwdComp * fwdComp) / (rFwdEff * rFwdEff);

        if (normDistSq < 1.0f)
        {
            float normDist = normDistSq > 1e-6f ? Mathf.Sqrt(normDistSq) : 1e-3f;
            float pushSide = sideComp / normDist;
            float pushFwd = fwdComp / normDist;

            float axisComp = diff.Dot(data.UNorm);
            point = center + data.NSide * pushSide + data.NFwd * pushFwd + data.UNorm * axisComp;
        }
    }

    private struct ThighCapsuleData
    {
        public Vector3 SegA;
        public Vector3 SegB;
        public Vector3 Ab;
        public float AbLenSq;
        public Vector3 UNorm;
        public Vector3 NFwd;
        public Vector3 NUp;
        public Vector3 NSide;
        public float Radius;
        public float ClothThickness;
        public float ThighFlex;

        public float MinX;
        public float MaxX;
        public float MinY;
        public float MaxY;
    }

    private static ThighCapsuleData ComputeThighCapsuleData(
        Vector3 segA, Vector3 segB,
        Quaternion rotThigh,
        bool isRight,
        float radius, float thickness,
        Transform3D deltaPelvis,
        float modelFwdSign)
    {
        Vector3 ab = segB - segA;
        float abLenSq = ab.LengthSquared();
        float abLen = abLenSq > 1e-6f ? Mathf.Sqrt(abLenSq) : 1e-3f;
        Vector3 uNorm = ab / abLen;

        Vector3 fwdThigh = rotThigh * new Vector3(0, 0, modelFwdSign);
        Vector3 nFwd = fwdThigh - uNorm * uNorm.Dot(fwdThigh);
        if (nFwd.LengthSquared() > 1e-4f) nFwd = nFwd.Normalized();
        else nFwd = (deltaPelvis.Basis * new Vector3(0, 0, modelFwdSign)).Normalized();

        Vector3 upThigh = rotThigh * Vector3.Up;
        Vector3 nUp = upThigh - uNorm * uNorm.Dot(upThigh);
        if (nUp.LengthSquared() > 1e-4f) nUp = nUp.Normalized();
        else nUp = (deltaPelvis.Basis * Vector3.Up).Normalized();

        Vector3 sideVec = isRight ? (deltaPelvis.Basis * Vector3.Left) : (deltaPelvis.Basis * Vector3.Right);
        Vector3 nSide = sideVec - uNorm * uNorm.Dot(sideVec);
        if (nSide.LengthSquared() > 1e-4f) nSide = nSide.Normalized();
        else nSide = isRight ? (deltaPelvis.Basis * Vector3.Left).Normalized() : (deltaPelvis.Basis * Vector3.Right).Normalized();

        float thighFlex = Mathf.Clamp(1.0f - Mathf.Max(0.0f, -uNorm.Y), 0.0f, 1.0f);

        float maxR = radius * 1.15f + thickness + 0.04f;
        float minX = Mathf.Min(segA.X, segB.X) - maxR;
        float maxX = Mathf.Max(segA.X, segB.X) + maxR;
        float minY = Mathf.Min(segA.Y, segB.Y) - maxR;
        float maxY = Mathf.Max(segA.Y, segB.Y) + maxR;

        return new ThighCapsuleData
        {
            SegA = segA,
            SegB = segB,
            Ab = ab,
            AbLenSq = abLenSq,
            UNorm = uNorm,
            NFwd = nFwd,
            NUp = nUp,
            NSide = nSide,
            Radius = radius,
            ClothThickness = thickness,
            ThighFlex = thighFlex,
            MinX = minX,
            MaxX = maxX,
            MinY = minY,
            MaxY = maxY
        };
    }

    private static void ResolveThighCapsuleCollisionRadial(
        ref Vector3 point,
        bool isAnterior,
        in ThighCapsuleData data)
    {
        if (data.AbLenSq < 1e-6f) return;
        // Fast AABB broadphase early-out
        if (point.Y < data.MinY || point.Y > data.MaxY || point.X < data.MinX || point.X > data.MaxX) return;

        float t = Mathf.Clamp((point - data.SegA).Dot(data.Ab) / data.AbLenSq, 0.0f, 1.0f);
        Vector3 closest = data.SegA + data.Ab * t;

        Vector3 diff = point - closest;
        float distSq = diff.LengthSquared();
        float effectiveRadius = data.Radius * Mathf.Lerp(1.12f, 1.0f, t) + data.ClothThickness;

        if (distSq >= effectiveRadius * effectiveRadius) return;

        float dist = Mathf.Sqrt(distSq);

        Vector3 radialDir;
        if (dist > 1e-4f)
        {
            radialDir = diff / dist;
        }
        else
        {
            radialDir = isAnterior ? (data.NFwd * 0.7f + data.NSide * 0.7f).Normalized() : data.NSide;
        }

        if (isAnterior)
        {
            float fwdComp = radialDir.Dot(data.NFwd);
            float sideComp = radialDir.Dot(data.NSide);

            if (data.ThighFlex > 0.15f)
            {
                float upComp = radialDir.Dot(data.NUp);
                if (fwdComp < 0.12f || upComp < -0.10f)
                {
                    float targetFwd = Mathf.Max(0.20f, fwdComp > 0 ? fwdComp : -fwdComp * 0.5f + 0.35f);
                    float targetUp = Mathf.Max(0.15f, upComp > 0 ? upComp : -upComp * 0.5f + 0.25f);
                    radialDir = (data.NFwd * targetFwd + data.NUp * targetUp + data.NSide * sideComp).Normalized();
                }
            }
            else
            {
                if (fwdComp < 0.15f)
                {
                    float targetFwd = Mathf.Max(0.25f, -fwdComp + 0.30f);
                    radialDir = (data.NFwd * targetFwd + data.NSide * sideComp).Normalized();
                }
            }
        }

        point = closest + radialDir * effectiveRadius;
    }

    private struct CalfCapsuleData
    {
        public Vector3 SegA;
        public Vector3 SegB;
        public Vector3 Ab;
        public float AbLenSq;
        public float EffRadius;
        public float EffRadiusSq;
        public Vector3 FallbackDir;

        public float MinX;
        public float MaxX;
        public float MinY;
        public float MaxY;
    }

    private static CalfCapsuleData ComputeCalfCapsuleData(
        Vector3 segA, Vector3 segB,
        float radius, float thickness,
        Vector3 fallbackDir)
    {
        Vector3 ab = segB - segA;
        float abLenSq = ab.LengthSquared();
        float effRadius = radius + thickness;
        float maxR = effRadius + 0.04f;
        float minX = Mathf.Min(segA.X, segB.X) - maxR;
        float maxX = Mathf.Max(segA.X, segB.X) + maxR;
        float minY = Mathf.Min(segA.Y, segB.Y) - maxR;
        float maxY = Mathf.Max(segA.Y, segB.Y) + maxR;

        return new CalfCapsuleData
        {
            SegA = segA,
            SegB = segB,
            Ab = ab,
            AbLenSq = abLenSq,
            EffRadius = effRadius,
            EffRadiusSq = effRadius * effRadius,
            FallbackDir = fallbackDir,
            MinX = minX,
            MaxX = maxX,
            MinY = minY,
            MaxY = maxY
        };
    }

    private static void ResolveCalfCapsuleCollisionRadial(
        ref Vector3 point,
        in CalfCapsuleData data,
        bool isAnterior = false)
    {
        if (data.AbLenSq < 1e-6f) return;
        // Fast AABB broadphase early-out
        if (point.Y < data.MinY || point.Y > data.MaxY || point.X < data.MinX || point.X > data.MaxX) return;

        float t = Mathf.Clamp((point - data.SegA).Dot(data.Ab) / data.AbLenSq, 0.0f, 1.0f);
        Vector3 closest = data.SegA + data.Ab * t;

        Vector3 diff = point - closest;
        float distSq = diff.LengthSquared();

        if (distSq >= data.EffRadiusSq) return;

        float dist = Mathf.Sqrt(distSq);
        Vector3 radialDir = (dist > 1e-4f) ? (diff / dist) : data.FallbackDir;
        if (isAnterior && radialDir.Dot(data.FallbackDir) < 0.15f)
        {
            radialDir = (radialDir + data.FallbackDir * (0.35f - radialDir.Dot(data.FallbackDir))).Normalized();
        }
        point = closest + radialDir.Normalized() * data.EffRadius;
    }

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

        return Mathf.Clamp((0.95f - dotStraight) / 1.45f, 0.0f, 1.0f);
    }

    private struct KneeApexData
    {
        public Vector3 CurrKnee;
        public Vector3 KneeCenter;
        public Vector3 KneeFwd;
        public Vector3 NSide;
        public float EffRadius;
        public float EffRadiusSq;
        public float FlexRatio;
        public bool IsValid;

        public float MinX;
        public float MaxX;
        public float MinY;
        public float MaxY;
    }

    private static KneeApexData ComputeKneeApexData(
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
        if (lenThighSq < 1e-6f || lenCalfSq < 1e-6f)
            return new KneeApexData { IsValid = false };

        float flexRatio = ComputeKneeFlexRatio(currHip, currKnee, currAnkle);
        float rKnee = Mathf.Lerp(0.11f, 0.15f, flexRatio);
        float effRadius = rKnee + clothThickness;

        Vector3 uThigh = vThigh / Mathf.Sqrt(lenThighSq);
        Vector3 uCalf = vCalf / Mathf.Sqrt(lenCalfSq);

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

        Vector3 sideVec = isRight ? (deltaPelvis.Basis * Vector3.Left) : (deltaPelvis.Basis * Vector3.Right);
        Vector3 nSide = sideVec - uThigh * uThigh.Dot(sideVec);
        if (nSide.LengthSquared() > 1e-4f) nSide = nSide.Normalized();
        else nSide = isRight ? (deltaPelvis.Basis * Vector3.Left).Normalized() : (deltaPelvis.Basis * Vector3.Right).Normalized();

        Vector3 kneeCenter = currKnee + kneeFwd * (flexRatio * 0.07f) + Vector3.Up * (flexRatio * 0.02f);

        float maxR = effRadius + 0.08f;
        float minX = kneeCenter.X - maxR;
        float maxX = kneeCenter.X + maxR;
        float minY = kneeCenter.Y - maxR;
        float maxY = kneeCenter.Y + maxR;

        return new KneeApexData
        {
            CurrKnee = currKnee,
            KneeCenter = kneeCenter,
            KneeFwd = kneeFwd,
            NSide = nSide,
            EffRadius = effRadius,
            EffRadiusSq = effRadius * effRadius,
            FlexRatio = flexRatio,
            IsValid = true,
            MinX = minX,
            MaxX = maxX,
            MinY = minY,
            MaxY = maxY
        };
    }

    private static void ResolveKneeApexSphereCollision(
        ref Vector3 point,
        bool isAnterior,
        in KneeApexData data)
    {
        if (!data.IsValid) return;
        // Fast AABB broadphase early-out
        if (point.Y < data.MinY || point.Y > data.MaxY || point.X < data.MinX || point.X > data.MaxX) return;

        Vector3 diff = point - data.KneeCenter;
        float distSq = diff.LengthSquared();

        if (distSq < data.EffRadiusSq)
        {
            float dist = Mathf.Sqrt(distSq);
            Vector3 pushDir = (dist > 1e-4f) ? (diff / dist) : (data.KneeFwd * 0.7f + Vector3.Up * 0.7f).Normalized();

            if (isAnterior)
            {
                float fwdDot = pushDir.Dot(data.KneeFwd);
                float upDot = pushDir.Dot(Vector3.Up);
                float sideDot = pushDir.Dot(data.NSide);

                if (data.FlexRatio > 0.20f)
                {
                    float targetFwd = Mathf.Max(0.55f, fwdDot > 0 ? fwdDot : 0.55f);
                    float targetUp = Mathf.Max(0.15f, upDot > 0 ? upDot : 0.15f);
                    pushDir = (data.KneeFwd * targetFwd + Vector3.Up * targetUp + data.NSide * sideDot).Normalized();
                }
                else if (fwdDot < 0.20f || upDot < -0.10f)
                {
                    float targetFwd = Mathf.Max(0.35f, fwdDot > 0 ? fwdDot : -fwdDot + 0.40f);
                    float targetUp = Mathf.Max(0.25f, upDot > 0 ? upDot : -upDot + 0.30f);
                    pushDir = (data.KneeFwd * targetFwd + Vector3.Up * targetUp + data.NSide * sideDot).Normalized();
                }
            }

            point = data.KneeCenter + pushDir * data.EffRadius;
        }

        // Robust Convex Knee Apex Projection: smoothly push anterior particles forward outside patella dome
        if (isAnterior && data.FlexRatio > 0.15f)
        {
            Vector3 dKnee = point - data.CurrKnee;
            if (Mathf.Abs(dKnee.Y) < 0.22f)
            {
                float latDist = dKnee.Dot(data.NSide);
                float fwdDist = dKnee.Dot(data.KneeFwd);
                float yDist = dKnee.Y;

                float latFactor = Mathf.Clamp(1.0f - (latDist * latDist) / ((data.EffRadius * 1.35f) * (data.EffRadius * 1.35f)), 0.0f, 1.0f);
                float yFactor = Mathf.Clamp(1.0f - (yDist * yDist) / (0.22f * 0.22f), 0.0f, 1.0f);
                float domeFactor = latFactor * yFactor * data.FlexRatio;

                if (domeFactor > 0.01f)
                {
                    if (fwdDist < data.EffRadius * 0.55f)
                    {
                        float pushFwd = (data.EffRadius * 0.55f - fwdDist) * domeFactor;
                        point += data.KneeFwd * pushFwd;
                    }
                    float targetY = data.CurrKnee.Y + 0.05f * domeFactor;
                    if (point.Y < targetY && fwdDist > -0.02f)
                    {
                        point.Y = Mathf.Lerp(point.Y, targetY, 0.65f * domeFactor);
                    }
                }
            }
        }
    }
    #endregion

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
        Transform3D deltaChest,
        Quaternion rotChest,
        Vector3 currChestPos,
        Vector3 restChestPos,
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

        float hipSpan = (currHipL - currHipR).Length();
        var torsoData = ComputeTorsoCapsuleData(currPelvisPos, currChestPos, deltaPelvis, deltaChest, hipSpan, modelFwdSign, clothThickness);

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

        // 1. Kinematic Warm Start
        for (int k = 0; k < pCount; k++)
        {
            var p = rig.Particles[k];
            if (p.IsAnchor)
            {
                if (p.IsTorso)
                {
                    float s = p.TorsoT * p.TorsoT * (3.0f - 2.0f * p.TorsoT);
                    Quaternion rotTorsoAnchor = rotPelvis.Slerp(rotChest, s);
                    Vector3 spineCenter = currPelvisPos.Lerp(currChestPos, p.TorsoT);
                    pos[k] = spineCenter + (rotTorsoAnchor * p.RestSpineOffset);
                }
                else
                {
                    pos[k] = currPelvisPos + (deltaPelvis.Basis * (p.RestPosition - restPelvisPos));
                }
                continue;
            }

            if (p.IsTorso)
            {
                // Smooth Hermite spine curve interpolation
                float s = p.TorsoT * p.TorsoT * (3.0f - 2.0f * p.TorsoT);
                Quaternion rotTorso = rotPelvis.Slerp(rotChest, s);
                Vector3 spineCenter = currPelvisPos.Lerp(currChestPos, p.TorsoT);
                Vector3 seed = spineCenter + (rotTorso * p.RestSpineOffset);
                ResolveTorsoCollision(ref seed, in torsoData);
                pos[k] = seed;
                continue;
            }

            Vector3 pRest = p.RestPosition;

            // Single-pivot hierarchical kinematic placement for Right Leg:
            Vector3 pLegR;
            if (p.ZoneR == 0)
            {
                pLegR = currHipR + (new Basis(rotThighR) * (pRest - restHipR));
            }
            else if (p.ZoneR == 1)
            {
                pLegR = currKneeR + (new Basis(rotKneeR) * (pRest - restKneeR));
            }
            else
            {
                Quaternion rotBlendR = rotThighR.Slerp(rotKneeR, p.SmoothUR);
                pLegR = currKneeR + (new Basis(rotBlendR) * (pRest - restKneeR));
            }

            // Single-pivot hierarchical kinematic placement for Left Leg:
            Vector3 pLegL;
            if (p.ZoneL == 0)
            {
                pLegL = currHipL + (new Basis(rotThighL) * (pRest - restHipL));
            }
            else if (p.ZoneL == 1)
            {
                pLegL = currKneeL + (new Basis(rotKneeL) * (pRest - restKneeL));
            }
            else
            {
                Quaternion rotBlendL = rotThighL.Slerp(rotKneeL, p.SmoothUL);
                pLegL = currKneeL + (new Basis(rotBlendL) * (pRest - restKneeL));
            }

            Vector3 pLeg = pLegR * p.WeightR + pLegL * p.WeightL;
            float flexRatioBilateral = Mathf.Lerp(flexRatioR, flexRatioL, p.WeightL);
            float rearDrape = Mathf.Clamp(flexRatioBilateral * 1.25f, 0.0f, 1.0f);
            float wRear = (1.0f - rearDrape) * 0.85f;
            float wLeg = Mathf.Lerp(wRear, 1.0f, p.FwdFactor);

            Vector3 pBasePelvis = currPelvisPos + (deltaPelvis.Basis * (pRest - restPelvisPos));
            Vector3 seedLeg = pBasePelvis.Lerp(pLeg, wLeg);

            if (seedLeg.Y < groundY) seedLeg.Y = groundY;
            pos[k] = seedLeg;
        }

        // 2. Optimized Relaxation Iterations (14 iterations with precalculated limb frames)
        Vector3 fallbackCalfDir = (deltaPelvis.Basis * new Vector3(0, 0, modelFwdSign)).Normalized();

        // Precalculate limb capsule and apex collision geometry ONCE before relaxation iterations
        var thighDataR = ComputeThighCapsuleData(currHipR, currKneeR, rotThighR, true, rThigh, clothThickness, deltaPelvis, modelFwdSign);
        var thighDataL = ComputeThighCapsuleData(currHipL, currKneeL, rotThighL, false, rThigh, clothThickness, deltaPelvis, modelFwdSign);
        var kneeApexDataR = ComputeKneeApexData(currHipR, currKneeR, currAnkleR, rotThighR, true, clothThickness, deltaPelvis, modelFwdSign);
        var kneeApexDataL = ComputeKneeApexData(currHipL, currKneeL, currAnkleL, rotThighL, false, clothThickness, deltaPelvis, modelFwdSign);
        var calfDataR = ComputeCalfCapsuleData(currKneeR, currAnkleR, rCalf, clothThickness, fallbackCalfDir);
        var calfDataL = ComputeCalfCapsuleData(currKneeL, currAnkleL, rCalf, clothThickness, fallbackCalfDir);

        int nonAnchorCount = rig.NonAnchorIndices.Length;
        int anchorCount = rig.AnchorIndices.Length;
        int cCount = rig.Constraints.Length;

        for (int iter = 0; iter < 14; iter++)
        {
            // A. Gravity / Drape on non-anchor particles:
            for (int i = 0; i < nonAnchorCount; i++)
            {
                int k = rig.NonAnchorIndices[i];
                var p = rig.Particles[k];

                if (p.IsTorso)
                {
                    pos[k].Y -= 0.0012f;
                    continue;
                }

                bool isSupportedOnLimb = false;
                if (p.IsAnterior)
                {
                    // Check right thigh upper surface
                    if (pos[k].Y >= thighDataR.MinY && pos[k].Y <= thighDataR.MaxY && thighDataR.AbLenSq > 1e-4f)
                    {
                        float tR = Mathf.Clamp((pos[k] - currHipR).Dot(vThighR) / thighDataR.AbLenSq, 0f, 1f);
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
                    if (!isSupportedOnLimb && pos[k].Y >= thighDataL.MinY && pos[k].Y <= thighDataL.MaxY && thighDataL.AbLenSq > 1e-4f)
                    {
                        float tL = Mathf.Clamp((pos[k] - currHipL).Dot(vThighL) / thighDataL.AbLenSq, 0f, 1f);
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

                    // Check knee apex upper surface
                    if (!isSupportedOnLimb && kneeApexDataR.IsValid && pos[k].Y >= kneeApexDataR.MinY && pos[k].Y <= kneeApexDataR.MaxY)
                    {
                        float distSqKneeR = (pos[k] - kneeApexDataR.KneeCenter).LengthSquared();
                        if (distSqKneeR >= (kneeApexDataR.EffRadius - 0.02f) * (kneeApexDataR.EffRadius - 0.02f) &&
                            distSqKneeR <= (kneeApexDataR.EffRadius + 0.03f) * (kneeApexDataR.EffRadius + 0.03f) &&
                            pos[k].Y >= kneeApexDataR.KneeCenter.Y - 0.01f)
                        {
                            isSupportedOnLimb = true;
                        }
                    }
                    if (!isSupportedOnLimb && kneeApexDataL.IsValid && pos[k].Y >= kneeApexDataL.MinY && pos[k].Y <= kneeApexDataL.MaxY)
                    {
                        float distSqKneeL = (pos[k] - kneeApexDataL.KneeCenter).LengthSquared();
                        if (distSqKneeL >= (kneeApexDataL.EffRadius - 0.02f) * (kneeApexDataL.EffRadius - 0.02f) &&
                            distSqKneeL <= (kneeApexDataL.EffRadius + 0.03f) * (kneeApexDataL.EffRadius + 0.03f) &&
                            pos[k].Y >= kneeApexDataL.KneeCenter.Y - 0.01f)
                        {
                            isSupportedOnLimb = true;
                        }
                    }
                }

                if (!isSupportedOnLimb)
                {
                    pos[k].Y -= p.Grav;
                }
            }

            // B. Unilateral Structural Constraints (Stretch Limiting) - EXECUTED BEFORE COLLISIONS
            for (int c = 0; c < cCount; c++)
            {
                ref var constraint = ref rig.Constraints[c];
                int idxA = constraint.ParticleA;
                int idxB = constraint.ParticleB;

                Vector3 delta = pos[idxA] - pos[idxB];
                float distSq = delta.LengthSquared();

                // Fast early-out: if within max allowed stretch distance, skip sqrt and correction entirely!
                if (distSq > constraint.MaxDistSq)
                {
                    float dist = Mathf.Sqrt(distSq);
                    float diff = (dist - constraint.MaxDist) / dist;
                    Vector3 correction = delta * (diff * constraint.Stiffness);

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

            // C. 3D Radial Capsule & Torso Collisions (Interleaved for high FPS performance)
            bool runCollisions = (iter % 2 == 1) || (iter == 13);
            if (runCollisions)
            {
                for (int i = 0; i < nonAnchorCount; i++)
                {
                    int k = rig.NonAnchorIndices[i];
                    ref Vector3 pt = ref pos[k];
                    var p = rig.Particles[k];

                    if (p.IsTorso)
                    {
                        ResolveTorsoCollision(ref pt, in torsoData);
                        if (p.TorsoT < 0.35f)
                        {
                            ResolveThighCapsuleCollisionRadial(ref pt, p.IsAnterior, in thighDataR);
                            ResolveThighCapsuleCollisionRadial(ref pt, p.IsAnterior, in thighDataL);
                        }
                        continue;
                    }

                    // Thigh capsules
                    ResolveThighCapsuleCollisionRadial(ref pt, p.IsAnterior, in thighDataR);
                    ResolveThighCapsuleCollisionRadial(ref pt, p.IsAnterior, in thighDataL);

                    // Knee apex sphere & Patella Tent elevation
                    ResolveKneeApexSphereCollision(ref pt, p.IsAnterior, in kneeApexDataR);
                    ResolveKneeApexSphereCollision(ref pt, p.IsAnterior, in kneeApexDataL);

                    // Calf capsules
                    ResolveCalfCapsuleCollisionRadial(ref pt, in calfDataR, p.IsAnterior);
                    ResolveCalfCapsuleCollisionRadial(ref pt, in calfDataL, p.IsAnterior);
                }
            }

            // D. Dynamic Ground Plane Collision & Horizontal Pooling
            for (int i = 0; i < nonAnchorCount; i++)
            {
                int k = rig.NonAnchorIndices[i];
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

            // E. Anchor Pinning: Re-enforce rigid anchor positions
            for (int i = 0; i < anchorCount; i++)
            {
                int k = rig.AnchorIndices[i];
                var p = rig.Particles[k];
                if (p.IsTorso)
                {
                    float s = p.TorsoT * p.TorsoT * (3.0f - 2.0f * p.TorsoT);
                    Quaternion rotTorsoAnchor = rotPelvis.Slerp(rotChest, s);
                    Vector3 spineCenter = currPelvisPos.Lerp(currChestPos, p.TorsoT);
                    pos[k] = spineCenter + (rotTorsoAnchor * p.RestSpineOffset);
                }
                else
                {
                    pos[k] = currPelvisPos + (deltaPelvis.Basis * (rig.Particles[k].RestPosition - restPelvisPos));
                }
            }
        }

        // 3. Dynamic Skinning Orientation with Lateral Bilateral Blending and Spine Blending
        for (int k = 0; k < pCount; k++)
        {
            var p = rig.Particles[k];
            if (p.IsTorso)
            {
                float s = p.TorsoT * p.TorsoT * (3.0f - 2.0f * p.TorsoT);
                Quaternion rotTorso = rotPelvis.Slerp(rotChest, s);
                skeleton.SetBonePosePosition(p.BoneIndex, pos[k]);
                skeleton.SetBonePoseRotation(p.BoneIndex, rotTorso * p.RestRotation);
                continue;
            }

            if (p.IsAnchor)
            {
                skeleton.SetBonePosePosition(p.BoneIndex, pos[k]);
                skeleton.SetBonePoseRotation(p.BoneIndex, rotPelvis * p.RestRotation);
                continue;
            }

            Vector3 pRest = p.RestPosition;
            float weightL = p.WeightL;

            Quaternion rotThighBilateral = rotThighR.Slerp(rotThighL, weightL);
            Quaternion rotCalfBilateral = rotKneeR.Slerp(rotKneeL, weightL);

            float fwdFactor = p.FwdFactor;

            // Displacement from pure pelvis drape
            Vector3 pBasePelvis = currPelvisPos + (deltaPelvis.Basis * (pRest - restPelvisPos));
            float dispFromPelvis = (pos[k] - pBasePelvis).Length();
            float dispFactor = Mathf.Clamp(dispFromPelvis / 0.15f, 0.0f, 1.0f);

            // Dynamic rotation blend factor (up to 85% thigh tracking for forward and displaced particles)
            float rotBlend = Mathf.Clamp(Mathf.Max(fwdFactor * 0.80f, dispFactor * 0.80f), 0.0f, 0.85f);

            // Calf rotation blend factor: smooth Hermite curve over 30cm knee envelope
            float flexRatioBilateral = Mathf.Lerp(flexRatioR, flexRatioL, weightL);
            float belowKnee = p.RestKneeY - pRest.Y;
            const float skinZoneHalf = 0.15f;
            float uKnee = Mathf.Clamp((belowKnee + skinZoneHalf) / (2.0f * skinZoneHalf), 0.0f, 1.0f);
            float heightFactor = uKnee * uKnee * (3.0f - 2.0f * uKnee);
            float calfBlend = Mathf.Clamp(heightFactor * flexRatioBilateral, 0.0f, 1.0f);

            // If tail particle is resting on or pooled on the ground, keep it aligned upright with pelvis so fabric lies flat
            bool isOnGround = pos[k].Y <= groundY + 0.015f;
            if (isOnGround && p.DiffZ < 0)
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

    private static readonly HashSet<ulong> _twoSidedEnforcedRoots = new();

    /// <summary>
    /// Enforces two-sided rendering (CullMode = Disabled) on character clothing materials
    /// so inner fabric and backfaces remain fully visible from all camera angles.
    /// Cached to run only once per instance to avoid expensive scene-tree traversals every frame.
    /// </summary>
    public static void EnforceClothTwoSidedMaterials(Node characterRoot, bool force = false)
    {
        if (characterRoot == null) return;
        ulong instanceId = characterRoot.GetInstanceId();
        if (!force && _twoSidedEnforcedRoots.Contains(instanceId))
        {
            return;
        }

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
        _twoSidedEnforcedRoots.Add(instanceId);
    }

    /// <summary>
    /// Safe no-op retained for backwards compatibility.
    /// Modifying BoneParent or BoneRest corrupts Godot's MeshInstance3D Skin inverse bind matrices.
    /// Kinematic clothing motion is handled safely via SetBonePosePosition/Rotation in ConformProceduralClothPoses.
    /// </summary>
    public static void EnsureClothingBonesParented(Skeleton3D skeleton, bool force = false)
    {
    }

    private static readonly Dictionary<string, HashSet<string>> _keyedBonesByAnim = new();

    private static bool IsBoneKeyedInAnimation(AnimationPlayer animPlayer, string boneName)
    {
        if (animPlayer == null || string.IsNullOrEmpty(animPlayer.CurrentAnimation))
            return false;

        string currentAnim = animPlayer.CurrentAnimation;
        if (!_keyedBonesByAnim.TryGetValue(currentAnim, out var keyedSet))
        {
            keyedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (animPlayer.HasAnimation(currentAnim))
            {
                var anim = animPlayer.GetAnimation(currentAnim);
                if (anim != null)
                {
                    int trackCount = anim.GetTrackCount();
                    for (int t = 0; t < trackCount; t++)
                    {
                        string path = anim.TrackGetPath(t).ToString();
                        int colonIdx = path.LastIndexOf(':');
                        string target = colonIdx != -1 ? path.Substring(colonIdx + 1) : path;
                        keyedSet.Add(target);
                    }
                }
            }
            _keyedBonesByAnim[currentAnim] = keyedSet;
        }

        return keyedSet.Contains(boneName);
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
    /// Also aligns parented anterior/posterior slit flaps (dress_cut_front_*, dress_out_front_*),
    /// and ensures unkeyed kinematic clothing bones follow body FK motion seamlessly.
    /// </summary>
    public static void ConformProceduralClothPoses(Skeleton3D skeleton, AnimationPlayer animPlayer = null)
    {
        if (skeleton == null) return;
        skeleton.ForceUpdateAllBoneTransforms();

        int boneCount = skeleton.GetBoneCount();
        if (boneCount == 0) return;

        // Auto-parent unparented kinematic clothing bones to chest/pelvis (cached, runs once per skeleton)
        EnsureClothingBonesParented(skeleton);

        // Ensure two-sided rendering on character clothing so inner faces are never culled (cached, runs once per root)
        EnforceClothTwoSidedMaterials(skeleton);

        // Build or reuse cached ClothSolverRig for this skeleton
        if (_clothSolverRig == null || _clothSolverRig.Skeleton != skeleton || _clothSolverRig.BoneCount != boneCount)
        {
            _clothSolverRig = BuildClothSolverRig(skeleton);
        }

        var rig = _clothSolverRig;
        int pelvisIdx = rig.PelvisIdx;
        if (pelvisIdx == -1) return;

        int chestIdx = rig.ChestIdx;
        int legLIdx = rig.LegLIdx;
        int legRIdx = rig.LegRIdx;
        int twistLIdx = rig.TwistLIdx;
        int twistRIdx = rig.TwistRIdx;
        int kneeLIdx = rig.KneeLIdx;
        int kneeRIdx = rig.KneeRIdx;
        int ankleLIdx = rig.AnkleLIdx;
        int ankleRIdx = rig.AnkleRIdx;
        int ballLIdx = rig.BallLIdx;
        int ballRIdx = rig.BallRIdx;
        float modelFwdSign = rig.ModelFwdSign;

        // 2. Matrices y transformaciones globales de Pelvis y Torso
        Transform3D restPelvis = skeleton.GetBoneGlobalRest(pelvisIdx);
        Transform3D currPelvis = skeleton.GetBoneGlobalPose(pelvisIdx);
        Transform3D deltaPelvis = currPelvis * restPelvis.AffineInverse();
        Quaternion rotPelvis = deltaPelvis.Basis.GetRotationQuaternion();

        Vector3 restPelvisPos = restPelvis.Origin;
        Vector3 currPelvisPos = currPelvis.Origin;

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

        // 5. Ajustar solapas emparentadas usando lista preclasificada (0 alocaciones por frame)
        if (rig.FlapBones != null)
        {
            for (int f = 0; f < rig.FlapBones.Length; f++)
            {
                ref var flap = ref rig.FlapBones[f];
                int i = flap.BoneIndex;
                int parentIdx = flap.ParentIndex;

                if (flap.ParentIsFlap)
                {
                    skeleton.SetBonePoseRotation(i, skeleton.GetBoneRest(i).Basis.GetRotationQuaternion());
                }
                else
                {
                    Quaternion targetLegRot = flap.IsRight ? rotThighR : rotThighL;
                    float trackWeight = flap.IsFlapFront ? SkirtFlapTracking : (SkirtFlapTracking * 0.45f);
                    Quaternion flapDeltaRot = rotPelvis.Slerp(targetLegRot, trackWeight);

                    Transform3D restFlapGlobal = flap.RestFlapGlobal;
                    Transform3D currParent = skeleton.GetBoneGlobalPose(parentIdx);

                    Quaternion targetGlobalRot = flapDeltaRot * restFlapGlobal.Basis.GetRotationQuaternion();
                    Quaternion localPoseRot = currParent.Basis.GetRotationQuaternion().Inverse() * targetGlobalRot;

                    skeleton.SetBonePoseRotation(i, localPoseRot);
                }
            }
        }

        // Kinematic clothing bones fallback (if unparented or under root, follow chest/pelvis FK motion)
        int rootIdx = FindFirstBone(skeleton, "root");
        if (rig.KinematicClothingBones != null)
        {
            for (int c = 0; c < rig.KinematicClothingBones.Length; c++)
            {
                ref var kcb = ref rig.KinematicClothingBones[c];
                int i = kcb.BoneIndex;

                // If already parented in skeleton hierarchy to a valid anatomical parent (not root), Godot FK moves it with parent!
                int parentIdx = skeleton.GetBoneParent(i);
                if (parentIdx != -1 && parentIdx != rootIdx) continue;

                // If keyed in active Source 2 animation clip, preserve authored keyframes!
                if (IsBoneKeyedInAnimation(animPlayer, kcb.BoneName)) continue;

                Vector3 pRest = kcb.RestCloth.Origin;
                Quaternion restClothRot = kcb.RestCloth.Basis.GetRotationQuaternion();

                if (kcb.IsUpper)
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
        }

        // 6. Ejecutar el Solver PBD para todas las partículas de falda/vestido/abrigo ($cloth* / cloth_*)
        if (rig.Particles != null && rig.Particles.Length > 0)
        {
            SolveClothPBD(skeleton, deltaPelvis, rotPelvis, currPelvisPos, restPelvisPos,
                          deltaChest, rotChest, currChestPos, restChestPos,
                          restHipR, currHipR, rotThighR, restKneeR, currKneeR, rotKneeR, currAnkleR,
                          restHipL, currHipL, rotThighL, restKneeL, currKneeL, rotKneeL, currAnkleL,
                          modelFwdSign, groundY);
        }

        skeleton.ForceUpdateAllBoneTransforms();
    }

}
