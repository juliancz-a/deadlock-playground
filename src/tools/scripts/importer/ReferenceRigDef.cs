using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace DeadlockPlayground.Importer
{
    public enum ReferenceBoneRole
    {
        Core = 0,
        Finger = 1,
        Cloth = 2
    }

    public enum ReferenceFingerRigMode
    {
        RigidMitten = 0,
        ArticulatedFingers = 1
    }

    public sealed class ReferenceClothChainDef
    {
        public string ChainId { get; set; } = "";
        public string Name { get; set; } = "cloth_chain";
        public string ParentBoneName { get; set; } = "spine_3";
        public string MirrorPairId { get; set; } = "";
        /// <summary>Ordered joint positions in model space (root -> tip).</summary>
        public List<Vector3> JointPositions { get; set; } = new();

        public ReferenceClothChainDef Clone() => new()
        {
            ChainId = ChainId,
            Name = Name,
            ParentBoneName = ParentBoneName,
            MirrorPairId = MirrorPairId,
            JointPositions = JointPositions != null ? new List<Vector3>(JointPositions) : new()
        };
    }

    /// <summary>
    /// Modular Auto-Rig Pro–style reference skeleton definition.
    /// Stores limb toggles (spine count 1–4, neck, clavicles, toes, per-finger toggles &amp; phalanx count),
    /// editable 3D joint positions in model space, and authored cloth bone chains.
    /// Single source of truth for the 3D "Edit Reference Bones" viewport mode, the modular geodesic weight
    /// solver (<see cref="CustomLandmarkWeightSolver"/>), and <c>.dpp</c> workspace persistence.
    /// </summary>
    public sealed class ReferenceRigDef
    {
        public static readonly string[] FingerNames = { "thumb", "index", "middle", "ring", "pinky" };

        // Modular limb configuration
        public int SpineCount { get; set; } = 4; // 1..4 (auto-mapped onto Deadlock spine_0..3)
        public bool HasNeck { get; set; } = true;
        public bool HasClavicles { get; set; } = true;
        public bool HasToes { get; set; } = false;

        // Hands & Fingers configuration
        public ReferenceFingerRigMode FingerMode { get; set; } = ReferenceFingerRigMode.RigidMitten;
        public bool EnableThumb { get; set; } = true;
        public bool EnableIndex { get; set; } = true;
        public bool EnableMiddle { get; set; } = true;
        public bool EnableRing { get; set; } = true;
        public bool EnablePinky { get; set; } = true;
        public int PhalanxCount { get; set; } = 3; // 2 or 3

        /// <summary>
        /// Editable 3D joint positions in imported model space (core pins + finger joints).
        /// Core pin keys match <see cref="CustomLandmarkWeightSolver.PinNames"/> plus optional
        /// <c>ToeL</c>/<c>ToeR</c> and finger keys <c>finger_{name}_{0..3}_{L|R}</c>.
        /// </summary>
        public Dictionary<string, Vector3> Joints { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>User-authored cloth bone chains attached to any reference bone.</summary>
        public List<ReferenceClothChainDef> ClothChains { get; set; } = new();

        public bool IsFingerEnabled(string fingerName) => (fingerName ?? "").ToLowerInvariant() switch
        {
            "thumb" => EnableThumb,
            "index" => EnableIndex,
            "middle" => EnableMiddle,
            "ring" => EnableRing,
            "pinky" => EnablePinky,
            _ => false
        };

        /// <summary>
        /// Maps modular spine count (1–4) onto Deadlock's canonical <c>spine_0..3</c> hierarchy so
        /// upper-torso aim/twist animations always drive the chest/arms cleanly even on short/cartoon characters.
        /// </summary>
        public static string[] GetDeadlockSpineBoneNames(int spineCount) => Math.Clamp(spineCount, 1, 4) switch
        {
            1 => new[] { "spine_2" },
            2 => new[] { "spine_0", "spine_3" },
            3 => new[] { "spine_0", "spine_2", "spine_3" },
            _ => new[] { "spine_0", "spine_1", "spine_2", "spine_3" }
        };

        public static string MirrorJointKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (key.EndsWith("_L", StringComparison.Ordinal)) return key[..^2] + "_R";
            if (key.EndsWith("_R", StringComparison.Ordinal)) return key[..^2] + "_L";
            if (key.EndsWith("L", StringComparison.Ordinal) && key.Length > 1 && char.IsLower(key[^2]))
                return key[..^1] + "R";
            if (key.EndsWith("R", StringComparison.Ordinal) && key.Length > 1 && char.IsLower(key[^2]))
                return key[..^1] + "L";
            return null;
        }

        public ReferenceRigDef Clone() => new()
        {
            SpineCount = SpineCount,
            HasNeck = HasNeck,
            HasClavicles = HasClavicles,
            HasToes = HasToes,
            FingerMode = FingerMode,
            EnableThumb = EnableThumb,
            EnableIndex = EnableIndex,
            EnableMiddle = EnableMiddle,
            EnableRing = EnableRing,
            EnablePinky = EnablePinky,
            PhalanxCount = PhalanxCount,
            Joints = new Dictionary<string, Vector3>(Joints, StringComparer.OrdinalIgnoreCase),
            ClothChains = ClothChains?.Select(c => c.Clone()).ToList() ?? new()
        };

        /// <summary>
        /// Ensures all required core, toe, and enabled finger joint keys exist in <see cref="Joints"/>,
        /// estimating missing finger/toe positions smoothly from existing wrist/elbow/ankle joints.
        /// </summary>
        public void EnsureDefaultJoints(float approxHeight = 1.7f)
        {
            float H = MathF.Max(0.2f, approxHeight);
            if (Joints.TryGetValue("Head", out var hp) && Joints.TryGetValue("AnkleL", out var al))
                H = MathF.Max(0.2f, MathF.Abs(hp.Y - al.Y) / 0.88f);

            // Optional toes
            foreach (string side in new[] { "L", "R" })
            {
                string toeKey = "Toe" + side;
                if (HasToes && !Joints.ContainsKey(toeKey) && Joints.TryGetValue("Ankle" + side, out var ak))
                {
                    Joints[toeKey] = new Vector3(ak.X, ak.Y - 0.035f * H, ak.Z + 0.11f * H);
                }
            }

            if (FingerMode == ReferenceFingerRigMode.ArticulatedFingers)
            {
                EnsureFingerJoints(H);
            }
        }

        public void EnsureFingerJoints(float H, bool forceRegenerate = false)
        {
            int phalanges = Math.Clamp(PhalanxCount, 2, 3);
            foreach (string side in new[] { "L", "R" })
            {
                if (!Joints.TryGetValue("Wrist" + side, out var wrist) ||
                    !Joints.TryGetValue("Elbow" + side, out var elbow))
                    continue;

                Vector3 handFwd = wrist - elbow;
                float armLen = handFwd.Length();
                handFwd = armLen > 1e-4f ? handFwd / armLen : (side == "L" ? Vector3.Right : Vector3.Left);

                // Orthonormal palm frame: forward along hand, up = world Y projected, across = thumb-to-pinky
                Vector3 upRef = MathF.Abs(handFwd.Y) < 0.9f ? Vector3.Up : Vector3.Forward;
                Vector3 palmRight = handFwd.Cross(upRef);
                if (palmRight.LengthSquared() < 1e-6f) palmRight = Vector3.Forward;
                else palmRight = palmRight.Normalized();
                // Ensure palmAcross points from pinky toward thumb (+Z front of hand in T/A-pose)
                if (palmRight.Z < 0f) palmRight = -palmRight;
                Vector3 palmUp = palmRight.Cross(handFwd).Normalized();

                float handLen = MathF.Max(0.06f * H, armLen * 0.42f);
                float palmWidth = handLen * 0.48f;

                // Offsets for thumb, index, middle, ring, pinky: (fwdFrac, acrossFrac, upFrac, lengthFrac)
                (string name, float fwd, float across, float up, float len)[] layout =
                {
                    ("thumb",  0.22f,  0.52f, -0.10f, 0.42f),
                    ("index",  0.52f,  0.34f,  0.02f, 0.50f),
                    ("middle", 0.54f,  0.10f,  0.03f, 0.56f),
                    ("ring",   0.52f, -0.14f,  0.02f, 0.50f),
                    ("pinky",  0.48f, -0.36f,  0.00f, 0.40f)
                };

                foreach (var (fName, fwd, across, up, len) in layout)
                {
                    if (!IsFingerEnabled(fName)) continue;
                    Vector3 fBase = wrist + handFwd * (handLen * fwd) + palmRight * (palmWidth * across) + palmUp * (palmWidth * up);
                    Vector3 fDir = fName == "thumb"
                        ? (handFwd * 0.72f + palmRight * 0.65f - palmUp * 0.20f).Normalized()
                        : handFwd;
                    float totalLen = handLen * len;

                    // Create phalanx joints 0..phalanges (e.g. 0,1,2,3 where 3 is the fingertip)
                    for (int j = 0; j <= phalanges; j++)
                    {
                        string key = $"finger_{fName}_{j}_{side}";
                        if (forceRegenerate || !Joints.ContainsKey(key))
                        {
                            float frac = j / (float)phalanges;
                            Joints[key] = fBase + fDir * (totalLen * frac);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Auto-Rig Pro–style mesh-aware finger snapping: measures the actual hand-tip extremity,
        /// palm width, and per-finger lateral slices from the imported hand mesh vertices distal to
        /// <c>WristL</c>/<c>WristR</c>, projecting every phalanx joint inside the real 3D fingers.
        /// </summary>
        public void SnapFingersToMesh(Node3D scene, IEnumerable<MeshInstance3D> meshes, bool forceRegenerate = true)
        {
            float H = 1.7f;
            if (Joints.TryGetValue("Head", out var hp) && Joints.TryGetValue("AnkleL", out var al))
                H = MathF.Max(0.2f, MathF.Abs(hp.Y - al.Y) / 0.88f);

            // Start with clean mathematical defaults as fallback
            EnsureFingerJoints(H, forceRegenerate: forceRegenerate);
            if (scene == null || meshes == null) return;

            var allPts = new List<Vector3>();
            foreach (var mi in meshes)
            {
                if (mi?.Mesh == null) continue;
                Transform3D m = CustomModelAutoRigger.ComputeTransformRelativeToRoot(mi, scene);
                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                {
                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (arrays == null || arrays.Count == 0) continue;
                    var verts = CustomModelAutoRigger.GetVector3Array(arrays[(int)Mesh.ArrayType.Vertex]);
                    if (verts == null) continue;
                    for (int i = 0; i < verts.Length; i++) allPts.Add(m * verts[i]);
                }
            }
            if (allPts.Count == 0) return;

            int phalanges = Math.Clamp(PhalanxCount, 2, 3);
            foreach (string side in new[] { "L", "R" })
            {
                if (!Joints.TryGetValue("Wrist" + side, out var wrist) ||
                    !Joints.TryGetValue("Elbow" + side, out var elbow))
                    continue;

                Vector3 forearm = wrist - elbow;
                float armLen = forearm.Length();
                if (armLen < 1e-4f) continue;
                Vector3 handFwd = forearm / armLen;

                // Gather vertices in the hand bounding volume distal to the wrist
                float maxHandReach = MathF.Max(0.14f * H, armLen * 0.95f);
                float maxLatRadius = MathF.Max(0.08f * H, armLen * 0.55f);
                var handPts = new List<Vector3>();
                foreach (var p in allPts)
                {
                    Vector3 rel = p - wrist;
                    float f = rel.Dot(handFwd);
                    if (f < -0.01f * H || f > maxHandReach) continue;
                    Vector3 perp = rel - handFwd * f;
                    if (perp.LengthSquared() <= maxLatRadius * maxLatRadius)
                        handPts.Add(p);
                }
                if (handPts.Count < 12) continue;

                // Refine handFwd using the actual distal hand-tip cluster (top 15% farthest along initial handFwd)
                handPts.Sort((a, b) => (a - wrist).Dot(handFwd).CompareTo((b - wrist).Dot(handFwd)));
                int tipStart = Math.Max(0, (int)(handPts.Count * 0.85f));
                Vector3 tipCentroid = Vector3.Zero;
                for (int i = tipStart; i < handPts.Count; i++) tipCentroid += handPts[i];
                tipCentroid /= (handPts.Count - tipStart);
                Vector3 refinedFwd = tipCentroid - wrist;
                float handLen = refinedFwd.Length();
                if (handLen < 0.02f * H) continue;
                handFwd = refinedFwd / handLen;

                // Build palm orthonormal frame aligned to actual hand geometry
                Vector3 upRef = MathF.Abs(handFwd.Y) < 0.9f ? Vector3.Up : Vector3.Forward;
                Vector3 palmAcross = handFwd.Cross(upRef);
                palmAcross = palmAcross.LengthSquared() > 1e-6f ? palmAcross.Normalized() : Vector3.Forward;
                if (palmAcross.Z < 0f) palmAcross = -palmAcross; // +across points toward thumb (+Z front)
                Vector3 palmUp = palmAcross.Cross(handFwd).Normalized();

                // Measure actual lateral (across) span of the palm/fingers band (0.35..0.88 of handLen)
                float minA = float.MaxValue, maxA = float.MinValue;
                float sumUp = 0f; int cntBand = 0;
                foreach (var p in handPts)
                {
                    Vector3 rel = p - wrist;
                    float f = rel.Dot(handFwd) / handLen;
                    if (f < 0.30f || f > 0.92f) continue;
                    float a = rel.Dot(palmAcross);
                    if (a < minA) minA = a;
                    if (a > maxA) maxA = a;
                    sumUp += rel.Dot(palmUp);
                    cntBand++;
                }
                if (cntBand < 6 || maxA - minA < 0.01f * H) continue;
                float palmWidth = maxA - minA;
                float avgUp = sumUp / cntBand;

                // Per-finger normalized lateral band [aMin..aMax] across [minA..maxA] and knuckle start fraction
                (string name, float a0, float a1, float fStart, float fFallbackEnd)[] bands =
                {
                    ("thumb",  0.74f, 1.02f, 0.24f, 0.62f),
                    ("index",  0.54f, 0.78f, 0.48f, 0.96f),
                    ("middle", 0.34f, 0.56f, 0.50f, 1.00f),
                    ("ring",   0.15f, 0.36f, 0.48f, 0.94f),
                    ("pinky", -0.02f, 0.17f, 0.44f, 0.82f)
                };

                foreach (var (fName, a0, a1, fStart, fFallbackEnd) in bands)
                {
                    if (!IsFingerEnabled(fName)) continue;
                    float loA = minA + a0 * palmWidth;
                    float hiA = minA + a1 * palmWidth;
                    float midA = (loA + hiA) * 0.5f;

                    // Collect vertices inside this finger's lateral corridor
                    var slice = new List<(float F, float A, float U, Vector3 P)>();
                    foreach (var p in handPts)
                    {
                        Vector3 rel = p - wrist;
                        float f = rel.Dot(handFwd) / handLen;
                        float a = rel.Dot(palmAcross);
                        if (f >= fStart * 0.80f && a >= loA && a <= hiA)
                            slice.Add((f, a, rel.Dot(palmUp), p));
                    }

                    float fMax = fFallbackEnd;
                    if (slice.Count >= 4)
                    {
                        slice.Sort((x, y) => x.F.CompareTo(y.F));
                        // Use 96th percentile as true fingertip so stray verts don't pull it out
                        fMax = MathF.Max(fStart + 0.16f, slice[(int)((slice.Count - 1) * 0.96f)].F);
                    }

                    for (int j = 0; j <= phalanges; j++)
                    {
                        string key = $"finger_{fName}_{j}_{side}";
                        if (!forceRegenerate && Joints.ContainsKey(key)) continue;

                        float frac = j / (float)phalanges;
                        float targetF = Mathf.Lerp(fStart, fMax, frac);
                        float bandHalf = (fMax - fStart) / (phalanges * 1.4f);

                        Vector3 snapped = Vector3.Zero;
                        int hits = 0;
                        foreach (var item in slice)
                        {
                            if (MathF.Abs(item.F - targetF) <= bandHalf)
                            {
                                snapped += item.P;
                                hits++;
                            }
                        }

                        if (hits >= 2)
                        {
                            Joints[key] = snapped / hits;
                        }
                        else
                        {
                            Joints[key] = wrist + handFwd * (targetF * handLen) + palmAcross * midA + palmUp * avgUp;
                        }
                    }
                }
            }
        }

        public static ReferenceClothChainDef CreateDefaultClothChain(
            string name,
            string parentBoneName,
            Vector3 parentWorldModelSpace,
            Vector3 directionModelSpace,
            int segmentCount,
            float totalLength)
        {
            int segs = Math.Clamp(segmentCount, 2, 6);
            Vector3 dir = directionModelSpace.LengthSquared() > 1e-6f ? directionModelSpace.Normalized() : Vector3.Down;
            float len = MathF.Max(0.12f, totalLength);
            var chain = new ReferenceClothChainDef
            {
                ChainId = $"refcloth_{Guid.NewGuid().ToString("N")[..8]}",
                Name = string.IsNullOrWhiteSpace(name) ? "cloth_chain" : name.Trim(),
                ParentBoneName = string.IsNullOrWhiteSpace(parentBoneName) ? "spine_3" : parentBoneName,
                JointPositions = new List<Vector3>(segs + 1)
            };
            // Offset root slightly along dir from parent so it sits cleanly on the garment seam
            Vector3 rootPos = parentWorldModelSpace + dir * MathF.Min(0.08f, len * 0.15f);
            for (int i = 0; i <= segs; i++)
            {
                chain.JointPositions.Add(rootPos + dir * (len * (i / (float)segs)));
            }
            return chain;
        }

        public ReferenceClothChainDef CreateMirroredClothChain(ReferenceClothChainDef src)
        {
            if (src == null) return null;
            float cx = Joints.TryGetValue("Pelvis", out var pel) ? pel.X : 0f;
            string mirroredName = src.Name;
            if (mirroredName.EndsWith("_L", StringComparison.OrdinalIgnoreCase))
                mirroredName = mirroredName[..^2] + "_R";
            else if (mirroredName.EndsWith("_R", StringComparison.OrdinalIgnoreCase))
                mirroredName = mirroredName[..^2] + "_L";
            else
                mirroredName += "_R";

            string mirroredParent = MirrorJointKey(src.ParentBoneName) ?? src.ParentBoneName;
            var mirrored = new ReferenceClothChainDef
            {
                ChainId = $"refcloth_{Guid.NewGuid().ToString("N")[..8]}",
                Name = mirroredName,
                ParentBoneName = mirroredParent,
                MirrorPairId = src.ChainId,
                JointPositions = src.JointPositions.Select(p => new Vector3(2f * cx - p.X, p.Y, p.Z)).ToList()
            };
            src.MirrorPairId = mirrored.ChainId;
            return mirrored;
        }

        /// <summary>
        /// Resolved bone specification used both for 3D Reference Rig visualization and for
        /// dynamic skeleton construction + weight solving in <see cref="CustomLandmarkWeightSolver"/>.
        /// </summary>
        public sealed class ResolvedBoneSpec
        {
            public int Index;
            public string Name = "";
            public int ParentIndex = -1;
            public ReferenceBoneRole Role = ReferenceBoneRole.Core;
            public Vector3 Origin;
            public Vector3 SegStart;
            public Vector3 SegEnd;
            public float Radius;
            public bool AllowBelow;
            public bool AllowBeyond;
            public string HeadJointKey = "";
            public string TailJointKey = "";
            public string ClothChainId = "";
        }

        /// <summary>
        /// Builds the complete modular bone hierarchy from this <see cref="ReferenceRigDef"/> in
        /// transformed space (pass <see cref="Transform3D.Identity"/> for model-space preview or
        /// <c>options.AlignedTransform</c> for hero-space weight solving).
        /// </summary>
        public List<ResolvedBoneSpec> BuildResolvedBones(Transform3D xf, float H, float footFwdZ, bool swapLeftRight = false)
        {
            Vector3 GetJ(string key, Vector3 fallback)
            {
                string k = key;
                if (swapLeftRight)
                {
                    string mir = MirrorJointKey(key);
                    if (!string.IsNullOrEmpty(mir) && Joints.ContainsKey(mir)) k = mir;
                }
                return Joints.TryGetValue(k, out var v) ? xf * v : fallback;
            }

            var list = new List<ResolvedBoneSpec>();
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            int AddBone(
                string name,
                string parentName,
                ReferenceBoneRole role,
                Vector3 origin,
                Vector3 segStart,
                Vector3 segEnd,
                float radius,
                bool allowBelow = false,
                bool allowBeyond = false,
                string headKey = "",
                string tailKey = "",
                string clothChainId = "")
            {
                int pIdx = !string.IsNullOrEmpty(parentName) && byName.TryGetValue(parentName, out int found) ? found : -1;
                int idx = list.Count;
                list.Add(new ResolvedBoneSpec
                {
                    Index = idx,
                    Name = name,
                    ParentIndex = pIdx,
                    Role = role,
                    Origin = origin,
                    SegStart = segStart,
                    SegEnd = segEnd,
                    Radius = MathF.Max(0.004f, radius),
                    AllowBelow = allowBelow,
                    AllowBeyond = allowBeyond,
                    HeadJointKey = headKey,
                    TailJointKey = tailKey,
                    ClothChainId = clothChainId
                });
                byName[name] = idx;
                return idx;
            }

            Vector3 pelvis = GetJ("Pelvis", Vector3.Zero);
            Vector3 chest = GetJ("Chest", pelvis + Vector3.Up * 0.25f * H);
            Vector3 neck = GetJ("Neck", chest + Vector3.Up * 0.12f * H);
            Vector3 headPin = GetJ("Head", neck + Vector3.Up * 0.12f * H);
            Vector3 hipL = GetJ("HipL", pelvis + new Vector3(0.08f * H, -0.04f * H, 0f));
            Vector3 hipR = GetJ("HipR", pelvis + new Vector3(-0.08f * H, -0.04f * H, 0f));
            Vector3 midHip = hipL.Lerp(hipR, 0.5f);

            // 1. Pelvis + Modular Spine (1..4 bones)
            string[] spineNames = GetDeadlockSpineBoneNames(SpineCount);
            Vector3 firstSpineOrigin = spineNames.Length == 1
                ? pelvis.Lerp(chest, 0.5f)
                : pelvis.Lerp(chest, 1f / (spineNames.Length + 0.2f));
            AddBone("pelvis", null, ReferenceBoneRole.Core, pelvis, midHip - Vector3.Up * 0.03f * H, firstSpineOrigin, 0.11f * H, allowBelow: true, headKey: "Pelvis");

            string prevTorso = "pelvis";
            for (int si = 0; si < spineNames.Length; si++)
            {
                float f0 = (si + 1f) / (spineNames.Length + 1f);
                float f1 = (si + 2f) / (spineNames.Length + 1f);
                Vector3 spOrig = si == spineNames.Length - 1
                    ? (spineNames.Length >= 3 ? chest.Lerp(neck, 0.55f) : chest)
                    : pelvis.Lerp(neck, f0 * 0.85f);
                Vector3 spEnd = si == spineNames.Length - 1 ? neck : pelvis.Lerp(neck, f1 * 0.85f);
                AddBone(spineNames[si], prevTorso, ReferenceBoneRole.Core, spOrig, spOrig, spEnd, 0.105f * H,
                    headKey: si == spineNames.Length - 1 ? "Chest" : "");
                prevTorso = spineNames[si];
            }
            string upperTorso = prevTorso;

            // 2. Optional Neck + Head
            string headParent = upperTorso;
            Vector3 headOrig = HasNeck ? neck.Lerp(headPin, 0.45f) : neck;
            if (HasNeck)
            {
                AddBone("neck_0", upperTorso, ReferenceBoneRole.Core, neck, neck, headOrig, 0.04f * H, headKey: "Neck");
                headParent = "neck_0";
            }
            AddBone("head", headParent, ReferenceBoneRole.Core, headOrig, headOrig, headPin + (headPin - neck), 0.07f * H, allowBeyond: true, headKey: "Head");

            // 3. Arms, Hands, Optional Clavicles, Optional Articulated Fingers & Legs/Toes
            int phalanges = Math.Clamp(PhalanxCount, 2, 3);
            foreach (bool left in new[] { true, false })
            {
                string s = left ? "L" : "R";
                Vector3 sh = GetJ("Shoulder" + s, chest + new Vector3((left ? 1 : -1) * 0.15f * H, 0.05f * H, 0f));
                Vector3 el = GetJ("Elbow" + s, sh + new Vector3((left ? 1 : -1) * 0.16f * H, 0f, 0f));
                Vector3 wr = GetJ("Wrist" + s, el + new Vector3((left ? 1 : -1) * 0.15f * H, 0f, 0f));
                Vector3 hip = left ? hipL : hipR;
                Vector3 kn = GetJ("Knee" + s, hip - Vector3.Up * 0.22f * H);
                Vector3 ak = GetJ("Ankle" + s, kn - Vector3.Up * 0.22f * H);

                string armParent = upperTorso;
                if (HasClavicles)
                {
                    Vector3 clavOrig = neck + (sh - neck) * 0.22f;
                    AddBone("clavicle_" + s, upperTorso, ReferenceBoneRole.Core, clavOrig, clavOrig, sh, 0.035f * H);
                    armParent = "clavicle_" + s;
                }
                AddBone("arm_upper_" + s, armParent, ReferenceBoneRole.Core, sh, sh, el, 0.035f * H, headKey: "Shoulder" + s, tailKey: "Elbow" + s);
                AddBone("arm_lower_" + s, "arm_upper_" + s, ReferenceBoneRole.Core, el, el, wr, 0.030f * H, headKey: "Elbow" + s, tailKey: "Wrist" + s);

                Vector3 handDir = (wr - el).LengthSquared() > 1e-6f ? (wr - el).Normalized() : -Vector3.Up;
                bool hasFingers = FingerMode == ReferenceFingerRigMode.ArticulatedFingers;
                Vector3 handEnd = wr + handDir * (hasFingers ? 0.055f * H : 0.11f * H);
                AddBone("hand_" + s, "arm_lower_" + s, ReferenceBoneRole.Core, wr, wr, handEnd, hasFingers ? 0.032f * H : 0.042f * H,
                    allowBelow: true, allowBeyond: !hasFingers, headKey: "Wrist" + s);

                // Individual Articulated Fingers (matching DeadlockRigs finger_{name}_{0..2}_{L|R})
                if (hasFingers)
                {
                    foreach (string fName in FingerNames)
                    {
                        if (!IsFingerEnabled(fName)) continue;
                        string prevF = "hand_" + s;
                        for (int j = 0; j < phalanges; j++)
                        {
                            string hk = $"finger_{fName}_{j}_{s}";
                            string tk = $"finger_{fName}_{j + 1}_{s}";
                            if (!Joints.ContainsKey(swapLeftRight ? (MirrorJointKey(hk) ?? hk) : hk) ||
                                !Joints.ContainsKey(swapLeftRight ? (MirrorJointKey(tk) ?? tk) : tk))
                                break;
                            Vector3 jHead = GetJ(hk, handEnd);
                            Vector3 jTail = GetJ(tk, jHead + handDir * 0.02f * H);
                            float fRad = MathF.Max(0.005f, (fName == "thumb" ? 0.011f : 0.0085f) * H);
                            string bName = $"finger_{fName}_{j}_{s}";
                            AddBone(bName, prevF, ReferenceBoneRole.Finger, jHead, jHead, jTail, fRad,
                                allowBelow: j == 0, allowBeyond: j == phalanges - 1, headKey: hk, tailKey: tk);
                            prevF = bName;
                        }
                    }
                }

                // Legs & optional Toes
                AddBone("leg_upper_" + s, "pelvis", ReferenceBoneRole.Core, hip, hip, kn, 0.055f * H, headKey: "Hip" + s, tailKey: "Knee" + s);
                AddBone("leg_lower_" + s, "leg_upper_" + s, ReferenceBoneRole.Core, kn, kn, ak, 0.040f * H, headKey: "Knee" + s, tailKey: "Ankle" + s);
                Vector3 footEnd = HasToes
                    ? GetJ("Toe" + s, new Vector3(ak.X, ak.Y - 0.035f * H, ak.Z + footFwdZ * 0.10f * H))
                    : new Vector3(ak.X, ak.Y - 0.04f * H, ak.Z + footFwdZ * 0.145f * H);
                AddBone("ankle_" + s, "leg_lower_" + s, ReferenceBoneRole.Core, ak, ak, footEnd, 0.055f * H,
                    allowBelow: true, allowBeyond: !HasToes, headKey: "Ankle" + s, tailKey: HasToes ? "Toe" + s : "");

                if (HasToes)
                {
                    Vector3 toeFwd = (footEnd - ak);
                    toeFwd.Y = 0f;
                    toeFwd = toeFwd.LengthSquared() > 1e-6f ? toeFwd.Normalized() : new Vector3(0f, 0f, footFwdZ);
                    AddBone("toe_" + s, "ankle_" + s, ReferenceBoneRole.Core, footEnd, footEnd, footEnd + toeFwd * 0.055f * H, 0.035f * H,
                        allowBelow: true, allowBeyond: true, headKey: "Toe" + s);
                }
            }

            // 4. Authored Cloth Bone Chains
            if (ClothChains != null)
            {
                foreach (var cc in ClothChains)
                {
                    if (cc == null || cc.JointPositions == null || cc.JointPositions.Count < 2) continue;
                    string parentBone = byName.ContainsKey(cc.ParentBoneName ?? "") ? cc.ParentBoneName : upperTorso;
                    string safeName = string.IsNullOrWhiteSpace(cc.Name) ? "cloth" : cc.Name.Trim().Replace(" ", "_");
                    int segs = cc.JointPositions.Count - 1;
                    string prevCloth = parentBone;
                    for (int k = 0; k < segs; k++)
                    {
                        Vector3 p0 = xf * cc.JointPositions[k];
                        Vector3 p1 = xf * cc.JointPositions[k + 1];
                        string bName = $"{safeName}_{k}";
                        int suffix = 1;
                        while (byName.ContainsKey(bName)) { bName = $"{safeName}_{k}_{suffix++}"; }
                        float segLen = p0.DistanceTo(p1);
                        float cRad = MathF.Max(0.025f * H, segLen * 0.45f);
                        AddBone(bName, prevCloth, ReferenceBoneRole.Cloth, p0, p0, p1, cRad,
                            allowBelow: k == 0, allowBeyond: k == segs - 1,
                            headKey: $"cloth::{cc.ChainId}::{k}",
                            tailKey: $"cloth::{cc.ChainId}::{k + 1}",
                            clothChainId: cc.ChainId);
                        prevCloth = bName;
                    }
                }
            }

            return list;
        }
    }
}

